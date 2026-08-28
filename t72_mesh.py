"""Erzeugt Wanne und Turm des T-72 als zwei getrennte .ndmesh.

WARUM ZWEI DATEIEN UND NICHT EINE
---------------------------------
Der Auftrag nennt eine Datei `t72.ndmesh`. Das geht nicht: `Turret` sucht im
gespawnten Fahrzeug alle Transforms mit dem Namen "turret"
(`RevivalPlugin.cs:3113`) und setzt deren `localRotation` (3275). Der Turm ist
im BTR-Prefab ein eigenes Kindobjekt mit eigenem MeshFilter - steckte er im
selben Mesh wie die Wanne, koennte er sich nicht mehr drehen, und das
Fadenkreuz zeigte in eine andere Richtung als das Rohr.

Deshalb:

    t72_hull.ndmesh     ersetzt das Mesh von .../Meshes/btr-80a_LOD0/hull
    t72_turret.ndmesh   ersetzt das Mesh von .../hull/turret

KOORDINATEN (aus dump_prefab.py BTR-80A_Spawn, 2026-08-28 belegt)
-----------------------------------------------------------------
Der Knoten `Meshes` traegt die Drehung (-0.707, 0, 0, 0.707), also -90 Grad um
X. Damit bildet sich der Meshraum so auf das Fahrzeug ab:

    Mesh +Z  ->  Fahrzeug +Y   oben
    Mesh -Y  ->  Fahrzeug +Z   vorn
    Mesh +X  ->  Fahrzeug +X   rechts

Belege: der Turm sitzt bei Mesh y = -2.661 und damit vorn; die Raeder liegen
bei Fahrzeug y = 1.770 mit Radius 1.762, der Boden also bei Mesh z = 0.

MASSSTAB
--------
Die Spielmeshes sind nicht in Metern. Aus dem Rad (Halbmasse 1.762 fuer einen
Reifen von rund 1,15 m Durchmesser) und der Wanne (Halbmasse 11.467 fuer einen
BTR-80 von 7,65 m) folgt uebereinstimmend rund 3 Einheiten je Meter. Dieses
Skript baut deshalb in METERN und multipliziert am Ende mit U = 3.0. Damit
laesst sich das Mesh ohne jede Skalierung an der Transform eintauschen.

GROESSE IM VERHAELTNIS ZUM BTR (Auftrag: der Panzer darf nicht groesser sein)
    BTR-Wanne   22.93 lang   8.50 breit   Turmoberkante  8.75
    T-72        20.25 lang  10.38 ueber Kette   Turmoberkante  6.75
Der Panzer ist also kuerzer und niedriger als der BTR und nur ueber den Ketten
breiter - genau wie beim Vorbild.
"""

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ndmesh import Mesh, face_normal, norm

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HIER, "assets")
OUT_HULL = os.path.join(ASSETS, "t72_hull.ndmesh")
OUT_TURRET = os.path.join(ASSETS, "t72_turret.ndmesh")

# Spieleinheiten je Meter. Siehe Kopf.
U = 3.0

# Wo der Turmring in der Wanne sitzt, in Metern. Phase 3 setzt die
# Turm-Transform auf diesen Punkt mal U.
RING_Y = -0.40
RING_Z = 1.50

# ---------------------------------------------------------------- Laufwerk
# Alle Kreise des Laufwerks in der Seitenansicht (y, z, r) in Metern. Die
# Kette ist die konvexe Huelle dieser Kreise, nach aussen um KETTE_D versetzt.
# Damit liegt sie zwangslaeufig an jedem Rad an, ohne dass ein einziger Punkt
# von Hand gesetzt werden muesste.
RAD_R = 0.375
RAD_Z = 0.465
RAD_Y = (-2.10, -1.25, -0.40, 0.45, 1.30, 2.15)
LEITRAD = (-2.92, 0.58, 0.30)          # vorn
TRIEBRAD = (2.97, 0.64, 0.30)          # hinten
STUETZROLLEN = ((-1.60, 0.80, 0.12), (0.25, 0.80, 0.12), (2.05, 0.80, 0.12))

