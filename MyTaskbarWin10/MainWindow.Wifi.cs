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
                if (WifiButton != null) WifiButton.ToolTip = st.Tooltip;
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

        struct NetworkStatus { public string Icon; public string Tooltip; public bool Active; }

        NetworkStatus GetNetworkStatus()
        {
            try
            {
                var ifaces = NetworkInterface.GetAllNetworkInterfaces();
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
                        return new NetworkStatus { Icon = "\uE839", Tooltip = $"Ethernet: {ni.Name}", Active = true };
                }
                foreach (var ni in ifaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                    if (HasRealIP(ni))
                    {
                        int s = _cachedWifiSignal >= 0 ? _cachedWifiSignal : 50;
                        string ss = _cachedWifiSignal >= 0 ? $"{s}%" : "…";
                        return new NetworkStatus { Icon = SignalToIcon(s), Tooltip = $"Wi-Fi · signal {ss}", Active = true };
                    }
                    return new NetworkStatus { Icon = "\uF384", Tooltip = "No Internet connection", Active = false };
                }
                return new NetworkStatus { Icon = "\uF384", Tooltip = "No network connections", Active = false };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MyTaskbar] GetNetworkStatus: {ex.Message}");
                return new NetworkStatus { Icon = "\uF384", Tooltip = "Network unavailable", Active = false };
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
                    _wifiWindow.IsVisibleChanged += (s2, ev) => { if (!(bool)ev.NewValue) { _wifiClosedAt = DateTime.UtcNow; UpdateWifiIcon(); } };
                }
                if (_wifiWindow.IsVisible) { _wifiWindow.Hide(); return; }
                _wifiWindow.IsBottom = _isBottom;
                _wifiWindow.ShowAt(0, TASKBAR_HEIGHT + 2);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] WifiButton_Click: {ex.Message}"); }
        }

    }
}
