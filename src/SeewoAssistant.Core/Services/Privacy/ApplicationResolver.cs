using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Interop;

namespace SeewoAssistant.Core.Services.Privacy;

/// <summary>
/// Turns an application identifier from the consent store into something a person
/// can read: a display name, an icon source, and a signer.
/// </summary>
public sealed class ApplicationResolver
{
    private readonly IAppLogger _logger;
    private readonly Dictionary<string, string> _packageNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ApplicationResolver(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>File description, product name, or the file name, whichever is most readable.</summary>
    public string ResolveDisplayName(string applicationId, bool isPackaged)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            return "未知程序";
        }

        if (isPackaged)
        {
            return ResolvePackageName(applicationId);
        }

        if (!File.Exists(applicationId))
        {
            return Path.GetFileName(applicationId);
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(applicationId);
            var description = info.FileDescription?.Trim();

            // Some vendors put a bare internal name in FileDescription, which reads
            // worse than the file name. Only prefer it when it looks like a product.
            if (!string.IsNullOrWhiteSpace(description) &&
                !description.Equals(Path.GetFileNameWithoutExtension(applicationId), StringComparison.OrdinalIgnoreCase))
            {
                return $"{description} ({Path.GetFileName(applicationId)})";
            }
        }
        catch (FileNotFoundException)
        {
            // The file vanished between the registry read and now; fall through.
        }
        catch (IOException)
        {
            // Locked or inaccessible; the file name is good enough.
        }

