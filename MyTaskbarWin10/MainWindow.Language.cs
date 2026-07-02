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
// MainWindow.Language.cs — Language/input layout tracker and switcher
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // ЯЗЫК
        // ═════════════════════════════════════════════════════════════════════
        // ── Language tracker ────────────────────────────────────────────────────
        // Автоопределение установленных языков, как в Win10:
        //   ЛКМ по кнопке → переключить на следующий язык
        //   ПКМ по кнопке → контекстное меню со списком языков
        //   Shift+Alt     → переключить на следующий (системный хоткей работает как обычно)

        void StartLangTracker()
        {
            UpdateLangLabel();
            _langTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _langTimer.Tick += (s, e) => UpdateLangLabel();
            _langTimer.Start();
        }

        void UpdateLangLabel()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow(); if (hwnd == IntPtr.Zero) return;
                uint tid = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                IntPtr hkl = GetKeyboardLayout(tid); if (hkl == IntPtr.Zero) return;
                int langId = (int)(long)hkl & 0xFFFF; if (langId == 0) return;
                string lang = new System.Globalization.CultureInfo(langId).TwoLetterISOLanguageName?.ToUpperInvariant() ?? "";
                if (lang != _lastLang && LangLabel != null) { _lastLang = lang; LangLabel.Text = lang; }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UpdateLangLabel: {ex.Message}"); }
        }

        /// <summary>Возвращает список всех установленных HKL в порядке системного списка.</summary>
        IntPtr[] GetInstalledLayouts()
        {
            int count = GetKeyboardLayoutList(0, null);
            if (count <= 0) return Array.Empty<IntPtr>();
            var list = new IntPtr[count];
            GetKeyboardLayoutList(count, list);
            return list;
        }

        /// <summary>Переключает раскладку активного окна на следующую из установленных (по кругу).</summary>
        void SwitchToNextLayout()
        {
            try
            {
                IntPtr[] layouts = GetInstalledLayouts();
                if (layouts.Length < 2) return;

                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;
                uint tid = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                IntPtr current = GetKeyboardLayout(tid);

                int idx = Array.IndexOf(layouts, current);
                IntPtr next = layouts[(idx + 1) % layouts.Length];

                // Посылаем WM_INPUTLANGCHANGEREQUEST в активное окно
                const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
                PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, next);
                // И активируем глобально
                ActivateKeyboardLayout(next, 0);

                // Обновляем метку немедленно
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        int langId = (int)(long)next & 0xFFFF;
                        string lang = new System.Globalization.CultureInfo(langId).TwoLetterISOLanguageName?.ToUpperInvariant() ?? "";
                        if (LangLabel != null) { _lastLang = lang; LangLabel.Text = lang; }
                    }
                    catch { }
                }));
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] SwitchToNextLayout: {ex.Message}"); }
        }

        // ЛКМ — переключить на следующий язык
        void LangButton_Click(object sender, RoutedEventArgs e)
        {
            SwitchToNextLayout();
        }

        // ПКМ — показать меню с выбором языка
        void LangButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                e.Handled = true;
                IntPtr[] layouts = GetInstalledLayouts();
                if (layouts.Length == 0) return;

                // Текущий язык активного окна
                IntPtr fgHwnd = GetForegroundWindow();
                uint fgTid = fgHwnd != IntPtr.Zero ? GetWindowThreadProcessId(fgHwnd, IntPtr.Zero) : 0;
                IntPtr currentHkl = fgTid != 0 ? GetKeyboardLayout(fgTid) : IntPtr.Zero;

                // Берём стили прямо из ресурсов окна
                var menuStyle = (Style)FindResource("DarkContextMenu");
                var itemStyle = (Style)FindResource("DarkMenuItem");

                var menu = new ContextMenu { Style = menuStyle };

                foreach (IntPtr hkl in layouts)
                {
                    IntPtr capturedHkl = hkl;
                    int langId = (int)(long)hkl & 0xFFFF;
                    string twoLetter;
                    string displayName;
                    try
                    {
                        var ci = new System.Globalization.CultureInfo(langId);
                        twoLetter = ci.TwoLetterISOLanguageName?.ToUpperInvariant() ?? "??";
                        // Только нативное имя без региона в скобках
                        displayName = ci.NativeName;
                        int paren = displayName.IndexOf('(');
                        if (paren > 0) displayName = displayName.Substring(0, paren).Trim();
                    }
                    catch { twoLetter = "??"; displayName = langId.ToString("X4"); }

                    bool isCurrent = (capturedHkl == currentHkl);

                    // Заголовок: "EN  English"
                    var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
                    headerPanel.Children.Add(new TextBlock
                    {
                        Text = twoLetter,
                        FontWeight = FontWeights.SemiBold,
                        MinWidth = 24,
                        Margin = new Thickness(0, 0, 8, 0),
                        Foreground = isCurrent ? new SolidColorBrush(Color.FromRgb(100, 180, 255)) : new SolidColorBrush(Color.FromRgb(224, 224, 224)),
                    });
                    headerPanel.Children.Add(new TextBlock
                    {
                        Text = displayName,
                        Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                        FontWeight = FontWeights.Normal,
                    });

                    var item = new MenuItem
                    {
                        Header = headerPanel,
                        Style = itemStyle,
                        FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                        Tag = capturedHkl,
                    };

                    item.Click += (s2, e2) =>
                    {
                        try
                        {
                            IntPtr fg = GetForegroundWindow();
                            if (fg != IntPtr.Zero)
                            {
                                const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
                                PostMessage(fg, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, capturedHkl);
                            }
                            ActivateKeyboardLayout(capturedHkl, 0);
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    int lid = (int)(long)capturedHkl & 0xFFFF;
                                    string l = new System.Globalization.CultureInfo(lid).TwoLetterISOLanguageName?.ToUpperInvariant() ?? "";
                                    if (LangLabel != null) { _lastLang = l; LangLabel.Text = l; }
                                }
                                catch { }
                            }));
                        }
                        catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LangMenu select: {ex.Message}"); }
                    };
                    menu.Items.Add(item);
                }

                menu.PlacementTarget = LangButton;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                menu.IsOpen = true;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LangButton_RightClick: {ex.Message}"); }
        }

        // [FIX-SECONDARY-POS] Показать меню выбора языка привязанное к кнопке второй панели.
        // placementTarget — кнопка LangButton на SecondaryTaskbarWindow.
        public void OpenLangMenuFromSecondary(System.Windows.Controls.Button placementTarget)
        {
            try
            {
                IntPtr[] layouts = GetInstalledLayouts();
                if (layouts.Length == 0) return;

                IntPtr fgHwnd = GetForegroundWindow();
                uint fgTid = fgHwnd != IntPtr.Zero ? GetWindowThreadProcessId(fgHwnd, IntPtr.Zero) : 0;
                IntPtr currentHkl = fgTid != 0 ? GetKeyboardLayout(fgTid) : IntPtr.Zero;

                var menuStyle = (Style)FindResource("DarkContextMenu");
                var itemStyle = (Style)FindResource("DarkMenuItem");
                var menu = new ContextMenu { Style = menuStyle };

                foreach (IntPtr hkl in layouts)
                {
                    IntPtr capturedHkl = hkl;
                    int langId = (int)(long)hkl & 0xFFFF;
                    string twoLetter; string displayName;
                    try
                    {
                        var ci = new System.Globalization.CultureInfo(langId);
                        twoLetter = ci.TwoLetterISOLanguageName?.ToUpperInvariant() ?? "??";
                        displayName = ci.NativeName;
                        int paren = displayName.IndexOf('(');
                        if (paren > 0) displayName = displayName.Substring(0, paren).Trim();
                    }
                    catch { twoLetter = "??"; displayName = langId.ToString("X4"); }

                    bool isCurrent = (capturedHkl == currentHkl);
                    var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
                    headerPanel.Children.Add(new TextBlock
                    {
                        Text = twoLetter, FontWeight = FontWeights.SemiBold, MinWidth = 24,
                        Margin = new Thickness(0, 0, 8, 0),
                        Foreground = isCurrent
                            ? new SolidColorBrush(Color.FromRgb(100, 180, 255))
                            : new SolidColorBrush(Color.FromRgb(224, 224, 224)),
                    });
                    headerPanel.Children.Add(new TextBlock
                    {
                        Text = displayName,
                        Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                        FontWeight = FontWeights.Normal,
                    });

                    var item = new MenuItem
                    {
                        Header = headerPanel, Style = itemStyle,
                        FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                        Tag = capturedHkl,
                    };
                    item.Click += (s2, e2) =>
                    {
                        try
                        {
                            IntPtr fg = GetForegroundWindow();
                            if (fg != IntPtr.Zero)
                            {
                                const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
                                PostMessage(fg, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, capturedHkl);
                            }
                            ActivateKeyboardLayout(capturedHkl, 0);
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    int lid = (int)(long)capturedHkl & 0xFFFF;
                                    string l = new System.Globalization.CultureInfo(lid).TwoLetterISOLanguageName?.ToUpperInvariant() ?? "";
                                    if (LangLabel != null) { _lastLang = l; LangLabel.Text = l; }
                                }
                                catch { }
                            }));
                        }
                        catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LangMenu select: {ex.Message}"); }
                    };
                    menu.Items.Add(item);
                }

                // Привязываем к кнопке второй панели — меню появится над/под ней
                menu.PlacementTarget = placementTarget;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                // [FIX-LANG-HIDE] Пока контекстное меню языка открыто — панель не должна скрываться.
                // ВАЖНО: флаг выставляем ДО menu.IsOpen = true, иначе MouseLeave срабатывает
                // раньше события Opened и ScheduleHide() успевает запустить таймер скрытия.
                _langContextMenuOpen = true;
                menu.Closed += (_, __) => _langContextMenuOpen = false;
                menu.IsOpen = true;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] OpenLangMenuFromSecondary: {ex.Message}"); }
        }

    }
}
