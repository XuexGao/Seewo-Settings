using System.Text.RegularExpressions;
using Xunit;

namespace SeewoAssistant.Tests;

/// <summary>
/// Covers the move of each page's permanent explanation into a one-time dialog.
/// </summary>
/// <remarks>
/// <para>
/// The explanations used to sit at the top of every page permanently, so reaching the
/// controls meant scrolling past a paragraph that had already been read. They are now
/// shown once, on first open.
/// </para>
/// <para>
/// Two properties have to hold for that to be an improvement rather than a loss. The
/// explanation must still exist (it cannot simply have been deleted), and it must be
/// recoverable after being dismissed — otherwise a user who clicks past it once has lost
/// the only description of the feature.
/// </para>
/// </remarks>
public sealed class PageIntroContractTests
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

    private static IEnumerable<(string Name, string Text)> Pages()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "src", "SeewoAssistant", "Pages");

        foreach (var file in Directory.EnumerateFiles(directory, "*Page.xaml.cs").OrderBy(f => f))
        {
            yield return (Path.GetFileNameWithoutExtension(file), File.ReadAllText(file));
        }
    }

    [Fact]
    public void EveryUserFacingPageHasAOneTimeIntro()
    {
        var missing = new List<string>();

        foreach (var (name, text) in Pages())
        {
            // Settings deliberately has one too: it explains what the page is for.
            if (!text.Contains("override string? IntroKey", StringComparison.Ordinal))
            {
                missing.Add(name);
            }
        }

        Assert.True(missing.Count == 0, $"这些页面没有一次性说明：{string.Join(", ", missing)}");
    }

    [Fact]
    public void IntroKeysAreUniqueAndNonEmpty()
    {
        var keys = new List<string>();

        foreach (var (name, text) in Pages())
        {
            var match = Regex.Match(text, @"override string\? IntroKey =>\s*""(?<key>[^""]*)""");

            Assert.True(match.Success, $"{name} 的 IntroKey 不是字符串字面量。");

            var key = match.Groups["key"].Value;
            Assert.False(string.IsNullOrWhiteSpace(key), $"{name} 的 IntroKey 是空的。");

            keys.Add(key);
        }

        // A duplicate key would make one page's intro suppress another's: the first page
        // visited would record the key and the second would never show anything.
        var duplicates = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, $"IntroKey 重复：{string.Join(", ", duplicates)}");
    }

    [Fact]
    public void EveryIntroHasATitleAndBody()
    {
        foreach (var (name, text) in Pages())
        {
            Assert.True(
                text.Contains("override string? IntroTitle =>", StringComparison.Ordinal),
                $"{name} 没有 IntroTitle。");

            // The body is a raw string literal, so it is checked by the presence of the
            // override and a non-trivial amount of text after it.
            var body = Regex.Match(text, @"override string\? IntroBody =>\s*""""""(?<body>.*?)""""""", RegexOptions.Singleline);

            Assert.True(body.Success, $"{name} 的 IntroBody 不是原始字符串字面量。");
            Assert.True(body.Groups["body"].Value.Trim().Length > 40, $"{name} 的说明太短，可能没写完。");
        }
    }

    [Fact]
    public void AnIntroIsRecordedBeforeItIsShown()
    {
        var basePage = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SeewoAssistant", "Pages", "ModulePageBase.cs"));

        var method = Regex.Match(
            basePage,
            @"private async Task ShowIntroOnceAsync\(\)(?<body>.*?)\n    \}",
            RegexOptions.Singleline);

        Assert.True(method.Success, "找不到 ShowIntroOnceAsync。");

        var body = method.Groups["body"].Value;

        // Recorded first, then awaited. If it were the other way round, a dialog that
        // failed to show would leave the key unrecorded and the intro would reappear on
        // every single visit - an un-dismissable prompt.
        var recordAt = body.IndexOf("SeenPageIntros.Add", StringComparison.Ordinal);
        var showAt = body.IndexOf("await ShowIntroAsync", StringComparison.Ordinal);

        Assert.True(recordAt >= 0, "ShowIntroOnceAsync 没有记录已读。");
        Assert.True(showAt >= 0, "ShowIntroOnceAsync 没有显示说明。");
        Assert.True(recordAt < showAt, "必须先记录再显示，否则说明会反复弹出。");
    }

    [Fact]
    public void AnIntroWaitsForThePageToBeLoaded()
    {
        // A ContentDialog needs a XamlRoot. During OnNavigatedTo the page is not in the
        // visual tree yet, so showing it there throws - and because the key is recorded
        // first, the explanation would be lost silently rather than retried.
        var basePage = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SeewoAssistant", "Pages", "ModulePageBase.cs"));

        var method = Regex.Match(
            basePage,
            @"private void TryShowIntro\(\)(?<body>.*?)\n    \}",
            RegexOptions.Singleline);

        Assert.True(method.Success, "找不到 TryShowIntro。");
        Assert.Contains("Loaded +=", method.Groups["body"].Value);
    }

    [Fact]
    public void DismissingAnIntroIsNotPermanent()
    {
        var basePage = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SeewoAssistant", "Pages", "ModulePageBase.cs"));

        // The dialog itself offers a way back...
        Assert.Contains("SeenPageIntros.Remove", basePage);

        // ...and the settings page can reset every page at once.
        var settings = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SeewoAssistant", "Pages", "SettingsPage.xaml.cs"));

        Assert.Contains("SeenPageIntros.Clear()", settings);
    }

    [Fact]
    public void TheIntroCloseButtonCannotCollideWithAPageButton()
    {
        var basePage = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SeewoAssistant", "Pages", "ModulePageBase.cs"));

        var match = Regex.Match(basePage, @"CloseButtonText = ""(?<text>[^""]+)""");
        Assert.True(match.Success, "找不到说明对话框的关闭按钮文案。");

        var closeText = match.Groups["text"].Value;

        // The automated smoke test dismisses these dialogs by button name. Several pages
        // have real buttons labelled 取消 / 确定 that the sweep is explicitly told not to
        // click, because they rewrite host configuration. If the intro reused one of
        // those labels, the dismissal helper would press exactly the wrong control.
        Assert.DoesNotContain(closeText, new[] { "取消", "确定", "关闭" });
    }
}
