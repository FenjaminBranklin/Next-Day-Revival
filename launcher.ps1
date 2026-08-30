# Next Day: Revival - Launcher
#
# One window instead of two batch files. It answers three questions that are
# NOT the same question, shows them side by side, and lets you switch between
# published versions:
#
#   INSTALLED   which build sits in BepInEx\plugins right now
#   SERVER      what the master server serves and what it expects
#   LATEST      the newest release on GitHub
#
# Five things you can do: install or switch a version, repair the
# installation, check it, and play.
#
# Two of those are younger than the rest:
#
#   ONLINE      how many people are on the master server right now, in the
#               biggest type in the window. The number comes from
#               revival.json ("playersOnline"), which is our own file - the
#               game's server list carries a hard-coded 0 and always did, so
#               a launcher that read THAT would print a confident lie. A
#               server that does not carry the field shows "-", not "0".
#   VANILLA     start the game exactly as SOFF Games shipped it, but pointed
#               at our master server. Same EAC patch, same ClientConfig.ini,
#               no plugin: BepInEx is switched off for that one start through
#               the DOORSTOP_DISABLE environment variable. Nothing on disk
#               changes, so the next normal start is modded again.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -STA -File launcher.ps1
#   ... -Console          no window: print the state and exit (the brain alone)
#   ... -Install 0.5.0    switch to that version without a window
#   ... -Game <path>      point at a game folder instead of searching for it
#   ... -RevivalUrl <url> another master server (a mock, for instance)
#   ... -ServerHost <ip>  start with this master server selected. Without it
#                         the launcher takes the one the game already points
#                         at, out of ClientConfig.ini - see below.
#   ... -PlayerName <n>   player name for ClientConfig.ini
#   ... -Vanilla          start the game without the plugin and exit
#
# The master server is picked in the window and it is passed to
# client_patch.ps1 on every install and repair. That is not a convenience:
# called without an address, client_patch.ps1 writes its OWN default into
# ClientConfig.ini, so a repair silently moved a machine off its local
# 127.0.0.1 test server and onto the VPS. The launcher therefore starts from
# what the game currently points at and never changes it behind your back.
#
# Rules this file obeys (docs/LAUNCHER.md in the public repo):
#   - client_patch.ps1 and start_game.ps1 are never modified, only called.
#   - Nothing here blocks Play. Every check is advisory, every network call
#     has a timeout, and a dead server costs two seconds.
#   - No self-update. Only the payload - plugin and assets - is versioned.
#   - Never ship game code: a downloaded package that carries
#     Assembly-CSharp.dll is refused, not unpacked.

param(
    [switch]$Console,
    [string]$Game = "",
    [string]$RevivalUrl = "",
    [string]$Repo = "FenjaminBranklin/Next-Day-Revival",
    [string]$PlayerName = "",
    [string]$ServerHost = "",
    [string]$Install = "",
    [switch]$Vanilla
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $root) { $root = (Get-Location).Path }

$DEFAULT_HOST = "187.124.117.145"
$LOCAL_HOST   = "127.0.0.1"
$PORT         = 12080

# -RevivalUrl pins the address; without it the URL follows whichever master
# server is selected in the window.
$script:RevivalFixed = [bool]$RevivalUrl
$script:RevivalUrl   = $RevivalUrl
$script:ServerChoice = ""

$VersionsDir = Join-Path $root "versions"
$UA          = "NextDayRevivalLauncher"
$SERVER_MS   = 2000     # the brief says two seconds, then move on
$GITHUB_MS   = 6000

# GitHub speaks TLS 1.2 and up. Windows PowerShell 5.1 still defaults to older
# protocols on some machines, and that failure reads as "connection closed".
try {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch {}

$script:Gui      = $false
$script:LogBox   = $null
$script:Progress = $null


# ============================================================= small helpers

function Say($text, $kind) {
    if (-not $kind) { $kind = "plain" }
    if ($script:Gui -and $script:LogBox) {
        # Mixed for a dark log pane. The older colours were chosen against
        # white and are close to unreadable on this background.
        $c = [System.Drawing.Color]::FromArgb(214, 212, 206)
        if ($kind -eq "ok")   { $c = [System.Drawing.Color]::FromArgb(110, 200, 130) }
        if ($kind -eq "warn") { $c = [System.Drawing.Color]::FromArgb(224, 168, 68) }
        if ($kind -eq "bad")  { $c = [System.Drawing.Color]::FromArgb(228, 98, 88) }
        if ($kind -eq "dim")  { $c = [System.Drawing.Color]::FromArgb(124, 130, 138) }
        $script:LogBox.SelectionStart  = $script:LogBox.TextLength
        $script:LogBox.SelectionLength = 0
        $script:LogBox.SelectionColor  = $c
        $script:LogBox.AppendText($text + "`r`n")
        $script:LogBox.ScrollToCaret()
        Pump
    } else {
        if     ($kind -eq "ok")   { Write-Host $text -ForegroundColor Green }
        elseif ($kind -eq "warn") { Write-Host $text -ForegroundColor Yellow }
        elseif ($kind -eq "bad")  { Write-Host $text -ForegroundColor Red }
        elseif ($kind -eq "dim")  { Write-Host $text -ForegroundColor DarkGray }
        else                      { Write-Host $text }
    }
}

# Keeps the window alive while a child process or a download runs. Without it
# the window greys out and Windows offers to kill the launcher.
function Pump {
    if ($script:Gui) { [System.Windows.Forms.Application]::DoEvents() }
}

function Set-Progress($pct) {
    if ($script:Gui -and $script:Progress) {
        if ($pct -lt 0) { $pct = 0 }
        if ($pct -gt 100) { $pct = 100 }
        $script:Progress.Value = $pct
        Pump
    }
}

# "0.5.3" against "0.10.0" - never compare version numbers as text. Returns
# -1/0/1, and 0 for anything unparseable rather than throwing at the user.
function Compare-Ver($a, $b) {
    if (-not $a -or -not $b) { return 0 }
    try {
        $x = [version]($a.Trim())
        $y = [version]($b.Trim())
        return $x.CompareTo($y)
    } catch { return 0 }
}

# The repository normalises line endings, so VERSION read from disk can carry
# a trailing \r - and "0.5.3" -ne "0.5.3\r" compares two numbers that look
# identical on screen. Same trim handles the "v" of a git tag.
function Clean-Ver($s) {
    if (-not $s) { return "" }
    return ($s -replace "[^0-9\.]", "")
}


# ============================================================ finding things

# Same search as client_patch.ps1 and start_game.ps1. Duplicated on purpose:
# those two are not to be touched, and thirty lines are cheaper than a shared
# module that would then have to ship with every package.
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

# Is EAC switched off inside the game code? Read only - the launcher never
# patches anything itself. It asks because the answer decides whether ANY
# start reaches the server, the vanilla one included.
#
# ClientOptions::IsDisabledEAC in its original form is eight bytes, and the
# field token in the middle makes them unique in the file:
#
#   1E 02 7B 0C 27 00 04 2A   tiny header, ldarg.0, ldfld _isDisabledEAC, ret
#
# A patched method comes in two shapes, because two tools wrote it. In place,
# with the original header kept and the rest padded away:
#
#   1E 17 2A 00 00 00 00 00   ldc.i4.1, ret, padding
#
# and dnSpy's, which shortens the method and rewrites the header with it:
#
#   0A 17 2A                  header for a two byte body, ldc.i4.1, ret
#
# Three bytes are short enough to turn up by chance in a file of this size, so
# the original is looked for FIRST. If it is there, EAC is on and nothing else
# is asked; only then does the short pattern get to mean anything.
function Test-EacPatched($game) {
    if (-not $game) { return "unknown" }
    $dll = Join-Path $game "nextday_game_Data\Managed\Assembly-CSharp.dll"
    if (-not (Test-Path $dll)) { return "unknown" }
    try {
        # Latin-1 maps every byte to exactly one char, so IndexOf over the
        # decoded string is a byte search - and a fast one, which matters on a
        # DLL of this size. Note the "" in front of every pattern: two chars
        # added together in PowerShell are two numbers, not a string.
        $text = [System.Text.Encoding]::GetEncoding(28591).GetString(
                    [System.IO.File]::ReadAllBytes($dll))
        $orig = "" + [char]0x1E + [char]0x02 + [char]0x7B + [char]0x0C +
                     [char]0x27 + [char]0x00 + [char]0x04 + [char]0x2A
        if ($text.IndexOf($orig) -ge 0) { return "on" }
        $inPlace = "" + [char]0x1E + [char]0x17 + [char]0x2A + [char]0x00 +
                        [char]0x00 + [char]0x00 + [char]0x00 + [char]0x00
        $dnSpy   = "" + [char]0x0A + [char]0x17 + [char]0x2A
        if ($text.IndexOf($inPlace) -ge 0) { return "off" }
        if ($text.IndexOf($dnSpy)   -ge 0) { return "off" }
    } catch {}
    return "unknown"
}

function Test-GameRunning {
    $p = @(Get-Process -Name "nextday_game" -ErrorAction SilentlyContinue)
    return ($p.Count -gt 0)
}


# =================================================== which version is that DLL

# The installed version is read out of the plugin itself, not out of a marker
# file the launcher wrote - a marker lies the moment somebody copies a DLL by
# hand, and every installation made before this launcher existed has none.
#
# [BepInPlugin(GUID, NAME, VERSION)] lands in the metadata as three
# length-prefixed UTF-8 strings in a row:
#
#     17 "nextday.revival.toolkit"  18 "Next Day Revival Toolkit"  05 "0.5.3"
#
# so the version is the first version-shaped string after the GUID.
function Read-PluginVersion($dll) {
    if (-not $dll -or -not (Test-Path $dll)) { return "" }
    try {
        $bytes = [System.IO.File]::ReadAllBytes($dll)
        $text  = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes)
        $m = [regex]::Match($text, 'nextday\.revival\.toolkit.{0,80}?(\d+\.\d+\.\d+)',
                            [System.Text.RegularExpressions.RegexOptions]::Singleline)
        if ($m.Success) { return $m.Groups[1].Value }
    } catch {}
    return ""
}

