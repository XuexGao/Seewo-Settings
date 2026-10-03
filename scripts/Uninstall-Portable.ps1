﻿#Requires -Version 5.1
<#
.SYNOPSIS
    Removes the changes the portable (zip) copy of 希沃助手 made to this machine.

.DESCRIPTION
    The zip distribution has no installer, so nothing owned its removal. Deleting the
    extracted folder left the virtual camera registered in the system: the CLSID keys
    stayed behind pointing at a DLL that no longer existed, every DirectShow
    enumeration still listed "Seewo Virtual Camera", and the script that could have
    removed it had just been deleted along with the folder.

    This script is that missing owner. Run it before deleting the folder - or after,
    it still works, because the cleanup does not need the payload to be present.

    The per-user work (shortcuts, the "Apps & features" entry) runs first and
    unelevated, then the machine-wide work runs elevated. That order matters: an
    elevated process gets a different token, and with over-the-shoulder elevation
    (a standard user typing an administrator's credentials) HKCU belongs to the
    administrator, so shortcuts and registry entries would land in the wrong profile.

.PARAMETER KeepUserData
    Keep %LOCALAPPDATA%\SeewoAssistant (settings, logs, crash reports). Without this
    switch the script asks, and the answer defaults to keeping them.

.PARAMETER DeleteFolder
    Delete the application folder too, after the cleanup. Off by default: the folder
    contains this script, and deleting the tree it is running from is the kind of
    thing that should be asked for explicitly.

.EXAMPLE
    .\Uninstall-Portable.ps1
    Removes everything this copy registered with the system. The folder is left.
#>

[CmdletBinding()]
param(
    [switch]$KeepUserData,
    [switch]$DeleteFolder
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$AppDisplayName = '希沃助手'
$AppPublisher = 'XuexGao'
# Matches SystemIntegrationService and installer/SeewoAssistant.iss. Kept per-user:
# the portable copy is not installed for the machine, and writing this to HKLM would
# silently require elevation just to list the program.
$AppsAndFeaturesKey = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SeewoAssistant'
$UserDataPath = Join-Path $env:LOCALAPPDATA 'SeewoAssistant'

function Write-Header {
    param([string]$Text)
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Text)
    Write-Host "   [OK]   $Text" -ForegroundColor Green
}

function Write-Info {
    param([string]$Text)
    Write-Host "   $Text"
}

function Write-Warn {
    param([string]$Text)
    Write-Host "   [警告] $Text" -ForegroundColor Yellow
}

function Test-IsElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------------------------------------------------------------------- per user

function Remove-Shortcuts {
    Write-Header '移除快捷方式'

    $targets = @(
        (Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppDisplayName.lnk")
        (Join-Path ([Environment]::GetFolderPath('Programs')) "$AppDisplayName.lnk")
    )

    $removed = 0

    foreach ($target in $targets) {
        if (Test-Path $target) {
            Remove-Item -Path $target -Force -ErrorAction SilentlyContinue

            if (Test-Path $target) {
                Write-Warn "无法删除 $target"
            }
            else {
                Write-Ok "已删除 $target"
                $removed++
            }
        }
    }

    if ($removed -eq 0) {
        Write-Info '没有本程序创建的快捷方式'
    }
}

function Remove-AppsAndFeaturesEntry {
    Write-Header '从「应用和功能」中移除'

    if (Test-Path $AppsAndFeaturesKey) {
        Remove-Item -Path $AppsAndFeaturesKey -Recurse -Force -ErrorAction SilentlyContinue

        if (Test-Path $AppsAndFeaturesKey) {
            Write-Warn '无法删除「应用和功能」条目'
        }
        else {
            Write-Ok '已从「应用和功能」中移除'
        }
    }
    else {
        Write-Info '本来就没有登记在「应用和功能」中'
    }
}

# ---------------------------------------------------------------------- per machine

function Invoke-ElevatedCleanup {
    param([string]$Root)

    Write-Header '反注册虚拟摄像头组件（需要管理员权限）'

    if (Test-IsElevated) {
        & (Join-Path $Root 'scripts\Install-Native.ps1') -Action Cleanup
        return $LASTEXITCODE
    }

    Write-Info '正在请求管理员权限，请在系统弹出的窗口中确认。'

    $scriptPath = Join-Path $Root 'scripts\Install-Native.ps1'
    $arguments = @(
        '-NoProfile'
        '-ExecutionPolicy'
        'Bypass'
        '-File'
        "`"$scriptPath`""
        '-Action'
        'Cleanup'
    ) -join ' '

    try {
        $process = Start-Process powershell.exe -ArgumentList $arguments -Verb RunAs -Wait -PassThru
        return $process.ExitCode
    }
    catch {
        # 1223 is ERROR_CANCELLED: the user dismissed the UAC prompt. That is a
        # choice, not a failure, and it needs a different message from a broken
        # cleanup - otherwise the user is told something went wrong when nothing did.
        #
        # Read through PSObject rather than straight off the exception: Set-StrictMode
        # -Version Latest makes a property access on an object that does not have it a
        # terminating error, and the exception PowerShell wraps this failure in is not
        # always a Win32Exception.
        $nativeCode = 0
        if ($_.Exception.PSObject.Properties['NativeErrorCode']) {
            $nativeCode = $_.Exception.NativeErrorCode
        }

        if ($nativeCode -eq 1223) {
            Write-Warn '已取消提权，系统级的注册项没有清除。'
        }
        else {
            Write-Warn "提权失败：$($_.Exception.Message)"
        }

        return 1
    }
}

# ---------------------------------------------------------------------- entry

$releaseRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSCommandPath) '..')).Path

Write-Host ''
Write-Host "$AppDisplayName：解压版清理" -ForegroundColor White
Write-Info "程序目录：$releaseRoot"

Remove-Shortcuts
Remove-AppsAndFeaturesEntry

$cleanupExit = Invoke-ElevatedCleanup -Root $releaseRoot

# Asked after the cleanup, so a user who only wanted the system side cleaned up can
# answer without having had to read the question first.
if (-not $KeepUserData) {
    Write-Header '个人配置和日志'

    if (Test-Path $UserDataPath) {
        Write-Info "设置、日志和崩溃记录在：$UserDataPath"
        $answer = Read-Host '是否同时删除它们？保留可以直接重装后继续用 (y/N)'

        if ($answer -eq 'y' -or $answer -eq 'Y') {
            Remove-Item -Path $UserDataPath -Recurse -Force -ErrorAction SilentlyContinue

            if (Test-Path $UserDataPath) {
                Write-Warn "无法完整删除 $UserDataPath"
            }
            else {
                Write-Ok '已删除个人配置和日志'
            }
        }
        else {
            Write-Info '已保留。'
        }
    }
    else {
        Write-Info '没有个人配置和日志'
    }
}

# The folder contains this script, so removing it is left as an explicit request
# rather than something that happens because the script was double-clicked.
Write-Header '程序文件'

# The cleanup result is decided before anything is said about deleting the folder. The
# previous order printed "deleting this folder will not leave anything behind" first and
# checked the exit code afterwards, so the sentence was printed even when the cleanup had
# failed - telling the user to do the one thing that turns a failed cleanup into a
# phantom camera. A promise about the machine's state has to be made after the state is
# known, not before.
if ($cleanupExit -ne 0) {
    Write-Header '清理未完成'
    Write-Warn '系统级的组件注册没有清除干净，请以管理员身份重新运行本脚本。'
    Write-Info "程序目录仍在：$releaseRoot"
    Write-Info '在清理成功之前请不要删除这个目录：反注册组件的脚本就在里面。'
    Write-Host ''
    exit $cleanupExit
}

if ($DeleteFolder) {
    Write-Info "待删除：$releaseRoot"
    Write-Info '本脚本正在从这个目录运行，删除会交给一个在它退出后才执行的命令。'

    Start-Process cmd.exe -ArgumentList @(
        '/c'
        'timeout /t 2 /nobreak >nul &'
        "rmdir /s /q `"$releaseRoot`""
    ) -join ' ' -WindowStyle Hidden

    Write-Ok '已安排在脚本退出后删除程序目录'
}
else {
    Write-Info "程序文件仍在：$releaseRoot"
    Write-Info '已经反注册了虚拟摄像头组件，并清除了这个工具创建的防火墙规则和系统集成记录。'
    Write-Info '现在可以删除这个目录，或加 -DeleteFolder 重新运行由脚本删除。'
}

Write-Host ''
Write-Ok '清理完成。'
exit 0
