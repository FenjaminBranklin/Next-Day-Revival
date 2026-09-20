"""Build the parachute item (2067) its own art: mesh, textures and icon.

Revival.Parachute.cs gives a man who jumps out of a helicopter the GAME's own
canopy - "PlayerDataPrefabs/Other/Parachute_Pref", the one the humanitarian aid
crates hang from, animated and hung from the neck bone by the game itself. That
model is the thing in the AIR, and nothing here touches it.

What is missing is the thing in the BACKPACK: a parachute that is packed. This
script builds that - a D-6 in its container, the way it looks strapped to a
frame and waiting:

    container   the pack tray, four closing flaps meeting in a cross, the seam
                and the grommets along it
    harness     two shoulder straps over the top onto the front, a chest strap
                with its buckle, two leg straps under the tray
    hardware    the riser covers at the top, the ripcord housing down the right
                edge and the D-ring handle at the end of it
    reserve     the flat reserve container across the bottom

Like every carried, non-weapon item of this plugin it clones donor 2030, so it
is fitted to the SAME reference box as ammo_mesh.py / jammer_mesh.py /
drone_gear_build.py (magaz_l: X wide, Y thin, Z tall) and sits inside its cloned
prefab's collider. +Y is the side the icon looks at, so the flaps, the seam and
the D-ring are all on +Y; the harness is on -Y, where it would be against a
man's back.

Outputs (assets/): parachute.ndmesh, parachute_diffuse.png,
parachute_normal.png, parachute_icon.png. C# side names these files in
Parachute.AddItems; verify.py lists them. Run through make_assets.py group
"parachute", or standalone:
    python parachute_build.py
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

# The magazine reference box every carried item of this plugin is fitted to.
REF_SIZE = (0.364, 0.150, 0.370)
REF_CENTER = (-0.022, -0.001, -0.206)


# -------------------------------------------------------------------- mesh

def parachute():
    """The D-6 in its container. Built straight in the reference frame - X wide,
    Y thin, Z tall - so nothing has to be swung about X the way the mast bundle
    and the extinguisher are."""
    m = Mesh("Parachute Pack")

    # --- the container itself: the tray, and the pack of canopy inside it that
    # makes the front stand proud of the tray's rim.
    m.cbox(0.0, -0.06, 0.06, 1.46, 0.62, 1.66, "receiver", c=0.05)
    m.cbox(0.0, 0.22, 0.06, 1.30, 0.34, 1.50, "shroud", c=0.05)

    # --- four closing flaps meeting in a cross on the front. Top and bottom
    # reach across, the two side flaps fold in under them, which is why they are
    # shorter and sit a shade deeper.
    m.cbox(0.0, 0.40, 0.56, 1.24, 0.09, 0.52, "shroud", c=0.02)
    m.cbox(0.0, 0.40, -0.44, 1.24, 0.09, 0.52, "shroud", c=0.02)
    for x in (-0.46, 0.46):
        m.cbox(x, 0.36, 0.06, 0.40, 0.09, 1.34, "shroud", c=0.02)

    # --- the seam where all four meet, and the grommets the closing pins go
    # through: one every third of the way along the upright bar.
    m.cbox(0.0, 0.46, 0.06, 0.09, 0.05, 1.46, "detail", c=0.008)
    m.cbox(0.0, 0.46, 0.06, 1.20, 0.05, 0.09, "detail", c=0.008)
    for z in (-0.44, 0.06, 0.56):
        m.tube(0.0, z, 0.46, 0.60, 0.085, "shroud", seg=12)
        m.tube(0.0, z, 0.54, 0.62, 0.045, "detail", seg=10)

    # --- harness. Two shoulder straps come over the top of the tray and run
    # down the back; each is three segments, because a strap that bends has to
    # be built out of pieces the kit can make.
    for x in (-0.44, 0.44):
        m.cbox(x, 0.20, 0.92, 0.26, 0.60, 0.16, "stock", c=0.012)   # over the top
        m.cbox(x, -0.42, 0.60, 0.26, 0.14, 0.62, "stock", c=0.012)  # down the back
        m.cbox(x, -0.42, -0.42, 0.26, 0.14, 1.42, "stock", c=0.012)

    # --- chest strap across the back of the pack, with its buckle in the middle.
    m.cbox(0.0, -0.44, 0.30, 1.10, 0.12, 0.20, "stock", c=0.012)
    m.cbox(0.0, -0.54, 0.30, 0.26, 0.14, 0.26, "detail", c=0.012)

    # --- two leg straps looping out under the tray.
    for x in (-0.40, 0.40):
        m.cbox(x, -0.30, -0.98, 0.24, 0.42, 0.18, "stock", c=0.012)
        m.cbox(x, -0.46, -0.86, 0.24, 0.14, 0.34, "stock", c=0.012)

    # --- riser covers along the top edge, where the lift webs leave the pack.
    for x in (-0.36, 0.36):
        m.cbox(x, 0.30, 0.84, 0.36, 0.30, 0.26, "receiver", c=0.02)
        m.cbox(x, 0.30, 0.98, 0.24, 0.22, 0.10, "detail", c=0.008)

    # --- ripcord: the housing down the right edge and the D-ring at its end.
    m.tube(0.66, 0.06, 0.30, 0.46, 0.055, "detail", seg=12)
    ring = Mesh("ripcord housing")
    ring.tube(0.0, 0.0, -0.62, 0.62, 0.05, "detail", seg=12)
    m.merge(ring, rot_deg=(90.0, 0.0, 0.0), offset=(0.66, 0.40, 0.40))
    # The D-ring itself: a crown lying in the X/Z plane, so it reads as a ring
    # rather than a peg. It is the one part a player looks for on the icon.
    handle = Mesh("D-ring")
    handle.crown(0.0, 0.0, 0.06, -0.06, 0.24, 0.15, "detail", seg=18)
    m.merge(handle, rot_deg=(90.0, 0.0, 0.0), offset=(0.74, 0.56, -0.36))

    # --- stencil panel: the flat plate the unit and pack date are printed on.
    m.cbox(-0.34, 0.47, -0.62, 0.44, 0.04, 0.24, "receiver", c=0.008)
    return m


# ----------------------------------------------------------------- texture

def paint(r, rgb, rough=0.05, wear=40, bright=0.13, length=30):
    out = T.base(r, H, H, rgb, rough, scale=8)
    return T.scratches(r, out, wear, bright, length)


def build_texture(name, seed):
    r = T.rng(seed)
    quads = {
        # Olive pack canvas, and the paler, more faded canvas of the flaps that
        # have been out in the weather; khaki webbing; dark hardware.
        "receiver": paint(r, (72, 80, 58), rough=0.07, wear=64),
        "shroud":   paint(r, (96, 102, 78), rough=0.07, wear=72),
        "stock":    paint(r, (122, 112, 82), rough=0.06, wear=56),
        "detail":   paint(r, (48, 48, 46), rough=0.05, wear=50),
    }
    return save(name, quads, {
        # Canvas is a weave, so it gets many short marks; the webbing fewer and
        # longer ones along its own run; the metal the least.
        "receiver": T.height_scratches(r, H, H, n=120, length=14),
        "shroud":   T.height_scratches(r, H, H, n=130, length=12),
        "stock":    T.height_scratches(r, H, H, n=70, length=26),
        "detail":   T.height_scratches(r, H, H, n=50, length=16),
    }, 2.4)


def save(name, quads, heights, strength):
    diffuse = os.path.join(ASSETS, name + "_diffuse.png")
    T.save_atlas(quads, diffuse)
    T.save_height_atlas(heights, os.path.join(ASSETS, name + "_normal.png"),
                        strength=strength)
    return diffuse


# ------------------------------------------------------------------ driver

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
    # Taller than it is wide, like the mast bundle, so it is laid over the
    # diagonal of the square icon rather than standing in the middle of it.
    build(parachute(), "parachute", build_texture, 2067, 0.52, 0.28, tilt=28.0)
