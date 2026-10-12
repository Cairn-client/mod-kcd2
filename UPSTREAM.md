0.48.6

# Where this mod comes from

This is the Cairn mod for Kingdom Come: Deliverance II. It is a fork of **Kingdom Come: Together** by DeepFriedDepp and its
contributors (https://github.com/DeepFriedDepp/KingdomCome-Together, GPLv3), and it stays GPLv3. The first line above is the
upstream version this tree is based on; every release note names it.

Branches:

- `upstream`: a mirror of the upstream repository's `main`. Never edited here; only fast-forwarded.
- `main`: ours. It starts at upstream's tag `v0.48.6` and adds Cairn's own modules.

Taking a new upstream version: merge its tag into a working branch, resolve the conflicts, run every gate, update the first
line of this file, release. Our own code lives in separate files where it can; where a file of upstream's had to be edited the
edit is marked `CAIRN:` so the next merge shows exactly what we changed.

What Cairn adds or replaces (kept up to date as it lands): see `docs/CAIRN.md`.
