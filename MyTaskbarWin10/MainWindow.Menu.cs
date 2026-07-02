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
// MainWindow.Menu.cs — Menu window, Start button highlight, button animation
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // MENU
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Публичный доступ к MenuWindow для вторичной панели.
        /// Позволяет SecondaryTaskbarWindow установить SourceMonitorRect перед ShowMenu.
        /// </summary>
        public MenuWindow PublicMenuWindow
        {
            get
            {
                EnsureMenuWindow();
                Debug.WriteLine("[MainWindow.PublicMenuWindow] геттер вызван, _menuWindow=" + (_menuWindow == null ? "null" : "ok"));
                return _menuWindow;
            }
        }

        // [FIX-SECONDARY-HIDE] true если открыт любой попап/фрейм запущенный с панели.
        // SecondaryTaskbarWindow проверяет это чтобы не скрыться пока открыт, например, трей или WiFi.
        // Внимание: TrayWindow всегда Show()н (прячется за край экрана), поэтому
        // проверяем IsOpen, а не IsVisible.
        bool _langContextMenuOpen = false; // true пока открыто контекстное меню выбора языка
        public bool IsAnyFlyoutOpen =>
            (_trayWindow       != null && _trayWindow.IsOpen)          ||
            (_wifiWindow       != null && _wifiWindow.IsVisible)       ||
            (_volumeFlyout     != null && _volumeFlyout.IsVisible)     ||
            (_brightnessFlyout != null && _brightnessFlyout.IsVisible) ||
            (_calendarWindow   != null && _calendarWindow.IsVisible)   ||
            (_menuWindow       != null && _menuWindow.IsVisible)       ||
            _langContextMenuOpen;

        void EnsureMenuWindow()
        {
            if (_menuWindow != null) return;
            _menuWindow = new MenuWindow(); _menuWindow.UIScale = _uiScale; _menuWindow.AnimEnabled = _menuAnimEnabled;
            // [FIX-2.2] Обновляем кэш HWND при создании MenuWindow
            _menuWindow.SourceInitialized += (_, _e) => SafeRun(RefreshOwnHwnds, nameof(RefreshOwnHwnds));
            _menuWindow.MenuVisibilityChanged += isOpen =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SetStartButtonHighlight(isOpen);
                    // ClipCursor трогаем ТОЛЬКО если блокер активен (FPS-режим).
                    // Без активного блокера курсор свободен — не ограничиваем его.
                    if (_blockerWindow == null || !_blockerWindow.IsBlockerActive) return;
                    if (isOpen)
                    {
                        // ClipCursor расширяется позже в MenuPositionReady —
                        // когда меню достигнет финальной позиции после анимации.
                        _blockerWindow.MenuWindow = _menuWindow;
                    }
                    else
                    {
                        if (_appOpenedOverFullscreen) return;
                        // Меню закрылось — сужаем зону обратно до одного тулбара
                        _blockerWindow.MenuWindow = null;
                        _blockerWindow.UpdateClipZone();
                    }
                }));
            // [GAME-9] Приложение запущено из меню Пуск — снимаем блокер
            _menuWindow.AppLaunched += () =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SuspendBlockerForAppWindow();
                }));
            // [CLIP-FIX] Меню достигло финальной позиции — расширяем ClipCursor
            // только если блокер активен (FPS-режим)
            _menuWindow.MenuPositionReady += () =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_blockerWindow == null || !_blockerWindow.IsBlockerActive) return;
                    _blockerWindow.MenuWindow = _menuWindow;
                    _blockerWindow.UpdateClipZone();
                }));
            _menuWindow.IsVisibleChanged += (s, e) =>
            {
                if (!(bool)e.NewValue) Dispatcher.BeginInvoke(new Action(() => SetStartButtonHighlight(false)));
            };
            // [BLUR-FIX-2] Після повного завершення анімації ховання меню (меню фізично за екраном)
            // Disable→Enable toggle убран: он вызывал мерцание панели после закрытия меню Пуск.
            // ApplyAcrylicBackground вызывать не нужно — акрил и так активен всё время.
            _menuWindow.MenuHideCompleted += () => { };
            _menuWindow.TaskbarWindow = this;
            _menuWindow.PreviewWindow = _previewWindow;
        }

        void SetStartButtonHighlight(bool active)
        {
            try
            {
                if (StartButton == null) return;
                StartButton.ApplyTemplate();
                var ab = StartButton.Template?.FindName("AppBorder", StartButton) as Border;
                if (ab != null)
                {
                    ab.Background = active ? BrushActiveBg : BrushTransparent;
                    var rb = StartButton.Template?.FindName("RunBar", StartButton) as Rectangle;
                    if (rb != null) rb.Visibility = Visibility.Collapsed;
                }
                else
                    StartButton.Background = active
                        ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255))
                        : Brushes.Transparent;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] SetStartButtonHighlight: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // АНИМАЦИЯ КНОПОК
        // ═════════════════════════════════════════════════════════════════════
        void AddButtonAnimated(Button btn)
        {
            btn.Width = 40; btn.Opacity = 0;
            EnsureTranslateTransform(btn).Y = -28;
            AppIcons.Children.Add(btn);
            btn.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                var slideIn = new DoubleAnimation { From = -28, To = 0, Duration = TimeSpan.FromMilliseconds(BTN_SLIDE_IN_MS), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                var fadeIn = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(BTN_SLIDE_IN_MS), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                slideIn.Completed += (s, e) =>
                {
                    try
                    {
                        btn.BeginAnimation(OpacityProperty, null);
                        EnsureTranslateTransform(btn).BeginAnimation(TranslateTransform.YProperty, null);
                        btn.Opacity = 1; EnsureTranslateTransform(btn).Y = 0; PositionTaskbar();
                    }
                    catch { }
                };
                btn.BeginAnimation(OpacityProperty, fadeIn);
                EnsureTranslateTransform(btn).BeginAnimation(TranslateTransform.YProperty, slideIn);
            }));
        }

        void RemoveButtonAnimated(Button btn, AppGroup group)
        {
            if (btn == null || _removingButtons.Contains(btn)) return;
            _removingButtons.Add(btn);
            var slideOut = new DoubleAnimation { From = 0, To = -28, Duration = TimeSpan.FromMilliseconds(BTN_SLIDE_OUT_MS), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            var fadeOut = new DoubleAnimation { From = btn.Opacity, To = 0, Duration = TimeSpan.FromMilliseconds(BTN_SLIDE_OUT_MS), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            slideOut.Completed += (s, e) =>
            {
                try
                {
                    btn.BeginAnimation(OpacityProperty, null);
                    EnsureTranslateTransform(btn).BeginAnimation(TranslateTransform.YProperty, null);
                    AppIcons.Children.Remove(btn); _buttonStateCache.Remove(btn); _removingButtons.Remove(btn); PositionTaskbar();
                }
                catch { }
            };
            btn.BeginAnimation(OpacityProperty, fadeOut);
            EnsureTranslateTransform(btn).BeginAnimation(TranslateTransform.YProperty, slideOut);
        }

        static TranslateTransform EnsureTranslateTransform(UIElement el)
        {
            if (el.RenderTransform is TranslateTransform tt) return tt;
            var t = new TranslateTransform(0, 0);
            el.RenderTransform = t; el.RenderTransformOrigin = new Point(0.5, 0.5);
            return t;
        }

        // ──────────────────────────────────────────────────────────────
        // Calendar window (clock click handler)
        // ──────────────────────────────────────────────────────────────
        private CalendarWindow _calendarWindow;
        private DateTime _calendarClosedAt = DateTime.MinValue;

        private void ClockBorder_MouseLeftButtonDown(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            try
            {
                MarkOwnActivity();
                if ((DateTime.UtcNow - _calendarClosedAt).TotalMilliseconds < 300) return;

                if (_calendarWindow == null)
                {
                    _calendarWindow = new CalendarWindow();
                    _calendarWindow.IsVisibleChanged += (s2, ev) =>
                    {
                        if (!(bool)ev.NewValue)
                        {
                            _calendarClosedAt = DateTime.UtcNow;
                        }
                    };
                }

                // Если уже видно - скрыть и выйти
                if (_calendarWindow.IsVisible)
                {
                    _calendarWindow.Hide();
                    return;
                }

                // Позиционирование как у яркости/громкости
                var button = sender as Button;
                if (button != null)
                {
                    var pos = button.PointToScreen(new System.Windows.Point(0, 0));
                    double buttonCenterX = pos.X + button.ActualWidth / 2;
                    double calTopFallback = _isBottom
                        ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - 180 - 4
                        : TASKBAR_HEIGHT + 4;
                    bool isBot = _isBottom;
                    
                    _calendarWindow.ShowAtCentered(buttonCenterX, calTopFallback, actualH =>
                        isBot
                            ? SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - actualH - 4
                            : TASKBAR_HEIGHT + 4);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] ClockBorder_MouseLeftButtonDown: {ex.Message}"); }
        }

    }
}
