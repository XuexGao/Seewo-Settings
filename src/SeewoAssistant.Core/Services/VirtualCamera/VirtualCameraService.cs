using System.Diagnostics;
using System.Runtime.Versioning;
using SeewoAssistant.Core.Abstractions;

namespace SeewoAssistant.Core.Services.VirtualCamera;

/// <summary>Which backend is available on this machine.</summary>
public enum VirtualCameraBackend
{
    /// <summary>Nothing available; the OS is too old.</summary>
    Unsupported,

    /// <summary>Windows 11 22000+: the Media Foundation virtual camera API.</summary>
    MediaFoundation,

    /// <summary>Windows 10: the DirectShow source filter fallback.</summary>
    DirectShow,
}

/// <summary>Describes what the current OS can actually do.</summary>
public sealed record VirtualCameraCapability(
    VirtualCameraBackend Backend,
    bool IsSupported,
    string Summary,
    string Detail)
{
    /// <summary>
    /// True when the backend surfaces the camera in the Windows Settings device
    /// list. The DirectShow filter does not, which is a user-visible limitation
    /// worth stating plainly rather than hiding.
    /// </summary>
    public bool AppearsInWindowsSettings => Backend == VirtualCameraBackend.MediaFoundation;
}

/// <summary>
/// Owns the virtual camera: capability detection, COM registration of the media
/// source, creation and removal of the camera instance, and the frame pump.
/// </summary>
/// <remarks>
/// The camera instance itself is created by a separate native tool
/// (<c>SeewoVirtualCamera.Setup.exe</c>) because <c>MFCreateVirtualCamera</c> must
/// not run on a UI thread and lives in an API set this managed process cannot
/// assume is present. Everything here shells out to that tool and manages the
/// frame channel.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class VirtualCameraService : IAsyncDisposable
{
    /// <summary>The media source CLSID, matching <c>native/SeewoVirtualCamera</c>.</summary>
    public const string MediaSourceClsid = "{A7E4B2C1-5D3F-4A88-9B6E-1C2D3E4F5A60}";

    /// <summary>The DirectShow filter CLSID, matching <c>native/SeewoVirtualCamera.DShow</c>.</summary>
    public const string DirectShowFilterClsid = "{B8F5C3D2-6E4A-4B99-8C7F-2D3E4F5A6B71}";

    /// <summary>The friendly name shown in device lists.</summary>
    public const string FriendlyName = "SeewoAssistant Virtual Camera";

    private readonly IAppLogger _logger;
    private readonly FrameSourceFactory _frameSource;
    private readonly VirtualCameraFrameChannel _channel;

    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private Func<FrameBuffer>? _frameProvider;
    private readonly object _gate = new();

    public VirtualCameraService(string? toolsDirectory = null, IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _frameSource = new FrameSourceFactory(_logger);
        _channel = new VirtualCameraFrameChannel(_logger);
        ToolsDirectory = toolsDirectory ?? AppContext.BaseDirectory;
    }

    /// <summary>Directory containing <c>SeewoVirtualCamera.Setup.exe</c> and the native DLLs.</summary>
    public string ToolsDirectory { get; }

    /// <summary>Resolution the frame pump renders at.</summary>
    public int FrameWidth { get; set; } = FrameSourceFactory.DefaultWidth;

    /// <summary>Resolution the frame pump renders at.</summary>
    public int FrameHeight { get; set; } = FrameSourceFactory.DefaultHeight;

    /// <summary>Target publish rate for the frame pump.</summary>
    public int FramesPerSecond { get; set; } = 30;

    /// <summary>True while the pump is running.</summary>
    public bool IsPumping
    {
        get
        {
            lock (_gate)
            {
                return _pumpTask is { IsCompleted: false };
            }
        }
    }

    public long PublishedFrames => _channel.PublishedFrames;

    /// <summary>
    /// Detects which backend this OS supports. <c>MFCreateVirtualCamera</c> only
    /// exists from Windows 11 build 22000, so the API-set probe is authoritative
    /// rather than a version check, which can be fooled by compatibility shims.
    /// </summary>
    public VirtualCameraCapability DetectCapability()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new VirtualCameraCapability(
                VirtualCameraBackend.Unsupported, false,
                "当前系统不是 Windows",
                "虚拟摄像头只能在 Windows 上工作。");
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && IsMediaFoundationVirtualCameraAvailable())
        {
            return new VirtualCameraCapability(
                VirtualCameraBackend.MediaFoundation, true,
                "Windows 11 原生虚拟摄像头（Media Foundation）",
                "使用系统官方 IMFVirtualCamera 方案，摄像头会出现在「设置 → 蓝牙和其他设备 → 摄像头」" +
                "以及 Zoom、Teams、微信等所有应用的设备列表中。");
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return new VirtualCameraCapability(
                VirtualCameraBackend.DirectShow, true,
                "DirectShow 虚拟摄像头（Windows 10 可用）",
                "Windows 10 上可以使用。微软没有在 Windows 10 提供官方用户态虚拟摄像头 API" +
                "（MFCreateVirtualCamera 是 Windows 11 内部版本 22000 才加入的），所以这里用 DirectShow " +
                "源滤镜实现：注册之后，Zoom、OBS、QQ、微信、钉钉、ffmpeg、PotPlayer 等绝大多数常见软件" +
                "都能在摄像头列表里选到它。\n\n" +
                "需要注意的两点：它不会出现在 Windows「设置 → 蓝牙和其他设备 → 摄像头」里，" +
                "UWP 应用（系统「相机」应用、部分商店应用）也看不到它。这是 Windows 10 方案的固有限制，" +
                "不是安装失败。\n\n" +
                "验证方法：安装后在命令行运行 " +
                "ffmpeg -list_devices true -f dshow -i dummy，列表中应出现 " +
                "「" + FriendlyName + "」。");
        }

        return new VirtualCameraCapability(
            VirtualCameraBackend.Unsupported, false,
            "系统版本过低",
            "虚拟摄像头需要 Windows 10 版本 2004（内部版本 19041）或更高版本。" +
            "当前系统版本低于该要求，因此没有可用的后端。");
    }

    /// <summary>
    /// Probes for <c>MFCreateVirtualCamera</c> in <c>mfsensorgroup.dll</c>. Done by
    /// loading the library rather than linking, so the app still starts on Windows 10.
    /// </summary>
    private static bool IsMediaFoundationVirtualCameraAvailable()
    {
        var module = Interop.NativeMethods.GetModuleHandleW("mfsensorgroup.dll");
        if (module == nint.Zero)
        {
            module = LoadLibraryW("mfsensorgroup.dll");
            if (module == nint.Zero)
            {
                return false;
            }
        }

        return Interop.NativeMethods.GetProcAddress(module, "MFCreateVirtualCamera") != nint.Zero;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryW(string lpFileName);

    // ------------------------------------------------------------------ setup tool

    private string SetupToolPath => Path.Combine(ToolsDirectory, "SeewoVirtualCamera.Setup.exe");

    /// <summary>True when the native setup tool is present next to the app.</summary>
    public bool IsSetupToolAvailable => File.Exists(SetupToolPath);

    /// <summary>
    /// Registers whichever backend this machine can actually use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This must dispatch on the detected backend. It previously always ran
    /// <c>SeewoVirtualCamera.Setup.exe install</c>, which registers the Media
    /// Foundation media source and then calls <c>MFCreateVirtualCamera</c>. On
    /// Windows 10 that API does not exist, so the tool exited with code 3
    /// ("unsupported") and the install button failed every time - the app was
    /// unusable on the very system it was written for.
    /// </para>
    /// <para>
    /// On Windows 10 the DirectShow filter is registered instead, via
    /// <c>regsvr32</c> running its <c>DllRegisterServer</c>. That writes every
    /// registry key a DirectShow capture device needs.
    /// </para>
    /// </remarks>
    public Task<ActionResult> RegisterAsync(CancellationToken cancellationToken = default)
    {
        var capability = DetectCapability();

        return capability.Backend switch
        {
            VirtualCameraBackend.MediaFoundation => RunSetupAsync("install", cancellationToken),
            VirtualCameraBackend.DirectShow => RegisterDirectShowAsync(cancellationToken),
            _ => Task.FromResult(ActionResult.Fail(capability.Detail)),
        };
    }

    /// <summary>Unregisters whichever backend is in use.</summary>
    public Task<ActionResult> UnregisterAsync(CancellationToken cancellationToken = default)
    {
        var capability = DetectCapability();

        return capability.Backend switch
        {
            VirtualCameraBackend.MediaFoundation => RunSetupAsync("uninstall", cancellationToken),
            VirtualCameraBackend.DirectShow => UnregisterDirectShowAsync(cancellationToken),
            _ => Task.FromResult(ActionResult.Fail(capability.Detail)),
        };
    }

    /// <summary>
    /// Creates the camera instance so it becomes enumerable.
    /// </summary>
    /// <remarks>
    /// Only the Media Foundation backend has a separate "instance" step: the DirectShow
    /// filter is registered as a device in its own right, so there is nothing further
    /// to create. Reporting success keeps the page's install flow identical on both
    /// backends instead of making the user interpret a backend-specific error.
    /// </remarks>
    public Task<ActionResult> CreateCameraAsync(CancellationToken cancellationToken = default)
    {
        if (DetectCapability().Backend == VirtualCameraBackend.DirectShow)
        {
            return Task.FromResult(ActionResult.Ok(
                "DirectShow 虚拟摄像头已注册为系统设备，无需额外创建实例。"));
        }

        return RunSetupAsync($"create --name \"{FriendlyName}\"", cancellationToken);
    }

    /// <summary>Removes the camera instance from the system.</summary>
    public Task<ActionResult> RemoveCameraAsync(CancellationToken cancellationToken = default)
    {
        if (DetectCapability().Backend == VirtualCameraBackend.DirectShow)
        {
            // There is no instance to remove on this backend; uninstalling the filter
            // is what removes the device.
            return Task.FromResult(ActionResult.Ok(
                "DirectShow 后端没有独立实例，如需移除设备请使用「完全卸载」。"));
        }

        return RunSetupAsync("remove", cancellationToken);
    }

    /// <summary>Lists the cameras this app created.</summary>
    public Task<ActionResult> ListCamerasAsync(CancellationToken cancellationToken = default) =>
        RunSetupAsync("list", cancellationToken);

    /// <summary>
    /// Name of the 64-bit DirectShow filter, which is what a 64-bit consumer loads.
    /// </summary>
    private const string DirectShowDllName64 = "SeewoVirtualCamera.DShow.dll";

    /// <summary>
    /// Locates the DirectShow filter for the requested architecture.
    /// </summary>
    /// <remarks>
    /// The release ships an x64 and an x86 filter. A 64-bit application loads only the
    /// x64 one and a 32-bit application only the x86 one, so both are registered -
    /// otherwise the camera is invisible to half the software on the machine, which is
    /// exactly the kind of half-working state that is hard to diagnose.
    /// </remarks>
    private string? ResolveDirectShowDll(string architecture)
    {
        string[] candidates =
        [
            Path.Combine(ToolsDirectory, "native", architecture, DirectShowDllName64),
            Path.Combine(ToolsDirectory, architecture, DirectShowDllName64),
            Path.Combine(ToolsDirectory, DirectShowDllName64),
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Registers the DirectShow filter, for both architectures when available.
    /// </summary>
    /// <remarks>
    /// <c>regsvr32</c> runs the filter's <c>DllRegisterServer</c>, which writes the
    /// CLSID keys, the friendly name and the capture-source category entry. That is the
    /// whole registration - there is no separate camera instance to create, unlike the
    /// Media Foundation backend.
    /// </remarks>
    private async Task<ActionResult> RegisterDirectShowAsync(CancellationToken cancellationToken)
    {
        // The native filter is registered by the tool when it is present, because the
        // tool already knows the release layout and reports proper exit codes. This
        // path covers the case where the tool is missing but the DLL is not.
        var results = new List<string>();
        var anyRegistered = false;
        var failures = new List<string>();

        foreach (var architecture in new[] { "x64", "x86" })
        {
            var dll = ResolveDirectShowDll(architecture);

            if (dll is null)
            {
                // The x86 filter is optional: a 64-bit-only install still works for
                // 64-bit applications, so this is a note rather than a failure.
                results.Add($"{architecture}：未找到 DirectShow 滤镜（已跳过）");
                continue;
            }

            var (exitCode, output) = await RunProcessAsync(
                "regsvr32.exe",
                $"/s \"{dll}\"",
                cancellationToken).ConfigureAwait(false);

            if (exitCode == 0)
            {
                anyRegistered = true;
                results.Add($"{architecture}：已注册");
            }
            else
            {
                failures.Add($"{architecture}：regsvr32 退出码 {exitCode}{FormatOutput(output)}");
            }
        }

        if (failures.Count > 0)
        {
            return ActionResult.Fail(
                "DirectShow 虚拟摄像头注册失败。" + string.Join("；", failures) +
                "。注册需要管理员权限，请以管理员身份重新运行本程序。");
        }

        if (!anyRegistered)
        {
            return ActionResult.Fail(
                $"未找到 {DirectShowDllName64}。请确认发行包完整，或运行 scripts\\Install-Native.ps1。");
        }

        _logger.Info($"DirectShow virtual camera registered. {string.Join("; ", results)}");

        return ActionResult.Ok(
            "DirectShow 虚拟摄像头已注册（" + string.Join("，", results) + "）。" +
            "它不会出现在 Windows「设置」的摄像头列表中，但 Zoom、OBS、微信、ffmpeg 等应用可以使用。");
    }

    /// <summary>Unregisters the DirectShow filter for both architectures.</summary>
    private async Task<ActionResult> UnregisterDirectShowAsync(CancellationToken cancellationToken)
    {
        var messages = new List<string>();

        foreach (var architecture in new[] { "x64", "x86" })
        {
            var dll = ResolveDirectShowDll(architecture);

            if (dll is null)
            {
                continue;
            }

            var (exitCode, _) = await RunProcessAsync(
                "regsvr32.exe",
                $"/u /s \"{dll}\"",
                cancellationToken).ConfigureAwait(false);

            messages.Add($"{architecture}：{(exitCode == 0 ? "已注销" : $"退出码 {exitCode}")}");
        }

        if (messages.Count == 0)
        {
            return ActionResult.Fail($"未找到 {DirectShowDllName64}。");
        }

        _logger.Info($"DirectShow virtual camera unregistered. {string.Join("; ", messages)}");

        return ActionResult.Ok("已注销 DirectShow 虚拟摄像头（" + string.Join("，", messages) + "）。");
    }

    /// <summary>Renders captured output for an error message, or nothing when empty.</summary>
    private static string FormatOutput(string output) =>
        string.IsNullOrWhiteSpace(output) ? string.Empty : $"：{output.Trim()}";

    /// <summary>Runs a process and returns its exit code and combined output.</summary>
    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return (-1, $"无法启动 {fileName}");
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var combined = string.Join(
                Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));

            return (process.ExitCode, combined);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>
    /// Locates the media source DLL so the tool can be told where it is.
    /// </summary>
    /// <remarks>
    /// A single-file published app extracts <c>Setup.exe</c> into a temporary directory
    /// that contains nothing else, so the tool's own "look beside myself" search fails
    /// and the in-app install reports "找不到媒体源 DLL". The app knows the real
    /// location, so it passes it with <c>--dll</c>.
    /// </remarks>
    private string? ResolveMediaSourceDllDirectory()
    {
        string[] candidates =
        [
            ToolsDirectory,
            Path.Combine(ToolsDirectory, "native", "x64"),
            Path.Combine(ToolsDirectory, "native", Environment.Is64BitProcess ? "x64" : "x86"),
        ];

        foreach (var directory in candidates)
        {
            if (File.Exists(Path.Combine(directory, "SeewoVirtualCamera.dll")))
            {
                return directory;
            }
        }

        return null;
    }

    private async Task<ActionResult> RunSetupAsync(string arguments, CancellationToken cancellationToken)
    {
        if (!IsSetupToolAvailable)
        {
            return ActionResult.Fail(
                $"未找到 {Path.GetFileName(SetupToolPath)}。请确认已解压完整的发行包，" +
                "或先运行 scripts\\Install-Native.ps1 安装原生组件。");
        }

        // Point the tool at the DLL explicitly. Without this, a single-file build had
        // no working in-app install path at all, and the documented fallback
        // (Install-Native.ps1) was itself broken.
        var dllDirectory = ResolveMediaSourceDllDirectory();

        if (dllDirectory is not null)
        {
            arguments = $"{arguments} --dll \"{dllDirectory}\"";
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = SetupToolPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = ToolsDirectory,

                // The setup tool writes UTF-8 when its output is redirected (it
                // detects that stdout is not a console). Without this the text is
                // decoded with the process's default code page and every Chinese
                // character arrives as mojibake, which is what the user would see in
                // the install output box.
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return ActionResult.Fail("无法启动虚拟摄像头安装工具。");
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = string.Join(
                Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));

            if (process.ExitCode == 0)
            {
                _logger.Info($"Virtual camera setup '{arguments}' succeeded: {output.Trim()}");
                return ActionResult.Ok(string.IsNullOrWhiteSpace(output) ? "操作成功。" : output.Trim());
            }

            _logger.Warn($"Virtual camera setup '{arguments}' exited with {process.ExitCode}: {output.Trim()}");
            return ActionResult.Fail(string.IsNullOrWhiteSpace(output)
                ? $"安装工具返回错误代码 {process.ExitCode}。"
                : output.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Virtual camera setup '{arguments}' failed.", ex);
            return ActionResult.Fail($"调用安装工具失败：{ex.Message}", ex);
        }
    }

    // ------------------------------------------------------------------ frame pump

    /// <summary>Pushes a single solid colour frame and stops the pump.</summary>
    public void PushSolidColor(string color)
    {
        StopPump();
        var frame = _frameSource.CreateSolidColor(color, FrameWidth, FrameHeight);
        _channel.Publish(frame.Pixels, frame.Width, frame.Height, frame.Stride);
    }

    /// <summary>Pushes a single image frame and stops the pump.</summary>
    public ActionResult PushImage(string path)
    {
        StopPump();
        var frame = _frameSource.TryCreateFromImage(path, FrameWidth, FrameHeight);
        if (frame is null)
        {
            return ActionResult.Fail($"无法读取图片：{path}");
        }

        _channel.Publish(frame.Pixels, frame.Width, frame.Height, frame.Stride);
        return ActionResult.Ok($"已推送图片 {Path.GetFileName(path)}（{frame.Width}×{frame.Height}）。");
    }

    /// <summary>Starts continuously publishing the built-in animated test pattern.</summary>
    public void StartTestPatternPump() =>
        StartPump(() => _frameSource.CreateTestPattern(FrameWidth, FrameHeight));

    /// <summary>Starts continuously publishing frames from a custom provider.</summary>
    public void StartPump(Func<FrameBuffer> frameProvider)
    {
        ArgumentNullException.ThrowIfNull(frameProvider);

        lock (_gate)
        {
            if (_pumpTask is { IsCompleted: false })
            {
                _frameProvider = frameProvider;
                return;
            }

            _frameProvider = frameProvider;
            _pumpCts = new CancellationTokenSource();
            var token = _pumpCts.Token;
            _pumpTask = Task.Run(() => PumpLoopAsync(token), CancellationToken.None);
        }

        _logger.Info($"Virtual camera frame pump started at {FrameWidth}x{FrameHeight}@{FramesPerSecond}fps.");
    }

    private async Task PumpLoopAsync(CancellationToken cancellationToken)
    {
        // Schedule against an absolute deadline rather than Task.Delay(interval) so
        // a slow frame does not accumulate drift and the effective rate stays honest.
        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, FramesPerSecond));
        var next = Stopwatch.GetTimestamp();
        var intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Func<FrameBuffer>? provider;
                lock (_gate)
                {
                    provider = _frameProvider;
                }

                if (provider is not null)
                {
                    try
                    {
                        var frame = provider();
                        _channel.Publish(frame.Pixels, frame.Width, frame.Height, frame.Stride);
                    }
                    catch (Exception ex)
                    {
                        // A failing provider must not kill the pump; the camera keeps
                        // running on whatever the media source falls back to.
                        _logger.Error("Frame provider threw; skipping this frame.", ex);
                    }
                }

                next += intervalTicks;
                var delayTicks = next - Stopwatch.GetTimestamp();
                if (delayTicks > 0)
                {
                    var delayMs = (int)Math.Min(int.MaxValue, delayTicks * 1000 / Stopwatch.Frequency);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (delayTicks < -intervalTicks * 5)
                {
                    // Fell far behind (system sleep, debugger break). Resynchronise
                    // instead of spinning to catch up.
                    next = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            _channel.MarkIdle();
        }
    }

    /// <summary>Stops the frame pump. Safe to call when it is not running.</summary>
    public void StopPump()
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_gate)
        {
            cts = _pumpCts;
            task = _pumpTask;
            _pumpCts = null;
            _pumpTask = null;
            _frameProvider = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            task?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation surfaced as a faulted wait; nothing to do.
        }
        finally
        {
            cts.Dispose();
        }

        _channel.MarkIdle();
        _logger.Info("Virtual camera frame pump stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        StopPump();
        await Task.CompletedTask.ConfigureAwait(false);
        _channel.Dispose();
    }
}
