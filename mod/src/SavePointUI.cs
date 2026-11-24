using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace TygMod
{
    // F8 window: make, list, restore, rename, pin and delete save points. F9: quick save point.
    internal sealed class SavePointUI : MonoBehaviour
    {
        private const string MenuScene = "MainMenu";
        private const string GameScene = "GameScene";

        private static SavePointUI _inst;
        private static string _toast;
        private static float _toastUntil;

        private bool _open;
        private bool _busy;
        private bool _pausedByMe;
        private bool _trackPause;
        private PauseMenuManager _pause;
        // Clickable "Save points" button: shown on the main menu, on the pause menu and after a death.
        // (On a Mac F8/F9 are media keys; they reach the game only with fn held.)
        private bool _showButton;
        private bool _openRequested; // set by the button in OnGUI, handled in Update (no layout change mid-event)
        private float _nextButtonCheck;
        private PauseMenuManager _pauseCache;
        private readonly List<BaseInputModule> _disabledModules = new List<BaseInputModule>();
        private List<SavePointInfo> _list = new List<SavePointInfo>();
        private SavePointInfo _current;
        private Vector2 _scroll;
        private string _status = "";
        private string _confirmKey;
        private float _confirmUntil;
        private float _confirmArmed;
        // A click only records its work here; it runs at the start of the next Layout event, so every event of a
        // frame draws the same list. Clicks are ignored for a moment after one is taken, so a double click cannot
        // land on a row that has just moved.
        private Action _pending;
        private float _clicksFrom;
        private Rect _rect;
        private GUIStyle _row, _rowTitle, _rowSub, _small, _title, _toastStyle;
        private string _renameDir;       // folder of the save point being renamed, or null
        private string _renameText = "";
        private GUIStyle _field, _badge, _badgeBig, _panelTitle, _panelText, _cellName;
        // Item icons: this game's IMGUI has no tooltips and no image buttons, so hover is found by testing the mouse
        // against each icon strip's rectangle in the Repaint event (all in the same pass, so nothing is stale).
        private int _hoverRow = -1;    // row whose icon strip is under the mouse in this Repaint, or -1
        private float _scrollTop, _scrollBottom; // the list's visible part, in the window area's coordinates
        private string _detailsDir;    // save point whose item panel stays open (its icons were clicked), or null
        private Rect _keptPanel;       // where the kept-open panel was drawn in the last Repaint (zero = not shown)
        private readonly Dictionary<string, ItemIcons.Entry[]> _strips = new Dictionary<string, ItemIcons.Entry[]>(StringComparer.Ordinal);
        private float _windowW = 1080f;

        // True while the rename field takes the keyboard (Keys.Down then ignores the mod's hotkeys).
        internal static bool Typing => _inst != null && _inst._open && _inst._renameDir != null;

        public static void Toast(string msg)
        {
            _toast = msg;
            _toastUntil = Time.unscaledTime + 5f;
            Plugin.Log.LogInfo("[toast] " + msg);
        }

        private void Awake()
        {
            _inst = this;
        }

        // ---------- test hook entry points (TestCommands; same code as the hotkeys and buttons) ----------

        internal static void CommandQuickSave()
        {
            if (_inst != null && !_inst._busy) _inst.QuickSave();
        }

        internal static void CommandOpen()
        {
            if (_inst != null && !_inst._busy && !_inst._open) _inst.TryOpen();
        }

        internal static void CommandClose()
        {
            if (_inst != null && _inst._open) _inst.Close();
        }

        internal static void CommandRestore(string name, bool full)
        {
            if (_inst == null) return;
            SavePointInfo sp = FindSavePoint(name);
            if (sp == null) return;
            if (!sp.HasRun || sp.ReadError != null)
            {
                Plugin.Log.LogWarning("Save point cannot be restored: " + name);
                return;
            }
            _inst.StartRestore(sp, full);
            if (!string.IsNullOrEmpty(_inst._status)) Plugin.Log.LogInfo("Restore: " + _inst._status);
        }

        internal static void CommandRename(string name, string text)
        {
            if (_inst == null) return;
            SavePointInfo sp = FindSavePoint(name);
            if (sp != null) Plugin.Log.LogInfo("Rename: " + _inst.DoRename(sp, text, out _));
        }

        internal static void CommandPin(string name, bool pin)
        {
            if (_inst == null) return;
            SavePointInfo sp = FindSavePoint(name);
            if (sp != null) Plugin.Log.LogInfo((pin ? "Pin: " : "Unpin: ") + (pin ? _inst.DoPin(sp) : _inst.DoUnpin(sp)));
        }

        // Keeps the item panel of one save point open (no name = close it), like clicking its icons.
        internal static void CommandDetails(string name)
        {
            if (_inst == null) return;
            if (string.IsNullOrEmpty(name))
            {
                _inst._detailsDir = null;
                return;
            }
            SavePointInfo sp = FindSavePoint(name);
            if (sp != null) _inst._detailsDir = sp.Dir;
        }

        internal static void CommandDelete(string name)
        {
            if (_inst == null) return;
            SavePointInfo sp = FindSavePoint(name);
            if (sp != null) Plugin.Log.LogInfo("Delete: " + _inst.DoDelete(sp));
        }

        private static SavePointInfo FindSavePoint(string name)
        {
            foreach (SavePointInfo sp in SavePointStore.List())
            {
                if (string.Equals(sp.Name, name, StringComparison.OrdinalIgnoreCase)) return sp;
            }
            Plugin.Log.LogWarning("No save point named " + name);
            return null;
        }

        private void Update()
        {
            if (_busy) return;
            if (Keys.Down(Plugin.KeyWindow.Value))
            {
                if (_open) Close();
                else TryOpen();
            }
            if (Keys.Down(Plugin.KeyQuickSave.Value)) QuickSave();
            if (_openRequested)
            {
                _openRequested = false;
                if (!_open) TryOpen();
            }

            // The pause menu under the window closed (Esc) or went away: close the window too.
            if (_open && _trackPause && (_pause == null || !_pause.IsPaused)) Close();
            UpdateButton();
        }

        private void UpdateButton()
        {
            if (Time.unscaledTime < _nextButtonCheck) return;
            _nextButtonCheck = Time.unscaledTime + 0.25f;
            string scene = SceneName();
            if (scene == MenuScene)
            {
                _showButton = true;
                return;
            }
            if (scene != GameScene)
            {
                _showButton = false;
                return;
            }
            if (IsDead())
            {
                _showButton = true;
                return;
            }
            if (_pauseCache == null) _pauseCache = FindFirstObjectByType<PauseMenuManager>();
            _showButton = _pauseCache != null && _pauseCache.IsPaused;
        }

        private static string SceneName()
        {
            return SceneManager.GetActiveScene().name;
        }

        private static bool IsDead()
        {
            return SaveManager.Instance != null && SaveManager.Instance.IsDead;
        }

        // In a run, alive, and the game itself would not let you pause now (ending, elevator ride, collar, ladder).
        private static bool RunPauseBlocked(out string why)
        {
            why = null;
            return SceneName() == GameScene && !IsDead() && SaveGate.PauseBlocked(out why);
        }

        private void TryOpen()
        {
            string scene = SceneName();
            _pausedByMe = false;
            _trackPause = false;
            if (scene == GameScene && !IsDead())
            {
                _pause = FindFirstObjectByType<PauseMenuManager>();
                if (_pause == null)
                {
                    Toast("Save window cannot open here. Press " + Plugin.KeyQuickSave.Value + " for a quick save point.");
                    return;
                }
                if (!_pause.IsPaused)
                {
                    _pause.Pause();
                    _pausedByMe = _pause.IsPaused;
                }
                if (!_pause.IsPaused)
                {
                    Toast("The game is busy. Try again in a moment, or press " + Plugin.KeyQuickSave.Value + " for a quick save point.");
                    return;
                }
                _trackPause = true;
            }
            // The game's own menus must not take the clicks meant for this window.
            BlockGameInput();
            Plugin.Log.LogInfo("Save window open in " + scene + ". Game input modules off: " + _disabledModules.Count +
                               ", EventSystem.current set: " + (EventSystem.current != null));
            _open = true;
            _confirmKey = null;
            _renameDir = null;
            _detailsDir = null;
            _status = "";
            Refresh();
        }

        // Stop the game's menus from getting clicks and key navigation while the mod's window is open.
        // Keep every EventSystem ON: game code calls EventSystem.current without a null check (for example
        // CustomMainMenuFrame.ClearSelection when the warning screen swaps to the main menu), and a null there
        // stops the main menu from showing. Only the input modules are turned off. UnblockGameInput turns them back on.
        private void BlockGameInput()
        {
            foreach (EventSystem es in FindObjectsByType<EventSystem>(FindObjectsSortMode.None))
            {
                if (es == null) continue;
                foreach (BaseInputModule m in es.GetComponents<BaseInputModule>())
                {
                    if (m != null && m.enabled)
                    {
                        m.enabled = false;
                        _disabledModules.Add(m);
                    }
                }
            }
        }

        private void UnblockGameInput()
        {
            foreach (BaseInputModule m in _disabledModules)
            {
                if (m != null) m.enabled = true;
            }
            _disabledModules.Clear();
        }

        private void Close(bool resume = true, bool keepInputBlocked = false)
        {
            _open = false;
            _renameDir = null;
            _detailsDir = null;
            if (!keepInputBlocked) UnblockGameInput();
            if (resume && _pausedByMe && _pause != null && _pause.IsPaused) _pause.Resume();
            _pausedByMe = false;
            _trackPause = false;
            _pause = null;
        }

        private void Refresh()
        {
            try
            {
                _list = SavePointStore.List();
                _current = SavePointStore.Read(SavePointStore.SaveDir, SavePointStore.SaveDir, "current");
                if (_renameDir != null && !_list.Exists(x => x.Dir == _renameDir)) _renameDir = null; // it was deleted
                if (_detailsDir != null && !_list.Exists(x => x.Dir == _detailsDir)) _detailsDir = null;
                ItemIcons.Refresh(_list);
                _strips.Clear();
                foreach (SavePointInfo sp in _list)
                {
                    if (sp.Details == null) continue;
                    var row = new List<ItemIcons.Entry>();
                    foreach (string id in sp.Details.Mementos) row.Add(ItemIcons.Memento(id));
                    foreach (string id in sp.Details.Balls) row.Add(ItemIcons.Ball(id));
                    _strips[sp.Dir] = row.ToArray();
                }
            }
            catch (Exception e)
            {
                _status = "List failed: " + e.Message;
                Plugin.Log.LogError("List failed: " + e);
            }
        }

        private void QuickSave()
        {
            if (SaveGate.IsSpinning(SaveManager.Instance))
            {
                Toast("Wait for the spin to end, then make the save point.");
                return;
            }
            if (RunPauseBlocked(out string why))
            {
                Toast("No save point during " + why + ". Try again when it ends.");
                return;
            }
            try
            {
                SavePointInfo sp = SavePointStore.Make("sp");
                Toast("Save point made: " + sp.Summary());
                if (_open) Refresh();
            }
            catch (Exception e)
            {
                Toast("Save point FAILED: " + e.Message);
                Plugin.Log.LogError("Save point failed: " + e);
            }
        }

        // ---------- rename, pin, delete (buttons and test hook) ----------
        // Each returns the line for the status area. None of them touches the game's save files.

        private string DoRename(SavePointInfo sp, string text, out bool ok)
        {
            ok = false;
            if (_busy) return "Wait for the restore to end.";
            _confirmKey = null;
            try
            {
                string label = SavePointStore.Rename(sp, text);
                ok = true;
                if (_open) Refresh();
                return label == null ? "Name removed from " + sp.Title() + "." : "Renamed to: " + label;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Rename failed: " + e);
                return "Rename FAILED: " + e.Message;
            }
        }

        private string DoPin(SavePointInfo sp)
        {
            if (_busy) return "Wait for the restore to end.";
            _confirmKey = null;
            try
            {
                int slot = SavePointStore.Pin(sp);
                if (_open) Refresh();
                return slot > 0
                    ? "Pinned to slot " + slot + ": " + sp.Title()
                    : "All " + SavePointMeta.MaxPins + " pins are used. Unpin one first.";
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Pin failed: " + e);
                if (_open) Refresh();
                return "Pin FAILED: " + e.Message;
            }
        }

        private string DoUnpin(SavePointInfo sp)
        {
            if (_busy) return "Wait for the restore to end.";
            _confirmKey = null;
            try
            {
                SavePointStore.Unpin(sp);
                if (_open) Refresh();
                return "Unpinned: " + sp.Title();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Unpin failed: " + e);
                if (_open) Refresh();
                return "Unpin FAILED: " + e.Message;
            }
        }

        private string DoDelete(SavePointInfo sp)
        {
            if (_busy) return "Wait for the restore to end.";
            _confirmKey = null;
            try
            {
                SavePointStore.Delete(sp);
                if (_open) Refresh();
                return "Deleted: " + sp.Title() + ". To get it back, move its folder out of " +
                       System.IO.Path.Combine(SavePointStore.Root, SavePointMeta.TrashFolder) + ".";
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Delete failed: " + e);
                if (_open) Refresh();
                return "Delete FAILED: " + e.Message;
            }
        }

        // Second click within 6 s confirms. A second click less than 0.3 s after the first is a double click, not a
        // separate second click, so it does not confirm.
        private bool Confirm(string key)
        {
            float now = Time.unscaledTime;
            if (_confirmKey == key && now < _confirmUntil)
            {
                if (now - _confirmArmed < 0.3f) return false;
                _confirmKey = null;
                return true;
            }
            _confirmKey = key;
            _confirmArmed = now;
            _confirmUntil = now + 6f;
            return false;
        }

        private void Queue(Action work)
        {
            if (_pending != null || Time.unscaledTime < _clicksFrom) return;
            _pending = work;
            _clicksFrom = Time.unscaledTime + 0.3f;
        }

        private bool Confirming(string key)
        {
            return _confirmKey == key && Time.unscaledTime < _confirmUntil;
        }

        // ---------- restore ----------

        private void StartRestore(SavePointInfo sp, bool full)
        {
            if (_busy) return;
            if (SaveGate.IsSpinning(SaveManager.Instance))
            {
                _status = "Wait for the spin to end, then restore.";
                return;
            }
            if (RunPauseBlocked(out string why))
            {
                _status = "No restore during " + why + ". Try again when it ends.";
                return;
            }
            StartCoroutine(Restore(sp, full));
        }

        // Same path as the game's own "Quit to menu" then "Continue":
        // save like Quit to menu, copy the current files to an 'auto' save point, go to the main menu,
        // copy the save point files in while the menu is idle, reset SaveManager's pending state,
        // then load the run scene. The game's GameBootstrapper calls SaveManager.LoadAll, which reads the restored file.
        private IEnumerator Restore(SavePointInfo sp, bool full)
        {
            _busy = true;
            SaveGate.RestoreInProgress = true;
            BlockGameInput(); // also when started with the window closed (test hook); a no-op when already blocked
            try
            {
                string label = (sp.Label != null ? "'" + sp.Label + "'  " : "") + sp.Summary();
                Plugin.Log.LogInfo("Restore start (" + (full ? "full" : "run only") + "): " + sp.Dir + "  " + label);
                bool resumeIfStopped = _pausedByMe;
                PauseMenuManager pausedMenu = _pause;
                // Stay paused (the scene change ends the pause) and keep the game's menus blocked, so no click
                // can start a run while the files are copied.
                Close(false, true);
                Toast("Restoring " + (full ? "everything" : "the run") + "...");
                yield return null;

                SaveManager sm = SaveManager.Instance;
                if (sm == null)
                {
                    Toast("Restore stopped: the game's save system was not found.");
                    if (resumeIfStopped && pausedMenu != null && pausedMenu.IsPaused) pausedMenu.Resume();
                    yield break;
                }
                if (!RestoreMark.CanWrite())
                {
                    Toast("Restore stopped: the mod could not write to its folder. Nothing was changed.");
                    if (resumeIfStopped && pausedMenu != null && pausedMenu.IsPaused) pausedMenu.Resume();
                    yield break;
                }

                // 1. Save the current run like "Quit to menu" does, then keep a copy of it.
                string err = null;
                try
                {
                    SaveGate.SaveLikeQuitToMenu(sm);
                    if (Plugin.AutoSavePointBeforeRestore.Value) SavePointStore.Make("auto");
                }
                catch (Exception e)
                {
                    err = e.Message;
                    Plugin.Log.LogError("Auto save point failed, restore stopped: " + e);
                }
                if (err != null)
                {
                    Toast("Restore stopped: auto save point failed (" + err + "). Nothing was changed.");
                    if (resumeIfStopped && pausedMenu != null && pausedMenu.IsPaused) pausedMenu.Resume();
                    yield break;
                }

                // 2. Leave the run. Its objects unload, so nothing is left that saves run data.
                if (SceneName() != MenuScene)
                {
                    yield return LoadScene(MenuScene);
                    float settle = Time.unscaledTime + 1.0f, giveUp = Time.unscaledTime + 20f;
                    while ((Time.unscaledTime < settle || LoaderTransitioning()) && Time.unscaledTime < giveUp) yield return null;
                }
                BlockGameInput(); // the main menu's own EventSystem

                // 3. Copy the files in while the menu is idle.
                bool armFailed = false;
                try
                {
                    sm = SaveManager.Instance;
                    sm.WaitForPendingWrites();
                    SaveGate.PrepareForRestore(sm);
                    if (!RestoreMark.Arm())
                    {
                        armFailed = true;
                        throw new System.IO.IOException("the mod could not write to its folder");
                    }
                    try
                    {
                        SavePointStore.RestoreFiles(sp, full);
                    }
                    catch
                    {
                        if (!SavePointStore.RunFileMatches(sp)) RestoreMark.Disarm();
                        throw;
                    }
                    SaveGate.PrepareForRestore(sm);
                }
                catch (Exception e)
                {
                    err = e.Message;
                    Plugin.Log.LogError("Restore failed: " + e);
                }
                if (err != null)
                {
                    Toast(armFailed
                        ? "Restore stopped: " + err + ". Your save files were not changed."
                        : "Restore FAILED: " + err + "." + UndoHint());
                    yield break;
                }

                // 4. Continue: load the run scene; the game loads the restored file.
                yield return LoadScene(GameScene);
                UnblockGameInput();
                float until = Time.unscaledTime + 20f;
                while (Time.unscaledTime < until && !(SaveManager.Instance != null && SaveManager.Instance.WasLoadedFromSave)) yield return null;
                if (SaveManager.Instance != null && SaveManager.Instance.WasLoadedFromSave)
                {
                    Plugin.Log.LogInfo("Restore done: the game loaded the restored run (" + label + ").");
                    Toast("Restored: " + label);
                }
                else
                {
                    Plugin.Log.LogError("Restore problem: the run scene did not load the restored file in 20 s.");
                    Toast("Restore problem: the run did not load." + UndoHint());
                }
            }
            finally
            {
                UnblockGameInput();
                SaveGate.RestoreInProgress = false;
                _busy = false;
            }
        }

        private static string UndoHint()
        {
            return Plugin.AutoSavePointBeforeRestore.Value ? " Your auto save point is in the list." : "";
        }

        private static readonly System.Reflection.FieldInfo LoaderBusy =
            typeof(SceneLoader).GetField("_isTransitioning", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private static bool LoaderTransitioning()
        {
            try
            {
                return SceneLoader.Instance != null && LoaderBusy != null && (bool)LoaderBusy.GetValue(SceneLoader.Instance);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private IEnumerator LoadScene(string name)
        {
            float idle = Time.unscaledTime + 10f;
            while (LoaderTransitioning() && Time.unscaledTime < idle) yield return null;
            if (SceneLoader.Instance != null && !LoaderTransitioning()) SceneLoader.Instance.LoadScene(name);
            else SceneManager.LoadScene(name);
            float until = Time.unscaledTime + 30f;
            while (SceneName() != name && Time.unscaledTime < until) yield return null;
            if (SceneName() != name)
            {
                Plugin.Log.LogWarning("SceneLoader did not load " + name + ", loading it directly.");
                SceneManager.LoadScene(name);
                yield return null;
                yield return null;
            }
        }

        // ---------- drawing ----------

        private float Scale()
        {
            float s = Plugin.UiScale.Value;
            if (s > 0.1f) return s;
            return Mathf.Clamp(Screen.height / 900f, 1f, 3f);
        }

        private void Styles()
        {
            if (_row != null) return;
            _row = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = false };
            _rowTitle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip };
            _rowSub = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = false, clipping = TextClipping.Clip };
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 14, wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft };
            _badge = new GUIStyle(GUI.skin.label) { fontSize = 9, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip, padding = new RectOffset(0, 0, 0, 0) };
            _badgeBig = new GUIStyle(_badge) { fontSize = 15 };
            _panelTitle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip };
            _panelText = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = false, clipping = TextClipping.Clip };
            _cellName = new GUIStyle(GUI.skin.label) { fontSize = 10, wordWrap = true, alignment = TextAnchor.UpperCenter, clipping = TextClipping.Clip };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            _toastStyle = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleCenter, wordWrap = true };
        }

        private void OnGUI()
        {
            if (BoardSyncDriver.Capturing) return;
            if (_pending != null && Event.current.type == EventType.Layout)
            {
                Action work = _pending;
                _pending = null;
                work();
            }
            Styles();
            float s = Scale();
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
            float sw = Screen.width / s, sh = Screen.height / s;

            if (_toast != null && Time.unscaledTime < _toastUntil)
            {
                Rect tr = new Rect(sw / 2f - 300f, 16f, 600f, 48f);
                GUI.Box(tr, "", _toastStyle);
                GUI.Box(tr, _toast, _toastStyle);
            }
            if (_showButton && !_open && !_busy)
            {
                // GUI.Button(Rect, string) is stripped in this game; the GUIContent + style overload exists.
                if (GUI.Button(new Rect(sw - 196f, 16f, 180f, 34f), new GUIContent("Save points (mod)"), GUI.skin.button))
                {
                    _nextButtonCheck = 0f;
                    _openRequested = true;
                }
            }
            if (_open)
            {
                float w = Mathf.Min(1080f, sw - 40f), h = Mathf.Min(640f, sh - 40f);
                _windowW = w;
                _rect = new Rect((sw - w) / 2f, (sh - h) / 2f, w, h);
                // The kept-open item panel is drawn on top of the window but is not a control: stop clicks on it
                // from reaching the buttons under it. A click on it closes it.
                Event ev = Event.current;
                if (_detailsDir != null && _keptPanel.width > 0f && _keptPanel.Contains(ev.mousePosition) &&
                    (ev.type == EventType.MouseDown || ev.type == EventType.MouseUp || ev.type == EventType.MouseDrag))
                {
                    if (ev.type == EventType.MouseDown) Queue(() => _detailsDir = null);
                    ev.Use();
                }
                // GUI.Window overloads are stripped from this game's IMGUI module; use a box + area instead.
                // The default window style is see-through; a dark back and two extra layers keep the text readable.
                GUI.Box(_rect, "", GUI.skin.window);
                GUI.Box(_rect, "", GUI.skin.window);
                GUI.Box(_rect, "Trust Your Gut - save points (mod)", GUI.skin.window);
                GUI.DrawTexture(new Rect(_rect.x + 4f, _rect.y + 20f, _rect.width - 8f, _rect.height - 24f), Texture2D.whiteTexture,
                                ScaleMode.StretchToFill, true, 0f, WindowBack, 0f, 4f);
                GUILayout.BeginArea(new Rect(_rect.x + 12f, _rect.y + 24f, _rect.width - 24f, _rect.height - 34f), GUIStyle.none);
                DrawWindow(0);
                GUILayout.EndArea();
                if (Event.current.type == EventType.Repaint) DrawDetailsPanel(sw, sh);
            }
            GUI.matrix = old;
        }

        private void DrawWindow(int id)
        {
            GUILayout.Space(4);
            string now = _current == null ? "?" : (_current.HasRun ? _current.Summary().Replace("  [backup]", "") : "no run file (dead or new)");
            GUILayout.Label("Now: " + now, _title);
            GUILayout.Label("Folder: " + SavePointStore.Root, _small);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Make save point (" + Plugin.KeyQuickSave.Value + ")", GUILayout.Height(30), GUILayout.Width(230)))
            {
                Queue(QuickSave);
            }
            if (GUILayout.Button("Refresh", GUILayout.Height(30), GUILayout.Width(100))) Queue(Refresh);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close (" + Plugin.KeyWindow.Value + ")", GUILayout.Height(30), GUILayout.Width(120))) Queue(() => Close());
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status, _small);
            GUILayout.Space(6);

            bool repaint = Event.current.type == EventType.Repaint;
            if (repaint) _hoverRow = -1;
            Rect top = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none, GUILayout.Height(0f));
            if (repaint) _scrollTop = top.y;
            _scroll = GUILayout.BeginScrollView(_scroll);
            if (_list.Count == 0) GUILayout.Label("No save points yet.", _row);
            int index = -1;
            foreach (SavePointInfo sp in _list)
            {
                SavePointInfo row = sp;
                index++;
                GUILayout.BeginHorizontal();
                if (_renameDir == row.Dir)
                {
                    DrawRenameRow(row);
                    GUILayout.EndHorizontal();
                    continue;
                }
                GUILayout.BeginVertical(GUIStyle.none, GUILayout.ExpandWidth(true));
                GUILayout.Label((row.Pin > 0 ? "[" + row.Pin + "]  " : "") + row.Title(), _rowTitle);
                GUILayout.Label((row.Label != null && row.When != DateTime.MinValue ? row.When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "   " : "") +
                                row.Summary(), _rowSub);
                if (row.Details != null && row.Details.Mementos.Count + row.Details.Balls.Count > 0) DrawStrip(row, index);
                GUILayout.EndVertical();
                if (row.HasRun && row.ReadError != null)
                {
                    GUILayout.Label("cannot read: no restore", _small, GUILayout.Width(190));
                }
                else if (row.HasRun)
                {
                    string kRun = row.Dir + "|run";
                    string kFull = row.Dir + "|full";
                    if (GUILayout.Button(Confirming(kRun) ? "Click again" : "Restore run", GUILayout.Width(110), GUILayout.Height(30)))
                    {
                        if (Confirm(kRun)) Queue(() => StartRestore(row, false));
                    }
                    if (GUILayout.Button(Confirming(kFull) ? "Click again" : "Full", GUILayout.Width(80), GUILayout.Height(30)))
                    {
                        if (Confirm(kFull)) Queue(() => StartRestore(row, true));
                    }
                }
                GUILayout.Space(10);
                if (GUILayout.Button("Rename", GUILayout.Width(80), GUILayout.Height(30)))
                {
                    Queue(() =>
                    {
                        _renameDir = row.Dir;
                        _renameText = row.Label ?? "";
                        _confirmKey = null;
                    });
                }
                if (GUILayout.Button(row.Pin > 0 ? "Unpin" : "Pin", GUILayout.Width(64), GUILayout.Height(30)))
                {
                    Queue(() => _status = row.Pin > 0 ? DoUnpin(row) : DoPin(row));
                }
                string kDel = row.Dir + "|del";
                if (row.Pin > 0)
                {
                    GUILayout.Label("pinned", _small, GUILayout.Width(70)); // unpin first: a pinned save point cannot be deleted
                }
                else if (GUILayout.Button(Confirming(kDel) ? "Sure?" : "Delete", GUILayout.Width(70), GUILayout.Height(30)))
                {
                    if (Confirm(kDel)) Queue(() => _status = DoDelete(row));
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(2);
            }
            GUILayout.EndScrollView();
            Rect bottom = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none, GUILayout.Height(0f));
            if (repaint) _scrollBottom = bottom.y;

            GUILayout.Label("Restore run = only the run (day, money, wheel, bets). Keeps your newer unlocks and stats. " +
                            "Full = also unlocks, stats and last character. Before a restore the mod saves the current state as 'auto'. " +
                            "Pin keeps up to " + SavePointMeta.MaxPins + " at the top. Delete moves the folder to .trash in the backup folder. " +
                            "Point at the item icons to see all items in run order; click them to keep the list open. " +
                            (RestoreMark.On ? "Save points are for strategy practice: restored runs do not count for leaderboards or Steam achievements. " : "") +
                            "Board sync: " + (BoardSync.Enabled ? "ON" : "OFF") + " (" + Plugin.KeyBoardToggle.Value + ").", _small);
        }

        // ---------- item icons ----------

        private static readonly Color PanelBack = new Color(0.05f, 0.08f, 0.065f, 0.97f);
        private static readonly Color WindowBack = new Color(0.07f, 0.08f, 0.08f, 0.82f);
        private static readonly Color PanelEdge = new Color(0.83f, 0.66f, 0.29f, 0.9f);
        private static readonly Color IconBack = new Color(0f, 0f, 0f, 0.55f);

        // One icon: dark rounded back, the item picture (or two letters), a border in the item's rarity color.
        // Call it in every event: the picture's clip group takes a control id, so the count of ids must be the same in
        // all events. draw = false (not Repaint, or out of view) only skips the drawing.
        private void DrawIcon(Rect r, ItemIcons.Entry e, GUIStyle badge, bool draw)
        {
            if (draw) GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, IconBack, 0f, 4f);
            if (e.Sheet != null) DrawPicture(new Rect(r.x + 2f, r.y + 2f, r.width - 4f, r.height - 4f), e.Sheet, e.Uv, draw);
            else if (draw) GUI.Label(r, ItemIcons.Badge(e.Name), badge);
            if (draw) GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, e.Rarity, 1.5f, 4f);
        }

        private static readonly GUIContent NoContent = new GUIContent("");

        // Draws one picture from a sheet, fitted into box with its own shape kept. This IMGUI has no call that draws
        // part of a texture, so the whole sheet is drawn, scaled and moved, inside a clip group the size of the picture.
        private static void DrawPicture(Rect box, Texture sheet, Rect uv, bool draw)
        {
            float pw = uv.width * sheet.width, ph = uv.height * sheet.height;
            float k = pw > 0f && ph > 0f ? Mathf.Min(box.width / pw, box.height / ph) : 0f;
            float w = pw * k, h = ph * k;
            GUI.BeginGroup(new Rect(box.x + (box.width - w) / 2f, box.y + (box.height - h) / 2f, w, h), NoContent, GUIStyle.none);
            if (draw && k > 0f)
            {
                float fw = sheet.width * k, fh = sheet.height * k;
                // UV y runs bottom to top; GUI y runs top to bottom.
                GUI.DrawTexture(new Rect(-uv.x * fw, -(1f - uv.yMax) * fh, fw, fh), sheet, ScaleMode.StretchToFill, true);
            }
            GUI.EndGroup();
        }

        // Small icons under a row: mementos, a gap, then the balls in machine order. Point at them for the full
        // list; click them to keep the list open.
        private void DrawStrip(SavePointInfo sp, int index)
        {
            const float size = 22f, step = 24f, gap = 10f;
            SaveDetails d = sp.Details;
            int total = d.Mementos.Count + d.Balls.Count;
            int max = Mathf.Max(6, (int)((_windowW - 520f) / step));
            int budget = total <= max ? total : max - 1; // leave room for "+N"
            float width = budget * step + (d.Mementos.Count > 0 && d.Balls.Count > 0 ? gap : 0f) + (total > budget ? 40f : 0f);
            Rect strip = GUILayoutUtility.GetRect(width, size + 2f, GUIStyle.none, GUILayout.Width(width), GUILayout.Height(size + 2f));
            bool repaint = Event.current.type == EventType.Repaint;
            float viewH = _scrollBottom - _scrollTop;
            bool inView = viewH <= 0f || (strip.yMax >= _scroll.y && strip.y <= _scroll.y + viewH);
            bool draw = repaint && inView;
            if (!_strips.TryGetValue(sp.Dir, out ItemIcons.Entry[] icons) || icons.Length != total) icons = null;
            float x = strip.x;
            int shown = 0;
            for (int k = 0; k < d.Mementos.Count && shown < budget; k++, shown++, x += step)
            {
                DrawIcon(new Rect(x, strip.y + 1f, size, size), icons != null ? icons[k] : ItemIcons.Memento(d.Mementos[k]), _badge, draw);
            }
            if (d.Mementos.Count > 0 && d.Balls.Count > 0) x += gap;
            for (int k = 0; k < d.Balls.Count && shown < budget; k++, shown++, x += step)
            {
                DrawIcon(new Rect(x, strip.y + 1f, size, size), icons != null ? icons[d.Mementos.Count + k] : ItemIcons.Ball(d.Balls[k]), _badge, draw);
            }
            if (draw && total > shown) GUI.Label(new Rect(x + 2f, strip.y, 40f, size + 2f), "+" + (total - shown), _panelText);
            if (repaint && strip.Contains(Event.current.mousePosition)) _hoverRow = index;
            if (GUI.Button(strip, new GUIContent(""), GUIStyle.none))
            {
                Queue(() => _detailsDir = _detailsDir == sp.Dir ? null : sp.Dir);
            }
        }

        // The full item list of one save point, in run order, drawn on top of the window (Repaint only; it takes no
        // clicks). Next to the mouse while pointing at a row's icons; at the window's right edge when kept open.
        private void DrawDetailsPanel(float sw, float sh)
        {
            _keptPanel = new Rect();
            SavePointInfo sp = null;
            bool atMouse = false;
            Vector2 m = Event.current.mousePosition;
            float areaX = _rect.x + 12f, areaY = _rect.y + 24f;
            bool inList = m.x >= areaX && m.x <= _rect.xMax - 12f && m.y >= areaY + _scrollTop && m.y <= areaY + _scrollBottom;
            if (_hoverRow >= 0 && _hoverRow < _list.Count && inList)
            {
                sp = _list[_hoverRow];
                atMouse = true;
            }
            else if (_detailsDir != null)
            {
                sp = _list.Find(x => x.Dir == _detailsDir);
            }
            if (sp == null || sp.Details == null) return;
            SaveDetails d = sp.Details;

            const float pad = 14f, line = 18f;
            float W = 600f, cell = 96f, icon = 44f, nameH = 30f;
            int cols = (int)((W - 2f * pad) / cell);
            float rowH = icon + nameH + 4f;
            float maxH = sh - 16f, maxW = Mathf.Max(600f, sw - 16f);
            float fixedH = pad + 22f + line + line + 6f + pad + 2f * line; // title, facts and extras (upper bound)
            for (int pass = 0; pass < 6 && fixedH + SectionHeight(d.Mementos.Count, cols, rowH) + SectionHeight(d.Balls.Count, cols, rowH) +
                                           SectionHeight(d.BallSlots.Count, cols, rowH) > maxH; pass++)
            {
                if (W < maxW && W < 1100f) W = Mathf.Min(Mathf.Min(maxW, 1100f), W + 250f); // wider first
                else
                {
                    cell = Mathf.Max(60f, cell - 12f); // then smaller cells
                    icon = Mathf.Max(28f, icon - 6f);
                    nameH = 26f;
                }
                cols = Mathf.Max(1, (int)((W - 2f * pad) / cell));
                rowH = icon + nameH + 4f;
            }
            var facts = new List<string>();
            if (d.Tokens >= 0) facts.Add("Tokens " + d.Tokens.ToString("#,0.##", System.Globalization.CultureInfo.InvariantCulture));
            if (d.BetLevel >= 0) facts.Add("Bet limit level " + d.BetLevel);
            if (d.CutParts.Count > 0) facts.Add("Cut: " + string.Join(", ", d.CutParts.ConvertAll(SaveDetails.PartName).ToArray()));
            var extras = new List<string>();
            if (d.Consumables.Count > 0) extras.Add("Consumables: " + string.Join(", ", d.Consumables.ToArray()));
            if (d.Starters.Count > 0) extras.Add("Starter items: " + string.Join(", ", d.Starters.ConvertAll(id => ItemIcons.Memento(id).Name).ToArray()));

            float h = pad + 22f + line + (facts.Count > 0 ? line : 0f) + 6f;
            h += SectionHeight(d.Mementos.Count, cols, rowH) + SectionHeight(d.Balls.Count, cols, rowH) + SectionHeight(d.BallSlots.Count, cols, rowH);
            h += extras.Count * line + pad;

            float x, y;
            if (atMouse)
            {
                x = m.x + 18f;
                if (x + W > sw - 8f) x = m.x - W - 18f;
                y = m.y - 40f;
            }
            else
            {
                x = _rect.xMax - W - 12f;
                y = _rect.y + 60f;
            }
            x = Mathf.Clamp(x, 8f, Mathf.Max(8f, sw - W - 8f));
            y = Mathf.Clamp(y, 8f, Mathf.Max(8f, sh - h - 8f));

            Rect r = new Rect(x, y, W, h);
            if (!atMouse) _keptPanel = r;
            // Still too tall (a huge run on a small screen): the clip group cuts it at the screen edge.
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, PanelBack, 0f, 8f);
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, PanelEdge, 1.5f, 8f);
            float cx = x + pad, cy = y + pad, iw = W - 2f * pad;
            GUI.Label(new Rect(cx, cy, iw, 22f), (sp.Pin > 0 ? "[" + sp.Pin + "]  " : "") + sp.Title() + (atMouse ? "" : "   (click here to close)"), _title);
            cy += 22f;
            GUI.Label(new Rect(cx, cy, iw, line), sp.Summary(), _panelText);
            cy += line;
            if (facts.Count > 0)
            {
                GUI.Label(new Rect(cx, cy, iw, line), string.Join("   ·   ", facts.ToArray()), _panelText);
                cy += line;
            }
            cy += 6f;
            cy = Section(cx, cy, iw, "Mementos", d.Mementos, false, cols, cell, icon, nameH, rowH);
            cy = Section(cx, cy, iw, "Balls in the machine", d.Balls, true, cols, cell, icon, nameH, rowH);
            cy = Section(cx, cy, iw, "Ball slots", d.BallSlots, true, cols, cell, icon, nameH, rowH);
            foreach (string t in extras)
            {
                GUI.Label(new Rect(cx, cy, iw, line), t, _panelText);
                cy += line;
            }
        }

        private static float SectionHeight(int n, int cols, float rowH)
        {
            return n == 0 ? 0f : 22f + ((n + cols - 1) / cols) * rowH;
        }

        private float Section(float x, float y, float width, string title, List<string> ids, bool balls, int cols, float cell, float icon, float nameH, float rowH)
        {
            if (ids.Count == 0) return y;
            GUI.Label(new Rect(x, y, width, 20f), title + " (" + ids.Count + ")", _panelTitle);
            y += 22f;
            for (int k = 0; k < ids.Count; k++)
            {
                ItemIcons.Entry e = balls ? ItemIcons.Ball(ids[k]) : ItemIcons.Memento(ids[k]);
                float cx = x + (k % cols) * cell, cy = y + (k / cols) * rowH;
                DrawIcon(new Rect(cx + (cell - icon) / 2f, cy, icon, icon), e, _badgeBig, true);
                GUI.Label(new Rect(cx + 2f, cy + icon + 1f, cell - 4f, nameH), (k + 1) + ". " + e.Name, _cellName);
            }
            return y + ((ids.Count + cols - 1) / cols) * rowH;
        }

        // Inline name field. This game's IMGUI has no TextField (stripped), so the field is drawn here and reads the
        // typed characters from the GUI key events: letters add, Backspace removes the last one, Enter or Save keeps
        // the name, Esc or Cancel stops. An empty name removes the name.
        private void DrawRenameRow(SavePointInfo sp)
        {
            Event e = Event.current;
            bool save = false, cancel = false;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    save = true;
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    cancel = true;
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Backspace)
                {
                    _renameText = RemoveLastChar(_renameText);
                    e.Use();
                }
                // Ctrl or Cmd chords are not text; Ctrl+Alt together is AltGr on Windows keyboards (for example @ or €).
                else if (e.character != '\0' && !char.IsControl(e.character) && !e.command && (!e.control || e.alt))
                {
                    if ((_renameText ?? "").Length < SavePointMeta.MaxLabelLength) _renameText = (_renameText ?? "") + e.character;
                    e.Use();
                }
            }
            GUILayout.Label("Name:", _rowTitle, GUILayout.Width(52), GUILayout.Height(30));
            bool caret = ((int)(Time.unscaledTime * 2f) & 1) == 0;
            GUILayout.Label((_renameText ?? "") + (caret ? "|" : " "), _field, GUILayout.ExpandWidth(true), GUILayout.Height(30));
            if (GUILayout.Button("Save", GUILayout.Width(80), GUILayout.Height(30))) save = true;
            if (GUILayout.Button("Cancel", GUILayout.Width(80), GUILayout.Height(30))) cancel = true;
            if (save)
            {
                string text = _renameText;
                Queue(() =>
                {
                    _status = DoRename(sp, text, out bool ok);
                    if (ok) _renameDir = null; // on a failure the field stays open with the typed text
                });
            }
            else if (cancel)
            {
                Queue(() =>
                {
                    _renameDir = null;
                    _status = "Rename stopped.";
                });
            }
        }

        private static string RemoveLastChar(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int cut = s.Length - 1;
            if (cut > 0 && char.IsLowSurrogate(s[cut]) && char.IsHighSurrogate(s[cut - 1])) cut--; // two-part character
            return s.Substring(0, cut);
        }
    }
}
