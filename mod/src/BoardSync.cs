using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TygMod
{
    // What one straight tile should show, worked out from the wheel.
    internal struct TileLook
    {
        public int Number;
        public bool Missing;       // no live pocket shows this number (renumbered away or blank)
        public bool Death;         // the main pocket for this number is a death pocket
        public bool Changed;       // a pocket that shows this number was renumbered into it
        public int Count;          // live pockets that show this number
        public Color Color;        // color of the main pocket
        public RouletteSlot Main;  // main pocket (native pocket first)
    }

    // Per tile visuals the mod owns.
    internal sealed class AreaVisual
    {
        public bool Applied;
        public bool Changed;
        public int ExpectedMods;   // outline rings the mod set; 0 after the game clears them = re-apply
        public string LabelText;   // label text the mod set; different later = re-apply
        public GameObject Skull;
        public readonly List<GameObject> Icons = new List<GameObject>();
    }

    internal static class BoardSync
    {
        public static bool Enabled => Plugin.BoardSyncEnabled.Value;

        // All wheel pockets, set by BoardSyncDriver each frame.
        internal static IReadOnlyList<RouletteSlot> AllSlots;
        private static readonly Dictionary<TMP_FontAsset, bool> ArrowOk = new Dictionary<TMP_FontAsset, bool>();

        private static bool _initDone;
        private static bool _ready;

        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // Private BetableArea members, read with plain reflection (no Harmony: this game's .NET is stripped).
        private static Func<BetableArea, List<RouletteSlot>> F_wheelSlots;
        private static Func<BetableArea, TextMeshPro> F_label;
        private static Func<BetableArea, bool> F_ready;
        private static Func<BetableArea, bool> F_dirty;
        private static Func<BetableArea, float> F_dim;
        private static Func<BetableArea, Material> F_baseTile;
        private static Func<BetableArea, BetAreaManager> F_manager;
        private static Func<BetableArea, List<Color>> F_outlineColors;
        private static Func<BetableArea, List<GameObject>> F_activeVisuals;
        private static Func<BetableArea, List<string>> F_modLabels;
        private static Func<BetableArea, List<Color>> F_modLabelColors;
        private static Func<BetableArea, List<int>> F_modLabelCounts;
        private static Func<BetableArea, Transform> F_modifierTransform;
        private static Func<BetableArea, float> F_labelBaseFontSize;
        private static Action<BetableArea, bool> S_missing;
        private static Action<BetableArea, bool> S_dirty;

        private static Action<BetableArea, Material> M_applyTile;
        private static Action<BetableArea> M_refreshLabelColor;
        private static Action<BetableArea> M_updateLabel;
        private static Action<BetableArea> M_applyOutlines;
        private static Action<BetableArea> M_fitLabel;
        private static Action<BetableArea> M_initWheelSync;
        private static Func<BetableArea, GameObject, GameObject> M_acquireVisual;
        private static Action<BetableArea, IReadOnlyList<SlotModifierInstance>> M_addModifierVisuals;
        private static Func<BetableArea, bool> M_collectWheelSlots;
        private static Func<BetableArea, RouletteSlot> M_pickBoardPocket;
        private static Action<BetableArea, RouletteSlot> M_syncOriginal;

        internal static SlotModifierManager CachedMods;

        private static readonly Dictionary<BetableArea, AreaVisual> Visuals = new Dictionary<BetableArea, AreaVisual>();
        private static readonly Dictionary<int, Material> TintCache = new Dictionary<int, Material>();
        private static Material _deathTile;
        private static Material _grayTile;
        private static Sprite _skullSprite;
        private static GameObject _overlayRoot;
        private static bool _loggedMaterials;
        private static readonly HashSet<string> LoggedErrors = new HashSet<string>();

        public static readonly Color ChangedLabelColor = Color.yellow;

        private static readonly HashSet<int> RedNumbers = new HashSet<int>
            { 1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36 };

        public static bool Init()
        {
            if (_initDone) return _ready;
            _initDone = true;
            try
            {
                F_wheelSlots = Get<List<RouletteSlot>>("_wheelSlots");
                F_label = Get<TextMeshPro>("betableAreaLabel");
                F_ready = Get<bool>("_wheelSyncReady");
                F_dirty = Get<bool>("_wheelSyncDirty");
                F_dim = Get<float>("_dimAmount");
                F_baseTile = Get<Material>("_baseTileMaterial");
                F_manager = Get<BetAreaManager>("_manager");
                F_outlineColors = Get<List<Color>>("_modifierOutlineColors");
                F_activeVisuals = Get<List<GameObject>>("_activeModifierVisuals");
                F_modLabels = Get<List<string>>("_modifierLabels");
                F_modLabelColors = Get<List<Color>>("_modifierLabelColors");
                F_modLabelCounts = Get<List<int>>("_modifierLabelCounts");
                F_modifierTransform = Get<Transform>("modifierTransform");
                F_labelBaseFontSize = Get<float>("_labelBaseFontSize");
                S_missing = Set<bool>("_numberMissingFromWheel");
                S_dirty = Set<bool>("_wheelSyncDirty");

                MethodInfo applyTile = Method("ApplyTileMaterial");
                MethodInfo refresh = Method("RefreshLabelColor");
                MethodInfo update = Method("UpdateLabel");
                MethodInfo outlines = Method("ApplyModifierOutlines");
                MethodInfo fit = Method("FitLabelToArea");
                MethodInfo init = Method("InitWheelSync");
                MethodInfo acquire = Method("AcquireModifierVisual");
                MethodInfo addVis = Method("AddModifierVisuals");
                MethodInfo collect = Method("CollectWheelSlots");
                MethodInfo pick = Method("PickBoardPocket");
                MethodInfo original = Method("SyncModifiersFromWheel");
                M_applyTile = (a, m) => Call(applyTile, a, m);
                M_refreshLabelColor = a => Call(refresh, a);
                M_updateLabel = a => Call(update, a);
                M_applyOutlines = a => Call(outlines, a);
                M_fitLabel = a => Call(fit, a);
                M_initWheelSync = a => Call(init, a);
                M_acquireVisual = (a, prefab) => (GameObject)Call(acquire, a, prefab);
                M_addModifierVisuals = (a, mods) => Call(addVis, a, mods);
                M_collectWheelSlots = a => (bool)Call(collect, a);
                M_pickBoardPocket = a => (RouletteSlot)Call(pick, a);
                M_syncOriginal = (a, pocket) => Call(original, a, pocket);
                _ready = true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Board sync init failed (game version changed?), board sync is off: " + e);
                _ready = false;
            }
            return _ready;
        }

        private static FieldInfo Field(string name)
        {
            FieldInfo f = typeof(BetableArea).GetField(name, Inst);
            if (f == null) throw new MissingFieldException("BetableArea." + name);
            return f;
        }

        private static Func<BetableArea, T> Get<T>(string name)
        {
            FieldInfo f = Field(name);
            if (!typeof(T).IsAssignableFrom(f.FieldType)) throw new InvalidCastException("BetableArea." + name + " is " + f.FieldType.Name);
            return a => (T)f.GetValue(a);
        }

        private static Action<BetableArea, T> Set<T>(string name)
        {
            FieldInfo f = Field(name);
            if (f.FieldType != typeof(T)) throw new InvalidCastException("BetableArea." + name + " is " + f.FieldType.Name);
            return (a, v) => f.SetValue(a, v);
        }

        private static MethodInfo Method(string name)
        {
            MethodInfo mi = typeof(BetableArea).GetMethod(name, Inst);
            if (mi == null) throw new MissingMethodException("BetableArea." + name);
            return mi;
        }

        private static object Call(MethodInfo mi, object target, params object[] args)
        {
            try
            {
                return mi.Invoke(target, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw new Exception("BetableArea." + mi.Name + " failed: " + e.InnerException.Message, e.InnerException);
            }
        }

        private static AreaVisual Visual(BetableArea area)
        {
            if (!Visuals.TryGetValue(area, out AreaVisual v))
            {
                v = new AreaVisual();
                Visuals[area] = v;
            }
            return v;
        }

        // ---------- called by the driver once per frame, after the game's LateUpdate ----------

        // True for tiles the mod handles: straight bets on one number.
        public static bool IsStraight(BetableArea area)
        {
            if (area == null || area.CurrentBetType != BetType.Straight) return false;
            IReadOnlyList<int> covered = area.GetCoveredNumbers();
            return covered != null && covered.Count == 1;
        }

        // The game sets this flag when the wheel changes; its LateUpdate then runs its own (stub) sync.
        // If the flag is still set, take it, so the game does not clear what the mod draws.
        public static bool TakeDirty(BetableArea area)
        {
            if (!F_dirty(area)) return false;
            S_dirty(area, false);
            return true;
        }

        // The game's own sync (or a language change) undid the mod's look on this tile.
        public static bool NeedsReapply(BetableArea area)
        {
            if (!Enabled || !Visuals.TryGetValue(area, out AreaVisual v) || !v.Applied) return false;
            if (v.ExpectedMods > 0 && F_outlineColors(area).Count == 0) return true;
            TextMeshPro label = F_label(area);
            return label != null && v.LabelText != null && label.text != v.LabelText;
        }

        // Same work as the game's LateUpdate wheel step, with the mod's sync in place of the game's stub.
        public static void Run(BetableArea area)
        {
            if (!Init()) return;
            try
            {
                M_collectWheelSlots(area);
                if (Sync(area)) M_syncOriginal(area, M_pickBoardPocket(area));
            }
            catch (Exception e)
            {
                LogOnce("run:" + e.Message, "Board sync step failed: " + e);
            }
        }

        // Renumbered pockets show a yellow number on the wheel; do the same on the table.
        // The game recolors labels when tiles dim, so this runs every frame for changed tiles.
        public static void KeepLabelColor(BetableArea area)
        {
            if (!Enabled || !Visuals.TryGetValue(area, out AreaVisual v) || !v.Applied || !v.Changed) return;
            TextMeshPro label = F_label(area);
            if (label == null) return;
            BetAreaManager mgr = F_manager(area);
            Color disabled = mgr != null ? mgr.DisabledLabelColor : new Color(0.42f, 0.43f, 0.48f, 1f);
            Color want = Color.Lerp(ChangedLabelColor, disabled, F_dim(area));
            if (label.color != want) label.color = want;
        }

        // ---------- the rule: what a tile shows ----------

        public static TileLook Compute(int number, IList<RouletteSlot> showing)
        {
            var look = new TileLook { Number = number };
            RouletteSlot native = null, firstAlive = null, firstAny = null;
            int count = 0;
            bool changed = false;
            if (showing != null)
            {
                for (int i = 0; i < showing.Count; i++)
                {
                    RouletteSlot s = showing[i];
                    if (s == null || s.IsBlank) continue; // a blank pocket has no number and no color
                    count++;
                    if (s.BaseNumber != number) changed = true;
                    if (firstAny == null) firstAny = s;
                    if (native == null && s.BaseNumber == number) native = s;
                    if (firstAlive == null && !s.IsDeath) firstAlive = s;
                }
            }
            RouletteSlot main = native ?? firstAlive ?? firstAny;
            look.Count = count;
            look.Changed = changed;
            look.Main = main;
            if (main == null)
            {
                look.Missing = true;
                return look;
            }
            look.Death = main.IsDeath;
            look.Color = main.currentColor;
            return look;
        }

        // For a tile that no pocket shows: the number its own pocket (the one with this base number) shows now,
        // or -1. The game pays the tile of the number a pocket shows now, so the gray tile cannot win.
        public static int MovedTo(int number, IReadOnlyList<RouletteSlot> all)
        {
            if (all == null) return -1;
            for (int i = 0; i < all.Count; i++)
            {
                RouletteSlot s = all[i];
                if (s != null && s.BaseNumber == number && !s.IsBlank && s.number != number) return s.number;
            }
            return -1;
        }

        // An arrow if the tile font has one, else ">".
        private static string Arrow(BetableArea area)
        {
            TextMeshPro label = F_label(area);
            TMP_FontAsset font = label != null ? label.font : null;
            if (font == null) return ">";
            if (!ArrowOk.TryGetValue(font, out bool ok))
            {
                try
                {
                    ok = font.HasCharacter('\u2192', true, true);
                }
                catch (Exception)
                {
                    ok = false;
                }
                ArrowOk[font] = ok;
                Plugin.Log.LogInfo("Tile font " + font.name + (ok ? " has" : " has no") + " arrow; gray tiles use " + (ok ? "\u2192" : ">") + ".");
            }
            return ok ? "\u2192" : ">";
        }

        // ---------- apply to one tile (runs from BetableArea.LateUpdate through the patch) ----------

        // Returns true when the game's own (empty) method should run.
        public static bool Sync(BetableArea area)
        {
            if (!Init() || area == null) return true;
            if (area.CurrentBetType != BetType.Straight) return true;
            IReadOnlyList<int> covered = area.GetCoveredNumbers();
            if (covered == null || covered.Count != 1) return true;
            AreaVisual vis = Visual(area);
            if (!Enabled)
            {
                Restore(area, vis);
                return true;
            }
            try
            {
                Apply(area, vis, covered[0]);
                return false;
            }
            catch (Exception e)
            {
                LogOnce("sync:" + e.GetType().Name + e.Message, "Board sync failed on tile " + covered[0] + ": " + e);
                return true;
            }
        }

        private static void Apply(BetableArea area, AreaVisual vis, int number)
        {
            if (!F_ready(area)) M_initWheelSync(area);
            BetAreaManager mgr = F_manager(area);
            List<RouletteSlot> showing = F_wheelSlots(area);
            TileLook look = Compute(number, showing);
            LogMaterialsOnce(mgr);

            // 1. tile material
            Material mat;
            if (look.Missing) mat = GrayTile(mgr);
            else if (look.Death) mat = DeathTile(mgr);
            else mat = ColorTile(mgr, look.Color);
            M_applyTile(area, mat);

            // 2. label text and color
            string text = Label(number);
            Sprite skull = look.Death ? SkullSprite(look.Main) : null;
            bool showSkull = look.Death && Plugin.ShowSkulls.Value && skull != null;
            if (showSkull) text = string.Empty;
            else if (look.Death) text = "<color=#CF2A2A>X</color>";
            else if (!look.Missing && look.Count > 1 && Plugin.ShowCopyCount.Value) text += "<size=55%> x" + look.Count + "</size>";
            else if (look.Missing)
            {
                int movedTo = MovedTo(number, AllSlots);
                if (movedTo >= 0) text += "<size=55%> " + Arrow(area) + Label(movedTo) + "</size>";
            }
            vis.Changed = look.Changed && !look.Missing;
            vis.Applied = true;
            S_missing(area, look.Missing);
            SetLabel(area, text);
            TextMeshPro shown = F_label(area);
            vis.LabelText = shown != null ? shown.text : null;
            M_refreshLabelColor(area);

            // 3. skull
            SetSkull(area, vis, showSkull ? skull : null);

            // 4. pocket modifiers (heat marks and others): outline rings, board overlay, small icons
            ApplyModifiers(area, vis, showing);
            vis.ExpectedMods = F_outlineColors(area).Count;
        }

        private static void Restore(BetableArea area, AreaVisual vis)
        {
            if (!vis.Applied) return;
            vis.Applied = false;
            vis.Changed = false;
            vis.ExpectedMods = 0;
            vis.LabelText = null;
            try
            {
                M_applyTile(area, null); // null = the tile's own material
                S_missing(area, false);
                TextMeshPro label = F_label(area);
                M_updateLabel(area);
                float baseSize = F_labelBaseFontSize(area);
                if (label != null && baseSize > 0f) label.fontSize = baseSize;
                M_refreshLabelColor(area);
                SetSkull(area, vis, null);
                SetIcons(area, vis, null);
            }
            catch (Exception e)
            {
                LogOnce("restore", "Board restore failed: " + e);
            }
        }

        private static string Label(int number)
        {
            return number == 37 ? "00" : number.ToString();
        }

        private static void SetLabel(BetableArea area, string text)
        {
            TextMeshPro label = F_label(area);
            if (label == null || label.text == text) return;
            label.richText = true;
            label.text = text;
            M_fitLabel(area);
        }

        private static void ApplyModifiers(BetableArea area, AreaVisual vis, List<RouletteSlot> showing)
        {
            List<Color> outline = F_outlineColors(area);
            outline.Clear();
            F_modLabels(area).Clear();
            F_modLabelColors(area).Clear();
            F_modLabelCounts(area).Clear();
            List<GameObject> active = F_activeVisuals(area);
            for (int i = 0; i < active.Count; i++)
            {
                if (active[i] != null) active[i].SetActive(false);
            }
            active.Clear();

            var icons = new List<Sprite>();
            SlotModifierManager mm = ModifierManager();
            if (mm != null && showing != null)
            {
                var seen = new HashSet<SlotModifierSO>();
                Transform boardRoot = F_modifierTransform(area);
                for (int s = 0; s < showing.Count; s++)
                {
                    if (showing[s] == null) continue;
                    IReadOnlyList<SlotModifierInstance> mods = mm.GetModifiersForSlot(showing[s]);
                    if (mods == null || mods.Count == 0) continue;
                    M_addModifierVisuals(area, mods);
                    for (int m = 0; m < mods.Count; m++)
                    {
                        SlotModifierSO so = mods[m]?.Modifier;
                        if (so == null || !seen.Add(so)) continue;
                        outline.Add(so.boardOutlineColor);
                        if (so.boardOverlayPrefab != null && boardRoot != null)
                        {
                            GameObject v = M_acquireVisual(area, so.boardOverlayPrefab);
                            if (v != null)
                            {
                                v.transform.localPosition = so.boardLocalOffset;
                                v.transform.localEulerAngles = so.boardLocalEuler;
                                v.transform.localScale = so.boardLocalScale;
                                active.Add(v);
                            }
                        }
                        if (so.ModifierIcon != null) icons.Add(so.ModifierIcon);
                    }
                }
            }
            M_applyOutlines(area);
            SetIcons(area, vis, Plugin.ShowModifierIcons.Value ? icons : null);
        }

        // ---------- materials ----------

        private static Material StraightBase(BetAreaManager mgr, int number)
        {
            if (mgr == null) return null;
            BetableArea a = mgr.GetStraightArea(number);
            return a != null ? F_baseTile(a) : null;
        }

        private static Material ColorTile(BetAreaManager mgr, Color c)
        {
            Material m = mgr != null ? mgr.GetTileMaterial(c) : null;
            if (m != null) return m;
            // The game's wheel-sync materials may be empty; fall back to tiles that have this color by default.
            if (Near(c, Color.red)) return StraightBase(mgr, 1);
            if (c.maxColorComponent <= 0.15f) return StraightBase(mgr, 2);
            if (Near(c, Color.green)) return StraightBase(mgr, 0);
            return TintTile(mgr, c);
        }

        private static Material GrayTile(BetAreaManager mgr)
        {
            Material m = mgr != null ? mgr.MissingTileMaterial : null;
            if (m != null) return m;
            if (_grayTile == null)
            {
                Material basis = StraightBase(mgr, 1);
                if (basis == null) return null;
                _grayTile = new Material(basis) { name = "TYG GrayTile" };
                SetIfHas(_grayTile, "_Saturation", 0f);
                SetIfHas(_grayTile, "_Brightness", 1.5f);
            }
            return _grayTile;
        }

        private static Material DeathTile(BetAreaManager mgr)
        {
            if (_deathTile != null) return _deathTile;
            Material basis = GrayTile(mgr);
            if (basis == null) return null;
            _deathTile = new Material(basis) { name = "TYG DeathTile" };
            if (_deathTile.HasProperty("_Brightness"))
            {
                _deathTile.SetFloat("_Brightness", Mathf.Max(2.6f, _deathTile.GetFloat("_Brightness") * 1.8f));
            }
            else
            {
                SetColorIfHas(_deathTile, Color.white);
            }
            return _deathTile;
        }

        private static Material TintTile(BetAreaManager mgr, Color c)
        {
            Color32 c32 = c;
            int key = (c32.r << 16) | (c32.g << 8) | c32.b;
            if (TintCache.TryGetValue(key, out Material m) && m != null) return m;
            Material basis = GrayTile(mgr);
            if (basis == null) return null;
            m = new Material(basis) { name = "TYG Tint " + ColorUtility.ToHtmlStringRGB(c) };
            SetColorIfHas(m, c);
            TintCache[key] = m;
            return m;
        }

        private static void SetIfHas(Material m, string prop, float v)
        {
            if (m.HasProperty(prop)) m.SetFloat(prop, v);
        }

        private static void SetColorIfHas(Material m, Color c)
        {
            if (m.HasProperty("_AlbedoColor")) m.SetColor("_AlbedoColor", c);
            else if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        private static bool Near(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.05f && Mathf.Abs(a.g - b.g) < 0.05f && Mathf.Abs(a.b - b.b) < 0.05f;
        }

        private static void LogMaterialsOnce(BetAreaManager mgr)
        {
            if (_loggedMaterials || mgr == null) return;
            _loggedMaterials = true;
            Plugin.Log.LogInfo("Tile materials: red=" + Name(mgr.GetTileMaterial(Color.red)) +
                               " black=" + Name(mgr.GetTileMaterial(Color.black)) +
                               " green=" + Name(mgr.GetTileMaterial(Color.green)) +
                               " missing=" + Name(mgr.MissingTileMaterial) +
                               " | base tiles 1=" + Name(StraightBase(mgr, 1)) + " 2=" + Name(StraightBase(mgr, 2)) +
                               " 0=" + Name(StraightBase(mgr, 0)));
        }

        private static string Name(UnityEngine.Object o)
        {
            return o == null ? "none" : o.name;
        }

        // ---------- skull and icons (sprites lying on the tile) ----------

        private static Sprite SkullSprite(RouletteSlot main)
        {
            if (main != null)
            {
                SlotVisualPresetSO death = main.GetDeathPreset();
                if (death != null && death.uiImage != null) _skullSprite = death.uiImage;
            }
            return _skullSprite;
        }

        private static Transform OverlayRoot()
        {
            if (_overlayRoot == null)
            {
                _overlayRoot = new GameObject("TygMod_BoardOverlays");
                SceneManager.MoveGameObjectToScene(_overlayRoot, SceneManager.GetActiveScene());
            }
            return _overlayRoot.transform;
        }

        private static GameObject MakeSprite(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(OverlayRoot(), false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            sr.receiveShadows = false;
            sr.sortingOrder = 50;
            return go;
        }

        private static float TileSize(BetableArea area, out Transform anchor)
        {
            TextMeshPro label = F_label(area);
            anchor = label != null ? label.transform : area.transform;
            Vector3 size;
            var box = area.GetComponent<BoxCollider>();
            if (box != null)
            {
                Vector3 ls = area.transform.lossyScale;
                size = new Vector3(Mathf.Abs(box.size.x * ls.x), Mathf.Abs(box.size.y * ls.y), Mathf.Abs(box.size.z * ls.z));
            }
            else
            {
                var col = area.GetComponent<Collider>();
                size = col != null ? col.bounds.size : Vector3.one * 0.1f;
            }
            // the tile is thin in one axis: use the middle size
            float a = size.x, b = size.y, c = size.z;
            float max = Mathf.Max(a, Mathf.Max(b, c));
            float min = Mathf.Min(a, Mathf.Min(b, c));
            return a + b + c - max - min;
        }

        private static void Place(GameObject go, Sprite sprite, Transform anchor, Vector3 offset, float worldSize)
        {
            go.transform.position = anchor.position + offset;
            go.transform.rotation = anchor.rotation;
            float s = Mathf.Max(sprite.bounds.size.x, sprite.bounds.size.y);
            if (s <= 0f) s = 1f;
            go.transform.localScale = Vector3.one * (worldSize / s);
        }

        private static void SetSkull(BetableArea area, AreaVisual vis, Sprite sprite)
        {
            if (sprite == null)
            {
                if (vis.Skull != null) vis.Skull.SetActive(false);
                return;
            }
            if (vis.Skull == null) vis.Skull = MakeSprite("TygSkull_" + area.name);
            var sr = vis.Skull.GetComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = RouletteSlot.DefaultDeathIconColor;
            float tile = TileSize(area, out Transform anchor);
            Place(vis.Skull, sprite, anchor, -anchor.forward * 0.002f, tile * 0.62f);
            vis.Skull.SetActive(true);
        }

        private static void SetIcons(BetableArea area, AreaVisual vis, List<Sprite> icons)
        {
            int n = icons == null ? 0 : Mathf.Min(icons.Count, 3);
            Transform anc = null;
            float tile = n > 0 ? TileSize(area, out anc) : 0f;
            for (int i = 0; i < n; i++)
            {
                if (vis.Icons.Count <= i || vis.Icons[i] == null)
                {
                    GameObject go = MakeSprite("TygModIcon_" + area.name + "_" + i);
                    if (vis.Icons.Count <= i) vis.Icons.Add(go);
                    else vis.Icons[i] = go;
                }
                GameObject icon = vis.Icons[i];
                var sr = icon.GetComponent<SpriteRenderer>();
                sr.sprite = icons[i];
                sr.color = Color.white;
                float iconSize = tile * 0.32f;
                Vector3 offset = anc.right * (tile * 0.33f - i * iconSize * 0.9f) + anc.up * (tile * 0.30f) - anc.forward * 0.003f;
                Place(icon, icons[i], anc, offset, iconSize);
                icon.SetActive(true);
            }
            for (int i = n; i < vis.Icons.Count; i++)
            {
                if (vis.Icons[i] != null) vis.Icons[i].SetActive(false);
            }
        }

        // ---------- helpers used by the driver ----------

        public static SlotModifierManager ModifierManager()
        {
            if (CachedMods != null) return CachedMods;
            SlotModifierManager mgr = null;
            EventManager.Events.OnRequestSlotModifierManager.Get().Invoke(null, (Action<SlotModifierManager>)(m => mgr = m));
            if (mgr != null) CachedMods = mgr;
            return mgr;
        }

        public static void ForgetDestroyed()
        {
            var dead = new List<BetableArea>();
            foreach (KeyValuePair<BetableArea, AreaVisual> kv in Visuals)
            {
                if (kv.Key == null) dead.Add(kv.Key);
            }
            foreach (BetableArea a in dead) Visuals.Remove(a);
        }

        public static string Describe(IReadOnlyList<RouletteSlot> slots)
        {
            var sb = new StringBuilder();
            var showing = new List<RouletteSlot>();
            SlotModifierManager mm = ModifierManager();
            for (int n = 0; n <= 37; n++)
            {
                showing.Clear();
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i] != null && slots[i].number == n) showing.Add(slots[i]);
                }
                TileLook t = Compute(n, showing);
                string def = n == 0 || n == 37 ? "green" : (RedNumbers.Contains(n) ? "red" : "black");
                string now = t.Missing ? "missing" : t.Death ? "death" : ColorName(t.Color);
                var extra = new List<string>();
                if (t.Count > 1) extra.Add("x" + t.Count);
                if (t.Changed) extra.Add("renumbered");
                if (t.Missing)
                {
                    int to = MovedTo(n, slots);
                    if (to >= 0) extra.Add("now " + Label(to));
                }
                if (mm != null)
                {
                    foreach (RouletteSlot s in showing)
                    {
                        foreach (SlotModifierInstance inst in mm.GetModifiersForSlot(s))
                        {
                            if (inst?.Modifier != null) extra.Add("mod:" + inst.Modifier.name);
                        }
                    }
                }
                if (now != def || extra.Count > 0)
                {
                    sb.Append(Label(n)).Append('=').Append(now);
                    if (extra.Count > 0) sb.Append('(').Append(string.Join(",", extra.ToArray())).Append(')');
                    sb.Append("  ");
                }
            }
            return sb.Length == 0 ? "all tiles default" : sb.ToString();
        }

        private static string ColorName(Color c)
        {
            if (Near(c, Color.red)) return "red";
            if (c.maxColorComponent <= 0.15f) return "black";
            if (Near(c, Color.green)) return "green";
            return "#" + ColorUtility.ToHtmlStringRGB(c);
        }

        private static void LogOnce(string key, string msg)
        {
            if (LoggedErrors.Add(key)) Plugin.Log.LogError(msg);
        }
    }

    // Runs the board sync once per frame, just before the frame is drawn (after every LateUpdate).
    internal sealed class BoardSyncDriver : MonoBehaviour
    {
        private RouletteSlotManager _slots;
        private BetAreaManager _areas;
        private readonly List<BetableArea> _straight = new List<BetableArea>();
        private float _nextFind;
        private int _lastHash;
        private bool _lastEnabled;
        private bool _hooked;
        internal static bool Capturing;

        private static BoardSyncDriver _inst;

        // Test hook entry point (TestCommands): same as the proof hotkey.
        internal static void CommandProof()
        {
            if (_inst != null && !Capturing) _inst.StartCoroutine(_inst.ProofCapture());
        }

        private void OnEnable()
        {
            _inst = this;
            Application.onBeforeRender += OnBeforeRender;
            _hooked = true;
        }

        private void OnDisable()
        {
            if (_hooked) Application.onBeforeRender -= OnBeforeRender;
            _hooked = false;
        }

        private void Update()
        {
            if (Keys.Down(Plugin.KeyBoardToggle.Value)) SetEnabled(!BoardSync.Enabled, true);
            if (Keys.Down(Plugin.KeyProofCapture.Value) && !Capturing) StartCoroutine(ProofCapture());
        }

        private void OnBeforeRender()
        {
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Board sync frame failed: " + e);
                _slots = null; // find again next second
            }
        }

        private void Step()
        {
            if (!Find()) return;
            BoardSync.AllSlots = _slots.Slots;
            bool en = BoardSync.Enabled;
            int h = Hash();
            bool wheelChanged = h != _lastHash || en != _lastEnabled;
            if (wheelChanged)
            {
                bool describe = h != _lastHash;
                _lastHash = h;
                _lastEnabled = en;
                if (describe)
                {
                    try
                    {
                        Plugin.Log.LogInfo("Wheel state: " + BoardSync.Describe(_slots.Slots) + (en ? "" : " [board sync OFF]"));
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("Wheel describe failed: " + e.Message);
                    }
                }
            }
            for (int i = 0; i < _straight.Count; i++)
            {
                BetableArea a = _straight[i];
                if (a == null)
                {
                    _slots = null;
                    continue;
                }
                bool dirty = BoardSync.TakeDirty(a);
                if (dirty || wheelChanged || BoardSync.NeedsReapply(a)) BoardSync.Run(a);
                BoardSync.KeepLabelColor(a);
            }
        }

        private bool Find()
        {
            if (_slots != null && _areas != null) return true;
            if (Time.unscaledTime < _nextFind) return false;
            _nextFind = Time.unscaledTime + 1f;
            BoardSync.ForgetDestroyed();
            _slots = FindFirstObjectByType<RouletteSlotManager>();
            _areas = FindFirstObjectByType<BetAreaManager>();
            BoardSync.CachedMods = null;
            BoardSync.CachedMods = BoardSync.ModifierManager();
            _straight.Clear();
            _lastHash = 0;
            if (_slots == null || _areas == null || !BoardSync.Init()) return false;
            IReadOnlyList<BetableArea> all = _areas.AllAreas;
            for (int i = 0; i < all.Count; i++)
            {
                if (BoardSync.IsStraight(all[i])) _straight.Add(all[i]);
            }
            Plugin.Log.LogInfo("Board found: " + _straight.Count + " number tiles, " + _slots.Slots.Count + " wheel pockets.");
            return _straight.Count > 0;
        }

        private int Hash()
        {
            unchecked
            {
                int h = 17;
                IReadOnlyList<RouletteSlot> slots = _slots.Slots;
                SlotModifierManager mm = BoardSync.ModifierManager();
                for (int i = 0; i < slots.Count; i++)
                {
                    RouletteSlot s = slots[i];
                    if (s == null) continue;
                    Color32 c = s.currentColor;
                    h = h * 31 + s.number;
                    h = h * 31 + ((c.r << 16) | (c.g << 8) | c.b);
                    h = h * 31 + (s.IsDeath ? 1 : 0) + (s.IsBlank ? 2 : 0);
                    if (mm != null)
                    {
                        IReadOnlyList<SlotModifierInstance> mods = mm.GetModifiersForSlot(s);
                        for (int m = 0; mods != null && m < mods.Count; m++)
                        {
                            h = h * 31 + (mods[m]?.Modifier != null ? mods[m].Modifier.GetInstanceID() : 7);
                        }
                    }
                }
                return h == 0 ? 1 : h;
            }
        }

        private void SetEnabled(bool on, bool toast)
        {
            Plugin.BoardSyncEnabled.Value = on;
            if (toast) SavePointUI.Toast(on ? "Board sync ON: table shows the wheel" : "Board sync OFF: default table");
        }

        private IEnumerator ProofCapture()
        {
            Capturing = true;
            bool was = BoardSync.Enabled;
            string dir = SavePointStore.ProofDir;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string before = Path.Combine(dir, "board-" + stamp + "-1-before.png");
            string after = Path.Combine(dir, "board-" + stamp + "-2-after.png");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Proof folder: " + e.Message);
            }
            SetEnabled(false, false);
            for (int i = 0; i < 6; i++) yield return null;
            ScreenCapture.CaptureScreenshot(before, 1);
            for (int i = 0; i < 4; i++) yield return null;
            SetEnabled(true, false);
            for (int i = 0; i < 6; i++) yield return null;
            ScreenCapture.CaptureScreenshot(after, 1);
            for (int i = 0; i < 4; i++) yield return null;
            SetEnabled(was, false);
            Capturing = false;
            Plugin.Log.LogInfo("Proof screenshots: " + before + " | " + after);
            SavePointUI.Toast("Proof screenshots saved");
        }
    }
}
