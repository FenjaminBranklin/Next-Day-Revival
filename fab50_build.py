"""Build the An-2's bomb item its own art (Revival.An2Bombs.cs).

  2072 FAB-50 bomb - a small Soviet high-explosive bomb: a round body with an
                     ogive nose and its fuze, a tapered tail with four fins in
                     a box ring, the suspension lug on top and a yellow band.

It clones donor 2030 like the other carried gear, so it is fitted to the SAME
reference box as repair_items_build.py (magaz_l: X wide, Y thin, Z tall) via
fit_box. The long axis lies along X (the item's wide axis), nose at -X; the
falling bomb in the world (Revival.An2Bombs.cs) loads the same mesh and scales
it to the real 1.07 m.

Outputs (assets/): fab50.ndmesh, fab50_diffuse.png, fab50_normal.png,
fab50_icon.png.

Run through make_assets.py group "fab50", or standalone:
    python fab50_build.py
"""

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


def bomb():
    """Built along Y, nose at +Y, in real proportions (1.07 m x 0.20 m),
    then laid along X."""
    b = Mesh("Fab50")
    b.cone(0.0, 0.0, 0.30, 0.44, 0.10, 0.06, "receiver", seg=28)    # ogive, rear
    b.cone(0.0, 0.0, 0.44, 0.52, 0.06, 0.025, "receiver", seg=28)   # ogive, front
    b.tube(0.0, 0.0, 0.52, 0.56, 0.022, "shroud", seg=14)           # nose fuze
    b.tube(0.0, 0.0, -0.10, 0.30, 0.10, "receiver", seg=28)         # body
    b.tube(0.0, 0.0, 0.10, 0.16, 0.102, "stock", seg=28)            # yellow band
    b.cone(0.0, 0.0, -0.34, -0.10, 0.055, 0.10, "receiver", seg=28)  # tail cone
    for sx, sz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        w, d = (0.20, 0.008) if sx else (0.008, 0.20)
        b.cbox(sx * 0.10, -0.42, sz * 0.10, w, 0.22, d, "detail", c=0.002)   # fin
    b.tube(0.0, 0.0, -0.53, -0.47, 0.135, "detail", seg=24)         # fin box ring
    b.cbox(0.0, 0.06, 0.105, 0.03, 0.05, 0.03, "shroud", c=0.004)   # lug
    out = Mesh("Fab50")
    out.merge(b, rot_deg=(0.0, 0.0, 90.0))
    return out


def paint(r, rgb, rough=0.05, wear=30, bright=0.13, length=32):
    out = T.base(r, H, H, rgb, rough, scale=8)
    return T.scratches(r, out, wear, bright, length)


def texture(name, seed):
    r = T.rng(seed)
    quads = {
        "receiver": paint(r, (74, 80, 62), rough=0.06, wear=50),    # grey-green
        "detail":   paint(r, (58, 62, 52), rough=0.05, wear=60),    # fins
        "stock":    paint(r, (196, 164, 52), rough=0.05, wear=30),  # band
        "shroud":   T.base(r, H, H, (150, 150, 146), 0.04, scale=7),  # steel
    }
    T.save_atlas(quads, os.path.join(ASSETS, name + "_diffuse.png"))
    T.save_height_atlas({
        "receiver": T.height_scratches(r, H, H, n=60, length=20),
        "detail":   T.height_scratches(r, H, H, n=80, length=22),
        "stock":    T.height_scratches(r, H, H, n=40, length=16),
        "shroud":   T.height_scratches(r, H, H, n=40, length=14),
    }, os.path.join(ASSETS, name + "_normal.png"), strength=2.2)
    return os.path.join(ASSETS, name + "_diffuse.png")


def build(name="fab50"):
    mesh = bomb()
    path = os.path.join(ASSETS, name + ".ndmesh")
    k = mesh.fit_box(REF_SIZE, REF_CENTER)
    mesh.write(path)
    (x0, x1), (y0, y1), (z0, z1) = mesh.bounds()
    tex = texture(name, 2072)
    iconlib.pack_icon(path, tex, os.path.join(ASSETS, name + "_icon.png"))
    print("%s: %d tris, fit %.3f, ext %.3f x %.3f x %.3f"
          % (name, len(mesh.IDX) // 3, k, x1 - x0, y1 - y0, z1 - z0))


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    build()
