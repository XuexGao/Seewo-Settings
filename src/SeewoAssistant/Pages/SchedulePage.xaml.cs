using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SeewoAssistant.Core.Models;
using SeewoAssistant.Core.Services.Scheduling;

namespace SeewoAssistant.Pages;

/// <summary>
/// Module 5: composing the other modules into cron-scheduled action sequences, plus
/// the system power operations.
/// </summary>
/// <remarks>
/// The action editor builds its cards in code rather than from a XAML
/// <c>DataTemplate</c>, because each <see cref="ActionKind"/> needs a different set
/// of parameter fields. A template would need a data-template selector with a case
/// per kind, which is more code and harder to keep in step with the enum.
/// </remarks>
public sealed partial class SchedulePage : ModulePageBase
{
    private readonly ObservableCollection<TaskItem> _tasks = [];
    private readonly List<ActionCard> _actionCards = [];

    private ScheduledTaskDefinition? _editingTask;
    private bool _suppressCronValidation;

    /// <summary>Human-readable labels for each action kind, in menu order.</summary>
    private static readonly (ActionKind Kind, string Label, string Group)[] ActionCatalog =
    [
        (ActionKind.StartVirtualCamera, "启动虚拟摄像头（推送测试画面）", "虚拟摄像头"),
        (ActionKind.StopVirtualCamera, "停止虚拟摄像头推流", "虚拟摄像头"),
        (ActionKind.PushVirtualCameraColor, "推送纯色画面", "虚拟摄像头"),
        (ActionKind.PushVirtualCameraImage, "推送本地图片", "虚拟摄像头"),
        (ActionKind.PushVirtualCameraTestPattern, "推送动态测试画面", "虚拟摄像头"),

        (ActionKind.StartPrivacyMonitor, "开启摄像头/麦克风监控", "隐私监控"),
        (ActionKind.StopPrivacyMonitor, "停止摄像头/麦克风监控", "隐私监控"),

        (ActionKind.ProtectWindow, "保护窗口（防截屏）", "防截屏"),
        (ActionKind.UnprotectWindow, "取消窗口保护", "防截屏"),

        (ActionKind.SuspendSeewo, "挂起希沃软件", "希沃软件"),
        (ActionKind.ResumeSeewo, "恢复希沃软件", "希沃软件"),
        (ActionKind.KillSeewo, "结束希沃软件进程", "希沃软件"),
        (ActionKind.BlockSeewoNetwork, "禁止希沃软件联网", "希沃软件"),
        (ActionKind.UnblockSeewoNetwork, "恢复希沃软件联网", "希沃软件"),
        (ActionKind.DisableSeewoStartup, "禁用希沃开机自启", "希沃软件"),
        (ActionKind.EnableSeewoStartup, "恢复希沃开机自启", "希沃软件"),

        (ActionKind.Shutdown, "关机", "电源"),
        (ActionKind.Restart, "重启", "电源"),
        (ActionKind.Logoff, "注销", "电源"),
        (ActionKind.Lock, "锁定工作站", "电源"),

        (ActionKind.Notify, "显示通知", "其他"),
        (ActionKind.RunProgram, "运行程序", "其他"),
    ];

    public SchedulePage()
    {
        InitializeComponent();

        TaskList.ItemsSource = _tasks;
    }

    protected override void OnServicesReady()
    {
        CountdownBox.Value = Services.Settings.ShutdownCountdownSeconds;
        ShutdownMessageBox.Text = Services.Settings.ShutdownMessage;

        ReloadTasks();
        StartNewTask();
    }

    // ------------------------------------------------------------------ task list

