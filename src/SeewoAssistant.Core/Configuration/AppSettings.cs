using System.Text.Json;
using System.Text.Json.Serialization;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Models;
using SeewoAssistant.Core.Services.CaptureGuard;
using SeewoAssistant.Core.Services.Privacy;
using SeewoAssistant.Core.Services.Scheduling;
using SeewoAssistant.Core.Services.Seewo;
using SeewoAssistant.Core.Services.VirtualCamera;

namespace SeewoAssistant.Core.Configuration;

/// <summary>How the app behaves when its window is closed.</summary>
public enum CloseBehavior
{
    /// <summary>Keep running in the notification area.</summary>
    MinimizeToTray,

    /// <summary>Exit the application.</summary>
    Exit,
}

/// <summary>The complete persisted application state.</summary>
public sealed class AppSettings
{
    /// <summary>Schema version, so a future migration can detect an old file.</summary>
    public int Version { get; set; } = 1;

    // ---------------------------------------------------------------- general

    /// <summary>What closing the main window does.</summary>
    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.MinimizeToTray;

    /// <summary>Start the app when the user signs in.</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>Start the app minimised to the tray.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>Follow the Windows light/dark theme instead of forcing light.</summary>
    public bool FollowSystemTheme { get; set; } = true;

    // ---------------------------------------------------------------- module 1

    /// <summary>Resolution the virtual camera frame pump renders at.</summary>
    public int VirtualCameraWidth { get; set; } = 1280;

    public int VirtualCameraHeight { get; set; } = 720;

    /// <summary>Publish rate for the frame pump.</summary>
    public int VirtualCameraFps { get; set; } = 30;

    /// <summary>Colour pushed on start, as <c>#RRGGBB</c>.</summary>
    public string VirtualCameraDefaultColor { get; set; } = "#1F6FEB";

    /// <summary>Optional image pushed on start; empty means use the colour.</summary>
    public string VirtualCameraDefaultImage { get; set; } = string.Empty;

    /// <summary>Whether the camera was left running, so it can be resumed on launch.</summary>
    public bool VirtualCameraAutoStart { get; set; }

    // ---------------------------------------------------------------- module 2

    public bool MonitorCamera { get; set; } = true;

    public bool MonitorMicrophone { get; set; } = true;

    /// <summary>Attribute a process ID to the event. Requires elevation.</summary>
    public bool PrivacyResolveProcessIds { get; set; }

    /// <summary>Show a toast notification when usage is detected.</summary>
    public bool PrivacyShowToast { get; set; } = true;

    /// <summary>Show a prominent on-screen banner in addition to the toast.</summary>
    public bool PrivacyShowBanner { get; set; }

    /// <summary>Seconds the banner stays on screen.</summary>
    public int PrivacyBannerSeconds { get; set; } = 6;

    /// <summary>Play a sound with the alert.</summary>
    public bool PrivacyPlaySound { get; set; } = true;

    /// <summary>Applications that never raise an alert.</summary>
    public List<string> PrivacyExcludedApplications { get; set; } = [];

    /// <summary>Start the privacy monitor automatically.</summary>
    public bool PrivacyMonitorAutoStart { get; set; } = true;

    // ---------------------------------------------------------------- module 3

    /// <summary>Which affinity the protect button applies by default.</summary>
    public CaptureProtectionMode CaptureDefaultMode { get; set; } = CaptureProtectionMode.ExcludeFromCapture;

    /// <summary>
    /// Whether cross-process injection is permitted. Off by default: it is the one
    /// feature that can be blocked by security software and can destabilise another
    /// process if misused, so the user must turn it on deliberately.
    /// </summary>
    public bool CaptureAllowCrossProcess { get; set; }

    /// <summary>True once the user has acknowledged the cross-process risk dialog.</summary>
    public bool CaptureCrossProcessAcknowledged { get; set; }

    /// <summary>Windows remembered as protected, so they can be re-applied on launch.</summary>
    public List<ProtectedWindowRecord> ProtectedWindows { get; set; } = [];

    // ---------------------------------------------------------------- module 4

    /// <summary>Rules describing which processes count as "Seewo software".</summary>
    public List<ProcessRule> SeewoRules { get; set; } = [];

    /// <summary>Process names currently suspended, so they can be resumed on exit.</summary>
    public List<string> SuspendedProcessNames { get; set; } = [];

    // ---------------------------------------------------------------- module 5

    public List<ScheduledTaskDefinition> ScheduledTasks { get; set; } = [];

