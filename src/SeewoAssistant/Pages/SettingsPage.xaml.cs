using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SeewoAssistant.Core.Configuration;
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
    }

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
    }

    // ------------------------------------------------------------------ load

    private void PopulateFromSettings()
    {
        _suppressSettingWrites = true;

        var settings = Services.Settings;

        CloseBehaviorCombo.SelectedIndex = settings.CloseBehavior == CloseBehavior.MinimizeToTray ? 0 : 1;

        RunAtStartupToggle.IsOn = settings.RunAtStartup;
        StartMinimizedToggle.IsOn = settings.StartMinimized;
        FollowSystemThemeToggle.IsOn = settings.FollowSystemTheme;

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
        settings.FollowSystemTheme = FollowSystemThemeToggle.IsOn;

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
