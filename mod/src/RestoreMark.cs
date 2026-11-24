using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace TygMod
{
    // Save points are for strategy practice. A run restored from a save point does not count for
    // leaderboards or Steam achievements: after the restored run has loaded, the mod sets the game's own flag
    // (RunIntegrity.MarkTainted, the flag the game itself sets) and the game saves that flag in the run file with its own
    // save call. A small marker file in the mod folder keeps this true if the game closes between the copy and the load.
    //
    // When to act comes from the game's own boot events (GameBootstrapper fires one of them after SaveManager.LoadAll):
    // OnSaveLoaded = the run file was loaded (set the flag), OnNewRunStarted = a new run replaced it (drop the marker).
    // If neither fires, the marker stays for the next run load.
    internal sealed class RestoreMark : MonoBehaviour
    {
        internal static readonly bool On = true;
        private const string Reason = "save_point_restore";
        private const string MarkerName = "restore.pending";
        private const string ProbeName = "restore.probe";
        private const float SettleWait = 2f; // let the scene settle before the save

        // Also kept in memory, in case the marker file goes missing while the game runs.
        private static bool _pending;
        // Marker state from before the last Arm, so Disarm can put it back.
        private static bool _pendingBeforeArm;
        private bool _listening;
        private Coroutine _running;

        private static string MarkerPath => Path.Combine(Plugin.ModDir ?? ".", MarkerName);
        private static bool Pending => _pending || File.Exists(MarkerPath);

        // True when the mod can write its marker. Checked before a restore changes anything.
        internal static bool CanWrite()
        {
            if (!On) return true;
            string probe = Path.Combine(Plugin.ModDir ?? ".", ProbeName);
            try
            {
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Mod folder not writable: " + e.Message);
                return false;
            }
        }

        // Call right BEFORE a save point's run file is copied in. Returns false when the marker could not be written;
        // the restore must then stop.
        internal static bool Arm()
        {
            if (!On) return true;
            bool before;
            try
            {
                before = Pending;
            }
            catch (Exception)
            {
                before = _pending;
            }
            try
            {
                File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Restore marker not written: " + e.Message);
                return false;
            }
            _pendingBeforeArm = before;
            _pending = true;
            return true;
        }

        // Call when a restore stopped and the live run file is still the old one: put the marker back as it was.
        internal static void Disarm()
        {
            if (!On) return;
            if (_pendingBeforeArm) return; // a marker from an earlier restore still belongs to the live run
            Delete();
        }

        private void OnEnable()
        {
            if (!On) return;
            try
            {
                EventManager.Events.OnSaveLoaded.Get().AddListener(OnSaveLoaded);
                EventManager.Events.OnNewRunStarted.Get().AddListener(OnNewRunStarted);
                _listening = true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Restore mark: game events not found: " + e.Message);
            }
        }

        private void OnDisable()
        {
            if (!_listening) return;
            try
            {
                EventManager.Events.OnSaveLoaded.Get().RemoveListener(OnSaveLoaded);
                EventManager.Events.OnNewRunStarted.Get().RemoveListener(OnNewRunStarted);
            }
            catch (Exception)
            {
                // the game is closing
            }
            _listening = false;
        }

        private void OnNewRunStarted(object _)
        {
            try
            {
                if (!Pending) return;
                if (_running != null)
                {
                    StopCoroutine(_running);
                    _running = null;
                }
                Delete(); // a new run took the place of the restored one
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Restore mark (new run): " + e.Message);
            }
        }

        private void OnSaveLoaded(object _)
        {
            try
            {
                if (!Pending) return;
                RunIntegrity.MarkTainted(Reason);
                if (_running != null) StopCoroutine(_running);
                _running = StartCoroutine(SaveAfterSettle());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Restore mark: " + e.Message);
            }
        }

        // Let the scene settle, then save only RunGameStats (its values were just loaded from the file) with the game's
        // own save call, so the flag is in the run file. The marker goes only when the file on disk has the flag.
        private IEnumerator SaveAfterSettle()
        {
            float settle = Time.unscaledTime + SettleWait;
            while (Time.unscaledTime < settle) yield return null;
            try
            {
                SaveManager sm = SaveManager.Instance;
                RunGameStats stats = RunGameStats.Instance;
                if (sm != null && !sm.IsDead && stats != null)
                {
                    RunIntegrity.MarkTainted(Reason); // no change when already set
                    sm.SaveOnly(stats);
                    sm.WaitForPendingWrites();
                    if (FlagInRunFile(stats.SaveKey + "_tainted")) Delete();
                    else Plugin.Log.LogWarning("Restore mark: not confirmed in the run file yet; the next run load tries again.");
                }
                // Otherwise (run ended or left in the 2 s): the marker stays and the next run load tries again.
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Restore mark save: " + e.Message);
            }
            _running = null;
        }

        // Read-only check of the run file on disk.
        private static bool FlagInRunFile(string key)
        {
            try
            {
                var file = new ES3File("Save_Game.es3");
                return file.KeyExists(key) && file.Load<bool>(key, false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Restore mark check: " + e.Message);
                return false;
            }
        }

        private static void Delete()
        {
            _pending = false;
            _pendingBeforeArm = false;
            try
            {
                if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Restore marker not removed: " + e.Message);
            }
        }
    }
}
