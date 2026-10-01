using System.Runtime.Versioning;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Interop;

namespace SeewoAssistant.Core.Services.Desktop;

/// <summary>
/// Hides every top-level window except the ones Windows itself needs, and restores them
/// on demand.
/// </summary>
/// <remarks>
/// <para>
/// The filter is deliberately minimal, following the approach used by the reference
/// project <c>NoMoreCapture</c>: enumerate the visible top-level windows, skip this
/// application's own windows, skip the three shell classes that make up the desktop and
/// taskbar, and hide everything else.
/// </para>
/// <para>
/// An earlier version of this class tried to be cleverer - it protected a long list of
/// process names, anything installed under the Windows directory, and any window without
/// a title. That was wrong twice over: it hid almost nothing (a title is not required for
/// a window to be worth hiding), and the intent was never "hide only some of them". The
/// only windows that genuinely must stay are the desktop and the taskbar, because hiding
/// those leaves the machine with no way to interact with it.
/// </para>
/// <para>
/// Windows are hidden rather than minimised: a minimised window still occupies the
/// taskbar and can be restored by a stray click. Every handle that was hidden is
/// recorded, and <see cref="Restore"/> puts exactly those back.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowHiderService
{
    /// <summary>
    /// Window classes that must never be hidden.
    /// </summary>
    /// <remarks>
    /// <c>Progman</c> and <c>WorkerW</c> are the desktop background, and
    /// <c>Shell_TrayWnd</c> is the taskbar. These are the only three the reference
    /// project skips, and they are the only three that would leave the desktop
    /// unusable - there would be no taskbar to click and nothing to bring anything
    /// back with.
    /// </remarks>
    private static readonly string[] ShellClasses =
    [
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
    ];

    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private readonly int _ownProcessId;

    /// <summary>Handles hidden by the last <see cref="HideAll"/>, in hide order.</summary>
    private readonly List<nint> _hidden = [];

    public WindowHiderService(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _ownProcessId = Environment.ProcessId;
    }

    /// <summary>
    /// Keep this application's own window visible even though everything else is hidden.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Normally the app hides itself along with everything else, because "hide everything
    /// except system programs" includes it. The tray icon is then the way back, and its
    /// window is message-only so it is never enumerated here.
    /// </para>
    /// <para>
    /// That reasoning depends on the tray icon existing. When it could not be created -
    /// the shell refuses <c>Shell_NotifyIcon</c> in some sessions - hiding this window too
    /// would take away the only control that can undo the operation, leaving the user to
    /// find Task Manager. The window is therefore kept visible in exactly that case, so
    /// the 恢复所有窗口 button stays reachable.
    /// </para>
    /// </remarks>
    public bool KeepOwnWindowVisible { get; set; }

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
    /// Hides every eligible top-level window, including this application's own.
    /// </summary>
    /// <returns>The number of windows hidden.</returns>
    /// <remarks>
    /// The app hides itself too: the request is "hide everything except system
    /// programs", and this application is not a system program. It stays reachable
    /// through the tray icon, whose window is message-only and therefore never
    /// enumerated here.
    /// </remarks>
    public int HideAll()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        // Start from a clean slate. A previous hide that was never restored would
        // otherwise accumulate handles that may since have been reused by other windows.
        lock (_gate)
        {
            _hidden.Clear();
        }

        var hidden = new List<nint>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!ShouldHide(hWnd))
                {
                    return true;
                }

                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE);
                hidden.Add(hWnd);

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
                if (!NativeMethods.IsWindow(hWnd))
                {
                    // The window closed while it was hidden; nothing to restore.
                    continue;
                }

                // ShowWindow returns whether the window was previously *visible*, not
                // whether the call succeeded. A window being restored was hidden, so it
                // returns FALSE even on success. Treating that as failure made the count
                // permanently zero and the UI report "nothing to restore" while the
                // desktop was in fact still hidden.
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOW);
                restored++;
            }
            catch (Exception ex)
            {
                _logger.Debug($"Could not restore window 0x{hWnd:X}: {ex.Message}");
            }
        }

        _logger.Info($"Restored {restored} window(s).");
        return restored;
    }

    /// <summary>True when a window should be hidden.</summary>
    private bool ShouldHide(nint hWnd)
    {
        if (!NativeMethods.IsWindowVisible(hWnd))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);

        if (processId == 0)
        {
            return false;
        }

        // Never hide ourselves when doing so would remove the only way to undo this.
        if (KeepOwnWindowVisible && processId == _ownProcessId)
        {
            return false;
        }

        // This application's own window is hidden along with everything else: the
        // request is "hide everything except system programs", and this is not a system
        // program. The tray icon is the way back, and its window is created with
        // HWND_MESSAGE, so it is message-only and never appears in this enumeration.
        //
        // The desktop background and the taskbar are the only windows that must stay:
        // without them there is no way to interact with the machine at all.
        var className = GetClassName(hWnd);

        foreach (var shellClass in ShellClasses)
        {
            if (string.Equals(className, shellClass, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string GetClassName(nint hWnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        NativeMethods.GetClassNameW(hWnd, buffer, buffer.Capacity);

        return buffer.ToString();
    }
}
