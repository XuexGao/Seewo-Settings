using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SeewoAssistant.Core.Configuration;
using SeewoAssistant.Core.Services;
using SeewoAssistant.Core.Services.CaptureGuard;

namespace SeewoAssistant.Pages;

/// <summary>
/// Application settings: general behaviour, per-module defaults, and the storage
/// locations.
/// </summary>
public sealed partial class SettingsPage : ModulePageBase
{
    private readonly ObservableCollection<string> _exclusions = [];

    private bool _suppressSettingWrites;

    public SettingsPage()
    {
        InitializeComponent();

        ExclusionList.ItemsSource = _exclusions;

        CloseBehaviorCombo.Items.Add("最小化到托盘（后台服务继续运行）");
        CloseBehaviorCombo.Items.Add("直接退出程序");

        foreach (var resolution in new[] { "1280×720", "1920×1080", "640×480", "320×240" })
        {
            DefaultResolutionCombo.Items.Add(resolution);
        }

        foreach (var fps in new[] { "30 fps", "15 fps", "60 fps" })
        {
            DefaultFpsCombo.Items.Add(fps);
        }

        CaptureModeCombo.Items.Add("穿透隐身（截图中完全不出现）");
        CaptureModeCombo.Items.Add("黑块遮蔽（截图中显示为黑块）");

        ThemeCombo.Items.Add("跟随系统");
        ThemeCombo.Items.Add("浅色");
        ThemeCombo.Items.Add("深色");
    }

    // ------------------------------------------------------------------ intro

    protected override string? IntroKey => "settings";

    protected override string? IntroTitle => "设置";

    protected override string? IntroBody =>
        """
这里的改动基本都会立刻生效，也会立刻存盘，不需要点保存。

「常规」是关闭窗口的行为、开机自启、外观主题。
往下是各模块的默认值——它们是程序启动时、以及托盘快捷开关使用的值，
改了不影响当前正在运行的推流或监控。

最底下一块是配置文件的位置，以及出问题时的重置入口。
""";

    protected override void OnServicesReady()
    {
        PopulateFromSettings();

        // Reflect the real registry state rather than the stored flag, which can
        // drift if the entry was removed by another tool.
        _suppressSettingWrites = true;
        RunAtStartupToggle.IsOn = Services.AppStartup.IsRegistered();
        _suppressSettingWrites = false;
        UpdateRunAtStartupHint();

        SettingsPathText.Text = Services.SettingsStore.FilePath;
        LogPathText.Text = Services.FileLogger.LogFilePath;

        VersionText.Text = $"版本：{Assembly.GetExecutingAssembly().GetName().Version}";

        var capability = Services.VirtualCamera.DetectCapability();
        NativeStatusText.Text =
            $"虚拟摄像头后端：{capability.Summary}；" +
            $"原生组件：{(Services.VirtualCamera.IsSetupToolAvailable ? "已就位" : "缺失")}；" +
            $"跨进程载荷：{(Services.CaptureGuard.IsCrossProcessAvailable ? "已就位" : "缺失")}";

        RefreshSystemIntegration();
    }

    // ------------------------------------------------------------------ load

