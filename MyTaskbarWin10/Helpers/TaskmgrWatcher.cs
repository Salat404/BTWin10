using System;
using System.Runtime.InteropServices;
using System.Text;

// ═══════════════════════════════════════════════════════════════════════════════
// TaskmgrWatcher — хелпер для определения запущенного Диспетчера задач
//                  и получения его имени процесса БЕЗ прав администратора.
//
// ПРОБЛЕМА (оригинальная):
//   taskmgr.exe запускается с повышенными привилегиями (High/System IL).
//   Process.GetProcessById(pid).ProcessName бросает Win32Exception (Access Denied)
//   для привилегированных процессов — и SafeGetProcessName возвращает "",
//   из-за чего окно taskmgr игнорируется в EnumWindows.
//
// РЕШЕНИЕ (оригинальное):
//   OpenProcess с PROCESS_QUERY_LIMITED_INFORMATION (0x1000) — этот флаг
//   разрешён ЛЮБОМУ процессу для ЛЮБОГО другого, независимо от уровня
//   целостности (IL). Затем QueryFullProcessImageName даёт полный путь
//   к EXE без чтения памяти процесса.
//
// ─────────────────────────────────────────────────────────────────────────────
// [FIX-TASKMGR-FOCUS] ЛОГИКА УПРАВЛЕНИЯ ФОКУСОМ
// ─────────────────────────────────────────────────────────────────────────────
// Базовое правило:
//   Фокус диспетчера задач убирается ВСЕГДА — Shell не должен видеть
//   foreground == High IL, иначе Win-key открывает оригинальный Пуск.
//
// Исключения (временный фокус на 1.5с):
//   1. Пользователь начинает перетаскивать окно (EVENT_SYSTEM_MOVESIZESTART).
//      Фокус нужен чтобы drag работал корректно.
//      По окончании перетаскивания (EVENT_SYSTEM_MOVESIZEEND) — немедленно
//      убираем фокус.
//
//   2. Пользователь нажимает кнопку закрытия — определяется через
//      WM_NCLBUTTONDOWN с HTCLOSE (хит-тест на title bar кнопку закрытия).
//      Фокус нужен чтобы WM_CLOSE был обработан. Через 1.5с фокус уходит.
//
//   В обоих случаях используется _tempFocusTimer: если активен — StealFocus
//   не вызывается ни таймером, ни WinEvent-хуком.
//
// ═══════════════════════════════════════════════════════════════════════════════

namespace MyTaskbar.Helpers
{
    public static class TaskmgrWatcher
    {
        // ── P/Invoke ──────────────────────────────────────────────────────────

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
        const uint EVENT_SYSTEM_MOVESIZEEND   = 0x000B;
        const uint EVENT_SYSTEM_FOREGROUND    = 0x0003;
        const uint WINEVENT_OUTOFCONTEXT      = 0x0000;

        // WM_NCLBUTTONDOWN + HTCLOSE — клик по кнопке закрытия в non-client area
        const int WM_NCLBUTTONDOWN = 0x00A1;
        const int HTCLOSE          = 20;

        // Длительность "временного окна фокуса" в мс
        const int TEMP_FOCUS_DURATION_MS = 1500;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(
            IntPtr hProcess,
            uint dwFlags,
            [Out] StringBuilder lpExeName,
            ref uint lpdwSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc,
            uint idProcess, uint idThread,
            uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(uint dwProcessId);
        const uint ASFW_ANY = 0xFFFFFFFF;

        // SetWindowsHookEx для перехвата WM_NCLBUTTONDOWN (кнопка закрытия)
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);


        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint  mouseData;
            public uint  flags;
            public uint  time;
            public IntPtr dwExtraInfo;
        }

        const int WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x0201;

