# Stufe 3 - Client-Patch: macht aus einer Steam-Installation einen Client,
# der sich am eigenen Masterserver anmeldet.
#
# Fuenf Dinge, ein Aufruf:
#   1. ClientConfig.ini zeigt auf die Serverliste des VPS
#   2. Assembly-CSharp.dll wird gepatcht, damit EAC im Spielcode aus ist
#   3. BepInEx wird installiert, falls keins da ist und eins beiliegt
#   4. Das aktuelle T-72-Modell entsteht lokal aus den eigenen Spieldateien
#   5. Plugin und Assets liegen in BepInEx\plugins
#
# Absichtlich eigenstaendig: kein Python, kein csc, kein Masterserver-Ordner,
# kein Adminrecht. Es soll auch auf dem PC eines Mitspielers laufen, der nur
# dieses Verzeichnis als Zip bekommen hat. Der Steam-Ordner ist beschreibbar,
# weil Steam ihn beim Anlegen fuer die Benutzergruppe freigibt - deshalb
# reicht eine normale PowerShell.
#
#   powershell -ExecutionPolicy Bypass -File client_patch.ps1
#   ... -Check                 nur nachsehen, nichts aendern
#   ... -Name "Kevin"          Spielername in ClientConfig.ini
#   ... -Server 1.2.3.4        anderer Masterserver
#   ... -Restore               ClientConfig.ini zurueck auf die letzte Sicherung
#
# Danach starten mit:  powershell -File start_game.ps1
# Nicht ueber den Steam-Play-Knopf - der startet den EAC-Launcher (E-014).

