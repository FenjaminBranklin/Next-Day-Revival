"""Erzeugt Diffuse- und Normal-Textur der .50 zu den UV-Regionen aus sniper50_mesh.py.

    oben links   shroud    Lauf und Handschutz, geriffelter Stahl
    oben rechts  receiver  Gehaeuse, matt eloxiertes Aluminium
    unten links  stock     Griff und Wangenauflage, mattes Polymer
    unten rechts detail    Muendungsbremse, Zielfernrohr, Kleinteile - fast schwarz

Bewusst anders als das MG42: das MG42 ist bruenierter Stahl mit braunem Bakelit,
die .50 ist ein modernes Chassis-Gewehr - kuehle Grautoene, kein Braun. So sind
die beiden Waffen schon am Farbklang auseinanderzuhalten.

SIE HATTE KEINE METALLKARTE, UND DAS WAR DER GROSSTE EINZELFEHLER
-----------------------------------------------------------------
Hier stand bis 2026-08-30: "Keine Metallic/Smoothness-Map, aus demselben Grund
wie beim MG42: kein einziges Spielmaterial benutzt eine". Der Satz war schon
fuer das MG42 ueberholt (der MTW hat eine, `dump_material.py btr-80a_alb`) und
fuer die .50 hatte er die Folge, die im Log stand:

    1161: Material Metallic=0 Glossiness=0,6 ... Metall-Map=keine

Metallic 0 heisst Nichtmetall. Ein Scharfschuetzengewehr aus lackiertem Stahl
sah damit aus wie ein Gewehr aus Karton, und keine noch so gute Diffusetextur
kann das ausgleichen - Metall erkennt das Auge am Glanzverhalten, nicht an der
Farbe. Jetzt liegen `sniper50_metal.png` und `sniper50_rough.png` daneben; was
die zweite Datei soll, steht in texlib bei `save_rough_atlas`.

Zusammen damit ist die Grundflaeche der drei Metallviertel von `texlib.base`
auf `texlib.gunmetal` umgestellt. `base` gewichtet die feinste Oktave am
staerksten - richtig fuer Lack und Polymer, falsch fuer Stahl, und in
Verbindung mit `mottle` bei 0.022 war das dieselbe Raufasertapete, die der
Benutzer am MG42 gemeldet hatte. Das Polymer des Griffs bleibt auf `base`; es
IST koernig.
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
OUT_M = os.path.join(ASSETS, "sniper50_metal.png")
OUT_R = os.path.join(ASSETS, "sniper50_rough.png")

r = T.rng(50500)
H = T.H
P = T.px


def flute_profile(w, pitch):
    pitch_px = float(P(pitch))
    phase = np.mod(np.arange(w, dtype=np.float32), pitch_px) / pitch_px
    distance = np.abs(phase - 0.5) * 2.0
    flank = np.clip((distance - 0.24) / 0.18, 0, 1)
    return flank * flank * (3.0 - 2.0 * flank)


def flutes(arr, pitch=22, depth=0.10):
    """Gefraeste Laengsriefen mit flacher Sohle und steilen Flanken."""
    h, w, _ = arr.shape
    profile = flute_profile(w, pitch)
    relief = (profile - profile.mean())[None, :, None]
    return np.clip(arr + relief * depth, 0, 1)


def machining(arr, pitch=9, depth=0.05):
    """Feine waagerechte Drehriefen, wie sie gefraestes Aluminium zeigt."""
    h, w, _ = arr.shape
    yy = np.arange(h)[:, None]
    band = (0.5 + 0.5 * np.sin(yy * 2.0 * np.pi / P(pitch)))[..., None].astype(np.float32)
    return np.clip(arr + (band - 0.5) * depth, 0, 1)


def brushed(r, arr, amount=0.030, pitch=3):
    """Sehr feine, ueberwiegend waagerechte Buerstspuren im Eloxal."""
    h, w, _ = arr.shape
    yy = np.arange(h, dtype=np.float32)[:, None]
    band = np.zeros((h, 1), np.float32)
    for mul, gain in ((1.0, 1.0), (1.73, 0.45), (2.41, 0.25)):
        band += np.sin(yy * 2.0 * np.pi / (P(pitch) * mul)
                       + r.random() * 2.0 * np.pi) * gain
    band /= max(1.0e-6, float(band.std()))
    variation = 0.70 + T.grain(r, w, h, 18, 0.16)
    return np.clip(arr + band[..., None] * variation[..., None] * amount, 0, 1)


def height_flutes(w, h, pitch=22):
    profile = flute_profile(w, pitch)[None, :]
    a = 0.36 + 0.28 * profile
    return np.repeat(a, h, axis=0).astype(np.float32) + T.grain(r, w, h, 6, 0.03)


def height_machining(w, h, pitch=9):
    yy = np.arange(h)[:, None]
    a = 0.5 + 0.10 * np.sin(yy * 2.0 * np.pi / P(pitch))
    return np.repeat(a, w, axis=1).astype(np.float32) + T.grain(r, w, h, 5, 0.04)


def height_stipple(r, w, h, count=1800):
    """Dichte Polymernarbung; in der Diffuse nur ueber die Kavitaeten sichtbar."""
    img = Image.new("L", (w, h), 112)
    d = ImageDraw.Draw(img)
    for _ in range(count):
        cx, cy = int(r.integers(0, w)), int(r.integers(0, h))
        rr = r.uniform(P(0.7), P(1.5))
        fill = int(r.integers(155, 226))
        for ox in (-w, 0, w):
            for oy in (-h, 0, h):
                d.ellipse((cx + ox - rr, cy + oy - rr,
                           cx + ox + rr, cy + oy + rr), fill=fill)
    a = np.asarray(img.filter(ImageFilter.GaussianBlur(T.PX * 0.28)),
                   np.float32) / 255.0
    return a + T.grain(r, w, h, 5, 0.035)


def anodize_wear(arr, height, strength=0.34):
    """Nur die hoechsten gefraesten Kanten bis zum hellen Aluminium abreiben."""
    local = T.periodic_blur(height, P(10) / 3.0)
    high = np.clip(height - local, 0, None)
    lo = float(np.percentile(high, 84.0))
    hi = max(lo + 1.0e-5, float(np.percentile(high, 98.5)))
    mask = np.clip((high - lo) / (hi - lo), 0, 1)[..., None]
    aluminum = np.asarray((142, 146, 153), np.float32).reshape(1, 1, 3) / 255.0
    mix = mask * strength
    return np.clip(arr * (1.0 - mix) + aluminum * mix, 0, 1)


# --------------------------------------------------------------- Normal Map
# Zuerst die Hoehenkarten; exakt dieselben Arrays treiben danach Kavitaeten und
# Kantenabrieb in der Diffuse und werden anschliessend als Normal Map gespeichert.
h_shroud = height_flutes(H, H, pitch=22)
h_receiver = height_machining(H, H, pitch=9)
h_receiver = np.clip(h_receiver + T.grain(r, H, H, 2, 0.018), 0, 1)
h_stock = height_stipple(r, H, H)
h_detail = T.height_scratches(r, H, H, n=110, length=26)
heights = {"shroud": h_shroud, "receiver": h_receiver,
           "stock": h_stock, "detail": h_detail}

# ------------------------------------------------------------------ Diffuse
shroud = T.gunmetal(r, H, H, (65, 67, 71), unruhe=0.007, sheen=0.012, axis="u")
shroud = T.mottle(r, shroud, 0.005, 12, (0.90, 0.94, 1.0))
shroud = flutes(shroud, pitch=22, depth=0.05)
shroud = T.scratches(r, shroud, 58, 0.12, 42, direction=90.0)
shroud = T.couple_height(shroud, h_shroud, 0.09, 0.05, 11)

receiver = T.gunmetal(r, H, H, (81, 83, 87), unruhe=0.006, sheen=0.011, axis="u")
receiver = T.mottle(r, receiver, 0.005, 13, (0.88, 0.94, 1.0))
receiver = machining(receiver, pitch=9, depth=0.025)
receiver = brushed(r, receiver, 0.014, pitch=3)
receiver = T.scratches(r, receiver, 65, 0.10, 46, direction=0.0)
receiver = T.couple_height(receiver, h_receiver, 0.09, 0.05, 10)
receiver = anodize_wear(receiver, h_receiver, 0.14)

# Polymer: matt, minimal gruenstichig, sehr feine Koernung - kein Bakelit.
stock = T.base(r, H, H, (58, 60, 56), 0.034, scale=9)
stock = T.mottle(r, stock, 0.014, 11, (0.86, 0.91, 0.84))
stock = T.scratches(r, stock, 22, 0.055, 28, direction=0.0)
stock = T.couple_height(stock, h_stock, 0.09, 0.05, 6)

detail = T.gunmetal(r, H, H, (36, 37, 40), unruhe=0.006, sheen=0.013, axis="v")
detail = T.mottle(r, detail, 0.005, 10, (0.85, 0.90, 1.0))
detail = T.scratches(r, detail, 82, 0.13, 36, direction=5.0)
detail = T.couple_height(detail, h_detail, 0.09, 0.05, 8)

os.makedirs(ASSETS, exist_ok=True)
T.save_atlas({"shroud": shroud, "receiver": receiver,
              "stock": stock, "detail": detail}, OUT_D)

print("Sniper50-Diffuse: %s  (%dx%d)" % (OUT_D, T.S, T.S))
print("  oben links   Lauf geriffelt,  Mittelwert RGB %s" % (T.mean_rgb(shroud),))
print("  oben rechts  Chassis,         Mittelwert RGB %s" % (T.mean_rgb(receiver),))
print("  unten links  Polymer,         Mittelwert RGB %s" % (T.mean_rgb(stock),))
print("  unten rechts Optik/Kleinteile,Mittelwert RGB %s" % (T.mean_rgb(detail),))

T.save_height_atlas(heights, OUT_N, strength=2.6)
print("Sniper50-Normal : %s" % OUT_N)

# ------------------------------------------------- Metallic und Smoothness
#
# Die .50 ist nicht bruenierter Stahl wie das MG42, sondern lackiertes und
# eloxiertes Metall - also dieselbe Metallizitaet, aber deutlich weniger
# Glanz. Der Lauf ist blanker als das Chassis (er wird nicht angefasst), die
# Optik ist das glaenzendste Teil der Waffe, das Polymer des Griffs ist kein
# Metall.
metall = {
    "shroud": T.gloss_quarter(r, 0.60, 0.40, 0.050),
    "receiver": T.gloss_quarter(r, 0.55, 0.32, 0.045),
    "stock": T.gloss_quarter(r, 0.02, 0.24, 0.035),
    "detail": T.gloss_quarter(r, 0.68, 0.50, 0.055),
}
T.save_gloss_atlas(metall, OUT_M)
T.save_rough_atlas(metall, OUT_R)
print("Sniper50-Metal  : %s   R=Metallic, A=Smoothness" % OUT_M)
print("Sniper50-Rough  : %s   R=Roughness = 1-Smoothness" % OUT_R)
print("    Lauf     Metallic 0.60  Smoothness 0.40")
print("    Chassis  Metallic 0.55  Smoothness 0.32   (matt lackiert)")
print("    Polymer  Metallic 0.02  Smoothness 0.24")
print("    Optik    Metallic 0.68  Smoothness 0.50")
