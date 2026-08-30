"""Gemeinsamer Mesh-Baukasten fuer die Waffen des Toolkits.

Herausgeloest aus mg42_mesh.py, damit MG42 und Scharfschuetzengewehr dieselbe
Geometrie-Werkstatt benutzen - und damit der Fehler, der beide betrifft, nur an
einer Stelle korrigiert werden muss.

DER FEHLER, DER HIER BEHOBEN IST
--------------------------------
Die Deckflaechen der Prismen wurden als Vierecke mit doppeltem Eckpunkt
erzeugt (p0, p1, p2, p0). Die Flaechennormale wurde aus p0, p1 und p3
berechnet - bei p3 == p0 ist das Kreuzprodukt null, und `_norm` lieferte
(0, 0, 0) statt eines Fehlers. Ergebnis im fertigen mg42.ndmesh:

    1888 von 3272 Vertices mit Normale (0, 0, 0)   -  57,7 %
     472 von 1636 Dreiecken mit Flaeche 0

Der Standard-Shader normalisiert die Normale pro Pixel. normalize(0) ist NaN,
und NaN im Beleuchtungsterm wird zu einem Pixel ohne Obergrenze. Mit Bloom
frisst das den halben Bildschirm - genau das "blendende Licht" im Hauptmenue.

Hier wird deshalb dreifach abgesichert:
  1. Deckflaechen sind echte Dreiecke mit der Ebenennormale des Deckels.
  2. `face_normal` probiert alle drei Eckenpaare durch und meldet, wenn eine
     Flaeche wirklich entartet ist - solche Flaechen werden verworfen.
  3. `Mesh.validate()` prueft das fertige Mesh und wirft, statt eine kaputte
     Datei zu schreiben.

Koordinatensystem (vom RPD uebernommen, playerdataprefabs/weapons/1023_weapon):
    Y  = Laengsachse, negativ zeigt zur Muendung
    +Z = oben
    X  = Breite
    Wurzel-Prefab traegt scale 0.01.
Referenz RPD: Bounds x -0.055..0.081, y -1.314..1.321, z -0.249..0.239,
Muendung bei (0.034, -1.390, 0.096), Pistolengriff bei y 0.50..0.74.

Dateiformat .ndmesh (little endian):
    magic  "NDMS"           4 Byte
    version int32           = 1
    vertexCount int32
    vertices  float32 * 3 * n     (x, y, z)
    normals   float32 * 3 * n
    uvs       float32 * 2 * n
    indexCount int32
    indices   int32 * m
"""

import math
import os
import struct

# Texel pro Modelleinheit. Groesser = feineres Muster auf der Oberflaeche.
UV_DENSITY = 5.0

# Vier Viertel im 512er-Atlas. Die Texturskripte benutzen dieselben Namen.
UV_REGIONS = {
    "shroud":   (0.02, 0.52, 0.48, 0.98),
    "receiver": (0.52, 0.52, 0.98, 0.98),
    "stock":    (0.02, 0.02, 0.48, 0.48),
    "detail":   (0.52, 0.02, 0.98, 0.48),
}

EPS = 1e-9


def norm(v):
    ln = math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
    if ln < EPS:
        return None
    return (v[0] / ln, v[1] / ln, v[2] / ln)


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def face_normal(p0, p1, p2):
    """Normale eines Dreiecks, oder None wenn es entartet ist.

    Es werden alle drei Eckenpaare probiert: bei einem sehr schmalen Dreieck
    kann ein Paar numerisch zusammenfallen, ein anderes aber tragfaehig sein.
    """
    for a, b, c in ((p0, p1, p2), (p1, p2, p0), (p2, p0, p1)):
        n = norm(cross(sub(b, a), sub(c, a)))
        if n is not None:
            return n
    return None


