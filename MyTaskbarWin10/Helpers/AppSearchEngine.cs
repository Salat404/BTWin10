using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MyTaskbar
{
    public enum AppKind { Win32, UWP, Shortcut }

    public class AppEntry
    {
        public string Name { get; set; }
        public string LaunchPath { get; set; }
        public string IconPath { get; set; }
        public string LnkPath { get; set; }
        public AppKind Kind { get; set; }
    }

    public class AppSearchEngine
    {
        private List<AppEntry> _index = new List<AppEntry>();
        private List<AppEntry> _indexFull = new List<AppEntry>();
        private readonly object _lock = new object();
        public bool IsReady { get; private set; }

        static readonly Guid FOLDERID_StartMenu = new Guid("625b53c3-ab48-4ec1-ba1f-a1ef4146fc19");
        static readonly Guid FOLDERID_CommonStartMenu = new Guid("a4115719-d62e-491d-aa7c-e74b8be3b067");
        static readonly Guid FOLDERID_Programs = new Guid("a77f5d77-2e2b-44c3-a6a2-aba601054a51");
        static readonly Guid FOLDERID_CommonPrograms = new Guid("0139d44e-6afe-49f2-8690-3dafcae6ffb8");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags,
            IntPtr hToken, out IntPtr ppszPath);

        public void BuildIndexAsync()
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var entries = new List<AppEntry>();
                    CollectStartMenuShortcuts(entries);
                    CollectUWPFromWindowsApps(entries);
                    CollectUWPFromRegistry(entries);
                    CollectWin32Registry(entries);

                    var all = entries
                        .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                        .ToList();

                    var deduped = all
                        .GroupBy(e => NormalizeForDedup(e.Name), StringComparer.OrdinalIgnoreCase)
                        .Select(g => g
                            .OrderByDescending(e => e.IconPath != null ? 1 : 0)
                            .ThenBy(e => (int)e.Kind)
                            .First())
                        .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var full = all
                        .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    lock (_lock)
                    {
                        _index = deduped;
                        _indexFull = full;
                    }
                }
                catch { }
                finally { IsReady = true; }
            });
        }

        public IEnumerable<AppEntry> GetAll()
        {
            lock (_lock) return _index.ToList();
        }

        public IEnumerable<AppEntry> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return GetAll();
            string q = query.ToLowerInvariant().Trim();
            lock (_lock)
            {
                return _indexFull
                    .Where(e => e.Name.ToLowerInvariant().Contains(q))
                    .OrderBy(e => {
                        string n = e.Name.ToLowerInvariant();
                        if (n == q) return 0;
                        if (n.StartsWith(q)) return 1;
                        return 2;
                    })
                    .ToList();
            }
        }

        static string NormalizeForDedup(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            string s = Regex.Replace(name, @"\s+v?\d[\d\.]*$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s*\([^)]{1,30}\)\s*$", "");
            s = Regex.Replace(s, @"[:\-–—]", " ");
            s = Regex.Replace(s, @"\s{2,}", " ");
            return s.Trim();
        }

        static readonly HashSet<string> _allowedLaunchExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".lnk", ".url", ".exe", ".msc", ".com", ".bat", ".cmd"
        };

        void CollectStartMenuShortcuts(List<AppEntry> entries)
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));

            foreach (var guid in new[] {
                FOLDERID_StartMenu, FOLDERID_CommonStartMenu,
                FOLDERID_Programs,  FOLDERID_CommonPrograms })
            {
                string p = GetKnownFolder(guid);
                if (!string.IsNullOrEmpty(p)) folders.Add(p);
            }

            foreach (var folder in folders)
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                try
                {
                    var files = Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories)
                        .Concat(Directory.GetFiles(folder, "*.url", SearchOption.AllDirectories));

                    foreach (var file in files)
                    {
                        try
                        {
                            string name = Path.GetFileNameWithoutExtension(file);
                            if (ShouldSkipName(name)) continue;

                            string ext = Path.GetExtension(file).ToLowerInvariant();
                            string resolved = null;
                            string iconPath = null;
                            string lnkPath = null;
                            AppKind kind = AppKind.Shortcut;

                            if (ext == ".lnk")
                            {
                                lnkPath = file;
                                resolved = Helpers.IconHelper.ResolveShortcut(file);
                                if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                                {
                                    string rext = Path.GetExtension(resolved).ToLowerInvariant();
                                    if (!_allowedLaunchExtensions.Contains(rext)) continue;
                                    iconPath = resolved;
                                    if (rext == ".exe" || rext == ".msc" || rext == ".com")
                                        kind = AppKind.Win32;
                                }
                                else
                                {
                                    continue;
                                }
                            }
                            else if (ext == ".url")
                            {
                                iconPath = file;
                            }

                            entries.Add(new AppEntry
                            {
                                Name = CleanName(name),
                                LaunchPath = file,
                                IconPath = iconPath,
                                LnkPath = lnkPath,
                                Kind = kind,
                            });
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        void CollectUWPFromWindowsApps(List<AppEntry> entries)
        {
            try
            {
                string windowsApps = @"C:\Program Files\WindowsApps";
                if (!Directory.Exists(windowsApps)) return;

                string[] pkgDirs;
                try { pkgDirs = Directory.GetDirectories(windowsApps); }
                catch { return; }

                foreach (var pkgDir in pkgDirs)
                {
                    try
                    {
                        string manifestPath = Path.Combine(pkgDir, "AppxManifest.xml");
                        if (!File.Exists(manifestPath)) continue;
                        ParseAppxManifest(manifestPath, pkgDir, entries);
                    }
                    catch { }
                }
            }
            catch { }
        }

        void CollectUWPFromRegistry(List<AppEntry> entries)
        {
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                {
                    if (root == null) return;
                    foreach (var pkgName in root.GetSubKeyNames())
                    {
                        try
                        {
                            using (var pkg = root.OpenSubKey(pkgName))
                            {
                                string packagePath = pkg?.GetValue("PackageRootFolder") as string;
                                if (string.IsNullOrEmpty(packagePath) || !Directory.Exists(packagePath)) continue;
                                string manifestPath = Path.Combine(packagePath, "AppxManifest.xml");
                                if (!File.Exists(manifestPath)) continue;
                                ParseAppxManifest(manifestPath, packagePath, entries);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        void ParseAppxManifest(string manifestPath, string packagePath, List<AppEntry> entries)
        {
            try
            {
                string xml = File.ReadAllText(manifestPath, Encoding.UTF8);

                string identityName = ExtractXmlAttr(xml, "Identity", "Name") ?? "";
                if (IsSystemInternalPackage(identityName)) return;

                string pfn = BuildPfn(Path.GetFileName(packagePath), identityName);

                int pos = 0;
                while (true)
                {
                    int appStart = xml.IndexOf("<Application ", pos, StringComparison.OrdinalIgnoreCase);
                    if (appStart < 0) break;

                    int selfClose = xml.IndexOf("/>", appStart);
                    int fullClose = xml.IndexOf("</Application>", appStart, StringComparison.OrdinalIgnoreCase);
                    int appEnd;
                    if (selfClose > 0 && (fullClose < 0 || selfClose < fullClose))
                        appEnd = selfClose + 2;
                    else if (fullClose > 0)
                        appEnd = fullClose + 14;
                    else
                        break;

                    string appXml = xml.Substring(appStart, appEnd - appStart);
                    pos = appEnd;

                    string appId = ExtractXmlAttr(appXml, "Application", "Id") ?? "";
                    if (string.IsNullOrEmpty(appId)) continue;

                    string executable = ExtractXmlAttr(appXml, "Application", "Executable");
                    if (string.IsNullOrEmpty(executable)) continue;

                    bool hasVisualElements =
                        appXml.IndexOf("uap:VisualElements", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        appXml.IndexOf("VisualElements", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hasVisualElements) continue;

                    string displayName =
                        ExtractXmlValue(appXml, "uap:DisplayName") ??
                        ExtractXmlValue(appXml, "DisplayName") ??
                        ExtractXmlValue(xml, "uap:DisplayName") ??
                        ExtractXmlValue(xml, "DisplayName") ??
                        identityName;

                    if (string.IsNullOrWhiteSpace(displayName)) continue;

                    if (displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                        displayName = FriendlyPkgName(identityName);

                    if (ShouldSkipName(displayName)) continue;

                    string logoRel =
                        ExtractXmlAttr(appXml, "uap:VisualElements", "Square44x44Logo") ??
                        ExtractXmlAttr(appXml, "VisualElements", "Square44x44Logo") ??
                        ExtractXmlAttr(appXml, "uap:VisualElements", "Square30x30Logo") ??
                        ExtractXmlValue(appXml, "uap:Square44x44Logo") ??
                        ExtractXmlValue(appXml, "Square44x44Logo") ??
                        ExtractXmlValue(xml, "uap:Square44x44Logo") ??
                        ExtractXmlValue(xml, "Logo");

                    string iconPath = null;
                    if (!string.IsNullOrEmpty(logoRel))
                        iconPath = ResolveScaledIcon(Path.Combine(packagePath, logoRel));

                    string aumid = pfn + "!" + appId;

                    entries.Add(new AppEntry
                    {
                        Name = CleanName(displayName),
                        LaunchPath = "shell:AppsFolder\\" + aumid,
                        IconPath = iconPath,
                        Kind = AppKind.UWP,
                    });
                }
            }
            catch { }
        }

        static readonly string[] _systemPackagePrefixes = new[]
        {
            "Microsoft.Windows.NarratorQuickStart",
            "Microsoft.Windows.SecureAssessmentBrowser",
            "Microsoft.BioEnrollment",
            "Microsoft.CredDialogHost",
            "Microsoft.ECApp",
            "Microsoft.LockApp",
            "Microsoft.MicrosoftEdgeDevToolsClient",
            "Microsoft.Win32WebViewHost",
            "Microsoft.Windows.Apprep",
            "Microsoft.Windows.AssignedAccessLockApp",
            "Microsoft.Windows.CallingShellApp",
            "Microsoft.Windows.CapturePicker",
            "Microsoft.Windows.ContentDeliveryManager",
            "Microsoft.Windows.PeopleExperienceHost",
            "Microsoft.Windows.PinningConfirmationDialog",
            "Microsoft.Windows.PrintQueueActionCenter",
            "Microsoft.Windows.ShellExperienceHost",
            "Microsoft.Windows.StartMenuExperienceHost",
            "Microsoft.Windows.XGpuEjectDialog",
            "MicrosoftWindows.UndockedDevKit",
            "MicrosoftWindows.Client.CBS",
            "MicrosoftWindows.Client.Core",
            "NcsiUwpApp",
            "Windows.CBSPreview",
            "Microsoft.AccountsControl",
            "Microsoft.AsyncTextService",
            "Microsoft.CaptivePortalPage",
            "Microsoft.549981C3F5F10",
        };

        static bool IsSystemInternalPackage(string identityName)
        {
            if (string.IsNullOrEmpty(identityName)) return false;
            foreach (var prefix in _systemPackagePrefixes)
                if (identityName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        void CollectWin32Registry(List<AppEntry> entries)
        {
            var roots = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };
            foreach (var root in roots)
            {
                foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    try
                    {
                        using (var key = hive.OpenSubKey(root))
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
                                        if (ShouldSkipName(name)) continue;

                                        if ((sk.GetValue("SystemComponent") as string) == "1") continue;
                                        string rt = sk.GetValue("ReleaseType") as string ?? "";
                                        if (rt.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                        string pk = sk.GetValue("ParentKeyName") as string ?? "";
                                        if (pk.StartsWith("OperatingSystem", StringComparison.OrdinalIgnoreCase)) continue;

                                        string iconPath = null;
                                        string iconRaw = sk.GetValue("DisplayIcon") as string;
                                        if (!string.IsNullOrEmpty(iconRaw))
                                        {
                                            ParseIconLocation(iconRaw, out string ip, out _);
                                            if (!string.IsNullOrEmpty(ip) && File.Exists(ip))
                                                iconPath = ip;
                                        }

                                        string exePath = null;
                                        if (iconPath != null &&
                                            iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                            exePath = iconPath;

                                        if (exePath == null)
                                        {
                                            string loc = (sk.GetValue("InstallLocation") as string ?? "")
                                                .Trim('"').Trim();
                                            if (Directory.Exists(loc))
                                                exePath = FindMainExe(loc, name);
                                        }

                                        string launchTarget = exePath ?? iconPath;
                                        if (string.IsNullOrEmpty(launchTarget)) continue;

                                        entries.Add(new AppEntry
                                        {
                                            Name = CleanName(name),
                                            LaunchPath = launchTarget,
                                            IconPath = iconPath ?? exePath,
                                            Kind = AppKind.Win32,
                                        });
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        static string ResolveScaledIcon(string iconPath)
        {
            if (string.IsNullOrEmpty(iconPath)) return null;
            string dir = Path.GetDirectoryName(iconPath) ?? "";
            string noExt = Path.GetFileNameWithoutExtension(iconPath);
            string ext = Path.GetExtension(iconPath);
            foreach (var scale in new[] {
                ".scale-200", ".scale-150", ".scale-100",
                ".targetsize-256", ".targetsize-96", ".targetsize-48", ".targetsize-32", "" })
            {
                string p = Path.Combine(dir, noExt + scale + ext);
                if (File.Exists(p)) return p;
            }
            if (Directory.Exists(dir))
            {
                var f = Directory.GetFiles(dir, "*.png").FirstOrDefault();
                if (f != null) return f;
            }
            return null;
        }

        static string BuildPfn(string folderName, string identityName)
        {
            if (!string.IsNullOrEmpty(folderName))
            {
                var parts = folderName.Split('_');
                if (parts.Length >= 5)
                    return parts[0] + "_" + parts[parts.Length - 1];
                if (parts.Length >= 2)
                    return parts[0] + "_" + parts[parts.Length - 1];
            }
            return identityName;
        }

        static string FriendlyPkgName(string identityName)
        {
            if (string.IsNullOrEmpty(identityName)) return identityName;
            string s = identityName;
            int dot = s.LastIndexOf('.');
            if (dot >= 0) s = s.Substring(dot + 1);
            s = Regex.Replace(s, @"([a-z])([A-Z])", "$1 $2");
            s = Regex.Replace(s, @"([A-Z]+)([A-Z][a-z])", "$1 $2");
            return s.Trim();
        }

        static string FindMainExe(string installDir, string appName)
        {
            try
            {
                var exes = Directory.GetFiles(installDir, "*.exe", SearchOption.TopDirectoryOnly);
                if (exes.Length == 0) return null;
                if (exes.Length == 1) return exes[0];

                string appLow = appName.ToLowerInvariant().Split(' ')[0];
                foreach (var exe in exes)
                {
                    string en = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                    if (en == appLow || appLow.Contains(en) || en.Contains(appLow))
                        return exe;
                }
                return exes.OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                           .First();
            }
            catch { return null; }
        }

        static string GetKnownFolder(Guid folderId)
        {
            IntPtr pPath = IntPtr.Zero;
            try
            {
                int hr = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out pPath);
                if (hr == 0 && pPath != IntPtr.Zero)
                    return Marshal.PtrToStringUni(pPath);
            }
            catch { }
            finally { if (pPath != IntPtr.Zero) Marshal.FreeCoTaskMem(pPath); }
            return null;
        }

        static readonly Regex _guidRegex = new Regex(
            @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex _hexOnlyRegex = new Regex(
            @"^[0-9a-f\-]{20,}$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly HashSet<string> _skipWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "uninstall", "setup", "install", "installer", "help", "readme",
            "release notes", "what's new", "getting started", "documentation",
        };

        static readonly string[] _skipContains = new[]
        {
            // Системные брокеры и хосты
            "broker plugin", "broker host", "host process", "runtime broker",
            "background task", "background host", "surrogate host",
            "start menu experience", "experience host",
            "captive portal", "capture picker", "bio enrollment",
            "store purchase", "purchase app",
            "runtime component", "framework package",
            "desktop app installer", "app installer",
            "package manager", "package experience",
            "input host", "text input host",
            "credential ui", "user account control",
            "application frame host", "app host",
            "web experience pack",
            "migration ui", "migration host",
            "device enrollment", "enrollment manager",
            "secure assessment", "lock app",
            "print3d ui", "mixed reality",
            "narrator quickstart",
            // NVIDIA служебные компоненты
            "nvidia frameview",
            "nvidia localsystem container",
            "nvidia messagebus",
            "nvidia platform controllers",
            "nvidia session container",
            "nvidia install application",
            "nvidia nvdlisr",
            "nvidia shadowplay",
            "nvidia physx",
            "nvidia graphics driver",
            "nvidia hd audio",
            "nvidia usb driver",
            "nvidia network access manager",
            "nvidia nview",
            "nvidia backend",
            "nvidia container",
            "nvidia telemetry",
            "nvidia watchdog",
            "nvidia graphics driver",
            "nvcpl",
            // Кортана
            "cortana",
            // Ссылки и вспомогательные элементы
            "release notes",
            "support center",
            "webview support",
            "uxp webview",
            "visit ",       // "Visit Java.com" и подобные
            "zune music",
            "zune video",
            "your phone",
        };

        static bool IsServiceComponent(string nl)
        {
            if (Regex.IsMatch(nl, @"^nvidia .+ (sdk|container|framework|driver|component|service|helper|daemon)\b"))
                return true;
            if (Regex.IsMatch(nl, @"\d+\.\d+\.\d+\.\d+"))
                return true;
            if (Regex.IsMatch(nl, @"^nvidia .+\s\d{3,}\.\d+$"))
                return true;
            // Unity 6000.3.2f1 и подобные версии разработчика
            if (Regex.IsMatch(nl, @"\d{3,}\.\d+\.\d+[a-z]\d+$"))
                return true;
            return false;
        }

        static bool ShouldSkipName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            if (name.Trim().Length <= 1) return true;  // "." и одиночные символы
            if (name.Length > 100) return true;
            if (_guidRegex.IsMatch(name.Trim())) return true;
            if (_hexOnlyRegex.IsMatch(name.Trim())) return true;
            if (name.TrimStart().StartsWith("{")) return true;
            if (name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return true;
            if (Regex.IsMatch(name, @"_[a-z0-9]{13}$", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(name, @"^[\d\s\.\-_]+$")) return true;
            if (Regex.IsMatch(name, @"\.(ini|cfg|xml|txt|log|dat|json|yaml|toml)$", RegexOptions.IgnoreCase)) return true;

            string nl = name.ToLowerInvariant();

            if (_skipWords.Any(w => nl == w)) return true;
            if (Regex.IsMatch(nl, @"^uninstall\s")) return true;
            if (Regex.IsMatch(nl, @"^деинсталлировать\s")) return true;  // русский uninstall
            if (Regex.IsMatch(nl, @"^запустить\s")) return true;          // "Запустить X"
            if (Regex.IsMatch(nl, @"^launch\s")) return true;             // "Launch X"
            if (Regex.IsMatch(nl, @"^run\s")) return true;                // "Run X"
            if (Regex.IsMatch(nl, @"^open\s")) return true;               // "Open X"
            if (Regex.IsMatch(nl, @"^visit\s")) return true;              // "Visit Java.com"
            if (nl.Contains("redistributable")) return true;
            if (Regex.IsMatch(nl, @"microsoft visual c\+\+ \d")) return true;
            if (Regex.IsMatch(nl, @"microsoft \.net \w+ \d")) return true;

            if (IsServiceComponent(nl)) return true;

            foreach (var skip in _skipContains)
                if (nl.Contains(skip)) return true;

            return false;
        }

        static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            name = Regex.Replace(name, @"\s*\(64.?bit\)", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s*\(x64\)", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s*\(x86\)", "", RegexOptions.IgnoreCase);
            return name.Trim();
        }

        static string ExtractXmlValue(string xml, string tag)
        {
            try
            {
                string open = "<" + tag + ">";
                string close = "</" + tag + ">";
                int i = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return null;
                i += open.Length;
                int j = xml.IndexOf(close, i, StringComparison.OrdinalIgnoreCase);
                if (j < 0) return null;
                return xml.Substring(i, j - i).Trim();
            }
            catch { return null; }
        }

        static string ExtractXmlAttr(string xml, string element, string attr)
        {
            try
            {
                int ei = xml.IndexOf("<" + element + " ", StringComparison.OrdinalIgnoreCase);
                if (ei < 0) return null;
                int end = xml.IndexOf('>', ei);
                if (end < 0) return null;
                string tag = xml.Substring(ei, end - ei);
                int ai = tag.IndexOf(attr + "=\"", StringComparison.OrdinalIgnoreCase);
                if (ai < 0) return null;
                ai += attr.Length + 2;
                int ae = tag.IndexOf('"', ai);
                if (ae < 0) return null;
                return tag.Substring(ai, ae - ai);
            }
            catch { return null; }
        }

        static void ParseIconLocation(string s, out string path, out int index)
        {
            s = (s ?? "").Trim('"');
            int comma = s.LastIndexOf(',');
            if (comma > 0 && int.TryParse(s.Substring(comma + 1), out index))
                path = s.Substring(0, comma).Trim('"');
            else { path = s; index = 0; }
        }
    }
}