        delegate void WinEventDelegate(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        // ── Состояние ─────────────────────────────────────────────────────────

        static IntPtr           _focusGuardHook     = IntPtr.Zero;
        static WinEventDelegate _focusGuardDelegate;

        static IntPtr           _moveSizeHook       = IntPtr.Zero;
        static WinEventDelegate _moveSizeDelegate;
        static volatile bool    _taskmgrMoving      = false;

        // Хук низкоуровневой мыши — ловим клик по кнопке закрытия taskmgr
        static IntPtr          _mouseHook          = IntPtr.Zero;
        static LowLevelMouseProc _mouseProcDelegate;

        // Таймер опроса foreground (200мс)
        static System.Windows.Threading.DispatcherTimer _pollTimer;

        // Таймер временного фокуса — пока активен, StealFocus не вызывается
        // Сбрасывается через TEMP_FOCUS_DURATION_MS после начала drag/close
        static System.Windows.Threading.DispatcherTimer _tempFocusTimer;

        // ── FocusGuard: публичный API ─────────────────────────────────────────

        /// <summary>
        /// Запускает постоянный мониторинг foreground-окна.
        /// Слой 1: WinEvent EVENT_SYSTEM_FOREGROUND — мгновенная реакция при смене окна.
        /// Слой 2: DispatcherTimer 200мс — перехватывает клики/drag внутри уже активного taskmgr.
        /// Слой 3: WinEvent EVENT_SYSTEM_MOVESIZE* — точный флаг drag/resize.
        /// Слой 4: LowLevel mouse hook — кнопка закрытия taskmgr.
        /// ВАЖНО: вызывать из UI-потока (STA с message loop).
        /// </summary>
        public static void StartFocusGuard(System.Windows.Threading.Dispatcher dispatcher)
        {
            if (_focusGuardHook != IntPtr.Zero) return;

            // Слой 1: WinEvent foreground
            _focusGuardDelegate = OnForegroundChanged;
            _focusGuardHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _focusGuardDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);

            // Слой 2: WinEvent MoveSize (drag/resize)
            _moveSizeDelegate = OnMoveSizeEvent;
            _moveSizeHook = SetWinEventHook(
                EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND,
                IntPtr.Zero, _moveSizeDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);

            // Слой 3: LowLevel mouse hook — кнопка закрытия
            _mouseProcDelegate = OnLowLevelMouse;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProcDelegate, IntPtr.Zero, 0);

