"""Erzeugt die Meshes der beiden neuen Munitionsitems.

    mgbelt.ndmesh    MG-Gurt 200 Schuss 7,62  - Gurtkasten mit heraushaengendem Gurt
    ammo50.ndmesh    .50-BMG-Kiste 10 Schuss  - Munitionskiste mit drei Patronen

GROESSE UND LAGE: WARUM AN magaz_l AUSGERICHTET
-----------------------------------------------
Die Loot-Prefabs stehen anders im Raum als die Waffenmodelle. Aus
resources.assets abgelesen:

    1023_Spawn (RPD)     Wurzel scale 1.0, Mesh RPD     extent (0.068, 1.318, 0.244)
    2030_Spawn (7,62)    Wurzel scale 1.0, Mesh magaz_l extent (0.182, 0.075, 0.185)
                         Mittelpunkt des Meshes (-0.022, -0.001, -0.206)

Das Plugin tauscht am geklonten Prefab nur das Mesh aus; Wurzelskalierung,
Collider und Drehung bleiben die des Originals. Ein Mesh mit anderer Ausdehnung
oder anderem Mittelpunkt liegt deshalb sichtbar neben seinem Collider. Beide
Kisten werden hier also so gebaut, dass ihre duenne Achse - wie bei magaz_l -
die Y-Achse ist, und dann per fit_box auf die Groesse der Vorlage gezogen.
Der Gurt darf etwas groesser sein, er fasst doppelt so viel.
"""

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ndmesh import Mesh

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")

# Vorlage: magaz_l, das Mesh der 7,62-Kiste. X breit, Y duenn, Z hoch.
REF_SIZE = (0.364, 0.150, 0.370)
REF_CENTER = (-0.022, -0.001, -0.206)


def cartridge(length, r_case, r_bullet, region_brass="stock", region_tip="shroud"):
    """Eine Patrone, laengs entlang +Y, Boden bei y = 0."""
    m = Mesh("cartridge")
    neck = length * 0.62
    shoulder = length * 0.70
    m.tube(0.0, 0.0, -0.05 * length, 0.0, r_case * 1.09, region_brass, seg=14)
    m.tube(0.0, 0.0, 0.0, neck, r_case, region_brass, seg=14)
    m.cone(0.0, 0.0, neck, shoulder, r_case, r_bullet, region_brass, seg=14)
    m.tube(0.0, 0.0, shoulder, length * 0.80, r_bullet, region_brass, seg=14)
    m.cone(0.0, 0.0, length * 0.80, length, r_bullet, r_bullet * 0.28,
           region_tip, seg=14)
    return m


def lay_across(target, length, r_case, r_bullet, x_center, y, z):
    """Legt eine Patrone quer (Laengsachse X) an die angegebene Stelle."""
    c = cartridge(length, r_case, r_bullet)
    target.merge(c, rot_deg=(0.0, 0.0, 90.0),
                 offset=(x_center - length / 2.0, y, z))