def _euler(deg):
    """Rotationsmatrix aus Eulerwinkeln in Grad, Reihenfolge X, Y, Z."""
    rx, ry, rz = [math.radians(d) for d in deg]
    cx, sx = math.cos(rx), math.sin(rx)
    cy, sy = math.cos(ry), math.sin(ry)
    cz, sz = math.cos(rz), math.sin(rz)
    mx = ((1, 0, 0), (0, cx, -sx), (0, sx, cx))
    my = ((cy, 0, sy), (0, 1, 0), (-sy, 0, cy))
    mz = ((cz, -sz, 0), (sz, cz, 0), (0, 0, 1))
    out = mx
    for mm in (my, mz):
        out = tuple(tuple(sum(mm[i][k] * out[k][j] for k in range(3))
                          for j in range(3)) for i in range(3))
    return out


def _apply(R, v):
    return (R[0][0] * v[0] + R[0][1] * v[1] + R[0][2] * v[2],
            R[1][0] * v[0] + R[1][1] * v[1] + R[1][2] * v[2],
            R[2][0] * v[0] + R[2][1] * v[1] + R[2][2] * v[2])


class Mesh(object):
    def __init__(self, name):
        self.name = name
        self.V = []
        self.N = []
        self.T = []
        self.IDX = []
        self.dropped = 0

    # ---------------------------------------------------------------- Flaechen

    def _uv(self, pts, region, plane_normal):
        """Box-Projektion auf die dominante Achse, feste Texeldichte.

        Ohne das haengt die Texeldichte von der Flaechengroesse ab: ein kleines
        Visier zeigte dasselbe Muster wie das grosse Gehaeuse, nur gestaucht.
        """
        ax = max(range(3), key=lambda i: abs(plane_normal[i]))
        a1, a2 = [(1, 2), (0, 2), (0, 1)][ax]
        u0, v0, u1, v1 = UV_REGIONS[region]
        du, dv = u1 - u0, v1 - v0

        ru = [p[a1] * UV_DENSITY for p in pts]
        rv = [p[a2] * UV_DENSITY for p in pts]
        # Gemeinsamer Versatz: sonst landet eine Ecke bei 0.98 und die naechste
        # bei 0.02, und die Textur wird ueber die Flaeche gequetscht.
        ou, ov = math.floor(min(ru)), math.floor(min(rv))
        ru = [x - ou for x in ru]
        rv = [x - ov for x in rv]
        su, sv = max(ru), max(rv)
        ku = 1.0 / su if su > 1.0 else 1.0
        kv = 1.0 / sv if sv > 1.0 else 1.0
        return [(u0 + a * ku * du, v0 + b * kv * dv) for a, b in zip(ru, rv)]

    def tri(self, p0, p1, p2, region, normals=None):
        fn = face_normal(p0, p1, p2)
        if fn is None:
            self.dropped += 1
            return
        out = (-fn[0], -fn[1], -fn[2])
        if normals is None:
            normals = (out, out, out)
        pts = (p0, p1, p2)
        uvs = self._uv(pts, region, fn)
        base = len(self.V)
        for p, n, t in zip(pts, normals, uvs):
            nn = norm(n) or out
            self.V.append(p)
            self.N.append(nn)
            self.T.append(t)
        # UMLAUFSINN, siehe Kommentar bei quad().
        self.IDX.extend([base, base + 2, base + 1])

    def quad(self, p0, p1, p2, p3, region, normals=None):
        """Viereck. Der Umlaufsinn wird hier einmal zentral auf Unity gedreht.

        Belegt am Spiel selbst (2026-08-28): `BoxAmmo01` und `Battery` aus
        `resources.assets` ueber UnityPy als OBJ exportiert. Der Exporter
        spiegelt x **und** dreht die Eckenreihenfolge um - zwei Umkehrungen,
        die sich aufheben. Im OBJ zeigt die Rechte-Hand-Normale der Wicklung
        bei 2659 von 2664 Dreiecken in dieselbe Richtung wie die gespeicherte
        Normale. In Unity gilt damit dasselbe: **die Rechte-Hand-Normale der
        Wicklung zeigt nach aussen**, das vorzeichenbehaftete Volumen eines
        geschlossenen Koerpers ist positiv.

        Der Baukasten zieht seine Profile andersherum. Bis 2026-08-28 landete
        das ungedreht im Mesh - alle Waffen waren von aussen unsichtbar und
        zeigten stattdessen ihre Innenseite. Sichtbar blieb nur, was doppelt
        gebaut ist: die M72 LAW hat jede Flaeche zweimal und sah deshalb als
        einzige richtig aus.

        Gedreht wird beides zusammen, sonst passt die Beleuchtung nicht zur
        Sichtbarkeit: die Indexreihenfolge und die aus ihr abgeleitete Normale.
        Ausdruecklich uebergebene Normalen (Rundungen, Deckel) sind bereits
        Aussennormalen und bleiben unangetastet.
        """
        fn = face_normal(p0, p1, p2) or face_normal(p0, p2, p3)
        if fn is None:
            self.dropped += 1
            return
        out = (-fn[0], -fn[1], -fn[2])
        if normals is None:
            normals = (out, out, out, out)
        pts = (p0, p1, p2, p3)
        uvs = self._uv(pts, region, fn)
        base = len(self.V)
        for p, n, t in zip(pts, normals, uvs):
            nn = norm(n) or out
            self.V.append(p)
            self.N.append(nn)
            self.T.append(t)
        self.IDX.extend([base, base + 2, base + 1, base, base + 3, base + 2])

    def fan(self, pts, region, plane_normal):
        """Deckflaeche als Dreiecksfaecher mit vorgegebener Ebenennormale.

        Genau hier lag der alte Fehler: als Viereck mit doppeltem Eckpunkt
        erzeugt, war die Flaechennormale null und wurde so ins Mesh geschrieben.
        """
        n = norm(plane_normal)
        if n is None or len(pts) < 3:
            self.dropped += 1
            return
        for i in range(1, len(pts) - 1):
            self.tri(pts[0], pts[i], pts[i + 1], region, (n, n, n))

    # ---------------------------------------------------------- Querschnitte

    @staticmethod
    def rect(cx, cz, sx, sz):
        hx, hz = sx / 2.0, sz / 2.0
        return [(cx - hx, cz - hz), (cx + hx, cz - hz),
                (cx + hx, cz + hz), (cx - hx, cz + hz)]

    @staticmethod
    def cham(cx, cz, sx, sz, c):
        """Rechteck mit gefasten Ecken - achteckiger Querschnitt.

        Eine scharfe Kante zeigt in jeder Beleuchtung zwei flache Flaechen
        nebeneinander; eine Fase erzeugt dazwischen einen schmalen Glanzstreifen,
        und genau der laesst Kanten wertig wirken.
        """
        hx, hz = sx / 2.0, sz / 2.0
        c = min(c, hx * 0.8, hz * 0.8)
        return [
            (cx - hx + c, cz - hz), (cx + hx - c, cz - hz),
            (cx + hx, cz - hz + c), (cx + hx, cz + hz - c),
            (cx + hx - c, cz + hz), (cx - hx + c, cz + hz),
            (cx - hx, cz + hz - c), (cx - hx, cz - hz + c),
        ]

    @staticmethod
    def circle(cx, cz, r, n):
        return [(cx + r * math.cos(2 * math.pi * i / n),
                 cz + r * math.sin(2 * math.pi * i / n)) for i in range(n)]

    # ---------------------------------------------------------------- Koerper

    def prism(self, y0, y1, prof0, prof1, region, smooth=False, caps=True,
              center=(0.0, 0.0)):
        """Zieht prof0 bei y0 nach prof1 bei y1. smooth = radiale Normalen."""
        assert len(prof0) == len(prof1)
        k = len(prof0)
        for i in range(k):
            j = (i + 1) % k
            a0 = (prof0[i][0], y0, prof0[i][1])
            a1 = (prof0[j][0], y0, prof0[j][1])
            b1 = (prof1[j][0], y1, prof1[j][1])
            b0 = (prof1[i][0], y1, prof1[i][1])
            if smooth:
                ni = norm((prof0[i][0] - center[0], 0.0, prof0[i][1] - center[1]))
                nj = norm((prof0[j][0] - center[0], 0.0, prof0[j][1] - center[1]))
                if ni is not None and nj is not None:
                    self.quad(a0, a1, b1, b0, region, (ni, nj, nj, ni))
                    continue
            self.quad(a0, a1, b1, b0, region)
        if caps:
            top = [(p[0], y1, p[1]) for p in prof1]
            bot = [(p[0], y0, p[1]) for p in prof0]
            self.fan(top, region, (0.0, 1.0, 0.0))
            self.fan(list(reversed(bot)), region, (0.0, -1.0, 0.0))

    def cbox(self, cx, cy, cz, sx, sy, sz, region, c=0.012, end_cham=True):
        """Gefaster Quader. end_cham fast auch die Stirnflaechen an."""
        y0, y1 = cy - sy / 2.0, cy + sy / 2.0
        if end_cham and sy > 4 * c:
            self.prism(y0, y0 + c, self.cham(cx, cz, sx - 2 * c, sz - 2 * c, c),
                       self.cham(cx, cz, sx, sz, c), region)
            self.prism(y0 + c, y1 - c, self.cham(cx, cz, sx, sz, c),
                       self.cham(cx, cz, sx, sz, c), region, caps=False)
            self.prism(y1 - c, y1, self.cham(cx, cz, sx, sz, c),
                       self.cham(cx, cz, sx - 2 * c, sz - 2 * c, c), region)
        else:
            self.prism(y0, y1, self.cham(cx, cz, sx, sz, c),
                       self.cham(cx, cz, sx, sz, c), region)

    def ctaper(self, y0, y1, c0, c1, region, c=0.012):
        """Gefaster, sich verjuengender Quader. c0/c1 = (cx, cz, sx, sz)."""
        self.prism(y0, y1, self.cham(c0[0], c0[1], c0[2], c0[3], c),
                   self.cham(c1[0], c1[1], c1[2], c1[3], c), region)

    def tube(self, cx, cz, y0, y1, r, region, seg=24, caps=True):
        self.prism(y0, y1, self.circle(cx, cz, r, seg), self.circle(cx, cz, r, seg),
                   region, smooth=True, caps=caps, center=(cx, cz))

    def cone(self, cx, cz, y0, y1, r0, r1, region, seg=20):
        self.prism(y0, y1, self.circle(cx, cz, r0, seg), self.circle(cx, cz, r1, seg),
                   region, smooth=True, center=(cx, cz))

    def pipe(self, cx, cz, y0, y1, r_out, r_in, region, bore_region=None,
             seg=24, open_y0=True, open_y1=True, inner=True):
        """Rohr MIT Bohrung: Aussenwand, Innenwand und Ringflaechen an den Enden.

        `tube` ist ein voller Zylinder mit zwei Deckeln. Fuer ein Startrohr, eine
        Muendung oder einen Laufmantel ist das falsch: von vorn sieht man dort
        eine Blechscheibe statt in ein Rohr hinein. Genau das war der Befund an
        der M72 LAW am 2026-08-30 - "nicht mal ein hohles Rohr".

        Die Innenwand bekommt eigene, nach INNEN zeigende Normalen. Ohne sie
        waere sie von innen unbeleuchtet und - weil die Spielmaterialien
        Rueckseiten cullen - von der Muendung aus gar nicht da.

        `bore_region` faerbt die Bohrung ueber ein anderes Viertel des Atlas
        ein; ohne Angabe traegt sie dasselbe Muster wie die Aussenwand.

        `inner=False` laesst die Innenwand weg und baut nur Aussenwand und
        Ringflaechen. Das ist fuer ein Rohr aus MEHREREN aufeinanderfolgenden
        Abschnitten gedacht: acht Ringe hintereinander bauen sonst acht kurze
        Innenwaende, die sich an den Stossstellen ueberlappen und dort
        flimmern. Ein einziges durchgehendes `bore` (siehe unten) an ihrer
        Stelle ist ruhiger, billiger und sieht auch wirklich nach einer
        Bohrung aus.
        """
        if bore_region is None:
            bore_region = region
        # Eine Wand mit negativer Dicke gibt es nicht. Bei r_in >= r_out laufen
        # die beiden Ringflaechen an den Enden verkehrt herum um, und das Teil
        # zeigt dort im Spiel seine Rueckseite - sichtbar wird das erst in
        # verify.py, lange nach dem Bauen. Deshalb hier, wo die Zahl steht.
        if r_in >= r_out:
            raise ValueError("%s: Rohr mit Bohrung %.4f >= Aussenradius %.4f"
                             % (self.name, r_in, r_out))
        outer0 = self.circle(cx, cz, r_out, seg)
        inner0 = self.circle(cx, cz, r_in, seg)

        # Aussenwand ohne Deckel - die Deckel sind hier Ringe, keine Scheiben.
        self.prism(y0, y1, outer0, outer0, region, smooth=True, caps=False,
                   center=(cx, cz))

        k = len(inner0)
        if inner:
            self.bore(cx, cz, y0, y1, r_in, bore_region, seg=seg)

        # Endflaechen: offener Ring oder voller Deckel.
        for yy, up, offen in ((y1, 1.0, open_y1), (y0, -1.0, open_y0)):
            plane = (0.0, up, 0.0)
            if not offen:
                pts = [(p[0], yy, p[1]) for p in outer0]
                self.fan(pts if up > 0 else list(reversed(pts)), region, plane)
                continue
            for i in range(k):
                j = (i + 1) % k
                oi = (outer0[i][0], yy, outer0[i][1])
                oj = (outer0[j][0], yy, outer0[j][1])
                ii = (inner0[i][0], yy, inner0[i][1])
                ij = (inner0[j][0], yy, inner0[j][1])
                q = (oi, oj, ij, ii) if up > 0 else (ii, ij, oj, oi)
                self.quad(q[0], q[1], q[2], q[3], region,
                          (plane, plane, plane, plane))

    def bore(self, cx, cz, y0, y1, r, region, seg=24):
        """Nur die Innenwand einer Bohrung: nach INNEN gedrehte Normalen.

        Ohne die eigenen Normalen waere die Wand von innen unbeleuchtet und -
        weil die Spielmaterialien Rueckseiten cullen - von der Muendung aus gar
        nicht vorhanden. Man saehe durch das Rohr hindurch ins Freie.
        """
        ring = self.circle(cx, cz, r, seg)
        k = len(ring)
        for i in range(k):
            j = (i + 1) % k
            a0 = (ring[i][0], y0, ring[i][1])
            a1 = (ring[j][0], y0, ring[j][1])
            b1 = (ring[j][0], y1, ring[j][1])
            b0 = (ring[i][0], y1, ring[i][1])
            ni = norm((cx - ring[i][0], 0.0, cz - ring[i][1]))
            nj = norm((cx - ring[j][0], 0.0, cz - ring[j][1]))
            if ni is None or nj is None:
                continue
            self.quad(a1, a0, b0, b1, region, (nj, ni, ni, nj))

    def crown(self, cx, cz, y_out, y_in, r_out, r_in, region, seg=24):
        """Die Fase an einer Muendung: ein Kegelring, der nach AUSSEN sieht.

        Der Unterschied zwischen einem Loch und einem gemalten Kreis. Eine
        ebene Ringflaeche an der Muendung faengt genau ein Glanzlicht - dasselbe
        wie die Rohrwand daneben - und ist damit von ihr nicht zu
        unterscheiden. Eine Fase steht schraeg, faengt ein anderes, und erst
        dieser helle Kranz sagt dem Auge, dass dahinter etwas aufhoert.

        `y_out` ist die Kante aussen, `y_in` die innere Kante weiter im Rohr.
        """
        outer = self.circle(cx, cz, r_out, seg)
        inner = self.circle(cx, cz, r_in, seg)
        k = len(outer)
        vor = -1.0 if y_in > y_out else 1.0
        for i in range(k):
            j = (i + 1) % k
            oi = (outer[i][0], y_out, outer[i][1])
            oj = (outer[j][0], y_out, outer[j][1])
            ii = (inner[i][0], y_in, inner[i][1])
            ij = (inner[j][0], y_in, inner[j][1])
            # Normale der Fase: halb nach aussen, halb laengs.
            ni = norm((outer[i][0] - cx, vor * (r_out - r_in) * 1.4,
                       outer[i][1] - cz))
            nj = norm((outer[j][0] - cx, vor * (r_out - r_in) * 1.4,
                       outer[j][1] - cz))
            if ni is None or nj is None:
                continue
            if vor < 0:
                self.quad(oi, oj, ij, ii, region, (ni, nj, nj, ni))
            else:
                self.quad(ii, ij, oj, oi, region, (ni, nj, nj, ni))

    def angled_box(self, y_c, z_c, length, thick, width, angle_deg, region,
                   x_c=0.0, c=0.008):
        """Gefaster Balken, in der YZ-Ebene gekippt - Griff, Zweibein, Schaft."""
        a = math.radians(angle_deg)
        ca, sa = math.cos(a), math.sin(a)
        prof = self.cham(0.0, 0.0, width, thick, c)
        ends = []
        for s in (-1, 1):
            cy = y_c + s * sa * length / 2.0
            cz = z_c - s * ca * length / 2.0
            # Balkenachse in (y,z) ist (sa, -ca); senkrecht dazu ist (ca, sa).
            # Mit (-sa, ca) laege die Dicke auf der Achse selbst - der
            # Querschnitt waere platt und das Bauteil unsichtbar.
            ends.append([(x_c + px, cy + pz * ca, cz + pz * sa) for (px, pz) in prof])
        a0, b0 = ends
        k = len(prof)
        # Wicklung wie in prism(): a-Ende, dann b-Ende, im selben Umlaufsinn.
        #
        # Hier stand bis 2026-08-28 die umgekehrte Reihenfolge, mit der
        # Begruendung, die Abbildung px->x und pz->(y,z) drehe die Haendigkeit.
        # Sie tut es nicht. Gemessen am fertigen Mesh: alle prism-Bauteile
        # haben ein negatives vorzeichenbehaftetes Volumen, die angled_box-
        # Bauteile als einzige ein positives - Griff und Zweibein waren damit
        # nach innen gewickelt und im Spiel von aussen unsichtbar. Genau das
        # war die "durchsichtige Seite" am MG42.
        for i in range(k):
            j = (i + 1) % k
            self.quad(a0[i], a0[j], b0[j], b0[i], region)
        axis = norm((0.0, sa, -ca)) or (0.0, 1.0, 0.0)
        # Die Deckel behalten ihre echten Aussennormalen; nur der Umlaufsinn
        # dreht sich mit. b0 liegt bei +axis, a0 bei -axis.
        self.fan(list(reversed(a0)), region, (-axis[0], -axis[1], -axis[2]))
        self.fan(b0, region, axis)


    # ------------------------------------------------------- Zusammensetzen

    def merge(self, other, rot_deg=(0.0, 0.0, 0.0), offset=(0.0, 0.0, 0.0),
              scale=1.0):
        """Haengt ein anderes Mesh gedreht, skaliert und verschoben an.

        Der Baukasten zieht Prismen nur entlang Y. Eine quer liegende Patrone
        baut man deshalb laengs und dreht sie hier um 90 Grad, statt fuer jede
        Achse eine eigene Variante der Prismenfunktion zu schreiben.
        """
        R = _euler(rot_deg)
        base = len(self.V)
        for p in other.V:
            q = _apply(R, (p[0] * scale, p[1] * scale, p[2] * scale))
            self.V.append((q[0] + offset[0], q[1] + offset[1], q[2] + offset[2]))
        for n in other.N:
            q = _apply(R, n)
            self.N.append(norm(q) or (0.0, 1.0, 0.0))
        self.T.extend(other.T)
        self.IDX.extend([i + base for i in other.IDX])
        self.dropped += other.dropped

    def fit_box(self, size, center=(0.0, 0.0, 0.0)):
        """Skaliert gleichmaessig und verschiebt, bis das Mesh in size passt.

        Damit laesst sich ein neues Item exakt so gross machen wie ein
        vorhandenes - hier: wie das Prefab-Mesh magaz_l der 7,62-Kiste.
        """
        (x0, x1), (y0, y1), (z0, z1) = self.bounds()
        cur = (x1 - x0, y1 - y0, z1 - z0)
        k = min(size[i] / cur[i] for i in range(3) if cur[i] > 1e-9)
        mid = ((x0 + x1) / 2.0, (y0 + y1) / 2.0, (z0 + z1) / 2.0)
        self.V = [((p[0] - mid[0]) * k + center[0],
                   (p[1] - mid[1]) * k + center[1],
                   (p[2] - mid[2]) * k + center[2]) for p in self.V]
        return k

    # --------------------------------------------------------------- Ausgabe

    def translate(self, dx, dy, dz):
        self.V = [(p[0] + dx, p[1] + dy, p[2] + dz) for p in self.V]

    def bounds(self):
        xs = [p[0] for p in self.V]
        ys = [p[1] for p in self.V]
        zs = [p[2] for p in self.V]
        return (min(xs), max(xs)), (min(ys), max(ys)), (min(zs), max(zs))

    def validate(self):
        """Wirft, statt ein Mesh mit kaputten Normalen zu schreiben."""
        bad_n = 0
        for n in self.N:
            ln = math.sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2])
            if not (0.99 < ln < 1.01):
                bad_n += 1
        degenerate = 0
        for i in range(0, len(self.IDX), 3):
            p0 = self.V[self.IDX[i]]
            p1 = self.V[self.IDX[i + 1]]
            p2 = self.V[self.IDX[i + 2]]
            c = cross(sub(p1, p0), sub(p2, p0))
            if math.sqrt(c[0] ** 2 + c[1] ** 2 + c[2] ** 2) * 0.5 < 1e-10:
                degenerate += 1
        bad_uv = sum(1 for t in self.T if not (0.0 <= t[0] <= 1.0 and 0.0 <= t[1] <= 1.0))
        nan = sum(1 for p in self.V for c in p if c != c)
        if bad_n or degenerate or bad_uv or nan:
            raise ValueError(
                "Mesh %s unbrauchbar: %d Normalen nicht normiert, %d entartete "
                "Dreiecke, %d UVs ausserhalb 0..1, %d NaN"
                % (self.name, bad_n, degenerate, bad_uv, nan))
        return True

    def write(self, path):
        self.validate()
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(b"NDMS")
            f.write(struct.pack("<i", 1))
            f.write(struct.pack("<i", len(self.V)))
            for p in self.V:
                f.write(struct.pack("<3f", *p))
            for n in self.N:
                f.write(struct.pack("<3f", *n))
            for t in self.T:
                f.write(struct.pack("<2f", *t))
            f.write(struct.pack("<i", len(self.IDX)))
            for i in self.IDX:
                f.write(struct.pack("<i", i))
        return os.path.getsize(path)

    def report(self, path):
        (x0, x1), (y0, y1), (z0, z1) = self.bounds()
        print("%s" % self.name)
        print("  Vertices : %d" % len(self.V))
        print("  Dreiecke : %d" % (len(self.IDX) // 3))
        print("  verworfen: %d entartete Flaechen" % self.dropped)
        print("  Ausdehnung X %.3f .. %.3f" % (x0, x1))
        print("  Ausdehnung Y %.3f .. %.3f   (RPD -1.314 .. 1.321)" % (y0, y1))
        print("  Ausdehnung Z %.3f .. %.3f   (RPD -0.249 .. 0.239)" % (z0, z1))
        print("  Datei    : %s  (%d Bytes)" % (path, os.path.getsize(path)))


def load(path):
    """Liest eine .ndmesh zurueck - fuer Vorschau und Pruefung."""
    with open(path, "rb") as f:
        d = f.read()
    if d[:4] != b"NDMS":
        raise ValueError("falsche Magic in " + path)
    n = struct.unpack_from("<i", d, 8)[0]
    off = 12
    V = [struct.unpack_from("<3f", d, off + 12 * i) for i in range(n)]
    off += 12 * n
    N = [struct.unpack_from("<3f", d, off + 12 * i) for i in range(n)]
    off += 12 * n
    T = [struct.unpack_from("<2f", d, off + 8 * i) for i in range(n)]
    off += 8 * n
    m = struct.unpack_from("<i", d, off)[0]
    off += 4
    IDX = list(struct.unpack_from("<%di" % m, d, off))
    return V, N, T, IDX
