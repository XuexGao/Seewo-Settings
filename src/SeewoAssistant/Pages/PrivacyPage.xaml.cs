using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SeewoAssistant.Core.Models;

namespace SeewoAssistant.Pages;

/// <summary>
/// Module 2: alerts the user when any program starts using the camera or microphone.
/// </summary>
/// <remarks>
/// The activity list is a rolling window rather than a persisted log: the monitor's
/// own <c>RecentEvents</c> is the source of truth on entry, and this page only mirrors
/// what arrives while it is open.
/// </remarks>
public sealed partial class PrivacyPage : ModulePageBase
{
    /// <summary>Matches the monitor's own cap, so the two views cannot drift.</summary>
    private const int MaxActivityRows = 200;

    private readonly ObservableCollection<PrivacyActivityRow> _activity = [];

    private bool _suppressSettingWrites;

    public PrivacyPage()
    {
        InitializeComponent();
        ActivityList.ItemsSource = _activity;
    }

    protected override void OnServicesReady()
    {
        _suppressSettingWrites = true;

        var settings = Services.Settings;

        MonitorCameraToggle.IsOn = settings.MonitorCamera;
        MonitorMicrophoneToggle.IsOn = settings.MonitorMicrophone;
        ResolveProcessIdsToggle.IsOn = settings.PrivacyResolveProcessIds;
        ShowToastToggle.IsOn = settings.PrivacyShowToast;
        ShowBannerToggle.IsOn = settings.PrivacyShowBanner;
        PlaySoundToggle.IsOn = settings.PrivacyPlaySound;
        BannerSecondsBox.Value = settings.PrivacyBannerSeconds;
        AutoStartToggle.IsOn = settings.PrivacyMonitorAutoStart;

        _suppressSettingWrites = false;

        RefreshExclusionList();
        SeedActivityFromMonitor();
        UpdateState();

        Services.PrivacyMonitor.UsageDetected += OnUsageDetected;
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // The monitor outlives the page. Leaving the handler attached would keep this
        // page (and its whole visual tree) alive and would append to a list nobody sees.
        Services.PrivacyMonitor.UsageDetected -= OnUsageDetected;
    }

    // ------------------------------------------------------------------ state

    private void UpdateState()
    {
        var monitor = Services.PrivacyMonitor;
        var running = monitor.IsRunning;

        MonitorStateText.Text = !running
            ? "监控已停止，不会产生任何提醒。"
            : DescribeMonitoredDevices() is { Length: > 0 } devices
                ? $"监控运行中，正在监视{devices}的使用情况。"
                : "监控正在运行，但摄像头和麦克风都被关闭了，因此不会有任何提醒。";

        MonitorBadgeText.Text = running ? "运行中" : "已停止";
        SetBadge(MonitorBadge, MonitorBadgeText, MonitorBadgeText.Text, running);

        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;

        UpdateStoreState(running);
    }

    private string DescribeMonitoredDevices()
    {
        var camera = Services.Settings.MonitorCamera;
        var microphone = Services.Settings.MonitorMicrophone;

        return (camera, microphone) switch
        {
            (true, true) => "摄像头和麦克风",
            (true, false) => "摄像头",
            (false, true) => "麦克风",
            _ => string.Empty,
        };
    }

    /// <summary>How many devices the current settings ask us to watch.</summary>
    private int DescribeMonitoredDeviceCount() =>
        (Services.Settings.MonitorCamera ? 1 : 0) + (Services.Settings.MonitorMicrophone ? 1 : 0);

