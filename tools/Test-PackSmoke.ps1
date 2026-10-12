# Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
<#
.SYNOPSIS
    Starts the programs of the pack that Build-Pack.ps1 just staged (release\pack-stage\bin) and checks that they come up.

.DESCRIPTION
    The agent, the relay and the bridge share one folder, and files that one project publishes can replace another's (the 0.2.1 beta shipped a
    bridge that overwrote System.Text.Json, and the relay died at start). So the staged relay is started on a spare port and must still be
    running a few seconds later, and, when the pack has one, the bridge must start and end by itself when it is given no launcher.
#>
[CmdletBinding()]
param([string] $Stage = (Join-Path (Split-Path $PSScriptRoot -Parent) 'release\pack-stage'), [int] $Port = 17778, [int] $InfoPort = 15273)

$ErrorActionPreference = 'Stop'
$bin = Join-Path $Stage 'bin'
$relay = Join-Path $bin 'KcdMpServer.exe'
if (-not (Test-Path $relay)) { throw "no relay in $bin" }

$out = [IO.Path]::GetTempFileName(); $err = [IO.Path]::GetTempFileName()
$p = Start-Process -FilePath $relay -ArgumentList "--port $Port" -WorkingDirectory $bin -RedirectStandardOutput $out -RedirectStandardError $err -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Seconds 8
    if ($p.HasExited) {
        Write-Host (Get-Content $err -Raw)
        throw "the relay of the pack exited at start (code $($p.ExitCode)): a file of the pack is wrong or missing"
    }
    Write-Host "relay: still running after 8 s"
} finally {
    if (-not $p.HasExited) { $p.Kill() }
    Remove-Item $out, $err -ErrorAction SilentlyContinue
}

$bridge = Join-Path $bin 'CairnBridgeKcd2.exe'
if (Test-Path $bridge) {
    $b = Start-Process -FilePath $bridge -WorkingDirectory $bin -PassThru -WindowStyle Hidden
    if (-not $b.WaitForExit(15000)) { $b.Kill(); throw 'the bridge did not end by itself when given no launcher' }
    if ($b.ExitCode -ne 2) { throw "the bridge exited with code $($b.ExitCode); 2 (no launcher pipe given) was expected" }
    Write-Host 'bridge: starts and ends as designed'
}
Write-Host 'pack smoke test: OK'
