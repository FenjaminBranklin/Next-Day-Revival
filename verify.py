"""Statische Pruefung des gebauten Plugins und der Assets.

Das Spiel kann hier nicht gestartet werden, also wird alles geprueft, was sich
ohne Start pruefen laesst. Die drei Fehlerarten, die sonst erst im Spiel
auffallen und dort nur als stilles Nichtstun erscheinen:

 1. Ein Tippfehler in GetMethod("Prefix") liefert zur Laufzeit null. Harmony
    patcht dann einfach nicht, ohne Fehlermeldung.
 2. Eine fehlende Assetdatei laesst das Item stumm ausfallen.
 3. Eine Null-Normale im Mesh wird im Shader zu NaN und frisst als weisser
    Fleck den Bildschirm.

    python verify.py
"""

import io
import os
import struct
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# ildasm.py liest die IL des gebauten Plugins. Es liegt beim Masterserver und
# nicht in diesem Repository - der Ordner wird daneben gesucht, keine feste
# Adresse. Fehlt er, entfaellt Pruefung [1] und alles andere laeuft weiter.
_tools = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                      "NextDaySurvival_Stage64_PhotonOwnAppId", "tools")
if os.path.isdir(_tools):
    sys.path.insert(0, _tools)
try:
    import ildasm
except ImportError:
    ildasm = None

ROOT = os.path.dirname(os.path.abspath(__file__))
# Nach einem Bau liegt die DLL in build\, im ausgelieferten Repository
# dagegen im Wurzelverzeichnis. Der frischere Stand gewinnt.
DLL = os.path.join(ROOT, "build", "NextDayRevivalToolkit.dll")
_fertig = os.path.join(ROOT, "NextDayRevivalToolkit.dll")
if os.path.exists(_fertig) and (not os.path.exists(DLL)
                                or os.path.getmtime(_fertig) > os.path.getmtime(DLL)):
    DLL = _fertig
SRC = os.path.join(ROOT, "RevivalPlugin.cs")
ASSETS = os.path.join(ROOT, "assets")


def _spielpfad():
    """Sucht die Spielinstallation - Registry, dann die Steam-Bibliotheken."""
    kandidaten = []
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as k:
            steam = winreg.QueryValueEx(k, "SteamPath")[0]
    except Exception:
        steam = ""
    for basis in [steam, r"C:\Program Files (x86)\Steam", r"C:\Program Files\Steam"]:
        if not basis:
            continue
        kandidaten.append(os.path.join(basis, "steamapps", "common", "Next Day Survival"))
        vdf = os.path.join(basis, "steamapps", "libraryfolders.vdf")
        if os.path.exists(vdf):
            import re
            txt = io.open(vdf, encoding="utf-8", errors="replace").read()
            for m in re.finditer(r'"path"\s+"([^"]+)"', txt):
                kandidaten.append(os.path.join(m.group(1).replace("\\\\", "\\"),
                                               "steamapps", "common", "Next Day Survival"))
    for k in kandidaten:
        if os.path.exists(os.path.join(k, "nextday_game.exe")):
            return k
    return ""


GAME = _spielpfad()
GAME_PLUGINS = os.path.join(GAME, "BepInEx", "plugins") if GAME else ""

ASSET_FILES = [
    "mg42.ndmesh", "mg42_diffuse.png", "mg42_normal.png",
    "mg42_icon.png", "mg42_weapon_icon.png",
    "sniper50.ndmesh", "sniper50_diffuse.png", "sniper50_normal.png",
    "sniper50_icon.png", "sniper50_weapon_icon.png",
    "mgbelt.ndmesh", "mgbelt_diffuse.png", "mgbelt_normal.png", "mgbelt_icon.png",
    "ammo50.ndmesh", "ammo50_diffuse.png", "ammo50_normal.png", "ammo50_icon.png",
    "law.ndmesh", "law_diffuse.png", "law_normal.png",
    "law_icon.png", "law_weapon_icon.png",
    "rocket.ndmesh", "rocket_diffuse.png", "rocket_normal.png", "rocket_icon.png",
    "drone.ndmesh", "drone_diffuse.png", "drone_normal.png", "drone_icon.png",
    "jammer.ndmesh", "jammer_diffuse.png", "jammer_normal.png", "jammer_icon.png",
    "t72_hull.ndmesh", "t72_turret.ndmesh",
    "t72_diffuse.png", "t72_normal.png", "t72_metal.png", "t72_scope.png",
    "apc_scope.png",
    "shell125.ndmesh", "shell125_diffuse.png", "shell125_normal.png",
    "shell125_icon.png",
    "scope50.png",
]

