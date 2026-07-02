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

                // [MIGRATION] Заменяем русский тултип Проводника на английский
                foreach (var a in loaded)
                {
                    if (a != null &&
                        string.Equals(System.IO.Path.GetFileNameWithoutExtension(a.Path), "explorer", StringComparison.OrdinalIgnoreCase) &&
                        (a.Tooltip == "Проводник" || a.Name == "Проводник"))
                    {
                        if (a.Tooltip == "Проводник") a.Tooltip = "File Explorer";
                        if (a.Name == "Проводник") a.Name = "explorer";
                        Debug.WriteLine("[PinnedAppsManager] Migrated explorer tooltip RU→EN");
                    }
                }

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

        // ── ОБНОВЛЕНИЕ ПУТЕЙ ДЛЯ VERSIONED-ПРИЛОЖЕНИЙ ───────────────────
        //
        // Вызывается один раз при старте, до Load().
        // Для каждого закреплённого приложения из «versioned» списка
        // (Discord, Slack, Postman, GitKraken…) проверяет, существует ли
        // путь из pinned.xml. Если нет — ищет актуальный exe через
        // FindLatestElectronExe (тот же алгоритм, что IconHelper) и,
        // если нашёл что-то новое, обновляет запись и перезаписывает файл.
        //
        // Добавить поддержку нового приложения = добавить одну строку в
        // VersionedApps ниже.

        public static void RefreshVersionedPaths()
        {
            if (!File.Exists(SavePath)) return;   // нечего обновлять
            try
            {
                var apps = Load();
                bool dirty = false;

                foreach (var app in apps)
                {
                    if (app == null || string.IsNullOrWhiteSpace(app.Path)) continue;

                    // Путь существует — всё хорошо
                    if (File.Exists(app.Path)) continue;

                    // Путь содержит versioned-папку (app-X.Y.Z) — пробуем обновить
                    string exeFile = System.IO.Path.GetFileName(app.Path);
                    string baseDir = FindVersionedBaseDir(app.Path);
                    if (baseDir == null) continue;

                    string fresh = FindLatestElectronExe(baseDir, exeFile);
                    if (fresh != null && !string.Equals(fresh, app.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"[PinnedAppsManager] RefreshVersionedPaths: {app.Name}");
                        Debug.WriteLine($"  old: {app.Path}");
                        Debug.WriteLine($"  new: {fresh}");
                        app.Path = fresh;
                        dirty = true;
                    }
                }

                if (dirty) Save(apps);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PinnedAppsManager] ERROR RefreshVersionedPaths: {ex}");
            }
        }

        // Возвращает базовую папку, если путь содержит сегмент вида app-X.Y.Z,
        // иначе null.
        // Пример: "…\Discord\app-1.0.9238\Discord.exe" → "…\Discord"
        private static string FindVersionedBaseDir(string exePath)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(exePath);
                if (string.IsNullOrEmpty(dir)) return null;

                string folder = System.IO.Path.GetFileName(dir);
                if (folder != null &&
                    folder.StartsWith("app-", StringComparison.OrdinalIgnoreCase) &&
                    Version.TryParse(folder.Substring(4), out _))
                {
                    return System.IO.Path.GetDirectoryName(dir);  // родитель app-X.Y.Z
                }
            }
            catch { }
            return null;
        }

        // Ищет самую новую папку app-X.Y.Z внутри baseDir и возвращает
        // путь к exeFileName в ней (или null если не нашёл).
        private static string FindLatestElectronExe(string baseDir, string exeFileName)
        {
            if (!Directory.Exists(baseDir)) return null;
            try
            {
                Version latestVer = null;
                string latestDir = null;
                foreach (var dir in Directory.GetDirectories(baseDir, "app-*"))
                {
                    string verStr = System.IO.Path.GetFileName(dir).Substring(4);
                    if (Version.TryParse(verStr, out Version v) && (latestVer == null || v > latestVer))
                    { latestVer = v; latestDir = dir; }
                }
                if (latestDir != null)
                {
                    string p = System.IO.Path.Combine(latestDir, exeFileName);
                    if (File.Exists(p)) return p;
                }
                // Fallback: exe прямо в baseDir (некоторые установщики кладут его туда)
                string direct = System.IO.Path.Combine(baseDir, exeFileName);
                if (File.Exists(direct)) return direct;
            }
            catch { }
            return null;
        }
    }
}
