using System.Text.RegularExpressions;
using Xunit;

namespace SeewoAssistant.Tests;

/// <summary>
/// Covers the installer's obligations, which no compile of the application can check.
/// </summary>
/// <remarks>
/// <para>
/// The installer is only ever exercised by a real Windows machine, so a regression in it
/// is invisible until someone runs it. These assertions are deliberately about the
/// things whose failure is silent and unrecoverable for the user - chiefly the removal
/// of the COM registration, where the leftover is a phantom camera that the tools able
/// to remove it have just been deleted alongside.
/// </para>
/// <para>
/// They read the script as text rather than running Inno Setup, so they pin intent
/// ("this line exists") and not behaviour. That is the honest limit here; the behaviour
/// still needs a machine.
/// </para>
/// </remarks>
public sealed class InstallerContractTests
{
    private static string RepositoryRoot()
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

    private static string Script() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "SeewoAssistant.iss"));

    /// <summary>Returns the body of one section, stopping at the next section header.</summary>
    private static string Section(string name)
    {
        var match = Regex.Match(
            Script(),
            $@"^\[{Regex.Escape(name)}\](?<body>.*?)(?=^\[|\z)",
            RegexOptions.Singleline | RegexOptions.Multiline);

        Assert.True(match.Success, $"安装脚本里没有 [{name}] 段。");

        return match.Groups["body"].Value;
    }

    /// <summary>Drops comment lines so an assertion cannot be satisfied by a comment.</summary>
    private static string Directives(string section) =>
        string.Join('\n', section
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith(';')));

    private const string MediaSourceClsid = "A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60";
    private const string DirectShowClsid = "B8F5C3D2-6E4A-4B99-8C7F-2D3E4F5A6B71";

    [Fact]
    public void TheInstallerDoesNotAskWhichInstallModeToUse()
    {
        // The prompt asked the user to choose between two options whose difference
        // (Program Files against AppData) the dialog itself could not explain, before
        // they had seen what was being installed. It also implied that "only for me"
        // avoided the administrator requirement, which is not true: the native
        // components register under HKLM either way.
        var setup = Directives(Section("Setup"));

        Assert.DoesNotContain("PrivilegesRequiredOverridesAllowed", setup);
        Assert.Contains("PrivilegesRequired=admin", setup);
    }

    [Fact]
    public void RegistrationIsRemovedBeforeTheFilesAreDeleted()
    {
        // `regsvr32 /u` needs the file it is unregistering, so this has to be an
        // [UninstallRun] entry: Inno runs those as the first step of uninstallation.
        // Moving it later, or into [UninstallDelete], would silently clean nothing.
        var uninstallRun = Directives(Section("UninstallRun"));

        Assert.Contains("Install-Native.ps1", uninstallRun);
        Assert.Contains("-Action Uninstall", uninstallRun);

        // Without this, reinstalling stacks copies of the entry in the uninstall log
        // and the cleanup runs several times.
        Assert.Contains("RunOnceId", uninstallRun);
    }

    [Fact]
    public void TheComRegistrationIsAlsoRemovedWithoutTheDlls()
    {
        // The defect this exists for: the uninstaller deleted the payload, so
        // `regsvr32 /u` could no longer run, so the CLSID survived pointing at a file
        // that no longer existed. The cleanup therefore must not depend on the DLL.
        var registry = Directives(Section("Registry"));
        var script = Script();

        // The section refers to the CLSIDs through the #define block rather than
        // repeating the literals, so that the [Code] section cannot drift from it. The
        // define values are checked here; the number of references is what proves both
        // registry views are covered.
        foreach (var (define, clsid) in new[]
                 {
                     ("ClsidMediaSource", MediaSourceClsid),
                     ("ClsidDirectShow", DirectShowClsid),
                 })
        {
            // Whitespace-tolerant: the defines are aligned for readability, so the
            // number of spaces between the name and the value is not part of the
            // contract.
            var definePattern = $@"^#define {define}\s+""{Regex.Escape(clsid)}""";
            Assert.True(
                Regex.IsMatch(script, definePattern, RegexOptions.Multiline),
                $"#define {define} 的值不是 {clsid}。");

            var uses = Regex.Matches(registry, Regex.Escape($"{{#{define}}}")).Count;
            Assert.True(uses >= 2, $"{define} 只覆盖了一个注册表视图（引用 {uses} 次）。");
        }

        // dontcreatekey keeps Setup from creating the keys it is only there to remove;
        // uninsdeletekey deletes the key and every subkey, including the
        // Instance\Seewo Virtual Camera entry that makes the phantom device appear.
        Assert.Contains("dontcreatekey", registry);
        Assert.Contains("uninsdeletekey", registry);
        Assert.Contains("HKLM32", registry);
    }

    [Fact]
    public void TheCodeSectionDeletesTheSameKeysAsTheRegistrySection()
    {
        // Deliberately redundant. The two mechanisms fail for different reasons - the
        // declarative one depends on how Inno treats keys Setup never created, the
        // other calls the API - and a silent failure here strands the user with a
        // camera they cannot remove.
        var code = Directives(Section("Code"));

        Assert.Contains(MediaSourceClsid, code);
        Assert.Contains(DirectShowClsid, code);

        Assert.Contains("RegDeleteKeyIncludingSubkeys", code);

        // The Pascal Script name is not the [Registry] spelling: it is
        // HKEY_LOCAL_MACHINE_32, not HKLM32. Using the wrong one is a compile error in
        // the installer, which only CI can catch.
        Assert.Contains("HKEY_LOCAL_MACHINE_32", code);
        Assert.DoesNotContain("HKEY_LOCAL_MACHINE32", code);
    }

    [Fact]
    public void InstallingRegistersTheNativeComponentsByDefault()
    {
        // The single biggest reported rough edge: the program installed, the camera
        // still did not work, and the missing step was an administrator-only script
        // run that nothing mentioned. Setup is already elevated, so it does it.
        var tasks = Directives(Section("Tasks"));
        var run = Directives(Section("Run"));

        Assert.Contains("registercamera", tasks);

        // A task with neither flag is checked by default, which is what "on by
        // default" means here. `unchecked` would quietly turn the registration off.
        var registerLine = tasks
            .Split('\n')
            .First(l => l.Contains("registercamera", StringComparison.Ordinal));
        Assert.DoesNotContain("unchecked", registerLine);

        var registrationRun = run
            .Split('\n')
            .First(l => l.Contains("Install-Native.ps1", StringComparison.Ordinal));

        Assert.Contains("-Action Install", registrationRun);
        Assert.Contains("Tasks: registercamera", registrationRun);

        // Must run unattended too: the reported test installed with /VERYSILENT, and a
        // skipifsilent here would mean silent installs get no camera.
        Assert.DoesNotContain("skipifsilent", registrationRun);
    }

    [Fact]
    public void TheZeroVersionPlaceholderIsGone()
    {
        // "Apps & features" showed 0.0.0 while the exe's own version resource said
        // 1.0.0, because the installer step hard-coded its own default instead of
        // using the build's version. Two numbers that disagree are worse than one.
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot(), ".github", "workflows", "build.yml"));

        Assert.Contains("steps.app_version.outputs.version", workflow);
        Assert.DoesNotContain("$version = '0.0.0'", workflow);

        // A prerelease tag such as 1.2.3-rc1 is a valid AppVersion but not a valid
        // VersionInfoVersion, which accepts only dot-separated numbers.
        Assert.Contains("version_numeric", workflow);
    }

    [Fact]
    public void ThePublisherIsNotThePlaceholderOrganizationName()
    {
        // Only the directive is checked, not the whole file. The comment above it
        // records what the value used to be and why it was wrong, and deleting that
        // would lose the reason the next person should not put it back.
        var publisher = Regex.Match(
            Script(),
            @"^#define AppPublisher ""(?<value>[^""]*)""",
            RegexOptions.Multiline);

        Assert.True(publisher.Success, "找不到 AppPublisher 定义。");

        var value = publisher.Groups["value"].Value;

        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.NotEqual("SeewoAssistant contributors", value);
    }

    [Fact]
    public void ThePortableDistributionShipsItsOwnRemovalPath()
    {
        // The zip has no installer, so this script is the only thing that can undo
        // what it registered. Without it, deleting the folder is exactly the sequence
        // that leaves the phantom camera behind.
        var root = RepositoryRoot();

        Assert.True(
            File.Exists(Path.Combine(root, "scripts", "Uninstall-Portable.ps1")),
            "缺少 scripts/Uninstall-Portable.ps1。");

        // And the build has to notice if it ever stops being packaged, or the release
        // silently ships without it.
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"));
        Assert.Contains("scripts/Uninstall-Portable.ps1", workflow);
    }

    [Fact]
    public void BothCleanupPathsCanRemoveAStaleRegistration()
    {
        // A registration that points at a directory other than the current one - a
        // second copy, or a copy whose folder was moved - is removed by name, not by
        // asking the DLL to unregister itself. Running the unregister for a DLL that
        // is not there has to remove the key anyway, which is what the previous
        // `if (Test-Path $dshowDll)` guard prevented.
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "Install-Native.ps1"));

        // Accepted by the parameter set and reachable from the dispatch, or the
        // application's "clean this machine up" button calls a script that errors out.
        var validateSet = Regex.Match(script, @"ValidateSet\([^)]*\)").Value;
        Assert.Contains("'Cleanup'", validateSet);
        Assert.Matches(@"'Cleanup'\s*\{\s*\$exitCode", script);

        Assert.Contains("Remove-ComRegistrationKeys", script);

        // The removal must not sit behind an existence check on the payload.
        var removal = script[script.IndexOf("function Remove-ComRegistrationKeys", StringComparison.Ordinal)..];
        removal = removal[..removal.IndexOf("function Remove-FirewallRules", StringComparison.Ordinal)];

        Assert.DoesNotContain("SeewoVirtualCamera.DShow.dll", removal);
    }
}
