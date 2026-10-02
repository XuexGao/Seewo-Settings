using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SeewoAssistant.Core.Services.CaptureGuard;
using Windows.ApplicationModel.DataTransfer;

namespace SeewoAssistant.Pages;

/// <summary>
/// Environment summary, capability self-check, live log view and diagnostics export.
/// </summary>
public sealed partial class DiagnosticsPage : ModulePageBase
{
    private EventHandler<string>? _logHandler;

    public DiagnosticsPage()
    {
        InitializeComponent();
    }

    protected override void OnServicesReady()
    {
        PopulateEnvironment();
        LoadLog();

        // Stream new lines into the view so the user can watch an operation happen
        // rather than having to press refresh.
        _logHandler = (_, line) => DispatcherQueue.TryEnqueue(() => AppendLogLine(line));
        Services.FileLogger.EntryWritten += _logHandler;
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // The logger is a long-lived service; leaving a handler attached would keep
        // this page alive and duplicate lines on the next visit.
        if (_logHandler is not null)
        {
            Services.FileLogger.EntryWritten -= _logHandler;
            _logHandler = null;
        }
    }

    // ------------------------------------------------------------------ environment

    private void PopulateEnvironment()
    {
        OsVersionText.Text = RuntimeInformation.OSDescription;

        ArchitectureText.Text =
            $"{RuntimeInformation.ProcessArchitecture} 进程 / {RuntimeInformation.OSArchitecture} 系统" +
            (Environment.Is64BitProcess ? "（64 位）" : "（32 位）");

        ElevationText.Text = IsElevated()
            ? "已以管理员身份运行，所有功能可用。"
            : "未以管理员身份运行。跨进程防截屏、防火墙规则、修改计划任务和定时关机会被拒绝。";

        var capability = Services.VirtualCamera.DetectCapability();
        CameraCapabilityText.Text = $"{capability.Summary} — {capability.Detail}";

        var hasPayload = Services.CaptureGuard.IsCrossProcessAvailable;
        PayloadText.Text = hasPayload
            ? "可用。打开开关后就能保护其他程序的窗口。"
            : $"不可用：系统版本过低（需要 Windows 10 2004，内部版本 {CaptureGuardService.ExcludeFromCaptureMinimumBuild} 以上）。";

        SettingsPathText.Text = Services.SettingsStore.FilePath;
        LogPathText.Text = Services.FileLogger.LogFilePath;
    }

    private static bool IsElevated()
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

    // ------------------------------------------------------------------ self-check

