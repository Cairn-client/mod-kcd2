// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Text.Json;
using KcdMp.CairnBridge.Session;

namespace KcdMp.CairnBridge.Tests;

/// <summary>The pack program's one-shot verbs the launcher used to do itself: delete all saves, add a start save, collect the logs.</summary>
public class PackVerbsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cairn-verbs-" + Guid.NewGuid().ToString("N")[..8]);
    public PackVerbsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Delete_all_takes_every_save_and_the_mods_characters_and_nothing_else()
    {
        string saves = Path.Combine(_dir, "saves"), data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(saves, "playline0")); Directory.CreateDirectory(Path.Combine(data, "henry")); Directory.CreateDirectory(Path.Combine(data, "other"));
        File.WriteAllText(Path.Combine(saves, "playline0", "a.whs"), "x"); File.WriteAllText(Path.Combine(saves, "playline0", "notes.txt"), "keep");
        File.WriteAllText(Path.Combine(data, "w-staged.json"), "{}"); File.WriteAllText(Path.Combine(data, "other", "f.txt"), "keep");
        var cat = new SaveCatalog(saves, new[] { data });

        var refused = cat.DeleteAll(gameRunning: true);
        Assert.False(refused.Ok); Assert.True(File.Exists(Path.Combine(saves, "playline0", "a.whs")));

        var done = cat.DeleteAll(gameRunning: false);
        Assert.True(done.Ok);
        Assert.False(File.Exists(Path.Combine(saves, "playline0", "a.whs"))); Assert.False(Directory.Exists(Path.Combine(data, "henry"))); Assert.False(File.Exists(Path.Combine(data, "w-staged.json")));
        Assert.True(File.Exists(Path.Combine(saves, "playline0", "notes.txt"))); Assert.True(File.Exists(Path.Combine(data, "other", "f.txt")));
        Assert.Equal("There were no saves to delete.", cat.DeleteAll(false).Message);
    }

    [Fact]
    public void Delete_all_is_a_verb_of_the_program()
    {
        string saves = Path.Combine(_dir, "s"); Directory.CreateDirectory(Path.Combine(saves, "playline1"));
        File.WriteAllText(Path.Combine(saves, "playline1", "a.whs"), "x");
        var sw = new StringWriter();
        Assert.Equal(0, SavesCli.Run(new[] { "saves", "delete-all" }, sw, new SaveCatalog(saves, Array.Empty<string>()), () => false));
        Assert.Contains("\"t\":\"toast\"", sw.ToString()); Assert.Contains("Deleted 1 save(s)", sw.ToString());
    }

    [Fact]
    public void The_mod_menus_settings_are_filtered_to_its_own_keys_and_plain_values()
    {
        var raw = System.Text.Encoding.UTF8.GetBytes("{\"NameBadges\":true,\"CrimeMode\":\"joint\",\"Secret\":\"hunter2\",\"FriendlyFire\":\"typed by hand\",\"NameBadges2\":false}");
        using var doc = JsonDocument.Parse(LogsCli.FilterSettings(raw)!);
        var r = doc.RootElement;
        Assert.True(r.GetProperty("NameBadges").GetBoolean()); Assert.Equal("joint", r.GetProperty("CrimeMode").GetString());
        Assert.False(r.TryGetProperty("Secret", out _)); Assert.False(r.TryGetProperty("FriendlyFire", out _));
        Assert.Equal(3, r.GetProperty("_left_out").GetInt32());
        Assert.Null(LogsCli.FilterSettings(System.Text.Encoding.UTF8.GetBytes("[1]")));
    }

    [Fact]
    public void The_logs_of_the_game_and_the_mod_are_copied_where_asked()
    {
        string pack = Path.Combine(_dir, "pack"), bin = Path.Combine(pack, "files", "bin"), root = Path.Combine(_dir, "game"), outDir = Path.Combine(_dir, "out");
        string exe = Path.Combine(root, "Bin", "x", "KingdomCome.exe");
        Directory.CreateDirectory(bin); Directory.CreateDirectory(Path.GetDirectoryName(exe)!); Directory.CreateDirectory(Path.Combine(root, "logbackups"));
        File.WriteAllText(Path.Combine(pack, "pack.json"), "{\"schema\":2,\"id\":\"kcd2\",\"agent\":\"bin/a.exe\",\"relay\":\"bin/r.exe\"}");
        File.WriteAllText(Path.Combine(root, "steam_appid.txt"), "1");
        File.WriteAllText(Path.Combine(root, "kcd.log"), "game log"); File.WriteAllText(Path.Combine(root, "logbackups", "old.log"), "old");
        File.WriteAllText(Path.Combine(bin, "agent.log"), "agent log"); File.WriteAllText(Path.Combine(bin, "mod-settings.json"), "{\"PingLine\":false,\"Token\":\"abc\"}");
        var sw = new StringWriter();
        Assert.Equal(0, LogsCli.Run(new[] { "logs", "collect", "--out", outDir, "--game-exe", exe }, sw, new SessionPack(pack)));
        Assert.Equal("game log", File.ReadAllText(Path.Combine(outDir, "kcd.log")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(outDir, "logbackups", "old.log")));
        Assert.Equal("agent log", File.ReadAllText(Path.Combine(outDir, "agent.log")));
        var ms = File.ReadAllText(Path.Combine(outDir, "mod-settings.json"));
        Assert.Contains("PingLine", ms); Assert.DoesNotContain("abc", ms);
    }

    [Fact]
    public async Task A_start_save_is_checked_by_the_agent_and_filed_under_its_playstyle()
    {
        string pack = Path.Combine(_dir, "pack2"), bin = Path.Combine(pack, "files", "bin"), dest = Path.Combine(_dir, "start-save"), file = Path.Combine(_dir, "my.whs");
        Directory.CreateDirectory(bin); File.WriteAllText(file, "save");
        File.WriteAllText(Path.Combine(pack, "pack.json"), "{\"schema\":2,\"id\":\"kcd2\",\"agent\":\"bin/agent.cmd\",\"lua\":{\"helperFlag\":\"--w159\",\"helperTag\":\"W159\"}}");
        string checks = "{\\\"checks\\\":[{\\\"name\\\":\\\"reads and verifies\\\",\\\"pass\\\":true},{\\\"name\\\":\\\"Modding Tools build\\\",\\\"pass\\\":true},{\\\"name\\\":\\\"after the prologue, as Henry\\\",\\\"pass\\\":true},{\\\"name\\\":\\\"a playthrough seed\\\",\\\"pass\\\":true},{\\\"name\\\":\\\"the scout playstyle\\\",\\\"pass\\\":true}]}";
        File.WriteAllText(Path.Combine(bin, "agent.cmd"), "@echo off\r\necho W159 " + checks.Replace("\\\"", "\"") + "\r\n");
        var sw = new StringWriter();
        int code = await StartSaveCli.RunAsync(new[] { "startsave", "add", "--file", file, "--start-saves", dest }, sw, new SessionPack(pack));
        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(dest, "adviser", "my.whs")) || File.Exists(Path.Combine(dest, "scout", "my.whs")));
        Assert.Contains("START SAVE ADDED", sw.ToString());
    }

    [Fact]
    public async Task A_start_save_the_agent_refuses_is_told_why_and_not_filed()
    {
        string pack = Path.Combine(_dir, "pack3"), bin = Path.Combine(pack, "files", "bin"), dest = Path.Combine(_dir, "ss"), file = Path.Combine(_dir, "bad.whs");
        Directory.CreateDirectory(bin); File.WriteAllText(file, "save");
        File.WriteAllText(Path.Combine(pack, "pack.json"), "{\"schema\":2,\"id\":\"kcd2\",\"agent\":\"bin/agent.cmd\",\"lua\":{\"helperFlag\":\"--w159\",\"helperTag\":\"W159\"}}");
        File.WriteAllText(Path.Combine(bin, "agent.cmd"), "@echo off\r\necho W159 {\"checks\":[{\"name\":\"reads and verifies\",\"pass\":false,\"detail\":\"damaged\"}]}\r\n");
        var sw = new StringWriter();
        Assert.Equal(1, await StartSaveCli.RunAsync(new[] { "startsave", "add", "--file", file, "--start-saves", dest }, sw, new SessionPack(pack)));
        Assert.Contains("This save cannot be used", sw.ToString()); Assert.Contains("damaged", sw.ToString());
        Assert.False(Directory.Exists(dest));
    }
}
