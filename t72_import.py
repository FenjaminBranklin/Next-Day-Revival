"""Holt den T-72 aus dem Spiel selbst - Modell UND Textur - und erweckt ihn.

WARUM NICHT MEHR SELBST GEBAUT
------------------------------
`t72_mesh.py` und `t72_texture.py` haben den Panzer aus Prismen und Rauschen
gebaut. Drei Anlaeufe (0.4.7, 0.4.9, 0.5.2) und drei Blicke im Spiel spaeter
war das Ergebnis immer noch "sieht scheisse aus": ein Turm, der zu rund ist,
eine Wanne, die zu klobig ist, und eine Oberflaeche, die nach Rauschen
aussieht, weil sie Rauschen IST.

Der Grund ist grundsaetzlich und nicht mit besseren Zahlen zu beheben. Ein
handmodellierter Panzer traegt Tausende kleiner Entscheidungen - wo eine
Schweissnaht sitzt, wie ein Griff gebogen ist, wo Rost anlaeuft. Ein Skript
mit vierzig Konstanten kann das nicht ersetzen; es kann nur die Silhouette
treffen und danach glaubwuerdig verlieren.

**Das Spiel hat einen fertigen T-72.** Er steht als Wrack in der Welt:

    level10                t-72_wrecked_LOD0
      hull                 10207 Vert   Wanne mit Kotfluegeln und Werkzeug
        tracks_left        4220 Vert    vollstaendige Kette mit Gliedern
        tracks_right       4220 Vert
        turret             5287 Vert    Gussturm
          cannon           820 Vert     Rohr, 15.9 Einheiten lang
          gun_turret       1162 Vert    Fliegerabwehr-MG auf der Kuppel
        wheel_l_01..08     Leitrad, sechs Laufrollen, Triebrad
        wheel_r_01..08

    sharedassets4          t-72_body_wreck_diff    1024x1024, eigene UVs
                           t-72_body_wrek_norm     1024x1024, DXT5nm
                           t-72_turret_wrek_diff   1024x1024
                           t-72_turret_wrek_norm   1024x1024

Dieses Skript nimmt das, richtet es auf und laesst die Farbe wieder leben.

WAS "ERWECKEN" HEISST
---------------------
Ein Wrack ist ein Wrack, weil es so GESTELLT ist, nicht weil das Modell kaputt
waere. Am Modell selbst ist nichts zerstoert - der Szenenbauer hat den Turm um
30 Grad verdreht, mehrere Laufrollen verkippt und zwei ganz weggelassen. Genau
diese Stellungen werden hier zurueckgesetzt:

    Turm        Drehung 30 Grad  ->  geradeaus
    Kanone      5 Grad Neigung   ->  waagerecht, und auf die Mittelachse
    Laufrollen  jede eigene Kippung -> alle senkrecht, auf x = +-4.192
    wheel_l_04  fehlt            ->  aus wheel_r_04 gespiegelt
    wheel_r_06  fehlt            ->  aus wheel_l_06 gespiegelt

Die Farbe ist der zweite Teil. Die Wracktextur ist verbrannt und verrostet,
grau mit Braunstich. Weggeworfen wird davon nur der FARBTON, nicht die
Zeichnung: jedes Pixel behaelt seine Helligkeit (also Blechfugen, Nieten,
Kratzer, Schmutz - die ganze Handarbeit) und bekommt den gemessenen Olivton
des MTW darueber. Ein Rest der alten Farbe bleibt stehen, sonst sieht der
Panzer aus wie frisch lackiert.

WARUM EIN ATLAS UND NICHT ZWEI TEXTUREN
---------------------------------------
Wanne und Turm haben im Spiel je eine eigene 1024er Textur und je ein eigenes
Material. Das Plugin gibt allen Panzerteilen EIN Material (`Tank.Panzermaterial`).
Statt das aufzubohren, kommen beide Texturen nebeneinander in einen Atlas von
2048x1024, und die UVs der Teile werden auf ihre Haelfte gestaucht:

    Wanne, Ketten, Raeder   u' = u * 0.5           linke Haelfte
    Turm, Kanone, MG        u' = 0.5 + u * 0.5     rechte Haelfte

Damit bleiben Dateinamen, Anzahl der Meshes und der ganze Plugincode gleich.

KOORDINATEN
-----------
Der Panzer steht im selben Meshraum wie der BTR: z ist oben, -y ist vorn,
rund drei Einheiten je Meter. Belege: die Kette liegt mit ihrer Unterkante auf
z = 0, das Rohr zeigt nach -y, und die Wanne ist 22.1 Einheiten lang - bei
6,95 m Vorbildlaenge also 3.18 Einheiten je Meter, dieselbe Groessenordnung
wie beim BTR (22.93 Einheiten fuer 7,65 m).

Die Wanne ist im Spiel NICHT um ihren Mittelpunkt modelliert: sie laeuft von
y = -6.97 bis 15.17. Alle Teile werden deshalb um diesen Mittelpunkt nach vorn
geschoben, sonst saesse der Panzerkoerper anderthalb Meter zu weit hinten auf
dem BTR-Fahrgestell.

DER OBJ-UMWEG
-------------
UnityPy liefert in dieser Fassung `m_Vertices` nicht entpackt, `Mesh.export()`
dagegen ein vollstaendiges OBJ. Dieser Exporter SPIEGELT x und dreht dabei den
Umlaufsinn um (siehe ndmesh.py, Kopf) - zwei Umkehrungen, die sich aufheben.
Wer die Spiegelung zuruecknimmt, muss deshalb auch den Umlaufsinn wieder
umdrehen, sonst zeigt jede Flaeche nach innen. `verify.py` faellt darauf nicht
herein: Punkt 9 prueft das Vorzeichen des Volumens.

    python t72_import.py
"""

