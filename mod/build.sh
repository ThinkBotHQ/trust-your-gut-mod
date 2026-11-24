#!/bin/bash
# Build TygMod.dll offline against the game's own (stripped) DLLs. No BepInEx, no Harmony.
set -euo pipefail
cd "$(dirname "$0")"
GAME="${GAME:-$HOME/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Trust Your Gut}"
M="$GAME/Trust Your Gut_Data/Managed"
DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
CSC="$(ls -d "$DOTNET_ROOT"/sdk/*/Roslyn/bincore/csc.dll | tail -1)"
mkdir -p bin
refs=()
for d in mscorlib System System.Core UnityEngine UnityEngine.CoreModule UnityEngine.IMGUIModule \
         UnityEngine.InputLegacyModule UnityEngine.PhysicsModule UnityEngine.ScreenCaptureModule \
         UnityEngine.TextRenderingModule UnityEngine.UI Unity.TextMeshPro Unity.InputSystem \
         Assembly-CSharp Assembly-CSharp-firstpass; do
  refs+=("-r:$M/$d.dll")
done
"$DOTNET_ROOT/dotnet" "$CSC" -noconfig -nostdlib+ -target:library -langversion:9 -optimize+ -deterministic \
  -nowarn:CS1701,CS1702 -out:bin/TygMod.dll "${refs[@]}" src/*.cs
ls -l bin/TygMod.dll
shasum -a 256 bin/TygMod.dll
