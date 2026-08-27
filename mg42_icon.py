"""Rendert die beiden MG42-Icons im Format, das das Spiel wirklich benutzt.

    mg42_icon.png         300 x 300   ItemIcon,   diagonal   (wie RPD_Item)
    mg42_weapon_icon.png  317 x 183   WeaponIcon, waagerecht (wie RPD_Weapon)

Die Bildgroessen sind aus resources.assets abgelesen, nicht geschaetzt:
RPD_Item, svd_Item und PSG-1_Item sind 300x300, RPD_Weapon, svd_Weapon und
PSG-1_Weapon sind 317x183.
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import iconlib

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
MESH = os.path.join(ASSETS, "mg42.ndmesh")
TEX = os.path.join(ASSETS, "mg42_diffuse.png")
ICON = os.path.join(ASSETS, "mg42_icon.png")
WICON = os.path.join(ASSETS, "mg42_weapon_icon.png")

if __name__ == "__main__":
    iconlib.item_icon(MESH, TEX, ICON, size=300, tilt=33.0)
    iconlib.weapon_icon(MESH, TEX, WICON)
    print("MG42-Icons")
    iconlib.report(ICON)
    iconlib.report(WICON)
