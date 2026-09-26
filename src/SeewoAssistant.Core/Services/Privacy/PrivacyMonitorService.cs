using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Models;

namespace SeewoAssistant.Core.Services.Privacy;

/// <summary>User-facing configuration for the privacy monitor.</summary>
public sealed class PrivacyMonitorOptions
{
    /// <summary>Alert when the camera is used.</summary>
    public bool MonitorCamera { get; set; } = true;

    /// <summary>Alert when the microphone is used.</summary>
    public bool MonitorMicrophone { get; set; } = true;

    /// <summary>
    /// Attribute a process ID by scanning handles. Requires elevation; when it is
    /// unavailable the monitor still reports the application path.
    /// </summary>
    public bool ResolveProcessIds { get; set; }

    /// <summary>
    /// Emit an alert for applications that are already using a device when the
    /// monitor starts, instead of only reporting new transitions.
    /// </summary>
    public bool ReportAlreadyInUseOnStart { get; set; }

    /// <summary>
    /// Executable names or full paths that should never raise an alert, compared
    /// case-insensitively. Matches on the file name as well as the full path.
    /// </summary>
    public List<string> ExcludedApplications { get; set; } = [];

    /// <summary>
    /// When true, only applications in <see cref="ExcludedApplications"/> are
    /// allowed to use a device silently; everything else alerts. When false the list
    /// is a plain ignore list. The two behave the same for alerting purposes, but
    /// the flag is surfaced in the UI because the intent differs.
    /// </summary>
    public bool TreatExclusionsAsAllowList { get; set; }

