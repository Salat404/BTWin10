using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

// ═══════════════════════════════════════════════════════════════════════════════
// StartMenuAcrylicFix — убирает «лишний блюр» акрила на панели задач когда
//                       под ней проходит меню Пуск.
//
// ПРОБЛЕМА:
//   Акриловый эффект (ACCENT_ENABLE_ACRYLICBLURBEHIND) на панели задач
//   размывает всё что находится позади HWND панели — включая окно меню Пуск
//   которое при анимации открытия/закрытия проходит сквозь зону панели.
//   Из-за этого на панели видны «призрачные» размытые следы содержимого Пуска.
//
// РЕШЕНИЕ:
//   Отслеживать через WinEventHook (EVENT_OBJECT_SHOW / EVENT_OBJECT_HIDE /
//   EVENT_OBJECT_DESTROY) окна Пуска Win10 и Win11.
//   Когда Пуск появляется — переключать панель в режим без blur (сплошной
//   полупрозрачный цвет через ACCENT_DISABLED + WPF Background).
//   Когда Пуск скрывается — восстанавливать акрил с небольшой задержкой
//   (чтобы анимация закрытия Пуска успела завершиться).
//
// КАК ИСПОЛЬЗОВАТЬ:
//   // В MainWindow.Startup.cs после ApplyAcrylicBackground():
//   _startMenuAcrylicFix = new StartMenuAcrylicFix(
//       this, () => ApplyAcrylicBackground(), Dispatcher);
//   _startMenuAcrylicFix.Start();
//
//   // В MainWindow_Closed:
//   _startMenuAcrylicFix?.Stop();
//
// ═══════════════════════════════════════════════════════════════════════════════

namespace MyTaskbar.Helpers
{
    public sealed class StartMenuAcrylicFix : IDisposable
    {
        // ── P/Invoke ──────────────────────────────────────────────────────────

        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
            IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
            uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        // WCA / AccentPolicy — для ACCENT_DISABLED без мигания
        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy
        {
            public uint AccentState;
            public uint AccentFlags;
            public uint GradientColor;
            public uint AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WindowCompositionAttributeData
        {
            public uint Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        static extern int SetWindowCompositionAttribute(IntPtr hwnd,
            ref WindowCompositionAttributeData data);

        // ── Константы ─────────────────────────────────────────────────────────

        // WinEvent
        const uint EVENT_OBJECT_SHOW    = 0x8002;
        const uint EVENT_OBJECT_HIDE    = 0x8003;
        const uint EVENT_OBJECT_DESTROY = 0x8001;
        const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        // WCA
        const uint WCA_ACCENT_POLICY = 19;
        const uint ACCENT_DISABLED   = 0;

        // Задержка восстановления акрила после закрытия Пуска (мс).
        // Нужна чтобы анимация «уезжания» Пуска завершилась прежде чем
        // панель снова начнёт размывать то что за ней.
        const int RESTORE_DELAY_MS = 250;

        // ── Состояние ─────────────────────────────────────────────────────────

        readonly Window      _window;
        readonly Action      _restoreAcrylic;   // callback → ApplyAcrylicBackground()
        readonly Dispatcher  _dispatcher;

        WinEventDelegate     _delegate;         // держим от GC
        IntPtr               _hook = IntPtr.Zero;

        bool                 _startMenuVisible = false;
        DispatcherTimer      _restoreTimer;

        // ── Конструктор ───────────────────────────────────────────────────────

        /// <param name="window">Окно панели задач.</param>
        /// <param name="restoreAcrylicCallback">Метод восстановления акрила (ApplyAcrylicBackground).</param>
        /// <param name="dispatcher">UI Dispatcher окна.</param>
        public StartMenuAcrylicFix(Window window, Action restoreAcrylicCallback, Dispatcher dispatcher)
        {
            _window        = window        ?? throw new ArgumentNullException(nameof(window));
            _restoreAcrylic = restoreAcrylicCallback ?? throw new ArgumentNullException(nameof(restoreAcrylicCallback));
            _dispatcher    = dispatcher    ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        // ── Публичный API ─────────────────────────────────────────────────────

        public void Start()
        {
            if (_hook != IntPtr.Zero) return;

            _delegate = OnWinEvent;
            _hook = SetWinEventHook(
                EVENT_OBJECT_DESTROY, EVENT_OBJECT_SHOW,   // диапазон: DESTROY(8001)..SHOW(8002) + HIDE(8003)
                IntPtr.Zero, _delegate, 0, 0, WINEVENT_OUTOFCONTEXT);

            // Дополнительный хук для HIDE (0x8003) — вне диапазона выше
            // SetWinEventHook поддерживает только один диапазон за вызов,
            // поэтому регистрируем второй хук отдельно.
            _hookHide = SetWinEventHook(
                EVENT_OBJECT_HIDE, EVENT_OBJECT_HIDE,
                IntPtr.Zero, _delegate, 0, 0, WINEVENT_OUTOFCONTEXT);

            // Проверим текущее состояние — вдруг Пуск уже открыт
            _dispatcher.BeginInvoke(new Action(CheckStartMenuNow));
        }

        public void Stop()
        {
            _restoreTimer?.Stop();

            if (_hook != IntPtr.Zero)     { UnhookWinEvent(_hook);     _hook     = IntPtr.Zero; }
            if (_hookHide != IntPtr.Zero) { UnhookWinEvent(_hookHide); _hookHide = IntPtr.Zero; }
        }

        public void Dispose() => Stop();

        // ── Внутренняя реализация ─────────────────────────────────────────────

        IntPtr _hookHide = IntPtr.Zero;

        void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // Фильтр: нас интересуют только OBJECT (idObject == 0)
            if (idObject != 0) return;
            if (hwnd == IntPtr.Zero) return;

            // Быстрая проверка: является ли hwnd окном Пуска?
            if (!IsStartMenuWindow(hwnd)) return;

            if (eventType == EVENT_OBJECT_SHOW)
            {
                // Пуск показывается — немедленно отключить акрил
                _dispatcher.BeginInvoke(new Action(OnStartMenuShown),
                    DispatcherPriority.Render);
            }
            else // HIDE или DESTROY
            {
                // Пуск скрывается — восстановить акрил с задержкой
                _dispatcher.BeginInvoke(new Action(OnStartMenuHidden),
                    DispatcherPriority.Render);
            }
        }

        void OnStartMenuShown()
        {
            if (_startMenuVisible) return;
            _startMenuVisible = true;

            // Останавливаем таймер восстановления если он тикал
            _restoreTimer?.Stop();

            // Отключаем DWM blur на панели задач
            DisableAcrylicOnTaskbar();
        }

        void OnStartMenuHidden()
        {
            if (!_startMenuVisible) return;

            // Двойная проверка: может быть ещё одно окно Пуска открыто
            if (IsAnyStartMenuVisible())
                return;

            _startMenuVisible = false;

            // Запускаем таймер восстановления
            if (_restoreTimer == null)
            {
                _restoreTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(RESTORE_DELAY_MS)
                };
                _restoreTimer.Tick += (s, e) =>
                {
                    _restoreTimer.Stop();
                    // Ещё раз убеждаемся что Пуск закрыт
                    if (!IsAnyStartMenuVisible())
                        _restoreAcrylic();
                };
            }
            _restoreTimer.Stop();
            _restoreTimer.Start();
        }

        void CheckStartMenuNow()
        {
            if (IsAnyStartMenuVisible())
                OnStartMenuShown();
        }

        /// <summary>
        /// Отключает акриловый эффект на панели через WCA_ACCENT_POLICY → ACCENT_DISABLED.
        /// Используем прямой WinAPI вызов (не через AcrylicHelper.Disable) чтобы не трогать
        /// WPF Background — панель останется с полупрозрачным фоном заданным в XAML.
        /// </summary>
        void DisableAcrylicOnTaskbar()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(_window).Handle;
                if (hwnd == IntPtr.Zero) return;

                var accent = new AccentPolicy
                {
                    AccentState   = ACCENT_DISABLED,
                    AccentFlags   = 0,
                    GradientColor = 0,
                    AnimationId   = 0,
                };

                int size = Marshal.SizeOf(accent);
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, ptr, false);
                    var data = new WindowCompositionAttributeData
                    {
                        Attribute  = WCA_ACCENT_POLICY,
                        Data       = ptr,
                        SizeOfData = size,
                    };
                    SetWindowCompositionAttribute(hwnd, ref data);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            catch { }
        }

