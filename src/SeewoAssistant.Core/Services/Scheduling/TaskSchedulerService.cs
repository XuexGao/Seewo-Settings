using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Models;

namespace SeewoAssistant.Core.Services.Scheduling;

/// <summary>Progress report for one task run.</summary>
public sealed record TaskRunReport(
    string TaskId,
    string TaskName,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    bool Success,
    IReadOnlyList<(ScheduledAction Action, ActionResult Result)> Steps)
{
    public string Summary
    {
        get
        {
            var failed = Steps.Count(s => !s.Result.Success);
            return failed == 0
                ? $"成功执行 {Steps.Count} 个动作，耗时 {Duration.TotalSeconds:0.0} 秒。"
                : $"{Steps.Count - failed}/{Steps.Count} 个动作成功，{failed} 个失败。";
        }
    }
}

/// <summary>
/// Runs scheduled tasks while the app is alive.
/// </summary>
/// <remarks>
/// <para>
/// The scheduler ticks once every 30 seconds and runs any task whose next
/// occurrence has passed. A 30-second tick is used rather than a timer armed to the
/// exact next occurrence because the latter needs re-arming after every system
/// resume, daylight-saving change and clock adjustment; polling at this interval
/// costs nothing measurable and is robust to all of them.
/// </para>
/// <para>
/// A task that is still running when its next occurrence arrives is skipped rather
/// than started concurrently, and the skip is reported. Overlapping runs of the same
/// task are almost never what a user wants, and silently queueing them would make a
/// long-running action pile up.
/// </para>
/// <para>
/// Missed occurrences are not replayed. If the machine was asleep at 08:00 and wakes
/// at 09:00, the 08:00 run does not fire late; only the next scheduled occurrence
/// does. This is stated in the UI so the behaviour is not surprising.
/// </para>
/// </remarks>
public sealed class TaskSchedulerService : IAsyncDisposable
{
    /// <summary>How often the scheduler checks for due tasks.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly IAppLogger _logger;
    private readonly ActionExecutor _executor;
    private readonly object _gate = new();

    private readonly List<ScheduledTaskDefinition> _tasks = [];
    private readonly HashSet<string> _runningTaskIds = [];

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TaskSchedulerService(ActionExecutor executor, IAppLogger? logger = null)
    {
        _executor = executor;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised after every task run completes.</summary>
    public event EventHandler<TaskRunReport>? TaskCompleted;

    /// <summary>Raised when a task starts running.</summary>
    public event EventHandler<ScheduledTaskDefinition>? TaskStarted;

    /// <summary>True while the scheduler loop is running.</summary>
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>A snapshot of the configured tasks.</summary>
    public IReadOnlyList<ScheduledTaskDefinition> Tasks
    {
        get
        {
            lock (_gate)
            {
                return _tasks.ToList();
            }
        }
    }

    /// <summary>Replaces the task list and recomputes every next-occurrence time.</summary>
    public void SetTasks(IEnumerable<ScheduledTaskDefinition> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        lock (_gate)
        {
            _tasks.Clear();
            _tasks.AddRange(tasks);

            foreach (var task in _tasks)
            {
                RecomputeNextRun(task);
            }
        }

        _logger.Info($"Scheduler loaded {_tasks.Count} task(s).");
    }

    /// <summary>Starts the scheduler loop.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false })
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
        }

