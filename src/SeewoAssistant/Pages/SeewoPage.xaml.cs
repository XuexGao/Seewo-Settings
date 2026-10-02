using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SeewoAssistant.Core.Models;

namespace SeewoAssistant.Pages;

/// <summary>
/// Module 4: discovering, controlling, and blocking network/auto-start for Seewo
/// software.
/// </summary>
public sealed partial class SeewoPage : ModulePageBase
{
    // The lists are observable collections of presentation wrappers. The Core models
    // stay free of any UI concern; these wrappers add only what the templates bind to.
    private readonly ObservableCollection<DiscoveredRuleItem> _discovered = [];
    private readonly ObservableCollection<RuleItem> _rules = [];
    private readonly ObservableCollection<ProcessItem> _processes = [];
    private readonly ObservableCollection<StartupItem> _startupEntries = [];


    public SeewoPage()
    {
        InitializeComponent();

        DiscoveredList.ItemsSource = _discovered;
        RulesList.ItemsSource = _rules;
        ProcessList.ItemsSource = _processes;
        StartupList.ItemsSource = _startupEntries;

        NewRuleKindCombo.Items.Add("进程名");
        NewRuleKindCombo.Items.Add("路径前缀");
        NewRuleKindCombo.Items.Add("数字签名");
        NewRuleKindCombo.SelectedIndex = 0;
    }

    protected override void OnServicesReady()
    {
        UpdatePermissionState();
        ReloadRules();
    }

    // ------------------------------------------------------------------ permissions

    private void UpdatePermissionState()
    {
        var isElevated = IsElevated();
        var hasDebug = Core.Services.Seewo.SeewoControlService.HasDebugPrivilege();

        PermissionText.Text = isElevated
            ? "已以管理员身份运行，所有功能可用。"
            : "未以管理员身份运行。挂起/结束进程、防火墙规则和修改计划任务会被拒绝。";

        PermissionBadgeText.Text = isElevated ? "已提权" : "未提权";
        SetBadge(PermissionBadge, PermissionBadgeText, PermissionBadgeText.Text, isElevated);

        if (isElevated)
        {
            PermissionInfoBar.IsOpen = false;
            return;
        }

        PermissionInfoBar.Severity = InfoBarSeverity.Warning;
        PermissionInfoBar.Title = "部分功能需要管理员权限";
        PermissionInfoBar.Message =
            "挂起或结束其他用户的进程、创建防火墙规则、修改计划任务和服务启动类型都需要管理员权限。" +
            (hasDebug
                ? " 当前已能获取调试特权。"
                : " 当前无法获取 SeDebugPrivilege。") +
            " 扫描和查看功能不受影响。请右键以管理员身份运行本程序以启用全部功能。";
        PermissionInfoBar.IsOpen = true;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ discovery

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        ScanProgress.IsActive = true;
        ScanProgressText.Text = "正在扫描…（要遍历几个目录树，大约十秒）";
        ScanProgressText.Visibility = Visibility.Visible;
        ScanButton.IsEnabled = false;
        _discovered.Clear();

        try
        {
            // The scan walks several directory trees, so it belongs off the UI thread.
            var found = await Task.Run(() => Services.SeewoControl.DiscoverSeewoSoftware());

            foreach (var rule in found)
            {
                _discovered.Add(new DiscoveredRuleItem(rule));
            }

            DiscoveredList.Visibility = _discovered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AddSelectedButton.IsEnabled = _discovered.Count > 0;
            SelectAllButton.IsEnabled = _discovered.Count > 0;

            ScanSummaryText.Text = _discovered.Count == 0
                ? "没有在本机找到希沃软件。如果确实已安装，可以手动添加规则。"
                : $"找到 {_discovered.Count} 个可能的希沃程序。勾选需要的，然后点「把勾选项保存为规则」。";
            ScanSummaryText.Visibility = Visibility.Visible;

            Report($"扫描完成，找到 {_discovered.Count} 个希沃程序。", StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Scanning for Seewo software failed.", ex);
            Report($"扫描失败：{ex.Message}", StatusSeverity.Error);
        }
        finally
        {
            ScanProgress.IsActive = false;
            ScanProgressText.Visibility = Visibility.Collapsed;
            ScanButton.IsEnabled = true;
        }
    }

    private void OnSelectAllDiscovered(object sender, RoutedEventArgs e)
    {
        // Toggle: if everything is already selected, clear; otherwise select all.
        var allSelected = _discovered.All(d => d.IsSelected);

        foreach (var item in _discovered)
        {
            item.IsSelected = !allSelected;
        }
    }

    private void OnAddSelectedRules(object sender, RoutedEventArgs e)
    {
        var selected = _discovered.Where(d => d.IsSelected).ToList();

        if (selected.Count == 0)
        {
            Report("请先勾选至少一个程序。", StatusSeverity.Warning);
            return;
        }

        var added = 0;

        foreach (var item in selected)
        {
            // Skip anything already present so repeated scans do not duplicate rules.
            var exists = Services.Settings.SeewoRules.Any(r =>
                r.MatchKind == item.Rule.MatchKind &&
                r.Pattern.Equals(item.Rule.Pattern, StringComparison.OrdinalIgnoreCase));

            if (exists)
            {
                continue;
            }

            Services.Settings.SeewoRules.Add(item.Rule);
            added++;
        }

        Services.SaveSettings();
        ReloadRules();

        Report(
            added > 0 ? $"已添加 {added} 条规则（跳过 {selected.Count - added} 条重复项）。" : "所选规则都已存在。",
            added > 0 ? StatusSeverity.Success : StatusSeverity.Informational);
    }

    // ------------------------------------------------------------------ rules

    private void ReloadRules()
    {
        _rules.Clear();

        foreach (var rule in Services.Settings.SeewoRules)
        {
            _rules.Add(new RuleItem(rule));
        }

        NoRulesText.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRuleToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: RuleItem item } toggle)
        {
            return;
        }

