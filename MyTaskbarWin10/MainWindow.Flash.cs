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
// MainWindow.Flash.cs — Flash timer, button highlight states
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // FLASH
        // ═════════════════════════════════════════════════════════════════════
        void StartFlashTimer()
        {
            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _flashTimer.Tick += (s, e) =>
            {
                try
                {
                    _flashState = !_flashState;
                    foreach (var b in _flashingButtons.ToList())
                    {
                        b.ApplyTemplate();
                        var rb = b.Template?.FindName("RunBar", b) as Rectangle;
                        var ab = b.Template?.FindName("AppBorder", b) as Border;
                        if (ab != null) ab.Background = _flashState ? BrushFlashOn : BrushTransparent;
                        // Пипка: ярко-оранжевая когда on, скрытая когда off
                        if (rb != null)
                        {
                            rb.Visibility = _flashState ? Visibility.Visible : Visibility.Collapsed;
                            if (_flashState) rb.Fill = BrushFlashPip;
                        }
                    }
                }
                catch { }
            };
        }

        void StartFlashForGroup(AppGroup g) { if (g?.Button == null) return; try { _flashingButtons.Add(g.Button); if (!_flashTimer.IsEnabled) _flashTimer.Start(); } catch { } }
        void StopFlash(AppGroup g) { if (g?.Button == null) return; try { _flashingButtons.Remove(g.Button); HighlightButton(g.Button, false); if (_flashingButtons.Count == 0) _flashTimer.Stop(); } catch { } }
        void HighlightButton(Button btn, bool on) { if (btn == null) return; try { btn.ApplyTemplate(); var ab = btn.Template?.FindName("AppBorder", btn) as Border; if (ab != null) ab.Background = on ? BrushFlashOn : BrushTransparent; } catch { } }

        void ApplyButtonState(Button btn, string state)
        {
            if (btn == null) return;
            try
            {
                // [ATTENTION] Если окно стало активным — сбрасываем флаг внимания
                if (state == "Active")
                {
                    var flashGroup = _groups.Values.FirstOrDefault(g => g.Button == btn);
                    if (flashGroup != null && flashGroup.NeedsAttention)
                        ClearAttention(flashGroup);
                    else if (_flashingButtons.Contains(btn))
                        StopFlash(_groups.Values.FirstOrDefault(g => g.Button == btn));
                }
                else if (_flashingButtons.Contains(btn) && state == "Active")
                    StopFlash(_groups.Values.FirstOrDefault(g => g.Button == btn));

                // Если кнопка в attention-режиме (статичный фон) — не затирать его при Running
                var attGroup = _groups.Values.FirstOrDefault(g => g.Button == btn);
                if (attGroup != null && attGroup.NeedsAttention && state == "Running")
                {
                    // Оставить оранжевый фон + показать пипку в оранжевом
                    ApplyAttentionBackground(btn, true);
                    return;
                }

                btn.ApplyTemplate();
                var rb = btn.Template?.FindName("RunBar", btn) as Rectangle;
                var ab = btn.Template?.FindName("AppBorder", btn) as Border;
                if (rb == null || ab == null) return;
                switch (state)
                {
                    case "Active": rb.Visibility = Visibility.Visible; rb.Fill = BrushActiveBar; ab.Background = BrushActiveBg; break;
                    case "Running": rb.Visibility = Visibility.Visible; rb.Fill = BrushRunningBar; ab.Background = BrushTransparent; break;
                    default: rb.Visibility = Visibility.Collapsed; ab.Background = BrushTransparent; break;
                }
            }
            catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        // POSITION / CLOCK / START BUTTON
        // ═════════════════════════════════════════════════════════════════════
        void Window_SizeChanged(object sender, SizeChangedEventArgs e) { try { PositionTaskbar(); } catch { } }

        // [STAB-14] IsLoaded-guard
        void PositionTaskbar()
        {
            try
            {
                if (!IsLoaded || !IsVisible) return;
                UpdateLayout();

                var source = PresentationSource.FromVisual(this);
                double dpi = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

                // [FIX-CENTER-v2] Считаем всё в физических пикселях, затем конвертируем.
                // SystemParameters.PrimaryScreenWidth — в DIP, умножаем → физические пиксели экрана.
                // ActualWidth — в DIP, умножаем → физические пиксели панели.
                // Целочисленное деление в пикселях даёт идеальный центр без погрешности округления.
                double screenPx = Math.Round(SystemParameters.PrimaryScreenWidth * dpi);
                double panelPx = Math.Round(ActualWidth * dpi);
                double leftPx = Math.Floor((screenPx - panelPx) / 2.0);
                Left = leftPx / dpi;

                if (!_taskbarAnimating)
                    Top = _isBottom ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT : 0;
                Height = TASKBAR_HEIGHT;

                // Обновляем 1px-линии поверх контента (поверх RunBar-индикаторов)
                // Bottom-режим: линия сверху, Top-режим: линия снизу; правая — всегда
                if (BorderLineTop != null)
                    BorderLineTop.Visibility = _isBottom ? Visibility.Visible : Visibility.Collapsed;
                if (BorderLineBottom != null)
                    BorderLineBottom.Visibility = _isBottom ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] PositionTaskbar: {ex.Message}"); }
        }

        // [FIX-DRAG] Добавляем WS_EX_LAYERED к окну.
        // Без этого флага HTTRANSPARENT в NcHitTestHook не работает корректно на всех билдах Windows.
        // WS_EX_TRANSPARENT намеренно НЕ выставляем глобально — тогда нельзя кликать по кнопкам.
        void ApplyLayeredStyle()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] ApplyLayeredStyle: {ex.Message}"); }
        }

        // [FIX-DRAG2] Регистрируем пустой IDropTarget — это говорит Windows что окно
        // участвует в OLE drag&drop, но мы отдаём DROPEFFECT_NONE, тем самым
        // позволяя дропу «провалиться» к окну ниже.
        PassthroughDropTarget _passthroughDrop;
        void RegisterPassthroughDrop()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                _passthroughDrop = new PassthroughDropTarget();
                RegisterDragDrop(hwnd, _passthroughDrop);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] RegisterPassthroughDrop: {ex.Message}"); }
        }

    }
}
