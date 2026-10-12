// Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace KcdMp.Wire;

// ---------------------------------------------------------------------------
// the host keeps every joiner's character.
//
// A joiner's Henry snapshots (HenryStore .hblk files) used to live on the joiner's machine only: a wiped
// %LOCALAPPDATA%, a missed pair or a host rollback and the joiner was a "first join" again. Now every snapshot the
// joiner stores is also sent to the host, which keeps it per player name and per world; when that player is back
// (same name), the host sends the snapshot paired with its own branch (and the newest) before anything is asked.
// One message rides the world transfer join channel (same header, same routing, a row in Protocol.JoinWire;
// "Either": a joiner's goes to the host (target 0xFF), the host's to one joiner by id):
//
//   type up/down  name         up body (after [target:1][joinId:4])
//   0x78 / 0x79   HenryBackup  [kind:1][xfer:4][data...]
//
// Kinds (APPEND-ONLY), xfer = the sender's transfer id:
//   1 Offer   (either)          [seed:4][size:4][chunkCount:4][sha256:32][name:1..HenryNameMax]   one snapshot follows
//   2 Chunk   (either)          [index:4][data:1..HenryChunkMax]                                  in index order (TCP)
//   3 Forget  (joiner -> host)  [seed:4]     the player starts a new character in that world: drop the backup
//   4 Stored  (host -> joiner)  [result:1]   0 kept, 1 refused, 2 the host has no start save of that playstyle (the answer to a FreshAsk)
//   5 FreshAsk (joiner -> host) [styleLen:1][style ASCII: soldier|adviser|scout]   a new character with that playstyle, built from the host's own start save
//       The host answers with ONE Offer/Chunk transfer whose name is snap-<time>-<md5>-fresh.hblk and whose seed is Protocol.HenryFreshSeed(style):
//       the bytes are the start save file itself ([hblkLen:4][the .whs][no chest ledger]); the joiner keeps it as that playstyle's start save and
//       joins from it, so a joiner needs no start saves of its own. Or with a Stored result 2.
// The transferred bytes: [hblkLen:4][hblk][chest ledger json, UTF-8, may be empty]. The name is the snapshot's own
// file name (snap-<yyyyMMddHHmmssfff>-<md5>-<source>.hblk), kept as is on both sides so a pair never changes.
// ---------------------------------------------------------------------------

public static partial class Protocol
{
    public const byte HenryBackupUp = 0x78, HenryBackupDown = 0x79;   // the host keeps every joiner's character

    public const int HenryChunkMax = 16 * 1024;
    public const int HenryNameMax = 72;
    /// <summary>One snapshot + its chest ledger bigger than this is refused (a late-game Henry block is well under 1 MB).</summary>
    public const int HenryMaxBytes = 4 * 1024 * 1024;
    /// <summary>A pause between chunks: the relay's per-client queue holds 512 KB.</summary>
    public const int HenryChunkPauseMs = 15;

    public const byte HenryOffer = 1, HenryChunk = 2, HenryForget = 3, HenryStored = 4, HenryFreshAsk = 5;
    public const byte HenryStoredOk = 0, HenryStoredRefused = 1, HenryStoredNoStartSave = 2;

    /// <summary>The playstyles of a start save, in the order a Fresh transfer's seed numbers them.</summary>
    public static readonly string[] HenryFreshStyles = { "soldier", "adviser", "scout" };
    private const uint HenryFreshSeedBase = 0xCA1B0000;
    /// <summary>The seed field of a Fresh transfer: a marker and the playstyle (never a world's real seed).</summary>
    public static uint HenryFreshSeed(string style) => HenryFreshSeedBase + (uint)(Array.IndexOf(HenryFreshStyles, style) + 1);
    /// <summary>The playstyle a Fresh transfer's seed names, or null when the seed is a world's.</summary>
    public static string? HenryFreshStyleOf(uint seed) => seed > HenryFreshSeedBase && seed <= HenryFreshSeedBase + HenryFreshStyles.Length ? HenryFreshStyles[seed - HenryFreshSeedBase - 1] : null;

    public const int HenryFixedLen = 1 + 4;                               // [kind][xfer]
    public const int HenryOfferFixedLen = HenryFixedLen + 4 + 4 + 4 + 32; // + the name
    public const int HenryBodyMax = HenryFixedLen + 4 + HenryChunkMax;    // the chunk is the biggest

    public static string HenryKindName(byte k) => k switch
    {
        HenryOffer => "offer", HenryChunk => "chunk", HenryForget => "forget", HenryStored => "stored", HenryFreshAsk => "fresh-ask", _ => $"unknown-{k}",
    };
}

