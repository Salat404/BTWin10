using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Radios;

namespace MyTaskbar
{
    public partial class WifiWindow : Window
    {
        // Устанавливается из MainWindow перед ShowAt()
        public bool IsBottom { get; set; } = false;
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

        // ════════════════════════════════════════════════════════════════
        //  WlanAPI P/Invoke
        // ════════════════════════════════════════════════════════════════

        [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint v, IntPtr r, out uint neg, out IntPtr h);
        [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr h, IntPtr r);
        [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr h, IntPtr r, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern uint WlanScan(IntPtr h, ref Guid g, IntPtr s, IntPtr ie, IntPtr r);
        [DllImport("wlanapi.dll")] static extern uint WlanGetAvailableNetworkList(IntPtr h, ref Guid g, uint f, IntPtr r, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern uint WlanConnect(IntPtr h, ref Guid g, ref WLAN_CONNECTION_PARAMETERS p, IntPtr r);
        [DllImport("wlanapi.dll")] static extern uint WlanDisconnect(IntPtr h, ref Guid g, IntPtr r);
        [DllImport("wlanapi.dll")] static extern uint WlanDeleteProfile(IntPtr h, ref Guid g, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr r);
        [DllImport("wlanapi.dll")] static extern uint WlanSetProfile(IntPtr h, ref Guid g, uint f, [MarshalAs(UnmanagedType.LPWStr)] string xml, [MarshalAs(UnmanagedType.LPWStr)] string sec, bool ov, IntPtr r, out uint rc);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);
        [DllImport("wlanapi.dll")] static extern uint WlanSetInterface(IntPtr h, ref Guid g, uint opCode, uint dataSize, IntPtr pData, IntPtr r);

        // ── Структуры ──────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WLAN_INTERFACE_INFO
        {
            public Guid Guid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Desc;
            public int State;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WLAN_INTERFACE_INFO_LIST
        {
            public uint Count;
            public uint Index;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DOT11_SSID
        {
            public uint Len;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] SSID;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        struct WLAN_AVAILABLE_NETWORK
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
            public DOT11_SSID Ssid;
            public uint BssType;
            public uint BssidCount;
            public int Connectable;
            public uint NotConnectableReason;
            public uint PhyCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] PhyTypes;
            public int MorePhy;
            public uint Signal;
            public int Secured;
            public uint AuthAlgo;
            public uint CipherAlgo;
            public uint Flags;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WLAN_CONNECTION_PARAMETERS
        {
            public uint Mode;
            [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
            public IntPtr Ssid;
            public IntPtr BssidList;
            public uint BssType;
            public uint Flags;
        }

        // ════════════════════════════════════════════════════════════════
        //  Модель сети
        // ════════════════════════════════════════════════════════════════

        class Net
        {
            public string SSID;
            public string ProfileName;
            public int Signal;
            public bool Secured;
            public bool Connected;
            public bool HasProfile => !string.IsNullOrEmpty(ProfileName);
        }

        // ════════════════════════════════════════════════════════════════
        //  Состояние
        // ════════════════════════════════════════════════════════════════

        IntPtr _wlan = IntPtr.Zero;
        Guid _iface = Guid.Empty;

        bool _wifiOn = false;
        bool _airplane = false;
        bool _adapterToggling = false;
        bool _connecting = false;

        DispatcherTimer _bgTimer;
        DispatcherTimer _refresh;

        List<Net> _cachedNets = new List<Net>();
        DateTime _cacheTime = DateTime.MinValue;
        bool _bgScanning = false;

        List<Net> _nets = new List<Net>();
        Border _panel;
        string _panelSsid;
        string _cachedAdapterName = null;

        Dictionary<string, bool> _autoConnectCache = new Dictionary<string, bool>(StringComparer.Ordinal);

        public int ConnectedSignal { get; private set; } = -1;

        // ════════════════════════════════════════════════════════════════
        //  Инициализация
        // ════════════════════════════════════════════════════════════════

        public WifiWindow()
        {
            InitializeComponent();
            SourceInitialized += (s, e) => ApplyAcrylic();

            try
            {
                uint ver;
                if (WlanOpenHandle(2, IntPtr.Zero, out ver, out _wlan) != 0)
                { _wlan = IntPtr.Zero; return; }

                IntPtr ifList;
                if (WlanEnumInterfaces(_wlan, IntPtr.Zero, out ifList) != 0) return;
                try
                {
                    var hdr = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(ifList);
                    if (hdr.Count == 0) return;
                    var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(
                        new IntPtr(ifList.ToInt64() + Marshal.SizeOf<WLAN_INTERFACE_INFO_LIST>()));
                    _iface = info.Guid;
                    _cachedAdapterName = info.Desc?.Trim();
                    Debug.WriteLine($"[WifiWindow] Adapter: '{_cachedAdapterName}', GUID={_iface}");
                }
                finally { WlanFreeMemory(ifList); }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WifiWindow] Init error: {ex.Message}");
                _wlan = IntPtr.Zero;
            }

            StartBackgroundScanner();
        }

        void ApplyAcrylic()
        {
            try { MyTaskbar.Helpers.AcrylicHelper.EnableAcrylic(this, 0x701A1A2E); }
            catch
            {
                try { MyTaskbar.Helpers.AcrylicHelper.EnableBlur(this, 0x70202030); }
                catch
                {
                    if (RootBorder != null)
                        RootBorder.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1A, 0x1A, 0x2E));
                }
            }
        }

