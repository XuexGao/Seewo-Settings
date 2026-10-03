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

        foreach (var clsid in new[] { MediaSourceClsid, DirectShowClsid })
        {
            // Spelled out rather than taken from a `{#define}`, and that is the point of
            // this assertion. Inno reads `{` as the start of a constant, so a literal
            // brace has to be doubled - but if the value came from a substitution, the
            // brace that closes the substitution is the one that would have closed the
            // GUID, leaving a different key path. The cleanup would then delete nothing
            // and report nothing. Hard-coding the pair of keys is what keeps the braces
            // unambiguous, and this count is what keeps the duplication honest.
            var literal = "\\{{" + clsid + "}";
            var uses = Regex.Matches(registry, Regex.Escape(literal)).Count;

            Assert.True(uses == 2, $"[Registry] 里 {clsid} 出现了 {uses} 次，应为 2 次（两个注册表视图）。");
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

    [Fact]
    public void CleanupRemovesTheDirectShowCategoryEntryAsWell()
    {
        // The reported defect, and it survived a review pass precisely because only the
        // other removal path had been checked. A DirectShow capture source registers in
        // two places - its own CLSID, and an entry under the video-input-device category
        // that ICreateDevEnum walks. Deleting only the first leaves the camera in every
        // device list, pointing at a CLSID that no longer exists.
        //
        // `-Action Uninstall` got this right because `regsvr32 /u` removes both.
        // `-Action Cleanup` deletes keys by hand and removed only one, so the zip
        // distribution - whose uninstaller calls Cleanup - still left the phantom camera.
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "Install-Native.ps1"));

        // The category GUID must be the one the native filter registers under. It is
        // read from the native source rather than repeated, because a mismatch here is
        // silent: the cleanup would delete a key nobody wrote and report success.
        var native = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "native", "SeewoVirtualCamera.DShow", "dllmain.cpp"));

        var category = Regex.Match(native, @"\{860BB310-[0-9A-Fa-f-]+\}");
        Assert.True(category.Success, "原生源码里找不到 VideoInputDeviceCategory 的 GUID。");
        Assert.Contains(category.Value, script);

        // And the friendly name, which is the other thing the entry is matched by. It is
        // defined in the filter's own translation unit rather than in dllmain.cpp, so
        // both files are searched - the point is to compare against whatever the filter
        // actually registers, not against a particular file.
        var friendlySource = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "native", "SeewoVirtualCamera.DShow", "SeewoDShowFilter.cpp"));

        var friendly = Regex.Match(friendlySource, @"kFriendlyName\[\] = L""(?<name>[^""]+)""");
        Assert.True(friendly.Success, "原生源码里找不到 kFriendlyName。");
        Assert.Contains(friendly.Groups["name"].Value, script);

        // The removal must be reachable from the shared routine, so both paths use it.
        var removal = script[script.IndexOf("function Remove-ComRegistrationKeys", StringComparison.Ordinal)..];
        removal = removal[..removal.IndexOf("function Remove-DirectShowCategoryEntries", StringComparison.Ordinal)];
        Assert.Contains("Remove-DirectShowCategoryEntries", removal);
    }

    [Fact]
    public void TheUninstallerDoesNotPromiseMoreThanItDelivers()
    {
        // The script told the user "deleting this folder will not leave anything behind"
        // before it had checked whether the cleanup succeeded. That sentence is what
        // turns a failed cleanup into a phantom camera, because it is an instruction to
        // delete the one thing that could still fix it.
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "Uninstall-Portable.ps1"));

        // The promise may only be made after the exit code has been examined.
        var checkAt = script.IndexOf("if ($cleanupExit -ne 0)", StringComparison.Ordinal);
        var promiseAt = script.IndexOf("已经反注册了虚拟摄像头组件", StringComparison.Ordinal);

        Assert.True(checkAt >= 0, "找不到对清理退出码的判断。");
        Assert.True(promiseAt >= 0, "找不到完成后的说明。");
        Assert.True(checkAt < promiseAt, "完成说明出现在退出码判断之前，失败时也会被打印。");

        Assert.Contains("请不要删除这个目录", script);
    }

    [Fact]
    public void ACleanupThatFailsIsReportedAsAFailure()
    {
        // The promise "you can delete this folder now, nothing will be left behind" is
        // made by Uninstall-Portable.ps1 only when the cleanup exits 0. That branch is
        // worthless unless the cleanup can actually exit non-zero, and it could not:
        // Cleanup-Native returned 0 unconditionally, and the helpers it called returned
        // the number of entries they had *removed*, which cannot express failure -
        // "0 removed" and "3 could not be removed" look identical to a caller.
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "Install-Native.ps1"));

        foreach (var name in new[]
                 {
                     "Remove-ComRegistrationKeys",
                     "Remove-DirectShowCategoryEntries",
                     "Remove-FirewallRules",
                     "Remove-OwnSettingsKey",
                 })
        {
            var body = Regex.Match(
                script,
                $@"^function {Regex.Escape(name)} \{{(?<body>.*?)^\}}",
                RegexOptions.Singleline | RegexOptions.Multiline);

            Assert.True(body.Success, $"找不到 {name}。");

            // Each of these has to be able to say "something is still there".
            Assert.True(
                Regex.IsMatch(body.Groups["body"].Value, @"Write-Warn|\$left\.Count"),
                $"{name} 没有任何失败路径，调用方无法判断清理是否成功。");
        }

        // And the two entry points have to turn that into a non-zero exit code.
        foreach (var name in new[] { "Cleanup-Native", "Uninstall-Native" })
        {
            var body = Regex.Match(
                script,
                $@"^function {Regex.Escape(name)} \{{(?<body>.*?)^\}}",
                RegexOptions.Singleline | RegexOptions.Multiline).Groups["body"].Value;

            Assert.Contains("if ($failures -gt 0)", body);
            Assert.Contains("return 1", body);
        }
    }

    [Fact]
    public void NoScriptMakesAnUnconditionalPromiseAboutTheMachine()
    {
        // The same sentence lived in two scripts, and fixing one left the other. It is
        // the sentence that turns a failed cleanup into a phantom camera, because it
        // tells the user to delete the one thing that could still fix it.
        foreach (var file in new[] { "Install-Native.ps1", "Uninstall-Portable.ps1" })
        {
            var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", file));

            Assert.DoesNotContain("不会再留下任何系统残留", script);
        }

        // Each script phrases its own two outcomes, so the contract is asserted as a
        // contract rather than by matching one wording: the failure message must come
        // from the branch that exits non-zero, and the "you may delete this" message
        // must not be reachable before that branch.
        var native = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "Install-Native.ps1"));
        var failBranch = native.IndexOf("if ($failures -gt 0)", StringComparison.Ordinal);
        var safeMessage = native.IndexOf("可以安全删除程序目录了", StringComparison.Ordinal);

        Assert.True(failBranch >= 0, "Install-Native.ps1 缺少清理失败的分支。");
        Assert.True(safeMessage >= 0, "Install-Native.ps1 缺少清理成功的说明。");
        Assert.True(failBranch < safeMessage, "「可以安全删除」出现在失败分支之前，失败时也会被打印。");

        var portable = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "Uninstall-Portable.ps1"));
        var portableFail = portable.IndexOf("if ($cleanupExit -ne 0)", StringComparison.Ordinal);
        var portableKeep = portable.IndexOf("在清理成功之前请不要删除这个目录", StringComparison.Ordinal);
        var portableDone = portable.IndexOf("已经反注册了虚拟摄像头组件", StringComparison.Ordinal);

        Assert.True(portableFail >= 0, "Uninstall-Portable.ps1 缺少对清理退出码的判断。");
        Assert.True(portableKeep >= 0, "Uninstall-Portable.ps1 失败时没有要求保留目录。");
        Assert.True(portableDone >= 0, "Uninstall-Portable.ps1 缺少清理成功的说明。");
        Assert.True(portableFail < portableDone, "完成说明出现在退出码判断之前，失败时也会被打印。");
    }

    [Fact]
    public void TheSuspendDialogTextIsNotMistakableForAButtonLabel()
    {
        // Reported four times: the binary string table contains "将挂起 " with a trailing
        // space, read as a button label. It was a fragment of an interpolated sentence -
        // the compiler emits the literal stretch before each hole as its own constant -
        // and the space is the correct spacing between a Chinese word and a numeral. The
        // label is the third argument of that call.
        //
        // Rather than keep explaining that, the sentence is built with string.Format, so
        // the whole template is one constant containing {0} and there is no fragment left
        // for a scanner to mistake for a label.
        var page = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "SeewoAssistant", "Pages", "SeewoPage.xaml.cs"));

        var dialog = page[page.IndexOf("挂起选中进程", StringComparison.Ordinal)..];
        dialog = dialog[..dialog.IndexOf("挂起\"", StringComparison.Ordinal)];

        Assert.Contains("string.Format(", dialog);
        Assert.DoesNotContain("$\"将挂起 ", dialog);
    }
}
