using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// ═══════════════════════════════════════════════════════════════════════════════
// StartMenuBlocker — блокировка системного меню Пуск Win10 когда активен
//                    Диспетчер задач (или другие привилегированные окна).
//
// ПРОБЛЕМА:
//   Когда Диспетчер задач является foreground-окном и пользователь нажимает Win,
//   Windows открывает системное меню Пуск Win10 РАНЬШЕ, чем наш low-level
//   keyboard hook успевает вернуть (IntPtr)1 (подавить клавишу).
//   Это происходит потому, что taskmgr работает на High IL — Windows обрабатывает
//   Win-клавишу на уровне ядра для привилегированных окон до обычных хуков.
//   В результате: хук срабатывает, TryCloseSystemStartMenu() закрывает Пуск,
//   но наше меню НЕ открывается (JustHidden = false, IsVisible = false уже),
//   потому что Пуск успел мигнуть и закрыться — а пользователь видит либо
//   пустой экран, либо задержку.
//
// РЕШЕНИЕ:
//   1. WinEventHook (SetWinEventHook) на EVENT_SYSTEM_FOREGROUND — отслеживаем
//      когда taskmgr/другое привилегированное окно становится foreground.
//   2. Пока такое окно активно — запускаем агрессивный мониторинг:
//      фоновый поток проверяет появление системного Пуска каждые 30 мс
//      и мгновенно закрывает его через WM_KEYDOWN/VK_ESCAPE.
//   3. OnWinKeyDown в MainWindow вызывает NotifyWinKeyPressed() — это сигнал
//      что нужно сделать дополнительные попытки закрытия через 50/100/200 мс.
//
// КАК ИСПОЛЬЗОВАТЬ:
//   В MainWindow.xaml.cs:
//     1. Создать экземпляр: _startMenuBlocker = new StartMenuBlocker();
//     2. В Loaded: _startMenuBlocker.Start();
//     3. В OnWinKeyDown перед ShowMenu(): _startMenuBlocker.NotifyWinKeyPressed();
//     4. В MainWindow_Closed: _startMenuBlocker.Stop();
//
// ═══════════════════════════════════════════════════════════════════════════════

namespace MyTaskbar.Helpers
{
    public sealed class StartMenuBlocker : IDisposable
    {
        // ── P/Invoke ──────────────────────────────────────────────────────────

        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
            [Out] StringBuilder lpExeName, ref uint lpdwSize);

        // ── Константы ─────────────────────────────────────────────────────────

        const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
        const uint WM_KEYDOWN              = 0x0100;
        const uint VK_ESCAPE               = 0x1B;
        const uint SMTO_ABORTIFHUNG        = 0x0002;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // Имена привилегированных процессов, при активности которых
        // нужна агрессивная блокировка системного Пуска
        static readonly string[] PrivilegedProcesses =
        {
            "taskmgr", "regedit", "mmc", "eventvwr", "perfmon",
            "compmgmt", "services", "devmgmt", "diskmgmt"
        };

        // ── Состояние ─────────────────────────────────────────────────────────

        IntPtr _winEventHook = IntPtr.Zero;
        WinEventDelegate _winEventDelegate; // держим ссылку чтобы GC не собрал

        // Флаг: сейчас foreground — привилегированный процесс?
        volatile bool _privilegedForeground = false;

        // Сигнал от OnWinKeyDown: нужны дополнительные попытки закрытия Пуска
        volatile bool _winKeyPending = false;
        long _winKeyPendingAt = 0;

        // Фоновый поток мониторинга
        Thread _monitorThread;
        volatile bool _running = false;
        readonly ManualResetEventSlim _stopEvent = new ManualResetEventSlim(false);

        // ── Публичные методы ──────────────────────────────────────────────────

        /// <summary>
        /// Запустить блокировщик. Вызывать из Loaded на UI-потоке.
        /// </summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _stopEvent.Reset();

