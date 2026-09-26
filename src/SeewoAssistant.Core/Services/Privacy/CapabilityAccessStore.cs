using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SeewoAssistant.Core.Services.Privacy;

/// <summary>
/// Watches the Capability Access Manager consent store, which is the same data the
/// Windows Settings privacy pages display.
/// </summary>
/// <remarks>
/// <para><b>Why this mechanism.</b> Three options were considered:</para>
/// <list type="bullet">
/// <item><description>
/// <b>Global API hooking</b> — rejected. Camera and microphone access does not go
/// through a user-mode API in the consuming process; it is brokered by Media
/// Foundation into the FrameServer service. A hook in the observing process cannot
/// see another process's device access at all.
/// </description></item>
/// <item><description>
/// <b>ETW tracing</b> — rejected as the primary mechanism. The providers that would
/// carry this information are not documented as a stable contract, and the
/// alternatives (kernel file providers) produce a firehose of unrelated events,
/// which conflicts with the "lowest resource usage" requirement.
/// </description></item>
/// <item><description>
/// <b>Consent store monitoring</b> — chosen. It is a documented location,
/// event-driven through <c>RegNotifyChangeKeyValue</c> so it costs nothing while
/// idle, works on Windows 10 2004 and later as well as Windows 11, needs no
/// elevation, and is exactly what the OS itself treats as ground truth.
/// </description></item>
/// </list>
/// <para>
/// The store is a per-user registry tree. Each application gets a subkey whose
/// <c>LastUsedTimeStart</c> and <c>LastUsedTimeStop</c> values mark the most recent
/// usage interval; a stop value of zero means the capability is in use right now.
/// </para>
/// </remarks>
internal static class CapabilityAccessStore
{
    private const string ConsentStoreRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    /// <summary>Capability key names, as used by the OS.</summary>
    internal const string WebcamKey = "webcam";
    internal const string MicrophoneKey = "microphone";

    internal const string NonPackagedSubKey = "NonPackaged";

    internal const string LastUsedTimeStartValue = "LastUsedTimeStart";
    internal const string LastUsedTimeStopValue = "LastUsedTimeStop";

    /// <summary>One application's recorded usage state.</summary>
    internal readonly record struct AppUsage(string ApplicationId, bool IsPackaged, long Start, long Stop)
    {
        /// <summary>A stop time of zero means the capability is currently held.</summary>
        internal bool IsInUse => Start > 0 && Stop == 0;
    }

    internal static string BuildKeyPath(string capability) => $@"{ConsentStoreRoot}\{capability}";

    /// <summary>
    /// Reads every application entry under a capability. Returns an empty list when
    /// the key does not exist, which is the normal case on a machine where the
    /// capability has never been used.
    /// </summary>
    internal static List<AppUsage> Read(string capability)
    {
        var results = new List<AppUsage>();
        var rootPath = BuildKeyPath(capability);

        using var root = Registry.CurrentUser.OpenSubKey(rootPath, writable: false);
        if (root is null)
        {
            return results;
        }

        // Packaged applications live directly under the capability key.
        ReadInto(root, isPackaged: true, results);

        // Desktop applications live under a NonPackaged child, where the key name is
        // the executable path with backslashes replaced by '#'. The drive colon is
        // kept, so "C:\Program Files\Zoom\Zoom.exe" becomes
        // "C:#Program Files#Zoom#Zoom.exe".
        using var nonPackaged = root.OpenSubKey(NonPackagedSubKey, writable: false);
        if (nonPackaged is not null)
        {
            ReadInto(nonPackaged, isPackaged: false, results);
        }

        return results;
    }

    private static void ReadInto(RegistryKey parent, bool isPackaged, List<AppUsage> results)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            using var appKey = parent.OpenSubKey(name, writable: false);
            if (appKey is null)
            {
                continue;
            }

            var start = ReadLong(appKey, LastUsedTimeStartValue);
            var stop = ReadLong(appKey, LastUsedTimeStopValue);