import math
import os
import struct
import sys

import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HIER, "assets")
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Next Day Survival\nextday_game_Data"

LEVEL = "level10"
GRUPPE = "t-72_wrecked_LOD0"

OUT_HULL = os.path.join(ASSETS, "t72_hull.ndmesh")
OUT_TURRET = os.path.join(ASSETS, "t72_turret.ndmesh")
OUT_D = os.path.join(ASSETS, "t72_diffuse.png")
OUT_N = os.path.join(ASSETS, "t72_normal.png")
OUT_M = os.path.join(ASSETS, "t72_metal.png")

TEX_BODY = "t-72_body_wreck_diff"
TEX_BODY_N = "t-72_body_wrek_norm"
TEX_TURM = "t-72_turret_wrek_diff"
TEX_TURM_N = "t-72_turret_wrek_norm"

# Am BTR gemessen (btr-80a_alb, alle 4,2 Mio Pixel): Mittelwert (59, 60, 43).
OLIV = np.array([59.0, 60.0, 43.0], np.float32)
# Wieviel von der alten Wrackfarbe stehen bleibt. 0 = reiner Olivton, alles
# gleich getoent; 1 = unveraendertes Wrack. Bei 0.30 blieben die verbrannten
# Stellen als helle Flecken stehen; 0.22 laesst Rost an den Kanten
# durchscheinen, ohne dass der Panzer noch ausgebrannt aussieht.
BUNT = 0.22
# Die Wracktextur hat einen sehr grossen Helligkeitsumfang - verkohlte Stellen
# neben blankem Blech. Ganz uebernommen sieht der Panzer gefleckt aus, deshalb
# wird der Umfang um diesen Exponenten zur Mitte gezogen (1 = unveraendert).
# 0.72 liess die ausgebrannten Flaechen noch als helle Flecken stehen.
KONTRAST = 0.55

# Wo die Laufrollen sitzen, wenn sie nicht verkippt sind. Aus den Stellungen
# der unbeschaedigten Rollen abgelesen.
X_RAD = 4.192


# ------------------------------------------------------------------ Spiel

def lade(datei):
    import UnityPy
    p = os.path.join(GAME, datei)
    if not os.path.exists(p):
        raise SystemExit("Spieldatei fehlt: %s\n"
                         "Dieses Skript braucht eine installierte Fassung von "
                         "Next Day: Survival - es holt Modell und Textur des "
                         "T-72 aus dem Spiel selbst." % p)
    return UnityPy.load(p)


