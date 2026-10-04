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
import json
import os
import re
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
    "stinger.ndmesh", "stinger_diffuse.png", "stinger_normal.png",
    "stinger_metal.png", "stinger_rough.png", "stinger_icon.png",
    "stinger_weapon_icon.png", "stinger_missile.ndmesh", "stinger_missile_diffuse.png",
    "stinger_missile_normal.png", "stinger_missile_metal.png", "stinger_missile_rough.png",
    "arty_hull.ndmesh", "arty_turret.ndmesh", "arty_barrel.ndmesh",
    "arty_recoil.ndmesh", "arty_diffuse.png", "arty_metal.png", "arty_normal.png",
    # The Gepard (gepard_import.py). gepard_rig.txt is checked in [28].
    "gepard_hull.ndmesh", "gepard_tracks.ndmesh", "gepard_turret.ndmesh",
    "gepard_gun_r.ndmesh", "gepard_gun_l.ndmesh", "gepard_radar_search.ndmesh",
    "gepard_radar_track.ndmesh", "gepard_diffuse.png", "gepard_metal.png",
    # The flyable An-2 (an2_import.py). an2_rig.txt is checked in
    # check_player_an2.
    "an2_body.ndmesh", "an2_glass.ndmesh", "an2_prop.ndmesh",
    "an2_aileron_l.ndmesh", "an2_aileron_r.ndmesh", "an2_elevator.ndmesh",
    "an2_rudder.ndmesh", "an2_diffuse.png", "an2_normal.png",
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
    # Parachute (2067) - own art (parachute_build.py): the packed chute the item
    # shows in the backpack. The canopy in the air is the game's own prefab.
    "parachute.ndmesh", "parachute_diffuse.png", "parachute_normal.png",
    "parachute_icon.png",
    # An-2 repair parts (2069/2070/2071) - own art (an2_parts_build.py).
    "an2part_cable.ndmesh", "an2part_cable_diffuse.png", "an2part_cable_normal.png",
    "an2part_cable_icon.png",
    "an2part_magneto.ndmesh", "an2part_magneto_diffuse.png", "an2part_magneto_normal.png",
    "an2part_magneto_icon.png",
    "an2part_prop.ndmesh", "an2part_prop_diffuse.png", "an2part_prop_normal.png",
    "an2part_prop_icon.png",
    "fireext.ndmesh", "fireext_diffuse.png", "fireext_normal.png", "fireext_icon.png",
    "toolkit.ndmesh", "toolkit_diffuse.png", "toolkit_normal.png", "toolkit_icon.png",
    "mine.ndmesh", "mine_diffuse.png", "mine_normal.png", "mine_icon.png",
    "apmine.ndmesh", "apmine_diffuse.png", "apmine_normal.png", "apmine_metal.png",
    "apmine_rough.png", "apmine_icon.png",
    "crocodile.ndmesh", "crocodile_diffuse.png", "crocodile_normal.png",
    "crocodile_rig.bin",
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
          "fireext.ndmesh", "toolkit.ndmesh", "mine.ndmesh", "apmine.ndmesh",
          "t72_hull.ndmesh",
          "crocodile.ndmesh",
          "t72_turret.ndmesh", "t72_track_left.ndmesh", "t72_track_right.ndmesh",
          "shell125.ndmesh", "thermal.ndmesh", "nvmodule.ndmesh",
          "jammod.ndmesh", "antenna_pack.ndmesh", "battery.ndmesh",
          "survdrone.ndmesh", "parachute.ndmesh",
          "an2part_cable.ndmesh", "an2part_magneto.ndmesh", "an2part_prop.ndmesh",
          "gepard_hull.ndmesh", "gepard_tracks.ndmesh", "gepard_turret.ndmesh",
          "gepard_gun_r.ndmesh", "gepard_gun_l.ndmesh",
          "gepard_radar_search.ndmesh", "gepard_radar_track.ndmesh",
          "an2_body.ndmesh", "an2_glass.ndmesh", "an2_prop.ndmesh",
          "an2_aileron_l.ndmesh", "an2_aileron_r.ndmesh",
          "an2_elevator.ndmesh", "an2_rudder.ndmesh"]

# Erwartete Bildgroessen, abgelesen an den Spielvorlagen.
ICON_SIZES = {
    "mg42_icon.png": (300, 300), "sniper50_icon.png": (300, 300),
    "m7_icon.png": (300, 300),
    "mag68box_icon.png": (300, 300), "mag68drum_icon.png": (300, 300),
    "mgbelt_icon.png": (300, 300), "ammo50_icon.png": (300, 300),
    "law_icon.png": (300, 300), "rocket_icon.png": (300, 300),
    "drone_icon.png": (300, 300), "jammer_icon.png": (300, 300),
    "fireext_icon.png": (300, 300), "toolkit_icon.png": (300, 300),
    "mine_icon.png": (300, 300), "apmine_icon.png": (300, 300),
    "shell125_icon.png": (300, 300),
    "thermal_icon.png": (300, 300), "nvmodule_icon.png": (300, 300),
    "jammod_icon.png": (300, 300), "antenna_pack_icon.png": (300, 300),
    "battery_icon.png": (300, 300), "survdrone_icon.png": (300, 300),
    "parachute_icon.png": (300, 300),
    "an2part_cable_icon.png": (300, 300), "an2part_magneto_icon.png": (300, 300),
    "an2part_prop_icon.png": (300, 300),
    "mg42_weapon_icon.png": (317, 183), "sniper50_weapon_icon.png": (317, 183),
    "m7_weapon_icon.png": (317, 183),
    "law_weapon_icon.png": (317, 183),
    "stinger_icon.png": (300, 300), "stinger_weapon_icon.png": (317, 183),
    "stinger_missile_icon.png": (300, 300),
    "scope50.png": (1920, 1920),
    "t72_scope.png": (1920, 1920),
    "apc_scope.png": (1920, 1920),
    "stinger_scope.png": (1920, 1920),
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
                   "RocketHook", "Turret", "Arena", "Stinger"]
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
    src += io.open(os.path.join(ROOT, "Revival.Stinger.cs"), encoding="utf-8").read()
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

    6.42 added the two behaviours that give a group somewhere to be: a patrol
    that walks the route the editor drew, and a guard that spreads onto a
    perimeter around its point instead of standing on it. Both are held to the
    same promise - the checks below insist that every one of their movement
    orders is a walk, that the patrol actually advances from waypoint to
    waypoint, and that a fight stops the patrol instead of counting as a stall.
    The published route is bounded in length and point count, and a patrol
    without a route is refused rather than left standing around.

    The rest guards the shape of the feature. Ground groups travel on their own
    hash-verified /runtime/ground envelope, so an older client keeps its convoy
    data; a bad snapshot is rejected as a whole before any live group is
    touched. Only the Photon master spawns, and it ADOPTS men it finds by their
    cached spawn key instead of spawning a second copy of the same group. The
    limits have to agree between grounddef.py and the plugin, otherwise the
    editor happily publishes a snapshot the game then refuses in full.

    The alive layer (Revival.GroundAlive.cs, Revival.GroundAliveCore.cs) made
    'roam' - guard with roam - the default and turned the old default
    'waiting' into it; its behaviour, cover, call, search and cost are proven
    by research/ground_alive_check.py, which must be in the repository.

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

    # --- the two behaviours with a place to be: die Route und die Stellung.
    need("if (s.GroundDuty == GroundMode.Patrol) { PatrolStep(f, s, now); continue; }" in block
         and "if (s.GroundDuty == GroundMode.Guard) { GuardStep(f, s, now); continue; }" in block,
         "Laufroute und Stellung haben ihr eigenes Verhalten",
         "die Bodengruppen kennen wieder nur Warten und Wandern - Laufrouten "
         "und Stellung halten fehlen")
    need(block.count("MainWalk") >= 6,
         "auch Streife und Stellungswechsel gehen im Schritt",
         "ein Bewegungsbefehl von Streife oder Stellung traegt kein MainWalk")
    need("s.GroundLeg = NextLeg(s);" in block and "static int NextLeg(Squad s)" in block,
         "die Streife wandert von Wegpunkt zu Wegpunkt weiter",
         "die Streife bleibt auf ihrem ersten Wegpunkt stehen")
    need("if (now < s.GroundContact)" in block,
         "ein Feuergefecht haelt die Streife an, statt als Stillstand zu zaehlen",
         "das Gefecht der Streife wird als haengender Marsch gewertet")

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
    need('if (g.Behavior != "waiting" && g.Behavior != "walking"' in g
         and '&& g.Behavior != "patrol" && g.Behavior != "guard"' in g
         and '&& g.Behavior != "roam")' in g,
         "gepruefte Verhalten: Bewachen mit Rundgang, Streife, Stellung halten, Wandern",
         "das Verhalten einer Bodengruppe wird nicht mehr vollstaendig geprueft")
    need('if (g.Behavior == "waiting") g.Behavior = "roam";' in g
         and "if (!g.Builtin) NpcWar.GroundAliveOn(g.Tag, g.Faction);" in g,
         "alte Daten (warten) bewachen mit Rundgang; Editorgruppen werden lebendig",
         "alte wartende Editorgruppen stehen wieder nur herum")
    need('throw new IOException("Ground patrol without a route")' in g,
         "eine Streife ohne Route wird abgelehnt",
         "eine Streife ohne Route wird angenommen und steht dann herum")
    need("static void Route(string text, List<Vector3> into)" in g
         and 'throw new IOException("Too many ground route points")' in g
         and 'throw new IOException("Ground route too long")' in g,
         "die veroeffentlichte Route ist in Laenge und Punktzahl begrenzt",
         "eine veroeffentlichte Route darf beliebig gross werden")
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
    need('BEHAVIORS = ["roam", "patrol", "guard", "walking"]' in gdef
         and 'LEGACY_BEHAVIORS = {"waiting": "roam"}' in gdef
         and '("behavior", "roam")' in gdef,
         "der Editor bietet genau die vier Verhalten des Plugins an, Rundgang als Vorgabe",
         "der Editor bietet ein Verhalten an, das das Plugin nicht kennt")
    cs_route = re.search(r"MaxRoutePoints = (\d+)", g)
    need(cs_route is not None and value("MAX_ROUTE_POINTS") == cs_route.group(1),
         "Editor und Plugin erlauben gleich viele Wegpunkte",
         "Editor und Plugin zaehlen verschiedene Wegpunkte - eine gespeicherte "
         "Route waere dann im Spiel ungueltig")
    need('ROUTE_MODES = ["pingpong", "loop"]' in gdef
         and 'if (c[10] != "loop" && c[10] != "pingpong")' in g,
         "Rundkurs und Hin-und-zurueck heissen auf beiden Seiten gleich",
         "die Routenrichtung des Editors kennt das Plugin nicht")

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
        need("function routeEditor(" in gjs and "'+ waypoint'" in gjs
             and "function drawRoute(" in gjs,
             "die Laufroute wird im Editor eingetragen und gezeichnet",
             "im Editor laesst sich keine Laufroute eintragen - genau die "
             "Luecke, die diese Aufgabe schliessen sollte")
        need("function seedRoute(" in gjs and "moveGroup(d, x, z)" in gjs,
             "eine neue Streife bekommt eine Route, und Kopien bekommen ihre eigene",
             "verteilte Streifen laufen alle auf der Route ihrer Vorlage")
        need('<script src="ground.js"></script>' in html
             and "NDRGround.init();" in app and "NDRGround.draw();" in app
             and "NDRGround.mousedown(ev)" in app,
             "die Bodenoberflaeche haengt in Seite, Zeichnung und Maus",
             "editor/ground.js ist nicht vollstaendig eingehaengt")

    # --- die beiden Regressionen zu diesem Feature muessen im Repository
    # liegen. verify.py fuehrt sie nicht aus (es startet keine Unterprozesse).
    if os.path.isdir(os.path.join(ROOT, "research")):
        for check in ("ground_enemy_check.py", "ground_editor_check.js", "ground_alive_check.py"):
            need(os.path.exists(os.path.join(ROOT, "research", check)),
                 "research/" + check + " liegt vor",
                 "research/" + check + " fehlt - die Bodengegner sind unbelegt")


def check_helipads():
    """[21] Editor helicopter landing pads.

    A pad is authored once and then has to survive four hand-offs: the editor
    writes it, compdef derives it, the live channel carries it and the plugin
    builds it. Every one of those has its own idea of how wide a row is and what
    a surface is called, and nothing links them but the checks below.

    The one promise that is not a format: a troop landing that falls on a pad
    must USE the pad. A deck is flat by construction, so the ring search for
    level ground has nothing left to find - but if the search still runs first,
    the machine sets down beside the deck and the pad is decoration.
    """
    print("[21] Helipads (Editor)")
    pad_p = os.path.join(ROOT, "Revival.Helipads.cs")
    if not os.path.exists(pad_p):
        bad("Revival.Helipads.cs fehlt - die Landeplaetze sind nicht gebaut")
        return
    import re
    raw = io.open(pad_p, "rb").read()
    pad = raw.decode("utf-8", "replace")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Helipads: " + why)

    live = read("Revival.LiveRoutes.cs")
    plug = read("RevivalPlugin.cs")
    admin = read("Revival.Admin.cs")
    troop = read("RevivalTroopInsertion.cs")
    sync = read("sync_public.py")

    # --- file rule: build.ps1 reads it as UTF-8 without BOM, and the ONLY
    # characters outside ASCII it may hold are the Cyrillic of the player-facing
    # Loc.T line (AGENTS.md). Anything else is mojibake from a wrong code page.
    need(not raw.startswith(b"\xef\xbb\xbf"), "keine BOM",
         "Revival.Helipads.cs beginnt mit einer BOM")
    strange = sorted(set(c for c in pad
                         if ord(c) > 126 and not 0x400 <= ord(c) <= 0x4FF))
    need(not strange,
         "ausserhalb ASCII nur Kyrillisch (Spielertext)",
         "Revival.Helipads.cs enthaelt Zeichen, die weder ASCII noch "
         "Kyrillisch sind: " + " ".join("U+%04X" % ord(c) for c in strange))

    # --- the published channel: own envelope, hashed, all-or-nothing.
    need('envelope[0] != "NDR-PADS-1"' in live and "Hash(tsv) != envelope[1]" in live,
         "eigener, hashgepruefter Umschlag fuer die Landeplaetze",
         "der Landeplatzkanal hat keinen eigenen geprueften Umschlag")
    need("Helipads.Parse(lines);" in live,
         "ein fehlerhafter Stand wird vor jedem Bau abgelehnt",
         "ein fehlerhafter Landeplatzstand wird erst beim Bauen bemerkt")
    need("keeping the last verified pads" in live,
         "bei Ausfall bleibt der letzte gepruefte Stand stehen",
         "ein Ausfall des Servers loescht die Landeplaetze")

    # --- the deck is a PLATFORM: highest ground under it, skirt to the slope.
    need("deck = best + DeckLift;" in pad and "if (y > best) best = y;" in pad,
         "das Deck liegt ueber dem hoechsten Boden darunter",
         "das Deck mittelt die Hoehen - dann ist es bergseitig vergraben")
    need("Mathf.Min(y - SkirtBite, deck - SkirtBite) - deck" in pad,
         "der Schuerze folgt dem Boden an jedem Randpunkt",
         "die Schuerze steht auf einer festen Hoehe - am Hang klafft eine Luecke")
    need("MeshCollider collider = surface.AddComponent<MeshCollider>();" in pad,
         "das Deck traegt einen Collider",
         "ohne Collider findet der Landeanflug das Deck nicht und der "
         "Hubschrauber setzt im Hang auf")
    need("RevivalTroopInsertion.TerrainHeight(xz, out y)" in pad,
         "die Hoehe kommt aus den Hoehendaten, nicht aus einem Strahl",
         "ein Strahl traefe ein schon stehendes Deck und stapelte das naechste "
         "darauf")

    # --- 6.39: the pad is raised, and the ramp around it is what makes the
    # raising bearable. A lift without an apron is a kerb; an apron whose
    # profile is linear still leaves an edge at the top and a step at the
    # bottom, which is exactly what "smooth" was asked NOT to be.
    need("static float ApronWidth(float radius)" in pad and "ApronRings" in pad,
         "das Deck hat eine abgeschraegte Rampe ausserhalb des Radius",
         "das Deck endet an einer senkrechten Kante - der Uebergang zum Boden "
         "ist eine Stufe")
    need("float profile = rim * (t * t * (3f - 2f * t));" in pad,
         "das Rampenprofil ist eine Smoothstep-Kurve, oben und unten flach",
         "die Rampe ist eine schiefe Ebene - oben und unten bleibt eine Kante")
    need("Mathf.Min(Mathf.Max(profile, here - deck + 0.02f), 0f)" in pad,
         "die Rampe liegt ueber dem Boden, den sie ueberquert, und nie ueber "
         "dem Deck",
         "die Rampe schneidet in den Hang oder steigt ueber das Deck")

    # --- 6.39: one mesh, one texture. Three flat colours on three meshes is
    # what made the pad look like plastic with a poster on it.
    need("const float DeckUv" in pad and "0.5f + cos * tex * 0.5f" in pad,
         "die Deckstextur ist radiusbezogen aufgespannt",
         "die UV des Decks haengen an Metern - dann traegt ein 60-m-Platz ein "
         "anderes Bild als ein 8-m-Platz")
    need('Tex("helipad_" + kind + ".png", false)' in pad
         and 'mat.EnableKeyword("_NORMALMAP")' in pad
         and 'mat.EnableKeyword("_METALLICGLOSSMAP")' in pad,
         "Deck, Normalen- und Metallkarte haengen am Material",
         "eine der drei Karten wird gesetzt, aber ohne Keyword nie gelesen")

    tex_p = os.path.join(ROOT, "helipad_texture.py")
    need(os.path.exists(tex_p),
         "helipad_texture.py liegt vor",
         "helipad_texture.py fehlt - die Deckstexturen lassen sich nicht neu "
         "bauen")
    if os.path.exists(tex_p):
        tex = io.open(tex_p, encoding="utf-8").read()
        cs_uv = re.search(r"const float DeckUv = ([\d.]+)f;", pad)
        py_uv = re.search(r"^DECK = ([\d.]+)", tex, re.M)
        need(cs_uv is not None and py_uv is not None
             and float(cs_uv.group(1)) == float(py_uv.group(1)),
             "Plugin und Texturgenerator teilen denselben Deckradius",
             "helipad_texture.py malt den Rand woanders hin, als das Plugin ihn "
             "aufspannt - Rand, H und Rampe sitzen dann verschoben")

    # --- 6.39: a pad clears the site it is built on, and gives it back.
    need("static void ClearProps(Pad p, float deck)" in pad
         and "static void ClearTerrain()" in pad,
         "der Platz raeumt Requisiten, Baeume und Gras aus seiner Flaeche",
         "ein Baum waechst durch das Deck")
    need("data.treeInstances = keep.ToArray();" in pad
         and "take.Data.treeInstances = all;" in pad,
         "entnommene Baeume werden aufbewahrt und wieder eingesetzt",
         "ein verschobener Platz laesst die Baeume geloescht zurueck")
    need("static void Restore()" in pad and "Restore();" in pad,
         "jede Aenderung an der Welt wird zurueckgenommen",
         "die Welt bleibt veraendert, wenn die Plaetze verschwinden")
    need("if (Networked(t)) return null;" in pad
         and "box.size.x <= cap && box.size.z <= cap" in pad
         and "if (hit is TerrainCollider) return null;" in pad,
         "nur kleine, eigene Objekte werden geraeumt",
         "der Platz koennte das Terrain oder ein Netzobjekt abschalten")
    # --- 6.40: the clearing is authored, and only an authored one takes a
    # structure. Without one the old limit has to stand unchanged.
    need("static float Cap(Pad p)" in pad
         and "Sited(p) && Footprint(p) > MaxProp ? Footprint(p) : MaxProp" in pad
         and "const float MaxProp = 14f;" in pad,
         "gezeichnete Raeumung nimmt grosse Haeuser, sonst bleibt die alte Grenze",
         "ein gezeichnetes Baufeld kann ein grosses Haus nicht abschalten")
    need('"ClearAreaKey", "F7"' in pad
         and "internal static bool ClearAreaMapClick()" in pad
         and "if (Helipads.ClearAreaMapClick()) return;" in admin
         and "static bool InClearArea(float x, float z)" in pad,
         "vier Kartenpunkte definieren ein dauerhaftes Raeumfeld",
         "Vierpunkt-Raeumwerkzeug fehlt")
    need("static float Footprint(Pad p)" in pad
         and "return p.Clear > deck ? p.Clear : deck;" in pad,
         "die Raeumung vergroessert die Flaeche nur, sie verkleinert sie nie",
         "ein zu kleiner Raeumungsradius wuerde den Boden unter dem Deck "
         "stehen lassen")
    need("Transform group = Group(t);" in pad
         and "LODGroup group = t.GetComponentInParent<LODGroup>();" in pad,
         "geraeumt wird die ganze LOD-Gruppe, nie ein Einzelteil davon",
         "der Platz koennte einzelne Pfosten aus einem Zaun schalten, der als "
         "ganzes Objekt viel zu gross zum Raeumen ist")
    need("hull.enabled = false;" in pad and "terrain.Flush();" in pad,
         "der gefaellte Baum verliert auch seinen Collider",
         "ein gefaellter Baum bleibt unsichtbar im Weg stehen")

    # --- the terrain API is only reachable with its two references, and the
    # file does not compile without them. A missing reference is a build error
    # in every OTHER file too, so it is worth naming here.
    build = read("build.ps1")
    need("UnityEngine.TerrainModule.dll" in build
         and "UnityEngine.TerrainPhysicsModule.dll" in build,
         "build.ps1 referenziert Terrain und TerrainPhysics",
         "ohne diese Referenzen kennt der Compiler Terrain, TerrainData, "
         "TreeInstance und TerrainCollider nicht")
    need('"helipad_concrete.png"' in build and '"helipad_steel.png"' in build,
         "die Deckstexturen werden mitinstalliert",
         "die Deckstexturen fehlen in der Asset-Liste von build.ps1 - im Spiel "
         "faellt der Platz auf Farbflaechen zurueck")

    # --- the landing zone asks the pads FIRST.
    snap = troop.find("Helipads.Snap(")
    search = troop.find("FindLandingSpot(new Vector3(d.X, 0f, d.Z), yaw, out lz)")
    need(snap > 0 and search > snap,
         "eine Landezone auf einem Platz benutzt den Platz",
         "die Ringsuche laeuft vor der Platzabfrage - der Hubschrauber setzt "
         "neben dem Deck auf")

    # --- seams and the public repository.
    need("Helipads.Tick();" in plug and "Helipads.Draw();" in plug
         and "Helipads.BindConfig(Config);" in plug,
         "Seams Helipads.BindConfig/Tick/Draw in RevivalPlugin.cs",
         "ein Seam fehlt in RevivalPlugin.cs: Helipads")
    need('"Revival.Helipads.cs"' in sync,
         "Revival.Helipads.cs geht ins oeffentliche Repository",
         "Revival.Helipads.cs fehlt in sync_public.py - das oeffentliche "
         "Repository laesst sich dann nicht uebersetzen")

    # --- limits that agree on both sides of the wire.
    cs_pads = re.search(r"internal const int MaxPads = (\d+);", pad)
    cs_radius = re.search(r"internal const float MinRadius = ([\d.]+)f, "
                          r"MaxRadius = ([\d.]+)f;", pad)
    cs_columns = re.search(r"c\.Length < (\d+) \|\| c\.Length > (\d+)", pad)
    cs_clear = re.search(r"internal const float MaxClear = ([\d.]+)f;", pad)
    need(cs_pads is not None and cs_radius is not None and cs_columns is not None,
         "die Grenzen des Plugins sind ablesbar",
         "die Grenzen in Revival.Helipads.cs haben ihre Form verloren")

    hdef_p = os.path.join(ROOT, "helipaddef.py")
    if cs_pads is None or not os.path.exists(hdef_p):
        return
    hdef = io.open(hdef_p, encoding="utf-8").read()

    def value(name):
        m = re.search(r"^%s = ([\d.]+)$" % name, hdef, re.M)
        return m.group(1) if m else None

    need(value("MAX_PADS") == cs_pads.group(1),
         "die Platzgrenze stimmt mit dem Plugin ueberein",
         "helipaddef.py und das Plugin nennen verschiedene Grenzen - der Editor "
         "veroeffentlicht dann einen Stand, den das Spiel ganz ablehnt")
    need(value("MIN_RADIUS") is not None and value("MAX_RADIUS") is not None
         and float(value("MIN_RADIUS")) == float(cs_radius.group(1))
         and float(value("MAX_RADIUS")) == float(cs_radius.group(2)),
         "der erlaubte Radius stimmt mit dem Plugin ueberein",
         "Editor und Plugin erlauben verschiedene Radien")
    need(cs_clear is not None and value("MAX_CLEAR") is not None
         and float(value("MAX_CLEAR")) == float(cs_clear.group(1)),
         "die erlaubte Raeumung stimmt mit dem Plugin ueberein",
         "der Editor laesst eine Raeumung zu, die das Plugin ablehnt - es "
         "verwirft dann die ganze Platztabelle")
    columns = re.search(r"TSV_COLUMNS = \[(.*?)\]", hdef, re.S)
    need(columns is not None
         and len(re.findall(r'"[A-Za-z]+"', columns.group(1))) == int(cs_columns.group(1)),
         "Editor und Plugin zaehlen dieselben Spalten",
         "die Spaltenzahl von helipaddef.py passt nicht zum Parser des Plugins")
    need('SURFACES = ["concrete", "steel", "cleared"]' in hdef
         and 'p.Surface != "concrete" && p.Surface != "steel"' in pad
         and 'p.Surface != "cleared"' in pad,
         "Editor und Plugin kennen dieselben Oberflaechen",
         "der Editor bietet eine Oberflaeche an, die das Plugin nicht kennt")

    comp = read("compdef.py")
    need("helipaddef.validate_pads(" in comp and "helipaddef.to_helipads_tsv(" in comp,
         "das Speichern des Editors prueft und leitet die Landeplaetze ab",
         "der Editor speichert Landeplaetze ungeprueft oder leitet keine "
         "Laufzeitdatei ab")

    route = read("routeeditor.py")
    need('path == "/runtime/helipads"' in route and 'path == "/helipads.js"' in route,
         "der Editorserver liefert Landeplaetze und Oberflaeche aus",
         "der Editorserver kennt den Landeplatzkanal nicht")

    editor_dir = os.path.join(ROOT, "editor")
    if os.path.isdir(editor_dir):
        def editor_file(name):
            path = os.path.join(editor_dir, name)
            return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

        pjs = editor_file("helipads.js")
        app = editor_file("app.js")
        html = editor_file("index.html")
        need("function nose(d, distance)" in pjs and "P.drag === 'centre'" in pjs,
             "Mittelpunkt und Blickrichtung lassen sich auf der Karte ziehen",
             "der Landeplatzeditor kennt keinen Ziehgriff fuer die Richtung")
        need('<script src="helipads.js"></script>' in html
             and "NDRPads.init();" in app and "NDRPads.draw();" in app
             and "NDRPads.mousedown(ev)" in app,
             "die Landeplatzoberflaeche haengt in Seite, Zeichnung und Maus",
             "editor/helipads.js ist nicht vollstaendig eingehaengt")

    if os.path.isdir(os.path.join(ROOT, "research")):
        need(os.path.exists(os.path.join(ROOT, "research", "helipad_check.py")),
             "research/helipad_check.py liegt vor",
             "research/helipad_check.py fehlt - die Landeplaetze sind unbelegt")


def check_settings():
    """[27c] Unified player settings (task P9, Revival.Settings.cs,
    docs/ai/tasks/settings-cleanup.md).

      1. Revival.Settings.cs is bound, ticked and drawn in RevivalPlugin, the
         settings window releases the cursor, and sync_public ships the file.
      2. [Effects] ParticleDensity reaches every particle system: each source
         that adds a ParticleSystem uses Fx, and every `ps.Play();` in them is
         followed by Fx.Apply / Fx.ApplyVital (or, for a hand-emitted pool,
         names the Fx.Keep gate on the same line).
      3. Tracers, recoil, radar rotation and muzzle flashes ask Anim.
      4. The permanent help texts (drone guide, east zone label) go through Hints.
    """
    import re

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Settings: " + why)

    src = read("Revival.Settings.cs")
    if not src:
        bad("Settings: Revival.Settings.cs missing")
        return
    plugin = read("RevivalPlugin.cs")
    need("Settings.BindConfig(Config)" in plugin and "Settings.Tick()" in plugin
         and "Settings.Draw()" in plugin and "Settings.IsOpen" in plugin,
         "settings bound, ticked, drawn; window frees the cursor",
         "RevivalPlugin lost Settings.BindConfig/Tick/Draw or the cursor release")
    need('"Revival.Settings.cs"' in read("sync_public.py"),
         "sync_public ships Revival.Settings.cs", "sync_public.py does not ship Revival.Settings.cs")
    for key in ('"ParticleDensity"', '"Animations"', '"Recoil"', '"RadarRotation"', '"Tracers"',
                '"MuzzleFlashes"', '"Mode"', '"Seconds"', '"Key"', '"MenuKey"', '"Version"'):
        need(key in src, "settings key " + key + " bound", "key " + key + " is gone")

    missing = []
    for name in sorted(os.listdir(ROOT)):
        if not name.endswith(".cs") or name == "Revival.Settings.cs":
            continue
        text = read(name)
        if "AddComponent<ParticleSystem>" not in text:
            continue
        if "Fx." not in text:
            missing.append(name + " (no Fx call)")
            continue
        lines = text.splitlines()
        for i, line in enumerate(lines):
            if re.match(r"\s*ps\.Play\(\);", line):
                nxt = lines[i + 1] if i + 1 < len(lines) else ""
                if "Fx." not in line and "Fx.Apply" not in nxt and "Fx.Apply" not in lines[i - 1]:
                    missing.append(name + ":" + str(i + 1))
    need(not missing, "every particle source follows [Effects] ParticleDensity",
         "particles outside the density level: " + ", ".join(missing[:6])
         + " - call Fx.Apply(ps) after Play()")

    need("Anim.Tracers" in _body(read("Revival.CombatHooks.cs"), "internal static void SpawnTracer(List<Vector3> points)")
         and "Anim.Tracers" in read("RevivalTechnical.cs") and "Anim.Tracers" in read("RevivalGepard.cs")
         and "Anim.Tracers" in read("Revival.CameraTurret.cs"),
         "tracers follow [Effects] Tracers", "a tracer ignores [Effects] Tracers")
    gep = read("RevivalGepard.cs")
    need("Anim.Radar" in gep and "Anim.Recoil" in gep and "Anim.MuzzleFlash" in gep
         and "Anim.Recoil" in read("RevivalArtyBattery.cs"),
         "radar rotation, recoil and muzzle flashes can be switched off",
         "Gepard/artillery lost an Anim switch")
    need("Hints.Alpha" in read("RevivalDroneGear.cs") and "Hints.Alpha" in read("Revival.EastZones.cs"),
         "drone guide and zone label follow [Hints]", "a permanent help box ignores [Hints]")


def check_vehicle_gunner_ai():
    """[27b] NPC vehicle gunners: sight, spread, reaction (Revival.GunnerAI.cs,
    docs/ai/tasks/vehicle-gunner-ai.md).

      1. Revival.GunnerAI.cs is ASCII without a BOM and bound in RevivalPlugin.
      2. Both AI guns (Patrol.Gun, TechnicalCrew) take sight through the
         vegetation test and through the hull a player sits in, get reaction
         and spread from GunnerAI, and log every round.
      3. The patrol gun rechecks sight before every round and fires without it
         only inside SuppressSeconds.
      4. research/vehicle_gunner_check.py (the real formulas, headless) passes
         when the .NET 3.5 compiler is there.
    """
    import subprocess

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Vehicle gunner AI: " + why)

    path = os.path.join(ROOT, "Revival.GunnerAI.cs")
    if not os.path.exists(path):
        bad("Vehicle gunner AI: Revival.GunnerAI.cs missing")
        return
    raw = open(path, "rb").read()
    need(not raw.startswith(b"\xef\xbb\xbf") and all(b < 127 for b in raw),
         "Revival.GunnerAI.cs is ASCII without a BOM", "Revival.GunnerAI.cs is not plain ASCII")
    ai = raw.decode("ascii", "replace")
    need("GunnerAI.BindConfig(Config)" in read("RevivalPlugin.cs"),
         "[GunnerAI] config is bound", "RevivalPlugin does not bind [GunnerAI]")
    need("TreeInstance" in ai and "SightThreshold" in ai,
         "gunner sight counts terrain trees and bushes", "the vegetation test is gone")

    patrol = read("Revival.Patrol.cs")
    einer = _body(patrol, "static void Einer(Unit u)")
    sicht = _body(patrol, "static float Sichtbar(Unit u, CombatTarget c)")
    shoot = _body(patrol, "static bool Schiessen(Unit u, Vector3 ziel, bool suppress)")
    need("GunnerAI.Transmit" in sicht and "GunnerAI.OnCarrier" in sicht,
         "patrol gun sight: vegetation and the seated player's hull",
         "Patrol.Gun.Sichtbar lost the vegetation or the carrier test")
    need("Sichtbar(u, u.GunTarget)" in einer and "SuppressSeconds" in einer
         and einer.find("Sichtbar(u, u.GunTarget)") < einer.find("Schiessen("),
         "patrol gun: sight before every round, suppression time-limited",
         "Patrol.Gun.Einer fires without a sight check or without the suppression limit")
    need("GunnerAI.Reaction" in einer and "CfgPatrolGunNotice" not in einer,
         "patrol gun reaction from range and visibility", "Patrol.Gun uses a fixed reaction again")
    need("GunnerAI.LogShot" in shoot and "Streuweite" in shoot,
         "patrol gun: GunnerAI spread and one log line per round",
         "Patrol.Gun.Schiessen lost the GunnerAI spread or the shot log")

    crew = read("RevivalTechnicalCrew.cs")
    need("GunnerAI.Transmit" in _body(crew, "static float Sicht(Truck t")
         and "GunnerAI.Spread" in _body(crew, "static void Feuern(Truck t")
         and "GunnerAI.LogShot" in _body(crew, "static void Feuern(Truck t")
         and "GunnerAI.Reaction" in _body(crew, "static void Zielen(Truck t)"),
         "technical gunner: vegetation sight, GunnerAI spread/reaction, shot log",
         "TechnicalCrew no longer uses GunnerAI for sight, spread, reaction or the log")

    check = os.path.join(ROOT, "research", "vehicle_gunner_check.py")
    need(os.path.exists(check), "research/vehicle_gunner_check.py present",
         "research/vehicle_gunner_check.py missing")
    csc = os.path.join(os.environ.get("WINDIR", "C:\\Windows"),
                       "Microsoft.NET", "Framework", "v3.5", "csc.exe")
    if os.path.exists(check) and os.path.exists(csc):
        r = subprocess.run([sys.executable, check], cwd=os.path.join(ROOT, "research"),
                           capture_output=True, text=True)
        need(r.returncode == 0 and "PASS" in r.stdout,
             "vehicle gunner numbers pass the headless check",
             "research/vehicle_gunner_check.py fails: " + (r.stdout + r.stderr).strip()[-300:])
    else:
        warn("Vehicle gunner AI: .NET 3.5 csc not found - headless check skipped")


def check_road_clear():
    """[27] Every road free (research/roadclear.py -> Revival.RoadClearData.cs).

    The list is generated offline from the road meshes and the asphalt splat;
    the runtime only switches off what it names. What must never be in it:
    a bridge, an overpass or a tunnel (they are road), the sky dome, and a
    list that no longer matches the generator's own output format.
    """
    print("[27] Road clearing")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    code = read("Revival.RoadClear.cs")
    data = read("Revival.RoadClearData.cs")
    plugin = read("RevivalPlugin.cs")
    if not code or not data:
        bad("Road clearing: Revival.RoadClear.cs or Revival.RoadClearData.cs missing")
        return
    if "RoadClear.Install(gameObject, Config)" in plugin:
        ok("RevivalPlugin installs RoadClear")
    else:
        bad("Road clearing: RevivalPlugin.cs no longer calls RoadClear.Install")
    if "Generated by research/roadclear.py -emit" in data:
        ok("Revival.RoadClearData.cs is the generator's output")
    else:
        bad("Road clearing: Revival.RoadClearData.cs was not written by research/roadclear.py -emit")
    rows = re.findall(r'^    "([^"]*)",$', data, re.M)
    forbidden = re.compile(r"bridge|birdge|overpass|tunnel|tonel|railroad|sky ?dome", re.I)
    wrong = [r for r in rows if forbidden.search(r.split("\\t")[1] if "\\t" in r else r)]
    malformed = [r for r in rows if r.count("\\t") != 4]
    if rows and not wrong and not malformed:
        ok("%d road objects listed, none a bridge, tunnel or sky" % len(rows))
    else:
        bad("Road clearing: %d rows, %d protected objects, %d malformed"
            % (len(rows), len(wrong), len(malformed)))
    trees = len(re.findall(r"\d\.\d{3}f,-?\d+\.\d{3}f", data))
    if "new TreeSet(" in data and trees:
        ok("%d road trees listed" % trees)
    else:
        warn("Road clearing: no road trees listed")


def check_editor_heights():
    """[26] Editor terrain heights are read row = z (REVERSE_ENGINEERING.md 42.1).

    Unity serialises TerrainData.m_Heights x-major. Read row = x, every editor
    height came from the point mirrored across x = z and route R5 lay 77 m
    under its road. The static half holds the transpose in both readers and
    the stamp on the served road network; with the game installed, the
    in-game heights themselves are compared (research/terrain_height_check.py
    adds all three maps and every network point).
    """
    print("[26] Editor terrain heights")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Editor heights: " + why)

    need(".reshape(side, side).T" in read(os.path.join("research", "terrainmap.py")),
         "terrainmap.py transposes m_Heights to row = z",
         "research/terrainmap.py reads m_Heights without the transpose - every "
         "height is the one mirrored across x = z")
    need(".reshape(1025, 1025).T" in read("routedraft.py"),
         "routedraft.py transposes m_Heights to row = z",
         "routedraft.py reads m_Heights without the transpose")
    need('HEIGHT_ORDER = "zx"' in read("roadnet.py")
         and '"heightOrder": HEIGHT_ORDER' in read("roadnet.py"),
         "roadnet.py stamps the networks it writes",
         "roadnet.py no longer stamps heightOrder - stale networks go unnoticed")
    sample = read(os.path.join("assets", "editor", "roadnet_sample.json"))
    need('"heightOrder": "zx"' in sample,
         "the served road network carries fixed-read heights",
         "assets/editor/roadnet_sample.json predates the height fix - "
         "python roadnet.py -reheight")
    if os.path.isdir(os.path.join(ROOT, "research")):
        need(os.path.exists(os.path.join(ROOT, "research", "terrain_height_check.py")),
             "research/terrain_height_check.py liegt vor",
             "research/terrain_height_check.py fehlt")

    # The heights the game itself reported (6.43.0 log; RE 37 teleport). Only
    # with the game and UnityPy present; the three on x = z agree either way.
    known = [(-1245.3, 1916.9, 518.0), (-1268.0, 1932.0, 519.0),
             (-1371.6, 1814.5, 530.4), (-1530.0, -1515.0, 496.7),
             (-1575.0, -1515.0, 496.5), (-1620.0, -1515.0, 495.9),
             (-513.0, -413.0, 497.8)]
    if not GAME:
        warn("Editor heights: no game installed - in-game heights not compared")
        return
    try:
        sys.path.insert(0, os.path.join(ROOT, "research"))
        import terrainmap
        terrain = terrainmap.terrain("GW_Scene_1")
    except Exception as ex:
        warn("Editor heights: GW_Scene_1 terrain unreadable (%s) - in-game "
             "heights not compared" % type(ex).__name__)
        return
    worst = max(abs(terrain.ground(x, z) - y) for x, z, y in known)
    need(worst <= 1.0,
         "%d in-game heights match ground(x, z) (largest gap %.2f m)"
         % (len(known), worst),
         "ground(x, z) is %.1f m off a height the game reported" % worst)


def check_flak():
    """[34] East airfield flak: 52-K 85 mm guns on the AA positions (Revival.Flak.cs).

    Pinned statically (docs/ai/tasks/airfield-flak-p4.md, N6 k52-flak.md): the file is plain
    ASCII and wired into the plugin, the camera and the public package; the
    rounds, damage and puffs go through the Gepard's code paths; the crew keys
    carry the slash GroundEnemies needs; every effect and animation has its
    switch; the numbers are real ones in world units (1 m = 2.8 u) and the
    gun is less accurate than the Gepard; the event code is free; the P5/P6a
    API is there. How it looks and feels is the in-game checklist.
    """
    import re
    print("[34] East airfield flak (52-K 85 mm)")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Flak: " + why)

    path = os.path.join(ROOT, "Revival.Flak.cs")
    if not os.path.exists(path):
        bad("Flak: Revival.Flak.cs missing")
        return
    raw = open(path, "rb").read()
    need(not raw.startswith(b"\xef\xbb\xbf") and all(b < 128 for b in raw),
         "Revival.Flak.cs is ASCII without a BOM", "Revival.Flak.cs is not plain ASCII")
    s = raw.decode("ascii", "replace")
    plug, cam, gep = read("RevivalPlugin.cs"), read("Revival.CameraTurret.cs"), read("RevivalGepard.cs")
    sync = read("sync_public.py")

    for seam in ("Flak.BindConfig(Config);", "Flak.Install(_harmony);", "Flak.Tick();",
                 "Flak.LateFrame();", "Flak.Draw();"):
        need(seam in plug, "seam " + seam + " in RevivalPlugin.cs", "seam missing in RevivalPlugin.cs: " + seam)
    need("public const int Flak = 8;" in cam and "NextDayRevival.Flak.LateTick();" in cam,
         "the sight camera is an owner of CameraOwner (8)",
         "the flak sight is not dispatched by CameraOwner")
    need('"Revival.Flak.cs"' in sync, "Revival.Flak.cs goes to the public repository",
         "sync_public.py does not ship Revival.Flak.cs - the public repo would not build")

    # the Gepard's code paths
    need("GepardShots.Fire(Spec()" in s and "internal sealed class Spec" in gep
         and "spec.Gravity" in gep,
         "rounds fly in the Gepard's round loop with the 52-K's ballistics",
         "the flak does not fire through GepardShots with its own Spec")
    need("GepardGun.Hit(c, at, dir, true, spec.HeliHits)" in gep and "GepardGun.Nearest(" in gep
         and "GepardFx.Flak(at, spec.PuffScale)" in gep and "_spec.PuffScale = PuffScale;" in s,
         "direct hits and timed bursts damage through GepardGun.Hit, the puff is GepardFx.Flak at the 85 mm scale",
         "the burst does not reach the Gepard's damage path or the puff effect")
    need("GepardNet.EnsureHooked();" in s,
         "a helicopter kill travels on the Gepard's event even with the Gepard off",
         "GepardNet is not hooked by the flak - kills stay local")

    # crew
    need('KeyPrefix = "flak/"' in s and 'KeyGunner = "/g"' in s and 'KeyLoader = "/c"' in s
         and "Crew.DropGroundSquad(" in s,
         "crew keys flak/<id>/g and /c (the slash keeps GroundEnemies off them)",
         "crew keys missing or without the slash")
    need("AirfieldOwnership.CrewSide" in s and "MilitaryTown.Faction()" in s and "TechnicalCrew.Sitzen(ai, clip)" in s,
         "the crew follows the persistent holder or town faction and sits with the game's clip",
         "crew faction or seated clip missing")
    need("friendly = side != null && pilot == side;" in s and "if (friendly || !hostile) continue;" in s
         and "Airborne(c)" in s and "AirPilot.Faction(c)" in s,
         "pilot faction IFF excludes friendly/unknown aircraft, air targets only",
         "the own-faction or airborne filter is missing")
    need("_manned" in s and "CameraOwner.Request(CameraOwner.Flak" in s
         and "Flak.Up(g.Gunner) || Flak.Up(g.Loader)" in s,
         "a player takes a gun over only when its crew is dead",
         "player takeover or its dead-crew condition is missing")

    # switches
    for key in ("Tracers", "MuzzleFlash", "BarrelRecoil", "Casings", "FlakPuffs", "GunSound",
                "BurstSound", "SeatedCrew", "MilitaryTownBattery"):
        need('cfg.Bind(S, "%s", true' % key in s, "switch [Flak] %s" % key,
             "effect/animation switch [Flak] %s missing" % key)

    # numbers ([Flak52K], N6: a section of its own so old ZU values cannot drive it)
    def num(key):
        m = re.search(r'cfg\.Bind\(G, "%s", (-?[0-9.]+)f?' % key, s)
        return float(m.group(1)) if m else None

    def gnum(key):
        m = re.search(r'"%s",\s*([0-9.]+)f?' % key, gep)
        return float(m.group(1)) if m else None

    need('const string G = "Flak52K";' in s, "the 52-K's numbers live in [Flak52K]",
         "the gun's numbers still share the ZU-23's [Flak] keys")
    need("internal const float K = 2.8f;" in s and "* K" in s,
         "metres go to world units at 2.8", "the 2.8 u/m conversion is missing")
    v, r, rr, d = num("MuzzleVelocity"), num("VisualRange"), num("RadarRange"), num("Dispersion")
    need(v is not None and 400 <= v <= 800, "muzzle velocity %s m/s (slow on purpose, the real 800 at most)" % v,
         "MuzzleVelocity is not a slow 85 mm shell (400..800 m/s)")
    need(r is not None and rr is not None and 1000 <= r < rr <= 8000,
         "visual range %s m < radar range %s m" % (r, rr),
         "VisualRange / RadarRange missing or the radar does not reach further")
    need(d is not None and gnum("Dispersion") is not None and d > gnum("Dispersion"),
         "dispersion %s mil, above the Gepard's %s" % (d, gnum("Dispersion")),
         "the 52-K is not less accurate than the Gepard")
    rpm = num("RateOfFire")
    need(rpm is not None and 5 <= rpm <= 25, "rate of fire %s shots a minute" % rpm,
         "RateOfFire is not an 85 mm gun's (5..25 a minute)")
    w, rw = num("WalkFactor"), num("RadarWalkFactor")
    need((num("InitialError") or 0) > (num("ErrorFloor") or 0) > 0 and w is not None and rw is not None
         and 0 < rw < w < 1 and (num("EvadeFactor") or 0) > 0,
         "bursts walk in (initial error > floor), faster radar-directed (%s) than by eye (%s), evading throws them off"
         % (rw, w),
         "the bracketing error model is missing, inert, or not faster with the radar")
    need(("g.Err = g.Err * Flak.Walk + Scatter(" in s or ("g.Err = g.Err * calibration.Walk + Scatter(" in s and "MercAA.Calibration(g)" in s)) and "Flak.SetRadarDirected(tier == 2);" in read("Revival.TowerRadar.cs"),
         "every shot is a bracketing step; the radar tier sets range and walk (SetRadarDirected)",
         "the bracketing step or the radar coupling is missing")
    tr, el = num("TraverseSpeed"), num("ElevationSpeed")
    need(tr is not None and el is not None and 0 < tr <= 40 and 0 < el <= 30
         and num("PitchMin") == -3 and num("PitchMax") == 82,
         "slew limits %s / %s deg/s, elevation -3..+82" % (tr, el),
         "slew limits or the elevation range are not the 52-K's")

    # the model: k52_build.py's parts at four LODs under a LODGroup, the recoil
    parts = ["k52_%s_lod%d.ndmesh" % (p, l) for p in ("base", "mount", "cradle", "barrel") for l in range(4)]
    parts += ["k52_diffuse.png", "k52_normal.png", "k52_rig.txt"]
    missing = [f for f in parts if not os.path.exists(os.path.join(ROOT, "assets", f))]
    need(not missing, "the 52-K's 16 meshes, atlas and rig are in assets/", "52-K assets missing: %s" % missing)
    bps = read("build.ps1")
    need(all('"%s"' % f in bps for f in parts), "build.ps1 installs every 52-K asset",
         "build.ps1 does not install every 52-K asset")
    need("group.SetLODs(lods);" in s and "k52_rig.txt" in s,
         "the gun is four parts at four LODs under one LODGroup, pivots from k52_rig.txt",
         "the 52-K model is not loaded with its LODs and rig")
    need("-g.Stroke * r * r * (3f - 2f * r)" in s and "g.Recoil = 1f;" in s and "const float RunOut" in s,
         "the barrel recoils its stroke and runs out again", "the barrel does not recoil")
    need("GepardFx.Muzzle(muzzle, dir);" in s and "GepardFx.Smoke(" in s and "FlakSound.Report(muzzle);" in s,
         "muzzle flash, smoke and report on every shot", "the shot's flash, smoke or report is missing")
    need('"MT-AA2a"' in s and "MilitaryTown.Faction()" in s,
         "the military town's battery fights for the town's faction",
         "the town battery or its faction is missing")
    # B2 (Kevin on 6.60.0: no visible aiming, the sight inside the model):
    # colliders from the rig, the ring's derelict ZU box off, the gunner
    # camera on the mount (shoulder / sight, never the old cradle eye), the
    # idle crew watching the sky, the shot's kick
    rig = open(os.path.join(ROOT, "assets", "k52_rig.txt"), encoding="utf-8").read() \
        if os.path.exists(os.path.join(ROOT, "assets", "k52_rig.txt")) else ""
    boxes = [l.split() for l in rig.splitlines() if l.startswith("box ")]
    need(len(boxes) >= 10 and all(len(b) == 8 and b[1] in ("base", "mount", "cradle") for b in boxes)
         and "\nshoulder " in rig and "\nsight " in rig,
         "k52_rig.txt carries %d collider boxes and the shoulder / sight marks" % len(boxes),
         "k52_rig.txt lacks the B2 colliders or camera marks (python k52_build.py --preview)")
    need("static void Colliders(Flak.Gun g, Transform[] parents)" in s and "rb.isKinematic = true;" in s
         and "static int Unblock(Transform holder)" in s,
         "the gun has colliders (kinematic compound on the mount) and the ring's derelict box is off",
         "the 52-K colliders or the derelict collider fix are missing")
    need("g.Sight.position" in s and "Clear(g, g.Shoulder != null" in s and "g.Eye.position" not in s,
         "the gunner looks over the shoulder or through the sight, clear of the gun",
         "the gunner camera still sits at the old eye inside the shield")
    need("static void Watch(Flak.Gun g)" in s and "if (Time.time - g.LastContact > 3f) Watch(g);" in s,
         "an idle crew sweeps the sky instead of parking the gun",
         "the idle 52-K stands still")

    # Q2 (after the 6.57.0 test): tracking, fair engagements, the crash, the crew
    heli, an2, crew, tech, radar, gepard = (read("Revival.PlayerHeli.cs"), read("Revival.PlayerAn2.cs"),
        read("RevivalGepardCrew.cs"), read("RevivalTechnicalCrew.cs"), read("Revival.TowerRadar.cs"),
        read("RevivalGepard.cs"))
    need("g.WantYawRate + Mathf.Clamp(dy * 4f, -stop, stop)" in s,
         "the mount tracks with feed-forward inside the slew limits",
         "the slew feed-forward is missing")
    # N6: a wounded man is not at his post (the radar operator stayed "manned")
    need("!NpcWar.GroundDowned(ai)" in s and "internal static bool GroundDowned(" in read("Revival.NpcCombat.cs")
         and "Flak.Up(_man)" in radar,
         "a wounded crewman or radar operator counts as gone (the console is free)",
         "the wounded state still counts as manned")
    need("internal static Vector3 Offset(" in s and "g.Err = Offset(" in s,
         "the first burst of an engagement is always visibly off the target",
         "the first burst can land on the target by chance")
    need("FlakFire.Offset(h.AirTarget" in crew and "GepardShots.FireTimed(h.Rig" in crew
         and "internal static void FireTimed(" in gepard and "CfgAirWalk" in crew,
         "the NPC Gepard's air bursts walk in as timed puffs too",
         "the NPC Gepard still fires at aircraft with a perfect radar solution")
    mi = heli[heli.find("internal static void MissileImpact("):]
    mi = mi[:mi.find(chr(10) + "        }" + chr(10))]
    need("Abandon(go, _vel, true);" in mi and "Airborne(go)" in mi and "static void ShotDownFallback()" in heli
         and "if (go == null || Falling(go) || Burning(go)) return;" in heli,
         "a helicopter shot down in the air falls with its crew aboard (no ejection, no teleport)",
         "a helicopter shot down in the air is still burnt in place and its crew thrown out")
    need("internal static void ShotDown(GameObject go, Vector3 where)" in an2 and '"ShotDown"' in crew
         and "if (f.Length == 5)" in an2,
         "an An-2 shot down in the air falls with its crew aboard",
         "the An-2 kill still burns the aeroplane in place")
    need("internal static void Unbewaffnet(Component ai)" in tech and "TechnicalCrew.Unbewaffnet(ai);" in s
         and "TechnicalCrew.Unbewaffnet(ai);" in radar,
         "the flak crews and the radar operator sit without a weapon in their hands",
         "a seated AA crewman or the radar operator still holds a weapon")

    # the wire
    m = re.search(r'"NetworkEventCode", ([0-9]+)', s)
    taken = set([150, 151, 152, 153, 154, 155, 160, 161, 162, 164, 170, 171, 172, 173, 174, 175,
                 176, 177, 178, 179, 180, 181, 182, 183, 184, 185, 190, 191, 192, 193, 194, 195, 196])
    need(m is not None and int(m.group(1)) not in taken and int(m.group(1)) < 200,
         "event code %s is free" % (m.group(1) if m else "?"),
         "the flak's event code overlaps another channel")

    # the API
    for api in ("public static List<FlakGunInfo> Guns()", "public static bool AssignTarget(",
                "public static bool WeaponsFree(", "public static bool HoldFire(",
                "public static bool ZoneDefence(", "public enum FlakState", "public static bool Authority"):
        need(api in s, "API: " + api.split("(")[0].replace("public static ", ""),
             "API member missing: " + api)

    # W-AA1: execute production ownership/IFF code, including master migration.
    import subprocess
    aa = read("Revival.AirDefence.cs")
    need('"Revival.AirDefence.cs"' in read("sync_public.py") and bool(aa)
         and all(ord(c) < 128 for c in aa),
         "AA ownership module is ASCII and ships in the public source package",
         "AA ownership module missing, non-ASCII, or absent from public sources")
    try:
        result = subprocess.run([sys.executable, os.path.join(ROOT, "research", "aa_ownership_check.py")],
                                capture_output=True, text=True, errors="replace", timeout=30)
        need(result.returncode == 0,
             "production AA ownership/IFF matrix, pilot leases and master authority PASS",
             "offline ownership check failed: " + (result.stderr or result.stdout)[-1600:])
    except (OSError, subprocess.TimeoutExpired) as ex:
        bad("Flak: offline ownership check could not run: " + str(ex))


def check_tower_radar():
    """[35] East airfield P5: the tower C1 as air defence HQ (Revival.TowerRadar.cs).

    Pinned statically (docs/ai/tasks/airfield-radar-p5.md): the file is plain
    ASCII and wired into the plugin and the public package; the radar is a
    building-kit model (unity/EastTile/Tools/c1_radar.py, built here into a
    temporary folder: its turning parts lie under "Head/") placed in the c1
    bundle's recipe; its measures are a P-18's in metres x 2.8; every effect
    and animation has its switch; fire control goes through the P4 flak API
    (AssignTarget / WeaponsFree / HoldFire / AssignedOnly, SetFireDirection)
    with an operator better and a destroyed HQ worse than the crews by eye;
    the operator key carries GroundEnemies' slash; the event code is free.
    How it looks and feels is the in-game checklist.
    """
    import json as _json
    import shutil
    import subprocess
    from research.sandbox_temp import mkdtemp
    print("[35] East airfield tower radar HQ (P5)")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("TowerRadar: " + why)

    path = os.path.join(ROOT, "Revival.TowerRadar.cs")
    if not os.path.exists(path):
        bad("TowerRadar: Revival.TowerRadar.cs missing")
        return
    raw = open(path, "rb").read()
    need(not raw.startswith(b"\xef\xbb\xbf") and all(b < 128 for b in raw),
         "Revival.TowerRadar.cs is ASCII without a BOM", "Revival.TowerRadar.cs is not plain ASCII")
    s = raw.decode("ascii", "replace")
    plug, flak, sync = read("RevivalPlugin.cs"), read("Revival.Flak.cs"), read("sync_public.py")
    for seam in ("TowerRadar.BindConfig(Config);", "TowerRadar.Install(_harmony);", "TowerRadar.Tick();",
                 "TowerRadar.LateFrame();", "TowerRadar.Draw();"):
        need(seam in plug, "seam " + seam + " in RevivalPlugin.cs", "seam missing in RevivalPlugin.cs: " + seam)
    need('"Revival.TowerRadar.cs"' in sync, "Revival.TowerRadar.cs goes to the public repository",
         "sync_public.py does not ship Revival.TowerRadar.cs - the public repo would not build")

    # the kit radar
    tool = os.path.join(ROOT, "unity", "EastTile", "Tools", "c1_radar.py")
    recipe = read(os.path.join("unity", "EastTile", "Content", "east_af_c1.json"))
    rebuild = read("rebuild_east.ps1")
    need(os.path.exists(tool), "kit radar source Tools/c1_radar.py", "unity/EastTile/Tools/c1_radar.py missing")
    need('"C1 radar"' in recipe and "Build/c1r/c1r_scene.json" in recipe,
         "the c1 bundle's recipe places the kit radar \"C1 radar\"",
         "east_af_c1.json does not place \"C1 radar\" (rerun airfield_assembly.py)")
    need('"c1_radar.py"' in rebuild, "rebuild_east.ps1 builds the kit radar before the bundles",
         "rebuild_east.ps1 does not run c1_radar.py - the bundle build would miss its scene file")
    t = read(os.path.join("unity", "EastTile", "Tools", "c1_radar.py"))

    def tnum(key):
        m = re.search(r"^%s = ([0-9.]+)" % key, t, re.M)
        return float(m.group(1)) if m else None
    ay, aw, boom, rpm = tnum("ARRAY_Y"), tnum("ARRAY_W"), tnum("YAGI_BOOM"), tnum("RPM")
    need(ay is not None and 6.0 <= ay <= 12.0 and aw is not None and 7.0 <= aw <= 11.0
         and boom is not None and 2.5 <= boom <= 4.5,
         "P-18 measures: array centre %s m, %s m wide, Yagi booms %s m" % (ay, aw, boom),
         "the radar's measures are not a P-18's (array 6-12 m up, 7-11 m wide, booms 2.5-4.5 m)")
    need(rpm is not None and 2 <= rpm <= 6 and "M = bk.M" in t, "turns at %s rpm, metres x 2.8 through bldg_kit" % rpm,
         "the radar's rpm or the kit's metre scale is off")
    if os.path.exists(tool):
        out = mkdtemp(prefix="c1r_")
        try:
            r = subprocess.run([sys.executable, tool, "--out", out], capture_output=True, text=True, timeout=300)
            scene = os.path.join(out, "c1r_scene.json")
            if r.returncode != 0 or not os.path.exists(scene):
                tail = (r.stderr or r.stdout or "").strip().splitlines()[-1:] or ["no output"]
                if "No module named" in " ".join(tail):
                    warn("TowerRadar: kit build skipped here: " + tail[0])
                else:
                    bad("TowerRadar: the kit build c1_radar.py failed: " + tail[0])
            else:
                sc = _json.load(open(scene))
                head = [x for x in sc["renderers"] if x["path"] == "Head"]
                names = set(x["name"] for x in head)
                need({"Frame", "Booms", "Elements"} <= names
                     and all(x["path"] in ("Head", "Static") for x in sc["renderers"]),
                     "kit build: %d renderers, the turning ones under Head (%s)"
                     % (len(sc["renderers"]), ", ".join(sorted(names))),
                     "the kit radar's turning parts are not under Head/ (TowerRadar turns \"C1 radar/Head\")")
                need(len(sc["boxes"]) >= 4, "kit build: %d box colliders" % len(sc["boxes"]),
                     "the kit radar has no colliders on trailer and shelter")
        except Exception as ex:
            warn("TowerRadar: kit build not run: %s" % ex)
        finally:
            shutil.rmtree(out, ignore_errors=True)

    # the runtime
    need('RadarName = "C1 radar"' in s and 'Find("Head")' in s and "RadarModel.Build(" in s,
         "the game turns the kit radar's Head, a primitive twin without the rebuilt bundle",
         "the kit radar is not found by name or has no fallback")
    need("internal const float K = 2.8f;" in s and "* K" in s, "metres go to world units at 2.8",
         "the 2.8 u/m conversion is missing")
    for key in ("AntennaRotation", "SweepLine", "BlipFade", "ScreenGlow", "Siren", "SirenNpcAlarm", "ContactLog",
                "RunwayLights", "SeatedOperator", "DamageVisuals", "NpcOperator"):
        need('cfg.Bind(S, "%s", true' % key in s, "switch [TowerRadar] %s" % key,
             "effect/animation switch [TowerRadar] %s missing" % key)
    for api in ("public static bool AssignedOnly(", "public static void SetFireDirection(", "AssignedOnly = 3,"):
        need(api in flak, "flak API: " + api.split("(")[0].replace("public static ", ""),
             "the P4 flak lacks " + api)
    need("g.Mode == FlakMode.AssignedOnly && c.Go != g.Assigned" in flak
         and (("* Flak.DirReaction" in flak and "* Flak.DirError" in flak) or ("c.InitialMil *= Flak.DirError" in read("Revival.MercAA.cs") and "c.Reaction *= Flak.DirReaction" in read("Revival.MercAA.cs")))
         and "* Flak.DirTracking" in flak,
         "the crews hold all but the assigned target and take the direction scales",
         "AssignedOnly or the direction scales do not reach the flak's fire control")
    # Q3: orders go gun by gun to those that follow the commanding side
    for call in ("Flak.AssignTarget(ids[i], c.Go)", "Flak.WeaponsFree(ids[i])", "Flak.HoldFire(ids[i])",
                 "Flak.AssignedOnly(ids[i])"):
        need(call in s, "console command " + call, "the console does not command the guns: " + call + " missing")
    need(("return side == HomeSide() || !NpcOperatorUp;" in s or "return merc != null ? side == merc.Side : side == HomeSide() || !NpcOperatorUp;" in s)
         and "if (!g.PlayerManned && g.MercActor >= 0) return side == g.CrewSide;" in s
         and "AirDefencePolicy.Follows(g.OwnerSide, side" in s
         and "TowerRadar.ControlSide, AirfieldOwnership.Holder" in s and "Flak.SightOrder = SightLine;" in s,
         "radar takeover: living HQ locks it; merc crews obey their side, other guns their owner (W AA1); holder synced",
         "the radar takeover (Q3) is incomplete: lock, per-gun follow, ControlSide sync or sight line missing")

    def num(key):
        m = re.search(r'cfg\.Bind\(S, "%s", (-?[0-9.]+)f?' % key, s)
        return float(m.group(1)) if m else None
    dr, de, dt = num("DirectedReaction"), num("DirectedError"), num("DirectedTracking")
    xr, xe, xt = num("DestroyedReaction"), num("DestroyedError"), num("DestroyedTracking")
    need(None not in (dr, de, dt, xr, xe, xt) and dr < 1 < xr and de < 1 < xe and dt > 1 > xt,
         "with an operator faster and closer (%s/%s/%s), destroyed slower and wider (%s/%s/%s)" % (dr, de, dt, xr, xe, xt),
         "the operator must make the guns better and a destroyed HQ worse than by eye")
    need("Obeyed(" in s and "return AirfieldOwnership.Holder;" in s and "AirfieldOwnership.MasterSender(sender)" in s,
         "commands use holder authority and clients accept state only from the master", "command authority is missing")
    need('Key = "radar/c1/op"' in s and "Crew.DropGroundSquad(" in s,
         "NPC operator key radar/c1/op (the slash keeps GroundEnemies off him)",
         "operator key missing or without the slash")
    need("RadarHits" in s and "ConsoleHits" in s and "FireOneShot" in s and "NetworkVisualizeExplode" in s
         and "!TowerRadar.Working" in s,
         "the radar and the console take rounds and explosions; destroyed = scope dark",
         "the HQ cannot be destroyed or the scope does not go dark")
    m = re.search(r'"NetworkEventCode", ([0-9]+)', s)
    fm = re.search(r'"NetworkEventCode", ([0-9]+)', flak)
    taken = set([150, 151, 152, 153, 154, 155, 156, 157, 160, 161, 162, 164, 170, 171, 172, 173, 174, 175,
                 176, 177, 178, 179, 180, 181, 182, 183, 184, 185, 190, 191, 192, 193, 194, 195, 196])
    if fm:
        taken.add(int(fm.group(1)))
    need(m is not None and int(m.group(1)) not in taken and int(m.group(1)) < 200,
         "event code %s is free" % (m.group(1) if m else "?"),
         "the tower radar's event code overlaps another channel")

    # B8b: the siren is a mechanical motor siren from assets/ndr_siren.wav
    # (research/siren_synth.py), spin-up / wail loop / spin-down by its smpl
    # loop, for the tower and the air events alike; the old synth is the fallback.
    need("internal sealed class SirenVoice" in s and 'FileName = "ndr_siren.wav"' in s
         and "PlayScheduled(" in s and "SetScheduledEndTime(" in s and "_loop = RadarSiren.Clip();" in s
         and 'SirenVoice.Make("NDR tower siren"' in s
         and 'SirenVoice.Make("NDR air raid siren (air event)"' in read("Revival.AirEvents.cs"),
         "siren: SirenVoice plays assets/ndr_siren.wav (tower + air events), synth fallback",
         "the siren does not go through SirenVoice / ndr_siren.wav")
    need('"ndr_siren.wav"' in read("build.ps1"), "siren: build.ps1 installs ndr_siren.wav",
         "build.ps1 does not install assets/ndr_siren.wav")
    synth = read(os.path.join("research", "siren_synth.py"))
    need("def town_ir(" in synth and "fft_convolve(" in synth and "PORTS2" in synth and "def check(" in synth,
         "siren: research/siren_synth.py (rotor/stator harmonics, town convolution, --check)",
         "research/siren_synth.py is missing or incomplete")
    wav = os.path.join(ROOT, "assets", "ndr_siren.wav")
    raw = open(wav, "rb").read() if os.path.exists(wav) else b""
    # A recording may replace it: any PCM/float WAV the game reads; a loop,
    # when there is one, lies inside the data.
    loop, fmt, size_data, pos = None, None, 0, 12
    while raw[:4] == b"RIFF" and raw[8:12] == b"WAVE" and pos + 8 <= len(raw):
        cid, size = raw[pos:pos + 4], struct.unpack("<I", raw[pos + 4:pos + 8])[0]
        if cid == b"fmt " and size >= 16:
            fmt = struct.unpack("<HHIIHH", raw[pos + 8:pos + 24])
        elif cid == b"data":
            size_data = size
        elif cid == b"smpl" and size >= 60 and struct.unpack("<I", raw[pos + 36:pos + 40])[0] > 0:
            loop = struct.unpack("<II", raw[pos + 8 + 44:pos + 8 + 52])
        pos += 8 + size + (size & 1)
    frames = size_data // (fmt[1] * fmt[5] // 8) if fmt and fmt[1] and fmt[5] >= 8 else 0
    need(fmt is not None and fmt[0] in (1, 3, 0xFFFE) and frames > 0
         and (loop is None or 0 <= loop[0] < loop[1] < frames),
         "siren: ndr_siren.wav readable, %d samples, %s" % (frames, "smpl loop %d..%d" % loop if loop
                                                              else "no loop (played whole)"),
         "assets/ndr_siren.wav missing, not a PCM/float WAV, or its smpl loop lies outside the data "
         "(python research/siren_synth.py)")

    # W-Tower1: the held radar's air picture on the map and its early warning
    # (docs/ai/tasks/w-tower1-airpicture.md) - production rules, simulation,
    # allocation benchmark and the draw path's seams (research/air_picture_check.py).
    import subprocess
    try:
        result = subprocess.run([sys.executable, os.path.join(ROOT, "research", "air_picture_check.py")],
                                capture_output=True, text=True, errors="replace", timeout=60)
        need(result.returncode == 0,
             "air picture: holder/manned rule, IFF, intercept and raid ETA, F6 slots, allocation-free draw PASS",
             "offline air picture check failed: " + (result.stderr or result.stdout)[-1600:])
    except (OSError, subprocess.TimeoutExpired) as ex:
        bad("TowerRadar: offline air picture check could not run: " + str(ex))


def check_retake_raids():
    """[W4] Retake raids (W Tower 4, Revival.RetakeRaids.cs + the pure core
    Revival.RetakeRaidsCore.cs, airdef.py "retakeRaids", editor/air.js,
    docs/ai/tasks/w-tower4-retake-raids.md).

    research/retake_raids_check.py compiles the production core unchanged
    with csc 3.5 and simulates the hold clock, the composition and a six-hour
    hold (rhythm rises, troops bounded, no allocation), round-trips the
    editor row into the plugin parser and pins the seams.
    """
    print("[W4] Retake raids (the garrison strikes back at a held airfield)")
    import subprocess
    check = os.path.join(ROOT, "research", "retake_raids_check.py")
    if not os.path.exists(check):
        bad("research/retake_raids_check.py missing")
        return
    r = subprocess.run([sys.executable, check], cwd=ROOT, capture_output=True, text=True)
    if r.returncode == 0 and r.stdout.strip().endswith("RESULT: PASS"):
        ok("retake raids pass research/retake_raids_check.py")
    else:
        bad("research/retake_raids_check.py fails: "
            + "; ".join(l.strip() for l in r.stdout.splitlines() if "FAIL" in l)[-400:]
            + r.stderr.strip()[-200:])


def check_tower_roof():
    """[Y B1] Tower roof (Revival.TowerRoof.cs + the pure core
    Revival.TowerRoofCore.cs, docs/ai/tasks/y-b1-tower-roof.md).

    research/tower_roof_check.py compiles the production core with csc 3.5,
    simulates mercs onto the roof and down (walk, climb, post), checks the
    ladder, the game ladder points, the sandbag posts and the NPC's climb path
    against the C1 collider boxes, and pins the seams.
    """
    print("[Y B1] Tower roof (outside ladder, mercs' climb, roof posts)")
    import subprocess
    check = os.path.join(ROOT, "research", "tower_roof_check.py")
    if not os.path.exists(check):
        bad("research/tower_roof_check.py missing")
        return
    r = subprocess.run([sys.executable, check], cwd=ROOT, capture_output=True, text=True)
    last = r.stdout.strip().splitlines()[-1] if r.stdout.strip() else ""
    if r.returncode == 0 and last.endswith(", 0 FAIL"):
        ok("tower roof passes research/tower_roof_check.py")
    else:
        bad("research/tower_roof_check.py fails: "
            + "; ".join(l.strip() for l in r.stdout.splitlines() if "FAIL" in l)[-400:]
            + r.stderr.strip()[-200:])


def check_tower_npc_nav():
    """[H T2] Tower NPCs (Revival.RadarSeatCore.cs, RadarOperator.Hold in
    Revival.TowerRadar.cs, Revival.TowerRoofCore.cs; docs/ai/tasks/
    h-t2-tower-npc-nav.md).

    research/h_t2_tower_nav_check.py compiles the pure cores with csc 3.5:
    every start to every upper-level post (roof posts, console seat, ladder
    posts) by explicit waypoints, clear of the cab furniture and catwalk
    rails; the radar operator's root on its seat anchor over time and on
    the floor once he is down.
    """
    print("[H T2] Tower NPCs (operator seat anchor, upper-level waypoints)")
    import subprocess
    check = os.path.join(ROOT, "research", "h_t2_tower_nav_check.py")
    if not os.path.exists(check):
        bad("research/h_t2_tower_nav_check.py missing")
        return
    r = subprocess.run([sys.executable, check], cwd=ROOT, capture_output=True, text=True)
    if r.returncode == 0 and r.stdout.strip().endswith("RESULT: PASS"):
        ok("tower NPCs pass research/h_t2_tower_nav_check.py")
    else:
        bad("research/h_t2_tower_nav_check.py fails: "
            + "; ".join(l.strip() for l in r.stdout.splitlines() if "FAIL" in l)[-400:]
            + r.stderr.strip()[-200:])


def check_bomb_damage():
    """W bomb damage: run the production queue/RPC and falloff offline."""
    import subprocess
    path = os.path.join(ROOT, "research", "bomb_damage_check.py")
    if not os.path.exists(path):
        bad("OrdnanceBlast: research/bomb_damage_check.py missing")
        return
    proc = subprocess.run([sys.executable, path], cwd=ROOT, capture_output=True, text=True)
    if proc.returncode == 0 and "BOMB DAMAGE CHECK PASS" in proc.stdout:
        ok("OrdnanceBlast: production NPC/player RPC routing, falloff, units, authority and bounded tick PASS")
    else:
        bad("OrdnanceBlast offline check: " + (proc.stdout + proc.stderr)[-1800:])

    position = os.path.join(ROOT, "research", "blast_position_check.py")
    proc = subprocess.run([sys.executable, position], cwd=ROOT, capture_output=True, text=True)
    if proc.returncode == 0 and "BLAST POSITION CHECK PASS" in proc.stdout:
        ok("OrdnanceBlast: disabled-collider registry, native/AA/mortar/patrol blasts and owner RPC PASS")
    else:
        bad("Blast position offline check: " + (proc.stdout + proc.stderr)[-1800:])


def check_air_events():
    """Editor air events (task N11, Revival.AirEvents.cs, airdef.py,
    editor/air.js, docs/ai/tasks/n11-air-events.md).

    Pinned statically: the plugin file is wired (plugin, An-2 carrier, live
    channel, admin panel, public package), the editor channel exists end to
    end, editor and plugin agree on the columns and the limits, the default
    airfield raid is valid and shipped, the carpet is realistic (40-60
    FAB-250 over 800-1000 m by default), damage is swept once on the master,
    the bursts are pooled, safe zones are held, and the Tu-95 assets ship.
    How it looks and plays is the in-game list.
    """
    import re
    print("[N11] Air events (Tu-95 bombers, An-2 paratroopers)")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("AirEvents: " + why)

    path = os.path.join(ROOT, "Revival.AirEvents.cs")
    if not os.path.exists(path):
        bad("AirEvents: Revival.AirEvents.cs missing")
        return
    raw = open(path, "rb").read()
    need(not raw.startswith(b"\xef\xbb\xbf"), "Revival.AirEvents.cs has no BOM",
         "Revival.AirEvents.cs starts with a BOM")
    src = raw.decode("utf-8", "replace")
    plug, an2, live = read("RevivalPlugin.cs"), read("Revival.PlayerAn2.cs"), read("Revival.LiveRoutes.cs")
    admin, sync, build = read("Revival.Admin.cs"), read("sync_public.py"), read("build.ps1")
    need("AirEvents.BindConfig(Config);" in plug and "AirEvents.Tick();" in plug and "AirEvents.Draw();" in plug,
         "bound, ticked and drawn by the plugin", "RevivalPlugin.cs does not bind/tick/draw AirEvents")
    need("AirEvents.Prepared(go, data);" in an2 and "AirEvents.Burned(go);" in an2
         and "AirEvents.WreckSeconds(go," in an2,
         "the An-2 carrier builds the Tu-95, its wreck and its hold",
         "Revival.PlayerAn2.cs lacks the Tu-95 seams (Prepared / Burned / WreckSeconds)")
    need('"/runtime/air"' in live and "AirEvents.Parse(lines);" in live and "AirEvents.Load(true);" in live,
         "the live channel /runtime/air is validated and applied",
         "Revival.LiveRoutes.cs does not fetch/validate /runtime/air")
    need("AirEvents.NowAtMe()" in admin and "AirEvents.Ask(_target)" in admin and "AirEvents.Options();" in admin,
         "admin panel: trigger now at my position, map right-click, options",
         "Revival.Admin.cs lacks the air event button or the map right-click")
    need('"Revival.AirEvents.cs"' in sync, "the public package ships Revival.AirEvents.cs",
         "sync_public.py does not ship Revival.AirEvents.cs - the public repo would not build")

    # Photon channel: its default is no other channel's default.
    codes = {}
    for name in os.listdir(ROOT):
        if name.endswith(".cs"):
            for m in re.finditer(r'"NetworkEventCode",\s*(\d+)', read(name)):
                codes.setdefault(m.group(1), []).append(name)
    need(codes.get("163") == ["Revival.AirEvents.cs"], "event code 163 is its own",
         "Photon event 163 is shared: %s" % codes.get("163"))

    # Numbers: a real Tu-95 stick, the warning, damage once, pooled bursts.
    need("WarnSeconds = 60f" in src, "the siren sounds ~60 s before the first aircraft",
         "the air raid warning is not 60 s")
    ordnance = read("Revival.OrdnanceBlast.cs")
    master_blast = ("if (!master) return;" in src and "OrdnanceBlast.Enqueue(at, radius," in src
                    and "if (!_ready || !_master()) return;" in ordnance
                    and '"PlayerApplyDamage", victim,' in ordnance
                    and "b.FactionShield = !anyFaction" in ordnance
                    and "b.FactionShield = false" in ordnance)
    need(master_blast or ("if (!master) return;" in src and "Mortar.AnyFaction = true;" in src
         and "!AnyFaction && FactionShield.SameFactionAsLocal(go)" in read("RevivalMortar.cs")),
         "one damage sweep per impact, on the master, every player's faction",
         "bomb damage is not swept once on the master")
    need("_flash.Emit(p, 1);" in src and "_dust.Emit(p, 1);" in src and "BombPool.Take()" in src
         and "Fx.Apply(ps);" in src,
         "bursts and bomb bodies are pooled and follow the particle density",
         "the bomb bursts are not pooled")
    need(src.count("Safe(") >= 5 and "if (Safe(p)) { safe++; continue; }" in src,
         "safe zones are never bombed, dropped on or targeted",
         "a bomb can be released over a safe zone")
    need("f.Toughness = 3;" in src and "PlayerAn2.Down(plane)" in src,
         "a Tu-95 is a real, tougher target and a downed one drops nothing more",
         "a shot-down bomber still bombs, or the Tu-95 is not toughened")

    # Editor and plugin agree.
    adef = read("airdef.py")
    cols = re.search(r"TSV_COLUMNS = \[(.*?)\]", adef, re.S)
    need(cols is not None and len(re.findall(r'"[A-Za-z]+"', cols.group(1))) == 20
         and "if (c.Length < 17)" in src and "c.Length > 18" in src
         and "c.Length > 19" in src and "e.SpeedFactor = factor;" in src,
         "editor and plugin count the same columns", "airdef.py and AirEvents.Parse disagree on the columns")
    need("MIN_BOMBS, MAX_BOMBS, DEFAULT_BOMBS = 20, 60, 48" in adef
         and "Mathf.Clamp(Mathf.RoundToInt(F(p[3])), 20, 60)" in src
         and "MIN_TROOPS, MAX_TROOPS, DEFAULT_TROOPS = 1, 12, 10" in adef
         and "Mathf.Clamp(Mathf.RoundToInt(F(p[3])), 1, 12)" in src
         and "MIN_LENGTH, MAX_LENGTH = 200.0, 1500.0" in adef and "Mathf.Clamp(F(c[5]), 200f, 1500f)" in src,
         "bombs, paratroopers and line length share their limits",
         "airdef.py and the plugin allow different bombs/troops/lengths")
    try:
        import sys as _sys
        if ROOT not in _sys.path:
            _sys.path.insert(0, ROOT)
        import airdef
        import compdef
        probs = []
        airdef.validate_events({"airEvents": airdef.DEFAULT_EVENTS}, lambda w, m: probs.append(w + ": " + m))
        need(not probs, "the default airfield raid validates", "default air event invalid: %s" % probs)
        d = airdef.DEFAULT_EVENTS[0]
        waves = [w["type"] for w in d["waves"]]
        need(d["intervalMinHours"] == 2.5 and d["intervalMaxHours"] == 3.5 and waves == ["bomber", "transport"]
             and 40 <= d["waves"][0]["load"] <= 60 and 800 <= d["length"] <= 1000,
             "default: one airfield raid every 2.5-3.5 h, bomber then paratroopers, 40-60 FAB-250 over 800-1000 m",
             "the default air event is not the airfield raid every ~3 h")
        definition = compdef.load_definition()
        shipped = read(os.path.join("assets", "ndr_airevents.tsv"))
        need(definition is not None and shipped == airdef.to_airevents_tsv(definition),
             "assets/ndr_airevents.tsv is the editor's current runtime view",
             "assets/ndr_airevents.tsv is stale - save in the editor or rerun airdef.to_airevents_tsv")
    except Exception as ex:
        bad("AirEvents: airdef/compdef not importable: %s" % ex)
    comp, route = read("compdef.py"), read("routeeditor.py")
    need("airdef.validate_events(" in comp and "airdef.to_airevents_tsv(" in comp,
         "the editor save validates and derives the air events",
         "compdef.py saves air events unchecked or writes no runtime view")
    need('path == "/runtime/air"' in route and 'path == "/air.js"' in route and '"airDefaults"' in route,
         "the editor server serves the air channel and page", "routeeditor.py lacks /runtime/air or /air.js")
    ajs, app, html = read("editor/air.js"), read("editor/app.js"), read("editor/index.html")
    need('<script src="air.js"></script>' in html and "NDRAir.init();" in app and "NDRAir.draw();" in app
         and "NDRAir.mousedown(ev)" in app and "function setLine(d, a, b)" in ajs,
         "the air event page hangs in page, drawing and mouse, the target is dragged on the map",
         "editor/air.js is not hooked in, or the target cannot be dragged")

    # The Tu-95 ships.
    names = ["tu95_body.ndmesh", "tu95_glass.ndmesh", "tu95_decals.ndmesh", "tu95_prop.ndmesh",
             "tu95_bay_l.ndmesh", "tu95_bay_r.ndmesh", "tu95_wreck.ndmesh", "tu95_atlas.png",
             "tu95_rig.txt", "ndr_airevents.tsv"]
    missing = [n for n in names if not os.path.exists(os.path.join(ROOT, "assets", n)) or '"%s"' % n not in build]
    need(not missing, "the Tu-95 meshes, atlas, rig and the air table are built and installed",
         "assets missing or not in build.ps1: %s (python tu95_import.py)" % missing)
    rig = read(os.path.join("assets", "tu95_rig.txt"))
    need(len(re.findall(r"^prop \d (Front|Rear) ", rig, re.M)) == 8 and rig.count("\nbox ") >= 3,
         "the rig has eight contra-rotating props and hull boxes", "assets/tu95_rig.txt is incomplete")

    # B8a (6.60.0: nobody saw it): drawn past the far clip, built on the master
    # too, a real altitude, heard for km. research/tu95_visibility_check.py
    # proves the numbers over the heightmap; here only the seams.
    npc = read("Revival.NpcAircraft.cs")
    launch = npc[npc.find("internal static Flight Launch("):npc.find("internal static void Prepared(")]
    need("Camera.onPreCull += PreCull;" in src and "internal static float ProxyU(" in src
         and "AirEvents.Prepared(go, data);" in launch
         and 'cfg.Bind("AirEvents", "BomberAltitude"' in src and "AudioRolloffMode.Custom" in src
         and os.path.exists(os.path.join(ROOT, "research", "tu95_visibility_check.py")),
         "B8a: the Tu-95 is drawn along the whole route (proxy), built on the master, 550 m, heard for km",
         "B8a seams missing: far-draw proxy, master Prepared fallback, BomberAltitude or the drone rolloff")


def check_npc_aircraft():
    """NPC aircraft on a flight path and the admin test flyover (task N3,
    Revival.NpcAircraft.cs, docs/ai/tasks/admin-flyover.md).

    Pinned statically: the file is plain ASCII, wired into the plugin, the
    admin panel and the public package; the seams that make the NPC An-2 a
    target for every AA system are in place (hostile for the Gepard crew, the
    ZU-23 and the no-fly zones; the Stinger lists GepardAir aircraft; the
    shot-down An-2 path flies an NPC one down at once); event 159 is not the
    default of another channel. How it flies and falls is the in-game list.
    """
    import re
    print("[N3] NPC aircraft / test flyover")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("NpcAircraft: " + why)

    path = os.path.join(ROOT, "Revival.NpcAircraft.cs")
    if not os.path.exists(path):
        bad("NpcAircraft: Revival.NpcAircraft.cs missing")
        return
    raw = open(path, "rb").read()
    need(not raw.startswith(b"\xef\xbb\xbf") and all(b < 128 for b in raw),
         "Revival.NpcAircraft.cs is ASCII without a BOM", "Revival.NpcAircraft.cs is not plain ASCII")
    src = raw.decode("ascii", "replace")
    plug, admin, an2 = read("RevivalPlugin.cs"), read("Revival.Admin.cs"), read("Revival.PlayerAn2.cs")
    gep, flak, nofly, sting = (read("RevivalGepardCrew.cs"), read("Revival.Flak.cs"),
                               read("Revival.NoFly.cs"), read("Revival.Stinger.cs"))
    for seam in ("NpcAircraft.BindConfig(Config);", "NpcAircraft.Install(_harmony);", "NpcAircraft.Tick();"):
        need(seam in plug, "seam " + seam + " in RevivalPlugin.cs", "seam missing in RevivalPlugin.cs: " + seam)
    need('"Revival.NpcAircraft.cs"' in read("sync_public.py"), "Revival.NpcAircraft.cs goes to the public repository",
         "sync_public.py does not ship Revival.NpcAircraft.cs - the public repo would not build")
    need('"Test flyover"' in admin and "Flyover.OverMe()" in admin and "Flyover.Ask(_target)" in admin,
         "admin panel: Test flyover button and the map right-click Flyover",
         "the admin panel lacks the Test flyover button or the map right-click Flyover")
    need("NpcAircraft.Prepared(go, data);" in an2 and "NpcAircraft.Is(go)" in an2
         and "NpcAircraft.Velocity(go, out vel)" in an2 and "internal static GameObject BuildNpc(" in an2,
         "PlayerAn2 prepares, excludes and shoots down NPC aeroplanes",
         "a PlayerAn2 seam for NPC aeroplanes is missing (BuildNpc / Prepared / Is / Velocity)")
    need("NpcAircraft.Hostile(c.Go)" in gep and "AirPilot.Faction(c)" in flak
         and "AirPilot.Faction(c)" in nofly and "NpcAircraft.Hostile(c.Go)" in read("Revival.AirDefence.cs"),
         "NPC aeroplanes are hostile to the Gepard crew, the ZU-23 and the no-fly zones",
         "a hostility seam (GepardCrew / Flak / NoFly) for NPC aeroplanes is missing")
    need("GepardAir.Collect(_planes)" in sting and "GepardAir.Kill(f.Target.Go, f.Position)" in sting,
         "the Stinger locks and kills registered aircraft",
         "Revival.Stinger.cs does not list GepardAir aircraft")
    need("internal static bool Kill(GameObject go, Vector3 point)" in gep, "GepardAir.Kill exists",
         "GepardAir.Kill is missing")
    need('"NetworkEventCode", 159' in src, "event code 159 by default", "the event code default is not 159")
    others = []
    for name in os.listdir(ROOT):
        if name.endswith(".cs") and name != "Revival.NpcAircraft.cs":
            text = read(name)
            others += [int(m) for m in re.findall(r'EventCode", (\d+)', text)]
    need(159 not in others, "event 159 is no other channel's default",
         "another channel defaults to event 159")


def check_nofly():
    """[35] No-fly zones (Revival.NoFly.cs, P6a).

    Pinned statically (docs/ai/tasks/nofly-zones-p6a.md): the file is plain
    ASCII and wired into the plugin and the public package; the zones and the
    map style in the plugin are the ones in assets/editor/nofly.json that the
    web editor draws (editor/nofly.js); the marker is finer than a patrol
    route; a violator is warned before it is engaged, by the zone's ZU-23s
    (the P4 API), a Gepard of the zone's faction, or scripted flak through the
    Gepard's damage path; the event code is free. How it looks and feels is
    the in-game checklist.
    """
    import json
    import re
    print("[35] No-fly zones (P6a)")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("NoFly: " + why)

    path = os.path.join(ROOT, "Revival.NoFly.cs")
    if not os.path.exists(path):
        bad("NoFly: Revival.NoFly.cs missing")
        return
    raw = open(path, "rb").read()
    need(not raw.startswith(b"\xef\xbb\xbf") and all(b < 128 for b in raw),
         "Revival.NoFly.cs is ASCII without a BOM", "Revival.NoFly.cs is not plain ASCII")
    s = raw.decode("ascii", "replace")
    plug, gep, ink = read("RevivalPlugin.cs"), read("RevivalGepardCrew.cs"), read("Revival.MapInk.cs")
    sync = read("sync_public.py")
    js, html, app, srv = read("editor/nofly.js"), read("editor/index.html"), read("editor/app.js"), read("routeeditor.py")

    for seam in ("NoFly.BindConfig(Config);", "NoFly.Tick();", "NoFly.Draw();"):
        need(seam in plug, "seam " + seam + " in RevivalPlugin.cs", "seam missing in RevivalPlugin.cs: " + seam)
    need('"Revival.NoFly.cs"' in sync, "Revival.NoFly.cs goes to the public repository",
         "sync_public.py does not ship Revival.NoFly.cs - the public repo would not build")

    # the one zone list, in both places
    try:
        data = json.load(io.open(os.path.join(ROOT, "assets", "editor", "nofly.json"), encoding="utf-8"))
    except (OSError, ValueError) as ex:
        bad("NoFly: assets/editor/nofly.json unreadable: %s" % ex)
        return
    zones = data.get("zones") or []
    need(len(zones) > 0, "%d zone(s) in assets/editor/nofly.json" % len(zones), "no zone in nofly.json")
    code = {}
    for m in re.finditer(r'(?:Zones\.Add\(|Zone \w+ = )Circle\("([^"]+)", "([^"]+)", "([^"]+)", "([^"]+)", '
                         r'(-?[0-9.]+)f, (-?[0-9.]+)f, ([0-9.]+)f, ([0-9.]+)f', s):
        code[m.group(1)] = m.groups()
    # polygon zones (P6b): Polygon("id", "name", "scene", "faction", new Vector2[] {...}, ceiling f
    poly = {}
    for m in re.finditer(r'Polygon\("([^"]+)", "([^"]+)", "([^"]+)", "([^"]+)", new Vector2\[\] \{(.*?)\}, '
                         r'([0-9.]+)f', s, re.S):
        pts = [(float(x), float(y)) for x, y in
               re.findall(r'new Vector2\((-?[0-9.]+)f, (-?[0-9.]+)f\)', m.group(5))]
        poly[m.group(1)] = (m.group(1), m.group(2), m.group(3), m.group(4), pts, m.group(6))
    for z in zones:
        c = code.get(z.get("id"))
        same = (c is not None and z.get("shape") == "circle" and c[1] == z.get("name")
                and c[2] == z.get("scene") and c[3] == z.get("faction")
                and abs(float(c[4]) - z["x"]) < 0.01 and abs(float(c[5]) - z["z"]) < 0.01
                and abs(float(c[6]) - z["radius"]) < 0.01 and abs(float(c[7]) - z["ceiling"]) < 0.01)
        q = poly.get(z.get("id"))
        if c is None and q is not None:
            zp = z.get("points") or []
            same = (z.get("shape") == "polygon" and q[1] == z.get("name") and q[2] == z.get("scene")
                    and q[3] == z.get("faction") and len(zp) == len(q[4]) >= 3
                    and all(abs(a[0] - b[0]) < 0.01 and abs(a[1] - b[1]) < 0.01 for a, b in zip(q[4], zp))
                    and abs(float(q[5]) - z["ceiling"]) < 0.01)
        need(same, "zone %s is the same in the plugin and the editor" % z.get("id"),
             "zone %s differs between Revival.NoFly.cs and assets/editor/nofly.json" % z.get("id"))
    need(len(code) + len(poly) == len(zones), "the plugin has no zone the editor lacks",
         "Revival.NoFly.cs and nofly.json list different zones")
    n12 = [z for z in zones if z.get("id") == "N12"]
    # B4: about 1.5 x the ~340 u settlement ring, a visible gap between them
    need(n12 and n12[0]["scene"] == "GW_Scene_1" and abs(n12[0]["x"] - 1446.6) < 60
         and abs(n12[0]["z"] - 1703.2) < 60 and 450 <= n12[0]["radius"] <= 600,
         "zone N12 over the neutral settlement NPC_Settlement[Neutrals] (1446.6, 1703.2)",
         "the neutral settlement's zone is missing or not over Point N12")

    # the style: B4 supersedes N04's patrol-weight ink with fine even dashes
    st = data.get("style") or {}

    def const(src, name):
        m = re.search(r'internal const float %s = ([0-9.]+)f;' % name, src)
        return float(m.group(1)) if m else None

    dash, gap, width = const(s, "DashLength"), const(s, "GapLength"), const(s, "StrokeWidth")
    need(dash == st.get("dash") and gap == st.get("gap") and width == st.get("width")
         and ('internal const string Label = "%s";' % st.get("label")) in s
         and ("internal const int LabelSize = %s;" % st.get("labelSize")) in s,
         "map style %s / %s / %s px, label %r - the same in game and editor" % (dash, gap, width, st.get("label")),
         "the map style differs between Revival.NoFly.cs and nofly.json")
    need(st.get("label") == "NO FLY ZONE", "the label reads NO FLY ZONE", "the label is not NO FLY ZONE")
    pw, pd = const(ink, "StrokeWidth"), const(ink, "DashLength")
    need(None not in (width, pw, dash, pd) and width < pw and dash < pd and gap < dash,
         "finer than a patrol route, even dashes (stroke %s vs %s, dash %s vs %s)" % (width, pw, dash, pd),
         "the no-fly line must be finer than a patrol route (B4)")
    m = re.search(r'internal const float InkAlpha = ([0-9.]+)f;', s)
    need(st.get("color") == "#ca2020" and m is not None and 0.6 <= float(m.group(1)) < 1
         and abs(float(m.group(1)) - st.get("alpha", 0)) < 1e-6
         and "202f / 255f, 32f / 255f, 32f / 255f, InkAlpha" in s
         and "Hatch" not in s and "ctx.clip()" not in js and "fillRect" not in js,
         "native target red, toned down, without hatch fill in both maps",
         "no-fly red/alpha differs or a hatch/fill came back (B4)")
    need("gui.y - StrokeWidth - 2f - size.y" in s and "var y = s[1] - st.width - 2;" in js,
         "the NO FLY ZONE label sits outside the ring's top edge in both maps",
         "the NO FLY ZONE label is not above the ring (B4)")
    need("internal static Dash Raster(List<Vector2> points, float strokeWidth)" in ink
         and "MapInk.Raster(samples, StrokeWidth)" in s and 'MapInkLayer.Begin("nofly"' in s,
         "drawn in the native map ink with its own stroke width", "the map ink path is missing")
    need("setLineDash" in js and "/api/nofly" in js and '<script src="nofly.js"></script>' in html
         and "NDRNoFly.draw()" in app and '"/api/nofly"' in srv and '"/nofly.js"' in srv
         and "5000 / 1024" in js,
         "the web editor draws the zones from nofly.json, scaled like the in-game ink",
         "the editor's no-fly layer is not wired (nofly.js, index.html, app.js, routeeditor.py)")

    # warning, then the defenders
    need("now - v.Since >= Seconds()" in s and "NO FLY ZONE: " in s
         and "defensive fire in " in s and "under defensive fire" in s and "NoFlyBeep.Play(" in s,
         "a violator is warned (banner, beep) before it is engaged",
         "the warning phase or the banner is missing")
    need("anyone && !owner" in s and "FlakFire.Airborne(c)" in s,
         "only airborne aircraft with a player aboard and nobody of the zone's faction",
         "the violator filter is missing")
    need("Flak.ZoneDefence(" in s and "Flak.AssignTarget(" in s and "Flak.ClearTarget(" in s,
         "the zone's ZU-23s go to zone defence and are assigned the violator (P4 API)",
         "the flak API is not used")
    need("GepardCrew.Defends(" in s and "NoFly.Engaged(c.Go, h.Side, h.Root.position)" in gep
         and "internal static bool Defends(" in gep,
         "a Gepard of the zone's faction takes the violator", "the Gepard hook is missing")
    # Q2 (after 6.57.0): no scripted defensive fire - zones are defended by
    # real guns only (ZU-23, Gepard), and the banner says so when none is up.
    need("NoFlyFire" not in s and "ScriptedFire" not in s and "GepardFx.Flak(" not in s
         and "GepardGun.Hit(" not in s and "static bool Defended(Zone zn)" in s
         and "no air defence answers" in s,
         "no scripted fire: a zone is defended only by its guns or a Gepard",
         "scripted defensive fire is back in Revival.NoFly.cs")


def check_military_town_ring():
    """[36] Military town outer ring and no-fly zone (P6b).

    Pinned statically (docs/ai/tasks/military-town-ring-nofly.md): the ring
    groups stand OUTSIDE the fence and close to it, beside the S3 road at its
    two checkpoints, before both breaches, and one patrol walks the whole fence
    and closes on itself; the watchtowers carry posted guards; the no-fly zone
    MT holds the whole town and the AA site, keeps a clear margin to the
    airfield, is shown only with the town and defended only by the town's
    Gepard. How it plays is the in-game checklist.
    """
    import json
    import math
    import re
    print("[36] Military town ring and no-fly zone (P6b)")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("MT ring: " + why)

    path = os.path.join(ROOT, "Revival.MilitaryTownRing.cs")
    if not os.path.exists(path):
        bad("MT ring: Revival.MilitaryTownRing.cs missing")
        return
    raw = open(path, "rb").read()
    need(not raw.startswith(b"\xef\xbb\xbf") and all(b < 128 for b in raw),
         "Revival.MilitaryTownRing.cs is ASCII without a BOM", "Revival.MilitaryTownRing.cs is not plain ASCII")
    ring = raw.decode("ascii", "replace")
    town, nofly, gep = read("Revival.MilitaryTown.cs"), read("Revival.NoFly.cs"), read("RevivalGepardCrew.cs")
    need('"Revival.MilitaryTownRing.cs"' in read("sync_public.py"), "Revival.MilitaryTownRing.cs goes to the public repository",
         "sync_public.py does not ship Revival.MilitaryTownRing.cs - the public repo would not build")
    need("internal static partial class MilitaryTown" in town and "internal static partial class MilitaryTown" in ring
         and "BindRingConfig(cfg);" in town and '"DefenceRing"' in ring and '"NoFlyZone"' in ring,
         "one MilitaryTown class over two files, [MilitaryTown] DefenceRing / NoFlyZone",
         "the ring is not part of MilitaryTown or its config keys are missing")
    need("Specs.Length + RingSpecs.Length" in town and "IsRing(p.Name) && !RingOn" in town
         and "IsRing(g.Name) ? RingHold" in town,
         "ring groups are town groups (AddGroups), switched by DefenceRing, respawn held near the fence",
         "AddGroups / HoldSpawn do not carry the ring groups")

    # --- the ring groups
    FX0, FX1, FZ0, FZ1 = 5452.0, 5848.0, 602.0, 1198.0
    AF_MAX_X = 4820.0

    def out_of_fence(x, z):
        dx = max(FX0 - x, 0.0, x - FX1)
        dz = max(FZ0 - z, 0.0, z - FZ1)
        return math.hypot(dx, dz)

    specs = re.findall(r'new Spec\("(mt-ring-[A-Za-z0-9]+)", "(\w+)", (\d+), ([\d.]+)f, "(\w+)", (\d),(.*?)\)(?=,\s*(?://[^\n]*\n\s*)*new Spec|\s*\};)',
                       ring, re.S)
    need(len(specs) >= 5, "%d ring groups" % len(specs), "fewer than 5 ring groups parsed")
    pts_of = {}
    good = True
    for s in specs:
        pts = [(float(x), float(z)) for x, z in re.findall(r'P\((-?[\d.]+)f, (-?[\d.]+)f\)', s[6])]
        pts_of[s[0]] = pts
        if not pts or (s[1] == "patrol" and len(pts) < 4):
            good = False
            bad("MT ring: %s has no point or a patrol without a route" % s[0])
        for x, z in pts:
            d = out_of_fence(x, z)
            if not (5.0 <= d <= 140.0):
                good = False
                bad("MT ring: %s point (%g, %g) is %.0f u outside the fence (want 5..140)" % (s[0], x, z, d))
            if x < AF_MAX_X + 400.0:
                good = False
                bad("MT ring: %s point (%g, %g) is near the airfield" % (s[0], x, z))
    need(good, "every ring point 5..140 u outside the fence, far from the airfield",
         "a ring point is inside the fence, too far out or near the airfield")

    s3 = []
    try:
        net = json.load(io.open(os.path.join(ROOT, "assets", "editor", "roadnet_sample.json"), encoding="utf-8"))
        for e in net.get("edges", []):
            if e.get("east") and e.get("eastRoad") == "s3":
                s3 += [(p[0], p[1]) for p in e["pts"]]
    except (OSError, ValueError):
        pass

    def road(x, z):
        return min(math.hypot(x - a, z - b) for a, b in s3) if s3 else 1e9

    names = dict((s[0], s) for s in specs)
    for cp in ("mt-ring-CPN", "mt-ring-CPS"):
        p = pts_of.get(cp, [None])[0]
        need(p is not None and names[cp][1] == "guard" and 4.0 <= road(p[0], p[1]) <= 25.0,
             "%s: a checkpoint beside the S3 road (%.0f u from its centre line)" % (cp, road(p[0], p[1]) if p else -1),
             "%s is missing or not beside the S3 road" % cp)
    cpn, cps = pts_of.get("mt-ring-CPN", [(0, 0)])[0], pts_of.get("mt-ring-CPS", [(0, 0)])[0]
    need(cpn[1] > FZ1 and cps[1] < FZ0, "the road checkpoints sit north (airfield side) and south of the town",
         "the S3 checkpoints are not on the north and south approaches")
    cpe, cpb = pts_of.get("mt-ring-CPE", [(0, 0)])[0], pts_of.get("mt-ring-CPB", [(0, 0)])[0]
    need(cpe[0] > FX1 and 700 <= cpe[1] <= 810 and cpb[1] > FZ1 and 5660 <= cpb[0] <= 5710,
         "posts before the east breach (z 745..765) and the north breach (x 5676..5690)",
         "the breach posts are missing or misplaced")
    loop = pts_of.get("mt-ring-loop", [])
    sides = set()
    for x, z in loop:
        if x < FX0:
            sides.add("W")
        if x > FX1:
            sides.add("E")
        if z < FZ0:
            sides.add("S")
        if z > FZ1:
            sides.add("N")
    closes = len(loop) > 3 and math.hypot(loop[0][0] - loop[-1][0], loop[0][1] - loop[-1][1]) < 100.0
    need(sides == set("NESW") and closes and names.get("mt-ring-loop", ("", ""))[1] == "patrol",
         "the ring patrol walks all four sides of the fence and closes on itself",
         "the ring patrol does not go round the whole fence or does not close (< 100 u)")
    crossing = False
    for (ax, az), (bx, bz) in zip(loop, loop[1:] + loop[:1]):
        for k in range(1, 20):
            t = k / 20.0
            if out_of_fence(ax + (bx - ax) * t, az + (bz - az) * t) <= 0.0:
                crossing = True
    need(not crossing, "no leg of the ring patrol cuts through the fenced town",
         "a leg of the ring patrol cuts through the town")
    tw = re.findall(r'new Post\("(mt-TW\d)", "(MT-T\d)"', town)
    need(sorted(tw) == [("mt-TW1", "MT-T1"), ("mt-TW2", "MT-T2")]
         and 'new Spec("mt-TW1", "waiting"' in town and 'new Spec("mt-TW2", "waiting"' in town,
         "a posted guard on each watchtower T1 / T2", "the watchtower guards are missing")

    # --- the no-fly zone
    zones = []
    try:
        zones = json.load(io.open(os.path.join(ROOT, "assets", "editor", "nofly.json"), encoding="utf-8")).get("zones") or []
    except (OSError, ValueError):
        pass
    mt = [z for z in zones if z.get("id") == "MT"]
    if not mt or mt[0].get("shape") not in ("polygon", "circle"):
        bad("MT ring: no zone MT in assets/editor/nofly.json")
        return
    if mt[0]["shape"] == "circle":
        # B4: a circle round the 380 u town map ring, as a 72-gon here
        cx, cz, cr = float(mt[0]["x"]), float(mt[0]["z"]), float(mt[0]["radius"])
        need(abs(cx - 5650.0) < 1 and abs(cz - 900.0) < 1 and cr >= 380.0 * 1.3,
             "zone MT stands clearly outside the town's 380 u map ring (%.0f u)" % cr,
             "zone MT is not concentric with or not clearly larger than the town ring")
        poly = [(cx + cr * math.sin(k * math.pi / 36), cz + cr * math.cos(k * math.pi / 36)) for k in range(72)]
    else:
        poly = [(float(p[0]), float(p[1])) for p in mt[0]["points"]]

    def inside(x, z):
        c = False
        for i in range(len(poly)):
            (ax, az), (bx, bz) = poly[i], poly[i - 1]
            if (az > z) != (bz > z) and x < (bx - ax) * (z - az) / (bz - az) + ax:
                c = not c
        return c
    corners = [(FX0, FZ0), (FX1, FZ0), (FX1, FZ1), (FX0, FZ1), (5482.0, 892.0)]
    need(all(inside(x, z) for x, z in corners) and mt[0].get("scene") == "GW_Scene_1",
         "zone MT holds the whole fenced town and the AA1 Gepard", "zone MT does not cover the town or AA1")
    gap = 1e9
    for i in range(len(poly)):
        (ax, az), (bx, bz) = poly[i - 1], poly[i]
        for k in range(21):
            t = k / 20.0
            x, z = ax + (bx - ax) * t, az + (bz - az) * t
            dx = max(3990.0 - x, 0.0, x - AF_MAX_X)
            dz = max(-1690.0 - z, 0.0, z - 1690.0)
            gap = min(gap, math.hypot(dx, dz))
    need(gap >= 300.0, "zone MT stays %.0f u (%.0f m) clear of the airfield fence" % (gap, gap / 2.8),
         "zone MT comes within %.0f u of the airfield (want >= 300)" % gap)
    need('mt.Reach = 0f;' in nofly
         and 'mt.Exists = MilitaryTown.NoFlyShown;' in nofly and 'mt.Armed = MilitaryTown.NoFlyArmed;' in nofly
         and 'mt.Side = MilitaryTown.Faction;' in nofly and "zn.Radius + zn.Reach" in nofly
         and "internal static bool Standing(" in gep
         and "GepardCrew.Standing(" in ring,
         "zone MT: only with the town, the garrison's faction, defended by the town's Gepard alone",
         "zone MT is not gated by the town or not tied to the AA1 Gepard")


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
    # .bin since 6.45: the crocodile's rig sidecar (crocodile_rig.bin).
    named = set(re.findall(r'"([A-Za-z0-9][A-Za-z0-9_]*\.(?:ndmesh|png|bin))"', src))
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
        if f in ("scope50.png", "t72_scope.png", "apc_scope.png",
                 "stinger_scope.png"):
            # Alle vier sind Zielfernrohrblenden: aussen deckend, in der Mitte
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


def check_apmine():
    """[11b] PMN-2 anti-personnel mine 1492: id band, donor, the no-throw
    placement, the grenade record, the Photon channel, who tests whom (every
    client its own player, the master the NPCs and vehicles), the corpse and
    single-fire guards, targeted damage through the game's gates, the seams
    and the Blender asset set. Stepping on it stays an in-game item.
    """
    print("[11b] Schuetzenmine PMN-2 (statisch)")
    p = os.path.join(ROOT, "RevivalApMine.cs")
    if not os.path.exists(p):
        bad("RevivalApMine.cs fehlt")
        return
    s = io.open(p, encoding="utf-8").read()
    plug = io.open(os.path.join(ROOT, "RevivalPlugin.cs"), encoding="utf-8").read()
    admin = io.open(os.path.join(ROOT, "Revival.Admin.cs"), encoding="utf-8").read()
    build = io.open(os.path.join(ROOT, "build.ps1"), encoding="utf-8").read()
    sync = io.open(os.path.join(ROOT, "sync_public.py"), encoding="utf-8").read()
    items = io.open(os.path.join(ROOT, "Revival.Items.cs"), encoding="utf-8").read()

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("AP-Mine: " + why)

    import re
    m = re.search(r"DEF_MINE = (\d+);", s)
    mid = int(m.group(1)) if m else -1
    need(mid == 1492, "Id 1492", "Item-Id ist nicht 1492")
    need(1401 <= mid <= 1500 and mid not in (1490, 1491),
         "Id im Granatenband 1401..1500 und frei neben 1490/1491",
         "Id ausserhalb 1401..1500 oder doppelt - dann kein Waffenslot")
    need("DEF_DONOR = 1403" in s and "DEF_MINE, DEF_DONOR, true," in s,
         "Spende 1403 mit Handmodell (IsWeapon)",
         "Spender nicht 1403 oder kein Handmodell")
    need('"apmine.ndmesh", "apmine_diffuse.png", "apmine_normal.png"' in s
         and '"apmine_icon.png"' in s and '"apmine_metal.png"' in s,
         "eigene Blender-Assets (Mesh, Albedo, Normal, Metall, Icon)",
         "die Mine nennt nicht ihre eigenen apmine_*-Dateien")
    need("ApMineThrowHook" in s and '"CantThrowGrenade"' in s
         and re.search(r"__result = true;\s+ApMine\.PlaceFromController", s) is not None,
         "Linksklick setzt statt zu werfen (Sperre vor der Platzierung)",
         "CantThrowGrenade-Postfix fehlt oder platziert vor der Sperre")
    need("ApMineDataHook" in s and '"GetGrenadeWeaponData"' in s
         and "entries.Contains(1403)" in s,
         "Granatendatensatz aus 1403 geklont",
         "kein Granatendatensatz fuer 1492 - dann nicht ausruestbar")
    need("new object[] { 2, DEF_MINE, true, false }" in s
         and "Send(OpPlace" in s
         and s.index("consumed = ConsumeEquipped(ctrl)") < s.index("Send(OpPlace"),
         "genau eine Mine verbraucht, erst dann gemeldet",
         "Verbrauch fehlt oder die Mine wird vor dem Verbrauch gemeldet")
    need("const byte EventCode = 196;" in s and '"apmine-v1"' in s
         and "Delegate.Combine" in s,
         "Photon-Kanal 196 (apmine-v1)",
         "kein eigener Ereigniskanal")
    need("Gepard.CfgEventCode" in s and "Crocodile.EventCode()" in s
         and "EventCode == 191" in s and "_cfgEventCode" in s,
         "Kanalpruefung gegen Gepard, Krokodil, Stinger, Moerser und die Plugin-Kanaele",
         "Ueberschneidungspruefung des Kanals unvollstaendig")
    need("if (owner != sender" in s,
         "nur der Leger meldet seine eigene Mine",
         "PLACE-Ereignisse werden von jedem Absender angenommen")
    need("Crocodile.Huntable(local)" in s and "if (!master) continue;" in s
         and "Crocodile.IsMaster()" in s,
         "jeder Client prueft nur seinen Spieler, der Master NPCs und Fahrzeuge",
         "Zustaendigkeit der Ausloesung stimmt nicht")
    need('"IsAlive"' in s and "a corpse is no trigger" in s,
         "tote NPCs loesen nicht aus",
         "keine IsAlive-Pruefung - eine Leiche wuerde die Mine zuenden")
    need("if (!_laid.Remove(m.Key)) return;" in s and "Send(OpGone" in s,
         "Einmalausloesung je Client plus Entfernen auf allen Clients",
         "keine Einmalwache oder kein Gone-Ereignis")
    need("PartBody = 1" in s and "TypeExplosion = 14" in s
         and '"PlayerApplyDamage"' in s and '"_lastKillerId"' in s,
         "gezielter Schaden: Koerper, Explosion, Spielertor, Killstreak-Schutz",
         "Schaden am Opfer falsch adressiert (Kopf x3 oder ohne Streak-Schutz)")
    need("RocketHook.Detonate" in s, "vernetzte Explosion", "keine vernetzte Explosion")
    need("ArmSeconds" in s and "Time.time < m.ArmAt" in s,
         "Scharfschaltverzoegerung", "Mine ist sofort scharf")
    for seam in ("ApMine.AddItems(Items)", "ApMine.BindConfig(Config)",
                 "ApMine.Install(_harmony)", "ApMine.Tick()"):
        need(seam in plug, "Seam " + seam, "Seam fehlt in RevivalPlugin.cs: " + seam)
    need("ApMine.ClearAll()" in admin, "Adminknopf Clear AP mines",
         "kein Adminknopf zum Raeumen")
    need(re.search(r"SellOnlyIds[^;]*1492", items) is not None,
         "Haendler nimmt die Mine an (Verkaufspreis)", "1492 hat keinen Verkaufspreis")
    files = ["apmine.ndmesh", "apmine_diffuse.png", "apmine_normal.png",
             "apmine_metal.png", "apmine_rough.png", "apmine_icon.png"]
    need(all('"%s"' % f in build for f in files),
         "build.ps1 installiert alle apmine-Dateien",
         "build.ps1 kopiert nicht alle apmine-Dateien ins Spiel")
    need('"RevivalApMine.cs"' in sync and '"apmine_build.py"' in sync
         and '"assets/src/apmine_blender.py"' in sync,
         "RevivalApMine.cs und die Blender-Quelle gehen ins oeffentliche Repository",
         "sync_public.py kennt RevivalApMine.cs oder die Blender-Quelle nicht")
    for f in ("apmine.blend", "apmine_blender.py", "apmine_preview.png"):
        need(os.path.exists(os.path.join(ASSETS, "src", f)),
             "Blender-Quelle assets/src/" + f, "Blender-Quelle fehlt: assets/src/" + f)
    mp = os.path.join(ASSETS, "apmine.ndmesh")
    if os.path.exists(mp):
        n, V, N, T, cnt, I = read_mesh(mp)
        ys = V[1::3]
        xs = V[0::3]
        need(abs(min(ys)) < 0.002 and 0.10 < max(ys) < 0.16 and 0.28 < max(xs) - min(xs) < 0.40,
             "PMN-2 steht auf y 0 und misst %.3f x %.3f Einheiten" % (max(xs) - min(xs), max(ys)),
             "apmine.ndmesh hat nicht die PMN-2-Masse (Boden y 0, 0.13 hoch, ~0.33 breit)")


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


def _code(src):
    """The source with its comments taken out. A rule that forbids a call has to
    read code: a file that explains at its top WHY it never touches
    `Passengers` names it twice in prose, and a plain substring test then fails
    the very file whose comment proves it is right."""
    out = []
    i = 0
    n = len(src)
    while i < n:
        c = src[i]
        if c == "/" and i + 1 < n and src[i + 1] == "/":
            j = src.find("\n", i)
            i = n if j < 0 else j
            continue
        if c == "/" and i + 1 < n and src[i + 1] == "*":
            j = src.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        if c == '"':
            out.append(c)
            i += 1
            while i < n:
                out.append(src[i])
                if src[i] == "\\" and i + 1 < n:
                    out.append(src[i + 1])
                    i += 2
                    continue
                if src[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


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
    #    The rail is the one vehicle the guard skips, and it may skip it only
    #    because the rail puts the hull on the surface every step itself - so
    #    the exemption is accepted in exactly that spelling and no other.
    guard = _body(code, "static bool GroundGuard(")
    guarded = ("if (GroundGuard(u)) continue;" in code
               or "if (!u.Rail && GroundGuard(u)) continue;" in code)
    if guarded and "u.Stuck = 0f;" in guard:
        ok("a falling vehicle is taken off the stuck timer before the driver runs")
    else:
        bad("Patrol ground guard: a falling hull can still feed the stuck timer")

    if ("if (!u.Rail && GroundGuard(u)) continue;" not in code
            or "if (u.Rail) { RailStep(u, Time.fixedDeltaTime); continue; }" in code
            or "if (u.Rail) { RailStep(u, StepDt); continue; }" in code):
        ok("a hull the guard skips is one the rail carries, not one nobody moves")
    else:
        bad("Patrol ground guard: the rail skips the guard without carrying the hull")

    # 5. Repeated failures end the vehicle instead of dropping it in again.
    recover = _body(code, "static void Recover(Unit u)")
    if "u.Recoveries > FallRecoveries" in recover and "Drop(u," in recover:
        ok("a vehicle that keeps falling is given up, not put back forever")
    else:
        bad("Patrol ground guard: a fallen vehicle can be recovered without limit")


def check_patrol_traffic():
    """[24] Ordinary patrol traffic and route discipline (static).

    The reported defect had two halves, both about a patrol that is put down as
    ONE editor composition, whose vehicles ignore each other's colliders:

      1. The civilian patrol stood on the road with the tank INSIDE the APC it
         drives with, and the pair neither moved nor fired. Nothing physical
         pushed the hulls apart; an interpenetrating pair SHAKES, so the
         speedometer read "moving" and the stuck timer never filled; and every
         line of sight ended on the other hull, so no gun ever took a target.
      2. The looter patrol's tank drove around in a wood its route does not go
         near. Its line-up slot was measured on a straight line drawn BACKWARDS
         from the head waypoint - which leaves the road at the first bend - and
         the shoulder rays of the obstacle avoidance answered every tree of a
         forest road with a fifth of a turn.

    Guards 8 to 11 are the second round (6.39.0). The hulls were held apart, but
    the pair still stood on the road all session, because the stuck RECOVERY
    refused to run: its search budget was a fixed count of waypoints on routes
    that differ threefold in density, two vehicles that met head on each refused
    to move because the other was there, an ordinary patrol could not pass
    through the prop it had no room to steer around, and the log reported all of
    it as "no ground" whatever the real reason had been.

    One guard per mistake. The driving itself stays an in-game acceptance item;
    research/patrol_traffic_check.py compiles the decisions below and runs them
    against a synthetic world (verify.py starts no subprocesses).
    """
    print("[24] Patrol traffic and route discipline (static)")
    patrol_p = os.path.join(ROOT, "Revival.Patrol.cs")
    if not os.path.exists(patrol_p):
        bad("Patrol traffic: source file missing")
        return
    code = _code(io.open(patrol_p, encoding="utf-8").read())

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Patrol traffic: " + why)

    # 1. Two hulls of ONE composition are eased apart - and no other pair,
    #    because every other pair on the road is solid and is physics' business.
    sep = _body(code, "static void PatrolSeparate(Unit u)")
    need("other.PatrolGroupId != u.PatrolGroupId" in sep
         and "Reach(u, away) + Reach(other, away) + PatrolOverlapGap" in sep
         and "RoadUnder(want, t, out y, out normal)" in sep
         and "PatrolSeparate(u);" in code,
         "two overlapping hulls of one patrol are eased apart onto found ground",
         "PatrolSeparate is gone or no longer separates the pair - a tank can "
         "stand inside its own APC again")

    # 2. The gap to the mate ahead is kept by the driver, because the hulls
    #    cannot keep it themselves. Never behind a wreck, never head on, and
    #    never for the rest of the session.
    queue = _body(code, "static float QueueBehind(Unit u, Transform t, "
                        "float want, float dt)")
    need("if (ahead <= 0f || ahead > QueueLook) continue;" in queue
         and "Vector3.Dot(forward, hers.normalized) < QueueSameWay" in queue
         and "if (other.Died > 0f) continue;" in queue
         and "u.Queued < QueuePatience" in queue
         and "want = QueueBehind(u, t, want, dt);" in code,
         "a patrol brakes for the mate ahead, but not for a wreck, not for "
         "oncoming traffic and not forever",
         "the driver no longer keeps the gap to its own composition mate")

    # 3. Shaking in place is not driving. This is the test the speedometer
    #    cannot do, and the one an interpenetrating pair fails.
    prog = _body(code, "static bool NoProgress(Unit u, Vector3 pos)")
    esc = _body(code, "static bool Escalate(Unit u, Vector3 pos)")
    hold = _body(code, "static void HoldStill(Unit u)")
    need("FlatDistance(pos, u.ProgressPos) > ProgressMetres" in prog
         and "Time.time - u.ProgressAt >= ProgressSeconds" in prog
         and "!slow && (u.ConvoyId != 0 || !NoProgress(u, pos))" in esc
         and "MadeProgress(u, u.Car.transform.position)" in hold,
         "ground covered, not the speedometer, decides that a patrol is stuck",
         "the stuck escalation is back on the speedometer alone - a shaking "
         "pair of hulls then holds a road for the whole session")

    # 4. A warp target nothing is standing on. Dropping a stuck hull onto a
    #    mate is the shortest way to weld two vehicles together there is.
    free = _body(code, "static void Free(Unit u, Vector3 pos)")
    back = _body(code, "static bool BackOnRoute(Unit u, Vector3 pos, float off)")
    need("SpotTaken(u, target, FreeRoom)" in free
         and "FlatDistance(r.P[to].Pos, pos) < clearOf" in free
         and "SpotTaken(u, target, FreeRoom)" in back,
         "no recovery warps a hull onto the spot another vehicle stands on",
         "a stuck patrol can be warped into a mate, or straight back onto the "
         "obstacle it was stuck against")

    # 5. Off the recorded line for good - the leash - and a line-up measured
    #    ALONG the road instead of across the country beside it.
    leash = _body(code, "static bool Leashed(Unit u, Vector3 pos)")
    slot = _body(code, "static bool LineupSlot(Route r, int start, float back, "
                       "out Vector3 point,")
    spawn = _body(code, "static void Spawn(Route src, bool auto)")
    need("OffRoute(u.Route, u.Next, pos)" in leash
         and "Time.time - u.OffRouteSince < LeashSeconds" in leash
         and "BackOnRoute(u, pos, off)" in leash
         and "if (Leashed(u, pos)) return;" in code,
         "a patrol that has left its own recorded line is put back on it",
         "nothing brings a patrol back out of the wood - driving off the route "
         "is not being stuck, so no other timer would ever notice")
    need("point = r.P[at].Pos - leg * (rest / len);" in slot
         and "LineupSlot(r, start, RevivalConvoy.LineupGap * k," in spawn
         and "firstWaypoint = nextWaypoint;" in spawn,
         "the vehicles of one composition line up along the recorded road",
         "the line-up is drawn as a straight line again - the tail of a "
         "composition is then put down wherever that line leaves the road")

    # 6. The shoulder rays. A forest road grazes them with every tree.
    avoid = _body(code, "static float Avoid(Unit u, Transform t, float speed)")
    need("if (near >= SideDodgeAt) return 0f;" in avoid
         and "SideDodge * urgency" in avoid,
         "only something the hull is about to scrape moves the wheel, gently",
         "a shoulder ray answers every passing tree with a fixed turn again - "
         "that is what walks a patrol off the road and into the wood")

    # 7. The half of the report that outlives the driving fix: they did not
    #    FIRE, because the other hull sat over the muzzle.
    strahl = _body(code, "static GameObject Strahl(Unit u, Vector3 from, "
                         "Vector3 dir, float range,")
    welded = _body(code, "static bool Welded(Unit u, GameObject go)")
    need("&& !Welded(u, go)" in strahl
         and ">= WeldedWithin) continue;" in welded
         and "go.transform.IsChildOf(other.Car.transform)" in welded,
         "a gun looks through a hull it is standing inside, as it does through "
         "its own bow plate",
         "a hull standing in another one blinds its gun - the pair then "
         "neither moves nor shoots")

    # 8. The ordinary patrol passes through what it cannot steer around, the
    #    way the convoy has since feature/convoy-oneway-drive. BOTH branches of
    #    the driver call it now, which is why this counts instead of searching.
    drive = _body(code, "static void Drive(Unit u)")
    ray = _body(code, "static void GhostRay(")
    need(drive.count("GhostAhead(u, t, vel.magnitude);") == 2
         and "if (Lebendig(go.transform)) return;" in ray
         and "if (IsDriveSurface(go, normal)) return;" in ray,
         "a patrol drives through the prop it cannot steer around, and never "
         "through the ground or through anything alive",
         "only the convoy ghosts through obstacles - an ordinary patrol then "
         "snags on the wreck parked across a dirt road, where there is nowhere "
         "to steer, and stands there for the rest of the session")

    # 9. The recovery's search budget is METRES. A count of waypoints punishes a
    #    densely recorded route for being densely recorded, and the two live
    #    recordings of 2026-09-21 differ by a factor of three.
    ground = _body(code, "static int GroundedWaypoint(")
    need("walked += FlatDistance(r.P[last].Pos, r.P[at].Pos);" in ground
         and "step >= GroundTries && walked >= GroundReach" in ground,
         "the stuck recovery searches a fixed number of METRES down the road",
         "the search is back on a fixed waypoint COUNT - twelve waypoints are "
         "138 m of the looter's recording and 44 m of the civilians', and 44 m "
         "is not enough to get past the hull a vehicle is stopped against")

    # 10. Two hulls that each refuse to move because the other one is there. An
    #     out-and-back route guarantees the pair meets head on somewhere.
    free = _body(code, "static void Free(Unit u, Vector3 pos)")
    hold = _body(code, "static void FreeHold(")
    blocked = _body(code, "static bool SpotBlocked(")
    need("u.Refusals >= HoldsBeforeForce" in free
         and "u.Refusals++;" in hold
         and "u.Refusals = 0;" in free
         and "if (mate) continue;" in blocked,
         "a patrol held again and again by its own mate is finally moved past "
         "it, and never onto a hull it cannot drive through",
         "nothing breaks the deadlock of two hulls that meet head on - each "
         "one refuses to warp because the other is standing there, and then "
         "neither of them ever moves again")

    # 11. The log has to name WHICH of the two requirements failed. Reporting a
    #     spot held by a vehicle as a spot with no ground is what pointed the
    #     previous fix round at the wrong half of the problem.
    need("bool anyGround = landed >= 0;" in free
         and "FreeHold(u, from, to, anyGround" in free,
         "a refusal says whether the ground or another vehicle was the reason",
         "both refusals read \"no ground\" again - the log then cannot tell an "
         "unloaded area from a deadlocked pair of hulls")

    # 12. The rail. A confirmed stop and the leash both end in a CARRY along the
    #     recorded line, not in a warp the driver has to take over from. Both
    #     entries keep the old warp only as the answer for a route the rail has
    #     no line to work with, which is what RailOn returning false means.
    esc = _body(code, "static bool Escalate(Unit u, Vector3 pos)")
    leash = _body(code, "static bool Leashed(Unit u, Vector3 pos)")
    rail_on = _body(code, "static bool RailOn(Unit u, Vector3 pos, string why)")
    rail_step = _body(code, "static void RailStep(Unit u, float dt)")
    need("if (u.ConvoyId == 0" in esc and "RailOn(u, pos" in esc
         and "Free(u, pos);" in esc
         and "RailOn(u, pos," in leash and "BackOnRoute(u, pos, off);" in leash,
         "a stuck patrol and a lost one are carried along their own line",
         "the stuck escalation or the leash warps the hull again without "
         "offering the rail first - that is the loop of teleport, wrong way "
         "round, corner, stuck, teleport")

    # 13. The carry starts where the hull stands, on the carriageway it was
    #     driving. An out-and-back route carries both directions on one road, so
    #     the nearest leg in the WORLD is a coin toss between them.
    #     The order of the walk is half the answer: overlapping legs are all
    #     zero metres from the hull, so the leg it was driving has to be asked
    #     first and a later one has to be strictly closer to win.
    arc_at = _body(code, "static float RailArcAt(Unit u, Vector3 pos)")
    need("for (int k = 0; k <= 2 * RailLook; k++)" in arc_at
         and "int step = ((k + 1) / 2) * ((k % 2) == 0 ? 1 : -1);" in arc_at
         and "int j = u.Next - 1 + step;" in arc_at
         and "if (have && off >= bestOff) continue;" in arc_at
         and "u.RailArc = RailArcAt(u, pos);" in rail_on,
         "the carry starts on the leg the vehicle was actually driving",
         "the rail picks its arc from the whole route again, or breaks a tie "
         "between two overlapping legs by luck - on an out-and-back road both "
         "face the vehicle back the way it came")

    # 14. The same tiebreak for the two warps that remain. Recover and
    #     BackOnRoute used the global nearest waypoint, which is the "wrong way
    #     round after a teleport" half of the 2026-09-22 report.
    recover = _body(code, "static void Recover(Unit u)")
    back = _body(code, "static bool BackOnRoute(Unit u, Vector3 pos, float off)")
    need("NearestOn(u, t.position)" in recover and "NearestOn(u, pos)" in back,
         "every recovery puts the hull back facing the way it was driving",
         "a recovery is back on the global nearest waypoint - on an "
         "out-and-back route that is the other carriageway half the time")

    # 15. A carried vehicle is placed by the same measured placement the convoy
    #     column uses, it keeps the gap to its own mate, and it is handed back
    #     only once it is a long way further on - or never again, after three
    #     carries inside two minutes.
    need("Carry(u, line, RailHeading(r, u.RailArc, u.OneWay), u.RailSpeed," in rail_step
         and "want = QueueBehind(u, t, want, dt);" in rail_step
         and "if (went < RailMetres || Time.time - u.RailSince < RailSeconds) return;"
             in rail_step
         and "if (u.RailForever) return;" in rail_step
         and "u.RailRuns >= RailRunsStick" in rail_on,
         "a carried patrol is placed, spaced and handed back on stated rules",
         "the rail no longer uses the measured placement, drops the spacing to "
         "its mate, or hands the wheel back on no rule at all")

    # verify.py runs no subprocesses, so the executable proof only has to be
    # in the repository (like research/patrol_fall_check.py for [18]).
    if os.path.isdir(os.path.join(ROOT, "research")):
        need(os.path.exists(os.path.join(ROOT, "research",
                                         "patrol_traffic_check.py")),
             "research/patrol_traffic_check.py is present",
             "research/patrol_traffic_check.py is missing - the traffic "
             "decisions of a patrol are unproven")


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
         and 'WriteVec3(c, t, "localRotation", euler, names[i]);' in items
         and 'AccessTools.Field(t, "localRotation")' in items
         and '"localEulerAngles"' not in items
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
    ordnance = io.open(os.path.join(ROOT, "Revival.OrdnanceBlast.cs"), encoding="utf-8").read()
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
    need('OrdnanceBlast.EnqueueMortar(point, shooter, radius, npcPeak, vehiclePeak, playerPeak, AnyFaction)' in s
         and 'new object[] { damage, Body, Explosion, 0, direction }' in ordnance,
         "NPC damage queued anonymously to the Photon health owner",
         "Mortar must use the position registry and anonymous NPC owner RPC")
    need("Mortar.FactionShield.SameFactionAsLocal(go)" in ordnance,
         "eigene Fraktion wird gar nicht getroffen",
         "der Schutz der eigenen Fraktion fehlt im Spielerdurchlauf")
    need("FactionShield.Arm();" in s and "const int Traitor = 6;" in s,
         "Fraktionsnetz bewacht jeden Feuerauftrag",
         "das Fraktionsnetz wird nicht mehr bewaffnet")
    # B1: GetPlayerInfo() is an instance method without arguments on the
    # player's own component (REVERSE_ENGINEERING 40); without this path
    # every player's faction reads unknown and the radar refuses all orders.
    need('AccessTools.Method(t, "GetPlayerInfo", Type.EmptyTypes, null)' in s
         and "_getInfo.Invoke(stats, null)" in s,
         "Spielerfraktion ueber GetPlayerInfo() am Spieler lesbar (B1)",
         "FactionShield liest GetPlayerInfo() nicht am Spieler - jede Spielerfraktion ist unbekannt")

    # --- the owner-0 price: StatsOnNpcKilled throws on PhotonPlayer.Find(0).
    need("Mortar.BreakKillStreak(__instance as Component)" in ordnance
         and '"_lastKillerId"' in s,
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
    need("if (_master())" in ordnance and "{ damage, Explosion }" in ordnance
         and "Body = 1, Explosion = 14" in ordnance,
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
    gear_p = os.path.join(ROOT, "RevivalDroneGear.cs")
    if not os.path.exists(bat_p):
        bad("RevivalArtyBattery.cs fehlt")
        return
    raw = io.open(bat_p, "rb").read()
    b = raw.decode("utf-8", "replace")
    crew = io.open(crew_p, encoding="utf-8").read() if os.path.exists(crew_p) else ""
    gear = io.open(gear_p, encoding="utf-8").read() if os.path.exists(gear_p) else ""
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

    # --- 3b-2: THE SPEED, AND THE CONFIG FILE THAT WOULD SWALLOW IT.
    # FIELD 2026-09-21: "die NPC recon drohne soll schneller fliegen". 16 m/s
    # was measured against the old 240 m ring - a 95 s lap - and stayed when
    # the ring went to 600 m, which made the lap 236 s. 26 is the flat-out
    # speed of the player's own surveillance drone, and that is the same
    # airframe (SurvDrone.MaxSpeed in RevivalDroneGear.cs), so the number is
    # the machine and not a feel. The migration is the half that actually
    # reaches a player: Config.Bind reads an existing file, so a new default
    # on its own changes nothing for anybody who has already played.
    need('"OrbitSpeed", 26f' in b,
         "the recon drone flies at the airframe's own speed, 26 m/s",
         "OrbitSpeed is not 26 - a 600 m ring at 16 m/s is a 236 s lap, and a "
         "warning line crossed once every four minutes warns late")
    need("if (_cfgOrbitSpeed.Value == 16f) _cfgOrbitSpeed.Value = 26f;" in b,
         "the released 16 m/s is migrated out of an existing config file",
         "the old speed is not migrated - every player who already has a "
         "nextday.revival.toolkit.cfg keeps the slow drone and the change "
         "reaches nobody")
    need("MaxSpeed = 26f" in gear,
         "and the surveillance drone that number was taken from still flies "
         "at 26",
         "SurvDrone.MaxSpeed moved - OrbitSpeed's justification in "
         "RevivalArtyBattery.cs now points at a number that is not there")

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
     12. The FLOOR TOLERANCE from rule 11 is EARNED, not assumed. Ruecksitze
         can legitimately find nothing to remove - a donor whose interior is
         one mesh shared with the front seats fails both the size cap and the
         front-seat guard on purpose - and until this rule, DeckStep granted
         its one-step tolerance anyway, which is enough on its own to read as
         "elevated over the last row of seats" when the measured surface was
         the still-standing bench. A confirmed-zero removal now collapses the
         tolerance to zero. Field report of 2026-09-20 (screenshots
         anothertechnicalbug/anothertechnicalbug2): the same symptom rule 11
         was meant to close, still present.

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
    cch_p = os.path.join(ROOT, "Revival.CombatHooks.cs")
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
    cch = io.open(cch_p, encoding="utf-8").read() if os.path.exists(cch_p) else ""
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
         and "boden + step" in t
         and "CfgDeckStep.Value * height" in t,
         "die Standflaeche wird auf Fussbodenhoehe begrenzt (DeckStep)",
         "die Obergrenze ueber dem Fussboden des Spenders fehlt - dann steht "
         "die Lafette wieder auf dem hoechsten Kollisionskoerper und der "
         "Schuetze wie auf einem Ausguck")
    step = _bind_number(t, "Technical", "DeckStep")
    need(step is not None and 0.0 < step <= 0.30,
         "DeckStep %s der Fahrzeughoehe, eine Stufe und kein Stockwerk" % step,
         "DeckStep fehlt oder laesst wieder ein halbes Fahrzeug Hoehe zu")

    # --- 7f: the fifth field report on the same spot (2026-09-20, screenshots
    # anothertechnicalbug/anothertechnicalbug2): the gunner still elevated over
    # the last row of seats. Ruecksitze can fail to find a separable bench (a
    # donor whose interior is one mesh shared with the front seats fails both
    # the size cap and the front-seat guard on purpose) and still return 0 -
    # and until this pass, DeckStep granted its knee-height tolerance anyway,
    # which is enough on its own to read as "elevated" when the surface it
    # measured was the still-standing bench and not a real load bed. The
    # tolerance is now EARNED: zero when removal was not confirmed.
    need("int ruecksitzeEntfernt = Ruecksitze(car, seats, min, max, unitsPerMetre);" in t
         and "float step = ruecksitzeEntfernt > 0 ? CfgDeckStep.Value * height : 0f;" in t,
         "die DeckStep-Toleranz gilt nur bei bestaetigt entfernter Ruecksitzbank",
         "eine nicht bestaetigt entfernte Ruecksitzbank bekommt wieder einen "
         "Toleranzschritt - der Schuetze kann wieder ueber der letzten "
         "Sitzreihe stehen (Feldbericht anothertechnicalbug/"
         "anothertechnicalbug2, 2026-09-20)")

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

    # --- 9: the tracer FLIES instead of materialising as a static line.
    #
    # Field report 2026-09-20: "aktuell sind die schuesse diese langweiligen
    # raytraces, ich will das die schuesse aussehen so wie die schuesse aus
    # der dragunov aber halt mit hohem frequenz". TechnicalTracerStreak moves
    # its LineRenderer's two endpoints every frame instead of drawing the
    # whole muzzle-to-impact line in one go, and a hard timeout keeps a
    # misconfigured TracerSpeed from leaking the GameObject forever.
    need("internal sealed class TechnicalTracerStreak : MonoBehaviour" in t
         and "TechnicalTracerStreak.Spawn(von, bis, tempo, laenge" in t
         and "_line.SetPosition(0, ende);" in t
         and "_line.SetPosition(1, spitze);" in t,
         "die MG-Leuchtspur fliegt zum Ziel statt sofort als Linie zu stehen",
         "die fliegende Leuchtspur fehlt - der Schuss ist wieder eine sofort "
         "erscheinende Linie (Feldbericht anothertechnicalbug/"
         "anothertechnicalbug2, 2026-09-20)")
    need("if (_t > Timeout) { Destroy(gameObject); return; }" in t,
         "eine haengengebliebene Leuchtspur raeumt sich selbst ab",
         "der Sicherheits-Timeout der fliegenden Leuchtspur fehlt - eine sehr "
         "kleine TracerSpeed wuerde das Objekt fuer immer stehen lassen")
    need("CfgTracerSpeed = cfg.Bind(\"TechnicalGun\", \"TracerSpeed\"" in t
         and "CfgTracerLength = cfg.Bind(\"TechnicalGun\", \"TracerLength\"" in t,
         "Fluggeschwindigkeit und Laenge der Leuchtspur sind einstellbar",
         "TracerSpeed/TracerLength fehlen in der Konfiguration")
    need("internal static Material TracerMaterial()" in cch,
         "das Leuchtspur-Material wird geteilt statt neu erzeugt",
         "RocketHook.TracerMaterial ist nicht mehr internal - "
         "TechnicalTracerStreak kann das Material dann nicht mehr teilen")

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


def check_technical_crew():
    """[17b] The men who ride the technical, and the editor kind that puts them
    on a route.

    A technical is the first vehicle of this toolkit whose crew has to be SEEN
    while it drives. Every rule below is one or two lines that a later edit
    could undo with nothing looking broken until somebody is in the game.

      1. THE EDITOR AND THE RUNTIME MUST AGREE ON THE KINDS. The route editor
         offers whatever compdef.VEHICLE_TYPES lists and the plugin spawns
         whatever VehicleRegistry registers. A kind in one and not the other is
         a column that silently spawns BTRs instead of what was drawn, which is
         exactly what the technical did before this change.
      2. THE SEATS MUST AGREE TOO. compdef.VEHICLE_SEATS names the technical's
         three places in order and the editor writes one crew line per place;
         Technical.SeatTotal/GunnerSeat decide which of them ends up at the gun.
         Disagree and the man the admin dressed as the gunner drives instead.
      3. NO NPC IN `Passengers`. VehicleGameSystem::SetDamageToAllPassengers
         walks that array and calls GetComponent<PlayerNetworkController>()
         .GetPhotonPlayer on every entry; on an NPC that throws, and it runs at
         the one moment that must not fail - the vehicle's destruction. This is
         the reason Revival.Crew.cs kept patrol crews a number in the first
         place, and it is not undone here.
      4. THE MEN ARE PLACED FROM THE TRUCK, EVERY FRAME, ON EVERY CLIENT. The
         other half of that same class comment: a body parented on the host
         alone leaves every other client watching the crew trail the truck down
         the road. Their place is derived from the vehicle's own transform in a
         LATE frame instead, which is what makes them ride it rather than
         follow it.
      5. THE GUNNER IS NAMED BY A KEY, NOT BY WHERE HE STANDS. He is spawned as
         a squad of one with his own Photon spawn key, so he is still the man at
         the pintle after one of the other two has been shot. "Whoever stands
         furthest back" would hand the gun to a driver at that moment.
      6. THAT KEY MUST CARRY A SLASH, and Revival.GroundEnemies.cs must skip
         keys that have one. It reconciles by key and DESTROYS every keyed NPC
         that is not in its desired set - without the guard it would clear every
         rider on the map as a stale ground group.
      7. A DRIVER, ALWAYS. The cab is filled before the gun, so a crew too small
         for three men is a driver and then a gunner, never a gunner alone in a
         truck that drives itself.
      8. THE RIDERS ARE THE WRECK CREW. Patrol.UnloadCrew must ask before it
         spawns, or a three-seat truck puts six men on the ground and the three
         who were visible a frame earlier have to vanish to do it.
      9. THE GUN IS AIMED THROUGH THE GUNNER'S BODY, which the game already
         synchronizes - the same rule check_technical() keeps for the player's
         gun (rule 6 there), for the same reason: a private Photon channel for a
         bearing is a bug this repository has already paid for once.
     10. THE GUN STAYS ANTI-PERSONNEL. No VehicleArmor hit from the NPC's fire
         either, or the crew would quietly give the machine gun the autocannon's
         anti-vehicle rate that check_technical() rule 5 forbids the player.
     11. THE MEN GO THROUGH THEIR OWN TRUCK. A rider stands inside the hull that
         carries him, and both sides are solid: the truck's rigidbody pushes
         itself out of three men that the next LateUpdate puts back, every
         physics step. That is the 2026-09-22 field report from both ends - a
         truck shoved off its road and off the ground, men tipped over the side
         and run over. Physics.IgnoreCollision between this truck and these men
         answers it, and it has to be laid AGAIN and again: Unity forgets an
         ignored pair when either collider is switched off and on, which is what
         the distance optimization does to a crew no player is near.
     12. NO RIDER IS GIVEN THE HULL'S ATTITUDE. A man takes the truck's HEADING
         and stays upright. Writing the hull's full rotation onto him lays the
         whole cab over with every slope and every bump, which is the "the NPCs
         in it fall over" half of the same report. TechnicalGun.Stellung has
         always flattened the gunner's; the cab must do the same.
     13. A TRUCK WITH NOBODY ALIVE IN THE CAB STOPS. The men can be shot off a
         truck that is still whole, and until 6.44.1 nothing told Patrol's
         driver: the truck drove its route empty (field report 2026-09-22).
         FixedTick asks Verwaist before it drives, Driverless holds the truck,
         and a dead crew is counted as dead, not as switched off - Steht must
         not read activeInHierarchy, or every patrol far from a player stops.
     14. THE GUNNER LIVES AND DIES WITH THE TRUCK. A prefix on
         NPC_AI2.ApplyDamage skips every hit on a riding gunner, ReleaseRiders
         kills him instead of handing him to NpcWar when the truck is destroyed
         (after Released is set, or the prefix swallows that round too), and
         his own rifle is switched off while he works the MG - on every
         client, from the scan, and back on when he dismounts.
    """
    print("[17b] Technical crew: the men who ride it (statisch)")
    crew_p = os.path.join(ROOT, "RevivalTechnicalCrew.cs")
    tech_p = os.path.join(ROOT, "RevivalTechnical.cs")
    patrol_p = os.path.join(ROOT, "Revival.Patrol.cs")
    ground_p = os.path.join(ROOT, "Revival.GroundEnemies.cs")
    ural_p = os.path.join(ROOT, "RevivalUralTruck.cs")
    for path in (crew_p, tech_p, patrol_p, ground_p, ural_p):
        if not os.path.exists(path):
            bad("Technical crew: %s missing" % os.path.basename(path))
            return
    crew = io.open(crew_p, encoding="utf-8").read()
    tech = io.open(tech_p, encoding="utf-8").read()
    patrol = io.open(patrol_p, encoding="utf-8").read()
    ground = io.open(ground_p, encoding="utf-8").read()
    ural = io.open(ural_p, encoding="utf-8").read()

    # 1 - the editor's kinds and the registry's kinds are the same set
    try:
        import compdef
        editor_kinds = set(compdef.VEHICLE_TYPES)
    except Exception as ex:
        editor_kinds = None
        bad("Technical crew: compdef could not be imported (%s)" % ex)
    registry = set(re.findall(r'Add\(Make\("([a-z0-9_]+)"', ural))
    if editor_kinds is not None:
        missing = editor_kinds - registry
        if missing:
            bad("Technical crew: the editor offers %s, which VehicleRegistry "
                "does not register - those columns would spawn BTRs"
                % sorted(missing))
        else:
            ok("every editor vehicle kind is a registered runtime kind (%s)"
               % ", ".join(sorted(editor_kinds)))
        if "technical" not in editor_kinds:
            bad("Technical crew: the editor no longer offers the technical")

    # The live snapshot is validated before RevivalComposition gets to resolve
    # registry kinds. Keep the validator in step with the editor, or one
    # technical row rejects the complete route update as an invalid composition.
    live_p = os.path.join(ROOT, "Revival.LiveRoutes.cs")
    live = io.open(live_p, encoding="utf-8").read() if os.path.exists(live_p) else ""
    validator = _body(live, "static void Validate(Snapshot value)")
    if 'c[2] != "technical"' in validator:
        ok("the live composition validator accepts technical rows")
    else:
        bad("Technical crew: live routes reject a technical composition row")

    # 2 - the seat table and the runtime seats agree
    seats = None
    try:
        import compdef as _cd
        seats = _cd.VEHICLE_SEATS.get("technical")
    except Exception:
        pass
    total = re.search(r"SeatTotal\s*=\s*(\d+)", tech)
    gunner = re.search(r"GunnerSeat\s*=\s*(\d+)", tech)
    if seats and total and gunner:
        if (len(seats) == int(total.group(1))
                and int(gunner.group(1)) == len(seats) - 1
                and seats[int(gunner.group(1))] == "gunner"
                and seats[0] == "driver"):
            ok("the editor's three places match SeatTotal/GunnerSeat, gunner last")
        else:
            bad("Technical crew: compdef.VEHICLE_SEATS %s disagrees with "
                "SeatTotal=%s GunnerSeat=%s"
                % (seats, total.group(1), gunner.group(1)))
    else:
        bad("Technical crew: the seat table or SeatTotal/GunnerSeat is missing")

    # 3 - no NPC is ever written into the game's passenger array
    crew_code = _code(crew)
    if "Passengers" in crew_code:
        bad("Technical crew: RevivalTechnicalCrew.cs touches `Passengers` - "
            "SetDamageToAllPassengers throws on an NPC entry")
    else:
        ok("no NPC reaches the game's Passengers array")

    # 4 - placed from the truck, in a late frame, on every client
    late = _body(crew, "internal static void LateFrame()")
    if ("TechnicalCrew.LateFrame();" in tech
            and tech.find("TechnicalCrew.LateFrame();")
                < tech.find("TechnicalGun.LateAll();")
            and "Halten(t)" in late):
        ok("the riders are placed from the truck in LateUpdate, before the gun work")
    else:
        bad("Technical crew: the per-frame placement is gone or runs after "
            "TechnicalGun.LateAll - the men would trail the truck")
    if "SetParent" in crew_code:
        bad("Technical crew: a rider is parented - that is host-only and is "
            "precisely what makes the crew lag on every other client")

    # 5 + 6 - the key names the job, and ground enemies leave it alone
    if ('KeyPrefix = "tech/"' in crew and 'KeyGunner = "/g"' in crew
            and 'KeyCab = "/c"' in crew and "t.Key + KeyGunner" in crew):
        ok("the gunner is spawned as his own squad and named by his own key")
    else:
        bad("Technical crew: the gunner is no longer named by a spawn key - "
            "a geometric rule hands the gun to a driver when he dies")
    guard = _body(ground, "static void Reconcile(")
    if not guard:
        guard = ground
    if "key.IndexOf('/') >= 0" in guard:
        ok("ground enemies skip the riding crew's keys")
    else:
        bad("Technical crew: Revival.GroundEnemies.cs would destroy every rider "
            "as a stale ground group")

    # 7 - the cab is filled before the gun
    manned = _body(crew, "static void Bemannen(Truck t)")
    if ("int cab = Mathf.Max(1, count - 1);" in manned
            and "bool gunner = count >= 2;" in manned):
        ok("the driver is the first man on, the gunner the last")
    else:
        bad("Technical crew: a crew too small for three men could leave the "
            "truck driving itself")

    # 8 - the riders ARE the wreck crew
    unload = _body(patrol, "static void UnloadCrew(Unit u)")
    if "TechnicalCrew.ReleaseRiders(u.Car, u.Seite)" in unload:
        ok("a wrecked technical releases its riders instead of doubling them")
    else:
        bad("Technical crew: Patrol.UnloadCrew spawns a second crew beside the "
            "men already standing on the truck")
    if ("Patrol.CrewedList" in crew and "Patrol.CrewedCount" in crew):
        ok("the riders wear the editor's uniform and carry its weapon")
    else:
        bad("Technical crew: the riders no longer read the editor loadout - "
            "the men would change clothes when the truck burns")

    # 9 - the bearing crosses the wire as the man's own rotation
    aim = _body(crew, "static void Zielen(Truck t)")
    if ("t.Gunner.transform.rotation" in aim
            and "Quaternion.LookRotation(face.normalized, Vector3.up)" in aim):
        ok("the master turns the gunner and the existing slew carries the bearing")
    else:
        bad("Technical crew: the aim no longer travels through the gunner's body")
    if "RaiseEvent" in crew_code or "PublishTurret" in crew_code:
        bad("Technical crew: a private network channel for the gun's bearing")

    # 10 - still anti-personnel
    if "VehicleArmor" in crew_code:
        bad("Technical crew: the NPC's machine gun damages armour - the player's "
            "does not (check_technical rule 5)")
    else:
        ok("the NPC gunner's fire stays anti-personnel, as the player's is")

    # 11 - the riders are not solid to the truck they ride, and stay that way
    through = _body(crew, "static void Durchlassen(Truck t)")
    scan_body = _body(crew, "internal static void Scan(Component[] all)")
    if ("Physics.IgnoreCollision(a, b, true)" in through
            and "Durchlassen(t)" in scan_body
            and "t.NextPass = Time.time + PassEvery" in scan_body):
        ok("the riders go through their own truck, and the pass is refreshed")
    else:
        bad("Technical crew: the crew is solid to the truck that carries it - "
            "the hull pushes itself out of its own driver every physics step "
            "(or the pass is laid only once and Unity forgets it)")
    if "Durchlassen(t);" in _body(crew, "static void Bemannen(Truck t)"):
        ok("a crew is let through the hull in the frame it is spawned in")
    else:
        bad("Technical crew: a freshly spawned crew is solid until the next "
            "scan - a hundred physics steps of the truck fighting its men")

    # 12 - Y B3 supersedes heading-only seating: riders share the full hull
    # attitude and are directly parented, so a bank cannot put them through it.
    hold = _body(crew, "static void Halten(Truck t)")
    attitude = _body(crew, "static Quaternion Aufrecht(Truck t)")
    binding = io.open(os.path.join(ROOT, "Revival.SeatBinding.cs"), encoding="utf-8").read()
    if ("SeatBinding.BindSeat(ai.transform, t.Seats.GetChild(seat), t.Root);" in hold
            and "return t.Root.rotation;" in attitude
            and "body.SetParent(r.Seat, true);" in binding
            and "SeatBinding.Detach(ai.transform);" in _body(crew, "static void Absteigen(Truck t)")):
        ok("riders bind directly to seats with full hull attitude and detach on exit")
    else:
        bad("Technical crew: direct seat parenting, full attitude or exit release is missing")

    # 13 - nobody alive in the cab, the truck stops
    fixed = _body(patrol, "public static void FixedTick()")
    orphan = _body(patrol, "static bool Verwaist(Unit u)")
    steht = _body(crew, "static bool Steht(Component ai)")
    if ("if (Verwaist(u))" in fixed
            and fixed.find("if (Verwaist(u))") < fixed.find("Drive(u);")
            and "if (u.Driverless) { HoldStill(u); continue; }" in fixed
            and "TechnicalCrew.Driverless(u.Vgs)" in orphan
            and "TechnicalCrew.Wiped(u.Vgs)" in orphan
            and "NpcWar.GroundAlive(ai)" in steht
            and "activeInHierarchy" not in steht):
        ok("a technical whose cab is dead stops, one whose crew is dead is abandoned")
    else:
        bad("Technical crew: a truck whose men were shot off it drives its "
            "route empty (or stops wherever its crew is merely switched off)")

    # 14 - the gunner lives and dies with the truck, rifle put away
    guard = _body(crew, "public static bool BoundPrefix(object __instance)")
    release = _body(crew, "internal static bool ReleaseRiders(GameObject car, string side)")
    scan_all = _body(crew, "internal static void Scan(Component[] all)")
    dismount = _body(crew, "static void Absteigen(Truck t)")
    if ("TechnicalCrew.Install(harmony);" in tech
            and '"BoundPrefix"' in crew
            and "return false;" in guard and "t.Released" in guard
            and "Toeten(t.Gunner)" in release
            and release.find("t.Released = true;") < release.find("Toeten(t.Gunner)")
            and 'GunnerDiesWithVehicle", true' in crew):
        ok("the riding gunner cannot be shot and dies with his truck")
    else:
        bad("Technical crew: the gunner no longer lives and dies with the truck "
            "(or is killed before Released, which the prefix swallows)")
    if ("Entwaffnen(_trucks[i]);" in scan_all and "Bewaffnen(t);" in dismount
            and '"Weapons_HelperR"' in crew):
        ok("the gunner's own rifle is put away while he works the MG")
    else:
        bad("Technical crew: the gunner holds his rifle through the MG's grips")

    # the file rule the rest of the feature follows
    raw = io.open(crew_p, "rb").read()
    if raw.startswith(b"\xef\xbb\xbf"):
        bad("Technical crew: RevivalTechnicalCrew.cs has a BOM - build.ps1 "
            "needs BOM-less sources")
    try:
        raw.decode("ascii")
        ok("RevivalTechnicalCrew.cs is ASCII without a BOM")
    except UnicodeDecodeError:
        bad("Technical crew: RevivalTechnicalCrew.cs is not ASCII - its "
            "player-facing lines belong in RevivalUralTruck.cs")


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
      7. THE TRAVEL LOCK, both halves (6.39.0). A self-propelled gun drives
         with its tube in its cradle and not otherwise. The field report was a
         gun that "slowly makes its way straight ahead" while the truck is
         already rolling, and that is one bug with two ends: the stowing was
         SLEWED at a rate, and driving was never held back until it finished.
         So: the driver's SEAT starts the stowing, not the engine and not the
         speed; a stowed gun's lay is ASSIGNED off the hull and never slewed,
         because any rate lags a hull that turns quicker than it does; aiming
         and travelling may not overlap, or a gun somebody keeps laying strands
         its own truck; and the throttle is held at the game's ONLY input seam,
         VehicleGameSystem::InputAxis, with the handbrake on rather than by
         switching the physics off.

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

    # --- 7: the travel lock, the gun half (RevivalMortar.cs).
    travel = _body(mort, "static bool Travel(Tube t, float dt)")
    slew = _body(mort, "static void Slew()")
    need(travel and "if (Travel(t, dt)) continue;" in slew,
         "the travel lock is the only thing that lays a howitzer somebody is "
         "driving",
         "Slew has no travel lock, or it does not take the gun out of the "
         "ordinary rate-limited slewing while the truck is being driven")
    need("static bool DriverAboard(GameObject body)" in mort
         and "DriverAboard(t.Go)" in travel
         and "EngineRunning" not in mort,
         "the driver's SEAT starts the stowing, not a running engine",
         "the stowing does not hang on the driver's seat - a crew at its own "
         "gun would lose the tube the moment somebody left the engine idling")
    need("t.Yaw = rest;" in travel and "t.Pitch = TravelPitch;" in travel
         and "public bool Stowed;" in mort,
         "a stowed gun's lay is ASSIGNED off the hull - it stands still "
         "relative to the truck",
         "the stowed gun is slewed towards the hull instead of written off it "
         "- every rate lags a hull that turns quicker than the turret, and "
         "that lag IS the reported swing")
    need("if (_aiming == t) LeaveAim(" in travel,
         "aiming and travelling exclude each other",
         "a gun can be kept laid while somebody drives - then it never "
         "reaches its cradle and the truck is held for good")
    need("internal static bool TravelLocked(GameObject car)" in mort
         and "internal static bool AnyTravelLock" in mort,
         "seam Mortar.TravelLocked / Mortar.AnyTravelLock",
         "the fire control does not tell the vehicle whether its gun is still "
         "out")

    # --- 7: the travel lock, the driving half (RevivalArtyVehicle.cs).
    prefix = _body(ac, "public static void Prefix(object __instance, ref float __0, ref bool __2)")
    need("ArtyVehicleTravel.Install(harmony);" in ac
         and '"InputAxis"' in ac
         and "typeof(float), typeof(float), typeof(bool)" in ac,
         "the lock sits on VehicleGameSystem::InputAxis, the one way a pedal "
         "reaches a vehicle",
         "the travel lock is not on the game's own input seam")
    need("__0 = 0f;" in prefix and "__2 = true;" in prefix
         and "Mortar.TravelLocked(" in prefix
         and "Mortar.AnyTravelLock" in prefix,
         "no throttle and the handbrake on while the gun is out of its cradle",
         "the lock takes no throttle away, pulls no handbrake, or does not ask "
         "the fire control whose gun is still out")
    need("IsMine" not in ac and "canControl" not in ac,
         "the lock holds the pedals, it does not switch the physics off",
         "the lock reaches for IsMine or canControl - RCC returns out of "
         "FixedUpdate before it brakes, so a locked howitzer would coast down "
         "a slope with nothing to stop it")

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


def check_arty_sync_authority():
    """[20] Master-authoritative settlement artillery emplacement sync."""
    print("[20] Artillery emplacement sync authority (static)")
    mortar_p = os.path.join(ROOT, "RevivalMortar.cs")
    if not os.path.exists(mortar_p):
        bad("RevivalMortar.cs missing")
        return
    s = io.open(mortar_p, encoding="utf-8").read()

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Arty sync: " + why)

    kind3_at = s.find("if (kind == 3)")
    kind3_end = s.find("if (kind == 1", kind3_at)
    kind3 = s[kind3_at:kind3_end] if kind3_at >= 0 and kind3_end > kind3_at else ""

    need('new object[] { "arty-v1", 3, key' in s
         and "spot.x, spot.y, spot.z" in s
         and "normal.x, normal.y, normal.z" in s,
         "kind 3 carries key, position and normal",
         "kind 3 payload is not the required key plus float[6]")
    need("arty.Length == 4" in kind3 and "pose.Length != 6" in s,
         "kind 3 rejects wrong envelope and pose lengths",
         "kind 3 length guard is incomplete")
    need("float.IsNaN(value)" in s and "float.IsInfinity(value)" in s,
         "kind 3 rejects non-finite floats",
         "kind 3 accepts NaN or infinity")
    need('const string prefix = "ndr.arty."' in s
         and "key.Substring(prefix.Length, xDot - prefix.Length)" in s
         and "SceneManager.GetActiveScene().name" in s,
         "kind 3 key is restricted to the complete active scene name",
         "kind 3 does not reject a key from another scene")
    need("if (Master()) return;" in kind3,
         "master ignores its own kind 3 echo",
         "master can apply its own kind 3 echo")

    need("_emplacements[key] = place;" in s
         and "_emplacements.TryGetValue(key, out received)" in s,
         "an early placement is remembered and consumed by Raise",
         "a placement received before Raise is not retained")
    need("bool moved = PutAt(t, place.Spot, place.Normal);" in s
         and "t.Go.transform.position = position;" in s,
         "a late placement moves an already raised local gun",
         "a late placement cannot move an existing gun")
    need("_placed.Remove(id);" in s
         and "_emplacements.ContainsKey(ArtyRoom.Key(centre))" in s,
         "a late placement reopens an exhausted local attempt",
         "a placement arriving after MaxTries can remain permanently unused")

    need("if (!Master()) return;" in s
         and "PublishEmplacement(id, true);" in s,
         "only the master publishes a successful placement",
         "successful placements are not published by the master only")
    need("PublishEmplacement(id, false);" in s
         and "const float EmplacementRepeat" in s,
         "the existing scan repeats placements with a rate limit",
         "late joiners receive no rate-limited placement repeat")
    need("if (!FreeGround(settlement, centre, out spot, out normal)) return false;" in s
         and "local fallback chose emplacement" in s,
         "missing network data falls open to the local search",
         "the previous local emplacement fallback is missing")
    need("ArtyRoom.Key(t.Centre)" in s and "ArtyRoom.Key(centre)" in s,
         "wire identity uses the stable scene/centre key",
         "emplacement sync does not consistently use ArtyRoom.Key")

def check_player_heli():
    """[22] The Mi-8 a player flies.

    The machine is the game's own aid helicopter, and that is the whole risk:
    the same prefab is already flown by the scripted troop landings, owned by
    the Photon master, moved by HelicopterDummy and watched by an orphan sweep
    that removes every helicopter no flight is driving. A parked machine waiting
    for a pilot has to survive all four of those, and a pilot who is not the
    master has to be able to move an object he does not own.

    Nothing here can be seen without the game; the rules below are the ones that
    would fail silently, with a helicopter that vanishes, one that cannot be
    moved, or a player who never gets his body back.
    """
    print("[22] Player-flown helicopter")
    src_p = os.path.join(ROOT, "Revival.PlayerHeli.cs")
    if not os.path.exists(src_p):
        bad("Revival.PlayerHeli.cs is missing - nothing can be flown")
        return
    raw = io.open(src_p, "rb").read()
    heli = raw.decode("utf-8", "replace")
    code = _code(heli)

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("PlayerHeli: " + why)

    plug = read("RevivalPlugin.cs")
    cam = read("Revival.CameraTurret.cs")
    troop = read("RevivalTroopInsertion.cs")
    sync = read("sync_public.py")

    # --- file rule: UTF-8 without BOM, and outside ASCII only the Cyrillic of
    # the player-facing Loc.T lines (AGENTS.md).
    need(not raw.startswith(b"\xef\xbb\xbf"), "no BOM",
         "Revival.PlayerHeli.cs starts with a BOM")
    strange = sorted(set(c for c in heli
                         if ord(c) > 126 and not 0x400 <= ord(c) <= 0x4FF))
    need(not strange,
         "outside ASCII only Cyrillic (player text)",
         "Revival.PlayerHeli.cs holds characters that are neither ASCII nor "
         "Cyrillic: " + " ".join("U+%04X" % ord(c) for c in strange))

    # --- the marker. The troop insertion sweeps every helicopter carrying ITS
    # marker that no scripted flight is driving, ten seconds after it appears -
    # which is exactly what a machine parked for a pilot looks like.
    need('Marker = "ndr-flyheli-1"' in code
         and "ndr-troopheli-1" not in code,
         "an own instantiation marker, not the troop one",
         "the flyable machine carries the troop marker - the troop orphan "
         "sweep removes it ten seconds after it is put down")
    need("CleanOrphans" not in code, "the troop orphan sweep is not reused",
         "the troop orphan sweep is called from here")

    # --- the vanilla mover must never take over. HelicopterDummy's own
    # movement returns at once while startPosition is zero.
    need("_fStart.SetValue(mover, Vector3.zero);" in code,
         "the game's own helicopter movement is left idle",
         "startPosition is not zeroed - the game moves the machine too")
    need("startPosition" in troop,
         "the same rule the scripted flight relies on is still in place",
         "RevivalTroopInsertion.cs no longer zeroes startPosition")

    # --- the ground under a flying helicopter is never a downward ray: it would
    # find the machine's own hull collider.
    floor = _body(code, "static bool Floor(Vector3 at, out float y)")
    fly = _body(code, "static void Fly()")
    need(floor != "" and "RaycastObject" not in floor
         and "RevivalTroopInsertion.TerrainHeight" in floor,
         "the floor comes from height data and the pads, not from a ray",
         "the flight casts a ray downwards - it finds its own hull and the "
         "machine sits on itself")
    need("RaycastObject" not in fly,
         "no ray per frame of flight",
         "the per-frame flight casts rays")
    need("Helipads.Snap(" in floor,
         "a helipad deck is a floor the machine can land on",
         "a helicopter sinks through an authored helipad deck")

    # --- authority. Photon lets only the master instantiate and destroy a scene
    # object, and only the owner's transform is replicated.
    spawn = _body(code, "static void Spawn()")
    need("if (!RevivalTroopInsertion.MasterClient())" in spawn
         and "Net.Send(Net.SpawnRequest," in spawn,
         "a client who is not the master asks it for a machine",
         "a client who is not the master calls InstantiateSceneObject itself")
    remove = _body(code, "static void Remove(GameObject go, bool say)")
    need("if (!RevivalTroopInsertion.MasterClient())" in remove
         and "Net.Send(Net.RemoveRequest," in remove,
         "removal goes through the master as well",
         "a client who is not the master destroys a scene object itself")
    event = _body(code, "public static void OnPhotonEvent(byte code, object content, int sender)")
    need(event.count("if (!RevivalTroopInsertion.MasterClient()) return;") >= 3,
         "spawn, pose and removal are acted on by the master only",
         "a message that only the master may act on is acted on by everybody")
    need("Interpolator(go, false);" in code and "Interpolator(go, true);" in code,
         "a pilot who is not the master silences his own transform sync",
         "a pilot who is not the master fights the interpolator - his own "
         "machine is pulled back every frame")

    # --- the body and the camera are BORROWED, and both have to come back.
    leave = _body(code, "static void Leave(bool byKey)")
    need("finally" in leave and "CameraOwner.Release(CameraOwner.Heli);" in leave,
         "the view is given back on every way out",
         "a way out of the machine keeps the camera - the player then looks "
         "through a camera nobody moves")
    need("if (PlayerHeli.Aboard) __result = true;" in code,
         "the body is frozen through the game's own 'not now' predicates",
         "the body is frozen by switching scripts off - then a flight that "
         "ends badly leaves a player who cannot walk")
    need("_hadBody" in code and "the flight ends" in heli,
         "a body that dies or is replaced ends the flight",
         "death or respawn leaves the player locked into a machine")

    # --- the collective is what makes it a helicopter and not a drone.
    need("if (!up && !down) vertical -= vertical" in code,
         "with no collective input the machine holds its height",
         "the machine sinks whenever nothing is pressed - that is a drone")

    # --- the balance, read out of the config defaults.
    thrust = _bind_number(heli, "PlayerHeli", "Thrust")
    drag = _bind_number(heli, "PlayerHeli", "Drag")
    top = _bind_number(heli, "PlayerHeli", "MaxSpeed")
    if thrust is None or drag is None or top is None:
        bad("PlayerHeli: Thrust, Drag or MaxSpeed is not a plain default any more")
    else:
        cruise = thrust / drag
        need(0 < cruise < top,
             "cruise %.0f m/s comes from Thrust/Drag and stays under the cap %.0f"
             % (cruise, top),
             "Thrust/Drag = %.0f m/s is at or above MaxSpeed %.0f - the hard cap "
             "decides the handling instead of the air" % (cruise, top))

    # --- the flight has a rate limit on every axis. The first version gave the
    # collective a free hand: nothing damped the vertical speed while space was
    # held, so it ran up to the ONE shared MaxSpeed - 50 m/s straight up. Climb
    # and sink need their own ceilings, and they have to be helicopter numbers.
    climb = _bind_number(heli, "PlayerHeli", "ClimbRate")
    sinkrate = _bind_number(heli, "PlayerHeli", "SinkRate")
    if climb is None or sinkrate is None:
        bad("PlayerHeli: ClimbRate or SinkRate is not a plain default any more")
    elif top is None:
        pass                      # already reported above
    else:
        need(0 < climb <= 15 and 0 < sinkrate <= 15,
             "climb %.0f m/s and sink %.0f m/s are helicopter rates"
             % (climb, sinkrate),
             "climb %.0f / sink %.0f m/s is a lift, not a helicopter" % (climb, sinkrate))
        need("Mathf.Clamp(vertical, floorSink, climb)" in code,
             "the vertical speed is clamped to those two, not to MaxSpeed",
             "the vertical speed is not clamped separately - the horizontal cap "
             "decides how fast the machine climbs again")
    need("_yawRate = Mathf.Lerp(_yawRate, want" in code,
         "the mouse asks for a turn RATE, it does not snap the heading",
         "Steer writes the heading straight from the mouse delta - eleven "
         "tonnes then turn as fast as the hand moves")

    # --- no downward ray anywhere in the flight. The floor is height data; the
    # crash test is a FORWARD cast, and it has to stay one, because a ray cast
    # downwards finds the machine's own hull.
    impact = _body(code, "static bool Impact(Vector3 from, Vector3 to)")
    need(impact != "" and "Vector3.down" not in impact
         and "Vector3.down" not in fly,
         "the crash test casts forward; nothing in the flight casts down",
         "the flight or the crash test casts downwards - it finds its own hull")

    # --- the engine. The prefab's own Start plays the rotor loop and never
    # stops it, which is why a parked machine used to roar to itself for ever.
    prepare = _body(code, "internal static void Prepare(GameObject go)")
    need("EngineApply(go, false);" in prepare,
         "a machine arrives cold on every client",
         "a machine put down is not silenced - the prefab's own rotor loop "
         "runs for ever")
    need("if (_pilot && !Burning(go)) SetEngine(false);" in leave,
         "the engine does not keep running behind the last man out",
         "leaving the machine leaves the engine on")

    # --- the crash is FireEffect's, not a second private effect, and since
    # 2026-09-21 it is FireEffect's biggest size: a burning aircraft that looked
    # like a burning car was the complaint. The vehicle wreck stays behind it as
    # the fallback for a build with no aircraft fire.
    burn = _body(code, "static void Burn(GameObject go, Vector3 where)")
    need("FireEffect.SpawnHeliBlast(" in burn
         and "FireEffect.SpawnHeliFire(" in burn
         and "FireEffect.SpawnWreck(" in burn,
         "a downed machine gets the aircraft blast and fire, vehicle fire as "
         "the fallback",
         "the helicopter wreck does not use FireEffect - it would look like "
         "nothing else in the world")
    fire = _code(read("Revival.WreckFire.cs"))
    need("public static bool SpawnHeliFire(" in fire
         and "static void HeliRauchstoss(" in fire,
         "the aircraft fire exists and opens with a one-shot smoke burst",
         "the aircraft fire is missing, or its column has no opening burst - "
         "the smoke then needs twenty seconds to arrive and the fire reads as "
         "starting long after the bang")
    need("Ausbruch(ps, " in _body(fire, "static void HeliRauchstoss(GameObject root, Material mat)"),
         "that burst is a burst and not another continuous rate",
         "the opening smoke is emitted over time - it is not instant")
    heli_fire = _body(fire, "public static bool SpawnHeliFire(GameObject heli)")
    need(heli_fire.count("HeliFlammen(root,") >= 3,
         "the fire is laid out along the airframe, not in one spot",
         "the aircraft burns from a single point - a 38-unit hull then shows "
         "one bonfire in the middle of it")
    # --- where a bed sits is the machine's business, which way it throws is
    # gravity's. The root used to keep world axes, so the beds ran along world
    # Z and crossed a hull that was pointing anywhere else.
    need("root.transform.localRotation = Quaternion.identity;" in heli_fire
         and "root.transform.localPosition = Vector3.zero;" in heli_fire,
         "the fire lies along the fuselage whichever way the wreck points",
         "the fire root keeps world axes - its beds cross the hull instead "
         "of running along it")
    need("Aufrecht(ps);" in _body(
             fire, "static void HeliFlammen(GameObject root, Material mat,"),
         "a flame bed is placed in the hull and still throws world upwards",
         "the emitters turn with the wreck - a machine on its roof fires its "
         "flames into the ground")
    # --- more fire, less smoke (2026-09-22) is the CRASH's mixture only.
    # FireHook puts every explosion in the game through Spawn.
    need("Blast(point, Mathf.Clamp(radius, 1.5f, 20f), 1f, 1f);" in fire,
         "an ordinary explosion keeps the mixture it has always had",
         "the game's own explosions were re-weighted along with the "
         "helicopter - every grenade would change with it")
    need("Blast(point, Mathf.Clamp(radius, 6f, 60f), 1.55f, 0.40f);" in fire,
         "the crash ball is weighted towards fire and away from smoke",
         "the helicopter blast is back on the ordinary mixture")

    # --- the attitude. Burn used to rebuild the rotation from the heading with
    # a fixed nose and bank, which stood a machine that came down on its side
    # upright in one frame. Nothing here may reconstruct a rotation again.
    need("Quaternion.LookRotation" not in burn,
         "the wreck keeps the attitude it arrived with",
         "Burn rebuilds the rotation - a machine that comes down across the "
         "ground stands itself up the instant it touches")
    need("go.AddComponent<HeliWreckSettle>()" in burn
         and "Interpolator(go, false);" in burn,
         "the wreck settles further over and is taken off the transform sync",
         "the wreck is not settled, or it is left on the interpolator and the "
         "host's last flying pose pulls it upright again")
    # --- the wreck that hung in the air (2026-09-22: "das wrack schwebt
    # mehrere meter ueber dem boden"). The old settle drove the nose FURTHER
    # down and then stood a 38-unit hull box on its single deepest corner,
    # which holds the machine's own origin some six metres up. Four things
    # keep that from coming back.
    settle = _body(code, "public sealed class HeliWreckSettle : MonoBehaviour")
    need(settle != "" and "static float Flat(" in settle
         and "static float Over(" in settle,
         "the fuselage lies down, on one of the four attitudes a machine "
         "comes to rest in",
         "the settle does not lay the hull down - a machine balanced on its "
         "nose holds its own origin metres above the ground")
    need("_under[i]" in settle and "static Vector3 Corner(int i)" in settle
         and "if (i == 0 || y > best) best = y;" in settle,
         "the rest height is every hull corner against the ground under THAT "
         "corner, highest contact winning",
         "the settle rests on one floor sample or on its lowest corner - the "
         "wreck then hangs over a rise or is buried in a slope")
    drop = _body(settle, "void Drop()")
    legacy_drop = ("MeshRenderer" in drop and "at.y -= gap;" in drop
                   and "at.y +=" not in drop)
    wreck_place = read("Revival.AircraftWreck.cs")
    geometry_drop = (
        "AircraftWreck.Place(transform, transform, false);" in drop
        and all(s in wreck_place for s in (
            "f.sharedMesh.vertices", "matrix.MultiplyPoint3x4(vertices[v])",
            "needed = y - support[i].y", "shift - Bed", "Physics.RaycastAll",
            "QueryTriggerInteraction.Ignore", "c.transform.IsChildOf(aircraft)",
            "RevivalTroopInsertion.TerrainHeight(at, out y)"))
        and os.path.exists(os.path.join(ROOT, "research", "aircraft_wreck_check.py")))
    need(legacy_drop or geometry_drop,
         "what is DRAWN has the last word: bounds lowering or actual mesh support on solid ground",
         "nothing measures the swapped wreck mesh against its ground")
    if geometry_drop:
        need("void Update(" not in wreck_place and "FindObjectsOfType" not in wreck_place
             and "internal const float Bed = 0.15f * 2.8f;" in wreck_place,
             "W1: wreck support is crash-only, excludes the carrier and beds the mesh 15 cm",
             "W1: wreck placement polls or lacks the specified bedding")
    need("HeliWreckModel.Apply(go);" in burn and "settle.Begin(floor);" in burn
         and burn.index("HeliWreckModel.Apply(go);")
             < burn.index("settle.Begin(floor);"),
         "the broken airframe is swapped in before the wreck is put down",
         "the settle measures the intact prefab instead of the mesh the "
         "player actually sees")
    need("&& !Burning(go)" in leave and "Interpolator(go, true);" in leave,
         "leaving a WRECK does not switch the interpolator back on",
         "Leave hands the wreck back to the transform sync a moment after "
         "Burn took it off")

    # --- the broken model, out of the game's own assets.
    model = _body(code, "internal static class HeliWreckModel")
    need('"mi-8_rusty"' in model,
         "the wreck is the game's own broken Mi-8 (mi-8_rusty_int)",
         "the crash does not reach for the game's broken helicopter model")
    need('"mchs"' in model and '"military"' in model,
         "only the two intact hulls are swapped, everything else is hidden",
         "the model swap matches more than the hull - the interior would be "
         "drawn as a second airframe inside the first")
    need("FindHulls(filters)" in model and "candidates > 0" in model
         and "if (!hulls[i])" in model,
         "the wreck swap proves a hull replacement before hiding renderers",
         "the wreck can hide every renderer when prefab and mesh names differ")
    need("largest * 0.25f" in model and "Auxiliary(mf, mesh)" in model,
         "an imported hull with a different mesh name is found by safe bounds",
         "the broken model only works when Unity preserves the prefab name")
    need("r.material" in model and "r.sharedMaterial =" not in model,
         "the scorch fallback edits a renderer copy, never a shared material",
         "the fallback writes to a shared material - every Mi-8 in the world "
         "would be scorched with it")
    need("Net.Send(Net.Crashed," in code and "Net.Send(Net.EngineState," in code,
         "engine and crash are told to every client",
         "the rotor, the sound or the fire is decided locally - every other "
         "client then sees a machine that is still flying")

    # --- leaving in the air is a descent, not an explosion at altitude. The
    # model has to remain visible until the floor, carry a world-space trail,
    # and only then hand the hull to the ordinary wreck visuals and a spatial
    # impact sound. The same reliable start event makes all peers see it.
    abandon = _body(code, "static void Abandon(GameObject go, Vector3 drift, bool broadcast)")
    fall = _body(code, "public sealed class HeliCrashFall : MonoBehaviour")
    finish = _body(code, "internal static void FinishAbandonedCrash(GameObject go, Vector3 where)")
    jump = _body(code, "static void Jump()")
    need("Abandon(go, drift, true);" in jump and "Crash(go, go.transform.position);" not in jump,
         "an abandoned machine descends instead of exploding in the air",
         "Jump still turns the helicopter into a wreck at its airborne position")
    need(abandon != "" and "Net.Send(Net.Crashed," in abandon
         and "go.AddComponent<HeliCrashFall>()" in abandon,
         "the fall starts reliably on every client",
         "the abandoned-helicopter fall is local to one client")
    need(fall != "" and "Gravity * k * dt" in fall
         and "transform.Rotate(" in fall and "CrashFloor(" in fall,
         "the airframe falls, drifts and rolls until it reaches the floor",
         "the crash descent has no gravity, visible attitude or ground test")
    need("FireEffect.SpawnDroneFire(_trail, true);" in fall
         and "FireEffect.StopEmitting(_trail);" in fall,
         "the descent carries a smoke and fire trail that ends on impact",
         "the falling helicopter has no bounded trail effect")
    # The bang used to be played here, and ONLY here - so a machine flown into
    # a mast made no sound at all. It moved into Burn, which every crash path
    # runs exactly once on every peer.
    need(finish != "" and "Burn(go, where);" in finish,
         "the delayed impact becomes the normal wreck",
         "the delayed impact does not become the normal wreck")
    need("HeliCrashSound.Play(where);" in burn
         and "HeliCrashSound.Play(" not in finish,
         "every crash bangs, because the sound sits in Burn and only there",
         "the crash sound is not in Burn - a machine flown into something is "
         "then silent, or the abandoned fall plays it twice")
    sound = _body(code, "internal static class HeliCrashSound")
    need(sound.count("Source(at,") >= 2,
         "the bang is two summed sources - one AudioSource is already at full "
         "volume",
         "the crash plays one source - it cannot be made louder than it was")

    # --- the event window. Every feature of this plugin raises Photon events in
    # its own band; two bands that overlap is a bug nobody sees until two
    # features are used at once.
    base = _bind_number(heli, "PlayerHeli", "NetworkEventCode")
    if base is None:
        bad("PlayerHeli: NetworkEventCode is not a plain default any more")
    else:
        taken = {}
        known = [("Troops", _bind_number(troop, "Troops", "NetworkEventCode"), 3),
                 ("Drone", _bind_number(plug, "Drone", "EventCode"), 5),
                 ("Turret", _bind_number(plug, "Turret", "NetworkEventCode"), 1),
                 ("Admin", _bind_number(plug, "Admin", "NetworkEventCode"), 1),
                 ("CrewDrone", _bind_number(plug, "Patrol", "CrewDroneEventCode"), 1),
                 ("SurvDrone", _bind_number(read("RevivalDroneGear.cs"),
                                            "DroneGear", "SurveillanceEventCode"), 1),
                 ("Mortar", _bind_number(read("RevivalMortar.cs"),
                                         "Mortar", "NetworkEventCode"), 6)]
        for name, start, span in known:
            if start is None:
                continue
            for c in range(int(start), int(start) + span):
                taken.setdefault(c, name)
        clash = [(c, taken[c]) for c in range(int(base), int(base) + 6) if c in taken]
        need(not clash,
             "event codes %d-%d are free" % (base, base + 5),
             "event codes collide with another feature: "
             + ", ".join("%d is %s" % (c, who) for c, who in clash))

    # --- seams outside this file.
    need("PlayerHeli.BindConfig(Config);" in plug
         and "PlayerHeli.Install(_harmony);" in plug
         and "PlayerHeli.Tick();" in plug
         and "PlayerHeli.Draw();" in plug
         and "PlayerHeli.LateFrame();" in plug,
         "seams BindConfig/Install/Tick/Draw/LateFrame in RevivalPlugin.cs",
         "a seam is missing in RevivalPlugin.cs: PlayerHeli")
    need("public const int Heli = 5;" in cam
         and "else if (_owner == Heli) PlayerHeli.LateTick();" in cam,
         "the pilot's view is an owner of the shared camera",
         "the helicopter view is not dispatched by CameraOwner - two features "
         "would write the camera in the same frame")
    need("PlayerHeli.LateFrame();" in _body(plug, "void LateUpdate()"),
         "everyone aboard is placed in LateUpdate, after the animator",
         "the seat is written in Update - the animator puts the man back on "
         "the ground before anything is drawn")
    need('"Revival.PlayerHeli.cs"' in sync,
         "Revival.PlayerHeli.cs goes into the public repository",
         "Revival.PlayerHeli.cs is missing from sync_public.py - the public "
         "repo does not build without it")

def check_player_an2():
    """[22b] The An-2 a player flies (Revival.PlayerAn2.cs, docs/ai/tasks/an2-flight.md).

    The order (task 3d31e25fcb): a fixed-wing flight model - throttle, pitch,
    roll, rudder; lift from speed and a stall below about 60 km/h; takeoff and
    landing on the runway or on grass; a fuel gauge; a crash when landing too
    hard - with the Mi-8's vehicle, seat, camera and crash plumbing reused and
    its fall guard protecting a seated pilot; one An-2 at the H1 apron behind a
    config key that is off by default; the scale proven against the player
    capsule, not the Mi-8. What can be held without the game:
      - the carrier is the Mi-8 prefab under an OWN marker, drawn as nothing;
      - the fall guard and the input locks ask PlayerAn2 as well;
      - the rig is at 2.8 u/m, the meshes measure the published span and
        length, and the plugin's K is the same 2.8;
      - the flight equations, run by research/an2_flight_sim.py, stall between
        45 and 65 km/h and take off on grass inside 300 m;
      - the seams, the camera owner, the event window and the file lists.
    """
    print("[22b] Player-flown An-2")
    src_p = os.path.join(ROOT, "Revival.PlayerAn2.cs")

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("PlayerAn2: " + why)

    if not os.path.exists(src_p):
        bad("Revival.PlayerAn2.cs is missing - the An-2 cannot be flown")
        return
    raw = io.open(src_p, "rb").read()
    an2 = raw.decode("utf-8", "replace")
    code = _code(an2)

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    import re
    heli = read("Revival.PlayerHeli.cs")
    plug = read("RevivalPlugin.cs")
    cam = read("Revival.CameraTurret.cs")
    sync = read("sync_public.py")
    build = read("build.ps1")

    def bind(key):
        m = re.search(r'Bind\(S,\s*"%s"\s*,\s*(-?[0-9.]+|true|false)f?\s*,' % re.escape(key), an2)
        return None if m is None else m.group(1)

    need(not raw.startswith(b"\xef\xbb\xbf"), "no BOM", "Revival.PlayerAn2.cs starts with a BOM")
    strange = sorted(set(c for c in an2 if ord(c) > 126 and not 0x400 <= ord(c) <= 0x4FF))
    need(not strange, "outside ASCII only Cyrillic (player text)",
         "characters that are neither ASCII nor Cyrillic: "
         + " ".join("U+%04X" % ord(c) for c in strange))

    # --- the carrier and its marker.
    need('Marker = "ndr-an2-1"' in code and "ndr-flyheli-1" not in code
         and "ndr-troopheli-1" not in code,
         "an own instantiation marker (neither the Mi-8's nor the troop one)",
         "the An-2 shares a marker - another feature's sweep would take it")
    need("_fStart.SetValue(mover, Vector3.zero);" in code,
         "the carrier's own helicopter movement is left idle",
         "startPosition is not zeroed - the game moves the carrier")
    need("drawn[i].enabled = false;" in code and "rotor.Kill();" in code,
         "the carrier Mi-8 is drawn as nothing and its rotor and sound are killed",
         "the carrier Mi-8 is still drawn or still runs its rotor")

    # --- on by default (Q4: every gameplay feature on), an older file
    # switched on once through the settings layout stamp; the apron stand.
    need(bind("Enabled") == "true"
         and "Settings.GameplayOn(cfg, CfgEnabled);" in _body(code, "internal static void BindConfig("),
         "[PlayerAn2] Enabled is on by default and an older file is switched on once",
         "[PlayerAn2] Enabled is not on by default or lacks the Settings.GameplayOn migration")
    # --- the admin spawn never refuses silently: a key that is off is named
    # and switched on for the session (Q4).
    ready = _body(code, "internal static string SpawnReadyInFront(")
    need("ForceOn()" in ready and "An2Bombs.ForceOn()" in ready
         and "switched off" not in ready,
         "the admin's ready An-2 names an off key and spawns anyway, with bombs",
         "the admin's ready An-2 still refuses when [PlayerAn2] Enabled or [Gameplay] An2Bombs is off")
    recipe = read("unity/EastTile/Content/east_airfield.json")
    m = re.search(r'"id":\s*"AN".*?"x":\s*([0-9.]+),\s*"z":\s*([0-9.]+)', recipe)
    m2 = re.search(r'Apron = new Vector3\(([0-9.]+)f, 0f, ([0-9.]+)f\)', code)
    need(m is not None and m2 is not None
         and abs(float(m.group(1)) - float(m2.group(1))) < 1
         and abs(float(m.group(2)) - float(m2.group(2))) < 1,
         "the apron spawn stands on the greybox An-2 stand (east_airfield.json AN)",
         "the apron spawn is not where the airfield recipe puts the An-2")
    need('StandInName = "AN An-2 (nose east)"' in code,
         "the greybox stand-in is hidden while a flyable An-2 exists",
         "the greybox stand-in is not named - two aeroplanes on one stand")

    # --- scale: the capsule, not the Mi-8.
    rig_p = os.path.join(ASSETS, "an2_rig.txt")
    rig = io.open(rig_p, encoding="ascii").read() if os.path.exists(rig_p) else ""
    kv = {}
    for line in rig.splitlines():
        p = line.split()
        if p and not p[0].startswith("#"):
            kv.setdefault(p[0], []).append(p[1:])
    need(os.path.exists(os.path.join(ROOT, "an2_import.py"))
         and os.path.exists(os.path.join(ASSETS, "an2_gameready", "an2_flyable.glb")),
         "an2_import.py and the source GLB are in the repository",
         "an2_import.py or assets/an2_gameready/an2_flyable.glb is missing")
    scale = kv.get("scale", [[None, None]])[0]
    need("internal const float K = 2.8f;" in code and scale[0] == "2.800" and scale[1] == "5.000",
         "2.8 u/m in the plugin and the rig: a 5.0 u capsule is a 1.79 m man",
         "the plugin and an2_rig.txt disagree on the world scale, or it is not 2.8")
    try:
        span = float(kv["span"][0][0])
        length = float(kv["length"][0][0])
        need(abs(span - 18.18) / 18.18 < 0.005 and abs(length - 12.74) / 12.74 < 0.005,
             "the meshes measure the published span %.2f m and length %.2f m" % (span, length),
             "an2_rig.txt span/length are off the published 18.18/12.74 m")
        cab = kv["cabin"][0]
        cabin = float(cab[1]) - float(cab[0])
        need(cabin * 2.8 > 5.0,
             "the cabin (%.2f m = %.1f u) takes a standing 5.0 u capsule" % (cabin, cabin * 2.8),
             "the cabin is lower than the player capsule - the scale is wrong")
    except (KeyError, IndexError, ValueError):
        bad("PlayerAn2: an2_rig.txt lacks span/length/cabin - python an2_import.py")
    pivots = set(p[0] for p in kv.get("pivot", []))
    need(pivots >= {"prop", "aileron_l", "aileron_r", "elevator", "rudder"}
         and set(p[0] for p in kv.get("contact", [])) == {"l", "r", "tail"}
         and "parked" in kv and any(p[0] == "-1" for p in kv.get("seat", [])),
         "an2_rig.txt: five hinges, three wheels, the parked pitch, the pilot's seat",
         "an2_rig.txt is incomplete - python an2_import.py")
    need('"an2_rig.txt"' in build and '"an2_body.ndmesh"' in build,
         "build.ps1 installs the An-2 assets", "build.ps1 does not install the An-2 assets")

    # --- the flight model: controls, lift, stall, fuel, crash.
    need("KeyCode.Space" in code and "Axis(KeyCode.S, KeyCode.W)" in code
         and "Axis(KeyCode.D, KeyCode.A)" in code and "Axis(KeyCode.E, KeyCode.Q)" in code,
         "throttle, pitch, roll and rudder each have their keys",
         "one of throttle/pitch/roll/rudder has no input")
    need("LiftShape(aoa, crit)" in code and "(speed / vs) * (speed / vs)" in code,
         "lift grows with the square of the airspeed over the stall speed",
         "the lift does not come from the airspeed")
    need(bind("StallSpeed") == "60" and "_stalled" in code,
         "the stall speed is 60 km/h and a stall is detected",
         "StallSpeed is not 60 km/h or no stall state")
    need(bind("GrassFriction") is not None and "Paved(" in code,
         "concrete and grass roll differently, and both carry a takeoff",
         "the ground roll does not know grass from concrete")
    need("vis.Fuel - burn * dt" in code and "Gauge(" in code and "SetEngine(false)" in code,
         "fuel burns with the throttle, the gauge is drawn, an empty tank stops the engine",
         "no fuel burn, no gauge or no engine stop on an empty tank")
    need("static bool Touchdown(Vector3 arrival)" in code and '"CrashSinkRate", 4f' in code
         and "Crash(_plane, _plane.transform.position);" in _body(code, "static bool Touchdown(Vector3 arrival)"),
         "a touchdown faster than 4 m/s of sink is a crash",
         "a hard landing does not crash the aeroplane")
    need("FireEffect.SpawnHeliFire(anchor)" in code and "HeliCrashSound.Play(where);" in code
         and "Parachute.Jump(" in code,
         "the crash reuses the aircraft fire, the crash sound and the parachute jump",
         "the crash does not reuse the Mi-8's fire/sound/parachute path")

    # The equations, flown by the sim. Its arithmetic mirrors the plugin.
    sim_p = os.path.join(ROOT, "research", "an2_flight_sim.py")
    if not os.path.exists(sim_p):
        bad("PlayerAn2: research/an2_flight_sim.py is missing")
    else:
        try:
            import importlib.util
            spec = importlib.util.spec_from_file_location("an2_flight_sim", sim_p)
            sim = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(sim)
            need(("Induced = %.1ff;" % sim.INDUCED) in code,
                 "the sim and the plugin use the same induced drag (%.1f)" % sim.INDUCED,
                 "research/an2_flight_sim.py and the plugin disagree on the induced drag")
            sv, lost, _ = sim.stall()
            need(45.0 <= sv <= 65.0, "the wing stalls at %.0f km/h (order: below ~60)" % sv,
                 "the model stalls at %.0f km/h, not near 60" % sv)
            grass = sim.takeoff(False, 1.0)
            need(grass is not None and grass[0] < 300.0,
                 "takeoff from grass in %.0f m (short field)" % (grass[0] if grass else -1),
                 "no short takeoff from grass")
            need(sim.landing(sim.CFG["CrashSinkRate"] + 0.5) == "crash" and sim.landing(1.5) == "ok",
                 "hard landing crashes, a normal one does not",
                 "the touchdown rule does not separate a landing from a crash")
        except Exception as ex:  # noqa: BLE001
            bad("PlayerAn2: research/an2_flight_sim.py failed: %s" % ex)

    # --- the Mi-8's plumbing asks for the An-2 too.
    guard = _body(heli, "public static bool FallPrefix(object __instance)")
    need("PlayerAn2.Aboard" in guard and "PlayerAn2.Body" in _body(heli, "static bool Mine(object instance)"),
         "the fall guard holds a seated An-2 pilot out of the game's fall state",
         "HeliBodyGuard does not protect an An-2 pilot - he can die of a fall in his seat")
    need("if (PlayerAn2.Aboard) __result = true;" in heli and "if (PlayerAn2.Flying) __result = true;" in heli,
         "the body and the orbit camera are locked through the game's own predicates",
         "the input locks do not know the An-2 - the body walks along")
    need("PlayerAn2.DiedAboard();" in heli, "a death aboard ends the An-2 flight",
         "a death aboard does not reach the An-2")
    need("HeliInputHook.Install(harmony);" in code and "HeliBodyGuard.Install(harmony);" in code,
         "the An-2 installs the shared locks itself (once, with the Mi-8 off too)",
         "with [PlayerHeli] off the An-2 pilot has no fall guard")
    need("public const int An2 = 7;" in cam and "else if (_owner == An2) PlayerAn2.LateTick();" in cam,
         "the pilot's view is an owner of the shared camera",
         "CameraOwner does not know the An-2")
    for seam in ("PlayerAn2.BindConfig(Config);", "PlayerAn2.Install(_harmony);",
                 "PlayerAn2.Tick();", "PlayerAn2.LateFrame();", "PlayerAn2.Draw();"):
        need(seam in plug, "seam " + seam, "seam missing in RevivalPlugin.cs: " + seam)
    need('"Revival.PlayerAn2.cs"' in sync and '"an2_import.py"' in sync,
         "Revival.PlayerAn2.cs and an2_import.py go into the public repository",
         "sync_public.py lacks Revival.PlayerAn2.cs or an2_import.py")

    # --- the event window: 150..157 against every other band (+6/+7 are the
    # repair state of Revival.An2Repair.cs).
    base = bind("NetworkEventCode")
    if base is None:
        bad("PlayerAn2: NetworkEventCode is not a plain default")
    else:
        base = int(float(base))
        troop = read("RevivalTroopInsertion.cs")
        known = [("Troops", _bind_number(troop, "Troops", "NetworkEventCode"), 3),
                 ("PlayerHeli", _bind_number(heli, "PlayerHeli", "NetworkEventCode"), 6),
                 ("Drone", _bind_number(plug, "Drone", "EventCode"), 5),
                 ("Turret", _bind_number(plug, "Turret", "NetworkEventCode"), 1),
                 ("Admin", _bind_number(plug, "Admin", "NetworkEventCode"), 1),
                 ("CrewDrone", _bind_number(plug, "Patrol", "CrewDroneEventCode"), 1),
                 ("SurvDrone", _bind_number(read("RevivalDroneGear.cs"),
                                            "DroneGear", "SurveillanceEventCode"), 4),
                 ("Mortar", _bind_number(read("RevivalMortar.cs"), "Mortar", "NetworkEventCode"), 6),
                 ("Crocodile", 164, 1), ("Stinger", 191, 1), ("Gepard", 193, 1), ("ApMine", 196, 1)]
        taken = {}
        for name, start, span in known:
            if start is None:
                continue
            for c in range(int(start), int(start) + span):
                taken.setdefault(c, name)
        clash = [(c, taken[c]) for c in range(base, base + 8) if c in taken]
        need(not clash and base + 7 <= 199, "event codes %d-%d are free" % (base, base + 7),
             "event codes collide: " + ", ".join("%d is %s" % (c, w) for c, w in clash))


def check_vehicle_spawns():
    """[22f] N9a - the find locations of the new vehicles
    (assets/editor/vehicle_spawns.json, docs/ai/tasks/vehicle-spawns.md).

    Data for N9b. research/vehicle_spawns_check.py proves every kind has an
    east and a main-map spot, trucks are common, and every measured east spot
    keeps its vehicle clear of the airfield and town greybox blocks.
    """
    print("[22f] Vehicle find locations (N9a)")
    import subprocess
    data = os.path.join(ROOT, "assets", "editor", "vehicle_spawns.json")
    check = os.path.join(ROOT, "research", "vehicle_spawns_check.py")
    if not (os.path.exists(data) and os.path.exists(check)):
        bad("assets/editor/vehicle_spawns.json or research/vehicle_spawns_check.py missing")
        return
    r = subprocess.run([sys.executable, check], cwd=ROOT, capture_output=True, text=True)
    if r.returncode == 0 and r.stdout.strip().endswith("PASS"):
        ok("vehicle find locations pass research/vehicle_spawns_check.py")
    else:
        bad("research/vehicle_spawns_check.py fails: "
            + "; ".join(l for l in r.stdout.splitlines() if l.startswith("FAIL "))[-400:]
            + r.stderr.strip()[-200:])


def check_mercs():
    """[22g] B3 - mercenaries phase 1 (docs/ai/tasks/mercenaries.md, B3 report).

    The shipped hire profiles (assets/editor/mercs.json) pass the same rules
    the web editor saves with (mercdef.py: ids in the catalogue with the right
    slot, prices > 0, upkeep <= price, traits 0..50, cap 1..10), and the
    plugin's built-in fallback table is exactly their runtime TSV, so a client
    that never reached /runtime/mercs offers the same cards the editor shows.
    """
    print("[22g] Mercenaries (B3 phase 1)")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Mercenaries: " + why)

    try:
        sys.path.insert(0, ROOT)
        import mercdef
    except Exception as ex:
        bad("mercdef.py does not import: %s" % ex)
        return
    problems = mercdef.validate_section(mercdef.defaults(), mercdef._catalogue())
    if problems:
        bad("assets/editor/mercs.json: " + "; ".join(problems)[:400])
    else:
        ok("assets/editor/mercs.json passes mercdef (catalogue ids, prices, traits, cap)")
    src = read("Revival.Mercs.cs")
    start = src.find("static readonly string[] DefaultProfiles")
    block = src[start:src.find("};", start)] if start >= 0 else ""
    rows = re.findall(r'"((?:[^"\\]|\\.)*)"', block)
    rows = [r.replace("\\t", "\t") for r in rows]
    want = [l for l in mercdef.to_tsv({}).splitlines() if l and not l.startswith("#")]
    need(rows == want, "plugin fallback profiles equal mercdef.to_tsv(defaults)",
         "Revival.Mercs.cs DefaultProfiles differ from assets/editor/mercs.json - "
         "regenerate the block from python mercdef.py")
    need("7700" in src and "MsgStorageListRecieve" in src,
         "roster channel on storageId 7700 (server-side roster, phase 1S)",
         "Revival.Mercs.cs lost the storageId 7700 roster channel")
    need("NDR-MERCS-1" in read("Revival.LiveRoutes.cs") and "/runtime/mercs" in read("routeeditor.py"),
         "editor profiles published on /runtime/mercs and read by LiveRoutes",
         "the /runtime/mercs channel is not wired on both ends")
    need('"Mercs.Tick"' in read("RevivalFrameProfiler.cs") and "Mercs.Tick()" in read("RevivalPlugin.cs"),
         "Mercs.Tick has its own F6 slot",
         "Mercs.Tick is not measured in F6")
    # G O1: orders reach the picked mercs (else the squad minus gun/radar
    # crews); FETCH sends one. Production core + wiring, offline.
    import subprocess
    check = os.path.join(ROOT, "research", "merc_order_targets_check.py")
    if os.path.exists(check):
        try:
            r = subprocess.run([sys.executable, check], capture_output=True, text=True, errors="replace", timeout=120)
            need(r.returncode == 0, "orders reach the picked mercs; gun/radar crews hold; FETCH sends one (G O1)",
                 "research/merc_order_targets_check.py fails: " + (r.stdout + r.stderr).strip()[-600:])
        except (OSError, subprocess.TimeoutExpired) as ex:
            bad("Mercenaries: research/merc_order_targets_check.py could not run: " + str(ex))
    else:
        bad("Mercenaries: research/merc_order_targets_check.py missing")


def check_vehicle_condition():
    """[22e] N8 - the new vehicles on the vanilla condition system
    (Revival.VehicleCondition.cs, docs/ai/tasks/n08-vehicles-vanilla.md).

    What can be held without the game: the parts are the game's own ids and
    go through LocalSetVehicleComponent; "as found" is the world's 50 percent
    roll and only reaches an admin spawn (AsAdmin scope), never a convoy; the
    CarSpawn paths call AfterSpawn; truck trunks are bigger than the game's 42
    slots and the window that shows them is installed independent of the Mi-8;
    the Mi-8 engine refuses without parts or fuel and burns fuel in flight.
    """
    print("[22e] Vehicle condition (N8)")
    src_p = os.path.join(ROOT, "Revival.VehicleCondition.cs")
    if not os.path.exists(src_p):
        bad("Revival.VehicleCondition.cs is missing - no vanilla condition for the new vehicles")
        return
    raw = io.open(src_p, "rb").read()

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad(why)

    def read(name):
        return io.open(os.path.join(ROOT, name), encoding="utf-8").read()

    BOM = bytes([0xEF, 0xBB, 0xBF])
    need(not raw.startswith(BOM), "Revival.VehicleCondition.cs has no BOM",
         "Revival.VehicleCondition.cs starts with a BOM")
    code = raw.decode("utf-8", "replace")
    tank = read("Revival.Tank.cs")
    heli = read("Revival.PlayerHeli.cs")
    plug = read("RevivalPlugin.cs")
    adm = read("Revival.Admin.cs")
    sync = read("sync_public.py")
    for n in ("BatteryId = 10002", "KeyId = 10003", "SparkId = 10004", "KeyAltId = 1306",
              "CanisterId = 10001"):
        need(n in code, "vanilla item " + n, "the vanilla item constant " + n + " changed")
    need('"LocalSetVehicleComponent"' in code, "parts go through the game's LocalSetVehicleComponent",
         "VehicleCondition no longer sets parts through LocalSetVehicleComponent")
    need("UnityEngine.Random.value > 0.5f" in code, "as found = the world's 50 percent roll",
         "the as-found roll is no longer 50 percent per part")
    need("return _scope && SpawnFound;" in code, "as found only inside an admin spawn",
         "FoundNow is not limited to the AsAdmin scope - a convoy could come as found")
    need(tank.count("VehicleCondition.AfterSpawn(car);") == 2,
         "both CarSpawn paths call VehicleCondition.AfterSpawn",
         "CarSpawn.SpawnAt/SpawnPrefab lost VehicleCondition.AfterSpawn")
    m = re.search(r"TruckSlots = (\d+);", code)
    need(m is not None and int(m.group(1)) > 42, "truck trunk bigger than the game's 42 slots",
         "TruckSlots is missing or not above the vanilla 42")
    need("HeliHold.InstallWindow(harmony);" in code and "internal static void InstallWindow(" in heli,
         "the >42-slot container window is installed for the trucks",
         "the container window growth is no longer installed by VehicleCondition")
    need("HeliCondition.CanStart(_heli, out missing)" in heli and "HeliCondition.Burn(_heli, power, dt)" in heli,
         "the Mi-8 engine needs parts and fuel and burns fuel",
         "PlayerHeli lost the HeliCondition engine gate or the fuel burn")
    need("if (f[1] < -0.5f) { HeliCondition.OnNet(f, sender); return; }" in heli,
         "Mi-8 condition rides on the helicopter's own event",
         "PlayerHeli EngineState no longer forwards the condition message")
    for n in ("VehicleCondition.Install(_harmony);", "VehicleCondition.Tick();", "VehicleCondition.Draw();"):
        need(n in plug, "RevivalPlugin calls " + n, "RevivalPlugin.cs lost " + n)
    need("VehicleCondition.SpawnFound = UiKit.Toggle(" in adm and "NearestCondition(" in adm,
         "admin panel: as-found switch and nearest-vehicle tools",
         "the admin panel lost the N8 condition controls")
    need('"Revival.VehicleCondition.cs"' in sync, "sync_public.py ships Revival.VehicleCondition.cs",
         "Revival.VehicleCondition.cs is missing in sync_public.py - the public repo does not build")


def check_fuel_economy():
    """[22d] Fuel economy and the airfield POL depot (Revival.Fuel.cs,
    docs/ai/tasks/fuel-economy.md, task 75462e6f82).

    What can be held without the game: the three parts are wired into the
    plugin, the depot decides on the master only, the tanks are the ones of the
    airfield recipe, the fire respects [Effects] Fire, the event code is its
    own, and the An-2 pump draws from the same pool.
    """
    print("[22d] Fuel economy and POL depot")
    src_p = os.path.join(ROOT, "Revival.Fuel.cs")
    if not os.path.exists(src_p):
        bad("Revival.Fuel.cs is missing - no fuel balance, stations or depot")
        return
    raw = io.open(src_p, "rb").read()
    code = raw.decode("utf-8", "replace")
    plug = io.open(os.path.join(ROOT, "RevivalPlugin.cs"), encoding="utf-8").read()
    an2r = io.open(os.path.join(ROOT, "Revival.An2Repair.cs"), encoding="utf-8").read()
    sync = io.open(os.path.join(ROOT, "sync_public.py"), encoding="utf-8").read()
    fuelwater = io.open(os.path.join(ROOT, "unity/EastTile/Content/east_af_fuel_water.json"),
                        encoding="utf-8").read()

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad(why)

    BOM = bytes([0xEF, 0xBB, 0xBF])
    need(not raw.startswith(BOM), "Revival.Fuel.cs has no BOM",
         "Revival.Fuel.cs starts with a BOM")
    need(all(("%s.%s" % (c, m)) in plug for c in ("FuelBalance", "FuelStations", "FuelDepot")
             for m in ("BindConfig(Config);", "Tick();"))
         and "FuelDepot.Install(_harmony);" in plug and "FuelDepot.Draw();" in plug,
         "fuel balance, stations and depot are bound, installed and ticked",
         "RevivalPlugin.cs does not wire all of Revival.Fuel.cs")
    need('"Revival.Fuel.cs"' in sync, "sync_public.py ships Revival.Fuel.cs",
         "Revival.Fuel.cs is missing in sync_public.py - the public repo does not build")
    dmg = _body(code, "static void Damage(int i, float dmg)")
    ev = _body(code, "public static void OnEvent(byte code, object content, int sender)")
    need("if (master && Active && f.Length >= 2) Damage(" in ev
         and "if (RevivalTroopInsertion.MasterClient()) Damage(tank, dmg);" in code
         and "RespawnAt" in dmg and "Send(OpExplode" in dmg,
         "only the master applies damage; a dead tank waits for its respawn",
         "tank damage is not master-owned")
    models = [(m["x"], m["z"]) for m in json.loads(fuelwater)["models"]
              if m["name"].startswith("pol_tank_") and "torn" not in m["name"]]
    tanks = [("%gf, %gf" % xz, xz) for xz in models]
    need(len(tanks) == 5 and all(a in code for a, _ in tanks),
         "the five pool tanks stand where east_af_fuel_water.json puts them",
         "the depot tanks do not match east_af_fuel_water.json")
    need("RevivalPlugin.CfgFire == null || !RevivalPlugin.CfgFire.Value" in _body(code, "static void Explode(int i, bool fresh)"),
         "the depot fire obeys [Effects] Fire", "the depot fire ignores [Effects] Fire")
    m = re.search(r'"NetworkEventCode", (\d+), "Photon event code \(0\.\.199\) of fuel', code)
    used = set()
    for f in os.listdir(ROOT):
        if f.endswith(".cs") and f != "Revival.Fuel.cs":
            t = io.open(os.path.join(ROOT, f), encoding="utf-8", errors="replace").read()
            used.update(int(x) for x in re.findall(r'(?:EventCode|NetworkEventCode)"?,\s*(\d+)', t))
            used.update(int(x) for x in re.findall(r'const (?:byte|int) (?:Default)?EventCode = (\d+);', t))
    need(m is not None and int(m.group(1)) not in used,
         "the fuel event code %s is its own" % (m.group(1) if m else "?"),
         "the fuel event code collides with another channel")
    need("if (global::NextDayRevival.FuelDepot.Active) return global::NextDayRevival.FuelDepot.Pool;" in an2r
         and "global::NextDayRevival.FuelDepot.Draw(add)" in an2r,
         "the An-2 pump draws from the depot pool", "the An-2 pump has its own fuel again")


def check_an2_repair():
    """[22c] The An-2 repair loop (Revival.An2Repair.cs, docs/ai/tasks/an2-repair.md).

    The order (task 93c547d5c5): the An-2 at the H1 apron starts broken; four
    stages (controls/instruments, engine, propeller, fuel) with existing loot
    items where possible and three new ones; the parts spawn through the
    airfield's loot tiers; fitting through the existing interaction style with
    progress per stage; fuel from canisters and the D1 depot; it flies only
    when every stage is done; the state saved and synced by the master; a
    config key that is off by default. What can be held without the game:
      - off by default, and the engine start asks the repair state;
      - the three new ids are free in the game's table and nowhere else in the
        plugin, their art exists and is installed;
      - the airfield's parts/fuel pools roll them;
      - a part is taken from the pack only after the timer ran out (an
        interrupted fitting never eats it), and fuel only grows through a
        canister or the depot;
      - the depot is where the airfield recipe puts the pump house;
      - the state travels in the spawn data, the heartbeat and two events, and
        a wreck resets it;
      - the seams and the file lists.
    """
    print("[22c] An-2 repair loop")
    src_p = os.path.join(ROOT, "Revival.An2Repair.cs")

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("An2Repair: " + why)

    if not os.path.exists(src_p):
        bad("Revival.An2Repair.cs is missing - the An-2 cannot be repaired")
        return
    raw = io.open(src_p, "rb").read()
    need(not raw.startswith(b"\xef\xbb\xbf"), "Revival.An2Repair.cs has no BOM",
         "Revival.An2Repair.cs starts with a BOM")
    code = _code(raw.decode("utf-8", "replace"))

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    import re
    import glob
    an2 = _code(read("Revival.PlayerAn2.cs"))
    plug = read("RevivalPlugin.cs")
    airfield = _code(read("Revival.Airfield.cs"))
    convoy = _code(read("RevivalConvoyRepair.cs"))
    items = read("Revival.Items.cs")

    need(re.search(r'cfg\.Bind\(S, "Enabled", true,', code) is not None
         and "Settings.GameplayOn(cfg, CfgEnabled);" in code,
         "[An2Repair] Enabled is on by default and an older file is switched on once",
         "[An2Repair] Enabled is not on by default or lacks the Settings.GameplayOn migration")

    # --- the ids: free in the game, one owner in the plugin, own art.
    ids = []
    for name in ("CableId", "MagnetoId", "PropId"):
        m = re.search(r"internal const int %s = ([0-9]+);" % name, code)
        ids.append(int(m.group(1)) if m else None)
    need(ids == [2069, 2070, 2071], "the three new parts are 2069, 2070 and 2071",
         "the part ids are not 2069/2070/2071: %s" % ids)
    game = set()
    tsv = os.path.join(ROOT, "research", "items.tsv")
    if os.path.exists(tsv):
        for line in io.open(tsv, encoding="utf-8", errors="replace"):
            p = line.split("\t")
            if len(p) > 1 and p[1].isdigit():
                game.add(int(p[1]))
    need(not ({2069, 2070, 2071} & game), "2069-2071 are not ids of the game",
         "an An-2 part id is already an item of the game")
    for want in (10001, 10002, 10004, 10005, 10006):
        need(want in game or not game, "the reused vehicle item %d exists in the game" % want,
             "item %d is not in research/items.tsv" % want)
    claimed = []
    for p in glob.glob(os.path.join(ROOT, "*.cs")):
        if os.path.basename(p) == "Revival.An2Repair.cs":
            continue
        if re.search(r"\b(2069|2070|2071)\s*,\s*[0-9]{4}\s*,\s*(true|false)",
                     io.open(p, encoding="utf-8").read()):
            claimed.append(os.path.basename(p))
    need(not claimed, "no other ItemDef claims 2069-2071",
         "another ItemDef uses an An-2 part id: " + ", ".join(claimed))
    need(re.search(r"SellOnlyIds[^;]*2069, 2070, 2071", items) is not None,
         "the three parts have a trader's sell price",
         "2069-2071 are not in Registry.SellOnlyIds - a trader will not take them")
    for stem in ("an2part_cable", "an2part_magneto", "an2part_prop"):
        need('"%s.ndmesh"' % stem in code and '"%s_icon.png"' % stem in code,
             "%s: mesh and icon named by its ItemDef" % stem,
             "%s is not the art of its item" % stem)
    need("An2Repair.AddItems(Items);" in plug, "the parts are in the item table",
         "RevivalPlugin.cs does not call An2Repair.AddItems")

    # --- the loot: the airfield's pools roll the parts and the canisters.
    pool = _body(code, "internal static string[] Pool(string pool, string[] entries)")
    need('pool == "parts"' in pool and all('"%d"' % i in pool for i in (2069, 2070, 2071))
         and 'pool == "fuel"' in pool and '"10001"' in pool,
         "the parts pool rolls the three parts, the fuel pool the canister",
         "the airfield pools do not roll the An-2 parts or canisters")
    need("entries = An2Repair.Pool(pool, entries);" in _body(airfield, "static object Draw(string pool)"),
         "the airfield loot draw asks the repair for its pools",
         "Airfield.Draw does not extend its pools - no part ever spawns")
    need(re.search(r'new Slot\("H1", 3, "parts"', airfield) is not None
         and re.search(r'new Slot\("D1c", 3, "fuel"', airfield) is not None,
         "H1 has its parts cage and D1c its fuel point (tier 3)",
         "the H1 parts cage or the D1c fuel point is gone")

    # --- interruption never eats a part; fuel only through an item or the depot.
    idle = _body(code, "static void TickIdle()")
    job = _body(code, "static void TickJob()")
    need("TakeItem" not in idle and "Turret.TakeItem(PartItem[part]" in job
         and 0 <= job.find("Time.time - _jobStart < _jobLen") < job.find("TakeItem"),
         "a part leaves the pack only after the fitting's timer ran out",
         "a part is taken before the fitting is finished - an interruption eats it")
    need(code.count("Turret.TakeItem(") == 2, "exactly two places consume: a part and a canister",
         "an unexpected TakeItem in An2Repair")
    apply = _body(code, "static void Apply(GameObject go, int what, float litres, int sender)")
    need("SetFuel(" in apply and "what == FuelCan || what == FuelDepot" in apply
         and "Mathf.Min(add, Reserve())" in apply
         and code.count("PlayerAn2.SetFuel(") == 2,
         "fuel grows only by a canister or the depot's reserve (and the master's state)",
         "fuel can grow without a canister or past the depot's reserve")

    # --- flies only when repaired.
    eng = _body(an2, "static void SetEngine(bool on)")
    need("An2Repair.CanStart(_plane, vis.Fuel, out why)" in eng,
         "the engine starts only on an airworthy An-2",
         "SetEngine does not ask An2Repair - a broken An-2 flies")
    can = _body(code, "internal static bool CanStart(GameObject go, float fuel, out string why)")
    need("AllParts" in can and "CfgMinFuel" in can, "airworthy = all six parts and the minimum fuel",
         "CanStart does not check every part and the fuel")
    newp = _body(code, "internal static void NewPlane(ref float fuel, out int mask)")
    need("mask = 0;" in newp and "fuel = 0f;" in newp, "a new An-2 starts broken and dry",
         "a new An-2 does not start broken")

    # --- the depot is the pump house of the recipe.
    recipe = read("unity/EastTile/Content/east_airfield.json")
    m = re.search(r'"id":\s*"D1c".*?"x":\s*([0-9.]+),\s*"z":\s*([0-9.]+)', recipe, re.S)
    m2 = re.search(r"Depot = new Vector3\(([0-9.]+)f, 0f, ([0-9.]+)f\)", code)
    need(m is not None and m2 is not None
         and abs(float(m.group(1)) - float(m2.group(1))) < 1
         and abs(float(m.group(2)) - float(m2.group(2))) < 1,
         "the fuel depot pump is the recipe's D1c pump house",
         "the depot is not where east_airfield.json puts D1c")

    # --- authority, sync, persistence.
    need("An2Repair.NewPlane(ref fuel, out parts);" in an2
         and "new object[] { Marker, fuel, parts }" in an2
         and "An2Repair.Prepared(go, data);" in an2,
         "the mask travels in the spawn data and is read on every client",
         "the repair mask is not in the spawn data")
    need("An2Repair.Broadcast(go, view);" in _body(an2, "static void State()")
         and "An2Repair.OnNet(kind, f, sender);" in an2
         and "internal const int NetState = 6;" in code and "internal const int NetRequest = 7;" in code,
         "the master repeats the state in the heartbeat; +6 state, +7 request",
         "the repair state is not synced")
    need("RevivalTroopInsertion.MasterClient()" in _body(code, "internal static void OnNet(int kind, float[] f, int sender)")
         and "if (!RevivalTroopInsertion.MasterClient()) return;" in _body(code, "static void Save(GameObject go)"),
         "only the master applies requests and writes the save",
         "a client applies requests or writes the save")
    need("An2Repair.Wrecked(go);" in _body(an2, "static void Burn(GameObject go, Vector3 where)"),
         "a wreck resets the repair (the next An-2 is broken)",
         "a wreck does not reset the repair state")
    need("An2Repair.SavedPlace(out saved, out savedHeading)" in _body(an2, "static void ApronSpawn()"),
         "the first An-2 of a session stands where it was parked",
         "the saved parking place is not used")
    need("An2Repair.Busy" in _body(convoy, "public static void Postfix(ref bool __result)")
         and "ConvoyFreezeHook.Install(harmony);" in code,
         "the body is held while fitting (the convoy repair's freeze)",
         "the body is not frozen while fitting")
    need("NativeActionProgress.Begin(ProgressOwner" in code,
         "fitting uses the native interaction progress",
         "fitting does not use NativeActionProgress")
    for seam in ("An2Repair.BindConfig(Config);", "An2Repair.Install(_harmony);",
                 "An2Repair.Tick();", "An2Repair.Draw();"):
        need(seam in plug, "seam " + seam, "seam missing in RevivalPlugin.cs: " + seam)

    # --- file lists.
    build = read("build.ps1")
    sync = read("sync_public.py")
    need(all('"an2part_%s_icon.png"' % n in build and '"an2part_%s.ndmesh"' % n in build
             for n in ("cable", "magneto", "prop")),
         "build.ps1 installs the part art", "build.ps1 does not install the An-2 part art")
    need('"Revival.An2Repair.cs"' in sync and '"an2_parts_build.py"' in sync,
         "Revival.An2Repair.cs and an2_parts_build.py go into the public repository",
         "sync_public.py lacks Revival.An2Repair.cs or an2_parts_build.py")


def check_parachute():
    """[23] The parachute.

    The whole feature is two calls into the game's own parachute, and the pair
    is the point: PlayerMovementController.FixedUpdate gives a man in character
    state 5 who reaches the ground with the canopy CLOSED 1000 damage. So a
    state entered without uncovering it in the same breath is not a parachute,
    it is a scripted death - and nothing in the game would say so.

    The rest is the id band and the seams, both of which fail silently: an id
    outside 2001..3000 lands in another category, and a missing seam means the
    item simply never exists.
    """
    print("[23] Parachute")
    src_p = os.path.join(ROOT, "Revival.Parachute.cs")
    if not os.path.exists(src_p):
        bad("Revival.Parachute.cs is missing - nothing opens over a jumper")
        return
    raw = io.open(src_p, "rb").read()
    para = raw.decode("utf-8", "replace")
    code = _code(para)

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Parachute: " + why)

    plug = read("RevivalPlugin.cs")
    sync = read("sync_public.py")
    heli = _code(read("Revival.PlayerHeli.cs"))

    # --- file rule: UTF-8 without BOM, and outside ASCII only the Cyrillic of
    # the player-facing Loc.T lines (AGENTS.md).
    need(not raw.startswith(b"\xef\xbb\xbf"), "no BOM",
         "Revival.Parachute.cs starts with a BOM")
    strange = sorted(set(c for c in para
                         if ord(c) > 126 and not 0x400 <= ord(c) <= 0x4FF))
    need(not strange,
         "outside ASCII only Cyrillic (player text)",
         "Revival.Parachute.cs holds characters that are neither ASCII nor "
         "Cyrillic: " + " ".join("U+%04X" % ord(c) for c in strange))

    # --- the pair. Both calls, in one method, in this order.
    open_body = _body(code, "static bool Open(Vector3 at)")
    fly_at = open_body.find("_mFly.Invoke")
    cover_at = open_body.find("_mUncover.Invoke")
    need(fly_at >= 0 and cover_at > fly_at,
         "the state is entered and the canopy opened in the same move",
         "SetPlayerParashuteFlyState and PlayerParashuteUncover are not called "
         "together - a state without a canopy is 1000 damage on landing")
    need('"SetPlayerParashuteFlyState"' in code
         and '"PlayerParashuteUncover"' in code,
         "both moves are the game's own methods, resolved by name",
         "the descent is reimplemented instead of using the game's parachute")
    need("Parachute_Pref" not in code and "InstanceParachute" not in code,
         "the canopy model is left to the game",
         "this file instantiates a canopy itself - PlayerObjectsManager already "
         "does it, from the RPC, on every client")

    # --- the id band. 2001..3000 is the game's ammunition band: right for
    # something CARRIED, wrong for anything that has to go into a weapon slot.
    # That distinction is the anti-tank mine's lesson (see check_mine).
    import re
    m = re.search(r"public const int ItemId = (\d+);", code)
    item_id = None if m is None else int(m.group(1))
    need(item_id is not None and 2001 <= item_id <= 3000,
         "item id %s is in the carried band 2001-3000" % item_id,
         "the parachute's item id is outside 2001..3000 - it would be read as "
         "another category")
    need(item_id is not None and ("%d" % item_id) not in
         _code(read("RevivalM7Rifle.cs")) + _code(read("RevivalDroneGear.cs"))
         + _code(read("RevivalConvoyRepair.cs")),
         "the id is not already taken by another item file",
         "the parachute's item id is used by another feature as well")

    # --- the height gate, and the one that matters: no canopy, no free ride.
    jump = _body(code, "public static bool Jump(Vector3 at, float height, out string say)")
    need("height < MinHeight" in jump and "if (!Have())" in jump,
         "too low or no chute in the pack is a fall, not a canopy",
         "the jump opens a canopy without checking the height or the pack")

    # --- the caller. The helicopter is what puts a man in the air.
    need("Parachute.Jump(door, height, out why)" in heli,
         "the helicopter's jump key asks the parachute for a canopy",
         "Revival.PlayerHeli.cs no longer calls Parachute.Jump - the jump key "
         "is a fall again")

    # --- seams outside this file.
    need("Parachute.BindConfig(Config);" in plug
         and "Parachute.AddItems(Items);" in plug,
         "seams BindConfig/AddItems in RevivalPlugin.cs",
         "a seam is missing in RevivalPlugin.cs: Parachute")
    need('"Revival.Parachute.cs"' in sync and '"parachute_build.py"' in sync,
         "source and asset generator go into the public repository",
         "Revival.Parachute.cs or parachute_build.py is missing from "
         "sync_public.py - the public repo does not build without them")


def check_crocodile():
    """[25] Blender crocodile art, native boss seam, and swimming contract."""
    print("[25] Toxic crocodile")
    paths = {
        "code": os.path.join(ROOT, "Revival.Crocodile.cs"),
        "plugin": os.path.join(ROOT, "RevivalPlugin.cs"),
        "build": os.path.join(ROOT, "build.ps1"),
        "assets": os.path.join(ROOT, "make_assets.py"),
        "sync": os.path.join(ROOT, "sync_public.py"),
        "wrapper": os.path.join(ROOT, "crocodile_build.py"),
        "blender": os.path.join(ROOT, "assets", "src", "crocodile_blender.py"),
        "blend": os.path.join(ROOT, "assets", "src", "crocodile.blend"),
        "preview": os.path.join(ROOT, "assets", "src", "crocodile_preview.png"),
        "resource_index": os.path.join(ROOT, "research", "resource_paths.tsv"),
    }

    def text(key):
        path = paths[key]
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad(why)

    code = text("code")
    plugin = text("plugin")
    build = text("build")
    assets_py = text("assets")
    sync = text("sync")
    wrapper = text("wrapper")
    blender = text("blender")
    resource_index = text("resource_index")

    need(all(seam in plugin for seam in (
            "Crocodile.BindConfig(Config)", "Crocodile.Install(_harmony)",
            "Crocodile.Tick()", "Crocodile.Draw()")),
         "all four RevivalPlugin lifecycle seams are wired",
         "RevivalPlugin.cs is missing a Crocodile BindConfig/Install/Tick/Draw seam")
    need("animalsspawn/bearboss_spawn" in resource_index.lower()
         and "AnimalsSpawn/BearBoss_Spawn" in code,
         "the native boss proxy is a resource proven by the asset index",
         "BearBoss_Spawn is absent from resource_paths.tsv or Revival.Crocodile.cs")
    need("NetworkApplyDamage" in code and "DamageTakenMultiplier" in code
         and "0.05f" in code and "CrocodileSwimmer" in code,
         "native Animal_AI damage is retained and resistance marks only the crocodile",
         "the crocodile lost its native damage gate or twenty-times effective health")
    # 6.47: bigger. The scale sits on the model transform, so the hit boxes
    # (its children) grow with it; the bite line and lunge distance must too.
    need('"BodyScale"' in code and "localScale = Vector3.one * _scale" in code
         and "4.2f : 4.6f" not in code and "6.5f * Crocodile.BodyScale()" in code,
         "BodyScale grows the model, its hit boxes, the bite reach and the lunge",
         "the crocodile's size no longer scales its bite and lunge with the body")
    need("SwimPoint(NetworkClock())" in code and "SwimTangent(NetworkClock())" in code
         and "FindWater(out float y" in code and "DefaultX = 1965f" in code
         and "DefaultZ = 900f" in code,
         "the boss follows the measured lake loop at the detected water surface",
         "the deterministic swimming loop, lake coordinate, or water lookup is missing")
    need("GasLauncher.Protection()" in code and "GasLauncher.AddToxicity" in code
         and "GasLauncher.HurtPlayer" in code,
         "the toxic aura uses the game's toxicity and protection path",
         "the crocodile no longer poisons through the shared gas/protection mechanics")

    need('"crocodile.ndmesh"' in build and '"crocodile_diffuse.png"' in build
         and '"crocodile_normal.png"' in build,
         "build.ps1 installs all three runtime assets",
         "build.ps1 does not install the crocodile mesh and texture set")
    need('(\"crocodile\", [\"crocodile_build.py\"])' in assets_py
         and '"crocodile_build.py"' in sync
         and '"assets/src/crocodile_blender.py"' in sync
         and '"assets/src/crocodile.blend"' in sync,
         "the Blender generator and editable source travel with the public tree",
         "make_assets.py or sync_public.py omits the crocodile Blender sources")
    need("--background" in wrapper and "--factory-startup" in wrapper
         and "import bpy" in blender and "bpy.ops.wm.save_as_mainfile" in blender,
         "the reproducible build actually runs Blender and saves a .blend",
         "crocodile_build.py no longer invokes the Blender modelling source")

    blend_header = b""
    if os.path.exists(paths["blend"]):
        with open(paths["blend"], "rb") as handle:
            blend_header = handle.read(7)
    need(blend_header == b"BLENDER",
         "assets/src/crocodile.blend is an editable Blender file",
         "assets/src/crocodile.blend is missing or not a Blender file")
    need(os.path.exists(paths["preview"])
         and os.path.getsize(paths["preview"]) > 10000,
         "the Blender source includes a rendered visual review image",
         "the crocodile Blender preview is missing or empty")

    mesh_path = os.path.join(ASSETS, "crocodile.ndmesh")
    try:
        n, vertices, _, _, index_count, _ = read_mesh(mesh_path)
        xs, ys, zs = vertices[0::3], vertices[1::3], vertices[2::3]
        width = max(xs) - min(xs)
        height = max(ys) - min(ys)
        length = max(zs) - min(zs)
        mesh_ok = (3000 <= index_count // 3 <= 20000 and n > 9000
                   and 2.5 <= width <= 5.0 and 0.7 <= height <= 2.0
                   and 7.0 <= length <= 12.0)
    except Exception:
        mesh_ok = False
        n = index_count = 0
        width = height = length = 0.0
    need(mesh_ok,
         "Blender crocodile is %.2f x %.2f x %.2f m, %d triangles"
         % (width, height, length, index_count // 3),
         "crocodile.ndmesh is absent or no longer a large animal-shaped mesh")

    # 6.45 the hunter. The rig names the jaw and four legs per vertex, in the
    # ndmesh's own order: a mesh rebuilt without it would animate the wrong
    # vertices, so both must come from one Blender run.
    rig_path = os.path.join(ASSETS, "crocodile_rig.bin")
    rig_ok = False
    counts = []
    try:
        rig = io.open(rig_path, "rb").read()
        magic, (version, count, parts) = rig[:4], struct.unpack_from("<iii", rig, 4)
        table = rig[16 + parts * 12:]
        pivots = [struct.unpack_from("<3f", rig, 16 + 12 * i) for i in range(parts)]
        counts = [table.count(bytes([p])) for p in range(parts)]
        rig_ok = (magic == b"NDRG" and count == n and parts == 6
                  and len(table) == count and min(counts[1:]) >= 50
                  and counts[0] > count // 2
                  and all(pivots[k][0] * (1 if k % 2 else -1) > 0.4 for k in range(2, 6))
                  and pivots[2][2] > 0.5 and pivots[4][2] < -0.5 and pivots[1][2] > 1.5)
    except Exception:
        rig_ok = False
    need(rig_ok,
         "crocodile_rig.bin maps all %d vertices: body/jaw/legs %s" % (n, counts),
         "crocodile_rig.bin is missing, stale against the ndmesh, or lacks a jaw/leg part")
    need('"crocodile_rig.bin"' in build and "RIG_PATH" in blender
         and "crocodile_rig.bin" in wrapper,
         "Blender writes the rig and build.ps1 installs it",
         "the rig is not produced by crocodile_blender.py or not installed by build.ps1")
    need('"StateAction"' in code and '"NetworkAttackState"' in code
         and "SetAnimalState" in code and "state != 3 && state != 4" in code,
         "the native bear brain and its claw attack are silenced for the crocodile only",
         "the native Animal_AI could still chase and claw from inside the crocodile")
    need('"PlayerBossController"' in code and '"SetBossData"' in code
         and '"customName"' in code,
         "the game's own boss bar is raised with the crocodile's name",
         "the crocodile no longer raises the native boss health bar")
    need('_hitRoot.tag = tag' in code and 'string tag = "Animal"' in code,
         "hit boxes carry the Animal tag the firearm path demands",
         "crocodile hit boxes lost the Animal tag; shots would not reach Animal_AI")
    need("CrocodileLake.HuntZone" in code and "ShoreReach" in code
         and "SwimDepth" in code and "Queue<int>" in code,
         "the hunting ground is the flooded lake plus its immediate shore",
         "the crocodile's water/shore map is missing")
    need("RunningStraightAway" in code and "EatDamage" in code
         and "_jinks >= 3" in code and "SprintEdge" in code,
         "straight flight is eaten, a zigzag makes it overshoot and give up",
         "the zigzag-or-be-eaten rule is missing")
    need("CrocodileNet.Send" in code and "NetworkEventCode" in code
         and "DefaultEventCode = 164" in code,
         "the master broadcasts the hunt on its own Photon event code",
         "the crocodile hunt is not networked")


def check_stinger():
    """[24] The Stinger: the sight, the kill, and the second shot.

    Three field reports, three rules that a later edit could silently undo.

    THE SIGHT is not drawn by this plugin. `xmlItemsDataManager` passes the
    weapons_db.xml `Scope` attribute to `Resources.Load` and casts the result
    to Texture2D, `CameraSwitch::CantRenderScope` refuses the whole scope mode
    when that field is null, and `ScopeCameraEffect::OnGUI` paints it. So the
    sight exists only while THREE things agree: mods/revival.json names the
    path, ResourceHook answers that exact path, and the lens numbers in
    Revival.Stinger.cs match the ones stinger_scope.py drew. Break any one and
    the weapon still works - it just quietly stops having a sight.

    THE KILL must not go back through a shared damage number. 900 is the LAW's
    blast and RevivalVehicleArmor reads it back AS a LAW hit, which halves it
    against a tank; ArtyBattery.Shoot is a rifle round and takes one of the
    recon drone's three hit points. Both were the reason "the missiles do not
    one-shot vehicles or the little drones", and both come back the moment
    Kill() starts trusting a constant again.

    THE SECOND SHOT is the game's own reload, and the two weapons that share
    the LAW donor now differ in exactly one thing: 1165 names a magazine and
    1162 does not. That difference lives in two files at once - the item table
    and the weapon record - and serversync.py only compares the magazine ID, so
    a ReloadTime left at the LAW's 99 would pass the release gate and still be
    a weapon nobody can reload.
    """
    print("[24] Stinger")
    src_p = os.path.join(ROOT, "Revival.Stinger.cs")
    if not os.path.exists(src_p):
        bad("Revival.Stinger.cs is missing")
        return
    raw = io.open(src_p, "rb").read()
    text = raw.decode("utf-8", "replace")
    code = _code(text)

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Stinger: " + why)

    import re
    import json as _json
    plug = read("RevivalPlugin.cs")
    items = read("Revival.Items.cs")
    armor = _code(read("RevivalVehicleArmor.cs"))
    gen = read("stinger_scope.py")
    sync = read("sync_public.py")
    build = read("build.ps1")
    assets_py = read("make_assets.py")

    # --- file rule: UTF-8 without BOM, and outside ASCII only the Cyrillic of
    # the player-facing Loc.T lines (AGENTS.md).
    need(not raw.startswith(b"\xef\xbb\xbf"), "no BOM",
         "Revival.Stinger.cs starts with a BOM")
    strange = sorted(set(c for c in text
                         if ord(c) > 126 and not 0x400 <= ord(c) <= 0x4FF))
    need(not strange,
         "outside ASCII only Cyrillic (player text)",
         "Revival.Stinger.cs holds characters that are neither ASCII nor "
         "Cyrillic: " + " ".join("U+%04X" % ord(c) for c in strange))

    # --- the weapon record. This is the half of the feature that lives on the
    # master server, and the server wins every argument about weapon data.
    weapons = {}
    mods_p = os.path.join(ROOT, "mods", "revival.json")
    if os.path.exists(mods_p):
        try:
            for w in _json.load(io.open(mods_p, encoding="utf-8"))["newWeapons"]:
                weapons[str(w.get("ItemID"))] = w
        except Exception as ex:
            bad("Stinger: mods/revival.json cannot be read: %s" % ex)
    sting = weapons.get("1165", {})
    law = weapons.get("1162", {})

    m = re.search(r'StingerScopePath\s*=\s*"([^"]+)"', plug)
    scope_path = None if m is None else m.group(1)
    need(scope_path is not None and sting.get("Scope") == scope_path,
         "the weapon record names the sight the plugin serves",
         "mods/revival.json 1165 Scope is %r, RevivalPlugin.StingerScopePath is "
         "%r - the game would load nothing and CantRenderScope refuses the "
         "whole scope mode" % (sting.get("Scope"), scope_path))
    try:
        fov = int(sting.get("ScopeFOV", "0"))
    except (TypeError, ValueError):
        fov = 0
    need(0 < fov < 60,
         "ScopeFOV %d is an aimed field of view" % fov,
         "mods/revival.json 1165 ScopeFOV is %r - ScopeFOV is an ObscuredInt "
         "and 0 is the same as having no sight" % sting.get("ScopeFOV"))
    need(scope_path is not None
         and "RevivalPlugin.StingerScopePath" in items
         and '"stinger_scope.png"' in items,
         "ResourceHook answers that path with stinger_scope.png",
         "Revival.Items.cs does not serve stinger_scope.png for the Stinger "
         "scope path - Resources.Load returns null and there is no sight")

    # --- lens geometry. Two numbers in two files that must be the same one.
    def number(src, pattern):
        # MULTILINE: the generator's constants sit at the start of their own
        # line, the C# ones do not.
        mm = re.search(pattern, src, re.M)
        return None if mm is None else float(mm.group(1))

    need(number(code, r"ScopeImage\s*=\s*([0-9.]+)f") == number(gen, r"^S\s*=\s*([0-9]+)")
         and number(code, r"ScopeLens\s*=\s*([0-9.]+)f") == number(gen, r"^R_LENS\s*=\s*([0-9.]+)"),
         "the lens the code clips to is the lens the generator drew",
         "ScopeImage/ScopeLens in Revival.Stinger.cs and S/R_LENS in "
         "stinger_scope.py disagree - the target boxes land on the mount")
    draw = _body(code, "public static void Draw()")
    need("if (!sight) {" in draw and "> lens) continue;" in draw,
         "no second cross over the sight, no box outside the glass",
         "Draw() no longer clips to the lens or still paints its own crosshair "
         "on top of the sight's aiming point")

    # --- the kill. One missile, one wreck, whatever was hit.
    kill = _body(code, "static void Kill(Flight f)")
    need(kill != "" and "VehicleArmor.MissileKill" in kill,
         "a vehicle is destroyed outright, not left to the blast",
         "Kill() does not call VehicleArmor.MissileKill - a vehicle is back to "
         "depending on the blast, and a tank survives it")
    need("ArtyBattery.Shoot" in kill and "for (int i = 0; i < 4; i++)" in kill,
         "the recon drone loses every hit it has, not one of three",
         "Kill() takes a single ArtyBattery.Shoot for the recon drone again - "
         "that is one of its three hit points, so three missiles per drone")
    need(re.search(r"Drone\.Net\.Send\(Drone\.Net\.Treffer[^;]*?100f", kill) is None
         and re.search(r"SurvNet\.Send\(SurvNet\.Treffer[^;]*?100f", kill) is None,
         "no drone is handed a flat hundred any more",
         "Kill() hands a drone a fixed 100 again - a hit-point change makes the "
         "missile survivable without anything here saying so")
    finish = _body(code, "static void Finish(Flight f, bool targetHit, bool impact)")
    need("Warhead()" in finish and "900f" not in finish,
         "the blast is the Stinger's own number, not the LAW's 900",
         "Finish() detonates with the LAW's literal 900 - VehicleArmor reads "
         "that back as a LAW hit and gives a tank two of them")
    missile_kill = _body(armor, "public static bool MissileKill(Component vehicle)")
    need(missile_kill != ""
         and 'GetFloat(vehicle, "Durability"' in missile_kill
         and "_missileKill = true;" in missile_kill,
         "the damage is read off the vehicle in front of the missile",
         "VehicleArmor.MissileKill no longer derives its damage from the "
         "vehicle's own Durability, or no longer holds the tank re-balance off")
    need("if (_missileKill) return true;" in _body(armor,
         "public static bool Prefix(object __instance, ref float __0, int __1)"),
         "the tank re-balance stands aside for that one call",
         "VehicleArmor.Prefix does not check _missileKill - an exact number is "
         "rewritten into a fraction of a pool that may not be this vehicle's")

    # --- the second shot, and the LAW that must not get one.
    def item_ints(src, item_id):
        mm = re.search(r"new ItemDef\(\s*%d\s*,(.*?)\)\s*\)" % item_id, src, re.S)
        if mm is None:
            return []
        block = re.sub(r'"[^"]*"', '""', mm.group(1))
        return [int(z) for z in re.findall(r"(?<![\w.])(\d+)(?![\w.])", block)]

    sting_ints = item_ints(plug, 1165)
    law_ints = item_ints(plug, 1162)
    need(len(sting_ints) >= 3 and sting_ints[2] == 2068,
         "the item table gives 1165 the magazine 2068",
         "the Stinger's ItemDef does not carry clip 2068 - the game has no "
         "magazine to reload from")
    need(len(law_ints) >= 3 and law_ints[2] == 0,
         "the item table leaves 1162 without one",
         "the M72 LAW's ItemDef carries a magazine - the LAW is a sealed tube "
         "and stays single shot")
    need(item_ints(plug, 2068) != [],
         "the reload round 2068 exists as an item",
         "item 2068 is not in the item table - the magazine the Stinger names "
         "cannot be carried, bought or loaded")
    need(sting.get("ClipItemID") == "2068" and law.get("ClipItemID") == "0",
         "the weapon record agrees: 1165 has a magazine, 1162 has none",
         "mods/revival.json disagrees with the item table about the magazines "
         "(1165 %r, 1162 %r) - the server wins and the reload is refused"
         % (sting.get("ClipItemID"), law.get("ClipItemID")))
    try:
        reload_s = float(sting.get("ReloadTime", "99"))
    except (TypeError, ValueError):
        reload_s = 99.0
    need(0 < reload_s < 30,
         "1165 reloads in %.1f s" % reload_s,
         "mods/revival.json 1165 ReloadTime is %r - 99 is the LAW's way of "
         "saying never" % sting.get("ReloadTime"))
    need(law.get("ReloadTime") == "99.0",
         "1162 still never reloads",
         "the M72 LAW's ReloadTime is no longer 99 - it is meant to stay a "
         "one-shot tube")

    # --- the round has to be obtainable, or the launcher is single shot again.
    shop = re.search(r"ShopItemIds = new int\[\] \{(.*?)\};", items, re.S)
    prices = re.search(r"ShopBuyPrices = new int\[\] \{(.*?)\};", items, re.S)
    shop_ids = [] if shop is None else [int(z) for z in re.findall(r"\d+", shop.group(1))]
    price_list = [] if prices is None else [int(z) for z in re.findall(r"\d+", prices.group(1))]
    need(2068 in shop_ids,
         "the reload round is on sale",
         "item 2068 is not in ShopItemIds - a player who fires the Stinger has "
         "no way to get another tube")
    need(len(shop_ids) == len(price_list),
         "every shop id has a price (%d of them)" % len(shop_ids),
         "ShopItemIds has %d entries and ShopBuyPrices %d - the prices are "
         "read by index" % (len(shop_ids), len(price_list)))

    # --- the sight image has to travel with the rest.
    need('"stinger_scope.png"' in build and '"stinger_missile_icon.png"' in build,
         "build.ps1 installs the sight and the round's icon",
         "build.ps1 does not copy stinger_scope.png or stinger_missile_icon.png "
         "into the game's assets folder")
    need('"stinger_scope.py"' in sync and "stinger_scope.py" in assets_py,
         "the generator runs with make_assets and goes into the public repo",
         "stinger_scope.py is missing from make_assets.py or sync_public.py - "
         "the public repo cannot rebuild the sight")


def check_traitor_vendor():
    """[25] The trader in the blue block at Litvinovka.

    The traitor settlement is the toolkit's own camp, and the request was a
    vendor in the closed block in the middle of it - an opening in the side
    facing the street, a man in it, and the same shop the civilian settlement's
    vendor has. Six rules, each of them one or two lines that a later edit could
    undo with nothing looking broken until somebody is in the game:

      1. HE IS BUILT BY THE GAME, not by hand. `Crew.DropCustomSquad` is
         `DropGroundSquad` plus a callback on the spawn point, and the callback
         has to be CALLED in `Absetzen` - without that line the trader is an
         ordinary armed crewman with a rifle.
      2. WHAT MAKES HIM A TRADER lives on that spawn point: BehaviorPattern
         StoreKeeper and the storekeeper prefab. `InitSpawnNpc` reads them; no
         other path in the game makes a shop.
      3. THE SHOP IS COPIED, NOT INVENTED. StorageId, MarketItemRanksSelling
         and BuyPlayerItemsPercent come off the civilian settlement's own
         storekeeper. Invented numbers are how a trader ends up with an empty
         shelf or paying nothing. The search may only latch on a HIT: the first
         spawn can run before the civilian settlement is in the scene, and a
         remembered miss would pin the shop to the fall-back for the whole
         session (the hazard NativeSettlement documents for Crew.Suchen).
      4. HIS SPAWN KEY CARRIES A SLASH and Revival.GroundEnemies.cs skips keys
         that have one - the same guard the technical's riders need, for the
         same reason: that pass DESTROYS every keyed NPC it does not know.
      5. THE BLOCK IS BORROWED, NOT DAMAGED. A cut always builds a NEW Mesh and
         the original is put back by `Restore`, so the asset in resources.assets
         and every other copy of that prop in the world are untouched.
      6. THE PLACE COMES FROM THE CAMP. Wanted/Centre/Here/Unhate of
         RevivalNewSettlement.cs: the shop closes with the camp's own switch, a
         camp moved in the config takes its shop with it, the opening is only
         built on the map Litvinovka is on, and the traitors do not shoot their
         own trader.
    """
    print("[25] Traitor settlement vendor (statisch)")
    vendor_p = os.path.join(ROOT, "RevivalTraitorVendor.cs")
    if not os.path.exists(vendor_p):
        bad("RevivalTraitorVendor.cs fehlt - die Verraeter haben keinen Haendler")
        return
    raw = io.open(vendor_p, "rb").read()
    vendor = raw.decode("utf-8", "replace")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Traitor vendor: " + why)

    crew = read("Revival.Crew.cs")
    camp = read("RevivalNewSettlement.cs")
    plug = read("RevivalPlugin.cs")
    ground = read("Revival.GroundEnemies.cs")
    sync = read("sync_public.py")

    # --- file rule: BOM-less UTF-8, and outside ASCII only the Cyrillic of the
    # player-facing Loc.T lines (the same rule check_helipads keeps).
    need(not raw.startswith(b"\xef\xbb\xbf"), "keine BOM",
         "RevivalTraitorVendor.cs beginnt mit einer BOM")
    strange = sorted(set(c for c in vendor
                         if ord(c) > 126 and not 0x400 <= ord(c) <= 0x4FF))
    need(not strange,
         "ausserhalb ASCII nur Kyrillisch (Spielertext)",
         "RevivalTraitorVendor.cs enthaelt Zeichen, die weder ASCII noch "
         "Kyrillisch sind: " + " ".join("U+%04X" % ord(c) for c in strange))

    # 1 - the game builds him, and the callback is actually reached
    need("Crew.DropCustomSquad(" in vendor,
         "der Haendler entsteht ueber die Spawnpunkt-Kette des Spiels",
         "der Haendler wird nicht mehr ueber Crew.DropCustomSquad gebaut")
    need("internal static GameObject DropCustomSquad(" in crew,
         "Crew.DropCustomSquad gibt den Spawnpunkt an den Aufrufer",
         "Crew.DropCustomSquad fehlt - der Haendler kann kein Haendler werden")
    need("_pointHook(punkt, i)" in crew,
         "der Rueckruf wird vor StartMainInit wirklich aufgerufen",
         "Crew.Absetzen ruft _pointHook nicht mehr auf - der Haendler waere ein "
         "bewaffneter Crewman")

    # 2 - what makes him a trader
    need('SetEnum(sp, "BehaviorPattern", "StoreKeeper")' in vendor,
         "BehaviorPattern StoreKeeper steht auf dem Spawnpunkt",
         "der Spawnpunkt wird nicht mehr auf StoreKeeper gesetzt")
    need('"Kladovshik"' in vendor,
         "er bekommt das Haendler-Prefab des Spiels",
         "das Kladovshik-Prefab wird nicht mehr genannt - der Haendler saehe "
         "aus wie ein Marodeur")

    # 3 - the shop is copied
    for field in ('"StorageId"', '"MarketItemRanksSelling"',
                  '"BuyPlayerItemsPercent"'):
        need(field in vendor,
             "Ladenwert " + field + " wird uebernommen",
             "der Ladenwert " + field + " wird nicht mehr vom Haendler der "
             "Zivilsiedlung abgeschrieben")
    # ... and the search may only remember a hit. The one assignment of the
    # latch has to stand INSIDE the branch that found a storekeeper; a latch
    # above it makes the first miss the answer for the whole session.
    hit = vendor.find("if (best != null)")
    latch = vendor.find("_tradeLooked = true")
    need(hit >= 0 and latch > hit
         and vendor.count("_tradeLooked = true") == 1,
         "die Ladensuche merkt sich nur den Treffer, nicht den Fehlversuch",
         "_tradeLooked wird gesetzt, bevor ein Haendler gefunden wurde - eine "
         "Suche vor dem Laden der Zivilsiedlung wuerde den Verraeter-Haendler "
         "fuer die ganze Sitzung auf die Notwerte festnageln")
    need("Regrade()" in vendor,
         "ein auf Notwerten gebauter Haendler wird spaeter neu gebaut",
         "Regrade fehlt - ein Haendler, der vor der Zivilsiedlung entstand, "
         "behielte seine Notwerte, obwohl die echten inzwischen lesbar sind")

    # 4 - the spawn key and the guard that lets it live
    import re
    key = re.search(r'const string ShopKey = "([^"]*)"', vendor)
    need(key is not None and "/" in key.group(1),
         "der Spawn-Key traegt einen Schraegstrich",
         "der Spawn-Key des Haendlers hat keinen Schraegstrich - "
         "Revival.GroundEnemies.cs wuerde ihn als veraltete Bodengruppe loeschen")
    need("key.IndexOf('/') >= 0" in ground,
         "Bodengruppen lassen fremde Schluessel in Ruhe",
         "der Schutz in Revival.GroundEnemies.cs ist weg - der Haendler wird "
         "beim naechsten Abgleich zerstoert")

    # 5 - the block is borrowed, not damaged
    need("new Mesh()" in vendor and "_cutFilters[i].sharedMesh = _cutBefore[i]" in vendor,
         "ein Schnitt erzeugt ein neues Mesh und wird zurueckgenommen",
         "der Schnitt gibt das Originalmesh nicht mehr zurueck - der Klotz "
         "bliebe fuer die Sitzung beschaedigt")

    # 6 - the place comes from the camp
    need("internal static Vector3 Centre()" in camp
         and "internal static bool Here()" in camp
         and "internal static void Unhate(" in camp,
         "Ort, Karte und Fraktion kommen aus der Siedlung selbst",
         "RevivalNewSettlement.cs gibt Centre/Here/Unhate nicht mehr heraus - "
         "der Haendler findet sein Dorf nicht")
    need("NewSettlement.Unhate(_shop)" in vendor,
         "die Verraeter schiessen nicht auf ihren eigenen Haendler",
         "der Haendler wird nicht mehr aus der Hassliste des Lagers genommen")
    # ... and the shop closes with the camp: a Litvinovka with no traitors in
    # it is no traitor settlement, so a switched-off camp may leave neither an
    # opened block nor a lone trader behind.
    need("internal static bool Wanted()" in camp
         and "NewSettlement.Wanted()" in vendor,
         "der Laden schliesst mit dem Lager",
         "TraitorVendor.Tick fragt NewSettlement.Wanted nicht mehr - bei "
         "abgeschaltetem Verraeterlager bliebe der Klotz offen und ein "
         "einzelner Haendler stuende in einem unveraenderten Litvinovka")

    # 7 - why the first version never stood in the kiosk (6.56.0 log, task
    # 423561235e). He was a crewman in a trader's body: Absichern forced his
    # god mode off and armed the settlement alarm and drone; he had no post
    # (task Empty) and was "held" through a NavMeshAgent field and a pause
    # method that do not exist; and he was built in the middle of the camp
    # because the host was far away, then never carried into the shop.
    need(re.search(r"DropCustomSquad\([^;]*Konfigurieren\),\s*true\)", vendor)
         and "if (_quietSquad) Beruhigen(sied);" in crew
         and "else Absichern(sied);" in crew,
         "der Haendler ist ein ruhiger Trupp: kein Alarm, keine Drohne, kein "
         "erzwungenes GodMode-aus",
         "der Haendler wird wieder als kaempfende Crew gebaut (Absichern: "
         "Alarm, Drohne, SetGodMode(false))")
    code = re.sub(r"//.*", "", vendor)    # the comments name the old bug
    need('"_navAgent"' in code and "_navMeshAgent" not in code
         and "SetCalculatedPauseTime" not in code,
         "der NavMeshAgent wird ueber das echte Feld _navAgent gefunden",
         "TraitorVendor liest wieder ein NPC_AI2-Feld/eine Methode, die es "
         "nicht gibt (_navMeshAgent / SetCalculatedPauseTime)")
    need('Write(sp, "GuardPoint", _post == null ? null : _post.transform)' in vendor
         and "TaskGuard" in vendor,
         "er steht auf Guard an seinem eigenen Posten",
         "der Haendler hat keinen GuardPoint mehr - ohne Posten laeuft er aus "
         "dem Kiosk")
    need(re.search(r"const float KioskX = -?\d", vendor) is not None
         and "TerrainHeight(kiosk" in vendor,
         "er wird sofort am vermessenen Kiosk gebaut, nicht in der Lagermitte",
         "die vermessene Kioskstelle fehlt - ein weit entfernter Host stellt "
         "den Haendler wieder mitten ins Lager")
    need("Snapshot(best)" in vendor and "Konfigurieren" in vendor,
         "der ganze Spawnpunkt des Vorlage-Haendlers wird uebernommen",
         "nur noch einzelne Ladenwerte werden kopiert - Verhalten und Aussehen "
         "weichen vom Haendler der Locator/Zivilsiedlung ab")
    need("Kunden()" in vendor and "SetBehaviorPattern" in vendor,
         "auch die Kopie auf einem Client wird zum Haendler",
         "auf Clients bleibt der Haendler eine Crew-Puppe ohne Laden")

    # the seams and the public repository
    for seam in ("TraitorVendor.BindConfig(Config)", "TraitorVendor.Tick()",
                 "TraitorVendor.LateFrame()"):
        need(seam in plug, "Seam " + seam + " in RevivalPlugin.cs",
             "Seam fehlt in RevivalPlugin.cs: " + seam)
    need('"RevivalTraitorVendor.cs"' in sync,
         "RevivalTraitorVendor.cs geht ins oeffentliche Repository",
         "RevivalTraitorVendor.cs fehlt in sync_public.py - dort baut das Repo "
         "nicht")


def check_gepard():
    """[28] The Gepard: Codex's Blender model as a drivable anti-aircraft gun.

    The order (2026-09-23): the new Gepard model into the game as a vehicle
    like the other new ones; the gunner switches on an automatic radar mode
    whose search radar turns all the time, steps between targets with one key,
    and the sight follows the drone, the helicopter, the tank or the vehicle
    by itself; realistic firing and damage, but no one-shot on a tank; the
    guns go right up. What makes that true without the game:

      1. THE ART IS THE MODEL. Seven ndmesh parts, atlas, metal map and the
         pivot file exist, gepard_import.py and the GLB they come from are in
         the repository, and gepard_rig.txt names every moving part and both
         muzzles. The pivots are read at runtime, never typed into C#.
      2. A REAL VEHICLE: a registry kind on the BTR-80A donor, rebuilt on every
         client by its own cached-spawn marker, spawnable from the admin menu.
      3. NOT THE BTR'S GUNNER. The instance name must not START with
         "BTR-80A" (Turret.IsBtr would hang its own seat and camera on it).
      4. THE RADAR: the search antenna turns in GepardRig.Update, which runs
         on every client whether or not anybody mans the gun; the gunner has a
         radar key and a next-target key; the auto mode lays the guns on an
         intercept (lead) point.
      5. THE GUNS GO UP: PitchMax defaults to 80 degrees or more.
      6. NO ONE-SHOT: a vehicle hit goes through ApplyDamage partType 10 with
         the 0.3 s cadence, a tank needs at least five seconds of continuous
         hits by default, and a helicopter more than one hit.
      7. THE SEAMS: plugin, camera owner, registry, admin, build and public
         repository.
    Plus the file rule: UTF-8 without BOM, outside ASCII only Cyrillic.
    """
    print("[28] Gepard: Flugabwehrkanonenpanzer mit Radar")
    g_p = os.path.join(ROOT, "RevivalGepard.cs")
    if not os.path.exists(g_p):
        bad("RevivalGepard.cs fehlt")
        return
    raw = io.open(g_p, "rb").read()
    g = raw.decode("utf-8", "replace")
    gc = _code(g)

    def read(name):
        p = os.path.join(ROOT, name)
        return io.open(p, encoding="utf-8").read() if os.path.exists(p) else ""

    plug = read("RevivalPlugin.cs")
    cam = read("Revival.CameraTurret.cs")
    ural = read("RevivalUralTruck.cs")
    adm = read("Revival.Admin.cs")
    build = read("build.ps1")
    sync = read("sync_public.py")

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Gepard: " + why)

    # --- file rule
    need(not raw.startswith(b"\xef\xbb\xbf"), "keine BOM",
         "RevivalGepard.cs beginnt mit einer BOM")
    odd = [c for c in g if ord(c) > 126 and not (0x400 <= ord(c) <= 0x4ff)]
    need(not odd, "ausserhalb ASCII nur Kyrillisch (Spielertext)",
         "RevivalGepard.cs enthaelt Zeichen, die weder ASCII noch Kyrillisch "
         "sind (" + "".join(odd[:8]) + ")")

    # --- 1: the art
    parts = ["hull", "tracks", "turret", "gun_r", "gun_l", "radar_search", "radar_track"]
    files = ["gepard_%s.ndmesh" % p for p in parts] + [
        "gepard_diffuse.png", "gepard_metal.png", "gepard_rig.txt"]
    missing = [f for f in files if not os.path.exists(os.path.join(ASSETS, f))]
    need(not missing, "alle %d Gepard-Dateien liegen in assets/" % len(files),
         "es fehlen: " + ", ".join(missing) + " - python gepard_import.py")
    need(os.path.exists(os.path.join(ROOT, "gepard_import.py"))
         and os.path.exists(os.path.join(ASSETS, "gepard", "gepard.glb")),
         "gepard_import.py und das Quell-GLB sind im Repository",
         "gepard_import.py oder assets/gepard/gepard.glb fehlt - die Teile "
         "waeren nicht wiederherstellbar")
    rig_p = os.path.join(ASSETS, "gepard_rig.txt")
    rig = {}
    muzzles = {}
    if os.path.exists(rig_p):
        for line in io.open(rig_p, encoding="ascii"):
            p = line.split()
            if len(p) >= 6 and p[0] == "part":
                rig[p[1]] = [float(x) for x in p[3:6]]
            elif len(p) >= 5 and p[0] == "muzzle":
                muzzles[p[1]] = [float(x) for x in p[2:5]]
    need(all(p in rig for p in parts[2:]) and len(muzzles) == 2
         and all(m[2] > 1.0 for m in muzzles.values()),
         "gepard_rig.txt: Turm, beide Rohre, beide Radare, beide Muendungen",
         "gepard_rig.txt ist unvollstaendig")
    need("gun_r" in rig and rig["gun_r"][0] > 0 and "gun_l" in rig and rig["gun_l"][0] < 0,
         "rechtes Rohr bei +x, linkes bei -x (Unity-Achsen, gespiegelt aus glTF)",
         "die Rohre stehen auf der falschen Seite - gepard_import.py spiegelt x nicht")
    need("gepard_rig.txt" in gc and "ReadRig(" in gc,
         "die Drehpunkte kommen zur Laufzeit aus gepard_rig.txt",
         "RevivalGepard.cs liest gepard_rig.txt nicht")

    # --- 2: a real vehicle
    need('Add(Make("gepard"' in ural and "Gepard.Umbauen" in ural
         and "GepardNet.SpawnData" in ural,
         "Fahrzeugregister: Art \"gepard\" mit Umbau und Spawnmarker",
         "der Gepard steht nicht im VehicleRegistry")
    need('Prefab = "btr-80a_spawn"' in gc, "Spender ist der BTR-80A",
         "der Spender ist nicht der BTR-80A")
    need("NDR_GEPARD_V1" in gc and "DoInstantiate" in gc and "Gepard.Umbauen(__result)" in gc,
         "Umbau auf jedem Client ueber den Spawnmarker",
         "kein Spawnmarker - andere Spieler saehen einen BTR")
    need("Gepard.SpawnInFront()" in adm, "Knopf im Adminmenue",
         "kein Spawnknopf im Adminmenue")

    # --- 3: never the BTR's gunner
    need('car.name = "Gepard_" + car.name + Marke;' in gc,
         "Instanzname beginnt mit \"Gepard_\" (Turret.IsBtr greift nicht)",
         "der Instanzname koennte mit BTR-80A beginnen - dann haengt sich das "
         "BTR-Geschuetz an den Gepard")

    # --- 4: the radar
    import re
    upd = re.search(r"void Update\(\)\s*\{(.*?)\n        \}", gc, re.S)
    body = upd.group(1) if upd else ""
    need("RadarSearch.localRotation" in body and "LocalControl" in body,
         "Suchradar dreht in GepardRig.Update auf jedem Client, bemannt oder nicht",
         "das Suchradar dreht nicht in GepardRig.Update")
    need("CfgRadarKey" in gc and "CfgNextKey" in gc and "NextTarget()" in gc,
         "Radartaste und Zielwechseltaste",
         "Radar- oder Zielwechseltaste fehlt")
    need("Intercept(" in gc and "_lock.Vel" in gc,
         "Radarbetrieb richtet auf den Vorhaltepunkt",
         "die automatische Richtung rechnet keinen Vorhalt")
    need("CameraOwner.Gepard" in gc and "else if (_owner == Gepard) GepardGun.LateTick();" in cam
         and "public const int Gepard = 6;" in cam,
         "Visierkamera ueber CameraOwner (Halter 6)",
         "die Visierkamera ist nicht bei CameraOwner angemeldet")

    # --- 5: the guns go up
    m = re.search(r'"PitchMax",\s*([0-9.]+)f', g)
    need(m is not None and float(m.group(1)) >= 80.0,
         "Rohrerhoehung bis %s Grad" % (m.group(1) if m else "?"),
         "PitchMax unter 80 Grad - der Gepard muss nach oben schiessen koennen")

    # --- 6: balance
    need("new object[] { perHit, 10 }" in gc and "Time.time + 0.3f" in gc,
         "Fahrzeugtreffer ueber ApplyDamage partType 10 im 0.3-s-Takt",
         "Fahrzeugschaden laeuft nicht ueber den Panzerungspfad des Spiels")
    m = re.search(r'"TankSeconds",\s*([0-9.]+)f', g)
    need(m is not None and float(m.group(1)) >= 5.0,
         "ein Panzer braucht %s s Dauerfeuer" % (m.group(1) if m else "?"),
         "TankSeconds unter 5 - der Panzer faellt zu schnell")
    m = re.search(r'"HeliHits",\s*([0-9]+)', g)
    need(m is not None and int(m.group(1)) > 1,
         "ein Hubschrauber haelt %s Treffer" % (m.group(1) if m else "?"),
         "HeliHits 1 - dann ist die Kanone eine Stinger")
    m = re.search(r'"NetworkEventCode",\s*([0-9]+)', g)
    taken = set([160, 161, 162, 164, 170, 171, 172, 173, 174, 175, 176, 177, 178,
                 179, 180, 181, 182, 183, 184, 185, 190, 191, 196])
    need(m is not None and int(m.group(1)) not in taken and int(m.group(1)) < 200,
         "Ereigniscode %s ist frei" % (m.group(1) if m else "?"),
         "der Ereigniscode des Gepard ueberschneidet einen anderen Kanal")

    # --- 7: seams
    for seam in ("Gepard.BindConfig(Config)", "Gepard.Install(_harmony)",
                 "Gepard.Tick()", "Gepard.Draw()"):
        need(seam in plug, "Seam " + seam + " in RevivalPlugin.cs",
             "Seam fehlt in RevivalPlugin.cs: " + seam)
    need(all('"%s"' % f in build for f in files),
         "build.ps1 installiert alle Gepard-Dateien",
         "build.ps1 kopiert nicht alle Gepard-Dateien ins Spiel")
    need('"RevivalGepard.cs"' in sync and '"gepard_import.py"' in sync,
         "RevivalGepard.cs und gepard_import.py gehen ins oeffentliche Repository",
         "sync_public.py kennt RevivalGepard.cs oder gepard_import.py nicht - "
         "dort baut das Repo nicht")
    if GAME_PLUGINS and os.path.exists(os.path.join(GAME_PLUGINS, "NextDayRevivalToolkit.dll")):
        dst = os.path.join(GAME_PLUGINS, "assets")
        if not os.path.exists(os.path.join(dst, "gepard_rig.txt")):
            warn("noch nicht installiert: gepard_rig.txt (und die Gepard-Teile)")


def check_gepard_npc():
    """[28b] The Gepard as an NPC vehicle (docs/ai/tasks/gepard-npc.md).

    The order (2026-09-26): the Gepard selectable in the route editor like
    the other armed vehicles, an NPC crew that rides via the Photon spawn key,
    a gunner who engages aircraft first and ground targets second, and a
    stationary "AA site" that holds its position. What makes that true
    without the game:

      1. THE EDITOR OFFERS IT: compdef.VEHICLE_TYPES, its three seats, the
         shipped catalogue sample, and a served catalogue that appends a kind
         an older extraction lacks.
      2. THE KEY HAS A SLASH: "gep/" - Revival.GroundEnemies.cs deletes every
         keyed NPC without one - and the guard there is still generic.
      3. NO NPC IN Passengers, as for the technical.
      4. AIR FIRST: the search looks at aircraft and only falls through to the
         ground when no aircraft was chosen; NPC rounds are judged against the
         gunner's own contacts.
      5. THE DONOR'S HIDDEN BTR TURRET never fires on a Gepard patrol.
      6. THE AA SITE: `hold` read by Patrol and held in FixedTick; compdef
         writes it and refuses it on a convoy.
      7. SEAMS: plugin, Gepard.Tick, Patrol.UnloadCrew, public repository.
    """
    import re
    print("[28b] Gepard als NPC-Fahrzeug (Editor, Besatzung, Flugabwehr zuerst, Stellung)")

    def read(name):
        p = os.path.join(ROOT, name)
        return io.open(p, encoding="utf-8").read() if os.path.exists(p) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Gepard NPC: " + why)

    crew_raw = read("RevivalGepardCrew.cs")
    if not crew_raw:
        bad("RevivalGepardCrew.cs fehlt")
        return
    crew = _code(crew_raw)
    need(all(ord(ch) < 128 for ch in crew_raw) and not crew_raw.startswith("\ufeff"),
         "RevivalGepardCrew.cs ist ASCII ohne BOM",
         "RevivalGepardCrew.cs ist nicht ASCII oder hat eine BOM")
    comp = read("compdef.py")
    cat = read(os.path.join("assets", "editor", "catalogue_sample.json"))
    catpy = read(os.path.join("research", "catalogue.py"))
    editor = read("routeeditor.py")
    app = read(os.path.join("editor", "app.js"))
    html = read(os.path.join("editor", "index.html"))
    patrol = _code(read("Revival.Patrol.cs"))
    ground = read("Revival.GroundEnemies.cs")
    gep = _code(read("RevivalGepard.cs"))
    plug = read("RevivalPlugin.cs")
    sync = read("sync_public.py")

    # --- 1: the editor
    types = re.search(r"^VEHICLE_TYPES = \[(.*?)\]", comp, re.M)
    need(types is not None and '"gepard"' in types.group(1),
         "compdef.VEHICLE_TYPES enthaelt gepard",
         "compdef.VEHICLE_TYPES kennt den Gepard nicht - der Editor bietet ihn nicht an")
    need('"gepard": ["driver", "gunner", "commander"]' in comp,
         "drei Plaetze: driver, gunner, commander (Sitzreihenfolge RevivalGepard.cs)",
         "VEHICLE_SEATS fehlt fuer den Gepard")
    need('"key": "gepard"' in cat and '"key": "gepard"' in catpy,
         "Katalog (Beispiel und research/catalogue.py) fuehrt den Gepard",
         "der Katalog kennt den Gepard nicht")
    need("catalogue_with_vehicles(" in editor and "compdef.VEHICLE_TYPES" in editor,
         "/api/catalogue ergaenzt fehlende Fahrzeugarten eines aelteren Katalogs",
         "ein alter Katalog auf dem Server versteckt den Gepard weiter")
    need("rHold" in app and 'id="rHold"' in html and "gepard:" in app,
         "Editor: Gepard-Sitze und Kaestchen 'Hold position (AA site)'",
         "Editor-Oberflaeche ohne Gepard-Sitze oder Stellungs-Kaestchen")

    # --- 2: the key
    need('KeyPrefix = "gep/"' in crew and 'KeyGunner = "/g"' in crew
         and 'KeyCrew = "/c"' in crew and "Crew.DropGroundSquad(" in crew,
         "Besatzung ueber den Photon-Spawnschluessel gep/<view>/g|c",
         "die Gepard-Besatzung hat keinen Schluessel mit Schraegstrich")
    need("key.IndexOf('/') >= 0" in ground,
         "GroundEnemies laesst Schluessel mit Schraegstrich in Ruhe",
         "Revival.GroundEnemies.cs loescht Schluessel mit Schraegstrich")

    # --- 3: no NPC in Passengers (reading it is allowed)
    need(not re.search(r"Passengers[^;]*SetValue|SetValue\([^;]*Passengers", crew),
         "kein NPC im Passengers-Array",
         "RevivalGepardCrew.cs schreibt in Passengers")

    # --- 4: air first
    m = re.search(r"static void Suchen\(Hull h\)(.*?)\n        \}", crew, re.S)
    body = m.group(1) if m else ""
    ground_at = body.find("NpcWar.PatrolTargets(")
    need("PlayerHeli.MissileTargets(" in body and "GepardAir.Collect(" in body
         and 0 <= body.find("GepardAir.Collect(") < ground_at
         and 0 <= body.find("if (best != null)") < ground_at,
         "Luftziele (Mi-8, An-2 ueber GepardAir) vor Bodenzielen",
         "die NPC-Suche nimmt Bodenziele nicht erst nach den Luftzielen")
    need("GepardShots.Fire(h.Rig, gun, dir, true, h.Air)" in crew
         and "GepardGun.Proximity(r.Npc," in gep and "GepardGun.Struck(r.Npc," in gep,
         "NPC-Schuesse werden gegen die eigenen Kontakte des Richtschuetzen gewertet",
         "NPC-Schuesse laufen ueber die Kontakte des lokalen Spielers")
    need("GepardNet.SendPose(" in crew,
         "Turm und Abzug gehen ueber das Gepard-Ereignis an alle Clients",
         "andere Spieler saehen den NPC-Turm nicht")

    # --- 5: the donor turret stays silent
    need("if (Gepard.IstGepard(u.Car.transform)) all = new Transform[0];" in patrol,
         "Patrol.Gun.Collect laesst den versteckten BTR-Turm des Spenders aus",
         "der unsichtbare BTR-Turm eines Gepard wuerde feuern")

    # --- 6: the AA site
    need('r.Site = HasFlag(p, "hold");' in patrol
         and "if (u.Route.Site && u.ConvoyId == 0) { HoldStill(u); continue; }" in patrol
         and 'if (r.Site) keep.Add("hold");' in patrol,
         "Stellung: Flag hold, das Fahrzeug steht gebremst am Startpunkt",
         "Patrol kennt die Stellung (hold) nicht")
    need('keep.append("hold")' in comp and "hold position (AA site) is a patrol option" in comp,
         "compdef schreibt hold nur fuer Patrouillen",
         "compdef schreibt oder prueft hold nicht")

    # --- 7: seams
    for seam in ("GepardCrew.BindConfig(Config)", "GepardCrew.Install(_harmony)",
                 "GepardCrew.LateFrame()"):
        need(seam in plug, "Seam " + seam + " in RevivalPlugin.cs",
             "Seam fehlt in RevivalPlugin.cs: " + seam)
    need("GepardCrew.Tick()" in gep, "Gepard.Tick ruft GepardCrew.Tick",
         "GepardCrew.Tick wird nicht gerufen")
    need("GepardCrew.ReleaseRiders(u.Car, u.Seite)" in patrol
         and "GepardCrew.StopAll()" in patrol,
         "Patrol: Wrack gibt die Besatzung frei, StopAll nimmt sie mit",
         "Patrol.UnloadCrew/StopAll kennen die Gepard-Besatzung nicht")
    need('"RevivalGepardCrew.cs"' in sync,
         "RevivalGepardCrew.cs geht ins oeffentliche Repository",
         "sync_public.py kennt RevivalGepardCrew.cs nicht - dort baut das Repo nicht")


def check_katyusha():
    """[28k] The Katyusha rocket launcher (RevivalKatyusha.cs,
    docs/ai/tasks/n07-katyusha.md). What holds without the game:
      - on by default, its own item id (2075) and event code (188) free;
      - the art exists and the rail slots in the plugin are the generator's;
      - the fire control is the MAP and a large AREA: the impact point is
        drawn uniformly over the ellipse (sqrt of a uniform radius), the salvo
        takes every loaded rocket, and a centre is refused (never clamped) out
        of range or touching a safe zone;
      - effects are pooled hand-emitted particles (no network explosion per
        rocket), the depot is set off by the master only;
      - every seam and file list.
    The picture on the truck, the feel of the lay and the salvo, and the
    damage in the field are in-game acceptance items.
    """
    import re
    import glob
    print("[28k] Katyusha")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Katyusha: " + why)

    path_k = os.path.join(ROOT, "RevivalKatyusha.cs")
    if not os.path.exists(path_k):
        bad("RevivalKatyusha.cs is missing")
        return
    raw_k = io.open(path_k, "rb").read()
    need(not raw_k.startswith(b"\xef\xbb\xbf"), "RevivalKatyusha.cs has no BOM",
         "RevivalKatyusha.cs starts with a BOM")
    code = _code(raw_k.decode("utf-8", "replace"))

    need(re.search(r'cfg\.Bind\(S, "Enabled", true,', code) is not None,
         "[Katyusha] Enabled is on by default", "[Katyusha] Enabled is not on by default")
    need("internal const int ItemId = 2075;" in code, "the M-13 rocket is item 2075",
         "the M-13 item id moved away from 2075")
    claimed = []
    for p in glob.glob(os.path.join(ROOT, "*.cs")):
        if os.path.basename(p) == "RevivalKatyusha.cs":
            continue
        if re.search(r"\b2075\s*,\s*[0-9]{4}\s*,\s*(true|false)", io.open(p, encoding="utf-8").read()):
            claimed.append(os.path.basename(p))
    need(not claimed, "no other ItemDef claims 2075", "2075 also defined in " + ", ".join(claimed))
    game = set()
    tsv = os.path.join(ROOT, "research", "items.tsv")
    if os.path.exists(tsv):
        for line in io.open(tsv, encoding="utf-8", errors="replace"):
            p = line.split("\t")
            if len(p) > 1 and p[1].isdigit():
                game.add(int(p[1]))
    need(2075 not in game, "2075 is not an id of the game", "2075 is already an item of the game")
    need(re.search(r"SellOnlyIds[^;]*2075", read("Revival.Items.cs")) is not None,
         "the M-13 has a trader's sell price", "2075 is missing from SellOnlyIds")
    codes = []
    for p in glob.glob(os.path.join(ROOT, "*.cs")):
        if os.path.basename(p) == "RevivalKatyusha.cs":
            continue
        for m in re.finditer(r'"[A-Za-z]*(?:EventCode|Code)"\s*,\s*([0-9]+)', io.open(p, encoding="utf-8").read()):
            codes.append(int(m.group(1)))
    need('"NetworkEventCode", 188,' in code and 188 not in codes,
         "event code 188 is the Katyusha's alone", "event code 188 is not free: %s" % sorted(set(codes)))

    for f in ("katyusha_mount.ndmesh", "katyusha_base.ndmesh", "katyusha_rack.ndmesh",
              "katyusha_diffuse.png", "m13.ndmesh", "m13_diffuse.png", "m13_normal.png", "m13_icon.png"):
        need(os.path.exists(os.path.join(ASSETS, f)) and '"' + f + '"' in read("build.ps1"),
             f + " built and packaged", f + " missing from assets/ or build.ps1")
    gen = read("katyusha_build.py")
    pairs = (("RAIL_PITCH", "RailPitch"), ("ROCKET_OFF", "RocketOff"), ("ROCKET_Z", "RocketZ"),
             ("TRUNNION_Y", "TrunnionY"))
    for py, cs in pairs:
        a = re.search(r"^%s = (-?[0-9.]+)" % py, gen, re.M)
        b = re.search(r"const float %s = (-?[0-9.]+)f;" % cs, code)
        need(a is not None and b is not None and abs(float(a.group(1)) - float(b.group(1))) < 1e-6,
             "rail slot %s matches katyusha_build.py" % cs,
             "%s in the plugin differs from %s in katyusha_build.py" % (cs, py))
    z1 = re.search(r"RAIL_Z0, RAIL_Z1 = (-?[0-9.]+), (-?[0-9.]+)", gen)
    front = re.search(r"const float RailFront = (-?[0-9.]+)f;", code)
    need(z1 is not None and front is not None and abs(float(z1.group(2)) - float(front.group(1))) < 1e-6,
         "rail front matches katyusha_build.py", "RailFront differs from RAIL_Z1")
    need('("katyusha", ["katyusha_build.py"])' in read("make_assets.py"),
         "make_assets.py has the katyusha group", "make_assets.py lacks the katyusha group")

    rnd = _body(code, "internal static Vector3 RandomInEllipse(")
    need("Math.Sqrt(rng.NextDouble())" in rnd and "semiLength" in rnd and "semiWidth" in rnd,
         "impact points are uniform over the ellipse area",
         "RandomInEllipse no longer draws uniformly per area")
    fire = _body(code, "static void Fire(Launcher l, Vector3 centre, float bearing)")
    need("int n = CountOf(l);" in fire and "RandomInEllipse(" in fire and "KatyushaNet.Send(msg, true);" in fire,
         "the salvo takes every loaded rocket and sends the points reliably",
         "Fire no longer fires all loaded rockets into the ellipse")
    ref = _body(code, "internal static string Refusal(Launcher l, Vector3 centre)")
    need("MinRange" in ref and "MaxRange" in ref and "SafeZones.Overlaps(" in ref and "SafeZones.Inside(" in ref,
         "minimum/maximum range and safe zones refuse a salvo",
         "Refusal lost the range or safe-zone rule")
    need("Mortar.ShowMap(true)" in code and "Mortar.MapPoint(" in code
         and "internal static bool ShowMap(bool open)" in read("RevivalMortar.cs"),
         "the fire control is the game's map (the howitzer's map seams)",
         "the fire control no longer opens the map")
    need("RocketHook.Detonate" not in _body(code, "internal static void Burst(Vector3 point, bool mine)")
         and "Fx.Keep()" in code and "ps.Emit(e, 1);" in code,
         "bursts are pooled local effects, not a network explosion per rocket",
         "a rocket burst spawns a network explosion")
    need("if (master) Depot(point);" in code and "FuelDepot.Blast(" in code,
         "the fuel depots take rocket bursts on the master", "the depot hit is gone or not master-only")
    need("KatyushaTravel" in code and '"InputAxis"' in code,
         "travel lock: the truck does not drive with the rack out", "the travel lock is gone")

    plug = read("RevivalPlugin.cs")
    for seam in ("Katyusha.BindConfig(Config);", "Katyusha.Install(_harmony);", "Katyusha.AddItems(Items);",
                 "Katyusha.Tick();", "Katyusha.Draw();"):
        need(seam in plug, "RevivalPlugin: " + seam, "RevivalPlugin lost " + seam)
    need('Add(Make("katyusha", KatyushaText.Label(),' in read("RevivalUralTruck.cs"),
         "VehicleRegistry has the katyusha", "the katyusha is not in VehicleRegistry")
    adm = read("Revival.Admin.cs")
    need("Katyusha.SpawnInFront()" in adm and "GibItem(Katyusha.ItemId" in adm,
         "admin panel: spawn a Katyusha and its rockets", "an admin button for the Katyusha is gone")
    need("Katyusha.Pool(pool, entries)" in read("Revival.Airfield.cs"),
         "the airfield loot rolls M-13 rockets", "the airfield loot lost the M-13")
    need('"RevivalKatyusha.cs", "katyusha_build.py"' in read("sync_public.py"),
         "sync_public ships the Katyusha", "sync_public.py does not ship RevivalKatyusha.cs")
    need(os.path.exists(os.path.join(ROOT, "docs", "ai", "tasks", "n07-katyusha.md")),
         "task report docs/ai/tasks/n07-katyusha.md", "docs/ai/tasks/n07-katyusha.md is missing")


def check_airfield():
    """[33] East airfield phase 1 (Revival.Airfield.cs).

    Loot points, defender pockets and the event budget of the airfield
    (docs/ai/tasks/airfield-gameplay-p1.md). Pinned statically: the whole
    feature acts only in the east world; every loot point names a building id
    the greybox recipe really has (so a final model that keeps the id keeps
    its loot) and a pool that exists; the pockets stand inside the fence, off
    every building footprint, and add up to the concept's 13-18 men; the
    ground groups, the troop landings and the convoy each carry their seam.
    Whether the pickups sit on the floors, the men walk their routes and the
    budget holds in a real round is the in-game checklist.
    """
    import json
    import re
    print("[33] East airfield phase 1 (loot, defenders, event budget)")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Airfield: " + why)

    raw_p = os.path.join(ROOT, "Revival.Airfield.cs")
    if not os.path.exists(raw_p):
        bad("Airfield: Revival.Airfield.cs fehlt")
        return
    raw = io.open(raw_p, "rb").read()
    a = raw.decode("utf-8", "replace")
    need(not raw.startswith(b"\xef\xbb\xbf") and not [c for c in a if ord(c) > 126],
         "Revival.Airfield.cs: ASCII ohne BOM",
         "Revival.Airfield.cs enthaelt eine BOM oder Zeichen ausserhalb ASCII")
    need("get { return EastWorld.On && (CfgEnabled == null || CfgEnabled.Value); }" in a,
         "wirkt nur in der Ostwelt ([World] EastTile)",
         "Airfield.On haengt nicht mehr an EastWorld.On - der Flugplatz wirkte "
         "auch auf der Heimatkarte")
    for key, default in (("Enabled", "true"), ("Loot", "true"), ("Defenders", "true")):
        need(re.search(r'cfg\.Bind\("Airfield", "%s", %s,' % (key, default), a) is not None,
             "[Airfield] %s = %s" % (key, default),
             "[Airfield] %s ist nicht mehr mit %s gebunden" % (key, default))
    need('cfg.Bind("Airfield", "EventBudget", 1,' in a,
         "[Airfield] EventBudget = 1 (Landung oder Konvoi, nie beides)",
         "das Ereignisbudget steht nicht mehr auf 1")

    # --- loot points against the greybox recipe
    recipe_p = os.path.join(ROOT, "unity", "EastTile", "Content", "east_airfield.json")
    try:
        recipe = json.load(io.open(recipe_p, encoding="utf-8"))
        gb = recipe["greybox"]
    except Exception as e:
        bad("Airfield: east_airfield.json nicht lesbar: %s" % e)
        return
    ids = set(b["id"] for b in gb.get("blocks", [])) | set(z["id"] for z in gb.get("zones", []))
    slots = re.findall(r'new Slot\("([A-Za-z0-9]+)", (\d), "([a-z]+)",\s*(-?[\d.]+)f,\s*(-?[\d.]+)f,\s*([\d.]+)f\)', a)
    pools = set(re.findall(r'p\["([a-z]+)"\] = new string\[\]', a))
    need(len(slots) >= 20, "%d Lootpunkte" % len(slots), "weniger als 20 Lootpunkte gefunden")
    field_slots = re.findall(r'FieldSlot\(new Slot\("([A-Za-z0-9]+)", (\d), "([a-z]+)",\s*(-?[\d.]+)f,\s*(-?[\d.]+)f,\s*([\d.]+)f\),\s*"([A-Za-z0-9]+)",\s*(-?[\d.]+)f,\s*(-?[\d.]+)f\)', a)
    if field_slots:
        # TU95 rolls sit in the parked bomber's hold (z-v2), not on a recipe id
        anchored = set()
        if 'if (s.Building == "TU95")' in a and "ParkedTu95.Loot(" in a:
            anchored.add("TU95")
        plain = re.findall(r'(?<!FieldSlot\()new Slot\("([A-Za-z0-9]+)"', a)
        need(all(pid in anchored for pid in plain), "every field loot roll has a surviving anchor",
             "partial field loot relocation loses recipe validation: " + ", ".join(sorted(set(plain) - anchored)))
        target_ids = set(s[6] for s in field_slots)
        need(all(2500 < float(s[7]) < 6500 and abs(float(s[8])) < 2500 for s in field_slots),
             "field loot anchors remain on the east tile", "field loot anchor is off the tile")
    else:
        target_ids = set(s[0] for s in slots)
    unknown = sorted(target_ids - ids)
    need(not unknown, "jeder aktive Lootpunkt nennt eine Greybox-ID",
         "Lootpunkte an IDs, die es im Greybox-Rezept nicht gibt: " + ", ".join(unknown))
    used = set(s[0] for s in slots)
    for must in ("H1", "H2", "C1", "F1", "B1", "D1", "D2a", "D3", "S1", "S2", "S4", "W1", "M1a", "AN"):
        need(must in used, "Loot in " + must, "kein Lootpunkt in " + must)
    need("S3" not in used and "D2b" not in used,
         "S3 und D2b bleiben verschlossen (spaetere Phase)",
         "S3 oder D2b hat Loot - beide sind fuer Quest/Schluessel reserviert")
    bad_pool = sorted(set(s[2] for s in slots) - pools)
    need(not bad_pool, "jeder Pool ist definiert", "unbekannte Pools: " + ", ".join(bad_pool))
    need(all(-0.5 <= float(s[3]) <= 0.5 and -0.5 <= float(s[4]) <= 0.5 for s in slots),
         "alle Punkte liegen im Markerkasten ihres Gebaeudes",
         "ein Lootpunkt liegt ausserhalb seines Markerkastens (Anteil > 0.5)")
    need(all(int(s[1]) <= 3 for s in slots) and any(s[1] == "3" for s in slots),
         "Stufen 0-3, mit Signaturpunkten",
         "Lootstufe ausserhalb 0-3 oder keine Stufe 3")
    sure = set(s[0] for s in slots if float(s[5]) >= 1.0)
    for must in ("H1", "F1", "D1c", "D2a"):
        need(must in sure, "Reset garantiert eine Kategorie in " + must,
             must + " hat keinen garantierten Punkt (Konzept: H1, F1, D1, D2)")
    need("InstantiateSceneObject" in a and "GetRandomItemByCategory" in a
         and "s.At + new Vector3(0f, 0.8f, 0f)" in a,
         "das Spiel spawnt seinen eigenen Loot, wie ItemSpawnPoint",
         "der Loot kommt nicht mehr aus den Kategorien des Spiels")
    need("if (!master) {" in a and "now + ResetSeconds(s.Tier)" in a,
         "nur der Master spawnt; ein neuer Master wartet einen Reset",
         "Loot wird nicht mehr nur vom Master gesetzt oder fuellt bei Masterwechsel auf")

    # --- defender pockets
    pockets = re.findall(r'new Pocket\("([A-Za-z0-9_.-]+)", "(\w+)", (\d+), ([\d.]+)f,(.*?)\)(?=,\s*new Pocket|\s*\};)',
                         a, re.S)
    total = sum(int(p[2]) for p in pockets)
    need(len(set(p[0].split("-")[1] for p in pockets)) == 4,
         "vier Nester N1-N4", "nicht genau vier Verteidigernester N1-N4")
    need(13 <= total <= 18, "%d Verteidiger (Konzept 13-18)" % total,
         "%d Verteidiger - das Konzept verlangt 13-18" % total)
    blocks = []
    for b in gb.get("blocks", []):
        k = 1.0 if b.get("units", recipe.get("sizeUnits", "m")) == "u" else 2.8
        if b.get("kind") in ("post", "panel", "mast"):
            continue
        blocks.append((b["id"], b["x"], b["z"], b["w"] * k / 2.0, b["d"] * k / 2.0))
    inside_all = True
    for p in pockets:
        pts = [(float(x), float(z)) for x, z in re.findall(r'P\((-?[\d.]+)f, (-?[\d.]+)f\)', p[4])]
        need(pts and (p[1] != "patrol" or len(pts) >= 2),
             p[0] + ": " + p[1] + " mit %d Punkt(en)" % len(pts),
             p[0] + ": Streife ohne Route oder ohne Punkt")
        for x, z in pts:
            if not (3990 <= x <= 4820 and -1690 <= z <= 1690):
                inside_all = False
                bad("Airfield: %s Punkt (%g, %g) liegt ausserhalb des Zauns" % (p[0], x, z))
            for bid, bx, bz, hw, hd in blocks:
                if abs(x - bx) < hw and abs(z - bz) < hd:
                    inside_all = False
                    bad("Airfield: %s Punkt (%g, %g) steht in %s" % (p[0], x, z, bid))
    need(inside_all, "alle Nestpunkte im Zaun und neben den Gebaeuden",
         "ein Nestpunkt liegt ausserhalb des Zauns oder in einem Gebaeude")

    # --- seams
    g = read("Revival.GroundEnemies.cs")
    troop = read("RevivalTroopInsertion.cs")
    convoy = read("RevivalConvoy.cs")
    plug = read("RevivalPlugin.cs")
    zones = read("Revival.EastZones.cs")
    sync = read("sync_public.py")
    need("Airfield.AddGroups(fresh);" in g and "Airfield.HoldSpawn(g)" in g,
         "die Nester laufen als Bodengruppen, Respawn nie vor Spielern",
         "die Bodengruppen kennen die Flugplatznester nicht mehr")
    need("Airfield.BlocksTroop(" in troop and "internal static int AirfieldActive()" in troop,
         "Truppenlandungen fragen das Ereignisbudget",
         "Truppenlandungen ignorieren das Ereignisbudget des Flugplatzes")
    need("Airfield.BlocksConvoy(pts)" in convoy and "OffAirfield(routes)" in convoy
         and "internal static int AirfieldActive()" in convoy,
         "Konvois fragen das Ereignisbudget",
         "Konvois ignorieren das Ereignisbudget des Flugplatzes")
    need("internal static bool Find(string id, out Vector3 centre" in zones,
         "Lootpunkte haengen an den Markern von EastZones",
         "EastZones.Find fehlt - die Lootpunkte haben keinen Anker")
    need("Airfield.BindConfig(Config);" in plug and "Airfield.Tick();" in plug,
         "Seams Airfield.BindConfig/Tick in RevivalPlugin.cs",
         "Seam fehlt in RevivalPlugin.cs: Airfield.BindConfig oder Airfield.Tick")
    need('"Revival.Airfield.cs"' in sync,
         "Revival.Airfield.cs geht ins oeffentliche Repository",
         "Revival.Airfield.cs fehlt in sync_public.py")
    need(os.path.exists(os.path.join(ROOT, "docs", "ai", "tasks", "airfield-gameplay-p1.md")),
         "Abnahmeliste docs/ai/tasks/airfield-gameplay-p1.md",
         "docs/ai/tasks/airfield-gameplay-p1.md fehlt")


def check_military_town():
    """[34] Military town gameplay (Revival.MilitaryTown.cs).

    The town as a hard, rewarding destination
    (docs/ai/tasks/military-town-gameplay.md). Pinned statically, against the
    town recipe (unity/EastTile/Content/east_town.json) so everything keys on
    MT building ids that really exist:

      1. EAST WORLD ONLY: MilitaryTown.On hangs on EastWorld.On; the config
         keys are default on.
      2. LOOT: every point names a recipe id and a defined pool; tier 4 only
         in the armoury H2 (the best tier of the east - the airfield stops at
         3), at least three guaranteed there, all of them kept inside the HQ
         compound; 122 mm shells at both ammunition niches; the officers'
         flats in A3.
      3. DEFENDERS: every point inside the fence and off every footprint; the
         HQ core inside the compound; street patrols on the streets; spotter
         and sniper posts on real buildings, one post per posted man.
      4. BATTERIES: four recipe pits, a town post never drones, spots only
         through a living spotter and never into the town.
      5. ROUTES: the generated data is what research/mt_gameplay.py writes;
         the AA site holds a Gepard on AA1; the reinforcement runs from
         outside the airfield's budget margin into the fence; built-in routes
         are never saved, drawn by the random convoy or driven early.
      6. BUDGET, seams, public repository, the acceptance doc.
    """
    import json
    import math
    import re
    print("[34] Military town gameplay (batteries, spotters, AA site, defenders, loot)")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Military town: " + why)

    raw_p = os.path.join(ROOT, "Revival.MilitaryTown.cs")
    if not os.path.exists(raw_p):
        bad("Military town: Revival.MilitaryTown.cs is missing")
        return
    for name in ("Revival.MilitaryTown.cs", "Revival.MilitaryTownData.cs"):
        raw = io.open(os.path.join(ROOT, name), "rb").read() if os.path.exists(os.path.join(ROOT, name)) else b""
        need(raw and not raw.startswith(b"\xef\xbb\xbf") and all(b < 127 for b in raw),
             name + ": ASCII without BOM", name + " is missing, has a BOM or non-ASCII bytes")
    m = io.open(raw_p, encoding="utf-8").read()
    code = _code(m)
    need("get { return EastWorld.On && (CfgEnabled == null || CfgEnabled.Value); }" in m,
         "acts only in the east world ([World] EastTile)",
         "MilitaryTown.On no longer hangs on EastWorld.On")
    for key in ("Enabled", "Loot", "Defenders", "Batteries", "AaSite", "VehiclePatrol", "Reinforcements"):
        need(re.search(r'cfg\.Bind\("MilitaryTown", "%s", true,' % key, m) is not None,
             "[MilitaryTown] %s = true" % key, "[MilitaryTown] %s is not bound default true" % key)

    try:
        recipe = json.load(io.open(os.path.join(ROOT, "unity", "EastTile", "Content", "east_town.json"),
                                   encoding="utf-8"))
        gb = recipe["greybox"]
    except Exception as e:
        bad("Military town: east_town.json unreadable: %s" % e)
        return
    ids = set(b["id"] for b in gb.get("blocks", [])) | set(z["id"] for z in gb.get("zones", []))
    air = read("Revival.Airfield.cs")
    pools = set(re.findall(r'p\["([a-z]+)"\] = new string\[\]', air))

    # --- 2: loot
    slots = re.findall(r'S\("(MT-[A-Za-z0-9]+)", (\d), "([a-z]+)",\s*(-?[\d.]+)f,\s*(-?[\d.]+)f,\s*([\d.]+)f\)', code)
    need(len(slots) >= 40, "%d town loot points" % len(slots), "fewer than 40 town loot points")
    unknown = sorted(set(s[0] for s in slots) - ids)
    need(not unknown, "every loot point names an MT id of the recipe",
         "loot at ids the town recipe does not have: " + ", ".join(unknown))
    bad_pool = sorted(set(s[2] for s in slots) - pools)
    need(not bad_pool, "every pool is defined in Airfield.MakePools", "undefined pools: " + ", ".join(bad_pool))
    need(all(-0.5 <= float(s[3]) <= 0.5 and -0.5 <= float(s[4]) <= 0.5 for s in slots),
         "every point lies in its building's marker box", "a loot fraction is beyond 0.5")
    tier4 = [s for s in slots if s[1] == "4"]
    need(tier4 and all(s[0] == "MT-H2" and s[2] == "armoury" for s in tier4)
         and sum(1 for s in tier4 if float(s[5]) >= 1.0) >= 3,
         "tier 4 only in the H2 armoury, %d points, 3+ guaranteed" % len(tier4),
         "tier 4 is not the armoury's alone or has fewer than 3 guaranteed points")
    air_tiers = re.findall(r'new Slot\("[A-Za-z0-9]+", (\d),', air)
    need(air_tiers and max(int(t) for t in air_tiers) < 4,
         "the armoury is the best tier of the east (airfield tops out at %s)" % max(air_tiers or ["?"]),
         "an airfield point reaches tier 4 - the armoury would not be the best tier")
    hq_lines = [ln for ln in code.split("\n") if 'S("MT-H2"' in ln or 'S("MT-H1"' in ln]
    need(hq_lines and all("Hq(S(" in ln for ln in hq_lines)
         and "s.KeepMinX = 5759f; s.KeepMaxX = 5844f; s.KeepMinZ = 872f; s.KeepMaxZ = 1063f;" in m
         and "if (moved && s.Keep && !Kept(s, floor) && !Anchor(s, out floor))" in air,
         "armoury and HQ loot kept inside the HQ compound (only through the HQ fight)",
         "an H1/H2 point is not kept inside the HQ compound")
    need(any(s[0] == "MT-AB1m" and s[2] == "shells" and float(s[5]) >= 1 for s in slots)
         and any(s[0] == "MT-AB2m" and s[2] == "shells" and float(s[5]) >= 1 for s in slots)
         and 'p["shells"] = new string[] { "2066" };' in air
         and "public const int DEF_SHELL = 2066;" in read("RevivalMortar.cs"),
         "122 mm shells (item 2066) at both batteries' ammunition niches",
         "no guaranteed shells at AB1m and AB2m, or the shell id changed")
    need(any(s[0] == "MT-A3" and s[2] == "officer" for s in slots),
         "officers' flats (A3) have their own pool", "no officer loot in A3")
    need("if (tier >= 4) return MilitaryTown.ArmourySeconds();" in air
         and "if (MilitaryTown.LootOn) all.AddRange(MilitaryTown.LootSlots());" in air,
         "the airfield's loot engine runs the town's points", "Airfield does not load the town's points")

    # --- 3: defenders
    k = 2.8
    blocks = []
    for b in gb.get("blocks", []):
        if b.get("kind") in ("post", "panel"):
            continue
        w, d = b["w"] * k / 2.0, b["d"] * k / 2.0
        if abs(float(b.get("yaw", 0.0))) % 180 == 90:
            w, d = d, w
        blocks.append((b["id"], b["x"], b["z"], w, d))
    specs = re.findall(r'new Spec\("([A-Za-z0-9_.-]+)", "(\w+)", (\d+), ([\d.]+)f, "(\w+)", (\d),(.*?)\)(?=,\s*(?://[^\n]*\n\s*)?new Spec|\s*\};)',
                       code, re.S)
    need(len(specs) >= 15, "%d town groups" % len(specs), "fewer than 15 town groups parsed")
    good = True
    for s in specs:
        pts = [(float(x), float(z)) for x, z in re.findall(r'P\((-?[\d.]+)f, (-?[\d.]+)f\)', s[6])]
        if not pts or (s[1] == "patrol" and len(pts) < 2):
            good = False
            bad("Military town: %s has no point or a patrol without a route" % s[0])
        for x, z in pts:
            if not (5452 < x < 5848 and 602 < z < 1198):
                good = False
                bad("Military town: %s point (%g, %g) is outside the fence" % (s[0], x, z))
            for bid, bx, bz, hw, hd in blocks:
                if abs(x - bx) < hw and abs(z - bz) < hd:
                    good = False
                    bad("Military town: %s point (%g, %g) stands in %s" % (s[0], x, z, bid))
            if s[0] in ("mt-HQ-core", "mt-HQ-armoury", "mt-HQ-reserve") and not (5757 < x < 5846 and 870 < z < 1065):
                good = False
                bad("Military town: %s point (%g, %g) is outside the HQ compound" % (s[0], x, z))
    need(good, "every group point inside the fence and off the footprints",
         "a group point is outside the fence, in a building or (HQ core) outside the compound")
    names = dict((s[0], s) for s in specs)
    for must in ("mt-R2-street", "mt-R4-street"):
        need(must in names and names[must][1] == "patrol", "street patrol on foot: " + must,
             must + " is not a foot patrol")
    need(all(n in names and names[n][4] == "defender" for n in ("mt-HQ-gate", "mt-HQ-core", "mt-HQ-armoury")),
         "the HQ core is defenders (armour, MG)", "the HQ core groups are not class defender")
    need(any(int(s[5]) > 1 for s in specs) and "Level() < spec.MinPlayers" in code,
         "extra groups with more players near the town", "no group scales with the player count")
    posts = re.findall(r'(?:Spot|new Post)\("(mt-[A-Za-z0-9]+)", "(MT-[A-Za-z0-9]+)"', code)
    need(posts and all(b in ids for g, b in posts), "%d posts on recipe buildings" % len(posts),
         "a post names a building the recipe does not have")
    spot_groups = set(g for g, b in re.findall(r'Spot\("(mt-SP\d)", "(MT-[A-Za-z0-9]+)"', code))
    need(spot_groups == set(["mt-SP1", "mt-SP2", "mt-SP3"]),
         "spotters SP1 chimney, SP2 A1 roof, SP3 HQ roof", "not exactly the three spotter posts SP1-SP3")
    need(set(g for g, b in posts if g.startswith("mt-SN")) >= set(["mt-SN1", "mt-SN2", "mt-SN3", "mt-SN4"]),
         "sniper floors SN1-SN4", "a sniper floor SN1-SN4 has no post")
    counts = {}
    for g, b in posts:
        counts[g] = counts.get(g, 0) + 1
    need(all(g in names and counts[g] >= int(names[g][2]) for g in counts),
         "one post per posted man", "a posted group has more men than posts")
    need("Park(p.Man);" in code and "tr.position = p.At;" in code and "Crew.GroundKey(ai)" in code
         and "ViewId(a).CompareTo(ViewId(b))" in code,
         "posted men held on every client by spawn key, Photon view order",
         "the posted men are not held on their posts by key")

    # --- 4: batteries
    pits = re.findall(r'"(MT-AB\d[ab])"', code)
    need(sorted(set(pits)) == ["MT-AB1a", "MT-AB1b", "MT-AB2a", "MT-AB2b"] and all(p in ids for p in pits),
         "four guns in the recipe pits AB1a/b, AB2a/b", "the gun pits do not match the recipe")
    arty = read("RevivalArtyBattery.cs")
    need("if (p.Town ? p.Wrecked || !MilitaryTown.SpotterAlive() : !p.DroneUp) return;" in arty
         and arty.count("if (p.Town ? !MilitaryTown.Sees(") == 2
         and "if (p.Town) { p.DroneUp = false; return; }" in arty
         and "if (p.Wrecked) { p.Sighting = false; return; }" in arty,
         "a town gun fires only on what a living spotter sees; no drone; a wreck never fires",
         "ArtyBattery's town post spots without the spotter rule or still flies a drone")
    need("if (Inside(at, 10f)) return false;" in code and "Physics.Raycast(eye + dir * 1.2f, dir, out hit" in code
         and "InSector(p, bearing)" in code,
         "spotter rule: range, sector, clear ray, never into the town",
         "MilitaryTown.Sees lost its range/sector/ray test or fires into the town")
    need("internal static bool RaiseFixed(" in read("RevivalMortar.cs")
         and "ArtyBattery.TownGunRaised(id, go, spot, name, side);" in read("RevivalMortar.cs"),
         "guns through the existing stationary artillery (Mortar.RaiseFixed)",
         "Mortar.RaiseFixed is missing or does not hand the gun to ArtyBattery")
    need("NetworkVisualizeExplode" in code and "if (g.Hits >= need) Wreck(g);" in code,
         "explosions wreck a gun (GunHits)", "no way to destroy a town gun")

    # --- 5: routes
    try:
        sys.path.insert(0, os.path.join(ROOT, "research"))
        import mt_gameplay
        want = mt_gameplay.emit()
        have = read("Revival.MilitaryTownData.cs").replace("\r\n", "\n")
        need(want == have, "Revival.MilitaryTownData.cs is what research/mt_gameplay.py writes",
             "Revival.MilitaryTownData.cs differs from research/mt_gameplay.py - regenerate it")
        conv, pat, aa = mt_gameplay.routes()
        need(abs(aa[0][0] - 5482) < 1 and abs(aa[0][2] - 892) < 1,
             "AA site waypoint 0 on the AA1 platform", "the AA site does not start on AA1")
        need(conv[0][0] > 4820 + 100 and 5452 < conv[-1][0] < 5848 and 602 < conv[-1][2] < 1198,
             "reinforcement: from outside the airfield's margin into the town (%d points)" % len(conv),
             "the reinforcement route starts inside the airfield margin or ends outside the town")
        inside = sum(1 for p in pat if 5452 < p[0] < 5848 and 602 < p[2] < 1198)
        need(inside >= 20 and math.hypot(pat[0][0] - pat[-1][0], pat[0][2] - pat[-1][2]) < 1.0,
             "vehicle patrol: S3 and %d town street points, closed loop" % inside,
             "the vehicle patrol does not run through the town or does not close")
    except Exception as e:
        bad("Military town: research/mt_gameplay.py failed: %s" % e)
    need('vehicle=gepard,count=1,hold' in code and ',kind=convoy' in code,
         "AA site = held Gepard route; reinforcement = convoy route",
         "the AA site is not a held Gepard or the reinforcement is not a convoy route")
    patrol = read("Revival.Patrol.cs")
    need("if (r.Builtin) continue;" in patrol and "&& !r.Builtin)" in patrol
         and "if (r.Builtin && !MilitaryTown.RouteReady(r.Name)) continue;" in patrol
         and "string[] builtin = MilitaryTown.RouteLines();" in patrol,
         "built-in routes: loaded, never saved, not in the random convoy draw, driven once the town is up",
         "Patrol's built-in route handling is incomplete")
    gep = read("RevivalGepardCrew.cs")
    need("GepardAir.Collect(" in gep and "PlayerHeli.MissileTargets(" in gep,
         "the AA site's gunner engages the Mi-8 and registered aircraft (An-2) first",
         "RevivalGepardCrew lost its air-first search")

    # --- 6: budget and seams
    need("if (MilitaryTown.TroopTargets(zone, arrow)) return true;" in air
         and "if (MilitaryTown.ConvoyTargets(route)) return true;" in air
         and "if (Airfield.Full())" in code,
         "one event budget for the airfield and the town", "the town does not share the airfield's event budget")
    need("MilitaryTown.ConvoyKinds(routeName)" in read("RevivalConvoy.cs")
         and "RevivalConvoy.SpawnOn(ReinforceRoute)" in code,
         "reinforcement convoy sent by the town, column by player count",
         "the reinforcement convoy seam is missing")
    g = read("Revival.GroundEnemies.cs")
    need("MilitaryTown.AddGroups(fresh);" in g and "MilitaryTown.HoldSpawn(g)" in g
         and "MilitaryTown.GroupsVersion()" in g,
         "defenders run as ground groups", "the ground groups do not know the town")
    plug = read("RevivalPlugin.cs")
    need(all(("MilitaryTown.%s" % s) in plug for s in ("BindConfig(Config);", "Install(_harmony);", "Tick();", "LateFrame();")),
         "seams BindConfig/Install/Tick/LateFrame in RevivalPlugin.cs", "a MilitaryTown seam is missing in RevivalPlugin.cs")
    sync = read("sync_public.py")
    need('"Revival.MilitaryTown.cs"' in sync and '"Revival.MilitaryTownData.cs"' in sync,
         "both files go to the public repository", "sync_public.py lacks a military town file")
    doc = read(os.path.join("docs", "ai", "tasks", "military-town-gameplay.md"))
    need(doc and "arty stops after the spotters die" in doc.lower() and "gepard shoots a heli" in doc.lower()
         and "armoury is reachable only through the hq fight" in doc.lower(),
         "docs/ai/tasks/military-town-gameplay.md with the in-game checklist",
         "docs/ai/tasks/military-town-gameplay.md or its three checklist items are missing")


def check_military_town_assembly():
    """[35] Military town assembly (Tools/town_assembly.py, east_mt_* bundles).

    The MT models in place of the town greybox, one content bundle per
    building (docs/ai/tasks/military-town-assembly.md). Pinned statically:
      1. RECIPES: every Content/east_mt_<id>.json is written by
         town_assembly.py (it names the id), built by AirfieldAssembly.Populate
         into its own scene, its kit scenes come from the MT generators, and
         what it replaces exists in east_town.json - each id once.
      2. FALLBACK: AirfieldGreybox takes the town's prefix for east_town.json;
         Revival.EastWorld.cs switches the town's Fallback groups like the
         airfield's; build.ps1 installs and prunes east_mt_*.bundle.
      3. PIPELINE: rebuild_east.ps1 checks the recipes, runs the generators,
         TownAssembly.Check and the headless load check --town.
      4. The acceptance doc with its checklist.
    """
    import json
    import re
    print("[35] Military town assembly (east_mt_* bundles, greybox fallback)")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("Town assembly: " + why)

    content = os.path.join(ROOT, "unity", "EastTile", "Content")
    tool = read(os.path.join("unity", "EastTile", "Tools", "town_assembly.py"))
    if not tool:
        bad("Town assembly: unity/EastTile/Tools/town_assembly.py is missing")
        return
    try:
        gb = json.load(io.open(os.path.join(content, "east_town.json"), encoding="utf-8"))["greybox"]
    except Exception as e:
        bad("Town assembly: east_town.json unreadable: %s" % e)
        return
    blocks = set(b["id"] for b in gb.get("blocks", []))
    fences = set(f["id"] for f in gb.get("fences", []))
    files = sorted(f for f in os.listdir(content) if f.startswith("east_mt_") and f.endswith(".json"))
    need(len(files) >= 20, "%d east_mt_ recipes" % len(files), "fewer than 20 east_mt_ recipes in Content/")
    seen, problems = {}, []
    for f in files:
        bid = f[len("east_mt_"):-5]
        try:
            r = json.load(io.open(os.path.join(content, f), encoding="utf-8"))
        except Exception as e:
            problems.append("%s unreadable (%s)" % (f, e))
            continue
        if '("%s", ' % bid not in tool:
            problems.append("%s: town_assembly.py has no builder %s" % (f, bid))
        if r.get("builder") != "AirfieldAssembly.Populate" or not str(r.get("scene", "")).startswith("EastMt"):
            problems.append("%s: builder or scene" % f)
        if "town_assembly.py" not in r.get("about", ""):
            problems.append("%s: not written by town_assembly.py" % f)
        for b in r.get("buildings", []):
            if not re.match(r"Build/mt(_civic|_service|_core)?/mt_\w+_scene\.json$", b.get("scene", "")):
                problems.append("%s: scene %s from no MT generator" % (f, b.get("scene")))
        rep = r.get("replaces", {})
        for i in rep.get("blocks", []) + rep.get("fences", []):
            if i not in blocks and i not in fences:
                problems.append("%s replaces unknown %s" % (f, i))
            if i in seen:
                problems.append("%s replaced by %s and %s" % (i, seen[i], f))
            seen[i] = f
    need(not problems, "every recipe written by town_assembly.py, one scene each, MT kit scenes, replaced ids "
         "in east_town.json once (%d)" % len(seen), "; ".join(problems[:4]))

    gbx = read(os.path.join("unity", "EastTile", "Assets", "Editor", "AirfieldGreybox.cs"))
    asm = read(os.path.join("unity", "EastTile", "Assets", "Editor", "AirfieldAssembly.cs"))
    need("AirfieldAssembly.ReplacedIds(prefix, rb, rs, rf);" in gbx and 'public const string TownPrefix = "east_mt_";' in asm
         and '== "east_town" ? TownPrefix : Prefix;' in asm,
         "east_town.json keeps the east_mt_ pieces as its switched-off fallback",
         "AirfieldGreybox/AirfieldAssembly do not give east_town.json the east_mt_ prefix")
    ew = read("Revival.EastWorld.cs")
    need('TownScene = "EastTown", TownRoot = "EastTownRoot"' in ew and 'ApplyFallback(s, TownRoot, "town");' in ew
         and 'ApplyFallback(s, AirfieldRoot, "airfield");' in ew,
         "Revival.EastWorld.cs switches the town's fallback groups like the airfield's",
         "Revival.EastWorld.cs does not apply the town's fallback")
    bp = read("build.ps1")
    need('@("east_mt_*.bundle", "the military town")' in bp, "build.ps1 installs and prunes east_mt_*.bundle",
         "build.ps1 does not install east_mt_*.bundle")
    rb = read("rebuild_east.ps1")
    need('town_assembly.py", "--check"' in rb and '"TownAssembly.Check"' in rb and 'airfield_load_check.py", "--town"' in rb
         and '"mt_blocks.py"' in rb and '"mt_core.py"' in rb,
         "rebuild_east.ps1 checks the recipes, builds the MT pieces, runs TownAssembly.Check and the load check",
         "rebuild_east.ps1 lacks a town assembly step")
    doc = read(os.path.join("docs", "ai", "tasks", "military-town-assembly.md")).lower()
    need(doc and "in-game checklist (yes / no)" in doc and "parts from their own bundles" in doc
         and "merged into this branch" in doc,
         "docs/ai/tasks/military-town-assembly.md with the in-game checklist",
         "docs/ai/tasks/military-town-assembly.md or its checklist is missing")


def check_east_world():
    """[29] The east world ([World] EastTile, Revival.EastWorld.cs).

    GW_Scene_1 plus an additively loaded east tile as one world - and, with
    the switch off, the vanilla map byte for byte. That second promise rests
    on structure this check pins: the switch defaults to false and is read
    once; Install returns before the first Harmony patch when it is off; every
    call site that knows the east world keeps its old expression on the off
    branch. The arithmetic itself (off = the old bits, on = one rectangle
    -2500..7500 x -2500..2500) is executed by research/east_world_check.py;
    verify.py starts no subprocesses, so that proof only has to be present.
    Also pinned: the tile is never loaded synchronously (feasibility 4.4: a
    sync additive load force-activates SceneStreamer's held chunks), and every
    game method the patches name exists in Assembly-CSharp.
    """
    print("[29] East world ([World] EastTile)")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("East world: " + why)

    src = read("Revival.EastWorld.cs")
    if not src:
        bad("East world: Revival.EastWorld.cs fehlt")
        return
    code = _code(src)
    # 6.57.0: on by default; a config from before (settings layout < 2) is
    # switched on once, a later "false" is the player's and stays.
    need('cfg.Bind("World", "EastTile", true,' in code,
         "[World] EastTile defaults to true (6.57.0)",
         "[World] EastTile is not bound with the default true")
    bc = _body(code, "internal static void BindConfig(")
    sset = _code(read("Revival.Settings.cs"))
    need("Settings.FileLayout(cfg) < Settings.EastTileOnLayout" in bc and "_cfg.Value = true;" in bc
         and bc.index("_cfg.Value = true;") < bc.index("On = _cfg.Value;")
         and "internal const int EastTileOnLayout = 2;" in sset
         and re.search(r"const int Layout = ([0-9]+);", sset) is not None
         and int(re.search(r"const int Layout = ([0-9]+);", sset).group(1)) >= 2
         and "if (_fileLayout < 0) _fileLayout = _version.Value;" in sset,
         "an older config's EastTile = false is switched on once, by the settings layout stamp",
         "the one-time EastTile migration (Settings.FileLayout < EastTileOnLayout) is missing")
    need(len(re.findall(r"\bOn = ", code)) == 1
         and "On = _cfg.Value;" in _body(code, "internal static void BindConfig("),
         "the switch is read once, in BindConfig",
         "EastWorld.On is assigned somewhere else than BindConfig")
    need(re.match(r"\{\s*if \(!On\) return;",
                  _body(code, "internal static void Install(Harmony h)")) is not None,
         "Install returns before the first patch when the switch is off",
         "Install can patch the game with [World] EastTile off")
    need("SceneManager.LoadScene(" not in code,
         "the tile is never loaded synchronously (4.4)",
         "a synchronous SceneManager.LoadScene would flush SceneStreamer's held chunks")
    tick = _body(code, "internal static void Tick()")
    guard = _body(code, "static void Guard()")
    need("Guard();" in tick and "if (!TileLoaded())" in guard and "SeamPoint(p, out seam)" in guard
         and "surface - 1f" in guard and "MapTools.TeleportLocal(up, out msg)" in guard,
         "Guard: nobody east of the edge without the tile, nobody left under the tile surface",
         "Guard() is missing or no longer holds/lifts - the 2026-09-23 relog fell under the tile and died")
    need("LoadSceneAsync(SceneName, LoadSceneMode.Additive)" in code,
         "the tile loads async and additive",
         "the tile load is not LoadSceneAsync(..., Additive)")
    # The real tile (unity/EastTile BuildTile.cs): its scene and bundle, and
    # the one terrain lookup every height/surface answer goes through skips
    # the tile's tree-only terrains (flat stand-in heightmap, no splat).
    need('internal const string SceneName = "EastTile";' in code
         and 'const string BundleFile = "east_tile.bundle";' in code
         and os.path.isfile(os.path.join(ROOT, "assets", "east_tile.bundle"))
         and '"east_tile.bundle"' in read("build.ps1"),
         "the east tile: scene EastTile from assets/east_tile.bundle, installed by build.ps1",
         "SceneName/BundleFile are not the east tile's, or assets/east_tile.bundle is missing or not installed")
    need("|| !t.drawHeightmap) continue;" in _body(code, "static Terrain OtherTerrainAt("),
         "terrain lookups take the tile terrain that draws the heightmap",
         "OtherTerrainAt can answer from a tree-only tile terrain (flat heights, no splat)")
    frac = _body(code, "internal static Vector2 Fraction(")
    need("if (!Extends) return new Vector2(pos.x / worldSize.x + 0.5f, pos.z / worldSize.y + 0.5f);" in frac,
         "Fraction keeps the old grid formula when the east world is not up",
         "Fraction's off branch is not the old pos / W + 0.5")

    plugin = _code(read("RevivalPlugin.cs"))
    for seam in ("EastWorld.BindConfig(Config);", "EastWorld.Install(_harmony);", "EastWorld.Tick();"):
        need(seam in plugin, "Seam " + seam + " in RevivalPlugin.cs", seam + " missing in RevivalPlugin.cs")

    ground = _code(read("Revival.GroundEnemies.cs"))
    need("EastWorld.On ? !EastWorld.OnTerrain(point)" in ground
         and "(point.x < -2500f || point.x > 2500f || point.z < -2500f || point.z > 2500f)" in ground
         and "result.x >= -2500f && result.x <= 2500f && result.z >= -2500f && result.z <= 2500f" in ground,
         "TryGround: terrain bounds when on, the old +-2500 when off",
         "TryGround lost either its east-world bound or its old +-2500")
    troops = _body(_code(read("RevivalTroopInsertion.cs")), "internal static bool TerrainHeight(")
    need("if (EastWorld.On) return EastWorld.TerrainHeight(xz, out y);" in troops
         and "_activeTerrain.Invoke(null, null)" in troops,
         "TerrainHeight: the terrain containing the point when on, activeTerrain when off",
         "RevivalTroopInsertion.TerrainHeight is not split on EastWorld.On")
    admin = _code(read("Revival.Admin.cs"))
    project = _code(read("Revival.MapProject.cs"))
    local = _body(project, "internal static Vector3 TextureLocal(")
    need("return MapProject.ToGui(point, texture, cam, world, map, out gui);" in admin
         and "(nx - 0.5f) * world.x + centre.x" in admin
         and "if (!EastWorld.Extends)" in local
         and "return new Vector3(point.x / world.x * map.x, point.z / world.y * map.y, 0f);" in local
         and "Rect w = EastWorld.Extended;" in local
         and "fx * map.x / s.x" in local and "fz * map.y / s.y" in local,
         "MapTools projects through MapProject (east rectangle, texture's own units) and clicks about EastWorld.MapCentre",
         "MapTools still assumes the origin-centred map, or projects in MAP_SIZE units onto the scaled texture")
    # The vanilla picture's 0.5 % fit lives in ONE place and is not applied to
    # the exactly registered east artwork (docs/ai/tasks/east-map-markers.md).
    fit = _body(project, "internal static Vector2 OnPicture(")
    copies = [f for f in ("Revival.Patrol.cs", "RevivalNewSettlement.cs", "Revival.Helipads.cs")
              if "1.005f" in _code(read(f))]
    need("if (EastWorld.Extends || full.width < 1f) return g;" in fit and not copies
         and "MapProject.OnPicture(" in _code(read("Revival.Patrol.cs"))
         and "MapProject.OnPicture(" in _code(read("RevivalNewSettlement.cs"))
         and "MapProject.OnPicture(" in _code(read("Revival.Helipads.cs")),
         "the vanilla picture fit is MapProject.OnPicture only (Patrol, settlement ring, helipads), off in the east world",
         "a map-picture fit outside MapProject.OnPicture, or applied to the east artwork: " + ", ".join(copies))
    need("(nx - 0.5f) * world.x + centre.x" in _code(read("RevivalMortar.cs")),
         "the mortar's map click uses EastWorld.MapCentre",
         "RevivalMortar.MapPoint still assumes the origin-centred map")
    for name in ("RevivalConvoy.cs", "RevivalTroopInsertion.cs"):
        need("EastWorld.Fraction(pos, w)" in _code(read(name)),
             name + " grid square through EastWorld.Fraction",
             name + " grid square still assumes the origin-centred map")
    need('"Revival.EastWorld.cs"' in read("sync_public.py"),
         "Revival.EastWorld.cs goes into the public repository",
         "sync_public.py does not carry Revival.EastWorld.cs - the public build breaks")
    need(os.path.exists(os.path.join(ROOT, "research", "east_world_check.py")),
         "research/east_world_check.py liegt vor",
         "research/east_world_check.py fehlt - off/on arithmetic unproven")

    ink = _code(read("Revival.MapInk.cs"))
    need("if (!EastWorld.Extends)" in _body(ink, "internal static Vector2 Artwork(")
         and "EastArtWidth" in _body(ink, "internal static Vector2 Artwork(")
         and "Assets.Texture(file, false, true)" in ink,
         "MapInk keeps vanilla registration off and installs the wider artwork on",
         "MapInk has no gated east-art registration")
    need(all(os.path.isfile(os.path.join(ROOT, "assets", f))
             for f in ("east_map_en.png", "east_map_ru.png"))
         and all('"%s"' % f in read("build.ps1")
                 for f in ("east_map_en.png", "east_map_ru.png")),
         "the RU/EN east artwork exists and build.ps1 installs it",
         "east_map_en.png/east_map_ru.png is missing or not installed")
    need(os.path.exists(os.path.join(ROOT, "assets", "editor", "basemap_east.png"))
         and os.path.exists(os.path.join(ROOT, "research", "east_map.py"))
         and os.path.exists(os.path.join(ROOT, "research", "east_map_check.py")),
         "editor artwork, deterministic generator and acceptance check exist",
         "east editor artwork or its generator/check is missing")
    # East map regenerated from the tile data (docs/ai/tasks/east-map-regen.md):
    # the generator reads the build data, and the projection is proven on the
    # known places both ways (marker paths and painted pixels).
    gen = read(os.path.join("research", "east_map.py"))
    pc = read(os.path.join("research", "east_map_panel_check.py"))
    need(os.path.exists(os.path.join(ROOT, "research", "east_map_art_check.py"))
         and all(k in gen for k in ("heights.bin", "trees.bin", "water_polys", "east_airfield.json", '"runway"'))
         and all(k in pc for k in ("(4632.0, -1602.0)", "(4632.0, 1483.0)", "(2262.0, 1846.0)", "saddle S2")),
         "east map drawn from heights/trees/water/airfield; runway ends, saddles, Conductor projected",
         "east map generator not data-driven or the projection points are missing")
    # Airfield and military town POIs (docs/ai/tasks/airfield-map-pois.md):
    # one generated list (research/east_pois.py) baked into the artwork with
    # the fence and the runway, and served read-only to the editor - never
    # through /api/save, so live authoring data is not touched.
    import json
    pois_path = os.path.join(ROOT, "assets", "editor", "pois_east.json")
    pois_ok = False
    try:
        pois = json.load(io.open(pois_path, encoding="utf-8"))
        kinds = set(p.get("kind") for p in pois.get("pois", []))
        pois_ok = (pois.get("scene") == "GW_Scene_1" and pois.get("fence") and pois.get("runway")
                   and {"airfield", "hangar", "tower", "fire", "fuel", "ammo", "town"} <= kinds)
    except (OSError, ValueError):
        pass
    need(pois_ok and os.path.exists(os.path.join(ROOT, "research", "east_pois.py")),
         "pois_east.json lists the airfield, its buildings and the military town, with fence and runway",
         "assets/editor/pois_east.json or research/east_pois.py is missing or incomplete")
    need(all(k in gen for k in ("pois_east.json", "def draw_runway(", "def draw_icons(", "east_pois.fence(")),
         "the east map bakes the POI icons and labels, the fence line and the runway markings",
         "research/east_map.py does not draw the POIs, the fence or the runway markings")
    route_src = read("routeeditor.py")
    pjs = read(os.path.join("editor", "pois.js")) if os.path.exists(os.path.join(ROOT, "editor", "pois.js")) else ""
    need('path == "/api/pois"' in route_src and 'path == "/pois.js"' in route_src
         and '<script src="pois.js"></script>' in read(os.path.join("editor", "index.html"))
         and "NDRPois.draw();" in read(os.path.join("editor", "app.js"))
         and bool(pjs) and "S.def" not in pjs.split("*/", 1)[1] and "/api/save" not in pjs.split("*/", 1)[1],
         "the editor serves and draws the POIs read-only (no S.def, no /api/save)",
         "the editor POI layer is not wired, or it writes into the saved definition")
    # The map window (Revival.EastMapPanel.cs, docs/ai/tasks/east-map-panel.md):
    # installed only from EastWorld.Install (after its !On return), every hook
    # gated on EastWorld.Extends, its assets built and installed.
    panel = _code(read("Revival.EastMapPanel.cs"))
    install = _body(code, "internal static void Install(")
    need(bool(panel) and "EastMapPanel.Install(h);" in install
         and install.index("if (!On) return;") < install.index("EastMapPanel.Install(h);"),
         "the map window is installed only with [World] EastTile on",
         "EastMapPanel.Install is missing or not behind EastWorld.Install's !On return")
    need(all("EastWorld.Extends" in _body(panel, sig) for sig in (
             "static void AfterInit(", "static void AfterEnable(", "static void AfterUpdate(",
             "static bool CenterOnPrefix(", "internal static void ApplyPreset(")),
         "every map window hook returns on !EastWorld.Extends (vanilla window otherwise)",
         "an EastMapPanel hook can act outside the east world")
    panel_assets = ("east_map_form.png", "east_map_legend.png", "east_map_grid_en.png", "east_map_grid_ru.png")
    need(all(os.path.isfile(os.path.join(ROOT, "assets", f)) and '"%s"' % f in read("build.ps1")
             for f in panel_assets)
         and os.path.exists(os.path.join(ROOT, "research", "east_map_panel.py"))
         and os.path.exists(os.path.join(ROOT, "research", "east_map_panel_check.py"))
         and '"Revival.EastMapPanel.cs"' in read("sync_public.py"),
         "map window frame/legend/grid assets built, installed and published with their source",
         "a map window asset, its generator/check, or the public source entry is missing")
    need(all("if (EastWorld.Extends) return EastMapPanel.GridSquare(pos);" in _body(_code(read(f)), "static string GridCell(")
             for f in ("RevivalConvoy.cs", "RevivalTroopInsertion.cs")),
         "banner grid squares name the drawn 20 x 10 grid in the east world",
         "a GridCell still names a plain 10 x 10 split of the 10 km world")
    crossings = _code(read("Revival.EastCrossings.cs"))
    need("EastCrossings.BeforeLocationMarker(__instance);" in code
         and "Place(trigger.transform, to" in _body(crossings, "internal static void BeforeLocationMarker("),
         "the Conductor moves before LocationChangeTrigger registers its marker",
         "the Conductor's map marker can still be registered at the old position")

    targets = re.findall(r'Patch\(h, "(\w+)", "(\w+)"', code)
    need(len(targets) == 11, "%d game methods patched" % len(targets),
         "expected 11 patch targets, found %d" % len(targets))
    game = os.path.join(GAME, "nextday_game_Data", "Managed", "Assembly-CSharp.dll") if GAME else ""
    if ildasm is None or not game or not os.path.exists(game):
        warn("East world: Assembly-CSharp.dll or ildasm.py missing - patch targets not checked")
        return
    methods = ildasm.Asm(game).methods
    for cls, meth in targets:
        need(cls + "::" + meth in methods, "%s.%s exists in the game" % (cls, meth),
             "%s.%s is patched but does not exist in Assembly-CSharp" % (cls, meth))
    for cls, meth in (("MapUIManager", "InitMapData"), ("MapUIManager", "OnEnable"),
                      ("MapUIManager", "Update"), ("UICenterOnChild", "CenterOn")):
        need(cls + "::" + meth in methods, "map window target %s.%s exists in the game" % (cls, meth),
             "map window patches %s.%s, which Assembly-CSharp does not have" % (cls, meth))


def check_east_roads():
    """[31] The east tile's roads (task I, docs/ai/tasks/east-roads.md).

    research/east_roads.py designs and carves them; the tile is built from
    its carved heights and paints them in a road layer of its own; the bundle
    meshes them with the game's road materials and carries the game's
    Bridge_1 and rail40; the pieces on GW_Scene_1's cut floors ship inactive
    and EastCrossings switches them on with the cuts; the served road network
    has the tile's sections APPENDED - every GW_Scene_1 id as it was. Pinned
    too: BuildTile paints the ground after CreateAsset, which resets a
    TerrainData's alphamaps (until task I the tile bundle shipped with no
    ground paint at all).
    """
    import json
    print("[31] East roads (task I)")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("East roads: " + why)

    need(os.path.isfile(os.path.join(ROOT, "research", "east_roads.py")),
         "research/east_roads.py designs, routes and carves the roads",
         "research/east_roads.py fehlt")
    # tile_data.py composes the per-concern files (docs/ai/tasks/east-pipeline.md)
    td = "".join(read(os.path.join("unity", "EastTile", "Tools", f))
                 for f in ("tile_data.py", "tile_common.py", "tile_splat.py"))
    need('"east_tile_roads.raw"' in td and "ROAD_LAYER = len(SPLATS) - 1" in td
         and "the road carve touched the seam column" in td,
         "tile_data.py builds the tile from the carved heights (seam column held) and paints the road layer last",
         "tile_data.py does not build from east_tile_roads.raw, or has no road layer")
    bt = _code(read(os.path.join("unity", "EastTile", "Assets", "Editor", "BuildTile.cs")))
    swap = _body(bt, "static void Swap(")
    gd = _body(bt, "static TerrainData GroundData(")
    need("ground.SetAlphamaps(0, 0, before);" in swap and "ground.splatPrototypes = sp;" in swap
         and swap.find("ground.splatPrototypes = sp;") < swap.find("ground.SetAlphamaps(0, 0, before);")
         and 0 <= gd.find("AssetDatabase.CreateAsset(td,") < gd.find("td.SetAlphamaps("),
         "BuildTile paints the ground AFTER CreateAsset (which resets alphamaps) and the GAME swap keeps it",
         "BuildTile paints before CreateAsset or swaps without restoring the alphamap - the bundle ships unpainted")
    need("static void Roads(" in bt and "static void MakeBridge(" in bt and "static void Rail(" in bt
         and "cutRoads.SetActive(false);" in bt and "RoadNav();" in bt and "BridgeNav();" in bt,
         "BuildTile meshes the roads, builds the bridge, lays the rail, ships the cut pieces inactive, "
         "checks the NavMesh along the roads and on the deck",
         "BuildTile lacks the roads, the bridge, the rail, the inactive cut group or the NavMesh checks")
    cr = _code(read("Revival.EastCrossings.cs"))
    seam = _body(cr, "static void TickSeam(")
    need('Find("EastTileCutRoads")' in seam and "SetActive(true)" in seam,
         "EastCrossings switches the tile's cut-floor roads on together with the cuts",
         "EastCrossings.TickSeam does not switch EastTileCutRoads on")
    rn = read("roadnet.py")
    need("def east_extend(" in rn and "EAST_ROAD_LAYER = 12" in rn and '"-extend" in sys.argv' in rn,
         "roadnet.py -east -extend skeletonises the tile's road layer",
         "roadnet.py has no -east -extend")
    sample = read(os.path.join("assets", "editor", "roadnet_sample.json"))
    try:
        net = json.loads(sample)
    except ValueError:
        net = {"edges": [], "nodes": []}
    east = [e for e in net.get("edges", []) if e.get("east")]
    van = [e for e in net.get("edges", []) if not e.get("east")]
    vnodes = [n for n in net.get("nodes", []) if not n.get("east")]
    need(east and [e["id"] for e in van] == list(range(len(van)))
         and min(e["id"] for e in east) == len(van)
         and [n["id"] for n in vnodes] == list(range(len(vnodes)))
         and all(n["id"] >= len(vnodes) for n in net["nodes"] if n.get("east")),
         "served network: %d east sections appended after GW_Scene_1's %d, every old id in place" % (len(east), len(van)),
         "assets/editor/roadnet_sample.json has no east sections, or they renumber GW_Scene_1's")
    by = {e["id"]: e for e in van}
    joined = True
    for vid, road in ((21, "s3"), (50, "s2")):
        v = by.get(vid)
        ends = [v["pts"][0][:2], v["pts"][-1][:2]] if v else []
        joined &= any(e.get("eastRoad") == road and e["pts"][0][:2] in ends for e in east)
    need(joined and all(len(e.get("displayPts") or []) == len(e["pts"]) for e in east),
         "S3 continues section 21 and S2 section 50 from their end points; every east section has display points",
         "an east saddle road does not start on its main-map section's end, or lacks displayPts")
    design = os.path.join(ROOT, "research", "out", "east-tile", "east_roads.json")
    if os.path.exists(design):
        checks = json.load(open(design)).get("checks", [])
        need(checks and not any(c.startswith("FAIL") for c in checks),
             "east_roads.py design checks: %d, none FAIL (grades < 12 %%, seam column, reserved areas)" % len(checks),
             "east_roads.py design checks FAIL - python research/east_roads.py")
    else:
        warn("East roads: research/out/east-tile/east_roads.json fehlt - Design nicht geprueft "
             "(python research/east_roads.py)")


def check_east_pipeline():
    """[32] The east tile as a recipe (docs/ai/tasks/east-pipeline.md).

    The height passes each in their own file, composed in one fixed order
    by research/east_tile.py from the shared research/east_layout.json; the
    tile data per concern, composed by tile_data.py; placed content in its
    own additive scene bundles, loaded with the tile; rebuild_east.ps1
    builds every east bundle from a checkout and installs them on main only.
    """
    import json
    print("[32] East pipeline (recipe, passes, content bundles)")

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("East pipeline: " + why)

    try:
        lay = json.loads(read(os.path.join("research", "east_layout.json")) or "{}")
    except ValueError:
        lay = {}
    need(all(k in lay for k in ("airfield", "runway", "town", "apron", "river", "roadBed", "saddles", "vegetation")),
         "research/east_layout.json holds the shared layout, one key per concern",
         "research/east_layout.json missing or incomplete")
    et = read(os.path.join("research", "east_tile.py"))
    passes = ("east_pass_base", "east_pass_relief", "east_pass_river", "east_pass_flatten", "east_pass_seam")
    need("for p in (%s):" % ", ".join(passes) in et and "LAYOUT_IN" in et,
         "east_tile.py composes the height passes base -> relief -> river -> flatten -> seam",
         "east_tile.py does not compose the five height passes in their fixed order")
    need(all("def apply(ctx):" in read(os.path.join("research", p + ".py")) for p in passes),
         "each height pass is its own file with apply(ctx)",
         "a research/east_pass_*.py file is missing or has no apply(ctx)")
    td = read(os.path.join("unity", "EastTile", "Tools", "tile_data.py"))
    need("PASSES = (tile_vegetation.density, tile_splat.paint, tile_vegetation.place, tile_grass.grass," in td
         and "tile_rocks.rocks, tile_seam.seam, tile_water.water)" in td,
         "tile_data.py composes vegetation, splat, grass, rocks, seam and water in their fixed order",
         "tile_data.py does not compose the per-concern files")
    content = ("east_airfield", "east_town", "east_bunker", "east_content_test")
    ew = read("Revival.EastWorld.cs")
    bp = read("build.ps1")
    need(all(os.path.isfile(os.path.join(ROOT, "unity", "EastTile", "Content", c + ".json")) for c in content)
         and "public static void Build()" in read(os.path.join("unity", "EastTile", "Assets", "Editor", "BuildContent.cs")),
         "BuildContent.cs builds one additive scene bundle per recipe in unity/EastTile/Content",
         "BuildContent.cs or a content recipe is missing")
    need(all('"%s.bundle"' % c in ew and '"%s.bundle"' % c in bp for c in content)
         and "QueueContent();" in ew and "UnloadContent();" in ew,
         "EastWorld loads the content bundles with the tile and unloads them with it; build.ps1 installs them",
         "the content loading hook or its install is missing")
    rb = read("rebuild_east.ps1")
    need(all(s in rb for s in ("BuildTile.Build", "BuildContent.Build", "tile_check.py", "east_seam.py",
                               "game_refs.py", '$branch -ne "main"')),
         "rebuild_east.ps1 builds, checks and renders every east bundle; installs on main only",
         "rebuild_east.ps1 is missing a step or the main-only install")
    need("rebuild_east.ps1" in read("AGENTS.md")
         and os.path.isfile(os.path.join(ROOT, "docs", "ai", "tasks", "east-pipeline.md")),
         "AGENTS.md carries the rule; docs/ai/tasks/east-pipeline.md the pass order, ownership and joint checks",
         "the AGENTS.md rule or docs/ai/tasks/east-pipeline.md is missing")


def check_east_crossings():
    """[30] The east crossings ([World] EastCrossings, Revival.EastCrossings.cs).

    The three saddle cuts into GW_Scene_1's east berm, in memory, only inside
    the east world. Pinned here: ONE switch, on by default but acting only with
    [World] EastTile (off by default), read once; Tick and BeforeSpawn return
    first when it is off; the heights go in from EastWorld's SpawnPlayer prefix,
    before anybody is put on the ground. The vanilla NavMesh is never updated
    in place - UpdateNavMeshData drops every tile outside its bounds
    (unity/EastCrossingsTest measured it), so only our own NavMeshData is ever
    built. The tile's temporary seam walls are switched off only by this class.
    The generated data is self-consistent, carries Kevin's decisions (both
    tunnels gone, the S1 track and train kept, the Conductor placed beside the
    road with his spawn points left where they are) and, when the design's
    cut file is present, equals it sample for sample. The engine behaviour is
    proved by python research/east_crossings_unity.py (Unity 2018.1.0f2, play
    mode); verify.py starts no subprocesses, so that proof only has to be present.
    """
    print("[30] East crossings ([World] EastCrossings)")
    import base64
    import json
    import struct

    def read(name):
        path = os.path.join(ROOT, name)
        return io.open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def need(cond, good, why):
        if cond:
            ok(good)
        else:
            bad("East crossings: " + why)

    src = read("Revival.EastCrossings.cs")
    core = read("Revival.EastCrossingsCore.cs")
    data = read("Revival.EastCrossingsData.cs")
    if not (src and core and data):
        bad("East crossings: Revival.EastCrossings.cs / Core.cs / Data.cs fehlt")
        return
    code, ccode = _code(src), _code(core)
    need('cfg.Bind("World", "EastCrossings", true,' in code and "EastCrossingTrial" not in code,
         "one switch, [World] EastCrossings (default true, acting only inside the east world)",
         "[World] EastCrossings is not the one switch bound with the default true")
    need(len(re.findall(r"\bOn = ", code)) == 1
         and "On = _cfg.Value && EastWorld.On;" in _body(code, "internal static void BindConfig("),
         "the switch is read once, and only acts together with [World] EastTile",
         "EastCrossings.On is assigned elsewhere, or not tied to EastWorld.On")
    need(re.match(r"\{\s*if \(!On\) return;", _body(code, "internal static void Tick()")) is not None
         and re.match(r"\{\s*if \(!On\) return;", _body(code, "internal static void BeforeSpawn()")) is not None,
         "Tick and BeforeSpawn return first when the switch is off",
         "EastCrossings.Tick or BeforeSpawn does work with the switch off")
    world = _code(read("Revival.EastWorld.cs"))
    need(re.match(r"\{\s*__state = null;\s*EastCrossings\.BeforeSpawn\(\);",
                  _body(world, "static void SpawnPrefix(")) is not None,
         "the cut heights go in from EastWorld.SpawnPrefix, before SpawnPlayer places anybody",
         "EastWorld.SpawnPrefix does not call EastCrossings.BeforeSpawn() first - a player saved on a cut "
         "would spawn inside the old berm")
    plugin = _code(read("RevivalPlugin.cs"))
    bw, bc = plugin.find("EastWorld.BindConfig(Config);"), plugin.find("EastCrossings.BindConfig(Config);")
    need(0 <= bw < bc, "EastCrossings.BindConfig runs after EastWorld.BindConfig (it reads EastWorld.On)",
         "EastCrossings.BindConfig missing or before EastWorld.BindConfig")
    need("EastCrossings.Tick();" in plugin, "Seam EastCrossings.Tick(); in RevivalPlugin.cs",
         "EastCrossings.Tick(); missing in RevivalPlugin.cs")
    # the vanilla NavMesh is never touched in place
    upd = re.findall(r"UpdateNavMeshData(?:Async)?\((\w+),", code + ccode)
    need(upd and set(upd) <= {"d"},
         "UpdateNavMeshData only on our own data (%s)" % ", ".join(sorted(set(upd))),
         "UpdateNavMeshData on %s - an in-place update drops every vanilla tile outside its bounds"
         % ", ".join(sorted(set(upd))))
    need("FindObjectsOfTypeAll(typeof(NavMeshData))" not in code + ccode,
         "the vanilla NavMeshData is not looked up",
         "the vanilla NavMeshData is looked up - nothing may rebuild it")
    need("new NavMeshData(0)" in ccode and "PatchArea = 3" in ccode,
         "the patch is its own NavMeshData on area 3",
         "the patch is not its own NavMeshData on area 3")
    need("DropNav(" in _body(code, "static void Hook()"),
         "the patches and links are removed with GW_Scene_1 (added NavMesh data outlives scenes)",
         "nothing removes the patches when GW_Scene_1 goes - they would stay on the next map")
    need("&& hang.y > pb.y + 0.3f) continue;" in _body(ccode, "internal static List<NavMeshLinkInstance> BandLinks("),
         "a band link never ends under a hanging remnant of the vanilla surface",
         "BandLinks can end under a hanging vanilla remnant - agents walk up onto a floating floor")
    wall = [name for name in os.listdir(ROOT) if name.endswith(".cs") and "EastTileSeamWall" in read(name)]
    need(wall == ["Revival.EastCrossings.cs"] and 'Find("EastTileSeamWall")' in _body(code, "static void TickSeam()"),
         "only the crossings take the tile's seam walls down (switch off = walls up)",
         "EastTileSeamWall is handled in %s, not only in EastCrossings.TickSeam" % wall)
    # B7a (6.60.0): a seam wall left standing is an invisible wall from the
    # vanilla side - it must not wait for the pipeline, and it goes for good
    tick = _body(code, "internal static void Tick()")
    seam_body = _body(code, "static void TickSeam()")
    need("TickSeam();" in tick and "if (_doneLoad != _load || _refused) return;" in tick
         and tick.index("TickSeam();") < tick.index("if (_doneLoad != _load || _refused) return;")
         and "SceneManager.sceneCount" in seam_body and "EdgeRise(" in seam_body
         and "UnityEngine.Object.Destroy(cols[c]);" in seam_body,
         "the seam walls go by the live heights on every tile scene, before the crossings pipeline, colliders destroyed",
         "EastCrossings.TickSeam waits for the pipeline, handles one tile scene only or only switches the wall off")
    # B7b (6.60.0): S2's approach lowers the floor under the tile bundle's cut
    # road piece - it is laid onto the live floor before it comes on
    need("ReseatCutRoads(st.CutRoads)" in seam_body
         and seam_body.index("ReseatCutRoads(st.CutRoads)") < seam_body.index("st.CutRoads.gameObject.SetActive(true);")
         and "mc.sharedMesh = m;" in _body(code, "static string ReseatCutRoads(Transform cutRoads)"),
         "the cut road pieces are laid onto the live cut floor (collider re-cooked) before they come on",
         "EastCrossings.TickSeam switches the cut road pieces on without ReseatCutRoads - S2's piece floats")
    # B7b: level7's invisible map bound (ServerObjects/Colliders/Collider) has
    # its east wall at x 2394.4 across all three cut roads, solid from the
    # vanilla side only - the one-way wall of the B7a report
    bound = _body(code, "static string OpenEastBound(GameObject[] roots)")
    need("OpenEastBound(" in _body(code, "static void Props(Scene scene)")
         and 'BoundPath = "ServerObjects/Colliders/Collider"' in code and "mc.sharedMesh = open;" in bound
         and "keep.Add(" in bound and "BoundEastX" in bound,
         "level7's map bound loses its east wall (x 2394.4, one-way) with the cuts in; the other walls stay",
         "EastCrossings does not open level7's east map bound - an invisible wall at x 2394 on every crossing road")
    # the generated data
    need("GENERATED by research/east_crossings.py -emit" in data,
         "Revival.EastCrossingsData.cs is generated", "Revival.EastCrossingsData.cs is not the generator's")
    blocks = data.split("new CrossingSaddle {")[1:]
    keys = [re.search(r'Key = "(\w+)"', b).group(1) for b in blocks]
    need(keys == ["S1", "S2", "S3"], "the data carries S1, S2 and S3", "the data carries %s" % keys)

    def num(b, name):
        m = re.search(r"\b" + name + r" = (-?[\d.]+)f?[,;]", b)
        return float(m.group(1)) if m else None

    def blob(b, name):
        m = re.search(r"\b" + name + r" =\s*((?:\"[^\"]*\"\s*[+,]\s*)+)", b)
        return base64.b64decode("".join(re.findall(r'"([^"]*)"', m.group(1)))) if m else b""

    cut = os.path.join(ROOT, "research", "out", "east-tile", "main_map_saddle_cuts.json")
    design = None
    if os.path.exists(cut):
        meta = json.load(io.open(cut, encoding="utf-8"))
        raw = open(cut[:-5] + ".raw", "rb").read()
        h, w = meta["shape"]
        design = (meta["origin"]["row"], meta["origin"]["col"], h, w, struct.unpack("<%dh" % (h * w), raw))
    else:
        warn("East crossings: research/out/east-tile fehlt - Schnitt nicht gegen das Design verglichen "
             "(python research/east_tile.py)")
    layers = re.search(r"PaintLayers = \{ ([\d, ]+) \}", data)
    need(layers is not None and [int(v) for v in layers.group(1).split(",")] == [6, 7, 14, 13],
         "paint layers Ter4, Ter6, asphalt_tint, dirt_tint_2 (GWTerrain2 6, 7, 14, 13)",
         "PaintLayers is not GWTerrain2's Ter4/Ter6/asphalt_tint/dirt_tint_2")
    for key, b in zip(keys, blocks):
        col0, row0, cols, rows = (int(num(b, n) or -1) for n in ("Col0", "Row0", "Cols", "Rows"))
        old = struct.unpack("<%dh" % (cols * rows), blob(b, "OldHeights")) if cols > 0 else ()
        new = struct.unpack("<%dh" % (cols * rows), blob(b, "NewHeights")) if cols > 0 else ()
        changed = sum(1 for a, c in zip(old, new) if a != c)
        need(len(old) == cols * rows == len(new) and changed == int(num(b, "ChangedSamples") or -1)
             and 0 <= col0 and col0 + cols <= 1025 and 0 <= row0 and row0 + rows <= 1025,
             "%s: height box %d x %d at col %d row %d, %d changed samples" % (key, cols, rows, col0, row0, changed),
             "%s: height box inconsistent (%d / %d samples, %d changed)" % (key, len(old), len(new), changed))
        if design and old:
            r0, c0, h, w, box = design
            off = 0
            for r in range(rows):
                for c in range(cols):
                    rr, cc = row0 + r - r0, col0 + c - c0
                    d = box[rr * w + cc] if 0 <= rr < h and 0 <= cc < w else 0
                    if new[r * cols + c] - old[r * cols + c] != d:
                        off += 1
            need(off == 0, "%s: the shipped cut equals the design's main_map_saddle_cuts.raw sample for sample" % key,
                 "%s: %d samples differ from main_map_saddle_cuts.raw - re-emit" % (key, off))
        aw, ah, n = int(num(b, "AlphaW") or 0), int(num(b, "AlphaH") or 0), int(num(b, "PaintTexels") or -1)
        rec = blob(b, "Paint")
        good = len(rec) == 7 * n and aw * ah <= 65535
        for i in range(0, len(rec) if good else 0, 7):
            idx = rec[i] | (rec[i + 1] << 8)
            if idx >= aw * ah or rec[i + 2] == 0 or sum(rec[i + 3:i + 7]) != 255:
                good = False
                break
        need(good, "%s: %d paint records inside the %d x %d texel box, each mix sums to 255" % (key, n, aw, ah),
             "%s: paint records inconsistent" % key)
    props = "\n".join(re.findall(r'"(GW_Scene_1[^"]*)"', data))
    hides = set(re.findall(r"/([^/\\]+)\\t[-\d.]+\\t[-\d.]+\\t[-\d.]+\\thide\\t", props))
    need({"Tonel_GD_LOD_Group", "Zaval_1_LOD_Group", "Tonel_Avto_LOD_Group", "Tonel_Gate_LOD_Group (2)",
          "Tonel_Avto_LOD_Group (1)", "Zaval_2_LOD_Group (1)"} <= hides
         and not {"Railroad_Section12_LOD_Group", "electric_train_LOD_Group"} & hides,
         "the tunnels on the crossing roads go (S1 Tonel_GD + Zaval_1, S2 Tonel_Avto (1) + Zaval_2 (1) - B7b, "
         "S3 Tonel_Avto + gate); the S1 track and train stay",
         "the tunnel hides / track-and-train keeps are not in the data")
    place = re.search(r"Place = \{(.*?)\};", data, re.S)
    cond = re.search(r'"GW_Scene_1\\tServerObjects/Triggers/ChangeLocationTriggers/Marauder_Conductor_02\\t'
                     r'([-\d. ]+)\\t([-\d. ]+)\\t([-\d.]+)\\tSpawnPoints"', place.group(1) if place else "")
    if cond:
        fx, fy, fz = (float(v) for v in cond.group(1).split())
        tx, ty, tz = (float(v) for v in cond.group(2).split())
        moved = ((tx - fx) ** 2 + (tz - fz) ** 2) ** 0.5
        need(tx < 2279.0 and 5.0 < moved < 60.0 and abs(tz - 1813.2) > 25.0,
             "the Conductor goes %.0f m to (%.0f, %.0f), beside the road and off the cut; his SpawnPoints stay"
             % (moved, tx, tz),
             "the Conductor's new spot (%.0f, %.0f) is on the cut, on the road or not beside his old one" % (tx, tz))
    else:
        bad("East crossings: no Place record moves Marauder_Conductor_02 with his SpawnPoints pinned")
    for name in ("Revival.EastCrossings.cs", "Revival.EastCrossingsCore.cs", "Revival.EastCrossingsData.cs"):
        need('"' + name + '"' in read("sync_public.py"), name + " goes into the public repository",
             "sync_public.py does not carry " + name + " - the public build breaks")
    for name in ("research/east_crossings.py", "research/east_crossings_emit.py", "research/east_crossings_unity.py",
                 "unity/EastCrossingsTest/Assets/Scripts/CrossingPlay.cs"):
        need(os.path.exists(os.path.join(ROOT, name)), name + " liegt vor", name + " fehlt")

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
    check_apmine()
    check_gas_launcher()
    check_convoy_ground_and_exit()
    check_convoy_column()
    check_patrol_fall()
    check_patrol_traffic()
    check_mortar()
    check_arty_sync_authority()
    check_arty_battery()
    check_native_action_progress()
    check_technical()
    check_technical_crew()
    check_vehicle_gunner_ai()
    check_settings()
    check_arty_vehicle()
    check_ground_enemies()
    check_helipads()
    check_player_heli()
    check_player_an2()
    check_an2_repair()
    check_fuel_economy()
    check_vehicle_condition()
    check_vehicle_spawns()
    check_mercs()
    check_parachute()
    check_stinger()
    check_crocodile()
    check_traitor_vendor()
    check_editor_heights()
    check_road_clear()
    check_gepard()
    check_gepard_npc()
    check_katyusha()
    check_bomb_damage()
    check_east_world()
    check_east_crossings()
    check_east_roads()
    check_east_pipeline()
    check_airfield()
    check_military_town()
    check_military_town_assembly()
    check_military_town_ring()
    check_flak()
    check_tower_radar()
    check_nofly()
    check_npc_aircraft()
    check_air_events()
    check_retake_raids()
    check_tower_roof()
    check_tower_npc_nav()
    check_version()
    print("=" * 74)
    print("Fehler: %d    Hinweise: %d" % (len(fails), len(warns)))
    for f in fails:
        print("  FEHLER  " + f)
    for w in warns:
        print("  HINWEIS " + w)
    sys.exit(1 if fails else 0)
