// Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using KcdMp.Wire;

namespace KcdMp.Client;

/// <summary>
/// the host keeps every joiner's character (ProtocolHenryBackup.cs).
///
/// Joiner: every snapshot it stores (each host world save's pair, the join save's pair) is also sent to the host.
/// A first join (Bring / a new character) tells the host to forget the old one first; so does mp_henry_reset.
/// When the host sends a character back, it is imported into this machine's Henry files (same name, same pair)
/// -- unless the player chose "a new character" this session -- and the usual restore picks from the union.
///
/// Host: keeps them under &lt;data&gt;/hosted-henry/&lt;player name&gt;/&lt;world&gt;/ (a <see cref="HenryStore"/> each, the same
/// 100-per-world rule). When a player with that name is here, the host sends the snapshot paired with its own branch and
/// the newest one: once on connect (before the joiner decides anything) and again right before every world offer.
/// </summary>
public partial class GameBridge
{
    private readonly ConcurrentDictionary<string, HenryStore> _cairnHostedHenry = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<byte, string> _cairnHenryPushed = new();   // host: ghost -> what was last pushed to it
    private readonly Dictionary<(byte Src, uint Xfer), CairnHenryRx> _cairnHenryRx = new();
    private readonly object _cairnHenryRxGate = new();
    private readonly SemaphoreSlim _cairnHenrySendGate = new(1, 1);

    private sealed class CairnHenryRx
    {
        public uint Seed;
        public int Size, Chunks, Next;
        public byte[] Sha = Array.Empty<byte>();
        public string Name = "";
        public readonly MemoryStream Data = new();
        public DateTime SinceUtc = DateTime.UtcNow;
    }

    private static string CairnHostedHenryRoot => Path.Combine(Path.GetDirectoryName(HenryStore.DefaultRoot())!, "hosted-henry");

    /// <summary>The host's store of one player's characters (null when the name gives no folder).</summary>
    private HenryStore? CairnHostedHenryFor(string? playerName)
    {
        string key = HenryBackupMsg.PlayerKey(playerName);
        if (key.Length == 0) return null;
        return _cairnHostedHenry.GetOrAdd(key, k => new HenryStore(Path.Combine(CairnHostedHenryRoot, k), HenryStore.ClockFromEnv));
    }

    // ---------------------------------------------------------------- sending one snapshot

