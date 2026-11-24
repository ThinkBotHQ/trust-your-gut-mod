# Read only: decrypt an ES3 save (AES-128-CBC, PBKDF2-SHA1 key from the game's save key) to plain JSON.
# The save key is not included. Set TYG_SAVE_KEY, or put the key on one line in tools/.save-key (git ignores it).
# Usage: uv run -q --with cryptography python -I decrypt_save.py <in.es3> <out.json>
import gzip
import hashlib
import os
import sys

from cryptography.hazmat.primitives import padding
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes


def save_key():
    k = os.environ.get("TYG_SAVE_KEY", "").strip()
    if not k:
        f = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".save-key")
        if os.path.isfile(f):
            k = open(f, encoding="utf-8").read().strip()
    if not k:
        sys.exit("Save key not included. Set TYG_SAVE_KEY or put it in tools/.save-key.")
    return k.encode()


data = open(sys.argv[1], "rb").read()
iv, body = data[:16], data[16:]
key = hashlib.pbkdf2_hmac("sha1", save_key(), iv, 100, dklen=16)
d = Cipher(algorithms.AES(key), modes.CBC(iv)).decryptor()
pl = d.update(body) + d.finalize()
u = padding.PKCS7(128).unpadder()
pl = u.update(pl) + u.finalize()
if pl[:2] == b"\x1f\x8b":
    pl = gzip.decompress(pl)
open(sys.argv[2], "wb").write(pl)
