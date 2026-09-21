"""Builds the Stinger gunner's sight - stinger_scope.png.

WHY THE STINGER GETS A SIGHT AT ALL
-----------------------------------
Until now the Stinger was aimed over the toolkit's own four-pixel cross drawn
in OnGUI, with white squares around every target. That reads as a HUD, not as
a weapon: the launcher is shouldered and the gunner still sees the whole open
screen. A MANPADS is aimed through the sight assembly clamped to the tube, and
that is what this image is.

The route is the game's own, not a second overlay: `xmlItemsDataManager::
DeserealizeWeaponsDB` loads the `Scope` attribute of weapons_db.xml through
`Resources.Load(string)` and casts it to Texture2D, and `ScopeCameraEffect::
OnGUI` draws whatever came back over the whole screen while
`CameraAimingSystem::ScopeAimingMode` is on (REVERSE_ENGINEERING.md 8). The
TAC-50 already goes this way with scope50.png. The Stinger's own path is
`WeaponElements/Scopes/NDR_ScopeStinger`, served by ResourceHook.

HOW THE IMAGE IS USED
---------------------
    ScopeCameraEffect::Update   scopePosition = Rect(0, 0, width, height)
    ScopeCameraEffect::OnGUI    GUI.DrawTexture(scopePosition, tex, ScaleMode 1)

ScaleMode 1 is ScaleAndCrop: a square image on 16:9 fits by WIDTH and loses
roughly a fifth at the top and at the bottom. The visible strip of a 1920
square is 1080 rows, so the lens has to stay under 540 px radius or the crop
eats it - the same constraint scope50.py and apc_scope.py are built to.

WHAT THIS RETICLE IS AND IS NOT
-------------------------------
Not a sniper glass. The FIM-92's sight is a wide, nearly unmagnified window
with an etched reticle; `ScopeFOV` in mods/revival.json is 30 against the
TAC-50's 8, so the picture stays a shoulder-fired one. The marks answer the
three questions a MANPADS gunner has and nothing else:

  1. IS THE TARGET IN THE SEEKER'S GATE? The cage in the middle. The lock in
     Revival.Stinger.cs is exactly this: hold the aiming point inside a target
     square until the tone turns solid. The cage is drawn a touch wider than
     `Stinger.HalfBox` so a square that qualifies sits INSIDE it rather than
     on it.
  2. HOW FAR DO I LEAD? The horizontal scale. A crossing helicopter is the
     normal shot and the missile is not instant.
  3. HOW HIGH AM I POINTING? The elevation rungs, above the centre only - a
     Stinger is not pointed at the ground.

Numbers are in pixels from the centre at image scale 1920, like the other two
overlays. Digits are deliberately absent for the same reason as in t72_scope.py
and apc_scope.py: a glyph this size needs either a system font, which differs
from machine to machine, or hand-placed strokes.

Run: python stinger_scope.py
"""

import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HIER, "assets")
OUT = os.path.join(ASSETS, "stinger_scope.png")

S = 1920                  # same as the game templates and the other overlays
SS = 2                    # supersampling for the line work
R_LENS = 524.0            # lens radius, under half of the 1080 visible strip
FEATHER = 3.0             # soft edge against stair steps

FASSUNG = (10, 11, 10)    # the sight housing, everything outside the lens
RING = (26, 29, 26)       # narrow ring inside the housing
STRICH = (15, 17, 16)     # core of the marks
SAUM = (196, 212, 196)    # bright halo around them, against a dark background
SAUM_A = 110

# Vignette. Wider open than the tank (0.62) and a shade wider than the BTR
# (0.72): a man tracking a helicopter needs the corners of his picture.
VIG_FREI = 0.76
VIG_MAX = 150.0

# The seeker gate. Stinger.HalfBox is clamp(Screen.height * 0.035, 18, 42), so
# on the 1080 strip a target square is 38 px to a side of centre at most. The
# cage sits at 74 so a square that qualifies is visibly INSIDE it.
GATE_HALB = 74.0
GATE_ARM = 30.0           # length of one corner leg
GATE_B = 5.0

# The aiming point itself: an open cross with a gap, so the target stays
# visible, and a small dot at the centre.
KREUZ_LUECKE = 20.0
KREUZ_ARM = 34.0
KREUZ_B = 4.0
PUNKT_R = 4.0

# Lead scale on the horizontal, outside the cage.
LEAD_VON = 150.0
LEAD_BIS = 430.0
LEAD_TEILUNG = 46.0
LEAD_KURZ = 13.0
LEAD_LANG = 26.0
LEAD_B = 4.0

# Elevation rungs ABOVE the aiming point only. The first clears the cage.
STEIG = ((150.0, 30.0), (232.0, 22.0), (314.0, 15.0))
STEIG_B = 4.0

