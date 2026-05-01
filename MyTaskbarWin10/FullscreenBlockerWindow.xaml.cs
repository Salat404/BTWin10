using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace MyTaskbar
{
    // ═════════════════════════════════════════════════════════════════════════
    // [GAME] FullscreenBlockerWindow  (v6 — блокер + дырки, без ClipCursor при открытой панели)
    //
    // Логика:
    //   • Блокер покрывает весь экран, Topmost=true
    //   • HTTRANSPARENT пробивает дырки для тулбара И меню Пуск
    //   • ClipCursor НЕ используется пока панель видима — курсор свободен
    //     по всем мониторам, чтобы пользователь мог управлять вторым экраном
    //   • При скрытии блокера ClipCursor снимается (IntPtr.Zero)
    //   • UpdateClipZone() оставлен для обратной совместимости (ничего не делает)
    // ═════════════════════════════════════════════════════════════════════════
    public partial class FullscreenBlockerWindow : Window
    {
        [DllImport("user32.dll")] static extern bool ClipCursor(ref RECT lpRect);
        [DllImport("user32.dll")] static extern bool ClipCursor(IntPtr lpRect);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;

        /// <summary>
        /// Вызывается при клике вне тулбара и меню.
        /// Передаёт экранные координаты курсора — чтобы MainWindow мог определить
        /// какое окно находится под курсором и передать фокус ему, а не игре.
        /// </summary>
        public event Action<int, int> DismissRequested;

        /// <summary>Тулбар — дырка №1 в блокере.</summary>
        public Window TaskbarWindow { get; set; }

        /// <summary>Меню Пуск — дырка №2 в блокере (если открыто).</summary>
        public Window MenuWindow { get; set; }

        bool _isActive = false;
        public bool IsBlockerActive => _isActive;

        public void ReleaseClip()
        {
            try { ClipCursor(IntPtr.Zero); }
            catch (Exception ex) { Debug.WriteLine($"[Blocker] ReleaseClip: {ex.Message}"); }
        }

        public FullscreenBlockerWindow()
        {
            InitializeComponent();

            SourceInitialized += (s, e) =>
            {
                try
                {
                    var helper = new WindowInteropHelper(this);
                    int ex = GetWindowLong(helper.Handle, GWL_EXSTYLE);
                    SetWindowLong(helper.Handle, GWL_EXSTYLE,
                        ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                    HwndSource.FromHwnd(helper.Handle)?.AddHook(WndProc);
                }
                catch (Exception ex2) { Debug.WriteLine($"[Blocker] Init: {ex2.Message}"); }
            };
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_NCHITTEST)
            {
                int screenX = (short)(lParam.ToInt32() & 0xFFFF);
                int screenY = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

                // Дырка для тулбара
                if (IsOverWindow(TaskbarWindow, screenX, screenY))
                {
                    handled = true;
                    return (IntPtr)HTTRANSPARENT;
                }
                // Дырка для меню Пуск (когда оно открыто)
                if (IsOverWindow(MenuWindow, screenX, screenY))
                {
                    handled = true;
                    return (IntPtr)HTTRANSPARENT;
                }
            }
            return IntPtr.Zero;
        }

        bool IsOverWindow(Window w, int screenX, int screenY)
        {
            if (w == null || !w.IsVisible) return false;
            try
            {
                var src = PresentationSource.FromVisual(w);
                double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

                double left = w.Left * dpi;
                double top = w.Top * dpi;
                double right = left + w.ActualWidth * dpi;
                double bottom = top + w.ActualHeight * dpi;

                return screenX >= left && screenX <= right
                    && screenY >= top && screenY <= bottom;
            }
            catch { return false; }
        }

        /// <summary>
        /// Ранее пересчитывал ClipCursor. Теперь не ограничивает курсор —
        /// оставлен для обратной совместимости с вызовами в MainWindow.
        /// Курсор свободен пока панель открыта (чтобы работал второй монитор).
        /// </summary>
        public void UpdateClipZone()
        {
            // Намеренно пусто: ClipCursor не вызываем пока блокер активен.
            // Блокер физически перекрывает игровой экран — этого достаточно.
            Debug.WriteLine("[Blocker] UpdateClipZone: skipped (multi-monitor mode)");
        }

        RECT GetClipRect()
        {
            // Ограничиваем курсор ВСЕМ экраном — блокер сам физически покрывает
            // весь экран и не даёт FPS-игре захватить курсор через её собственный
            // ClipCursor. Курсор при этом свободно ходит по всему экрану (по блокеру),
            // а дырки HTTRANSPARENT пропускают клики только в тулбар и меню.
            return new RECT
            {
                left = 0,
                top = 0,
                right = (int)SystemParameters.PrimaryScreenWidth,
                bottom = (int)SystemParameters.PrimaryScreenHeight
            };
        }

        RECT GetWinRect(Window w)
        {
            if (w == null)
                return new RECT
                {
                    left = 0,
                    top = 0,
                    right = (int)SystemParameters.PrimaryScreenWidth,
                    bottom = (int)SystemParameters.PrimaryScreenHeight
                };
            try
            {
                var src = PresentationSource.FromVisual(w);
                double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                return new RECT
                {
                    left = (int)(w.Left * dpi),
                    top = (int)(w.Top * dpi),
                    right = (int)((w.Left + w.ActualWidth) * dpi),
                    bottom = (int)((w.Top + w.ActualHeight) * dpi)
                };
            }
            catch
            {
                return new RECT
                {
                    left = 0,
                    top = 0,
                    right = (int)SystemParameters.PrimaryScreenWidth,
                    bottom = (int)SystemParameters.PrimaryScreenHeight
                };
            }
        }

        public void ShowBlocker()
        {
            try
            {
                Left = 0;
                Top = 0;
                Width = SystemParameters.PrimaryScreenWidth;
                Height = SystemParameters.PrimaryScreenHeight;
                Topmost = true;
                if (!IsVisible) Show();

                _isActive = true;
                // Не вызываем ClipCursor — блокер физически перекрывает игровой экран,
                // и курсор может свободно уходить на второй монитор.
                ClipCursor(IntPtr.Zero);
            }
            catch (Exception ex) { Debug.WriteLine($"[Blocker] ShowBlocker: {ex.Message}"); }
        }

        public void HideBlocker()
        {
            try
            {
                _isActive = false;
                ClipCursor(IntPtr.Zero);
                if (IsVisible) Hide();
            }
            catch (Exception ex) { Debug.WriteLine($"[Blocker] HideBlocker: {ex.Message}"); }
        }

        // Срабатывает только вне тулбара и меню (там HTTRANSPARENT — события не доходят)
        private void Grid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Передаём экранные координаты клика чтобы MainWindow мог решить
                // куда передать фокус — в игру или в окно под курсором
                var pt = e.GetPosition(null);
                var src = PresentationSource.FromVisual(this);
                double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                int screenX = (int)(pt.X * dpi) + (int)(Left * dpi);
                int screenY = (int)(pt.Y * dpi) + (int)(Top * dpi);
                DismissRequested?.Invoke(screenX, screenY);
                e.Handled = true;
            }
            catch (Exception ex) { Debug.WriteLine($"[Blocker] MouseDown: {ex.Message}"); }
        }
    }
}