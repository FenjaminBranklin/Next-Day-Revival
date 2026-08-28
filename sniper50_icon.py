"""Rendert die beiden Icons der .50 im Format, das das Spiel benutzt.

    sniper50_icon.png         300 x 300   ItemIcon,   diagonal
    sniper50_weapon_icon.png  317 x 183   WeaponIcon, waagerecht
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import iconlib

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
MESH = os.path.join(ASSETS, "sniper50.ndmesh")
TEX = os.path.join(ASSETS, "sniper50_diffuse.png")
ICON = os.path.join(ASSETS, "sniper50_icon.png")
WICON = os.path.join(ASSETS, "sniper50_weapon_icon.png")

if __name__ == "__main__":
    iconlib.item_icon(MESH, TEX, ICON, size=300)
    iconlib.weapon_icon(MESH, TEX, WICON)
    print("Sniper50-Icons")
    iconlib.report(ICON)
    iconlib.report(WICON)
