# Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
<#
.SYNOPSIS
    Checks game/game.json, the file the Cairn launcher reads to know what this game is (name, Steam ids, requirements, what the mod does),
    and the pictures it names. The release publishes them beside the mod pack; the launcher refuses a game.json it does not understand
    and keeps the one it had, so a mistake here only means the change does not arrive.
.DESCRIPTION
    The rules are those of Cairn.Setup/Catalog.cs in the launcher repository (plain ids, plain picture file names, short texts).
    RELEASE_CHANNEL is also checked: "stable" (the release is the latest one) or "beta" (it is published as a pre-release, which only
    players who chose the beta channel are offered).
#>
[CmdletBinding()]
param([string] $Root = '')
if (-not $Root) { $Root = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..' }
$ErrorActionPreference = 'Stop'
$errors = New-Object System.Collections.Generic.List[string]
function Fail([string] $m) { $script:errors.Add($m) }
function Items($x) { if ($null -eq $x) { , @() } else { , @($x) } }

$game = Join-Path $Root 'game'
$path = Join-Path $game 'game.json'
if (-not (Test-Path $path)) { throw 'game/game.json is missing' }
if ((Get-Item $path).Length -gt 256KB) { Fail 'game.json is larger than 256 KB' }
$m = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json

if ($m.schema -ne 1) { Fail 'schema must be 1' }
if ($m.id -notmatch '^[A-Za-z0-9_-]{1,40}$') { Fail 'id is not a plain id' }
if (-not $m.name) { Fail 'no name' }
if ($m.accent -and $m.accent -notmatch '^#[0-9A-Fa-f]{6}$') { Fail 'bad accent' }
if ($m.exe -and ($m.exe -notmatch '^[A-Za-z0-9._ -]{1,80}$' -or $m.exe.Contains('..'))) { Fail 'bad exe' }

$pics = @()
if ($m.images) {
    foreach ($k in 'background', 'logo', 'hero', 'poster') { if ($m.images.$k) { $pics += $m.images.$k } }
    $pics += (Items $m.images.screenshots)
}
foreach ($pic in $pics) {
    # release assets are flat: a picture is a plain file name beside game.json
    if ($pic -notmatch '^[A-Za-z0-9_.-]+$' -or $pic.Contains('..') -or @('.jpg', '.jpeg', '.png', '.webp') -notcontains ([IO.Path]::GetExtension($pic).ToLowerInvariant())) { Fail "'$pic' is not a plain picture file name"; continue }
    $fp = Join-Path $game $pic
    if (-not (Test-Path $fp)) { Fail "game.json names $pic, which is not in game/"; continue }
    if ((Get-Item $fp).Length -gt 3MB) { Fail "$pic is larger than 3 MB" }
}
foreach ($grp in (Items $m.features)) {
    if (-not $grp.title) { Fail 'a feature group has no title' }
    foreach ($f in (Items $grp.items)) {
        if (-not $f.name) { Fail 'a feature has no name' }
        if ($f.status -and @('ready', 'inTest', 'planned') -notcontains $f.status) { Fail "feature status '$($f.status)'" }
    }
}
$channel = (Get-Content (Join-Path $Root 'RELEASE_CHANNEL') -Raw).Trim()
if (@('stable', 'beta') -notcontains $channel) { Fail "RELEASE_CHANNEL is '$channel' (stable or beta)" }

if ($errors.Count -gt 0) { $errors | % { Write-Host "FAIL  $_" -ForegroundColor Red }; exit 1 }
Write-Host "OK    game.json: $($m.name), $($pics.Count) picture(s), release channel $channel" -ForegroundColor Green
