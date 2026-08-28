"""Erzeugt das Munitionsitem 2052: eine LAW-Rakete im Transportrohr."""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ndmesh import Mesh

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
OUT = os.path.join(ASSETS, "rocket.ndmesh")
REF_SIZE = (0.364, 0.150, 0.370)
REF_CENTER = (-0.022, -0.001, -0.206)


def build():
    pack = Mesh("M72 Rocket Pack")

    # Zuerst laengs entlang Y bauen, danach fuer das Kisten-Prefab querlegen.
    tube = Mesh("transport tube")
    tube.tube(0.0, 0.0, -0.58, 0.52, 0.145, "shroud", seg=32)
    tube.tube(0.0, 0.0, -0.61, -0.54, 0.162, "stock", seg=32)
    tube.tube(0.0, 0.0, 0.48, 0.55, 0.162, "stock", seg=32)
    tube.tube(0.0, 0.0, -0.30, -0.24, 0.153, "detail", seg=32)
    tube.tube(0.0, 0.0, 0.22, 0.28, 0.153, "detail", seg=32)

    # Sichtbare Raketenspitze an einem geoeffneten Ende.
    tube.cone(0.0, 0.0, 0.42, 0.67, 0.095, 0.018, "receiver", seg=32)
    tube.tube(0.0, 0.0, 0.36, 0.44, 0.097, "receiver", seg=32)

    # Tragegriff und zwei Spannbaender.
    tube.cbox(0.0, -0.02, 0.178, 0.060, 0.45, 0.055,
              "detail", c=0.012)
    tube.cbox(-0.105, -0.02, 0.145, 0.045, 0.13, 0.085,
              "detail", c=0.010)
    tube.cbox(0.105, -0.02, 0.145, 0.045, 0.13, 0.085,
              "detail", c=0.010)

    pack.merge(tube, rot_deg=(0.0, 0.0, 90.0))
    pack.fit_box(REF_SIZE, REF_CENTER)
    return pack


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    mesh = build()
    mesh.write(OUT)
    mesh.report(OUT)
    print("  Auf magaz_l-Groesse und Mittelpunkt eingepasst")
