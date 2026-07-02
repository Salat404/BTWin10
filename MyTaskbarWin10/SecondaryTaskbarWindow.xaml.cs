using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyTaskbar.Helpers;

// ═══════════════════════════════════════════════════════════════════════════
// SecondaryTaskbarWindow.xaml.cs
//
// Полная копия панели MyTaskbar для ВТОРОГО монитора.
//
// Отличие от главной (MainWindow):
//   • Всегда auto-hide — скрывается даже на рабочем столе (не только в fullscreen).
//   • Появляется только когда курсор касается нижнего (или верхнего) края
//     второго монитора.
//   • Все кнопки, часы, иконки — идентичны главной панели.
//   • Синхронизирует своё состояние кнопок приложений через MainWindow._groups.
// ═══════════════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class SecondaryTaskbarWindow : Window
    {
        // ── P/Invoke ────────────────────────────────────────────────────────
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int nIndex, int val);
        [DllImport("user32.dll")] static extern bool RegisterDragDrop(IntPtr hwnd, IDropTarget pDropTarget);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT  { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        // COM IDropTarget (passthrough) — чтобы drag&drop не застревал на панели
        [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDropTarget
        {
            [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] object pDataObj, uint grfKeyState, NsPoint pt, ref uint pdwEffect);
            [PreserveSig] int DragOver(uint grfKeyState, NsPoint pt, ref uint pdwEffect);
            [PreserveSig] int DragLeave();
            [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] object pDataObj, uint grfKeyState, NsPoint pt, ref uint pdwEffect);
        }
        [StructLayout(LayoutKind.Sequential)] struct NsPoint { public int x, y; }
        class PassthroughDrop : IDropTarget
        {
            public int DragEnter(object d, uint k, NsPoint p, ref uint e) { e = 0; return 0; }
            public int DragOver(uint k, NsPoint p, ref uint e) { e = 0; return 0; }
            public int DragLeave() { return 0; }
            public int Drop(object d, uint k, NsPoint p, ref uint e) { e = 0; return 0; }
        }

        const uint MONITOR_DEFAULTTONEAREST = 2;
        const uint MONITOR_DEFAULTTONULL    = 0;
        const int  GWL_EXSTYLE   = -20;
        const int  WS_EX_LAYERED = 0x00080000;
        const int  WS_EX_NOACTIVATE = 0x08000000;

        // ── Состояние ───────────────────────────────────────────────────────
        bool   _isBottom        = true;   // позиция: снизу или сверху
        bool   _isHidden        = true;   // панель скрыта
        bool   _animating       = false;  // идёт анимация
        DateTime _animStartTime = DateTime.MinValue; // для определения зависшей анимации
        double _uiScale         = 1.0;
        double _dpiScale        = 1.0;
        int    TASKBAR_H        => (int)Math.Round(40.0 * _dpiScale * _uiScale);

        // Геометрия второго монитора (WPF DIP)
        double _monLeft, _monTop, _monRight, _monBottom, _monWidth, _monHeight;
        bool   _hasSecondMonitor = false;

        // Ссылка на главное окно (для синхронизации кнопок/состояний)
        MainWindow _main;

        // [PRIMARY-MONITOR] Имя устройства монитора, который занята главная панель.
        // Пустая строка = системный primary. Обновляется через SetPrimaryMonitorDevice().
        string _primaryMonitorDevice = "";

        // Таймеры
        DispatcherTimer _edgeTimer;    // опрос края
        DispatcherTimer _clockTimer;
        DispatcherTimer _hideTimer;    // задержка скрытия после ухода курсора

        int _hideDelayMs = 400;

        // [FIX-MENU-HIDE] true пока меню Пуск открыто — панель не скрывается
        bool _menuIsOpen = false;

        // ── Конструктор ─────────────────────────────────────────────────────
        public SecondaryTaskbarWindow(MainWindow main, bool isBottom, double uiScale, string primaryMonitorDevice = "")
        {
            _main     = main;
            _isBottom = isBottom;
            _uiScale  = uiScale;
            _primaryMonitorDevice = primaryMonitorDevice;
            InitializeComponent();
            Loaded  += OnLoaded;
            Closed  += OnClosed;
            // Не активируем окно при клике — фокус остаётся у приложения
            MouseEnter += (_, __) => OnPanelMouseEnter();
            MouseLeave += (_, __) => OnPanelMouseLeave();
        }

        // ── Загрузка ────────────────────────────────────────────────────────
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Читаем DPI (isBottom и uiScale уже переданы через конструктор)
                var src = PresentationSource.FromVisual(this);
                _dpiScale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

                // Применяем масштаб как у главного окна
                if (Math.Abs(_uiScale - 1.0) > 0.01)
                    MainPanel.LayoutTransform = new ScaleTransform(_uiScale, _uiScale);

                // Acrylic / blur — такой же как у главного окна
                ApplyAcrylic();

                // WS_EX_LAYERED нужен для HTTRANSPARENT, WS_EX_NOACTIVATE — не уводим фокус
                var hwnd = new WindowInteropHelper(this).Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED | WS_EX_NOACTIVATE);

                // Passthrough drag&drop
                try { RegisterDragDrop(hwnd, new PassthroughDrop()); } catch { }

                // Читаем геометрию второго монитора
                RefreshSecondMonitorBounds();

                if (!_hasSecondMonitor)
                {
                    // Нет второго монитора — окно остаётся скрытым
                    Visibility = Visibility.Hidden;
                    return;
                }

                // Начальное положение: спрятано за краем экрана
                PositionHidden();
                Visibility = Visibility.Visible;

                // Часы
                StartClock();

                // Синхронизация кнопок приложений
                SyncAppButtons();

                // Таймер опроса края
                _edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                _edgeTimer.Tick += (_, __) => CheckEdge();
                _edgeTimer.Start();

                // [FIX-MENU-HIDE] Подписываемся на открытие/закрытие меню Пуск.
                // Когда меню открыто — блокируем скрытие панели.
                // Когда меню закрывается — скрываем панель если курсор ушёл.
                var menuWnd = _main.PublicMenuWindow;
                if (menuWnd != null)
                {
                    menuWnd.MenuVisibilityChanged += isOpen =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _menuIsOpen = isOpen;
                            if (!isOpen && !IsMouseOver)
                                ScheduleHide();
                        }));
                    };
                }

                // Таймер часов уже запущен выше
                UpdateBorderLines();

                Debug.WriteLine("[Secondary] Loaded on monitor: " +
                    $"L={_monLeft:F0} T={_monTop:F0} W={_monWidth:F0} H={_monHeight:F0}");
            }
            catch (Exception ex) { Debug.WriteLine($"[Secondary] OnLoaded: {ex}"); }
        }

        void OnClosed(object sender, EventArgs e)
        {
            _edgeTimer?.Stop();
            _clockTimer?.Stop();
            _hideTimer?.Stop();
        }

        // ── Второй монитор: нахождение геометрии ────────────────────────────
        /// <summary>
        /// Перечисляет все мониторы и возвращает первый, который НЕ является
        /// основным (primary). Координаты переводятся из физических пикселей в DIP.
        /// </summary>
        void RefreshSecondMonitorBounds()
        {
            _hasSecondMonitor = false;

            // Определяем какой монитор занят главной панелью:
            // Если пользователь выбрал конкретный монитор — исключаем его по имени устройства.
            // Иначе исключаем системный primary (точка 0,0).
            var ptZero = new POINT { x = 0, y = 0 };
            IntPtr sysPrimaryMon = MonitorFromPoint(ptZero, MONITOR_DEFAULTTONEAREST);

            var secondRect = new RECT();
            bool found = false;

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMon, IntPtr hdc, ref RECT monRect, IntPtr data) =>
                {
                    if (found) return true; // берём только первый дополнительный

                    var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (!GetMonitorInfo(hMon, ref mi)) return true;

                    // Пропускаем монитор, занятый главной панелью
                    if (!string.IsNullOrEmpty(_primaryMonitorDevice))
                    {
                        // Сравниваем по имени устройства через MONITORINFOEX
                        var miEx = new MONITORINFOEX_S { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFOEX_S)) };
                        if (GetMonitorInfoW_S(hMon, ref miEx))
                        {
                            if (string.Equals(miEx.szDevice, _primaryMonitorDevice,
                                              StringComparison.OrdinalIgnoreCase))
                                return true; // это главная — пропускаем
                        }
                    }
                    else
                    {
                        // Пропускаем системный primary
                        if (hMon == sysPrimaryMon) return true;
                    }

                    secondRect = mi.rcMonitor;
                    found = true;
                    return true;
                },
                IntPtr.Zero);

            if (!found) return;

            double dpi = _dpiScale > 0 ? _dpiScale : 1.0;
            _monLeft   = secondRect.left   / dpi;
            _monTop    = secondRect.top    / dpi;
            _monRight  = secondRect.right  / dpi;
            _monBottom = secondRect.bottom / dpi;
            _monWidth  = _monRight - _monLeft;
            _monHeight = _monBottom - _monTop;
            _hasSecondMonitor = true;
        }

        // MONITORINFOEX для чтения szDevice внутри SecondaryTaskbarWindow
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX_S
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfoW_S(IntPtr hMon, ref MONITORINFOEX_S lpmi);

        // ── Позиционирование ────────────────────────────────────────────────
        void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                if (!_hasSecondMonitor) return;
                if (_isHidden) PositionHidden();
                else           PositionVisible();
            }
            catch { }
        }

        double VisibleTop  => _isBottom ? _monBottom - TASKBAR_H : _monTop;
        double HiddenTop   => _isBottom ? _monBottom + 4         : _monTop - (TASKBAR_H + 4);
        double CenteredLeft
        {
            get
            {
                UpdateLayout();
                double dpi = _dpiScale > 0 ? _dpiScale : 1.0;
                double screenPx = Math.Round(_monWidth * dpi);
                double panelPx  = Math.Round(ActualWidth * dpi);
                double leftPx   = Math.Floor((screenPx - panelPx) / 2.0);
                return _monLeft + leftPx / dpi;
            }
        }

        void PositionVisible()
        {
            if (!IsLoaded) return;
            Left   = CenteredLeft;
            Top    = VisibleTop;
            Height = TASKBAR_H;
        }

        void PositionHidden()
        {
            if (!IsLoaded) return;
            Left   = CenteredLeft;
            Top    = HiddenTop;
            Height = TASKBAR_H;
        }

        void UpdateBorderLines()
        {
            if (BorderLineTop    != null) BorderLineTop.Visibility    = _isBottom ? Visibility.Visible  : Visibility.Collapsed;
            if (BorderLineBottom != null) BorderLineBottom.Visibility = _isBottom ? Visibility.Collapsed : Visibility.Visible;
        }

        // ── Edge reveal / hide (опрос края второго монитора) ────────────────
        // Вызывается каждые 80 мс таймером. Это единственный надёжный источник
        // истины о положении курсора: WPF MouseLeave ненадёжен для окон с
        // WS_EX_NOACTIVATE — событие может не прийти когда курсор уходит с панели.
        void CheckEdge()
        {
            try
            {
                if (!_hasSecondMonitor) return;

                // [FIX-ANIM-STUCK] Если анимация идёт дольше 700 мс — что-то пошло
                // не так (например окно было скрыто в момент анимации). Сбрасываем флаг.
                if (_animating)
                {
                    var elapsed = (DateTime.UtcNow - _animStartTime).TotalMilliseconds;
                    if (elapsed > 700) _animating = false;
                    else return;
                }

                if (!GetCursorPos(out POINT cp)) return;

                // Курсор в физических пикселях (Win32). Используем их напрямую для
                // hit-теста по RECT окна — без конвертации в DIP.
                // Для edge-check всё же нужны DIP (монитор в DIP).
                double dpi = _dpiScale > 0 ? _dpiScale : 1.0;
                double cx = cp.x / dpi;
                double cy = cp.y / dpi;

                // [FIX-EDGE-WIDTH] Полоска срабатывания — по ширине панели, а не всего монитора
                double panelLeft  = CenteredLeft;
                double panelRight = panelLeft + (ActualWidth > 0 ? ActualWidth : _monWidth);
                bool inPanelX = cx >= panelLeft && cx <= panelRight;

                bool atEdge = _isBottom
                    ? (inPanelX && cy >= _monBottom - 2)
                    : (inPanelX && cy <= _monTop + 2);

                // ── Показать панель ─────────────────────────────────────────
                if (atEdge && _isHidden)
                {
                    ShowPanel(animate: true);
                    return;
                }

                // ── Скрыть панель ───────────────────────────────────────────
                // Условия: панель видима, нет открытых попапов, курсор не над панелью.
                // Win32 GetWindowRect используется вместо WPF IsMouseOver —
                // IsMouseOver ненадёжен для WS_EX_NOACTIVATE окон.
                if (!_isHidden && !_hideTimer_Active())
                {
                    bool anyFlyout = _main?.IsAnyFlyoutOpen == true;
                    if (!anyFlyout && !_menuIsOpen)
                    {
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT wr))
                        {
                            bool overPanel = cp.x >= wr.left && cp.x <= wr.right
                                          && cp.y >= wr.top  && cp.y <= wr.bottom;

                            // Не скрываем панель если курсор над окном превью DWM
                            bool overPreview = false;
                            try
                            {
                                IntPtr ph = _main?.PreviewHwnd ?? IntPtr.Zero;
                                if (ph != IntPtr.Zero && GetWindowRect(ph, out RECT pr))
                                    overPreview = cp.x >= pr.left && cp.x <= pr.right
                                               && cp.y >= pr.top  && cp.y <= pr.bottom;
                            }
                            catch { }

                            if (!overPanel && !overPreview)
                                ScheduleHide();
                        }
                    }
                }
            }
            catch { }
        }

        // Вспомогательное: запущен ли таймер скрытия прямо сейчас
        bool _hideTimer_Active() => _hideTimer != null && _hideTimer.IsEnabled;

        void OnPanelMouseEnter()
        {
            // Курсор вошёл на панель — отменяем таймер скрытия
            _hideTimer?.Stop();
        }

        void OnPanelMouseLeave()
        {
            // Курсор ушёл — проверяем не перешёл ли он на окно превью.
            // Если да — не скрываем панель (CheckEdge это тоже отловит, но здесь быстрее).
            try
            {
                if (GetCursorPos(out POINT cp2))
                {
                    IntPtr ph = _main?.PreviewHwnd ?? IntPtr.Zero;
                    if (ph != IntPtr.Zero && GetWindowRect(ph, out RECT pr))
                    {
                        bool overPreview = cp2.x >= pr.left && cp2.x <= pr.right
                                        && cp2.y >= pr.top  && cp2.y <= pr.bottom;
                        if (overPreview) return; // курсор на превью — не скрываем
                    }
                }
            }
            catch { }
            ScheduleHide();
        }

        void ScheduleHide()
        {
            // Не скрываем пока открыт любой попап (меню Пуск, трей, WiFi, громкость…)
            // IsAnyFlyoutOpen включает _menuWindow.IsVisible, поэтому отдельный _menuIsOpen
            // используется только для подписки MenuVisibilityChanged (она всё ещё нужна
            // чтобы сбросить таймер когда меню закроется).
            if (_menuIsOpen || (_main?.IsAnyFlyoutOpen == true)) return;

            _hideTimer?.Stop();
            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_hideDelayMs) };
            _hideTimer.Tick += (_, __) =>
            {
                _hideTimer.Stop();
                HidePanel(animate: true);
            };
            _hideTimer.Start();
        }

        // ── Анимация показа/скрытия ─────────────────────────────────────────
        void ShowPanel(bool animate)
        {
            if (_animating) return;
            if (!_isHidden)  return;

            _isHidden = false;
            if (!IsLoaded || !IsVisible) return;

            // [FIX-ZORDER] Принудительно поднимаем окно поверх всех.
            // Простого Topmost=true в XAML недостаточно — если другое topmost-окно
            // появилось пока панель была скрыта, оно могло занять верхний z-order.
            // Сброс+установка заставляет Windows переместить окно в самый верх стека.
            Topmost = false;
            Topmost = true;

            double to   = VisibleTop;
            double from = HiddenTop;

            if (animate)
            {
                _animating = true;
                _animStartTime = DateTime.UtcNow;
                Left = CenteredLeft;
                Top  = from;
                var a = new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(180)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                a.Completed += (_, __) =>
                {
                    BeginAnimation(TopProperty, null);
                    Top = to;
                    _animating = false;
                };
                BeginAnimation(TopProperty, a);
            }
            else
            {
                Top = to;
            }
        }

        void HidePanel(bool animate)
        {
            if (_animating) return;
            if (_isHidden)  return;

            _isHidden = true;
            if (!IsLoaded || !IsVisible) return;

            double from = VisibleTop;
            double to   = HiddenTop;

            if (animate)
            {
                _animating = true;
                _animStartTime = DateTime.UtcNow;
                var a = new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(180)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
                a.Completed += (_, __) =>
                {
                    BeginAnimation(TopProperty, null);
                    Top = to;
                    _animating = false;
                };
                BeginAnimation(TopProperty, a);
            }
            else
            {
                Top = to;
            }
        }

        // ── Синхронизация кнопок приложений с MainWindow ────────────────────
        /// <summary>
        /// Копирует кнопки из AppIcons главного окна в AppIcons второй панели.
        /// Вызывается периодически (каждые 500 мс) из MainWindow.
        /// </summary>
        public void SyncAppButtons()
        {
            try
            {
                if (!IsLoaded) return;

                // Собираем актуальный список кнопок из главного окна
                var mainButtons = new List<Button>();
                foreach (UIElement el in _main.AppIcons.Children)
                {
                    if (el is Button btn) mainButtons.Add(btn);
                }

                // Синхронизируем список в нашем AppIcons
                // Используем Tag кнопки как идентификатор группы (exe-имя)
                // Простая стратегия: если количество или порядок изменился — перестраиваем

                bool needRebuild = false;
                if (AppIcons.Children.Count != mainButtons.Count)
                {
                    needRebuild = true;
                }
                else
                {
                    for (int i = 0; i < mainButtons.Count; i++)
                    {
                        var local = AppIcons.Children[i] as Button;
                        // [FIX-SECONDARY] Если Tag у кнопок теперь установлен (GroupKey) —
                        // сравниваем по Tag. Для надёжности дополнительно сравниваем по
                        // захваченной ссылке: у клона Tag совпадает с оригиналом, но это
                        // разные объекты, поэтому смотрим на значение Tag.
                        var mainTag = mainButtons[i].Tag?.ToString();
                        var localTag = local?.Tag?.ToString();
                        bool tagsMatch = mainTag != null
                            ? mainTag == localTag
                            : ReferenceEquals(local, mainButtons[i]); // fallback если Tag ещё null
                        if (!tagsMatch)
                        { needRebuild = true; break; }
                    }
                }

                if (!needRebuild) return;

                // Перестраиваем
                AppIcons.Children.Clear();
                foreach (var src in mainButtons)
                {
                    var clone = CloneAppButton(src);
                    AppIcons.Children.Add(clone);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[Secondary] SyncAppButtons: {ex.Message}"); }
        }

        /// <summary>
        /// Создаёт визуальную копию кнопки приложения.
        /// Клик на копии — активирует то же окно что и оригинал.
        /// </summary>
        Button CloneAppButton(Button src)
        {
            var btn = new Button
            {
                Width   = src.Width,
                Height  = src.Height,
                Tag     = src.Tag,
                ToolTip = src.ToolTip,
                Style   = TryFindResource("IconButton") as Style,
                Focusable = false,
            };

            // Копируем иконку через Image (ImageSource)
            try
            {
                if (src.Content is Image srcImg && srcImg.Source != null)
                {
                    btn.Content = new Image
                    {
                        Source  = srcImg.Source,
                        Width   = srcImg.Width,
                        Height  = srcImg.Height,
                        Stretch = srcImg.Stretch,
                        IsHitTestVisible = false,
                    };
                }
            }
            catch { }

            // [FIX-SECONDARY] При клике делегируем на оригинальную кнопку напрямую через
            // захваченную ссылку на src. Старый вариант искал оригинал по Tag — но Tag у
            // всех кнопок был null (Tag не устанавливался при создании), поэтому сравнение
            // null == null всегда было true и клик всегда попадал на первую кнопку в списке.
            var originalSrc = src; // захватываем ссылку явно, чтобы не потерять при перестройке
            btn.Click += (_, __) =>
            {
                try
                {
                    originalSrc.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch { }
            };

            // [SECONDARY-PREVIEW] Превью при наведении — вызываем MainWindow с геометрией
            // нашего монитора, чтобы окно превью появлялось над второй панелью, а не над первой.
            var capturedTag = btn.Tag?.ToString();
            btn.MouseEnter += (_, __) =>
            {
                try
                {
                    // Тултип показываем ТОЛЬКО если программа не запущена (нет окон).
                    // Для запущенных — скрываем, чтобы не перекрывал превью.
                    bool hasWindows = !string.IsNullOrEmpty(capturedTag) && _main.GroupHasWindows(capturedTag);
                    if (hasWindows)
                    {
                        ToolTipService.SetIsEnabled(btn, false);
                        if (btn.ToolTip is System.Windows.Controls.ToolTip secTt && secTt.IsOpen)
                            secTt.IsOpen = false;
                    }
                    else
                    {
                        ToolTipService.SetIsEnabled(btn, true);
                    }

                    if (!string.IsNullOrEmpty(capturedTag))
                        _main.ShowPreviewFromSecondary(btn, capturedTag,
                            _monLeft, _monRight, _monTop, _monBottom,
                            _isBottom, TaskbarHDip);
                }
                catch { }
            };
            btn.MouseLeave += (_, __) =>
            {
                try
                {
                    // Восстанавливаем тултип при уходе мыши (для pinned-кнопок без окон)
                    ToolTipService.SetIsEnabled(btn, true);
                    _main.HidePreviewFromSecondary();
                }
                catch { }
            };

            // Синхронизируем состояние (active/running/idle) через RunBar
            SyncButtonState(btn, src);

            return btn;
        }

        /// <summary>
        /// Копирует состояние RunBar и AppBorder из src в dst.
        /// Вызывается периодически для живого обновления.
        /// </summary>
        void SyncButtonState(Button dst, Button src)
        {
            try
            {
                dst.ApplyTemplate();
                src.ApplyTemplate();

                var srcRb = src.Template?.FindName("RunBar",    src) as System.Windows.Shapes.Rectangle;
                var srcAb = src.Template?.FindName("AppBorder", src) as Border;
                var dstRb = dst.Template?.FindName("RunBar",    dst) as System.Windows.Shapes.Rectangle;
                var dstAb = dst.Template?.FindName("AppBorder", dst) as Border;

                if (srcRb != null && dstRb != null)
                {
                    dstRb.Visibility = srcRb.Visibility;
                    dstRb.Fill       = srcRb.Fill;
                }
                if (srcAb != null && dstAb != null)
                {
                    dstAb.Background = srcAb.Background;
                }
            }
            catch { }
        }

        /// <summary>
        /// Обновляет состояния всех кнопок-клонов. Вызывается из MainWindow каждые ~500 мс.
        /// </summary>
        public void UpdateButtonStates()
        {
            try
            {
                if (!IsLoaded) return;
                var mainButtons = new List<Button>();
                foreach (UIElement el in _main.AppIcons.Children)
                    if (el is Button b) mainButtons.Add(b);

                for (int i = 0; i < AppIcons.Children.Count && i < mainButtons.Count; i++)
                {
                    if (AppIcons.Children[i] is Button dst)
                        SyncButtonState(dst, mainButtons[i]);
                }
            }
            catch { }
        }

        // ── Часы ────────────────────────────────────────────────────────────
        void StartClock()
        {
            UpdateClock();
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, __) => UpdateClock();
            _clockTimer.Start();
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

        // ── Acrylic ──────────────────────────────────────────────────────────
        void ApplyAcrylic()
        {
            try
            {
                bool is1903 = Environment.OSVersion.Version.Major >= 10 &&
                              Environment.OSVersion.Version.Build >= 18362;
                if (is1903) AcrylicHelper.EnableAcrylic(this, 0x701A1A2E);
                else        AcrylicHelper.EnableBlur(this, 0x70202030);
            }
            catch
            {
                try { AcrylicHelper.EnableBlur(this, 0x70202030); } catch
                {
                    if (RootBorder != null)
                        RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x70, 0x1A, 0x1A, 0x2A));
                }
            }
        }

        // ── Кнопки ────────────────────────────────────────────────────────────
        void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("[Secondary.StartButton_Click]"
                    + " _hasSecondMonitor=" + _hasSecondMonitor
                    + " _monLeft=" + _monLeft.ToString("F1") + " _monTop=" + _monTop.ToString("F1")
                    + " _monWidth=" + _monWidth.ToString("F1") + " _monHeight=" + _monHeight.ToString("F1")
                    + " _monBottom=" + _monBottom.ToString("F1"));

                // [SECONDARY-MENU] Передаём координаты второго монитора в MenuWindow,
                // чтобы меню Пуск открылось над второй панелью, а не над первым монитором.
                var menu = _main.PublicMenuWindow;
                Debug.WriteLine("[Secondary.StartButton_Click] PublicMenuWindow=" + (menu == null ? "null" : "ok"));
                if (menu != null)
                {
                    var rect = new System.Windows.Rect(_monLeft, _monTop, _monWidth, _monHeight);
                    menu.SourceMonitorRect = rect;
                    Debug.WriteLine("[Secondary.StartButton_Click] SourceMonitorRect установлен: " + rect.ToString());
                }
                _main.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                // [SECONDARY-MENU] Сбрасываем rect после RaiseEvent:
                // если StartButton_Click не открыл меню (JustHidden), rect остался бы грязным
                // и следующий клик с монитора 1 открыл бы меню на мониторе 2.
                if (menu != null && !menu.IsVisible)
                {
                    menu.SourceMonitorRect = null;
                    Debug.WriteLine("[Secondary.StartButton_Click] меню не открылось (JustHidden?) — rect сброшен");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Secondary.StartButton_Click] EXCEPTION: " + ex.ToString());
            }
        }

        void StartButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            try { _main.StartButton.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                e.MouseDevice, e.Timestamp, System.Windows.Input.MouseButton.Right)
                { RoutedEvent = Button.MouseRightButtonUpEvent }); }
            catch { }
        }

        // ── Вспомогательный: X-центр кнопки в экранных DIP-координатах ────
        double BtnCenterX(Button btn)
        {
            try { return btn.PointToScreen(new System.Windows.Point(btn.ActualWidth / 2, 0)).X; }
            catch { return _monLeft + _monWidth / 2; }
        }

        // ── Высота панели в DIP (для передачи в MainWindow) ─────────────────
        // TASKBAR_H умножает на _dpiScale (физические пиксели), поэтому
        // для позиционирования в WPF DIP используем только uiScale без dpiScale.
        double TaskbarHDip => 40.0 * _uiScale;

        void TrayButton_Click(object sender, RoutedEventArgs e)
        {
            // [FIX-SECONDARY-POS] Открываем трей на втором мониторе, а не на первом
            try
            {
                double cx = BtnCenterX(sender as Button ?? TrayButton);
                _main.OpenTrayFromSecondary(cx, _monBottom, _monTop,
                                            _monLeft, _monRight, TaskbarHDip, _isBottom);
            }
            catch { }
        }

        void BrightnessButton_Click(object sender, RoutedEventArgs e)
        {
            // [FIX-SECONDARY-POS] Яркость на втором мониторе
            try
            {
                double cx = BtnCenterX(sender as Button ?? BrightnessButton);
                _main.OpenBrightnessFromSecondary(cx, _monBottom, _monTop, TaskbarHDip, _isBottom);
            }
            catch { }
        }

        void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            // [FIX-SECONDARY-POS] Громкость на втором мониторе
            try
            {
                double cx = BtnCenterX(sender as Button ?? VolumeButton);
                _main.OpenVolumeFromSecondary(cx, _monBottom, _monTop, TaskbarHDip, _isBottom);
            }
            catch { }
        }

        void VolumeButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            try { _main.VolumeButton.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                e.MouseDevice, e.Timestamp, System.Windows.Input.MouseButton.Right)
                { RoutedEvent = Button.MouseRightButtonUpEvent }); }
            catch { }
        }

        void LangButton_Click(object sender, RoutedEventArgs e)
        {
            try { _main.LangButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            catch { }
        }

        void LangButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            // [FIX-SECONDARY-POS] Меню языка привязываем к кнопке второй панели, не первой.
            // [FIX-LANG-TIMER] Отменяем таймер скрытия сразу — до того как OpenLangMenuFromSecondary
            // выставит _langContextMenuOpen, чтобы исключить гонку с MouseLeave.
            _hideTimer?.Stop();
            try { _main.OpenLangMenuFromSecondary(sender as Button ?? LangButton); }
            catch { }
        }

        void BluetoothButton_Click(object sender, RoutedEventArgs e)
        {
            try { _main.BluetoothButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            catch { }
        }

        void WifiButton_Click(object sender, RoutedEventArgs e)
        {
            // [FIX-SECONDARY-POS] WiFi на втором мониторе
            try
            {
                _main.OpenWifiFromSecondary(_monLeft, _monRight, _monBottom,
                                            _monTop, TaskbarHDip, _isBottom);
            }
            catch { }
        }

        // ── Публичные методы для синхронизации из MainWindow ─────────────────

        /// <summary>Синхронизирует позицию (top/bottom) с главной панелью.</summary>
        public void SetPosition(bool isBottom)
        {
            if (_isBottom == isBottom) return;
            _isBottom = isBottom;
            UpdateBorderLines();
            if (_isHidden) PositionHidden();
            else           PositionVisible();
        }

        /// <summary>Синхронизирует масштаб UI с главной панелью.</summary>
        public void SetUiScale(double scale)
        {
            _uiScale = scale;
            MainPanel.LayoutTransform = Math.Abs(scale - 1.0) < 0.01
                ? null
                : new ScaleTransform(scale, scale);
            if (_isHidden) PositionHidden();
            else           PositionVisible();
        }

        /// <summary>Синхронизирует текст LangLabel.</summary>
        public void SetLang(string text)
        {
            try { if (LangLabel != null) LangLabel.Text = text; } catch { }
        }

        /// <summary>Синхронизирует иконку Wi-Fi.</summary>
        public void SetWifiIcon(string icon, string tooltip)
        {
            try
            {
                if (WifiIcon    != null) WifiIcon.Text    = icon;
                if (WifiButton  != null) WifiButton.ToolTip = tooltip;
            }
            catch { }
        }

        /// <summary>Синхронизирует иконку громкости.</summary>
        public void SetVolumeIcon(string icon)
        {
            try { if (VolumeIcon != null) VolumeIcon.Text = icon; } catch { }
        }

        /// <summary>Синхронизирует иконку яркости.</summary>
        public void SetBrightnessIcon(string icon)
        {
            try { if (BrightnessIcon != null) BrightnessIcon.Text = icon; } catch { }
        }

        /// <summary>Синхронизирует состояние батареи.</summary>
        public void SetBattery(string label, bool visible, bool charging, bool showPercent)
        {
            try
            {
                if (BatteryButton  != null) BatteryButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (BatteryLabel   != null) BatteryLabel.Text   = label;
                if (BatteryPercent != null) BatteryPercent.Visibility = showPercent ? Visibility.Visible : Visibility.Collapsed;
                if (BatteryLightning != null) BatteryLightning.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>Синхронизирует иконку Bluetooth.</summary>
        public void SetBluetooth(Brush stroke, string tooltip)
        {
            try
            {
                if (BluetoothIcon   != null) BluetoothIcon.Stroke = stroke;
                if (BluetoothButton != null) BluetoothButton.ToolTip = tooltip;
            }
            catch { }
        }

        /// <summary>
        /// Перепрочитывает геометрию второго монитора (например, после смены конфигурации дисплеев).
        /// </summary>
        public void RefreshMonitor()
        {
            RefreshSecondMonitorBounds();
            if (_hasSecondMonitor) PositionHidden();
        }

        /// <summary>
        /// Обновляет имя устройства монитора, занятого главной панелью,
        /// и перепозиционирует вторичную панель на следующий свободный монитор.
        /// </summary>
        public void SetPrimaryMonitorDevice(string deviceName)
        {
            _primaryMonitorDevice = deviceName;
            RefreshSecondMonitorBounds();
            if (_hasSecondMonitor)
            {
                PositionHidden();
                Visibility = Visibility.Visible;
            }
            else
            {
                Visibility = Visibility.Hidden;
            }
        }
    }
}
