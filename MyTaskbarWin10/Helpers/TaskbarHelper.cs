using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MyTaskbar.Helpers
{
    public static class TaskbarHelper
    {
        [DllImport("user32.dll")] private static extern IntPtr FindWindow(string cls, string win);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
        [DllImport("shell32.dll")] private static extern uint SHAppBarMessage(uint msg, ref APPBARDATA data);

        // Перечисление ВСЕХ окон (для поиска Shell_SecondaryTrayWnd)
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public uint cbSize; public IntPtr hWnd;
            public uint uCallbackMessage; public uint uEdge;
            public RECT rc; public int lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        private const uint ABM_SETSTATE = 0x0000000A;
        private const uint ABM_GETSTATE = 0x00000004;
        private const int ABS_AUTOHIDE = 0x01;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private static RECT _savedRect;
        private static bool _hasSavedRect = false;
        private static int _savedState = -1;

        // Список всех скрытых вторичных панелей (для возврата при закрытии)
        private static readonly List<(IntPtr hwnd, RECT rect)> _hiddenSecondary = new List<(IntPtr, RECT)>();

        private static IntPtr Taskbar() => FindWindow("Shell_TrayWnd", null);

        // ────────────────────────────────────────────────────────────────
        //  Найти все вторичные панели задач (Shell_SecondaryTrayWnd)
        // ────────────────────────────────────────────────────────────────

        private static List<IntPtr> FindAllSecondaryTaskbars()
        {
            var result = new List<IntPtr>();
            EnumWindows((hwnd, lp) =>
            {
                try
                {
                    var sb = new System.Text.StringBuilder(64);
                    GetClassName(hwnd, sb, sb.Capacity);
                    if (sb.ToString() == "Shell_SecondaryTrayWnd")
                        result.Add(hwnd);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        // ────────────────────────────────────────────────────────────────
        public static void Hide()
        {
            int screenH = (int)System.Windows.SystemParameters.PrimaryScreenHeight;

            // ── 1. Главная панель ────────────────────────────────────────
            IntPtr hWnd = Taskbar();
            if (hWnd != IntPtr.Zero)
            {
                if (GetWindowRect(hWnd, out RECT r)) { _savedRect = r; _hasSavedRect = true; }

                var abd = MakeABD(hWnd);
                _savedState = (int)SHAppBarMessage(ABM_GETSTATE, ref abd);

                abd.lParam = ABS_AUTOHIDE;
                SHAppBarMessage(ABM_SETSTATE, ref abd);

                SetAutoHideBit("StuckRects3", true);
                SetAutoHideBit("StuckRects2", true);

                ShowWindow(hWnd, SW_HIDE);

                int w = _hasSavedRect ? _savedRect.right - _savedRect.left : 1920;
                int h = _hasSavedRect ? _savedRect.bottom - _savedRect.top : 40;
                MoveWindow(hWnd, 0, screenH + 200, w, h, false);
            }

            // ── 2. Вторичные панели (второй монитор и т.д.) ──────────────
            _hiddenSecondary.Clear();
            foreach (var sec in FindAllSecondaryTaskbars())
            {
                try
                {
                    RECT sr; GetWindowRect(sec, out sr);
                    _hiddenSecondary.Add((sec, sr));
                    ShowWindow(sec, SW_HIDE);
                    // Двигаем за нижний край, чтобы тригерная зона мыши не работала
                    int sw = sr.right - sr.left;
                    int sh2 = sr.bottom - sr.top;
                    MoveWindow(sec, sr.left, screenH + 200, sw, sh2, false);
                }
                catch { }
            }
        }

        // ────────────────────────────────────────────────────────────────
        public static void Show()
        {
            // ── 1. Главная панель ────────────────────────────────────────
            IntPtr hWnd = Taskbar();
            if (hWnd != IntPtr.Zero)
            {
                if (_hasSavedRect)
                    MoveWindow(hWnd, _savedRect.left, _savedRect.top,
                        _savedRect.right - _savedRect.left, _savedRect.bottom - _savedRect.top, true);

                if (_savedState >= 0)
                {
                    var abd = MakeABD(hWnd);
                    abd.lParam = _savedState;
                    SHAppBarMessage(ABM_SETSTATE, ref abd);
                    SetAutoHideBit("StuckRects3", (_savedState & ABS_AUTOHIDE) != 0);
                    SetAutoHideBit("StuckRects2", (_savedState & ABS_AUTOHIDE) != 0);
                }
                ShowWindow(hWnd, SW_SHOW);
            }

            // ── 2. Вторичные панели ──────────────────────────────────────
            foreach (var (sec, rect) in _hiddenSecondary)
            {
                try
                {
                    MoveWindow(sec, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top, true);
                    ShowWindow(sec, SW_SHOW);
                }
                catch { }
            }

            // Ещё раз найдём все вторичные — вдруг появились новые после запуска
            foreach (var sec in FindAllSecondaryTaskbars())
            {
                try { ShowWindow(sec, SW_SHOW); } catch { }
            }
        }

        // ────────────────────────────────────────────────────────────────
        public static bool IsSystemTaskbarVisible()
        {
            IntPtr hWnd = Taskbar();
            if (hWnd == IntPtr.Zero) return false;
            if (!IsWindowVisible(hWnd)) return false;
            int screenH = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
            if (GetWindowRect(hWnd, out RECT r)) return r.top < screenH;
            return true;
        }

        // ────────────────────────────────────────────────────────────────
        private static void SetAutoHideBit(string keyName, bool enable)
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

        private static APPBARDATA MakeABD(IntPtr hWnd) => new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf(typeof(APPBARDATA)),
            hWnd = hWnd
        };
    }
}