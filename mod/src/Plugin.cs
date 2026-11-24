using System.IO;
using UnityEngine;

namespace TygMod
{
    // Two features for Trust Your Gut:
    //  1. Board sync: the betting table tiles show the real wheel pocket state.
    //  2. Save points: make, list and restore copies of the save folder from inside the game.
    // Both are visual / file-copy only. Nothing here calls a console command or touches money.
    internal static class Plugin
    {
        public const string Version = "1.3.1";

        internal static readonly ModLog Log = new ModLog();
        internal static string ModDir;
        internal static bool Started;

        internal static Setting<bool> BoardSyncEnabled;
        internal static Setting<bool> ShowSkulls;
        internal static Setting<bool> ShowModifierIcons;
        internal static Setting<bool> ShowCopyCount;
        internal static Setting<string> KeyBoardToggle;
        internal static Setting<string> KeyProofCapture;
        internal static Setting<string> KeyWindow;
        internal static Setting<string> KeyQuickSave;
        internal static Setting<string> BackupFolder;
        internal static Setting<string> ProofFolder;
        internal static Setting<bool> AutoSavePointBeforeRestore;
        internal static Setting<float> UiScale;
        internal static Setting<bool> CommandFile;

        // Runs once, on the first scene load (main thread, Unity ready).
        public static void Init()
        {
            if (Started) return;
            Started = true;
            ModLog.UnitySink = (level, msg) =>
            {
                if (level == "Info ") Debug.Log("[TygMod] " + msg);
                else Debug.LogWarning("[TygMod] " + level.Trim() + ": " + msg);
            };

            ModConfig.Load(Path.Combine(ModDir ?? ".", "TygMod.cfg"));
            BoardSyncEnabled = ModConfig.Bind("BoardSync", "Enabled", true,
                "Color the table tiles like the wheel pockets. Toggle in game with the board hotkey.");
            ShowSkulls = ModConfig.Bind("BoardSync", "ShowSkulls", true,
                "Show a skull on the table tile of a death pocket.");
            ShowModifierIcons = ModConfig.Bind("BoardSync", "ShowModifierIcons", true,
                "Show small pocket modifier icons (for example heat marks) on the table tile.");
            ShowCopyCount = ModConfig.Bind("BoardSync", "ShowCopyCount", true,
                "Add a small x2, x3 to the tile number when more than one pocket shows that number.");
            ProofFolder = ModConfig.Bind("BoardSync", "ProofFolder", "",
                "Windows path for proof screenshots. Empty = <BackupFolder>\\board-proof.");
            KeyBoardToggle = ModConfig.Bind("Hotkeys", "BoardToggle", "F7", "Turn board sync on or off.");
            KeyProofCapture = ModConfig.Bind("Hotkeys", "ProofCapture", "F6",
                "Take two screenshots: board sync off, then on. They go to ProofFolder.");
            KeyWindow = ModConfig.Bind("Hotkeys", "SaveWindow", "F8", "Open or close the save point window.");
            KeyQuickSave = ModConfig.Bind("Hotkeys", "QuickSavePoint", "F9", "Make a save point at once, no window.");
            BackupFolder = ModConfig.Bind("SavePoints", "BackupFolder", "",
                "Windows path of the backup folder. Empty = auto (Z:\\<your mac home>\\tyg-backup).");
            AutoSavePointBeforeRestore = ModConfig.Bind("SavePoints", "AutoSavePointBeforeRestore", true,
                "Before a restore, make a save point of the current state (kind 'auto'), so a wrong restore can be undone.");
            UiScale = ModConfig.Bind("SavePoints", "UiScale", 0f, "Window scale. 0 = auto from screen height.");
            CommandFile = ModConfig.Bind("Debug", "CommandFile", false,
                "Test hook. When true, the mod runs commands from TygMod.cmd in the mod folder (savepoint, open, close, list, restore <name> run|full, rename <name> <text>, pin|unpin <name>, delete <name>, proof, shot <name>). Keep false.");
            ModConfig.Save();

            // Own root object, so a scene change never removes the mod.
            var host = new GameObject("TygMod_Host");
            host.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(host);
            host.AddComponent<BoardSyncDriver>();
            host.AddComponent<SavePointUI>();
            host.AddComponent<RestoreMark>();
            host.AddComponent<TestCommands>();

            // If false, the game saves on focus loss (OnApplicationPause). The setter is stripped, so only log it.
            Log.LogInfo("Application.runInBackground = " + Application.runInBackground);
            Log.LogInfo("TYG mod " + Version + " ready. Save folder: " + SavePointStore.SaveDir + "  Backup folder: " + SavePointStore.Root);
        }
    }
}
