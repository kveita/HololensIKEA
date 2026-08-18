"""
make_assets.py
==============
Generates and repairs PNG assets required by Package.appxmanifest.

Usage:
    python scripts/make_assets.py

Behavior:
- Creates missing assets.
- Rewrites assets whose dimensions are wrong.
- Generates both scale-200 names and common unscaled aliases.
"""

import os
import struct
import zlib

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ASSETS_DIR = os.path.join(os.path.dirname(SCRIPT_DIR), "Assets")

# (filename, width, height, R, G, B)
ASSETS = [
    ("LockScreenLogo.scale-200.png",                        96,   96, 0x7F, 0x9F, 0xFF),
    ("SplashScreen.scale-200.png",                        1240,  600, 0x1A, 0x1A, 0x2E),
    ("Square150x150Logo.scale-200.png",                    300,  300, 0x7F, 0x9F, 0xFF),
    ("Square44x44Logo.scale-200.png",                       88,   88, 0x7F, 0x9F, 0xFF),
    ("Square44x44Logo.targetsize-24_altform-unplated.png",  24,   24, 0x7F, 0x9F, 0xFF),
    ("StoreLogo.png",                                        50,   50, 0x7F, 0x9F, 0xFF),
    ("Wide310x150Logo.scale-200.png",                       620,  300, 0x7F, 0x9F, 0xFF),

    # Common aliases used by older manifests/project files.
    ("SplashScreen.png",                                     620,  300, 0x1A, 0x1A, 0x2E),
    ("Square150x150Logo.png",                                150,  150, 0x7F, 0x9F, 0xFF),
    ("Square44x44Logo.png",                                   44,   44, 0x7F, 0x9F, 0xFF),
    ("Wide310x150Logo.png",                                  310,  150, 0x7F, 0x9F, 0xFF),
]


def _png_chunk(tag: bytes, data: bytes) -> bytes:
    payload = tag + data
    return (struct.pack(">I", len(data))
            + payload
            + struct.pack(">I", zlib.crc32(payload) & 0xFFFFFFFF))


def _read_png_size(path: str):
    """Return (w, h) for PNG, or None if invalid/unreadable."""
    try:
        with open(path, "rb") as fh:
            sig = fh.read(8)
            if sig != b"\x89PNG\r\n\x1a\n":
                return None
            length = struct.unpack(">I", fh.read(4))[0]
            chunk_type = fh.read(4)
            if chunk_type != b"IHDR" or length < 13:
                return None
            data = fh.read(length)
            w, h = struct.unpack(">II", data[:8])
            return (w, h)
    except Exception:
        return None


def make_png(path: str, w: int, h: int, r: int, g: int, b: int) -> None:
    """Write a minimal valid solid-colour RGB PNG."""
    sig = b"\x89PNG\r\n\x1a\n"
    ihdr = _png_chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    row = b"\x00" + bytes([r, g, b]) * w
    raw = row * h
    idat = _png_chunk(b"IDAT", zlib.compress(raw, level=1))
    iend = _png_chunk(b"IEND", b"")
    with open(path, "wb") as fh:
        fh.write(sig + ihdr + idat + iend)


def main():
    os.makedirs(ASSETS_DIR, exist_ok=True)
    print(f"Writing assets to: {ASSETS_DIR}\n")

    for filename, w, h, r, g, b in ASSETS:
        dest = os.path.join(ASSETS_DIR, filename)
        existing_size = _read_png_size(dest) if os.path.exists(dest) else None

        if existing_size == (w, h):
            print(f"  ok             {filename}  ({w}x{h})")
            continue

        make_png(dest, w, h, r, g, b)
        if existing_size is None:
            print(f"  created        {filename}  ({w}x{h})")
        else:
            print(f"  repaired       {filename}  {existing_size} -> ({w}x{h})")

    print("\nDone.")


if __name__ == "__main__":
    main()
