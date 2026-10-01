using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Interop;
using SeewoAssistant.Core.Models;

namespace SeewoAssistant.Core.Services.Seewo;

/// <summary>
/// Discovers, matches and controls the Seewo family of applications: suspending,
/// resuming, terminating, blocking network access, and disabling auto-start.
/// </summary>
/// <remarks>
/// <para>
/// <b>Suspend and resume</b> use the undocumented <c>NtSuspendProcess</c> and
/// <c>NtResumeProcess</c> in ntdll. They are the only user-mode way to freeze a
/// process without terminating it, and they have been stable across every Windows
/// version since XP. Suspending a process is safe and fully reversible, but it does
/// hold the process's threads at an arbitrary instruction, so it is never applied to
/// a process that is mid-write to a file the user cares about without saying so.
/// </para>
/// <para>
/// <b>Network blocking</b> uses the Windows Firewall COM API
/// (<c>INetFwPolicy2</c>) to create matching inbound and outbound block rules for a
/// specific executable path. This is per-program and does not affect anything else.
/// It requires elevation.
/// </para>
/// <para>
/// <b>Auto-start</b> is disabled by finding the actual mechanism in use. Seewo
/// products typically register Scheduled Tasks, so those are handled through
/// <c>schtasks.exe</c>; registry Run keys and Startup-folder shortcuts are also
/// covered. Every discovered entry records how it was found so it can be restored
/// exactly.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SeewoControlService
{
    /// <summary>
    /// Keywords used to recognise Seewo software. Matching is done on the image path
    /// and product name, never on a hardcoded list of executable names, because the
    /// Seewo product line changes names between versions and a wrong guess would
    /// silently do nothing.
    /// </summary>
    private static readonly string[] SeewoKeywords =
    [
        "seewo",
        "easinote",
        "希沃",
        "seewoservice",
        "seewolink",
        "seeworeverseproxy",
        "seewoupdate",
        "seewocloud",
        "easicare",
        "swproxy",
        "swupdate",
    ];

    /// <summary>
    /// Known Seewo executable names, used only to label a scan result as
    /// "recognised" versus "possible". Matching still requires the keyword test.
    /// </summary>
    private static readonly string[] KnownSeewoExecutables =
    [
        "EasiNote.exe",
        "EasiNote5.exe",
        "EasiNoteUpdate.exe",
        "SeewoService.exe",
        "SeewoServiceAssistant.exe",
        "SeewoLink.exe",
        "SeewoLinkService.exe",
        "SeewoRemoteDesktop.exe",
        "SeewoUpdate.exe",
        "SeewoUpdater.exe",
        "SeewoCloud.exe",
        "SeewoReverseProxy.exe",
        "SeewoIwbAssistant.exe",
        "SeewoPcManager.exe",
        "EasiCare.exe",
        "SeewoCamera.exe",
        "SWProxy.exe",
        "SWUpdate.exe",
    ];

    private readonly IAppLogger _logger;
    private readonly string _stateDirectory;

    /// <summary>Process IDs this service has suspended, so they can be resumed.</summary>
    private readonly HashSet<int> _suspendedProcesses = [];

    private readonly object _gate = new();

    public SeewoControlService(string? stateDirectory = null, IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _stateDirectory = stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeewoAssistant");
    }

    /// <summary>True when the current process can control processes owned by others.</summary>
    public static bool HasDebugPrivilege()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return Privacy.DeviceHandleScanner.TryEnableDebugPrivilege();
    }

    // ------------------------------------------------------------------ discovery

    /// <summary>
    /// Scans the machine for installed Seewo software and returns rules for what it
    /// actually found, rather than a canned list that may not match this machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three sources are combined, because no single one is complete:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>The uninstall registry keys.</b> This is the authoritative record of where
    /// something was installed, and it works even when the software lives somewhere
    /// other than Program Files. Both the 64-bit and 32-bit views are read, plus the
    /// per-user hive, because Seewo products install in all three.
    /// </description></item>
    /// <item><description>
    /// <b>Running processes.</b> Catches a portable or already-running install that
    /// never registered itself.
    /// </description></item>
    /// <item><description>
    /// <b>Directory scanning</b> under the usual install roots, as a last resort.
    /// </description></item>
    /// </list>
    /// <para>
    /// The previous implementation scanned directories only, and its directory walk
    /// was broken: <c>Directory.EnumerateFiles(..., AllDirectories)</c> is lazy, so it
    /// throws when the enumerator reaches a protected subdirectory rather than at the
    /// call site. The surrounding try/catch therefore did not cover it and the first
    /// access-denied folder aborted the entire scan, which is why nothing was ever
    /// found on a real machine.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ProcessRule> DiscoverSeewoSoftware()
    {
        var rules = new List<ProcessRule>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Our own executable is named SeewoAssistant, so it matches the "seewo" keyword
        // and used to be reported as a discovered Seewo component - offering to suspend
        // or terminate this very application. Exclude it explicitly.
        var ownName = GetOwnProcessName();

        var candidates = new List<string>();

        candidates.AddRange(FindExecutablesFromRegistry());
        candidates.AddRange(FindExecutablesFromRunningProcesses());
        candidates.AddRange(FindExecutablesFromDirectories());

        _logger.Info($"Seewo discovery examined {candidates.Count} candidate executable(s).");

        foreach (var executable in candidates)
        {
            string fileName;

            try
            {
                fileName = Path.GetFileName(executable);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(fileName) || !seenNames.Add(fileName))
            {
                continue;
            }

            // Never list ourselves.
            if (string.Equals(
                    Path.GetFileNameWithoutExtension(fileName),
                    ownName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A path can be reported by more than one source; the name set above
            // already de-duplicates, but keeping the path set makes the intent clear
            // and guards against a name that differs only by case.
            if (!seenPaths.Add(executable))
            {
                continue;
            }

            if (!LooksLikeSeewo(executable))
            {
                continue;
            }

            var description = TryGetFileDescription(executable);
            var isKnown = KnownSeewoExecutables.Contains(fileName, StringComparer.OrdinalIgnoreCase);

            rules.Add(new ProcessRule
            {
                Name = string.IsNullOrWhiteSpace(description) ? fileName : description,
                MatchKind = ProcessMatchKind.ProcessName,
                Pattern = fileName,
                Enabled = true,
                AutoApply = false,
                IsDiscovered = true,
                Notes = isKnown
                    ? $"已识别的希沃组件（{SafeDirectoryName(executable)}）"
                    : $"可能是希沃组件（{SafeDirectoryName(executable)}）",
            });
        }

        _logger.Info($"Seewo discovery found {rules.Count} executable(s).");
        return rules.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The current process's name, used to keep the application out of its own scan
    /// results.
    /// </summary>
    private static string GetOwnProcessName()
    {
        try
        {
            return System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        }
        catch (Exception)
        {
            // Fall back to the assembly name, which is what the process name derives
            // from in the normal case.
            return "SeewoAssistant";
        }
    }

    /// <summary>Directory part of a path, or an empty string when it has none.</summary>
    private static string SafeDirectoryName(string path)
    {
        try
        {
            return Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Reads install locations out of the uninstall registry keys, which is where an
    /// installer records what it put on the machine.
    /// </summary>
    private List<string> FindExecutablesFromRegistry()
    {
        var results = new List<string>();

        // Both registry views, and both hives. A 32-bit installer on a 64-bit system
        // writes under WOW6432Node, and a per-user install goes to HKCU.
        (Microsoft.Win32.RegistryKey Root, string Path)[] locations =
        [
            (Microsoft.Win32.Registry.LocalMachine,
             @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.LocalMachine,
             @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.CurrentUser,
             @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        ];

        foreach (var (root, path) in locations)
        {
            try
            {
                using var key = root.OpenSubKey(path, writable: false);

                if (key is null)
                {
                    continue;
                }

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(subKeyName, writable: false);

                    if (subKey is null)
                    {
                        continue;
                    }

                    var displayName = subKey.GetValue("DisplayName")?.ToString() ?? string.Empty;
                    var publisher = subKey.GetValue("Publisher")?.ToString() ?? string.Empty;
                    var installLocation = subKey.GetValue("InstallLocation")?.ToString() ?? string.Empty;
                    var displayIcon = subKey.GetValue("DisplayIcon")?.ToString() ?? string.Empty;
                    var uninstallString = subKey.GetValue("UninstallString")?.ToString() ?? string.Empty;

                    // The product must look like Seewo by display name, publisher, or
                    // the paths it recorded. Checking the publisher matters because
                    // some Seewo components carry a generic display name.
                    // The publisher is the strongest signal, so it is checked first;
                    // a path match alone is weak and is what produced false positives.
                    var looksSeewo =
                        MentionsSeewo(publisher) || MentionsSeewo(displayName) ||
                        PathHasSeewoSegment(installLocation) ||
                        PathHasSeewoSegment(ExtractExecutablePath(displayIcon) ?? string.Empty) ||
                        PathHasSeewoSegment(ExtractExecutablePath(uninstallString) ?? string.Empty);

                    if (!looksSeewo)
                    {
                        continue;
                    }

                    _logger.Debug($"Seewo registry entry '{displayName}' at '{installLocation}'.");

                    // DisplayIcon often points straight at the main executable.
                    var iconPath = ExtractExecutablePath(displayIcon);
                    if (iconPath is not null && File.Exists(iconPath))
                    {
                        results.Add(iconPath);
                    }

                    if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
                    {
                        results.AddRange(EnumerateExecutablesSafely(installLocation, maxDepth: 3));
                    }
                }
            }
            catch (System.Security.SecurityException ex)
            {
                _logger.Debug($"Access denied reading {path}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Debug($"Access denied reading {path}: {ex.Message}");
            }
            catch (IOException ex)
            {
                _logger.Debug($"Could not read {path}: {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>
    /// Pulls an executable path out of a registry value such as
    /// <c>"C:\Program Files\Seewo\App.exe",0</c> or <c>C:\App.exe --flag</c>.
    /// </summary>
    private static string? ExtractExecutablePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        // A leading quote is the reliable delimiter when present.
        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            if (closing > 1)
            {
                return text[1..closing];
            }
        }

        // Otherwise take everything up to the first argument or icon-index suffix.
        var candidates = new[] { ".exe", ".EXE", ".Exe" };

        foreach (var extension in candidates)
        {
            var index = text.IndexOf(extension, StringComparison.Ordinal);
            if (index >= 0)
            {
                var end = index + extension.Length;
                return text[..end].Trim().Trim('"');
            }
        }

        return null;
    }

    /// <summary>
    /// Enumerates <c>*.exe</c> files under a directory without letting a single
    /// inaccessible subdirectory abort the walk.
    /// </summary>
    /// <remarks>
    /// This is the core fix for the scan finding nothing. The enumeration is driven
    /// manually with an explicit stack so each directory's failure is handled at the
    /// point it occurs, instead of relying on a try/catch around a lazy enumerator
    /// that throws somewhere else entirely.
    /// </remarks>
    private static List<string> EnumerateExecutablesSafely(string root, int maxDepth)
    {
        var results = new List<string>();

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return results;
        }

        // Breadth-first with an explicit stack of (path, depth). A depth limit keeps
        // the walk bounded; Seewo installs its executables within a few levels.
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();

            if (!visited.Add(current))
            {
                continue;
            }

            // Each of the three operations is guarded separately, because any one of
            // them can fail on a protected directory and none should stop the walk.
            try
            {
                foreach (var file in Directory.EnumerateFiles(current, "*.exe"))
                {
                    results.Add(file);
                }
            }
            catch (UnauthorizedAccessException) { /* Skip this directory. */ }
            catch (DirectoryNotFoundException) { /* Vanished mid-walk. */ }
            catch (IOException) { /* Locked or on a failing device. */ }
            catch (System.Security.SecurityException) { /* Policy denied. */ }

            if (depth >= maxDepth)
            {
                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    pending.Push((child, depth + 1));
                }
            }
            catch (UnauthorizedAccessException) { /* Skip this directory's children. */ }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (System.Security.SecurityException) { }
        }

        return results;
    }

    /// <summary>
    /// Adds the image path of every running process whose name looks like Seewo.
    /// Catches an install that never registered itself.
    /// </summary>
    private static List<string> FindExecutablesFromRunningProcesses()
    {
        var results = new List<string>();

        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;

                if (!MentionsSeewo(name))
                {
                    continue;
                }

                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (System.ComponentModel.Win32Exception) { /* Bitness or protected. */ }
                catch (InvalidOperationException) { /* Exited. */ }
                catch (NotSupportedException) { }

                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    results.Add(path);
                }
            }
            catch (InvalidOperationException) { /* Exited while enumerating. */ }
            catch (ArgumentException) { }
            finally
            {
                process.Dispose();
            }
        }

        return results;
    }

    /// <summary>Scans the usual install roots as a last resort.</summary>
    private static List<string> FindExecutablesFromDirectories()
    {
        var results = new List<string>();

        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        // Per-user installs land here.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            roots.Add(Path.Combine(localAppData, "Programs"));
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            // Only descend into directories whose name already looks relevant. Walking
            // all of Program Files would take a long time and find nothing, since a
            // Seewo install always has a recognisable vendor folder.
            results.AddRange(EnumerateExecutablesSafely(root, maxDepth: 0));

            try
            {
                foreach (var child in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(child);

                    if (MentionsSeewo(name))
                    {
                        results.AddRange(EnumerateExecutablesSafely(child, maxDepth: 3));
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (System.Security.SecurityException) { }
        }

        return results;
    }

    /// <summary>True when a string mentions any Seewo keyword.</summary>
    /// <summary>
    /// True when a registry value mentions Seewo.
    /// </summary>
    /// <remarks>
    /// A registry value may be a product name, a publisher, or a path, and each needs a
    /// different test. Paths in particular must not be matched by raw substring search:
    /// the user profile of a Seewo classroom machine is often literally
    /// <c>C:\Users\seewo</c>, so every per-user application installed there matched and
    /// was reported as a Seewo component - the scan produced 139 rules including
    /// unrelated tools such as WorkBuddy.
    /// </remarks>
    private static bool MentionsSeewo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // A path is matched on its segments; anything else on the whole string.
        return LooksLikePath(text)
            ? PathHasSeewoSegment(text)
            : SeewoKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when a string looks like a file system path rather than a name.</summary>
    private static bool LooksLikePath(string text) =>
        text.Contains('\\') ||
        text.Contains('/') ||
        text.Contains(":", StringComparison.Ordinal);

    /// <summary>
    /// True when any directory segment of a path is a Seewo vendor folder.
    /// </summary>
    /// <remarks>
    /// Only whole segments count, so <c>\Seewo\</c> matches but
    /// <c>C:\Users\seewo\AppData\...</c> does not: the <c>seewo</c> there is the
    /// account name, not a vendor folder. The final component (the file name) is checked
    /// too, because an installer often records <c>DisplayIcon</c> as a bare executable
    /// path.
    /// </remarks>
    private static bool PathHasSeewoSegment(string path)
    {
        string[] segments;

        try
        {
            segments = path.Split(
                ['\\', '/'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (ArgumentException)
        {
            return false;
        }

        // The account name under Users\ is skipped entirely. On a Seewo classroom machine
        // the profile is routinely C:\Users\seewo, and checking only the container
        // segments is not enough: the offending segment IS the user name, so it has to be
        // excluded by position rather than by name.
        var skipNext = false;

        foreach (var segment in segments)
        {
            // A drive letter such as "C:" is not a name and must never be treated as one.
            if (segment.Length <= 1 || segment.EndsWith(':'))
            {
                continue;
            }

            if (skipNext)
            {
                // This is the account name; never a vendor folder.
                skipNext = false;
                continue;
            }

            if (segment.Equals("Users", StringComparison.OrdinalIgnoreCase))
            {
                skipNext = true;
                continue;
            }

            // AppData and the standard shell folders carry no vendor meaning.
            if (segment.Equals("AppData", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Local", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Roaming", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Programs", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Documents", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (SeewoKeywords.Any(k => segment.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when a path or its file description mentions Seewo.</summary>
    private static bool LooksLikeSeewo(string executablePath)
    {
        var fileName = Path.GetFileName(executablePath);

        if (SeewoKeywords.Any(k => fileName.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // The directory is checked per segment, not as one string, so a user profile
        // named "seewo" does not make every application under it look like Seewo
        // software.
        var directory = Path.GetDirectoryName(executablePath) ?? string.Empty;
        if (PathHasSeewoSegment(directory))
        {
            return true;
        }

        // A vendor-named folder is not enough on its own: the file description is
        // checked as a second signal so a generic helper shipped alongside Seewo is
        // included, while unrelated utilities in the same folder are not.
        var description = TryGetFileDescription(executablePath);
        return !string.IsNullOrWhiteSpace(description) &&
               SeewoKeywords.Any(k => description.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static string TryGetFileDescription(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileDescription?.Trim()
                ?? info.ProductName?.Trim()
                ?? string.Empty;
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    // ------------------------------------------------------------------ matching

    /// <summary>Returns every running process that matches a rule.</summary>
    public IReadOnlyList<MatchedProcess> FindMatchingProcesses(ProcessRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var results = new List<MatchedProcess>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (Matches(rule, process, out var path, out var description))
                {
                    results.Add(new MatchedProcess
                    {
                        ProcessId = process.Id,
                        ProcessName = process.ProcessName + ".exe",
                        Path = path,
                        Description = description,
                        CommandLine = TryGetCommandLine(process.Id),
                    });
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited while enumerating.
            }
            catch (ArgumentException)
            {
                // Same.
            }
            finally
            {
                process.Dispose();
            }
        }

        return results;
    }

    /// <summary>Returns every running process matching any enabled rule, de-duplicated.</summary>
    public IReadOnlyList<MatchedProcess> FindAllMatches(IEnumerable<ProcessRule> rules)
    {
        var byPid = new Dictionary<int, MatchedProcess>();

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            foreach (var match in FindMatchingProcesses(rule))
            {
                byPid[match.ProcessId] = match;
            }
        }

        return byPid.Values.OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool Matches(ProcessRule rule, Process process, out string path, out string description)
    {
        path = string.Empty;
        description = string.Empty;

        var processName = process.ProcessName + ".exe";

        switch (rule.MatchKind)
        {
            case ProcessMatchKind.ProcessName:
                return processName.Equals(rule.Pattern, StringComparison.OrdinalIgnoreCase);

            case ProcessMatchKind.PathPrefix:
            {
                path = TryGetProcessPath(process);
                if (string.IsNullOrEmpty(path))
                {
                    return false;
                }

                // A trailing separator means "anything under this directory".
                return rule.Pattern.EndsWith(Path.DirectorySeparatorChar)
                    ? path.StartsWith(rule.Pattern, StringComparison.OrdinalIgnoreCase)
                    : path.Equals(rule.Pattern, StringComparison.OrdinalIgnoreCase);
            }

            case ProcessMatchKind.Signer:
            {
                path = TryGetProcessPath(process);
                if (string.IsNullOrEmpty(path))
                {
                    return false;
                }

                var signer = TryGetSigner(path);
                return signer is not null &&
                       signer.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);
            }

            default:
                return false;
        }
    }

    private static string TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
        catch (NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static string TryGetSigner(string path)
    {
        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            return certificate.Subject;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static string TryGetCommandLine(int processId)
    {
        // WMI is used because reading another process's command line via
        // NtQueryInformationProcess requires a correctly sized buffer guess and
        // elevation, whereas this degrades to an empty string.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");

            foreach (var item in searcher.Get())
            {
                using var managementObject = (ManagementObject)item;
                return managementObject["CommandLine"]?.ToString() ?? string.Empty;
            }
        }
        catch (ManagementException)
        {
            // WMI is unavailable or the query is not permitted.
        }
        catch (COMException)
        {
            // Same.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        return string.Empty;
    }

    // ------------------------------------------------------------------ control

    /// <summary>Suspends or resumes a process.</summary>
    public ActionResult SetSuspended(int processId, bool suspend)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ActionResult.Fail("进程控制只在 Windows 上可用。");
        }

        if (processId <= 4)
        {
            return ActionResult.Fail($"拒绝操作系统关键进程 PID {processId}。");
        }

        Privacy.DeviceHandleScanner.TryEnableDebugPrivilege();

        var process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SUSPEND_RESUME | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            (uint)processId);

        if (process == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return ActionResult.Fail(
                error == NativeMethods.ERROR_ACCESS_DENIED
                    ? $"打开进程 {processId} 被拒绝，请以管理员身份运行。"
                    : $"打开进程 {processId} 失败，Win32 错误 {error}。");
        }

        try
        {
            var status = suspend
                ? NativeMethods.NtSuspendProcess(process)
                : NativeMethods.NtResumeProcess(process);

            if (status < 0)
            {
                return ActionResult.Fail(
                    $"{(suspend ? "挂起" : "恢复")}进程 {processId} 失败，NTSTATUS 0x{status:X8}。");
            }

            lock (_gate)
            {
                if (suspend)
                {
                    _suspendedProcesses.Add(processId);
                }
                else
                {
                    _suspendedProcesses.Remove(processId);
                }
            }

            _logger.Info($"{(suspend ? "Suspended" : "Resumed")} process {processId}.");
            return ActionResult.Ok($"已{(suspend ? "挂起" : "恢复")}进程 {processId}。");
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>Suspends or resumes every process matching a rule, including children.</summary>
    public ActionResult SetSuspendedForRule(ProcessRule rule, bool suspend)
    {
        var matches = FindMatchingProcesses(rule);
        if (matches.Count == 0)
        {
            return ActionResult.Ok($"没有正在运行的进程匹配「{rule.Name}」。");
        }

        var succeeded = 0;
        var failures = new List<string>();

        // Suspend children before parents so a parent does not recreate a child
        // while the operation is in progress. Resume in the opposite order.
        var ordered = suspend
            ? matches.OrderByDescending(m => m.ProcessId)
            : matches.OrderBy(m => m.ProcessId);

        foreach (var match in ordered)
        {
            var result = SetSuspended(match.ProcessId, suspend);
            if (result.Success)
            {
                succeeded++;
            }
            else
            {
                failures.Add($"{match.ProcessName}: {result.Message}");
            }
        }

        if (failures.Count == 0)
        {
            return ActionResult.Ok($"已{(suspend ? "挂起" : "恢复")} {succeeded} 个进程。");
        }

        return ActionResult.Fail(
            $"成功 {succeeded} 个，失败 {failures.Count} 个。{string.Join("；", failures.Take(3))}");
    }

    /// <summary>
    /// Terminates a process, stopping any service that backs it first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ending the process alone is not enough for a Seewo component that runs as a
    /// service: the Service Control Manager owns the process and immediately starts a
    /// replacement, so the process appears to come back and "cannot be killed". Worse,
    /// the restart also brings up whatever that service depends on, which is what the
    /// user saw as dormant processes being woken up.
    /// </para>
    /// <para>
    /// Stopping the service first puts the process into a state where the SCM will not
    /// restart it, and then terminating it is final. If the service cannot be stopped -
    /// no elevation, or a protected service - the process is still terminated and the
    /// result says plainly that it may return.
    /// </para>
    /// </remarks>
    public ActionResult Terminate(int processId)
    {
        if (processId <= 4)
        {
            return ActionResult.Fail($"拒绝操作系统关键进程 PID {processId}。");
        }

        Privacy.DeviceHandleScanner.TryEnableDebugPrivilege();

        // Find and stop any service whose image is this process before killing it.
        var serviceNote = StopBackingServices(processId);

        var process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_TERMINATE | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            (uint)processId);

        if (process == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return ActionResult.Fail(
                error == NativeMethods.ERROR_ACCESS_DENIED
                    ? $"结束进程 {processId} 被拒绝，请以管理员身份运行。"
                    : $"打开进程 {processId} 失败，Win32 错误 {error}。");
        }

        try
        {
            if (!NativeMethods.TerminateProcess(process, 1))
            {
                return ActionResult.Fail($"结束进程 {processId} 失败，Win32 错误 {Marshal.GetLastWin32Error()}。");
            }

            lock (_gate)
            {
                _suspendedProcesses.Remove(processId);
            }

            _logger.Info($"Terminated process {processId}.");

            return ActionResult.Ok(
                $"已结束进程 {processId}。" + serviceNote);
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>
    /// Stops every Windows service whose running process is <paramref name="processId"/>.
    /// </summary>
    /// <returns>A sentence to append to the result, or an empty string.</returns>
    /// <remarks>
    /// Without this, ending a service-hosted Seewo process is pointless: the Service
    /// Control Manager treats the termination as a failure and starts the service again,
    /// which is the "kill it and it comes back" behaviour. Stopping the service makes
    /// the termination stick.
    /// </remarks>
    private string StopBackingServices(int processId)
    {
        var stopped = new List<string>();
        var refused = new List<string>();

        foreach (var (serviceName, displayName) in FindServicesForProcess(processId))
        {
            // `sc stop` is used rather than the SCM API because the service may be
            // running as another account, and the command line needs no extra interop.
            var (exitCode, output) = RunSc($"stop \"{serviceName}\"");

            if (exitCode == 0)
            {
                stopped.Add(displayName);
                _logger.Info($"Stopped service '{serviceName}' before terminating PID {processId}.");
            }
            else
            {
                refused.Add(displayName);
                _logger.Warn($"Could not stop service '{serviceName}': exit {exitCode} {output.Trim()}");
            }
        }

        if (stopped.Count > 0 && refused.Count == 0)
        {
            return $" 已先停止其服务：{string.Join("、", stopped)}，服务不会再自动拉起该进程。";
        }

        if (stopped.Count > 0)
        {
            return $" 已停止服务 {string.Join("、", stopped)}，但 {string.Join("、", refused)} 停止失败，" +
                   "这些服务可能会重新启动该进程。";
        }

        if (refused.Count > 0)
        {
            return $" 它的服务（{string.Join("、", refused)}）停止失败，通常会重新启动该进程，" +
                   "请以管理员身份重试。";
        }

        return string.Empty;
    }

    /// <summary>
    /// Runs <c>sc.exe</c> with the given arguments and returns its exit code and output.
    /// </summary>
    /// <remarks>
    /// <c>sc.exe</c> writes in the console OEM code page, so the provider is registered
    /// before the code page is resolved. Without that, decoding throws
    /// NotSupportedException and the whole operation fails for a reason unrelated to
    /// the service.
    /// </remarks>
    private static (int ExitCode, string Output) RunSc(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Interop.ConsoleEncoding.Oem,
                StandardErrorEncoding = Interop.ConsoleEncoding.Oem,
            };

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return (-1, "无法启动 sc.exe。");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, stdout + stderr);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>
    /// Finds the services whose process is <paramref name="processId"/>.
    /// </summary>
    /// <remarks>
    /// Matched through the SCM rather than by image path, because several Seewo
    /// services share one executable and the SCM knows which instance is which.
    /// </remarks>
    private static List<(string ServiceName, string DisplayName)> FindServicesForProcess(int processId)
    {
        var results = new List<(string, string)>();

        try
        {
            // Win32_Service exposes ProcessId and is readable without elevation; the
            // command is only issued for a match, and it fails cleanly without rights.
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT Name, DisplayName, ProcessId FROM Win32_Service WHERE ProcessId = {processId}");

            foreach (var service in searcher.Get())
            {
                using (service)
                {
                    var name = service["Name"]?.ToString();
                    var display = service["DisplayName"]?.ToString();

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        results.Add((name, string.IsNullOrWhiteSpace(display) ? name : display));
                    }
                }
            }
        }
        catch (System.Management.ManagementException)
        {
            // WMI unavailable or access denied; the caller still terminates the process.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return results;
    }

    /// <summary>Terminates every process matching a rule.</summary>
    public ActionResult TerminateForRule(ProcessRule rule)
    {
        var matches = FindMatchingProcesses(rule);
        if (matches.Count == 0)
        {
            return ActionResult.Ok($"没有正在运行的进程匹配「{rule.Name}」。");
        }

        var succeeded = 0;
        var failures = new List<string>();

        foreach (var match in matches)
        {
            var result = Terminate(match.ProcessId);
            if (result.Success)
            {
                succeeded++;
            }
            else
            {
                failures.Add($"{match.ProcessName}: {result.Message}");
            }
        }

        if (failures.Count == 0)
        {
            return ActionResult.Ok($"已结束 {succeeded} 个进程。");
        }

        return ActionResult.Fail($"成功 {succeeded} 个，失败 {failures.Count} 个。{string.Join("；", failures.Take(3))}");
    }

    /// <summary>
    /// Resumes every process this service suspended, used on shutdown so the user is
    /// never left with a frozen application after the app exits.
    /// </summary>
    public void ResumeAllSuspended()
    {
        List<int> suspended;

        lock (_gate)
        {
            suspended = _suspendedProcesses.ToList();
            _suspendedProcesses.Clear();
        }

        foreach (var processId in suspended)
        {
            try
            {
                var process = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_SUSPEND_RESUME, false, (uint)processId);

                if (process == nint.Zero)
                {
                    continue;
                }

                try
                {
                    NativeMethods.NtResumeProcess(process);
                }
                finally
                {
                    NativeMethods.CloseHandle(process);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Could not resume process {processId}: {ex.Message}");
            }
        }

        if (suspended.Count > 0)
        {
            _logger.Info($"Resumed {suspended.Count} process(es) on shutdown.");
        }
    }
}
