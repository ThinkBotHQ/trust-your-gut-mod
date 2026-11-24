#!/bin/bash
# Remove TygMod and its Doorstop loader from Trust Your Gut, and remove the per-game DLL override.
# Your saves and your save points in ~/tyg-backup are not touched.
set -euo pipefail
BOTTLE="${BOTTLE:-Steam}"
GAME="${GAME:-$HOME/Library/Application Support/CrossOver/Bottles/$BOTTLE/drive_c/Program Files (x86)/Steam/steamapps/common/Trust Your Gut}"
WINE="${WINE:-}"
for app in "/Applications/CrossOver.app" "/Applications/CrossOver Preview.app"; do
  [ -z "$WINE" ] && [ -x "$app/Contents/SharedSupport/CrossOver/bin/wine" ] && WINE="$app/Contents/SharedSupport/CrossOver/bin/wine"
done
[ -n "$WINE" ] || { echo "CrossOver wine not found. Set WINE=/path/to/CrossOver/bin/wine" >&2; exit 1; }

procs="$(ps -axo command= 2>/dev/null || true)"
if grep -qE '^[A-Z]:.*Trust Your Gut\.exe' <<<"$procs"; then
  echo "The game is running. Quit it first (do not kill it)." >&2
  exit 1
fi
[ -f "$GAME/Trust Your Gut.exe" ] || { echo "Game not found: $GAME" >&2; exit 1; }

rm -f "$GAME/winhttp.dll" "$GAME/doorstop_config.ini" "$GAME/.doorstop_version" "$GAME"/preloader_*.log
rm -rf "$GAME/TygMod"
# An older install of this mod used BepInEx; remove only our file (and folders left empty).
if [ -f "$GAME/BepInEx/plugins/TygMod.dll" ]; then
  rm -f "$GAME/BepInEx/plugins/TygMod.dll"
  rmdir "$GAME/BepInEx/plugins" "$GAME/BepInEx" 2>/dev/null || true
fi
"$WINE" --bottle "$BOTTLE" reg delete 'HKCU\Software\Wine\AppDefaults\Trust Your Gut.exe\DllOverrides' /v winhttp /f || true
echo "Removed. The game now runs without mods."
