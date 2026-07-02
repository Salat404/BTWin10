using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;

// ═══════════════════════════════════════════════════════════════════════════════
// AggressiveStartMenuBlocker — полностью вырезает системный Пуск Win10/11
//
// ПРОБЛЕМА:
//   1. Keyboard hook срабатывает, но Windows на уровне ядра уже обработала Win
//   2. Пуск открывается МИЛЛИСЕКУНДЫ после hook-обработки, прежде чем меню показалось
//   3. Когда taskmgr в фокусе (High IL), Windows обрабатывает Win до обычных процессов
//   4. Попытка закрытия в OnWinKeyDown слишком ранняя — Пуск ещё не открылся
//
// РЕШЕНИЕ:
//   1. Постоянный ФОНОВЫЙ ПОТОК (30мс polling) проверяет и закрывает Пуск
//   2. При нажатии Win в OnWinKeyDown — продлить интенсивное мониторинг на 300мс
//   3. При обнаружении появления Пуска в фокусе-задач — мгновенно закрыть
//   4. Не полагаться на delay-таймеры — использовать постоянный monitor
//
// ═══════════════════════════════════════════════════════════════════════════════

namespace MyTaskbar.Helpers
{
    public sealed class AggressiveStartMenuBlocker : IDisposable
    {
        // ── P/Invoke ──────────────────────────────────────────────────────────

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        const uint WM_KEYDOWN = 0x0100;
        const uint VK_ESCAPE = 0x1B;
        const uint SMTO_ABORTIFHUNG = 0x0002;

        // ── Состояние ─────────────────────────────────────────────────────────

        private Thread _monitorThread;
        private volatile bool _running = false;
        private volatile bool _winKeyPending = false;
        private long _winKeyPendingTime = 0;
        private readonly object _lockObj = new object();

        // ── Публичные методы ──────────────────────────────────────────────────

        /// <summary>
        /// Запустить мониторинг. Вызывать из Loaded на UI-потоке.
        /// </summary>
        public void Start()
        {
            lock (_lockObj)
            {
                if (_running) return;
                _running = true;

                _monitorThread = new Thread(MonitorLoop)
                {
                    Name = "AggressiveStartMenuBlocker",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                _monitorThread.Start();
            }
        }

        /// <summary>
        /// Остановить мониторинг. Вызывать при закрытии приложения.
        /// </summary>
        public void Stop()
        {
            lock (_lockObj)
            {
                _running = false;
            }
            if (_monitorThread != null)
            {
                _monitorThread.Join(500);
            }
        }

        /// <summary>
        /// Сигнал: Windows был нажат. Активирует интенсивное закрытие на 300мс.
        /// Вызывать ИЗ OnWinKeyDown ПЕРЕД ShowMenu().
        /// </summary>
        public void NotifyWinKeyPressed()
        {
            _winKeyPending = true;
            Interlocked.Exchange(ref _winKeyPendingTime, DateTime.UtcNow.Ticks);
        }

        public void Dispose() => Stop();

        // ── Мониторинг ────────────────────────────────────────────────────────

        private void MonitorLoop()
        {
            while (_running)
            {
                try
                {
                    bool needIntensive = _winKeyPending;
                    if (needIntensive)
                    {
                        long elapsed = DateTime.UtcNow.Ticks - Interlocked.Read(ref _winKeyPendingTime);
                        // Интенсивное закрытие 300мс после нажатия Win
                        if (TimeSpan.FromTicks(elapsed).TotalMilliseconds > 300)
                        {
                            _winKeyPending = false;
                            needIntensive = false;
                        }
                    }

                    // Каждый цикл проверяем, открыт ли Пуск, и закрываем его
                    if (IsStartMenuVisible())
                    {
                        CloseStartMenu();
                    }

                    // Интервал: 30мс при нажатии Win, 100мс иначе
                    int sleepMs = needIntensive ? 30 : 100;
                    Thread.Sleep(sleepMs);
                }
                catch { }
            }
        }

        /// <summary>
        /// Проверяет видимость системного Пуска Win10/11.
        /// </summary>
        private bool IsStartMenuVisible()
        {
            try
            {
                // Win10: CoreWindow "Пуск" или "Start"
                IntPtr h = FindWindow("Windows.UI.Core.CoreWindow", "Пуск");
                if (h != IntPtr.Zero && IsWindowVisible(h)) return true;

                h = FindWindow("Windows.UI.Core.CoreWindow", "Start");
                if (h != IntPtr.Zero && IsWindowVisible(h)) return true;

                // Win11 / альтернативный хост
                h = FindWindow("Microsoft.UI.Content.DesktopChildSiteBridge", null);
                if (h != IntPtr.Zero && IsWindowVisible(h)) return true;

                // Fallback: SearchIndexWindow (иногда Пуск скрывается под другим классом)
                h = FindWindow("SearchIndexWindow", null);
                if (h != IntPtr.Zero && IsWindowVisible(h)) return true;

                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Закрывает системный Пуск через WM_KEYDOWN/VK_ESCAPE.
        /// </summary>
        private void CloseStartMenu()
        {
            try
            {
                // Win10: CoreWindow "Пуск" / "Start"
                IntPtr h = FindWindow("Windows.UI.Core.CoreWindow", "Пуск");
                if (h == IntPtr.Zero)
                    h = FindWindow("Windows.UI.Core.CoreWindow", "Start");

                if (h != IntPtr.Zero && IsWindowVisible(h))
                {
                    SendEscape(h);
                    return;
                }

                // Win11 / DesktopChildSiteBridge
                h = FindWindow("Microsoft.UI.Content.DesktopChildSiteBridge", null);
                if (h != IntPtr.Zero && IsWindowVisible(h))
                {
                    SendEscape(h);
                    return;
                }

                // Fallback для SearchIndexWindow
                h = FindWindow("SearchIndexWindow", null);
                if (h != IntPtr.Zero && IsWindowVisible(h))
                {
                    SendEscape(h);
                }
            }
            catch { }
        }

        /// <summary>
        /// Безопасно отправляет ESC в окно без риска зависания.
        /// </summary>
        private static void SendEscape(IntPtr hwnd)
        {
            try
            {
                SendMessageTimeout(hwnd, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, 100, out _);
            }
            catch { }
        }
    }
}
