using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MyTaskbar.Helpers
{
    public static class IconHelper
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string szFileName, int nIconIndex,
            IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
            ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ExtractAssociatedIcon(IntPtr hInst,
            StringBuilder lpIconPath, out ushort lpiIcon);

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hInst, string name,
            uint type, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SISIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)] struct SISIZE { public int cx, cy; }

        [Flags]
        enum SIIGBF : int
        {
            ResizeToFit   = 0x00,
            BiggerSizeOk  = 0x01,
            MemoryOnly    = 0x02,
            IconOnly      = 0x04,
            ThumbnailOnly = 0x08,
            InCacheOnly   = 0x10,
            NoOverlay     = 0x40,
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
        }

        private const uint SHGFI_ICON             = 0x000000100;
        private const uint SHGFI_LARGEICON        = 0x000000000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL  = 0x000000080;

        // ══════════════════════════════════════════════════════════════
        //  КЭШ
        // ══════════════════════════════════════════════════════════════
        private static readonly Dictionary<string, BitmapSource> _cache =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        // ══════════════════════════════════════════════════════════════
        //  ПУБЛИЧНЫЕ МЕТОДЫ
        // ══════════════════════════════════════════════════════════════
        public static BitmapSource GetIconFromExe(string path, int size = 32)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = path + "@" + size;
            if (_cache.TryGetValue(key, out var cached)) return cached;
            var result = GetIconInternal(path, size);
            if (result != null) _cache[key] = result;
            return result;
        }

        public static BitmapSource GetIconFromProcess(int pid, int size = 32)
        {
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(pid);
                string exePath = null;
                try { exePath = proc.MainModule?.FileName; } catch { }
                if (!string.IsNullOrEmpty(exePath))
                    return GetIconFromExe(exePath, size);
            }
            catch { }
            return null;
        }

        // ══════════════════════════════════════════════════════════════
        //  ЦЕПОЧКА ПОИСКА ИКОНКИ
        // ══════════════════════════════════════════════════════════════
        private static BitmapSource GetIconInternal(string path, int size)
        {
            try { path = Environment.ExpandEnvironmentVariables(path); } catch { }

            // ── 0. Прямой .png / .ico / .jpg ──
            {
                string ext = Path.GetExtension(path);
                if (!string.IsNullOrEmpty(ext))
                {
                    string extLow = ext.ToLowerInvariant();
                    if (extLow == ".png" || extLow == ".jpg" || extLow == ".jpeg" || extLow == ".bmp")
                    {
                        if (File.Exists(path))
                        {
                            var bmp = LoadBitmapFile(path);
                            if (bmp != null) return bmp;
                        }
                        string dir2  = Path.GetDirectoryName(path) ?? "";
                        string noExt = Path.GetFileNameWithoutExtension(path);
                        foreach (var scale in new[] { ".scale-200", ".scale-150", ".scale-100", ".targetsize-48", ".targetsize-32" })
                        {
                            string scaled = Path.Combine(dir2, noExt + scale + ext);
                            if (!File.Exists(scaled)) continue;
                            var bmp2 = LoadBitmapFile(scaled);
                            if (bmp2 != null) return bmp2;
                        }
                        if (Directory.Exists(dir2))
                            foreach (var png in Directory.GetFiles(dir2, "*.png"))
                            {
                                var bmp2 = LoadBitmapFile(png);
                                if (bmp2 != null) return bmp2;
                            }
                        return null;
                    }
                    if (extLow == ".ico")
                    {
                        if (File.Exists(path))
                        {
                            var bmp = LoadIcoFile(path, size);
                            if (bmp != null) return bmp;
                        }
                        return null;
                    }
                }
            }

            // ── 1. .lnk → резолвим цель, затем Shell на сам .lnk ──
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string resolved = ResolveShortcut(path);
                if (!string.IsNullOrEmpty(resolved) && resolved != path)
                {
                    var targetBmp = GetIconInternal(resolved, size);
                    if (targetBmp != null) return targetBmp;
                }
                // Shell на сам .lnk
                if (File.Exists(path))
                {
                    var lnkBmp = TryShellItemImage(path, size);
                    if (lnkBmp != null) return lnkBmp;

                    // SHGetFileInfo как финальный запасной для .lnk
                    var lnkFb = TrySHGetFileInfo(path, false);
                    if (lnkFb != null) return lnkFb;
                }
                return null;
            }

            // ── 2. .msc — файлы оснасток MMC (Управление компьютером и др.) ──
            if (path.EndsWith(".msc", StringComparison.OrdinalIgnoreCase))
            {
                // Shell умеет доставать иконки .msc напрямую
                if (File.Exists(path))
                {
                    var mscBmp = TryShellItemImage(path, size);
                    if (mscBmp != null) return mscBmp;

                    var mscFb = TrySHGetFileInfo(path, false);
                    if (mscFb != null) return mscFb;
                }
                return null;
            }

            // ── Просто имя без пути ──
            if (!Path.IsPathRooted(path))
            {
                string found = FindExeByName(path);
                if (!string.IsNullOrEmpty(found)) path = found;
            }

            bool exists = File.Exists(path);

            bool isSystemFile = exists && (
                path.IndexOf(@"\System32\",   StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf(@"\SysWOW64\",   StringComparison.OrdinalIgnoreCase) >= 0 ||
                (path.IndexOf(@"\Windows\",   StringComparison.OrdinalIgnoreCase) >= 0 &&
                 path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) < 0)
            );

            if (exists)
            {
                BitmapSource bmp;

                // 3. Системные exe Win10 — LoadLibraryEx первым
                if (isSystemFile)
                {
                    bmp = TryLoadLibraryIcon(path);
                    if (bmp != null) return bmp;
                }

                // 4. Sidecar .ico (Electron и др.)
                bmp = TrySidecarIco(path, size);
                if (bmp != null) return bmp;

                // 5. Sidecar .png
                bmp = TrySidecarPng(path, size);
                if (bmp != null) return bmp;

                // 6. Windows Shell кэш (перебор флагов)
                bmp = TryShellItemImage(path, size);
                if (bmp != null) return bmp;

                // 7. ExtractAssociatedIcon
                bmp = TryAssociatedIcon(path);
                if (bmp != null) return bmp;

                // 8. LoadLibraryEx для не-системных
                if (!isSystemFile)
                {
                    bmp = TryLoadLibraryIcon(path);
                    if (bmp != null) return bmp;
                }

                // 9. ExtractIconEx
                bmp = TryExtractIconEx(path);
                if (bmp != null) return bmp;
            }

            // 10. SHGetFileInfo
            var fallback = TrySHGetFileInfo(path, !exists);
            if (fallback != null) return fallback;

            // 11. UWP AppxManifest
            return TryUwpPackageIcon(path, size);
        }

        // ══════════════════════════════════════════════════════════════
        //  LOADLIBRARYEX + EXTRACTICONEX
        // ══════════════════════════════════════════════════════════════
        private static BitmapSource TryLoadLibraryIcon(string exePath)
        {
            try
            {
                var large = new IntPtr[1];
                var small = new IntPtr[1];
                uint cnt = ExtractIconEx(exePath, 0, large, small, 1);
                if (cnt > 0)
                {
                    IntPtr hIcon  = large[0] != IntPtr.Zero ? large[0] : small[0];
                    IntPtr hOther = large[0] != IntPtr.Zero ? small[0] : IntPtr.Zero;
                    if (hOther != IntPtr.Zero) try { DestroyIcon(hOther); } catch { }
                    if (hIcon != IntPtr.Zero)
                    {
                        var bmp = FromHIcon(hIcon);
                        try { DestroyIcon(hIcon); } catch { }
                        if (bmp != null) return bmp;
                    }
                }
            }
            catch { }

            IntPtr hLib = IntPtr.Zero;
            try
            {
                hLib = LoadLibraryEx(exePath, IntPtr.Zero, 0);
                if (hLib != IntPtr.Zero)
                    foreach (var id in new[] { 1, 2, 100, 101, 32512 })
                    {
                        IntPtr hIcon = LoadIcon(hLib, new IntPtr(id));
                        if (hIcon == IntPtr.Zero) continue;
                        var bmp = FromHIcon(hIcon);
                        if (bmp != null) return bmp;
                    }
            }
            catch { }
            finally { if (hLib != IntPtr.Zero) try { FreeLibrary(hLib); } catch { } }
            return null;
        }

        // ══════════════════════════════════════════════════════════════
        //  SIDECAR ICO / PNG
        // ══════════════════════════════════════════════════════════════
        private static BitmapSource TrySidecarIco(string exePath, int size)
        {
            try
            {
                string exeFileName = Path.GetFileNameWithoutExtension(exePath) ?? "";
                string exeNameLower = exeFileName.ToLowerInvariant();
                string dir     = Path.GetDirectoryName(exePath) ?? "";
                string parent  = Path.GetDirectoryName(dir)     ?? "";
                string parent2 = Path.GetDirectoryName(parent)  ?? "";

                var candidates = new[]
                {
                    Path.Combine(parent,  exeNameLower + ".ico"),
                    Path.Combine(parent,  exeFileName  + ".ico"),
                    Path.Combine(parent,  "app.ico"),
                    Path.Combine(parent,  "icon.ico"),
                    Path.Combine(parent,  "app-icon.ico"),
                    Path.Combine(dir,     exeNameLower + ".ico"),
                    Path.Combine(dir,     exeFileName  + ".ico"),
                    Path.Combine(dir,     "app.ico"),
                    Path.Combine(dir,     "icon.ico"),
                    Path.Combine(dir,     "app-icon.ico"),
                    Path.Combine(dir,     "resources", "app.ico"),
                    Path.Combine(dir,     "resources", "icon.ico"),
                    Path.Combine(dir,     "resources", "app-icon.ico"),
                    Path.Combine(parent,  "resources", "app.ico"),
                    Path.Combine(parent,  "resources", "icon.ico"),
                    Path.Combine(parent2, exeNameLower + ".ico"),
                    Path.Combine(parent2, "app.ico"),
                };

                foreach (var ico in candidates)
                {
                    if (string.IsNullOrEmpty(ico) || !File.Exists(ico)) continue;
                    var bmp = LoadIcoFile(ico, size);
                    if (bmp != null) return bmp;
                }
            }
            catch { }
            return null;
        }

        private static BitmapSource TrySidecarPng(string exePath, int size)
        {
            try
            {
                string dir    = Path.GetDirectoryName(exePath) ?? "";
                string parent = Path.GetDirectoryName(dir)     ?? "";

                var candidates = new[]
                {
                    Path.Combine(dir,    "icon.png"),
                    Path.Combine(dir,    "app.png"),
                    Path.Combine(dir,    "resources", "icon.png"),
                    Path.Combine(dir,    "resources", "app.png"),
                    Path.Combine(parent, "icon.png"),
                    Path.Combine(parent, "resources", "icon.png"),
                };

                foreach (var png in candidates)
                {
                    if (string.IsNullOrEmpty(png) || !File.Exists(png)) continue;
                    var bmp = LoadBitmapFile(png);
                    if (bmp != null) return bmp;
                }
            }
            catch { }
            return null;
        }

        // ══════════════════════════════════════════════════════════════
        //  SHELL / SYSTEM  — перебор флагов убирает белые иконки
        // ══════════════════════════════════════════════════════════════
        private static BitmapSource TryShellItemImage(string path, int size)
        {
            try
            {
                var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object obj);
                var factory = obj as IShellItemImageFactory;
                if (factory == null) return null;

                var flagVariants = new SIIGBF[]
                {
                    SIIGBF.ResizeToFit | SIIGBF.BiggerSizeOk,
                    SIIGBF.IconOnly    | SIIGBF.BiggerSizeOk,
                    SIIGBF.ResizeToFit,
                    SIIGBF.IconOnly,
                };

                foreach (var flags in flagVariants)
                {
                    int hr = factory.GetImage(new SISIZE { cx = size, cy = size },
                        flags, out IntPtr hBitmap);
                    if (hr == 0 && hBitmap != IntPtr.Zero)
                    {
                        var bmp = FromHBitmap(hBitmap, size);
                        DeleteObject(hBitmap);
                        if (bmp != null) return bmp;
                    }
                }
            }
            catch { }
            return null;
        }

        private static BitmapSource TryAssociatedIcon(string path)
        {
            try
            {
                var sb = new StringBuilder(path, 260);
                IntPtr hIcon = ExtractAssociatedIcon(IntPtr.Zero, sb, out _);
                if (hIcon == IntPtr.Zero) return null;
                var bmp = FromHIcon(hIcon);
                DestroyIcon(hIcon);
                return bmp;
            }
            catch { return null; }
        }

        private static BitmapSource TryExtractIconEx(string path)
        {
            try
            {
                var large = new IntPtr[1];
                var small = new IntPtr[1];
                ExtractIconEx(path, 0, large, small, 1);
                IntPtr hIcon  = large[0] != IntPtr.Zero ? large[0] : small[0];
                IntPtr hOther = large[0] != IntPtr.Zero ? small[0] : IntPtr.Zero;
                if (hOther != IntPtr.Zero) DestroyIcon(hOther);
                if (hIcon == IntPtr.Zero) return null;
                var bmp = FromHIcon(hIcon);
                DestroyIcon(hIcon);
                return bmp;
            }
            catch { return null; }
        }

        private static BitmapSource TrySHGetFileInfo(string path, bool useFileAttributes)
        {
            try
            {
                var info = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_LARGEICON;
                uint attr  = 0;
                if (useFileAttributes) { flags |= SHGFI_USEFILEATTRIBUTES; attr = FILE_ATTRIBUTE_NORMAL; }
                IntPtr res = SHGetFileInfo(path, attr, ref info, (uint)Marshal.SizeOf(info), flags);
                if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
                var bmp = FromHIcon(info.hIcon);
                DestroyIcon(info.hIcon);
                return bmp;
            }
            catch { return null; }
        }

        private static BitmapSource TryUwpPackageIcon(string path, int size)
        {
            try
            {
                string uwpBase = @"C:\Program Files\WindowsApps";
                if (!Directory.Exists(uwpBase)) return null;
                string exeName = Path.GetFileName(path);
                string[] dirs; try { dirs = Directory.GetDirectories(uwpBase); } catch { return null; }
                foreach (var dir in dirs)
                {
                    try
                    {
                        if (!File.Exists(Path.Combine(dir, exeName))) continue;
                        string manifest = Path.Combine(dir, "AppxManifest.xml");
                        if (!File.Exists(manifest)) continue;
                        string content = File.ReadAllText(manifest);
                        string logo    = ExtractXmlValue(content, "Logo");
                        if (string.IsNullOrEmpty(logo)) continue;
                        string iconPath = Path.Combine(dir, logo);
                        string baseDir  = Path.GetDirectoryName(iconPath) ?? dir;
                        string baseName = Path.GetFileNameWithoutExtension(iconPath);
                        string ext      = Path.GetExtension(iconPath);
                        foreach (var scale in new[] { ".scale-200", ".scale-150", ".scale-100", ".targetsize-256", ".targetsize-48", "" })
                        {
                            string scaled = Path.Combine(baseDir, baseName + scale + ext);
                            if (!File.Exists(scaled)) continue;
                            var bmp = LoadIcoOrBitmap(scaled, size);
                            if (bmp != null) return bmp;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // ══════════════════════════════════════════════════════════════
        //  ПОИСК EXE ПО ИМЕНИ
        // ══════════════════════════════════════════════════════════════
        public static string FindExeByName(string nameOrExe)
        {
            if (string.IsNullOrEmpty(nameOrExe)) return null;

            string exeName = nameOrExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? nameOrExe : nameOrExe + ".exe";

            string r;
            r = FindViaAppPaths(exeName);    if (!string.IsNullOrEmpty(r)) return r;
            r = FindViaUninstall(exeName);   if (!string.IsNullOrEmpty(r)) return r;

            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var c in new[] {
                Path.Combine(winDir, exeName),
                Path.Combine(winDir, "System32", exeName),
                Path.Combine(winDir, "SysWOW64", exeName),
                Path.Combine(winDir, "System32", "WindowsPowerShell", "v1.0", exeName),
            }) if (File.Exists(c)) return c;

            r = FindViaKnownFolders(exeName); if (!string.IsNullOrEmpty(r)) return r;
            r = FindInWindowsApps(exeName);   if (!string.IsNullOrEmpty(r)) return r;
            r = FindInPathEnv(exeName);       if (!string.IsNullOrEmpty(r)) return r;

            return null;
        }

        private static string FindViaKnownFolders(string exeName)
        {
            string lf   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string af   = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string el   = exeName.ToLowerInvariant();

            if (el == "discord.exe")
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Discord"))
                    {
                        string loc = (key?.GetValue("InstallLocation") as string ?? "").Trim('"').Trim();
                        if (!string.IsNullOrEmpty(loc))
                        { string f = FindLatestElectronExe(loc, "Discord.exe"); if (f != null) return f; }
                    }
                }
                catch { }
                string db = Path.Combine(lf, "Discord");
                if (Directory.Exists(db)) { string f = FindLatestElectronExe(db, "Discord.exe"); if (f != null) return f; }
                foreach (var v in new[] { "DiscordCanary", "DiscordPTB" })
                {
                    string vb = Path.Combine(lf, v);
                    if (Directory.Exists(vb)) { string f = FindLatestElectronExe(vb, v + ".exe"); if (f != null) return f; }
                }
            }

            if (el == "telegram.exe")
                foreach (var p in new[] {
                    Path.Combine(af,  "Telegram Desktop", "Telegram.exe"),
                    Path.Combine(lf,  "Telegram Desktop", "Telegram.exe"),
                    Path.Combine(pf,  "Telegram Desktop", "Telegram.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "steam.exe")
            {
                foreach (var p in new[] {
                    Path.Combine(pf86, "Steam", "steam.exe"),
                    Path.Combine(pf,   "Steam", "steam.exe"),
                    @"C:\Steam\steam.exe", @"D:\Steam\steam.exe", @"E:\Steam\steam.exe",
                }) if (File.Exists(p)) return p;
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                                  ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam"))
                    {
                        string sp = key?.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(sp))
                        { string p = Path.Combine(sp, "steam.exe"); if (File.Exists(p)) return p; }
                    }
                }
                catch { }
            }

            if (el == "spotify.exe")
                foreach (var p in new[] {
                    Path.Combine(af, "Spotify", "Spotify.exe"),
                    Path.Combine(lf, "Spotify", "Spotify.exe"),
                    Path.Combine(lf, "Microsoft", "WindowsApps", "Spotify.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "slack.exe")
            {
                string sb2 = Path.Combine(lf, "slack");
                if (Directory.Exists(sb2)) { string f = FindLatestElectronExe(sb2, "slack.exe"); if (f != null) return f; }
            }

            if (el == "whatsapp.exe")
                foreach (var p in new[] {
                    Path.Combine(lf, "WhatsApp",  "WhatsApp.exe"),
                    Path.Combine(af, "WhatsApp",  "WhatsApp.exe"),
                    Path.Combine(lf, "Microsoft", "WindowsApps", "WhatsApp.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "code.exe")
                foreach (var p in new[] {
                    Path.Combine(lf,   "Programs", "Microsoft VS Code", "Code.exe"),
                    Path.Combine(pf,   "Microsoft VS Code", "Code.exe"),
                    Path.Combine(pf86, "Microsoft VS Code", "Code.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "cursor.exe")
                foreach (var p in new[] {
                    Path.Combine(lf, "Programs", "cursor", "Cursor.exe"),
                    Path.Combine(lf, "cursor",   "Cursor.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "obsidian.exe")
                foreach (var p in new[] {
                    Path.Combine(lf, "Obsidian", "Obsidian.exe"),
                    Path.Combine(pf, "Obsidian", "Obsidian.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "notion.exe")
                foreach (var p in new[] {
                    Path.Combine(lf, "Programs", "Notion", "Notion.exe"),
                    Path.Combine(lf, "Notion",   "Notion.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "zoom.exe")
                foreach (var p in new[] {
                    Path.Combine(af, "Zoom", "bin", "Zoom.exe"),
                    Path.Combine(pf, "Zoom", "bin", "Zoom.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "obs64.exe" || el == "obs.exe")
                foreach (var p in new[] {
                    Path.Combine(pf,   "obs-studio", "bin", "64bit", "obs64.exe"),
                    Path.Combine(pf86, "obs-studio", "bin", "64bit", "obs64.exe"),
                    Path.Combine(pf,   "OBS Studio",  "bin", "64bit", "obs64.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "vlc.exe")
                foreach (var p in new[] {
                    Path.Combine(pf,   "VideoLAN", "VLC", "vlc.exe"),
                    Path.Combine(pf86, "VideoLAN", "VLC", "vlc.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "7zfm.exe")
                foreach (var p in new[] {
                    Path.Combine(pf,   "7-Zip", "7zFM.exe"),
                    Path.Combine(pf86, "7-Zip", "7zFM.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "winrar.exe")
                foreach (var p in new[] {
                    Path.Combine(pf,   "WinRAR", "WinRAR.exe"),
                    Path.Combine(pf86, "WinRAR", "WinRAR.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "notepad++.exe")
                foreach (var p in new[] {
                    Path.Combine(pf,   "Notepad++", "notepad++.exe"),
                    Path.Combine(pf86, "Notepad++", "notepad++.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "figma.exe")
                foreach (var p in new[] {
                    Path.Combine(lf, "Figma",    "Figma.exe"),
                    Path.Combine(lf, "Programs", "Figma", "Figma.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "postman.exe")
            {
                string pb = Path.Combine(lf, "Postman");
                if (Directory.Exists(pb)) { string f = FindLatestElectronExe(pb, "Postman.exe"); if (f != null) return f; }
            }

            if (el == "gitkraken.exe")
            {
                string gb = Path.Combine(lf, "gitkraken");
                if (Directory.Exists(gb)) { string f = FindLatestElectronExe(gb, "gitkraken.exe"); if (f != null) return f; }
            }

            if (el == "skype.exe")
                foreach (var p in new[] {
                    Path.Combine(pf,   "Microsoft", "Skype for Desktop", "Skype.exe"),
                    Path.Combine(pf86, "Microsoft", "Skype for Desktop", "Skype.exe"),
                    Path.Combine(lf,   "Microsoft", "Skype for Desktop", "Skype.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "ms-teams.exe" || el == "teams.exe")
                foreach (var p in new[] {
                    Path.Combine(lf, "Microsoft", "Teams", "current", "Teams.exe"),
                    Path.Combine(pf, "Microsoft", "Teams", "current", "Teams.exe"),
                    Path.Combine(lf, "Programs",  "Microsoft Teams", "Teams.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "viber.exe")
                foreach (var p in new[] {
                    Path.Combine(lf, "Viber", "Viber.exe"),
                    Path.Combine(pf, "Viber", "Viber.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "qbittorrent.exe")
                foreach (var p in new[] {
                    Path.Combine(pf,   "qBittorrent", "qbittorrent.exe"),
                    Path.Combine(pf86, "qBittorrent", "qbittorrent.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "utorrent.exe")
            {
                string utBase = Path.Combine(af, "uTorrent");
                if (Directory.Exists(utBase))
                    foreach (var f in SafeGetFiles(utBase, "uTorrent.exe")) return f;
            }

            if (el == "anydesk.exe")
                foreach (var p in new[] {
                    Path.Combine(pf,   "AnyDesk", "AnyDesk.exe"),
                    Path.Combine(pf86, "AnyDesk", "AnyDesk.exe"),
                    Path.Combine(lf,   "AnyDesk", "AnyDesk.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "gimp-2.10.exe" || el == "gimp.exe")
                foreach (var gbase in new[] { Path.Combine(pf, "GIMP 2", "bin"), Path.Combine(pf86, "GIMP 2", "bin") })
                    foreach (var f in SafeGetFiles(gbase, "gimp-*.exe")) return f;

            if (el == "totalcmd64.exe" || el == "totalcmd.exe")
                foreach (var p in new[] {
                    Path.Combine(pf,   "totalcmd", "TOTALCMD64.EXE"),
                    Path.Combine(pf86, "totalcmd", "TOTALCMD.EXE"),
                    @"C:\totalcmd\TOTALCMD64.EXE",
                }) if (File.Exists(p)) return p;

            if (el == "far.exe")
                foreach (var p in new[] {
                    Path.Combine(pf,   "Far Manager", "Far.exe"),
                    Path.Combine(pf86, "Far Manager", "Far.exe"),
                }) if (File.Exists(p)) return p;

            if (el == "snippingtool.exe" || el == "screenclippinghost.exe" || el == "screensketch.exe")
            {
                string winDir2 = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                foreach (var p in new[] {
                    Path.Combine(winDir2, "System32",  "SnippingTool.exe"),
                    Path.Combine(winDir2, "SysWOW64",  "SnippingTool.exe"),
                    Path.Combine(winDir2, "System32",  "ScreenClippingHost.exe"),
                }) if (File.Exists(p)) return p;
                try
                {
                    string uwp = @"C:\Program Files\WindowsApps";
                    if (Directory.Exists(uwp))
                    {
                        foreach (var dir in Directory.GetDirectories(uwp, "MicrosoftWindows.Client.CBS*"))
                        { string p = Path.Combine(dir, "SnippingTool.exe"); if (File.Exists(p)) return p; }
                        foreach (var dir in Directory.GetDirectories(uwp, "Microsoft.ScreenSketch*"))
                        {
                            foreach (var f in SafeGetFiles(dir, "SnippingTool.exe"))  return f;
                            foreach (var f in SafeGetFiles(dir, "ScreenSketch.exe"))  return f;
                        }
                    }
                }
                catch { }
            }

            if (el == "mspaint.exe")
            {
                string winDir2 = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                foreach (var p in new[] {
                    Path.Combine(winDir2, "System32",  "mspaint.exe"),
                    Path.Combine(winDir2, "SysWOW64",  "mspaint.exe"),
                }) if (File.Exists(p)) return p;
            }

            if (el == "notepad.exe")
            {
                string winDir2 = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                foreach (var p in new[] {
                    Path.Combine(winDir2, "notepad.exe"),
                    Path.Combine(winDir2, "System32", "notepad.exe"),
                }) if (File.Exists(p)) return p;
            }

            if (el == "taskmgr.exe")
            {
                string winDir2 = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string p2 = Path.Combine(winDir2, "System32", "Taskmgr.exe");
                if (File.Exists(p2)) return p2;
            }

            if (el == "calculator.exe" || el == "calc.exe")
            {
                string winDir2 = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string p2 = Path.Combine(winDir2, "System32", "calc.exe");
                if (File.Exists(p2)) return p2;
                try
                {
                    string uwp = @"C:\Program Files\WindowsApps";
                    if (Directory.Exists(uwp))
                        foreach (var dir in Directory.GetDirectories(uwp, "Microsoft.WindowsCalculator*"))
                            foreach (var f in SafeGetFiles(dir, "Calculator.exe")) return f;
                }
                catch { }
            }

            return null;
        }

        private static string FindLatestElectronExe(string baseDir, string exeFileName)
        {
            if (!Directory.Exists(baseDir)) return null;
            try
            {
                Version latestVer = null; string latestDir = null;
                foreach (var dir in Directory.GetDirectories(baseDir, "app-*"))
                {
                    string verStr = Path.GetFileName(dir).Substring(4);
                    if (Version.TryParse(verStr, out Version v) && (latestVer == null || v > latestVer))
                    { latestVer = v; latestDir = dir; }
                }
                if (latestDir != null)
                { string p = Path.Combine(latestDir, exeFileName); if (File.Exists(p)) return p; }
                string direct = Path.Combine(baseDir, exeFileName);
                if (File.Exists(direct)) return direct;
            }
            catch { }
            return null;
        }

        private static string FindViaAppPaths(string exeName)
        {
            foreach (var regPath in new[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
            })
            {
                try
                {
                    foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                    {
                        using (var key = hive.OpenSubKey(regPath))
                        {
                            string val = key?.GetValue(null) as string;
                            if (string.IsNullOrEmpty(val)) continue;
                            val = val.Trim('"').Trim();
                            if (File.Exists(val)) return val;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static string FindViaUninstall(string exeName)
        {
            foreach (var root in new[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            })
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(root))
                    {
                        if (key == null) continue;
                        foreach (var sub in key.GetSubKeyNames())
                        {
                            try
                            {
                                using (var sk = key.OpenSubKey(sub))
                                {
                                    if (sk == null) continue;
                                    string name = sk.GetValue("DisplayName") as string;
                                    if (string.IsNullOrWhiteSpace(name)) continue;

                                    string releaseType = sk.GetValue("ReleaseType") as string ?? "";
                                    string parentKey   = sk.GetValue("ParentKeyName") as string ?? "";
                                    string systemComp  = sk.GetValue("SystemComponent") as string ?? "";
                                    if (systemComp == "1") continue;
                                    if (releaseType.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                    if (parentKey.StartsWith("OperatingSystem", StringComparison.OrdinalIgnoreCase)) continue;

                                    string icon = sk.GetValue("DisplayIcon") as string;
                                    if (!string.IsNullOrEmpty(icon))
                                    {
                                        ParseIconLocation(icon, out string ip, out _);
                                        if (!string.IsNullOrEmpty(ip) &&
                                            ip.EndsWith(exeName, StringComparison.OrdinalIgnoreCase) &&
                                            File.Exists(ip)) return ip;
                                    }
                                    string loc = (sk.GetValue("InstallLocation") as string ?? "").Trim('"').Trim();
                                    if (!Directory.Exists(loc)) continue;
                                    string c = Path.Combine(loc, exeName);
                                    if (File.Exists(c)) return c;
                                    foreach (var sub2 in Directory.GetDirectories(loc))
                                    { c = Path.Combine(sub2, exeName); if (File.Exists(c)) return c; }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (key != null)
                        foreach (var sub in key.GetSubKeyNames())
                        {
                            try
                            {
                                using (var sk = key.OpenSubKey(sub))
                                {
                                    string loc = (sk?.GetValue("InstallLocation") as string ?? "").Trim('"').Trim();
                                    if (!Directory.Exists(loc)) continue;
                                    string c = Path.Combine(loc, exeName);
                                    if (File.Exists(c)) return c;
                                }
                            }
                            catch { }
                        }
                }
            }
            catch { }
            return null;
        }

        private static string FindInWindowsApps(string exeName)
        {
            try
            {
                string uwpBase = @"C:\Program Files\WindowsApps";
                if (!Directory.Exists(uwpBase)) return null;
                foreach (var dir in Directory.GetDirectories(uwpBase))
                    try { string c = Path.Combine(dir, exeName); if (File.Exists(c)) return c; } catch { }
            }
            catch { }
            return null;
        }

        private static string FindInPathEnv(string exeName)
        {
            try
            {
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string c = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(c)) return c;
                }
            }
            catch { }
            return null;
        }

        private static IEnumerable<string> SafeGetFiles(string dir, string pattern)
        {
            try { return Directory.GetFiles(dir, pattern, SearchOption.AllDirectories); }
            catch { return Array.Empty<string>(); }
        }

        // ══════════════════════════════════════════════════════════════
        //  ЗАГРУЗКА ФАЙЛОВ
        // ══════════════════════════════════════════════════════════════
        private static BitmapSource LoadIcoFile(string icoPath, int desiredSize)
        {
            try
            {
                using (var stream = new FileStream(icoPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var decoder = BitmapDecoder.Create(stream,
                        BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapFrame best = null; int bestDiff = int.MaxValue;
                    foreach (var frame in decoder.Frames)
                    {
                        int d = Math.Abs(frame.PixelWidth - desiredSize);
                        if (d < bestDiff) { bestDiff = d; best = frame; }
                    }
                    if (best == null) return null;
                    var f = best.Clone(); if (f.CanFreeze) f.Freeze(); return f;
                }
            }
            catch { }
            try
            {
                IntPtr h = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, desiredSize, desiredSize, LR_LOADFROMFILE);
                if (h == IntPtr.Zero) return null;
                var bmp = FromHIcon(h); DestroyIcon(h); return bmp;
            }
            catch { return null; }
        }

        private static BitmapSource LoadBitmapFile(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var decoder = BitmapDecoder.Create(stream,
                        BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames[0]; if (frame == null) return null;
                    var f = frame.Clone(); if (f.CanFreeze) f.Freeze(); return f;
                }
            }
            catch { return null; }
        }

        private static BitmapSource LoadIcoOrBitmap(string path, int size)
            => Path.GetExtension(path).ToLowerInvariant() == ".ico"
                ? LoadIcoFile(path, size)
                : LoadBitmapFile(path);

        // ══════════════════════════════════════════════════════════════
        //  КОНВЕРТЕРЫ HANDLE → BitmapSource
        // ══════════════════════════════════════════════════════════════
        private static BitmapSource FromHIcon(IntPtr hIcon)
        {
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                if (bmp != null && bmp.CanFreeze) bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private static BitmapSource FromHBitmap(IntPtr hBitmap, int size)
        {
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero,
                    new Int32Rect(0, 0, size, size),
                    BitmapSizeOptions.FromEmptyOptions());
                if (bmp != null && bmp.CanFreeze) bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private static void ParseIconLocation(string s, out string path, out int index)
        {
            s = s.Trim('"');
            int comma = s.LastIndexOf(',');
            if (comma > 0 && int.TryParse(s.Substring(comma + 1), out index))
                path = s.Substring(0, comma).Trim('"');
            else { path = s; index = 0; }
        }

        private static string ExtractXmlValue(string xml, string tag)
        {
            try
            {
                string open  = "<" + tag + ">",  close = "</" + tag + ">";
                int i = xml.IndexOf(open,  StringComparison.OrdinalIgnoreCase); if (i < 0) return null;
                i += open.Length;
                int j = xml.IndexOf(close, i, StringComparison.OrdinalIgnoreCase); if (j < 0) return null;
                return xml.Substring(i, j - i).Trim();
            }
            catch { return null; }
        }

        // ══════════════════════════════════════════════════════════════
        //  РЕЗОЛВ .LNK
        // ══════════════════════════════════════════════════════════════
        public static string ResolveShortcut(string lnkPath)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(t);
                dynamic sc    = shell.CreateShortcut(lnkPath);
                return sc.TargetPath;
            }
            catch { return null; }
        }
    }
}