# A folder that could install a version: the launcher's own folder, or one of
# the unpacked packages under versions\.
function Read-FolderVersion($folder) {
    if (-not $folder -or -not (Test-Path $folder)) { return "" }
    $vf = Join-Path $folder "VERSION"
    if (Test-Path $vf) {
        $v = Clean-Ver (Get-Content $vf -Raw -ErrorAction SilentlyContinue)
        if ($v) { return $v }
    }
    foreach ($k in @("build\NextDayRevivalToolkit.dll", "NextDayRevivalToolkit.dll")) {
        $p = Join-Path $folder $k
        if (Test-Path $p) {
            $v = Read-PluginVersion $p
            if ($v) { return $v }
        }
    }
    return ""
}

# Can this folder actually install? It needs the patch script, a plugin and the
# assets. Anything less runs client_patch.ps1 into a warning instead of an
# installation.
function Test-SourceFolder($folder) {
    if (-not $folder -or -not (Test-Path $folder)) { return $false }
    if (-not (Test-Path (Join-Path $folder "client_patch.ps1"))) { return $false }
    if (-not (Test-Path (Join-Path $folder "assets"))) { return $false }
    foreach ($k in @("build\NextDayRevivalToolkit.dll", "NextDayRevivalToolkit.dll")) {
        if (Test-Path (Join-Path $folder $k)) { return $true }
    }
    return $false
}


# =============================================================== the network

function Get-RevivalUrlFor($h) { return "http://" + $h + ":" + $PORT + "/revival.json" }
function Get-ListUrlFor($h)    { return "http://" + $h + ":" + $PORT + "/servers_report" }

# Which master server does the GAME point at? That is not the launcher's
# choice, it is one line in ClientConfig.ini, and it is the line that decides
# whether the server list in the main menu is empty.
function Read-GameServerHost($game) {
    if (-not $game) { return "" }
    $cfg = Join-Path $game "nextday_game_Data\ClientConfig.ini"
    if (-not (Test-Path $cfg)) { return "" }
    try {
        $txt = Get-Content $cfg -Raw -ErrorAction SilentlyContinue
        $m = [regex]::Match($txt, '"ServersListURL"\s*:\s*"https?://([^:/"]+)')
        if ($m.Success) { return $m.Groups[1].Value }
    } catch {}
    return ""
}

# The question the player actually has: can the game reach its server list?
# Separate from revival.json on purpose - the local test server answers a
# plain "OK" on every other path, so a failing version file says nothing
# about whether that server is up.
function Test-ServerList($h) {
    $out = @{ ok = $false; error = ""; bytes = 0; seats = 0 }
    try {
        $req = [Net.HttpWebRequest]::Create((Get-ListUrlFor $h))
        $req.Timeout = $SERVER_MS
        $req.ReadWriteTimeout = $SERVER_MS
        $req.UserAgent = $UA
        $resp = $req.GetResponse()
        $sr   = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $raw  = $sr.ReadToEnd()
        $sr.Close(); $resp.Close()
        $out.ok = $true
        $out.bytes = $raw.Length
        # How many seats the server advertises. Both rows in the list are the
        # same server twice - normal and easy-connection - so this is the
        # largest number, never the sum.
        foreach ($m in [regex]::Matches($raw, '"maxClients"\s*:\s*(\d+)')) {
            $n = [int]$m.Groups[1].Value
            if ($n -gt $out.seats) { $out.seats = $n }
        }
    } catch {
        $out.error = $_.Exception.Message
    }
    return $out
}

function Get-ServerInfo($url) {
    # players = -1 means "this server does not report it", which is not the
    # same statement as "nobody is online" and must not be shown as 0.
    $out = @{ ok = $false; error = ""; contentVersion = ""; minClientVersion = "";
              downloadUrl = ""; message = ""; weapons = @(); players = -1; seats = 0 }
    try {
        $req = [Net.HttpWebRequest]::Create($url)
        $req.Timeout = $SERVER_MS
        $req.ReadWriteTimeout = $SERVER_MS
        $req.UserAgent = $UA
        $resp = $req.GetResponse()
        $sr   = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $raw  = $sr.ReadToEnd()
        $sr.Close(); $resp.Close()
        $j = $raw | ConvertFrom-Json
        $out.ok               = $true
        $out.contentVersion   = [string]$j.contentVersion
        $out.minClientVersion = [string]$j.minClientVersion
        $out.downloadUrl      = [string]$j.downloadUrl
        $out.message          = [string]$j.message
        $w = @()
        foreach ($e in $j.modWeapons) { $w += @{ id = [int]$e.id; clip = [int]$e.clip } }
        $out.weapons = $w
        # Asked for by name, not read blindly: an older server has neither
        # field, and ConvertFrom-Json turns a missing property into $null,
        # which [int] would happily make a 0 out of.
        foreach ($prop in $j.PSObject.Properties) {
            if ($prop.Name -eq "playersOnline") { try { $out.players = [int]$prop.Value } catch {} }
            if ($prop.Name -eq "playersMax")    { try { $out.seats   = [int]$prop.Value } catch {} }
        }
    } catch {
        $out.error = $_.Exception.Message
    }
    return $out
}

function Get-Releases {
    $out = @{ ok = $false; error = ""; list = @() }
    try {
        $req = [Net.HttpWebRequest]::Create("https://api.github.com/repos/$Repo/releases?per_page=40")
        $req.Timeout = $GITHUB_MS
        $req.ReadWriteTimeout = $GITHUB_MS
        $req.UserAgent = $UA
        $req.Accept = "application/vnd.github+json"
        $resp = $req.GetResponse()
        $sr   = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $raw  = $sr.ReadToEnd()
        $sr.Close(); $resp.Close()
        $list = @()
        foreach ($r in ($raw | ConvertFrom-Json)) {
            if ($r.draft) { continue }
            $zip = $null
            foreach ($a in $r.assets) { if ($a.name -like "*.zip") { $zip = $a; break } }
            if (-not $zip) { continue }
            $list += @{
                version = Clean-Ver ([string]$r.tag_name)
                tag     = [string]$r.tag_name
                url     = [string]$zip.browser_download_url
                size    = [int64]$zip.size
                name    = [string]$zip.name
                date    = [string]$r.published_at
                pre     = [bool]$r.prerelease
            }
        }
        $out.list = $list
        $out.ok   = $true
    } catch {
        $out.error = $_.Exception.Message
    }
    return $out
}


# ================================================================= the brain

