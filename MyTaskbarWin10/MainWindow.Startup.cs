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
// MainWindow.Startup.cs — Acrylic background, Bluetooth, Power/Sleep events
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // ACRYLIC / BLUR [FIX-4]
        // ═════════════════════════════════════════════════════════════════════
        void ApplyAcrylicBackground()
        {
            if (Environment.OSVersion.Version.Major < 10)
            {
                if (RootBorder != null)
                    RootBorder.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1A, 0x1A, 0x2A));
                return;
            }
            try
            {
                if (IsWin10_1903Plus) AcrylicHelper.EnableAcrylic(this, 0x701A1A2E);
                else AcrylicHelper.EnableBlur(this, 0x70202030);
            }
            catch
            {
                try { AcrylicHelper.EnableBlur(this, 0x70202030); }
                catch
                {
                    if (RootBorder != null)
                        RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x70, 0x1A, 0x1A, 0x2A));
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // BLUETOOTH
        // ═════════════════════════════════════════════════════════════════════
        void StartBluetoothTracker()
        {
            PollBluetoothState();
            _bluetoothTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _bluetoothTimer.Tick += (s, e) => SafeRun(PollBluetoothState, "PollBluetoothState");
            _bluetoothTimer.Start();
        }

        void PollBluetoothState()
        {
            // [STAB-6] Флаг реентрантности
            if (System.Threading.Interlocked.CompareExchange(ref _btBusyInt, 1, 0) != 0) return;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    int state = GetBluetoothStateInt();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (state == _btLastStateInt) return;
                            _btLastStateInt = state;
                            ApplyBluetoothState(state);
                        }
                        catch { }
                        finally { System.Threading.Interlocked.Exchange(ref _btBusyInt, 0); }
                    }));
                }
                catch { System.Threading.Interlocked.Exchange(ref _btBusyInt, 0); }
            });
        }

        static int GetBluetoothStateInt()
        {
            try
            {
                var p = new BLUETOOTH_FIND_RADIO_PARAMS
                { dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS)) };
                IntPtr hRadio;
                IntPtr hFind = BluetoothFindFirstRadio(ref p, out hRadio);
                if (hFind != IntPtr.Zero)
                { CloseHandle(hRadio); BluetoothFindRadioClose(hFind); return 1; }
                bool hasAdapter = false;
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\bthserv"))
                    if (key != null) hasAdapter = true;
                if (!hasAdapter)
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHUSB"))
                        if (key != null) hasAdapter = true;
                if (!hasAdapter)
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHMINI"))
                        if (key != null) hasAdapter = true;
                return hasAdapter ? 0 : -1;
            }
            catch { return -1; }
        }

        // [STAB-10] ApplyBluetoothState: Dispatcher.CheckAccess + IsLoaded
        void ApplyBluetoothState(int state)
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                { Dispatcher.BeginInvoke(new Action(() => ApplyBluetoothState(state))); return; }
                if (!IsLoaded || BluetoothButton == null || BluetoothIcon == null) return;
                if (state == 1)
                { BluetoothIcon.Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x9D, 0xFF)); BluetoothButton.ToolTip = "Bluetooth: on"; }
                else if (state == 0)
                { BluetoothIcon.Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)); BluetoothButton.ToolTip = "Bluetooth: off"; }
                else
                { BluetoothIcon.Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0xAA, 0xAA, 0xAA)); BluetoothButton.ToolTip = "Bluetooth: adapter not found"; }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] ApplyBluetoothState: {ex.Message}"); }
        }

        void BluetoothButton_Click(object sender, RoutedEventArgs e)
        {
            try { MarkOwnActivity(); Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] BluetoothButton_Click: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // ВЫХОД ИЗ СНА [FIX-8] + БЛОКИРОВКА ЭКРАНА [STAB-5]
        // ═════════════════════════════════════════════════════════════════════
        void HookPowerEvents()
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (source != null)
            {
                source.AddHook(PowerBroadcastHook);
                source.AddHook(SessionChangeHook); // [STAB-5]
                source.AddHook(NcHitTestHook);     // [FIX-DRAG]
                source.AddHook(ShellAttentionHook); // [ATTENTION]
                source.AddHook(DpiChangedHook);     // [DPI-AUTO]

                // [DPI-AUTO] Читаем начальный DPI при старте
                _currentDpiScale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            }
            // [STAB-5] Регистрация WTS-уведомлений
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] WTS register: {ex.Message}"); }
        }

        // [FIX-DRAG] Пропускаем мышиные события (включая drag&drop) сквозь
        // пустые зоны панели. Кнопки и интерактивные элементы получают клики нормально.
        IntPtr NcHitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_NCHITTEST) return IntPtr.Zero;
            try
            {
                // Экранные координаты из lParam
                int sx = unchecked((short)(lParam.ToInt32() & 0xFFFF));
                int sy = unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));

                // Переводим в координаты WPF с учётом DPI
                Point ptWpf;
                try { ptWpf = PointFromScreen(new Point(sx, sy)); }
                catch { return IntPtr.Zero; }

                // Hit-test: есть ли интерактивный элемент под курсором?
                var hit = VisualTreeHelper.HitTest(this, ptWpf);
                if (hit == null)
                {
                    handled = true;
                    return new IntPtr(HTTRANSPARENT);
                }

                // Если попали в пустой фон (само окно, корневой Border или StackPanel) — пропускаем
                var visual = hit.VisualHit;
                bool isBackground =
                    visual is Window ||
                    (visual is Border brd && (brd.Name == "RootBorder" || string.IsNullOrEmpty(brd.Name))) ||
                    (visual is StackPanel sp && (sp.Name == "MainPanel" || string.IsNullOrEmpty(sp.Name)));

                if (isBackground)
                {
                    handled = true;
                    return new IntPtr(HTTRANSPARENT);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] NcHitTestHook: {ex.Message}"); }
            return IntPtr.Zero;
        }

        IntPtr PowerBroadcastHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_POWERBROADCAST)
            {
                int evt = wParam.ToInt32();
                if (evt == PBT_APMRESUMEAUTOMATIC || evt == PBT_APMRESUMESUSPEND)
                {
                    Debug.WriteLine($"[MyTaskbar] PowerResume evt=0x{evt:X}");
                    Dispatcher.BeginInvoke(new Action(OnSystemResume));
                }
            }
            return IntPtr.Zero;
        }

        // [STAB-5] Блокировка экрана / смена пользователя = как sleep
        IntPtr SessionChangeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_WTSSESSION_CHANGE)
            {
                int evt = wParam.ToInt32();
                if (evt == WTS_SESSION_LOCK || evt == WTS_REMOTE_DISCONNECT || evt == WTS_CONSOLE_DISCONNECT)
                {
                    Debug.WriteLine($"[MyTaskbar] WTS session event=0x{evt:X}");
                    Dispatcher.BeginInvoke(new Action(OnSystemResume));
                }
            }
            return IntPtr.Zero;
        }

        void OnSystemResume()
        {
            // [FIX-RESUME-INSTANT] Не удаляем кнопки при пробуждении — как в оригинальном Win10.
            // Старый подход "удалить всё -> заблокировать 8 сек -> пересоздать" давал:
            //   - пустую панель на 8 секунд
            //   - дубликаты если блок снимался раньше чем окна успевали зарегистрироваться
            //
            // Новый подход: чистим кеши, снимаем блок сразу, запускаем UpdateWindowGroups.
            // UpdateWindowGroupsCore сам сверит живые окна с группами:
            //   - окна пережившие сон — останутся, кнопки не мигают
            //   - окна закрывшиеся во сне — уберутся через обычную логику удаления
            //   - новые окна — добавятся как обычно
            _pidNameCache.Clear();
            _pidPathCache.Clear();
            _pendingNewGroups.Clear();
            _resumingFromSleep = false;

            _resumeBlockTimer?.Stop();
            _resumeBlockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _resumeBlockTimer.Tick += (s, e) =>
            {
                _resumeBlockTimer.Stop();
                UpdateWindowGroups();
                Debug.WriteLine("[MyTaskbar] Resume: UpdateWindowGroups triggered");
            };
            _resumeBlockTimer.Start();
        }

    }
}
