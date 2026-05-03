using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyTaskbar.Helpers;
using IOPath = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

// ═══════════════════════════════════════════════════════════════════
// MainWindow.Interop.cs — P/Invoke declarations, structs, delegates
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ── P/Invoke ──────────────────────────────────────────────────────────
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int FlashWindowEx(ref FLASHWINFO pwfi);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")] static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
        [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint dwProcessId);
        const uint ASFW_ANY = 0xFFFFFFFF;
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[] lpList);
        [DllImport("user32.dll")] static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint Flags);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll", CharSet = CharSet.Auto, EntryPoint = "GetClassName")] static extern int GetWindowClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern bool GetCursorInfo(out CURSORINFO pci);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);
        // [GAME-1] Cursor clip — захват/освобождение курсора и детекция FPS
        [DllImport("user32.dll")] static extern bool ClipCursor(ref RECT lpRect);
        [DllImport("user32.dll")] static extern bool ClipCursor(IntPtr lpRect);
        [DllImport("user32.dll")] static extern bool GetClipCursor(out RECT lpRect);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);

        // [GAME-RAW] Raw Input — перехват мыши у игры при показе панели
        [DllImport("user32.dll")] static extern bool RegisterRawInputDevices([MarshalAs(UnmanagedType.LPArray)] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }
        const uint RIDEV_REMOVE = 0x00000001;
        const uint RIDEV_CAPTUREMOUSE = 0x00000200;
        const uint RIDEV_NOLEGACY = 0x00000030;

        // [FIX-DRAG2] OLE Drag & Drop форвардинг
        [DllImport("ole32.dll")] static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget pDropTarget);
        [DllImport("ole32.dll")] static extern int RevokeDragDrop(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT Point);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        // [STAB-1] SendMessageTimeout — не вешает UI на зависших окнах
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
        const uint SMTO_ABORTIFHUNG = 0x0002;
        const uint SMTO_BLOCK = 0x0001;

        // [STAB-2] IsHungAppWindow — пропускаем зависшие окна в EnumWindows
        [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr hWnd);

        // [STAB-5] WTS — уведомления блокировки экрана
        [DllImport("wtsapi32.dll")] static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);
        [DllImport("wtsapi32.dll")] static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
        const uint NOTIFY_FOR_THIS_SESSION = 0;
        const int WM_WTSSESSION_CHANGE = 0x02B1;
        const int WTS_SESSION_LOCK = 0x7;
        const int WTS_REMOTE_DISCONNECT = 0x4;
        const int WTS_CONSOLE_DISCONNECT = 0x2;

        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr phThumbnailId);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProps);
        [DllImport("dwmapi.dll")] static extern int DwmIsCompositionEnabled(out bool pfEnabled);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute); // [FIX-GHOST-v3]

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        // [ATTENTION] Shell hook для получения HSHELL_FLASH
        [DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool DeregisterShellHookWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern uint RegisterWindowMessage(string lpString);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
        static extern uint GetClassLong32(IntPtr hWnd, int nIndex);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        const uint SHGFI_ICON = 0x000000100;
        const uint SHGFI_LARGEICON = 0x000000000;
        const uint SHGFI_SMALLICON = 0x000000001;
        const uint SIIGBF_BIGGERSIZEOK = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        struct BLUETOOTH_FIND_RADIO_PARAMS { public uint dwSize; }
        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr phRadio);
        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern bool BluetoothFindRadioClose(IntPtr hFind);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
        const int CURSOR_SHOWING = 0x00000001;

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }

        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        struct DWM_THUMBNAIL_PROPERTIES { public uint dwFlags; public RECT rcDestination; public RECT rcSource; public byte opacity; public bool fVisible; public bool fSourceClientAreaOnly; }

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MONITORINFO { public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);

        // [FIX-TASKMGR-RESTORE] GetWindowPlacement работает для привилегированных окон,
        // в отличие от IsIconic который может возвращать false для taskmgr после SC_MINIMIZE.
        [StructLayout(LayoutKind.Sequential)]
        struct POINT_WP { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential)]
        struct RECT_WP { public int left; public int top; public int right; public int bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPLACEMENT
        {
            public uint length;
            public uint flags;
            public uint showCmd;
            public POINT_WP ptMinPosition;
            public POINT_WP ptMaxPosition;
            public RECT_WP rcNormalPosition;
        }
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        const uint GA_ROOT = 2;

        // Возвращает true если окно свёрнуто — работает и для привилегированных процессов
        static bool IsWindowMinimized(IntPtr hwnd)
        {
            var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
            if (!GetWindowPlacement(hwnd, ref wp)) return false;
            return wp.showCmd == 2; // SW_SHOWMINIMIZED
        }

        static IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex)
        {
            try { return IntPtr.Size == 8 ? GetClassLongPtr64(hWnd, nIndex) : new IntPtr((long)GetClassLong32(hWnd, nIndex)); }
            catch { return IntPtr.Zero; }
        }

        // ── Константы ─────────────────────────────────────────────────────────
        const uint DWM_TNP_RECTDESTINATION = 0x00000001;
        const uint DWM_TNP_VISIBLE = 0x00000008;
        const uint DWM_TNP_OPACITY = 0x00000004;

        const int GWL_EXSTYLE = -20;
        const int GWL_STYLE = -16;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_APPWINDOW = 0x00040000;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int DWMWA_CLOAKED = 14; // [FIX-GHOST-v3] DWM cloaking: 0=visible, 1=app, 2=shell(ghost UWP), 4=inherited
        const int WS_EX_LAYERED = 0x00080000;  // [FIX-DRAG] нужен для корректной работы HTTRANSPARENT
        const int WS_EX_TRANSPARENT = 0x00000020;  // [FIX-DRAG] резерв (не применяем глобально)
        const int WM_NCHITTEST = 0x0084;       // [FIX-DRAG]
        const int HTTRANSPARENT = -1;            // [FIX-DRAG] пропустить событие сквозь окно
        const int WS_CHILD = 0x40000000;
        const int SW_RESTORE = 9;
        const int SW_MINIMIZE = 6;
        const int SW_MAXIMIZE = 3;
        const int SW_SHOW = 5;
        const int SW_SHOWNOACTIVATE = 4;
        const uint SPI_SETWORKAREA = 0x002F;
        const uint WM_CLOSE = 0x0010;
        const uint WM_GETICON = 0x007F;
        const int ICON_BIG = 1;
        const int ICON_SMALL = 0;
        const int ICON_SMALL2 = 2;
        const int GCL_HICON = -14;
        const int GCL_HICONSM = -34;
        const byte VK_MENU = 0x12;
        const byte VK_SHIFT = 0x10;
        const int VK_CONTROL = 0x11;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const byte VK_ESCAPE = 0x1B;

        const int WH_KEYBOARD_LL = 13;
        const int WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_RBUTTONDOWN = 0x0204;
        const int WM_MBUTTONDOWN = 0x0207;
        // WinEvent constants — для мгновенного обнаружения перезапуска Explorer
        const uint EVENT_SYSTEM_FOREGROUND = 0x0003;  // [FIX-TASKMGR-FOCUS] смена foreground-окна
        const uint EVENT_OBJECT_CREATE = 0x8000;
        const uint EVENT_OBJECT_SHOW = 0x8002;
        const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_KEYUP = 0x0101;
        const int WM_SYSKEYUP = 0x0105;
        const int VK_LWIN = 0x5B;
        const int VK_RWIN = 0x5C;

        // [DPI-AUTO] TASKBAR_HEIGHT вычисляется динамически из DPI.
        // Базовый размер 40px при 100% (96 dpi). При изменении DPI пересчитывается.
        int TASKBAR_HEIGHT => (int)Math.Round(40.0 * _currentDpiScale * _uiScale);
        double _currentDpiScale = 1.0; // текущий масштаб (1.0 = 100%, 1.25 = 125% и т.д.)

        // [UI-SCALE] Масштаб всего UI: 1.0=100% ... 1.5=150%.
        // Shift+Alt+B  → +10%, Shift+Alt+L → -10% (диапазон 100%-150%).
        double _uiScale = 1.0;

        const int PREVIEW_CARD_WIDTH = 240;
        const int PREVIEW_THUMB_HEIGHT = 135;
        const int PREVIEW_TITLE_HEIGHT = 28;
        const int PREVIEW_PADDING = 8;
        const int PREVIEW_GAP = 4;

        const double ANIM_MS = 80;
        const double BTN_BAR_EXPAND_MS = 100;
        const double BTN_SLIDE_IN_MS = 160;
        const double BTN_SLIDE_OUT_MS = 120;
        const double BTN_BAR_SHRINK_MS = 90;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;

    }
}
