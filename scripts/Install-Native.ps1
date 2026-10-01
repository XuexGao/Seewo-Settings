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
    [ValidateSet('Install', 'Uninstall', 'Status')]
    [string]$Action = 'Install',

    [ValidateSet('Auto', 'x64', 'x86')]
    [string]$Architecture = 'Auto'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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

    $mediaSourceClsid = '{A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60}'
    $dshowClsid = '{B8F5C3D2-6E4A-4B99-8C7F-2D3E4F5A6B71}'

    foreach ($entry in @(
            @{ Name = 'Media Foundation 媒体源'; Clsid = $mediaSourceClsid },
            @{ Name = 'DirectShow 源滤镜'; Clsid = $dshowClsid })) {

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

function Install-Native {
    param([string]$Root)

    $targetArchitecture = Get-TargetArchitecture
    $nativePath = Get-NativePath -Root $Root -TargetArchitecture $targetArchitecture

    Write-Header "安装原生组件（$targetArchitecture）"

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

    Write-Header '注销 DirectShow 滤镜'

    $dshowDll = Join-Path $nativePath 'SeewoVirtualCamera.DShow.dll'
    if (Test-Path $dshowDll) {
        Start-Process regsvr32.exe -ArgumentList '/u', '/s', "`"$dshowDll`"" -Wait
        Write-Ok '已注销 DirectShow 滤镜'
    }
    else {
        Write-Info '未找到 DirectShow 滤镜，跳过'
    }

    Write-Header '移除本程序创建的防火墙规则'

    try {
        $rules = Get-NetFirewallRule -DisplayName 'SeewoAssistant Block*' -ErrorAction SilentlyContinue
        if ($rules) {
            $rules | Remove-NetFirewallRule
            Write-Ok "已移除 $($rules.Count) 条规则"
        }
        else {
            Write-Info '没有需要移除的规则'
        }
    }
    catch {
        Write-Warn "移除防火墙规则失败：$($_.Exception.Message)"
    }

    Write-Header '卸载完成'
    Write-Info '配置文件保留在 %LOCALAPPDATA%\SeewoAssistant，如需彻底清理可手动删除。'

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
    }

    exit $exitCode
}
catch {
    Write-Fail $_.Exception.Message
    exit 1
}
