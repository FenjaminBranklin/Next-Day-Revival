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

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texlib as T

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
OUT_D = os.path.join(ASSETS, "mg42_diffuse.png")
OUT_N = os.path.join(ASSETS, "mg42_normal.png")

r = T.rng(4242)
H = T.H

# ------------------------------------------------------------------ Diffuse
shroud = T.base(r, H, H, (86, 88, 92), 0.05)
shroud = T.perforation(r, shroud, step=30, radius=9)
shroud = T.scratches(r, shroud, 70, 0.18, 40)

receiver = T.steel(r, H, H, (58, 60, 66), scratch_n=90)
receiver = T.rivets(r, receiver, rows=3, cols=9)

stock = T.bakelite(r, H, H, (48, 33, 29))

detail = T.base(r, H, H, (44, 46, 50), 0.05, scale=5)
detail = T.scratches(r, detail, 110, 0.22, 45)

os.makedirs(ASSETS, exist_ok=True)
T.save_atlas({"shroud": shroud, "receiver": receiver,
              "stock": stock, "detail": detail}, OUT_D)

print("MG42-Diffuse: %s  (%dx%d)" % (OUT_D, T.S, T.S))
print("  oben links   Laufmantel, Mittelwert RGB %s" % (T.mean_rgb(shroud),))
print("  oben rechts  Gehaeuse,   Mittelwert RGB %s" % (T.mean_rgb(receiver),))
print("  unten links  Bakelit,    Mittelwert RGB %s   (vorher 74,48,32 Holz)"
      % (T.mean_rgb(stock),))
print("  unten rechts Kleinteile, Mittelwert RGB %s" % (T.mean_rgb(detail),))

# --------------------------------------------------------------- Normal Map
# Bricht das Licht pro Pixel: Kuehlbohrungen, Nietenkoepfe, Fugen und die
# Fischhaut am Griff bekommen einen eigenen Glanz, ohne ein zusaetzliches Dreieck.
T.save_height_atlas({
    "shroud":   T.height_perforation(r, H, H, step=30, radius=9),
    "receiver": T.height_rivets(r, H, H),
    "stock":    T.height_checker(r, H, H, pitch=13, depth=0.20),
    "detail":   T.height_scratches(r, H, H),
}, OUT_N, strength=3.2)
print("MG42-Normal : %s" % OUT_N)
