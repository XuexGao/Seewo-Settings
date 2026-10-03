<#
.SYNOPSIS
    Installs or removes the native components of SeewoAssistant.

.DESCRIPTION
    The managed app works without elevation for the virtual camera instance, the
    privacy monitor and own-window capture protection. The parts that write to
    machine-wide state are gathered here so the user sees one clear prompt instead
    of several unrelated failures:

      * the Media Foundation virtual camera media source (COM registration in HKLM)
      * the DirectShow virtual camera filter (also HKLM)
      * the firewall rules the app creates (created on demand from the UI)

    Run this from an elevated PowerShell prompt.

.PARAMETER Action
    Install, Uninstall, or Status. Defaults to Install.

.PARAMETER Architecture
    x64, x86, or Auto. Defaults to Auto, which follows the current process.

.EXAMPLE
    .\Install-Native.ps1 -Action Install
    Registers both virtual camera backends and creates the camera instance.

.EXAMPLE
    .\Install-Native.ps1 -Action Status
    Reports what is currently registered, without changing anything.
#>

[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall', 'Status', 'Cleanup')]
    [string]$Action = 'Install',

    [ValidateSet('Auto', 'x64', 'x86')]
    [string]$Architecture = 'Auto'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The CLSIDs the native components register. The same two strings appear in
# installer/SeewoAssistant.iss and in the application's SystemIntegrationService,
# because all three have to be able to remove the registration on their own: the
# uninstaller after the payload is gone, this script when only it is left, and the
# application when the user asks it to clean up. A mismatch between the copies is
# silent - every side reports success and the keys stay - so
# SystemIntegrationContractTests compares all three.
$MediaSourceClsid = '{A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60}'
$DirectShowClsid = '{B8F5C3D2-6E4A-4B99-8C7F-2D3E4F5A6B71}'

# CLSID_VideoInputDeviceCategory = {860BB310-5D01-11d0-BD3B-00A0C911CE86}
#
# The category a capture source must also be listed under. DirectShow registration is
# two separate places, and this is the one that actually makes a camera appear:
#   * CLSID\{filter}                     - the COM object, so CoCreateInstance works
#   * CLSID\{category}\Instance\<name>  - the device list ICreateDevEnum walks
# Deleting only the first leaves a device in every camera picker that cannot be opened.
# native/SeewoVirtualCamera.DShow/dllmain.cpp documents and writes both.
$VideoInputDeviceCategoryClsid = '{860BB310-5D01-11d0-BD3B-00A0C911CE86}'

# The FriendlyName the filter registers itself under, and therefore the name of its
# entry in the category. Used to remove that entry by name.
$DirectShowFriendlyName = 'Seewo Virtual Camera'

# Both registry views. A 32-bit host application loads the x86 DirectShow filter,
# which registers under WOW6432Node; removing only the 64-bit keys is what leaves a
# camera that still enumerates for those applications and can never open.
$ClsidRegistryPaths = @(
    'HKLM:\SOFTWARE\Classes\CLSID'
    'HKLM:\SOFTWARE\Classes\WOW6432Node\CLSID'
)

# Where this script records which copy of the application owns the registration.
# The application reads it back to notice that the shared CLSID points at a
# different directory.
$OwnSettingsKey = 'HKLM:\SOFTWARE\SeewoAssistant'

# ---------------------------------------------------------------------- helpers

function Write-Header {
    param([string]$Text)
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Text)
    Write-Host "   [完成] $Text" -ForegroundColor Green
}

function Write-Info {
    param([string]$Text)
    Write-Host "   $Text" -ForegroundColor Gray
}

function Write-Warn {
    param([string]$Text)
    Write-Host "   [警告] $Text" -ForegroundColor Yellow
}

function Write-Fail {
    param([string]$Text)
    Write-Host "   [错误] $Text" -ForegroundColor Red
}

function Test-IsElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-TargetArchitecture {
    if ($Architecture -ne 'Auto') {
        return $Architecture
    }

    # A 64-bit process on 64-bit Windows gets x64. A 32-bit process on 64-bit
    # Windows must still install the 64-bit components, because the app itself is
    # published as x64; the WOW64 check handles that.
    if ([Environment]::Is64BitOperatingSystem) {
        return 'x64'
    }

    return 'x86'
}

# The script lives in <release>/scripts, so the release root is one level up.
function Get-ReleaseRoot {
    $scriptDirectory = Split-Path -Parent $PSCommandPath
    return (Resolve-Path (Join-Path $scriptDirectory '..')).Path
}

