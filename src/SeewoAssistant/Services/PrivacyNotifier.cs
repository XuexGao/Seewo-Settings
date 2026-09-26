using System.Runtime.Versioning;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Configuration;
using SeewoAssistant.Core.Models;

namespace SeewoAssistant.Services;

/// <summary>
/// Delivers the camera/microphone alert as a Windows toast, and optionally as a
/// prominent on-screen banner.
/// </summary>
/// <remarks>
/// <para>
/// Windows App SDK's <c>AppNotificationManager</c> is used rather than the
/// WinRT <c>ToastNotificationManager</c> because the latter requires an AppUserModelID
/// registered through a Start-menu shortcut, which an unpackaged app does not have.
/// The Windows App SDK registration works for an unpackaged app directly.
/// </para>
/// <para>
/// The banner is offered as well because a toast can be suppressed by Focus Assist
/// or Do Not Disturb. A camera or microphone alert is exactly the case where a
/// silently swallowed notification is unacceptable, so the user can choose a
/// prominent alert that the shell cannot suppress.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PrivacyNotifier : IPrivacyNotifier, IDisposable
{
    private readonly IAppLogger _logger;
    private readonly object _gate = new();

    private bool _toastRegistered;
    private bool _registrationFailed;
    private BannerWindow? _banner;

    public PrivacyNotifier(AppSettings settings, IAppLogger? logger = null)
    {
        Settings = settings;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Live settings; replaced when the user saves changes.</summary>
    public AppSettings Settings { get; set; }

    /// <summary>Raised for every alert, so the UI can add it to the activity list.</summary>
    public event EventHandler<PrivacyUsageEvent>? AlertRaised;

    /// <summary>Registers the notification channel. Call once at startup.</summary>
    public void Initialize()
    {
        lock (_gate)
        {
            if (_toastRegistered || _registrationFailed)
            {
                return;
            }

            try
            {
                var manager = AppNotificationManager.Default;
                manager.NotificationInvoked += OnNotificationInvoked;
                manager.Register();

                _toastRegistered = true;
                _logger.Info("Toast notification channel registered.");
            }
            catch (Exception ex)
            {
                // Registration can fail on a locked-down machine. The banner path
                // still works, so this is a warning rather than a fatal error.
                _registrationFailed = true;
                _logger.Warn($"Toast registration failed; falling back to the in-app banner only. {ex.Message}");
            }
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // Clicking the toast brings the window forward. Marshalled to the UI thread
        // because the notification callback arrives on a background thread.
        App.Current?.UiDispatcher?.TryEnqueue(() => App.Current?.ShowMainWindow());
    }

    public Task NotifyAsync(PrivacyUsageEvent usageEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usageEvent);

        // Only a start is worth interrupting the user for. Reporting the stop as well
        // would double the noise for no benefit.
        if (usageEvent.Change != PrivacyUsageChange.Started)
        {
            return Task.CompletedTask;
        }

        AlertRaised?.Invoke(this, usageEvent);

        if (Settings.PrivacyShowToast)
        {
            ShowToast(usageEvent);
        }

        if (Settings.PrivacyShowBanner)
        {
            ShowBanner(usageEvent);
        }

        return Task.CompletedTask;
    }

    private void ShowToast(PrivacyUsageEvent usageEvent)
    {
        if (!_toastRegistered)
        {
            return;
        }

        try
        {
            var title = $"检测到程序正在使用{usageEvent.DeviceName}";

            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText($"{usageEvent.BestName} 正在使用{usageEvent.DeviceName}。")
                .AddText($"时间：{usageEvent.ObservedAt.LocalDateTime:HH:mm:ss}");

            if (usageEvent.ProcessIds.Count > 0)
            {
                builder.AddText($"进程 ID：{string.Join(", ", usageEvent.ProcessIds)}");
            }

            // The alert is the point of the feature, so it is marked urgent and given
            // a button that opens the app for a closer look.
            builder.SetScenario(AppNotificationScenario.Urgent);
            builder.AddButton(new AppNotificationButton("打开 SeewoAssistant")
                .AddArgument("action", "open"));

            if (Settings.PrivacyPlaySound)
            {
                // AppNotificationSoundEvent has no "Alert" member. The available
                // values are Alarm, Call, Default, IM, Mail, Reminder and SMS (plus
                // numbered Alarm/Call variants). Alarm is the urgent one, which is
                // what a camera or microphone alert warrants.
                builder.SetAudioEvent(AppNotificationSoundEvent.Alarm);
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
            _logger.Info($"Toast shown for {usageEvent.BestName} using the {usageEvent.Device}.");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Showing the toast failed: {ex.Message}");
        }
    }

    private void ShowBanner(PrivacyUsageEvent usageEvent)
    {
        // A banner must be created on the UI thread.
        App.Current?.UiDispatcher?.TryEnqueue(() =>
        {
            try
            {
                _banner ??= new BannerWindow();

                _banner.ShowAlert(
                    $"检测到程序正在使用{usageEvent.DeviceName}",
                    usageEvent.BestName,
                    usageEvent.ObservedAt.LocalDateTime,
                    TimeSpan.FromSeconds(Math.Max(2, Settings.PrivacyBannerSeconds)),
                    Settings.PrivacyPlaySound);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Showing the banner failed: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_toastRegistered)
            {
                return;
            }

            try
            {
                var manager = AppNotificationManager.Default;
                manager.NotificationInvoked -= OnNotificationInvoked;
                manager.Unregister();
            }
            catch (Exception ex)
            {
                _logger.Debug($"Unregistering notifications failed: {ex.Message}");
            }

            _toastRegistered = false;
        }

        _banner?.Close();
        _banner = null;
    }
}
