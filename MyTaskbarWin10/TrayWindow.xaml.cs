using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MyTaskbar
{
    public partial class TrayWindow : Window
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr FindWindow(string cls, string wnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string cls, string wnd);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern IntPtr CopyIcon(IntPtr hIcon);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
        [DllImport("user32.dll")]
        static extern bool BringWindowToTop(IntPtr hwnd);

        // ── НОВИЙ P/Invoke для Z-order ────────────────────────────────
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        static readonly IntPtr HWND_TOP = new IntPtr(0);
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOACTIVATE = 0x0010;
        // ─────────────────────────────────────────────────────────────

        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
            IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
            uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")]
        static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        const uint EVENT_OBJECT_CREATE = 0x8000;
        const uint EVENT_OBJECT_SHOW = 0x8002;
        const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        [DllImport("kernel32.dll")]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")]
        static extern bool ReadProcessMemory(IntPtr proc, IntPtr baseAddr, IntPtr buffer, int size, out int read);
        [DllImport("kernel32.dll")]
        static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, int size, uint allocType, uint protect);
        [DllImport("kernel32.dll")]
        static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, int size, uint freeType);

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        const uint TB_BUTTONCOUNT = 0x0418;
        const uint TB_GETBUTTON = 0x0417;
        const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
        const uint MEM_COMMIT = 0x1000;
        const uint MEM_RELEASE = 0x8000;
        const uint PAGE_READWRITE = 0x04;

        const uint WM_LBUTTONDOWN = 0x0201;
        const uint WM_LBUTTONUP = 0x0202;
        const uint WM_LBUTTONDBLCLK = 0x0203;
        const uint WM_RBUTTONDOWN = 0x0204;
        const uint WM_RBUTTONUP = 0x0205;
        const uint WM_CONTEXTMENU = 0x007B;
        const uint WM_CLOSE = 0x0010;
        const uint NIN_SELECT = 0x0400;

        const int SW_RESTORE = 9;
        const byte TBSTATE_HIDDEN = 0x08;

        const int ICON_SIZE = 24;
        const int ICON_MARGIN = 4;
        const int ICON_CELL = ICON_SIZE + ICON_MARGIN;
        const int PANEL_PAD = 8;

        static readonly HashSet<string> _watchedProcs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "telegram", "discord", "slack", "teams", "zoom", "skype" };

        static readonly HashSet<string> _hardSysProcs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "dwm","fontdrvhost","audiodg","lsass","smss","csrss",
                "wininit","services","spoolsv","conhost","wermgr","werfault",
                "searchindexer","msiexec"
            };

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct TBBUTTON64
        {
            public int iBitmap, idCommand;
            public byte fsState, fsStyle;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] bReserved;
            public ulong dwData;
            public long iString;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct TBBUTTON32
        {
            public int iBitmap, idCommand;
            public byte fsState, fsStyle, r0, r1;
            public uint dwData;
            public int iString;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TRAYDATA
        {
            public IntPtr hwnd;
            public uint uID;
            public uint uCallbackMessage;
            public int Reserved0, Reserved1;
            public IntPtr hIcon;
        }

        class TrayIcon
        {
            public IntPtr AppHwnd;
            public uint AppID;
            public uint CallbackMsg;
            public string ProcessName;
            public int ProcessId;
            public BitmapSource Icon;

            public bool IsTelegram => ProcessName != null &&
                ProcessName.IndexOf("telegram", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        bool _suppressDeactivate = false;
        static readonly int _ownPid = Process.GetCurrentProcess().Id;

        bool _isOpen = false;
        bool _animating = false;
        const int ANIM_MS = 180;

        public bool IsOpen => _isOpen;

        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _fastTimer;
        private readonly DispatcherTimer _processWatcher;
        private readonly HashSet<string> _pendingProcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _pendingStart = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private WinEventDelegate _winEventDelegate;
        private IntPtr _winEventHook = IntPtr.Zero;

        public TrayWindow()
        {
            InitializeComponent();

            SourceInitialized += (s, e) =>
            {
                ApplyAcrylic();
                Top = -2000;
                Opacity = 0;
            };

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _refreshTimer.Tick += (s, e) => RefreshIconsAlways();
            _refreshTimer.Start();

            _fastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _fastTimer.Tick += FastTimerTick;

            _processWatcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _processWatcher.Tick += ProcessWatcherTick;
            _processWatcher.Start();

            _winEventDelegate = OnWinEvent;
            _winEventHook = SetWinEventHook(
                EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW,
                IntPtr.Zero, _winEventDelegate,
                0, 0, WINEVENT_OUTOFCONTEXT);

            Closed += (s, e) =>
            {
                if (_winEventHook != IntPtr.Zero)
                    UnhookWinEvent(_winEventHook);
            };
        }

        // ── Акрил ────────────────────────────────────────────────────
        void ApplyAcrylic()
        {
            try { MyTaskbar.Helpers.AcrylicHelper.EnableAcrylic(this, 0x701A1A2E); }
            catch
            {
                try { MyTaskbar.Helpers.AcrylicHelper.EnableBlur(this, 0x70202030); }
                catch
                {
                    if (RootBorder != null)
                        RootBorder.Background = new SolidColorBrush(
                            Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1C));
                }
            }
        }

        // ── Z-order: наше вікно під панеллю задач ────────────────────
        void PlaceBelowTaskbar()
        {
            var helper = new WindowInteropHelper(this);
            IntPtr myHwnd = helper.Handle;
            IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);

            // Спочатку піднімаємо наше вікно вгору (але не TOPMOST)
            SetWindowPos(myHwnd, HWND_TOP,
                0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            // Потім ставимо панель задач ПОВЕРХ нашого вікна
            if (taskbarHwnd != IntPtr.Zero)
                SetWindowPos(taskbarHwnd, HWND_TOP,
                    0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // ── WinEvent ──────────────────────────────────────────────────
        void OnWinEvent(IntPtr hHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd == IntPtr.Zero || idObject != 0) return;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == (uint)_ownPid) return;

            string procName = "";
            try { procName = Process.GetProcessById((int)pid).ProcessName; }
            catch { return; }

            if (!_watchedProcs.Contains(procName)) return;

            if (!_pendingProcs.Contains(procName))
            {
                _pendingProcs.Add(procName);
                _pendingStart[procName] = DateTime.Now;
            }
            if (!_fastTimer.IsEnabled) _fastTimer.Start();
        }

        // ── Telegram ──────────────────────────────────────────────────
        IntPtr FindTelegramMainWindow(int pid)
        {
            IntPtr best = IntPtr.Zero, fallback = IntPtr.Zero;
            EnumWindows((hwnd, lp) =>
            {
                GetWindowThreadProcessId(hwnd, out uint wpid);
                if ((int)wpid != pid) return true;
                var sb = new System.Text.StringBuilder(128);
                GetClassName(hwnd, sb, 128);
                string cls = sb.ToString();
                if (cls == "TelegramDesktopClass") { best = hwnd; return false; }
                if (cls.StartsWith("Qt", StringComparison.OrdinalIgnoreCase) ||
                    cls.IndexOf("Telegram", StringComparison.OrdinalIgnoreCase) >= 0)
                    fallback = hwnd;
                return true;
            }, IntPtr.Zero);
            return best != IntPtr.Zero ? best : fallback;
        }

        IntPtr FindTrayWindowForProcess(int pid, IntPtr originalHwnd, bool isTelegram)
        {
            if (isTelegram) return FindTelegramMainWindow(pid);
            if (IsWindow(originalHwnd)) return originalHwnd;
            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, lp) =>
            {
                GetWindowThreadProcessId(hwnd, out uint wpid);
                if ((int)wpid == pid) { found = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        void ActivateTelegram(TrayIcon icon)
        {
            IntPtr mainHwnd = FindTelegramMainWindow(icon.ProcessId);
            IntPtr fakeHwnd = IntPtr.Zero;

            EnumWindows((hwnd, lp) =>
            {
                GetWindowThreadProcessId(hwnd, out uint wpid);
                if ((int)wpid != icon.ProcessId) return true;
                var sb = new System.Text.StringBuilder(128);
                GetClassName(hwnd, sb, 128);
                string cls = sb.ToString();
                if (cls == "TelegramDesktopFakeWindow" ||
                    cls.IndexOf("FakeWindow", StringComparison.OrdinalIgnoreCase) >= 0)
                { fakeHwnd = hwnd; return false; }
                return true;
            }, IntPtr.Zero);

            IntPtr callbackTarget = fakeHwnd != IntPtr.Zero ? fakeHwnd :
                                    IsWindow(icon.AppHwnd) ? icon.AppHwnd :
                                    mainHwnd;

            if (callbackTarget == IntPtr.Zero && mainHwnd == IntPtr.Zero) return;

            GetCursorPos(out POINT pt);
            if (callbackTarget != IntPtr.Zero && icon.CallbackMsg != 0)
            {
                SetForegroundWindow(callbackTarget);
                PostMessage(callbackTarget, icon.CallbackMsg, (IntPtr)icon.AppID, (IntPtr)WM_LBUTTONDOWN);
                PostMessage(callbackTarget, icon.CallbackMsg, (IntPtr)icon.AppID, (IntPtr)WM_LBUTTONUP);
                PostMessage(callbackTarget, icon.CallbackMsg, (IntPtr)icon.AppID, (IntPtr)NIN_SELECT);
                IntPtr wParamV4 = new IntPtr(unchecked((int)((pt.Y << 16) | (pt.X & 0xFFFF))));
                PostMessage(callbackTarget, icon.CallbackMsg, wParamV4, new IntPtr(unchecked((int)((icon.AppID << 16) | (WM_LBUTTONDOWN & 0xFFFF)))));
                PostMessage(callbackTarget, icon.CallbackMsg, wParamV4, new IntPtr(unchecked((int)((icon.AppID << 16) | (WM_LBUTTONUP & 0xFFFF)))));
                PostMessage(callbackTarget, icon.CallbackMsg, wParamV4, new IntPtr(unchecked((int)((icon.AppID << 16) | (NIN_SELECT & 0xFFFF)))));
            }

            if (mainHwnd != IntPtr.Zero)
            {
                Task.Delay(150).ContinueWith(_ =>
                    Dispatcher.Invoke(() =>
                    {
                        IntPtr fg = GetForegroundWindow();
                        GetWindowThreadProcessId(fg, out uint fgPid);
                        if ((int)fgPid != icon.ProcessId)
                        {
                            ShowWindow(mainHwnd, SW_RESTORE);
                            SetForegroundWindow(mainHwnd);
                            BringWindowToTop(mainHwnd);
                        }
                    }));
            }
        }

        // ── Таймери ───────────────────────────────────────────────────
        void ProcessWatcherTick(object sender, EventArgs e)
        {
            var currentIcons = GetTrayIcons();
            var iconProcNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var icon in currentIcons) iconProcNames.Add(icon.ProcessName);

            bool newPending = false;
            foreach (var watchedName in _watchedProcs)
            {
                var procs = Process.GetProcessesByName(watchedName);
                if (procs.Length == 0) continue;
                if (!iconProcNames.Contains(watchedName) && !_pendingProcs.Contains(watchedName))
                {
                    _pendingProcs.Add(watchedName);
                    _pendingStart[watchedName] = DateTime.Now;
                    newPending = true;
                    if (!_fastTimer.IsEnabled) _fastTimer.Start();
                }
                if (iconProcNames.Contains(watchedName) && _pendingProcs.Contains(watchedName))
                {
                    _pendingProcs.Remove(watchedName);
                    _pendingStart.Remove(watchedName);
                }
            }

            var toRemove = new List<string>();
            foreach (var kv in _pendingStart)
                if ((DateTime.Now - kv.Value).TotalMinutes > 2)
                    toRemove.Add(kv.Key);
            foreach (var name in toRemove) { _pendingProcs.Remove(name); _pendingStart.Remove(name); }

            if (_pendingProcs.Count == 0) _fastTimer.Stop();
            if (newPending) RefreshIconsAlways();
        }

        void FastTimerTick(object sender, EventArgs e)
        {
            RefreshIconsAlways();
            var currentIcons = GetTrayIcons();
            var iconProcNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var icon in currentIcons) iconProcNames.Add(icon.ProcessName);
            var appeared = new List<string>();
            foreach (var name in _pendingProcs)
                if (iconProcNames.Contains(name)) appeared.Add(name);
            foreach (var name in appeared) { _pendingProcs.Remove(name); _pendingStart.Remove(name); }
            if (_pendingProcs.Count == 0) _fastTimer.Stop();
        }

        void RefreshIconsAlways() => RenderIcons(GetTrayIcons());

        // ── SlideDown / SlideUp ───────────────────────────────────────
        double _pendingCenterX;
        double _pendingVisibleTop;

        public void SlideDown(double centerX, double visibleTop)
        {
            if (_animating || _isOpen) return;

            _pendingCenterX = centerX;
            _pendingVisibleTop = visibleTop;

            RenderIcons(GetTrayIcons());

            Opacity = 0;
            Top = -9999;
            Left = -9999;

            Dispatcher.BeginInvoke(new Action(SlideDown_AfterMeasure),
                System.Windows.Threading.DispatcherPriority.Render);
        }

        void SlideDown_AfterMeasure()
        {
            InvalidateMeasure();
            InvalidateArrange();
            UpdateLayout();

            double sw = SystemParameters.PrimaryScreenWidth;
            double w = ActualWidth > 4 ? ActualWidth : 80;
            double h = ActualHeight > 4 ? ActualHeight : 40;

            double left = _pendingCenterX - w / 2;
            if (left + w > sw - 4) left = sw - w - 4;
            if (left < 4) left = 4;

            Left = left;
            double hiddenTop = -(h + 10);
            double shownTop = _pendingVisibleTop;

            _isOpen = true;
            _animating = true;

            Top = hiddenTop;
            Opacity = 1;
            Activate();

            // ── Ставимо Z-order: панель задач поверх нашого вікна ────
            PlaceBelowTaskbar();
            // ─────────────────────────────────────────────────────────

            var anim = new DoubleAnimation(hiddenTop, shownTop, TimeSpan.FromMilliseconds(ANIM_MS))
            {
                EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            anim.Completed += (s, e) =>
            {
                BeginAnimation(TopProperty, null);
                Top = shownTop;
                _animating = false;

                // ── Повторно виправляємо Z-order після завершення анімації
                PlaceBelowTaskbar();
            };
            BeginAnimation(TopProperty, anim);

            Task.Delay(500).ContinueWith(_ =>
                Dispatcher.Invoke(() =>
                {
                    if (_isOpen)
                    {
                        RenderIcons(GetTrayIcons());
                        PlaceBelowTaskbar(); // ще раз після перемальовки
                    }
                }));
        }

        public void SlideUp(Action onComplete = null)
        {
            if (_animating || !_isOpen) { onComplete?.Invoke(); return; }

            _isOpen = false;
            _animating = true;

            double currentTop = Top;
            double h = ActualHeight > 0 ? ActualHeight : 40;
            double hiddenTop = -(h + 10);

            var anim = new DoubleAnimation(currentTop, hiddenTop, TimeSpan.FromMilliseconds(ANIM_MS))
            {
                EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop
            };
            anim.Completed += (s, e) =>
            {
                BeginAnimation(TopProperty, null);
                Top = -2000;
                Opacity = 0;
                _animating = false;
                onComplete?.Invoke();
            };
            BeginAnimation(TopProperty, anim);
        }

        // ── Tray icons ────────────────────────────────────────────────
        List<TrayIcon> GetTrayIcons()
        {
            var result = new List<TrayIcon>();

            IntPtr overflow = FindWindow("NotifyIconOverflowWindow", null);
            if (overflow != IntPtr.Zero)
            {
                IntPtr tb = FindWindowEx(overflow, IntPtr.Zero, "ToolbarWindow32", null);
                if (tb != IntPtr.Zero)
                    ReadToolbar(tb, result, filterHidden: false);
            }

            IntPtr mainTb = GetMainTrayToolbar();
            if (mainTb != IntPtr.Zero)
                ReadToolbar(mainTb, result, filterHidden: false);

            return result;
        }

        IntPtr GetMainTrayToolbar()
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            IntPtr pager = FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
            if (pager != IntPtr.Zero)
            {
                IntPtr tb = FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", null);
                if (tb != IntPtr.Zero) return tb;
            }
            return FindWindowEx(notify, IntPtr.Zero, "ToolbarWindow32", null);
        }

        void ReadToolbar(IntPtr toolbar, List<TrayIcon> result, bool filterHidden)
        {
            GetWindowThreadProcessId(toolbar, out uint explorerPid);
            IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, explorerPid);
            if (hProc == IntPtr.Zero) return;

            try
            {
                int count = (int)(uint)SendMessage(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
                if (count <= 0) return;

                bool is64 = IntPtr.Size == 8;
                int btnSz = is64 ? Marshal.SizeOf<TBBUTTON64>() : Marshal.SizeOf<TBBUTTON32>();
                int trySz = Marshal.SizeOf<TRAYDATA>();
                int bufSz = Math.Max(btnSz, trySz);

                IntPtr remBuf = VirtualAllocEx(hProc, IntPtr.Zero, bufSz, MEM_COMMIT, PAGE_READWRITE);
                if (remBuf == IntPtr.Zero) return;

                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        SendMessage(toolbar, TB_GETBUTTON, (IntPtr)i, remBuf);
                        IntPtr localBtn = Marshal.AllocHGlobal(btnSz);
                        try
                        {
                            if (!ReadProcessMemory(hProc, remBuf, localBtn, btnSz, out _)) continue;

                            ulong dwData; byte fsState;
                            if (is64)
                            {
                                var b = Marshal.PtrToStructure<TBBUTTON64>(localBtn);
                                dwData = b.dwData; fsState = b.fsState;
                            }
                            else
                            {
                                var b = Marshal.PtrToStructure<TBBUTTON32>(localBtn);
                                dwData = (ulong)b.dwData; fsState = b.fsState;
                            }

                            if (filterHidden && (fsState & TBSTATE_HIDDEN) != 0) continue;

                            IntPtr trayPtr = new IntPtr(unchecked((long)dwData));
                            IntPtr localTry = Marshal.AllocHGlobal(trySz);
                            try
                            {
                                if (!ReadProcessMemory(hProc, trayPtr, localTry, trySz, out _)) continue;
                                var td = Marshal.PtrToStructure<TRAYDATA>(localTry);
                                if (td.uCallbackMessage == 0) continue;

                                GetWindowThreadProcessId(td.hwnd, out uint appPid);
                                int pid = (int)appPid;
                                if (pid == 0 || pid == _ownPid) continue;

                                string procName = "";
                                try { procName = Process.GetProcessById(pid).ProcessName; }
                                catch { continue; }

                                if (_hardSysProcs.Contains(procName)) continue;

                                bool isTelegram = procName.IndexOf("telegram",
                                    StringComparison.OrdinalIgnoreCase) >= 0;
                                if (isTelegram)
                                {
                                    if (result.Exists(x => x.IsTelegram)) continue;
                                }
                                else
                                {
                                    if (result.Exists(x => x.ProcessId == pid)) continue;
                                }

                                BitmapSource bmp = null;
                                if (td.hIcon != IntPtr.Zero)
                                {
                                    try
                                    {
                                        IntPtr copied = CopyIcon(td.hIcon);
                                        if (copied != IntPtr.Zero)
                                        {
                                            bmp = Imaging.CreateBitmapSourceFromHIcon(
                                                copied, Int32Rect.Empty,
                                                BitmapSizeOptions.FromEmptyOptions());
                                            if (bmp?.CanFreeze == true) bmp.Freeze();
                                            DestroyIcon(copied);
                                        }
                                    }
                                    catch { }
                                }
                                if (bmp == null)
                                    try { bmp = Helpers.IconHelper.GetIconFromProcess(pid, 16); } catch { }

                                result.Add(new TrayIcon
                                {
                                    AppHwnd = td.hwnd,
                                    AppID = td.uID,
                                    CallbackMsg = td.uCallbackMessage,
                                    ProcessName = procName,
                                    ProcessId = pid,
                                    Icon = bmp
                                });
                            }
                            finally { Marshal.FreeHGlobal(localTry); }
                        }
                        finally { Marshal.FreeHGlobal(localBtn); }
                    }
                }
                finally { VirtualFreeEx(hProc, remBuf, 0, MEM_RELEASE); }
            }
            finally { CloseHandle(hProc); }
        }

        // ── Рендер ───────────────────────────────────────────────────
        void RenderIcons(List<TrayIcon> icons)
        {
            IconsPanel.Children.Clear();
            EmptyLabel.Visibility = icons.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var icon in icons)
                IconsPanel.Children.Add(MakeBtn(icon));

            int count = icons.Count;
            int cols = count == 0 ? 1 :
                        count <= 3 ? count :
                        count <= 9 ? 3 : 4;

            double winWidth = cols * ICON_CELL + PANEL_PAD + 2;
            IconsPanel.MaxWidth = winWidth;
            Width = winWidth;
        }

        UIElement MakeBtn(TrayIcon icon)
        {
            var btn = new Button
            {
                Width = ICON_SIZE,
                Height = ICON_SIZE,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                Margin = new Thickness(2),
                ToolTip = string.IsNullOrEmpty(icon.ProcessName) ? "Додаток" : icon.ProcessName
            };

            var bdF = new FrameworkElementFactory(typeof(Border));
            bdF.Name = "bg";
            bdF.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bdF.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            var cpF = new FrameworkElementFactory(typeof(ContentPresenter));
            cpF.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cpF.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bdF.AppendChild(cpF);

            var tpl = new ControlTemplate(typeof(Button)) { VisualTree = bdF };

            var hoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), "bg"));
            var pressTrigger = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), "bg"));
            tpl.Triggers.Add(hoverTrigger);
            tpl.Triggers.Add(pressTrigger);
            btn.Template = tpl;

            if (icon.Icon != null)
                btn.Content = new Image
                {
                    Source = icon.Icon,
                    Width = 16,
                    Height = 16,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true
                };
            else
            {
                string letter = string.IsNullOrEmpty(icon.ProcessName)
                    ? "?" : icon.ProcessName.Substring(0, 1).ToUpper();
                btn.Content = new TextBlock
                {
                    Text = letter,
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
            }

            btn.Click += (s, e) =>
            {
                _suppressDeactivate = true;
                try
                {
                    if (icon.IsTelegram)
                    {
                        ActivateTelegram(icon);
                    }
                    else
                    {
                        IntPtr targetHwnd = FindTrayWindowForProcess(icon.ProcessId, icon.AppHwnd, false);
                        if (targetHwnd != IntPtr.Zero)
                        {
                            SetForegroundWindow(targetHwnd);
                            GetCursorPos(out POINT pt);
                            var eff = IconWithHwnd(icon, targetHwnd);
                            SendTrayV3(eff, WM_LBUTTONDOWN, pt);
                            SendTrayV3(eff, WM_LBUTTONUP, pt);
                            SendTrayV3(eff, NIN_SELECT, pt);
                            SendTrayV4(eff, WM_LBUTTONDOWN, pt);
                            SendTrayV4(eff, WM_LBUTTONUP, pt);
                            SendTrayV4(eff, NIN_SELECT, pt);
                        }
                    }
                }
                catch { }
                Task.Delay(300).ContinueWith(_ =>
                    Dispatcher.Invoke(() => { _suppressDeactivate = false; SlideUp(); }));
            };

            btn.MouseDoubleClick += (s, e) =>
            {
                if (e.ChangedButton != MouseButton.Left) return;
                e.Handled = true;
                _suppressDeactivate = true;
                try
                {
                    if (icon.IsTelegram)
                    {
                        ActivateTelegram(icon);
                    }
                    else
                    {
                        IntPtr targetHwnd = FindTrayWindowForProcess(icon.ProcessId, icon.AppHwnd, false);
                        if (targetHwnd != IntPtr.Zero)
                        {
                            SetForegroundWindow(targetHwnd);
                            GetCursorPos(out POINT pt);
                            var eff = IconWithHwnd(icon, targetHwnd);
                            SendTrayV3(eff, WM_LBUTTONDBLCLK, pt);
                            SendTrayV4(eff, WM_LBUTTONDBLCLK, pt);
                            SendTrayV3(eff, NIN_SELECT, pt);
                            SendTrayV4(eff, NIN_SELECT, pt);
                        }
                    }
                }
                catch { }
                Task.Delay(300).ContinueWith(_ =>
                    Dispatcher.Invoke(() => { _suppressDeactivate = false; SlideUp(); }));
            };

            btn.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (icon.IsTelegram)
                    ShowTelegramMenu(icon);
                else
                    ShowNativeContextMenu(icon);
            };

            return btn;
        }

        // ── Контекстні меню ──────────────────────────────────────────
        void ShowTelegramMenu(TrayIcon icon)
        {
            _suppressDeactivate = true;
            var cm = new ContextMenu();

            var openItem = new MenuItem { Header = "Відкрити Telegram" };
            openItem.Click += (s, e) =>
            {
                ActivateTelegram(icon);
                _suppressDeactivate = false;
                SlideUp();
            };
            cm.Items.Add(openItem);
            cm.Items.Add(new Separator());

            var closeItem = new MenuItem { Header = "Закрити Telegram", Foreground = Brushes.Tomato };
            closeItem.Click += (s, e) =>
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(icon.ProcessName))
                        try { p.Kill(); } catch { }
                    foreach (var p in Process.GetProcessesByName("Telegram"))
                        try { p.Kill(); } catch { }
                }
                catch { }
                _suppressDeactivate = false;
                SlideUp();
            };
            cm.Items.Add(closeItem);

            cm.Closed += (s, e) => { _suppressDeactivate = false; };
            cm.IsOpen = true;
        }

        void ShowNativeContextMenu(TrayIcon icon)
        {
            _suppressDeactivate = true;
            bool nativeSent = false;
            try
            {
                IntPtr targetHwnd = FindTrayWindowForProcess(icon.ProcessId, icon.AppHwnd, false);
                if (targetHwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(targetHwnd);
                    GetCursorPos(out POINT pt);
                    var eff = IconWithHwnd(icon, targetHwnd);
                    SendTrayV3(eff, WM_RBUTTONDOWN, pt);
                    SendTrayV3(eff, WM_RBUTTONUP, pt);
                    SendTrayV4(eff, WM_RBUTTONDOWN, pt);
                    SendTrayV4(eff, WM_RBUTTONUP, pt);
                    PostMessage(targetHwnd, WM_CONTEXTMENU, targetHwnd, MakePoint(pt));
                    nativeSent = true;
                }
            }
            catch { }

            Task.Delay(nativeSent ? 450 : 0).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    IntPtr fg = GetForegroundWindow();
                    var sb = new System.Text.StringBuilder(64);
                    GetClassName(fg, sb, 64);
                    bool nativeMenuVisible = sb.ToString() == "#32768";
                    if (nativeMenuVisible) { _suppressDeactivate = false; SlideUp(); }
                    else { _suppressDeactivate = false; }
                });
            });
        }

        // ── Хелпери ───────────────────────────────────────────────────
        static TrayIcon IconWithHwnd(TrayIcon src, IntPtr hwnd) => new TrayIcon
        {
            AppHwnd = hwnd,
            AppID = src.AppID,
            CallbackMsg = src.CallbackMsg,
            ProcessName = src.ProcessName,
            ProcessId = src.ProcessId,
            Icon = src.Icon
        };

        void SendTrayV3(TrayIcon icon, uint msg, POINT pt)
        {
            if (!IsWindow(icon.AppHwnd)) return;
            PostMessage(icon.AppHwnd, icon.CallbackMsg, (IntPtr)icon.AppID, (IntPtr)msg);
        }

        void SendTrayV4(TrayIcon icon, uint msg, POINT pt)
        {
            if (!IsWindow(icon.AppHwnd)) return;
            IntPtr wParam = new IntPtr(unchecked((int)((pt.Y << 16) | (pt.X & 0xFFFF))));
            IntPtr lParam = new IntPtr(unchecked((int)((icon.AppID << 16) | (msg & 0xFFFF))));
            PostMessage(icon.AppHwnd, icon.CallbackMsg, wParam, lParam);
        }

        static IntPtr MakePoint(POINT pt)
            => new IntPtr(unchecked((int)((pt.Y << 16) | (pt.X & 0xFFFF))));

        public Action OnClosed { get; set; }

        void Window_Deactivated(object sender, EventArgs e)
        {
            if (!_suppressDeactivate && _isOpen && !_animating)
                SlideUp(() => Dispatcher.Invoke(() => OnClosed?.Invoke()));
        }
    }
}