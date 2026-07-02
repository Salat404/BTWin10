using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyTaskbar.Helpers;
using IOPath = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

// ═══════════════════════════════════════════════════════════════════
// MainWindow.Media.cs — Brightness and Volume controls
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // BRIGHTNESS
        // ═════════════════════════════════════════════════════════════════════
        void StartBrightnessIconTracker()
        {
            UpdateBrightnessIcon();
            _brightnessIconTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _brightnessIconTimer.Tick += (s, e) => SafeRun(UpdateBrightnessIcon, "UpdateBrightnessIcon");
            _brightnessIconTimer.Start();
        }

        void UpdateBrightnessIcon()
        {
            try
            {
                if (BrightnessButton == null) return;
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    int b = BrightnessHelper.GetBuiltInBrightness();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { if (BrightnessButton != null) BrightnessButton.ToolTip = b >= 0 ? $"Brightness: {b}%" : "Brightness"; } catch { }
                    }));
                });
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UpdateBrightnessIcon: {ex.Message}"); }
        }

        void BrightnessButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MarkOwnActivity();
                if ((DateTime.UtcNow - _brightnessClosedAt).TotalMilliseconds < 300) return;

                if (_brightnessFlyout == null)
                {
                    _brightnessFlyout = new BrightnessFlyoutWindow(); _brightnessFlyout.UIScale = _uiScale;
                    _brightnessFlyout.IsVisibleChanged += (s2, ev) =>
                    {
                        if (!(bool)ev.NewValue)
                        {
                            _brightnessClosedAt = DateTime.UtcNow;
                            UpdateBrightnessIcon();
                        }
                    };
                }

                if (_brightnessFlyout.IsVisible)
                {
                    _brightnessFlyout.Hide();
                    return;
                }

                var btn = BrightnessButton;
                var pos = btn.PointToScreen(new System.Windows.Point(0, 0));
                double bfW = _brightnessFlyout.Width > 0 ? _brightnessFlyout.Width : 220;
                double bfLeft = pos.X + btn.ActualWidth / 2 - bfW / 2;
                double bfTopFallback = _isBottom
                    ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - 80 - 4
                    : TASKBAR_HEIGHT + 4;
                bool isBot = _isBottom;
                _brightnessFlyout.ShowAt(bfLeft, bfTopFallback, actualH =>
                    isBot
                        ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - actualH - 4
                        : TASKBAR_HEIGHT + 4);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] BrightnessButton_Click: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // VOLUME
        // ═════════════════════════════════════════════════════════════════════
        void StartVolumeIconTracker()
        {
            UpdateVolumeIcon();
            _volumeIconTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _volumeIconTimer.Tick += (s, e) => SafeRun(UpdateVolumeIcon, "UpdateVolumeIcon");
            _volumeIconTimer.Start();
        }

        void UpdateVolumeIcon()
        {
            try
            {
                if (VolumeIcon == null) return;
                bool muted = AudioHelper.IsMuted();
                int vol = AudioHelper.GetVolume();
                string icon;
                if (muted || vol == 0) icon = "\uE74F";
                else if (vol < 33) icon = "\uE993";
                else if (vol < 66) icon = "\uE994";
                else icon = "\uE767";
                VolumeIcon.Text = icon;
                if (VolumeButton != null) VolumeButton.ToolTip = muted ? "Muted" : $"Volume: {vol}%";
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UpdateVolumeIcon: {ex.Message}"); }
        }

        // [VOL-RIGHTCLICK] ПКМ на иконке громкости: микшер и настройки звука
        void VolumeButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                MarkOwnActivity();
                var menu = new ContextMenu { Style = TryFindResource("DarkContextMenu") as Style };

                var mixerItem = MakeMenuItem("Открыть микшер громкости");
                mixerItem.Click += (s2, e2) =>
                {
                    try { Process.Start(new ProcessStartInfo("sndvol.exe") { UseShellExecute = true }); }
                    catch { try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); } catch { } }
                };
                menu.Items.Add(mixerItem);

                var soundItem = MakeMenuItem("Звук (настройки устройств)");
                soundItem.Click += (s2, e2) =>
                {
                    try { Process.Start(new ProcessStartInfo("mmsys.cpl") { UseShellExecute = true }); } catch { }
                };
                menu.Items.Add(soundItem);

                var settingsItem = MakeMenuItem("Параметры звука");
                settingsItem.Click += (s2, e2) =>
                {
                    try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); } catch { }
                };
                menu.Items.Add(settingsItem);

                menu.PlacementTarget = VolumeButton;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                menu.IsOpen = true;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] VolumeButton_RightClick: {ex.Message}"); }
        }

        void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MarkOwnActivity();
                if ((DateTime.UtcNow - _volumeClosedAt).TotalMilliseconds < 300) return;

                if (_volumeFlyout == null)
                {
                    _volumeFlyout = new VolumeFlyoutWindow(); _volumeFlyout.UIScale = _uiScale;
                    _volumeFlyout.IsVisibleChanged += (s2, ev) =>
                    {
                        if (!(bool)ev.NewValue)
                        {
                            _volumeClosedAt = DateTime.UtcNow;
                            UpdateVolumeIcon();
                        }
                    };
                    _volumeFlyout.SizeChanged += (s2, ev) =>
                    {
                        if (!_volumeFlyout.IsVisible) return;
                        double h = _volumeFlyout.ActualHeight;
                        if (h <= 0) return;
                        _volumeFlyout.Top = _isBottom
                            ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - h - 4
                            : TASKBAR_HEIGHT + 4;
                    };
                }

                if (_volumeFlyout.IsVisible)
                {
                    _volumeFlyout.Hide();
                    return;
                }

                var btn = VolumeButton;
                var pos = btn.PointToScreen(new System.Windows.Point(0, 0));
                double vfW = _volumeFlyout.Width > 0 ? _volumeFlyout.Width : 220;
                _volumeFlyout.Left = pos.X + btn.ActualWidth / 2 - vfW / 2;
                _volumeFlyout.Top = -9999;
                _volumeFlyout.Show();
                // Пересчитываем Top после завершения layout
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    double h = _volumeFlyout.ActualHeight > 0 ? _volumeFlyout.ActualHeight : 80;
                    _volumeFlyout.Top = _isBottom
                        ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - h - 4
                        : TASKBAR_HEIGHT + 4;
                }), System.Windows.Threading.DispatcherPriority.Render);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] VolumeButton_Click: {ex.Message}"); }
        }

        // [FIX-SECONDARY-POS] Volume flyout позиционированный на втором мониторе.
        public void OpenVolumeFromSecondary(double btnCenterX, double monBottom, double monTop,
                                            double taskbarH, bool isBottom)
        {
            try
            {
                MarkOwnActivity();
                if ((DateTime.UtcNow - _volumeClosedAt).TotalMilliseconds < 300) return;
                if (_volumeFlyout == null)
                {
                    _volumeFlyout = new VolumeFlyoutWindow(); _volumeFlyout.UIScale = _uiScale;
                    _volumeFlyout.IsVisibleChanged += (s2, ev) =>
                    { if (!(bool)ev.NewValue) { _volumeClosedAt = DateTime.UtcNow; UpdateVolumeIcon(); } };
                    _volumeFlyout.SizeChanged += (s2, ev) =>
                    {
                        if (!_volumeFlyout.IsVisible) return;
                        // Перепозиционируем при изменении размера (используем последние сохранённые данные)
                        double h2 = _volumeFlyout.ActualHeight;
                        if (h2 <= 0) return;
                        _volumeFlyout.Top = _volumeFlyout.Tag is bool ib && ib
                            ? (double)(_volumeFlyout.Resources["_secMonBottom"] ?? monBottom) - taskbarH - h2 - 4
                            : (double)(_volumeFlyout.Resources["_secMonTop"]    ?? monTop)    + taskbarH + 4;
                    };
                }
                if (_volumeFlyout.IsVisible) { _volumeFlyout.Hide(); return; }

                // Сохраняем данные монитора для SizeChanged
                _volumeFlyout.Tag = isBottom;
                _volumeFlyout.Resources["_secMonBottom"] = monBottom;
                _volumeFlyout.Resources["_secMonTop"]    = monTop;

                double vfW = _volumeFlyout.Width > 0 ? _volumeFlyout.Width : 220;
                _volumeFlyout.Left = btnCenterX - vfW / 2;
                _volumeFlyout.Top  = -9999;
                _volumeFlyout.Show();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    double h = _volumeFlyout.ActualHeight > 0 ? _volumeFlyout.ActualHeight : 80;
                    _volumeFlyout.Top = isBottom
                        ? monBottom - taskbarH - h - 4
                        : monTop    + taskbarH + 4;
                }), System.Windows.Threading.DispatcherPriority.Render);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] OpenVolumeFromSecondary: {ex.Message}"); }
        }

        // [FIX-SECONDARY-POS] Brightness flyout позиционированный на втором мониторе.
        public void OpenBrightnessFromSecondary(double btnCenterX, double monBottom, double monTop,
                                                double taskbarH, bool isBottom)
        {
            try
            {
                MarkOwnActivity();
                if ((DateTime.UtcNow - _brightnessClosedAt).TotalMilliseconds < 300) return;
                if (_brightnessFlyout == null)
                {
                    _brightnessFlyout = new BrightnessFlyoutWindow(); _brightnessFlyout.UIScale = _uiScale;
                    _brightnessFlyout.IsVisibleChanged += (s2, ev) =>
                    { if (!(bool)ev.NewValue) { _brightnessClosedAt = DateTime.UtcNow; UpdateBrightnessIcon(); } };
                }
                if (_brightnessFlyout.IsVisible) { _brightnessFlyout.Hide(); return; }

                double bfW   = _brightnessFlyout.Width > 0 ? _brightnessFlyout.Width : 220;
                double bfLeft = btnCenterX - bfW / 2;
                double bfTopFallback = isBottom
                    ? monBottom - taskbarH - 80 - 4
                    : monTop    + taskbarH + 4;
                bool ib = isBottom;
                double mb = monBottom, mt = monTop, th = taskbarH;
                _brightnessFlyout.ShowAt(bfLeft, bfTopFallback, actualH =>
                    ib ? mb - th - actualH - 4
                       : mt + th + 4);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] OpenBrightnessFromSecondary: {ex.Message}"); }
        }

    }
}