# The seeker's own field, as one thin circle. It is what the missile can still
# turn onto after launch, not a rangefinder.
FELD_R = 306.0
FELD_B = 3.0
FELD_LUECKE = 0.20        # radians cut out of the circle at each cardinal

# Level marks at the rim, left and right, like the BTR sight: while the gunner
# swings up onto a helicopter the centred cross cannot say which way is level.
HORIZONT_VON = 452.0
HORIZONT_BIS = 496.0
HORIZONT_B = 4.0


def gate(d, cx, cy, farbe, breite):
    """Four corner brackets: the seeker gate the target has to sit in."""
    h, a = GATE_HALB * SS, GATE_ARM * SS
    for sx in (-1, 1):
        for sy in (-1, 1):
            x, y = cx + sx * h, cy + sy * h
            d.line([(x, y), (x - sx * a, y)], fill=farbe, width=int(round(breite)))
            d.line([(x, y), (x, y - sy * a)], fill=farbe, width=int(round(breite)))


def kreuz(d, cx, cy, farbe, breite):
    """Open cross around the aiming point."""
    a, b = KREUZ_LUECKE * SS, (KREUZ_LUECKE + KREUZ_ARM) * SS
    for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1)):
        d.line([(cx + dx * a, cy + dy * a), (cx + dx * b, cy + dy * b)],
               fill=farbe, width=int(round(breite)))


def punkt(d, cx, cy, farbe):
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


def steigstriche(d, cx, cy, farbe, breite):
    """Elevation rungs above the aiming point."""
    for (dy, halb) in STEIG:
        d.line([(cx - halb * SS, cy - dy * SS), (cx + halb * SS, cy - dy * SS)],
               fill=farbe, width=int(round(breite)))


def feldkreis(d, cx, cy, farbe, breite):
    """The seeker field as four arcs, broken at the cardinals so the lead
    scale and the rungs are not cut by a closed circle."""
    r = FELD_R * SS
    box = [cx - r, cy - r, cx + r, cy + r]
    luecke = np.degrees(FELD_LUECKE)
    for start in (0.0, 90.0, 180.0, 270.0):
        d.arc(box, start + luecke, start + 90.0 - luecke,
              fill=farbe, width=int(round(breite)))


def horizont(d, cx, cy, farbe, breite):
    for s in (-1, 1):
        d.line([(cx + s * HORIZONT_VON * SS, cy),
                (cx + s * HORIZONT_BIS * SS, cy)],
               fill=farbe, width=int(round(breite)))


def marken(farbe, breite_zu):
    """Draw every stroke once, in the supersampled image."""
    im = Image.new("RGBA", (S * SS, S * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    cx = cy = S * SS / 2.0

    gate(d, cx, cy, farbe, (GATE_B + breite_zu) * SS)
    kreuz(d, cx, cy, farbe, (KREUZ_B + breite_zu) * SS)
    leadmarken(d, cx, cy, farbe, (LEAD_B + breite_zu) * SS)
    steigstriche(d, cx, cy, farbe, (STEIG_B + breite_zu) * SS)
    feldkreis(d, cx, cy, farbe, (FELD_B + breite_zu) * SS)
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

    ring = (r > R_LENS - 13.0) & (r < R_LENS - 3.0)
    for i in range(3):
        rgb[..., i][ring] = RING[i]

    # Marks: the bright halo first, the dark core on top. Without the halo a
    # black reticle disappears against a bright sky, which is where a Stinger
    # spends its whole sight picture.
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
    print("Stinger sight: %s  (%dx%d)" % (OUT, img.width, img.height))
    print("  opaque outside      %5.1f %% of the area" % (100.0 * (a > 247).mean()))
    print("  fully clear         %5.1f %% of the area" % (100.0 * (a < 8).mean()))
    print("  lens diameter       %d px of %d" % (int(2 * R_LENS), S))
    print("  seeker gate         %d px across" % int(2 * GATE_HALB))
    print("  visible strip on 16:9: %d px high, lens fits: %s"
          % (int(S * 9 / 16), "yes" if 2 * R_LENS <= S * 9 / 16 else "NO"))

    # Control view: the strip 16:9 really shows, over a sky colour. A file with
    # an alpha channel says nothing without a background under it, and the sky
    # is the background this sight is used against.
    hoch = int(S * 9 / 16)
    band = img.crop((0, (S - hoch) // 2, S, (S + hoch) // 2))
    grund = Image.new("RGBA", band.size, (128, 150, 176, 255))
    vor = os.path.join(ASSETS, "stinger_scope_preview.png")
    Image.alpha_composite(grund, band).convert("RGB").resize(
        (960, 540), Image.LANCZOS).save(vor)
    print("  preview             %s" % vor)
