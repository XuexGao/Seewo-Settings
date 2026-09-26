using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Interop;
using SeewoAssistant.Core.Models;

namespace SeewoAssistant.Core.Services.CaptureGuard;

/// <summary>
/// Enumerates top-level windows and resolves their owning process, title, class and
/// current capture-protection state.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowEnumerator
{
    private readonly IAppLogger _logger;
    private readonly int _ownProcessId = Environment.ProcessId;

    public WindowEnumerator(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Lists visible, titled top-level windows owned by other processes, plus this
    /// process's own windows when <paramref name="includeOwnProcess"/> is set.
    /// </summary>
    /// <remarks>
    /// Windows that have no title and are not owned by this process are skipped:
    /// they are almost always invisible helper or cloaked windows that would only
    /// add noise to a picker. Tool windows are also skipped for the same reason.
    /// </remarks>
    public IReadOnlyList<WindowInfo> Enumerate(bool includeOwnProcess = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<WindowInfo>();
        }

        var results = new List<WindowInfo>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            try
            {
                var info = Describe(hWnd, includeOwnProcess);
                if (info is not null)
                {
                    results.Add(info);
                }
            }
            catch (Exception ex)
            {
                // A window can disappear mid-enumeration, and a protected process can
                // refuse the query. Neither should abort the whole scan.
                _logger.Debug($"Skipping window 0x{hWnd:X}: {ex.Message}");
            }

            return true; // Continue enumeration.
        }, nint.Zero);

        return results
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Builds a <see cref="WindowInfo"/> for one handle, or null when it is not a candidate.</summary>
    public WindowInfo? Describe(nint hWnd, bool includeOwnProcess = true)
    {
        if (hWnd == nint.Zero || !NativeMethods.IsWindow(hWnd))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);

        if (processId == 0)
        {
            return null;
        }

        var isOwnProcess = processId == _ownProcessId;

        if (isOwnProcess && !includeOwnProcess)
        {
            return null;
        }

        if (!isOwnProcess && !NativeMethods.IsWindowVisible(hWnd))
        {
            return null;
        }

        var title = GetWindowTitle(hWnd);
        var className = GetWindowClassName(hWnd);

        // Skip tool windows: they are things like floating palettes that users do
        // not think of as "a window" to protect.
        if (HasToolWindowStyle(hWnd))
        {
            return null;
        }

        // An untitled window from another process is not identifiable in a list.
        if (string.IsNullOrWhiteSpace(title) && !isOwnProcess)
        {
            return null;
        }

        NativeMethods.GetWindowRect(hWnd, out var rect);

        var (processName, processPath) = ResolveProcess(processId);
        var protection = GetProtection(hWnd);

        return new WindowInfo
        {
            Handle = hWnd,
            Title = title,
            ClassName = className,
            ProcessId = (int)processId,
            ProcessName = processName,
            ProcessPath = processPath,
            Width = rect.Width,
            Height = rect.Height,
            Protection = protection,
            IsOwnProcess = isOwnProcess,
        };
    }

    /// <summary>Reads the display affinity currently set on a window.</summary>
    public static CaptureProtectionState GetProtection(nint hWnd)
    {
        if (!NativeMethods.GetWindowDisplayAffinity(hWnd, out var affinity))
        {
            return CaptureProtectionState.None;
        }

        // WDA_NONE=0x00, WDA_MONITOR=0x01, WDA_EXCLUDEFROMCAPTURE=0x11
        return affinity switch
        {
            0x11 => CaptureProtectionState.Excluded,
            0x01 => CaptureProtectionState.Blackout,
            _ => CaptureProtectionState.None,
        };
    }

    /// <summary>
    /// Resolves the top-level window under a screen point, which is what the
    /// drag-a-crosshair picker uses.
    /// </summary>
    public nint GetWindowAtCursor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return nint.Zero;
        }

        if (!GetCursorPos(out var point))
        {
            return nint.Zero;
        }

        var hWnd = NativeMethods.WindowFromPoint(new NativeMethods.Point { X = point.X, Y = point.Y });
        if (hWnd == nint.Zero)
        {
            return nint.Zero;
        }

        // Walk up to the top-level window so dragging over a child control still
        // selects the window the user sees.
        var root = NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOT);
        return root != nint.Zero ? root : hWnd;
    }

    private static string GetWindowTitle(nint hWnd)
    {
        var length = NativeMethods.GetWindowTextLengthW(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        NativeMethods.GetWindowTextW(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetWindowClassName(nint hWnd)
    {
        var buffer = new StringBuilder(256);
        NativeMethods.GetClassNameW(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static bool HasToolWindowStyle(nint hWnd)
    {
        var style = IntPtr.Size == 8
            ? NativeMethods.GetWindowLongPtrW(hWnd, NativeMethods.GWL_EXSTYLE)
            : NativeMethods.GetWindowLongW(hWnd, NativeMethods.GWL_EXSTYLE);

        return (style.ToInt64() & NativeMethods.WS_EX_TOOLWINDOW) != 0;
    }

    private static (string Name, string Path) ResolveProcess(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName + ".exe";

            string path = string.Empty;
            try
            {
                path = process.MainModule?.FileName ?? string.Empty;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // A 64-bit process reading a 32-bit module, or a protected process.
                // The name alone is still useful.
            }
            catch (InvalidOperationException)
            {
                // The process exited.
            }

            return (name, path);
        }
        catch (ArgumentException)
        {
            // No such process.
            return ($"PID {processId}", string.Empty);
        }
        catch (InvalidOperationException)
        {
            return ($"PID {processId}", string.Empty);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
