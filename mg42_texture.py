"""Erzeugt Diffuse, Normal Map und Metallic/Gloss-Map des MG42.

Die vier Viertel gehoeren zu den UV-Regionen aus mg42_mesh.py:

    oben links   shroud    Laufmantel mit Kuehlbohrungen
    oben rechts  receiver  bruenierter Stahl, Nietenreihen
    unten links  stock     Bakelit fuer Griff und Schaft
    unten rechts detail    dunkles Metall fuer Kleinteile

WAS SICH GEAENDERT HAT (2026-08-30)
-----------------------------------
Der Befund des Benutzers war "sieht extrem nach Raufasertapete aus, das soll
eher metallisch aussehen wie der aktuelle Panzer". Beides trifft zu, und beides
hat eine eigene Ursache.

1. DIE TAPETE kam aus `texlib.base`. Diese Funktion gewichtet absichtlich die
   feinste Oktave am staerksten - richtig fuer Lack und Bakelit, falsch fuer
   Stahl. Mit `rough` 0.041 lag auf jedem zweiten Pixel ein anderer Wert, rund
   plus/minus zehn Stufen. Aus einem halben Meter, und naeher steht die Waffe in
   der ersten Person nie, ist das genau die Koernung einer Raufasertapete.
   Stahl und Bakelit standen daraufhin auf `texlib.machined`: weiche
   Grosstruktur, kaum Feinkorn, dazu schwache gerichtete Polierzuege.

2. DER PANZER sieht metallisch aus, weil er als einziges Stueck des Toolkits
   eine `_MetallicGlossMap` traegt (seit 0.5.2, gemessen am MTW). Die Waffen
   standen dagegen auf `_Metallic` 0.0 - also auf Nichtmetall, egal wie das
   Diffusebild aussieht. Ab jetzt liegt auch neben dem MG42 eine solche Map.

   Die Zahlen bleiben massvoll und in der Groessenordnung des MTW (Metallic
   0.15, Smoothness 0.40). Ein Metallic nahe 1.0 nimmt seine Farbe komplett aus
   der Umgebungsspiegelung; im Keller waere die Waffe dann schwarz.

WAS DER ZWEITE ANLAUF UEBERSAH (2026-08-30, zweiter Befund desselben Tages)
--------------------------------------------------------------------------
Der Benutzer meldete dieselbe Raufasertapete noch einmal, mit der Karte im
Spiel und geladen ("Metall-Map=mg42_metal.png" im Log). Punkt 2 oben war also
nur die halbe Rechnung, und die andere Haelfte ist der SHADER.

`resources.assets`, alle 1708 Materialien nach ihren Texturslots sortiert:

    Shader 56  Standard                    nur _MetallicGlossMap       773
    Shader 55  Standard (Specular setup)   nur _SpecGlossMap           466
    Shader 57  Standard (Roughness setup)  BEIDE                       165

Der Panzer erbt sein Material vom MTW und landet auf 56. Dort IST die
Smoothness der Alphakanal der Metallic-Map, und alles stimmt. Die Waffen erben
ihr Material von ihrer Spende-Waffe, und das Log sagt fuer 1160, 1161 und 1162
dasselbe: `Standard (Roughness setup)`, also 57. In dieser Fassung kommt die
Smoothness nicht aus dem Alpha, sondern als ROUGHNESS aus `_SpecGlossMap` -
ein Slot, den niemand gesetzt hat. Sein Vorgabewert ist "white": Roughness 1.0,
Smoothness 0, kein Glanzlicht auf der ganzen Waffe. Das Metallic war die ganze
Zeit richtig; es gab nur nichts zu spiegeln.

Zwei Dinge folgen daraus, und beide stehen jetzt hier:

  a) `mg42_rough.png` wird mitgeschrieben - dieselbe Aussage wie der
     Alphakanal, nur umgekehrt. Das Plugin setzt sie in `_SpecGlossMap`.
     Zusaetzlich nimmt es fuer eigene Waffen den Shader `Standard`, denselben
     wie der Panzer; die Roughness-Map ist die Absicherung fuer den Fall, dass
     das nicht gelingt.

  b) DAS ALBEDO WIRD RUHIGER, nicht bunter. Solange es kein Glanzlicht gab,
     musste das Diffusebild die ganze Oberflaeche allein tragen, und alles, was
     man dafuer hineinmalt, sind Farbwolken. Bruenierter Stahl ist im Albedo
     fast einfarbig; was man an ihm sieht, ist Glanz. Die Metallviertel stehen
     deshalb auf `texlib.gunmetal` statt `machined`, die grobe Wolkenoktave ist
     gedrittelt, `mottle` ist auf den Schleier reduziert, den ein Rohr
     wirklich hat, und die gerichteten Polierzuege sind jetzt das staerkste
     Element der Flaeche.

3. Der Laufmantel stand auf RGB (92, 94, 98) - fuer bruenierten Blechmantel
   deutlich zu hell, er stach gegen das Gehaeuse heraus. Jetzt (74, 76, 81),
   also nur noch eine Spur heller als das Gehaeuse, so wie es der Abrieb macht.

Die Normal Map entsteht hier mit, statt in einem zweiten Skript: sie muss zum
selben Atlas passen; zwei Dateien mit getrennten Zufallszahlen driften
auseinander, sobald man an einem Viertel etwas aendert.
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
OUT_M = os.path.join(ASSETS, "mg42_metal.png")
OUT_R = os.path.join(ASSETS, "mg42_rough.png")

r = T.rng(4242)
H = T.H
P = T.px

# Bruenierter Stahl. Alle drei Metallviertel liegen dicht beieinander - eine
# Waffe ist aus einem Werkstoff gebaut, nicht aus drei verschiedenen Graus.
STAHL_MANTEL = (74, 76, 81)
STAHL_GEHAEUSE = (64, 66, 72)
STAHL_KLEINTEIL = (50, 52, 57)
BAKELIT = (55, 39, 34)


def heat_tint(arr, height):
    """Sehr dezenter braun-violetter Anlasshof um die Kuehlbohrungen."""
    holes = np.clip((0.36 - height) / 0.32, 0, 1)
    img = Image.fromarray((holes * 255).astype(np.uint8))
    halo = np.asarray(img.filter(ImageFilter.GaussianBlur(P(6))), np.float32) / 255.0
    ring = np.clip(halo - holes * 0.72, 0, 1)[..., None]
    shift = np.asarray((0.030, -0.016, 0.022), np.float32).reshape(1, 1, 3)
    return np.clip(arr + ring * shift, 0, 1)


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
#
# Die Kratzerzahlen sind gegenueber 0.5.4 rund halbiert und die Helligkeit
# gedrittelt. Ein Kratzer auf bruenierter Ware ist ein duenner, blanker Strich,
# kein weisser Pinselstrich - und wo frueher achtzig davon lagen, sah das
# Viertel gebuerstet aus statt gebraucht.
shroud = T.gunmetal(r, H, H, STAHL_MANTEL, unruhe=0.006, sheen=0.013, axis="u")
shroud = T.mottle(r, shroud, 0.004, 20, (0.92, 0.94, 1.0))
shroud = T.perforation(r, shroud, step=30, radius=9, layout=holes)
shroud = heat_tint(shroud, h_shroud)
shroud = T.scratches(r, shroud, 26, 0.055, 38, direction=90.0)
shroud = T.couple_height(shroud, h_shroud, 0.07, 0.04, 10)

receiver = T.gunmetal(r, H, H, STAHL_GEHAEUSE, unruhe=0.006, sheen=0.012, axis="u")
receiver = T.mottle(r, receiver, 0.004, 22, (0.86, 0.90, 1.0))
receiver = T.scratches(r, receiver, 32, 0.050, 50, direction=0.0)
receiver = T.rivets(r, receiver, rows=3, cols=9, layout=rivet_points)
receiver = T.couple_height(receiver, h_receiver, 0.05, 0.03, 11)
# Blanke Grate an den Nietenkoepfen und Fugen: dort ist die Bruenierung ab.
receiver = T.wear_edges(receiver, h_receiver, (112, 118, 132), 0.10)

# Bakelit bleibt Bakelit - aber `texlib.bakelite` steht selbst auf `base` und
# brachte damit dieselbe Koernung mit. Hier deshalb von Hand aufgebaut: matter
# Grundton, fleckige Marmorierung, kein Feinkorn.
stock = T.machined(r, H, H, BAKELIT, unruhe=0.013, sheen=0.0)
marmor = (T.grain(r, H, H, 20, 0.024) + T.grain(r, H, H, 8, 0.014)
          + T.grain(r, H, H, 3, 0.007))
stock = np.clip(stock + marmor[..., None] * np.array([1.0, 0.74, 0.66]), 0, 1)
stock = hand_wear(stock)
stock = T.scratches(r, stock, 12, 0.035, 34)
stock = T.couple_height(stock, h_stock, 0.09, 0.05, 9)

detail = T.gunmetal(r, H, H, STAHL_KLEINTEIL, unruhe=0.006, sheen=0.014, axis="v")
detail = T.mottle(r, detail, 0.004, 18, (0.88, 0.92, 1.0))
detail = T.scratches(r, detail, 34, 0.065, 42, direction=4.0)
detail = T.couple_height(detail, h_detail, 0.05, 0.03, 9)

os.makedirs(ASSETS, exist_ok=True)
T.save_atlas({"shroud": shroud, "receiver": receiver,
              "stock": stock, "detail": detail}, OUT_D)

print("MG42-Diffuse: %s  (%dx%d)" % (OUT_D, T.S, T.S))
print("  oben links   Laufmantel, Mittelwert RGB %s   (vorher 92,94,98)"
      % (T.mean_rgb(shroud),))
print("  oben rechts  Gehaeuse,   Mittelwert RGB %s" % (T.mean_rgb(receiver),))
print("  unten links  Bakelit,    Mittelwert RGB %s" % (T.mean_rgb(stock),))
print("  unten rechts Kleinteile, Mittelwert RGB %s" % (T.mean_rgb(detail),))

# Bricht das Licht pro Pixel: Kuehlbohrungen, Nietenkoepfe, Fugen und die
# Fischhaut am Griff bekommen einen eigenen Glanz, ohne ein zusaetzliches Dreieck.
T.save_height_atlas(heights, OUT_N, strength=3.2)
print("MG42-Normal : %s" % OUT_N)

# ------------------------------------------------- Metallic und Smoothness
#
# Der Laufmantel ist matter als das Gehaeuse: er wird heiss, traegt Anlauffarben
# und ist rundum abgegriffen. Die Kleinteile - Muendungsbooster, Visier,
# Zweibein - sind blanker Stahl und bekommen den hoechsten Wert. Bakelit ist
# kein Metall; es steht auf Metallic 0.02 und einer maessigen Smoothness, denn
# gepresstes Kunstharz glaenzt schwach, aber es glaenzt.
metall = {
    "shroud": T.gloss_quarter(r, 0.62, 0.46, 0.055),
    "receiver": T.gloss_quarter(r, 0.70, 0.54, 0.050),
    "stock": T.gloss_quarter(r, 0.02, 0.34, 0.040),
    "detail": T.gloss_quarter(r, 0.78, 0.60, 0.060),
}
T.save_gloss_atlas(metall, OUT_M)
T.save_rough_atlas(metall, OUT_R)
print("MG42-Metal  : %s   R=Metallic, A=Smoothness  (Shader Standard)" % OUT_M)
print("MG42-Rough  : %s   R=Roughness = 1-Smoothness (Shader Roughness setup)"
      % OUT_R)
print("    Mantel   Metallic 0.62  Smoothness 0.46")
print("    Gehaeuse Metallic 0.70  Smoothness 0.54")
print("    Bakelit  Metallic 0.02  Smoothness 0.34")
print("    Kleinteil Metallic 0.78 Smoothness 0.60")
print()
print("  Die Metallic-Werte liegen deutlich ueber dem MTW (0.15). Das ist")
print("  Absicht: der MTW ist LACKIERTES Blech, eine bruenierte Waffe ist")
print("  blanker Stahl unter einer Oxidschicht. Wird die Waffe im dunklen")
print("  Innenraum zu schwarz, sind diese vier Zahlen die Stellschraube.")