        // Same hazard as the startup list, and the same defence: read the switch itself,
        // and ignore an event that agrees with what the rule already says. A refresh that
        // re-presents an unchanged rule raises Toggled without the user having done
        // anything.
        var target = toggle.IsOn;

        if (target == item.Rule.Enabled)
        {
            item.Enabled = target;
            return;
        }

        item.Enabled = target;
        item.Rule.Enabled = target;
        Services.SaveSettings();
        Report($"规则「{item.Rule.Name}」已{(target ? "启用" : "停用")}。");
    }

    private void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RuleItem item })
        {
            return;
        }

        Services.Settings.SeewoRules.Remove(item.Rule);
        Services.SaveSettings();
        ReloadRules();

        Report($"已删除规则「{item.Rule.Name}」。");
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var name = NewRuleNameBox.Text?.Trim();
        var pattern = NewRulePatternBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(pattern))
        {
            Report("请填写规则名称和匹配内容。", StatusSeverity.Warning);
            return;
        }

        var kind = NewRuleKindCombo.SelectedIndex switch
        {
            1 => ProcessMatchKind.PathPrefix,
            2 => ProcessMatchKind.Signer,
            _ => ProcessMatchKind.ProcessName,
        };

        // A path-prefix rule is far more useful when it covers a directory, so
        // normalise a bare directory to end with a separator.
        if (kind == ProcessMatchKind.PathPrefix &&
            !pattern.EndsWith(Path.DirectorySeparatorChar) &&
            !pattern.EndsWith('\\') &&
            !pattern.Contains('.', StringComparison.Ordinal))
        {
            pattern += Path.DirectorySeparatorChar;
        }

        Services.Settings.SeewoRules.Add(new ProcessRule
        {
            Name = name,
            MatchKind = kind,
            Pattern = pattern,
            Enabled = true,
            AutoApply = NewRuleAutoApplyToggle.IsOn,
            Notes = "手动添加",
        });

        Services.SaveSettings();
        ReloadRules();

        NewRuleNameBox.Text = string.Empty;
        NewRulePatternBox.Text = string.Empty;

        Report($"已添加规则「{name}」。", StatusSeverity.Success);
    }

    // ------------------------------------------------------------------ processes

    private async void OnRefreshProcesses(object sender, RoutedEventArgs e)
    {
        ProcessProgress.IsActive = true;
        RefreshProcessesButton.IsEnabled = false;

        try
        {
            var rules = Services.Settings.SeewoRules.ToList();

            if (rules.Count == 0)
            {
                Report("还没有规则。请先扫描或添加规则。", StatusSeverity.Warning);
                return;
            }

            // Enumerating processes and reading their modules and signatures is slow.
            var matches = await Task.Run(() => Services.SeewoControl.FindAllMatches(rules));

            _processes.Clear();

            foreach (var match in matches)
            {
                var blocked = string.IsNullOrWhiteSpace(match.Path)
                    ? false
                    : await Task.Run(() => Services.Firewall.IsBlocked(match.Path));

                _processes.Add(new ProcessItem(match, blocked));
            }

            NoProcessesText.Visibility = _processes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoProcessesText.Text = _processes.Count == 0
                ? "当前没有正在运行的希沃进程。"
                : string.Empty;

            Report($"找到 {_processes.Count} 个正在运行的希沃进程。", StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Enumerating matching processes failed.", ex);
            Report($"刷新失败：{ex.Message}", StatusSeverity.Error);
        }
        finally
        {
            ProcessProgress.IsActive = false;
            RefreshProcessesButton.IsEnabled = true;
            UpdateProcessButtons();
        }
    }

    private void OnProcessSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProcessButtons();

    private void UpdateProcessButtons()
    {
        var count = ProcessList.SelectedItems.Count;
        var hasSelection = count > 0;

        SuspendSelectedButton.IsEnabled = hasSelection;
        ResumeSelectedButton.IsEnabled = hasSelection;
        KillSelectedButton.IsEnabled = hasSelection;
    }

    private List<ProcessItem> SelectedProcesses() => ProcessList.SelectedItems.OfType<ProcessItem>().ToList();

    private async void OnSuspendSelected(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProcesses();
        if (selected.Count == 0)
        {
            return;
        }

        if (!await ConfirmAsync(
                "挂起选中进程",
                $"将挂起 {selected.Count} 个进程：\n{string.Join("\n", selected.Select(p => $"· {p.ProcessName} (PID {p.ProcessId})"))}\n\n" +
                "被挂起的进程会完全冻结（不响应、不刷新画面），但不会丢失数据。随时可以恢复。\n" +
                "退出本程序时会自动恢复所有被挂起的进程。",
                "挂起"))
        {
            return;
        }

        await ApplyToProcessesAsync(selected, p => Services.SeewoControl.SetSuspended(p.ProcessId, true), "挂起");
    }

    private async void OnResumeSelected(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProcesses();
        if (selected.Count == 0)
        {
            return;
        }

        await ApplyToProcessesAsync(selected, p => Services.SeewoControl.SetSuspended(p.ProcessId, false), "恢复");
    }

    private async void OnKillSelected(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProcesses();
        if (selected.Count == 0)
        {
            return;
        }

        if (!await ConfirmAsync(
                "结束选中进程",
                $"将强制结束 {selected.Count} 个进程：\n{string.Join("\n", selected.Select(p => $"· {p.ProcessName} (PID {p.ProcessId})"))}\n\n" +
                "未保存的数据会丢失。这个操作不可撤销。",
                "结束进程"))
        {
            return;
        }

        await ApplyToProcessesAsync(selected, p => Services.SeewoControl.Terminate(p.ProcessId), "结束");
    }

    private async void OnSuspendAll(object sender, RoutedEventArgs e) => await BulkAsync(suspend: true);

    private async void OnResumeAll(object sender, RoutedEventArgs e) => await BulkAsync(suspend: false);

    private async Task BulkAsync(bool suspend)
    {
        var rules = Services.Settings.SeewoRules.ToList();

        if (rules.Count == 0)
        {
            Report("还没有规则。请先扫描或添加规则。", StatusSeverity.Warning);
            return;
        }

        // Suspending or resuming every match is disruptive enough to warrant an
        // explicit confirmation that names what will happen.
        var action = suspend ? "挂起" : "恢复";

        if (!await ConfirmAsync(
                $"全部{action}",
                $"将对所有匹配规则的希沃进程执行「{action}」。\n\n" +
                (suspend
                    ? "被挂起的进程会完全冻结，退出本程序时会自动恢复。"
                    : "恢复所有被挂起的希沃进程。"),
                action))
        {
            return;
        }

        ProcessProgress.IsActive = true;

        try
        {
            var result = await Task.Run(() =>
            {
                var messages = new List<string>();
                var failed = false;

                foreach (var rule in rules.Where(r => r.Enabled))
                {
                    var outcome = Services.SeewoControl.SetSuspendedForRule(rule, suspend);
                    messages.Add(outcome.Message);
                    failed |= !outcome.Success;
                }

                return (Messages: messages, Failed: failed);
            });

            Report(string.Join(" ", result.Messages),
                result.Failed ? StatusSeverity.Warning : StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            Services.Logger.Error($"Bulk {action} failed.", ex);
            Report($"{action}失败：{ex.Message}", StatusSeverity.Error);
        }
        finally
        {
            ProcessProgress.IsActive = false;
            OnRefreshProcesses(sender: this, e: new RoutedEventArgs());
        }
    }

    private async void OnKillAll(object sender, RoutedEventArgs e)
    {
        var rules = Services.Settings.SeewoRules.ToList();

        if (rules.Count == 0)
        {
            Report("还没有规则。请先扫描或添加规则。", StatusSeverity.Warning);
            return;
        }

        if (!await ConfirmAsync(
                "全部结束",
                "将强制结束所有匹配规则的希沃进程。未保存的数据会丢失，这个操作不可撤销。\n\n" +
                "如果只是想临时停用希沃软件，建议改用「全部挂起」，那是可逆的。",
                "全部结束"))
        {
            return;
        }

        ProcessProgress.IsActive = true;

        try
        {
            var result = await Task.Run(() =>
            {
                var messages = new List<string>();
                var failed = false;

                foreach (var rule in rules.Where(r => r.Enabled))
                {
                    var outcome = Services.SeewoControl.TerminateForRule(rule);
                    messages.Add(outcome.Message);
                    failed |= !outcome.Success;
                }

                return (Messages: messages, Failed: failed);
            });

            Report(string.Join(" ", result.Messages),
                result.Failed ? StatusSeverity.Warning : StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Bulk terminate failed.", ex);
            Report($"结束进程失败：{ex.Message}", StatusSeverity.Error);
        }
        finally
        {
            ProcessProgress.IsActive = false;
            OnRefreshProcesses(sender: this, e: new RoutedEventArgs());
        }
    }

    private async Task ApplyToProcessesAsync(
        List<ProcessItem> items,
        Func<ProcessItem, ActionResult> operation,
        string verb)
    {
        ProcessProgress.IsActive = true;

        try
        {
            var result = await Task.Run(() =>
            {
                var messages = new List<string>();
                var failed = false;

                foreach (var item in items)
                {
                    var outcome = operation(item);
                    messages.Add(outcome.Message);
                    failed |= !outcome.Success;
                }

                return (Messages: messages, Failed: failed);
            });

            Report(string.Join(" ", result.Messages),
                result.Failed ? StatusSeverity.Warning : StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            Services.Logger.Error($"{verb} failed.", ex);
            Report($"{verb}失败：{ex.Message}", StatusSeverity.Error);
        }
        finally
        {
            ProcessProgress.IsActive = false;
            OnRefreshProcesses(sender: this, e: new RoutedEventArgs());
        }
    }

    // ------------------------------------------------------------------ firewall

    private async void OnBlockNetwork(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProcessItem item })
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Process.Path))
        {
            // Logged as well as reported: this path used to return silently, so an
            // attempt to block a process whose path could not be read left no trace.
            Services.Logger.Warn(
                $"Cannot block network access for '{item.Process.ProcessName}' (PID {item.Process.ProcessId}): " +
                "its executable path could not be read.");

            Report($"无法获取「{item.Process.ProcessName}」的完整路径，无法创建防火墙规则。", StatusSeverity.Warning);
            return;
        }

        Services.Logger.Info(
            $"Blocking network access for '{item.Process.ProcessName}' at '{item.Process.Path}'.");

        await RunGuardedAsync($"禁止「{item.Process.ProcessName}」联网", async () =>
        {
            var result = await Task.Run(() => Services.Firewall.Block(item.Process.Path));

            item.IsNetworkBlocked = result.Success;
            Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Error);
        });
    }

    private async void OnUnblockNetwork(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProcessItem item })
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Process.Path))
        {
            return;
        }

        await RunGuardedAsync($"恢复「{item.Process.ProcessName}」联网", async () =>
        {
            var result = await Task.Run(() => Services.Firewall.Unblock(item.Process.Path));

            item.IsNetworkBlocked = !result.Success;
            Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Error);
        });
    }

    private async void OnRemoveAllFirewallRules(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "移除所有防火墙规则",
                "这只会删掉我们建的那几条 SeewoAssistant Block 规则，被禁止联网的希沃程序都会恢复联网。\n\n" +
                "你或者其他软件建的规则，一律不动。",
                "移除全部"))
        {
            return;
        }

        await RunGuardedAsync("移除防火墙规则", async () =>
        {
            var result = await Task.Run(() => Services.Firewall.RemoveAllRules());
            Report(result.Message, result.Success ? StatusSeverity.Success : StatusSeverity.Error);

            // Reflect the change in the list rather than leaving stale badges.
            if (result.Success)
            {
                foreach (var item in _processes)
                {
                    item.IsNetworkBlocked = false;
                }
            }
        });
    }

    // ------------------------------------------------------------------ startup

    private async void OnScanStartup(object sender, RoutedEventArgs e)
    {
        StartupProgress.IsActive = true;
        ScanStartupButton.IsEnabled = false;

        try
        {
            var entries = await Task.Run(() => Services.StartupManager.FindSeewoStartupEntries());

            _startupEntries.Clear();

            foreach (var entry in entries)
            {
                _startupEntries.Add(new StartupItem(entry));
            }

            NoStartupText.Visibility = _startupEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoStartupText.Text = _startupEntries.Count == 0
                ? "没有发现希沃相关的开机自启项。"
                : string.Empty;

            Report($"找到 {_startupEntries.Count} 个希沃自启项。", StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Scanning startup entries failed.", ex);
            Report($"扫描失败：{ex.Message}", StatusSeverity.Error);
        }
        finally
        {
            StartupProgress.IsActive = false;
            ScanStartupButton.IsEnabled = true;
        }
    }

    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: StartupItem item } toggle)
        {
            return;
        }

        // The switch's own value is read rather than the bound property, because that
        // value is already correct whenever this event fires - whereas whether the binding
        // has written back to the item by now depends on framework internals. Reading it
        // here makes the decision below independent of that ordering.
        var target = toggle.IsOn;

        // The event that caused the reported damage is the one raised while the row
        // container is being created: the binding sets IsOn from the entry, which raises
        // Toggled, and the handler then applied that "change" back to the entry. That is
        // why merely pressing 扫描开机自启项 silently re-enabled every disabled startup
        // entry on the machine.
        //
        // The stored entry is only ever changed by the apply step below, so a toggle that
        // matches it is the binding catching up rather than a user action.
        if (target == item.Entry.Enabled)
        {
            item.Enabled = target;
            return;
        }

        item.Enabled = target;

        await RunGuardedAsync(
            $"{(target ? "启用" : "禁用")}启动项「{item.Entry.Name}」",
            async () =>
            {
                var result = await Task.Run(() => Services.StartupManager.SetEnabled(item.Entry, target));

                if (result.Success)
                {
                    Report(result.Message, StatusSeverity.Success);
                }
                else
                {
                    // The operation failed, so the toggle must be put back to the
                    // real state rather than left showing an intention that did not
                    // take effect.
                    // Put the switch back to the real state. The entry was never
                    // changed, so the resulting Toggled event matches it and is ignored.
                    toggle.IsOn = item.Entry.Enabled;
                    item.Enabled = item.Entry.Enabled;

                    Report(result.Message, StatusSeverity.Error);
                }
            });
    }

    private async void OnDisableAllStartup(object sender, RoutedEventArgs e) =>
        await SetAllStartupAsync(enabled: false);

    private async void OnEnableAllStartup(object sender, RoutedEventArgs e) =>
        await SetAllStartupAsync(enabled: true);

    private async Task SetAllStartupAsync(bool enabled)
    {
        if (_startupEntries.Count == 0)
        {
            Report("请先扫描开机自启项。", StatusSeverity.Warning);
            return;
        }

        var action = enabled ? "恢复" : "禁用";
        var pending = _startupEntries.Where(i => i.Entry.CanToggle && i.Enabled != enabled).ToList();

        if (pending.Count == 0)
        {
            Report($"所有自启项都已处于目标状态。");
            return;
        }

        if (!await ConfirmAsync(
                $"全部{action}",
                $"将对 {pending.Count} 个希沃自启项执行「{action}」。\n\n" +
                (enabled
                    ? "这会恢复它们随系统启动。"
                    : "注册表项会被改名保留（加 .seewoassistant-disabled 后缀），计划任务会被禁用，随时可以完整还原。"),
                action))
        {
            return;
        }

        StartupProgress.IsActive = true;

        try
        {
            var result = await Task.Run(() =>
            {
                var messages = new List<string>();
                var failed = false;

                foreach (var item in pending)
                {
                    var outcome = Services.StartupManager.SetEnabled(item.Entry, enabled);
                    messages.Add(outcome.Message);
                    failed |= !outcome.Success;
                }

                return (Messages: messages, Failed: failed);
            });

            // Refresh so the toggles show the real post-operation state.
            OnScanStartup(sender: this, e: new RoutedEventArgs());

            Report(string.Join(" ", result.Messages),
                result.Failed ? StatusSeverity.Warning : StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            Services.Logger.Error($"Bulk startup {action} failed.", ex);
            Report($"{action}失败：{ex.Message}", StatusSeverity.Error);
        }
        finally
        {
            StartupProgress.IsActive = false;
        }
    }

    // ------------------------------------------------------------------ wrappers

    /// <summary>Presentation wrapper for a discovered rule, adding a selection flag.</summary>
    private sealed class DiscoveredRuleItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;

        internal DiscoveredRuleItem(ProcessRule rule)
        {
            Rule = rule;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal ProcessRule Rule { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public string Name => Rule.Name;

        public string Pattern => Rule.Pattern;

        public string Notes => Rule.Notes;

        public string ConfidenceLabel => Rule.Notes.StartsWith("已识别", StringComparison.Ordinal) ? "已识别" : "可能";

        /// <summary>
        /// Identifies this row to assistive technology.
        /// </summary>
        /// <remarks>
        /// The list's ListViewItem binds its automation name to the item, and without
        /// this override that name is the type name - useless to a screen reader and to
        /// UI Automation.
        /// </remarks>
        public override string ToString() => $"{Name} （{ConfidenceLabel}）：{Pattern}";
    }

    /// <summary>Presentation wrapper for a configured rule.</summary>
    private sealed class RuleItem
    {
        internal RuleItem(ProcessRule rule)
        {
            Rule = rule;
            Enabled = rule.Enabled;
        }

        internal ProcessRule Rule { get; }

        public string Name => Rule.Name;

        public string Pattern => Rule.Pattern;

        public string Notes => Rule.Notes;

        /// <summary>
        /// The state shown in the list, which is written back to the rule only once the
        /// change has been accepted.
        /// </summary>
        /// <remarks>
        /// Not a pass-through to <see cref="ProcessRule.Enabled"/>: the two-way binding
        /// writes this while the row container is being created, and if that write reached
        /// the rule directly there would be no way to tell it apart from a click. See
        /// <c>OnRuleToggled</c>.
        /// </remarks>
        public bool Enabled { get; set; }

        public string MatchKindLabel => Rule.MatchKind switch
        {
            ProcessMatchKind.PathPrefix => "路径前缀",
            ProcessMatchKind.Signer => "数字签名",
            _ => "进程名",
        };
    }

    /// <summary>Presentation wrapper for a running matched process.</summary>
    private sealed class ProcessItem : INotifyPropertyChanged
    {
        private bool _isNetworkBlocked;

        internal ProcessItem(MatchedProcess process, bool isNetworkBlocked)
        {
            Process = process;
            _isNetworkBlocked = isNetworkBlocked;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal MatchedProcess Process { get; }

        public int ProcessId => Process.ProcessId;

        public string ProcessName => Process.ProcessName;

        public string Path => Process.Path;

        public string Description => Process.Description;

        public bool IsNetworkBlocked
        {
            get => _isNetworkBlocked;
            set
            {
                if (_isNetworkBlocked == value)
                {
                    return;
                }

                _isNetworkBlocked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNetworkBlocked)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateLabel)));
            }
        }

        public string StateLabel => IsNetworkBlocked ? "已断网" : "运行中";

        /// <inheritdoc cref="DiscoveredRuleItem.ToString"/>
        public override string ToString() =>
            $"{ProcessName}（PID {ProcessId}）{StateLabel}";
    }

    /// <summary>Presentation wrapper for a startup entry.</summary>
    private sealed class StartupItem
    {
        internal StartupItem(StartupEntry entry)
        {
            Entry = entry;
            Enabled = entry.Enabled;
        }

        internal StartupEntry Entry { get; }

        public string Name => Entry.Name;

        public string Location => Entry.Location;

        public string Command => Entry.Command;

        public string DisabledReason => Entry.DisabledReason;

        public bool CanToggle => Entry.CanToggle;

        /// <summary>
        /// The state shown in the list, which is only written back to the entry once the
        /// change has actually been applied.
        /// </summary>
        /// <remarks>
        /// Deliberately not a pass-through to <see cref="StartupEntry.Enabled"/>. The
        /// two-way binding writes this property while the row container is being created,
        /// and the setter is what raises <c>Toggled</c>. If the setter wrote straight
        /// through, the stored state would already equal the shown state by the time the
        /// handler ran, and there would be no way to tell that write apart from a real
        /// click. Keeping the two separate is what makes the comparison in
        /// <c>OnStartupToggled</c> meaningful.
        /// </remarks>
        public bool Enabled { get; set; }

        /// <inheritdoc cref="DiscoveredRuleItem.ToString"/>
        public override string ToString() => $"{KindLabel} {Name}";

        public string KindLabel => Entry.Kind switch
        {
            StartupEntryKind.ScheduledTask => "计划任务",
            StartupEntryKind.RegistryRun => "注册表启动项",
            StartupEntryKind.StartupFolder => "启动文件夹",
            StartupEntryKind.Service => "系统服务",
            _ => "未知",
        };
    }
}
