using System.Runtime.InteropServices;

namespace SeewoAssistant.Services;

/// <summary>
/// A notification-area icon with a context menu.
/// </summary>
/// <remarks>
/// Implemented with <c>Shell_NotifyIcon</c> directly rather than through a wrapper
/// package so the app has no extra dependency and the lifetime of the icon is fully
/// under our control. The icon is created with a message-only window, so it never
/// appears in the taskbar or Alt+Tab.
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private const string WindowClassName = "SeewoAssistant.TrayWindow";
    private const int TrayIconId = 1;

    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;
    private const int WM_COMMAND = 0x0111;
    private const int WM_DESTROY = 0x0002;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    private const int MF_STRING = 0x00000000;
    private const int MF_SEPARATOR = 0x00000800;
    private const int TPM_RIGHTBUTTON = 0x0002;
    private const int TPM_RETURNCMD = 0x0100;

    private const int MenuOpen = 1001;
    private const int MenuTogglePrivacy = 1002;
    private const int MenuToggleCamera = 1003;
    private const int MenuExit = 1099;

    private static readonly object ClassGate = new();
    private static bool _classRegistered;
    private static WndProcDelegate? _wndProcDelegate;
    private static TrayIcon? _instance;

    private readonly nint _hwnd;
    private nint _icon;
    private bool _disposed;

    private TrayIcon(nint hwnd, nint icon)
    {
        _hwnd = hwnd;
        _icon = icon;
    }

    /// <summary>Raised when the user chooses "open".</summary>
    internal event EventHandler? OpenRequested;

    /// <summary>Raised when the user chooses "exit".</summary>
    internal event EventHandler? ExitRequested;

    /// <summary>Raised when the user toggles the privacy monitor from the menu.</summary>
    internal event EventHandler? TogglePrivacyRequested;

    /// <summary>Raised when the user toggles the virtual camera from the menu.</summary>
    internal event EventHandler? ToggleCameraRequested;

    /// <summary>Text shown when hovering the icon.</summary>
    internal string Tooltip { get; set; } = "希沃助手";

    /// <summary>
    /// Creates the tray icon. Must be called on a thread with a message loop.
    /// </summary>
    /// <returns>The icon, or null when it could not be created.</returns>
    internal static TrayIcon? TryCreate()
    {
        RegisterWindowClass();

        // A message-only window keeps the icon's owner out of the taskbar and
        // Alt+Tab while still receiving the shell's callback messages.
        var hwnd = CreateWindowExW(
            0, WindowClassName, "SeewoAssistant.Tray",
            0, 0, 0, 0, 0,
            HWND_MESSAGE, nint.Zero, GetModuleHandleW(null), nint.Zero);

        if (hwnd == nint.Zero)
        {
            return null;
        }

        // IDI_APPLICATION is MAKEINTRESOURCE(32512), which is the integer 32512 in
        // the low word. Passing it as an nint avoids the string overload.
        var icon = LoadIconW(GetModuleHandleW(null), new nint(32512));

        if (icon == nint.Zero)
        {
            icon = LoadIconW(nint.Zero, new nint(32512));
        }

        var instance = new TrayIcon(hwnd, icon);
        _instance = instance;

        if (!instance.AddOrModify(NIM_ADD))
        {
            DestroyWindow(hwnd);
            _instance = null;
            return null;
        }

        return instance;
    }

    private bool AddOrModify(int message)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _icon,
            szTip = Tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        return Shell_NotifyIconW(message, ref data);
    }

    /// <summary>Updates the hover text.</summary>
    internal void UpdateTooltip(string tooltip)
    {
        Tooltip = tooltip.Length > 120 ? tooltip[..120] : tooltip;
        AddOrModify(NIM_MODIFY);
    }

    private static void RegisterWindowClass()
    {
        lock (ClassGate)
        {
            if (_classRegistered)
            {
                return;
            }

            _wndProcDelegate = WindowProc;

            var windowClass = new WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = GetModuleHandleW(null),
                lpszClassName = WindowClassName,
            };

            RegisterClassExW(ref windowClass);
            _classRegistered = true;
        }
    }

    private static nint WindowProc(nint hWnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_TRAYICON:
            {
                var notification = (int)(lParam.ToInt64() & 0xFFFF);

                switch (notification)
                {
                    case WM_LBUTTONUP:
                    case WM_LBUTTONDBLCLK:
                        _instance?.OpenRequested?.Invoke(_instance, EventArgs.Empty);
                        return 0;

                    case WM_RBUTTONUP:
                        _instance?.ShowContextMenu();
                        return 0;
                }

                return 0;
            }

            case WM_COMMAND:
            {
                switch ((int)(wParam.ToInt64() & 0xFFFF))
                {
                    case MenuOpen:
                        _instance?.OpenRequested?.Invoke(_instance, EventArgs.Empty);
                        return 0;

                    case MenuTogglePrivacy:
                        _instance?.TogglePrivacyRequested?.Invoke(_instance, EventArgs.Empty);
                        return 0;

                    case MenuToggleCamera:
                        _instance?.ToggleCameraRequested?.Invoke(_instance, EventArgs.Empty);
                        return 0;

                    case MenuExit:
                        _instance?.ExitRequested?.Invoke(_instance, EventArgs.Empty);
                        return 0;
                }

                return 0;
            }

            case WM_DESTROY:
                return 0;

            default:
                return DefWindowProcW(hWnd, message, wParam, lParam);
        }
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();

        if (menu == nint.Zero)
        {
            return;
        }

        try
        {
            AppendMenuW(menu, MF_STRING, MenuOpen, "打开希沃助手");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, MenuTogglePrivacy, "开关摄像头/麦克风监控");
            AppendMenuW(menu, MF_STRING, MenuToggleCamera, "开关虚拟摄像头");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, MenuExit, "退出");

            GetCursorPos(out var cursor);

            // SetForegroundWindow before TrackPopupMenu is required, otherwise the
            // menu does not dismiss when the user clicks elsewhere.
            SetForegroundWindow(_hwnd);

            var command = TrackPopupMenu(
                menu,
                TPM_RIGHTBUTTON | TPM_RETURNCMD,
                cursor.X, cursor.Y,
                0, _hwnd, nint.Zero);

            if (command != 0)
            {
                PostMessageW(_hwnd, WM_COMMAND, command, nint.Zero);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AddOrModify(NIM_DELETE);

        if (_hwnd != nint.Zero)
        {
            DestroyWindow(_hwnd);
        }

        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    // ---------------------------------------------------------------- interop

    private delegate nint WndProcDelegate(nint hWnd, uint message, nint wParam, nint lParam);

    private static readonly nint HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
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
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

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
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint hMenu, int uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(nint hMenu, int uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint hInstance, nint lpIconName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);
}
