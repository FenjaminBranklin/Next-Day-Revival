"""Erzeugt das Modell der ausgefahrenen M72 LAW.

Die Masse folgen dem Auftrag: 890 mm Laenge, 66 mm Aussendurchmesser und
51 mm Innenrohr. Die Laufachse und das Abzugsgehaeuse liegen an den am RPD
gemessenen Bezugspunkten, damit die Spender-Animation die Hand nicht neben
die Waffe setzt.
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ndmesh import Mesh

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
OUT = os.path.join(ASSETS, "law.ndmesh")

MM = 1.0 / 393.5
BZ = 0.096
GRIP_Y = 0.624
Y_REAR = -0.962
Y_MUZZLE = Y_REAR + 890.0 * MM


def pin_x(length, radius, region, seg=16):
    """Kleiner Zylinder, der nach dem Merge entlang X liegt."""
    p = Mesh("pin")
    p.tube(0.0, 0.0, -length / 2.0, length / 2.0, radius, region, seg=seg)
    return p


def add_screw(m, x, y, z, radius=0.012):
    m.merge(pin_x(0.018, radius, "detail", 14), rot_deg=(0.0, 0.0, 90.0),
            offset=(x, y, z))


def build():
    m = Mesh("M72 LAW")

    # Zwei deutlich getrennte Teleskoprohre. Das hintere Rohr ist das breite
    # Glasfasergehaeuse, das vordere Rohr ist ausgezogen und schlanker.
    m.tube(0.0, BZ, Y_REAR + 0.030, 0.335, 0.084, "shroud", seg=40)
    m.tube(0.0, BZ, 0.270, Y_MUZZLE - 0.035, 0.071, "receiver", seg=40)

    # Ueberlappung, Endkappen und Gummiringe erzeugen die typische gestufte
    # Silhouette. Mehrere schmale Ringe vermeiden den Eindruck eines Rohblocks.
    for yy, rr, width, region in (
            (Y_REAR + 0.022, 0.095, 0.044, "stock"),
            (Y_REAR + 0.080, 0.090, 0.030, "detail"),
            (0.285, 0.090, 0.035, "stock"),
            (0.340, 0.086, 0.026, "detail"),
            (Y_MUZZLE - 0.045, 0.080, 0.045, "stock"),
            (Y_MUZZLE - 0.006, 0.077, 0.028, "detail")):
        m.tube(0.0, BZ, yy - width / 2.0, yy + width / 2.0, rr, region, seg=40)

    # Leicht vertiefte Muendung mit dunklem Innenring.
    m.cone(0.0, BZ, Y_MUZZLE - 0.020, Y_MUZZLE + 0.012,
           0.064, 0.056, "stock", seg=40)

    # Abzugs- und Zuendgehaeuse an der RPD-Handposition. Es umgreift das Rohr
    # von unten und gibt der rechten Hand eine echte, gefaste Kontaktflaeche.
    m.cbox(0.0, GRIP_Y, 0.016, 0.118, 0.190, 0.205, "receiver", c=0.014)
    m.cbox(0.0, GRIP_Y + 0.014, -0.095, 0.094, 0.135, 0.100,
           "stock", c=0.014)
    m.cbox(0.0, GRIP_Y - 0.078, 0.205, 0.112, 0.055, 0.090,
           "detail", c=0.010)

    # Abzugsbuegel: zwei schraye Wangen und ein gerundeter Boden.
    m.angled_box(GRIP_Y + 0.005, -0.070, 0.145, 0.025, 0.024,
                  24.0, "detail", x_c=-0.052, c=0.006)
    m.angled_box(GRIP_Y + 0.005, -0.070, 0.145, 0.025, 0.024,
                  24.0, "detail", x_c=0.052, c=0.006)
    m.cbox(0.0, GRIP_Y + 0.052, -0.142, 0.125, 0.030, 0.025,
           "detail", c=0.006)
    m.cbox(0.0, GRIP_Y + 0.012, -0.087, 0.050, 0.028, 0.075,
           "receiver", c=0.007)

    # Sicherungshebel und roter Sicherungsstift auf der linken Seite.
    m.cbox(-0.075, GRIP_Y - 0.030, 0.105, 0.030, 0.115, 0.032,
           "detail", c=0.006)
    m.merge(pin_x(0.175, 0.016, "stock", 18), rot_deg=(0.0, 0.0, 90.0),
            offset=(-0.005, GRIP_Y + 0.055, 0.184))
    m.cbox(-0.110, GRIP_Y + 0.055, 0.184, 0.045, 0.025, 0.045,
           "stock", c=0.007)

    # Vordere Klappvisierung: Sockel, zwei seitliche Streben, Schutzbuegel und
    # Korn. Durch die getrennten Teile bleibt das Visier auch im Icon lesbar.
    fy = Y_MUZZLE - 0.285
    m.cbox(0.0, fy, 0.188, 0.130, 0.105, 0.055, "detail", c=0.008)
    m.angled_box(fy, 0.258, 0.150, 0.025, 0.022, 0.0,
                  "detail", x_c=-0.052, c=0.005)
    m.angled_box(fy, 0.258, 0.150, 0.025, 0.022, 0.0,
                  "detail", x_c=0.052, c=0.005)
    m.cbox(0.0, fy, 0.334, 0.130, 0.028, 0.028, "detail", c=0.006)
    m.cbox(0.0, fy, 0.287, 0.018, 0.020, 0.090, "receiver", c=0.004)
    m.cbox(0.0, fy, 0.343, 0.035, 0.022, 0.035, "receiver", c=0.005)

    # Hinteres Klappvisier mit Leiter, Seitenwangen und Lochkimme.
    ry = -0.315
    m.cbox(0.0, ry, 0.187, 0.140, 0.115, 0.055, "detail", c=0.008)
    for xx in (-0.055, 0.055):
        m.angled_box(ry, 0.270, 0.165, 0.024, 0.022, 0.0,
                      "detail", x_c=xx, c=0.005)
    for zz in (0.228, 0.274, 0.320):
        m.cbox(0.0, ry, zz, 0.125, 0.023, 0.018, "detail", c=0.004)
    m.tube(0.0, 0.350, ry - 0.016, ry + 0.016, 0.032,
           "receiver", seg=24)
    m.tube(0.0, 0.350, ry - 0.018, ry + 0.018, 0.014,
           "stock", seg=20)

    # Schulterauflage am hinteren Ende: zwei Streben und eine gefaste Platte.
    m.angled_box(Y_REAR + 0.090, -0.020, 0.235, 0.030, 0.026,
                  12.0, "detail", x_c=-0.055, c=0.006)
    m.angled_box(Y_REAR + 0.090, -0.020, 0.235, 0.030, 0.026,
                  12.0, "detail", x_c=0.055, c=0.006)
    m.cbox(0.0, Y_REAR + 0.012, -0.088, 0.205, 0.050, 0.118,
           "stock", c=0.014)

    # Trageriemen-Oesen als offene, gefaste Buegel.
    for yy in (Y_REAR + 0.250, Y_MUZZLE - 0.430):
        m.cbox(-0.110, yy, 0.100, 0.024, 0.105, 0.030,
               "detail", c=0.005)
        m.angled_box(yy - 0.040, 0.050, 0.110, 0.022, 0.022,
                      60.0, "detail", x_c=-0.105, c=0.005)
        m.angled_box(yy + 0.040, 0.050, 0.110, 0.022, 0.022,
                      -60.0, "detail", x_c=-0.105, c=0.005)

    # Seitliche Warnschild-Unterlage und kleine Schraubkoepfe. Die eigentliche
    # Schablonenschrift kommt aus der Textur.
    m.cbox(0.087, -0.180, 0.100, 0.018, 0.500, 0.115,
           "shroud", c=0.005)
    for yy in (-0.405, -0.205, -0.005, 0.195, 0.455, 0.785):
        add_screw(m, 0.091, yy, 0.102, 0.010)
        add_screw(m, -0.091, yy, 0.102, 0.010)

    # Kleine Bedienplatte oben auf dem hinteren Rohr.
    m.cbox(0.0, 0.020, 0.202, 0.125, 0.260, 0.050,
           "receiver", c=0.009)
    m.cbox(0.0, 0.055, 0.239, 0.065, 0.085, 0.030,
           "stock", c=0.006)

    # Das Spender-Skelett richtet die Laengsachse im Spiel entgegengesetzt
    # zur Generatoransicht aus. Um 180 Grad um den Griff drehen, damit das
    # lange hintere Rohr zur Schulter und die Muendung vom Spieler weg zeigt.
    m.V = [(-p[0], 2.0 * GRIP_Y - p[1], p[2]) for p in m.V]
    m.N = [(-n[0], -n[1], n[2]) for n in m.N]

    # Beim Tragen schneidet die Schulterkamera stellenweise in das lange Rohr.
    # Die Spielmaterialien cullen Rueckseiten; dann sah die kameranahe Rohrwand
    # durchsichtig oder aufgebrochen aus. Eine zweite, nach innen gerichtete
    # Flaechenschicht macht das Geraet aus jeder Trageperspektive geschlossen.
    base = len(m.V)
    front_vertices = list(m.V)
    front_normals = list(m.N)
    front_uvs = list(m.T)
    front_indices = list(m.IDX)
    m.V.extend(front_vertices)
    m.N.extend([(-n[0], -n[1], -n[2]) for n in front_normals])
    m.T.extend(front_uvs)
    for i in range(0, len(front_indices), 3):
        m.IDX.extend((front_indices[i] + base,
                      front_indices[i + 2] + base,
                      front_indices[i + 1] + base))

    return m


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    mesh = build()
    mesh.write(OUT)
    mesh.report(OUT)
    print("  Soll-Laenge: %.3f Einheiten (890 mm)" % (890.0 * MM))
    print("  Griffmitte : y %.3f" % GRIP_Y)
