# WO-176 — the 0.50.0 installer record (local, unsigned, release candidate)

Built unattended 2026-10-12 from a fresh clone of `f00aef7` (`release\c0500`, git-ignored), with the three start saves copied in (SHA-1
equal to the source copies), the Modding Tools game running with a throwaway `playline4` save loaded (so the live save round trip ran):
`tools\Build-Installer.ps1 -SoakWaiver "the maintainer's standing rule, quoted in the WO-176 run prompt (2026-10-11): 'Ends in a
release-candidate installer, always (soak waived under the standing rule, quoted and dated)'; built unattended 2026-10-12"`. Exit 0.

| | |
|---|---|
| file | `release\KingdomComeTogether-Setup-0.50.0.exe` (local only; nothing uploaded) |
| size | 106,949,330 bytes |
| sha256 | `9e4ee30fc730d3b6093a8b8f2f2f1766949c62920762596fa6e7225541b1c021` |
| header version | **0.50.0** (checked: the compile line `KCDMP.iss (version 0.50.0)` and the exe's ProductVersion / FileVersion) |
| signed | no (no signing settings on this machine) |
| soak | waived, as quoted above (`release\SOAK-WAIVED-0.50.0.txt`); the same-machine frame A/B against 0.49.2 is in the findings |

The first build attempt (from `85fed25`) stopped at the Lua gate: `Test-WO175Synthetic` L7 counted one timer after a load; since 0.50.0
the same hook re-arms WO-176's figure check as well (an intended change). The check was updated (`f00aef7`), all 61 suites were run
locally, and the build was redone from a new clone.

## The build's own gates

| gate | 0.49.2 | 0.50.0 |
|---|---|---|
| relay | 63 | **63** |
| agent (`KcdMp.Client.Tests`) | 1,503 | **1,512** (+9: `Wo176Tests`) |
| setup | 77 | **77** |
| log bundle | 14 | **14** |
| Lua synthetic suites | 60 / 4,017 checks | **61 / 4,049 checks**, 0 failed (new `Test-WO176Synthetic`, 32) |
| static checks (console placeholders, Lua locals, native guards, WO-157) | green | **green** |
| save tools (`test_whs_repair.py`, `test_whs_dump.py`) | 14 + 7 | **14 + 7** |
| save round trip, live (WO-175 S5) | SKIPPED (no game) | **PASS** (the game up: draw → save → health → reload → draw) |
| native | 9,318 | **9,364** (+46: `wo176_tests`) |
| payload smoke | ok | **ok** (`RELAY-SMOKE ok protocol=v10 release=0.50.0`) |
| no-input-tooling (WO-175) | 0 hits | **0 hits** over 1,036 payload files (the Microsoft .NET Desktop runtime exemptions unchanged, listed by name) |
| installer detection / the four cases | 26/26, 41/41 | **26/26, 41/41** |
| guards | 7/7 | **7/7** |
