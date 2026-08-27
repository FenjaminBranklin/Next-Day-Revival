"""Erzeugt das Mesh der .50 (TAC-50) und schreibt es als .ndmesh.

Gleiche Werkstatt und dieselbe Ausrichtung wie beim MG42: der Pistolengriff
liegt auf y = 0.624 und die Laufachse auf z = 0.096, beides aus dem RPD-Mesh
gemessen. Damit greift die rechte Hand am Griff, ohne dass irgendein Anker
verschoben werden muesste - die Waffe wird ueber dieselben kopierten
WeaponTranformManager-Werte angelegt.

Massstab: 1 Einheit = 393.5 mm (aus der RPD hergeleitet, siehe mg42_mesh.py).

    TAC-50 real     Gesamtlaenge 1448 mm, Lauf 737 mm, 5 Schuss .50 BMG
    im Mesh         y -2.120 .. 1.559   =  3.68 Einheiten
    zum Vergleich   MG42  3.10 Einheiten, RPD 2.64 Einheiten

Die Waffe ist damit deutlich groesser als das MG42 - so gewollt.

Das Zielfernrohr steckt im Mesh, weil es zur Silhouette gehoert. Das
Bildschirm-Overlay beim Zielen ist davon unabhaengig und kommt aus scope50.py.
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ndmesh import Mesh

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

OUT = os.path.join(HIER, "assets", "sniper50.ndmesh")

MM = 1.0 / 393.5
GRIP_Y = 0.624            # Handposition, aus dem RPD-Mesh
BZ = 0.096                # Laufachse, aus dem RPD-Mesh
GRIP_D = 1080.0           # mm von der Muendung bis zur Griffmitte


def y(mm_from_muzzle):
    return GRIP_Y + (mm_from_muzzle - GRIP_D) * MM


m = Mesh("Sniper50")

# ------------------------------------------------------------ Muendungsbremse
# Der grosse Kasten vorn ist das auffaelligste Merkmal einer .50.
m.cbox(0.0, y(84), BZ, 0.116, 168 * MM, 0.104, "detail", c=0.016)
m.cbox(0.0, y(52), BZ + 0.062, 0.096, 90 * MM, 0.022, "detail", c=0.006)
m.tube(0.0, BZ, y(168), y(210), 0.052, "detail", seg=18)

# ------------------------------------------------------------------- Lauf
# Schwerer, geriffelter Lauf. Die Riefen liefert die Textur.
m.tube(0.0, BZ, y(210), y(620), 0.041, "shroud", seg=24)
m.tube(0.0, BZ, y(620), y(760), 0.048, "shroud", seg=24)

# Handschutzrohr um den hinteren Laufabschnitt
m.tube(0.0, BZ, y(430), y(770), 0.070, "shroud", seg=26)
m.cbox(0.0, y(600), BZ + 0.082, 0.048, 340 * MM, 0.026, "receiver", c=0.006)

# ---------------------------------------------------------------- Gehaeuse
m.ctaper(y(760), y(1160),
         (0.0, BZ - 0.006, 0.098, 0.168),
         (0.0, BZ - 0.014, 0.092, 0.152), "receiver", c=0.026)
# Verschluss und Kammerstengel rechts
m.cbox(0.0, y(880), BZ + 0.088, 0.062, 260 * MM, 0.036, "receiver", c=0.010)
m.angled_box(y(980), BZ + 0.086, 130 * MM, 0.022, 0.022, 62.0, "detail",
             x_c=0.070, c=0.005)
m.cbox(0.098, y(1002), BZ + 0.052, 0.030, 30 * MM, 0.030, "detail", c=0.008)

# Magazin, 5 Schuss .50 - ein tiefer Kasten unter dem Gehaeuse
m.cbox(0.0, y(900), BZ - 0.150, 0.062, 150 * MM, 0.200, "detail", c=0.012)

# ------------------------------------------------------------ Zielfernrohr
SZ = BZ + 0.208                                   # Hoehe der Rohrachse
m.tube(0.0, SZ, y(800), y(1090), 0.046, "detail", seg=22)
m.cone(0.0, SZ, y(760), y(800), 0.070, 0.046, "detail", seg=22)   # Objektiv
m.cone(0.0, SZ, y(1090), y(1120), 0.046, 0.062, "detail", seg=22)  # Okular
m.tube(0.0, SZ, y(1120), y(1160), 0.062, "detail", seg=22)
m.tube(0.0, SZ, y(930), y(970), 0.062, "detail", seg=22)           # Verstellturm
m.cbox(0.0, y(950), SZ + 0.070, 0.044, 40 * MM, 0.038, "detail", c=0.006)
# Montageringe
for d in (830, 1060):
    m.cbox(0.0, y(d), (SZ + BZ + 0.088) / 2.0, 0.040, 34 * MM, SZ - BZ - 0.060,
           "detail", c=0.006)

# ------------------------------------------------------- Griff und Abzug
m.angled_box(GRIP_Y, BZ - 0.176, 0.310, 0.064, 0.052, 14.0, "stock", c=0.018)
m.cbox(0.0, y(1020), BZ - 0.112, 0.026, 110 * MM, 0.018, "detail", c=0.005)
m.cbox(0.0, y(986), BZ - 0.076, 0.022, 26 * MM, 0.052, "detail", c=0.005)

# ----------------------------------------------------------------- Schaft
m.ctaper(y(1160), y(1300),
         (0.0, BZ - 0.014, 0.086, 0.152),
         (0.0, BZ - 0.020, 0.072, 0.130), "stock", c=0.022)
m.ctaper(y(1300), y(1420),
         (0.0, BZ - 0.020, 0.072, 0.130),
         (0.0, BZ - 0.030, 0.084, 0.166), "stock", c=0.022)
m.cbox(0.0, y(1434), BZ - 0.030, 0.090, 28 * MM, 0.178, "detail", c=0.022)
# Wangenauflage
m.cbox(0.0, y(1250), BZ + 0.086, 0.062, 200 * MM, 0.044, "stock", c=0.012)

# ---------------------------------------------------------------- Zweibein
# Angeklappt am Handschutz, wie beim MG42 - sonst wird die Silhouette zu breit.
for side in (-1, 1):
    m.angled_box(y(400), BZ - 0.062, 340 * MM, 0.026, 0.026, 90.0, "detail",
                 x_c=side * 0.052, c=0.005)
    m.cbox(side * 0.052, y(246), BZ - 0.070, 0.028, 36 * MM, 0.028, "detail", c=0.006)

# ---------------------------------------------------------------- schreiben
m.write(OUT)
m.report(OUT)
(x0, x1), (yy0, yy1), (z0, z1) = m.bounds()
print()
print("  Bezugspunkte")
print("    Muendung        y %.3f" % y(0))
print("    Griffmitte      y %.3f   (RPD 0.624, MG42 0.624)" % GRIP_Y)
print("    Laufachse       z %.3f   (RPD 0.096)" % BZ)
print("    Zielfernrohr    z %.3f" % SZ)
print("    Gesamtlaenge    %.0f mm  (TAC-50 real 1448 mm, MG42 1220 mm)"
      % ((yy1 - yy0) / MM))
print("    Breite          %.0f mm  (MG42 83 mm, RPD 54 mm)" % ((x1 - x0) / MM))
