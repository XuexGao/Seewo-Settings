using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SeewoAssistant.Core.Abstractions;

namespace SeewoAssistant.Core.Services.Seewo;

/// <summary>
/// Registers and unregisters this application in the per-user Run key so it starts
/// with the user's session.
/// </summary>
/// <remarks>
/// The per-user key (<c>HKCU</c>) is used rather than the machine-wide one because
/// starting a tray app is a per-user preference, and the per-user key needs no
/// elevation. Writing to <c>HKLM</c> for this would force an administrator prompt
/// just to toggle a checkbox.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AppStartupService
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Value name under the Run key. Distinctive enough not to collide.</summary>
    public const string ValueName = "SeewoAssistant";

    private readonly IAppLogger _logger;

    public AppStartupService(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>True when the Run key value exists and points at this executable.</summary>
    public bool IsRegistered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName)?.ToString();

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            // If the app has been moved since the entry was written, the stale path
            // is treated as not registered so the next enable rewrites it correctly.
            var currentPath = GetExecutablePath();
            return value.Contains(currentPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Adds or removes the Run key value. Returns a result describing what happened.</summary>
    public Models.ActionResult SetRegistered(bool enabled, bool startMinimized = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Models.ActionResult.Fail("开机自启只在 Windows 上可用。");
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

            if (key is null)
            {
                return Models.ActionResult.Fail($"无法打开注册表项 HKCU\\{RunKeyPath}。");
            }

            if (enabled)
            {
                var command = BuildCommand(startMinimized);
                key.SetValue(ValueName, command, RegistryValueKind.String);

                _logger.Info($"Registered startup entry: {command}");
                return Models.ActionResult.Ok("已设置为开机自动启动。");
            }

            if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _logger.Info("Removed the startup entry.");
            }

            return Models.ActionResult.Ok("已取消开机自动启动。");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Models.ActionResult.Fail("写入注册表被拒绝。", ex);
        }
        catch (System.Security.SecurityException ex)
        {
            return Models.ActionResult.Fail("写入注册表被安全策略拒绝。", ex);
        }
        catch (IOException ex)
        {
            return Models.ActionResult.Fail($"写入注册表失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds the Run command. Quoted because the install path can contain spaces,
    /// which this project's own folder name does.
    /// </summary>
    private static string BuildCommand(bool startMinimized)
    {
        var path = GetExecutablePath();
        return startMinimized ? $"\"{path}\" --minimized" : $"\"{path}\"";
    }

    /// <summary>Full path of the running executable, or the process path as a fallback.</summary>
    private static string GetExecutablePath()
    {
        // Environment.ProcessPath is the correct API here: Assembly.Location returns
        // an empty string for a single-file published app, and the app ships as one.
        var path = Environment.ProcessPath;

        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        using var process = Process.GetCurrentProcess();
        return process.MainModule?.FileName ?? string.Empty;
    }
}