param(
    [string]$Server = "",
    [string]$Name   = "",
    [string]$Game   = "",
    [switch]$Restore,
    [switch]$Check,
    [switch]$NoPlugin,
    [switch]$NoEac,
    [switch]$ResetConfig
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Rueckfall, wenn der Masterserver-Ordner nicht danebenliegt. Steht auch in
# deploy\server.json als advertiseHost - von dort wird bevorzugt gelesen,
# damit es bei einem Serverumzug genau eine Stelle zu aendern gibt.
$SERVER_FALLBACK = "187.124.117.145"

$fehler = @()
$hinweise = @()

function Schritt($n, $text) {
    Write-Host ""
    Write-Host ("[{0}] {1}" -f $n, $text) -ForegroundColor Cyan
}
function Gut($t)  { Write-Host ("    OK      " + $t) -ForegroundColor Green }
function Info($t) { Write-Host ("            " + $t) }
function Warn($t) { Write-Host ("    ACHTUNG " + $t) -ForegroundColor Yellow; $script:hinweise += $t }
function Bad($t)  { Write-Host ("    FEHLER  " + $t) -ForegroundColor Red;    $script:fehler   += $t }


# ---------------------------------------------------------------- Spiel finden

function Get-SteamPath {
    $p = @()
    try {
        $r = Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue
        if ($r.SteamPath) { $p += $r.SteamPath }
    } catch {}
    $p += @("C:\Program Files (x86)\Steam", "C:\Program Files\Steam")
    return ($p | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1)
}

# Wie im Masterserver-Skript: Registry, dann die Bibliotheksordner aus
# libraryfolders.vdf, dann die ueblichen zweiten Platten.
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

Write-Host ""
Write-Host "Next Day: Survival - Client-Patch (Stufe 3)" -ForegroundColor White

Schritt 1 "Spielordner"
if (-not $Game) { $Game = Get-NextDayPath }
if (-not $Game -or -not (Test-Path (Join-Path $Game "nextday_game.exe"))) {
    Write-Host ""
    Write-Host "Der Spielordner wurde nicht gefunden." -ForegroundColor Red
    Write-Host "Er heisst 'Next Day Survival' und enthaelt nextday_game.exe."
    Write-Host "Mit -Game angeben, zum Beispiel:"
    Write-Host '    powershell -File client_patch.ps1 -Game "D:\SteamLibrary\steamapps\common\Next Day Survival"'
    exit 1
}
Gut $Game

$dataDir  = Join-Path $Game "nextday_game_Data"
$managed  = Join-Path $dataDir "Managed"
$config   = Join-Path $dataDir "ClientConfig.ini"
$dll      = Join-Path $managed "Assembly-CSharp.dll"
$bepCore  = Join-Path $Game "BepInEx\core\BepInEx.dll"
$plugins  = Join-Path $Game "BepInEx\plugins"
$pluginCfg = Join-Path $Game "BepInEx\config\nextday.revival.toolkit.cfg"
$sicher   = Join-Path $root "backup_game"
# Config.Bind reads the value out of the existing file and ignores the default
# in the code, so a client can carry the right DLL and the right assets and
# still play like an older version. Seen on the second machine on 2026-08-31:
# sixteen values from an older build under a header BepInEx had already
# relabelled to the current version.
#
# This is a LINE EDITOR, like retune.py and for the same reason: the file's own
# "# Default value:" comments are what the installed plugin wrote, so the file
# is its own reference and nothing outside it has to be kept in step. Only
# version-owned keys are rewritten; a key binding or a personal setting belongs
# to the player and is left exactly as it stands, comments included.
#
# A value can also differ on purpose. The file cannot tell a deliberate choice
# from an older version's leftover, so the person at the keyboard says which is
# which: put a line saying ndr-keep in a key's comment block and that key is
# never counted and never reset.
#
#     # ndr-keep
#     # Setting type: Boolean
#     # Default value: false
#     Enabled = true

$configPlayerKeys = @(
    "AllowedSteamIds",
    "ConfineCursorToWindow", "CursorLockFix",
    "InvertX", "InvertY", "Sensitivity",
    "SoundVolume",
    "Verbose", "NetWatch", "NetWatchEvery", "NetWatchHitch"
)

function Test-PlayerKey($key) {
    if ($configPlayerKeys -contains $key) { return $true }
    if ($key -eq "Key" -or $key.EndsWith("Key")) { return $true }
    return $false
}

# The same walk as Reset-PluginConfig, without writing. -Check must be able to
# say what would change, and must never change it.
function Get-ConfigDriftNames($cfgPath) {
    $out = @()
    if (-not (Test-Path $cfgPath)) { return $out }
    $default = $null
    $pinned = $false
    foreach ($line in (Get-Content -LiteralPath $cfgPath)) {
        if ($line -match 'ndr-keep') { $pinned = $true; continue }
        $m = [regex]::Match($line, '^#\s*Default value:\s*(.*)$')
        if ($m.Success) { $default = $m.Groups[1].Value.Trim(); continue }
        $m = [regex]::Match($line, '^([A-Za-z0-9_ ]+?)\s*=\s*(.*)$')
        if ($m.Success -and $null -ne $default) {
            $key = $m.Groups[1].Value.Trim()
            $value = $m.Groups[2].Value.Trim()
            if ((-not $pinned) -and (-not (Test-PlayerKey $key)) -and $value -ne $default) {
                $out += ("{0}={1} (soll {2})" -f $key, $value, $default)
            }
            $default = $null
            $pinned = $false
        }
    }
    return $out
}

function Reset-PluginConfig($cfgPath) {
    if (-not (Test-Path $cfgPath)) {
        Info "keine Plugin-Konfiguration vorhanden - nichts zurueckzusetzen"
        return
    }
    $lines = @(Get-Content -LiteralPath $cfgPath)
    $default = $null
    $changed = @()
    $pinned = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match 'ndr-keep') { $pinned = $true; continue }
        $m = [regex]::Match($lines[$i], '^#\s*Default value:\s*(.*)$')
        if ($m.Success) { $default = $m.Groups[1].Value.Trim(); continue }
        $m = [regex]::Match($lines[$i], '^([A-Za-z0-9_ ]+?)(\s*=\s*)(.*)$')
        if ($m.Success -and $null -ne $default) {
            $key = $m.Groups[1].Value.Trim()
            $value = $m.Groups[3].Value.Trim()
            if ((-not $pinned) -and (-not (Test-PlayerKey $key)) -and $value -ne $default) {
                $lines[$i] = $m.Groups[1].Value + $m.Groups[2].Value + $default
                $changed += ("{0}: {1} -> {2}" -f $key, $value, $default)
            }
            $default = $null
            $pinned = $false
        }
    }
    if ($changed.Count -eq 0) {
        Gut "Plugin-Konfiguration entspricht bereits dieser Fassung"
        return
    }
    # Back up before writing, always. The player's previous choices stay
    # readable, which is the whole reason this is allowed to overwrite at all.
    $cfgDir = Split-Path -Parent $cfgPath
    $backupCfg = Join-Path $cfgDir ("nextday.revival.toolkit.cfg.bak_reset_" +
        (Get-Date -Format "yyyyMMdd_HHmmss"))
    Copy-Item -LiteralPath $cfgPath -Destination $backupCfg -Force
    Set-Content -LiteralPath $cfgPath -Value $lines -Encoding UTF8
    Gut ("Plugin-Konfiguration gesichert: " + (Split-Path -Leaf $backupCfg))
    Gut ("{0} Einstellungen auf die Werte dieser Fassung zurueckgesetzt" -f $changed.Count)
    foreach ($c in $changed) { Info ("    " + $c) }
}

$sourceVersion = ""
$versionFile = Join-Path $root "VERSION"
if (Test-Path $versionFile) {
    $sourceVersion = (Get-Content $versionFile -Raw -ErrorAction SilentlyContinue).Trim()
}

