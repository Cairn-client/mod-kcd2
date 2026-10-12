// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KcdMp.CairnBridge.Setup;
using Microsoft.Win32;

namespace KcdMp.CairnBridge.Session;

// The payloads of the relay's and the agent's local endpoints, and of the agent's one-shot helpers (they were the launcher's own models).

public class RelayLocalStatusData
{
    public string Release { get; set; } = "";
    public RelaySteamData Steam { get; set; } = new();
    public int Players { get; set; }
    public bool HostConnected { get; set; }
    public string? RefusedRelease { get; set; }
    public int RefusedSecondsAgo { get; set; } = -1;
}

public class RelaySteamData
{
    public string State { get; set; } = "off";
    public string Message { get; set; } = "";
    public uint AppId { get; set; }
    public string AppName { get; set; } = "";
    public string? Code { get; set; }
    public int Peers { get; set; }
}

public class TestConnectionData
{
    public bool Reachable { get; set; }
    public string Via { get; set; } = "";
    public int RttMs { get; set; } = -1;
    public int ConnectMs { get; set; } = -1;
    public string? HostRelease { get; set; }
    public string MyRelease { get; set; } = "";
    public bool VersionMatch { get; set; }
    public bool? HostConnected { get; set; }
    public int Players { get; set; } = -1;
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    public string Next { get; set; } = "";
    public int SteamPingMs { get; set; } = -1;
    public bool? Relayed { get; set; }
    public string Detail { get; set; } = "";
}

public class SteamFriendsData
{
    public string State { get; set; } = "";
    public string Message { get; set; } = "";
    public string Next { get; set; } = "";
    public string App { get; set; } = "";
    public List<SteamFriendData> Friends { get; set; } = new();
}

public class SteamFriendData
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string Release { get; set; } = "";
}

public class VersionStatusData
{
    public string MyReleaseVersion { get; set; } = "";
    public List<PeerVersionData> Peers { get; set; } = new();
}

public class PeerVersionData
{
    public byte GhostId { get; set; }
    public string ReleaseVersion { get; set; } = "";
}

/// <summary>The local HTTP endpoints of the relay and the agent (loopback only), each answering or not; a failure is "no answer", never an exception.</summary>
public static class NetClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static async Task<T?> GetAsync<T>(string url) where T : class
    {
        try { return await Http.GetFromJsonAsync<T>(url); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return null; }
    }

    public static Task<VersionStatusData?> GetVersionStatusAsync(int port) => GetAsync<VersionStatusData>($"http://localhost:{port}/version-status");
    public static Task<JoinStatusData?> GetJoinStatusAsync(int port) => GetAsync<JoinStatusData>($"http://localhost:{port}/join-status");
    public static Task<ConnectionStatusData?> GetConnectionStatusAsync(int port) => GetAsync<ConnectionStatusData>($"http://localhost:{port}/connection-status");
    public static Task<RelayLocalStatusData?> GetRelayLocalStatusAsync(int infoPort) => GetAsync<RelayLocalStatusData>($"http://127.0.0.1:{infoPort}/api/local/status");

    public static async Task<bool> PostJoinChoiceAsync(int port, string choice)
    {
        try
        {
            using var r = await Http.PostAsync($"http://localhost:{port}/join-choice?c={Uri.EscapeDataString(choice)}", null);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    /// <summary>A ping time proves reachability but its absence proves nothing (routers drop ICMP): the operation that matters is a TCP connection to the relay's port.</summary>
    public static async Task<bool> CanConnectTcpAsync(string host, int port, int timeoutMs = 3000)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await tcp.ConnectAsync(host.Trim(), port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException or ArgumentException) { return false; }
    }

    public static bool ValidServerAddress(string address) =>
        !string.IsNullOrWhiteSpace(address) && Uri.CheckHostName(address.Trim()) != UriHostNameType.Unknown;
}

/// <summary>The game's logs: where kcd.log is, and the previous launch's kept before the game starts over them.</summary>
public static class GameLogs
{
    public const int HistoryKeep = 6;

