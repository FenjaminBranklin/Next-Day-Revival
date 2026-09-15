"""Build the three vehicle modules their OWN art: a thermal imager, a
night-vision module and a large jamming module.

Items 2060, 2061 and 2062 (Revival.Modules.cs) shipped on borrowed placeholder
art - all three wore the portable jammer's model, textures and icon. Three
different pieces of hardware looked like three copies of the R-330 in the
backpack, and the icon was the only thing the player ever sees of a module he
has not installed yet. Each one gets its own procedural model, diffuse texture,
normal map and 300 px inventory icon here, so that the three read apart at a
glance:

    thermal    an armoured sensor box - fin comb on top, one big round
               germanium window, a control panel and a power connector
    nvmodule   an image intensifier lying across a mount - objective bell at
               one end, rubber eyecup at the other, power pack on top
    jammod     a turret transmitter block - horizontal heat-sink fins across
               the front and four flat blade antennas standing on top, which is
               exactly what the portable jammer's four round whips are not

All three clone donor 2030 like the placeholder did, so they are fitted to the
SAME reference box as ammo_mesh.py / jammer_mesh.py / repair_items_build.py
(magaz_l: X wide, Y thin, Z tall) and sit inside their cloned prefab's collider.

The icon camera looks at the +Y side (see iconlib.pack_icon), so every model
puts what identifies it - window, lens, fins, panel - on +Y.

Outputs (assets/): thermal.*, nvmodule.*, jammod.*.

C# side names these files in VehicleModules.RegisterItems; verify.py lists them.
Run through make_assets.py group "modules", or standalone:
    python vehicle_modules_build.py
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

# Donor 2030 reference, identical to ammo_mesh.py / jammer_mesh.py: magaz_l,
# X wide, Y thin, Z tall. fit_box pulls the finished model onto this box.
REF_SIZE = (0.364, 0.150, 0.370)
REF_CENTER = (-0.022, -0.001, -0.206)


# ------------------------------------------------------------------- meshes

def thermal():
    """Thermal imaging module (2060): an armoured sensor box. A comb of cooling
    fins along the top, one large round germanium window in a tapered shroud on
    the front, a small control panel with two buttons beside it, a power
    connector low on the front, and two mounting feet under the box."""
    m = Mesh("Thermal Imaging Module")

    # Armoured housing with a proud front plate, so the box does not read as a
    # plain slab at icon size.
    m.cbox(0.0, -0.08, -0.10, 1.80, 0.52, 1.30, "receiver", c=0.06)
    m.cbox(0.0, 0.20, -0.10, 1.62, 0.10, 1.14, "receiver", c=0.04)

    # The window: a tapered shroud around a dark germanium disc. A cone instead
    # of a ring keeps the part a closed body - no bore, nothing to wind wrong.
    m.cone(0.0, -0.10, 0.22, 0.40, 0.46, 0.34, "stock", seg=28)
    m.tube(0.0, -0.10, 0.38, 0.44, 0.33, "shroud", seg=28)

    # Cooling fin comb along the top edge.
    for i in range(7):
        m.cbox(-0.72 + i * 0.24, -0.08, 0.72, 0.09, 0.46, 0.30,
               "stock", c=0.014)

    # Control panel with two buttons, on the front beside the window.
    m.cbox(0.58, 0.24, 0.30, 0.42, 0.10, 0.24, "detail", c=0.012)
    for x in (0.48, 0.68):
        m.tube(x, 0.30, 0.26, 0.34, 0.05, "stock", seg=12)

    # Power connector and its knurled collar, low on the front.
    m.tube(-0.62, -0.50, 0.18, 0.36, 0.14, "detail", seg=16)
    m.cone(-0.62, -0.50, 0.34, 0.44, 0.14, 0.10, "detail", seg=16)

    # Mounting feet.
    for x in (-0.66, 0.66):
        m.cbox(x, -0.08, -0.82, 0.36, 0.44, 0.22, "stock", c=0.02)
    return m


def nvmodule():
    """Night-vision module (2061): an image intensifier lying ACROSS the piece
    on a dovetail mount - objective bell at one end, rubber eyecup at the other,
    a power pack with a switch knob and an indicator lamp on top. Nothing about
    the silhouette is a box, which is the whole point next to the other two.

    The tube is built along the kit's Y axis and swung about Z so it lies along
    X; that is the same trick ammo_mesh.py uses for a cartridge lying across a
    box."""
    m = Mesh("Night Vision Module")

    # Mount: base plate and the dovetail rail under it.
    m.cbox(0.0, -0.05, -0.62, 1.70, 0.50, 0.26, "stock", c=0.03)
    m.cbox(0.0, -0.05, -0.82, 1.30, 0.34, 0.16, "detail", c=0.02)
    # The posts reach UP INTO the tube (its lowest point is z -0.20): parts that
    # only touch look detached once fit_box has shrunk everything.
    for x in (-0.40, 0.40):
        m.cbox(x, -0.05, -0.36, 0.26, 0.30, 0.34, "stock", c=0.02)

    # The intensifier itself: body tube, objective bell with its green glass,
    # and the rubber eyecup. Built along Y, laid along X by the merge.
    optic = Mesh("intensifier")
    optic.tube(0.0, 0.0, -0.66, 0.62, 0.30, "receiver", seg=26)
    optic.cone(0.0, 0.0, 0.60, 0.80, 0.30, 0.38, "stock", seg=26)
    optic.tube(0.0, 0.0, 0.78, 0.86, 0.32, "shroud", seg=26)
    optic.cone(0.0, 0.0, -0.86, -0.64, 0.34, 0.28, "detail", seg=24)
    m.merge(optic, rot_deg=(0.0, 0.0, 90.0), offset=(0.0, -0.05, 0.10))

    # Power pack on top, with the switch knob and the indicator lamp on its
    # front face.
    m.cbox(0.30, -0.05, 0.58, 0.90, 0.40, 0.36, "receiver", c=0.03)
    m.tube(0.60, 0.60, 0.10, 0.34, 0.11, "stock", seg=16)
    m.tube(0.06, 0.60, 0.10, 0.28, 0.06, "shroud", seg=12)
    return m


def jammod():
    """Large jamming module (2062): a turret electronic-warfare block. The
    portable jammer is a frame with four ROUND whips; this one is a solid
    transmitter with horizontal heat-sink fins across its front face and four
    FLAT blade antennas standing on its roof, so the two never look alike even
    in the small backpack cell."""
    m = Mesh("Large Jamming Module")

    # Transmitter housing.
    m.cbox(0.0, 0.0, 0.0, 1.80, 0.55, 1.10, "receiver", c=0.06)

    # Heat-sink fins across the front - the horizontal banding is the module's
    # signature in the icon.
    for i in range(6):
        m.cbox(0.0, 0.34, -0.36 + i * 0.17, 1.56, 0.16, 0.09,
               "stock", c=0.014)

    # Four blade antennas on the roof, the inner pair taller, each on its own
    # insulator base.
    for x, high in zip((-0.60, -0.20, 0.20, 0.60), (0.46, 0.60, 0.60, 0.46)):
        m.cbox(x, 0.10, 0.60, 0.40, 0.14, 0.14, "detail", c=0.016)
        m.cbox(x, 0.10, 0.64 + high / 2.0, 0.30, 0.07, high, "stock", c=0.010)

    # Data plate and lifting eyes in hazard yellow, connectors and feet black.
    m.cbox(-0.52, 0.30, -0.46, 0.50, 0.08, 0.16, "shroud", c=0.010)
    for x in (-0.80, 0.80):
        m.cbox(x, 0.0, 0.60, 0.16, 0.22, 0.14, "shroud", c=0.014)
    for x in (0.48, 0.76):
        m.tube(x, -0.46, 0.26, 0.44, 0.12, "detail", seg=16)
    for x in (-0.62, 0.62):
        m.cbox(x, 0.0, -0.66, 0.40, 0.46, 0.24, "detail", c=0.02)
    return m


# ------------------------------------------------------------------ textures

def paint(r, rgb, rough=0.05, wear=40, bright=0.13, length=30):
    out = T.base(r, H, H, rgb, rough, scale=8)
    return T.scratches(r, out, wear, bright, length)


def build_texture_thermal(name, seed):
    r = T.rng(seed)
    quads = {
        "receiver": paint(r, (74, 80, 62), rough=0.05, wear=46),      # olive armour
        "shroud":   T.base(r, H, H, (104, 86, 42), 0.03, scale=6),    # germanium window
        "stock":    paint(r, (62, 64, 68), rough=0.05, wear=62),      # fins, shroud, feet
        "detail":   paint(r, (32, 33, 36), rough=0.05, wear=52),      # panel, connector
    }
    return save(name, quads, {
        "receiver": T.height_scratches(r, H, H, n=60, length=22),
        "shroud":   T.height_scratches(r, H, H, n=18, length=12),
        "stock":    T.height_scratches(r, H, H, n=90, length=20),
        "detail":   T.height_scratches(r, H, H, n=80, length=18),
    }, 2.2)


def build_texture_nv(name, seed):
    r = T.rng(seed)
    quads = {
        "receiver": paint(r, (44, 62, 48), rough=0.05, wear=44),      # dark green body
        "shroud":   T.base(r, H, H, (46, 118, 60), 0.035, scale=6),   # intensifier glass
        "stock":    paint(r, (70, 74, 72), rough=0.05, wear=58),      # mount, bell, knob
        "detail":   paint(r, (28, 29, 28), rough=0.06, wear=50),      # rubber, dovetail
    }
    return save(name, quads, {
        "receiver": T.height_scratches(r, H, H, n=55, length=20),
        "shroud":   T.height_scratches(r, H, H, n=14, length=10),
        "stock":    T.height_scratches(r, H, H, n=85, length=22),
        "detail":   T.height_scratches(r, H, H, n=70, length=16),
    }, 2.2)


def build_texture_jam(name, seed):
    r = T.rng(seed)
    quads = {
        "receiver": paint(r, (78, 86, 96), rough=0.05, wear=48),      # grey-blue armour
        "shroud":   paint(r, (178, 132, 34), rough=0.06, wear=54),    # hazard yellow plates
        "stock":    paint(r, (132, 136, 142), rough=0.05, wear=66),   # fins and blades
        "detail":   paint(r, (30, 31, 34), rough=0.05, wear=52),      # insulators, feet
    }
    return save(name, quads, {
        "receiver": T.height_scratches(r, H, H, n=65, length=22),
        "shroud":   T.height_scratches(r, H, H, n=50, length=16),
        "stock":    T.height_scratches(r, H, H, n=95, length=24),
        "detail":   T.height_scratches(r, H, H, n=75, length=18),
    }, 2.4)


def save(name, quads, heights, strength):
    diffuse = os.path.join(ASSETS, name + "_diffuse.png")
    T.save_atlas(quads, diffuse)
    T.save_height_atlas(heights, os.path.join(ASSETS, name + "_normal.png"),
                        strength=strength)
    return diffuse


# -------------------------------------------------------------------- driver

def build(mesh, name, tex_fn, seed, yaw, pitch):
    path = os.path.join(ASSETS, name + ".ndmesh")
    k = mesh.fit_box(REF_SIZE, REF_CENTER)
    mesh.write(path)
    (x0, x1), (y0, y1), (z0, z1) = mesh.bounds()
    tex = tex_fn(name, seed)
    iconlib.pack_icon(path, tex, os.path.join(ASSETS, name + "_icon.png"),
                      yaw=yaw, pitch=pitch)
    print("%-10s %5d tris, fit %.3f, ext %.3f x %.3f x %.3f"
          % (name, len(mesh.IDX) // 3, k, x1 - x0, y1 - y0, z1 - z0))


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    # yaw/pitch per model: a high yaw looks down onto the +Y side (window, fin
    # comb, blade antennas), a low one keeps the X-Z silhouette (the lying
    # intensifier tube) readable.
    build(thermal(), "thermal", build_texture_thermal, 2060, 0.82, 0.24)
    build(nvmodule(), "nvmodule", build_texture_nv, 2061, 0.50, 0.26)
    build(jammod(), "jammod", build_texture_jam, 2062, 0.88, 0.22)
