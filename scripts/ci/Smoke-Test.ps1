<#
.SYNOPSIS
    Launches SeewoAssistant on a build agent, walks every page, and screenshots each
    one.

.DESCRIPTION
    A build that compiles proves the code type-checks; it does not prove the app
    starts. This script closes that gap by actually running the published binary and
    exercising the UI, which is the only way to catch the failures a compiler cannot
    see:

      * a {StaticResource} key that does not exist (throws while parsing the XAML)
      * an x:Name the code-behind expects but the XAML does not define
      * a null reference in a page's OnServicesReady
      * a service that throws during construction or on its first call

    Every page is navigated to, because each one loads its own XAML and runs its own
    initialisation. A page that throws shows up as a missing screenshot plus an entry
    in the app's own log.

    Screenshots are captured with BitBlt from the screen rather than PrintWindow:
    WinUI 3 composites through DirectComposition, so PrintWindow frequently returns
    an all-black bitmap for it, whereas reading the composited desktop returns what
    the user actually sees.

    Nothing here is fatal on its own. The script collects what it can, always writes
    the app log, and fails at the end only if the app never produced a window.

.PARAMETER AppPath
    Full path to SeewoAssistant.exe.

.PARAMETER OutputDirectory
    Directory to write the screenshots and the collected app log into.

.PARAMETER StartupTimeoutSeconds
    How long to wait for the main window to appear.

.EXAMPLE
    .\Smoke-Test.ps1 -AppPath .\SeewoAssistant.exe -OutputDirectory .\screenshots
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AppPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [int]$StartupTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------- results

$script:Findings = [System.Collections.Generic.List[string]]::new()
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Write-Step {
    param([string]$Text)
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Write-Pass {
    param([string]$Text)
    Write-Host "   [OK]   $Text" -ForegroundColor Green
    $script:Findings.Add("OK: $Text")
}

function Write-Warn {
    param([string]$Text)
    Write-Host "   [警告] $Text" -ForegroundColor Yellow
    $script:Findings.Add("WARN: $Text")
}

function Write-Fail {
    param([string]$Text)
    Write-Host "   [失败] $Text" -ForegroundColor Red
    $script:Findings.Add("FAIL: $Text")
    $script:Failures.Add($Text)
}

# ---------------------------------------------------------------------- interop

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class NativeCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SW_MAXIMIZE = 3;
    public const int SW_RESTORE = 9;
    public const int SRCCOPY = 0x00CC0020;
}
'@

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# ---------------------------------------------------------------------- capture

function Save-ScreenRegion {
    param(
        [string]$Path,
        [int]$X,
        [int]$Y,
        [int]$Width,
        [int]$Height
    )

    if ($Width -le 0 -or $Height -le 0) {
        return $false
    }

    $desktop = [NativeCapture]::GetDesktopWindow()
    $sourceDc = [NativeCapture]::GetWindowDC($desktop)
    $memoryDc = [NativeCapture]::CreateCompatibleDC($sourceDc)
    $bitmap = [NativeCapture]::CreateCompatibleBitmap($sourceDc, $Width, $Height)

    try {
        $previous = [NativeCapture]::SelectObject($memoryDc, $bitmap)

        # SRCCOPY blits the composited desktop, which is what includes WinUI content.
        $copied = [NativeCapture]::BitBlt(
            $memoryDc, 0, 0, $Width, $Height, $sourceDc, $X, $Y, [NativeCapture]::SRCCOPY)

        if (-not $copied) {
            return $false
        }

        $image = [System.Drawing.Image]::FromHbitmap($bitmap)
        try {
            $image.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $image.Dispose()
        }

        [NativeCapture]::SelectObject($memoryDc, $previous) | Out-Null
        return $true
    }
    finally {
        [NativeCapture]::DeleteObject($bitmap) | Out-Null
        [NativeCapture]::DeleteDC($memoryDc) | Out-Null
        [NativeCapture]::ReleaseDC($desktop, $sourceDc) | Out-Null
    }
}

