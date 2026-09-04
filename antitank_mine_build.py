"""Modell, Texturen und Icon der Panzerabwehrmine (TM-62-Bauart).

Eine flache Scheibenmine: olivgruener Blechkoerper, eingesetzter Deckel mit
zentralem Zuenderdom und Druckteller, Klappgriff, Randschrauben. Vier
Atlas-Viertel, wie bei den anderen Items:

    shroud    Minenkoerper (olivgruenes Blech)
    receiver  Deckel und Zuenderdom (dunkleres Blech)
    stock     Bodenring und Klappgriff (mattes Schwarz)
    detail    Deckelfase, Druckteller, Schrauben, Warnband und Schablonenschrift

    python antitank_mine_build.py

Danach build.ps1 und verify.py. Massstab wie die uebrigen Items: rund 1 Einheit
= 393.5 mm (BORE-Konvention der LAW), die TM-62 misst ~320 mm im Durchmesser.
Das im Spiel platzierte Objekt wird zusaetzlich im Code skaliert.
"""

import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageFilter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ndmesh import Mesh
import texlib as T
import iconlib

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HIER, "assets")

OUT_MESH = os.path.join(ASSETS, "mine.ndmesh")
OUT_D = os.path.join(ASSETS, "mine_diffuse.png")
OUT_N = os.path.join(ASSETS, "mine_normal.png")
OUT_M = os.path.join(ASSETS, "mine_metal.png")
OUT_R = os.path.join(ASSETS, "mine_rough.png")
OUT_ICON = os.path.join(ASSETS, "mine_icon.png")

R_BODY = 0.40          # Aussenradius Koerper
H_BODY = 0.12          # Hoehe des Koerpers
R_LID = 0.36           # Deckelradius
R_BOSS = 0.12          # Zuenderdom
H = T.H
P = T.px


def build():
    m = Mesh("Anti-tank mine")

    # Bodenring aus mattem Gummi/Schwarz (stock): ganz unten, schmal.
    m.tube(0.0, 0.0, -0.005, 0.018, R_BODY - 0.004, "stock", seg=48)

    # Der olivgruene Blechkoerper.
    m.tube(0.0, 0.0, 0.015, H_BODY, R_BODY, "shroud", seg=48)

    # Deckelfase: der umlaufende Rand, wo der Deckel im Koerper sitzt.
    m.crown(0.0, 0.0, H_BODY, H_BODY - 0.020, R_BODY, R_LID, "detail", seg=48)

    # Deckelscheibe (receiver): leicht vertieft eingesetzt.
    m.tube(0.0, 0.0, H_BODY - 0.020, H_BODY + 0.010, R_LID, "receiver", seg=48)

    # Zentraler Zuenderdom (receiver) und der Druckteller darueber (detail).
    m.tube(0.0, 0.0, H_BODY + 0.010, H_BODY + 0.075, R_BOSS, "receiver", seg=32)
    m.cone(0.0, 0.0, H_BODY + 0.075, H_BODY + 0.105, R_BOSS, R_BOSS + 0.02,
           "detail", seg=32)
    m.tube(0.0, 0.0, H_BODY + 0.105, H_BODY + 0.120, R_BOSS + 0.02,
           "detail", seg=32)

    # Randschrauben rund um den Deckel (detail): kleine Zylinder.
    import math
    for i in range(8):
        a = 2.0 * math.pi * i / 8.0
        x = math.cos(a) * (R_LID - 0.03)
        z = math.sin(a) * (R_LID - 0.03)
        m.tube(x, z, H_BODY - 0.012, H_BODY + 0.018, 0.022, "detail", seg=10)

    # Klappgriff (stock): eine flache Buegelplatte, die am Rand aufliegt.
    m.cbox(0.0, H_BODY + 0.020, R_BODY - 0.06, 0.055, 0.020, 0.140,
           "stock", c=0.006)
    m.cbox(0.0, H_BODY + 0.010, R_BODY - 0.135, 0.045, 0.030, 0.030,
           "stock", c=0.005)

    return m


# ---------------------------------------------------------------- Textur

def stencil(lines):
    mask = Image.new("L", (H, H), 0)
    d = ImageDraw.Draw(mask)
    try:
        font = ImageFont.truetype("DejaVuSans-Bold.ttf", P(13))
    except OSError:
        font = ImageFont.load_default()
    for text, xy in lines:
        d.text((P(xy[0]), P(xy[1])), text, font=font, fill=225,
               stroke_width=P(1), stroke_fill=35)
    return mask.filter(ImageFilter.GaussianBlur(T.PX * 0.35))