            var applicationId = isPackaged ? name : DecodeNonPackagedName(name);
            results.Add(new AppUsage(applicationId, isPackaged, start, stop));
        }
    }

    private static long ReadLong(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value switch
        {
            long l => l,
            int i => i,
            byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes, 0),
            _ => 0L,
        };
    }

    /// <summary>
    /// Reverses the registry key encoding of a desktop application path. The OS
    /// replaces every backslash with '#' and leaves the drive colon intact.
    /// </summary>
    internal static string DecodeNonPackagedName(string keyName)
    {
        if (string.IsNullOrEmpty(keyName))
        {
            return keyName;
        }

        // Guard against a key name that legitimately contains no separator: only
        // decode when the result looks like a rooted path.
        var decoded = keyName.Replace('#', '\\');

        // The drive colon is preserved by the OS, so "C:\\Program Files\\..." is
        // already correct. Some builds encode it as "C:#\\..." which the replace
        // above turns into "C:\\..."; both land here.
        if (decoded.Length >= 2 && decoded[1] == ':' && decoded.Length > 2 && decoded[2] != '\\')
        {
            decoded = decoded.Insert(2, "\\");
        }

        return decoded;
    }
}

/// <summary>
/// Blocking registry change notification, wrapped so it can run on a dedicated
/// background thread. Uses <c>RegNotifyChangeKeyValue</c> rather than polling so the
/// monitor consumes no CPU while nothing is happening.
/// </summary>
internal sealed class RegistryChangeWatcher : IDisposable
{
    private const int KEY_NOTIFY = 0x0010;
    private const int KEY_READ = 0x20019;

    private const uint REG_NOTIFY_CHANGE_NAME = 0x00000001;
    private const uint REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;
    private const uint REG_NOTIFY_CHANGE_SECURITY = 0x00000008;
    private const uint INFINITE = 0xFFFFFFFF;

    private readonly string _subKey;
    private readonly bool _watchSubTree;
    private readonly Action _onChanged;

    private nint _key;
    private nint _stopEvent;
    private Thread? _thread;
    private volatile bool _disposed;

    internal RegistryChangeWatcher(string subKey, bool watchSubTree, Action onChanged)
    {
        _subKey = subKey;
        _watchSubTree = watchSubTree;
        _onChanged = onChanged;
    }

    /// <summary>True when the key was opened and change notifications can be armed.</summary>
    internal bool IsArmed { get; private set; }

    internal void Start()
    {
        var result = RegOpenKeyExW(HiveCurrentUser, _subKey, 0, KEY_NOTIFY | KEY_READ, out _key);
        if (result != 0)
        {
            // The consent store only exists once a capability has been used at least
            // once. Report "not armed" rather than throwing; the caller retries.
            IsArmed = false;
            return;
        }

        _stopEvent = CreateEventW(nint.Zero, true, false, null);
        if (_stopEvent == nint.Zero)
        {
            RegCloseKey(_key);
            _key = nint.Zero;
            IsArmed = false;
            return;
        }

        IsArmed = true;
        _thread = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "SeewoAssistant.RegistryWatcher",
        };
        _thread.Start();
    }

    private void WatchLoop()
    {
        var flags = REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_CHANGE_SECURITY;

        while (!_disposed)
        {
            // RegNotifyChangeKeyValue returns as soon as anything under the key
            // changes, or immediately when the stop event is signalled.
            var result = RegNotifyChangeKeyValue(
                _key,
                _watchSubTree,
                flags,
                _stopEvent,
                true);

            if (result != 0)
            {
                // ERROR_KEY_DELETED (1018) and friends: back off briefly and retry so
                // a store that is recreated during a privacy setting change is picked
                // back up instead of silently dropping the monitor.
                if (WaitForSingleObject(_stopEvent, 2000) == 0)
                {
                    break;
                }

                continue;
            }

            if (_disposed)
            {
                break;
            }

            try
            {
                _onChanged();
            }
            catch
            {
                // Never let a consumer exception kill the watch thread.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_stopEvent != nint.Zero)
        {
            SetEvent(_stopEvent);
        }

        // Give the watch thread a moment to observe the stop event and exit.
        _thread?.Join(TimeSpan.FromSeconds(2));

        if (_key != nint.Zero)
        {
            RegCloseKey(_key);
            _key = nint.Zero;
        }

        if (_stopEvent != nint.Zero)
        {
            CloseHandle(_stopEvent);
            _stopEvent = nint.Zero;
        }
    }

    private static readonly nint HiveCurrentUser = unchecked((nint)0x80000001);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(nint hKey, string lpSubKey, int ulOptions, int samDesired, out nint phkResult);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(nint hKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(nint hKey, [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree, uint dwNotifyFilter, nint hEvent, [MarshalAs(UnmanagedType.Bool)] bool fAsynchronous);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateEventW(nint lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(nint hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