            // WinEvent хук — уведомления о смене foreground окна
            _winEventDelegate = OnWinEvent;
            _winEventHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);

            // Проверим текущее foreground сразу при старте
            CheckCurrentForeground();

            // Фоновый мониторинг
            _monitorThread = new Thread(MonitorLoop)
            {
                IsBackground = true,
                Name = "StartMenuBlocker"
            };
            _monitorThread.Start();
        }

        /// <summary>
        /// Остановить блокировщик. Вызывать при закрытии окна.
        /// </summary>
        public void Stop()
        {
            _running = false;
            _stopEvent.Set();

            if (_winEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Вызывать из OnWinKeyDown в MainWindow ПЕРЕД ShowMenu().
        /// Сигнализирует что нужны отложенные попытки закрытия системного Пуска —
        /// на случай если он откроется с задержкой (так бывает когда taskmgr foreground).
        /// </summary>
        public void NotifyWinKeyPressed()
        {
            _winKeyPending = true;
            Interlocked.Exchange(ref _winKeyPendingAt, DateTime.UtcNow.Ticks);
        }

        public void Dispose() => Stop();

        // ── Внутренняя логика ─────────────────────────────────────────────────

        void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (eventType == EVENT_SYSTEM_FOREGROUND)
                CheckCurrentForeground();
        }

        void CheckCurrentForeground()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) { _privilegedForeground = false; return; }

                if (!GetWindowThreadProcessId(fg, out uint pid)) { _privilegedForeground = false; return; }

                string name = GetProcessNameByPid(pid);
                bool isPriv = IsPrivilegedProcess(name);
                _privilegedForeground = isPriv;

                System.Diagnostics.Debug.WriteLineIf(isPriv,
                    $"[StartMenuBlocker] Privileged foreground: {name} (pid={pid})");
            }
            catch
            {
                _privilegedForeground = false;
            }
        }

        void MonitorLoop()
        {
            while (_running)
            {
                try
                {
                    bool needAggressiveCheck = _privilegedForeground;

                    // Если был сигнал Win-клавиши — агрессивно проверяем 300 мс
                    if (_winKeyPending)
                    {
                        long pressedAt = Interlocked.Read(ref _winKeyPendingAt);
                        double elapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - pressedAt).TotalMilliseconds;
                        if (elapsed > 300)
                            _winKeyPending = false;
                        else
                            needAggressiveCheck = true;
                    }

                    if (needAggressiveCheck)
                    {
                        if (IsSystemStartMenuVisible())
                        {
                            CloseSystemStartMenu();
                            System.Diagnostics.Debug.WriteLine("[StartMenuBlocker] Closed system Start Menu");
                        }
                    }
                }
                catch { }

                // Интервал: 30 мс при активном мониторинге, 200 мс иначе
                bool fast = _privilegedForeground || _winKeyPending;
                _stopEvent.Wait(fast ? 30 : 200);
                if (!_running) break;
            }
        }

        bool IsSystemStartMenuVisible()
        {
            // Win10: CoreWindow с заголовком "Пуск" / "Start"
            IntPtr h = FindWindow("Windows.UI.Core.CoreWindow", "Пуск");
            if (h != IntPtr.Zero && IsWindowVisible(h)) return true;

            h = FindWindow("Windows.UI.Core.CoreWindow", "Start");
            if (h != IntPtr.Zero && IsWindowVisible(h)) return true;

            // Win11 / некоторые сборки Win10
            h = FindWindow("Microsoft.UI.Content.DesktopChildSiteBridge", null);
            if (h != IntPtr.Zero && IsWindowVisible(h)) return true;

            return false;
        }

        void CloseSystemStartMenu()
        {
            // 1. CoreWindow "Пуск"
            IntPtr h = FindWindow("Windows.UI.Core.CoreWindow", "Пуск");
            if (h == IntPtr.Zero) h = FindWindow("Windows.UI.Core.CoreWindow", "Start");
            if (h != IntPtr.Zero && IsWindowVisible(h))
            {
                SafeSend(h, WM_KEYDOWN, VK_ESCAPE);
                return;
            }

            // 2. Win11 / DesktopChildSiteBridge
            h = FindWindow("Microsoft.UI.Content.DesktopChildSiteBridge", null);
            if (h != IntPtr.Zero && IsWindowVisible(h))
                SafeSend(h, WM_KEYDOWN, VK_ESCAPE);
        }

        static void SafeSend(IntPtr hwnd, uint msg, uint vk)
        {
            try
            {
                SendMessageTimeout(hwnd, msg, (IntPtr)vk, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, 100, out _);
            }
            catch { }
        }

        static bool IsPrivilegedProcess(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var p in PrivilegedProcesses)
                if (name == p) return true;
            return false;
        }

        static string GetProcessNameByPid(uint pid)
        {
            if (pid == 0) return "";
            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(260);
                uint size = (uint)sb.Capacity;
                if (!QueryFullProcessImageName(hProc, 0, sb, ref size)) return "";
                string full = sb.ToString(0, (int)size);
                return string.IsNullOrEmpty(full) ? "" :
                    System.IO.Path.GetFileNameWithoutExtension(full)?.ToLowerInvariant() ?? "";
            }
            catch { return ""; }
            finally { CloseHandle(hProc); }
        }
    }
}
