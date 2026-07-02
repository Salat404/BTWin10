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
// MainWindow.Buttons.cs — Button setup, active window tracker, button states
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // ACTIVE WINDOW TRACKER [STAB-6]
        // ═════════════════════════════════════════════════════════════════════
        void StartActiveWindowTracker()
        {
            _activeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _activeTimer.Tick += (s, e) =>
            {
                // UpdateWindowGroups уже имеет Interlocked-защиту внутри
                SafeRun(UpdateWindowGroups, "UpdateWindowGroups");
                // [STAB-6] UpdateButtonStates — отдельный флаг
                if (System.Threading.Interlocked.CompareExchange(ref _btnStateBusyInt, 1, 0) == 0)
                {
                    try { SafeRun(UpdateButtonStates, "UpdateButtonStates"); }
                    finally { System.Threading.Interlocked.Exchange(ref _btnStateBusyInt, 0); }
                }
            };
            _activeTimer.Start();
        }

        void UpdateGroupBadge(AppGroup g)
        {
            if (g?.Button == null) return;

            // Когда все окна закрыты (pinned приложение без окон) — сбрасываем тултип на базовое имя
            if (g.Hwnds.Count == 0)
            {
                string baseTitle = g.PinnedTooltip ?? g.ExeName;
                if (g.LastTitle != baseTitle) { if (g.Button.ToolTip is ToolTip tt0) tt0.Content = baseTitle; else g.Button.ToolTip = baseTitle; g.LastTitle = baseTitle; }
                return;
            }
            try
            {
                // [FIX-D3DPROXY] Перебираем все hwnd группы и выбираем лучший заголовок:
                // берём первый заголовок, который не совпадает с именем window-class (технический)
                // и не содержит только имя exe. Это гарантирует что "Team Fortress 2 - Direct3D"
                // будет выбран вместо "D3DProxyWindow" даже если proxy-окно стоит первым.
                
                // [TELEGRAM-FIX] Для Telegram используем имя приложения вместо названия окна
                // которое содержит имя аккаунта и количество сообщений
                if (g.ExeName.ToLower().Contains("telegram"))
                {
                    string title = g.Hwnds.Count == 1 ? "Telegram" : $"Telegram ({g.Hwnds.Count} windows)";
                    if (title != g.LastTitle) { if (g.Button.ToolTip is ToolTip tt1) tt1.Content = title; else g.Button.ToolTip = title; g.LastTitle = title; }
                    return;
                }
                
                string dn = null;
                foreach (IntPtr hwnd in g.Hwnds)
                {
                    string t = SafeGetWindowText(hwnd);
                    if (string.IsNullOrEmpty(t)) continue;
                    // Предпочитаем заголовок который не является именем window-class
                    string cls = GetWindowClass(hwnd);
                    if (!string.IsNullOrEmpty(cls) && IgnoredWindowClasses.Contains(cls)) continue;
                    // Первый подходящий — лучший
                    dn = t;
                    break;
                }
                if (string.IsNullOrEmpty(dn)) dn = SafeGetWindowText(g.Hwnds[0]);
                if (string.IsNullOrEmpty(dn)) dn = g.LastTitle ?? g.ExeName;
                string title2 = g.Hwnds.Count == 1 ? dn : $"{dn} ({g.Hwnds.Count} windows)";
                if (title2 != g.LastTitle) { if (g.Button.ToolTip is ToolTip tt1) tt1.Content = title2; else g.Button.ToolTip = title2; g.LastTitle = title2; }
            }
            catch { }
        }

        void UpdateButtonIcon(Button btn, BitmapSource icon)
        { try { if (btn?.Content is System.Windows.Controls.Image img) img.Source = icon; } catch { } }

        void UpdateButtonStates()
        {
            IntPtr active = GetForegroundWindow();
            IntPtr myHwnd; try { myHwnd = new WindowInteropHelper(this).Handle; } catch { myHwnd = IntPtr.Zero; }
            
            // [FIX-DESKTOP-EARLY] Проверяем процесс ДО сохранения в _lastForegroundWindow
            string activeProcessName = "";
            if (active != myHwnd && active != IntPtr.Zero)
            {
                try
                {
                    GetWindowThreadProcessId(active, out uint pid);
                    if (pid != 0)
                    {
                        if (!_pidNameCache.TryGetValue(pid, out activeProcessName))
                        {
                            try { activeProcessName = Process.GetProcessById((int)pid).ProcessName?.ToLowerInvariant() ?? ""; }
                            catch { activeProcessName = ""; }
                        }
                    }
                }
                catch { activeProcessName = ""; }
            }
            
            // Если процесс explorer.exe при drag&drop — не обновляем _lastForegroundWindow
            // и не будем считать его активным
            bool isDesktopDragOp = activeProcessName == "explorer" && 
                                   (GetKeyState(0x01) < 0 || GetKeyState(0x02) < 0); // Left или Right mouse button pressed
            
            if (!isDesktopDragOp && active != myHwnd && active != IntPtr.Zero)
            {
                _lastForegroundWindow = active;
                // [FIX-NOACTIVATE-TOGGLE] Если реальный OS-фокус перешёл на ДРУГОЕ окно
                // (не то, что мы сами последним активировали через таскбар) — сбрасываем
                // флаг, чтобы следующий клик по прежней кнопке снова открывал, а не сворачивал.
                if (_lastActivatedByTaskbar != IntPtr.Zero && _lastActivatedByTaskbar != active)
                    _lastActivatedByTaskbar = IntPtr.Zero;
            }

            if (active != myHwnd && active != IntPtr.Zero)
            {
                var sbCls = new StringBuilder(128); GetWindowClassName(active, sbCls, sbCls.Capacity); string cls = sbCls.ToString();
                
                // [FIX-DESKTOP] Фильтруем Desktop/Progman
                if (cls == "Progman")
                {
                    active = IntPtr.Zero;
                }
                else if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                {
                    active = IntPtr.Zero;
                }
                // [FIX-DRAG-DETECTION] Если Explorer с нажатой мышью = drag операция
                else if (isDesktopDragOp)
                {
                    active = IntPtr.Zero;
                }
                
                if (active != IntPtr.Zero && (cls == "Windows.UI.Core.CoreWindow" || cls == "ApplicationFrameInputSinkWindow"))
                    foreach (var g in _groups.Values)
                        if (IsUwpAppName(g.ExeName) && g.Hwnds.Count > 0)
                        { IntPtr parent = active; for (int depth = 0; depth < 5; depth++) { parent = GetParent(parent); if (parent == IntPtr.Zero) break; if (g.Hwnds.Contains(parent)) { _lastForegroundWindow = parent; break; } } }
            }

            string activeExe = "";
            if (active != myHwnd && active != IntPtr.Zero)
            {
                GetWindowThreadProcessId(active, out uint pid);
                if (pid != 0 && !_pidNameCache.TryGetValue(pid, out activeExe))
                    try { activeExe = Process.GetProcessById((int)pid).ProcessName?.ToLowerInvariant() ?? ""; } catch { activeExe = ""; }
                if (ProcessAliases.TryGetValue(activeExe, out string al)) activeExe = al;

                if (string.Equals(activeExe, "applicationframehost", StringComparison.OrdinalIgnoreCase) || activeExe == "")
                {
                    // [STAB-4] SafeGetWindowText
                    string activeTitle = SafeGetWindowText(active);
                    string uwpName = ResolveUwpAppName(activeTitle);
                    if (!string.IsNullOrEmpty(uwpName)) activeExe = uwpName;
                    if (string.IsNullOrEmpty(activeExe) || activeExe == "applicationframehost")
                        foreach (var kvp2 in _groups) if (IsUwpAppName(kvp2.Key) && kvp2.Value.Hwnds.Contains(active)) { activeExe = kvp2.Key; break; }
                }
                else
                {
                    if (DirectUwpProcesses.Contains(activeExe) && activeExe == "microsoft.photos") activeExe = "photos";
                    var sbCls2 = new StringBuilder(128); GetWindowClassName(active, sbCls2, sbCls2.Capacity); string cls2 = sbCls2.ToString();
                    if (cls2 == "Windows.UI.Core.CoreWindow" || cls2 == "ApplicationFrameInputSinkWindow")
                        foreach (var kvp2 in _groups)
                            if (IsUwpAppName(kvp2.Key) && kvp2.Value.Hwnds.Count > 0)
                            { IntPtr par = active; for (int depth = 0; depth < 5; depth++) { par = GetParent(par); if (par == IntPtr.Zero) break; if (kvp2.Value.Hwnds.Contains(par)) { activeExe = kvp2.Key; break; } } if (!string.IsNullOrEmpty(activeExe)) break; }
                }
            }

            foreach (var kvp in _groups)
            {
                try
                {
                    var g = kvp.Value;
                    if (_removingButtons.Contains(g.Button)) continue;
                    bool running = g.Hwnds.Count > 0;
                    // [PER-WINDOW] Per-window groups match by hwnd; normal groups match by exe name
                    bool isActive;
                    if (running && kvp.Key.Contains(":") && active != IntPtr.Zero)
                        isActive = g.Hwnds.Contains(active);
                    else
                        // [FIX-DESKTOP-v2] Если activeExe пустая (рабочий стол активен), не подсвечиваем приложение
                        isActive = !string.IsNullOrEmpty(activeExe) && running && string.Equals(kvp.Key, activeExe, StringComparison.OrdinalIgnoreCase);
                    string state = isActive ? "Active" : running ? "Running" : "";
                    if (_buttonStateCache.TryGetValue(g.Button, out string old) && old == state) continue;
                    _buttonStateCache[g.Button] = state; ApplyButtonState(g.Button, state);
                }
                catch { }
            }
        }

        Button CreateAppButton(string tooltip, BitmapSource icon, int iconSize = 24)
        {
            // [UI-SCALE-ICON] Иконка уже масштабируется через LayoutTransform MainPanel,
            // поэтому размер иконки задаём в базовых DIP (не умножаем на _uiScale) —
            // ScaleTransform на родительском элементе сделает это автоматически.
            var tipObj = new ToolTip { Content = tooltip };
            var btn = new Button { Style = TryFindResource("IconButton") as Style, ToolTip = tipObj };
            btn.RenderTransform = new TranslateTransform(0, 0);
            btn.RenderTransformOrigin = new Point(0.5, 0.5);
            if (icon != null)
            {
                var img = new System.Windows.Controls.Image { Source = icon, Width = iconSize, Height = iconSize, Stretch = Stretch.Uniform, SnapsToDevicePixels = true, UseLayoutRounding = true };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                btn.Content = img;
            }
            else
                btn.Content = new TextBlock { Text = (tooltip?.Length > 0 ? tooltip.Substring(0, 1) : "?").ToUpper(), Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            return btn;
        }

    }
}
