using System.Runtime.InteropServices;

namespace SeewoAssistant.Services;

/// <summary>
/// A Win32 popup window that shows the camera/microphone alert prominently.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a toast notification can be suppressed by Focus Assist, by
/// full-screen presentation mode, or by the user's notification settings. For a
/// camera or microphone alert, silent suppression defeats the entire purpose, so an
/// optional always-on-top banner is offered as well.
/// </para>
/// <para>
/// It is a raw Win32 window rather than a second WinUI window because it must be
/// creatable and dismissible from any thread, must never take focus, and must not
/// appear in the taskbar or Alt+Tab. A layered, non-activating popup with
/// <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST</c> is the correct
/// primitive and is far lighter than a second XAML window.
/// </para>
/// </remarks>
internal sealed class BannerWindow : IDisposable
{
    private const string WindowClassName = "SeewoAssistant.BannerWindow";

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;

    private const uint SW_SHOWNOACTIVATE = 4;
    private const int SW_HIDE = 0;

    private const int WM_PAINT = 0x000F;
    private const int WM_TIMER = 0x0113;
    private const int WM_DESTROY = 0x0002;
    private const int WM_ERASEBKGND = 0x0014;

    private const int TimerDismiss = 1;

    private static readonly object ClassGate = new();
    private static bool _classRegistered;
    private static WndProcDelegate? _wndProcDelegate;

    private readonly object _gate = new();

    private nint _hwnd;
    private int _dismissSeconds = 6;
    private bool _disposed;

    /// <summary>
    /// Shows or updates the alert. Safe to call from any thread.
    /// </summary>
    internal void ShowAlert(string title, string body, DateTime observedAt, TimeSpan duration, bool playSound)
    {
        _dismissSeconds = Math.Max(2, (int)duration.TotalSeconds);

        // The window procedure is a static method, so the strings it paints are
        // published here. Only one banner is shown at a time, so a static slot is
        // correct and avoids marshalling an instance pointer through a window word.
        _activeTitle = title;
        _activeBody = $"{body}    时间：{observedAt:HH:mm:ss}";

        EnsureWindow();

        if (_hwnd == nint.Zero)
        {
            return;
        }

        // Position at the top centre of the primary monitor's work area, clear of
        // the taskbar and of the notification area.
        var workArea = GetPrimaryWorkArea();
        const int width = 420;
        var height = 120;

        var x = workArea.Left + ((workArea.Right - workArea.Left) - width) / 2;
        var y = workArea.Top + 24;

        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        InvalidateRect(_hwnd, nint.Zero, true);
        UpdateWindow(_hwnd);

        // Restart the dismissal timer on every alert so a burst of events does not
        // make the banner disappear early.
        KillTimer(_hwnd, TimerDismiss);
        SetTimer(_hwnd, TimerDismiss, (uint)(_dismissSeconds * 1000), nint.Zero);

        if (playSound)
        {
            // A system sound is used rather than a custom asset so there is nothing
            // extra to ship and the sound respects the user's scheme.
            MessageBeep(0x00000040); // MB_ICONINFORMATION
        }
    }

