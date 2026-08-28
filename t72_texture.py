"""Erzeugt Diffuse und Normal Map des T-72 aus einem gemeinsamen Atlas.

DIE FARBE IST NICHT ERFUNDEN
----------------------------
Vorgabe war, dass der Panzer neben dem MTW nicht wie ein Fremdkoerper steht.
Deshalb ist die Grundfarbe an `btr-80a_alb` aus `resources.assets` gemessen
(2048x2048, mit `extract.py tex` geholt): Mittelwert RGB (60, 61, 44), also ein
entsaettigtes Oliv mit Braunstich. Haeufigster Ton (56, 56, 40).

Genauso wichtig wie der Ton ist der KONTRAST - und hier lag der Fehler des
ersten Anlaufs. Die Spieltextur des BTR ist auffallend flau: keine Nieten,
keine Schablonenschrift, keine harten Kanten - nur weiche Schmutzwolken und
ganz schwache Blechfugen. Die erste Fassung dieser Datei war deutlich
kontrastreicher, und im Spiel sah der Panzer am 2026-08-29 aus, als haette
jemand mit einem hellen Stift darauf gekritzelt: gleiches Muster auf jeder
Platte, ueber die ganze Wanne wiederholt.

Der Grund steht unten unter "WARUM DIE VIERTEL KEIN LAYOUT TRAGEN" - eine
grosse Platte sieht das ganze Viertel GESTRECKT. Was hier fein aussieht, ist
am Panzer meterhoch. Seit 0.4.9 sind Grundton, Flecken, Kratzer und Normal Map
deshalb rund auf ein Drittel zurueckgenommen und die Grundfarbe ein Drittel
dunkler als die Messung am BTR.

WARUM DIE VIERTEL KEIN LAYOUT TRAGEN
------------------------------------
`ndmesh._uv` projiziert jede Flaeche einzeln und zieht dabei die laengere
Achse auf die volle Breite des Viertels. Eine grosse Wannenplatte sieht also
das ganze Viertel gestreckt, ein kleines Kettenglied nur einen Streifen davon.
Ein Muster mit Aufbau (Schrift, einzelne Nieten, eine Schweissnaht) wuerde
dadurch einmal quer ueber die ganze Wanne laufen. Alle vier Viertel sind
deshalb periodisch und ohne Grossstruktur - mit einer Ausnahme:

    detail   traegt konzentrische Ringe, denn die Laufrollen sind quadratisch
             begrenzt und bekommen damit exakt das volle Viertel. Der Ring
             sitzt auf der Rolle, wo er hingehoert - Nabe, Schuessel,
             Gummibandage.
"""

import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import texlib as T

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HIER, "assets")
OUT_D = os.path.join(ASSETS, "t72_diffuse.png")
OUT_N = os.path.join(ASSETS, "t72_normal.png")
H = T.H

# Am BTR gemessen, siehe Kopf - und dann bewusst abgedunkelt.
#
# Der Mittelwert von btr-80a_alb ist (60, 61, 44). Genau so hell hat der Panzer
# im Spiel am 2026-08-29 ausgesehen: zu hell, und weil die Textur zusaetzlich
# viel Kontrast trug, wirkte die Oberflaeche gekritzelt statt lackiert. Der BTR
# ist aber auch ein anderes Fahrzeug - grosse Blechflaechen, viel Streulicht.
# Ein Panzer soll daneben dunkel und stumpf stehen. Deshalb rund ein Drittel
# dunkler als die Messung, und der Kontrast auf ein Drittel des alten Wertes.
OLIV_WANNE = (41, 42, 31)
OLIV_TURM = (39, 41, 32)
STAHL_KETTE = (33, 32, 28)
STAHL_ROLLE = (36, 36, 32)
STAUB = (0.42, 0.38, 0.26)


def guss(r, w, h, staerke=1.0):
    """Orangenhaut eines Gussturms: weiche, dicht gepackte Beulen.

    Der Turm des T-72 ist gegossen, nicht geschweisst - und das sieht man ihm
    an. Zwei Rauschoktaven, die zweite ueber den Betrag gefaltet, ergeben
    Kuppen mit Taelern dazwischen statt eines gleichmaessigen Rauschens.
    """
    a = T.grain(r, w, h, 4, 1.0)
    b = np.abs(T.grain(r, w, h, 2, 1.0))
    return (0.62 * a + 0.38 * (b - b.mean())) * staerke


