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
// MainWindow.Fullscreen.cs — Edge reveal, Fullscreen detection, FPS cursor blocker
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // EDGE REVEAL / FULLSCREEN
        // ═════════════════════════════════════════════════════════════════════
        void StartEdgeRevealWatcher()
        {
            // [PERF-1] 50ms достаточно для edge-reveal (было 16ms = 60 тиков/сек на UI-потоке)
            _edgeRevealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _edgeRevealTimer.Tick += (s, e) => SafeRun(CheckEdgeReveal, "CheckEdgeReveal");
            _edgeRevealTimer.Start();
        }

        void CheckEdgeReveal()
        {
            // [EDGE-FIX] Если панель уже видима (показана через attention или обычно) —
            // edge reveal не нужен вообще. Предотвращает повторный тригер при наведении
            // курсора на верхний/нижний край видимой панели.
            if (Visibility == Visibility.Visible || _taskbarAnimating)
            { _edgeRevealPending = false; _edgeCursorEnteredAt = DateTime.MinValue; return; }
            if (!_isHiddenByFullscreen)
            { _edgeRevealPending = false; _edgeCursorEnteredAt = DateTime.MinValue; return; }
            if (!GetCursorPos(out POINT cp)) return;
            try
            {
                var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
                if (GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) == 0)
                { _edgeCursorEnteredAt = DateTime.MinValue; _edgeRevealPending = false; return; }
            }
            catch { }
            // [FIX-2.9] _currentDpiScale кэшируется при WM_DPICHANGED, не пересчитываем каждые 50мс
            double dpi = _currentDpiScale;
            double taskbarLeft = Left * dpi;
            double taskbarRight = taskbarLeft + ActualWidth * dpi;
            var (monLeft2, monTop2, monWidth2, monHeight2) = GetPrimaryMonitorRectDip();
            bool atEdge = _isBottom
                ? cp.y >= (int)((monTop2 + monHeight2) * dpi) - 2 && cp.x >= taskbarLeft && cp.x <= taskbarRight
                : cp.y <= (int)(monTop2 * dpi) + 2 && cp.x >= taskbarLeft && cp.x <= taskbarRight;
            if (atEdge)
            {
                if (_edgeCursorEnteredAt == DateTime.MinValue) _edgeCursorEnteredAt = DateTime.UtcNow;
                // [ATTENTION-HIDE] Панель уже показана из-за attention — edge reveal не нужен,
                // иначе _isHiddenByFullscreen сбросится и панель не спрячется после.
                // [EDGE-DELAY] Show taskbar only after holding cursor at edge for _revealDelayMs
                if (!_edgeRevealPending && !_shownByAttention
                    && (DateTime.UtcNow - _edgeCursorEnteredAt).TotalMilliseconds >= _revealDelayMs)
                {
                    _edgeRevealPending = true;
                    _isHiddenByFullscreen = false;
                    _fullscreenConfirmCount = 0;
                    _notFullscreenConfirmCount = 0;
                    bool fps = IsCursorCapturedByFpsGame();
                    // [FULLSCREEN-AUTOHIDE] Панель показана через edge reveal — запоминаем,
                    // чтобы авто-скрыть если курсор уйдёт через 400мс
                    _shownByEdgeReveal = true;
                    ShowTaskbar(animate: true, onComplete: () =>
                    {
                        // [GAME-4] Edge reveal в FPS — блокер сразу после появления панели
                        if (fps) ShowFullscreenBlocker();
                        // [FULLSCREEN-AUTOHIDE] После анимации — запускаем таймер скрытия
                        if (!IsMouseOver) StartFullscreenNoMouseHideTimer();
                    });
                }
            }
            else { _edgeCursorEnteredAt = DateTime.MinValue; _edgeRevealPending = false; }
        }

        void StartFullscreenWatcher()
        {
            _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            // [STAB-6] Флаг реентрантности для fullscreen-чека
            _fullscreenTimer.Tick += (s, e) =>
            {
                if (System.Threading.Interlocked.CompareExchange(ref _fsBusyInt, 1, 0) != 0) return;
                try { SafeRun(CheckFullscreen, "CheckFullscreen"); }
                finally { System.Threading.Interlocked.Exchange(ref _fsBusyInt, 0); }
            };
            _fullscreenTimer.Start();
        }

        void CheckFullscreen()
        {
            if (_taskbarAnimating) return;
            if (!_fullscreenAutoHide) return; // user disabled auto-hide in settings
            // [FIX-2.6] Единый метод вместо дублирования
            bool anyOwnVisible = AnyOwnPopupVisible();
            bool fs = IsForegroundFullscreen();
            if (fs && _menuWindow != null && _menuWindow.IsVisible)
            {
                IntPtr fg = GetForegroundWindow();
                // [FIX-2.2] Используем кэшированные HWND вместо new WindowInteropHelper
                IntPtr hMenu = _menuHwnd, hTaskbar = _myHwnd;
                if (fg != hMenu && fg != hTaskbar && fg == _lastFullscreenHwnd && !IsInOwnActivityGrace())
                    _menuWindow.HideAnimated();
            }
            if (anyOwnVisible) { _fullscreenConfirmCount = 0; _notFullscreenConfirmCount = 0; MarkOwnActivity(); return; }
            if (IsCursorOverOwnWindows()) { _fullscreenConfirmCount = 0; _notFullscreenConfirmCount = 0; MarkOwnActivity(); return; }
            if (IsInOwnActivityGrace()) { _fullscreenConfirmCount = 0; _notFullscreenConfirmCount = 0; return; }

            // [GAME-9] Если пользователь открыл окно поверх игры — ждём пока игра снова не станет
            // foreground. Как только игра вернула фокус (fullscreen и fg == lastFullscreenHwnd) —
            // сбрасываем флаг и возвращаем блокер, чтобы курсор снова не уходил в игру.
            if (_appOpenedOverFullscreen)
            {
                if (fs)
                {
                    IntPtr fg = GetForegroundWindow();
                    if (fg == _lastFullscreenHwnd)
                    {
                        // Игра снова в фокусе — восстанавливаем блокер
                        _appOpenedOverFullscreen = false;
                        Debug.WriteLine("[MyTaskbar] Game regained focus — restoring blocker");
                        if (Visibility == Visibility.Visible)
                            ShowFullscreenBlocker();
                    }
                }
                else
                {
                    // Игра вышла из fullscreen пока окно было открыто — сбрасываем флаг
                    _appOpenedOverFullscreen = false;
                }
                return;
            }

            if (fs)
            {
                _notFullscreenConfirmCount = 0;
                if (!_isHiddenByFullscreen)
                {
                    _fullscreenConfirmCount++;
                    if (_fullscreenConfirmCount >= FULLSCREEN_CONFIRM_TICKS)
                    { _fullscreenConfirmCount = 0; _isHiddenByFullscreen = true; HideTaskbar(animate: true); }
                }
            }
            else
            {
                _fullscreenConfirmCount = 0;
                if (_isHiddenByFullscreen)
                {
                    // [MONITOR-FIX] Foreground больше не fullscreen (пользователь кликнул
                    // на второй монитор), но fullscreen-окно ещё покрывает основной монитор.
                    // Win10: панель остаётся скрытой, пока игра физически на экране.
                    if (IsWindowCoveringPrimaryMonitor(_lastFullscreenHwnd))
                    {
                        _notFullscreenConfirmCount = 0;
                        return; // остаёмся скрытыми
                    }
                    _notFullscreenConfirmCount++;
                    if (_notFullscreenConfirmCount >= NOT_FULLSCREEN_CONFIRM_TICKS)
                    { _notFullscreenConfirmCount = 0; _isHiddenByFullscreen = false; ShowTaskbar(animate: true); }
                }
            }
        }

        static string GetWindowClass(IntPtr hwnd)
        {
            try { var sb = new StringBuilder(256); GetWindowClassName(hwnd, sb, sb.Capacity); return sb.ToString(); } catch { return ""; }
        }

        // [ATTENTION-HIDE] Устанавливаем глобальный mouse hook пока панель показана в noActivate-режиме.
        // Любой клик ВНЕ панели → скрываем её и снимаем hook.
        void InstallAttentionMouseHook()
        {
            if (_attentionMouseHook != IntPtr.Zero) return;
            _attentionMouseProc = AttentionMouseHookCallback;
            using (var m = System.Diagnostics.Process.GetCurrentProcess().MainModule)
                _attentionMouseHook = SetWindowsHookEx(WH_MOUSE_LL, _attentionMouseProc, GetModuleHandle(m.ModuleName), 0);
            Debug.WriteLine("[ATTENTION] Mouse hook installed");
        }

        void UninstallAttentionMouseHook()
        {
            if (_attentionMouseHook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_attentionMouseHook); } catch { }
            _attentionMouseHook = IntPtr.Zero;
            Debug.WriteLine("[ATTENTION] Mouse hook removed");
        }

        IntPtr AttentionMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _shownByAttention)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
                {
                    // [FIX-2.1] Один вызов вместо двух (мёртвый первый вызов убран)
                    var pt = Marshal.PtrToStructure<POINT>(lParam);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            // Получаем RECT панели в экранных координатах
                            var src = PresentationSource.FromVisual(this);
                            double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                            int left = (int)(Left * dpi);
                            int top = (int)(Top * dpi);
                            int right = (int)((Left + ActualWidth) * dpi);
                            int bottom = (int)((Top + ActualHeight) * dpi);
                            bool insideTaskbar = pt.x >= left && pt.x <= right
                                              && pt.y >= top && pt.y <= bottom;
                            if (!insideTaskbar && _shownByAttention)
                            {
                                Debug.WriteLine("[ATTENTION] Click outside taskbar — hiding");
                                _shownByAttention = false;
                                UninstallAttentionMouseHook();
                                HideTaskbar(animate: true);
                            }
                        }
                        catch { }
                    }));
                }
            }
            return CallNextHookEx(_attentionMouseHook, nCode, wParam, lParam);
        }

        // [MONITOR-FIX] Окно всё ещё покрывает основной монитор полностью?
        bool IsWindowCoveringPrimaryMonitor(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
                if (!GetWindowRect(hwnd, out RECT r)) return false;
                var src = PresentationSource.FromVisual(this);
                double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                var (monLeft, monTop, monWidth, monHeight) = GetPrimaryMonitorRectDip();
                int ml = (int)(monLeft * dpi), mt = (int)(monTop * dpi);
                int mr = ml + (int)(monWidth * dpi), mb = mt + (int)(monHeight * dpi);
                return r.left <= ml + 4 && r.top <= mt + 4 && r.right >= mr - 4 && r.bottom >= mb - 4;
            }
            catch { return false; }
        }

        bool IsForegroundFullscreen()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow(); if (hwnd == IntPtr.Zero) return false;
                if (hwnd == GetShellWindow()) return false;
                if (DesktopWindowClasses.Contains(GetWindowClass(hwnd))) return false;
                // [FIX-D3DPROXY] Proxy/overlay окна не считаются fullscreen-окнами
                if (IgnoredWindowClasses.Contains(GetWindowClass(hwnd))) return false;
                // [FIX-2.2] Используем кэшированные HWND вместо new WindowInteropHelper на каждый вызов
                if (_myHwnd != IntPtr.Zero && hwnd == _myHwnd) return false;
                if (_menuHwnd != IntPtr.Zero && hwnd == _menuHwnd) return false;
                if (_previewHwnd != IntPtr.Zero && hwnd == _previewHwnd) return false;
                if (_trayHwnd != IntPtr.Zero && hwnd == _trayHwnd) return false;
                if (_wifiHwnd != IntPtr.Zero && hwnd == _wifiHwnd) return false;
                GetWindowThreadProcessId(hwnd, out uint pid); if (pid == 0) return false;
                string pn = "";
                // [FIX-2.10] Кэшируем имя процесса чтобы не звать GetProcessById каждые 400мс
                if (!_pidNameCache.TryGetValue(pid, out pn))
                {
                    try { pn = Process.GetProcessById((int)pid).ProcessName?.ToLowerInvariant() ?? ""; } catch { }
                    if (!string.IsNullOrEmpty(pn) && _pidNameCache.Count < 256)
                        _pidNameCache.TryAdd(pid, pn);
                }
                if (string.Equals(pn, "explorer", StringComparison.OrdinalIgnoreCase)) return false;
                if (IgnoredProcesses.Contains(pn)) return false;
                // [STAB-4] SafeGetWindowText
                string title = SafeGetWindowText(hwnd);
                if (string.IsNullOrWhiteSpace(title) || title == "Program Manager") return false;
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
                // [STAB-11] IsWindow перед GetWindowLong
                if (!IsWindow(hwnd)) return false;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0) return false;
                if (!GetWindowRect(hwnd, out RECT r)) return false;
                var srcFs = PresentationSource.FromVisual(this);
                double dpiFs = srcFs?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                var (fsMonLeft, fsMonTop, fsMonW, fsMonH) = GetPrimaryMonitorRectDip();
                int fsML = (int)(fsMonLeft * dpiFs), fsMT = (int)(fsMonTop * dpiFs);
                int fsMR = fsML + (int)(fsMonW * dpiFs), fsMB = fsMT + (int)(fsMonH * dpiFs);
                if (r.left > fsML + 4 || r.top > fsMT + 4 || r.right < fsMR - 4 || r.bottom < fsMB - 4) return false;
                _lastFullscreenHwnd = hwnd; return true;
            }
            catch { return false; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // [GAME] FPS-ДЕТЕКЦИЯ И БЛОКЕР КУРСОРА
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Возвращает true если активная игра захватила курсор (FPS / захват мышью).
        /// Проверяем два признака:
        ///   1) CURSOR_SHOWING == 0 — курсор скрыт (типично для FPS)
        ///   2) GetClipCursor возвращает прямоугольник &lt;= 2x2 пикселя —
        ///      игра вызвала ClipCursor с точечной областью (raw mouse)
        /// </summary>
        bool IsCursorCapturedByFpsGame()
        {
            try
            {
                // Признак 1: курсор скрыт
                var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
                if (GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) == 0)
                    return true;
                // Признак 2: ClipCursor ограничен малой зоной (захват мышью без скрытия)
                if (GetClipCursor(out RECT clip))
                {
                    int w = clip.right - clip.left;
                    int h = clip.bottom - clip.top;
                    if (w <= 2 && h <= 2) return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Забирает фокус у игры через AttachThreadInput — единственный надёжный способ
        /// вырвать курсор из Raw Input захвата CS2/TF2/Minecraft без NOACTIVATE ограничений.
        /// После этого игра перестаёт получать Raw Input движения мыши.
        /// </summary>
        void StealFocusFromGame()
        {
            try
            {
                // [FIX-2.2] Используем кэшированный HWND
                if (_myHwnd == IntPtr.Zero) return;

                IntPtr fgHwnd = GetForegroundWindow();
                if (fgHwnd == IntPtr.Zero || fgHwnd == _myHwnd) return;

                uint myTid = GetWindowThreadProcessId(_myHwnd, IntPtr.Zero);
                uint fgTid = GetWindowThreadProcessId(fgHwnd, IntPtr.Zero);

                // Присоединяем наш поток к потоку игры — теперь SetForegroundWindow работает
                bool attached = false;
                if (myTid != fgTid)
                {
                    attached = AttachThreadInput(myTid, fgTid, true);
                }

                try
                {
                    AllowSetForegroundWindow((uint)System.Diagnostics.Process.GetCurrentProcess().Id);
                    SetForegroundWindow(_myHwnd);
                    // Снимаем ClipCursor ПОСЛЕ того как мы в фокусе
                    ClipCursor(IntPtr.Zero);
                }
                finally
                {
                    if (attached) AttachThreadInput(myTid, fgTid, false);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] StealFocusFromGame: {ex.Message}"); }
        }

        void ShowFullscreenBlocker()
        {
            try
            {
                if (_blockerWindow == null)
                {
                    _blockerWindow = new FullscreenBlockerWindow();
                    _blockerWindow.DismissRequested += (sx, sy) =>
                        Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                            new Action(() => OnBlockerDismissed(sx, sy)));
                    _blockerWindow.TaskbarWindow = this;
                }
                // [GAME-7] Передаём ссылку на меню Пуск — дырка №2
                _blockerWindow.MenuWindow = _menuWindow;
                _blockerWindow.ShowBlocker();
                // [GAME-6] Тулбар должен быть выше блокера — поднимаем его после
                Topmost = false;
                Topmost = true;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] ShowFullscreenBlocker: {ex.Message}"); }
        }

        void HideFullscreenBlocker()
        {
            try
            {
                _blockerWindow?.HideBlocker();
                _appOpenedOverFullscreen = false;
                // Вернуть фокус обратно в игру
                if (_lastFullscreenHwnd != IntPtr.Zero && IsWindow(_lastFullscreenHwnd))
                    SetForegroundWindow(_lastFullscreenHwnd);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] HideFullscreenBlocker: {ex.Message}"); }
        }

        /// <summary>
        /// Вызывается когда пользователь кликнул вне тулбара/меню по блокеру.
        /// Определяет что под курсором: если это другое окно (не игра) —
        /// передаёт фокус ему (suspend-режим). Если пустое место или сама игра —
        /// возвращает фокус в игру как обычно.
        /// </summary>
        void OnBlockerDismissed(int screenX, int screenY)
        {
            try
            {
                _blockerWindow?.HideBlocker();

                // Ищем окно под курсором, игнорируя блокер (он уже скрыт)
                IntPtr hwndUnder = WindowFromPoint(new POINT { x = screenX, y = screenY });

                // Получаем корневое окно (не дочерний контрол)
                if (hwndUnder != IntPtr.Zero)
                    hwndUnder = GetAncestor(hwndUnder, GA_ROOT);

                // Проверяем: это наше собственное окно?
                // [FIX-2.2] Кэшированные HWND
                bool isOwn = (_myHwnd != IntPtr.Zero && hwndUnder == _myHwnd)
                          || (_menuHwnd != IntPtr.Zero && hwndUnder == _menuHwnd);

                if (!isOwn && hwndUnder != IntPtr.Zero && hwndUnder != _lastFullscreenHwnd
                    && IsWindowVisible(hwndUnder))
                {
                    // Под курсором — другое видимое окно (браузер, медиаплеер и т.д.)
                    // Переключаемся на него без возврата в игру
                    _appOpenedOverFullscreen = true;
                    Debug.WriteLine($"[MyTaskbar] Blocker dismissed → switching to window {hwndUnder:X}");
                    SetForegroundWindow(hwndUnder);
                }
                else
                {
                    // Пустое место или сама игра — возвращаем в игру
                    _appOpenedOverFullscreen = false;
                    Debug.WriteLine("[MyTaskbar] Blocker dismissed → returning to game");
                    if (_lastFullscreenHwnd != IntPtr.Zero && IsWindow(_lastFullscreenHwnd))
                        SetForegroundWindow(_lastFullscreenHwnd);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] OnBlockerDismissed: {ex.Message}"); }
        }

        // [GAME-9] Скрыть блокер когда пользователь открывает/переключается на окно поверх игры.
        // Блокер вернётся только когда игра снова получит фокус (CheckFullscreen это отследит).
        // ВАЖНО: используем _lastFullscreenHwnd а не IsForegroundFullscreen() — когда меню открыто,
        // foreground = меню, и IsForegroundFullscreen() вернёт false, флаг не поставится,
        // MenuVisibilityChanged вернёт блокер и первый клик по окну уйдёт в игру.
        void SuspendBlockerForAppWindow()
        {
            try
            {
                // Игра была fullscreen если lastFullscreenHwnd валиден и ещё существует
                bool wasFullscreen = _lastFullscreenHwnd != IntPtr.Zero && IsWindow(_lastFullscreenHwnd);
                if (!wasFullscreen) return;
                _blockerWindow?.HideBlocker();
                _appOpenedOverFullscreen = true;
                Debug.WriteLine("[MyTaskbar] Blocker suspended — app window opened over game");
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] SuspendBlockerForAppWindow: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // [FULLSCREEN-AUTOHIDE] Win10-style: панель в fullscreen скрывается сама,
        // если курсор не вернулся за 400мс после того как ушёл с панели.
        // Срабатывает только когда панель показана в fullscreen (_shownByAttention
        // или _shownByEdgeReveal), то есть не в обычном режиме.
        // ═════════════════════════════════════════════════════════════════════

        void StartFullscreenNoMouseHideTimer()
        {
            StopFullscreenNoMouseHideTimer();
            _fullscreenNoMouseHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(100, _hideDelayMs))
            };
            _fullscreenNoMouseHideTimer.Tick += (_, _t) =>
            {
                _fullscreenNoMouseHideTimer.Stop();
                _fullscreenNoMouseHideTimer = null;
                SafeRun(CheckAndAutoHideInFullscreen, "FullscreenNoMouseHide");
            };
            _fullscreenNoMouseHideTimer.Start();
            Debug.WriteLine($"[FULLSCREEN-AUTOHIDE] Timer started ({_hideDelayMs}ms)");
        }

        void StopFullscreenNoMouseHideTimer()
        {
            if (_fullscreenNoMouseHideTimer == null) return;
            _fullscreenNoMouseHideTimer.Stop();
            _fullscreenNoMouseHideTimer = null;
            Debug.WriteLine("[FULLSCREEN-AUTOHIDE] Timer cancelled");
        }

        void CheckAndAutoHideInFullscreen()
        {
            // Скрываем только если панель реально была показана в fullscreen-режиме
            if (!_shownByAttention && !_shownByEdgeReveal) return;
            if (Visibility != Visibility.Visible || _taskbarAnimating) return;
            // Курсор над панелью или дочерним окном? Тогда не прячем.
            if (IsMouseOver) return;
            if (IsCursorOverOwnWindows()) { StartFullscreenNoMouseHideTimer(); return; }
            // [FIX-2.6] Единый метод вместо дублирования
            if (AnyOwnPopupVisible()) { StartFullscreenNoMouseHideTimer(); return; }

            Debug.WriteLine("[FULLSCREEN-AUTOHIDE] No cursor for 400ms — hiding taskbar");

            bool wasEdge = _shownByEdgeReveal;
            _shownByEdgeReveal = false;

            if (_shownByAttention)
            {
                // Если показана из-за attention — скрываем как обычно
                _shownByAttention = false;
                UninstallAttentionMouseHook();
                _isHiddenByFullscreen = true;
                HideTaskbar(animate: true);
            }
            else if (wasEdge)
            {
                // Если показана через edge reveal — возвращаем в fullscreen-скрытое состояние
                _isHiddenByFullscreen = true;
                _edgeRevealPending = false;
                HideTaskbar(animate: true);
            }
        }

        // Вызывается из MainWindow.xaml.cs когда курсор заходит/выходит с панели
        void OnTaskbarMouseEnterFullscreen()
        {
            if (!_shownByAttention && !_shownByEdgeReveal) return;
            StopFullscreenNoMouseHideTimer();
            Debug.WriteLine("[FULLSCREEN-AUTOHIDE] Cursor on taskbar — timer cancelled");
        }

        void OnTaskbarMouseLeaveFullscreen()
        {
            if (!_shownByAttention && !_shownByEdgeReveal) return;
            // [FIX-2.5] Используем поле вместо new DispatcherTimer при каждом MouseLeave —
            // предотвращает накопление таймеров при быстром движении курсора
            _mouseLeaveCheckTimer?.Stop();
            _mouseLeaveCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _mouseLeaveCheckTimer.Tick += (_, _t) =>
            {
                _mouseLeaveCheckTimer?.Stop();
                _mouseLeaveCheckTimer = null;
                if (IsCursorOverOwnWindows())
                {
                    Debug.WriteLine("[FULLSCREEN-AUTOHIDE] Cursor moved to own child window — not hiding");
                    return;
                }
                StartFullscreenNoMouseHideTimer();
                Debug.WriteLine("[FULLSCREEN-AUTOHIDE] Cursor left taskbar — timer started");
            };
            _mouseLeaveCheckTimer.Start();
        }

        void HideTaskbar(bool animate = false)
        {
            if (_taskbarAnimating) return;
            // [FULLSCREEN-AUTOHIDE] Сбрасываем флаг edge reveal и останавливаем таймер
            _shownByEdgeReveal = false;
            StopFullscreenNoMouseHideTimer();
            _wifiWindow?.Hide(); HidePreview();
            // [GAME-5] Снимаем блокер курсора вместе с панелью
            _blockerWindow?.HideBlocker();
            if (_menuWindow != null && _menuWindow.IsVisible)
            {
                _menuWindow.HideAnimated();
                int delay = (int)MenuWindow.ANIM_HIDE_MS + 10;
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
                t.Tick += (s, e) => { t.Stop(); HideTaskbarCore(animate); };
                t.Start();
            }
            else { HideTaskbarCore(animate); }
        }

        void HideTaskbarCore(bool animate)
        {
            if (_taskbarAnimating) return;
            var (_, monTop, _, monHeight) = GetPrimaryMonitorRectDip();
            double hiddenTop = _isBottom
                ? monTop + monHeight + 4
                : monTop - (TASKBAR_HEIGHT + 4);
            if (animate)
            {
                _taskbarAnimating = true;
                double from = Top, to = hiddenTop;
                var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ANIM_MS))
                { EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop };
                a.Completed += (s, e) => { BeginAnimation(TopProperty, null); Top = to; _taskbarAnimating = false; Visibility = Visibility.Hidden; };
                BeginAnimation(TopProperty, a);
            }
            else { Visibility = Visibility.Hidden; Top = hiddenTop; }
        }

        void ShowTaskbar(bool animate = false, Action onComplete = null, bool noActivate = false)
        {
            if (_taskbarAnimating) return;
            Topmost = true;
            // [FIX-2.12] StealFocusFromGame только если курсор реально захвачен FPS-игрой.
            // Вызов при любом ShowTaskbar ломал фокус у обычных fullscreen-приложений.
            if (!noActivate && IsCursorCapturedByFpsGame())
                StealFocusFromGame();

            // [PRIMARY-MONITOR] Позиционируем относительно выбранного монитора
            var (monLeft, monTop, monWidth, monHeight) = GetPrimaryMonitorRectDip();
            var src2 = PresentationSource.FromVisual(this);
            double dpi = src2?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double screenPx2 = Math.Round(monWidth * dpi);
            double panelPx2 = Math.Round(ActualWidth * dpi);
            Left = monLeft + Math.Floor((screenPx2 - panelPx2) / 2.0) / dpi;

            SafeRun(ApplyAcrylicBackground, "ApplyAcrylicBackground_Show");
            double visibleTop = _isBottom ? monTop + monHeight - TASKBAR_HEIGHT : monTop;
            double hiddenTop  = _isBottom ? monTop + monHeight + 4              : monTop - (TASKBAR_HEIGHT + 4);
            if (animate)
            {
                _taskbarAnimating = true;
                Top = hiddenTop; Visibility = Visibility.Visible;
                var a = new DoubleAnimation(hiddenTop, visibleTop, TimeSpan.FromMilliseconds(ANIM_MS))
                { EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
                a.Completed += (s, e) => { BeginAnimation(TopProperty, null); Top = visibleTop; _taskbarAnimating = false; onComplete?.Invoke(); };
                BeginAnimation(TopProperty, a);
            }
            else { Visibility = Visibility.Visible; Top = visibleTop; onComplete?.Invoke(); }
        }

    }
}
