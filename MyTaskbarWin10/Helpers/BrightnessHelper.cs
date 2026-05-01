using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MyTaskbar.Helpers
{
    public static class BrightnessHelper
    {
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor, uint dwPhysicalMonitorArraySize,
            [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetMonitorBrightness(
            IntPtr hPhysicalMonitor, out uint pdwMinimumBrightness,
            out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool SetMonitorBrightness(
            IntPtr hPhysicalMonitor, uint dwNewBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool DestroyPhysicalMonitors(
            uint dwPhysicalMonitorArraySize,
            [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(
            IntPtr hdc, IntPtr lprcClip,
            MonitorEnumProc lpfnEnum, IntPtr dwData);

        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
            ref RECT lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        // ─────────────────────────────────────────────────────────────────────────

        public class MonitorInfo
        {
            public int Index { get; set; }
            public string Name { get; set; }
            public int Brightness { get; set; }   // 0-100
            public bool IsBuiltIn { get; set; }
            public IntPtr HMonitor { get; set; }   // для DDC/CI; IntPtr.Zero у встроенного
        }

        // ── Перечисление мониторов ────────────────────────────────────────────────

        public static List<MonitorInfo> GetMonitors()
        {
            var result = new List<MonitorInfo>();

            // 1. Встроенный дисплей через WMI
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    @"root\WMI", "SELECT * FROM WmiMonitorBrightness"))
                using (var col = searcher.Get())
                {
                    int idx = 0;
                    foreach (ManagementObject obj in col)
                    {
                        int cur = Convert.ToInt32(obj["CurrentBrightness"]);
                        result.Add(new MonitorInfo
                        {
                            Index = idx++,
                            Name = "Built-in display",
                            Brightness = cur,
                            IsBuiltIn = true,
                            HMonitor = IntPtr.Zero
                        });
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[Brightness] WMI enum: {ex.Message}"); }

            // 2. Внешние мониторы через DDC/CI
            var hmons = EnumHMonitors();
            int extIdx = result.Count;

            foreach (var hmon in hmons)
            {
                try
                {
                    if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint count) || count == 0)
                        continue;
                    var physMons = new PHYSICAL_MONITOR[count];
                    if (!GetPhysicalMonitorsFromHMONITOR(hmon, count, physMons)) continue;

                    foreach (var pm in physMons)
                    {
                        try
                        {
                            if (GetMonitorBrightness(pm.hPhysicalMonitor,
                                out _, out uint cur, out uint max) && max > 0)
                            {
                                int pct = (int)Math.Round(cur * 100.0 / max);
                                string name = string.IsNullOrWhiteSpace(pm.szPhysicalMonitorDescription)
                                    ? $"Monitor {extIdx + 1}"
                                    : pm.szPhysicalMonitorDescription;
                                result.Add(new MonitorInfo
                                {
                                    Index = extIdx++,
                                    Name = name,
                                    Brightness = pct,
                                    IsBuiltIn = false,
                                    HMonitor = hmon
                                });
                            }
                        }
                        catch { }
                    }
                    DestroyPhysicalMonitors(count, physMons);
                }
                catch (Exception ex) { Debug.WriteLine($"[Brightness] DDC/CI enum: {ex.Message}"); }
            }

            return result;
        }

        public static List<IntPtr> EnumHMonitors()
        {
            var list = new List<IntPtr>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hm, IntPtr hdc, ref RECT r, IntPtr d) => { list.Add(hm); return true; },
                IntPtr.Zero);
            return list;
        }

        // ── Чтение яркости ────────────────────────────────────────────────────────

        /// <summary>Яркость встроенного монитора (0-100). -1 если недоступно.</summary>
        public static int GetBuiltInBrightness()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    @"root\WMI", "SELECT * FROM WmiMonitorBrightness"))
                using (var col = searcher.Get())
                {
                    foreach (ManagementObject obj in col)
                        return Convert.ToInt32(obj["CurrentBrightness"]);
                }
            }
            catch { }
            return -1;
        }

        public static Task<int> GetBrightnessAsync() =>
            _ = Task.Run(() => GetBuiltInBrightness());

        // ── Установка яркости ─────────────────────────────────────────────────────

        /// <summary>Установить яркость встроенного монитора через WMI (0-100).</summary>
        public static bool SetBuiltInBrightness(int value)
        {
            value = Clamp(value);
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    @"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
                using (var col = searcher.Get())
                {
                    foreach (ManagementObject obj in col)
                    {
                        obj.InvokeMethod("WmiSetBrightness",
                            new object[] { (uint)1, (byte)value });
                        return true;
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[Brightness] WMI Set: {ex.Message}"); }

            return SetBrightnessViaPowerShell(value);
        }

        /// <summary>Установить яркость внешнего монитора через DDC/CI (0-100).</summary>
        public static bool SetExternalBrightness(IntPtr hMonitor, int value)
        {
            value = Clamp(value);
            if (hMonitor == IntPtr.Zero) return false;
            try
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
                    return false;
                var physMons = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physMons)) return false;

                bool ok = false;
                foreach (var pm in physMons)
                {
                    try
                    {
                        if (GetMonitorBrightness(pm.hPhysicalMonitor,
                            out uint mn, out _, out uint mx) && mx > mn)
                        {
                            uint newVal = (uint)(mn + (mx - mn) * value / 100);
                            ok |= SetMonitorBrightness(pm.hPhysicalMonitor, newVal);
                        }
                    }
                    catch { }
                }
                DestroyPhysicalMonitors(count, physMons);
                return ok;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Brightness] DDC/CI Set: {ex.Message}");
                return false;
            }
        }

        /// <summary>Установить яркость по объекту MonitorInfo (встроенный или внешний).</summary>
        public static bool SetBrightness(MonitorInfo monitor, int value)
        {
            if (monitor.IsBuiltIn) return SetBuiltInBrightness(value);
            return SetExternalBrightness(monitor.HMonitor, value);
        }

        public static Task<bool> SetBuiltInBrightnessAsync(int value) =>
            _ = Task.Run(() => SetBuiltInBrightness(value));

        public static Task<bool> SetBrightnessAsync(MonitorInfo monitor, int value) =>
            _ = Task.Run(() => SetBrightness(monitor, value));

        // ── PowerShell fallback ───────────────────────────────────────────────────

        static bool SetBrightnessViaPowerShell(int value)
        {
            try
            {
                string cmd =
                    $"(Get-WmiObject -Namespace root/WMI -Class WmiMonitorBrightnessMethods)" +
                    $".WmiSetBrightness(1,{value})";
                var psi = new ProcessStartInfo(
                    "powershell.exe",
                    $"-NonInteractive -WindowStyle Hidden -Command \"{cmd}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p?.WaitForExit(3000);
                    return p?.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Brightness] PS Set: {ex.Message}");
                return false;
            }
        }

        static int Clamp(int v) => Math.Max(0, Math.Min(100, v));
    }
}
