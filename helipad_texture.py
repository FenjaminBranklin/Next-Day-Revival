"""Deck textures for the helicopter landing pads (Revival.Helipads.cs).

    python helipad_texture.py

Writes six files into assets/ - albedo, normal and metallic/gloss map for each
of the two built surfaces:

    helipad_concrete.png  helipad_concrete_normal.png  helipad_concrete_metal.png
    helipad_steel.png     helipad_steel_normal.png     helipad_steel_metal.png

plus a *_preview.png per surface, which is working material and ships nowhere.

WHY THE WHOLE PAD IS ONE TEXTURE.
Until 6.39 the deck was three flat colours on three meshes: grey surface, black
disc, white H. Flat colour under the Standard shader is what reads in game as
plastic, and a black disc with a white H beside a brown hillside reads as a
poster. So the pad is painted instead: one image per surface holds the material,
the dirt, the scorch under the rotor, the worn paint AND the apron that meets
the ground, and the deck mesh maps it 0..1 over its own footprint.

The mapping is RADIUS-RELATIVE, not metric: the authored pad rim always lands on
texture radius DECK (0.80) and the apron fills 0.80..1.0, whatever the pad's
radius in metres is. An 8 m pad and a 60 m pad therefore carry the same painting
at the same relative size, and one texture serves every pad of its kind. What
that costs is resolution per metre, which is why nothing in here is drawn as a
hard edge: this is a surface somebody stopped maintaining years ago.

HOW THE DIRT IS BUILT, because the first pass got it wrong: every variation of
VALUE comes from one monochrome noise field applied to all three channels at
once, and COLOUR only ever arrives through a named mask - rust, moss, oil,
earth. Independent noise per channel is what turns a grey surface into coloured
confetti, which at a distance reads as a broken texture rather than as dirt.

ASCII only, English comments - AGENTS.md.
"""

import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

import texlib

ROOT = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(ROOT, "assets")

S = 1024                 # texture edge, same size as the weapon atlases
HALF = S // 2
DECK = 0.80              # texture radius of the authored pad rim
FIELD = 0.44             # the dark field the H stands on (0.55 of the deck)
BAND_IN, BAND_OUT = 0.704, 0.760     # the rim band (0.88..0.95 of the deck)

# The H, in deck-relative units taken straight from the old PaintMesh, so the
# marking keeps the size and the position it had.
H_BAR = 0.11             # bar width
H_LEG = 0.62             # leg length
H_OFF = 0.24             # leg centre offset from the middle
H_CROSS = 0.48           # crossbar width

# The only colours in the file. Everything else is one of these, darkened or
# lightened by the grey noise.
RUST_LIGHT = (0.315, 0.170, 0.080)
RUST_DEEP = (0.205, 0.100, 0.048)
MOSS = (0.170, 0.185, 0.120)
DIRT = (0.250, 0.212, 0.155)
OIL = (0.052, 0.046, 0.042)
SOOT = (0.105, 0.092, 0.080)
BONE = (0.635, 0.615, 0.545)        # the H: paint that was white a long time ago
OCHRE = (0.395, 0.310, 0.140)       # the rim band
FIELD_RGB = (0.145, 0.132, 0.118)   # the field: dark brown-grey, never black
GRAVEL = (0.270, 0.240, 0.190)
EARTH = (0.205, 0.178, 0.132)


# ----------------------------------------------------------------- helpers

def polar():
    """r (0 at the centre, 1 at the texture edge) and the angle, per pixel."""
    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32)
    x = (xx + 0.5 - HALF) / HALF
    y = (yy + 0.5 - HALF) / HALF
    return x, y, np.sqrt(x * x + y * y), np.arctan2(y, x)


def grey(r, *octaves):
    """One monochrome noise field; octaves are (scale, amplitude) pairs."""
    out = np.zeros((S, S), np.float32)
    for scale, amp in octaves:
        out += texlib.grain(r, S, S, scale, amp)
    return out


def unit(r, *octaves):
    """The same field mapped to roughly 0..1, for use as a mask."""
    return np.clip(grey(r, *octaves) * 0.5 + 0.5, 0.0, 1.0)


