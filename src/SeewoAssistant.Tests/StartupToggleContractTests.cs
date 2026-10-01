using System.Text.RegularExpressions;
using Xunit;

namespace SeewoAssistant.Tests;

/// <summary>
/// Guards the rule that stops a list refresh from being mistaken for a user action.
/// </summary>
/// <remarks>
/// <para>
/// A two-way bound <c>ToggleSwitch</c> raises <c>Toggled</c> when the binding sets
/// <c>IsOn</c>, not only when the user clicks it. WinUI builds list row containers
/// lazily, so that assignment happens after the page has finished updating its
/// collection - which means a flag set and cleared synchronously around the update is
/// already back to false by the time the handler runs.
/// </para>
/// <para>
/// The consequence was measured on a real classroom machine: pressing
/// 「扫描开机自启项」 silently re-enabled every disabled Seewo startup entry, changing seven
/// HKLM Run values and more than ten scheduled tasks.
/// </para>
/// <para>
/// The fix is that the view model keeps its own shown value instead of writing straight
/// through to the entry, and the handler ignores a toggle that matches the entry's stored
/// state. These tests pin both halves, because either one alone would reintroduce the bug:
/// a pass-through property makes the two states inseparable, and dropping the comparison
/// makes every refresh write to the machine.
/// </para>
/// </remarks>
public sealed class StartupToggleContractTests
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

    private static string ReadPage() =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SeewoAssistant", "Pages", "SeewoPage.xaml.cs"));

    [Fact]
    public void TheStartupViewModelsShownStateIsNotAPassThrough()
    {
        var source = ReadPage();

        // Locate the StartupItem class body.
        var match = Regex.Match(
            source,
            @"private sealed class StartupItem\s*\{(?<body>.*?)\n    \}",
            RegexOptions.Singleline);

        Assert.True(match.Success, "找不到 StartupItem 类。");

        var body = match.Groups["body"].Value;

        // A pass-through getter would read Entry.Enabled, which makes the shown state and
        // the stored state indistinguishable and defeats the comparison.
        var enabled = Regex.Match(
            body,
            @"public bool Enabled\s*\{(?<accessors>.*?)\}",
            RegexOptions.Singleline);

        Assert.True(enabled.Success, "StartupItem 上找不到 Enabled 属性。");

        Assert.DoesNotContain("Entry.Enabled", enabled.Groups["accessors"].Value);

        // It must still be initialised from the entry, or the list would show every row as
        // off when it opens.
        Assert.Contains("Enabled = entry.Enabled", body);
    }

    [Fact]
    public void TheToggleHandlerComparesAgainstTheStoredEntryState()
    {
        var source = ReadPage();

        var handler = Regex.Match(
            source,
            @"private async void OnStartupToggled\(.*?\n    \}",
            RegexOptions.Singleline);

        Assert.True(handler.Success, "找不到 OnStartupToggled。");

        var body = handler.Value;

        // The guard must be present and must compare the switch's own value against the
        // entry, since that comparison is the entire defence.
        Assert.Contains("toggle.IsOn", body);
        Assert.Contains("item.Entry.Enabled", body);
        Assert.Contains("if (target == item.Entry.Enabled)", body);

        // The switch must be read rather than the bound property, so the decision does not
        // depend on whether the binding has written back yet.
        Assert.DoesNotContain("var target = item.Enabled;", body);
    }

    [Fact]
    public void NoTimingDependentSuppressionFlagRemains()
    {
        // The original approach used a boolean set and cleared around the collection
        // update. It cannot work, because the event it was meant to suppress arrives
        // later. If one reappears, the bug is back.
        var source = ReadPage();

        Assert.DoesNotContain("_suppressStartupToggle", source);
        Assert.DoesNotContain("_suppressRuleToggle", source);
    }

    [Fact]
    public void TheRulesListUsesTheSameComparison()
    {
        var source = ReadPage();

        var handler = Regex.Match(
            source,
            @"private void OnRuleToggled\(.*?\n    \}",
            RegexOptions.Singleline);

        Assert.True(handler.Success, "找不到 OnRuleToggled。");

        // The rules list has the same two-way binding and the same hazard.
        Assert.Contains("item.Enabled == item.Rule.Enabled", handler.Value);
    }
}