function Get-State {
    $s = @{}
    $s.game = $Game
    if (-not $s.game) { $s.game = Get-NextDayPath }

    $s.pluginDll = ""
    if ($s.game) { $s.pluginDll = Join-Path $s.game "BepInEx\plugins\NextDayRevivalToolkit.dll" }
    $s.installed = Read-PluginVersion $s.pluginDll
    $s.bepinex   = $false
    if ($s.game) { $s.bepinex = Test-Path (Join-Path $s.game "BepInEx\core\BepInEx.dll") }

    $s.package   = Read-FolderVersion $root
    $s.packageOk = Test-SourceFolder $root

    # Which master server: whatever the game already points at wins, then
    # -ServerHost, then the built-in one. Decided once; after that the window
    # owns the choice.
    $s.configHost = Read-GameServerHost $s.game
    if (-not $script:ServerChoice) {
        if     ($ServerHost)    { $script:ServerChoice = $ServerHost }
        elseif ($s.configHost)  { $script:ServerChoice = $s.configHost }
        else                    { $script:ServerChoice = $DEFAULT_HOST }
    }
    $s.serverHost = $script:ServerChoice
    if (-not $script:RevivalFixed) { $script:RevivalUrl = Get-RevivalUrlFor $s.serverHost }

    $s.eac      = Test-EacPatched $s.game
    $s.list     = Test-ServerList $s.serverHost
    $s.server   = Get-ServerInfo $script:RevivalUrl

    # The number in the biggest type in the window. -1 is "not reported",
    # and the window prints that as a dash - see the note at the top.
    $s.players = -1
    if ($s.server.ok) { $s.players = $s.server.players }
    $s.seats = $s.server.seats
    if ($s.seats -le 0) { $s.seats = $s.list.seats }
    $s.releases = Get-Releases
    $s.latest   = ""
    foreach ($r in $s.releases.list) {
        if (-not $s.latest -or (Compare-Ver $r.version $s.latest) -gt 0) { $s.latest = $r.version }
    }

    $s.cached = @()
    if (Test-Path $VersionsDir) {
        foreach ($d in (Get-ChildItem $VersionsDir -Directory -ErrorAction SilentlyContinue)) {
            if (Test-SourceFolder $d.FullName) { $s.cached += $d.Name }
        }
    }

    # ---- the verdict. Four states, and the version strings decide only what
    # they are entitled to decide.
    #
    # contentVersion is not one of them: it describes the server's item
    # database and only moves when somebody deploys a new weapons_db.xml.
    # Client 0.4.5 against content 0.4.3 was correct and in sync. A launcher
    # that compares those two strings shouts at every player about a problem
    # that does not exist, and after the second false alarm nobody reads its
    # warnings again. minClientVersion is the field that carries a demand, so
    # that is the field the verdict uses.
    if (-not $s.game) {
        $s.state   = "nogame"
        $s.verdict = "Next Day: Survival was not found on this PC."
        $s.detail  = "Install the game through Steam and press Refresh, or start the launcher with -Game <path> if it lives somewhere unusual."
    } elseif (-not $s.list.ok) {
        # The server list, not the version file: this is the one the game
        # itself asks for, and an empty server list in the main menu is what
        # the player sees when it fails.
        $s.state   = "offline"
        $s.verdict = "Master server " + $s.serverHost + " is not answering - you can still play."
        $s.detail  = "No answer from " + (Get-ListUrlFor $s.serverHost) + " within two seconds, and that is the address the game asks for its server list."
        if ($s.serverHost -eq $LOCAL_HOST -or $s.serverHost -eq "localhost") {
            $s.detail += " Nothing is listening here, so the local master server is simply not running: start its script in NextDaySurvival_Stage64_PhotonOwnAppId\ without parameters, or pick the VPS above."
        }
    } elseif (-not $s.server.ok) {
        # Up, but no revival.json - the local test server answers "OK" on
        # every other path. Nothing is wrong; there is just nothing to
        # compare against.
        $s.state   = "noinfo"
        $s.verdict = "Master server " + $s.serverHost + " is up. It does not report a version."
        $s.detail  = "The server list answers, so the game can log in. " + (Get-RevivalUrlFor $s.serverHost) + " gave nothing back, so there is no version to compare against - that route only exists on the VPS."
    } elseif (-not $s.installed) {
        $s.state   = "missing"
        $s.verdict = "No Revival plugin installed yet."
        $s.detail  = "Pick a version below and press Install. That also installs BepInEx and switches EAC off inside the game code."
    } elseif ($s.server.minClientVersion -and (Compare-Ver $s.installed $s.server.minClientVersion) -lt 0) {
        $s.state   = "old"
        $s.verdict = "Your client is older than the server expects."
        $s.detail  = "Installed $($s.installed), the server asks for $($s.server.minClientVersion) or newer. Items the server knows and your build does not are simply missing for you. Install $($s.server.minClientVersion) or later below."
    } elseif ($s.server.minClientVersion -and (Compare-Ver $s.installed $s.server.minClientVersion) -gt 0) {
        # Deliberately not a warning. Being ahead of minClientVersion is the
        # normal state of a development machine, and a launcher that shouts
        # about it teaches everybody to ignore the line where it matters.
        $s.state   = "ahead"
        $s.verdict = "Newer than the server requires. Ready to play."
        $s.detail  = "Installed $($s.installed), the server asks for $($s.server.minClientVersion) and serves content $($s.server.contentVersion). That gap is only a problem when your build registers an item id the server's weapons_db.xml does not have - then that item turns back into its donor weapon, for everybody. The weapon check in the log below is what decides it."
    } else {
        $s.state   = "sync"
        $s.verdict = "In sync. Ready to play."
        $s.detail  = "Client $($s.installed), server content $($s.server.contentVersion), server asks for $($s.server.minClientVersion) or newer."
    }

    # Selected here is not the same as written into the game. Say which is
    # which, because the game reads only ClientConfig.ini.
    $s.pointNote = ""
    if (-not $s.configHost) {
        $s.pointNote = "The game does not point anywhere yet - Install or Repair writes the address."
    } elseif ($s.configHost -ne $s.serverHost) {
        $s.pointNote = "The game still points at " + $s.configHost + ". Press Repair to move it to " + $s.serverHost + "."
    } else {
        $s.pointNote = "The game points at " + $s.configHost + " - the same one."
    }
    return $s
}

# The full weapon check, the only one that can actually prove the "client is
# ahead" case. It needs the source, so it runs on a development machine and
# nowhere else: serversync.py parses RevivalPlugin.cs and compares it against
# the server's modWeapons. Do not re-derive that rule here - call the file
# that owns it and use its exit code.
function Invoke-WeaponCheck($s) {
    $py  = Join-Path $root "serversync.py"
    $src = Join-Path $root "RevivalPlugin.cs"
    if (-not (Test-Path $py) -or -not (Test-Path $src)) {
        Say "Weapon check: skipped - serversync.py and RevivalPlugin.cs ship with the source, not with the package." "dim"
        if ($s.server.ok) {
            $ids = ($s.server.weapons | ForEach-Object { $_.id }) -join ", "
            if (-not $ids) { $ids = "none" }
            Say ("Server knows these mod weapon ids: " + $ids) "dim"
        }
        return
    }
    Say ("Weapon check: python serversync.py --url " + $script:RevivalUrl) "dim"
    $code = Start-Child "python" @("serversync.py", "--url", $script:RevivalUrl) $root
    if     ($code -eq 0) { Say "Weapon check: the server knows every weapon this source registers." "ok" }
    elseif ($code -eq 1) { Say "Weapon check: MISMATCH - see the lines above. Those items turn into their donor weapon in the game." "bad" }
    elseif ($code -eq 2) { Say "Weapon check: the server did not answer." "warn" }
    else                 { Say ("Weapon check: serversync.py could not run (exit " + $code + "). Is python on PATH?") "warn" }
}


# ===================================================== running child processes

# client_patch.ps1 and start_game.ps1 are called, never edited. Their output is
# streamed into the log pane, because when something goes wrong that log is the
# only thing worth sending back.
function Start-Child($exe, $argList, $workDir) {
    $outFile = [System.IO.Path]::GetTempFileName()
    $errFile = [System.IO.Path]::GetTempFileName()
    $code = -1

    # Why cmd.exe and not Start-Process -PassThru: that cmdlet hands back a
    # process object whose ExitCode is $null here, even after WaitForExit -
    # so every run looked like a failure. A Process we create ourselves
    # reports the code, and cmd's own redirection gives the two log files
    # that the pump loop tails while the child is still running.
    $cmd = '""' + $exe + '"'
    foreach ($a in $argList) { $cmd += ' "' + $a + '"' }
    $cmd += ' >"' + $outFile + '" 2>"' + $errFile + '""'

    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName        = "cmd.exe"
        $psi.Arguments       = "/c " + $cmd
        $psi.WorkingDirectory = $workDir
        $psi.UseShellExecute  = $false
        $psi.CreateNoWindow   = $true
        $p = [System.Diagnostics.Process]::Start($psi)

        $seen = 0
        while (-not $p.HasExited) {
            $seen = Show-NewLines $outFile $seen
            Pump
            Start-Sleep -Milliseconds 120
        }
        $p.WaitForExit()
        Start-Sleep -Milliseconds 150
        $seen = Show-NewLines $outFile $seen
        $err = Get-Content $errFile -Raw -ErrorAction SilentlyContinue
        if ($err -and $err.Trim()) { Say $err.Trim() "bad" }
        $code = $p.ExitCode
        if ($null -eq $code) { $code = -1 }
    } catch {
        Say ("Could not start " + $exe + ": " + $_.Exception.Message) "bad"
    } finally {
        Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue
    }
    return $code
}

function Show-NewLines($file, $seen) {
    $lines = @(Get-Content $file -ErrorAction SilentlyContinue)
    if ($lines.Count -le $seen) { return $seen }
    for ($i = $seen; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        $kind = "plain"
        if     ($l -match "FEHLER|ERROR|failed") { $kind = "bad" }
        elseif ($l -match "ACHTUNG|WARN")        { $kind = "warn" }
        elseif ($l -match "OK      ")            { $kind = "ok" }
        Say ("    " + $l) $kind
    }
    return $lines.Count
}


# ====================================================== downloading a version

# A truncated download that overwrites a working installation is worse than no
# update at all. So: expected size, zip magic, the right files inside, nothing
# belonging to the game - and the unpacked folder only becomes versions\<v>
# after all of that passed. Kill the launcher at any point before that last
# move and the installation on disk is untouched.
function Get-VersionFolder($state, $version) {
    $dir = Join-Path $VersionsDir $version
    if (Test-SourceFolder $dir) { return $dir }

    $rel = $null
    foreach ($r in $state.releases.list) { if ($r.version -eq $version) { $rel = $r; break } }
    if (-not $rel) {
        Say ("Nothing to download for version " + $version + " - GitHub lists no zip for it.") "bad"
        return ""
    }

    New-Item -ItemType Directory -Force -Path $VersionsDir | Out-Null
    $tmpZip = Join-Path $VersionsDir ($version + ".part")
    $tmpDir = Join-Path $VersionsDir ($version + ".unpack")
    Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
    if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue }

    Say ("Downloading " + $rel.name + "  (" + [math]::Round($rel.size / 1MB, 1) + " MB)")
    Say ("    " + $rel.url) "dim"
    try {
        $wc = New-Object System.Net.WebClient
        $wc.Headers.Add("User-Agent", $UA)
        $task = $wc.DownloadFileTaskAsync([uri]$rel.url, $tmpZip)
        $shown = 0
        while (-not $task.IsCompleted) {
            Pump
            Start-Sleep -Milliseconds 200
            if ((Test-Path $tmpZip) -and $rel.size -gt 0) {
                $pct = [int](100 * (Get-Item $tmpZip).Length / $rel.size)
                Set-Progress $pct
                if ($pct -ge $shown + 20) { $shown = $pct - ($pct % 20); Say ("    " + $shown + "%") "dim" }
            }
        }
        if ($task.IsFaulted) { throw $task.Exception.InnerException }
        $wc.Dispose()
    } catch {
        Say ("Download failed: " + $_.Exception.Message) "bad"
        Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
        Set-Progress 0
        return ""
    }
    Set-Progress 100

    $have = (Get-Item $tmpZip).Length
    if ($rel.size -gt 0 -and $have -ne $rel.size) {
        Say ("Download is " + $have + " bytes, GitHub says " + $rel.size + ". Discarded.") "bad"
        Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
        return ""
    }
    $head = [System.IO.File]::ReadAllBytes($tmpZip)[0..1]
    if ($head[0] -ne 0x50 -or $head[1] -ne 0x4B) {
        Say "That download is not a zip file. Discarded." "bad"
        Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
        return ""
    }
    Say ("Verified: " + $have + " bytes, and it is a zip.") "ok"

    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($tmpZip, $tmpDir)
    } catch {
        Say ("Unpacking failed: " + $_.Exception.Message) "bad"
        Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
        if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue }
        return ""
    }

    # The package is one folder deep: NextDayRevival_Client_<v>\...
    $inner = $tmpDir
    if (-not (Test-Path (Join-Path $inner "client_patch.ps1"))) {
        $sub = @(Get-ChildItem $tmpDir -Directory)
        if ($sub.Count -eq 1) { $inner = $sub[0].FullName }
    }

    # Never distribute somebody else's code. client_patch.ps1 computes the EAC
    # patch on the player's own installation; a package that carries game files
    # is wrong, and it does not get unpacked over an installation here.
    $bad = @(Get-ChildItem $inner -Recurse -File -Include "Assembly-CSharp.dll", "*.nd", "players.json" -ErrorAction SilentlyContinue)
    if ($bad.Count -gt 0) {
        Say ("This package carries game files (" + $bad[0].Name + "). Refused.") "bad"
        Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
        Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
        return ""
    }
    if (-not (Test-SourceFolder $inner)) {
        Say "This package has no client_patch.ps1, plugin and assets together. Refused." "bad"
        Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
        Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
        return ""
    }

    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    Move-Item $inner $dir
    Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
    if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue }
    Say ("Unpacked to versions\" + $version) "ok"
    return $dir
}

