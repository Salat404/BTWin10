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
// MainWindow.ContextMenu.cs — Context menu, pin/unpin logic
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // CONTEXT MENU [STAB-13]
        // ═════════════════════════════════════════════════════════════════════

        // Builds a MenuItem whose header is a two-column Grid:
        //   col0 (28px fixed) — optional icon
        //   col1 (auto)       — label text
        // This keeps every item's text perfectly left-aligned regardless of
        // whether it has an icon, exactly like the Win10 taskbar jump list.
        MenuItem MakeMenuItem(string label, BitmapSource icon = null, bool bold = false)
        {
            var grid = new Grid { Margin = new Thickness(icon != null ? 3 : 2, 0, 0, 0) };

            if (icon != null)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var img = new System.Windows.Controls.Image
                {
                    Source = icon,
                    Width = 16,
                    Height = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    SnapsToDevicePixels = true
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Grid.SetColumn(img, 0);
                grid.Children.Add(img);

                var tb = new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
                };
                Grid.SetColumn(tb, 1);
                grid.Children.Add(tb);
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var tb = new TextBlock
                {
                    Text = label,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
                };
                Grid.SetColumn(tb, 0);
                grid.Children.Add(tb);
            }

            return new MenuItem { Header = grid, Style = TryFindResource("DarkMenuItem") as Style };
        }

        void ShowContextMenu(Button btn, AppGroup group)
        {
            try
            {
                HidePreview();
                var menu = new ContextMenu { Style = TryFindResource("DarkContextMenu") as Style };
                bool hasWindows = group.Hwnds.Count > 0;

                // ── App name + icon (always at top, like Win10) ───────────────
                string launchPath = group.LaunchPath;
                if (string.IsNullOrEmpty(launchPath) && hasWindows)
                {
                    try
                    {
                        GetWindowThreadProcessId(group.Hwnds[0], out uint pid);
                        if (pid != 0) launchPath = System.Diagnostics.Process.GetProcessById((int)pid).MainModule?.FileName ?? "";
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(launchPath))
                {
                    string appLabel = System.IO.Path.GetFileNameWithoutExtension(launchPath);
                    if (string.IsNullOrEmpty(appLabel)) appLabel = group.ExeName ?? "";
                    if (appLabel.Length > 45) appLabel = appLabel.Substring(0, 45) + "…";

                    string lp = launchPath;
                    var miLaunch = MakeMenuItem(appLabel, group.Icon, bold: true);
                    miLaunch.Click += (s, e) =>
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(lp) { UseShellExecute = true }); }
                        catch { }
                    };
                    menu.Items.Add(miLaunch);
                    menu.Items.Add(new Separator { Style = TryFindResource("DarkMenuSeparator") as Style });
                }

                // ── Pin / Unpin ───────────────────────────────────────────────
                if (group.IsPinned)
                {
                    var miUnpin = MakeMenuItem("Unpin from taskbar");
                    miUnpin.Click += (s, e) => { try { UnpinGroup(group); } catch { } };
                    menu.Items.Add(miUnpin);
                }
                else if (!string.IsNullOrEmpty(group.LaunchPath) || hasWindows)
                {
                    var miPin = MakeMenuItem("Pin to taskbar");
                    miPin.Click += (s, e) => { try { PinGroup(group); } catch { } };
                    menu.Items.Add(miPin);
                }

                // ── Close (only when windows are open) ────────────────────────
                if (hasWindows)
                {
                    if (menu.Items.Count > 0)
                        menu.Items.Add(new Separator { Style = TryFindResource("DarkMenuSeparator") as Style });

                    if (group.Hwnds.Count == 1)
                    {
                        IntPtr hwndSingle = group.Hwnds[0];
                        var miClose = MakeMenuItem("Close window");
                        miClose.Click += (s, e) =>
                        {
                            try { if (IsWindow(hwndSingle)) PostMessage(hwndSingle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
                            catch { }
                        };
                        menu.Items.Add(miClose);
                    }
                    else
                    {
                        foreach (var hwnd in group.Hwnds.ToList())
                        {
                            IntPtr cap = hwnd;
                            string t = SafeGetWindowText(hwnd);
                            if (string.IsNullOrEmpty(t)) t = group.ExeName;
                            string header = t.Length > 40 ? t.Substring(0, 40) + "…" : t;
                            var miOne = MakeMenuItem("Close \"" + header + "\"");
                            miOne.Click += (s, e) =>
                            {
                                try { if (IsWindow(cap)) PostMessage(cap, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
                                catch { }
                            };
                            menu.Items.Add(miOne);
                        }
                        menu.Items.Add(new Separator { Style = TryFindResource("DarkMenuSeparator") as Style });
                        var miCloseAll = MakeMenuItem("Close all windows");
                        miCloseAll.Click += (s, e) =>
                        {
                            foreach (var h in group.Hwnds.ToList())
                                try { if (IsWindow(h)) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
                                catch { }
                        };
                        menu.Items.Add(miCloseAll);
                    }
                }

                if (menu.Items.Count == 0) return;
                menu.PlacementTarget = btn; menu.Placement = PlacementMode.Top; menu.IsOpen = true;
            }
            catch { }
        }

        string ResolveExePath(AppGroup group)
        {
            if (!string.IsNullOrEmpty(group.LaunchPath) && File.Exists(group.LaunchPath))
            { string bn = IOPath.GetFileNameWithoutExtension(group.LaunchPath); if (!HelperExeNames.Contains(bn)) return group.LaunchPath; }
            if (group.Hwnds.Count > 0)
            {
                try
                {
                    GetWindowThreadProcessId(group.Hwnds[0], out uint pid);
                    if (pid != 0) { string path = Process.GetProcessById((int)pid).MainModule?.FileName ?? ""; if (!string.IsNullOrEmpty(path) && File.Exists(path)) { string bn = IOPath.GetFileNameWithoutExtension(path); if (!HelperExeNames.Contains(bn)) return path; } }
                }
                catch { }
            }
            string found = IconHelper.FindExeByName(group.ExeName);
            if (!string.IsNullOrEmpty(found) && File.Exists(found)) return found;
            return null;
        }

        void PinGroup(AppGroup group)
        {
            try
            {
                string exePath = ResolveExePath(group);
                if (string.IsNullOrEmpty(exePath)) { Debug.WriteLine($"[MyTaskbar] PinGroup: path not found for '{group.ExeName}'"); return; }
                group.IsPinned = true; group.LaunchPath = exePath;
                var apps = PinnedAppsManager.Load() ?? new List<PinnedApp>();
                bool already = apps.Any(a => string.Equals(IOPath.GetFileNameWithoutExtension(a?.Path ?? ""), group.ExeName, StringComparison.OrdinalIgnoreCase));
                if (!already) { apps.Add(new PinnedApp { Name = group.ExeName, Path = exePath, Tooltip = group.LastTitle }); group.PinOrder = apps.Count - 1; }
                PinnedAppsManager.Save(apps); SortButtonsByPinOrder();
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] PinGroup: {ex.Message}"); }
        }

        void UnpinGroup(AppGroup group)
        {
            try
            {
                group.IsPinned = false; group.LaunchPath = null; group.PinOrder = int.MaxValue;
                var apps = PinnedAppsManager.Load() ?? new List<PinnedApp>();
                apps.RemoveAll(a => string.Equals(IOPath.GetFileNameWithoutExtension(a?.Path ?? ""), group.ExeName, StringComparison.OrdinalIgnoreCase));
                PinnedAppsManager.Save(apps);
                for (int i = 0; i < apps.Count; i++) { string en = IOPath.GetFileNameWithoutExtension(apps[i]?.Path ?? "")?.ToLowerInvariant() ?? ""; if (_groups.TryGetValue(en, out var g)) g.PinOrder = i; }
                if (group.Hwnds.Count == 0)
                {
                    if (group.IconHash != null) _iconHashToGroupKey.Remove(group.IconHash); // [ICON-HASH]
                    _groups.Remove(group.GroupKey ?? group.ExeName); RemoveButtonAnimated(group.Button, group);
                }
                else SortButtonsByPinOrder();
            }
            catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] UnpinGroup: {ex.Message}"); }
        }

    }
}
