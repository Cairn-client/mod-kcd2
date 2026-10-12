// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Net;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using KcdMp.CairnBridge;

namespace KcdMp.CairnBridge.Tests;

/// <summary>
/// The whole chain with a stand-in for the game: the real bridge program (<see cref="Program.Main"/>) against a fake kcd.log, a fake
/// console (the game's local API) and the launcher's side of the pipe. What the mod writes in the log arrives as bridge messages, and
/// what the launcher asks arrives at the console as the Lua the mod's menu function expects.
/// </summary>
[Collection("environment")]   // the bridge reads its settings from environment variables: one run at a time
public class EndToEndTests : IDisposable
{
    private const string P = "[KCD2-MP-EVT] v1 ";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cairn-bridge-e2e-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly HttpListener _console = new();
    private readonly List<string> _calls = new();
    private readonly string _consoleUrl;

    public EndToEndTests()
    {
        Directory.CreateDirectory(_dir);
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        _consoleUrl = $"http://127.0.0.1:{port}/";
        _console.Prefixes.Add(_consoleUrl);
        _console.Start();
        _ = Task.Run(async () =>
        {
            while (_console.IsListening)
            {
                HttpListenerContext c;
                try { c = await _console.GetContextAsync(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
                lock (_calls) _calls.Add(Uri.UnescapeDataString(c.Request.QueryString["command"] ?? ""));
                c.Response.StatusCode = 200;
                c.Response.Close();
            }
        });
    }

    public void Dispose()
    {
        _console.Close();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static async Task Until(Func<bool> done, int ms = 8000)
    {
        for (int i = 0; i < ms / 25 && !done(); i++) await Task.Delay(25);
        Assert.True(done(), "timed out");
    }

    [Fact]
    public async Task TheModsMenuReachesTheLauncherAndTheLaunchersChoicesReachTheGame()
    {
        string pipeName = "cairn-e2e-" + Guid.NewGuid().ToString("N")[..12];
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
        string log = Path.Combine(_dir, "kcd.log");
        File.WriteAllText(log, $"Log Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)\n");

        Environment.SetEnvironmentVariable("CAIRN_BRIDGE_PIPE", pipeName);
        Environment.SetEnvironmentVariable("CAIRN_GAME_DIR", _dir);
        Environment.SetEnvironmentVariable("CAIRN_GAME_START", DateTime.Now.AddSeconds(-2).ToString("o"));
        Environment.SetEnvironmentVariable("CAIRN_GAME_PID", null);
        Environment.SetEnvironmentVariable("CAIRN_CONSOLE_URL", _consoleUrl);
        var bridge = Task.Run(() => Program.Main(Array.Empty<string>()));

        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        async Task<JsonElement> Next() => JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)))!).RootElement.Clone();
        async Task Say(string line) { var b = Encoding.UTF8.GetBytes(line + "\n"); await server.WriteAsync(b); await server.FlushAsync(); }

        // the bridge says hello
        var hello = await Next();
        Assert.Equal("hello", hello.GetProperty("t").GetString());
        Assert.Equal("kcd2", hello.GetProperty("game").GetString());
        Assert.Equal(1, hello.GetProperty("protocol").GetInt32());

