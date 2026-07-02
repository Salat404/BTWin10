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
// MainWindow.Battery.cs — Battery level tracker
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // БАТАРЕЯ [STAB-6]
        // ═════════════════════════════════════════════════════════════════════
        void StartBatteryTracker()
        {
            UpdateBattery();
            _batteryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _batteryTimer.Tick += (s, e) =>
            {
                // [STAB-6] Флаг реентрантности
                if (System.Threading.Interlocked.CompareExchange(ref _batteryBusyInt, 1, 0) != 0) return;
                try { SafeRun(UpdateBattery, "UpdateBattery"); }
                finally { System.Threading.Interlocked.Exchange(ref _batteryBusyInt, 0); }
            };
            _batteryTimer.Start();
        }

        void UpdateBattery()
        {
            try
            {
                // [BATTERY-TOGGLE] Проверяем, включён ли индикатор батареи в настройках
                if (!_batteryIndicatorEnabled) { if (BatteryButton != null) BatteryButton.Visibility = Visibility.Collapsed; return; }
                
                var status = System.Windows.Forms.SystemInformation.PowerStatus;
                bool noBatt = status.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
                            || status.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.Unknown;
                if (noBatt) { if (BatteryButton != null) BatteryButton.Visibility = Visibility.Collapsed; return; }
                if (BatteryButton != null) BatteryButton.Visibility = Visibility.Visible;
                float raw = status.BatteryLifePercent;
                int pct = float.IsNaN(raw) ? 0 : Math.Max(0, Math.Min(100, (int)Math.Round(raw * 100)));
                bool chg = status.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                if (BatteryLabel != null) { BatteryLabel.Text = pct.ToString(); BatteryLabel.Foreground = Brushes.White; }
                if (BatteryPercent != null) BatteryPercent.Visibility = chg ? Visibility.Collapsed : Visibility.Visible;
                if (BatteryLightning != null) BatteryLightning.Visibility = chg ? Visibility.Visible : Visibility.Collapsed;
                string tl = "";
                if (!chg && status.BatteryLifeRemaining > 0)
                { var ts = TimeSpan.FromSeconds(status.BatteryLifeRemaining); tl = $" · ~{(int)ts.TotalHours}h {ts.Minutes}m"; }
                if (BatteryButton != null) BatteryButton.ToolTip = $"{pct}%{(chg ? " · charging" : "")}{tl}";
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UpdateBattery: {ex.Message}"); if (BatteryButton != null) BatteryButton.Visibility = Visibility.Collapsed; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // TRAY
        // ═════════════════════════════════════════════════════════════════════
        DateTime _trayClosedAt = DateTime.MinValue;
        DateTime _volumeClosedAt = DateTime.MinValue;      // ← добавить
        DateTime _brightnessClosedAt = DateTime.MinValue;  // ← добавить

        void InitTrayWindow()
        {
            if (_trayWindow != null) return;
            _trayWindow = new TrayWindow(); _trayWindow.UIScale = _uiScale;
            // [FIX-2.2] Кэшируем HWND
            _trayWindow.SourceInitialized += (_, _e) => SafeRun(RefreshOwnHwnds, nameof(RefreshOwnHwnds));
            _trayWindow.OnTrayClosed = () =>
            {
                _trayClosedAt = DateTime.UtcNow;
                if (TrayIconClosed != null) TrayIconClosed.Visibility = Visibility.Visible;
                if (TrayIconOpen != null) TrayIconOpen.Visibility = Visibility.Collapsed;
            };
            // Окно всегда видимо (спрятано за верхний край), показываем его сразу
            _trayWindow.Show();
        }

        void TrayButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MarkOwnActivity(); InitTrayWindow();
                if ((DateTime.UtcNow - _trayClosedAt).TotalMilliseconds < 300) return;
                if (_trayWindow.IsOpen)
                {
                    _trayWindow.SlideUp(() => Dispatcher.Invoke(() =>
                    {
                        _trayClosedAt = DateTime.UtcNow;
                        if (TrayIconClosed != null) TrayIconClosed.Visibility = Visibility.Visible;
                        if (TrayIconOpen != null) TrayIconOpen.Visibility = Visibility.Collapsed;
                    }));
                    return;
                }
                var btn = sender as Button; if (btn == null) return;
                var pos = btn.PointToScreen(new System.Windows.Point(0, 0));
                _trayWindow.IsBottom = _isBottom;
                double trayY = _isBottom
                    ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - 2
                    : TASKBAR_HEIGHT + 4;
                _trayWindow.SlideDown(pos.X + btn.ActualWidth / 2, trayY);
                TrayIconClosed.Visibility = Visibility.Collapsed;
                TrayIconOpen.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] TrayButton_Click: {ex.Message}"); }
        }

        // [FIX-SECONDARY-POS] Трей позиционированный на втором мониторе.
        // btnCenterX — центр кнопки в экранных координатах (DIP),
        // monBottom/monTop — границы монитора, taskbarH — высота панели, isBottom — позиция.
        public void OpenTrayFromSecondary(double btnCenterX, double monBottom, double monTop,
                                          double monLeft, double monRight,
                                          double taskbarH, bool isBottom)
        {
            try
            {
                MarkOwnActivity(); InitTrayWindow();
                if ((DateTime.UtcNow - _trayClosedAt).TotalMilliseconds < 300) return;
                if (_trayWindow.IsOpen)
                {
                    _trayWindow.SlideUp(() => Dispatcher.Invoke(() =>
                    {
                        _trayClosedAt = DateTime.UtcNow;
                        if (TrayIconClosed != null) TrayIconClosed.Visibility = Visibility.Visible;
                        if (TrayIconOpen   != null) TrayIconOpen.Visibility   = Visibility.Collapsed;
                    }));
                    return;
                }
                _trayWindow.IsBottom = isBottom;
                // [FIX-SECONDARY-POS] Передаём границы монитора для корректного ограничения позиции
                _trayWindow.MonLeft  = monLeft;
                _trayWindow.MonRight = monRight;
                double trayY = isBottom
                    ? monBottom - taskbarH - 2
                    : monTop    + taskbarH + 4;
                _trayWindow.SlideDown(btnCenterX, trayY);
                if (TrayIconClosed != null) TrayIconClosed.Visibility = Visibility.Collapsed;
                if (TrayIconOpen   != null) TrayIconOpen.Visibility   = Visibility.Visible;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] OpenTrayFromSecondary: {ex.Message}"); }
        }

    }
}
