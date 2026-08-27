"""Erzeugt Diffuse- und Normal-Textur der .50 zu den UV-Regionen aus sniper50_mesh.py.

    oben links   shroud    Lauf und Handschutz, geriffelter Stahl
    oben rechts  receiver  Gehaeuse, matt eloxiertes Aluminium
    unten links  stock     Griff und Wangenauflage, mattes Polymer
    unten rechts detail    Muendungsbremse, Zielfernrohr, Kleinteile - fast schwarz

Bewusst anders als das MG42: das MG42 ist bruenierter Stahl mit braunem Bakelit,
die .50 ist ein modernes Chassis-Gewehr - kuehle Grautoene, kein Braun. So sind
die beiden Waffen schon am Farbklang auseinanderzuhalten.

Keine Metallic/Smoothness-Map, aus demselben Grund wie beim MG42: kein einziges
Spielmaterial benutzt eine (research/FINDINGS.md).
"""

import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texlib as T

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
OUT_D = os.path.join(ASSETS, "sniper50_diffuse.png")
OUT_N = os.path.join(ASSETS, "sniper50_normal.png")

r = T.rng(50500)
H = T.H


def flutes(arr, pitch=22, depth=0.10):
    """Laengsriefen im schweren Lauf - senkrecht im Atlasviertel."""
    h, w, _ = arr.shape
    xx = np.arange(w)[None, :]
    groove = 0.5 + 0.5 * np.cos(xx * 2.0 * np.pi / pitch)
    g = (groove ** 3)[..., None].astype(np.float32)
    return np.clip(arr * (1.0 - depth) + arr * depth * 2.0 * g, 0, 1)


def machining(arr, pitch=9, depth=0.05):
    """Feine waagerechte Drehriefen, wie sie gefraestes Aluminium zeigt."""
    h, w, _ = arr.shape
    yy = np.arange(h)[:, None]
    band = (0.5 + 0.5 * np.sin(yy * 2.0 * np.pi / pitch))[..., None].astype(np.float32)
    return np.clip(arr + (band - 0.5) * depth, 0, 1)


def height_flutes(w, h, pitch=22):
    xx = np.arange(w)[None, :]
    a = 0.5 + 0.22 * np.cos(xx * 2.0 * np.pi / pitch)
    return np.repeat(a, h, axis=0).astype(np.float32) + T.grain(r, w, h, 6, 0.03)


def height_machining(w, h, pitch=9):
    yy = np.arange(h)[:, None]
    a = 0.5 + 0.10 * np.sin(yy * 2.0 * np.pi / pitch)
    return np.repeat(a, w, axis=1).astype(np.float32) + T.grain(r, w, h, 5, 0.04)


# ------------------------------------------------------------------ Diffuse
shroud = T.base(r, H, H, (62, 64, 68), 0.045, scale=7)
shroud = flutes(shroud, pitch=22, depth=0.12)
shroud = T.scratches(r, shroud, 60, 0.14, 45)

receiver = T.base(r, H, H, (74, 76, 80), 0.04, scale=6)
receiver = machining(receiver, pitch=9, depth=0.05)
receiver = T.scratches(r, receiver, 70, 0.12, 50)

# Polymer: matt, minimal gruenstichig, sehr feine Koernung - kein Bakelit.
stock = T.base(r, H, H, (52, 54, 50), 0.05, scale=9)
stock = np.clip(stock + T.grain(r, H, H, 3, 0.05)[..., None], 0, 1)
stock = T.scratches(r, stock, 24, 0.06, 30)

detail = T.base(r, H, H, (30, 31, 34), 0.04, scale=5)
detail = T.scratches(r, detail, 90, 0.16, 40)

os.makedirs(ASSETS, exist_ok=True)
T.save_atlas({"shroud": shroud, "receiver": receiver,
              "stock": stock, "detail": detail}, OUT_D)

print("Sniper50-Diffuse: %s  (%dx%d)" % (OUT_D, T.S, T.S))
print("  oben links   Lauf geriffelt,  Mittelwert RGB %s" % (T.mean_rgb(shroud),))
print("  oben rechts  Chassis,         Mittelwert RGB %s" % (T.mean_rgb(receiver),))
print("  unten links  Polymer,         Mittelwert RGB %s" % (T.mean_rgb(stock),))
print("  unten rechts Optik/Kleinteile,Mittelwert RGB %s" % (T.mean_rgb(detail),))

# --------------------------------------------------------------- Normal Map
T.save_height_atlas({
    "shroud":   height_flutes(H, H, pitch=22),
    "receiver": height_machining(H, H, pitch=9),
    "stock":    T.height_checker(r, H, H, pitch=9, depth=0.13),
    "detail":   T.height_scratches(r, H, H, n=110, length=26),
}, OUT_N, strength=2.6)
print("Sniper50-Normal : %s" % OUT_N)
