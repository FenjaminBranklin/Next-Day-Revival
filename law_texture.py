"""Erzeugt Diffuse, Normal Map und Metallic/Gloss-Map der M72 LAW.

    oben links   shroud    hinteres Startrohr, lackiertes Glasfaserlaminat
    oben rechts  receiver  vorderes Rohr, gleicher Lack
    unten links  stock     Gummiringe UND das Innere der Bohrung
    unten rechts detail    Blechteile, Visier, Warnbaender, Raketenkopf

WAS SICH GEAENDERT HAT (2026-08-30)
-----------------------------------
1. Die vier Viertel standen auf `texlib.base` mit `rough` um 0.042. Diese
   Funktion gewichtet die feinste Oktave am staerksten; auf jedem zweiten Pixel
   lag damit ein anderer Wert. Aus Armlaenge liest sich das als Raufasertapete,
   nicht als lackiertes Laminat. Jetzt `texlib.machined` ohne Polierzuege:
   weiche Grosstruktur, kaum Feinkorn - Lack ist glatt.

2. Neu ist `law_metal.png`. Lack ist KEIN Metall, deshalb stehen die beiden
   Rohrviertel auf Metallic 0.03; ihren Charakter bekommen sie ueber die
   Smoothness. Blechteile und Raketenkopf gehen deutlich hoeher, Gummi und
   Bohrung praktisch auf null.

3. Das Viertel "stock" traegt seit dem hohlen Rohr (siehe law_mesh.py) auch das
   Innere der Bohrung. Es ist deshalb dunkler und matter als vorher.
"""

import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageFilter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texlib as T

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
OUT_D = os.path.join(ASSETS, "law_diffuse.png")
OUT_N = os.path.join(ASSETS, "law_normal.png")
OUT_M = os.path.join(ASSETS, "law_metal.png")
OUT_R = os.path.join(ASSETS, "law_rough.png")
H = T.H
P = T.px


def stencil_mask(lines):
    mask = Image.new("L", (H, H), 0)
    d = ImageDraw.Draw(mask)
    try:
        font_big = ImageFont.truetype("DejaVuSans-Bold.ttf", P(17))
        font_small = ImageFont.truetype("DejaVuSans-Bold.ttf", P(10))
    except OSError:
        font_big = ImageFont.load_default()
        font_small = font_big
    for text, xy, big in lines:
        d.text((P(xy[0]), P(xy[1])), text, font=font_big if big else font_small,
               fill=235, stroke_width=P(1), stroke_fill=40)
    # Schmale Unterbrechungen machen aus der fetten Schrift eine Feldschablone.
    for x in range(P(35), H, P(43)):
        d.rectangle((x, P(18), x + P(2), P(52)), fill=0)
    return mask.filter(ImageFilter.GaussianBlur(T.PX * 0.35))


def apply_label(arr, mask, color):
    a = np.asarray(mask, np.float32)[..., None] / 255.0
    c = np.asarray(color, np.float32).reshape(1, 1, 3) / 255.0
    return np.clip(arr * (1.0 - a * 0.90) + c * a * 0.90, 0, 1)


def fiberglass(arr, depth=0.018, pitch=4):
    """Feines, gerichtetes Kreuzgewebe des glasfaserverstaerkten Startrohrs."""
    h, w, _ = arr.shape
    yy, xx = np.mgrid[0:h, 0:w]
    pp = float(P(pitch))
    weave = (np.sin((xx + yy * 0.42) * np.pi / pp)
             * np.sin((xx - yy * 0.42) * np.pi / pp))
    return np.clip(arr + weave[..., None] * depth, 0, 1)


def height_fiberglass(w, h, pitch=4):
    yy, xx = np.mgrid[0:h, 0:w]
    pp = float(P(pitch))
    return (0.018 * np.sin((xx + yy * 0.42) * np.pi / pp)
            * np.sin((xx - yy * 0.42) * np.pi / pp)).astype(np.float32)


