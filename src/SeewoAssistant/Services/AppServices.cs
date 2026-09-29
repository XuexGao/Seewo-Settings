using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Configuration;
using SeewoAssistant.Core.Services.CaptureGuard;
using SeewoAssistant.Core.Services.Desktop;
using SeewoAssistant.Core.Services.Power;
using SeewoAssistant.Core.Services.Privacy;
using SeewoAssistant.Core.Services.Scheduling;
using SeewoAssistant.Core.Services.Seewo;
using SeewoAssistant.Core.Services.VirtualCamera;

namespace SeewoAssistant.Services;

/// <summary>
/// Composition root. Owns every long-lived service and disposes them in order.
/// </summary>
/// <remarks>
/// Constructed once at startup and passed to each page's view model. A plain class
/// rather than a DI container: the graph is small, fixed and ordered, and explicit
/// construction makes the shutdown sequence — which matters here, because suspended
/// processes must be resumed and injected payloads unloaded — obvious and testable.
/// </remarks>
public sealed class AppServices : IAsyncDisposable
{
    private readonly FileLogger _logger;

    public AppServices()
    {
        _logger = new FileLogger(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeewoAssistant",
            "logs"));

        SettingsStore = new SettingsStore(logger: _logger);
        Settings = SettingsStore.Load();

        VirtualCamera = new VirtualCameraService(logger: _logger)
        {
            FrameWidth = Settings.VirtualCameraWidth,
            FrameHeight = Settings.VirtualCameraHeight,
            FramesPerSecond = Settings.VirtualCameraFps,
        };

        PrivacyNotifier = new PrivacyNotifier(Settings, _logger);
        PrivacyMonitor = new PrivacyMonitorService(PrivacyNotifier, _logger)
        {
            Options = Settings.ToPrivacyOptions(),
        };

        CaptureGuard = new CaptureGuardService(logger: _logger);

        // Hiding and restoring desktop windows. Stateless between operations, so it
        // needs nothing from settings.
        WindowHider = new WindowHiderService(_logger);
        SeewoControl = new SeewoControlService(logger: _logger);
        Firewall = new FirewallService(_logger);
        StartupManager = new StartupManagerService(_logger);
        AppStartup = new AppStartupService(_logger);
        Power = new PowerService(_logger);

        ActionExecutor = new ActionExecutor(
            VirtualCamera, PrivacyMonitor, CaptureGuard,
            SeewoControl, Firewall, StartupManager, Power, _logger);