    private void PopulateFromSettings()
    {
        _suppressSettingWrites = true;

        var settings = Services.Settings;

        CloseBehaviorCombo.SelectedIndex = settings.CloseBehavior == CloseBehavior.MinimizeToTray ? 0 : 1;

        RunAtStartupToggle.IsOn = settings.RunAtStartup;
        StartMinimizedToggle.IsOn = settings.StartMinimized;

        ThemeCombo.SelectedIndex = settings.Theme switch
        {
            AppTheme.Light => 1,
            AppTheme.Dark => 2,
            _ => 0,
        };

        // Say where a migrated value came from, so a light theme on a light-looking
        // machine is not mistaken for the switch being broken.
        ThemeHint.Text = settings.ThemeMigratedFromLegacyKey
            ? "已沿用旧版设置（原先关闭了「跟随系统主题」，因此固定为浅色）。随时可以改。"
            : settings.Theme switch
            {
                AppTheme.System => "跟随 Windows 的浅色/深色设置，系统切换时界面会一起变。",
                AppTheme.Light => "始终使用浅色外观，不随系统变化。",
                _ => "始终使用深色外观，不随系统变化。",
            };

        ShowPageIntrosToggle.IsOn = settings.ShowPageIntros;

        SelectCombo(DefaultResolutionCombo, $"{settings.VirtualCameraWidth}×{settings.VirtualCameraHeight}");
        SelectCombo(DefaultFpsCombo, $"{settings.VirtualCameraFps} fps");

        DefaultColorBox.Text = settings.VirtualCameraDefaultColor;
        DefaultImageBox.Text = settings.VirtualCameraDefaultImage;

        MonitorCameraToggle.IsOn = settings.MonitorCamera;
        MonitorMicrophoneToggle.IsOn = settings.MonitorMicrophone;
        PrivacyAutoStartToggle.IsOn = settings.PrivacyMonitorAutoStart;
        ResolvePidsToggle.IsOn = settings.PrivacyResolveProcessIds;
        ShowToastToggle.IsOn = settings.PrivacyShowToast;
        ShowBannerToggle.IsOn = settings.PrivacyShowBanner;
        PlaySoundToggle.IsOn = settings.PrivacyPlaySound;
        BannerSecondsBox.Value = settings.PrivacyBannerSeconds;

        _exclusions.Clear();
        foreach (var exclusion in settings.PrivacyExcludedApplications)
        {
            _exclusions.Add(exclusion);
        }

        CaptureModeCombo.SelectedIndex =
            settings.CaptureDefaultMode == CaptureProtectionMode.ExcludeFromCapture ? 0 : 1;

        AllowCrossProcessToggle.IsOn = settings.CaptureAllowCrossProcess;

        UpdateRunAtStartupHint();
        UpdateCrossProcessInfo();

        _suppressSettingWrites = false;
    }

    private static void SelectCombo(ComboBox combo, string value)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is string item && item.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private void UpdateRunAtStartupHint()
    {
        if (!RunAtStartupToggle.IsOn)
        {
            RunAtStartupHint.Text = string.Empty;
            RunAtStartupHint.Visibility = Visibility.Collapsed;
            return;
        }

        // Registering a startup entry writes to the per-user Run key, which does not
        // need elevation. Saying where it goes is more useful than a bare toggle.
        RunAtStartupHint.Text =
            "会在 HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run 下写入一条启动项，不需要管理员权限。" +
            "关闭这个开关会把它删除。";
        RunAtStartupHint.Visibility = Visibility.Visible;
    }

    private void UpdateCrossProcessInfo()
    {
        if (!AllowCrossProcessToggle.IsOn)
        {
            CrossProcessInfoBar.IsOpen = false;
            return;
        }

        CrossProcessInfoBar.IsOpen = true;
        CrossProcessInfoBar.Severity = Services.CaptureGuard.IsCrossProcessAvailable
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Error;

        CrossProcessInfoBar.Message = Services.CaptureGuard.IsCrossProcessAvailable
            ? "保护其他程序的窗口时，会往那个进程里写入一小段机器码桩，由它自己调用 SetWindowDisplayAffinity，"
              + "调用返回后立即释放，不会驻留。杀软的主动防御可能拦下这个动作，这是这类技术的固有限制，不是缺陷。"
              + "只对你显式选中的那一个窗口生效。"
            : $"当前系统不支持跨进程保护：需要 Windows 10 2004（内部版本 {CaptureGuardService.ExcludeFromCaptureMinimumBuild}）以上。";
    }

