"""Erzeugt das Mesh der .50 (TAC-50) und schreibt es als .ndmesh.

DIE BEZUGSPUNKTE KOMMEN VON DER SVD, NICHT VOM RPD (2026-08-30)
---------------------------------------------------------------
Der Befund des Benutzers war, dass die Handhaltung nicht zum Griff passt und
dass der Griff keinen Abzug hat. Das zweite stimmte woertlich; das erste hatte
eine messbare Ursache.

Bis hierher stand in dieser Datei GRIP_Y = 0.624 und BZ = 0.096, "aus dem
RPD-Mesh gemessen". Das ist die richtige Referenz fuer das MG42 - dessen
Spende-Waffe IST das RPD (1023). Die TAC-50 wird dagegen von der **SVD (1010)**
abgeleitet, und `CopyDonorComponents` uebernimmt deren
WeaponTranformManager-Werte. Die rechte Hand landet also dort, wo die SVD ihren
Pistolengriff hat - und nicht dort, wo das RPD seinen hat.

DIE ERSTE MESSUNG WAR UM 79 mm FALSCH (2026-08-30, zweiter Befund)
------------------------------------------------------------------
Der Benutzer meldete dieselbe Sache noch einmal: die Hand passt nicht zum
Griff. Also nachgemessen, diesmal am Mesh selbst statt an Augenmass.

`m.export()` auf das Mesh "SVD" aus `resources.assets` (UnityPy liefert
`m_Vertices` seit einer Version nicht mehr direkt - die OBJ-Ausgabe schon),
dann in Scheiben von 0.04 laengs der Waffe geschnitten und je Scheibe
gefragt, wie weit sie unter die Laufachse reicht und wie breit sie dort ist.
Ein Pistolengriff ist SCHMAL und TIEF; ein Schaft ist breit:

    y 0.36..0.44   z bis -0.167   x -0.033..0.024   Abzug und Buegel
    y 0.56..0.80   z bis -0.172   x -0.021..0.022   PISTOLENGRIFF
    y 0.80..1.00   z bis -0.316   x -0.046..0.031   Daumenlochschaft
    y 1.06..1.62   z bis -0.293                     Kolben und Wangenauflage

Die bisherigen 0.801..0.966 sind also NICHT der Griff, sondern der Rahmen des
Daumenlochschafts dahinter - dort ist das Mesh am tiefsten, und genau darauf
ist die erste Messung hereingefallen. Der Griff sitzt bei y 0.56..0.80, Mitte
**0.68**, und der Abzug bei y 0.40.

    GRIP_Y  0.884 -> 0.680     0.204 Einheiten = 79 mm weiter VORN
    Abzug   GRIP_Y-0.162 -> GRIP_Y-0.235

`GRIP_Y` ist zugleich der Anker von `y()`, die ganze Waffe wandert also
geschlossen mit - Lauf, Gehaeuse und Schaft behalten ihre Verhaeltnisse, nur
der Griff kommt dorthin, wo die Hand der Spende zugreift.

    Laufachse       x  0.000            z  0.026   (SVD_Muzzle 0, -1.675, 0.026)
    Bounds          x +-0.101   y +-1.581   z +-0.316

Massstab: 1 Einheit = 393.5 mm (aus der RPD hergeleitet, siehe mg42_mesh.py).

    TAC-50 real     Gesamtlaenge 1448 mm, Lauf 737 mm, 5 Schuss .50 BMG
    im Mesh         3.68 Einheiten
    zum Vergleich   SVD 3.16 Einheiten, MG42 3.10, RPD 2.64

Die Waffe ist damit deutlich groesser als die Spende - so gewollt.

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
GRIP_Y = 0.680            # Handposition, Mitte des SVD-Griffs (0.56..0.80)
BZ = 0.026                # Laufachse, aus SVD_Muzzle
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

# Magazin, 5 Schuss .50 - ein tiefer Kasten unter dem Gehaeuse. 860 statt 900
# mm: bei 900 reichte sein hinteres Ende bis y GRIP_Y-0.266 und damit in den
# Abzugsbuegel hinein, der jetzt an der gemessenen Stelle sitzt. Ein Magazin
# gehoert VOR den Buegel, nicht in ihn.
m.cbox(0.0, y(860), BZ - 0.150, 0.062, 150 * MM, 0.200, "detail", c=0.012)

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
#
# Der Griff fuellt die GEMESSENE Huelle des SVD-Griffs: z -0.179..-0.025 gegen
# gemessene -0.172..-0.030, x +-0.034 gegen gemessene -0.021..0.022. Er ist
# absichtlich eine Spur breiter als die Spende - das SVD-Mesh ist an dieser
# Stelle sehr grob, und ein 13 mm breiter Griff sieht in der Hand aus wie ein
# Lineal.
GZ = BZ - 0.128                       # Mitte des Griffs in der Hoehe
m.angled_box(GRIP_Y + 0.010, GZ, 0.160, 0.108, 0.068, 15.0, "stock", c=0.018)
# Ruecken mit Handballenauflage - ohne ihn ist der Griff ein Brett.
m.angled_box(GRIP_Y + 0.052, GZ + 0.006, 0.124, 0.040, 0.060, 15.0,
             "stock", c=0.014)
# Zwei Fingerrillen an der Vorderkante. Sie kosten zwoelf Dreiecke und sind
# das, was einen Griff in der ersten Person als Griff lesbar macht.
for dz in (-0.030, 0.026):
    m.angled_box(GRIP_Y - 0.040, GZ + dz, 0.030, 0.028, 0.062, 15.0,
                 "stock", c=0.008)
# Zwischenstueck vom Gehaeuseboden zum Griffkopf und die Griffkappe unten.
m.cbox(0.0, GRIP_Y - 0.018, BZ - 0.072, 0.076, 0.132, 0.062, "receiver", c=0.010)
m.cbox(0.0, GRIP_Y + 0.032, BZ - 0.206, 0.074, 0.116, 0.020, "detail", c=0.006)

# ABZUGSBUEGEL ALS GESCHLOSSENER RING (2026-08-30)
#
# Bis hierher waren es zwei Teile - ein Bodenbuegel und ein Steg davor - und
# das ist ein L, kein Buegel: von der Seite fehlte der Ring, und der Abzug
# stand darin ohne erkennbare Fassung. Vier Teile schliessen ihn: Boden, Steg
# vorn, Steg hinten in den Griff, und oben die Decke am Gehaeuseboden.
# Der hintere Steg sitzt an der VORDERKANTE DES GRIFFS, nicht davor. Beim
# ersten Versuch stand er bei GRIP_Y-0.122 und der Griff beginnt bei
# GRIP_Y-0.044 - dazwischen klaffte ein Spalt von 24 mm, und ein Buegel, der
# den Griff nicht beruehrt, ist kein Buegel.
BUEGEL_Z = BZ - 0.152                 # Unterkante des Rings
m.cbox(0.0, GRIP_Y - 0.172, BUEGEL_Z, 0.030, 0.270, 0.024, "detail", c=0.005)
m.cbox(0.0, GRIP_Y - 0.290, BZ - 0.112, 0.028, 0.030, 0.104, "detail", c=0.005)
m.cbox(0.0, GRIP_Y - 0.055, BZ - 0.116, 0.030, 0.038, 0.096, "detail", c=0.005)
m.cbox(0.0, GRIP_Y - 0.172, BZ - 0.066, 0.030, 0.280, 0.022, "detail", c=0.005)

# DER ABZUG, an der gemessenen Stelle (SVD: y 0.40, hier GRIP_Y-0.235) und
# gross genug, um einer zu sein. Das Blatt haengt aus dem Gehaeuseboden in den
# Ring und ist nach hinten geneigt; unten sitzt der breitere Abzugsschuh.
m.angled_box(GRIP_Y - 0.235, BZ - 0.100, 0.088, 0.026, 0.022, 20.0,
             "detail", c=0.004)
m.cbox(0.0, GRIP_Y - 0.219, BZ - 0.140, 0.026, 0.038, 0.018, "detail", c=0.003)
# Sicherungsfluegel ueber dem Abzug, rechts am Gehaeuse.
m.cbox(0.052, GRIP_Y - 0.090, BZ - 0.040, 0.026, 0.062, 0.030, "detail", c=0.005)

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
print("    Griffmitte      y %.3f   (SVD gemessen 0.56..0.80, Mitte 0.68)"
      % GRIP_Y)
print("    Abzug           y %.3f   (SVD gemessen 0.40)" % (GRIP_Y - 0.235))
print("    Laufachse       z %.3f   (SVD_Muzzle 0.026)" % BZ)
print("    Zielfernrohr    z %.3f" % SZ)
print("    Gesamtlaenge    %.0f mm  (TAC-50 real 1448 mm, MG42 1220 mm)"
      % ((yy1 - yy0) / MM))
print("    Breite          %.0f mm  (SVD 79 mm, MG42 83 mm)" % ((x1 - x0) / MM))
