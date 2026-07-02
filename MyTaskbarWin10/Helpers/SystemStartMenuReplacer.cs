using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace MyTaskbar.Helpers
{
    /// <summary>
    /// Повна заміна системного Start Menu (Win10) на кастомний.
    /// - Перехоплює Win-key на низькому рівні
    /// - Підавляє системне Star Menu
    /// - Відкриває наше кастомне меню
    /// - Синхронізується з Shell Replacement
    /// </summary>
    public sealed class SystemStartMenuReplacer : IDisposable
    {
        // P/Invoke для низькорівневого перехоплення клавіш
        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        // Константи
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_KEYUP = 0x0101;
        const int VK_LWIN = 0x5B;
        const int VK_RWIN = 0x5C;
        const int VK_ESCAPE = 0x1B;

        // Стан
        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelKeyboardProc _proc = null;
        private Action _onStartMenuRequested;
        private IntPtr _taskbarHwnd = IntPtr.Zero;

        public SystemStartMenuReplacer(Action onStartMenuRequested)
        {
            _onStartMenuRequested = onStartMenuRequested;
            _proc = HookCallback;
        }

        public void Start(IntPtr taskbarHwnd)
        {
            _taskbarHwnd = taskbarHwnd;

            try
            {
                var curProcess = Process.GetCurrentProcess();
                var curModule = curProcess.MainModule;
                var mh = GetModuleHandle(curModule.ModuleName);
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, mh, 0);
                Debug.WriteLine("[SystemStartMenuReplacer] Hook installed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemStartMenuReplacer] Failed to install hook: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                Debug.WriteLine("[SystemStartMenuReplacer] Hook removed");
            }
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
                {
                    KBDLLHOOKSTRUCT kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                    // Перехоплюємо Win-key (VK_LWIN або VK_RWIN)
                    if (kbd.vkCode == VK_LWIN || kbd.vkCode == VK_RWIN)
                    {
                        // Спробуємо закрити системне Start Menu якщо воно відкрито
                        CloseSystemStartMenu();

                        // Вимикаємо обробку Win-key в системі (повертаємо 1)
                        // Це запобігає відкриттю системного Start Menu

                        // Потім відкриваємо наше меню
                        _onStartMenuRequested?.Invoke();

                        // Підавляємо системну обробку (повертаємо -1 або 1)
                        return (IntPtr)1;
                    }
                }
            }
            catch { }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        void CloseSystemStartMenu()
        {
            try
            {
                // Пошукаємо системне Start Menu по різним можливим класам
                var startMenuClasses = new[] 
                { 
                    "Windows.UI.Core.CoreWindow",  // Win10 Start Menu
                    "ApplicationFrameWindow",      // УШ контейнер
                    "MetroWindow",                 // Старший стиль
                    "Shell_TrayWnd",              // Таскбар
                };

                foreach (var className in startMenuClasses)
                {
                    IntPtr hWnd = FindWindow(className, null);
                    if (hWnd != IntPtr.Zero && IsWindowVisible(hWnd))
                    {
                        // Посилаємо ESC щоб закрити меню
                        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero);
                        break;
                    }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
