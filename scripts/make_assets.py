"""
make_assets.py
==============
Generates and repairs PNG assets required by Package.appxmanifest for the
HoloLens 1 UWP app, using dedicated master artwork images as sources.

Each asset family has its own master (single source of truth):

- ``scripts/icon_master.jpg`` — square app-icon artwork (300x300). Every
  square app icon (44px through 300px) and the wide/splash targets are
  derived from it with LANCZOS resampling:
  - Square targets: direct resize of the master.
  - Wide targets (Wide310x150 / SplashScreen): center crop of the master to
    the target aspect ratio (2.07:1) so the subject stays framed, then resize.
- ``scripts/box_art_master_1080.jpg`` — square Microsoft Store box art
  (1080x1080); the 2160x2160 box is upscaled from ``box_art_master_1280.jpg``.
- ``scripts/poster_master_720.jpg`` / ``poster_master_853.jpg`` — portrait
  9:16 poster artwork (720x1080 / 853x1280); the 1440x2160 poster is a
  center-crop-to-9:16 then upscale of the 853 master.
- ``scripts/icon_master_150.jpg`` / ``icon_master_100.jpg`` — smaller icon
  artwork variants for the 150px / 71px Store tiles.

Usage:
    python scripts/make_assets.py

Behavior:
- Rewrites assets whose dimensions are wrong.
- Generates both scale-200 names and common unscaled aliases.
- Also regenerates the Microsoft Store listing images (StoreListing/) from
  the same masters.
"""

import os

from PIL import Image

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_DIR = os.path.dirname(SCRIPT_DIR)
ASSETS_DIR = os.path.join(REPO_DIR, "Assets")
STORE_DIR = os.path.join(REPO_DIR, "StoreListing")

MASTER_PATH = os.path.join(SCRIPT_DIR, "icon_master.jpg")
BOX_MASTER_1080 = os.path.join(SCRIPT_DIR, "box_art_master_1080.jpg")
BOX_MASTER_1280 = os.path.join(SCRIPT_DIR, "box_art_master_1280.jpg")
POSTER_MASTER_720 = os.path.join(SCRIPT_DIR, "poster_master_720.jpg")
POSTER_MASTER_853 = os.path.join(SCRIPT_DIR, "poster_master_853.jpg")
ICON_MASTER_150 = os.path.join(SCRIPT_DIR, "icon_master_150.jpg")
ICON_MASTER_100 = os.path.join(SCRIPT_DIR, "icon_master_100.jpg")

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def downsample(img, size):
    """LANCZOS down/up-scale to target size."""
    return img.resize(size, Image.LANCZOS)


