using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MyTaskbar
{
    public partial class TrayWindow : Window
    {
        // ── P/Invoke ──────────────────────────────────────────────────
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr FindWindow(string cls, string wnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string cls, string wnd);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern IntPtr CopyIcon(IntPtr hIcon);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        [DllImport("kernel32.dll")]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")]
        static extern bool ReadProcessMemory(IntPtr proc, IntPtr baseAddr, IntPtr buffer, int size, out int read);
        [DllImport("kernel32.dll")]
        static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, int size, uint allocType, uint protect);
        [DllImport("kernel32.dll")]
        static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, int size, uint freeType);

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool RegisterShellHookWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern bool DeregisterShellHookWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern uint RegisterWindowMessage(string msg);


        static uint WM_SHELLHOOK;
        const int HSHELL_REDRAW           = 6;   // иконка трея изменилась/добавилась
        const int HSHELL_TASKMAN          = 4;
        const int HSHELL_WINDOWCREATED    = 1;
        const int HSHELL_WINDOWDESTROYED  = 2;

        // ── Константы ────────────────────────────────────────────────
        static readonly IntPtr HWND_TOP     = new IntPtr(0);
        const uint SWP_NOMOVE     = 0x0002;
        const uint SWP_NOSIZE     = 0x0001;
        const uint SWP_NOACTIVATE = 0x0010;

        const uint TB_BUTTONCOUNT = 0x0418;
        const uint TB_GETBUTTON   = 0x0417;
        const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
        const uint MEM_COMMIT   = 0x1000;
        const uint MEM_RELEASE  = 0x8000;
        const uint PAGE_READWRITE = 0x04;

        const uint WM_LBUTTONDOWN   = 0x0201;
        const uint WM_LBUTTONUP     = 0x0202;
        const uint WM_LBUTTONDBLCLK = 0x0203;
        const uint WM_RBUTTONDOWN   = 0x0204;
        const uint WM_RBUTTONUP     = 0x0205;
        const uint WM_CONTEXTMENU   = 0x007B;
        const uint NIN_SELECT       = 0x0400;

        const int SW_RESTORE    = 9;
        const byte TBSTATE_HIDDEN = 0x08;

        const int ICON_SIZE   = 24;
        const int ICON_MARGIN = 4;
        const int ICON_CELL   = ICON_SIZE + ICON_MARGIN;
        const int PANEL_PAD   = 8;

        static readonly HashSet<string> _hardSysProcs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "dwm","fontdrvhost","audiodg","lsass","smss","csrss",
                "wininit","services","spoolsv","conhost","wermgr","werfault",
                "searchindexer","msiexec"
            };

        // ── Структуры ────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct TBBUTTON64
        {
            public int iBitmap, idCommand;
            public byte fsState, fsStyle;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] bReserved;
            public ulong dwData;
            public long iString;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct TBBUTTON32
        {
            public int iBitmap, idCommand;
            public byte fsState, fsStyle, r0, r1;
            public uint dwData;
            public int iString;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TRAYDATA
        {
            public IntPtr hwnd;
            public uint uID;
            public uint uCallbackMessage;
            public int Reserved0, Reserved1;
            public IntPtr hIcon;
            public uint uVersion; // NOTIFYICON_VERSION: 0/3=legacy, 4=modern
        }

        // Версии протокола Shell_NotifyIcon
        // NOTIFYICON_VERSION_4 = 4 (Win Vista+, wParam=coords, lParam=MAKELPARAM(msg,id))
        // NOTIFYICON_VERSION   = 3 (legacy,      wParam=id,     lParam=msg)
        // 0 = неизвестно, шлём оба
        const uint NOTIFYICONVERSION_4    = 4;
        const uint NOTIFYICONVERSION_3    = 3;

        class TrayIcon
        {
            public IntPtr AppHwnd;
            public uint   AppID;
            public uint   CallbackMsg;
            public string ProcessName;
            public int    ProcessId;
            public BitmapSource IconImage;
            public uint   NotifyVersion; // 0=unknown, 3=legacy, 4=modern

            public bool IsTelegram => ProcessName != null &&
                ProcessName.IndexOf("telegram", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ── Поля состояния ───────────────────────────────────────────
        static readonly int _ownPid = Process.GetCurrentProcess().Id;

        bool _isOpen         = false;
        bool _animating      = false;
        bool _blockDeactivate = false;

        public bool IsOpen   => _isOpen;
        public bool IsBottom { get; set; } = false;

        double _uiScale = 1.0;
        public double UIScale
        {
            get => _uiScale;
            set
            {
                _uiScale = value;
                if (Content is FrameworkElement root)
                    root.LayoutTransform = Math.Abs(value - 1.0) < 0.01
                        ? Transform.Identity
                        : new ScaleTransform(value, value);
            }
        }

        // Фоновый поток опроса трея
        private CancellationTokenSource _trayCts;
        private Task _trayPollingTask;


        // Shell-hook debounce: при изменении трея не делаем refresh чаще раза в 150мс

        // ── Скрытые иконки ───────────────────────────────────────────
        // Ключ: "ProcessName" (нижний регистр) — пользователь может скрыть любую иконку
        private readonly HashSet<string> _hiddenIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly string _hiddenIconsPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MyTaskbar", "tray_hidden.txt");

        void LoadHiddenIcons()
        {
            try
            {
                if (!System.IO.File.Exists(_hiddenIconsPath)) return;
                foreach (var line in System.IO.File.ReadAllLines(_hiddenIconsPath))
                {
                    var s = line.Trim();
                    if (s.Length > 0) _hiddenIcons.Add(s);
                }
            }
            catch { }
        }

        void SaveHiddenIcons()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_hiddenIconsPath);
                if (!System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllLines(_hiddenIconsPath, _hiddenIcons);
            }
            catch { }
        }

        public void HideIcon(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return;
            _hiddenIcons.Add(processName.ToLowerInvariant());
            SaveHiddenIcons();
            RefreshIconsAlways();
        }

        public void UnhideAllIcons()
        {
            _hiddenIcons.Clear();
            SaveHiddenIcons();
            RefreshIconsAlways();
        }

        double _pendingCenterX;
        double _pendingVisibleTop;
        // [FIX-SECONDARY-POS] Границы монитора для корректного ограничения позиции.
        // MainWindow устанавливает PrimaryScreenWidth по умолчанию; вторая панель передаёт свои.
        public double MonLeft  { get; set; } = 0;
        public double MonRight { get; set; } = 0; // 0 = не задано → использовать PrimaryScreenWidth

        // ── Конструктор ──────────────────────────────────────────────
        public TrayWindow()
        {
            InitializeComponent();

            SourceInitialized += (s, e) =>
            {
                ApplyAcrylic();
                Top     = -2000;
                Opacity = 0;

                // Регистрируем shell-hook: Windows будет слать нам WM_SHELLHOOK
                // каждый раз когда что-то меняется в трее — мгновенно, без polling
                var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                src?.AddHook(WndProc);

                WM_SHELLHOOK = RegisterWindowMessage("SHELLHOOK");
                RegisterShellHookWindow(new WindowInteropHelper(this).Handle);
            };

            // Запускаем фоновый поток опроса трея — НЕ блокирует UI
            LoadHiddenIcons();
            StartTrayPolling();

            // ── Правый клик на пустой области трея ──────────────────
            // Кнопки иконок помечают e.Handled = true, поэтому сюда
            // событие доходит только когда кликнули мимо иконок.
            MouseRightButtonUp += (s, e) =>
            {
                if (e.Handled) return;
                e.Handled = true;
                _blockDeactivate = true;
                ShowEmptyAreaContextMenu();
            };
        }

        // ── Акрил ────────────────────────────────────────────────────
        void ApplyAcrylic()
        {
            try   { MyTaskbar.Helpers.AcrylicHelper.EnableAcrylic(this, 0x701A1A2E); }
            catch
            {
                try { MyTaskbar.Helpers.AcrylicHelper.EnableBlur(this, 0x70202030); }
                catch
                {
                    if (RootBorder != null)
                        RootBorder.Background = new SolidColorBrush(
                            Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1C));
                }
            }
        }

        // ── Z-order ──────────────────────────────────────────────────
        void EnsureOnTop()
        {
            var helper = new WindowInteropHelper(this);
            SetWindowPos(helper.Handle, HWND_TOP,
                0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // ── Telegram ─────────────────────────────────────────────────
        IntPtr FindTelegramMainWindow(int pid)
        {
            IntPtr best = IntPtr.Zero, fallback = IntPtr.Zero;
            EnumWindows((hwnd, lp) =>
            {
                GetWindowThreadProcessId(hwnd, out uint wpid);
                if ((int)wpid != pid) return true;
                var sb = new System.Text.StringBuilder(128);
                GetClassName(hwnd, sb, 128);
                string cls = sb.ToString();
                if (cls == "TelegramDesktopClass") { best = hwnd; return false; }
                if (cls.StartsWith("Qt", StringComparison.OrdinalIgnoreCase) ||
                    cls.IndexOf("Telegram", StringComparison.OrdinalIgnoreCase) >= 0)
                    fallback = hwnd;
                return true;
            }, IntPtr.Zero);
            return best != IntPtr.Zero ? best : fallback;
        }

        void ActivateTelegram(TrayIcon icon)
        {
            IntPtr mainHwnd = FindTelegramMainWindow(icon.ProcessId);
            IntPtr fakeHwnd = IntPtr.Zero;

            EnumWindows((hwnd, lp) =>
            {
                GetWindowThreadProcessId(hwnd, out uint wpid);
                if ((int)wpid != icon.ProcessId) return true;
                var sb = new System.Text.StringBuilder(128);
                GetClassName(hwnd, sb, 128);
                string cls = sb.ToString();
                if (cls == "TelegramDesktopFakeWindow" ||
                    cls.IndexOf("FakeWindow", StringComparison.OrdinalIgnoreCase) >= 0)
                { fakeHwnd = hwnd; return false; }
                return true;
            }, IntPtr.Zero);

            IntPtr callbackTarget = fakeHwnd != IntPtr.Zero ? fakeHwnd :
                                    IsWindow(icon.AppHwnd) ? icon.AppHwnd :
                                    mainHwnd;

            if (callbackTarget == IntPtr.Zero && mainHwnd == IntPtr.Zero) return;

            if (callbackTarget != IntPtr.Zero && icon.CallbackMsg != 0)
                PostMessage(callbackTarget, icon.CallbackMsg,
                    (IntPtr)icon.AppID, (IntPtr)NIN_SELECT);

            if (mainHwnd != IntPtr.Zero)
            {
                // [FIX] Было 200мс — делало Telegram «медленным». Достаточно 50мс,
                // чтобы NIN_SELECT успел обработаться раньше SetForegroundWindow.
                _ = Task.Delay(50).ContinueWith(_ => Dispatcher.Invoke(() =>
                {
                    GetWindowThreadProcessId(GetForegroundWindow(), out uint fgPid);
                    if ((int)fgPid != icon.ProcessId)
                    {
                        ShowWindow(mainHwnd, SW_RESTORE);
                        SetForegroundWindow(mainHwnd);
                    }
                }));
            }
        }

        // ── Shell hook + фоновый опрос трея ─────────────────────────
        //
        // Стратегия двух уровней:
        // ── WndProc: WM_SHELLHOOK ────────────────────────────────
        // RegisterShellHookWindow даёт нам HSHELL_REDRAW при смене видимых иконок.
        // Дополнительный источник — ToolbarSubclass ниже.
        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (WM_SHELLHOOK != 0 && (uint)msg == WM_SHELLHOOK)
                TriggerIconRefresh();
            return IntPtr.Zero;
        }

        // ── Subclass ToolbarWindow32 трея ────────────────────────────
        // Это самый надёжный способ: мы подменяем WndProc самого тулбара трея
        // и ловим WM_PAINT / TB_BUTTONCOUNT — они приходят при каждом добавлении
        // или удалении иконки. Работает для overflow и основного трея.
        //
        // Subclass через SetWindowLongPtr / CallWindowProc — стандартная техника Win32.
        [DllImport("user32.dll")]
        static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr newLong);
        [DllImport("user32.dll")]
        static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hwnd,
            uint msg, IntPtr wParam, IntPtr lParam);

        const int GWLP_WNDPROC = -4;
        const uint WM_PAINT    = 0x000F;

        delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        WndProcDelegate _tbSubclassDelegate;   // не давать GC собрать
        IntPtr _tbOrigWndProc   = IntPtr.Zero;
        IntPtr _tbSubclassedHwnd = IntPtr.Zero;
        WndProcDelegate _tbOvSubclassDelegate;
        IntPtr _tbOvOrigWndProc   = IntPtr.Zero;
        IntPtr _tbOvSubclassedHwnd = IntPtr.Zero;

        void SubclassTrayToolbars()
        {
            UnsubclassTrayToolbars();

            // Основной toolbar
            IntPtr tb = GetMainTrayToolbar();
            if (tb != IntPtr.Zero)
            {
                _tbSubclassDelegate = (hwnd, msg, wp, lp) =>
                {
                    if (msg == WM_PAINT || msg == TB_BUTTONCOUNT)
                        TriggerIconRefresh();
                    return CallWindowProc(_tbOrigWndProc, hwnd, msg, wp, lp);
                };
                _tbOrigWndProc      = SetWindowLongPtr(tb, GWLP_WNDPROC,
                    Marshal.GetFunctionPointerForDelegate(_tbSubclassDelegate));
                _tbSubclassedHwnd   = tb;
            }

            // Overflow toolbar (скрытые иконки системы)
            IntPtr overflow = FindWindow("NotifyIconOverflowWindow", null);
            if (overflow != IntPtr.Zero)
            {
                IntPtr tbOv = FindWindowEx(overflow, IntPtr.Zero, "ToolbarWindow32", null);
                if (tbOv != IntPtr.Zero)
                {
                    _tbOvSubclassDelegate = (hwnd, msg, wp, lp) =>
                    {
                        if (msg == WM_PAINT || msg == TB_BUTTONCOUNT)
                            TriggerIconRefresh();
                        return CallWindowProc(_tbOvOrigWndProc, hwnd, msg, wp, lp);
                    };
                    _tbOvOrigWndProc    = SetWindowLongPtr(tbOv, GWLP_WNDPROC,
                        Marshal.GetFunctionPointerForDelegate(_tbOvSubclassDelegate));
                    _tbOvSubclassedHwnd = tbOv;
                }
            }
        }

        void UnsubclassTrayToolbars()
        {
            if (_tbSubclassedHwnd != IntPtr.Zero && _tbOrigWndProc != IntPtr.Zero)
            {
                try { SetWindowLongPtr(_tbSubclassedHwnd, GWLP_WNDPROC, _tbOrigWndProc); } catch { }
                _tbSubclassedHwnd = IntPtr.Zero;
                _tbOrigWndProc    = IntPtr.Zero;
            }
            if (_tbOvSubclassedHwnd != IntPtr.Zero && _tbOvOrigWndProc != IntPtr.Zero)
            {
                try { SetWindowLongPtr(_tbOvSubclassedHwnd, GWLP_WNDPROC, _tbOvOrigWndProc); } catch { }
                _tbOvSubclassedHwnd = IntPtr.Zero;
                _tbOvOrigWndProc    = IntPtr.Zero;
            }
        }

        // ── Умный diff: RenderIcons только при реальном изменении ────
        // Сравниваем fingerprint списка иконок — если не изменился, не перерисовываем.
        private string _lastIconsFingerprint = "";

        string IconsFingerprint(List<TrayIcon> icons) =>
            string.Join("|", icons.Select(x => $"{x.ProcessId}:{x.AppID}"));

        // ── Единственная точка входа для обновления ──────────────────
        // Вызывается из любого потока (WM_PAINT тулбара, WM_SHELLHOOK, 1с-poll).
        // [FIX-TRAY-DEBOUNCE] Раньше каждый вызов сразу плодил Task.Run — а WM_PAINT
        // тулбара стреляет очень часто (не только при реальном добавлении/удалении
        // иконки). Несколько триггеров подряд запускали параллельные GetTrayIcons()
        // (каждый — OpenProcess + ReadProcessMemory в цикле), которые копились в
        // пуле потоков и выполнялись вперемешку — отсюда нарастающая задержка после
        // нескольких срабатываний вместо мгновенного отклика. Теперь: пачка триггеров
        // за 150мс схлопывается в один запуск, и повторный запуск не стартует, пока
        // предыдущий не закончился (а если за это время прилетел новый триггер —
        // выполняется ровно один дозапуск после, без накопления очереди).
        readonly object _refreshLock = new object();
        System.Threading.Timer _refreshDebounceTimer;
        bool _refreshRunning;
        bool _refreshRerunRequested;
        const int RefreshDebounceMs = 150;

        void TriggerIconRefresh()
        {
            lock (_refreshLock)
            {
                _refreshDebounceTimer?.Dispose();
                _refreshDebounceTimer = new System.Threading.Timer(
                    _ => RunIconRefresh(), null, RefreshDebounceMs, System.Threading.Timeout.Infinite);
            }
        }

        void RunIconRefresh()
        {
            lock (_refreshLock)
            {
                if (_refreshRunning) { _refreshRerunRequested = true; return; }
                _refreshRunning = true;
            }

            try
            {
                var icons = GetTrayIcons();
                var fp    = IconsFingerprint(icons);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (fp == _lastIconsFingerprint) return; // ничего не изменилось
                    _lastIconsFingerprint = fp;
                    RenderIcons(icons);
                }));
            }
            catch { }
            finally
            {
                bool rerun;
                lock (_refreshLock)
                {
                    _refreshRunning = false;
                    rerun = _refreshRerunRequested;
                    _refreshRerunRequested = false;
                }
                // Что-то изменилось, пока мы читали трей — перечитываем один раз,
                // без нового debounce-ожидания.
                if (rerun) _ = Task.Run(RunIconRefresh);
            }
        }


        void StartTrayPolling()
        {
            _trayCts?.Cancel();
            _trayCts = new CancellationTokenSource();
            var token = _trayCts.Token;

            // Subclass тулбаров — мгновенная реакция на WM_PAINT/TB_BUTTONCOUNT
            SubclassTrayToolbars();

            // Страховочный poll каждые 1с — на случай если subclass пропустит
            // (например Explorer перезапустился, overflow ещё не существует и т.д.)
            _trayPollingTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    TriggerIconRefresh();
                    try { await Task.Delay(1000, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }, token);
        }

        void StopTrayPolling()
        {
            // Восстанавливаем оригинальные WndProc у тулбаров
            UnsubclassTrayToolbars();

            // Снимаем shell hook
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    DeregisterShellHookWindow(hwnd);
            }
            catch { }

            lock (_refreshLock)
            {
                _refreshDebounceTimer?.Dispose();
                _refreshDebounceTimer = null;
            }

            _trayCts?.Cancel();
            _trayCts = null;
        }

        void RefreshIconsAlways() => TriggerIconRefresh();

        // ── SlideDown / SlideUp ──────────────────────────────────────
        public void SlideDown(double centerX, double visibleTop)
        {
            if (_animating || _isOpen) return;

            _pendingCenterX    = centerX;
            _pendingVisibleTop = visibleTop;

            // Прячем окно пока не позиционируем
            Opacity = 0;
            Top  = -9999;
            Left = -9999;

            // [FIX-TRAY-POS] Сначала получаем иконки и рендерим их синхронно
            // на UI-потоке (через Task.Run + BeginInvoke), затем в колбэке RenderIcons
            // выполняем позиционирование — когда WPF уже знает реальный размер окна.
            _ = Task.Run(() =>
            {
                try
                {
                    var icons = GetTrayIcons();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            RenderIcons(icons);
                            // После RenderIcons запускаем layout + позиционирование
                            // на следующем Render-тике, когда WPF пересчитает размеры
                            Dispatcher.BeginInvoke(new Action(SlideDown_AfterMeasure),
                                DispatcherPriority.Render);
                        }
                        catch { }
                    }));
                }
                catch { }
            });
        }

        void SlideDown_AfterMeasure()
        {
            InvalidateMeasure();
            InvalidateArrange();
            UpdateLayout();

            // [FIX-SECONDARY-POS] Используем границы нужного монитора, а не всегда первого экрана
            double monLeft  = MonLeft;
            double monRight = MonRight > 0 ? MonRight : SystemParameters.PrimaryScreenWidth;
            double w  = ActualWidth  > 4 ? ActualWidth  : 80;
            double h  = ActualHeight > 4 ? ActualHeight : 80;

            double left = _pendingCenterX - w / 2;
            if (left + w > monRight - 4) left = monRight - w - 4;
            if (left < monLeft + 4)      left = monLeft + 4;

            Left = left;
            Top  = IsBottom
                ? _pendingVisibleTop - h
                : _pendingVisibleTop;

            Opacity    = 1;
            _isOpen    = true;
            _animating = false;

            Activate();
            EnsureOnTop();

            // Refresh при открытии — poll сам обновит, но дадим ему пинок
            _ = Task.Delay(100).ContinueWith(_ => TriggerIconRefresh());
        }

        public void SlideUp(Action onComplete = null)
        {
            if (!_isOpen) { onComplete?.Invoke(); return; }
            _isOpen    = false;
            _animating = false;
            Top     = -2000;
            Opacity = 0;
            onComplete?.Invoke();
        }

        // ── Чтение иконок трея ───────────────────────────────────────
        List<TrayIcon> GetTrayIcons()
        {
            var result = new List<TrayIcon>();

            IntPtr overflow = FindWindow("NotifyIconOverflowWindow", null);
            if (overflow != IntPtr.Zero)
            {
                IntPtr tb = FindWindowEx(overflow, IntPtr.Zero, "ToolbarWindow32", null);
                if (tb != IntPtr.Zero)
                    ReadToolbar(tb, result, filterHidden: false);
            }

            IntPtr mainTb = GetMainTrayToolbar();
            if (mainTb != IntPtr.Zero)
                ReadToolbar(mainTb, result, filterHidden: false);

            // Убираем иконки которые пользователь скрыл
            result.RemoveAll(x => _hiddenIcons.Contains(x.ProcessName));

            // Дедупликация по процессу: если одно приложение зарегистрировало
            // несколько иконок (например Steam: основная + друзья + загрузки),
            // показываем только первую (основную). Клик/скрытие по ProcessName
            // всё равно затрагивает весь процесс.
            var seen = new HashSet<int>();
            result = result.Where(x => seen.Add(x.ProcessId)).ToList();

            return result;
        }

        IntPtr GetMainTrayToolbar()
        {
            IntPtr tray   = FindWindow("Shell_TrayWnd", null);
            IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            IntPtr pager  = FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
            if (pager != IntPtr.Zero)
            {
                IntPtr tb = FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", null);
                if (tb != IntPtr.Zero) return tb;
            }
            return FindWindowEx(notify, IntPtr.Zero, "ToolbarWindow32", null);
        }

        void ReadToolbar(IntPtr toolbar, List<TrayIcon> result, bool filterHidden)
        {
            GetWindowThreadProcessId(toolbar, out uint explorerPid);
            IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, explorerPid);
            if (hProc == IntPtr.Zero) return;

            try
            {
                int count = (int)(uint)SendMessage(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
                if (count <= 0 || count > 256) return; // sanity check

                bool is64  = IntPtr.Size == 8;
                int  btnSz = is64 ? Marshal.SizeOf<TBBUTTON64>() : Marshal.SizeOf<TBBUTTON32>();
                int  trySz = Marshal.SizeOf<TRAYDATA>();
                // Буфер достаточный для обеих структур + запас для строки tooltip
                int  bufSz = Math.Max(btnSz, trySz) + 512;

                IntPtr remBuf = VirtualAllocEx(hProc, IntPtr.Zero, bufSz, MEM_COMMIT, PAGE_READWRITE);
                if (remBuf == IntPtr.Zero) return;

                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        SendMessage(toolbar, TB_GETBUTTON, (IntPtr)i, remBuf);

                        IntPtr localBtn = Marshal.AllocHGlobal(btnSz);
                        try
                        {
                            if (!ReadProcessMemory(hProc, remBuf, localBtn, btnSz, out _)) continue;

                            ulong dwData; byte fsState; long iString;
                            if (is64)
                            {
                                var b = Marshal.PtrToStructure<TBBUTTON64>(localBtn);
                                dwData = b.dwData; fsState = b.fsState; iString = b.iString;
                            }
                            else
                            {
                                var b = Marshal.PtrToStructure<TBBUTTON32>(localBtn);
                                dwData = (ulong)b.dwData; fsState = b.fsState; iString = b.iString;
                            }

                            if (filterHidden && (fsState & TBSTATE_HIDDEN) != 0) continue;

                            // dwData — указатель на TRAYDATA в памяти Explorer
                            IntPtr trayPtr = new IntPtr(unchecked((long)dwData));
                            if (trayPtr == IntPtr.Zero) continue;

                            IntPtr localTry = Marshal.AllocHGlobal(trySz);
                            try
                            {
                                if (!ReadProcessMemory(hProc, trayPtr, localTry, trySz, out _)) continue;
                                var td = Marshal.PtrToStructure<TRAYDATA>(localTry);

                                if (td.uCallbackMessage == 0) continue;
                                if (td.hwnd == IntPtr.Zero) continue;

                                // Уникальный ключ иконки — (hwnd, uID), а не ProcessId
                                // Один процесс может иметь несколько иконок с разными uID
                                if (result.Exists(x => x.AppHwnd == td.hwnd && x.AppID == td.uID)) continue;

                                GetWindowThreadProcessId(td.hwnd, out uint appPid);
                                int pid = (int)appPid;
                                if (pid == 0 || pid == _ownPid) continue;

                                string procName;
                                try   { procName = Process.GetProcessById(pid).ProcessName; }
                                catch { continue; }

                                if (_hardSysProcs.Contains(procName)) continue;

                                // Читаем tooltip (iString — указатель на строку в памяти Explorer)
                                string tooltip = procName;
                                if (iString > 0xFFFF && iString != 0) // не индекс, а указатель
                                {
                                    try
                                    {
                                        IntPtr strLocal = Marshal.AllocHGlobal(256);
                                        try
                                        {
                                            if (ReadProcessMemory(hProc, new IntPtr(iString), strLocal, 256, out _))
                                            {
                                                // Пробуем Unicode сначала
                                                string s = Marshal.PtrToStringUni(strLocal, 64).TrimEnd('\0').Trim();
                                                if (s.Length > 1 && s.All(c => c >= 32 || c == '\n'))
                                                    tooltip = s;
                                            }
                                        }
                                        finally { Marshal.FreeHGlobal(strLocal); }
                                    }
                                    catch { }
                                }

                                // Читаем иконку:
                                // td.hIcon — это HICON внутри адресного пространства Explorer.
                                // Нельзя использовать напрямую — нужно читать само значение хэндла
                                // через ReadProcessMemory и потом вызывать CopyIcon.
                                BitmapSource bmp = null;

                                if (td.hIcon != IntPtr.Zero)
                                {
                                    try
                                    {
                                        // td.hIcon уже содержит значение хэндла (не указатель на хэндл),
                                        // но хэндл принадлежит Explorer — CopyIcon работает кросс-процессно
                                        IntPtr copied = CopyIcon(td.hIcon);
                                        if (copied != IntPtr.Zero)
                                        {
                                            try
                                            {
                                                bmp = Imaging.CreateBitmapSourceFromHIcon(
                                                    copied, Int32Rect.Empty,
                                                    BitmapSizeOptions.FromEmptyOptions());
                                                if (bmp?.CanFreeze == true) bmp.Freeze();
                                            }
                                            finally { DestroyIcon(copied); }
                                        }
                                    }
                                    catch { }
                                }

                                // Fallback: берём иконку из исполняемого файла процесса
                                if (bmp == null)
                                {
                                    try { bmp = Helpers.IconHelper.GetIconFromProcess(pid, 16); }
                                    catch { }
                                }

                                result.Add(new TrayIcon
                                {
                                    AppHwnd      = td.hwnd,
                                    AppID        = td.uID,
                                    CallbackMsg  = td.uCallbackMessage,
                                    ProcessName  = tooltip,
                                    ProcessId    = pid,
                                    IconImage    = bmp,
                                    NotifyVersion = td.uVersion
                                });
                            }
                            finally { Marshal.FreeHGlobal(localTry); }
                        }
                        finally { Marshal.FreeHGlobal(localBtn); }
                    }
                }
                finally { VirtualFreeEx(hProc, remBuf, 0, MEM_RELEASE); }
            }
            finally { CloseHandle(hProc); }
        }

        // ── Рендер ───────────────────────────────────────────────────
        void RenderIcons(List<TrayIcon> icons)
        {
            IconsPanel.Children.Clear();
            EmptyLabel.Visibility = icons.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var icon in icons)
                IconsPanel.Children.Add(MakeBtn(icon));

            int count = icons.Count;
            int cols  = count == 0 ? 1 :
                        count <= 3 ? count :
                        count <= 9 ? 3 : 4;

            double winWidth = cols * ICON_CELL + PANEL_PAD + 2;
            IconsPanel.MaxWidth = winWidth;
            Width = winWidth;
        }

        UIElement MakeBtn(TrayIcon icon)
        {
            var btn = new Button
            {
                Width           = ICON_SIZE,
                Height          = ICON_SIZE,
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor          = Cursors.Hand,
                Focusable       = false,
                Margin          = new Thickness(2),
                ToolTip         = string.IsNullOrEmpty(icon.ProcessName) ? "App" : icon.ProcessName
            };

            var bdF = new FrameworkElementFactory(typeof(Border));
            bdF.Name = "bg";
            bdF.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bdF.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            var cpF = new FrameworkElementFactory(typeof(ContentPresenter));
            cpF.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cpF.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bdF.AppendChild(cpF);

            var tpl = new ControlTemplate(typeof(Button)) { VisualTree = bdF };

            var hoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), "bg"));
            var pressTrigger = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), "bg"));
            tpl.Triggers.Add(hoverTrigger);
            tpl.Triggers.Add(pressTrigger);
            btn.Template = tpl;

            if (icon.IconImage != null)
                btn.Content = new Image
                {
                    Source              = icon.IconImage,
                    Width               = 16,
                    Height              = 16,
                    Stretch             = Stretch.Uniform,
                    SnapsToDevicePixels = true
                };
            else
            {
                string letter = string.IsNullOrEmpty(icon.ProcessName)
                    ? "?" : icon.ProcessName.Substring(0, 1).ToUpper();
                btn.Content = new TextBlock
                {
                    Text                = letter,
                    Foreground          = Brushes.White,
                    FontSize            = 11,
                    FontWeight          = FontWeights.SemiBold,
                    VerticalAlignment   = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
            }

            // ── Левый клик ─────────────────────────────────────────
            btn.Click += (s, e) =>
            {
                _blockDeactivate = true;
                try
                {
                    if (icon.IsTelegram)
                        ActivateTelegram(icon);
                    else
                        SendIconClick(icon, WM_LBUTTONDOWN, WM_LBUTTONUP, NIN_SELECT);
                }
                catch { }
                finally
                {
                    _ = Task.Delay(350).ContinueWith(_ => Dispatcher.Invoke(() =>
                    {
                        _blockDeactivate = false;
                        SlideUp();
                    }));
                }
            };

            // ── Двойной клик ───────────────────────────────────────
            btn.MouseDoubleClick += (s, e) =>
            {
                if (e.ChangedButton != MouseButton.Left) return;
                e.Handled = true;

                _blockDeactivate = true;
                try
                {
                    if (icon.IsTelegram)
                        ActivateTelegram(icon);
                    else
                        SendIconClick(icon, WM_LBUTTONDBLCLK, WM_LBUTTONUP, NIN_SELECT);
                }
                catch { }
                finally
                {
                    _ = Task.Delay(350).ContinueWith(_ => Dispatcher.Invoke(() =>
                    {
                        _blockDeactivate = false;
                        SlideUp();
                    }));
                }
            };

            // ── Правый клик — сразу нативное меню приложения ──────
            btn.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                _blockDeactivate = true;
                ShowNativeContextMenu(icon);
            };

            return btn;
        }

        // ── Отправка tray-сообщений ──────────────────────────────────
        //
        // Протоколы Shell_NotifyIcon:
        //   V4 (Vista+, uVersion=4): wParam=MAKEWPARAM(x,y),  lParam=MAKELPARAM(uMsg,uID)
        //   V3 (legacy, uVersion≤3): wParam=uID,              lParam=uMsg
        //
        // Алгоритм:
        //   - uVersion==4  → только V4
        //   - uVersion==3  → только V3
        //   - uVersion==0  → пробуем V4, через 80мс V3 (если приложение не отреагировало)
        //     Проверка реакции: смотрим GetForegroundWindow до и после
        //
        void SendIconClick(TrayIcon icon, uint downMsg, uint upMsg, uint selectMsg)
        {
            if (!IsWindow(icon.AppHwnd)) return;

            GetCursorPos(out POINT pt);
            IntPtr wp4 = MakeWParam(pt.X, pt.Y);
            IntPtr wp3 = (IntPtr)icon.AppID;

            if (icon.NotifyVersion == NOTIFYICONVERSION_4)
            {
                // Только V4
                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp4, MakeLParam(downMsg,   icon.AppID));
                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp4, MakeLParam(upMsg,     icon.AppID));
                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp4, MakeLParam(selectMsg, icon.AppID));
            }
            else if (icon.NotifyVersion == NOTIFYICONVERSION_3 || icon.NotifyVersion == 0 && IsLegacyApp(icon))
            {
                // Только V3
                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp3, (IntPtr)downMsg);
                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp3, (IntPtr)upMsg);
                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp3, (IntPtr)selectMsg);
            }
            else
            {
                // Версия неизвестна — шлём V4, и через 80мс если окно не стало foreground — шлём V3
                GetWindowThreadProcessId(GetForegroundWindow(), out uint fgBefore);

                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp4, MakeLParam(downMsg,   icon.AppID));
                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp4, MakeLParam(upMsg,     icon.AppID));
                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp4, MakeLParam(selectMsg, icon.AppID));

                // Запоминаем версию как V4 — если сработает, больше не будем гадать
                _ = Task.Delay(120).ContinueWith(_ =>
                {
                    GetWindowThreadProcessId(GetForegroundWindow(), out uint fgAfter);
                    if (fgAfter == (uint)icon.ProcessId)
                    {
                        // V4 сработал — запоминаем
                        icon.NotifyVersion = NOTIFYICONVERSION_4;
                    }
                    else
                    {
                        // V4 не сработал — пробуем V3 и запоминаем
                        icon.NotifyVersion = NOTIFYICONVERSION_3;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!IsWindow(icon.AppHwnd)) return;
                            PostMessage(icon.AppHwnd, icon.CallbackMsg, wp3, (IntPtr)downMsg);
                            PostMessage(icon.AppHwnd, icon.CallbackMsg, wp3, (IntPtr)upMsg);
                            PostMessage(icon.AppHwnd, icon.CallbackMsg, wp3, (IntPtr)selectMsg);
                        }));
                    }
                });
            }
        }

        // Эвристика: старые приложения (.NET 2-4, MFC, Delphi) обычно имеют старые окна
        bool IsLegacyApp(TrayIcon icon)
        {
            try
            {
                var proc = Process.GetProcessById(icon.ProcessId);
                // Приложения старше ~2010 года обычно 32-битные
                return !Environment.Is64BitOperatingSystem ||
                       proc.MainModule?.FileName?.EndsWith("wow64", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch { return false; }
        }

        void SendIconRightClick(TrayIcon icon, POINT pt)
        {
            if (!IsWindow(icon.AppHwnd)) return;

            IntPtr wp4 = MakeWParam(pt.X, pt.Y);
            IntPtr wp3 = (IntPtr)icon.AppID;

            if (icon.NotifyVersion == NOTIFYICONVERSION_3 || icon.NotifyVersion == 0 && IsLegacyApp(icon))
            {
                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp3, (IntPtr)WM_RBUTTONDOWN);
                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp3, (IntPtr)WM_RBUTTONUP);
                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp3, (IntPtr)WM_CONTEXTMENU);
            }
            else
            {
                // NotifyVersion == 4 или неизвестен — шлём только V4.
                // Дублировать V3 НЕЛЬЗЯ: приложение получит два WM_CONTEXTMENU и покажет меню дважды.
                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp4, MakeLParam(WM_RBUTTONDOWN, icon.AppID));
                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp4, MakeLParam(WM_RBUTTONUP,   icon.AppID));
                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp4, MakeLParam(WM_CONTEXTMENU, icon.AppID));

                // Если версия неизвестна — проверяем через 120мс; если V4 не сработал — шлём V3 (один раз)
                if (icon.NotifyVersion == 0)
                {
                    _ = Task.Delay(120).ContinueWith(_ =>
                    {
                        GetWindowThreadProcessId(GetForegroundWindow(), out uint fgAfter);
                        if (fgAfter == (uint)icon.ProcessId)
                        {
                            icon.NotifyVersion = NOTIFYICONVERSION_4;
                        }
                        else
                        {
                            icon.NotifyVersion = NOTIFYICONVERSION_3;
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (!IsWindow(icon.AppHwnd)) return;
                                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp3, (IntPtr)WM_RBUTTONDOWN);
                                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp3, (IntPtr)WM_RBUTTONUP);
                                PostMessage(icon.AppHwnd, icon.CallbackMsg, wp3, (IntPtr)WM_CONTEXTMENU);
                            }));
                        }
                    });
                }
            }
        }

        // MAKEWPARAM(lo=x, hi=y)
        static IntPtr MakeWParam(int x, int y)
            => new IntPtr(unchecked((int)(((uint)(ushort)y << 16) | (uint)(ushort)x)));

        // MAKELPARAM(lo=msg, hi=id)
        static IntPtr MakeLParam(uint msg, uint id)
            => new IntPtr(unchecked((int)(((uint)(ushort)id << 16) | (uint)(ushort)msg)));

        // ── Контекстное меню пустой области трея ───────────────────
        void ShowEmptyAreaContextMenu()
        {
            var menu = new ContextMenu { IsOpen = false };

            if (_hiddenIcons.Count > 0)
            {
                var itemUnhide = new MenuItem { Header = $"Показать скрытые ({_hiddenIcons.Count})" };
                itemUnhide.Click += (s, e) => UnhideAllIcons();
                menu.Items.Add(itemUnhide);
                menu.Items.Add(new Separator());
            }

            var itemClose = new MenuItem { Header = "Закрыть панель" };
            itemClose.Click += (s, e) =>
            {
                SlideUp(() => OnTrayClosed?.Invoke());
                _blockDeactivate = false;
            };
            menu.Items.Add(itemClose);

            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen    = true;
            _blockDeactivate = true;

            menu.Closed += (s, e) =>
            {
                _blockDeactivate = false;
            };
        }

        // ── Наше контекстное меню иконки трея ───────────────────────
        void ShowTrayContextMenu(TrayIcon icon)
        {
            var menu = new ContextMenu { IsOpen = false };

            // Пункт: открыть меню приложения
            var itemOpen = new MenuItem { Header = "Открыть меню" };
            itemOpen.Click += (s, e) => ShowNativeContextMenu(icon);
            menu.Items.Add(itemOpen);

            menu.Items.Add(new Separator());

            // Пункт: скрыть иконку
            string displayName = icon.ProcessName ?? "?";
            var itemHide = new MenuItem { Header = $"Скрыть «{displayName}»" };
            itemHide.Click += (s, e) =>
            {
                SlideUp(() => OnTrayClosed?.Invoke());
                _blockDeactivate = false;
                HideIcon(icon.ProcessName);
            };
            menu.Items.Add(itemHide);

            // Пункт: показать все скрытые (только если есть скрытые)
            if (_hiddenIcons.Count > 0)
            {
                var itemUnhide = new MenuItem { Header = $"Показать скрытые ({_hiddenIcons.Count})" };
                itemUnhide.Click += (s, e) => UnhideAllIcons();
                menu.Items.Add(itemUnhide);
            }

            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen    = true;
            _blockDeactivate = true;

            menu.Closed += (s, e) =>
            {
                _blockDeactivate = false;
            };
        }

        // ── Контекстные меню ─────────────────────────────────────────
        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(uint dwProcessId);

        void ShowNativeContextMenu(TrayIcon icon)
        {
            // [FIX] Запоминаем позицию курсора ДО скрытия попапа —
            // после SlideUp мышь может сместиться и меню откроется в неверном месте.
            GetCursorPos(out POINT savedPt);

            // Сначала скрываем наш попап — тогда курсор "освобождается"
            // и приложение покажет меню точно под иконкой трея.
            SlideUp(() => OnTrayClosed?.Invoke());
            _blockDeactivate = false;

            // Небольшая пауза чтобы попап успел скрыться перед отправкой клика
            _ = Task.Delay(50).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                try
                {
                    AllowSetForegroundWindow((uint)icon.ProcessId);

                    if (icon.IsTelegram)
                        SendTelegramRightClick(icon, savedPt);
                    else
                        SendIconRightClick(icon, savedPt);
                }
                catch { }
            }));
        }

        // [FIX] Правый клик для Telegram: использует FakeWindow как цель,
        // передаёт сохранённые координаты cursor чтобы меню появилось под иконкой.
        void SendTelegramRightClick(TrayIcon icon, POINT pt)
        {
            // Ищем TelegramDesktopFakeWindow — именно он обрабатывает tray-сообщения
            IntPtr fakeHwnd = IntPtr.Zero;
            EnumWindows((hwnd, lp) =>
            {
                GetWindowThreadProcessId(hwnd, out uint wpid);
                if ((int)wpid != icon.ProcessId) return true;
                var sb = new System.Text.StringBuilder(128);
                GetClassName(hwnd, sb, 128);
                string cls = sb.ToString();
                if (cls == "TelegramDesktopFakeWindow" ||
                    cls.IndexOf("FakeWindow", StringComparison.OrdinalIgnoreCase) >= 0)
                { fakeHwnd = hwnd; return false; }
                return true;
            }, IntPtr.Zero);

            IntPtr target = fakeHwnd != IntPtr.Zero ? fakeHwnd :
                            IsWindow(icon.AppHwnd) ? icon.AppHwnd : IntPtr.Zero;
            if (target == IntPtr.Zero || icon.CallbackMsg == 0) return;

            // Telegram: Shell_NotifyIcon V4
            //   wParam = MAKEWPARAM(cursorX, cursorY), lParam = MAKELPARAM(msg, id)
            IntPtr wp = MakeWParam(pt.X, pt.Y);
            PostMessage(target, icon.CallbackMsg, wp, MakeLParam(WM_RBUTTONDOWN, icon.AppID));
            PostMessage(target, icon.CallbackMsg, wp, MakeLParam(WM_RBUTTONUP,   icon.AppID));
            PostMessage(target, icon.CallbackMsg, wp, MakeLParam(WM_CONTEXTMENU, icon.AppID));
        }

        // ── Deactivated ──────────────────────────────────────────────
        void Window_Deactivated(object sender, EventArgs e)
        {
            if (!_blockDeactivate && _isOpen && !_animating)
                SlideUp(() => Dispatcher.Invoke(() => OnTrayClosed?.Invoke()));
        }

        protected override void OnClosed(EventArgs e)
        {
            StopTrayPolling();
            base.OnClosed(e);
        }

        public Action OnTrayClosed { get; set; }
    }
}
