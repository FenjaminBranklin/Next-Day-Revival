"""Builds the gunner sight of the BTR-80A - apc_scope.png.

WHY A THIRD OVERLAY NEXT TO scope50.png AND t72_scope.png
---------------------------------------------------------
The .50 image is a hunting-style glass: round lens, thin cross, nothing else.
The T-72 image is an artillery sight: a wedge, a side scale and a stadiametric
rangefinder for a shell that arcs.

The BTR gun is neither. Since 0.5.1 it fires single flat rounds with a tracer
at roughly three per second, so its gunner needs a reticle for a fast gun on a
moving target: an open cross that does not cover the target, lead marks left
and right of it, and only a short drop ladder below - a flat round inside 900
units does not fall far enough for more.

The lens is also wider than the tank's (538 px against 522) and the vignette
opens up later. A vehicle gun is aimed with both eyes open; the tank's narrow
tunnel would be wrong here.

HOW THE IMAGE IS USED
---------------------
`Turret.Vollbild` draws it as a square of side max(width, height), centred -
hand-written ScaleAndCrop. On 16:9 the width fits exactly and roughly a fifth
is cut off at the top and at the bottom. The visible strip is 1080 of 1920
rows, so the lens must stay below 540 px radius or the crop clips it.

WHAT IS DELIBERATELY MISSING
----------------------------
Numbers on the scale, for the same reason as in t72_scope.py: a digit this
size needs either a system font, which looks different on another machine, or
hand-placed strokes. Long and short ticks alternate instead.
"""

import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HIER, "assets")
OUT = os.path.join(ASSETS, "apc_scope.png")

S = 1920                  # same as the game templates and the other overlays
SS = 2                    # supersampling for the line work
R_LENS = 538.0            # lens radius, just under half of 1080
FEATHER = 3.0             # soft edge against stair steps

FASSUNG = (9, 9, 11)      # everything outside the lens
RING = (24, 26, 28)       # narrow ring inside the mount
STRICH = (16, 18, 17)     # core of the marks
SAUM = (198, 214, 190)    # bright halo around them, against a dark background
SAUM_A = 105

# Vignette: fully clear up to VIG_FREI, then closing towards the rim. Both
# numbers are friendlier than the tank's (0.62 / 218) - this is the "more
# field of view" half of the change.
VIG_FREI = 0.72
VIG_MAX = 168.0

# Marks, all in pixels from the centre (image scale 1920).
KREUZ_LUECKE = 30.0       # free space around the aiming point
KREUZ_ARM = 128.0         # length of one arm of the cross
KREUZ_B = 5.0             # stroke width of the cross
PUNKT_R = 4.5             # the aiming point itself

LEAD_VON = 168.0          # lead marks start beyond the cross arms
LEAD_BIS = 452.0
LEAD_TEILUNG = 47.0
LEAD_KURZ = 12.0
LEAD_LANG = 24.0
LEAD_B = 4.0

# Drop ladder below the centre: a flat round needs three rungs, not five.
# The first rung sits BELOW the lower cross arm (which ends at 30 + 128 = 158);
# any closer and rung and arm cross into a plus sign.
FALL = ((196.0, 30.0), (272.0, 22.0), (348.0, 16.0))
FALL_B = 4.0

# Two short horizon marks at the left and right rim. They say which way is
# level while the turret swings - the cross alone cannot, it is always centred.
HORIZONT_VON = 476.0
HORIZONT_BIS = 512.0
HORIZONT_B = 4.0


def kreuz(d, cx, cy, farbe, breite):
    """Open cross: four arms with a gap, so the target stays visible."""
    a, b = KREUZ_LUECKE * SS, (KREUZ_LUECKE + KREUZ_ARM) * SS
    for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1)):
        d.line([(cx + dx * a, cy + dy * a), (cx + dx * b, cy + dy * b)],
               fill=farbe, width=int(round(breite)))


def punkt(d, cx, cy, farbe):
    """The aiming point. Small on purpose - it is the whole target at 900 m."""
    r = PUNKT_R * SS
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=farbe)


def leadmarken(d, cx, cy, farbe, breite):
    """Lead ticks on the horizontal line, hanging downwards."""
    for s in (-1, 1):
        d.line([(cx + s * LEAD_VON * SS, cy), (cx + s * LEAD_BIS * SS, cy)],
               fill=farbe, width=int(round(breite)))
        k = 1
        x = LEAD_VON
        while x <= LEAD_BIS:
            lang = LEAD_LANG if k % 2 == 0 else LEAD_KURZ
            d.line([(cx + s * x * SS, cy), (cx + s * x * SS, cy + lang * SS)],
                   fill=farbe, width=int(round(breite)))
            x += LEAD_TEILUNG
            k += 1