def belt():
    """Gurtkasten mit einem Gurt, der ueber die Vorderkante haengt.

    Der Kasten liegt mit der langen Achse in X und der duennen in Y - dieselbe
    Lage wie das Vorlagenmesh magaz_l.
    """
    m = Mesh("MGBelt")

    # Kasten, Deckel, Scharnier, Verschluss
    m.cbox(0.0, 0.0, 0.0, 1.00, 0.46, 0.60, "receiver", c=0.030)
    m.cbox(0.0, 0.0, 0.325, 1.06, 0.50, 0.06, "detail", c=0.012)
    m.cbox(0.0, 0.23, 0.300, 0.62, 0.06, 0.05, "detail", c=0.010)
    m.cbox(0.0, -0.23, 0.180, 0.20, 0.05, 0.18, "detail", c=0.010)

    # Tragebuegel quer ueber den Deckel
    m.cbox(-0.22, 0.0, 0.470, 0.05, 0.05, 0.24, "detail", c=0.008)
    m.cbox(0.22, 0.0, 0.470, 0.05, 0.05, 0.24, "detail", c=0.008)
    m.cbox(0.0, 0.0, 0.580, 0.50, 0.05, 0.05, "detail", c=0.008)

    # Standfuesse
    for sx in (-0.40, 0.40):
        m.cbox(sx, 0.0, -0.315, 0.10, 0.42, 0.04, "detail", c=0.008)

    # ------------------------------------------------------------- der Gurt
    # Viertelellipse: startet waagerecht auf dem Deckel, endet senkrecht vor
    # der Kiste. Zehn Patronen mit Gurtgliedern dazwischen.
    n = 10
    for i in range(n):
        t = i / float(n - 1)
        ang = t * (math.pi / 2.0)
        by = -0.20 - 0.20 * math.sin(ang)
        bz = 0.38 - 0.72 * (1.0 - math.cos(ang))
        lay_across(m, 0.26, 0.032, 0.020, 0.0, by, bz)
        link = Mesh("link")
        link.cbox(0.0, 0.0, 0.0, 0.055, 0.075, 0.075, "shroud", c=0.010)
        m.merge(link, rot_deg=(-t * 90.0, 0.0, 0.0), offset=(-0.10, by, bz))
        m.merge(link, rot_deg=(-t * 90.0, 0.0, 0.0), offset=(0.10, by, bz))
    return m


def crate50():
    """Munitionskiste .50 BMG mit drei Patronen auf dem Deckel."""
    m = Mesh("Ammo50")

    m.cbox(0.0, 0.0, 0.0, 1.00, 0.52, 0.46, "receiver", c=0.026)
    m.cbox(0.0, 0.0, 0.255, 1.06, 0.56, 0.06, "detail", c=0.012)
    m.cbox(0.0, 0.26, 0.230, 0.68, 0.06, 0.05, "detail", c=0.010)
    m.cbox(0.0, -0.26, 0.120, 0.22, 0.05, 0.20, "detail", c=0.010)

    # Tragegriff laengs auf dem Deckel
    m.cbox(0.0, 0.0, 0.360, 0.44, 0.07, 0.05, "detail", c=0.010)
    m.cbox(0.20, 0.0, 0.310, 0.05, 0.07, 0.11, "detail", c=0.008)
    m.cbox(-0.20, 0.0, 0.310, 0.05, 0.07, 0.11, "detail", c=0.008)

    for sx in (-0.42, 0.42):
        m.cbox(sx, 0.0, -0.245, 0.10, 0.48, 0.04, "detail", c=0.008)

    # Drei .50er quer auf dem Deckel - der Groessenvergleich macht das Item
    # sofort erkennbar.
    for sy in (-0.16, 0.0, 0.16):
        lay_across(m, 0.46, 0.050, 0.030, 0.0, sy, 0.330)
    return m


def build(mesh, name, factor):
    path = os.path.join(ASSETS, name + ".ndmesh")
    size = tuple(REF_SIZE[i] * factor for i in range(3))
    k = mesh.fit_box(size, REF_CENTER)
    mesh.write(path)
    mesh.report(path)
    (x0, x1), (y0, y1), (z0, z1) = mesh.bounds()
    print("    Skalierung   Faktor %.4f auf %.0f %% der Vorlage" % (k, factor * 100))
    print("    Ausdehnung   %.3f x %.3f x %.3f   (magaz_l %.3f x %.3f x %.3f)"
          % (x1 - x0, y1 - y0, z1 - z0, REF_SIZE[0], REF_SIZE[1], REF_SIZE[2]))
    print("    Mittelpunkt  (%.3f, %.3f, %.3f)   (magaz_l %.3f, %.3f, %.3f)"
          % ((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2,
             REF_CENTER[0], REF_CENTER[1], REF_CENTER[2]))
    print()


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    build(belt(), "mgbelt", 1.25)
    build(crate50(), "ammo50", 1.15)
