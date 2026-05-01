using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyTaskbar.Helpers;

namespace MyTaskbar
{
    public class AudioDeviceItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsDefault { get; set; }
        public Visibility IsDefaultVisibility =>
            IsDefault ? Visibility.Visible : Visibility.Hidden;
    }

    public partial class VolumeFlyoutWindow : Window
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
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }

        delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        const int WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_RBUTTONDOWN = 0x0204;

        private readonly DispatcherTimer _refreshTimer;
        private bool _deviceListVisible;
        private bool _switchingDevice;
        private bool _sliderDragging = false;
        private IntPtr _mouseHook = IntPtr.Zero;
        private LowLevelMouseProc _mouseProc;

        public VolumeFlyoutWindow()
        {
            InitializeComponent();

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _refreshTimer.Tick += (s, e) => RefreshFromSystem();

            _mouseProc = MouseHookCallback;

            Loaded += (s, e) =>
            {
                RefreshFromSystem();
                HookSlider();
            };
        }

        // ── Кастомный слайдер ─────────────────────────────────────────

        void HookSlider()
        {
            SliderTrackBorder.MouseDown += SliderMouseDown;
            SliderTrackBorder.MouseMove += SliderMouseMove;
            SliderTrackBorder.MouseUp += SliderMouseUp;
            SliderTrackBorder.MouseLeave += SliderMouseLeave;
        }

        void SliderMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            _sliderDragging = true;
            SliderTrackBorder.CaptureMouse();
            UpdateSliderFromMouse(e.GetPosition(SliderTrackBorder));
            e.Handled = true;
        }

        void SliderMouseMove(object sender, MouseEventArgs e)
        {
            if (!_sliderDragging) return;
            UpdateSliderFromMouse(e.GetPosition(SliderTrackBorder));
            e.Handled = true;
        }

        void SliderMouseUp(object sender, MouseButtonEventArgs e)
        {
            _sliderDragging = false;
            if (SliderTrackBorder.IsMouseCaptured)
                SliderTrackBorder.ReleaseMouseCapture();
        }

        void SliderMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_sliderDragging)
                SliderTrackBorder.ReleaseMouseCapture();
        }

        void UpdateSliderFromMouse(Point pos)
        {
            double w = SliderTrackBorder.ActualWidth;
            if (w <= 0) return;
            double ratio = Math.Max(0, Math.Min(1, pos.X / w));
            int vol = (int)Math.Round(ratio * 100);
            SetVolume(vol);
        }

        void UpdateSliderVisual(int vol)
        {
            double w = SliderTrackBorder.ActualWidth;
            double ratio = vol / 100.0;
            double fillWidth = w * ratio;
            double thumbOffset = (w - 8) * ratio; // двигаем пипку от 0 до (ширина - ширина пипки)
            SliderFill.Width = Math.Max(0, fillWidth);
            SliderThumb.Margin = new Thickness(Math.Max(0, thumbOffset), 0, 0, 0);
        }

        void SetVolume(int vol)
        {
            try
            {
                VolumePercent.Text = vol.ToString();
                AudioHelper.SetVolume(vol);
                MuteIcon.Text = GetVolumeIcon(AudioHelper.IsMuted(), vol);
                UpdateSliderVisual(vol);
            }
            catch (Exception ex) { Debug.WriteLine($"[VolumeFlyout] SetVolume: {ex.Message}"); }
        }

        // ── Хелперы шаблона ──────────────────────────────────────────

        private TextBlock GetDeviceLabel() =>
            DeviceChevronButton.Template.FindName("DeviceLabel", DeviceChevronButton) as TextBlock;

        private TextBlock GetChevronIcon() =>
            DeviceChevronButton.Template.FindName("ChevronIcon", DeviceChevronButton) as TextBlock;

        // ── Глобальный хук мыши ───────────────────────────────────────

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
                if (!_switchingDevice && !_sliderDragging && !IsCursorOverSelf())
                    Dispatcher.BeginInvoke(new Action(Hide));
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // ── Acrylic ───────────────────────────────────────────────────

        void Window_SourceInitialized(object sender, EventArgs e) => ApplyAcrylic();

        void ApplyAcrylic()
        {
            try { AcrylicHelper.EnableAcrylic(this, 0x701A1A2E); }
            catch
            {
                try { AcrylicHelper.EnableBlur(this, 0x70202030); }
                catch
                {
                    if (RootBorder != null)
                        RootBorder.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1A, 0x1A, 0x2E));
                }
            }
        }

        // ── Показ / скрытие ───────────────────────────────────────────

        public new void Show()
        {
            _deviceListVisible = false;
            DeviceList.Visibility = Visibility.Collapsed;
            DeviceListSeparator.Visibility = Visibility.Collapsed;

            var chevron = GetChevronIcon();
            if (chevron != null) chevron.Text = "\uE70D";

            RefreshFromSystem();
            base.Show();
            Activate();
            _refreshTimer.Start();
            InstallMouseHook();
        }

        public new void Hide()
        {
            UninstallMouseHook();
            _refreshTimer.Stop();
            base.Hide();
        }

        // ── Обновление состояния ──────────────────────────────────────

        void RefreshFromSystem()
        {
            try
            {
                bool muted = AudioHelper.IsMuted();
                int vol = AudioHelper.GetVolume();

                VolumePercent.Text = vol.ToString();
                MuteIcon.Text = GetVolumeIcon(muted, vol);
                MuteIcon.Opacity = muted ? 0.45 : 1.0;
                UpdateSliderVisual(vol);

                string name = AudioHelper.GetDefaultDeviceName();
                var label = GetDeviceLabel();
                if (label != null)
                    label.Text = string.IsNullOrEmpty(name) ? "Output device" : name;

                var devices = AudioHelper.GetPlaybackDevices();
                bool hasMultiple = devices.Count > 1;
                DeviceChevronButton.IsEnabled = hasMultiple;

                var chevron = GetChevronIcon();
                if (chevron != null)
                    chevron.Visibility = hasMultiple ? Visibility.Visible : Visibility.Hidden;

                if (!hasMultiple)
                {
                    _deviceListVisible = false;
                    DeviceList.Visibility = Visibility.Collapsed;
                    DeviceListSeparator.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[VolumeFlyout] Refresh: {ex.Message}"); }
        }

        static string GetVolumeIcon(bool muted, int vol)
        {
            if (muted || vol == 0) return "\uE74F";
            if (vol < 33) return "\uE993";
            if (vol < 66) return "\uE994";
            return "\uE767";
        }

        // ── Шеврон ────────────────────────────────────────────────────

        void DeviceChevronButton_Click(object sender, RoutedEventArgs e)
        {
            _deviceListVisible = !_deviceListVisible;
            var chevron = GetChevronIcon();

            if (_deviceListVisible)
            {
                try { BuildDeviceList(AudioHelper.GetPlaybackDevices()); }
                catch (Exception ex) { Debug.WriteLine($"[VolumeFlyout] GetPlaybackDevices: {ex.Message}"); }
                DeviceList.Visibility = Visibility.Visible;
                DeviceListSeparator.Visibility = Visibility.Visible;
                if (chevron != null) chevron.Text = "\uE70E";
            }
            else
            {
                DeviceList.Visibility = Visibility.Collapsed;
                DeviceListSeparator.Visibility = Visibility.Collapsed;
                if (chevron != null) chevron.Text = "\uE70D";
            }
        }

        // ── Список устройств ──────────────────────────────────────────

        void BuildDeviceList(IList<AudioDeviceItem> devices)
        {
            DeviceList.Children.Clear();

            var activeBg = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD7));
            var activeHover = new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x88, 0xE7));
            var activePress = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x60, 0xB0));
            var normalBg = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            var normalHover = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            var normalPress = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

            foreach (var device in devices)
            {
                var deviceId = device.Id;
                bool isDefault = device.IsDefault;

                var nameBlock = new TextBlock
                {
                    Text = device.Name,
                    Foreground = isDefault
                        ? Brushes.White
                        : new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var border = new Border
                {
                    Background = isDefault ? activeBg : normalBg,
                    Padding = new Thickness(10, 6, 10, 6),
                    Cursor = Cursors.Hand,
                    Child = nameBlock
                };

                if (isDefault)
                {
                    border.MouseEnter += (s, e) => border.Background = activeHover;
                    border.MouseLeave += (s, e) => border.Background = activeBg;
                    border.MouseDown += (s, e) => border.Background = activePress;
                    border.MouseLeftButtonUp += (s, e) => { border.Background = activeHover; OnDeviceSelected(deviceId); };
                }
                else
                {
                    border.MouseEnter += (s, e) => border.Background = normalHover;
                    border.MouseLeave += (s, e) => border.Background = normalBg;
                    border.MouseDown += (s, e) => border.Background = normalPress;
                    border.MouseLeftButtonUp += (s, e) => { border.Background = normalHover; OnDeviceSelected(deviceId); };
                }

                DeviceList.Children.Add(border);
            }
        }

        // ── Выбор устройства ─────────────────────────────────────────

        void OnDeviceSelected(string deviceId)
        {
            _deviceListVisible = false;
            DeviceList.Visibility = Visibility.Collapsed;
            DeviceListSeparator.Visibility = Visibility.Collapsed;
            var chevron = GetChevronIcon();
            if (chevron != null) chevron.Text = "\uE70D";

            _switchingDevice = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    bool ok = AudioHelper.SetDefaultDevice(deviceId);
                    if (!ok) Debug.WriteLine("[VolumeFlyout] SetDefaultDevice returned false");
                }
                catch (Exception ex) { Debug.WriteLine($"[VolumeFlyout] OnDeviceSelected: {ex.Message}"); }

                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                timer.Tick += (s2, e2) => { timer.Stop(); _switchingDevice = false; RefreshFromSystem(); };
                timer.Start();
            }), DispatcherPriority.Background);
        }

        // ── Mute ─────────────────────────────────────────────────────

        void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            try { AudioHelper.ToggleMute(); RefreshFromSystem(); }
            catch (Exception ex) { Debug.WriteLine($"[VolumeFlyout] Mute: {ex.Message}"); }
        }

        // ── Курсор над окном ──────────────────────────────────────────

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

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { }
    }
}