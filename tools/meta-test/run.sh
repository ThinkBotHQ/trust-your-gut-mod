#!/bin/bash
# Offline test of mod/src/SavePointMeta.cs against normal .NET (the game build checks the stripped API separately).
set -euo pipefail
cd "$(dirname "$0")"
DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
CSC="$(ls -d "$DOTNET_ROOT"/sdk/*/Roslyn/bincore/csc.dll | tail -1)"
REF="$(ls -d "$DOTNET_ROOT"/packs/Microsoft.NETCore.App.Ref/*/ref/net* | tail -1)"
RT="$(basename "$(ls -d "$DOTNET_ROOT"/shared/Microsoft.NETCore.App/* | tail -1)")"
OUT="${TMPDIR:-/tmp}/tyg-meta-test"
mkdir -p "$OUT"
refs=()
for f in "$REF"/*.dll; do refs+=("-r:$f"); done
"$DOTNET_ROOT/dotnet" "$CSC" -noconfig -nostdlib+ -nologo -target:exe -langversion:latest -nowarn:CS1701,CS1702 \
  -out:"$OUT/MetaTest.dll" "${refs[@]}" MetaTest.cs DetailsTest.cs ../../mod/src/SavePointMeta.cs ../../mod/src/SaveDetails.cs
cat > "$OUT/MetaTest.runtimeconfig.json" <<EOF
{ "runtimeOptions": { "tfm": "net${RT%%.*}.0", "framework": { "name": "Microsoft.NETCore.App", "version": "$RT" } } }
EOF
# Real-save check: decrypt (read only) a copy of one save point's run file into the temp folder.
# Needs the save key (not included): TYG_SAVE_KEY or tools/.save-key. Without it, only the fixed cases run.
SP="${SP:-$HOME/tyg-backup/sp-20261123-003541}"
JSON=""
if [ -f "$SP/Trust Your Gut/Save_Game.es3" ] && { [ -n "${TYG_SAVE_KEY:-}" ] || [ -s ../.save-key ]; }; then
  JSON="$OUT/$(basename "$SP").json"
  uv run -q --with cryptography python -I decrypt_save.py "$SP/Trust Your Gut/Save_Game.es3" "$JSON"
fi
"$DOTNET_ROOT/dotnet" exec "$OUT/MetaTest.dll" $JSON
