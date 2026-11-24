using System;
using System.Reflection;
using UnityEngine.SceneManagement;

namespace TygMod
{
    // Helpers around the game's SaveManager. No patches: everything here uses the game's own public save calls
    // or resets private "pending" state with reflection, the same state a fresh "Continue" starts from.
    internal static class SaveGate
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // True from the start of a restore until the restored run has loaded (or the restore failed).
        public static bool RestoreInProgress;

        private static bool _spinCheckWarned, _pauseCheckWarned;

        private static object Get(object o, string field)
        {
            FieldInfo f = o.GetType().GetField(field, Inst);
            if (f == null) throw new MissingFieldException("SaveManager." + field);
            return f.GetValue(o);
        }

        private static void Set(object o, string field, object value)
        {
            FieldInfo f = o.GetType().GetField(field, Inst);
            if (f == null) throw new MissingFieldException("SaveManager." + field);
            f.SetValue(o, value);
        }

        private static void SetProperty(object o, string name, object value)
        {
            PropertyInfo p = typeof(SaveManager).GetProperty(name, Inst);
            MethodInfo setter = p != null ? p.GetSetMethod(true) : null;
            if (setter == null) throw new MissingMethodException("SaveManager." + name + " setter");
            setter.Invoke(o, new[] { value });
        }

        // The game holds deferred saves while the wheel spins. The mod does not copy or restore mid-spin.
        // If a game update renames the field, this says "spinning" (refuse) instead of letting a save run mid-spin.
        public static bool IsSpinning(SaveManager sm)
        {
            if (sm == null) return false;
            try
            {
                return (bool)Get(sm, "_spinActive");
            }
            catch (Exception e)
            {
                if (!_spinCheckWarned)
                {
                    _spinCheckWarned = true;
                    Plugin.Log.LogError("Spin check failed (game update?). Save points and restores in a run are refused: " + e.Message);
                }
                return true;
            }
        }

        // The game's own checks before its pause menu opens (PauseMenuManager.Pause): not during the ending, and not
        // while a sequence (elevator ride, collar, ladder) blocks the pause. "Quit to menu" is only reachable from the
        // pause menu, so the mod makes its full save only when the game would let you pause and quit there too.
        // The blockers only add a mark to the context; asking them changes nothing in the game.
        public static bool PauseBlocked(out string why)
        {
            why = null;
            try
            {
                if (EndGameManager.IsEndingGame)
                {
                    why = "the ending";
                    return true;
                }
                var ctx = new PreCheckContext();
                EventManager.Events.OnInteractionPreCheckAction.Get("Pause").Invoke(ctx);
                if (ctx.HasBlockers)
                {
                    why = "this game sequence";
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                if (!_pauseCheckWarned)
                {
                    _pauseCheckWarned = true;
                    Plugin.Log.LogError("Pause check failed (game update?). Save points and restores in a run are refused: " + e.Message);
                }
                why = "a failed check (see TygMod.log)";
                return true;
            }
        }

        // What the game itself does when its window loses focus: write pending saves now.
        public static void FlushLikeFocusLoss(SaveManager sm)
        {
            if (sm == null) return;
            sm.FlushDeferred();
            sm.WaitForPendingWrites();
        }

        // What the pause menu's "Quit to menu" does before it leaves the run: SaveAll.
        // Only in the run scene, only while alive, not mid-spin and only when the game would let you pause
        // (see PauseBlocked); elsewhere SaveAll could write an empty or half-done run file.
        public static void SaveLikeQuitToMenu(SaveManager sm)
        {
            if (sm == null) return;
            if (SceneManager.GetActiveScene().name == "GameScene" && !sm.IsDead && !IsSpinning(sm) && !PauseBlocked(out _)) sm.SaveAll();
            sm.WaitForPendingWrites();
        }

        // Puts SaveManager in the state a fresh "Continue" from the main menu starts from:
        // nothing pending, not dead, no "new run" flag, no cached run file. The next LoadAll reads the file on disk.
        public static void PrepareForRestore(SaveManager sm)
        {
            if (sm == null) throw new InvalidOperationException("SaveManager not found");
            if (Get(sm, "_deferredRun") is System.Collections.IList deferred) deferred.Clear();
            Set(sm, "_deferredPersistent", false);
            Set(sm, "_deferredFirstRequest", -1f);
            Set(sm, "_runCommitPending", false);
            Set(sm, "_persistentSavePending", false);
            if (Get(sm, "_writer") is BackgroundSaveWriter writer)
            {
                writer.WaitIdle();
                writer.ConsumeFailure();
            }
            Set(sm, "_runFile", null);
            Set(sm, "_newRunPending", false);
            SetProperty(sm, "IsDead", false);
            SetProperty(sm, "WasLoadedFromSave", false);
        }
    }
}
