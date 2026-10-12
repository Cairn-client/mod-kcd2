// Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports.
using System.Security.Cryptography;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

/// <summary>The host collects everyone's logs: the message, its row in the join wire, the bundle's chunks.</summary>
public class LogsTests
{
    [Fact]
    public void TheLogMessageIsARowOfTheJoinWireEitherWayWithRoomForAFullChunk()
    {
        var row = Protocol.JoinWireFor(Protocol.LogUp);
        Assert.NotNull(row);
        Assert.Equal(Protocol.LogDown, row!.Value.Down);
        Assert.Equal(Protocol.JoinFrom.Either, row.Value.From);
        var request = LogMsg.Request(7).BuildUp(3);
        Assert.InRange(request.Length - 3, row.Value.Min, row.Value.Max);
        var chunk = LogMsg.Chunk(7, 0, new byte[Protocol.LogChunkMax]).BuildUp(Protocol.JoinTargetHost);
        Assert.InRange(chunk.Length - 3, row.Value.Min, row.Value.Max);
        var offer = LogMsg.Offer(7, 100, 1, new byte[32], new string('a', Protocol.LogNameMax)).BuildUp(Protocol.JoinTargetHost);
        Assert.InRange(offer.Length - 3, row.Value.Min, row.Value.Max);
    }

    [Fact]
    public void ARequestAndItsAnswersRoundTrip()
    {
        var up = LogMsg.Request(0xABCD).BuildUp(5);
        Assert.Equal(Protocol.LogUp, up[0]);
        Assert.Equal(5, up[3]);                                         // the target
        Assert.True(Protocol.TrySplitJoinDown(Down(up, source: 1), out byte src, out _, out _, out var body));
        Assert.Equal(1, src);
        Assert.True(LogMsg.TryDecode(body, out var m));
        Assert.Equal(Protocol.LogRequest, m.Kind);
        Assert.Equal(0xABCDu, m.Req);

        var sha = SHA256.HashData(new byte[] { 1, 2, 3 });
        var offer = LogMsg.Offer(9, 20_000, 2, sha, "Zipette Babinks/..");
        Assert.True(LogMsg.TryOffer(offer.Data, out int size, out int chunks, out var s2, out string name));
        Assert.Equal((20_000, 2), (size, chunks));
        Assert.Equal(sha, s2);
        Assert.Equal("ZipetteBabinks", name);                           // a name is letters, digits, - and _ only: never a path
        Assert.False(LogMsg.TryOffer(LogMsg.Offer(9, 20_000, 5, sha, "x").Data, out _, out _, out _, out _));      // chunk count must fit the size
        Assert.False(LogMsg.TryOffer(LogMsg.Offer(9, Protocol.LogMaxBytes + 1, 4000, sha, "x").Data, out _, out _, out _, out _));
        Assert.False(LogMsg.TryOffer(LogMsg.Offer(9, 0, 0, sha, "x").Data, out _, out _, out _, out _));

        var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var chunk = LogMsg.Chunk(9, 4, data);
        Assert.True(LogMsg.TryChunk(chunk.Data, out int index, out var bytes));
        Assert.Equal(4, index);
        Assert.Equal(data, bytes.ToArray());
        Assert.False(LogMsg.TryChunk(new byte[3], out _, out _));
        Assert.False(LogMsg.TryDecode(new byte[2], out _));
        Assert.False(LogMsg.TryDecode(new byte[Protocol.LogBodyMax + 1], out _));
    }

    [Fact]
    public void ABundleInChunksIsRebuiltByteForByteAndItsHashChecks()
    {
        var zip = new byte[Protocol.LogChunkMax * 3 + 123];
        new Random(5).NextBytes(zip);
        int chunks = (zip.Length + Protocol.LogChunkMax - 1) / Protocol.LogChunkMax;
        Assert.Equal(4, chunks);
        var sha = SHA256.HashData(zip);
        Assert.True(LogMsg.TryOffer(LogMsg.Offer(1, zip.Length, chunks, sha, "p").Data, out int size, out int n, out var h, out _));
        var rebuilt = new MemoryStream();
        for (int i = 0; i < n; i++)
        {
            int at = i * Protocol.LogChunkMax;
            var c = LogMsg.Chunk(1, i, zip.AsSpan(at, Math.Min(Protocol.LogChunkMax, zip.Length - at)));
            Assert.True(LogMsg.TryChunkBytes(c.Data, out int idx, out var b));
            Assert.Equal(i, idx);
            rebuilt.Write(b);
        }
        Assert.Equal(size, rebuilt.Length);
        Assert.True(SHA256.HashData(rebuilt.ToArray()).AsSpan().SequenceEqual(h));
    }

    [Theory]
    [InlineData("Zipette Babinks", "ZipetteBabinks")]
    [InlineData("../../etc/passwd", "etcpasswd")]
    [InlineData("", "player")]
    [InlineData("!!!", "player")]
    [InlineData("a_b-c", "a_b-c")]
    public void AFileNamePartIsNeverAPath(string input, string expected) => Assert.Equal(expected, LogMsg.SafeName(input));

    // what the relay does to an up packet: the payload [target:1][joinId:4][body] arrives as [source:1][target:1][joinId:4][body]
    private static byte[] Down(byte[] up, byte source)
    {
        var payload = new byte[up.Length - 3 + 1];
        payload[0] = source;
        Array.Copy(up, 3, payload, 1, up.Length - 3);
        return payload;
    }
}

public class CairnLogRoundsTests
{
    [Fact]
    public void OnlyTheNewestRoundsAreKeptAndOnlyTimestampFoldersCount()
    {
        var names = new[] { "20261001-100000", "20261003-100000", "20261002-100000", "notes", "20261004-100000", "20260930-235959" };
        var old = GameBridge.CairnLogsRoundsToPrune(names, 3);
        Assert.Equal(new[] { "20261001-100000", "20260930-235959" }, old.OrderByDescending(x => x, StringComparer.Ordinal));
        Assert.Empty(GameBridge.CairnLogsRoundsToPrune(names, 12));
    }

    [Fact]
    public void ThePrunedFolderNamesAreNeverAPath()
    {
        var old = GameBridge.CairnLogsRoundsToPrune(new[] { @"..\..\evil", "20261001-100000", "20261002-100000" }, 1);
        Assert.Equal(new[] { "20261001-100000" }, old);
    }
}
