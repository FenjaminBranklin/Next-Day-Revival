"""Erzeugt Diffuse und Normal Map fuer die FPV-Drohne."""

import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texlib as T

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
OUT_D = os.path.join(ASSETS, "drone_diffuse.png")
OUT_N = os.path.join(ASSETS, "drone_normal.png")
H = T.H


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    r = T.rng(1163)
    shroud = T.base(r, H, H, (18, 20, 22), 0.030, scale=5)
    shroud = T.scratches(r, shroud, 35, 0.08, 22)
    receiver = T.base(r, H, H, (43, 47, 51), 0.035, scale=7)
    receiver = T.scratches(r, receiver, 35, 0.08, 26)
    stock = T.base(r, H, H, (57, 61, 64), 0.045, scale=8)
    stock = T.scratches(r, stock, 50, 0.09, 30)
    detail = T.steel(r, H, H, (69, 73, 78), scratch_n=95)

    im = Image.fromarray((receiver * 255).astype(np.uint8))
    d = ImageDraw.Draw(im)
    try:
        font_large = ImageFont.truetype("DejaVuSans-Bold.ttf", 32)
        font_small = ImageFont.truetype("DejaVuSans-Bold.ttf", 22)
    except OSError:
        font_large = ImageFont.load_default()
        font_small = font_large
    accent = (238, 84, 37)
    shadow = (24, 26, 28)
    d.text((22, 30), "RAVEN-1163", font=font_large, fill=accent,
           stroke_width=1, stroke_fill=shadow)
    d.text((24, 78), "ARMED - KEEP CLEAR", font=font_small, fill=accent,
           stroke_width=1, stroke_fill=shadow)
    d.rectangle((20, 124, 236, 139), fill=accent)
    d.rectangle((20, 151, 168, 160), fill=(205, 66, 30))
    receiver = np.asarray(im, np.float32) / 255.0

    T.save_atlas({"shroud": shroud, "receiver": receiver,
                  "stock": stock, "detail": detail}, OUT_D)
    T.save_height_atlas({
        "shroud": T.height_scratches(r, H, H, n=45, length=20),
        "receiver": T.height_checker(r, H, H, pitch=12, depth=0.08),
        "stock": T.height_scratches(r, H, H, n=55, length=24),
        "detail": T.height_rivets(r, H, H, rows=3, cols=8,
                                   seams=(30, 128, 226)),
    }, OUT_N, strength=2.4)
    print("FPV-Drohnen-Texturen")
    print("  %s" % OUT_D)
    print("  %s" % OUT_N)