function Save-WindowScreenshot {
    param(
        [IntPtr]$Handle,
        [string]$Path
    )

    $rect = New-Object NativeCapture+RECT
    if (-not [NativeCapture]::GetWindowRect($Handle, [ref]$rect)) {
        return $false
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top

    # Clamp to the desktop: BitBlt cannot read outside it, and a window larger than
    # the screen would otherwise produce a bitmap with undefined regions.
    $screenWidth = [NativeCapture]::GetSystemMetrics([NativeCapture]::SM_CXSCREEN)
    $screenHeight = [NativeCapture]::GetSystemMetrics([NativeCapture]::SM_CYSCREEN)

    $x = [Math]::Max(0, $rect.Left)
    $y = [Math]::Max(0, $rect.Top)
    $width = [Math]::Min($width, $screenWidth - $x)
    $height = [Math]::Min($height, $screenHeight - $y)

    return (Save-ScreenRegion -Path $Path -X $x -Y $y -Width $width -Height $height)
}

# ---------------------------------------------------------------------- ui automation

function Get-MainWindow {
    param([int]$ProcessId, [int]$TimeoutSeconds)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)

        if ($null -ne $window) {
            return $window
        }

        Start-Sleep -Milliseconds 500
    }

    return $null
}

function Invoke-NavigationItem {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$Name
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)

    $item = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)

    if ($null -eq $item) {
        return $false
    }

    # A WinUI NavigationViewItem exposes SelectionItemPattern; if a future template
    # change makes it a plain invokable item, InvokePattern still works. Try the
    # typed patterns directly rather than looking them up by numeric id.
    try {
        $selection = $item.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selection.Select()
        return $true
    }
    catch {
        # Fall through to InvokePattern.
    }

    try {
        $invoke = $item.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)
        $invoke.Invoke()
        return $true
    }
    catch {
        return $false
    }
}

# ---------------------------------------------------------------------- main

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# Without this the bitmaps come out at the wrong scale on a DPI-scaled desktop.
[NativeCapture]::SetProcessDPIAware() | Out-Null

$screenWidth = [NativeCapture]::GetSystemMetrics([NativeCapture]::SM_CXSCREEN)
$screenHeight = [NativeCapture]::GetSystemMetrics([NativeCapture]::SM_CYSCREEN)

Write-Step '运行环境'
Write-Host "   桌面分辨率 : ${screenWidth}x${screenHeight}"
Write-Host "   应用路径   : $AppPath"
Write-Host "   输出目录   : $OutputDirectory"
Write-Host "   会话 ID    : $((Get-Process -Id $PID).SessionId)"
Write-Host "   交互式会话 : $([Environment]::UserInteractive)"

# The app clamps its window to the work area, so a smaller desktop is not a problem
# in itself; this is informational, and it also records the desktop size in the
# findings in case a layout issue turns out to be resolution-dependent.
if ($screenWidth -lt 1280 -or $screenHeight -lt 900) {
    Write-Host "   提示       : 桌面较小，应用会把窗口收缩到工作区内。"
}

Write-Step '启动应用'

if (-not (Test-Path $AppPath)) {
    Write-Fail "找不到可执行文件：$AppPath"
    exit 1
}

$appDirectory = Split-Path -Parent $AppPath

# The app writes its log and settings under %LOCALAPPDATA%\SeewoAssistant. Clearing
# them first means this run exercises the genuine first-launch path, including the
# "no settings file yet" branch.
$stateDirectory = Join-Path $env:LOCALAPPDATA 'SeewoAssistant'
if (Test-Path $stateDirectory) {
    Remove-Item $stateDirectory -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "   已清除上次运行的配置：$stateDirectory"
}

$process = Start-Process -FilePath $AppPath -WorkingDirectory $appDirectory -PassThru

Write-Host "   进程已启动，PID = $($process.Id)"

$window = Get-MainWindow -ProcessId $process.Id -TimeoutSeconds $StartupTimeoutSeconds

