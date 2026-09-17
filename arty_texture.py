"""The howitzer's material atlas - four quarters, one per material group.

Run: python arty_texture.py        (before arty_import.py, which reads it)

WHY THIS FILE EXISTS. The 6.22.0 atlas was a set of weathered photo-style
surfaces: olive steel under heavy rust and moss, a tyre, dark rusted plate and
blue-grey plate. On the vehicle it read as a wreck that had stood in a forest
for twenty years, and the field report was blunt - "the texture of the arty
battery is completely ugly" (2026-09-17).

THE REASON IT LOOKED THAT WAY IS THE MAPPING, NOT THE PAINT. arty_import.py
projects every triangle PLANARLY over the whole vehicle's bounding box into one
quarter of this atlas:

    uv = tile * 0.5 + 0.015 + (point - low) / span * 0.47

`span` is the entire truck, about 10.5 m, and a quarter is 627 px. That is
about 60 px per metre, so a feature 60 px wide on this image is a METRE wide on
the hull. The old atlas was built from surfaces whose blotches are 60 to 120 px
across; blown up to one and two metres they stopped reading as rust and started
reading as camouflage-sized stains, and the eye reads that as "broken", not as
"used".

SO THE RULE FOR THIS ATLAS IS A LENGTH: nothing structural above about 15 px
(a quarter of a metre). Fine grain, thin scratches, weld seams, a few small
chips - and only a very gentle large-scale shading, far too weak to read as a
pattern. Everything here is procedural and deterministic (texlib, one seed), so
the same bytes come out on every machine.

The quarter layout is fixed by `surface_uv` in arty_import.py and must not be
reordered:

    top left      body      painted olive drab - cab, hull, gun shield, barrel
    top right     rubber    tyres
    bottom left   steel     dark machinery: running gear, breech, stabilizers
    bottom right  glass     glazing and dark hatches
"""

import numpy as np
from PIL import Image

import texlib

SIZE = 1254                  # unchanged from 6.22.0; arty_import wants >= 1024
HALF = SIZE // 2

# Matte military olive drab, and the three darker surfaces around it.
BODY = (78, 82, 58)
RUBBER = (38, 38, 40)
STEEL = (58, 59, 62)
GLASS = (42, 48, 55)


def seams(r, arr, spacing, width, depth, axis):
    """Panel joints: a dark hairline every `spacing` px, straight, with a
    faint highlight on one side of it. This is the only large-scale structure
    in the atlas, and it is a LINE, so it survives the 60 px/m stretch as a
    line instead of as a blob."""
    h, w, _ = arr.shape
    out = arr.copy()
    n = w if axis == "x" else h
    for p in range(0, n, spacing):
        jitter = int(r.integers(-2, 3))
        a = max(0, min(n - 1, p + jitter))
        b = max(0, min(n - 1, a + width))
        if axis == "x":
            out[:, a:b] *= 1.0 - depth
            out[:, b:b + 1] *= 1.0 + depth * 0.45
        else:
            out[a:b, :] *= 1.0 - depth
            out[b:b + 1, :] *= 1.0 + depth * 0.45
    return np.clip(out, 0, 1)


def chips(r, arr, n, radius, color):
    """Small paint chips down to the primer. Radius is in PIXELS and stays
    under five, which is under ten centimetres on the truck."""
    h, w, _ = arr.shape
    out = arr.copy()
    ys = r.integers(0, h, n)
    xs = r.integers(0, w, n)
    for y, x in zip(ys, xs):
        rad = int(max(1, r.integers(1, radius + 1)))
        y0, y1 = max(0, y - rad), min(h, y + rad + 1)
        x0, x1 = max(0, x - rad), min(w, x + rad + 1)
        yy, xx = np.mgrid[y0:y1, x0:x1]
        mask = ((yy - y) ** 2 + (xx - x) ** 2) <= rad * rad
        if not mask.any():
            continue
        patch = out[y0:y1, x0:x1]
        tint = np.asarray(color, np.float32) / 255.0
        patch[mask] = patch[mask] * 0.30 + tint * 0.70
    return np.clip(out, 0, 1)


