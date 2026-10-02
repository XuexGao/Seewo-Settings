using System.Text.RegularExpressions;
using SeewoAssistant.Core.Services;
using Xunit;

namespace SeewoAssistant.Tests;

/// <summary>
/// Pins the cross-file and cross-privilege contracts of the system-integration service.
/// </summary>
/// <remarks>
/// <para>
/// The CLSIDs are written into <c>HKLM</c> by <c>scripts\Install-Native.ps1</c> and read back
/// by the app. If the two disagree, nothing fails: the app reports "未注册", the camera simply
/// never appears, and the cause is a literal in a file nobody looked at. That is the same
/// class of silent mismatch as the shared-channel names, so it gets the same treatment.
/// </para>
/// <para>
/// The remaining assertions cover mistakes that are invisible at runtime in the good case and
/// expensive in the bad one: an unhandled UAC cancellation, and an「应用和功能」entry written to
/// <c>HKLM</c>, which would silently start requiring administrator rights.
/// </para>
/// </remarks>
public sealed class SystemIntegrationContractTests
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

    private static string ServiceSource =>
        ReadSource("src", "SeewoAssistant.Core", "Services", "SystemIntegrationService.cs");

    /// <summary>Enumerates shipped C# sources, skipping build output.</summary>
    private static IEnumerable<string> EnumerateCoreSources(string root) =>
        Directory
            .EnumerateFiles(Path.Combine(root, "src", "SeewoAssistant.Core"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>Extracts a parameterless method body, up to the closing brace at four spaces.</summary>
    private static string ExtractMethod(string source, string name)
    {
        var match = Regex.Match(
            source,
            $@"(?:public|private|internal|protected)[\w\s<>\?\[\]]*?\s{Regex.Escape(name)}\(\)(?<body>.*?)\n    \}}",
            RegexOptions.Singleline);

        Assert.True(match.Success, $"找不到方法 {name}。");

        return match.Groups["body"].Value;
    }

    [Fact]
    public void TheClsidsAppearOnceAndMatchTheInstallScript()
    {
        var service = ServiceSource;

        // One literal, one place to change. The service is the source of truth; the camera
        // service's constants alias these so a second copy cannot drift away from it.
        var mediaSourceCount = Regex.Matches(
            service, Regex.Escape(SystemIntegrationService.MediaSourceClsid)).Count;
        var directShowCount = Regex.Matches(
            service, Regex.Escape(SystemIntegrationService.DirectShowFilterClsid)).Count;

        Assert.True(
            mediaSourceCount == 1,
            $"SystemIntegrationService.cs 里出现了 {mediaSourceCount} 次 {SystemIntegrationService.MediaSourceClsid}，应当只有一次。");

        Assert.True(
            directShowCount == 1,
            $"SystemIntegrationService.cs 里出现了 {directShowCount} 次 {SystemIntegrationService.DirectShowFilterClsid}，应当只有一次。");

        var root = FindRepositoryRoot();
        var duplicates = EnumerateCoreSources(root)
            .Where(f => !Path.GetFileName(f).Equals("SystemIntegrationService.cs", StringComparison.Ordinal))
            .Where(f =>
            {
                var text = File.ReadAllText(f);

                return text.Contains(SystemIntegrationService.MediaSourceClsid, StringComparison.OrdinalIgnoreCase) ||
                       text.Contains(SystemIntegrationService.DirectShowFilterClsid, StringComparison.OrdinalIgnoreCase);
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            $"这些文件重复定义了 CLSID，两处一旦不一致运行时不会有任何报错：{string.Join(", ", duplicates)}");

        // The script is what actually writes the keys, so it is the other half of the contract.
        var script = ReadSource("scripts", "Install-Native.ps1");

        Assert.True(
            script.Contains(SystemIntegrationService.MediaSourceClsid, StringComparison.OrdinalIgnoreCase),
            $"scripts\\Install-Native.ps1 里没有 {SystemIntegrationService.MediaSourceClsid}，两边已经不一致。");

        Assert.True(
            script.Contains(SystemIntegrationService.DirectShowFilterClsid, StringComparison.OrdinalIgnoreCase),
            $"scripts\\Install-Native.ps1 里没有 {SystemIntegrationService.DirectShowFilterClsid}，两边已经不一致。");

        // The value name the script records and the app reads is the same cross-file contract:
        // a rename on either side turns the recorded path into nothing at all.
        Assert.True(
            script.Contains(SystemIntegrationService.InstallRecordValueName, StringComparison.OrdinalIgnoreCase),
            $"scripts\\Install-Native.ps1 里没有 {SystemIntegrationService.InstallRecordValueName}，" +
            "安装位置记录会永远读不到，但不会有任何报错。");
    }

    [Fact]
    public void ElevationUsesRunAsAndTreatsACancelledPromptAsADecision()
    {
        var service = ServiceSource;

        Assert.Contains("UseShellExecute = true", service);
        Assert.Contains("Verb = \"runas\"", service);
        Assert.Contains("-NoProfile -ExecutionPolicy Bypass -File", service);
        Assert.Contains("-Action ", service);

        // Declining UAC makes Process.Start throw Win32Exception with ERROR_CANCELLED, and the
        // numeric value is part of the contract with the shell, so both the property and the
        // value are pinned. Without this branch it surfaces as a crash or a misleading
        // "install failed".
        Assert.Contains("NativeErrorCode == ElevatedScriptResult.UserCancelledErrorCode", service);
        Assert.Contains("UserCancelledErrorCode = 1223", service);
        Assert.Contains("UserCancelled", service);

        // The wait covers the UAC prompt plus the registration, so it must not run on the UI
        // thread: doing so froze the window for as long as the user took to decide.
        Assert.Contains("Task.Run", service);
    }

    [Fact]
    public void TheAppsAndFeaturesEntryIsWrittenPerUser()
    {
        var register = ExtractMethod(ServiceSource, "RegisterInAppsAndFeatures");

        // HKCU needs no elevation; HKLM would turn a label toggle into an administrator prompt.
        Assert.Contains("Registry.CurrentUser", register);
        Assert.DoesNotContain("LocalMachine", register);

        Assert.Contains("UninstallString", register);
        Assert.Contains("InstallLocation", register);
        Assert.Contains("DisplayVersion", register);
        Assert.Contains("NoModify", register);
        Assert.Contains("NoRepair", register);

        // The uninstall command must point at the portable uninstaller, and only that script
        // can remove the registration the app itself cannot.
        Assert.Contains("Uninstall-Portable.ps1", ServiceSource);

        // The same guard on the way out, or the entry could be deleted from under a real install.
        var unregister = ExtractMethod(ServiceSource, "UnregisterFromAppsAndFeatures");
        Assert.Contains("Registry.CurrentUser", unregister);
        Assert.DoesNotContain("LocalMachine", unregister);

        // The portable uninstaller removes the shortcut named after the same display name. A
        // rename on either side leaves a shortcut that nothing will ever remove.
        var portable = ReadSource("scripts", "Uninstall-Portable.ps1");
        var displayName = Regex.Match(portable, @"\$AppDisplayName\s*=\s*'([^']+)'");

        Assert.True(displayName.Success, "scripts\\Uninstall-Portable.ps1 里找不到 $AppDisplayName。");
        Assert.Equal(SystemIntegrationService.DisplayName, displayName.Groups[1].Value);
        Assert.Equal($"{SystemIntegrationService.DisplayName}.lnk", SystemIntegrationService.ShortcutFileName);
    }

    [Fact]
    public void EveryNewSettingsButtonIsNamedForTheSweep()
    {
        var xaml = ReadSource("src", "SeewoAssistant", "Pages", "SettingsPage.xaml");

        const string startMarker = "<!-- ============================== system integration";
        const string endMarker = "<!-- ============================== camera";

        var start = xaml.IndexOf(startMarker, StringComparison.Ordinal);
        var end = xaml.IndexOf(endMarker, StringComparison.Ordinal);

        Assert.True(start >= 0, "设置页里找不到系统集成卡片的标记注释。");
        Assert.True(end > start, "找不到系统集成卡片之后的下一段标记。");

        var card = xaml[start..end];
        var buttons = Regex.Matches(card, @"<Button\b[^>]*>");

        // A sweep that silently found nothing would let this test pass while checking nothing.
        Assert.True(buttons.Count >= 6, $"系统集成卡片里只找到 {buttons.Count} 个按钮。");

        foreach (Match button in buttons)
        {
            var tag = button.Value;

            Assert.True(
                tag.Contains("AutomationProperties.Name=", StringComparison.Ordinal),
                $"这个按钮没有 AutomationProperties.Name，冒烟测试点不到，屏幕阅读器也读不出来：{tag}");

            Assert.True(
                tag.Contains("x:Name=", StringComparison.Ordinal),
                $"这个按钮没有 x:Name，自动化脚本无法定位：{tag}");
        }

        // The camera page's new control has the same requirement.
        var camera = ReadSource("src", "SeewoAssistant", "Pages", "VirtualCameraPage.xaml");
        var reregister = Regex.Match(camera, @"<Button[^>]*x:Name=""ReregisterButton""[^>]*>");

        Assert.True(reregister.Success, "虚拟摄像头页里找不到 ReregisterButton。");
        Assert.Contains("AutomationProperties.Name=", reregister.Value);
    }

    [Fact]
    public void TheCameraPageReachesForTheElevatedHelper()
    {
        // C-1: the in-app install used to fail outright because the app is not elevated. The
        // page must route registration through the elevated script in that case, not only
        // report the failure.
        var source = ReadSource("src", "SeewoAssistant", "Pages", "VirtualCameraPage.xaml.cs");

        Assert.Contains("Services.SystemIntegration.RunElevatedAsync(NativeScriptAction.Install)", source);

        // C-2: a registration pointing at another folder must be visible and repairable here,
        // which is where the user looks when the camera does not work.
        Assert.Contains("NativeRegistrationStatus.RegisteredToCurrentFolder", source);
        Assert.Contains("FindBackendComponent", source);
    }
}
