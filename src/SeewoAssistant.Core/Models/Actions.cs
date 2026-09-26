namespace SeewoAssistant.Core.Models;

/// <summary>
/// Every operation the app can perform. The scheduler composes these into timed
/// action sequences, so adding a feature means adding a kind here plus a handler in
/// <c>ActionExecutor</c>.
/// </summary>
public enum ActionKind
{
    // ---- module 1: virtual camera ----
    StartVirtualCamera,
    StopVirtualCamera,
    /// <summary>Pushes a solid colour. Parameter <c>color</c> is <c>#RRGGBB</c>.</summary>
    PushVirtualCameraColor,
    /// <summary>Pushes an image file. Parameter <c>path</c>.</summary>
    PushVirtualCameraImage,
    /// <summary>Pushes the built-in animated test pattern.</summary>
    PushVirtualCameraTestPattern,

    // ---- module 2: privacy monitor ----
    StartPrivacyMonitor,
    StopPrivacyMonitor,

    // ---- module 3: capture guard ----
    /// <summary>Protects a window. Parameters <c>mode</c> (exclude|blackout) and a target selector.</summary>
    ProtectWindow,
    UnprotectWindow,

    // ---- module 4: seewo control ----
    SuspendSeewo,
    ResumeSeewo,
    KillSeewo,
    BlockSeewoNetwork,
    UnblockSeewoNetwork,
    DisableSeewoStartup,
    EnableSeewoStartup,

    // ---- module 5: power ----
    Shutdown,
    Restart,
    Logoff,
    Lock,

    // ---- generic ----
    /// <summary>Shows a toast. Parameter <c>message</c>.</summary>
    Notify,
    /// <summary>Runs a program. Parameters <c>path</c>, optional <c>arguments</c>.</summary>
    RunProgram,
}

/// <summary>
/// How a window target is resolved when protecting or unprotecting a window.
/// </summary>
public enum WindowTargetKind
{
    /// <summary>Match the process that owns the window by image name, e.g. <c>notepad.exe</c>.</summary>
    ProcessName,
    /// <summary>Match the window class name exactly, e.g. <c>Notepad</c>.</summary>
    ClassName,
    /// <summary>Match a substring of the window title.</summary>
    TitleContains,
    /// <summary>Use the raw HWND value. Only stable within a single session.</summary>
    Handle,
}

/// <summary>Which display affinity to apply to a protected window.</summary>
public enum CaptureProtectionMode
{
    /// <summary><c>WDA_EXCLUDEFROMCAPTURE</c> (0x11): the window does not appear in captures at all.</summary>
    ExcludeFromCapture,

    /// <summary><c>WDA_MONITOR</c> (0x01): the window appears as a black rectangle in captures.</summary>
    Blackout,
}

/// <summary>How a protected window is reached.</summary>
public enum ProtectionScope
{
    /// <summary>Only windows owned by this process. Always safe, no injection.</summary>
    OwnProcess,

    /// <summary>Cross-process, requires injecting the payload. Opt-in only.</summary>
    CrossProcess,
}

/// <summary>A single step in a scheduled action sequence.</summary>
public sealed class ScheduledAction
{
    public ActionKind Kind { get; set; }

    /// <summary>
    /// Free-form parameters. Keys are documented per <see cref="ActionKind"/>.
    /// Kept as a dictionary so the settings file stays forward compatible.
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Seconds to wait before running this step, relative to the previous step.</summary>
    public int DelaySeconds { get; set; }

    /// <summary>When false, a failure in this step does not stop the remaining steps.</summary>
    public bool StopOnFailure { get; set; } = true;

    public string? Get(string key) =>
        Parameters.TryGetValue(key, out var value) ? value : null;

    public override string ToString() =>
        Parameters.Count == 0 ? Kind.ToString() : $"{Kind}({string.Join(", ", Parameters.Select(p => $"{p.Key}={p.Value}"))})";
}

/// <summary>Result of executing one action.</summary>
public sealed record ActionResult(bool Success, string Message, Exception? Exception = null)
{
    public static ActionResult Ok(string message = "OK") => new(true, message);
    public static ActionResult Fail(string message, Exception? ex = null) => new(false, message, ex);
}
