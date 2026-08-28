"""Rendert das 300x300-Inventaricon fuer die FPV-Drohne."""

import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import iconlib

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
MESH = os.path.join(ASSETS, "drone.ndmesh")
TEX = os.path.join(ASSETS, "drone_diffuse.png")
ICON = os.path.join(ASSETS, "drone_icon.png")

if __name__ == "__main__":
    v, nm, idx, uv = iconlib.load(MESH)
    tex = iconlib.load_texture(TEX)
    # The donor mesh is flat along Y. Turn that thin axis toward the normal
    # item camera so the icon gets the intended oblique top/front view.
    a = np.radians(90.0)
    rz = np.array([[np.cos(a), -np.sin(a), 0.0],
                   [np.sin(a), np.cos(a), 0.0],
                   [0.0, 0.0, 1.0]], np.float32)
    v = v @ rz.T
    nm = nm @ rz.T
    big = iconlib.render(v, nm, idx, uv, tex,
                         300 * iconlib.SS, 300 * iconlib.SS,
                         yaw=0.48, pitch=0.30, fill=0.96)
    img = iconlib.fit(big, 300, 300, margin=0.87)
    img = iconlib.drop_shadow(img, offset=(4, 6), blur=6, strength=0.5)
    img.save(ICON)
    print("FPV-Drohnen-Icon")
    iconlib.report(ICON)
