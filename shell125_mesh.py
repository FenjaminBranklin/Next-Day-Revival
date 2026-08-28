"""Erzeugt das Munitionsitem 2053: eine 125-mm-Granate fuer den T-72.

Der T-72 laedt getrennt - Geschoss und Treibladung sind zwei Teile. Als
Inventargegenstand waeren zwei Teile aber nur verwirrend, deshalb steht hier
beides zusammengesetzt: Geschoss mit Ogivenspitze, Fuehrungsband, dahinter die
teilverbrennliche Treibladung mit ihrem Metallboden.

Wie bei der LAW-Rakete wird laengs entlang Y gebaut, dann quergelegt und auf
die Groesse des Kisten-Prefabs eingepasst - so liegt das Item im Inventar wie
die anderen Munitionskisten.
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ndmesh import Mesh

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
OUT = os.path.join(ASSETS, "shell125.ndmesh")

# Dieselben Bezugsmasse wie rocket_mesh.py: das Kisten-Prefab magaz_l.
REF_SIZE = (0.364, 0.150, 0.370)
REF_CENTER = (-0.022, -0.001, -0.206)

R = 0.0625                      # 125 mm, halbiert
SEG = 32


def build():
    pack = Mesh("125 mm Granate")

    s = Mesh("Patrone")

    # Treibladung: dickere Huelse mit Metallboden, hinten.
    s.tube(0.0, 0.0, -0.62, -0.57, R * 1.10, "detail", seg=SEG)
    s.tube(0.0, 0.0, -0.60, -0.18, R * 1.02, "stock", seg=SEG)
    # Uebergangsring zum Geschoss - ohne ihn sieht die Patrone aus wie ein
    # durchgehendes Rohr, und genau daran erkennt man Munition.
    s.tube(0.0, 0.0, -0.20, -0.14, R * 1.08, "detail", seg=SEG)

    # Geschosskoerper.
    s.tube(0.0, 0.0, -0.16, 0.30, R, "shroud", seg=SEG)

    # Fuehrungsband aus Kupfer, kurz vor dem Boden des Geschosses.
    s.tube(0.0, 0.0, -0.10, -0.03, R * 1.06, "detail", seg=SEG)

    # Ogive in drei Stufen. Ein einzelner Kegel waere ein Bleistift; drei
    # Abschnitte mit abnehmender Steigung ergeben die gewoelbte Spitze.
    s.cone(0.0, 0.0, 0.30, 0.46, R, R * 0.86, "shroud", seg=SEG)
    s.cone(0.0, 0.0, 0.46, 0.58, R * 0.86, R * 0.60, "shroud", seg=SEG)
    s.cone(0.0, 0.0, 0.58, 0.66, R * 0.60, R * 0.30, "shroud", seg=SEG)

    # Aufschlagzuender an der Spitze, dunkel abgesetzt.
    s.tube(0.0, 0.0, 0.64, 0.70, R * 0.32, "receiver", seg=SEG)
    s.cone(0.0, 0.0, 0.70, 0.745, R * 0.30, R * 0.14, "receiver", seg=SEG)

    pack.merge(s, rot_deg=(0.0, 0.0, 90.0))
    pack.fit_box(REF_SIZE, REF_CENTER)
    return pack


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    mesh = build()
    mesh.write(OUT)
    mesh.report(OUT)
    print("  Auf magaz_l-Groesse und Mittelpunkt eingepasst")
