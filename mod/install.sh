#!/bin/bash
# Install TygMod into Trust Your Gut (CrossOver bottle "Steam"). Run only while the game is closed.
# Layout in the game folder:
#   winhttp.dll, .doorstop_version   Unity Doorstop 4 loader (from BepInEx 5.4.23.6's zip; BepInEx itself is not used)
#   doorstop_config.ini              points Doorstop at TygMod\TygMod.dll
#   TygMod\TygMod.dll, TygMod.cfg    the mod and its settings (TygMod.log is written here)
# This game ships a stripped .NET runtime, so BepInEx/Harmony cannot run; the mod starts itself from Doorstop.
set -euo pipefail
cd "$(dirname "$0")"
BOTTLE="${BOTTLE:-Steam}"
GAME="${GAME:-$HOME/Library/Application Support/CrossOver/Bottles/$BOTTLE/drive_c/Program Files (x86)/Steam/steamapps/common/Trust Your Gut}"
DOORSTOP="${DOORSTOP:-../vendor/bepinex-5.4.23.6}"
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
[ -f bin/TygMod.dll ] || { echo "Build first: ./build.sh" >&2; exit 1; }

# Remove an older BepInEx-based install of this mod (BepInEx cannot run on this game).
# Remove only our file; empty folders go too. Other BepInEx files (config, patchers, core) are left in place.
if [ -f "$GAME/BepInEx/plugins/TygMod.dll" ]; then
  rm -f "$GAME/BepInEx/plugins/TygMod.dll"
  rmdir "$GAME/BepInEx/plugins" "$GAME/BepInEx" 2>/dev/null || true
  echo "Removed old BepInEx/plugins/TygMod.dll."
fi
rm -f "$GAME"/preloader_*.log

cp "$DOORSTOP/winhttp.dll" "$DOORSTOP/.doorstop_version" "$GAME/"
python3 -I - "$DOORSTOP/doorstop_config.ini" "$GAME/doorstop_config.ini" <<'PY'
import re, sys
s = open(sys.argv[1], newline="").read()
s = re.sub(r"(?m)^enabled\s*=.*$", "enabled = true", s, count=1)
s = re.sub(r"(?m)^target_assembly\s*=.*$", r"target_assembly=TygMod\\TygMod.dll", s, count=1)
s = re.sub(r"(?m)^dll_search_path_override\s*=.*$", "dll_search_path_override =", s, count=1)
open(sys.argv[2], "w", newline="").write(s)
PY

mkdir -p "$GAME/TygMod" "$HOME/tyg-backup"
cp bin/TygMod.dll "$GAME/TygMod/TygMod.dll"
# Save points go to ~/tyg-backup (Z: is the Mac root in CrossOver). The mod adds the other settings on first start.
if [ ! -f "$GAME/TygMod/TygMod.cfg" ]; then
  printf '[SavePoints]\nBackupFolder = Z:%s\n' "$(echo "$HOME/tyg-backup" | tr '/' '\\')" > "$GAME/TygMod/TygMod.cfg"
fi

# Load our winhttp.dll (the Doorstop loader) for this game only, not for Steam.
"$WINE" --bottle "$BOTTLE" reg add 'HKCU\Software\Wine\AppDefaults\Trust Your Gut.exe\DllOverrides' /v winhttp /d native,builtin /f
"$WINE" --bottle "$BOTTLE" reg query 'HKCU\Software\Wine\AppDefaults\Trust Your Gut.exe\DllOverrides' /v winhttp

grep -nE '^(enabled|target_assembly)' "$GAME/doorstop_config.ini"
shasum -a 256 "$GAME/TygMod/TygMod.dll"
echo "Installed. Start the game from Steam, then check: $GAME/TygMod/TygMod.log"
