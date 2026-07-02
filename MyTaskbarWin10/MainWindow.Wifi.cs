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
// MainWindow.Wifi.cs — Wi-Fi icon tracker
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // WIFI
        // ═════════════════════════════════════════════════════════════════════
        void StartWifiIconTracker()
        {
            FetchWifiSignalAsync(); UpdateWifiIcon();
            // [PERF-2] Увеличен интервал с 3 до 10 сек — netsh.exe дорогой, каждые 3 сек избыточно
            _wifiIconTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _wifiIconTimer.Tick += (s, e) => { FetchWifiSignalAsync(); UpdateWifiIcon(); };
            _wifiIconTimer.Start();
        }

        void UpdateWifiIcon()
        {
            try
            {
                if (WifiIcon == null) return;
                var st = GetNetworkStatus();
                WifiIcon.Text = st.Icon;
                WifiIcon.Foreground = st.Active ? Brushes.White : new SolidColorBrush(Color.FromRgb(130, 130, 130));
                if (WifiButton != null)
                {
                    WifiButton.ToolTip = st.Tooltip;
                    // [FIX] Кнопка кликабельна если адаптер присутствует (даже без подключения),
                    // чтобы пользователь мог открыть список сетей и подключиться.
                    // Кнопка отключается только если WiFi-адаптера вообще нет.
                    WifiButton.IsEnabled = st.AdapterPresent;
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UpdateWifiIcon: {ex.Message}"); }
        }

        void FetchWifiSignalAsync()
        {
            if (_wifiSignalPending) return;
            _wifiSignalPending = true;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                int sig = -1;
                try
                {
                    var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.GetEncoding(866) };
                    using (var p = Process.Start(psi))
                    {
                        if (p == null) return;
                        string output = p.StandardOutput.ReadToEnd();
                        // [PERF-2] Сокращён таймаут netsh: 2500->1200ms, чтобы не держать поток
                        if (!p.WaitForExit(1200)) { try { p.Kill(); } catch { } }
                        foreach (var rawLine in output.Split('\n'))
                        {
                            string line = rawLine.Trim();
                            int colon = line.IndexOf(':'); if (colon < 1) continue;
                            string key = line.Substring(0, colon).Trim();
                            string val = line.Substring(colon + 1).Trim();
                            bool isSig = string.Equals(key, "Signal", StringComparison.OrdinalIgnoreCase) || key == "Сигнал"; // "Сигнал" = Russian for Signal
                            if (!isSig) continue;
                            string ns = val.Replace("%", "").Trim();
                            if (int.TryParse(ns, out int pct)) sig = Math.Max(0, Math.Min(100, pct));
                            break;
                        }
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] FetchWifi: {ex.Message}"); }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        _wifiSignalPending = false;
                        if (sig != _cachedWifiSignal) { _cachedWifiSignal = sig; UpdateWifiIcon(); }
                    });
                }
            });
        }

        struct NetworkStatus { public string Icon; public string Tooltip; public bool Active; public bool AdapterPresent; }

        NetworkStatus GetNetworkStatus()
        {
            try
            {
                var ifaces = NetworkInterface.GetAllNetworkInterfaces();

                // Ethernet с реальным IP — высший приоритет
                foreach (var ni in ifaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    bool isEth = ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                              || ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet
                              || ni.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT
                              || ni.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx;
                    if (isEth && HasRealIP(ni))
                        return new NetworkStatus { Icon = "\uE839", Tooltip = $"Ethernet: {ni.Name}", Active = true, AdapterPresent = true };
                }

                // Ищем WiFi-адаптеры (любое состояние — чтобы знать присутствует ли адаптер)
                NetworkInterface wifiConnected = null;   // Up + реальный IP
                NetworkInterface wifiUp = null;          // Up, но нет IP (модем включён, сеть не выбрана)
                NetworkInterface wifiAny = null;         // адаптер есть, но выключен

                foreach (var ni in ifaces)
                {
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    wifiAny = ni; // фиксируем что адаптер вообще есть

                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (wifiUp == null) wifiUp = ni;

                    if (HasRealIP(ni)) { wifiConnected = ni; break; }
                }

                if (wifiConnected != null)
                {
                    // Подключён и есть IP → белая, активная
                    int s = _cachedWifiSignal >= 0 ? _cachedWifiSignal : 50;
                    string ss = _cachedWifiSignal >= 0 ? $"{s}%" : "…";
                    return new NetworkStatus { Icon = SignalToIcon(s), Tooltip = $"Wi-Fi · signal {ss}", Active = true, AdapterPresent = true };
                }
                if (wifiUp != null)
                {
                    // [FIX] Адаптер включён (Up), но нет подключения к сети / нет IP.
                    // Active = false → иконка серая, НО AdapterPresent = true → кнопка кликабельна
                    // (пользователь должен иметь возможность открыть список сетей и подключиться)
                    return new NetworkStatus { Icon = "\uF384", Tooltip = "Wi-Fi: not connected (click to view networks)", Active = false, AdapterPresent = true };
                }
                if (wifiAny != null)
                {
                    // Адаптер физически присутствует, но нет ассоциации с сетью
                    // (точка выключилась, сигнал пропал, ассоциация разорвана).
                    // OperationalStatus = Down/NotPresent здесь означает «нет точки», а не
                    // «адаптер аппаратно выключен» — пользователь должен иметь возможность
                    // открыть список сетей и подключиться к другой.
                    // Исключение: явно Disabled через диспетчер устройств / ncpa.cpl →
                    // в этом случае блокируем кнопку.
                    bool hardDisabled = wifiAny.OperationalStatus == OperationalStatus.NotPresent
                                     || wifiAny.OperationalStatus == OperationalStatus.LowerLayerDown;
                    return new NetworkStatus
                    {
                        Icon = "\uF384",
                        Tooltip = hardDisabled ? "Wi-Fi adapter disabled" : "Wi-Fi: disconnected (click to view networks)",
                        Active = false,
                        AdapterPresent = !hardDisabled   // кликабельна если адаптер жив
                    };
                }

                // Нет вообще никакого WiFi-адаптера
                return new NetworkStatus { Icon = "\uF384", Tooltip = "No network connections", Active = false, AdapterPresent = false };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MyTaskbar] GetNetworkStatus: {ex.Message}");
                return new NetworkStatus { Icon = "\uF384", Tooltip = "Network unavailable", Active = false, AdapterPresent = false };
            }
        }

        bool HasRealIP(NetworkInterface ni)
        {
            try
            {
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    { string ip = a.Address.ToString(); if (!ip.StartsWith("169.254") && ip != "0.0.0.0") return true; }
            }
            catch { }
            return false;
        }

        string SignalToIcon(int s) =>
            s <= 0 ? "\uF384" : s >= 80 ? "\uE701" : s >= 60 ? "\uE872" :
            s >= 40 ? "\uE873" : s >= 20 ? "\uE874" : "\uE875";

        DateTime _wifiClosedAt = DateTime.MinValue;

        void WifiButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MarkOwnActivity();
                if ((DateTime.UtcNow - _wifiClosedAt).TotalMilliseconds < 300) return;
                if (_wifiWindow == null)
                {
                    _wifiWindow = new WifiWindow(); _wifiWindow.UIScale = _uiScale;
                    // [FIX-2.2] Кэшируем HWND
                    _wifiWindow.SourceInitialized += (_, _e) => SafeRun(RefreshOwnHwnds, nameof(RefreshOwnHwnds));
                    _wifiWindow.IsVisibleChanged += (s2, ev) => { if (!(bool)ev.NewValue) { _wifiClosedAt = DateTime.UtcNow; UpdateWifiIcon(); } };
                }
                if (_wifiWindow.IsVisible) { _wifiWindow.Hide(); return; }
                _wifiWindow.IsBottom = _isBottom;
                _wifiWindow.ShowAt(0, TASKBAR_HEIGHT + 2);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] WifiButton_Click: {ex.Message}"); }
        }

        // [FIX-SECONDARY-POS] Открываем WiFi-окно позиционированное на втором мониторе.
        // Параметры: monRight/monBottom/monTop/monWidth — границы второго монитора в DIP,
        // taskbarH — высота панели в DIP, isBottom — панель снизу или сверху.
        public void OpenWifiFromSecondary(double monLeft, double monRight, double monBottom,
                                          double monTop, double taskbarH, bool isBottom)
        {
            try
            {
                MarkOwnActivity();
                if ((DateTime.UtcNow - _wifiClosedAt).TotalMilliseconds < 300) return;
                if (_wifiWindow == null)
                {
                    _wifiWindow = new WifiWindow(); _wifiWindow.UIScale = _uiScale;
                    _wifiWindow.SourceInitialized += (_, _e) => SafeRun(RefreshOwnHwnds, nameof(RefreshOwnHwnds));
                    _wifiWindow.IsVisibleChanged += (s2, ev) =>
                    { if (!(bool)ev.NewValue) { _wifiClosedAt = DateTime.UtcNow; UpdateWifiIcon(); } };
                }
                if (_wifiWindow.IsVisible) { _wifiWindow.Hide(); return; }
                _wifiWindow.IsBottom = isBottom;
                // [FIX-SECONDARY-POS] ShowAtMonitor знает правильные границы монитора
                _wifiWindow.ShowAtMonitor(monLeft, monRight, monTop, monBottom, taskbarH, isBottom);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] OpenWifiFromSecondary: {ex.Message}"); }
        }

    }
}
