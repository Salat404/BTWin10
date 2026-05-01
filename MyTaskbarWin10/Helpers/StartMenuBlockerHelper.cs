using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

// ═══════════════════════════════════════════════════════════════════════════════
// StartMenuBlockerHelper — закрывает системный Пуск если он открылся
//                          после нажатия Win, пока taskmgr (или другой
//                          High-IL процесс) был в фокусе.
//
// ПРОБЛЕМА:
//   WH_KEYBOARD_LL подавляет Win key на уровне сообщений (return 1),
//   но Windows 10 Start menu host определяет нажатие Win независимо —
//   через GetAsyncKeyState-polling или Raw Input в своём процессе.
//   Из-за этого TryCloseSystemStartMenu в OnWinKeyDown вызывается до
//   того как Пуск успел открыться и не находит его окно.
//   Проявляется именно когда в фокусе High-IL процесс (taskmgr, regedit).
//
// РЕШЕНИЕ:
//   После нажатия Win вызвать BeginBlock() — хелпер запускает
//   DispatcherTimer (UI-поток), который каждые POLL_MS миллисекунд
//   проверяет не открылся ли системный Пуск и немедленно закрывает его
//   (ESC). Polling продолжается TOTAL_MS миллисекунд, затем
//   автоматически останавливается.
//
// КАК ИСПОЛЬЗОВАТЬ:
//   // В OnWinKeyDown ПОСЛЕ TryCloseSystemStartMenu():
//   StartMenuBlockerHelper.BeginBlock(Dispatcher);
//
//   // При необходимости досрочной остановки (не обязательно):
//   StartMenuBlockerHelper.Cancel();
//
// ═══════════════════════════════════════════════════════════════════════════════

namespace MyTaskbar.Helpers
{
    public static class StartMenuBlockerHelper
    {
        // ── Настройки ──────────────────────────────────────────────────────────

        /// <summary>Интервал проверки (мс). Не делать меньше 30 — лишняя нагрузка.</summary>
        const int POLL_MS = 40;

        /// <summary>Суммарное время блокировки после нажатия Win (мс).</summary>
        const int TOTAL_MS = 300;

        // ── P/Invoke ──────────────────────────────────────────────────────────

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        // SendMessageTimeout: безопасная отправка без риска зависания
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        const uint WM_KEYDOWN   = 0x0100;
        const int  VK_ESCAPE    = 0x1B;
        const uint SMTO_ABORTIFHUNG = 0x0002;

        // ── Состояние ─────────────────────────────────────────────────────────

        static DispatcherTimer _timer;
        static int             _ticksLeft;

        // ── Публичный API ─────────────────────────────────────────────────────

        /// <summary>
        /// Запускает polling-закрытие системного Пуска на TOTAL_MS мс.
        /// Безопасно вызывать повторно — перезапускает таймер.
        /// Должен вызываться из UI-потока (например, из OnWinKeyDown).
        /// </summary>
        public static void BeginBlock(Dispatcher dispatcher)
        {
            if (dispatcher == null) return;

            // Если таймер уже работает — просто сбрасываем счётчик тиков,
            // чтобы продлить блокировку ещё на TOTAL_MS от текущего момента.
            if (_timer != null && _timer.IsEnabled)
            {
                _ticksLeft = TOTAL_MS / POLL_MS;
                return;
            }

            _ticksLeft = TOTAL_MS / POLL_MS;

            _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(POLL_MS)
            };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        /// <summary>Досрочная остановка (вызывать не обязательно).</summary>
        public static void Cancel()
        {
            if (_timer == null) return;
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        // ── Внутренняя логика ─────────────────────────────────────────────────

        static void OnTick(object sender, EventArgs e)
        {
            _ticksLeft--;
            if (_ticksLeft <= 0)
            {
                Cancel();
                return;
            }

            TryClose();
        }

        /// <summary>
        /// Закрывает системный Пуск если он видим.
        /// Аналогично MainWindow.TryCloseSystemStartMenu, но вынесено сюда
        /// для самостоятельного использования.
        /// </summary>
        static void TryClose()
        {
            try
            {
                // Win10 Start: CoreWindow класс, заголовок «Пуск» / «Start»
                IntPtr h = FindWindow("Windows.UI.Core.CoreWindow", "Пуск");
                if (h == IntPtr.Zero)
                    h = FindWindow("Windows.UI.Core.CoreWindow", "Start");

                if (h != IntPtr.Zero && IsWindowVisible(h))
                {
                    SendMessageTimeout(h, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero,
                                       SMTO_ABORTIFHUNG, 100, out _);
                    // Досрочно останавливаем — Пуск найден и закрыт,
                    // дальнейший polling не нужен.
                    Cancel();
                    return;
                }

                // Win11 / альтернативный хост
                h = FindWindow("Microsoft.UI.Content.DesktopChildSiteBridge", null);
                if (h != IntPtr.Zero && IsWindowVisible(h))
                {
                    SendMessageTimeout(h, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero,
                                       SMTO_ABORTIFHUNG, 100, out _);
                    Cancel();
                }
            }
            catch { }
        }
    }
}