MESHES = ["mg42.ndmesh", "sniper50.ndmesh", "mgbelt.ndmesh", "ammo50.ndmesh",
          "law.ndmesh", "rocket.ndmesh", "drone.ndmesh", "jammer.ndmesh",
          "t72_hull.ndmesh", "t72_turret.ndmesh", "shell125.ndmesh"]

# Erwartete Bildgroessen, abgelesen an den Spielvorlagen.
ICON_SIZES = {
    "mg42_icon.png": (300, 300), "sniper50_icon.png": (300, 300),
    "mgbelt_icon.png": (300, 300), "ammo50_icon.png": (300, 300),
    "law_icon.png": (300, 300), "rocket_icon.png": (300, 300),
    "drone_icon.png": (300, 300), "jammer_icon.png": (300, 300),
    "shell125_icon.png": (300, 300),
    "mg42_weapon_icon.png": (317, 183), "sniper50_weapon_icon.png": (317, 183),
    "law_weapon_icon.png": (317, 183),
    "scope50.png": (1920, 1920),
    "t72_scope.png": (1920, 1920),
    "apc_scope.png": (1920, 1920),
}

fails = []
warns = []


def ok(msg):
    print("  OK    " + msg)


def bad(msg):
    fails.append(msg)
    print("  FEHLT " + msg)


def warn(msg):
    warns.append(msg)
    print("  HINW  " + msg)


# --------------------------------------------------------------------- DLL

def check_dll():
    print("[1] Gebaute DLL")
    if not os.path.exists(DLL):
        bad("build/NextDayRevivalToolkit.dll fehlt - build.ps1 -NoInstall laufen lassen")
        return None
    if ildasm is None:
        warn("ildasm.py fehlt - IL des Plugins nicht geprueft")
        return None
    a = ildasm.Asm(DLL)
    types = set()
    for td in a.md.TypeDef.rows:
        types.add(a._s(td.TypeName))
    want_types = ["RevivalPlugin", "ItemDef", "ItemFactory", "ResourceHook",
                  "LocalizationHook", "CursorTracker", "CursorGuard", "Assets",
                   "Registry", "WeaponData", "Diag", "Research", "Regions",
                   "RocketHook", "Turret", "Arena"]
    for t in want_types:
        if t in types:
            ok("Typ " + t)
        else:
            bad("Typ " + t + " nicht in der DLL")
    print("      %d Typen, %d Methoden" % (len(types), len(a.methods)))
    return a


def check_reflection_targets(a):
    """Jeder GetMethod("...")-String muss eine Methode sein, die es gibt.

    Harmony bekommt sonst null und patcht stillschweigend nicht.
    """
    print("[2] Reflexionsziele aus dem Quelltext")
    src = io.open(SRC, encoding="utf-8").read()
    import re
    pairs = re.findall(r'typeof\((\w+)\)\.GetMethod\("(\w+)"\)', src)
    if not pairs:
        warn("keine typeof(X).GetMethod(\"y\")-Paare gefunden")
    for cls, meth in pairs:
        key = cls + "::" + meth
        if key in a.methods:
            ok("%s.%s vorhanden" % (cls, meth))
        else:
            bad("%s.%s wird per Reflexion gesucht, existiert aber nicht" % (cls, meth))