KETTE_D = 0.09                          # Dicke des Kettenbandes
KETTE_MITTE = 1.44                      # Abstand der Kettenmitte von der Achse
KETTE_B = 0.58                          # Breite des Kettenbandes
KETTE_N = 72                            # Stuetzpunkte je Kette
RAD_B = 0.42                            # Breite der Laufrollen

# ------------------------------------------------------------------- Wanne
# Stationen der Wanne von vorn nach hinten:
#   y, Bodenhoehe, Deckhoehe, Halbbreite unten, Halbbreite oben
# Die vier vorderen Deckhoehen liegen auf einer Geraden - so wird die Glacis
# eine ebene Platte und keine Treppe.
STATIONEN = (
    (-3.30, 0.52, 0.611, 1.00, 1.24),
    (-2.90, 0.49, 0.836, 1.06, 1.33),
    (-2.30, 0.47, 1.174, 1.09, 1.38),
    (-1.72, 0.47, 1.500, 1.09, 1.40),
    ( 1.55, 0.47, 1.500, 1.09, 1.40),
    ( 2.05, 0.47, 1.455, 1.09, 1.40),
    ( 3.15, 0.48, 1.440, 1.09, 1.40),
    ( 3.45, 0.62, 1.220, 1.00, 1.30),
)

# --------------------------------------------------------------------- Turm
TURM_H = 0.78                           # Ring bis Dach
TURM_B = 1.34                           # Halbbreite am Ring
TURM_V = 1.36                           # nach vorn
TURM_R = 1.18                           # nach hinten
TURM_SEG = 56                           # Stuetzpunkte im Umriss (vorher 32)
# Hoehe ueber dem Ring, Skalierung des Umrisses. Zwoelf Lagen statt acht, und
# der Umriss faellt zum Dach hin nur noch auf 0.70 statt auf 0.60: der T-72
# traegt einen flachen Gussturm mit einem breiten Dach, keine Halbkugel. Die
# Halbkugel war der Grund, warum der Turm im Spiel wie eine Suppenschuessel
# aussah.
TURM_LAGEN = ((-0.14, 0.885), (0.00, 1.000), (0.11, 1.020), (0.22, 1.018),
              (0.33, 1.000), (0.43, 0.970), (0.52, 0.932), (0.60, 0.888),
              (0.67, 0.840), (0.72, 0.792), (0.76, 0.744), (0.78, 0.700))

ROHR_Z = 0.28                           # Rohrachse ueber dem Turmring
ROHR_SPITZE = -5.36
ROHR_SEG = 24                           # Umfangssegmente am Rohr (vorher 20)
BOHRUNG_R = 0.062                       # Kaliber, halb - das Rohr ist hohl
BOHRUNG_T = 0.40                        # so tief reicht die sichtbare Bohrung.
                                        # Weiter darf sie nicht gehen: bei -4.95
                                        # beginnt die Waermeschutzhuelle, und deren
                                        # Stirnflaeche stuende sonst im Bohrungsgrund.


# --------------------------------------------------------------- Werkzeuge

def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def oriented_quad(m, p0, p1, p2, p3, region, aussen):
    """Viereck mit vorgegebener Aussenrichtung, unabhaengig von der Reihenfolge.

    `Mesh.quad` leitet die Sichtbarkeit aus dem Umlaufsinn ab: die
    Rechte-Hand-Normale der uebergebenen Reihenfolge muss nach INNEN zeigen
    (siehe Kommentar dort). Bei einem Kettenband, das um Raeder herumlaeuft,
    ist "innen" aber nicht durch die Reihenfolge der Punkte bestimmt, sondern
    durch die Geometrie. Deshalb wird hier gemessen und notfalls gedreht -
    statt an vier Stellen im Kopf zu rechnen und an einer davon falsch.
    """
    fn = face_normal(p0, p1, p2) or face_normal(p0, p2, p3)
    if fn is None:
        m.dropped += 1
        return
    if dot(fn, aussen) > 0.0:
        p1, p3 = p3, p1
    m.quad(p0, p1, p2, p3, region)