        // ── Определение окна Пуска ────────────────────────────────────────────

        static bool IsStartMenuWindow(IntPtr hwnd)
        {
            try
            {
                var cls = new StringBuilder(256);
                GetClassName(hwnd, cls, cls.Capacity);
                string className = cls.ToString();

                // Win10: "Windows.UI.Core.CoreWindow" + title "Пуск"/"Start"
                if (className == "Windows.UI.Core.CoreWindow")
                {
                    var title = new StringBuilder(256);
                    GetWindowText(hwnd, title, title.Capacity);
                    string t = title.ToString();
                    return t == "Пуск" || t == "Start" || t == "开始" || t == "スタート"
                           || t.Equals("start", StringComparison.OrdinalIgnoreCase);
                }

                // Win11 / новые сборки Win10
                if (className == "Microsoft.UI.Content.DesktopChildSiteBridge")
                    return true;

                // StartMenuExperienceHost (Win11)
                if (className == "Xaml_WindowedPopupClass")
                    return true;
            }
            catch { }

            return false;
        }

        static bool IsAnyStartMenuVisible()
        {
            // Win10 CoreWindow (ru)
            IntPtr h = FindWindow("Windows.UI.Core.CoreWindow", "Пуск");
            if (h != IntPtr.Zero && IsWindowVisible(h)) return true;

            // Win10 CoreWindow (en)
            h = FindWindow("Windows.UI.Core.CoreWindow", "Start");
            if (h != IntPtr.Zero && IsWindowVisible(h)) return true;

            // Win11 / новые сборки
            h = FindWindow("Microsoft.UI.Content.DesktopChildSiteBridge", null);
            if (h != IntPtr.Zero && IsWindowVisible(h)) return true;

            return false;
        }
    }
}
