namespace SeewoAssistant.Core.Models;

/// <summary>Which capability is being used.</summary>
public enum PrivacyDevice
{
    Camera,
    Microphone,
}

/// <summary>Whether usage started or stopped.</summary>
public enum PrivacyUsageChange
{
    Started,
    Stopped,
}

/// <summary>
/// One observation that an application started or stopped using the camera or
/// microphone.
/// </summary>
public sealed class PrivacyUsageEvent
{
    public required PrivacyDevice Device { get; init; }

    public required PrivacyUsageChange Change { get; init; }

    /// <summary>Full path for a desktop app, or the package family name for a Store app.</summary>
    public required string ApplicationId { get; init; }

    /// <summary>Friendly name for display, e.g. <c>Zoom.exe</c> or a resolved package name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>True when the app came from the Store rather than a plain executable.</summary>
    public bool IsPackaged { get; init; }

    /// <summary>
    /// Time the transition was observed, in UTC.
    /// </summary>
    /// <remarks>
    /// Stored in UTC because the underlying consent-store timestamps are UTC and mixing
    /// kinds is how the display came to be eight hours off. Everything shown to the user
    /// goes through <see cref="ObservedAtLocal"/>.
    /// </remarks>
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// <see cref="ObservedAt"/> converted to the machine's local time zone.
    /// </summary>
    /// <remarks>
    /// The log line and the UI both previously formatted <see cref="ObservedAt"/>
    /// directly, so a user at UTC+8 saw "at 12:34:59" next to a local log timestamp of
    /// 20:35:00 - the recorded time appeared eight hours earlier than it happened, which
    /// undermines the whole point of a privacy audit trail.
    /// </remarks>
    public DateTimeOffset ObservedAtLocal => ObservedAt.ToLocalTime();

    /// <summary>Raw <c>LastUsedTimeStart</c>, when known.</summary>
    public long LastUsedTimeStart { get; init; }

    /// <summary>Raw <c>LastUsedTimeStop</c>, when known.</summary>
    public long LastUsedTimeStop { get; init; }

    /// <summary>Process IDs attributed to this application, when resolution succeeded.</summary>
    public IReadOnlyList<int> ProcessIds { get; init; } = Array.Empty<int>();

    /// <summary>Name of the device in Chinese, for messages.</summary>
    public string DeviceName => Device == PrivacyDevice.Camera ? "摄像头" : "麦克风";

    /// <summary>The best available label for the application.</summary>
    public string BestName =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
        : !string.IsNullOrWhiteSpace(ApplicationId) ? Path.GetFileName(ApplicationId)
        : "未知程序";

    public override string ToString() =>
        $"{DeviceName} {Change} by {BestName} at {ObservedAtLocal:HH:mm:ss}";
}