if ($null -eq $window) {
    $exited = $process.HasExited

    if ($exited) {
        Write-Fail "应用在 $StartupTimeoutSeconds 秒内退出，退出码 $($process.ExitCode)。"
    }
    else {
        Write-Fail "应用在 $StartupTimeoutSeconds 秒内没有创建主窗口。"
    }

    # Still capture the desktop: an error dialog is itself the diagnostic.
    Save-ScreenRegion -Path (Join-Path $OutputDirectory '00-desktop.png') `
        -X 0 -Y 0 -Width $screenWidth -Height $screenHeight | Out-Null

    if (-not $exited) { $process.Kill() }
    exit 1
}

Write-Pass "主窗口已创建：$($window.Current.Name)"

# Bring it forward and give it the whole desktop so the screenshot is not clipped by
# the app's default 1180x820 size on a smaller screen.
try {
    [NativeCapture]::ShowWindow($process.MainWindowHandle, [NativeCapture]::SW_MAXIMIZE) | Out-Null
    [NativeCapture]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    Start-Sleep -Seconds 2
}
catch {
    Write-Warn "无法最大化窗口：$($_.Exception.Message)"
}

Write-Step '逐页浏览并截图'

# Each entry is a navigation label and the file name to save. Every page loads its
# own XAML and runs its own initialisation, so visiting all of them covers all the
# page-level startup paths.
$pages = @(
    @{ Name = '虚拟摄像头';   File = '01-virtual-camera.png' },
    @{ Name = '隐私监控';     File = '02-privacy.png' },
    @{ Name = '防截屏保护';   File = '03-capture-guard.png' },
    @{ Name = '希沃软件';     File = '04-seewo.png' },
    @{ Name = '定时任务';     File = '05-schedule.png' },
    @{ Name = '日志与诊断';   File = '06-diagnostics.png' },
    @{ Name = '设置';         File = '07-settings.png' }
)

$capturedCount = 0

foreach ($page in $pages) {
    $path = Join-Path $OutputDirectory $page.File

    $navigated = Invoke-NavigationItem -Window $window -Name $page.Name

    if (-not $navigated) {
        Write-Fail "找不到导航项「$($page.Name)」，无法进入该页面。"
        continue
    }

    # Let the page load, run OnServicesReady and render before capturing.
    Start-Sleep -Seconds 3

    # Re-acquire the window handle: the previous page may have been replaced.
    try {
        [NativeCapture]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    }
    catch {
        # Not fatal; the screenshot below may be partially obscured.
    }

    $saved = Save-WindowScreenshot -Handle $process.MainWindowHandle -Path $path

    if ($saved) {
        $size = (Get-Item $path).Length
        Write-Pass "已截图「$($page.Name)」-> $($page.File) ($size 字节)"
        $capturedCount++
    }
    else {
        Write-Fail "「$($page.Name)」截图失败。"
    }
}

# Also grab the whole desktop once, which shows the window in context and would
# reveal anything the app drew outside its own window.
Save-ScreenRegion -Path (Join-Path $OutputDirectory '08-full-desktop.png') `
    -X 0 -Y 0 -Width $screenWidth -Height $screenHeight | Out-Null

Write-Step '逐页交互'

# Click every button that is safe to click, on every page, and verify the app is
# still alive afterwards.
#
# This is the check that was missing. Merely rendering each page proves the XAML
# parses; it does not execute the code behind the buttons, which is where the crashes
# were: the banner window and the test-pattern push both faulted only when invoked.
# A button that kills the process is now a failed check rather than something a user
# discovers.
#
# Buttons that would shut the machine down, terminate processes, or change system
# state are listed as unsafe and are never clicked. The list is explicit rather than
# pattern-matched, so adding a new button makes it visible here as a button that is
# not being exercised.
$unsafeButtons = @(
    '关机', '重启', '注销', '锁定工作站', '取消待执行的关机',
    '全部结束', '结束选中', '结束进程',
    '全部挂起', '挂起选中', '挂起',
    '完全卸载', '移除摄像头', '移除本程序创建的所有防火墙规则',
    '全部禁用希沃自启', '全部恢复', '恢复选中', '恢复',
    '保存设置', '恢复默认设置', '放弃修改并重新加载',
    '删除', '删除该规则', '重置跨进程风险确认',
    '导出诊断报告', '打开配置文件夹', '打开日志文件夹', '清空显示',
    '浏览…', '全选', '把勾选项保存为规则', '开始扫描', '扫描开机自启项',
    '新建任务', '添加动作', '保存任务', '取消', '确定',
    '开始推送测试画面', '停止推送', '推送图片', '推送纯色',
    '保护本程序窗口', '取消保护', '保护选中窗口', '取消保护选中窗口',
    '一键安装（注册 + 创建）', '创建摄像头实例', '列出系统设备', '刷新',
    # These open an external window or the file picker, which then covers the app and
    # makes every later UI Automation call fail. One of them launched Notepad over the
    # window during the first run of this sweep, which is what broke navigation to
    # every subsequent page.
    '运行程序', '打开配置文件', '浏览…', '导出诊断报告',
    '打开配置文件夹', '打开日志文件夹', '复制到剪贴板', '复制全部'
)

function Get-SafeButtons {
    param([System.Windows.Automation.AutomationElement]$Window)

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)

    $buttons = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $result = @()

    foreach ($button in $buttons) {
        $name = $button.Current.Name

        if ([string]::IsNullOrWhiteSpace($name)) {
            # An unnamed button is itself a defect: it is unreachable for assistive
            # technology. Report it rather than skipping silently.
            Write-Warn '发现一个没有名称的按钮（辅助技术无法识别，测试也无法点击）。'
            continue
        }

        if ($button.Current.IsOffscreen) {
            continue
        }

        if (-not $button.Current.IsEnabled) {
            continue
        }

        if ($unsafeButtons -contains $name) {
            continue
        }

        $result += $button
    }

    return $result
}

