using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FSRestore.Services;

/// <summary>
/// Tray icon via Shell_NotifyIcon + a hidden message-only window that
/// receives tray callbacks. Replaces WinForms NotifyIcon so the app
/// doesn't drag the whole System.Windows.Forms assembly into memory.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifShowTip = 0x00000080;
    private const uint WmUser = 0x0400;
    private const uint WmTrayIcon = WmUser + 1;
    private const uint WmShowMe = 0x0402; // WM_USER + 2, matches App.xaml.cs
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmCommand = 0x0111;
    private const uint WmDestroy = 0x0002;
    private const int MenuOpen = 1;
    private const int MenuClose = 2;

    private HwndSource? _msgWindow;
    private IntPtr _hwnd;
    private bool _added;
    private readonly Action _onOpen;
    private readonly Action _onExit;
    private IntPtr _icon;

    public TrayIconService(string tooltip, Action onOpen, Action onExit)
    {
        _onOpen = onOpen;
        _onExit = onExit;
        CreateMessageWindow(tooltip);
    }

    private void CreateMessageWindow(string tooltip)
    {
        // Message-only window: invisible, just a WndProc sink.
        var parameters = new HwndSourceParameters("FSRestore_TrayWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };

        _msgWindow = new HwndSource(parameters);
        _msgWindow.AddHook(WndProc);
        _hwnd = _msgWindow.Handle;

        var ico = LoadIcon();
        if (ico != IntPtr.Zero)
        {
            _icon = ico;
        }

        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip | NifShowTip,
            uCallbackMessage = WmTrayIcon,
            hIcon = _icon != IntPtr.Zero ? _icon : LoadSystemIcon(),
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip,
            uVersion = 4,
        };

        _added = Shell_NotifyIcon(NimAdd, ref data);
    }

    private IntPtr LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                var h = ExtractIcon(IntPtr.Zero, exe, 0);
                return h;
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    private static IntPtr LoadSystemIcon()
    {
        // IDI_APPLICATION
        return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmTrayIcon)
        {
            var mouse = (uint)lParam.ToInt64() & 0xFFFF;
            if (mouse == WmLButtonUp)
            {
                _onOpen();
                handled = true;
            }
            else if (mouse == WmRButtonUp)
            {
                ShowContextMenu();
                handled = true;
            }
        }
        else if (msg == WmCommand)
        {
            var id = wParam.ToInt32() & 0xFFFF;
            if (id == MenuOpen)
            {
                _onOpen();
                handled = true;
            }
            else if (id == MenuClose)
            {
                _onExit();
                handled = true;
            }
        }
        else if (msg == WmShowMe)
        {
            // Another instance launched and wants this one to come forward.
            _onOpen();
            handled = true;
        }
        else if (msg == WmDestroy)
        {
            RemoveIcon();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        AppendMenu(menu, 0, MenuOpen, "Open App");
        AppendMenu(menu, 0, MenuClose, "Close");

        // Required: set our window foreground so the menu dismisses on click-away.
        SetForegroundWindow(_hwnd);
        GetCursorPos(out var pt);
        TrackPopupMenu(menu, 0x0002, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_added)
        {
            return;
        }

        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifShowTip | 0x00000010, // NIF_INFO
            szInfoTitle = title,
            szInfo = text,
            dwInfoFlags = 1, // NIIF_INFO
        };
        Shell_NotifyIcon(NimModify, ref data);
    }

    private void RemoveIcon()
    {
        if (!_added)
        {
            return;
        }

        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
        };
        Shell_NotifyIcon(NimDelete, ref data);
        _added = false;
    }

    public void Dispose()
    {
        RemoveIcon();
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }
        _msgWindow?.Dispose();
    }

    // ---------- interop ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, uint nIconIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
}