def check_version():
    """VERSION-Datei und die Konstante im Quelltext muessen gleich sein.

    Die Datei ist das, was ein Launcher oder ein Updater von der Platte liest;
    die Konstante ist das, was BepInEx im Log anzeigt und was das Plugin von
    sich selbst behauptet. Laufen sie auseinander, meldet ein Client eine
    Version und liefert eine andere - dann ist jeder Abgleich mit dem Server
    wertlos.
    """
    print("[10] Versionsnummer")
    datei = os.path.join(ROOT, "VERSION")
    if not os.path.exists(datei):
        bad("VERSION fehlt im Wurzelverzeichnis")
        return
    aus_datei = io.open(datei, encoding="utf-8").read().strip()
    src = io.open(SRC, encoding="utf-8").read()
    import re
    m = re.search(r'const\s+string\s+VERSION\s*=\s*"([^"]+)"', src)
    if m is None:
        bad("VERSION-Konstante in RevivalPlugin.cs nicht gefunden")
        return
    if aus_datei == m.group(1):
        ok("VERSION und RevivalPlugin.VERSION stimmen ueberein: " + aus_datei)
    else:
        bad("VERSION-Datei sagt %s, RevivalPlugin.cs sagt %s"
            % (aus_datei, m.group(1)))


def check_item_table():
    """Jede Assetdatei aus der Tabelle muss auch wirklich dort liegen."""
    print("[3] Item-Tabelle gegen das assets-Verzeichnis")
    src = io.open(SRC, encoding="utf-8").read()
    import re
    # Der Name muss mit Buchstabe oder Ziffer anfangen. Was mit Unterstrich
    # beginnt, ist im Quelltext kein Dateiname, sondern eine ENDUNG: das
    # Waffenmaterial baut "<stamm>_diffuse.png" zu "<stamm>_metal.png" um.
    # Ohne diese Einschraenkung meldet die Pruefung zwei Dateien als fehlend,
    # die es nie geben soll.
    named = set(re.findall(r'"([A-Za-z0-9][A-Za-z0-9_]*\.(?:ndmesh|png))"', src))
    for f in sorted(named):
        p = os.path.join(ASSETS, f)
        if os.path.exists(p):
            ok("%-26s %8d Bytes" % (f, os.path.getsize(p)))
        else:
            bad("im Quelltext genannt, aber nicht vorhanden: " + f)
    for f in ASSET_FILES:
        if f not in named:
            warn("liegt im assets-Ordner, wird im Quelltext aber nicht genannt: " + f)


# ------------------------------------------------------------------ Meshes

def read_mesh(path):
    d = io.open(path, "rb").read()
    if d[:4] != b"NDMS":
        raise ValueError("falsche Magic")
    n = struct.unpack_from("<i", d, 8)[0]
    off = 12
    V = struct.unpack_from("<%df" % (n * 3), d, off); off += n * 12
    N = struct.unpack_from("<%df" % (n * 3), d, off); off += n * 12
    T = struct.unpack_from("<%df" % (n * 2), d, off); off += n * 8
    m = struct.unpack_from("<i", d, off)[0]; off += 4
    I = struct.unpack_from("<%di" % m, d, off)
    return n, V, N, T, m, I


