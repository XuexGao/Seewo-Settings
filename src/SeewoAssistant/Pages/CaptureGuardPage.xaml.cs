using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using SeewoAssistant.Core.Models;
using SeewoAssistant.Core.Services.CaptureGuard;

namespace SeewoAssistant.Pages;

/// <summary>
/// Module 3: hides windows from screen capture, recording and screen sharing.
/// </summary>
/// <remarks>
/// The page is built around one asymmetry: protecting this process's own windows is a
/// plain API call that always works, while protecting another program's window needs an
/// injected payload. Every control therefore reports which scope it used, and the
/// cross-process path is opt-in and never taken implicitly.
/// </remarks>
public sealed partial class CaptureGuardPage : ModulePageBase
{
    /// <summary>How often the radar picker re-reads the window under the cursor.</summary>
    private static readonly TimeSpan RadarInterval = TimeSpan.FromMilliseconds(100);

    private readonly ObservableCollection<WindowInfo> _visibleWindows = [];

    private List<WindowInfo> _allWindows = [];
    private WindowInfo? _selected;
    private WindowInfo? _radarCandidate;
    private DispatcherTimer? _radarTimer;
    private bool _radarActive;
    private bool _suppressSettingWrites;

    public CaptureGuardPage()
    {
        InitializeComponent();
        WindowList.ItemsSource = _visibleWindows;
    }

    // ------------------------------------------------------------------ intro

    protected override string? IntroKey => "capture";

    protected override string? IntroTitle => "防截屏保护";

    // The page used to carry a whole "先知道这个限制" card, two paragraphs under the
    // own-window buttons and one above the window list. All of it explained, none of it
    // was a control, so it moved here and is read once.
    protected override string? IntroBody =>
        """
让选中的窗口不出现在截图、录屏和屏幕共享里。
聊天窗口、密码框、写着身份证号的通知——不想被录进去的都适合。

有个绕不开的限制得先讲清楚：Windows 的接口只肯保护"调用它自己的那个窗口"。
所以保护这个工具自己的窗口永远成功；要保护微信、浏览器这类别的程序，
就得写一小段代码进对方进程，让它自己去调这个接口。这一步就是「跨进程」，
默认关着，需要你手动打开。

于是这一页分成两半：

- 保护这个工具窗口：不用写入别的进程，也不会被杀软拦。想先确认功能有没有用，
  试这个最稳。验证方法是点「保护本程序窗口」，然后按 Win+Shift+S 截图并框选
  本窗口所在的区域——窗口区域会变成空白或黑色；点「取消保护」再截一次就恢复正常。
- 保护其他程序的窗口：属于实验性功能。开了也可能失败：杀软会拦、部分系统进程
  不让写、权限比它高的进程写进去也调不动。

「窗口列表」列出当前可见的顶层窗口，选一行就能对它应用或取消保护。列表是刷新时
的快照，窗口关掉之后那一行就失效了，刷新一下即可。

懒得在列表里翻，可以点「雷达取景」：开启后每秒约 10 次读取鼠标指针下方的窗口并
实时显示，把鼠标移到目标窗口上，点「确定」就选中了。
""";
    protected override void OnServicesReady()
    {
        _suppressSettingWrites = true;

        var mode = Services.Settings.CaptureDefaultMode;
        ModeRadio.SelectedIndex = mode == CaptureProtectionMode.Blackout ? 1 : 0;
        CrossProcessToggle.IsOn = Services.Settings.CaptureAllowCrossProcess;

        _suppressSettingWrites = false;

        UpdateModeDescription();
        UpdateCrossProcessState();
        UpdateOwnWindowState();
        UpdateSelectedWindowState();
        RefreshWindows();

        Report("提示：SetWindowDisplayAffinity 只能作用于这个工具自己的窗口；保护其他程序需要开启跨进程注入。");
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // A running timer would keep polling the cursor and keep this page alive.
        StopRadar();
    }

    // ------------------------------------------------------------------ helpers

    private CaptureProtectionMode SelectedMode =>
        ModeRadio.SelectedIndex == 1 ? CaptureProtectionMode.Blackout : CaptureProtectionMode.ExcludeFromCapture;

    private nint GetOwnWindowHandle()
    {
        var window = App.Current?.MainWindow;

        if (window is null)
        {
            return nint.Zero;
        }

        try
        {
            return WinRT.Interop.WindowNative.GetWindowHandle(window);
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Reading the main window handle failed.", ex);
            return nint.Zero;
        }
    }

