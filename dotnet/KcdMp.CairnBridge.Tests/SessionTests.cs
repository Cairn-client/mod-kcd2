// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using KcdMp.CairnBridge.Session;

namespace KcdMp.CairnBridge.Tests;

/// <summary>The session program (SessionProgram): commands in, state and events out, with a fake relay program and a fake relay status endpoint.</summary>
public class SessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cairn-session-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Channel<string> _in = Channel.CreateUnbounded<string>();
    private readonly ConcurrentQueue<JsonElement> _events = new();
    private readonly Task<int> _run;
    private HttpListener? _http;

    private const int HostPort = 47778, InfoPort = 47273;

    private sealed class ChannelReader(Channel<string> ch) : TextReader
    {
        public override async Task<string?> ReadLineAsync()
        {
            try { return await ch.Reader.ReadAsync(); }
            catch (ChannelClosedException) { return null; }
        }
    }

    private sealed class LineWriter(ConcurrentQueue<JsonElement> sink) : TextWriter
    {
        private readonly StringBuilder _sb = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { if (value == '\n') Flush(); else _sb.Append(value); }
        public override void Write(string? value) { foreach (var c in value ?? "") Write(c); }
        public override void WriteLine(string? value) { lock (_sb) { var s = (_sb.ToString() + value).Trim('\r', '\n'); _sb.Clear(); if (s.Length > 0) { try { sink.Enqueue(JsonDocument.Parse(s).RootElement.Clone()); } catch (JsonException) { } } } }
        public override void Flush() { }
    }

    public SessionTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "files", "bin"));
        File.WriteAllText(Path.Combine(_dir, "pack.json"), "{\"schema\":2,\"id\":\"kcd2\",\"version\":\"0.2.9\",\"relay\":\"bin/fakerelay.cmd\",\"agent\":\"bin/fakeagent.cmd\",\"native\":\"bin/fake.dll\"}");
        File.WriteAllText(Path.Combine(_dir, "files", "bin", "fakerelay.cmd"), "@echo off\r\nping -n 40 127.0.0.1 >nul\r\n");
        File.WriteAllText(Path.Combine(_dir, "files", "bin", "fakeagent.cmd"), "@echo off\r\nping -n 40 127.0.0.1 >nul\r\n");
        File.WriteAllBytes(Path.Combine(_dir, "files", "bin", "fake.dll"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(_dir, "files", "bin", "fakerelay.dll"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(_dir, "files", "bin", "fakeagent.dll"), new byte[] { 1, 2, 3 });
        _run = SessionProgram.RunAsync(new ChannelReader(_in), new LineWriter(_events), new SessionPack(_dir));
    }

    public void Dispose()
    {
        _in.Writer.TryComplete();
        try { _http?.Stop(); _http?.Close(); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void Send(object message) => _in.Writer.TryWrite(JsonSerializer.Serialize(message));

    private void Config(object? extra = null) => Send(new { t = "config", gameExe = "", hostPort = HostPort, infoPort = InfoPort, versionPort = 47902, steamAppId = 480, allowSteam = true, relayReuse = false, connectGate = true, injectDelaySeconds = 5, menuTakeover = true });

    private async Task<JsonElement> WaitFor(Func<JsonElement, bool> match, int seconds = 12)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        int seen = 0;
        while (DateTime.UtcNow < until)
        {
            var all = _events.ToArray();
            for (; seen < all.Length; seen++) if (match(all[seen])) return all[seen];
            await Task.Delay(50);
        }
        throw new TimeoutException("no such event; got: " + string.Join(" | ", _events.Select(e => e.ToString())));
    }

    private static bool Is(JsonElement e, string t) => e.TryGetProperty("t", out var v) && v.GetString() == t;

    [Fact]
    public async Task It_says_hello_and_its_idle_state_first()
    {
        var hello = await WaitFor(e => Is(e, "hello"));
        Assert.Equal(SessionProgram.Protocol, hello.GetProperty("protocol").GetInt32());
        var state = await WaitFor(e => Is(e, "state"));
        Assert.Equal("idle", state.GetProperty("stage").GetString());
        Assert.False(state.GetProperty("host").GetProperty("relay").GetBoolean());
        Assert.True(state.GetProperty("connect").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task A_launch_without_a_game_is_told_in_plain_words()
    {
        Config();
        Send(new { t = "launch", ip = "127.0.0.1", port = HostPort });
        var err = await WaitFor(e => Is(e, "error"));
        Assert.Contains("Game Executable not found", err.GetProperty("text").GetString());
    }

    [Fact]
    public async Task The_retail_game_is_refused_for_the_modding_tools_build()
    {
        var exe = Path.Combine(_dir, "KingdomCome.exe"); File.WriteAllText(exe, "x");
        Send(new { t = "config", gameExe = exe, hostPort = HostPort, infoPort = InfoPort });
        Send(new { t = "launch", ip = "127.0.0.1", port = HostPort, ping = 5 });
        var err = await WaitFor(e => Is(e, "error"));
        Assert.Contains("retail game", err.GetProperty("text").GetString());
    }

    [Fact]
    public async Task The_relay_is_started_and_its_status_is_passed_on()
    {
        _http = new HttpListener();
        _http.Prefixes.Add($"http://127.0.0.1:{InfoPort}/");
        _http.Start();
        _ = Task.Run(async () =>
        {
            while (_http.IsListening)
            {
                HttpListenerContext c;
                try { c = await _http.GetContextAsync(); } catch { break; }
                var body = Encoding.UTF8.GetBytes("{\"release\":\"0.2.0\",\"steam\":{\"state\":\"ready\",\"message\":\"Steam is ready\",\"appId\":480,\"code\":\"ABC-123\"},\"players\":1}");
                c.Response.ContentType = "application/json"; c.Response.OutputStream.Write(body); c.Response.Close();
            }
        });
        Config();
        Send(new { t = "relay" });
        var up = await WaitFor(e => Is(e, "state") && e.GetProperty("host").GetProperty("relay").GetBoolean() && e.GetProperty("host").GetProperty("steamCode").ValueKind == JsonValueKind.String);
        Assert.Equal("ABC-123", up.GetProperty("host").GetProperty("steamCode").GetString());
        Assert.Equal("ready", up.GetProperty("host").GetProperty("steamState").GetString());

        Send(new { t = "relay_stop" });
        var down = await WaitFor(e => Is(e, "state") && !e.GetProperty("host").GetProperty("relay").GetBoolean() && e.GetProperty("host").GetProperty("steamCode").ValueKind == JsonValueKind.Null);
        Assert.Equal("off", down.GetProperty("host").GetProperty("steamState").GetString());
    }

    [Fact]
    public async Task A_relay_file_that_is_gone_is_a_problem_not_a_crash()
    {
        File.Delete(Path.Combine(_dir, "files", "bin", "fakerelay.dll"));
        Config();
        Send(new { t = "relay" });
        var p = await WaitFor(e => Is(e, "problem"));
        Assert.True(p.GetProperty("reportBug").GetBoolean());
        Assert.Contains("fakerelay.dll", p.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Nothing_is_started_before_the_launcher_has_given_its_settings()
    {
        Send(new { t = "relay" });
        Send(new { t = "launch", ip = "127.0.0.1", port = HostPort });
        var err = await WaitFor(e => Is(e, "error"));
        Assert.Contains("not been given its settings", err.GetProperty("text").GetString());
        await Task.Delay(500);
        Assert.DoesNotContain(_events.ToArray(), e => Is(e, "state") && e.GetProperty("host").GetProperty("relay").GetBoolean());
    }

    [Fact]
    public async Task A_settings_line_with_a_byte_order_mark_is_still_taken()
    {
        _in.Writer.TryWrite("﻿" + System.Text.Json.JsonSerializer.Serialize(new { t = "config", hostPort = 47778, infoPort = 47273 }));
        var log = await WaitFor(e => Is(e, "log") && e.GetProperty("line").GetString()!.Contains("settings taken"));
        Assert.Contains("relay port 47778", log.GetProperty("line").GetString());
    }

    [Fact]
    public async Task Stop_ends_the_program()
    {
        Send(new { t = "stop" });
        Assert.Equal(0, await _run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Closing_the_input_ends_the_program_and_garbage_is_ignored()
    {
        _in.Writer.TryWrite("not json");
        _in.Writer.TryWrite("[1,2]");
        _in.Writer.TryWrite("{\"t\":5}");
        _in.Writer.TryWrite(new string('x', 20000));
        _in.Writer.TryComplete();
        Assert.Equal(0, await _run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData("bring", true)]
    [InlineData("fresh:medieval", true)]
    [InlineData("fresh&x=1", false)]
    [InlineData("", false)]
    public void A_join_choice_is_a_plain_word(string choice, bool ok) => Assert.Equal(ok, SessionProgram.IsChoice(choice));

    [Fact]
    public void A_steam_code_loses_what_would_cut_a_command_line() => Assert.Equal("ABC-12 3", SessionProgram.CleanCode("\"ABC-12\" 3\r\n".Replace("\" 3", " 3")));

    [Fact]
    public void The_config_is_bounded_and_falls_back_to_the_defaults()
    {
        using var doc = JsonDocument.Parse("{\"hostPort\":99999,\"infoPort\":-1,\"injectDelaySeconds\":0,\"connectGate\":false,\"gameExe\":\"C:\\\\g\\\\KingdomCome.exe\",\"steamAppId\":480}");
        var c = SessionConfig.Parse(doc.RootElement);
        Assert.Equal(7778, c.HostPort); Assert.Equal(5273, c.InfoPort); Assert.Equal(20, c.InjectDelaySeconds);
        Assert.False(c.ConnectGate); Assert.Equal(480u, c.SteamAppId); Assert.EndsWith("KingdomCome.exe", c.GameExe);
    }

    [Fact]
    public void The_handshake_is_the_packs_major_and_minor()
    {
        Assert.Equal("0.2.0", new SessionPack(_dir).Handshake);
    }
}