        // the mod writes its menu and a notice in the game's log: they arrive as bridge messages
        File.AppendAllText(log, P + "1 menu_state Kingdom Come~Mod menu~~Host: Ann|friendly_fire~Rules~onoff~1~0~~Friendly fire~Players can hurt each other~f3\n"
                              + P + "2 notice Ann joined^^Welcome\n");
        var menu = await Next();
        Assert.Equal("menu", menu.GetProperty("t").GetString());
        Assert.Equal("friendly_fire", menu.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.True(menu.GetProperty("items")[0].GetProperty("on").GetBoolean());
        var notice = await Next();
        Assert.Equal("Ann joined^^Welcome", notice.GetProperty("text").GetString());

        // the launcher asks: the game's console gets the mod's menu function called, with the plain words
        await Say("""{"t":"attach"}""");
        await Say("""{"t":"state"}""");
        await Say("""{"t":"do","id":"friendly_fire","verb":"nudge"}""");
        await Say("""{"t":"do","id":"x\"); os.exit(); (\"","verb":"enter"}""");   // never reaches the game
        await Until(() => { lock (_calls) return _calls.Count >= 3; });
        await Task.Delay(300);
        lock (_calls)
        {
            Assert.Equal(new[]
            {
                "#if KCD2MP_MenuRequest then KCD2MP_MenuRequest(\"attach\") end",
                "#if KCD2MP_MenuRequest then KCD2MP_MenuRequest(\"state\") end",
                "#if KCD2MP_MenuRequest then KCD2MP_MenuRequest(\"do friendly_fire nudge\") end",
            }, _calls.ToArray());
        }

        // the launcher ends the conversation: the bridge ends too
        await Say("""{"t":"bye","reason":"done"}""");
        Assert.Equal(0, await bridge.WaitAsync(TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public async Task TheLauncherCanListAndDeleteTheGamesSaves()
    {
        string saves = Path.Combine(_dir, "saves", "playline0"); Directory.CreateDirectory(saves);
        string oldSave = Path.Combine(saves, "old.whs"); File.WriteAllBytes(oldSave, new byte[1024]); File.SetLastWriteTimeUtc(oldSave, DateTime.UtcNow.AddDays(-3));
        string freshSave = Path.Combine(saves, "now.whs"); File.WriteAllBytes(freshSave, new byte[1024]);   // written just now: in use
        string data = Path.Combine(_dir, "data"); string chr = Path.Combine(data, "hosted-henry", "Ann", "world1"); Directory.CreateDirectory(chr);
        File.WriteAllBytes(Path.Combine(chr, "s.bin"), new byte[100]);

        string pipeName = "cairn-e2e-" + Guid.NewGuid().ToString("N")[..12];
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
        File.WriteAllText(Path.Combine(_dir, "kcd.log"), $"Log Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)\n");
        Environment.SetEnvironmentVariable("CAIRN_BRIDGE_PIPE", pipeName);
        Environment.SetEnvironmentVariable("CAIRN_GAME_DIR", _dir);
        Environment.SetEnvironmentVariable("CAIRN_GAME_START", DateTime.Now.ToString("o"));
        Environment.SetEnvironmentVariable("CAIRN_GAME_PID", null);
        Environment.SetEnvironmentVariable("CAIRN_CONSOLE_URL", _consoleUrl);
        Environment.SetEnvironmentVariable("CAIRN_SAVES_ROOT", Path.Combine(_dir, "saves"));
        Environment.SetEnvironmentVariable("CAIRN_DATA_ROOTS", data);
        try
        {
            var bridge = Task.Run(() => Program.Main(Array.Empty<string>()));
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            async Task<JsonElement> Next() => JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)))!).RootElement.Clone();
            async Task Say(string line) { var b = Encoding.UTF8.GetBytes(line + "\n"); await server.WriteAsync(b); await server.FlushAsync(); }
            Assert.Equal("hello", (await Next()).GetProperty("t").GetString());

            await Say("""{"t":"saves_ask"}""");
            var list = await Next();
            Assert.Equal("saves", list.GetProperty("t").GetString());
            var slots = list.GetProperty("slots").EnumerateArray().ToList();
            Assert.Equal(3, slots.Count);
            string oldId = slots.First(s => s.GetProperty("name").GetString() == "old").GetProperty("id").GetString()!;
            string freshId = slots.First(s => s.GetProperty("name").GetString() == "now").GetProperty("id").GetString()!;

            // a save in use is refused with a plain message; the old one goes; the list comes back without it
            await Say("{\"t\":\"save_delete\",\"id\":\"" + freshId + "\"}");
            var refused = await Next();
            Assert.Equal("toast", refused.GetProperty("t").GetString());
            Assert.True(refused.GetProperty("error").GetBoolean());
            Assert.Equal(3, (await Next()).GetProperty("slots").GetArrayLength());
            Assert.True(File.Exists(freshSave));

            await Say("{\"t\":\"save_delete\",\"id\":\"" + oldId + "\"}");
            var done = await Next();
            Assert.False(done.GetProperty("error").GetBoolean());
            Assert.Equal(2, (await Next()).GetProperty("slots").GetArrayLength());
            Assert.False(File.Exists(oldSave));

            // a name that is not a listed save deletes nothing
            await Say("""{"t":"save_delete","id":"../../../Windows"}""");
            Assert.True((await Next()).GetProperty("error").GetBoolean());
            await Next();

            await Say("""{"t":"bye","reason":"done"}""");
            Assert.Equal(0, await bridge.WaitAsync(TimeSpan.FromSeconds(8)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAIRN_SAVES_ROOT", null);
            Environment.SetEnvironmentVariable("CAIRN_DATA_ROOTS", null);
        }
    }

    [Fact]
    public async Task TheBridgeEndsWhenTheGameIsGone()
    {
        string pipeName = "cairn-e2e-" + Guid.NewGuid().ToString("N")[..12];
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
        using var gone = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        gone.WaitForExit();
        File.WriteAllText(Path.Combine(_dir, "kcd.log"), $"Log Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)\n");
        Environment.SetEnvironmentVariable("CAIRN_BRIDGE_PIPE", pipeName);
        Environment.SetEnvironmentVariable("CAIRN_GAME_DIR", _dir);
        Environment.SetEnvironmentVariable("CAIRN_GAME_START", DateTime.Now.ToString("o"));
        Environment.SetEnvironmentVariable("CAIRN_GAME_PID", gone.Id.ToString());
        Environment.SetEnvironmentVariable("CAIRN_CONSOLE_URL", _consoleUrl);
        var bridge = Task.Run(() => Program.Main(Array.Empty<string>()));
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await bridge.WaitAsync(TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public async Task TheDlssRowOpensItsSubmenuInTheMenuItselfAndBackReturnsToTheModsMenu()
    {
        string ini = Path.Combine(_dir, "OptiScaler.ini");
        File.WriteAllText(ini, "[Menu]\r\nShortcutKey=45\r\n[DlssNr]\r\nEnabled=false\r\n");
        string pipeName = "cairn-e2e-" + Guid.NewGuid().ToString("N")[..12];
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
        string log = Path.Combine(_dir, "kcd.log");
        File.WriteAllText(log, $"Log Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)\n");
        Environment.SetEnvironmentVariable("CAIRN_BRIDGE_PIPE", pipeName);
        Environment.SetEnvironmentVariable("CAIRN_GAME_DIR", _dir);
        Environment.SetEnvironmentVariable("CAIRN_GAME_START", DateTime.Now.ToString("o"));
        Environment.SetEnvironmentVariable("CAIRN_GAME_PID", null);
        Environment.SetEnvironmentVariable("CAIRN_CONSOLE_URL", _consoleUrl);
        var bridge = Task.Run(() => Program.Main(Array.Empty<string>()));
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        async Task<JsonElement> Next() => JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)))!).RootElement.Clone();
        async Task Say(string line) { var b = Encoding.UTF8.GetBytes(line + "\n"); await server.WriteAsync(b); await server.FlushAsync(); }
        await Next();   // hello

