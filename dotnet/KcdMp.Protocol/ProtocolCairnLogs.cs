// Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Text;

namespace KcdMp.Wire;

// ---------------------------------------------------------------------------
// the host collects everyone's logs.
//
// The host asks, each player's agent zips its own logs (the agent's, the game's, the plugin's) and sends the zip back in chunks, and the
// host's agent saves them in one folder. One message rides the world transfer join channel (same header, same routing, a row in
// Protocol.JoinWire; "Either": a joiner's goes to the host (target 0xFF), the host's to one joiner by id):
//
//   type up/down  name  up body (after [target:1][joinId:4])
//   0x76 / 0x77   Logs  [kind:1][req:4][data...]
//
// Kinds (APPEND-ONLY), req = the host's request id:
//   1 Request  (host -> joiner)    no data
//   2 Offer    (joiner -> host)    [size:4][chunkCount:4][sha256:32][name:1..LogNameMax]   the zip that follows
//   3 Chunk    (joiner -> host)    [index:4][data:1..LogChunkMax]                           in index order; TCP keeps the order
//   4 Done     (host -> joiner)    [result:1]  0 saved, 1 refused (bad hash, too big, unknown request)
//   5 Decline  (joiner -> host)    [reason:1]  1 the player does not share their logs, 2 nothing to send, 3 busy
// ---------------------------------------------------------------------------

public static partial class Protocol
{
    public const byte LogUp = 0x76, LogDown = 0x77;   // collecting logs (Cairn)

    public const int LogChunkMax = 16 * 1024;
    public const int LogNameMax = 40;
    /// <summary>A bundle bigger than this is refused at the offer (the zip of the logs is a few MB).</summary>
    public const int LogMaxBytes = 48 * 1024 * 1024;
    /// <summary>One chunk, then this long a pause: the relay's queue to the host holds 512 KB, so a slow link is never outrun.</summary>
    public const int LogChunkPauseMs = 30;

    public const byte LogRequest = 1, LogOffer = 2, LogChunk = 3, LogDone = 4, LogDecline = 5;
    public const byte LogDoneSaved = 0, LogDoneRefused = 1;
    public const byte LogDeclineNotShared = 1, LogDeclineNothing = 2, LogDeclineBusy = 3;

    public const int LogFixedLen = 1 + 4;                                  // [kind][req]
    public const int LogOfferFixedLen = LogFixedLen + 4 + 4 + 32;          // + the name
    public const int LogBodyMax = LogOfferFixedLen + LogNameMax > LogFixedLen + 4 + LogChunkMax ? LogOfferFixedLen + LogNameMax : LogFixedLen + 4 + LogChunkMax;

    public static string LogKindName(byte k) => k switch
    {
        LogRequest => "request", LogOffer => "offer", LogChunk => "chunk", LogDone => "done", LogDecline => "decline", _ => $"unknown-{k}",
    };
}

/// <summary>One Logs message: the body after the join header.</summary>
public readonly record struct LogMsg(byte Kind, uint Req, byte[] Data)
{
    public byte[] BuildUp(byte target)
    {
        if (Data.Length > Protocol.LogBodyMax - Protocol.LogFixedLen) throw new ArgumentOutOfRangeException(nameof(Data), $"log data {Data.Length}");
        int len = Protocol.JoinHeaderLen + Protocol.LogFixedLen + Data.Length;
        var p = new byte[3 + len];
        p[0] = Protocol.LogUp;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), (ushort)len);
        p[3] = target;                         // joinId [4..7] = 0
        p[8] = Kind;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(9), Req);
        Data.CopyTo(p, 13);
        return p;
    }

    public static bool TryDecode(ReadOnlySpan<byte> body, out LogMsg m)
    {
        m = default;
        if (body.Length < Protocol.LogFixedLen || body.Length > Protocol.LogBodyMax) return false;
        m = new LogMsg(body[0], BinaryPrimitives.ReadUInt32LittleEndian(body[1..]), body[Protocol.LogFixedLen..].ToArray());
        return true;
    }

    public static LogMsg Request(uint req) => new(Protocol.LogRequest, req, Array.Empty<byte>());

    public static LogMsg Offer(uint req, int size, int chunks, ReadOnlySpan<byte> sha256, string name)
    {
        var nb = Encoding.ASCII.GetBytes(SafeName(name));
        var d = new byte[4 + 4 + 32 + nb.Length];
        BinaryPrimitives.WriteInt32LittleEndian(d, size);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), chunks);
        sha256[..32].CopyTo(d.AsSpan(8));
        nb.CopyTo(d, 40);
        return new LogMsg(Protocol.LogOffer, req, d);
    }

    public static bool TryOffer(ReadOnlySpan<byte> data, out int size, out int chunks, out byte[] sha256, out string name)
    {
        size = chunks = 0; sha256 = Array.Empty<byte>(); name = "";
        if (data.Length < 4 + 4 + 32 + 1 || data.Length > 4 + 4 + 32 + Protocol.LogNameMax) return false;
        size = BinaryPrimitives.ReadInt32LittleEndian(data);
        chunks = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if (size <= 0 || size > Protocol.LogMaxBytes || chunks <= 0 || chunks != (size + Protocol.LogChunkMax - 1) / Protocol.LogChunkMax) return false;
        sha256 = data.Slice(8, 32).ToArray();
        name = Encoding.ASCII.GetString(data[40..]);
        return name == SafeName(name);
    }

    public static LogMsg Chunk(uint req, int index, ReadOnlySpan<byte> data)
    {
        var d = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(d, index);
        data.CopyTo(d.AsSpan(4));
        return new LogMsg(Protocol.LogChunk, req, d);
    }

    public static bool TryChunk(ReadOnlySpan<byte> data, out int index, out ReadOnlySpan<byte> bytes)
    {
        index = 0; bytes = default;
        if (data.Length < 5 || data.Length > 4 + Protocol.LogChunkMax) return false;
        index = BinaryPrimitives.ReadInt32LittleEndian(data);
        bytes = data[4..];
        return index >= 0;
    }

    public static bool TryChunkBytes(byte[] data, out int index, out byte[] bytes)
    {
        bool ok = TryChunk(data, out index, out var span);
        bytes = ok ? span.ToArray() : Array.Empty<byte>();
        return ok;
    }

    /// <summary>A file name part: letters, digits, '-' and '_' only (never a path), 1..LogNameMax.</summary>
    public static string SafeName(string? name)
    {
        var sb = new StringBuilder();
        foreach (char c in name ?? "")
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
        if (sb.Length == 0) sb.Append("player");
        return sb.Length > Protocol.LogNameMax ? sb.ToString(0, Protocol.LogNameMax) : sb.ToString();
    }
}
