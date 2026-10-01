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
# Minimises every visible top-level window that is not the application under test.
#
# The Actions runner itself has an interactive host window ("C:\ProgramData\GitHub\Host")
# which can be maximised over everything. It does not belong to the app, it is not
# part of what is being tested, and leaving it on top makes the app window unreachable
# for the clicks that follow.
function Hide-InterferingWindows {
    param([int]$KeepProcessId)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $windows = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)

    $hidden = 0

    foreach ($w in $windows) {
        try {
            if ($w.Current.ProcessId -eq $KeepProcessId) { continue }
            if (-not $w.Current.IsEnabled) { continue }

            $name = $w.Current.Name

            # Only touch windows that can actually get in the way, and never the shell.
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            if ($name -match 'Program Manager|^Start$|Taskbar') { continue }

            $pattern = $w.GetCurrentPattern(
                [System.Windows.Automation.WindowPattern]::Pattern)
            $pattern.SetWindowVisualState(
                [System.Windows.Automation.WindowVisualState]::Minimized)
            $hidden++
        }
        catch {
            # Some windows refuse; that is fine.
        }
    }

    if ($hidden -gt 0) {
        Write-Host "   已最小化 $hidden 个干扰窗口。"
    }
}

# Re-reads the application's main window element.
#
# A cached AutomationElement can go stale once the page inside it is replaced by
# navigation, after which every descendant lookup fails even though the window is
# perfectly healthy. Re-acquiring by process id is cheap and removes that whole class
# of confusing failure.
function Get-AppWindow {
    param([int]$ProcessId)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)

    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
}


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

# Finds a single element by its automation id.
#
# WinUI sets AutomationId from x:Name, so this is a stable handle that does not depend
# on display text or control type. Prefer it over matching on a label.
# Verifies that the banner actually rendered its text.
#
# The banner is drawn with a dark background, an orange accent bar on its left edge,
# and light text. If the text is never drawn the result is a featureless dark
# rectangle, which is what a user reported as "显示黑色". Checking that the process
# survived does not distinguish those cases, so this inspects the pixels.
#
# The banner is located by its accent bar rather than by a hard-coded rectangle, so a
# change to its position or size does not silently turn this into a no-op.
function Assert-BannerHasText {
    param([string]$ImagePath)

    if (-not (Test-Path $ImagePath)) {
        Write-Fail "横幅截图不存在：$ImagePath"
        return
    }

    Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue

    $image = $null
    $bitmap = $null

    try {
        $image = [System.Drawing.Image]::FromFile($ImagePath)
        $bitmap = New-Object System.Drawing.Bitmap $image

        $width = $bitmap.Width
        $height = $bitmap.Height
        $scanHeight = [Math]::Min(300, $height)

        # Find the accent bar: the one strongly orange element on screen.
        $accentLeft = [int]::MaxValue
        $accentRight = -1
        $accentTop = [int]::MaxValue
        $accentBottom = -1

        for ($y = 0; $y -lt $scanHeight; $y++) {
            for ($x = 0; $x -lt $width; $x++) {
                $pixel = $bitmap.GetPixel($x, $y)

                if ($pixel.R -gt 190 -and $pixel.G -gt 50 -and $pixel.G -lt 150 -and $pixel.B -lt 120) {
                    if ($x -lt $accentLeft) { $accentLeft = $x }
                    if ($x -gt $accentRight) { $accentRight = $x }
                    if ($y -lt $accentTop) { $accentTop = $y }
                    if ($y -gt $accentBottom) { $accentBottom = $y }
                }
            }
        }

        if ($accentRight -lt 0) {
            Write-Fail "截图里找不到横幅的橙色色条，横幅可能没有显示。"
            return
        }

        Write-Host "   横幅定位：左侧 x=$accentLeft，y=$accentTop..$accentBottom"

        # The banner is 420 wide, with the accent bar occupying its first 6 pixels.
        $textLeft = $accentRight + 6
        $textRight = [Math]::Min($width - 1, $accentLeft + 420)

        $lightPixels = 0

        for ($y = $accentTop; $y -le $accentBottom; $y++) {
            for ($x = $textLeft; $x -lt $textRight; $x++) {
                $pixel = $bitmap.GetPixel($x, $y)

                # The title is white and the body is light grey; requiring all three
                # channels high keeps the orange accent bar from counting.
                if ($pixel.R -gt 190 -and $pixel.G -gt 190 -and $pixel.B -gt 190) {
                    $lightPixels++
                }
            }
        }

        if ($lightPixels -lt 30) {
            Write-Fail ("横幅里几乎没有浅色像素（$lightPixels 个），说明只画了背景和色条、" +
                        "没有画出文字——这正是「显示黑色」的成因。")
        }
        else {
            Write-Pass "横幅包含文字像素（$lightPixels 个浅色像素）。"
        }
    }
    catch {
        Write-Warn "无法分析横幅截图：$($_.Exception.Message)"
    }
    finally {
        if ($null -ne $bitmap) { $bitmap.Dispose() }
        if ($null -ne $image) { $image.Dispose() }
    }
}