    private async void OnRunChecks(object sender, RoutedEventArgs e)
    {
        CheckProgress.IsActive = true;
        CheckResultsPanel.Children.Clear();
        RunChecksButton.IsEnabled = false;

        try
        {
            // Each probe is deliberately isolated so one failing check cannot hide
            // the results of the others.
            await Task.Run(() =>
            {
                AddCheck("Windows 版本 ≥ 10.0.19041", OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041),
                    $"当前 {RuntimeInformation.OSDescription}",
                    "防截屏的 WDA_EXCLUDEFROMCAPTURE 需要此版本。");

                AddCheck("WDA_EXCLUDEFROMCAPTURE 可用", CaptureGuardService.SupportsExcludeFromCapture,
                    CaptureGuardService.SupportsExcludeFromCapture ? "支持穿透隐身" : "将降级为黑块遮蔽",
                    "低于 19041 的系统会把 WDA_EXCLUDEFROMCAPTURE 当作 WDA_MONITOR 处理。");

                var capability = Services.VirtualCamera.DetectCapability();
                AddCheck("虚拟摄像头后端", capability.IsSupported, capability.Summary,
                    capability.Detail);

                AddCheck("虚拟摄像头管理工具", Services.VirtualCamera.IsSetupToolAvailable,
                    Services.VirtualCamera.IsSetupToolAvailable
                        ? Path.Combine(Services.VirtualCamera.ToolsDirectory, "SeewoVirtualCamera.Setup.exe")
                        : "未找到 SeewoVirtualCamera.Setup.exe",
                    "缺少该工具时无法注册媒体源或创建摄像头实例。");

                AddCheck("跨进程防截屏", Services.CaptureGuard.IsCrossProcessAvailable,
                    Services.CaptureGuard.IsCrossProcessAvailable ? "可用" : "系统版本不支持",
                    $"不可用时只能保护这个工具自己的窗口。需要 Windows 10 2004（内部版本 {CaptureGuardService.ExcludeFromCaptureMinimumBuild}）以上。");

                AddCheck("摄像头/麦克风监控", Services.PrivacyMonitor.IsStoreReady,
                    Services.PrivacyMonitor.IsStoreReady ? "已连接到系统记录" : "尚未找到系统记录",
                    "系统要等某个程序第一次用过摄像头或麦克风之后才会留下记录，所以现在空着是正常的。");

                AddCheck("管理员权限", IsElevated(),
                    IsElevated() ? "已提权" : "未提权",
                    "未提权时跨进程防截屏、防火墙规则、计划任务修改和定时关机不可用。");

                AddCheck("SeDebugPrivilege", Core.Services.Seewo.SeewoControlService.HasDebugPrivilege(),
                    Core.Services.Seewo.SeewoControlService.HasDebugPrivilege() ? "已启用" : "不可用",
                    "挂起和结束其他用户的进程需要该特权。");

                AddCheck("关机特权", Core.Services.Power.PowerService.CanShutdown(),
                    Core.Services.Power.PowerService.CanShutdown() ? "已启用" : "不可用",
                    "定时关机需要 SeShutdownPrivilege。");
            });

            Report("自检完成。", StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            Services.Logger.Error("The self-check failed.", ex);
            Report($"自检失败：{ex.Message}", StatusSeverity.Error);
        }
        finally
        {
            CheckProgress.IsActive = false;
            RunChecksButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Adds one check result. Called from a background thread, so it marshals to the
    /// UI thread itself.
    /// </summary>
    private void AddCheck(string name, bool passed, string detail, string explanation)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var row = new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(4),
                Background = (Brush)Application.Current.Resources[
                    passed ? "SystemFillColorSuccessBackgroundBrush" : "SystemFillColorCautionBackgroundBrush"],
            };

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new FontIcon
            {
                Glyph = passed ? "\uE73E" : "\uE7BA",
                FontSize = 16,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });

            if (!passed)
            {
                text.Children.Add(new TextBlock
                {
                    Text = explanation,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(text, 1);
            layout.Children.Add(icon);
            layout.Children.Add(text);

            row.Child = layout;
            CheckResultsPanel.Children.Add(row);
        });
    }

    // ------------------------------------------------------------------ log

