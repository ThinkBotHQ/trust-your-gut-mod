using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace TygMod
{
    // Test hook, OFF by default ([Debug] CommandFile = false in TygMod.cfg).
    // When it is on, the mod reads <mod folder>\TygMod.cmd about once a second, deletes the file, and runs each line:
    //   savepoint                     same as F9
    //   open / close                  same as F8 (open pauses a run)
    //   list                          write the save point folder names to TygMod.log
    //   restore <folder name> run     same as "Restore run" in the window (no second click needed)
    //   restore <folder name> full    same as "Full"
    //   rename <folder name> <name>   same as "Rename" + Save (no name = remove the name)
    //   pin / unpin <folder name>     same as "Pin" / "Unpin"
    //   delete <folder name>          same as "Delete" (no second click needed); the folder goes to .trash
    //   details [folder name]         keep that save point's item panel open (no name = close it)
    //   proof                         same as F6 (board sync off/on screenshots)
    //   shot <name>                   one screenshot with the mod's UI visible, to the proof folder
    // It only starts the mod's own features (the same code as the hotkeys and buttons). It is for testing
    // the mod when no mouse or keyboard can reach the game window.
    internal sealed class TestCommands : MonoBehaviour
    {
        private float _next;

        private void Update()
        {
            if (Plugin.CommandFile == null || !Plugin.CommandFile.Value) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            string path = Path.Combine(Plugin.ModDir ?? ".", "TygMod.cmd");
            string[] lines;
            try
            {
                if (!File.Exists(path)) return;
                lines = File.ReadAllLines(path, Encoding.UTF8);
                File.Delete(path);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Command file: " + e.Message);
                return;
            }
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                Plugin.Log.LogInfo("Command: " + line);
                try
                {
                    Run(line);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Command failed: " + line + ": " + e);
                }
            }
        }

        private static void Run(string line)
        {
            string[] p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            switch (p[0].ToLowerInvariant())
            {
                case "savepoint":
                    SavePointUI.CommandQuickSave();
                    break;
                case "open":
                    SavePointUI.CommandOpen();
                    break;
                case "close":
                    SavePointUI.CommandClose();
                    break;
                case "list":
                    foreach (SavePointInfo sp in SavePointStore.List())
                    {
                        Plugin.Log.LogInfo("  " + sp.Name + (sp.Pin > 0 ? "  [pin " + sp.Pin + "]" : "") +
                                           (sp.Label != null ? "  '" + sp.Label + "'" : "") + "  " + sp.Summary() +
                                           (sp.ReadError != null ? "  (cannot read)" : ""));
                    }
                    break;
                case "restore":
                    if (p.Length < 2)
                    {
                        Plugin.Log.LogWarning("restore needs a save point folder name.");
                        break;
                    }
                    SavePointUI.CommandRestore(p[1], p.Length > 2 && p[2].ToLowerInvariant() == "full");
                    break;
                case "rename":
                {
                    if (p.Length < 2)
                    {
                        Plugin.Log.LogWarning("rename needs a save point folder name.");
                        break;
                    }
                    // The name is the rest of the line after the folder name, spaces kept.
                    string rest = line.Substring(p[0].Length).TrimStart();
                    string text = rest.Substring(p[1].Length).Trim();
                    SavePointUI.CommandRename(p[1], text);
                    break;
                }
                case "pin":
                case "unpin":
                    if (p.Length < 2)
                    {
                        Plugin.Log.LogWarning(p[0] + " needs a save point folder name.");
                        break;
                    }
                    SavePointUI.CommandPin(p[1], p[0].ToLowerInvariant() == "pin");
                    break;
                case "delete":
                    if (p.Length < 2)
                    {
                        Plugin.Log.LogWarning("delete needs a save point folder name.");
                        break;
                    }
                    SavePointUI.CommandDelete(p[1]);
                    break;
                case "details":
                    SavePointUI.CommandDetails(p.Length > 1 ? p[1] : null);
                    break;
                case "proof":
                    BoardSyncDriver.CommandProof();
                    break;
                case "shot":
                    Shot(p.Length > 1 ? p[1] : "shot");
                    break;
                default:
                    Plugin.Log.LogWarning("Unknown command: " + p[0]);
                    break;
            }
        }

        private static void Shot(string name)
        {
            // Keep the file name plain, so it cannot point outside the proof folder.
            var safe = new StringBuilder();
            foreach (char ch in name)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_') safe.Append(ch);
            }
            name = safe.Length > 0 ? safe.ToString() : "shot";
            string dir = SavePointStore.ProofDir;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "ui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + name + ".png");
            ScreenCapture.CaptureScreenshot(path, 1);
            Plugin.Log.LogInfo("Screenshot: " + path);
        }
    }
}