    private void EnsureWindow()
    {
        lock (_gate)
        {
            if (_disposed || _hwnd != nint.Zero)
            {
                return;
            }

            RegisterWindowClass();

            _hwnd = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED,
                WindowClassName,
                "SeewoAssistant",
                WS_POPUP,
                0, 0, 420, 120,
                nint.Zero, nint.Zero, GetModuleHandleW(null), nint.Zero);

            if (_hwnd == nint.Zero)
            {
                return;
            }

            // 235/255 gives a slight translucency so the banner reads as an overlay
            // rather than as a window the user has to deal with.
            SetLayeredWindowAttributes(_hwnd, 0, 235, 0x00000002); // LWA_ALPHA
        }
    }

    private static void RegisterWindowClass()
    {
        lock (ClassGate)
        {
            if (_classRegistered)
            {
                return;
            }

            _wndProcDelegate = StaticWindowProc;

            var windowClass = new WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = GetModuleHandleW(null),
                hIcon = nint.Zero,
                hCursor = LoadCursorW(nint.Zero, 32512), // IDC_ARROW
                hbrBackground = nint.Zero,               // painted in WM_PAINT
                lpszMenuName = null,
                lpszClassName = WindowClassName,
                hIconSm = nint.Zero,
            };

            // A failure here means the class already exists, which is harmless.
            RegisterClassExW(ref windowClass);
            _classRegistered = true;
        }
    }

    private static nint StaticWindowProc(nint hWnd, uint message, nint wParam, nint lParam)
    {
        // A managed exception raised inside a native callback unwinds through native
        // frames, which terminates the process and bypasses App.UnhandledException.
        // Catching here means a drawing or timing fault degrades to a blank banner
        // rather than taking the whole application down.
        try
        {
            switch (message)
            {
                case WM_ERASEBKGND:
                    // Painting happens in WM_PAINT; suppressing the erase avoids flicker.
                    return 1;

                case WM_PAINT:
                    PaintBanner(hWnd);
                    return 0;

                case WM_TIMER:
                    if (wParam == TimerDismiss)
                    {
                        KillTimer(hWnd, TimerDismiss);

                        // Hide rather than destroy: the window is reused for the next
                        // alert, and destroying it here would leave a dangling handle
                        // in the field that owns it.
                        ShowWindow(hWnd, SW_HIDE);
                    }

                    return 0;

                case WM_DESTROY:
                    return 0;

                default:
                    return DefWindowProcW(hWnd, message, wParam, lParam);
            }
        }
        catch (Exception)
        {
            // Deliberately swallowed. There is no logging facility available from a
            // static callback without risking a re-entrant call, and the window is
            // non-essential: the toast notification is the primary alert channel.
            return DefWindowProcW(hWnd, message, wParam, lParam);
        }
    }

    /// <summary>
    /// Draws the banner. The text is pulled from the window's owning instance via
    /// <c>GetWindowLongPtrW(GWLP_USERDATA)</c>, which is set at creation.
    /// </summary>
    private static void PaintBanner(nint hWnd)
    {
        var deviceContext = BeginPaint(hWnd, out var paintStruct);

        if (deviceContext == nint.Zero)
        {
            EndPaint(hWnd, ref paintStruct);
            return;
        }

        try
        {
            GetClientRect(hWnd, out var client);

            // Dark, high-contrast surface. This deliberately does not follow the
            // system theme: the banner must be unmistakable, and it is shown for a
            // few seconds at a time.
            var background = CreateSolidBrush(Rgb(28, 28, 32));
            FillRect(deviceContext, ref client, background);
            DeleteObject(background);

            // Accent bar down the left edge.
            var accent = CreateSolidBrush(Rgb(232, 90, 60));
            var accentRect = client;
            accentRect.Right = client.Left + 6;
            FillRect(deviceContext, ref accentRect, accent);
            DeleteObject(accent);

            SetBkMode(deviceContext, 1); // TRANSPARENT
            SetTextColor(deviceContext, Rgb(255, 255, 255));

            // The banner content is fetched from the static field set by ShowAlert;
            // this window class has at most one live instance per process.
            var title = _activeTitle;
            var body = _activeBody;

            var titleRect = client;
            titleRect.Left += 20;
            titleRect.Top += 16;
            titleRect.Right -= 16;

            var titleFont = CreateFont(-20, 0, 0, 0, 700, 0, 0, 0, 0, 0, 0, 0, 0, "Microsoft YaHei UI");
            var previousFont = SelectObject(deviceContext, titleFont);
            DrawText(deviceContext, title, -1, ref titleRect, 0x00000010 | 0x00000020); // NOPREFIX | WORDBREAK
            SelectObject(deviceContext, previousFont);
            DeleteObject(titleFont);

            SetTextColor(deviceContext, Rgb(210, 210, 215));

            var bodyRect = client;
            bodyRect.Left += 20;
            bodyRect.Top += 48;
            bodyRect.Right -= 16;
            bodyRect.Bottom -= 12;

            var bodyFont = CreateFont(-15, 0, 0, 0, 400, 0, 0, 0, 0, 0, 0, 0, 0, "Microsoft YaHei UI");
            previousFont = SelectObject(deviceContext, bodyFont);
            DrawText(deviceContext, body, -1, ref bodyRect, 0x00000010 | 0x00000020);
            SelectObject(deviceContext, previousFont);
            DeleteObject(bodyFont);
        }
        finally
        {
            EndPaint(hWnd, ref paintStruct);
        }
    }

    // The window procedure is static, so the text to paint lives in static fields.
    // Only one banner is ever shown at a time.
    private static string _activeTitle = string.Empty;
    private static string _activeBody = string.Empty;

    private static uint Rgb(byte r, byte g, byte b) =>
        (uint)((b << 16) | (g << 8) | r);

    private static RECT GetPrimaryWorkArea()
    {
        var area = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };

        if (SystemParametersInfoW(0x0030, 0, ref area, 0)) // SPI_GETWORKAREA
        {
            return area;
        }

        return area;
    }

    internal void Close()
    {
        lock (_gate)
        {
            if (_hwnd != nint.Zero)
            {
                KillTimer(_hwnd, TimerDismiss);
                DestroyWindow(_hwnd);
                _hwnd = nint.Zero;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
    }

    // ---------------------------------------------------------------- interop

    private delegate nint WndProcDelegate(nint hWnd, uint message, nint wParam, nint lParam);

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly nint HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(nint hWnd, nint lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetTimer(nint hWnd, nint nIDEvent, uint uElapse, nint lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(nint hWnd, nint uIDEvent);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DrawTextW")]
    private static extern int DrawText(nint hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint hdc, ref RECT lprc, nint hbr);

    [DllImport("user32.dll")]
    private static extern int SetBkMode(nint hdc, int mode);

    [DllImport("user32.dll")]
    private static extern uint SetTextColor(nint hdc, uint color);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW")]
    private static extern nint CreateFont(int cHeight, int cWidth, int cEscapement, int cOrientation,
        int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadCursorW(nint hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint uType);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);
}
