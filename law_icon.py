"""Rendert Item- und Waffenicon der M72 LAW mit Muendung nach rechts."""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import iconlib

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
MESH = os.path.join(ASSETS, "law.ndmesh")
TEX = os.path.join(ASSETS, "law_diffuse.png")
ICON = os.path.join(ASSETS, "law_icon.png")
WICON = os.path.join(ASSETS, "law_weapon_icon.png")

if __name__ == "__main__":
    iconlib.item_icon(MESH, TEX, ICON, size=300)
    iconlib.weapon_icon(MESH, TEX, WICON)
    print("M72-LAW-Icons")
    iconlib.report(ICON)
    iconlib.report(WICON)
