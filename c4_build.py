"""Build the C4 demolition set its own art: the charge (1493), the remote
detonator (1494) and the defuse kit (2078), see Revival.C4.cs.

All three are simple shapes the procedural pipeline handles well, in the
vanilla look (worn olive/khaki paint, black tape, dull steel), so no imported
model is needed:

- c4: two wrapped demolition blocks bound with black tape, a detonator box
  with its LED housing on top and a red and a blue wire looping from the box
  into the blocks. Y-up and thin in Y (ndmesh axes are the game's axes, as for
  mine.ndmesh), so a placed charge lies flat on the surface it is stuck to;
  Revival.C4.cs turns the thinnest axis onto the surface normal.
- detonator: a handheld radio firing box with an antenna and a guarded red
  button, standing in Y.
- defusekit: a canvas tool roll with wire cutters strapped on, cloned from
  donor 2030 like the heavy tool kit, so it is fitted on the same reference
  box as repair_items_build.py.

Sizes are game units (about 2.8 per metre).

Outputs (assets/): c4.*, detonator.*, defusekit.* (.ndmesh, _diffuse.png,
_normal.png, _icon.png). Run through make_assets.py group "c4", or:
    python c4_build.py
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

# Donor-2030 reference box (repair_items_build.py): X wide, Y thin, Z tall.
REF_SIZE = (0.364, 0.150, 0.370)
REF_CENTER = (-0.022, -0.001, -0.206)


# ------------------------------------------------------------------- meshes

def charge():
    """Two M112-style blocks side by side (0.78 x 0.15 x 0.32 units, about
    28 x 5 x 11 cm), three bands of black tape, the detonator box on top with
    an LED housing, and two wires arching from the box into the blocks."""
    m = Mesh("C4Charge")
    # The blocks (khaki wrap), slightly apart so the seam reads.
    for z in (-0.082, 0.082):
        m.cbox(0.0, 0.0, z, 0.78, 0.15, 0.155, "receiver", c=0.012)
    # Tape bands (black) around both blocks.
    for x in (-0.27, 0.0, 0.27):
        m.cbox(x, 0.0, 0.0, 0.065, 0.162, 0.335, "stock", c=0.006)
    # Detonator box (dark olive) on top, a little off centre.
    m.cbox(-0.12, 0.115, 0.0, 0.20, 0.085, 0.13, "detail", c=0.008)
    # LED housing and lens (the runtime adds the blinking light here).
    m.tube(-0.06, 0.0, 0.155, 0.18, 0.018, "shroud", seg=12)
    # Blasting cap sleeve going into the near block.
    m.cbox(-0.24, 0.095, 0.0, 0.07, 0.03, 0.03, "shroud", c=0.004)
    # Red and blue wire: three short segments each, an arch from box to block.
    for z, region in ((0.035, "stock"), (-0.035, "shroud")):
        m.cbox(0.00, 0.125, z, 0.06, 0.016, 0.016, region, c=0.003)
        m.cbox(0.06, 0.135, z, 0.07, 0.016, 0.016, region, c=0.003)
        m.cbox(0.12, 0.112, z, 0.06, 0.016, 0.016, region, c=0.003)
        m.cbox(0.155, 0.090, z, 0.016, 0.03, 0.016, region, c=0.003)
    return m


def detonator():
    """A handheld firing box standing in Y (0.22 x 0.34 x 0.11 units): the
    body, a darker front panel, a guarded red button on top and a whip
    antenna."""
    m = Mesh("C4Detonator")
    m.cbox(0.0, 0.0, 0.0, 0.22, 0.34, 0.11, "detail", c=0.012)        # body
    m.cbox(0.0, 0.02, 0.058, 0.17, 0.22, 0.012, "stock", c=0.004)     # front panel
    m.tube(0.04, 0.0, 0.17, 0.205, 0.032, "stock", seg=16)            # button guard
    m.tube(0.04, 0.0, 0.17, 0.225, 0.020, "receiver", seg=14)         # red button
    m.tube(-0.07, 0.0, 0.17, 0.20, 0.016, "shroud", seg=10)           # antenna foot
    m.tube(-0.07, 0.0, 0.20, 0.52, 0.006, "shroud", seg=8)            # whip
    m.cbox(0.0, -0.05, 0.062, 0.05, 0.03, 0.01, "shroud", c=0.002)    # arming switch
    return m


def defusekit():
    """A canvas tool roll (X wide, Z tall, Y the shallow depth like magaz_l)
    with a strap and a pair of wire cutters with red grips on its face."""
    m = Mesh("C4DefuseKit")
    m.cbox(0.0, 0.0, 0.0, 0.90, 0.30, 0.62, "receiver", c=0.05)       # canvas roll
    m.cbox(0.0, 0.0, 0.0, 0.10, 0.32, 0.66, "stock", c=0.01)          # strap
    m.cbox(-0.22, 0.17, 0.10, 0.07, 0.04, 0.34, "detail", c=0.01)     # cutter grip
    m.cbox(-0.12, 0.17, 0.10, 0.07, 0.04, 0.34, "detail", c=0.01)     # cutter grip
    m.cbox(-0.17, 0.17, -0.14, 0.12, 0.04, 0.14, "shroud", c=0.01)    # steel jaws
    m.cbox(0.25, 0.17, 0.0, 0.26, 0.03, 0.18, "shroud", c=0.008)      # probe pouch flap
    return m


# ------------------------------------------------------------------ textures

def paint(r, rgb, rough=0.05, wear=30, bright=0.13, length=32):
    out = T.base(r, H, H, rgb, rough, scale=8)
    return T.scratches(r, out, wear, bright, length)


def textures(name, seed, quads_rgb):
    r = T.rng(seed)
    quads = {}
    for region, (rgb, rough, wear) in quads_rgb.items():
        quads[region] = paint(r, rgb, rough=rough, wear=wear)
    T.save_atlas(quads, os.path.join(ASSETS, name + "_diffuse.png"))
    T.save_height_atlas({
        "receiver": T.height_scratches(r, H, H, n=60, length=20),
        "detail":   T.height_scratches(r, H, H, n=80, length=22),
        "stock":    T.height_scratches(r, H, H, n=50, length=18),
        "shroud":   T.height_scratches(r, H, H, n=40, length=14),
    }, os.path.join(ASSETS, name + "_normal.png"), strength=2.2)
    return os.path.join(ASSETS, name + "_diffuse.png")


C4_RGB = {
    "receiver": ((156, 146, 108), 0.06, 28),   # khaki wrap of the blocks
    "detail":   ((70, 76, 52), 0.05, 50),      # olive detonator box
    "stock":    ((30, 30, 32), 0.05, 40),      # black tape / red wire region
    "shroud":   ((54, 74, 120), 0.04, 30),     # blue wire, cap sleeve, LED housing
}
DET_RGB = {
    "receiver": ((170, 34, 28), 0.05, 20),     # red firing button
    "detail":   ((72, 78, 54), 0.05, 60),      # olive body
    "stock":    ((32, 32, 34), 0.05, 44),      # black panel / guard
    "shroud":   ((120, 122, 128), 0.05, 30),   # steel antenna, switch
}
KIT_RGB = {
    "receiver": ((96, 98, 66), 0.07, 40),      # olive canvas
    "detail":   ((150, 40, 32), 0.05, 36),     # red cutter grips
    "stock":    ((44, 40, 34), 0.06, 40),      # dark strap
    "shroud":   ((118, 120, 126), 0.05, 34),   # steel jaws, flap
}


# -------------------------------------------------------------------- driver

def lay_for_icon(mesh):
    """Tip a Y-thin model 90 degrees about Z so its big face looks at the icon
    camera (the anti-tank mine's icon trick); the shipped mesh stays as is."""
    mesh.V = [(-p[1], p[0], p[2]) for p in mesh.V]
    mesh.N = [(-n[1], n[0], n[2]) for n in mesh.N]
    return mesh


def build(make, name, tex_rgb, seed, fit=False, flat_icon=False):
    path = os.path.join(ASSETS, name + ".ndmesh")
    mesh = make()
    if fit:
        mesh.fit_box(REF_SIZE, REF_CENTER)
    mesh.write(path)
    (x0, x1), (y0, y1), (z0, z1) = mesh.bounds()
    tex = textures(name, seed, tex_rgb)
    icon_path = path
    if flat_icon:
        icon_mesh = lay_for_icon(make())
        icon_path = os.path.join(ASSETS, "_" + name + "_icon_tmp.ndmesh")
        icon_mesh.write(icon_path)
    try:
        iconlib.item_icon(icon_path, tex, os.path.join(ASSETS, name + "_icon.png"), size=300,
                          tilt=0.0 if flat_icon else 49.0)
    finally:
        if flat_icon and os.path.exists(icon_path):
            os.remove(icon_path)
    print("%s: %d tris, ext %.3f x %.3f x %.3f"
          % (name, len(mesh.IDX) // 3, x1 - x0, y1 - y0, z1 - z0))


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    build(charge, "c4", C4_RGB, 1493, flat_icon=True)
    build(detonator, "detonator", DET_RGB, 1494)
    build(defusekit, "defusekit", KIT_RGB, 2078, fit=True)
