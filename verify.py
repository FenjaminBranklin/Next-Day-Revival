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
    "m7.ndmesh", "m7_diffuse.png", "m7_normal.png",
    "m7_icon.png", "m7_weapon_icon.png",
    "mag68box.ndmesh", "mag68box_diffuse.png", "mag68box_normal.png",
    "mag68box_icon.png",
    "mag68drum.ndmesh", "mag68drum_diffuse.png", "mag68drum_normal.png",
    "mag68drum_icon.png",
    "mgbelt.ndmesh", "mgbelt_diffuse.png", "mgbelt_normal.png", "mgbelt_icon.png",
    "ammo50.ndmesh", "ammo50_diffuse.png", "ammo50_normal.png", "ammo50_icon.png",
    "law.ndmesh", "law_diffuse.png", "law_normal.png",
    "law_icon.png", "law_weapon_icon.png",
    "rocket.ndmesh", "rocket_diffuse.png", "rocket_normal.png", "rocket_icon.png",
    "drone.ndmesh", "drone_diffuse.png", "drone_normal.png", "drone_icon.png",
    "jammer.ndmesh", "jammer_diffuse.png", "jammer_normal.png", "jammer_icon.png",
    "antenna_head.ndmesh",
    # Vehicle modules (2060/2061/2062) - own art (vehicle_modules_build.py).
    # Before that all three wore the portable jammer's model, textures and icon.
    "thermal.ndmesh", "thermal_diffuse.png", "thermal_normal.png",
    "thermal_icon.png",
    "nvmodule.ndmesh", "nvmodule_diffuse.png", "nvmodule_normal.png",
    "nvmodule_icon.png",
    "jammod.ndmesh", "jammod_diffuse.png", "jammod_normal.png",
    "jammod_icon.png",
    # Drone gear (2055/2056/2057) - own art (drone_gear_build.py). Before that
    # the jammer, the .50 ammo tin and the FPV drone stood in for them.
    "antenna_pack.ndmesh", "antenna_pack_diffuse.png", "antenna_pack_normal.png",
    "antenna_pack_icon.png",
    "battery.ndmesh", "battery_diffuse.png", "battery_normal.png",
    "battery_icon.png",
    "survdrone.ndmesh", "survdrone_diffuse.png", "survdrone_normal.png",
    "survdrone_icon.png",
    "fireext.ndmesh", "fireext_diffuse.png", "fireext_normal.png", "fireext_icon.png",
    "toolkit.ndmesh", "toolkit_diffuse.png", "toolkit_normal.png", "toolkit_icon.png",
    "mine.ndmesh", "mine_diffuse.png", "mine_normal.png", "mine_icon.png",
    "t72_hull.ndmesh", "t72_turret.ndmesh",
    "t72_track_left.ndmesh", "t72_track_right.ndmesh", "t72_track.png",
    "t72_diffuse.png", "t72_normal.png", "t72_metal.png", "t72_scope.png",
    "apc_scope.png",
    "shell125.ndmesh", "shell125_diffuse.png", "shell125_normal.png",
    "shell125_icon.png",
    "scope50.png",
]

# Dateinamen, die der Quelltext NENNT, ohne dass es sie geben muss.
#
# Das Artilleriefahrzeug der Siedlungen (ArtyModel in RevivalArtyBattery.cs)
# ist erzeugte Geometrie wie das alte Moerserrohr. Es SUCHT aber zuerst nach
# einem echten Modell und nimmt es, sobald es da liegt - jede dieser Dateien
# haengt hinter File.Exists bzw. Assets.TextureIfPresent, das Fehlen ist der
# Normalfall und kein Fehler. Wird ein echtes Modell geliefert, gehoert es in
# ASSET_FILES, in make_assets.py und in die Startquittung (ClientIntegrity
# lehnt jede Datei unter plugins\assets ab, die die Quittung nicht kennt).
OPTIONAL_ASSETS = [
    "arty_hull.ndmesh", "arty_turret.ndmesh", "arty_barrel.ndmesh",
    "arty_diffuse.png",
]

MESHES = ["mg42.ndmesh", "sniper50.ndmesh", "m7.ndmesh", "mag68box.ndmesh",
          "mag68drum.ndmesh", "mgbelt.ndmesh", "ammo50.ndmesh", "law.ndmesh",
          "rocket.ndmesh", "drone.ndmesh", "jammer.ndmesh", "antenna_head.ndmesh",
          "fireext.ndmesh", "toolkit.ndmesh", "mine.ndmesh", "t72_hull.ndmesh",
          "t72_turret.ndmesh", "t72_track_left.ndmesh", "t72_track_right.ndmesh",
          "shell125.ndmesh", "thermal.ndmesh", "nvmodule.ndmesh",
          "jammod.ndmesh", "antenna_pack.ndmesh", "battery.ndmesh",
          "survdrone.ndmesh"]