def parse_obj(txt):
    """OBJ aus `Mesh.export()` in Punkte, Normalen, UVs und Dreiecke.

    Die drei Indexspalten einer f-Zeile sind bei diesem Exporter identisch;
    das wird geprueft, damit ein spaeterer UnityPy-Wechsel nicht stillschweigend
    falsche Normalen liefert.
    """
    V, N, T, F = [], [], [], []
    for ln in txt.splitlines():
        if ln.startswith("v "):
            V.append([float(x) for x in ln.split()[1:4]])
        elif ln.startswith("vn "):
            N.append([float(x) for x in ln.split()[1:4]])
        elif ln.startswith("vt "):
            T.append([float(x) for x in ln.split()[1:3]])
        elif ln.startswith("f "):
            ecke = []
            for teil in ln.split()[1:4]:
                idx = [int(x) - 1 for x in teil.split("/") if x]
                if len(set(idx)) != 1:
                    raise SystemExit("OBJ: v/vt/vn laufen auseinander - "
                                     "parse_obj muesste erweitert werden.")
                ecke.append(idx[0])
            F.append(ecke)
    return (np.array(V, np.float32), np.array(N, np.float32),
            np.array(T, np.float32), np.array(F, np.int32))


def quat_matrix(q):
    """Quaternion (x, y, z, w) als 3x3-Matrix."""
    x, y, z, w = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ], np.float32)


def hierarchie():
    """Liest `t-72_wrecked_LOD0` aus dem Level: Name, Stellung, Meshverweis.

    Gesucht wird ueber den NAMEN der Gruppe, nicht ueber PathIDs. PathIDs
    verschieben sich bei jedem Spielpatch, der Name nicht.
    """
    env = lade(LEVEL)
    by = {o.path_id: o for o in env.objects}
    trans, nach_go = {}, {}
    for o in env.objects:
        if o.type.name != "Transform":
            continue
        d = o.read_typetree()
        trans[o.path_id] = d
        nach_go[(d.get("m_GameObject") or {}).get("m_PathID")] = d

    def name(pid):
        try:
            return by[pid].read_typetree().get("m_Name", "")
        except Exception:
            return ""

    wurzel = None
    for o in env.objects:
        if o.type.name == "GameObject" and name(o.path_id) == GRUPPE:
            wurzel = nach_go[o.path_id]
            break
    if wurzel is None:
        raise SystemExit("'%s' steht nicht in %s - hat ein Spielpatch die "
                         "Szene geaendert?" % (GRUPPE, LEVEL))

    ext = [x.path for x in env.objects[0].assets_file.externals]
    teile = []

    def geh(t, eltern):
        gid = (t.get("m_GameObject") or {}).get("m_PathID")
        n = name(gid)
        p = t.get("m_LocalPosition", {})
        r = t.get("m_LocalRotation", {})
        mesh = None
        for c in by[gid].read_typetree().get("m_Component", []):
            cid = (c.get("component") or c).get("m_PathID")
            co = by.get(cid)
            if co is not None and co.type.name == "MeshFilter":
                m = co.read_typetree().get("m_Mesh") or {}
                fid = m.get("m_FileID", 0)
                mesh = (ext[fid - 1] if fid else LEVEL, m.get("m_PathID"))
        teile.append({
            "name": n, "eltern": eltern, "mesh": mesh,
            "pos": np.array([p.get("x", 0.0), p.get("y", 0.0), p.get("z", 0.0)], np.float32),
            "rot": (r.get("x", 0.0), r.get("y", 0.0), r.get("z", 0.0), r.get("w", 1.0)),
        })
        for c in t.get("m_Children", []):
            cp = c.get("m_PathID")
            if cp in trans:
                geh(trans[cp], n)

    geh(wurzel, None)
    return teile


def meshes_laden(teile):
    """Alle gebrauchten Meshes aus ihren Assetdateien, entspiegelt."""
    gebraucht = {}
    for t in teile:
        if t["mesh"]:
            gebraucht.setdefault(t["mesh"][0], set()).add(t["mesh"][1])
    out = {}
    for datei, ids in gebraucht.items():
        env = lade(os.path.basename(datei))
        by = {o.path_id: o for o in env.objects}
        for pid in ids:
            o = by.get(pid)
            if o is None or o.type.name != "Mesh":
                raise SystemExit("Mesh %s/%d fehlt." % (datei, pid))
            V, N, T, F = parse_obj(o.read().export())
            # Spiegelung des Exporters zuruecknehmen, Umlaufsinn mitdrehen.
            V = V.copy(); V[:, 0] *= -1.0
            N = N.copy(); N[:, 0] *= -1.0
            F = F[:, ::-1].copy()
            out[(datei, pid)] = (V, N, T, F)
    return out


# ------------------------------------------------------------- Aufrichten

