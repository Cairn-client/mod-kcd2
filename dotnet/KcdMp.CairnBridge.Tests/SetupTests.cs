// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Security.Cryptography;
using System.Text.Json;
using KcdMp.CairnBridge.Setup;

namespace KcdMp.CairnBridge.Tests;

/// <summary>The pack's setup (SetupCli): the steps as JSON lines, and the mod placed from the pack's own files.</summary>
public class SetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cairn-setup-" + Guid.NewGuid().ToString("N")[..8]);
    private string Pack => Path.Combine(_dir, "pack");
    private string Steam => Path.Combine(_dir, "Steam");
    public SetupTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeHost(string? steamRoot, bool steamRunning) : ISetupHost
    {
        public string? SteamRootFromRegistry() => steamRoot;
        public bool IsProcessRunning(string processName) => steamRunning && processName == "steam";
        public bool? SteamSignedIn() => steamRunning ? true : null;
        public long? FreeBytes(string path) => 500L * 1024 * 1024 * 1024;
        public bool HasDotNet6Runtime() => true;
        public bool HasVcRuntime2013() => true;
        public bool HasVcRuntime2010() => true;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>A pack on disk: pack.json (mod folder kdcmp, replaces "cairn"), manifest.txt and files\mod with the two mod files.</summary>
    private (byte[] Pak, byte[] Manifest) MakePack()
    {
        var pak = new byte[1500]; new Random(1).NextBytes(pak);
        var manifest = System.Text.Encoding.UTF8.GetBytes("<modid>kdcmp</modid><name>KCD2 Multiplayer</name>");
        Directory.CreateDirectory(Path.Combine(Pack, "files", "mod", "Data"));
        File.WriteAllBytes(Path.Combine(Pack, "files", "mod", "Data", "kdcmp.pak"), pak);
        File.WriteAllBytes(Path.Combine(Pack, "files", "mod", "mod.manifest"), manifest);
        File.WriteAllText(Path.Combine(Pack, "pack.json"), "{\"schema\":2,\"id\":\"kcd2\",\"modFolder\":\"kdcmp\",\"replacesFolders\":[\"cairn\"]}");
        File.WriteAllLines(Path.Combine(Pack, "manifest.txt"), new[]
        {
            "# pack|kcd2|0.0.0",
            $@"MOD|Data\kdcmp.pak|{pak.Length}|{Sha(pak)}",
            $"MOD|mod.manifest|{manifest.Length}|{Sha(manifest)}",
        });
        return (pak, manifest);
    }

    private string Mt => Path.Combine(_dir, "MT");

    [Fact]
    public void The_mod_is_placed_from_the_packs_own_files_and_checks_out()
    {
        var (pak, _) = MakePack();
        Directory.CreateDirectory(Mt);
        Assert.False(ModInstall.IsPlaced(Mt, Pack));
        var r = ModInstall.Place(Mt, Pack);
        Assert.True(r.Ok, r.Message);
        Assert.True(ModInstall.IsPlaced(Mt, Pack));
        Assert.Equal(pak, File.ReadAllBytes(Path.Combine(Mt, "Mods", "kdcmp", "Data", "kdcmp.pak")));
        var report = ModInstall.Check(Mt, ownsModFolder: false, Pack);
        Assert.True(report.Ok); Assert.True(report.StagedAvailable); Assert.Empty(report.Warnings);
    }

    [Fact]
    public void A_foreign_folder_is_never_written_over()
    {
        MakePack();
        var target = Path.Combine(Mt, "Mods", "kdcmp"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "mod.manifest"), "somebody else's mod");
        var report = ModInstall.Check(Mt, ownsModFolder: false, Pack);
        Assert.True(report.ForeignFolder); Assert.False(report.Ok);
        Assert.True(ModInstall.Check(Mt, ownsModFolder: true, Pack).ForeignFolder == false);   // the registry says it was ours
    }

    [Fact]
    public void An_earlier_copy_of_ours_is_removed_and_a_stranger_is_not()
    {
        MakePack();
        var old = Path.Combine(Mt, "Mods", "cairn"); Directory.CreateDirectory(Path.Combine(old, "Data"));
        File.WriteAllText(Path.Combine(old, "mod.manifest"), "<modid>cairn</modid><name>KCD2 Multiplayer</name>");
        File.WriteAllText(Path.Combine(old, "Data", "cairn.pak"), "x");
        Assert.True(ModInstall.HasLegacyFolder(Mt, Pack));
        Assert.True(ModInstall.Place(Mt, Pack).Ok);
        Assert.False(Directory.Exists(old));

        var stranger = Path.Combine(Mt, "Mods", "cairn"); Directory.CreateDirectory(stranger);
        File.WriteAllText(Path.Combine(stranger, "mod.manifest"), "<modid>cairn</modid><name>Another mod</name>");
        Assert.False(ModInstall.HasLegacyFolder(Mt, Pack));
        Assert.True(ModInstall.Place(Mt, Pack).Ok);
        Assert.True(Directory.Exists(stranger));
    }

    [Fact]
    public void A_damaged_pack_file_is_not_placed()
    {
        MakePack();
        File.WriteAllBytes(Path.Combine(Pack, "files", "mod", "Data", "kdcmp.pak"), new byte[10]);
        Directory.CreateDirectory(Mt);
        var r = ModInstall.Place(Mt, Pack);
        Assert.False(r.Ok);
        Assert.False(ModInstall.Check(Mt, false, Pack).StagedAvailable);
        Assert.False(Directory.Exists(Path.Combine(Mt, "Mods", "kdcmp")));
    }

    [Fact]
    public void Unplace_takes_out_only_the_packs_files()
    {
        MakePack(); Directory.CreateDirectory(Mt);
        Assert.True(ModInstall.Place(Mt, Pack).Ok);
        var mine = Path.Combine(Mt, "Mods", "kdcmp", "notes.txt"); File.WriteAllText(mine, "keep");
        Assert.True(ModInstall.Unplace(Mt, Pack).Ok);
        Assert.False(ModInstall.IsPlaced(Mt, Pack));
        Assert.True(File.Exists(mine));
    }

    private static List<JsonElement> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    [Fact]
    public async Task Without_Steam_the_checklist_says_so_and_is_not_ready()
    {
        MakePack();
        var sw = new StringWriter();
        Assert.Equal(0, await SetupCli.RunAsync(new[] { "setup", "check" }, sw, new FakeHost(null, false), Pack));
        var lines = Lines(sw.ToString());
        var steam = lines.First(l => l.TryGetProperty("step", out var s) && s.GetProperty("id").GetString() == "steam").GetProperty("step");
        Assert.Equal("needs-you", steam.GetProperty("status").GetString());
        Assert.True(steam.GetProperty("system").GetBoolean());
        Assert.False(steam.TryGetProperty("action", out _));   // Steam is not installed: nothing to start
        var done = lines.Single(l => l.TryGetProperty("done", out _));
        Assert.False(done.GetProperty("ready").GetBoolean());
        Assert.Contains(lines, l => l.TryGetProperty("step", out var s) && s.GetProperty("id").GetString() == "game" && s.GetProperty("status").GetString() == "waiting");
    }

    [Fact]
    public async Task With_Steam_installed_but_not_running_the_launcher_is_told_to_start_it_once()
    {
        MakePack();
        Directory.CreateDirectory(Path.Combine(Steam, "steamapps"));
        File.WriteAllText(Path.Combine(Steam, "steam.exe"), "");
        var sw = new StringWriter();
        await SetupCli.RunAsync(new[] { "setup", "check" }, sw, new FakeHost(Steam, false), Pack);
        var steam = Lines(sw.ToString()).First(l => l.TryGetProperty("step", out var s) && s.GetProperty("id").GetString() == "steam").GetProperty("step");
        Assert.Equal("start-steam", steam.GetProperty("action").GetString());
        Assert.Equal("once", steam.GetProperty("auto").GetString());
    }

    [Fact]
    public async Task Unknown_requests_are_refused()
    {
        var sw = new StringWriter();
        Assert.Equal(2, await SetupCli.RunAsync(new[] { "setup", "nonsense" }, sw, new FakeHost(null, false), Pack));
        Assert.Equal(1, await SetupCli.RunAsync(new[] { "setup", "do", "nonsense" }, new StringWriter(), new FakeHost(null, false), Pack));
    }

    [Fact]
    public async Task The_link_helper_refuses_anything_that_is_not_a_game_and_its_modding_tools()
    {
        var status = Path.Combine(_dir, "status.txt");
        var args = new[] { "setup", "link-elevated", "--game", _dir, "--mt", _dir, "--status", status };
        Assert.Equal(2, await SetupCli.RunAsync(args, new StringWriter(), new FakeHost(null, false), Pack));
        Assert.Contains("refused", File.ReadAllText(status));
    }

    [Fact]
    public async Task Taking_the_mod_out_with_no_modding_tools_has_nothing_to_remove()
    {
        MakePack();
        var sw = new StringWriter();
        Assert.Equal(0, await SetupCli.RunAsync(new[] { "setup", "do", "unplace-mod" }, sw, new FakeHost(null, false), Pack));
        Assert.Contains("nothing to remove", sw.ToString());
    }
}
