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
// MainWindow.TrayIcon.cs — System tray icon (NotifyIcon)
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // PINNED APPS / SHORTCUTS
        // ═════════════════════════════════════════════════════════════════════
        void LoadPinnedApps()
        {
            // Обновляем устаревшие пути для versioned-приложений (Discord, Slack…)
            // до загрузки, чтобы Load() сразу получил актуальные пути.
            PinnedAppsManager.RefreshVersionedPaths();

            List<PinnedApp> apps;
            try { apps = PinnedAppsManager.Load() ?? new List<PinnedApp>(); }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LoadPinnedApps: {ex.Message}"); return; }
            for (int i = 0; i < apps.Count; i++)
            {
                var app = apps[i];
                try
                {
                    if (string.IsNullOrWhiteSpace(app?.Path)) continue;
                    string fullPath = app.Path;
                    if (!IOPath.IsPathRooted(fullPath)) fullPath = PinnedAppsManager.FindApp(app.Path) ?? app.Path;
                    string exeName = IOPath.GetFileNameWithoutExtension(fullPath)?.ToLowerInvariant() ?? "";
                    if (string.IsNullOrEmpty(exeName)) continue;
                    if (HelperExeNames.Contains(exeName)) continue;
                    string tooltip = string.IsNullOrWhiteSpace(app.Tooltip) ? (app.Name ?? exeName) : app.Tooltip;
                    var group = GetOrCreateGroup(exeName, GetCachedIcon(fullPath), tooltip);
                    group.IsPinned = true; group.LaunchPath = fullPath; group.PinOrder = i;
                }
                catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LoadPinnedApps item: {ex.Message}"); }
            }
            SortButtonsByPinOrder();
        }

        void LoadShortcuts()
        {
            try
            {
                if (!Directory.Exists(ShortcutsFolder)) return;
                foreach (var lnkPath in Directory.GetFiles(ShortcutsFolder, "*.lnk"))
                {
                    try
                    {
                        string tooltip = IOPath.GetFileNameWithoutExtension(lnkPath) ?? "";
                        string exePath = IconHelper.ResolveShortcut(lnkPath);
                        string exeName = !string.IsNullOrEmpty(exePath)
                            ? (IOPath.GetFileNameWithoutExtension(exePath)?.ToLowerInvariant() ?? "")
                            : tooltip.ToLowerInvariant();
                        if (string.IsNullOrEmpty(exeName)) continue;
                        var group = GetOrCreateGroup(exeName, GetCachedIcon(lnkPath), tooltip);
                        group.IsPinned = true; group.LaunchPath = lnkPath;
                    }
                    catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LoadShortcuts item: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] LoadShortcuts: {ex.Message}"); }
        }

        void SortButtonsByPinOrder()
        {
            try
            {
                var pinned = _groups.Values.Where(g => g.IsPinned && g.Button != null && AppIcons.Children.Contains(g.Button)).OrderBy(g => g.PinOrder).Select(g => g.Button).ToList();
                var unpinned = _groups.Values.Where(g => !g.IsPinned && g.Button != null && AppIcons.Children.Contains(g.Button)).Select(g => g.Button).ToList();
                var all = pinned.Concat(unpinned).ToList();
                foreach (var b in all) AppIcons.Children.Remove(b);
                foreach (var b in all) AppIcons.Children.Add(b);
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] SortButtonsByPinOrder: {ex.Message}"); }
        }

        // [DRAG-REORDER] Считывает текущий визуальный порядок кнопок в AppIcons,
        // обновляет PinOrder у закреплённых групп и сохраняет в pinned.xml.
    }
}
