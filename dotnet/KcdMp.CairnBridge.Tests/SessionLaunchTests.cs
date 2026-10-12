// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using KcdMp.CairnBridge.Session;

namespace KcdMp.CairnBridge.Tests;

/// <summary>
/// A whole launch through the session program with a stand-in for the game (KcdMp.CairnBridge.FakeGame): a fake Modding Tools tree, a fake relay and agent
/// (scripts), a copy of a harmless system DLL as the plugin, and local fake status endpoints. It follows the real code path: the game is started, the gate reads its
/// kcd.log, the plugin is loaded into the game's process, its log is read, the agent is started, and everything goes when the game does. Ports are ones nothing uses.
/// </summary>
public class SessionLaunchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cairn-launch-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Channel<string> _in = Channel.CreateUnbounded<string>();
    private readonly ConcurrentQueue<JsonElement> _events = new();
    private readonly List<HttpListener> _listeners = new();
    private const int HostPort = 47881, InfoPort = 47882, AgentPort = 47883;

    private string Packs => Path.Combine(_dir, "pack");
    private string Root => Path.Combine(_dir, "KCD2Mod");
    private string BinDir => Path.Combine(Root, "Bin", "Win64ReleaseSteamLTO_DLL");
    private string GameExe => Path.Combine(BinDir, "KingdomCome.exe");

    private sealed class ChannelReader(Channel<string> ch) : TextReader
    {
        public override async Task<string?> ReadLineAsync()
        {
            try { return await ch.Reader.ReadAsync(); } catch (ChannelClosedException) { return null; }
        }
    }

    private sealed class LineWriter(ConcurrentQueue<JsonElement> sink) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try { sink.Enqueue(JsonDocument.Parse(value).RootElement.Clone()); } catch (JsonException) { }
        }
    }

    public SessionLaunchTests()
    {
        string bin = Path.Combine(Packs, "files", "bin");
        Directory.CreateDirectory(bin); Directory.CreateDirectory(BinDir);
        File.WriteAllText(Path.Combine(Packs, "pack.json"), "{\"schema\":2,\"id\":\"kcd2\",\"version\":\"0.2.10\",\"relay\":\"bin/fakerelay.cmd\",\"agent\":\"bin/fakeagent.cmd\",\"native\":\"bin/fake.dll\",\"lua\":{\"nativeLog\":\"kcdmp-native.log\"}}");
        File.WriteAllText(Path.Combine(bin, "fakerelay.cmd"), "@echo off\r\nping -n 60 127.0.0.1 >nul\r\n");
        File.WriteAllText(Path.Combine(bin, "fakeagent.cmd"),
            "@echo off\r\n" +
            "if \"%1\"==\"--keys-pak\" (echo KEYS-PAK {\"ok\":true,\"action\":\"built\",\"detail\":\"d\"} & exit /b 0)\r\n" +
            "if \"%1\"==\"--test-connection\" (echo TEST-CONNECTION {\"reachable\":true,\"kind\":\"None\",\"hostConnected\":true} & exit /b 0)\r\n" +
            "ping -n 60 127.0.0.1 >nul\r\n");
        foreach (var n in new[] { "fakerelay.dll", "fakeagent.dll" }) File.WriteAllBytes(Path.Combine(bin, n), new byte[] { 1, 2, 3 });
        string sys = Path.Combine(Environment.SystemDirectory, "version.dll");
        File.Copy(sys, Path.Combine(bin, "fake.dll"));                       // the plugin: a harmless DLL, loaded into the fake game by the real injector
        File.Copy(sys, Path.Combine(BinDir, "WHGame.dll"));                  // what the launcher waits for in the game's process
        File.WriteAllBytes(Path.Combine(BinDir, "Framework.dll"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(BinDir, "CrySystem.dll"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(Root, "steam_appid.txt"), "2429020");
        File.WriteAllText(Path.Combine(Root, "kcd.log"), "Log Started at 2020-01-01 00:00:00\n");   // the previous run's log, as a real install has: the gate finds the file by its place
        foreach (var f in Directory.GetFiles(AppContext.BaseDirectory, "KingdomCome.*")) File.Copy(f, Path.Combine(BinDir, Path.GetFileName(f)));

        _ = SessionProgram.RunAsync(new ChannelReader(_in), new LineWriter(_events), new SessionPack(Packs));
        Send(new { t = "config", gameExe = GameExe, hostPort = HostPort, infoPort = InfoPort, versionPort = AgentPort, steamAppId = 480, allowSteam = false, relayReuse = false,
                   connectGate = true, injectDelaySeconds = 20, menuTakeover = false });
    }

    public void Dispose()
    {
        File.WriteAllText(Path.Combine(Root, "quit.flag"), "");
        _in.Writer.TryComplete();
        foreach (var l in _listeners) { try { l.Stop(); l.Close(); } catch { } }
        SessionHost.CloseProcesses(new[] { "KingdomCome" }.Where(_ => false));   // nothing by name: only what these tests started
        Thread.Sleep(300);
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void Send(object message) => _in.Writer.TryWrite(JsonSerializer.Serialize(message));
    private static bool Is(JsonElement e, string t) => e.TryGetProperty("t", out var v) && v.GetString() == t;
    private static bool Stage(JsonElement e, string s) => Is(e, "state") && e.GetProperty("stage").GetString() == s;

    private async Task<JsonElement> WaitFor(Func<JsonElement, bool> match, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        int seen = 0;
        while (DateTime.UtcNow < until)
        {
            var all = _events.ToArray();
            for (; seen < all.Length; seen++) if (match(all[seen])) return all[seen];
            await Task.Delay(50);
        }
        throw new TimeoutException("no such event; got: " + string.Join(" | ", _events.ToArray().Select(e => e.ToString().Length > 300 ? e.ToString()[..300] : e.ToString())));
    }

    private void Serve(int port, Func<string, string> answer)
    {
        var l = new HttpListener(); l.Prefixes.Add($"http://127.0.0.1:{port}/"); l.Start(); _listeners.Add(l);
        _ = Task.Run(async () =>
        {
            while (l.IsListening)
            {
                HttpListenerContext c;
                try { c = await l.GetContextAsync(); } catch { break; }
                var body = Encoding.UTF8.GetBytes(answer(c.Request.Url!.AbsolutePath));
                c.Response.ContentType = "application/json"; c.Response.OutputStream.Write(body); c.Response.Close();
            }
        });
    }

    private void ServeRelayAndAgent()
    {
        Serve(InfoPort, _ => "{\"release\":\"0.2.0\",\"steam\":{\"state\":\"off\",\"message\":\"\",\"appId\":0},\"players\":1}");
        Serve(AgentPort, p => p switch
        {
            "/connection-status" => "{\"state\":\"connected\",\"via\":\"direct\",\"kind\":\"None\",\"message\":\"Connected.\"}",
            "/join-status" => "{\"state\":\"idle\"}",
            "/version-status" => "{\"myReleaseVersion\":\"0.2.0\",\"peers\":[]}",
            _ => "{}",
        });
    }

    private void WriteNativeLog(int pid) =>
        File.WriteAllText(Path.Combine(Packs, "files", "bin", "kcdmp-native.log"), $"attach pid={pid}\nMAIN: 7 frames after ~120 ms -- tick is live\n");

    [Fact]
    public async Task A_host_launch_goes_from_the_relay_to_a_connected_game_and_back_to_nothing()
    {
        ServeRelayAndAgent();
        Send(new { t = "relay" });
        await WaitFor(e => Is(e, "state") && e.GetProperty("host").GetProperty("relay").GetBoolean());

        Send(new { t = "launch", ip = "127.0.0.1", port = HostPort, steam = "", ping = 5, skipModCheck = false });
        var game = await WaitFor(e => Is(e, "game"));
        int pid = game.GetProperty("pid").GetInt32();
        Assert.True(game.GetProperty("hosting").GetBoolean());
        var waiting = await WaitFor(e => Stage(e, "waiting-connect") && e.GetProperty("status").GetString()!.StartsWith("Load into your save"));
        Assert.False(waiting.GetProperty("connect").GetProperty("enabled").GetBoolean());
        Assert.Equal("Load your save first.", waiting.GetProperty("connect").GetProperty("reason").GetString());

        // the player loads a save: the gate opens once the world has been there for a while
        File.WriteAllText(Path.Combine(Root, "world.flag"), "");
        await WaitFor(e => Stage(e, "waiting-connect") && e.GetProperty("status").GetString()!.StartsWith("Load into") && e.GetProperty("connect").GetProperty("enabled").GetBoolean(), 40);

        WriteNativeLog(pid);
        Send(new { t = "connect" });
        await WaitFor(e => Stage(e, "injecting"));
        await WaitFor(e => Stage(e, "connected"), 30);
        Assert.Contains(_events.ToArray(), e => Is(e, "toast"));
        Assert.Contains(Process.GetProcessById(pid).Modules.Cast<ProcessModule>(), m => m.ModuleName.Equals("fake.dll", StringComparison.OrdinalIgnoreCase));   // the plugin is in the game
        var conn = await WaitFor(e => Is(e, "state") && e.GetProperty("stage").GetString() == "connected" && e.GetProperty("conn").GetProperty("text").GetString() == "", 10);
        Assert.True(conn.GetProperty("game").GetBoolean());

        // the game ends: the relay, the agent and the launch go with it
        File.WriteAllText(Path.Combine(Root, "quit.flag"), "");
        await WaitFor(e => Is(e, "game_exit"), 20);
        var idle = await WaitFor(e => Stage(e, "idle") && !e.GetProperty("host").GetProperty("relay").GetBoolean(), 10);
        Assert.False(idle.GetProperty("game").GetBoolean());
    }

    [Fact]
    public async Task A_joiner_launch_waits_for_the_host_then_connects()
    {
        ServeRelayAndAgent();
        Send(new { t = "launch", ip = "127.0.0.1", port = HostPort, steam = "", ping = 5, skipModCheck = false });
        var game = await WaitFor(e => Is(e, "game"));
        Assert.False(game.GetProperty("hosting").GetBoolean());
        int pid = game.GetProperty("pid").GetInt32();
        // the fake agent says the host is in: CONNECT opens by itself
        await WaitFor(e => Stage(e, "waiting-connect") && e.GetProperty("status").GetString()!.Length > 25 && e.GetProperty("connect").GetProperty("enabled").GetBoolean(), 30);
        WriteNativeLog(pid);
        Send(new { t = "connect" });
        await WaitFor(e => Stage(e, "connected"), 30);
    }

    [Fact]
    public async Task A_plugin_that_never_attached_fails_the_launch_without_starting_the_agent()
    {
        ServeRelayAndAgent();
        Send(new { t = "launch", ip = "127.0.0.1", port = HostPort, steam = "", ping = 5, skipModCheck = false });
        await WaitFor(e => Is(e, "game"));
        await WaitFor(e => Stage(e, "waiting-connect") && e.GetProperty("status").GetString()!.Length > 25 && e.GetProperty("connect").GetProperty("enabled").GetBoolean(), 30);
        File.WriteAllText(Path.Combine(Packs, "files", "bin", "kcdmp-native.log"), "attach pid=1\ntick never fired after 5000 ms\n");   // not this game's pid: inconclusive
        Send(new { t = "connect" });
        var failed = await WaitFor(e => Stage(e, "failed"), 30);
        Assert.Contains("Restart the game", failed.GetProperty("status").GetString());
        Assert.Contains(_events.ToArray(), e => Is(e, "error") && e.GetProperty("text").GetString()!.Contains("never saw the game running"));
    }

    [Fact]
    public async Task A_missing_plugin_file_is_a_problem_before_any_game_starts()
    {
        File.Delete(Path.Combine(Packs, "files", "bin", "fake.dll"));
        Send(new { t = "launch", ip = "127.0.0.1", port = HostPort, steam = "", ping = 5, skipModCheck = false });
        var p = await WaitFor(e => Is(e, "problem"), 15);
        Assert.Contains("fake.dll", p.GetProperty("text").GetString());
        Assert.DoesNotContain(_events.ToArray(), e => Is(e, "game"));
    }
}
