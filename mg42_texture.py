"""Erzeugt Diffuse- und Normal-Textur des MG42 zu den UV-Regionen aus mg42_mesh.py.

    oben links   shroud    Laufmantel mit Kuehlbohrungen
    oben rechts  receiver  bruenierter Stahl, Nietenreihen
    unten links  stock     Bakelit fuer Griff und Schaft
    unten rechts detail    dunkles Metall fuer Kleinteile

WAS SICH GEAENDERT HAT
----------------------
1. Das Viertel "stock" trug (74, 48, 32) mit gerichteten Faserstreifen und einem
   Farbstich nach (1.2, 0.8, 0.5) - also helles Holz mit Maserung. Am MG42 sind
   Pistolengriff und Schaft dunkles Bakelit: fast schwarzbraun, fleckig statt
   faserig, matt. Das liefert texlib.bakelite(), jetzt bei (48, 33, 29).
2. Die Normal Map entsteht hier mit, statt in einem zweiten Skript. Sie muss zum
   selben Atlas passen; zwei Dateien mit getrennten Zufallszahlen driften
   auseinander, sobald man an einem Viertel etwas aendert.
3. Die Metallic/Smoothness-Map faellt weg. Keines der 1488 Spielmaterialien mit
   _Metallic benutzt eine solche Map (research/FINDINGS.md); die Waffen stehen
   auf _Metallic 0.0 und _Glossiness 0.6.
"""

import os
import sys

import numpy as np
from PIL import Image, ImageFilter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texlib as T

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
OUT_D = os.path.join(ASSETS, "mg42_diffuse.png")
OUT_N = os.path.join(ASSETS, "mg42_normal.png")

r = T.rng(4242)
H = T.H
P = T.px


def heat_tint(arr, height):
    """Sehr dezenter braun-violetter Anlasshof um die Kuehlbohrungen."""
    holes = np.clip((0.36 - height) / 0.32, 0, 1)
    img = Image.fromarray((holes * 255).astype(np.uint8))
    halo = np.asarray(img.filter(ImageFilter.GaussianBlur(P(6))), np.float32) / 255.0
    ring = np.clip(halo - holes * 0.72, 0, 1)[..., None]
    shift = np.asarray((0.030, -0.016, 0.022), np.float32).reshape(1, 1, 3)
    return np.clip(arr + ring * shift, 0, 1)


def blue_edge_wear(arr, height, strength=0.09):
    local = T.periodic_blur(height, P(11) / 3.0)
    high = np.clip(height - local, 0, None)
    lo = float(np.percentile(high, 86.0))
    hi = max(lo + 1.0e-5, float(np.percentile(high, 98.5)))
    mask = np.clip((high - lo) / (hi - lo), 0, 1)[..., None]
    blue_gray = np.asarray((88, 94, 108), np.float32).reshape(1, 1, 3) / 255.0
    mix = mask * strength
    return np.clip(arr * (1.0 - mix) + blue_gray * mix, 0, 1)


def hand_wear(arr):
    """Randlose, breit abgegriffene Stellen im Bakelit ohne Holzmaserung."""
    h, w, _ = arr.shape
    yy, xx = np.mgrid[0:h, 0:w]
    wear = (0.5 + 0.5 * np.sin(xx * 2.0 * np.pi / w + 0.7)
            * np.cos(yy * 2.0 * np.pi / h - 0.4))
    wear = np.clip((wear - 0.56) / 0.44, 0, 1)[..., None]
    worn = np.asarray((72, 49, 40), np.float32).reshape(1, 1, 3) / 255.0
    return np.clip(arr * (1.0 - wear * 0.09) + worn * wear * 0.09, 0, 1)


# --------------------------------------------------------------- Normal Map
# Die gestreuten Loch- und Nietenlayouts werden fuer Hoehe und Diffuse geteilt.
holes = T.perforation_layout(r, H, H, step=30, radius=9)
rivet_points = T.rivet_layout(r, H, H, rows=3, cols=9, radius=4)
h_shroud = T.height_perforation(r, H, H, step=30, radius=9, layout=holes)
h_receiver = T.height_rivets(r, H, H, layout=rivet_points)
h_stock = T.height_checker(r, H, H, pitch=13, depth=0.20)
h_stock = np.clip(h_stock + (T.height_bakelite(r, H, H) - 0.5) * 0.62, 0, 1)
h_detail = T.height_scratches(r, H, H)
heights = {"shroud": h_shroud, "receiver": h_receiver,
           "stock": h_stock, "detail": h_detail}

# ------------------------------------------------------------------ Diffuse
shroud = T.base(r, H, H, (92, 94, 98), 0.042)
shroud = T.mottle(r, shroud, 0.025, 12, (0.92, 0.94, 1.0))
shroud = T.perforation(r, shroud, step=30, radius=9, layout=holes)
shroud = heat_tint(shroud, h_shroud)
shroud = T.scratches(r, shroud, 64, 0.14, 38, direction=90.0)
shroud = T.couple_height(shroud, h_shroud, 0.09, 0.05, 10)

receiver = T.base(r, H, H, (68, 70, 77), 0.041, scale=6)
receiver = T.mottle(r, receiver, 0.028, 14, (0.86, 0.90, 1.0))
receiver = T.scratches(r, receiver, 82, 0.14, 50, direction=0.0)
receiver = T.rivets(r, receiver, rows=3, cols=9, layout=rivet_points)
receiver = T.couple_height(receiver, h_receiver, 0.09, 0.05, 11)
receiver = blue_edge_wear(receiver, h_receiver)

stock = T.bakelite(r, H, H, (55, 39, 34), swirl=0.045)
stock = T.mottle(r, stock, 0.018, 14, (1.0, 0.80, 0.72))
stock = hand_wear(stock)
stock = T.couple_height(stock, h_stock, 0.09, 0.05, 9)

detail = T.base(r, H, H, (52, 54, 59), 0.043, scale=5)
detail = T.mottle(r, detail, 0.025, 11, (0.88, 0.92, 1.0))
detail = T.scratches(r, detail, 100, 0.17, 42, direction=4.0)
detail = T.couple_height(detail, h_detail, 0.09, 0.05, 9)

os.makedirs(ASSETS, exist_ok=True)
T.save_atlas({"shroud": shroud, "receiver": receiver,
              "stock": stock, "detail": detail}, OUT_D)

print("MG42-Diffuse: %s  (%dx%d)" % (OUT_D, T.S, T.S))
print("  oben links   Laufmantel, Mittelwert RGB %s" % (T.mean_rgb(shroud),))
print("  oben rechts  Gehaeuse,   Mittelwert RGB %s" % (T.mean_rgb(receiver),))
print("  unten links  Bakelit,    Mittelwert RGB %s   (vorher 74,48,32 Holz)"
      % (T.mean_rgb(stock),))
print("  unten rechts Kleinteile, Mittelwert RGB %s" % (T.mean_rgb(detail),))

# Bricht das Licht pro Pixel: Kuehlbohrungen, Nietenkoepfe, Fugen und die
# Fischhaut am Griff bekommen einen eigenen Glanz, ohne ein zusaetzliches Dreieck.
T.save_height_atlas(heights, OUT_N, strength=3.2)
print("MG42-Normal : %s" % OUT_N)