            // Слой 4: таймер временного фокуса — убирает фокус после drag/close
            _tempFocusTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Normal,
                dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(TEMP_FOCUS_DURATION_MS)
            };
            _tempFocusTimer.Tick += (s, e) =>
            {
                _tempFocusTimer.Stop();
                // Временное окно истекло — убираем фокус если всё ещё у taskmgr
                try
                {
                    IntPtr fg = GetForegroundWindow();
                    if (fg != IntPtr.Zero && IsTaskmgrHwnd(fg))
                        StealFocus();
                }
                catch { }
            };

            // Слой 5: опросный таймер 200мс
            _pollTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Normal,
                dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _pollTimer.Tick += (s, e) =>
            {
                try
                {
                    // Не трогаем фокус пока временное окно активно
                    if (IsTempFocusActive()) return;
                    IntPtr fg = GetForegroundWindow();
                    if (fg != IntPtr.Zero && IsTaskmgrHwnd(fg))
                        StealFocus();
                }
                catch { }
            };
            _pollTimer.Start();
        }

        /// <summary>
        /// Останавливает мониторинг и освобождает ресурсы.
        /// </summary>
        public static void StopFocusGuard()
        {
            if (_focusGuardHook == IntPtr.Zero && _pollTimer == null) return;

            try { UnhookWinEvent(_focusGuardHook); } catch { }
            _focusGuardHook     = IntPtr.Zero;
            _focusGuardDelegate = null;

            try { UnhookWinEvent(_moveSizeHook); } catch { }
            _moveSizeHook     = IntPtr.Zero;
            _moveSizeDelegate = null;
            _taskmgrMoving    = false;

            try { if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook); } catch { }
            _mouseHook         = IntPtr.Zero;
            _mouseProcDelegate = null;

            try { _tempFocusTimer?.Stop(); } catch { }
            _tempFocusTimer = null;

            try { _pollTimer?.Stop(); } catch { }
            _pollTimer = null;
        }

        // ── Внутренние методы ─────────────────────────────────────────────────

        /// <summary>
        /// Проверяет, активно ли временное окно фокуса.
        /// Пока активно — StealFocus не вызывается (drag или закрытие в процессе).
        /// </summary>
        static bool IsTempFocusActive()
        {
            return _taskmgrMoving || (_tempFocusTimer != null && _tempFocusTimer.IsEnabled);
        }

        /// <summary>
        /// Начинает временное окно фокуса на TEMP_FOCUS_DURATION_MS.
        /// Гарантирует что диспетчер задач получит фокус и сможет завершить действие.
        /// </summary>
        static void BeginTempFocus()
        {
            // Перезапускаем таймер (продлеваем если уже идёт)
            try
            {
                _tempFocusTimer?.Stop();
                _tempFocusTimer?.Start();
            }
            catch { }

            // Явно отдаём фокус диспетчеру задач чтобы он мог обработать действие
            try
            {
                IntPtr taskmgrHwnd = FindWindow("TaskManagerWindow", null);
                if (taskmgrHwnd == IntPtr.Zero) return;

                uint taskmgrTid = GetWindowThreadProcessId(taskmgrHwnd, out _);
                uint myTid      = GetCurrentThreadId();

                AllowSetForegroundWindow(ASFW_ANY);
                bool attached = (taskmgrTid != 0 && taskmgrTid != myTid)
                    && AttachThreadInput(myTid, taskmgrTid, true);
                try
                {
                    SetForegroundWindow(taskmgrHwnd);
                }
                finally
                {
                    if (attached)
                        AttachThreadInput(myTid, taskmgrTid, false);
                }
            }
            catch { }
        }

        // WinEvent: смена foreground-окна
        static void OnForegroundChanged(
            IntPtr hHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                if (!IsTaskmgrHwnd(hwnd)) return;
                // Временное окно активно — не трогаем
                if (IsTempFocusActive()) return;
                StealFocus();
            }
            catch { }
        }

        // WinEvent: начало/конец перетаскивания окна taskmgr
        static void OnMoveSizeEvent(
            IntPtr hHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                if (!IsTaskmgrHwnd(hwnd)) return;

                if (eventType == EVENT_SYSTEM_MOVESIZESTART)
                {
                    // Пользователь начал перетаскивать — даём фокус
                    _taskmgrMoving = true;
                    BeginTempFocus();
                }
                else // EVENT_SYSTEM_MOVESIZEEND
                {
                    // Перетаскивание закончено — сбрасываем флаг, запускаем таймер отъёма фокуса
                    _taskmgrMoving = false;
                    // Перезапускаем tempFocusTimer с нуля чтобы дать 1.5с на завершение
                    try
                    {
                        _tempFocusTimer?.Stop();
                        _tempFocusTimer?.Start();
                    }
                    catch { }
                }
            }
            catch { }
        }

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        // Высота title bar в пикселях (non-client area сверху окна).
        // SM_CYCAPTION — высота caption, SM_CYBORDER — рамка.
        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);
        const int SM_CYCAPTION = 4;
        const int SM_CYBORDER  = 6;

        // LowLevel mouse hook: перехватываем клик по title bar / non-client area taskmgr.
        //
        // Почему WM_NCHITTEST / SendMessage не работал:
        //   taskmgr работает на High IL. Medium IL процесс не может посылать
        //   сообщения в High IL окно через SendMessage — возвращается 0 (HTNOWHERE).
        //   WindowFromPoint возвращает дочернее окно (TaskMgrRebar), у которого
        //   hit-test отличается от родительского. Итог — BeginTempFocus не вызывался.
        //
        // Новое решение — геометрический hit-test без IPC:
        //   GetWindowRect даёт координаты окна без обращения в его поток.
        //   Title bar = верхние (SM_CYCAPTION + SM_CYBORDER) пикселей окна.
        //   Если курсор попал туда — даём временный фокус.
        //   Это работает для High IL окон без каких-либо привилегий.
        static IntPtr OnLowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && wParam == (IntPtr)WM_LBUTTONDOWN)
                {
                    var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));

                    // Ищем главное окно диспетчера напрямую — не через WindowFromPoint,
                    // чтобы не зависеть от того дочернее окно или нет под курсором
                    IntPtr taskmgrHwnd = FindWindow("TaskManagerWindow", null);
                    if (taskmgrHwnd == IntPtr.Zero) goto done;

                    if (!GetWindowRect(taskmgrHwnd, out RECT r)) goto done;

                    int cx = info.pt.x;
                    int cy = info.pt.y;

                    // Курсор вообще над окном диспетчера?
                    if (cx < r.left || cx > r.right || cy < r.top || cy > r.bottom) goto done;

                    // Высота title bar = SM_CYCAPTION + SM_CYBORDER (рамка сверху)
                    int titleBarH = GetSystemMetrics(SM_CYCAPTION) + GetSystemMetrics(SM_CYBORDER);

                    // Клик в верхней полосе окна — title bar (drag + кнопки управления)
                    if (cy <= r.top + titleBarH)
                    {
                        BeginTempFocus();
                    }

                    done:;
                }
            }
            catch { }

            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // Проверяет принадлежность hwnd диспетчеру задач по window class
        static bool IsTaskmgrHwnd(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();
            return cls == "TaskManagerWindow" || cls == "TaskMgrRebar";
        }

        // Уводит foreground на Progman (рабочий стол, Medium IL)
        static void StealFocus()
        {
            try
            {
                IntPtr desktop = FindWindow("Progman", null);
                if (desktop == IntPtr.Zero) return;

                IntPtr taskmgrHwnd = FindWindow("TaskManagerWindow", null);
                if (taskmgrHwnd == IntPtr.Zero) return;

                uint taskmgrTid = GetWindowThreadProcessId(taskmgrHwnd, out _);
                uint myTid      = GetCurrentThreadId();

                AllowSetForegroundWindow(ASFW_ANY);

                bool attached = (taskmgrTid != 0 && taskmgrTid != myTid)
                    && AttachThreadInput(myTid, taskmgrTid, true);
                try
                {
                    SetForegroundWindow(desktop);
                }
                finally
                {
                    if (attached)
                        AttachThreadInput(myTid, taskmgrTid, false);
                }
            }
            catch { }
        }

        // ── Публичные методы (оригинальные) ──────────────────────────────────

        public static string GetProcessNameByPid(uint pid)
        {
            if (pid == 0) return "";

            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return "";

            try
            {
                var sb = new StringBuilder(260);
                uint size = (uint)sb.Capacity;
                if (!QueryFullProcessImageName(hProc, 0, sb, ref size))
                    return "";

                string fullPath = sb.ToString(0, (int)size);
                if (string.IsNullOrEmpty(fullPath)) return "";

                string fileName = System.IO.Path.GetFileNameWithoutExtension(fullPath);
                return fileName?.ToLowerInvariant() ?? "";
            }
            catch
            {
                return "";
            }
            finally
            {
                CloseHandle(hProc);
            }
        }

        /// <summary>
        /// Возвращает полный путь к исполняемому файлу процесса через
        /// QueryFullProcessImageName (работает для elevated-процессов,
        /// для которых Process.MainModule бросает Win32Exception).
        /// Возвращает пустую строку если путь недоступен.
        /// </summary>
        public static string GetProcessFullPathByPid(uint pid)
        {
            if (pid == 0) return "";

            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return "";

            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (!QueryFullProcessImageName(hProc, 0, sb, ref size))
                    return "";

                string fullPath = sb.ToString(0, (int)size);
                return fullPath ?? "";
            }
            catch
            {
                return "";
            }
            finally
            {
                CloseHandle(hProc);
            }
        }

        public static bool IsTaskManagerRunning()
        {
            IntPtr hwnd = FindWindow("TaskManagerWindow", null);
            if (hwnd != IntPtr.Zero) return true;
            hwnd = FindWindow(null, "Диспетчер задач");
            if (hwnd != IntPtr.Zero) return true;
            hwnd = FindWindow(null, "Task Manager");
            return hwnd != IntPtr.Zero;
        }

        public static void LaunchTaskManager()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("ms-taskmgr:")
                {
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
                return;
            }
            catch { }

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("taskmgr.exe")
                {
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }
    }
}
