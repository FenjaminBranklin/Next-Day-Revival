# Stufe 3 - Client-Patch: macht aus einer Steam-Installation einen Client,
# der sich am eigenen Masterserver anmeldet.
#
# Vier Dinge, ein Aufruf:
#   1. ClientConfig.ini zeigt auf die Serverliste des VPS
#   2. Assembly-CSharp.dll wird gepatcht, damit EAC im Spielcode aus ist
#   3. BepInEx wird installiert, falls keins da ist und eins beiliegt
#   4. Plugin und Assets liegen in BepInEx\plugins
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
    [switch]$NoEac
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
$sicher   = Join-Path $root "backup_game"

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
        $a = (Get-Item $dllQuelle).Length
        $b = (Get-Item $ziel).Length
        if ($a -eq $b) { Gut ("installiert, {0} Bytes" -f $b) }
        else { Warn ("installiert, aber andere Groesse: {0} statt {1} Bytes" -f $b, $a) }
    } else { Warn "nicht installiert" }
} else {
    New-Item -ItemType Directory -Force -Path $plugins | Out-Null
    Copy-Item $dllQuelle (Join-Path $plugins "NextDayRevivalToolkit.dll") -Force
    Gut ("NextDayRevivalToolkit.dll  {0} Bytes" -f (Get-Item $dllQuelle).Length)

    if (-not (Test-Path $assetQuelle)) {
        Bad ("assets\ fehlt: " + $assetQuelle)
    } else {
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
            Copy-Item $f.FullName (Join-Path $assetZiel $f.Name) -Force
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