def kuppel(m, lagen, region):
    """Gussturm als glatte Flaeche - Normalen aus den Nachbarn, nicht radial.

    `Mesh.prism(smooth=True)` gibt jedem Punkt eine WAAGERECHTE Normale, so als
    stuende dort eine Zylinderwand. Bei einem Turm, dessen Umriss sich nach oben
    zusammenzieht, stimmt das nur am Ring: weiter oben laeuft das Licht ueber
    jede Lagengrenze hinweg als sichtbare Stufe. Genau das hat den Turm im Spiel
    aussehen lassen wie eine Treppe aus Ringen.

    Hier wird die Normale aus den beiden Nachbarn in Umfangs- und in
    Hoehenrichtung gebildet - also aus der wirklichen Flaeche. Der Umlaufsinn
    ist derselbe wie in prism(), sonst waere der Turm von aussen unsichtbar.

    Gebaut wird im liegenden Hilfsraum: x = Breite, y = Hoehe, z = vorn.
    """
    ringe = []
    for (h, sk) in lagen:
        ringe.append([(x, h, z) for (x, z) in turm_umriss(sk)])
    k = len(ringe[0])

    normalen = []
    for li in range(len(ringe)):
        lo = ringe[max(0, li - 1)]
        hi = ringe[min(len(ringe) - 1, li + 1)]
        reihe = []
        for i in range(k):
            a = ringe[li][(i + 1) % k]
            b = ringe[li][(i - 1) % k]
            tang = (a[0] - b[0], a[1] - b[1], a[2] - b[2])
            auf = (hi[i][0] - lo[i][0], hi[i][1] - lo[i][1], hi[i][2] - lo[i][2])
            n = norm((tang[1] * auf[2] - tang[2] * auf[1],
                      tang[2] * auf[0] - tang[0] * auf[2],
                      tang[0] * auf[1] - tang[1] * auf[0]))
            radial = norm((ringe[li][i][0], 0.0, ringe[li][i][2])) or (0.0, 0.0, 1.0)
            if n is None:
                n = radial
            elif dot(n, radial) < 0.0:
                n = (-n[0], -n[1], -n[2])
            reihe.append(n)
        normalen.append(reihe)

    for li in range(len(ringe) - 1):
        for i in range(k):
            j = (i + 1) % k
            m.quad(ringe[li][i], ringe[li][j], ringe[li + 1][j], ringe[li + 1][i],
                   region, (normalen[li][i], normalen[li][j],
                            normalen[li + 1][j], normalen[li + 1][i]))

    m.fan(list(ringe[-1]), region, (0.0, 1.0, 0.0))
    m.fan(list(reversed(ringe[0])), region, (0.0, -1.0, 0.0))


def kreis(cx, cz, r, n):
    return [(cx + r * math.cos(2.0 * math.pi * i / n),
             cz + r * math.sin(2.0 * math.pi * i / n)) for i in range(n)]


def rohr_offen(m, cz, y0, y1, r, region, seg, deckel_hinten=True):
    """Rohrstueck, das nach VORN (kleines y) offen bleibt.

    `Mesh.tube` macht beide Enden zu. Fuer ein hohles Geschuetzrohr braucht die
    Muendungsseite aber statt eines Deckels einen Kranz und dahinter die
    Bohrung - deshalb hier ein Prisma ohne Deckel und, wenn gewuenscht, nur
    hinten ein Deckel.
    """
    prof = kreis(0.0, cz, r, seg)
    m.prism(y0, y1, prof, prof, region, smooth=True, caps=False, center=(0.0, cz))
    if deckel_hinten:
        m.fan([(px, y1, pz) for (px, pz) in prof], region, (0.0, 1.0, 0.0))


