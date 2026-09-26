using System.Runtime.InteropServices;
using System.Text;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Interop;

namespace SeewoAssistant.Core.Services.CaptureGuard;

/// <summary>Bitness of a process, as far as injection is concerned.</summary>
internal enum ProcessBitness
{
    X86,
    X64,
    Unknown,
}

/// <summary>
/// Injects the capture-guard payload into another process and drives it over the
/// shared request channel.
/// </summary>
/// <remarks>
/// <para><b>Why injection at all.</b> <c>SetWindowDisplayAffinity</c> is validated
/// in the kernel against the calling process, so a window can only be protected by
/// the process that owns it. There is no user-mode alternative.</para>
/// <para><b>Why not shellcode.</b> The reference implementation builds remote
/// shellcode and resolves API addresses by parsing PE export tables by hand at
/// runtime. That is compact but opaque: a crash gives no stack, and an antivirus
/// engine sees exactly the pattern it is trained to block. Injecting a real,
/// inspectable DLL that self-unloads is the same capability with far better
/// debuggability. The unavoidable cost is that antivirus active-defence engines may
/// still flag remote thread creation; that is inherent to the technique and is why
/// this is opt-in and off by default.</para>
/// <para><b>Cross-bitness.</b> A 32-bit target needs a 32-bit payload and a 32-bit
/// <c>LoadLibraryW</c> address. The address of <c>LoadLibraryW</c> inside the target
/// is derived by reading the target's own <c>kernel32.dll</c> from disk and adding
/// the export's RVA to the module base observed in that process, which is the only
/// approach that is correct when the injector and target differ in bitness.</para>
/// </remarks>
internal sealed class PayloadInjector
{
    private readonly IAppLogger _logger;
    private readonly string _payloadDirectory;

    /// <summary>File name of the injected payload, used to locate it in the target.</summary>
    private const string PayloadFileName = "SeewoCaptureGuard.Payload.dll";