    private void LoadLog()
    {
        var entries = Services.FileLogger.RecentEntries;
        LogText.Text = entries.Count == 0
            ? "（暂无日志）"
            : string.Join(Environment.NewLine, entries);

        if (AutoScrollToggle.IsOn)
        {
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, true);
        }
    }

    private void AppendLogLine(string line)
    {
        // Rebuild rather than append: the in-memory ring buffer is the source of
        // truth, so this keeps the view consistent with it after a rollover.
        LogText.Text = string.Join(Environment.NewLine, Services.FileLogger.RecentEntries);

        if (AutoScrollToggle.IsOn)
        {
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, true);
        }
    }

    private void OnRefreshLog(object sender, RoutedEventArgs e)
    {
        LoadLog();
        Report("日志已刷新。");
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        // This clears the view only. The file on disk is the record of what happened
        // and is deliberately not touched.
        LogText.Text = "（显示已清空，磁盘日志保留）";
        Report("已清空日志显示。磁盘上的日志文件不受影响。");
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(Services.FileLogger.LogFilePath);

        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        OpenInExplorer(directory);
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(LogText.Text);
        Report("日志已复制到剪贴板。", StatusSeverity.Success);
    }

    // ------------------------------------------------------------------ export

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = BuildReport();
            var directory = Services.SettingsStore.Directory;

            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(path, report, Encoding.UTF8);

            ExportResultText.Text = $"诊断报告已保存到：{path}";
            ExportResultText.Visibility = Visibility.Visible;

            OpenInExplorer(directory);
            Report("诊断报告已导出。", StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Exporting diagnostics failed.", ex);
            Report($"导出失败：{ex.Message}", StatusSeverity.Error);
        }
    }

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(BuildReport());
        Report("诊断报告已复制到剪贴板。", StatusSeverity.Success);
    }

    private string BuildReport()
    {
        var builder = new StringBuilder();

        builder.AppendLine("SeewoAssistant 诊断报告");
        builder.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine(new string('-', 60));

        builder.AppendLine("[运行环境]");
        builder.AppendLine($"操作系统      : {RuntimeInformation.OSDescription}");
        builder.AppendLine($"系统架构      : {RuntimeInformation.OSArchitecture}");
        builder.AppendLine($"进程架构      : {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"运行时        : {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"管理员权限    : {(IsElevated() ? "是" : "否")}");
        builder.AppendLine($"配置文件      : {Services.SettingsStore.FilePath}");
        builder.AppendLine($"日志文件      : {Services.FileLogger.LogFilePath}");
        builder.AppendLine();

        builder.AppendLine("[模块状态]");

        var capability = Services.VirtualCamera.DetectCapability();
        builder.AppendLine($"虚拟摄像头后端: {capability.Summary}");
        builder.AppendLine($"  支持        : {capability.IsSupported}");
        builder.AppendLine($"  出现在系统设置: {capability.AppearsInWindowsSettings}");
        builder.AppendLine($"  说明        : {capability.Detail}");
        builder.AppendLine($"管理工具      : {(Services.VirtualCamera.IsSetupToolAvailable ? "已找到" : "未找到")}");
        builder.AppendLine($"推流中        : {Services.VirtualCamera.IsPumping}");
        builder.AppendLine($"已发送帧数    : {Services.VirtualCamera.PublishedFrames}");
        builder.AppendLine();

        builder.AppendLine($"隐私监控运行中: {Services.PrivacyMonitor.IsRunning}");
        builder.AppendLine($"系统记录可用  : {Services.PrivacyMonitor.IsStoreReady}");
        builder.AppendLine($"监控摄像头    : {Services.Settings.MonitorCamera}");
        builder.AppendLine($"监控麦克风    : {Services.Settings.MonitorMicrophone}");
        builder.AppendLine();

        builder.AppendLine($"防截屏支持隐身: {CaptureGuardService.SupportsExcludeFromCapture}");
        builder.AppendLine($"跨进程载荷    : {(Services.CaptureGuard.IsCrossProcessAvailable ? "已找到" : "未找到")}");
        builder.AppendLine($"允许跨进程注入: {Services.Settings.CaptureAllowCrossProcess}");
        builder.AppendLine();

        builder.AppendLine($"希沃规则数    : {Services.Settings.SeewoRules.Count}");
        builder.AppendLine($"定时任务数    : {Services.Scheduler.Tasks.Count}");
        builder.AppendLine($"调度器运行中  : {Services.Scheduler.IsRunning}");
        builder.AppendLine();

        builder.AppendLine("[最近日志]");
        builder.AppendLine(new string('-', 60));

        var entries = Services.FileLogger.RecentEntries;
        if (entries.Count == 0)
        {
            builder.AppendLine("（暂无日志）");
        }
        else
        {
            foreach (var entry in entries)
            {
                builder.AppendLine(entry);
            }
        }

        return builder.ToString();
    }

    // ------------------------------------------------------------------ helpers

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
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