    /// <summary>Checks whether an application should raise an alert.</summary>
    public bool ShouldNotify(string applicationId)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            return true;
        }

        var fileName = SafeFileName(applicationId);

        foreach (var excluded in ExcludedApplications)
        {
            if (string.IsNullOrWhiteSpace(excluded))
            {
                continue;
            }

            if (applicationId.Equals(excluded, StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(excluded, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            // A package family name or a malformed registry key is not a path.
            return path;
        }
    }
}

/// <summary>
/// Watches the camera and microphone consent stores and raises an event whenever an
/// application starts or stops using either device.
/// </summary>
/// <remarks>
/// <para><b>Design.</b> Two registry keys are watched asynchronously with
/// <c>RegNotifyChangeKeyValue</c>, so there is no polling and no idle CPU cost. On
/// every notification the whole store is re-read and diffed against the previous
/// snapshot; only genuine transitions are reported. A snapshot diff is used rather
/// than per-key watching because the OS rewrites both timestamp values on a single
/// transition, which would otherwise produce duplicate events.</para>
/// <para><b>Why not a hook or ETW.</b> See the remarks on
/// <see cref="CapabilityAccessStore"/>: camera and microphone access is brokered
/// through Media Foundation into the FrameServer service, so a user-mode hook in an
/// observer process cannot see it, and the ETW providers that would carry it are
/// not a documented stable contract.</para>
/// <para><b>Re-arming.</b> The consent store keys do not exist on a machine where a
/// capability has never been used, and they are recreated when a privacy setting is
/// toggled. A slow retry timer therefore re-arms the watchers until they take.</para>
/// </remarks>
public sealed class PrivacyMonitorService : IAsyncDisposable
{
    /// <summary>How often to retry arming the registry watchers when they are not ready.</summary>
    private static readonly TimeSpan ArmRetryInterval = TimeSpan.FromSeconds(30);

    private readonly IAppLogger _logger;
    private readonly IPrivacyNotifier _notifier;
    private readonly ApplicationResolver _resolver;
    private readonly DeviceHandleScanner _handleScanner;

    private readonly List<RegistryChangeWatcher> _watchers = [];

    /// <summary>Devices whose consent-store key exists and is being watched.</summary>
    private readonly HashSet<PrivacyDevice> _armedDevices = [];
    private readonly Dictionary<string, bool> _lastKnownState = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PrivacyUsageEvent> _recent = [];
    private readonly object _gate = new();

    private Timer? _armRetryTimer;
    private volatile bool _running;
    private volatile bool _storeReady;

    public PrivacyMonitorService(
        IPrivacyNotifier? notifier = null,
        IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _notifier = notifier ?? NullPrivacyNotifier.Instance;
        _resolver = new ApplicationResolver(_logger);
        _handleScanner = new DeviceHandleScanner(_logger);
    }

    /// <summary>Current configuration. Changes take effect on the next evaluation.</summary>
    public PrivacyMonitorOptions Options { get; set; } = new();

    /// <summary>Raised for every transition that passes the exclusion filter.</summary>
    public event EventHandler<PrivacyUsageEvent>? UsageDetected;

    /// <summary>True while the monitor is armed.</summary>
    public bool IsRunning => _running;

    /// <summary>True when the consent store keys were found and the watchers are live.</summary>
    public bool IsStoreReady => _storeReady;

    /// <summary>
    /// The devices whose consent-store key actually exists and is being watched.
    /// Exposed so the UI can say precisely which devices are observable instead of
    /// implying that both are.
    /// </summary>
    public IReadOnlyCollection<PrivacyDevice> ArmedDevices
    {
        get
        {
            lock (_gate)
            {
                return _armedDevices.ToList();
            }
        }
    }

    /// <summary>The most recent events, newest first. Capped at 200 entries.</summary>
    public IReadOnlyList<PrivacyUsageEvent> RecentEvents
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToList();
            }
        }
    }

    /// <summary>Starts monitoring. Idempotent.</summary>
    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;

        // Establish the baseline before arming so that already-running usage is not
        // reported as a fresh transition unless the caller asked for it.
        EstablishBaseline();

        ArmWatchers();

        _armRetryTimer = new Timer(_ => ArmWatchers(), null, ArmRetryInterval, ArmRetryInterval);
        _logger.Info("Privacy monitor started.");
    }

    /// <summary>Stops monitoring and releases the registry watchers. Idempotent.</summary>
    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;

        _armRetryTimer?.Dispose();
        _armRetryTimer = null;

        lock (_gate)
        {
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        _storeReady = false;

        lock (_gate)
        {
            _armedDevices.Clear();
        }

        _logger.Info("Privacy monitor stopped.");
    }

    /// <summary>
    /// Reads the current store and records the initial state without raising events,
    /// unless <see cref="PrivacyMonitorOptions.ReportAlreadyInUseOnStart"/> is set.
    /// </summary>
    private void EstablishBaseline()
    {
        foreach (var device in EnumerateEnabledDevices())
        {
            var capability = ToCapabilityName(device);

            foreach (var usage in CapabilityAccessStore.Read(capability))
            {
                var key = BuildStateKey(device, usage.ApplicationId);
                var inUse = usage.IsInUse;

                lock (_gate)
                {
                    _lastKnownState[key] = inUse;
                }

                if (inUse && Options.ReportAlreadyInUseOnStart)
                {
                    RaiseEvent(device, PrivacyUsageChange.Started, usage);
                }
            }
        }
    }

    private void ArmWatchers()
    {
        if (!_running)
        {
            return;
        }

        lock (_gate)
        {
            if (_watchers.Count > 0 && _storeReady)
            {
                return;
            }
        }

        var armedAny = false;

        foreach (var device in EnumerateEnabledDevices())
        {
            var capability = ToCapabilityName(device);
            var watcher = new RegistryChangeWatcher(
                CapabilityAccessStore.BuildKeyPath(capability),
                watchSubTree: true,
                onChanged: () => Evaluate(device));

            watcher.Start();

            lock (_gate)
            {
                _watchers.Add(watcher);
            }

            armedAny |= watcher.IsArmed;

            lock (_gate)
            {
                if (watcher.IsArmed)
                {
                    _armedDevices.Add(device);
                }
                else
                {
                    _armedDevices.Remove(device);
                }
            }

            if (!watcher.IsArmed)
            {
                _logger.Debug(
                    $"Consent store key for '{capability}' is not present yet; will retry. " +
                    "This is normal until the capability has been used at least once.");
            }
        }

        _storeReady = armedAny;

        if (!armedAny)
        {
            // Fall back to a slow poll so a machine whose keys appear later is still
            // picked up, and so a transition that happens between arm attempts is not
            // missed entirely. One read per device per 30 seconds is negligible.
            EvaluateAll();
        }
    }

    /// <summary>Re-reads one device's store and reports transitions.</summary>
    private void Evaluate(PrivacyDevice device)
    {
        if (!_running)
        {
            return;
        }

        var capability = ToCapabilityName(device);
        List<CapabilityAccessStore.AppUsage> current;

        try
        {
            current = CapabilityAccessStore.Read(capability);
        }
        catch (Exception ex)
        {
            _logger.Debug($"Reading consent store for '{capability}' failed: {ex.Message}");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var usage in current)
        {
            var key = BuildStateKey(device, usage.ApplicationId);
            seen.Add(key);

            bool previous;
            lock (_gate)
            {
                _lastKnownState.TryGetValue(key, out previous);
                _lastKnownState[key] = usage.IsInUse;
            }

            if (usage.IsInUse == previous)
            {
                continue;
            }

            RaiseEvent(
                device,
                usage.IsInUse ? PrivacyUsageChange.Started : PrivacyUsageChange.Stopped,
                usage);
        }

        // An entry that disappears entirely means the app was uninstalled or its
        // history was cleared. If it was in use, treat that as a stop so the UI does
        // not keep showing a stale "in use" badge.
        lock (_gate)
        {
            foreach (var key in _lastKnownState.Keys.ToList())
            {
                if (!key.StartsWith(device.ToString(), StringComparison.OrdinalIgnoreCase) || seen.Contains(key))
                {
                    continue;
                }

                if (_lastKnownState[key])
                {
                    var applicationId = key[(key.IndexOf(':') + 1)..];
                    RaiseEvent(
                        device,
                        PrivacyUsageChange.Stopped,
                        new CapabilityAccessStore.AppUsage(applicationId, false, 0, 1));
                }

                _lastKnownState.Remove(key);
            }
        }
    }

    private void EvaluateAll()
    {
        foreach (var device in EnumerateEnabledDevices())
        {
            Evaluate(device);
        }
    }

    private void RaiseEvent(
        PrivacyDevice device,
        PrivacyUsageChange change,
        CapabilityAccessStore.AppUsage usage)
    {
        if (!Options.ShouldNotify(usage.ApplicationId))
        {
            _logger.Debug($"Suppressed {device} {change} for excluded application '{usage.ApplicationId}'.");
            return;
        }

        IReadOnlyList<int> processIds = Array.Empty<int>();

        if (Options.ResolveProcessIds && change == PrivacyUsageChange.Started)
        {
            try
            {
                processIds = _handleScanner.FindProcessesUsing(device);
            }
            catch (Exception ex)
            {
                _logger.Debug($"Process attribution failed for {device}: {ex.Message}");
            }
        }

        var usageEvent = new PrivacyUsageEvent
        {
            Device = device,
            Change = change,
            ApplicationId = usage.ApplicationId,
            DisplayName = _resolver.ResolveDisplayName(usage.ApplicationId, usage.IsPackaged),
            IsPackaged = usage.IsPackaged,
            LastUsedTimeStart = usage.Start,
            LastUsedTimeStop = usage.Stop,
            ProcessIds = processIds,
        };

        lock (_gate)
        {
            _recent.Insert(0, usageEvent);
            if (_recent.Count > 200)
            {
                _recent.RemoveRange(200, _recent.Count - 200);
            }
        }

        _logger.Info(usageEvent.ToString());

        // The event is raised on a registry watch thread. Consumers must marshal to
        // their own thread; the notifier implementation does that internally.
        UsageDetected?.Invoke(this, usageEvent);

        if (change == PrivacyUsageChange.Started)
        {
            _ = NotifySafeAsync(usageEvent);
        }
    }

    private async Task NotifySafeAsync(PrivacyUsageEvent usageEvent)
    {
        try
        {
            await _notifier.NotifyAsync(usageEvent).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error("Delivering the privacy notification failed.", ex);
        }
    }

    private IEnumerable<PrivacyDevice> EnumerateEnabledDevices()
    {
        if (Options.MonitorCamera)
        {
            yield return PrivacyDevice.Camera;
        }

        if (Options.MonitorMicrophone)
        {
            yield return PrivacyDevice.Microphone;
        }
    }

    private static string ToCapabilityName(PrivacyDevice device) =>
        device == PrivacyDevice.Camera
            ? CapabilityAccessStore.WebcamKey
            : CapabilityAccessStore.MicrophoneKey;

    private static string BuildStateKey(PrivacyDevice device, string applicationId) =>
        $"{device}:{applicationId}";

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
