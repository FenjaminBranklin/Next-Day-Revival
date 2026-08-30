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

THE RED STAR
------------
The turret carries a red star on both cheeks. It is not drawn into the atlas
at a spot picked by eye - the game's UV islands make that a lottery - but
PROJECTED onto the turret from a plane beside it and rasterized in texel
space, the way a decal is applied. Four numbers place the plane; everything
else follows the casting. See "Markings" further down.

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

import argparse
import math
import os
import struct
import sys
import types

import numpy as np
from PIL import Image

# UnityPy's export package imports audio and ASTC support eagerly even when
# this tool only exports meshes and DXT textures. The frozen release excludes
# those optional packages (and the proprietary FMOD DLL they would pull in).
# Tiny placeholders are sufficient because neither code path is called here.
if getattr(sys, "frozen", False):
    sys.modules["fmod_toolkit"] = types.ModuleType("fmod_toolkit")
    sys.modules["astc_encoder"] = types.ModuleType("astc_encoder")

if getattr(sys.stdout, "reconfigure", None):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = (os.path.dirname(sys.executable) if getattr(sys, "frozen", False)
        else os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(HIER, "assets")
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Next Day Survival\nextday_game_Data"

LEVEL = "level10"
GRUPPE = "t-72_wrecked_LOD0"

OUT_HULL = os.path.join(ASSETS, "t72_hull.ndmesh")
OUT_TURRET = os.path.join(ASSETS, "t72_turret.ndmesh")
OUT_D = os.path.join(ASSETS, "t72_diffuse.png")
OUT_N = os.path.join(ASSETS, "t72_normal.png")
OUT_M = os.path.join(ASSETS, "t72_metal.png")


def configure_paths():
    """Resolve input and output outside a frozen PyInstaller executable."""
    global GAME, ASSETS, OUT_HULL, OUT_TURRET, OUT_D, OUT_N, OUT_M

    parser = argparse.ArgumentParser(
        description="Build the current T-72 assets from an installed copy of the game.")
    parser.add_argument("--game-data", default=GAME,
                        help="Path to nextday_game_Data or the game installation.")
    parser.add_argument("--assets", default=ASSETS,
                        help="Output directory for the five generated assets.")
    args = parser.parse_args()

    game = os.path.abspath(args.game_data)
    nested = os.path.join(game, "nextday_game_Data")
    if os.path.isdir(nested):
        game = nested
    GAME = game
    ASSETS = os.path.abspath(args.assets)
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


# ---------------------------------------------------------------- Markings

# A tank without a marking reads as a prop. The red star is the one sign every
# T-72 in Soviet and Russian service carried, so that is what goes on.
#
# IT IS NOT DRAWN INTO THE ATLAS BY HAND
# --------------------------------------
# The turret keeps the game's own UVs. They are hand made, they are neither
# flat nor rectangular, and nothing in the file says which patch of the
# 1024x1024 texture is the cheek of the turret. A star drawn at a guessed spot
# would land on the roof, on a hatch, or across a seam.
#
# So the star is PROJECTED, the way a real decal is applied. A plane is put
# beside the turret; every triangle that faces that plane and lies close to it
# is rasterized IN TEXEL SPACE, and each texel asks where its own 3D point
# falls on the plane. The star then follows the casting - it curves with the
# cheek and stops at the edge - and it lands correctly no matter how the UV
# island is cut. Only the plane has to be measured, and that is four numbers.
#
# Both sides are projected. If the model mirrors its UVs - many hand made
# turrets do - the second pass writes the same texels as the first; coverage
# is combined with a maximum, so nothing is painted twice.

# Turret coordinates: z is up, -y is forward, the turret ring is the origin.
# Measured on the turret's own side wall: the cheek runs z -0.2 .. 2.0 and is
# widest around y = 0, at x = 4.0 .. 4.4. Roughly 3.18 units are a metre, so a
# radius of 0.62 is a star 39 cm across - the size it is painted at.
STERN_EBENE_X = 4.0        # the cheek, not the widest point of the turret
STERN_MITTE = (0.6, 0.95)  # (y, z) on that plane
STERN_R = 0.62
STERN_TIEFE = 1.3          # how far off the plane a surface may lie and still be hit
STERN_KANTE = 0.28         # how far a face may turn away from the plane (cosine)

# Weathered, not signal red. The star has been on that turret for years, and
# the vehicle beside it (the MTW) is a flat olive - a pure red would be the
# brightest thing in the scene, which is the mistake 0.4.7 already made once.
STERN_ROT = np.array([124.0, 34.0, 28.0], np.float32)
STERN_ROT_DECKUNG = 0.80
# The thin light rim is what makes the star readable against olive at distance.
STERN_RAND = np.array([176.0, 172.0, 158.0], np.float32)
STERN_RAND_DECKUNG = 0.45
STERN_RAND_PIXEL = 4       # rim width in texels of the atlas


def stern_polygon(zacken=5):
    """The ten points of a five pointed star, radius 1, one point up.

    The inner radius is not free: for the flanks of neighbouring points to lie
    on one straight line - which is what makes a star look drawn rather than
    spiky - it has to be cos(72 deg) / cos(36 deg) = 0.382.
    """
    innen = math.cos(math.radians(72.0)) / math.cos(math.radians(36.0))
    pts = []
    for i in range(2 * zacken):
        r = 1.0 if i % 2 == 0 else innen
        a = math.pi / 2.0 + i * math.pi / zacken
        pts.append((r * math.cos(a), r * math.sin(a)))
    return np.array(pts, np.float32)


def im_polygon(poly, s, t):
    """Even-odd test of many points against one polygon.

    A star is concave, so the cheap "same side of every edge" test does not
    work on it. Counting crossings does, and it costs one pass per edge.
    """
    innen = np.zeros(s.shape, bool)
    n = len(poly)
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        if y1 == y2:
            continue
        schneidet = (y1 > t) != (y2 > t)
        xk = x1 + (t - y1) * (x2 - x1) / (y2 - y1)
        innen ^= schneidet & (s < xk)
    return innen


def projizieren(deckung, V, T, F, mitte, achse, hoch, groesse, tiefe, form):
    """Project one marking onto the mesh and record it in atlas texels.

    `mitte`, `achse` and `hoch` place the projector in mesh space; `groesse`
    is the half width of the marking in mesh units, so `form` always sees
    coordinates in -1 .. +1. `tiefe` stops the projection from reaching
    through the turret and painting the far wall as well.

    `deckung` is a float buffer 0 .. 1 over the whole atlas and is filled with
    a maximum, so two passes over the same texels do not add up.
    """
    H, W = deckung.shape
    achse = np.asarray(achse, np.float32)
    achse = achse / np.linalg.norm(achse)
    hoch = np.asarray(hoch, np.float32)
    hoch = hoch - achse * float(hoch @ achse)
    hoch = hoch / np.linalg.norm(hoch)
    # The projector looks along -achse, so this is the right hand side of what
    # it sees. On the left cheek the axis flips and the marking mirrors with
    # it - which is exactly what a decal does.
    rechts = np.cross(hoch, achse)
    mitte = np.asarray(mitte, np.float32)

    a, b, c = V[F[:, 0]], V[F[:, 1]], V[F[:, 2]]
    fn = np.cross(b - a, c - a)
    ln = np.linalg.norm(fn, axis=1)
    fn = fn / np.where(ln > 0.0, ln, 1.0)[:, None]

    rel = V - mitte
    vs = (rel @ rechts) / groesse
    vt = (rel @ hoch) / groesse
    vd = rel @ achse

    # A face counts when it turns towards the projector and at least one of
    # its corners sits inside the marking's square and inside the depth window.
    drin = (np.abs(vs) <= 1.0) & (np.abs(vt) <= 1.0) & (np.abs(vd) <= tiefe)
    treffer = np.where((fn @ achse > STERN_KANTE) & drin[F].any(axis=1))[0]

    # Texel coordinates. v = 0 is the BOTTOM of the image - Unity's own
    # convention, and the texture is written out the same way it was read in.
    px = T[:, 0] * W
    py = (1.0 - T[:, 1]) * H

    # Four subsamples per texel. Without them the points of the star turn into
    # a staircase - the whole star is only about a hundred texels wide.
    unter = ((-0.25, -0.25), (0.25, -0.25), (-0.25, 0.25), (0.25, 0.25))

    gemalt = 0
    for i in treffer:
        ia, ib, ic = F[i]
        x = np.array([px[ia], px[ib], px[ic]], np.float64)
        y = np.array([py[ia], py[ib], py[ic]], np.float64)
        flaeche = (x[1] - x[0]) * (y[2] - y[0]) - (x[2] - x[0]) * (y[1] - y[0])
        if flaeche == 0.0:
            continue
        x0 = int(max(0, math.floor(x.min())))
        x1 = int(min(W - 1, math.ceil(x.max())))
        y0 = int(max(0, math.floor(y.min())))
        y1 = int(min(H - 1, math.ceil(y.max())))
        if x1 < x0 or y1 < y0:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5,
                             np.arange(y0, y1 + 1) + 0.5)
        deck = np.zeros(gx.shape, np.float32)
        for dx, dy in unter:
            sx, sy = gx + dx, gy + dy
            w0 = ((x[1] - x[0]) * (sy - y[0]) - (sx - x[0]) * (y[1] - y[0])) / flaeche
            w1 = ((sx - x[0]) * (y[2] - y[0]) - (x[2] - x[0]) * (sy - y[0])) / flaeche
            w2 = 1.0 - w0 - w1
            m = (w0 >= 0.0) & (w1 >= 0.0) & (w2 >= 0.0)
            if not m.any():
                continue
            ss = w2 * vs[ia] + w1 * vs[ib] + w0 * vs[ic]
            tt = w2 * vt[ia] + w1 * vt[ib] + w0 * vt[ic]
            dd = w2 * vd[ia] + w1 * vd[ib] + w0 * vd[ic]
            m &= np.abs(dd) <= tiefe
            if not m.any():
                continue
            deck[m & form(ss, tt)] += 0.25
        if deck.any():
            ziel = deckung[y0:y1 + 1, x0:x1 + 1]
            np.maximum(ziel, deck, out=ziel)
            gemalt += 1
    return gemalt