# Finds an actionable element by its automation name within a given root.
#
# Two subtleties, both of which caused silent skips:
#
#   * Callers pass the content frame, not the window. The navigation pane holds items
#     whose names collide with page content (the settings entry is named "设置"), and a
#     window-wide search returns the navigation item first.
#   * A Button with a TextBlock child shares its name with that child, and FindFirst
#     often returns the TextBlock - which supports neither InvokePattern nor
#     TogglePattern. So every match is examined and the first actionable one wins.
function Find-ByName {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$Name
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)

    $matches = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)

    if ($matches.Count -eq 0) { return $null }

    # Prefer an element that can actually be driven.
    foreach ($candidate in $matches) {
        if (-not $candidate.Current.IsEnabled) { continue }

        foreach ($pattern in @(
            [System.Windows.Automation.TogglePattern]::Pattern,
            [System.Windows.Automation.InvokePattern]::Pattern)) {
            $supported = $null

            if ($candidate.TryGetCurrentPattern($pattern, [ref]$supported)) {
                return $candidate
            }
        }
    }

    # Nothing actionable carries that name; return the first match so the caller's
    # error message can still name it.
    return $matches[0]
}

function Find-ByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$AutomationId
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)

    return $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

# Returns the frame that hosts the page content, so lookups can be scoped to it.
function Get-ContentRoot {
    param([System.Windows.Automation.AutomationElement]$Window)

    # The Frame is a named element; fall back to the window when it cannot be found.
    $frame = Find-ByAutomationId -Window $Window -AutomationId 'ContentFrame'

    if ($null -ne $frame) { return $frame }

    return $Window
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

# Get anything the runner put on screen out of the way before touching the UI.
Hide-InterferingWindows -KeepProcessId $process.Id

# Enable the banner before the sweep. It is off by default, so without this the banner
# window - the thing reported as showing a black block and then crashing - is never
# created and its code never runs.
Write-Step '启用醒目横幅（默认关闭，但需要验证）'

if (Invoke-NavigationItem -Window $window -Name '隐私监控') {
    Start-Sleep -Seconds 2

    # Address the toggle by its automation id (ShowBannerToggle) rather than its
    # label. Matching on the label proved fragile: it silently skipped the whole
    # banner path, which is the one thing this check exists to cover.
    $toggle = Find-ByAutomationId -Window $window -AutomationId 'ShowBannerToggle'

    $enabledBanner = $false

    if ($null -ne $toggle) {
        try {
            $pattern = $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)

            if ($pattern.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
                $pattern.Toggle()
                Start-Sleep -Milliseconds 800
            }

            Write-Pass '已开启「显示屏幕横幅」，横幅窗口会被创建。'
            $enabledBanner = $true
        }
        catch {
            Write-Warn "无法切换「显示屏幕横幅」：$($_.Exception.Message)"
        }
    }

    if (-not $enabledBanner) {
        # Dump what the page actually exposes, so a future failure is diagnosable from
        # the log alone instead of needing another round trip.
        Write-Warn '没有找到 ShowBannerToggle，横幅代码路径不会被验证。页面上的控件：'

        $all = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)

        foreach ($element in $all) {
            $id = $element.Current.AutomationId
            $nm = $element.Current.Name

            if (-not [string]::IsNullOrWhiteSpace($id)) {
                Write-Host "       id='$id' type=$($element.Current.ControlType.ProgrammaticName) name='$nm'"
            }
        }
    }

    # This raises a sample alert, which is what creates and paints the banner.
    $alertCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, '测试提醒')

    $alertButton = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants, $alertCondition)

    if ($null -ne $alertButton) {
        try {
            $alertButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Write-Pass '已点击「测试提醒」，横幅应该已经出现。'

            # The banner lives for about six seconds, so capture it while it is still
            # on screen. Screenshotting after it has gone produced a picture of an
            # ordinary page and proved nothing.
            Start-Sleep -Seconds 2

            $bannerShot = Join-Path $OutputDirectory '18-banner.png'

            Save-ScreenRegion -Path $bannerShot `
                -X 0 -Y 0 -Width $screenWidth -Height $screenHeight | Out-Null

            # The banner must actually contain text. Rendering its background but not
            # its text is a real defect this check exists to catch: the app drew a dark
            # block, the exception that followed killed the process, and the test still
            # passed because it only checked that the process was alive.
            Assert-BannerHasText -ImagePath $bannerShot

            # Then wait out the rest of its lifetime, so a crash on dismissal is caught
            # rather than missed.
            Start-Sleep -Seconds 7

            Save-ScreenRegion -Path (Join-Path $OutputDirectory '19-banner-dismissed.png') `
                -X 0 -Y 0 -Width $screenWidth -Height $screenHeight | Out-Null
        }
        catch {
            Write-Warn "无法点击「测试提醒」：$($_.Exception.Message)"
        }
    }
    else {
        Write-Warn '没有找到「测试提醒」按钮。'
    }

    if ($process.HasExited) {
        Write-Fail '点击「测试提醒」后应用崩溃了，横幅代码路径有问题。'
    }
    else {
        Write-Pass '横幅显示与自动消失后，应用仍然存活。'
    }
}
else {
    Write-Warn '无法进入「隐私监控」页面，横幅代码路径不会被验证。'
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
# Controls the sweep must never click, because they change machine state in a way that
# would take the runner down or is not reversible in a test run.
#
# Everything not listed here IS clicked, so this list should stay as short as the risk
# allows. In particular the read-only scan and refresh buttons are deliberately NOT
# listed: "希沃软件扫描扫不出来" was one of the reported defects, and excluding the scan
# button is what stopped the sweep from ever reproducing it.
#
# The comments are outside the array on purpose - text inside it would be parsed as
# entries.
# Controls the sweep must never click, because they change machine state in a way that
# would take the runner down or cannot be undone in a test run.
#
# Everything not listed here IS clicked, so keep this list as short as the risk allows.
# The read-only scan and refresh buttons are deliberately absent: the Seewo scan not
# finding anything was one of the reported defects, and excluding its button is what
# stopped the sweep from ever reproducing it.
#
# Grouped by reason. Keep all prose out of the array body: an apostrophe in a comment
# is harmless to PowerShell, but it makes the list impossible to verify mechanically.
$unsafeButtons = @(
    # Power and session state: would end the test run.
    '关机', '重启', '注销', '锁定工作站', '取消待执行的关机',
    # Process control: terminates or freezes real programs.
    '全部结束', '结束选中', '结束进程', '全部挂起', '挂起选中', '挂起',
    # Destructive or system-wide, and not reversible within a test.
    '完全卸载', '移除摄像头', '移除本程序创建的所有防火墙规则',
    '全部禁用希沃自启', '全部恢复', '恢复选中', '恢复',
    '保存设置', '恢复默认设置', '放弃修改并重新加载',
    '删除', '删除该规则', '重置跨进程风险确认',
    # Writes to the registry; the camera is already covered by the push buttons.
    '一键安装（注册 + 创建）', '创建摄像头实例', '列出系统设备',
    # Window chrome and navigation pane: not page functionality, and Close hides the
    # app to the tray, which ends that page's turn.
    'Minimize', 'Maximize', 'Restore', 'Close', 'Close Navigation', 'Open Navigation',

    # The navigation items themselves. These matter: the settings entry is named "设置",
    # the same as controls on the settings page, and a by-name lookup returns the
    # navigation item first. Clicking it navigated away from the page under test, so
    # every page silently exercised the Settings page instead.
    '虚拟摄像头', '隐私监控', '防截屏保护', '希沃软件', '定时任务', '日志与诊断', '设置',
    # Opens a modal dialog or an external window that then covers the app and breaks
    # every later lookup.
    '新建任务', '添加动作', '保存任务', '取消', '确定',
    '浏览…', '运行程序', '打开配置文件', '导出诊断报告',
    '打开配置文件夹', '打开日志文件夹'
)


# Sanity-checks the unsafe list before the sweep relies on it.
#
# This list has already gone wrong twice in ways nothing caught: a rewrite dropped the
# window-chrome entries (so the sweep clicked Close and ended the page's turn), and
# comment prose inside the array was parsed as entries. Both were only visible by
# reading a run log closely. The assertions below make either mistake a loud failure.
function Assert-UnsafeListIsSane {
    if ($unsafeButtons.Count -lt 20) {
        Write-Fail "禁用列表只有 $($unsafeButtons.Count) 项，可能被误改。"
        return
    }

    $malformed = $unsafeButtons | Where-Object {
        [string]::IsNullOrWhiteSpace($_) -or $_.Length -gt 40 -or $_.Contains("`n")
    }

    if ($malformed) {
        Write-Fail "禁用列表里有异常条目（很可能是注释被当成了字符串）：$($malformed -join ' | ')"
        return
    }

    # These must never be clickable.
    foreach ($required in @('关机', '重启', '全部结束', '全部挂起', 'Minimize', 'Close')) {
        if ($unsafeButtons -notcontains $required) {
            Write-Fail "禁用列表缺少「$required」，冒烟测试可能会执行危险操作。"
            return
        }
    }

    # These must stay clickable: each one covers a defect that was reported.
    foreach ($forbidden in @('开始扫描', '运行自检', '刷新窗口列表', '开始推送测试画面', '推送纯色')) {
        if ($unsafeButtons -contains $forbidden) {
            Write-Fail "「$forbidden」被列入了禁用列表，但它对应的功能正是需要被验证的。"
            return
        }
    }

    Write-Pass "禁用列表检查通过（$($unsafeButtons.Count) 项）。"
}

Assert-UnsafeListIsSane

function Get-SafeButtons {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$PageName = '(unknown)'
    )

    # Include check boxes and toggles as well as buttons: the settings pages express
    # almost everything as a ToggleSwitch, and a sweep that only clicked Buttons would
    # leave the entire settings surface untested.
    $isButton = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $isCheckBox = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::CheckBox)

    $condition = New-Object System.Windows.Automation.OrCondition($isButton, $isCheckBox)

    $buttons = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $result = @()
    $unnamed = 0

    foreach ($button in $buttons) {
        $name = $button.Current.Name

        if ([string]::IsNullOrWhiteSpace($name)) {
            # An unnamed control is a defect in its own right: it is unreachable for
            # assistive technology. Report which control it is, not just that one exists -
            # the previous message named neither the page nor the control, so a run that
            # reported forty of them gave no clue where to look.
            $unnamed++
            $automationId = $button.Current.AutomationId
            $type = $button.Current.ControlType.ProgrammaticName

            Write-Warn ("未命名控件：page='$PageName' type=$type id='$automationId' " +
                        "class='$($button.Current.ClassName)' " +
                        "bounds=$($button.Current.BoundingRectangle)")

            continue
        }

        if (-not $button.Current.IsEnabled) {
            continue
        }

        # Bring the element into view before judging whether it is offscreen. The
        # window is small on the CI desktop, so most page content starts below the fold
        # and would otherwise be skipped - which is how the camera page's push buttons
        # went unclicked.
        if ($button.Current.IsOffscreen) {
            $scrolled = $false

            try {
                $scrollItem = $button.GetCurrentPattern(
                    [System.Windows.Automation.ScrollItemPattern]::Pattern)
                $scrollItem.ScrollIntoView()
                $scrolled = $true
                Start-Sleep -Milliseconds 250
            }
            catch {
                # Not every element supports ScrollItemPattern; try the container.
            }

            if (-not $scrolled) {
                try {
                    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
                    $parent = $walker.GetParent($button)

                    while ($null -ne $parent) {
                        try {
                            $scroll = $parent.GetCurrentPattern(
                                [System.Windows.Automation.ScrollPattern]::Pattern)
                            $scroll.SetScrollPercent(
                                [System.Windows.Automation.ScrollPattern]::NoScroll, 100)
                            $scrolled = $true
                            Start-Sleep -Milliseconds 300
                            break
                        }
                        catch {
                            $parent = $walker.GetParent($parent)
                        }
                    }
                }
                catch {
                    # Give up on this element.
                }
            }

            if (-not $scrolled) {
                continue
            }
        }

        if ($unsafeButtons -contains $name) {
            continue
        }

        $result += $button
    }

    if ($unnamed -gt 0) {
        Write-Host "   「$PageName」有 $unnamed 个未命名控件。" -ForegroundColor Yellow
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

    # Clear anything that appeared on top, then re-acquire the window element. Both
    # are needed: a window the runner opened can cover the app, and the element cached
    # before navigation can be stale.
    Hide-InterferingWindows -KeepProcessId $process.Id

    try {
        [NativeCapture]::ShowWindow($process.MainWindowHandle, [NativeCapture]::SW_RESTORE) | Out-Null
        [NativeCapture]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 500
    }
    catch {
        # Not fatal; the lookup below reports it if the window is really gone.
    }

    $window = Get-AppWindow -ProcessId $process.Id

    if ($null -eq $window) {
        Write-Fail "找不到「$($entry.Page)」的应用窗口，应用可能已经退出。"
        break
    }

    if (-not (Invoke-NavigationItem -Window $window -Name $entry.Page)) {
        Write-Fail "找不到导航项「$($entry.Page)」（窗口可能被其他窗口遮挡）。"
        continue
    }

    Start-Sleep -Seconds 2

    # Work from a list of names and re-query each control immediately before clicking
    # it, rather than holding a collection of elements. Scrolling to reach one control
    # invalidates the cached references to the rest, which silently reduced this sweep
    # from 50 clicks to 27.
    #
    # Stop-before-start ordering matters: pushing a solid colour starts the frame pump,
    # and starting the pump disables the test-pattern button. Clicking the colour first
    # is how 测试摄像头 escaped this check entirely.
    $pageClicked = 0
    $clickedNames = @()

    # Bounded so a page that keeps adding controls cannot loop forever.
    for ($pass = 0; $pass -lt 4; $pass++) {
        if ($process.HasExited) { break }

        $contentRoot = Get-ContentRoot -Window $window

        $candidates = @(Get-SafeButtons -Window $contentRoot -PageName $entry.Page |
            Where-Object { $clickedNames -notcontains $_.Current.Name } |
            Sort-Object -Property @{
                Expression = {
                    switch -Wildcard ($_.Current.Name) {
                        '停止推送*' { 0 }
                        '*测试画面*' { 1 }
                        default     { 2 }
                    }
                }
            })

        if ($candidates.Count -eq 0) { break }

        foreach ($candidate in $candidates) {
            if ($process.HasExited) { break }

            $name = $candidate.Current.Name

            if ([string]::IsNullOrWhiteSpace($name) -or $clickedNames -contains $name) {
                continue
            }

            # Re-query by name: the element captured a moment ago may already be stale.
            $button = Find-ByName -Window $contentRoot -Name $name

            if ($null -eq $button) { continue }

            # Bring it into view, since most page content starts below the fold on the
            # small CI desktop.
            try {
                $scrollItem = $button.GetCurrentPattern(
                    [System.Windows.Automation.ScrollItemPattern]::Pattern)
                $scrollItem.ScrollIntoView()
                Start-Sleep -Milliseconds 250

                # Scrolling can move it, so look it up once more.
                $button = Find-ByName -Window $contentRoot -Name $name
                if ($null -eq $button) { continue }
            }
            catch {
                # Not every control supports ScrollItemPattern.
            }

            # Some handlers do real work - enumerating windows, scanning the
            # filesystem, probing every subsystem - and need longer than others.
            $waitMs = 1200

            foreach ($slow in @('开始扫描', '扫描开机自启项', '运行自检', '刷新窗口列表',
                                '刷新运行中的进程', '刷新状态', '立即执行一次',
                                '测试提醒', '保护本窗口', '保护选中窗口')) {
                if ($name -eq $slow) {
                    $waitMs = 6000
                    break
                }
            }

            try {
                # A control can support either pattern. ToggleSwitch - used for every
                # setting on these pages - exposes TogglePattern, not InvokePattern.
                $invoked = $false

                try {
                    $toggle = $button.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                    $toggle.Toggle()
                    $invoked = $true
                }
                catch {
                    $invoke = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                    $invoke.Invoke()
                    $invoked = $true
                }

                if (-not $invoked) { continue }

                $pageClicked++
                $clickedTotal++
                $clickedNames += $name

                Start-Sleep -Milliseconds $waitMs

                # A click can open a dialog, move focus, or replace the page content.
                try {
                    [NativeCapture]::ShowWindow($process.MainWindowHandle, [NativeCapture]::SW_RESTORE) | Out-Null
                    [NativeCapture]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
                }
                catch { }

                $refreshed = Get-AppWindow -ProcessId $process.Id
                if ($null -ne $refreshed) { $window = $refreshed }
            }
            catch {
                Write-Warn "无法点击「$($entry.Page)」上的「$name」：$($_.Exception.Message)"
            }
        }
    }

    Write-Pass "「$($entry.Page)」：点击了 $pageClicked 个控件，应用仍然存活。"

    if ($clickedNames.Count -gt 0) {
        Write-Host "      已点击：$($clickedNames -join '、')"
    }

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
    # @() is required: a single match comes back as a bare string, and a string has no
    # .Count. Without it the script threw here - after finding a real error, so the
    # failure was reported as a script bug instead of the defect it had just detected.
    $errors = @($logLines | Where-Object { $_ -match '\[ERROR\]' })

    if ($errors.Count -gt 0) {
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
