# Baut das Plugin und legt es zusammen mit den Assets im Spiel ab.
#
# Zweistufig, und zwar mit Absicht: uebersetzt wird immer nach .\build, kopiert
# wird nur, wenn nextday_game.exe NICHT laeuft. Ein laufendes Spiel haelt die
# DLL als Speicherabbild offen; csc bricht dann mit
#   error CS0016 ... "Der Vorgang ist bei einer Datei mit einem geoeffneten
#   Bereich, der einem Benutzer zugeordnet ist, nicht anwendbar"
# ab, und man haelt die Meldung faelschlich fuer einen Compilerfehler.
#
#   .\build.ps1              uebersetzen, danach installieren wenn moeglich
#   .\build.ps1 -NoInstall   nur uebersetzen (Syntaxpruefung)
#   .\build.ps1 -Force       auch installieren, wenn das Spiel laeuft (schlaegt fehl)

param(
    [switch]$NoInstall,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Spielordner nicht fest verdrahten - auf einem anderen PC liegt Steam
# woanders. Gleiche Suche wie in client_patch.ps1.
$game = ""
try {
    $r = Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue
    $basen = @($r.SteamPath, "C:\Program Files (x86)\Steam", "C:\Program Files\Steam")
} catch { $basen = @("C:\Program Files (x86)\Steam") }
$kandidaten = @()
foreach ($b in $basen) {
    if (-not $b -or -not (Test-Path $b)) { continue }
    $kandidaten += Join-Path $b "steamapps\common\Next Day Survival"
    $lf = Join-Path $b "steamapps\libraryfolders.vdf"
    if (Test-Path $lf) {
        [regex]::Matches((Get-Content $lf -Raw), '"path"\s+"([^"]+)"') | ForEach-Object {
            $kandidaten += Join-Path ($_.Groups[1].Value.Replace("\\","\")) "steamapps\common\Next Day Survival"
        }
    }
}
$game = $kandidaten | Where-Object { Test-Path (Join-Path $_ "nextday_game.exe") } | Select-Object -First 1
if (-not $game) { throw "Spielordner nicht gefunden. Next Day: Survival muss installiert sein." }
$managed = Join-Path $game "nextday_game_Data\Managed"
$core    = Join-Path $game "BepInEx\core"
$plugins = Join-Path $game "BepInEx\plugins"
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
# Compile every top-level .cs beside build.ps1. Core and feature systems live in
# separate source files so agents can load and own only the relevant module.
# csc compiles them into one DLL. RevivalPlugin.cs comes first, the rest sorted;
# order is irrelevant to csc but a stable build is easier to read.
$mainSrc = Join-Path $root "RevivalPlugin.cs"
$src     = @($mainSrc) + (
    Get-ChildItem -Path $root -Filter *.cs -File |
        Where-Object { $_.FullName -ne $mainSrc } |
        Sort-Object Name |
        ForEach-Object { $_.FullName }
)
if ($src.Count -eq 0) { throw "no .cs sources found in $root" }
$stage   = Join-Path $root "build"
$staged  = Join-Path $stage "NextDayRevivalToolkit.dll"
$out     = Join-Path $plugins "NextDayRevivalToolkit.dll"

# BepInEx 5.4 / HarmonyX are CLR v2.0.50727, so the 3.5 compiler is the match.
$csc = "C:\Windows\Microsoft.NET\Framework64\v3.5\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

New-Item -ItemType Directory -Force -Path $stage | Out-Null

$refs = @(
    (Join-Path $core "BepInEx.dll"),
    (Join-Path $core "0Harmony.dll"),
    (Join-Path $managed "UnityEngine.dll"),
    (Join-Path $managed "UnityEngine.CoreModule.dll"),
    (Join-Path $managed "UnityEngine.PhysicsModule.dll"),
    (Join-Path $managed "UnityEngine.AIModule.dll"),
    (Join-Path $managed "UnityEngine.ImageConversionModule.dll"),
    (Join-Path $managed "UnityEngine.IMGUIModule.dll"),
    # AudioModule: die Drohne rechnet ihr Surren zur Laufzeit aus (AudioClip.Create)
    # und haengt es an eine AudioSource. Ohne das hoert man sie nicht kommen.
    (Join-Path $managed "UnityEngine.AudioModule.dll"),
    # ParticleSystemModule: das Feuer auf der Explosion (FireEffect) baut seine
    # Partikelsysteme zur Laufzeit. Ohne diese Referenz kennt der Compiler
    # ParticleSystem nicht.
    (Join-Path $managed "UnityEngine.ParticleSystemModule.dll"),
    # Terrain + TerrainPhysics: a helipad clears the ground it is built on, and
    # a tree on this map is not a GameObject but an entry in
    # TerrainData.treeInstances, with its collider on a SECOND, collider-only
    # terrain (REVERSE_ENGINEERING.md 37). Both are read and written directly
    # in Revival.Helipads.cs; without these references the compiler knows
    # neither Terrain, TerrainData, TreeInstance nor TerrainCollider.
    (Join-Path $managed "UnityEngine.TerrainModule.dll"),
    (Join-Path $managed "UnityEngine.TerrainPhysicsModule.dll")
) | Where-Object { Test-Path $_ }

# /codepage:65001 - plugin sources are UTF-8 (no BOM) and may carry Russian
# string literals for in-game text. Without this flag csc reads them under the
# system ANSI code page and the Cyrillic turns into mojibake in the compiled
# strings. Files stay BOM-less so Python tools see no leading marker.
$cscArgs = @("/target:library", "/optimize+", "/nologo", "/warn:2",
             "/codepage:65001", "/out:$staged")
foreach ($r in $refs) { $cscArgs += "/reference:$r" }
foreach ($s in $src) { $cscArgs += $s }

Write-Host ("uebersetze {0} Datei(en) -> {1}" -f $src.Count, $staged)
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed with exit code $LASTEXITCODE" }
Write-Host ("OK  {0} bytes" -f (Get-Item $staged).Length)

if ($NoInstall) { Write-Host "-NoInstall: nicht installiert."; exit 0 }

$running = @(Get-Process -Name "nextday_game" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0 -and -not $Force) {
    Write-Host ""
    Write-Host ("nextday_game.exe laeuft (PID {0}) und haelt die DLL offen." -f $running[0].Id)
    Write-Host "Nicht installiert. Spiel schliessen, dann .\build.ps1 erneut ausfuehren."
    exit 2
}

New-Item -ItemType Directory -Force -Path $plugins | Out-Null
Copy-Item $staged $out -Force
Write-Host ("installiert -> {0}" -f $out)

$assetSrc = Join-Path $root "assets"
$assetDst = Join-Path $plugins "assets"
New-Item -ItemType Directory -Force -Path $assetDst | Out-Null

$assets = @(
    "stinger.ndmesh", "stinger_diffuse.png", "stinger_normal.png",
    "stinger_metal.png", "stinger_rough.png", "stinger_icon.png",
    "stinger_weapon_icon.png", "stinger_missile.ndmesh", "stinger_missile_diffuse.png",
    "stinger_missile_normal.png", "stinger_missile_metal.png", "stinger_missile_rough.png",
    "stinger_missile_icon.png", "stinger_scope.png",
    "arty_hull.ndmesh", "arty_turret.ndmesh", "arty_barrel.ndmesh", "arty_recoil.ndmesh",
    "arty_diffuse.png", "arty_metal.png", "arty_normal.png",
    # The Gepard (RevivalGepard.cs): seven moving parts, atlas, metal map and
    # the pivot file, all written by gepard_import.py from assets/gepard/.
    "gepard_hull.ndmesh", "gepard_tracks.ndmesh", "gepard_turret.ndmesh",
    "gepard_gun_r.ndmesh", "gepard_gun_l.ndmesh", "gepard_radar_search.ndmesh",
    "gepard_radar_track.ndmesh", "gepard_diffuse.png", "gepard_metal.png",
    "gepard_rig.txt",
    "mg42.ndmesh", "mg42_diffuse.png", "mg42_normal.png", "mg42_metal.png",
    "mg42_rough.png", "mg42_icon.png", "mg42_weapon_icon.png",
    "sniper50.ndmesh", "sniper50_diffuse.png", "sniper50_normal.png",
    "sniper50_metal.png", "sniper50_rough.png",
    "sniper50_icon.png", "sniper50_weapon_icon.png",
    "mgbelt.ndmesh", "mgbelt_diffuse.png", "mgbelt_normal.png", "mgbelt_icon.png",
    "ammo50.ndmesh", "ammo50_diffuse.png", "ammo50_normal.png", "ammo50_icon.png",
    "law.ndmesh", "law_diffuse.png", "law_normal.png", "law_metal.png",
    "law_rough.png", "law_icon.png", "law_weapon_icon.png",
    "rocket.ndmesh", "rocket_diffuse.png", "rocket_normal.png", "rocket_icon.png",
    "drone.ndmesh", "drone_diffuse.png", "drone_normal.png", "drone_icon.png",
    "jammer.ndmesh", "jammer_diffuse.png", "jammer_normal.png", "jammer_icon.png",
    "t72_hull.ndmesh", "t72_turret.ndmesh", "t72_diffuse.png", "t72_normal.png",
    "t72_metal.png", "t72_scope.png",
    "apc_scope.png",
    "shell125.ndmesh", "shell125_diffuse.png", "shell125_normal.png",
    "shell125_icon.png",
    # Anti-tank mine (item 1490): mesh + diffuse/normal + inventory icon (the
    # item passes null for metal, so mine_metal/rough are generated but unused).
    "mine.ndmesh", "mine_diffuse.png", "mine_normal.png", "mine_icon.png",
    # PMN-2 anti-personnel mine (item 1492), Blender-built (apmine_build.py):
    # the item and the laid mine both read the metal map.
    "apmine.ndmesh", "apmine_diffuse.png", "apmine_normal.png", "apmine_metal.png",
    "apmine_rough.png", "apmine_icon.png",
    # Blender-built toxic crocodile boss in the Point 12 lake.
    "crocodile.ndmesh", "crocodile_diffuse.png", "crocodile_normal.png",
    # Its jaw/leg part map from the same Blender run (crocodile_build.py).
    "crocodile_rig.bin",
    # Deployed head of the mast antenna (item 2055): mesh only.
    "antenna_head.ndmesh",
    # Vehicle modules (2060 thermal, 2061 night vision, 2062 large jammer) and
    # drone gear (2055 mast antenna, 2056 battery, 2057 recon drone). Each of
    # the six carries its own model, textures and inventory icon; until
    # vehicle_modules_build.py / drone_gear_build.py they borrowed the jammer,
    # the .50 ammo tin and the FPV drone, so five of them shared one icon.
    "thermal.ndmesh", "thermal_diffuse.png", "thermal_normal.png",
    "thermal_icon.png",
    "nvmodule.ndmesh", "nvmodule_diffuse.png", "nvmodule_normal.png",
    "nvmodule_icon.png",
    "jammod.ndmesh", "jammod_diffuse.png", "jammod_normal.png",
    "jammod_icon.png",
    "antenna_pack.ndmesh", "antenna_pack_diffuse.png", "antenna_pack_normal.png",
    "antenna_pack_icon.png",
    "battery.ndmesh", "battery_diffuse.png", "battery_normal.png",
    "battery_icon.png",
    "survdrone.ndmesh", "survdrone_diffuse.png", "survdrone_normal.png",
    "survdrone_icon.png",
    # Parachute (2067) - the PACKED chute in the backpack (parachute_build.py).
    # The canopy that opens in the air is the game's own prefab.
    "parachute.ndmesh", "parachute_diffuse.png", "parachute_normal.png",
    "parachute_icon.png",
    "scope50.png",
    # Helipad decks: one painted texture per built surface, mapped
    # radius-relative over the whole pad (helipad_texture.py). Without them the
    # pad falls back to flat colours, so they are assets and not a hard
    # requirement.
    "helipad_concrete.png", "helipad_concrete_normal.png", "helipad_concrete_metal.png",
    "helipad_steel.png", "helipad_steel_normal.png", "helipad_steel_metal.png",
    # Runtime view generated by routeeditor.py from the authoritative JSON.
    # Unlike ndr_routes.tsv, the game never edits this file, so each build may
    # safely refresh the installed vehicle/crew/loadout composition.
    "ndr_composition.tsv",
    # Heli troop landings, same story: an editor-written runtime view the game
    # never edits. A default header-only file ships so the feature is dormant
    # until landings are authored (RevivalTroopInsertion.cs).
    "ndr_troopdrops.tsv",
    # Helicopter landing pads, the third editor-written runtime view. Header
    # only until pads are authored, so Revival.Helipads.cs builds nothing.
    "ndr_helipads.tsv",
    # East extension probe (Revival.EastTile.cs, [Research] EastTile, off by
    # default): a Unity 2018.1.0f2 scene bundle with one terrain tile, built by
    # unity/EastTileProbe. Shipped like a real tile would be.
    "east_tile_probe.bundle",
    # The east tile itself ([World] EastTile, Revival.EastWorld.cs, off by
    # default): the scene bundle built by unity/EastTile BuildTile.cs, its
    # game content referenced in the game's own files, not copied.
    "east_tile.bundle",
    # Its placed content, one additive scene bundle per concern, loaded with
    # the tile (unity/EastTile BuildContent.cs). Build products of
    # rebuild_east.ps1, committed on main only (docs/ai/tasks/east-pipeline.md).
    "east_airfield.bundle", "east_town.bundle", "east_bunker.bundle", "east_content_test.bundle",
    # Bilingual 2:1 artwork for that world. MapInk installs one only while the
    # east world is active; the vanilla MapLanguagePreset remains untouched off.
    "east_map_en.png", "east_map_ru.png",
    # Its map window (Revival.EastMapPanel.cs): frame, legend icons, the
    # 20 x 10 grid in RU/EN - research/east_map_panel.py.
    "east_map_form.png", "east_map_legend.png", "east_map_grid_en.png", "east_map_grid_ru.png"
)

# Alte Dateien, die es nicht mehr gibt - sonst liegt die Metallic-Map von 0.2.0
# weiter im Zielordner herum und stiftet bei der Fehlersuche Verwirrung.
foreach ($f in @("mg42_metallic.png")) {
    $p = Join-Path $assetDst $f
    if (Test-Path $p) { Remove-Item $p -Force; Write-Host ("  Alt entfernt: {0}" -f $f) }
}

# ndr_routes.tsv has TWO writers: the map/road/composition editor
# (routeeditor.py, the authoring surface) writes the repository copy, and the
# in-game recorder appends to the INSTALLED copy. Overwriting blindly either
# way loses work: install the editor file over an in-game recording and the
# recording is gone; keep the installed file forever and the editor's routes
# never reach the game (which is exactly the "the server still has none of my
# editor routes" failure). So: NEWEST WINS. When the repository (editor) file
# is newer than the installed one, it is installed and the previous installed
# file is kept beside it as ndr_routes.prev.tsv; when the installed file is
# newer (a fresh in-game recording), it is left in place. python
# routecheck.py --pull is still the way to fold an in-game recording back into
# the repository.
$routes = "ndr_routes.tsv"
$routeSrc = Join-Path $assetSrc $routes
$routeDst = Join-Path $assetDst $routes
$routePrev = Join-Path $assetDst "ndr_routes.prev.tsv"
if (-not (Test-Path $routeSrc)) {
    throw "Assets fehlen: $routes`nErst die Generatoren laufen lassen: python make_assets.py"
}
if (-not (Test-Path $routeDst)) {
    Copy-Item $routeSrc $routeDst -Force
    Write-Host ("  Asset kopiert: {0,-26} {1,8} bytes" -f $routes, (Get-Item $routeDst).Length)
} elseif ((Get-Item $routeSrc).LastWriteTimeUtc -gt (Get-Item $routeDst).LastWriteTimeUtc) {
    Copy-Item $routeDst $routePrev -Force
    Copy-Item $routeSrc $routeDst -Force
    Write-Host ("  Editor-Route installiert (neuer): {0,-8} {1,8} bytes" -f $routes, (Get-Item $routeDst).Length)
    Write-Host ("    vorherige installierte Datei gesichert: ndr_routes.prev.tsv")
    Write-Host ("    war das eine im Spiel aufgezeichnete Route? python routecheck.py --pull")
} else {
    Write-Host ("  Route-Datei behalten: {0} (installierte Fassung ist neuer als der Editor-Stand)" -f $routes)
    Write-Host ("    zurueck ins Repository: python routecheck.py --pull")
}

$missing = @()
foreach ($f in $assets) {
    $s = Join-Path $assetSrc $f
    if (-not (Test-Path $s)) { $missing += $f; continue }
    Copy-Item $s (Join-Path $assetDst $f) -Force
    Write-Host ("  Asset kopiert: {0,-26} {1,8} bytes" -f $f, (Get-Item (Join-Path $assetDst $f)).Length)
}
if ($missing.Count -gt 0) {
    throw ("Assets fehlen: {0}`nErst die Generatoren laufen lassen: python make_assets.py" -f ($missing -join ", "))
}

Write-Host ""
Write-Host ("FERTIG  {0}  {1} bytes" -f (Split-Path -Leaf $out), (Get-Item $out).Length)