# Where a version can be installed from, without downloading anything: the
# launcher's own folder if it happens to be that version, otherwise the cache.
function Find-LocalSource($state, $version) {
    if ($state.packageOk -and $state.package -eq $version) { return $root }
    $dir = Join-Path $VersionsDir $version
    if (Test-SourceFolder $dir) { return $dir }
    return ""
}


# ================================================================= the actions

function Invoke-Install($state, $version) {
    if (-not $version) { Say "Pick a version in the list first." "warn"; return $false }
    if (Test-GameRunning) {
        Say "The game is running. Close Next Day: Survival first - it holds the plugin DLL open, and a half-written DLL is worse than an old one." "bad"
        return $false
    }
    $src = Find-LocalSource $state $version
    if (-not $src) { $src = Get-VersionFolder $state $version }
    if (-not $src) { return $false }

    Say ""
    Say ("Installing " + $version + " from " + $src)
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                 (Join-Path $src "client_patch.ps1"))
    if ($PlayerName) { $argList += @("-Name", $PlayerName) }
    # Always, never only when asked: without -Server the patch script writes
    # its own default and a repair moves the machine off its own server.
    if ($state.serverHost) { $argList += @("-Server", $state.serverHost) }
    if ($state.game) { $argList += @("-Game", $state.game) }
    $code = Start-Child "powershell.exe" $argList $src

    # The exit code of client_patch.ps1 is not the same question as "is the
    # plugin installed". It also returns 1 when the master server did not
    # answer its probe - which happens every time the local test server is
    # off, and which has nothing to do with the copy that just succeeded. So
    # the verdict comes from the DLL on disk, and the exit code is only a
    # reason to read the log.
    $now = Read-PluginVersion $state.pluginDll
    if ($now -eq $version) {
        Say ("Installed. " + $version + " is now the version in BepInEx\plugins.") "ok"
        if ($code -ne 0) {
            Say ("client_patch.ps1 ended with exit code " + $code + " - the plugin is in place, but something above wants reading.") "warn"
        }
        return $true
    }
    Say ("The plugin in the game folder still reports '" + $now + "', not " + $version + ". client_patch.ps1 exit code " + $code + " - read the lines above.") "bad"
    return $false
}

function Invoke-Repair($state) {
    if (Test-GameRunning) {
        Say "The game is running. Close Next Day: Survival first." "bad"
        return
    }
    # Repair means: put back exactly what is installed now. After a Steam file
    # verification the EAC patch is gone and the game hangs on connect - this
    # is the button that fixes that, and it must not quietly change versions.
    $v = $state.installed
    $src = ""
    if ($v) { $src = Find-LocalSource $state $v }
    if (-not $src) {
        if ($state.packageOk) {
            $src = $root
            if ($v -and $state.package -ne $v) {
                Say ("Version " + $v + " is not on this disk. Repairing with the launcher's own " + $state.package + " instead.") "warn"
            }
        }
    }
    if (-not $src) {
        Say "Nothing here to repair from: no plugin and assets next to the launcher, and no unpacked version in versions\." "bad"
        Say "Pick a version in the list and press Install - that repairs as well." "dim"
        return
    }
    Say ""
    Say ("Repairing from " + $src)
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $src "client_patch.ps1"))
    if ($PlayerName) { $argList += @("-Name", $PlayerName) }
    # Always, never only when asked: without -Server the patch script writes
    # its own default and a repair moves the machine off its own server.
    if ($state.serverHost) { $argList += @("-Server", $state.serverHost) }
    if ($state.game) { $argList += @("-Game", $state.game) }
    $code = Start-Child "powershell.exe" $argList $src
    $now = Read-PluginVersion $state.pluginDll
    if ($now) { Say ("Repaired: server address, EAC patch, BepInEx, plugin " + $now + " and assets are in place again.") "ok" }
    else { Say "The plugin is still not in the game folder - read the lines above." "bad" }
    if ($code -ne 0) { Say ("client_patch.ps1 ended with exit code " + $code + ", so read the log above as well.") "warn" }
}

function Invoke-Check($state) {
    $src = ""
    if ($state.installed) { $src = Find-LocalSource $state $state.installed }
    if (-not $src -and $state.packageOk) { $src = $root }
    if (-not $src) { Say "No client_patch.ps1 next to the launcher, so there is nothing to check with." "bad"; return }
    Say ""
    Say "Checking the installation - this changes nothing (client_patch.ps1 -Check)."
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                 (Join-Path $src "client_patch.ps1"), "-Check")
    if ($state.game) { $argList += @("-Game", $state.game) }
    Start-Child "powershell.exe" $argList $src | Out-Null
}

function Invoke-Play($state) {
    # Never Steam's Play button: that starts the EAC launcher, which since the
    # August 2026 module update aborts with "Untrusted system file".
    $starter = Join-Path $root "start_game.ps1"
    if (-not (Test-Path $starter) -and $state.installed) {
        $src = Find-LocalSource $state $state.installed
        if ($src) { $starter = Join-Path $src "start_game.ps1" }
    }
    if (-not (Test-Path $starter)) { Say "start_game.ps1 is missing next to the launcher." "bad"; return }
    Say ""
    Say "Starting the game without the EAC launcher (start_game.ps1)."
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $starter)
    if ($state.game) { $argList += @("-Game", $state.game) }
    Start-Child "powershell.exe" $argList $root | Out-Null
}


# The vanilla start. Same game, same EAC patch, same ClientConfig.ini, same
# master server - only without our plugin.
#
# Doorstop is what injects BepInEx (winhttp.dll next to the exe), and the
# copy in this installation is version 4: its strings carry both the
# --doorstop-enabled switch and the DOORSTOP_DISABLE environment variable.
# Both are set here, because the two Doorstop generations disagree about
# which one they read, and neither touches a single file on disk. That is the
# whole point: renaming the plugin away would leave a machine modless if the
# launcher died between the two renames.
#
# start_game.ps1 is not called for this one. It is not allowed to change and
# it cannot carry an environment variable in; it also waits forty seconds for
# a BepInEx log line that, in a vanilla start, must never come. So the launch
# is repeated here - the same three things it does: Steam first, then the
# exe, never Steam's Play button.
function Invoke-PlayVanilla($state) {
    if (-not $state.game) { Say "No game folder - nothing to start." "bad"; return }
    if (Test-GameRunning) { Say "Next Day: Survival is already running." "warn"; return }
    $exe = Join-Path $state.game "nextday_game.exe"
    if (-not (Test-Path $exe)) { Say ("Not found: " + $exe) "bad"; return }

    Say ""
    Say "Vanilla start: the game as it shipped, on our master server, without the plugin."

    # Advisory, never a block - the same rule the rest of this window obeys.
    if ($state.eac -eq "on") {
        Say "EAC is still switched on in the game code, so the game will hang on connect. Press Repair once - it patches EAC and writes the server address - then start again." "warn"
    }
    if (-not $state.configHost) {
        Say "ClientConfig.ini points at no master server yet, so the server list in the main menu stays empty. Press Repair once." "warn"
    }

    # Steam has to run: the game asks it for the id it logs in with, and the
    # master server files a profile under exactly that id.
    if (-not (Get-Process -Name "steam" -ErrorAction SilentlyContinue)) {
        $steamDir = Get-SteamPath
        $steamExe = ""
        if ($steamDir) { $steamExe = Join-Path $steamDir "steam.exe" }
        if ($steamExe -and (Test-Path $steamExe)) {
            Say "Steam is not running - starting it first." "dim"
            Start-Process $steamExe | Out-Null
            for ($i = 0; $i -lt 30; $i++) {
                if (Get-Process -Name "steam" -ErrorAction SilentlyContinue) { break }
                Start-Sleep -Seconds 1
                Pump
            }
        } else {
            Say "Steam was not found. Start Steam by hand, then press this button again." "warn"
        }
    }

    $log = Join-Path $state.game "BepInEx\LogOutput.log"
    $before = $null
    if (Test-Path $log) { $before = (Get-Item $log).LastWriteTime }

    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName         = $exe
        $psi.Arguments        = "--doorstop-enabled false"
        $psi.WorkingDirectory = $state.game
        $psi.UseShellExecute  = $false      # required, or the variable below is dropped
        $psi.EnvironmentVariables["DOORSTOP_DISABLE"] = "1"
        [System.Diagnostics.Process]::Start($psi) | Out-Null
    } catch {
        Say ("Could not start the game: " + $_.Exception.Message) "bad"
        return
    }
    Say "nextday_game.exe started with BepInEx switched off for this run." "ok"

    # Proof rather than promise: if BepInEx loads anyway it writes its log
    # within a second or two, and then this was not a vanilla start.
    $loaded = $false
    for ($i = 0; $i -lt 14; $i++) {
        Start-Sleep -Milliseconds 900
        Pump
        if (Test-Path $log) {
            $now = (Get-Item $log).LastWriteTime
            if ($null -eq $before -or $now -gt $before) { $loaded = $true; break }
        }
    }
    if ($loaded) {
        Say "BepInEx wrote to its log anyway - this run is NOT vanilla. Doorstop ignored both switches; close the game and say so." "bad"
    } else {
        Say "No BepInEx log line in twelve seconds - the plugin stayed out of this run. Nothing on disk changed; the next normal start is modded again." "ok"
    }
}