def bohrung(m, cz, y_muendung, r_aussen, region, seg):
    """Muendungskranz, Bohrungswand und Bohrungsgrund - das Rohr ist hohl.

    Ein Geschuetzrohr, das vorn mit einem Deckel zugemacht ist, sieht auf jedem
    Bild aus wie ein Besenstiel. Drei Flaechen mehr, und man sieht in das Rohr
    hinein.

    ZWEI KUNSTGRIFFE, DAMIT ES AUCH WIE EIN LOCH AUSSIEHT
    Ein Boden, der wie der Muendungskranz nach vorn zeigt, bekommt vom Shader
    exakt dieselbe Helligkeit wie der Kranz - die Bohrung waere dann ein Kreis
    in derselben Farbe, also unsichtbar. Deshalb:

      1. Der Boden ist von vorn SICHTBAR (Umlaufsinn), traegt aber die Normale
         NACH UNTEN. Eine nach unten zeigende Flaeche bekommt von einer Sonne
         nie direktes Licht, nur Umgebungslicht - sie ist dunkel, egal wie der
         Turm gerade steht. Nach HINTEN zu zeigen waere noch dunkler, aber
         `verify.py` meldet dann zu Recht "Rueckseite nach aussen": das Skalar-
         produkt von Flaechen- und Punktnormale waere negativ. Senkrecht dazu
         ist es null, und die Pruefung bleibt scharf.
      2. Bohrungswand und Boden liegen im Viertel `stock` - dem dunkelsten des
         Atlas. Ein Rohrinneres ist dunkler Stahl, keine Panzerplatte.
    """
    innen_region = "stock"
    aussen = kreis(0.0, cz, r_aussen, seg)
    innen = kreis(0.0, cz, BOHRUNG_R, seg)
    y_grund = y_muendung + BOHRUNG_T

    for i in range(seg):
        j = (i + 1) % seg
        # Kranz an der Muendung
        oriented_quad(m,
                      (aussen[i][0], y_muendung, aussen[i][1]),
                      (aussen[j][0], y_muendung, aussen[j][1]),
                      (innen[j][0], y_muendung, innen[j][1]),
                      (innen[i][0], y_muendung, innen[i][1]),
                      region, (0.0, -1.0, 0.0))
        # Bohrungswand, sichtbar von INNEN
        mitte = ((innen[i][0] + innen[j][0]) / 2.0,
                 (innen[i][1] + innen[j][1]) / 2.0)
        nach_innen = norm((-(mitte[0]), 0.0, -(mitte[1] - cz))) or (0.0, 0.0, 1.0)
        oriented_quad(m,
                      (innen[i][0], y_muendung, innen[i][1]),
                      (innen[j][0], y_muendung, innen[j][1]),
                      (innen[j][0], y_grund, innen[j][1]),
                      (innen[i][0], y_grund, innen[i][1]),
                      innen_region, nach_innen)
    # Grund der Bohrung: von vorn sichtbar, aber nach hinten beleuchtet.
    m.fan([(px, y_grund, pz) for (px, pz) in reversed(innen)],
          innen_region, (0.0, 0.0, -1.0))


def huelle(kreise, aufmass, n):
    """Konvexe Huelle mehrerer Kreise in der (y,z)-Ebene, als Punktliste.

    Fuer jede Richtung u wird der Kreis mit dem groessten Stuetzwert gewaehlt
    und sein Randpunkt genommen. Wo zwei Kreise sich abloesen, entsteht dabei
    von selbst die gemeinsame Tangente - also das gerade Trum der Kette.
    """
    pts = []
    for i in range(n):
        a = 2.0 * math.pi * i / n
        u = (math.cos(a), math.sin(a))
        best = None
        for (cy, cz, r) in kreise:
            rr = r + aufmass
            h = cy * u[0] + cz * u[1] + rr
            if best is None or h > best[0]:
                best = (h, (cy + rr * u[0], cz + rr * u[1]))
        pts.append(best[1])
    return pts


