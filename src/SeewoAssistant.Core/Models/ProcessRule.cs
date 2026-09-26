namespace SeewoAssistant.Core.Models;

/// <summary>What to do with a matched process.</summary>
public enum ProcessAction
{
    Suspend,
    Resume,
    Terminate,
}

/// <summary>How a rule decides which processes it applies to.</summary>
public enum ProcessMatchKind
{
    /// <summary>Match the executable name, e.g. <c>EasiNote.exe</c>.</summary>
    ProcessName,

    /// <summary>Match the full image path, or a directory prefix when it ends with a separator.</summary>
    PathPrefix,

    /// <summary>Match the Authenticode signer subject, as a substring.</summary>
    Signer,
}

/// <summary>A rule selecting processes and describing what to do with them.</summary>
public sealed class ProcessRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name, shown in the rules list.</summary>
    public string Name { get; set; } = string.Empty;

    public ProcessMatchKind MatchKind { get; set; } = ProcessMatchKind.ProcessName;

    /// <summary>The value to match against, interpreted according to <see cref="MatchKind"/>.</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Whether this rule is evaluated at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether the rule should be applied automatically when a match appears.</summary>
    public bool AutoApply { get; set; }

    /// <summary>True for rules the app generated from a scan, as opposed to user-authored ones.</summary>
    public bool IsDiscovered { get; set; }

    /// <summary>Free-form note, e.g. which Seewo product this came from.</summary>
    public string Notes { get; set; } = string.Empty;

    public override string ToString() => $"{Name} ({MatchKind}:{Pattern})";
}

/// <summary>A process matched by a rule, with its live state.</summary>
public sealed class MatchedProcess
{
    public required int ProcessId { get; init; }

    public required string ProcessName { get; init; }

    public required string Path { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>Command line, when it could be read. Requires elevation.</summary>
    public string CommandLine { get; init; } = string.Empty;

    /// <summary>True when the process is currently suspended by this app.</summary>
    public bool IsSuspended { get; set; }

    /// <summary>True when an outbound firewall block rule exists for this executable.</summary>
    public bool IsNetworkBlocked { get; set; }

    public override string ToString() => $"{ProcessName} (PID {ProcessId})";
}

/// <summary>What kind of startup entry was found.</summary>
public enum StartupEntryKind
{
    /// <summary>A Windows Task Scheduler task.</summary>
    ScheduledTask,

    /// <summary>A registry Run key value.</summary>
    RegistryRun,

    /// <summary>A shortcut in a Startup folder.</summary>
    StartupFolder,

    /// <summary>A Windows service set to start automatically.</summary>
    Service,
}

/// <summary>A discovered auto-start entry.</summary>
public sealed class StartupEntry
{
    public required StartupEntryKind Kind { get; init; }

    /// <summary>Task path, registry value name, file name, or service name.</summary>
    public required string Name { get; init; }

    /// <summary>Where it lives: the task folder, registry key path, or file path.</summary>
    public required string Location { get; init; }

    /// <summary>The command or target the entry launches.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>True when the entry is currently enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether this app can change the entry's state.</summary>
    public bool CanToggle { get; init; } = true;

    /// <summary>Explains why the entry cannot be toggled, when it cannot.</summary>
    public string DisabledReason { get; init; } = string.Empty;

    public override string ToString() => $"[{Kind}] {Name} — {Location}";
}