function Get-NativePath {
    param(
        [string]$Root,
        [string]$TargetArchitecture
    )

    $folder = if ($TargetArchitecture -eq 'x64') { 'x64' } else { 'x86' }
    return Join-Path $Root "native\$folder"
}

# ---------------------------------------------------------------------- actions

function Show-Status {
    param([string]$Root)

    Write-Header '系统能力'

    $os = Get-CimInstance Win32_OperatingSystem
    $build = [int](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber

    Write-Info "操作系统      : $($os.Caption) (内部版本 $build)"

    if ($build -ge 22000) {
        Write-Ok 'Windows 11：支持 MFCreateVirtualCamera 原生虚拟摄像头'
    }
    elseif ($build -ge 19041) {
        Write-Warn 'Windows 10：不支持官方虚拟摄像头 API，将使用 DirectShow 回退方案'
    }
    else {
        Write-Fail "内部版本 $build 低于 19041，不支持 WDA_EXCLUDEFROMCAPTURE，虚拟摄像头也不可用"
    }

    Write-Header 'COM 组件注册状态'

    foreach ($entry in @(
            @{ Name = 'Media Foundation 媒体源'; Clsid = $MediaSourceClsid },
            @{ Name = 'DirectShow 源滤镜'; Clsid = $DirectShowClsid })) {

        $key = "HKLM:\SOFTWARE\Classes\CLSID\$($entry.Clsid)\InProcServer32"

        if (Test-Path $key) {
            $dll = (Get-ItemProperty $key).'(default)'

            # Flag a registration that points outside this installation. Installing a new
            # copy into a different folder leaves the old CLSID pointing at the previous
            # one, and nothing complained: the status output showed the stale path as if
            # it were fine, so the only way to notice was to read the path closely. On
            # Windows 10 the Media Foundation backend is inert, which is why the leftover
            # did no harm - but the same leftover on Windows 11 would silently load the
            # old DLL, and deleting the old folder would break the camera.
            $isCurrent = -not [string]::IsNullOrWhiteSpace($dll) -and
                         $dll.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)

            if ($isCurrent) {
                Write-Ok "$($entry.Name) 已注册 -> $dll"
            }
            else {
                Write-Warn "$($entry.Name) 指向的不是当前安装目录：$dll"
                Write-Info "       当前安装目录：$Root"
                Write-Info '       重新运行本脚本的 Install 会把注册更新为当前目录。'
            }
        }
        else {
            Write-Info "$($entry.Name) 未注册"
        }
    }

    Write-Header '防火墙规则'

    try {
        $rules = Get-NetFirewallRule -DisplayName 'SeewoAssistant Block*' -ErrorAction SilentlyContinue
        if ($rules) {
            Write-Info "本程序创建的规则：$($rules.Count) 条"
        }
        else {
            Write-Info '本程序没有创建任何防火墙规则'
        }
    }
    catch {
        Write-Warn "无法查询防火墙规则：$($_.Exception.Message)"
    }

    Write-Header '原生文件'

    $nativePath = Get-NativePath -Root $Root -TargetArchitecture (Get-TargetArchitecture)
    if (Test-Path $nativePath) {
        Get-ChildItem $nativePath -File | ForEach-Object {
            Write-Info "$($_.Name)  ($([math]::Round($_.Length / 1KB, 1)) KB)"
        }
    }
    else {
        Write-Warn "未找到原生组件目录：$nativePath"
    }
}

function Remove-ComRegistrationKeys {
    # Deletes the CLSID keys outright, without regsvr32 and without needing the DLL.
    #
    # This is the case that produced the reported defect. `regsvr32 /u` cannot
    # unregister a file that is no longer there, and the previous version only ran it
    # `if (Test-Path $dshowDll)` - so an uninstall or a cleanup performed after the
    # payload was gone silently removed nothing. What survived was worse than a stale
    # key: every DirectShow enumeration still listed "Seewo Virtual Camera", selecting
    # it always failed, and the only tool that could remove it (this script) had just
    # been deleted together with the install directory.
    #
    # Returns the number of entries it could not remove, so a caller can tell a clean
    # machine from a failed cleanup.
    $removed = 0
    $failed = 0

    foreach ($clsid in @($MediaSourceClsid, $DirectShowClsid)) {
        foreach ($base in $ClsidRegistryPaths) {
            $key = Join-Path $base $clsid

            if (-not (Test-Path $key)) {
                continue
            }

            Remove-Item -Path $key -Recurse -Force -ErrorAction SilentlyContinue | Out-Null

            if (Test-Path $key) {
                Write-Warn "无法删除注册项 $key（可能被占用或权限不足）"
                $failed++
            }
            else {
                Write-Ok "已删除注册项 $key"
                $removed++
            }
        }
    }

    # Each helper returns how many entries it could NOT remove. Returning the number of
    # successes instead - which is what this did - makes the value useless to a caller
    # that needs to know whether the cleanup worked: "0 removed" and "3 failed" look
    # identical from the outside, so every caller ended up ignoring it and
    # Cleanup-Native could only ever report success.
    $failed += Remove-DirectShowCategoryEntries

    if (($removed -eq 0) -and ($failed -eq 0)) {
        Write-Info '没有需要删除的 COM 注册项'
    }

    return $failed
}