/// <summary>One HenryBackup message: the body after the join header.</summary>
public readonly record struct HenryBackupMsg(byte Kind, uint Xfer, byte[] Data)
{
    private static readonly Regex NameRx = new(@"^snap-\d{17}-[0-9a-f]{32}-[a-z]+\.hblk$", RegexOptions.CultureInvariant);

    public static bool ValidName(string n) => n.Length <= Protocol.HenryNameMax && NameRx.IsMatch(n);

    public byte[] BuildUp(byte target)
    {
        if (Data.Length > Protocol.HenryBodyMax - Protocol.HenryFixedLen) throw new ArgumentOutOfRangeException(nameof(Data), $"henry data {Data.Length}");
        int len = Protocol.JoinHeaderLen + Protocol.HenryFixedLen + Data.Length;
        var p = new byte[3 + len];
        p[0] = Protocol.HenryBackupUp;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), (ushort)len);
        p[3] = target;                         // joinId [4..7] = 0
        p[8] = Kind;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(9), Xfer);
        Data.CopyTo(p, 13);
        return p;
    }

    public static bool TryDecode(ReadOnlySpan<byte> body, out HenryBackupMsg m)
    {
        m = default;
        if (body.Length < Protocol.HenryFixedLen || body.Length > Protocol.HenryBodyMax) return false;
        m = new HenryBackupMsg(body[0], BinaryPrimitives.ReadUInt32LittleEndian(body[1..]), body[Protocol.HenryFixedLen..].ToArray());
        return true;
    }

    public static HenryBackupMsg Offer(uint xfer, uint seed, int size, int chunks, ReadOnlySpan<byte> sha256, string name)
    {
        if (!ValidName(name)) throw new ArgumentException("not a snapshot name: " + name, nameof(name));
        var nb = Encoding.ASCII.GetBytes(name);
        var d = new byte[4 + 4 + 4 + 32 + nb.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(d, seed);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), size);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(8), chunks);
        sha256[..32].CopyTo(d.AsSpan(12));
        nb.CopyTo(d, 44);
        return new HenryBackupMsg(Protocol.HenryOffer, xfer, d);
    }

    public static bool TryOffer(ReadOnlySpan<byte> data, out uint seed, out int size, out int chunks, out byte[] sha256, out string name)
    {
        seed = 0; size = chunks = 0; sha256 = Array.Empty<byte>(); name = "";
        if (data.Length < 44 + 1 || data.Length > 44 + Protocol.HenryNameMax) return false;
        seed = BinaryPrimitives.ReadUInt32LittleEndian(data);
        size = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        chunks = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        if (size <= 0 || size > Protocol.HenryMaxBytes || chunks <= 0 || chunks != (size + Protocol.HenryChunkMax - 1) / Protocol.HenryChunkMax) return false;
        sha256 = data.Slice(12, 32).ToArray();
        name = Encoding.ASCII.GetString(data[44..]);
        return ValidName(name);
    }

    public static HenryBackupMsg Chunk(uint xfer, int index, ReadOnlySpan<byte> data)
    {
        var d = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(d, index);
        data.CopyTo(d.AsSpan(4));
        return new HenryBackupMsg(Protocol.HenryChunk, xfer, d);
    }

    public static bool TryChunk(byte[] data, out int index, out byte[] bytes)
    {
        index = 0; bytes = Array.Empty<byte>();
        if (data.Length < 5 || data.Length > 4 + Protocol.HenryChunkMax) return false;
        index = BinaryPrimitives.ReadInt32LittleEndian(data);
        bytes = data.AsSpan(4).ToArray();
        return index >= 0;
    }

    public static HenryBackupMsg Forget(uint seed)
    {
        var d = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(d, seed);
        return new HenryBackupMsg(Protocol.HenryForget, 0, d);
    }

    public static HenryBackupMsg FreshAsk(string style)
    {
        if (Array.IndexOf(Protocol.HenryFreshStyles, style) < 0) throw new ArgumentException("not a playstyle: " + style, nameof(style));
        var sb = Encoding.ASCII.GetBytes(style);
        var d = new byte[1 + sb.Length];
        d[0] = (byte)sb.Length;
        sb.CopyTo(d, 1);
        return new HenryBackupMsg(Protocol.HenryFreshAsk, 0, d);
    }

    public static bool TryFreshAsk(byte[] data, out string style)
    {
        style = "";
        if (data.Length < 2 || data[0] != data.Length - 1) return false;
        string s = Encoding.ASCII.GetString(data, 1, data.Length - 1);
        if (Array.IndexOf(Protocol.HenryFreshStyles, s) < 0) return false;
        style = s;
        return true;
    }

    /// <summary>The name of a Fresh transfer (it must pass <see cref="ValidName"/>): the time, the start save's md5, and the source "fresh".</summary>
    public static string FreshName(DateTime utc, ReadOnlySpan<byte> md5) =>
        $"snap-{utc:yyyyMMddHHmmssfff}-{Convert.ToHexString(md5).ToLowerInvariant()}-fresh.hblk";

    /// <summary>The transferred bytes: [hblkLen:4][hblk][chest ledger json].</summary>
    public static byte[] Pack(byte[] hblk, string? chestJson)
    {
        var cj = chestJson is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(chestJson);
        var o = new byte[4 + hblk.Length + cj.Length];
        BinaryPrimitives.WriteInt32LittleEndian(o, hblk.Length);
        hblk.CopyTo(o, 4);
        cj.CopyTo(o, 4 + hblk.Length);
        return o;
    }

    public static bool TryUnpack(byte[] all, out byte[] hblk, out string? chestJson)
    {
        hblk = Array.Empty<byte>(); chestJson = null;
        if (all.Length < 4) return false;
        int n = BinaryPrimitives.ReadInt32LittleEndian(all);
        if (n <= 0 || n > all.Length - 4) return false;
        hblk = all.AsSpan(4, n).ToArray();
        if (all.Length > 4 + n) chestJson = Encoding.UTF8.GetString(all, 4 + n, all.Length - 4 - n);
        return true;
    }

    /// <summary>The folder name of a player on the host: letters, digits, '-' and '_' only, lower case (never a path).</summary>
    public static string PlayerKey(string? name)
    {
        var sb = new StringBuilder();
        foreach (char c in (name ?? "").Trim())
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_') sb.Append(char.ToLowerInvariant(c));
        if (sb.Length == 0) return "";
        return sb.Length > 40 ? sb.ToString(0, 40) : sb.ToString();
    }
}