    private void ReloadTasks()
    {
        _tasks.Clear();

        foreach (var task in Services.Scheduler.Tasks)
        {
            _tasks.Add(new TaskItem(task));
        }

        NoTasksText.Visibility = _tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRefreshTasks(object sender, RoutedEventArgs e)
    {
        ReloadTasks();
        Report("任务列表已刷新。");
    }

    private void OnTaskToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: TaskItem item } toggle)
        {
            return;
        }

        // Same guard as the Seewo startup list: a refresh re-presents the tasks, the
        // binding sets IsOn, and Toggled fires without the user having clicked anything.
        // The switch's own value is read so the decision does not depend on whether the
        // binding has written back yet.
        var target = toggle.IsOn;

        if (target == item.Task.Enabled)
        {
            item.Enabled = target;
            return;
        }

        item.Enabled = target;
        item.Task.Enabled = target;

        // The scheduler caches next-run times, so re-applying the list is what makes
        // a toggle take effect immediately rather than at the next tick.
        Services.Scheduler.SetTasks(Services.Scheduler.Tasks);
        Services.SaveSettings();
        ReloadTasks();

        Report($"任务「{item.Task.Name}」已{(target ? "启用" : "停用")}。");
    }

    private async void OnRunTaskNow(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TaskItem item })
        {
            return;
        }

        if (item.Task.Actions.Count == 0)
        {
            Report($"任务「{item.Task.Name}」没有任何动作。", StatusSeverity.Warning);
            return;
        }

        await RunGuardedAsync($"执行任务「{item.Task.Name}」", async () =>
        {
            Report($"正在执行任务「{item.Task.Name}」…");

            var report = await Services.Scheduler.RunNowAsync(item.Task);

            ReloadTasks();

            Report(
                $"任务「{item.Task.Name}」：{report.Summary}",
                report.Success ? StatusSeverity.Success : StatusSeverity.Warning);
        });
    }

    private void OnEditTask(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TaskItem item })
        {
            return;
        }

        LoadIntoEditor(item.Task);
    }

    private void OnDuplicateTask(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TaskItem item })
        {
            return;
        }

        // Deep copy through CloneAction so the copy shares nothing with the original.
        var copy = new ScheduledTaskDefinition
        {
            Name = item.Task.Name + "（副本）",
            Cron = item.Task.Cron,
            // The copy starts disabled so a duplicate cannot silently double up on a
            // schedule the user has not reviewed yet.
            Enabled = false,
            Notes = item.Task.Notes,
            Actions = item.Task.Actions.Select(CloneAction).ToList(),
        };

        var tasks = Services.Scheduler.Tasks.ToList();
        tasks.Add(copy);
        Services.Scheduler.SetTasks(tasks);
        Services.SaveSettings();
        ReloadTasks();

        Report($"已复制任务「{item.Task.Name}」。副本默认为停用状态。", StatusSeverity.Success);
    }

    private async void OnDeleteTask(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TaskItem item })
        {
            return;
        }

        if (!await ConfirmAsync(
                "删除任务",
                $"确定要删除任务「{item.Task.Name}」吗？这个操作不可撤销。",
                "删除"))
        {
            return;
        }

        var tasks = Services.Scheduler.Tasks.ToList();
        tasks.RemoveAll(t => t.Id == item.Task.Id);

        Services.Scheduler.SetTasks(tasks);
        Services.SaveSettings();
        ReloadTasks();

        Report($"已删除任务「{item.Task.Name}」。", StatusSeverity.Success);
    }

    private static ScheduledAction CloneAction(ScheduledAction source) => new()
    {
        Kind = source.Kind,
        DelaySeconds = source.DelaySeconds,
        StopOnFailure = source.StopOnFailure,
        Parameters = new Dictionary<string, string>(source.Parameters, StringComparer.OrdinalIgnoreCase),
    };

    // ------------------------------------------------------------------ editor

    private void OnNewTask(object sender, RoutedEventArgs e) => StartNewTask();

    private void StartNewTask()
    {
        _editingTask = null;
        EditorHeader.Text = "新建任务";

        TaskNameBox.Text = string.Empty;

        _suppressCronValidation = true;
        CronBox.Text = "0 8 * * *";
        _suppressCronValidation = false;

        ClearActions();
        ValidateCron();

        Report("已切换到新建任务。填写名称、定时表达式和动作序列后保存。");
    }

    private void LoadIntoEditor(ScheduledTaskDefinition task)
    {
        _editingTask = task;
        EditorHeader.Text = $"编辑任务：{task.Name}";

        TaskNameBox.Text = task.Name;

        _suppressCronValidation = true;
        CronBox.Text = task.Cron;
        _suppressCronValidation = false;

        ClearActions();

        foreach (var action in task.Actions)
        {
            AddActionCard(CloneAction(action));
        }

        ValidateCron();
        Report($"正在编辑任务「{task.Name}」。");
    }

    private void OnCancelEdit(object sender, RoutedEventArgs e)
    {
        StartNewTask();
        Report("已取消编辑。");
    }

    private void ClearActions()
    {
        _actionCards.Clear();
        ActionList.Children.Clear();
        UpdateActionEmptyState();
    }

    private void UpdateActionEmptyState() =>
        NoActionsText.Visibility = _actionCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------------ cron

    private void OnQuickCron(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string expression })
        {
            // Assigning Text raises TextChanged; the handler would validate while the
            // rest of the editor is still empty. Set the guard and validate once here.
            _suppressCronValidation = true;
            CronBox.Text = expression;
            _suppressCronValidation = false;

            ValidateCron();
        }
    }

    private void OnCronChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressCronValidation)
        {
            ValidateCron();
        }
    }

    /// <summary>
    /// Validates the expression as the user types and shows the human description and
    /// the next run time, or the error. A wrong expression silently never fires, so
    /// this feedback is the main defence against a task that looks configured but
    /// never runs.
    /// </summary>
    private void ValidateCron()
    {
        var text = CronBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(text))
        {
            CronInfoBar.IsOpen = true;
            CronInfoBar.Severity = InfoBarSeverity.Warning;
            CronInfoBar.Message = "定时表达式不能为空。";
            return;
        }

        var (valid, description, error) = TaskSchedulerService.ValidateCron(text);

        CronInfoBar.IsOpen = true;

        if (valid)
        {
            CronInfoBar.Severity = InfoBarSeverity.Success;
            CronInfoBar.Message = description;
        }
        else
        {
            CronInfoBar.Severity = InfoBarSeverity.Error;
            CronInfoBar.Message = error ?? "表达式无效。";
        }
    }

    // ------------------------------------------------------------------ action cards

    private void OnAddAction(object sender, RoutedEventArgs e) => AddActionCard(new ScheduledAction
    {
        Kind = ActionKind.Notify,
        StopOnFailure = true,
        Parameters = { ["message"] = "希沃助手定时提醒" },
    });

    private void AddActionCard(ScheduledAction action)
    {
        var card = new ActionCard(action, ActionCatalog, this);
        _actionCards.Add(card);
        ActionList.Children.Add(card.Container);
        UpdateActionEmptyState();
    }

    private void RemoveActionCard(ActionCard card)
    {
        _actionCards.Remove(card);
        ActionList.Children.Remove(card.Container);
        UpdateActionEmptyState();
    }

    private void MoveActionCard(ActionCard card, int delta)
    {
        var index = _actionCards.IndexOf(card);
        var target = index + delta;

        if (index < 0 || target < 0 || target >= _actionCards.Count)
        {
            return;
        }

        _actionCards.RemoveAt(index);
        _actionCards.Insert(target, card);

        // Rebuild the panel so visual order matches the list order.
        ActionList.Children.Clear();

        foreach (var item in _actionCards)
        {
            ActionList.Children.Add(item.Container);
        }
    }

    // ------------------------------------------------------------------ save

    private void OnSaveTask(object sender, RoutedEventArgs e)
    {
        var name = TaskNameBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            Report("请填写任务名称。", StatusSeverity.Warning);
            return;
        }

        var cron = CronBox.Text?.Trim() ?? string.Empty;
        var (valid, _, error) = TaskSchedulerService.ValidateCron(cron);

        if (!valid)
        {
            Report($"定时表达式无效：{error}", StatusSeverity.Error);
            return;
        }

        if (_actionCards.Count == 0)
        {
            Report("任务至少需要一个动作。", StatusSeverity.Warning);
            return;
        }

        var actions = _actionCards.Select(c => c.ToAction()).ToList();

        var tasks = Services.Scheduler.Tasks.ToList();

        if (_editingTask is null)
        {
            tasks.Add(new ScheduledTaskDefinition
            {
                Name = name,
                Cron = cron,
                Enabled = true,
                Actions = actions,
            });

            Report($"已创建任务「{name}」。", StatusSeverity.Success);
        }
        else
        {
            var existing = tasks.FirstOrDefault(t => t.Id == _editingTask.Id);

            if (existing is null)
            {
                // The task was deleted in another view while being edited.
                Report("该任务已不存在，已改为新建。", StatusSeverity.Warning);
                tasks.Add(new ScheduledTaskDefinition
                {
                    Name = name,
                    Cron = cron,
                    Enabled = true,
                    Actions = actions,
                });
            }
            else
            {
                existing.Name = name;
                existing.Cron = cron;
                existing.Actions = actions;
            }

            Report($"已保存任务「{name}」。", StatusSeverity.Success);
        }

        Services.Scheduler.SetTasks(tasks);
        Services.SaveSettings();
        ReloadTasks();
        StartNewTask();
    }

    // ------------------------------------------------------------------ power

    private void PersistPowerSettings()
    {
        Services.Settings.ShutdownCountdownSeconds = (int)CountdownBox.Value;
        Services.Settings.ShutdownMessage = ShutdownMessageBox.Text ?? string.Empty;
    }

    private async void OnShutdown(object sender, RoutedEventArgs e)
    {
        PersistPowerSettings();

        var seconds = (int)CountdownBox.Value;

        if (!await ConfirmAsync(
                "关机",
                seconds > 0
                    ? $"系统将在 {seconds} 秒后关机，期间会显示倒计时提示，可以取消。\n\n确定要继续吗？"
                    : "倒计时为 0，系统会立即关机，没有取消的机会。\n\n确定要立即关机吗？",
                "关机"))
        {
            return;
        }

        var result = Services.Power.Initiate(
            Core.Services.Power.PowerAction.Shutdown,
            seconds,
            ShutdownMessageBox.Text);

        Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Error);
    }

    private async void OnRestart(object sender, RoutedEventArgs e)
    {
        PersistPowerSettings();

        var seconds = (int)CountdownBox.Value;

        if (!await ConfirmAsync(
                "重启",
                seconds > 0
                    ? $"系统将在 {seconds} 秒后重启，期间会显示倒计时提示，可以取消。\n\n确定要继续吗？"
                    : "倒计时为 0，系统会立即重启。\n\n确定要立即重启吗？",
                "重启"))
        {
            return;
        }

        var result = Services.Power.Initiate(
            Core.Services.Power.PowerAction.Restart,
            seconds,
            ShutdownMessageBox.Text);

        Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Error);
    }

    private async void OnLogoff(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "注销",
                "将注销当前用户，所有未保存的工作会丢失。确定要继续吗？",
                "注销"))
        {
            return;
        }

        var result = Services.Power.Initiate(Core.Services.Power.PowerAction.Logoff);
        Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Error);
    }

    private void OnLock(object sender, RoutedEventArgs e)
    {
        var result = Services.Power.Initiate(Core.Services.Power.PowerAction.Lock);
        Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Error);
    }

    private void OnAbortShutdown(object sender, RoutedEventArgs e)
    {
        var result = Services.Power.Abort();
        Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Warning);
    }

    // ------------------------------------------------------------------ task wrapper

    private sealed class TaskItem
    {
        internal TaskItem(ScheduledTaskDefinition task)
        {
            Task = task;
            Enabled = task.Enabled;
        }

        internal ScheduledTaskDefinition Task { get; }

        public string Name => Task.Name;

        public string Cron => Task.Cron;

        /// <summary>
        /// The state shown in the list, written back to the task only once applied.
        /// </summary>
        /// <remarks>
        /// Not a pass-through for the same reason as StartupItem: the two-way binding
        /// assigns this while the row container is created, which raises Toggled, and a
        /// pass-through would make that assignment indistinguishable from a click.
        /// </remarks>
        public bool Enabled { get; set; }

        public string Description
        {
            get
            {
                var (valid, description, error) = TaskSchedulerService.ValidateCron(Task.Cron);
                return valid ? description : $"表达式无效：{error}";
            }
        }

        public string NextRunLabel => Task.Enabled
            ? Task.NextRunAt is null
                ? "下次执行：未安排"
                : $"下次执行：{Task.NextRunAt.Value.LocalDateTime:MM-dd HH:mm}"
            : "已停用";

        public string LastRunLabel => Task.LastRunAt is null
            ? "从未执行"
            : $"上次执行：{Task.LastRunAt.Value.LocalDateTime:MM-dd HH:mm}（{Task.LastRunResult}）";

        public string ActionSummary => Task.Actions.Count == 0
            ? "没有动作"
            : $"{Task.Actions.Count} 个动作：" + string.Join(" → ", Task.Actions.Take(3).Select(DescribeAction)) +
              (Task.Actions.Count > 3 ? " …" : string.Empty);

        private static string DescribeAction(ScheduledAction action) => action.Kind switch
        {
            ActionKind.StartVirtualCamera => "启动摄像头",
            ActionKind.StopVirtualCamera => "停止摄像头",
            ActionKind.PushVirtualCameraColor => $"推送{action.Get("color") ?? "颜色"}",
            ActionKind.PushVirtualCameraImage => "推送图片",
            ActionKind.PushVirtualCameraTestPattern => "推送测试画面",
            ActionKind.StartPrivacyMonitor => "开启隐私监控",
            ActionKind.StopPrivacyMonitor => "停止隐私监控",
            ActionKind.ProtectWindow => "保护窗口",
            ActionKind.UnprotectWindow => "取消窗口保护",
            ActionKind.SuspendSeewo => "挂起希沃",
            ActionKind.ResumeSeewo => "恢复希沃",
            ActionKind.KillSeewo => "结束希沃",
            ActionKind.BlockSeewoNetwork => "禁止希沃联网",
            ActionKind.UnblockSeewoNetwork => "恢复希沃联网",
            ActionKind.DisableSeewoStartup => "禁用希沃自启",
            ActionKind.EnableSeewoStartup => "恢复希沃自启",
            ActionKind.Shutdown => "关机",
            ActionKind.Restart => "重启",
            ActionKind.Logoff => "注销",
            ActionKind.Lock => "锁定",
            ActionKind.Notify => "通知",
            ActionKind.RunProgram => "运行程序",
            _ => action.Kind.ToString(),
        };
    }

    /// <summary>
    /// One action in the editor: a kind picker, the parameter fields that kind needs,
    /// and the delay and failure-handling options.
    /// </summary>
    private sealed class ActionCard
    {
        private readonly ScheduledAction _action;
        private readonly (ActionKind Kind, string Label, string Group)[] _catalog;
        private readonly SchedulePage _page;
        private readonly StackPanel _parameterPanel;
        private readonly TextBlock _descriptionText;
        private readonly ComboBox _kindCombo;

        private readonly Dictionary<string, TextBox> _textFields = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ComboBox> _choiceFields = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ToggleSwitch> _toggleFields = new(StringComparer.OrdinalIgnoreCase);

        internal ActionCard(
            ScheduledAction action,
            (ActionKind Kind, string Label, string Group)[] catalog,
            SchedulePage page)
        {
            _action = action;
            _catalog = catalog;
            _page = page;

            _descriptionText = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };

            _kindCombo = new ComboBox
            {
                Header = "动作",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = catalog.Select(c => $"{c.Group} · {c.Label}").ToList(),
            };

            _kindCombo.SelectionChanged += (_, _) =>
            {
                var index = _kindCombo.SelectedIndex;
                if (index < 0 || index >= _catalog.Length)
                {
                    return;
                }

                _action.Kind = _catalog[index].Kind;
                RebuildParameters();
            };

            _parameterPanel = new StackPanel { Spacing = 8 };

            var upButton = IconButton("\uE74A", "上移", () => _page.MoveActionCard(this, -1));
            var downButton = IconButton("\uE74B", "下移", () => _page.MoveActionCard(this, 1));
            var removeButton = IconButton("\uE74D", "删除", () => _page.RemoveActionCard(this));

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Bottom,
                Children = { upButton, downButton, removeButton },
            };

            var headerRow = new Grid { ColumnSpacing = 12 };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_kindCombo, 0);
            Grid.SetColumn(buttonRow, 1);
            headerRow.Children.Add(_kindCombo);
            headerRow.Children.Add(buttonRow);

            var delayBox = new NumberBox
            {
                Header = "延迟（秒）",
                Value = action.DelaySeconds,
                Minimum = 0,
                Maximum = 3600,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Width = 150,
            };
            delayBox.ValueChanged += (_, args) =>
            {
                if (!double.IsNaN(args.NewValue))
                {
                    _action.DelaySeconds = (int)args.NewValue;
                }
            };

            var stopToggle = new ToggleSwitch
            {
                Header = "失败时停止",
                IsOn = action.StopOnFailure,
                OnContent = "停止",
                OffContent = "继续",
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            stopToggle.Toggled += (_, _) => _action.StopOnFailure = stopToggle.IsOn;

            var optionsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 16,
                Children = { delayBox, stopToggle },
            };

            var content = new StackPanel
            {
                Spacing = 10,
                Children = { headerRow, _descriptionText, _parameterPanel, optionsRow },
            };

            Container = new Border
            {
                Margin = new Thickness(0, 4, 0, 4),
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Child = content,
            };

            // Select the current kind without firing a rebuild before the fields exist.
            var kindIndex = Array.FindIndex(catalog, c => c.Kind == action.Kind);
            _kindCombo.SelectedIndex = kindIndex >= 0 ? kindIndex : 0;
            _action.Kind = _catalog[_kindCombo.SelectedIndex].Kind;

            RebuildParameters();
        }

        internal Border Container { get; }

        /// <summary>Reads the edited values back out of the card.</summary>
        internal ScheduledAction ToAction()
        {
            _action.Parameters.Clear();

            foreach (var (key, box) in _textFields)
            {
                var value = box.Text?.Trim();

                if (!string.IsNullOrEmpty(value))
                {
                    _action.Parameters[key] = value;
                }
            }

            foreach (var (key, combo) in _choiceFields)
            {
                if (combo.SelectedItem is ChoiceOption option)
                {
                    _action.Parameters[key] = option.Value;
                }
            }

            foreach (var (key, toggle) in _toggleFields)
            {
                _action.Parameters[key] = toggle.IsOn ? "true" : "false";
            }

            return _action;
        }

        private static Button IconButton(string glyph, string tooltip, Action onClick)
        {
            var button = new Button
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            };

            ToolTipService.SetToolTip(button, tooltip);
            button.Click += (_, _) => onClick();
            return button;
        }

        /// <summary>A combo box entry that carries the value written into the parameters.</summary>
        private sealed record ChoiceOption(string Label, string Value)
        {
            public override string ToString() => Label;
        }

        /// <summary>
        /// Rebuilds the parameter fields for the selected kind. Only the fields that
        /// kind actually uses are created, so the editor never shows an irrelevant
        /// option that would silently do nothing.
        /// </summary>
        private void RebuildParameters()
        {
            _parameterPanel.Children.Clear();
            _textFields.Clear();
            _choiceFields.Clear();
            _toggleFields.Clear();

            var kind = _action.Kind;
            _descriptionText.Text = Describe(kind);

            switch (kind)
            {
                case ActionKind.PushVirtualCameraColor:
                    AddTextField("color", "颜色值", "#RRGGBB", _action.Get("color") ?? "#1F6FEB");
                    break;

                case ActionKind.PushVirtualCameraImage:
                    AddTextField("path", "图片完整路径", @"C:\Pictures\logo.png", _action.Get("path") ?? string.Empty);
                    break;

                case ActionKind.ProtectWindow:
                case ActionKind.UnprotectWindow:
                    AddChoiceField("targetKind", "匹配方式",
                    [
                        new ChoiceOption("进程名", "processName"),
                        new ChoiceOption("窗口类名", "className"),
                        new ChoiceOption("标题包含", "title"),
                        new ChoiceOption("窗口句柄", "hwnd"),
                    ], _action.Get("targetKind") ?? "processName");

                    AddTextField("pattern", "匹配内容", "例如 EasiNote.exe 或 0x00123456",
                        _action.Get("pattern") ?? string.Empty);

                    if (kind == ActionKind.ProtectWindow)
                    {
                        AddChoiceField("mode", "保护模式",
                        [
                            new ChoiceOption("穿透隐身（截图中完全不出现）", "exclude"),
                            new ChoiceOption("黑块遮蔽（截图中显示为黑块）", "blackout"),
                        ], _action.Get("mode") ?? "exclude");
                    }

                    AddToggleField("crossProcess", "允许跨进程注入",
                        string.Equals(_action.Get("crossProcess"), "true", StringComparison.OrdinalIgnoreCase));
                    break;

                case ActionKind.SuspendSeewo:
                case ActionKind.ResumeSeewo:
                case ActionKind.KillSeewo:
                case ActionKind.BlockSeewoNetwork:
                case ActionKind.UnblockSeewoNetwork:
                    AddTextField("rules", "限定规则（可留空表示全部）", "留空 = 全部已发现的希沃程序",
                        _action.Get("rules") ?? string.Empty);
                    break;

                case ActionKind.Shutdown:
                case ActionKind.Restart:
                    AddTextField("timeoutSeconds", "倒计时（秒）", "60",
                        _action.Get("timeoutSeconds") ?? "60");
                    AddTextField("message", "提示语", "可选", _action.Get("message") ?? string.Empty);
                    break;

                case ActionKind.Notify:
                    AddTextField("message", "通知内容", "例如 该休息一下了", _action.Get("message") ?? string.Empty);
                    break;

                case ActionKind.RunProgram:
                    AddTextField("path", "程序路径", @"C:\Windows\notepad.exe", _action.Get("path") ?? string.Empty);
                    AddTextField("arguments", "启动参数", "可选", _action.Get("arguments") ?? string.Empty);
                    break;

                default:
                    // Kinds with no parameters show only their description.
                    break;
            }

            _parameterPanel.Visibility = _parameterPanel.Children.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void AddTextField(string key, string header, string placeholder, string value)
        {
            var box = new TextBox
            {
                Header = header,
                PlaceholderText = placeholder,
                Text = value,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            _textFields[key] = box;
            _parameterPanel.Children.Add(box);
        }

        private void AddChoiceField(string key, string header, ChoiceOption[] options, string current)
        {
            var combo = new ComboBox
            {
                Header = header,
                ItemsSource = options,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var index = Array.FindIndex(options, o => o.Value.Equals(current, StringComparison.OrdinalIgnoreCase));
            combo.SelectedIndex = index >= 0 ? index : 0;

            _choiceFields[key] = combo;
            _parameterPanel.Children.Add(combo);
        }

        private void AddToggleField(string key, string header, bool value)
        {
            var toggle = new ToggleSwitch
            {
                Header = header,
                IsOn = value,
                OnContent = "是",
                OffContent = "否",
            };

            _toggleFields[key] = toggle;
            _parameterPanel.Children.Add(toggle);
        }

        private static string Describe(ActionKind kind) => kind switch
        {
            ActionKind.StartVirtualCamera => "启动虚拟摄像头并推送测试画面。如果同时配置了「推送图片」，那一步会覆盖它。",
            ActionKind.StopVirtualCamera => "停止推流。摄像头会回退到内置的动态测试画面，不会黑屏。",
            ActionKind.PushVirtualCameraColor => "向虚拟摄像头推送一个纯色画面。",
            ActionKind.PushVirtualCameraImage => "向虚拟摄像头推送一张本地图片，按比例缩放居中。",
            ActionKind.PushVirtualCameraTestPattern => "推送彩条 + 移动扫描条，用于确认摄像头工作正常。",
            ActionKind.StartPrivacyMonitor => "开始监控摄像头和麦克风的调用并弹窗提醒。",
            ActionKind.StopPrivacyMonitor => "停止监控。",
            ActionKind.ProtectWindow => "对匹配的窗口应用防截屏保护。跨进程保护需要显式开启，并可能被杀软拦截。",
            ActionKind.UnprotectWindow => "取消匹配窗口的防截屏保护。",
            ActionKind.SuspendSeewo => "挂起希沃进程（完全冻结，可恢复）。退出程序时会自动恢复。",
            ActionKind.ResumeSeewo => "恢复被挂起的希沃进程。",
            ActionKind.KillSeewo => "强制结束希沃进程，未保存的数据会丢失。",
            ActionKind.BlockSeewoNetwork => "为希沃程序创建入站和出站防火墙阻断规则，需要管理员权限。",
            ActionKind.UnblockSeewoNetwork => "移除希沃程序的防火墙阻断规则。",
            ActionKind.DisableSeewoStartup => "禁用希沃的开机自启项。只改名、不删，随时能还原。",
            ActionKind.EnableSeewoStartup => "恢复希沃的开机自启项。",
            ActionKind.Shutdown => "关闭计算机。会先显示倒计时提示，期间可以取消。需要管理员权限。",
            ActionKind.Restart => "重启计算机。会先显示倒计时提示，期间可以取消。需要管理员权限。",
            ActionKind.Logoff => "注销当前用户，未保存的工作会丢失。",
            ActionKind.Lock => "锁定工作站。不需要管理员权限，也不会关闭任何程序。",
            ActionKind.Notify => "在希沃助手的日志和状态栏里记录一条通知。",
            ActionKind.RunProgram => "启动一个程序。请确认路径正确，错误的路径只会在运行时失败。",
            _ => string.Empty,
        };
    }
}