# Pages to exercise, with the buttons on each that are worth clicking first so the
# page has content before the generic sweep runs.
$interactionPlan = @(
    @{ Page = '虚拟摄像头'; File = '11-interact-camera.png' },
    @{ Page = '隐私监控';   File = '12-interact-privacy.png' },
    @{ Page = '防截屏保护'; File = '13-interact-capture.png' },
    @{ Page = '希沃软件';   File = '14-interact-seewo.png' },
    @{ Page = '定时任务';   File = '15-interact-schedule.png' },
    @{ Page = '日志与诊断'; File = '16-interact-diagnostics.png' },
    @{ Page = '设置';       File = '17-interact-settings.png' }
)

$clickedTotal = 0

foreach ($entry in $interactionPlan) {
    if ($process.HasExited) {
        Write-Fail "在进入「$($entry.Page)」之前应用已经退出。"
        break
    }

    # Bring the app back to the foreground before navigating. A click on the previous
    # page may have opened a dialog or another window; without this the navigation
    # item is present but obscured, and every later lookup fails for a reason that has
    # nothing to do with the page being tested.
    try {
        [NativeCapture]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 400
    }
    catch {
        # Not fatal; the lookup below will report it if the window really is gone.
    }

    if (-not (Invoke-NavigationItem -Window $window -Name $entry.Page)) {
        Write-Fail "找不到导航项「$($entry.Page)」（窗口可能被其他窗口遮挡）。"
        continue
    }

    Start-Sleep -Seconds 2

    $buttons = Get-SafeButtons -Window $window

    if ($buttons.Count -eq 0) {
        Write-Host "   「$($entry.Page)」没有可安全点击的按钮。"
        continue
    }

    $pageClicked = 0

    foreach ($button in $buttons) {
        if ($process.HasExited) {
            Write-Fail "点击「$($entry.Page)」上的按钮时应用崩溃了。"
            break
        }

        $name = $button.Current.Name

        # Some handlers do real work - enumerating windows, scanning the filesystem,
        # probing every subsystem - and need longer than others. Waiting a fixed short
        # time would let the next click land while the previous one is still running,
        # which produces confusing failures.
        $waitMs = 1200

        foreach ($slow in @('开始扫描', '扫描开机自启项', '运行自检', '刷新窗口列表',
                            '刷新运行中的进程', '刷新状态', '刷新', '立即执行一次',
                            '测试提醒', '保护本程序窗口', '取消保护')) {
            if ($name -eq $slow) {
                $waitMs = 6000
                break
            }
        }

        try {
            $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $pattern.Invoke()
            $pageClicked++
            $clickedTotal++

            Start-Sleep -Milliseconds $waitMs

            # A click can open a dialog or move focus. Bring the window back so the
            # next lookup is not defeated by something being on top of it.
            try {
                [NativeCapture]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
            }
            catch { }
        }
        catch {
            Write-Warn "无法点击「$($entry.Page)」上的「$name」：$($_.Exception.Message)"
        }
    }

    Write-Pass "「$($entry.Page)」：点击了 $pageClicked 个按钮，应用仍然存活。"

    Save-WindowScreenshot -Handle $process.MainWindowHandle `
        -Path (Join-Path $OutputDirectory $entry.File) | Out-Null
}

Write-Pass "共点击 $clickedTotal 个按钮。"

# Let any pending work settle before judging the process, since a crash from an
# asynchronous handler would otherwise be missed.
Start-Sleep -Seconds 3

Write-Step '进程状态'

if ($process.HasExited) {
    Write-Fail "应用已经退出，退出码 $($process.ExitCode)。这通常意味着某个页面或后台服务抛出了未处理的异常。"
}
else {
    Write-Pass "应用仍在运行（PID $($process.Id)），未发生崩溃。"

    # Sample the memory so a runaway allocation would be visible in the log.
    try {
        $process.Refresh()
        Write-Host "   工作集     : $([math]::Round($process.WorkingSet64 / 1MB, 1)) MB"
        Write-Host "   线程数     : $($process.Threads.Count)"
    }
    catch {
        # The process may have exited between the check and the read.
    }
}

Write-Step '应用日志'

$logPath = Join-Path $stateDirectory 'logs\seewo-assistant.log'

if (Test-Path $logPath) {
    Copy-Item $logPath (Join-Path $OutputDirectory 'app-log.txt') -Force

    $logLines = Get-Content $logPath
    Write-Host "   共 $($logLines.Count) 行，完整内容已写入 app-log.txt"

    # Any logged error is worth surfacing in the CI output directly, since a page
    # that throws is caught by the app's own handler rather than crashing.
    $errors = $logLines | Where-Object { $_ -match '\[ERROR\]' }

    if ($errors) {
        Write-Fail "日志中有 $($errors.Count) 条 ERROR："
        $errors | Select-Object -First 10 | ForEach-Object { Write-Host "     $_" -ForegroundColor Red }
    }
    else {
        Write-Pass '日志中没有 ERROR。'
    }

    Write-Host ''
    Write-Host '   日志末尾 20 行：'
    $logLines | Select-Object -Last 20 | ForEach-Object { Write-Host "     $_" }
}
else {
    Write-Warn "没有找到日志文件（$logPath）。应用可能启动得不够久，或者写入失败。"
}

# ---------------------------------------------------------------------- summary

Write-Step '结果汇总'

Write-Host "   截图数量   : $capturedCount / $($pages.Count)"
Write-Host "   失败项     : $($script:Failures.Count)"

$FindingsPath = Join-Path $OutputDirectory 'smoke-test-findings.txt'
$script:Findings | Set-Content $FindingsPath -Encoding UTF8

# Surface the findings in the job summary so they are visible without downloading
# the artifact.
if ($env:GITHUB_STEP_SUMMARY) {
    $summary = [System.Collections.Generic.List[string]]::new()
    $summary.Add('## UI 冒烟测试结果')
    $summary.Add('')
    $summary.Add("截图：$capturedCount / $($pages.Count)")
    $summary.Add('')
    $summary.Add('```')
    $script:Findings | ForEach-Object { $summary.Add($_) }
    $summary.Add('```')
    $summary | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding UTF8
}

# Stop the app. It is killed rather than closed because closing minimises it to the
# tray (its default behaviour), which would leave the process running.
if (-not $process.HasExited) {
    $process.Kill()
    $process.WaitForExit(10000) | Out-Null
}

Write-Host ''
if ($script:Failures.Count -gt 0) {
    Write-Host "冒烟测试失败：$($script:Failures.Count) 项。" -ForegroundColor Red
    exit 1
}

Write-Host '冒烟测试通过。' -ForegroundColor Green
exit 0
