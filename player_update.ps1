# Trusted player entry point. A release ZIP is authenticated using the SHA-256
# digest returned by GitHub over HTTPS. Extracted caches are never trusted.
param([string]$Game = "", [string]$Server = "187.124.117.145",
      [switch]$OpenLauncher)

$ErrorActionPreference = "Stop"

function Get-NdrJson([string]$Url) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    return Invoke-RestMethod -Uri $Url -TimeoutSec 15 -Headers @{
        'User-Agent' = 'NextDayRevivalVerifiedLauncher'; Accept = 'application/vnd.github+json'
    }
}

function Assert-NdrChild([string]$Parent, [string]$Path) {
    $base = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the managed folder: $Path"
    }
    return $full
}

function Get-NdrRelease([string]$HostName) {
    if ($HostName -notmatch '^[A-Za-z0-9.-]+$') { throw 'Invalid master server address.' }
    $serverInfo = Get-NdrJson "http://${HostName}:12080/revival.json"
    $version = [string]$serverInfo.minClientVersion
    if ($version -notmatch '^\d+\.\d+\.\d+$' -or $serverInfo.contentVersion -ne $version) {
        throw 'The server has no unambiguous client release. Please try again after the server update.'
    }
    $release = Get-NdrJson "https://api.github.com/repos/FenjaminBranklin/Next-Day-Revival/releases/tags/v$version"
    if ($release.draft -or $release.prerelease -or $release.tag_name -ne "v$version") {
        throw 'The required stable release is not published yet.'
    }
    $assets = @($release.assets | Where-Object { $_.name -ceq "NextDayRevival_Client_$version.zip" })
    if ($assets.Count -ne 1 -or [string]$assets[0].digest -notmatch '^sha256:[0-9a-fA-F]{64}$') {
        throw 'GitHub has not provided a verifiable client package. Start is blocked; try again later.'
    }
    $asset = $assets[0]
    $expectedUrl = "https://github.com/FenjaminBranklin/Next-Day-Revival/releases/download/v$version/NextDayRevival_Client_$version.zip"
    if ($asset.browser_download_url -cne $expectedUrl) { throw 'Unexpected release download address.' }
    return @{ version = $version; hash = $asset.digest.Substring(7).ToLowerInvariant()
              url = $expectedUrl; size = [long]$asset.size }
}

function Expand-NdrRelease($Release, [string]$Cache) {
    New-Item -ItemType Directory -Force -Path $Cache | Out-Null
    $zip = Assert-NdrChild $Cache (Join-Path $Cache ($Release.hash + '.zip'))
    if (-not (Test-Path -LiteralPath $zip) -or
        (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ine $Release.hash) {
        $part = Assert-NdrChild $Cache (Join-Path $Cache ([guid]::NewGuid().ToString('N') + '.part'))
        try {
            Write-Host ('Downloading verified client ' + $Release.version + ' ...')
            Invoke-WebRequest -Uri $Release.url -OutFile $part -UseBasicParsing -TimeoutSec 180
            if ((Get-Item -LiteralPath $part).Length -ne $Release.size -or
                (Get-FileHash -LiteralPath $part -Algorithm SHA256).Hash -ine $Release.hash) {
                throw 'The download is incomplete or its checksum is wrong. Nothing was installed.'
            }
            Move-Item -LiteralPath $part -Destination $zip -Force
        } finally {
            if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }
        }
    }
    $session = Assert-NdrChild $Cache (Join-Path $Cache ([guid]::NewGuid().ToString('N')))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            $target = Assert-NdrChild $session (Join-Path $session $entry.FullName)
            if ($entry.FullName -match '(^|[/\\])(Assembly-CSharp\.dll|players\.json|[^/\\]*\.nd)$') {
                throw 'The release contains game code or a savegame.'
            }
        }
    } finally { $archive.Dispose() }
    $complete = $false
    try {
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $session)
    $source = Join-Path $session ('NextDayRevival_Client_' + $Release.version)
    foreach ($name in @('VERSION', 'launcher.ps1', 'player_update.ps1', 'client_patch.ps1',
                       'start_game.ps1', 'NextDayRevivalToolkit.dll', 't72_import.exe', 'steam_base.sha1',
                       'bepinex/winhttp.dll', 'bepinex/doorstop_config.ini', 'bepinex/BepInEx/core/BepInEx.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf)) {
            throw "The verified release is missing $name. No start is possible."
        }
    }
    if ((Get-Content -LiteralPath (Join-Path $source 'VERSION') -Raw).Trim() -cne $Release.version) {
        throw 'The package version does not match its release tag.'
    }
    $complete = $true
    return $source
    } finally {
        if (-not $complete -and (Test-Path -LiteralPath $session)) {
            $safeSession = Assert-NdrChild $Cache $session
            Remove-Item -LiteralPath $safeSession -Recurse -Force
        }
    }
}