        _logger.Info("Task scheduler started.");
    }

    /// <summary>Stops the scheduler loop. Safe to call when it is not running.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;

        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Cancellation surfaced as a faulted wait.
        }
        finally
        {
            cts.Dispose();
        }

        _logger.Info("Task scheduler stopped.");
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RunDueTasksAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A failure in one tick must not kill the scheduler.
                    _logger.Error("Scheduler tick failed.", ex);
                }

                await Task.Delay(TickInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunDueTasksAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        List<ScheduledTaskDefinition> due;

        lock (_gate)
        {
            due = _tasks
                .Where(t => t.Enabled && t.NextRunAt is not null && t.NextRunAt <= now)
                .ToList();
        }

        foreach (var task in due)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            lock (_gate)
            {
                if (!_runningTaskIds.Add(task.Id))
                {
                    _logger.Warn($"Task '{task.Name}' is still running; skipping this occurrence.");
                    RecomputeNextRun(task, now);
                    continue;
                }
            }

            try
            {
                await RunTaskAsync(task, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _runningTaskIds.Remove(task.Id);
                    RecomputeNextRun(task, DateTimeOffset.Now);
                }
            }
        }
    }

    /// <summary>Runs a task immediately, regardless of its schedule.</summary>
    public async Task<TaskRunReport> RunNowAsync(
        ScheduledTaskDefinition task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        lock (_gate)
        {
            if (!_runningTaskIds.Add(task.Id))
            {
                return new TaskRunReport(
                    task.Id, task.Name, DateTimeOffset.Now, TimeSpan.Zero, false,
                    [(new ScheduledAction { Kind = ActionKind.Notify },
                      ActionResult.Fail("该任务正在运行中，已跳过本次手动执行。"))]);
            }
        }

        try
        {
            return await RunTaskAsync(task, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _runningTaskIds.Remove(task.Id);
                RecomputeNextRun(task, DateTimeOffset.Now);
            }
        }
    }

    private async Task<TaskRunReport> RunTaskAsync(
        ScheduledTaskDefinition task,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var steps = new List<(ScheduledAction, ActionResult)>();

        _logger.Info($"Running scheduled task '{task.Name}'.");
        TaskStarted?.Invoke(this, task);

        var overallSuccess = true;

        foreach (var action in task.Actions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                steps.Add((action, ActionResult.Fail("应用正在退出，已中止。")));
                overallSuccess = false;
                break;
            }

            // Honour the inter-step delay before running the action.
            if (action.DelaySeconds > 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(action.DelaySeconds), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    steps.Add((action, ActionResult.Fail("等待期间应用退出，已中止。")));
                    overallSuccess = false;
                    break;
                }
            }

            var result = await _executor.ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
            steps.Add((action, result));

            if (!result.Success)
            {
                _logger.Warn($"Action {action.Kind} in task '{task.Name}' failed: {result.Message}");

                if (action.StopOnFailure)
                {
                    overallSuccess = false;
                    break;
                }

                overallSuccess = false;
            }
        }

        var report = new TaskRunReport(
            task.Id, task.Name, startedAt, DateTimeOffset.Now - startedAt, overallSuccess, steps);

        task.LastRunAt = startedAt;
        task.LastRunResult = report.Summary;
        task.RunCount++;

        _logger.Info($"Task '{task.Name}' finished: {report.Summary}");
        TaskCompleted?.Invoke(this, report);

        return report;
    }

    /// <summary>Recomputes a task's next occurrence from its cron expression.</summary>
    private void RecomputeNextRun(ScheduledTaskDefinition task, DateTimeOffset? from = null)
    {
        if (!task.Enabled)
        {
            task.NextRunAt = null;
            return;
        }

        if (!CronExpression.TryParse(task.Cron, out var cron, out var error) || cron is null)
        {
            task.NextRunAt = null;
            _logger.Warn($"Task '{task.Name}' has an invalid cron expression '{task.Cron}': {error}");
            return;
        }

        task.NextRunAt = cron.GetNextOccurrence(from ?? DateTimeOffset.Now);
    }

    /// <summary>Validates a cron expression and returns its human description.</summary>
    public static (bool Valid, string Description, string? Error) ValidateCron(string expression)
    {
        if (!CronExpression.TryParse(expression, out var cron, out var error) || cron is null)
        {
            return (false, string.Empty, error);
        }

        var next = cron.GetNextOccurrence(DateTimeOffset.Now);
        var nextText = next is null
            ? "（该表达式永远不会匹配任何时间）"
            : $"下次执行：{next.Value.LocalDateTime:yyyy-MM-dd HH:mm}";

        return (true, $"{cron.Describe()}。{nextText}", null);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
