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

    /// <summary>
    /// Applies the affinity inside the target process with a short machine-code stub.
    /// </summary>
    /// <remarks>
    /// A persistent injected DLL and its shared-memory channel were removed in favour of
    /// this. See <see cref="ApplyCrossProcess"/> for why.
    /// </remarks>
    private readonly ShellcodeAffinitySetter _affinitySetter;

    public CaptureGuardService(string? payloadDirectory = null, IAppLogger? logger = null)
    {
        _ = payloadDirectory;

        _logger = logger ?? NullLogger.Instance;
        _enumerator = new WindowEnumerator(_logger);
        _affinitySetter = new ShellcodeAffinitySetter(_logger);
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

    /// <summary>
    /// True when cross-process protection can be attempted on this machine.
    /// </summary>
    /// <remarks>
    /// The stub is built at runtime, so unlike the previous payload-DLL design there is
    /// no file that can be missing from the package. The remaining requirement is the OS
    /// itself: <c>SetWindowDisplayAffinity</c> and the ability to create a remote thread
    /// both exist from Windows 10 2004 onwards.
    /// </remarks>
    public bool IsCrossProcessAvailable =>
        OperatingSystem.IsWindows() &&
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, ExcludeFromCaptureMinimumBuild);

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

        if (ShellcodeAffinitySetter.IsForbiddenProcess(window.ProcessName))
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
    /// <summary>
    /// Applies the affinity to a window owned by another process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows only allows <c>SetWindowDisplayAffinity</c> on a window the calling
    /// process owns, so the call has to be made inside the target. That is done with a
    /// short machine-code stub, which is what <see cref="ShellcodeAffinitySetter"/>
    /// exists for.
    /// </para>
    /// <para>
    /// This replaces an earlier design that injected a persistent DLL and talked to it
    /// over a named shared-memory channel. That approach needed the two sides to agree
    /// on a namespace and on a memory layout, and any disagreement produced no error at
    /// all - just a reply that never arrived, reported to the user as a timeout. It also
    /// left a DLL resident in a third-party process for up to ten minutes, which is the
    /// behaviour antivirus heuristics are built to catch. The stub has none of those
    /// properties: it is one call, it needs no imports or shared state, and it is freed
    /// as soon as it returns.
    /// </para>
    /// </remarks>
    private ProtectionResult ApplyCrossProcess(WindowInfo window, uint affinity, ProtectionScope scope)
    {
        var result = _affinitySetter.SetAffinity(window.ProcessId, window.Handle, affinity);

        if (!result.Success)
        {
            _logger.Warn($"Cross-process affinity change failed for PID {window.ProcessId}: {result.Message}");
            return ProtectionResult.Fail(result.Message, scope, result.Exception);
        }

        var state = AffinityToState(affinity);

        _logger.Info(
            $"Applied {state} to {window.ProcessName} (PID {window.ProcessId}) " +
            $"window {window.HandleHex} via remote stub.");

        var note = state == CaptureProtectionState.Excluded && !SupportsExcludeFromCapture
            ? "（当前系统版本低于 19041，系统会按「黑块遮蔽」处理）"
            : string.Empty;

        return ProtectionResult.Ok(
            state, scope,
            $"已对「{window.ProcessName}」应用 {Describe(state)}{note}。");
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

    /// <summary>
    /// Retained for callers. Nothing stays loaded in a target process any more, so there
    /// is nothing to release.
    /// </summary>
    /// <remarks>
    /// The previous design kept a DLL resident in each target for up to ten minutes and
    /// needed an explicit unload. The remote stub frees itself as soon as the call
    /// returns, so this is now a no-op rather than a real teardown step.
    /// </remarks>
    public void ReleaseInjectedPayloads()
    {
    }

    public void Dispose()
    {
    }
}