        // the mod's menu arrives with DLSS 5's row at its end
        File.AppendAllText(log, P + "1 menu_state Kingdom Come~Mod menu~~Host|friendly_fire~Rules~onoff~1~0~~Friendly fire~help~f3\n");
        var menu = await Next();
        var ids = menu.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { "friendly_fire", "dlss5_overlay" }, ids);

        // Enter on the row: the submenu comes back in the same menu, marked as a submenu (Back goes to the bridge)
        await Say("""{"t":"do","id":"dlss5_overlay","verb":"enter"}""");
        var sub = await Next();
        Assert.True(sub.GetProperty("back").GetBoolean());
        Assert.Contains(sub.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == "dlss5_nr_enabled");

        // a value changes in OptiScaler's own file, to the right and back to the left
        await Say("""{"t":"do","id":"dlss5_nr_enabled","verb":"nudge"}""");
        await Next();
        Assert.Contains("Enabled=true", File.ReadAllText(ini));
        await Say("""{"t":"do","id":"dlss5_nr_enabled","verb":"nudge","dir":-1}""");
        await Next();
        Assert.Contains("Enabled=false", File.ReadAllText(ini));

        // none of that reached the game's console; Back asks the mod for its own menu again
        lock (_calls) Assert.Empty(_calls);
        await Say("""{"t":"back"}""");
        await Until(() => { lock (_calls) return _calls.Count == 1; });
        lock (_calls) Assert.Equal("#if KCD2MP_MenuRequest then KCD2MP_MenuRequest(\"state\") end", _calls[0]);
        File.AppendAllText(log, P + "2 menu_state Kingdom Come~Mod menu~~Host|friendly_fire~Rules~onoff~1~0~~Friendly fire~help~f3\n");
        var again = await Next();
        Assert.False(again.TryGetProperty("back", out var b) && b.GetBoolean());
        Assert.Equal("dlss5_overlay", again.GetProperty("items").EnumerateArray().Last().GetProperty("id").GetString());

        await Say("""{"t":"bye","reason":"done"}""");
        Assert.Equal(0, await bridge.WaitAsync(TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public async Task WithoutALauncherPipeTheBridgeDoesNothing()
    {
        Environment.SetEnvironmentVariable("CAIRN_BRIDGE_PIPE", null);
        Assert.Equal(2, await Program.Main(Array.Empty<string>()));
    }
}

[CollectionDefinition("environment", DisableParallelization = true)]
public class EnvironmentCollection { }
