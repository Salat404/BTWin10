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
// [FIX-DRAG] NcHitTestHook + WS_EX_LAYERED: панель больше не мешает drag&drop других программ
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
        [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint dwProcessId);
        const uint ASFW_ANY = 0xFFFFFFFF;
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

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
        static extern uint GetClassLong32(IntPtr hWnd, int nIndex);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

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

        [StructLayout(LayoutKind.Sequential)]
        struct DWM_THUMBNAIL_PROPERTIES { public uint dwFlags; public RECT rcDestination; public RECT rcSource; public byte opacity; public bool fVisible; public bool fSourceClientAreaOnly; }

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);

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
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_KEYUP = 0x0101;
        const int WM_SYSKEYUP = 0x0105;
        const int VK_LWIN = 0x5B;
        const int VK_RWIN = 0x5C;

        readonly int TASKBAR_HEIGHT = 40;

        const int PREVIEW_CARD_WIDTH = 160;
        const int PREVIEW_THUMB_HEIGHT = 90;
        const int PREVIEW_TITLE_HEIGHT = 24;
        const int PREVIEW_PADDING = 6;
        const int PREVIEW_GAP = 3;

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
        static readonly SolidColorBrush BrushFlashOn = Frozen(Color.FromArgb(120, 255, 200, 0));
        static readonly SolidColorBrush BrushCloseHover = Frozen(Color.FromArgb(200, 196, 43, 28));

        // ── Дочерние окна ─────────────────────────────────────────────────────
        MenuWindow _menuWindow;
        TrayWindow _trayWindow;
        WifiWindow _wifiWindow;

        // ── Таймеры ───────────────────────────────────────────────────────────
        DispatcherTimer _clockTimer, _activeTimer, _langTimer, _batteryTimer,
                        _wifiIconTimer, _taskbarWatcher, _fullscreenTimer,
                        _volumeIconTimer, _brightnessIconTimer,
                        _edgeRevealTimer, _bluetoothTimer;

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

        // ── Прочее ────────────────────────────────────────────────────────────
        IntPtr _lastForegroundWindow = IntPtr.Zero;
        readonly string ShortcutsFolder;

        IntPtr _keyboardHook = IntPtr.Zero;
        LowLevelKeyboardProc _keyboardProc;

        volatile bool _winKeyDown;
        volatile bool _winUsedInCombo;
        volatile bool _shiftDown;
        volatile bool _ctrlDown;
        volatile bool _altDown;
        volatile bool _injectingWin;

        bool _isHiddenByFullscreen = false;
        bool _taskbarAnimating = false;

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
        const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        const int PBT_APMRESUMESUSPEND = 0x0007;

        readonly Dictionary<string, DateTime> _pendingNewGroups =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        const int NEW_GROUP_CONFIRM_MS = 1800;

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
            public Button Button;
            public List<IntPtr> Hwnds = new List<IntPtr>();
            public string LastTitle;
            public BitmapSource Icon;
            public bool IsPinned;
            public string LaunchPath;
            public int PinOrder = int.MaxValue;
            public bool IsUwp;
        }

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
            try { if (!Directory.Exists(ShortcutsFolder)) Directory.CreateDirectory(ShortcutsFolder); }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] Shortcuts folder: {ex.Message}"); }

            SafeRun(LoadPinnedApps, "LoadPinnedApps");
            SafeRun(LoadShortcuts, "LoadShortcuts");
            SafeRun(PositionTaskbar, "PositionTaskbar");
            SafeRun(ApplyAcrylicBackground, "ApplyAcrylicBackground");
            SafeRun(() => TaskbarHelper.Hide(), "TaskbarHelper.Hide");
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
            SafeRun(InstallKeyboardHook, "InstallKeyboardHook");
            SafeRun(StartFullscreenWatcher, "StartFullscreenWatcher");
            SafeRun(StartEdgeRevealWatcher, "StartEdgeRevealWatcher");
            SafeRun(HookPowerEvents, "HookPowerEvents");
            SafeRun(ApplyLayeredStyle, "ApplyLayeredStyle"); // [FIX-DRAG]
            SafeRun(RegisterPassthroughDrop, "RegisterPassthroughDrop"); // [FIX-DRAG2]
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
            catch (System.ComponentModel.Win32Exception) { return ""; }
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
                BitmapSource result = src;
                if (targetSize > 0 && (src.PixelWidth != targetSize || src.PixelHeight != targetSize))
                    result = ResizeBitmap(src, targetSize, targetSize);
                if (result != null && result.CanFreeze) result.Freeze();
                return result;
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

        const int UWP_ICON_SIZE = 48;

        // [STAB-7] Синхронный кэш — только HICON-методы (быстро)
        // Shell-методы вызываются асинхронно через LoadIconAsync
        BitmapSource GetCachedIconSafe(IntPtr hwnd, uint pid, string exePath)
        {
            string key = !string.IsNullOrEmpty(exePath) ? exePath : $"pid:{pid}";
            if (_iconCache.TryGetValue(key, out var c)) return c;
            BitmapSource result = null;
            if (hwnd != IntPtr.Zero && IsWindow(hwnd))
            {
                result = TryGetHIcon(SafeSendMessage(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero), 32)
                      ?? TryGetHIcon(GetClassLongPtr(hwnd, GCL_HICON), 32);
            }
            if (result == null && !string.IsNullOrEmpty(exePath) && IsLocalPath(exePath))
                result = TryShellItemImage(exePath, 32) ?? TrySHGetFileInfo(exePath, 32);
            if (result != null) _iconCache[key] = result;
            return result;
        }

        // [STAB-7] Асинхронная загрузка иконки с таймаутом 500ms
        void LoadIconAsync(AppGroup g, IntPtr hwnd, uint pid, string exePath)
        {
            if (g == null) return;
            var cts = new System.Threading.CancellationTokenSource(500);
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (cts.IsCancellationRequested) return;
                    BitmapSource icon = IsUwpAppName(g.ExeName)
                        ? GetCachedUwpIcon(g.ExeName)
                        : GetBestIcon(hwnd, pid, exePath, 32);
                    if (icon == null) return;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (g.Icon == null && g.Button != null)
                            { g.Icon = icon; UpdateButtonIcon(g.Button, icon); }
                        }
                        catch { }
                    }));
                }
                catch { }
                finally { cts.Dispose(); }
            }, cts.Token);
        }

        BitmapSource GetCachedUwpIcon(string n)
        {
            if (string.IsNullOrEmpty(n)) return null;
            string key = "uwp:" + n;
            if (_iconCache.TryGetValue(key, out var c)) return c;
            var raw = TryGetUwpIcon(n);
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
                        return TryUwpFolderPatternIcon("Microsoft.Windows.Photos*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("Microsoft.Photos.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("Microsoft.Photos.exe", UWP_ICON_SIZE);
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
            "calculatorapp","calculator","systemsettings",
        };

        static readonly HashSet<string> ForceShowProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "taskmgr","regedit","mmc","eventvwr",
            "devmgmt","diskmgmt","perfmon","resmon","cmd","powershell","windowsterminal",
            "wt","calc","notepad","mspaint","snippingtool","explorer",
            "hxoutlook","hxcalendarappimm","winstore","photos","video.ui","maps",
            "microsoft.bingweather","windowscamera","xboxapp","xboxgamingoverlay",
            "minecraftlauncher","minecraft","javaw",
        };

        // ═════════════════════════════════════════════════════════════════════
        // UWP РЕЗОЛВИНГ [FIX-2]
        // ═════════════════════════════════════════════════════════════════════
        static readonly (string[] Titles, string AppName)[] UwpTitleMap =
        {
            (new[]{"параметры","settings","параметры windows","system settings","windows settings"}, "systemsettings"),
            (new[]{"калькулятор","calculator"}, "calculator"),
            (new[]{"почта","mail"}, "hxoutlook"),
            (new[]{"календарь","calendar"}, "hxcalendarappimm"),
            (new[]{"microsoft store","магазин microsoft","магазин"}, "winstore"),
            (new[]{"фотографии","photos","фото"}, "photos"),
            (new[]{"кино и тв","movies & tv","фильмы"}, "video.ui"),
            (new[]{"карты","maps"}, "maps"),
            (new[]{"погода","msn погода","weather"}, "microsoft.bingweather"),
            (new[]{"камера","camera"}, "windowscamera"),
            (new[]{"xbox"}, "xboxapp"),
            (new[]{"блокнот","notepad"}, "notepad"),
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
            if (t.EndsWith(" — фотографии") || t.EndsWith(" - photos")
                || t.EndsWith(" — photos") || t.EndsWith(" - фотографии"))
                return "photos";
            if (t == "microsoft store" || t == "магазин microsoft" || t == "магазин")
                return "winstore";
            return null;
        }

        bool IsUwpAppName(string n)
        {
            switch (n?.ToLowerInvariant())
            {
                case "systemsettings":
                case "calculator":
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
            System.Threading.Tasks.Task.Run(() =>
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
                { BluetoothIcon.Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x9D, 0xFF)); BluetoothButton.ToolTip = "Bluetooth: включён"; }
                else if (state == 0)
                { BluetoothIcon.Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)); BluetoothButton.ToolTip = "Bluetooth: выключен"; }
                else
                { BluetoothIcon.Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0xAA, 0xAA, 0xAA)); BluetoothButton.ToolTip = "Bluetooth: адаптер не найден"; }
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
            _resumingFromSleep = true;
            _pidNameCache.Clear();
            _pidPathCache.Clear();
            _pendingNewGroups.Clear();

            var toRemove = _groups.Values.Where(g => !g.IsPinned).ToList();
            foreach (var g in toRemove)
            { _groups.Remove(g.ExeName); RemoveButtonAnimated(g.Button, g); }

            _resumeBlockTimer?.Stop();
            _resumeBlockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _resumeBlockTimer.Tick += (s, e) =>
            {
                _resumeBlockTimer.Stop();
                _resumingFromSleep = false;
                _pendingNewGroups.Clear();
                Debug.WriteLine("[MyTaskbar] Resume block lifted");
            };
            _resumeBlockTimer.Start();
        }

        // ═════════════════════════════════════════════════════════════════════
        // EDGE REVEAL / FULLSCREEN
        // ═════════════════════════════════════════════════════════════════════
        void StartEdgeRevealWatcher()
        {
            _edgeRevealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _edgeRevealTimer.Tick += (s, e) => SafeRun(CheckEdgeReveal, "CheckEdgeReveal");
            _edgeRevealTimer.Start();
        }

        void CheckEdgeReveal()
        {
            if (!_isHiddenByFullscreen || _taskbarAnimating)
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
            bool atEdge = cp.y <= 2 && cp.x >= taskbarLeft && cp.x <= taskbarRight;
            if (atEdge)
            {
                if (_edgeCursorEnteredAt == DateTime.MinValue) _edgeCursorEnteredAt = DateTime.UtcNow;
                if (!_edgeRevealPending)
                {
                    _edgeRevealPending = true;
                    _isHiddenByFullscreen = false;
                    _fullscreenConfirmCount = 0;
                    _notFullscreenConfirmCount = 0;
                    ShowTaskbar(animate: true);
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

        bool IsForegroundFullscreen()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow(); if (hwnd == IntPtr.Zero) return false;
                if (hwnd == GetShellWindow()) return false;
                if (DesktopWindowClasses.Contains(GetWindowClass(hwnd))) return false;
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

        void HideTaskbar(bool animate = false)
        {
            if (_taskbarAnimating) return;
            _wifiWindow?.Hide(); HidePreview();
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
            if (animate)
            {
                _taskbarAnimating = true;
                double from = Top, to = -(TASKBAR_HEIGHT + 4);
                var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ANIM_MS))
                { EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop };
                a.Completed += (s, e) => { BeginAnimation(TopProperty, null); Top = to; _taskbarAnimating = false; Visibility = Visibility.Hidden; };
                BeginAnimation(TopProperty, a);
            }
            else { Visibility = Visibility.Hidden; Top = -(TASKBAR_HEIGHT + 4); }
        }

        void ShowTaskbar(bool animate = false, Action onComplete = null)
        {
            if (_taskbarAnimating) return;
            Topmost = true;
            var src2 = PresentationSource.FromVisual(this);
            double dpi = src2?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            Left = Math.Round((SystemParameters.PrimaryScreenWidth / dpi - ActualWidth) / 2);
            SafeRun(ApplyAcrylicBackground, "ApplyAcrylicBackground_Show");
            if (animate)
            {
                _taskbarAnimating = true;
                double from = -(TASKBAR_HEIGHT + 4);
                Top = from; Visibility = Visibility.Visible;
                var a = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(ANIM_MS))
                { EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
                a.Completed += (s, e) => { BeginAnimation(TopProperty, null); Top = 0; _taskbarAnimating = false; onComplete?.Invoke(); };
                BeginAnimation(TopProperty, a);
            }
            else { Visibility = Visibility.Visible; Top = 0; onComplete?.Invoke(); }
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

                    // [STAB-2] Пропуск зависших окон — не блокируемся
                    try { if (IsHungAppWindow(hWnd)) return true; } catch { return true; }
                    // [STAB-11] Проверка валидности
                    if (!IsWindow(hWnd)) return true;

                    // [STAB-4] Безопасный GetWindowText
                    string title = SafeGetWindowText(hWnd);
                    if (string.IsNullOrWhiteSpace(title) || title == "Program Manager") return true;

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

                    if (exeName == "applicationframehost")
                    {
                        string u = ResolveUwpAppName(title);
                        if (!string.IsNullOrEmpty(u))
                        {
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

                    if (!fresh.ContainsKey(exeName)) fresh[exeName] = new List<IntPtr>(2);
                    fresh[exeName].Add(hWnd);

                    if (!_groups.ContainsKey(exeName))
                    {
                        // [FIX-9] Задержка подтверждения
                        if (!_pendingNewGroups.TryGetValue(exeName, out DateTime firstSeen))
                        {
                            _pendingNewGroups[exeName] = DateTime.UtcNow;
                            return true;
                        }
                        if ((DateTime.UtcNow - firstSeen).TotalMilliseconds < NEW_GROUP_CONFIRM_MS)
                            return true;

                        _pendingNewGroups.Remove(exeName);
                        _pidPathCache.TryGetValue(winPid, out string ep);
                        bool uwpIcon = IsUwpAppName(exeName);
                        // [STAB-7] Быстрый синхронный путь — только HICON
                        BitmapSource icon = uwpIcon
                            ? GetCachedUwpIcon(exeName)
                            : GetCachedIconSafe(hWnd, winPid, ep);
                        var g = new AppGroup { ExeName = exeName, Icon = icon, LastTitle = title, IsUwp = uwpIcon };
                        var b = CreateAppButton(title, icon, uwpIcon ? 22 : 24);
                        SetupGroupButton(b, g); g.Button = b; _groups[exeName] = g;
                        try { AddButtonAnimated(b); } catch { }
                        // [STAB-7] Shell-иконки догружаем асинхронно
                        if (icon == null) LoadIconAsync(g, hWnd, winPid, ep);
                    }
                    else
                    {
                        var g = _groups[exeName];
                        if (g.Icon == null)
                        {
                            _pidPathCache.TryGetValue(winPid, out string ep);
                            LoadIconAsync(g, hWnd, winPid, ep); // [STAB-7]
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

            bool needLayout = false;
            var toRemove = new List<string>(4);
            foreach (var kvp in _groups)
            {
                try
                {
                    var g = kvp.Value;
                    g.Hwnds = fresh.TryGetValue(kvp.Key, out var list2) ? list2 : new List<IntPtr>(0);
                    if (g.Hwnds.Count == 0 && !g.IsPinned) toRemove.Add(kvp.Key);
                    else UpdateGroupBadge(g);
                }
                catch { }
            }
            foreach (var exe in toRemove)
            {
                try
                {
                    if (_groups.TryGetValue(exe, out var g))
                    { _groups.Remove(exe); needLayout = true; RemoveButtonAnimated(g.Button, g); }
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
                System.Threading.Tasks.Task.Run(() =>
                {
                    int b = BrightnessHelper.GetBuiltInBrightness();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { if (BrightnessButton != null) BrightnessButton.ToolTip = b >= 0 ? $"Яркость: {b}%" : "Яркость"; } catch { }
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
                if (_brightnessFlyout == null)
                {
                    _brightnessFlyout = new BrightnessFlyoutWindow();
                    _brightnessFlyout.IsVisibleChanged += (s2, ev) => { if (!(bool)ev.NewValue) UpdateBrightnessIcon(); };
                }
                if (_brightnessFlyout.IsVisible) { _brightnessFlyout.Hide(); return; }
                var btn = BrightnessButton;
                var pos = btn.PointToScreen(new System.Windows.Point(0, 0));
                _brightnessFlyout.Left = pos.X + btn.ActualWidth / 2 - _brightnessFlyout.Width / 2;
                _brightnessFlyout.Top = TASKBAR_HEIGHT + 4;
                _brightnessFlyout.ShowAt(_brightnessFlyout.Left, _brightnessFlyout.Top);
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
                if (VolumeButton != null) VolumeButton.ToolTip = muted ? "Без звука" : $"Громкость: {vol}%";
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UpdateVolumeIcon: {ex.Message}"); }
        }

        void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MarkOwnActivity();
                if (_volumeFlyout == null)
                {
                    _volumeFlyout = new VolumeFlyoutWindow();
                    _volumeFlyout.IsVisibleChanged += (s2, ev) => { if (!(bool)ev.NewValue) UpdateVolumeIcon(); };
                }
                if (_volumeFlyout.IsVisible) { _volumeFlyout.Hide(); return; }
                var btn = VolumeButton;
                var pos = btn.PointToScreen(new System.Windows.Point(0, 0));
                _volumeFlyout.Left = pos.X + btn.ActualWidth / 2 - _volumeFlyout.Width / 2;
                _volumeFlyout.Top = TASKBAR_HEIGHT + 4;
                _volumeFlyout.Show();
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] VolumeButton_Click: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // MENU
        // ═════════════════════════════════════════════════════════════════════
        void EnsureMenuWindow()
        {
            if (_menuWindow != null) return;
            _menuWindow = new MenuWindow();
            _menuWindow.MenuVisibilityChanged += isOpen =>
                Dispatcher.BeginInvoke(new Action(() => SetStartButtonHighlight(isOpen)));
            _menuWindow.IsVisibleChanged += (s, e) =>
            {
                if (!(bool)e.NewValue) Dispatcher.BeginInvoke(new Action(() => SetStartButtonHighlight(false)));
            };
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
            _keyboardProc = KeyboardHookCallback;
            using (var p = Process.GetCurrentProcess())
            using (var m = p.MainModule)
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(m.ModuleName), 0);
            if (_keyboardHook == IntPtr.Zero) Debug.WriteLine("[MyTaskbar] Hook FAILED: " + Marshal.GetLastWin32Error());
            else Debug.WriteLine("[MyTaskbar] Hook installed OK");
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
                    if (!_winKeyDown) { _winKeyDown = true; _winUsedInCombo = false; if (_shiftDown || _ctrlDown || _altDown) InjectWinDownOnce(); }
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
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        void OnWinKeyDown()
        {
            try
            {
                MarkOwnActivity();
                TryCloseSystemStartMenu();
                EnsureMenuWindow();
                if (_menuWindow.JustHidden) return;
                if (_menuWindow.IsVisible) { _menuWindow.HideAnimated(); return; }
                if (_isHiddenByFullscreen)
                {
                    _isHiddenByFullscreen = false; _fullscreenConfirmCount = 0; _notFullscreenConfirmCount = 0;
                    ShowTaskbar(animate: true, onComplete: () => _menuWindow?.ShowMenu());
                }
                else { _menuWindow.ShowMenu(); }
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
        void StartTaskbarWatcher()
        {
            _taskbarWatcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _taskbarWatcher.Tick += (s, e) => { try { if (TaskbarHelper.IsSystemTaskbarVisible()) TaskbarHelper.Hide(); } catch { } };
            _taskbarWatcher.Start();
        }

        // ═════════════════════════════════════════════════════════════════════
        // WIFI
        // ═════════════════════════════════════════════════════════════════════
        void StartWifiIconTracker()
        {
            FetchWifiSignalAsync(); UpdateWifiIcon();
            _wifiIconTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
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
            System.Threading.Tasks.Task.Run(() =>
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
                        if (!p.WaitForExit(2500)) { try { p.Kill(); } catch { } }
                        foreach (var rawLine in output.Split('\n'))
                        {
                            string line = rawLine.Trim();
                            int colon = line.IndexOf(':'); if (colon < 1) continue;
                            string key = line.Substring(0, colon).Trim();
                            string val = line.Substring(colon + 1).Trim();
                            bool isSig = string.Equals(key, "Signal", StringComparison.OrdinalIgnoreCase) || key == "Сигнал";
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
                        return new NetworkStatus { Icon = SignalToIcon(s), Tooltip = $"Wi-Fi · сигнал {ss}", Active = true };
                    }
                    return new NetworkStatus { Icon = "\uF384", Tooltip = "Нет подключения к Интернету", Active = false };
                }
                return new NetworkStatus { Icon = "\uF384", Tooltip = "Нет сетевых подключений", Active = false };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MyTaskbar] GetNetworkStatus: {ex.Message}");
                return new NetworkStatus { Icon = "\uF384", Tooltip = "Сеть недоступна", Active = false };
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
                    _wifiWindow = new WifiWindow();
                    _wifiWindow.IsVisibleChanged += (s2, ev) => { if (!(bool)ev.NewValue) { _wifiClosedAt = DateTime.UtcNow; UpdateWifiIcon(); } };
                }
                if (_wifiWindow.IsVisible) { _wifiWindow.Hide(); return; }
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
                { var ts = TimeSpan.FromSeconds(status.BatteryLifeRemaining); tl = $" · ~{(int)ts.TotalHours}ч {ts.Minutes}мин"; }
                if (BatteryButton != null) BatteryButton.ToolTip = $"{pct}%{(chg ? " · заряжается" : "")}{tl}";
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UpdateBattery: {ex.Message}"); if (BatteryButton != null) BatteryButton.Visibility = Visibility.Collapsed; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // TRAY
        // ═════════════════════════════════════════════════════════════════════
        DateTime _trayClosedAt = DateTime.MinValue;

        void InitTrayWindow()
        {
            if (_trayWindow != null) return;
            _trayWindow = new TrayWindow();
            _trayWindow.OnClosed = () =>
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
                _trayWindow.SlideDown(pos.X + btn.ActualWidth / 2, TASKBAR_HEIGHT + 4);
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
            if (_groups.TryGetValue(exeName, out var ex2)) { if (ex2.Icon == null && icon != null) ex2.Icon = icon; return ex2; }
            bool uwpIcon = IsUwpAppName(exeName);
            var group = new AppGroup { ExeName = exeName, Icon = icon, LastTitle = tooltip ?? exeName, IsUwp = uwpIcon };
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
                try { _previewHideTimer?.Stop(); _pendingPreviewGroup = group; _pendingPreviewBtn = btn; _previewShowTimer?.Stop(); _previewShowTimer?.Start(); } catch { }
            };
            btn.MouseLeave += (s, e) =>
            {
                try { _previewShowTimer?.Stop(); _pendingPreviewGroup = null; _pendingPreviewBtn = null; ScheduleHidePreview(); } catch { }
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
                        if (IsIconic(hwnd)) { ShowWindow(hwnd, SW_RESTORE); SetForegroundWindow(hwnd); if (uwp) ActivateUwpWindow(hwnd); }
                        else if (_lastForegroundWindow == hwnd) { ShowWindow(hwnd, SW_MINIMIZE); }
                        else { ShowWindow(hwnd, SW_RESTORE); SetForegroundWindow(hwnd); if (uwp) ActivateUwpWindow(hwnd); }
                    }
                    else
                    {
                        IntPtr active = GetForegroundWindow();
                        int idx = group.Hwnds.IndexOf(active);
                        IntPtr next = group.Hwnds[(idx + 1) % group.Hwnds.Count];
                        ShowWindow(next, SW_RESTORE); SetForegroundWindow(next);
                        if (IsUwpAppName(group.ExeName)) ActivateUwpWindow(next);
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] btn.Click: {ex.Message}"); }
            };
            btn.MouseRightButtonUp += (s, e) => { try { e.Handled = true; ShowContextMenu(btn, group); } catch { } };
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
            var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            delay.Tick += (s, e) => { delay.Stop(); _activeTimer.Start(); };
            delay.Start();
        }

        void UpdateGroupBadge(AppGroup g)
        {
            if (g?.Button == null || g.Hwnds.Count == 0) return;
            try
            {
                string dn = SafeGetWindowText(g.Hwnds[0]); // [STAB-4]
                if (string.IsNullOrEmpty(dn)) dn = g.LastTitle ?? g.ExeName;
                string title = g.Hwnds.Count == 1 ? dn : $"{dn} ({g.Hwnds.Count} окна)";
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
                    bool isActive = running && string.Equals(kvp.Key, activeExe, StringComparison.OrdinalIgnoreCase);
                    string state = isActive ? "Active" : running ? "Running" : "";
                    if (_buttonStateCache.TryGetValue(g.Button, out string old) && old == state) continue;
                    _buttonStateCache[g.Button] = state; ApplyButtonState(g.Button, state);
                }
                catch { }
            }
        }

        Button CreateAppButton(string tooltip, BitmapSource icon, int iconSize = 24)
        {
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
        void ShowContextMenu(Button btn, AppGroup group)
        {
            try
            {
                HidePreview();
                var menu = new ContextMenu { Style = TryFindResource("DarkContextMenu") as Style };
                bool hw = group.Hwnds.Count > 0;
                if (hw)
                {
                    if (group.Hwnds.Count > 1)
                    {
                        foreach (var hwnd in group.Hwnds)
                        {
                            IntPtr cap = hwnd;
                            // [STAB-4] SafeGetWindowText
                            string t = SafeGetWindowText(hwnd);
                            if (string.IsNullOrEmpty(t)) t = group.ExeName;
                            var mi = new MenuItem { Header = t.Length > 40 ? t.Substring(0, 40) + "…" : t, Style = TryFindResource("DarkMenuItem") as Style };
                            mi.Click += (s, e) =>
                            {
                                try
                                {
                                    // [STAB-11] IsWindow-проверка
                                    if (!IsWindow(cap)) return;
                                    if (IsIconic(cap)) ShowWindow(cap, SW_RESTORE); SetForegroundWindow(cap);
                                }
                                catch { }
                            };
                            menu.Items.Add(mi);
                        }
                        menu.Items.Add(new Separator { Style = TryFindResource("DarkMenuSeparator") as Style });
                    }
                    IntPtr mh = group.Hwnds[0]; bool z = IsWindow(mh) && IsZoomed(mh);
                    var mt = new MenuItem { Header = z ? "Восстановить" : "Развернуть", Style = TryFindResource("DarkMenuItem") as Style };
                    mt.Click += (s, e) => { try { if (IsWindow(mh)) { ShowWindow(mh, z ? SW_RESTORE : SW_MAXIMIZE); SetForegroundWindow(mh); } } catch { } };
                    menu.Items.Add(mt);
                    var mm = new MenuItem { Header = "Свернуть все", Style = TryFindResource("DarkMenuItem") as Style };
                    mm.Click += (s, e) => { foreach (var h in group.Hwnds) try { if (IsWindow(h)) ShowWindow(h, SW_MINIMIZE); } catch { } };
                    menu.Items.Add(mm);
                    menu.Items.Add(new Separator { Style = TryFindResource("DarkMenuSeparator") as Style });
                    var mc = new MenuItem { Header = "Закрыть все окна", Style = TryFindResource("DarkMenuItem") as Style };
                    mc.Click += (s, e) =>
                    {
                        foreach (var h in group.Hwnds.ToList())
                        {
                            try
                            {
                                // [STAB-13] IsWindow перед PostMessage
                                if (IsWindow(h)) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            }
                            catch { }
                        }
                    };
                    menu.Items.Add(mc);
                }
                if (group.IsPinned)
                {
                    if (menu.Items.Count > 0) menu.Items.Add(new Separator { Style = TryFindResource("DarkMenuSeparator") as Style });
                    var mi = new MenuItem { Header = "Открепить от панели", Style = TryFindResource("DarkMenuItem") as Style };
                    mi.Click += (s, e) => { try { UnpinGroup(group); } catch { } };
                    menu.Items.Add(mi);
                }
                else if (!string.IsNullOrEmpty(group.LaunchPath) || hw)
                {
                    if (menu.Items.Count > 0) menu.Items.Add(new Separator { Style = TryFindResource("DarkMenuSeparator") as Style });
                    var mi = new MenuItem { Header = "Закрепить на панели", Style = TryFindResource("DarkMenuItem") as Style };
                    mi.Click += (s, e) => { try { PinGroup(group); } catch { } };
                    menu.Items.Add(mi);
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
                if (string.IsNullOrEmpty(exePath)) { Debug.WriteLine($"[MyTaskbar] PinGroup: путь не найден для '{group.ExeName}'"); return; }
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
                if (group.Hwnds.Count == 0) { _groups.Remove(group.ExeName); RemoveButtonAnimated(group.Button, group); }
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
        }

        (FrameworkElement card, Border thumbSlot) CreatePreviewCard(IntPtr hwnd, AppGroup group)
        {
            // [STAB-4] SafeGetWindowText
            string title = SafeGetWindowText(hwnd); if (string.IsNullOrEmpty(title)) title = group?.ExeName ?? "—";
            IntPtr cap = hwnd;
            var nb = Frozen(Color.FromArgb(220, 32, 32, 32)); var hb = Frozen(Color.FromArgb(255, 50, 50, 55));
            var nb2 = Frozen(Color.FromArgb(60, 255, 255, 255)); var hb2 = Frozen(Color.FromArgb(120, 255, 255, 255));
            var card = new Border { Width = PREVIEW_CARD_WIDTH, Background = nb, BorderBrush = nb2, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(0), Margin = new Thickness(PREVIEW_GAP, 0, PREVIEW_GAP, 0), Cursor = Cursors.Hand, SnapsToDevicePixels = true, UseLayoutRounding = true };
            card.MouseEnter += (s, e) => { _previewHideTimer?.Stop(); card.Background = hb; card.BorderBrush = hb2; };
            card.MouseLeave += (s, e) => { card.Background = nb; card.BorderBrush = nb2; ScheduleHidePreview(); };
            card.MouseLeftButtonUp += (s, e) =>
            {
                try
                {
                    if (e.Source is Button) return; HidePreview(); _menuWindow?.HideAnimated();
                    // [STAB-11]
                    if (!IsWindow(cap)) return;
                    if (IsIconic(cap)) ShowWindow(cap, SW_RESTORE); SetForegroundWindow(cap);
                }
                catch { }
            };
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PREVIEW_TITLE_HEIGHT) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PREVIEW_THUMB_HEIGHT) });
            var tr = new Grid { Margin = new Thickness(6, 0, 4, 0) };
            tr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            tr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            Grid.SetRow(tr, 0);
            if (group?.Icon != null) { var ii = new System.Windows.Controls.Image { Source = group.Icon, Width = 14, Height = 14, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, SnapsToDevicePixels = true }; RenderOptions.SetBitmapScalingMode(ii, BitmapScalingMode.HighQuality); Grid.SetColumn(ii, 0); tr.Children.Add(ii); }
            string st = title.Length > 24 ? title.Substring(0, 24) + "…" : title;
            var tt = new TextBlock { Text = st, Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), FontSize = 10, FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(5, 0, 2, 0) };
            Grid.SetColumn(tt, 1); tr.Children.Add(tt);
            var cb = new Button { Content = new TextBlock { Text = "✕", FontSize = 8, Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, Width = 18, Height = 18, Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Cursor = Cursors.Hand, Focusable = false };
            cb.MouseEnter += (s, e) => cb.Background = BrushCloseHover; cb.MouseLeave += (s, e) => cb.Background = Brushes.Transparent;
            cb.Click += (s, e) =>
            {
                try
                {
                    e.Handled = true;
                    // [STAB-13] IsWindow перед PostMessage
                    if (IsWindow(cap)) PostMessage(cap, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
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
                _previewWindow.Left = left; _previewWindow.Top = TASKBAR_HEIGHT + 6; _previewWindow.Visibility = Visibility.Visible; _previewWindow.UpdateLayout();
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
            try { UnregisterAllThumbnails(); if (_previewWindow != null) _previewWindow.Visibility = Visibility.Hidden; _currentPreviewGroup = null; }
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
                try { _flashState = !_flashState; foreach (var b in _flashingButtons.ToList()) HighlightButton(b, _flashState); }
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
                if (_flashingButtons.Contains(btn) && state == "Active") StopFlash(_groups.Values.FirstOrDefault(g => g.Button == btn));
                btn.ApplyTemplate();
                var rb = btn.Template?.FindName("RunBar", btn) as Rectangle;
                var ab = btn.Template?.FindName("AppBorder", btn) as Border;
                if (rb == null || ab == null) return;
                switch (state)
                {
                    case "Active": rb.Visibility = Visibility.Visible; rb.Fill = BrushActiveBar; ab.Background = BrushActiveBg; break;
                    case "Running": rb.Visibility = Visibility.Visible; rb.Fill = BrushRunningBar; ab.Background = BrushTransparent; break;
                    default: rb.Visibility = Visibility.Collapsed; ab.Background = BrushTransparent; break;
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
                double screenW = SystemParameters.PrimaryScreenWidth / dpi;
                Left = Math.Round((screenW - ActualWidth) / 2);
                if (!_taskbarAnimating) Top = 0;
                Height = TASKBAR_HEIGHT;
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

        void ReserveScreenSpace()
        {
            try
            {
                double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
                RECT wa = new RECT { left = 0, top = TASKBAR_HEIGHT, right = (int)sw, bottom = (int)sh };
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

        void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MarkOwnActivity(); EnsureMenuWindow();
                if (_menuWindow.JustHidden) return;
                if (_menuWindow.IsVisible) { _menuWindow.HideAnimated(); return; }
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
            if (_keyboardHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_keyboardHook); } catch { }
                _keyboardHook = IntPtr.Zero;
            }

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