def fallstriche(d, cx, cy, farbe, breite):
    """Drop ladder: three horizontal rungs below the aiming point."""
    for (dy, halb) in FALL:
        d.line([(cx - halb * SS, cy + dy * SS), (cx + halb * SS, cy + dy * SS)],
               fill=farbe, width=int(round(breite)))


def horizont(d, cx, cy, farbe, breite):
    """Level marks at the rim, left and right."""
    for s in (-1, 1):
        d.line([(cx + s * HORIZONT_VON * SS, cy),
                (cx + s * HORIZONT_BIS * SS, cy)],
               fill=farbe, width=int(round(breite)))


def marken(farbe, breite_zu):
    """Draw every stroke once, in the supersampled image."""
    im = Image.new("RGBA", (S * SS, S * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    cx = cy = S * SS / 2.0

    kreuz(d, cx, cy, farbe, (KREUZ_B + breite_zu) * SS)
    leadmarken(d, cx, cy, farbe, (LEAD_B + breite_zu) * SS)
    fallstriche(d, cx, cy, farbe, (FALL_B + breite_zu) * SS)
    horizont(d, cx, cy, farbe, (HORIZONT_B + breite_zu) * SS)
    punkt(d, cx, cy, farbe)

    return np.asarray(im.resize((S, S), Image.LANCZOS)).astype(np.float32)


def build():
    c = S / 2.0
    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32)
    dx, dy = xx - c + 0.5, yy - c + 0.5
    r = np.sqrt(dx * dx + dy * dy)

    # Opaque outside, the vignette inside.
    aussen = np.clip((r - (R_LENS - FEATHER)) / (2.0 * FEATHER), 0.0, 1.0)
    t = np.clip((r / R_LENS - VIG_FREI) / (1.0 - VIG_FREI), 0.0, 1.0)
    alpha = np.maximum(aussen * 255.0, (t ** 2.2) * VIG_MAX)

    rgb = np.zeros((S, S, 3), np.float32)
    for i in range(3):
        rgb[..., i] = FASSUNG[i]

    ring = (r > R_LENS - 12.0) & (r < R_LENS - 3.0)
    for i in range(3):
        rgb[..., i][ring] = RING[i]

    # Marks: the bright halo first, the dark core on top. Without the halo a
    # black reticle disappears in front of a tree line.
    innen = (r < R_LENS - 6.0)[..., None]
    for (farbe, zu, deck) in ((SAUM, 3.2, SAUM_A / 255.0), (STRICH, 0.0, 1.0)):
        lage = marken((farbe[0], farbe[1], farbe[2], 255), zu)
        m = (lage[..., 3:4] / 255.0) * deck * innen
        rgb = rgb * (1.0 - m) + np.asarray(farbe, np.float32).reshape(1, 1, 3) * m
        alpha = np.maximum(alpha, (m[..., 0] * 255.0))

    out = np.dstack([np.clip(rgb, 0, 255), np.clip(alpha, 0, 255)])
    return Image.fromarray(out.astype(np.uint8), "RGBA")


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    img = build()
    img.save(OUT)
    a = np.asarray(img)[..., 3]
    print("APC gunner sight: %s  (%dx%d)" % (OUT, img.width, img.height))
    print("  opaque outside      %5.1f %% of the area" % (100.0 * (a > 247).mean()))
    print("  fully clear         %5.1f %% of the area" % (100.0 * (a < 8).mean()))
    print("  lens diameter       %d px of %d" % (int(2 * R_LENS), S))
    print("  visible strip on 16:9: %d px high, lens fits: %s"
          % (int(S * 9 / 16), "yes" if 2 * R_LENS <= S * 9 / 16 else "NO"))

    # Control view: the strip 16:9 really shows, over a meadow colour. A file
    # with an alpha channel says nothing without a background under it.
    hoch = int(S * 9 / 16)
    band = img.crop((0, (S - hoch) // 2, S, (S + hoch) // 2))
    grund = Image.new("RGBA", band.size, (110, 125, 95, 255))
    vor = os.path.join(ASSETS, "apc_scope_preview.png")
    Image.alpha_composite(grund, band).convert("RGB").resize(
        (960, 540), Image.LANCZOS).save(vor)
    print("  preview             %s" % vor)
