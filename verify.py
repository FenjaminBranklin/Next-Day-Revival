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
    "arty_hull.ndmesh", "arty_turret.ndmesh", "arty_barrel.ndmesh",
    "arty_recoil.ndmesh", "arty_diffuse.png", "arty_metal.png", "arty_normal.png",
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
# Das Artilleriefahrzeug der Siedlungen ist KEIN Fall mehr fuer diese Liste:
# die vier arty_*-Dateien sind seit dem Bohdana-Modell Pflicht und stehen in
# ASSET_FILES.
#
# Der Chemie-Granatwerfer 1491 (GasGunModel in RevivalGasLauncher.cs) baut
# seinen Einzelschussgranatwerfer, seine Palettentextur und sein Inventarsymbol
# im Code - vorher trug er das Modell der M72 LAW, und das lag in der
# Granatenhand verkehrt herum und auf dem Kopf. Jeder dieser Namen haengt
# hinter File.Exists bzw. Assets.TextureIfPresent, das Fehlen ist der Normalfall.
#
# Dasselbe gilt fuer die Technische (TechnicalModel in RevivalTechnical.cs):
# Lafette, MG und Schild sind erzeugte Geometrie, und die Karosserie ist die
# des Spender-UAZ. Liegt eine der Dateien unten doch da - technical_build.py
# baut sie aus dem Quellmodell, das es im Toolkit-Ordner findet -, gewinnt sie
# gegen das erzeugte Teil. Auch hier haengt jede hinter File.Exists bzw.
# Assets.TextureIfPresent.
OPTIONAL_ASSETS = [
    "gasgun.ndmesh", "gasgun_diffuse.png", "gasgun_icon.png",
    "technical_body.ndmesh", "technical_mg.ndmesh", "technical_mount.ndmesh",
    "technical_shield.ndmesh", "technical_diffuse.png", "technical_normal.png",
    "technical_mg_diffuse.png", "technical_mg_normal.png",
]

