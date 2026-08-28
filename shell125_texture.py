"""Diffuse und Normal Map der 125-mm-Granate.

Vier Viertel, vier Werkstoffe: lackierter Geschosskoerper, geschwaerzter
Zuender, Messinghuelse der Treibladung, Kupfer fuer Fuehrungsband und Ringe.

Der Geschosskoerper traegt dasselbe Oliv wie der Panzer selbst
(`t72_texture.py`, am BTR gemessen).

EIN VERSUCH, DER NICHT GEHT: ein gelbes Kennband quer ueber den Geschoss-
koerper, damit das Icon vor dunklem Grund mehr hergibt. `ndmesh._uv`
projiziert jede Flaeche einzeln, und der Geschosskoerper besteht aus 32
schmalen Streifen - jeder sieht einen anderen Ausschnitt des Viertels. Aus dem
Band wurden im Icon ein Dutzend verstreuter gelber Striche. Farbe muss hier
ueber die REGION kommen, nicht ueber eine Stelle im Bild: Messing fuer die
Huelse, Kupfer fuer Fuehrungsband und Ringe, Schwarz fuer den Zuender.
"""

import os
import sys

import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texlib as T

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HIER, "assets")
OUT_D = os.path.join(ASSETS, "shell125_diffuse.png")
OUT_N = os.path.join(ASSETS, "shell125_normal.png")
H = T.H

OLIV = (58, 59, 43)             # wie die Wanne des T-72
ZUENDER = (38, 38, 36)
MESSING = (120, 96, 44)
KUPFER = (118, 71, 38)


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    r = T.rng(722053)

    h_koerper = 0.5 + T.grain(r, H, H, 5, 0.045) + T.grain(r, H, H, 2, 0.03)
    h_zuender = T.height_rivets(r, H, H, rows=1, cols=6, seams=(96, 160))
    h_huelse = 0.5 + T.grain(r, H, H, 4, 0.05)
    h_ring = 0.5 + T.grain(r, H, H, 3, 0.06)
    heights = {"shroud": h_koerper, "receiver": h_zuender,
               "stock": h_huelse, "detail": h_ring}

    koerper = T.base(r, H, H, OLIV, 0.032, scale=7)
    koerper = T.mottle(r, koerper, 0.020, 16, (0.94, 0.90, 0.66))
    koerper = T.scratches(r, koerper, 45, 0.08, 40, direction=0.0)

    zuender = T.base(r, H, H, ZUENDER, 0.040, scale=5)
    zuender = T.mottle(r, zuender, 0.024, 9, (0.85, 0.85, 0.80))
    zuender = T.scratches(r, zuender, 70, 0.13, 26, direction=0.0)

    huelse = T.base(r, H, H, MESSING, 0.036, scale=6)
    huelse = T.mottle(r, huelse, 0.030, 12, (1.00, 0.88, 0.58))
    huelse = T.scratches(r, huelse, 90, 0.16, 34, direction=0.0)

    ring = T.base(r, H, H, KUPFER, 0.038, scale=5)
    ring = T.mottle(r, ring, 0.030, 8, (1.00, 0.80, 0.55))
    ring = T.scratches(r, ring, 80, 0.18, 24, direction=0.0)

    staub = (0.44, 0.39, 0.26)
    koerper = T.couple_height(koerper, h_koerper, 0.10, 0.06, 13, staub, 0.028)
    zuender = T.couple_height(zuender, h_zuender, 0.13, 0.09, 10, staub, 0.030)
    huelse = T.couple_height(huelse, h_huelse, 0.11, 0.10, 11, staub, 0.026)
    ring = T.couple_height(ring, h_ring, 0.12, 0.12, 9, staub, 0.024)

    quads = {"shroud": koerper, "receiver": zuender,
             "stock": huelse, "detail": ring}
    T.save_atlas(quads, OUT_D)
    T.save_height_atlas(heights, OUT_N, strength=2.4)

    print("125-mm-Granate, Texturen")
    print("  %s" % OUT_D)
    print("    Geschoss RGB %s" % (T.mean_rgb(koerper),))
    print("    Huelse   RGB %s" % (T.mean_rgb(huelse),))
    print("  %s" % OUT_N)
