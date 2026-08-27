"""Erzeugt das Zielfernrohr-Overlay fuer die .50 - ein eigenes, schlichtes Design.

WIE DAS SPIEL DAS BILD BENUTZT (aus dem IL gelesen)
---------------------------------------------------
    ScopeCameraEffect::Update
        scopePosition = new Rect(0, 0, Screen.width, Screen.height)
    ScopeCameraEffect::OnGUI
        GUI.DrawTexture(scopePosition, scopeTexture, ScaleMode 1)

ScaleMode 1 ist ScaleAndCrop: die Textur wird proportional so vergroessert, dass
sie den Bildschirm vollstaendig bedeckt, und der Ueberstand wird abgeschnitten.
Bei einem quadratischen Bild auf 16:9 heisst das: die Breite passt genau, oben
und unten wird je gut ein Fuenftel weggeschnitten. Deshalb ist das Bild wie die
Spielvorlagen 1920x1920 und der Linsenkreis nur 1060 px gross - sonst schneidet
der Rand das Fadenkreuz an.

Vorlagen aus resources.assets zum Vergleich:
    SniperScope2k  1920x1920 RGBA, Linse teiltransparent (Alpha ~57 in der Mitte)
    PSO_Scope_ND   1920x1920 RGBA
    Mosin_Scope    1920x1440 RGBA

Hier wird bewusst keine davon nachgebaut: aussen deckend schwarz, innen voellig
frei, und ein duennes Fadenkreuz. Nichts weiter.
"""

import os
import sys

import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
OUT = os.path.join(ASSETS, "scope50.png")

S = 1920                 # wie die Spielvorlagen
R_LENS = 530.0           # Linsenradius; SniperScope2k hat rund 530
FEATHER = 3.0            # weicher Rand gegen Treppenstufen
LINE = 3.0               # Strichstaerke des Fadenkreuzes in Pixeln
GAP = 26.0               # freie Mitte, damit das Ziel nicht verdeckt wird
TICK = 26.0              # Laenge der Teilstriche
TICK_STEP = 90.0         # Abstand der Teilstriche vom Zentrum
SURROUND = (9, 9, 11)    # Farbe ausserhalb der Linse
RETICLE = (14, 14, 16)   # Farbe des Fadenkreuzes


def build():
    c = S / 2.0
    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32)
    dx, dy = xx - c + 0.5, yy - c + 0.5
    r = np.sqrt(dx * dx + dy * dy)

    # Aussen deckend, innen frei, dazwischen ein weicher Uebergang.
    outside = np.clip((r - (R_LENS - FEATHER)) / (2.0 * FEATHER), 0.0, 1.0)
    alpha = outside.copy()

    # Ganz aussen ein schmaler, dunkler Ring wirkt wie die Fassung; er entsteht
    # von selbst, weil ausserhalb ohnehin alles deckend ist.
    rgb = np.zeros((S, S, 3), np.float32)
    for i in range(3):
        rgb[..., i] = SURROUND[i]

    inside = r < R_LENS

    # ------------------------------------------------------------ Fadenkreuz
    half = LINE / 2.0
    vert = (np.abs(dx) <= half) & (np.abs(dy) > GAP) & inside
    horz = (np.abs(dy) <= half) & (np.abs(dx) > GAP) & inside

    # Teilstriche: nur senkrecht unter der Mitte, das reicht als Haltepunkt.
    ticks = np.zeros_like(vert)
    for k in (1, 2, 3, 4):
        y0 = k * TICK_STEP
        ticks |= (np.abs(dy - y0) <= half) & (np.abs(dx) <= TICK) & inside

    mark = vert | horz | ticks
    alpha[mark] = 255.0
    for i in range(3):
        rgb[..., i][mark] = RETICLE[i]

    # Ein Punkt genau im Zentrum, damit der Treffpunkt eindeutig ist.
    dot = (r <= half + 0.5)
    alpha[dot] = 255.0
    for i in range(3):
        rgb[..., i][dot] = RETICLE[i]

    a = np.clip(alpha * 255.0, 0, 255)
    a[mark] = 255.0
    a[dot] = 255.0
    out = np.dstack([rgb, a]).astype(np.uint8)
    return Image.fromarray(out, "RGBA")


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    img = build()
    img.save(OUT)
    a = np.asarray(img)[..., 3]
    c = S // 2
    lens = a[c - int(R_LENS) + 10:c + int(R_LENS) - 10,
             c - int(R_LENS) + 10:c + int(R_LENS) - 10]
    print("Zielfernrohr: %s  (%dx%d)" % (OUT, img.width, img.height))
    print("  deckend aussen      %5.1f %% der Flaeche" % (100.0 * (a > 247).mean()))
    print("  voellig frei        %5.1f %% der Flaeche" % (100.0 * (a < 8).mean()))
    print("  Linsendurchmesser   %d px von %d  (SniperScope2k rund 1060)"
          % (int(2 * R_LENS), S))
    print("  sichtbarer Streifen bei 16:9: %d px hoch, Linse passt: %s"
          % (int(S * 9 / 16), "ja" if 2 * R_LENS <= S * 9 / 16 else "NEIN"))
