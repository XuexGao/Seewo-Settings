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
    /// <summary>
    /// The page currently on screen, used to attribute status messages.
    /// </summary>
    /// <remarks>
    /// Set by the shell as it navigates. A message reported while this is null belongs to
    /// the shell and is never cleared by navigation.
    /// </remarks>
    public string? CurrentPage { get; set; }

    /// <summary>
    /// Applies a theme to the live window, supplied by the shell once it exists.
    /// </summary>
    /// <remarks>
    /// A callback rather than a reference: the theme is a property of the window, and
    /// <c>Core</c> must not know about WinUI. The shell assigns this during startup and
    /// the settings page calls <see cref="ApplyTheme"/>, so the page never has to reach
    /// for a parent window by walking the visual tree.
    /// </remarks>
    public Action<AppTheme>? ThemeApplier { get; set; }

    /// <summary>Applies <see cref="AppSettings.Theme"/> to the window.</summary>
    public void ApplyTheme() => ThemeApplier?.Invoke(Settings.Theme);

    public void Report(string message, StatusSeverity severity = StatusSeverity.Informational) =>
        StatusReported?.Invoke(this, new StatusMessage(message, severity, CurrentPage));

    /// <summary>
    /// Reloads settings from disk and pushes them onto the services. Used after an
    /// import or an external edit.
    /// </summary>
    public void ReloadSettings()
    {
        Settings = SettingsStore.Load();
        ApplySettingsToServices();
    }

    /// <summary>Signature of the task list at the last scheduler reload.</summary>
    private string? _schedulerSignature;

    /// <summary>Pushes the current settings onto the services that cache them.</summary>
    /// <remarks>
    /// The scheduler is only reloaded when the task list actually differs from the last
    /// reload. <see cref="TaskSchedulerService.SetTasks"/> recomputes every task's next
    /// run and logs a line, and this method runs on every settings change - so without
    /// the comparison, changing the theme also reloaded the scheduler. The test report
    /// showed 50 such reloads in a single minute.
    /// </remarks>
    public void ApplySettingsToServices()
    {
        PrivacyMonitor.Options = Settings.ToPrivacyOptions();
        PrivacyNotifier.Settings = Settings;

        VirtualCamera.FrameWidth = Settings.VirtualCameraWidth;
        VirtualCamera.FrameHeight = Settings.VirtualCameraHeight;
        VirtualCamera.FramesPerSecond = Settings.VirtualCameraFps;

        ApplyScheduledTasks();
    }

    /// <summary>Reloads the scheduler only when the task list has changed.</summary>
    private void ApplyScheduledTasks()
    {
        var tasks = Settings.ScheduledTasks;
        var signature = SettingsStore.SerializeTasks(tasks);

        if (string.Equals(signature, _schedulerSignature, StringComparison.Ordinal))
        {
            return;
        }

        _schedulerSignature = signature;
        Scheduler.SetTasks(tasks);
    }

    /// <summary>Guards the debounce state below.</summary>
    private readonly object _saveGate = new();

    private Timer? _saveTimer;
    private bool _savePending;
    private string? _lastSavedJson;

    /// <summary>
    /// How long to wait for further changes before writing settings to disk.
    /// </summary>
    /// <remarks>
    /// Toggling a switch writes immediately, but dragging a slider or clicking several
    /// settings in a row produces a burst. Without this the file was rewritten on every
    /// keystroke - the test report measured 20 writes in one second and 50 in a minute,
    /// each accompanied by a scheduler reload and a DEBUG line.
    /// </remarks>
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>Persists settings and reports the outcome.</summary>
    /// <remarks>
    /// The write is debounced and the content is compared against the last write, so a
    /// burst of changes produces one save and an unchanged save produces none.
    /// </remarks>
    public bool SaveSettings()
    {
        // The scheduler owns the live task list, so copy it back before writing.
        Settings.ScheduledTasks = Scheduler.Tasks.ToList();

        lock (_saveGate)
        {
            _savePending = true;

            // Restart the window on every change, so a continuous stream of edits
            // coalesces into a single write once the user pauses.
            _saveTimer ??= new Timer(_ => FlushPendingSave(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
        }

        return true;
    }

    /// <summary>
    /// Writes settings now, cancelling any pending debounced save.
    /// </summary>
    /// <remarks>
    /// Called on shutdown and by anything that needs the file to be current immediately.
    /// </remarks>
    public bool SaveSettingsNow()
    {
        lock (_saveGate)
        {
            _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _savePending = true;
        }

        return FlushPendingSave();
    }

    /// <summary>Performs the deferred write, if one is outstanding.</summary>
    private bool FlushPendingSave()
    {
        string json;

        lock (_saveGate)
        {
            if (!_savePending)
            {
                return true;
            }

            _savePending = false;
            json = SettingsStore.Serialize(Settings);
        }

        // Applying settings to the services is cheap and idempotent, but the scheduler
        // reload is not: it recomputes every task's next run. Both are skipped when the
        // serialised content is byte-identical to the last write, which is what stops a
        // re-entrant change from causing another save.
        var unchanged = string.Equals(json, _lastSavedJson, StringComparison.Ordinal);

        ApplySettingsToServices();

        if (unchanged)
        {
            return true;
        }

        var result = SettingsStore.Save(Settings);

        if (result.Success)
        {
            _lastSavedJson = json;
        }
        else
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
        // Flush any debounced save before anything else. A change made in the last
        // fraction of a second before exit would otherwise be lost, which is exactly the
        // kind of bug that only shows up as "my setting did not stick" much later.
        try
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            SaveSettingsNow();
        }
        catch (Exception ex)
        {
            _logger.Error("Flushing pending settings on shutdown failed.", ex);
        }

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

/// <summary>
/// A message for the shell's status bar.
/// </summary>
/// <param name="Text">The message to show.</param>
/// <param name="Severity">How prominent it is.</param>
/// <param name="SourcePage">
/// The page that produced it, or null for a message that belongs to the shell itself.
/// </param>
/// <remarks>
/// The source is recorded so the shell can drop a message when the user navigates away
/// from the page it came from. Without it the bar kept showing, for example, "默认保护方式
/// 已设为「穿透隐身」" while the Seewo page was open - the text described an action the
/// user had taken somewhere else entirely.
/// </remarks>
public sealed record StatusMessage(string Text, StatusSeverity Severity, string? SourcePage = null);
