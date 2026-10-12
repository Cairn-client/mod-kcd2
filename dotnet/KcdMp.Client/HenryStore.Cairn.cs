// Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using System.Security.Cryptography;

namespace KcdMp.Client;

// Cairn: the host keeps every joiner's character (GameBridge.CairnHenry.cs). The two things the store needs for it.
public sealed partial class HenryStore
{
    /// <summary>The snapshot file's bytes (a Henry block), as stored.</summary>
    public byte[] ReadRaw(Snapshot s) => File.ReadAllBytes(Path.Combine(WorldDir(s.Tag), s.File));

    /// <summary>
    /// A snapshot that came over the wire, kept under its OWN name (so its time and its pair with a host save never change). Checked
    /// like <see cref="Store"/> (a full parse, written .part + rename, read back). Returns the snapshot when it is new here, null when
    /// this world already has it (or it was pruned away as older than the newest <see cref="KeepPerWorld"/>). Throws when it does not parse.
    /// </summary>
    public Snapshot? Import(string tag, string fileName, byte[] block, string? chestJson, string detail)
    {
        CheckTag(tag);
        var m = SnapRx.Match(fileName);
        if (!m.Success) throw new ArgumentException("not a snapshot name: " + fileName);
        if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
            throw new ArgumentException("bad snapshot time: " + fileName);
        WhsSave.ParseBlock(block);   // throws if it would not load later
        lock (_gate)
        {
            var dir = WorldDir(tag);
            string final = Path.Combine(dir, fileName);
            if (File.Exists(final)) return null;
            var all = Snapshots(tag);
            if (all.Count >= KeepPerWorld && t <= all[KeepPerWorld - 1].TakenUtc) return null;   // older than everything kept
            Directory.CreateDirectory(dir);
            string part = final + ".part";
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write))
            {
                fs.Write(block);
                fs.Flush(true);
            }
            File.Move(part, final, overwrite: true);
            if (!SHA256.HashData(File.ReadAllBytes(final)).AsSpan().SequenceEqual(SHA256.HashData(block)))
            {
                TryDelete(final);
                throw new IOException("the snapshot does not read back as written");
            }
            var snap = new Snapshot(tag, fileName, m.Groups[2].Value, t, m.Groups[3].Value, block.Length);
            if (chestJson is { Length: > 0 })
            {
                var p = ChestLedgerPath(snap);
                File.WriteAllText(p + ".part", chestJson);
                File.Move(p + ".part", p, overwrite: true);
            }
            UpdateWorld(tag, w =>
            {
                w.FirstSource ??= snap.Source;
                w.Pairs[fileName] = detail;
                if (w.LastJoinedUtc < t) w.LastJoinedUtc = t;
            });
            _log($"MP-HENRY world {tag}: imported snapshot {snap.Short} ({block.Length} B, parse ok){(detail != "" ? " -- " + detail : "")}");
            Prune(tag);
            return snap;
        }
    }
}
