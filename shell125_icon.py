"""Rendert das 300x300-Inventaricon der 125-mm-Granate."""

import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import iconlib

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
MESH = os.path.join(ASSETS, "shell125.ndmesh")
TEX = os.path.join(ASSETS, "shell125_diffuse.png")
ICON = os.path.join(ASSETS, "shell125_icon.png")

if __name__ == "__main__":
    v, nm, idx, uv = iconlib.load(MESH)
    tex = iconlib.load_texture(TEX)
    # Die Granate liegt fuer das Kisten-Prefab entlang X, der Item-Renderer
    # blickt fast entlang dieser Achse - ohne Vordrehung saehe man nur den
    # Geschossboden. Dieselbe Drehung wie bei der LAW-Rakete.
    a = np.radians(90.0)
    rz = np.array([[np.cos(a), -np.sin(a), 0.0],
                   [np.sin(a), np.cos(a), 0.0],
                   [0.0, 0.0, 1.0]], np.float32)
    v = v @ rz.T
    nm = nm @ rz.T
    big = iconlib.render(v, nm, idx, uv, tex,
                         300 * iconlib.SS, 300 * iconlib.SS,
                         yaw=0.62, pitch=0.30, fill=0.96)
    img = iconlib.fit(big, 300, 300, margin=0.90)
    img = iconlib.drop_shadow(img, offset=(4, 6), blur=6, strength=0.5)
    img.save(ICON)
    print("125-mm-Granate, Icon")
    iconlib.report(ICON)
