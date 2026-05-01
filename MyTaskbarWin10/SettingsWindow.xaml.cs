using System;
using System.Windows;
using System.Windows.Media;
using MyTaskbar.Helpers;

namespace MyTaskbar
{
    /// <summary>
    /// Settings window opened via Shift+Alt+O.
    /// Exposes: taskbar position (top/bottom), UI scale, and fullscreen auto-hide toggle.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        // ── Public state ────────────────────────────────────────────────────
        /// <summary>True = taskbar is at the bottom of the screen.</summary>
        public bool IsBottom { get; private set; }

        /// <summary>Current UI scale in [1.0 … 1.5].</summary>
        public double UIScale { get; private set; }

        /// <summary>Whether to auto-hide the taskbar in fullscreen games/apps.</summary>
        public bool FullscreenAutoHide { get; private set; }

        // ── Callbacks raised when the user hits Apply ────────────────────────
        public event Action<bool>   PositionChanged;    // isBottom
        public event Action<double> ScaleChanged;       // newScale
        public event Action<bool>   FullscreenChanged;  // enabled

        // ── Internal tracking ────────────────────────────────────────────────
        private bool   _pendingBottom;
        private double _pendingScale;
        private bool   _pendingFullscreen;

        private const double ScaleMin  = 1.0;
        private const double ScaleMax  = 1.5;
        private const double ScaleStep = 0.10;

        // ── Constructor ─────────────────────────────────────────────────────
        public SettingsWindow(bool isBottom, double uiScale, bool fullscreenAutoHide)
        {
            InitializeComponent();

            IsBottom          = isBottom;
            UIScale           = uiScale;
            FullscreenAutoHide = fullscreenAutoHide;

            _pendingBottom     = isBottom;
            _pendingScale      = uiScale;
            _pendingFullscreen = fullscreenAutoHide;

            // Initialise controls
            RadioBottom.IsChecked    = isBottom;
            RadioTop.IsChecked       = !isBottom;
            ToggleFullscreen.IsChecked = fullscreenAutoHide;

            RefreshScaleDisplay();

            // Apply acrylic blur (like the taskbar)
            Loaded += (s, e) => AcrylicHelper.EnableAcrylic(this, 0x881C1C28);
        }

        // ── Scale helpers ────────────────────────────────────────────────────
        private void RefreshScaleDisplay()
        {
            TxtScale.Text = $"{(int)Math.Round(_pendingScale * 100)} %";

            // Fill the scale bar proportionally
            double fraction = (_pendingScale - ScaleMin) / (ScaleMax - ScaleMin);
            double maxWidth = 332; // approx inner width; bar fills relative to parent via binding-like calc
            ScaleBar.Width = Math.Max(6, fraction * maxWidth);

            BtnScaleDown.IsEnabled = _pendingScale > ScaleMin + 0.001;
            BtnScaleUp.IsEnabled   = _pendingScale < ScaleMax - 0.001;
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

        private void ToggleFullscreen_Changed(object sender, RoutedEventArgs e)
        {
            _pendingFullscreen = ToggleFullscreen.IsChecked == true;
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
            bool posChanged  = (_pendingBottom != IsBottom);
            bool scaleChanged = (Math.Abs(_pendingScale - UIScale) > 0.001);
            bool fsChanged   = (_pendingFullscreen != FullscreenAutoHide);

            IsBottom          = _pendingBottom;
            UIScale           = _pendingScale;
            FullscreenAutoHide = _pendingFullscreen;

            if (posChanged)   PositionChanged?.Invoke(IsBottom);
            if (scaleChanged) ScaleChanged?.Invoke(UIScale);
            if (fsChanged)    FullscreenChanged?.Invoke(FullscreenAutoHide);
        }
    }
}
