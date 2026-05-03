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
// MainWindow.Fields.cs — Constants, static fields, AppGroup class
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
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
        static readonly SolidColorBrush BrushFlashPip = Frozen(Color.FromRgb(255, 140, 0));    // ярко-оранжевый RunBar
        // [ATTENTION] Фон кнопки при мигании (on-фаза) — чуть ярче чтобы была видна пульсация
        static readonly SolidColorBrush BrushFlashOn = Frozen(Color.FromArgb(120, 255, 140, 0));  // оранжевый 47% on
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
        uint _wmShellHook = 0;

        IntPtr _attentionEventHook = IntPtr.Zero;

        // ── Drag-reorder иконок (Win10-style: зажал 300мс → тащишь) ─────────
        Button   _dragBtn;          // кнопка под курсором при нажатии
        System.Windows.Point _dragOrigin;   // позиция нажатия в координатах AppIcons
        bool     _dragActive;       // флаг: drag идёт прямо сейчас
        System.Windows.Threading.DispatcherTimer _dragHoldTimer; // 300мс таймер

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
        const int WM_DPICHANGED = 0x02E0;  // [DPI-AUTO] Windows посылает при смене DPI/масштаба
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
            public bool NeedsAttention;      // true = окно запросило внимание (FlashWindow)
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
    }
}