function Get-NdrPayload([string]$Source) {
    $files = @(@{ source = (Join-Path $Source 'NextDayRevivalToolkit.dll')
                 relative = 'BepInEx/plugins/NextDayRevivalToolkit.dll' })
    $assets = Join-Path $Source 'assets'
    foreach ($file in Get-ChildItem -LiteralPath $assets -File -Recurse) {
        if ($file.Name -like '*_preview.png' -or $file.Name -eq 'icon_vergleich.png') { continue }
        $relative = $file.FullName.Substring($assets.Length).TrimStart('\', '/').Replace('\', '/')
        $files += @{ source = $file.FullName; relative = 'BepInEx/plugins/assets/' + $relative }
    }
    $loader = Join-Path $Source 'bepinex'
    foreach ($file in Get-ChildItem -LiteralPath $loader -File -Recurse) {
        $relative = $file.FullName.Substring($loader.Length).TrimStart('\', '/').Replace('\', '/')
        $files += @{ source = $file.FullName; relative = $relative }
    }
    return $files
}

function Assert-NdrInstalled([string]$GamePath, [string]$Source) {
    $lines = @()
    foreach ($plugin in Get-ChildItem -LiteralPath (Join-Path $GamePath 'BepInEx/plugins') -Filter '*.dll' -File -Recurse) {
        if ($plugin.FullName -ine [IO.Path]::GetFullPath((Join-Path $GamePath 'BepInEx/plugins/NextDayRevivalToolkit.dll'))) {
            throw ('Additional plugin found: ' + $plugin.Name + '. Move other plugins out of BepInEx/plugins before starting this client.')
        }
    }
    foreach ($file in Get-NdrPayload $Source) {
        $target = Assert-NdrChild $GamePath (Join-Path $GamePath $file.relative)
        $hash = (Get-FileHash -LiteralPath $file.source -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
            (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine $hash) {
            throw ('Installation is incomplete: ' + $file.relative + '. Close the game and try again.')
        }
        $lines += $file.relative + "`t" + $hash
    }
    return $lines
}

function Assert-NdrSteamBase([string]$GamePath, [string]$Source) {
    $lines = @()
    $count = 0
    foreach ($row in Get-Content -LiteralPath (Join-Path $Source 'steam_base.sha1')) {
        if ($row.StartsWith('#') -or -not $row.Trim()) { continue }
        $count++
        if ($count % 20 -eq 0) { Write-Host ('Checking Steam files: ' + $count + ' ...') }
        $fields = $row.Split("`t")
        if ($fields.Count -ne 2 -or $fields[1] -notmatch '^[0-9a-f]{40}$') { throw 'Invalid Steam baseline in the release.' }
        $path = Assert-NdrChild $GamePath (Join-Path $GamePath $fields[0])
        $before = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA1).Hash -ine $fields[1]) {
            throw ('Steam game files differ: ' + $fields[0] + '. Verify installed files in Steam, then run Launcher again. Local skin/map edits must be shipped as mod assets.')
        }
        $after = Get-Item -LiteralPath $path
        if ($before.Length -ne $after.Length -or $before.LastWriteTimeUtc.Ticks -ne $after.LastWriteTimeUtc.Ticks) {
            throw ('Steam changed a file during verification: ' + $fields[0] + '. Wait for Steam to finish and retry.')
        }
        # The child game inherits this fresh receipt. Runtime checks metadata
        # for these multi-GB Steam files, avoiding a second scan after the TCP
        # connection is opened. Mod payloads are hashed again before auth.
        $lines += $row + "`t" + $after.Length + "`t" + $after.LastWriteTimeUtc.Ticks
    }
    if ($lines.Count -eq 0) { throw 'Empty Steam baseline.' }
    return $lines
}

function Invoke-NdrPrepare([string]$GamePath, [string]$HostName) {
    if (Get-Process nextday_game -ErrorAction SilentlyContinue) { throw 'Close Next Day: Survival before updating.' }
    $GamePath = [IO.Path]::GetFullPath($GamePath)
    if (-not (Test-Path -LiteralPath (Join-Path $GamePath 'nextday_game.exe'))) { throw 'Game folder not found.' }
    $release = Get-NdrRelease $HostName
    $cache = Join-Path $env:LOCALAPPDATA 'NextDayRevival/verified'
    Write-Host ('Checking and repairing client ' + $release.version + ' ...')
    $source = Expand-NdrRelease $release $cache
    try {
        Write-Host 'Checking original Steam maps, textures and engine files ...'
        try { $baseLines = @(Assert-NdrSteamBase $GamePath $source) }
        catch {
            # Steam repairs its own content; no downloaded game files or old
            # developer backups are installed by this updater.
            Start-Process 'steam://validate/519190'
            throw
        }
        # Each extraction is fresh, including the extractor and all scripts.
        # This also regenerates the five game-derived T-72 files on this PC.
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $source 'client_patch.ps1') -Game $GamePath -Server $HostName -ResetConfig | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Client repair failed. The game was not started; see the repair output above.' }
        $lines = @(Assert-NdrInstalled $GamePath $source) + $baseLines

        # Old release assets are moved aside, never destroyed. A stray route
        # or model must not survive solely because the new ZIP no longer has it.
        $expected = @{}
        foreach ($file in Get-NdrPayload $source) { $expected[$file.relative.Replace('/', '\')] = $true }
        $assetDir = Join-Path $GamePath 'BepInEx/plugins/assets'
        $recovery = Join-Path $GamePath ('ndr-recovery/' + [guid]::NewGuid().ToString('N'))
        foreach ($file in Get-ChildItem -LiteralPath $assetDir -File -Recurse) {
            $relative = $file.FullName.Substring($GamePath.TrimEnd('\').Length).TrimStart('\')
            if ($expected.ContainsKey($relative)) { continue }
            $destination = Assert-NdrChild $recovery (Join-Path $recovery $relative)
            New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
            $oldPath = Assert-NdrChild $assetDir $file.FullName
            Move-Item -LiteralPath $oldPath -Destination $destination
        }
        # Include patched game code and loader/config files in the launch
        # receipt so a Steam update or a late write invalidates this launch.
        foreach ($relative in @('nextday_game_Data/Managed/Assembly-CSharp.dll',
            'nextday_game_Data/ClientConfig.ini')) {
            $path = Assert-NdrChild $GamePath (Join-Path $GamePath $relative)
            if (Test-Path -LiteralPath $path) {
                $lines += $relative + "`t" + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            } else { throw "Required installed file missing: $relative" }
        }
        # Re-query after installation: a deployment during a download is not
        # permission to start against the newly changed server.
        $after = Get-NdrRelease $HostName
        if ($after.version -cne $release.version -or $after.hash -cne $release.hash) {
            throw 'The server release changed during repair. Please press Play again.'
        }
        $receipt = Join-Path $cache ([guid]::NewGuid().ToString('N') + '.receipt')
        $header = "NDR1`t" + $release.version + "`t" + $GamePath
        [IO.File]::WriteAllLines($receipt, @($header) + $lines, (New-Object Text.UTF8Encoding($false)))
        return $receipt
    } finally {
        $session = Assert-NdrChild $cache (Split-Path $source -Parent)
        Remove-Item -LiteralPath $session -Recurse -Force
    }
}

# Dot-sourcing exposes the functions to start_game.ps1 and the offline tests.
if ($MyInvocation.InvocationName -ne '.') {
    try {
        if ($OpenLauncher) {
            $release = Get-NdrRelease $Server
            $source = Expand-NdrRelease $release (Join-Path $env:LOCALAPPDATA 'NextDayRevival/verified')
            try {
                $argsForLauncher = @{ ServerHost = $Server }
                if ($Game) { $argsForLauncher.Game = $Game }
                & (Join-Path $source 'launcher.ps1') @argsForLauncher
            } finally {
                $cache = Join-Path $env:LOCALAPPDATA 'NextDayRevival/verified'
                $session = Assert-NdrChild $cache (Split-Path $source -Parent)
                Remove-Item -LiteralPath $session -Recurse -Force
            }
        } else { Invoke-NdrPrepare $Game $Server }
    } catch {
        Write-Host ('Start blocked: ' + $_.Exception.Message) -ForegroundColor Red
        if ($OpenLauncher) {
            Add-Type -AssemblyName System.Windows.Forms
            [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Next Day Revival - update required') | Out-Null
        }
        exit 1
    }
}
