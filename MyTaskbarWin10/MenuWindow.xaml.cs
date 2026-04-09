using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MyTaskbar
{
    public partial class MenuWindow : Window
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, MouseHookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        delegate IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

        const int WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_RBUTTONDOWN = 0x0204;
        const int WM_MBUTTONDOWN = 0x0207;

        IntPtr _mouseHook;
        MouseHookProc _mouseHookProc;

        void InstallMouseHook()
        {
            if (_mouseHook != IntPtr.Zero) return;
            _mouseHookProc = MouseHookCallback;
            using (var p = Process.GetCurrentProcess())
            using (var m = p.MainModule)
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(m.ModuleName), 0);
        }

        void UninstallMouseHook()
        {
            if (_mouseHook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                bool isClick = msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN;
                if (isClick && IsVisible)
                {
                    var hs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    POINT p = hs.pt;

                    bool overUs = IsOver(this, p)
                        || (PreviewWindow != null && PreviewWindow.IsVisible && IsOver(PreviewWindow, p))
                        || (TaskbarWindow != null && TaskbarWindow.IsVisible && IsOver(TaskbarWindow, p));

                    if (!overUs)
                    {
                        Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Normal,
                            new Action(() =>
                            {
                                try
                                {
                                    if (_powerExpanded)
                                        CollapsePowerButtons();
                                    else
                                        HideAnimated();
                                }
                                catch { }
                            }));
                    }
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }

        const byte VK_LWIN = 0x5B;
        const byte VK_S = 0x53;
        const uint KEYEVENTF_KEYUP = 0x0002;

        [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        { [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm); }

        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [Flags] enum SIIGBF : int { ResizeToFit = 0x00, BiggerSizeOk = 0x01, IconOnly = 0x04, NoOverlay = 0x40 }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }
        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x000;

        // ══════════════════════════════════════════════════════════════
        //  Поля
        // ══════════════════════════════════════════════════════════════

        static readonly string ProgramsFolder = Path.Combine(
    AppDomain.CurrentDomain.BaseDirectory, "Programs");
static readonly string GamesFolder = Path.Combine(
    AppDomain.CurrentDomain.BaseDirectory, "Games");

        const double TileIconSize = 32;
        const double TileHeight = 48;
        const double TileMargin = 2;

        List<(string Name, string Path, BitmapSource Icon)> _tileItems = new List<(string, string, BitmapSource)>();

        DateTime _hiddenAt = DateTime.MinValue;
        DateTime _lastClickInside = DateTime.MinValue;

        public const int HideGraceMs = 300;
        public bool JustHidden => (DateTime.UtcNow - _hiddenAt).TotalMilliseconds < HideGraceMs;

        public event Action<bool> MenuVisibilityChanged;

        public Window PreviewWindow { get; set; }
        public Window TaskbarWindow { get; set; }

        static bool IsOver(Window w, POINT p)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return false;
                if (!GetWindowRect(h, out RECT r)) return false;
                return p.x >= r.left && p.x <= r.right && p.y >= r.top && p.y <= r.bottom;
            }
            catch { return false; }
        }

        // ══════════════════════════════════════════════════════════════
        //  Анимация главного окна
        // ══════════════════════════════════════════════════════════════

        public const double ANIM_SHOW_MS = 180;
        public const double ANIM_HIDE_MS = 130;
        const double SLIDE_PX = 12;
        bool _isAnimating = false;

        const int GWL_EXSTYLE_MW = -20;
        const int WS_EX_TRANSPARENT_MW = 0x00000020;
        const int WS_EX_NOACTIVATE_MW = 0x08000000;
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] static extern int GetWindowLongMW(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] static extern int SetWindowLongMW(IntPtr hwnd, int index, int val);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLongMW(hwnd, GWL_EXSTYLE_MW);
            style &= ~WS_EX_TRANSPARENT_MW;
            style &= ~WS_EX_NOACTIVATE_MW;
            SetWindowLongMW(hwnd, GWL_EXSTYLE_MW, style);
        }

        public MenuWindow()
        {
            InitializeComponent();

            Topmost = true;
            RenderTransform = new TranslateTransform(0, 0);
            RenderTransformOrigin = new Point(0.5, 0);

            PreviewMouseDown += (s, e) =>
            {
                _lastClickInside = DateTime.UtcNow;

                if (_powerExpanded)
                {
                    var clicked = e.OriginalSource as DependencyObject;
                    bool onPowerArea = false;

                    foreach (var btn in new[] { BtnPower, BtnRestart, BtnSleep })
                    {
                        if (clicked != null && (clicked == btn || IsVisualChild(clicked, btn)))
                        {
                            onPowerArea = true;
                            break;
                        }
                    }

                    if (!onPowerArea)
                        CollapsePowerButtons();
                }
            };

            Deactivated += (s, e) =>
            {
                if ((DateTime.UtcNow - _lastClickInside).TotalMilliseconds < 200) return;
                HideAnimated();
            };

            Loaded += (s, e) =>
            {
                ApplyAcrylic();
                BuildAll();
                PositionWindow();
                if (Background == null || Background == Brushes.Transparent)
                    Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            };
        }

        static bool IsVisualChild(DependencyObject child, DependencyObject parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        void ApplyAcrylic()
        {
            try { MyTaskbar.Helpers.AcrylicHelper.EnableAcrylic(this, 0x701A1A2E); }
            catch { try { MyTaskbar.Helpers.AcrylicHelper.EnableBlur(this, 0x70202030); } catch { } }
        }

        void PositionWindow()
        {
            UpdateLayout();
            var src = PresentationSource.FromVisual(this);
            double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sw = SystemParameters.PrimaryScreenWidth / dpi;
            Left = Math.Round((sw - ActualWidth) / 2);
            Top = 40 + 8;
        }

        public void ShowMenu()
        {
            if (_isAnimating) StopAllAnimations();

            PositionWindow();

            var tt = GetTranslate();
            tt.Y = -SLIDE_PX;
            Opacity = 0;

            Topmost = true;
            Show();
            Topmost = true;
            Activate();

            InstallMouseHook();
            MenuVisibilityChanged?.Invoke(true);

            _isAnimating = true;
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 };
            var dur = TimeSpan.FromMilliseconds(ANIM_SHOW_MS);

            var aOpacity = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
            var aSlide = new DoubleAnimation(-SLIDE_PX, 0, dur) { EasingFunction = ease };
            aSlide.Completed += (s, e) => { _isAnimating = false; tt.Y = 0; };

            BeginAnimation(OpacityProperty, aOpacity);
            tt.BeginAnimation(TranslateTransform.YProperty, aSlide);
        }

        public void HideAnimated()
        {
            if (!IsVisible) return;

            if (_powerExpanded)
                CollapsePowerImmediate();

            _hiddenAt = DateTime.UtcNow;
            UninstallMouseHook();

            if (_isAnimating) StopAllAnimations();

            _isAnimating = true;
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 4 };
            var dur = TimeSpan.FromMilliseconds(ANIM_HIDE_MS);

            double fromOpacity = Opacity;
            double fromY = GetTranslate().Y;

            var aOpacity = new DoubleAnimation(fromOpacity, 0, dur) { EasingFunction = ease };
            var aSlide = new DoubleAnimation(fromY, -SLIDE_PX, dur) { EasingFunction = ease };
            aOpacity.Completed += (s, e) =>
            {
                _isAnimating = false;
                BeginAnimation(OpacityProperty, null);
                GetTranslate().BeginAnimation(TranslateTransform.YProperty, null);
                GetTranslate().Y = -SLIDE_PX;
                Opacity = 0;
                Hide();
                MenuVisibilityChanged?.Invoke(false);
            };

            BeginAnimation(OpacityProperty, aOpacity);
            GetTranslate().BeginAnimation(TranslateTransform.YProperty, aSlide);
        }

        void StopAllAnimations()
        {
            BeginAnimation(OpacityProperty, null);
            GetTranslate().BeginAnimation(TranslateTransform.YProperty, null);
            _isAnimating = false;
        }

        TranslateTransform GetTranslate()
        {
            if (RenderTransform is TranslateTransform tt) return tt;
            var t = new TranslateTransform(0, 0);
            RenderTransform = t;
            RenderTransformOrigin = new Point(0.5, 0);
            return t;
        }

        public void Refresh() => BuildAll();

        // ══════════════════════════════════════════════════════════════
        //  Питание — Restart и Sleep выезжают вправо прямо в тулбаре
        // ══════════════════════════════════════════════════════════════

        // true = кнопки Restart и Sleep сейчас открыты
        bool _powerExpanded = false;

        // Итоговая ширина панели: 2 кнопки по 36px = 72
        const double PowerExpandedWidth = 72.0;

        void Power_Click(object sender, RoutedEventArgs e)
        {
            if (_powerExpanded)
            {
                // Второй клик — shutdown
                CollapsePowerImmediate();
                HideAnimated();
                DoShutdown();
            }
            else
            {
                // Первый клик — раскрыть Restart и Sleep
                ExpandPowerButtons();
            }
        }

        void ExpandPowerButtons()
        {
            PowerExpandPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
            PowerExpandPanel.BeginAnimation(UIElement.OpacityProperty, null);

            // Сброс трансформ на кнопках
            BtnRestart.RenderTransform = new TranslateTransform(-PowerExpandedWidth, 0);
            BtnSleep.RenderTransform = new TranslateTransform(-PowerExpandedWidth, 0);

            PowerExpandPanel.Width = PowerExpandedWidth;
            PowerExpandPanel.Opacity = 1;

            _powerExpanded = true;

            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 };
            var dur = TimeSpan.FromMilliseconds(220);

            // Кнопки выезжают слева направо
            var animX1 = new DoubleAnimation(-PowerExpandedWidth, 0, dur) { EasingFunction = ease };
            var animX2 = new DoubleAnimation(-PowerExpandedWidth, 0, dur)
            {
                EasingFunction = ease,
                BeginTime = TimeSpan.FromMilliseconds(30) // небольшой стаггер
            };

            // Прозрачность панели
            var animO = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 3 }
            };

            BtnRestart.RenderTransform.BeginAnimation(TranslateTransform.XProperty, animX1);
            BtnSleep.RenderTransform.BeginAnimation(TranslateTransform.XProperty, animX2);
            PowerExpandPanel.BeginAnimation(UIElement.OpacityProperty, animO);
        }

        void CollapsePowerButtons()
        {
            _powerExpanded = false;

            var ease = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 4 };
            var dur = TimeSpan.FromMilliseconds(160);

            var animX1 = new DoubleAnimation(0, -PowerExpandedWidth, dur) { EasingFunction = ease };
            var animX2 = new DoubleAnimation(0, -PowerExpandedWidth, dur) { EasingFunction = ease };

            var animO = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 3 }
            };

            // По завершении — схлопываем ширину
            animO.Completed += (s, e) =>
            {
                PowerExpandPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
                PowerExpandPanel.Width = 0;
                PowerExpandPanel.Opacity = 0;
                BtnRestart.RenderTransform = new TranslateTransform(-PowerExpandedWidth, 0);
                BtnSleep.RenderTransform = new TranslateTransform(-PowerExpandedWidth, 0);
            };

            BtnRestart.RenderTransform?.BeginAnimation(TranslateTransform.XProperty, animX1);
            BtnSleep.RenderTransform?.BeginAnimation(TranslateTransform.XProperty, animX2);
            PowerExpandPanel.BeginAnimation(UIElement.OpacityProperty, animO);
        }

        void CollapsePowerImmediate()
        {
            _powerExpanded = false;
            PowerExpandPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
            PowerExpandPanel.BeginAnimation(UIElement.OpacityProperty, null);
            PowerExpandPanel.Width = 0;
            PowerExpandPanel.Opacity = 0;
            BtnRestart.RenderTransform = new TranslateTransform(-PowerExpandedWidth, 0);
            BtnSleep.RenderTransform = new TranslateTransform(-PowerExpandedWidth, 0);
        }

        void Restart_Click(object sender, RoutedEventArgs e)
        {
            CollapsePowerImmediate();
            DoRestart();
        }

        void Sleep_Click(object sender, RoutedEventArgs e)
        {
            CollapsePowerImmediate();
            Hide();
            DoSleep();
        }

        // ══════════════════════════════════════════════════════════════
        //  Поиск
        // ══════════════════════════════════════════════════════════════

        void Search_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            System.Threading.Tasks.Task.Delay(150).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                        keybd_event(VK_S, 0, 0, UIntPtr.Zero);
                        keybd_event(VK_S, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    }
                    catch { }
                });
            });
        }

        // ══════════════════════════════════════════════════════════════
        //  Плитки
        // ══════════════════════════════════════════════════════════════

        void BuildAll()
        {
            _tileItems.Clear();
            FillPanel(PanelPrograms, ProgramsFolder);
            FillPanel(PanelGames, GamesFolder);
            UpdateLayout();
        }

        void FillPanel(UniformGrid panel, string folder)
        {
            panel.Children.Clear();
            if (!Directory.Exists(folder)) return;
            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.lnk")
                    .Concat(Directory.GetFiles(folder, "*.url")).ToArray();
            }
            catch { return; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                try
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    string resolved = Helpers.IconHelper.ResolveShortcut(file);
                    BitmapSource ico = null;
                    if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                        ico = Helpers.IconHelper.GetIconFromExe(resolved, 36);
                    if (ico == null) ico = ShellIconNoOverlay(file) ?? ShellIconFallback(file);
                    _tileItems.Add((name, file, ico));
                    panel.Children.Add(MakeTile(name, ico, file));
                }
                catch { }
            }
        }

        Button MakeTile(string name, BitmapSource ico, string filePath)
        {
            var sp = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (ico != null)
            {
                var img = new Image
                {
                    Source = ico,
                    Width = TileIconSize,
                    Height = TileIconSize,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                sp.Children.Add(img);
            }
            else
            {
                sp.Children.Add(new TextBlock
                {
                    Text = name.Length > 0 ? name[0].ToString().ToUpper() : "?",
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Width = TileIconSize,
                    TextAlignment = TextAlignment.Center
                });
            }

            var btn = new Button
            {
                Height = TileHeight,
                Margin = new Thickness(TileMargin),
                Cursor = Cursors.Hand,
                ToolTip = name,
                Tag = filePath,
                Content = sp
            };

            var bf = new FrameworkElementFactory(typeof(Border)); bf.Name = "bd";
            bf.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
            bf.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)));
            bf.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bf.SetValue(Border.CornerRadiusProperty, new CornerRadius(0));
            bf.SetValue(Border.SnapsToDevicePixelsProperty, true);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bf.AppendChild(cp);
            var tpl = new ControlTemplate(typeof(Button)) { VisualTree = bf };

            var hov = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hov.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)), "bd"));
            hov.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x88, 0x4A, 0x9E, 0xFF)), "bd"));
            tpl.Triggers.Add(hov);
            var prs = new Trigger { Property = Button.IsPressedProperty, Value = true };
            prs.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x55, 0x4A, 0x9E, 0xFF)), "bd"));
            tpl.Triggers.Add(prs);
            btn.Template = tpl;

            btn.Click += (s, ev) =>
            {
                try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); }
                catch (Exception ex)
                {
                    MessageBox.Show("Launch error:\n" + filePath + "\n\n" + ex.Message,
                        "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                Hide();
            };
            return btn;
        }

        // ══════════════════════════════════════════════════════════════
        //  Иконки
        // ══════════════════════════════════════════════════════════════

        static BitmapSource ShellIconNoOverlay(string path)
        {
            try
            {
                var iid = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory factory);
                if (factory == null) return null;
                var flagVariants = new[]
                {
                    (SIIGBF)((int)SIIGBF.IconOnly    | (int)SIIGBF.BiggerSizeOk),
                    (SIIGBF)((int)SIIGBF.IconOnly    | (int)SIIGBF.BiggerSizeOk | (int)SIIGBF.NoOverlay),
                    (SIIGBF)((int)SIIGBF.ResizeToFit | (int)SIIGBF.BiggerSizeOk),
                    (SIIGBF)((int)SIIGBF.ResizeToFit | (int)SIIGBF.BiggerSizeOk | (int)SIIGBF.NoOverlay),
                    SIIGBF.IconOnly, SIIGBF.ResizeToFit,
                };
                foreach (var flags in flagVariants)
                {
                    int hr = factory.GetImage(new SIZE { cx = 48, cy = 48 }, flags, out IntPtr hbm);
                    if (hr != 0 || hbm == IntPtr.Zero) continue;
                    BitmapSource bmp = null;
                    try
                    {
                        bmp = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero,
                            Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        if (bmp != null && bmp.CanFreeze) bmp.Freeze();
                    }
                    finally { DeleteObject(hbm); }
                    if (bmp != null && !IsBlankBitmap(bmp)) return bmp;
                }
                return null;
            }
            catch { return null; }
        }

        static BitmapSource ShellIconFallback(string path)
        {
            try
            {
                var info = new SHFILEINFO();
                IntPtr r = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_LARGEICON);
                if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                {
                    BitmapSource bmp = null;
                    try
                    {
                        bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon,
                            Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        if (bmp != null && bmp.CanFreeze) bmp.Freeze();
                    }
                    finally { DestroyIcon(info.hIcon); }
                    if (bmp != null && !IsBlankBitmap(bmp)) return bmp;
                }
            }
            catch { }
            try
            {
                const uint SHGFI_USEFILEATTRIBUTES = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
                var info2 = new SHFILEINFO();
                IntPtr r2 = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref info2,
                    (uint)Marshal.SizeOf(info2), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
                if (r2 != IntPtr.Zero && info2.hIcon != IntPtr.Zero)
                {
                    BitmapSource bmp = null;
                    try
                    {
                        bmp = Imaging.CreateBitmapSourceFromHIcon(info2.hIcon,
                            Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        if (bmp != null && bmp.CanFreeze) bmp.Freeze();
                    }
                    finally { DestroyIcon(info2.hIcon); }
                    if (bmp != null && !IsBlankBitmap(bmp)) return bmp;
                }
            }
            catch { }
            return null;
        }

        static bool IsBlankBitmap(BitmapSource bmp)
        {
            try
            {
                if (bmp == null || bmp.PixelWidth == 0 || bmp.PixelHeight == 0) return true;
                var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    bmp, PixelFormats.Bgra32, null, 0);
                int w = Math.Min(conv.PixelWidth, 16), h = Math.Min(conv.PixelHeight, 16), stride = w * 4;
                byte[] px = new byte[h * stride];
                conv.CopyPixels(new Int32Rect(0, 0, w, h), px, stride, 0);
                int n = 0;
                for (int i = 0; i < px.Length; i += 4)
                    if (px[i + 3] > 32 && !(px[i + 2] > 240 && px[i + 1] > 240 && px[i] > 240)) n++;
                return n < 5;
            }
            catch { return false; }
        }

        // ══════════════════════════════════════════════════════════════
        //  Хелперы
        // ══════════════════════════════════════════════════════════════

        static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var p = VisualTreeHelper.GetParent(child);
            return p == null ? null : p is T t ? t : FindVisualParent<T>(p);
        }

        static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var c = VisualTreeHelper.GetChild(parent, i);
                if (c is T t) return t;
                var r = FindVisualChild<T>(c); if (r != null) return r;
            }
            return null;
        }

        void Settings_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start("ms-settings:"); } catch { }
            Hide();
        }

        void DoSleep()
        {
            try
            {
                Process.Start(new ProcessStartInfo("rundll32.exe",
                    "powrprof.dll,SetSuspendState 0,1,0")
                { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Sleep error:\n" + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void DoShutdown()
        {
            try
            {
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 0")
                { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Shutdown error:\n" + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void DoRestart()
        {
            try
            {
                Process.Start(new ProcessStartInfo("shutdown", "/r /t 0")
                { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Restart error:\n" + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}