        return Path.GetFileName(applicationId);
    }

    /// <summary>
    /// Maps a package family name such as
    /// <c>Microsoft.SkypeApp_kzf8qxf38zg5c</c> to its display name.
    /// </summary>
    private string ResolvePackageName(string packageFamilyName)
    {
        lock (_gate)
        {
            if (_packageNameCache.TryGetValue(packageFamilyName, out var cached))
            {
                return cached;
            }
        }

        var display = packageFamilyName;

        try
        {
            var packageName = packageFamilyName.Split('_')[0];
            foreach (var package in EnumeratePackages())
            {
                if (package.StartsWith(packageName, StringComparison.OrdinalIgnoreCase))
                {
                    display = package;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"Could not resolve package '{packageFamilyName}': {ex.Message}");
        }

        lock (_gate)
        {
            _packageNameCache[packageFamilyName] = display;
        }

        return display;
    }

    /// <summary>
    /// Enumerates installed package display names via the AppX manifest. Implemented
    /// with PowerShell rather than the WinRT packaging API so the Core assembly does
    /// not need a Windows Runtime projection.
    /// </summary>
    private static IEnumerable<string> EnumeratePackages()
    {
        var result = RunPowerShell(
            "Get-AppxPackage | Select-Object -ExpandProperty Name");

        return result
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string RunPowerShell(string command)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Reads the Authenticode signer subject of an executable, used both for display
    /// and for whitelist rules. Returns null when the file is unsigned or unreadable.
    /// </summary>
    public string? TryGetSigner(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            // CreateFromSignedFile reads the Authenticode signature embedded in the
            // PE, which is what we want. The X509Certificate2(filePath) constructor
            // would instead try to parse the executable as a certificate file.
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            return certificate.Subject;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Resolves which process currently holds a camera or microphone device, by
/// scanning system handles for the device interface symbolic link.
/// </summary>
/// <remarks>
/// <para>
/// The consent store tells us <em>which application</em> is using a capability and
/// <em>when</em>, but not the process ID. When a precise attribution is needed —
/// for example to act on the process, or to show its icon — this scanner closes
/// that gap by finding the process that has an open handle to the camera's device
/// interface.
/// </para>
/// <para>
/// It is only invoked in response to a consent-store transition, never on a timer,
/// so the cost is paid a few times a minute at most. It requires
/// <c>SeDebugPrivilege</c>; when that is unavailable the monitor simply reports the
/// application path without a PID, which is still correct, just less specific.
/// </para>
/// </remarks>
public sealed class DeviceHandleScanner
{
    private readonly IAppLogger _logger;

    public DeviceHandleScanner(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Device interface class GUIDs for cameras and microphones.</summary>
    private static readonly Guid CameraInterfaceClass = new("E5323777-F976-4F5B-9B55-B94699C46E44");
    private static readonly Guid MicrophoneInterfaceClass = new("2E5D6A2B-6C4E-4A3F-9B7A-2F1C6D8E4A90");

    /// <summary>
    /// Finds process IDs with an open handle to any device in the given interface
    /// class. Returns an empty list when the scan is unavailable.
    /// </summary>
    public IReadOnlyList<int> FindProcessesUsing(PrivacyDevice device)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<int>();
        }

        var interfaceClass = device == PrivacyDevice.Camera ? CameraInterfaceClass : MicrophoneInterfaceClass;

        if (!TryEnableDebugPrivilege())
        {
            _logger.Debug("SeDebugPrivilege unavailable; skipping device handle attribution.");
            return Array.Empty<int>();
        }

        try
        {
            return ScanHandles(interfaceClass);
        }
        catch (Exception ex)
        {
            _logger.Debug($"Device handle scan failed: {ex.Message}");
            return Array.Empty<int>();
        }
    }

    private List<int> ScanHandles(Guid interfaceClass)
    {
        var results = new HashSet<int>();

        // Enumerate every device interface instance in the class, then look for
        // handles whose object name contains that instance path.
        foreach (var devicePath in EnumerateDeviceInterfaces(interfaceClass))
        {
            foreach (var pid in FindProcessesWithHandleNamed(devicePath))
            {
                results.Add(pid);
            }
        }

        return results.ToList();
    }

    private static IEnumerable<string> EnumerateDeviceInterfaces(Guid interfaceClass)
    {
        var paths = new List<string>();
        var guid = interfaceClass;
        var set = SetupDiGetClassDevsW(ref guid, null, nint.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

        if (set == new nint(-1))
        {
            return paths;
        }

        try
        {
            var index = 0;
            while (true)
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>(),
                };

                if (!SetupDiEnumDeviceInterfaces(set, nint.Zero, ref guid, index, ref interfaceData))
                {
                    break;
                }

                index++;

                // Two-call pattern: first to learn the required size, then to fill.
                SetupDiGetDeviceInterfaceDetailW(set, ref interfaceData, nint.Zero, 0, out var requiredSize, nint.Zero);
                if (requiredSize <= 0)
                {
                    continue;
                }

                var buffer = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    // cbSize is 8 on 64-bit and 6 on 32-bit because the struct is
                    // followed by a variable-length path.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);

                    if (SetupDiGetDeviceInterfaceDetailW(set, ref interfaceData, buffer, requiredSize, out _, nint.Zero))
                    {
                        var path = Marshal.PtrToStringUni(buffer + 4);
                        if (!string.IsNullOrEmpty(path))
                        {
                            paths.Add(path);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return paths;
    }

    /// <summary>
    /// Scans all handles and keeps the PIDs owning a handle whose name contains the
    /// device path.
    /// </summary>
    private List<int> FindProcessesWithHandleNamed(string devicePath)
    {
        var results = new List<int>();

        // Query the size first; the handle table changes between calls, so allow a
        // couple of retries when it grows.
        var length = 0;
        var buffer = nint.Zero;

        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var status = NtQuerySystemInformation(
                    SystemExtendedHandleInformation, buffer, length, out var returnedLength);

                if (status == STATUS_INFO_LENGTH_MISMATCH || buffer == nint.Zero)
                {
                    if (buffer != nint.Zero)
                    {
                        Marshal.FreeHGlobal(buffer);
                    }

                    // Over-allocate so a growing table does not immediately force
                    // another retry.
                    length = returnedLength > 0 ? returnedLength : 1024 * 1024;
                    length += 256 * 1024;
                    buffer = Marshal.AllocHGlobal(length);
                    continue;
                }

                if (status < 0)
                {
                    break;
                }

                length = returnedLength;
                break;
            }

            if (buffer == nint.Zero)
            {
                return results;
            }

            var handleCount = Marshal.ReadInt32(buffer);
            var entrySize = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
            var entryPtr = buffer + 16; // Skip NumberOfHandles + Reserved

            for (var i = 0; i < handleCount; i++)
            {
                var entry = Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(entryPtr + (i * entrySize));

                if (entry.ObjectTypeIndex == 0)
                {
                    continue;
                }

                // Cheap pre-filter: only duplicate handles that are likely files or
                // device handles. This keeps the scan from touching every handle in
                // the system, which would be slow and could stall other processes.
                if (entry.GrantedAccess == 0)
                {
                    continue;
                }

                var pid = (int)entry.UniqueProcessId;
                if (pid <= 4)
                {
                    continue; // System Idle / System
                }

                if (!TryQueryHandleName(pid, entry.HandleValue, out var name))
                {
                    continue;
                }

                if (name.Contains(devicePath, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(pid);
                    break; // One match per device is enough to attribute the PID.
                }
            }
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return results;
    }

    /// <summary>
    /// Duplicates a handle into this process and asks the kernel for its name.
    /// </summary>
    private bool TryQueryHandleName(int pid, nint handle, out string name)
    {
        name = string.Empty;

        var process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_DUP_HANDLE, false, (uint)pid);
        if (process == nint.Zero)
        {
            return false;
        }

        nint duplicate = nint.Zero;
        try
        {
            if (!DuplicateHandle(process, handle, GetCurrentProcess(), out duplicate, 0, false, DUPLICATE_SAME_ACCESS))
            {
                return false;
            }

            // ObjectNameInformation = 1
            var status = NtQueryObject(duplicate, 1, nint.Zero, 0, out var required);
            if (required <= 0)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal(required);
            try
            {
                status = NtQueryObject(duplicate, 1, buffer, required, out _);
                if (status < 0)
                {
                    return false;
                }

                // UNICODE_STRING: USHORT Length, USHORT MaximumLength, PWSTR Buffer
                var stringLength = (ushort)Marshal.ReadInt16(buffer);
                var stringBuffer = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4);

                if (stringLength == 0 || stringBuffer == nint.Zero)
                {
                    return false;
                }

                name = Marshal.PtrToStringUni(stringBuffer, stringLength / 2) ?? string.Empty;
                return name.Length > 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (duplicate != nint.Zero)
            {
                NativeMethods.CloseHandle(duplicate);
            }

            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>
    /// Enables <c>SeDebugPrivilege</c> on the current token so handles in other
    /// processes can be duplicated. Returns false when the caller is not elevated.
    /// </summary>
    public static bool TryEnableDebugPrivilege()
    {
        if (!NativeMethods.OpenProcessToken(
                GetCurrentProcess(),
                NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY,
                out var token))
        {
            return false;
        }

        try
        {
            if (!NativeMethods.LookupPrivilegeValueW(null, "SeDebugPrivilege", out var luid))
            {
                return false;
            }

            var privileges = new NativeMethods.TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = NativeMethods.SE_PRIVILEGE_ENABLED,
            };

            if (!NativeMethods.AdjustTokenPrivileges(token, false, ref privileges, 0, nint.Zero, nint.Zero))
            {
                return false;
            }

            // AdjustTokenPrivileges reports success even when it enabled nothing,
            // so the last error is the real answer.
            return Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    // ---------------------------------------------------------------- interop

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const int SystemExtendedHandleInformation = 64;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const uint DUPLICATE_SAME_ACCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
    {
        public nint Object;
        public nint UniqueProcessId;
        public nint HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, nint deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(nint deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, nint deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, nint deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, nint systemInformation, int systemInformationLength, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(nint handle, int objectInformationClass, nint objectInformation, int objectInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcessHandle, nint sourceHandle, nint targetProcessHandle, out nint targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);
}
