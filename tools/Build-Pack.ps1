# Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
<#
.SYNOPSIS
    Builds the mod pack the Cairn launcher downloads for Kingdom Come: Deliverance II: release\Cairn-Pack-kcd2-<version>.zip
    and release\pack-index.json (what the launcher reads to see that a newer pack exists).

.DESCRIPTION
    One pack is the whole game-specific side of a session, so a mod updates on its own, without a new launcher:

      mod\   the mod folder, put into <Modding Tools>\Mods\<modFolder>\ (mod.manifest and Data\kdcmp.pak, and nothing else:
             loose sources in a mod folder stop the game, "114 tables are not loaded")
      bin\   the programs that run a session, kept in the launcher's pack store and started from there:
             the agent, the relay, the native plugin, the menu logo pak (self-contained .NET beside them)
      pack.json      what the launcher needs to know to drive them (names, the Lua prefix, the compatibility rule)
      manifest.txt   MOD|<path>|<size>|<sha256> and BIN|<path>|<size>|<sha256>, one line per file

    The version is the VERSION file (strict X.Y.Z); the upstream version this tree is based on is UPSTREAM.md's first line.

.PARAMETER NoBridge
    Leave the menu bridge out. By default the pack carries it (CairnBridgeKcd2.exe, dotnet\KcdMp.CairnBridge) and names it as the pack's companion: the launcher
    then draws the mod menu from the bridge (docs/BRIDGE.md in the launcher repository), and falls back to the log and console if it does not connect.
    A pack without it needs launcher 0.3.7 or newer; one with it, 0.3.10.

.PARAMETER SkipBuild
    Pack what release\pack-stage already holds (iterating on this script only).
#>
[CmdletBinding()]
param([switch] $SkipBuild, [switch] $NoBridge)
$Bridge = -not $NoBridge

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content (Join-Path $root 'VERSION') -TotalCount 1).Trim()
if ($version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw "VERSION must be strict X.Y.Z, got '$version'" }
$upstream = ((Get-Content (Join-Path $root 'UPSTREAM.md') -TotalCount 1) -replace '[^0-9.]', '').Trim('.')
$release = Join-Path $root 'release'
$stage = Join-Path $release 'pack-stage'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not $SkipBuild) {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'mod\Data'), (Join-Path $stage 'bin') | Out-Null

    # the pak, rebuilt from the sources (it is not byte-deterministic: the shipped file is what gets hashed below)
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-And-Install-Mod.ps1') -NoInstall
    if ($LASTEXITCODE -ne 0) { throw 'Build-And-Install-Mod.ps1 failed' }
    Copy-Item (Join-Path $root 'kdcmp\mod.manifest') (Join-Path $stage 'mod\mod.manifest')
    Copy-Item (Join-Path $root 'kdcmp\Data\kdcmp.pak') (Join-Path $stage 'mod\Data\kdcmp.pak')

    # the agent and the relay, self-contained, into one folder (a later publish overwrites shared dlls with identical copies)
    $projects = @('dotnet\KcdMp.Client\KcdMp.Client.csproj', 'dotnet\KcdMp.Server\KcdMp.Server.csproj')
    if ($Bridge) { $projects += 'dotnet\KcdMp.CairnBridge\KcdMp.CairnBridge.csproj' }
    foreach ($p in $projects) {
        & dotnet publish (Join-Path $root $p) -c Release -p:PublishProfile=FolderProfile --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "publish failed: $p" }
        $published = Join-Path (Split-Path (Join-Path $root $p) -Parent) 'bin\Release\net8.0\publish'
        if ($p -like '*CairnBridge*') {
            # the bridge shares this folder with the agent and the relay, which carry newer copies of shared dlls (System.Text.Json 10): it must be ONE file,
            # or it overwrites them and the relay no longer starts (0.2.1 shipped that way)
            $files = @(Get-ChildItem $published -File)
            if ($files.Count -ne 1 -or $files[0].Name -ne 'CairnBridgeKcd2.exe') { throw "the bridge must publish as one file, CairnBridgeKcd2.exe; got: $($files.Name -join ', ')" }
        }
        Copy-Item (Join-Path $published '*') (Join-Path $stage 'bin') -Recurse -Force
    }

    # the native plugin (always rebuilt: a stale one beside a fresh agent has no handshake to catch it)
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'native\Build-Native.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'native build failed' }
    Copy-Item (Join-Path $root 'native\build\KCDMP\KCDMP.dll') (Join-Path $stage 'bin\KCDMP.dll') -Force
    Remove-Item (Join-Path $stage 'bin\KCDMP_LauncherInjector.exe') -Force -ErrorAction SilentlyContinue

    # the logo the launcher puts on the game's main menu (stored entries, like kdcmp.pak)
    $dds = Join-Path $root 'docs\branding\pak-source\Libs\UI\Textures\KCDLogo.dds'
    if (Test-Path $dds) {
        $brand = Join-Path $stage 'bin\kdcmp_brand.pak'
        $bz = [System.IO.Compression.ZipFile]::Open($brand, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $be = $bz.CreateEntry('Libs/UI/Textures/KCDLogo.dds', [System.IO.Compression.CompressionLevel]::NoCompression)
            $bs = $be.Open(); $bb = [System.IO.File]::ReadAllBytes($dds); $bs.Write($bb, 0, $bb.Length); $bs.Close()
        } finally { $bz.Dispose() }
    }
}