function Remove-DirectShowCategoryEntries {
    # Removes the filter's entry from the video-input-device category.
    #
    # Deleting the filter's own CLSID key is not enough to make the camera disappear,
    # and this is exactly how that was discovered: `-Action Uninstall` runs
    # `regsvr32 /u`, which removes both places, while `-Action Cleanup` deleted the
    # CLSID key directly and left the category entry behind. The result looked clean
    # and still listed "Seewo Virtual Camera" in every DirectShow enumeration, pointing
    # at a CLSID that no longer existed - the same phantom device, reached by the other
    # of the two removal paths.
    #
    # Both the name and the CLSID are matched. The name is what this program registers,
    # but a stale entry from an older version could carry a different CLSID value, and
    # an entry whose CLSID points at us under a different name would otherwise survive.
    #
    # Returns the number of entries it could not remove, like its caller.
    $removed = 0
    $failed = 0

    foreach ($base in $ClsidRegistryPaths) {
        $instanceRoot = Join-Path (Join-Path $base $VideoInputDeviceCategoryClsid) 'Instance'

        if (-not (Test-Path $instanceRoot)) {
            continue
        }

        foreach ($entry in @(Get-ChildItem -Path $instanceRoot -ErrorAction SilentlyContinue)) {
            $value = $null

            try {
                $value = (Get-ItemProperty -Path $entry.PSPath -ErrorAction Stop).CLSID
            }
            catch {
                # An entry with no CLSID value cannot belong to this filter; leave it.
                continue
            }

            $isOurs = $entry.PSChildName -eq $DirectShowFriendlyName -or
                      "$value" -eq $DirectShowClsid

            if (-not $isOurs) {
                continue
            }

            Remove-Item -Path $entry.PSPath -Recurse -Force -ErrorAction SilentlyContinue | Out-Null

            if (Test-Path $entry.PSPath) {
                Write-Warn "无法删除分类注册项 $($entry.PSPath)"
                $failed++
            }
            else {
                Write-Ok "已删除分类注册项 $($entry.PSChildName)"
                $removed++
            }
        }
    }

    return $failed
}

function Remove-FirewallRules {
    # Returns the number of rules that are still there afterwards.
    $rules = Get-NetFirewallRule -DisplayName 'SeewoAssistant Block*' -ErrorAction SilentlyContinue

    if (-not $rules) {
        Write-Info '没有需要移除的防火墙规则'
        return 0
    }

    $rules | Remove-NetFirewallRule | Out-Null
    Write-Ok "已移除 $($rules.Count) 条防火墙规则"

    # Removal is not always immediate, so this asks the system rather than assuming the
    # cmdlet succeeding meant the rules are gone.
    $left = @(Get-NetFirewallRule -DisplayName 'SeewoAssistant Block*' -ErrorAction SilentlyContinue)

    if ($left.Count -gt 0) {
        Write-Warn "仍有 $($left.Count) 条防火墙规则没有移除"
    }

    return $left.Count
}

function Remove-OwnSettingsKey {
    # Returns 1 when the key survived, 0 otherwise, matching the other removal helpers.
    if (-not (Test-Path $OwnSettingsKey)) {
        return 0
    }

    Remove-Item -Path $OwnSettingsKey -Recurse -Force -ErrorAction SilentlyContinue | Out-Null

    if (Test-Path $OwnSettingsKey) {
        Write-Warn "无法删除 $OwnSettingsKey"
        return 1
    }

    Write-Ok '已删除本程序记录的系统集成信息'
    return 0
}

function Write-InstallRecord {
    param([string]$Root)

    # Written before the registration is attempted rather than after, so the record
    # also exists on a machine where the camera could not be created (Windows 10
    # without a capture device, camera access switched off). "Which copy owns the
    # registration" is a separate fact from "did the camera start".
    if (-not (Test-Path $OwnSettingsKey)) {
        New-Item -Path $OwnSettingsKey -Force | Out-Null
    }

    New-ItemProperty -Path $OwnSettingsKey -Name 'InstallPath' -Value $Root `
        -PropertyType String -Force | Out-Null

    $exe = Join-Path $Root 'SeewoAssistant.exe'
    if (Test-Path $exe) {
        $version = (Get-Item $exe).VersionInfo.FileVersion
        if (-not [string]::IsNullOrWhiteSpace($version)) {
            New-ItemProperty -Path $OwnSettingsKey -Name 'Version' -Value $version `
                -PropertyType String -Force | Out-Null
        }
    }
}