    private void UpdateStoreState(bool running)
    {
        var ready = Services.PrivacyMonitor.IsStoreReady;

        if (!running)
        {
            StoreStateText.Text = "监控未运行，尚未尝试读取授权记录。";
            StoreBadgeText.Text = "未监视";
            SetBadge(StoreBadge, StoreBadgeText, StoreBadgeText.Text, active: false);
        }
        else
        {
            // Report per device rather than as one lump. IsStoreReady is true when
            // *either* key exists, so saying "已找到摄像头和麦克风的授权记录" on a
            // machine that has only ever used a microphone would be a lie the user
            // has no way to check.
            var armed = Services.PrivacyMonitor.ArmedDevices;
            var monitored = DescribeMonitoredDevices();

            var armedNames = armed
                .Select(d => d == PrivacyDevice.Camera ? "摄像头" : "麦克风")
                .ToList();

            if (armed.Count > 0)
            {
                StoreStateText.Text =
                    $"已找到{string.Join("和", armedNames)}的授权记录，可以实时收到变化通知。" +
                    (armed.Count < DescribeMonitoredDeviceCount()
                        ? $"（{monitored}中尚未出现记录的设备会被持续重试）"
                        : string.Empty);
                StoreBadgeText.Text = "已就绪";
                SetBadge(StoreBadge, StoreBadgeText, StoreBadgeText.Text, active: true);
            }
            else
            {
                StoreStateText.Text = "监控正在运行，但系统中还没有找到对应的授权记录。";
                StoreBadgeText.Text = "等待中";
                SetBadge(StoreBadge, StoreBadgeText, StoreBadgeText.Text, active: true, warning: true);
            }
        }

        // Only explain the missing key when it is actually missing: this is the single
        // most common "it looks broken but is not" report, so the wording is explicit
        // that nothing is wrong and nothing needs fixing.
        StoreInfoBar.Message =
            "注册表中的摄像头/麦克风授权记录键只有在某个能力被使用过至少一次之后才会出现，因此全新或从未用过摄像头的系统上这里是空的。\n" +
            "这是正常现象，不是错误。监控会每 30 秒自动重试一次；你只要用任意程序打开一次摄像头或麦克风，记录就会出现。";
        StoreInfoBar.Severity = InfoBarSeverity.Informational;
        StoreInfoBar.IsOpen = running && !ready;
    }

    private void OnRefreshState(object sender, RoutedEventArgs e)
    {
        UpdateState();
        Report("状态已刷新。");
    }

    // ------------------------------------------------------------------ monitor control

    private void OnStartMonitor(object sender, RoutedEventArgs e)
    {
        try
        {
            Services.PrivacyMonitor.Options = Services.Settings.ToPrivacyOptions();
            Services.PrivacyMonitor.Start();

            UpdateState();
            Report("隐私监控已启动。", StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Starting the privacy monitor failed.", ex);
            Report($"启动监控失败：{ex.Message}", StatusSeverity.Error);
        }
    }

    private void OnStopMonitor(object sender, RoutedEventArgs e)
    {
        try
        {
            Services.PrivacyMonitor.Stop();

            UpdateState();
            Report("隐私监控已停止。", StatusSeverity.Informational);
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Stopping the privacy monitor failed.", ex);
            Report($"停止监控失败：{ex.Message}", StatusSeverity.Error);
        }
    }

    // ------------------------------------------------------------------ settings

    private void OnMonitorCameraToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingWrites)
        {
            return;
        }

