#!/bin/bash
# Read-only: copy the live Trust Your Gut run save to a temp folder, decrypt the copy, print taint + day.
# Usage: bash tools/taint_check.sh
# Run it with the game quit or in the main menu (the game writes the file on quit and at many actions).
# The save key is not included. Set TYG_SAVE_KEY, or put the key on one line in tools/.save-key (git ignores it).
set -u
KEYFILE="$(cd "$(dirname "$0")" && pwd)/.save-key"
if [ -z "${TYG_SAVE_KEY:-}" ] && [ ! -s "$KEYFILE" ]; then
  echo "Save key not included. Set TYG_SAVE_KEY or put it in tools/.save-key."
  exit 1
fi
SAVE="$HOME/Library/Application Support/CrossOver/Bottles/Steam/drive_c/users/crossover/AppData/LocalLow/Mishleto/Trust Your Gut/Save_Game.es3"
if [ ! -f "$SAVE" ]; then
  echo "No run save (no run in progress, or the run ended)."
  exit 0
fi
TMP="$(mktemp -d)"
cp "$SAVE" "$TMP/Save_Game.es3"
uv run -q --with cryptography python -I - "$TMP/Save_Game.es3" "$KEYFILE" <<'PY'
import gzip, hashlib, json, os, sys
k = os.environ.get("TYG_SAVE_KEY", "").strip() or open(sys.argv[2], encoding="utf-8").read().strip()
if not k:
    sys.exit("Save key is empty. Set TYG_SAVE_KEY or put it in tools/.save-key.")
from cryptography.hazmat.primitives import padding
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
data = open(sys.argv[1], "rb").read()
iv, body = data[:16], data[16:]
key = hashlib.pbkdf2_hmac("sha1", k.encode(), iv, 100, dklen=16)
dec = Cipher(algorithms.AES(key), modes.CBC(iv)).decryptor()
plain = dec.update(body) + dec.finalize()
un = padding.PKCS7(128).unpadder()
plain = un.update(plain) + un.finalize()
if plain[:2] == b"\x1f\x8b":
    plain = gzip.decompress(plain)
d = json.loads(plain.decode("utf-8", "replace"))
val = lambda k: (d.get(k) or {}).get("value") if isinstance(d.get(k), dict) else d.get(k)
print("tainted :", val("RunGameStats_tainted"))
print("reason  :", val("RunGameStats_taintReason") or "-")
print("day     :", val("RoundManagerData_currentDay"))
PY
rm -rf "$TMP"
