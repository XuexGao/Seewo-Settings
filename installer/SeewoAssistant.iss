; SeewoAssistant installer (Inno Setup 6).
;
; This is the second distribution option. The zip produced by build.yml remains
; the "extract and run" package; this script wraps the *same* assembled release
; tree (artifacts/release/win-x64) into a normal Windows installer so a user can
; pick a location and choose shortcuts.
;
; Compile (CI does this; see .github/workflows/build.yml):
;   ISCC.exe installer\SeewoAssistant.iss /DAppVersion=1.2.3 /DAppVersionNumeric=1.2.3
;
; /DAppVersion is optional. When the build is triggered by a tag the workflow
; passes the tag with its leading "v" stripped; otherwise the default below is
; used. The #ifndef guards are what make the /D overrides work without a
; "duplicate definition" error.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

; AppVersion is free-form and is what the user sees, so a prerelease tag may keep
; its suffix ("1.2.3-rc1"). VersionInfoVersion is stricter: it must be up to four
; dot-separated numbers, and passing it a suffix is a compile error. CI therefore
; passes a sanitised numeric form separately; compiling by hand, where nobody
; supplied one, falls back to AppVersion, which is all-numeric by default.
#ifndef AppVersionNumeric
  #define AppVersionNumeric AppVersion
#endif

; The release folder is assembled by the "Assemble the release folder" step in
; build.yml, next to this script's parent directory. Overridable so the same
; script can be compiled by hand against a different tree.
#ifndef SourceDir
  #define SourceDir "..\artifacts\release\win-x64"
#endif

#define AppName "希沃助手"
; Shown as the Publisher in "Apps & features". The previous value,
; "SeewoAssistant contributors", read as a GitHub org rather than a vendor, which
; makes the entry hard to trust and impossible to look up. This is the account
; that actually publishes the releases.
#define AppPublisher "XuexGao"
#define AppPublisherURL "https://github.com/XuexGao/Seewo-Settings"
#define AppExeName "SeewoAssistant.exe"

; The installer icon, taken from the same file the application itself uses.
; Guarded so the script also compiles when it is run against a checkout that
; does not have that asset, rather than failing on a missing file.
#ifndef AppIcon
  #if FileExists("..\src\SeewoAssistant\Assets\app.ico")
    #define AppIcon "..\src\SeewoAssistant\Assets\app.ico"
  #else
    #define AppIcon ""
  #endif
#endif