    /// <summary>Where earlier launches' logs are kept (one folder per launch); the launcher's bug report bundles them.</summary>
    public static string HistoryFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cairn", "log-history");

    public static string? FindKcdLog(string gameRoot)
    {
        try
        {
            string dir = gameRoot ?? "";
            for (int i = 0; i < 4 && !string.IsNullOrWhiteSpace(dir); i++)
            {
                string candidate = Path.Combine(dir, "kcd.log");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir) ?? "";
            }

            var logs = new List<FileInfo>();
            foreach (var root in SteamLibraryRoots())
            {
                string common = Path.Combine(root, "steamapps", "common");
                if (!Directory.Exists(common)) continue;
                IEnumerable<string> subdirs;
                try { subdirs = Directory.EnumerateDirectories(common); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var d in subdirs)
                {
                    string name = Path.GetFileName(d);
                    if (!name.Contains("Kingdom", StringComparison.OrdinalIgnoreCase) && !name.Contains("KCD", StringComparison.OrdinalIgnoreCase)) continue;
                    try { logs.AddRange(new DirectoryInfo(d).EnumerateFiles("kcd.log", SearchOption.TopDirectoryOnly)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            return logs.Count == 0 ? null : logs.OrderByDescending(f => f.LastWriteTimeUtc).First().FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static IEnumerable<string> SteamLibraryRoots()
    {
        string? steam =
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string
            ?? Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam", "SteamPath", null) as string;
        if (steam is null) yield break;
        yield return steam;
        string vdf = Path.Combine(steam, "config", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };
        IEnumerable<string> lines;
        try { lines = File.ReadLines(vdf); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var line in lines)
        {
            var m = Regex.Match(line.Trim(), @"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            string p = m.Groups[1].Value.Replace(@"\\", @"\");
            if (seen.Add(p)) yield return p;
        }
    }

    /// <summary>
    /// Called right before the game starts: the game writes a new kcd.log at every start and keeps ONE backup, so a second restart after a crash used to overwrite the
    /// only copy of the crash's logs. The logs as they are now (the previous launch's) go to log-history\launch-&lt;time&gt;, the newest <see cref="HistoryKeep"/> are kept. Never fatal.
    /// </summary>
    public static void KeepHistory(string gameRoot, string agentDirectory)
    {
        try
        {
            var files = new List<(string Path, string Name)>();
            string? kcdLog = FindKcdLog(gameRoot);
            if (kcdLog is not null)
            {
                string kcdDir = Path.GetDirectoryName(kcdLog) ?? "";
                files.Add((kcdLog, "kcd.log"));
                files.Add((Path.Combine(kcdDir, "cairn-native.mirror.log"), "cairn-native.mirror.log"));
            }
            if (!string.IsNullOrWhiteSpace(agentDirectory)) files.Add((Path.Combine(agentDirectory, "cairn-native.log"), "cairn-native.log"));
            files = files.Where(f => File.Exists(f.Path)).ToList();
            if (files.Count == 0) return;

            DateTime stamp = files.Max(f => File.GetLastWriteTime(f.Path));
            string dir = Path.Combine(HistoryFolder, $"launch-{stamp:yyyyMMdd-HHmmss}");
            if (Directory.Exists(dir)) return;
            Directory.CreateDirectory(dir);
            foreach (var (path, name) in files)
            {
                try
                {
                    using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var dst = new FileStream(Path.Combine(dir, name), FileMode.CreateNew, FileAccess.Write);
                    src.CopyTo(dst);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            foreach (var old in new DirectoryInfo(HistoryFolder).GetDirectories("launch-*").OrderByDescending(d => d.Name).Skip(HistoryKeep))
            {
                try { old.Delete(recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* log retention must never stop a launch */ }
    }
}

/// <summary>The settings the launcher gives a session (first line it sends): the player's choices and paths, bounded.</summary>
public sealed record SessionConfig(string GameExe, int HostPort, int InfoPort, int VersionPort, uint SteamAppId, bool AllowSteam, bool RelayReuse,
                                   bool ConnectGate, int InjectDelaySeconds, bool MenuTakeover, string AgentPath, string RelayPath, string DllPath, string StartSaves = "")
{
    public static SessionConfig Defaults { get; } = new("", 7778, 5273, 5902, 2429020, true, true, true, 20, true, "", "", "", "");

    public static SessionConfig Parse(JsonElement r)
    {
        string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int I(string n, int d, int min, int max) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= min && i <= max ? i : d;
        bool B(string n, bool d) => r.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.ValueKind == JsonValueKind.True : d;
        var d0 = Defaults;
        uint app = r.TryGetProperty("steamAppId", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetUInt32(out var au) ? au : d0.SteamAppId;
        static string Path0(string s) => s.Length <= 260 && !s.Any(char.IsControl) ? s : "";
        return new SessionConfig(Path0(S("gameExe")), I("hostPort", d0.HostPort, 1, 65535), I("infoPort", d0.InfoPort, 1, 65535), I("versionPort", d0.VersionPort, 1, 65535),
            app, B("allowSteam", d0.AllowSteam), B("relayReuse", d0.RelayReuse), B("connectGate", d0.ConnectGate), I("injectDelaySeconds", d0.InjectDelaySeconds, 1, 300),
            B("menuTakeover", d0.MenuTakeover), Path0(S("agentPath")), Path0(S("relayPath")), Path0(S("dllPath")), Path0(S("startSaves")));
    }
}

/// <summary>The pack this program belongs to: its programs and the names its mod uses (pack.json), found from the program's own place.</summary>
public sealed class SessionPack
{
    public string Root { get; }
    public string FilesDir => Path.Combine(Root, "files");
    private readonly JsonElement? _contract;

    public SessionPack(string? root = null)
    {
        Root = root ?? ModInstall.DefaultPackRoot;
        try
        {
            string path = Path.Combine(Root, "pack.json");
            if (File.Exists(path)) { using var doc = JsonDocument.Parse(File.ReadAllText(path)); _contract = doc.RootElement.Clone(); }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }

    private string Str(string key, string fallback = "") =>
        _contract is { } c && c.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    public string Version => Str("version");

    /// <summary>The release the relay and the agent compare between peers: this pack's MAJOR.MINOR (a patch is compatible).</summary>
    public string Handshake => System.Version.TryParse(Version, out var p) ? $"{p.Major}.{p.Minor}.0" : "0.0.0";

    private string Resolve(string rel) => rel.Length == 0 ? "" : Path.GetFullPath(Path.Combine(FilesDir, rel.Replace('/', Path.DirectorySeparatorChar)));
    public string AgentExe(SessionConfig c) => c.AgentPath.Length > 0 ? c.AgentPath : Resolve(Str("agent"));
    public string RelayExe(SessionConfig c) => c.RelayPath.Length > 0 ? c.RelayPath : Resolve(Str("relay"));
    public string NativeDll(SessionConfig c) => c.DllPath.Length > 0 ? c.DllPath : Resolve(Str("native"));
    public string BrandPak => Resolve(Str("brandPak"));

    public string Lua(string key, string fallback) =>
        _contract is { } c && c.TryGetProperty("lua", out var l) && l.ValueKind == JsonValueKind.Object && l.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback : fallback;
    public string NativeLog => Lua("nativeLog", "kcdmp-native.log");

    /// <summary>The pack's own BIN file entry for a program (its size and sha256), or null.</summary>
    public ManifestEntry? BinEntry(string fullPath)
    {
        string bin = Path.GetFullPath(Path.Combine(FilesDir, "bin")) + Path.DirectorySeparatorChar;
        string full;
        try { full = Path.GetFullPath(fullPath); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!full.StartsWith(bin, StringComparison.OrdinalIgnoreCase)) return null;
        string rel = full[bin.Length..];
        return ModInstall.ReadManifest(Root).FirstOrDefault(e => e.Kind == "BIN" && string.Equals(e.Rel.Replace('/', '\\'), rel, StringComparison.OrdinalIgnoreCase));
    }
}
