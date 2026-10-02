using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SeewoAssistant.Core.Services;
using SeewoAssistant.Core.Services.VirtualCamera;
using Windows.Storage.Pickers;

namespace SeewoAssistant.Pages;

/// <summary>
/// Module 1: registers and drives the virtual camera.
/// </summary>
public sealed partial class VirtualCameraPage : ModulePageBase
{
    private bool _suppressSettingWrites;

    public VirtualCameraPage()
    {
        InitializeComponent();
    }

    // ------------------------------------------------------------------ intro

    protected override string? IntroKey => "camera";

    protected override string? IntroTitle => "虚拟摄像头";

    protected override string? IntroBody =>
        """
给系统装一个"假的"摄像头，名字就叫「SeewoAssistant Virtual Camera」。
装好以后，微信、钉钉、腾讯会议、OBS 这些软件的摄像头下拉框里就能选到它，
选上之后对方看到的画面由这个程序决定，而不是你真实的摄像头。

具体怎么用，看这一页的「选择分辨率」和「选择颜色 / 图片」两块：挑好画面，
点「开始推流」，再去别的软件里选这个摄像头就行。

两个前提说在前面：Windows 10 和 Windows 11 走的是两套完全不同的实现，
装之前先看上面那行「系统支持情况」；装完第一次用可能需要重启一下目标软件，
它才会重新读取设备列表。
""";

    protected override void OnServicesReady()
    {
        _suppressSettingWrites = true;

        PopulateResolutions();
        PopulateFrameRates();

        var settings = Services.Settings;

        SelectComboValue(ResolutionCombo, $"{settings.VirtualCameraWidth}×{settings.VirtualCameraHeight}");
        SelectComboValue(FpsCombo, $"{settings.VirtualCameraFps} fps");

        AutoStartToggle.IsOn = settings.VirtualCameraAutoStart;
        ColorTextBox.Text = settings.VirtualCameraDefaultColor;
        ImagePathTextBox.Text = settings.VirtualCameraDefaultImage;

        _suppressSettingWrites = false;

        UpdateColorPreview();
        UpdateBackend();
        UpdateRegistrationState();
        UpdatePumpStatus();

        ColorTextBox.TextChanged += (_, _) => UpdateColorPreview();
    }

    private void PopulateResolutions()
    {
        // Ordered from most useful to least, so the common choice is first.
        foreach (var resolution in new[] { "1280×720", "1920×1080", "640×480", "320×240" })
        {
            ResolutionCombo.Items.Add(resolution);
        }
    }

    private void PopulateFrameRates()
    {
        foreach (var fps in new[] { "30 fps", "15 fps", "60 fps" })
        {
            FpsCombo.Items.Add(fps);
        }
    }

    private static void SelectComboValue(ComboBox combo, string value)
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

    // ------------------------------------------------------------------ capability

    private void UpdateBackend()
    {
        var capability = Services.VirtualCamera.DetectCapability();

        BackendSummaryText.Text = capability.Summary;
        BackendBadgeText.Text = capability.IsSupported
            ? (capability.AppearsInWindowsSettings ? "原生支持" : "兼容模式")
            : "不支持";

        SetBadge(
            BackendBadge,
            BackendBadgeText,
            BackendBadgeText.Text,
            capability.IsSupported,
            warning: capability.IsSupported && !capability.AppearsInWindowsSettings);

        BackendInfoBar.Message = capability.Detail;
        BackendInfoBar.Severity = capability.IsSupported
            ? (capability.AppearsInWindowsSettings ? InfoBarSeverity.Success : InfoBarSeverity.Warning)
            : InfoBarSeverity.Error;
        BackendInfoBar.IsOpen = true;

        // Install and uninstall are always offered when the backend is usable. The
        // Media Foundation backend needs the setup tool, but the DirectShow backend
        // registers its filter with regsvr32 and does not - gating everything on the
        // tool made the button dead on Windows 10 even though registration would have
        // worked.
        var hasTool = Services.VirtualCamera.IsSetupToolAvailable;
        var isMediaFoundation = capability.Backend == VirtualCameraBackend.MediaFoundation;

        // One source of truth for the button rules.
        SetSetupButtonsEnabled(true);

        if (isMediaFoundation && !hasTool)
        {
            SetupOutputText.Text =
                $"未找到 SeewoVirtualCamera.Setup.exe（查找目录：{Services.VirtualCamera.ToolsDirectory}）。\n" +
                "请使用完整发行包，或运行 scripts\\Install-Native.ps1 安装原生组件。";
            SetupOutputText.Visibility = Visibility.Visible;
        }
    }

    // ------------------------------------------------------------------ registration state