def check_meshes():
    print("[4] Meshes")
    for f in MESHES:
        p = os.path.join(ASSETS, f)
        if not os.path.exists(p):
            bad("Mesh fehlt: " + f)
            continue
        try:
            n, V, N, T, m, I = read_mesh(p)
        except Exception as ex:
            bad("%s nicht lesbar: %s" % (f, ex))
            continue

        zero = 0
        unnorm = 0
        for i in range(n):
            x, y, z = N[3 * i], N[3 * i + 1], N[3 * i + 2]
            ln = (x * x + y * y + z * z) ** 0.5
            if ln < 1e-6:
                zero += 1
            elif not (0.99 < ln < 1.01):
                unnorm += 1

        degen = 0
        for t in range(0, m, 3):
            a, b, c = I[t], I[t + 1], I[t + 2]
            ax, ay, az = V[3 * a], V[3 * a + 1], V[3 * a + 2]
            bx, by, bz = V[3 * b], V[3 * b + 1], V[3 * b + 2]
            cx, cy, cz = V[3 * c], V[3 * c + 1], V[3 * c + 2]
            ux, uy, uz = bx - ax, by - ay, bz - az
            vx, vy, vz = cx - ax, cy - ay, cz - az
            nx = uy * vz - uz * vy
            ny = uz * vx - ux * vz
            nz = ux * vy - uy * vx
            if (nx * nx + ny * ny + nz * nz) ** 0.5 * 0.5 < 1e-10:
                degen += 1

        uv_bad = sum(1 for i in range(n)
                     if not (0.0 <= T[2 * i] <= 1.0 and 0.0 <= T[2 * i + 1] <= 1.0))
        idx_bad = sum(1 for i in I if i < 0 or i >= n)
        nan = sum(1 for x in V if x != x) + sum(1 for x in N if x != x)

        xs = V[0::3]; ys = V[1::3]; zs = V[2::3]
        line = ("%-16s %5d Vert  %5d Tri  x %6.3f..%6.3f  y %6.3f..%6.3f  z %6.3f..%6.3f"
                % (f, n, m // 3, min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)))
        if zero or degen or uv_bad or idx_bad or nan or unnorm:
            bad(line + "  -> Null-Normalen %d, entartet %d, UV %d, Index %d, NaN %d, unnormiert %d"
                % (zero, degen, uv_bad, idx_bad, nan, unnorm))
        else:
            ok(line)


def check_grip_alignment():
    """Die Handposition ist der einzige Wert, an dem die Waffen ausgerichtet sind.

    Aus dem RPD-Mesh gemessen: Pistolengriff y 0.555 .. 0.692. Beide eigenen
    Waffen muessen dort Geometrie unterhalb der Laufachse haben, sonst greift die
    Hand ins Leere.
    """
    print("[5] Griff an der RPD-Handposition (y 0.555 .. 0.692, z unter 0.05)")
    for f in ("mg42.ndmesh", "sniper50.ndmesh", "law.ndmesh"):
        p = os.path.join(ASSETS, f)
        if not os.path.exists(p):
            continue
        n, V, N, T, m, I = read_mesh(p)
        hits = 0
        zmin = 9.0
        for i in range(n):
            y = V[3 * i + 1]
            z = V[3 * i + 2]
            if 0.555 <= y <= 0.692 and z < 0.05:
                hits += 1
                if z < zmin:
                    zmin = z
        if hits >= 20 and zmin < -0.08:
            ok("%-16s %4d Vertices im Griffbereich, reicht bis z %.3f" % (f, hits, zmin))
        else:
            bad("%s hat dort nur %d Vertices (tiefstes z %.3f) - Griff sitzt falsch"
                % (f, hits, zmin))


# ------------------------------------------------------------------ Bilder

def check_images():
    print("[6] Bildgroessen gegen die Spielvorlagen")
    from PIL import Image
    import numpy as np
    for f, size in sorted(ICON_SIZES.items()):
        p = os.path.join(ASSETS, f)
        if not os.path.exists(p):
            bad("Bild fehlt: " + f)
            continue
        im = Image.open(p)
        if (im.width, im.height) != size:
            bad("%s ist %dx%d, erwartet %dx%d" % (f, im.width, im.height, size[0], size[1]))
            continue
        a = np.asarray(im.convert("RGBA"))
        alpha = a[..., 3]
        filled = 100.0 * (alpha > 8).mean()
        if f in ("scope50.png", "t72_scope.png", "apc_scope.png"):
            # Alle drei sind Zielfernrohrblenden: aussen deckend, in der Mitte
            # ein Loch. Panzerglas und BTR-Optik haben zusaetzlich eine Vignette
            # zum Rand hin, deshalb ist "voellig frei" dort kleiner - 8 Prozent
            # reichen als Nachweis, dass ueberhaupt noch durchgesehen werden kann.
            opaque = 100.0 * (alpha > 247).mean()
            clear = 100.0 * (alpha < 8).mean()
            if opaque < 50 or clear < 8:
                bad("%s: %.1f %% deckend, %.1f %% frei - Linse stimmt nicht"
                    % (f, opaque, clear))
            else:
                ok("%s %dx%d  %.1f %% deckend, %.1f %% freie Linse"
                   % (f, im.width, im.height, opaque, clear))
        elif filled < 3.0:
            bad("%s ist praktisch leer (%.1f %% Alpha)" % (f, filled))
        else:
            ok("%-26s %4dx%-4d  %5.1f %% gefuellt" % (f, im.width, im.height, filled))


def check_installed():
    print("[7] Installierter Stand im Spielordner")
    dll = os.path.join(GAME_PLUGINS, "NextDayRevivalToolkit.dll")
    if not os.path.exists(dll):
        warn("noch nichts installiert")
        return
    staged_time = os.path.getmtime(DLL) if os.path.exists(DLL) else 0
    if os.path.getmtime(dll) < staged_time:
        warn("installierte DLL ist aelter als die gebaute - build.ps1 nochmal, "
             "wenn das Spiel geschlossen ist")
    else:
        ok("DLL im Spielordner ist aktuell")
    dst = os.path.join(GAME_PLUGINS, "assets")
    stale = os.path.join(dst, "mg42_metallic.png")
    if os.path.exists(stale):
        warn("alte mg42_metallic.png liegt noch im Spielordner")
    for f in ASSET_FILES:
        if not os.path.exists(os.path.join(dst, f)):
            warn("noch nicht installiert: " + f)


def check_eac():
    """Schlaegt Alarm, wenn Easy Anti-Cheat wieder an ist.

    Steam kann Assembly-CSharp.dll jederzeit ersetzen - bei "Dateien auf
    Fehler ueberpruefen", bei einem Update, bei einer Neuinstallation. Der
    EAC-Patch ist dann weg, das Plugin laedt nicht mehr, und das sieht aus wie
    ein Fehler im Plugin. Deshalb hier zuerst nachsehen.
    """
    print("[8] EAC-Patch im Spielcode")
    try:
        import eacpatch
    except Exception as ex:
        warn("eacpatch.py nicht vorhanden - EAC nicht geprueft. Das Werkzeug "
             "gehoert nicht zu diesem Repository; wenn das Plugin nicht laedt, "
             "client_patch.ps1 nochmal laufen lassen. (%s)" % ex)
        return
    state = eacpatch.describe(eacpatch.DLL)[0]
    if state.startswith("GEPATCHT"):
        ok("IsDisabledEAC liefert true - EAC ist aus")
    elif state.startswith("ORIGINAL"):
        bad("EAC ist WIEDER AN. Das Plugin wird nicht laden. "
            "Beheben mit: python eacpatch.py patch")
    else:
        warn("EAC-Zustand unklar (%s) - python eacpatch.py status" % state)


def check_winding():
    """Zeigt die Vorderseite jedes Dreiecks nach aussen?

    Unitys Konvention ist am Spiel selbst gemessen (2026-08-28): `BoxAmmo01`
    und `Battery` aus `resources.assets` als OBJ exportiert - der UnityPy-
    Exporter spiegelt x und dreht die Eckenreihenfolge, zwei Umkehrungen, die
    sich aufheben - und dort zeigt die Rechte-Hand-Normale der Wicklung bei
    2659 von 2664 Dreiecken in dieselbe Richtung wie die gespeicherte Normale.

    Daraus zwei Bedingungen, die beide gelten muessen:

      1. Je Dreieck: gespeicherte Normale und Wicklung zeigen dieselbe Seite.
         Sonst wird die Flaeche weggecullt, waehrend sie beleuchtet wird - die
         Waffe sieht aus, als koenne man hineinsehen.
      2. Je geschlossenem Koerper: vorzeichenbehaftetes Volumen positiv.

    Bis 2026-08-28 war beides im ganzen Baukasten verkehrt herum.

    BEDINGUNG 2 GILT NUR FUER GESCHLOSSENE KOERPER, und nicht jedes Mesh ist
    einer. Wer eine Bohrung hat, hat eine nach INNEN gedrehte Flaeche darin,
    und die zaehlt negativ; wer zusaetzlich eine zweite Flaechenschicht traegt,
    zaehlt sein Volumen ein zweites Mal mit umgekehrtem Vorzeichen. Bei solchen
    Meshes sagt das Vorzeichen nichts ueber den Umlaufsinn - Bedingung 1 sagt
    es weiterhin, und die ist die schaerfere von beiden. Sie stehen in
    `NICHT_GESCHLOSSEN`, jedes mit dem Grund, aus dem es dort steht.
    """
    # Mesh -> warum sein Volumen nicht aussagekraeftig ist.
    NICHT_GESCHLOSSEN = {
        "law.ndmesh": "Bohrung ueber die ganze Laenge, dazu eine zweite "
                      "Flaechenschicht auf der Aussenhaut",
    }

    print("[9] Vorderseiten und Umlaufsinn")
    for f in MESHES:
        p = os.path.join(ASSETS, f)
        if not os.path.exists(p):
            continue
        try:
            n, V, N, T, m, I = read_mesh(p)
        except Exception:
            continue

        vol = 0.0
        gegen = 0
        for t in range(0, m, 3):
            a, b, c = I[t], I[t + 1], I[t + 2]
            pa = (V[3 * a], V[3 * a + 1], V[3 * a + 2])
            pb = (V[3 * b], V[3 * b + 1], V[3 * b + 2])
            pc = (V[3 * c], V[3 * c + 1], V[3 * c + 2])
            vol += (pa[0] * (pb[1] * pc[2] - pb[2] * pc[1])
                    - pa[1] * (pb[0] * pc[2] - pb[2] * pc[0])
                    + pa[2] * (pb[0] * pc[1] - pb[1] * pc[0])) / 6.0
            u = (pb[0] - pa[0], pb[1] - pa[1], pb[2] - pa[2])
            v = (pc[0] - pa[0], pc[1] - pa[1], pc[2] - pa[2])
            fn = (u[1] * v[2] - u[2] * v[1],
                  u[2] * v[0] - u[0] * v[2],
                  u[0] * v[1] - u[1] * v[0])
            nv = (N[3 * a], N[3 * a + 1], N[3 * a + 2])
            if fn[0] * nv[0] + fn[1] * nv[1] + fn[2] * nv[2] < 0:
                gegen += 1

        line = "%-16s %5d Tri  Volumen %+8.5f" % (f, m // 3, vol)
        if gegen:
            bad(line + "  -> %d Dreiecke zeigen die Rueckseite nach aussen "
                       "(im Spiel unsichtbar)" % gegen)
        elif f in NICHT_GESCHLOSSEN:
            ok(line + "  Vorderseiten stimmen (kein geschlossener Koerper: "
               + NICHT_GESCHLOSSEN[f] + ")")
        elif vol < -1e-9:
            bad(line + "  -> Koerper ist nach innen gewickelt")
        elif abs(vol) <= 1e-9:
            ok(line + "  Vorderseiten stimmen (Volumen 0: doppelt gebaute "
                      "Flaechen, beidseitig sichtbar)")
        else:
            ok(line + "  Vorderseiten stimmen")


if __name__ == "__main__":
    print("=" * 74)
    print("Statische Pruefung des Revival Toolkits")
    print("=" * 74)
    asm = check_dll()
    if asm is not None:
        check_reflection_targets(asm)
    check_item_table()
    check_meshes()
    check_grip_alignment()
    check_images()
    check_installed()
    check_eac()
    check_winding()
    check_version()
    print("=" * 74)
    print("Fehler: %d    Hinweise: %d" % (len(fails), len(warns)))
    for f in fails:
        print("  FEHLER  " + f)
    for w in warns:
        print("  HINWEIS " + w)
    sys.exit(1 if fails else 0)