def aufweiten(deckung, radius):
    """Grow a coverage buffer by `radius` texels - the rim around the star.

    A rim of constant width in TEXELS, not a second star scaled up: a scaled
    star has a fat rim at its points and almost none in its notches.
    """
    out = deckung.copy()
    r = int(radius)
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            if dx * dx + dy * dy > radius * radius:
                continue
            np.maximum(out, np.roll(np.roll(deckung, dy, axis=0), dx, axis=1),
                       out=out)
    return out


def einbrennen(rgba, deckung, farbe, staerke):
    """Paint a coverage buffer onto the diffuse, keeping the drawing under it.

    Not a flat fill: every texel keeps its own brightness relative to the
    average, so seams, scorch marks and dirt still come through the paint. A
    marking that ignores them looks like a sticker stuck on a photograph.
    """
    lum = rgba[..., :3] @ np.array([0.299, 0.587, 0.114], np.float32)
    gain = np.clip(lum / max(1.0, float(lum.mean())), 0.45, 1.55)[..., None]
    ziel = np.clip(farbe.reshape(1, 1, 3) * gain, 0.0, 255.0)
    a = (deckung * staerke)[..., None]
    rgba[..., :3] = rgba[..., :3] * (1.0 - a) + ziel * a


def zeichen(rgba, V, T, F):
    """The red star on both cheeks of the turret.

    Reports how many texels it covers, so a silent miss - a game patch that
    moves the turret, a UV island that is no longer where it was - shows up as
    a zero in the build log instead of as a missing star in the game.
    """
    H, W = rgba.shape[0], rgba.shape[1]
    stern = stern_polygon()

    def form(s, t):
        return im_polygon(stern, s, t)

    deckung = np.zeros((H, W), np.float32)
    flaechen = 0
    for seite in (1.0, -1.0):
        mitte = np.array([seite * STERN_EBENE_X, STERN_MITTE[0], STERN_MITTE[1]],
                         np.float32)
        flaechen += projizieren(deckung, V, T, F, mitte,
                                np.array([seite, 0.0, 0.0], np.float32),
                                np.array([0.0, 0.0, 1.0], np.float32),
                                STERN_R, STERN_TIEFE, form)

    rand = np.clip(aufweiten(deckung, STERN_RAND_PIXEL) - deckung, 0.0, 1.0)
    einbrennen(rgba, rand, STERN_RAND, STERN_RAND_DECKUNG)
    einbrennen(rgba, deckung, STERN_ROT, STERN_ROT_DECKUNG)
    return int((deckung > 0.5).sum()), flaechen


# ------------------------------------------------------------------- Lauf

if __name__ == "__main__":
    configure_paths()
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
    # The star is projected onto exactly this geometry further down, and its
    # UVs are already the atlas UVs - so mesh and texture cannot drift apart.
    turm_geo = (V, T, F)

    body = spieltextur(TEX_BODY)
    turm_t = spieltextur(TEX_TURM)
    d = atlas(erwecken(body), erwecken(turm_t))
    texel, flaechen = zeichen(d, *turm_geo)
    if texel == 0:
        raise SystemExit("The red star did not land on a single texel - the "
                         "projection plane misses the turret. Check "
                         "STERN_EBENE_X against the printed turret bounds.")
    print("red star: %d texels on %d faces of the turret, both cheeks"
          % (texel, flaechen))
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
