# What Cairn adds to Kingdom Come: Together

Cairn's mod for Kingdom Come: Deliverance II is Kingdom Come: Together (see `UPSTREAM.md`) plus the pieces below. The launcher
that installs and starts it is a separate project (https://github.com/Cairn-client/launcher); a mod release publishes a pack
(`Cairn-Pack-kcd2-X.Y.Z.zip`) and `pack-index.json`, and the launcher updates this mod on its own.

| Piece | State |
| --- | --- |
| The pack for the Cairn launcher (`tools/Build-Pack.ps1`) | done |
| Mod menu (our module; no "teleport players to the host" button; the F3 option is gone) | planned |
| Save system (the host's world is sent to a joiner, the host keeps every joiner's character) | planned |
| Shared cutscenes | planned |
| Log collection: the host's one toggle collects every player's logs, the joiner's included | planned |
| Players on each other's map (upstream's own setting, on by default) | planned |

Versions: this mod has its own, starting at 0.1.0 (`VERSION`). Two players need the same MAJOR.MINOR.