        Services.Settings.MonitorCamera = MonitorCameraToggle.IsOn;
        PersistSetting(MonitorCameraToggle.IsOn ? "已开启摄像头监控。" : "已关闭摄像头监控。");
        RearmMonitorForDeviceChange();
    }

    private void OnMonitorMicrophoneToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingWrites)
        {
            return;
        }

        Services.Settings.MonitorMicrophone = MonitorMicrophoneToggle.IsOn;
        PersistSetting(MonitorMicrophoneToggle.IsOn ? "已开启麦克风监控。" : "已关闭麦克风监控。");
        RearmMonitorForDeviceChange();
    }

    /// <summary>
    /// Restarts the monitor after a device is switched on or off.
    /// </summary>
    /// <remarks>
    /// The service arms its registry watchers once at <c>Start</c> and does not
    /// re-evaluate which devices are enabled afterwards, so without this a newly
    /// enabled device would only be picked up on the next launch.
    /// </remarks>
    private void RearmMonitorForDeviceChange()
    {
        try
        {
            if (!Services.PrivacyMonitor.IsRunning)
            {
                return;
            }

            Services.PrivacyMonitor.Stop();
            Services.PrivacyMonitor.Start();

            UpdateState();
            Report("监控范围已更新，注册表监视已按新设置重新建立。");
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Re-arming the privacy monitor after a device change failed.", ex);
            Report($"重新建立监控失败：{ex.Message}", StatusSeverity.Error);
        }
    }

    private void OnResolveProcessIdsToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingWrites)
        {
            return;
        }

        Services.Settings.PrivacyResolveProcessIds = ResolveProcessIdsToggle.IsOn;
        PersistSetting(ResolveProcessIdsToggle.IsOn
            ? "已开启进程 ID 解析（需要管理员权限，权限不足时提醒仍会发出）。"
            : "已关闭进程 ID 解析。");
    }

    private void OnShowToastToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingWrites)
        {
            return;
        }

        Services.Settings.PrivacyShowToast = ShowToastToggle.IsOn;
        PersistSetting(ShowToastToggle.IsOn ? "已开启系统通知。" : "已关闭系统通知。");
    }

    private void OnShowBannerToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingWrites)
        {
            return;
        }

        Services.Settings.PrivacyShowBanner = ShowBannerToggle.IsOn;
        PersistSetting(ShowBannerToggle.IsOn ? "已开启屏幕横幅提醒。" : "已关闭屏幕横幅提醒。");
    }

    private void OnPlaySoundToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingWrites)
        {
            return;
        }

        Services.Settings.PrivacyPlaySound = PlaySoundToggle.IsOn;
        PersistSetting(PlaySoundToggle.IsOn ? "已开启提示音。" : "已关闭提示音。");
    }

    private void OnBannerSecondsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressSettingWrites)
        {
            return;
        }

        // Clearing the box yields NaN; leaving the previous value in place is friendlier
        // than writing a nonsense number into the settings file.
        if (double.IsNaN(args.NewValue))
        {
            return;
        }

        var seconds = (int)Math.Round(args.NewValue);
        Services.Settings.PrivacyBannerSeconds = seconds;
        PersistSetting($"横幅停留时间已设为 {seconds} 秒。");
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingWrites)
        {
            return;
        }

        Services.Settings.PrivacyMonitorAutoStart = AutoStartToggle.IsOn;
        PersistSetting(AutoStartToggle.IsOn ? "已开启随程序启动自动监控。" : "已关闭随程序启动自动监控。");
    }

    private void PersistSetting(string message)
    {
        // Push the change onto the live monitor options before saving, so a running
        // monitor picks it up even if the write to disk fails.
        Services.ApplySettingsToServices();

        if (Services.SaveSettings())
        {
            Report(message, StatusSeverity.Success);
        }

        UpdateState();
    }

    // ------------------------------------------------------------------ exclusions

    private void RefreshExclusionList()
    {
        // The settings list is mutated in place, so the ItemsSource is reassigned to
        // force the ListView to re-read it rather than relying on change notification.
        ExclusionList.ItemsSource = null;
        ExclusionList.ItemsSource = Services.Settings.PrivacyExcludedApplications;

        ExclusionEmptyText.Visibility = Services.Settings.PrivacyExcludedApplications.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnAddExclusion(object sender, RoutedEventArgs e)
    {
        var value = ExclusionTextBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            Report("请先填写要排除的程序名或路径。", StatusSeverity.Warning);
            return;
        }

        var list = Services.Settings.PrivacyExcludedApplications;

        if (list.Any(item => item.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            Report($"「{value}」已经在排除名单里了。", StatusSeverity.Warning);
            return;
        }

        list.Add(value);
        ExclusionTextBox.Text = string.Empty;
        RefreshExclusionList();

        PersistSetting($"已把「{value}」加入排除名单，该程序使用摄像头或麦克风时不再提醒。");
    }

    private void OnRemoveExclusion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string value })
        {
            return;
        }

        if (Services.Settings.PrivacyExcludedApplications.RemoveAll(
                item => item.Equals(value, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return;
        }

        RefreshExclusionList();
        PersistSetting($"已把「{value}」从排除名单移除。");
    }

    // ------------------------------------------------------------------ activity

    private void SeedActivityFromMonitor()
    {
        _activity.Clear();

        // The monitor keeps its own capped history, so a page opened mid-session starts
        // with the recent past rather than an empty list.
        foreach (var usageEvent in Services.PrivacyMonitor.RecentEvents)
        {
            _activity.Add(new PrivacyActivityRow(usageEvent));
        }

        UpdateActivityCount();
    }

    private void OnUsageDetected(object? sender, PrivacyUsageEvent usageEvent)
    {
        // Raised on a registry watch thread. Touching an ObservableCollection bound to
        // the ListView from here would throw, so the row is added on the UI thread.
        if (DispatcherQueue is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _activity.Insert(0, new PrivacyActivityRow(usageEvent));

                while (_activity.Count > MaxActivityRows)
                {
                    _activity.RemoveAt(_activity.Count - 1);
                }

                UpdateActivityCount();
            }
            catch (Exception ex)
            {
                Services.Logger.Error("Adding a privacy activity row failed.", ex);
            }
        });
    }

    private void UpdateActivityCount()
    {
        ActivityCountText.Text = _activity.Count == 0
            ? "暂无记录。"
            : $"已显示 {_activity.Count} 条，最新的一条在最上方。";

        ActivityEmptyText.Visibility = _activity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearActivityButton.IsEnabled = _activity.Count > 0;
    }

    private void OnClearActivity(object sender, RoutedEventArgs e)
    {
        _activity.Clear();
        UpdateActivityCount();
        Report("已清空本页的活动记录。", StatusSeverity.Informational);
    }

    private async void OnTestAlert(object sender, RoutedEventArgs e)
    {
        // The name is deliberately obvious so a real alert is never mistaken for this one.
        const string fakeApplication = "示例程序（测试用，并非真实使用）";

        var sample = new PrivacyUsageEvent
        {
            Device = PrivacyDevice.Camera,
            Change = PrivacyUsageChange.Started,
            ApplicationId = "SeewoAssistant.TestAlert.exe",
            DisplayName = fakeApplication,
            ObservedAt = DateTimeOffset.Now,
            ProcessIds = [Environment.ProcessId],
        };

        await RunGuardedAsync("发送测试提醒", async () =>
        {
            await Services.PrivacyNotifier.NotifyAsync(sample);

            _activity.Insert(0, new PrivacyActivityRow(sample));
            UpdateActivityCount();

            Report(
                $"已发出测试提醒（程序名：{fakeApplication}）。" +
                (Services.Settings.PrivacyShowToast || Services.Settings.PrivacyShowBanner
                    ? "如果没有看到通知，请检查系统通知设置和「专注助手」。"
                    : "当前系统通知和横幅都已关闭，请在设置中打开后再试。"),
                Services.Settings.PrivacyShowToast || Services.Settings.PrivacyShowBanner
                    ? StatusSeverity.Success
                    : StatusSeverity.Warning);
        });
    }
}