        void StartBackgroundScanner()
        {
            BgScan();
            _bgTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _bgTimer.Tick += (s, e) => BgScan(onlyIfVisible: true);
            _bgTimer.Start();
        }

        void BgScan() => BgScan(onlyIfVisible: false);

        void BgScan(bool onlyIfVisible)
        {
            if (_bgScanning || _adapterToggling || _connecting || _wlan == IntPtr.Zero || _iface == Guid.Empty) return;
            if (onlyIfVisible && !IsVisible) return;
            _bgScanning = true;

            _ = Task.Run(() =>
            {
                try
                {
                    var g = _iface;
                    uint scanResult = WlanScan(_wlan, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    Debug.WriteLine($"[WifiWindow] WlanScan result={scanResult}");

                    Thread.Sleep(4000);

                    if (_adapterToggling) return;

                    var nets = LoadNets();
                    _cachedNets = nets;
                    _cacheTime = DateTime.Now;

                    var con = nets.Find(n => n.Connected);
                    ConnectedSignal = con?.Signal ?? -1;

                    Dispatcher.Invoke(() =>
                    {
                        if (IsVisible && !_adapterToggling)
                            Rebuild(nets);
                    });
                }
                catch (Exception ex) { Debug.WriteLine($"[WifiWindow] BgScan: {ex.Message}"); }
                finally { _bgScanning = false; }
            });
        }

        public void ShowAt(double unused, double unused2)
        {
            _wifiOn = AdapterOn();
            SetWifiTile(_wifiOn);

            if (_wifiOn)
            {
                if (_cachedNets.Count > 0)
                {
                    Rebuild(_cachedNets);
                    StatusLabel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    Status("Scanning…", "#888888");
                }
            }
            else
            {
                OffState();
            }

            double screenH = SystemParameters.PrimaryScreenHeight;
            Height = screenH / 2.0;

            Visibility = Visibility.Visible;
            UpdateLayout();

            double screenW = SystemParameters.PrimaryScreenWidth;
            double winW = ActualWidth > 0 ? ActualWidth : Width;
            Left = screenW - winW;

            if (IsBottom)
            {
                // Панель внизу — прижимаем меню вплотную над ней
                Top = screenH; // временно за экраном
                RootBorder.BorderThickness = new Thickness(0, 1, 0, 0); // сверху
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    InvalidateMeasure();
                    InvalidateArrange();
                    UpdateLayout();
                    double h = ActualHeight > 0 ? ActualHeight : Height;
                    // WorkArea.Bottom = верхний край нашей панели (панель зарезервировала низ)
                    double panelTop = SystemParameters.WorkArea.Bottom;
                    Top = panelTop - h;
                    Activate();
                }));
            }
            else
            {
                // Панель вверху — оригинальное поведение
                RootBorder.BorderThickness = new Thickness(0, 0, 0, 1); // снизу
                Top = 0;
                Activate();
            }

            _refresh?.Stop();
            _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _refresh.Tick += (s, e) =>
            {
                if (_adapterToggling || _connecting) return;
                bool real = AdapterOn();
                if (real != _wifiOn)
                {
                    _wifiOn = real;
                    SetWifiTile(real);
                    if (!real) { ConnectedSignal = -1; OffState(); return; }
                }
                if (real) BgScan();
            };
            _refresh.Start();

