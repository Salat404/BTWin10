using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MyTaskbar.Helpers;

namespace MyTaskbar
{
    public partial class CalendarWindow : Window
    {
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }

        delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_RBUTTONDOWN = 0x0204;

        private DateTime _currentDate;
        private IntPtr _mouseHook = IntPtr.Zero;
        private LowLevelMouseProc _mouseProc;

        public CalendarWindow()
        {
            InitializeComponent();
            _currentDate = DateTime.Now;
            PopulateCalendar();
            
            _mouseProc = MouseHookCallback;
            
            // Обработчики кнопок навигации
            PrevButton.Click += (s, e) => 
            {
                e.Handled = true;
                PreviousMonth();
            };
            NextButton.Click += (s, e) => 
            {
                e.Handled = true;
                NextMonth();
            };

            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                ApplyAcrylic();
            };

            // Сбрасываем дату при скрытии
            IsVisibleChanged += (s, e) =>
            {
                if (!(bool)e.NewValue)
                {
                    _currentDate = DateTime.Now;
                    PopulateCalendar();
                }
            };
        }

        void ApplyAcrylic()
        {
            try
            {
                RootBorder.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                AcrylicHelper.EnableAcrylic(this, 0x701A1A2E);
            }
            catch
            {
                try
                {
                    RootBorder.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                    AcrylicHelper.EnableBlur(this, 0x70202030);
                }
                catch
                {
                    RootBorder.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1A, 0x1A, 0x2E));
                }
            }
        }

        // ── Хук мыши ─────────────────────────────────────────────────

        void InstallMouseHook()
        {
            if (_mouseHook != IntPtr.Zero) return;
            using (var proc = Process.GetCurrentProcess())
            using (var mod = proc.MainModule)
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(mod.ModuleName), 0);
        }

        void UninstallMouseHook()
        {
            if (_mouseHook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_LBUTTONDOWN || wParam == (IntPtr)WM_RBUTTONDOWN))
            {
                if (!IsCursorOverSelf())
                    Dispatcher.BeginInvoke(new System.Action(() => { UninstallMouseHook(); Hide(); }));
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        bool IsCursorOverSelf()
        {
            try
            {
                GetCursorPos(out POINT pt);
                var source = PresentationSource.FromVisual(this);
                if (source == null) return false;
                double dpiX = source.CompositionTarget.TransformToDevice.M11;
                double dpiY = source.CompositionTarget.TransformToDevice.M22;
                double l = Left * dpiX, t = Top * dpiY;
                double r = (Left + ActualWidth) * dpiX;
                double b = (Top + ActualHeight) * dpiY;
                return pt.x >= l && pt.x <= r && pt.y >= t && pt.y <= b;
            }
            catch { return false; }
        }

        private void PreviousMonth()
        {
            _currentDate = _currentDate.AddMonths(-1);
            PopulateCalendar();
        }

        private void NextMonth()
        {
            _currentDate = _currentDate.AddMonths(1);
            PopulateCalendar();
        }

        private void PopulateCalendar()
        {
            DateTime firstDay = new DateTime(_currentDate.Year, _currentDate.Month, 1);
            int daysInMonth = DateTime.DaysInMonth(_currentDate.Year, _currentDate.Month);
            
            // Set header
            HeaderText.Text = $"{_currentDate.ToString("MMMM yyyy")}";
            
            // Get first day of week (0 = Sunday, 1 = Monday, ... 6 = Saturday)
            // We want Monday to be first (index 0 in calendar)
            int startDayOfWeek = (int)firstDay.DayOfWeek;
            if (startDayOfWeek == 0) startDayOfWeek = 6; // Convert Sunday to 6
            else startDayOfWeek--; // Convert to Monday = 0
            
            var dayItems = new ObservableCollection<DayItem>();
            
            // Add empty cells for days before month starts
            for (int i = 0; i < startDayOfWeek; i++)
            {
                dayItems.Add(new DayItem 
                { 
                    Day = "",
                    IsEmpty = true,
                    Foreground = new SolidColorBrush(Colors.Transparent) 
                });
            }
            
            // Add days of month
            for (int day = 1; day <= daysInMonth; day++)
            {
                bool isToday = day == DateTime.Now.Day && 
                               _currentDate.Month == DateTime.Now.Month && 
                               _currentDate.Year == DateTime.Now.Year;
                
                var foreground = isToday 
                    ? new SolidColorBrush(Color.FromRgb(100, 180, 255))  // Light blue for today
                    : new SolidColorBrush(Color.FromRgb(220, 220, 220));  // Light gray for other days
                
                dayItems.Add(new DayItem 
                { 
                    Day = day.ToString(),
                    IsEmpty = false,
                    Foreground = foreground,
                    IsToday = isToday
                });
            }
            
            CalendarPanel.ItemsSource = dayItems;
        }

        public void ShowAt(double left, double top, Func<double, double> repositionTop = null)
        {
            Left = left;
            UpdateLayout();
            
            Top = repositionTop != null ? repositionTop(ActualHeight) : top;
            
            Show();
            Activate();
            InstallMouseHook();
        }

        public void ShowAtCentered(double buttonCenterX, double top, Func<double, double> repositionTop = null)
        {
            // Сначала показываем невидимо в оффскрине для получения размеров
            Left = -9999;
            Top = -9999;
            Opacity = 0;
            Show();
            UpdateLayout();
            
            // Теперь у нас есть реальная ширина, центрируем
            double centerLeft = buttonCenterX - ActualWidth / 2;
            Left = Math.Max(0, Math.Min(centerLeft, SystemParameters.PrimaryScreenWidth - ActualWidth));
            
            Top = repositionTop != null ? repositionTop(ActualHeight) : top;
            Opacity = 1;
            
            Activate();
            InstallMouseHook();
        }
    }

    public class DayItem
    {
        public string Day { get; set; }
        public bool IsEmpty { get; set; }
        public bool IsToday { get; set; }
        public Brush Foreground { get; set; }
    }
}
