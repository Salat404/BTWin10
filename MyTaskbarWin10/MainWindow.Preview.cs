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
// MainWindow.Preview.cs — DWM thumbnail preview window
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // PREVIEW [STAB-3] [STAB-9]
        // ═════════════════════════════════════════════════════════════════════
        void InitPreviewWindow()
        {
            _previewWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Visibility = Visibility.Hidden
            };
            _previewWindow.SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(_previewWindow).Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
            };
            _previewWindow.Show(); _previewWindow.Hide();
            _previewWindow.MouseEnter += (s, e) => { _previewHideTimer?.Stop(); _previewShowTimer?.Stop(); };
            _previewWindow.MouseLeave += (s, e) => ScheduleHidePreview();
            if (_menuWindow != null) { _menuWindow.PreviewWindow = _previewWindow; _menuWindow.TaskbarWindow = this; }
            _previewHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _previewHideTimer.Tick += (s, e) => { _previewHideTimer.Stop(); HidePreview(); };
            _previewShowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _previewShowTimer.Tick += (s, e) =>
            {
                _previewShowTimer.Stop();
                if (_pendingPreviewGroup != null && _pendingPreviewBtn != null)
                    ShowPreview(_pendingPreviewGroup, _pendingPreviewBtn);
            };
            // [FIX-PREVIEW] Вспомогательный метод: показать немедленно или запустить таймер
        }

        (FrameworkElement card, Border thumbSlot) CreatePreviewCard(IntPtr hwnd, AppGroup group)
        {
            // [STAB-4] SafeGetWindowText
            string title = SafeGetWindowText(hwnd); if (string.IsNullOrEmpty(title)) title = group?.ExeName ?? "—";
            IntPtr cap = hwnd;
            var nb = Frozen(Color.FromArgb(220, 32, 32, 32)); var hb = Frozen(Color.FromArgb(255, 50, 50, 55));
            var nb2 = Frozen(Color.FromArgb(60, 255, 255, 255)); var hb2 = Frozen(Color.FromArgb(120, 255, 255, 255));
            var cbPath = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 9.5,9.5 L 18.5,18.5 M 18.5,9.5 L 9.5,18.5"), Stroke = new SolidColorBrush(Color.FromRgb(255, 255, 255)), StrokeThickness = 1.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = PREVIEW_TITLE_HEIGHT, Height = PREVIEW_TITLE_HEIGHT, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true, UseLayoutRounding = true, Stretch = Stretch.None };
            var cb = new Border { Tag = "close", Width = PREVIEW_TITLE_HEIGHT, Height = PREVIEW_TITLE_HEIGHT, Background = Brushes.Transparent, CornerRadius = new CornerRadius(0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Cursor = Cursors.Hand, Visibility = Visibility.Hidden, Child = cbPath };
            var card = new Border { Width = PREVIEW_CARD_WIDTH, Background = nb, BorderBrush = nb2, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(0), Margin = new Thickness(PREVIEW_GAP, 0, PREVIEW_GAP, 0), Cursor = Cursors.Hand, SnapsToDevicePixels = true, UseLayoutRounding = true };
            card.MouseEnter += (s, e) => { _previewHideTimer?.Stop(); card.Background = hb; card.BorderBrush = hb2; cb.Visibility = Visibility.Visible; };
            card.MouseLeave += (s, e) => { card.Background = nb; card.BorderBrush = nb2; cb.Visibility = Visibility.Hidden; ScheduleHidePreview(); };
            card.MouseLeftButtonUp += (s, e) =>
            {
                try
                {
                    if (e.OriginalSource is Border b2 && b2.Tag is string t2 && t2 == "close") return; HidePreview(); _menuWindow?.HideAnimated();
                    // [STAB-11]
                    if (!IsWindow(cap)) return;
                    if (IsIconic(cap)) ShowWindow(cap, SW_RESTORE); SetForegroundWindow(cap);
                }
                catch { }
            };
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PREVIEW_TITLE_HEIGHT) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PREVIEW_THUMB_HEIGHT) });
            var tr = new Grid { Margin = new Thickness(0) };
            tr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            tr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PREVIEW_TITLE_HEIGHT) });
            Grid.SetRow(tr, 0);
            if (group?.Icon != null) { var ii = new System.Windows.Controls.Image { Source = group.Icon, Width = 14, Height = 14, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, SnapsToDevicePixels = true, Margin = new Thickness(4, 0, 0, 0) }; RenderOptions.SetBitmapScalingMode(ii, BitmapScalingMode.HighQuality); Grid.SetColumn(ii, 0); tr.Children.Add(ii); }
            string st = title.Length > 24 ? title.Substring(0, 24) + "…" : title;
            var tt = new TextBlock { Text = st, Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), FontSize = 10, FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 2, 0) };
            Grid.SetColumn(tt, 1); tr.Children.Add(tt);
            cb.MouseEnter += (s, e) => cb.Background = new SolidColorBrush(Color.FromArgb(220, 232, 17, 35));
            cb.MouseLeave += (s, e) => cb.Background = Brushes.Transparent;
            cb.MouseLeftButtonUp += (s, e) =>
            {
                try
                {
                    e.Handled = true;
                    // [STAB-13] IsWindow перед PostMessage
                    CloseWindow(cap, IsUwpAppName(group.ExeName));
                    if (card.Parent is Panel pp) pp.Children.Remove(card);
                    if (_previewWindow?.Content is Border ob && ob.Child is StackPanel sp && sp.Children.Count == 0) HidePreview();
                }
                catch { }
            };
            Grid.SetColumn(cb, 2); tr.Children.Add(cb); root.Children.Add(tr);
            var ts = new Border { Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), Margin = new Thickness(4, 0, 4, 4), SnapsToDevicePixels = true, UseLayoutRounding = true };
            Grid.SetRow(ts, 1); root.Children.Add(ts); card.Child = root;
            return (card, ts);
        }

        void ShowPreview(AppGroup group, Button btn)
        {
            try
            {
                _previewHideTimer?.Stop();
                if (group == null || group.Hwnds.Count == 0) { HidePreview(); return; }
                DwmIsCompositionEnabled(out bool dwm); if (!dwm) return;
                _currentPreviewGroup = group;
                IntPtr ph; try { ph = new WindowInteropHelper(_previewWindow).Handle; } catch { return; }
                if (ph == IntPtr.Zero) { _previewWindow.Show(); _previewWindow.Hide(); ph = new WindowInteropHelper(_previewWindow).Handle; if (ph == IntPtr.Zero) return; }
                UnregisterAllThumbnails();
                int cnt = Math.Min(group.Hwnds.Count, 8);
                double tw = cnt * (PREVIEW_CARD_WIDTH + PREVIEW_GAP * 2) + PREVIEW_PADDING * 2;
                var ob = new Border { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(PREVIEW_PADDING), SnapsToDevicePixels = true };
                var sp = new StackPanel { Orientation = Orientation.Horizontal, SnapsToDevicePixels = true };
                var slots = new List<(Border, IntPtr)>(cnt);
                for (int i = 0; i < cnt; i++)
                {
                    var h = group.Hwnds[i];
                    // [STAB-3] Пропуск зависших окон в preview
                    if (!IsWindow(h)) continue;
                    try { if (IsHungAppWindow(h)) continue; } catch { continue; }
                    try { var (c, ts2) = CreatePreviewCard(h, group); sp.Children.Add(c); slots.Add((ts2, h)); } catch { }
                }
                if (sp.Children.Count == 0) { HidePreview(); return; }
                ob.Child = sp; _previewWindow.Content = ob; _previewWindow.SizeToContent = SizeToContent.WidthAndHeight; _previewWindow.UpdateLayout();
                var bp = btn.PointToScreen(new System.Windows.Point(0, 0)); double sw2 = SystemParameters.PrimaryScreenWidth;
                double left = bp.X + btn.ActualWidth / 2 - tw / 2; if (left < 4) left = 4; if (left + tw > sw2 - 4) left = sw2 - tw - 4;
                _previewWindow.Left = left;
                _previewWindow.Top = _isBottom
                    ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - _previewWindow.ActualHeight - 6
                    : TASKBAR_HEIGHT + 6;
                _previewWindow.Visibility = Visibility.Visible; _previewWindow.UpdateLayout();
                foreach (var (ts2, h) in slots) try { RegisterThumbnailForSlot(ph, h, ts2); } catch { }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] ShowPreview: {ex.Message}"); }
        }

        void RegisterThumbnailForSlot(IntPtr dest, IntPtr src, Border slot)
        {
            // [STAB-3] Полная проверка перед DwmRegisterThumbnail
            if (dest == IntPtr.Zero || src == IntPtr.Zero || slot == null) return;
            if (!IsWindow(src)) return;
            try { if (IsHungAppWindow(src)) return; } catch { return; }
            try
            {
                if (DwmRegisterThumbnail(dest, src, out IntPtr th) != 0) return;
                // [STAB-9] lock при добавлении в список
                lock (_thumbnailLock) { _thumbnailHandles.Add(th); }
                var pos = slot.PointToScreen(new System.Windows.Point(0, 0));
                int dL = (int)(pos.X - _previewWindow.Left), dT = (int)(pos.Y - _previewWindow.Top);
                int dR = dL + Math.Max(1, (int)slot.ActualWidth), dB = dT + Math.Max(1, (int)slot.ActualHeight);
                if (dR <= dL) dR = dL + PREVIEW_CARD_WIDTH - 8; if (dB <= dT) dB = dT + PREVIEW_THUMB_HEIGHT - 4;
                var props = new DWM_THUMBNAIL_PROPERTIES { dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY, rcDestination = new RECT { left = dL, top = dT, right = dR, bottom = dB }, opacity = 255, fVisible = true };
                DwmUpdateThumbnailProperties(th, ref props);
            }
            catch { }
        }

        // [STAB-9] Атомарная очистка thumbnail-хэндлов
        void UnregisterAllThumbnails()
        {
            List<IntPtr> toFree;
            lock (_thumbnailLock)
            {
                toFree = new List<IntPtr>(_thumbnailHandles);
                _thumbnailHandles.Clear();
            }
            foreach (var h in toFree)
                try { if (h != IntPtr.Zero) DwmUnregisterThumbnail(h); } catch { }
        }

        void ScheduleHidePreview() { try { _previewHideTimer?.Stop(); _previewHideTimer?.Start(); } catch { } }
        void HidePreview()
        {
            try
            {
                // [FIX-PREVIEW] Останавливаем оба таймера и чистим всё состояние,
                // чтобы при следующем наведении всё начиналось с чистого листа.
                _previewShowTimer?.Stop();
                _previewHideTimer?.Stop();
                _pendingPreviewGroup = null;
                _pendingPreviewBtn = null;
                UnregisterAllThumbnails();
                if (_previewWindow != null) _previewWindow.Visibility = Visibility.Hidden;
                _currentPreviewGroup = null;
            }
            catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        // [DPI-AUTO] Авто-масштабирование при смене DPI/масштаба Windows
        // ═════════════════════════════════════════════════════════════════════
        IntPtr DpiChangedHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_DPICHANGED) return IntPtr.Zero;
            try
            {
                // wParam: LOWORD = новый DPI по X, HIWORD = новый DPI по Y
                int newDpiX = wParam.ToInt32() & 0xFFFF;
                if (newDpiX <= 0) newDpiX = 96;
                double newScale = newDpiX / 96.0;
                if (Math.Abs(newScale - _currentDpiScale) < 0.001) return IntPtr.Zero;

                _currentDpiScale = newScale;
                Debug.WriteLine($"[DPI-AUTO] DPI changed: {newDpiX} ({newScale * 100:F0}%)");

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        SafeRun(PositionTaskbar, "PositionTaskbar@DpiChanged");
                        SafeRun(ReserveScreenSpace, "ReserveScreenSpace@DpiChanged");
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch { }
            return IntPtr.Zero;
        }

    }
}
