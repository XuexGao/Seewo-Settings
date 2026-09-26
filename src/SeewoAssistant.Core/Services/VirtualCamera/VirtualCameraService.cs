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
                "DirectShow 虚拟摄像头（Windows 10 兼容模式）",
                "Windows 10 没有官方用户态虚拟摄像头 API（MFCreateVirtualCamera 需要 Windows 11 22000+）。" +
                "这里使用 DirectShow 源滤镜作为替代：Zoom、OBS、QQ/微信、ffmpeg 等 DirectShow 应用可以看到它，" +
                "但它不会出现在 Windows「设置」的摄像头列表里，UWP 应用也看不到。");
        }

        return new VirtualCameraCapability(
            VirtualCameraBackend.Unsupported, false,
            "系统版本过低",
            "需要 Windows 10 版本 2004（内部版本 19041）或更高版本。");
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

    /// <summary>Registers the media source COM server. Requires elevation.</summary>
    public Task<ActionResult> RegisterAsync(CancellationToken cancellationToken = default) =>
        RunSetupAsync("install", cancellationToken);

    /// <summary>Unregisters the media source and removes every camera it created.</summary>
    public Task<ActionResult> UnregisterAsync(CancellationToken cancellationToken = default) =>
        RunSetupAsync("uninstall", cancellationToken);

    /// <summary>Creates the camera instance so it becomes enumerable.</summary>
    public Task<ActionResult> CreateCameraAsync(CancellationToken cancellationToken = default) =>
        RunSetupAsync($"create --name \"{FriendlyName}\"", cancellationToken);

    /// <summary>Removes the camera instance from the system.</summary>
    public Task<ActionResult> RemoveCameraAsync(CancellationToken cancellationToken = default) =>
        RunSetupAsync("remove", cancellationToken);

    /// <summary>Lists the cameras this app created.</summary>
    public Task<ActionResult> ListCamerasAsync(CancellationToken cancellationToken = default) =>
        RunSetupAsync("list", cancellationToken);

    private async Task<ActionResult> RunSetupAsync(string arguments, CancellationToken cancellationToken)
    {
        if (!IsSetupToolAvailable)
        {
            return ActionResult.Fail(
                $"未找到 {Path.GetFileName(SetupToolPath)}。请确认已解压完整的发行包，" +
                "或先运行 scripts\\Install-Native.ps1 安装原生组件。");
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
