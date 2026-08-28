"""Erzeugt das Zielfernrohrbild des Panzers - t72_scope.png.

WARUM EIN ZWEITES OVERLAY NEBEN scope50.png
--------------------------------------------
Das Bild der .50 ist ein Scharfschuetzenglas: runde Linse, duennes Fadenkreuz,
sonst nichts. Ein Panzerrichtschuetze sieht etwas anderes - eine Winkelmarke
statt eines Kreuzes, eine seitliche Entfernungsskala und unten den
Strichentfernungsmesser, mit dem die Entfernung an der Zielhoehe abgelesen
wird. Und er sieht nicht durch ein offenes Fenster: zum Rand hin wird es
dunkel, weil das Okular nun einmal ein Rohr ist.

Beides zusammen ist der Grund, warum der Panzer nicht einfach das Bild der .50
mitbenutzt.

WIE DAS BILD BENUTZT WIRD
-------------------------
`Turret.Vollbild` zeichnet es als Quadrat mit der Kantenlaenge
max(Bildbreite, Bildhoehe), mittig - das ist von Hand gerechnetes
ScaleAndCrop. Auf 16:9 passt also die Breite genau, und oben und unten wird je
gut ein Fuenftel abgeschnitten. Der sichtbare Streifen ist 1080 von 1920
Zeilen hoch, die Linse darf deshalb hoechstens 540 px Radius haben, sonst
schneidet der Rand sie an.

WAS BEWUSST FEHLT
-----------------
Zahlen an der Skala. Eine Ziffer in dieser Groesse braucht entweder eine
Schriftart aus dem System - die auf einem anderen Rechner anders aussieht -
oder von Hand gesetzte Striche. Die Skala traegt stattdessen lange und kurze
Teilstriche im Wechsel; wofuer sie stehen, sagt kein Bild, sondern die
Gewoehnung.
"""

import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HIER, "assets")
OUT = os.path.join(ASSETS, "t72_scope.png")

S = 1920                  # wie die Spielvorlagen und wie scope50.png
SS = 2                    # Ueberabtastung fuer die Strichzeichnung
R_LENS = 522.0            # Linsenradius, knapp unter der Haelfte von 1080
FEATHER = 3.0             # weicher Rand gegen Treppenstufen

FASSUNG = (9, 9, 11)      # alles ausserhalb der Linse
RING = (26, 27, 30)       # schmaler Ring innen an der Fassung
STRICH = (17, 18, 19)     # Kern der Marken
SAUM = (196, 202, 186)    # heller Saum darum, damit sie vor dunklem Grund steht
SAUM_A = 105

# Vignette: bis VIG_FREI voellig frei, dann bis zum Rand auf VIG_MAX zu.
VIG_FREI = 0.62
VIG_MAX = 218.0

# Marken, alle in Pixeln vom Zentrum aus (Bildmassstab 1920).
KEIL_B = 62.0             # halbe Breite der Winkelmarke
KEIL_H = 46.0             # Hoehe der Winkelmarke
STRICH_B = 7.0            # Strichstaerke der Winkelmarke
LINIE_B = 5.0             # Strichstaerke der Seitenskala
SKALA_VON = 92.0          # wo die Seitenskala beginnt
SKALA_BIS = 468.0         # und wo sie endet
TEILUNG = 56.0            # Abstand der Teilstriche
TICK_KURZ = 13.0
TICK_LANG = 25.0
UNTER = ((104.0, 38.0, 24.0), (196.0, 30.0, 19.0), (288.0, 23.0, 15.0))
EM_Y = 252.0              # Grundlinie des Entfernungsmessers
EM_VON = -466.0
EM_BIS = -166.0
EM_HOCH = 146.0


def keil(d, cx, cy, halb, hoehe, farbe, breite):
    """Winkelmarke - die Spitze ist der Treffpunkt, die Schenkel zeigen nach unten."""
    d.line([(cx - halb, cy + hoehe), (cx, cy), (cx + halb, cy + hoehe)],
           fill=farbe, width=int(round(breite)), joint="curve")


def seitenskala(d, cx, cy, farbe, breite):
    """Waagerechte Skala links und rechts der Mitte, mit Teilstrichen nach oben."""
    for s in (-1, 1):
        d.line([(cx + s * SKALA_VON, cy), (cx + s * SKALA_BIS, cy)],
               fill=farbe, width=int(round(breite)))
        k = 1
        x = SKALA_VON + TEILUNG
        while x <= SKALA_BIS:
            lang = TICK_LANG if k % 2 == 0 else TICK_KURZ
            d.line([(cx + s * x, cy), (cx + s * x, cy - lang)],
                   fill=farbe, width=int(round(breite)))
            x += TEILUNG
            k += 1


