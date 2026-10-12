// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Text;
using System.Text.Json;

namespace KcdMp.CairnBridge.Session;

/// <summary>What the session says to the launcher: one JSON object per line on standard output. Everything else the program prints is a log line the launcher puts in its log.</summary>
public sealed class SessionOut(TextWriter writer)
{
    private readonly object _gate = new();

    public void SendRaw(string json) { lock (_gate) { writer.WriteLine(json); writer.Flush(); } }
    public void Send(object message) => SendRaw(JsonSerializer.Serialize(message));
    /// <summary>A line for the launcher's log (redacted there like any other).</summary>
    public void Log(string line) => Send(new { t = "log", line });
}

/// <summary>
/// <c>CairnBridgeKcd2.exe session</c>: the connection side of a session, driven by the launcher over standard input and output (docs/SESSION.md in the launcher repository).
/// It runs until the launcher closes its input or sends <c>stop</c>; the game, the relay and the agent it started are separate programs and are left running when it ends.
/// </summary>
public static class SessionProgram
{
    public const int Protocol = 1;
    private const int MaxLine = 16 * 1024;

    public static async Task<int> RunAsync(TextReader input, TextWriter output, SessionPack? pack = null)
    {
        var o = new SessionOut(output);
        var host = new SessionHost(o, pack);
        o.Send(new { t = "hello", protocol = Protocol });
        host.Publish(force: true);

        string? line;
        while ((line = await input.ReadLineAsync()) is not null)
        {
            line = line.TrimStart((char)0xFEFF);   // a byte order mark some writers put at the start
            if (line.Length is 0 or > MaxLine) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (doc)
            {
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("t", out var tv) || tv.ValueKind != JsonValueKind.String) continue;
                string Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                int Int(string n, int d) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : d;
                bool Bool(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
                ServerSpec Server()
                {
                    string code = Str("steam");
                    return new ServerSpec(Str("ip"), Int("port", 0), code.Length > 0 ? CleanCode(code) : null, Int("ping", -1));
                }

                // nothing is started on a guess: the ports and paths are the launcher's to give
                if (tv.GetString() is "relay" or "launch" or "connect" or "friends" or "test" or "fallback" && !host.Configured)
                {
                    o.Send(new { t = "error", text = "The multiplayer helper has not been given its settings. Try again; if it keeps failing, send the logs with Report a bug." });
                    continue;
                }

                switch (tv.GetString())
                {
                    case "config":
                        host.Config = SessionConfig.Parse(r);
                        o.Log($"session: settings taken (relay port {host.Config.HostPort}, info port {host.Config.InfoPort}, reuse {host.Config.RelayReuse}, gate {host.Config.ConnectGate})");
                        break;
                    case "relay": Run(o, host.RelayAsync); break;
                    case "relay_stop": host.StopRelay(); break;
                    case "launch": { var s = Server(); bool skip = Bool("skipModCheck"); Run(o, () => host.LaunchAsync(s, skip)); break; }
                    case "connect": Run(o, host.ConnectAsync); break;
                    case "choice": { string c = Str("choice"); if (IsChoice(c)) Run(o, () => host.ChoiceAsync(c)); break; }
                    case "fallback": host.FallbackToAddress(Str("host"), Int("port", 0)); break;
                    case "cancel": host.Cancel(); break;
                    case "friends": Run(o, host.FriendsAsync); break;
                    case "test": { var s = Server(); Run(o, () => host.TestConnectionAsync(s)); break; }
                    case "close_game": host.CloseGameAndTools(); break;
                    case "stop": return 0;
                }
            }
        }
        return 0;
    }

    /// <summary>A Steam code without the quote or line break that would cut a command-line argument.</summary>
    internal static string CleanCode(string code) => new string(code.Where(c => c is not ('"' or '\r' or '\n')).ToArray()).Trim();

    /// <summary>"bring", "fresh", or "fresh:" and a playstyle word: plain words only, they go to the agent in a URL.</summary>
    internal static bool IsChoice(string c) => c.Length is > 0 and <= 40 && c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is ':' or '-' or '_');

    // each command runs on its own, so a long one (a launch waiting for the game) never holds back the next (CONNECT, a choice, a stop)
    private static void Run(SessionOut o, Func<Task> work) =>
        _ = Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception ex) { o.Log($"session: a command failed ({ex.GetType().Name}: {ex.Message})"); o.Send(new { t = "error", text = "That did not work. Try again; if it keeps failing, send the logs with Report a bug." }); }
        });
}
