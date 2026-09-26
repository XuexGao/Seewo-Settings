using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SeewoAssistant.Core.Abstractions;

namespace SeewoAssistant.Core.Services.Seewo;

/// <summary>
/// Creates and removes per-program Windows Firewall block rules.
/// </summary>
/// <remarks>
/// <para>
/// The Windows Firewall COM API (<c>INetFwPolicy2</c>) is used rather than
/// <c>netsh advfirewall</c> because it gives structured error reporting and does not
/// require parsing localized console output, which breaks on non-English systems.
/// </para>
/// <para>
/// Rules are created in both directions. Blocking only outbound traffic would still
/// leave the program able to receive connections and to be reached on the local
/// network, which is not what "禁止联网" means to a user.
/// </para>
/// <para>
/// Every rule this class creates is named with a fixed prefix so the app can find
/// and remove exactly its own rules and never touch a rule the user or another
/// program created.
/// </para>
/// <para>Requires elevation; the firewall API returns access denied otherwise.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class FirewallService
{
    /// <summary>Prefix on every rule this app creates, used to identify its own rules.</summary>
    public const string RuleNamePrefix = "SeewoAssistant Block ";

    private const int NET_FW_IP_PROTOCOL_ANY = 256;
    private const int NET_FW_RULE_DIR_IN = 1;
    private const int NET_FW_RULE_DIR_OUT = 2;
    private const int NET_FW_ACTION_BLOCK = 0;

    private readonly IAppLogger _logger;

    public FirewallService(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Builds the deterministic rule name for an executable.</summary>
    public static string BuildRuleName(string executablePath) =>
        RuleNamePrefix + Path.GetFileName(executablePath);

    /// <summary>True when a block rule already exists for the given executable.</summary>
    public bool IsBlocked(string executablePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            dynamic policy = CreatePolicy();
            dynamic rules = policy.Rules;

            foreach (var rule in rules)
            {
                string name = rule.Name;
                if (name.StartsWith(RuleNamePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string appPath = TryGetRuleApplicationName(rule);
                    if (!string.IsNullOrEmpty(appPath) &&
                        appPath.Equals(executablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        Marshal.ReleaseComObject(rule);
                        return true;
                    }
                }

                Marshal.ReleaseComObject(rule);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"Querying firewall rules failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Adds inbound and outbound block rules for an executable. Idempotent: an
    /// existing rule is replaced so the state is always what the caller asked for.
    /// </summary>
    public ActionResult Block(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (!OperatingSystem.IsWindows())
        {
            return ActionResult.Fail("防火墙规则只在 Windows 上可用。");
        }

        if (!File.Exists(executablePath))
        {
            return ActionResult.Fail($"找不到可执行文件：{executablePath}");
        }

        try
        {
            dynamic policy = CreatePolicy();

            // Remove any previous rule for this program so the operation is
            // idempotent and stale rules cannot accumulate.
            RemoveExisting(policy, executablePath);

            var name = BuildRuleName(executablePath);
            var added = 0;

            foreach (var direction in new[] { NET_FW_RULE_DIR_IN, NET_FW_RULE_DIR_OUT })
            {
                dynamic rule = CreateRule(direction, executablePath);
                rule.Name = direction == NET_FW_RULE_DIR_OUT ? name + " (出站)" : name + " (入站)";
                rule.Description =
                    "由 SeewoAssistant 创建：阻止该程序访问网络。可在 SeewoAssistant 中移除。";
                rule.ApplicationName = executablePath;
                rule.Protocol = NET_FW_IP_PROTOCOL_ANY;
                rule.Direction = direction;
                rule.Action = NET_FW_ACTION_BLOCK;
                rule.Enabled = true;

                // Apply to every profile: the same program should not be able to
                // reach the network just because the network type changed.
                rule.Profiles = CurrentProfileMask(policy);

                policy.Rules.Add(rule);
                Marshal.ReleaseComObject(rule);
                added++;
            }

            Marshal.ReleaseComObject(policy);

            _logger.Info($"Blocked network access for {executablePath} ({added} rules).");
            return ActionResult.Ok($"已禁止「{Path.GetFileName(executablePath)}」联网（入站 + 出站）。");
        }
        catch (UnauthorizedAccessException)
        {
            return ActionResult.Fail("修改防火墙规则需要管理员权限，请以管理员身份运行本程序。");
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            return ActionResult.Fail("修改防火墙规则被拒绝，请以管理员身份运行本程序。");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to block {executablePath}.", ex);
            return ActionResult.Fail($"设置防火墙规则失败：{ex.Message}", ex);
        }
    }

    /// <summary>Removes the block rules for an executable. Idempotent.</summary>
    public ActionResult Unblock(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (!OperatingSystem.IsWindows())
        {
            return ActionResult.Fail("防火墙规则只在 Windows 上可用。");
        }

        try
        {
            dynamic policy = CreatePolicy();
            var removed = RemoveExisting(policy, executablePath);
            Marshal.ReleaseComObject(policy);

            _logger.Info($"Removed {removed} firewall rule(s) for {executablePath}.");
            return removed > 0
                ? ActionResult.Ok($"已恢复「{Path.GetFileName(executablePath)}」联网（移除 {removed} 条规则）。")
                : ActionResult.Ok($"「{Path.GetFileName(executablePath)}」本来就没有被禁止联网。");
        }
        catch (UnauthorizedAccessException)
        {
            return ActionResult.Fail("修改防火墙规则需要管理员权限，请以管理员身份运行本程序。");
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            return ActionResult.Fail("修改防火墙规则被拒绝，请以管理员身份运行本程序。");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to unblock {executablePath}.", ex);
            return ActionResult.Fail($"移除防火墙规则失败：{ex.Message}", ex);
        }
    }

    /// <summary>Removes every rule this app has ever created. Used on uninstall.</summary>
    public ActionResult RemoveAllRules()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ActionResult.Fail("防火墙规则只在 Windows 上可用。");
        }

        try
        {
            dynamic policy = CreatePolicy();
            dynamic rules = policy.Rules;

            var toRemove = new List<string>();

            foreach (var rule in rules)
            {
                try
                {
                    string name = rule.Name;
                    if (name.StartsWith(RuleNamePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        toRemove.Add(name);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(rule);
                }
            }

            foreach (var name in toRemove)
            {
                policy.Rules.Remove(name);
            }

            Marshal.ReleaseComObject(policy);

            return ActionResult.Ok($"已移除 {toRemove.Count} 条由本程序创建的防火墙规则。");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to remove all firewall rules.", ex);
            return ActionResult.Fail($"移除防火墙规则失败：{ex.Message}", ex);
        }
    }

    private int RemoveExisting(dynamic policy, string executablePath)
    {
        dynamic rules = policy.Rules;
        var toRemove = new List<string>();

        foreach (var rule in rules)
        {
            try
            {
                string name = rule.Name;
                if (!name.StartsWith(RuleNamePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var appPath = TryGetRuleApplicationName(rule);
                if (!string.IsNullOrEmpty(appPath) &&
                    appPath.Equals(executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    toRemove.Add(name);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(rule);
            }
        }

        foreach (var name in toRemove)
        {
            policy.Rules.Remove(name);
        }

        return toRemove.Count;
    }

    private static string TryGetRuleApplicationName(dynamic rule)
    {
        try
        {
            return (string)rule.ApplicationName;
        }
        catch (COMException)
        {
            // A rule with no application scope, e.g. a port rule.
            return string.Empty;
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return string.Empty;
        }
    }

    /// <summary>Returns the bitmask of the profiles that are currently active.</summary>
    private static int CurrentProfileMask(dynamic policy)
    {
        // NET_FW_PROFILE2_DOMAIN=1, PRIVATE=2, PUBLIC=4. Applying to all three is
        // the safest default for a block rule.
        return 1 | 2 | 4;
    }

    private static dynamic CreatePolicy()
    {
        // CLSID_NetFwPolicy2 = {E2B3C97F-6AE1-41AC-817A-F6F92166D7DD}
        var type = Type.GetTypeFromCLSID(new Guid("E2B3C97F-6AE1-41AC-817A-F6F92166D7DD"))
            ?? throw new InvalidOperationException("Windows 防火墙 COM 组件不可用。");

        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("无法创建 Windows 防火墙策略对象。");
    }

    private static dynamic CreateRule(int direction, string executablePath)
    {
        // CLSID_NetFwRule = {2C5BC43E-3369-4C33-AB0C-BE9469677AF4}
        var type = Type.GetTypeFromCLSID(new Guid("2C5BC43E-3369-4C33-AB0C-BE9469677AF4"))
            ?? throw new InvalidOperationException("Windows 防火墙规则 COM 组件不可用。");

        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("无法创建防火墙规则对象。");
    }
}
