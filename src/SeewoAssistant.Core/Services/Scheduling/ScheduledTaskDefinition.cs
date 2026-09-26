using SeewoAssistant.Core.Models;

namespace SeewoAssistant.Core.Services.Scheduling;

/// <summary>A named, cron-scheduled sequence of actions.</summary>
public sealed class ScheduledTaskDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name shown in the task list.</summary>
    public string Name { get; set; } = "新定时任务";

    /// <summary>Five-field cron expression, or a macro such as <c>@daily</c>.</summary>
    public string Cron { get; set; } = "0 8 * * *";

    public bool Enabled { get; set; } = true;

    /// <summary>Steps to run, in order.</summary>
    public List<ScheduledAction> Actions { get; set; } = [];

    /// <summary>When the task last ran to completion, in local time.</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>Outcome summary of the last run.</summary>
    public string LastRunResult { get; set; } = string.Empty;

    /// <summary>How many times this task has run.</summary>
    public long RunCount { get; set; }

    /// <summary>Cached next occurrence, refreshed whenever the expression changes.</summary>
    public DateTimeOffset? NextRunAt { get; set; }

    /// <summary>Optional note.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// When true, the task is also registered with the Windows Task Scheduler so it
    /// fires even when this app is not running.
    /// </summary>
    public bool RegisterWithWindowsTaskScheduler { get; set; }

    public override string ToString() => $"{Name} [{Cron}] ({Actions.Count} 个动作)";
}