# ============================================================== console mode

function Show-Console($s) {
    $line = "=" * 68
    Write-Host ""
    Write-Host $line
    Write-Host "Next Day: Revival - Launcher"
    Write-Host $line
    Write-Host ("  Installed        : " + $(if ($s.installed) { $s.installed } else { "nothing" }))
    Write-Host ("  EAC in game code : " + $s.eac)
    Write-Host ("  Game folder      : " + $(if ($s.game) { $s.game } else { "not found" }))
    Write-Host ("  BepInEx          : " + $(if ($s.bepinex) { "yes" } else { "no" }))
    Write-Host ("  This package     : " + $(if ($s.package) { $s.package } else { "-" }))
    Write-Host ("  Master server    : " + $s.serverHost + "   (" + $s.pointNote + ")")
    if ($s.list.ok) {
        Write-Host ("  Server list      : answers, " + $s.list.bytes + " bytes")
    } else {
        Write-Host ("  Server list      : NO ANSWER from " + (Get-ListUrlFor $s.serverHost))
    }
    if ($s.players -ge 0) {
        Write-Host ("  Players online   : " + $s.players + $(if ($s.seats -gt 0) { " of " + $s.seats } else { "" }))
    } else {
        Write-Host ("  Players online   : not reported by this server")
    }
    if ($s.server.ok) {
        Write-Host ("  Server content   : " + $s.server.contentVersion)
        Write-Host ("  Server asks for  : " + $s.server.minClientVersion + " or newer")
        $ids = ($s.server.weapons | ForEach-Object { $_.id }) -join ", "
        Write-Host ("  Server weapons   : " + $(if ($ids) { $ids } else { "none" }))
    } else {
        Write-Host ("  Server version   : none - " + $s.server.error)
    }
    if ($s.releases.ok) {
        Write-Host ("  Latest release   : " + $s.latest)
        Write-Host ("  Releases         : " + (($s.releases.list | ForEach-Object { $_.version }) -join ", "))
    } else {
        Write-Host ("  GitHub           : not reachable - " + $s.releases.error)
    }
    if ($s.cached.Count -gt 0) { Write-Host ("  Downloaded here  : " + ($s.cached -join ", ")) }
    Write-Host ""
    $colour = "Green"
    if ($s.state -eq "old" -or $s.state -eq "missing") { $colour = "Yellow" }
    if ($s.state -eq "offline") { $colour = "Yellow" }
    if ($s.state -eq "ahead" -or $s.state -eq "noinfo") { $colour = "Cyan" }
    if ($s.state -eq "nogame")  { $colour = "Red" }
    Write-Host ("  " + $s.verdict) -ForegroundColor $colour
    Write-Host ("  " + $s.detail)
    Write-Host ""
}

if ($Console -or $Install -or $Vanilla) {
    $s = Get-State
    Show-Console $s
    if ($Vanilla) {
        # The same start the button performs, without a window. This is how
        # the vanilla path is tested.
        Invoke-PlayVanilla $s
        exit 0
    }
    if ($Install) {
        # Same switch the button performs, without a window - this is how the
        # install path gets tested, and it is scriptable.
        $ok = Invoke-Install $s $Install
        Write-Host ""
        if ($ok) {
            $now = Read-PluginVersion $s.pluginDll
            Write-Host ("Now installed: " + $now) -ForegroundColor Green
            if ($now -ne $Install) {
                Write-Host ("Asked for " + $Install + " but the plugin reports " + $now + ".") -ForegroundColor Red
                exit 1
            }
            exit 0
        }
        exit 1
    }
    Invoke-WeaponCheck $s
    Write-Host ""
    exit 0
}


# ==================================================================== the window
#
# Dark, because the game is dark and a launcher that looks like a tax form is
# the first thing a player distrusts. Nothing here is decoration for its own
# sake: the logo is the game's own icon, the biggest number in the window is
# the one a player opens a launcher for - is anybody on? - and every colour
# still means exactly one thing (green ready, amber attention, red broken).

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$script:Gui = $true

# Launcher.bat starts this hidden, so a crash before the window exists would
# be a double-click that does nothing at all. Say it out loud instead.
trap {
    $msg = $_.Exception.Message + "`r`n`r`n" + $_.InvocationInfo.PositionMessage
    if ($script:Gui) {
        try {
            [System.Windows.Forms.MessageBox]::Show($msg,
                "Next Day: Revival - Launcher", "OK", "Error") | Out-Null
        } catch {}
    } else {
        Write-Host $msg -ForegroundColor Red
    }
    exit 1
}

# ---- palette. Grey-black and bone white out of the game's own icon, one
# amber accent, and three signal colours that are never used decoratively.
# $DEEP, not $BAND: PowerShell does not tell $BAND and the header panel
# $band apart, and the second one wins.
$BG     = [System.Drawing.Color]::FromArgb(18, 19, 22)
$DEEP   = [System.Drawing.Color]::FromArgb(12, 13, 15)
$CARD   = [System.Drawing.Color]::FromArgb(27, 29, 34)
$HOVER  = [System.Drawing.Color]::FromArgb(38, 41, 48)
$SEL    = [System.Drawing.Color]::FromArgb(44, 48, 57)
$LINE   = [System.Drawing.Color]::FromArgb(48, 52, 60)
$INK    = [System.Drawing.Color]::FromArgb(232, 230, 225)
$MUTED  = [System.Drawing.Color]::FromArgb(146, 152, 160)
$DIM    = [System.Drawing.Color]::FromArgb(104, 110, 118)
$ACCENT = [System.Drawing.Color]::FromArgb(212, 146, 48)
$GREEN  = [System.Drawing.Color]::FromArgb(110, 200, 130)
$AMBER  = [System.Drawing.Color]::FromArgb(224, 168, 68)
$RED    = [System.Drawing.Color]::FromArgb(228, 98, 88)
$BLUE   = [System.Drawing.Color]::FromArgb(116, 166, 222)

$script:PenLine = New-Object System.Drawing.Pen($LINE)
$script:BrCard  = New-Object System.Drawing.SolidBrush($CARD)
$script:BrSel   = New-Object System.Drawing.SolidBrush($SEL)
$script:BrHead  = New-Object System.Drawing.SolidBrush($DEEP)
$script:BrInk   = New-Object System.Drawing.SolidBrush($INK)
$script:BrMuted = New-Object System.Drawing.SolidBrush($MUTED)
$script:BrDim   = New-Object System.Drawing.SolidBrush($DIM)
$script:BrAcc   = New-Object System.Drawing.SolidBrush($ACCENT)
$script:BrGreen = New-Object System.Drawing.SolidBrush($GREEN)
$script:BrBlue  = New-Object System.Drawing.SolidBrush($BLUE)
$script:FRow    = New-Object System.Drawing.Font("Segoe UI", 9)
$script:FRowB   = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
$script:FHead   = New-Object System.Drawing.Font("Segoe UI", 8, [System.Drawing.FontStyle]::Bold)
$script:SFRow   = New-Object System.Drawing.StringFormat
$script:SFRow.Trimming      = [System.Drawing.StringTrimming]::EllipsisCharacter
$script:SFRow.FormatFlags   = [System.Drawing.StringFormatFlags]::NoWrap
$script:SFRow.LineAlignment = [System.Drawing.StringAlignment]::Center

# Two things Windows will not do through WinForms: a dark title bar, and
# handing out the large icon frame of an exe. One call each, both optional -
# without the compiler the window is simply a little plainer.
$script:Native = $false
try {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class NdLauncherNative {
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int PrivateExtractIcons(string file, int index, int cx, int cy,
                                                 IntPtr[] icons, int[] ids, int count, int flags);
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr h);
}
"@
    $script:Native = $true
} catch {}

# The logo is not shipped with this launcher and must not be: it is SOFF
# Games' artwork. It is read out of the player's own nextday_game.exe when the
# window opens - the icon in that file IS the NEXT DAY wordmark, and it
# carries a 256 pixel frame. Same principle as the EAC patch: computed on the
# machine it belongs to, never distributed. No game, no logo, and the window
# then says so instead of pretending.
function Get-GameLogo($game) {
    if (-not $game) { return $null }
    $exe = Join-Path $game "nextday_game.exe"
    if (-not (Test-Path $exe)) { return $null }
    if ($script:Native) {
        foreach ($size in @(256, 128, 64)) {
            try {
                $h  = New-Object IntPtr[] 1
                $id = New-Object int[] 1
                $n  = [NdLauncherNative]::PrivateExtractIcons($exe, 0, $size, $size, $h, $id, 1, 0)
                if ($n -gt 0 -and $h[0] -ne [IntPtr]::Zero) {
                    $ic  = [System.Drawing.Icon]::FromHandle($h[0])
                    $bmp = $ic.ToBitmap()
                    [void][NdLauncherNative]::DestroyIcon($h[0])
                    return $bmp
                }
            } catch {}
        }
    }
    try { return ([System.Drawing.Icon]::ExtractAssociatedIcon($exe)).ToBitmap() } catch {}
    return $null
}

