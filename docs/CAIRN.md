# What Cairn adds to Kingdom Come: Together

Cairn's mod for Kingdom Come: Deliverance II is Kingdom Come: Together (see `UPSTREAM.md`) plus the pieces below. The launcher
that installs and starts it is a separate project (https://github.com/Cairn-client/launcher); a mod release publishes a pack
(`Cairn-Pack-kcd2-X.Y.Z.zip`) and `pack-index.json`, and the launcher updates this mod on its own.

Our code lives in files of its own (`kdcmp_cairn_*.lua` beside `kdcmp.lua`, `*Cairn*.cs`) so a merge of a new upstream version shows
what we changed. Where a file of upstream's had to be edited, the edit is marked `CAIRN:`.

| Piece | State |
| --- | --- |
| The pack for the Cairn launcher (`tools/Build-Pack.ps1`) | done |
| The launcher draws the mod menu (`kdcmp_cairn_menu.lua`); the menu has no teleport-the-partner row and no F3 row | done |
| Players on each other's map (upstream's own pin; the menu row "Partner on the map" is on by default) | done (upstream's) |
| Log collection: the host's one row "Collect everyone's logs" (`mp_collect_logs`) takes every player's logs, the joiner's included (`kdcmp_cairn_logs.lua`, `GameBridge.CairnLogs.cs`, `ProtocolCairnLogs.cs`, join wire 0x76/0x77) | done |
| Shared cutscenes (`kdcmp_cairn_cutscene.lua`, `GameBridge.CairnCutscene.cs`): the joiner holds its input and plays the host's scene | done, untried in the game |
| Save system: the host keeps every joiner's character (`GameBridge.CairnHenry.cs`, `HenryStore.Cairn.cs`, join wire 0x78/0x79); the world fallback is not ported (upstream's per-host worlds replace it) | done, untried in the game |

Versions: this mod has its own, starting at 0.1.0 (`VERSION`). Two players need the same MAJOR.MINOR.

## What a release publishes for the launcher

Beside the pack and `pack-index.json`, every release carries `game.json` and its pictures (from `game/`): what the launcher shows for this game --
name, Steam ids, what it requires, the "What the mod does" card (`docs/CATALOG.md` in the launcher repository). Change the card by editing `game/game.json`;
`tools/Test-GameJson.ps1` checks it (CI runs it). `RELEASE_CHANNEL` (`stable` or `beta`) says how the next tagged release is published: `beta` makes it
a GitHub pre-release, which only players who chose the beta channel are offered. Set it back to `stable` for the release after.