def entfernungsmesser(d, cx, cy, farbe, breite):
    """Strichentfernungsmesser: Grundlinie und die dazu abfallende Kurve.

    Im Original wird ein Ziel bekannter Hoehe - beim T-72 2,7 m - zwischen
    Grundlinie und Kurve eingepasst; wo es genau hineinpasst, steht die
    Entfernung. Hier ist die Kurve reine Form: die Mechanik dahinter gibt es
    im Spiel nicht, das Bild soll nur aussehen wie ein Panzerglas.
    """
    y0 = cy + EM_Y
    d.line([(cx + EM_VON, y0), (cx + EM_BIS, y0)],
           fill=farbe, width=int(round(breite)))
    punkte = []
    n = 48
    for i in range(n + 1):
        t = i / float(n)
        x = EM_VON + t * (EM_BIS - EM_VON)
        h = EM_HOCH * (1.0 - t) ** 1.5 + 14.0
        punkte.append((cx + x, y0 - h))
    d.line(punkte, fill=farbe, width=int(round(breite)), joint="curve")
    for i in (0, 12, 24, 36, 48):
        x, y = punkte[i]
        d.line([(x, y), (x, y - 16)], fill=farbe, width=int(round(breite)))


def marken(farbe, breite_zu):
    """Alle Striche einmal zeichnen, in der Ueberabtastung."""
    im = Image.new("RGBA", (S * SS, S * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    cx = cy = S * SS / 2.0

    keil(d, cx, cy, KEIL_B * SS, KEIL_H * SS, farbe, (STRICH_B + breite_zu) * SS)
    seitenskala(d, cx, cy, farbe, (LINIE_B + breite_zu) * SS)
    for (dy, halb, hoehe) in UNTER:
        keil(d, cx, cy + dy * SS, halb * SS, hoehe * SS, farbe,
             (LINIE_B + breite_zu) * SS)
    entfernungsmesser(d, cx, cy, farbe, (LINIE_B + breite_zu) * SS)

    return np.asarray(im.resize((S, S), Image.LANCZOS)).astype(np.float32)


def build():
    c = S / 2.0
    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32)
    dx, dy = xx - c + 0.5, yy - c + 0.5
    r = np.sqrt(dx * dx + dy * dy)

    # Aussen deckend, innen die Vignette.
    aussen = np.clip((r - (R_LENS - FEATHER)) / (2.0 * FEATHER), 0.0, 1.0)
    t = np.clip((r / R_LENS - VIG_FREI) / (1.0 - VIG_FREI), 0.0, 1.0)
    alpha = np.maximum(aussen * 255.0, (t ** 2.2) * VIG_MAX)

    rgb = np.zeros((S, S, 3), np.float32)
    for i in range(3):
        rgb[..., i] = FASSUNG[i]

    # Schmaler Ring innen an der Fassung - das ist die Kante des Okulars.
    ring = (r > R_LENS - 13.0) & (r < R_LENS - 3.0)
    for i in range(3):
        rgb[..., i][ring] = RING[i]

    # Marken: erst der helle Saum, dann der dunkle Kern darauf. Ohne Saum
    # verschwindet eine schwarze Marke vor einer Baumreihe.
    innen = (r < R_LENS - 6.0)[..., None]
    for (farbe, zu, deck) in ((SAUM, 3.4, SAUM_A / 255.0), (STRICH, 0.0, 1.0)):
        lage = marken((farbe[0], farbe[1], farbe[2], 255), zu)
        m = (lage[..., 3:4] / 255.0) * deck * innen
        rgb = rgb * (1.0 - m) + np.asarray(farbe, np.float32).reshape(1, 1, 3) * m
        alpha = np.maximum(alpha, (m[..., 0] * 255.0))

    out = np.dstack([np.clip(rgb, 0, 255), np.clip(alpha, 0, 255)])
    return Image.fromarray(out.astype(np.uint8), "RGBA")


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    img = build()
    img.save(OUT)
    a = np.asarray(img)[..., 3]
    print("Panzerzielfernrohr: %s  (%dx%d)" % (OUT, img.width, img.height))
    print("  deckend aussen      %5.1f %% der Flaeche" % (100.0 * (a > 247).mean()))
    print("  voellig frei        %5.1f %% der Flaeche" % (100.0 * (a < 8).mean()))
    print("  Linsendurchmesser   %d px von %d" % (int(2 * R_LENS), S))
    print("  sichtbarer Streifen bei 16:9: %d px hoch, Linse passt: %s"
          % (int(S * 9 / 16), "ja" if 2 * R_LENS <= S * 9 / 16 else "NEIN"))

    # Kontrollblick: der Ausschnitt, den 16:9 wirklich zeigt, ueber einer
    # Wiesenfarbe. Ohne Untergrund sagt eine Datei mit Alphakanal nichts.
    hoch = int(S * 9 / 16)
    band = img.crop((0, (S - hoch) // 2, S, (S + hoch) // 2))
    grund = Image.new("RGBA", band.size, (110, 125, 95, 255))
    vor = os.path.join(ASSETS, "t72_scope_preview.png")
    Image.alpha_composite(grund, band).convert("RGB").resize(
        (960, 540), Image.LANCZOS).save(vor)
    print("  Vorschau            %s" % vor)