def rippen(w, h, teilung, tiefe, quer=False):
    """Periodische Rippen - Kettenglieder quer, Motordeckgitter laengs."""
    yy, xx = np.mgrid[0:h, 0:w]
    t = float(T.px(teilung))
    a = xx if quer else yy
    # Rechteckiges Profil mit weichen Flanken: eine reine Sinuswelle sieht
    # aus wie Wellblech, eine harte Kante flimmert.
    s = np.sin(a * 2.0 * np.pi / t)
    return tiefe * np.tanh(s * 2.2)


def laufrolle(w, h):
    """Hoehenkarte einer Laufrolle: Nabe, Schuessel, Felgenring, Gummi."""
    yy, xx = np.mgrid[0:h, 0:w]
    cx = cy = (w - 1) / 2.0
    d = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2) / (w / 2.0)
    a = np.full((h, w), 0.50, np.float32)
    a = np.where(d < 0.97, 0.44, a)          # Gummibandage
    a = np.where(d < 0.80, 0.56, a)          # Felgenring
    a = np.where(d < 0.72, 0.40, a)          # Schuessel, vertieft
    a = np.where(d < 0.24, 0.60, a)          # Nabe
    a = np.where(d < 0.11, 0.52, a)          # Nabendeckel
    # Radbolzen auf dem Nabenkranz
    ang = np.arctan2(yy - cy, xx - cx)
    bolzen = (np.abs(d - 0.175) < 0.045) & (np.cos(ang * 8.0) > 0.55)
    a = np.where(bolzen, 0.68, a)
    return a


