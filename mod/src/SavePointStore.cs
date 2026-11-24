using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TygMod
{
    internal sealed class SavePointInfo
    {
        public string Dir;       // the save point folder (what the user sees)
        public string DataDir;   // folder that holds the .es3 files (Dir or Dir\Trust Your Gut)
        public string Name;
        public string Kind;      // save point / auto / backup
        public DateTime When;
        public bool HasRun;
        public int Day = -1;
        public int Round = -1;
        public string Character;
        public bool Endless;
        public double Cash = -1;
        public bool? Tainted;
        public string ReadError;
        public string Label;     // name the player gave it (label.txt), or null
        public int Pin;          // pin slot 1-4 (pin.txt), or 0
        public SaveDetails Details; // items in run order (mementos, balls, ...), or null when the run file was not read

        // The name, or the date when it has no name.
        public string Title()
        {
            if (!string.IsNullOrEmpty(Label)) return Label;
            return When == DateTime.MinValue ? Name : When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public string Summary()
        {
            if (!HasRun) return Kind + "  (no run file)";
            var sb = new StringBuilder();
            sb.Append("Day ").Append(Day < 0 ? "?" : Day.ToString());
            if (Round >= 0) sb.Append(" r").Append(Round);
            if (Endless) sb.Append(" endless");
            sb.Append("  ").Append(SavePointStore.CharacterName(Character));
            if (Cash >= 0) sb.Append("  $").Append(SavePointStore.Short(Cash));
            if (ReadError != null) sb.Append("  (info: ").Append(ReadError).Append(')');
            sb.Append("  [").Append(Kind).Append(']');
            return sb.ToString();
        }
    }

    internal static class SavePointStore
    {
        public const string RunFile = "Save_Game.es3";
        public const string PersistentFile = "Save_Persistent.es3";
        public const string CharacterFile = "Save_CharacterSelect.es3";

        private static readonly Dictionary<string, SavePointInfo> Cache = new Dictionary<string, SavePointInfo>();
        private static Dictionary<string, string> _charNames;

        public static string SaveDir => Application.persistentDataPath;

        public static string Root
        {
            get
            {
                string cfg = Plugin.BackupFolder != null ? Plugin.BackupFolder.Value : null;
                if (!string.IsNullOrWhiteSpace(cfg)) return cfg.Trim();
                return Path.Combine(GuessHome(), "tyg-backup");
            }
        }

        public static string ProofDir
        {
            get
            {
                string cfg = Plugin.ProofFolder != null ? Plugin.ProofFolder.Value : null;
                if (!string.IsNullOrWhiteSpace(cfg)) return cfg.Trim();
                return Path.Combine(Root, "board-proof");
            }
        }

        private static string GuessHome()
        {
            // Wine gives the Mac home as \??\Z:\Users\name
            string w = Environment.GetEnvironmentVariable("WINEHOMEDIR");
            if (!string.IsNullOrEmpty(w)) return w.Replace(@"\??\", "");
            string h = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(h) && h.StartsWith("/")) return "Z:" + h.Replace('/', '\\');
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        // ---------- game save before a copy ----------

        // 1. Write the saves the game has pending (what it does when its window loses focus; also the menu's
        //    unlock/stat saves).
        // 2. In a run (alive, wheel not spinning): the full save the pause menu's "Quit to menu" makes (SaveAll).
        //    Step 1 alone is not enough: placing chips on the table does not ask the game to save, so a save
        //    point made after betting held the run from before the bets (found 2026-11-23).
        public static void FlushGame()
        {
            SaveManager sm = SaveManager.Instance;
            SaveGate.FlushLikeFocusLoss(sm);
            SaveGate.SaveLikeQuitToMenu(sm);
        }

        // ---------- make ----------

        public static SavePointInfo Make(string kind)
        {
            FlushGame();
            string root = Root;
            Directory.CreateDirectory(root);
            string prefix = kind == "auto" ? "auto-" : "sp-";
            string name = prefix + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string final = Path.Combine(root, name);
            for (int i = 2; Directory.Exists(final); i++) final = Path.Combine(root, name + "-" + i);
            string tmp = Path.Combine(root, ".tmp-" + Path.GetFileName(final));
            string data = Path.Combine(tmp, "Trust Your Gut");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(data);

            int copied = 0;
            foreach (string src in Directory.GetFiles(SaveDir))
            {
                string fn = Path.GetFileName(src);
                string lower = fn.ToLowerInvariant();
                if (lower.EndsWith(".log") || lower.EndsWith(".write") || lower.EndsWith(".tmp") || lower.EndsWith(".tygrestore")) continue;
                string dst = Path.Combine(data, fn);
                File.Copy(src, dst, true);
                if (!SameFile(src, dst)) throw new IOException("Copy check failed for " + fn);
                copied++;
            }
            if (copied == 0) throw new IOException("No save files found in " + SaveDir);

            SavePointInfo info = Read(tmp, data, Path.GetFileName(final));
            File.WriteAllText(Path.Combine(tmp, "info.txt"),
                "made_by=TYG mod " + Plugin.Version + "\n" +
                "kind=" + kind + "\n" +
                "time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\n" +
                "summary=" + info.Summary() + "\n" +
                "files=" + copied + "\n" +
                "restore=use the mod window (F8 or the Save points (mod) button)\n");
            Directory.Move(tmp, final);
            Cache.Clear();
            SavePointInfo done = Read(final, Path.Combine(final, "Trust Your Gut"), Path.GetFileName(final));
            Plugin.Log.LogInfo("Save point made: " + final + "  " + done.Summary());
            return done;
        }

        // ---------- list ----------

        public static List<SavePointInfo> List()
        {
            var list = new List<SavePointInfo>();
            string root = Root;
            if (!Directory.Exists(root)) return list;
            foreach (string dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith(".")) continue;
                if (SavePointMeta.IsSameOrInside(SaveDir, dir)) continue; // BackupFolder set around the live save folder
                string data = null;
                if (HasSaveFiles(dir)) data = dir;
                else if (HasSaveFiles(Path.Combine(dir, "Trust Your Gut"))) data = Path.Combine(dir, "Trust Your Gut");
                if (data == null) continue;
                SavePointInfo info;
                try
                {
                    info = ReadCached(dir, data, name);
                }
                catch (Exception e)
                {
                    info = new SavePointInfo { Dir = dir, DataDir = data, Name = name, Kind = KindOf(name), When = WhenOf(name, dir), ReadError = e.Message };
                }
                // Name and pin change without a change to the run file, so read them fresh every time (two tiny files).
                info.Label = SavePointMeta.ReadLabel(dir);
                info.Pin = SavePointMeta.ReadPin(dir);
                list.Add(info);
            }
            // Pinned first (slot 1-4), then newest first.
            list.Sort((a, b) =>
            {
                bool pa = a.Pin > 0, pb = b.Pin > 0;
                if (pa != pb) return pa ? -1 : 1;
                if (pa && a.Pin != b.Pin) return a.Pin.CompareTo(b.Pin);
                int t = b.When.CompareTo(a.When);
                return t != 0 ? t : string.CompareOrdinal(a.Name, b.Name);
            });
            return list;
        }

        // ---------- name, pin, delete (the file work is in SavePointMeta) ----------

        public static string Rename(SavePointInfo sp, string text)
        {
            string label = SavePointMeta.SetLabel(Root, sp.Dir, text);
            sp.Label = label;
            Plugin.Log.LogInfo("Save point " + sp.Name + (label == null ? ": name removed" : " named: " + label));
            return label;
        }

        // Returns the slot (1-4), or 0 when all slots are used.
        public static int Pin(SavePointInfo sp)
        {
            int slot = SavePointMeta.Pin(Root, sp.Dir);
            if (slot > 0) sp.Pin = slot;
            Plugin.Log.LogInfo("Save point " + sp.Name + (slot > 0 ? " pinned to slot " + slot : ": pin refused, all slots used"));
            return slot;
        }

        public static void Unpin(SavePointInfo sp)
        {
            SavePointMeta.Unpin(Root, sp.Dir);
            sp.Pin = 0;
            Plugin.Log.LogInfo("Save point " + sp.Name + " unpinned");
        }

        // Moves the folder into <backup folder>\.trash. Returns the new path.
        public static string Delete(SavePointInfo sp)
        {
            if (SavePointMeta.IsSameOrInside(SaveDir, sp.Dir)) throw new InvalidOperationException("That folder holds the game's live save.");
            string dst = SavePointMeta.MoveToTrash(Root, sp.Dir);
            Cache.Clear();
            Plugin.Log.LogInfo("Save point " + sp.Name + " moved to " + dst);
            return dst;
        }

        private static bool HasSaveFiles(string dir)
        {
            return Directory.Exists(dir) && (File.Exists(Path.Combine(dir, RunFile)) || File.Exists(Path.Combine(dir, PersistentFile)));
        }

        private static SavePointInfo ReadCached(string dir, string data, string name)
        {
            string run = Path.Combine(data, RunFile);
            string key = dir + "|" + (File.Exists(run) ? new FileInfo(run).Length : 0);
            if (Cache.TryGetValue(key, out SavePointInfo hit)) return hit;
            SavePointInfo info = Read(dir, data, name);
            if (info.ReadError == null) Cache[key] = info; // a failed read is tried again on the next refresh
            return info;
        }

        private static string KindOf(string name)
        {
            if (name.StartsWith("sp-")) return "save point";
            if (name.StartsWith("auto-")) return "auto, before restore";
            return "backup";
        }

        private static DateTime WhenOf(string name, string dir)
        {
            Match m = Regex.Match(name, @"(\d{8})-(\d{4,6})");
            if (m.Success)
            {
                string t = m.Groups[2].Value;
                string fmt = t.Length == 6 ? "yyyyMMddHHmmss" : "yyyyMMddHHmm";
                if (DateTime.TryParseExact(m.Groups[1].Value + t, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt)) return dt;
            }
            return DateTime.MinValue;
        }

        public static SavePointInfo Read(string dir, string data, string name)
        {
            var info = new SavePointInfo { Dir = dir, DataDir = data, Name = name, Kind = KindOf(name), When = WhenOf(name, dir) };
            string run = Path.Combine(data, RunFile);
            info.HasRun = File.Exists(run);
            if (!info.HasRun) return info;
            try
            {
                string json = Decrypt(File.ReadAllBytes(run));
                info.Day = Int(json, "RoundManagerData_currentDay");
                info.Round = Int(json, "RoundManagerData_currentRound");
                info.Character = Str(json, "CharacterBodyPartsData_character");
                info.Endless = Bool(json, "RoundManagerData_endless") == true;
                info.Cash = Dbl(json, "PlayerChipInventory_currency");
                info.Tainted = Bool(json, "RunGameStats_tainted");
                info.Details = SaveDetails.Parse(json);
            }
            catch (Exception e)
            {
                info.ReadError = e.GetType().Name;
                Plugin.Log.LogWarning("Cannot read save point " + name + ": " + e.GetType().Name + ": " + e.Message);
            }
            return info;
        }

        // ---------- restore ----------

        // Copies the save point files into the game save folder. Run only = Save_Game.es3.
        // Full = also Save_Persistent.es3 and Save_CharacterSelect.es3 (unlocks, stats, last character).
        public static void RestoreFiles(SavePointInfo sp, bool full)
        {
            var names = new List<string> { RunFile };
            if (full)
            {
                names.Add(PersistentFile);
                names.Add(CharacterFile);
            }
            foreach (string fn in names)
            {
                string src = Path.Combine(sp.DataDir, fn);
                if (!File.Exists(src))
                {
                    if (fn == RunFile) throw new FileNotFoundException("The save point has no run file.", src);
                    continue;
                }
                string dst = Path.Combine(SaveDir, fn);
                string tmp = dst + ".tygrestore";
                File.Copy(src, tmp, true);
                if (!SameFile(src, tmp)) throw new IOException("Copy check failed for " + fn);
                if (File.Exists(dst)) File.Replace(tmp, dst, null); // one step: the old file is never missing
                else File.Move(tmp, dst);
                if (!SameFile(src, dst)) throw new IOException("Restore check failed for " + fn);
                Plugin.Log.LogInfo("Restored " + fn + " from " + sp.Dir);
            }
        }

        // True when the live run file is byte-identical to the save point's run file.
        public static bool RunFileMatches(SavePointInfo sp)
        {
            try
            {
                return SameFile(Path.Combine(sp.DataDir, RunFile), Path.Combine(SaveDir, RunFile));
            }
            catch (Exception)
            {
                return true; // unknown: keep the practice marker
            }
        }

        // ---------- helpers ----------

        private static bool SameFile(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!File.Exists(a) || !File.Exists(b) || fa.Length != fb.Length) return false;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] ha = sha.ComputeHash(File.ReadAllBytes(a));
                byte[] hb = sha.ComputeHash(File.ReadAllBytes(b));
                for (int i = 0; i < ha.Length; i++)
                {
                    if (ha[i] != hb[i]) return false;
                }
            }
            return true;
        }

        // The game opens its saves with Easy Save 3's default settings (new ES3File("Save_Game.es3")), so the key
        // comes from the game's own ES3 defaults at run time. The mod holds no key. No key = this save shows as
        // unreadable in the list (Read catches the exception).
        private static string Password()
        {
            string p = ES3Settings.defaultSettings.encryptionPassword;
            if (string.IsNullOrEmpty(p)) throw new IOException("the game's save key was not found");
            return p;
        }

        // Read only. Easy Save 3 AES file: 16 byte IV, then AES-128-CBC; key = PBKDF2-SHA1(password, IV, 100).
        public static string Decrypt(byte[] data)
        {
            if (data.Length < 32) throw new IOException("file too short");
            byte[] iv = new byte[16];
            Buffer.BlockCopy(data, 0, iv, 0, 16);
            byte[] key;
            using (var kdf = new Rfc2898DeriveBytes(Password(), iv, 100))
            {
                key = kdf.GetBytes(16);
            }
            byte[] plain;
            using (var aes = new RijndaelManaged { Mode = CipherMode.CBC, Padding = PaddingMode.PKCS7, KeySize = 128, BlockSize = 128, Key = key, IV = iv })
            using (ICryptoTransform dec = aes.CreateDecryptor())
            {
                plain = dec.TransformFinalBlock(data, 16, data.Length - 16);
            }
            if (plain.Length > 2 && plain[0] == 0x1f && plain[1] == 0x8b)
            {
                using (var ms = new MemoryStream(plain))
                using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                using (var outMs = new MemoryStream())
                {
                    gz.CopyTo(outMs);
                    plain = outMs.ToArray();
                }
            }
            return Encoding.UTF8.GetString(plain);
        }

        private static string Raw(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\{\\s*\"__type\"\\s*:\\s*\"[^\"]*\"\\s*,\\s*\"value\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|[^,}\\s]+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static int Int(string json, string key)
        {
            string r = Raw(json, key);
            return r != null && int.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;
        }

        private static double Dbl(string json, string key)
        {
            string r = Raw(json, key);
            return r != null && double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : -1;
        }

        private static bool? Bool(string json, string key)
        {
            string r = Raw(json, key);
            if (r == "true") return true;
            if (r == "false") return false;
            return null;
        }

        private static string Str(string json, string key)
        {
            string r = Raw(json, key);
            if (r == null || r.Length < 2 || r[0] != '"') return null;
            return r.Substring(1, r.Length - 2);
        }

        public static string Short(double v)
        {
            if (v >= 1e9) return (v / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "B";
            if (v >= 1e6) return (v / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (v >= 1e3) return (v / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            return v.ToString("0", CultureInfo.InvariantCulture);
        }

        public static string CharacterName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "?";
            if (_charNames == null)
            {
                _charNames = new Dictionary<string, string>();
                try
                {
                    foreach (CharacterData cd in Resources.FindObjectsOfTypeAll<CharacterData>())
                    {
                        if (cd == null || string.IsNullOrEmpty(cd.Id)) continue;
                        string n = null;
                        try
                        {
                            n = cd.NameKey.Value;
                        }
                        catch (Exception)
                        {
                        }
                        if (!string.IsNullOrEmpty(n) && !n.StartsWith("#")) _charNames[cd.Id] = n;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Character names: " + e.Message);
                }
                if (_charNames.Count == 0) _charNames = null; // try again later (assets not loaded yet)
            }
            return _charNames != null && _charNames.TryGetValue(id, out string name) ? name : id;
        }
    }
}