    /// <summary>
    /// Processes that must never be injected into. Injecting into the desktop
    /// compositor or the shell can black-screen the session; injecting into a
    /// critical system process bugchecks the machine.
    /// </summary>
    private static readonly HashSet<string> ForbiddenProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "registry",
        "idle",
        "smss.exe",
        "csrss.exe",
        "wininit.exe",
        "winlogon.exe",
        "services.exe",
        "lsass.exe",
        "lsaiso.exe",
        "svchost.exe",
        "dwm.exe",
        "explorer.exe",
        "audiodg.exe",
        "fontdrvhost.exe",
        "sihost.exe",
        "shellexperiencehost.exe",
        "startmenuexperiencehost.exe",
        "searchhost.exe",
        "securityhealthservice.exe",
        "msmpeng.exe",
        "memcompression",
        "wudfhost.exe",
    };

    internal PayloadInjector(string? payloadDirectory = null, IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _payloadDirectory = payloadDirectory ?? AppContext.BaseDirectory;
    }

    /// <summary>Returns true when a process name is on the never-inject list.</summary>
    internal static bool IsForbiddenProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return true;
        }

        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName
            : processName + ".exe";

        return ForbiddenProcesses.Contains(name);
    }

    /// <summary>
    /// Injects the payload into <paramref name="processId"/> and waits until the
    /// payload reports itself ready on the shared channel.
    /// </summary>
    internal ActionResult Inject(int processId, GuardChannel channel)
    {
        if (processId <= 4)
        {
            return ActionResult.Fail($"拒绝注入 PID {processId}：这是系统关键进程。");
        }

        var bitness = GetProcessBitness(processId);
        if (bitness == ProcessBitness.Unknown)
        {
            return ActionResult.Fail($"无法确定进程 {processId} 的位数，已中止注入。");
        }

        // A 64-bit process cannot host a 32-bit DLL, and vice versa.
        var injectorIs64 = Environment.Is64BitProcess;
        if ((bitness == ProcessBitness.X64) != injectorIs64)
        {
            return ActionResult.Fail(
                $"位数不匹配：本程序是 {(injectorIs64 ? "64" : "32")} 位，" +
                $"目标进程是 {(bitness == ProcessBitness.X64 ? "64" : "32")} 位。" +
                "请使用与目标进程位数一致的 SeewoAssistant 版本。");
        }

        var payloadPath = ResolvePayloadPath(bitness);
        if (payloadPath is null)
        {
            return ActionResult.Fail(
                $"未找到 {bitness} 位的注入载荷。请确认发行包中包含 " +
                $"native\\{(bitness == ProcessBitness.X64 ? "x64" : "x86")}\\SeewoCaptureGuard.Payload.dll。");
        }

        if (!GuardInjectorNative.TryEnableDebugPrivilege())
        {
            _logger.Warn("SeDebugPrivilege could not be enabled; injection may fail for processes owned by another user.");
        }

        const uint access =
            NativeMethods.PROCESS_CREATE_THREAD |
            NativeMethods.PROCESS_VM_OPERATION |
            NativeMethods.PROCESS_VM_WRITE |
            NativeMethods.PROCESS_VM_READ |
            NativeMethods.PROCESS_QUERY_INFORMATION;

        var process = NativeMethods.OpenProcess(access, false, (uint)processId);
        if (process == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return ActionResult.Fail(
                error == NativeMethods.ERROR_ACCESS_DENIED
                    ? $"打开进程 {processId} 被拒绝。请以管理员身份运行本程序。"
                    : $"打开进程 {processId} 失败，Win32 错误 {error}。");
        }

        nint remotePath = nint.Zero;
        nint remoteThread = nint.Zero;

        try
        {
            // ---- 1. write the payload path into the target -------------------
            var pathBytes = Encoding.Unicode.GetBytes(payloadPath + "\0");
            remotePath = NativeMethods.VirtualAllocEx(
                process, nint.Zero, (nuint)pathBytes.Length,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);

            if (remotePath == nint.Zero)
            {
                return ActionResult.Fail($"在目标进程中分配内存失败，Win32 错误 {Marshal.GetLastWin32Error()}。");
            }

            if (!NativeMethods.WriteProcessMemory(
                    process, remotePath, pathBytes, (nuint)pathBytes.Length, out var written) ||
                written != (nuint)pathBytes.Length)
            {
                return ActionResult.Fail($"写入目标进程内存失败，Win32 错误 {Marshal.GetLastWin32Error()}。");
            }

            // ---- 2. resolve LoadLibraryW in the target -----------------------
            var loadLibrary = ResolveLoadLibraryAddress(process, bitness);
            if (loadLibrary == nint.Zero)
            {
                return ActionResult.Fail("无法在目标进程中定位 LoadLibraryW 的地址。");
            }

            // ---- 3. LoadLibraryW(payloadPath) --------------------------------
            remoteThread = NativeMethods.CreateRemoteThread(
                process, nint.Zero, 0, loadLibrary, remotePath, 0, nint.Zero);

            if (remoteThread == nint.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                return ActionResult.Fail(
                    error == NativeMethods.ERROR_ACCESS_DENIED
                        ? "创建远程线程被拒绝。这通常意味着被杀软或 EDR 的主动防御拦截了。"
                        : $"创建远程线程失败，Win32 错误 {error}。" +
                          (error == 5 ? " 这通常意味着被杀软主动防御拦截。" : string.Empty));
            }

            NativeMethods.WaitForSingleObject(remoteThread, 15000);
            NativeMethods.GetExitCodeThread(remoteThread, out var loadResult);

            if (loadResult == 0)
            {
                return ActionResult.Fail(
                    "载荷 DLL 加载失败。可能原因：目标进程位数不符、被杀软拦截、" +
                    "或 DLL 依赖缺失（请确认使用静态 CRT 构建的版本）。");
            }

            // The thread exit code is a 32-bit DWORD, so it cannot carry a 64-bit
            // HMODULE. Enumerating the target's modules for the DLL we just loaded is
            // correct for both bitnesses and is what the entry-point lookup needs.
            var moduleHandle = FindRemoteModuleBase(process, PayloadFileName);
            if (moduleHandle == nint.Zero)
            {
                return ActionResult.Fail(
                    "载荷已加载，但无法在目标进程中定位它。请确认目标进程仍然存活。");
            }

            _logger.Info($"Payload loaded into PID {processId} at 0x{moduleHandle:X}.");

            // ---- 4. start the payload entry point ----------------------------
            var entryPoint = GetRemoteProcAddress(process, moduleHandle, bitness, "SeewoCaptureGuardEntry");
            if (entryPoint == nint.Zero)
            {
                return ActionResult.Fail("在目标进程中找不到 SeewoCaptureGuardEntry 导出函数。");
            }

            var entryThread = NativeMethods.CreateRemoteThread(
                process, nint.Zero, 0, entryPoint, nint.Zero, 0, nint.Zero);

            if (entryThread == nint.Zero)
            {
                return ActionResult.Fail($"启动载荷入口线程失败，Win32 错误 {Marshal.GetLastWin32Error()}。");
            }

            NativeMethods.CloseHandle(entryThread);

            // ---- 5. wait for the payload to announce itself -------------------
            // The entry thread returns immediately after starting its loop, so
            // liveness is confirmed through the channel rather than the thread.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (channel.PeekPayloadPid() == (uint)processId)
                {
                    _logger.Info($"Capture guard payload is live in PID {processId}.");
                    return ActionResult.Ok($"已注入进程 {processId}，防截屏载荷已就绪。");
                }

                Thread.Sleep(100);
            }

            return ActionResult.Fail(
                "载荷已加载但没有在 10 秒内响应。它可能被安全软件挂起，或目标进程已退出。");
        }
        finally
        {
            if (remotePath != nint.Zero)
            {
                NativeMethods.VirtualFreeEx(process, remotePath, 0, NativeMethods.MEM_RELEASE);
            }

            if (remoteThread != nint.Zero)
            {
                NativeMethods.CloseHandle(remoteThread);
            }

            NativeMethods.CloseHandle(process);
        }
    }

    private string? ResolvePayloadPath(ProcessBitness bitness)
    {
        var architecture = bitness == ProcessBitness.X64 ? "x64" : "x86";

        // The published layout puts the payload next to the app under native\<arch>,
        // but a developer build may have it in the raw project output. Try the
        // likely locations in order.
        string[] candidates =
        [
            Path.Combine(_payloadDirectory, "native", architecture, "SeewoCaptureGuard.Payload.dll"),
            Path.Combine(_payloadDirectory, architecture, "SeewoCaptureGuard.Payload.dll"),
            Path.Combine(_payloadDirectory, "SeewoCaptureGuard.Payload.dll"),
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the address of <c>LoadLibraryW</c> as it exists inside the target
    /// process.
    /// </summary>
    private nint ResolveLoadLibraryAddress(nint process, ProcessBitness bitness)
    {
        // Same bitness: kernel32 is mapped at the same base in every process, so our
        // own resolved address is valid in the target.
        if ((bitness == ProcessBitness.X64) == Environment.Is64BitProcess)
        {
            var kernel32 = NativeMethods.GetModuleHandleW("kernel32.dll");
            return kernel32 == nint.Zero
                ? nint.Zero
                : NativeMethods.GetProcAddress(kernel32, "LoadLibraryW");
        }

        // Different bitness: read the target's kernel32 base, then add the export RVA
        // from the matching on-disk binary.
        var remoteBase = FindRemoteModuleBase(process, "kernel32.dll");
        if (remoteBase == nint.Zero)
        {
            _logger.Warn("Could not locate kernel32.dll in the target process.");
            return nint.Zero;
        }

        var onDiskPath = bitness == ProcessBitness.X86
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "kernel32.dll")
            : Path.Combine(Environment.SystemDirectory, "kernel32.dll");

        var rva = PeExportReader.GetExportRva(onDiskPath, "LoadLibraryW");
        if (rva == 0)
        {
            _logger.Warn($"Could not read the LoadLibraryW export RVA from {onDiskPath}.");
            return nint.Zero;
        }

        return remoteBase + (nint)rva;
    }

    /// <summary>Finds the base address of a module inside another process.</summary>
    private nint FindRemoteModuleBase(nint process, string moduleName)
    {
        var needed = 0u;
        var buffer = new nint[1024];

        if (!NativeMethods.EnumProcessModulesEx(
                process, buffer, (uint)(buffer.Length * IntPtr.Size), out needed, NativeMethods.LIST_MODULES_ALL))
        {
            return nint.Zero;
        }

        var count = Math.Min(buffer.Length, (int)(needed / IntPtr.Size));

        for (var i = 0; i < count; i++)
        {
            var name = new StringBuilder(260);
            if (NativeMethods.GetModuleBaseNameW(process, buffer[i], name, (uint)name.Capacity) == 0)
            {
                continue;
            }

            if (name.ToString().Equals(moduleName, StringComparison.OrdinalIgnoreCase))
            {
                return buffer[i];
            }
        }

        return nint.Zero;
    }

    /// <summary>
    /// Resolves an export's address inside a remote module by reading the PE headers
    /// from the target process.
    /// </summary>
    private nint GetRemoteProcAddress(nint process, nint moduleBase, ProcessBitness bitness, string exportName)
    {
        // Read the DOS header to find the PE header offset.
        var headerBuffer = new byte[4096];
        if (!NativeMethods.ReadProcessMemory(process, moduleBase, headerBuffer, (nuint)headerBuffer.Length, out _))
        {
            return nint.Zero;
        }

        if (headerBuffer[0] != 'M' || headerBuffer[1] != 'Z')
        {
            return nint.Zero;
        }

        var peOffset = BitConverter.ToInt32(headerBuffer, 0x3C);
        if (peOffset <= 0 || peOffset + 0x100 > headerBuffer.Length)
        {
            return nint.Zero;
        }

        if (headerBuffer[peOffset] != 'P' || headerBuffer[peOffset + 1] != 'E')
        {
            return nint.Zero;
        }

        // Optional header: data directories start at 0x60 for PE32+ and 0x60 for
        // PE32 as well, but the export directory index 0 lives at +0x70 (PE32+) or
        // +0x78 (PE32) from the optional header start.
        var optionalHeader = peOffset + 24;
        var is64 = headerBuffer[optionalHeader] == 0x0B || headerBuffer[optionalHeader] == 0x02;
        var dataDirectoryOffset = is64 ? optionalHeader + 0x70 : optionalHeader + 0x60;

        if (dataDirectoryOffset + 8 > headerBuffer.Length)
        {
            return nint.Zero;
        }

        var exportRva = BitConverter.ToInt32(headerBuffer, dataDirectoryOffset);
        if (exportRva == 0)
        {
            return nint.Zero;
        }

        // Read the export directory. 40 bytes covers the whole structure.
        var exportDirectory = new byte[40];
        if (!NativeMethods.ReadProcessMemory(
                process, moduleBase + exportRva, exportDirectory, (nuint)exportDirectory.Length, out _))
        {
            return nint.Zero;
        }

        var numberOfNames = BitConverter.ToInt32(exportDirectory, 24);
        var addressOfFunctions = BitConverter.ToInt32(exportDirectory, 28);
        var addressOfNames = BitConverter.ToInt32(exportDirectory, 32);
        var addressOfNameOrdinals = BitConverter.ToInt32(exportDirectory, 36);

        if (numberOfNames <= 0 || numberOfNames > 65536)
        {
            return nint.Zero;
        }

        var namePointers = new byte[numberOfNames * 4];
        if (!NativeMethods.ReadProcessMemory(
                process, moduleBase + addressOfNames, namePointers, (nuint)namePointers.Length, out _))
        {
            return nint.Zero;
        }

        var ordinals = new byte[numberOfNames * 2];
        if (!NativeMethods.ReadProcessMemory(
                process, moduleBase + addressOfNameOrdinals, ordinals, (nuint)ordinals.Length, out _))
        {
            return nint.Zero;
        }

        for (var i = 0; i < numberOfNames; i++)
        {
            var nameRva = BitConverter.ToInt32(namePointers, i * 4);
            var nameBytes = new byte[256];

            if (!NativeMethods.ReadProcessMemory(process, moduleBase + nameRva, nameBytes, (nuint)nameBytes.Length, out var bytesRead))
            {
                continue;
            }

            var length = Array.IndexOf(nameBytes, (byte)0);
            if (length < 0)
            {
                length = Math.Min((int)bytesRead, nameBytes.Length);
            }

            var name = Encoding.ASCII.GetString(nameBytes, 0, length);
            if (!name.Equals(exportName, StringComparison.Ordinal))
            {
                continue;
            }

            var ordinal = BitConverter.ToUInt16(ordinals, i * 2);
            var functionRvaBytes = new byte[4];
            if (!NativeMethods.ReadProcessMemory(
                    process, moduleBase + addressOfFunctions + (ordinal * 4), functionRvaBytes, 4, out _))
            {
                return nint.Zero;
            }

            var functionRva = BitConverter.ToInt32(functionRvaBytes, 0);
            return moduleBase + functionRva;
        }

        return nint.Zero;
    }

    /// <summary>Determines a process's bitness using the documented API-set first.</summary>
    internal static ProcessBitness GetProcessBitness(int processId)
    {
        var process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);

        if (process == nint.Zero)
        {
            return ProcessBitness.Unknown;
        }

        try
        {
            // IsWow64Process2 is Windows 10 1709+. It reports the target's machine
            // type directly, which is unambiguous even for ARM64 hosts.
            if (NativeMethods.IsWow64Process2(process, out var processMachine, out _))
            {
                // IMAGE_FILE_MACHINE_UNKNOWN (0) means the process runs natively.
                if (processMachine == 0)
                {
                    return Environment.Is64BitOperatingSystem ? ProcessBitness.X64 : ProcessBitness.X86;
                }

                // IMAGE_FILE_MACHINE_I386 (0x14C)
                if (processMachine == 0x014C)
                {
                    return ProcessBitness.X86;
                }

                return ProcessBitness.Unknown;
            }

            // Fall back for older builds.
            if (!NativeMethods.IsWow64Process(process, out var wow64))
            {
                return ProcessBitness.Unknown;
            }

            if (wow64)
            {
                return ProcessBitness.X86;
            }

            return Environment.Is64BitOperatingSystem ? ProcessBitness.X64 : ProcessBitness.X86;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}

/// <summary>Privilege helper shared by the injector and the handle scanner.</summary>
internal static class GuardInjectorNative
{
    /// <summary>
    /// Enables <c>SeDebugPrivilege</c> so processes owned by other accounts can be
    /// opened. Returns false when the caller is not elevated.
    /// </summary>
    internal static bool TryEnableDebugPrivilege() =>
        Privacy.DeviceHandleScanner.TryEnableDebugPrivilege();
}
