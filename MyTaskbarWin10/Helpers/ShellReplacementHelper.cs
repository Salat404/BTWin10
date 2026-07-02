using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MyTaskbar.Helpers
{
    /// <summary>
    /// Полнозначная замена Windows shell (explorer.exe как shell).
    /// MyTaskbar становится главным shell-приложением, обрабатывает:
    /// - Рабочий стол (Progman)
    /// - Панель (меняет встроенную)
    /// - Файловый менеджер (CabinetWClass) остаётся от explorer.exe
    /// 
    /// ПЛАН:
    /// 1. Убиваем explorer.exe на startup (кроме рабочего стола)
    /// 2. Перезапускаем его ТОЛЬКО для Progman (рабочий стол) + File Explorer
    /// 3. Регистрируем MyTaskbar как shell в реестре
    /// 4. Перехватываем Win-key для кастомного Start Menu
    /// 5. При выходе MyTaskbar — восстанавливаем explorer.exe как shell
    /// </summary>
    public sealed class ShellReplacementHelper : IDisposable
    {
        // P/Invoke
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern bool SetShellWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
        [DllImport("kernel32.dll")]
        static extern bool CreateProcess(string lpApplicationName, string lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
            uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
            IntPtr lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        // Константы
        const uint PROCESS_TERMINATE = 0x0001;
        const uint PROCESS_QUERY_INFORMATION = 0x0400;
        const uint CREATE_NO_WINDOW = 0x08000000;
        const uint WM_CLOSE = 0x0010;

        // Состояние
        private bool _isShellInstalled = false;
        private bool _originalShellRestored = false;
        private IntPtr _myTaskbarHwnd = IntPtr.Zero;

        /// <summary>
        /// Инициализирует полнозначный shell-replacement.
        /// Вызывать из App.OnStartup ДО создания главного окна.
        /// </summary>
        public void Initialize(IntPtr myTaskbarHwnd)
        {
            _myTaskbarHwnd = myTaskbarHwnd;

            try
            {
                // Шаг 1: Убиваем explorer.exe (но оставляем окна рабочего стола и файлового менеджера)
                KillExplorerButKeepWindows();

                // Шаг 2: Перезапускаем explorer ТОЛЬКО для Progman (рабочий стол)
                RestartExplorerForDesktop();

                // Шаг 3: Регистрируем MyTaskbar как shell в реестре
                RegisterAsShell();

                // Шаг 4: Устанавливаем наше окно как Shell Window
                // (это делает его главным окном shell layer, отвечающим за рабочий стол)
                SetAsShellWindow();

                _isShellInstalled = true;
                Debug.WriteLine("[MyTaskbar] Shell replacement initialized successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MyTaskbar] Shell replacement error: {ex.Message}");
            }
        }

        /// <summary>
        /// Убивает все процессы explorer.exe, кроме тех окон которые нужны.
        /// </summary>
        void KillExplorerButKeepWindows()
        {
            try
            {
                // Находим окна перед убийством процесса
                IntPtr progman = FindWindow("Progman", null);
                IntPtr cabinet = FindWindow("CabinetWClass", null);

                // Убиваем все explorer.exe процессы
                foreach (var p in Process.GetProcessesByName("explorer"))
                {
                    try { p.Kill(); p.WaitForExit(2000); }
                    catch { }
                }

                Thread.Sleep(500);
            }
            catch { }
        }

        /// <summary>
        /// Перезапускает explorer.exe ДЛЯ рабочего стола (Progman).
        /// Это необходимо чтобы рабочий стол отображался и работал.
        /// </summary>
        void RestartExplorerForDesktop()
        {
            try
            {
                // Запускаем explorer.exe без параметров — это откроет Progman (рабочий стол)
                // и CabinetWClass (файловый менеджер "Этот компьютер").
                // Файловый менеджер мы потом закроем.
                var psi = new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                Process.Start(psi);

                // Ждём пока Progman поднимется
                int waited = 0;
                while (waited < 10000)
                {
                    IntPtr progman = FindWindow("Progman", null);
                    if (progman != IntPtr.Zero && IsWindowVisible(progman))
                        break;
                    Thread.Sleep(150);
                    waited += 150;
                }

                // Ждём пока появится CabinetWClass и закрываем его
                Task.Run(() =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        Thread.Sleep(100);
                        IntPtr cabinet = FindWindow("CabinetWClass", null);
                        if (cabinet != IntPtr.Zero)
                        {
                            PostMessage(cabinet, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            break;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MyTaskbar] RestartExplorerForDesktop error: {ex.Message}");
            }
        }

        /// <summary>
        /// Регистрирует MyTaskbar как shell в реестре Windows.
        /// При следующем перезагрузке Windows будет запускать MyTaskbar вместо explorer.exe.
        /// </summary>
        void RegisterAsShell()
        {
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;

                // Сохраняем оригинальный shell (explorer.exe) для восстановления позже
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", writable: true))
                {
                    if (key != null)
                    {
                        var curShell = key.GetValue("Shell") as string;
                        if (curShell != exePath)
                        {
                            // Сохраняем оригинальный shell (для восстановления при выходе)
                            if (string.IsNullOrEmpty(curShell) || curShell == "explorer.exe")
                            {
                                // Рабочий ключ для восстановления
                                key.SetValue("ShellBackup", "explorer.exe");
                            }
                            else
                            {
                                key.SetValue("ShellBackup", curShell);
                            }

                            // Устанавливаем MyTaskbar как shell
                            key.SetValue("Shell", exePath);
                            Debug.WriteLine("[MyTaskbar] Registered as shell in registry");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MyTaskbar] RegisterAsShell error: {ex.Message}");
            }
        }

        /// <summary>
        /// Устанавливает окно MyTaskbar как Shell Window.
        /// Это даёт ему особый статус в Windows.
        /// </summary>
        void SetAsShellWindow()
        {
            try
            {
                if (_myTaskbarHwnd != IntPtr.Zero)
                {
                    SetShellWindow(_myTaskbarHwnd);
                    Debug.WriteLine("[MyTaskbar] Set as shell window");
                }
            }
            catch { }
        }

        /// <summary>
        /// Восстанавливает оригинальный shell (explorer.exe) при выходе MyTaskbar.
        /// </summary>
        public void RestoreOriginalShell()
        {
            if (_originalShellRestored) return;

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", writable: true))
                {
                    if (key != null)
                    {
                        var backup = key.GetValue("ShellBackup") as string;
                        if (!string.IsNullOrEmpty(backup))
                        {
                            key.SetValue("Shell", backup);
                            key.DeleteValue("ShellBackup", false);
                            Debug.WriteLine("[MyTaskbar] Restored original shell");
                        }
                    }
                }

                _originalShellRestored = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MyTaskbar] RestoreOriginalShell error: {ex.Message}");
            }
        }

        /// <summary>
        /// Запускает File Explorer (Проводник).
        /// </summary>
        public static void OpenFileExplorer(string path = null)
        {
            try
            {
                string args = string.IsNullOrEmpty(path) ? "" : $"\"{path}\"";
                Process.Start("explorer.exe", args);
            }
            catch { }
        }

        public void Dispose()
        {
            RestoreOriginalShell();
        }
    }
}