[Setup]
; AppId identifies the product across upgrades. Keep it stable: changing it
; makes every new installer a separate product that no longer replaces the old
; one (the old entry would stay in "Apps & features" forever).
AppId={{CF4B567A-DBCF-4358-BBEE-13D040AF8C47}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppPublisherURL}
AppSupportURL={#AppPublisherURL}/issues
AppUpdatesURL={#AppPublisherURL}/releases
VersionInfoVersion={#AppVersionNumeric}

; DefaultDirName is only the starting point. DisableDirPage=no keeps the
; "Select Destination Location" page so the user can change it.
DefaultDirName={autopf}\SeewoAssistant
DisableDirPage=no
DefaultGroupName={#AppName}
; The Start Menu folder page is skipped; the shortcut itself is optional and is
; controlled by the startmenuicon task below.
DisableProgramGroupPage=yes
AllowNoIcons=yes

; x64 only, matching the shipped binaries.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Minimum OS is Windows 10 2004 (build 19041), the same floor the app declares
; in its own csproj (TargetPlatformMinVersion 10.0.19041.0).
MinVersion=10.0.19041

; Always per-machine, never a choice made up front. The previous
; `PrivilegesRequiredOverridesAllowed=dialog` made Setup open with an
; "install for all users, or only for me?" prompt as its very first screen.
; That is the wrong question to lead with: it asks the user to make an
; elevation decision before they have seen what is being installed or where,
; and its two buttons differ only in a detail (Program Files vs AppData) that
; the dialog cannot explain. The reference installer this project follows
; (FlClash) fixes the mode and goes straight to the wizard.
;
; The per-user mode was also never a real second option here: the native
; components register under HKLM and need administrator rights regardless, so
; "only for me" produced an installation whose main feature could not work
; without elevation anyway.
PrivilegesRequired=admin

; LZMA2/max gives the smallest download at the cost of compression time, which
; is the right trade for a release asset.
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; The release tree is one self-contained folder; the Ready page is kept so the
; user sees the chosen location and tasks before anything is written.
DisableReadyPage=no

OutputDir=..\artifacts\dist
OutputBaseFilename=SeewoAssistant-Setup-x64
#if AppIcon != ""
SetupIconFile={#AppIcon}
#endif
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
; ChineseSimplified.isl is an official Inno Setup translation, but it is NOT
; bundled with every Inno Setup install: the Chocolatey `innosetup` package that
; CI uses (6.7.1) ships only the compiler plus a handful of languages, and
; referencing `compiler:Languages\ChineseSimplified.isl` failed the build with
; "Couldn't open include file". The file is therefore vendored next to this
; script, which also makes a local compile behave identically to CI.
;
; Only this language is defined: the app itself is Simplified Chinese only, and
; adding English would double every message for no benefit.
;
; The relative path resolves against this script's own directory (Inno's "source
; directory"), so the file must stay beside this .iss. It is plain UTF-8 with no
; BOM, which is what Inno's [Languages] documentation recommends for .isl files.
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
; Both shortcuts are offered on the "Select Additional Tasks" page and both can
; be cleared. A task with neither flag is checked by default, which is why
; `launchapp` carries `unchecked` and the others do not.
Name: "registercamera"; Description: "注册虚拟摄像头组件（推荐，需要管理员权限）"; GroupDescription: "系统集成："
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startmenuicon"; Description: "创建开始菜单快捷方式"; GroupDescription: "{cm:AdditionalIcons}"
Name: "launchapp"; Description: "安装完成后启动 {#AppName}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Copy the assembled release tree verbatim. recursesubdirs and createallsubdirs
; preserve native\x64, native\x86 and scripts exactly as the zip has them.
; This layout is load-bearing, not cosmetic:
;   * SeewoVirtualCamera.Setup.exe resolves SeewoVirtualCamera.dll relative to
;     its own directory, so that DLL must stay next to the tool at the root.
;     Reorganising these files is what once made every camera install fail with
;     "找不到媒体源 DLL".
;   * native\x86 must stay separate from native\x64: it holds the 32-bit
;     DirectShow filter used by 32-bit host applications.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; The installation is always per-machine, so these land in the shared Start Menu
; and on the public desktop, and every user of the machine sees them.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Uninstall cleanup for the native components, done declaratively rather than in
; code because it has to work in the case that caused the reported defect: the
; uninstaller had already removed the DLLs, so `regsvr32 /u` could no longer run
; and the CLSID keys survived, pointing at a file that no longer existed. That
; left a "Seewo Virtual Camera" device in every DirectShow enumeration that
; could never work and that the user had no way to remove, because the script
; that could remove it had just been deleted along with the install directory.
;
; `dontcreatekey` + `uninsdeletekey` is the documented idiom for exactly this:
; create nothing at install time, delete the key and all its subkeys at
; uninstall time. Deleting a key that is not there is not an error, so this is
; safe whether or not the components were ever registered.
;
; Both registry views are covered. regsvr32.exe switches view according to which
; System32/SysWOW64 copy runs, and the x86 DirectShow filter that 32-bit host
; applications load registers under WOW6432Node.
; The CLSIDs are written out rather than taken from a `{#define}`. Subtle and worth
; spelling out, because the failure is silent: Inno reads `{` as the start of a
; constant, so a literal brace has to be doubled to `{{`. If the value came from a
; `{#Name}` substitution the closing brace would be the one that ends the
; substitution, leaving `\{{GUID` and no terminator - a different key path, so the
; cleanup would delete nothing and report nothing. `{{GUID}` involves no
; substitution at all and is the same form the AppId above uses, which is known to
; work. The duplicates are checked against the [Code] section and against
; scripts/Install-Native.ps1 by InstallerContractTests.
Root: HKLM; Subkey: "SOFTWARE\Classes\CLSID\{{A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60}"; Flags: dontcreatekey uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Classes\CLSID\{{B8F5C3D2-6E4A-4B99-8C7F-2D3E4F5A6B71}"; Flags: dontcreatekey uninsdeletekey
Root: HKLM32; Subkey: "SOFTWARE\Classes\CLSID\{{A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60}"; Flags: dontcreatekey uninsdeletekey
Root: HKLM32; Subkey: "SOFTWARE\Classes\CLSID\{{B8F5C3D2-6E4A-4B99-8C7F-2D3E4F5A6B71}"; Flags: dontcreatekey uninsdeletekey

; Where this copy lives. The application reads it back on startup to notice that
; the COM registration points at a different directory - the situation where a
; second copy re-points the global CLSID and deleting the first copy silently
; breaks the second. Written by Setup rather than by the registration script, so
; the record exists even when the user leaves the registration task unchecked.
Root: HKLM; Subkey: "SOFTWARE\SeewoAssistant"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\SeewoAssistant"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"; Flags: uninsdeletekey

[Run]
; Registration runs during Setup, before the final page, because the user is
; already elevated here. Without it the virtual camera does not work until
; someone works out that a second, administrator-only step is required - the
; single biggest reported rough edge of both distributions.
;
; `runhidden waituntilterminated` keeps it off screen and lets Setup wait for
; the result. There is deliberately no `skipifsilent`, so an unattended install
; registers too. If the user clears the task, or the script fails, the camera is
; still usable after a manual registration from the application's settings.
Filename: "powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\scripts\Install-Native.ps1"" -Action Install"; Tasks: registercamera; Flags: runhidden waituntilterminated

Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent; Tasks: launchapp

[UninstallRun]
; Registration is undone as the FIRST step of uninstallation, while the script
; and the DLLs are still on disk - `regsvr32 /u` needs the file it is
; unregistering. The [Registry] entries above are the backstop for when this
; cannot run at all.
;
; RunOnceId keeps a reinstall from stacking duplicate entries in the uninstall
; log, which would otherwise run this several times.
Filename: "powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\scripts\Install-Native.ps1"" -Action Uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterNativeComponents"

; Uninstall: the generated uninstaller removes every file that was installed,
; using the file list it recorded at install time. There is deliberately NO
; [UninstallDelete] entry for user data, because there is no per-user data
; inside {app} to clean up: the application writes nothing into its own
; installation directory.
;
; User settings are deliberately NOT deleted by default. Configuration, logs and
; crash reports live in %LOCALAPPDATA%\SeewoAssistant, which is outside {app},
; and they should survive an uninstall: reinstalling or upgrading keeps the
; user's configuration and crash history. Deleting them is offered as a question
; instead of being done silently - see [Code]. Inno only ever removes files it
; installed, so anything dropped into the install directory afterwards is left
; alone.

[Code]

var
  { Set by InitializeUninstall and acted on in usPostUninstall, so the deletion
    happens after the files are gone rather than while the application may still
    be using its own log files. }
  DeleteUserData: Boolean;

{ Deletes the COM registration of both native components, in both registry views.

  This duplicates the [Registry] entries above on purpose. That is the one part of
  this installer whose failure is invisible to the user and unrecoverable by them:
  a leftover CLSID keeps a "Seewo Virtual Camera" device in every DirectShow
  enumeration that can never open, and by the time it is noticed the DLLs and the
  scripts are gone. The two mechanisms also fail differently - the [Registry]
  entries depend on how the uninstaller treats keys Setup never created, while this
  calls the API directly - so having both means one of them still works.

  Deleting a key that is not there is not an error, so this is safe whether or not
  the components were ever registered. }
procedure RemoveComRegistration;
begin
  { The 32/64 suffixes are Inno's Pascal Script names, which are not the same as the
    syntax used in the [Registry] section: there the suffix is HKLM32, here it is
    HKEY_LOCAL_MACHINE_32. Verified against the compiler source
    (Projects/Src/Compiler.ScriptFunc.pas), because a wrong name here is a compile
    error and this is not the kind of guess worth making twice. }
  RegDeleteKeyIncludingSubkeys(
    HKEY_LOCAL_MACHINE, 'SOFTWARE\Classes\CLSID\{A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60}');
  RegDeleteKeyIncludingSubkeys(
    HKEY_LOCAL_MACHINE, 'SOFTWARE\Classes\CLSID\{B8F5C3D2-6E4A-4B99-8C7F-2D3E4F5A6B71}');
  RegDeleteKeyIncludingSubkeys(
    HKEY_LOCAL_MACHINE_32, 'SOFTWARE\Classes\CLSID\{A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60}');
  RegDeleteKeyIncludingSubkeys(
    HKEY_LOCAL_MACHINE_32, 'SOFTWARE\Classes\CLSID\{B8F5C3D2-6E4A-4B99-8C7F-2D3E4F5A6B71}');
end;

{ Removes the configuration directory, if the user asked for it. Split out because
  two different steps need it and the path expression is easy to get subtly wrong in
  one of them. }
procedure RemoveUserData;
begin
  DelTree(ExpandConstant('{localappdata}\SeewoAssistant'), True, True, True);
end;

function InitializeUninstall(): Boolean;
begin
  { True, always: nothing here should be able to block an uninstall. }
  Result := True;
  DeleteUserData := False;

  { A silent uninstall cannot ask, and a question with no answer must not destroy
    data. Keeping the configuration is the recoverable choice; deleting it is
    not. `UninstallSilent` also covers /SUPPRESSMSGBOXES, where a message box
    would be suppressed and this function would otherwise never run. }
  if UninstallSilent then
  begin
    Exit;
  end;

  DeleteUserData :=
    SuppressibleMsgBox(
      '是否同时删除个人配置和日志？' + #13#10 + #13#10 +
      '选择「否」会保留下面这个目录，重新安装后设置、日志和崩溃记录都还在：' + #13#10 +
      ExpandConstant('{localappdata}\SeewoAssistant'),
      mbConfirmation, MB_YESNO, IDNO) = IDYES;
end;

{ Says so when the registration the user asked for did not take.

  Without this the failure is completely silent: the [Run] entry is hidden, so an
  install that registered nothing looks exactly like one that worked, and the user
  only finds out later from a camera that is not there - which is the confusing
  half of the original report. Setup is the only party that knows both that the
  task was selected and what the registry looks like afterwards.

  Only informational, and skipped when running unattended, so it can never block an
  installation from finishing. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Registered: Boolean;
begin
  if CurStep <> ssDone then
  begin
    Exit;
  end;

  if WizardSilent or (not WizardIsTaskSelected('registercamera')) then
  begin
    Exit;
  end;

  { Either backend counts: the Media Foundation media source on Windows 11, the
    DirectShow filter on Windows 10. Both register into the 64-bit view, which is
    the one a 64-bit install mode reads by default. }
  Registered :=
    RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\Classes\CLSID\{A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60}') or
    RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\Classes\CLSID\{B8F5C3D2-6E4A-4B99-8C7F-2D3E4F5A6B71}');

  if Registered then
  begin
    Exit;
  end;

  SuppressibleMsgBox(
    '虚拟摄像头组件没有注册成功，其他功能不受影响。' + #13#10 + #13#10 +
    '打开程序，在「设置 → 系统集成」里点「注册到当前目录」可以重试（需要管理员权限）。',
    mbInformation, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { usUninstall runs after [UninstallRun] and before the files are deleted, which is
    the last point at which the registration can still be undone. }
  if CurUninstallStep = usUninstall then
  begin
    RemoveComRegistration;
  end;

  if (CurUninstallStep = usPostUninstall) and DeleteUserData then
  begin
    RemoveUserData;
  end;
end;

{ Warns when the copy being replaced lives somewhere else, and offers to remove
  it.

  The CLSID registration is global, so a second copy re-points it at itself. The
  first copy then still occupies ~150 MB and still looks installed, but is inert,
  and deleting it "to clean up" would break the newer copy because the shared
  registration would be left pointing at the deleted folder. That is exactly the
  reported failure, so the old directory is named before it becomes a mystery.

  Also runs the other way: this is what catches an earlier portable copy when the
  user moves to the installer. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  PreviousPath: String;
  CurrentPath: String;
begin
  Result := '';
  NeedsRestart := False;

  CurrentPath := ExpandConstant('{app}');

  if not RegQueryStringValue(HKLM, 'SOFTWARE\SeewoAssistant', 'InstallPath', PreviousPath) then
  begin
    Exit;
  end;

  if (PreviousPath = '') or (CompareText(PreviousPath, CurrentPath) = 0) then
  begin
    Exit;
  end;

  if not DirExists(PreviousPath) then
  begin
    Exit;
  end;

  if SuppressibleMsgBox(
       '检测到上一次安装在这个目录：' + #13#10 + PreviousPath + #13#10 + #13#10 +
       '本次会安装到：' + #13#10 + CurrentPath + #13#10 + #13#10 +
       '是否删除旧目录？（个人配置不在其中，不会被删除）',
       mbConfirmation, MB_YESNO, IDNO) = IDYES then
  begin
    DelTree(PreviousPath, True, True, True);
  end;
end;