def kettenband(m, x_innen, x_aussen, region):
    """Ein Kettenband als geschlossener Ring um das Laufwerk.

    Aussenflaeche, Innenflaeche und die beiden Wangen. Die Aussenflaeche
    wechselt von Stuetzpunkt zu Stuetzpunkt leicht die Dicke - das ergibt die
    Zaehnung der Kettenglieder, ohne fuer jedes Glied einen Koerper zu bauen.
    """
    kreise = [(y, RAD_Z, RAD_R) for y in RAD_Y]
    kreise += [LEITRAD, TRIEBRAD]
    kreise += list(STUETZROLLEN)

    innen = huelle(kreise, 0.0, KETTE_N)
    aussen = []
    for i in range(KETTE_N):
        d = KETTE_D if i % 2 == 0 else KETTE_D - 0.010
        aussen.append(huelle(kreise, d, KETTE_N)[i])

    # Schwerpunkt als Bezug fuer "aussen" - das Laufwerk ist konvex, damit ist
    # die Richtung vom Schwerpunkt zur Kante immer die richtige.
    cy = sum(p[0] for p in innen) / KETTE_N
    cz = sum(p[1] for p in innen) / KETTE_N

    for i in range(KETTE_N):
        j = (i + 1) % KETTE_N
        ai, aj = aussen[i], aussen[j]
        ii, ij = innen[i], innen[j]
        my = (ai[0] + aj[0]) / 2.0 - cy
        mz = (ai[1] + aj[1]) / 2.0 - cz
        radial = norm((0.0, my, mz)) or (0.0, 0.0, 1.0)
        gegen = (0.0, -radial[1], -radial[2])

        # Laufflaeche und Innenlauf
        oriented_quad(m, (x_innen, ai[0], ai[1]), (x_innen, aj[0], aj[1]),
                      (x_aussen, aj[0], aj[1]), (x_aussen, ai[0], ai[1]),
                      region, radial)
        oriented_quad(m, (x_innen, ii[0], ii[1]), (x_innen, ij[0], ij[1]),
                      (x_aussen, ij[0], ij[1]), (x_aussen, ii[0], ii[1]),
                      region, gegen)
        # Die beiden Wangen
        oriented_quad(m, (x_innen, ai[0], ai[1]), (x_innen, aj[0], aj[1]),
                      (x_innen, ij[0], ij[1]), (x_innen, ii[0], ii[1]),
                      region, (-1.0, 0.0, 0.0))
        oriented_quad(m, (x_aussen, ai[0], ai[1]), (x_aussen, aj[0], aj[1]),
                      (x_aussen, ij[0], ij[1]), (x_aussen, ii[0], ii[1]),
                      region, (1.0, 0.0, 0.0))


def rad_quer(radius, breite, region, seg=18):
    """Zylinder, der nach dem Merge quer zur Fahrtrichtung liegt."""
    p = Mesh("rad")
    p.tube(0.0, 0.0, -breite / 2.0, breite / 2.0, radius, region, seg=seg)
    return p


def setze_rad(m, x, y, z, radius, breite, region, seg=18):
    m.merge(rad_quer(radius, breite, region, seg), rot_deg=(0.0, 0.0, 90.0),
            offset=(x, y, z))


def deckel(m, prof, y, nach_vorn, region):
    """Stirnflaeche der Wanne, in zwei konvexe Teile zerlegt.

    Der Querschnitt ist ein Pilz: die schmale Wanne unten, darueber der
    ueberhaengende Kasten. An den beiden Absaetzen ist er einspringend, und ein
    Dreiecksfaecher ueber einem einspringenden Umriss legt Dreiecke ausserhalb
    der Flaeche ab - die kommen verdreht heraus und sind im Spiel unsichtbar.
    Zerlegt in Unterbau (0,1,2,9) und Aufbau (2..9) ist beides konvex.
    """
    n = (0.0, -1.0, 0.0) if nach_vorn else (0.0, 1.0, 0.0)
    for teil in ([0, 1, 2, 9], [2, 3, 4, 5, 6, 7, 8, 9]):
        pts = [(prof[i][0], y, prof[i][1]) for i in teil]
        m.fan(list(reversed(pts)) if nach_vorn else pts, region, n)


