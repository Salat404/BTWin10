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
// MainWindow.Icons.cs — Icon loading — full hierarchy
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // ИКОНКИ — ПОЛНАЯ ИЕРАРХИЯ [FIX-5]
        // ═════════════════════════════════════════════════════════════════════
        BitmapSource GetBestIcon(IntPtr hwnd, uint pid, string exePath, int size = 32)
        {
            BitmapSource result = null;

            if (hwnd != IntPtr.Zero && IsWindow(hwnd))
            {
                // [STAB-1] SafeSendMessage вместо SendMessage
                result = TryGetHIcon(SafeSendMessage(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero), size)
                      ?? TryGetHIcon(SafeSendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL2, IntPtr.Zero), size)
                      ?? TryGetHIcon(SafeSendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero), size);
                if (result != null) return result;

                result = TryGetHIcon(GetClassLongPtr(hwnd, GCL_HICON), size)
                      ?? TryGetHIcon(GetClassLongPtr(hwnd, GCL_HICONSM), size);
                if (result != null) return result;
            }

            // [STAB-7] Shell-вызовы только для локальных файлов
            if (!string.IsNullOrEmpty(exePath) && IsLocalPath(exePath))
            {
                result = TryShellItemImage(exePath, size);
                if (result != null) return result;
                result = TrySHGetFileInfo(exePath, size);
                if (result != null) return result;
            }

            if (!string.IsNullOrEmpty(exePath))
            {
                try { result = IconHelper.GetIconFromExe(exePath, size); if (result != null) return result; } catch { }
            }
            if (pid != 0)
            {
                try { result = IconHelper.GetIconFromProcess((int)pid, size); if (result != null) return result; } catch { }
            }
            return null;
        }

        // [STAB-7] Проверка: только локальные диски (не сетевые пути)
        static bool IsLocalPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (path.StartsWith(@"\\")) return false;
                string root = IOPath.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return false;
                var di = new System.IO.DriveInfo(root);
                return di.DriveType == System.IO.DriveType.Fixed
                    || di.DriveType == System.IO.DriveType.Ram;
            }
            catch { return false; }
        }

        static BitmapSource TryGetHIcon(IntPtr hIcon, int targetSize)
        {
            if (hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                if (src == null) return null;
                // [FIX-ICON-DPI] Всегда нормализуем к targetSize x targetSize при 96 dpi.
                // Без этого иконки с нестандартным DPI (72, 120, 144...) рендерятся
                // в неправильных DIU-размерах и выглядят растянутыми / обрезанными.
                BitmapSource result = targetSize > 0 ? ResizeBitmap(src, targetSize, targetSize) : src;
                if (result != null && result.CanFreeze) result.Freeze();
                return result;
            }
            catch { return null; }
        }

        // Вариант для виртуальных путей: shell:AppsFolder\..., ::{GUID} и т.д.
        static BitmapSource TryShellParsingNameImage(string parsingName, int size)
        {
            if (string.IsNullOrEmpty(parsingName)) return null;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out object obj);
                var factory = obj as IShellItemImageFactory;
                if (factory == null) return null;
                var sz = new SIZE { cx = size, cy = size };
                int hr = factory.GetImage(sz, SIIGBF_BIGGERSIZEOK, out IntPtr hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero) return null;
                try
                {
                    var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    if (bmp == null) return null;
                    var result = size > 0 ? ResizeBitmap(bmp, size, size) : bmp;
                    if (result != null && result.CanFreeze) result.Freeze();
                    return result;
                }
                finally { try { DeleteObject(hBitmap); } catch { } }
            }
            catch { return null; }
        }

        static BitmapSource TryShellItemImage(string path, int size)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object obj);
                var factory = obj as IShellItemImageFactory;
                if (factory == null) return null;
                var sz = new SIZE { cx = size, cy = size };
                int hr = factory.GetImage(sz, SIIGBF_BIGGERSIZEOK, out IntPtr hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero) return null;
                try
                {
                    var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    if (bmp == null) return null;
                    var result = size > 0 ? ResizeBitmap(bmp, size, size) : bmp;
                    if (result != null && result.CanFreeze) result.Freeze();
                    return result;
                }
                finally { try { DeleteObject(hBitmap); } catch { } }
            }
            catch { return null; }
        }

        static BitmapSource TrySHGetFileInfo(string path, int size)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var shfi = new SHFILEINFO();
                IntPtr hr = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_ICON | SHGFI_LARGEICON);
                if (hr == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
                try { return TryGetHIcon(shfi.hIcon, size); }
                finally { try { DestroyIcon(shfi.hIcon); } catch { } }
            }
            catch { return null; }
        }

        static BitmapSource ResizeBitmap(BitmapSource src, int w, int h)
        {
            try
            {
                if (src.PixelWidth == w && src.PixelHeight == h) return src;
                var tb = new TransformedBitmap();
                tb.BeginInit();
                tb.Source = src;
                tb.Transform = new ScaleTransform((double)w / src.PixelWidth, (double)h / src.PixelHeight);
                tb.EndInit();
                if (tb.CanFreeze) tb.Freeze();
                return tb;
            }
            catch { return src; }
        }

        // [ICON-HASH] Вычисляем хэш пикселей иконки (16×16 downsample → SHA256 первые 8 байт).
        // Одинаковый хэш = одинаковый аватар профиля = одна кнопка на панели.
        static string ComputeIconHash(BitmapSource src)
        {
            try
            {
                if (src == null) return null;
                BitmapSource conv = src.Format != System.Windows.Media.PixelFormats.Bgra32
                    ? new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0)
                    : src;
                // Уменьшаем до 16×16 для быстрого сравнения
                var tb = new TransformedBitmap();
                tb.BeginInit();
                tb.Source = conv;
                tb.Transform = new ScaleTransform(16.0 / conv.PixelWidth, 16.0 / conv.PixelHeight);
                tb.EndInit();
                byte[] px = new byte[16 * 16 * 4];
                tb.CopyPixels(px, 16 * 4, 0);
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(px);
                    return BitConverter.ToString(hash, 0, 8); // 8 байт = 64 бита, коллизии невероятны
                }
            }
            catch { return null; }
        }

        const int UWP_ICON_SIZE = 48;

        // [STAB-7] Синхронный кэш — только HICON-методы (быстро)
        // Shell-методы вызываются асинхронно через LoadIconAsync
        // [PER-WINDOW-ICON] Для per-window процессов (Chrome и др.) используем
        // ключ по HWND — у каждого профиля своя иконка, полученная через WM_GETICON.
        BitmapSource GetCachedIconSafe(IntPtr hwnd, uint pid, string exePath, string groupKey = null)
        {
            // Для per-window групп (groupKey содержит ":hwnd") кэшируем по hwnd,
            // иначе — по exePath/pid как раньше.
            bool isPerWindow = !string.IsNullOrEmpty(groupKey) && groupKey.Contains(":");
            string key = isPerWindow
                ? $"hwnd:{hwnd.ToString("X")}"
                : (!string.IsNullOrEmpty(exePath) ? exePath : $"pid:{pid}");

            if (_iconCache.TryGetValue(key, out var c)) return c;
            BitmapSource result = null;
            if (hwnd != IntPtr.Zero && IsWindow(hwnd))
            {
                // [PER-WINDOW-ICON] Для браузерных профилей запрашиваем ВСЕ форматы HICON
                // (ICON_BIG → ICON_SMALL2 → ICON_SMALL → class icon).
                // Chrome выставляет иконку профиля именно через WM_GETICON.
                result = TryGetHIcon(SafeSendMessage(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero), 32)
                      ?? TryGetHIcon(SafeSendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL2, IntPtr.Zero), 32)
                      ?? TryGetHIcon(SafeSendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero), 32)
                      ?? TryGetHIcon(GetClassLongPtr(hwnd, GCL_HICON), 32)
                      ?? TryGetHIcon(GetClassLongPtr(hwnd, GCL_HICONSM), 32);
            }
            if (result == null && !string.IsNullOrEmpty(exePath) && IsLocalPath(exePath))
                result = TryShellItemImage(exePath, 32) ?? TrySHGetFileInfo(exePath, 32);
            if (result != null) _iconCache[key] = result;
            return result;
        }

        // [STAB-7] Асинхронная загрузка иконки с таймаутом 500ms
        // [PER-WINDOW-ICON] Для per-window групп (Chrome профили) кэш по hwnd
        void LoadIconAsync(AppGroup g, IntPtr hwnd, uint pid, string exePath)
        {
            if (g == null) return;
            bool isPerWindow = !string.IsNullOrEmpty(g.GroupKey) && g.GroupKey.Contains(":");
            // Для per-window: кэш-ключ по hwnd. Если уже есть в кэше — сразу применяем.
            if (isPerWindow && hwnd != IntPtr.Zero)
            {
                string hwndKey = $"hwnd:{hwnd.ToString("X")}";
                if (_iconCache.TryGetValue(hwndKey, out var cached) && cached != null)
                {
                    if (g.Icon == null && g.Button != null)
                    { g.Icon = cached; UpdateButtonIcon(g.Button, cached); }
                    return;
                }
            }
            var cts = new System.Threading.CancellationTokenSource(500);
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (cts.IsCancellationRequested) return;
                    BitmapSource icon;
                    if (IsUwpAppName(g.ExeName))
                    {
                        icon = GetCachedUwpIcon(g.ExeName);
                        if (icon == null && hwnd != IntPtr.Zero)
                            icon = GetBestIcon(hwnd, pid, exePath, 32);
                    }
                    else if (isPerWindow && hwnd != IntPtr.Zero)
                    {
                        // [PER-WINDOW-ICON] Для браузерных профилей читаем иконку
                        // напрямую из окна — каждый профиль Chrome имеет свою HICON
                        icon = GetBestIcon(hwnd, pid, exePath, 32);
                        if (icon != null)
                        {
                            string hwndKey = $"hwnd:{hwnd.ToString("X")}";
                            lock (_iconCache) { _iconCache[hwndKey] = icon; }
                        }
                    }
                    else
                    {
                        icon = GetBestIcon(hwnd, pid, exePath, 32);
                    }
                    if (icon == null) return;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (g.Icon == null && g.Button != null)
                            {
                                g.Icon = icon; UpdateButtonIcon(g.Button, icon);
                                // [ICON-HASH] Регистрируем хэш иконки если ещё не зарегистрирован
                                bool isPerWin = !string.IsNullOrEmpty(g.GroupKey) && g.GroupKey.Contains(":");
                                if (isPerWin && g.IconHash == null)
                                {
                                    string h = ComputeIconHash(icon);
                                    if (h != null && !_iconHashToGroupKey.ContainsKey(h))
                                    { g.IconHash = h; _iconHashToGroupKey[h] = g.GroupKey; }
                                }
                            }
                        }
                        catch { }
                    }));
                }
                catch { }
                finally { cts.Dispose(); }
            }, cts.Token);
        }

        // AUMID таблица для shell:AppsFolder
        static readonly Dictionary<string, string> UwpAumidMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "photos",               "Microsoft.Windows.Photos_8wekyb3d8bbwe!App" },
            { "calculator",           "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" },
            { "hxoutlook",            "microsoft.windowscommunicationsapps_8wekyb3d8bbwe!microsoft.windowslive.mail" },
            { "hxcalendarappimm",     "microsoft.windowscommunicationsapps_8wekyb3d8bbwe!microsoft.windowslive.calendar" },
            { "winstore",             "Microsoft.WindowsStore_8wekyb3d8bbwe!App" },
            { "video.ui",             "Microsoft.ZuneVideo_8wekyb3d8bbwe!Microsoft.ZuneVideo" },
            { "maps",                 "Microsoft.WindowsMaps_8wekyb3d8bbwe!App" },
            { "microsoft.bingweather","Microsoft.BingWeather_8wekyb3d8bbwe!App" },
            { "windowscamera",        "Microsoft.WindowsCamera_8wekyb3d8bbwe!App" },
            { "xboxapp",              "Microsoft.XboxApp_8wekyb3d8bbwe!Microsoft.XboxApp" },
            { "systemsettings",       "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel" },
        };

        BitmapSource GetCachedUwpIcon(string n)
        {
            if (string.IsNullOrEmpty(n)) return null;
            string key = "uwp:" + n;
            if (_iconCache.TryGetValue(key, out var c)) return c;
            var raw = TryGetUwpIcon(n);
            // Fallback через shell:AppsFolder (не требует доступа к WindowsApps)
            if (raw == null && UwpAumidMap.TryGetValue(n.ToLowerInvariant(), out string aumid))
                raw = TryShellParsingNameImage("shell:AppsFolder\\" + aumid, UWP_ICON_SIZE);
            var i = raw != null ? TrimTransparentBorders(raw) ?? raw : null;
            if (i != null) _iconCache[key] = i;
            return i;
        }

        BitmapSource TryGetUwpIcon(string n)
        {
            try
            {
                switch (n?.ToLowerInvariant())
                {
                    case "systemsettings":
                        {
                            string p = @"C:\Windows\ImmersiveControlPanel\SystemSettings.exe";
                            return TryShellItemImage(File.Exists(p) ? p : "SystemSettings.exe", UWP_ICON_SIZE)
                                ?? IconHelper.GetIconFromExe(p, UWP_ICON_SIZE);
                        }
                    case "calculator":
                        return TryUwpFolderPatternIcon("Microsoft.WindowsCalculator*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("Calculator.exe", UWP_ICON_SIZE)
                            ?? TryShellItemImage("calc.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("Calculator.exe", UWP_ICON_SIZE);
                    case "hxoutlook":
                        return TryUwpFolderPatternIcon("microsoft.windowscommunicationsapps*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("HxOutlook.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("HxOutlook.exe", UWP_ICON_SIZE);
                    case "hxcalendarappimm":
                        return TryUwpFolderPatternIcon("microsoft.windowscommunicationsapps*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("HxCalendarAppImm.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("HxCalendarAppImm.exe", UWP_ICON_SIZE);
                    case "winstore":
                    case "winstore.app":
                        return TryUwpFolderPatternIcon("Microsoft.WindowsStore*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("WinStore.App.exe", UWP_ICON_SIZE);
                    case "photos":
                        return TryGetPhotosIcon(UWP_ICON_SIZE);
                    case "video.ui":
                        return TryUwpFolderPatternIcon("Microsoft.ZuneVideo*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("Video.UI.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("Video.UI.exe", UWP_ICON_SIZE);
                    case "maps":
                        return TryUwpFolderPatternIcon("Microsoft.WindowsMaps*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("Maps.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("Maps.exe", UWP_ICON_SIZE);
                    case "microsoft.bingweather":
                        return TryUwpFolderPatternIcon("Microsoft.BingWeather*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("Microsoft.BingWeather.exe", UWP_ICON_SIZE);
                    case "windowscamera":
                        return TryUwpFolderPatternIcon("Microsoft.WindowsCamera*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("WindowsCamera.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("WindowsCamera.exe", UWP_ICON_SIZE);
                    case "xboxapp":
                        return TryUwpFolderPatternIcon("Microsoft.XboxApp*", UWP_ICON_SIZE)
                            ?? TryFindInWindowsApps("XboxApp.exe", UWP_ICON_SIZE)
                            ?? IconHelper.GetIconFromExe("XboxApp.exe", UWP_ICON_SIZE);
                    default:
                        {
                            string ef = n + ".exe";
                            return TryFindInWindowsApps(ef, UWP_ICON_SIZE)
                                ?? TryShellItemImage(ef, UWP_ICON_SIZE)
                                ?? IconHelper.GetIconFromExe(ef, UWP_ICON_SIZE);
                        }
                }
            }
            catch { return null; }
        }

        BitmapSource TryGetPhotosIcon(int size)
        {
            // 1. shell:AppsFolder — работает без прав администратора
            try
            {
                string aumid = "Microsoft.Windows.Photos_8wekyb3d8bbwe!App";
                var bmp = TryShellParsingNameImage("shell:AppsFolder\\" + aumid, size);
                if (bmp != null) return bmp;
            }
            catch { }
            // 2. Через путь из _pidPathCache (если доступ есть)
            try
            {
                foreach (var kvp in _pidPathCache)
                {
                    string p = kvp.Value;
                    if (!string.IsNullOrEmpty(p) &&
                        p.IndexOf("Microsoft.Windows.Photos", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        File.Exists(p))
                    {
                        var bmp = TryShellItemImage(p, size) ?? IconHelper.GetIconFromExe(p, size);
                        if (bmp != null) return bmp;
                    }
                }
            }
            catch { }
            // 3. Стандартный путь через WindowsApps
            return TryUwpFolderPatternIcon("Microsoft.Windows.Photos*", size)
                ?? TryFindInWindowsApps("Photos.exe", size);
        }

        BitmapSource TryUwpFolderPatternIcon(string folderPattern, int size)
        {
            try
            {
                string uwpBase = @"C:\Program Files\WindowsApps";
                if (!Directory.Exists(uwpBase)) return null;
                string[] dirs;
                try { dirs = Directory.GetDirectories(uwpBase, folderPattern); }
                catch (UnauthorizedAccessException) { return null; }
                catch { return null; }
                if (dirs == null || dirs.Length == 0) return null;
                Array.Sort(dirs, (a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase));
                foreach (var dir in dirs)
                {
                    try
                    {
                        string manifest = IOPath.Combine(dir, "AppxManifest.xml");
                        if (!File.Exists(manifest)) continue;
                        string content;
                        try { content = File.ReadAllText(manifest); }
                        catch (UnauthorizedAccessException) { continue; }
                        string logo = ExtractManifestLogo(content);
                        if (string.IsNullOrEmpty(logo)) continue;
                        string iconPath = IOPath.Combine(dir, logo);
                        string baseDir = IOPath.GetDirectoryName(iconPath) ?? dir;
                        string baseName = IOPath.GetFileNameWithoutExtension(iconPath);
                        string ext = IOPath.GetExtension(iconPath);
                        var scales = new[]
                        {
                            ".scale-400",".scale-200",".scale-150",".scale-100",
                            ".targetsize-256",".targetsize-96",".targetsize-48",".targetsize-32",""
                        };
                        foreach (var scale in scales)
                        {
                            string scaled = IOPath.Combine(baseDir, baseName + scale + ext);
                            if (!File.Exists(scaled)) continue;
                            try
                            {
                                BitmapSource bmp = string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase)
                                    ? LoadPngIcon(scaled, size)
                                    : TryShellItemImage(scaled, size) ?? IconHelper.GetIconFromExe(scaled, size);
                                if (bmp != null) return bmp;
                            }
                            catch { }
                        }
                        if (Directory.Exists(baseDir))
                        {
                            var pngs = new List<string>();
                            try { pngs.AddRange(Directory.GetFiles(baseDir, baseName + "*.png")); } catch { }
                            pngs.Sort((a2, b2) => GetIconScaleOrder(a2).CompareTo(GetIconScaleOrder(b2)));
                            foreach (var png in pngs)
                            {
                                try { var bmp = LoadPngIcon(png, size); if (bmp != null) return bmp; }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        static int GetIconScaleOrder(string path)
        {
            if (path.Contains("scale-400")) return 0;
            if (path.Contains("scale-200")) return 1;
            if (path.Contains("scale-150")) return 2;
            if (path.Contains("targetsize-256")) return 3;
            if (path.Contains("targetsize-96")) return 4;
            if (path.Contains("scale-100")) return 5;
            if (path.Contains("targetsize-48")) return 6;
            if (path.Contains("targetsize-32")) return 7;
            return 8;
        }

        static BitmapSource LoadPngIcon(string path, int size)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                if (size > 0) { bi.DecodePixelWidth = size; bi.DecodePixelHeight = size; }
                bi.EndInit();
                if (bi.CanFreeze) bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        BitmapSource TryFindInWindowsApps(string exe, int size = 32)
        {
            try
            {
                string b = @"C:\Program Files\WindowsApps";
                if (!Directory.Exists(b)) return null;
                string[] dirs;
                try { dirs = Directory.GetDirectories(b); }
                catch (UnauthorizedAccessException) { return null; }
                catch { return null; }
                foreach (var d in dirs)
                {
                    try
                    {
                        string c = IOPath.Combine(d, exe);
                        if (!File.Exists(c)) continue;
                        var i = TryShellItemImage(c, size) ?? IconHelper.GetIconFromExe(c, size);
                        if (i != null) return i;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        static BitmapSource TrimTransparentBorders(BitmapSource src)
        {
            try
            {
                BitmapSource work = src;
                if (src.Format != System.Windows.Media.PixelFormats.Bgra32)
                    work = new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int w = work.PixelWidth, h = work.PixelHeight;
                if (w == 0 || h == 0) return src;
                int stride = w * 4;
                byte[] pixels = new byte[h * stride];
                work.CopyPixels(pixels, stride, 0);
                int minX = w, maxX = -1, minY = h, maxY = -1;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        byte alpha = pixels[y * stride + x * 4 + 3];
                        if (alpha > 8) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
                    }
                if (maxX < 0 || maxY < 0) return src;
                int cropW = maxX - minX + 1, cropH = maxY - minY + 1;
                if (cropW >= w * 0.9 && cropH >= h * 0.9) return src;
                const int pad = 2;
                int rx = Math.Max(0, minX - pad), ry = Math.Max(0, minY - pad);
                int rw = Math.Min(w - rx, cropW + pad * 2), rh = Math.Min(h - ry, cropH + pad * 2);
                var cropped = new CroppedBitmap(work, new Int32Rect(rx, ry, rw, rh));
                if (cropped.CanFreeze) cropped.Freeze();
                return cropped;
            }
            catch { return src; }
        }

        static string ExtractManifestLogo(string xml)
        {
            try
            {
                foreach (var attr in new[] { "Square44x44Logo", "Square30x30Logo", "Logo" })
                {
                    string search = attr + "=\"";
                    int i = xml.IndexOf(search, StringComparison.OrdinalIgnoreCase);
                    if (i >= 0)
                    {
                        i += search.Length;
                        int j = xml.IndexOf('"', i);
                        if (j > i) { string val = xml.Substring(i, j - i).Trim(); if (!string.IsNullOrEmpty(val)) return val; }
                    }
                }
                const string open = "<Logo>", close = "</Logo>";
                int ii = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
                if (ii >= 0)
                {
                    ii += open.Length;
                    int jj = xml.IndexOf(close, ii, StringComparison.OrdinalIgnoreCase);
                    if (jj > ii) return xml.Substring(ii, jj - ii).Trim();
                }
            }
            catch { }
            return null;
        }

    }
}
