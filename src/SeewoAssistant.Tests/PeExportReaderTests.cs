using SeewoAssistant.Core.Interop;
using SeewoAssistant.Core.Services.CaptureGuard;
using Xunit;

namespace SeewoAssistant.Tests;

/// <summary>
/// Exercises the PE export-table parser against real system libraries.
/// </summary>
/// <remarks>
/// <para>
/// This parser had an off-by-four in the <c>IMAGE_EXPORT_DIRECTORY</c> prefix: it skipped
/// 24 bytes where the structure has 20. Every field from <c>NumberOfFunctions</c> onwards
/// was therefore read four bytes late, <c>numberOfNames</c> picked up
/// <c>AddressOfFunctions</c> - an RVA in the hundreds of thousands - and the plausibility
/// guard rejected it. The parser returned 0 for every module on every machine.
/// </para>
/// <para>
/// The consequence was that cross-process capture protection could never work, because it
/// resolves <c>SetWindowDisplayAffinity</c> through this parser to compute the address to
/// call inside the target process. The failure was reported to the user as an OS version
/// problem, which sent the investigation in the wrong direction.
/// </para>
/// <para>
/// A field-offset error is pure arithmetic, so a test against a real library catches it
/// immediately and costs nothing. These tests only run on Windows, where system libraries
/// with export tables exist.
/// </para>
/// </remarks>
public sealed class PeExportReaderTests
{
    private static string System32Path(string fileName) =>
        Path.Combine(Environment.SystemDirectory, fileName);

    [Fact]
    public void ResolvesAKnownExportFromUser32()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = System32Path("user32.dll");
        Assert.True(File.Exists(path), $"找不到 {path}。");

        var rva = PeExportReader.GetExportRva(path, "SetWindowDisplayAffinity");

        // This is the exact lookup cross-process capture protection performs. It returned
        // 0 on every machine before the prefix size was corrected.
        Assert.True(
            rva > 0,
            "无法从 user32.dll 解析 SetWindowDisplayAffinity 的导出。"
            + "这通常意味着 IMAGE_EXPORT_DIRECTORY 的字段偏移又算错了。");
    }

    [Fact]
    public void ResolvesAKnownExportFromKernel32()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = System32Path("kernel32.dll");
        Assert.True(File.Exists(path), $"找不到 {path}。");

        // LoadLibraryW is what cross-bitness injection resolves through the same parser,
        // so it is the second path that depends on this arithmetic being right.
        var rva = PeExportReader.GetExportRva(path, "LoadLibraryW");

        Assert.True(rva > 0, "无法从 kernel32.dll 解析 LoadLibraryW 的导出。");
    }

    [Fact]
    public void TheResolvedRvaPointsAtTheRealFunction()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The strongest check available: the parsed RVA, added to the module's load
        // address, must equal the address GetProcAddress returns. That closes the loop
        // end to end rather than merely asserting a non-zero result.
        var path = System32Path("user32.dll");

        var module = NativeMethods.GetModuleHandleW("user32.dll");

        if (module == nint.Zero)
        {
            // user32 is always loaded by a GUI process; if it is not, this check cannot
            // run and the other two still cover the arithmetic.
            return;
        }

        var expected = NativeMethods.GetProcAddress(module, "SetWindowDisplayAffinity");

        if (expected == nint.Zero)
        {
            return;
        }

        var rva = PeExportReader.GetExportRva(path, "SetWindowDisplayAffinity");

        Assert.Equal(expected, module + rva);
    }

    [Fact]
    public void ReturnsZeroForANameThatDoesNotExist()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A missing export must return 0 rather than a bogus address, since callers treat
        // 0 as "not found". A parser that guessed would be far more dangerous here than
        // one that gives up.
        var rva = PeExportReader.GetExportRva(
            System32Path("user32.dll"),
            "ThisExportNameDoesNotExistAnywhere12345");

        Assert.Equal(0, rva);
    }

    [Fact]
    public void ReturnsZeroForAMissingFile()
    {
        var rva = PeExportReader.GetExportRva(
            Path.Combine(Path.GetTempPath(), "definitely-not-a-real-file-98765.dll"),
            "Anything");

        Assert.Equal(0, rva);
    }
}
