# Schnuert aus diesem Repository das Zip fuer die Release-Seite.
#
# Das Repository ist selbst schon spielfertig: wer es als ZIP herunterlaedt,
# entpackt und 1_EINRICHTEN.bat anklickt, kann spielen. Dieses Skript nimmt
# nur die Entwicklerdateien heraus, damit auf der Release-Seite ein Paket
# liegt, in dem nichts steht, was ein Spieler nicht braucht.
#
#   powershell -File make_package.ps1
#   ... -Version 0.3.0     steht im Ordner- und Dateinamen
#   ... -Ziel D:\irgendwo  Ablageort (Vorgabe: dist\ daneben)

param(
    [string]$Version = "0.3.0",
    [string]$Ziel = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $Ziel) { $Ziel = Join-Path $root "dist" }
$name  = "NextDayRevival_Client_" + $Version
$stage = Join-Path $Ziel $name
$zip   = Join-Path $Ziel ($name + ".zip")

# Was ein Spieler braucht. Alles andere - Plugin-Quelltext, Generatoren,
# Bauwerkzeuge, docs\ - bleibt im Repository und nicht im Zip.
$dateien = @(
    "1_EINRICHTEN.bat", "2_SPIELEN.bat",
    "client_patch.ps1", "start_game.ps1",
    "NextDayRevivalToolkit.dll",
    "LIESMICH.txt", "README_EN.txt", "DRITTANBIETER.txt"
)
$ordner = @("assets", "bepinex")

Write-Host ""
Write-Host ("Release-Paket " + $Version) -ForegroundColor White
Write-Host ""

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

foreach ($f in $dateien) {
    $q = Join-Path $root $f
    if (-not (Test-Path $q)) { throw ("fehlt im Repository: " + $f) }
    Copy-Item $q (Join-Path $stage $f) -Force
}
Write-Host ("  Dateien       {0}" -f $dateien.Count)

foreach ($o in $ordner) {
    $q = Join-Path $root $o
    if (-not (Test-Path $q)) { throw ("fehlt im Repository: " + $o) }
    Copy-Item $q (Join-Path $stage $o) -Recurse -Force
    Write-Host ("  {0,-13} {1} Dateien" -f $o, (Get-ChildItem $q -Recurse -File).Count)
}

# Vorschaubilder der Generatoren sind Arbeitsmaterial und im Spiel nutzlos.
foreach ($p in @("*_preview.png", "icon_vergleich.png")) {
    Get-ChildItem (Join-Path $stage "assets") -Filter $p -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

# Version in die Texte einsetzen.
foreach ($f in @("LIESMICH.txt", "README_EN.txt")) {
    $p = Join-Path $stage $f
    $t = (Get-Content $p -Raw).Replace("@VERSION@", $Version)
    [System.IO.File]::WriteAllText($p, $t, (New-Object System.Text.UTF8Encoding($false)))
}

# Gegenprobe. Faellt lieber hier auf als nach dem Hochladen.
$verboten = @("Assembly-CSharp.dll", "*.nd", "players.json", "*.orig_backup",
              "RevivalPlugin.cs", "*.py")
$treffer = @()
foreach ($p in $verboten) {
    $treffer += Get-ChildItem $stage -Recurse -File -Filter $p -ErrorAction SilentlyContinue
}
if ($treffer.Count -gt 0) {
    foreach ($t in $treffer) { Write-Host ("  GEHOERT NICHT INS PAKET: " + $t.Name) -ForegroundColor Red }
    throw "Paket enthaelt Dateien, die dort nicht hingehoeren."
}

if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)

$mb = [math]::Round((Get-Item $zip).Length / 1MB, 2)
Write-Host ""
Write-Host ("FERTIG  {0}  {1} MB" -f $zip, $mb) -ForegroundColor Green
Write-Host ""
Write-Host "Auf die GitHub-Release-Seite laden. Der Spieler entpackt und"
Write-Host "klickt 1_EINRICHTEN.bat an."
Write-Host ""