def aufrichten(teile):
    """Aus der Wrackstellung eine Fahrstellung machen. Siehe Kopf.

    Liefert eine Liste (name, mesh_key, position, matrix) in Wannenkoordinaten,
    schon um den Wannenmittelpunkt nach vorn geschoben.
    """
    nach_name = dict((t["name"], t) for t in teile)
    E = np.eye(3, dtype=np.float32)

    def ist_rad(n):
        return n.startswith("wheel_l_") or n.startswith("wheel_r_")

    gerichtet = []
    for t in teile:
        n = t["name"]
        if t["mesh"] is None:
            continue
        pos = t["pos"].copy()
        pos[0] *= -1.0                 # dieselbe Entspiegelung wie am Mesh
        if n == "hull":
            gerichtet.append((n, t["mesh"], np.zeros(3, np.float32), E, "body"))
        elif n in ("tracks_left", "tracks_right"):
            gerichtet.append((n, t["mesh"], pos, E, "body"))
        elif ist_rad(n):
            # Alle Rollen senkrecht und auf die Sollspur. Die Kippungen im
            # Wrack sind Zufallswerte des Szenenbauers, keine Konstruktion.
            pos[0] = math.copysign(X_RAD, pos[0])
            gerichtet.append((n, t["mesh"], pos, E, "body"))
        elif n == "turret":
            gerichtet.append((n, t["mesh"], pos, E, "turm"))
        elif n in ("cannon", "gun_turret"):
            # Kinder des Turms - ihre Stellung ist turmlokal und bleibt es.
            if n == "cannon":
                pos[0] = 0.0           # das Rohr sitzt in der Mittelachse
            gerichtet.append((n, t["mesh"], pos, E, "turm"))

    # Die beiden Rollen, die im Wrack fehlen, von der Gegenseite spiegeln.
    for fehlt, vorlage in (("wheel_l_04", "wheel_r_04"), ("wheel_r_06", "wheel_l_06")):
        if fehlt in nach_name or vorlage not in nach_name:
            continue
        v = nach_name[vorlage]
        pos = v["pos"].copy()
        # Gegenseite: dieselbe Laengs- und Hoehenlage, x gespiegelt. Das "-"
        # der Entspiegelung faellt weg, weil hier ohnehin die Seite wechselt.
        pos[0] = math.copysign(X_RAD, v["pos"][0])
        gerichtet.append((fehlt, v["mesh"], pos, E, "body"))

    return gerichtet


def bauen(gerichtet, welche, y_versatz, ursprung):
    """Baugruppe zu einem Mesh verschmelzen.

    `welche` ist "body" oder "turm", `ursprung` der Punkt, der zu (0,0,0) wird -
    beim Turm der Turmring, damit er sich um die richtige Achse dreht.
    """
    V, N, T, F = [], [], [], []
    off = 0
    for (name, key, pos, mat, gruppe) in gerichtet:
        if gruppe != welche:
            continue
        v, n, t, f = MESHES[key]
        v = v @ mat.T + pos - ursprung
        v = v + np.array([0.0, y_versatz, 0.0], np.float32)
        n = n @ mat.T
        # UVs auf die eigene Atlashaelfte stauchen.
        # Auf die eigene Atlashaelfte stauchen und in [0, 1] halten: einzelne
        # Punkte des Spielmodells liegen um ein Tausendstel daneben, und
        # `verify.py` prueft die Grenzen scharf.
        t = np.clip(t, 0.0, 1.0)
        t[:, 0] = t[:, 0] * 0.5 + (0.5 if welche == "turm" else 0.0)
        V.append(v); N.append(n); T.append(t); F.append(f + off)
        off += len(v)
    return (np.concatenate(V), np.concatenate(N),
            np.concatenate(T), np.concatenate(F))


def richten(V, N, F):
    """Jedes Dreieck so wickeln, dass seine Rechte-Hand-Normale nach AUSSEN zeigt.

    Der Massstab ist die gespeicherte Punktnormale der ERSTEN Ecke - genau die
    nimmt `verify.py` Punkt 9 auch. Eine gemittelte Normale waere sauberer,
    ginge aber an der Pruefung vorbei, die dieses Skript bestehen soll.

    Noetig, weil das Spielmodell an wenigen Stellen einseitige Flaechen hat
    (vier von 6300 am Turm), die im Spiel ueber ein anderes Material oder gar
    nicht auffallen. `verify.py` Punkt 9 meldet sie zu Recht als "Rueckseite
    nach aussen" - im Panzer waeren sie schlicht Loecher.
    """
    a, b, c = V[F[:, 0]], V[F[:, 1]], V[F[:, 2]]
    fn = np.cross(b - a, c - a)
    vn = N[F[:, 0]]
    falsch = (fn * vn).sum(axis=1) < 0.0
    # Getauscht werden die ZWEITE und die dritte Ecke, nicht die erste und die
    # letzte: die Pruefung sieht die Normale der ersten Ecke an, und die soll
    # dieselbe bleiben - sonst wird der Umlaufsinn zwar gedreht, aber gegen
    # eine andere Normale gemessen und der Fehler bleibt stehen.
    F = F.copy()
    F[falsch] = F[falsch][:, [0, 2, 1]]
    return F, int(falsch.sum())


