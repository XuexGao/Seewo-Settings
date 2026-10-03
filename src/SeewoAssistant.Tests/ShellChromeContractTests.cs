using System.Text.RegularExpressions;
using SeewoAssistant.Core.Configuration;
using Xunit;

namespace SeewoAssistant.Tests;

/// <summary>
/// Covers the fifth round of interface changes: the title bar no longer duplicates the
/// navigation pane, and the theme moved into the settings page.
/// </summary>
public sealed class ShellChromeContractTests
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
    public void TheTitleBarHasNoDuplicateSettingsOrThemeButtons()
    {
        // Both were removed: the settings page is already one click away in the
        // navigation pane, and the window buttons are drawn over that corner in full
        // screen, which is what made them unreachable.
        var xaml = ReadSource("src", "SeewoAssistant", "MainWindow.xaml");

        Assert.DoesNotContain("x:Name=\"SettingsButton\"", xaml);
        Assert.DoesNotContain("x:Name=\"ThemeButton\"", xaml);
        Assert.DoesNotContain("x:Name=\"ThemeIcon\"", xaml);

        // The handlers must be gone too, or the XAML no longer compiles against them.
        var code = ReadSource("src", "SeewoAssistant", "MainWindow.xaml.cs");
        Assert.DoesNotContain("OnOpenSettings", code);
        Assert.DoesNotContain("OnToggleTheme", code);
    }

    [Fact]
    public void TheThemeIsSelectableOnTheSettingsPage()
    {
        var xaml = ReadSource("src", "SeewoAssistant", "Pages", "SettingsPage.xaml");

        Assert.Contains("x:Name=\"ThemeCombo\"", xaml);

        // The dead boolean switch must be gone, not merely hidden: it saved a value
        // nothing ever read.
        Assert.DoesNotContain("FollowSystemThemeToggle", xaml);
    }

    [Fact]
    public void TheChosenThemeIsAppliedAndSurvivesARestart()
    {
        var window = ReadSource("src", "SeewoAssistant", "MainWindow.xaml.cs");

        // Applied at startup from the stored value...
        Assert.Contains("ApplyTheme(_services.Settings.Theme);", window);

        // ...and the settings page pushes a change through the same path. Without one of
        // these the picker would be as inert as the switch it replaced.
        var settings = ReadSource("src", "SeewoAssistant", "Pages", "SettingsPage.xaml.cs");
        Assert.Contains("Services.ApplyTheme();", settings);
        Assert.Contains("Services.SaveSettings();", settings);
    }

    [Fact]
    public void FollowingTheSystemIsExpressible()
    {
        // The old title-bar button could only alternate light and dark, so once forced
        // there was no way back to following Windows. Three states are required.
        var settings = ReadSource("src", "SeewoAssistant", "Pages", "SettingsPage.xaml.cs");

        Assert.Contains("AppTheme.System", settings);
        Assert.Contains("AppTheme.Light", settings);
        Assert.Contains("AppTheme.Dark", settings);

        // ElementTheme.Default is what "follow the system" means in WinUI; freezing the
        // currently resolved theme would stop the app reacting to a later system change.
        var window = ReadSource("src", "SeewoAssistant", "MainWindow.xaml.cs");
        Assert.Contains("ElementTheme.Default", window);
    }
    [Fact]
    public void NoStandaloneLabelHasStrayWhitespace()
    {
        // Reported three times as a defect: the binary string table contains "将挂起 "
        // with a trailing space, read as a button label. It is not one. That space is
        // inside a sentence built by string interpolation -
        //     $"将挂起 {selected.Count} 个进程："
        // - and the compiler emits the literal part before the hole as its own
        // constant, so a string scanner sees a fragment ending in a space. The space
        // separates a Chinese word from a following numeral, which is correct
        // typography, and the real button label is the third argument of that call.
        //
        // This asserts what was actually meant. Two kinds of text are excluded, both
        // because a space at their edge is correct rather than stray:
        //   * <Run> children, which are pieces of one composed sentence. "PID " and
        //     " · " are joined with their neighbours, so trimming them would run the
        //     words together.
        //   * values containing a binding or a format placeholder, which are templates
        //     rather than text the user reads verbatim.
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src", "SeewoAssistant"), "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(
                         text,
                         @"<(?<tag>\w+)(?<attrs>[^>]*?)(?:Content|Text|Header|PlaceholderText)=""(?<value>[^""]*)""[^>]*>"))
            {
                // Inline runs are sentence fragments, not controls with a label.
                if (match.Groups["tag"].Value == "Run")
                {
                    continue;
                }

                var value = match.Groups["value"].Value;

                if (value.Length == 0 || value == value.Trim())
                {
                    continue;
                }

                // A template, not a literal the user reads.
                if (value.Contains('{'))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(file)}: [{value}]");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"这些控件文案带有首尾空格：{string.Join(" | ", offenders)}");
}

/// <summary>
/// Covers the retirement of the <c>followSystemTheme</c> boolean.
/// </summary>
/// <remarks>
/// The property was deleted rather than kept alongside the new enum, which means an
/// existing settings file would silently lose the user's choice: <c>followSystemTheme</c>
/// would simply be ignored by the deserialiser and the default (follow the system) would
/// win. Anyone who had turned it off would be reset without being told.
/// </remarks>
public sealed class LegacyThemeMigrationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "seewo-theme-tests", Guid.NewGuid().ToString("N"));

    public LegacyThemeMigrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A failed cleanup must not fail the test run.
        }
    }

    private AppSettings LoadWith(string json)
    {
        var store = new SettingsStore(_directory);
        File.WriteAllText(store.FilePath, json);
        return store.Load();
    }

    [Fact]
    public void ALegacyFalseBecomesAlwaysLight()
    {
        // The old switch meant "do not follow the system". Reading it as Light is the
        // only mapping that honours what the user picked.
        var settings = LoadWith("""{ "version": 1, "followSystemTheme": false }""");

        Assert.Equal(AppTheme.Light, settings.Theme);
        Assert.True(settings.ThemeMigratedFromLegacyKey);
    }

    [Fact]
    public void ALegacyTrueIsLeftFollowingTheSystem()
    {
        // true already meant "follow the system", which is the new default, so there is
        // nothing to carry over and no note to show.
        var settings = LoadWith("""{ "version": 1, "followSystemTheme": true }""");

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.False(settings.ThemeMigratedFromLegacyKey);
    }

    [Fact]
    public void ANewFileIsUnaffected()
    {
        var settings = LoadWith("""{ "version": 1, "theme": "Dark" }""");

        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.False(settings.ThemeMigratedFromLegacyKey);
    }

    [Fact]
    public void AnExplicitNewValueBeatsTheLegacyKey()
    {
        // Only a legacy `false` is acted on, and an explicit theme in the same file is
        // the more recent statement of intent. Without this guard a file written by a
        // build in between the two schemas would be forced back to light forever.
        var settings = LoadWith("""{ "version": 1, "theme": "Dark", "followSystemTheme": false }""");

        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public void AnEmptyOrMissingFileUsesTheDefaults()
    {
        Assert.Equal(AppTheme.System, new SettingsStore(_directory).Load().Theme);
    }

    }
}
