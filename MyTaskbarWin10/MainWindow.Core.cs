using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
// MainWindow.Core.cs — Constructor, Loaded, Closed, SafeRun utilities
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        public MainWindow()
        {
            InitializeComponent();
            ShortcutsFolder = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Shortcuts");
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;

            // [FIX-7] Watchdog
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { TaskbarHelper.Show(); } catch { }
                try
                {
                    double sw = SystemParameters.PrimaryScreenWidth;
                    double sh = SystemParameters.PrimaryScreenHeight;
                    RECT wa = new RECT { left = 0, top = 0, right = (int)sw, bottom = (int)sh };
                    SystemParametersInfo(SPI_SETWORKAREA, 0, ref wa, 0x01 | 0x02);
                }
                catch { }
            };
        }

        void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Shortcuts folder creation removed — folder is not used by the app

            // [FAST-START] Скрываем системный таскбар ПЕРВЫМ — до загрузки иконок
            SafeRun(() => TaskbarHelper.Hide(), "TaskbarHelper.Hide");

            SafeRun(LoadPinnedApps, "LoadPinnedApps");
            SafeRun(LoadShortcuts, "LoadShortcuts");
            SafeRun(LoadPosition, "LoadPosition");   // загружаем позицию до PositionTaskbar
            SafeRun(LoadFullscreenAutoHide, "LoadFullscreenAutoHide");
            SafeRun(LoadRevealDelay, "LoadRevealDelay");
            SafeRun(LoadPreviewDelay, "LoadPreviewDelay");
            SafeRun(LoadHideDelay, "LoadHideDelay");
            SafeRun(LoadAttentionShow, "LoadAttentionShow");
            // [DPI-AUTO] Читаем начальный масштаб до первого PositionTaskbar
            try { var src0 = PresentationSource.FromVisual(this); _currentDpiScale = src0?.CompositionTarget?.TransformToDevice.M11 ?? 1.0; } catch { }
            // [UI-SCALE] Применяем сохранённый масштаб до первого PositionTaskbar
            if (Math.Abs(_uiScale - 1.0) > 0.01) MainPanel.LayoutTransform = new ScaleTransform(_uiScale, _uiScale);
            SafeRun(PositionTaskbar, "PositionTaskbar");
            SafeRun(ApplyAcrylicBackground, "ApplyAcrylicBackground");
            SafeRun(ReserveScreenSpace, "ReserveScreenSpace");

            try { EnsureMenuWindow(); }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] MenuWindow: {ex.Message}"); }

            SafeRun(InitPreviewWindow, "InitPreviewWindow");
            SafeRun(StartClock, "StartClock");
            SafeRun(StartFlashTimer, "StartFlashTimer");
            SafeRun(StartActiveWindowTracker, "StartActiveWindowTracker");
            SafeRun(StartLangTracker, "StartLangTracker");
            SafeRun(StartBatteryTracker, "StartBatteryTracker");
            SafeRun(StartWifiIconTracker, "StartWifiIconTracker");
            SafeRun(StartVolumeIconTracker, "StartVolumeIconTracker");
            SafeRun(StartBrightnessIconTracker, "StartBrightnessIconTracker");
            SafeRun(StartBluetoothTracker, "StartBluetoothTracker");
            SafeRun(StartTaskbarWatcher, "StartTaskbarWatcher");
            // [REMOVED] TaskmgrWatcher.StartFocusGuard - focus lock disabled
            SafeRun(InstallKeyboardHook, "InstallKeyboardHook");
            SafeRun(StartFullscreenWatcher, "StartFullscreenWatcher");
            SafeRun(StartEdgeRevealWatcher, "StartEdgeRevealWatcher");
            SafeRun(HookPowerEvents, "HookPowerEvents");
            SafeRun(RegisterShellAttentionHook, "RegisterShellAttentionHook"); // [ATTENTION]
            SafeRun(ApplyLayeredStyle, "ApplyLayeredStyle"); // [FIX-DRAG]
            SafeRun(RegisterPassthroughDrop, "RegisterPassthroughDrop"); // [FIX-DRAG2]
            SafeRun(InitNotifyIcon, "InitNotifyIcon"); // Иконка в системном трее
            // [DRAG-REORDER] Обработчики drag-reorder на уровне панели
            AppIcons.PreviewMouseLeftButtonDown += AppIcons_DragMouseDown;
            AppIcons.PreviewMouseMove           += AppIcons_DragMouseMove;
            AppIcons.PreviewMouseLeftButtonUp   += AppIcons_DragMouseUp;
            AppIcons.MouseLeave                 += AppIcons_DragMouseLeave;

            // [FULLSCREEN-AUTOHIDE] Трекаем вход/выход курсора с панели
            // для авто-скрытия в fullscreen-режиме (Win10-поведение)
            this.MouseEnter += (_, _me) => SafeRun(OnTaskbarMouseEnterFullscreen, "OnTaskbarMouseEnterFullscreen");
            this.MouseLeave += (_, _me) => SafeRun(OnTaskbarMouseLeaveFullscreen, "OnTaskbarMouseLeaveFullscreen");

            // [FIX-2.2] Кэшируем HWND собственного окна один раз — используется в CheckFullscreen
            try { _myHwnd = new WindowInteropHelper(this).Handle; } catch { }
        }

        // [FIX-2.11] CallerMemberName — автоматически подставляет имя метода-вызывателя
        // Если передать явное имя — использует его; если не передать — подставляет имя caller'а
        static void SafeRun(Action a, [CallerMemberName] string n = "")
        { try { a(); } catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] {n}: {ex}"); } }

        // ═════════════════════════════════════════════════════════════════════
        // [STAB-1] SafeSendMessage — не вешает UI на зависших окнах
        // ═════════════════════════════════════════════════════════════════════
        static IntPtr SafeSendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint timeoutMs = 200)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return IntPtr.Zero;
            try { if (IsHungAppWindow(hWnd)) return IntPtr.Zero; } catch { return IntPtr.Zero; }
            try
            {
                UIntPtr result;
                IntPtr ret = SendMessageTimeout(hWnd, msg, wParam, lParam,
                    SMTO_ABORTIFHUNG | SMTO_BLOCK, timeoutMs, out result);
                return ret == IntPtr.Zero ? IntPtr.Zero : (IntPtr)(long)result.ToUInt64();
            }
            catch { return IntPtr.Zero; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // [STAB-4] SafeGetWindowText — через SendMessageTimeout
        // ═════════════════════════════════════════════════════════════════════
        static string SafeGetWindowText(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return "";
            try { if (IsHungAppWindow(hWnd)) return ""; } catch { return ""; }
            try
            {
                // GetWindowText читает из внутреннего буфера напрямую — безопасен
                int len = GetWindowTextLength(hWnd);
                if (len <= 0 || len > 2048) return "";
                var sb = new StringBuilder(len + 2);
                int actual = GetWindowText(hWnd, sb, len + 1);
                return actual > 0 ? sb.ToString() : "";
            }
            catch { return ""; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // [STAB-8] SafeGetProcessName — полная обработка исключений
        // [FIX-TASKMGR] Fallback через PROCESS_QUERY_LIMITED_INFORMATION
        //   для привилегированных процессов (taskmgr, regedit и др.) —
        //   Process.GetProcessById бросает Win32Exception (Access Denied),
        //   поэтому при ошибке используем TaskmgrWatcher.GetProcessNameByPid,
        //   который открывает процесс с минимальным флагом и не требует прав.
        // ═════════════════════════════════════════════════════════════════════
        string SafeGetProcessName(uint pid)
        {
            try
            {
                var proc = Process.GetProcessById((int)pid);
                if (proc.HasExited) return "";
                string name = proc.ProcessName?.ToLowerInvariant() ?? "";
                if (string.IsNullOrEmpty(name)) return "";
                try
                {
                    string path = proc.MainModule?.FileName ?? "";
                    if (!string.IsNullOrEmpty(path) && _pidPathCache.Count < 256)
                        _pidPathCache.TryAdd(pid, path);
                }
                catch { _pidPathCache.TryAdd(pid, ""); }
                return name;
            }
            catch (ArgumentException) { return ""; }
            catch (InvalidOperationException) { return ""; }
            catch (System.ComponentModel.Win32Exception)
            {
                // [FIX-TASKMGR] Process.GetProcessById не даёт доступ к
                // привилегированным процессам (High/System IL: taskmgr, regedit, tf_win64…).
                // Используем QueryFullProcessImageName с PROCESS_QUERY_LIMITED_INFORMATION —
                // этот флаг разрешён без прав администратора для любого процесса.
                //
                // [FIX-ELEVATED-ICON] Дополнительно сохраняем ПОЛНЫЙ ПУТЬ в _pidPathCache.
                // Без этого у elevated-игр (TF2, CS2 и т.п.) ep всегда пустой →
                // GetBestIcon не может найти exe → иконка не отображается.
                string fullPath = TaskmgrWatcher.GetProcessFullPathByPid(pid);
                if (!string.IsNullOrEmpty(fullPath))
                {
                    if (_pidPathCache.Count < 256)
                        _pidPathCache.TryAdd(pid, fullPath);
                    string nameFromPath = System.IO.Path.GetFileNameWithoutExtension(fullPath)?.ToLowerInvariant() ?? "";
                    if (!string.IsNullOrEmpty(nameFromPath) && _pidNameCache.Count < 256)
                        _pidNameCache.TryAdd(pid, nameFromPath);
                    return nameFromPath;
                }
                // Крайний fallback: хотя бы имя (без пути — иконка через WM_GETICON)
                string fallback = TaskmgrWatcher.GetProcessNameByPid(pid);
                if (!string.IsNullOrEmpty(fallback) && _pidNameCache.Count < 256)
                    _pidNameCache.TryAdd(pid, fallback);
                return fallback;
            }
            catch { return ""; }
        }

    }
}
