// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace KcdMp.CairnBridge;

/// <summary>
/// The launcher starts this with CAIRN_BRIDGE_PIPE (its pipe), CAIRN_GAME_DIR (the game's folder), CAIRN_GAME_PID and CAIRN_GAME_START
/// (when the game started, local time, round-trip format). It says hello, sends the mod's menu as the game's log shows it, and runs the
/// launcher's requests through the game's console. It ends when the launcher closes the pipe or the game is gone.
/// </summary>
public static class Program
{
    private const string DefaultPrefix = "[KCD2-MP-EVT] v1 ";
    private const string DefaultMenuFunction = "KCD2MP_MenuRequest";

    public static async Task<int> Main(string[] args)
    {
        // the pack's options program: the launcher runs "options check|apply ..." (OptionsCli.cs); no pipe, no game needed
        if (args.Length > 0 && args[0] == "options") return await OptionsCli.RunAsync(args, Console.Out);
        if (args.Length > 0 && args[0] == "saves") return SavesCli.Run(args, Console.Out);
        string? pipeName = Environment.GetEnvironmentVariable("CAIRN_BRIDGE_PIPE");
        string gameDir = Environment.GetEnvironmentVariable("CAIRN_GAME_DIR") ?? "";
        if (string.IsNullOrWhiteSpace(pipeName) || gameDir.Length == 0) return 2;
        DateTime start = DateTime.TryParse(Environment.GetEnvironmentVariable("CAIRN_GAME_START"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var s)
            ? s.ToLocalTime() : DateTime.Now;
        bool pidGiven = int.TryParse(Environment.GetEnvironmentVariable("CAIRN_GAME_PID"), out int pid);
        Process? game = pidGiven ? TryProcess(pid) : null;
        bool gameGone = pidGiven && game is null;   // the game was asked for but is not running (any more): there is nothing to bridge
        var (prefix, function) = ReadNames();

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(10_000); }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return 3; }

        var send = new SemaphoreSlim(1, 1);
        async Task Send(string line)
        {
            await send.WaitAsync();
            try { var b = Encoding.UTF8.GetBytes(line + "\n"); await pipe.WriteAsync(b); await pipe.FlushAsync(); }
            finally { send.Release(); }
        }

        await Send("{\"t\":\"hello\",\"protocol\":1,\"game\":\"kcd2\"}");
        using var cts = new CancellationTokenSource();
        using var console = new GameConsole();
        var tail = new LogTail(gameDir, start);
        var dlss = new Dlss5Menu(gameDir, game);   // DLSS 5's row in the menu and its submenu (it was the launcher's own)
        var saves = SaveCatalog.Default();
        string lastSaves = "";
        async Task SendSaves(bool force)
        {
            // the saves list is an extra: whatever goes wrong while making it must never stop the menu, which rides on the same bridge
            try
            {
                string json = SaveCatalog.ToJson(await Task.Run(saves.List));
                if (!force && json == lastSaves) return;   // sent again only when something changed
                lastSaves = json;
                await Send(json);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or IOException or ObjectDisposedException)) { /* the list is skipped this time */ }
        }

        // asks the mod for its menu again (it answers with a menu_state line): after DLSS 5's submenu closes
        async Task AskModMenu()
        {
            using var ask = JsonDocument.Parse("{\"t\":\"state\"}");
            if (Translator.ToLua(function, ask.RootElement) is { } lua) await console.LuaAsync(lua, cts.Token);
        }

        // the launcher's requests, in order, one console call at a time
        var reader = Task.Run(async () =>
        {
            try
            {
                using var sr = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                string? line;
                while ((line = await sr.ReadLineAsync(cts.Token)) is not null)
                {
                    if (line.Length > 16 * 1024) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                        if (doc.RootElement.TryGetProperty("t", out var t) && t.GetString() == "bye") break;
                        string kind = t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                        if (kind == "back") { if (dlss.OnBack()) await AskModMenu(); continue; }
                        if (kind == "do")
                        {
                            // DLSS 5's row and its submenu are the bridge's own; every other row is the mod's (a console call)
                            string rowId = doc.RootElement.TryGetProperty("id", out var ri) && ri.ValueKind == JsonValueKind.String ? ri.GetString() ?? "" : "";
                            string verb = doc.RootElement.TryGetProperty("verb", out var vb) && vb.ValueKind == JsonValueKind.String ? vb.GetString() ?? "" : "";
                            int dir = doc.RootElement.TryGetProperty("dir", out var dr) && dr.ValueKind == JsonValueKind.Number && dr.TryGetInt32(out var dv) && dv < 0 ? -1 : 1;
                            var done = dlss.OnDo(rowId, verb, dir);
                            if (done.Handled)
                            {
                                if (done.Send is { } submenu) await Send(submenu);
                                if (done.AskMod) await AskModMenu();
                                continue;
                            }
                        }
                        if (t.ValueKind == JsonValueKind.String && t.GetString() == "saves_ask") { await SendSaves(force: true); continue; }
                        if (t.ValueKind == JsonValueKind.String && t.GetString() == "save_delete")
                        {
                            string id = doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
                            (bool ok, string message) result;
                            try { result = id.Length is > 0 and <= 60 ? await Task.Run(() => saves.Delete(id, DateTime.UtcNow)) : (false, "That save is not there any more."); }
                            catch (Exception ex) when (ex is not (OperationCanceledException or IOException or ObjectDisposedException)) { result = (false, "That save could not be deleted."); }
                            var (ok, message) = result;
                            await Send(SaveCatalog.ToastJson(message, error: !ok));
                            await SendSaves(force: true);
                            continue;
                        }
                        if (Translator.ToLua(function, doc.RootElement) is { } lua) await console.LuaAsync(lua, cts.Token);
                    }
                    catch (JsonException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            cts.Cancel();
        });

        try
        {
            int tick = 0;
            while (!cts.IsCancellationRequested && !gameGone && game is not { HasExited: true })
            {
                if (++tick % 300 == 0) await SendSaves(force: false);   // every 30 s, when something changed (the game saves, a joiner's character is kept)
                var lines = new List<string>();
                tail.Poll(lines.Add);
                foreach (var l in lines)
                    if (Translator.ToBridge(l, prefix) is { } msg) await Send(msg.StartsWith("{\"t\":\"menu\"", StringComparison.Ordinal) ? dlss.OnModMenu(msg) : msg);
                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        cts.Cancel();
        await reader;
        return 0;
    }

    private static Process? TryProcess(int pid)
    {
        try { return Process.GetProcessById(pid); } catch (ArgumentException) { return null; }
    }

    /// <summary>The names the pack's contract gives (pack.json two folders above this program: &lt;pack&gt;\files\bin); the defaults of the mod when there is none.</summary>
    private static (string Prefix, string Function) ReadNames()
    {
        try
        {
            string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "pack.json"));
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("lua", out var lua))
                {
                    string Get(string k, string d) => lua.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : d;
                    return (Get("eventPrefix", DefaultPrefix), Get("menuRequest", DefaultMenuFunction));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return (DefaultPrefix, DefaultMenuFunction);
    }
}
