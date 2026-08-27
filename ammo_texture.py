"""Texturen der beiden neuen Munitionsitems.

Beide benutzen denselben Vier-Viertel-Atlas wie die Waffen, nur anders belegt:

    shroud    Geschossmantel und Gurtglieder   entsaettigtes Kupfer
    receiver  Kistenkoerper                    lackiertes Blech
    stock     Patronenhuelsen                  Messing
    detail    Deckel, Scharnier, Griff, Fuesse dunkles Metall

Der Farbklang trennt die beiden Items klar voneinander:
MG-Gurt = deutsches Feldgrau, .50-Kiste = olivgruen.
"""

import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texlib as T

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
H = T.H


def brass(r, rgb=(150, 118, 52)):
    """Messing: warm, leicht fleckig, mit feinen Ziehriefen laengs."""
    out = T.base(r, H, H, rgb, 0.05, scale=10)
    yy = np.arange(H)[:, None]
    band = (0.5 + 0.5 * np.sin(yy * 2.0 * np.pi / 5.0))[..., None].astype(np.float32)
    out = np.clip(out + (band - 0.5) * 0.05, 0, 1)
    return T.scratches(r, out, 30, 0.10, 26)


def painted(r, rgb, wear=42):
    """Lackiertes Blech mit abgeriebenen Kanten."""
    out = T.base(r, H, H, rgb, 0.05, scale=9)
    return T.scratches(r, out, wear, 0.16, 48)


def build(name, can_rgb, seed, label):
    r = T.rng(seed)
    quads = {
        "shroud":   T.base(r, H, H, (118, 86, 62), 0.05, scale=7),
        "receiver": painted(r, can_rgb),
        "stock":    brass(r),
        "detail":   T.base(r, H, H, (62, 63, 58), 0.05, scale=6),
    }
    quads["shroud"] = T.scratches(r, quads["shroud"], 40, 0.12, 30)
    quads["detail"] = T.scratches(r, quads["detail"], 80, 0.18, 40)

    out_d = os.path.join(ASSETS, name + "_diffuse.png")
    out_n = os.path.join(ASSETS, name + "_normal.png")
    T.save_atlas(quads, out_d)
    T.save_height_atlas({
        "shroud":   T.height_scratches(r, H, H, n=70, length=22),
        "receiver": T.height_rivets(r, H, H, rows=2, cols=7,
                                    seams=(20, 120, 232)),
        "stock":    T.height_scratches(r, H, H, n=50, length=18),
        "detail":   T.height_scratches(r, H, H, n=100, length=24),
    }, out_n, strength=2.4)

    print("%s" % label)
    print("  %s" % out_d)
    print("    Kiste     RGB %s" % (T.mean_rgb(quads["receiver"]),))
    print("    Messing   RGB %s" % (T.mean_rgb(quads["stock"]),))
    print("    Geschoss  RGB %s" % (T.mean_rgb(quads["shroud"]),))
    print("    Beschlaege RGB %s" % (T.mean_rgb(quads["detail"]),))
    print("  %s" % out_n)


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    build("mgbelt", (92, 96, 84), 2050, "MG-Gurt 200 Schuss (Feldgrau)")
    build("ammo50", (100, 102, 72), 2051, ".50-BMG-Kiste 10 Schuss (Oliv)")