def build_textures():
    r = T.rng(72065)

    # Hoehenkarten je Viertel.
    h_shroud = T.height_scratches(r, H, H, n=80, length=26)
    h_receiver = T.height_scratches(r, H, H, n=60, length=20)
    h_stock = T.height_checker(r, H, H, pitch=20, depth=0.06)
    h_detail = T.height_rivets(r, H, H, rows=2, cols=8, seams=(24, 232))
    heights = {"shroud": h_shroud, "receiver": h_receiver,
               "stock": h_stock, "detail": h_detail}

    # Olivgruener Minenkoerper.
    shroud = T.machined(r, H, H, (74, 82, 52), unruhe=0.014, sheen=0.0)
    shroud = T.mottle(r, shroud, 0.016, 20, (0.90, 0.94, 0.72))
    shroud = T.scratches(r, shroud, 30, 0.050, 40, direction=0.0)

    # Deckel/Zuenderdom: dunkleres Blech.
    receiver = T.machined(r, H, H, (60, 66, 46), unruhe=0.013, sheen=0.004)
    receiver = T.mottle(r, receiver, 0.014, 18, (0.86, 0.92, 0.70))
    receiver = T.scratches(r, receiver, 24, 0.045, 34, direction=2.0)

    # Bodenring/Griff: mattes Schwarz.
    stock = T.machined(r, H, H, (30, 32, 28), unruhe=0.012, sheen=0.0)
    stock = T.mottle(r, stock, 0.012, 16, (0.75, 0.80, 0.66))

    # Detail: Blech mit Warnband und Schablonenschrift.
    detail = T.machined(r, H, H, (66, 66, 56), unruhe=0.014, sheen=0.006,
                        axis="v")
    detail = T.mottle(r, detail, 0.014, 16, (0.85, 0.84, 0.72))
    detail = T.scratches(r, detail, 26, 0.060, 34, direction=5.0)

    label = stencil([("TM-62  9 KG", (14, 24)), ("HEAT AT MINE", (14, 78))])
    detail_img = Image.fromarray((detail * 255).astype(np.uint8))
    dd = ImageDraw.Draw(detail_img)
    # Gelbes Warnband.
    dd.rectangle((P(10), P(150), P(244), P(166)), fill=(190, 168, 72))
    for x in range(P(14), P(244), P(26)):
        dd.line((x, P(150), x + P(12), P(166)), fill=(44, 46, 40), width=P(4))
    detail = np.asarray(detail_img, np.float32) / 255.0
    a = np.asarray(label, np.float32)[..., None] / 255.0
    c = np.asarray((210, 200, 120), np.float32).reshape(1, 1, 3) / 255.0
    detail = np.clip(detail * (1.0 - a * 0.85) + c * a * 0.85, 0, 1)

    dust = (0.46, 0.40, 0.23)
    shroud = T.couple_height(shroud, h_shroud, 0.09, 0.05, 12, dust, 0.026)
    receiver = T.couple_height(receiver, h_receiver, 0.09, 0.05, 12, dust, 0.024)
    stock = T.couple_height(stock, h_stock, 0.09, 0.05, 10, dust, 0.017)
    detail = T.couple_height(detail, h_detail, 0.09, 0.05, 10, dust, 0.017)

    quads = {"shroud": shroud, "receiver": receiver,
             "stock": stock, "detail": detail}
    T.save_atlas(quads, OUT_D)
    T.save_height_atlas(heights, OUT_N, strength=2.8)

    metall = {
        "shroud": T.gloss_quarter(r, 0.05, 0.30, 0.045),
        "receiver": T.gloss_quarter(r, 0.10, 0.34, 0.045),
        "stock": T.gloss_quarter(r, 0.02, 0.12, 0.030),
        "detail": T.gloss_quarter(r, 0.45, 0.46, 0.060),
    }
    T.save_gloss_atlas(metall, OUT_M)
    T.save_rough_atlas(metall, OUT_R)
    return shroud


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    mesh = build()
    mesh.write(OUT_MESH)
    mesh.report(OUT_MESH)

    shroud = build_textures()
    print("Panzerabwehrmine-Texturen")
    print("  %s  Oliv RGB %s" % (OUT_D, T.mean_rgb(shroud)))

    # Das Icon aus einer GEDREHTEN Kopie rendern. iconlib blickt fest entlang
    # der X-Achse und kann mit yaw/pitch nur um Y und X drehen - die Deckel-
    # normale (weltweit +Y) laesst sich damit NIE zur Kamera drehen, eine flache
    # Scheibe bliebe immer hochkant. Deshalb die Mine fuer das Icon um 90 Grad um
    # Z kippen, sodass die Deckelnormale entlang -X zeigt (zur Kamera); die
    # eigentliche mine.ndmesh bleibt Y-oben, damit das im Spiel platzierte Objekt
    # flach auf dem Boden liegt.
    icon_mesh = build()
    icon_mesh.V = [(-p[1], p[0], p[2]) for p in icon_mesh.V]
    icon_mesh.N = [(-n[1], n[0], n[2]) for n in icon_mesh.N]
    tmp = os.path.join(ASSETS, "_mine_icon_tmp.ndmesh")
    icon_mesh.write(tmp)
    iconlib.item_icon(tmp, OUT_D, OUT_ICON, size=300, tilt=0.0,
                      yaw=0.55, pitch=0.55, margin=0.88)
    os.remove(tmp)
    print("Panzerabwehrmine-Icon")
    iconlib.report(OUT_ICON)
