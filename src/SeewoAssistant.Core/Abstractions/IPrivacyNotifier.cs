using SeewoAssistant.Core.Models;

namespace SeewoAssistant.Core.Abstractions;

/// <summary>
/// Delivers the user-facing alert when an application starts using the camera or
/// microphone. Implemented in the UI project so the Core library stays free of any
/// Windows App SDK dependency.
/// </summary>
public interface IPrivacyNotifier
{
    Task NotifyAsync(PrivacyUsageEvent usageEvent, CancellationToken cancellationToken = default);
}

/// <summary>A notifier that swallows everything, for tests and headless runs.</summary>
public sealed class NullPrivacyNotifier : IPrivacyNotifier
{
    public static readonly NullPrivacyNotifier Instance = new();

    public Task NotifyAsync(PrivacyUsageEvent usageEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
