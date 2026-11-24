using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TygMod
{
    // Log file <game>\TygMod\TygMod.log (the last run is kept as TygMod-prev.log).
    // After the mod starts, lines also go to the Unity Player.log with a [TygMod] prefix.
    internal sealed class ModLog
    {
        private static readonly object Gate = new object();
        private static StreamWriter _writer;
        private static string _path;
        // Set by Plugin.Init once Unity runs; this file never touches UnityEngine before that.
        internal static Action<string, string> UnitySink;

        public static void Open(string path)
        {
            lock (Gate)
            {
                _path = path;
                try
                {
                    if (File.Exists(path))
                    {
                        string prev = Path.Combine(Path.GetDirectoryName(path), "TygMod-prev.log");
                        File.Copy(path, prev, true);
                    }
                }
                catch (Exception)
                {
                    // keep going with a fresh log
                }
                _writer = new StreamWriter(path, false, new UTF8Encoding(false));
                _writer.AutoFlush = true;
            }
        }

        // Last resort when the logger itself is not up.
        public static void Emergency(string msg)
        {
            try
            {
                string p = _path ?? Path.Combine(Path.GetTempPath(), "TygMod-boot-error.log");
                File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " [Fatal] " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception)
            {
                // nothing else to do
            }
        }

        public void LogInfo(string msg) => Write("Info ", msg);
        public void LogWarning(string msg) => Write("Warn ", msg);
        public void LogError(string msg) => Write("Error", msg);

        private static void Write(string level, string msg)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + level + "] " + msg;
            lock (Gate)
            {
                try
                {
                    if (_writer != null) _writer.WriteLine(line);
                }
                catch (Exception)
                {
                    // a full disk must not break the game
                }
            }
            Action<string, string> sink = UnitySink;
            if (sink != null) sink(level, msg);
        }
    }

    internal sealed class Setting<T>
    {
        public T Value;
        public readonly T Default;
        public readonly string Section;
        public readonly string Key;
        public readonly string Description;

        public Setting(string section, string key, T def, string description, T value)
        {
            Section = section;
            Key = key;
            Default = def;
            Description = description;
            Value = value;
        }
    }

    // Small ini file: [Section] then "Key = value". Comments start with #. Save() writes only the bound keys
    // (with their descriptions); keys or comments added by hand are dropped on the next start.
    internal static class ModConfig
    {
        private static string _path;
        private static readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        private static readonly List<string[]> Bound = new List<string[]>(); // section, key, description, default

        public static void Load(string path)
        {
            _path = path;
            Values.Clear();
            Bound.Clear();
            if (!File.Exists(path)) return;
            string section = "";
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                Values[section + "." + line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        }

        public static Setting<T> Bind<T>(string section, string key, T def, string description)
        {
            T value = def;
            if (Values.TryGetValue(section + "." + key, out string text) && !TryParse(text, out value))
            {
                Plugin.Log.LogWarning("Setting " + section + "." + key + " = '" + text + "' is not valid, using " + Format(def));
                value = def;
            }
            Values[section + "." + key] = Format(value);
            Bound.Add(new[] { section, key, description, Format(def) });
            return new Setting<T>(section, key, def, description, value);
        }

        // Writes the file with all known settings (keeps the user's values).
        public static void Save()
        {
            if (_path == null) return;
            var sb = new StringBuilder();
            sb.Append("# TYG mod settings. Edit with the game closed.\n");
            string last = null;
            foreach (string[] b in Bound)
            {
                if (b[0] != last)
                {
                    sb.Append('\n').Append('[').Append(b[0]).Append("]\n");
                    last = b[0];
                }
                sb.Append("\n# ").Append(b[2]).Append("\n# Default: ").Append(b[3]).Append('\n');
                sb.Append(b[1]).Append(" = ").Append(Values[b[0] + "." + b[1]]).Append('\n');
            }
            try
            {
                File.WriteAllText(_path, sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not write settings file: " + e.Message);
            }
        }

        private static bool TryParse<T>(string text, out T value)
        {
            object v = null;
            if (typeof(T) == typeof(string)) v = text;
            else if (typeof(T) == typeof(bool) && bool.TryParse(text, out bool b)) v = b;
            else if (typeof(T) == typeof(float) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) v = f;
            if (v == null)
            {
                value = default(T);
                return false;
            }
            value = (T)v;
            return true;
        }

        private static string Format<T>(T value)
        {
            if (value is float f) return f.ToString(CultureInfo.InvariantCulture);
            if (value is bool b) return b ? "true" : "false";
            return value == null ? "" : value.ToString();
        }
    }
}