        Scheduler = new TaskSchedulerService(ActionExecutor, _logger);
        Scheduler.SetTasks(Settings.ScheduledTasks);
    }

    public IAppLogger Logger => _logger;

    public FileLogger FileLogger => _logger;

    public SettingsStore SettingsStore { get; }

    /// <summary>The live settings object. Mutated by the UI and persisted on save.</summary>
    public AppSettings Settings { get; private set; }

    public VirtualCameraService VirtualCamera { get; }

    public PrivacyMonitorService PrivacyMonitor { get; }

    public PrivacyNotifier PrivacyNotifier { get; }

    public CaptureGuardService CaptureGuard { get; }

    /// <summary>Hides and restores desktop windows on demand.</summary>
    public WindowHiderService WindowHider { get; }

    public SeewoControlService SeewoControl { get; }

    public FirewallService Firewall { get; }

    public StartupManagerService StartupManager { get; }

    /// <summary>Registers this app in the per-user Run key.</summary>
    public AppStartupService AppStartup { get; }

    public PowerService Power { get; }

    public ActionExecutor ActionExecutor { get; }

    public TaskSchedulerService Scheduler { get; }

    /// <summary>Raised whenever any service wants to surface a transient message.</summary>
    public event EventHandler<StatusMessage>? StatusReported;

    /// <summary>Publishes a message to the shell's status bar.</summary>
    public void Report(string message, StatusSeverity severity = StatusSeverity.Informational) =>
        StatusReported?.Invoke(this, new StatusMessage(message, severity));

    /// <summary>
    /// Reloads settings from disk and pushes them onto the services. Used after an
    /// import or an external edit.
    /// </summary>
    public void ReloadSettings()
    {
        Settings = SettingsStore.Load();
        ApplySettingsToServices();
    }

    /// <summary>Pushes the current settings onto the services that cache them.</summary>
    public void ApplySettingsToServices()
    {
        PrivacyMonitor.Options = Settings.ToPrivacyOptions();
        PrivacyNotifier.Settings = Settings;

        VirtualCamera.FrameWidth = Settings.VirtualCameraWidth;
        VirtualCamera.FrameHeight = Settings.VirtualCameraHeight;
        VirtualCamera.FramesPerSecond = Settings.VirtualCameraFps;

        Scheduler.SetTasks(Settings.ScheduledTasks);
    }

    /// <summary>Persists settings and reports the outcome.</summary>
    public bool SaveSettings()
    {
        // The scheduler owns the live task list, so copy it back before writing.
        Settings.ScheduledTasks = Scheduler.Tasks.ToList();
        ApplySettingsToServices();

        var result = SettingsStore.Save(Settings);

        if (!result.Success)
        {
            Report(result.Message, StatusSeverity.Error);
        }

        return result.Success;
    }

    /// <summary>
    /// Starts the background services that should run whenever the app is alive.
    /// </summary>
    public void StartBackgroundServices()
    {
        if (Settings.PrivacyMonitorAutoStart)
        {
            PrivacyMonitor.Start();
        }

        Scheduler.Start();

        if (Settings.VirtualCameraAutoStart && VirtualCamera.DetectCapability().IsSupported)
        {
            _ = StartVirtualCameraAsync();
        }
    }

    private async Task StartVirtualCameraAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Settings.VirtualCameraDefaultImage))
            {
                var result = VirtualCamera.PushImage(Settings.VirtualCameraDefaultImage);
                if (!result.Success)
                {
                    VirtualCamera.PushSolidColor(Settings.VirtualCameraDefaultColor);
                }
            }
            else
            {
                VirtualCamera.PushSolidColor(Settings.VirtualCameraDefaultColor);
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.Error("Auto-starting the virtual camera failed.", ex);
        }
    }

    /// <summary>
    /// Shuts everything down in the reverse order of construction, restoring the
    /// machine to the state it was in before the app touched it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            Scheduler.Stop();
        }
        catch (Exception ex)
        {
            _logger.Error("Stopping the scheduler failed.", ex);
        }

        try
        {
            PrivacyMonitor.Stop();
        }
        catch (Exception ex)
        {
            _logger.Error("Stopping the privacy monitor failed.", ex);
        }

        try
        {
            // Resume anything this session suspended, so the user is never left with
            // a frozen application after the app exits.
            SeewoControl.ResumeAllSuspended();
        }
        catch (Exception ex)
        {
            _logger.Error("Resuming suspended processes failed.", ex);
        }

        try
        {
            // Ask injected payloads to unload rather than leaving them resident.
            CaptureGuard.Dispose();

            // Never leave the desktop hidden because the app exited while windows were
            // hidden - the user would have no way to bring them back.
            WindowHider.Restore();
        }
        catch (Exception ex)
        {
            _logger.Error("Releasing capture-guard payloads failed.", ex);
        }

        try
        {
            await VirtualCamera.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Stopping the virtual camera failed.", ex);
        }

        try
        {
            SaveSettings();
        }
        catch (Exception ex)
        {
            _logger.Error("Saving settings on exit failed.", ex);
        }

        _logger.Info("SeewoAssistant shut down.");
        _logger.Dispose();
    }
}

/// <summary>How prominent a status message is.</summary>
public enum StatusSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>A message for the shell's status bar.</summary>
public sealed record StatusMessage(string Text, StatusSeverity Severity);
