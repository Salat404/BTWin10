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

// ═══════════════════════════════════════════════════════════════════════════════
// CHANGELOG:
// [FIX-1]  IgnoredProcesses: "calculator", "systemsettings", "applicationframehost"
// [FIX-2]  ResolveUwpAppName: только точные совпадения (==) и StartsWith с разделителем
// [FIX-3]  TryCloseSystemStartMenu: WM_KEYDOWN/VK_ESCAPE вместо WM_CLOSE
// [FIX-4]  Win10 acrylic: проверка билда >= 18362
// [FIX-5]  GetBestIcon: полная иерархия источников иконок
// [FIX-6]  UWP дедупликация: защита от IntPtr.Zero
// [FIX-7]  Watchdog: восстановление системного таскбара при аварийном выходе
// [FIX-8]  WM_POWERBROADCAST: обработка выхода из сна
// [FIX-9]  NEW_GROUP_CONFIRM_MS: задержка подтверждения новой группы
//
// [STAB-1] SendMessage → SendMessageTimeout (200ms, SMTO_ABORTIFHUNG)
// [STAB-2] EnumWindows: флаг реентрантности + IsHungAppWindow
// [STAB-3] DwmRegisterThumbnail: IsWindow + IsHungAppWindow перед вызовом
// [STAB-4] SafeGetWindowText: через SendMessageTimeout
// [STAB-5] WM_WTSSESSION_CHANGE: блокировка экрана = как sleep
// [STAB-6] Флаги реентрантности для каждого таймера (Interlocked)
// [STAB-7] Shell-иконки асинхронно в Task.Run с CancellationToken 500ms
// [STAB-8] SafeGetProcessName: полная обработка всех исключений + HasExited
// [STAB-9] _thumbnailHandles: lock при Add/Clear
// [STAB-10] ApplyBluetoothState: Dispatcher.CheckAccess + IsLoaded
// [STAB-11] IsWindow-проверка перед GetWindowRect/GetWindowLong
// [STAB-12] ConcurrentDictionary для PID-кэшей, ограничение 256 записей
// [STAB-13] PostMessage: IsWindow-проверка перед отправкой WM_CLOSE
// [STAB-14] PositionTaskbar: IsLoaded-guard
// [STAB-15] MainWindow_Closed: каждый таймер в отдельном try/catch
// [FIX-GHOST-v2] WS_EX_NOACTIVATE: ghost-фильтр для calculator/systemsettings вместо IgnoredProcesses
// [FIX-GHOST-v3] DWMWA_CLOAKED: надёжный ghost-фильтр для ВСЕХ UWP-окон (calculator, settings и др.);
//                добавлен DwmGetWindowAttribute + DWMWA_CLOAKED в начало EnumWindows;
//                "calculatorapp" добавлен в IsUwpAppName(); resume-блок 4s→8s
// [FIX-DRAG] NcHitTestHook + WS_EX_LAYERED: панель больше не мешает drag&drop других программ
// [GAME-1]  ClipCursor/GetClipCursor/GetSystemMetrics P/Invoke для детекции FPS-захвата курсора
// [GAME-2]  FullscreenBlockerWindow: прозрачный полноэкранный оверлей при fullscreen-режиме
// [GAME-3]  OnWinKeyDown в fullscreen: только ShowTaskbar, без ShowMenu (меню Пуск не открывается)
// [GAME-4]  CheckEdgeReveal: ShowFullscreenBlocker при FPS-режиме
// [GAME-5]  HideTaskbar: снимает блокер вместе с панелью
// [GAME-6]  ShowFullscreenBlocker: тулбар поднимается выше блокера (Topmost toggle)
// [GAME-7]  FullscreenBlockerWindow: дырка только для тулбара (HTTRANSPARENT)
// [GAME-8]  MenuVisibilityChanged: блокер прячется при открытии меню, возвращается при закрытии
// [GAME-9]  SuspendBlockerForAppWindow: блокер снимается при открытии окна из панели/меню Пуск;
//           CheckFullscreen восстанавливает блокер только когда игра снова получает фокус
// [FIX-TASKMGR] SafeGetProcessName: fallback через PROCESS_QUERY_LIMITED_INFORMATION
//           (TaskmgrWatcher.GetProcessNameByPid) при Win32Exception — позволяет видеть
//           taskmgr и другие привилегированные процессы без прав администратора.
//           Новый файл: Helpers/TaskmgrWatcher.cs
// [FIX-TASKMGR-STARTMENU] OnWinKeyDown + TryCloseSystemStartMenuDelayed:
//           При диспетчере задач в фокусе (High IL) Windows Shell открывает оригинальный
//           Пуск через внутренний IPC-канал (~80-150мс после Win-key), в обход LowLevel
//           keyboard hook. TryCloseSystemStartMenu() вызванный до ShowMenu() не помогает —
//           Пуск ещё не открылся. Добавлена IsTaskmgrForeground() (проверка по window class
//           "TaskManagerWindow") и TryCloseSystemStartMenuDelayed(180мс): после ShowMenu()
//           ждём 180мс и закрываем Пуск повторно — к этому моменту он уже открылся и
//           гарантированно закроется. Своё меню остаётся единственным видимым.
// ═══════════════════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    // [FIX-DRAG2] POINT на уровне namespace для использования в IDropTarget
    [StructLayout(LayoutKind.Sequential)]
    struct NsPoint { public int x, y; }

    // [FIX-DRAG2] COM-интерфейс IDropTarget для перехвата OLE drag&drop
    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTarget
    {
        [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] object pDataObj, uint grfKeyState, NsPoint pt, ref uint pdwEffect);
        [PreserveSig] int DragOver(uint grfKeyState, NsPoint pt, ref uint pdwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] object pDataObj, uint grfKeyState, NsPoint pt, ref uint pdwEffect);
    }

    // [FIX-DRAG2] Пустой IDropTarget — Windows видит что окно участвует в OLE drop,
    // но мы возвращаем DROPEFFECT_NONE, что позволяет дропу пройти к окну под нами.
    class PassthroughDropTarget : IDropTarget
    {
        public int DragEnter(object pDataObj, uint grfKeyState, NsPoint pt, ref uint pdwEffect)
        { pdwEffect = 0; return 0; }
        public int DragOver(uint grfKeyState, NsPoint pt, ref uint pdwEffect)
        { pdwEffect = 0; return 0; }
        public int DragLeave() { return 0; }
        public int Drop(object pDataObj, uint grfKeyState, NsPoint pt, ref uint pdwEffect)
        { pdwEffect = 0; return 0; }
    }

    public partial class MainWindow : Window
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
        const uint RIDEV_REMOVE       = 0x00000001;
        const uint RIDEV_CAPTUREMOUSE = 0x00000200;
        const uint RIDEV_NOLEGACY     = 0x00000030;

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
        const int WH_MOUSE_LL    = 14;
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

        static readonly bool IsWin10_1903Plus =
            Environment.OSVersion.Version.Major == 10 &&
            Environment.OSVersion.Version.Build >= 18362;

        static readonly HashSet<string> DesktopWindowClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Progman","WorkerW","Shell_TrayWnd","Shell_SecondaryTrayWnd",
            "DV2ControlHost","SideBar_AppBarWindow",
        };

        // Window-классы которые никогда не должны появляться в таскбаре.
        // Это служебные/proxy/overlay окна создаваемые рендер-движками, Steam,
        // DXVK, VR-рантаймами и т.п. — они не являются окнами приложения для
        // пользователя. Фильтрация по class надёжнее чем по имени процесса.
        static readonly HashSet<string> IgnoredWindowClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Direct3D / DXVK / Vulkan proxy-окна (TF2, CS2, Proton-игры)
            "D3DProxyWindow",
            // Steam GameOverlay (оверлей Steam в играх)
            "GameOverlayWindow", "ValveGameOverlayWindow",
            // NVIDIA/AMD overlay
            "CEF-OSC-WIDGET",
            // Epic/Battle.net overlay
            "EpicGamesOverlay",
            // Reshade / ENB proxy
            "ReShade",
            // OpenVR / SteamVR compositor
            "VROverlay",
        };

        static readonly HashSet<string> HelperExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "steamwebhelper","steamservice","steam_osx","gameoverlayui",
            "steamlauncher","steamerrorreporter","steamerrorreporter64",
            "crashpad_handler","crashhandler","cef_helper",
            "discordcrashhandler","chrome_crashpad_handler",
        };

        static readonly Dictionary<string, string> ProcessAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"steamwebhelper","steam"},{"gameoverlayui","steam"},{"steamlauncher","steam"},
        };

        static readonly HashSet<string> DirectUwpProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "photos","microsoft.photos","windowscamera","video.ui","maps",
            "microsoft.bingweather","hxoutlook","hxcalendarappimm",
        };

        // ── Кэши [STAB-12] ConcurrentDictionary вместо Dictionary ─────────────
        readonly Dictionary<string, BitmapSource> _iconCache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        // [ICON-HASH] Для per-window процессов: хэш иконки → groupKey существующей группы
        readonly Dictionary<string, string> _iconHashToGroupKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<uint, string> _pidNameCache = new ConcurrentDictionary<uint, string>();
        readonly ConcurrentDictionary<uint, string> _pidPathCache = new ConcurrentDictionary<uint, string>();
        readonly StringBuilder _sbTitle = new StringBuilder(512);
        string _lastLang = "";

        // ── Кисти ─────────────────────────────────────────────────────────────
        static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        static readonly SolidColorBrush BrushTransparent = Frozen(Color.FromArgb(0, 0, 0, 0));
        static readonly SolidColorBrush BrushActiveBg = Frozen(Color.FromArgb(60, 255, 255, 255));
        static readonly SolidColorBrush BrushActiveBar = Frozen(Color.FromRgb(100, 200, 255));
        static readonly SolidColorBrush BrushRunningBar = Frozen(Color.FromRgb(140, 140, 140));
        // [ATTENTION] Win10-style: ярко-оранжевая пипка (pip) при мигании
        static readonly SolidColorBrush BrushFlashPip  = Frozen(Color.FromRgb(255, 140, 0));    // ярко-оранжевый RunBar
        // [ATTENTION] Фон кнопки при мигании (on-фаза) — чуть ярче чтобы была видна пульсация
        static readonly SolidColorBrush BrushFlashOn   = Frozen(Color.FromArgb(120, 255, 140, 0));  // оранжевый 47% on
        // [ATTENTION] Фон кнопки в статичном состоянии (мигание прошло, но внимание ещё нужно)
        static readonly SolidColorBrush BrushAttentionBg = Frozen(Color.FromArgb(89, 255, 140, 0)); // оранжевый 35%
        static readonly SolidColorBrush BrushCloseHover = Frozen(Color.FromArgb(200, 100, 100, 100));

        // ── Позиция панели (верх / низ) ───────────────────────────────────────
        bool _isBottom = false;
        System.Windows.Forms.NotifyIcon _appNotifyIcon;

        // ── Settings window (Shift+Alt+O) ─────────────────────────────────────
        SettingsWindow _settingsWindow;
        bool _fullscreenAutoHide = true; // auto-hide taskbar in fullscreen games/apps

        // ── Дочерние окна ─────────────────────────────────────────────────────
        MenuWindow _menuWindow;
        TrayWindow _trayWindow;
        WifiWindow _wifiWindow;
        // [GAME-2] Прозрачный блокер на весь экран — удерживает курсор пока панель видна
        FullscreenBlockerWindow _blockerWindow;

        // ── Таймеры ───────────────────────────────────────────────────────────
        DispatcherTimer _clockTimer, _activeTimer, _langTimer, _batteryTimer,
                        _wifiIconTimer, _taskbarWatcher, _fullscreenTimer,
                        _volumeIconTimer, _brightnessIconTimer,
                        _edgeRevealTimer, _bluetoothTimer,
                        _hookWatchdog; // [FIX-HOOK-WATCHDOG]

        // ── Группы приложений ─────────────────────────────────────────────────
        readonly Dictionary<string, AppGroup> _groups = new Dictionary<string, AppGroup>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<Button> _removingButtons = new HashSet<Button>();

        // ── Preview ───────────────────────────────────────────────────────────
        Window _previewWindow;
        readonly List<IntPtr> _thumbnailHandles = new List<IntPtr>();
        readonly object _thumbnailLock = new object(); // [STAB-9]
        DispatcherTimer _previewHideTimer;
        DispatcherTimer _previewShowTimer;
        AppGroup _currentPreviewGroup;
        AppGroup _pendingPreviewGroup;
        Button _pendingPreviewBtn;

        // ── Flash ─────────────────────────────────────────────────────────────
        readonly HashSet<Button> _flashingButtons = new HashSet<Button>();
        DispatcherTimer _flashTimer;
        bool _flashState;
        // [ATTENTION] Группы, у которых NeedsAttention=true (мигание уже закончилось, фон статичный)
        readonly HashSet<AppGroup> _attentionGroups = new HashSet<AppGroup>();
        // [ATTENTION] Shell hook для HSHELL_FLASH
        IntPtr _shellHookWindow = IntPtr.Zero;
        uint   _wmShellHook     = 0;

        IntPtr _attentionEventHook = IntPtr.Zero;

        // ── Прочее ────────────────────────────────────────────────────────────
        IntPtr _lastForegroundWindow = IntPtr.Zero;
        readonly string ShortcutsFolder;

        IntPtr _keyboardHook = IntPtr.Zero;
        // [EXP-RESTART] WinEvent хук — мгновенная реакция при пересоздании Shell_TrayWnd
        IntPtr _winEventHook = IntPtr.Zero;
        WinEventDelegate _winEventDelegate; // ОБЯЗАТЕЛЬНО хранить ссылку, иначе GC уберёт
        bool _desktopRefreshDone = false;   // [DESKTOP-REFRESH] однократный refresh при первом старте Explorer
        LowLevelKeyboardProc _keyboardProc;
        IntPtr _attentionMouseHook = IntPtr.Zero;  // [ATTENTION-HIDE] активен только в noActivate-режиме
        LowLevelMouseProc _attentionMouseProc;
        bool _shownByAttention = false;            // панель показана из-за attention во время fullscreen

        volatile bool _winKeyDown;
        volatile bool _winUsedInCombo;
        volatile bool _shiftDown;
        volatile bool _ctrlDown;
        volatile bool _altDown;
        volatile bool _injectingWin;

        bool _isHiddenByFullscreen = false;
        bool _taskbarAnimating = false;


        // [GAME-9] Флаг: пользователь открыл/переключился на окно поверх fullscreen-игры.
        // Пока этот флаг стоит — блокер не возвращается (игра ещё не в фокусе).
        bool _appOpenedOverFullscreen = false;

        IntPtr _menuOpenedOverHwnd = IntPtr.Zero;
        IntPtr _lastFullscreenHwnd = IntPtr.Zero;
        DateTime _ownWindowActivityAt = DateTime.MinValue;
        const int OWN_ACTIVITY_GRACE_MS = 1500;
        int _fullscreenConfirmCount = 0;
        const int FULLSCREEN_CONFIRM_TICKS = 2;
        int _notFullscreenConfirmCount = 0;
        const int NOT_FULLSCREEN_CONFIRM_TICKS = 3;

        int _cachedWifiSignal = -1;
        bool _wifiSignalPending = false;

        VolumeFlyoutWindow _volumeFlyout;
        BrightnessFlyoutWindow _brightnessFlyout;

        DateTime _edgeCursorEnteredAt = DateTime.MinValue;
        bool _edgeRevealPending = false;

        const int BT_SENTINEL = -99;
        int _btLastStateInt = BT_SENTINEL;

        volatile bool _resumingFromSleep = false;
        DispatcherTimer _resumeBlockTimer;

        const int WM_POWERBROADCAST = 0x0218;
        const int WM_DPICHANGED    = 0x02E0;  // [DPI-AUTO] Windows посылает при смене DPI/масштаба
        const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        const int PBT_APMRESUMESUSPEND = 0x0007;

        readonly Dictionary<string, DateTime> _pendingNewGroups =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        const int NEW_GROUP_CONFIRM_MS = 0;

        // [STAB-6] Флаги реентрантности таймеров (Interlocked)
        volatile int _enumBusyInt = 0;
        volatile int _btnStateBusyInt = 0;
        volatile int _batteryBusyInt = 0;
        volatile int _btBusyInt = 0;
        volatile int _fsBusyInt = 0;

        // ── Словарь кэша состояний кнопок ─────────────────────────────────────
        readonly Dictionary<Button, string> _buttonStateCache = new Dictionary<Button, string>();

        // ── AppGroup ──────────────────────────────────────────────────────────
        class AppGroup
        {
            public string ExeName;
            public string GroupKey;   // [PER-WINDOW] ключ в _groups: для per-window = "exeName:hwnd", иначе = exeName
            public Button Button;
            public List<IntPtr> Hwnds = new List<IntPtr>();
            public string LastTitle;
            public string PinnedTooltip; // базовое имя для сброса тултипа когда окна закрыты
            public BitmapSource Icon;
            public bool IsPinned;
            public string LaunchPath;
            public int PinOrder = int.MaxValue;
            public bool IsUwp;
            public string IconHash;   // [ICON-HASH] хэш иконки для группировки per-window по профилю
            // [ATTENTION] Win10-style attention indication
            public bool  NeedsAttention;      // true = окно запросило внимание (FlashWindow)
            public DateTime FlashStartTime;   // момент начала мигания
        }

        // [PER-WINDOW] Браузеры/приложения, у которых каждое окно (профиль) получает
        // отдельную кнопку на панели задач, как в оригинальном Win10.
        static readonly HashSet<string> PerWindowProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "brave", "opera", "vivaldi", "firefox",
            "waterfox", "librewolf", "chromium", "iexplore", "safari",
        };

        // ═════════════════════════════════════════════════════════════════════
        // КОНСТРУКТОР / ЗАГРУЗКА
        // ═════════════════════════════════════════════════════════════════════
        public MainWindow()
        {
            InitializeComponent();
            ShortcutsFolder = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Shortcuts");
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;

            // [FIX-7] Watchdog
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { TaskbarHelper.Show(); } catch { }
                try
                {
                    double sw = SystemParameters.PrimaryScreenWidth;
                    double sh = SystemParameters.PrimaryScreenHeight;
                    RECT wa = new RECT { left = 0, top = 0, right = (int)sw, bottom = (int)sh };
                    SystemParametersInfo(SPI_SETWORKAREA, 0, ref wa, 0x01 | 0x02);
                }
                catch { }
            };
        }

        void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Shortcuts folder creation removed — folder is not used by the app

            // [FAST-START] Скрываем системный таскбар ПЕРВЫМ — до загрузки иконок
            SafeRun(() => TaskbarHelper.Hide(), "TaskbarHelper.Hide");

            SafeRun(LoadPinnedApps, "LoadPinnedApps");
            SafeRun(LoadShortcuts, "LoadShortcuts");
            SafeRun(LoadPosition, "LoadPosition");   // загружаем позицию до PositionTaskbar
            SafeRun(LoadFullscreenAutoHide, "LoadFullscreenAutoHide");
            // [DPI-AUTO] Читаем начальный масштаб до первого PositionTaskbar
            try { var src0 = PresentationSource.FromVisual(this); _currentDpiScale = src0?.CompositionTarget?.TransformToDevice.M11 ?? 1.0; } catch { }
            // [UI-SCALE] Применяем сохранённый масштаб до первого PositionTaskbar
            if (Math.Abs(_uiScale - 1.0) > 0.01) MainPanel.LayoutTransform = new ScaleTransform(_uiScale, _uiScale);
            SafeRun(PositionTaskbar, "PositionTaskbar");
            SafeRun(ApplyAcrylicBackground, "ApplyAcrylicBackground");
            SafeRun(ReserveScreenSpace, "ReserveScreenSpace");

            try { EnsureMenuWindow(); }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] MenuWindow: {ex.Message}"); }

            SafeRun(InitPreviewWindow, "InitPreviewWindow");
            SafeRun(StartClock, "StartClock");
            SafeRun(StartFlashTimer, "StartFlashTimer");
            SafeRun(StartActiveWindowTracker, "StartActiveWindowTracker");
            SafeRun(StartLangTracker, "StartLangTracker");
            SafeRun(StartBatteryTracker, "StartBatteryTracker");
            SafeRun(StartWifiIconTracker, "StartWifiIconTracker");
            SafeRun(StartVolumeIconTracker, "StartVolumeIconTracker");
            SafeRun(StartBrightnessIconTracker, "StartBrightnessIconTracker");
            SafeRun(StartBluetoothTracker, "StartBluetoothTracker");
            SafeRun(StartTaskbarWatcher, "StartTaskbarWatcher");
            SafeRun(() => TaskmgrWatcher.StartFocusGuard(Dispatcher), "TaskmgrWatcher.StartFocusGuard"); // [FIX-TASKMGR-FOCUS]
            SafeRun(InstallKeyboardHook, "InstallKeyboardHook");
            SafeRun(StartFullscreenWatcher, "StartFullscreenWatcher");
            SafeRun(StartEdgeRevealWatcher, "StartEdgeRevealWatcher");
            SafeRun(HookPowerEvents, "HookPowerEvents");
            SafeRun(RegisterShellAttentionHook, "RegisterShellAttentionHook"); // [ATTENTION]
            SafeRun(ApplyLayeredStyle, "ApplyLayeredStyle"); // [FIX-DRAG]
            SafeRun(RegisterPassthroughDrop, "RegisterPassthroughDrop"); // [FIX-DRAG2]
            SafeRun(InitNotifyIcon, "InitNotifyIcon"); // Иконка в системном трее
        }

        static void SafeRun(Action a, string n = "")
        { try { a(); } catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] {n}: {ex}"); } }

        // ═════════════════════════════════════════════════════════════════════
        // [STAB-1] SafeSendMessage — не вешает UI на зависших окнах
        // ═════════════════════════════════════════════════════════════════════
        static IntPtr SafeSendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint timeoutMs = 200)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return IntPtr.Zero;
            try { if (IsHungAppWindow(hWnd)) return IntPtr.Zero; } catch { return IntPtr.Zero; }
            try
            {
                UIntPtr result;
                IntPtr ret = SendMessageTimeout(hWnd, msg, wParam, lParam,
                    SMTO_ABORTIFHUNG | SMTO_BLOCK, timeoutMs, out result);
                return ret == IntPtr.Zero ? IntPtr.Zero : (IntPtr)(long)result.ToUInt64();
            }
            catch { return IntPtr.Zero; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // [STAB-4] SafeGetWindowText — через SendMessageTimeout
        // ═════════════════════════════════════════════════════════════════════
        static string SafeGetWindowText(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return "";
            try { if (IsHungAppWindow(hWnd)) return ""; } catch { return ""; }
            try
            {
                // GetWindowText читает из внутреннего буфера напрямую — безопасен
                int len = GetWindowTextLength(hWnd);
                if (len <= 0 || len > 2048) return "";
                var sb = new StringBuilder(len + 2);
                int actual = GetWindowText(hWnd, sb, len + 1);
                return actual > 0 ? sb.ToString() : "";
            }
            catch { return ""; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // [STAB-8] SafeGetProcessName — полная обработка исключений
        // [FIX-TASKMGR] Fallback через PROCESS_QUERY_LIMITED_INFORMATION
        //   для привилегированных процессов (taskmgr, regedit и др.) —
        //   Process.GetProcessById бросает Win32Exception (Access Denied),
        //   поэтому при ошибке используем TaskmgrWatcher.GetProcessNameByPid,
        //   который открывает процесс с минимальным флагом и не требует прав.
        // ═════════════════════════════════════════════════════════════════════
        string SafeGetProcessName(uint pid)
        {
            try
            {
                var proc = Process.GetProcessById((int)pid);
                if (proc.HasExited) return "";
                string name = proc.ProcessName?.ToLowerInvariant() ?? "";
                if (string.IsNullOrEmpty(name)) return "";
                try
                {
                    string path = proc.MainModule?.FileName ?? "";
                    if (!string.IsNullOrEmpty(path) && _pidPathCache.Count < 256)
                        _pidPathCache.TryAdd(pid, path);
                }
                catch { _pidPathCache.TryAdd(pid, ""); }
                return name;
            }
            catch (ArgumentException) { return ""; }
            catch (InvalidOperationException) { return ""; }
            catch (System.ComponentModel.Win32Exception)
            {
                // [FIX-TASKMGR] Process.GetProcessById не даёт доступ к
                // привилегированным процессам (High/System IL: taskmgr, regedit, tf_win64…).
                // Используем QueryFullProcessImageName с PROCESS_QUERY_LIMITED_INFORMATION —
                // этот флаг разрешён без прав администратора для любого процесса.
                //
                // [FIX-ELEVATED-ICON] Дополнительно сохраняем ПОЛНЫЙ ПУТЬ в _pidPathCache.
                // Без этого у elevated-игр (TF2, CS2 и т.п.) ep всегда пустой →
                // GetBestIcon не может найти exe → иконка не отображается.
                string fullPath = TaskmgrWatcher.GetProcessFullPathByPid(pid);
                if (!string.IsNullOrEmpty(fullPath))
                {
                    if (_pidPathCache.Count < 256)
                        _pidPathCache.TryAdd(pid, fullPath);
                    string nameFromPath = System.IO.Path.GetFileNameWithoutExtension(fullPath)?.ToLowerInvariant() ?? "";
                    if (!string.IsNullOrEmpty(nameFromPath) && _pidNameCache.Count < 256)
                        _pidNameCache.TryAdd(pid, nameFromPath);
                    return nameFromPath;
                }
                // Крайний fallback: хотя бы имя (без пути — иконка через WM_GETICON)
                string fallback = TaskmgrWatcher.GetProcessNameByPid(pid);
                if (!string.IsNullOrEmpty(fallback) && _pidNameCache.Count < 256)
                    _pidNameCache.TryAdd(pid, fallback);
                return fallback;
            }
            catch { return ""; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // ИКОНКИ — ПОЛНАЯ ИЕРАРХИЯ [FIX-5]
        // ═════════════════════════════════════════════════════════════════════
        BitmapSource GetBestIcon(IntPtr hwnd, uint pid, string exePath, int size = 32)
        {
            BitmapSource result = null;

            if (hwnd != IntPtr.Zero && IsWindow(hwnd))
            {
                // [STAB-1] SafeSendMessage вместо SendMessage
                result = TryGetHIcon(SafeSendMessage(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero), size)
                      ?? TryGetHIcon(SafeSendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL2, IntPtr.Zero), size)
                      ?? TryGetHIcon(SafeSendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero), size);
                if (result != null) return result;

                result = TryGetHIcon(GetClassLongPtr(hwnd, GCL_HICON), size)
                      ?? TryGetHIcon(GetClassLongPtr(hwnd, GCL_HICONSM), size);
                if (result != null) return result;
            }

            // [STAB-7] Shell-вызовы только для локальных файлов
            if (!string.IsNullOrEmpty(exePath) && IsLocalPath(exePath))
            {
                result = TryShellItemImage(exePath, size);
                if (result != null) return result;
                result = TrySHGetFileInfo(exePath, size);
                if (result != null) return result;
            }

            if (!string.IsNullOrEmpty(exePath))
            {
                try { result = IconHelper.GetIconFromExe(exePath, size); if (result != null) return result; } catch { }
            }
            if (pid != 0)
            {
                try { result = IconHelper.GetIconFromProcess((int)pid, size); if (result != null) return result; } catch { }
            }
            return null;
        }

        // [STAB-7] Проверка: только локальные диски (не сетевые пути)
        static bool IsLocalPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (path.StartsWith(@"\\")) return false;
                string root = IOPath.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return false;
                var di = new System.IO.DriveInfo(root);
                return di.DriveType == System.IO.DriveType.Fixed
                    || di.DriveType == System.IO.DriveType.Ram;
            }
            catch { return false; }
        }

        static BitmapSource TryGetHIcon(IntPtr hIcon, int targetSize)
        {
            if (hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                if (src == null) return null;
                // [FIX-ICON-DPI] Всегда нормализуем к targetSize x targetSize при 96 dpi.
                // Без этого иконки с нестандартным DPI (72, 120, 144...) рендерятся
                // в неправильных DIU-размерах и выглядят растянутыми / обрезанными.
                BitmapSource result = targetSize > 0 ? ResizeBitmap(src, targetSize, targetSize) : src;
                if (result != null && result.CanFreeze) result.Freeze();
                return result;
            }
            catch { return null; }
        }

        // Вариант для виртуальных путей: shell:AppsFolder\..., ::{GUID} и т.д.
        static BitmapSource TryShellParsingNameImage(string parsingName, int size)
        {
            if (string.IsNullOrEmpty(parsingName)) return null;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out object obj);
                var factory = obj as IShellItemImageFactory;
                if (factory == null) return null;
                var sz = new SIZE { cx = size, cy = size };
                int hr = factory.GetImage(sz, SIIGBF_BIGGERSIZEOK, out IntPtr hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero) return null;
                try
                {
                    var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    if (bmp == null) return null;
                    var result = size > 0 ? ResizeBitmap(bmp, size, size) : bmp;
                    if (result != null && result.CanFreeze) result.Freeze();
                    return result;
                }
                finally { try { DeleteObject(hBitmap); } catch { } }
            }
            catch { return null; }
        }

        static BitmapSource TryShellItemImage(string path, int size)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object obj);
                var factory = obj as IShellItemImageFactory;
                if (factory == null) return null;
                var sz = new SIZE { cx = size, cy = size };
                int hr = factory.GetImage(sz, SIIGBF_BIGGERSIZEOK, out IntPtr hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero) return null;
                try
                {
                    var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    if (bmp == null) return null;
                    var result = size > 0 ? ResizeBitmap(bmp, size, size) : bmp;
                    if (result != null && result.CanFreeze) result.Freeze();
                    return result;
                }
                finally { try { DeleteObject(hBitmap); } catch { } }
            }
            catch { return null; }
        }

        static BitmapSource TrySHGetFileInfo(string path, int size)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var shfi = new SHFILEINFO();
                IntPtr hr = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_ICON | SHGFI_LARGEICON);
                if (hr == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
                try { return TryGetHIcon(shfi.hIcon, size); }
                finally { try { DestroyIcon(shfi.hIcon); } catch { } }
            }
            catch { return null; }
        }

        static BitmapSource ResizeBitmap(BitmapSource src, int w, int h)
        {
            try
            {
                if (src.PixelWidth == w && src.PixelHeight == h) return src;
                var tb = new TransformedBitmap();
                tb.BeginInit();
                tb.Source = src;
                tb.Transform = new ScaleTransform((double)w / src.PixelWidth, (double)h / src.PixelHeight);
                tb.EndInit();
                if (tb.CanFreeze) tb.Freeze();
                return tb;
            }
            catch { return src; }
        }

        // [ICON-HASH] Вычисляем хэш пикселей иконки (16×16 downsample → SHA256 первые 8 байт).
        // Одинаковый хэш = одинаковый аватар профиля = одна кнопка на панели.
        static string ComputeIconHash(BitmapSource src)
        {
            try
            {
                if (src == null) return null;
                BitmapSource conv = src.Format != System.Windows.Media.PixelFormats.Bgra32
                    ? new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0)
                    : src;
                // Уменьшаем до 16×16 для быстрого сравнения
                var tb = new TransformedBitmap();
                tb.BeginInit();
                tb.Source = conv;
                tb.Transform = new ScaleTransform(16.0 / conv.PixelWidth, 16.0 / conv.PixelHeight);
                tb.EndInit();
                byte[] px = new byte[16 * 16 * 4];
                tb.CopyPixels(px, 16 * 4, 0);
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(px);
                    return BitConverter.ToString(hash, 0, 8); // 8 байт = 64 бита, коллизии невероятны
                }
            }
            catch { return null; }
        }

        const int UWP_ICON_SIZE = 48;

        // [STAB-7] Синхронный кэш — только HICON-методы (быстро)
        // Shell-методы вызываются асинхронно через LoadIconAsync
        // [PER-WINDOW-ICON] Для per-window процессов (Chrome и др.) используем
        // ключ по HWND — у каждого профиля своя иконка, полученная через WM_GETICON.
        BitmapSource GetCachedIconSafe(IntPtr hwnd, uint pid, string exePath, string groupKey = null)
        {
            // Для per-window групп (groupKey содержит ":hwnd") кэшируем по hwnd,
            // иначе — по exePath/pid как раньше.
            bool isPerWindow = !string.IsNullOrEmpty(groupKey) && groupKey.Contains(":");
            string key = isPerWindow
                ? $"hwnd:{hwnd.ToString("X")}"
                : (!string.IsNullOrEmpty(exePath) ? exePath : $"pid:{pid}");

            if (_iconCache.TryGetValue(key, out var c)) return c;
            BitmapSource result = null;
            if (hwnd != IntPtr.Zero && IsWindow(hwnd))
            {
                // [PER-WINDOW-ICON] Для браузерных профилей запрашиваем ВСЕ форматы HICON
                // (ICON_BIG → ICON_SMALL2 → ICON_SMALL → class icon).
                // Chrome выставляет иконку профиля именно через WM_GETICON.
                result = TryGetHIcon(SafeSendMessage(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero), 32)
                      ?? TryGetHIcon(SafeSendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL2, IntPtr.Zero), 32)
                      ?? TryGetHIcon(SafeSendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero), 32)
                      ?? TryGetHIcon(GetClassLongPtr(hwnd, GCL_HICON), 32)
                      ?? TryGetHIcon(GetClassLongPtr(hwnd, GCL_HICONSM), 32);
            }
            if (result == null && !string.IsNullOrEmpty(exePath) && IsLocalPath(exePath))
                result = TryShellItemImage(exePath, 32) ?? TrySHGetFileInfo(exePath, 32);
            if (result != null) _iconCache[key] = result;
            return result;
        }

        // [STAB-7] Асинхронная загрузка иконки с таймаутом 500ms
        // [PER-WINDOW-ICON] Для per-window групп (Chrome профили) кэш по hwnd
        void LoadIconAsync(AppGroup g, IntPtr hwnd, uint pid, string exePath)
        {
            if (g == null) return;
            bool isPerWindow = !string.IsNullOrEmpty(g.GroupKey) && g.GroupKey.Contains(":");
            // Для per-window: кэш-ключ по hwnd. Если уже есть в кэше — сразу применяем.
            if (isPerWindow && hwnd != IntPtr.Zero)
            {
                string hwndKey = $"hwnd:{hwnd.ToString("X")}";
                if (_iconCache.TryGetValue(hwndKey, out var cached) && cached != null)
                {
                    if (g.Icon == null && g.Button != null)
                    { g.Icon = cached; UpdateButtonIcon(g.Button, cached); }
                    return;
                }
            }
            var cts = new System.Threading.CancellationTokenSource(500);
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (cts.IsCancellationRequested) return;
                    BitmapSource icon;
                    if (IsUwpAppName(g.ExeName))
                    {
                        icon = GetCachedUwpIcon(g.ExeName);
                        if (icon == null && hwnd != IntPtr.Zero)
                            icon = GetBestIcon(hwnd, pid, exePath, 32);
                    }
                    else if (isPerWindow && hwnd != IntPtr.Zero)
                    {
                        // [PER-WINDOW-ICON] Для браузерных профилей читаем иконку
                        // напрямую из окна — каждый профиль Chrome имеет свою HICON
                        icon = GetBestIcon(hwnd, pid, exePath, 32);
                        if (icon != null)
                        {
                            string hwndKey = $"hwnd:{hwnd.ToString("X")}";
                            lock (_iconCache) { _iconCache[hwndKey] = icon; }
                        }
                    }
                    else
                    {
                        icon = GetBestIcon(hwnd, pid, exePath, 32);
                    }
                    if (icon == null) return;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (g.Icon == null && g.Button != null)
                            {
                                g.Icon = icon; UpdateButtonIcon(g.Button, icon);
                                // [ICON-HASH] Регистрируем хэш иконки если ещё не зарегистрирован
                                bool isPerWin = !string.IsNullOrEmpty(g.GroupKey) && g.GroupKey.Contains(":");
                                if (isPerWin && g.IconHash == null)
                                {
                                    string h = ComputeIconHash(icon);
                                    if (h != null && !_iconHashToGroupKey.ContainsKey(h))
                                    { g.IconHash = h; _iconHashToGroupKey[h] = g.GroupKey; }
                                }
                            }
                        }
                        catch { }
                    }));
                }
                catch { }
                finally { cts.Dispose(); }
            }, cts.Token);
        }

        // AUMID таблица для shell:AppsFolder
        static readonly Dictionary<string, string> UwpAumidMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "photos",               "Microsoft.Windows.Photos_8wekyb3d8bbwe!App" },
            { "calculator",           "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" },
            { "hxoutlook",            "microsoft.windowscommunicationsapps_8wekyb3d8bbwe!microsoft.windowslive.mail" },
            { "hxcalendarappimm",     "microsoft.windowscommunicationsapps_8wekyb3d8bbwe!microsoft.windowslive.calendar" },
            { "winstore",             "Microsoft.WindowsStore_8wekyb3d8bbwe!App" },
            { "video.ui",             "Microsoft.ZuneVideo_8wekyb3d8bbwe!Microsoft.ZuneVideo" },
            { "maps",                 "Microsoft.WindowsMaps_8wekyb3d8bbwe!App" },
            { "microsoft.bingweather","Microsoft.BingWeather_8wekyb3d8bbwe!App" },
            { "windowscamera",        "Microsoft.WindowsCamera_8wekyb3d8bbwe!App" },
            { "xboxapp",              "Microsoft.XboxApp_8wekyb3d8bbwe!Microsoft.XboxApp" },
            { "systemsettings",       "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel" },
        };

        BitmapSource GetCachedUwpIcon(string n)
        {
            if (string.IsNullOrEmpty(n)) return null;
            string key = "uwp:" + n;
            if (_iconCache.TryGetValue(key, out var c)) return c;
            var raw = TryGetUwpIcon(n);
            // Fallback через shell:AppsFolder (не требует доступа к WindowsApps)
            if (raw == null && UwpAumidMap.TryGetValue(n.ToLowerInvariant(), out string aumid))
                raw = TryShellParsingNameImage("shell:AppsFolder\\" + aumid, UWP_ICON_SIZE);
            var i = raw != null ? TrimTransparentBorders(raw) ?? raw : null;
            if (i != null) _iconCache[key] = i;
            return i;
        }

        BitmapSource TryGetUwpIcon(string n)
        {
            try
            {
                switch (n?.ToLowerInvariant())
                {
                    case "systemsettings":
                        {
                            string p = @"C:\Windows\ImmersiveControlPanel\SystemSettings.exe";
                            return TryShellItemImage(File.Exists(p) ? p : "SystemSettings.exe", UWP_ICON_SIZE)
                                ?? IconHelper.GetIconFromExe(p, UWP_ICON_SIZE);
                        }
                    case "calculator":
                        return TryUwpFolderPatternIcon("Microsoft.WindowsCalculator*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("Calculator.exe", UWP_ICON_SIZE)
                            ?? TryShellItemImage("calc.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("Calculator.exe", UWP_ICON_SIZE);
                    case "hxoutlook":
                        return TryUwpFolderPatternIcon("microsoft.windowscommunicationsapps*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("HxOutlook.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("HxOutlook.exe", UWP_ICON_SIZE);
                    case "hxcalendarappimm":
                        return TryUwpFolderPatternIcon("microsoft.windowscommunicationsapps*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("HxCalendarAppImm.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("HxCalendarAppImm.exe", UWP_ICON_SIZE);
                    case "winstore":
                    case "winstore.app":
                        return TryUwpFolderPatternIcon("Microsoft.WindowsStore*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("WinStore.App.exe", UWP_ICON_SIZE);
                    case "photos":
                        return TryGetPhotosIcon(UWP_ICON_SIZE);
                    case "video.ui":
                        return TryUwpFolderPatternIcon("Microsoft.ZuneVideo*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("Video.UI.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("Video.UI.exe", UWP_ICON_SIZE);
                    case "maps":
                        return TryUwpFolderPatternIcon("Microsoft.WindowsMaps*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("Maps.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("Maps.exe", UWP_ICON_SIZE);
                    case "microsoft.bingweather":
                        return TryUwpFolderPatternIcon("Microsoft.BingWeather*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("Microsoft.BingWeather.exe", UWP_ICON_SIZE);
                    case "windowscamera":
                        return TryUwpFolderPatternIcon("Microsoft.WindowsCamera*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("WindowsCamera.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("WindowsCamera.exe", UWP_ICON_SIZE);
                    case "xboxapp":
                        return TryUwpFolderPatternIcon("Microsoft.XboxApp*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("XboxApp.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("XboxApp.exe", UWP_ICON_SIZE);
                    default:
                        {
                            string ef = n + ".exe";
                            return TryFindInWindowsApps(ef, UWP_ICON_SIZE)
                                ?? TryShellItemImage(ef, UWP_ICON_SIZE)
                                ?? IconHelper.GetIconFromExe(ef, UWP_ICON_SIZE);
                        }
                }
            }
            catch { return null; }
        }

        BitmapSource TryGetPhotosIcon(int size)
        {
            // 1. shell:AppsFolder — работает без прав администратора
            try
            {
                string aumid = "Microsoft.Windows.Photos_8wekyb3d8bbwe!App";
                var bmp = TryShellParsingNameImage("shell:AppsFolder\\" + aumid, size);
                if (bmp != null) return bmp;
            }
            catch { }
            // 2. Через путь из _pidPathCache (если доступ есть)
            try
            {
                foreach (var kvp in _pidPathCache)
                {
                    string p = kvp.Value;
                    if (!string.IsNullOrEmpty(p) &&
                        p.IndexOf("Microsoft.Windows.Photos", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        File.Exists(p))
                    {
                        var bmp = TryShellItemImage(p, size) ?? IconHelper.GetIconFromExe(p, size);
                        if (bmp != null) return bmp;
                    }
                }
            }
            catch { }
            // 3. Стандартный путь через WindowsApps
            return TryUwpFolderPatternIcon("Microsoft.Windows.Photos*", size)
                ?? TryFindInWindowsApps("Photos.exe", size);
        }

        BitmapSource TryUwpFolderPatternIcon(string folderPattern, int size)
        {
            try
            {
                string uwpBase = @"C:\Program Files\WindowsApps";
                if (!Directory.Exists(uwpBase)) return null;
                string[] dirs;
                try { dirs = Directory.GetDirectories(uwpBase, folderPattern); }
                catch (UnauthorizedAccessException) { return null; }
                catch { return null; }
                if (dirs == null || dirs.Length == 0) return null;
                Array.Sort(dirs, (a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase));
                foreach (var dir in dirs)
                {
                    try
                    {
                        string manifest = IOPath.Combine(dir, "AppxManifest.xml");
                        if (!File.Exists(manifest)) continue;
                        string content;
                        try { content = File.ReadAllText(manifest); }
                        catch (UnauthorizedAccessException) { continue; }
                        string logo = ExtractManifestLogo(content);
                        if (string.IsNullOrEmpty(logo)) continue;
                        string iconPath = IOPath.Combine(dir, logo);
                        string baseDir = IOPath.GetDirectoryName(iconPath) ?? dir;
                        string baseName = IOPath.GetFileNameWithoutExtension(iconPath);
                        string ext = IOPath.GetExtension(iconPath);
                        var scales = new[]
                        {
                            ".scale-400",".scale-200",".scale-150",".scale-100",
                            ".targetsize-256",".targetsize-96",".targetsize-48",".targetsize-32",""
                        };
                        foreach (var scale in scales)
                        {
                            string scaled = IOPath.Combine(baseDir, baseName + scale + ext);
                            if (!File.Exists(scaled)) continue;
                            try
                            {
                                BitmapSource bmp = string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase)
                                    ? LoadPngIcon(scaled, size)
                                    : TryShellItemImage(scaled, size) ?? IconHelper.GetIconFromExe(scaled, size);
                                if (bmp != null) return bmp;
                            }
                            catch { }
                        }
                        if (Directory.Exists(baseDir))
                        {
                            var pngs = new List<string>();
                            try { pngs.AddRange(Directory.GetFiles(baseDir, baseName + "*.png")); } catch { }
                            pngs.Sort((a2, b2) => GetIconScaleOrder(a2).CompareTo(GetIconScaleOrder(b2)));
                            foreach (var png in pngs)
                            {
                                try { var bmp = LoadPngIcon(png, size); if (bmp != null) return bmp; }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        static int GetIconScaleOrder(string path)
        {
            if (path.Contains("scale-400")) return 0;
            if (path.Contains("scale-200")) return 1;
            if (path.Contains("scale-150")) return 2;
            if (path.Contains("targetsize-256")) return 3;
            if (path.Contains("targetsize-96")) return 4;
            if (path.Contains("scale-100")) return 5;
            if (path.Contains("targetsize-48")) return 6;
            if (path.Contains("targetsize-32")) return 7;
            return 8;
        }

        static BitmapSource LoadPngIcon(string path, int size)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                if (size > 0) { bi.DecodePixelWidth = size; bi.DecodePixelHeight = size; }
                bi.EndInit();
                if (bi.CanFreeze) bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        BitmapSource TryFindInWindowsApps(string exe, int size = 32)
        {
            try
            {
                string b = @"C:\Program Files\WindowsApps";
                if (!Directory.Exists(b)) return null;
                string[] dirs;
                try { dirs = Directory.GetDirectories(b); }
                catch (UnauthorizedAccessException) { return null; }
                catch { return null; }
                foreach (var d in dirs)
                {
                    try
                    {
                        string c = IOPath.Combine(d, exe);
                        if (!File.Exists(c)) continue;
                        var i = TryShellItemImage(c, size) ?? IconHelper.GetIconFromExe(c, size);
                        if (i != null) return i;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        static BitmapSource TrimTransparentBorders(BitmapSource src)
        {
            try
            {
                BitmapSource work = src;
                if (src.Format != System.Windows.Media.PixelFormats.Bgra32)
                    work = new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int w = work.PixelWidth, h = work.PixelHeight;
                if (w == 0 || h == 0) return src;
                int stride = w * 4;
                byte[] pixels = new byte[h * stride];
                work.CopyPixels(pixels, stride, 0);
                int minX = w, maxX = -1, minY = h, maxY = -1;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        byte alpha = pixels[y * stride + x * 4 + 3];
                        if (alpha > 8) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
                    }
                if (maxX < 0 || maxY < 0) return src;
                int cropW = maxX - minX + 1, cropH = maxY - minY + 1;
                if (cropW >= w * 0.9 && cropH >= h * 0.9) return src;
                const int pad = 2;
                int rx = Math.Max(0, minX - pad), ry = Math.Max(0, minY - pad);
                int rw = Math.Min(w - rx, cropW + pad * 2), rh = Math.Min(h - ry, cropH + pad * 2);
                var cropped = new CroppedBitmap(work, new Int32Rect(rx, ry, rw, rh));
                if (cropped.CanFreeze) cropped.Freeze();
                return cropped;
            }
            catch { return src; }
        }

        static string ExtractManifestLogo(string xml)
        {
            try
            {
                foreach (var attr in new[] { "Square44x44Logo", "Square30x30Logo", "Logo" })
                {
                    string search = attr + "=\"";
                    int i = xml.IndexOf(search, StringComparison.OrdinalIgnoreCase);
                    if (i >= 0)
                    {
                        i += search.Length;
                        int j = xml.IndexOf('"', i);
                        if (j > i) { string val = xml.Substring(i, j - i).Trim(); if (!string.IsNullOrEmpty(val)) return val; }
                    }
                }
                const string open = "<Logo>", close = "</Logo>";
                int ii = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
                if (ii >= 0)
                {
                    ii += open.Length;
                    int jj = xml.IndexOf(close, ii, StringComparison.OrdinalIgnoreCase);
                    if (jj > ii) return xml.Substring(ii, jj - ii).Trim();
                }
            }
            catch { }
            return null;
        }

        // ═════════════════════════════════════════════════════════════════════
        // СПИСКИ ПРОЦЕССОВ [FIX-1]
        // ═════════════════════════════════════════════════════════════════════
        static readonly HashSet<string> IgnoredProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "mytaskbar","textinputhost","shellexperiencehost","startmenuexperiencehost",
    "searchhost","lockapp","dwm","csrss","smss","wininit","services",
    "lsass","svchost","fontdrvhost","sihost","ctfmon",
    "microsoft.media.player","zunemusic",   // ← добавить
};

        static readonly HashSet<string> ForceShowProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "taskmgr","regedit","mmc","eventvwr",
            "devmgmt","diskmgmt","perfmon","resmon","cmd","powershell","windowsterminal",
            "wt","calc","notepad","calculator","calculatorapp","systemsettings","mspaint","snippingtool","explorer",
            "hxoutlook","hxcalendarappimm","winstore","photos","video.ui","maps",
            "microsoft.bingweather","windowscamera","xboxapp","xboxgamingoverlay",
            "minecraftlauncher","minecraft","javaw",
        };

        // ═════════════════════════════════════════════════════════════════════
        // UWP РЕЗОЛВИНГ [FIX-2]
        // ═════════════════════════════════════════════════════════════════════
        static readonly (string[] Titles, string AppName)[] UwpTitleMap =
        {
            (new[]{"settings","параметры","параметры windows","system settings","windows settings"}, "systemsettings"),
            (new[]{"calculator","калькулятор"}, "calculator"),
            (new[]{"mail","почта"}, "hxoutlook"),
            (new[]{"calendar","календарь"}, "hxcalendarappimm"),
            (new[]{"microsoft store","магазин microsoft","магазин","store"}, "winstore"),
            (new[]{"photos","фотографии","фото"}, "photos"),
            (new[]{"movies & tv","кино и тв","фильмы"}, "video.ui"),
            (new[]{"maps","карты"}, "maps"),
            (new[]{"weather","msn weather","погода","msn погода"}, "microsoft.bingweather"),
            (new[]{"camera","камера"}, "windowscamera"),
            (new[]{"xbox"}, "xboxapp"),
            (new[]{"notepad","блокнот"}, "notepad"),
        };

        string ResolveUwpAppName(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;
            string t = title.Trim().ToLowerInvariant();
            if (t.Length == 0) return null;
            foreach (var (titles, appName) in UwpTitleMap)
                foreach (var c in titles)
                {
                    if (t == c
                        || t.StartsWith(c + " — ")
                        || t.StartsWith(c + " - ")
                        || t.StartsWith(c + " | ")
                        || t.StartsWith(c + ": "))
                        return appName;
                }
            if (t.EndsWith(" — photos") || t.EndsWith(" - photos")
                || t.EndsWith(" — фотографии") || t.EndsWith(" - фотографии"))
                return "photos";
            if (t == "microsoft store" || t == "магазин microsoft" || t == "магазин" || t == "store")
                return "winstore";
            return null;
        }

        bool IsUwpAppName(string n)
        {
            switch (n?.ToLowerInvariant())
            {
                case "systemsettings":
                case "calculator":
                case "calculatorapp":   // [FIX-GHOST-v3] прямой процесс калькулятора
                case "hxoutlook":
                case "hxcalendarappimm":
                case "winstore":
                case "winstore.app":
                case "photos":
                case "video.ui":
                case "maps":
                case "microsoft.bingweather":
                case "windowscamera":
                case "xboxapp":
                case "xboxgamingoverlay": return true;
                default: return false;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // ЗАКРЫТИЕ СИСТЕМНОГО ПУСКА [FIX-3]
        // ═════════════════════════════════════════════════════════════════════
        void TryCloseSystemStartMenu()
        {
            try
            {
                IntPtr h = FindWindow("Windows.UI.Core.CoreWindow", "Пуск");
                if (h == IntPtr.Zero) h = FindWindow("Windows.UI.Core.CoreWindow", "Start");
                if (h != IntPtr.Zero && IsWindowVisible(h))
                {
                    // [STAB-1] SafeSendMessage вместо SendMessage
                    SafeSendMessage(h, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero);
                    return;
                }
                h = FindWindow("Microsoft.UI.Content.DesktopChildSiteBridge", null);
                if (h != IntPtr.Zero && IsWindowVisible(h))
                    SafeSendMessage(h, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero);
            }
            catch { }
        }

        // [FIX-TASKMGR-STARTMENU] Отложенное закрытие системного Пуска.
        // Когда диспетчер задач в фокусе (High IL), Shell открывает Пуск через
        // внутренний IPC-канал с задержкой ~80-150мс после Win-key — в обход
        // LowLevel keyboard hook. Вызов TryCloseSystemStartMenu() до ShowMenu()
        // не помогает: Пуск ещё не открылся. Ждём 180мс и закрываем повторно.
        void TryCloseSystemStartMenuDelayed(int delayMs = 180)
        {
            var t = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(delayMs)
            };
            t.Tick += (s, e) =>
            {
                t.Stop();
                TryCloseSystemStartMenu();
            };
            t.Start();
        }

        // Проверяет, является ли foreground-окно диспетчером задач.
        // Определяем по window class — надёжнее имени процесса, не требует
        // прав администратора и работает для любой локализации Windows.
        bool IsTaskmgrForeground()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                var sb = new StringBuilder(256);
                GetWindowClassName(fg, sb, sb.Capacity);
                string cls = sb.ToString();
                // TaskManagerWindow  — основное окно taskmgr на Win10/Win11
                // TaskMgrRebar       — дочерний rebar Win10 taskmgr (на всякий случай)
                return cls == "TaskManagerWindow" || cls == "TaskMgrRebar";
            }
            catch { return false; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // ACRYLIC / BLUR [FIX-4]
        // ═════════════════════════════════════════════════════════════════════
        void ApplyAcrylicBackground()
        {
            if (Environment.OSVersion.Version.Major < 10)
            {
                if (RootBorder != null)
                    RootBorder.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1A, 0x1A, 0x2A));
                return;
            }
            try
            {
                if (IsWin10_1903Plus) AcrylicHelper.EnableAcrylic(this, 0x701A1A2E);
                else AcrylicHelper.EnableBlur(this, 0x70202030);
            }
            catch
            {
                try { AcrylicHelper.EnableBlur(this, 0x70202030); }
                catch
                {
                    if (RootBorder != null)
                        RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x70, 0x1A, 0x1A, 0x2A));
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // BLUETOOTH
        // ═════════════════════════════════════════════════════════════════════
        void StartBluetoothTracker()
        {
            PollBluetoothState();
            _bluetoothTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _bluetoothTimer.Tick += (s, e) => SafeRun(PollBluetoothState, "PollBluetoothState");
            _bluetoothTimer.Start();
        }

        void PollBluetoothState()
        {
            // [STAB-6] Флаг реентрантности
            if (System.Threading.Interlocked.CompareExchange(ref _btBusyInt, 1, 0) != 0) return;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    int state = GetBluetoothStateInt();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (state == _btLastStateInt) return;
                            _btLastStateInt = state;
                            ApplyBluetoothState(state);
                        }
                        catch { }
                        finally { System.Threading.Interlocked.Exchange(ref _btBusyInt, 0); }
                    }));
                }
                catch { System.Threading.Interlocked.Exchange(ref _btBusyInt, 0); }
            });
        }

        static int GetBluetoothStateInt()
        {
            try
            {
                var p = new BLUETOOTH_FIND_RADIO_PARAMS
                { dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS)) };
                IntPtr hRadio;
                IntPtr hFind = BluetoothFindFirstRadio(ref p, out hRadio);
                if (hFind != IntPtr.Zero)
                { CloseHandle(hRadio); BluetoothFindRadioClose(hFind); return 1; }
                bool hasAdapter = false;
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\bthserv"))
                    if (key != null) hasAdapter = true;
                if (!hasAdapter)
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHUSB"))
                        if (key != null) hasAdapter = true;
                if (!hasAdapter)
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHMINI"))
                        if (key != null) hasAdapter = true;
                return hasAdapter ? 0 : -1;
            }
            catch { return -1; }
        }

        // [STAB-10] ApplyBluetoothState: Dispatcher.CheckAccess + IsLoaded
        void ApplyBluetoothState(int state)
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                { Dispatcher.BeginInvoke(new Action(() => ApplyBluetoothState(state))); return; }
                if (!IsLoaded || BluetoothButton == null || BluetoothIcon == null) return;
                if (state == 1)
                { BluetoothIcon.Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x9D, 0xFF)); BluetoothButton.ToolTip = "Bluetooth: on"; }
                else if (state == 0)
                { BluetoothIcon.Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)); BluetoothButton.ToolTip = "Bluetooth: off"; }
                else
                { BluetoothIcon.Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0xAA, 0xAA, 0xAA)); BluetoothButton.ToolTip = "Bluetooth: adapter not found"; }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] ApplyBluetoothState: {ex.Message}"); }
        }

        void BluetoothButton_Click(object sender, RoutedEventArgs e)
        {
            try { MarkOwnActivity(); Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] BluetoothButton_Click: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // ВЫХОД ИЗ СНА [FIX-8] + БЛОКИРОВКА ЭКРАНА [STAB-5]
        // ═════════════════════════════════════════════════════════════════════
        void HookPowerEvents()
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (source != null)
            {
                source.AddHook(PowerBroadcastHook);
                source.AddHook(SessionChangeHook); // [STAB-5]
                source.AddHook(NcHitTestHook);     // [FIX-DRAG]
                source.AddHook(ShellAttentionHook); // [ATTENTION]
                source.AddHook(DpiChangedHook);     // [DPI-AUTO]

            // [DPI-AUTO] Читаем начальный DPI при старте
            _currentDpiScale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            }
            // [STAB-5] Регистрация WTS-уведомлений
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] WTS register: {ex.Message}"); }
        }

        // [FIX-DRAG] Пропускаем мышиные события (включая drag&drop) сквозь
        // пустые зоны панели. Кнопки и интерактивные элементы получают клики нормально.
        IntPtr NcHitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_NCHITTEST) return IntPtr.Zero;
            try
            {
                // Экранные координаты из lParam
                int sx = unchecked((short)(lParam.ToInt32() & 0xFFFF));
                int sy = unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));

                // Переводим в координаты WPF с учётом DPI
                Point ptWpf;
                try { ptWpf = PointFromScreen(new Point(sx, sy)); }
                catch { return IntPtr.Zero; }

                // Hit-test: есть ли интерактивный элемент под курсором?
                var hit = VisualTreeHelper.HitTest(this, ptWpf);
                if (hit == null)
                {
                    handled = true;
                    return new IntPtr(HTTRANSPARENT);
                }

                // Если попали в пустой фон (само окно, корневой Border или StackPanel) — пропускаем
                var visual = hit.VisualHit;
                bool isBackground =
                    visual is Window ||
                    (visual is Border brd && (brd.Name == "RootBorder" || string.IsNullOrEmpty(brd.Name))) ||
                    (visual is StackPanel sp && (sp.Name == "MainPanel" || string.IsNullOrEmpty(sp.Name)));

                if (isBackground)
                {
                    handled = true;
                    return new IntPtr(HTTRANSPARENT);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] NcHitTestHook: {ex.Message}"); }
            return IntPtr.Zero;
        }

        IntPtr PowerBroadcastHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_POWERBROADCAST)
            {
                int evt = wParam.ToInt32();
                if (evt == PBT_APMRESUMEAUTOMATIC || evt == PBT_APMRESUMESUSPEND)
                {
                    Debug.WriteLine($"[MyTaskbar] PowerResume evt=0x{evt:X}");
                    Dispatcher.BeginInvoke(new Action(OnSystemResume));
                }
            }
            return IntPtr.Zero;
        }

        // [STAB-5] Блокировка экрана / смена пользователя = как sleep
        IntPtr SessionChangeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_WTSSESSION_CHANGE)
            {
                int evt = wParam.ToInt32();
                if (evt == WTS_SESSION_LOCK || evt == WTS_REMOTE_DISCONNECT || evt == WTS_CONSOLE_DISCONNECT)
                {
                    Debug.WriteLine($"[MyTaskbar] WTS session event=0x{evt:X}");
                    Dispatcher.BeginInvoke(new Action(OnSystemResume));
                }
            }
            return IntPtr.Zero;
        }

        void OnSystemResume()
        {
            // [FIX-RESUME-INSTANT] Не удаляем кнопки при пробуждении — как в оригинальном Win10.
            // Старый подход "удалить всё -> заблокировать 8 сек -> пересоздать" давал:
            //   - пустую панель на 8 секунд
            //   - дубликаты если блок снимался раньше чем окна успевали зарегистрироваться
            //
            // Новый подход: чистим кеши, снимаем блок сразу, запускаем UpdateWindowGroups.
            // UpdateWindowGroupsCore сам сверит живые окна с группами:
            //   - окна пережившие сон — останутся, кнопки не мигают
            //   - окна закрывшиеся во сне — уберутся через обычную логику удаления
            //   - новые окна — добавятся как обычно
            _pidNameCache.Clear();
            _pidPathCache.Clear();
            _pendingNewGroups.Clear();
            _resumingFromSleep = false;

            _resumeBlockTimer?.Stop();
            _resumeBlockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _resumeBlockTimer.Tick += (s, e) =>
            {
                _resumeBlockTimer.Stop();
                UpdateWindowGroups();
                Debug.WriteLine("[MyTaskbar] Resume: UpdateWindowGroups triggered");
            };
            _resumeBlockTimer.Start();
        }

        // ═════════════════════════════════════════════════════════════════════
        // EDGE REVEAL / FULLSCREEN
        // ═════════════════════════════════════════════════════════════════════
        void StartEdgeRevealWatcher()
        {
            // [PERF-1] 50ms достаточно для edge-reveal (было 16ms = 60 тиков/сек на UI-потоке)
            _edgeRevealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _edgeRevealTimer.Tick += (s, e) => SafeRun(CheckEdgeReveal, "CheckEdgeReveal");
            _edgeRevealTimer.Start();
        }

        void CheckEdgeReveal()
        {
            // [EDGE-FIX] Если панель уже видима (показана через attention или обычно) —
            // edge reveal не нужен вообще. Предотвращает повторный тригер при наведении
            // курсора на верхний/нижний край видимой панели.
            if (Visibility == Visibility.Visible || _taskbarAnimating)
            { _edgeRevealPending = false; _edgeCursorEnteredAt = DateTime.MinValue; return; }
            if (!_isHiddenByFullscreen)
            { _edgeRevealPending = false; _edgeCursorEnteredAt = DateTime.MinValue; return; }
            if (!GetCursorPos(out POINT cp)) return;
            try
            {
                var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
                if (GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) == 0)
                { _edgeCursorEnteredAt = DateTime.MinValue; _edgeRevealPending = false; return; }
            }
            catch { }
            var src2 = PresentationSource.FromVisual(this);
            double dpi = src2?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double taskbarLeft = Left * dpi;
            double taskbarRight = taskbarLeft + ActualWidth * dpi;
            bool atEdge = _isBottom
                ? cp.y >= (int)(SystemParameters.PrimaryScreenHeight * dpi) - 2 && cp.x >= taskbarLeft && cp.x <= taskbarRight
                : cp.y <= 2 && cp.x >= taskbarLeft && cp.x <= taskbarRight;
            if (atEdge)
            {
                if (_edgeCursorEnteredAt == DateTime.MinValue) _edgeCursorEnteredAt = DateTime.UtcNow;
                // [ATTENTION-HIDE] Панель уже показана из-за attention — edge reveal не нужен,
                // иначе _isHiddenByFullscreen сбросится и панель не спрячется после.
                if (!_edgeRevealPending && !_shownByAttention)
                {
                    _edgeRevealPending = true;
                    _isHiddenByFullscreen = false;
                    _fullscreenConfirmCount = 0;
                    _notFullscreenConfirmCount = 0;
                    bool fps = IsCursorCapturedByFpsGame();
                    ShowTaskbar(animate: true, onComplete: () =>
                    {
                        // [GAME-4] Edge reveal в FPS — блокер сразу после появления панели
                        if (fps) ShowFullscreenBlocker();
                    });
                }
            }
            else { _edgeCursorEnteredAt = DateTime.MinValue; _edgeRevealPending = false; }
        }

        void StartFullscreenWatcher()
        {
            _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            // [STAB-6] Флаг реентрантности для fullscreen-чека
            _fullscreenTimer.Tick += (s, e) =>
            {
                if (System.Threading.Interlocked.CompareExchange(ref _fsBusyInt, 1, 0) != 0) return;
                try { SafeRun(CheckFullscreen, "CheckFullscreen"); }
                finally { System.Threading.Interlocked.Exchange(ref _fsBusyInt, 0); }
            };
            _fullscreenTimer.Start();
        }

        void CheckFullscreen()
        {
            if (_taskbarAnimating) return;
            if (!_fullscreenAutoHide) return; // user disabled auto-hide in settings
            bool anyOwnVisible = (_menuWindow != null && _menuWindow.IsVisible)
                              || (_trayWindow != null && _trayWindow.IsOpen)
                              || (_wifiWindow != null && _wifiWindow.IsVisible)
                              || (_previewWindow != null && _previewWindow.IsVisible);
            bool fs = IsForegroundFullscreen();
            if (fs && _menuWindow != null && _menuWindow.IsVisible)
            {
                IntPtr fg = GetForegroundWindow();
                IntPtr hMenu = IntPtr.Zero, hTaskbar = IntPtr.Zero;
                try { hMenu = new WindowInteropHelper(_menuWindow).Handle; } catch { }
                try { hTaskbar = new WindowInteropHelper(this).Handle; } catch { }
                if (fg != hMenu && fg != hTaskbar && fg == _lastFullscreenHwnd && !IsInOwnActivityGrace())
                    _menuWindow.HideAnimated();
            }
            if (anyOwnVisible) { _fullscreenConfirmCount = 0; _notFullscreenConfirmCount = 0; MarkOwnActivity(); return; }
            if (IsCursorOverOwnWindows()) { _fullscreenConfirmCount = 0; _notFullscreenConfirmCount = 0; MarkOwnActivity(); return; }
            if (IsInOwnActivityGrace()) { _fullscreenConfirmCount = 0; _notFullscreenConfirmCount = 0; return; }

            // [GAME-9] Если пользователь открыл окно поверх игры — ждём пока игра снова не станет
            // foreground. Как только игра вернула фокус (fullscreen и fg == lastFullscreenHwnd) —
            // сбрасываем флаг и возвращаем блокер, чтобы курсор снова не уходил в игру.
            if (_appOpenedOverFullscreen)
            {
                if (fs)
                {
                    IntPtr fg = GetForegroundWindow();
                    if (fg == _lastFullscreenHwnd)
                    {
                        // Игра снова в фокусе — восстанавливаем блокер
                        _appOpenedOverFullscreen = false;
                        Debug.WriteLine("[MyTaskbar] Game regained focus — restoring blocker");
                        if (Visibility == Visibility.Visible)
                            ShowFullscreenBlocker();
                    }
                }
                else
                {
                    // Игра вышла из fullscreen пока окно было открыто — сбрасываем флаг
                    _appOpenedOverFullscreen = false;
                }
                return;
            }

            if (fs)
            {
                _notFullscreenConfirmCount = 0;
                if (!_isHiddenByFullscreen)
                {
                    _fullscreenConfirmCount++;
                    if (_fullscreenConfirmCount >= FULLSCREEN_CONFIRM_TICKS)
                    { _fullscreenConfirmCount = 0; _isHiddenByFullscreen = true; HideTaskbar(animate: true); }
                }
            }
            else
            {
                _fullscreenConfirmCount = 0;
                if (_isHiddenByFullscreen)
                {
                    // [MONITOR-FIX] Foreground больше не fullscreen (пользователь кликнул
                    // на второй монитор), но fullscreen-окно ещё покрывает основной монитор.
                    // Win10: панель остаётся скрытой, пока игра физически на экране.
                    if (IsWindowCoveringPrimaryMonitor(_lastFullscreenHwnd))
                    {
                        _notFullscreenConfirmCount = 0;
                        return; // остаёмся скрытыми
                    }
                    _notFullscreenConfirmCount++;
                    if (_notFullscreenConfirmCount >= NOT_FULLSCREEN_CONFIRM_TICKS)
                    { _notFullscreenConfirmCount = 0; _isHiddenByFullscreen = false; ShowTaskbar(animate: true); }
                }
            }
        }

        static string GetWindowClass(IntPtr hwnd)
        {
            try { var sb = new StringBuilder(256); GetWindowClassName(hwnd, sb, sb.Capacity); return sb.ToString(); } catch { return ""; }
        }

        // [ATTENTION-HIDE] Устанавливаем глобальный mouse hook пока панель показана в noActivate-режиме.
        // Любой клик ВНЕ панели → скрываем её и снимаем hook.
        void InstallAttentionMouseHook()
        {
            if (_attentionMouseHook != IntPtr.Zero) return;
            _attentionMouseProc = AttentionMouseHookCallback;
            using (var m = System.Diagnostics.Process.GetCurrentProcess().MainModule)
                _attentionMouseHook = SetWindowsHookEx(WH_MOUSE_LL, _attentionMouseProc, GetModuleHandle(m.ModuleName), 0);
            Debug.WriteLine("[ATTENTION] Mouse hook installed");
        }

        void UninstallAttentionMouseHook()
        {
            if (_attentionMouseHook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_attentionMouseHook); } catch { }
            _attentionMouseHook = IntPtr.Zero;
            Debug.WriteLine("[ATTENTION] Mouse hook removed");
        }

        IntPtr AttentionMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _shownByAttention)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
                {
                    // Проверяем: клик внутри панели? Если нет — скрываем.
                    System.Runtime.InteropServices.Marshal.PtrToStructure(
                        lParam, typeof(POINT)); // просто читаем
                    var pt = (POINT)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(POINT));
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            // Получаем RECT панели в экранных координатах
                            var src = PresentationSource.FromVisual(this);
                            double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                            int left   = (int)(Left   * dpi);
                            int top    = (int)(Top    * dpi);
                            int right  = (int)((Left + ActualWidth)  * dpi);
                            int bottom = (int)((Top  + ActualHeight) * dpi);
                            bool insideTaskbar = pt.x >= left && pt.x <= right
                                              && pt.y >= top  && pt.y <= bottom;
                            if (!insideTaskbar && _shownByAttention)
                            {
                                Debug.WriteLine("[ATTENTION] Click outside taskbar — hiding");
                                _shownByAttention = false;
                                UninstallAttentionMouseHook();
                                HideTaskbar(animate: true);
                            }
                        }
                        catch { }
                    }));
                }
            }
            return CallNextHookEx(_attentionMouseHook, nCode, wParam, lParam);
        }

        // [MONITOR-FIX] Окно всё ещё покрывает основной монитор полностью?
        bool IsWindowCoveringPrimaryMonitor(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
                if (!GetWindowRect(hwnd, out RECT r)) return false;
                int sw = (int)SystemParameters.PrimaryScreenWidth;
                int sh = (int)SystemParameters.PrimaryScreenHeight;
                return r.left <= 4 && r.top <= 4 && r.right >= sw - 4 && r.bottom >= sh - 4;
            }
            catch { return false; }
        }

        bool IsForegroundFullscreen()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow(); if (hwnd == IntPtr.Zero) return false;
                if (hwnd == GetShellWindow()) return false;
                if (DesktopWindowClasses.Contains(GetWindowClass(hwnd))) return false;
                // [FIX-D3DPROXY] Proxy/overlay окна не считаются fullscreen-окнами
                if (IgnoredWindowClasses.Contains(GetWindowClass(hwnd))) return false;
                try { if (hwnd == new WindowInteropHelper(this).Handle) return false; } catch { }
                if (_menuWindow != null) try { if (hwnd == new WindowInteropHelper(_menuWindow).Handle) return false; } catch { }
                if (_previewWindow != null) try { if (hwnd == new WindowInteropHelper(_previewWindow).Handle) return false; } catch { }
                if (_trayWindow != null) try { if (hwnd == new WindowInteropHelper(_trayWindow).Handle) return false; } catch { }
                if (_wifiWindow != null) try { if (hwnd == new WindowInteropHelper(_wifiWindow).Handle) return false; } catch { }
                GetWindowThreadProcessId(hwnd, out uint pid); if (pid == 0) return false;
                string pn = "";
                if (!_pidNameCache.TryGetValue(pid, out pn))
                    try { pn = Process.GetProcessById((int)pid).ProcessName?.ToLowerInvariant() ?? ""; } catch { }
                if (string.Equals(pn, "explorer", StringComparison.OrdinalIgnoreCase)) return false;
                if (IgnoredProcesses.Contains(pn)) return false;
                // [STAB-4] SafeGetWindowText
                string title = SafeGetWindowText(hwnd);
                if (string.IsNullOrWhiteSpace(title) || title == "Program Manager") return false;
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
                // [STAB-11] IsWindow перед GetWindowLong
                if (!IsWindow(hwnd)) return false;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0) return false;
                if (!GetWindowRect(hwnd, out RECT r)) return false;
                int sw = (int)SystemParameters.PrimaryScreenWidth, sh = (int)SystemParameters.PrimaryScreenHeight;
                if (r.left > 4 || r.top > 4 || r.right < sw - 4 || r.bottom < sh - 4) return false;
                _lastFullscreenHwnd = hwnd; return true;
            }
            catch { return false; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // [GAME] FPS-ДЕТЕКЦИЯ И БЛОКЕР КУРСОРА
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Возвращает true если активная игра захватила курсор (FPS / захват мышью).
        /// Проверяем два признака:
        ///   1) CURSOR_SHOWING == 0 — курсор скрыт (типично для FPS)
        ///   2) GetClipCursor возвращает прямоугольник &lt;= 2x2 пикселя —
        ///      игра вызвала ClipCursor с точечной областью (raw mouse)
        /// </summary>
        bool IsCursorCapturedByFpsGame()
        {
            try
            {
                // Признак 1: курсор скрыт
                var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
                if (GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) == 0)
                    return true;
                // Признак 2: ClipCursor ограничен малой зоной (захват мышью без скрытия)
                if (GetClipCursor(out RECT clip))
                {
                    int w = clip.right - clip.left;
                    int h = clip.bottom - clip.top;
                    if (w <= 2 && h <= 2) return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Забирает фокус у игры через AttachThreadInput — единственный надёжный способ
        /// вырвать курсор из Raw Input захвата CS2/TF2/Minecraft без NOACTIVATE ограничений.
        /// После этого игра перестаёт получать Raw Input движения мыши.
        /// </summary>
        void StealFocusFromGame()
        {
            try
            {
                var myHwnd = new WindowInteropHelper(this).Handle;
                if (myHwnd == IntPtr.Zero) return;

                IntPtr fgHwnd = GetForegroundWindow();
                if (fgHwnd == IntPtr.Zero || fgHwnd == myHwnd) return;

                uint myTid = GetWindowThreadProcessId(myHwnd, IntPtr.Zero);
                uint fgTid = GetWindowThreadProcessId(fgHwnd, IntPtr.Zero);

                // Присоединяем наш поток к потоку игры — теперь SetForegroundWindow работает
                bool attached = false;
                if (myTid != fgTid)
                {
                    attached = AttachThreadInput(myTid, fgTid, true);
                }

                try
                {
                    AllowSetForegroundWindow((uint)System.Diagnostics.Process.GetCurrentProcess().Id);
                    SetForegroundWindow(myHwnd);
                    // Снимаем ClipCursor ПОСЛЕ того как мы в фокусе
                    ClipCursor(IntPtr.Zero);
                }
                finally
                {
                    if (attached) AttachThreadInput(myTid, fgTid, false);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] StealFocusFromGame: {ex.Message}"); }
        }

        void ShowFullscreenBlocker()
        {
            try
            {
                if (_blockerWindow == null)
                {
                    _blockerWindow = new FullscreenBlockerWindow();
                    _blockerWindow.DismissRequested += (sx, sy) =>
                        Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                            new Action(() => OnBlockerDismissed(sx, sy)));
                    _blockerWindow.TaskbarWindow = this;
                }
                // [GAME-7] Передаём ссылку на меню Пуск — дырка №2
                _blockerWindow.MenuWindow = _menuWindow;
                _blockerWindow.ShowBlocker();
                // [GAME-6] Тулбар должен быть выше блокера — поднимаем его после
                Topmost = false;
                Topmost = true;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] ShowFullscreenBlocker: {ex.Message}"); }
        }

        void HideFullscreenBlocker()
        {
            try
            {
                _blockerWindow?.HideBlocker();
                _appOpenedOverFullscreen = false;
                // Вернуть фокус обратно в игру
                if (_lastFullscreenHwnd != IntPtr.Zero && IsWindow(_lastFullscreenHwnd))
                    SetForegroundWindow(_lastFullscreenHwnd);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] HideFullscreenBlocker: {ex.Message}"); }
        }

        /// <summary>
        /// Вызывается когда пользователь кликнул вне тулбара/меню по блокеру.
        /// Определяет что под курсором: если это другое окно (не игра) —
        /// передаёт фокус ему (suspend-режим). Если пустое место или сама игра —
        /// возвращает фокус в игру как обычно.
        /// </summary>
        void OnBlockerDismissed(int screenX, int screenY)
        {
            try
            {
                _blockerWindow?.HideBlocker();

                // Ищем окно под курсором, игнорируя блокер (он уже скрыт)
                IntPtr hwndUnder = WindowFromPoint(new POINT { x = screenX, y = screenY });

                // Получаем корневое окно (не дочерний контрол)
                if (hwndUnder != IntPtr.Zero)
                    hwndUnder = GetAncestor(hwndUnder, GA_ROOT);

                // Проверяем: это наше собственное окно?
                bool isOwn = false;
                try { isOwn = hwndUnder == new WindowInteropHelper(this).Handle; } catch { }
                if (!isOwn && _menuWindow != null)
                    try { isOwn |= hwndUnder == new WindowInteropHelper(_menuWindow).Handle; } catch { }

                if (!isOwn && hwndUnder != IntPtr.Zero && hwndUnder != _lastFullscreenHwnd
                    && IsWindowVisible(hwndUnder))
                {
                    // Под курсором — другое видимое окно (браузер, медиаплеер и т.д.)
                    // Переключаемся на него без возврата в игру
                    _appOpenedOverFullscreen = true;
                    Debug.WriteLine($"[MyTaskbar] Blocker dismissed → switching to window {hwndUnder:X}");
                    SetForegroundWindow(hwndUnder);
                }
                else
                {
                    // Пустое место или сама игра — возвращаем в игру
                    _appOpenedOverFullscreen = false;
                    Debug.WriteLine("[MyTaskbar] Blocker dismissed → returning to game");
                    if (_lastFullscreenHwnd != IntPtr.Zero && IsWindow(_lastFullscreenHwnd))
                        SetForegroundWindow(_lastFullscreenHwnd);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] OnBlockerDismissed: {ex.Message}"); }
        }

        // [GAME-9] Скрыть блокер когда пользователь открывает/переключается на окно поверх игры.
        // Блокер вернётся только когда игра снова получит фокус (CheckFullscreen это отследит).
        // ВАЖНО: используем _lastFullscreenHwnd а не IsForegroundFullscreen() — когда меню открыто,
        // foreground = меню, и IsForegroundFullscreen() вернёт false, флаг не поставится,
        // MenuVisibilityChanged вернёт блокер и первый клик по окну уйдёт в игру.
        void SuspendBlockerForAppWindow()
        {
            try
            {
                // Игра была fullscreen если lastFullscreenHwnd валиден и ещё существует
                bool wasFullscreen = _lastFullscreenHwnd != IntPtr.Zero && IsWindow(_lastFullscreenHwnd);
                if (!wasFullscreen) return;
                _blockerWindow?.HideBlocker();
                _appOpenedOverFullscreen = true;
                Debug.WriteLine("[MyTaskbar] Blocker suspended — app window opened over game");
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] SuspendBlockerForAppWindow: {ex.Message}"); }
        }

        void HideTaskbar(bool animate = false)
        {
            if (_taskbarAnimating) return;
            _wifiWindow?.Hide(); HidePreview();
            // [GAME-5] Снимаем блокер курсора вместе с панелью
            _blockerWindow?.HideBlocker();
            if (_menuWindow != null && _menuWindow.IsVisible)
            {
                _menuWindow.HideAnimated();
                int delay = (int)MenuWindow.ANIM_HIDE_MS + 10;
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
                t.Tick += (s, e) => { t.Stop(); HideTaskbarCore(animate); };
                t.Start();
            }
            else { HideTaskbarCore(animate); }
        }

        void HideTaskbarCore(bool animate)
        {
            if (_taskbarAnimating) return;
            double hiddenTop = _isBottom
                ? SystemParameters.PrimaryScreenHeight + 4
                : -(TASKBAR_HEIGHT + 4);
            if (animate)
            {
                _taskbarAnimating = true;
                double from = Top, to = hiddenTop;
                var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ANIM_MS))
                { EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop };
                a.Completed += (s, e) => { BeginAnimation(TopProperty, null); Top = to; _taskbarAnimating = false; Visibility = Visibility.Hidden; };
                BeginAnimation(TopProperty, a);
            }
            else { Visibility = Visibility.Hidden; Top = hiddenTop; }
        }

        void ShowTaskbar(bool animate = false, Action onComplete = null, bool noActivate = false)
        {
            if (_taskbarAnimating) return;
            Topmost = true;
            // [ATTENTION-NOACTIVATE] Если noActivate=true — не крадём фокус у игры
            if (!noActivate)
            {
                // Забираем фокус у игры через AttachThreadInput — снимает Raw Input CS2/TF2/Minecraft
                StealFocusFromGame();
            }
            var src2 = PresentationSource.FromVisual(this);
            double dpi = src2?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double screenPx2 = Math.Round(SystemParameters.PrimaryScreenWidth * dpi);
            double panelPx2 = Math.Round(ActualWidth * dpi);
            Left = Math.Floor((screenPx2 - panelPx2) / 2.0) / dpi;
            SafeRun(ApplyAcrylicBackground, "ApplyAcrylicBackground_Show");
            double visibleTop = _isBottom ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT : 0;
            double hiddenTop  = _isBottom ? SystemParameters.PrimaryScreenHeight + 4 : -(TASKBAR_HEIGHT + 4);
            if (animate)
            {
                _taskbarAnimating = true;
                Top = hiddenTop; Visibility = Visibility.Visible;
                var a = new DoubleAnimation(hiddenTop, visibleTop, TimeSpan.FromMilliseconds(ANIM_MS))
                { EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
                a.Completed += (s, e) => { BeginAnimation(TopProperty, null); Top = visibleTop; _taskbarAnimating = false; onComplete?.Invoke(); };
                BeginAnimation(TopProperty, a);
            }
            else { Visibility = Visibility.Visible; Top = visibleTop; onComplete?.Invoke(); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // ГРУППЫ ОКОН [STAB-2] [FIX-6] [FIX-8] [FIX-9]
        // ═════════════════════════════════════════════════════════════════════
        void UpdateWindowGroups()
        {
            // [STAB-6] Не запускать новый тик если предыдущий ещё идёт
            if (System.Threading.Interlocked.CompareExchange(ref _enumBusyInt, 1, 0) != 0) return;
            try { UpdateWindowGroupsCore(); }
            finally { System.Threading.Interlocked.Exchange(ref _enumBusyInt, 0); }
        }

        void UpdateWindowGroupsCore()
        {
            if (_resumingFromSleep) return;

            IntPtr shellWindow = GetShellWindow();
            IntPtr myHwnd;
            try { myHwnd = new WindowInteropHelper(this).Handle; } catch { myHwnd = IntPtr.Zero; }

            var fresh = new Dictionary<string, List<IntPtr>>(16, StringComparer.OrdinalIgnoreCase);

            EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    if (hWnd == shellWindow || hWnd == myHwnd) return true;
                    if (!IsWindowVisible(hWnd)) return true;

                    // [FIX-GHOST-v3] Cloaked-окна — это ghost-призраки UWP (calculator, settings и др.).
                    // Windows держит их в памяти заранее; DWM помечает их CLOAKED=2 (shell).
                    // Это надёжнее WS_EX_NOACTIVATE и работает после sleep/resume.
                    try
                    {
                        if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                            return true;
                    }
                    catch { /* dwmapi unavailable — continuing without filter */ }

                    // [STAB-2] Пропуск зависших окон — не блокируемся
                    try { if (IsHungAppWindow(hWnd)) return true; } catch { return true; }
                    // [STAB-11] Проверка валидности
                    if (!IsWindow(hWnd)) return true;

                    // [STAB-4] Безопасный GetWindowText
                    string title = SafeGetWindowText(hWnd);
                    if (string.IsNullOrWhiteSpace(title) || title == "Program Manager") return true;

                    // [FIX-D3DPROXY] Фильтр по window class — убирает служебные/proxy-окна
                    // (D3DProxyWindow, GameOverlayWindow и т.п.) которые рендер-движки и Steam
                    // создают как технические контейнеры. Они не являются окнами приложения.
                    {
                        string wndClass = GetWindowClass(hWnd);
                        if (!string.IsNullOrEmpty(wndClass) && IgnoredWindowClasses.Contains(wndClass))
                            return true;
                    }

                    int style = GetWindowLong(hWnd, GWL_STYLE);
                    if ((style & WS_CHILD) != 0) return true;
                    int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);

                    GetWindowThreadProcessId(hWnd, out uint winPid);
                    if (winPid == 0) return true;

                    // [STAB-8] Безопасное получение имени процесса
                    if (!_pidNameCache.TryGetValue(winPid, out string exeName))
                    {
                        exeName = SafeGetProcessName(winPid);
                        if (string.IsNullOrEmpty(exeName)) return true;
                        // [STAB-12] Ограничение размера кэша
                        if (_pidNameCache.Count < 256)
                            _pidNameCache.TryAdd(winPid, exeName);
                    }

                    if (string.IsNullOrEmpty(exeName)) return true;
                    if (IgnoredProcesses.Contains(exeName)) return true;
                    if (ProcessAliases.TryGetValue(exeName, out string alias)) exeName = alias;

                    if (exeName != "applicationframehost"
                        && IsUwpAppName(exeName)
                        && !DirectUwpProcesses.Contains(exeName))
                        return true;

                    // [FIX-GHOST-v3] Ghost-фильтр для прямых UWP-процессов (calculatorapp, systemsettings и др.)
                    // Windows создаёт их заранее в фоне с флагом WS_EX_NOACTIVATE — снимает при реальном открытии.
                    // Ранее этот фильтр применялся только в ветке applicationframehost — теперь и здесь.
                    if (IsUwpAppName(exeName) && !DirectUwpProcesses.Contains(exeName))
                    {
                        int directExStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                        if ((directExStyle & WS_EX_NOACTIVATE) != 0) return true;
                    }

                    if (exeName == "applicationframehost")
                    {
                        string u = ResolveUwpAppName(title);
                        if (!string.IsNullOrEmpty(u))
                        {
                            // [FIX-GHOST v2] Ghost-окна UWP (calculator, systemsettings и др.)
                            // создаются Windows заранее в фоне с флагом WS_EX_NOACTIVATE.
                            // Пока пользователь не открыл приложение — этот флаг стоит.
                            // При реальном открытии Windows снимает WS_EX_NOACTIVATE.
                            // Фильтруем по этому флагу вместо полного игнора через IgnoredProcesses.
                            int afExStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                            if ((afExStyle & WS_EX_NOACTIVATE) != 0) return true;

                            if (IgnoredProcesses.Contains(u)) return true;
                            if (!IsWindowVisible(hWnd)) return true;
                            if (!IsIconic(hWnd))
                            {
                                // [STAB-11]
                                if (!IsWindow(hWnd)) return true;
                                if (!GetWindowRect(hWnd, out RECT wr)) return true;
                                if (wr.right - wr.left < 50 || wr.bottom - wr.top < 50) return true;
                            }
                            if (!HasLiveUwpChild(hWnd)) return true;
                            exeName = u;
                        }
                        else return true;
                    }

                    if (DirectUwpProcesses.Contains(exeName))
                    {
                        string canonical = exeName.ToLowerInvariant();
                        if (canonical == "microsoft.photos") canonical = "photos";
                        exeName = canonical;
                    }

                    bool forced = ForceShowProcesses.Contains(exeName);
                    bool isToolWin = (exStyle & WS_EX_TOOLWINDOW) != 0;
                    bool isAppWin = (exStyle & WS_EX_APPWINDOW) != 0;
                    if (!forced && isToolWin && !isAppWin) return true;

                    if (!IsIconic(hWnd))
                    {
                        if (!IsWindow(hWnd)) return true;
                        if (GetWindowRect(hWnd, out RECT sc) && (sc.right - sc.left < 50 || sc.bottom - sc.top < 50))
                            return true;
                    }

                    // [PER-WINDOW] Для браузеров с несколькими профилями каждое окно
                    // получает уникальный ключ группы — как в оригинальном Win10.
                    bool isPerWindow = PerWindowProcesses.Contains(exeName);
                    string groupKey = isPerWindow ? (exeName + ":" + hWnd.ToString("X")) : exeName;

                    if (!fresh.ContainsKey(groupKey)) fresh[groupKey] = new List<IntPtr>(2);
                    fresh[groupKey].Add(hWnd);

                    if (!_groups.ContainsKey(groupKey))
                    {
                        // [ICON-HASH] Для per-window: проверяем хэш иконки ДО задержки подтверждения.
                        // Если совпадает с существующей группой — мгновенный merge, без 1800мс ожидания.
                        if (isPerWindow)
                        {
                            _pidPathCache.TryGetValue(winPid, out string epEarly);
                            BitmapSource iconEarly = GetCachedIconSafe(hWnd, winPid, epEarly, groupKey);
                            if (iconEarly != null)
                            {
                                string hashEarly = ComputeIconHash(iconEarly);
                                if (hashEarly != null
                                    && _iconHashToGroupKey.TryGetValue(hashEarly, out string existingKeyEarly)
                                    && _groups.TryGetValue(existingKeyEarly, out AppGroup existingGroupEarly))
                                {
                                    if (!fresh.ContainsKey(existingKeyEarly)) fresh[existingKeyEarly] = new List<IntPtr>(2);
                                    if (!fresh[existingKeyEarly].Contains(hWnd)) fresh[existingKeyEarly].Add(hWnd);
                                    fresh.Remove(groupKey);
                                    _pendingNewGroups.Remove(groupKey);
                                    _groups[groupKey] = existingGroupEarly;
                                    Debug.WriteLine($"[MyTaskbar][ICON-HASH] {exeName} HWND={hWnd:X} → fast-merged into {existingKeyEarly}");
                                    return true;
                                }
                            }
                        }

                        // [FIX-9] Задержка подтверждения
                        if (!_pendingNewGroups.TryGetValue(groupKey, out DateTime firstSeen))
                        {
                            _pendingNewGroups[groupKey] = DateTime.UtcNow;
                            return true;
                        }
                        if ((DateTime.UtcNow - firstSeen).TotalMilliseconds < NEW_GROUP_CONFIRM_MS)
                            return true;

                        _pendingNewGroups.Remove(groupKey);
                        _pidPathCache.TryGetValue(winPid, out string ep);
                        bool uwpIcon = IsUwpAppName(exeName);
                        // [STAB-7] Быстрый синхронный путь — только HICON
                        // [PER-WINDOW-ICON] передаём groupKey чтобы per-window группы
                        // кэшировались по HWND (у каждого профиля Chrome — своя иконка)
                        BitmapSource icon = uwpIcon
                            ? GetCachedUwpIcon(exeName)
                            : GetCachedIconSafe(hWnd, winPid, ep, groupKey);

                        // [ICON-HASH] Для per-window процессов проверяем хэш иконки.
                        // Вытащенная вкладка создаёт новый HWND, но иконка профиля та же —
                        // добавляем окно в существующую группу, а не создаём новую кнопку.
                        if (isPerWindow && icon != null)
                        {
                            string iconHash = ComputeIconHash(icon);
                            if (iconHash != null
                                && _iconHashToGroupKey.TryGetValue(iconHash, out string existingKey)
                                && _groups.TryGetValue(existingKey, out AppGroup existingGroup))
                            {
                                // Совпадение по иконке — добавляем HWND в fresh[existingKey],
                                // чтобы основной цикл обновил Hwnds и превью увидело окно.
                                if (!fresh.ContainsKey(existingKey)) fresh[existingKey] = new List<IntPtr>(2);
                                if (!fresh[existingKey].Contains(hWnd)) fresh[existingKey].Add(hWnd);
                                // Убираем новый groupKey из fresh — он не нужен (нет своей группы)
                                fresh.Remove(groupKey);
                                _pendingNewGroups.Remove(groupKey);
                                // Алиас: чтобы при следующих энумерациях этот HWND шёл через ветку else
                                _groups[groupKey] = existingGroup;
                                Debug.WriteLine($"[MyTaskbar][ICON-HASH] {exeName} HWND={hWnd:X} → merged into {existingKey}");
                                return true; // кнопка не создаётся
                            }

                            // Новый профиль — регистрируем хэш
                            string newHash = iconHash;
                            var g2 = new AppGroup { ExeName = exeName, GroupKey = groupKey, Icon = icon, LastTitle = title, IsUwp = uwpIcon, IconHash = newHash };
                            if (newHash != null) _iconHashToGroupKey[newHash] = groupKey;
                            var b2 = CreateAppButton(title, icon, 24);
                            SetupGroupButton(b2, g2); g2.Button = b2; _groups[groupKey] = g2;
                            try { AddButtonAnimated(b2); } catch { }
                            if (icon == null) LoadIconAsync(g2, hWnd, winPid, ep);
                            return true;
                        }

                        var g = new AppGroup { ExeName = exeName, GroupKey = groupKey, Icon = icon, LastTitle = title, IsUwp = uwpIcon };
                        var b = CreateAppButton(title, icon, uwpIcon ? 24 : 24);
                        SetupGroupButton(b, g); g.Button = b; _groups[groupKey] = g;
                        try { AddButtonAnimated(b); } catch { }
                        // [STAB-7] Shell-иконки догружаем асинхронно
                        if (icon == null) LoadIconAsync(g, hWnd, winPid, ep);
                    }
                    else
                    {
                        var g = _groups[groupKey];

                        // [ICON-HASH] Алиас — просто пропускаем, слияние fresh делается после EnumWindows
                        string realKey = g.GroupKey ?? g.ExeName;
                        if (realKey != groupKey) return true;

                        if (g.Icon == null)
                        {
                            _pidPathCache.TryGetValue(winPid, out string ep);
                            LoadIconAsync(g, hWnd, winPid, ep); // [STAB-7]
                        }
                        else if (isPerWindow)
                        {
                            // [PER-WINDOW-ICON] Для браузерных профилей периодически
                            // проверяем не сменилась ли иконка (смена аватара профиля Chrome).
                            // Проверяем через WM_GETICON — быстро, без блокировки UI.
                            string hwndKey = $"hwnd:{hWnd.ToString("X")}";
                            if (!_iconCache.ContainsKey(hwndKey))
                            {
                                // Иконки для этого HWND ещё нет в кэше — загружаем
                                _pidPathCache.TryGetValue(winPid, out string ep2);
                                g.Icon = null; // сбросим чтобы LoadIconAsync отработал
                                LoadIconAsync(g, hWnd, winPid, ep2);
                            }
                        }
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] EnumWindows inner: {ex.Message}"); }
                return true;
            }, IntPtr.Zero);

            // [FIX-6] UWP дедупликация с защитой от IntPtr.Zero
            foreach (var key in fresh.Keys.ToList())
            {
                if (!IsUwpAppName(key)) continue;
                var list = fresh[key];
                if (list.Count <= 1) continue;
                try
                {
                    IntPtr best = IntPtr.Zero;
                    foreach (var h in list)
                    {
                        if (h == IntPtr.Zero || !IsWindow(h)) continue;
                        IntPtr core = FindWindowEx(h, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                        if (core == IntPtr.Zero)
                            core = FindWindowEx(h, IntPtr.Zero, "ApplicationFrameInputSinkWindow", null);
                        if (core != IntPtr.Zero) { best = h; break; }
                    }
                    if (best == IntPtr.Zero)
                        best = list.FirstOrDefault(h => h != IntPtr.Zero && IsWindow(h) && !IsIconic(h));
                    if (best == IntPtr.Zero)
                        best = list.FirstOrDefault(h => h != IntPtr.Zero && IsWindow(h));
                    if (best != IntPtr.Zero) fresh[key] = new List<IntPtr> { best };
                    else fresh.Remove(key);
                }
                catch { if (list.Count > 0) fresh[key] = new List<IntPtr> { list[0] }; }
            }

            // [FIX-9] Чистим pending от умерших окон
            foreach (var key in _pendingNewGroups.Keys.ToList())
                if (!fresh.ContainsKey(key)) _pendingNewGroups.Remove(key);

            // [STAB-12] Очистка PID-кэша по размеру
            if (_pidNameCache.Count > 200) { _pidNameCache.Clear(); _pidPathCache.Clear(); }

            // [ICON-HASH] Слияние fresh: для каждого алиаса переносим его HWND в fresh реального ключа
            foreach (var kvp in _groups)
            {
                try
                {
                    var g = kvp.Value;
                    string realKey = g.GroupKey ?? g.ExeName;
                    if (realKey == kvp.Key) continue; // не алиас
                    if (!fresh.TryGetValue(kvp.Key, out var aliasList)) continue;
                    if (!fresh.ContainsKey(realKey)) fresh[realKey] = new List<IntPtr>(aliasList.Count);
                    foreach (var h in aliasList)
                        if (!fresh[realKey].Contains(h)) fresh[realKey].Add(h);
                    fresh.Remove(kvp.Key);
                }
                catch { }
            }

            bool needLayout = false;
            var toRemove = new List<string>(4);
            var aliasesToRemove = new List<string>(4); // [ICON-HASH] алиасы без живого HWND
            foreach (var kvp in _groups)
            {
                try
                {
                    var g = kvp.Value;
                    // [ICON-HASH] Алиасы (groupKey != g.GroupKey) — пропускаем обновление Hwnds:
                    // их Hwnds управляются через основную группу (g.GroupKey).
                    if (kvp.Key != (g.GroupKey ?? g.ExeName))
                    {
                        // [ICON-HASH] Алиас живёт пока его HWND существует.
                        // fresh уже слит в realKey, поэтому проверяем IsWindow напрямую.
                        // Извлекаем HWND из ключа вида "exeName:HEXHWND"
                        bool hwndAlive = false;
                        try
                        {
                            int colon = kvp.Key.LastIndexOf(':');
                            if (colon >= 0 && IntPtr.Size == 8)
                            {
                                if (long.TryParse(kvp.Key.Substring(colon + 1), System.Globalization.NumberStyles.HexNumber, null, out long h))
                                    hwndAlive = IsWindow(new IntPtr(h));
                            }
                            else if (colon >= 0)
                            {
                                if (int.TryParse(kvp.Key.Substring(colon + 1), System.Globalization.NumberStyles.HexNumber, null, out int h))
                                    hwndAlive = IsWindow(new IntPtr(h));
                            }
                        }
                        catch { }
                        if (!hwndAlive) aliasesToRemove.Add(kvp.Key);
                        continue;
                    }
                    g.Hwnds = fresh.TryGetValue(kvp.Key, out var list2) ? list2 : new List<IntPtr>(0);
                    if (g.Hwnds.Count == 0 && !g.IsPinned) toRemove.Add(kvp.Key);
                    else UpdateGroupBadge(g);
                }
                catch { }
            }
            foreach (var aliasKey in aliasesToRemove)
                try { _groups.Remove(aliasKey); } catch { }
            foreach (var exe in toRemove)
            {
                try
                {
                    if (_groups.TryGetValue(exe, out var g))
                    {
                        if (g.IconHash != null) _iconHashToGroupKey.Remove(g.IconHash); // [ICON-HASH]
                        _groups.Remove(exe); needLayout = true; RemoveButtonAnimated(g.Button, g);
                    }
                }
                catch { }
            }
            if (needLayout) try { PositionTaskbar(); } catch { }
        }

        static bool HasLiveUwpChild(IntPtr frameHwnd)
        {
            if (frameHwnd == IntPtr.Zero || !IsWindow(frameHwnd)) return false;
            IntPtr child = FindWindowEx(frameHwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (child != IntPtr.Zero && IsWindow(child)) return true;
            child = FindWindowEx(frameHwnd, IntPtr.Zero, "ApplicationFrameInputSinkWindow", null);
            return child != IntPtr.Zero && IsWindow(child);
        }

        // ═════════════════════════════════════════════════════════════════════
        // BRIGHTNESS
        // ═════════════════════════════════════════════════════════════════════
        void StartBrightnessIconTracker()
        {
            UpdateBrightnessIcon();
            _brightnessIconTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _brightnessIconTimer.Tick += (s, e) => SafeRun(UpdateBrightnessIcon, "UpdateBrightnessIcon");
            _brightnessIconTimer.Start();
        }

        void UpdateBrightnessIcon()
        {
            try
            {
                if (BrightnessButton == null) return;
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    int b = BrightnessHelper.GetBuiltInBrightness();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { if (BrightnessButton != null) BrightnessButton.ToolTip = b >= 0 ? $"Brightness: {b}%" : "Brightness"; } catch { }
                    }));
                });
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UpdateBrightnessIcon: {ex.Message}"); }
        }

        void BrightnessButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MarkOwnActivity();
                if ((DateTime.UtcNow - _brightnessClosedAt).TotalMilliseconds < 300) return;

                if (_brightnessFlyout == null)
                {
                    _brightnessFlyout = new BrightnessFlyoutWindow(); _brightnessFlyout.UIScale = _uiScale;
                    _brightnessFlyout.IsVisibleChanged += (s2, ev) =>
                    {
                        if (!(bool)ev.NewValue)
                        {
                            _brightnessClosedAt = DateTime.UtcNow;
                            UpdateBrightnessIcon();
                        }
                    };
                }

                if (_brightnessFlyout.IsVisible)
                {
                    _brightnessFlyout.Hide();
                    return;
                }

                var btn = BrightnessButton;
                var pos = btn.PointToScreen(new System.Windows.Point(0, 0));
                double bfW = _brightnessFlyout.Width > 0 ? _brightnessFlyout.Width : 220;
                double bfLeft = pos.X + btn.ActualWidth / 2 - bfW / 2;
                double bfTopFallback = _isBottom
                    ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - 80 - 4
                    : TASKBAR_HEIGHT + 4;
                bool isBot = _isBottom;
                _brightnessFlyout.ShowAt(bfLeft, bfTopFallback, actualH =>
                    isBot
                        ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - actualH - 4
                        : TASKBAR_HEIGHT + 4);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] BrightnessButton_Click: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // VOLUME
        // ═════════════════════════════════════════════════════════════════════
        void StartVolumeIconTracker()
        {
            UpdateVolumeIcon();
            _volumeIconTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _volumeIconTimer.Tick += (s, e) => SafeRun(UpdateVolumeIcon, "UpdateVolumeIcon");
            _volumeIconTimer.Start();
        }

        void UpdateVolumeIcon()
        {
            try
            {
                if (VolumeIcon == null) return;
                bool muted = AudioHelper.IsMuted();
                int vol = AudioHelper.GetVolume();
                string icon;
                if (muted || vol == 0) icon = "\uE74F";
                else if (vol < 33) icon = "\uE993";
                else if (vol < 66) icon = "\uE994";
                else icon = "\uE767";
                VolumeIcon.Text = icon;
                if (VolumeButton != null) VolumeButton.ToolTip = muted ? "Muted" : $"Volume: {vol}%";
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UpdateVolumeIcon: {ex.Message}"); }
        }

        // [VOL-RIGHTCLICK] ПКМ на иконке громкости: микшер и настройки звука
        void VolumeButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                MarkOwnActivity();
                var menu = new ContextMenu { Style = TryFindResource("DarkContextMenu") as Style };

                var mixerItem = MakeMenuItem("Открыть микшер громкости");
                mixerItem.Click += (s2, e2) =>
                {
                    try { Process.Start(new ProcessStartInfo("sndvol.exe") { UseShellExecute = true }); }
                    catch { try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); } catch { } }
                };
                menu.Items.Add(mixerItem);

                var soundItem = MakeMenuItem("Звук (настройки устройств)");
                soundItem.Click += (s2, e2) =>
                {
                    try { Process.Start(new ProcessStartInfo("mmsys.cpl") { UseShellExecute = true }); } catch { }
                };
                menu.Items.Add(soundItem);

                var settingsItem = MakeMenuItem("Параметры звука");
                settingsItem.Click += (s2, e2) =>
                {
                    try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); } catch { }
                };
                menu.Items.Add(settingsItem);

                menu.PlacementTarget = VolumeButton;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                menu.IsOpen = true;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] VolumeButton_RightClick: {ex.Message}"); }
        }

        void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MarkOwnActivity();
                if ((DateTime.UtcNow - _volumeClosedAt).TotalMilliseconds < 300) return;

                if (_volumeFlyout == null)
                {
                    _volumeFlyout = new VolumeFlyoutWindow(); _volumeFlyout.UIScale = _uiScale;
                    _volumeFlyout.IsVisibleChanged += (s2, ev) =>
                    {
                        if (!(bool)ev.NewValue)
                        {
                            _volumeClosedAt = DateTime.UtcNow;
                            UpdateVolumeIcon();
                        }
                    };
                    _volumeFlyout.SizeChanged += (s2, ev) =>
                    {
                        if (!_volumeFlyout.IsVisible) return;
                        double h = _volumeFlyout.ActualHeight;
                        if (h <= 0) return;
                        _volumeFlyout.Top = _isBottom
                            ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - h - 4
                            : TASKBAR_HEIGHT + 4;
                    };
                }

                if (_volumeFlyout.IsVisible)
                {
                    _volumeFlyout.Hide();
                    return;
                }

                var btn = VolumeButton;
                var pos = btn.PointToScreen(new System.Windows.Point(0, 0));
                double vfW = _volumeFlyout.Width > 0 ? _volumeFlyout.Width : 220;
                _volumeFlyout.Left = pos.X + btn.ActualWidth / 2 - vfW / 2;
                _volumeFlyout.Top = -9999;
                _volumeFlyout.Show();
                // Пересчитываем Top после завершения layout
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    double h = _volumeFlyout.ActualHeight > 0 ? _volumeFlyout.ActualHeight : 80;
                    _volumeFlyout.Top = _isBottom
                        ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - h - 4
                        : TASKBAR_HEIGHT + 4;
                }), System.Windows.Threading.DispatcherPriority.Render);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] VolumeButton_Click: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // MENU
        // ═════════════════════════════════════════════════════════════════════
        void EnsureMenuWindow()
        {
            if (_menuWindow != null) return;
            _menuWindow = new MenuWindow(); _menuWindow.UIScale = _uiScale;
            _menuWindow.MenuVisibilityChanged += isOpen =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SetStartButtonHighlight(isOpen);
                    // ClipCursor трогаем ТОЛЬКО если блокер активен (FPS-режим).
                    // Без активного блокера курсор свободен — не ограничиваем его.
                    if (_blockerWindow == null || !_blockerWindow.IsBlockerActive) return;
                    if (isOpen)
                    {
                        // ClipCursor расширяется позже в MenuPositionReady —
                        // когда меню достигнет финальной позиции после анимации.
                        _blockerWindow.MenuWindow = _menuWindow;
                    }
                    else
                    {
                        if (_appOpenedOverFullscreen) return;
                        // Меню закрылось — сужаем зону обратно до одного тулбара
                        _blockerWindow.MenuWindow = null;
                        _blockerWindow.UpdateClipZone();
                    }
                }));
            // [GAME-9] Приложение запущено из меню Пуск — снимаем блокер
            _menuWindow.AppLaunched += () =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SuspendBlockerForAppWindow();
                }));
            // [CLIP-FIX] Меню достигло финальной позиции — расширяем ClipCursor
            // только если блокер активен (FPS-режим)
            _menuWindow.MenuPositionReady += () =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_blockerWindow == null || !_blockerWindow.IsBlockerActive) return;
                    _blockerWindow.MenuWindow = _menuWindow;
                    _blockerWindow.UpdateClipZone();
                }));
            _menuWindow.IsVisibleChanged += (s, e) =>
            {
                if (!(bool)e.NewValue) Dispatcher.BeginInvoke(new Action(() => SetStartButtonHighlight(false)));
            };
            // [BLUR-FIX-2] Після повного завершення анімації ховання меню (меню фізично за екраном)
            // робимо Disable→Enable toggle blur на панелі — це єдиний надійний спосіб
            // примусити DWM скинути залишковий blur-артефакт.
            _menuWindow.MenuHideCompleted += () =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        AcrylicHelper.Disable(this);
                        ApplyAcrylicBackground();
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.Render);
            _menuWindow.TaskbarWindow = this;
            _menuWindow.PreviewWindow = _previewWindow;
        }

        void SetStartButtonHighlight(bool active)
        {
            try
            {
                if (StartButton == null) return;
                StartButton.ApplyTemplate();
                var ab = StartButton.Template?.FindName("AppBorder", StartButton) as Border;
                if (ab != null)
                {
                    ab.Background = active ? BrushActiveBg : BrushTransparent;
                    var rb = StartButton.Template?.FindName("RunBar", StartButton) as Rectangle;
                    if (rb != null) rb.Visibility = Visibility.Collapsed;
                }
                else
                    StartButton.Background = active
                        ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255))
                        : Brushes.Transparent;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] SetStartButtonHighlight: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // АНИМАЦИЯ КНОПОК
        // ═════════════════════════════════════════════════════════════════════
        void AddButtonAnimated(Button btn)
        {
            btn.Width = 40; btn.Opacity = 0;
            EnsureTranslateTransform(btn).Y = -28;
            AppIcons.Children.Add(btn);
            btn.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                var slideIn = new DoubleAnimation { From = -28, To = 0, Duration = TimeSpan.FromMilliseconds(BTN_SLIDE_IN_MS), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                var fadeIn = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(BTN_SLIDE_IN_MS), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                slideIn.Completed += (s, e) =>
                {
                    try
                    {
                        btn.BeginAnimation(OpacityProperty, null);
                        EnsureTranslateTransform(btn).BeginAnimation(TranslateTransform.YProperty, null);
                        btn.Opacity = 1; EnsureTranslateTransform(btn).Y = 0; PositionTaskbar();
                    }
                    catch { }
                };
                btn.BeginAnimation(OpacityProperty, fadeIn);
                EnsureTranslateTransform(btn).BeginAnimation(TranslateTransform.YProperty, slideIn);
            }));
        }

        void RemoveButtonAnimated(Button btn, AppGroup group)
        {
            if (btn == null || _removingButtons.Contains(btn)) return;
            _removingButtons.Add(btn);
            var slideOut = new DoubleAnimation { From = 0, To = -28, Duration = TimeSpan.FromMilliseconds(BTN_SLIDE_OUT_MS), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            var fadeOut = new DoubleAnimation { From = btn.Opacity, To = 0, Duration = TimeSpan.FromMilliseconds(BTN_SLIDE_OUT_MS), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            slideOut.Completed += (s, e) =>
            {
                try
                {
                    btn.BeginAnimation(OpacityProperty, null);
                    EnsureTranslateTransform(btn).BeginAnimation(TranslateTransform.YProperty, null);
                    AppIcons.Children.Remove(btn); _buttonStateCache.Remove(btn); _removingButtons.Remove(btn); PositionTaskbar();
                }
                catch { }
            };
            btn.BeginAnimation(OpacityProperty, fadeOut);
            EnsureTranslateTransform(btn).BeginAnimation(TranslateTransform.YProperty, slideOut);
        }

        static TranslateTransform EnsureTranslateTransform(UIElement el)
        {
            if (el.RenderTransform is TranslateTransform tt) return tt;
            var t = new TranslateTransform(0, 0);
            el.RenderTransform = t; el.RenderTransformOrigin = new Point(0.5, 0.5);
            return t;
        }

        // ═════════════════════════════════════════════════════════════════════
        // KEYBOARD HOOK
        // ═════════════════════════════════════════════════════════════════════
        void InstallKeyboardHook()
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_keyboardHook); } catch { }
                _keyboardHook = IntPtr.Zero;
            }
            _keyboardProc = KeyboardHookCallback;
            using (var p = Process.GetCurrentProcess())
            using (var m = p.MainModule)
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(m.ModuleName), 0);
            if (_keyboardHook == IntPtr.Zero) Debug.WriteLine("[MyTaskbar] Hook FAILED: " + Marshal.GetLastWin32Error());
            else Debug.WriteLine("[MyTaskbar] Hook installed OK");

            // [FIX-HOOK-WATCHDOG] Переустанавливаем хук если Windows его убил (LowLevelHooksTimeout).
            // Это происходит когда callback не успевает вернуться за ~300 мс (реестровый таймаут).
            if (_hookWatchdog == null)
            {
                _hookWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _hookWatchdog.Tick += (s, e) =>
                {
                    if (_keyboardHook == IntPtr.Zero)
                    {
                        Debug.WriteLine("[MyTaskbar] Hook watchdog: hook is dead, reinstalling...");
                        _winKeyDown = false; _winUsedInCombo = false;
                        _shiftDown = false; _ctrlDown = false; _altDown = false; _injectingWin = false;
                        InstallKeyboardHook();
                    }
                };
                _hookWatchdog.Start();
            }
        }

        IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            if (_injectingWin) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            int vk = Marshal.ReadInt32(lParam);
            int msg = (int)wParam;
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            bool isWin = vk == VK_LWIN || vk == VK_RWIN;

            void InjectWinDownOnce()
            {
                if (_winUsedInCombo) return;
                _winUsedInCombo = true; _injectingWin = true;
                try { keybd_event(0x5B, 0, 0, UIntPtr.Zero); } finally { _injectingWin = false; }
            }

            if (vk == VK_SHIFT || vk == 0xA0 || vk == 0xA1) { _shiftDown = isDown; if (_winKeyDown && isDown) InjectWinDownOnce(); return CallNextHookEx(_keyboardHook, nCode, wParam, lParam); }
            if (vk == VK_CONTROL || vk == 0xA2 || vk == 0xA3) { _ctrlDown = isDown; if (_winKeyDown && isDown) InjectWinDownOnce(); return CallNextHookEx(_keyboardHook, nCode, wParam, lParam); }
            if (vk == VK_MENU || vk == 0xA4 || vk == 0xA5) { _altDown = isDown; if (_winKeyDown && isDown) InjectWinDownOnce(); return CallNextHookEx(_keyboardHook, nCode, wParam, lParam); }


            if (isWin)
            {
                if (isDown)
                {
                    if (!_winKeyDown)
                    {
                        _winKeyDown = true; _winUsedInCombo = false;
                        // [FIX-MODIFIER-SYNC] Синхронизируем реальное состояние модификаторов через
                        // GetAsyncKeyState, т.к. после диспетчера задач (Ctrl+Shift+Esc) keyup мог
                        // не дойти до хука — _ctrlDown/_shiftDown могли застрять в true.
                        // Если они застряли: InjectWinDownOnce() → _winUsedInCombo=true → Win-up
                        // не блокируется → система открывает оригинальный Пуск.
                        _shiftDown = (GetAsyncKeyState(0xA0) & 0x8000) != 0 || (GetAsyncKeyState(0xA1) & 0x8000) != 0;
                        _ctrlDown = (GetAsyncKeyState(0xA2) & 0x8000) != 0 || (GetAsyncKeyState(0xA3) & 0x8000) != 0;
                        _altDown = (GetAsyncKeyState(0xA4) & 0x8000) != 0 || (GetAsyncKeyState(0xA5) & 0x8000) != 0;
                        if (_shiftDown || _ctrlDown || _altDown) InjectWinDownOnce();
                    }
                    return (IntPtr)1;
                }
                if (isUp)
                {
                    bool wasCombo = _winUsedInCombo;
                    _winKeyDown = false; _winUsedInCombo = false;
                    if (wasCombo) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(OnWinKeyDown));
                    return (IntPtr)1;
                }
            }
            if (_winKeyDown && isDown) InjectWinDownOnce();

            // Shift+Alt+O — open Settings window
            if (isDown && vk == 0x4F && _shiftDown && _altDown && !_winKeyDown)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(OpenSettingsWindow));
                return (IntPtr)1; // consume keypress
            }

            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        void OnWinKeyDown()
        {
            try
            {
                MarkOwnActivity();
                // [FIX-MODIFIER-SYNC] Дополнительный сброс модификаторов здесь — на случай race condition.
                // Если _ctrlDown/_shiftDown застряли, это предотвращает ложную логику комбо.
                _shiftDown = (GetAsyncKeyState(0xA0) & 0x8000) != 0 || (GetAsyncKeyState(0xA1) & 0x8000) != 0;
                _ctrlDown = (GetAsyncKeyState(0xA2) & 0x8000) != 0 || (GetAsyncKeyState(0xA3) & 0x8000) != 0;
                _altDown = (GetAsyncKeyState(0xA4) & 0x8000) != 0 || (GetAsyncKeyState(0xA5) & 0x8000) != 0;
                TryCloseSystemStartMenu();
                EnsureMenuWindow();
                if (_menuWindow.JustHidden) return;
                if (_menuWindow.IsVisible) { _menuWindow.HideAnimated(); return; }
                // Забираем фокус у игры при вызове меню Win-клавишей
                StealFocusFromGame();
                if (_isHiddenByFullscreen)
                {
                    _isHiddenByFullscreen = false; _fullscreenConfirmCount = 0; _notFullscreenConfirmCount = 0;
                    bool fps = IsCursorCapturedByFpsGame();
                    // [GAME-3] В fullscreen показываем ТОЛЬКО панель задач — без меню Пуск.
                    // Если игра захватила курсор (FPS) — добавляем прозрачный блокер,
                    // чтобы курсор не мог уйти обратно в игру.
                    ShowTaskbar(animate: true, onComplete: () =>
                    {
                        if (fps) ShowFullscreenBlocker();
                    });
                }
                else
                {
                    // [GAME-10] Если игра fullscreen — меню должно быть Topmost,
                    // иначе после нескольких открытий оно уходит под игру.
                    bool fps = IsCursorCapturedByFpsGame();
                    _menuWindow.IsFullscreenMode = IsForegroundFullscreen() || fps;
                    // [GAME-11] Если FPS захватил курсор — блокер должен быть активен
                    // чтобы ClipCursor держал курсор на весь экран поверх игры.
                    // Без этого после клика в игру (HideFullscreenBlocker) блокер скрыт,
                    // и при повторном открытии меню FPS перехватывает курсор везде кроме панели+меню.
                    if (fps) ShowFullscreenBlocker();
                    _menuWindow.IsBottom = _isBottom;
                    _menuWindow.ShowMenu();
                    // [FIX-TASKMGR-STARTMENU] Если диспетчер задач был в фокусе —
                    // Shell открывает оригинальный Пуск через IPC с задержкой даже
                    // после того как hook поглотил Win-key. Закрываем Пуск повторно
                    // с задержкой 180мс, когда он уже гарантированно открылся.
                    if (IsTaskmgrForeground())
                        TryCloseSystemStartMenuDelayed(180);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] OnWinKeyDown: {ex.Message}"); }
        }

        void MarkOwnActivity() => _ownWindowActivityAt = DateTime.UtcNow;
        bool IsInOwnActivityGrace() => (DateTime.UtcNow - _ownWindowActivityAt).TotalMilliseconds < OWN_ACTIVITY_GRACE_MS;

        bool IsCursorOverOwnWindows()
        {
            try
            {
                if (!GetCursorPos(out POINT cp)) return false;
                double cx = cp.x, cy = cp.y;
                if (IsPointOverWindow(this, cx, cy)) return true;
                if (_menuWindow != null && _menuWindow.IsVisible && IsPointOverWindow(_menuWindow, cx, cy)) return true;
                if (_previewWindow != null && _previewWindow.IsVisible && IsPointOverWindow(_previewWindow, cx, cy)) return true;
                if (_trayWindow != null && _trayWindow.IsOpen && IsPointOverWindow(_trayWindow, cx, cy)) return true;
                if (_wifiWindow != null && _wifiWindow.IsVisible && IsPointOverWindow(_wifiWindow, cx, cy)) return true;
                return false;
            }
            catch { return false; }
        }

        static bool IsPointOverWindow(Window w, double cx, double cy)
        {
            try { if (w == null || !w.IsVisible) return false; return cx >= w.Left && cx <= w.Left + w.ActualWidth && cy >= w.Top && cy <= w.Top + w.ActualHeight; }
            catch { return false; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // TASKBAR WATCHER
        // ═════════════════════════════════════════════════════════════════════
        // [EXP-RESTART] Двухуровневая защита:
        //   1. SetWinEventHook — мгновенная реакция (< 50 мс) при пересоздании
        //      Shell_TrayWnd после перезапуска Explorer (Диспетчер устройств и т.п.)
        //   2. Backup DispatcherTimer 500 мс — на случай, если хук пропустит событие
        void StartTaskbarWatcher()
        {
            // ── 1. WinEvent хук ─────────────────────────────────────────────
            _winEventDelegate = OnShellWinEvent;   // держим ссылку — иначе GC уберёт!
            _winEventHook = SetWinEventHook(
                EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW,
                IntPtr.Zero, _winEventDelegate,
                0, 0, WINEVENT_OUTOFCONTEXT);

            // ── 2. Backup-таймер (был 3 сек, теперь 500 мс) ─────────────────
            _taskbarWatcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _taskbarWatcher.Tick += (s, e) =>
            {
                try { if (TaskbarHelper.IsSystemTaskbarVisible()) TaskbarHelper.Hide(); }
                catch { }
            };
            _taskbarWatcher.Start();
        }

        // Вызывается системой немедленно при создании/показе любого окна.
        // Нас интересует только Shell_TrayWnd / Shell_SecondaryTrayWnd —
        // это признак того, что Explorer только что перезапустился.
        void OnShellWinEvent(IntPtr hHook, uint eventType, IntPtr hwnd,
                             int idObject, int idChild,
                             uint dwEventThread, uint dwmsEventTime)
        {
            // idObject == 0 → OBJID_WINDOW (само окно, не его дочерние элементы)
            if (hwnd == IntPtr.Zero || idObject != 0) return;
            try
            {
                var sb = new StringBuilder(64);
                GetWindowClassName(hwnd, sb, sb.Capacity);
                string cls = sb.ToString();
                if (cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd") return;

                // Explorer только что создал/показал панель задач.
                // Небольшая пауза (250 мс) даём Explorer'у завершить инициализацию,
                // затем скрываем — всё это на UI-потоке через Dispatcher.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    t.Tick += (s, e) =>
                    {
                        t.Stop();
                        try { TaskbarHelper.Hide(); }
                        catch { }

                        // [DESKTOP-REFRESH] При первом старте на медленных ПК Explorer
                        // не успевает нарисовать рабочий стол — получаем чёрный экран.
                        // Посылаем Progman команду обновить иконки рабочего стола.
                        // Срабатывает только один раз за сессию.
                        if (!_desktopRefreshDone)
                        {
                            _desktopRefreshDone = true;
                            RefreshDesktop();
                        }
                    };
                    t.Start();
                }));
            }
            catch { }
        }

        // [DESKTOP-REFRESH] Принудительно обновляет рабочий стол Explorer'а.
        // Используется при старте на медленных ПК где Explorer не успел инициализироваться.
        void RefreshDesktop()
        {
            try
            {
                // Шаг 1: SHChangeNotify — сигнализируем Explorer что рабочий стол изменился
                SHChangeNotify(0x8000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }

            try
            {
                // Шаг 2: PostMessage на Progman — триггер перерисовки иконок рабочего стола
                // 0x0403 = WM_SETTINGCHANGE (заставляет Explorer перечитать настройки рабочего стола)
                IntPtr progman = FindWindow("Progman", null);
                if (progman != IntPtr.Zero)
                {
                    PostMessage(progman, 0x0052, IntPtr.Zero, IntPtr.Zero); // WM_SETICON — будит Progman
                    PostMessage(progman, 0x0403, IntPtr.Zero, IntPtr.Zero); // WM_SETTINGCHANGE
                }
            }
            catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        // WIFI
        // ═════════════════════════════════════════════════════════════════════
        void StartWifiIconTracker()
        {
            FetchWifiSignalAsync(); UpdateWifiIcon();
            // [PERF-2] Увеличен интервал с 3 до 10 сек — netsh.exe дорогой, каждые 3 сек избыточно
            _wifiIconTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _wifiIconTimer.Tick += (s, e) => { FetchWifiSignalAsync(); UpdateWifiIcon(); };
            _wifiIconTimer.Start();
        }

        void UpdateWifiIcon()
        {
            try
            {
                if (WifiIcon == null) return;
                var st = GetNetworkStatus();
                WifiIcon.Text = st.Icon;
                WifiIcon.Foreground = st.Active ? Brushes.White : new SolidColorBrush(Color.FromRgb(130, 130, 130));
                if (WifiButton != null) WifiButton.ToolTip = st.Tooltip;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UpdateWifiIcon: {ex.Message}"); }
        }

        void FetchWifiSignalAsync()
        {
            if (_wifiSignalPending) return;
            _wifiSignalPending = true;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                int sig = -1;
                try
                {
                    var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.GetEncoding(866) };
                    using (var p = Process.Start(psi))
                    {
                        if (p == null) return;
                        string output = p.StandardOutput.ReadToEnd();
                        // [PERF-2] Сокращён таймаут netsh: 2500->1200ms, чтобы не держать поток
                        if (!p.WaitForExit(1200)) { try { p.Kill(); } catch { } }
                        foreach (var rawLine in output.Split('\n'))
                        {
                            string line = rawLine.Trim();
                            int colon = line.IndexOf(':'); if (colon < 1) continue;
                            string key = line.Substring(0, colon).Trim();
                            string val = line.Substring(colon + 1).Trim();
                            bool isSig = string.Equals(key, "Signal", StringComparison.OrdinalIgnoreCase) || key == "Сигнал"; // "Сигнал" = Russian for Signal
                            if (!isSig) continue;
                            string ns = val.Replace("%", "").Trim();
                            if (int.TryParse(ns, out int pct)) sig = Math.Max(0, Math.Min(100, pct));
                            break;
                        }
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] FetchWifi: {ex.Message}"); }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        _wifiSignalPending = false;
                        if (sig != _cachedWifiSignal) { _cachedWifiSignal = sig; UpdateWifiIcon(); }
                    });
                }
            });
        }

        struct NetworkStatus { public string Icon; public string Tooltip; public bool Active; }

        NetworkStatus GetNetworkStatus()
        {
            try
            {
                var ifaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var ni in ifaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    bool isEth = ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                              || ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet
                              || ni.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT
                              || ni.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx;
                    if (isEth && HasRealIP(ni))
                        return new NetworkStatus { Icon = "\uE839", Tooltip = $"Ethernet: {ni.Name}", Active = true };
                }
                foreach (var ni in ifaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                    if (HasRealIP(ni))
                    {
                        int s = _cachedWifiSignal >= 0 ? _cachedWifiSignal : 50;
                        string ss = _cachedWifiSignal >= 0 ? $"{s}%" : "…";
                        return new NetworkStatus { Icon = SignalToIcon(s), Tooltip = $"Wi-Fi · signal {ss}", Active = true };
                    }
                    return new NetworkStatus { Icon = "\uF384", Tooltip = "No Internet connection", Active = false };
                }
                return new NetworkStatus { Icon = "\uF384", Tooltip = "No network connections", Active = false };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MyTaskbar] GetNetworkStatus: {ex.Message}");
                return new NetworkStatus { Icon = "\uF384", Tooltip = "Network unavailable", Active = false };
            }
        }

        bool HasRealIP(NetworkInterface ni)
        {
            try
            {
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    { string ip = a.Address.ToString(); if (!ip.StartsWith("169.254") && ip != "0.0.0.0") return true; }
            }
            catch { }
            return false;
        }

        string SignalToIcon(int s) =>
            s <= 0 ? "\uF384" : s >= 80 ? "\uE701" : s >= 60 ? "\uE872" :
            s >= 40 ? "\uE873" : s >= 20 ? "\uE874" : "\uE875";

        DateTime _wifiClosedAt = DateTime.MinValue;

        void WifiButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MarkOwnActivity();
                if ((DateTime.UtcNow - _wifiClosedAt).TotalMilliseconds < 300) return;
                if (_wifiWindow == null)
                {
                    _wifiWindow = new WifiWindow(); _wifiWindow.UIScale = _uiScale;
                    _wifiWindow.IsVisibleChanged += (s2, ev) => { if (!(bool)ev.NewValue) { _wifiClosedAt = DateTime.UtcNow; UpdateWifiIcon(); } };
                }
                if (_wifiWindow.IsVisible) { _wifiWindow.Hide(); return; }
                _wifiWindow.IsBottom = _isBottom;
                _wifiWindow.ShowAt(0, TASKBAR_HEIGHT + 2);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] WifiButton_Click: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // ЯЗЫК
        // ═════════════════════════════════════════════════════════════════════
        void StartLangTracker()
        {
            UpdateLangLabel();
            _langTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _langTimer.Tick += (s, e) => UpdateLangLabel();
            _langTimer.Start();
        }

        void UpdateLangLabel()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow(); if (hwnd == IntPtr.Zero) return;
                uint tid = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                IntPtr hkl = GetKeyboardLayout(tid); if (hkl == IntPtr.Zero) return;
                int langId = (int)(long)hkl & 0xFFFF; if (langId == 0) return;
                string lang = new System.Globalization.CultureInfo(langId).TwoLetterISOLanguageName?.ToUpperInvariant() ?? "";
                if (lang != _lastLang && LangLabel != null) { _lastLang = lang; LangLabel.Text = lang; }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UpdateLangLabel: {ex.Message}"); }
        }

        void LangButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LangButton_Click: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // БАТАРЕЯ [STAB-6]
        // ═════════════════════════════════════════════════════════════════════
        void StartBatteryTracker()
        {
            UpdateBattery();
            _batteryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _batteryTimer.Tick += (s, e) =>
            {
                // [STAB-6] Флаг реентрантности
                if (System.Threading.Interlocked.CompareExchange(ref _batteryBusyInt, 1, 0) != 0) return;
                try { SafeRun(UpdateBattery, "UpdateBattery"); }
                finally { System.Threading.Interlocked.Exchange(ref _batteryBusyInt, 0); }
            };
            _batteryTimer.Start();
        }

        void UpdateBattery()
        {
            try
            {
                var status = System.Windows.Forms.SystemInformation.PowerStatus;
                bool noBatt = status.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
                            || status.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.Unknown;
                if (noBatt) { if (BatteryButton != null) BatteryButton.Visibility = Visibility.Collapsed; return; }
                if (BatteryButton != null) BatteryButton.Visibility = Visibility.Visible;
                float raw = status.BatteryLifePercent;
                int pct = float.IsNaN(raw) ? 0 : Math.Max(0, Math.Min(100, (int)Math.Round(raw * 100)));
                bool chg = status.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                if (BatteryLabel != null) { BatteryLabel.Text = pct.ToString(); BatteryLabel.Foreground = Brushes.White; }
                if (BatteryPercent != null) BatteryPercent.Visibility = chg ? Visibility.Collapsed : Visibility.Visible;
                if (BatteryLightning != null) BatteryLightning.Visibility = chg ? Visibility.Visible : Visibility.Collapsed;
                string tl = "";
                if (!chg && status.BatteryLifeRemaining > 0)
                { var ts = TimeSpan.FromSeconds(status.BatteryLifeRemaining); tl = $" · ~{(int)ts.TotalHours}h {ts.Minutes}m"; }
                if (BatteryButton != null) BatteryButton.ToolTip = $"{pct}%{(chg ? " · charging" : "")}{tl}";
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UpdateBattery: {ex.Message}"); if (BatteryButton != null) BatteryButton.Visibility = Visibility.Collapsed; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // TRAY
        // ═════════════════════════════════════════════════════════════════════
        DateTime _trayClosedAt = DateTime.MinValue;
        DateTime _volumeClosedAt = DateTime.MinValue;      // ← добавить
        DateTime _brightnessClosedAt = DateTime.MinValue;  // ← добавить

        void InitTrayWindow()
        {
            if (_trayWindow != null) return;
            _trayWindow = new TrayWindow(); _trayWindow.UIScale = _uiScale;
            _trayWindow.OnTrayClosed = () =>
            {
                _trayClosedAt = DateTime.UtcNow;
                if (TrayIconClosed != null) TrayIconClosed.Visibility = Visibility.Visible;
                if (TrayIconOpen != null) TrayIconOpen.Visibility = Visibility.Collapsed;
            };
            // Окно всегда видимо (спрятано за верхний край), показываем его сразу
            _trayWindow.Show();
        }

        void TrayButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MarkOwnActivity(); InitTrayWindow();
                if ((DateTime.UtcNow - _trayClosedAt).TotalMilliseconds < 300) return;
                if (_trayWindow.IsOpen)
                {
                    _trayWindow.SlideUp(() => Dispatcher.Invoke(() =>
                    {
                        _trayClosedAt = DateTime.UtcNow;
                        if (TrayIconClosed != null) TrayIconClosed.Visibility = Visibility.Visible;
                        if (TrayIconOpen != null) TrayIconOpen.Visibility = Visibility.Collapsed;
                    }));
                    return;
                }
                var btn = sender as Button; if (btn == null) return;
                var pos = btn.PointToScreen(new System.Windows.Point(0, 0));
                _trayWindow.IsBottom = _isBottom;
                double trayY = _isBottom
                    ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - 2
                    : TASKBAR_HEIGHT + 4;
                _trayWindow.SlideDown(pos.X + btn.ActualWidth / 2, trayY);
                TrayIconClosed.Visibility = Visibility.Collapsed;
                TrayIconOpen.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] TrayButton_Click: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // PINNED APPS / SHORTCUTS
        // ═════════════════════════════════════════════════════════════════════
        void LoadPinnedApps()
        {
            List<PinnedApp> apps;
            try { apps = PinnedAppsManager.Load() ?? new List<PinnedApp>(); }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LoadPinnedApps: {ex.Message}"); return; }
            for (int i = 0; i < apps.Count; i++)
            {
                var app = apps[i];
                try
                {
                    if (string.IsNullOrWhiteSpace(app?.Path)) continue;
                    string fullPath = app.Path;
                    if (!IOPath.IsPathRooted(fullPath)) fullPath = PinnedAppsManager.FindApp(app.Path) ?? app.Path;
                    string exeName = IOPath.GetFileNameWithoutExtension(fullPath)?.ToLowerInvariant() ?? "";
                    if (string.IsNullOrEmpty(exeName)) continue;
                    if (HelperExeNames.Contains(exeName)) continue;
                    string tooltip = string.IsNullOrWhiteSpace(app.Tooltip) ? (app.Name ?? exeName) : app.Tooltip;
                    var group = GetOrCreateGroup(exeName, GetCachedIcon(fullPath), tooltip);
                    group.IsPinned = true; group.LaunchPath = fullPath; group.PinOrder = i;
                }
                catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LoadPinnedApps item: {ex.Message}"); }
            }
            SortButtonsByPinOrder();
        }

        void LoadShortcuts()
        {
            try
            {
                if (!Directory.Exists(ShortcutsFolder)) return;
                foreach (var lnkPath in Directory.GetFiles(ShortcutsFolder, "*.lnk"))
                {
                    try
                    {
                        string tooltip = IOPath.GetFileNameWithoutExtension(lnkPath) ?? "";
                        string exePath = IconHelper.ResolveShortcut(lnkPath);
                        string exeName = !string.IsNullOrEmpty(exePath)
                            ? (IOPath.GetFileNameWithoutExtension(exePath)?.ToLowerInvariant() ?? "")
                            : tooltip.ToLowerInvariant();
                        if (string.IsNullOrEmpty(exeName)) continue;
                        var group = GetOrCreateGroup(exeName, GetCachedIcon(lnkPath), tooltip);
                        group.IsPinned = true; group.LaunchPath = lnkPath;
                    }
                    catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LoadShortcuts item: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LoadShortcuts: {ex.Message}"); }
        }

        void SortButtonsByPinOrder()
        {
            try
            {
                var pinned = _groups.Values.Where(g => g.IsPinned && g.Button != null && AppIcons.Children.Contains(g.Button)).OrderBy(g => g.PinOrder).Select(g => g.Button).ToList();
                var unpinned = _groups.Values.Where(g => !g.IsPinned && g.Button != null && AppIcons.Children.Contains(g.Button)).Select(g => g.Button).ToList();
                var all = pinned.Concat(unpinned).ToList();
                foreach (var b in all) AppIcons.Children.Remove(b);
                foreach (var b in all) AppIcons.Children.Add(b);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] SortButtonsByPinOrder: {ex.Message}"); }
        }

        AppGroup GetOrCreateGroup(string exeName, BitmapSource icon, string tooltip)
        {
            if (string.IsNullOrEmpty(exeName)) exeName = "unknown";
            if (_groups.TryGetValue(exeName, out var ex2)) { if (ex2.Icon == null && icon != null) ex2.Icon = icon; if (!string.IsNullOrEmpty(tooltip) && string.IsNullOrEmpty(ex2.PinnedTooltip)) ex2.PinnedTooltip = tooltip; return ex2; }
            bool uwpIcon = IsUwpAppName(exeName);
            var group = new AppGroup { ExeName = exeName, GroupKey = exeName, Icon = icon, LastTitle = tooltip ?? exeName, PinnedTooltip = tooltip ?? exeName, IsUwp = uwpIcon };
            Button btn;
            try { btn = CreateAppButton(tooltip, icon, uwpIcon ? 22 : 24); }
            catch { btn = new Button { Content = exeName.Substring(0, Math.Min(1, exeName.Length)).ToUpper() }; }
            SetupGroupButton(btn, group);
            group.Button = btn; _groups[exeName] = group;
            try { AddButtonAnimated(btn); } catch (Exception e) { Debug.WriteLine($"[MyTaskbar] AppIcons.Add: {e.Message}"); }
            return group;
        }

        BitmapSource GetCachedIcon(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_iconCache.TryGetValue(key, out var c)) return c;
            try
            {
                var i = TryShellItemImage(key, 32) ?? TrySHGetFileInfo(key, 32) ?? IconHelper.GetIconFromExe(key, 32);
                if (i != null) _iconCache[key] = i;
                return i;
            }
            catch { return null; }
        }

        void SetupGroupButton(Button btn, AppGroup group)
        {
            if (btn == null || group == null) return;
            btn.MouseEnter += (s, e) =>
            {
                try
                {
                    _previewHideTimer?.Stop();
                    _previewShowTimer?.Stop();
                    _pendingPreviewGroup = group;
                    _pendingPreviewBtn = btn;
                    // [FIX-PREVIEW] Если preview уже открыт — переключаем немедленно,
                    // без задержки 500мс. Это убирает "залипание" при наведении по очереди.
                    if (_previewWindow != null && _previewWindow.IsVisible && _currentPreviewGroup != null)
                        ShowPreview(group, btn);
                    else
                        _previewShowTimer?.Start();
                }
                catch { }
            };
            btn.MouseLeave += (s, e) =>
            {
                try
                {
                    _previewShowTimer?.Stop();
                    // [FIX-PREVIEW] Сбрасываем pending только если уходим в никуда
                    // (не в другую кнопку). Другая кнопка сразу поставит свои значения в MouseEnter.
                    _pendingPreviewGroup = null;
                    _pendingPreviewBtn = null;
                    ScheduleHidePreview();
                }
                catch { }
            };
            btn.PreviewMouseDown += (s, e) =>
            {
                try { MarkOwnActivity(); IntPtr mh = new WindowInteropHelper(this).Handle; IntPtr fg = GetForegroundWindow(); if (fg != mh && fg != IntPtr.Zero) _lastForegroundWindow = fg; } catch { }
            };
            btn.Click += (s, e) =>
            {
                try
                {
                    if (_menuWindow != null && _menuWindow.IsVisible) _menuWindow.HideAnimated();
                    _previewShowTimer?.Stop(); _pendingPreviewGroup = null; _pendingPreviewBtn = null;
                    _previewHideTimer?.Stop(); HidePreview();
                    AllowSetForegroundWindow(ASFW_ANY);
                    if (group.Hwnds.Count == 0)
                    {
                        if (!string.IsNullOrWhiteSpace(group.LaunchPath))
                            try { Process.Start(new ProcessStartInfo(group.LaunchPath) { UseShellExecute = true }); } catch { }
                        return;
                    }
                    if (group.Hwnds.Count == 1)
                    {
                        IntPtr hwnd = group.Hwnds[0]; bool uwp = IsUwpAppName(group.ExeName);
                        // [FIX-TASKMGR-RESTORE] IsIconic не работает для привилегированных окон
                        // (taskmgr, regedit) — используем GetWindowPlacement через IsWindowMinimized.
                        // ShowWindow(SW_RESTORE) тоже игнорируется — используем SC_RESTORE.
                        bool minimized = IsIconic(hwnd) || IsWindowMinimized(hwnd);
                        if (minimized)
                        {
                            SuspendBlockerForAppWindow();
                            // SC_RESTORE разворачивает окно через очередь сообщений —
                            // работает даже для привилегированных процессов
                            PostMessage(hwnd, 0x0112 /*WM_SYSCOMMAND*/, new IntPtr(0xF120 /*SC_RESTORE*/), IntPtr.Zero);
                            SetForegroundWindow(hwnd);
                            if (uwp) ActivateUwpWindow(hwnd);
                        }
                        else if (_lastForegroundWindow == hwnd || GetForegroundWindow() == hwnd)
                        {
                            // [FIX-TASKMGR-TOGGLE] ShowWindow(SW_MINIMIZE) игнорируется
                            // привилегированными окнами — используем SC_MINIMIZE.
                            PostMessage(hwnd, 0x0112 /*WM_SYSCOMMAND*/, new IntPtr(0xF020 /*SC_MINIMIZE*/), IntPtr.Zero);
                        }
                        else
                        {
                            SuspendBlockerForAppWindow();
                            // [FIX-MAXIMIZE] Не посылаем SC_RESTORE если окно не минимизировано —
                            // иначе maximized-окно (Chrome в fullscreen) схлопывается до нормального размера.
                            // Просто переводим фокус на уже видимое окно.
                            SetForegroundWindow(hwnd);
                            if (uwp) ActivateUwpWindow(hwnd);
                        }
                    }
                    else
                    {
                        IntPtr active = GetForegroundWindow();
                        int idx = group.Hwnds.IndexOf(active);
                        IntPtr next = group.Hwnds[(idx + 1) % group.Hwnds.Count];
                        SuspendBlockerForAppWindow();
                        // [FIX-MAXIMIZE] Только восстанавливаем если минимизировано — иначе не трогаем WindowState
                        if (IsIconic(next) || IsWindowMinimized(next)) ShowWindow(next, SW_RESTORE);
                        SetForegroundWindow(next);
                        if (IsUwpAppName(group.ExeName)) ActivateUwpWindow(next);
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] btn.Click: {ex.Message}"); }
            };
            btn.MouseRightButtonUp += (s, e) => { try { e.Handled = true; ShowContextMenu(btn, group); } catch { } };
        }

        // Закрытие окна: для UWP и привилегированных процессов (taskmgr и др.)
        // используем WM_SYSCOMMAND+SC_CLOSE, для обычных — WM_CLOSE
        void CloseWindow(IntPtr hwnd, bool isUwp)
        {
            try
            {
                if (!IsWindow(hwnd)) return;
                if (isUwp)
                {
                    PostMessage(hwnd, 0x0112 /*WM_SYSCOMMAND*/, new IntPtr(0xF060 /*SC_CLOSE*/), IntPtr.Zero);
                }
                else
                {
                    // [FIX-TASKMGR-CLOSE] Привилегированные окна (taskmgr, regedit)
                    // могут игнорировать WM_CLOSE. SC_CLOSE имитирует нажатие кнопки «×»
                    // и обрабатывается такими окнами корректно.
                    PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    PostMessage(hwnd, 0x0112 /*WM_SYSCOMMAND*/, new IntPtr(0xF060 /*SC_CLOSE*/), IntPtr.Zero);
                }
            }
            catch { }
        }

        void ActivateUwpWindow(IntPtr frameHwnd)
        {
            if (frameHwnd == IntPtr.Zero || !IsWindow(frameHwnd)) return;
            try
            {
                AllowSetForegroundWindow(ASFW_ANY);
                if (IsIconic(frameHwnd)) ShowWindow(frameHwnd, SW_RESTORE); else ShowWindow(frameHwnd, SW_SHOW);
                SetForegroundWindow(frameHwnd);
                IntPtr child = FindWindowEx(frameHwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (child == IntPtr.Zero) child = FindWindowEx(frameHwnd, IntPtr.Zero, "ApplicationFrameInputSinkWindow", null);
                if (child != IntPtr.Zero) SetForegroundWindow(child);
            }
            catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        // ACTIVE WINDOW TRACKER [STAB-6]
        // ═════════════════════════════════════════════════════════════════════
        void StartActiveWindowTracker()
        {
            _activeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _activeTimer.Tick += (s, e) =>
            {
                // UpdateWindowGroups уже имеет Interlocked-защиту внутри
                SafeRun(UpdateWindowGroups, "UpdateWindowGroups");
                // [STAB-6] UpdateButtonStates — отдельный флаг
                if (System.Threading.Interlocked.CompareExchange(ref _btnStateBusyInt, 1, 0) == 0)
                {
                    try { SafeRun(UpdateButtonStates, "UpdateButtonStates"); }
                    finally { System.Threading.Interlocked.Exchange(ref _btnStateBusyInt, 0); }
                }
            };
            _activeTimer.Start();
        }

        void UpdateGroupBadge(AppGroup g)
        {
            if (g?.Button == null) return;

            // Когда все окна закрыты (pinned приложение без окон) — сбрасываем тултип на базовое имя
            if (g.Hwnds.Count == 0)
            {
                string baseTitle = g.PinnedTooltip ?? g.ExeName;
                if (g.LastTitle != baseTitle) { g.Button.ToolTip = baseTitle; g.LastTitle = baseTitle; }
                return;
            }
            try
            {
                // [FIX-D3DPROXY] Перебираем все hwnd группы и выбираем лучший заголовок:
                // берём первый заголовок, который не совпадает с именем window-class (технический)
                // и не содержит только имя exe. Это гарантирует что "Team Fortress 2 - Direct3D"
                // будет выбран вместо "D3DProxyWindow" даже если proxy-окно стоит первым.
                string dn = null;
                foreach (IntPtr hwnd in g.Hwnds)
                {
                    string t = SafeGetWindowText(hwnd);
                    if (string.IsNullOrEmpty(t)) continue;
                    // Предпочитаем заголовок который не является именем window-class
                    string cls = GetWindowClass(hwnd);
                    if (!string.IsNullOrEmpty(cls) && IgnoredWindowClasses.Contains(cls)) continue;
                    // Первый подходящий — лучший
                    dn = t;
                    break;
                }
                if (string.IsNullOrEmpty(dn)) dn = SafeGetWindowText(g.Hwnds[0]);
                if (string.IsNullOrEmpty(dn)) dn = g.LastTitle ?? g.ExeName;
                string title = g.Hwnds.Count == 1 ? dn : $"{dn} ({g.Hwnds.Count} windows)";
                if (title != g.LastTitle) { g.Button.ToolTip = title; g.LastTitle = title; }
            }
            catch { }
        }

        void UpdateButtonIcon(Button btn, BitmapSource icon)
        { try { if (btn?.Content is System.Windows.Controls.Image img) img.Source = icon; } catch { } }

        void UpdateButtonStates()
        {
            IntPtr active = GetForegroundWindow();
            IntPtr myHwnd; try { myHwnd = new WindowInteropHelper(this).Handle; } catch { myHwnd = IntPtr.Zero; }
            if (active != myHwnd && active != IntPtr.Zero) _lastForegroundWindow = active;

            if (active != myHwnd && active != IntPtr.Zero)
            {
                var sbCls = new StringBuilder(128); GetWindowClassName(active, sbCls, sbCls.Capacity); string cls = sbCls.ToString();
                if (cls == "Windows.UI.Core.CoreWindow" || cls == "ApplicationFrameInputSinkWindow")
                    foreach (var g in _groups.Values)
                        if (IsUwpAppName(g.ExeName) && g.Hwnds.Count > 0)
                        { IntPtr parent = active; for (int depth = 0; depth < 5; depth++) { parent = GetParent(parent); if (parent == IntPtr.Zero) break; if (g.Hwnds.Contains(parent)) { _lastForegroundWindow = parent; break; } } }
            }

            string activeExe = "";
            if (active != myHwnd && active != IntPtr.Zero)
            {
                GetWindowThreadProcessId(active, out uint pid);
                if (pid != 0 && !_pidNameCache.TryGetValue(pid, out activeExe))
                    try { activeExe = Process.GetProcessById((int)pid).ProcessName?.ToLowerInvariant() ?? ""; } catch { activeExe = ""; }
                if (ProcessAliases.TryGetValue(activeExe, out string al)) activeExe = al;

                if (string.Equals(activeExe, "applicationframehost", StringComparison.OrdinalIgnoreCase) || activeExe == "")
                {
                    // [STAB-4] SafeGetWindowText
                    string activeTitle = SafeGetWindowText(active);
                    string uwpName = ResolveUwpAppName(activeTitle);
                    if (!string.IsNullOrEmpty(uwpName)) activeExe = uwpName;
                    if (string.IsNullOrEmpty(activeExe) || activeExe == "applicationframehost")
                        foreach (var kvp2 in _groups) if (IsUwpAppName(kvp2.Key) && kvp2.Value.Hwnds.Contains(active)) { activeExe = kvp2.Key; break; }
                }
                else
                {
                    if (DirectUwpProcesses.Contains(activeExe) && activeExe == "microsoft.photos") activeExe = "photos";
                    var sbCls2 = new StringBuilder(128); GetWindowClassName(active, sbCls2, sbCls2.Capacity); string cls2 = sbCls2.ToString();
                    if (cls2 == "Windows.UI.Core.CoreWindow" || cls2 == "ApplicationFrameInputSinkWindow")
                        foreach (var kvp2 in _groups)
                            if (IsUwpAppName(kvp2.Key) && kvp2.Value.Hwnds.Count > 0)
                            { IntPtr par = active; for (int depth = 0; depth < 5; depth++) { par = GetParent(par); if (par == IntPtr.Zero) break; if (kvp2.Value.Hwnds.Contains(par)) { activeExe = kvp2.Key; break; } } if (!string.IsNullOrEmpty(activeExe)) break; }
                }
            }

            foreach (var kvp in _groups)
            {
                try
                {
                    var g = kvp.Value;
                    if (_removingButtons.Contains(g.Button)) continue;
                    bool running = g.Hwnds.Count > 0;
                    // [PER-WINDOW] Per-window groups match by hwnd; normal groups match by exe name
                    bool isActive;
                    if (running && kvp.Key.Contains(":"))
                        isActive = g.Hwnds.Contains(active);
                    else
                        isActive = running && string.Equals(kvp.Key, activeExe, StringComparison.OrdinalIgnoreCase);
                    string state = isActive ? "Active" : running ? "Running" : "";
                    if (_buttonStateCache.TryGetValue(g.Button, out string old) && old == state) continue;
                    _buttonStateCache[g.Button] = state; ApplyButtonState(g.Button, state);
                }
                catch { }
            }
        }

        Button CreateAppButton(string tooltip, BitmapSource icon, int iconSize = 24)
        {
            // [UI-SCALE-ICON] Иконка уже масштабируется через LayoutTransform MainPanel,
            // поэтому размер иконки задаём в базовых DIP (не умножаем на _uiScale) —
            // ScaleTransform на родительском элементе сделает это автоматически.
            var btn = new Button { Style = TryFindResource("IconButton") as Style, ToolTip = tooltip };
            btn.RenderTransform = new TranslateTransform(0, 0);
            btn.RenderTransformOrigin = new Point(0.5, 0.5);
            if (icon != null)
            {
                var img = new System.Windows.Controls.Image { Source = icon, Width = iconSize, Height = iconSize, Stretch = Stretch.Uniform, SnapsToDevicePixels = true, UseLayoutRounding = true };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                btn.Content = img;
            }
            else
                btn.Content = new TextBlock { Text = (tooltip?.Length > 0 ? tooltip.Substring(0, 1) : "?").ToUpper(), Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            return btn;
        }

        // ═════════════════════════════════════════════════════════════════════
        // CONTEXT MENU [STAB-13]
        // ═════════════════════════════════════════════════════════════════════

        // Builds a MenuItem whose header is a two-column Grid:
        //   col0 (28px fixed) — optional icon
        //   col1 (auto)       — label text
        // This keeps every item's text perfectly left-aligned regardless of
        // whether it has an icon, exactly like the Win10 taskbar jump list.
        MenuItem MakeMenuItem(string label, BitmapSource icon = null, bool bold = false)
        {
            var grid = new Grid { Margin = new Thickness(icon != null ? 3 : 2, 0, 0, 0) };

            if (icon != null)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var img = new System.Windows.Controls.Image
                {
                    Source = icon, Width = 16, Height = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    SnapsToDevicePixels = true
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Grid.SetColumn(img, 0);
                grid.Children.Add(img);

                var tb = new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
                };
                Grid.SetColumn(tb, 1);
                grid.Children.Add(tb);
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var tb = new TextBlock
                {
                    Text = label,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
                };
                Grid.SetColumn(tb, 0);
                grid.Children.Add(tb);
            }

            return new MenuItem { Header = grid, Style = TryFindResource("DarkMenuItem") as Style };
        }

        void ShowContextMenu(Button btn, AppGroup group)
        {
            try
            {
                HidePreview();
                var menu = new ContextMenu { Style = TryFindResource("DarkContextMenu") as Style };
                bool hasWindows = group.Hwnds.Count > 0;

                // ── App name + icon (always at top, like Win10) ───────────────
                string launchPath = group.LaunchPath;
                if (string.IsNullOrEmpty(launchPath) && hasWindows)
                {
                    try
                    {
                        GetWindowThreadProcessId(group.Hwnds[0], out uint pid);
                        if (pid != 0) launchPath = System.Diagnostics.Process.GetProcessById((int)pid).MainModule?.FileName ?? "";
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(launchPath))
                {
                    string appLabel = System.IO.Path.GetFileNameWithoutExtension(launchPath);
                    if (string.IsNullOrEmpty(appLabel)) appLabel = group.ExeName ?? "";
                    if (appLabel.Length > 45) appLabel = appLabel.Substring(0, 45) + "…";

                    string lp = launchPath;
                    var miLaunch = MakeMenuItem(appLabel, group.Icon, bold: true);
                    miLaunch.Click += (s, e) =>
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(lp) { UseShellExecute = true }); }
                        catch { }
                    };
                    menu.Items.Add(miLaunch);
                    menu.Items.Add(new Separator { Style = TryFindResource("DarkMenuSeparator") as Style });
                }

                // ── Pin / Unpin ───────────────────────────────────────────────
                if (group.IsPinned)
                {
                    var miUnpin = MakeMenuItem("Unpin from taskbar");
                    miUnpin.Click += (s, e) => { try { UnpinGroup(group); } catch { } };
                    menu.Items.Add(miUnpin);
                }
                else if (!string.IsNullOrEmpty(group.LaunchPath) || hasWindows)
                {
                    var miPin = MakeMenuItem("Pin to taskbar");
                    miPin.Click += (s, e) => { try { PinGroup(group); } catch { } };
                    menu.Items.Add(miPin);
                }

                // ── Close (only when windows are open) ────────────────────────
                if (hasWindows)
                {
                    if (menu.Items.Count > 0)
                        menu.Items.Add(new Separator { Style = TryFindResource("DarkMenuSeparator") as Style });

                    if (group.Hwnds.Count == 1)
                    {
                        IntPtr hwndSingle = group.Hwnds[0];
                        var miClose = MakeMenuItem("Close window");
                        miClose.Click += (s, e) =>
                        {
                            try { if (IsWindow(hwndSingle)) PostMessage(hwndSingle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
                            catch { }
                        };
                        menu.Items.Add(miClose);
                    }
                    else
                    {
                        foreach (var hwnd in group.Hwnds.ToList())
                        {
                            IntPtr cap = hwnd;
                            string t = SafeGetWindowText(hwnd);
                            if (string.IsNullOrEmpty(t)) t = group.ExeName;
                            string header = t.Length > 40 ? t.Substring(0, 40) + "…" : t;
                            var miOne = MakeMenuItem("Close \"" + header + "\"");
                            miOne.Click += (s, e) =>
                            {
                                try { if (IsWindow(cap)) PostMessage(cap, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
                                catch { }
                            };
                            menu.Items.Add(miOne);
                        }
                        menu.Items.Add(new Separator { Style = TryFindResource("DarkMenuSeparator") as Style });
                        var miCloseAll = MakeMenuItem("Close all windows");
                        miCloseAll.Click += (s, e) =>
                        {
                            foreach (var h in group.Hwnds.ToList())
                                try { if (IsWindow(h)) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
                                catch { }
                        };
                        menu.Items.Add(miCloseAll);
                    }
                }

                if (menu.Items.Count == 0) return;
                menu.PlacementTarget = btn; menu.Placement = PlacementMode.Top; menu.IsOpen = true;
            }
            catch { }
        }

        string ResolveExePath(AppGroup group)
        {
            if (!string.IsNullOrEmpty(group.LaunchPath) && File.Exists(group.LaunchPath))
            { string bn = IOPath.GetFileNameWithoutExtension(group.LaunchPath); if (!HelperExeNames.Contains(bn)) return group.LaunchPath; }
            if (group.Hwnds.Count > 0)
            {
                try
                {
                    GetWindowThreadProcessId(group.Hwnds[0], out uint pid);
                    if (pid != 0) { string path = Process.GetProcessById((int)pid).MainModule?.FileName ?? ""; if (!string.IsNullOrEmpty(path) && File.Exists(path)) { string bn = IOPath.GetFileNameWithoutExtension(path); if (!HelperExeNames.Contains(bn)) return path; } }
                }
                catch { }
            }
            string found = IconHelper.FindExeByName(group.ExeName);
            if (!string.IsNullOrEmpty(found) && File.Exists(found)) return found;
            return null;
        }

        void PinGroup(AppGroup group)
        {
            try
            {
                string exePath = ResolveExePath(group);
                if (string.IsNullOrEmpty(exePath)) { Debug.WriteLine($"[MyTaskbar] PinGroup: path not found for '{group.ExeName}'"); return; }
                group.IsPinned = true; group.LaunchPath = exePath;
                var apps = PinnedAppsManager.Load() ?? new List<PinnedApp>();
                bool already = apps.Any(a => string.Equals(IOPath.GetFileNameWithoutExtension(a?.Path ?? ""), group.ExeName, StringComparison.OrdinalIgnoreCase));
                if (!already) { apps.Add(new PinnedApp { Name = group.ExeName, Path = exePath, Tooltip = group.LastTitle }); group.PinOrder = apps.Count - 1; }
                PinnedAppsManager.Save(apps); SortButtonsByPinOrder();
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] PinGroup: {ex.Message}"); }
        }

        void UnpinGroup(AppGroup group)
        {
            try
            {
                group.IsPinned = false; group.LaunchPath = null; group.PinOrder = int.MaxValue;
                var apps = PinnedAppsManager.Load() ?? new List<PinnedApp>();
                apps.RemoveAll(a => string.Equals(IOPath.GetFileNameWithoutExtension(a?.Path ?? ""), group.ExeName, StringComparison.OrdinalIgnoreCase));
                PinnedAppsManager.Save(apps);
                for (int i = 0; i < apps.Count; i++) { string en = IOPath.GetFileNameWithoutExtension(apps[i]?.Path ?? "")?.ToLowerInvariant() ?? ""; if (_groups.TryGetValue(en, out var g)) g.PinOrder = i; }
                if (group.Hwnds.Count == 0)
                {
                    if (group.IconHash != null) _iconHashToGroupKey.Remove(group.IconHash); // [ICON-HASH]
                    _groups.Remove(group.GroupKey ?? group.ExeName); RemoveButtonAnimated(group.Button, group);
                }
                else SortButtonsByPinOrder();
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UnpinGroup: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // PREVIEW [STAB-3] [STAB-9]
        // ═════════════════════════════════════════════════════════════════════
        void InitPreviewWindow()
        {
            _previewWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Visibility = Visibility.Hidden
            };
            _previewWindow.SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(_previewWindow).Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
            };
            _previewWindow.Show(); _previewWindow.Hide();
            _previewWindow.MouseEnter += (s, e) => { _previewHideTimer?.Stop(); _previewShowTimer?.Stop(); };
            _previewWindow.MouseLeave += (s, e) => ScheduleHidePreview();
            if (_menuWindow != null) { _menuWindow.PreviewWindow = _previewWindow; _menuWindow.TaskbarWindow = this; }
            _previewHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _previewHideTimer.Tick += (s, e) => { _previewHideTimer.Stop(); HidePreview(); };
            _previewShowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _previewShowTimer.Tick += (s, e) =>
            {
                _previewShowTimer.Stop();
                if (_pendingPreviewGroup != null && _pendingPreviewBtn != null)
                    ShowPreview(_pendingPreviewGroup, _pendingPreviewBtn);
            };
            // [FIX-PREVIEW] Вспомогательный метод: показать немедленно или запустить таймер
        }

        (FrameworkElement card, Border thumbSlot) CreatePreviewCard(IntPtr hwnd, AppGroup group)
        {
            // [STAB-4] SafeGetWindowText
            string title = SafeGetWindowText(hwnd); if (string.IsNullOrEmpty(title)) title = group?.ExeName ?? "—";
            IntPtr cap = hwnd;
            var nb = Frozen(Color.FromArgb(220, 32, 32, 32)); var hb = Frozen(Color.FromArgb(255, 50, 50, 55));
            var nb2 = Frozen(Color.FromArgb(60, 255, 255, 255)); var hb2 = Frozen(Color.FromArgb(120, 255, 255, 255));
            var cbPath = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 9.5,9.5 L 18.5,18.5 M 18.5,9.5 L 9.5,18.5"), Stroke = new SolidColorBrush(Color.FromRgb(255, 255, 255)), StrokeThickness = 1.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = PREVIEW_TITLE_HEIGHT, Height = PREVIEW_TITLE_HEIGHT, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true, UseLayoutRounding = true, Stretch = Stretch.None };
            var cb = new Border { Tag = "close", Width = PREVIEW_TITLE_HEIGHT, Height = PREVIEW_TITLE_HEIGHT, Background = Brushes.Transparent, CornerRadius = new CornerRadius(0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Cursor = Cursors.Hand, Visibility = Visibility.Hidden, Child = cbPath };
            var card = new Border { Width = PREVIEW_CARD_WIDTH, Background = nb, BorderBrush = nb2, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(0), Margin = new Thickness(PREVIEW_GAP, 0, PREVIEW_GAP, 0), Cursor = Cursors.Hand, SnapsToDevicePixels = true, UseLayoutRounding = true };
            card.MouseEnter += (s, e) => { _previewHideTimer?.Stop(); card.Background = hb; card.BorderBrush = hb2; cb.Visibility = Visibility.Visible; };
            card.MouseLeave += (s, e) => { card.Background = nb; card.BorderBrush = nb2; cb.Visibility = Visibility.Hidden; ScheduleHidePreview(); };
            card.MouseLeftButtonUp += (s, e) =>
            {
                try
                {
                    if (e.OriginalSource is Border b2 && b2.Tag is string t2 && t2 == "close") return; HidePreview(); _menuWindow?.HideAnimated();
                    // [STAB-11]
                    if (!IsWindow(cap)) return;
                    if (IsIconic(cap)) ShowWindow(cap, SW_RESTORE); SetForegroundWindow(cap);
                }
                catch { }
            };
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PREVIEW_TITLE_HEIGHT) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PREVIEW_THUMB_HEIGHT) });
            var tr = new Grid { Margin = new Thickness(0) };
            tr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            tr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PREVIEW_TITLE_HEIGHT) });
            Grid.SetRow(tr, 0);
            if (group?.Icon != null) { var ii = new System.Windows.Controls.Image { Source = group.Icon, Width = 14, Height = 14, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, SnapsToDevicePixels = true, Margin = new Thickness(4, 0, 0, 0) }; RenderOptions.SetBitmapScalingMode(ii, BitmapScalingMode.HighQuality); Grid.SetColumn(ii, 0); tr.Children.Add(ii); }
            string st = title.Length > 24 ? title.Substring(0, 24) + "…" : title;
            var tt = new TextBlock { Text = st, Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), FontSize = 10, FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 2, 0) };
            Grid.SetColumn(tt, 1); tr.Children.Add(tt);
            cb.MouseEnter += (s, e) => cb.Background = new SolidColorBrush(Color.FromArgb(220, 232, 17, 35));
            cb.MouseLeave += (s, e) => cb.Background = Brushes.Transparent;
            cb.MouseLeftButtonUp += (s, e) =>
            {
                try
                {
                    e.Handled = true;
                    // [STAB-13] IsWindow перед PostMessage
                    CloseWindow(cap, IsUwpAppName(group.ExeName));
                    if (card.Parent is Panel pp) pp.Children.Remove(card);
                    if (_previewWindow?.Content is Border ob && ob.Child is StackPanel sp && sp.Children.Count == 0) HidePreview();
                }
                catch { }
            };
            Grid.SetColumn(cb, 2); tr.Children.Add(cb); root.Children.Add(tr);
            var ts = new Border { Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), Margin = new Thickness(4, 0, 4, 4), SnapsToDevicePixels = true, UseLayoutRounding = true };
            Grid.SetRow(ts, 1); root.Children.Add(ts); card.Child = root;
            return (card, ts);
        }

        void ShowPreview(AppGroup group, Button btn)
        {
            try
            {
                _previewHideTimer?.Stop();
                if (group == null || group.Hwnds.Count == 0) { HidePreview(); return; }
                DwmIsCompositionEnabled(out bool dwm); if (!dwm) return;
                _currentPreviewGroup = group;
                IntPtr ph; try { ph = new WindowInteropHelper(_previewWindow).Handle; } catch { return; }
                if (ph == IntPtr.Zero) { _previewWindow.Show(); _previewWindow.Hide(); ph = new WindowInteropHelper(_previewWindow).Handle; if (ph == IntPtr.Zero) return; }
                UnregisterAllThumbnails();
                int cnt = Math.Min(group.Hwnds.Count, 8);
                double tw = cnt * (PREVIEW_CARD_WIDTH + PREVIEW_GAP * 2) + PREVIEW_PADDING * 2;
                var ob = new Border { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(PREVIEW_PADDING), SnapsToDevicePixels = true };
                var sp = new StackPanel { Orientation = Orientation.Horizontal, SnapsToDevicePixels = true };
                var slots = new List<(Border, IntPtr)>(cnt);
                for (int i = 0; i < cnt; i++)
                {
                    var h = group.Hwnds[i];
                    // [STAB-3] Пропуск зависших окон в preview
                    if (!IsWindow(h)) continue;
                    try { if (IsHungAppWindow(h)) continue; } catch { continue; }
                    try { var (c, ts2) = CreatePreviewCard(h, group); sp.Children.Add(c); slots.Add((ts2, h)); } catch { }
                }
                if (sp.Children.Count == 0) { HidePreview(); return; }
                ob.Child = sp; _previewWindow.Content = ob; _previewWindow.SizeToContent = SizeToContent.WidthAndHeight; _previewWindow.UpdateLayout();
                var bp = btn.PointToScreen(new System.Windows.Point(0, 0)); double sw2 = SystemParameters.PrimaryScreenWidth;
                double left = bp.X + btn.ActualWidth / 2 - tw / 2; if (left < 4) left = 4; if (left + tw > sw2 - 4) left = sw2 - tw - 4;
                _previewWindow.Left = left;
                _previewWindow.Top = _isBottom
                    ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - _previewWindow.ActualHeight - 6
                    : TASKBAR_HEIGHT + 6;
                _previewWindow.Visibility = Visibility.Visible; _previewWindow.UpdateLayout();
                foreach (var (ts2, h) in slots) try { RegisterThumbnailForSlot(ph, h, ts2); } catch { }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] ShowPreview: {ex.Message}"); }
        }

        void RegisterThumbnailForSlot(IntPtr dest, IntPtr src, Border slot)
        {
            // [STAB-3] Полная проверка перед DwmRegisterThumbnail
            if (dest == IntPtr.Zero || src == IntPtr.Zero || slot == null) return;
            if (!IsWindow(src)) return;
            try { if (IsHungAppWindow(src)) return; } catch { return; }
            try
            {
                if (DwmRegisterThumbnail(dest, src, out IntPtr th) != 0) return;
                // [STAB-9] lock при добавлении в список
                lock (_thumbnailLock) { _thumbnailHandles.Add(th); }
                var pos = slot.PointToScreen(new System.Windows.Point(0, 0));
                int dL = (int)(pos.X - _previewWindow.Left), dT = (int)(pos.Y - _previewWindow.Top);
                int dR = dL + Math.Max(1, (int)slot.ActualWidth), dB = dT + Math.Max(1, (int)slot.ActualHeight);
                if (dR <= dL) dR = dL + PREVIEW_CARD_WIDTH - 8; if (dB <= dT) dB = dT + PREVIEW_THUMB_HEIGHT - 4;
                var props = new DWM_THUMBNAIL_PROPERTIES { dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY, rcDestination = new RECT { left = dL, top = dT, right = dR, bottom = dB }, opacity = 255, fVisible = true };
                DwmUpdateThumbnailProperties(th, ref props);
            }
            catch { }
        }

        // [STAB-9] Атомарная очистка thumbnail-хэндлов
        void UnregisterAllThumbnails()
        {
            List<IntPtr> toFree;
            lock (_thumbnailLock)
            {
                toFree = new List<IntPtr>(_thumbnailHandles);
                _thumbnailHandles.Clear();
            }
            foreach (var h in toFree)
                try { if (h != IntPtr.Zero) DwmUnregisterThumbnail(h); } catch { }
        }

        void ScheduleHidePreview() { try { _previewHideTimer?.Stop(); _previewHideTimer?.Start(); } catch { } }
        void HidePreview()
        {
            try
            {
                // [FIX-PREVIEW] Останавливаем оба таймера и чистим всё состояние,
                // чтобы при следующем наведении всё начиналось с чистого листа.
                _previewShowTimer?.Stop();
                _previewHideTimer?.Stop();
                _pendingPreviewGroup = null;
                _pendingPreviewBtn = null;
                UnregisterAllThumbnails();
                if (_previewWindow != null) _previewWindow.Visibility = Visibility.Hidden;
                _currentPreviewGroup = null;
            }
            catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        // [DPI-AUTO] Авто-масштабирование при смене DPI/масштаба Windows
        // ═════════════════════════════════════════════════════════════════════
        IntPtr DpiChangedHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_DPICHANGED) return IntPtr.Zero;
            try
            {
                // wParam: LOWORD = новый DPI по X, HIWORD = новый DPI по Y
                int newDpiX = wParam.ToInt32() & 0xFFFF;
                if (newDpiX <= 0) newDpiX = 96;
                double newScale = newDpiX / 96.0;
                if (Math.Abs(newScale - _currentDpiScale) < 0.001) return IntPtr.Zero;

                _currentDpiScale = newScale;
                Debug.WriteLine($"[DPI-AUTO] DPI changed: {newDpiX} ({newScale*100:F0}%)");

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        SafeRun(PositionTaskbar,    "PositionTaskbar@DpiChanged");
                        SafeRun(ReserveScreenSpace, "ReserveScreenSpace@DpiChanged");
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch { }
            return IntPtr.Zero;
        }

        // ═════════════════════════════════════════════════════════════════════
        // [ATTENTION] Win10-style attention indication — shell hook HSHELL_FLASH
        // ═════════════════════════════════════════════════════════════════════
        const int HSHELL_FLASH = 0x8006;  // окно требует внимания (FlashWindow)

        void RegisterShellAttentionHook()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                _wmShellHook = RegisterWindowMessage("SHELLHOOK");
                RegisterShellHookWindow(hwnd);
                Debug.WriteLine($"[ATTENTION] Shell hook registered, WM=0x{_wmShellHook:X}");
            }
            catch (Exception ex) { Debug.WriteLine($"[ATTENTION] Register: {ex.Message}"); }
        }

        // WndProc-хук: ловим SHELLHOOK с кодом HSHELL_FLASH
        IntPtr ShellAttentionHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                if (_wmShellHook != 0 && (uint)msg == _wmShellHook)
                {
                    // HSHELL_FLASH (0x8006) — единственный код, который Win10 taskbar
                    // использует для индикации "требует внимания".
                    // HSHELL_REDRAW (0x0006) — изменение заголовка/перерисовка, НЕ внимание.
                    // Ранее второе условие ошибочно трактовало HSHELL_REDRAW как flash,
                    // что вызывало ложные срабатывания у Discord, Teams, браузеров и т.д.
                    int rawCode = wParam.ToInt32();
                    // FIX: проверяем ТОЛЬКО точное значение 0x8006, без маскирования
                    bool isFlash = (rawCode == HSHELL_FLASH);  // 0x8006 = реальный HSHELL_FLASH
                    if (isFlash)
                    {
                        IntPtr flashHwnd = lParam;
                        if (flashHwnd != IntPtr.Zero)
                            Dispatcher.BeginInvoke(new Action(() => OnWindowFlash(flashHwnd)));
                    }
                    // FIX: HSHELL_WINDOWACTIVATED (4) и HSHELL_RUDEAPPACTIVATED (0x8004) —
                    // окно получило фокус. Win10 taskbar снимает attention при активации.
                    // Это покрывает случаи Alt+Tab и клика по самому окну (не по кнопке).
                    bool isActivated = (rawCode == 4) || (rawCode == 0x8004);
                    if (isActivated)
                    {
                        IntPtr activeHwnd = lParam;
                        if (activeHwnd != IntPtr.Zero)
                            Dispatcher.BeginInvoke(new Action(() => OnWindowActivatedByShell(activeHwnd)));
                    }
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        void OnWindowFlash(IntPtr flashHwnd)
        {
            try
            {
                // Найти группу которой принадлежит это окно
                AppGroup target = null;
                foreach (var g in _groups.Values)
                    if (g.Hwnds.Contains(flashHwnd)) { target = g; break; }

                if (target == null) return;

                // Не начинать заново если уже мигает или в статичном attention-режиме
                if (target.NeedsAttention) return;

                // [FIX-FALSE-FLASH-1] Игнорируем flash если окно уже в фокусе.
                // Telegram вызывает FlashWindow при переключении чатов — это не реальное
                // требование внимания, а внутренняя логика приложения.
                try
                {
                    IntPtr fg = GetForegroundWindow();
                    if (fg != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(fg, out uint fgPid);
                        GetWindowThreadProcessId(flashHwnd, out uint flashPid);
                        if (fgPid != 0 && fgPid == flashPid)
                        {
                            Debug.WriteLine("[ATTENTION] Ignored false flash from " + target.ExeName + " — already foreground");
                            return;
                        }
                    }
                }
                catch { }

                // [FIX-FALSE-FLASH-2] Игнорируем flash если у группы нет живых окон.
                if (target.Hwnds.Count == 0)
                {
                    Debug.WriteLine("[ATTENTION] Ignored flash from " + target.ExeName + " — no visible windows");
                    return;
                }

                // [FIX-FALSE-FLASH-3] Некоторые системные процессы никогда не требуют
                // внимания в Win10 — explorer, система, фоновые сервисы.
                if (string.Equals(target.ExeName, "explorer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(target.ExeName, "svchost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(target.ExeName, "sihost", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine("[ATTENTION] Ignored flash from " + target.ExeName + " — system process");
                    return;
                }

                target.NeedsAttention = true;
                target.FlashStartTime = DateTime.UtcNow;
                _attentionGroups.Add(target);

                // [ATTENTION-SHOW] Если панель скрыта (fullscreen-режим или не видна) —
                // показываем её чтобы пользователь увидел мигающую кнопку.
                if (_isHiddenByFullscreen || Visibility != Visibility.Visible)
                {
                    // [MONITOR-FIX] Показываем панель только если fullscreen-окно
                    // больше не покрывает основной монитор. Если покрывает — мигаем
                    // кнопкой, но панель не показываем (чтобы не мешать игре).
                    if (!IsWindowCoveringPrimaryMonitor(_lastFullscreenHwnd))
                    {
                        _isHiddenByFullscreen = false;
                        _fullscreenConfirmCount = 0;
                        _notFullscreenConfirmCount = 0;
                        ShowTaskbar(animate: true);
                        Debug.WriteLine($"[ATTENTION] ShowTaskbar triggered by flash from {target.ExeName}");
                    }
                    else
                    {
                        // [ATTENTION-NOACTIVATE] Fullscreen-игра на экране — показываем панель
                        // НЕ забирая фокус. Кнопка мигает, панель видна, игра не прерывается.
                        _shownByAttention = true;
                        ShowTaskbar(animate: true, noActivate: true);
                        InstallAttentionMouseHook();
                        Debug.WriteLine($"[ATTENTION] ShowTaskbar noActivate triggered by flash from {target.ExeName}");
                    }
                }

                // Запустить мигание (StartFlashForGroup уже добавляет в _flashingButtons)
                StartFlashForGroup(target);

                // Через 3 секунды остановить мигание, оставить статичный оранжевый фон
                var stopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                stopTimer.Tick += (s, e) =>
                {
                    stopTimer.Stop();
                    try
                    {
                        // Убрать из мигающих, но оставить NeedsAttention=true → статичный фон
                        if (target.Button != null)
                        {
                            _flashingButtons.Remove(target.Button);
                            if (_flashingButtons.Count == 0) _flashTimer.Stop();
                            // Установить статичный оранжевый фон кнопки
                            ApplyAttentionBackground(target.Button, true);
                        }
                    }
                    catch { }
                };
                stopTimer.Start();
                Debug.WriteLine($"[ATTENTION] Flash started for {target.ExeName}");
            }
            catch (Exception ex) { Debug.WriteLine($"[ATTENTION] OnWindowFlash: {ex.Message}"); }
        }

        void ApplyAttentionBackground(Button btn, bool on)
        {
            if (btn == null) return;
            try
            {
                btn.ApplyTemplate();
                var rb = btn.Template?.FindName("RunBar", btn) as Rectangle;
                var ab = btn.Template?.FindName("AppBorder", btn) as Border;
                if (ab != null) ab.Background = on ? BrushAttentionBg : BrushTransparent;
                if (rb != null && on) { rb.Visibility = Visibility.Visible; rb.Fill = BrushFlashPip; }
            }
            catch { }
        }

        // [FIX-ATTENTION] Вызывается когда окно получает фокус через shell hook.
        // Win10 taskbar всегда снимает attention при активации окна — любым способом.
        void OnWindowActivatedByShell(IntPtr activeHwnd)
        {
            try
            {
                foreach (var g in _groups.Values)
                {
                    if (!g.NeedsAttention) continue;
                    if (g.Hwnds.Contains(activeHwnd))
                    {
                        ClearAttention(g);
                        Debug.WriteLine($"[ATTENTION] Cleared for {g.ExeName} via shell activation");
                        break;
                    }
                }
            }
            catch { }
        }

        void ClearAttention(AppGroup g)
        {
            if (g == null) return;
            try
            {
                g.NeedsAttention = false;
                _attentionGroups.Remove(g);
                _flashingButtons.Remove(g.Button);
                if (_flashingButtons.Count == 0) _flashTimer?.Stop();
                ApplyAttentionBackground(g.Button, false);
                // [ATTENTION-AUTOHIDE] Если панель была показана noActivate во время игры
                // и больше нет групп требующих внимания — скрываем панель обратно.
                if (_attentionGroups.Count == 0
                    && IsWindowCoveringPrimaryMonitor(_lastFullscreenHwnd)
                    && Visibility == Visibility.Visible
                    && _isHiddenByFullscreen)
                {
                    _shownByAttention = false;
                    UninstallAttentionMouseHook();
                    HideTaskbar(animate: true);
                    Debug.WriteLine("[ATTENTION] All attention cleared — auto-hiding taskbar (fullscreen active)");
                }
            }
            catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        // FLASH
        // ═════════════════════════════════════════════════════════════════════
        void StartFlashTimer()
        {
            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _flashTimer.Tick += (s, e) =>
            {
                try
                {
                    _flashState = !_flashState;
                    foreach (var b in _flashingButtons.ToList())
                    {
                        b.ApplyTemplate();
                        var rb = b.Template?.FindName("RunBar", b) as Rectangle;
                        var ab = b.Template?.FindName("AppBorder", b) as Border;
                        if (ab != null) ab.Background = _flashState ? BrushFlashOn : BrushTransparent;
                        // Пипка: ярко-оранжевая когда on, скрытая когда off
                        if (rb != null)
                        {
                            rb.Visibility = _flashState ? Visibility.Visible : Visibility.Collapsed;
                            if (_flashState) rb.Fill = BrushFlashPip;
                        }
                    }
                }
                catch { }
            };
        }

        void StartFlashForGroup(AppGroup g) { if (g?.Button == null) return; try { _flashingButtons.Add(g.Button); if (!_flashTimer.IsEnabled) _flashTimer.Start(); } catch { } }
        void StopFlash(AppGroup g) { if (g?.Button == null) return; try { _flashingButtons.Remove(g.Button); HighlightButton(g.Button, false); if (_flashingButtons.Count == 0) _flashTimer.Stop(); } catch { } }
        void HighlightButton(Button btn, bool on) { if (btn == null) return; try { btn.ApplyTemplate(); var ab = btn.Template?.FindName("AppBorder", btn) as Border; if (ab != null) ab.Background = on ? BrushFlashOn : BrushTransparent; } catch { } }

        void ApplyButtonState(Button btn, string state)
        {
            if (btn == null) return;
            try
            {
                // [ATTENTION] Если окно стало активным — сбрасываем флаг внимания
                if (state == "Active")
                {
                    var flashGroup = _groups.Values.FirstOrDefault(g => g.Button == btn);
                    if (flashGroup != null && flashGroup.NeedsAttention)
                        ClearAttention(flashGroup);
                    else if (_flashingButtons.Contains(btn))
                        StopFlash(_groups.Values.FirstOrDefault(g => g.Button == btn));
                }
                else if (_flashingButtons.Contains(btn) && state == "Active")
                    StopFlash(_groups.Values.FirstOrDefault(g => g.Button == btn));

                // Если кнопка в attention-режиме (статичный фон) — не затирать его при Running
                var attGroup = _groups.Values.FirstOrDefault(g => g.Button == btn);
                if (attGroup != null && attGroup.NeedsAttention && state == "Running")
                {
                    // Оставить оранжевый фон + показать пипку в оранжевом
                    ApplyAttentionBackground(btn, true);
                    return;
                }

                btn.ApplyTemplate();
                var rb = btn.Template?.FindName("RunBar", btn) as Rectangle;
                var ab = btn.Template?.FindName("AppBorder", btn) as Border;
                if (rb == null || ab == null) return;
                switch (state)
                {
                    case "Active":  rb.Visibility = Visibility.Visible;   rb.Fill = BrushActiveBar;   ab.Background = BrushActiveBg;    break;
                    case "Running": rb.Visibility = Visibility.Visible;   rb.Fill = BrushRunningBar;  ab.Background = BrushTransparent; break;
                    default:        rb.Visibility = Visibility.Collapsed;                              ab.Background = BrushTransparent; break;
                }
            }
            catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        // POSITION / CLOCK / START BUTTON
        // ═════════════════════════════════════════════════════════════════════
        void Window_SizeChanged(object sender, SizeChangedEventArgs e) { try { PositionTaskbar(); } catch { } }

        // [STAB-14] IsLoaded-guard
        void PositionTaskbar()
        {
            try
            {
                if (!IsLoaded || !IsVisible) return;
                UpdateLayout();

                var source = PresentationSource.FromVisual(this);
                double dpi = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

                // [FIX-CENTER-v2] Считаем всё в физических пикселях, затем конвертируем.
                // SystemParameters.PrimaryScreenWidth — в DIP, умножаем → физические пиксели экрана.
                // ActualWidth — в DIP, умножаем → физические пиксели панели.
                // Целочисленное деление в пикселях даёт идеальный центр без погрешности округления.
                double screenPx = Math.Round(SystemParameters.PrimaryScreenWidth * dpi);
                double panelPx = Math.Round(ActualWidth * dpi);
                double leftPx = Math.Floor((screenPx - panelPx) / 2.0);
                Left = leftPx / dpi;

                if (!_taskbarAnimating)
                    Top = _isBottom ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT : 0;
                Height = TASKBAR_HEIGHT;

                // Обновляем 1px-линии поверх контента (поверх RunBar-индикаторов)
                // Bottom-режим: линия сверху, Top-режим: линия снизу; правая — всегда
                if (BorderLineTop != null)
                    BorderLineTop.Visibility = _isBottom ? Visibility.Visible : Visibility.Collapsed;
                if (BorderLineBottom != null)
                    BorderLineBottom.Visibility = _isBottom ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] PositionTaskbar: {ex.Message}"); }
        }

        // [FIX-DRAG] Добавляем WS_EX_LAYERED к окну.
        // Без этого флага HTTRANSPARENT в NcHitTestHook не работает корректно на всех билдах Windows.
        // WS_EX_TRANSPARENT намеренно НЕ выставляем глобально — тогда нельзя кликать по кнопкам.
        void ApplyLayeredStyle()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] ApplyLayeredStyle: {ex.Message}"); }
        }

        // [FIX-DRAG2] Регистрируем пустой IDropTarget — это говорит Windows что окно
        // участвует в OLE drag&drop, но мы отдаём DROPEFFECT_NONE, тем самым
        // позволяя дропу «провалиться» к окну ниже.
        PassthroughDropTarget _passthroughDrop;
        void RegisterPassthroughDrop()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                _passthroughDrop = new PassthroughDropTarget();
                RegisterDragDrop(hwnd, _passthroughDrop);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] RegisterPassthroughDrop: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // СОХРАНЕНИЕ / ЗАГРУЗКА ПОЗИЦИИ ПАНЕЛИ
        // ═════════════════════════════════════════════════════════════════════
        static readonly string _settingsPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MyTaskbar", "settings.xml");

        void SavePosition()
        {
            try
            {
                string dir = IOPath.GetDirectoryName(_settingsPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string posLine = _isBottom ? "bottom" : "top";
                string scaleLine = _uiScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
                File.WriteAllText(_settingsPath, posLine + "\n" + scaleLine);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] SavePosition: {ex.Message}"); }
        }

        void LoadPosition()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return;
                string[] lines = File.ReadAllText(_settingsPath).Trim().Split('\n');
                if (lines.Length > 0 && lines[0].Trim() == "bottom") _isBottom = true;
                if (lines.Length > 1)
                {
                    if (double.TryParse(lines[1].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double s))
                    {
                        _uiScale = Math.Max(1.0, Math.Min(1.5, Math.Round(s, 2)));
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LoadPosition: {ex.Message}"); }
        }
        void InitNotifyIcon()
        {
            try
            {
                // Создаём иконку из стандартного системного ресурса Windows
                // (IDI_APPLICATION = 32512) — не требует GDI/System.Drawing
                IntPtr hIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
                var icon = hIcon != IntPtr.Zero
                    ? System.Drawing.Icon.FromHandle(hIcon)
                    : System.Drawing.SystemIcons.Application;

                _appNotifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon    = icon,
                    Text    = "MyTaskbar — click to move the taskbar",
                    Visible = true
                };
                _appNotifyIcon.Click += (s, e) =>
                    Dispatcher.Invoke(ToggleTaskbarPosition);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"InitNotifyIcon error:\n{ex}",
                    "MyTaskbar", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }

        void CloseAllPopups()
        {
            try { if (_menuWindow != null && _menuWindow.IsVisible) _menuWindow.HideAnimated(); } catch { }
            try { if (_wifiWindow != null && _wifiWindow.IsVisible) _wifiWindow.Hide(); } catch { }
            try { if (_volumeFlyout != null && _volumeFlyout.IsVisible) _volumeFlyout.Hide(); } catch { }
            try { if (_brightnessFlyout != null && _brightnessFlyout.IsVisible) _brightnessFlyout.Hide(); } catch { }
            try { if (_trayWindow != null && _trayWindow.IsOpen) _trayWindow.SlideUp(); } catch { }
            try { HidePreview(); } catch { }
        }

        // ── Settings window ────────────────────────────────────────────────────
        void OpenSettingsWindow()
        {
            if (_settingsWindow != null && _settingsWindow.IsVisible)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(_isBottom, _uiScale, _fullscreenAutoHide);

            _settingsWindow.PositionChanged += (isBot) =>
            {
                if (isBot != _isBottom)
                    Dispatcher.Invoke(ToggleTaskbarPosition);
            };

            _settingsWindow.ScaleChanged += (newScale) =>
            {
                _uiScale = newScale;
                Dispatcher.Invoke(ApplyUIScale);
                SaveUIScale();
            };

            _settingsWindow.FullscreenChanged += (enabled) =>
            {
                _fullscreenAutoHide = enabled;
                if (!enabled)
                {
                    // show taskbar immediately if it was hidden by fullscreen
                    if (_isHiddenByFullscreen)
                    {
                        _isHiddenByFullscreen = false;
                        _fullscreenConfirmCount = 0;
                        _notFullscreenConfirmCount = 0;
                        ShowTaskbar(animate: true);
                    }
                }
                SaveFullscreenAutoHide();
            };

            // Position the window near the taskbar edge, horizontally centered
            _settingsWindow.Loaded += (s, e) =>
            {
                double sw = SystemParameters.PrimaryScreenWidth;
                double ww = _settingsWindow.ActualWidth;
                double wh = _settingsWindow.ActualHeight;
                _settingsWindow.Left = (sw - ww) / 2;
                if (_isBottom)
                    _settingsWindow.Top = SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - wh - 8;
                else
                    _settingsWindow.Top = TASKBAR_HEIGHT + 8;
            };

            _settingsWindow.Show();
        }

        void SaveFullscreenAutoHide()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(_settingsPath);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                // Append/overwrite a separate file next to the main settings file
                string path = System.IO.Path.Combine(dir, "fullscreen_hide.txt");
                System.IO.File.WriteAllText(path, _fullscreenAutoHide ? "1" : "0");
            }
            catch { }
        }

        void LoadFullscreenAutoHide()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(_settingsPath);
                string path = System.IO.Path.Combine(dir, "fullscreen_hide.txt");
                if (System.IO.File.Exists(path))
                {
                    string v = System.IO.File.ReadAllText(path).Trim();
                    _fullscreenAutoHide = (v != "0");
                }
            }
            catch { }
        }

        void ToggleTaskbarPosition()

        {
            _isBottom = !_isBottom;

            // Закрываем все открытые попапы — они привязаны к старой позиции
            CloseAllPopups();

            // Убираем трансформацию — содержимое панели не зеркалим
            // [UI-SCALE] Сохраняем масштаб панели после смены позиции
            MainPanel.LayoutTransform = Math.Abs(_uiScale - 1.0) < 0.01
                ? Transform.Identity
                : new ScaleTransform(_uiScale, _uiScale);

            // Обновляем позицию окна и рабочую область
            PositionTaskbar();
            ReserveScreenSpace();

            // Сохраняем позицию для следующего запуска
            SavePosition();

            // Обновляем tooltip иконки трея
            if (_appNotifyIcon != null)
                _appNotifyIcon.Text = _isBottom
                    ? "MyTaskbar — taskbar at bottom  (click → move to top)"
                    : "MyTaskbar — taskbar at top (click → move to bottom)";
        }

        // [UI-SCALE] Изменить масштаб панели и всех всплывающих окон.
        void ChangeUIScale(double delta)
        {
            double newScale = Math.Round(_uiScale + delta, 2);
            newScale = Math.Round(Math.Max(1.0, Math.Min(1.5, newScale)), 2);
            if (Math.Abs(newScale - _uiScale) < 0.01) return;

            _uiScale = newScale;
            ApplyUIScale();
            SaveUIScale();
        }

        void ApplyUIScale()
        {
            // Главная панель
            MainPanel.LayoutTransform = new ScaleTransform(_uiScale, _uiScale);
            PositionTaskbar();
            ReserveScreenSpace();

            // Передаём масштаб открытым попап-окнам
            // [UI-SCALE-FIX] Для MenuWindow: сначала передаём масштаб (LayoutTransform),
            // потом вызываем RepositionAfterScale() — пересчёт VisibleTop с новым ScaledTaskbarH.
            if (_menuWindow   != null)
            {
                _menuWindow.UIScale = _uiScale;
                _menuWindow.RepositionAfterScale();
            }
            if (_trayWindow   != null) _trayWindow.UIScale   = _uiScale;
            if (_volumeFlyout != null) _volumeFlyout.UIScale = _uiScale;
            if (_wifiWindow   != null) _wifiWindow.UIScale   = _uiScale;
            if (_brightnessFlyout != null) _brightnessFlyout.UIScale = _uiScale;
        }

        void SaveUIScale()
        {
            try
            {
                string dir = IOPath.GetDirectoryName(_settingsPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string posLine = _isBottom ? "bottom" : "top";
                string scaleLine = _uiScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
                File.WriteAllText(_settingsPath, posLine + "\n" + scaleLine);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] SaveUIScale: {ex.Message}"); }
        }

        void ReserveScreenSpace()
        {
            try
            {
                // [FIX-WORKAREA-DPI] SPI_SETWORKAREA требует физические пиксели, а не WPF DIP.
                // SystemParameters.PrimaryScreenWidth/Height — в DIP, нужно умножить на DPI.
                // TASKBAR_HEIGHT тоже в DIP — тоже умножаем.
                // Иначе при DPI > 96 (125%, 150%) right/bottom оказываются меньше экрана
                // и maximized-окна (Chrome, Edge) получают зазор справа и сверху.
                var source = PresentationSource.FromVisual(this);
                double dpi = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                int swPx = (int)Math.Round(SystemParameters.PrimaryScreenWidth  * dpi);
                int shPx = (int)Math.Round(SystemParameters.PrimaryScreenHeight * dpi);
                int thPx = (int)Math.Round(TASKBAR_HEIGHT * dpi);
                RECT wa = _isBottom
                    ? new RECT { left = 0, top = 0,    right = swPx, bottom = shPx - thPx }
                    : new RECT { left = 0, top = thPx, right = swPx, bottom = shPx };
                SystemParametersInfo(SPI_SETWORKAREA, 0, ref wa, 0x01 | 0x02);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] ReserveScreenSpace: {ex.Message}"); }
        }

        void StartClock()
        {
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) => UpdateClock();
            _clockTimer.Start(); UpdateClock();
        }

        void UpdateClock()
        {
            try
            {
                var n = DateTime.Now;
                if (TimeLabel != null) TimeLabel.Text = n.ToString("HH:mm:ss");
                if (DateLabel != null) DateLabel.Text = n.ToString("dd.MM.yyyy");
            }
            catch { }
        }

        // [WINX] ПКМ на кнопке Пуск — WinX-меню
        void StartButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                MarkOwnActivity();
                var menu = new ContextMenu { Style = TryFindResource("DarkContextMenu") as Style };

                void AddItem(string label, Action action, bool bold = false)
                {
                    var item = MakeMenuItem(label, null, bold);
                    item.Click += (s2, e2) => { try { action(); } catch { } };
                    menu.Items.Add(item);
                }
                void AddSep() { menu.Items.Add(new Separator()); }
                void Run(string file, string args = "", bool admin = false)
                {
                    var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
                    if (admin) psi.Verb = "runas";
                    Process.Start(psi);
                }

                AddItem("Диспетчер задач",                 () => Run("taskmgr.exe"));
                AddSep();
                AddItem("Диспетчер устройств",             () => Run("devmgmt.msc"));
                AddItem("Управление дисками",              () => Run("diskmgmt.msc"));
                AddItem("Управление компьютером",          () => Run("compmgmt.msc"));
                AddSep();
                AddItem("PowerShell",                      () => Run("powershell.exe"));
                AddItem("PowerShell (администратор)",      () => Run("powershell.exe", "", true));
                AddItem("Командная строка",                () => Run("cmd.exe"));
                AddItem("Командная строка (администратор)", () => Run("cmd.exe", "", true));
                AddSep();
                AddItem("Система",                         () => Run("ms-settings:about", "", false));
                AddItem("Параметры",                       () => Run("ms-settings:"));
                AddItem("Проводник",                       () => Run("explorer.exe"));

                menu.PlacementTarget = StartButton;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                menu.IsOpen = true;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] StartButton_RightClick: {ex.Message}"); }
        }

        void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MarkOwnActivity(); EnsureMenuWindow();
                if (_menuWindow.IsVisible) { _menuWindow.HideAnimated(); return; }
                if (_menuWindow.JustHidden) return;
                // [GAME-10] Если игра fullscreen — меню должно быть Topmost
                bool fps = IsCursorCapturedByFpsGame();
                _menuWindow.IsFullscreenMode = IsForegroundFullscreen() || fps;
                // [GAME-11] Восстанавливаем блокер если FPS захватил курсор
                if (fps) ShowFullscreenBlocker();
                _menuWindow.IsBottom = _isBottom;
                _menuWindow.ShowMenu();
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] StartButton_Click: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // CLOSED [STAB-15]
        // ═════════════════════════════════════════════════════════════════════
        void MainWindow_Closed(object sender, EventArgs e)
        {
            // Хук клавиатуры — первым
            _hookWatchdog?.Stop(); // [FIX-HOOK-WATCHDOG] останавливаем watchdog перед unhook
            if (_keyboardHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_keyboardHook); } catch { }
                _keyboardHook = IntPtr.Zero;
            }

            // [EXP-RESTART] WinEvent хук
            if (_winEventHook != IntPtr.Zero)
            {
                try { UnhookWinEvent(_winEventHook); } catch { }
                _winEventHook = IntPtr.Zero;
            }

            // [ATTENTION] Shell hook
            try { DeregisterShellHookWindow(new WindowInteropHelper(this).Handle); } catch { }

            // [FIX-TASKMGR-FOCUS] Хук фокуса — теперь в хелпере
            TaskmgrWatcher.StopFocusGuard();

            // [STAB-5] Отписка WTS
            try { WTSUnRegisterSessionNotification(new WindowInteropHelper(this).Handle); } catch { }

            // [STAB-15] Каждый таймер в отдельном try/catch
            void St(DispatcherTimer t, string n)
            { try { t?.Stop(); } catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] StopTimer {n}: {ex.Message}"); } }

            St(_taskbarWatcher, "taskbarWatcher");
            St(_activeTimer, "activeTimer");
            St(_clockTimer, "clockTimer");
            St(_langTimer, "langTimer");
            St(_batteryTimer, "batteryTimer");
            St(_wifiIconTimer, "wifiIconTimer");
            St(_volumeIconTimer, "volumeIconTimer");
            St(_brightnessIconTimer, "brightnessIconTimer");
            St(_bluetoothTimer, "bluetoothTimer");
            St(_edgeRevealTimer, "edgeRevealTimer");
            St(_flashTimer, "flashTimer");
            St(_previewHideTimer, "previewHideTimer");
            St(_fullscreenTimer, "fullscreenTimer");
            St(_previewShowTimer, "previewShowTimer");
            St(_resumeBlockTimer, "resumeBlockTimer");

            SafeRun(UnregisterAllThumbnails, "UnregisterAllThumbnails");

            try { _wifiWindow?.Close(); } catch { }
            try { _volumeFlyout?.Close(); } catch { }
            try { _brightnessFlyout?.Close(); } catch { }
            try { _previewWindow?.Close(); } catch { }
            try { _menuWindow?.Close(); } catch { }
            try { _trayWindow?.Close(); } catch { }
            try { _blockerWindow?.Close(); } catch { }

            // Убираем иконку из системного трея
            try { if (_appNotifyIcon != null) { _appNotifyIcon.Visible = false; _appNotifyIcon.Dispose(); _appNotifyIcon = null; } } catch { }

            // Восстановление системного таскбара
            SafeRun(() => TaskbarHelper.Show(), "TaskbarHelper.Show");

            // [FIX-DRAG2] Снимаем регистрацию OLE drop
            try { RevokeDragDrop(new WindowInteropHelper(this).Handle); } catch { }

            // Восстановление рабочей области
            try
            {
                double sw = SystemParameters.PrimaryScreenWidth;
                double sh = SystemParameters.PrimaryScreenHeight;
                RECT wa = new RECT { left = 0, top = 0, right = (int)sw, bottom = (int)sh };
                SystemParametersInfo(SPI_SETWORKAREA, 0, ref wa, 0x01 | 0x02);
            }
            catch { }
        }
    }
}