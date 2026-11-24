using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TygMod
{
    // What a run file holds besides day and money: the items in run order, for the save point list.
    // Plain .NET only (no Unity), so tools/meta-test can test it on real decrypted saves.
    // Read only: this never writes a save.
    internal sealed class SaveDetails
    {
        public readonly List<string> Mementos = new List<string>();     // memento slots, in slot order
        public readonly List<string> Balls = new List<string>();        // balls in the ball machine, in machine order
        public readonly List<string> BallSlots = new List<string>();    // ball inventory slots that hold a ball, in slot order
        public readonly List<string> Consumables = new List<string>();  // consumable slots that hold one
        public readonly List<string> Starters = new List<string>();     // starter items still owned
        public readonly List<string> CutParts = new List<string>();     // body parts cut off (game names, e.g. Left.Hand)
        public double Tokens = -1;     // the save stores it as a double
        public int BetLevel = -1;

        public int ItemCount => Mementos.Count + Balls.Count + BallSlots.Count + Consumables.Count + Starters.Count;

        // One pass over the JSON for the scalar keys of the item groups (each key is { "__type": ..., "value": ... }).
        private static readonly Regex Scalar = new Regex(
            "\"((?:PlayerCharmInventoryData|BallMachineData|ConsumableInventoryData|StarterItemInventoryData|PlayerChipInventory|BetLimitManager)_[A-Za-z_]*[0-9]*)\"" +
            "\\s*:\\s*\\{\\s*\"__type\"\\s*:\\s*\"[^\"]*\"\\s*,\\s*\"value\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|[^,}\\s]+)",
            RegexOptions.CultureInvariant);

        private static readonly Regex Parts = new Regex(
            "\"CharacterBodyPartsData_parts\"\\s*:\\s*\\{\\s*\"__type\"\\s*:\\s*\"[^\"]*\"\\s*,\\s*\"value\"\\s*:\\s*\\{([^{}]*)\\}",
            RegexOptions.CultureInvariant);

        private static readonly Regex PartTrue = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*true", RegexOptions.CultureInvariant);

        public static SaveDetails Parse(string json)
        {
            var d = new SaveDetails();
            if (string.IsNullOrEmpty(json)) return d;
            var v = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Scalar.Matches(json))
            {
                v[m.Groups[1].Value] = m.Groups[2].Value;
            }

            // Mementos: the game saves them sorted by slot as id_0 .. id_<count-1>. Keys past count are left over
            // from an earlier save (the save file keeps old keys), so count decides.
            ReadList(v, "PlayerCharmInventoryData_count", "PlayerCharmInventoryData_id_", d.Mementos);
            ReadList(v, "BallMachineData_active_total", "BallMachineData_active_id_", d.Balls);
            ReadList(v, "BallMachineData_invslot_total", "BallMachineData_invslot_id_", d.BallSlots);
            ReadList(v, "ConsumableInventoryData_total", "ConsumableInventoryData_id_", d.Consumables);
            ReadList(v, "StarterItemInventoryData_count", "StarterItemInventoryData_id_", d.Starters);
            d.Tokens = Num(v, "PlayerChipInventory_coin");
            d.BetLevel = Int(v, "BetLimitManager_level");

            Match p = Parts.Match(json);
            if (p.Success)
            {
                foreach (Match t in PartTrue.Matches(p.Groups[1].Value)) d.CutParts.Add(Unescape(t.Groups[1].Value));
            }
            return d;
        }

        private static void ReadList(Dictionary<string, string> v, string countKey, string idPrefix, List<string> into)
        {
            int n = Int(v, countKey);
            if (n <= 0) return;
            n = Math.Min(n, 200); // a damaged count must not make a huge loop
            for (int i = 0; i < n; i++)
            {
                if (!v.TryGetValue(idPrefix + i.ToString(CultureInfo.InvariantCulture), out string raw)) continue;
                string s = Str(raw);
                if (!string.IsNullOrEmpty(s)) into.Add(s); // empty = empty slot
            }
        }

        private static double Num(Dictionary<string, string> v, string key)
        {
            return v.TryGetValue(key, out string raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) &&
                   !double.IsNaN(n) && !double.IsInfinity(n) ? n : -1;
        }

        private static int Int(Dictionary<string, string> v, string key)
        {
            return v.TryGetValue(key, out string raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : -1;
        }

        private static string Str(string raw)
        {
            if (raw == null || raw.Length < 2 || raw[0] != '"') return null;
            return Unescape(raw.Substring(1, raw.Length - 2));
        }

        // JSON string escapes (\" \\ \/ \n \t \uXXXX ...).
        public static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(c);
                    continue;
                }
                char e = s[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        else sb.Append('u');
                        break;
                    default: sb.Append(e); break; // \" \\ \/
                }
            }
            return sb.ToString();
        }

        // "Left.Hand" -> "left hand", "Right.R_Eye" -> "right eye", "Left.PinkyTarget" -> "left pinky finger".
        public static string PartName(string part)
        {
            if (string.IsNullOrEmpty(part)) return part;
            string side = "", name = part;
            int dot = part.IndexOf('.');
            if (dot > 0)
            {
                side = part.Substring(0, dot).ToLowerInvariant();
                name = part.Substring(dot + 1);
            }
            if (name.Length > 2 && name[1] == '_' && (name[0] == 'L' || name[0] == 'R')) name = name.Substring(2);
            string lower = name.ToLowerInvariant();
            switch (lower)
            {
                case "thumbtrget":
                case "thumbtarget": return side + " thumb";
                case "pointtarget": return side + " index finger";
                case "middletarget": return side + " middle finger";
                case "ringtarget": return side + " ring finger";
                case "pinkytarget": return side + " pinky finger";
                case "tongue": return "tongue";
                case "heart": return "heart";
                default: return (side.Length > 0 ? side + " " : "") + lower;
            }
        }
    }
}