def profil(floor, top, lhw, uhw):
    """Querschnitt der Wanne als Zehneck, gegen den Uhrzeigersinn in (x, z).

    Unten die schmale Wanne zwischen den Ketten, darueber der ueberhaengende
    Kasten, oben eine Fase zum Dach. Fase und Absatzhoehe wachsen mit der
    Bauhoehe mit, sonst kippt der Umriss am flachen Bug in sich zusammen.
    """
    ch = min(0.10, 0.25 * (top - floor))
    spz = min(1.04, floor + 0.55 * (top - floor))
    spz = min(spz, top - ch - 0.02)
    return [
        (-lhw, floor), (lhw, floor),
        (lhw, spz), (uhw, spz), (uhw, top - ch), (uhw - ch, top),
        (-(uhw - ch), top), (-uhw, top - ch), (-uhw, spz), (-lhw, spz),
    ]


# ------------------------------------------------------------------- Wanne

def build_hull():
    m = Mesh("T-72 Wanne")

    profile = [profil(f, t, l, u) for (_, f, t, l, u) in STATIONEN]
    for i in range(len(STATIONEN) - 1):
        m.prism(STATIONEN[i][0], STATIONEN[i + 1][0], profile[i], profile[i + 1],
                "shroud", caps=False)

    deckel(m, profile[0], STATIONEN[0][0], True, "shroud")
    deckel(m, profile[-1], STATIONEN[-1][0], False, "shroud")

    # Kotfluegel ueber den Ketten. Sie schliessen die Luecke zwischen dem
    # ueberhaengenden Wannenkasten und der Kettenaussenkante.
    #
    # Sie beginnen erst bei y = -2.30 und nicht an der Bugspitze: weiter vorn
    # faellt die Glacis unter die Kotfluegelhoehe, das Blech haette dort nichts
    # mehr, woran es sitzt, und stand in der Vorschau als Brett in der Luft.
    for s in (-1, 1):
        m.cbox(s * 1.59, 0.50, 1.065, 0.46, 5.60, 0.05, "shroud", c=0.012)
        # Zusatzfasstank hinten auf dem Kotfluegel. Zwei Faesser sind das
        # Merkmal, an dem ein sowjetischer Kampfpanzer von hinten sofort
        # erkennbar ist, und kosten zusammen keine 150 Dreiecke.
        fass = Mesh("Fass")
        fass.tube(0.0, 0.0, -0.46, 0.46, 0.235, "shroud", seg=16)
        m.merge(fass, offset=(s * 1.60, 2.70, 1.325))

    # Motordeck: eine flache Platte, knapp ueber dem Dach, damit sie nicht
    # z-flimmert. Sie lag bis 0.4.9 im Viertel `stock` - dem der Kette. Die
    # Kettenglieder sind ein feines Rippenmuster, und `_uv` zieht jedes Viertel
    # ueber die ganze Flaeche: aus dem Rippenmuster wurde auf dem Deck ein
    # grobes Schachbrett, das ueber den halben Panzer lief. Jetzt traegt das
    # Deck dieselbe Panzerplatte wie der Rest, und das Gitter ist Geometrie.
    m.cbox(0.0, 2.60, 1.462, 2.34, 1.02, 0.030, "shroud", c=0.010)
    for k in range(5):
        m.cbox(0.0, 2.24 + k * 0.18, 1.492, 2.10, 0.075, 0.030, "shroud", c=0.008)

    # Ketten und Laufwerk. Die Segmentzahlen sind bewusst hoch: das Laufwerk
    # ist das Einzige am Panzer, das aus lauter Kreisen besteht, und ein
    # sichtbares Achteck als Laufrolle faellt sofort auf.
    for s in (-1, 1):
        xa = s * (KETTE_MITTE + KETTE_B / 2.0)
        xi = s * (KETTE_MITTE - KETTE_B / 2.0)
        kettenband(m, min(xi, xa), max(xi, xa), "stock")
        for y in RAD_Y:
            setze_rad(m, s * KETTE_MITTE, y, RAD_Z, RAD_R, RAD_B, "detail", 26)
        setze_rad(m, s * KETTE_MITTE, LEITRAD[0], LEITRAD[1], LEITRAD[2],
                  RAD_B, "detail", 22)
        setze_rad(m, s * KETTE_MITTE, TRIEBRAD[0], TRIEBRAD[1], TRIEBRAD[2],
                  RAD_B, "detail", 22)
        for (y, z, r) in STUETZROLLEN:
            setze_rad(m, s * KETTE_MITTE, y, z, r, 0.20, "detail", 16)

        # Gummischuerzen ueber der vorderen Kettenhaelfte. Beim Vorbild haengen
        # sie in vier Platten am Kotfluegel und decken das Laufwerk bis auf die
        # untere Haelfte ab. Sie sind der billigste Weg, dem Panzer von der
        # Seite eine Silhouette zu geben, die nicht nur aus Kreisen besteht.
        for k in range(4):
            # x = 1.79: die Kette liegt bei 1.44 plus halbe Breite 0.29, ihre
            # Aussenflaeche also bei 1.73. Eine Schuerze bei 1.63 steckte
            # innerhalb der Kette und war unsichtbar - der Kotfluegel darueber
            # reicht bis 1.82.
            m.cbox(s * 1.79, -2.10 + k * 0.90, 0.845, 0.055, 0.86, 0.44,
                   "shroud", c=0.012)

    m.V = [(p[0] * U, p[1] * U, p[2] * U) for p in m.V]
    return m


