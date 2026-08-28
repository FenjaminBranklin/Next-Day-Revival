"""Rendert das 300x300-Inventaricon fuer die einzelne LAW-Rakete."""

import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import iconlib

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
MESH = os.path.join(ASSETS, "rocket.ndmesh")
TEX = os.path.join(ASSETS, "rocket_diffuse.png")
ICON = os.path.join(ASSETS, "rocket_icon.png")

if __name__ == "__main__":
    v, nm, idx, uv = iconlib.load(MESH)
    tex = iconlib.load_texture(TEX)
    # Das Packrohr liegt fuer das Spender-Prefab entlang X. Der normale
    # Item-Renderer blickt fast entlang dieser Achse; 90 Grad Vordrehung zeigt
    # stattdessen Rohr, Raketenspitze, Baender und Griff von der Seite.
    a = np.radians(90.0)
    rz = np.array([[np.cos(a), -np.sin(a), 0.0],
                   [np.sin(a), np.cos(a), 0.0],
                   [0.0, 0.0, 1.0]], np.float32)
    v = v @ rz.T
    nm = nm @ rz.T
    big = iconlib.render(v, nm, idx, uv, tex,
                         300 * iconlib.SS, 300 * iconlib.SS,
                         yaw=0.65, pitch=0.34, fill=0.96)
    img = iconlib.fit(big, 300, 300, margin=0.90)
    img = iconlib.drop_shadow(img, offset=(4, 6), blur=6, strength=0.5)
    img.save(ICON)
    print("M72-Raketenpack-Icon")
    iconlib.report(ICON)