# Ein laufendes Spiel haelt Assembly-CSharp.dll und die Plugin-DLL als
# Speicherabbild offen. Schreiben scheitert dann - oder hinterlaesst, schlimmer,
# eine halb geschriebene Datei.
#
# Nur der Prozess AUS DIESEM Ordner zaehlt. Laesst sich der Pfad eines
# Prozesses nicht lesen (kommt bei fremden Rechten vor), gilt er als
# zugehoerig - lieber einmal zu oft abbrechen als in eine offene Datei
# schreiben.
$laeuft = @(Get-Process -Name "nextday_game" -ErrorAction SilentlyContinue | Where-Object {
    try { $_.MainModule.FileName -like ((Join-Path $Game "") + "*") } catch { $true }
})
if ($laeuft.Count -gt 0 -and -not $Check) {
    Write-Host ""
    Write-Host ("nextday_game.exe laeuft (PID {0})." -f $laeuft[0].Id) -ForegroundColor Red
    Write-Host "Spiel schliessen, dann erneut. (Nur nachsehen: -Check)"
    exit 1
}


# ------------------------------------------------------------- Serveradresse

Schritt 2 "Serveradresse"
if (-not $Server) {
    # Eine Quelle der Wahrheit: der Server sagt selbst, unter welchem Namen er
    # erreichbar ist. Liegt der Masterserver-Ordner nicht daneben (Mitspieler-PC),
    # greift der eingebaute Rueckfall.
    $serverJson = Join-Path (Split-Path -Parent $root) "NextDaySurvival_Stage64_PhotonOwnAppId\deploy\server.json"
    if (Test-Path $serverJson) {
        $txt = Get-Content $serverJson -Raw
        $m = [regex]::Match($txt, '"advertiseHost"\s*:\s*"([^"]+)"')
        if ($m.Success) {
            $Server = $m.Groups[1].Value
            Info ("aus server.json: " + $serverJson)
        }
    }
}
if (-not $Server) { $Server = $SERVER_FALLBACK; Info "eingebauter Rueckfall" }
$listUrl = "http://" + $Server + ":12080/servers_report"
Gut $listUrl


# ------------------------------------------------------- ClientConfig.ini

Schritt 3 "ClientConfig.ini"

# Der Client liest hier nur die Adresse der Serverliste. Welcher Masterserver
# und welche Photon-AppId benutzt werden, steht in der JSON, die der Server auf
# 12080 ausliefert - deshalb muss bei einem Serverumzug nur der Server umziehen,
# nicht jeder Client neu gepatcht werden, solange die Adresse gleich bleibt.
function Get-Feld($text, $name) {
    if (-not $text) { return "" }
    $m = [regex]::Match($text, ('"' + $name + '"\s*:\s*"([^"]*)"'))
    if ($m.Success) { return $m.Groups[1].Value }
    return ""
}

$alt = $null
if (Test-Path $config) { $alt = Get-Content $config -Raw -ErrorAction SilentlyContinue }

# Die Sicherungen aller Installationen liegen in einem Ordner. Ohne Kennung
# haette -Restore die Datei einer ANDEREN Installation zurueckgespielt - beim
# Test mit -Game genau so passiert. Acht Zeichen aus dem Pfad reichen.
$sha1 = [System.Security.Cryptography.SHA256]::Create()
$kennung = ([BitConverter]::ToString(
    $sha1.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Game.ToLower())))
).Replace("-", "").Substring(0, 8).ToLower()

if ($Restore) {
    $letzte = Get-ChildItem (Join-Path $sicher ("ClientConfig.ini." + $kennung + ".*")) -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $letzte) {
        Write-Host ("    FEHLER  Keine Sicherung fuer diesen Spielordner in " + $sicher) -ForegroundColor Red
        Write-Host ""
        exit 1
    }
    Copy-Item $letzte.FullName $config -Force
    Gut ("zurueckgespielt <- " + $letzte.Name)
    Info ((Get-Content $config -Raw).Trim())
    Write-Host ""
    exit 0
}

# Name: was schon dasteht, bleibt stehen. Sonst -Name, sonst der Windows-Benutzer.
# HYPOTHESE: ob UserName wirklich bis zum Charakternamen durchschlaegt, ist nicht
# belegt - der Server nimmt den Namen aus basicPlayerInfo.charName des Profils.
if (-not $Name) { $Name = Get-Feld $alt "UserName" }
if (-not $Name) { $Name = $env:USERNAME }

$neu = "{`r`n" +
       "  `"ServersListURL`": `"$listUrl`",`r`n" +
       "  `"Language`": `"`",`r`n" +
       "  `"UserName`": `"$Name`"`r`n" +
       "}`r`n"

$altUrl = Get-Feld $alt "ServersListURL"
if ($altUrl) { Info ("bisher:  " + $altUrl) }
Info ("neu:     " + $listUrl)
Info ("Name:    " + $Name)