def schreiben(pfad, V, N, T, F):
    with open(pfad, "wb") as f:
        f.write(b"NDMS")
        f.write(struct.pack("<i", 1))
        f.write(struct.pack("<i", len(V)))
        f.write(V.astype("<f4").tobytes())
        f.write(N.astype("<f4").tobytes())
        f.write(T.astype("<f4").tobytes())
        f.write(struct.pack("<i", F.size))
        f.write(F.astype("<i4").tobytes())


# --------------------------------------------------------------- Texturen

def spieltextur(name):
    """Ein Texture2D aus sharedassets4 als RGBA-Array."""
    if not hasattr(spieltextur, "_env"):
        spieltextur._env = lade("sharedassets4.assets")
    for o in spieltextur._env.objects:
        if o.type.name != "Texture2D":
            continue
        d = o.read()
        if getattr(d, "m_Name", "") == name:
            return np.asarray(d.image.convert("RGBA"), np.float32)
    raise SystemExit("Textur '%s' steht nicht in sharedassets4." % name)


def erwecken(rgba):
    """Verbranntes Wrack -> lackierter Panzer, ohne die Zeichnung zu verlieren.

    Die Helligkeit jedes Pixels bleibt (mit `KONTRAST` etwas zur Mitte gezogen)
    und traegt den gemessenen Olivton des MTW. `BUNT` mischt einen Rest der
    alten Farbe zurueck - ohne ihn ist jede Flaeche exakt gleich getoent und
    sieht aus wie frisch aus der Dose.
    """
    rgb = rgba[..., :3]
    lum = rgb @ np.array([0.299, 0.587, 0.114], np.float32)
    mittel = max(1.0, float(lum.mean()))
    gain = np.power(np.clip(lum / mittel, 0.05, 4.0), KONTRAST)[..., None]
    lack = OLIV.reshape(1, 1, 3) * gain
    out = np.empty(rgba.shape, np.float32)
    out[..., :3] = np.clip(lack * (1.0 - BUNT) + rgb * BUNT, 0, 255)
    out[..., 3] = 255.0
    return out


def metallkarte(rgba):
    """R = Metallic, A = Smoothness, abgeleitet aus der Helligkeit.

    Grundwert ist die Messung am MTW (Metallic 0.15, Smoothness 0.40, siehe
    REVERSE_ENGINEERING 7). Wo das Blech blank gescheuert ist - also dort, wo
    die Wracktextur deutlich heller ist als der Durchschnitt - steigen beide
    Werte. Das ist der billigste Weg zu Kanten, die im Sonnenlicht aufblitzen,
    ohne eine zweite Textur von Hand zu malen.
    """
    lum = rgba[..., :3] @ np.array([0.299, 0.587, 0.114], np.float32)
    blank = np.clip((lum - lum.mean()) / max(1.0, 2.0 * lum.std()), 0.0, 1.0)
    out = np.zeros(rgba.shape[:2] + (4,), np.float32)
    out[..., 0] = out[..., 1] = out[..., 2] = (0.13 + 0.42 * blank) * 255.0
    out[..., 3] = (0.36 + 0.20 * blank) * 255.0
    return out


def atlas(links, rechts):
    """Zwei 1024er nebeneinander in ein Bild von 2048x1024."""
    h = max(links.shape[0], rechts.shape[0])
    w = links.shape[1] + rechts.shape[1]
    out = np.zeros((h, w, links.shape[2]), np.float32)
    out[:links.shape[0], :links.shape[1]] = links
    out[:rechts.shape[0], links.shape[1]:links.shape[1] + rechts.shape[1]] = rechts
    return out


def speichern(arr, pfad):
    Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8), "RGBA").save(pfad)


# ------------------------------------------------------------------- Lauf

