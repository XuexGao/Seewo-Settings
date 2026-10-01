using System.Text.RegularExpressions;
using Xunit;

namespace SeewoAssistant.Tests;

/// <summary>
/// Guards the property that hiding every window can always be undone.
/// </summary>
/// <remarks>
/// <para>
/// The hide operation covers this application's own window too, because the request is
/// "hide everything except system programs" and this is not one. That is only safe
/// because the tray icon remains reachable, and its window is message-only so it is never
/// enumerated.
/// </para>
/// <para>
/// The tray icon can fail to be created - the shell refuses Shell_NotifyIcon in some
/// sessions - and in that case hiding this window as well would remove the only control
/// that can undo the operation, leaving the user to find Task Manager. The service
/// therefore has to be able to keep its own window visible, and the caller has to ask it
/// to when the tray icon is missing. Either half alone is useless, so both are pinned.
/// </para>
/// </remarks>
public sealed class WindowHiderContractTests
{
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src")) &&
                Directory.Exists(Path.Combine(directory.FullName, "native")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录。");
    }

    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));

    [Fact]
    public void TheWindowHiderCanSpareItsOwnWindow()
    {
        var source = ReadSource("src", "SeewoAssistant.Core", "Services", "Desktop",
                                "WindowHiderService.cs");

        Assert.Contains("KeepOwnWindowVisible", source);

        // The check has to be inside the per-window decision, not merely declared.
        var shouldHide = Regex.Match(
            source,
            @"private bool ShouldHide\(nint hWnd\)(?<body>.*?)\n    \}",
            RegexOptions.Singleline);

        Assert.True(shouldHide.Success, "找不到 ShouldHide。");
        Assert.Contains("KeepOwnWindowVisible && processId == _ownProcessId", shouldHide.Groups["body"].Value);

        // It must be an instance method: a static one could not read the property.
        Assert.DoesNotContain("private static bool ShouldHide", source);
    }

    [Fact]
    public void TheWindowIsSparedWhenTheTrayIconIsMissing()
    {
        var source = ReadSource("src", "SeewoAssistant", "MainWindow.xaml.cs");

        // The flag is only set in the else branch of the tray-icon attempt, which is the
        // case where no tray icon exists.
        var tray = Regex.Match(
            source,
            @"_trayIcon = TrayIcon\.TryCreate\(\);(?<body>.*?)ContentFrame\.Navigate",
            RegexOptions.Singleline);

        Assert.True(tray.Success, "找不到托盘图标创建块。");
        Assert.Contains("WindowHider.KeepOwnWindowVisible = true", tray.Groups["body"].Value);
    }
}
