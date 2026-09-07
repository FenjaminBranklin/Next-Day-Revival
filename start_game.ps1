# Startet Next Day: Survival ohne den EAC-Launcher.
#
# Steam startet normalerweise nextday.exe. Das ist der Launcher von Easy
# Anti-Cheat; er laedt EAC in nextday_game.exe. Seit dem EAC-Modulupdate vom
# 2026-08-27 erkennt EAC dabei die BepInEx-Injektion (winhttp.dll) und bricht
# mit "Untrusted system file" ab - das Spiel startet nicht mehr.
#
# Der DLL-Patch (ClientOptions::IsDisabledEAC -> true) schaltet EAC nur im
# Spielcode ab, nicht den Launcher davor. Also wird nextday_game.exe direkt
# gestartet. Steam muss laufen, steam_appid.txt liegt neben der exe.

param(
    [string]$Game = ""
)

$ErrorActionPreference = "Stop"

# Nicht fest verdrahten: auf einem anderen PC liegt Steam woanders, oft auf
# einer zweiten Platte. Gleiche Suche wie in client_patch.ps1.
function Get-SteamPath {
    $p = @()
    try {
        $r = Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue
        if ($r.SteamPath) { $p += $r.SteamPath }
    } catch {}
    $p += @("C:\Program Files (x86)\Steam", "C:\Program Files\Steam")
    return ($p | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1)
}

function Get-NextDayPath {
    $c = @()
    $steam = Get-SteamPath
    if ($steam) {
        $c += Join-Path $steam "steamapps\common\Next Day Survival"
        $lf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path $lf) {
            $txt = Get-Content $lf -Raw -ErrorAction SilentlyContinue
            [regex]::Matches($txt, '"path"\s+"([^"]+)"') | ForEach-Object {
                $c += Join-Path ($_.Groups[1].Value.Replace("\\", "\")) "steamapps\common\Next Day Survival"
            }
        }
    }
    $c += @("D:\SteamLibrary\steamapps\common\Next Day Survival",
            "E:\SteamLibrary\steamapps\common\Next Day Survival",
            "C:\SteamLibrary\steamapps\common\Next Day Survival")
    return ($c | Where-Object { Test-Path (Join-Path $_ "nextday_game.exe") } | Select-Object -First 1)
}

if (-not $Game) { $Game = Get-NextDayPath }
if (-not $Game) { throw "Spielordner nicht gefunden. Mit -Game <Pfad> angeben." }

$exe = Join-Path $Game "nextday_game.exe"
if (-not (Test-Path $exe)) { throw "nicht gefunden: $exe" }

if (Get-Process nextday_game -ErrorAction SilentlyContinue) {
    Write-Host "Das Spiel laeuft bereits." -ForegroundColor Yellow
    return
}

# Every supported entry point reaches this gate, including an old launcher's
# Play button when it calls a newly downloaded start script.
$lockHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash(
    [Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($Game).ToLowerInvariant()))).Replace('-', '')
$launchLock = New-Object Threading.Mutex($false, ('Local\NextDayRevival-' + $lockHash))
try { $locked = $launchLock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
if (-not $locked) { $launchLock.Dispose(); throw 'Another launcher is already preparing this game. Please wait.' }
try {
$updater = Join-Path $PSScriptRoot 'player_update.ps1'
if (-not (Test-Path -LiteralPath $updater)) { throw 'Updater missing. Download the latest Launcher from GitHub.' }
$configText = Get-Content -LiteralPath (Join-Path $Game 'nextday_game_Data/ClientConfig.ini') -Raw -ErrorAction SilentlyContinue
$serverHost = '187.124.117.145'
if ($configText -match '"ServersListURL"\s*:\s*"(https?://[^\"]+)"') { $serverHost = ([uri]$Matches[1]).Host }
. $updater -Game $Game -Server $serverHost
$receipt = Invoke-NdrPrepare $Game $serverHost

if (-not (Get-Process steam -ErrorAction SilentlyContinue)) {
    $steamDir = Get-SteamPath
    $steam = if ($steamDir) { Join-Path $steamDir "steam.exe" } else { "" }
    if (-not $steam -or -not (Test-Path $steam)) { throw "Steam nicht gefunden. Steam von Hand starten." }
    Write-Host "Steam startet ..."
    Start-Process $steam
    for ($i = 0; $i -lt 30; $i++) {
        if (Get-Process steam -ErrorAction SilentlyContinue) { break }
        Start-Sleep -Seconds 1
    }
}

$log = Join-Path $Game "BepInEx\LogOutput.log"
$vorher = if (Test-Path $log) { (Get-Item $log).LastWriteTime } else { $null }

try {
    $env:NDR_VERIFIED_RECEIPT = $receipt
    Start-Process -FilePath $exe -WorkingDirectory $Game
} finally { Remove-Item Env:NDR_VERIFIED_RECEIPT -ErrorAction SilentlyContinue }
Write-Host "nextday_game.exe gestartet (ohne EAC-Launcher)."

for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Seconds 1
    if (Test-Path $log) {
        $jetzt = (Get-Item $log).LastWriteTime
        if ($null -eq $vorher -or $jetzt -gt $vorher) {
            Write-Host "BepInEx laedt (LogOutput.log $jetzt)." -ForegroundColor Green
            return
        }
    }
}

Write-Host "BepInEx hat in 40s nichts geschrieben - LogOutput.log pruefen." -ForegroundColor Yellow
} finally { $launchLock.ReleaseMutex(); $launchLock.Dispose() }
