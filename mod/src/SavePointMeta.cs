using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TygMod
{
    // Name, pin and delete for save points. Plain .NET only (no Unity, no Plugin), so tools/meta-test can run it
    // outside the game.
    // The data sits next to the save files, never inside them: label.txt (the name) and pin.txt (the slot, 1-4)
    // in the save point folder. A restore copies only the named .es3 files, so these files never reach the game.
    internal static class SavePointMeta
    {
        public const int MaxPins = 4;
        public const int MaxLabelLength = 60;
        public const string LabelFile = "label.txt";
        public const string PinFile = "pin.txt";
        public const string TrashFolder = ".trash"; // the list skips folders whose name starts with a dot

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        // ---------- name ----------

        // The name, or null when there is none. A name that cannot be read counts as no name (it is only a label).
        public static string ReadLabel(string dir)
        {
            string p = Path.Combine(dir, LabelFile);
            try
            {
                if (!File.Exists(p)) return null;
                string s = Clean(Utf8.GetString(File.ReadAllBytes(p))); // File.ReadAllText(path, encoding) is stripped in this game
                return s.Length == 0 ? null : s;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        // One line, no control characters, single spaces, at most MaxLabelLength characters.
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text)
            {
                if (c == '\uFEFF') continue; // byte order mark from a hand edit
                if (char.IsControl(c) || char.IsWhiteSpace(c))
                {
                    space = sb.Length > 0;
                    continue;
                }
                if (space) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            string s = sb.ToString();
            if (s.Length > MaxLabelLength)
            {
                int cut = MaxLabelLength;
                if (char.IsHighSurrogate(s[cut - 1])) cut--; // do not split a two-part character
                s = s.Substring(0, cut).TrimEnd();
            }
            return s;
        }

        // Sets the name. An empty name removes it. Returns the name as saved (null when removed).
        public static string SetLabel(string root, string dir, string text)
        {
            dir = RequireSavePointFolder(root, dir);
            string s = Clean(text);
            string p = Path.Combine(dir, LabelFile);
            if (s.Length == 0)
            {
                if (File.Exists(p)) File.Delete(p);
                return null;
            }
            WriteSmall(p, s + "\n");
            return s;
        }

        // ---------- pin ----------

        // The pin slot (1-4), or 0 when not pinned. A pin file that cannot be read counts as not pinned.
        public static int ReadPin(string dir)
        {
            string p = Path.Combine(dir, PinFile);
            try
            {
                if (!File.Exists(p)) return 0;
                return int.TryParse(Utf8.GetString(File.ReadAllBytes(p)).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                       && n >= 1 && n <= MaxPins ? n : 0;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }

        // Pins the save point to the lowest free slot. Returns the slot, or 0 when all MaxPins slots are used.
        // A save point that is already pinned keeps its slot.
        public static int Pin(string root, string dir)
        {
            dir = RequireSavePointFolder(root, dir);
            int have = ReadPin(dir);
            if (have > 0) return have;
            var used = new bool[MaxPins + 1];
            foreach (string d in Directory.GetDirectories(root))
            {
                if (Path.GetFileName(d).StartsWith(".", StringComparison.Ordinal)) continue;
                int n = ReadPin(d);
                if (n > 0) used[n] = true;
            }
            for (int slot = 1; slot <= MaxPins; slot++)
            {
                if (used[slot]) continue;
                WriteSmall(Path.Combine(dir, PinFile), slot.ToString(CultureInfo.InvariantCulture) + "\n");
                return slot;
            }
            return 0;
        }

        public static void Unpin(string root, string dir)
        {
            dir = RequireSavePointFolder(root, dir);
            string p = Path.Combine(dir, PinFile);
            if (File.Exists(p)) File.Delete(p);
        }

        // ---------- delete ----------

        // Delete = move the folder into <root>/.trash. Nothing is erased; empty .trash by hand to free disk space.
        // A pinned save point is refused (unpin it first). Returns the new folder path.
        public static string MoveToTrash(string root, string dir)
        {
            dir = RequireSavePointFolder(root, dir);
            if (ReadPin(dir) > 0) throw new InvalidOperationException("It is pinned. Unpin it first.");
            string trash = Path.Combine(root, TrashFolder);
            Directory.CreateDirectory(trash);
            string name = Path.GetFileName(dir);
            string dst = Path.Combine(trash, name);
            for (int i = 2; Directory.Exists(dst) || File.Exists(dst); i++) dst = Path.Combine(trash, name + "-" + i);
            Directory.Move(dir, dst);
            return dst;
        }

        // ---------- helpers ----------

        // Only a direct child folder of the backup folder, not a dot folder, and it must exist.
        // Returns the full path (no "..", no end slash); callers work on that path only.
        public static string RequireSavePointFolder(string root, string dir)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("No save point folder given.");
            string r = TrimEnd(Path.GetFullPath(root));
            string d = TrimEnd(Path.GetFullPath(dir));
            string parent = Path.GetDirectoryName(d);
            string name = Path.GetFileName(d);
            if (parent == null || name.Length == 0 || name.StartsWith(".", StringComparison.Ordinal) ||
                !string.Equals(TrimEnd(parent), r, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Not a save point folder: " + dir);
            }
            if (!Directory.Exists(d)) throw new DirectoryNotFoundException("The save point folder is gone: " + dir);
            return d;
        }

        // True when 'inner' is 'outer' or a folder inside it.
        public static bool IsSameOrInside(string inner, string outer)
        {
            if (string.IsNullOrWhiteSpace(inner) || string.IsNullOrWhiteSpace(outer)) return false;
            string i = TrimEnd(Path.GetFullPath(inner));
            string o = TrimEnd(Path.GetFullPath(outer));
            return string.Equals(i, o, StringComparison.OrdinalIgnoreCase) ||
                   i.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase) ||
                   i.StartsWith(o + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string TrimEnd(string path)
        {
            return path.TrimEnd('\\', '/');
        }

        // Write to a temp file first, then swap it in, so a crash never leaves half a file.
        private static void WriteSmall(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, Utf8);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
