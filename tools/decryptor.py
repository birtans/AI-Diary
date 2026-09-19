#!/usr/bin/env python3
"""
AI Diary reference decryptor.

Decrypts an encrypted diary export with a recovery key and nothing else.
The export directory contains recovery.dat, keys.json and one .md.enc
object per note, exactly as produced by the "encrypted export" endpoint.

Usage:
    python3 decryptor.py --key RECOVERY-KEY --dir EXPORT_DIR [--out OUTPUT_DIR]

Python standard library only; no third-party dependencies.
AES-256-GCM is implemented here directly (key expansion, CTR, GHASH).
"""

import argparse
import hashlib
import hmac
import json
import os
import struct
import sys
import uuid

MAGIC = b"WNMD1"
ENVELOPE_VERSION = 1
R = 0xE1000000000000000000000000000000  # reduction polynomial, MSB-aligned

# ---------------------------------------------------------------- AES tables


def _rotl8(x, n):
    return ((x << n) | (x >> (8 - n))) & 0xFF


def _gf_mul8(a, b):
    r = 0
    while b:
        if b & 1:
            r ^= a
        b >>= 1
        a <<= 1
        if a & 0x100:
            a ^= 0x11B
    return r & 0xFF


def _gf_inv8(a):
    """Multiplicative inverse in GF(2^8) with polynomial x^8+x^4+x^3+x+1."""
    if a == 0:
        return 0
    result = 1
    base = a
    exp = 254
    while exp:
        if exp & 1:
            result = _gf_mul8(result, base)
        base = _gf_mul8(base, base)
        exp >>= 1
    return result


def _build_sbox():
    sbox = [0] * 256
    for a in range(256):
        inv = _gf_inv8(a)
        sbox[a] = inv ^ _rotl8(inv, 1) ^ _rotl8(inv, 2) ^ _rotl8(inv, 3) ^ _rotl8(inv, 4) ^ 0x63
    return sbox


SBOX = _build_sbox()
RCON = [0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1B, 0x36, 0x6C, 0xD8, 0xAB, 0x4D]