def body_quarter(r):
    """Painted steel. Fine grain, weld seams, light use - no blotches."""
    a = texlib.base(r, HALF, HALF, BODY, rough=0.017, scale=6)
    # Two very weak clouds: enough that a flat panel is not dead, far too weak
    # to be seen as a shape once it is stretched over a metre.
    a = texlib.mottle(r, a, amount=0.013, scale=11, tint=(1.0, 0.99, 0.92))
    a = texlib.mottle(r, a, amount=0.009, scale=24, tint=(0.96, 1.0, 0.95))
    a = seams(r, a, spacing=104, width=2, depth=0.13, axis="x")
    a = seams(r, a, spacing=138, width=2, depth=0.10, axis="y")
    a = texlib.scratches(r, a, 22, 0.038, length=34, direction=0.0)
    a = texlib.scratches(r, a, 14, 0.030, length=26, direction=90.0)
    # A trace of rust where paint gives up, and bare primer under the chips.
    # Deliberately sparse: this is a gun in service, not one in a field.
    a = chips(r, a, 120, 2, (86, 76, 58))
    a = chips(r, a, 40, 2, (104, 82, 60))
    return a


def rubber_quarter(r):
    """Tyre. The tread pitch is 18 px - about 30 cm - so a wheel a metre
    across still shows three or four blocks instead of one dark smear."""
    a = texlib.base(r, HALF, HALF, RUBBER, rough=0.018, scale=4)
    y, x = np.mgrid[0:HALF, 0:HALF]
    # Chevron blocks: two mirrored rows of angled bars with a rib between them.
    pitch = 18.0
    phase = (x + np.where(y % (2 * pitch) < pitch, 1.0, -1.0) * y * 0.55) % pitch
    block = (phase < pitch * 0.62).astype(np.float32)
    rib = (np.abs((y % (2 * pitch)) - pitch) < 2.0).astype(np.float32)
    height = np.clip(block * 0.85 + rib * 0.6, 0, 1)
    shade = 0.62 + 0.38 * height
    a = a * shade[..., None]
    # Sidewall dust, and the grey of a tyre that has been driven.
    a = texlib.mottle(r, a, amount=0.022, scale=7, tint=(1.0, 0.95, 0.85))
    return np.clip(a, 0, 1)


def steel_quarter(r):
    """Running gear, breech, stabilizer feet: oiled dark steel, worn bright
    where it is handled, never rusted through."""
    a = texlib.base(r, HALF, HALF, STEEL, rough=0.022, scale=5)
    a = texlib.mottle(r, a, amount=0.018, scale=13, tint=(1.0, 0.97, 0.90))
    a = texlib.scratches(r, a, 34, 0.055, length=30, direction=0.0)
    a = texlib.scratches(r, a, 22, 0.045, length=22, direction=90.0)
    a = seams(r, a, spacing=88, width=2, depth=0.14, axis="y")
    a = chips(r, a, 90, 2, (92, 76, 58))
    return a


def glass_quarter(r):
    """Glazing and dark hatches. Nearly flat on purpose: the shader's own
    specular does the work, and any pattern here becomes a stain on a
    windscreen."""
    a = texlib.base(r, HALF, HALF, GLASS, rough=0.018, scale=4)
    a = texlib.mottle(r, a, amount=0.020, scale=16, tint=(0.92, 0.97, 1.0))
    a = texlib.scratches(r, a, 10, 0.040, length=40, direction=15.0)
    return a


def main():
    r = texlib.rng(0x4127)
    tex = np.zeros((SIZE, SIZE, 3), np.float32)
    tex[0:HALF, 0:HALF] = body_quarter(r)
    tex[0:HALF, HALF:SIZE] = rubber_quarter(r)
    tex[HALF:SIZE, 0:HALF] = steel_quarter(r)
    tex[HALF:SIZE, HALF:SIZE] = glass_quarter(r)
    img = Image.fromarray((np.clip(tex, 0, 1) * 255).astype(np.uint8))
    path = "assets/arty_diffuse.png"
    img.save(path)
    print(path, img.size)
    for name, quad in (("body", tex[0:HALF, 0:HALF]),
                       ("rubber", tex[0:HALF, HALF:SIZE]),
                       ("steel", tex[HALF:SIZE, 0:HALF]),
                       ("glass", tex[HALF:SIZE, HALF:SIZE])):
        print("   %-7s mean %s" % (name, texlib.mean_rgb(quad)))


if __name__ == "__main__":
    main()
