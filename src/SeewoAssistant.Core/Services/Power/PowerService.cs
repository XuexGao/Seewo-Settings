using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Interop;

namespace SeewoAssistant.Core.Services.Power;

/// <summary>Which power transition to perform.</summary>
public enum PowerAction
{
    Shutdown,
    Restart,
    Logoff,
    Lock,
}

/// <summary>
/// Performs system power transitions.
/// </summary>
/// <remarks>
/// <para>
/// Shutdown and restart use <c>InitiateSystemShutdownExW</c> rather than
/// <c>ExitWindowsEx</c> because it supports a countdown and an on-screen message,
/// which gives the user a chance to cancel a scheduled shutdown. Both require
/// <c>SeShutdownPrivilege</c>, which is enabled here on demand — a standard user
/// holds the privilege but it is disabled by default.
/// </para>
/// <para>
/// <b>On scheduled power-on.</b> This service deliberately does not offer it. A
/// machine in the S5 (fully off) state cannot be started by software running on
/// that machine: there is no OS to run the code. The only reliable mechanisms are
/// the firmware's RTC alarm or Wake-on-LAN, both configured outside Windows. Rather
/// than present an unreliable feature, the UI explains this and points at the
/// firmware setting.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PowerService
{
    private readonly IAppLogger _logger;

    public PowerService(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Schedules a shutdown or restart with a countdown.
    /// </summary>
    /// <param name="action">Shutdown or restart.</param>
    /// <param name="timeoutSeconds">
    /// Countdown length. Zero shuts down immediately with no chance to cancel.
    /// </param>
    /// <param name="message">Message shown in the system shutdown dialog.</param>
    /// <param name="forceCloseApplications">
    /// When true, applications that do not respond are terminated rather than
    /// blocking the shutdown.
    /// </param>
    public ActionResult Initiate(
        PowerAction action,
        int timeoutSeconds = 60,
        string? message = null,
        bool forceCloseApplications = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ActionResult.Fail("电源操作只在 Windows 上可用。");
        }

        if (action == PowerAction.Logoff || action == PowerAction.Lock)
        {
            return LogoffOrLock(action);
        }

        if (!TryEnableShutdownPrivilege())
        {
            return ActionResult.Fail(
                "无法获取关机特权（SeShutdownPrivilege）。请以管理员身份运行本程序。");
        }

        var reboot = action == PowerAction.Restart;
        var reason = NativeMethods.SHTDN_REASON_MAJOR_APPLICATION | NativeMethods.SHTDN_REASON_FLAG_PLANNED;

        var ok = NativeMethods.InitiateSystemShutdownExW(
            null,
            string.IsNullOrWhiteSpace(message) ? null : message,
            (uint)Math.Max(0, timeoutSeconds),
            forceCloseApplications,
            reboot,
            reason);

        if (!ok)
        {
            var error = Marshal.GetLastWin32Error();
            var text = $"{(reboot ? "重启" : "关机")}失败，Win32 错误 {error}。";
            _logger.Warn(text);
            return ActionResult.Fail(text);
        }

        var description = reboot ? "重启" : "关机";
        var suffix = timeoutSeconds > 0 ? $"，将在 {timeoutSeconds} 秒后执行" : "，正在立即执行";

        _logger.Info($"Initiated {description}{suffix}.");
        return ActionResult.Ok($"已计划{description}{suffix}。可运行 shutdown /a 取消。");
    }

    /// <summary>Cancels a pending shutdown that has a countdown.</summary>
    public ActionResult Abort()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ActionResult.Fail("电源操作只在 Windows 上可用。");
        }

        if (!TryEnableShutdownPrivilege())
        {
            return ActionResult.Fail("无法获取关机特权，请以管理员身份运行。");
        }

        if (!NativeMethods.AbortSystemShutdownW(null))
        {
            var error = Marshal.GetLastWin32Error();
            return ActionResult.Fail($"取消关机失败，Win32 错误 {error}（可能本来就没有待执行的关机）。");
        }

        _logger.Info("Pending shutdown aborted.");
        return ActionResult.Ok("已取消待执行的关机。");
    }

    private ActionResult LogoffOrLock(PowerAction action)
    {
        if (action == PowerAction.Lock)
        {
            // LockWorkStation needs no privilege and no confirmation.
            if (!LockWorkStation())
            {
                var error = Marshal.GetLastWin32Error();
                return ActionResult.Fail($"锁定工作站失败，Win32 错误 {error}。");
            }

            _logger.Info("Workstation locked.");
            return ActionResult.Ok("已锁定工作站。");
        }

        if (!NativeMethods.ExitWindowsEx(NativeMethods.EWX_LOGOFF, 0))
        {
            var error = Marshal.GetLastWin32Error();
            return ActionResult.Fail($"注销失败，Win32 错误 {error}。");
        }

        _logger.Info("Logoff initiated.");
        return ActionResult.Ok("已注销当前用户。");
    }

    /// <summary>
    /// Enables <c>SeShutdownPrivilege</c> on the current token. The privilege is
    /// present but disabled for a standard user, so this is required before any
    /// shutdown call.
    /// </summary>
    internal static bool TryEnableShutdownPrivilege()
    {
        if (!NativeMethods.OpenProcessToken(
                GetCurrentProcess(),
                NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY,
                out var token))
        {
            return false;
        }

        try
        {
            if (!NativeMethods.LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid))
            {
                return false;
            }

            var privileges = new NativeMethods.TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = NativeMethods.SE_PRIVILEGE_ENABLED,
            };

            if (!NativeMethods.AdjustTokenPrivileges(token, false, ref privileges, 0, nint.Zero, nint.Zero))
            {
                return false;
            }

            // AdjustTokenPrivileges succeeds even when nothing was enabled, so the
            // last error is the real result.
            return Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    /// <summary>True when the current process can shut the machine down.</summary>
    public static bool CanShutdown()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // Probe without side effects: enabling the privilege is harmless and is the
        // only reliable way to know.
        return TryEnableShutdownPrivilege();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetCurrentProcess();
}
