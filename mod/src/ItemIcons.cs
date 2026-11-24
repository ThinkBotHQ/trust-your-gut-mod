using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TygMod
{
    // Icons, names and rarity colors for the mementos and balls a save point holds, looked up by the Id the save
    // stores. Read only: it reads the game's own item databases (the lists the market and the death screen use) and
    // never adds to or changes anything in the game.
    internal static class ItemIcons
    {
        internal sealed class Entry
        {
            public string Id;
            public string Name;       // name in the game's language, or the id when unknown
            public Texture Sheet;     // texture that holds the item picture (often a sheet of many); null = text badge
            public Rect Uv;           // where the picture is on Sheet, 0..1, origin bottom left (Unity UV)
            public Color Rarity = new Color(0.45f, 0.45f, 0.45f, 1f);
            public bool Known;        // found in the game's databases
        }

        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Dictionary<string, CharmBase> Charms = new Dictionary<string, CharmBase>(StringComparer.Ordinal);
        private static readonly Dictionary<string, BallBase> Balls = new Dictionary<string, BallBase>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly Dictionary<Sprite, Picture> Pictures = new Dictionary<Sprite, Picture>();
        private static bool _scanWarned, _buildWarned;
        private static int _pictures, _noSprite, _noTexture, _badUv;

        private sealed class Picture
        {
            public Texture Sheet;
            public Rect Uv;
        }
        private static string _lastReport;

        public static Entry Memento(string id) => Get(false, id);
        public static Entry Ball(string id) => Get(true, id);

        // Called when the save point window opens or refreshes (not every frame): learn any items the game has loaded
        // since the last time, look up again the ids that were unknown, and look up every item of the listed save
        // points now, so drawing the window only reads the cache.
        public static void Refresh(List<SavePointInfo> list)
        {
            _noSprite = _noTexture = _badUv = 0; // failures are not cached, so count them again for each refresh
            try
            {
                Add(StaticProperty("CharmDb") as CharmDatabaseSO);
                Add(StaticProperty("BallDb") as BallDatabaseSO);
                foreach (CharmDatabaseSO db in Resources.FindObjectsOfTypeAll<CharmDatabaseSO>()) Add(db);
                foreach (BallDatabaseSO db in Resources.FindObjectsOfTypeAll<BallDatabaseSO>()) Add(db);
                // Also loaded item objects that are not in a database list (for example starter items).
                foreach (CharmBase c in Resources.FindObjectsOfTypeAll<CharmBase>()) AddCharm(c);
                foreach (BallBase b in Resources.FindObjectsOfTypeAll<BallBase>()) AddBall(b);
            }
            catch (Exception e)
            {
                if (!_scanWarned)
                {
                    _scanWarned = true;
                    Plugin.Log.LogWarning("Item icons: could not read the game's item lists: " + e.Message);
                }
            }
            // Look every name up again (the game's language or text may have loaded since). Pictures stay cached.
            Cache.Clear();
            if (list != null)
            {
                foreach (SavePointInfo sp in list)
                {
                    SaveDetails d = sp != null ? sp.Details : null;
                    if (d == null) continue;
                    foreach (string id in d.Mementos) Memento(id);
                    foreach (string id in d.Starters) Memento(id);
                    foreach (string id in d.Balls) Ball(id);
                    foreach (string id in d.BallSlots) Ball(id);
                }
            }
            string report = "Item icons: " + Charms.Count + " mementos, " + Balls.Count + " balls known; pictures " + _pictures +
                            ", none (no sprite " + _noSprite + ", no texture " + _noTexture + ", bad area " + _badUv + ").";
            if (report != _lastReport)
            {
                _lastReport = report;
                Plugin.Log.LogInfo(report);
            }
        }

        private static object StaticProperty(string name)
        {
            PropertyInfo p = typeof(ItemFactory).GetProperty(name, Any);
            return p != null ? p.GetValue(null, null) : null;
        }

        private static void Add(CharmDatabaseSO db)
        {
            if (db == null || db.AllCharms == null) return;
            foreach (CharmBase c in db.AllCharms) AddCharm(c);
        }

        private static void Add(BallDatabaseSO db)
        {
            if (db == null || db.AllBalls == null) return;
            foreach (BallBase b in db.AllBalls) AddBall(b);
        }

        // Only the game's item assets (templates), never the copies it makes at run time ("(Clone)"): those carry
        // run state and are destroyed when the run ends. An entry whose object was destroyed is replaced.
        private static void AddCharm(CharmBase c)
        {
            if (c == null || string.IsNullOrEmpty(c.Id) || IsClone(c)) return;
            if (!Charms.TryGetValue(c.Id, out CharmBase old) || old == null) Charms[c.Id] = c;
        }

        private static void AddBall(BallBase b)
        {
            if (b == null || string.IsNullOrEmpty(b.Id) || IsClone(b)) return;
            if (!Balls.TryGetValue(b.Id, out BallBase old) || old == null) Balls[b.Id] = b;
        }

        private static bool IsClone(UnityEngine.Object o)
        {
            string n = o.name;
            return n != null && n.EndsWith("(Clone)", StringComparison.Ordinal);
        }

        private static Entry Get(bool ball, string id)
        {
            if (string.IsNullOrEmpty(id)) id = "?";
            string key = (ball ? "b|" : "m|") + id;
            if (Cache.TryGetValue(key, out Entry hit)) return hit;
            var e = new Entry { Id = id, Name = id };
            try
            {
                if (!ball && Charms.TryGetValue(id, out CharmBase c) && c != null)
                {
                    e.Name = Name(c.NameKey, c.EnglishName, id);
                    SetPicture(e, c.CharmIcon);
                    e.Rarity = RarityColor(c.EndScreenRarity);
                    e.Known = true;
                }
                else if (ball && Balls.TryGetValue(id, out BallBase b) && b != null)
                {
                    e.Name = Name(b.NameKey, null, id);
                    SetPicture(e, b.ballSprite);
                    e.Rarity = RarityColor(b.EndScreenRarity);
                    e.Known = true;
                }
            }
            catch (Exception ex)
            {
                if (!_buildWarned)
                {
                    _buildWarned = true;
                    Plugin.Log.LogWarning("Item icons: lookup failed for " + id + ": " + ex.Message);
                }
            }
            Cache[key] = e;
            return e;
        }

        // Same as the game's base GetDisplayName, read from the asset's fields. The virtual GetDisplayName is not
        // called: some items override it with run logic (for example the camera memento builds a copy of an item).
        private static string Name(MishLocalizedString key, string english, string id)
        {
            string display = null;
            try
            {
                if (key != null && key.IsResolved) display = key.Value;
            }
            catch (Exception)
            {
                display = null;
            }
            if (!string.IsNullOrEmpty(display)) return display;
            if (!string.IsNullOrEmpty(english)) return english;
            return id;
        }

        private static Color RarityColor(ItemFactory.Rarity r)
        {
            return ItemFactory.rarityLineColors != null && ItemFactory.rarityLineColors.TryGetValue(r, out Color c) ? c : new Color(0.45f, 0.45f, 0.45f, 1f);
        }

        // ---------- where a sprite's picture is ----------
        // Item sprites sit on shared sheets (atlases), often tightly packed and compressed, and the game does not mark
        // them readable, so they cannot be cut out into a new texture. Nothing is copied: the UI draws the whole sheet
        // inside a clip box placed so only this picture shows (see SavePointUI.DrawIcon). The area comes from the
        // sprite's own mesh UVs, which works for every packing mode.

        private static void SetPicture(Entry e, Sprite s)
        {
            Picture p = PictureOf(s);
            if (p == null) return;
            e.Sheet = p.Sheet;
            e.Uv = p.Uv;
        }

        private static Picture PictureOf(Sprite s)
        {
            if (s == null)
            {
                _noSprite++;
                return null;
            }
            if (Pictures.TryGetValue(s, out Picture hit)) return hit;
            Picture p = null;
            Texture2D tex = s.texture;
            if (tex == null || tex.width <= 0 || tex.height <= 0) _noTexture++;
            else
            {
                Vector2[] uv = s.uv;
                float x0 = 1f, y0 = 1f, x1 = 0f, y1 = 0f;
                if (uv != null)
                {
                    foreach (Vector2 v in uv)
                    {
                        x0 = Mathf.Min(x0, v.x);
                        y0 = Mathf.Min(y0, v.y);
                        x1 = Mathf.Max(x1, v.x);
                        y1 = Mathf.Max(y1, v.y);
                    }
                }
                x0 = Mathf.Clamp01(x0);
                y0 = Mathf.Clamp01(y0);
                x1 = Mathf.Clamp01(x1);
                y1 = Mathf.Clamp01(y1);
                // At least about one pixel wide and high, or there is nothing to show.
                if (x1 - x0 < 1f / tex.width || y1 - y0 < 1f / tex.height) _badUv++;
                else
                {
                    p = new Picture { Sheet = tex, Uv = Rect.MinMaxRect(x0, y0, x1, y1) };
                    _pictures++;
                }
            }
            if (p != null) Pictures[s] = p;
            return p;
        }

        // Two letters for a text badge: "Reaper Ball" -> "RB", "decaying_bet_limit_01" -> "DB".
        public static string Badge(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            var letters = new List<char>();
            bool start = true;
            foreach (char ch in name)
            {
                if (char.IsLetter(ch))
                {
                    if (start) letters.Add(char.ToUpperInvariant(ch));
                    start = false;
                }
                else start = true;
                if (letters.Count == 2) break;
            }
            return letters.Count == 0 ? name.Substring(0, 1) : new string(letters.ToArray());
        }
    }
}
