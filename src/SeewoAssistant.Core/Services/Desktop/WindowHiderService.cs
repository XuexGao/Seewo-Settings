using System.Runtime.Versioning;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Interop;

namespace SeewoAssistant.Core.Services.Desktop;

/// <summary>
/// Hides every top-level window except those belonging to Windows itself, and restores
/// them on demand.
/// </summary>
/// <remarks>
/// <para>
/// This is the "一键隐藏" action: the user wants a clean desktop with nothing on it but
/// the system's own windows, without closing anything. Hiding is the right primitive
/// rather than minimising, because a minimised window still occupies the taskbar and
/// can be restored by an errant click.
/// </para>
/// <para>
/// Nothing is destroyed. Every handle that was hidden is recorded, and
/// <see cref="Restore"/> puts exactly those windows back. A window that has since
/// closed is skipped rather than treated as an error.
/// </para>
/// <para>
/// "System" windows are identified conservatively: the shell, the taskbar, the desktop,
/// the accessibility and input surfaces, and anything running from the Windows
/// directory. Hiding those would leave the user with no way to interact with the
/// machine, which is why the list is a hard-coded set of names and paths rather than a
/// heuristic.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowHiderService
{
    private readonly IAppLogger _logger;
    private readonly object _gate = new();

    /// <summary>Handles hidden by the last <see cref="HideAll"/>, in hide order.</summary>
    private readonly List<nint> _hidden = [];

    public WindowHiderService(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>True when windows are currently hidden.</summary>
    public bool IsHidden
    {
        get
        {
            lock (_gate)
            {
                return _hidden.Count > 0;
            }
        }
    }

    /// <summary>How many windows the last <see cref="HideAll"/> hid.</summary>
    public int HiddenCount
    {
        get
        {
            lock (_gate)
            {
                return _hidden.Count;
            }
        }
    }

    /// <summary>
    /// Process names that must never be hidden.
    /// </summary>
    /// <remarks>
    /// Hiding the shell, the taskbar or the desktop leaves the machine unusable - there
    /// is no taskbar to click and no way to bring anything back except the tray icon,
    /// which may itself be inside the hidden shell. The list is explicit rather than
    /// pattern-based so that a new entry is a deliberate decision.
    /// </remarks>
    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // The shell and its surfaces.
        "explorer",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
        "ShellHost",
        "sihost",
        "dwm",
        "TextInputHost",
        "ApplicationFrameHost",
        "LockApp",
        "LogonUI",
        "winlogon",
        "csrss",
        "wininit",
        "services",
        "lsass",
        "smss",
        "fontdrvhost",
        "ctfmon",

        // Accessibility and input: hiding these can strand a user who depends on them.
        "Magnify",
        "Narrator",
        "osk",
        "TabTip",
        "SecurityHealthSystray",
        "SecurityHealthService",

        // The notification and volume flyouts.
        "SystemSettings",
        "ShellHostExperience",
        "Widgets",
        "WidgetService",
    };

    /// <summary>
    /// Process names that are the user's own session infrastructure rather than
    /// applications, and are also left alone.
    /// </summary>
    private static readonly HashSet<string> ProtectedProcessNamesExtra = new(StringComparer.OrdinalIgnoreCase)
    {
        "SeewoAssistant",
    };

    /// <summary>
    /// Hides every eligible top-level window.
    /// </summary>
    /// <returns>The number of windows hidden.</returns>
    public int HideAll(bool includeOwnWindow = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        // Start from a clean slate: a previous hide that was never restored would
        // otherwise accumulate handles that may since have been reused.
        lock (_gate)
        {
            _hidden.Clear();
        }

        var ownProcessId = Environment.ProcessId;
        var hidden = new List<nint>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);

                if (processId == 0)
                {
                    return true;
                }

                // The app's own window is normally kept visible so the user can undo
                // this, and so the tray icon remains reachable.
                if (processId == (uint)ownProcessId && !includeOwnWindow)
                {
                    return true;
                }

                var (processName, processPath) = ResolveProcess(processId);

                if (ShouldNeverHide(processName, processPath, hWnd))
                {
                    return true;
                }

                // A window with no title and no size is a helper or cloaked surface;
                // hiding it achieves nothing and risks breaking an app.
                if (!IsRealWindow(hWnd))
                {
                    return true;
                }

                if (NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE))
                {
                    hidden.Add(hWnd);
                }

                return true;
            }
            catch (Exception ex)
            {
                // One uncooperative window must not abort the whole operation.
                _logger.Debug($"Skipping window 0x{hWnd:X} while hiding: {ex.Message}");
                return true;
            }
        }, nint.Zero);

        lock (_gate)
        {
            _hidden.AddRange(hidden);
        }

        _logger.Info($"Hid {hidden.Count} window(s).");
        return hidden.Count;
    }

    /// <summary>
    /// Restores every window hidden by the last <see cref="HideAll"/>.
    /// </summary>
    /// <returns>The number of windows restored.</returns>
    public int Restore()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        List<nint> targets;

        lock (_gate)
        {
            targets = [.. _hidden];
            _hidden.Clear();
        }

        var restored = 0;

        foreach (var hWnd in targets)
        {
            try
            {
                // The handle may have been reused by a different window after the
                // original closed. IsWindow only proves the handle is valid, so the
                // restore is best-effort: showing a stale handle is harmless, and a
                // reused handle belonging to a hidden window would be shown, which is
                // the same outcome the user asked for.
                if (!NativeMethods.IsWindow(hWnd))
                {
                    continue;
                }

                if (NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOW))
                {
                    restored++;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Could not restore window 0x{hWnd:X}: {ex.Message}");
            }
        }

        _logger.Info($"Restored {restored} window(s).");
        return restored;
    }

    /// <summary>True when a window must be left alone.</summary>
    private static bool ShouldNeverHide(string processName, string processPath, nint hWnd)
    {
        if (ProtectedProcessNames.Contains(processName) ||
            ProtectedProcessNamesExtra.Contains(processName))
        {
            return true;
        }

        // Anything running out of the Windows directory is part of the OS.
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            if (!string.IsNullOrWhiteSpace(windowsDirectory) &&
                processPath.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // The desktop and the taskbar are also identifiable by class, which covers a
        // shell whose process could not be resolved.
        var className = GetClassName(hWnd);

        return className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "NotifyIconOverflowWindow" or "TaskListThumbnailWnd" or "Windows.UI.Core.CoreWindow"
            or "XamlExplorerHostIslandWindow" or "MultitaskingViewFrame" or "ForegroundStaging";
    }

    /// <summary>True when a window is substantial enough to be worth hiding.</summary>
    private static bool IsRealWindow(nint hWnd)
    {
        if (!NativeMethods.GetWindowRect(hWnd, out var rect))
        {
            return false;
        }

        // Ignore zero-area windows: they are invisible helpers.
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        var title = GetWindowText(hWnd);

        // An untitled window is a helper surface. Hiding it would achieve nothing the
        // user can see, and might break a tray or input component.
        return !string.IsNullOrWhiteSpace(title);
    }

    private static string GetWindowText(nint hWnd)
    {
        var length = NativeMethods.GetWindowTextLengthW(hWnd);

        if (length <= 0)
        {
            return string.Empty;
        }

        // One extra character for the terminator.
        var buffer = new System.Text.StringBuilder(length + 1);
        NativeMethods.GetWindowTextW(hWnd, buffer, buffer.Capacity);

        return buffer.ToString();
    }

    private static string GetClassName(nint hWnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        NativeMethods.GetClassNameW(hWnd, buffer, buffer.Capacity);

        return buffer.ToString();
    }

    private static (string Name, string Path) ResolveProcess(uint processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);

            string? path = null;

            try
            {
                path = process.MainModule?.FileName;
            }
            catch (System.ComponentModel.Win32Exception) { /* Bitness or protected. */ }
            catch (InvalidOperationException) { /* Exited. */ }
            catch (NotSupportedException) { }

            return (process.ProcessName, path ?? string.Empty);
        }
        catch (ArgumentException)
        {
            // The process exited between enumeration and this call.
            return (string.Empty, string.Empty);
        }
        catch (InvalidOperationException)
        {
            return (string.Empty, string.Empty);
        }
    }
}