# Erwartete Bildgroessen, abgelesen an den Spielvorlagen.
ICON_SIZES = {
    "mg42_icon.png": (300, 300), "sniper50_icon.png": (300, 300),
    "m7_icon.png": (300, 300),
    "mag68box_icon.png": (300, 300), "mag68drum_icon.png": (300, 300),
    "mgbelt_icon.png": (300, 300), "ammo50_icon.png": (300, 300),
    "law_icon.png": (300, 300), "rocket_icon.png": (300, 300),
    "drone_icon.png": (300, 300), "jammer_icon.png": (300, 300),
    "fireext_icon.png": (300, 300), "toolkit_icon.png": (300, 300),
    "mine_icon.png": (300, 300),
    "shell125_icon.png": (300, 300),
    "thermal_icon.png": (300, 300), "nvmodule_icon.png": (300, 300),
    "jammod_icon.png": (300, 300), "antenna_pack_icon.png": (300, 300),
    "battery_icon.png": (300, 300), "survdrone_icon.png": (300, 300),
    "mg42_weapon_icon.png": (317, 183), "sniper50_weapon_icon.png": (317, 183),
    "m7_weapon_icon.png": (317, 183),
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
    # Scan every top-level .cs build.ps1 compiles, not just RevivalPlugin.cs:
    # items in RevivalM7Rifle.cs / Revival.Modules.cs etc. name their own art.
    import glob
    src = "".join(io.open(p, encoding="utf-8").read()
                  for p in glob.glob(os.path.join(ROOT, "*.cs")))
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
        elif f in OPTIONAL_ASSETS:
            # Optional: der Quelltext fragt danach und baut ohne sie weiter.
            ok("%-26s optional, nicht vorhanden" % f)
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
    for f in ("mg42.ndmesh", "sniper50.ndmesh", "m7.ndmesh", "law.ndmesh"):
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


def check_mine():
    """[11] Anti-tank mine: the structural invariants that can be checked
    without the game - id/donor/category, the no-throw guard, the placement
    lock, the single-fire guard, the vehicle-only filter, the unconditional
    kill, consume-exactly-one, and the RevivalPlugin seams. Runtime behaviour
    (a real vehicle actually triggering it in a networked session) stays an
    in-game acceptance item.
    """
    print("[11] Panzerabwehrmine (statisch)")
    mine_p = os.path.join(ROOT, "RevivalAntiTankMine.cs")
    plug_p = os.path.join(ROOT, "RevivalPlugin.cs")
    if not os.path.exists(mine_p):
        bad("RevivalAntiTankMine.cs fehlt")
        return
    s = io.open(mine_p, encoding="utf-8").read()
    plug = io.open(plug_p, encoding="utf-8").read() if os.path.exists(plug_p) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Mine: " + why)

    need("DEF_MINE = 1490" in s, "Id 1490", "Item-Id 1490 nicht gesetzt")
    need("DEF_DONOR = 1403" in s,
         "Spende 1403 (Granatenkategorie)",
         "Spender ist nicht die Granate 1403 - dann keine Granatenkategorie")
    # The id IS the equip category: ItemDataManager::GetItemCatData is a
    # hard-coded id-range switch, and only 1401..1500 lights up the grenade
    # slot. An id outside the donor's band cannot be equipped at all.
    # (No regex here on purpose - verify.py does not import re.)
    mine_id = -1
    marker = "DEF_MINE = "
    if marker in s:
        digits = ""
        for ch in s[s.index(marker) + len(marker):]:
            if not ch.isdigit():
                break
            digits += ch
        if digits:
            mine_id = int(digits)
    need(1401 <= mine_id <= 1500,
         "Id liegt im Granatenband 1401..1500",
         "Mine-Id liegt ausserhalb 1401..1500 - dann nimmt kein Waffenslot sie an")
    # And it must stay there: a config key would let a .cfg put it back into
    # the ammunition band, which is exactly how 2065 became unequippable.
    need("CfgMineId" not in s,
         "keine frei setzbare Mine-Id (Band bleibt garantiert)",
         "MineId ist wieder konfigurierbar - eine .cfg kann die Mine unausruestbar machen")
    # No-throw: the CantThrowGrenade postfix forces __result true for the mine.
    need("CantThrowGrenade" in s and "__result = true" in s,
         "Linksklick-Wurfsperre (CantThrowGrenade -> true)",
         "keine CantThrowGrenade-Sperre gefunden")
    # Placement lock reads the Placing flag and patches the PlayerCant* set.
    need("AntiTankMine.Placing" in s and "PlayerCantMovement" in s,
         "Bewegungssperre an Placing gebunden",
         "Bewegungssperre nicht an Placing gebunden")
    # Single-fire guard.
    need("_fired" in s and "if (_fired) return;" in s and "_fired = true;" in s,
         "Einmalausloesung (_fired-Wache)",
         "keine _fired-Einmalwache")
    # Vehicle-only: only the VehicleGameSystem scan is tested.
    need("VehicleScan.All()" in s,
         "nur Fahrzeuge (VehicleScan.All)",
         "Ausloeser prueft nicht ausschliesslich Fahrzeuge")
    # Unconditional kill: ApplyDamage with the explosion part and the huge,
    # non-weapon KillDamage; plus the networked explosion visual.
    need("ApplyDamage" in s and ", 14 }" in s and "CfgKillDamage" in s,
         "garantierter Abschuss (ApplyDamage part 14, KillDamage)",
         "kein gezielter ApplyDamage-Abschuss")
    need("RocketHook.Detonate" in s,
         "vernetzte Explosion (RocketHook.Detonate)",
         "keine vernetzte Explosion")
    # Consume exactly one, only in Finish (after a successful placement).
    equipped_consume = ("|| ConsumeEquipped(ctrl)" in s
                        and "new object[] { 2, MineId, true, false }" in s
                        and "items.GetValue(2)) != MineId" in s
                        and "mine.SetActive(false)" in s
                        and "UnityEngine.Object.Destroy(mine)" in s)
    need("TakeItem(MineId" in s or equipped_consume,
         "verbraucht genau eine Mine bei Erfolg",
         "neither inventory nor verified equipped-slot consumption found")
    # Slot-3 equipment requires BOTH a hand prefab and grenade weapon data.
    need("DEF_MINE, DEF_DONOR, true," in s
         and "MineGrenadeDataHook.Install(harmony)" in s
         and '"GetGrenadeWeaponData"' in s,
         "mine hand model and grenade data enabled",
         "missing mine equipment model or grenade data hook")
    need("AntiTankMine.PlaceFromController(__instance as Component)" in s
         and "if (__result) return;" in s
         and "_lastPlaceFrame == Time.frameCount" in s
         and "finally { _placing = false; }" in s,
         "left-click placement preserves game guards and releases its lock",
         "left-click placement guard or cleanup missing")
    # RevivalPlugin seams.
    for seam in ("AntiTankMine.AddItems", "AntiTankMine.BindConfig",
                 "AntiTankMine.Install", "AntiTankMine.Tick", "AntiTankMine.Draw"):
        need(seam in plug, "Seam " + seam, "Seam fehlt in RevivalPlugin.cs: " + seam)


def check_convoy_ground_and_exit():
    """Regression guards for the convoy road collision and all-vehicle exit.

    Runtime movement still needs an in-game acceptance pass. These checks keep
    the two exact structural mistakes from returning: treating only a Unity
    Terrain component as ground, and deriving local exit ownership only from
    the BTR/T-72 turret scan.
    """
    print("[12] Convoy ground and vehicle exit (static)")
    patrol_p = os.path.join(ROOT, "Revival.Patrol.cs")
    camera_p = os.path.join(ROOT, "Revival.CameraTurret.cs")
    if not os.path.exists(patrol_p) or not os.path.exists(camera_p):
        bad("Convoy ground check: source file missing")
        return
    patrol = io.open(patrol_p, encoding="utf-8").read()
    camera = io.open(camera_p, encoding="utf-8").read()

    if ("IsDriveSurface(go, normal)" in patrol
            and 'name.IndexOf("road")' in patrol
            and 'name.IndexOf("ground")' in patrol):
        ok("mesh roads remain solid during convoy ghosting")
    else:
        bad("Convoy ground check: mesh-road collision guard missing")

    if ("out Vector3 normal" in camera
            and '_hitType.GetProperty("normal"' in camera):
        ok("raycast hit normal is available to ground classification")
    else:
        bad("Convoy ground check: RaycastHit.normal seam missing")

    if ("IsLocalVehicleManager(__instance)" in camera
            and "VehicleScan.All()" in camera
            and 'IntField(vgs, "_localPlayerPassengerId")' in camera):
        ok("exit cleanup covers the local Ural and other vehicle types")
    else:
        bad("Vehicle exit check: cleanup is still limited to turret vehicles")


def check_convoy_column():
    """Regression guards for the convoy column (6.8.4).

    The three mistakes these keep from coming back are the ones the user hit:
    a convoy that loses its formation and its editor order within seconds of
    the spawn, a single crewman climbing out of every vehicle, and an editor
    uniform that is dropped because only one of the game's two appearance
    builders was hooked. Movement itself still needs an in-game acceptance.
    """
    print("[13] Convoy column, crew size and uniform (static)")
    patrol_p = os.path.join(ROOT, "Revival.Patrol.cs")
    convoy_p = os.path.join(ROOT, "RevivalConvoy.cs")
    crew_p = os.path.join(ROOT, "Revival.Crew.cs")
    for path in (patrol_p, convoy_p, crew_p):
        if not os.path.exists(path):
            bad("Convoy column check: source file missing: "
                + os.path.basename(path))
            return
    patrol = io.open(patrol_p, encoding="utf-8").read()
    convoy = io.open(convoy_p, encoding="utf-8").read()
    crew = io.open(crew_p, encoding="utf-8").read()

    # 1. The formation lock itself: an intact convoy is carried on the recorded
    #    line and the per-vehicle driver does not touch it.
    if ("static void Columns()" in patrol
            and "static void ColumnStep(" in patrol
            and "static void PlaceInColumn(" in patrol
            and "if (u.Column) continue;" in patrol
            and "try { Columns(); }" in patrol):
        ok("an intact convoy is carried as one column, not driven per vehicle")
    else:
        bad("Convoy column check: the formation lock is not wired into the driver")

    # 2. The column ends at the first loss and never re-forms - that is what
    #    hands the survivors to the behaviour layer.
    if ('ColumnBreak(u.ConvoyId, "vehicle destroyed")' in patrol
            and "internal static void ColumnBreak(" in patrol):
        ok("the first loss breaks the column and frees the survivors")
    else:
        bad("Convoy column check: a destroyed vehicle no longer breaks the column")

    # 3. The start line-up runs ALONG the recorded road. The old version
    #    extrapolated backwards off waypoint 0 and put the tail in a hillside.
    if ("float headArc" in convoy
            and "SpawnConvoyUnit(routeName, kind, headArc, back," in convoy
            and "PointOnRoute(r, arc, out seg)" in patrol):
        ok("the convoy lines up on the recorded road, not off its start")
    else:
        bad("Convoy column check: the start line-up is not on the route")

    # 4. Crew size comes from the seats; the editor list is the loadout.
    if "u.CrewSize = Mathf.Min(u.CrewSize, crew.Count)" in patrol:
        bad("Crew size check: the editor crew list still clamps the crew to one man")
    elif ("Mathf.Max(u.CrewSize, crew.Count)" in patrol
            and "composition[i % composition.Count]" in crew):
        ok("editor roles are a loadout template, the seats set the crew size")
    else:
        bad("Crew size check: the crew template path is missing")

    # 5. BOTH appearance builders carry the editor uniform. InitSpawnNpc calls
    #    GetCustomAppearanceItems when UseCustomAppearance is set and
    #    GetRandomAppearance otherwise; hooking one of them drops the uniform
    #    on every map that has a military template spawn point.
    if ('Anziehen(harmony, type, "GetRandomAppearance")' in crew
            and 'Anziehen(harmony, type, "GetCustomAppearanceItems")' in crew):
        ok("the editor uniform overlays both appearance paths")
    else:
        bad("Uniform check: only one appearance path carries the editor uniform")


def check_gas_launcher():
    """[14] Chemical launcher RG-Kh and its 30-minute gas cloud (static).

    The four mistakes this keeps from coming back are the ones that would make
    the weapon look broken instead of failing loudly: an id outside the grenade
    band (then no slot accepts the tube at all - the 2065 lesson), a missing
    grenade record (then the equipped tube has no data and is dropped), a shot
    that does not consume its one-shot tube, and a cloud that keeps poisoning
    after its time is up. Everything that needs eyes - the cloud in the sky,
    the mask actually saving a player - stays an in-game acceptance item.
    """
    print("[14] Chemie-Granatwerfer und Giftgaswolke (statisch)")
    gas_p = os.path.join(ROOT, "RevivalGasLauncher.cs")
    plug_p = os.path.join(ROOT, "RevivalPlugin.cs")
    if not os.path.exists(gas_p):
        bad("RevivalGasLauncher.cs fehlt")
        return
    s = io.open(gas_p, encoding="utf-8").read()
    plug = io.open(plug_p, encoding="utf-8").read() if os.path.exists(plug_p) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Gaswerfer: " + why)

    # The id IS the equip category (ItemDataManager::GetItemCatData is a
    # hard-coded id-range switch): only 1401..1500 lights up the grenade slot.
    need("DEF_LAUNCHER = 1491" in s, "Id 1491", "Item-Id 1491 nicht gesetzt")
    need("DEF_DONOR = 1403" in s,
         "Spende 1403 (Granatenkategorie)",
         "Spender ist nicht die Granate 1403 - dann keine Granatenkategorie")
    gas_id = -1
    marker = "DEF_LAUNCHER = "
    if marker in s:
        digits = ""
        for ch in s[s.index(marker) + len(marker):]:
            if not ch.isdigit():
                break
            digits += ch
        if digits:
            gas_id = int(digits)
    need(1401 <= gas_id <= 1500,
         "Id liegt im Granatenband 1401..1500",
         "Werfer-Id liegt ausserhalb 1401..1500 - dann nimmt kein Waffenslot sie an")
    need("CfgLauncherId" not in s,
         "keine frei setzbare Werfer-Id (Band bleibt garantiert)",
         "die Id ist konfigurierbar - eine .cfg kann die Waffe unausruestbar machen")
    # Equipment needs BOTH the hand model (ItemDef) and a grenade record that
    # no server delivers for 1491.
    need("DEF_LAUNCHER, DEF_DONOR, true," in s
         and "GasGrenadeDataHook.Install(harmony)" in s
         and '"GetGrenadeWeaponData"' in s,
         "Handmodell und Granatendaten fuer 1491 vorhanden",
         "Ausruestungsmodell oder Granatendaten fehlen")
    # Left click fires through the game's own throw guards.
    need("CantThrowGrenade" in s and "__result = true;" in s
         and "GasLauncher.FireFromController(__instance as Component)" in s
         and "_lastShotFrame == Time.frameCount" in s
         and "finally { _firing = false; }" in s,
         "Linksklick-Schuss haelt die Spielsperren ein und gibt sein Schloss frei",
         "Linksklick-Schuss oder seine Wache fehlt")
    # Exactly one tube per shot, taken from the equipped slot 2 - and no shot
    # at all when it cannot be taken.
    need("|| ConsumeEquipped(ctrl)" in s
         and "new object[] { 2, DEF_LAUNCHER, true, false }" in s
         and "ToInt(items.GetValue(2)) != DEF_LAUNCHER" in s
         and "if (!consumed)" in s,
         "verbraucht genau ein Rohr je Schuss, sonst faellt der Schuss aus",
         "Verbrauch der ausgeruesteten Rohrwaffe nicht nachweisbar")
    # The gas is the game's own Toxicity value, not a private counter.
    need('AccessTools.Field(data.GetType(), "Toxicity")' in s
         and "AddToxicity" in s,
         "Wirkung ueber PlayerLifeData.Toxicity des Spiels",
         "die Wolke schreibt nicht die Vergiftung des Spiels")
    # Protection: sealed masks, L-1 suits, and the full set as immunity.
    need('"4708,4710"' in s and '"4203,4204,4205"' in s
         and "FullSetImmune" in s,
         "Schutz durch Maske 4708/4710 und Anzug 4203-4205, Vollsatz immun",
         "Schutzausruestung nicht hinterlegt")
    # NPC damage: body part and damage type Toxicity (12), never head (0),
    # plus the id-0 kill-streak guard from RE 35.
    need("args[i] = 12;" in s and "args[i] = 1;" in s and "_lastKillerId" in s,
         "NPC-Schaden als Toxicity am Rumpf, mit Kill-Streak-Wache",
         "NPC-Schadensart/Trefferzone oder die Kill-Streak-Wache fehlt")
    # Time is up means time is up, and the map cannot fill with clouds.
    need("if (age >= _life) { Stop(); return; }" in s
         and "CfgMaxClouds" in s and "GasLauncher.Register(c)" in s,
         "Wolke endet mit ihrer Standzeit und ist mengenbegrenzt",
         "Standzeitende oder Mengenbegrenzung der Wolken fehlt")
    # One networked burst so a second client sees where the round landed.
    need("RocketHook.Detonate" in s,
         "vernetzter Zerleger am Einschlag (RocketHook.Detonate)",
         "kein vernetzter Einschlag")
    # RevivalPlugin seams.
    for seam in ("GasLauncher.AddItems", "GasLauncher.BindConfig",
                 "GasLauncher.Install", "GasLauncher.Tick", "GasLauncher.Draw"):
        need(seam in plug, "Seam " + seam, "Seam fehlt in RevivalPlugin.cs: " + seam)


def check_mortar():
    """[15] Settlement mortar: the invariants that decide whether the feature is
    correct rather than merely present.

    The one hard requirement of the order was that a casualty from the firing
    player's OWN faction must never turn him into a traitor. That rests on three
    things in the source - anonymous damage (owner 0 through Turret.TryDamage),
    a visible explosion that carries no damage of its own, and a player of the
    shooter's own faction who is not hit at all - and every one of them is a
    single line that a later edit could quietly undo. The rest of this section
    guards the two mistakes that were made and fixed while writing it: an event
    code inside the surveillance drone's block, and a kill credited to owner 0
    that throws in NPC_Settlement.StatsOnNpcKilled. Aim mode, the map ring and
    the flight of a bomb stay in-game acceptance items.
    """
    print("[15] Siedlungsmoerser (statisch)")
    mortar_p = os.path.join(ROOT, "RevivalMortar.cs")
    plug_p = os.path.join(ROOT, "RevivalPlugin.cs")
    if not os.path.exists(mortar_p):
        bad("RevivalMortar.cs fehlt")
        return
    s = io.open(mortar_p, encoding="utf-8").read()
    plug = io.open(plug_p, encoding="utf-8").read() if os.path.exists(plug_p) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Mortar: " + why)

    # --- the bomb is ammunition, and its id band is what makes it one.
    need("DEF_SHELL = 2066" in s, "Bomben-Id 2066",
         "Item-Id 2066 nicht gesetzt")
    shell_id = -1
    marker = "DEF_SHELL = "
    if marker in s:
        digits = ""
        for ch in s[s.index(marker) + len(marker):]:
            if not ch.isdigit():
                break
            digits += ch
        if digits:
            shell_id = int(digits)
    need(2001 <= shell_id <= 3000,
         "Id liegt im Munitionsband 2001..3000",
         "Bomben-Id liegt ausserhalb 2001..3000 - dann ist sie keine Munition")
    need("DEF_SHELL, DEF_DONOR, false," in s,
         "Bombe ist Munition, keine Waffe",
         "die Bombe ist als Waffe eingetragen")

    # --- THE FACTION RULE. Three independent lines, all three required.
    need('"BlastDamage", 0f' in s,
         "sichtbare Explosion ohne Schaden (BlastDamage 0)",
         "BlastDamage ist nicht 0 - dann gehoert jeder Tote dem Schuetzen")
    need('Turret.TryDamage(ai.gameObject, "NPC_AI2", "ApplyDamage", dmg)' in s,
         "NPC-Schaden anonym (Turret.TryDamage, Besitzer 0)",
         "NPC-Schaden laeuft nicht mehr ueber Turret.TryDamage")
    need("FactionShield.SameFactionAsLocal(go)" in s,
         "eigene Fraktion wird gar nicht getroffen",
         "der Schutz der eigenen Fraktion fehlt im Spielerdurchlauf")
    need("FactionShield.Arm();" in s and "const int Traitor = 6;" in s,
         "Fraktionsnetz bewacht jeden Feuerauftrag",
         "das Fraktionsnetz wird nicht mehr bewaffnet")

    # --- the owner-0 price: StatsOnNpcKilled throws on PhotonPlayer.Find(0).
    need("BreakKillStreak(ai);" in s and '"_lastKillerId"' in s,
         "Abschussserie mit Besitzer 0 wird gebrochen",
         "keine _lastKillerId-Wache - ein raeumender Treffer wirft")

    # --- the Photon channel must not sit in another feature's block.
    need("code >= surv && code <= surv + 3" in s
         and "code >= troops && code <= troops + 2" in s,
         "Ereigniscode kollidiert mit keinem anderen Kanal",
         "die Kanalpruefung kennt Aufklaerungsdrohne oder Truppen nicht")
    need('ps[1].ParameterType.Name != "PhotonPlayer"' in s,
         "Spieler-RPC waehlt die PhotonPlayer-Ueberladung",
         "die RPC-Auswahl kann die PhotonTargets-Ueberladung erwischen")

    # --- reach, ground and the overlay.
    need("Too far" in s and "Too close" in s,
         "Klick ausserhalb der Reichweite wird abgelehnt, nicht beschnitten",
         "keine Ablehnung fuer zu weit oder zu nah")
    need("RevivalTroopInsertion.GroundY" in s,
         "Boden fern vom Spieler ueber GroundY (E-059)",
         "Einschlagshoehe ohne GroundY - fern vom Spieler gibt es keinen Strahl")
    need("GUI.BeginClip(clip)" in s and "MapTools.MapViewportRect" in s,
         "Kartenoverlay hart auf das Kartenfenster geschnitten",
         "das Overlay ist nicht auf das Kartenfenster geschnitten")
    need("MaxTries" in s,
         "Aufstellung wird wiederholt, nicht einmal versucht",
         "ein Fehlversuch beim Aufstellen wird nicht wiederholt")
    need("if (Master())" in s and "{ dmg, 14 }" in s,
         "Fahrzeugschaden nur auf dem Master, ueber Teil 14",
         "Fahrzeugschaden nicht auf den Master begrenzt")

    # --- RevivalPlugin seams.
    for seam in ("Mortar.BindConfig", "Mortar.AddItems(Items)",
                 "Mortar.Tick()", "Mortar.Draw()"):
        need(seam in plug, "Seam " + seam,
             "Seam fehlt in RevivalPlugin.cs: " + seam)


def check_arty_battery():
    """[16] The settlement artillery vehicle, its crew and the recon drone.

    Six rules decide whether this feature is what was ordered rather than merely
    present, and every one of them is a line or two that a later edit could undo
    without anything looking broken:

      1. The crosshair may not outrun the turret. The whole point of the aim
         rework is that the lay is stepped with MoveTowardsAngle at the gun's
         own traverse rate and that the crosshair is DRAWN on that lay - draw it
         on the mouse again and the limit becomes invisible, which is the same
         as gone.
      2. A shell the NPC crew fires must never be one player damaging another.
         The master sweeps NPCs and vehicles; the blast on a player is applied
         by that player's own client through Self().
      3. The drone's circle must be the same circle on every client, so it comes
         from PhotonNetwork.time and a phase taken from the settlement's own
         position - never from an instance id or a random draw.
      4. The crew must take the settlement's own faction, or a gun crew stands
         in a village that shoots it.
      5. The map overlay must be hard-clipped to the map window, the rule the
         route overlay and the mortar ring both learned the hard way.
      6. RevivalArtyBattery.cs must stay ASCII: it is a machine-written file and
         build.ps1 requires BOM-less sources, so its Russian lives in
         RevivalMortar.cs instead.
    """
    print("[16] Artilleriefahrzeug, Besatzung und Aufklaerungsdrohne (statisch)")
    bat_p = os.path.join(ROOT, "RevivalArtyBattery.cs")
    mortar_p = os.path.join(ROOT, "RevivalMortar.cs")
    plug_p = os.path.join(ROOT, "RevivalPlugin.cs")
    sync_p = os.path.join(ROOT, "sync_public.py")
    if not os.path.exists(bat_p):
        bad("RevivalArtyBattery.cs fehlt")
        return
    raw = io.open(bat_p, "rb").read()
    b = raw.decode("utf-8", "replace")
    s = io.open(mortar_p, encoding="utf-8").read() if os.path.exists(mortar_p) else ""
    plug = io.open(plug_p, encoding="utf-8").read() if os.path.exists(plug_p) else ""
    sync = io.open(sync_p, encoding="utf-8").read() if os.path.exists(sync_p) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("ArtyBattery: " + why)

    # --- 6: ASCII, and no BOM in front of it.
    need(not raw.startswith(b"\xef\xbb\xbf"), "keine BOM",
         "RevivalArtyBattery.cs beginnt mit einer BOM")
    nonascii = [c for c in b if ord(c) > 126]
    need(not nonascii, "reines ASCII",
         "RevivalArtyBattery.cs enthaelt Nicht-ASCII (" + "".join(nonascii[:8]) + ")")
    need("Mortar.TextSpotted()" in b
         and "internal static string TextSpotted()" in s
         and "internal static string TextCrewAtGun()" in s,
         "zweisprachige Zeilen liegen in RevivalMortar.cs",
         "die Spielertexte der Batterie stehen nicht in der UTF-8-Datei")

    # --- 1: the crosshair is the gun.
    need("Mathf.MoveTowardsAngle(haveBear, wantBear, Traverse * dt)" in s,
         "Fadenkreuz dreht nur so schnell wie der Turm",
         "die Winkelbegrenzung des Fadenkreuzes fehlt")
    need("Elevate * MetresPerDegree * dt" in s,
         "Entfernung folgt der Rohrerhoehung",
         "die radiale Begrenzung des Fadenkreuzes fehlt")
    need("Vector3 under = _aimHave ? _aimPoint : tube;" in s,
         "Overlay und Fadenkreuz sitzen auf der Richtung, nicht auf der Maus",
         "das Overlay liest wieder die Mausposition")
    need("Fire(_aiming, _aimPoint);" in s,
         "der Klick feuert auf die Richtung des Rohres",
         "der Klick feuert wieder auf den Mauspunkt")
    need("t.Turret.localRotation" in s and "t.Barrel.localRotation" in s,
         "Turm und Rohr werden wirklich gedreht",
         "das Modell wird nicht mitgedreht")
    need("ArtyModel.Build(out turret, out barrel)" in s,
         "das Fahrzeug ersetzt das Rohr",
         "die Aufstellung baut kein Fahrzeug")

    # --- 2: an NPC shell never makes one player hurt another.
    need("static void NpcImpact(Vector3 point)" in s
         and "Sweep(point, false, out npc, out veh, out plr);" in s,
         "NPC-Einschlag trifft Spieler nicht vom Master aus",
         "der NPC-Einschlag laeuft durch den Spielerdurchlauf des Schuetzen")
    need("internal static void Self(Vector3 point)" in s
         and "Net.SendNpcImpact(point);" in s,
         "jeder Client wendet den Einschlag auf den EIGENEN Spieler an",
         "die Selbstanwendung des NPC-Einschlags fehlt")
    need("if (d.Length > 3 && d[3] > 0.5f)" in s,
         "vierter Float trennt Spieler- und NPC-Schuss",
         "der Empfaenger unterscheidet die beiden Schussarten nicht")

    # --- 3: one circle for every client.
    need('AccessTools.PropertyGetter(photon, "time")' in b,
         "Drohnenbahn haengt an der gemeinsamen Photon-Uhr",
         "die Drohnenbahn benutzt keine gemeinsame Uhr")
    need("centre.x * 0.0131f + centre.z * 0.0177f" in b,
         "Phase kommt aus der Siedlungsposition",
         "die Phase der Bahn ist nicht aus der Position abgeleitet")
    need("Drone.Modell.Bauen()" in b,
         "es fliegt eine echte Drohne, kein Symbol",
         "die Aufklaerungsdrohne hat kein Modell")

    # --- the delay and the random accuracy, both ordered explicitly.
    need("_cfgReportDelay" in b and "_cfgReportJitter" in b,
         "Meldung erreicht den Schuetzen mit Verzoegerung",
         "die Meldeverzoegerung fehlt")
    need("UnityEngine.Random.insideUnitCircle" in b and "_cfgAimError" in b,
         "jede Feuerbitte traegt einen eigenen Zielfehler",
         "der Zielfehler der Feuerbitte fehlt")
    need("Mortar.Laid(p.SettlementId, point)" in b,
         "gefeuert wird erst, wenn das Rohr steht",
         "der Schuetze feuert, bevor der Turm auf dem Punkt ist")

    # --- 4: the crew belongs to its settlement.
    # The hated list is COPIED, never shared: other parts of the toolkit
    # rewrite a settlement's list in place, and a shared reference would carry
    # that edit back into the men it was taken from.
    need("MatchFaction" in b
         and "_fHated.SetValue(opt, hated.Clone() as Array)" in b,
         "Besatzung uebernimmt die Fraktion der Siedlung (als Kopie)",
         "die Besatzung behaelt eine fremde Fraktion")
    need("Crew.DropSquad(at, gun.eulerAngles.y, 2, side, loadout)" in b,
         "zwei Mann je Geschuetz: Schuetze und Drohnenfuehrer",
         "die Besatzung wird nicht gesetzt")
    need("ArtyBattery.CrewHoldsGun" in s,
         "die Besatzung haelt das Visier, bis sie tot ist",
         "der Spieler kann das Geschuetz an der lebenden Besatzung vorbei bedienen")

    # --- 5: the map.
    need("GUI.BeginClip(clip)" in b and "MapTools.MapViewportRect" in b,
         "Kartenoverlay hart auf das Kartenfenster geschnitten",
         "das Overlay der Batterie ist nicht auf das Kartenfenster geschnitten")
    need("static void MapSnapshot(float now)" in b,
         "Kartenstand wird beim Oeffnen eingefroren",
         "die Drohnenposition auf der Karte ist nicht eingefroren")
    need("new Color(0.72f, 0.13f, 0.125f" in b,
         "Marker im Locator-Rot der Patrouillengrenze",
         "der Drohnenmarker benutzt eine fremde Farbe")

    # --- seams and the public repository.
    for seam in ("ArtyBattery.BindConfig", "ArtyBattery.Tick()", "ArtyBattery.Draw()"):
        need(seam in plug, "Seam " + seam,
             "Seam fehlt in RevivalPlugin.cs: " + seam)
    for seam in ("ArtyBattery.GunRaised", "ArtyBattery.GunLost"):
        need(seam in s, "Seam " + seam,
             "Seam fehlt in RevivalMortar.cs: " + seam)
    # sync_public.py belongs to the private repository only; in the public
    # copy there is nothing to check here.
    if os.path.exists(sync_p):
        need('"RevivalArtyBattery.cs"' in sync,
             "Datei geht ins oeffentliche Repository",
             "RevivalArtyBattery.cs fehlt in sync_public.py - dort baut das Repo nicht")


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
    check_mine()
    check_gas_launcher()
    check_convoy_ground_and_exit()
    check_convoy_column()
    check_mortar()
    check_arty_battery()
    check_version()
    print("=" * 74)
    print("Fehler: %d    Hinweise: %d" % (len(fails), len(warns)))
    for f in fails:
        print("  FEHLER  " + f)
    for w in warns:
        print("  HINWEIS " + w)
    sys.exit(1 if fails else 0)
