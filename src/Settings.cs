using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Discphew
{
    /// <summary>Tiny key=value settings file in %APPDATA%\discphew. Nothing goes in the registry.</summary>
    public static class Settings
    {
        static readonly object gate = new object();
        static Dictionary<string, string> values;

        public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discphew");
        static string FilePath => Path.Combine(Folder, "settings.ini");

        static void Load()
        {
            if (values != null) return;
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
                }
            }
            catch { }
        }

        public static string Get(string key, string fallback = "")
        {
            lock (gate)
            {
                Load();
                return values.TryGetValue(key, out var v) ? v : fallback;
            }
        }

        public static bool GetBool(string key, bool fallback) => Get(key, fallback ? "1" : "0") == "1";
        public static int GetInt(string key, int fallback) => int.TryParse(Get(key), out var v) ? v : fallback;

        public static void Set(string key, string value)
        {
            lock (gate)
            {
                Load();
                values[key] = (value ?? "").Replace("\r", " ").Replace("\n", " ");
                try
                {
                    Directory.CreateDirectory(Folder);
                    File.WriteAllLines(FilePath, values.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value), Encoding.UTF8);
                }
                catch { }
            }
        }

        public static void Set(string key, bool value) => Set(key, value ? "1" : "0");
        public static void Set(string key, int value) => Set(key, value.ToString());
    }
}
