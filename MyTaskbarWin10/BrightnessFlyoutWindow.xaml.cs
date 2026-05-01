using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyTaskbar.Helpers;

namespace MyTaskbar
{
    public partial class BrightnessFlyoutWindow : Window
    {
        // [UI-SCALE]
        double _uiScale = 1.0;
        public double UIScale
        {
            get => _uiScale;
            set
            {
                _uiScale = value;
                if (Content is System.Windows.FrameworkElement root)
                    root.LayoutTransform = Math.Abs(value - 1.0) < 0.01
                        ? Transform.Identity
                        : new ScaleTransform(value, value);
            }
        }
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

        readonly Dictionary<int, DispatcherTimer> _debounceTimers = new Dictionary<int, DispatcherTimer>();
        readonly Dictionary<int, int> _pendingValues = new Dictionary<int, int>();
        readonly Dictionary<int, BrightnessHelper.MonitorInfo> _monitors = new Dictionary<int, BrightnessHelper.MonitorInfo>();

        // для блокировки закрытия при перетаскивании
        bool _anySliderDragging = false;

        List<BrightnessHelper.MonitorInfo> _cachedMonitors = null;

        IntPtr _mouseHook = IntPtr.Zero;
        LowLevelMouseProc _mouseProc;

        public BrightnessFlyoutWindow()
        {
            InitializeComponent();
            _mouseProc = MouseHookCallback;

            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                ApplyAcrylic();
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
            if (nCode >= 0 &&
                (wParam == (IntPtr)WM_LBUTTONDOWN || wParam == (IntPtr)WM_RBUTTONDOWN))
            {
                if (!_anySliderDragging && !IsCursorOverSelf())
                    Dispatcher.BeginInvoke(new Action(() => { UninstallMouseHook(); Hide(); }));
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

        // ── Показ ─────────────────────────────────────────────────────

        public void ShowAt(double left, double top, Func<double, double> repositionTop = null)
        {
            _monitors.Clear();

            if (_cachedMonitors != null && _cachedMonitors.Count > 0)
            {
                BuildUI(_cachedMonitors);
                Left = left;
                UpdateLayout();
                Top = repositionTop != null ? repositionTop(ActualHeight) : top;
                Opacity = 1;
                Show();
                Activate();
                InstallMouseHook();
                RefreshCacheInBackground();
                return;
            }

            MonitorStack.Children.Clear();
            Left = left;
            Top = -9999;
            Opacity = 0;
            Show();

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                List<BrightnessHelper.MonitorInfo> list = null;
                try { list = BrightnessHelper.GetMonitors(); }
                catch (Exception ex) { Debug.WriteLine($"[BrightnessFlyout] GetMonitors: {ex.Message}"); }
                list = list ?? new List<BrightnessHelper.MonitorInfo>();

                Dispatcher.Invoke(() =>
                {
                    _cachedMonitors = list;
                    BuildUI(list);
                    UpdateLayout();
                    // Пересчитываем Top по реальной высоте
                    if (repositionTop != null)
                        Top = repositionTop(ActualHeight);
                    else
                        Top = top;
                    Opacity = 1;
                    Activate();
                    InstallMouseHook();
                });
            });
        }

