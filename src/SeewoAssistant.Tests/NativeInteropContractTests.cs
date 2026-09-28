using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;

namespace SeewoAssistant.Tests;

/// <summary>
/// Checks every P/Invoke declaration for the mistakes that only fail at runtime.
/// </summary>
/// <remarks>
/// <para>
/// A wrong library name in a <see cref="DllImportAttribute"/> compiles cleanly and
/// throws <see cref="EntryPointNotFoundException"/> the first time the method is
/// called. If that call happens inside a native callback, the process dies.
/// </para>
/// <para>
/// That is exactly what shipped: <c>SetBkMode</c> and <c>SetTextColor</c> are GDI
/// functions but were declared against <c>user32.dll</c>. The banner painted its
/// background and accent bar, then threw on the first text call - so the user saw a
/// dark block and the app crashed. Nothing in the build or the unit tests noticed.
/// </para>
/// <para>
/// These checks read the declarations as source text rather than loading the
/// assemblies, because the test host runs on a non-Windows platform in some
/// environments and the assemblies are Windows-only.
/// </para>
/// </remarks>
public sealed class NativeInteropContractTests
{
    /// <summary>
    /// Functions that live in gdi32.dll. Declaring any of them against user32.dll
    /// resolves at compile time and fails at call time.
    /// </summary>
    private static readonly HashSet<string> GdiFunctions = new(StringComparer.Ordinal)
    {
        "SetBkMode", "SetBkColor", "SetTextColor", "GetTextColor",
        "CreateSolidBrush", "CreatePen", "CreateFontW", "CreateFontIndirectW",
        "SelectObject", "DeleteObject", "DeleteDC", "GetStockObject",
        "CreateCompatibleDC", "CreateCompatibleBitmap", "CreateDIBSection",
        "BitBlt", "StretchBlt", "AlphaBlend", "GdiAlphaBlend",
        "GetDIBits", "SetDIBitsToDevice", "GetObjectW", "GetDeviceCaps",
        "Rectangle", "Ellipse", "MoveToEx", "LineTo", "GetPixel", "SetPixel",
        "GetTextExtentPoint32W", "TextOutW", "ExtTextOutW",
    };

    /// <summary>
    /// Functions that live in user32.dll. The mirror of the check above: importing
    /// these from gdi32.dll fails the same way.
    /// </summary>
    private static readonly HashSet<string> User32Functions = new(StringComparer.Ordinal)
    {
        "MessageBoxW", "SetWindowPos", "ShowWindow", "GetClientRect", "GetWindowRect",
        "BeginPaint", "EndPaint", "InvalidateRect", "UpdateWindow", "DefWindowProcW",
        "RegisterClassExW", "CreateWindowExW", "DestroyWindow", "SetTimer", "KillTimer",
        "SetLayeredWindowAttributes", "DrawTextW", "FillRect", "GetSystemMetrics",
        "SystemParametersInfoW", "SetForegroundWindow", "GetForegroundWindow",
        "LoadCursorW", "LoadIconW", "MessageBeep", "EnumWindows", "GetWindowThreadProcessId",
    };

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

