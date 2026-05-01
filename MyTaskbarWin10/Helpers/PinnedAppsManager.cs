using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Xml.Serialization;

namespace MyTaskbar.Helpers
{
    public class PinnedApp
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Tooltip { get; set; }
        public PinnedApp() { }
        public PinnedApp(string name, string path, string tooltip)
        { Name = name; Path = path; Tooltip = tooltip; }
    }

    public static class PinnedAppsManager
    {
        private static readonly string SavePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MyTaskbar", "pinned.xml");

        // ── СОХРАНЕНИЕ ──────────────────────────────────────────────────

        public static void Save(List<PinnedApp> apps)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(SavePath);

                // Создаём папку если не существует
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var s = new XmlSerializer(typeof(List<PinnedApp>));

                // Пишем во временный файл, потом атомарно заменяем —
                // чтобы не испортить сохранение при сбое посередине записи
                string tmp = SavePath + ".tmp";
                using (var w = new StreamWriter(tmp, false, System.Text.Encoding.UTF8))
                    s.Serialize(w, apps);

                // Заменяем основной файл
                if (File.Exists(SavePath))
                    File.Delete(SavePath);
                File.Move(tmp, SavePath);

                Debug.WriteLine($"[PinnedAppsManager] Saved {apps.Count} apps → {SavePath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PinnedAppsManager] ERROR Save: {ex}");
            }
        }

        // ── ЗАГРУЗКА ─────────────────────────────────────────────────────

        public static List<PinnedApp> Load()
        {
            try
            {
                if (!File.Exists(SavePath))
                {
                    Debug.WriteLine($"[PinnedAppsManager] File not found, creating default: {SavePath}");
                    var def = GetDefaults();
                    Save(def);
                    return def;
                }

                var s = new XmlSerializer(typeof(List<PinnedApp>));
                List<PinnedApp> loaded;
                using (var r = new StreamReader(SavePath, System.Text.Encoding.UTF8))
                    loaded = (List<PinnedApp>)s.Deserialize(r);

                // Убираем записи с пустым путём
                loaded.RemoveAll(a => string.IsNullOrWhiteSpace(a?.Path));

                Debug.WriteLine($"[PinnedAppsManager] Loaded {loaded.Count} apps from {SavePath}");
                foreach (var a in loaded)
                    Debug.WriteLine($"  · {a.Name} | {a.Path}");

                return loaded;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PinnedAppsManager] ERROR Load: {ex}");
                return GetDefaults();
            }
        }

        // ── ДЕФОЛТ (только Проводник) ────────────────────────────────────

        private static List<PinnedApp> GetDefaults()
        {
            var list = new List<PinnedApp>();

            string explorerPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe");

            if (!File.Exists(explorerPath))
                explorerPath = "explorer.exe";

            list.Add(new PinnedApp("explorer", explorerPath, "Explorer"));
            return list;
        }

        // ── ПОИСК EXE ────────────────────────────────────────────────────

        public static string FindApp(string exeName)
        {
            return IconHelper.FindExeByName(exeName) ?? exeName;
        }
    }
}