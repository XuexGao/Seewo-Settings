using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SeewoAssistant.Core.Configuration;
using SeewoAssistant.Pages;
using SeewoAssistant.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace SeewoAssistant;

/// <summary>
/// The shell window: title bar, side navigation, content frame and status bar.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly TrayIcon? _trayIcon;

    private AppWindow? _appWindow;
    private DispatcherTimer? _statusTimer;
    private bool _exitRequested;
    private bool _lastPrivacyRunning;
    private bool _lastCameraPumping;
    private bool _lastStoreReady;

    public MainWindow(AppServices services)
    {
        _services = services;

        InitializeComponent();

        Title = "希沃助手";
        ConfigureWindow();

        // The status bar subscribes first so nothing reported during startup is lost.
        _services.StatusReported += OnStatusReported;
        _services.PrivacyMonitor.UsageDetected += OnPrivacyUsageDetected;

        _trayIcon = TrayIcon.TryCreate();
        if (_trayIcon is not null)
        {
            _trayIcon.OpenRequested += (_, _) => RestoreFromTray();
            _trayIcon.ExitRequested += (_, _) => ExitApplication();
            _trayIcon.TogglePrivacyRequested += (_, _) => TogglePrivacyMonitor();
            _trayIcon.ToggleCameraRequested += (_, _) => ToggleVirtualCamera();

            // Hiding takes this window away too, so the tray menu is the only place the
            // user can always reach to undo it. Restoring from here also brings the
            // window back, which is what someone who just hid everything expects.
            _trayIcon.HideWindowsRequested += OnTrayHideWindowsRequested;

            UpdateTrayTooltip();
        }
        else
        {
            _services.Logger.Warn("The tray icon could not be created; the app will close instead of minimising.");
        }

        // Navigate to the first module.
        ContentFrame.Navigate(typeof(VirtualCameraPage), _services);

        Closed += OnClosed;
        UpdateElevationBadge();
        UpdateStatusBar();

        // The services that the footer reports on are started by
        // App.OnLaunched *after* this window is constructed, so a single
        // UpdateStatusBar() here would leave the footer showing "已停止" for the
        // whole session. A slow timer keeps it truthful without any of the services
        // needing to raise a change event.
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) =>
        {
            UpdateStatusBar();
            UpdateTrayTooltip();
        };
        _statusTimer.Start();
    }

    private void ConfigureWindow()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        if (_appWindow is null)
        {
            return;
        }

        // Extend the content into the title bar so the custom bar in the XAML is
        // what the user sees, and drag regions work as expected.
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            SetTitleBar(AppTitleBar);
        }
        else
        {
            // Without title-bar customisation the XAML title bar is redundant.
            AppTitleBar.Visibility = Visibility.Collapsed;
        }

        // A fixed 1180x820 is larger than the usable area on a 1024x768 display or a
        // small virtual desktop, which pushes the window partly off-screen and makes
        // its own bottom edge unreachable. Clamp to the work area instead, leaving a
        // small margin so the window still reads as a window.
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(1180, Math.Max(720, workArea.Width - 80));
        var height = Math.Min(820, Math.Max(520, workArea.Height - 80));

        _appWindow.Resize(new SizeInt32(width, height));

        // Mica gives the window the Windows 11 depth effect. On Windows 10 the
        // backdrop is unavailable, so a plain solid brush is used instead; the app
        // must look deliberate on both, not broken on one.
        TryApplyBackdrop();
    }

    private void TryApplyBackdrop()
    {
        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            }
            else if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
            }
            else
            {
                // Windows 10: fall back to a solid themed background so the window is
                // still fully readable.
                RootGrid.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Warn($"Applying the window backdrop failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ navigation

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "camera" => typeof(VirtualCameraPage),
            "privacy" => typeof(PrivacyPage),
            "capture" => typeof(CaptureGuardPage),
            "seewo" => typeof(SeewoPage),
            "schedule" => typeof(SchedulePage),
            "diagnostics" => typeof(DiagnosticsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(VirtualCameraPage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            // Clear any message left by the page being left. It described an action taken
            // there, so showing it over a different page is misleading.
            ClearStatusFromOtherPages(tag);

            ContentFrame.Navigate(pageType, _services);
        }

        // Attribute anything the incoming page reports to that page.
        _services.CurrentPage = tag;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        Nav.SelectedItem = Nav.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (i.Tag as string) == "settings");
    }

    // ------------------------------------------------------------------ theme

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        // Cycling through three states rather than toggling two keeps the "follow
        // the system" option reachable from the button.
        var current = RootGrid.ActualTheme;
        var next = current == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;

        if (ContentFrame.Content is FrameworkElement page)
        {
            page.RequestedTheme = next;
        }

        RootGrid.RequestedTheme = next;
        ThemeIcon.Glyph = next == ElementTheme.Dark ? "\uE708" : "\uE706";

        _services.Report(next == ElementTheme.Dark ? "已切换到深色主题。" : "已切换到浅色主题。");
    }

    // ------------------------------------------------------------------ tray

    /// <summary>Restores the window from the tray or from a minimised state.</summary>
    public void RestoreFromTray()
    {
        if (_appWindow is null)
        {
            return;
        }

        // Two distinct cases reach here: the window was hidden to the tray, or the
        // user minimised it with the taskbar button. Show() alone does not restore a
        // minimised window, so the presenter is restored first when needed.
        if (_appWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        _appWindow.Show();
        Activate();
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var privacy = _services.PrivacyMonitor.IsRunning ? "监控中" : "已停止";
        var camera = _services.VirtualCamera.IsPumping ? "推流中" : "已停止";
        _trayIcon.UpdateTooltip($"希沃助手 — 隐私监控：{privacy}，虚拟摄像头：{camera}");
    }

    private void TogglePrivacyMonitor()
    {
        if (_services.PrivacyMonitor.IsRunning)
        {
            _services.PrivacyMonitor.Stop();
            _services.Report("已停止摄像头/麦克风监控。", StatusSeverity.Informational);
        }
        else
        {
            _services.PrivacyMonitor.Start();
            _services.Report("已开启摄像头/麦克风监控。", StatusSeverity.Success);
        }

        UpdateTrayTooltip();
        UpdateStatusBar();
    }

    /// <summary>
    /// Hides or restores desktop windows from the tray menu.
    /// </summary>
    /// <param name="hide">True to hide, false to restore.</param>
    /// <remarks>
    /// The work runs off the UI thread because it enumerates and touches every
    /// top-level window. When restoring, this window is brought back as well - the user
    /// asked to undo the hide, and leaving the app invisible would make the tray menu
    /// the only way to reach anything.
    /// </remarks>
    private async void OnTrayHideWindowsRequested(object? sender, bool hide)
    {
        try
        {
            if (hide)
            {
                var hidden = await Task.Run(() => _services.WindowHider.HideAll());

                _services.Report(
                    hidden == 0
                        ? "没有找到可以隐藏的窗口。"
                        : $"已隐藏 {hidden} 个窗口。右键托盘图标可以恢复。",
                    StatusSeverity.Informational);
            }
            else
            {
                var restored = await Task.Run(() => _services.WindowHider.Restore());

                _services.Report(
                    restored == 0 ? "没有需要恢复的窗口。" : $"已恢复 {restored} 个窗口。",
                    StatusSeverity.Informational);

                // Make the app itself visible again, since hiding took it away.
                RestoreFromTray();
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Toggling desktop window visibility from the tray failed.", ex);
            _services.Report($"操作窗口失败：{ex.Message}", StatusSeverity.Error);
        }
    }

    private void ToggleVirtualCamera()
    {
        if (_services.VirtualCamera.IsPumping)
        {
            _services.VirtualCamera.StopPump();
            _services.Report("已停止虚拟摄像头推流。", StatusSeverity.Informational);
        }
        else
        {
            _services.VirtualCamera.PushSolidColor(_services.Settings.VirtualCameraDefaultColor);
            _services.Report(
                $"已推送纯色画面 {_services.Settings.VirtualCameraDefaultColor}。",
                StatusSeverity.Success);
        }

        UpdateTrayTooltip();
        UpdateStatusBar();
    }

    // ------------------------------------------------------------------ status

    private void OnStatusReported(object? sender, StatusMessage message)
    {
        // Reports arrive from background threads, so they must be marshalled.
        DispatcherQueue.TryEnqueue(() => ShowStatus(message));
    }

    /// <summary>The page whose message is currently displayed, if any.</summary>
    private string? _statusSourcePage;

    /// <summary>
    /// Clears the status bar when the message came from a different page.
    /// </summary>
    /// <param name="incomingPage">The page being navigated to.</param>
    private void ClearStatusFromOtherPages(string incomingPage)
    {
        if (_statusSourcePage is null ||
            string.Equals(_statusSourcePage, incomingPage, StringComparison.Ordinal))
        {
            return;
        }

        _statusSourcePage = null;
        StatusText.Text = string.Empty;
        StatusIcon.Glyph = "\uE946";

        // The title bar carries the same message and was being left behind. The report
        // saw「已切换到新建任务。填写名称、定时表达式和动作序列后保存。」still showing on the
        // diagnostics and settings pages, describing something the user had done
        // somewhere else entirely.
        TitleBarStatus.Text = string.Empty;
    }

    private void ShowStatus(StatusMessage message)
    {
        // Remember where the message came from so a later navigation can drop it.
        _statusSourcePage = message.SourcePage;

        StatusText.Text = message.Text;

        StatusIcon.Glyph = message.Severity switch
        {
            StatusSeverity.Success => "\uE73E",
            StatusSeverity.Warning => "\uE7BA",
            StatusSeverity.Error => "\uEA39",
            _ => "\uE946",
        };

        TitleBarStatus.Text = message.Text;
    }

    private void OnPrivacyUsageDetected(object? sender, Core.Models.PrivacyUsageEvent usageEvent)
    {
        // Only a start is worth surfacing in the status bar.
        if (usageEvent.Change != Core.Models.PrivacyUsageChange.Started)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ShowStatus(new StatusMessage(
                $"检测到 {usageEvent.BestName} 正在使用{usageEvent.DeviceName}",
                StatusSeverity.Warning));

            UpdateTrayTooltip();
        });
    }

    private void UpdateStatusBar()
    {
        var privacyRunning = _services.PrivacyMonitor.IsRunning;
        var storeReady = _services.PrivacyMonitor.IsStoreReady;
        var cameraPumping = _services.VirtualCamera.IsPumping;

        // Only touch the UI when something changed. Re-assigning identical strings
        // every tick would be wasted work and would fight text selection.
        if (privacyRunning != _lastPrivacyRunning || storeReady != _lastStoreReady)
        {
            _lastPrivacyRunning = privacyRunning;
            _lastStoreReady = storeReady;

            PrivacyStatusText.Text = privacyRunning
                ? (storeReady ? "隐私监控：运行中" : "隐私监控：等待系统记录")
                : "隐私监控：已停止";
        }

        if (cameraPumping != _lastCameraPumping)
        {
            _lastCameraPumping = cameraPumping;
            CameraStatusText.Text = "虚拟摄像头：推流中";
        }

        // The published-frame count changes constantly while pumping, so it is
        // updated on every tick but only when there is something to show.
        if (cameraPumping)
        {
            CameraStatusText.Text = $"虚拟摄像头：推流中（{_services.VirtualCamera.PublishedFrames} 帧）";
        }
    }

    private void UpdateElevationBadge()
    {
        // Several features need elevation. Telling the user up front is far better
        // than letting each of them fail individually with an access-denied error.
        var isElevated = IsRunningElevated();

        if (isElevated)
        {
            ElevationBadge.Visibility = Visibility.Collapsed;
            return;
        }

        ElevationBadgeText.Text = "未以管理员身份运行";
        ToolTipService.SetToolTip(
            ElevationBadge,
            "跨进程防截屏、防火墙规则、修改计划任务和定时关机需要管理员权限。" +
            "虚拟摄像头、隐私监控和本程序窗口的防截屏不受影响。");
        ElevationBadge.Visibility = Visibility.Visible;
    }

    private static bool IsRunningElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ lifetime

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_exitRequested)
        {
            return;
        }

        // Closing the window should not kill a background privacy monitor unless the
        // user asked for that. Cancel the close and hide the window.
        //
        // This previously called OverlappedPresenter.Minimize(), which is not what
        // "hide to the tray" means: the window stayed in the taskbar and Alt+Tab as a
        // minimised window, so the user could still see it and could not tell that the
        // app had moved to the tray. AppWindow.Hide() is the operation that removes it
        // from both, leaving only the tray icon.
        if (_services.Settings.CloseBehavior == CloseBehavior.MinimizeToTray && _trayIcon is not null)
        {
            args.Handled = true;

            _appWindow?.Hide();

            _services.Report(
                "希沃助手已隐藏到托盘，后台服务继续运行。单击托盘图标或右键选择「打开」可恢复窗口。",
                StatusSeverity.Informational);

            return;
        }

        ExitApplication();
    }

    private void ExitApplication()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;

        _statusTimer?.Stop();
        _statusTimer = null;

        _services.StatusReported -= OnStatusReported;
        _services.PrivacyMonitor.UsageDetected -= OnPrivacyUsageDetected;

        _trayIcon?.Dispose();

        // Disposal resumes suspended processes, unloads injected payloads, stops the
        // frame pump and saves settings. It must run before the process exits, so it
        // is awaited on a background task and the process is ended when it finishes.
        var shutdown = Task.Run(async () => await _services.DisposeAsync());

        shutdown.ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                Close();
                Environment.Exit(0);
            });
        }, TaskScheduler.Default);
    }
}
