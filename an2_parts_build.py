"""Build the three An-2 repair parts their own art (Revival.An2Repair.cs).

  2069 control cable set  - a steel cable reel with two turnbuckles and a tag
  2070 magneto             - the radial engine's ignition magneto: a finned
                              body, a round distributor cap with its leads and
                              a drive flange
  2071 propeller assembly  - a four-blade hub, the blades cut short (the
                              repair set, not the 3.6 m propeller)

All three clone donor 2030 like the other carried gear, so they are fitted to
the SAME reference box as repair_items_build.py (magaz_l: X wide, Y thin, Z
tall) via fit_box and sit in the cloned prefab's collider. The propeller is
scaled up (factor) because it is the bulky carry of the repair loop.

Outputs (assets/): an2part_cable.*, an2part_magneto.*, an2part_prop.*
(ndmesh, _diffuse.png, _normal.png, _icon.png).

Run through make_assets.py group "an2parts", or standalone:
    python an2_parts_build.py
"""

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ndmesh import Mesh
import texlib as T
import iconlib

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HIER, "assets")
H = T.H

# Same donor-2030 reference as repair_items_build.py.
REF_SIZE = (0.364, 0.150, 0.370)
REF_CENTER = (-0.022, -0.001, -0.206)


# ------------------------------------------------------------------- meshes

def cable_reel():
    """A steel cable reel standing on edge: two flanges and the wound core,
    all along Y (the thin axis), a turnbuckle bar on each side and a paper
    tag on a wire at the top."""
    m = Mesh("An2CableSet")
    m.tube(0.0, 0.0, -0.20, -0.16, 0.46, "detail", seg=32)     # front flange
    m.tube(0.0, 0.0, 0.16, 0.20, 0.46, "detail", seg=32)       # back flange
    m.tube(0.0, 0.0, -0.16, 0.16, 0.38, "receiver", seg=32)    # wound cable
    m.tube(0.0, 0.0, -0.24, 0.24, 0.09, "shroud", seg=16)      # axle boss
    for x in (-0.58, 0.58):
        m.cbox(x, 0.0, -0.10, 0.08, 0.10, 0.44, "shroud", c=0.01)   # turnbuckle body
        m.tube(x, -0.10 + 0.30, -0.03, 0.03, 0.03, "stock", seg=10)  # eye
    m.cbox(0.0, 0.0, 0.55, 0.26, 0.03, 0.16, "stock", c=0.006)  # tag
    m.cbox(0.0, 0.0, 0.47, 0.02, 0.02, 0.08, "shroud", c=0.004)  # tag wire
    return m


def magneto():
    """A magneto on its side: finned body, a round distributor cap with
    nine lead sockets on the front, a drive flange at the back."""
    b = Mesh("An2Magneto")
    b.cbox(0.0, 0.0, 0.0, 0.62, 0.40, 0.44, "receiver", c=0.03)      # body
    for z in (-0.16, -0.08, 0.0, 0.08, 0.16):
        b.cbox(0.0, 0.0, z, 0.66, 0.30, 0.025, "detail", c=0.006)    # fins
    b.tube(0.0, 0.0, 0.20, 0.30, 0.20, "stock", seg=28)              # cap
    for i in range(9):
        a = 2.0 * math.pi * i / 9.0
        b.tube(0.14 * math.cos(a), 0.14 * math.sin(a), 0.30, 0.36, 0.022,
               "shroud", seg=8)                                      # lead sockets
    b.tube(0.0, 0.0, -0.30, -0.20, 0.16, "shroud", seg=24)           # drive flange
    b.tube(0.0, 0.0, -0.40, -0.30, 0.04, "shroud", seg=12)           # shaft stub
    b.cbox(0.0, 0.0, -0.27, 0.34, 0.14, 0.05, "detail", c=0.008)     # mounting foot
    # Lay the long axis (cap to shaft, built along Y) along X, the item's
    # wide axis, so fit_box does not squeeze it into the thin one.
    out = Mesh("An2Magneto")
    out.merge(b, rot_deg=(0.0, 0.0, 90.0))
    return out


def propeller():
    """A four-blade hub in the XZ plane, the blade roots cut short, the hub
    along Y."""
    p = Mesh("An2Propeller")
    p.tube(0.0, 0.0, -0.14, 0.14, 0.15, "shroud", seg=28)            # hub
    p.cone(0.0, 0.0, 0.14, 0.26, 0.15, 0.04, "shroud", seg=28)       # spinner
    p.tube(0.0, 0.0, -0.22, -0.14, 0.10, "detail", seg=20)           # shaft flange
    for sx, sz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        cx, cz = sx * 0.40, sz * 0.40
        w, h = (0.54, 0.13) if sx else (0.13, 0.54)
        p.cbox(cx, 0.0, cz, w, 0.04, h, "receiver", c=0.01)          # blade
        tx, tz = sx * 0.66, sz * 0.66
        tw, th = (0.02, 0.13) if sx else (0.13, 0.02)
        p.cbox(tx, 0.0, tz, tw, 0.045, th, "stock", c=0.004)         # yellow tip
    return p


# ------------------------------------------------------------------ textures

def paint(r, rgb, rough=0.05, wear=30, bright=0.13, length=32):
    out = T.base(r, H, H, rgb, rough, scale=8)
    return T.scratches(r, out, wear, bright, length)


def texture(name, seed, colours):
    r = T.rng(seed)
    quads = {
        "receiver": paint(r, colours[0], rough=0.05, wear=40),
        "detail":   paint(r, colours[1], rough=0.05, wear=60),
        "stock":    paint(r, colours[2], rough=0.06, wear=40),
        "shroud":   T.base(r, H, H, colours[3], 0.04, scale=7),
    }
    T.save_atlas(quads, os.path.join(ASSETS, name + "_diffuse.png"))
    T.save_height_atlas({
        "receiver": T.height_scratches(r, H, H, n=60, length=20),
        "detail":   T.height_scratches(r, H, H, n=80, length=22),
        "stock":    T.height_scratches(r, H, H, n=40, length=16),
        "shroud":   T.height_scratches(r, H, H, n=40, length=14),
    }, os.path.join(ASSETS, name + "_normal.png"), strength=2.2)
    return os.path.join(ASSETS, name + "_diffuse.png")


# -------------------------------------------------------------------- driver

PARTS = [
    # name, mesh, fit factor, seed, (receiver, detail, stock, shroud)
    ("an2part_cable", cable_reel, 1.0, 2069,
     ((128, 130, 134), (70, 74, 60), (214, 200, 160), (168, 170, 176))),
    ("an2part_magneto", magneto, 1.0, 2070,
     ((58, 62, 66), (40, 42, 44), (120, 34, 30), (186, 150, 70))),
    ("an2part_prop", propeller, 1.6, 2071,
     ((38, 40, 38), (60, 64, 58), (214, 180, 40), (170, 172, 176))),
]


def build(name, fn, factor, seed, colours):
    mesh = fn()
    path = os.path.join(ASSETS, name + ".ndmesh")
    size = tuple(REF_SIZE[i] * factor for i in range(3))
    k = mesh.fit_box(size, REF_CENTER)
    mesh.write(path)
    (x0, x1), (y0, y1), (z0, z1) = mesh.bounds()
    tex = texture(name, seed, colours)
    iconlib.pack_icon(path, tex, os.path.join(ASSETS, name + "_icon.png"))
    print("%s: %d tris, fit %.3f, ext %.3f x %.3f x %.3f"
          % (name, len(mesh.IDX) // 3, k, x1 - x0, y1 - y0, z1 - z0))


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    for part in PARTS:
        build(*part)
