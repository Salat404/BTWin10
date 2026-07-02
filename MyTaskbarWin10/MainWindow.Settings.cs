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
// MainWindow.Settings.cs — Position, Clock, Start button, settings save/load
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // СОХРАНЕНИЕ / ЗАГРУЗКА ПОЗИЦИИ ПАНЕЛИ
        // ═════════════════════════════════════════════════════════════════════
        // [FIX-2.3] Единый JSON-файл настроек вместо 7 отдельных .txt файлов.
        // Атомарная запись через temp + rename предотвращает порчу файла при сбое.
        static readonly string _settingsDir = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyTaskbar");
        static readonly string _settingsPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MyTaskbar", "settings.json");
        // Старый путь — для миграции
        static readonly string _settingsPathLegacy = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MyTaskbar", "settings.xml");

        // ── Единое сохранение всех настроек ───────────────────────────────────
        void SaveAllSettings()
        {
            try
            {
                if (!Directory.Exists(_settingsDir)) Directory.CreateDirectory(_settingsDir);
                string ic = System.Globalization.CultureInfo.InvariantCulture.NumberFormat.NumberDecimalSeparator;
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"position\": \"{(_isBottom ? "bottom" : "top")}\",");
                sb.AppendLine($"  \"uiScale\": {_uiScale.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"fullscreenAutoHide\": {(_fullscreenAutoHide ? "true" : "false")},");
                sb.AppendLine($"  \"revealDelayMs\": {_revealDelayMs},");
                sb.AppendLine($"  \"previewDelayMs\": {_previewDelayMs},");
                sb.AppendLine($"  \"hideDelayMs\": {_hideDelayMs},");
                sb.AppendLine($"  \"attentionShow\": {(_attentionShowEnabled ? "true" : "false")},");
                sb.AppendLine($"  \"menuAnim\": {(_menuAnimEnabled ? "true" : "false")},");
                sb.AppendLine($"  \"batteryIndicator\": {(_batteryIndicatorEnabled ? "true" : "false")},");
                sb.AppendLine($"  \"primaryMonitorDevice\": \"{_primaryMonitorDevice.Replace("\\", "\\\\")}\"");
                sb.AppendLine("}");
                // Атомарная запись: сначала во временный файл, потом rename
                string tmp = _settingsPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), System.Text.Encoding.UTF8);
                File.Replace(tmp, _settingsPath, null);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] SaveAllSettings: {ex.Message}"); }
        }

        // ── Обратная совместимость: делегаты вызывают единый SaveAllSettings ──
        void SavePosition()            => SaveAllSettings();
        void SaveUIScale()             => SaveAllSettings();
        void SaveFullscreenAutoHide()  => SaveAllSettings();
        void SaveRevealDelay()         => SaveAllSettings();
        void SavePreviewDelay()        => SaveAllSettings();
        void SaveHideDelay()           => SaveAllSettings();
        void SaveAttentionShow()       => SaveAllSettings();
        void SavePrimaryMonitor()      => SaveAllSettings();

        // ── Единая загрузка всех настроек ─────────────────────────────────────
        void LoadPosition()
        {
            try
            {
                // Пробуем новый JSON
                if (File.Exists(_settingsPath))
                {
                    LoadAllSettingsFromJson(_settingsPath);
                    return;
                }
                // Миграция: читаем старый settings.xml (на самом деле plain-text)
                if (File.Exists(_settingsPathLegacy))
                {
                    string[] lines = File.ReadAllText(_settingsPathLegacy).Trim().Split('\n');
                    if (lines.Length > 0 && lines[0].Trim() == "bottom") _isBottom = true;
                    if (lines.Length > 1)
                        if (double.TryParse(lines[1].Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double s))
                            _uiScale = Math.Max(1.0, Math.Min(1.5, Math.Round(s, 2)));
                    // Мигрируем остальные .txt-настройки и сразу сохраняем в JSON
                    MigrateLegacySettings();
                    SaveAllSettings();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LoadPosition: {ex.Message}"); }
        }

        // Простой ручной JSON-парсер — не тянет зависимости (Newtonsoft/System.Text.Json)
        void LoadAllSettingsFromJson(string path)
        {
            try
            {
                string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                string Get(string key)
                {
                    // Ищем "key": value — value может быть числом, bool или строкой
                    int i = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
                    if (i < 0) return null;
                    int colon = json.IndexOf(':', i + key.Length + 2);
                    if (colon < 0) return null;
                    int start = colon + 1;
                    while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
                    bool quoted = start < json.Length && json[start] == '"';
                    if (quoted) start++;
                    int end = start;
                    while (end < json.Length && json[end] != ',' && json[end] != '\n' && json[end] != '}'
                           && (!quoted || json[end] != '"'  )) end++;
                    return json.Substring(start, end - start).Trim().Trim('"');
                }
                string pos = Get("position");
                if (pos == "bottom") _isBottom = true;
                if (double.TryParse(Get("uiScale"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double sc))
                    _uiScale = Math.Max(1.0, Math.Min(1.5, Math.Round(sc, 2)));
                string fah = Get("fullscreenAutoHide");
                if (fah != null) _fullscreenAutoHide = (fah != "false" && fah != "0");
                if (int.TryParse(Get("revealDelayMs"), out int rd))
                    _revealDelayMs = Math.Max(0, Math.Min(3000, rd));
                if (int.TryParse(Get("previewDelayMs"), out int pd))
                    _previewDelayMs = Math.Max(0, Math.Min(2000, pd));
                if (int.TryParse(Get("hideDelayMs"), out int hd))
                    _hideDelayMs = Math.Max(100, Math.Min(5000, hd));
                string attn = Get("attentionShow");
                if (attn != null) _attentionShowEnabled = (attn != "false" && attn != "0");
                string mnanim = Get("menuAnim");
                if (mnanim != null) _menuAnimEnabled = (mnanim != "false" && mnanim != "0");
                string batt = Get("batteryIndicator");
                if (batt != null) _batteryIndicatorEnabled = (batt != "false" && batt != "0");
                string pmd = Get("primaryMonitorDevice");
                if (pmd != null) _primaryMonitorDevice = pmd.Replace("\\\\", "\\");
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LoadAllSettingsFromJson: {ex.Message}"); }
        }

        // Миграция старых .txt файлов → поля перед записью в JSON
        void MigrateLegacySettings()
        {
            try
            {
                string TryRead(string fname)
                {
                    string p = IOPath.Combine(_settingsDir, fname);
                    return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
                }
                string v;
                if ((v = TryRead("fullscreen_hide.txt")) != null) _fullscreenAutoHide = (v != "0");
                if ((v = TryRead("reveal_delay.txt")) != null && int.TryParse(v, out int rd))
                    _revealDelayMs = Math.Max(0, Math.Min(3000, rd));
                if ((v = TryRead("preview_delay.txt")) != null && int.TryParse(v, out int pd))
                    _previewDelayMs = Math.Max(0, Math.Min(2000, pd));
                if ((v = TryRead("hide_delay.txt")) != null && int.TryParse(v, out int hd))
                    _hideDelayMs = Math.Max(100, Math.Min(5000, hd));
                if ((v = TryRead("attention_show.txt")) != null) _attentionShowEnabled = (v != "0");
                Debug.WriteLine("[MyTaskbar] Legacy settings migrated to settings.json");
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] MigrateLegacySettings: {ex.Message}"); }
        }
        void InitNotifyIcon()
        {
            try
            {
                // Создаём иконку из стандартного системного ресурса Windows
                // (IDI_APPLICATION = 32512) — не требует GDI/System.Drawing
                IntPtr hIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
                var icon = hIcon != IntPtr.Zero
                    ? System.Drawing.Icon.FromHandle(hIcon)
                    : System.Drawing.SystemIcons.Application;

                _appNotifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = icon,
                    Text = "MyTaskbar — click to move the taskbar",
                    Visible = true
                };
                _appNotifyIcon.Click += (s, e) =>
                    Dispatcher.Invoke(ToggleTaskbarPosition);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"InitNotifyIcon error:\n{ex}",
                    "MyTaskbar", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }

        void CloseAllPopups()
        {
            try { if (_menuWindow != null && _menuWindow.IsVisible) _menuWindow.HideAnimated(); } catch { }
            try { if (_wifiWindow != null && _wifiWindow.IsVisible) _wifiWindow.Hide(); } catch { }
            try { if (_volumeFlyout != null && _volumeFlyout.IsVisible) _volumeFlyout.Hide(); } catch { }
            try { if (_brightnessFlyout != null && _brightnessFlyout.IsVisible) _brightnessFlyout.Hide(); } catch { }
            try { if (_trayWindow != null && _trayWindow.IsOpen) _trayWindow.SlideUp(); } catch { }
            try { HidePreview(); } catch { }
        }

        // ── Settings window ────────────────────────────────────────────────────
        void OpenSettingsWindow()
        {
            if (_settingsWindow != null && _settingsWindow.IsVisible)
            {
                _settingsWindow.Activate();
                _settingsWindow.Focus();
                return;
            }

            _settingsWindow = new SettingsWindow(_isBottom, _uiScale, _fullscreenAutoHide,
                                                 _revealDelayMs, _previewDelayMs, _hideDelayMs,
                                                 _attentionShowEnabled, _primaryMonitorDevice,
                                                 _menuAnimEnabled, _batteryIndicatorEnabled);

            _settingsWindow.PositionChanged += (isBot) =>
            {
                if (isBot != _isBottom)
                    Dispatcher.Invoke(ToggleTaskbarPosition);
            };

            _settingsWindow.ScaleChanged += (newScale) =>
            {
                _uiScale = newScale;
                Dispatcher.Invoke(ApplyUIScale);
                SaveUIScale();
            };

            _settingsWindow.FullscreenChanged += (enabled) =>
            {
                _fullscreenAutoHide = enabled;
                if (!enabled)
                {
                    // show taskbar immediately if it was hidden by fullscreen
                    if (_isHiddenByFullscreen)
                    {
                        _isHiddenByFullscreen = false;
                        _fullscreenConfirmCount = 0;
                        _notFullscreenConfirmCount = 0;
                        ShowTaskbar(animate: true);
                    }
                }
                SaveFullscreenAutoHide();
            };

            _settingsWindow.RevealDelayChanged += (ms) =>
            {
                _revealDelayMs = ms;
                SaveRevealDelay();
            };

            _settingsWindow.PreviewDelayChanged += (ms) =>
            {
                _previewDelayMs = ms;
                if (_previewShowTimer != null)
                    _previewShowTimer.Interval = TimeSpan.FromMilliseconds(ms);
                SavePreviewDelay();
            };

            _settingsWindow.HideDelayChanged += (ms) =>
            {
                _hideDelayMs = ms;
                SaveHideDelay();
            };

            _settingsWindow.AttentionShowChanged += (enabled) =>
            {
                _attentionShowEnabled = enabled;
                SaveAttentionShow();
            };

            _settingsWindow.MenuAnimChanged += (enabled) =>
            {
                _menuAnimEnabled = enabled;
                if (_menuWindow != null) _menuWindow.AnimEnabled = enabled;
                SaveAllSettings();
            };

            _settingsWindow.BatteryIndicatorChanged += (enabled) =>
            {
                _batteryIndicatorEnabled = enabled;
                if (BatteryButton != null)
                {
                    BatteryButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
                }
                SaveAllSettings();
            };

            _settingsWindow.PrimaryMonitorChanged += (deviceName) =>
            {
                _primaryMonitorDevice = deviceName;
                SavePrimaryMonitor();
                // Перепозиционируем главную панель на выбранный монитор
                Dispatcher.Invoke(() =>
                {
                    PositionTaskbar();
                    ReserveScreenSpace();
                });
                // Уведомляем вторичную панель — она пересчитает свои мониторы
                SafeRun(NotifySecondaryMonitorChanged, "NotifySecondaryMonitorChanged");
            };

            // Position the window near the taskbar edge, horizontally centered on selected monitor
            _settingsWindow.Loaded += (s, e) =>
            {
                var (monLeft, monTop, monWidth, monHeight) = GetPrimaryMonitorRectDip();
                double ww = _settingsWindow.ActualWidth;
                double wh = _settingsWindow.ActualHeight;
                _settingsWindow.Left = monLeft + (monWidth - ww) / 2;
                if (_isBottom)
                    _settingsWindow.Top = monTop + monHeight - TASKBAR_HEIGHT - wh - 8;
                else
                    _settingsWindow.Top = monTop + TASKBAR_HEIGHT + 8;
            };

            _settingsWindow.Show();
        }

        // [FIX-2.3] Разрозненные Save*/Load* методы заменены единым SaveAllSettings/LoadAllSettingsFromJson выше
        // Stub'ы для обратной совместимости с вызовами из MainWindow_Loaded:
        void LoadFullscreenAutoHide() { /* [FIX-2.3] Загружается в LoadPosition → LoadAllSettingsFromJson */ }
        void LoadRevealDelay()        { /* [FIX-2.3] Загружается в LoadPosition → LoadAllSettingsFromJson */ }
        void LoadPreviewDelay()       { /* [FIX-2.3] Загружается в LoadPosition → LoadAllSettingsFromJson */ }
        void LoadHideDelay()          { /* [FIX-2.3] Загружается в LoadPosition → LoadAllSettingsFromJson */ }
        void LoadAttentionShow()      { /* [FIX-2.3] Загружается в LoadPosition → LoadAllSettingsFromJson */ }

        // ── Получить геометрию главного монитора (учитывает _primaryMonitorDevice) ──
        // Возвращает координаты в физических пикселях.
        // Если монитор не найден — возвращает системный primary.
        (double left, double top, double width, double height) GetPrimaryMonitorRectDip()
        {
            try
            {
                var source = PresentationSource.FromVisual(this);
                double dpi = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

                var monitors = EnumerateMonitors();
                RECT rc;

                if (!string.IsNullOrEmpty(_primaryMonitorDevice))
                {
                    // Ищем монитор по имени устройства
                    foreach (var m in monitors)
                    {
                        if (string.Equals(m.name, _primaryMonitorDevice, StringComparison.OrdinalIgnoreCase))
                        {
                            rc = m.rc;
                            return (rc.left / dpi, rc.top / dpi,
                                    (rc.right - rc.left) / dpi,
                                    (rc.bottom - rc.top) / dpi);
                        }
                    }
                }

                // Fallback: системный primary (содержит флаг MONITORINFOF_PRIMARY)
                foreach (var m in monitors)
                {
                    if (m.isPrimary)
                    {
                        rc = m.rc;
                        return (rc.left / dpi, rc.top / dpi,
                                (rc.right - rc.left) / dpi,
                                (rc.bottom - rc.top) / dpi);
                    }
                }

                // Крайний fallback — SystemParameters
                return (0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            }
            catch
            {
                return (0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            }
        }

        void ToggleTaskbarPosition()

        {
            _isBottom = !_isBottom;

            // Закрываем все открытые попапы — они привязаны к старой позиции
            CloseAllPopups();

            // Убираем трансформацию — содержимое панели не зеркалим
            // [UI-SCALE] Сохраняем масштаб панели после смены позиции
            MainPanel.LayoutTransform = Math.Abs(_uiScale - 1.0) < 0.01
                ? Transform.Identity
                : new ScaleTransform(_uiScale, _uiScale);

            // Обновляем позицию окна и рабочую область
            PositionTaskbar();
            ReserveScreenSpace();

            // Сохраняем позицию для следующего запуска
            SavePosition();

            // Обновляем tooltip иконки трея
            if (_appNotifyIcon != null)
                _appNotifyIcon.Text = _isBottom
                    ? "MyTaskbar — taskbar at bottom  (click → move to top)"
                    : "MyTaskbar — taskbar at top (click → move to bottom)";

            // [SECONDARY] Синхронизируем позицию второй панели
            SafeRun(NotifySecondaryPositionChanged, "NotifySecondaryPositionChanged");
        }

        // [UI-SCALE] Изменить масштаб панели и всех всплывающих окон.
        void ChangeUIScale(double delta)
        {
            double newScale = Math.Round(_uiScale + delta, 2);
            newScale = Math.Round(Math.Max(1.0, Math.Min(1.5, newScale)), 2);
            if (Math.Abs(newScale - _uiScale) < 0.01) return;

            _uiScale = newScale;
            ApplyUIScale();
            SaveUIScale();
        }

        void ApplyUIScale()
        {
            // Главная панель
            MainPanel.LayoutTransform = new ScaleTransform(_uiScale, _uiScale);
            PositionTaskbar();
            ReserveScreenSpace();

            // Передаём масштаб открытым попап-окнам
            // [UI-SCALE-FIX] Для MenuWindow: сначала передаём масштаб (LayoutTransform),
            // потом вызываем RepositionAfterScale() — пересчёт VisibleTop с новым ScaledTaskbarH.
            if (_menuWindow != null)
            {
                _menuWindow.UIScale = _uiScale;
                _menuWindow.RepositionAfterScale();
            }
            if (_trayWindow != null) _trayWindow.UIScale = _uiScale;
            if (_volumeFlyout != null) _volumeFlyout.UIScale = _uiScale;
            if (_wifiWindow != null) _wifiWindow.UIScale = _uiScale;
            if (_brightnessFlyout != null) _brightnessFlyout.UIScale = _uiScale;

            // [SECONDARY] Синхронизируем масштаб второй панели
            SafeRun(NotifySecondaryScaleChanged, "NotifySecondaryScaleChanged");
        }

        // SaveUIScale() — делегат → SaveAllSettings() (см. выше)

        void ReserveScreenSpace()
        {
            try
            {
                // [PRIMARY-MONITOR] Используем выбранный монитор
                var source = PresentationSource.FromVisual(this);
                double dpi = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

                var (monLeft, monTop, monWidth, monHeight) = GetPrimaryMonitorRectDip();
                int mlPx = (int)Math.Round(monLeft   * dpi);
                int mtPx = (int)Math.Round(monTop    * dpi);
                int mrPx = (int)Math.Round((monLeft + monWidth)  * dpi);
                int mbPx = (int)Math.Round((monTop  + monHeight) * dpi);
                int thPx = (int)Math.Round(TASKBAR_HEIGHT * dpi);

                RECT wa = _isBottom
                    ? new RECT { left = mlPx, top = mtPx,  right = mrPx, bottom = mbPx - thPx }
                    : new RECT { left = mlPx, top = mtPx + thPx, right = mrPx, bottom = mbPx };
                SystemParametersInfo(SPI_SETWORKAREA, 0, ref wa, 0x01 | 0x02);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] ReserveScreenSpace: {ex.Message}"); }
        }

        void StartClock()
        {
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) => UpdateClock();
            _clockTimer.Start(); UpdateClock();
        }

        void UpdateClock()
        {
            try
            {
                var n = DateTime.Now;
                if (TimeLabel != null) TimeLabel.Text = n.ToString("HH:mm:ss");
                if (DateLabel != null) DateLabel.Text = n.ToString("dd.MM.yyyy");
            }
            catch { }
        }

        // [WINX] ПКМ на кнопке Пуск — WinX-меню
        void StartButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                MarkOwnActivity();
                var menu = new ContextMenu { Style = TryFindResource("DarkContextMenu") as Style };

                void AddItem(string label, Action action, bool bold = false)
                {
                    var item = MakeMenuItem(label, null, bold);
                    item.Click += (s2, e2) => { try { action(); } catch { } };
                    menu.Items.Add(item);
                }
                void AddSep() { menu.Items.Add(new Separator()); }
                void Run(string file, string args = "", bool admin = false)
                {
                    var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
                    if (admin) psi.Verb = "runas";
                    Process.Start(psi);
                }

                AddItem("Диспетчер задач", () => Run("taskmgr.exe"));
                AddSep();
                AddItem("Диспетчер устройств", () => Run("devmgmt.msc"));
                AddItem("Управление дисками", () => Run("diskmgmt.msc"));
                AddItem("Управление компьютером", () => Run("compmgmt.msc"));
                AddSep();
                AddItem("PowerShell", () => Run("powershell.exe"));
                AddItem("PowerShell (администратор)", () => Run("powershell.exe", "", true));
                AddItem("Командная строка", () => Run("cmd.exe"));
                AddItem("Командная строка (администратор)", () => Run("cmd.exe", "", true));
                AddSep();
                AddItem("Система", () => Run("ms-settings:about", "", false));
                AddItem("Параметры", () => Run("ms-settings:"));
                AddItem("Проводник", () => Run("explorer.exe"));

                menu.PlacementTarget = StartButton;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                menu.IsOpen = true;
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] StartButton_RightClick: {ex.Message}"); }
        }

        void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MarkOwnActivity(); EnsureMenuWindow();
                if (_menuWindow.IsVisible) { _menuWindow.HideAnimated(); return; }
                if (_menuWindow.JustHidden) return;
                // [GAME-10] Если игра fullscreen — меню должно быть Topmost
                bool fps = IsCursorCapturedByFpsGame();
                _menuWindow.IsFullscreenMode = IsForegroundFullscreen() || fps;
                // [GAME-11] Восстанавливаем блокер если FPS захватил курсор
                if (fps) ShowFullscreenBlocker();
                _menuWindow.IsBottom = _isBottom;
                // [SECONDARY-MENU] Сбрасываем SourceMonitorRect ТОЛЬКО если Secondary его не выставил.
                // Если Secondary уже установил rect — значит клик пришёл оттуда, не трогаем.
                if (!_menuWindow.SourceMonitorRect.HasValue)
                {
                    // Клик с основного монитора: rect уже null, ничего не делаем
                }
                // rect будет сброшен в null самим MenuWindow после закрытия (см. ниже)
                Debug.WriteLine("[MainWindow.StartButton_Click] SourceMonitorRect="
                    + (_menuWindow.SourceMonitorRect.HasValue ? _menuWindow.SourceMonitorRect.Value.ToString() : "null")
                    + " IsBottom=" + _menuWindow.IsBottom);
                _menuWindow.ShowMenu();
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] StartButton_Click: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // CLOSED [STAB-15]
        // ═════════════════════════════════════════════════════════════════════
        void MainWindow_Closed(object sender, EventArgs e)
        {
            // Хук клавиатуры — первым
            _hookWatchdog?.Stop(); // [FIX-HOOK-WATCHDOG] останавливаем watchdog перед unhook
            if (_keyboardHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_keyboardHook); } catch { }
                _keyboardHook = IntPtr.Zero;
            }

            // [EXP-RESTART] WinEvent хук
            if (_winEventHook != IntPtr.Zero)
            {
                try { UnhookWinEvent(_winEventHook); } catch { }
                _winEventHook = IntPtr.Zero;
            }

            // [ATTENTION] Shell hook
            try { DeregisterShellHookWindow(new WindowInteropHelper(this).Handle); } catch { }

            // [REMOVED] TaskmgrWatcher.StopFocusGuard - focus lock disabled

            // [STAB-5] Отписка WTS
            try { WTSUnRegisterSessionNotification(new WindowInteropHelper(this).Handle); } catch { }

            // [STAB-15] Каждый таймер в отдельном try/catch
            void St(DispatcherTimer t, string n)
            { try { t?.Stop(); } catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] StopTimer {n}: {ex.Message}"); } }

            St(_taskbarWatcher, "taskbarWatcher");
            St(_activeTimer, "activeTimer");
            St(_clockTimer, "clockTimer");
            St(_langTimer, "langTimer");
            St(_batteryTimer, "batteryTimer");
            St(_wifiIconTimer, "wifiIconTimer");
            St(_volumeIconTimer, "volumeIconTimer");
            St(_brightnessIconTimer, "brightnessIconTimer");
            St(_bluetoothTimer, "bluetoothTimer");
            St(_edgeRevealTimer, "edgeRevealTimer");
            St(_flashTimer, "flashTimer");
            St(_previewHideTimer, "previewHideTimer");
            St(_fullscreenTimer, "fullscreenTimer");
            St(_previewShowTimer, "previewShowTimer");
            St(_resumeBlockTimer, "resumeBlockTimer");

            SafeRun(UnregisterAllThumbnails, "UnregisterAllThumbnails");

            try { _wifiWindow?.Close(); } catch { }
            try { _volumeFlyout?.Close(); } catch { }
            try { _brightnessFlyout?.Close(); } catch { }
            try { _previewWindow?.Close(); } catch { }
            try { _menuWindow?.Close(); } catch { }
            try { _trayWindow?.Close(); } catch { }
            try { _blockerWindow?.Close(); } catch { }

            // Убираем иконку из системного трея
            try { if (_appNotifyIcon != null) { _appNotifyIcon.Visible = false; _appNotifyIcon.Dispose(); _appNotifyIcon = null; } } catch { }

            // Восстановление системного таскбара
            SafeRun(() => TaskbarHelper.Show(), "TaskbarHelper.Show");

            // [FIX-DRAG2] Снимаем регистрацию OLE drop
            try { RevokeDragDrop(new WindowInteropHelper(this).Handle); } catch { }

            // Восстановление рабочей области
            try
            {
                double sw = SystemParameters.PrimaryScreenWidth;
                double sh = SystemParameters.PrimaryScreenHeight;
                RECT wa = new RECT { left = 0, top = 0, right = (int)sw, bottom = (int)sh };
                SystemParametersInfo(SPI_SETWORKAREA, 0, ref wa, 0x01 | 0x02);
            }
            catch { }
        }
    }
}