/// <summary>
/// One row of the activity list. Pre-formatted because the monitor's event arrives on
/// a background thread and the bindings must not read live service state.
/// </summary>
public sealed class PrivacyActivityRow
{
    public PrivacyActivityRow(PrivacyUsageEvent usageEvent)
    {
        DeviceLabel = usageEvent.DeviceName;

        var change = usageEvent.Change == PrivacyUsageChange.Started ? "开始使用" : "停止使用";
        ApplicationLabel = $"{usageEvent.BestName} {change}{usageEvent.DeviceName}";

        TimeLabel = usageEvent.ObservedAt.LocalDateTime.ToString("HH:mm:ss");

        ProcessLabel = usageEvent.ProcessIds.Count > 0
            ? $"进程 ID：{string.Join(", ", usageEvent.ProcessIds)}"
            : string.Empty;

        ProcessVisibility = string.IsNullOrEmpty(ProcessLabel)
            ? Visibility.Collapsed
            : Visibility.Visible;

        DetailLabel = usageEvent.IsPackaged
            ? $"来自应用商店：{usageEvent.ApplicationId}"
            : usageEvent.ApplicationId;
    }

    public string DeviceLabel { get; }

    public string ApplicationLabel { get; }

    public string TimeLabel { get; }

    public string ProcessLabel { get; }

    public Visibility ProcessVisibility { get; }

    public string DetailLabel { get; }
}
