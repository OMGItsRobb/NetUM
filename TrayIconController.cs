using System.Runtime.InteropServices;

namespace NetUM;

internal sealed class TrayIconController : IDisposable
{
    private const int GwlWndProc = -4;
    private const int SwHide = 0;
    private const int SwRestore = 9;

    private const uint WmClose = 0x0010;
    private const uint WmApp = 0x8000;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;

    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NotifyIconVersion4 = 4;

    private const uint MfString = 0x00000000;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmBottomAlign = 0x0020;
    private const uint TpmReturnCommand = 0x0100;

    private const int CommandOpen = 1001;
    private const int CommandExit = 1002;
    private const uint TrayCallbackMessage = WmApp + 1;
    private static readonly nint DefaultApplicationIcon = (nint)0x7F00;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x00000010;
    private const uint DefaultSize = 0x00000040;

    private readonly nint _windowHandle;
    private readonly Action _onOpenRequested;
    private readonly Action _onExitRequested;
    private readonly WindowProcedure _windowProcedure;

    private nint _originalWindowProcedure;
    private nint _iconHandle;
    private bool _ownsIconHandle;
    private bool _allowClose;
    private bool _iconAdded;
    private bool _disposed;

    public static string GetIconPath()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "NetUM.ico");
        return File.Exists(iconPath) ? iconPath : string.Empty;
    }

    public TrayIconController(nint windowHandle, Action onOpenRequested, Action onExitRequested)
    {
        _windowHandle = windowHandle;
        _onOpenRequested = onOpenRequested;
        _onExitRequested = onExitRequested;
        _windowProcedure = WindowProcedureCallback;

        HookWindowProcedure();
        AddTrayIcon();
    }

    public void RequestExit()
    {
        _allowClose = true;
    }

    public void RestoreWindow()
    {
        ShowWindow(_windowHandle, SwRestore);
        SetForegroundWindow(_windowHandle);
        _onOpenRequested();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        RemoveTrayIcon();
        UnhookWindowProcedure();

        if (_ownsIconHandle && _iconHandle != 0)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = 0;
        }
    }

    private void HookWindowProcedure()
    {
        _originalWindowProcedure = SetWindowLongPtr(
            _windowHandle,
            GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProcedure));
    }

    private void UnhookWindowProcedure()
    {
        if (_originalWindowProcedure == 0)
            return;

        SetWindowLongPtr(_windowHandle, GwlWndProc, _originalWindowProcedure);
        _originalWindowProcedure = 0;
    }

    private nint WindowProcedureCallback(nint hWnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmClose && !_allowClose)
        {
            ShowWindow(_windowHandle, SwHide);
            return 0;
        }

        if (message == TrayCallbackMessage)
        {
            var trayEvent = LowWord(lParam);
            if (trayEvent is WmLButtonUp or WmLButtonDoubleClick or NinSelect or NinKeySelect)
            {
                RestoreWindow();
                return 0;
            }

            if (trayEvent is WmRButtonUp or WmContextMenu)
            {
                ShowContextMenu();
                return 0;
            }
        }

        return CallWindowProc(_originalWindowProcedure, hWnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
            return;

        try
        {
            AppendMenu(menu, MfString, CommandOpen, "Open");
            AppendMenu(menu, MfString, CommandExit, "Exit");

            GetCursorPos(out var cursorPosition);
            SetForegroundWindow(_windowHandle);

            var command = TrackPopupMenu(
                menu,
                TpmLeftAlign | TpmBottomAlign | TpmRightButton | TpmReturnCommand,
                cursorPosition.X,
                cursorPosition.Y,
                0,
                _windowHandle,
                nint.Zero);

            switch ((int)command)
            {
                case CommandOpen:
                    RestoreWindow();
                    break;
                case CommandExit:
                    _onExitRequested();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void AddTrayIcon()
    {
        var data = CreateNotifyIconData();
        if (!Shell_NotifyIcon(NimAdd, ref data))
            return;

        _iconAdded = true;
        data.uVersionOrTimeout = NotifyIconVersion4;
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    private void RemoveTrayIcon()
    {
        if (!_iconAdded)
            return;

        var data = CreateNotifyIconData();
        Shell_NotifyIcon(NimDelete, ref data);
        _iconAdded = false;
    }

    private NOTIFYICONDATA CreateNotifyIconData()
    {
        if (_iconHandle == 0)
            _iconHandle = LoadTrayIconHandle(out _ownsIconHandle);

        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _windowHandle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _iconHandle,
            szTip = "NetUM"
        };
    }

    private static uint LowWord(nint value)
    {
        return unchecked((uint)(value.ToInt64() & 0xFFFF));
    }

    private static nint LoadTrayIconHandle(out bool ownsIconHandle)
    {
        var iconPath = GetIconPath();
        if (!string.IsNullOrEmpty(iconPath))
        {
            var iconHandle = LoadImage(
                nint.Zero,
                iconPath,
                ImageIcon,
                0,
                0,
                LoadFromFile | DefaultSize);

            if (iconHandle != 0)
            {
                ownsIconHandle = true;
                return iconHandle;
            }
        }

        ownsIconHandle = false;
        return LoadIcon(nint.Zero, DefaultApplicationIcon);
    }

    private static nint SetWindowLongPtr(nint hWnd, int index, nint newProcedure)
    {
        if (nint.Size == 8)
            return SetWindowLongPtr64(hWnd, index, newProcedure);

        return SetWindowLong32(hWnd, index, newProcedure.ToInt32());
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")]
    private static extern nint LoadImage(
        nint hInst,
        string name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern nint SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(
        nint lpPrevWndFunc,
        nint hWnd,
        uint msg,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint TrackPopupMenu(
        nint hMenu,
        uint uFlags,
        int x,
        int y,
        int nReserved,
        nint hWnd,
        nint prcRect);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint hWnd, uint message, nint wParam, nint lParam);
}