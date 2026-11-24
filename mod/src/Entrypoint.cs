using System;
using System.IO;
using System.Reflection;
using UnityEngine.SceneManagement;

// Unity Doorstop (winhttp.dll) calls Doorstop.Entrypoint.Start() very early, before Unity loads the game code.
// This game ships a stripped .NET runtime, so BepInEx and Harmony cannot run. The mod starts itself instead:
// it waits for the game assembly, then for the first scene, then creates its own object.
namespace Doorstop
{
    public static class Entrypoint
    {
        public static void Start()
        {
            TygMod.Boot.Start();
        }
    }
}

namespace TygMod
{
    internal static class Boot
    {
        private static bool _hooked;

        public static void Start()
        {
            try
            {
                string dll = Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH");
                if (string.IsNullOrEmpty(dll)) dll = typeof(Boot).Assembly.Location;
                Plugin.ModDir = Path.GetDirectoryName(dll);
                ModLog.Open(Path.Combine(Plugin.ModDir, "TygMod.log"));
                Plugin.Log.LogInfo("TYG mod " + Plugin.Version + " started by Doorstop. Mod folder: " + Plugin.ModDir);
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (IsGameAssembly(a))
                    {
                        Hook();
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.Emergency("Boot failed: " + e);
            }
        }

        private static bool IsGameAssembly(Assembly a)
        {
            string n = a.FullName;
            return n != null && n.StartsWith("Assembly-CSharp,", StringComparison.Ordinal);
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e)
        {
            try
            {
                if (IsGameAssembly(e.LoadedAssembly)) Hook();
            }
            catch (Exception ex)
            {
                ModLog.Emergency("AssemblyLoad hook failed: " + ex);
            }
        }

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                UnityHooks.Install();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not hook scene loading, the mod is off: " + e);
            }
        }
    }

    // Kept apart from Boot so no UnityEngine code is compiled before Unity is ready.
    internal static class UnityHooks
    {
        public static void Install()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            Plugin.Log.LogInfo("Game code loaded. Waiting for the first scene.");
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                if (!Plugin.Started) Plugin.Init();
                Plugin.Log.LogInfo("Scene loaded: " + scene.name);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Mod start failed: " + e);
            }
        }
    }
}
