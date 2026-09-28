using System.Text.RegularExpressions;
using SeewoAssistant.Core.Services.CaptureGuard;
using SeewoAssistant.Core.Services.VirtualCamera;
using Xunit;

namespace SeewoAssistant.Tests;

/// <summary>
/// Asserts that the managed and native sides agree on the shared-memory channel
/// names.
/// </summary>
/// <remarks>
/// <para>
/// These two sides are a matched pair that nothing else checks. A mismatch does not
/// fail to compile and does not throw at runtime: both sides successfully create
/// their own objects and simply never see each other. The user-visible symptom is
/// "the virtual camera shows the test pattern instead of my picture" and
/// "cross-process capture protection times out", with no error anywhere.
/// </para>
/// <para>
/// This exact defect shipped: the native constants had no namespace prefix while the
/// managed ones used <c>Global\</c>. A name without a prefix lands in the caller's
/// session namespace, i.e. it is effectively <c>Local\</c>, so the two never met.
/// </para>
/// <para>
/// The test reads the native header as text rather than linking against it, because
/// the header is C++ and this is a managed test assembly. That is deliberate: the
/// goal is to catch a divergence in the literals, and reading them is the most direct
/// way to do it.
/// </para>
/// </remarks>
public sealed class SharedChannelContractTests
{
    /// <summary>
    /// Locates <c>native/SeewoCommon/SeewoIpc.h</c> by walking up from the test
    /// binary. The test host runs from <c>src/SeewoAssistant.Tests/bin/&lt;cfg&gt;/...</c>,
    /// so the repository root is several levels up.
    /// </summary>
    private static string? FindNativeHeader()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "native", "SeewoCommon", "SeewoIpc.h");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Returns the native header path, failing the test when it cannot be found.
    /// </summary>
    /// <remarks>
    /// Failing rather than skipping is deliberate. If the header cannot be located,
    /// the contract between the two sides is unverified, and a skipped test would
    /// report success for something that was never checked.
    /// </remarks>
    private static string RequireNativeHeader() =>
        FindNativeHeader() ?? throw new InvalidOperationException(
            "找不到 native/SeewoCommon/SeewoIpc.h，无法验证共享通道契约。");

    /// <summary>
    /// Reads a <c>wchar_t</c> constant out of the native header and unescapes it the
    /// way the C++ compiler would.
    /// </summary>
    private static string? ReadNativeConstant(string headerText, string constantName)
    {
        // Matches: inline constexpr wchar_t kName[] = L"...";
        var pattern = $@"{Regex.Escape(constantName)}\s*\[\s*\]\s*=\s*L""([^""]*)""";
        var match = Regex.Match(headerText, pattern);

        if (!match.Success)
        {
            return null;
        }

        // "Global\\Seewo..." in the header is the C++ spelling of Global\Seewo...,
        // so each doubled backslash collapses to one.
        return match.Groups[1].Value.Replace("\\\\", "\\");
    }

    [Fact]
    public void NativeHeaderIsReachableFromTheTests()
    {
        // If this fails, every other test in this class would silently pass by being
        // skipped, which would defeat the point of having them.
        Assert.True(
            FindNativeHeader() is not null,
            "找不到 native/SeewoCommon/SeewoIpc.h。共享通道契约测试无法运行，请检查测试的搜索路径。");
    }

    [Theory]
    // Virtual camera frame channel.
    [InlineData("kVcamSectionName", "Global\\SeewoAssistant.VCam.Frame.v1")]
    [InlineData("kVcamLocalSectionName", "Local\\SeewoAssistant.VCam.Frame.v1")]
    [InlineData("kVcamDataEventName", "Global\\SeewoAssistant.VCam.DataReady.v1")]
    [InlineData("kVcamLocalDataEventName", "Local\\SeewoAssistant.VCam.DataReady.v1")]
    // Capture guard request channel.
    [InlineData("kGuardSectionName", "Global\\SeewoAssistant.CaptureGuard.v1")]
    [InlineData("kGuardLocalSectionName", "Local\\SeewoAssistant.CaptureGuard.v1")]
    [InlineData("kGuardRequestEventName", "Global\\SeewoAssistant.CaptureGuard.Request.v1")]
    [InlineData("kGuardLocalRequestEventName", "Local\\SeewoAssistant.CaptureGuard.Request.v1")]
    public void NativeConstantHasTheExpectedName(string constantName, string expected)
    {
        var header = RequireNativeHeader();

        var actual = ReadNativeConstant(File.ReadAllText(header!), constantName);

        Assert.True(
            actual is not null,
            $"在 SeewoIpc.h 中找不到常量 {constantName}。它可能被重命名或删除，而托管端仍在引用旧名字。");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ManagedVirtualCameraNamesMatchTheNativeHeader()
    {
        var header = RequireNativeHeader();

        var text = File.ReadAllText(header!);

        Assert.Equal(ReadNativeConstant(text, "kVcamSectionName"), FrameChannelContract.SectionName);
        Assert.Equal(ReadNativeConstant(text, "kVcamLocalSectionName"), FrameChannelContract.LocalSectionName);
        Assert.Equal(ReadNativeConstant(text, "kVcamDataEventName"), FrameChannelContract.DataEventName);
        Assert.Equal(ReadNativeConstant(text, "kVcamLocalDataEventName"), FrameChannelContract.LocalDataEventName);
    }

    [Fact]
    public void ManagedGuardNamesMatchTheNativeHeader()
    {
        var header = RequireNativeHeader();

        var text = File.ReadAllText(header!);

        Assert.Equal(ReadNativeConstant(text, "kGuardSectionName"), GuardChannel.SectionName);
        Assert.Equal(ReadNativeConstant(text, "kGuardLocalSectionName"), GuardChannel.LocalSectionName);
        Assert.Equal(ReadNativeConstant(text, "kGuardRequestEventName"), GuardChannel.RequestEventName);
        Assert.Equal(ReadNativeConstant(text, "kGuardLocalRequestEventName"), GuardChannel.LocalRequestEventName);
    }

    [Fact]
    public void EveryChannelNameCarriesANamespacePrefix()
    {
        var header = RequireNativeHeader();

        var text = File.ReadAllText(header!);

        string[] constants =
        [
            "kVcamSectionName", "kVcamLocalSectionName",
            "kVcamDataEventName", "kVcamLocalDataEventName",
            "kVcamRequestEventName", "kVcamLocalRequestEventName",
            "kGuardSectionName", "kGuardLocalSectionName",
            "kGuardRequestEventName", "kGuardLocalRequestEventName",
        ];

        foreach (var name in constants)
        {
            var value = ReadNativeConstant(text, name);

            Assert.True(
                value is not null,
                $"{name} 在 SeewoIpc.h 中不存在。");

            // An unprefixed name is created in the caller's session namespace, which
            // is what "Local\" means, so it silently cannot be shared with a service
            // in another session. Requiring the prefix makes that explicit.
            Assert.True(
                value!.StartsWith("Global\\", StringComparison.Ordinal) ||
                value.StartsWith("Local\\", StringComparison.Ordinal),
                $"{name} 的值 \"{value}\" 没有 Global\\ 或 Local\\ 前缀。" +
                "没有前缀的名字会落在调用方的会话命名空间里，跨会话共享会静默失败。");
        }
    }

    [Fact]
    public void GlobalAndLocalVariantsOfEachChannelDiffer()
    {
        var header = RequireNativeHeader();

        var text = File.ReadAllText(header!);

        (string Global, string Local)[] pairs =
        [
            ("kVcamSectionName", "kVcamLocalSectionName"),
            ("kVcamDataEventName", "kVcamLocalDataEventName"),
            ("kGuardSectionName", "kGuardLocalSectionName"),
            ("kGuardRequestEventName", "kGuardLocalRequestEventName"),
        ];

        foreach (var (globalName, localName) in pairs)
        {
            var globalValue = ReadNativeConstant(text, globalName);
            var localValue = ReadNativeConstant(text, localName);

            Assert.NotNull(globalValue);
            Assert.NotNull(localValue);

            // If these were equal, the fallback path would open the same object it
            // already failed to open, and the fallback would be useless.
            Assert.NotEqual(globalValue, localValue);
        }
    }
}
