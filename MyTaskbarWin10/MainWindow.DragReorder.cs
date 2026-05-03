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
// MainWindow.DragReorder.cs — Drag-reorder of taskbar buttons
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═══════════════════════════════════════════════════════════════════
        // DRAG-REORDER  —  Win10-style: зажал 300мс, потом тащишь
        // ═══════════════════════════════════════════════════════════════════

        // Сброс всего состояния drag'а
        void DragReset(bool restoreOpacity = true)
        {
            _dragHoldTimer?.Stop();
            _dragHoldTimer = null;
            if (restoreOpacity && _dragBtn != null)
                _dragBtn.Opacity = 1.0;
            _dragBtn    = null;
            _dragActive = false;
        }

        // Нажатие ЛКМ на панели — запоминаем кнопку и запускаем таймер 300мс
        void AppIcons_DragMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Находим кнопку под курсором
            var hit = AppIcons.InputHitTest(e.GetPosition(AppIcons)) as DependencyObject;
            Button btn = null;
            while (hit != null)
            {
                if (hit is Button b && AppIcons.Children.Contains(b)) { btn = b; break; }
                hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
            }
            if (btn == null) return;

            DragReset();
            _dragBtn    = btn;
            _dragOrigin = e.GetPosition(AppIcons);
            _dragActive = false;

            try { MarkOwnActivity(); IntPtr mh = new WindowInteropHelper(this).Handle; IntPtr fg = GetForegroundWindow(); if (fg != mh && fg != IntPtr.Zero) _lastForegroundWindow = fg; } catch { }

            // Таймер 300мс — как Win10: просто удержи, не двигай
            _dragHoldTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(300) };
            _dragHoldTimer.Tick += (ts, te) =>
            {
                _dragHoldTimer?.Stop();
                // Проверяем: кнопка та же и мышь всё ещё зажата
                if (_dragBtn == btn && Mouse.LeftButton == MouseButtonState.Pressed)
                {
                    _dragActive = true;
                    btn.Opacity = 0.65;
                    _previewShowTimer?.Stop();
                    _pendingPreviewGroup = null;
                    _pendingPreviewBtn   = null;
                    HidePreview();
                    // Захватываем мышь на панели чтобы не терять события
                    AppIcons.CaptureMouse();
                }
            };
            _dragHoldTimer.Start();
        }

        // Движение мыши — переставляем кнопку только когда drag активен
        void AppIcons_DragMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_dragActive || _dragBtn == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { DragCancel(); return; }
            try
            {
                var pos  = e.GetPosition(AppIcons);
                var btn  = _dragBtn;
                var dx   = pos.X - _dragOrigin.X;

                Button target = null;
                foreach (UIElement child in AppIcons.Children)
                {
                    if (!(child is Button b) || b == btn) continue;
                    var bp  = b.TranslatePoint(new System.Windows.Point(0, 0), AppIcons);
                    var bnd = new Rect(bp, new System.Windows.Size(b.ActualWidth, b.ActualHeight));
                    if (!bnd.Contains(pos)) continue;
                    double relX    = pos.X - bnd.Left;
                    bool   overHalf = dx > 0 ? relX >= bnd.Width * 0.5 : relX <= bnd.Width * 0.5;
                    if (overHalf) { target = b; break; }
                }
                if (target == null) return;

                int si = AppIcons.Children.IndexOf(btn);
                int ti = AppIcons.Children.IndexOf(target);
                if (si < 0 || ti < 0 || si == ti) return;

                AppIcons.Children.Remove(btn);
                AppIcons.Children.Insert(ti, btn);
                _dragOrigin = pos;
                UpdatePinOrderFromPanel();
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] DragMouseMove: {ex.Message}"); }
        }

        // Отпустили ЛКМ — завершаем drag, подавляем Click если было реальное перемещение
        void AppIcons_DragMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            AppIcons.ReleaseMouseCapture();
            bool wasActive = _dragActive;
            DragReset();
            if (wasActive)
            {
                e.Handled = true;
                _lastForegroundWindow = IntPtr.Zero;
            }
        }

        // Мышь ушла с панели во время drag'а — отменяем
        void AppIcons_DragMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_dragActive) DragReset();
            // Если drag активен — CaptureMouse держит события, MouseLeave не придёт
        }

        // Внутренняя отмена drag'а без подавления Click
        void DragCancel()
        {
            AppIcons.ReleaseMouseCapture();
            DragReset();
        }

        void UpdatePinOrderFromPanel()
        {
            try
            {
                // Строим словарь Button → AppGroup для быстрого поиска
                var btnToGroup = _groups.Values
                    .Where(g => g.Button != null)
                    .ToDictionary(g => g.Button, g => g);

                // Проходим по кнопкам в их текущем визуальном порядке
                int pinnedOrder = 0;
                var newPinnedList = new List<PinnedApp>();
                foreach (UIElement child in AppIcons.Children)
                {
                    if (!(child is Button b)) continue;
                    if (!btnToGroup.TryGetValue(b, out var g)) continue;
                    if (!g.IsPinned) continue;
                    g.PinOrder = pinnedOrder++;
                    // Формируем путь для сохранения
                    string path = g.LaunchPath ?? g.ExeName;
                    newPinnedList.Add(new PinnedApp
                    {
                        Name = g.ExeName,
                        Path = path,
                        Tooltip = g.PinnedTooltip ?? g.LastTitle ?? g.ExeName
                    });
                }
                PinnedAppsManager.Save(newPinnedList);
                Debug.WriteLine($"[MyTaskbar] UpdatePinOrderFromPanel: saved {newPinnedList.Count} pinned apps");
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UpdatePinOrderFromPanel: {ex.Message}"); }
        }

        AppGroup GetOrCreateGroup(string exeName, BitmapSource icon, string tooltip)
        {
            if (string.IsNullOrEmpty(exeName)) exeName = "unknown";
            if (_groups.TryGetValue(exeName, out var ex2)) { if (ex2.Icon == null && icon != null) ex2.Icon = icon; if (!string.IsNullOrEmpty(tooltip) && string.IsNullOrEmpty(ex2.PinnedTooltip)) ex2.PinnedTooltip = tooltip; return ex2; }
            bool uwpIcon = IsUwpAppName(exeName);
            var group = new AppGroup { ExeName = exeName, GroupKey = exeName, Icon = icon, LastTitle = tooltip ?? exeName, PinnedTooltip = tooltip ?? exeName, IsUwp = uwpIcon };
            Button btn;
            try { btn = CreateAppButton(tooltip, icon, uwpIcon ? 22 : 24); }
            catch { btn = new Button { Content = exeName.Substring(0, Math.Min(1, exeName.Length)).ToUpper() }; }
            SetupGroupButton(btn, group);
            group.Button = btn; _groups[exeName] = group;
            try { AddButtonAnimated(btn); } catch (Exception e) { Debug.WriteLine($"[MyTaskbar] AppIcons.Add: {e.Message}"); }
            return group;
        }

        BitmapSource GetCachedIcon(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_iconCache.TryGetValue(key, out var c)) return c;
            try
            {
                var i = TryShellItemImage(key, 32) ?? TrySHGetFileInfo(key, 32) ?? IconHelper.GetIconFromExe(key, 32);
                if (i != null) _iconCache[key] = i;
                return i;
            }
            catch { return null; }
        }

        void SetupGroupButton(Button btn, AppGroup group)
        {
            if (btn == null || group == null) return;
            btn.MouseEnter += (s, e) =>
            {
                try
                {
                    _previewHideTimer?.Stop();
                    _previewShowTimer?.Stop();
                    _pendingPreviewGroup = group;
                    _pendingPreviewBtn = btn;
                    // [FIX-PREVIEW] Если preview уже открыт — переключаем немедленно,
                    // без задержки 500мс. Это убирает "залипание" при наведении по очереди.
                    if (_previewWindow != null && _previewWindow.IsVisible && _currentPreviewGroup != null)
                        ShowPreview(group, btn);
                    else
                        _previewShowTimer?.Start();
                }
                catch { }
            };
            btn.MouseLeave += (s, e) =>
            {
                try
                {
                    _previewShowTimer?.Stop();
                    // [FIX-PREVIEW] Сбрасываем pending только если уходим в никуда
                    // (не в другую кнопку). Другая кнопка сразу поставит свои значения в MouseEnter.
                    _pendingPreviewGroup = null;
                    _pendingPreviewBtn = null;
                    ScheduleHidePreview();
                }
                catch { }
            };
            btn.Click += (s, e) =>
            {
                try
                {
                    if (_menuWindow != null && _menuWindow.IsVisible) _menuWindow.HideAnimated();
                    _previewShowTimer?.Stop(); _pendingPreviewGroup = null; _pendingPreviewBtn = null;
                    _previewHideTimer?.Stop(); HidePreview();
                    AllowSetForegroundWindow(ASFW_ANY);
                    if (group.Hwnds.Count == 0)
                    {
                        if (!string.IsNullOrWhiteSpace(group.LaunchPath))
                            try { Process.Start(new ProcessStartInfo(group.LaunchPath) { UseShellExecute = true }); } catch { }
                        return;
                    }
                    if (group.Hwnds.Count == 1)
                    {
                        IntPtr hwnd = group.Hwnds[0]; bool uwp = IsUwpAppName(group.ExeName);
                        // [FIX-TASKMGR-RESTORE] IsIconic не работает для привилегированных окон
                        // (taskmgr, regedit) — используем GetWindowPlacement через IsWindowMinimized.
                        // ShowWindow(SW_RESTORE) тоже игнорируется — используем SC_RESTORE.
                        bool minimized = IsIconic(hwnd) || IsWindowMinimized(hwnd);
                        if (minimized)
                        {
                            SuspendBlockerForAppWindow();
                            // SC_RESTORE разворачивает окно через очередь сообщений —
                            // работает даже для привилегированных процессов
                            PostMessage(hwnd, 0x0112 /*WM_SYSCOMMAND*/, new IntPtr(0xF120 /*SC_RESTORE*/), IntPtr.Zero);
                            SetForegroundWindow(hwnd);
                            if (uwp) ActivateUwpWindow(hwnd);
                        }
                        else if (_lastForegroundWindow == hwnd || GetForegroundWindow() == hwnd)
                        {
                            // [FIX-TASKMGR-TOGGLE] ShowWindow(SW_MINIMIZE) игнорируется
                            // привилегированными окнами — используем SC_MINIMIZE.
                            PostMessage(hwnd, 0x0112 /*WM_SYSCOMMAND*/, new IntPtr(0xF020 /*SC_MINIMIZE*/), IntPtr.Zero);
                        }
                        else
                        {
                            SuspendBlockerForAppWindow();
                            // [FIX-MAXIMIZE] Не посылаем SC_RESTORE если окно не минимизировано —
                            // иначе maximized-окно (Chrome в fullscreen) схлопывается до нормального размера.
                            // Просто переводим фокус на уже видимое окно.
                            SetForegroundWindow(hwnd);
                            if (uwp) ActivateUwpWindow(hwnd);
                        }
                    }
                    else
                    {
                        IntPtr active = GetForegroundWindow();
                        int idx = group.Hwnds.IndexOf(active);
                        IntPtr next = group.Hwnds[(idx + 1) % group.Hwnds.Count];
                        SuspendBlockerForAppWindow();
                        // [FIX-MAXIMIZE] Только восстанавливаем если минимизировано — иначе не трогаем WindowState
                        if (IsIconic(next) || IsWindowMinimized(next)) ShowWindow(next, SW_RESTORE);
                        SetForegroundWindow(next);
                        if (IsUwpAppName(group.ExeName)) ActivateUwpWindow(next);
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] btn.Click: {ex.Message}"); }
            };
            btn.MouseRightButtonUp += (s, e) => { try { e.Handled = true; ShowContextMenu(btn, group); } catch { } };
        }

        // Закрытие окна: для UWP и привилегированных процессов (taskmgr и др.)
        // используем WM_SYSCOMMAND+SC_CLOSE, для обычных — WM_CLOSE
        void CloseWindow(IntPtr hwnd, bool isUwp)
        {
            try
            {
                if (!IsWindow(hwnd)) return;
                if (isUwp)
                {
                    PostMessage(hwnd, 0x0112 /*WM_SYSCOMMAND*/, new IntPtr(0xF060 /*SC_CLOSE*/), IntPtr.Zero);
                }
                else
                {
                    // [FIX-TASKMGR-CLOSE] Привилегированные окна (taskmgr, regedit)
                    // могут игнорировать WM_CLOSE. SC_CLOSE имитирует нажатие кнопки «×»
                    // и обрабатывается такими окнами корректно.
                    PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    PostMessage(hwnd, 0x0112 /*WM_SYSCOMMAND*/, new IntPtr(0xF060 /*SC_CLOSE*/), IntPtr.Zero);
                }
            }
            catch { }
        }

        void ActivateUwpWindow(IntPtr frameHwnd)
        {
            if (frameHwnd == IntPtr.Zero || !IsWindow(frameHwnd)) return;
            try
            {
                AllowSetForegroundWindow(ASFW_ANY);
                if (IsIconic(frameHwnd)) ShowWindow(frameHwnd, SW_RESTORE); else ShowWindow(frameHwnd, SW_SHOW);
                SetForegroundWindow(frameHwnd);
                IntPtr child = FindWindowEx(frameHwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (child == IntPtr.Zero) child = FindWindowEx(frameHwnd, IntPtr.Zero, "ApplicationFrameInputSinkWindow", null);
                if (child != IntPtr.Zero) SetForegroundWindow(child);
            }
            catch { }
        }

    }
}
