using System.Text.RegularExpressions;
using Xunit;

namespace SeewoAssistant.Tests;

/// <summary>
/// Guards two defects the fourth test report found, and one stale claim found while
/// checking them.
/// </summary>
/// <remarks>
/// <para>
/// V4-01: a window protected in an earlier session could not be released. Protection
/// lives on the window, so it survives this app restarting, but the "取消保护" button was
/// gated on the same flag that gates protecting - cross-process injection being allowed -
/// and that flag is off by default. Both buttons therefore ended up disabled while the
/// page displayed, in its own words, that the window was still protected. The user could
/// only restart the protected program, with nothing explaining why.
/// </para>
/// <para>
/// The stale claim: the cross-process design used to inject a resident payload DLL. It no
/// longer does - a short machine-code stub is written into the target and freed as soon
/// as the call returns - but three UI strings still announced whether
/// <c>SeewoCaptureGuard.Payload.dll</c> had been found, and told users to re-download the
/// package for what was actually an OS-version requirement.
/// </para>
/// </remarks>
public sealed class CaptureGuardUxContractTests
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
    public void AProtectedWindowCanAlwaysBeUnprotected()
    {
        var source = ReadSource("src", "SeewoAssistant", "Pages", "CaptureGuardPage.xaml.cs");

        var method = Regex.Match(
            source,
            @"private void UpdateSelectedWindowState\(\)(?<body>.*?)\n    \}",
            RegexOptions.Singleline);

        Assert.True(method.Success, "找不到 UpdateSelectedWindowState。");

        var body = method.Groups["body"].Value;

        // The button must follow the window's real state. Gating it on `blocked` is the
        // regression: it disabled the control for a window the same method had just
        // described as protected.
        Assert.Contains("UnprotectSelectedButton.IsEnabled = isProtected;", body);
        Assert.DoesNotContain("UnprotectSelectedButton.IsEnabled = !blocked", body);

        // ...and `isProtected` must actually mean "the window is protected".
        Assert.Contains("_selected.Protection != CaptureProtectionState.None", body);
    }

    [Fact]
    public void UnprotectingWithoutPermissionOffersToGrantIt()
    {
        // Enabling the button is only half the fix: the click has to be able to succeed,
        // or the user is moved from a disabled button to a failing one.
        var source = ReadSource("src", "SeewoAssistant", "Pages", "CaptureGuardPage.xaml.cs");

        var method = Regex.Match(
            source,
            @"private async Task ApplyToSelectedAsync\(bool protect\)(?<body>.*?)\n    \}",
            RegexOptions.Singleline);

        Assert.True(method.Success, "找不到 ApplyToSelectedAsync。");

        var body = method.Groups["body"].Value;

        // The already-protected case must be distinguished from the plain "not allowed"
        // case, and must prompt rather than return early.
        Assert.Contains("var alreadyProtected = !protect && _selected.Protection != CaptureProtectionState.None;", body);
        Assert.Contains("await ConfirmAsync(", body);
        Assert.Contains("Services.Settings.CaptureAllowCrossProcess = true;", body);

        // The choice has to be persisted, or the next launch asks again.
        Assert.Contains("Services.SaveSettings();", body);
    }

    [Fact]
    public void NoSourceClaimsAPayloadDllExists()
    {
        // The file is gone from the design, so any mention of it as something to find is
        // wrong - it sent users to re-download a package that was already complete.
        //
        // Only shipped code is scanned. This test necessarily contains the string it
        // searches for, and the test project is not part of what the user sees.
        var root = FindRepositoryRoot();
        var shipped = Path.Combine(root, "src", "SeewoAssistant");
        var core = Path.Combine(root, "src", "SeewoAssistant.Core");
        var offenders = new List<string>();

        foreach (var directory in new[] { shipped, core })
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (extension is not (".cs" or ".xaml"))
                {
                    continue;
                }

                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                if (File.ReadAllText(file).Contains("SeewoCaptureGuard.Payload", StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetFileName(file));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"这些文件仍在提到已经不存在的载荷 DLL：{string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheTitleBarDoesNotDuplicateTheStatusBar()
    {
        // The report saw one sentence at both the top and the bottom of the same page,
        // which makes it unclear which is "the" status, and the top copy also went stale
        // across pages. The bottom bar owns transient messages.
        var source = ReadSource("src", "SeewoAssistant", "MainWindow.xaml.cs");

        var showStatus = Regex.Match(
            source,
            @"private void ShowStatus\(StatusMessage message\)(?<body>.*?)\n    \}",
            RegexOptions.Singleline);

        Assert.True(showStatus.Success, "找不到 ShowStatus。");
        Assert.DoesNotContain("TitleBarStatus.Text = message.Text", showStatus.Groups["body"].Value);

        // The title bar no longer carries any text of its own. It briefly named the
        // current page, which restated the navigation pane's selection highlight; that
        // was removed at the user's request, so the status bar is now the only place a
        // message can appear - and it is cleared on navigation, which is what stops a
        // message from one page being read as belonging to another.
        Assert.DoesNotContain("TitleBarStatus", source);

        var xaml = ReadSource("src", "SeewoAssistant", "MainWindow.xaml");
        Assert.DoesNotContain("TitleBarStatus", xaml);
    }
}
