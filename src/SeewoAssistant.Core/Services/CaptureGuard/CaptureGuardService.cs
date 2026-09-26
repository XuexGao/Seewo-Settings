using System.Runtime.Versioning;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Interop;
using SeewoAssistant.Core.Models;

namespace SeewoAssistant.Core.Services.CaptureGuard;

/// <summary>Result of a protection change, including how it was achieved.</summary>
public sealed record ProtectionResult(
    bool Success,
    CaptureProtectionState State,
    ProtectionScope Scope,
    string Message,
    Exception? Exception = null)
{
    public static ProtectionResult Ok(CaptureProtectionState state, ProtectionScope scope, string message) =>
        new(true, state, scope, message);

    public static ProtectionResult Fail(string message, ProtectionScope scope, Exception? ex = null) =>
        new(false, CaptureProtectionState.None, scope, message, ex);
}

/// <summary>
/// Applies and removes screen-capture protection on windows.
/// </summary>
/// <remarks>
/// <para>Two scopes exist, and the difference matters:</para>
/// <list type="bullet">
/// <item><description>
/// <b><see cref="ProtectionScope.OwnProcess"/></b> — the window belongs to this
/// application. <c>SetWindowDisplayAffinity</c> is called directly. This always
/// works, cannot be blocked, and is the right choice for the app's own overlay or
/// mirror window.
/// </description></item>
/// <item><description>
/// <b><see cref="ProtectionScope.CrossProcess"/></b> — the window belongs to
/// another program. The kernel rejects a direct call, so the capture-guard payload
/// is injected into the owning process and makes the call from there. This is
/// opt-in, requires elevation for processes owned by other users, and may be
/// blocked by antivirus active defence. It is never attempted implicitly.
/// </description></item>
/// </list>
/// <para>
/// <c>WDA_EXCLUDEFROMCAPTURE</c> requires Windows 10 version 2004 (build 19041).
/// On earlier builds the OS silently downgrades it to <c>WDA_MONITOR</c>, which
/// still hides the content but leaves a black placeholder; that behaviour is
/// detected and reported rather than presented as full protection.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class CaptureGuardService : IDisposable
{
    /// <summary><c>WDA_NONE</c> — no restriction.</summary>
    public const uint WdaNone = 0x00000000;

    /// <summary><c>WDA_MONITOR</c> — content shows only on a monitor; captures get black.</summary>
    public const uint WdaMonitor = 0x00000001;

    /// <summary><c>WDA_EXCLUDEFROMCAPTURE</c> — the window is absent from captures entirely.</summary>
    public const uint WdaExcludeFromCapture = 0x00000011;

    /// <summary>First Windows build that honours <c>WDA_EXCLUDEFROMCAPTURE</c>.</summary>
    public const int ExcludeFromCaptureMinimumBuild = 19041;

    private readonly IAppLogger _logger;
    private readonly WindowEnumerator _enumerator;
    private readonly PayloadInjector _injector;
    private readonly GuardChannel _channel;
    private readonly object _gate = new();

    /// <summary>Processes already injected, so a repeat request does not re-inject.</summary>
    private readonly HashSet<int> _injectedProcesses = [];

    public CaptureGuardService(string? payloadDirectory = null, IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _enumerator = new WindowEnumerator(_logger);
        _injector = new PayloadInjector(payloadDirectory, _logger);
        _channel = new GuardChannel(_logger);
    }

    /// <summary>Enumerates candidate windows for the picker.</summary>
    public IReadOnlyList<WindowInfo> EnumerateWindows() => _enumerator.Enumerate();

    /// <summary>Describes one window handle, or null when it is not a candidate.</summary>
    public WindowInfo? Describe(nint hWnd) => _enumerator.Describe(hWnd);

    /// <summary>Reads the top-level window currently under the cursor.</summary>
    public nint GetWindowAtCursor() => _enumerator.GetWindowAtCursor();

    /// <summary>
    /// True when the running OS honours <c>WDA_EXCLUDEFROMCAPTURE</c>. On older
    /// builds the flag degrades to <c>WDA_MONITOR</c>.
    /// </summary>
    public static bool SupportsExcludeFromCapture =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, ExcludeFromCaptureMinimumBuild);

    /// <summary>True when the cross-process payload is present next to the app.</summary>
    public bool IsCrossProcessAvailable
    {
        get
        {
            var baseDirectory = AppContext.BaseDirectory;
            var architecture = Environment.Is64BitProcess ? "x64" : "x86";

            return File.Exists(Path.Combine(baseDirectory, "native", architecture, "SeewoCaptureGuard.Payload.dll"))
                || File.Exists(Path.Combine(baseDirectory, architecture, "SeewoCaptureGuard.Payload.dll"))
                || File.Exists(Path.Combine(baseDirectory, "SeewoCaptureGuard.Payload.dll"));
        }
    }

    /// <summary>
    /// Applies protection to a window, choosing the scope automatically from
    /// window ownership.
    /// </summary>
    /// <param name="allowCrossProcess">
    /// Must be true for a window owned by another process. Passing false for such a
    /// window returns a failure explaining why, rather than silently doing nothing.
    /// </param>
    public ProtectionResult Protect(
        WindowInfo window,
        CaptureProtectionMode mode,
        bool allowCrossProcess)
    {
        ArgumentNullException.ThrowIfNull(window);

        var affinity = mode == CaptureProtectionMode.ExcludeFromCapture
            ? WdaExcludeFromCapture
            : WdaMonitor;

        if (!SupportsExcludeFromCapture && mode == CaptureProtectionMode.ExcludeFromCapture)
        {
            _logger.Warn(
                $"This build ({Environment.OSVersion.Version}) predates {ExcludeFromCaptureMinimumBuild}; " +
                "WDA_EXCLUDEFROMCAPTURE will behave as WDA_MONITOR.");
        }

        var scope = window.IsOwnProcess ? ProtectionScope.OwnProcess : ProtectionScope.CrossProcess;

        if (scope == ProtectionScope.OwnProcess)
        {
            return ApplyDirect(window, affinity, scope);
        }

        if (!allowCrossProcess)
        {
            return ProtectionResult.Fail(
                $"「{window.ProcessName}」属于其他进程。保护其他程序的窗口需要开启跨进程注入，" +
                "请在设置中显式启用该功能。",
                scope);
        }

        if (PayloadInjector.IsForbiddenProcess(window.ProcessName))
        {
            return ProtectionResult.Fail(
                $"出于系统稳定性考虑，拒绝向「{window.ProcessName}」注入。" +
                "注入桌面合成器、外壳或关键系统进程可能导致黑屏或系统崩溃。",
                scope);
        }

        return ApplyCrossProcess(window, affinity, scope);
    }

    /// <summary>Removes protection from a window.</summary>
    public ProtectionResult Unprotect(WindowInfo window, bool allowCrossProcess)
    {
        ArgumentNullException.ThrowIfNull(window);

        var scope = window.IsOwnProcess ? ProtectionScope.OwnProcess : ProtectionScope.CrossProcess;

        if (scope == ProtectionScope.OwnProcess)
        {
            return ApplyDirect(window, WdaNone, scope);
        }

        if (!allowCrossProcess)
        {
            return ProtectionResult.Fail(
                $"「{window.ProcessName}」属于其他进程，需要跨进程注入才能取消保护。",
                scope);
        }

        return ApplyCrossProcess(window, WdaNone, scope);
    }

    /// <summary>Applies an affinity directly, which only works for our own windows.</summary>
    private ProtectionResult ApplyDirect(WindowInfo window, uint affinity, ProtectionScope scope)
    {
        if (!NativeMethods.IsWindow(window.Handle))
        {
            return ProtectionResult.Fail("窗口已不存在。", scope);
        }

        if (!NativeMethods.SetWindowDisplayAffinity(window.Handle, affinity))
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            var message = error == NativeMethods.ERROR_ACCESS_DENIED
                ? "系统拒绝了这次调用：SetWindowDisplayAffinity 只能作用于本进程自己的窗口。"
                : $"设置窗口显示亲和性失败，Win32 错误 {error}。";

            _logger.Warn(message);
            return ProtectionResult.Fail(message, scope);
        }

        var state = AffinityToState(affinity);
        _logger.Info($"Applied {state} to own window {window.HandleHex} ({window.ClassName}).");
        return ProtectionResult.Ok(state, scope, $"已对本程序窗口应用 {Describe(state)}。");
    }

    /// <summary>Injects the payload if needed, then sends the affinity request.</summary>
    private ProtectionResult ApplyCrossProcess(WindowInfo window, uint affinity, ProtectionScope scope)
    {
        if (!IsCrossProcessAvailable)
        {
            return ProtectionResult.Fail(
                "未找到跨进程注入载荷 SeewoCaptureGuard.Payload.dll。请确认发行包完整。",
                scope);
        }

        if (!_channel.Open())
        {
            return ProtectionResult.Fail("无法创建跨进程通信通道。", scope);
        }

        lock (_gate)
        {
            if (!_injectedProcesses.Contains(window.ProcessId))
            {
                var injection = _injector.Inject(window.ProcessId, _channel);
                if (!injection.Success)
                {
                    return ProtectionResult.Fail(injection.Message, scope, injection.Exception);
                }

                _injectedProcesses.Add(window.ProcessId);
            }
        }

        var response = _channel.Send(
            GuardCommandNative.SetAffinity,
            (ulong)window.Handle,
            affinity);

        if (response is null)
        {
            // The payload stopped answering; drop it from the cache so the next
            // attempt re-injects rather than silently failing forever.
            lock (_gate)
            {
                _injectedProcesses.Remove(window.ProcessId);
            }

            return ProtectionResult.Fail(
                "注入载荷没有响应。目标进程可能已退出，或载荷已被安全软件终止。",
                scope);
        }

        var (success, lastError, _) = response.Value;

        if (!success)
        {
            var message = lastError switch
            {
                NativeMethods.ERROR_ACCESS_DENIED =>
                    "目标进程内的调用被拒绝。这通常意味着目标进程受保护（PPL）或以更高权限运行。",
                NativeMethods.ERROR_INVALID_WINDOW_HANDLE => "窗口句柄在目标进程中已失效。",
                _ => $"目标进程返回失败，Win32 错误 {lastError}。",
            };

            _logger.Warn($"Cross-process affinity change failed for PID {window.ProcessId}: {message}");
            return ProtectionResult.Fail(message, scope);
        }

        var state = AffinityToState(affinity);
        _logger.Info($"Applied {state} to {window.ProcessName} (PID {window.ProcessId}) window {window.HandleHex}.");

        var note = state == CaptureProtectionState.Excluded && !SupportsExcludeFromCapture
            ? "（当前系统版本低于 19041，系统会按「黑块遮蔽」处理）"
            : string.Empty;

        return ProtectionResult.Ok(state, scope, $"已对「{window.ProcessName}」应用 {Describe(state)}{note}。");
    }

    private static CaptureProtectionState AffinityToState(uint affinity) => affinity switch
    {
        WdaExcludeFromCapture => CaptureProtectionState.Excluded,
        WdaMonitor => CaptureProtectionState.Blackout,
        _ => CaptureProtectionState.None,
    };

    /// <summary>Human-readable name of a protection state, in Chinese.</summary>
    public static string Describe(CaptureProtectionState state) => state switch
    {
        CaptureProtectionState.Excluded => "穿透隐身（截图/录屏中完全不出现）",
        CaptureProtectionState.Blackout => "黑块遮蔽（截图中显示为黑色方块）",
        _ => "无保护",
    };

    /// <summary>Asks every injected payload to unload. Best effort.</summary>
    public void ReleaseInjectedPayloads()
    {
        lock (_gate)
        {
            if (_injectedProcesses.Count == 0)
            {
                return;
            }

            _channel.Send(GuardCommandNative.Unload);
            _injectedProcesses.Clear();
        }

        _logger.Info("Requested all injected capture-guard payloads to unload.");
    }

    public void Dispose()
    {
        ReleaseInjectedPayloads();
        _channel.Dispose();
    }
}
