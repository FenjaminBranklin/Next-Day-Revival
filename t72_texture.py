"""ABGELOEST SEIT 0.5.3 - dieses Skript laeuft nicht mehr mit.

`make_assets.py` ruft stattdessen `t72_import.py` auf. Der Panzer kommt seit
0.5.3 als Modell UND Textur aus dem Spiel selbst: `t-72_wrecked` in level10
ist ein vollstaendiger, handmodellierter T-72 mit eigenen UV-Texturen. Drei
Anlaeufe mit diesem Generator (0.4.7, 0.4.9, 0.5.2) haben die Silhouette
getroffen und danach an der Oberflaeche verloren - ein Skript mit vierzig
Konstanten ersetzt keine Handarbeit.

Die Datei bleibt liegen, weil sie das Einzige ist, was ohne Spielinstallation
einen Panzer erzeugt, und weil ihre Kommentare die Messungen am BTR und die
Falle mit `ndmesh._uv` festhalten.

Erzeugt Diffuse und Normal Map des T-72 aus einem gemeinsamen Atlas.

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
OUT_M = os.path.join(ASSETS, "t72_metal.png")
H = T.H

# Am BTR gemessen - und seit 0.5.2 NICHT mehr abgedunkelt.
#
# 0.4.9 hatte die Messung um ein Drittel abgedunkelt, weil der Panzer im Spiel
# zu hell wirkte. Der Blick am 2026-08-29 hat gezeigt, dass das die falsche
# Stellschraube war: zu hell war er nie, er war zu BUNT und zu unruhig. Der
# Benutzer will ausdruecklich denselben Ton wie der MTW daneben. Also steht
# die Wanne jetzt exakt auf dem gemessenen Mittelwert von btr-80a_alb, und der
# Kontrast faellt an anderer Stelle (siehe unten: keine Kratzer mehr).
#
# Messung an btr-80a_alb (2048x2048, alle 4 Mio Pixel):
#     R Mittel 59.2  Median 57   p5 49  p95 73  Streuung 8.2
#     G Mittel 60.0  Median 60   p5 48  p95 69  Streuung 8.1
#     B Mittel 43.3  Median 41   p5 33  p95 57  Streuung 7.5
OLIV_WANNE = (59, 60, 43)
OLIV_TURM = (57, 58, 42)
STAHL_KETTE = (38, 37, 33)
STAHL_ROLLE = (43, 43, 38)
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


def metall_viertel(r, metallic, smoothness, unruhe=0.05, maske=None):
    """Ein Viertel der Metallic/Gloss-Map: R = Metallic, A = Smoothness.

    Der Unity-Standardshader liest bei gesetztem `_MetallicGlossMap` das
    Metallic aus dem ROTEN Kanal und die Smoothness aus dem ALPHAKANAL
    (`_SmoothnessTextureChannel` = 0, so steht es auch am BTR). Gruen und Blau
    sind unbenutzt; sie bekommen hier denselben Wert wie Rot, damit die Datei
    beim Ansehen lesbar ist.

    `unruhe` streut beide Werte leicht. Eine Flaeche mit exakt konstanter
    Smoothness bekommt ein Glanzlicht wie eine Billardkugel - gleichmaessig und
    tot. Erst die Streuung laesst sie nach gewalztem Blech aussehen.

    `maske` ist optional (Wert 0..1) und mischt einen zweiten, matten Satz ein:
    damit bekommt die Gummibandage der Laufrolle ihren eigenen Wert, ohne dass
    das Viertel dafuer zerteilt werden muss.
    """
    n = T.grain(r, H, H, 4, unruhe)
    m = np.clip(metallic + n, 0.0, 1.0)
    g = np.clip(smoothness + n * 0.8, 0.0, 1.0)
    if maske is not None:
        k = np.clip(maske, 0.0, 1.0)
        m = m * (1.0 - k) + 0.02 * k
        g = g * (1.0 - k) + 0.10 * k
    out = np.zeros((H, H, 4), np.float32)
    out[..., 0] = m
    out[..., 1] = m
    out[..., 2] = m
    out[..., 3] = g
    return out


def save_metal_atlas(quads, path):
    """Wie T.save_atlas, aber mit Alphakanal - der traegt die Smoothness."""
    tex = np.zeros((T.S, T.S, 4), np.float32)
    tex[0:H, 0:H] = quads["shroud"]
    tex[0:H, H:T.S] = quads["receiver"]
    tex[H:T.S, 0:H] = quads["stock"]
    tex[H:T.S, H:T.S] = quads["detail"]
    img = Image.fromarray((np.clip(tex, 0, 1) * 255).astype(np.uint8), "RGBA")
    img.save(path)
    return img


def gummi_maske(w, h):
    """1 auf der Gummibandage der Laufrolle, 0 auf dem Stahl - weiche Kante."""
    yy, xx = np.mgrid[0:h, 0:w]
    cx = cy = (w - 1) / 2.0
    d = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2) / (w / 2.0)
    return np.clip((d - 0.78) / 0.04, 0.0, 1.0).astype(np.float32)


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
    # 0.5.2: KEINE Kratzer und KEINE Wolken mehr auf Wanne und Turm.
    #
    # Der Blick im Spiel am 2026-08-29 nannte die Oberflaeche "Gekritzel,
    # Linien, billiges Tarnmuster". Das war woertlich zutreffend, und es war
    # kein Zufall, sondern die Folge der Streckung: `scratches` legt vierzehn
    # Striche von 40 Pixeln ins Viertel, und eine Wannenplatte sieht das ganze
    # Viertel - aus jedem Strich wird ein METERLANGER heller Zug quer ueber die
    # Platte, und weil jede Platte dasselbe Viertel sieht, liegt derselbe Zug
    # auf jeder Platte. Dasselbe gilt fuer `mottle` bei Skala 22: aus der Wolke
    # wird ein Fleck in Panzergroesse, also ein Tarnfleck.
    #
    # Es gibt fuer beides keine Dosierung, die hilft - der Fehler ist nicht die
    # Staerke, sondern die Groesse. Was gestreckt wird, darf keine Struktur
    # haben. Auf Wanne und Turm bleibt deshalb nur noch feine Koernung uebrig:
    # `base` mit kleiner Skala, das gibt Lack auf Blech und nichts sonst. Der
    # sichtbare Charakter der Oberflaeche kommt ab jetzt aus dem MATERIAL
    # (Metallic und Smoothness, siehe t72_metal.png), nicht aus Bemalung.
    wanne = T.base(r, H, H, OLIV_WANNE, 0.013, scale=3)

    # Turm: derselbe Lack auf rauem Guss. Die Orangenhaut bleibt, sie ist fein
    # genug - vier Oktaven auf 512 Pixel, also auch gestreckt noch ein Korn.
    turm = T.base(r, H, H, OLIV_TURM, 0.012, scale=3)
    turm = np.clip(turm + guss(r, H, H, 0.006)[..., None], 0, 1)

    # Kette: dunkler, brauner, oelig. Das Motordeck liegt seit 0.4.9 NICHT mehr
    # in diesem Viertel - gestreckt wurde aus den Kettenrippen dort ein
    # Schachbrett quer ueber den halben Panzer (siehe t72_mesh.py).
    #
    # Kette und Laufrollen duerfen mehr Struktur tragen als die Wanne: ein
    # Kettenglied und eine Laufrolle sind KLEINE Flaechen, die vom Viertel nur
    # einen Ausschnitt sehen. Dort wird nichts panzergross gestreckt.
    kette = T.base(r, H, H, STAHL_KETTE, 0.026, scale=5)
    kette = T.mottle(r, kette, 0.016, 9, (0.80, 0.76, 0.66))
    kette = np.clip(kette + rippen(H, H, 11, 0.018, quer=True)[..., None], 0, 1)
    kette = T.scratches(r, kette, 50, 0.070, 30, direction=90.0)

    # Laufrollen: Stahlschuessel mit Gummibandage.
    rolle = T.base(r, H, H, STAHL_ROLLE, 0.022, scale=5)
    rolle = T.mottle(r, rolle, 0.014, 10, (0.84, 0.80, 0.68))
    rolle = ring_farbe(rolle, H, H)
    rolle = T.scratches(r, rolle, 44, 0.055, 26, direction=0.0)

    # Staub in den Vertiefungen, blanke Kanten auf den Graten. Am Laufwerk
    # kraeftiger als an der Wanne - dort sammelt sich der Dreck.
    # Auf Wanne und Turm ist auch diese Kopplung zurueckgenommen: sie arbeitet
    # ueber einen Radius von 12 bis 14 Pixeln, und genau dieser Radius wird auf
    # der Platte zu einem halben Meter. Was hier ein dunkler Grat ist, ist am
    # Panzer eine Schliere. Am Laufwerk bleibt sie voll - dort wird nichts
    # gestreckt, und dort gehoert Dreck hin.
    wanne = T.couple_height(wanne, h_wanne, 0.018, 0.010, 14, STAUB, 0.004)
    turm = T.couple_height(turm, h_turm, 0.016, 0.009, 12, STAUB, 0.004)
    kette = T.couple_height(kette, h_kette, 0.090, 0.050, 8, STAUB, 0.045)
    rolle = T.couple_height(rolle, h_rolle, 0.130, 0.080, 10, STAUB, 0.050)

    quads = {"shroud": wanne, "receiver": turm,
             "stock": kette, "detail": rolle}
    T.save_atlas(quads, OUT_D)
    # Noch schwaecher als bisher (2.0) und viel schwaecher als bei den Waffen
    # (2.8 bis 3.2): eine Panzerplatte ist glatt. Zusammen mit den flacheren
    # Hoehenkarten oben ist die Walzhaut jetzt eine Andeutung und keine
    # Kraterlandschaft mehr.
    T.save_height_atlas(heights, OUT_N, strength=0.5)

    # ------------------------------------------------- Metallic und Gloss
    #
    # WOHER DIE ZAHLEN KOMMEN
    # Aus dem Material des MTW selbst. `research/dump_material.py btr-80a_alb`
    # (2026-08-29) liefert: Standardshader mit den Keywords _METALLICGLOSSMAP
    # und _NORMALMAP, `_GlossMapScale` 0.4, `_Metallic` 0.30, `_Glossiness`
    # 0.5. Wirksam sind bei gesetzter Map aber nicht die beiden Zahlen, sondern
    # die Map: `btr-80a_met` hat im roten Kanal Mittelwert 38 von 255, also
    # Metallic rund 0.15 (Spanne 0.02 bis 0.28), und im Alphakanal ueberall
    # 255 - die Datei liegt als DXT1 ohne Alpha vor. Die wirksame Smoothness
    # des MTW ist damit gleichmaessig 1.0 * 0.4 = 0.40.
    #
    # Genau das steht hier auf Wanne und Turm. Der Panzer bekommt damit
    # dieselbe Lichtantwort wie der MTW daneben - dieselbe Farbe UND dieselbe
    # Oberflaeche, wie es der Auftrag verlangt.
    #
    # Laufwerk und Rollen duerfen davon abweichen, und zwar nach oben: eine
    # Kettenlauffflaeche und ein Radkranz sind blanker Stahl, den die Fahrt
    # poliert. Nur die Gummibandage ist matt, dafuer die Maske.
    metall = {
        "shroud": metall_viertel(r, 0.15, 0.40, 0.045),
        "receiver": metall_viertel(r, 0.13, 0.36, 0.050),
        "stock": metall_viertel(r, 0.55, 0.48, 0.090),
        "detail": metall_viertel(r, 0.40, 0.44, 0.080, gummi_maske(H, H)),
    }
    save_metal_atlas(metall, OUT_M)

    print("T-72-Texturen")
    print("  %s" % OUT_D)
    print("    Wanne  RGB %s   (btr-80a_alb: (60, 61, 44))" % (T.mean_rgb(wanne),))
    print("    Turm   RGB %s" % (T.mean_rgb(turm),))
    print("    Kette  RGB %s" % (T.mean_rgb(kette),))
    print("    Rolle  RGB %s" % (T.mean_rgb(rolle),))
    print("  %s" % OUT_N)
    print("  %s   R=Metallic, A=Smoothness" % OUT_M)
    print("    Wanne  Metallic 0.15  Smoothness 0.40  (MTW: 0.15 / 0.40)")
