using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyTaskbar.Helpers;

namespace MyTaskbar
{
    /// <summary>
    /// Settings window opened via Shift+Alt+O.
    /// Exposes: taskbar position (top/bottom), UI scale, fullscreen auto-hide toggle,
    /// reveal delay (ms), preview hover delay (ms), and primary monitor selection.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        // ── P/Invoke для перечисления мониторов ──────────────────────────────
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumCb cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr hMon, ref MONIEX lpmi);
        delegate bool MonitorEnumCb(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONIEX
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        struct MonitorInfo { public string DeviceName; public RECT Bounds; public bool IsPrimary; }

        List<MonitorInfo> _monitors = new List<MonitorInfo>();
        // ── Public state ────────────────────────────────────────────────────
        /// <summary>True = taskbar is at the bottom of the screen.</summary>
        public bool IsBottom { get; private set; }

        /// <summary>Current UI scale in [1.0 … 1.5].</summary>
        public double UIScale { get; private set; }

        /// <summary>Whether to auto-hide the taskbar in fullscreen games/apps.</summary>
        public bool FullscreenAutoHide { get; private set; }

        /// <summary>Delay (ms) before hidden taskbar appears when cursor hovers the edge.</summary>
        public int RevealDelayMs { get; private set; }

        /// <summary>Delay (ms) before thumbnail preview appears when hovering a taskbar button.</summary>
        public int PreviewDelayMs { get; private set; }

        /// <summary>Delay (ms) before taskbar auto-hides after cursor leaves it in fullscreen mode.</summary>
        public int HideDelayMs { get; private set; }

        /// <summary>Whether to show taskbar when a fullscreen app requests attention (flashes).</summary>
        public bool AttentionShowEnabled { get; private set; }

        /// <summary>Whether Start menu open/close uses slide animation.</summary>
        public bool MenuAnimEnabled { get; private set; }

        /// <summary>Whether to show the battery indicator on the taskbar.</summary>
        public bool BatteryIndicatorEnabled { get; private set; }

        /// <summary>szDevice of the monitor that should host the main taskbar. Empty = system primary.</summary>
        public string PrimaryMonitorDevice { get; private set; }

        // ── Callbacks raised when the user hits Apply ────────────────────────
        public event Action<bool>   PositionChanged;    // isBottom
        public event Action<double> ScaleChanged;       // newScale
        public event Action<bool>   FullscreenChanged;  // enabled
        public event Action<int>    RevealDelayChanged; // ms
        public event Action<int>    PreviewDelayChanged;// ms
        public event Action<int>    HideDelayChanged;   // ms
        public event Action<bool>   AttentionShowChanged;// enabled
        public event Action<bool>   MenuAnimChanged;     // enabled
        public event Action<bool>   BatteryIndicatorChanged; // enabled
        public event Action<string> PrimaryMonitorChanged; // deviceName

        // ── Internal tracking ────────────────────────────────────────────────
        private bool   _pendingBottom;
        private double _pendingScale;
        private bool   _pendingFullscreen;
        private int    _pendingRevealDelay;
        private int    _pendingPreviewDelay;
        private int    _pendingHideDelay;
        private bool   _pendingAttentionShow;
        private bool   _pendingMenuAnim;
        private bool   _pendingBatteryIndicator;
        private string _pendingPrimaryMonitorDevice;

        private const double ScaleMin  = 1.0;
        private const double ScaleMax  = 1.5;
        private const double ScaleStep = 0.10;

        private const int RevealDelayMin  = 0;
        private const int RevealDelayMax  = 3000;
        private const int RevealDelayStep = 100;

        private const int PreviewDelayMin  = 0;
        private const int PreviewDelayMax  = 2000;
        private const int PreviewDelayStep = 100;

        private const int HideDelayMin  = 100;
        private const int HideDelayMax  = 5000;
        private const int HideDelayStep = 100;

        // ── Constructor ─────────────────────────────────────────────────────
        public SettingsWindow(bool isBottom, double uiScale, bool fullscreenAutoHide,
                              int revealDelayMs = 400, int previewDelayMs = 400, int hideDelayMs = 400,
                              bool attentionShowEnabled = true, string primaryMonitorDevice = "",
                              bool menuAnimEnabled = true, bool batteryIndicatorEnabled = true)
        {
            InitializeComponent();

            IsBottom              = isBottom;
            UIScale               = uiScale;
            FullscreenAutoHide    = fullscreenAutoHide;
            RevealDelayMs         = revealDelayMs;
            PreviewDelayMs        = previewDelayMs;
            HideDelayMs           = hideDelayMs;
            AttentionShowEnabled  = attentionShowEnabled;
            MenuAnimEnabled       = menuAnimEnabled;
            BatteryIndicatorEnabled = batteryIndicatorEnabled;
            PrimaryMonitorDevice  = primaryMonitorDevice;

            _pendingBottom              = isBottom;
            _pendingScale               = uiScale;
            _pendingFullscreen          = fullscreenAutoHide;
            _pendingRevealDelay         = revealDelayMs;
            _pendingPreviewDelay        = previewDelayMs;
            _pendingHideDelay           = hideDelayMs;
            _pendingAttentionShow       = attentionShowEnabled;
            _pendingMenuAnim            = menuAnimEnabled;
            _pendingBatteryIndicator    = batteryIndicatorEnabled;
            _pendingPrimaryMonitorDevice = primaryMonitorDevice;

            // Initialise controls
            RadioBottom.IsChecked          = isBottom;
            RadioTop.IsChecked             = !isBottom;
            ToggleFullscreen.IsChecked     = fullscreenAutoHide;
            ToggleAttentionShow.IsChecked  = attentionShowEnabled;
            ToggleMenuAnim.IsChecked       = menuAnimEnabled;
            ToggleBattery.IsChecked        = batteryIndicatorEnabled;

            RefreshScaleDisplay();
            RefreshRevealDelayDisplay();
            RefreshPreviewDelayDisplay();
            RefreshHideDelayDisplay();

            // Populate monitor list
            Loaded += (s, e) =>
            {
                AcrylicHelper.EnableAcrylic(this, 0x881C1C28);
                RefreshMonitorList();
            };
        }

        // ── Monitor dropdown helpers ─────────────────────────────────────────
        private void RefreshMonitorList()
        {
            _monitors.Clear();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
                {
                    var mi = new MONIEX { cbSize = (uint)Marshal.SizeOf(typeof(MONIEX)) };
                    if (GetMonitorInfoW(hMon, ref mi))
                        _monitors.Add(new MonitorInfo
                        {
                            DeviceName = mi.szDevice ?? "",
                            Bounds     = mi.rcMonitor,
                            IsPrimary  = (mi.dwFlags & 1) != 0
                        });
                    return true;
                }, IntPtr.Zero);

            // Temporarily unsubscribe to avoid firing selection logic while rebuilding
            MonitorComboBox.SelectionChanged -= MonitorComboBox_SelectionChanged;
            MonitorComboBox.Items.Clear();

            int selectedIndex = -1;
            for (int i = 0; i < _monitors.Count; i++)
            {
                var m = _monitors[i];
                int w = m.Bounds.right  - m.Bounds.left;
                int h = m.Bounds.bottom - m.Bounds.top;
                string label = $"Monitor {i + 1}  —  {w}×{h}";
                if (m.IsPrimary) label += "  (system primary)";

                MonitorComboBox.Items.Add(new ComboBoxItem
                {
                    Content = label,
                    Tag     = m.DeviceName
                });

                bool isSelected = string.IsNullOrEmpty(_pendingPrimaryMonitorDevice)
                    ? m.IsPrimary
                    : string.Equals(m.DeviceName, _pendingPrimaryMonitorDevice, StringComparison.OrdinalIgnoreCase);

                if (isSelected) selectedIndex = i;
            }

            // Fallback: select system primary if nothing matched
            if (selectedIndex < 0)
                for (int i = 0; i < _monitors.Count; i++)
                    if (_monitors[i].IsPrimary) { selectedIndex = i; break; }

            MonitorComboBox.SelectedIndex = selectedIndex;
            MonitorComboBox.SelectionChanged += MonitorComboBox_SelectionChanged;
        }

        private void MonitorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MonitorComboBox.SelectedIndex < 0 || MonitorComboBox.SelectedIndex >= _monitors.Count)
                return;

            var m = _monitors[MonitorComboBox.SelectedIndex];
            // "" means "use system primary"; otherwise store the device name
            _pendingPrimaryMonitorDevice = m.IsPrimary ? "" : m.DeviceName;
        }

        private void BtnRefreshMonitors_Click(object sender, RoutedEventArgs e)
        {
            RefreshMonitorList();
        }
        private void RefreshScaleDisplay()
        {
            TxtScale.Text = $"{(int)Math.Round(_pendingScale * 100)} %";

            // Fill the scale bar proportionally
            double fraction = (_pendingScale - ScaleMin) / (ScaleMax - ScaleMin);
            double maxWidth = 332;
            ScaleBar.Width = Math.Max(6, fraction * maxWidth);

            BtnScaleDown.IsEnabled = _pendingScale > ScaleMin + 0.001;
            BtnScaleUp.IsEnabled   = _pendingScale < ScaleMax - 0.001;
        }

        // ── Reveal delay helpers ─────────────────────────────────────────────
        private void RefreshRevealDelayDisplay()
        {
            TxtRevealDelay.Text = $"{_pendingRevealDelay} ms";

            double fraction = (double)(_pendingRevealDelay - RevealDelayMin) / (RevealDelayMax - RevealDelayMin);
            double maxWidth = 332;
            RevealDelayBar.Width = Math.Max(6, fraction * maxWidth);

            BtnRevealDelayDown.IsEnabled = _pendingRevealDelay > RevealDelayMin;
            BtnRevealDelayUp.IsEnabled   = _pendingRevealDelay < RevealDelayMax;
        }

        // ── Preview delay helpers ─────────────────────────────────────────────
        private void RefreshPreviewDelayDisplay()
        {
            TxtPreviewDelay.Text = $"{_pendingPreviewDelay} ms";

            double fraction = (double)(_pendingPreviewDelay - PreviewDelayMin) / (PreviewDelayMax - PreviewDelayMin);
            double maxWidth = 332;
            PreviewDelayBar.Width = Math.Max(6, fraction * maxWidth);

            BtnPreviewDelayDown.IsEnabled = _pendingPreviewDelay > PreviewDelayMin;
            BtnPreviewDelayUp.IsEnabled   = _pendingPreviewDelay < PreviewDelayMax;
        }

        // ── Hide delay helpers ────────────────────────────────────────────────
        private void RefreshHideDelayDisplay()
        {
            TxtHideDelay.Text = $"{_pendingHideDelay} ms";

            double fraction = (double)(_pendingHideDelay - HideDelayMin) / (HideDelayMax - HideDelayMin);
            double maxWidth = 332;
            HideDelayBar.Width = Math.Max(6, fraction * maxWidth);

            BtnHideDelayDown.IsEnabled = _pendingHideDelay > HideDelayMin;
            BtnHideDelayUp.IsEnabled   = _pendingHideDelay < HideDelayMax;
        }

        // ── Event handlers ────────────────────────────────────────────────────
        private void RadioTop_Checked(object sender, RoutedEventArgs e)
        {
            _pendingBottom = false;
        }

        private void RadioBottom_Checked(object sender, RoutedEventArgs e)
        {
            _pendingBottom = true;
        }

        private void BtnScaleDown_Click(object sender, RoutedEventArgs e)
        {
            double next = Math.Round(_pendingScale - ScaleStep, 2);
            if (next < ScaleMin) next = ScaleMin;
            _pendingScale = next;
            RefreshScaleDisplay();
        }

        private void BtnScaleUp_Click(object sender, RoutedEventArgs e)
        {
            double next = Math.Round(_pendingScale + ScaleStep, 2);
            if (next > ScaleMax) next = ScaleMax;
            _pendingScale = next;
            RefreshScaleDisplay();
        }

        private void BtnRevealDelayDown_Click(object sender, RoutedEventArgs e)
        {
            _pendingRevealDelay = Math.Max(RevealDelayMin, _pendingRevealDelay - RevealDelayStep);
            RefreshRevealDelayDisplay();
        }

        private void BtnRevealDelayUp_Click(object sender, RoutedEventArgs e)
        {
            _pendingRevealDelay = Math.Min(RevealDelayMax, _pendingRevealDelay + RevealDelayStep);
            RefreshRevealDelayDisplay();
        }

        private void BtnPreviewDelayDown_Click(object sender, RoutedEventArgs e)
        {
            _pendingPreviewDelay = Math.Max(PreviewDelayMin, _pendingPreviewDelay - PreviewDelayStep);
            RefreshPreviewDelayDisplay();
        }

        private void BtnPreviewDelayUp_Click(object sender, RoutedEventArgs e)
        {
            _pendingPreviewDelay = Math.Min(PreviewDelayMax, _pendingPreviewDelay + PreviewDelayStep);
            RefreshPreviewDelayDisplay();
        }

        private void BtnHideDelayDown_Click(object sender, RoutedEventArgs e)
        {
            _pendingHideDelay = Math.Max(HideDelayMin, _pendingHideDelay - HideDelayStep);
            RefreshHideDelayDisplay();
        }

        private void BtnHideDelayUp_Click(object sender, RoutedEventArgs e)
        {
            _pendingHideDelay = Math.Min(HideDelayMax, _pendingHideDelay + HideDelayStep);
            RefreshHideDelayDisplay();
        }

        private void ToggleFullscreen_Changed(object sender, RoutedEventArgs e)
        {
            _pendingFullscreen = ToggleFullscreen.IsChecked == true;
        }

        private void ToggleAttentionShow_Changed(object sender, RoutedEventArgs e)
        {
            _pendingAttentionShow = ToggleAttentionShow.IsChecked == true;
        }

        private void ToggleMenuAnim_Changed(object sender, RoutedEventArgs e)
        {
            _pendingMenuAnim = ToggleMenuAnim.IsChecked == true;
        }

        private void ToggleBattery_Changed(object sender, RoutedEventArgs e)
        {
            _pendingBatteryIndicator = ToggleBattery.IsChecked == true;
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            ApplyChanges();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        private void ApplyChanges()
        {
            bool posChanged        = (_pendingBottom != IsBottom);
            bool scaleChanged      = (Math.Abs(_pendingScale - UIScale) > 0.001);
            bool fsChanged         = (_pendingFullscreen != FullscreenAutoHide);
            bool revealChanged     = (_pendingRevealDelay != RevealDelayMs);
            bool previewChanged    = (_pendingPreviewDelay != PreviewDelayMs);
            bool hideChanged       = (_pendingHideDelay != HideDelayMs);
            bool attentionChanged  = (_pendingAttentionShow != AttentionShowEnabled);
            bool menuAnimChanged   = (_pendingMenuAnim != MenuAnimEnabled);
            bool batteryChanged    = (_pendingBatteryIndicator != BatteryIndicatorEnabled);
            bool monitorChanged    = !string.Equals(_pendingPrimaryMonitorDevice, PrimaryMonitorDevice,
                                                    StringComparison.OrdinalIgnoreCase);

            IsBottom              = _pendingBottom;
            UIScale               = _pendingScale;
            FullscreenAutoHide    = _pendingFullscreen;
            RevealDelayMs         = _pendingRevealDelay;
            PreviewDelayMs        = _pendingPreviewDelay;
            HideDelayMs           = _pendingHideDelay;
            AttentionShowEnabled  = _pendingAttentionShow;
            MenuAnimEnabled       = _pendingMenuAnim;
            BatteryIndicatorEnabled = _pendingBatteryIndicator;
            PrimaryMonitorDevice  = _pendingPrimaryMonitorDevice;

            if (posChanged)       PositionChanged?.Invoke(IsBottom);
            if (scaleChanged)     ScaleChanged?.Invoke(UIScale);
            if (fsChanged)        FullscreenChanged?.Invoke(FullscreenAutoHide);
            if (revealChanged)    RevealDelayChanged?.Invoke(RevealDelayMs);
            if (previewChanged)   PreviewDelayChanged?.Invoke(PreviewDelayMs);
            if (hideChanged)      HideDelayChanged?.Invoke(HideDelayMs);
            if (attentionChanged) AttentionShowChanged?.Invoke(AttentionShowEnabled);
            if (menuAnimChanged)  MenuAnimChanged?.Invoke(MenuAnimEnabled);
            if (batteryChanged)   BatteryIndicatorChanged?.Invoke(BatteryIndicatorEnabled);
            if (monitorChanged)   PrimaryMonitorChanged?.Invoke(PrimaryMonitorDevice);
        }
    }
}
