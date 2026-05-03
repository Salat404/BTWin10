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
// MainWindow.AppLists.cs — Process filter lists, UWP name resolving
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // СПИСКИ ПРОЦЕССОВ [FIX-1]
        // ═════════════════════════════════════════════════════════════════════
        static readonly HashSet<string> IgnoredProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "mytaskbar","textinputhost","shellexperiencehost","startmenuexperiencehost",
    "searchhost","lockapp","dwm","csrss","smss","wininit","services",
    "lsass","svchost","fontdrvhost","sihost","ctfmon",
    "microsoft.media.player","zunemusic",   // ← добавить
};

        static readonly HashSet<string> ForceShowProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "taskmgr","regedit","mmc","eventvwr",
            "devmgmt","diskmgmt","perfmon","resmon","cmd","powershell","windowsterminal",
            "wt","calc","notepad","calculator","calculatorapp","systemsettings","mspaint","snippingtool","explorer",
            "hxoutlook","hxcalendarappimm","winstore","photos","video.ui","maps",
            "microsoft.bingweather","windowscamera","xboxapp","xboxgamingoverlay",
            "minecraftlauncher","minecraft","javaw",
        };

        // ═════════════════════════════════════════════════════════════════════
        // UWP РЕЗОЛВИНГ [FIX-2]
        // ═════════════════════════════════════════════════════════════════════
        static readonly (string[] Titles, string AppName)[] UwpTitleMap =
        {
            (new[]{"settings","параметры","параметры windows","system settings","windows settings"}, "systemsettings"),
            (new[]{"calculator","калькулятор"}, "calculator"),
            (new[]{"mail","почта"}, "hxoutlook"),
            (new[]{"calendar","календарь"}, "hxcalendarappimm"),
            (new[]{"microsoft store","магазин microsoft","магазин","store"}, "winstore"),
            (new[]{"photos","фотографии","фото"}, "photos"),
            (new[]{"movies & tv","кино и тв","фильмы"}, "video.ui"),
            (new[]{"maps","карты"}, "maps"),
            (new[]{"weather","msn weather","погода","msn погода"}, "microsoft.bingweather"),
            (new[]{"camera","камера"}, "windowscamera"),
            (new[]{"xbox"}, "xboxapp"),
            (new[]{"notepad","блокнот"}, "notepad"),
        };

        string ResolveUwpAppName(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;
            string t = title.Trim().ToLowerInvariant();
            if (t.Length == 0) return null;
            foreach (var (titles, appName) in UwpTitleMap)
                foreach (var c in titles)
                {
                    if (t == c
                        || t.StartsWith(c + " — ")
                        || t.StartsWith(c + " - ")
                        || t.StartsWith(c + " | ")
                        || t.StartsWith(c + ": "))
                        return appName;
                }
            if (t.EndsWith(" — photos") || t.EndsWith(" - photos")
                || t.EndsWith(" — фотографии") || t.EndsWith(" - фотографии"))
                return "photos";
            if (t == "microsoft store" || t == "магазин microsoft" || t == "магазин" || t == "store")
                return "winstore";
            return null;
        }

        bool IsUwpAppName(string n)
        {
            switch (n?.ToLowerInvariant())
            {
                case "systemsettings":
                case "calculator":
                case "calculatorapp":   // [FIX-GHOST-v3] прямой процесс калькулятора
                case "hxoutlook":
                case "hxcalendarappimm":
                case "winstore":
                case "winstore.app":
                case "photos":
                case "video.ui":
                case "maps":
                case "microsoft.bingweather":
                case "windowscamera":
                case "xboxapp":
                case "xboxgamingoverlay": return true;
                default: return false;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // ЗАКРЫТИЕ СИСТЕМНОГО ПУСКА [FIX-3]
        // ═════════════════════════════════════════════════════════════════════
        void TryCloseSystemStartMenu()
        {
            try
            {
                IntPtr h = FindWindow("Windows.UI.Core.CoreWindow", "Пуск");
                if (h == IntPtr.Zero) h = FindWindow("Windows.UI.Core.CoreWindow", "Start");
                if (h != IntPtr.Zero && IsWindowVisible(h))
                {
                    // [STAB-1] SafeSendMessage вместо SendMessage
                    SafeSendMessage(h, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero);
                    return;
                }
                h = FindWindow("Microsoft.UI.Content.DesktopChildSiteBridge", null);
                if (h != IntPtr.Zero && IsWindowVisible(h))
                    SafeSendMessage(h, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero);
            }
            catch { }
        }

        // [FIX-TASKMGR-STARTMENU] Отложенное закрытие системного Пуска.
        // Когда диспетчер задач в фокусе (High IL), Shell открывает Пуск через
        // внутренний IPC-канал с задержкой ~80-150мс после Win-key — в обход
        // LowLevel keyboard hook. Вызов TryCloseSystemStartMenu() до ShowMenu()
        // не помогает: Пуск ещё не открылся. Ждём 180мс и закрываем повторно.
        void TryCloseSystemStartMenuDelayed(int delayMs = 180)
        {
            var t = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(delayMs)
            };
            t.Tick += (s, e) =>
            {
                t.Stop();
                TryCloseSystemStartMenu();
            };
            t.Start();
        }

        // Проверяет, является ли foreground-окно диспетчером задач.
        // Определяем по window class — надёжнее имени процесса, не требует
        // прав администратора и работает для любой локализации Windows.
        bool IsTaskmgrForeground()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                var sb = new StringBuilder(256);
                GetWindowClassName(fg, sb, sb.Capacity);
                string cls = sb.ToString();
                // TaskManagerWindow  — основное окно taskmgr на Win10/Win11
                // TaskMgrRebar       — дочерний rebar Win10 taskmgr (на всякий случай)
                return cls == "TaskManagerWindow" || cls == "TaskMgrRebar";
            }
            catch { return false; }
        }

    }
}
