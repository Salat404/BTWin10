using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MyTaskbar
{
    public partial class MenuWindow : Window
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, MouseHookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        // ── SetWindowPos для управления Z-order ──
        static readonly IntPtr HWND_TOP = new IntPtr(0);
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;

        // [GAME-10] Флаг: меню открывается поверх fullscreen-игры — нужен Topmost
        public bool IsFullscreenMode { get; set; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        delegate IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

        const int WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_RBUTTONDOWN = 0x0204;
        const int WM_MBUTTONDOWN = 0x0207;

        IntPtr _mouseHook;
        MouseHookProc _mouseHookProc;

        void InstallMouseHook()
        {
            if (_mouseHook != IntPtr.Zero) return;
            _mouseHookProc = MouseHookCallback;
            using (var p = Process.GetCurrentProcess())
            using (var m = p.MainModule)
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(m.ModuleName), 0);
        }

        void UninstallMouseHook()
        {
            if (_mouseHook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                bool isClick = msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN;
                if (isClick && IsVisible)
                {
                    var hs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    POINT p = hs.pt;

                    bool overMenu     = IsOver(this, p);
                    bool overPreview  = PreviewWindow != null && PreviewWindow.IsVisible && IsOver(PreviewWindow, p);
                    bool overTaskbar  = TaskbarWindow != null && TaskbarWindow.IsVisible && IsOver(TaskbarWindow, p);
                    
                    bool overUs = overMenu || overPreview || overTaskbar;
                    
                    if (!overUs)
                    {
                        Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Normal,
                            new Action(() =>
                            {
                                try
                                {
                                    if (_powerExpanded)
                                        CollapsePowerButtons();
                                    else
                                        HideAnimated();
                                }
                                catch { }
                            }));
                    }
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }

        const byte VK_LWIN = 0x5B;
        const byte VK_S = 0x53;
        const uint KEYEVENTF_KEYUP = 0x0002;

        [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        { [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm); }

        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [Flags] enum SIIGBF : int { ResizeToFit = 0x00, BiggerSizeOk = 0x01, IconOnly = 0x04, NoOverlay = 0x40 }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }
        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x000;

        // ══════════════════════════════════════════════════════════════
        //  Поля
        // ══════════════════════════════════════════════════════════════

        static readonly string ProgramsFolder = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Programs");
        static readonly string GamesFolder = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Games");

        // TileIconSize, TileHeight, TileMargin теперь — свойства выше (масштабируются с UIScale)

        List<(string Name, string Path, BitmapSource Icon)> _tileItems =
            new List<(string, string, BitmapSource)>();

        FileSystemWatcher _watcherPrograms;
        FileSystemWatcher _watcherGames;

        DateTime _hiddenAt = DateTime.MinValue;
        DateTime _lastClickInside = DateTime.MinValue;

        public const int HideGraceMs = 300;
        public bool JustHidden => (DateTime.UtcNow - _hiddenAt).TotalMilliseconds < HideGraceMs;
        public void ResetHiddenAt() => _hiddenAt = DateTime.MinValue;

        public event Action<bool> MenuVisibilityChanged;
        // [GAME-9] Событие: пользователь запустил приложение из меню (нужно снять блокер)
        public event Action AppLaunched;
        // [CLIP-FIX] Событие: меню достигло финальной позиции после анимации
        public event Action MenuPositionReady;
        // [BLUR-FIX] Стріляє після завершення анімації ховання (меню фізично за екраном)
        public event Action MenuHideCompleted;

        public Window PreviewWindow { get; set; }
        public Window TaskbarWindow { get; set; }

        static bool IsOver(Window w, POINT p)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return false;
                if (!GetWindowRect(h, out RECT r)) return false;
                return p.x >= r.left && p.x <= r.right && p.y >= r.top && p.y <= r.bottom;
            }
            catch { return false; }
        }

        // ══════════════════════════════════════════════════════════════
        //  Высота панели / позиции
        // ══════════════════════════════════════════════════════════════

        // [UI-SCALE-MENU] Высота панели задач с учётом масштаба (40px × uiScale)
        double ScaledTaskbarH => 40.0 * _uiScale;

        // [SECONDARY-MENU] Прямоугольник монитора, с которого открыто меню (WPF DIP).
        // null = использовать основной монитор (поведение по умолчанию).
        System.Windows.Rect? _sourceMonitorRect = null;
        DateTime _sourceMonitorSetAt = DateTime.MinValue;
        bool _deactivatedFromSecondary = false;
        public System.Windows.Rect? SourceMonitorRect
        {
            get => _sourceMonitorRect;
            set
            {
                _sourceMonitorRect = value;
                if (value.HasValue)
                {
                    _sourceMonitorSetAt = DateTime.UtcNow;
                    _deactivatedFromSecondary = true;
                }
                else
                    _deactivatedFromSecondary = false;
                // Сбросить кэш позиций при смене монитора
                _hasCachedPositions = false;
            }
        }

        // Вспомогательные свойства: высота и нижний край «нужного» монитора
        double MonHeight => SourceMonitorRect.HasValue
            ? SourceMonitorRect.Value.Height
            : SystemParameters.PrimaryScreenHeight;

        double MonBottom => SourceMonitorRect.HasValue
            ? SourceMonitorRect.Value.Bottom
            : SystemParameters.PrimaryScreenHeight;

        double MonTop => SourceMonitorRect.HasValue
            ? SourceMonitorRect.Value.Top
            : 0;

        double MonLeft => SourceMonitorRect.HasValue
            ? SourceMonitorRect.Value.Left
            : 0;

        double MonWidth => SourceMonitorRect.HasValue
            ? SourceMonitorRect.Value.Width
            : SystemParameters.PrimaryScreenWidth;

        double _visibleTopCache;
        double _hiddenTopCache;
        bool _hasCachedPositions = false;

        double VisibleTop
        {
            get
            {
                if (!_hasCachedPositions)
                    return IsBottom
                        ? MonBottom - ScaledTaskbarH - (ActualHeight > 0 ? ActualHeight : 500) - 4
                        : MonTop + ScaledTaskbarH + 4;
                return _visibleTopCache;
            }
            set => _visibleTopCache = value;
        }

        double HiddenTop
        {
            get
            {
                if (!_hasCachedPositions)
                    return IsBottom
                        ? MonBottom + 4
                        : MonTop - ((ActualHeight > 0 ? ActualHeight : 500) + 4);
                return _hiddenTopCache;
            }
            set => _hiddenTopCache = value;
        }

        // Устанавливается из MainWindow перед ShowMenu()
        public bool IsBottom { get; set; } = false;
        // [UI-SCALE]
        const double BaseTopBarH   = 36.0; // базовая высота верхней полоски
        const double BaseTopBarBtn = 36.0; // базовая ширина/высота кнопок

        const double BaseIconFontSize = 15.0;

        // Находит первый TextBlock внутри визуального дерева элемента
        static System.Windows.Controls.TextBlock FindIconTextBlock(DependencyObject parent)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is System.Windows.Controls.TextBlock tb) return tb;
                var found = FindIconTextBlock(child);
                if (found != null) return found;
            }
            return null;
        }

        void ScaleBtnIcon(Button btn, double fontSize)
        {
            if (btn == null || !btn.IsLoaded) return;
            var tb = FindIconTextBlock(btn);
            if (tb != null) tb.FontSize = fontSize;
        }

        void ApplyTopBarScale()
        {
            double s = _uiScale;
            double h = Math.Round(BaseTopBarH * s);
            double b = Math.Round(BaseTopBarBtn * s);
            double fs = Math.Round(BaseIconFontSize * s, 1);

            if (TopBarGrid != null)   TopBarGrid.Height = h;
            if (ColPower != null)     ColPower.Width    = new GridLength(b);
            if (ColSearch != null)    ColSearch.Width   = new GridLength(b);
            if (ColSettings != null)  ColSettings.Width = new GridLength(b);

            // Кнопки Restart и Sleep
            if (BtnRestart != null) { BtnRestart.Width = b; BtnRestart.Height = h; }
            if (BtnSleep   != null) { BtnSleep.Width   = b; BtnSleep.Height   = h; }

            // Иконки — ищем TextBlock внутри визуального дерева каждой кнопки
            ScaleBtnIcon(BtnPower,      fs);
            ScaleBtnIcon(BtnRestart,    fs);
            ScaleBtnIcon(BtnSleep,      fs);
            ScaleBtnIcon(BtnToggleView, fs);
            ScaleBtnIcon(BtnSettings,   fs);
        }

        const double BaseMenuWidth = 380.0; // базовая ширина меню при scale=1.0
        const double BaseTileIconSize = 32;
        const double BaseTileHeight   = 48;
        const double BaseTileMargin   = 2;

        // Актуальные размеры плиток с учётом текущего масштаба
        double TileIconSize => BaseTileIconSize * _uiScale;
        double TileHeight   => BaseTileHeight   * _uiScale;
        double TileMargin   => BaseTileMargin;

        double _uiScale = 1.0;
        public double UIScale
        {
            get => _uiScale;
            set
            {
                _uiScale = value;
                // Масштабируем ширину окна пропорционально scale.
                // LayoutTransform НЕ используем — он растягивает содержимое внутри
                // фиксированной ширины и сжимает плитки.
                // Вместо этого меняем Width окна, а SizeToContent="Height" сам
                // пересчитает высоту под новую ширину.
                this.Width = Math.Round(BaseMenuWidth * value);
                ApplyTopBarScale();

                // Пересобираем плитки чтобы TileIconSize/TileHeight пересчитались
                if (IsLoaded && PanelPrograms != null)
                    BuildAll();
            }
        }

        // [UI-SCALE-MENU] Пересчёт позиции меню после изменения масштаба.
        // Вызывается из MainWindow.ApplyUIScale() после установки UIScale.
        // Если меню видно — немедленно двигаем его на новый VisibleTop.
        // Если скрыто — просто обновляем HiddenTop чтобы следующий ShowMenu вышел правильно.
        public void RepositionAfterScale()
        {
            try
            {
                UpdateLayout(); // пересчитать ActualHeight после нового ScaleTransform
                PositionWindow();
                if (_isShown && !_isAnimating)
                    Top = VisibleTop;
                else if (!_isShown)
                    Top = HiddenTop;
            }
            catch { }
        }

        public const double ANIM_SHOW_MS = 180;
        public const double ANIM_HIDE_MS = 130;

        /// <summary>Если false — ShowMenu/HideAnimated мгновенно без анимации.</summary>
        public bool AnimEnabled { get; set; } = true;

        bool _isAnimating = false;
        bool _initialized = false;
        bool _isShown = false; // true когда меню видно или анимируется к видимому состоянию

        const int GWL_EXSTYLE_MW = -20;
        const int WS_EX_NOACTIVATE_MW = 0x08000000;
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] static extern int GetWindowLongMW(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] static extern int SetWindowLongMW(IntPtr hwnd, int index, int val);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLongMW(hwnd, GWL_EXSTYLE_MW);
            style &= ~WS_EX_NOACTIVATE_MW;
            SetWindowLongMW(hwnd, GWL_EXSTYLE_MW, style);
        }

        // ══════════════════════════════════════════════════════════════
        //  Z-order: под панелью задач
        // ══════════════════════════════════════════════════════════════

        void PutBelowTaskbar()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (IsFullscreenMode && TaskbarWindow != null)
            {
                // [GAME-10] В fullscreen-режиме вставляем меню сразу ПОД панелью задач в z-order.
                // SetWindowPos с hWndInsertAfter=taskbarHwnd означает "поместить сразу за ней (ниже)".
                // Это гарантирует: панель > меню > игра, без риска что меню окажется выше панели.
                try
                {
                    IntPtr taskbarHwnd = new WindowInteropHelper(TaskbarWindow).Handle;
                    if (taskbarHwnd != IntPtr.Zero)
                    {
                        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                        SetWindowPos(hwnd, taskbarHwnd, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                        return;
                    }
                }
                catch { }
                // Fallback: просто Topmost если нет handle панели
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            }
            else
            {
                // Обычный режим — снимаем Topmost, поднимаем среди обычных окон
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Конструктор
        // ══════════════════════════════════════════════════════════════

        public MenuWindow()
        {
            InitializeComponent();

            // Topmost намеренно НЕ ставим — панель задач сама держит себя выше

            PreviewMouseDown += (s, e) =>
            {
                _lastClickInside = DateTime.UtcNow;

                if (_powerExpanded)
                {
                    var clicked = e.OriginalSource as DependencyObject;
                    bool onPowerArea = false;

                    foreach (var btn in new[] { BtnPower, BtnRestart, BtnSleep })
                    {
                        if (clicked != null && (clicked == btn || IsVisualChild(clicked, btn)))
                        {
                            onPowerArea = true;
                            break;
                        }
                    }

                    if (!onPowerArea)
                        CollapsePowerButtons();
                }
            };

            Deactivated += (s, e) =>
            {
                if ((DateTime.UtcNow - _lastClickInside).TotalMilliseconds < 200) return;
                if (!_isShown) return;
                // [SECONDARY-MENU] Если меню открыто вторичной панелью (флаг установлен)
                // и прошло менее 300 мс — это обычная раб процедура открытия меню
                // Deactivated вызван RaiseEvent(), а не реальным уходом фокуса
                if (_deactivatedFromSecondary && (DateTime.UtcNow - _sourceMonitorSetAt).TotalMilliseconds < 300)
                {
                    Debug.WriteLine("[MenuWindow.Deactivated] открыто с вторичного монитора — пропускаем HideAnimated");
                    return;
                }
                HideAnimated();
            };

            Loaded += (s, e) =>
            {
                ApplyAcrylic();
                ApplyTopBarScale();
                BuildAll();
                PositionWindow();
                Top = HiddenTop;
                Opacity = 1;
                if (Background == null || Background == Brushes.Transparent)
                    Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

                // Следим за папками Programs и Games — автообновление при добавлении/удалении ярлыков
                _watcherPrograms = StartFolderWatcher(ProgramsFolder);
                _watcherGames = StartFolderWatcher(GamesFolder);
            };

            Closed += (s, e) =>
            {
                try { _watcherPrograms?.Dispose(); } catch { }
                try { _watcherGames?.Dispose(); } catch { }
            };
        }

        static bool IsVisualChild(DependencyObject child, DependencyObject parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        void ApplyAcrylic()
        {
            try { MyTaskbar.Helpers.AcrylicHelper.EnableAcrylic(this, 0x701A1A2E); }
            catch { try { MyTaskbar.Helpers.AcrylicHelper.EnableBlur(this, 0x70202030); } catch { } }
        }

        void PositionWindow()
        {
            UpdateLayout();
            // [SECONDARY-MENU] Центрирование меню по монитору, с которого открыли.
            bool hasRect = SourceMonitorRect.HasValue;
            string rectStr = hasRect ? SourceMonitorRect.Value.ToString() : "null";
            double sw = MonWidth;
            double sl = MonLeft;
            double st = MonTop;
            double sb = MonBottom;
            double newLeft = Math.Round(sl + (sw - ActualWidth) / 2.0);

            // [SECONDARY-MENU-FIX] Учитываем Top координату вторичного монитора при расчёте HiddenTop и VisibleTop
            double newHiddenTop, newVisibleTop;
            if (hasRect)
            {
                // Для вторичного монитора: меню скрывается над верхним краем экрана
                newHiddenTop = st - ActualHeight;
                // Меню показывается ниже панели задач на вторичном мониторе С ОТСТУПОМ 4px
                newVisibleTop = IsBottom 
                    ? sb - ScaledTaskbarH - (ActualHeight > 0 ? ActualHeight : 500) - 4
                    : st + ScaledTaskbarH + 4;  // ДОБАВЛЕН ОТСТУП + 4
            }
            else
            {
                // Первичный монитор — старое поведение
                newHiddenTop = HiddenTop;
                newVisibleTop = VisibleTop;
            }

            Debug.WriteLine("[MenuWindow.PositionWindow]"
                + " SourceMonitorRect=" + rectStr
                + " MonLeft=" + sl.ToString("F1") + " MonWidth=" + sw.ToString("F1")
                + " ActualWidth=" + ActualWidth.ToString("F1")
                + " Left=" + newLeft.ToString("F1") + " (было " + Left.ToString("F1") + ")");
            Debug.WriteLine("[MenuWindow.PositionWindow]"
                + " IsBottom=" + IsBottom
                + " MonTop=" + st.ToString("F1") + " MonBottom=" + sb.ToString("F1")
                + " ScaledTaskbarH=" + ScaledTaskbarH.ToString("F1") + " ActualH=" + ActualHeight.ToString("F1")
                + " newHiddenTop=" + newHiddenTop.ToString("F1") + " newVisibleTop=" + newVisibleTop.ToString("F1"));

            Left = newLeft;
            // Обновляем позиции для вторичного монитора, если он установлен
            if (hasRect)
            {
                _hasCachedPositions = true;
                HiddenTop = newHiddenTop;
                VisibleTop = newVisibleTop;
                Top = newHiddenTop; // Начинаем с скрытой позиции
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  ShowMenu — слайд сверху вниз
        // ══════════════════════════════════════════════════════════════

        public void ShowMenu()
        {
            string smr = SourceMonitorRect.HasValue ? SourceMonitorRect.Value.ToString() : "null";
            Debug.WriteLine("[MenuWindow.ShowMenu] вызван. SourceMonitorRect=" + smr + " IsBottom=" + IsBottom);

            if (_isAnimating) StopAllAnimations();

            PositionWindow();

            if (!_initialized)
            {
                Top = HiddenTop;
                Opacity = 1;
                Show();
                _initialized = true;
            }

            // Вместо Topmost = true — ставим под панель задач
            PutBelowTaskbar();

            InstallMouseHook();
            _isShown = true;
            MenuVisibilityChanged?.Invoke(true);

            _isAnimating = true;
            // Если анимация скрытия была прервана — начинаем с текущей позиции,
            // а не прыгаем на HiddenTop (иначе дёрганье)
            double minTop = Math.Min(HiddenTop, VisibleTop);
            double maxTop = Math.Max(HiddenTop, VisibleTop);
            double fromTop = (Top >= minTop && Top <= maxTop) ? Top : HiddenTop;
            double toTop = VisibleTop;

            BeginAnimation(TopProperty, null);
            Top = fromTop;

            // [MENU-ANIM] Если анимация отключена — сразу на финальную позицию
            if (!AnimEnabled)
            {
                Top = toTop;
                _isAnimating = false;
                PutBelowTaskbar();
                Activate();
                MenuPositionReady?.Invoke();
                return;
            }

            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 };
            var aSlide = new DoubleAnimation(fromTop, toTop, TimeSpan.FromMilliseconds(ANIM_SHOW_MS))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            aSlide.Completed += (s, e) =>
            {
                _isAnimating = false;
                BeginAnimation(TopProperty, null);
                Top = toTop;
                PutBelowTaskbar();   // повторяем после анимации — на случай перекрытия
                Activate();
                // [CLIP-FIX] Меню достигло финальной позиции — обновляем ClipCursor
                // чтобы курсор мог свободно перемещаться в область меню
                MenuPositionReady?.Invoke();
            };

            BeginAnimation(TopProperty, aSlide);
        }

        // ══════════════════════════════════════════════════════════════
        //  HideAnimated — слайд снизу вверх за край
        // ══════════════════════════════════════════════════════════════

        public void HideAnimated()
        {
            if (!_initialized) return;
            // [FIX-FLICKER] Если уже скрываемся (анимация скрытия запущена) — не перезапускаем.
            // Повторный вызов из Deactivated прерывал бы анимацию и вызывал мерцание/дёрганье
            // при последующих нажатиях Win-клавиши.
            if (_isAnimating && !_isShown) return;

            if (_powerExpanded)
                CollapsePowerImmediate();

            // [GAME-11-FIX] Перед анимацией скрытия принудительно ставим меню
            // ПОД панелью задач — иначе при первом скрытии меню уезжает поверх тулбара.
            // (z-order мог сбиться после ShowFullscreenBlocker, который делает тулбар Topmost)
            PutBelowTaskbar();

            // _hiddenAt обновляем только если меню действительно было видно —
            // иначе повторный вызов (напр. из Deactivated после Win-key) сдвигает
            // таймер вперёд и StartButton_Click пропускает следующий клик мышью.
            if (IsVisible) _hiddenAt = DateTime.UtcNow;
            UninstallMouseHook();

            // Логически закрываем сразу — чтобы StartButton_Click не вызвал
            // HideAnimated повторно пока анимация скрытия ещё идёт (дёрганье)
            _isShown = false;
            MenuVisibilityChanged?.Invoke(false);
            // [SECONDARY-MENU] Сбрасываем источник монитора — следующий ShowMenu
            // будет позиционироваться по свежеустановленному rect (или по null = монитор 1)
            SourceMonitorRect = null;
            Debug.WriteLine("[MenuWindow.HideAnimated] SourceMonitorRect сброшен в null");

            if (_isAnimating) StopAllAnimations();

            _isAnimating = true;
            double fromTop = Top;
            double toTop = HiddenTop;

            // [MENU-ANIM] Если анимация отключена — мгновенно убираем за экран
            if (!AnimEnabled)
            {
                Top = toTop;
                _isAnimating = false;
                IsFullscreenMode = false;
                var hwnd2 = new WindowInteropHelper(this).Handle;
                if (hwnd2 != IntPtr.Zero)
                    SetWindowPos(hwnd2, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                MenuHideCompleted?.Invoke();
                return;
            }

            var ease = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 4 };
            var aSlide = new DoubleAnimation(fromTop, toTop, TimeSpan.FromMilliseconds(ANIM_HIDE_MS))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            aSlide.Completed += (s, e) =>
            {
                _isAnimating = false;
                BeginAnimation(TopProperty, null);
                Top = toTop;
                // [GAME-10] Сбрасываем fullscreen-режим и снимаем Topmost при закрытии
                IsFullscreenMode = false;
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                // [BLUR-FIX] Меню фізично за екраном — сигналізуємо для скидання blur
                MenuHideCompleted?.Invoke();
            };

            BeginAnimation(TopProperty, aSlide);
        }

        // ══════════════════════════════════════════════════════════════
        //  IsVisible / StopAllAnimations
        // ══════════════════════════════════════════════════════════════

        public new bool IsVisible => _isShown;

        void StopAllAnimations()
        {
            BeginAnimation(TopProperty, null);
            BeginAnimation(OpacityProperty, null);
            _isAnimating = false;
        }

        public void Refresh() => BuildAll();

        // ══════════════════════════════════════════════════════════════
        //  Питание
        // ══════════════════════════════════════════════════════════════

        bool _powerExpanded = false;
        const double BasePowerExpandedWidth = 72.0;
        double PowerExpandedWidth => Math.Round(BasePowerExpandedWidth * _uiScale);

        void Power_Click(object sender, RoutedEventArgs e)
        {
            if (_powerExpanded)
            {
                CollapsePowerImmediate();
                HideAnimated();
                DoShutdown();
            }
            else
            {
                ExpandPowerButtons();
            }
        }

        void ExpandPowerButtons()
        {
            PowerExpandPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
            PowerExpandPanel.BeginAnimation(UIElement.OpacityProperty, null);

            BtnRestart.RenderTransform = new TranslateTransform(-PowerExpandedWidth, 0);
            BtnSleep.RenderTransform = new TranslateTransform(-PowerExpandedWidth, 0);

            PowerExpandPanel.Width = PowerExpandedWidth;
            PowerExpandPanel.Opacity = 1;

            _powerExpanded = true;

            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 };
            var dur = TimeSpan.FromMilliseconds(220);

            var animX1 = new DoubleAnimation(-PowerExpandedWidth, 0, dur) { EasingFunction = ease };
            var animX2 = new DoubleAnimation(-PowerExpandedWidth, 0, dur)
            {
                EasingFunction = ease,
                BeginTime = TimeSpan.FromMilliseconds(30)
            };
            var animO = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 3 }
            };

            BtnRestart.RenderTransform.BeginAnimation(TranslateTransform.XProperty, animX1);
            BtnSleep.RenderTransform.BeginAnimation(TranslateTransform.XProperty, animX2);
            PowerExpandPanel.BeginAnimation(UIElement.OpacityProperty, animO);
        }

        void CollapsePowerButtons()
        {
            _powerExpanded = false;

            var ease = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 4 };
            var dur = TimeSpan.FromMilliseconds(160);

            var animX1 = new DoubleAnimation(0, -PowerExpandedWidth, dur) { EasingFunction = ease };
            var animX2 = new DoubleAnimation(0, -PowerExpandedWidth, dur) { EasingFunction = ease };
            var animO = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 3 }
            };

            animO.Completed += (s, e) =>
            {
                PowerExpandPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
                PowerExpandPanel.Width = 0;
                PowerExpandPanel.Opacity = 0;
                BtnRestart.RenderTransform = new TranslateTransform(-PowerExpandedWidth, 0);
                BtnSleep.RenderTransform = new TranslateTransform(-PowerExpandedWidth, 0);
            };

            BtnRestart.RenderTransform?.BeginAnimation(TranslateTransform.XProperty, animX1);
            BtnSleep.RenderTransform?.BeginAnimation(TranslateTransform.XProperty, animX2);
            PowerExpandPanel.BeginAnimation(UIElement.OpacityProperty, animO);
        }

        void CollapsePowerImmediate()
        {
            _powerExpanded = false;
            PowerExpandPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
            PowerExpandPanel.BeginAnimation(UIElement.OpacityProperty, null);
            PowerExpandPanel.Width = 0;
            PowerExpandPanel.Opacity = 0;
            BtnRestart.RenderTransform = new TranslateTransform(-PowerExpandedWidth, 0);
            BtnSleep.RenderTransform = new TranslateTransform(-PowerExpandedWidth, 0);
        }

        void Restart_Click(object sender, RoutedEventArgs e)
        {
            CollapsePowerImmediate();
            DoRestart();
        }

        void Sleep_Click(object sender, RoutedEventArgs e)
        {
            CollapsePowerImmediate();
            HideAnimated();
            DoSleep();
        }

        // ══════════════════════════════════════════════════════════════
        //  Поиск
        // ══════════════════════════════════════════════════════════════

        void Search_Click(object sender, RoutedEventArgs e)
        {
            HideAnimated();
            _ = System.Threading.Tasks.Task.Delay(150).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                        keybd_event(VK_S, 0, 0, UIntPtr.Zero);
                        keybd_event(VK_S, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    }
                    catch { }
                });
            });
        }

        // ══════════════════════════════════════════════════════════════
        //  Плитки
        // ══════════════════════════════════════════════════════════════

        // ══════════════════════════════════════════════════════════════
        //  Авто-обновление папок Programs / Games
        // ══════════════════════════════════════════════════════════════

        // ══════════════════════════════════════════════════════════════
        //  Порядок ярлыков: суффикс .N в имени файла (Discord.1, Chrome.2 …)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Возвращает число из суффикса ".N" в конце имени файла, или -1 если суффикса нет.
        /// Примеры: "Discord.1" → 1, "Chrome.12" → 12, "Telegram" → -1
        /// </summary>
        static int ParseOrderSuffix(string nameWithoutExt)
        {
            if (string.IsNullOrEmpty(nameWithoutExt)) return -1;
            int dot = nameWithoutExt.LastIndexOf('.');
            if (dot < 0 || dot == nameWithoutExt.Length - 1) return -1;
            string suffix = nameWithoutExt.Substring(dot + 1);
            return int.TryParse(suffix, out int n) && n >= 0 ? n : -1;
        }

        /// <summary>
        /// Убирает суффикс ".N" из имени для отображения.
        /// "Discord.1" → "Discord", "Telegram" → "Telegram"
        /// </summary>
        static string StripOrderSuffix(string nameWithoutExt)
        {
            if (string.IsNullOrEmpty(nameWithoutExt)) return nameWithoutExt;
            int dot = nameWithoutExt.LastIndexOf('.');
            if (dot < 0) return nameWithoutExt;
            string suffix = nameWithoutExt.Substring(dot + 1);
            return int.TryParse(suffix, out _) ? nameWithoutExt.Substring(0, dot) : nameWithoutExt;
        }

        FileSystemWatcher StartFolderWatcher(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                var w = new FileSystemWatcher(folder)
                {
                    Filter = "*.*",
                    NotifyFilter = NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                // Дебаунс: перестраиваем меню не чаще раза в 500 мс,
                // чтобы не дёргать UI при копировании нескольких файлов сразу
                System.Threading.Timer debounce = null;
                FileSystemEventHandler handler = (s, e) =>
                {
                    debounce?.Dispose();
                    debounce = new System.Threading.Timer(_ =>
                        Dispatcher.BeginInvoke(new Action(BuildAll)),
                        null, 500, System.Threading.Timeout.Infinite);
                };
                RenamedEventHandler renamedHandler = (s, e) =>
                {
                    debounce?.Dispose();
                    debounce = new System.Threading.Timer(_ =>
                        Dispatcher.BeginInvoke(new Action(BuildAll)),
                        null, 500, System.Threading.Timeout.Infinite);
                };
                w.Created += handler;
                w.Deleted += handler;
                w.Renamed += renamedHandler;
                return w;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MenuWindow] StartFolderWatcher({folder}): {ex.Message}");
                return null;
            }
        }

        void BuildAll()
        {
            _tileItems.Clear();
            FillPanel(PanelPrograms, ProgramsFolder);
            FillPanel(PanelGames, GamesFolder);
            UpdateLayout();
        }

        void FillPanel(UniformGrid panel, string folder)
        {
            panel.Children.Clear();
            if (!Directory.Exists(folder)) return;
            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.lnk")
                    .Concat(Directory.GetFiles(folder, "*.url")).ToArray();
            }
            catch { return; }

            // Сортируем: сначала файлы с суффиксом .N (по номеру), потом остальные по алфавиту
            Array.Sort(files, (a, b) =>
            {
                int na = ParseOrderSuffix(Path.GetFileNameWithoutExtension(a));
                int nb = ParseOrderSuffix(Path.GetFileNameWithoutExtension(b));
                if (na >= 0 && nb >= 0) return na.CompareTo(nb);
                if (na >= 0) return -1;   // у a есть номер — он идёт раньше
                if (nb >= 0) return 1;   // у b есть номер — он идёт раньше
                return StringComparer.OrdinalIgnoreCase.Compare(a, b);
            });

            foreach (string file in files)
            {
                try
                {
                    string rawName = Path.GetFileNameWithoutExtension(file);
                    string name = StripOrderSuffix(rawName);  // убираем .1 .2 .3 из отображаемого имени
                    string resolved = Helpers.IconHelper.ResolveShortcut(file);
                    BitmapSource ico = null;
                    if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                        ico = Helpers.IconHelper.GetIconFromExe(resolved, 36);
                    if (ico == null) ico = ShellIconNoOverlay(file) ?? ShellIconFallback(file);
                    _tileItems.Add((name, file, ico));
                    panel.Children.Add(MakeTile(name, ico, file));
                }
                catch { }
            }
        }

        Button MakeTile(string name, BitmapSource ico, string filePath)
        {
            var sp = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (ico != null)
            {
                var img = new Image
                {
                    Source = ico,
                    Width = TileIconSize,
                    Height = TileIconSize,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                sp.Children.Add(img);
            }
            else
            {
                sp.Children.Add(new TextBlock
                {
                    Text = name.Length > 0 ? name[0].ToString().ToUpper() : "?",
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Width = TileIconSize,
                    TextAlignment = TextAlignment.Center
                });
            }

            var btn = new Button
            {
                Height = TileHeight,
                Margin = new Thickness(TileMargin),
                Cursor = Cursors.Hand,
                ToolTip = name,
                Tag = filePath,
                Content = sp
            };

            var bf = new FrameworkElementFactory(typeof(Border)); bf.Name = "bd";
            bf.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
            bf.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)));
            bf.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bf.SetValue(Border.CornerRadiusProperty, new CornerRadius(0));
            bf.SetValue(Border.SnapsToDevicePixelsProperty, true);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bf.AppendChild(cp);
            var tpl = new ControlTemplate(typeof(Button)) { VisualTree = bf };

            var hov = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hov.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)), "bd"));
            hov.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0xCC, 0xE0, 0xE0, 0xE0)), "bd"));
            hov.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1.5), "bd"));
            tpl.Triggers.Add(hov);
            var prs = new Trigger { Property = Button.IsPressedProperty, Value = true };
            prs.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), "bd"));
            prs.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0)), "bd"));
            prs.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1.5), "bd"));
            tpl.Triggers.Add(prs);
            btn.Template = tpl;

            btn.Click += (s, ev) =>
            {
                try { AppLaunched?.Invoke(); Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); }
                catch (Exception ex)
                {
                    MessageBox.Show("Launch error:\n" + filePath + "\n\n" + ex.Message,
                        "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                HideAnimated();
            };
            return btn;
        }

        // ══════════════════════════════════════════════════════════════
        //  Иконки
        // ══════════════════════════════════════════════════════════════

        static BitmapSource ShellIconNoOverlay(string path)
        {
            try
            {
                var iid = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory factory);
                if (factory == null) return null;
                var flagVariants = new[]
                {
                    (SIIGBF)((int)SIIGBF.IconOnly    | (int)SIIGBF.BiggerSizeOk),
                    (SIIGBF)((int)SIIGBF.IconOnly    | (int)SIIGBF.BiggerSizeOk | (int)SIIGBF.NoOverlay),
                    (SIIGBF)((int)SIIGBF.ResizeToFit | (int)SIIGBF.BiggerSizeOk),
                    (SIIGBF)((int)SIIGBF.ResizeToFit | (int)SIIGBF.BiggerSizeOk | (int)SIIGBF.NoOverlay),
                    SIIGBF.IconOnly, SIIGBF.ResizeToFit,
                };
                foreach (var flags in flagVariants)
                {
                    int hr = factory.GetImage(new SIZE { cx = 48, cy = 48 }, flags, out IntPtr hbm);
                    if (hr != 0 || hbm == IntPtr.Zero) continue;
                    BitmapSource bmp = null;
                    try
                    {
                        bmp = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero,
                            Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        if (bmp != null && bmp.CanFreeze) bmp.Freeze();
                    }
                    finally { DeleteObject(hbm); }
                    if (bmp != null && !IsBlankBitmap(bmp)) return bmp;
                }
                return null;
            }
            catch { return null; }
        }

        static BitmapSource ShellIconFallback(string path)
        {
            try
            {
                var info = new SHFILEINFO();
                IntPtr r = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_LARGEICON);
                if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                {
                    BitmapSource bmp = null;
                    try
                    {
                        bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon,
                            Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        if (bmp != null && bmp.CanFreeze) bmp.Freeze();
                    }
                    finally { DestroyIcon(info.hIcon); }
                    if (bmp != null && !IsBlankBitmap(bmp)) return bmp;
                }
            }
            catch { }
            try
            {
                const uint SHGFI_USEFILEATTRIBUTES = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
                var info2 = new SHFILEINFO();
                IntPtr r2 = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref info2,
                    (uint)Marshal.SizeOf(info2), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
                if (r2 != IntPtr.Zero && info2.hIcon != IntPtr.Zero)
                {
                    BitmapSource bmp = null;
                    try
                    {
                        bmp = Imaging.CreateBitmapSourceFromHIcon(info2.hIcon,
                            Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        if (bmp != null && bmp.CanFreeze) bmp.Freeze();
                    }
                    finally { DestroyIcon(info2.hIcon); }
                    if (bmp != null && !IsBlankBitmap(bmp)) return bmp;
                }
            }
            catch { }
            return null;
        }

        static bool IsBlankBitmap(BitmapSource bmp)
        {
            try
            {
                if (bmp == null || bmp.PixelWidth == 0 || bmp.PixelHeight == 0) return true;
                var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    bmp, PixelFormats.Bgra32, null, 0);
                int w = Math.Min(conv.PixelWidth, 16), h = Math.Min(conv.PixelHeight, 16), stride = w * 4;
                byte[] px = new byte[h * stride];
                conv.CopyPixels(new Int32Rect(0, 0, w, h), px, stride, 0);
                int n = 0;
                for (int i = 0; i < px.Length; i += 4)
                    if (px[i + 3] > 32 && !(px[i + 2] > 240 && px[i + 1] > 240 && px[i] > 240)) n++;
                return n < 5;
            }
            catch { return false; }
        }

        // ══════════════════════════════════════════════════════════════
        //  Хелперы
        // ══════════════════════════════════════════════════════════════

        static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var p = VisualTreeHelper.GetParent(child);
            return p == null ? null : p is T t ? t : FindVisualParent<T>(p);
        }

        static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var c = VisualTreeHelper.GetChild(parent, i);
                if (c is T t) return t;
                var r = FindVisualChild<T>(c); if (r != null) return r;
            }
            return null;
        }

        void Settings_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start("ms-settings:"); } catch { }
            HideAnimated();
        }

        void DoSleep()
        {
            try
            {
                Process.Start(new ProcessStartInfo("rundll32.exe",
                    "powrprof.dll,SetSuspendState 0,1,0")
                { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Sleep error:\n" + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void DoShutdown()
        {
            try
            {
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 0")
                { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Shutdown error:\n" + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void DoRestart()
        {
            try
            {
                Process.Start(new ProcessStartInfo("shutdown", "/r /t 0")
                { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Restart error:\n" + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}