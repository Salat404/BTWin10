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
// MainWindow.TaskbarWatch.cs — Taskbar watcher + Explorer shell restore
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // TASKBAR WATCHER
        // ═════════════════════════════════════════════════════════════════════
        // [EXP-RESTART] Двухуровневая защита:
        //   1. SetWinEventHook — мгновенная реакция (< 50 мс) при пересоздании
        //      Shell_TrayWnd после перезапуска Explorer (Диспетчер устройств и т.п.)
        //   2. Backup DispatcherTimer 500 мс — на случай, если хук пропустит событие
        void StartTaskbarWatcher()
        {
            // ── 1. WinEvent хук ─────────────────────────────────────────────
            _winEventDelegate = OnShellWinEvent;   // держим ссылку — иначе GC уберёт!
            _winEventHook = SetWinEventHook(
                EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW,
                IntPtr.Zero, _winEventDelegate,
                0, 0, WINEVENT_OUTOFCONTEXT);

            // ── 2. Backup-таймер (был 3 сек, теперь 500 мс) ─────────────────
            _taskbarWatcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _taskbarWatcher.Tick += (s, e) =>
            {
                try { if (TaskbarHelper.IsSystemTaskbarVisible()) TaskbarHelper.Hide(); }
                catch { }
            };
            _taskbarWatcher.Start();
        }

        // Вызывается системой немедленно при создании/показе любого окна.
        // Нас интересует только Shell_TrayWnd / Shell_SecondaryTrayWnd —
        // это признак того, что Explorer только что перезапустился.
        void OnShellWinEvent(IntPtr hHook, uint eventType, IntPtr hwnd,
                             int idObject, int idChild,
                             uint dwEventThread, uint dwmsEventTime)
        {
            // idObject == 0 → OBJID_WINDOW (само окно, не его дочерние элементы)
            if (hwnd == IntPtr.Zero || idObject != 0) return;
            try
            {
                var sb = new StringBuilder(64);
                GetWindowClassName(hwnd, sb, sb.Capacity);
                string cls = sb.ToString();
                if (cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd") return;

                // Explorer только что создал/показал панель задач.
                // Небольшая пауза (250 мс) даём Explorer'у завершить инициализацию,
                // затем скрываем — всё это на UI-потоке через Dispatcher.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    t.Tick += (s, e) =>
                    {
                        t.Stop();
                        try { TaskbarHelper.Hide(); }
                        catch { }

                        // [DESKTOP-REFRESH] При первом старте на медленных ПК Explorer
                        // не успевает нарисовать рабочий стол — получаем чёрный экран.
                        // Посылаем Progman команду обновить иконки рабочего стола.
                        // Срабатывает только один раз за сессию.
                        if (!_desktopRefreshDone)
                        {
                            _desktopRefreshDone = true;
                            RefreshDesktop();
                        }
                    };
                    t.Start();
                }));
            }
            catch { }
        }

        // [DESKTOP-REFRESH] Принудительно обновляет рабочий стол Explorer'а.
        // Используется при старте на медленных ПК где Explorer не успел инициализироваться.
        void RefreshDesktop()
        {
            try
            {
                // Шаг 1: SHChangeNotify — сигнализируем Explorer что рабочий стол изменился
                SHChangeNotify(0x8000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }

            try
            {
                // Шаг 2: PostMessage на Progman — триггер перерисовки иконок рабочего стола
                // 0x0403 = WM_SETTINGCHANGE (заставляет Explorer перечитать настройки рабочего стола)
                IntPtr progman = FindWindow("Progman", null);
                if (progman != IntPtr.Zero)
                {
                    PostMessage(progman, 0x0052, IntPtr.Zero, IntPtr.Zero); // WM_SETICON — будит Progman
                    PostMessage(progman, 0x0403, IntPtr.Zero, IntPtr.Zero); // WM_SETTINGCHANGE
                }
            }
            catch { }
        }

    }
}