# -------------------------------------------------------------------- Turm

def turm_umriss(s):
    """Umriss des Gussturms, gegen den Uhrzeigersinn in (x, z).

    Gebaut wird im liegenden Hilfsraum: x = Breite, z = VORN, y = Hoehe. Der
    fertige Turm entsteht daraus durch merge(rot_deg=(90, 0, 0)); dabei geht
    +Y nach oben und +Z nach vorn ueber in -Y, also in die Rohrrichtung des
    Spiels. So laesst sich prism() unveraendert benutzen, statt eine zweite
    Prismenfunktion fuer die senkrechte Achse zu schreiben.

    Kein Kreis und keine Ellipse: der T-72 traegt seine Breite hinter der
    Mitte, laeuft nach vorn deutlich schmaler zu und hat hinten eine fast
    senkrechte Wand. Genau diese Abweichung vom Kreis ist der Unterschied
    zwischen "Panzerturm" und "Schuessel".
    """
    pts = []
    n = TURM_SEG
    for i in range(n):
        a = 2.0 * math.pi * i / n
        ca, sa = math.cos(a), math.sin(a)
        # Vorn (sa > 0) zieht sich der Umriss zusammen, hinten bleibt er voll.
        schmal = 1.0 - 0.20 * max(0.0, sa) ** 1.6
        x = s * TURM_B * ca * schmal
        z = s * (TURM_V if sa > 0 else TURM_R) * sa
        # Hinten abgeflacht: der T-72 hat dort eine fast senkrechte Wand.
        z = max(z, -s * TURM_R * 0.86)
        pts.append((x, z))
    return pts


