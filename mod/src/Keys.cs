using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TygMod
{
    // Hotkeys through the new Input System (the game uses it). Falls back to the old Input class.
    internal static class Keys
    {
        private static bool _legacyBroken;

        public static bool Down(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (SavePointUI.Typing) return false; // keys typed into the rename field are not hotkeys
            try
            {
                var kb = Keyboard.current;
                if (kb != null && Enum.TryParse(name, true, out Key key) && key != Key.None)
                {
                    return kb[key].wasPressedThisFrame;
                }
            }
            catch (Exception)
            {
                // fall through to legacy input
            }
            if (_legacyBroken) return false;
            try
            {
                if (Enum.TryParse(name, true, out KeyCode code)) return Input.GetKeyDown(code);
            }
            catch (Exception)
            {
                _legacyBroken = true;
            }
            return false;
        }

        public static bool ShiftHeld()
        {
            try
            {
                var kb = Keyboard.current;
                if (kb != null) return kb.shiftKey.isPressed;
            }
            catch (Exception)
            {
            }
            return false;
        }
    }
}
