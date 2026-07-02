using System;
using System.Collections.ObjectModel;
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

        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;

        private DateTime _currentDate;

        public CalendarWindow()
        {
            InitializeComponent();
            _currentDate = DateTime.Now;
            PopulateCalendar();
            
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

            // Закрываем окно при потере фокуса и сбрасываем дату
            Deactivated += (s, e) => 
            {
                _currentDate = DateTime.Now;
                PopulateCalendar();
                Hide();
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
            Top = top;
            Show();
            
            UpdateLayout();
            
            if (repositionTop != null)
            {
                Top = repositionTop(ActualHeight);
            }
            
            Activate();
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
