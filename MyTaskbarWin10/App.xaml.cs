using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace MyTaskbar
{
    public partial class App : Application
    {
        private static Mutex _instanceMutex;
        private const string MutexName = "Global\\MyTaskbarWin10_SingleInstance";
        private const string TaskName = "MyTaskbarWin10_Autostart";

        protected override void OnStartup(StartupEventArgs e)
        {
            // 1. Один экземпляр
            bool createdNew;
            _instanceMutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                _instanceMutex.Close();
                Shutdown(0);
                return;
            }

            // 2. Сразу прячем системный таскбар через реестр (autohide бит).
            //    Это работает моментально — до любого UI, до WPF окна.
            //    При следующем входе Windows прочитает этот бит и стартует
            //    системный таскбар уже скрытым, поэтому мелькания не будет вообще.
            HideSystemTaskbarImmediate();

            // 3. Task Scheduler — в фоне, не блокируем старт
            _ = Task.Run(() =>
            {
                EnsureTaskScheduler();
                RemoveLegacyRegistryAutostart();
            });

            // 4. Запускаем главное окно
            base.OnStartup(e);

            // [DESKTOP-READY] На медленных ПК Explorer не успевает поднять Progman
            // к моменту старта панели — рабочий стол остаётся чёрным.
            // Ждём Progman в фоне (макс 10 сек) и только потом показываем главное окно.
            _ = Task.Run(() => WaitForProgmanThenShow());
        }

        // Ждём пока Explorer поднимет Progman (рабочий стол готов),
        // затем перезапускаем Explorer чтобы он нарисовал рабочий стол чисто,
        // ждём пока он снова поднимется, и показываем главное окно.
        void WaitForProgmanThenShow()
        {
            const int maxWaitMs = 15000;
            const int pollMs    = 150;

            // Шаг 1: ждём пока Explorer поднимет Shell_TrayWnd (он готов к работе)
            int waited = 0;
            while (waited < maxWaitMs)
            {
                IntPtr tray = FindWindow("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero) break;
                Thread.Sleep(pollMs);
                waited += pollMs;
            }

            // Шаг 2: перезапускаем Explorer — убиваем и сразу стартуем заново.
            // Это гарантирует что рабочий стол нарисуется чисто на любом ПК.
            try
            {
                // Убиваем все процессы explorer.exe
                foreach (var p in Process.GetProcessesByName("explorer"))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                }

                // Небольшая пауза чтобы Windows успела убрать окна
                Thread.Sleep(500);

                // Запускаем explorer.exe заново — без аргументов чтобы он поднял
                // оболочку (Progman, рабочий стол, иконки) нормально.
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = true
                });

                // [FIX-EXPLORER-WINDOW] explorer.exe без аргументов также открывает окно
                // "Этот компьютер" (CabinetWClass). Ждём его появления и сразу закрываем.
                Task.Run(() =>
                {
                    const int maxMs = 5000;
                    int elapsed = 0;
                    while (elapsed < maxMs)
                    {
                        Thread.Sleep(100);
                        elapsed += 100;
                        IntPtr cabinet = FindWindow("CabinetWClass", null);
                        if (cabinet != IntPtr.Zero)
                        {
                            // SW_HIDE = 0, затем PostMessage WM_CLOSE = 0x0010
                            ShowWindow(cabinet, 0);
                            System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                            // Посылаем WM_CLOSE чтобы закрыть окно
                            PostMessage(cabinet, 0x0010, IntPtr.Zero, IntPtr.Zero);
                            break;
                        }
                    }
                });
            }
            catch { }

            // Шаг 3: ждём пока Explorer снова поднимет Progman (рабочий стол готов)
            waited = 0;
            while (waited < maxWaitMs)
            {
                IntPtr progman = FindWindow("Progman", null);
                if (progman != IntPtr.Zero) break;
                Thread.Sleep(pollMs);
                waited += pollMs;
            }

            // Шаг 4: небольшая пауза — даём Explorer дорисовать иконки рабочего стола
            Thread.Sleep(600);

            // Шаг 5: показываем панель
            Dispatcher.Invoke(() =>
            {
                var mainWindow = new MainWindow();
                MainWindow = mainWindow;
                mainWindow.Show();
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            try { _instanceMutex?.Close(); } catch { }
            base.OnExit(e);
        }

        // ─────────────────────────────────────────────────────────────────
        // Мгновенное скрытие через реестр — до создания любого WPF окна.
        // Ставим autohide бит в StuckRects3/StuckRects2, затем прячем
        // Shell_TrayWnd через ShowWindow. Без WaitForInputIdle, без задержек.
        // ─────────────────────────────────────────────────────────────────
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr FindWindow(string cls, string win);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int ShowWindow(IntPtr hwnd, int cmd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        static void HideSystemTaskbarImmediate()
        {
            try
            {
                // Пишем autohide бит — при следующем входе системный таскбар
                // стартует уже скрытым, без мелькания
                SetAutoHideBit("StuckRects3", true);
                SetAutoHideBit("StuckRects2", true);
            }
            catch { }

            try
            {
                // Прячем прямо сейчас если уже запущен
                IntPtr hwnd = FindWindow("Shell_TrayWnd", null);
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, 0); // SW_HIDE
                    // Двигаем за экран чтобы триггерная зона не работала
                    int screenH = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
                    MoveWindow(hwnd, 0, screenH + 200, 1920, 40, false);
                }
            }
            catch { }
        }

        static void SetAutoHideBit(string keyName, bool enable)
        {
            try
            {
                const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\";
                using (var key = Registry.CurrentUser.OpenSubKey(path + keyName, writable: true))
                {
                    if (key == null) return;
                    var data = key.GetValue("Settings") as byte[];
                    if (data == null || data.Length < 9) return;
                    if (enable) data[8] |= 0x01; else data[8] &= 0xFE;
                    key.SetValue("Settings", data);
                }
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────
        // Task Scheduler — выполняется в фоновом потоке
        // ─────────────────────────────────────────────────────────────────
        static void EnsureTaskScheduler()
        {
            try
            {
                string exePath = Assembly.GetExecutingAssembly().Location;
                string userName = Environment.UserName;

                if (TaskExists(TaskName))
                {
                    if (TaskPathMatches(TaskName, exePath)) return;
                    DeleteTask(TaskName);
                }

                string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>MyTaskbar autostart</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{userName}</UserId>
      <Delay>PT5S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>{exePath}</Command></Exec>
  </Actions>
</Task>";

                string tmpXml = Path.Combine(Path.GetTempPath(), "mytaskbar_task.xml");
                File.WriteAllText(tmpXml, xml, System.Text.Encoding.Unicode);

                var psi = new ProcessStartInfo("schtasks.exe",
                    $"/Create /TN \"{TaskName}\" /XML \"{tmpXml}\" /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                    p.WaitForExit(5000);

                try { File.Delete(tmpXml); } catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MyTaskbar] TaskScheduler failed: {ex.Message}");
                FallbackRegistryAutostart();
            }
        }

        static bool TaskExists(string taskName)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{taskName}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi)) { p.WaitForExit(3000); return p.ExitCode == 0; }
            }
            catch { return false; }
        }

        static bool TaskPathMatches(string taskName, string exePath)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{taskName}\" /XML")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    return output.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        static void DeleteTask(string taskName)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{taskName}\" /F")
                { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) p.WaitForExit(3000);
            }
            catch { }
        }

        static void FallbackRegistryAutostart()
        {
            try
            {
                string exePath = Assembly.GetExecutingAssembly().Location;
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true))
                {
                    if (key == null) return;
                    var cur = key.GetValue("MyTaskbarWin10") as string;
                    if (!string.Equals(cur, exePath, StringComparison.OrdinalIgnoreCase))
                        key.SetValue("MyTaskbarWin10", exePath);
                }
            }
            catch { }
        }

        static void RemoveLegacyRegistryAutostart()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true))
                    key?.DeleteValue("MyTaskbarWin10", false);
            }
            catch { }
        }
    }
}
