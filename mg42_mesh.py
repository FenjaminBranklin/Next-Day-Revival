"""Erzeugt das MG42-Mesh und schreibt es als .ndmesh.

Der Baukasten steckt in ndmesh.py; hier steht nur noch das Modell.

AUSRICHTUNG AN DER RPD - warum das die Handhaltung repariert
------------------------------------------------------------
Die Waffe wird beim Anlegen ueber WeaponTranformManager::ApplyLocalTransformData
positioniert. Das Plugin uebernimmt diese Werte von der RPD, also sitzt der
Mesh-Ursprung meiner Waffe exakt dort, wo er bei der RPD sitzt - und damit
liegt auch die rechte Hand relativ zum Mesh an derselben Stelle wie bei der RPD.

Aus dem RPD-Mesh gemessen (research/mesh_dump.py):

    Laufachse        x = 0.000, z = 0.096      (MuzzleShoot-Anker: 0.097)
    Pistolengriff    y 0.555 .. 0.692, Mitte y = 0.624, z -0.084 .. -0.239
    Abzug            y 0.40 .. 0.50, x +-0.013
    Gesamtlaenge     y -1.314 .. 1.321  =  2.635 Einheiten fuer 1037 mm RPD
                     also 1 Einheit = 393.5 mm

Das bisherige MG42-Mesh hatte den Griff bei y = 0.44. Das sind 0.184 Einheiten
oder 72 mm vor der Stelle, an der die Hand zugreift - die Hand stand also
sichtbar neben dem Griff. Hier wird der Griff auf y = 0.624 gesetzt und alles
andere in echten MG42-Massen darum herum gebaut.

Zweite Aenderung: das Zweibein liegt jetzt angeklappt am Laufmantel statt
aufgestellt. Aufgestellt zog es die Silhouette auf x +-0.19 auseinander - fast
das Dreifache der RPD-Breite (x -0.055 .. 0.081). Getragen wird ein MG42 mit
eingeklapptem Zweibein.
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ndmesh import Mesh

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

OUT = os.path.join(HIER, "assets", "mg42.ndmesh")

# ------------------------------------------------- Massstab und Bezugspunkte
MM = 1.0 / 393.5          # eine Einheit sind 393.5 mm (aus der RPD gemessen)
GRIP_Y = 0.624            # Griffmitte = Handposition, aus dem RPD-Mesh
BZ = 0.096                # Hoehe der Laufachse, aus dem RPD-Mesh
GRIP_D = 855.0            # mm von der Muendung bis zur Griffmitte (MG42)


def y(mm_from_muzzle):
    """Laengsposition aus dem Abstand zur Muendung in Millimetern."""
    return GRIP_Y + (mm_from_muzzle - GRIP_D) * MM


m = Mesh("MG42")

# ---------------------------------------------------------------- Muendung
# Muendungsbooster: kegeliger Trichter, davor die Lauf-Mutter.
Y_MUZZLE = y(0)
m.tube(0.0, BZ, y(0), y(28), 0.052, "detail", seg=20)
m.cone(0.0, BZ, y(28), y(96), 0.052, 0.036, "detail", seg=20)
m.tube(0.0, BZ, y(96), y(150), 0.030, "detail", seg=16)

# ------------------------------------------------------------- Laufmantel
# Rundes Rohr mit weichen Normalen; die Kuehlbohrungen liefert die Textur.
m.tube(0.0, BZ, y(150), y(620), 0.082, "shroud", seg=28)
m.tube(0.0, BZ, y(150), y(196), 0.094, "detail", seg=24)      # vorderer Ring
m.tube(0.0, BZ, y(576), y(620), 0.094, "detail", seg=24)      # hinterer Ring

# Gasrohr unter dem Mantel
m.cbox(0.0, y(400), BZ - 0.098, 0.030, 400 * MM, 0.036, "detail", c=0.008)

# ---------------------------------------------------------------- Gehaeuse
# Kastenfoermig, nach hinten leicht schmaler - die MG42-Blechpresse.
m.ctaper(y(620), y(1010),
         (0.0, BZ - 0.020, 0.128, 0.226),
         (0.0, BZ - 0.034, 0.116, 0.196), "receiver", c=0.034)

# Gurtdeckel oben mit dem charakteristischen Scharnier
m.ctaper(y(640), y(940),
         (0.0, BZ + 0.116, 0.120, 0.046),
         (0.0, BZ + 0.108, 0.114, 0.042), "receiver", c=0.014)
m.cbox(0.0, y(660), BZ + 0.140, 0.052, 40 * MM, 0.018, "detail", c=0.005)

# Gurtkasten links am Gehaeuse (in -x, damit die rechte Hand frei bleibt)
m.cbox(-0.092, y(760), BZ - 0.046, 0.048, 280 * MM, 0.140, "detail", c=0.020)
m.cbox(-0.092, y(640), BZ - 0.046, 0.036, 30 * MM, 0.100, "detail", c=0.008)

# --------------------------------------------------------- Griff und Abzug
# Griffmitte auf GRIP_Y - das ist der Bezugspunkt der ganzen Datei.
m.angled_box(GRIP_Y, BZ - 0.170, 0.300, 0.062, 0.052, 16.0, "stock", c=0.018)
# Abzugsbuegel, so schmal wie bei der RPD (x +-0.013)
m.cbox(0.0, y(800), BZ - 0.106, 0.026, 90 * MM, 0.016, "detail", c=0.005)
m.cbox(0.0, y(772), BZ - 0.072, 0.022, 26 * MM, 0.048, "detail", c=0.005)

# ----------------------------------------------------------------- Schaft
# MG42-Schaft: schmaler Hals aus dem Gehaeuse, dann die breite Kappe.
m.ctaper(y(1010), y(1120),
         (0.0, BZ - 0.034, 0.104, 0.164),
         (0.0, BZ - 0.052, 0.078, 0.112), "stock", c=0.026)
m.ctaper(y(1120), y(1200),
         (0.0, BZ - 0.052, 0.078, 0.112),
         (0.0, BZ - 0.070, 0.092, 0.168), "stock", c=0.026)
m.cbox(0.0, y(1210), BZ - 0.070, 0.098, 20 * MM, 0.180, "detail", c=0.024)

# ------------------------------------------------------------- Visierung
# Klappkorn vorn, Kimmenleiter hinten - beide schmal, sonst stehen sie im Bild.
m.cbox(0.0, y(300), BZ + 0.118, 0.016, 26 * MM, 0.070, "detail", c=0.004)
m.cbox(0.0, y(690), BZ + 0.160, 0.038, 40 * MM, 0.052, "detail", c=0.006)

# ------------------------------------------------------------- Zweibein
# Angeklappt: die Beine liegen laengs am Laufmantel, leicht nach aussen gekippt.
for side in (-1, 1):
    m.angled_box(y(370), BZ - 0.056, 360 * MM, 0.026, 0.026, 90.0, "detail",
                 x_c=side * 0.058, c=0.006)
    m.cbox(side * 0.058, y(196), BZ - 0.064, 0.030, 40 * MM, 0.030, "detail", c=0.006)

# ---------------------------------------------------------------- schreiben
m.write(OUT)
m.report(OUT)
(x0, x1), (yy0, yy1), (z0, z1) = m.bounds()
print()
print("  Bezugspunkte")
print("    Muendung        y %.3f   (Anker Muzzle)" % Y_MUZZLE)
print("    Griffmitte      y %.3f   (RPD 0.624)" % GRIP_Y)
print("    Laufachse       z %.3f   (RPD 0.096)" % BZ)
print("    Gesamtlaenge    %.0f mm  (MG42 real 1220 mm)" % ((yy1 - yy0) / MM))
print("    Breite          %.0f mm  (RPD %.0f mm)" % ((x1 - x0) / MM, 0.136 / MM))
