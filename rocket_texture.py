"""Erzeugt Diffuse und Normal Map fuer das LAW-Raketen-Packrohr."""

import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texlib as T

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
OUT_D = os.path.join(ASSETS, "rocket_diffuse.png")
OUT_N = os.path.join(ASSETS, "rocket_normal.png")
H = T.H


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    r = T.rng(2052)
    tube = T.base(r, H, H, (83, 87, 50), 0.055, scale=9)
    tube = T.scratches(r, tube, 110, 0.16, 42)
    rocket = T.base(r, H, H, (92, 96, 63), 0.045, scale=8)
    rocket = T.scratches(r, rocket, 55, 0.10, 32)
    rubber = T.base(r, H, H, (29, 32, 27), 0.035, scale=5)
    detail = T.steel(r, H, H, (51, 53, 47), scratch_n=75)

    im = Image.fromarray((tube * 255).astype(np.uint8))
    d = ImageDraw.Draw(im)
    try:
        font = ImageFont.truetype("DejaVuSans-Bold.ttf", 28)
    except OSError:
        font = ImageFont.load_default()
    d.text((25, 38), "M72  66 MM", font=font, fill=(205, 190, 104),
           stroke_width=1, stroke_fill=(40, 43, 30))
    d.text((28, 82), "HEAT ROCKET", font=font, fill=(205, 190, 104),
           stroke_width=1, stroke_fill=(40, 43, 30))
    d.rectangle((20, 132, 236, 146), fill=(190, 168, 72))
    tube = np.asarray(im, np.float32) / 255.0

    T.save_atlas({"shroud": tube, "receiver": rocket,
                  "stock": rubber, "detail": detail}, OUT_D)
    T.save_height_atlas({
        "shroud": T.height_scratches(r, H, H, n=100, length=28),
        "receiver": T.height_scratches(r, H, H, n=60, length=20),
        "stock": T.height_checker(r, H, H, pitch=18, depth=0.07),
        "detail": T.height_rivets(r, H, H, rows=2, cols=7,
                                  seams=(28, 126, 228)),
    }, OUT_N, strength=2.5)
    print("M72-Raketenpack-Texturen")
    print("  %s" % OUT_D)
    print("  %s" % OUT_N)