            if (_wifiOn && (DateTime.Now - _cacheTime).TotalSeconds > 10)
                BgScan();
        }

        bool AdapterOn()
        {
            if (_wlan == IntPtr.Zero) return false;
            IntPtr l = IntPtr.Zero;
            try
            {
                if (WlanEnumInterfaces(_wlan, IntPtr.Zero, out l) != 0) return false;
                var h = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(l);
                if (h.Count == 0) return false;
                var i = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(
                    new IntPtr(l.ToInt64() + Marshal.SizeOf<WLAN_INTERFACE_INFO_LIST>()));
                Debug.WriteLine($"[WifiWindow] AdapterOn: State={i.State}");

                // Состояния WLAN_INTERFACE_STATE:
                //  0 = not_ready      — адаптер не готов / выключен
                //  1 = connected      — подключён        ← включён
                //  2 = ad_hoc         — ad-hoc           ← включён
                //  3 = disconnecting  — отключается      ← включён
                //  4 = disconnected   — радио выключено WinRT/airplane mode ← ВЫКЛЮЧЕН
                //  5 = associating    — подключается     ← включён
                //  6 = discovering    — сканирует        ← включён
                //  7 = authenticating — аутентификация   ← включён
                //
                // State=4 после WinRT SetState(Off) — это "радио выключено",
                // возвращаем false чтобы тайл стал серым.
                return i.State != 0 && i.State != 4;
            }
            catch { return false; }
            finally { if (l != IntPtr.Zero) WlanFreeMemory(l); }
        }

        List<Net> LoadNets()
        {
            if (_wlan == IntPtr.Zero || _iface == Guid.Empty) return new List<Net>();

            IntPtr p = IntPtr.Zero;
            var g = _iface;

            uint getResult = WlanGetAvailableNetworkList(_wlan, ref g, 0, IntPtr.Zero, out p);
            Debug.WriteLine($"[WifiWindow] WlanGetAvailableNetworkList result={getResult}, ptr={p}");

            if (getResult != 0 || p == IntPtr.Zero)
                return new List<Net>();

            var dict = new Dictionary<string, Net>(StringComparer.Ordinal);

            try
            {
                uint cnt = (uint)Marshal.ReadInt32(p, 0);
                int sz = Marshal.SizeOf<WLAN_AVAILABLE_NETWORK>();
                string conSsid = ConnectedSsid();

                Debug.WriteLine($"[WifiWindow] LoadNets: total entries = {cnt}, ConnectedSsid='{conSsid}'");

                for (int i = 0; i < cnt; i++)
                {
                    try
                    {
                        var n = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK>(
                            new IntPtr(p.ToInt64() + 8 + i * sz));

                        int rawLen = n.Ssid.Len > 0 ? Math.Min((int)n.Ssid.Len, 32) : 0;
                        string rawHex = rawLen > 0
                            ? BitConverter.ToString(n.Ssid.SSID, 0, rawLen)
                            : "(empty)";
                        Debug.WriteLine($"[WifiWindow] [{i}] RAW SSID: len={n.Ssid.Len} hex={rawHex} | Profile='{n.ProfileName}' Signal={n.Signal}");

                        string ssid = "";
                        if (n.Ssid.Len > 0 && n.Ssid.SSID != null)
                        {
                            int len = Math.Min((int)n.Ssid.Len, 32);
                            try { ssid = Encoding.UTF8.GetString(n.Ssid.SSID, 0, len).Trim('\0'); }
                            catch { }

                            if (string.IsNullOrWhiteSpace(ssid))
                                ssid = Encoding.GetEncoding("iso-8859-1").GetString(n.Ssid.SSID, 0, len).Trim('\0');
                        }

                        if (string.IsNullOrWhiteSpace(ssid)) continue;

                        int sig = (int)n.Signal;
                        bool secured = n.Secured != 0;
                        bool connected = ssid == conSsid;
                        string profile = n.ProfileName ?? "";

                        if (dict.TryGetValue(ssid, out Net existing))
                        {
                            if (sig > existing.Signal) existing.Signal = sig;
                            if (!string.IsNullOrEmpty(profile) && string.IsNullOrEmpty(existing.ProfileName))
                                existing.ProfileName = profile;
                            if (connected) existing.Connected = true;
                        }
                        else
                        {
                            dict[ssid] = new Net
                            {
                                SSID = ssid,
                                Signal = sig,
                                Secured = secured,
                                Connected = connected,
                                ProfileName = profile
                            };
                        }
                    }
                    catch (Exception itemEx)
                    {
                        Debug.WriteLine($"[WifiWindow] LoadNets item {i}: {itemEx.Message}");
                    }
                }
            }
            finally { WlanFreeMemory(p); }

            var result = dict.Values.ToList();
            result.Sort((a, b) =>
            {
                if (a.Connected != b.Connected) return a.Connected ? -1 : 1;
                return b.Signal.CompareTo(a.Signal);
            });

            Debug.WriteLine($"[WifiWindow] LoadNets result: {result.Count} networks");
            foreach (var net in result)
                Debug.WriteLine($"  {net.Signal,3}% {(net.Connected ? "[*]" : "   ")} {net.SSID}");

            return result;
        }

        string ConnectedSsid()
        {
            try
            {
                var pr = new Process
                {
                    StartInfo = new ProcessStartInfo("netsh", "wlan show interfaces")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    }
                };
                pr.Start();
                string o = pr.StandardOutput.ReadToEnd();
                if (!pr.WaitForExit(2000)) { try { pr.Kill(); } catch { } return ""; }

                foreach (var line in o.Split('\n'))
                {
                    var t = line.TrimStart();
                    if (t.StartsWith("SSID") && !t.Contains("BSSID"))
                    {
                        int idx = line.IndexOf(':');
                        if (idx >= 0) return line.Substring(idx + 1).Trim();
                    }
                }
            }
            catch { }
            return "";
        }

        bool GetAutoConnect(string profileName)
        {
            try
            {
                var pr = new Process
                {
                    StartInfo = new ProcessStartInfo("netsh", $"wlan show profile name=\"{profileName}\"")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.GetEncoding(866)
                    }
                };
                pr.Start();
                string o = pr.StandardOutput.ReadToEnd();
                if (!pr.WaitForExit(2000)) { try { pr.Kill(); } catch { } return false; }

                foreach (var line in o.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.IndexOf("onnect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        t.IndexOf("одключения", StringComparison.Ordinal) >= 0 || // "одключения" = suffix of Russian "connection"
                        t.IndexOf("ежим", StringComparison.OrdinalIgnoreCase) >= 0) // "ежим" = suffix of Russian "mode"
                    {
                        int colon = t.IndexOf(':');
                        if (colon < 0) continue;
                        string val = t.Substring(colon + 1).Trim().ToLowerInvariant();
                        return val.Contains("auto") || val.Contains("автомат"); // "автомат" = Russian "automatic"
                    }
                }
            }
            catch { }
            return false;
        }

        void SetAutoConnect(string profileName, bool auto)
        {
            if (string.IsNullOrEmpty(profileName)) return;
            _ = Task.Run(() =>
            {
                try
                {
                    string mode = auto ? "auto" : "manual";
                    var psi = new ProcessStartInfo(
                        "netsh",
                        $"wlan set profileparameter name=\"{profileName}\" connectionmode={mode}")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                        p?.WaitForExit(3000);
                    Debug.WriteLine($"[WifiWindow] SetAutoConnect '{profileName}' = {auto}");
                }
                catch (Exception ex) { Debug.WriteLine($"[WifiWindow] SetAutoConnect: {ex.Message}"); }
            });
        }

        void Rebuild(List<Net> nets)
        {
            bool same = NetsEqual(_nets, nets);
            _nets = nets;

            var con = nets.Find(n => n.Connected);
            if (con != null)
            {
                ConnectedSignal = con.Signal;
                ConnectedSsidLabel.Text = con.SSID;
                ConnectedStatusLabel.Text = con.Secured ? "Connected, secured" : "Connected";

                var sigElement = MakeSignalBars(con.Signal, con.Secured);
                ConnectedIcon.Content = sigElement;

                ConnectedBlock.Visibility = Visibility.Visible;
                ConnectedBlock.Background = new SolidColorBrush(Color.FromArgb(60, 0, 130, 230));
                ConnectedBlock.BorderThickness = new Thickness(3, 0, 0, 0);
                ConnectedBlock.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 160, 255));
                ConnectedBlock.Padding = new Thickness(9, 10, 12, 10);
                ConnectedBlock.Margin = new Thickness(0);
                DisconnectBlock.Visibility = Visibility.Visible;
                DisconnectBlock.Background = new SolidColorBrush(Color.FromArgb(60, 0, 130, 230));
                DisconnectBlock.BorderThickness = new Thickness(3, 0, 0, 0);
                DisconnectBlock.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 160, 255));
                DisconnectBlock.Padding = new Thickness(9, 6, 12, 10);
                DisconnectBlock.Margin = new Thickness(0);
                DisconnectButton.IsEnabled = true;
            }
            else
            {
                ConnectedSignal = -1;
                ConnectedBlock.Visibility = Visibility.Collapsed;
                ConnectedBlock.Background = null;
                ConnectedBlock.BorderThickness = new Thickness(0);
                DisconnectBlock.Visibility = Visibility.Collapsed;
                DisconnectBlock.Background = null;
                DisconnectBlock.BorderThickness = new Thickness(0);
            }

            if (same) return;

            string prev = _panelSsid;
            ClosePanel();
            NetworkList.Children.Clear();

            var nonConnected = nets.Where(n => !n.Connected).ToList();
            foreach (var net in nonConnected)
            {
                var row = MakeRow(net, isConnected: false);
                NetworkList.Children.Add(row);
                if (net.SSID == prev) OpenPanel(net, row);
            }

            if (nets.Count == 0)
                Status("No networks found", "#666666");
            else
                StatusLabel.Visibility = Visibility.Collapsed;
        }

        bool NetsEqual(List<Net> a, List<Net> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].SSID != b[i].SSID) return false;
                if (a[i].Connected != b[i].Connected) return false;
                if (Math.Abs(a[i].Signal - b[i].Signal) > 10) return false;
            }
            return true;
        }

        void OffState()
        {
            ConnectedBlock.Visibility = Visibility.Collapsed;
            DisconnectBlock.Visibility = Visibility.Collapsed;
            NetworkList.Children.Clear();
            _nets.Clear();
            ClosePanel();
            Status("Wi-Fi is off", "#666666");
        }

        static readonly SolidColorBrush _rowNormal = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        static readonly SolidColorBrush _rowHover = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        static readonly SolidColorBrush _rowConnected = new SolidColorBrush(Color.FromArgb(55, 0, 140, 255));
        static readonly SolidColorBrush _rowConHover = new SolidColorBrush(Color.FromArgb(80, 0, 140, 255));

        FrameworkElement MakeRow(Net net, bool isConnected = false)
        {
            var normalBg = isConnected ? _rowConnected : _rowNormal;
            var hoverBg = isConnected ? _rowConHover : _rowHover;

            var row = new Border
            {
                Background = normalBg,
                Padding = new Thickness(isConnected ? 8 : 12, 9, 12, 9),
                Cursor = Cursors.Hand,
                Tag = net.SSID,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                BorderThickness = isConnected ? new Thickness(3, 0, 0, 0) : new Thickness(0),
                BorderBrush = isConnected
                    ? new SolidColorBrush(Color.FromRgb(0, 160, 255))
                    : null
            };

            var g = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var sigEl = MakeSignalBars(net.Signal, net.Secured);
            Grid.SetColumn(sigEl, 0);
            g.Children.Add(sigEl);

            var nameBlock = new TextBlock
            {
                Text = net.SSID,
                Foreground = isConnected
                    ? new SolidColorBrush(Color.FromRgb(130, 220, 255))
                    : Brushes.White,
                FontSize = 13,
                FontWeight = isConnected ? FontWeights.SemiBold : FontWeights.Normal,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(nameBlock, 1);
            g.Children.Add(nameBlock);

            if (isConnected)
            {
                var check = new TextBlock
                {
                    Text = "",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(80, 200, 255)),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(6, 0, 0, 0)
                };
                Grid.SetColumn(check, 2);
                g.Children.Add(check);
            }

            row.Child = g;

            row.MouseEnter += (s, e) => row.Background = hoverBg;
            row.MouseLeave += (s, e) => row.Background = normalBg;
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (_panelSsid == net.SSID) ClosePanel();
                else OpenPanel(net, row);
            };

            return row;
        }

        UIElement MakeSignalBars(int signal, bool secured)
        {
            int bars = signal >= 75 ? 4 :
                       signal >= 50 ? 3 :
                       signal >= 25 ? 2 :
                       signal > 0 ? 1 : 0;

            var container = new Grid
            {
                Width = 28,
                Height = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var activeColor = Color.FromRgb(210, 210, 210);
            var inactiveColor = Color.FromArgb(60, 210, 210, 210);
            int[] heights = { 4, 7, 11, 15 };
            int startX = 5;

            for (int i = 0; i < 4; i++)
            {
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = 3,
                    Height = heights[i],
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = new SolidColorBrush(i < bars ? activeColor : inactiveColor),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(startX + i * 5, 0, 0, secured ? 5 : 2)
                };
                container.Children.Add(rect);
            }

            if (secured)
            {
                var lk = new TextBlock
                {
                    Text = "\uE72E",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 7,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 0, 1, 0)
                };
                TextOptions.SetTextFormattingMode(lk, TextFormattingMode.Display);
                container.Children.Add(lk);
            }

            return container;
        }

        string SigIcon(int s) =>
            s >= 75 ? "\uE701" :
            s >= 50 ? "\uE872" :
            s >= 25 ? "\uE873" :
            s > 0 ? "\uE874" :
                      "\uE875";

        void OpenPanel(Net net, FrameworkElement after)
        {
            ClosePanel();
            _panelSsid = net.SSID;

            var panel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
                Padding = new Thickness(12, 6, 12, 10)
            };
            var stack = new StackPanel();

            bool currentAuto = false;
            if (_autoConnectCache.TryGetValue(net.SSID, out bool cachedValue))
            {
                currentAuto = cachedValue;
                Debug.WriteLine($"[WifiWindow] OpenPanel: from CACHE '{net.SSID}' = {currentAuto}");
            }
            else if (net.HasProfile)
            {
                currentAuto = GetAutoConnect(net.ProfileName);
                Debug.WriteLine($"[WifiWindow] OpenPanel: from SYSTEM '{net.SSID}' = {currentAuto}");
            }
            else
            {
                currentAuto = false;
                Debug.WriteLine($"[WifiWindow] OpenPanel: NEW network '{net.SSID}'");
            }

            var chk = new CheckBox
            {
                Content = "Connect automatically",
                IsChecked = currentAuto,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                Focusable = false
            };

            if (net.HasProfile)
            {
                chk.Checked += (s, e) =>
                {
                    SetAutoConnect(net.ProfileName, true);
                    _autoConnectCache[net.SSID] = true;
                    Debug.WriteLine($"[WifiWindow] Checkbox ON for '{net.SSID}'");
                };
                chk.Unchecked += (s, e) =>
                {
                    SetAutoConnect(net.ProfileName, false);
                    _autoConnectCache[net.SSID] = false;
                    Debug.WriteLine($"[WifiWindow] Checkbox OFF for '{net.SSID}'");
                };
            }
            else
            {
                chk.IsEnabled = true;
            }

            stack.Children.Add(chk);

            PasswordBox pwdBox = null;
            TextBox txtBox = null;
            TextBlock errLbl = null;

            if (!net.HasProfile && net.Secured)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "Enter the network security key",
                    Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(0, 0, 0, 4)
                });

                var pg = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                pg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                pg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var bg = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
                var bbr = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));

                pwdBox = new PasswordBox
                {
                    FontSize = 12,
                    FontFamily = new FontFamily("Segoe UI"),
                    Foreground = Brushes.White,
                    Background = bg,
                    BorderBrush = bbr,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 5, 6, 5),
                    Margin = new Thickness(0, 0, 4, 0),
                    CaretBrush = Brushes.White
                };
                txtBox = new TextBox
                {
                    FontSize = 12,
                    FontFamily = new FontFamily("Segoe UI"),
                    Foreground = Brushes.White,
                    Background = bg,
                    BorderBrush = bbr,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 5, 6, 5),
                    Margin = new Thickness(0, 0, 4, 0),
                    CaretBrush = Brushes.White,
                    Visibility = Visibility.Collapsed
                };

                var eyeTb = new TextBlock
                {
                    Text = "\uE7B3",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150))
                };
                var eyeBtn = new Button
                {
                    Content = eyeTb,
                    Background = bg,
                    BorderBrush = bbr,
                    BorderThickness = new Thickness(1),
                    Width = 30,
                    Cursor = Cursors.Hand,
                    Focusable = false,
                    Padding = new Thickness(3)
                };
                bool show = false;
                eyeBtn.Click += (s, e) =>
                {
                    show = !show;
                    if (show)
                    {
                        txtBox.Text = pwdBox.Password;
                        txtBox.Visibility = Visibility.Visible;
                        pwdBox.Visibility = Visibility.Collapsed;
                        eyeTb.Text = "\uED1A";
                    }
                    else
                    {
                        pwdBox.Password = txtBox.Text;
                        pwdBox.Visibility = Visibility.Visible;
                        txtBox.Visibility = Visibility.Collapsed;
                        eyeTb.Text = "\uE7B3";
                    }
                };

                Grid.SetColumn(pwdBox, 0); Grid.SetColumn(txtBox, 0); Grid.SetColumn(eyeBtn, 1);
                pg.Children.Add(pwdBox); pg.Children.Add(txtBox); pg.Children.Add(eyeBtn);
                stack.Children.Add(pg);

                errLbl = new TextBlock
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 80, 80)),
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(0, 0, 0, 4),
                    Visibility = Visibility.Collapsed
                };
                stack.Children.Add(errLbl);
            }

            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var conBtn = new Button
            {
                Content = "Connect",
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 4, 12, 5),
                Cursor = Cursors.Hand,
                Focusable = false,
                Margin = new Thickness(0, 0, 6, 0)
            };
            conBtn.Click += (s, e) =>
            {
                string pwd = pwdBox != null
                    ? (txtBox?.Visibility == Visibility.Visible ? txtBox.Text : pwdBox.Password)
                    : null;
                if (net.Secured && pwdBox != null && (pwd?.Length ?? 0) < 8)
                {
                    if (errLbl != null) { errLbl.Text = "Minimum 8 characters"; errLbl.Visibility = Visibility.Visible; }
                    return;
                }
                bool auto = chk.IsChecked == true;
                ClosePanel();
                Connect(net, pwd, auto);
            };
            btns.Children.Add(conBtn);

            if (net.HasProfile)
            {
                var forBtn = new Button
                {
                    Content = "Forget",
                    FontSize = 12,
                    FontFamily = new FontFamily("Segoe UI"),
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromRgb(58, 58, 58)),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(12, 4, 12, 5),
                    Cursor = Cursors.Hand,
                    Focusable = false
                };
                forBtn.Click += (s, e) => { ClosePanel(); Forget(net); };
                btns.Children.Add(forBtn);
            }

            stack.Children.Add(btns);
            panel.Child = stack;
            _panel = panel;

            int idx = NetworkList.Children.IndexOf(after);
            if (idx >= 0) NetworkList.Children.Insert(idx + 1, panel);
            else NetworkList.Children.Add(panel);

            if (pwdBox != null)
                pwdBox.Dispatcher.BeginInvoke(new Action(() => pwdBox.Focus()), DispatcherPriority.Input);
        }

        void ClosePanel()
        {
            if (_panel != null) { NetworkList.Children.Remove(_panel); _panel = null; }
            _panelSsid = null;
        }

        void Connect(Net net, string pwd, bool auto)
        {
            Status($"Connecting to \"{net.SSID}\"…", "#55CC77");
            _connecting = true;

            _ = Task.Run(() =>
            {
                try
                {
                    _autoConnectCache[net.SSID] = auto;
                    Debug.WriteLine($"[WifiWindow] Connect: saved CACHE '{net.SSID}' = {auto}");

                    string mode = auto ? "auto" : "manual";

                    bool needProfile = !string.IsNullOrEmpty(pwd) || !net.HasProfile;

                    if (needProfile && _wlan != IntPtr.Zero && _iface != Guid.Empty)
                    {
                        string hexSsid = BitConverter.ToString(
                            Encoding.UTF8.GetBytes(net.SSID)).Replace("-", "");

                        string xml;
                        if (net.Secured)
                        {
                            xml = "<?xml version=\"1.0\"?>"
                                + "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">"
                                + "<n>" + EscapeXml(net.SSID) + "</n>"
                                + "<SSIDConfig>"
                                + "<SSID>"
                                + "<hex>" + hexSsid + "</hex>"
                                + "<n>" + EscapeXml(net.SSID) + "</n>"
                                + "</SSID>"
                                + "</SSIDConfig>"
                                + "<connectionType>ESS</connectionType>"
                                + "<connectionMode>" + mode + "</connectionMode>"
                                + "<MSM><security><authEncryption>"
                                + "<authentication>WPA2PSK</authentication>"
                                + "<encryption>AES</encryption>"
                                + "<useOneX>false</useOneX>"
                                + "</authEncryption>"
                                + "<sharedKey>"
                                + "<keyType>passPhrase</keyType>"
                                + "<protected>false</protected>"
                                + "<keyMaterial>" + EscapeXml(pwd) + "</keyMaterial>"
                                + "</sharedKey>"
                                + "</security></MSM>"
                                + "</WLANProfile>";
                        }
                        else
                        {
                            xml = "<?xml version=\"1.0\"?>"
                                + "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">"
                                + "<n>" + EscapeXml(net.SSID) + "</n>"
                                + "<SSIDConfig>"
                                + "<SSID>"
                                + "<hex>" + hexSsid + "</hex>"
                                + "<n>" + EscapeXml(net.SSID) + "</n>"
                                + "</SSID>"
                                + "</SSIDConfig>"
                                + "<connectionType>ESS</connectionType>"
                                + "<connectionMode>" + mode + "</connectionMode>"
                                + "<MSM><security><authEncryption>"
                                + "<authentication>open</authentication>"
                                + "<encryption>none</encryption>"
                                + "<useOneX>false</useOneX>"
                                + "</authEncryption></security></MSM>"
                                + "</WLANProfile>";
                        }

                        var g2 = _iface;
                        uint rc;
                        uint setResult = WlanSetProfile(_wlan, ref g2, 0, xml, null, true, IntPtr.Zero, out rc);
                        Debug.WriteLine($"[WifiWindow] WlanSetProfile result={setResult}");

                        if (setResult != 0)
                        {
                            _connecting = false;
                            Dispatcher.Invoke(() => Status($"Profile error ({setResult})", "#FF6060"));
                            return;
                        }

                        Thread.Sleep(150);
                    }
                    else if (net.HasProfile)
                    {
                        SetAutoConnect(net.ProfileName, auto);
                        Thread.Sleep(150);
                    }

                    if (_wlan != IntPtr.Zero && _iface != Guid.Empty)
                    {
                        string profName = net.HasProfile ? net.ProfileName : net.SSID;

                        var cp = new WLAN_CONNECTION_PARAMETERS
                        {
                            Mode = 0,
                            Profile = profName,
                            BssType = 1,
                            Flags = 0
                        };
                        var g = _iface;
                        uint cr = WlanConnect(_wlan, ref g, ref cp, IntPtr.Zero);
                        Debug.WriteLine($"[WifiWindow] WlanConnect result={cr}");
                    }

                    Thread.Sleep(2000);
                    _connecting = false;
                    BgScan();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WifiWindow] Connect error: {ex.Message}");
                    _connecting = false;
                    Dispatcher.Invoke(() => Status("Connection error", "#FF6060"));
                }
            });
        }

        static string EscapeXml(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;")
                    .Replace("'", "&apos;");
        }

        void Disconnect()
        {
            if (_wlan == IntPtr.Zero || _iface == Guid.Empty) return;
            DisconnectButton.IsEnabled = false;
            var g = _iface;
            WlanDisconnect(_wlan, ref g, IntPtr.Zero);
            ConnectedSignal = -1;
            ConnectedBlock.Visibility = Visibility.Collapsed;
            DisconnectBlock.Visibility = Visibility.Collapsed;
            Status("Disconnected", "#AAAAAA");
            Delay(1500, BgScan);
        }

        void Forget(Net net)
        {
            if (_wlan == IntPtr.Zero || _iface == Guid.Empty) return;
            var g = _iface;
            WlanDeleteProfile(_wlan, ref g,
                !string.IsNullOrEmpty(net.ProfileName) ? net.ProfileName : net.SSID,
                IntPtr.Zero);
            _autoConnectCache.Remove(net.SSID);
            Status($"Network \"{net.SSID}\" forgotten", "#AAAAAA");
            Delay(800, BgScan);
        }

        void Status(string text, string hex)
        {
            StatusLabel.Text = text;
            StatusLabel.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            StatusLabel.Visibility = Visibility.Visible;
            Delay(3000, () => { if (StatusLabel.Text == text) StatusLabel.Visibility = Visibility.Collapsed; });
        }

        void Delay(int ms, Action action)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); action(); };
            t.Start();
        }

        void SetWifiTile(bool on)
        {
            WifiTile.Background = on
                ? new SolidColorBrush(Color.FromRgb(0, 120, 212))
                : new SolidColorBrush(Color.FromRgb(42, 42, 42));
        }

        string GetWifiAdapterName()
        {
            if (!string.IsNullOrEmpty(_cachedAdapterName)) return _cachedAdapterName;
            try
            {
                if (_wlan != IntPtr.Zero)
                {
                    IntPtr l = IntPtr.Zero;
                    if (WlanEnumInterfaces(_wlan, IntPtr.Zero, out l) == 0 && l != IntPtr.Zero)
                    {
                        try
                        {
                            var hdr = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(l);
                            if (hdr.Count > 0)
                            {
                                var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(
                                    new IntPtr(l.ToInt64() + Marshal.SizeOf<WLAN_INTERFACE_INFO_LIST>()));
                                if (!string.IsNullOrEmpty(info.Desc))
                                { _cachedAdapterName = info.Desc.Trim(); return _cachedAdapterName; }
                            }
                        }
                        finally { WlanFreeMemory(l); }
                    }
                }
            }
            catch { }
            return _cachedAdapterName ?? "";
        }

        // ════════════════════════════════════════════════════════════════
        //  Включение / выключение Wi-Fi (без прав администратора)
        //  Приоритет: WinRT Radio API → WlanAPI → ничего
        // ════════════════════════════════════════════════════════════════

        void SetWifiAdapter(bool enable)
        {
            _adapterToggling = true;
            SetWifiTile(enable);

            if (!enable)
            {
                ConnectedSignal = -1;
                _cachedNets.Clear();
                OffState();
            }

            // async void — запускаем асинхронную цепочку из синхронного контекста
            _ = SetWifiAdapterAsync(enable);
        }

        async Task SetWifiAdapterAsync(bool enable)
        {
            bool success = false;

            if (enable)
            {
                // ══ ВКЛЮЧЕНИЕ ════════════════════════════════════════════
                // После RequestAccessAsync WinRT видит выключенное радио тоже.

                // 1. WinRT Radio API (приоритет)
                try
                {
                    success = await SetRadioViaWinRT(true);
                    Debug.WriteLine($"[WifiWindow] WinRT Radio enable: success={success}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WifiWindow] WinRT Radio enable error: {ex.Message}");
                }

                // 2. Fallback: WlanAPI opCode=7
                if (!success)
                {
                    try
                    {
                        success = SetRadioViaWlanApi(true);
                        Debug.WriteLine($"[WifiWindow] WlanSetInterface enable: success={success}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[WifiWindow] WlanSetInterface enable error: {ex.Message}");
                    }
                }

                // 3. Последний шанс: PowerShell Enable-NetAdapter
                if (!success)
                {
                    try
                    {
                        string adapterName = GetWifiAdapterName();
                        if (!string.IsNullOrEmpty(adapterName))
                        {
                            var psi = new ProcessStartInfo("powershell.exe",
                                $"-NoProfile -NonInteractive -WindowStyle Hidden " +
                                $"-Command \"Enable-NetAdapter -Name '{adapterName}' -Confirm:$false\"")
                            {
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using (var p = Process.Start(psi))
                            {
                                p?.WaitForExit(5000);
                                success = true;
                            }
                            Debug.WriteLine($"[WifiWindow] PowerShell Enable-NetAdapter sent");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[WifiWindow] PowerShell enable error: {ex.Message}");
                    }
                }
            }
            else
            {
                // ══ ВЫКЛЮЧЕНИЕ ═══════════════════════════════════════════
                // WinRT видит включённый адаптер — используем его первым,
                // так как это самый надёжный способ без прав администратора.

                // 1. WinRT Radio API
                try
                {
                    success = await SetRadioViaWinRT(false);
                    Debug.WriteLine($"[WifiWindow] WinRT Radio disable: success={success}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WifiWindow] WinRT Radio disable error: {ex.Message}");
                }

                // 2. Fallback: WlanAPI opCode=7
                if (!success)
                {
                    try
                    {
                        success = SetRadioViaWlanApi(false);
                        Debug.WriteLine($"[WifiWindow] WlanSetInterface disable: success={success}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[WifiWindow] WlanSetInterface disable error: {ex.Message}");
                    }
                }
            }

            // ── Ждём применения в фоне ───────────────────────────────────
            await Task.Run(() =>
            {
                for (int i = 0; i < 16; i++)
                {
                    Thread.Sleep(300);
                    bool current = AdapterOn();
                    if (enable ? current : !current) break;
                }
            });

            // ── Обновляем UI ─────────────────────────────────────────────
            _adapterToggling = false;
            bool real = AdapterOn();
            _wifiOn = real;
            SetWifiTile(real);

            if (!real)
            {
                ConnectedSignal = -1;
                _cachedNets.Clear();
                OffState();
            }
            else
            {
                StatusLabel.Visibility = Visibility.Collapsed;
                BgScan();
            }
        }

        // ── WinRT: Radio.SetStateAsync — работает без UAC ────────────────
        // GetRadiosAsync() возвращает ВСЕ радио включая выключенные программно.
        // Предварительно вызываем RequestAccessAsync() чтобы получить разрешение.
        async Task<bool> SetRadioViaWinRT(bool enable)
        {
            try
            {
                // Запрашиваем разрешение на управление радио (обязательно для включения)
                var access = await Radio.RequestAccessAsync();
                Debug.WriteLine($"[WifiWindow] WinRT RequestAccess result={access}");

                if (access != RadioAccessStatus.Allowed)
                    return false;

                var radios = await Radio.GetRadiosAsync();
                Debug.WriteLine($"[WifiWindow] WinRT GetRadiosAsync: found {radios.Count} radio(s)");

                bool any = false;
                foreach (var radio in radios)
                {
                    Debug.WriteLine($"[WifiWindow] WinRT Radio: Kind={radio.Kind} State={radio.State} Name={radio.Name}");
                    if (radio.Kind == RadioKind.WiFi)
                    {
                        RadioState targetState = enable ? RadioState.On : RadioState.Off;
                        RadioAccessStatus result = await radio.SetStateAsync(targetState);
                        Debug.WriteLine($"[WifiWindow] WinRT SetState={targetState} result={result}");

                        if (result == RadioAccessStatus.Allowed)
                            any = true;
                    }
                }

                return any;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WifiWindow] SetRadioViaWinRT: {ex.Message}");
                return false;
            }
        }

        // ── WlanAPI opCode=7: второй вариант без UAC ─────────────────────
        bool SetRadioViaWlanApi(bool enable)
        {
            if (_wlan == IntPtr.Zero || _iface == Guid.Empty) return false;
            try
            {
                int size = Marshal.SizeOf<ulong>();
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    uint soft = enable ? 0u : 1u;
                    uint hard = 0u;
                    Marshal.WriteInt32(buf, 0, (int)soft);
                    Marshal.WriteInt32(buf, 4, (int)hard);

                    var g = _iface;
                    uint r = WlanSetInterface(_wlan, ref g, 7, (uint)size, buf, IntPtr.Zero);
                    Debug.WriteLine($"[WifiWindow] WlanSetInterface opCode=7 result={r}");
                    return r == 0;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WifiWindow] SetRadioViaWlanApi: {ex.Message}");
                return false;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Обработчики кнопок
        // ════════════════════════════════════════════════════════════════

        void DisconnectButton_Click(object sender, RoutedEventArgs e) => Disconnect();

        void PropertiesButton_Click(object sender, RoutedEventArgs e)
        { try { Process.Start(new ProcessStartInfo("ms-settings:network-wifi") { UseShellExecute = true }); } catch { } }

        void NetworkSettingsButton_Click(object sender, RoutedEventArgs e)
        { try { Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true }); } catch { } }

        void WifiTile_Click(object sender, MouseButtonEventArgs e)
        {
            if (_adapterToggling) return;
            bool target = !_wifiOn;
            _wifiOn = target;
            SetWifiAdapter(target);
        }

        void AirplaneTile_Click(object sender, MouseButtonEventArgs e)
        {
            _airplane = !_airplane;
            AirplaneTile.Background = _airplane
                ? new SolidColorBrush(Color.FromRgb(0, 120, 212))
                : new SolidColorBrush(Color.FromRgb(42, 42, 42));
            try { Process.Start(new ProcessStartInfo("ms-settings:network-airplanemode") { UseShellExecute = true }); } catch { }
        }

        void HotspotTile_Click(object sender, MouseButtonEventArgs e)
        { try { Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot") { UseShellExecute = true }); } catch { } }

        void Window_Deactivated(object sender, EventArgs e) { _refresh?.Stop(); ClosePanel(); Hide(); }

        protected override void OnClosed(EventArgs e)
        {
            _refresh?.Stop();
            _bgTimer?.Stop();
            if (_wlan != IntPtr.Zero) WlanCloseHandle(_wlan, IntPtr.Zero);
            base.OnClosed(e);
        }
    }
}