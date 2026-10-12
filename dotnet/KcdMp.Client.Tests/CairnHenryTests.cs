// Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Security.Cryptography;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

/// <summary>The host keeps every joiner's character: the messages and the store's import, on synthetic saves only.</summary>
public class CairnHenryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cairn-henry-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void TheBackupIsARowOfTheJoinWireEitherWay()
    {
        var row = Protocol.JoinWireFor(Protocol.HenryBackupUp);
        Assert.NotNull(row);
        Assert.Equal(Protocol.HenryBackupDown, row!.Value.Down);
        Assert.Equal(Protocol.JoinFrom.Either, row.Value.From);
        var chunk = HenryBackupMsg.Chunk(1, 0, new byte[Protocol.HenryChunkMax]).BuildUp(Protocol.JoinTargetHost);
        Assert.InRange(chunk.Length - 3, row.Value.Min, row.Value.Max);
        // the new types do not meet any other message's
        var ups = Protocol.JoinWire.Select(r => r.Up).ToList();
        Assert.Equal(ups.Count, ups.Distinct().Count());
        Assert.NotEqual(Protocol.LogUp, Protocol.HenryBackupUp);
        Assert.NotEqual(Protocol.W164Up, Protocol.HenryBackupUp);
    }

    [Theory]
    [InlineData("soldier")]
    [InlineData("adviser")]
    [InlineData("scout")]
    public void ANewCharactersAskAndItsStartSaveTransferRoundTrip(string style)
    {
        // the ask: one message to the host, the playstyle in it
        var ask = HenryBackupMsg.FreshAsk(style);
        Assert.Equal(Protocol.HenryFreshAsk, ask.Kind);
        Assert.True(HenryBackupMsg.TryFreshAsk(ask.Data, out string got));
        Assert.Equal(style, got);
        var wire = ask.BuildUp(Protocol.JoinTargetHost);
        var row = Protocol.JoinWireFor(Protocol.HenryBackupUp)!.Value;
        Assert.InRange(wire.Length - 3, row.Min, row.Max);
        Assert.True(HenryBackupMsg.TryDecode(wire.AsSpan(8), out var back));
        Assert.Equal(Protocol.HenryFreshAsk, back.Kind);

        // the answer's transfer: a name the offer accepts, and a seed that names the playstyle and no world's
        var save = new byte[1_350_000];
        new Random(7).NextBytes(save);
        string name = HenryBackupMsg.FreshName(new DateTime(2026, 10, 12, 15, 30, 0, DateTimeKind.Utc), MD5.HashData(save));
        Assert.True(HenryBackupMsg.ValidName(name));
        Assert.EndsWith("-fresh.hblk", name);
        uint seed = Protocol.HenryFreshSeed(style);
        Assert.Equal(style, Protocol.HenryFreshStyleOf(seed));
        var all = HenryBackupMsg.Pack(save, null);
        Assert.True(all.Length <= Protocol.HenryMaxBytes);
        int chunks = (all.Length + Protocol.HenryChunkMax - 1) / Protocol.HenryChunkMax;
        var offer = HenryBackupMsg.Offer(7, seed, all.Length, chunks, SHA256.HashData(all), name);
        Assert.True(HenryBackupMsg.TryOffer(offer.Data, out uint s2, out int size, out int c2, out _, out string n2));
        Assert.Equal((seed, all.Length, chunks, name), (s2, size, c2, n2));
        Assert.True(HenryBackupMsg.TryUnpack(all, out var hblk, out var chest));
        Assert.Equal(save, hblk);
        Assert.Null(chest);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 4, (byte)'b', (byte)'a', (byte)'r', (byte)'d' })]     // not a playstyle
    [InlineData(new byte[] { 9, (byte)'s', (byte)'o', (byte)'l', (byte)'d', (byte)'i', (byte)'e', (byte)'r' })]   // the length lies
    public void AnAskThatIsNotAPlaystyleIsRefused(byte[] data) => Assert.False(HenryBackupMsg.TryFreshAsk(data, out _));

    [Fact]
    public void AWorldsSeedIsNeverTakenForAStartSave()
    {
        Assert.Null(Protocol.HenryFreshStyleOf(0x12345678));
        Assert.Null(Protocol.HenryFreshStyleOf(0));
        Assert.Null(Protocol.HenryFreshStyleOf(Protocol.HenryFreshSeed("scout") + 1));
        Assert.Equal(3, Protocol.HenryFreshStyles.Select(Protocol.HenryFreshSeed).Distinct().Count());
        Assert.Equal("fresh-ask", Protocol.HenryKindName(Protocol.HenryFreshAsk));
    }

    [Fact]
    public void Henry_backup_messages_and_import_round_trip()
    {
        uint seed = 0x12345678;
        string tag = WhsSave.SeedTag(seed);
        string snapName = "snap-20261011120000000-0123456789abcdef0123456789abcdef-join.hblk";
        var (_, js) = WhsSaveTests.Pair();
        var parts = WhsSave.PartsFromFile(WhsSaveTests.File(js), WhsSave.HenryParts.OriginSnapshot);
        byte[] hblk = WhsSave.SerializeBlock(parts);
        string chest = "{\"chest_1\": [\"item_a\"]}";

        byte[] packed = HenryBackupMsg.Pack(hblk, chest);
        Assert.True(HenryBackupMsg.TryUnpack(packed, out var unpackedHblk, out var unpackedChest));
        Assert.Equal(hblk, unpackedHblk);
        Assert.Equal(chest, unpackedChest);

        int chunks = (packed.Length + Protocol.HenryChunkMax - 1) / Protocol.HenryChunkMax;
        var sha = SHA256.HashData(packed);
        var offer = HenryBackupMsg.Offer(42, seed, packed.Length, chunks, sha, snapName);
        var offerPkt = offer.BuildUp(Protocol.JoinTargetHost);
        byte[] offerDown = new byte[] { 1 }.Concat(offerPkt.Skip(3)).ToArray();
        Assert.True(Protocol.TrySplitJoinDown(offerDown, out _, out _, out _, out var offerBody));
        Assert.True(HenryBackupMsg.TryDecode(offerBody, out var decodedOffer));
        Assert.Equal(Protocol.HenryOffer, decodedOffer.Kind);
        Assert.Equal(42u, decodedOffer.Xfer);
        Assert.True(HenryBackupMsg.TryOffer(decodedOffer.Data, out uint decSeed, out int decSize, out int decChunks, out var decSha, out string decName));
        Assert.Equal(seed, decSeed);
        Assert.Equal(packed.Length, decSize);
        Assert.Equal(chunks, decChunks);
        Assert.Equal(sha, decSha);
        Assert.Equal(snapName, decName);

        var chunkMsg = HenryBackupMsg.Chunk(42, 0, packed.AsSpan(0, Math.Min(Protocol.HenryChunkMax, packed.Length)));
        var chunkPkt = chunkMsg.BuildUp(1);
        byte[] chunkDown = new byte[] { 1 }.Concat(chunkPkt.Skip(3)).ToArray();
        Assert.True(Protocol.TrySplitJoinDown(chunkDown, out _, out _, out _, out var chunkBody));
        Assert.True(HenryBackupMsg.TryDecode(chunkBody, out var decodedChunk));
        Assert.True(HenryBackupMsg.TryChunk(decodedChunk.Data, out int chunkIdx, out var chunkData));
        Assert.Equal(0, chunkIdx);
        Assert.Equal(Math.Min(Protocol.HenryChunkMax, packed.Length), chunkData.Length);

        // the host's store imports it under its own name
        var hostStore = new HenryStore(Path.Combine(_dir, "host-henry"));
        var snap = hostStore.Import(tag, snapName, hblk, chest, "test-import");
        Assert.NotNull(snap);
        Assert.Equal(snapName, snap!.File);
        Assert.Single(hostStore.Snapshots(tag));
        Assert.Equal(chest, hostStore.LoadChestLedger(snap));

        // importing it again changes nothing
        Assert.Null(hostStore.Import(tag, snapName, hblk, chest, "repeat"));

        // a player name is a folder name: normalised, never a path
        Assert.Equal("bob_the_builder", HenryBackupMsg.PlayerKey("  Bob_The_Builder  "));
        Assert.Equal("", HenryBackupMsg.PlayerKey("   "));
    }

    [Fact]
    public void ASnapshotThatDoesNotParseIsNotKept()
    {
        var store = new HenryStore(Path.Combine(_dir, "bad-henry"));
        string name = "snap-20261011120000000-0123456789abcdef0123456789abcdef-join.hblk";
        Assert.ThrowsAny<Exception>(() => store.Import(WhsSave.SeedTag(1), name, new byte[] { 1, 2, 3 }, null, "junk"));
        Assert.Empty(store.Snapshots(WhsSave.SeedTag(1)));
        Assert.ThrowsAny<ArgumentException>(() => store.Import(WhsSave.SeedTag(1), "../evil.hblk", new byte[] { 1 }, null, "path"));
    }
}