if ($Check) {
    if ($altUrl -eq $listUrl) { Gut "zeigt schon auf den Server" }
    else { Warn "zeigt noch nicht auf den Server" }
} else {
    New-Item -ItemType Directory -Force -Path $sicher | Out-Null
    if ($alt) {
        # Immer sichern, nicht nur beim ersten Mal: die Datei ist 100 Byte gross,
        # und der Zustand davor ist genau das, was -Restore braucht.
        $stempel = Join-Path $sicher ("ClientConfig.ini." + $kennung + "." + (Get-Date -Format "yyyyMMdd_HHmmss"))
        Copy-Item $config $stempel -Force
        Info ("gesichert -> backup_game\" + (Split-Path -Leaf $stempel))
    }
    # UTF-8 mit BOM und CRLF - genau die Form, die im Spiel nachweislich gelesen
    # wird. Nicht ueber Set-Content: das schreibt je nach PowerShell-Fassung mal
    # mit und mal ohne BOM.
    [System.IO.File]::WriteAllText($config, $neu, (New-Object System.Text.UTF8Encoding($true)))
    $pruef = Get-Feld (Get-Content $config -Raw) "ServersListURL"
    if ($pruef -eq $listUrl) { Gut "geschrieben" } else { Bad ("Nachkontrolle fehlgeschlagen: " + $pruef) }
}


# ------------------------------------------------------------------ EAC

Schritt 4 "EAC im Spielcode"

# Gepatcht wird genau eine Methode: ClientOptions::IsDisabledEAC liefert hart
# true, statt das Feld _isDisabledEAC zu lesen. Belegt durch einen Vergleich
# aller 15395 Methodenrumpfe (siehe eacpatch.py).
#
#   original    1E   02 7B 0C 27 00 04 2A    ldarg.0; ldfld _isDisabledEAC; ret
#   gepatcht    1E   17 2A 00 00 00 00 00    ldc.i4.1; ret; aufgefuellt mit nop
#
# 0x1E ist der Tiny-Header (Laenge 7 << 2 | 2). Kopf und Laenge bleiben, der
# Rest wird nop: Dateigroesse und alle Metadaten bleiben unveraendert.
#
# Hier wird per Bytemuster gesucht statt ueber die Metadaten wie in eacpatch.py,
# damit das Skript ohne Python auskommt. Gemessen an der Auslieferungs-DLL:
# das Muster kommt genau EINMAL vor - der Feldtoken 0C270004 macht es
# eindeutig. Kommt es nicht genau einmal vor, wird nichts angefasst.
$EAC_ORIG    = [byte[]](0x1E, 0x02, 0x7B, 0x0C, 0x27, 0x00, 0x04, 0x2A)
$EAC_PATCHED = [byte[]](0x1E, 0x17, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x00)

# Bekannte Staende dieser Datei. Der dnSpy-Stand hat dasselbe Ergebnis, aber
# eine neu geschriebene Assembly (60 KB kleiner) - dort ist das Muster nicht
# mehr zu finden, deshalb die Liste.
$EAC_BEKANNT = @{
    "3c1f95398253c8beda36b1ed511bb56f9950f948695649d8ff8222f61507b122" = "ORIGINAL (EAC an)"
    "51dc9ed33b08021e49b3d578e93e2e3ffe0c54e699b517d25b7e78409981000b" = "GEPATCHT (EAC aus, mit dnSpy geschrieben)"
    "4f3fc90f99893dbbd0e592818eb40cc4f1c06ff0656e274105a850e7d1933764" = "GEPATCHT (EAC aus, an Ort und Stelle)"
}

function Get-Sha($bytes) {
    $h = [System.Security.Cryptography.SHA256]::Create()
    return ([BitConverter]::ToString($h.ComputeHash($bytes))).Replace("-", "").ToLower()
}

# Bytesuche ueber Latin-1: jedes Byte wird genau ein Zeichen, damit findet
# IndexOf das Muster in einer 3,7-MB-Datei sofort. Eine PowerShell-Schleife
# ueber 3,7 Millionen Bytes braucht dafuer Sekunden.
function Find-Muster($bytes, $muster) {
    $enc = [System.Text.Encoding]::GetEncoding(28591)
    $h = $enc.GetString($bytes)
    $n = $enc.GetString($muster)
    $treffer = @()
    $i = $h.IndexOf($n, [StringComparison]::Ordinal)
    while ($i -ge 0) {
        $treffer += $i
        $i = $h.IndexOf($n, $i + 1, [StringComparison]::Ordinal)
    }
    return ,$treffer
}

if ($NoEac) {
    Info "-NoEac: uebersprungen"
} elseif (-not (Test-Path $dll)) {
    Bad ("nicht gefunden: " + $dll)
} else {
    $bytes = [System.IO.File]::ReadAllBytes($dll)
    $sha = Get-Sha $bytes
    $zustand = $EAC_BEKANNT[$sha]
    $treffer = Find-Muster $bytes $EAC_ORIG

    if ($zustand -and $zustand.StartsWith("GEPATCHT")) {
        Gut $zustand
        Info ("sha256 " + $sha)
    } elseif ($treffer.Count -eq 1) {
        Info ("ORIGINAL (EAC an) - Rumpf bei 0x{0:X8}" -f $treffer[0])
        if ($Check) {
            Warn "EAC ist an. Das Plugin wuerde nicht laden."
        } else {
            New-Item -ItemType Directory -Force -Path $sicher | Out-Null
            $kopie = Join-Path $sicher ("Assembly-CSharp.original." + (Get-Date -Format "yyyyMMdd_HHmmss") + ".dll")
            Copy-Item $dll $kopie -Force
            Info ("gesichert -> backup_game\" + (Split-Path -Leaf $kopie))

            $fs = [System.IO.File]::Open($dll, "Open", "Write")
            try {
                $fs.Seek($treffer[0], "Begin") | Out-Null
                $fs.Write($EAC_PATCHED, 0, $EAC_PATCHED.Length)
            } finally { $fs.Close() }

            $nach = [System.IO.File]::ReadAllBytes($dll)
            if ((Find-Muster $nach $EAC_PATCHED).Count -eq 1) {
                Gut "gepatcht - EAC ist abgeschaltet"
                Info ("sha256 " + (Get-Sha $nach))
            } else {
                Bad "Patch hat nicht gegriffen. Sicherung liegt in backup_game\."
            }
        }
    } elseif ($treffer.Count -eq 0 -and (Find-Muster $bytes $EAC_PATCHED).Count -eq 1) {
        Gut "GEPATCHT (EAC aus)"
        Info ("sha256 " + $sha)
    } else {
        # Weder bekannter Stand noch eindeutiges Muster: das kann ein neuer
        # Spielbuild sein. Nichts anfassen - eine falsch gepatchte
        # Assembly-CSharp.dll kostet mehr Zeit als eine Rueckfrage.
        Bad ("Zustand unklar (" + $treffer.Count + " Treffer, sha256 " + $sha + ")")
        Info "Nichts geaendert. Auf diesem PC mit dem Werkzeug nachsehen:"
        Info "    python eacpatch.py status"
    }
}


# -------------------------------------------------------------- BepInEx

Schritt 5 "BepInEx"

# Ohne BepInEx laedt kein Plugin. Auf dem PC eines Mitspielers ist keins da,
# und "lad dir BepInEx herunter und entpack es richtig" ist genau der Schritt,
# an dem so etwas scheitert. Im Weitergabe-Paket liegt deshalb ein Ordner
# bepinex\ daneben, dessen Inhalt 1:1 in den Spielordner gehoert.
$bepQuelle = Join-Path $root "bepinex"

if (Test-Path $bepCore) {
    Gut "schon installiert"
} elseif (-not (Test-Path $bepQuelle)) {
    # Kein Abbruch: der Login am Masterserver funktioniert auch ohne Plugin.
    Warn "BepInEx fehlt, und es liegt keins bei (bepinex\)."
    Info "Der Login am Masterserver geht trotzdem. Fuer die Mod-Items:"
    Info "BepInEx 5.4.x fuer Unity Mono x64 in den Spielordner entpacken."
} elseif ($Check) {
    Warn "BepInEx fehlt - wuerde aus bepinex\ installiert"
} else {
    $n = 0
    foreach ($f in Get-ChildItem $bepQuelle -Recurse -File) {
        $rel = $f.FullName.Substring($bepQuelle.Length).TrimStart("\")
        $ziel = Join-Path $Game $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ziel) | Out-Null
        Copy-Item $f.FullName $ziel -Force
        $n++
    }
    if (Test-Path $bepCore) { Gut ("installiert, {0} Dateien" -f $n) }
    else { Bad "kopiert, aber BepInEx\core\BepInEx.dll fehlt weiterhin" }
}


# --------------------------------------------------------------- Plugin

Schritt 6 "Plugin und Assets"

$dllQuelle = $null
foreach ($k in @((Join-Path $root "build\NextDayRevivalToolkit.dll"),
                 (Join-Path $root "NextDayRevivalToolkit.dll"))) {
    if (Test-Path $k) { $dllQuelle = $k; break }
}
$assetQuelle = Join-Path $root "assets"
$t72Required = @("t72_hull.ndmesh", "t72_turret.ndmesh",
                 "t72_diffuse.png", "t72_normal.png", "t72_metal.png")

if ($NoPlugin) {
    Info "-NoPlugin: uebersprungen"
} elseif (-not (Test-Path $bepCore)) {
    Info "ohne BepInEx uebersprungen - siehe Schritt 5"
} elseif (-not $dllQuelle) {
    Warn "NextDayRevivalToolkit.dll nicht gefunden (weder build\ noch daneben)."
    Info "Auf diesem PC bauen:  powershell -File build.ps1"
} elseif ($Check) {
    $ziel = Join-Path $plugins "NextDayRevivalToolkit.dll"
    if (Test-Path $ziel) {
        $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $dllQuelle).Hash
        $targetHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ziel).Hash
        if ($sourceHash -eq $targetHash) {
            Gut ("DLL stimmt byteweise, {0} Bytes" -f (Get-Item $ziel).Length)
        } else {
            Bad "installierte DLL unterscheidet sich vom Installationspaket"
        }
    } else { Bad "Plugin-DLL ist nicht installiert" }

    if (Test-Path (Join-Path $root "t72_import.exe")) {
        Gut "T-72-Extraktor liegt bereit"
    } elseif ((Test-Path (Join-Path $root "t72_import.py")) -and
              (Get-Command python -ErrorAction SilentlyContinue)) {
        & python -c "import UnityPy" 2>$null
        if ($LASTEXITCODE -eq 0) {
            Gut "T-72-Entwicklungsskript, Python und UnityPy liegen bereit"
        } else {
            # Not Bad: the install path installs UnityPy itself now. It is
            # still worth naming, because that install needs the network.
            Warn "T-72-Entwicklungsskript: Python-Modul UnityPy fehlt - wird bei der Installation einmalig nachgeladen"
        }
    } else {
        Bad "T-72-Extraktor fehlt - dieses Paket kann nur das alte Modell installieren."
    }

    if (-not (Test-Path $assetQuelle)) {
        Bad ("assets\ fehlt: " + $assetQuelle)
    } else {
        $sourceT72Missing = @($t72Required | Where-Object {
            -not (Test-Path (Join-Path $assetQuelle $_))
        })
        if ($sourceT72Missing.Count -gt 0) {
            Bad ("T-72-Ausgabe im Paket noch nicht erzeugt ({0}): {1}" -f
                 $sourceT72Missing.Count, ($sourceT72Missing -join ", "))
        }
        $assetZiel = Join-Path $plugins "assets"
        $gleich = 0
        $fehlt = @()
        $anders = @()
        foreach ($f in Get-ChildItem $assetQuelle -File) {
            if ($f.Name -like "*_preview.png" -or $f.Name -eq "icon_vergleich.png") {
                continue
            }
            $installed = Join-Path $assetZiel $f.Name
            if (-not (Test-Path $installed)) {
                $fehlt += $f.Name
                continue
            }
            # This is written by the route recorder. Presence matters; equality
            # would incorrectly call a player's own route data stale.
            if ($f.Name -eq "ndr_routes.tsv") { $gleich++; continue }
            $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $f.FullName).Hash
            $targetHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installed).Hash
            if ($sourceHash -eq $targetHash) { $gleich++ }
            else { $anders += $f.Name }
        }
        if ($fehlt.Count -gt 0) {
            Bad ("installierte Assets fehlen ({0}): {1}" -f
                 $fehlt.Count, ($fehlt -join ", "))
        }
        if ($anders.Count -gt 0) {
            Bad ("installierte Assets sind veraltet oder anders ({0}): {1}" -f
                 $anders.Count, ($anders -join ", "))
        }
        if ($fehlt.Count -eq 0 -and $anders.Count -eq 0) {
            Gut ("{0} installierte Assets stimmen; Route-Datei nur auf Vorhandensein geprueft" -f $gleich)
        }

        if (Test-Path $pluginCfg) {
            $kopf = (Get-Content $pluginCfg -TotalCount 1 -ErrorAction SilentlyContinue)
            $m = [regex]::Match([string]$kopf, "v([0-9]+\.[0-9]+\.[0-9]+)")
            $cfgVersion = if ($m.Success) { $m.Groups[1].Value } else { "unknown" }
            # The header version is not the question - BepInEx relabels it
            # while keeping every old value, which is how sixteen settings from
            # an older build survived under a current header on the second
            # machine. So count the values themselves.
            $cfgDrift = @(Get-ConfigDriftNames $pluginCfg)
            if ($cfgDrift.Count -gt 0) {
                Bad ("{0} Einstellungen halten den Wert einer aelteren Fassung: {1}" -f
                     $cfgDrift.Count, (($cfgDrift | Select-Object -First 6) -join "; "))
                Info "    client_patch.ps1 -ResetConfig setzt sie zurueck (Sicherung wird angelegt)"
            } else {
                Gut "alle versionseigenen Einstellungen entsprechen dieser Fassung"
            }
            if ($sourceVersion -and $cfgVersion -eq $sourceVersion) {
                Gut ("Plugin-Konfiguration meldet " + $cfgVersion)
            } elseif ($sourceVersion) {
                Warn ("Plugin-Konfiguration ist von " + $cfgVersion +
                    ", Installationspaket ist " + $sourceVersion +
                    " - bei Installation wird sie gesichert und neu erzeugt")
            }
        }
    }
} else {
    if (-not (Test-Path $assetQuelle)) {
        Bad ("assets\ fehlt: " + $assetQuelle)
    } else {
        $t72Ok = $true
        $t72Exe = Join-Path $root "t72_import.exe"
        $t72Py = Join-Path $root "t72_import.py"
        if (Test-Path $t72Exe) {
            Info "Baue das aktuelle T-72-Modell aus der eigenen Spielinstallation ..."
            & $t72Exe --game-data $dataDir --assets $assetQuelle
            if ($LASTEXITCODE -ne 0) {
                Bad ("T-72-Extraktor fehlgeschlagen (Code {0})." -f $LASTEXITCODE)
                $t72Ok = $false
            } else {
                Gut "aktuelles T-72-Modell lokal erzeugt"
            }
        } elseif ((Test-Path $t72Py) -and (Get-Command python -ErrorAction SilentlyContinue)) {
            # Development checkout only. Published packages carry the frozen
            # executable and never require Python on the player's computer.
            #
            # t72_import.py needs UnityPy, and nothing else in this repository
            # does - so a machine that has numpy and pillow can still fail on
            # that single import, take the whole asset copy down with it and
            # leave the client on old art. Install it once instead of aborting.
            # This is exactly what happened on the second machine on
            # 2026-08-31: ModuleNotFoundError: UnityPy, 15 assets left stale.
            & python -c "import UnityPy" 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Info "UnityPy fehlt - wird einmalig nachinstalliert ..."
                & python -m pip install --quiet UnityPy
                & python -c "import UnityPy" 2>$null | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    Bad "UnityPy laesst sich nicht installieren - der T-72 kann nicht aus dem Spiel gebaut werden."
                }
            }
            Info "Baue das aktuelle T-72-Modell mit dem Entwicklungsskript ..."
            & python $t72Py --game-data $dataDir --assets $assetQuelle
            if ($LASTEXITCODE -ne 0) {
                Bad ("T-72-Entwicklungsskript fehlgeschlagen (Code {0})." -f $LASTEXITCODE)
                $t72Ok = $false
            } else {
                Gut "aktuelles T-72-Modell lokal erzeugt"
            }
        } else {
            Bad "T-72-Extraktor fehlt - Abbruch statt Installation des alten Modells."
            $t72Ok = $false
        }

        foreach ($required in $t72Required) {
            if (-not (Test-Path (Join-Path $assetQuelle $required))) {
                Bad ("T-72-Ausgabe fehlt: assets\" + $required)
                $t72Ok = $false
            }
        }

        if (-not $t72Ok) {
            Bad "Plugin und Assets werden nicht kopiert; der bisherige Client bleibt zusammen."
        } else {
        New-Item -ItemType Directory -Force -Path $plugins | Out-Null
        $assetZiel = Join-Path $plugins "assets"
        New-Item -ItemType Directory -Force -Path $assetZiel | Out-Null

        # build.ps1 fuehrt eine Positivliste, weil dort ein fehlendes Asset ein
        # Baufehler ist. Hier ist die Ausschlussliste richtig: es wird nichts
        # gebaut, und ein neues Asset soll ohne zweite Pflegestelle mitkommen.
        # Ausgeschlossen sind nur die Vorschaubilder der Generatoren.
        $aus = @("*_preview.png", "icon_vergleich.png")
        $n = 0
        foreach ($f in Get-ChildItem $assetQuelle -File) {
            $skip = $false
            foreach ($p in $aus) { if ($f.Name -like $p) { $skip = $true } }
            if ($skip) { continue }

            $assetAusgabe = Join-Path $assetZiel $f.Name
            # The in-game recorder writes this file. An update must never
            # replace locally recorded routes with the package's starter file.
            # build.ps1 follows the same rule.
            if ($f.Name -eq "ndr_routes.tsv" -and (Test-Path $assetAusgabe)) {
                Info "Route-Datei behalten: BepInEx\plugins\assets\ndr_routes.tsv"
                continue
            }
            Copy-Item $f.FullName $assetAusgabe -Force
            $n++
        }
        Gut ("{0} Assets kopiert -> BepInEx\plugins\assets" -f $n)

        # Nicht loeschen, nur melden: was hier liegt und nicht mehr erzeugt wird,
        # stiftet spaeter bei der Fehlersuche Verwirrung.
        foreach ($f in Get-ChildItem $assetZiel -File) {
            if (-not (Test-Path (Join-Path $assetQuelle $f.Name))) {
                Warn ("altes Asset liegt noch da: " + $f.Name)
            }
        }

        # Config.Bind keeps every old value, even when a later plugin changes
        # its default. Back up an older config only after extraction and asset
        # copying succeeded, immediately before the new DLL becomes active.
        # BepInEx creates a fresh file on the next start; the player's previous
        # choices remain readable in the backup.
        if ($sourceVersion -and (Test-Path $pluginCfg)) {
            $kopf = (Get-Content $pluginCfg -TotalCount 1 -ErrorAction SilentlyContinue)
            $m = [regex]::Match([string]$kopf, "v([0-9]+\.[0-9]+\.[0-9]+)")
            $cfgVersion = if ($m.Success) { $m.Groups[1].Value } else { "unknown" }
            if ($cfgVersion -ne $sourceVersion) {
                $cfgDir = Split-Path -Parent $pluginCfg
                $backupCfg = Join-Path $cfgDir ("nextday.revival.toolkit.cfg.bak_" + $cfgVersion)
                if (Test-Path $backupCfg) {
                    $backupCfg = Join-Path $cfgDir ("nextday.revival.toolkit.cfg.bak_" +
                        $cfgVersion + "_" + (Get-Date -Format "yyyyMMdd_HHmmss"))
                }
                Move-Item $pluginCfg $backupCfg -Force
                Gut ("alte Plugin-Konfiguration gesichert: " + (Split-Path -Leaf $backupCfg))
            }
        }

        # Commit the DLL last. If extraction or asset validation fails, the old
        # DLL remains beside the old assets instead of claiming a new version
        # over an old art set.
        Copy-Item $dllQuelle (Join-Path $plugins "NextDayRevivalToolkit.dll") -Force
        Gut ("NextDayRevivalToolkit.dll zuletzt kopiert, {0} Bytes" -f
             (Get-Item $dllQuelle).Length)

        # Only when asked. An ordinary install leaves a settings file that
        # survived the version check alone; the launcher's Repair asks for this
        # once it has measured that the file holds an older version's values.
        if ($ResetConfig) {
            Info "Setze die Plugin-Konfiguration auf die Werte dieser Fassung zurueck ..."
            Reset-PluginConfig $pluginCfg
        }
        }
    }
}


