// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Text.Json;
using System.Text.Json.Nodes;
using KcdMp.CairnBridge.Session;

namespace KcdMp.CairnBridge;

/// <summary>
/// <c>CairnBridgeKcd2.exe logs collect --out DIR [--game-exe EXE]</c>: copies what this game's mod has to say about a session into DIR, for the launcher's "Save the logs" and
/// "Report a bug" (it adds its own and zips the folder): the game's kcd.log and the logs of the launches before, the plugin's logs, the agent's, the relay's, the mod menu's
/// remembered choices (filtered: its own keys, on/off or joint/individual only, nothing typed by hand) and the leash recorder's files.
/// (This was the launcher's own code.)
/// </summary>
public static class LogsCli
{
    public static readonly string[] KnownKeys = { "NameBadges", "PingLine", "CleanScreen", "FriendlyFire", "CrimeMode", "FastTravel", "Leash", "Whistle", "PartnerHerbs", "ShareLogs" };

    public static int Run(string[] args, TextWriter output, SessionPack? pack = null)
    {
        string? Get(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        pack ??= new SessionPack();
        if (args.Length < 2 || args[1] != "collect" || Get("--out") is not { Length: > 0 } outDir) return 2;
        string gameRoot = Get("--game-exe") is { Length: > 0 } exe ? SessionHost.GameRootOf(exe) : "";
        Directory.CreateDirectory(outDir);
        int count = 0;

        void Add(string? path, string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
                string dest = Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);   // the game holds its log open while it runs
                using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write);
                src.CopyTo(dst);
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* one unreadable file must not sink the bundle */ }
        }

        string? kcdLog = GameLogs.FindKcdLog(gameRoot);
        Add(kcdLog, "kcd.log");
        if (kcdLog is not null)
        {
            string kcdDir = Path.GetDirectoryName(kcdLog) ?? "";
            Add(Path.Combine(kcdDir, "cairn-native.mirror.log"), "cairn-native.mirror.log");
            Add(Path.Combine(kcdDir, "kcdmp-native.mirror.log"), "kcdmp-native.mirror.log");
            try
            {
                // CryEngine rotates the PREVIOUS run's kcd.log into logbackups: the run a tester is usually reporting about
                foreach (var b in new DirectoryInfo(Path.Combine(kcdDir, "logbackups")).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(2))
                    Add(b.FullName, $"logbackups/{b.Name}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        string agentDir = Path.GetDirectoryName(pack.AgentExe(SessionConfig.Defaults)) ?? "";
        if (agentDir.Length > 0)
        {
            foreach (var n in new[] { "agent.log", "agent.prev.log", "agent.prev2.log", "cairn-native.log", "kcdmp-native.log" }) Add(Path.Combine(agentDir, n), n);
            try
            {
                string ms = Path.Combine(agentDir, "mod-settings.json");
                if (File.Exists(ms))
                {
                    using var src = new FileStream(ms, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var mem = new MemoryStream();
                    src.CopyTo(mem);
                    if (FilterSettings(mem.ToArray()) is { } filtered) { File.WriteAllText(Path.Combine(outDir, "mod-settings.json"), filtered); count++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try
            {
                var leash = new DirectoryInfo(Path.Combine(agentDir, "leash"));
                if (leash.Exists) foreach (var f in leash.GetFiles("leash-*.csv").OrderByDescending(f => f.LastWriteTimeUtc).Take(8)) Add(f.FullName, $"leash/{f.Name}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            // the relay's own output (relay<yyyyMMdd>.log), only on a machine that hosted
            try
            {
                foreach (var f in new DirectoryInfo(Path.GetDirectoryName(pack.RelayExe(SessionConfig.Defaults)) ?? agentDir).GetFiles("relay*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(2)) Add(f.FullName, f.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        // the logs of the launches before this one (kept by the session before each game start)
        try
        {
            var hist = new DirectoryInfo(GameLogs.HistoryFolder);
            if (hist.Exists)
                foreach (var d in hist.GetDirectories("launch-*").OrderByDescending(d => d.Name).Take(GameLogs.HistoryKeep))
                    foreach (var f in d.GetFiles()) Add(f.FullName, $"history/{d.Name}/{f.Name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        output.WriteLine(JsonSerializer.Serialize(new { files = count }));
        return 0;
    }

    /// <summary>The mod menu's remembered choices, filtered: its own keys with on/off or joint/individual only.</summary>
    public static string? FilterSettings(byte[]? file)
    {
        if (file is null) return null;
        int bom = file.Length >= 3 && file[0] == 0xEF && file[1] == 0xBB && file[2] == 0xBF ? 3 : 0;
        try
        {
            using var doc = JsonDocument.Parse(file.AsMemory(bom), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var o = new JsonObject();
            int left = 0;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                bool known = KnownKeys.Contains(p.Name, StringComparer.Ordinal) && !o.ContainsKey(p.Name);
                JsonNode? v = p.Value.ValueKind switch
                {
                    JsonValueKind.True => JsonValue.Create(true),
                    JsonValueKind.False => JsonValue.Create(false),
                    JsonValueKind.String when p.Value.GetString() is "joint" or "individual" => JsonValue.Create(p.Value.GetString()),
                    _ => null,
                };
                if (known && v is not null) o[p.Name] = v; else left++;
            }
            if (left > 0) o["_left_out"] = left;
            return o.ToJsonString();
        }
        catch (JsonException) { return null; }
    }
}
