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
#define AppPublisher "SeewoAssistant contributors"
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

; PrivilegesRequiredOverridesAllowed=dialog puts the "install for all users or
; for me only" choice in front of the user. The default stays per-machine
; (administrator, Program Files); choosing per-user needs no elevation.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

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
; ChineseSimplified.isl is an official Inno Setup translation and ships with
; Inno Setup 6. Only this language is defined: the app itself is Simplified
; Chinese only, and adding English would double every message for no benefit.
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
; Both shortcuts are offered on the "Select Additional Tasks" page and both can
; be cleared. The default (checked/unchecked) is a suggestion only.
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
; {autoprograms} and {autodesktop} follow the install mode chosen on the
; privileges dialog, so a per-user install writes to the user's own Start Menu.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent; Tasks: launchapp

; Uninstall: the generated uninstaller removes every file that was installed,
; using the file list it recorded at install time. There is deliberately NO
; [UninstallDelete] entry for user data, because there is no per-user data
; inside {app} to clean up: the application writes nothing into its own
; installation directory.
;
; User settings are deliberately NOT deleted. Configuration, logs and crash
; reports live in %LOCALAPPDATA%\SeewoAssistant, which is outside {app}, and
; they must survive an uninstall: reinstalling or upgrading should keep the
; user's configuration and crash history, and an uninstall is not the place to
; destroy data the user never asked to delete. Anyone who wants them gone can
; delete that folder by hand; the README says where it is. Inno only ever removes
; files it installed, so anything dropped into the install directory afterwards
; is left alone.