def ring_farbe(arr, w, h, gummi=(30, 30, 28), felge=(52, 52, 46)):
    """Dieselben Ringe noch einmal als Farbe: Gummi dunkel, Felge oliv."""
    yy, xx = np.mgrid[0:h, 0:w]
    cx = cy = (w - 1) / 2.0
    d = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2) / (w / 2.0)
    out = arr.copy()
    for maske, rgb, staerke in (((d >= 0.80), gummi, 0.80),
                                ((d >= 0.72) & (d < 0.80), felge, 0.55)):
        c = np.asarray(rgb, np.float32).reshape(1, 1, 3) / 255.0
        m = maske[..., None].astype(np.float32)
        out = out * (1.0 - m * staerke) + c * m * staerke
    return np.clip(out, 0, 1)


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    r = T.rng(720721)

    # ------------------------------------------------------- Hoehenkarten
    # Erst die Hoehen, dann die Farbe - couple_height braucht beide, damit
    # Vertiefungen dunkel und Grate blank werden.
    h_wanne = 0.5 + T.grain(r, H, H, 5, 0.022) + T.grain(r, H, H, 2, 0.014)
    h_turm = 0.5 + guss(r, H, H, 0.055)
    h_kette = (0.5 + rippen(H, H, 11, 0.10, quer=True)
               + T.grain(r, H, H, 3, 0.045))
    h_rolle = laufrolle(H, H) + T.grain(r, H, H, 3, 0.03)
    heights = {"shroud": h_wanne, "receiver": h_turm,
               "stock": h_kette, "detail": h_rolle}

    # ------------------------------------------------------------- Diffuse
    #
    # WARUM HIER SO WENIG PASSIERT
    # `ndmesh._uv` zieht jedes Viertel ueber die GANZE Flaeche, sobald sie
    # laenger als eine Texeleinheit ist - eine zwanzig Einheiten lange
    # Wannenplatte sieht also das komplette Viertel einmal, gestreckt. Alles,
    # was in diesem Viertel Struktur hat, wird damit zu einem Muster in
    # Panzergroesse, und weil jede Platte dasselbe Viertel sieht, wiederholt
    # sich dieses Muster ueber das ganze Fahrzeug. Genau so sah der Panzer am
    # 2026-08-29 aus: ein Fliesenmuster aus Kritzeln, auf jeder Platte gleich.
    #
    # Die Folgerung ist nicht "mehr Details", sondern weniger: was gestreckt
    # ueber eine ganze Platte laeuft, darf hoechstens eine weiche Schattierung
    # sein. Kratzer, Flecken und Koernung bleiben deshalb knapp ueber der
    # Sichtbarkeitsgrenze - sie sollen die Flaeche beleben, nicht bemalen.
    wanne = T.base(r, H, H, OLIV_WANNE, 0.011, scale=7)
    wanne = T.mottle(r, wanne, 0.008, 22, (0.90, 0.88, 0.70))
    wanne = T.scratches(r, wanne, 14, 0.030, 40, direction=0.0)

    # Turm: derselbe Lack auf rauem Guss, deshalb minimal heller gesprenkelt.
    turm = T.base(r, H, H, OLIV_TURM, 0.010, scale=6)
    turm = T.mottle(r, turm, 0.007, 18, (0.90, 0.90, 0.72))
    turm = np.clip(turm + guss(r, H, H, 0.007)[..., None], 0, 1)
    turm = T.scratches(r, turm, 10, 0.026, 30, direction=0.0)

    # Kette: dunkler, brauner, oelig. Das Motordeck liegt seit 0.4.9 NICHT mehr
    # in diesem Viertel - gestreckt wurde aus den Kettenrippen dort ein
    # Schachbrett quer ueber den halben Panzer (siehe t72_mesh.py).
    #
    # Kette und Laufrollen duerfen mehr Struktur tragen als die Wanne: ein
    # Kettenglied und eine Laufrolle sind KLEINE Flaechen, die vom Viertel nur
    # einen Ausschnitt sehen. Dort wird nichts panzergross gestreckt.
    kette = T.base(r, H, H, STAHL_KETTE, 0.030, scale=5)
    kette = T.mottle(r, kette, 0.022, 9, (0.80, 0.72, 0.56))
    kette = np.clip(kette + rippen(H, H, 11, 0.018, quer=True)[..., None], 0, 1)
    kette = T.scratches(r, kette, 50, 0.085, 30, direction=90.0)

    # Laufrollen: Stahlschuessel mit Gummibandage.
    rolle = T.base(r, H, H, STAHL_ROLLE, 0.026, scale=5)
    rolle = T.mottle(r, rolle, 0.020, 10, (0.84, 0.78, 0.60))
    rolle = ring_farbe(rolle, H, H)
    rolle = T.scratches(r, rolle, 44, 0.07, 26, direction=0.0)

    # Staub in den Vertiefungen, blanke Kanten auf den Graten. Am Laufwerk
    # kraeftiger als an der Wanne - dort sammelt sich der Dreck.
    wanne = T.couple_height(wanne, h_wanne, 0.045, 0.022, 14, STAUB, 0.013)
    turm = T.couple_height(turm, h_turm, 0.040, 0.020, 12, STAUB, 0.011)
    kette = T.couple_height(kette, h_kette, 0.090, 0.050, 8, STAUB, 0.045)
    rolle = T.couple_height(rolle, h_rolle, 0.130, 0.080, 10, STAUB, 0.050)

    quads = {"shroud": wanne, "receiver": turm,
             "stock": kette, "detail": rolle}
    T.save_atlas(quads, OUT_D)
    # Noch schwaecher als bisher (2.0) und viel schwaecher als bei den Waffen
    # (2.8 bis 3.2): eine Panzerplatte ist glatt. Zusammen mit den flacheren
    # Hoehenkarten oben ist die Walzhaut jetzt eine Andeutung und keine
    # Kraterlandschaft mehr.
    T.save_height_atlas(heights, OUT_N, strength=0.9)

    print("T-72-Texturen")
    print("  %s" % OUT_D)
    print("    Wanne  RGB %s   (btr-80a_alb: (60, 61, 44))" % (T.mean_rgb(wanne),))
    print("    Turm   RGB %s" % (T.mean_rgb(turm),))
    print("    Kette  RGB %s" % (T.mean_rgb(kette),))
    print("    Rolle  RGB %s" % (T.mean_rgb(rolle),))
    print("  %s" % OUT_N)
