"""Rendert die ItemIcons der beiden Munitionsitems, 300x300 wie im Spiel.

Munition hat kein WeaponIcon - bei 2030_Spawn (7,62-Kiste) ist das Feld leer,
gesetzt ist nur ItemIcon = RPD_Drum (300x300). Genau so wird es hier gemacht.

Die Kisten werden fast von vorn gezeigt, nicht diagonal wie die Waffen: das
Vorbild RPD_Drum steht ebenfalls aufrecht im Bild.
"""

import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import iconlib

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")


def one(name, yaw, pitch, pre_z=40.0):
    mesh = os.path.join(ASSETS, name + ".ndmesh")
    tex = os.path.join(ASSETS, name + "_diffuse.png")
    out = os.path.join(ASSETS, name + "_icon.png")
    v, nm, idx, uv = iconlib.load(mesh)
    t = iconlib.load_texture(tex)
    # Der Renderer blickt entlang X, die Kisten liegen mit der langen Achse in
    # X. Ohne Vordrehung sieht man nur die Schmalseite; mit 90 Grad zeigt die
    # duenne Achse zur Kamera und der Gurt haengt hinter der Kiste. Ein Wert
    # dazwischen gibt die Dreiviertelansicht, in der Deckel, Seite und Gurt
    # gleichzeitig zu sehen sind.
    a = np.radians(pre_z)
    RZ = np.array([[np.cos(a), -np.sin(a), 0.0],
                   [np.sin(a), np.cos(a), 0.0],
                   [0.0, 0.0, 1.0]], np.float32)
    v = v @ RZ.T
    nm = nm @ RZ.T
    big = iconlib.render(v, nm, idx, uv, t, 300 * iconlib.SS, 300 * iconlib.SS,
                         yaw, pitch, fill=0.98)
    img = iconlib.fit(big, 300, 300, margin=0.90)
    img = iconlib.drop_shadow(img, offset=(4, 6), blur=6, strength=0.5)
    img.save(out)
    iconlib.report(out)


if __name__ == "__main__":
    print("Munitions-Icons")
    # Leicht von der Seite und von oben, damit Deckel, Griff und der
    # heraushaengende Gurt gleichzeitig zu sehen sind.
    one("mgbelt", yaw=0.75, pitch=0.42, pre_z=0.0)
    one("ammo50", yaw=0.75, pitch=0.42, pre_z=0.0)