MESHES = ["arty_hull.ndmesh", "arty_turret.ndmesh", "arty_barrel.ndmesh", "arty_recoil.ndmesh", "mg42.ndmesh", "sniper50.ndmesh", "m7.ndmesh", "mag68box.ndmesh",
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


def check_ground_enemies():
    """[19] Editor ground enemies: random placement, crew loadouts and the one
    promise about them that no screenshot can show.

    The order was explicit on two points. A walking group WALKS - "und mit
    laufen meine ich wirklich laufen und nicht rennen" - and it stays within a
    few hundred metres of the point it was dropped at. Both rest on a handful
    of lines that a later edit could quietly undo: the two movement orders in
    RunGround carry MainWalk, and both the chosen waypoint AND every corner of
    the NavMesh path to it must lie inside the group's radius around its home.
    A single MainRun in that block turns the feature back into the sprinting
    NPCs it was written to avoid, and a missing corner check lets a man walk a
    legal-looking straight line around a lake and out of his area.

    The rest guards the shape of the feature. Ground groups travel on their own
    hash-verified /runtime/ground envelope, so an older client keeps its convoy
    data; a bad snapshot is rejected as a whole before any live group is
    touched. Only the Photon master spawns, and it ADOPTS men it finds by their
    cached spawn key instead of spawning a second copy of the same group. The
    limits have to agree between grounddef.py and the plugin, otherwise the
    editor happily publishes a snapshot the game then refuses in full.

    Movement on real terrain, the walking animation itself and the respawn
    timer stay in-game acceptance items.
    """
    import re
    print("[19] Editor-Bodengegner: Verteilen, Ausruestung, Gehen (statisch)")
    ground_p = os.path.join(ROOT, "Revival.GroundEnemies.cs")
    npc_p = os.path.join(ROOT, "Revival.NpcCombat.cs")
    live_p = os.path.join(ROOT, "Revival.LiveRoutes.cs")
    crew_p = os.path.join(ROOT, "Revival.Crew.cs")
    plug_p = os.path.join(ROOT, "RevivalPlugin.cs")
    sync_p = os.path.join(ROOT, "sync_public.py")
    if not os.path.exists(ground_p):
        bad("Revival.GroundEnemies.cs fehlt")
        return
    raw = io.open(ground_p, "rb").read()
    g = raw.decode("utf-8", "replace")
    npc = io.open(npc_p, encoding="utf-8").read() if os.path.exists(npc_p) else ""
    live = io.open(live_p, encoding="utf-8").read() if os.path.exists(live_p) else ""
    crew = io.open(crew_p, encoding="utf-8").read() if os.path.exists(crew_p) else ""
    plug = io.open(plug_p, encoding="utf-8").read() if os.path.exists(plug_p) else ""
    sync = io.open(sync_p, encoding="utf-8").read() if os.path.exists(sync_p) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Ground: " + why)

    # --- file rule: machine-read source, build.ps1 wants it BOM-less and ASCII.
    need(not raw.startswith(b"\xef\xbb\xbf"), "keine BOM",
         "Revival.GroundEnemies.cs beginnt mit einer BOM")
    need(not [c for c in g if ord(c) > 126], "reines ASCII",
         "Revival.GroundEnemies.cs enthaelt Zeichen ausserhalb ASCII")

    # --- WALKING, not running. The whole point of the two behaviours.
    start = npc.find("static void RunGround(Squad s, float now)")
    stop = npc.find("static Vector3 Anchor(", start + 1) if start >= 0 else -1
    block = npc[start:stop] if start >= 0 and stop > start else ""
    need(block != "", "RunGround und GroundDestination liegen beieinander",
         "RunGround fehlt in Revival.NpcCombat.cs - Bodengruppen haben kein "
         "eigenes Verhalten mehr")
    need("MainRun" not in block and "Stance.Bound" not in block,
         "keine einzige Laufanweisung im Bodenverhalten",
         "im Bodenverhalten steht wieder MainRun oder ein Sprung - die Gruppen "
         "rennen dann, statt zu gehen")
    need(block.count("MainWalk") >= 2,
         "beide Bewegungsbefehle der Gehgruppe gehen im Schritt",
         "ein Bewegungsbefehl der Gehgruppe traegt kein MainWalk mehr")
    need("Go(f, dest, MainWalk, PoseStand, now, Stance.Advance);" in block,
         "ein neues Ziel wird im Schritt angegangen",
         "der Marschbefehl der Gehgruppe ist kein Schritt mehr")
    need("Drive(f, MainWalk, AddNone, PoseStand, now, false);" in block,
         "der Alarmzustand schaltet den Schritt nicht auf Lauf um",
         "ohne die Schrittstuetze schaltet der native Alarm die Gruppe auf Lauf")
    need("if (!s.GroundWalking) { Hold(f, null, now); continue; }" in block,
         "wartende Gruppen bleiben stehen",
         "wartende Bodengruppen bleiben nicht mehr auf ihrem Posten")

    # --- and inside the radius around the drop point, path included.
    need("Flat(dest - s.Lz) > s.GroundRadius" in block,
         "das gewaehlte Ziel liegt im Umkreis um den Aussetzpunkt",
         "das Wanderziel wird nicht mehr gegen den Umkreis geprueft")
    need("if (Flat(corners[c] - s.Lz) > s.GroundRadius) { inside = false; break; }" in block,
         "auch der Weg dorthin bleibt im Umkreis",
         "nur das Ziel liegt im Umkreis - der Weg dorthin darf ihn verlassen")
    need("NavMeshPathStatus.PathComplete" in block,
         "nur vollstaendig begehbare Wege werden befohlen",
         "die Gehgruppe bekommt auch unvollstaendige Wege befohlen")
    need("Vector3 target = f.Squad != null && f.Squad.GroundGroup ? dest : Ground(dest);" in npc,
         "die geprueften Bodenziele werden nicht noch einmal verschoben",
         "eine zweite Projektion kann das gepruefte Ziel aus dem Umkreis tragen")

    # --- the published channel: own envelope, hashed, all-or-nothing.
    need('envelope[0] != "NDR-GROUND-1"' in live and "Hash(tsv) != envelope[1]" in live,
         "eigener, hashgepruefter Umschlag fuer die Bodengruppen",
         "der Bodenkanal hat keinen eigenen geprueften Umschlag mehr")
    need("RevivalGroundEnemies.Parse(lines);" in live,
         "ein fehlerhafter Stand wird vor jeder Uebernahme abgelehnt",
         "ein fehlerhafter Bodenstand wird erst beim Setzen bemerkt")
    need("keeping the last verified ground groups" in live,
         "bei Ausfall bleibt der letzte gepruefte Stand stehen",
         "ein Ausfall des Servers loescht die Bodengruppen")

    # --- master only, and adoption instead of a second copy.
    need("bool master = room != null && (bool)_masterGetter.Invoke(null, null);" in g
         and "if (!master) { _wasMaster = false; return; }" in g,
         "nur der Photon-Master setzt Bodengegner",
         "auch ein Nicht-Master setzt Bodengegner - jeder Client spawnt dann "
         "seine eigene Kopie")
    need("_masterReady = Time.time + 5f;" in g,
         "ein neuer Master wartet auf die uebertragenen Spawns",
         "ein neuer Master raeumt auf, bevor die Spawns angekommen sind")
    need("string key = Crew.GroundKey(ai);" in g and "men.ToArray()" in g,
         "vorhandene Maenner werden uebernommen statt neu gesetzt",
         "der Master uebernimmt keine vorhandenen Bodengegner mehr - er setzt "
         "eine zweite Gruppe daneben")
    need('extended[10] = "ndr-ground-1:" + _groundKey;' in crew
         and "new object[_groundKey == null ? 10 : 11]" in crew,
         "der Gruppenschluessel reist im zwischengespeicherten Spawn mit",
         "ohne Schluessel im Spawn kann ein neuer Master nichts wiedererkennen")
    need("Vector3[] wo = _groundPositions ?? Ausstiege(car, vgs, count);" in crew,
         "die Bodengruppe setzt auf ihren eigenen geprueften Punkten auf",
         "die Bodengruppe benutzt wieder die Ausstiegspunkte eines Fahrzeugs")
    need("RevivalTroopInsertion.TerrainHeight(point, out y)" in g
         and "NavMesh.SamplePosition(point, out hit, search, NavMesh.AllAreas)" in g,
         "jeder Aussetzpunkt wird auf begehbaren Boden gezogen",
         "ein zufaelliger Punkt wird ohne Boden- und NavMesh-Pruefung benutzt")

    # --- bounded input, and limits that agree on both sides of the wire.
    need("if (lines == null || lines.Length > 1025)" in g,
         "die Zeilenzahl des Bodenstands ist begrenzt",
         "ein Bodenstand darf beliebig viele Zeilen haben")
    need('if (g.Behavior != "waiting" && g.Behavior != "walking")' in g,
         "genau zwei Verhalten: warten oder gehen",
         "das Verhalten einer Bodengruppe wird nicht mehr geprueft")
    need("if (total > MaxTotal) throw new IOException" in g,
         "die Gesamtstaerke ist im Plugin begrenzt",
         "das Plugin nimmt beliebig viele Bodengegner an")
    need("RevivalGroundEnemies.Tick();" in plug,
         "Seam RevivalGroundEnemies.Tick in RevivalPlugin.cs",
         "Seam fehlt in RevivalPlugin.cs: RevivalGroundEnemies.Tick")
    need('"Revival.GroundEnemies.cs"' in sync,
         "Revival.GroundEnemies.cs geht ins oeffentliche Repository",
         "Revival.GroundEnemies.cs fehlt in sync_public.py - das oeffentliche "
         "Repository laesst sich dann nicht uebersetzen")

    cs = re.search(r"MaxGroups = (\d+), MaxGroupSize = (\d+), MaxTotal = (\d+)", g)
    cs_radius = re.search(r"g\.Radius = Number\(c\[7\], (\d+)f, (\d+)f\)", g)
    cs_columns = re.search(r"c\.Length != (\d+)", g)
    need(cs is not None and cs_radius is not None and cs_columns is not None,
         "die Grenzen des Plugins sind ablesbar",
         "die Grenzen in Revival.GroundEnemies.cs haben ihre Form verloren")

    # --- grounddef.py and the editor are private; the public copy stops here.
    gdef_p = os.path.join(ROOT, "grounddef.py")
    if cs is None or not os.path.exists(gdef_p):
        return
    gdef = io.open(gdef_p, encoding="utf-8").read()

    def value(name):
        m = re.search(r"^%s = (\d+)$" % name, gdef, re.M)
        return m.group(1) if m else None

    need([value("MAX_GROUPS"), value("MAX_GROUP_SIZE"), value("MAX_TOTAL")]
         == list(cs.groups()),
         "Gruppen-, Gruppengroessen- und Gesamtgrenze stimmen mit dem Plugin ueberein",
         "grounddef.py und das Plugin nennen verschiedene Grenzen - der Editor "
         "veroeffentlicht dann einen Stand, den das Spiel ganz ablehnt")
    need(cs_radius is not None
         and [value("MIN_RADIUS"), value("MAX_RADIUS")] == list(cs_radius.groups()),
         "der erlaubte Umkreis stimmt mit dem Plugin ueberein",
         "Editor und Plugin erlauben verschiedene Umkreise")
    columns = re.search(r"TSV_COLUMNS = \[(.*?)\]", gdef, re.S)
    need(columns is not None and cs_columns is not None
         and len(re.findall(r'"[A-Za-z]+"', columns.group(1))) == int(cs_columns.group(1)),
         "Editor und Plugin zaehlen dieselben Spalten",
         "die Spaltenzahl von grounddef.py passt nicht zum Parser des Plugins")
    need('BEHAVIORS = ["waiting", "walking"]' in gdef,
         "der Editor bietet genau die beiden Verhalten an",
         "der Editor bietet ein Verhalten an, das das Plugin nicht kennt")

    comp_p = os.path.join(ROOT, "compdef.py")
    comp = io.open(comp_p, encoding="utf-8").read() if os.path.exists(comp_p) else ""
    need("grounddef.validate_groups(" in comp and "grounddef.migrate_groups(" in comp,
         "das Speichern des Editors prueft die Bodengruppen mit",
         "der Editor speichert Bodengruppen ungeprueft")

    route_p = os.path.join(ROOT, "routeeditor.py")
    route = io.open(route_p, encoding="utf-8").read() if os.path.exists(route_p) else ""
    need('path == "/runtime/ground"' in route and 'path == "/ground.js"' in route,
         "der Editorserver liefert Bodenstand und Bodenoberflaeche aus",
         "der Editorserver kennt den Bodenkanal nicht")

    editor_dir = os.path.join(ROOT, "editor")
    if os.path.isdir(editor_dir):
        def editor_file(name):
            path = os.path.join(editor_dir, name)
            return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

        gjs = editor_file("ground.js")
        app = editor_file("app.js")
        html = editor_file("index.html")
        need("'Distribute random groups'" in gjs and "function scatter()" in gjs,
             "der Editor verteilt Gruppen zufaellig auf der Karte",
             "die Zufallsverteilung fehlt in editor/ground.js")
        need("openPicker('weapon'" in gjs and "SLOTS.forEach" in gjs,
             "Bodengruppen tragen dieselbe Ausruestungsauswahl wie die Besatzungen",
             "die Ausruestungsauswahl der Bodengruppen fehlt")
        need('<script src="ground.js"></script>' in html
             and "NDRGround.init();" in app and "NDRGround.draw();" in app
             and "NDRGround.mousedown(ev)" in app,
             "die Bodenoberflaeche haengt in Seite, Zeichnung und Maus",
             "editor/ground.js ist nicht vollstaendig eingehaengt")

    # --- die beiden Regressionen zu diesem Feature muessen im Repository
    # liegen. verify.py fuehrt sie nicht aus (es startet keine Unterprozesse).
    if os.path.isdir(os.path.join(ROOT, "research")):
        for check in ("ground_enemy_check.py", "ground_editor_check.js"):
            need(os.path.exists(os.path.join(ROOT, "research", check)),
                 "research/" + check + " liegt vor",
                 "research/" + check + " fehlt - die Bodengegner sind unbelegt")


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


def _body(src, signature):
    """The braces-balanced body of the method whose signature line is given, or
    an empty string when that method is not there any more. Rules that belong to
    ONE method have to be read inside it: `u.Stuck = 0f` appears half a dozen
    times in Revival.Patrol.cs, and only one of them is the guard."""
    start = src.find(signature)
    if start < 0:
        return ""
    brace = src.find("{", start)
    if brace < 0:
        return ""
    depth = 0
    for i in range(brace, len(src)):
        if src[i] == "{":
            depth += 1
        elif src[i] == "}":
            depth -= 1
            if depth == 0:
                return src[brace:i + 1]
    return ""


def check_patrol_fall():
    """[18] The patrol ground guard (static).

    The reported defect: vehicles and patrols that spawn somewhere, fall
    through the map and reappear above it, on repeat. Three mistakes in
    Revival.Patrol.cs made that loop, and these guards keep each of them from
    coming back:

      1. A placement that took the first collider under an origin 30 m up - a
         tree crown, a shed roof, another vehicle - and that placed the hull on
         the AUTHORED point when it hit nothing at all. Far from every player
         the colliders are not loaded, and an automatic patrol starts at the
         waypoint farthest from everybody.
      2. A stuck timer that reads a falling hull as "not moving", because the
         driver measures speed in the ground plane only.
      3. A stuck recovery that warps that hull onto a waypoint without asking
         whether there is anything under it - which is the "reappears above the
         map" half of the loop.

    The movement itself still needs an in-game acceptance pass; these are
    source rules, not physics.
    """
    print("[18] Patrol ground guard (static)")
    patrol_p = os.path.join(ROOT, "Revival.Patrol.cs")
    if not os.path.exists(patrol_p):
        bad("Patrol ground guard: source file missing")
        return
    code = _code(io.open(patrol_p, encoding="utf-8").read())

    # 1. Every placement goes through the checked lookup AND reports a miss.
    spot = _body(code, "static bool GroundSpot(")
    if ("RoadUnder(point, own, out y, out normal)" in spot
            and "return false;" in spot):
        ok("placement uses the checked ground lookup and reports a miss")
    else:
        bad("Patrol ground guard: GroundSpot no longer reports a missing surface")

    if "static Vector3 Grounded(" in code:
        bad("Patrol ground guard: the unfiltered 30 m placement ray is back")
    else:
        ok("the unfiltered first-hit placement ray is gone")

    # 2. A spawn refuses a place with nothing under it. The automatic already
    #    backs off when a spawn produces no vehicle, so the refusal is a wait.
    spawn = _body(code, "static void Spawn(Route src, bool auto)")
    if ("GroundedWaypoint(r, start, 1.6f, true, out startPos)" in spawn
            and "if (firm < 0)" in spawn):
        ok("a patrol is not put down where there is nothing to stand on")
    else:
        bad("Patrol ground guard: the spawn no longer checks the ground")

    # 3. The stuck recovery only warps onto a waypoint that HAS ground.
    free = _body(code, "static void Free(Unit u, Vector3 pos)")
    if ("GroundedWaypoint(r, to, 1.5f, !u.OneWay, out target)" in free
            and "if (landed < 0)" in free):
        ok("the stuck recovery never warps a hull into nothing")
    else:
        bad("Patrol ground guard: FREE can warp onto a waypoint with no ground")

    # 4. Falling is not being stuck, and it is decided before the driver runs.
    guard = _body(code, "static bool GroundGuard(")
    if "if (GroundGuard(u)) continue;" in code and "u.Stuck = 0f;" in guard:
        ok("a falling vehicle is taken off the stuck timer before the driver runs")
    else:
        bad("Patrol ground guard: a falling hull can still feed the stuck timer")

    # 5. Repeated failures end the vehicle instead of dropping it in again.
    recover = _body(code, "static void Recover(Unit u)")
    if "u.Recoveries > FallRecoveries" in recover and "Drop(u," in recover:
        ok("a vehicle that keeps falling is given up, not put back forever")
    else:
        bad("Patrol ground guard: a fallen vehicle can be recovered without limit")


def check_gas_launcher():
    """[14] Chemical launcher RG-Kh and its 30-minute gas cloud (static).

    The four mistakes this keeps from coming back are the ones that would make
    the weapon look broken instead of failing loudly: an id outside the grenade
    band (then no slot accepts the tube at all - the 2065 lesson), a missing
    grenade record (then the equipped tube has no data and is dropped), a shot
    that does not consume its one-shot tube, and a cloud that keeps poisoning
    after its time is up. Everything that needs eyes - the cloud in the sky,
    the mask actually saving a player - stays an in-game acceptance item.

    Since the weapon got its own model there is a fifth: falling back to
    another weapon's art. And since that model sat in the hand at an angle
    nobody chose, there is a sixth, which is the interesting one. The pose of a
    weapon comes from the transform data of the item it was CLONED from, and
    this one has to clone the frag grenade 1403 to reach the grenade band at
    all. Both halves of the fix are checked here: the mesh has to be built in
    the game's own weapon frame (fist y 0.624, bore axis z 0.096, measured from
    the RPD), and the prefab has to take a REAL WEAPON's pose through
    ItemDef.HandPoseFrom instead of the grenade's. The guessed 180 degree flip
    that used to stand in for both is checked to be GONE.
    """
    print("[14] Chemie-Granatwerfer und Giftgaswolke (statisch)")
    gas_p = os.path.join(ROOT, "RevivalGasLauncher.cs")
    plug_p = os.path.join(ROOT, "RevivalPlugin.cs")
    items_p = os.path.join(ROOT, "Revival.Items.cs")
    if not os.path.exists(gas_p):
        bad("RevivalGasLauncher.cs fehlt")
        return
    s = io.open(gas_p, encoding="utf-8").read()
    plug = io.open(plug_p, encoding="utf-8").read() if os.path.exists(plug_p) else ""
    items = io.open(items_p, encoding="utf-8").read() if os.path.exists(items_p) else ""

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
    # Das Modell. Frueher war es das der M72 LAW - die falsche Waffe, und es
    # lag in der Hand schief, weil dieses Item die Transformdaten der
    # Splittergranate 1403 aufgeschrieben bekommt (siehe die Haltungspruefungen
    # unten: da liegt der eigentliche Fehler, nicht im Mesh).
    need("internal static class GasGunModel" in s
         and "GasGunModel.MESH_FILE, GasGunModel.DIFFUSE_FILE" in s
         and "GasGunModel.ICON_FILE" in s,
         "eigenes Werfermodell statt der LAW-Kunst",
         "das Item benennt nicht das eigene Modell - traegt es wieder die LAW?")
    need("GasGunModel.Provide();" in s and "GasGunModel.TickIcon();" in s,
         "Modell vor dem ItemDef bereitgestellt, Symbol im ersten Tick gerendert",
         "Naht zum Modellbau fehlt (Provide/TickIcon)")
    # DIE LAGE IN DER HAND, erste Haelfte: das Mesh steht im Waffenrahmen des
    # Spiels. Faust y 0.624, Laufachse z 0.096 - am RPD gemessen (RE 5), und
    # check [5] haelt jedes gelieferte Waffenmesh daran fest.
    need("const float HAND_Y = 0.624f;" in s
         and "const float HAND_Z = 0.096f;" in s
         and "static Vector3 ToWeapon(Vector3 p)" in s
         and "v[i] = ToWeapon(v[i]);" in s,
         "Modell im Waffenrahmen des Spiels (Faust y 0.624, Lauf z 0.096)",
         "das Modell wird nicht in den Waffenrahmen geschoben - dann greift "
         "die Hand daneben")
    # Zweite Haelfte, und die wichtigere: die Prefabwurzel bekommt die Haltung
    # einer echten Waffe statt die der Splittergranate, von der das Item nur
    # seine Kategorie erbt.
    need("def.HandPoseFrom = " in s
         and 'cfg.Bind("GasLauncher", "HandPoseFrom", 1023' in s,
         "Handhaltung einer echten Waffe (HandPoseFrom, Vorgabe RPD 1023)",
         "das Item uebernimmt weiter die Haltung der Splittergranate")
    need("public int HandPoseFrom;" in plug,
         "ItemDef.HandPoseFrom vorhanden",
         "ItemDef kennt kein HandPoseFrom")
    need("_def.HandPoseFrom != 0 && HandPose(ref pos, ref euler)" in items
         and 'WriteVec3(c, t, "localPosition", pos, names[i]);' in items
         and 'WriteVec3(c, t, "localEulerAngles", euler, names[i]);' in items
         and "HAND_POSE_EULER" in items,
         "ItemFactory schreibt Lage und Drehung der Referenzwaffe",
         "ItemFactory setzt nur die Skalierung - die Haltung bleibt die der "
         "Spende")
    # Die Feinkorrektur bleibt, aber sie dreht um die FAUST und nicht mehr um
    # den Ursprung, und sie ist leer als Vorgabe: geraten wird hier nichts mehr.
    need('cfg.Bind("GasLauncher", "GripEuler", "0,0,0"' in s
         and 'cfg.Bind("GasLauncher", "GripOffset", "0,0,0"' in s
         and "static void Correct(Mesh m)" in s
         and "q * (vs[i] - pivot) + pivot + offset" in s,
         "Feinkorrektur um den Griff einstellbar (GripEuler/GripOffset)",
         "keine einstellbare Korrektur der Lage in der Hand, oder sie dreht "
         "nicht um den Griff")
    need("ModelEuler" not in s,
         "der geratene 180-Grad-Kipper ist raus",
         "ModelEuler steht wieder im Quelltext - das war die Vermutung, die "
         "nicht gestimmt hat")
    # Und ein echtes Modell muss die erzeugte Geometrie ersetzen koennen.
    need("Assets.Provide(MESH_FILE, _mesh)" in s and "Present(MESH_FILE)" in s,
         "ein geliefertes gasgun.ndmesh schlaegt die erzeugte Geometrie",
         "ein echtes Modell koennte die erzeugte Geometrie nicht ersetzen")
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

    Since 6.26 it also guards WHERE the gun is put: the emplacement search
    scores every candidate patch and takes the best one. Drop the scoring and
    it is again the first patch that merely passes, starting at the settlement
    centre - which is where the houses are.
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

    # --- WER EINE BATTERIE BEKOMMT. NPC_Settlement ist im Spiel jede NPC-
    # Gruppe: auf level7 sind es 39, davon vier echte Siedlungen. Ohne diese
    # Pruefung bekam jedes Questlager und jede Zufallsgruppe eine Haubitze
    # samt Besatzung und 240-m-Drohnenkreis auf der Karte (Feldbericht
    # 2026-09-17). research/arty_regression_check.py spielt die vollstaendige
    # Zaehlung von level7 gegen die echten Methoden durch.
    need("string no = NotASettlement(s);" in s,
         "jede Siedlung wird vor dem Geschuetz geprueft",
         "Place() prueft nicht mehr, ob der Ort ueberhaupt eine Siedlung ist")
    need('Flag(s, "IsIndoors")' in s and "SafeSettlement(s)" in s,
         "Haendlerlager und Innenraeume bekommen kein Geschuetz",
         "IsSafeSettlement oder IsIndoors wird nicht mehr geprueft")
    need('"[Neutral"' in s,
         "die neutrale Basis bleibt ohne Geschuetz",
         "die Ausnahme fuer die neutrale Basis fehlt")
    need("SettlementType(s, out type)" in s,
         "Quest- und Zufallslager bleiben ohne Geschuetz",
         "NPC_SettlementType wird nicht mehr gelesen")
    need('RevivalPlugin.TypeByName("NPC_SpawnPoint")' in s
         and "GetComponentsInChildren(_spawnPointType, true)" in s,
         "die Groesse eines Ortes wird an seinen Spawnpunkten gemessen",
         "die Spawnpunkte werden nicht mehr gezaehlt")
    need("men >= 0 && men < want" in s,
         "eine unlesbare Groesse entwaffnet nicht jede Siedlung",
         "eine fehlende NPC_SpawnPoint-Klasse wuerde jede Siedlung ablehnen")
    need('cfg.Bind("Mortar", "SkipSafeSettlements"' not in s,
         "SkipSafeSettlements ist aus dem Code genommen",
         "SkipSafeSettlements wird noch gebunden, entscheidet aber nichts mehr")
    need("if (Master())" in s and "{ dmg, 14 }" in s,
         "Fahrzeugschaden nur auf dem Master, ueber Teil 14",
         "Fahrzeugschaden nicht auf den Master begrenzt")

    # --- WO DAS GESCHUETZ STEHT. Bis 6.25 nahm FreeGround den ERSTEN Fleck,
    # der die Pruefung bestand, und begann in der Siedlungsmitte - dort stehen
    # die Haeuser. Der Feldbericht 2026-09-18 ("die Batterie in Locator muss
    # hinter die Satellitenschuessel, ausserdem brauchen wir bessere
    # Mechanismen, damit die Batterien immer auf freiem Grund mit moeglichst
    # viel Platz drum herum spawnen") ist genau diese Zeile. Jetzt wird jeder
    # Kandidat bewertet und der BESTE genommen; ohne die Bewertung ist die
    # Suche wieder die alte, und nichts sonst wuerde das auffallen lassen.
    need("float score = Score(point, centre, village);" in s
         and "if (have && score <= best) continue;" in s,
         "die Aufstellung nimmt den besten Platz, nicht den ersten",
         "FreeGround bewertet die Kandidaten nicht mehr - es gewinnt wieder "
         "der erste, also die Siedlungsmitte")
    need("List<Vector3> village = SpawnPointsOf(settlement);" in s,
         "die Spawnpunkte der Siedlung zaehlen als bewohnter Grund",
         "die Bewertung kennt die Spawnpunkte der Siedlung nicht mehr")
    need((_bind_number(s, "Mortar", "PlaceSearchRadius") or 0) >= 48,
         "der Suchradius umfasst mehr als die Siedlungsmitte",
         "PlaceSearchRadius ist zu klein - die Suche sieht nur die Mitte")

    # --- RevivalPlugin seams.
    for seam in ("Mortar.BindConfig", "Mortar.AddItems(Items)",
                 "Mortar.Tick()", "Mortar.Draw()"):
        need(seam in plug, "Seam " + seam,
             "Seam fehlt in RevivalPlugin.cs: " + seam)


def check_arty_battery():
    """[16] The settlement artillery vehicle, its crew and the recon drone.

    The rules below decide whether this feature is what was ordered rather than merely
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
      7. The two men must STAY at the vehicle. Crew.DropSquad gives every squad
         a ring of walk points and the vanilla idle logic walks it; the hold is
         a pause refreshed twice a second plus a station warp, and without it
         the gunner is back to wandering nine metres from his gun.
      8. And they must stand ON THE GROUND while they do it (field report
         2026-09-18). The stations are built in a level frame, because the hull
         itself is stood on the ground normal, and their ground ray walks past
         the gun's own hierarchy, because the barrel sweeps over both of them.
      9. The drone must be in the air before the player has been there (order of
         2026-09-18). The gun waits for loaded terrain; the orbit needs only the
         settlement centre, so a settlement that is going to get a battery flies
         its drone from the moment the level is loaded - and gives it up again
         with the scene.
     10. The crew stands at its station FROM THE FIRST FRAME, not after a lap in
         the air (second field report 2026-09-18). Both stations are handed to
         Crew, so no ring is drawn around a single point with half of it on the
         vehicle, and Crew's own spawn ray walks past the carrier.
     11. And it never climbs again (third field report 2026-09-19). No station
         may be measured above the ground the vehicle itself stands on, a man
         counts as a man whichever of his colliders is hit, and a height guard
         runs every frame between two posting passes.
     12. The two men work the side of the hull the target computer is on, close
         enough to touch it, facing it - the gunner upright at the box, the
         operator crouching beside him (order 2026-09-19). Both places, both
         clips and the crouch pose are configuration, because where exactly the
         box sits on the model cannot be read from here.
    """
    print("[16] Artilleriefahrzeug, Besatzung und Aufklaerungsdrohne (statisch)")
    bat_p = os.path.join(ROOT, "RevivalArtyBattery.cs")
    mortar_p = os.path.join(ROOT, "RevivalMortar.cs")
    plug_p = os.path.join(ROOT, "RevivalPlugin.cs")
    sync_p = os.path.join(ROOT, "sync_public.py")
    crew_p = os.path.join(ROOT, "Revival.Crew.cs")
    if not os.path.exists(bat_p):
        bad("RevivalArtyBattery.cs fehlt")
        return
    raw = io.open(bat_p, "rb").read()
    b = raw.decode("utf-8", "replace")
    crew = io.open(crew_p, encoding="utf-8").read() if os.path.exists(crew_p) else ""
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
    need('s.gameObject.name.StartsWith("NDR_", StringComparison.Ordinal)' in s,
         "runtime crew settlements cannot generate artillery",
         "runtime crew exclusion missing: recursive artillery spawns")
    need('_tubes[i].SettlementId == id && _tubes[i].Go != null' in s,
         "gun allocation is idempotent per settlement",
         "duplicate live-gun guard missing")
    need('sharedMesh = mesh' in b and 'sharedMaterial = _material' in b,
         "settlements share the imported meshes and material",
         "artillery duplicates model resources per settlement")
    need('Bohdana assets missing; repair the client package' in b
         and 'static Mesh Hull()' not in b,
         "missing Bohdana assets cannot become generated placeholder art",
         "artillery still allows the generated vehicle fallback")
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

    # --- 3b: DIE BAHN MUSS RUND LAUFEN. PhotonNetwork.time ist eine ganze
    # Millisekundenzahl, offline Environment.TickCount mit rund 15,6 ms
    # Aufloesung; direkt in die Position gerechnet ergibt das ein Stottern
    # (Feldbericht 2026-09-17). research/arty_drone_orbit_check.py misst beide
    # Fassungen gegeneinander.
    need("AdvanceFlightClock();" in b and "_flightClock * speed / r" in b,
         "die Drohne fliegt auf der geglaetteten Uhr, nicht auf der rohen",
         "die Bahn haengt wieder direkt an der rohen Netzuhr - das ruckelt")
    need("drift > 1f || drift < -1f" in b,
         "ein Sprung der Netzuhr wird in einem Schritt genommen",
         "ein Uhrensprung wuerde minutenlang nachgezogen")
    need("Mathf.Lerp(p.Ground, p.GroundWant" in b,
         "die Bodenhoehe wird nachgefuehrt, nicht gestuft",
         "die Drohne springt wieder auf jede neue Bodenmessung")
    need("range * 1.1f" in b,
         "das Modell hat eine Hysterese an der Sichtgrenze",
         "am Sichtrand wird das Drohnenmodell im Wechsel gebaut und geloescht")
    need("RevivalTroopInsertion.TerrainHeight(flat, out y)" in b,
         "die Drohne fliegt ueber das Gelaende, nicht ueber Daecher",
         "die Flughoehe kommt wieder aus einem Strahl ohne Layer-Maske")

    # --- 3c: die Karte darf keine Kreise einer Ebene behalten, die weg ist.
    need("if (_marks.Count > 0) _marks.Clear();" in b and "_mapOpen = false;" in b,
         "ohne Batterien wird die Kartenaufnahme geleert",
         "eine leere Postenliste laesst die alten Drohnenkreise stehen")
    need("if (p.CrewSettlement != null) Crew.Forget(p.CrewSettlement);" in b,
         "eine abgeraeumte Besatzung wird bei Crew abgemeldet",
         "Crew._settlements behaelt einen Eintrag fuer ein zerstoertes Objekt")

    # --- die beiden Regressionen zu diesem Feature muessen im Repository
    # liegen. verify.py fuehrt sie nicht aus (es startet keine Unterprozesse),
    # aber ein stilles Verschwinden faellt hier auf.
    # research/ gehoert nur ins private Repository; in der oeffentlichen
    # Kopie gibt es hier nichts zu pruefen.
    if os.path.isdir(os.path.join(ROOT, "research")):
        for check in ("arty_regression_check.py", "arty_drone_orbit_check.py", "arty_combat_check.py"):
            need(os.path.exists(os.path.join(ROOT, "research", check)),
                 "research/" + check + " liegt vor",
                 "research/" + check + " fehlt - die Batterieregression ist unbelegt")

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

    # --- DIE BESATZUNG STEHT AM GESCHUETZ UND BEWEGT SICH NICHT (Auftrag
    # 2026-09-18). Crew.DropSquad legt jeder Gruppe einen Ring aus acht
    # Laufpunkten an, und die Spielroutine IdleStateAction laeuft ihn ab; ohne
    # den Halt wandern die beiden Maenner wieder vom Fahrzeug weg. Der Halt
    # selbst ist die Pause, die IdleStateAction frueh zurueckkehren laesst
    # (CONFIRMED IL), und die Station wird mit NavMeshAgent.Warp gesetzt, weil
    # ein blosses Versetzen den Mann am alten Pfad zurueckziehen wuerde.
    need("static void Posted(Post p, float now, bool master)" in b
         and "_mPauseTime.Invoke(ai, new object[] { 1.4f });" in b,
         "die Besatzung wird an ihrer Station festgehalten",
         "der Halt der Besatzung fehlt - die beiden Maenner laufen wieder ihren "
         "Ring ab")
    need("agent.Warp(at);" in b,
         "der Mann wird auf die Station gewarpt, nicht versetzt",
         "ohne Warp zieht der NavMeshAgent den Mann an seinen Pfad zurueck")
    need("static readonly Vector3 GunnerPost" in b
         and "static readonly Vector3 OperatorPost" in b,
         "Schuetze und Drohnenfuehrer haben feste Plaetze am Fahrzeug",
         "die beiden Stationen am Fahrzeug fehlen")
    # DIE RICHTIGE SEITE, UND DICHT DRAN (Feldmeldung 2026-09-19: "dort ist
    # bereits der zusehende target computer angebracht, da soll der gunner bis
    # ganz kurz vorm fahrzeug stehen, also richtig dran"). Beide Maenner stehen
    # auf der +X-Seite - der Seite mit dem Zielrechner - und 8 statt 10,5
    # Einheiten aussen; breiter als 7,3 wird das Modell nie. Wer die Seite
    # wechselt, muss auch die Blickrichtung mitnehmen: eine feste
    # Vierteldrehung liesse beide mit dem Ruecken zum Fahrzeug arbeiten.
    need("static readonly Vector3 GunnerPost = new Vector3(8f, 0f, -8f);" in b
         and "static readonly Vector3 OperatorPost = new Vector3(8f, 0f, -12f);" in b,
         "beide Stationen liegen auf der Seite des Zielrechners, dicht am Rumpf",
         "die Stationen liegen wieder auf der falschen Seite oder weit weg vom "
         "Fahrzeug")
    need("static float StationYaw(Transform gun, Vector3 post)" in b
         and "post.x >= 0f ? -90f : 90f" in b,
         "die Blickrichtung folgt der Seite, auf der der Mann steht",
         "die feste Vierteldrehung dreht einen Mann auf der anderen Seite vom "
         "Fahrzeug weg")
    need('cfg.Bind("Artillery", "CrewGunnerPost"' in b
         and 'cfg.Bind("Artillery", "CrewOperatorPost"' in b
         and "static Vector3 PostOf(bool gunner)" in b,
         "beide Plaetze sind Konfiguration - der Kasten laesst sich ohne "
         "neuen Build treffen",
         "die Stationen stehen nur im Quelltext - ein falsch sitzender Mann "
         "braucht dann einen neuen Build")
    # DER DROHNENFUEHRER HOCKT (Auftrag 2026-09-19: "der drone operator soll
    # daneben dauerhaft hocken"). NPCPoseState.Crouch ist die Hocke des Spiels,
    # und GetAnimationNameCrouchPose bildet sie auf crouch_idle ab (CONFIRMED
    # IL) - das ist die Dauerhocke, ohne dass ein Clip geraten werden muss.
    need("const int PoseCrouch = 1;" in b and "static int OperatorPose()" in b
         and 'cfg.Bind("Artillery", "CrewOperatorPose"' in b,
         "der Drohnenfuehrer wird in die Hocke des Spiels gesetzt",
         "der Drohnenfuehrer steht wieder - die Hocke fehlt")
    need("Arg(_mStateSync, 3, pose)" in b,
         "die Haltung geht mit dem Zustand ueber die RPC des Spiels raus",
         "die Haltung wird nicht mitgesendet - auf anderen Clients steht der "
         "Mann")
    # ... und der Schuetze arbeitet auf Brusthoehe, nicht am Boden ("sie muss
    # zu der position des target computers passen, also nicht das er da unten
    # irgendwo rumfummelt"). Die Beerenpflueck-Animation aus 6.26 ist ein Mann
    # auf den Knien; sie ist jetzt Sache des hockenden Drohnenfuehrers.
    need("static readonly string[] NotUpright = {" in b
         and "if (upright)" in b and "NotUpright[i]" in b
         and '"berr"' in b.split("static readonly string[] NotUpright = {")[1][:400],
         "dem Schuetzen sind die Bodenclips verboten",
         "der Schuetze kann wieder eine Animation am Boden bekommen - die "
         "Beerenpflueckerei war genau die Klage")
    need('cfg.Bind("Artillery", "CrewOperatorClip"' in b
         and "sealed class ClipPick" in b,
         "beide Maenner haben je einen eigenen Clip und einen eigenen Schluessel",
         "ein einziger Clip muss fuer einen stehenden und einen hockenden Mann "
         "zugleich passen")
    # Die Arbeitsanimation ist eine Kette von Rueckfallebenen, und jede einzelne
    # endet mit einem Mann, der sich nicht bewegt: Arbeitszustand, eigener Clip
    # aus dem Satz des Modells, sonst der Stand-Idle. Faellt eine davon weg,
    # steht am Ende ein laufender oder ein zuckender Mann.
    need("static int WorkState()" in b and "hold.Held = true;" in b
         and "hold.Broken = true;" in b,
         "ein Zustand, der nicht haelt, faellt auf den Stand-Idle zurueck",
         "ohne Rueckfallebene wird ein nicht gehaltener Zustand zum RPC-Sturm")
    need("now - since >= SettleSeconds" in b,
         "der Mann steht erst, dann arbeitet er",
         "ohne das Stehen davor bleibt der Ganzkoerper-Clip ein Laufclip")
    # DIE BESATZUNG STEHT AUF DEM BODEN (Feldmeldung 2026-09-18: "fliegt
    # teilweise in der luft"). Zwei Ursachen, und beide Gegenmittel sind je eine
    # Zeile, die ein spaeterer Umbau lautlos wieder einsammeln koennte:
    #   - Mortar.Raise stellt das Fahrzeug auf die Bodennormale (transform.up).
    #     Ueber TransformPoint traegt diese Neigung die 10,5 Einheiten weit
    #     aussen liegende Station mit in die Luft; die Station wird deshalb in
    #     einem WAAGERECHTEN Rahmen aus HullYaw gebaut.
    #   - Ein Strahl von oben trifft neben einem Fahrzeug das Fahrzeug, und das
    #     schwenkende Rohr streicht ueber beide Stationen. GunGround geht an der
    #     eigenen Hierarchie des Geschuetzes und an Maennern vorbei.
    need("Quaternion.Euler(0f, HullYaw(gun), 0f)" in b
         and "static float HullYaw(Transform gun)" in b,
         "die Stationen werden waagerecht gebaut, nicht ueber die geneigte Wanne",
         "die Station folgt der Neigung der Wanne - auf Hang steht die "
         "Besatzung in der Luft")
    need("static bool GunGround(Vector3 at, Transform gun, out float y)" in b
         and "PartOfGun(go, gun)" in b and "IsMan(go)" in b,
         "der Bodenstrahl der Station geht am Geschuetz und an Maennern vorbei",
         "der Bodenstrahl nimmt den ersten Treffer - neben dem Fahrzeug ist das "
         "das Fahrzeug, und das Rohr schwenkt darueber")
    need("NavMesh.SamplePosition(at, out nav, 5f, NavMesh.AllAreas)" in b,
         "die Station liegt auf dem NavMesh, wo es eines gibt",
         "ohne NavMesh-Probe wird der Mann neben begehbaren Boden gewarpt")
    need("const float StationRise" in b
         and "Mathf.Abs(t.position.y - at.y) > StationRise" in b,
         "ein Mann ueber seiner Station wird heruntergeholt",
         "die Hoehentoleranz laesst einen schwebenden Mann schweben")
    # 9: UND ZWAR SOFORT. Die Korrektur oben ist eine Heilung 1,5 s nach dem
    # Spawn, und genau diese anderthalb Sekunden waren die zweite Feldmeldung
    # vom 2026-09-18 ("davor fliegen sie erstmal ne runde"). Der Mann darf gar
    # nicht erst in der Luft entstehen: Crew bekommt beide Stationen statt eines
    # Punktes, um den es sonst blind einen 4,5er Ring legt, und Crews eigener
    # Bodenstrahl beim Aufsetzen geht an der Wanne des Traegers vorbei, statt
    # den Mann auf Deck oder Rohr zu stellen.
    need("static Vector3 CrewGround(Vector3 position, Transform carrier," in crew
         and "PartOfCarrier(go, carrier)" in crew,
         "der Aufsetzstrahl der Besatzung geht am Fahrzeug vorbei",
         "der Aufsetzstrahl nimmt den ersten Treffer - neben dem Fahrzeug ist "
         "das die Wanne, und der Mann entsteht darauf")
    need("wo[i] = CrewGround(wo[i], car.transform, 6f, 30f);" in crew,
         "jeder Ausstiegspunkt wird so auf den Boden gezogen",
         "die Ausstiegspunkte benutzen wieder den ungefilterten Strahl")
    need("internal static GameObject DropSquadAt(Vector3 home, Vector3[] positions," in crew,
         "ein Rufer kann seine eigenen geprueften Plaetze uebergeben",
         "ohne diesen Weg wird jede Gruppe wieder auf einen Ring verteilt")

    # 11: UND ER STEIGT AUCH NICHT WIEDER AUF (dritte Feldmeldung, 2026-09-19:
    # "dann steigen sie in stufen wieder gen himmel auf, und werden wieder
    # runter tp'd"). Eine Treppe im Takt der Postenrunde ist die Schleife, die
    # sich selbst fuettert: der Strahl, der die Station misst, geht durch den
    # Mann, der auf dieser Station steht. Wird sein Kopf als Boden genommen,
    # steht er eine Kapsellaenge hoeher - und beim naechsten Mal noch eine,
    # bis er ueber dem Startpunkt des Strahls ist und auf den echten Boden
    # faellt. Drei Riegel, jeder fuer sich ausreichend:
    #   - die Station darf nie hoeher liegen als StandMaxRise ueber dem Boden,
    #     auf dem das Fahrzeug selbst steht. Ein Mann ist fuenf Einheiten hoch,
    #     ein Deck mehr - darunter passt nichts, worauf man einen Mann stellen
    #     kann.
    #   - ein Mann wird als Mann erkannt, egal auf welchen seiner Collider der
    #     Strahl trifft: die GANZE Ahnenkette wird gelaufen, nicht vier Glieder.
    #   - und zwischen zwei Postenrunden haelt eine Hoehenwache je Frame fest,
    #     was eine halbe Sekunde lang sonst sichtbar waere.
    need("const float StandMaxRise" in b
         and "static float StandCeiling(Vector3 at, Transform gun)" in b
         and "if (at.y > ceiling) at.y = ceiling;" in b
         and "hit.y <= ceiling" in b,
         "keine Station ueber dem Boden des eigenen Fahrzeugs",
         "ohne die Obergrenze kann der Bodenstrahl wieder einen Kopf, ein Deck "
         "oder eine Kiste als Boden nehmen - das ist die Treppe nach oben")
    for name, src in (("ArtyBattery", b), ("Crew", crew)):
        need("if (ai != null && t.GetComponent(ai) != null) return true;" in src
             and "for (int i = 0; i < 4 && t != null; i++)" not in src,
             name + ": ein Mann wird an jedem seiner Collider erkannt",
             name + ": IsMan laeuft wieder nur vier Glieder der Ahnenkette - "
             "ein Treffer auf einen Knochen tief im Modell gilt dann als Boden")
    need("Hold(p, master);" in b
         and "static void KeepDown(Component ai, Vector3 at)" in b
         and "if (now.y - at.y <= StationRise) return;" in b,
         "zwischen zwei Postenrunden haelt eine Hoehenwache je Frame",
         "ohne die Wache je Frame ist jeder Lift eine halbe Sekunde lang zu "
         "sehen - genau der Sprung, der gemeldet wurde")

    # --- 4: the crew belongs to its settlement.
    # The hated list is COPIED, never shared: other parts of the toolkit
    # rewrite a settlement's list in place, and a shared reference would carry
    # that edit back into the men it was taken from.
    need("MatchFaction" in b
         and "_fHated.SetValue(opt, hated.Clone() as Array)" in b,
         "Besatzung uebernimmt die Fraktion der Siedlung (als Kopie)",
         "die Besatzung behaelt eine fremde Fraktion")
    need("StationYaw(gun, PostOf(true)), side, loadout)" in b
         and "new Vector3[] { Station(gun, true), Station(gun, false) }" in b,
         "zwei Mann je Geschuetz, jeder gleich auf seiner eigenen Station",
         "die Besatzung wird auf einen einzigen Punkt gesetzt - Crew legt dann "
         "wieder einen Ring darum, und dessen halbe Seite ist das Fahrzeug")
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
    # Das Symbol gehoert zur KARTE, nicht zum Overlay (Auftrag 2026-09-18: "ich
    # haette die farbe gerne als grau/weiss wie in der og karten
    # beschriftungen"). Vorher war es das Locator-Rot der Patrouillengrenze.
    # Heller Rumpf, dunkler Umriss - ein heller Halo um eine helle Silhouette
    # waere gar keine Silhouette.
    need("new Color(0.88f, 0.87f, 0.83f" in b
         and "new Color(0.12f, 0.11f, 0.09f" in b,
         "Drohnensymbol im Grau/Weiss der Kartenbeschriftung",
         "der Drohnenmarker ist nicht im Grau/Weiss der Kartenbeschriftung")

    # --- 9: DIE DROHNE WARTET NICHT AUF DEN SPIELER (Auftrag 2026-09-18: "ich
    # will das die drohne auch angezeigt wird ohne das man vorher bei dem
    # settlement war"). Mortar.Place sieht jede Siedlung der Ebene ab dem ersten
    # Frame, stellt das Geschuetz aber erst innerhalb von PlaceRange auf, weil
    # die Bodensuche geladene TerrainCollider braucht (E-059). Haengt die
    # Batterie allein am Geschuetz, erscheint die Drohne erst nach dem Besuch.
    # Der Geisterposten fliegt dieselbe Bahn (gleiche Phase aus derselben
    # Mitte), er bekommt aber keine Besatzung und keinen Feuerauftrag, und er
    # muss mit der Szene verschwinden - sonst stehen wieder Kreise einer Ebene
    # auf der Karte, die weg ist.
    need("internal static void GunExpected(int settlementId, Component settlement" in b
         and "p.Ghost = true;" in b,
         "eine Siedlung ohne Geschuetz fliegt ihre Drohne trotzdem",
         "die Drohne entsteht erst mit dem Geschuetz - sie erscheint dann erst "
         "nach dem Besuch der Siedlung")
    need("if (p.Ghost) return true;" in b,
         "der Drohnenfuehrer einer unbesuchten Siedlung gilt als lebend",
         "ohne Besatzung am nicht existierenden Geschuetz bleibt die Drohne am "
         "Boden")
    need("g.Site == null || now - g.SeenAtScan > GhostTimeout" in b,
         "ein Geisterposten stirbt mit seiner Siedlung",
         "die Geisterposten werden nicht abgeraeumt - die Karte behaelt Drohnen "
         "einer Ebene, die weg ist")
    need("DropGhost(settlementId);" in b,
         "das aufgestellte Geschuetz uebernimmt die Bahn seines Geisterpostens",
         "Geist und Posten wuerden zwei Drohnen um dieselbe Siedlung fliegen")
    need("Aimed(_ghosts, from, direction, ref best, ref nearest);" in b,
         "auch die Drohne einer unbesuchten Siedlung laesst sich abschiessen",
         "eine sichtbare Drohne ohne Posten waere unverwundbar")

    # --- seams and the public repository.
    for seam in ("ArtyBattery.BindConfig", "ArtyBattery.Tick()", "ArtyBattery.Draw()"):
        need(seam in plug, "Seam " + seam,
             "Seam fehlt in RevivalPlugin.cs: " + seam)
    for seam in ("ArtyBattery.GunRaised", "ArtyBattery.GunLost",
                 "ArtyBattery.GunExpected"):
        need(seam in s, "Seam " + seam,
             "Seam fehlt in RevivalMortar.cs: " + seam)
    # sync_public.py belongs to the private repository only; in the public
    # copy there is nothing to check here.
    if os.path.exists(sync_p):
        need('"RevivalArtyBattery.cs"' in sync,
             "Datei geht ins oeffentliche Repository",
             "RevivalArtyBattery.cs fehlt in sync_public.py - dort baut das Repo nicht")


def check_native_action_progress():
    """Keep custom timed actions on the base game's interaction presentation."""
    print("[17] Native action progress (static)")
    native_p = os.path.join(ROOT, "Revival.NativeProgress.cs")
    drone_p = os.path.join(ROOT, "RevivalDroneGear.cs")
    repair_p = os.path.join(ROOT, "RevivalConvoyRepair.cs")
    input_p = os.path.join(ROOT, "Revival.FpvDrone.cs")
    turret_p = os.path.join(ROOT, "Revival.CameraTurret.cs")
    if not all(os.path.exists(p) for p in
               (native_p, drone_p, repair_p, input_p, turret_p)):
        bad("Native action progress: source file missing")
        return

    native = io.open(native_p, encoding="utf-8").read()
    drone = io.open(drone_p, encoding="utf-8").read()
    repair = io.open(repair_p, encoding="utf-8").read()
    input_hooks = io.open(input_p, encoding="utf-8").read()
    turret = io.open(turret_p, encoding="utf-8").read()

    if ('"HUD_InteractingProgress"' in native
            and '"ShowInteractingProgressByTime"' in native):
        ok("source routes custom actions to the original interaction HUD")
    else:
        bad("Native action progress: original HUD bridge missing")

    if ('"PlayerInteractingWithItem"' in native
            and '"CharacterInteractState"' in native
            and '"PlayerUseItemAnim"' in native):
        ok("source declares stationary and movement-capable animation paths")
    else:
        bad("Native action progress: original player animation paths missing")

    if ('SetGlobalInteraction(stationary ? 1 : 2)' in native
            and 'SetGlobalInteraction(0)' in native
            and 'ReleaseAnimation();' in native):
        ok("source includes native interaction cleanup")
    else:
        bad("Native action progress: interaction cleanup missing")

    # Cancelling an action must not kill the native animation coroutine: its
    # own tail restores the pose, so a stopped routine left the player stuck
    # in the interaction animation (task a9b39e9116).
    if 'StopCoroutine(_animation)' in native:
        bad("Native action progress: cancel still stops the native animation "
            "coroutine, which strands the player in the interaction pose")
    elif '_animationEnds' in native:
        ok("cancel lets the native interaction clip restore the pose itself")
    else:
        bad("Native action progress: no cancel guard for the running clip")

    drone_wired = ('NativeActionProgress.Begin(_owner' in drone
                   and 'NativeActionProgress.Begin("antenna-deploy"' in drone
                   and drone.count('NativeActionProgress.End(_owner)') >= 3
                   and drone.count('NativeActionProgress.End("antenna-deploy")') >= 2)
    if drone_wired:
        ok("antenna and both drone launches use the native presentation")
    else:
        bad("Native action progress: drone or antenna lifecycle is not wired")

    repair_wired = ('NativeActionProgress.Begin(ProgressOwner' in repair
                    and repair.count('NativeActionProgress.End(ProgressOwner)') >= 4)
    if repair_wired:
        ok("convoy extinguish and repair use the native presentation")
    else:
        bad("Native action progress: convoy repair lifecycle is not wired")

    turret_wired = ('NativeActionProgress.Begin(ReloadProgressOwner' in turret
                    and 'NativeActionProgress.End(ReloadProgressOwner)' in turret
                    and 'TickReloadProgress();' in turret)
    if turret_wired:
        ok("source routes turret reloads to the native presentation")
    else:
        bad("Native action progress: turret reload lifecycle is not wired")

    # Repair owns a separate ConvoyFreezeHook in RevivalConvoyRepair.cs;
    # it is not part of the drone input hook. Both must block movement.
    movement_predicate = '"PlayerMovementController::PlayerCantMovement"'
    stationary_locks = ('Antenna.Frozen' in input_hooks
                        and 'DroneGear.LaunchBusy' in input_hooks
                        and movement_predicate in input_hooks
                        and movement_predicate in repair
                        and 'if (ConvoyRepair.Busy) __result = true;' in repair)
    if stationary_locks:
        ok("stationary native actions retain their movement locks")
    else:
        bad("Native action progress: a stationary movement lock is missing")

    legacy = ('FpvHold.Draw(' in drone or 'Antenna.Draw(' in drone
              or 'Hold.Draw(' in drone or 'static void DrawBar()' in repair
              or 'DrawLadeanzeige' in turret)
    if not legacy:
        ok("legacy custom progress bars are removed")
    else:
        bad("Native action progress: a legacy custom progress bar remains")

    sync_p = os.path.join(ROOT, "sync_public.py")
    if os.path.exists(sync_p):
        sync = io.open(sync_p, encoding="utf-8").read()
        if '"Revival.NativeProgress.cs"' in sync:
            ok("native progress bridge is included in the public sync")
        else:
            bad("Native action progress: bridge missing from sync_public.py")


def _code(src):
    """The source with its // comments stripped.

    Rules of the form "this call must NOT appear" have to read code, not prose:
    a comment that EXPLAINS why a call is absent contains the call's name, and
    a plain substring test then fails on the very documentation that proves the
    rule is being honoured.
    """
    out = []
    for line in src.splitlines():
        i = line.find("//")
        out.append(line if i < 0 else line[:i])
    return "\n".join(out)


def _bind_number(src, section, key):
    """The default of a cfg.Bind("<section>", "<key>", <number>...) as a float,
    or None. The config defaults ARE the balance, so the checks below read them
    out of the source instead of repeating them."""
    import re
    m = re.search(r'Bind\(\s*"%s"\s*,\s*"%s"\s*,\s*(-?[0-9.]+)f?\s*,'
                  % (re.escape(section), re.escape(key)), src)
    return None if m is None else float(m.group(1))


def check_technical():
    """[17] The technical: three places, a gunner who stands, a weak gun.

    Eight rules decide whether this is the vehicle that was ordered rather than
    merely a vehicle, and every one of them is a line or two that a later edit
    could undo without anything looking broken:

      1. Three places, and the gunner is the LAST of them. The seat index is the
         child order of SeatPoints (RE 18.1), so a fourth seat or a reordered
         one silently puts somebody else on the gun.
      2. It must stay fragile. CarSpawn.Prepare hands every mod vehicle
         Durability 2000 - BTR armour. The cap is the VAZ-1111's 150, and it
         must be applied DOWNWARDS only, or a re-applied cap would heal a
         damaged truck.
      3. The gun must stay weaker than the BTR autocannon, per shot AND per
         second. That was the explicit request and it is two numbers in two
         different files, so nothing but a comparison catches it drifting.
      4. Recoil must stay small - also explicitly requested.
      5. It must NOT eat armour. VehicleArmor.GunHit would give a machine gun
         the autocannon's anti-vehicle rate, which is rule 3 undone by the back
         door.
      6. The gun follows the GUNNER'S BODY, which the game already synchronizes.
         Replace that with a private Photon event and every client needs the
         mod's event code to agree - the exact class of bug the turret's
         rotation channel already cost once.
      7. Placement is DERIVED from the donor's own mesh bounds, never typed in:
         the vehicle models are not metric (the BTR's 2.9 m track measures
         +-3.47 units), so absolute numbers are guesses.
      8. The gunner's hands are SOLVED onto the grips, and that solution runs in
         LateUpdate. The game has no animation for a man at a pintle mount, and
         the animator rewrites every bone between Update and LateUpdate - a hand
         placed any earlier is back at the man's side before anything is drawn.
      9. The station is MEASURED on the donor: its height is a ray dropped onto
         the donor's own colliders, its place along the vehicle comes off the
         rear edge, and the man stands one arm's length behind the grips - on
         the mount's own bearing, every frame, because a man at a pintle walks
         around it. Three of the four are field reports. technicalbug.png: a
         bounding box says nothing about what is there, so the station stood in
         the air over the cabin. technical.png: the donor's rear seat is inside
         the cabin, so the weapon reached into the windscreen and stood between
         the front seats, and a gunner rooted to one spot had the grips beside
         him as soon as he turned.
     10. The place IS the gun: whoever is in it mans it without knowing a key,
         and the gun has a BELT with a reload on the game's own progress bar.
         The same report: "man kann auf dem gunner sitz weder aimen noch
         schiessen noch nachladen".
     11. The station stands on a FLOOR. The donor's rear bench is taken off the
         truck before the deck is measured - nobody can sit on it anymore, and
         while it is there it is the highest surface over the rear - and the
         measured deck is held to one step above the floor the donor's own
         passengers stand on. Field report of 2026-09-19: "das mg steht AUF der
         sitzreihe drauf, und der spieler steht fast wie auf einem ausguck".
         Each half fails on its own: without the bench removal the pintle is
         bolted to a backrest, without the ceiling it climbs onto the roof of
         any donor whose body is one closed collider.

    Plus the file rule: RevivalTechnical.cs is machine-written and build.ps1
    needs BOM-less sources, so it is ASCII and its Russian lives in the UTF-8
    file RevivalUralTruck.cs.

    Plus the conversion path: technical_build.py has to keep finding a delivered
    model in the toolkit folder and keep splitting the machine gun off into its
    own mesh. Neither file has to exist - the vehicle ships on generated
    geometry - but a gun welded into the body mesh cannot swivel, so losing that
    split would quietly turn the finished vehicle back into a prop.
    """
    print("[17] Technische: drei Plaetze, stehender Schuetze, schwaches MG (statisch)")
    tech_p = os.path.join(ROOT, "RevivalTechnical.cs")
    ural_p = os.path.join(ROOT, "RevivalUralTruck.cs")
    plug_p = os.path.join(ROOT, "RevivalPlugin.cs")
    cam_p = os.path.join(ROOT, "Revival.CameraTurret.cs")
    sync_p = os.path.join(ROOT, "sync_public.py")
    build_p = os.path.join(ROOT, "technical_build.py")
    if not os.path.exists(tech_p):
        bad("RevivalTechnical.cs fehlt")
        return
    raw = io.open(tech_p, "rb").read()
    t = raw.decode("utf-8", "replace")
    tc = _code(t)            # the same file without its // comments
    u = io.open(ural_p, encoding="utf-8").read() if os.path.exists(ural_p) else ""
    plug = io.open(plug_p, encoding="utf-8").read() if os.path.exists(plug_p) else ""
    cam = io.open(cam_p, encoding="utf-8").read() if os.path.exists(cam_p) else ""
    sync = io.open(sync_p, encoding="utf-8").read() if os.path.exists(sync_p) else ""
    build = io.open(build_p, encoding="utf-8").read() if os.path.exists(build_p) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Technical: " + why)

    # --- file rule: ASCII, no BOM, Cyrillic elsewhere.
    need(not raw.startswith(b"\xef\xbb\xbf"), "keine BOM",
         "RevivalTechnical.cs beginnt mit einer BOM")
    nonascii = [c for c in t if ord(c) > 126]
    need(not nonascii, "reines ASCII",
         "RevivalTechnical.cs enthaelt Nicht-ASCII (" + "".join(nonascii[:8]) + ")")
    need("public static class TechnicalText" in u
         and "internal static string NoAmmo(int itemId)" in u
         and "TechnicalText.Spawned()" in t,
         "zweisprachige Zeilen liegen in RevivalUralTruck.cs",
         "die Spielertexte der Technischen stehen nicht in der UTF-8-Datei")

    # --- 1: three places, the gunner last.
    need("public const int SeatTotal = 3;" in t
         and "public const int GunnerSeat = 2;" in t,
         "drei Plaetze, das MG ist der letzte",
         "Sitzzahl oder Geschuetzindex geaendert")
    need("int keep = SeatTotal - 1;" in t
         and "UnityEngine.Object.Destroy(vorn[i].gameObject);" in t,
         "ueberzaehlige Sitze des Spenders werden entfernt",
         "die Sitze des Spender-UAZ werden nicht auf drei gekuerzt")
    need("gunner.SetAsLastSibling();" in t,
         "der Stehplatz ist das letzte Kind von SeatPoints",
         "der Stehplatz haengt nicht garantiert hinten")
    need("new GameObject[seats.childCount]" in t,
         "Passengers wird auf die neue Sitzzahl gesetzt",
         "Passengers wird nicht neu dimensioniert")

    # --- 2: fragile, and only ever downwards.
    dur = _bind_number(t, "Technical", "Durability")
    need(dur is not None and dur <= 150.0,
         "Trefferpunkte %s, nicht mehr als der VAZ-1111 (150)"
         % ("?" if dur is None else int(dur)),
         "die Technische haelt mehr aus als das schwaechste Vanilla-Auto")
    need("if (have <= cap) return;" in t,
         "der Deckel wirkt nur nach unten",
         "der Trefferpunkt-Deckel koennte Schaden zuruecknehmen")
    need('Marke = "_TECHNICAL"' in t
         and "btr-80a" not in tc.lower() and "_T72" not in tc,
         "der Instanzname ist weder APC noch Panzer",
         "der Name koennte als BTR oder T-72 gelesen werden, dann greift "
         "VehicleArmor und die Technische waere nicht mehr zerbrechlich")

    # --- 3: weaker than the BTR autocannon, per shot and per second.
    mg_dmg = _bind_number(t, "TechnicalGun", "Damage")
    mg_delay = _bind_number(t, "TechnicalGun", "FireDelay")
    apc_dmg = _bind_number(plug, "Turret", "Damage")
    apc_delay = _bind_number(plug, "Turret", "FireDelay")
    have_all = None not in (mg_dmg, mg_delay, apc_dmg, apc_delay)
    need(have_all and mg_dmg < apc_dmg,
         "Schaden je Schuss %s < BTR %s" % (mg_dmg, apc_dmg),
         "das MG trifft nicht mehr schwaecher als das BTR-Bordgeschuetz")
    need(have_all and (mg_dmg / mg_delay) < (apc_dmg / apc_delay),
         "Dauerleistung %.0f/s < BTR %.0f/s"
         % ((mg_dmg / mg_delay) if have_all else 0,
            (apc_dmg / apc_delay) if have_all else 0),
         "die Dauerleistung des MG liegt ueber der des BTR-Bordgeschuetzes")

    # --- 4: low recoil.
    recoil = _bind_number(t, "TechnicalGun", "Recoil")
    need(recoil is not None and 0.0 <= recoil <= 0.10,
         "Rueckstoss %s Grad je Schuss, niedrig wie gefordert" % recoil,
         "der Rueckstoss ist nicht mehr niedrig")

    # --- 5: no anti-armour path.
    need("VehicleArmor.GunHit" not in tc,
         "das MG frisst keine Fahrzeugpanzerung",
         "das MG benutzt den Panzerungspfad des BTR-Geschuetzes")

    # --- 6: the gun follows the gunner's own synchronized bearing.
    need("static void Koerper()" in t
         and "body.transform.rotation = Quaternion.LookRotation" in t,
         "der Koerper des Schuetzen dreht sich auf die Rohrrichtung",
         "der Schuetze dreht sich nicht mit dem MG")
    need("internal static void SlewRemote(Component vgs)" in t
         and "mount.parent.InverseTransformDirection" in t,
         "fremde MGs folgen der Koerperrichtung ihres Schuetzen",
         "das MG anderer Spieler wird nicht nachgefuehrt")
    need("PhotonNetwork" not in tc and "RaiseEvent" not in tc,
         "kein eigener Netzwerkkanal fuer die Rohrrichtung",
         "die Technische oeffnet einen eigenen Photon-Kanal, statt die schon "
         "synchronisierte Koerperrichtung zu benutzen")
    need('"NDR_TECHNICAL_V1"' in t and "DoInstantiate" in t,
         "Spawnmarker wie beim T-72 und beim Ural",
         "der Umbau erreicht Mitspieler und Nachzuegler nicht")

    # --- 7: placement is derived, not typed in.
    need("static bool Karosserie(GameObject car, out Vector3 min, out Vector3 max)" in t
         and "root.InverseTransformPoint(" in t,
         "die Masse kommen aus den Meshes des Spenders",
         "die Aufbaumasse werden nicht am Fahrzeug gemessen")
    need("CfgMountRear.Value * length" in t
         and "CfgDeckHeight.Value * height" in t
         and "CfgSeatBack.Value * length" in t,
         "Lafette und Stehplatz sind Anteile der gemessenen Groesse",
         "die Aufbaupunkte stehen als feste Zahlen im Quelltext")
    need("float unitsPerMetre = length / DonorLengthMetres;" in t,
         "Modelleinheiten werden in Meter umgerechnet",
         "die Groesse des MG haengt nicht an der gemessenen Fahrzeuglaenge")

    # --- 7b: the deck is a MEASURED SURFACE, and the seat is only the fallback.
    #
    # Two field reports, two wrong derivations, and this is what guards the
    # third. technicalbug.png: the station stood on a FRACTION of the bounding
    # box with the height fraction at 1.00 - the top of that box is the highest
    # point of the whole vehicle, aerial included, so gun and gunner hung in the
    # air over the cabin. technical.png: it stood on the donor's rear SEAT, and a
    # jeep's rear seat is inside the cabin - "das geschuetz ist viel zu niedrig,
    # geht durch die scheibe, ist zwischen den sitzen". A collider hit is neither
    # guess: it is the surface a pintle could be bolted to.
    need("static bool Oberflaeche(GameObject car, Vector3 min, Vector3 max," in t
         and "Physics.RaycastAll(" in t
         and "Gehoert(root, col.transform)" in t,
         "die Standflaeche wird am Spender gemessen (Strahl von oben)",
         "die Standflaeche wird nicht mehr am Fahrzeug gemessen - dann ist sie "
         "wieder geraten, und geraten war sie schon zweimal falsch")
    need("static bool Stehplatz(Transform root, Transform seats, out Vector3 point)" in t
         and "FeetAboveSeat" in t
         and "seatPoint.y + FeetAboveSeat" in t,
         "ohne Treffer bleibt der Boden am Ruecksitz des Spenders",
         "der Rueckfall auf den Ruecksitz fehlt - ein Spender ohne greifbare "
         "Kollisionskoerper haette dann gar keine Standflaeche")
    need("TechnicalModel.StandOff() * unitsPerMetre" in t
         and "internal static float StandOff()" in t,
         "der Schuetze steht eine Armlaenge hinter den Griffen",
         "der Abstand des Schuetzen zum MG haengt nicht mehr an der Armlaenge "
         "- dann steht er im Geschuetz oder zu weit davon weg")

    # --- 7e: die Ruecksitzbank ist weg, und die Standflaeche ist ein Fussboden.
    #
    # Der dritte Feldbericht zur selben Stelle (2026-09-19): "aktuell steht das
    # mg AUF der sitzreihe drauf, und der spieler steht fast wie auf einem
    # ausguck auf dem ding. Die hintere sitzreihe muss weg [...] dann den mg
    # stand runter moven und dann ans mg". Eine GEMESSENE Oberflaeche ist das
    # hoechste, was wirklich da ist - und das war die Bank. Beide Haelften der
    # Abhilfe koennen einzeln verloren gehen, ohne dass etwas kaputt aussieht:
    # ohne das Entfernen steht das MG wieder auf der Lehne, ohne die Obergrenze
    # steht es auf dem Dach, sobald ein Spender einen geschlossenen
    # Kollisionskoerper hat.
    need("static int Ruecksitze(GameObject car, Transform seats, Vector3 min," in t
         and "mf.gameObject.SetActive(false);" in t
         and "Ruecksitze(car, seats, min, max, unitsPerMetre);" in t,
         "die Ruecksitzbank des Spenders wird abgebaut",
         "die Ruecksitzbank bleibt stehen - auf ihr landet die Lafette, und "
         "der Schuetze steht auf der Lehne")
    _bauen = t.find("static void Aufbauen(GameObject car)")
    _bank = t.find("Ruecksitze(car, seats, min, max, unitsPerMetre);", _bauen)
    _flaeche = t.find("Oberflaeche(car, min, max, x, mountZ, out deckY)", _bauen)
    need(_bauen >= 0 and _bank > _bauen and _flaeche > _bank,
         "erst die Bank abbauen, dann die Standflaeche messen",
         "die Standflaeche wird gemessen, bevor die Bank weg ist - dann misst "
         "der Strahl wieder die Lehne")
    need("if (Drin(lo, hi, vorn[i], 0.05f * length)) return false;" in t,
         "ein Teil mit einem Vordersitz darin bleibt unangetastet",
         "der Schutz der Vordersitze fehlt - dann kann der Innenraum samt "
         "Fahrersitz verschwinden")
    need("static bool Sitzboden(Transform root, Transform seats, out float y)" in t
         and "boden + CfgDeckStep.Value * height" in t,
         "die Standflaeche wird auf Fussbodenhoehe begrenzt (DeckStep)",
         "die Obergrenze ueber dem Fussboden des Spenders fehlt - dann steht "
         "die Lafette wieder auf dem hoechsten Kollisionskoerper und der "
         "Schuetze wie auf einem Ausguck")
    step = _bind_number(t, "Technical", "DeckStep")
    need(step is not None and 0.0 < step <= 0.30,
         "DeckStep %s der Fahrzeughoehe, eine Stufe und kein Stockwerk" % step,
         "DeckStep fehlt oder laesst wieder ein halbes Fahrzeug Hoehe zu")

    # --- 7c: the place IS the gun, and the gun has a belt.
    #
    # Both come from the same report: "man kann auf dem gunner sitz weder aimen
    # noch schiessen noch nachladen". The place was reachable only through an
    # undocumented key, and there was no reload at all - one round was taken
    # from the inventory per shot.
    need("static void Anbieten()" in t
         and "if (_atGun && !_manning) Anbieten();" in t,
         "wer auf dem Stehplatz steht, besetzt das MG selbst",
         "der Stehplatz besetzt das MG nicht mehr selbst - dann steht ein "
         "Spieler wieder an einem MG, das nichts tut")
    belt = _bind_number(t, "TechnicalGun", "BeltRounds")
    reload_s = _bind_number(t, "TechnicalGun", "ReloadSeconds")
    need("static void Ladebeginn(bool byHand)" in t
         and "NativeActionProgress.Begin(ReloadOwner" in t
         and belt is not None and belt >= 1
         and reload_s is not None and reload_s > 0,
         "Gurt mit %s Schuss, %s s Nachladen am Balken des Spiels"
         % ("?" if belt is None else int(belt), reload_s),
         "das MG hat kein Nachladen mehr - ein Gurt mit Ladezeit war der "
         "Kern des Feldberichts vom 2026-09-18")
    need("static string ManHint(string key)" in u
         and "internal static string Reloading()" in u,
         "Bedienhinweis und Ladeanzeige stehen als Spielertext bereit",
         "die Hinweiszeilen des Schuetzen fehlen in RevivalUralTruck.cs")

    # --- 7d: the station is a CIRCLE. A man at a pintle walks around it, so the
    # gunner is placed behind the MOUNT'S bearing every late frame - after the
    # mount is final and before his arms are solved onto the grips. The same
    # report: "der spieler hat zwar die haende am mg beim grade ausgucken, aber
    # sobald er dreht sieht es glitchy und falsch aus" - at ninety degrees the
    # grips stood beside a man rooted to one spot, and the arm solver, which
    # always reaches, dragged his arms across his chest after them.
    need("static void Stellung(Station st, GameObject body, bool drehen)" in t
         and "new Vector3(0f, 0f, -TechnicalModel.StandOff())" in t,
         "der Schuetze steht bei jedem Schwenk hinter seiner Waffe",
         "der Schuetze steht wieder fest auf einem Punkt - dann liegen die "
         "Griffe beim Drehen neben ihm")
    _late = t.find("internal static void LateAll()")
    _stellung = t.find("if (!losgelassen) Stellung(st, body, selbst);", _late)
    _hands = t.find("Hands(st, body, dt);", _late)
    need(_late >= 0 and _stellung > _late and _hands > _stellung,
         "erst die Lafette, dann der Mann, dann die Arme - in einem LateUpdate",
         "die Reihenfolge in LateAll stimmt nicht mehr: Lafette, Stehplatz und "
         "Arme muessen in dieser Folge im selben Frame geschrieben werden")

    # --- 8: the hands on the grips, after the animation.
    need("static void Arm(Transform upper, Transform fore, Transform hand," in t
         and "Quaternion.AngleAxis(bend, axis.normalized)" in t,
         "die Arme des Schuetzen werden auf die Griffe gerechnet",
         "die Haende des Schuetzen liegen nicht am MG - es gibt keine "
         "Loesung fuer die Arme")
    need("internal static void LateAll()" in t
         and "Technical.LateFrame();" in plug,
         "Haende und fremde MGs laufen im LateUpdate",
         "die Knochen werden nicht nach der Animation geschrieben - der "
         "Animator ueberschreibt sie dann im selben Frame")
    need("internal static Vector3 GripLocal(bool left)" in t
         and "b.min.z + 0.12f * b.size.z" in t,
         "die Griffpunkte kommen aus dem Modell des MG",
         "die Griffpunkte eines gelieferten MG-Modells werden nicht an ihm "
         "gemessen")

    # --- the standing gunner and the third-person view, both ordered explicitly.
    need("_inVehiclePose" in t and "static void ReleasePose()" in t,
         "der Schuetze steht, und die Haltung wird zurueckgegeben",
         "die Stehhaltung fehlt oder wird nicht zurueckgesetzt")
    need("cam.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);" in t
         and "CfgCamBack" in t and "cam.fieldOfView" not in tc,
         "Blick in der dritten Person, Bildwinkel unveraendert",
         "die Kamera zielt nicht in der dritten Person oder schreibt den "
         "Bildwinkel um, was wie ein Zielfernrohr aussieht")

    # --- seams.
    for seam in ("Technical.BindConfig", "TechnicalGun.BindConfig",
                 "Technical.Install", "Technical.Tick()", "Technical.Draw()",
                 "Technical.LateFrame()"):
        need(seam in plug, "Seam " + seam,
             "Seam fehlt in RevivalPlugin.cs: " + seam)
    need('Add(Make("technical"' in u,
         "Seam VehicleRegistry-Eintrag",
         "die Technische steht nicht in der VehicleRegistry")
    need("public const int GunTruck = 4;" in cam
         and "TechnicalGun.LateTick();" in cam,
         "Seam CameraOwner.GunTruck",
         "die Kameravergabe kennt das MG der Technischen nicht")
    # sync_public.py belongs to the private repository only; in the public
    # copy there is nothing to check here.
    if sync:
        need('"RevivalTechnical.cs"' in sync,
             "Datei geht ins oeffentliche Repository",
             "RevivalTechnical.cs fehlt in sync_public.py - dort baut das Repo nicht")

    # --- der Weg vom gelieferten Modell zu den Assetdateien.
    #
    # Nichts davon muss gebaut sein: das Fahrzeug faehrt mit erzeugter Geometrie
    # auf dem Spender-UAZ. Aber das SKRIPT, das ein geliefertes Modell in die
    # Dateien oben verwandelt, muss (a) das Modell im Toolkit-Ordner selbst
    # finden - sonst ist der letzte Schritt wieder Handarbeit an der falschen
    # Stelle - und (b) das MG aus dem Modell herausloesen, denn ein MG, das im
    # selben Mesh wie der Rumpf steckt, kann sich im Spiel nicht drehen.
    need("def source()" in build and "NAME_HINTS" in build
         and "def search_dirs()" in build,
         "technical_build.py sucht das Quellmodell im Toolkit-Ordner",
         "technical_build.py findet ein geliefertes Modell nicht mehr selbst")
    # Der Dateiname eines heruntergeladenen Modells ist der seines Autors. Das
    # gelieferte heisst "pick-up_truck_improvised_fighting_vehicle.glb" und
    # enthaelt das Wort "technical" nirgends - genau daran ist die Suche bisher
    # vorbeigelaufen. Deshalb wird der Name entkernt (nur Buchstaben und
    # Ziffern) und gegen mehrere Schreibweisen geprueft.
    need("def says_technical(name)" in build and "def flat(name)" in build
         and "pickuptruck" in build and "improvisedfighting" in build,
         "der Dateiname wird entkernt und in mehreren Schreibweisen gesucht",
         "technical_build.py sucht wieder nur nach dem Wort 'technical' - "
         "das gelieferte Modell heisst anders und wird dann nicht gefunden")
    need("def guess(table)" in build and "GUN_WORDS" in build
         and '"technical_mg"' in build,
         "das MG wird als eigenes Mesh aus dem Modell geloest",
         "technical_build.py trennt das MG nicht mehr vom Rumpf - dann kann "
         "es sich im Spiel nicht mit dem Schuetzen drehen")


def check_arty_vehicle():
    """[18] The drivable howitzer: the settlement gun, on wheels.

    The order was "take exactly that model and turn it into a real vehicle,
    like the tank". Six rules decide whether that is what was built rather than
    a second howitzer that happens to look similar:

      1. THE SAME MODEL. ArtyModel.Build - the one the settlement gun uses - and
         no second import. A private Assets.Load here would be a fork of the
         art: the day arty_import.py changes, one of the two howitzers moves.
      2. THE SAME FIRE CONTROL. The turret is handed to Mortar, and given back.
         A private reach, dispersion or flight time here would be the fire
         mission written twice, and the two copies would drift apart.
      3. A REAL VEHICLE, not a prop: the donor's driving physics, networking
         and seats stay the donor's, and the rebuild reaches every client
         through the same cached-spawn marker the T-72, the Ural and the
         technical use.
      4. NOT ARMOUR. The instance name must contain neither "btr-80a" nor
         "_T72", or VehicleArmor/Turret/Tank would treat a soft-skinned gun
         truck as an APC; and its hit points are capped DOWNWARDS only, below
         the 2000 CarSpawn.Prepare hands out.
      5. PLACEMENT IS DERIVED from the two measured boxes, never typed in - the
         same rule the technical is held to, for the same reason: the models
         are not metric, so an absolute number is a guess that a re-import
         silently invalidates.
      6. NO SETTLEMENT BOOKKEEPING. A gun that drives belongs to no village, so
         it must never reach the battery's crew/drone path, and losing it must
         not take the settlements' shells or their placement state with it.

    Plus the file rule: RevivalArtyVehicle.cs is machine-written and build.ps1
    needs BOM-less sources, so it is ASCII and its Russian lives in the UTF-8
    file RevivalUralTruck.cs.
    """
    print("[18] Fahrbare Haubitze: Siedlungsgeschuetz auf Raedern")
    arty_p = os.path.join(ROOT, "RevivalArtyVehicle.cs")
    mort_p = os.path.join(ROOT, "RevivalMortar.cs")
    ural_p = os.path.join(ROOT, "RevivalUralTruck.cs")
    plug_p = os.path.join(ROOT, "RevivalPlugin.cs")
    adm_p = os.path.join(ROOT, "Revival.Admin.cs")
    sync_p = os.path.join(ROOT, "sync_public.py")
    if not os.path.exists(arty_p):
        bad("RevivalArtyVehicle.cs fehlt")
        return
    raw = io.open(arty_p, "rb").read()
    a = raw.decode("utf-8", "replace")
    ac = _code(a)            # the same file without its // comments
    mort = io.open(mort_p, encoding="utf-8").read() if os.path.exists(mort_p) else ""
    u = io.open(ural_p, encoding="utf-8").read() if os.path.exists(ural_p) else ""
    plug = io.open(plug_p, encoding="utf-8").read() if os.path.exists(plug_p) else ""
    adm = io.open(adm_p, encoding="utf-8").read() if os.path.exists(adm_p) else ""
    sync = io.open(sync_p, encoding="utf-8").read() if os.path.exists(sync_p) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("ArtyVehicle: " + why)

    # --- file rule: ASCII, no BOM, Cyrillic elsewhere.
    need(not raw.startswith(b"\xef\xbb\xbf"), "keine BOM",
         "RevivalArtyVehicle.cs beginnt mit einer BOM")
    nonascii = [c for c in a if ord(c) > 126]
    need(not nonascii, "reines ASCII",
         "RevivalArtyVehicle.cs enthaelt Nicht-ASCII (" + "".join(nonascii[:8]) + ")")
    need("public static class ArtyVehicleText" in u
         and "ArtyVehicleText.Spawned()" in a,
         "zweisprachige Zeilen liegen in RevivalUralTruck.cs",
         "die Spielertexte der Haubitze stehen nicht in der UTF-8-Datei")

    # --- 1: the settlement gun's own model, imported once.
    need("ArtyModel.Build(out turret, out barrel)" in ac,
         "dasselbe Modell wie das Siedlungsgeschuetz",
         "das Fahrzeug baut sein Modell nicht mit ArtyModel.Build")
    need("Assets.Load" not in ac and ".ndmesh" not in ac,
         "kein zweiter Import der Bohdana",
         "die Datei laedt eigene Meshes - dann gibt es zwei Modelle, die "
         "auseinanderlaufen koennen")

    # --- 2: the fire control is Mortar's, and it is given back.
    need("Mortar.AttachMobile(" in ac and "Mortar.ReleaseMobile(" in ac,
         "das Rohr haengt an der vorhandenen Feuerleitung",
         "die Haubitze uebergibt ihr Rohr nicht an Mortar")
    need("internal static object AttachMobile(" in mort
         and "internal static void ReleaseMobile(" in mort
         and "public bool Mobile;" in mort,
         "Seam Mortar.AttachMobile/ReleaseMobile und Tube.Mobile",
         "die Feuerleitung kennt keine fahrende Haubitze")
    need("Dispersion" not in ac and "FlightSeconds" not in ac
         and "MaxRange" not in ac,
         "keine zweite Feuermission (Reichweite, Streuung, Flugzeit)",
         "das Fahrzeug schreibt die Feuermission ein zweites Mal - zwei "
         "Kopien derselben Ballistik laufen auseinander")

    # --- 3: a real vehicle - donor physics, seats, network.
    need('Prefab = "ural-375(mod)_spawn"' in a,
         "Spender ist der sechsraedrige Ural",
         "der Spender ist nicht mehr der Sechsradlaster")
    need("public const int SeatTotal = 3;" in a
         and "new GameObject[seats.childCount]" in a,
         "drei Plaetze, Passengers wird neu dimensioniert",
         "Sitzzahl oder Passengers-Array stimmen nicht")
    need('"RevivalArtyVehicle.cs"' in sync if sync else True,
         "Datei geht ins oeffentliche Repository",
         "RevivalArtyVehicle.cs fehlt in sync_public.py - dort baut das Repo nicht")
    need('"NDR_ARTYVEH_V1"' in a and "DoInstantiate" in a,
         "Spawnmarker wie beim T-72, beim Ural und bei der Technischen",
         "der Umbau erreicht Mitspieler und Nachzuegler nicht")
    need("RCCCarControllerV2" in a and "VehicleNetworkController" in a
         and "PhotonView" in a,
         "Fahrphysik und Netzwerk des Spenders werden geprueft",
         "der Selbsttest prueft Fahrphysik oder Netzwerk nicht")
    need("Renderer[] all = car.GetComponentsInChildren<Renderer>(true);" in a
         and "r.enabled = false;" in a,
         "der Karosseriewechsel schaltet nur Renderer ab - Kollider, "
         "Radcollider und Schadenszonen des Spenders bleiben",
         "der Karosseriewechsel greift nicht nur an den Renderern an - dann "
         "kann er das Fahrzeug unfahrbar oder untreffbar machen")

    # --- 4: not armour, and the cap only goes downwards.
    dur = _bind_number(a, "ArtyVehicle", "Durability")
    need(dur is not None and 150.0 < dur < 2000.0,
         "Trefferpunkte %s: weniger als BTR-Panzerung (2000), mehr als der "
         "VAZ-1111 (150)" % ("?" if dur is None else int(dur)),
         "die Haubitze ist so zaeh wie ein BTR oder so zerbrechlich wie der "
         "schwaechste Vanillawagen")
    need("if (have <= cap) return;" in a,
         "der Deckel wirkt nur nach unten",
         "der Trefferpunkt-Deckel koennte Schaden zuruecknehmen")
    need('Marke = "_ARTY"' in a
         and "btr-80a" not in ac.lower() and "_T72" not in ac,
         "der Instanzname ist weder APC noch Panzer",
         "der Name koennte als BTR oder T-72 gelesen werden, dann greift die "
         "Panzerungsregel des APC auf einen weichen Geschuetzwagen")

    # --- 5: placement derived from the two measured boxes.
    need("static bool Masse(GameObject car, out Vector3 min, out Vector3 max)" in a
         and "root.InverseTransformPoint(" in a,
         "die Masse kommen aus den Meshes des Spenders",
         "die Aufbaumasse werden nicht am Fahrzeug gemessen")
    need("float scale = donorLength / modelLength" in a,
         "die Modellgroesse ist das Verhaeltnis der beiden Laengen",
         "die Groesse des Modells steht als feste Zahl im Quelltext")

    # --- 6: no settlement bookkeeping behind a gun that drives.
    need("ArtyBattery" not in ac,
         "kein Trupp und keine Drohne an einer fahrenden Haubitze",
         "das Fahrzeug haengt an der Siedlungsbatterie - dann bekaeme ein "
         "Fahrzeug Besatzung und Aufklaerungsdrohne einer Siedlung")
    need("if (_tubes[i].Mobile)" in mort and "static void DropShells(Tube t)" in mort,
         "eine verlorene fahrende Haubitze raeumt nur ihre eigenen Granaten",
         "der Verlust einer fahrenden Haubitze greift in die Buchfuehrung der "
         "Siedlungsgeschuetze ein")

    # --- seams.
    for seam in ("ArtyVehicle.BindConfig", "ArtyVehicle.Install",
                 "ArtyVehicle.Tick()"):
        need(seam in plug, "Seam " + seam,
             "Seam fehlt in RevivalPlugin.cs: " + seam)
    need('Add(Make("arty"' in u,
         "Seam VehicleRegistry-Eintrag",
         "die Haubitze steht nicht in der VehicleRegistry")
    need("ArtyVehicle.SpawnInFront()" in adm,
         "Seam Adminknopf",
         "das Adminmenue kann keine Haubitze setzen - und eine freie F-Taste "
         "gibt es nicht mehr")


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
    check_patrol_fall()
    check_mortar()
    check_arty_battery()
    check_native_action_progress()
    check_technical()
    check_arty_vehicle()
    check_ground_enemies()
    check_version()
    print("=" * 74)
    print("Fehler: %d    Hinweise: %d" % (len(fails), len(warns)))
    for f in fails:
        print("  FEHLER  " + f)
    for w in warns:
        print("  HINWEIS " + w)
    sys.exit(1 if fails else 0)
