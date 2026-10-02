using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using SeewoAssistant.Core.Abstractions;

namespace SeewoAssistant.Core.Services;

/// <summary>Which native script action to run elevated.</summary>
public enum NativeScriptAction
{
    /// <summary>Register the native components for this machine and record the install path.</summary>
    Install,

    /// <summary>Unregister the components, the install record and this app's firewall rules.</summary>
    Uninstall,

    /// <summary>Remove stale registrations whatever they point at, plus the record and firewall rules.</summary>
    Cleanup,
}

/// <summary>Where a shortcut can be created.</summary>
public enum ShortcutLocation
{
    Desktop,
    StartMenu,
}

/// <summary>How one native COM component is currently registered.</summary>
public enum NativeRegistrationStatus
{
    /// <summary>No <c>InProcServer32</c> key under this CLSID.</summary>
    NotRegistered,

    /// <summary>Registered, and the DLL it names is inside this application's folder.</summary>
    RegisteredToCurrentFolder,

    /// <summary>Registered, but the folder it names is not this one.</summary>
    RegisteredElsewhere,

    /// <summary>Registered, but the DLL it names no longer exists.</summary>
    FileMissing,
}

/// <summary>The registration state of one native COM component.</summary>
public sealed record NativeComponentState(
    string DisplayName,
    string Clsid,
    NativeRegistrationStatus Status,
    string? RegisteredPath)
{
    /// <summary>True when any registration exists, wherever it points.</summary>
    public bool IsRegistered => Status != NativeRegistrationStatus.NotRegistered;

    /// <summary>True when the DLL the registration names is still on disk.</summary>
    public bool FileExists => Status is NativeRegistrationStatus.RegisteredToCurrentFolder
        or NativeRegistrationStatus.RegisteredElsewhere;

    /// <summary>True when the registration points inside this copy's folder.</summary>
    public bool IsInCurrentFolder => Status == NativeRegistrationStatus.RegisteredToCurrentFolder;

    /// <summary>
    /// True when the registration cannot work for this copy: it points at a different
    /// folder, or the file it names is already gone. Both are leftovers of another copy
    /// and both are fixed by re-registering to the current folder.
    /// </summary>
    public bool IsOrphaned =>
        Status is NativeRegistrationStatus.RegisteredElsewhere or NativeRegistrationStatus.FileMissing;

    /// <summary>One line for the UI, in the wording the user needs to act on.</summary>
    public string Describe() => Status switch
    {
        NativeRegistrationStatus.RegisteredToCurrentFolder =>
            $"{DisplayName}：已注册到当前目录（{RegisteredPath}）",
        NativeRegistrationStatus.RegisteredElsewhere =>
            $"{DisplayName}：指向其他目录（{RegisteredPath}）",
        NativeRegistrationStatus.FileMissing =>
            $"{DisplayName}：已注册，但文件已不存在（{RegisteredPath}）",
        _ => $"{DisplayName}：未注册",
    };
}

/// <summary>Everything the integration card needs, read without elevation.</summary>
public sealed record SystemIntegrationState(
    IReadOnlyList<NativeComponentState> Components,
    string? RecordedInstallPath,
    bool RecordedPathDiffers,
    bool IsElevated,
    bool DesktopShortcutExists,
    bool StartMenuShortcutExists,
    bool IsListedInAppsAndFeatures,
    string? AppsAndFeaturesInstallLocation)
{
    /// <summary>True when at least one component is registered somewhere it should not be.</summary>
    public bool HasOrphanedRegistration => Components.Any(c => c.IsOrphaned);

    /// <summary>True when at least one component is not registered to this folder.</summary>
    public bool NeedsRegistration =>
        Components.Any(c => c.Status != NativeRegistrationStatus.RegisteredToCurrentFolder);
}

/// <summary>The outcome of a script action that needed administrator rights.</summary>
public sealed record ElevatedScriptResult(bool Success, bool UserCancelled, int ExitCode, string Message)
{
    /// <summary><c>ERROR_CANCELLED</c>: what <c>Process.Start</c> reports when UAC is dismissed.</summary>
    public const int UserCancelledErrorCode = 1223;

