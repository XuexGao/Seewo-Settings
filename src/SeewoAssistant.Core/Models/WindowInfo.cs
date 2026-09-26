namespace SeewoAssistant.Core.Models;

/// <summary>How a window is protected against screen capture.</summary>
public enum CaptureProtectionState
{
    /// <summary>No protection; the window appears in captures normally.</summary>
    None,

    /// <summary><c>WDA_MONITOR</c>: the window is a black rectangle in captures.</summary>
    Blackout,

    /// <summary><c>WDA_EXCLUDEFROMCAPTURE</c>: the window does not appear at all.</summary>
    Excluded,
}

/// <summary>
/// A snapshot of a top-level window. Immutable so a selection cannot go stale
/// between the picker and the action that uses it.
/// </summary>
public sealed class WindowInfo
{
    public required nint Handle { get; init; }

    public required string Title { get; init; }

    public required string ClassName { get; init; }

    public required int ProcessId { get; init; }

    public required string ProcessName { get; init; }

    /// <summary>Full image path when it could be read; otherwise the process name.</summary>
    public string ProcessPath { get; init; } = string.Empty;

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Current display affinity, read back from the OS.</summary>
    public CaptureProtectionState Protection { get; init; }

    /// <summary>True when the window belongs to this process.</summary>
    public bool IsOwnProcess { get; init; }

    /// <summary>Handle as a hex string, which is how it is shown in the UI.</summary>
    public string HandleHex => $"0x{Handle:X}";

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Title)
            ? $"{ProcessName} [{ClassName}] {HandleHex}"
            : $"{Title} — {ProcessName} [{ClassName}] {HandleHex}";
}
