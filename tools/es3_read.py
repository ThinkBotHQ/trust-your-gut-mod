"""Read-only: decrypt an Easy Save 3 file copy (AES-128-CBC, PBKDF2-SHA1 100 iters, IV prefix) to stdout.

Usage: uv run -q --with cryptography python -I es3_read.py <file.es3> [key]
The save key is not included. Give it as the second argument, set TYG_SAVE_KEY, or put it on one line in
tools/.save-key (git ignores it).
"""
import gzip
import hashlib
import os
import sys

from cryptography.hazmat.primitives import padding
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

path = sys.argv[1]
password = sys.argv[2] if len(sys.argv) > 2 else os.environ.get("TYG_SAVE_KEY", "").strip()
if not password:
    f = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".save-key")
    if os.path.isfile(f):
        password = open(f, encoding="utf-8").read().strip()
if not password:
    sys.exit("Save key not included. Give it as the second argument, set TYG_SAVE_KEY or put it in tools/.save-key.")
data = open(path, "rb").read()
iv, body = data[:16], data[16:]
key = hashlib.pbkdf2_hmac("sha1", password.encode(), iv, 100, dklen=16)
dec = Cipher(algorithms.AES(key), modes.CBC(iv)).decryptor()
plain = dec.update(body) + dec.finalize()
unpad = padding.PKCS7(128).unpadder()
plain = unpad.update(plain) + unpad.finalize()
if plain[:2] == b"\x1f\x8b":
    plain = gzip.decompress(plain)
sys.stdout.write(plain.decode("utf-8", "replace"))
