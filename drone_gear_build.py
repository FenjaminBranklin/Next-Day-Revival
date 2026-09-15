"""Build the three drone-gear items their OWN art: the mast antenna, the drone
battery and the surveillance drone.

Items 2055, 2056 and 2057 (RevivalDroneGear.cs) shipped on borrowed placeholder
art - the mast antenna wore the portable jammer, the battery wore the .50 ammo
tin, and the recon drone wore the FPV drone. In the backpack the antenna was a
second R-330 and the battery a second ammunition box, which is exactly the
confusion the item names were there to prevent.

    antenna_pack  the mast FOLDED for carrying: four telescoping sections
                  bundled on a frame, two webbing straps, a foot plate with guy
                  lugs and the ground-plane radials folded up at the head
    battery       a lithium traction pack: shrink-wrapped cell block, label,
                  charge lamps, two power leads into a keyed yellow connector
    survdrone     a large folded multirotor: four arms with their blades folded
                  along them, a nose gimbal ball and landing skids

Deliberate, and the reason the recon drone still gets a new INVENTORY model at
all: the drone the player FLIES stays the FPV airframe scaled up
(RevivalSurvDrone.Shape -> Drone.Modell.Bauen -> drone.ndmesh). That is on
purpose - it reads as a large version of the same machine in the air. Only the
item in the backpack and on the ground is this one, and there it should look
like a machine that is folded up for carrying.

All three clone donor 2030, so they are fitted to the SAME reference box as
ammo_mesh.py / jammer_mesh.py / repair_items_build.py (magaz_l: X wide, Y thin,
Z tall) and sit inside their cloned prefab's collider.

Outputs (assets/): antenna_pack.*, battery.*, survdrone.*. The DEPLOYED head of
the mast is a separate model and is not touched here - it is built by
antenna_head.py and lives on the extended mast at runtime.

C# side names these files in DroneGear.AddItems; verify.py lists them. Run
through make_assets.py group "dronegear", or standalone:
    python drone_gear_build.py
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

REF_SIZE = (0.364, 0.150, 0.370)
REF_CENTER = (-0.022, -0.001, -0.206)


# ------------------------------------------------------------------- meshes

def antenna_pack():
    """Mast antenna (2055), folded on its carry frame. The kit draws tubes along
    Y, so every upright part is built along Y and swung about X by the merge -
    the same move repair_items_build.py uses to stand the extinguisher up."""
    m = Mesh("Mast Antenna Pack")

    # Carry frame behind the bundle: two uprights and three cross bars.
    rail = Mesh("frame rail")
    rail.tube(0.0, 0.0, -0.86, 0.86, 0.055, "stock", seg=14)
    for x in (-0.62, 0.62):
        m.merge(rail, rot_deg=(90.0, 0.0, 0.0), offset=(x, -0.18, 0.0))
    cross = Mesh("frame cross bar")
    cross.tube(0.0, 0.0, -0.66, 0.66, 0.05, "stock", seg=14)
    for z in (-0.78, 0.0, 0.78):
        m.merge(cross, rot_deg=(0.0, 0.0, 90.0), offset=(0.0, -0.18, z))

    # The mast itself: four telescoping sections side by side, each thinner and
    # a different length, each with the collar the next one slides through.
    for x, rad, top in ((-0.42, 0.17, 0.74), (-0.14, 0.15, 0.86),
                        (0.14, 0.13, 0.94), (0.42, 0.11, 0.66)):
        part = Mesh("mast section")
        part.tube(0.0, 0.0, -0.74, top, rad, "receiver", seg=18)
        part.tube(0.0, 0.0, -0.12, 0.02, rad + 0.035, "shroud", seg=18)
        part.cone(0.0, 0.0, top, top + 0.06, rad, rad * 0.55, "detail", seg=16)
        m.merge(part, rot_deg=(90.0, 0.0, 0.0), offset=(x, 0.10, 0.0))

    # Two webbing straps hold the bundle against the frame.
    for z in (-0.44, 0.46):
        m.cbox(0.0, 0.10, z, 1.22, 0.50, 0.09, "detail", c=0.012)

    # Foot plate with the two guy lugs.
    m.cbox(0.0, 0.06, -0.84, 1.26, 0.40, 0.16, "shroud", c=0.02)
    for x in (-0.52, 0.52):
        m.cbox(x, 0.06, -0.96, 0.16, 0.22, 0.14, "detail", c=0.012)

    # Ground-plane radials, folded up along the head of the bundle. They start
    # at z 0.62, where all four mast sections still are - higher up the two short
    # ones have ended and the rods would begin in mid air.
    radial = Mesh("radial")
    radial.tube(0.0, 0.0, 0.0, 0.46, 0.032, "detail", seg=10)
    for x in (-0.44, -0.16, 0.16, 0.44):
        m.merge(radial, rot_deg=(68.0, 0.0, 0.0), offset=(x, 0.16, 0.62))
    return m


def battery():
    """Drone battery (2056): a lithium traction pack. Shrink-wrapped cell block
    with a label panel, four charge-state lamps, two power leads into a keyed
    connector and a thin balance lead beside them."""
    m = Mesh("Drone Battery")

    # Cell block under shrink wrap, proud label panel, two wrap seams.
    m.cbox(0.0, 0.0, 0.0, 1.70, 0.62, 1.20, "receiver", c=0.05)
    m.cbox(-0.08, 0.34, 0.06, 1.16, 0.08, 0.76, "shroud", c=0.03)
    for z in (-0.54, 0.50):
        m.cbox(0.0, 0.32, z, 1.60, 0.06, 0.05, "detail", c=0.008)

    # Charge-state lamps low on the wrap.
    for i in range(4):
        m.tube(-0.54 + i * 0.30, -0.42, 0.30, 0.38, 0.055, "shroud", seg=12)

    # Power leads out of the top edge into the keyed connector.
    lead = Mesh("power lead")
    lead.tube(0.0, 0.0, 0.0, 0.46, 0.075, "detail", seg=12)
    for x in (-0.26, 0.26):
        m.merge(lead, rot_deg=(90.0, 0.0, 0.0), offset=(x, 0.12, 0.58))
    m.cbox(0.0, 0.12, 1.14, 0.46, 0.30, 0.26, "stock", c=0.02)

    # Balance lead and its small plug.
    ribbon = Mesh("balance lead")
    ribbon.tube(0.0, 0.0, 0.0, 0.34, 0.032, "detail", seg=10)
    m.merge(ribbon, rot_deg=(90.0, 0.0, 0.0), offset=(0.62, 0.12, 0.58))
    m.cbox(0.62, 0.12, 0.98, 0.24, 0.20, 0.14, "stock", c=0.012)
    return m


def survdrone():
    """Surveillance drone (2057), folded for the backpack: fuselage with an
    avionics hump, a nose gimbal ball, four arms swept out with their blades
    folded along them, and landing skids. The FPV drone is a small open frame
    with four guard rings; this one has no rings, a camera ball and legs."""
    m = Mesh("Surveillance Drone")

    # Fuselage and the avionics hump on top of it.
    m.cbox(0.0, 0.0, 0.0, 0.80, 0.46, 1.25, "receiver", c=0.05)
    m.cbox(0.0, 0.28, -0.14, 0.56, 0.24, 0.78, "receiver", c=0.035)

    # Nose boom and the gimbal ball: two cones and a barrel, built along Y and
    # swung upright so the camera looks out of the top of the icon.
    m.cbox(0.0, -0.10, 0.70, 0.26, 0.22, 0.32, "stock", c=0.02)
    ball = Mesh("gimbal")
    ball.cone(0.0, 0.0, -0.26, -0.10, 0.13, 0.27, "stock", seg=20)
    ball.tube(0.0, 0.0, -0.10, 0.10, 0.27, "stock", seg=20)
    ball.cone(0.0, 0.0, 0.10, 0.26, 0.27, 0.14, "stock", seg=20)
    ball.tube(0.0, 0.0, 0.24, 0.32, 0.13, "detail", seg=16)
    m.merge(ball, rot_deg=(90.0, 0.0, 0.0), offset=(0.0, -0.12, 0.94))

    # Four arms. Each is built straight along Z and turned into its quadrant, so
    # the motor, the hi-vis cap and the folded blade ride along with it.
    for x_s, z_s in ((1.0, 1.0), (-1.0, 1.0), (1.0, -1.0), (-1.0, -1.0)):
        ang = math.degrees(math.atan2(x_s * 0.78, z_s * 0.92))
        part = Mesh("arm")
        part.cbox(0.0, 0.0, 0.60, 0.13, 0.13, 1.20, "stock", c=0.018)
        part.tube(0.0, 1.16, 0.00, 0.34, 0.15, "detail", seg=18)
        part.tube(0.0, 1.16, 0.32, 0.40, 0.07, "stock", seg=12)
        part.cbox(0.0, 0.30, 1.16, 0.22, 0.07, 0.20, "shroud", c=0.012)
        part.cbox(0.0, 0.41, 1.16, 0.10, 0.04, 0.96, "shroud", c=0.006)
        m.merge(part, rot_deg=(0.0, ang, 0.0))

    # Landing skids under the fuselage.
    skid = Mesh("skid")
    skid.tube(0.0, 0.0, -0.46, 0.42, 0.05, "detail", seg=12)
    for x in (-0.30, 0.30):
        m.merge(skid, rot_deg=(90.0, 0.0, 0.0), offset=(x, -0.34, -0.02))
        m.cbox(x, -0.26, -0.02, 0.07, 0.24, 0.07, "detail", c=0.012)
    return m


# ------------------------------------------------------------------ textures

def paint(r, rgb, rough=0.05, wear=40, bright=0.13, length=30):
    out = T.base(r, H, H, rgb, rough, scale=8)
    return T.scratches(r, out, wear, bright, length)


def build_texture_antenna(name, seed):
    r = T.rng(seed)
    quads = {
        "receiver": paint(r, (128, 132, 128), rough=0.05, wear=70),   # alloy mast tubes
        "shroud":   paint(r, (88, 92, 96), rough=0.05, wear=58),      # foot plate, collars
        "stock":    paint(r, (84, 90, 64), rough=0.05, wear=50),      # olive carry frame
        "detail":   paint(r, (34, 34, 32), rough=0.06, wear=46),      # straps, radials, lugs
    }
    return save(name, quads, {
        "receiver": T.height_scratches(r, H, H, n=95, length=26),
        "shroud":   T.height_scratches(r, H, H, n=70, length=20),
        "stock":    T.height_scratches(r, H, H, n=60, length=22),
        "detail":   T.height_scratches(r, H, H, n=80, length=16),
    }, 2.2)


def build_texture_battery(name, seed):
    r = T.rng(seed)
    quads = {
        "receiver": paint(r, (38, 52, 96), rough=0.04, wear=30),      # blue shrink wrap
        "shroud":   paint(r, (176, 178, 182), rough=0.04, wear=34),   # label and lamps
        "stock":    paint(r, (190, 160, 40), rough=0.05, wear=40),    # keyed connector
        "detail":   paint(r, (26, 26, 28), rough=0.05, wear=44),      # leads and seams
    }
    return save(name, quads, {
        "receiver": T.height_scratches(r, H, H, n=40, length=18),
        "shroud":   T.height_scratches(r, H, H, n=30, length=14),
        "stock":    T.height_scratches(r, H, H, n=55, length=16),
        "detail":   T.height_scratches(r, H, H, n=65, length=16),
    }, 2.0)


def build_texture_survdrone(name, seed):
    r = T.rng(seed)
    quads = {
        "receiver": paint(r, (134, 136, 134), rough=0.05, wear=48),   # grey composite shell
        "shroud":   paint(r, (172, 88, 30), rough=0.05, wear=44),     # hi-vis blades and caps
        "stock":    paint(r, (50, 50, 54), rough=0.05, wear=60),      # carbon arms, gimbal
        "detail":   paint(r, (26, 26, 28), rough=0.05, wear=50),      # motors, lens, skids
    }
    return save(name, quads, {
        "receiver": T.height_scratches(r, H, H, n=55, length=20),
        "shroud":   T.height_scratches(r, H, H, n=45, length=16),
        "stock":    T.height_scratches(r, H, H, n=85, length=22),
        "detail":   T.height_scratches(r, H, H, n=70, length=16),
    }, 2.2)


def save(name, quads, heights, strength):
    diffuse = os.path.join(ASSETS, name + "_diffuse.png")
    T.save_atlas(quads, diffuse)
    T.save_height_atlas(heights, os.path.join(ASSETS, name + "_normal.png"),
                        strength=strength)
    return diffuse


# -------------------------------------------------------------------- driver

def build(mesh, name, tex_fn, seed, yaw, pitch, tilt=0.0):
    path = os.path.join(ASSETS, name + ".ndmesh")
    k = mesh.fit_box(REF_SIZE, REF_CENTER)
    mesh.write(path)
    (x0, x1), (y0, y1), (z0, z1) = mesh.bounds()
    tex = tex_fn(name, seed)
    iconlib.pack_icon(path, tex, os.path.join(ASSETS, name + "_icon.png"),
                      yaw=yaw, pitch=pitch, tilt=tilt)
    print("%-13s %5d tris, fit %.3f, ext %.3f x %.3f x %.3f"
          % (name, len(mesh.IDX) // 3, k, x1 - x0, y1 - y0, z1 - z0))


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    # The mast bundle is three times as tall as it is wide, so it is the one
    # that gets laid over the diagonal; the other two fill the square upright.
    build(antenna_pack(), "antenna_pack", build_texture_antenna, 2055,
          0.46, 0.26, tilt=38.0)
    build(battery(), "battery", build_texture_battery, 2056, 0.62, 0.30)
    build(survdrone(), "survdrone", build_texture_survdrone, 2057, 0.46, 0.30)