    /// <summary>
    /// Shows where the native components are actually registered, and offers to point them
    /// back at this folder when they are not.
    /// </summary>
    /// <remarks>
    /// The CLSID is machine-wide, so this is the only place a second or portable copy can be
    /// noticed at all: installing one re-points the registration at the new folder and the
    /// older copy simply stops working, with nothing said. Reading the state needs no
    /// elevation, so the answer is available before the user commits to anything.
    /// </remarks>
    private void UpdateRegistrationState()
    {
        var state = Services.SystemIntegration.GetState();
        var component = FindBackendComponent(state);

        RegistrationStateText.Text = string.Join("\n", state.Components.Select(c => c.Describe()));

        // Only the component this machine's backend uses decides whether anything is wrong:
        // the install script registers one backend, so the other one legitimately reads
        // "未注册" on every machine.
        var needsRegistration = component is null ||
            component.Status != NativeRegistrationStatus.RegisteredToCurrentFolder;

        ReregisterButton.Visibility = needsRegistration ? Visibility.Visible : Visibility.Collapsed;
        ReregisterButton.IsEnabled = needsRegistration;
    }

    /// <summary>Picks the component the detected backend actually uses.</summary>
    private NativeComponentState? FindBackendComponent(SystemIntegrationState state)
    {
        var expected = Services.VirtualCamera.DetectCapability().Backend == VirtualCameraBackend.MediaFoundation
            ? SystemIntegrationService.MediaSourceClsid
            : SystemIntegrationService.DirectShowFilterClsid;

        return state.Components.FirstOrDefault(
            c => string.Equals(c.Clsid, expected, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdatePumpStatus()
    {
        var pumping = Services.VirtualCamera.IsPumping;

        PumpStatusText.Text = pumping
            ? $"正在推流，已发送 {Services.VirtualCamera.PublishedFrames} 帧（{Services.VirtualCamera.FrameWidth}×{Services.VirtualCamera.FrameHeight} @ {Services.VirtualCamera.FramesPerSecond}fps）"
            : "未在推流。摄像头会输出内置的动态测试画面。";

        PumpBadgeText.Text = pumping ? "推流中" : "已停止";
        SetBadge(PumpBadge, PumpBadgeText, PumpBadgeText.Text, pumping);

        TestPatternButton.IsEnabled = !pumping;
        StopButton.IsEnabled = pumping;
    }

    // ------------------------------------------------------------------ setup

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        var state = Services.SystemIntegration.GetState();
        var component = FindBackendComponent(state);

        var needsRegistration = component is null ||
            component.Status != NativeRegistrationStatus.RegisteredToCurrentFolder;

        // Registering writes HKLM, which an unelevated process cannot do. The report found the
        // button simply failing here; the elevated script is the supported path instead.
        var useElevatedScript = needsRegistration && !state.IsElevated;

        var message = "这会把媒体源组件注册到系统（需要管理员权限），并创建一个摄像头实例。\n\n";

        if (useElevatedScript)
        {
            message += "程序当前不是以管理员身份运行，接下来会弹出系统提权提示。\n\n";
        }
        else if (!needsRegistration)
        {
            message += "组件已经注册到当前目录，这一步只会创建摄像头实例。\n\n";
        }

        message += "是否继续？";

        if (!await ConfirmAsync("安装虚拟摄像头", message, "开始安装"))
        {
            return;
        }

        await RunSetupAsync("正在安装虚拟摄像头…", async () =>
        {
            if (needsRegistration)
            {
                if (useElevatedScript)
                {
                    var script = await Services.SystemIntegration.RunElevatedAsync(NativeScriptAction.Install);
                    AppendOutput(script.Message);

                    if (!script.Success)
                    {
                        if (script.UserCancelled)
                        {
                            AppendOutput("也可以手动以管理员身份运行 scripts\\Install-Native.ps1。");
                        }

                        Report(
                            script.Message,
                            script.UserCancelled ? StatusSeverity.Warning : StatusSeverity.Error);
                        return;
                    }
                }
                else
                {
                    var register = await Services.VirtualCamera.RegisterAsync();
                    AppendOutput(register.Message);

                    if (!register.Success)
                    {
                        Report("注册失败。请尝试以管理员身份重新运行本程序。", StatusSeverity.Error);
                        return;
                    }
                }
            }

            // The script's install already creates the camera; running create again is
            // harmless and keeps both paths producing the same result, including when the
            // registration was already in place.
            var create = await Services.VirtualCamera.CreateCameraAsync();
            AppendOutput(create.Message);

            Report(
                create.Success ? "虚拟摄像头已安装并创建。" : "组件已注册，但创建摄像头失败。",
                create.Success ? StatusSeverity.Success : StatusSeverity.Warning);
        });
    }

    private async void OnReregister(object sender, RoutedEventArgs e)
    {
        var state = Services.SystemIntegration.GetState();

        if (!await ConfirmAsync(
                "重新注册到当前目录",
                "这会把虚拟摄像头组件重新注册到本程序当前所在的目录：\n\n"
                + Services.SystemIntegration.AppDirectory + "\n\n"
                + (state.IsElevated
                    ? "当前已经以管理员身份运行。"
                    : "需要管理员权限，接下来会弹出系统提权提示。")
                + "\n\n是否继续？",
                "重新注册"))
        {
            return;
        }

        await RunSetupAsync("正在重新注册…", async () =>
        {
            // The script also updates the HKLM install record, which the in-process path
            // cannot do; the in-process path only covers a development tree without scripts\.
            if (File.Exists(Services.SystemIntegration.NativeScriptPath))
            {
                var script = await Services.SystemIntegration.RunElevatedAsync(NativeScriptAction.Install);
                AppendOutput(script.Message);
                Report(script.Message, script.UserCancelled ? StatusSeverity.Warning : StatusSeverity.Error);
                return;
            }

            var result = await Services.VirtualCamera.RegisterAsync();
            AppendOutput(result.Message);
            Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Error);
        });
    }

    private async void OnCreate(object sender, RoutedEventArgs e) =>
        await RunSetupAsync("正在创建摄像头实例…", async () =>
        {
            var result = await Services.VirtualCamera.CreateCameraAsync();
            AppendOutput(result.Message);
            Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Error);
        });

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "移除摄像头",
                "这会把虚拟摄像头从系统中移除，正在使用它的应用会失去画面。媒体源组件会保留。",
                "移除"))
        {
            return;
        }

        await RunSetupAsync("正在移除摄像头…", async () =>
        {
            var result = await Services.VirtualCamera.RemoveCameraAsync();
            AppendOutput(result.Message);
            Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Error);
        });
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        var state = Services.SystemIntegration.GetState();

        if (!await ConfirmAsync(
                "完全卸载",
                "这会从系统注销本程序注册的虚拟摄像头组件（删除 HKLM 里的 COM 注册），并移除摄像头实例。\n\n"
                + (state.IsElevated
                    ? "当前已经以管理员身份运行。"
                    : "注册写在 HKLM，需要管理员权限，接下来会弹出系统提权提示。")
                + "\n\n卸载后需要重新安装才能再次使用虚拟摄像头。",
                "完全卸载"))
        {
            return;
        }

        await RunSetupAsync("正在卸载…", async () =>
        {
            if (!state.IsElevated && File.Exists(Services.SystemIntegration.NativeScriptPath))
            {
                // The in-process unregister needs administrator rights just as much as the
                // registration does, so an unelevated app has to go through the script.
                var script = await Services.SystemIntegration.RunElevatedAsync(NativeScriptAction.Uninstall);
                AppendOutput(script.Message);
                Report(script.Message, script.UserCancelled ? StatusSeverity.Warning : StatusSeverity.Error);
                return;
            }

            var result = await Services.VirtualCamera.UnregisterAsync();
            AppendOutput(result.Message);
            Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Error);
        });
    }

    private async void OnList(object sender, RoutedEventArgs e) =>
        await RunSetupAsync("正在枚举系统设备…", async () =>
        {
            var result = await Services.VirtualCamera.ListCamerasAsync();
            AppendOutput(result.Message);
            Report(result.Message.Split('\n')[0], StatusSeverity.Informational);
        });

    private async Task RunSetupAsync(string progressMessage, Func<Task> operation)
    {
        SetupProgress.IsActive = true;
        SetSetupButtonsEnabled(false);
        Report(progressMessage);

        try
        {
            await operation();
        }
        finally
        {
            SetupProgress.IsActive = false;
            SetSetupButtonsEnabled(true);
            UpdateRegistrationState();
            UpdatePumpStatus();
        }
    }

    /// <summary>
    /// Applies the enable/disable state for the setup buttons.
    /// </summary>
    /// <remarks>
    /// The rules live here alone. They were previously written out twice, and the second
    /// copy still used the old "needs the setup tool" rule - so the backend-aware gating
    /// was silently undone as soon as any button was pressed.
    /// </remarks>
    private void SetSetupButtonsEnabled(bool enabled)
    {
        var capability = Services.VirtualCamera.DetectCapability();
        var hasTool = Services.VirtualCamera.IsSetupToolAvailable;
        var isMediaFoundation = capability.Backend == VirtualCameraBackend.MediaFoundation;

        // Both backends can be installed and removed, but only Media Foundation needs
        // the native tool to do it.
        var canManage = enabled && capability.IsSupported && (!isMediaFoundation || hasTool);

        InstallButton.IsEnabled = canManage;
        UninstallButton.IsEnabled = canManage;

        // A camera instance is a Media Foundation concept only.
        var canManageInstance = canManage && isMediaFoundation;

        CreateButton.IsEnabled = canManageInstance;
        RemoveButton.IsEnabled = canManageInstance;
        ListButton.IsEnabled = canManageInstance;
    }

    private void AppendOutput(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var existing = SetupOutputText.Text;
        SetupOutputText.Text = string.IsNullOrEmpty(existing) ? message : existing + "\n" + message;
        SetupOutputText.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ frame pushing

    private void OnPresetColor(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            ColorTextBox.Text = color;
        }
    }

    private void UpdateColorPreview()
    {
        var (a, r, g, b) = FrameSourceFactory.ParseColor(ColorTextBox.Text);

        ColorPreview.Fill = new SolidColorBrush(
            Windows.UI.Color.FromArgb(a, r, g, b));
    }

    private void OnPushColor(object sender, RoutedEventArgs e)
    {
        var color = ColorTextBox.Text;

        try
        {
            Services.VirtualCamera.PushSolidColor(color);

            // Remember it so the tray toggle and auto-start use the same colour.
            Services.Settings.VirtualCameraDefaultColor = color;
            Services.Settings.VirtualCameraDefaultImage = string.Empty;

            UpdatePumpStatus();
            Report($"已推送纯色画面 {color}。", StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Pushing a solid colour frame failed.", ex);
            Report($"推送失败：{ex.Message}", StatusSeverity.Error);
        }
    }

    private async void OnBrowseImage(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();

            // An unpackaged app must associate the picker with a window handle, or
            // the call throws.
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;

            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" })
            {
                picker.FileTypeFilter.Add(extension);
            }

            var file = await picker.PickSingleFileAsync();

            if (file is not null)
            {
                ImagePathTextBox.Text = file.Path;
            }
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Opening the image picker failed.", ex);
            Report($"无法打开文件选择器：{ex.Message}", StatusSeverity.Error);
        }
    }

    private void OnPushImage(object sender, RoutedEventArgs e)
    {
        var path = ImagePathTextBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(path))
        {
            Report("请先选择一张图片。", StatusSeverity.Warning);
            return;
        }

        var result = Services.VirtualCamera.PushImage(path);

        if (result.Success)
        {
            Services.Settings.VirtualCameraDefaultImage = path;
            UpdatePumpStatus();
            Report(result.Message, StatusSeverity.Success);
        }
        else
        {
            Report(result.Message, StatusSeverity.Error);
        }
    }

    private void OnStartTestPattern(object sender, RoutedEventArgs e)
    {
        Services.VirtualCamera.StartTestPatternPump();
        Services.Settings.VirtualCameraDefaultImage = string.Empty;

        UpdatePumpStatus();
        Report("已开始推送动态测试画面。摄像头输出中会出现彩条和移动扫描条。", StatusSeverity.Success);
    }

    private void OnStopPushing(object sender, RoutedEventArgs e)
    {
        Services.VirtualCamera.StopPump();
        UpdatePumpStatus();
        Report("已停止推送。摄像头会回退到内置测试画面。", StatusSeverity.Informational);
    }

    // ------------------------------------------------------------------ output settings

    private void OnResolutionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingWrites || ResolutionCombo.SelectedItem is not string value)
        {
            return;
        }

        var parts = value.Split('×');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var width) ||
            !int.TryParse(parts[1], out var height))
        {
            return;
        }

        Services.VirtualCamera.FrameWidth = width;
        Services.VirtualCamera.FrameHeight = height;
        Services.Settings.VirtualCameraWidth = width;
        Services.Settings.VirtualCameraHeight = height;

        UpdatePumpStatus();
        Report($"分辨率已设为 {width}×{height}。", StatusSeverity.Informational);
    }

    private void OnFpsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingWrites || FpsCombo.SelectedItem is not string value)
        {
            return;
        }

        if (!int.TryParse(value.Split(' ')[0], out var fps))
        {
            return;
        }

        Services.VirtualCamera.FramesPerSecond = fps;
        Services.Settings.VirtualCameraFps = fps;

        UpdatePumpStatus();
        Report($"帧率已设为 {fps}fps。", StatusSeverity.Informational);
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingWrites)
        {
            return;
        }

        Services.Settings.VirtualCameraAutoStart = AutoStartToggle.IsOn;
        Report(AutoStartToggle.IsOn ? "已开启随程序自动推送。" : "已关闭随程序自动推送。");
    }
}
