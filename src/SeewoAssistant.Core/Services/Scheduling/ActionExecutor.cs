using System.Diagnostics;
using System.Runtime.Versioning;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Models;
using SeewoAssistant.Core.Services.CaptureGuard;
using SeewoAssistant.Core.Services.Power;
using SeewoAssistant.Core.Services.Privacy;
using SeewoAssistant.Core.Services.Seewo;
using SeewoAssistant.Core.Services.VirtualCamera;

namespace SeewoAssistant.Core.Services.Scheduling;

/// <summary>
/// Executes a single <see cref="ScheduledAction"/> by dispatching to the service
/// that owns the capability.
/// </summary>
/// <remarks>
/// This is the single place where the five modules meet the scheduler. Keeping the
/// dispatch in one switch means a new capability only needs a new
/// <see cref="ActionKind"/> plus a case here, and the scheduler itself never has to
/// know what the actions mean.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ActionExecutor
{
    private readonly IAppLogger _logger;
    private readonly VirtualCameraService _virtualCamera;
    private readonly PrivacyMonitorService _privacyMonitor;
    private readonly CaptureGuardService _captureGuard;
    private readonly SeewoControlService _seewo;
    private readonly FirewallService _firewall;
    private readonly StartupManagerService _startup;
    private readonly PowerService _power;

    public ActionExecutor(
        VirtualCameraService virtualCamera,
        PrivacyMonitorService privacyMonitor,
        CaptureGuardService captureGuard,
        SeewoControlService seewo,
        FirewallService firewall,
        StartupManagerService startup,
        PowerService power,
        IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _virtualCamera = virtualCamera;
        _privacyMonitor = privacyMonitor;
        _captureGuard = captureGuard;
        _seewo = seewo;
        _firewall = firewall;
        _startup = startup;
        _power = power;
    }

    /// <summary>Runs one action and reports the outcome.</summary>
    public async Task<ActionResult> ExecuteAsync(ScheduledAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            _logger.Info($"Executing scheduled action: {action}");

            return action.Kind switch
            {
                // ---- module 1: virtual camera ----
                ActionKind.StartVirtualCamera => await StartVirtualCameraAsync(action, cancellationToken).ConfigureAwait(false),
                ActionKind.StopVirtualCamera => StopVirtualCamera(),
                ActionKind.PushVirtualCameraColor => PushCameraColor(action),
                ActionKind.PushVirtualCameraImage => PushCameraImage(action),
                ActionKind.PushVirtualCameraTestPattern => PushCameraTestPattern(),

                // ---- module 2: privacy monitor ----
                ActionKind.StartPrivacyMonitor => StartPrivacyMonitor(),
                ActionKind.StopPrivacyMonitor => StopPrivacyMonitor(),

                // ---- module 3: capture guard ----
                ActionKind.ProtectWindow => ProtectWindow(action),
                ActionKind.UnprotectWindow => UnprotectWindow(action),

                // ---- module 4: seewo ----
                ActionKind.SuspendSeewo => ApplyToSeewoRules(action, r => _seewo.SetSuspendedForRule(r, suspend: true)),
                ActionKind.ResumeSeewo => ApplyToSeewoRules(action, r => _seewo.SetSuspendedForRule(r, suspend: false)),
                ActionKind.KillSeewo => ApplyToSeewoRules(action, r => _seewo.TerminateForRule(r)),
                ActionKind.BlockSeewoNetwork => ApplyToSeewoPaths(action, path => _firewall.Block(path)),
                ActionKind.UnblockSeewoNetwork => ApplyToSeewoPaths(action, path => _firewall.Unblock(path)),
                ActionKind.DisableSeewoStartup => ApplyToStartupEntries(action, enabled: false),
                ActionKind.EnableSeewoStartup => ApplyToStartupEntries(action, enabled: true),

                // ---- module 5: power ----
                ActionKind.Shutdown => _power.Initiate(PowerAction.Shutdown, GetInt(action, "timeoutSeconds", 60), Get(action, "message")),
                ActionKind.Restart => _power.Initiate(PowerAction.Restart, GetInt(action, "timeoutSeconds", 60), Get(action, "message")),
                ActionKind.Logoff => _power.Initiate(PowerAction.Logoff),
                ActionKind.Lock => _power.Initiate(PowerAction.Lock),

                // ---- generic ----
                ActionKind.Notify => ActionResult.Ok(Get(action, "message") ?? "（空通知）"),
                ActionKind.RunProgram => RunProgram(action),

                _ => ActionResult.Fail($"未知的操作类型：{action.Kind}"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Action {action.Kind} threw.", ex);
            return ActionResult.Fail($"执行「{action.Kind}」时发生异常：{ex.Message}", ex);
        }
    }

    // ------------------------------------------------------------------ module 1

    private async Task<ActionResult> StartVirtualCameraAsync(ScheduledAction action, CancellationToken cancellationToken)
    {
        var capability = _virtualCamera.DetectCapability();
        if (!capability.IsSupported)
        {
            return ActionResult.Fail($"当前系统不支持虚拟摄像头：{capability.Summary}");
        }

        // Pushing a colour or image is the usual way to "start" the camera with
        // content; with no parameter the animated test pattern is used.
        var imagePath = Get(action, "path");
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            return _virtualCamera.PushImage(imagePath);
        }

        var color = Get(action, "color");
        if (!string.IsNullOrWhiteSpace(color))
        {
            _virtualCamera.PushSolidColor(color);
            return ActionResult.Ok($"已推送纯色画面 {color}。");
        }

        _virtualCamera.StartTestPatternPump();
        await Task.CompletedTask.ConfigureAwait(false);
        return ActionResult.Ok("已启动虚拟摄像头并推送测试画面。");
    }

    private ActionResult StopVirtualCamera()
    {
        _virtualCamera.StopPump();
        return ActionResult.Ok("已停止虚拟摄像头推流。");
    }

    private ActionResult PushCameraColor(ScheduledAction action)
    {
        var color = Get(action, "color") ?? "#000000";
        _virtualCamera.PushSolidColor(color);
        return ActionResult.Ok($"已推送纯色画面 {color}。");
    }

    private ActionResult PushCameraImage(ScheduledAction action)
    {
        var path = Get(action, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return ActionResult.Fail("推送图片需要指定 path 参数。");
        }

        return _virtualCamera.PushImage(path);
    }

    private ActionResult PushCameraTestPattern()
    {
        _virtualCamera.StartTestPatternPump();
        return ActionResult.Ok("已开始推送动态测试画面。");
    }

    // ------------------------------------------------------------------ module 2

    private ActionResult StartPrivacyMonitor()
    {
        _privacyMonitor.Start();
        return ActionResult.Ok("已开启摄像头/麦克风调用监控。");
    }

    private ActionResult StopPrivacyMonitor()
    {
        _privacyMonitor.Stop();
        return ActionResult.Ok("已停止摄像头/麦克风调用监控。");
    }

    // ------------------------------------------------------------------ module 3

    private ActionResult ProtectWindow(ScheduledAction action)
    {
        var target = ResolveWindowTarget(action, out var error);
        if (target is null)
        {
            return ActionResult.Fail(error ?? "没有找到匹配的窗口。");
        }

        var mode = (Get(action, "mode") ?? "exclude").ToLowerInvariant() switch
        {
            "blackout" or "monitor" => CaptureProtectionMode.Blackout,
            _ => CaptureProtectionMode.ExcludeFromCapture,
        };

        var allowCrossProcess = GetBool(action, "crossProcess", false);

        var result = _captureGuard.Protect(target, mode, allowCrossProcess);
        return result.Success ? ActionResult.Ok(result.Message) : ActionResult.Fail(result.Message, result.Exception);
    }

    private ActionResult UnprotectWindow(ScheduledAction action)
    {
        var target = ResolveWindowTarget(action, out var error);
        if (target is null)
        {
            return ActionResult.Fail(error ?? "没有找到匹配的窗口。");
        }

        var allowCrossProcess = GetBool(action, "crossProcess", false);
        var result = _captureGuard.Unprotect(target, allowCrossProcess);

        return result.Success ? ActionResult.Ok(result.Message) : ActionResult.Fail(result.Message, result.Exception);
    }

    /// <summary>
    /// Resolves the window a protection action should act on. Uses the same target
    /// selector shape as the UI so a scheduled action and a manual click behave
    /// identically.
    /// </summary>
    private WindowInfo? ResolveWindowTarget(ScheduledAction action, out string? error)
    {
        error = null;

        var handleText = Get(action, "hwnd");
        if (!string.IsNullOrWhiteSpace(handleText))
        {
            // Accept both decimal and 0x-prefixed hexadecimal.
            var handle = handleText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.TryParse(handleText[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex) ? hex : 0
                : long.TryParse(handleText, out var dec) ? dec : 0;

            if (handle == 0)
            {
                error = $"窗口句柄「{handleText}」无法解析。";
                return null;
            }

            var described = _captureGuard.Describe((nint)handle);
            if (described is null)
            {
                error = $"句柄 {handleText} 对应的窗口不存在或已关闭。";
            }

            return described;
        }

        var kind = (Get(action, "targetKind") ?? "processName").ToLowerInvariant();
        var pattern = Get(action, "pattern");

        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "需要指定 pattern（匹配内容）或 hwnd（窗口句柄）。";
            return null;
        }

        var windows = _captureGuard.EnumerateWindows();

        var match = kind switch
        {
            "classname" => windows.FirstOrDefault(w => w.ClassName.Equals(pattern, StringComparison.OrdinalIgnoreCase)),
            "title" or "titlecontains" => windows.FirstOrDefault(w => w.Title.Contains(pattern, StringComparison.OrdinalIgnoreCase)),
            _ => windows.FirstOrDefault(w => w.ProcessName.Equals(pattern, StringComparison.OrdinalIgnoreCase)),
        };

        if (match is null)
        {
            error = $"没有找到匹配「{pattern}」的窗口。请确认该程序正在运行。";
        }

        return match;
    }

    // ------------------------------------------------------------------ module 4

    private ActionResult ApplyToSeewoRules(ScheduledAction action, Func<ProcessRule, ActionResult> operation)
    {
        var rules = ResolveSeewoRules(action, out var error);
        if (rules is null)
        {
            return ActionResult.Fail(error ?? "没有找到匹配的规则。");
        }

        var messages = new List<string>();
        var anyFailed = false;

        foreach (var rule in rules)
        {
            var result = operation(rule);
            messages.Add(result.Message);
            anyFailed |= !result.Success;
        }

        var combined = string.Join(" ", messages);
        return anyFailed ? ActionResult.Fail(combined) : ActionResult.Ok(combined);
    }

    private ActionResult ApplyToSeewoPaths(ScheduledAction action, Func<string, ActionResult> operation)
    {
        var rules = ResolveSeewoRules(action, out var error);
        if (rules is null)
        {
            return ActionResult.Fail(error ?? "没有找到匹配的规则。");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();
        var anyFailed = false;

        foreach (var rule in rules)
        {
            foreach (var match in _seewo.FindMatchingProcesses(rule))
            {
                if (string.IsNullOrWhiteSpace(match.Path))
                {
                    continue;
                }

                // The firewall rule is per executable path, so only distinct paths
                // need to be processed.
                if (!paths.Add(match.Path))
                {
                    continue;
                }

                var result = operation(match.Path);
                messages.Add(result.Message);
                anyFailed |= !result.Success;
            }
        }

        if (messages.Count == 0)
        {
            return ActionResult.Ok("没有正在运行的希沃程序，无需修改防火墙规则。");
        }

        var combined = string.Join(" ", messages);
        return anyFailed ? ActionResult.Fail(combined) : ActionResult.Ok(combined);
    }

    private ActionResult ApplyToStartupEntries(ScheduledAction action, bool enabled)
    {
        var entries = _startup.FindSeewoStartupEntries();

        if (entries.Count == 0)
        {
            return ActionResult.Ok("没有发现希沃相关的开机自启项。");
        }

        // Only touch entries that are not already in the target state, so a repeated
        // schedule does not churn the registry.
        var pending = entries.Where(e => e.Enabled != enabled && e.CanToggle).ToList();

        if (pending.Count == 0)
        {
            return ActionResult.Ok($"所有 {entries.Count} 个自启项都已处于目标状态。");
        }

        var messages = new List<string>();
        var anyFailed = false;

        foreach (var entry in pending)
        {
            var result = _startup.SetEnabled(entry, enabled);
            messages.Add(result.Message);
            anyFailed |= !result.Success;
        }

        var combined = string.Join(" ", messages);
        return anyFailed ? ActionResult.Fail(combined) : ActionResult.Ok(combined);
    }

    /// <summary>
    /// Resolves which Seewo rules an action applies to: either the explicitly named
    /// rules, or every enabled rule when none are named.
    /// </summary>
    private List<ProcessRule>? ResolveSeewoRules(ScheduledAction action, out string? error)
    {
        error = null;

        var allRules = _seewo.DiscoverSeewoSoftware();

        var names = Get(action, "rules");
        if (string.IsNullOrWhiteSpace(names))
        {
            // No rule named: apply to everything discovered, which is what a
            // "suspend all Seewo software" schedule means.
            return allRules.ToList();
        }

        var wanted = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = allRules
            .Where(r => wanted.Any(w =>
                r.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                r.Pattern.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (selected.Count == 0)
        {
            error = $"没有找到匹配「{names}」的希沃程序。请先在「希沃软件」页扫描本机已安装的希沃组件。";
            return null;
        }

        return selected;
    }

    // ------------------------------------------------------------------ generic

    private ActionResult RunProgram(ScheduledAction action)
    {
        var path = Get(action, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return ActionResult.Fail("运行程序需要指定 path 参数。");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                Arguments = Get(action, "arguments") ?? string.Empty,
                UseShellExecute = true,
            };

            Process.Start(startInfo);
            return ActionResult.Ok($"已启动 {path}。");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ActionResult.Fail($"启动 {path} 失败：{ex.Message}", ex);
        }
    }

    private static string? Get(ScheduledAction action, string key) => action.Get(key);

    private static int GetInt(ScheduledAction action, string key, int fallback) =>
        int.TryParse(action.Get(key), out var value) ? value : fallback;

    private static bool GetBool(ScheduledAction action, string key, bool fallback) =>
        bool.TryParse(action.Get(key), out var value) ? value : fallback;
}