        void RefreshCacheInBackground()
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { var l = BrightnessHelper.GetMonitors(); if (l != null) _cachedMonitors = l; }
                catch { }
            });
        }

        // ── UI ────────────────────────────────────────────────────────

        void BuildUI(List<BrightnessHelper.MonitorInfo> list)
        {
            MonitorStack.Children.Clear();

            if (list == null || list.Count == 0)
            {
                MonitorStack.Children.Add(new TextBlock
                {
                    Text = "Brightness not supported",
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 100, 100)),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(10, 8, 10, 8),
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                _monitors[m.Index] = m;
                AddMonitorRow(m);
                if (i < list.Count - 1)
                    MonitorStack.Children.Add(MakeSeparator());
            }
        }

        static System.Windows.Shapes.Rectangle MakeSeparator() =>
            new System.Windows.Shapes.Rectangle
            {
                Height = 1,
                Fill = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                Margin = new Thickness(10, 3, 10, 3)
            };

        void AddMonitorRow(BrightnessHelper.MonitorInfo monitor)
        {
            var row = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

            // Заголовок
            var header = new Grid { Margin = new Thickness(10, 0, 10, 3) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string displayName = monitor.Name.Length > 24
                ? monitor.Name.Substring(0, 24) + "…"
                : monitor.Name;

            var nameTb = new TextBlock
            {
                Text = displayName,
                Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(nameTb, 0);
            header.Children.Add(nameTb);

            var pctTb = new TextBlock
            {
                Text = monitor.Brightness.ToString(),
                Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 24,
                TextAlignment = TextAlignment.Right
            };
            Grid.SetColumn(pctTb, 1);
            header.Children.Add(pctTb);

            row.Children.Add(header);

            // Кастомный слайдер
            var trackBorder = new Border
            {
                Height = 28,
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                Margin = new Thickness(10, 0, 10, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };

            var trackGrid = new Grid();

            var trackBg = new Border
            {
                Height = 3,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(1.5),
                IsHitTestVisible = false
            };

            var fillBorder = new Border
            {
                Height = 3,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
                CornerRadius = new CornerRadius(1.5, 0, 0, 1.5),
                IsHitTestVisible = false,
                Width = 0
            };

            var thumbBorder = new Border
            {
                Width = 8,
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
                CornerRadius = new CornerRadius(0),
                IsHitTestVisible = false,
                Margin = new Thickness(0)
            };

            trackGrid.Children.Add(trackBg);
            trackGrid.Children.Add(fillBorder);
            trackGrid.Children.Add(thumbBorder);
            trackBorder.Child = trackGrid;
            row.Children.Add(trackBorder);

            MonitorStack.Children.Add(row);

            int monIdx = monitor.Index;
            bool builtIn = monitor.IsBuiltIn;
            IntPtr captHMon = monitor.HMonitor;
            int currentVal = monitor.Brightness;

            void UpdateVisual(int val)
            {
                double w = trackBorder.ActualWidth;
                double ratio = val / 100.0;
                double fillWidth = w * ratio;
                double thumbOffset = (w - 8) * ratio;
                fillBorder.Width = Math.Max(0, fillWidth);
                thumbBorder.Margin = new Thickness(Math.Max(0, thumbOffset), 0, 0, 0);
            }

            void Apply(int val)
            {
                val = Math.Max(0, Math.Min(100, val));
                currentVal = val;
                pctTb.Text = val.ToString();
                UpdateVisual(val);

                _pendingValues[monIdx] = val;

                if (_cachedMonitors != null)
                {
                    var cm = _cachedMonitors.Find(m => m.Index == monIdx);
                    if (cm != null) cm.Brightness = val;
                }

                if (!_debounceTimers.TryGetValue(monIdx, out var dt))
                {
                    dt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
                    int captIdx = monIdx;
                    dt.Tick += (s, e) =>
                    {
                        dt.Stop();
                        if (!_pendingValues.TryGetValue(captIdx, out int v)) return;
                        _ = System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                if (builtIn) BrightnessHelper.SetBuiltInBrightness(v);
                                else BrightnessHelper.SetExternalBrightness(captHMon, v);
                            }
                            catch (Exception ex2) { Debug.WriteLine($"[BrightnessFlyout] Set: {ex2.Message}"); }
                        });
                    };
                    _debounceTimers[monIdx] = dt;
                }
                dt.Stop();
                dt.Start();
            }

            // инициализируем визуал после рендера
            trackBorder.Loaded += (s, e) => UpdateVisual(currentVal);

            // Кастомная обработка мыши
            trackBorder.MouseDown += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                _anySliderDragging = true;
                trackBorder.CaptureMouse();
                double ratio = Math.Max(0, Math.Min(1, e.GetPosition(trackBorder).X / trackBorder.ActualWidth));
                Apply((int)Math.Round(ratio * 100));
                e.Handled = true;
            };

            trackBorder.MouseMove += (s, e) =>
            {
                if (!_anySliderDragging || !trackBorder.IsMouseCaptured) return;
                double ratio = Math.Max(0, Math.Min(1, e.GetPosition(trackBorder).X / trackBorder.ActualWidth));
                Apply((int)Math.Round(ratio * 100));
                e.Handled = true;
            };

            trackBorder.MouseUp += (s, e) =>
            {
                _anySliderDragging = false;
                if (trackBorder.IsMouseCaptured) trackBorder.ReleaseMouseCapture();
            };

            trackBorder.MouseLeave += (s, e) =>
            {
                if (!_anySliderDragging) trackBorder.ReleaseMouseCapture();
            };

            row.MouseWheel += (s, e) =>
            {
                Apply(currentVal + (e.Delta > 0 ? 5 : -5));
                e.Handled = true;
            };
        }

        protected override void OnMouseDown(MouseButtonEventArgs e) { base.OnMouseDown(e); }
    }
}