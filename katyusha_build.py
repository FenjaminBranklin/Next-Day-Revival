"""Build the Katyusha's art (RevivalKatyusha.cs).

No Katyusha, BM-13 or BM-21 model existed anywhere in the repository, its
branches or the main checkout (task n07, 2026-09-28), so this is the model:
a BM-13-style launcher - 8 guide rails, each carrying one M-13 rocket on top
and one hanging underneath, 16 rockets - that stands on the game's own
Ural-375 truck in place of that truck's machine-gun turret and bed boxes.

  katyusha_mount.ndmesh   the sub-frame on the bed: base plate, four legs,
                          two longitudinal bearers. Does not move.
  katyusha_base.ndmesh    the turntable with its two trunnion brackets and
                          the elevating screw housing. Turns (yaw).
  katyusha_rack.ndmesh    the rails with their cross frames and side truss.
                          Elevates (pitch) about the trunnion axis.
  katyusha_diffuse.png / katyusha_normal.png   one 1024 atlas for all three.

  2075 M-13 rocket (item) - 132 mm, 1.41 m, a slim body with the warhead's
       ogive, the four long tail fins and the two guide studs.
  m13.ndmesh, m13_diffuse.png, m13_normal.png, m13_icon.png

AXES AND UNITS. The launcher meshes are in REAL METRES in Unity's axes
(x right, y up, z forward = the firing direction at zero traverse). The
plugin scales them onto the truck by the donor's measured length, so no
number here fixes the size in the world. Origins:
  mount  bed-top centre (the plate's top face is y = 0)
  base   turntable centre on the plate (y = 0); the trunnion axis is at
         (0, TRUNNION_Y, 0)
  rack   the trunnion axis itself

RAIL SLOTS. The plugin puts one M-13 on every slot; the numbers are copied
into RevivalKatyusha.cs (KatyushaModel.Slot) and verify.py compares them:
  x = (i - 3.5) * RAIL_PITCH for rail i = 0..7
  y = +ROCKET_OFF (top row) and -ROCKET_OFF (bottom row)
  z = ROCKET_Z (rocket centre)

The item clones donor 2030 like the FAB-50 and is fitted to the same
reference box (fab50_build.py); its long axis lies along X, nose at -X.

Run through make_assets.py group "katyusha", or standalone:
    python katyusha_build.py
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

# Same donor-2030 reference as fab50_build.py / repair_items_build.py.
REF_SIZE = (0.364, 0.150, 0.370)
REF_CENTER = (-0.022, -0.001, -0.206)

# ---- the launcher, real metres (BM-13N on a ZiS-151: rails 5 m, rack 2.3 m wide)
RAIL_PITCH = 0.30          # rail to rail
RAILS = 8
RAIL_Z0, RAIL_Z1 = -2.1, 2.9   # rail length 5.0 m, trunnion 2.1 m from the rear
RAIL_H = 0.10              # I-beam height
ROCKET_OFF = 0.135         # rocket axis above / below the rail axis
ROCKET_Z = -1.15           # rocket centre along the rail: loaded at the breech end
TRUNNION_Y = 0.95          # trunnion axis above the plate
M13_LEN, M13_R = 1.41, 0.066

ROT_Y_TO_Z = (90.0, 0.0, 0.0)     # (x, y, z) -> (x, -z, y): +Y becomes +Z
ROT_Y_TO_X = (0.0, 0.0, -90.0)    # +Y becomes +X


def along_z(builder):
    """Build a part along +Y with `builder(mesh)` and lay it along +Z."""
    part = Mesh("part")
    builder(part)
    out = Mesh("part")
    out.merge(part, rot_deg=ROT_Y_TO_Z)
    return out


def along_x(builder):
    part = Mesh("part")
    builder(part)
    out = Mesh("part")
    out.merge(part, rot_deg=ROT_Y_TO_X)
    return out


# ---------------------------------------------------------------- the rocket

def rocket_along_y(m, tip=True):
    """M-13 along +Y, nose at +Y, centred on its length."""
    h = M13_LEN / 2.0
    m.tube(0.0, 0.0, -h + 0.30, h - 0.36, M13_R, "receiver", seg=20)          # motor body
    m.cone(0.0, 0.0, h - 0.36, h - 0.12, M13_R, 0.040, "stock", seg=20)       # warhead ogive
    m.cone(0.0, 0.0, h - 0.12, h - 0.02, 0.040, 0.016, "stock", seg=16)
    if tip:
        m.tube(0.0, 0.0, h - 0.02, h + 0.02, 0.012, "shroud", seg=10)          # fuze
    m.tube(0.0, 0.0, h - 0.40, h - 0.36, M13_R + 0.004, "detail", seg=20)     # joint band
    m.tube(0.0, 0.0, -h, -h + 0.30, M13_R * 0.92, "detail", seg=18)          # tail
    for sx, sz in ((1, 0), (-1, 0), (0, 1), (0, -1)):                        # four fins
        w, d = (0.09, 0.006) if sx else (0.006, 0.09)
        m.cbox(sx * (M13_R + 0.045), -h + 0.17, sz * (M13_R + 0.045), w, 0.30, d, "detail", c=0.002)
    m.tube(0.0, 0.0, -h - 0.01, -h + 0.01, M13_R * 0.55, "shroud", seg=12)   # nozzle


def rocket_item():
    m = Mesh("M13")
    rocket_along_y(m)
    m.cbox(0.0, 0.10, -M13_R - 0.012, 0.03, 0.05, 0.024, "shroud", c=0.004)  # guide studs
    m.cbox(0.0, -0.30, -M13_R - 0.012, 0.03, 0.05, 0.024, "shroud", c=0.004)
    out = Mesh("M13")
    out.merge(m, rot_deg=(0.0, 0.0, 90.0))     # +Y -> -X: nose at -X, like the FAB-50
    return out


# --------------------------------------------------------------- the launcher

def mount():
    m = Mesh("KatyushaMount")
    m.cbox(0.0, -0.06, 0.0, 2.30, 0.12, 3.00, "receiver", c=0.02)             # base plate
    for sx in (-1, 1):
        m.cbox(sx * 0.95, -0.22, 0.0, 0.16, 0.22, 3.10, "detail", c=0.015)   # bearers
        for sz in (-1, 1):
            m.cbox(sx * 0.95, -0.72, sz * 1.25, 0.14, 0.90, 0.14, "detail", c=0.012)  # legs
    for sz in (-1.3, 0.0, 1.3):
        m.cbox(0.0, -0.20, sz, 1.80, 0.10, 0.10, "detail", c=0.01)            # cross members
    for i in range(10):                                                     # tread plate ribs
        m.cbox(0.0, 0.005, -1.35 + i * 0.30, 2.1, 0.012, 0.04, "shroud", c=0.004)
    return m


def base():
    m = Mesh("KatyushaBase")
    m.tube(0.0, 0.0, 0.0, 0.10, 0.72, "detail", seg=32)                     # ring
    m.tube(0.0, 0.0, 0.10, 0.20, 0.62, "receiver", seg=32)                  # turntable
    for sx in (-1, 1):                                                      # trunnion brackets
        x = sx * 1.02
        m.ctaper(0.20, TRUNNION_Y - 0.05, (x, 0.0, 0.14, 1.10),
                 (x, 0.0, 0.12, 0.34), "receiver", c=0.015)
        m.cbox(x, 0.35, 0.0, 0.10, 0.30, 1.30, "detail", c=0.01)            # foot
        hub = along_x(lambda p: p.tube(0.0, 0.0, -0.09, 0.09, 0.13, "shroud", seg=18))
        m.merge(hub, offset=(x, TRUNNION_Y, 0.0))
    m.cbox(0.0, 0.26, 0.0, 2.08, 0.12, 0.26, "receiver", c=0.015)           # bracket tie
    m.tube(0.0, 0.62, 0.20, 0.62, 0.08, "detail", seg=14)                   # elevating screw housing
    m.cbox(0.62, 0.40, 0.55, 0.10, 0.34, 0.10, "detail", c=0.01)            # traverse handwheel post
    wheel = along_x(lambda p: p.tube(0.0, 0.0, -0.02, 0.02, 0.16, "shroud", seg=18))
    m.merge(wheel, offset=(0.70, 0.60, 0.55))
    return m


def rack():
    m = Mesh("KatyushaRack")
    length = RAIL_Z1 - RAIL_Z0
    zc = (RAIL_Z0 + RAIL_Z1) / 2.0
    for i in range(RAILS):
        x = (i - 3.5) * RAIL_PITCH
        # I-beam: web + two flanges, guide grooves carry the rockets' studs.
        m.cbox(x, 0.0, zc, 0.018, RAIL_H, length, "shroud", c=0.003)
        m.cbox(x, RAIL_H / 2.0, zc, 0.07, 0.014, length, "shroud", c=0.003)
        m.cbox(x, -RAIL_H / 2.0, zc, 0.07, 0.014, length, "shroud", c=0.003)
    width = RAILS * RAIL_PITCH + 0.12
    for z in (RAIL_Z0 + 0.12, 0.0, RAIL_Z1 - 0.25):                         # cross frames
        m.cbox(0.0, 0.0, z, width, 0.12, 0.10, "receiver", c=0.012)
        m.cbox(0.0, -0.30, z, width, 0.07, 0.07, "detail", c=0.01)
    for sx in (-1, 1):                                                      # side truss
        x = sx * (width / 2.0 + 0.03)
        m.cbox(x, 0.0, zc, 0.06, 0.10, length, "receiver", c=0.01)
        m.cbox(x, -0.30, zc - 0.3, 0.05, 0.07, length - 0.6, "detail", c=0.008)
        for k in range(5):
            z = RAIL_Z0 + 0.4 + k * (length - 0.8) / 4.0
            m.cbox(x, -0.15, z, 0.04, 0.30, 0.05, "detail", c=0.006)
        m.cbox(x, 0.0, 0.0, 0.08, 0.26, 0.26, "receiver", c=0.012)          # trunnion lug
    # blast deflector over the breech end
    m.cbox(0.0, 0.34, RAIL_Z0 + 0.05, width * 0.9, 0.04, 0.35, "detail", c=0.008)
    # elevating arc under the trunnion
    m.cbox(0.0, -0.46, 0.35, 0.06, 0.30, 0.70, "detail", c=0.008)
    return m


# ------------------------------------------------------------------ textures

def paint(r, rgb, rough=0.06, wear=40, bright=0.12, length=36):
    out = T.base(r, H, H, rgb, rough, scale=8)
    out = T.mottle(r, out, amount=0.07, scale=14)
    return T.scratches(r, out, wear, bright, length)


def texture(name, seed, quads):
    r = T.rng(seed)
    T.save_atlas(quads(r), os.path.join(ASSETS, name + "_diffuse.png"))
    T.save_height_atlas({
        "receiver": T.height_scratches(r, H, H, n=90, length=24),
        "detail":   T.height_scratches(r, H, H, n=110, length=22),
        "stock":    T.height_scratches(r, H, H, n=50, length=16),
        "shroud":   T.height_scratches(r, H, H, n=140, length=30),
    }, os.path.join(ASSETS, name + "_normal.png"), strength=2.4)
    return os.path.join(ASSETS, name + "_diffuse.png")


def launcher_quads(r):
    # Worn Soviet olive on the frames (style bible: military paint, worn,
    # value ~.21, saturation ~.2), bare grey-brown steel on the rails that
    # the rockets scrape along, dark grimy olive on the fittings.
    return {
        "receiver": paint(r, (66, 70, 46), rough=0.07, wear=70),
        "detail":   paint(r, (44, 46, 34), rough=0.08, wear=80, bright=0.10),
        "stock":    paint(r, (96, 70, 44), rough=0.09, wear=60),
        "shroud":   T.base(r, H, H, (92, 88, 80), 0.06, scale=7),
    }


def rocket_quads(r):
    return {
        "receiver": paint(r, (70, 74, 60), rough=0.05, wear=40),     # grey-green motor
        "detail":   paint(r, (50, 52, 44), rough=0.05, wear=50),     # fins, tail
        "stock":    paint(r, (84, 86, 70), rough=0.05, wear=30),     # warhead
        "shroud":   T.base(r, H, H, (150, 148, 140), 0.04, scale=7),  # steel
    }


def write(mesh, name):
    path = os.path.join(ASSETS, name + ".ndmesh")
    mesh.write(path)
    (x0, x1), (y0, y1), (z0, z1) = mesh.bounds()
    print("%s: %d tris, ext %.3f x %.3f x %.3f"
          % (name, len(mesh.IDX) // 3, x1 - x0, y1 - y0, z1 - z0))
    return path


def build():
    os.makedirs(ASSETS, exist_ok=True)
    write(mount(), "katyusha_mount")
    write(base(), "katyusha_base")
    write(rack(), "katyusha_rack")
    texture("katyusha", 2075, launcher_quads)

    item = rocket_item()
    k = item.fit_box(REF_SIZE, REF_CENTER)
    path = write(item, "m13")
    tex = texture("m13", 2076, rocket_quads)
    iconlib.pack_icon(path, tex, os.path.join(ASSETS, "m13_icon.png"))
    print("m13: fit %.3f" % k)


if __name__ == "__main__":
    build()
