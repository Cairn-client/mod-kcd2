// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Text.Json;
using KcdMp.CairnBridge;

namespace KcdMp.CairnBridge.Tests;

public class SaveCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cairn-saves-" + Guid.NewGuid().ToString("N")[..8]);
    private string Saves => Path.Combine(_dir, "saves");
    private string Data => Path.Combine(_dir, "data");
    public SaveCatalogTests() { Directory.CreateDirectory(Saves); Directory.CreateDirectory(Data); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SaveCatalog Catalog => new(Saves, new[] { Data });

    private string World(string line, string file, DateTime written, int bytes = 2048)
    {
        var dir = Path.Combine(Saves, line); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, file);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    private string Character(string player, string world, DateTime written)
    {
        var dir = Path.Combine(Data, "hosted-henry", player, world); Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, "snapshot.bin"); File.WriteAllBytes(f, new byte[4096]); File.SetLastWriteTimeUtc(f, written);
        return dir;
    }

    [Fact]
    public void TheWorldSavesAndTheKeptCharactersAreListed()
    {
        World("playline0", "exit.whs", new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
        World("playline1", "quick.whs", new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc), 3 * 1024 * 1024);
        File.WriteAllText(Path.Combine(Saves, "playline0", "notes.txt"), "not a save");
        Character("Ann", "world1", new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc));

        var slots = Catalog.List();
        Assert.Equal(3, slots.Count);
        Assert.Equal(new[] { "world", "world", "character" }, slots.Select(s => s.Kind));
        Assert.Equal(new[] { "quick", "Exit save", "Ann's character" }, slots.Select(s => s.Name));   // the newest world first, the characters after
        Assert.Equal("Playline 1, 3 MB", slots[0].Detail);
        Assert.Equal("Playline 0, 2 KB", slots[1].Detail);

        Assert.StartsWith("kept for when they join again", slots[2].Detail);
        Assert.Equal(new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc), slots[2].WhenUtc);
    }

    [Fact]
    public void IdsArePlainAndStayTheSameOnTheNextListing()
    {
        World("playline0", "exit.whs", DateTime.UtcNow.AddDays(-1));
        Character("Ann", "world1", DateTime.UtcNow.AddDays(-1));
        var a = Catalog.List(); var b = Catalog.List();
        Assert.Equal(a.Select(s => s.Id), b.Select(s => s.Id));
        Assert.All(a, s => Assert.Matches("^[wc]-[0-9a-f]{12}$", s.Id));
        Assert.Equal(a.Count, a.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void AnOldWorldSaveIsDeleted()
    {
        var path = World("playline0", "old.whs", DateTime.UtcNow.AddDays(-2));
        var (ok, message) = Catalog.Delete(Catalog.List()[0].Id, DateTime.UtcNow);
        Assert.True(ok, message);
        Assert.False(File.Exists(path));
        Assert.Empty(Catalog.List());
    }

    [Fact]
    public void ASaveWrittenAMomentAgoIsNotDeletedFromHere()
    {
        var path = World("playline0", "now.whs", DateTime.UtcNow.AddMinutes(-1));
        var (ok, message) = Catalog.Delete(Catalog.List()[0].Id, DateTime.UtcNow);
        Assert.False(ok);
        Assert.Contains("may be using it", message);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AKeptCharacterIsDeletedWithItsFolderAndNothingAround()
    {
        var dir = Character("Ann", "world1", DateTime.UtcNow.AddMinutes(-1));   // a character has no "in use" rule
        var other = Character("Ben", "world1", DateTime.UtcNow.AddDays(-3));
        var ann = Catalog.List().First(s => s.Name.StartsWith("Ann"));
        Assert.True(Catalog.Delete(ann.Id, DateTime.UtcNow).Ok);
        Assert.False(Directory.Exists(dir));
        Assert.True(Directory.Exists(other));
        Assert.Single(Catalog.List());
    }

    [Theory]
    [InlineData("w-doesnotexist")]
    [InlineData("../../Windows")]
    [InlineData("C:\\Windows\\System32")]
    [InlineData("")]
    public void ANameThatIsNotOneOfTheListedSavesDeletesNothing(string id)
    {
        var keep = World("playline0", "old.whs", DateTime.UtcNow.AddDays(-2));
        var (ok, _) = Catalog.Delete(id, DateTime.UtcNow);
        Assert.False(ok);
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void WithNoFoldersThereAreNoSaves() =>
        Assert.Empty(new SaveCatalog(Path.Combine(_dir, "none"), new[] { Path.Combine(_dir, "nodata") }).List());

    [Fact]
    public void TheListGoesToTheLauncherAsABridgeMessage()
    {
        World("playline0", "old.whs", new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
        using var doc = JsonDocument.Parse(SaveCatalog.ToJson(Catalog.List()));
        Assert.Equal("saves", doc.RootElement.GetProperty("t").GetString());
        var slot = doc.RootElement.GetProperty("slots")[0];
        Assert.Equal("world", slot.GetProperty("kind").GetString());
        Assert.Equal("old", slot.GetProperty("name").GetString());
        Assert.StartsWith("2026-10-01T12:00:00", slot.GetProperty("when").GetString());
        Assert.Equal("""{"t":"toast","text":"Deleted x.","error":false}""", SaveCatalog.ToastJson("Deleted x.", false));
    }

    [Theory]
    [InlineData("autosave023", "Autosave 23")]
    [InlineData("quicksave022", "Quicksave 22")]
    [InlineData("permanent028", "Permanent save 28")]
    [InlineData("exit", "Exit save")]
    [InlineData("Autosave7", "Autosave 7")]
    [InlineData("my own save", "my own save")]       // a name that is not one of the game's own stays as it is
    [InlineData("autosave1x", "autosave1x")]
    public void TheGamesOwnSaveNamesAreSaidInPlainWords(string file, string shown) => Assert.Equal(shown, SaveCatalog.FriendlyName(file));

    [Fact]
    public void ThePlaylineIsNamedAndTheNewestSaveIsFirst()
    {
        World("playline0", "autosave001.whs", new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc));
        World("playline0", "autosave002.whs", new DateTime(2026, 10, 1, 11, 0, 0, DateTimeKind.Utc));
        World("playline1", "quicksave003.whs", new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc));
        var slots = Catalog.List();
        Assert.Equal(new[] { "Quicksave 3", "Autosave 2", "Autosave 1" }, slots.Select(s => s.Name));
        Assert.StartsWith("Playline 1,", slots[0].Detail);
        Assert.Equal("Playline 10", SaveCatalog.FriendlyLine("playline10"));
        Assert.Equal("elsewhere", SaveCatalog.FriendlyLine("elsewhere"));
    }

    // ---- the same questions with the game closed (the launcher's settings window), through the program's command line ----

    private (int Code, List<JsonElement> Lines) Cli(bool running, params string[] args)
    {
        var sw = new StringWriter();
        int code = SavesCli.Run(args, sw, Catalog, () => running);
        return (code, sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList());
    }

    [Fact]
    public void TheCommandLineListsTheSavesAsTheBridgeWould()
    {
        World("playline0", "autosave001.whs", DateTime.UtcNow.AddDays(-1));
        var (code, lines) = Cli(false, "saves", "list");
        Assert.Equal(0, code);
        var list = Assert.Single(lines);
        Assert.Equal("saves", list.GetProperty("t").GetString());
        Assert.Equal("Autosave 1", list.GetProperty("slots")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void WithTheGameClosedASaveJustWrittenCanBeDeleted()
    {
        var path = World("playline0", "exit.whs", DateTime.UtcNow);                     // the exit save of the game that was just closed
        var id = Catalog.List()[0].Id;
        var (refusedCode, refused) = Cli(true, "saves", "delete", id);                   // running: it may be in use
        Assert.Equal(1, refusedCode); Assert.True(refused[0].GetProperty("error").GetBoolean()); Assert.True(File.Exists(path));
        var (code, lines) = Cli(false, "saves", "delete", id);                           // closed: nothing can be using it
        Assert.Equal(0, code);
        Assert.False(lines[0].GetProperty("error").GetBoolean());
        Assert.Equal(0, lines[1].GetProperty("slots").GetArrayLength());
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("saves")]
    [InlineData("saves", "delete")]
    [InlineData("saves", "list", "extra")]
    [InlineData("saves", "format")]
    public void ARequestItDoesNotKnowIsRefusedWithoutDeletingAnything(params string[] args)
    {
        var keep = World("playline0", "old.whs", DateTime.UtcNow.AddDays(-5));
        var (code, lines) = Cli(false, args);
        Assert.Equal(2, code);
        Assert.True(lines[0].GetProperty("error").GetBoolean());
        Assert.True(File.Exists(keep));
    }
}