def crop_wide(master, target_w, target_h):
    """Center-crop the square master to a wide aspect ratio (w:h)."""
    mw, mh = master.size
    target_ar = target_w / target_h
    crop_h = int(round(mw / target_ar))
    crop_h = min(crop_h, mh)
    # Bias the crop window toward the subject band (vertical center).
    top = max(0, (mh - crop_h) // 2)
    # Shift slightly up if the crop would cut the top of a tall subject.
    top = max(0, top - int(mh * 0.02))
    box = (0, top, mw, top + crop_h)
    return master.crop(box)


# ---------------------------------------------------------------------------
# Output targets
# ---------------------------------------------------------------------------

SQUARE_TARGETS = [
    ("Square150x150Logo.png", 150, 150),
    ("Square150x150Logo.scale-200.png", 300, 300),
    ("Square44x44Logo.png", 44, 44),
    ("Square44x44Logo.scale-200.png", 88, 88),
    ("Square44x44Logo.targetsize-24_altform-unplated.png", 24, 24),
    ("StoreLogo.png", 50, 50),
    ("LockScreenLogo.scale-200.png", 48, 48),
]

WIDE_TARGETS = [
    ("Wide310x150Logo.png", 310, 150),
    ("Wide310x150Logo.scale-200.png", 620, 300),
    ("SplashScreen.png", 620, 300),
    ("SplashScreen.scale-200.png", 1240, 600),
]

# Microsoft Store listing images — square boxes from the box-art masters.
BOX_TARGETS = [
    ("1x1_Box_1080x1080.png", 1080, 1080),
    ("1x1_Box_2160x2160.png", 2160, 2160),
]

# Microsoft Store listing images — 9:16 posters from the poster masters.
POSTER_TARGETS = [
    ("9x16_Poster_720x1080.png", 720, 1080),
    ("9x16_Poster_1440x2160.png", 1440, 2160),
]

# Microsoft Store listing images — small square tiles from the icon masters.
TILE_TARGETS = [
    ("1x1_Tile_71x71.png", 71, 71),
    ("1x1_Tile_150x150.png", 150, 150),
    ("1x1_Tile_300x300.png", 300, 300),
]


def main():
    required = {
        "MASTER_PATH": MASTER_PATH,
        "BOX_MASTER_1080": BOX_MASTER_1080,
        "BOX_MASTER_1280": BOX_MASTER_1280,
        "POSTER_MASTER_720": POSTER_MASTER_720,
        "POSTER_MASTER_853": POSTER_MASTER_853,
    }
    for name, path in required.items():
        if not os.path.exists(path):
            raise FileNotFoundError(f"Master artwork not found: {path} ({name})")

    master = Image.open(MASTER_PATH).convert("RGB")
    box_1080 = Image.open(BOX_MASTER_1080).convert("RGB")
    box_1280 = Image.open(BOX_MASTER_1280).convert("RGB")
    poster_720 = Image.open(POSTER_MASTER_720).convert("RGB")
    poster_853 = Image.open(POSTER_MASTER_853).convert("RGB")
    icon_150 = Image.open(ICON_MASTER_150).convert("RGB") if os.path.exists(ICON_MASTER_150) else master
    icon_100 = Image.open(ICON_MASTER_100).convert("RGB") if os.path.exists(ICON_MASTER_100) else master

    print(f"App-icon master:      {MASTER_PATH} ({master.size[0]}x{master.size[1]})")
    print(f"Box art master 1080:  {BOX_MASTER_1080} ({box_1080.size[0]}x{box_1080.size[1]})")
    print(f"Box art master 1280:  {BOX_MASTER_1280} ({box_1280.size[0]}x{box_1280.size[1]})")
    print(f"Poster master 720:    {POSTER_MASTER_720} ({poster_720.size[0]}x{poster_720.size[1]})")
    print(f"Poster master 853:    {POSTER_MASTER_853} ({poster_853.size[0]}x{poster_853.size[1]})")
    print(f"Writing assets to: {ASSETS_DIR} and {STORE_DIR}\n")

    os.makedirs(ASSETS_DIR, exist_ok=True)
    os.makedirs(STORE_DIR, exist_ok=True)

    # Square app icons: direct resize of the icon master.
    for filename, tw, th in SQUARE_TARGETS:
        out = downsample(master, (tw, th))
        out.save(os.path.join(ASSETS_DIR, filename))
        print(f"  Assets/{filename}  ({tw}x{th})")

    # Wide app assets: aspect-crop the icon master, then resize.
    for filename, tw, th in WIDE_TARGETS:
        cropped = crop_wide(master, tw, th)
        out = downsample(cropped, (tw, th))
        out.save(os.path.join(ASSETS_DIR, filename))
        print(f"  Assets/{filename}  ({tw}x{th}, crop {cropped.size[0]}x{cropped.size[1]})")

    # Store listing — box art from the box masters.
    for filename, tw, th in BOX_TARGETS:
        src = box_1080 if tw <= 1080 else box_1280
        out = downsample(src, (tw, th))
        out.save(os.path.join(STORE_DIR, filename))
        print(f"  StoreListing/{filename}  ({tw}x{th}, from {src.size[0]}x{src.size[1]})")

    # Store listing — portrait 9:16 posters (center-crop to 9:16, then resize).
    for filename, tw, th in POSTER_TARGETS:
        src = poster_720 if tw <= 720 else poster_853
        sw, sh = src.size
        target_ar = tw / th  # 0.667
        crop_w = int(round(sh * target_ar))
        crop_w = min(crop_w, sw)
        left = (sw - crop_w) // 2
        box = (left, 0, left + crop_w, sh)
        cropped = src.crop(box)
        out = downsample(cropped, (tw, th))
        out.save(os.path.join(STORE_DIR, filename))
        print(f"  StoreListing/{filename}  ({tw}x{th}, crop {cropped.size[0]}x{cropped.size[1]})")

    # Store listing — small square tiles from the matching icon masters.
    for filename, tw, th in TILE_TARGETS:
        if tw == 150:
            src = icon_150
        elif tw <= 100:
            src = icon_100
        else:
            src = master
        out = downsample(src, (tw, th))
        out.save(os.path.join(STORE_DIR, filename))
        print(f"  StoreListing/{filename}  ({tw}x{th}, from {src.size[0]}x{src.size[1]})")

    print("\nDone.")


if __name__ == "__main__":
    main()