def smoothstep(edge0, edge1, x):
    t = np.clip((x - edge0) / (edge1 - edge0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def band(value, low, high, feather):
    """1 inside [low, high], falling off over `feather` on both sides."""
    return (smoothstep(low - feather, low, value)
            * (1.0 - smoothstep(high, high + feather, value)))


def shade(rgb, amount):
    """Lighten (>0) or darken (<0) without touching the hue."""
    return rgb * (1.0 + amount[..., None])


def tint(rgb, mask, colour, amount=1.0):
    """Lay `colour` over rgb where mask says so. mask is 0..1, per pixel."""
    k = (np.clip(mask, 0.0, 1.0) * amount)[..., None]
    return rgb * (1.0 - k) + np.asarray(colour, np.float32) * k


def flat(colour):
    return np.repeat(np.asarray(colour, np.float32)[None, None, :],
                     S, 0).repeat(S, 1).copy()


def mask_from_draw(painter, blur=0.0):
    """Run a PIL drawing function on an L image and return it as 0..1."""
    img = Image.new("L", (S, S), 0)
    painter(ImageDraw.Draw(img))
    if blur > 0:
        img = img.filter(ImageFilter.GaussianBlur(blur))
    return np.asarray(img, np.float32) / 255.0


def rect_mask(cx, cz, w, d, feather=0.003):
    """An axis-aligned rectangle in texture units, with a soft edge.

    Deck-relative like the old PaintMesh: cx/cz and the sizes are fractions of
    the PAD radius, so they are multiplied by DECK here and nowhere else.
    """
    x, y, _, _ = POLAR
    dx = np.abs(x - cx * DECK) - w * DECK * 0.5
    dy = np.abs(y - cz * DECK) - d * DECK * 0.5
    outside = np.maximum(np.maximum(dx, dy), 0.0)
    return 1.0 - smoothstep(0.0, feather, outside)


# ------------------------------------------------------------ the surfaces

def concrete(r):
    """Old poured concrete: bays, joints, cracks, patches of exposed stone."""
    _, _, rad, ang = POLAR
    rgb = flat((0.440, 0.425, 0.390))

    # Two scales of discolouration. Concrete that has stood outdoors is never
    # one grey: it is patches of grey, each a different age.
    slow = grey(r, (90, 0.045), (30, 0.028))
    rgb = shade(rgb, slow)
    rgb = shade(rgb, grey(r, (6, 0.014), (2, 0.010)))

    # Aggregate: the stones under the surface, where the top layer is gone.
    exposed = smoothstep(0.62, 0.90, unit(r, (4, 1.0))) * smoothstep(-0.02, 0.03, slow)
    rgb = shade(rgb, exposed * 0.12)
    rgb = tint(rgb, exposed * 0.25, (0.470, 0.450, 0.415))

    # Bays. Concrete is poured in panels and every panel edge is a joint that
    # collects dirt; a round pad still gets a square grid, because the formwork
    # does not care about the shape.
    pitch = S / 5.6

    def joints(d):
        for k in range(-4, 5):
            p = HALF + k * pitch
            d.line([(p, 0), (p, S)], fill=255, width=3)
            d.line([(0, p), (S, p)], fill=255, width=3)

    joint = mask_from_draw(joints, blur=1.6)
    joint *= 0.45 + 0.55 * smoothstep(0.35, 0.75, unit(r, (26, 1.0)))
    joint *= smoothstep(DECK + 0.01, DECK - 0.04, rad)     # no bays on the apron
    rgb = tint(rgb, joint * 0.55, (0.215, 0.200, 0.180))

    # Cracks: a few random walks, thin and dark.
    crack = crack_mask(r, 8, 260) * smoothstep(DECK + 0.01, DECK - 0.04, rad)
    rgb = tint(rgb, crack * 0.6, (0.205, 0.195, 0.180))

    height = exposed * 0.25 + grey(r, (4, 0.35), (9, 0.20)) - joint * 1.7 - crack * 1.1
    metal = np.zeros((S, S), np.float32)
    smooth = 0.085 + grey(r, (20, 0.02)) - exposed * 0.02
    return rgb, height, metal, smooth


def steel(r):
    """A welded deck: sector plates, rivet rows, and rust wherever water sits."""
    _, _, rad, ang = POLAR
    rgb = flat((0.238, 0.224, 0.205))

    # Rolled sheet: fine grain, plus slow warping over whole plates.
    rgb = shade(rgb, grey(r, (55, 0.075), (14, 0.045), (3, 0.020)))

    # The plates: eight sectors over three rings, the seams welded.
    sectors = 8
    seam = band(np.abs(((ang + np.pi / sectors) % (2 * np.pi / sectors))
                       - np.pi / sectors), 0.0, 0.009, 0.005)
    seam = np.maximum(seam, band(rad, 0.400, 0.404, 0.006))
    seam = np.maximum(seam, band(rad, 0.660, 0.664, 0.006))
    seam *= smoothstep(DECK + 0.01, DECK - 0.03, rad)      # no seams on the apron
    rgb = tint(rgb, seam * 0.75, (0.150, 0.143, 0.135))
    weld = seam * smoothstep(0.40, 0.80, unit(r, (14, 1.0)))
    rgb = tint(rgb, weld * 0.45, (0.330, 0.315, 0.295))

    # Rivets along every seam, a lighter head with its own little shadow.
    rivet = rivet_rows(r, sectors)
    rgb = tint(rgb, rivet * 0.55, (0.360, 0.345, 0.325))

    # Rust. It starts at the seams, the rivets and the rim - everywhere water
    # stands or the coating is broken - and then it spreads in blooms.
    wet = smoothstep(0.40, 0.95, rad) * 0.45 + seam * 0.30 + rivet * 0.25
    bloom = smoothstep(0.34, 0.74, unit(r, (60, 0.8), (18, 0.7), (6, 0.45), (2, 0.2))
                       + wet * 0.5)
    bloom *= smoothstep(DECK + 0.02, DECK - 0.04, rad)
    deep = smoothstep(0.55, 0.95, bloom) * smoothstep(0.45, 0.80, unit(r, (9, 1.0)))
    rgb = tint(rgb, bloom * 0.80, RUST_LIGHT)
    rgb = tint(rgb, deep * 0.75, RUST_DEEP)

    # Scuffs where skids have been dragged across the plates.
    scuff = scuff_mask(r, 26)
    rgb = tint(rgb, scuff * (1.0 - bloom) * 0.30, (0.420, 0.408, 0.390))

    height = (rivet * 1.3 - seam * 1.8 + weld * 0.7
              + grey(r, (30, 0.25), (8, 0.18)) + deep * 0.45)
    metal = np.clip(0.60 - bloom * 0.50, 0.0, 1.0)
    smooth = np.clip(0.20 - bloom * 0.15 + scuff * 0.04 + grey(r, (20, 0.02)), 0.0, 1.0)
    return rgb, height, metal, smooth


def crack_mask(r, count, length):
    """`count` random walks across the deck, drawn thin."""
    lines = []
    for _ in range(count):
        a = r.uniform(0, 2 * np.pi)
        rr = r.uniform(0.0, DECK * 0.9) * HALF
        x, y = HALF + np.cos(a) * rr, HALF + np.sin(a) * rr
        heading = r.uniform(0, 2 * np.pi)
        path = [(x, y)]
        for _ in range(14):
            heading += r.normal(0.0, 0.55)
            x += np.cos(heading) * length / 14.0
            y += np.sin(heading) * length / 14.0
            path.append((x, y))
        lines.append(path)

    def painter(d):
        for path in lines:
            d.line(path, fill=255, width=2, joint="curve")

    return mask_from_draw(painter, blur=0.7)


def scuff_mask(r, count):
    marks = []
    for _ in range(count):
        a = r.uniform(0, 2 * np.pi)
        rr = r.uniform(0.0, DECK * 0.85) * HALF
        x, y = HALF + np.cos(a) * rr, HALF + np.sin(a) * rr
        heading = r.uniform(0, 2 * np.pi)
        span = r.uniform(30, 150)
        marks.append(((x, y), (x + np.cos(heading) * span, y + np.sin(heading) * span),
                      int(r.integers(1, 4))))

    def painter(d):
        for a, b, w in marks:
            d.line([a, b], fill=190, width=w)

    return mask_from_draw(painter, blur=1.1)


def rivet_rows(r, sectors):
    """Rivet heads down every radial seam and around the two ring seams."""
    heads = []
    for k in range(sectors):
        a = (k + 0.5) * 2 * np.pi / sectors
        rr = 0.10
        while rr < DECK - 0.03:
            heads.append((HALF + np.cos(a) * rr * HALF, HALF + np.sin(a) * rr * HALF))
            rr += 0.035
    for ring in (0.402, 0.662):
        n = int(2 * np.pi * ring * HALF / 18)
        for k in range(n):
            a = k * 2 * np.pi / n
            heads.append((HALF + np.cos(a) * ring * HALF,
                          HALF + np.sin(a) * ring * HALF))

    def painter(d):
        for x, y in heads:
            d.ellipse([x - 3.4, y - 3.4, x + 3.4, y + 3.4], fill=255)

    return mask_from_draw(painter, blur=0.9)


# -------------------------------------------------------------- the paint

def markings(r, rgb, height, metal, smooth):
    """The dark field, the H and the rim band - faded, chipped, still legible.

    Deliberately not white on black. Paint on an outdoor deck fades to bone, the
    field under it weathers to a brown-black, and both are eaten away in
    patches. What is left still has to read from the air, which is why the field
    stays and why the wear takes about a fifth of it and not half: an H nobody
    can find is not weathering, it is a missing marking.
    """
    _, _, rad, ang = POLAR

    # Where paint survives. Whole patches are gone; a fine pitting breaks up
    # every edge so nothing looks printed.
    patch = smoothstep(0.30, 0.62, unit(r, (45, 1.0), (13, 0.55)))
    pit = smoothstep(0.22, 0.60, unit(r, (3, 1.0)))
    keep = np.clip(0.55 + 0.45 * patch, 0.0, 1.0) * np.clip(0.60 + 0.40 * pit, 0.0, 1.0)
    keep = np.clip(keep * 1.25, 0.0, 1.0)

    # The field: old paint, worn round at the edges and thin in the middle where
    # everything lands.
    edge = FIELD * (1.0 + 0.035 * np.sin(ang * 3.0 + 0.7)
                    + 0.025 * np.sin(ang * 7.0 - 1.3))
    field = ((1.0 - smoothstep(edge - 0.025, edge + 0.008, rad))
             * np.clip(0.72 + 0.28 * keep, 0.0, 1.0))
    field_rgb = shade(flat(FIELD_RGB), grey(r, (12, 0.22), (4, 0.13)))
    rgb = rgb * (1.0 - (field * 0.85)[..., None]) + field_rgb * (field * 0.85)[..., None]

    # The H and the band. Bone white, not white; the band a faded ochre, so the
    # pad does not read as two colours straight out of a printer.
    h = np.maximum(np.maximum(rect_mask(-H_OFF, 0.0, H_BAR, H_LEG),
                              rect_mask(H_OFF, 0.0, H_BAR, H_LEG)),
                   rect_mask(0.0, 0.0, H_CROSS, H_BAR))
    h *= np.clip(0.74 + 0.26 * keep, 0.0, 1.0)
    rim = band(rad, BAND_IN, BAND_OUT, 0.005) * np.clip(keep - 0.18, 0.0, 1.0)

    bone = shade(flat(BONE), grey(r, (10, 0.10), (3, 0.05)))
    ochre = shade(flat(OCHRE), grey(r, (10, 0.10), (3, 0.05)))
    rgb = rgb * (1.0 - h[..., None]) + bone * h[..., None]
    rgb = rgb * (1.0 - rim[..., None]) + ochre * rim[..., None]

    painted = np.clip(field * 0.7 + h + rim, 0.0, 1.0)
    height = height + painted * 0.12
    metal = metal * (1.0 - painted)
    smooth = smooth * (1.0 - painted) + painted * 0.10
    return rgb, height, metal, smooth


# ------------------------------------------------------- dirt and the apron

def weather(r, rgb, metal, smooth):
    """Everything the pad has stood through - laid over the paint, not under."""
    _, _, rad, ang = POLAR

    # Soot under the machine and the oil it drops. Soft and uneven: a burn mark
    # is not a disc.
    core = np.clip(np.exp(-((rad / 0.50) ** 2))
                   * (0.55 + 0.70 * unit(r, (40, 1.0), (12, 0.6))), 0.0, 1.0)
    rgb = shade(rgb, -core * 0.13)
    rgb = tint(rgb, core * 0.10, SOOT)
    oil = (smoothstep(0.62, 0.95, unit(r, (22, 1.0), (7, 0.5)))
           * np.exp(-((rad / 0.50) ** 2)))
    rgb = tint(rgb, oil * 0.55, OIL)

    # Downwash: everything loose is blown outward, so the dirt lies in streaks
    # that run with the radius.
    streak = smoothstep(0.55, 1.0, unit(r, (110, 1.2), (40, 0.7))) * smoothstep(0.20, 0.95, rad)
    rgb = tint(rgb, streak * 0.22, DIRT)

    # Dirt and moss climbing in from the edge, over the paint and all.
    creep = smoothstep(0.50, 0.94, rad) * (0.40 + 0.60 * smoothstep(0.35, 0.75,
                                                                   unit(r, (28, 1.0), (8, 0.5))))
    rgb = tint(rgb, creep * 0.48, DIRT)
    rgb = tint(rgb, creep * smoothstep(0.55, 0.85, unit(r, (13, 1.0))) * 0.35, MOSS)
    smooth = smooth * (1.0 - creep * 0.7) + 0.02 * creep
    metal = metal * (1.0 - creep * 0.85)

    # Slow, wide staining over the whole deck - rain, fuel, years - and then a
    # last quiet grime pass so nothing on it is a clean value.
    rgb = tint(rgb, smoothstep(0.42, 0.95, unit(r, (75, 1.0), (26, 0.6))) * 0.30, DIRT)
    rgb = shade(rgb, grey(r, (65, 0.045), (18, 0.028), (5, 0.015)))
    smooth = smooth * (1.0 - core * 0.45)
    return np.clip(rgb, 0.0, 1.0), metal, smooth


def apron(r, rgb, height, metal, smooth):
    """The outer band, 0.80..1.0: the ramp that carries the deck to the ground.

    It has to stop being a pad and become ground within one fifth of the radius,
    and it must not do it on a circle - a perfect ring around a built thing is
    exactly what makes it look dropped into the world. So the boundary wobbles,
    and gravel and earth spill inwards over the rim.
    """
    _, _, rad, ang = POLAR
    rad = np.minimum(rad, 1.0)      # the corners continue the outermost ring

    wobble = (0.020 * np.sin(ang * 3.0 + 1.1) + 0.014 * np.sin(ang * 5.0 - 0.4)
              + 0.010 * np.sin(ang * 11.0 + 2.2))
    t = smoothstep(DECK + wobble - 0.030, 1.0, rad)   # 0 on the deck, 1 at the rim

    ground = flat(GRAVEL) * (1.0 - t[..., None]) + flat(EARTH) * t[..., None]
    ground = shade(ground, grey(r, (24, 0.085), (6, 0.055), (2, 0.030)))
    ground = tint(ground, smoothstep(0.55, 0.85, unit(r, (10, 1.0))) * t * 0.35, MOSS)

    rgb = rgb * (1.0 - t[..., None]) + ground * t[..., None]

    # The kerb: the deck does end somewhere, and a hint of a shadow line is what
    # makes the ramp read as a ramp instead of a smear.
    kerb = band(rad - wobble, DECK - 0.010, DECK + 0.003, 0.008)
    rgb = tint(rgb, kerb * 0.26, (0.150, 0.132, 0.110))

    height = height * (1.0 - t) + grey(r, (3, 0.55), (6, 0.35)) * t - kerb * 1.1
    metal = metal * (1.0 - t)
    smooth = smooth * (1.0 - t) + 0.015 * t
    return np.clip(rgb, 0.0, 1.0), height, metal, smooth


# ------------------------------------------------------------------ output

def build(kind, seed):
    r = texlib.rng(seed)
    rgb, height, metal, smooth = steel(r) if kind == "steel" else concrete(r)
    rgb, height, metal, smooth = markings(r, rgb, height, metal, smooth)
    rgb, metal, smooth = weather(r, rgb, metal, smooth)
    rgb, height, metal, smooth = apron(r, rgb, height, metal, smooth)

    stem = os.path.join(ASSETS, "helipad_" + kind)
    Image.fromarray((np.clip(rgb, 0, 1) * 255).astype(np.uint8), "RGB").save(stem + ".png")

    h = height - height.min()
    if h.max() > 1e-6:
        h = h / h.max()
    texlib.height_to_normal(h.astype(np.float32), strength=2.4).resize(
        (HALF, HALF), Image.LANCZOS).save(stem + "_normal.png")

    # R = metallic, A = smoothness; green and blue carry the metallic value too,
    # so the file is readable when it is opened (texlib.gloss_quarter does the
    # same). The Standard shader reads R and A only.
    m = np.clip(metal, 0, 1)
    gloss = np.dstack([m, m, m, np.clip(smooth, 0, 1)])
    Image.fromarray((gloss * 255).astype(np.uint8), "RGBA").resize(
        (HALF, HALF), Image.LANCZOS).save(stem + "_metal.png")

    preview(stem, rgb, h)
    print("  helipad_%-9s albedo %dx%d  normal/metal %dx%d  mean %s"
          % (kind, S, S, HALF, HALF, texlib.mean_rgb(rgb[300:724, 300:724])))


def preview(stem, rgb, height):
    """Working material: the deck as it reads from above, lit from one side."""
    gx = np.zeros_like(height)
    gy = np.zeros_like(height)
    gx[:, 1:-1] = height[:, 2:] - height[:, :-2]
    gy[1:-1, :] = height[2:, :] - height[:-2, :]
    light = np.clip(1.0 + (gx - gy) * 4.0, 0.80, 1.25)
    shaded = np.clip(rgb * light[..., None], 0, 1)
    Image.fromarray((shaded * 255).astype(np.uint8), "RGB").resize(
        (HALF, HALF), Image.LANCZOS).save(stem + "_preview.png")


POLAR = polar()


def main():
    if not os.path.isdir(ASSETS):
        os.makedirs(ASSETS)
    print("Helipad deck textures")
    build("concrete", 20260921)
    build("steel", 20260922)
    return 0


if __name__ == "__main__":
    sys.exit(main())