function Get-GameIcon($game) {
    if (-not $game) { return $null }
    $exe = Join-Path $game "nextday_game.exe"
    if (-not (Test-Path $exe)) { return $null }
    try { return [System.Drawing.Icon]::ExtractAssociatedIcon($exe) } catch {}
    return $null
}


# ---- the window itself

$form = New-Object System.Windows.Forms.Form
$form.Text = "Next Day: Revival"
$form.Size = New-Object System.Drawing.Size(960, 812)
$form.MinimumSize = New-Object System.Drawing.Size(860, 720)
$form.StartPosition = "CenterScreen"
$form.BackColor = $BG
$form.ForeColor = $INK
$form.Font = New-Object System.Drawing.Font("Segoe UI", 9)

if ($script:Native) {
    try {
        # 20 is the documented attribute on current Windows 10 and 11, 19 was
        # the one on the first builds that had it. Setting both is cheaper
        # than asking for the build number.
        $on = 1
        [void][NdLauncherNative]::DwmSetWindowAttribute($form.Handle, 20, [ref]$on, 4)
        [void][NdLauncherNative]::DwmSetWindowAttribute($form.Handle, 19, [ref]$on, 4)
    } catch {}
}

function New-Label($text, $x, $y, $w, $h, $size, $bold, $colour, $back) {
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $text
    $l.Location = New-Object System.Drawing.Point($x, $y)
    $l.Size = New-Object System.Drawing.Size($w, $h)
    $l.AutoSize = $false
    $l.ForeColor = $colour
    $l.BackColor = $back
    $style = [System.Drawing.FontStyle]::Regular
    if ($bold) { $style = [System.Drawing.FontStyle]::Bold }
    $l.Font = New-Object System.Drawing.Font("Segoe UI", $size, $style)
    return $l
}

# A panel that draws its own one pixel border: BorderStyle only knows the
# system colours, and those are mixed for a white window.
function New-Card($x, $y, $w, $h) {
    $p = New-Object System.Windows.Forms.Panel
    $p.Location = New-Object System.Drawing.Point($x, $y)
    $p.Size = New-Object System.Drawing.Size($w, $h)
    $p.BackColor = $CARD
    $p.Add_Paint({
        param($sender, $e)
        $r = New-Object System.Drawing.Rectangle(0, 0, ($sender.Width - 1), ($sender.Height - 1))
        $e.Graphics.DrawRectangle($script:PenLine, $r)
    })
    return $p
}

function New-Btn($text, $w, $primary) {
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $text
    $b.Size = New-Object System.Drawing.Size($w, 36)
    $b.FlatStyle = "Flat"
    $b.FlatAppearance.BorderSize = 1
    $b.UseVisualStyleBackColor = $false
    if ($primary) {
        $b.BackColor = $ACCENT
        $b.ForeColor = [System.Drawing.Color]::FromArgb(24, 20, 12)
        $b.FlatAppearance.BorderColor = $ACCENT
        $b.FlatAppearance.MouseOverBackColor = [System.Drawing.Color]::FromArgb(232, 168, 70)
        $b.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
    } else {
        $b.BackColor = $CARD
        $b.ForeColor = $INK
        $b.FlatAppearance.BorderColor = $LINE
        $b.FlatAppearance.MouseOverBackColor = $HOVER
    }
    return $b
}


# ---- header band: logo, name, and the number this window exists for

$band = New-Object System.Windows.Forms.Panel
$band.Location = New-Object System.Drawing.Point(0, 0)
$band.Size = New-Object System.Drawing.Size(940, 108)
$band.BackColor = $DEEP
$form.Controls.Add($band)

$logo = New-Object System.Windows.Forms.PictureBox
$logo.Location = New-Object System.Drawing.Point(22, 20)
$logo.Size = New-Object System.Drawing.Size(68, 68)
$logo.SizeMode = "Zoom"
$logo.BackColor = $DEEP
$band.Controls.Add($logo)

$title = New-Label "NEXT DAY: REVIVAL" 106 24 460 32 16.5 $true $INK $DEEP
$band.Controls.Add($title)

$subtitle = New-Label "starting up ..." 108 60 500 20 9 $false $MUTED $DEEP
$band.Controls.Add($subtitle)

# The online count. Right hand side, larger than anything else in the window,
# because "is anybody playing" is the question a launcher gets opened for.
$onlineCap = New-Label "PLAYERS ONLINE" 600 18 320 16 8 $true $MUTED $DEEP
$onlineCap.TextAlign = "MiddleRight"
$band.Controls.Add($onlineCap)

$onlineNum = New-Label "?" 600 28 320 56 38 $true $DIM $DEEP
$onlineNum.TextAlign = "MiddleRight"
$band.Controls.Add($onlineNum)

$onlineWho = New-Label "" 600 84 320 16 8 $false $DIM $DEEP
$onlineWho.TextAlign = "MiddleRight"
$band.Controls.Add($onlineWho)

$rule = New-Object System.Windows.Forms.Panel
$rule.Location = New-Object System.Drawing.Point(0, 108)
$rule.Size = New-Object System.Drawing.Size(940, 1)
$rule.BackColor = $LINE
$form.Controls.Add($rule)


# ---- verdict

$verdict = New-Label "Checking ..." 18 124 880 24 12 $true $INK $BG
$form.Controls.Add($verdict)

$detail = New-Label "" 18 150 880 34 9 $false $MUTED $BG
$form.Controls.Add($detail)


# ---- three tiles: installed, server, latest

function New-Tile($caption, $x) {
    $panel = New-Card $x 194 280 84
    $t = New-Label $caption 14 10 250 15 8 $true $DIM $CARD
    $panel.Controls.Add($t)
    $v = New-Label "..." 12 26 254 34 17 $true $INK $CARD
    $panel.Controls.Add($v)
    $n = New-Label "" 14 62 254 16 8 $false $MUTED $CARD
    $panel.Controls.Add($n)
    return @{ panel = $panel; caption = $t; value = $v; note = $n }
}

$tileInstalled = New-Tile "INSTALLED ON THIS PC" 16
$tileServer    = New-Tile "THE SERVER" 308
$tileLatest    = New-Tile "NEWEST RELEASE" 600
$form.Controls.Add($tileInstalled.panel)
$form.Controls.Add($tileServer.panel)
$form.Controls.Add($tileLatest.panel)


# ---- which master server. Visible, because it decides whether the game
# finds a server list at all, and because Install and Repair write it.

$srvLabel = New-Label "Master server" 18 296 96 20 9 $false $MUTED $BG
$form.Controls.Add($srvLabel)

$srvBox = New-Object System.Windows.Forms.ComboBox
$srvBox.Location = New-Object System.Drawing.Point(116, 292)
$srvBox.Size = New-Object System.Drawing.Size(200, 24)
$srvBox.DropDownStyle = "DropDown"
$srvBox.FlatStyle = "Flat"
$srvBox.BackColor = $CARD
$srvBox.ForeColor = $INK
$form.Controls.Add($srvBox)

$srvNote = New-Label "" 328 296 570 18 9 $false $DIM $BG
$form.Controls.Add($srvNote)

$listLabel = New-Label "Pick a version, then Install. Switching down works the same way as switching up." 18 328 700 18 9 $false $DIM $BG
$form.Controls.Add($listLabel)


# ---- version list. Owner drawn from here on: a ListView paints its rows and
# its column headers in the system colours, and on this background that is a
# white bar with white text in it.

$listFrame = New-Card 16 350 908 168
$listFrame.Padding = New-Object System.Windows.Forms.Padding(1)
$form.Controls.Add($listFrame)

$list = New-Object System.Windows.Forms.ListView
$list.Dock = "Fill"
$list.View = "Details"
$list.FullRowSelect = $true
$list.GridLines = $false
$list.MultiSelect = $false
$list.HideSelection = $false
$list.BorderStyle = "None"
$list.BackColor = $CARD
$list.ForeColor = $INK
$list.HeaderStyle = "Nonclickable"
$list.OwnerDraw = $true
$list.Columns.Add("VERSION", 110)      | Out-Null
$list.Columns.Add("STATE", 330)        | Out-Null
$list.Columns.Add("WHERE IT IS", 250)  | Out-Null
$list.Columns.Add("DOWNLOAD", 110)     | Out-Null

# Row height. A ListView takes it from its image list and from nothing else,
# so an image list with no images in it is the only lever there is.
$spacer = New-Object System.Windows.Forms.ImageList
$spacer.ImageSize = New-Object System.Drawing.Size(1, 26)
$list.SmallImageList = $spacer

# Owner drawing without double buffering flickers on every mouse move, and
# the property that switches it on is protected.
try {
    $flags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic
    $dbl = [System.Windows.Forms.ListView].GetProperty("DoubleBuffered", $flags)
    $dbl.SetValue($list, $true, $null)
} catch {}

$list.Add_DrawColumnHeader({
    param($sender, $e)
    $e.Graphics.FillRectangle($script:BrHead, $e.Bounds)
    $r = New-Object System.Drawing.RectangleF(($e.Bounds.X + 10), $e.Bounds.Y,
                                              ($e.Bounds.Width - 14), $e.Bounds.Height)
    $e.Graphics.DrawString($e.Header.Text, $script:FHead, $script:BrDim, $r, $script:SFRow)
    $e.Graphics.DrawLine($script:PenLine, $e.Bounds.Left, ($e.Bounds.Bottom - 1),
                         $e.Bounds.Right, ($e.Bounds.Bottom - 1))
})