    /// <summary>Builds a <see cref="WindowInfo"/> for this app's own main window.</summary>
    private WindowInfo? DescribeOwnWindow()
    {
        var hwnd = GetOwnWindowHandle();

        if (hwnd == nint.Zero)
        {
            return null;
        }

        try
        {
            return Services.CaptureGuard.Describe(hwnd);
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Describing the main window failed.", ex);
            return null;
        }
    }

    /// <summary>Records the outcome of an operation in the result card and the status bar.</summary>
    private void ShowResult(ProtectionResult result)
    {
        var scope = result.Scope == ProtectionScope.OwnProcess ? "本进程" : "跨进程";

        LastResultText.Text =
            $"[{DateTime.Now:HH:mm:ss}] {result.Message}\n" +
            $"作用范围：{scope}；结果状态：{CaptureGuardService.Describe(result.State)}";

        LastResultBadgeText.Text = result.Success ? "成功" : "失败";
        SetBadge(LastResultBadge, LastResultBadgeText, LastResultBadgeText.Text, result.Success);

        if (result.Exception is not null)
        {
            Services.Logger.Error($"Capture protection failed: {result.Message}", result.Exception);
        }

        Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Error);
    }

    // ------------------------------------------------------------------ own window

    private void UpdateOwnWindowState()
    {
        var window = DescribeOwnWindow();

        if (window is null)
        {
            OwnWindowText.Text = "读不到这个工具主窗口的句柄，本机窗口操作不可用。";
            ProtectOwnButton.IsEnabled = false;
            UnprotectOwnButton.IsEnabled = false;
            return;
        }

        OwnWindowText.Text =
            $"句柄：{window.HandleHex}　类名：{window.ClassName}　进程：{window.ProcessName}\n" +
            $"当前保护状态：{CaptureGuardService.Describe(window.Protection)}";

        ProtectOwnButton.IsEnabled = window.Protection != CaptureProtectionState.Excluded;
        UnprotectOwnButton.IsEnabled = window.Protection != CaptureProtectionState.None;
    }

    private void OnProtectOwnWindow(object sender, RoutedEventArgs e)
    {
        var window = DescribeOwnWindow();

        if (window is null)
        {
            Report("读不到这个工具主窗口的句柄，操作已取消。", StatusSeverity.Error);
            return;
        }

        // Own-process scope, so cross-process permission is irrelevant and is passed
        // as false to make that explicit.
        ShowResult(Services.CaptureGuard.Protect(window, SelectedMode, allowCrossProcess: false));
        UpdateOwnWindowState();
    }

    private void OnUnprotectOwnWindow(object sender, RoutedEventArgs e)
    {
        var window = DescribeOwnWindow();

        if (window is null)
        {
            Report("无法读取这个工具主窗口的句柄，操作已取消。", StatusSeverity.Error);
            return;
        }

        ShowResult(Services.CaptureGuard.Unprotect(window, allowCrossProcess: false));
        UpdateOwnWindowState();
    }

    // ------------------------------------------------------------------ mode

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModeDescription();

        if (_suppressSettingWrites)
        {
            return;
        }

        Services.Settings.CaptureDefaultMode = SelectedMode;

        if (Services.SaveSettings())
        {
            Report($"默认保护方式已设为「{CaptureGuardService.Describe(ToState(SelectedMode))}」。");
        }
    }

    private void UpdateModeDescription()
    {
        var mode = SelectedMode;
        var description = CaptureGuardService.Describe(ToState(mode));

        ModeDescriptionText.Text = mode == CaptureProtectionMode.ExcludeFromCapture
            ? $"当前选择：{description}。窗口在截图和录屏里完全不出现，是最干净的效果。"
            : $"当前选择：{description}。窗口位置会留下一个黑色方块，兼容性最好，但对方能看出这里「有东西」。";

        // WDA_EXCLUDEFROMCAPTURE only exists from Windows 10 2004; on older builds the
        // OS silently downgrades it, so saying "no protection" would be wrong.
        ModeInfoBar.Message =
            "当前系统版本低于 Windows 10 2004（内部版本 19041），系统不支持「穿透隐身」，" +
            "选择它时会自动降级为「黑块遮蔽」：截图里仍然看不到内容，但会留下一个黑色方块。";
        ModeInfoBar.Severity = InfoBarSeverity.Warning;
        ModeInfoBar.IsOpen = !CaptureGuardService.SupportsExcludeFromCapture
            && mode == CaptureProtectionMode.ExcludeFromCapture;
    }

    private static CaptureProtectionState ToState(CaptureProtectionMode mode) =>
        mode == CaptureProtectionMode.ExcludeFromCapture
            ? CaptureProtectionState.Excluded
            : CaptureProtectionState.Blackout;

    // ------------------------------------------------------------------ cross-process

    private void UpdateCrossProcessState()
    {
        var allowed = Services.Settings.CaptureAllowCrossProcess;
        var available = Services.CaptureGuard.IsCrossProcessAvailable;

        CrossProcessStateText.Text = allowed
            ? "已允许。保护其他程序的窗口时，会往那个进程里写入一小段机器码桩，由它自己调用系统接口。"
            : "已禁止。这时只能保护这个工具自己的窗口。选中别的程序的窗口时会告诉你为什么不行，不会点了没反应。";

        CrossProcessBadgeText.Text = allowed ? (available ? "已允许" : "系统版本不支持") : "已禁止";
        SetBadge(
            CrossProcessBadge,
            CrossProcessBadgeText,
            CrossProcessBadgeText.Text,
            active: allowed,
            warning: allowed && !available);

        // This used to report whether a payload DLL was present. There is no such file
        // any more - a short machine-code stub is written into the target and freed as
        // soon as the call returns - so the old text named a file that does not exist and
        // told users to re-download the package for a problem that was really an OS
        // version. It now states the actual requirement.
        CrossProcessAvailabilityText.Text = available
            ? "功能可用。保护其他程序的窗口时，会往那个进程里写入一小段机器码桩，由目标进程自己调用系统接口，调用返回后立即释放。"
            : $"功能不可用：系统版本过低（需要 Windows 10 2004，内部版本 {CaptureGuardService.ExcludeFromCaptureMinimumBuild} 以上）。"
              + "这个限制来自系统本身，换发行包也解决不了。";
    }

    private async void OnCrossProcessToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingWrites)
        {
            return;
        }

        if (!CrossProcessToggle.IsOn)
        {
            Services.Settings.CaptureAllowCrossProcess = false;
            PersistCrossProcess("已关闭跨进程注入，现在只能保护这个工具自己的窗口。");
            return;
        }

        var accepted = await ConfirmAsync(
            "开启跨进程注入？",
            "开启后，保护其他程序的窗口时，会往那个程序里写入一小段机器码桩，"
            + "由它自己调用系统的窗口保护接口。先看完这几点：\n\n"
            + "· 杀软和安全软件可能把这次写入当成可疑行为拦下来，甚至弹告警。\n"
            + "· 只对你手动选中的那一个进程生效，不会碰其他程序。\n"
            + "· 保护会一直留在那个窗口上，直到你点「取消保护」，或者那个程序自己重启。\n"
            + "· 桌面、任务栏和关键系统进程不能注入——风险太大，已直接跳过。\n\n"
            + "还要开启吗？",
            "我已了解，开启",
            "取消");

        if (!accepted)
        {
            // Reverting the switch is deliberate: leaving it on after a refusal would
            // make the UI claim a capability the user just declined.
            _suppressSettingWrites = true;
            CrossProcessToggle.IsOn = false;
            _suppressSettingWrites = false;

            Report("已保持关闭跨进程注入。", StatusSeverity.Informational);
            return;
        }

        Services.Settings.CaptureAllowCrossProcess = true;
        Services.Settings.CaptureCrossProcessAcknowledged = true;
        PersistCrossProcess("已开启跨进程注入。保护其他程序的窗口时如果失败，会在这里给出具体原因。");
    }

    private void PersistCrossProcess(string message)
    {
        if (Services.SaveSettings())
        {
            Report(message, StatusSeverity.Success);
        }

        UpdateCrossProcessState();
        UpdateSelectedWindowState();
    }

    // ------------------------------------------------------------------ window picker

    private void OnRefreshWindows(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        Report($"窗口列表已刷新，共找到 {_allWindows.Count} 个窗口。");
    }

    private void RefreshWindows()
    {
        try
        {
            _allWindows = [.. Services.CaptureGuard.EnumerateWindows()];
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Enumerating windows failed.", ex);
            Report($"刷新窗口列表失败：{ex.Message}", StatusSeverity.Error);
            return;
        }

        ApplyWindowFilter(keepSelection: true);
    }

    private void OnWindowFilterChanged(object sender, TextChangedEventArgs e) =>
        ApplyWindowFilter(keepSelection: true);

    private void ApplyWindowFilter(bool keepSelection)
    {
        var filter = WindowFilterBox.Text?.Trim() ?? string.Empty;
        var previousHandle = keepSelection ? _selected?.Handle ?? nint.Zero : nint.Zero;

        var matches = string.IsNullOrEmpty(filter)
            ? _allWindows
            : _allWindows.Where(window =>
                Contains(window.Title, filter) ||
                Contains(window.ProcessName, filter) ||
                Contains(window.ClassName, filter)).ToList();

        _visibleWindows.Clear();

        foreach (var window in matches)
        {
            _visibleWindows.Add(window);
        }

        WindowCountText.Text = string.IsNullOrEmpty(filter)
            ? $"共 {matches.Count} 个窗口"
            : $"匹配 {matches.Count} / {_allWindows.Count} 个窗口";

        if (previousHandle != nint.Zero)
        {
            var restored = _visibleWindows.FirstOrDefault(window => window.Handle == previousHandle);

            if (restored is not null)
            {
                WindowList.SelectedItem = restored;
            }
        }

        // Clearing the collection drops the ListView selection; re-read it here so the
        // action buttons and the field cannot disagree about what is selected.
        _selected = WindowList.SelectedItem as WindowInfo;
        UpdateSelectedWindowState();

        static bool Contains(string value, string filter) =>
            !string.IsNullOrEmpty(value) && value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void OnWindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = WindowList.SelectedItem as WindowInfo;
        UpdateSelectedWindowState();
    }

    private void UpdateSelectedWindowState()
    {
        if (_selected is null)
        {
            SelectedWindowText.Text = "尚未选择窗口。";
            ProtectSelectedButton.IsEnabled = false;
            UnprotectSelectedButton.IsEnabled = false;
            return;
        }

        var ownership = _selected.IsOwnProcess ? "本程序窗口" : "其他程序窗口";

        SelectedWindowText.Text =
            $"已选择：{_selected.Title}\n" +
            $"进程：{_selected.ProcessName}（PID {_selected.ProcessId}）　类名：{_selected.ClassName}　句柄：{_selected.HandleHex}\n" +
            $"归属：{ownership}　当前保护状态：{CaptureGuardService.Describe(_selected.Protection)}";

        // A cross-process target without permission cannot be *protected*: there is no way
        // to reach into that process, so the button is disabled and the reason is stated
        // rather than letting the click fail.
        var blocked = !_selected.IsOwnProcess && !Services.Settings.CaptureAllowCrossProcess;

        // Removing protection is a different case, and gating it on the same flag created
        // a dead end. A window can still be protected from an earlier session - the
        // affinity lives in the window, so it survives this app restarting - and if
        // cross-process injection is off by then, gating on `blocked` left both buttons
        // disabled with no way to release the window. The user could only restart the
        // protected program itself, with nothing on screen explaining why.
        //
        // The button is therefore enabled whenever the window is actually protected, and
        // clicking it offers to turn the permission on instead of refusing. A state the
        // user can see must not be a state they cannot leave.
        var isProtected = _selected.Protection != CaptureProtectionState.None;

        ProtectSelectedButton.IsEnabled = !blocked && _selected.Protection != CaptureProtectionState.Excluded;
        UnprotectSelectedButton.IsEnabled = isProtected;

        PickerInfoBar.Message = isProtected
            ? $"「{_selected.ProcessName}」当前仍处于受保护状态。取消保护同样需要写入那个进程，"
              + "所以要先把上方「跨进程保护（实验性）」的开关打开；点「取消保护」时可以直接开启。"
            : $"「{_selected.ProcessName}」属于其他进程，这个工具没法直接保护它的窗口。"
              + "请先在上方「跨进程保护（实验性）」中开启跨进程注入；开启后仍然可能因为杀毒软件拦截或目标进程权限更高而失败。";
        PickerInfoBar.Severity = InfoBarSeverity.Warning;
        PickerInfoBar.IsOpen = blocked && !isProtected;
    }

    private async void OnProtectSelected(object sender, RoutedEventArgs e) =>
        await ApplyToSelectedAsync(protect: true);

    private async void OnUnprotectSelected(object sender, RoutedEventArgs e) =>
        await ApplyToSelectedAsync(protect: false);

    /// <summary>
    /// Applies or removes protection on the selected window.
    /// </summary>
    /// <remarks>
    /// This runs the work on a background thread. Cross-process protection injects a
    /// DLL and then waits for the payload to answer, with a ten-second budget; doing
    /// that inline froze the window for the whole wait, which the user experienced as
    /// the operation "timing out". The UI now stays responsive and the buttons are
    /// disabled while the work is in flight so it cannot be started twice.
    /// </remarks>
    private async Task ApplyToSelectedAsync(bool protect)
    {
        if (_selected is null)
        {
            Report("请先在窗口列表中选择一个窗口。", StatusSeverity.Warning);
            return;
        }

        var allowCrossProcess = Services.Settings.CaptureAllowCrossProcess;

        if (!_selected.IsOwnProcess && !allowCrossProcess)
        {
            // Releasing a window that is already protected has to be possible, so this is
            // offered rather than refused. Leaving the window stuck is worse than the
            // injection the user already consented to once - and the consent is what the
            // setting records, so turning it back on here does not skip any warning.
            var alreadyProtected = !protect && _selected.Protection != CaptureProtectionState.None;

            if (!alreadyProtected)
            {
                var reason =
                    $"无法保护「{_selected.ProcessName}」：它是其他进程的窗口，而跨进程注入当前处于关闭状态。" +
                    "请在上方「跨进程保护（实验性）」中打开开关后重试。";

                PickerInfoBar.Message = reason;
                PickerInfoBar.Severity = InfoBarSeverity.Warning;
                PickerInfoBar.IsOpen = true;

                Report(reason, StatusSeverity.Warning);
                return;
            }

            var accepted = await ConfirmAsync(
                $"取消对「{_selected.ProcessName}」的保护？",
                "这个窗口现在处于受保护状态，在截图和录屏里完全不出现。要取消它，需要往那个进程里注入"
                + "一小段载荷，让它自己把保护关掉——所以必须先打开跨进程注入。\n\n"
                + "· 取消保护后这个窗口会恢复成正常显示，可以在截图和录屏里重新看到。\n"
                + "· 载荷调用返回后立即释放，不会驻留在对方进程里。\n"
                + "· 上次开启时的风险确认依然有效，不再重复询问。\n\n"
                + "是否现在打开并取消保护？",
                "打开并取消保护",
                "先不取消");

            if (!accepted)
            {
                Report($"已保持「{_selected.ProcessName}」的保护状态。", StatusSeverity.Informational);
                return;
            }

            Services.Settings.CaptureAllowCrossProcess = true;
            _suppressSettingWrites = true;
            CrossProcessToggle.IsOn = true;
            _suppressSettingWrites = false;

            UpdateCrossProcessState();
            allowCrossProcess = true;

            // Persist it, so the next launch starts from the state the user just chose
            // instead of making them answer the same prompt again.
            Services.SaveSettings();
        }

        var target = _selected;

        // Everything the background work needs must be read here, on the UI thread.
        //
        // SelectedMode reads RadioButtons.SelectedIndex, and WinUI 3 enforces UI-thread
        // affinity on XAML controls: touching one from a thread-pool thread throws
        // RPC_E_WRONG_THREAD. Reading it inside the Task.Run below made cross-process
        // protection fail 100% of the time, which is why the feature appeared to be
        // broken regardless of elevation. The value is a plain enum, so capturing it
        // before the switch to the pool is both necessary and sufficient.
        var mode = SelectedMode;

        ProtectSelectedButton.IsEnabled = false;
        UnprotectSelectedButton.IsEnabled = false;
        Report($"正在{(protect ? "保护" : "取消保护")}「{target.ProcessName}」…");

        try
        {
            // Protect/Unprotect block while injecting and waiting for the payload, so
            // they belong off the UI thread. Only plain data crosses the boundary.
            var result = await Task.Run(() => protect
                ? Services.CaptureGuard.Protect(target, mode, allowCrossProcess)
                : Services.CaptureGuard.Unprotect(target, allowCrossProcess));

            ShowResult(result);
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Applying capture protection failed.", ex);
            Report($"{(protect ? "保护" : "取消保护")}窗口失败：{ex.Message}", StatusSeverity.Error);
        }
        finally
        {
            // Re-read the real state from the OS instead of trusting the snapshot we
            // sent, and restore the buttons.
            RefreshWindows();
            UpdateOwnWindowState();
        }
    }

    // ------------------------------------------------------------------ radar picker

    private void OnToggleRadar(object sender, RoutedEventArgs e)
    {
        if (_radarActive)
        {
            StopRadar();
            return;
        }

        StartRadar();
    }

    private void StartRadar()
    {
        _radarTimer ??= new DispatcherTimer { Interval = RadarInterval };
        _radarTimer.Tick -= OnRadarTick;
        _radarTimer.Tick += OnRadarTick;
        _radarTimer.Start();

        _radarActive = true;
        _radarCandidate = null;

        RadarButtonText.Text = "停止取景";
        RadarConfirmButton.IsEnabled = false;
        RadarCancelButton.IsEnabled = true;
        RadarPreviewText.Text = "正在读取鼠标指针下方的窗口…";
        RadarStatusText.Text = "取景进行中：把鼠标移到目标窗口上，预览会实时更新，然后点「确定」。";

        Report("雷达取景已开始，把鼠标移到目标窗口上。");
    }

    private void StopRadar()
    {
        if (_radarTimer is not null)
        {
            _radarTimer.Stop();
            _radarTimer.Tick -= OnRadarTick;
        }

        if (!_radarActive)
        {
            return;
        }

        _radarActive = false;
        _radarCandidate = null;

        RadarButtonText.Text = "雷达取景";
        RadarConfirmButton.IsEnabled = false;
        RadarCancelButton.IsEnabled = false;
        RadarStatusText.Text = "取景已停止。";
        RadarPreviewText.Text = "（未开始取景）";
    }

    private void OnRadarTick(object? sender, object e)
    {
        nint hwnd;

        try
        {
            hwnd = Services.CaptureGuard.GetWindowAtCursor();
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Reading the window under the cursor failed.", ex);
            StopRadar();
            Report($"读取鼠标下的窗口失败：{ex.Message}", StatusSeverity.Error);
            return;
        }

        if (hwnd == nint.Zero)
        {
            _radarCandidate = null;
            RadarConfirmButton.IsEnabled = false;
            RadarPreviewText.Text = "（指针下方没有可识别的窗口）";
            return;
        }

        // Describe on every change of handle only: it opens the owning process, which is
        // far too expensive to do ten times a second for the same window.
        if (_radarCandidate is null || _radarCandidate.Handle != hwnd)
        {
            try
            {
                _radarCandidate = Services.CaptureGuard.Describe(hwnd);
            }
            catch (Exception ex)
            {
                Services.Logger.Debug($"Describing the window under the cursor failed: {ex.Message}");
                _radarCandidate = null;
            }
        }

        if (_radarCandidate is null)
        {
            RadarConfirmButton.IsEnabled = false;
            RadarPreviewText.Text = $"（句柄 0x{hwnd:X} 不是可保护的目标窗口）";
            return;
        }

        var ownership = _radarCandidate.IsOwnProcess ? "本程序窗口" : "其他程序窗口";

        RadarPreviewText.Text =
            $"标题：{_radarCandidate.Title}\n" +
            $"进程：{_radarCandidate.ProcessName}（PID {_radarCandidate.ProcessId}）\n" +
            $"类名：{_radarCandidate.ClassName}　句柄：{_radarCandidate.HandleHex}　尺寸：{_radarCandidate.Width}×{_radarCandidate.Height}\n" +
            $"归属：{ownership}　当前保护状态：{CaptureGuardService.Describe(_radarCandidate.Protection)}";

        RadarConfirmButton.IsEnabled = true;
    }

    private void OnConfirmRadar(object sender, RoutedEventArgs e)
    {
        var candidate = _radarCandidate;

        StopRadar();

        if (candidate is null)
        {
            Report("取景已结束，没有可确认的窗口。", StatusSeverity.Warning);
            return;
        }

        // Prefer the row from the list so the selection highlight matches what the
        // action buttons will use; the radar snapshot is the fallback.
        var row = _visibleWindows.FirstOrDefault(window => window.Handle == candidate.Handle)
            ?? _allWindows.FirstOrDefault(window => window.Handle == candidate.Handle);

        if (row is not null)
        {
            WindowList.SelectedItem = row;
            _selected = row;
        }
        else
        {
            _selected = candidate;
        }

        UpdateSelectedWindowState();
        Report($"已选中「{_selected.Title}」，可以点「保护选中窗口」应用保护。", StatusSeverity.Success);
    }

    private void OnCancelRadar(object sender, RoutedEventArgs e)
    {
        StopRadar();
        Report("已取消雷达取景。", StatusSeverity.Informational);
    }
}

/// <summary>Renders a protection state using the same wording as the service.</summary>
public sealed partial class ProtectionStateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is CaptureProtectionState state ? CaptureGuardService.Describe(state) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Renders window ownership as text for the picker rows.</summary>
public sealed partial class OwnProcessConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool isOwnProcess
            ? (isOwnProcess ? "本程序窗口" : "其他程序窗口")
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