function Get-Sha256([string] $Path) { (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$agentName = 'KcdMpClient.exe'; $relayName = 'KcdMpServer.exe'; $nativeName = 'KCDMP.dll'
foreach ($n in @("bin\$agentName", "bin\$relayName", "bin\$nativeName", 'mod\mod.manifest', 'mod\Data\kdcmp.pak')) {
    if (-not (Test-Path (Join-Path $stage $n))) { throw "the pack is missing $n" }
}

# the menu bridge, when this pack carries one (a stage built without -NoBridge): the launcher then talks to the mod's menu through it and needs a launcher that speaks the bridge
$bridgeName = 'CairnBridgeKcd2.exe'
$hasBridge = Test-Path (Join-Path $stage "bin\$bridgeName")
$launcherMin = if ($hasBridge) { '0.3.10' } else { '0.3.7' }

# the launcher's contract with this mod. The compatibility rule: two players need the same MAJOR.MINOR of the pack.
$packJson = [ordered]@{
    schema      = 2
    id          = 'kcd2'
    game        = 'kcd2'
    version     = $version
    upstream    = $upstream
    modFolder   = 'kdcmp'
    luaPrefix   = 'KCD2MP_'
    agent       = "bin/$agentName"
    relay       = "bin/$relayName"
    native      = "bin/$nativeName"
    brandPak    = 'bin/kdcmp_brand.pak'
    agentConfig = 'kcdmp-client.json'
    launcherMin = $launcherMin
    lua         = [ordered]@{   # how the launcher talks to the mod through the game's console and log
        eventPrefix         = '[KCD2-MP-EVT] v1 '
        startGameEvent      = 'w159'
        startGameModel      = 'KCD2MP_W159Model'
        startGameTick       = 'KCD2MP_W159Tick'
        startGameRecapStart = 'KCD2MP_W159RecapStart'
        startGameDisarm     = 'KCD2MP_W159Disarm'
        joinFlowLoad        = 'KCD2MP_Wo124LoadGame'
        menuRequest         = 'KCD2MP_MenuRequest'
        helperFlag          = '--w159'   # the agent's one-shot helper for the menu's worlds and start saves, and the tag of its answer
        helperTag           = 'W159'
        nativeLog           = 'kcdmp-native.log'   # the plugin's own log beside its DLL: the launcher reads it to see the plugin attached
    }
    replacesFolders = @('cairn')   # the earlier Cairn mod's folder: the launcher removes it when it is provably ours (two mods would load twice)
}
# the same program also does the game's options (DLSS 5): launcher 0.3.14 shows them; an older launcher keeps its own DLSS 5 and ignores this
if ($hasBridge) { $packJson.companion = "bin/$bridgeName"; $packJson.optionsProgram = "bin/$bridgeName"; $packJson.setup = $true }
[IO.File]::WriteAllText((Join-Path $stage 'pack.json'), (($packJson | ConvertTo-Json) + "`n"), (New-Object System.Text.UTF8Encoding($false)))

$lines = New-Object System.Collections.Generic.List[string]
foreach ($kind in @('mod', 'bin')) {
    $dir = Join-Path $stage $kind
    foreach ($f in Get-ChildItem $dir -Recurse -File | Sort-Object FullName) {
        $rel = $f.FullName.Substring($dir.Length + 1)
        $lines.Add(('{0}|{1}|{2}|{3}' -f $kind.ToUpperInvariant(), $rel, $f.Length, (Get-Sha256 $f.FullName)))
    }
}
[IO.File]::WriteAllText((Join-Path $stage 'manifest.txt'), (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

$zipName = "Cairn-Pack-kcd2-$version.zip"
$zipPath = Join-Path $release $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$fs = [IO.File]::Open($zipPath, 'Create')
try {
    $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in Get-ChildItem $stage -Recurse -File) {
            $rel = $f.FullName.Substring($stage.Length + 1) -replace '\\', '/'
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $zip.Dispose() }
} finally { $fs.Dispose() }

$index = [ordered]@{
    schema   = 2
    id       = 'kcd2'
    game     = 'kcd2'
    version  = $version
    upstream = $upstream
    file     = $zipName
    sha256   = Get-Sha256 $zipPath
    size     = (Get-Item $zipPath).Length
    launcherMin = $launcherMin
}
[IO.File]::WriteAllText((Join-Path $release 'pack-index.json'), (($index | ConvertTo-Json) + "`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "pack: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB), version $version, based on upstream $upstream"