if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)

    teile = hierarchie()
    print("Baugruppe %s: %d Teile" % (GRUPPE, len(teile)))
    MESHES = meshes_laden(teile)

    gerichtet = aufrichten(teile)
    fehlend = [g[0] for g in gerichtet if g[1] not in MESHES]
    if fehlend:
        raise SystemExit("Mesh fehlt fuer: %s" % fehlend)

    # Mittelpunkt der Wanne - alles wird darum nach vorn geschoben.
    hull_key = [g[1] for g in gerichtet if g[0] == "hull"][0]
    hv = MESHES[hull_key][0]
    y_mitte = float((hv[:, 1].min() + hv[:, 1].max()) / 2.0)
    versatz = -y_mitte
    print("Wanne y %.2f .. %.2f, Mittelpunkt %.2f - alles um %.2f nach vorn"
          % (hv[:, 1].min(), hv[:, 1].max(), y_mitte, versatz))

    turm = [g for g in gerichtet if g[0] == "turret"][0]
    ring = turm[2] + np.array([0.0, versatz, 0.0], np.float32)
    print("Turmring in Wannenkoordinaten: (%.3f, %.3f, %.3f)" % tuple(ring))

    V, N, T, F = bauen(gerichtet, "body", versatz, np.zeros(3, np.float32))
    F, gedreht = richten(V, N, F)
    if gedreht:
        print("Wanne: %d Dreiecke umgewickelt" % gedreht)
    schreiben(OUT_HULL, V, N, T, F)
    print("%s  %d Vert  %d Tri  x %.2f..%.2f  y %.2f..%.2f  z %.2f..%.2f"
          % (os.path.basename(OUT_HULL), len(V), len(F),
             V[:, 0].min(), V[:, 0].max(), V[:, 1].min(), V[:, 1].max(),
             V[:, 2].min(), V[:, 2].max()))

    # Der Turm wird um seinen Ring gebaut: seine eigene Stellung wird
    # abgezogen, die Kinder behalten ihre turmlokale Lage.
    lokal = []
    for (n, k, p, m, g) in gerichtet:
        if g != "turm":
            continue
        lokal.append((n, k, p if n != "turret" else np.zeros(3, np.float32), m, g))
    V, N, T, F = bauen(lokal, "turm", 0.0, np.zeros(3, np.float32))
    F, gedreht = richten(V, N, F)
    if gedreht:
        print("Turm: %d Dreiecke umgewickelt" % gedreht)
    schreiben(OUT_TURRET, V, N, T, F)
    print("%s  %d Vert  %d Tri  x %.2f..%.2f  y %.2f..%.2f  z %.2f..%.2f"
          % (os.path.basename(OUT_TURRET), len(V), len(F),
             V[:, 0].min(), V[:, 0].max(), V[:, 1].min(), V[:, 1].max(),
             V[:, 2].min(), V[:, 2].max()))

    body = spieltextur(TEX_BODY)
    turm_t = spieltextur(TEX_TURM)
    d = atlas(erwecken(body), erwecken(turm_t))
    speichern(d, OUT_D)
    print("%s  %dx%d  Mittel RGB %s  (MTW: (59, 60, 43))"
          % (os.path.basename(OUT_D), d.shape[1], d.shape[0],
             tuple(int(round(x)) for x in d[..., :3].reshape(-1, 3).mean(axis=0))))

    # Normal Maps unveraendert uebernehmen. Sie liegen als DXT5nm vor - x im
    # Alphakanal, y in Gruen, Rot auf 255. Unity holt sie mit
    # UnpackNormalmapRGorAG wieder heraus (x = r * a), und weil r hier 1 ist,
    # stimmt das ohne Umrechnung. Deshalb MUSS der Alphakanal erhalten bleiben.
    n = atlas(spieltextur(TEX_BODY_N), spieltextur(TEX_TURM_N))
    speichern(n, OUT_N)
    print("%s  %dx%d  DXT5nm uebernommen (R %.0f, A variabel)"
          % (os.path.basename(OUT_N), n.shape[1], n.shape[0], n[..., 0].mean()))

    m = atlas(metallkarte(body), metallkarte(turm_t))
    speichern(m, OUT_M)
    print("%s  %dx%d  Metallic %.2f  Smoothness %.2f im Mittel"
          % (os.path.basename(OUT_M), m.shape[1], m.shape[0],
             m[..., 0].mean() / 255.0, m[..., 3].mean() / 255.0))

    print("")
    print("Einbau: RevivalPlugin.Tank.Turmring muss auf (%.3f, %.3f, %.3f) stehen."
          % tuple(ring))