$list.Add_DrawItem({
    param($sender, $e)
    if ($e.Item.Selected) {
        $e.Graphics.FillRectangle($script:BrSel, $e.Bounds)
        $bar = New-Object System.Drawing.Rectangle($e.Bounds.X, $e.Bounds.Y, 3, $e.Bounds.Height)
        $e.Graphics.FillRectangle($script:BrAcc, $bar)
    } else {
        $e.Graphics.FillRectangle($script:BrCard, $e.Bounds)
    }
})

$list.Add_DrawSubItem({
    param($sender, $e)
    $text = $e.SubItem.Text
    if (-not $text) { return }
    $brush = $script:BrMuted
    $font  = $script:FRow
    if ($e.ColumnIndex -eq 0) {
        $brush = $script:BrInk
        if ($e.Item.Tag -eq "installed") { $font = $script:FRowB; $brush = $script:BrGreen }
    } elseif ($e.ColumnIndex -eq 1) {
        # Contains, not -like: -like ignores case, and then the sentence
        # "older than the server asks for" gets the colour of the mark
        # "WHAT THE SERVER ASKS FOR". The marks are the shouted ones.
        if ($text.StartsWith("INSTALLED"))            { $brush = $script:BrGreen }
        elseif ($text.Contains("WHAT THE SERVER"))    { $brush = $script:BrAcc }
        elseif ($text.StartsWith("NEWEST"))           { $brush = $script:BrBlue }
    }
    $r = New-Object System.Drawing.RectangleF(($e.Bounds.X + 10), $e.Bounds.Y,
                                              ($e.Bounds.Width - 14), $e.Bounds.Height)
    $e.Graphics.DrawString($text, $font, $brush, $r, $script:SFRow)
})

$listFrame.Controls.Add($list)


# ---- buttons. Install on the left, next to the version list it belongs to;
# play on the right, where a player looks last.

$btnInstall = New-Btn "Install selected version" 200 $false
$btnRepair  = New-Btn "Repair" 104 $false
$btnCheck   = New-Btn "Check" 104 $false
$btnRefresh = New-Btn "Refresh" 104 $false
$btnVanilla = New-Btn "Play vanilla" 132 $false
$btnPlay    = New-Btn "PLAY" 140 $true
$btnInstall.Location = New-Object System.Drawing.Point(16, 530)
$btnRepair.Location  = New-Object System.Drawing.Point(224, 530)
$btnCheck.Location   = New-Object System.Drawing.Point(336, 530)
$btnRefresh.Location = New-Object System.Drawing.Point(448, 530)
$btnVanilla.Location = New-Object System.Drawing.Point(640, 530)
$btnPlay.Location    = New-Object System.Drawing.Point(784, 530)
$form.Controls.Add($btnInstall)
$form.Controls.Add($btnRepair)
$form.Controls.Add($btnCheck)
$form.Controls.Add($btnRefresh)
$form.Controls.Add($btnVanilla)
$form.Controls.Add($btnPlay)

$hint = New-Label "" 16 572 908 34 8.5 $false $DIM $BG
$hint.Text = "Repair re-applies the EAC patch and the server address - the button to press after Steam has verified the game files." + "`r`n" +
             "Play vanilla starts the untouched game on the same master server: no plugin for that one start, and nothing on disk changes."
$form.Controls.Add($hint)

# A ProgressBar is drawn by the system theme in system green and cannot be
# talked out of it, so the bar is two panels.
$progBack = New-Object System.Windows.Forms.Panel
$progBack.Location = New-Object System.Drawing.Point(16, 610)
$progBack.Size = New-Object System.Drawing.Size(908, 4)
$progBack.BackColor = $LINE
$form.Controls.Add($progBack)

$script:ProgFill = New-Object System.Windows.Forms.Panel
$script:ProgFill.Location = New-Object System.Drawing.Point(0, 0)
$script:ProgFill.Size = New-Object System.Drawing.Size(0, 4)
$script:ProgFill.BackColor = $ACCENT
$progBack.Controls.Add($script:ProgFill)

# Set-Progress at the top of the file drives a ProgressBar. Here it drives the
# two panels instead - same name, same contract, same call sites.
function Set-Progress($pct) {
    if ($pct -lt 0) { $pct = 0 }
    if ($pct -gt 100) { $pct = 100 }
    $script:ProgFill.Width = [int]($progBack.Width * $pct / 100)
    Pump
}

$logFrame = New-Card 16 622 908 150
$logFrame.Padding = New-Object System.Windows.Forms.Padding(1)
$form.Controls.Add($logFrame)

$script:LogBox = New-Object System.Windows.Forms.RichTextBox
$script:LogBox.Dock = "Fill"
$script:LogBox.ReadOnly = $true
$script:LogBox.BackColor = $CARD
$script:LogBox.ForeColor = $INK
$script:LogBox.Font = New-Object System.Drawing.Font("Consolas", 8.5)
$script:LogBox.BorderStyle = "None"
$logFrame.Controls.Add($script:LogBox)


# ---- layout. The window is resizable, so everything that is not anchored
# has to be told where it went.

function Update-Layout {
    $w = $form.ClientSize.Width
    $h = $form.ClientSize.Height

    $band.Width = $w
    $rule.Width = $w
    $right = $w - 24 - 320
    $onlineCap.Left = $right
    $onlineNum.Left = $right
    $onlineWho.Left = $right

    $subtitle.Width = $w - 108 - 344
    $title.Width    = $w - 108 - 344
    $verdict.Width = $w - 36
    $detail.Width  = $w - 36
    $srvNote.Width = $w - 346
    $hint.Width    = $w - 32

    $tileW = [int](($w - 32 - 24) / 3)
    $tileInstalled.panel.Width = $tileW
    $tileServer.panel.Left  = 16 + $tileW + 12
    $tileServer.panel.Width = $tileW
    $tileLatest.panel.Left  = 16 + 2 * ($tileW + 12)
    $tileLatest.panel.Width = $w - 16 - $tileLatest.panel.Left
    foreach ($t in @($tileInstalled, $tileServer, $tileLatest)) {
        $t.caption.Width = $t.panel.Width - 28
        $t.value.Width   = $t.panel.Width - 24
        $t.note.Width    = $t.panel.Width - 28
        $t.panel.Invalidate()
    }

    $listFrame.Width = $w - 32
    # Minus the vertical scrollbar as well: without that the columns add up
    # to more than the client area and the list grows a horizontal scrollbar
    # it does not need.
    $rest = $listFrame.Width - 2 - 110 - 250 - 110 - 32
    if ($rest -lt 120) { $rest = 120 }
    $list.Columns[1].Width = $rest

    $btnPlay.Left    = $w - 16 - $btnPlay.Width
    $btnVanilla.Left = $btnPlay.Left - 12 - $btnVanilla.Width

    $progBack.Width = $w - 32
    $logFrame.Width = $w - 32
    $logFrame.Height = $h - 622 - 16
}

$form.Add_Resize({ Update-Layout })


# ---- filling it in

$script:State   = $null
$script:Busy    = $false
$script:Filling = $false

function Update-Header($s) {
    $bits = @()
    if ($s.installed) { $bits += "Revival " + $s.installed + " installed" }
    else { $bits += "no plugin installed" }
    if ($s.game) { $bits += $s.game } else { $bits += "game folder not found" }
    $subtitle.Text = $bits -join "   -   "

    # The number, and what it is allowed to claim. -1 is "this server does not
    # report it" and reads as a dash, never as a zero: a wrong zero is the
    # fastest way to convince somebody that nobody plays here.
    if ($s.players -ge 0) {
        $onlineNum.Text = "" + $s.players
        if ($s.players -gt 0) { $onlineNum.ForeColor = $ACCENT } else { $onlineNum.ForeColor = $MUTED }
        if ($s.players -eq 1) { $onlineCap.Text = "PLAYER ONLINE" } else { $onlineCap.Text = "PLAYERS ONLINE" }
        $seats = ""
        if ($s.seats -gt 0) { $seats = "   -   " + $s.seats + " seats" }
        $onlineWho.Text = "on " + $s.serverHost + $seats
    } else {
        $onlineNum.Text = "?"
        $onlineNum.ForeColor = $DIM
        $onlineCap.Text = "PLAYERS ONLINE"
        if ($s.list.ok) { $onlineWho.Text = $s.serverHost + " does not report it" }
        else { $onlineWho.Text = $s.serverHost + " is not answering" }
    }
}

function Update-Tiles($s) {
    if ($s.installed) {
        $tileInstalled.value.Text = $s.installed
        $tileInstalled.value.ForeColor = $INK
        $eac = ""
        if ($s.eac -eq "on")  { $eac = "   -   EAC still on" }
        if ($s.eac -eq "off") { $eac = "   -   EAC off" }
        $tileInstalled.note.Text = "plugin in BepInEx\plugins" + $eac
    } else {
        $tileInstalled.value.Text = "none"
        $tileInstalled.value.ForeColor = $AMBER
        if ($s.game) { $tileInstalled.note.Text = "game found, no plugin yet" }
        else { $tileInstalled.note.Text = "game folder not found" }
    }

    if ($s.server.ok) {
        $tileServer.value.Text = $s.server.contentVersion
        $tileServer.value.ForeColor = $INK
        $tileServer.note.Text = $s.serverHost + " - asks for " + $s.server.minClientVersion + "+"
    } elseif ($s.list.ok) {
        $tileServer.value.Text = "up"
        $tileServer.value.ForeColor = $BLUE
        $tileServer.note.Text = $s.serverHost + " - answers, reports no version"
    } else {
        $tileServer.value.Text = "offline"
        $tileServer.value.ForeColor = $AMBER
        $tileServer.note.Text = $s.serverHost + " - no answer in two seconds"
    }

    if ($s.latest) {
        $tileLatest.value.Text = $s.latest
        $tileLatest.value.ForeColor = $INK
        $tileLatest.note.Text = "on GitHub"
    } else {
        $tileLatest.value.Text = "?"
        $tileLatest.value.ForeColor = $AMBER
        $tileLatest.note.Text = "GitHub not reachable"
    }

    $verdict.Text = $s.verdict
    $detail.Text  = $s.detail
    if ($s.state -eq "sync")       { $verdict.ForeColor = $GREEN }
    elseif ($s.state -eq "ahead")  { $verdict.ForeColor = $BLUE }
    elseif ($s.state -eq "noinfo") { $verdict.ForeColor = $BLUE }
    elseif ($s.state -eq "nogame") { $verdict.ForeColor = $RED }
    else { $verdict.ForeColor = $AMBER }
}