# ---------------------------------------------------------------- Probe

Schritt 7 "Server erreichbar"

# Die einzige Abnahme, die ohne Spielstart etwas aussagt: liefert der VPS die
# Serverliste, und steht darin dieselbe Adresse, auf die der Client nun zeigt?
try {
    $r = Invoke-WebRequest -Uri $listUrl -UseBasicParsing -TimeoutSec 10
    $json = $r.Content
    $mHost = [regex]::Match($json, '"masterServerHost"\s*:\s*"([^"]+)"')
    $mPort = [regex]::Match($json, '"masterServerPort"\s*:\s*(\d+)')
    $mApp  = [regex]::Match($json, '"photonAppId"\s*:\s*"([^"]+)"')
    $mName = [regex]::Match($json, '"name"\s*:\s*"([^"]+)"')

    Gut ("HTTP {0}, {1} Bytes" -f [int]$r.StatusCode, $json.Length)
    if ($mName.Success) { Info ("Zeile:   " + $mName.Groups[1].Value) }
    if ($mHost.Success) { Info ("Master:  " + $mHost.Groups[1].Value + ":" + $mPort.Groups[1].Value) }
    if ($mApp.Success)  { Info ("Photon:  " + $mApp.Groups[1].Value) }

    if ($mHost.Success -and $mHost.Groups[1].Value -ne $Server) {
        # Der Client verbindet sich auf die Adresse AUS DER JSON, nicht auf die
        # aus ClientConfig.ini. Stimmen sie nicht ueberein, laeuft die Anmeldung
        # woanders hin als erwartet.
        Warn ("Serverliste nennt " + $mHost.Groups[1].Value + ", gepatcht wurde auf " + $Server + ".")
        Info "Der Serverbetreiber muss das richtigstellen - sag ihm Bescheid."
    }
    if ($mApp.Success -and $mApp.Groups[1].Value.Length -lt 8) {
        Warn "photonAppId sieht leer aus - ohne sie kommt kein Spieler in einen Raum."
    }
} catch {
    # Diese Hinweise liest ein SPIELER. Hier standen bis 0.4.5 Handgriffe fuer
    # den Serverbetreiber - ein ssh-Aufruf mit Hostnamen und Dienstnamen, dazu
    # eine E-Nummer aus der internen Doku. Das half niemandem, der es zu sehen
    # bekam, und trug Betriebsinterna in jedes ausgelieferte Paket.
    Bad ("nicht erreichbar: " + $listUrl)
    Info ($_.Exception.Message)
    Info "Der Server ist gerade nicht erreichbar. Das liegt nicht an deinem"
    Info "Spiel - sag dem Bescheid, von dem du dieses Paket hast."
}


# -------------------------------------------------------------- Ergebnis

Write-Host ""
Write-Host ("-" * 62)
if ($fehler.Count -gt 0) {
    Write-Host ("{0} Fehler:" -f $fehler.Count) -ForegroundColor Red
    foreach ($f in $fehler) { Write-Host ("  - " + $f) }
    Write-Host ""
    exit 1
}
if ($Check) {
    Write-Host "Nur nachgesehen, nichts geaendert." -ForegroundColor White
} else {
    Write-Host "Client steht. Jetzt starten mit:" -ForegroundColor Green
    Write-Host "    powershell -File start_game.ps1"
    Write-Host ""
    Write-Host "Nicht ueber den Steam-Play-Knopf - der startet den EAC-Launcher,"
    Write-Host "und der bricht seit einem Update im August 2026 ab."
}
if ($hinweise.Count -gt 0) {
    Write-Host ""
    Write-Host ("{0} Hinweis(e) oben beachten." -f $hinweise.Count) -ForegroundColor Yellow
}
Write-Host ""
exit 0
