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
// MainWindow.Keyboard.cs — Low-level keyboard hook, WinKey handling
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // KEYBOARD HOOK
        // ═════════════════════════════════════════════════════════════════════
        void InstallKeyboardHook()
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_keyboardHook); } catch { }
                _keyboardHook = IntPtr.Zero;
            }
            _keyboardProc = KeyboardHookCallback;
            using (var p = Process.GetCurrentProcess())
            using (var m = p.MainModule)
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(m.ModuleName), 0);
            if (_keyboardHook == IntPtr.Zero) Debug.WriteLine("[MyTaskbar] Hook FAILED: " + Marshal.GetLastWin32Error());
            else Debug.WriteLine("[MyTaskbar] Hook installed OK");

            // [FIX-HOOK-WATCHDOG] Переустанавливаем хук если Windows его убил (LowLevelHooksTimeout).
            // Это происходит когда callback не успевает вернуться за ~300 мс (реестровый таймаут).
            if (_hookWatchdog == null)
            {
                _hookWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _hookWatchdog.Tick += (s, e) =>
                {
                    if (_keyboardHook == IntPtr.Zero)
                    {
                        Debug.WriteLine("[MyTaskbar] Hook watchdog: hook is dead, reinstalling...");
                        _winKeyDown = false; _winUsedInCombo = false;
                        _shiftDown = false; _ctrlDown = false; _altDown = false; _injectingWin = false;
                        InstallKeyboardHook();
                    }
                };
                _hookWatchdog.Start();
            }
        }

        IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            if (_injectingWin) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            int vk = Marshal.ReadInt32(lParam);
            int msg = (int)wParam;
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            bool isWin = vk == VK_LWIN || vk == VK_RWIN;

            void InjectWinDownOnce()
            {
                if (_winUsedInCombo) return;
                _winUsedInCombo = true; _injectingWin = true;
                try { keybd_event(0x5B, 0, 0, UIntPtr.Zero); } finally { _injectingWin = false; }
            }

            if (vk == VK_SHIFT || vk == 0xA0 || vk == 0xA1) { _shiftDown = isDown; if (_winKeyDown && isDown) InjectWinDownOnce(); return CallNextHookEx(_keyboardHook, nCode, wParam, lParam); }
            if (vk == VK_CONTROL || vk == 0xA2 || vk == 0xA3) { _ctrlDown = isDown; if (_winKeyDown && isDown) InjectWinDownOnce(); return CallNextHookEx(_keyboardHook, nCode, wParam, lParam); }
            if (vk == VK_MENU || vk == 0xA4 || vk == 0xA5) { _altDown = isDown; if (_winKeyDown && isDown) InjectWinDownOnce(); return CallNextHookEx(_keyboardHook, nCode, wParam, lParam); }


            if (isWin)
            {
                if (isDown)
                {
                    if (!_winKeyDown)
                    {
                        _winKeyDown = true; _winUsedInCombo = false;
                        // [FIX-MODIFIER-SYNC] Синхронизируем реальное состояние модификаторов через
                        // GetAsyncKeyState, т.к. после диспетчера задач (Ctrl+Shift+Esc) keyup мог
                        // не дойти до хука — _ctrlDown/_shiftDown могли застрять в true.
                        // Если они застряли: InjectWinDownOnce() → _winUsedInCombo=true → Win-up
                        // не блокируется → система открывает оригинальный Пуск.
                        _shiftDown = (GetAsyncKeyState(0xA0) & 0x8000) != 0 || (GetAsyncKeyState(0xA1) & 0x8000) != 0;
                        _ctrlDown = (GetAsyncKeyState(0xA2) & 0x8000) != 0 || (GetAsyncKeyState(0xA3) & 0x8000) != 0;
                        _altDown = (GetAsyncKeyState(0xA4) & 0x8000) != 0 || (GetAsyncKeyState(0xA5) & 0x8000) != 0;
                        if (_shiftDown || _ctrlDown || _altDown) InjectWinDownOnce();
                    }
                    return (IntPtr)1;
                }
                if (isUp)
                {
                    bool wasCombo = _winUsedInCombo;
                    _winKeyDown = false; _winUsedInCombo = false;
                    if (wasCombo) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(OnWinKeyDown));
                    return (IntPtr)1;
                }
            }
            if (_winKeyDown && isDown) InjectWinDownOnce();

            // Shift+Alt+O — open Settings window
            if (isDown && vk == 0x4F && _shiftDown && _altDown && !_winKeyDown)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(OpenSettingsWindow));
                return (IntPtr)1; // consume keypress
            }

            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        void OnWinKeyDown()
        {
            try
            {
                MarkOwnActivity();
                // [FIX-MODIFIER-SYNC] Дополнительный сброс модификаторов здесь — на случай race condition.
                // Если _ctrlDown/_shiftDown застряли, это предотвращает ложную логику комбо.
                _shiftDown = (GetAsyncKeyState(0xA0) & 0x8000) != 0 || (GetAsyncKeyState(0xA1) & 0x8000) != 0;
                _ctrlDown = (GetAsyncKeyState(0xA2) & 0x8000) != 0 || (GetAsyncKeyState(0xA3) & 0x8000) != 0;
                _altDown = (GetAsyncKeyState(0xA4) & 0x8000) != 0 || (GetAsyncKeyState(0xA5) & 0x8000) != 0;
                TryCloseSystemStartMenu();
                EnsureMenuWindow();
                if (_menuWindow.JustHidden) return;
                if (_menuWindow.IsVisible) { _menuWindow.HideAnimated(); return; }
                // Забираем фокус у игры при вызове меню Win-клавишей
                StealFocusFromGame();
                if (_isHiddenByFullscreen)
                {
                    _isHiddenByFullscreen = false; _fullscreenConfirmCount = 0; _notFullscreenConfirmCount = 0;
                    bool fps = IsCursorCapturedByFpsGame();
                    // [GAME-3] В fullscreen показываем ТОЛЬКО панель задач — без меню Пуск.
                    // Если игра захватила курсор (FPS) — добавляем прозрачный блокер,
                    // чтобы курсор не мог уйти обратно в игру.
                    ShowTaskbar(animate: true, onComplete: () =>
                    {
                        if (fps) ShowFullscreenBlocker();
                    });
                }
                else
                {
                    // [GAME-10] Если игра fullscreen — меню должно быть Topmost,
                    // иначе после нескольких открытий оно уходит под игру.
                    bool fps = IsCursorCapturedByFpsGame();
                    _menuWindow.IsFullscreenMode = IsForegroundFullscreen() || fps;
                    // [GAME-11] Если FPS захватил курсор — блокер должен быть активен
                    // чтобы ClipCursor держал курсор на весь экран поверх игры.
                    // Без этого после клика в игру (HideFullscreenBlocker) блокер скрыт,
                    // и при повторном открытии меню FPS перехватывает курсор везде кроме панели+меню.
                    if (fps) ShowFullscreenBlocker();
                    _menuWindow.IsBottom = _isBottom;
                    _menuWindow.ShowMenu();
                    // [FIX-TASKMGR-STARTMENU] Если диспетчер задач был в фокусе —
                    // Shell открывает оригинальный Пуск через IPC с задержкой даже
                    // после того как hook поглотил Win-key. Закрываем Пуск повторно
                    // с задержкой 180мс, когда он уже гарантированно открылся.
                    if (IsTaskmgrForeground())
                        TryCloseSystemStartMenuDelayed(180);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] OnWinKeyDown: {ex.Message}"); }
        }

        void MarkOwnActivity() => _ownWindowActivityAt = DateTime.UtcNow;
        bool IsInOwnActivityGrace() => (DateTime.UtcNow - _ownWindowActivityAt).TotalMilliseconds < OWN_ACTIVITY_GRACE_MS;

        // [FIX-2.6] Единый метод проверки открытых попапов — используется в CheckFullscreen
        // и CheckAndAutoHideInFullscreen вместо дублированных списков условий
        bool AnyOwnPopupVisible() =>
            (_menuWindow   != null && _menuWindow.IsVisible)    ||
            (_trayWindow   != null && _trayWindow.IsOpen)       ||
            (_wifiWindow   != null && _wifiWindow.IsVisible)    ||
            (_previewWindow!= null && _previewWindow.IsVisible) ||
            (_brightnessFlyout != null && _brightnessFlyout.IsVisible) ||
            (_volumeFlyout != null && _volumeFlyout.IsVisible) ||
            (_calendarWindow != null && _calendarWindow.IsVisible);

        // [FIX-2.2] Обновляет кэш HWND при создании/показе дочерних окон.
        // Вызывается из EnsureMenuWindow, InitPreviewWindow и т.п.
        void RefreshOwnHwnds()
        {
            try
            {
                if (_myHwnd == IntPtr.Zero)
                    try { _myHwnd = new WindowInteropHelper(this).Handle; } catch { }
                if (_menuWindow != null)
                    try { _menuHwnd = new WindowInteropHelper(_menuWindow).Handle; } catch { }
                if (_previewWindow != null)
                    try { _previewHwnd = new WindowInteropHelper(_previewWindow).Handle; } catch { }
                if (_trayWindow != null)
                    try { _trayHwnd = new WindowInteropHelper(_trayWindow).Handle; } catch { }
                if (_wifiWindow != null)
                    try { _wifiHwnd = new WindowInteropHelper(_wifiWindow).Handle; } catch { }
            }
            catch { }
        }

        // [FIX-2.4] Удаляет PID из кэшей когда группа (процесс) больше не нужна.
        // Предотвращает накопление мёртвых PID в _pidNameCache/_pidPathCache.
        void CleanPidCacheForGroup(AppGroup g)
        {
            if (g == null) return;
            try
            {
                var pidsToCheck = new HashSet<uint>();
                foreach (var hwnd in g.Hwnds)
                {
                    GetWindowThreadProcessId(hwnd, out uint p);
                    if (p != 0) pidsToCheck.Add(p);
                }
                foreach (var pid in pidsToCheck)
                {
                    // Удаляем из кэша только если этот PID больше не используется другими группами
                    bool stillUsed = false;
                    foreach (var gr in _groups.Values)
                    {
                        if (gr == g) continue;
                        foreach (var h in gr.Hwnds)
                        {
                            GetWindowThreadProcessId(h, out uint p2);
                            if (p2 == pid) { stillUsed = true; break; }
                        }
                        if (stillUsed) break;
                    }
                    if (!stillUsed)
                    {
                        _pidNameCache.TryRemove(pid, out _);
                        _pidPathCache.TryRemove(pid, out _);
                    }
                }
            }
            catch { }
        }

        bool IsCursorOverOwnWindows()
        {
            try
            {
                if (!GetCursorPos(out POINT cp)) return false;
                double cx = cp.x, cy = cp.y;
                if (IsPointOverWindow(this, cx, cy)) return true;
                if (_menuWindow != null && _menuWindow.IsVisible && IsPointOverWindow(_menuWindow, cx, cy)) return true;
                if (_previewWindow != null && _previewWindow.IsVisible && IsPointOverWindow(_previewWindow, cx, cy)) return true;
                if (_trayWindow != null && _trayWindow.IsOpen && IsPointOverWindow(_trayWindow, cx, cy)) return true;
                if (_wifiWindow != null && _wifiWindow.IsVisible && IsPointOverWindow(_wifiWindow, cx, cy)) return true;
                return false;
            }
            catch { return false; }
        }

        static bool IsPointOverWindow(Window w, double cx, double cy)
        {
            try { if (w == null || !w.IsVisible) return false; return cx >= w.Left && cx <= w.Left + w.ActualWidth && cy >= w.Top && cy <= w.Top + w.ActualHeight; }
            catch { return false; }
        }

    }
}