    private void OnAllowCrossProcessToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingWrites)
        {
            return;
        }

        Services.Settings.CaptureAllowCrossProcess = AllowCrossProcessToggle.IsOn;
        UpdateCrossProcessInfo();
    }

    // ------------------------------------------------------------------ theme

    /// <summary>
    /// Applies a new theme the moment it is picked, then saves it.
    /// </summary>
    /// <remarks>
    /// Applied immediately rather than on the page's usual deferred save, because the
    /// whole point of a theme picker is to see the result as you choose. Saving at the
    /// same time is what makes it survive a restart — the retired title-bar button
    /// changed the live tree and persisted nothing.
    /// </remarks>
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingWrites)
        {
            return;
        }

        Services.Settings.Theme = ThemeCombo.SelectedIndex switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.System,
        };

        Services.Settings.ThemeMigratedFromLegacyKey = false;
        Services.ApplyTheme();
        Services.SaveSettings();

        // Re-read through the normal path so the hint and the combo agree, and so the
        // page's deferred CollectFromControls cannot write the pre-change value back over
        // this on the next navigation.
        PopulateFromSettings();
    }

    /// <summary>
    /// Clears the record of shown explanations so they appear again.
    /// </summary>
    /// <remarks>
    /// This is what makes the one-time intros safe to dismiss: the explanation is still
    /// recoverable from a known place, rather than being gone for good once the dialog is
    /// closed. It also turns the feature back on, because clearing the list while the
    /// feature is off would appear to do nothing.
    /// </remarks>
    private void OnReplayIntros(object sender, RoutedEventArgs e)
    {
        Services.Settings.SeenPageIntros.Clear();
        Services.Settings.ShowPageIntros = true;

        _suppressSettingWrites = true;
        ShowPageIntrosToggle.IsOn = true;
        _suppressSettingWrites = false;

        Services.SaveSettings();

        Report("已重置。下次进入各页面时会重新显示说明。");
    }

    // ------------------------------------------------------------------ exclusions

    private void OnAddExclusion(object sender, RoutedEventArgs e)
    {
        var value = NewExclusionBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!_exclusions.Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            _exclusions.Add(value);
        }

        NewExclusionBox.Text = string.Empty;
        Report($"已加入不提醒列表：{value}。记得保存设置。");
    }

    private void OnRemoveExclusion(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value })
        {
            _exclusions.Remove(value);
            Report($"已从不提醒列表移除：{value}。记得保存设置。");
        }
    }

    // ------------------------------------------------------------------ save / reset

    private void OnSave(object sender, RoutedEventArgs e)
    {
        CollectFromControls();

        // The startup entry is a registry side effect, not just a stored flag, so it
        // is applied here and its outcome folded into the save result.
        var startupResult = Services.AppStartup.SetRegistered(
            RunAtStartupToggle.IsOn,
            StartMinimizedToggle.IsOn);

        var saved = Services.SaveSettings();

        if (!startupResult.Success)
        {
            SaveStatusText.Text = $"设置已保存，但开机自启设置失败：{startupResult.Message}";
            SaveStatusText.Visibility = Visibility.Visible;
            Report(startupResult.Message, StatusSeverity.Warning);
            return;
        }

        SaveStatusText.Text = saved
            ? $"已保存到 {Services.SettingsStore.FilePath}（{DateTime.Now:HH:mm:ss}）。{startupResult.Message}"
            : "保存失败，详情见状态栏和日志。";
        SaveStatusText.Visibility = Visibility.Visible;

        Report(saved ? "设置已保存。" : "设置保存失败。", saved ? StatusSeverity.Success : StatusSeverity.Error);
    }

    /// <summary>Copies the control values back into the settings object.</summary>
    private void CollectFromControls()
    {
        var settings = Services.Settings;

        settings.CloseBehavior = CloseBehaviorCombo.SelectedIndex == 0
            ? CloseBehavior.MinimizeToTray
            : CloseBehavior.Exit;

        settings.RunAtStartup = RunAtStartupToggle.IsOn;
        settings.StartMinimized = StartMinimizedToggle.IsOn;

        settings.Theme = ThemeCombo.SelectedIndex switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.System,
        };

        // The choice has now been made here, so the migration note no longer applies.
        settings.ThemeMigratedFromLegacyKey = false;

        settings.ShowPageIntros = ShowPageIntrosToggle.IsOn;

        if (DefaultResolutionCombo.SelectedItem is string resolution)
        {
            var parts = resolution.Split('×');
            if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height))
            {
                settings.VirtualCameraWidth = width;
                settings.VirtualCameraHeight = height;
            }
        }

        if (DefaultFpsCombo.SelectedItem is string fps && int.TryParse(fps.Split(' ')[0], out var fpsValue))
        {
            settings.VirtualCameraFps = fpsValue;
        }

        settings.VirtualCameraDefaultColor = DefaultColorBox.Text?.Trim() ?? "#1F6FEB";
        settings.VirtualCameraDefaultImage = DefaultImageBox.Text?.Trim() ?? string.Empty;

        settings.MonitorCamera = MonitorCameraToggle.IsOn;
        settings.MonitorMicrophone = MonitorMicrophoneToggle.IsOn;
        settings.PrivacyMonitorAutoStart = PrivacyAutoStartToggle.IsOn;
        settings.PrivacyResolveProcessIds = ResolvePidsToggle.IsOn;
        settings.PrivacyShowToast = ShowToastToggle.IsOn;
        settings.PrivacyShowBanner = ShowBannerToggle.IsOn;
        settings.PrivacyPlaySound = PlaySoundToggle.IsOn;

        if (!double.IsNaN(BannerSecondsBox.Value))
        {
            settings.PrivacyBannerSeconds = (int)BannerSecondsBox.Value;
        }

        settings.PrivacyExcludedApplications = _exclusions.ToList();

        settings.CaptureDefaultMode = CaptureModeCombo.SelectedIndex == 0
            ? CaptureProtectionMode.ExcludeFromCapture
            : CaptureProtectionMode.Blackout;

        settings.CaptureAllowCrossProcess = AllowCrossProcessToggle.IsOn;

        // Re-applying pushes the new values onto the live services so they take
        // effect without a restart.
        Services.ApplySettingsToServices();
    }

    private async void OnReload(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "放弃修改",
                "未保存的修改会丢失，设置将重新从磁盘加载。确定要继续吗？",
                "重新加载"))
        {
            return;
        }

        Services.ReloadSettings();
        PopulateFromSettings();

        SaveStatusText.Text = $"已从 {Services.SettingsStore.FilePath} 重新加载。";
        SaveStatusText.Visibility = Visibility.Visible;

        Report("设置已重新加载。", StatusSeverity.Success);
    }

    private async void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "恢复默认设置",
                "所有设置都会恢复为默认值，包括不提醒列表、希沃规则和定时任务。\n\n" +
                "这个操作不可撤销。配置文件会被覆盖。",
                "恢复默认"))
        {
            return;
        }

        // A fresh instance carries the defaults. Scheduled tasks and rules are part
        // of the settings file, so they are cleared too, which the confirmation says.
        Services.SettingsStore.Save(new AppSettings());
        Services.ReloadSettings();

        // The scheduler holds its own copy of the task list, so it must be told.
        Services.Scheduler.SetTasks(Services.Settings.ScheduledTasks);

        PopulateFromSettings();

        SaveStatusText.Text = "已恢复默认设置。";
        SaveStatusText.Visibility = Visibility.Visible;

        Report("已恢复默认设置。", StatusSeverity.Success);
    }

    // ------------------------------------------------------------------ folders

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(Services.SettingsStore.FilePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            OpenInExplorer(directory);
        }
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(Services.FileLogger.LogFilePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            OpenInExplorer(directory);
        }
    }

    private void OnResetAcknowledgement(object sender, RoutedEventArgs e)
    {
        Services.Settings.CaptureCrossProcessAcknowledged = false;
        Services.Settings.CaptureAllowCrossProcess = false;

        _suppressSettingWrites = true;
        AllowCrossProcessToggle.IsOn = false;
        _suppressSettingWrites = false;

        UpdateCrossProcessInfo();
        Services.SaveSettings();

        Report("已重置跨进程风险确认。下次开启时会重新弹出确认对话框。", StatusSeverity.Success);
    }

    // ------------------------------------------------------------------ system integration

    /// <summary>
    /// Redraws the system-integration card from the live machine state.
    /// </summary>
    /// <remarks>
    /// Read-only and unelevated: reading HKLM and HKCU needs no administrator rights, which is
    /// what lets the card tell the truth about where the CLSID points before the user decides
    /// whether to change anything.
    /// </remarks>
    private void RefreshSystemIntegration()
    {
        var state = Services.SystemIntegration.GetState();
        var lines = new List<string>();

        foreach (var component in state.Components)
        {
            lines.Add(component.Describe());
        }

        if (state.RecordedPathDiffers)
        {
            lines.Add(
                $"系统记录的安装目录：{state.RecordedInstallPath}" +
                $"（当前目录：{Services.SystemIntegration.AppDirectory}）");
        }

        if (state.HasOrphanedRegistration)
        {
            lines.Add("有注册指向已删除或其他目录，点「注册到当前目录」可以修正。");
        }

        var shortcuts = new List<string>();

        if (state.DesktopShortcutExists)
        {
            shortcuts.Add("桌面");
        }

        if (state.StartMenuShortcutExists)
        {
            shortcuts.Add("开始菜单");
        }

        lines.Add(shortcuts.Count == 0
            ? "还没有创建快捷方式。"
            : $"已有快捷方式：{string.Join("、", shortcuts)}。");

        lines.Add(state.IsListedInAppsAndFeatures
            ? "已在「应用和功能」中登记，可以从那里卸载。"
            : "未在「应用和功能」中登记；直接删除文件夹不会清理系统里的注册。");

        lines.Add(state.IsElevated
            ? "当前以管理员身份运行，注册和清除不会弹提权提示。"
            : "当前不是管理员身份，注册和清除会弹出系统提权提示。");

        lines.Add("本机只使用其中一个后端，另一个显示「未注册」是正常的。");

        SystemIntegrationStatusText.Text = string.Join("\n", lines);
    }

    private async void OnRegisterToCurrentFolder(object sender, RoutedEventArgs e)
    {
        var state = Services.SystemIntegration.GetState();

        if (!await ConfirmAsync(
                "注册到当前目录",
                "这会把虚拟摄像头组件注册到系统（HKLM 里的 COM 注册表项），"
                + "并把系统记录的安装目录改成这个工具的目录：\n\n"
                + Services.SystemIntegration.AppDirectory + "\n\n"
                + (state.IsElevated
                    ? "当前已经以管理员身份运行，不会弹出提权提示。"
                    : "需要管理员权限，接下来会弹出系统提权提示。")
                + "\n\n是否继续？",
                "注册"))
        {
            return;
        }

        await RunGuardedAsync("注册到当前目录", async () =>
        {
            var result = await Services.SystemIntegration.RunElevatedAsync(NativeScriptAction.Install);
            RefreshSystemIntegration();
            ReportScriptResult(result);
        });
    }

    private async void OnCleanupSystemChanges(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "清除本工具对系统的所有改动",
                "这会反注册这个工具注册到系统的虚拟摄像头 COM 组件，移除系统记录的安装目录、"
                + "这个工具创建的防火墙规则、快捷方式，以及「应用和功能」里的登记。\n\n"
                + "它不依赖这个工具的原生文件，删除文件夹之后也能用来清理残留。"
                + "组件注册部分需要管理员权限，接下来会弹出系统提权提示。这个操作不可撤销。",
                "清除"))
        {
            return;
        }

        await RunGuardedAsync("清除系统改动", async () =>
        {
            var scriptResult = await Services.SystemIntegration.RunElevatedAsync(NativeScriptAction.Cleanup);

            // The per-user half of the change lives here, not in the script: the shortcut and
            // the「应用和功能」entry are the app's own doing. They are cleaned even when the
            // elevated half was declined, and the message says which half did not run.
            var apps = Services.SystemIntegration.UnregisterFromAppsAndFeatures();
            var shortcuts = Services.SystemIntegration.RemoveShortcuts();

            RefreshSystemIntegration();

            if (scriptResult.Success && apps.Success && shortcuts.Success)
            {
                Report("已清除这个工具对系统的所有改动。", StatusSeverity.Success);
                return;
            }

            var messages = new List<string> { scriptResult.Message };

            if (!apps.Success)
            {
                messages.Add(apps.Message);
            }

            if (!shortcuts.Success)
            {
                messages.Add(shortcuts.Message);
            }

            Report(
                string.Join("；", messages.Where(m => !string.IsNullOrWhiteSpace(m))),
                scriptResult.UserCancelled ? StatusSeverity.Warning : StatusSeverity.Error);
        });
    }

    private void OnCreateDesktopShortcut(object sender, RoutedEventArgs e) =>
        RunShortcutAction(() => Services.SystemIntegration.CreateShortcut(ShortcutLocation.Desktop));

    private void OnCreateStartMenuShortcut(object sender, RoutedEventArgs e) =>
        RunShortcutAction(() => Services.SystemIntegration.CreateShortcut(ShortcutLocation.StartMenu));

    private void OnRemoveShortcuts(object sender, RoutedEventArgs e) =>
        RunShortcutAction(() => Services.SystemIntegration.RemoveShortcuts());

    private void RunShortcutAction(Func<ActionResult> action)
    {
        var result = action();
        RefreshSystemIntegration();
        Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Error);
    }

    private void OnToggleAppsAndFeatures(object sender, RoutedEventArgs e)
    {
        // The button is one control in both directions, so the current state decides what it
        // does - and the card above always says which of the two just happened.
        var listed = Services.SystemIntegration.GetState().IsListedInAppsAndFeatures;

        var result = listed
            ? Services.SystemIntegration.UnregisterFromAppsAndFeatures()
            : Services.SystemIntegration.RegisterInAppsAndFeatures();

        RefreshSystemIntegration();
        Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Error);
    }

    /// <summary>Reports a script result, treating a declined UAC prompt as a decision.</summary>
    private void ReportScriptResult(ElevatedScriptResult result) =>
        Report(
            result.Message,
            result.Success
                ? StatusSeverity.Success
                : (result.UserCancelled ? StatusSeverity.Warning : StatusSeverity.Error));

    private void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Services.Logger.Error($"Opening '{path}' failed.", ex);
            Report($"无法打开 {path}：{ex.Message}", StatusSeverity.Error);
        }
    }
}