function Update-Server($s) {
    $script:Filling = $true
    $srvBox.Items.Clear()
    $seen = @()
    foreach ($h in @($s.serverHost, $s.configHost, $LOCAL_HOST, $DEFAULT_HOST)) {
        if ($h -and ($seen -notcontains $h)) { $seen += $h; $srvBox.Items.Add($h) | Out-Null }
    }
    $srvBox.Text = $s.serverHost
    $script:Filling = $false
    $srvNote.Text = $s.pointNote
}

function Update-List($s) {
    $list.Items.Clear()
    $seen = @{}
    $rows = @()

    foreach ($r in $s.releases.list) {
        if (-not $r.version) { continue }
        if ($seen.ContainsKey($r.version)) { continue }
        $seen[$r.version] = $true
        $rows += @{ version = $r.version; size = $r.size; release = $true }
    }
    foreach ($v in $s.cached) {
        if ($seen.ContainsKey($v)) { continue }
        $seen[$v] = $true
        $rows += @{ version = $v; size = 0; release = $false }
    }
    if ($s.package -and -not $seen.ContainsKey($s.package)) {
        $seen[$s.package] = $true
        $rows += @{ version = $s.package; size = 0; release = $false }
    }

    $rows = @($rows | Sort-Object -Property @{ Expression = {
        try { [version]$_.version } catch { [version]"0.0.0" } } } -Descending)

    foreach ($r in $rows) {
        $v = $r.version
        $marks = @()
        if ($v -eq $s.installed) { $marks += "INSTALLED" }
        if ($v -eq $s.latest)    { $marks += "NEWEST" }
        if ($s.server.ok -and $s.server.minClientVersion -and $v -eq (Clean-Ver $s.server.minClientVersion)) {
            $marks += "WHAT THE SERVER ASKS FOR"
        }
        $state = $marks -join "  -  "
        if (-not $state -and $s.server.ok -and $s.server.minClientVersion) {
            if ((Compare-Ver $v $s.server.minClientVersion) -lt 0) { $state = "older than the server asks for" }
            else { $state = "newer than the server asks for" }
        }

        $where = "download from GitHub"
        if ($s.packageOk -and $v -eq $s.package) { $where = "next to this launcher" }
        elseif ($s.cached -contains $v)          { $where = "downloaded, in versions\" }
        elseif (-not $r.release)                 { $where = "on this PC only" }

        $size = ""
        if ($r.size -gt 0) { $size = "" + [math]::Round($r.size / 1MB, 1) + " MB" }
        if ($where -ne "download from GitHub") { $size = "ready" }

        $item = New-Object System.Windows.Forms.ListViewItem($v)
        $item.SubItems.Add($state)  | Out-Null
        $item.SubItems.Add($where)  | Out-Null
        $item.SubItems.Add($size)   | Out-Null
        if ($v -eq $s.installed) {
            $item.Tag = "installed"
            $item.Selected = $true
        }
        $list.Items.Add($item) | Out-Null
    }
    if ($list.SelectedItems.Count -eq 0 -and $list.Items.Count -gt 0) { $list.Items[0].Selected = $true }
    # Filling a ListView leaves it scrolled to the last row it drew, which
    # here is the oldest release nobody wants. Newest first, and then the
    # selected one in view.
    if ($list.Items.Count -gt 0) { $list.Items[0].EnsureVisible() }
    if ($list.SelectedItems.Count -gt 0) { $list.SelectedItems[0].EnsureVisible() }
    $list.Focus() | Out-Null
}

function Set-Busy($busy) {
    $script:Busy = $busy
    $btnInstall.Enabled = -not $busy
    $btnRepair.Enabled  = -not $busy
    $btnCheck.Enabled   = -not $busy
    $btnRefresh.Enabled = -not $busy
    $btnPlay.Enabled    = -not $busy
    $btnVanilla.Enabled = -not $busy
    if ($busy) { $form.Cursor = [System.Windows.Forms.Cursors]::AppStarting }
    else { $form.Cursor = [System.Windows.Forms.Cursors]::Default }
    Pump
}

function Refresh-All($quiet) {
    Set-Busy $true
    if (-not $quiet) { Say "" }
    Say "Checking installation, master server and GitHub ..." "dim"
    $s = Get-State
    $script:State = $s
    Update-Header $s
    Update-Tiles $s
    Update-Server $s
    Update-List $s
    if ($s.game) { Say ("Game folder: " + $s.game) "dim" }
    Say ("Master server: " + $s.serverHost + " - " + $s.pointNote) "dim"
    if (-not $s.list.ok) { Say ((Get-ListUrlFor $s.serverHost) + " did not answer: " + $s.list.error) "warn" }
    if ($s.list.ok -and -not $s.server.ok) { Say ((Get-RevivalUrlFor $s.serverHost) + " gave no version: " + $s.server.error) "dim" }
    if (-not $s.releases.ok) { Say ("GitHub did not answer: " + $s.releases.error) "warn" }
    if ($s.server.ok -and $s.server.message) { Say ("Message from the server: " + $s.server.message) "warn" }
    if ($s.players -ge 0) { Say ("Players online: " + $s.players) "dim" }
    $kind = "ok"
    if ($s.state -ne "sync") { $kind = "warn" }
    Say $s.verdict $kind
    Set-Busy $false
}

# Read once, the count would be a lie a minute later - so it is re-read on a
# timer. Only that one call, never the whole state, and never while a download
# or an install is running. A server that did not answer is asked less often
# afterwards: two seconds of dead air every twenty seconds is a window that
# stutters for no reason.
$script:Poll = New-Object System.Windows.Forms.Timer
$script:Poll.Interval = 20000
$script:Poll.Add_Tick({
    if ($script:Busy -or -not $script:State) { return }
    $info = Get-ServerInfo $script:RevivalUrl
    if ($info.ok) {
        $script:Poll.Interval = 20000
        $script:State.server  = $info
        $script:State.players = $info.players
        if ($info.seats -gt 0) { $script:State.seats = $info.seats }
    } else {
        $script:Poll.Interval = 60000
        $script:State.players = -1
    }
    Update-Header $script:State
})

function Use-Server($h) {
    $h = ("" + $h).Trim()
    if (-not $h) { return }
    if ($h -eq $script:ServerChoice) { return }
    $script:ServerChoice = $h
    Say ""
    Say ("Master server is now " + $h + ". Install or Repair writes it into ClientConfig.ini; nothing changed in the game yet.")
    Refresh-All $true
}

$srvBox.Add_SelectedIndexChanged({ if (-not $script:Filling) { Use-Server $srvBox.SelectedItem } })
$srvBox.Add_Leave({ if (-not $script:Filling) { Use-Server $srvBox.Text } })
$srvBox.Add_KeyDown({ if ($_.KeyCode -eq "Return" -and -not $script:Filling) { Use-Server $srvBox.Text } })

$btnRefresh.Add_Click({
    Refresh-All $false
    Invoke-WeaponCheck $script:State
})

$btnInstall.Add_Click({
    if ($list.SelectedItems.Count -eq 0) { Say "Pick a version in the list first." "warn"; return }
    $v = $list.SelectedItems[0].Text
    if ($v -eq $script:State.installed) {
        $ans = [System.Windows.Forms.MessageBox]::Show(
            ("Version " + $v + " is already installed. Install it again anyway?"),
            "Already installed", "YesNo", "Question")
        if ($ans -ne "Yes") { return }
    }
    Set-Busy $true
    Invoke-Install $script:State $v | Out-Null
    Set-Progress 0
    Set-Busy $false
    Refresh-All $true
})

$btnRepair.Add_Click({
    Set-Busy $true
    Invoke-Repair $script:State
    Set-Busy $false
    Refresh-All $true
})

$btnCheck.Add_Click({
    Set-Busy $true
    Invoke-Check $script:State
    Set-Busy $false
})

$btnPlay.Add_Click({
    Set-Busy $true
    Invoke-Play $script:State
    Set-Busy $false
})

$btnVanilla.Add_Click({
    Set-Busy $true
    Invoke-PlayVanilla $script:State
    Set-Busy $false
})

$list.Add_DoubleClick({ $btnInstall.PerformClick() })

$form.Add_Shown({
    Update-Layout
    Say "Next Day: Revival launcher"
    Say ("Releases: github.com/" + $Repo) "dim"
    Refresh-All $true

    # The logo comes out of the installation the first refresh just found, so
    # it is fetched after that refresh and not before.
    $bmp = Get-GameLogo $script:State.game
    if ($bmp) { $logo.Image = $bmp }
    $ico = Get-GameIcon $script:State.game
    if ($ico) { $form.Icon = $ico }

    Invoke-WeaponCheck $script:State
    $script:Poll.Start()
})

$form.Add_FormClosed({ try { $script:Poll.Stop() } catch {} })

[void]$form.ShowDialog()
