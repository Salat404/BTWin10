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
        static readonly string _settingsPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MyTaskbar", "settings.xml");

        void SavePosition()
        {
            try
            {
                string dir = IOPath.GetDirectoryName(_settingsPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string posLine = _isBottom ? "bottom" : "top";
                string scaleLine = _uiScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
                File.WriteAllText(_settingsPath, posLine + "\n" + scaleLine);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] SavePosition: {ex.Message}"); }
        }

        void LoadPosition()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return;
                string[] lines = File.ReadAllText(_settingsPath).Trim().Split('\n');
                if (lines.Length > 0 && lines[0].Trim() == "bottom") _isBottom = true;
                if (lines.Length > 1)
                {
                    if (double.TryParse(lines[1].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double s))
                    {
                        _uiScale = Math.Max(1.0, Math.Min(1.5, Math.Round(s, 2)));
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LoadPosition: {ex.Message}"); }
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
                return;
            }

            _settingsWindow = new SettingsWindow(_isBottom, _uiScale, _fullscreenAutoHide);

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

            // Position the window near the taskbar edge, horizontally centered
            _settingsWindow.Loaded += (s, e) =>
            {
                double sw = SystemParameters.PrimaryScreenWidth;
                double ww = _settingsWindow.ActualWidth;
                double wh = _settingsWindow.ActualHeight;
                _settingsWindow.Left = (sw - ww) / 2;
                if (_isBottom)
                    _settingsWindow.Top = SystemParameters.PrimaryScreenHeight - TASKBAR_HEIGHT - wh - 8;
                else
                    _settingsWindow.Top = TASKBAR_HEIGHT + 8;
            };

            _settingsWindow.Show();
        }

        void SaveFullscreenAutoHide()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(_settingsPath);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                // Append/overwrite a separate file next to the main settings file
                string path = System.IO.Path.Combine(dir, "fullscreen_hide.txt");
                System.IO.File.WriteAllText(path, _fullscreenAutoHide ? "1" : "0");
            }
            catch { }
        }

        void LoadFullscreenAutoHide()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(_settingsPath);
                string path = System.IO.Path.Combine(dir, "fullscreen_hide.txt");
                if (System.IO.File.Exists(path))
                {
                    string v = System.IO.File.ReadAllText(path).Trim();
                    _fullscreenAutoHide = (v != "0");
                }
            }
            catch { }
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
        }

        void SaveUIScale()
        {
            try
            {
                string dir = IOPath.GetDirectoryName(_settingsPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string posLine = _isBottom ? "bottom" : "top";
                string scaleLine = _uiScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
                File.WriteAllText(_settingsPath, posLine + "\n" + scaleLine);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] SaveUIScale: {ex.Message}"); }
        }

        void ReserveScreenSpace()
        {
            try
            {
                // [FIX-WORKAREA-DPI] SPI_SETWORKAREA требует физические пиксели, а не WPF DIP.
                // SystemParameters.PrimaryScreenWidth/Height — в DIP, нужно умножить на DPI.
                // TASKBAR_HEIGHT тоже в DIP — тоже умножаем.
                // Иначе при DPI > 96 (125%, 150%) right/bottom оказываются меньше экрана
                // и maximized-окна (Chrome, Edge) получают зазор справа и сверху.
                var source = PresentationSource.FromVisual(this);
                double dpi = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                int swPx = (int)Math.Round(SystemParameters.PrimaryScreenWidth * dpi);
                int shPx = (int)Math.Round(SystemParameters.PrimaryScreenHeight * dpi);
                int thPx = (int)Math.Round(TASKBAR_HEIGHT * dpi);
                RECT wa = _isBottom
                    ? new RECT { left = 0, top = 0, right = swPx, bottom = shPx - thPx }
                    : new RECT { left = 0, top = thPx, right = swPx, bottom = shPx };
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

            // [FIX-TASKMGR-FOCUS] Хук фокуса — теперь в хелпере
            TaskmgrWatcher.StopFocusGuard();

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
