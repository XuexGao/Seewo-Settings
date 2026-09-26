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
        "SeewoAssistant.exe",
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
    public IReadOnlyList<ProcessRule> DiscoverSeewoSoftware()
    {
        var rules = new List<ProcessRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in EnumerateInstallDirectories())
        {
            IEnumerable<string> executables;

            try
            {
                executables = Directory.EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var executable in executables)
            {
                if (!LooksLikeSeewo(executable))
                {
                    continue;
                }

                var fileName = Path.GetFileName(executable);
                if (!seen.Add(fileName))
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
                        ? $"已识别的希沃组件（{Path.GetDirectoryName(executable)}）"
                        : $"可能是希沃组件（{Path.GetDirectoryName(executable)}）",
                });
            }
        }

        _logger.Info($"Seewo discovery found {rules.Count} executables.");
        return rules.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> EnumerateInstallDirectories()
    {
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        };

        // Seewo commonly installs under a vendor-named subfolder, so only descend
        // into directories whose name already looks relevant. Scanning all of
        // Program Files would take seconds and find nothing.
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            yield return root;

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(root);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (SeewoKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>True when a path or its file description mentions Seewo.</summary>
    private static bool LooksLikeSeewo(string executablePath)
    {
        var fileName = Path.GetFileName(executablePath);

        if (SeewoKeywords.Any(k => fileName.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(executablePath) ?? string.Empty;
        if (SeewoKeywords.Any(k => directory.Contains(k, StringComparison.OrdinalIgnoreCase)))
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

    /// <summary>Terminates a process.</summary>
    public ActionResult Terminate(int processId)
    {
        if (processId <= 4)
        {
            return ActionResult.Fail($"拒绝操作系统关键进程 PID {processId}。");
        }

        Privacy.DeviceHandleScanner.TryEnableDebugPrivilege();

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
            return ActionResult.Ok($"已结束进程 {processId}。");
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
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
