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

# 51 mm Innendurchmesser aus dem Auftrag - der Radius der Bohrung.
#
# DAS ROHR IST HOHL - ZWEITER ANLAUF (2026-08-30)
# ------------------------------------------------
# Der erste Anlauf hat aus jedem `tube` auf der Achse ein `pipe` gemacht, und
# geometrisch war das richtig: ein Strahl durch die Muendung faellt seither
# 90 mm weit ins Rohr, bevor er etwas trifft. Der Benutzer sah trotzdem
# weiterhin kein Loch. Drei Gruende, alle nachgemessen:
#
#  1. ACHT INNENWAENDE UEBEREINANDER. Startrohr, Vorderrohr und sechs Ringe
#     waren alle `pipe`, und jedes hat seine eigene Innenwand gebaut. An jeder
#     Stossstelle lagen zwei Waende auf demselben Radius - Z-Fighting genau
#     dort, wo man hineinsieht. Jetzt bauen die Schalenteile mit `inner=False`
#     nur noch aussen, und die Bohrung ist EIN durchgehendes `bore` ueber die
#     volle Laenge.
#
#  2. DIE ZWEITE FLAECHENSCHICHT. Am Ende der Datei wird das ganze Modell ein
#     zweites Mal mit umgedrehten Normalen angehaengt, damit die Schulterkamera
#     nicht durch das Rohr sieht. Das traf auch die Innenwand: zu jeder nach
#     innen sehenden Flaeche lag eine nach aussen sehende auf demselben Platz,
#     und aus der Muendung sah man deren falsch beleuchtete Rueckseite - eine
#     helle, flache Scheibe. Die zweite Schicht bekommt jetzt nur noch die
#     AUSSENHAUT.
#
#  3. DIE RAKETE STAND ZU WEIT VORN. 90 mm hinter der Muendung, bei fast
#     vollem Bohrungsdurchmesser und in dunklem Metall: das ist aus zwei
#     Metern Entfernung ein schwarzer Deckel. Sie sitzt jetzt 300 mm tief, und
#     davor liegt ein Schacht, in dem das Licht abnimmt.
#
# Dazu die MUENDUNGSFASE (`Mesh.crown`). Eine ebene Ringflaeche an der Muendung
# faengt dasselbe Glanzlicht wie die Rohrwand daneben und ist von ihr nicht zu
# unterscheiden. Eine Fase steht schraeg und faengt ein anderes; dieser helle
# Kranz ist der Unterschied zwischen einem Loch und einem gemalten Kreis.
#
# Die Bohrung traegt das Viertel "stock" - das ist bei der LAW das fast
# schwarze Gummi. Ein Rohrinneres ist dunkel; mit dem Oliv der Aussenhaut
# saehe die Bohrung aus wie ein aufgemalter Kreis.
BORE = 51.0 * MM / 2.0
BORE_REGION = "stock"


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
    # Glasfasergehaeuse, das vordere Rohr ist ausgezogen und schlanker. Beide
    # sind durchgehend gebohrt: die M72 ist ein rueckstossfreier Werfer und an
    # BEIDEN Enden offen.
    m.pipe(0.0, BZ, Y_REAR + 0.030, 0.335, 0.084, BORE, "shroud",
           bore_region=BORE_REGION, seg=40, inner=False)
    m.pipe(0.0, BZ, 0.270, Y_MUZZLE - 0.035, 0.071, BORE, "receiver",
           bore_region=BORE_REGION, seg=40, inner=False)

    # Ueberlappung, Endkappen und Gummiringe erzeugen die typische gestufte
    # Silhouette. Mehrere schmale Ringe vermeiden den Eindruck eines Rohblocks.
    # Auch sie sind Ringe und keine Scheiben - ein einziger voller Deckel
    # irgendwo auf der Achse wuerde die ganze Bohrung wieder zumauern.
    for yy, rr, width, region in (
            (Y_REAR + 0.022, 0.095, 0.044, "stock"),
            (Y_REAR + 0.080, 0.090, 0.030, "detail"),
            (0.285, 0.090, 0.035, "stock"),
            (0.340, 0.086, 0.026, "detail"),
            (Y_MUZZLE - 0.045, 0.080, 0.045, "stock"),
            (Y_MUZZLE - 0.006, 0.077, 0.028, "detail")):
        m.pipe(0.0, BZ, yy - width / 2.0, yy + width / 2.0, rr, BORE, region,
               bore_region=BORE_REGION, seg=40, inner=False)

    # Muendungsring: aussen leicht angefast, innen die Bohrung. Hier stand ein
    # `cone` - ein VOLLER Kegel, der die Bohrung vorn zugestopft hat.
    #
    # Der Aussenradius war danach 0.064 und lag damit UNTER der Bohrung
    # (BORE = 0.0648): ein Rohr mit negativer Wandstaerke. Die beiden
    # Ringflaechen an den Enden liefen dadurch verkehrt herum um - die 320
    # Dreiecke, die verify.py bis 2026-08-30 gemeldet hat. 0.070 laesst den
    # Ring schmal, aber mit Wand: knapp 2 mm, innerhalb des 0.077er Rings
    # davor.
    m.pipe(0.0, BZ, Y_MUZZLE - 0.020, Y_MUZZLE + 0.012, 0.070, BORE,
           "stock", bore_region=BORE_REGION, seg=40, inner=False)

    # Abzugs- und Zuendgehaeuse an der RPD-Handposition. Es umgreift das Rohr
    # von unten und gibt der rechten Hand eine echte, gefaste Kontaktflaeche.
    #
    # ES DARF DIE BOHRUNG NICHT SCHNEIDEN (2026-08-30). Der Kasten stand auf
    # z 0.016 mit 0.205 Hoehe, reichte also von -0.087 bis 0.119 - und die
    # Bohrung beginnt bei z = BZ - BORE = 0.031. Er ragte damit 87 Tausendstel
    # in das Rohr hinein und verschloss es 228 mm hinter der Muendung. Wer
    # vorn hineinsah, sah ein kurzes Stueck Rohr und dahinter eine Wand - der
    # eigentliche Grund, warum die LAW auch nach dem ersten Anlauf nicht hohl
    # aussah. Jetzt endet er bei z 0.028, drei Tausendstel unter der Bohrung,
    # und liegt immer noch auf der Rohraussenwand auf (die bei z 0.012
    # beginnt).
    m.cbox(0.0, GRIP_Y, -0.029, 0.118, 0.190, 0.114, "receiver", c=0.014)
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
    m.cbox(0.0, ry, 0.192, 0.140, 0.115, 0.055, "detail", c=0.008)
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

    # ------------------------------------------------------------ die Huelle
    # ist fertig. Ab hier kommt, was NICHT doppelt gebaut werden darf.
    schale_v = len(m.V)
    schale_i = len(m.IDX)

    # EINE Bohrung ueber die ganze Laenge, statt acht kurzer uebereinander.
    m.bore(0.0, BZ, Y_REAR + 0.020, Y_MUZZLE + 0.010, BORE, BORE_REGION,
           seg=40)

    # Die Fase an beiden Enden - die M72 ist an beiden offen. Aussen der
    # groessere Radius, innen die Bohrung, 8 mm tief.
    m.crown(0.0, BZ, Y_MUZZLE + 0.012, Y_MUZZLE - 0.008, 0.070, BORE,
            "detail", seg=40)
    m.crown(0.0, BZ, Y_REAR + 0.022, Y_REAR + 0.042, 0.094, BORE,
            "detail", seg=40)

    # Die Rakete steckt im Rohr. Ohne sie sieht man durch die Bohrung hindurch
    # ins Freie und auf der anderen Seite den Spieler stehen: die LAW ist ab
    # Werk geladen, das Rohr ist nicht leer. 300 mm tief statt 90 - davor
    # liegt jetzt ein Schacht, in dem das Licht abnimmt, und genau der macht
    # aus einer schwarzen Scheibe ein Rohr.
    nose = Y_MUZZLE - 0.760
    m.cone(0.0, BZ, nose - 0.090, nose, BORE - 0.006, 0.010, "detail", seg=24)
    m.tube(0.0, BZ, nose - 0.330, nose - 0.090, BORE - 0.006, "detail", seg=24)

    # Das Spender-Skelett richtet die Laengsachse im Spiel entgegengesetzt
    # zur Generatoransicht aus. Um 180 Grad um den Griff drehen, damit das
    # lange hintere Rohr zur Schulter und die Muendung vom Spieler weg zeigt.
    m.V = [(-p[0], 2.0 * GRIP_Y - p[1], p[2]) for p in m.V]
    m.N = [(-n[0], -n[1], n[2]) for n in m.N]

    # Beim Tragen schneidet die Schulterkamera stellenweise in das lange Rohr.
    # Die Spielmaterialien cullen Rueckseiten; dann sah die kameranahe Rohrwand
    # durchsichtig oder aufgebrochen aus. Eine zweite, nach innen gerichtete
    # Flaechenschicht macht das Geraet aus jeder Trageperspektive geschlossen.
    #
    # NUR DIE AUSSENHAUT. Bis 2026-08-30 traf das auch die Bohrung, und dort
    # richtet dieselbe Massnahme genau den Schaden an, den sie aussen behebt:
    # zu jeder nach innen sehenden Flaeche lag eine nach aussen sehende auf
    # demselben Platz, und aus der Muendung sah man deren falsch beleuchtete
    # Rueckseite - eine helle, flache Scheibe statt eines Lochs.
    kopiert = {}
    for i in range(0, schale_i, 3):
        drei = []
        for k in (i, i + 2, i + 1):
            alt = m.IDX[k]
            if alt >= schale_v:
                drei = None
                break
            neu = kopiert.get(alt)
            if neu is None:
                neu = len(m.V)
                kopiert[alt] = neu
                m.V.append(m.V[alt])
                n = m.N[alt]
                m.N.append((-n[0], -n[1], -n[2]))
                m.T.append(m.T[alt])
            drei.append(neu)
        if drei is not None:
            m.IDX.extend(drei)

    return m


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    mesh = build()
    mesh.write(OUT)
    mesh.report(OUT)
    print("  Soll-Laenge: %.3f Einheiten (890 mm)" % (890.0 * MM))
    print("  Griffmitte : y %.3f" % GRIP_Y)
