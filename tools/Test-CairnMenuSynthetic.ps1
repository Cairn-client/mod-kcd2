# Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
<#
.SYNOPSIS
    Synthetic test for the Cairn module that lets the launcher draw the mod menu (kdcmp_cairn_menu.lua). No game, relay or agent needed.
.DESCRIPTION
    The real kdcmp.lua with the module joined after it (the game loads the two files in that order), under MoonSharp, engine stubbed:
    the same driver and stubs as Test-WO154Synthetic.ps1. The scenario is Test-CairnMenuSynthetic.part2.lua behind the stub part of
    Test-WO154Synthetic.lua.
#>
[CmdletBinding()]
param([string] $MoonSharpVersion = '2.0.0.0')

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$startup = Join-Path $toolsDir '..\kdcmp\Data\Scripts\Startup'
$work = Join-Path $env:TEMP ('cairn-menu-synth-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    # the mod, joined as the game loads it: kdcmp.lua, then the module
    $joined = (Get-Content (Join-Path $startup 'kdcmp.lua') -Raw) + "`n" + (Get-Content (Join-Path $startup 'kdcmp_cairn_menu.lua') -Raw) + "`n" + (Get-Content (Join-Path $startup 'kdcmp_cairn_notice.lua') -Raw) + "`n" + (Get-Content (Join-Path $startup 'kdcmp_cairn_logs.lua') -Raw) + "`n" + (Get-Content (Join-Path $startup 'kdcmp_cairn_cutscene.lua') -Raw)
    $modPath = Join-Path $work 'joined.lua'
    [IO.File]::WriteAllText($modPath, $joined, (New-Object System.Text.UTF8Encoding($false)))

    # the stub part of the WO-154 scenario (up to and including the splice marker), then ours
    $base = (Get-Content (Join-Path $toolsDir 'Test-WO154Synthetic.lua') -Raw)
    $marker = '-- @@KDCMP@@'
    $cut = $base.IndexOf($marker)
    if ($cut -lt 0) { throw 'the stub scenario has no splice marker' }
    $scenario = $base.Substring(0, $cut + $marker.Length) + "`n" + (Get-Content (Join-Path $toolsDir 'Test-CairnMenuSynthetic.part2.lua') -Raw)
    $scenarioPath = Join-Path $work 'scenario.lua'
    [IO.File]::WriteAllText($scenarioPath, $scenario, (New-Object System.Text.UTF8Encoding($false)))

    & (Join-Path $toolsDir 'Test-NpcSmoothSynthetic.ps1') -KdcmpLua $modPath -MoonSharpVersion $MoonSharpVersion `
        -Scenario $scenarioPath -Title 'Cairn module: the launcher draws the mod menu'
    $code = $LASTEXITCODE
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
exit $code