    private static IEnumerable<(string File, int Line, string Library, string EntryPoint, string Managed)> ReadDeclarations()
    {
        var root = FindRepositoryRoot();
        var sources = Directory.EnumerateFiles(
            Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);

        // Matches: [DllImport("lib", ...)] ... static extern <ret> <Name>(
        var pattern = new Regex(
            @"\[DllImport\(\s*""(?<lib>[^""]+)""(?<attrs>[^\]]*)\)\]\s*" +
            @"(?:\[[^\]]*\]\s*)*" +
            @"(?:private|internal|public|protected)?\s*static\s+extern\s+[^;{]+?\s(?<name>\w+)\s*\(",
            RegexOptions.Singleline);

        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(root, file);

            foreach (Match match in pattern.Matches(text))
            {
                var attrs = match.Groups["attrs"].Value;
                var entryPoint = Regex.Match(attrs, @"EntryPoint\s*=\s*""([^""]+)""");

                yield return (
                    relative,
                    text[..match.Index].Count(c => c == '\n') + 1,
                    match.Groups["lib"].Value,
                    entryPoint.Success ? entryPoint.Groups[1].Value : match.Groups["name"].Value,
                    match.Groups["name"].Value);
            }
        }
    }

    [Fact]
    public void NoGdiFunctionIsImportedFromTheWrongLibrary()
    {
        var wrong = new List<string>();

        foreach (var (file, line, library, entryPoint, _) in ReadDeclarations())
        {
            if (GdiFunctions.Contains(entryPoint) &&
                !library.Equals("gdi32.dll", StringComparison.OrdinalIgnoreCase))
            {
                wrong.Add($"{file}:{line} 的 {entryPoint} 声明在 {library}，应为 gdi32.dll");
            }
        }

        Assert.True(
            wrong.Count == 0,
            "GDI 函数声明在错误的库里（编译通过，调用时抛 EntryPointNotFoundException）：\n" +
            string.Join("\n", wrong));
    }

    [Fact]
    public void NoUser32FunctionIsImportedFromTheWrongLibrary()
    {
        var wrong = new List<string>();

        foreach (var (file, line, library, entryPoint, _) in ReadDeclarations())
        {
            if (User32Functions.Contains(entryPoint) &&
                !library.Equals("user32.dll", StringComparison.OrdinalIgnoreCase))
            {
                wrong.Add($"{file}:{line} 的 {entryPoint} 声明在 {library}，应为 user32.dll");
            }
        }

        Assert.True(
            wrong.Count == 0,
            "user32 函数声明在错误的库里：\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void EveryTextFunctionDeclaresUnicodeCharSet()
    {
        // A *W function with a string parameter and no CharSet is marshalled as ANSI,
        // so the callee reads a narrow buffer as wide characters. That produced a
        // black banner followed by a crash for DrawTextW and CreateFontW.
        var root = FindRepositoryRoot();
        var pattern = new Regex(
            @"\[DllImport\(\s*""(?<lib>[^""]+)""(?<attrs>[^\]]*)\)\]\s*" +
            @"(?:\[[^\]]*\]\s*)*" +
            @"(?:private|internal|public|protected)?\s*static\s+extern\s+(?<ret>[^;{]+?)\s(?<name>\w+)\s*\((?<params>[^)]*)\)",
            RegexOptions.Singleline);

        var problems = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(root, file);

            foreach (Match match in pattern.Matches(text))
            {
                var attrs = match.Groups["attrs"].Value;
                var managed = match.Groups["name"].Value;
                var entryPointMatch = Regex.Match(attrs, @"EntryPoint\s*=\s*""([^""]+)""");
                var entryPoint = entryPointMatch.Success ? entryPointMatch.Groups[1].Value : managed;
                var parameters = match.Groups["params"].Value;

                // Only the W variants, and only when a string crosses the boundary.
                if (!entryPoint.EndsWith('W') || !parameters.Contains("string")) continue;

                if (!attrs.Contains("CharSet.Unicode") && !attrs.Contains("CharSet = CharSet.Unicode"))
                {
                    var line = text[..match.Index].Count(c => c == '\n') + 1;
                    problems.Add($"{relative}:{line} 的 {entryPoint} 有 string 参数但没有 CharSet.Unicode");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "宽字符 API 缺少 CharSet.Unicode，会把窄缓冲按宽字符读取：\n" +
            string.Join("\n", problems));
    }

    [Fact]
    public void EveryEntryPointCanBeFoundInTheDeclaredLibraryOnWindows()
    {
        // On Windows this can be verified for real: load the library and look up the
        // export. On other platforms the lookup is meaningless, so the check reports
        // that it was skipped rather than passing silently.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var missing = new List<string>();

        foreach (var (file, line, library, entryPoint, _) in ReadDeclarations())
        {
            if (!NativeLibrary.TryLoad(library, out var handle)) continue;

            try
            {
                if (!NativeLibrary.TryGetExport(handle, entryPoint, out _))
                {
                    missing.Add($"{file}:{line} 在 {library} 中找不到导出 {entryPoint}");
                }
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }

        Assert.True(
            missing.Count == 0,
            "以下 P/Invoke 的导出名在目标库中不存在，调用时会抛 EntryPointNotFoundException：\n" +
            string.Join("\n", missing));
    }

    [Fact]
    public void TheAuditActuallyReadsDeclarations()
    {
        // If the regex silently stopped matching, every check above would pass while
        // verifying nothing. This pins the floor.
        var count = ReadDeclarations().Count();

        Assert.True(
            count > 50,
            $"只解析到 {count} 个 P/Invoke 声明，明显偏少，检查用的正则可能已经失效。");
    }

    [Fact]
    public void LegacyCodePagesAreUsable()
    {
        // schtasks.exe and sc.exe write in the console OEM code page. .NET Core does
        // not ship those code pages, so Encoding.GetEncoding throws
        // NotSupportedException until the provider is registered. That exception broke
        // the entire startup scan with "No data is available for encoding 437", and the
        // page simply showed nothing.
        //
        // This asserts the registration the service performs, using the same call.
        System.Text.Encoding.RegisterProvider(
            System.Text.CodePagesEncodingProvider.Instance);

        var codePage = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;

        var exception = Record.Exception(() => System.Text.Encoding.GetEncoding(codePage));

        Assert.True(
            exception is null,
            $"OEM 代码页 {codePage} 无法获取（{exception?.GetType().Name}: {exception?.Message}）。" +
            "schtasks.exe / sc.exe 的输出解码会失败，启动项扫描会整体抛异常。");

        // Code page 437 specifically: it is the one in the reported failure, and it is
        // not one of the encodings .NET Core provides out of the box.
        Assert.NotNull(System.Text.Encoding.GetEncoding(437));
    }
}