def build_turret():
    m = Mesh("T-72 Turm")

    liegend = Mesh("Turmglocke")
    kuppel(liegend, TURM_LAGEN, "receiver")
    m.merge(liegend, rot_deg=(90.0, 0.0, 0.0))

    # Blende und Rohr. Die Blende ueberdeckt die Naht zwischen Turmvorderseite
    # und Rohrwurzel - ohne sie steht das Rohr in einem Loch.
    # Die Blende ist bewusst klein und stark gefast: als grosser scharfer
    # Kasten sah sie in der Vorschau wie eine aufgeklebte Platte aus.
    m.cbox(0.0, -1.24, ROHR_Z, 0.86, 0.50, 0.52, "receiver", c=0.14)
    m.tube(0.0, ROHR_Z, -2.30, -1.40, 0.155, "receiver", seg=ROHR_SEG)
    # Waermeschutzhuelle ueber fast der ganzen Rohrlaenge. Ohne sie wirkt das
    # Rohr wie ein Besenstiel; sie ist das, was ein modernes Panzerrohr dick
    # aussehen laesst.
    m.tube(0.0, ROHR_Z, -4.95, -2.30, 0.135, "receiver", seg=ROHR_SEG)
    # Rauchabsauger - das Merkmal, an dem man ein sowjetisches Panzerrohr auf
    # hundert Meter erkennt.
    m.tube(0.0, ROHR_Z, -4.05, -3.40, 0.185, "receiver", seg=ROHR_SEG)

    # Die letzten beiden Stuecke bleiben vorn OFFEN, danach kommt die Bohrung.
    rohr_offen(m, ROHR_Z, ROHR_SPITZE, -4.90, 0.105, "receiver", ROHR_SEG)
    rohr_offen(m, ROHR_Z, ROHR_SPITZE - 0.02, ROHR_SPITZE + 0.14, 0.118,
               "receiver", ROHR_SEG)
    bohrung(m, ROHR_Z, ROHR_SPITZE - 0.02, 0.118, "receiver", ROHR_SEG)

    # Kommandantenkuppel rechts, Richtschuetzenvisier links. Mehr kommt nicht
    # aufs Dach: der Auftrag will keine Luken und keine Griffe, aber ein
    # voellig kahles Dach sieht aus wie ein unfertiges Modell.
    haube = Mesh("Kuppel")
    haube.tube(0.0, 0.0, 0.0, 0.14, 0.31, "receiver", seg=20)
    m.merge(haube, rot_deg=(90.0, 0.0, 0.0), offset=(0.45, 0.16, TURM_H - 0.01))
    m.cbox(-0.44, -0.52, TURM_H + 0.06, 0.30, 0.34, 0.18, "receiver", c=0.02)

    # Nebelwurfbecher auf beiden Wangen und der Gepaeckkorb hinten. Beides
    # kostet zusammen keine 400 Dreiecke und ist genau das, was einen Turm von
    # einem Rotationskoerper unterscheidet - der Umriss bekommt Ecken, an denen
    # das Auge sich festhaelt.
    for s in (-1, 1):
        for k in range(4):
            becher = Mesh("Nebelbecher")
            becher.tube(0.0, 0.0, 0.0, 0.30, 0.058, "detail", seg=10)
            m.merge(becher, rot_deg=(64.0, 0.0, s * 14.0),
                    offset=(s * (0.62 + k * 0.135), -0.86 + k * 0.055,
                            TURM_H - 0.30))
    m.cbox(0.0, 1.02, TURM_H - 0.30, 1.70, 0.34, 0.26, "stock", c=0.03)
    m.cbox(0.0, 0.86, TURM_H - 0.05, 1.10, 0.22, 0.14, "stock", c=0.02)

    m.V = [(p[0] * U, p[1] * U, p[2] * U) for p in m.V]
    return m


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)

    hull = build_hull()
    hull.write(OUT_HULL)
    hull.report(OUT_HULL)

    turret = build_turret()
    turret.write(OUT_TURRET)
    turret.report(OUT_TURRET)

    print("")
    print("Einbau (Phase 3):")
    print("  hull   -> t72_hull.ndmesh, Transform unveraendert")
    print("  turret -> t72_turret.ndmesh, localPosition (%.4f, %.4f, %.4f)"
          % (0.0, RING_Y * U, RING_Z * U))
    print("  Massstab %.1f Einheiten je Meter, keine Skalierung noetig." % U)