def chipped_paint(r, arr, count=11):
    """Wenige kleine Abplatzer mit dunklem Rand und hellem Traegermaterial."""
    h, w, _ = arr.shape
    outer = Image.new("L", (w, h), 0)
    inner = Image.new("L", (w, h), 0)
    do, di = ImageDraw.Draw(outer), ImageDraw.Draw(inner)
    for _ in range(count):
        cx = int(r.integers(P(6), w - P(6)))
        cy = int(r.integers(P(6), h - P(6)))
        rr = r.uniform(P(1.5), P(4.0))
        points = []
        for j in range(int(r.integers(5, 9))):
            ang = 2.0 * np.pi * j / 7.0 + r.uniform(-0.28, 0.28)
            rad = rr * r.uniform(0.65, 1.20)
            points.append((cx + np.cos(ang) * rad, cy + np.sin(ang) * rad))
        do.polygon(points, fill=225)
        di.polygon([(cx + (x - cx) * 0.62, cy + (y - cy) * 0.62)
                    for x, y in points], fill=235)
    mo = np.asarray(outer.filter(ImageFilter.GaussianBlur(T.PX * 0.35)),
                    np.float32)[..., None] / 255.0
    mi = np.asarray(inner.filter(ImageFilter.GaussianBlur(T.PX * 0.25)),
                    np.float32)[..., None] / 255.0
    carrier = np.asarray((116, 114, 101), np.float32).reshape(1, 1, 3) / 255.0
    out = arr * (1.0 - mo * 0.42)
    return np.clip(out * (1.0 - mi * 0.92) + carrier * mi * 0.92, 0, 1)


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    r = T.rng(721162)

    label = stencil_mask([
        ("M72 LAW  66 MM HEAT", (16, 27), True),
    ])

    # Zuerst exakt die Hoehenkarten bauen, die spaeter gespeichert und fuer
    # Kavitaeten, Staub und blanke Kanten in der Diffuse benutzt werden.
    h_label = np.asarray(label, np.float32) / 255.0
    h_shroud = T.height_scratches(r, H, H, n=150, length=35)
    h_shroud = np.clip(h_shroud + height_fiberglass(H, H) + h_label * 0.08, 0, 1)
    h_receiver = T.height_rivets(r, H, H, rows=2, cols=8,
                                 seams=(18, 94, 172, 238))
    h_stock = T.height_checker(r, H, H, pitch=18, depth=0.08)
    h_detail = T.height_scratches(r, H, H, n=120, length=28)
    heights = {"shroud": h_shroud, "receiver": h_receiver,
               "stock": h_stock, "detail": h_detail}

    # Lack auf Laminat: fast gleichmaessig, mit weichem Wolkenspiel. Die
    # Kratzerzahlen sind gegenueber 0.5.4 gedrittelt - hundertzwanzig helle
    # Striche im Viertel sahen aus wie mit Stahlwolle gescheuert.
    shroud = T.machined(r, H, H, (84, 89, 51), unruhe=0.013, sheen=0.0)
    shroud = T.mottle(r, shroud, 0.016, 20, (0.90, 0.94, 0.72))
    shroud = fiberglass(shroud)
    shroud = T.scratches(r, shroud, 38, 0.055, 48, direction=2.0)
    shroud = apply_label(shroud, label, (205, 188, 104))
    shroud = chipped_paint(r, shroud)

    receiver = T.machined(r, H, H, (67, 73, 47), unruhe=0.013, sheen=0.0)
    receiver = T.mottle(r, receiver, 0.015, 18, (0.88, 0.94, 0.70))
    receiver = T.scratches(r, receiver, 30, 0.050, 42, direction=0.0)

    # Gummi UND Bohrung: dunkel, matt, ohne jede Zeichnung. Ein Rohrinneres
    # zeigt keine Kratzer - es zeigt gar nichts, es ist dunkel.
    rubber = T.machined(r, H, H, (30, 33, 28), unruhe=0.012, sheen=0.0)
    rubber = T.mottle(r, rubber, 0.012, 16, (0.75, 0.80, 0.66))
    rubber = T.scratches(r, rubber, 14, 0.030, 28, direction=0.0)

    detail = T.machined(r, H, H, (63, 63, 54), unruhe=0.014, sheen=0.006,
                        axis="v")
    detail = T.mottle(r, detail, 0.014, 16, (0.85, 0.84, 0.72))
    detail = T.scratches(r, detail, 34, 0.070, 40, direction=5.0)

    # Gelbe Warnbaender und roter Sicherungsakzent im Detailviertel.
    detail_img = Image.fromarray((detail * 255).astype(np.uint8))
    dd = ImageDraw.Draw(detail_img)
    dd.rectangle((P(12), P(196), P(244), P(210)), fill=(190, 168, 72))
    for x in range(P(18), P(244), P(28)):
        dd.line((x, P(196), x + P(14), P(210)), fill=(44, 46, 40), width=P(5))
    dd.rectangle((P(18), P(222), P(95), P(242)), fill=(132, 35, 28))
    detail = np.asarray(detail_img, np.float32) / 255.0

    dust = (0.46, 0.40, 0.23)
    shroud = T.couple_height(shroud, h_shroud, 0.09, 0.05, 12, dust, 0.0275)
    receiver = T.couple_height(receiver, h_receiver, 0.09, 0.05, 12, dust, 0.025)
    rubber = T.couple_height(rubber, h_stock, 0.09, 0.05, 10, dust, 0.0175)
    detail = T.couple_height(detail, h_detail, 0.09, 0.05, 10, dust, 0.0175)

    quads = {"shroud": shroud, "receiver": receiver,
             "stock": rubber, "detail": detail}
    T.save_atlas(quads, OUT_D)
    T.save_height_atlas(heights, OUT_N, strength=2.8)

    # ------------------------------------------- Metallic und Smoothness
    #
    # Der Lack ist ein Dielektrikum und bleibt es. Was die LAW im Licht
    # ausmacht, ist nicht Metallic, sondern die Smoothness: ein abgegriffenes
    # Startrohr glaenzt matt, das Gummi gar nicht, und die Blechteile - Visier,
    # Buegel, Schrauben, der Raketenkopf in der Bohrung - sind blanker Stahl.
    # Die LAW ist lackiertes Glasfaserrohr, kein blanker Stahl - nur die
    # Beschlaege sind Metall. Die Zahlen bleiben deshalb niedrig; was sich
    # 2026-08-30 geaendert hat, ist die ZWEITE Datei. Warum es zwei sind, steht
    # in texlib bei `save_rough_atlas`: der Shader der Waffen liest die
    # Smoothness nicht aus dem Alphakanal, sondern als Roughness aus einem
    # eigenen Slot, und ohne diese Datei war die LAW vollstaendig matt - was
    # bei einem Rohr besonders auffaellt, weil ein Rohr ohne Glanzstreifen
    # laengs der Achse flach aussieht.
    metall = {
        "shroud": T.gloss_quarter(r, 0.03, 0.32, 0.045),
        "receiver": T.gloss_quarter(r, 0.03, 0.30, 0.045),
        "stock": T.gloss_quarter(r, 0.02, 0.15, 0.030),
        "detail": T.gloss_quarter(r, 0.45, 0.48, 0.060),
    }
    T.save_gloss_atlas(metall, OUT_M)
    T.save_rough_atlas(metall, OUT_R)

    print("M72-LAW-Texturen")
    print("  %s  Oliv RGB %s" % (OUT_D, T.mean_rgb(shroud)))
    print("  %s" % OUT_N)
    print("  %s   R=Metallic, A=Smoothness" % OUT_M)
    print("  %s   R=Roughness = 1-Smoothness" % OUT_R)
    print("    Rohr      Metallic 0.03  Smoothness 0.32   (Lack, kein Metall)")
    print("    Bohrung   Metallic 0.02  Smoothness 0.15")
    print("    Blechteil Metallic 0.36  Smoothness 0.42")