function Install-Native {
    param([string]$Root)

    $targetArchitecture = Get-TargetArchitecture
    $nativePath = Get-NativePath -Root $Root -TargetArchitecture $targetArchitecture

    Write-Header "安装原生组件（$targetArchitecture）"

    Write-InstallRecord -Root $Root

    if (-not (Test-Path $nativePath)) {
        Write-Fail "未找到原生组件目录：$nativePath"
        Write-Info '请确认使用完整的发行包，而不是只复制了单个 exe。'
        return 1
    }

    $build = [int](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber
    $setupTool = Join-Path $nativePath 'SeewoVirtualCamera.Setup.exe'

    # The Media Foundation path only exists on Windows 11 22000+. On Windows 10 the
    # DirectShow filter is registered instead; registering the MF media source there
    # would succeed but produce a camera nothing can activate.
    if ($build -ge 22000) {
        Write-Header '注册 Media Foundation 虚拟摄像头'

        if (Test-Path $setupTool) {
            & $setupTool install
            $installExit = $LASTEXITCODE

            if ($installExit -eq 0) {
                Write-Ok 'Media Foundation 虚拟摄像头已安装'
            }
            else {
                # Translate the tool's exit codes rather than only echoing the
                # number, so the reader knows whether anything is actually broken.
                # See native/SeewoVirtualCamera.Setup/main.cpp for the definitions.
                $explanation = switch ($installExit) {
                    1 { '注册 COM 组件失败，通常是没有以管理员身份运行。' }
                    2 { '找不到 SeewoVirtualCamera.dll，发行包可能不完整。' }
                    3 { '本机不支持 MFCreateVirtualCamera（需要 Windows 11 内部版本 22000 或更高）。' }
                    5 { 'MFCreateVirtualCamera 调用失败，最常见的原因是系统相机访问被关闭。' }
                    6 { '摄像头未能注册到系统。COM 组件已注册成功，但 Start 失败；多数情况下是系统相机访问被关闭，或该环境没有视频设备（虚拟机、远程会话、Server Core 常见）。' }
                    default { '未知错误。' }
                }

                Write-Fail "Media Foundation 虚拟摄像头安装未完成（退出码 $installExit）：$explanation"

                if ($installExit -in 5, 6) {
                    Write-Info 'COM 组件注册本身是成功的。修好相机访问后，可以只运行以下命令重试创建：'
                    Write-Info "  & `"$setupTool`" create"
                }
            }
        }
        else {
            Write-Fail "未找到 $setupTool"
        }
    }
    else {
        Write-Header '注册 DirectShow 虚拟摄像头（Windows 10 回退方案）'

        $dshowDll = Join-Path $nativePath 'SeewoVirtualCamera.DShow.dll'

        if (Test-Path $dshowDll) {
            # regsvr32 runs DllRegisterServer, which writes every key a DirectShow
            # capture device needs.
            $process = Start-Process regsvr32.exe -ArgumentList '/s', "`"$dshowDll`"" -Wait -PassThru

            if ($process.ExitCode -eq 0) {
                Write-Ok 'DirectShow 虚拟摄像头已注册'
                Write-Info '它不会出现在 Windows「设置」的摄像头列表中，但 Zoom / OBS / ffmpeg 等 DirectShow 应用可以看到。'
            }
            else {
                Write-Fail "regsvr32 返回退出码 $($process.ExitCode)"
            }
        }
        else {
            Write-Fail "未找到 $dshowDll"
        }
    }

    Write-Header '安装完成'
    Write-Info '现在可以直接运行 SeewoAssistant.exe。'
    Write-Info '提示：跨进程防截屏、防火墙规则、计划任务修改和定时关机需要以管理员身份运行主程序。'

    return 0
}

function Uninstall-Native {
    param([string]$Root)

    $targetArchitecture = Get-TargetArchitecture
    $nativePath = Get-NativePath -Root $Root -TargetArchitecture $targetArchitecture

    Write-Header '移除虚拟摄像头'

    $setupTool = Join-Path $nativePath 'SeewoVirtualCamera.Setup.exe'
    if (Test-Path $setupTool) {
        # Best effort: the camera may never have been created, which is not an error.
        & $setupTool uninstall
        Write-Ok '已调用卸载工具'
    }
    else {
        Write-Info '未找到安装工具，跳过'
    }

    Write-Header '反注册 DirectShow 滤镜'

    $dshowDll = Join-Path $nativePath 'SeewoVirtualCamera.DShow.dll'
    if (Test-Path $dshowDll) {
        Start-Process regsvr32.exe -ArgumentList '/u', '/s', "`"$dshowDll`"" -Wait
        Write-Ok '已反注册 DirectShow 滤镜'
    }
    else {
        Write-Info '未找到 DirectShow 滤镜，跳过'
    }

    # After the best-effort unregister above, delete the keys outright. This covers
    # both the case where the DLL was already missing (where regsvr32 above was
    # skipped entirely) and the case where it existed but refused to unregister.
    Write-Header '删除 COM 注册项'

    $failures = Remove-ComRegistrationKeys

    Write-Header '移除这个工具创建的防火墙规则'

    try {
        $failures += Remove-FirewallRules
    }
    catch {
        Write-Warn "移除防火墙规则失败：$($_.Exception.Message)"
        $failures++
    }

    Write-Header '删除系统集成记录'

    $failures += Remove-OwnSettingsKey

    Write-Header '卸载完成'
    Write-Info '配置文件保留在 %LOCALAPPDATA%\SeewoAssistant，如需彻底清理可手动删除。'

    if ($failures -gt 0) {
        Write-Fail "有 $failures 处没有清理成功，请以管理员身份重新运行本脚本。"
        return 1
    }

    return 0
}

function Cleanup-Native {
    param([string]$Root)

    # Unlike Uninstall, this deliberately does not care whether the payload is still
    # present, which copy it belongs to, or whether this directory is the one that
    # registered the components. It is the "get this machine back to a clean state"
    # action: the entry point for a copy that was moved, was deleted by hand, or lost
    # the race with a second copy over the shared CLSID.
    Write-Header '清除这个工具对系统的改动'

    Write-Header 'COM 组件注册'

    $failures = Remove-ComRegistrationKeys

    Write-Header '防火墙规则'

    try {
        $failures += Remove-FirewallRules
    }
    catch {
        Write-Warn "移除防火墙规则失败：$($_.Exception.Message)"
        $failures++
    }

    Write-Header '系统集成记录'

    $failures += Remove-OwnSettingsKey

    Write-Info '个人配置和日志没有删除，仍在 %LOCALAPPDATA%\SeewoAssistant。'
    Write-Info "这个工具的文件也没有删除，仍在 $Root。"

    # The exit code is what Uninstall-Portable.ps1 branches on before it tells the user
    # they can delete the folder, so it has to reflect reality. Returning 0
    # unconditionally - which is what this did - made that branch dead code and made the
    # promise below unconditional too.
    if ($failures -gt 0) {
        Write-Fail "有 $failures 处没有清理成功，请以管理员身份重新运行本脚本。"
        Write-Info '在这些项目清理成功之前，请不要删除程序目录。'
        return 1
    }

    Write-Header '清理完成'
    Write-Info '虚拟摄像头组件、防火墙规则和系统集成记录都已清除，可以安全删除程序目录了。'

    return 0
}

# ---------------------------------------------------------------------- entry

# $IsWindows only exists in PowerShell 6 and later, and this script enables
# Set-StrictMode -Version Latest, under which reading an undefined variable throws
# VariableIsUndefined. The version test must therefore come first: with `-and` the left
# operand is evaluated before the right, so the original order threw on Windows
# PowerShell 5.1 - the default shell on a stock Chinese Windows 10 - before the version
# check could protect it. The script then failed to run at all.
$isPowerShellCore = $PSVersionTable.PSVersion.Major -ge 6

if ($isPowerShellCore -and -not $IsWindows) {
    Write-Fail '此脚本只能在 Windows 上运行。'
    exit 1
}

$releaseRoot = Get-ReleaseRoot
$targetArchitecture = Get-TargetArchitecture

if ($Action -eq 'Status') {
    Show-Status -Root $releaseRoot
    exit 0
}

if (-not (Test-IsElevated)) {
    Write-Fail '此操作需要管理员权限。'
    Write-Info '请右键点击 PowerShell，选择「以管理员身份运行」，然后重新执行本脚本。'
    exit 1
}

try {
    switch ($Action) {
        'Install' { $exitCode = Install-Native -Root $releaseRoot }
        'Uninstall' { $exitCode = Uninstall-Native -Root $releaseRoot }
        'Cleanup' { $exitCode = Cleanup-Native -Root $releaseRoot }
    }

    exit $exitCode
}
catch {
    Write-Fail $_.Exception.Message
    exit 1
}