    private async Task CairnHenrySendSnapshotAsync(byte target, uint seed, HenryStore store, HenryStore.Snapshot s, string why)
    {
        byte[] all;
        try { all = HenryBackupMsg.Pack(store.ReadRaw(s), store.LoadChestLedger(s)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine($"MP-HENRY backup: {s.Short} not readable ({ex.Message}) -- not sent"); return; }
        if (all.Length > Protocol.HenryMaxBytes) { Console.WriteLine($"MP-HENRY backup: {s.Short} is {all.Length} B, over the limit -- not sent"); return; }
        await CairnHenrySendBytesAsync(target, seed, s.File, all, $"snapshot {s.Short}", why);
    }

    /// <summary>One transfer (an offer, then its chunks) of already packed bytes to the host or to one joiner.</summary>
    private async Task CairnHenrySendBytesAsync(byte target, uint seed, string name, byte[] all, string what, string why)
    {
        int chunks = (all.Length + Protocol.HenryChunkMax - 1) / Protocol.HenryChunkMax;
        uint xfer = (uint)Random.Shared.Next(1, int.MaxValue);
        await _cairnHenrySendGate.WaitAsync(_wo122Ct);
        try
        {
            await WriteJoinAsync(HenryBackupMsg.Offer(xfer, seed, all.Length, chunks, SHA256.HashData(all), name).BuildUp(target));
            for (int i = 0; i < chunks; i++)
            {
                int at = i * Protocol.HenryChunkMax;
                await WriteJoinAsync(HenryBackupMsg.Chunk(xfer, i, all.AsSpan(at, Math.Min(Protocol.HenryChunkMax, all.Length - at))).BuildUp(target));
                if (i + 1 < chunks) await Task.Delay(Protocol.HenryChunkPauseMs, _wo122Ct);
            }
            Console.WriteLine($"MP-HENRY backup: sent {what} ({all.Length} B, {chunks} chunk(s)) to {(target == Protocol.JoinTargetHost ? "the host" : $"ghost {target}")} -- {why}");
        }
        finally { _cairnHenrySendGate.Release(); }
    }

    // ---------------------------------------------------------------- joiner: up to the host

    /// <summary>A snapshot was just stored on this joiner: the host keeps a copy (off the caller's path).</summary>
    private void CairnHenryBackupUpload(HenryStore.Snapshot s, string why)
    {
        if (_isDamageAuthority || !_peerSeedKnown || _wo122Stream is null) return;
        uint seed = _peerSeed;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, _wo122Ct);   // its chest ledger is written right after the snapshot
                await CairnHenrySendSnapshotAsync(Protocol.JoinTargetHost, seed, _henry, s, why);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"MP-HENRY backup: snapshot {s.Short} not sent to the host ({ex.GetType().Name}: {ex.Message}) -- it is still kept here"); }
        });
    }

    /// <summary>The player starts a new character in the host's world (a first join, mp_henry_reset): the host drops the old one.</summary>
    private void CairnHenryBackupForget(string why)
    {
        if (_isDamageAuthority || !_peerSeedKnown || _wo122Stream is null) return;
        uint seed = _peerSeed;
        _ = Task.Run(async () =>
        {
            try
            {
                await _cairnHenrySendGate.WaitAsync(_wo122Ct);
                try { await WriteJoinAsync(HenryBackupMsg.Forget(seed).BuildUp(Protocol.JoinTargetHost)); }
                finally { _cairnHenrySendGate.Release(); }
                Console.WriteLine($"MP-HENRY backup: told the host to forget this player's character in world {WhsSave.SeedTag(seed)} -- {why}");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"MP-HENRY backup: forget not sent ({ex.GetType().Name})"); }
        });
    }

    // ---------------------------------------------------------------- host: down to a joiner

    /// <summary>
    /// The host sends one player its character for the host's current world: the snapshot paired with the newest save
    /// of the host's branch that has one, and the newest snapshot (one file when they are the same). Skipped when the
    /// same files were already pushed to that peer, unless <paramref name="force"/>.
    /// </summary>
    private async Task CairnHenryBackupPushAsync(byte g, bool force, string why)
    {
        if (!_isDamageAuthority || !_sharedWorld || _hostWorldSeed is not uint seed || _hostWorldHenry != true) return;
        if (!_ghostNames.TryGetValue(g, out var name) || CairnHostedHenryFor(name) is not { } store) return;
        string tag = WhsSave.SeedTag(seed);
        try
        {
            var snaps = store.Snapshots(tag);
            if (snaps.Count == 0) return;
            var send = new List<HenryStore.Snapshot>();
            if (store.PickFor(tag, BranchNewestFirst()) is { } pick) send.Add(pick.Snapshot);
            if (!send.Any(x => x.File == snaps[0].File)) send.Add(snaps[0]);
            string key = HenryBackupMsg.PlayerKey(name) + "|" + tag + "|" + string.Join(",", send.Select(x => x.File));
            if (!force && _cairnHenryPushed.TryGetValue(g, out var was) && was == key) return;
            _cairnHenryPushed[g] = key;
            foreach (var s in send)
                await CairnHenrySendSnapshotAsync(g, seed, store, s, $"{name}'s character kept by the host ({why})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Console.WriteLine($"MP-HENRY backup: {name}'s character not sent ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // ---------------------------------------------------------------- a new character from the host's start save
    //
    // "Join with a new character" with a playstyle needs that playstyle's start save. A joiner who has none (nothing ships them to a
    // player) asks the host, whose own New adventure needs them anyway: the host sends the file as a Fresh transfer, the joiner keeps it
    // as its copy and joins from it exactly as from a start save of its own. The joiner's character then lives on the host like any other.

    private string? _freshAskedStyle;          // joiner: the playstyle already asked for (once per answer)
    private bool _freshAwaitingHost;           // joiner: the file has been asked for and has not come
    private bool _freshHostHasNone;            // joiner: the host answered that it has no start save of that playstyle

    /// <summary>Where a start save the host sent is kept on this machine (a copy of the host's: never the launcher's own folder).</summary>
    internal static string CairnHostFreshRoot() => Path.Combine(Path.GetDirectoryName(HenryStore.DefaultRoot())!, "host-start-saves");

    /// <summary>Joiner: the host's copy of a playstyle's start save, when it already came (null otherwise).</summary>
    internal static string? CairnHostFreshFile(string style)
    {
        string file = Path.Combine(CairnHostFreshRoot(), style + ".whs");
        return File.Exists(file) ? file : null;
    }

    /// <summary>Joiner: asks the host for that playstyle's start save, once. False when the ask could not go out.</summary>
    private bool CairnFreshAsk(string style)
    {
        if (_isDamageAuthority || _wo122Stream is null) return false;
        if (_freshAskedStyle == style && (_freshAwaitingHost || _freshHostHasNone)) return true;
        _freshAskedStyle = style;
        _freshAwaitingHost = true;
        _freshHostHasNone = false;
        _ = Task.Run(async () =>
        {
            try
            {
                await _cairnHenrySendGate.WaitAsync(_wo122Ct);
                try { await WriteJoinAsync(HenryBackupMsg.FreshAsk(style).BuildUp(Protocol.JoinTargetHost)); }
                finally { _cairnHenrySendGate.Release(); }
                Console.WriteLine($"MP-HENRY fresh: no {style} start save here -- asked the host for its own");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _freshAwaitingHost = false;
                Console.WriteLine($"MP-HENRY fresh: the ask for the {style} start save was not sent ({ex.GetType().Name})");
            }
        });
        return true;
    }

    /// <summary>Host: one joiner wants a new character of a playstyle: this host's own start save of it goes down, or "none".</summary>
    private async Task CairnFreshAnswerAsync(byte g, string style)
    {
        string who = _ghostNames.TryGetValue(g, out var n) ? n : $"ghost {g}";
        try
        {
            if (Wo159Rules.StartSaveRoot() is not string root || Wo159.StyleFile(root, style) is not string file)
            {
                Console.WriteLine($"MP-HENRY fresh: {who} asked for a {style} start save and this host has none");
                await WriteJoinAsync(new HenryBackupMsg(Protocol.HenryStored, 0, new[] { Protocol.HenryStoredNoStartSave }).BuildUp(g));
                return;
            }
            byte[] bytes = WhsSave.ReadShared(file);
            var v = WhsSave.Verify(bytes);
            var all = HenryBackupMsg.Pack(bytes, null);
            if (!v.Ok || all.Length > Protocol.HenryMaxBytes)
            {
                Console.WriteLine($"MP-HENRY fresh: this host's {style} start save cannot be sent ({(v.Ok ? $"{all.Length} B" : v.Reason)})");
                await WriteJoinAsync(new HenryBackupMsg(Protocol.HenryStored, 0, new[] { Protocol.HenryStoredNoStartSave }).BuildUp(g));
                return;
            }
            string name = HenryBackupMsg.FreshName(DateTime.UtcNow, MD5.HashData(bytes));
            await CairnHenrySendBytesAsync(g, Protocol.HenryFreshSeed(style), name, all, $"the {style} start save", $"{who} wants a new {style} character");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Console.WriteLine($"MP-HENRY fresh: the {style} start save could not be sent to {who} ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>Joiner: the host's start save came whole: kept as this playstyle's copy, and the join goes on from it.</summary>
    private void CairnFreshStartSaveReceived(string style, byte[] bytes)
    {
        try
        {
            var v = WhsSave.Verify(bytes);
            if (!v.Ok) { Console.WriteLine($"MP-HENRY fresh: the host's {style} start save is not a save ({v.Reason}) -- dropped"); _freshAwaitingHost = false; _freshHostHasNone = true; return; }
            var c = WhsSave.Inflate(bytes);
            if (!WhsSave.PlayerOf(c.Raw).IsHenry) { Console.WriteLine($"MP-HENRY fresh: the host's {style} start save has no Henry in it -- dropped"); _freshAwaitingHost = false; _freshHostHasNone = true; return; }
            Directory.CreateDirectory(CairnHostFreshRoot());
            string file = Path.Combine(CairnHostFreshRoot(), style + ".whs"), part = file + ".part";
            File.WriteAllBytes(part, bytes);
            File.Move(part, file, overwrite: true);
            Console.WriteLine($"MP-HENRY fresh: kept the host's {style} start save ({bytes.Length} B) -- joining from it");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.WriteLine($"MP-HENRY fresh: the host's {style} start save could not be kept ({ex.GetType().Name}: {ex.Message})");
            _freshHostHasNone = true;
        }
        _freshAwaitingHost = false;
        _autoNextUtc = DateTime.UtcNow;   // the join is looked at again now
    }

    /// <summary>Joiner: the host has no start save of that playstyle.</summary>
    private void CairnFreshHostHasNone()
    {
        Console.WriteLine($"MP-HENRY fresh: the host has no {_freshAskedStyle} start save");
        _freshAwaitingHost = false;
        _freshHostHasNone = true;
        _autoNextUtc = DateTime.UtcNow;
    }

    // ---------------------------------------------------------------- frames in

    private async Task CairnHenryBackupOnFrameAsync(byte src, byte[] body)
    {
        if (!HenryBackupMsg.TryDecode(body, out var m)) { Console.WriteLine($"MP-HENRY backup: a malformed message from ghost {src} (len={body.Length}) dropped"); return; }
        try
        {
            switch (m.Kind)
            {
                case Protocol.HenryOffer:
                {
                    if (!HenryBackupMsg.TryOffer(m.Data, out uint seed, out int size, out int chunks, out byte[] sha, out string name))
                    {
                        Console.WriteLine($"MP-HENRY backup: a bad offer from ghost {src} dropped");
                        return;
                    }
                    lock (_cairnHenryRxGate)
                    {
                        // a transfer that never finished is dropped after a minute
                        foreach (var k in _cairnHenryRx.Where(kv => (DateTime.UtcNow - kv.Value.SinceUtc).TotalSeconds > 60).Select(kv => kv.Key).ToList()) _cairnHenryRx.Remove(k);
                        _cairnHenryRx[(src, m.Xfer)] = new CairnHenryRx { Seed = seed, Size = size, Chunks = chunks, Sha = sha, Name = name };
                    }
                    return;
                }
                case Protocol.HenryChunk:
                {
                    CairnHenryRx? rx;
                    lock (_cairnHenryRxGate) _cairnHenryRx.TryGetValue((src, m.Xfer), out rx);
                    if (rx is null || !HenryBackupMsg.TryChunk(m.Data, out int index, out byte[] bytes) || index != rx.Next) return;   // TCP keeps the order: anything else is stray
                    if (rx.Data.Length + bytes.Length > rx.Size) { lock (_cairnHenryRxGate) _cairnHenryRx.Remove((src, m.Xfer)); return; }
                    rx.Data.Write(bytes);
                    rx.Next++;
                    if (rx.Next < rx.Chunks) return;
                    lock (_cairnHenryRxGate) _cairnHenryRx.Remove((src, m.Xfer));
                    var all = rx.Data.ToArray();
                    if (all.Length != rx.Size || !SHA256.HashData(all).AsSpan().SequenceEqual(rx.Sha) || !HenryBackupMsg.TryUnpack(all, out var hblk, out var chest))
                    {
                        Console.WriteLine($"MP-HENRY backup: {rx.Name} from ghost {src} did not arrive intact -- dropped");
                        if (_isDamageAuthority) await WriteJoinAsync(new HenryBackupMsg(Protocol.HenryStored, m.Xfer, new[] { Protocol.HenryStoredRefused }).BuildUp(src));
                        return;
                    }
                    if (_isDamageAuthority) await CairnHenryBackupHostKeepAsync(src, m.Xfer, rx.Seed, rx.Name, hblk, chest);
                    else if (Protocol.HenryFreshStyleOf(rx.Seed) is string freshStyle) CairnFreshStartSaveReceived(freshStyle, hblk);
                    else CairnHenryBackupJoinerImport(rx.Seed, rx.Name, hblk, chest);
                    return;
                }
                case Protocol.HenryForget when _isDamageAuthority:
                {
                    if (m.Data.Length < 4 || !_ghostNames.TryGetValue(src, out var name) || CairnHostedHenryFor(name) is not { } store) return;
                    string tag = WhsSave.SeedTag(BitConverter.ToUInt32(m.Data, 0));
                    bool had = store.DeleteWorld(tag, $"{name} starts a new character in this world");
                    _cairnHenryPushed.TryRemove(src, out _);
                    Console.WriteLine($"MP-HENRY backup: {name} starts over in world {tag} -- the host's copy {(had ? "deleted" : "was empty")}");
                    return;
                }
                case Protocol.HenryFreshAsk when _isDamageAuthority:
                    if (HenryBackupMsg.TryFreshAsk(m.Data, out string askedStyle)) await CairnFreshAnswerAsync(src, askedStyle);
                    else Console.WriteLine($"MP-HENRY fresh: a malformed ask from ghost {src} dropped");
                    return;
                case Protocol.HenryStored when !_isDamageAuthority && m.Data.Length > 0 && m.Data[0] == Protocol.HenryStoredNoStartSave:
                    CairnFreshHostHasNone();
                    return;
                case Protocol.HenryStored when !_isDamageAuthority:
                    if (m.Data.Length > 0 && m.Data[0] != Protocol.HenryStoredOk) Console.WriteLine("MP-HENRY backup: the host could NOT keep a snapshot (it is still kept here)");
                    return;
                default:
                    Console.WriteLine($"MP-HENRY backup: {Protocol.HenryKindName(m.Kind)} from ghost {src}: not for this role, dropped");
                    return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Console.WriteLine($"MP-HENRY backup: {Protocol.HenryKindName(m.Kind)} from ghost {src}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Host: one joiner's snapshot arrived -- kept under that player's name.</summary>
    private async Task CairnHenryBackupHostKeepAsync(byte src, uint xfer, uint seed, string file, byte[] hblk, string? chest)
    {
        bool ok = false;
        string who = _ghostNames.TryGetValue(src, out var n) ? n : $"player {src}";
        try
        {
            if (CairnHostedHenryFor(n) is not { } store) { Console.WriteLine($"MP-HENRY backup: ghost {src} has no usable name -- its snapshot is not kept"); }
            else
            {
                string tag = WhsSave.SeedTag(seed);
                var snap = store.Import(tag, file, hblk, chest, $"kept for {who}");
                store.MarkJoined(tag);
                ok = true;
                if (snap is not null) Console.WriteLine($"MP-HENRY backup: the host keeps {who}'s character in world {tag}: {snap.Short} ({store.Snapshots(tag).Count} kept)");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Console.WriteLine($"MP-HENRY backup: {who}'s snapshot {file} NOT kept: {ex.GetType().Name}: {ex.Message}");
        }
        try { await WriteJoinAsync(new HenryBackupMsg(Protocol.HenryStored, xfer, new[] { ok ? Protocol.HenryStoredOk : Protocol.HenryStoredRefused }).BuildUp(src)); }
        catch (IOException) { }
    }

    /// <summary>Joiner: the host sent this player's character back -- imported into the Henry files (same name, same pair).</summary>
    private void CairnHenryBackupJoinerImport(uint seed, string file, byte[] hblk, string? chest)
    {
        string tag = Wo157TagFor(seed);
        if (_resetTag == tag) { Console.WriteLine($"MP-HENRY backup: the host's copy {file} not imported -- mp_henry_reset this session"); return; }
        if (CurrentChoice() == "fresh" && !_henry.HasWorld(tag))
        {
            Console.WriteLine($"MP-HENRY backup: the host's copy {file} not imported -- the player chose a new character");
            return;
        }
        bool hadWorld = _henry.HasWorld(tag);
        var snap = _henry.Import(tag, file, hblk, chest, "kept by the host");
        if (snap is null) return;   // already here
        Console.WriteLine($"MP-HENRY backup: imported {snap.Short} from the host into world {tag}{(hadWorld ? "" : " -- this player's character was missing here and is RESTORED from the host's copy")}");
        if (!hadWorld || _chooseAsked)
        {
            _chooseAsked = false;
            SetJoinUi("waiting", "Your character was found on your host. Joining...");
            _ = Wo125ToastAsync("Co-op: your character was restored from your host.");
        }
    }
}