def _key_expansion(key):
    """AES key expansion for 128/192/256-bit keys; returns a list of round words."""
    nk = len(key) // 4
    if nk not in (4, 6, 8):
        raise ValueError("key must be 16, 24 or 32 bytes")
    nr = nk + 6
    w = [int.from_bytes(key[4 * i:4 * i + 4], "big") for i in range(nk)]
    for i in range(nk, 4 * (nr + 1)):
        t = w[i - 1]
        if i % nk == 0:
            t = ((t << 8) | (t >> 24)) & 0xFFFFFFFF
            t = _sub_word(t) ^ (RCON[i // nk - 1] << 24)
        elif nk > 6 and i % nk == 4:
            t = _sub_word(t)
        w.append(w[i - nk] ^ t)
    return w


def _sub_word(word):
    return (
        (SBOX[(word >> 24) & 0xFF] << 24)
        | (SBOX[(word >> 16) & 0xFF] << 16)
        | (SBOX[(word >> 8) & 0xFF] << 8)
        | (SBOX[word & 0xFF])
    )


def _xtime(a):
    a <<= 1
    return (a ^ 0x1B) & 0xFF if a & 0x100 else a & 0xFF


def _encrypt_block(round_keys, block):
    """Encrypt one 16-byte block; block is a 16-byte bytearray/list."""
    nr = len(round_keys) // 4 - 1
    state = [block[i] for i in range(16)]

    def add_round_key(rnd):
        base = rnd * 4
        for c in range(4):
            word = round_keys[base + c]
            state[4 * c] ^= (word >> 24) & 0xFF
            state[4 * c + 1] ^= (word >> 16) & 0xFF
            state[4 * c + 2] ^= (word >> 8) & 0xFF
            state[4 * c + 3] ^= word & 0xFF

    def sub_bytes():
        for i in range(16):
            state[i] = SBOX[state[i]]

    def shift_rows():
        # row r shifted left by r positions (state is column-major)
        state[1], state[5], state[9], state[13] = state[5], state[9], state[13], state[1]
        state[2], state[6], state[10], state[14] = state[10], state[14], state[2], state[6]
        state[3], state[7], state[11], state[15] = state[15], state[3], state[7], state[11]

    def mix_columns():
        for c in range(4):
            a0, a1, a2, a3 = state[4 * c:4 * c + 4]
            state[4 * c] = _xtime(a0) ^ _xtime(a1) ^ a1 ^ a2 ^ a3
            state[4 * c + 1] = a0 ^ _xtime(a1) ^ _xtime(a2) ^ a2 ^ a3
            state[4 * c + 2] = a0 ^ a1 ^ _xtime(a2) ^ _xtime(a3) ^ a3
            state[4 * c + 3] = _xtime(a0) ^ a0 ^ a1 ^ a2 ^ _xtime(a3)

    add_round_key(0)
    for rnd in range(1, nr):
        sub_bytes()
        shift_rows()
        mix_columns()
        add_round_key(rnd)
    sub_bytes()
    shift_rows()
    add_round_key(nr)
    return bytes(state)


def _aes_block(key, block):
    return _encrypt_block(_key_expansion(key), block)


def _inc32(block):
    counter = (int.from_bytes(block[12:], "big") + 1) & 0xFFFFFFFF
    return block[:12] + counter.to_bytes(4, "big")


def _aes_ctr(key, initial_counter, data):
    round_keys = _key_expansion(key)
    counter = initial_counter
    out = bytearray()
    for i in range(0, len(data), 16):
        keystream = _encrypt_block(round_keys, counter)
        counter = _inc32(counter)
        chunk = data[i:i + 16]
        out += bytes(a ^ b for a, b in zip(chunk, keystream))
    return bytes(out)


# ---------------------------------------------------------------- GCM / GHASH


def _gf_mul(x, y):
    """Multiplication in GF(2^128) with GCM's bit ordering."""
    z = 0
    v = y
    for i in range(127, -1, -1):
        if (x >> i) & 1:
            z ^= v
        if v & 1:
            v = (v >> 1) ^ R
        else:
            v >>= 1
    return z


def _ghash(h_int, data):
    y = 0
    for i in range(0, len(data), 16):
        block = data[i:i + 16].ljust(16, b"\x00")
        y ^= int.from_bytes(block, "big")
        y = _gf_mul(y, h_int)
    return y


def _pad(data):
    return b"\x00" * ((16 - len(data) % 16) % 16)


def gcm_decrypt(key, nonce, ciphertext, tag, aad=b""):
    """AES-GCM open. Raises ValueError when the tag does not authenticate."""
    if len(nonce) != 12 or len(tag) != 16:
        raise ValueError("GCM requires a 12-byte nonce and 16-byte tag")
    h_int = int.from_bytes(_aes_block(key, b"\x00" * 16), "big")
    j0 = nonce + b"\x00\x00\x00\x01"
    plaintext = _aes_ctr(key, _inc32(j0), ciphertext)

    lengths = (len(aad) * 8).to_bytes(8, "big") + (len(ciphertext) * 8).to_bytes(8, "big")
    s = _ghash(h_int, aad + _pad(aad) + ciphertext + _pad(ciphertext) + lengths)
    expected = bytes(a ^ b for a, b in zip(_aes_block(key, j0), s.to_bytes(16, "big")))
    if not hmac.compare_digest(expected, tag):
        raise ValueError("authentication failed: wrong key or corrupted data")
    return plaintext


def gcm_open_blob(key, blob, aad=b""):
    """Open the app's sealed-blob layout: nonce(12) || ciphertext || tag(16)."""
    if len(blob) < 28:
        raise ValueError("sealed blob too short")
    return gcm_decrypt(key, blob[:12], blob[12:-16], blob[-16:], aad)


# ---------------------------------------------------------------- HKDF / base32


def hkdf_sha256(ikm, salt, info, length=32):
    if not salt:
        salt = b"\x00" * 32
    prk = hmac.new(salt, ikm, hashlib.sha256).digest()
    t = b""
    out = b""
    i = 1
    while len(out) < length:
        t = hmac.new(prk, t + info + bytes([i]), hashlib.sha256).digest()
        out += t
        i += 1
    return out[:length]


_B32_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"


def base32_decode(text):
    clean = text.replace("-", "").replace(" ", "").upper()
    buffer = 0
    bits = 0
    out = bytearray()
    for ch in clean:
        value = _B32_ALPHABET.find(ch)
        if value < 0:
            raise ValueError("invalid base32 character: %r" % ch)
        buffer = (buffer << 5) | value
        bits += 5
        if bits >= 8:
            out.append((buffer >> (bits - 8)) & 0xFF)
            bits -= 8
    return bytes(out)


# ---------------------------------------------------------------- envelope


def open_envelope(path, ukek, wrapped_dek_b64):
    data = open(path, "rb").read()
    if len(data) < 38 + 16:
        raise ValueError("%s is truncated" % os.path.basename(path))
    if data[:5] != MAGIC:
        raise ValueError("%s is not an AI Diary encrypted object" % os.path.basename(path))
    if data[5] != ENVELOPE_VERSION:
        raise ValueError("%s has unsupported envelope version %d" % (os.path.basename(path), data[5]))

    note_id = uuid.UUID(bytes=data[6:22])
    version = struct.unpack(">I", data[22:26])[0]
    nonce = data[26:38]
    ciphertext = data[38:-16]
    tag = data[-16:]
    aad = data[6:22] + data[22:26]  # note id + version, as in the API

    dek = gcm_open_blob(ukek, base64_decode_arg(wrapped_dek_b64))
    plaintext = gcm_decrypt(dek, nonce, ciphertext, tag, aad)
    return note_id, version, plaintext


def base64_decode_arg(value):
    import base64
    return base64.b64decode(value)


# ---------------------------------------------------------------- CLI


def main():
    parser = argparse.ArgumentParser(description="Decrypt an AI Diary encrypted export.")
    parser.add_argument("--key", required=False, default=os.environ.get("DIARY_RECOVERY_KEY"),
                        help="Your 26-character recovery key (or set DIARY_RECOVERY_KEY)")
    parser.add_argument("--dir", required=True, help="Directory containing recovery.dat, keys.json and .md.enc files")
    parser.add_argument("--out", required=False, help="Output directory (default: DIR/decrypted)")
    args = parser.parse_args()

    if not args.key:
        print("error: missing --key (your 26-character recovery key)", file=sys.stderr)
        return 2

    export_dir = args.dir
    out_dir = args.out or os.path.join(export_dir, "decrypted")
    os.makedirs(out_dir, exist_ok=True)

    recovery_path = os.path.join(export_dir, "recovery.dat")
    keys_path = os.path.join(export_dir, "keys.json")
    if not os.path.exists(recovery_path) or not os.path.exists(keys_path):
        print("error: %s must contain recovery.dat and keys.json" % export_dir, file=sys.stderr)
        return 2

    try:
        dat = json.load(open(recovery_path, encoding="utf-8"))
        keys = json.load(open(keys_path, encoding="utf-8"))
        seed = base32_decode(args.key)
        if len(seed) != 16:
            raise ValueError("the recovery key must decode to 16 bytes")
        kek = hkdf_sha256(seed, uuid.UUID(str(dat["user_id"])).bytes, b"ai-diary/recovery-kek/v1", 32)

        import base64
        try:
            ukek = gcm_open_blob(kek, base64.b64decode(dat["recovery_wrapped_ukek"]))
        except ValueError:
            print("error: the recovery key does not match recovery.dat for this account", file=sys.stderr)
            return 2

        count = 0
        for note in keys["notes"]:
            name = note["filename"]
            src = os.path.join(export_dir, name)
            if not os.path.exists(src):
                print("skipping %s: not found" % name, file=sys.stderr)
                continue
            try:
                _, _, plaintext = open_envelope(src, ukek, note["wrapped_dek"])
            except ValueError as ex:
                print("error: %s: %s" % (name, ex), file=sys.stderr)
                return 1
            out_name = name[:-4] if name.lower().endswith(".enc") else name + ".md"
            out_path = os.path.join(out_dir, out_name)
            os.makedirs(os.path.dirname(out_path) or out_dir, exist_ok=True)
            with open(out_path, "wb") as fh:
                fh.write(plaintext)
            print("wrote %s" % out_path)
            count += 1
        print("decrypted %d note(s) into %s" % (count, out_dir))
        return 0
    except (ValueError, KeyError, TypeError, json.JSONDecodeError) as ex:
        print("error: %s" % ex, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