    /// <summary>
    /// Declining the UAC prompt is a decision, not a malfunction, so it gets its own
    /// result instead of an error the user would try to troubleshoot.
    /// </summary>
    public static ElevatedScriptResult Cancelled() =>
        new(false, true, UserCancelledErrorCode, "用户取消了提权，操作没有执行。");
}

/// <summary>
/// Manages the pieces of this program that live outside its own folder: the machine-wide
/// COM registration of the native camera components, the per-user shortcuts, and the
/// per-user「应用和功能」entry.
/// </summary>
/// <remarks>
/// <para>
/// The native components register into <c>HKLM</c>, which needs administrator rights, and
/// the CLSID is machine-wide: a second copy - or a copy unzipped somewhere else - silently
/// takes the registration over from the first one. Nothing in the OS objects, so this
/// service both reports where the registration actually points and offers to point it back
/// at the current folder. The earlier design had no such view at all, and the symptom was
/// simply "the camera does not work".
/// </para>
/// <para>
/// The elevated work is delegated to <c>scripts\Install-Native.ps1</c>, which is the only
/// place that knows how to write the machine-wide keys, and it is launched through
/// <c>Verb = "runas"</c>. That call blocks until the user answers the UAC prompt, so it
/// runs on a thread-pool thread: doing it on the UI thread froze the window for the whole
/// round trip.
/// </para>
/// <para>
/// Everything that can be per-user is per-user. The「应用和功能」entry goes under
/// <c>HKCU</c> and the shortcuts into the user's own folders, because writing those to
/// <c>HKLM</c> would force an administrator prompt just to switch a label on.
/// </para>
/// <para>
/// The shortcuts use the late-bound <c>WScript.Shell</c> COM object rather than a shell
/// API P/Invoke: it is the documented way to write a <c>.lnk</c>, it needs no additional
/// package, and a failure to activate it is reported as a result instead of thrown.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SystemIntegrationService
{
    /// <summary>The Media Foundation media source CLSID, matching <c>native/SeewoVirtualCamera</c>.</summary>
    public const string MediaSourceClsid = "{A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60}";

    /// <summary>The DirectShow filter CLSID, matching <c>native/SeewoVirtualCamera.DShow</c>.</summary>
    public const string DirectShowFilterClsid = "{B8F5C3D2-6E4A-4B99-8C7F-2D3E4F5A6B71}";

    /// <summary>Where the install script records the folder it registered.</summary>
    public const string InstallRecordKeyPath = @"SOFTWARE\SeewoAssistant";

    /// <summary>Value name of the recorded install path.</summary>
    public const string InstallRecordValueName = "InstallPath";

    /// <summary>The per-user「应用和功能」entry for the portable copy.</summary>
    public const string AppsAndFeaturesKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SeewoAssistant";

    /// <summary>Name shown in「应用和功能」and on the shortcuts.</summary>
    public const string DisplayName = "希沃助手";

    /// <summary>
    /// Publisher shown in「应用和功能」. Matches <c>installer/SeewoAssistant.iss</c> and
    /// <c>scripts\Uninstall-Portable.ps1</c>, so all three describe the same product.
    /// </summary>
    public const string Publisher = "XuexGao";

    /// <summary>File name of the shortcut this app creates in both locations.</summary>
    public const string ShortcutFileName = "希沃助手.lnk";

    private const string NativeScriptRelativePath = @"scripts\Install-Native.ps1";
    private const string PortableUninstallScriptRelativePath = @"scripts\Uninstall-Portable.ps1";

    /// <summary>Labels for the two components in the status the UI shows.</summary>
    private const string MediaSourceDisplayName = "Media Foundation 媒体源";
    private const string DirectShowDisplayName = "DirectShow 源滤镜";

    /// <summary>
    /// Both registry views are consulted: the script may have run as 64-bit or 32-bit
    /// PowerShell, and a value written through the other view is invisible from this
    /// process's own view.
    /// </summary>
    private static readonly RegistryView[] Views = [RegistryView.Registry64, RegistryView.Registry32];

    private readonly IAppLogger _logger;

    public SystemIntegrationService(string? appDirectory = null, IAppLogger? logger = null)
    {
        // The published app is a single file in the release root, so the base directory is
        // that root and scripts\ sits directly under it. This is the same assumption
        // VirtualCameraService makes for the native tools; it is not re-derived here.
        AppDirectory = Path.TrimEndingDirectorySeparator(
            string.IsNullOrWhiteSpace(appDirectory) ? AppContext.BaseDirectory : appDirectory);

        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The release root this copy runs from.</summary>
    public string AppDirectory { get; }

    /// <summary>Path of the script that owns the machine-wide changes.</summary>
    public string NativeScriptPath => Path.Combine(AppDirectory, NativeScriptRelativePath);

    // ------------------------------------------------------------------ state

    /// <summary>
    /// Reads the current integration state. Read-only and needs no elevation, so it is
    /// safe to call while filling in the UI.
    /// </summary>
    public SystemIntegrationState GetState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SystemIntegrationState(
                [
                    new NativeComponentState(MediaSourceDisplayName, MediaSourceClsid, NativeRegistrationStatus.NotRegistered, null),
                    new NativeComponentState(DirectShowDisplayName, DirectShowFilterClsid, NativeRegistrationStatus.NotRegistered, null),
                ],
                null,
                false,
                false,
                false,
                false,
                false,
                null);
        }

        var components = new[]
        {
            ReadComponent(MediaSourceDisplayName, MediaSourceClsid),
            ReadComponent(DirectShowDisplayName, DirectShowFilterClsid),
        };

        var recordedPath = ReadRecordedInstallPath();
        var (listed, listedLocation) = ReadAppsAndFeaturesEntry();
        var desktopPath = GetShortcutPath(ShortcutLocation.Desktop);

        return new SystemIntegrationState(
            components,
            recordedPath,
            !string.IsNullOrWhiteSpace(recordedPath) && !IsInAppDirectory(recordedPath),
            IsCurrentProcessElevated(),
            desktopPath is not null && File.Exists(desktopPath),
            GetShortcutPath(ShortcutLocation.StartMenu) is { } startMenu && File.Exists(startMenu),
            listed,
            listedLocation);
    }

    /// <summary>
    /// True when this process already has administrator rights. Used to decide whether a
    /// registration can be attempted in-process or has to go through the elevated script.
    /// </summary>
    public static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            // An unreadable token is not a reason to claim elevation.
            return false;
        }
    }

    private NativeComponentState ReadComponent(string displayName, string clsid)
    {
        string? path;

        try
        {
            path = ReadInProcServerPath(clsid);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Reading InProcServer32 for {clsid} failed: {ex.Message}");
            path = null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return new NativeComponentState(displayName, clsid, NativeRegistrationStatus.NotRegistered, null);
        }

        if (!File.Exists(path))
        {
            return new NativeComponentState(displayName, clsid, NativeRegistrationStatus.FileMissing, path);
        }

        return IsInAppDirectory(path)
            ? new NativeComponentState(displayName, clsid, NativeRegistrationStatus.RegisteredToCurrentFolder, path)
            : new NativeComponentState(displayName, clsid, NativeRegistrationStatus.RegisteredElsewhere, path);
    }

    private static string? ReadInProcServerPath(string clsid)
    {
        var subKey = $@"SOFTWARE\Classes\CLSID\{clsid}\InProcServer32";

        foreach (var view in Views)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(subKey, writable: false);

                // The default value holds the DLL path; ThreadingModel sits beside it.
                var value = key?.GetValue(null)?.ToString();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch (Exception)
            {
                // A view can be unavailable (32-bit OS, policy); the other one still answers.
            }
        }

        return null;
    }

    private string? ReadRecordedInstallPath()
    {
        foreach (var view in Views)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(InstallRecordKeyPath, writable: false);
                var value = key?.GetValue(InstallRecordValueName)?.ToString();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Reading HKLM\\{InstallRecordKeyPath} failed: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>True when a path sits inside this copy's folder.</summary>
    private bool IsInAppDirectory(string path)
    {
        try
        {
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppDirectory));
            var candidate = Path.GetFullPath(path);

            return candidate.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(candidate, directory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // An unparseable path is certainly not this folder.
            return false;
        }
    }

    // ------------------------------------------------------------------ elevated script

    /// <summary>
    /// Runs <c>scripts\Install-Native.ps1</c> with the given action through a UAC prompt
    /// and returns what happened. Never throws: a declined prompt and a non-zero exit code
    /// are both ordinary results the caller has to show.
    /// </summary>
    public async Task<ElevatedScriptResult> RunElevatedAsync(
        NativeScriptAction action,
        CancellationToken cancellationToken = default)
    {
        var scriptPath = NativeScriptPath;
        var actionName = action.ToString();

        if (!OperatingSystem.IsWindows())
        {
            return new ElevatedScriptResult(false, false, -1, "这一步只在 Windows 上可用。");
        }

        if (!File.Exists(scriptPath))
        {
            return new ElevatedScriptResult(
                false, false, -1,
                $"未找到 {NativeScriptRelativePath}（查找位置：{scriptPath}）。请确认发行包完整。");
        }

        var arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Action {actionName}";

        _logger.Info($"Running '{scriptPath}' -Action {actionName} elevated.");

        (int ExitCode, bool UserCancelled, string? Error) outcome;

        try
        {
            // The child holds the UAC prompt open until the user answers, and then runs the
            // registration, so waiting here on the UI thread would freeze the window for as
            // long as the user takes to decide. Only the argument string crosses threads.
            outcome = await Task.Run(() => StartElevated(arguments), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ElevatedScriptResult(false, false, -1, "已取消，脚本没有等待执行完成。");
        }

        if (outcome.UserCancelled)
        {
            _logger.Info($"Elevation for '{actionName}' was declined by the user.");
            return ElevatedScriptResult.Cancelled();
        }

        if (outcome.Error is not null)
        {
            _logger.Error($"Launching the elevated '{actionName}' script failed: {outcome.Error}");
            return new ElevatedScriptResult(false, false, -1, $"启动提权进程失败：{outcome.Error}");
        }

        if (outcome.ExitCode != 0)
        {
            _logger.Warn($"Elevated '{actionName}' script exited with {outcome.ExitCode}.");
            return new ElevatedScriptResult(
                false, false, outcome.ExitCode,
                $"脚本 {actionName} 未完成（退出码 {outcome.ExitCode}）。详细原因见脚本窗口或日志。");
        }

        _logger.Info($"Elevated '{actionName}' script succeeded.");

        var message = action switch
        {
            NativeScriptAction.Install => "已把虚拟摄像头组件注册到当前目录，并记录安装位置。",
            NativeScriptAction.Uninstall => "已从系统中注销本程序注册的虚拟摄像头组件。",
            _ => "已清除本程序对系统所做的改动（组件注册、安装位置记录和防火墙规则）。",
        };

        return new ElevatedScriptResult(true, false, 0, message);
    }

    /// <summary>Starts PowerShell elevated and waits for it. Runs on a worker thread.</summary>
    private static (int ExitCode, bool UserCancelled, string? Error) StartElevated(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
        };

        try
        {
            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return (-1, false, "无法启动 powershell.exe。");
            }

            process.WaitForExit();
            return (process.ExitCode, false, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ElevatedScriptResult.UserCancelledErrorCode)
        {
            return (ElevatedScriptResult.UserCancelledErrorCode, true, null);
        }
        catch (Win32Exception ex)
        {
            return (-1, false, ex.Message);
        }
    }

    // ------------------------------------------------------------------ shortcuts

    /// <summary>Creates the shortcut in the requested per-user location. Overwrites its own.</summary>
    /// <remarks>
    /// Keyed on the product name, exactly as <c>scripts\Uninstall-Portable.ps1</c> removes it,
    /// so what the app creates is what the uninstaller is able to take away again.
    /// </remarks>
    public ActionResult CreateShortcut(ShortcutLocation location)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ActionResult.Fail("创建快捷方式只在 Windows 上可用。");
        }

        var linkPath = GetShortcutPath(location);

        if (linkPath is null)
        {
            return ActionResult.Fail("系统没有返回桌面或开始菜单目录，无法创建快捷方式。");
        }

        var executablePath = GetExecutablePath();

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return ActionResult.Fail($"找不到本程序的可执行文件：{executablePath}");
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");

            if (shellType is null)
            {
                return ActionResult.Fail("系统没有注册 WScript.Shell 组件，无法创建快捷方式。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);

            dynamic shell = Activator.CreateInstance(shellType)!;

            try
            {
                dynamic link = shell.CreateShortcut(linkPath);

                try
                {
                    link.TargetPath = executablePath;
                    link.WorkingDirectory = AppDirectory;
                    link.Description = DisplayName;
                    link.IconLocation = executablePath;
                    link.Save();
                }
                finally
                {
                    ReleaseComObject(link);
                }
            }
            finally
            {
                ReleaseComObject(shell);
            }

            _logger.Info($"Created the {location} shortcut at '{linkPath}'.");
            return ActionResult.Ok($"已在{Describe(location)}创建快捷方式。");
        }
        catch (Exception ex)
        {
            _logger.Error($"Creating the {location} shortcut failed.", ex);
            return ActionResult.Fail($"创建快捷方式失败：{ex.Message}", ex);
        }
    }

    /// <summary>Removes the shortcuts this app created. Missing ones are not an error.</summary>
    /// <remarks>
    /// Removed by name rather than by inspecting the link target, matching
    /// <c>scripts\Uninstall-Portable.ps1</c>: a copy that was moved to a new folder leaves a
    /// shortcut pointing at the old, deleted executable, and its name is the only handle on it.
    /// </remarks>
    public ActionResult RemoveShortcuts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ActionResult.Fail("移除快捷方式只在 Windows 上可用。");
        }

        var removed = 0;
        var failures = new List<string>();

        foreach (var location in new[] { ShortcutLocation.Desktop, ShortcutLocation.StartMenu })
        {
            var linkPath = GetShortcutPath(location);

            if (linkPath is null)
            {
                continue;
            }

            try
            {
                if (File.Exists(linkPath))
                {
                    File.Delete(linkPath);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Removing the {location} shortcut failed: {ex.Message}");
                failures.Add($"{Describe(location)}：{ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            return ActionResult.Fail("移除快捷方式失败：" + string.Join("；", failures));
        }

        return removed > 0
            ? ActionResult.Ok($"已移除 {removed} 个快捷方式。")
            : ActionResult.Ok("本来就没有本程序创建的快捷方式。");
    }

    private static string Describe(ShortcutLocation location) =>
        location == ShortcutLocation.Desktop ? "桌面" : "开始菜单";

    private static string? GetShortcutPath(ShortcutLocation location)
    {
        var folder = location == ShortcutLocation.Desktop
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : Environment.GetFolderPath(Environment.SpecialFolder.Programs);

        return string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder, ShortcutFileName);
    }

    /// <summary>
    /// Releases a late-bound COM object. Failing to release is not worth failing the
    /// operation for, so this swallows the "not a COM object" cases.
    /// </summary>
    private static void ReleaseComObject(object? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(value);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidCastException)
        {
        }
    }

    // ------------------------------------------------------------------ 应用和功能

    /// <summary>
    /// Publishes the per-user「应用和功能」entry so the portable copy can be uninstalled
    /// from the normal Windows place. Uses <c>HKCU</c>, which needs no elevation.
    /// </summary>
    public ActionResult RegisterInAppsAndFeatures()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ActionResult.Fail("「应用和功能」登记只在 Windows 上可用。");
        }

        var executablePath = GetExecutablePath();

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return ActionResult.Fail("无法确定本程序的可执行文件路径。");
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppsAndFeaturesKeyPath, writable: true);

            if (key is null)
            {
                return ActionResult.Fail($"无法创建注册表项 HKCU\\{AppsAndFeaturesKeyPath}。");
            }

            var existingLocation = key.GetValue("InstallLocation")?.ToString();

            // A real installer writes its own entry. Repointing it at this folder would make
            //「应用和功能」offer the wrong uninstaller, so the entry is left alone instead.
            if (!string.IsNullOrWhiteSpace(existingLocation) &&
                !IsInAppDirectory(existingLocation) &&
                LooksLikeAnInstalledCopy(key))
            {
                _logger.Warn($"Refusing to overwrite the uninstall entry owned by '{existingLocation}'.");
                return ActionResult.Fail(
                    $"「应用和功能」里已有一条由安装程序写入的记录（位置：{existingLocation}），本程序不会覆盖它。");
            }

            key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
            key.SetValue("DisplayVersion", AppVersion, RegistryValueKind.String);
            key.SetValue("Publisher", Publisher, RegistryValueKind.String);
            key.SetValue("DisplayIcon", executablePath, RegistryValueKind.String);
            key.SetValue("InstallLocation", AppDirectory, RegistryValueKind.String);
            key.SetValue("UninstallString", BuildPortableUninstallCommand(), RegistryValueKind.String);

            // Both suppress the "修改"/"修复" buttons, which this entry cannot honour.
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            _logger.Info($"Registered in Apps & features: {AppDirectory}");
            return ActionResult.Ok("已在「应用和功能」中登记本程序，卸载入口指向 scripts\\Uninstall-Portable.ps1。");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ActionResult.Fail("写入「应用和功能」记录被拒绝。", ex);
        }
        catch (System.Security.SecurityException ex)
        {
            return ActionResult.Fail("写入「应用和功能」记录被安全策略拒绝。", ex);
        }
        catch (IOException ex)
        {
            return ActionResult.Fail($"写入「应用和功能」记录失败：{ex.Message}", ex);
        }
    }

    /// <summary>Removes this app's own「应用和功能」entry.</summary>
    public ActionResult UnregisterFromAppsAndFeatures()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ActionResult.Fail("「应用和功能」登记只在 Windows 上可用。");
        }

        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(AppsAndFeaturesKeyPath, writable: true))
            {
                if (key is null)
                {
                    return ActionResult.Ok("「应用和功能」里本来就没有本程序的记录。");
                }

                var existingLocation = key.GetValue("InstallLocation")?.ToString();

                if (!string.IsNullOrWhiteSpace(existingLocation) &&
                    !IsInAppDirectory(existingLocation) &&
                    LooksLikeAnInstalledCopy(key))
                {
                    _logger.Warn($"Refusing to delete the uninstall entry owned by '{existingLocation}'.");
                    return ActionResult.Fail(
                        $"「应用和功能」里那条记录属于另一个安装（位置：{existingLocation}），本程序不会删除它。");
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree(AppsAndFeaturesKeyPath, throwOnMissingSubKey: false);

            _logger.Info("Removed the Apps & features entry.");
            return ActionResult.Ok("已从「应用和功能」中移除本程序的记录。");
        }
        catch (Exception ex)
        {
            _logger.Error("Removing the Apps & features entry failed.", ex);
            return ActionResult.Fail($"移除「应用和功能」记录失败：{ex.Message}", ex);
        }
    }

    private (bool Exists, string? InstallLocation) ReadAppsAndFeaturesEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppsAndFeaturesKeyPath, writable: false);

            return key is null
                ? (false, null)
                : (true, key.GetValue("InstallLocation")?.ToString());
        }
        catch (Exception ex)
        {
            _logger.Warn($"Reading HKCU\\{AppsAndFeaturesKeyPath} failed: {ex.Message}");
            return (false, null);
        }
    }

    /// <summary>
    /// True when an existing entry was written by a real installer: MSI marks its entry
    /// with <c>WindowsInstaller=1</c>, and Inno Setup's uninstaller is called
    /// <c>unins*.exe</c>. Either means the entry is not ours to repoint.
    /// </summary>
    private static bool LooksLikeAnInstalledCopy(RegistryKey key)
    {
        if (key.GetValue("WindowsInstaller") is int installerFlag && installerFlag == 1)
        {
            return true;
        }

        var uninstall = key.GetValue("UninstallString")?.ToString() ?? string.Empty;

        return uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ||
               uninstall.Contains("unins", StringComparison.OrdinalIgnoreCase) ||
               uninstall.Contains("Uninstall.exe", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildPortableUninstallCommand()
    {
        var scriptPath = Path.Combine(AppDirectory, PortableUninstallScriptRelativePath);

        // Quotes because the release folder name contains spaces, which it does here.
        return $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";
    }

    /// <summary>
    /// The version shown in「应用和功能」. The UI reads its own assembly the same way;
    /// both assemblies share the repository's single version.
    /// </summary>
    private static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    // ------------------------------------------------------------------ executable path

    /// <summary>Full path of the running executable, or an empty string as a fallback.</summary>
    private static string GetExecutablePath()
    {
        // Environment.ProcessPath is the correct API here: Assembly.Location returns an
        // empty string for a single-file published app, and the app ships as one.
        var path = Environment.ProcessPath;

        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        using var process = Process.GetCurrentProcess();
        return process.MainModule?.FileName ?? string.Empty;
    }
}
