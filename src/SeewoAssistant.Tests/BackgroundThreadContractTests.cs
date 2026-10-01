using System.Text.RegularExpressions;
using Xunit;

namespace SeewoAssistant.Tests;

/// <summary>
/// Checks that UI controls are never touched from background threads.
/// </summary>
/// <remarks>
/// <para>
/// WinUI 3 enforces UI-thread affinity on XAML controls: reading one from a thread-pool
/// thread throws <c>RPC_E_WRONG_THREAD</c>. This compiled cleanly and passed every test,
/// and it made cross-process capture protection fail 100% of the time - while own-window
/// protection, which takes a different code path, kept working and made the feature look
/// healthy.
/// </para>
/// <para>
/// The check is a source-level one on purpose. A running UI test cannot reliably reach
/// the failing path (the button is disabled until a window is selected and cross-process
/// mode is enabled), and this failure mode is a static property of the code, so reading
/// the source is both simpler and deterministic.
/// </para>
/// </remarks>
public sealed class BackgroundThreadContractTests
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

    /// <summary>
    /// Names that are known to be UI controls or UI-bound members, by suffix.
    /// </summary>
    private static readonly string[] ControlSuffixes =
    [
        "Button", "Toggle", "Box", "Combo", "Radio", "Badge", "Bar",
        "List", "View", "Panel", "Grid", "Info", "Progress", "Check",
        "Slider", "Picker", "Switch", "Block", "Item", "Icon", "Ring",
        "Flyout", "Dialog", "Menu",
    ];

    /// <summary>
    /// Types whose members are never XAML controls, so a match on them is a false
    /// positive rather than a defect.
    /// </summary>
    private static readonly string[] IgnoredPrefixes =
    [
        "Task", "Service", "Services", "System", "Interop", "Console", "Path",
        "File", "Directory", "Process", "Registry", "Encoding", "Marshal",
        "Native", "Environment", "App", "Math", "String", "Enumerable",
        "TimeSpan", "DateTime", "CancellationToken", "Action", "Func", "Object",
        "Convert", "Enum", "Guid", "Debug", "Trace", "Json", "Regex", "Char",
        "Exception", "Argument", "InvalidOperation", "Null", "KeyValue",
        "Memory", "Buffer", "Span", "Array", "Comparer", "Culture", "Thread",
    ];

    /// <summary>
    /// Members that read a XAML control and are therefore UI-thread-only.
    /// </summary>
    private static readonly string[] UiBoundMembers =
    [
        "SelectedMode", "SelectedIndex", "SelectedItem", "SelectedValue", "IsOn",
        "IsChecked", "Text", "Value", "Visibility", "Content", "ItemsSource",
        "SelectedItems", "Header", "Tag",
    ];

    /// <summary>
    /// Finds properties and methods on a page that read a XAML control.
    /// </summary>
    /// <remarks>
    /// This indirection is the whole point. The defect that shipped referenced
    /// <c>SelectedMode</c>, not a control: that property reads
    /// <c>ModeRadio.SelectedIndex</c>, so scanning the background lambda for control
    /// names alone misses it entirely. Anything reachable from a background thread that
    /// touches a control is a defect, however many hops away it is.
    /// </remarks>
    private static HashSet<string> FindUiBoundMembers(string text)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);

        // Expression-bodied property: `private X Name => SomeControl...;`
        foreach (Match m in Regex.Matches(
            text,
            @"(?:private|internal|public|protected)[\w\s<>\?\[\]]*?\s(\w+)\s*=>\s*([^;]+);"))
        {
            if (Regex.IsMatch(m.Groups[2].Value, ControlPattern))
            {
                members.Add(m.Groups[1].Value);
            }
        }

        // Block-bodied property whose getter reads a control.
        foreach (Match m in Regex.Matches(
            text,
            @"(?:private|internal|public|protected)[\w\s<>\?\[\]]*?\s(\w+)\s*\{([^}]*get[^}]*)\}",
            RegexOptions.Singleline))
        {
            if (Regex.IsMatch(m.Groups[2].Value, ControlPattern))
            {
                members.Add(m.Groups[1].Value);
            }
        }

        return members;
    }

    /// <summary>Matches a reference to something whose name looks like a XAML control.</summary>
    private static string ControlPattern =>
        @"\b(?:[A-Z]\w*(?:" + string.Join("|", ControlSuffixes) + @"))\.\w+";

    [Fact]
    public void NoUiControlIsReadFromInsideTaskRun()
    {
        var root = FindRepositoryRoot();
        var problems = new List<string>();

        var files = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var lines = text.Split('\n');

            // Members of this file that themselves read a control.
            var uiBound = FindUiBoundMembers(text);

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("Task.Run", StringComparison.Ordinal))
                {
                    continue;
                }

                var body = ExtractLambdaBody(lines, i);

                if (body is null)
                {
                    continue;
                }

                // A control reached directly.
                var offenders = new List<string>();

                foreach (Match match in Regex.Matches(body, ControlPattern))
                {
                    var owner = match.Value.Split('.')[0];

                    if (IgnoredPrefixes.Any(p => owner.StartsWith(p, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    offenders.Add(match.Value);
                }

                // A member that reads a control, reached by name.
                foreach (var member in uiBound)
                {
                    if (Regex.IsMatch(body, $@"\b{Regex.Escape(member)}\b"))
                    {
                        offenders.Add($"{member}（该成员读取了 UI 控件）");
                    }
                }

                foreach (var offender in offenders.Distinct())
                {
                    var lineNumber = i + 1;
                    problems.Add(
                        $"{Path.GetRelativePath(root, file)}:{lineNumber} 在 Task.Run 内访问了 UI：{offender}");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "后台线程访问了 UI，会在运行时抛 RPC_E_WRONG_THREAD：\n" +
            string.Join("\n", problems.Distinct()));
    }

    /// <summary>
    /// Returns the text of a Task.Run statement starting at <paramref name="startLine"/>.
    /// </summary>
    /// <remarks>
    /// The statement ends at the first semicolon seen with the parentheses balanced.
    /// Waiting only for the depth to reach zero stops early on a multi-line conditional
    /// lambda, whose first argument list closes before the statement does - which would
    /// silently skip the operands on the later lines.
    /// </remarks>
    private static string? ExtractLambdaBody(string[] lines, int startLine)
    {
        var builder = new System.Text.StringBuilder();
        var depth = 0;

        for (var i = startLine; i < lines.Length && i < startLine + 30; i++)
        {
            builder.AppendLine(lines[i]);

            // Ignore braces and string contents; only parentheses matter for balance.
            foreach (var c in lines[i])
            {
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                }
            }

            // The statement is complete once it terminates and the nesting is closed.
            if (depth <= 0 && lines[i].Contains(';'))
            {
                return builder.ToString();
            }
        }

        // Unbalanced: return what was collected so the caller still inspects it.
        return builder.Length > 0 ? builder.ToString() : null;
    }

    [Fact]
    public void TheUiAffinityCheckActuallyReadsCode()
    {
        // If the scan silently stopped matching, the test above would pass while checking
        // nothing at all. This pins a floor on how much it inspects.
        var root = FindRepositoryRoot();
        var count = 0;

        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            count += Regex.Matches(File.ReadAllText(file), @"Task\.Run").Count;
        }

        Assert.True(count > 5, $"只找到 {count} 处 Task.Run，扫描逻辑可能已经失效。");
    }
}
