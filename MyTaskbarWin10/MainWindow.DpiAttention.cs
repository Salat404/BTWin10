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
// MainWindow.DpiAttention.cs — DPI auto-scaling, attention/shell hook
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // [ATTENTION] Win10-style attention indication — shell hook HSHELL_FLASH
        // ═════════════════════════════════════════════════════════════════════
        const int HSHELL_FLASH = 0x8006;  // окно требует внимания (FlashWindow)

        void RegisterShellAttentionHook()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                _wmShellHook = RegisterWindowMessage("SHELLHOOK");
                RegisterShellHookWindow(hwnd);
                Debug.WriteLine($"[ATTENTION] Shell hook registered, WM=0x{_wmShellHook:X}");
            }
            catch (Exception ex) { Debug.WriteLine($"[ATTENTION] Register: {ex.Message}"); }
        }

        // WndProc-хук: ловим SHELLHOOK с кодом HSHELL_FLASH
        IntPtr ShellAttentionHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                if (_wmShellHook != 0 && (uint)msg == _wmShellHook)
                {
                    // HSHELL_FLASH (0x8006) — единственный код, который Win10 taskbar
                    // использует для индикации "требует внимания".
                    // HSHELL_REDRAW (0x0006) — изменение заголовка/перерисовка, НЕ внимание.
                    // Ранее второе условие ошибочно трактовало HSHELL_REDRAW как flash,
                    // что вызывало ложные срабатывания у Discord, Teams, браузеров и т.д.
                    int rawCode = wParam.ToInt32();
                    // FIX: проверяем ТОЛЬКО точное значение 0x8006, без маскирования
                    bool isFlash = (rawCode == HSHELL_FLASH);  // 0x8006 = реальный HSHELL_FLASH
                    if (isFlash)
                    {
                        IntPtr flashHwnd = lParam;
                        if (flashHwnd != IntPtr.Zero)
                            Dispatcher.BeginInvoke(new Action(() => OnWindowFlash(flashHwnd)));
                    }
                    // FIX: HSHELL_WINDOWACTIVATED (4) и HSHELL_RUDEAPPACTIVATED (0x8004) —
                    // окно получило фокус. Win10 taskbar снимает attention при активации.
                    // Это покрывает случаи Alt+Tab и клика по самому окну (не по кнопке).
                    bool isActivated = (rawCode == 4) || (rawCode == 0x8004);
                    if (isActivated)
                    {
                        IntPtr activeHwnd = lParam;
                        if (activeHwnd != IntPtr.Zero)
                            Dispatcher.BeginInvoke(new Action(() => OnWindowActivatedByShell(activeHwnd)));
                    }
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        void OnWindowFlash(IntPtr flashHwnd)
        {
            try
            {
                // Найти группу которой принадлежит это окно
                AppGroup target = null;
                foreach (var g in _groups.Values)
                    if (g.Hwnds.Contains(flashHwnd)) { target = g; break; }

                if (target == null) return;

                // Не начинать заново если уже мигает или в статичном attention-режиме
                if (target.NeedsAttention) return;

                // [FIX-FALSE-FLASH-1] Игнорируем flash если окно уже в фокусе.
                // Telegram вызывает FlashWindow при переключении чатов — это не реальное
                // требование внимания, а внутренняя логика приложения.
                try
                {
                    IntPtr fg = GetForegroundWindow();
                    if (fg != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(fg, out uint fgPid);
                        GetWindowThreadProcessId(flashHwnd, out uint flashPid);
                        if (fgPid != 0 && fgPid == flashPid)
                        {
                            Debug.WriteLine("[ATTENTION] Ignored false flash from " + target.ExeName + " — already foreground");
                            return;
                        }
                    }
                }
                catch { }

                // [FIX-FALSE-FLASH-2] Игнорируем flash если у группы нет живых окон.
                if (target.Hwnds.Count == 0)
                {
                    Debug.WriteLine("[ATTENTION] Ignored flash from " + target.ExeName + " — no visible windows");
                    return;
                }

                // [FIX-FALSE-FLASH-3] Некоторые системные процессы никогда не требуют
                // внимания в Win10 — explorer, система, фоновые сервисы.
                if (string.Equals(target.ExeName, "explorer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(target.ExeName, "svchost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(target.ExeName, "sihost", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine("[ATTENTION] Ignored flash from " + target.ExeName + " — system process");
                    return;
                }

                target.NeedsAttention = true;
                target.FlashStartTime = DateTime.UtcNow;
                _attentionGroups.Add(target);

                // [ATTENTION-SHOW] Если панель скрыта (fullscreen-режим или не видна) —
                // показываем её чтобы пользователь увидел мигающую кнопку.
                if (_isHiddenByFullscreen || Visibility != Visibility.Visible)
                {
                    // [MONITOR-FIX] Показываем панель только если fullscreen-окно
                    // больше не покрывает основной монитор. Если покрывает — мигаем
                    // кнопкой, но панель не показываем (чтобы не мешать игре).
                    if (!IsWindowCoveringPrimaryMonitor(_lastFullscreenHwnd))
                    {
                        _isHiddenByFullscreen = false;
                        _fullscreenConfirmCount = 0;
                        _notFullscreenConfirmCount = 0;
                        ShowTaskbar(animate: true);
                        Debug.WriteLine($"[ATTENTION] ShowTaskbar triggered by flash from {target.ExeName}");
                    }
                    else
                    {
                        // [ATTENTION-NOACTIVATE] Fullscreen-игра на экране — показываем панель
                        // НЕ забирая фокус. Кнопка мигает, панель видна, игра не прерывается.
                        _shownByAttention = true;
                        ShowTaskbar(animate: true, noActivate: true);
                        InstallAttentionMouseHook();
                        Debug.WriteLine($"[ATTENTION] ShowTaskbar noActivate triggered by flash from {target.ExeName}");
                    }
                }

                // Запустить мигание (StartFlashForGroup уже добавляет в _flashingButtons)
                StartFlashForGroup(target);

                // Через 3 секунды остановить мигание, оставить статичный оранжевый фон
                var stopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                stopTimer.Tick += (s, e) =>
                {
                    stopTimer.Stop();
                    try
                    {
                        // Убрать из мигающих, но оставить NeedsAttention=true → статичный фон
                        if (target.Button != null)
                        {
                            _flashingButtons.Remove(target.Button);
                            if (_flashingButtons.Count == 0) _flashTimer.Stop();
                            // Установить статичный оранжевый фон кнопки
                            ApplyAttentionBackground(target.Button, true);
                        }
                    }
                    catch { }
                };
                stopTimer.Start();
                Debug.WriteLine($"[ATTENTION] Flash started for {target.ExeName}");
            }
            catch (Exception ex) { Debug.WriteLine($"[ATTENTION] OnWindowFlash: {ex.Message}"); }
        }

        void ApplyAttentionBackground(Button btn, bool on)
        {
            if (btn == null) return;
            try
            {
                btn.ApplyTemplate();
                var rb = btn.Template?.FindName("RunBar", btn) as Rectangle;
                var ab = btn.Template?.FindName("AppBorder", btn) as Border;
                if (ab != null) ab.Background = on ? BrushAttentionBg : BrushTransparent;
                if (rb != null && on) { rb.Visibility = Visibility.Visible; rb.Fill = BrushFlashPip; }
            }
            catch { }
        }

        // [FIX-ATTENTION] Вызывается когда окно получает фокус через shell hook.
        // Win10 taskbar всегда снимает attention при активации окна — любым способом.
        void OnWindowActivatedByShell(IntPtr activeHwnd)
        {
            try
            {
                foreach (var g in _groups.Values)
                {
                    if (!g.NeedsAttention) continue;
                    if (g.Hwnds.Contains(activeHwnd))
                    {
                        ClearAttention(g);
                        Debug.WriteLine($"[ATTENTION] Cleared for {g.ExeName} via shell activation");
                        break;
                    }
                }
            }
            catch { }
        }

        void ClearAttention(AppGroup g)
        {
            if (g == null) return;
            try
            {
                g.NeedsAttention = false;
                _attentionGroups.Remove(g);
                _flashingButtons.Remove(g.Button);
                if (_flashingButtons.Count == 0) _flashTimer?.Stop();
                ApplyAttentionBackground(g.Button, false);
                // [ATTENTION-AUTOHIDE] Если панель была показана noActivate во время игры
                // и больше нет групп требующих внимания — скрываем панель обратно.
                if (_attentionGroups.Count == 0
                    && IsWindowCoveringPrimaryMonitor(_lastFullscreenHwnd)
                    && Visibility == Visibility.Visible
                    && _isHiddenByFullscreen)
                {
                    _shownByAttention = false;
                    UninstallAttentionMouseHook();
                    HideTaskbar(animate: true);
                    Debug.WriteLine("[ATTENTION] All attention cleared — auto-hiding taskbar (fullscreen active)");
                }
            }
            catch { }
        }

    }
}