    /// <summary>Default countdown before a scheduled shutdown fires.</summary>
    public int ShutdownCountdownSeconds { get; set; } = 60;

    /// <summary>Message shown in the shutdown dialog.</summary>
    public string ShutdownMessage { get; set; } = "SeewoAssistant 计划任务正在关机。";

    /// <summary>Seconds a scheduled notification stays on screen.</summary>
    public int NotificationSeconds { get; set; } = 5;

    // ---------------------------------------------------------------- helpers

    /// <summary>Projects the persisted values onto the privacy monitor options.</summary>
    public PrivacyMonitorOptions ToPrivacyOptions() => new()
    {
        MonitorCamera = MonitorCamera,
        MonitorMicrophone = MonitorMicrophone,
        ResolveProcessIds = PrivacyResolveProcessIds,
        ExcludedApplications = [.. PrivacyExcludedApplications],
    };

    /// <summary>Produces a deep copy, used to detect unsaved changes.</summary>
    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsJson.Options), SettingsJson.Options)
        ?? new AppSettings();
}

/// <summary>A window the user asked to keep protected across restarts.</summary>
public sealed class ProtectedWindowRecord
{
    public string ProcessName { get; set; } = string.Empty;

    public string ClassName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public CaptureProtectionMode Mode { get; set; } = CaptureProtectionMode.ExcludeFromCapture;

    /// <summary>Whether to re-apply this protection automatically on launch.</summary>
    public bool ReapplyOnLaunch { get; set; }

    public override string ToString() => $"{ProcessName} [{ClassName}]";
}

/// <summary>Shared JSON options so every read and write is consistent.</summary>
internal static class SettingsJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
        // Chinese text must survive round-tripping without being escaped into
        // \uXXXX sequences, so the file stays readable and hand-editable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in the user's local app data.
/// </summary>
/// <remarks>
/// Writes go to a temporary file that is then moved over the target, so an
/// interrupted save cannot leave a truncated settings file behind. A corrupt file is
/// renamed rather than deleted, so the user can recover it.
/// </remarks>
public sealed class SettingsStore
{
    private readonly IAppLogger _logger;
    private readonly object _gate = new();

    public SettingsStore(string? directory = null, IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeewoAssistant");
        FilePath = Path.Combine(Directory, "settings.json");
    }

    /// <summary>Directory holding the settings file and logs.</summary>
    public string Directory { get; }

    /// <summary>Full path of the settings file.</summary>
    public string FilePath { get; }

    /// <summary>Loads settings, falling back to defaults when none exist or the file is unreadable.</summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                _logger.Info($"No settings file at {FilePath}; using defaults.");
                return new AppSettings();
            }

            try
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, SettingsJson.Options);

                if (settings is null)
                {
                    throw new JsonException("配置文件反序列化结果为空。");
                }

                _logger.Info($"Loaded settings from {FilePath}.");
                return settings;
            }
            catch (JsonException ex)
            {
                // Preserve the damaged file so the user can inspect or recover it.
                var backup = FilePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                try
                {
                    File.Move(FilePath, backup, overwrite: true);
                    _logger.Error($"Settings file was invalid and has been moved to {backup}.", ex);
                }
                catch (IOException)
                {
                    _logger.Error("Settings file was invalid and could not be moved aside.", ex);
                }

                return new AppSettings();
            }
            catch (IOException ex)
            {
                _logger.Error("Could not read the settings file; using defaults.", ex);
                return new AppSettings();
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Error("Access denied reading the settings file; using defaults.", ex);
                return new AppSettings();
            }
        }
    }

    /// <summary>Saves settings atomically.</summary>
    public ActionResult Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                var json = JsonSerializer.Serialize(settings, SettingsJson.Options);
                var temporary = FilePath + ".tmp";

                File.WriteAllText(temporary, json);

                // File.Move with overwrite is atomic on the same volume, so a crash
                // here leaves either the old file or the new one, never a partial one.
                File.Move(temporary, FilePath, overwrite: true);

                _logger.Debug($"Saved settings to {FilePath}.");
                return ActionResult.Ok("设置已保存。");
            }
            catch (UnauthorizedAccessException ex)
            {
                return ActionResult.Fail($"没有权限写入 {FilePath}。", ex);
            }
            catch (IOException ex)
            {
                return ActionResult.Fail($"保存设置失败：{ex.Message}", ex);
            }
        }
    }

    /// <summary>Path of the rolling log file, for the diagnostics panel.</summary>
    public string LogFilePath => Path.Combine(Directory, "logs", "seewo-assistant.log");
}
