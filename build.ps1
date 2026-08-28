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
$src     = Join-Path $root "RevivalPlugin.cs"
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
    (Join-Path $managed "UnityEngine.ImageConversionModule.dll"),
    (Join-Path $managed "UnityEngine.IMGUIModule.dll")
) | Where-Object { Test-Path $_ }

$cscArgs = @("/target:library", "/optimize+", "/nologo", "/warn:2", "/out:$staged")
foreach ($r in $refs) { $cscArgs += "/reference:$r" }
$cscArgs += $src

Write-Host ("uebersetze -> {0}" -f $staged)
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
    "mg42.ndmesh", "mg42_diffuse.png", "mg42_normal.png",
    "mg42_icon.png", "mg42_weapon_icon.png",
    "sniper50.ndmesh", "sniper50_diffuse.png", "sniper50_normal.png",
    "sniper50_icon.png", "sniper50_weapon_icon.png",
    "mgbelt.ndmesh", "mgbelt_diffuse.png", "mgbelt_normal.png", "mgbelt_icon.png",
    "ammo50.ndmesh", "ammo50_diffuse.png", "ammo50_normal.png", "ammo50_icon.png",
    "law.ndmesh", "law_diffuse.png", "law_normal.png",
    "law_icon.png", "law_weapon_icon.png",
    "rocket.ndmesh", "rocket_diffuse.png", "rocket_normal.png", "rocket_icon.png",
    "scope50.png"
)

# Alte Dateien, die es nicht mehr gibt - sonst liegt die Metallic-Map von 0.2.0
# weiter im Zielordner herum und stiftet bei der Fehlersuche Verwirrung.
foreach ($f in @("mg42_metallic.png")) {
    $p = Join-Path $assetDst $f
    if (Test-Path $p) { Remove-Item $p -Force; Write-Host ("  Alt entfernt: {0}" -f $f) }
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
