// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KcdMp.CairnBridge.Setup;

namespace KcdMp.CairnBridge.Session;

public enum LaunchStage { Idle, WaitingForConnect, Injecting, Verifying, Connected, Failed }

/// <summary>Who to connect to: an address, or a Steam code.</summary>
public sealed record ServerSpec(string Ip, int Port, string? SteamCode, int PingMs = -1);

/// <summary>
/// The connection side of a session for this game, which was the launcher's own code until it moved into the pack (docs/SESSION.md in the launcher repository):
/// the host's relay, the game's start, the CONNECT gate, the plugin loaded into the game, the agent and what it reports. The launcher sends commands and draws the
/// state this sends back; it knows nothing of a relay, an agent or a plugin.
/// </summary>
public sealed class SessionHost
{
    private readonly SessionOut _out;
    private readonly SessionPack _pack;
    private SessionConfig _cfg = SessionConfig.Defaults;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // ---- what the launcher is shown (the snapshot) ----
    private LaunchStage _stage = LaunchStage.Idle;
    private string _status = "";
    private bool _gateOpen = true;
    private string _gateReason = "";
    private string _hostError = "";
    private string _steamState = "off", _steamMessage = "", _refusedNotice = "";
    private string? _steamCode;
    private readonly AgentStatusBanner _banner = new();
    private string _connLine = "", _joinMessage = "", _joinState = "idle";
    private bool _connBad, _joinCanFresh;
    private string _lastPublished = "";

    // ---- what it holds ----
    private Process? _relay, _game, _agent;
    private ServerSpec? _server, _lastLaunch;
    private string? _dllPath;
    private DateTime _gameStartLocal;
    private bool _hosting;
    private CancellationTokenSource? _gateCts, _versionCts, _agentCts, _hostCts;
    private bool _steamFailedShown, _ownWorldShown;

    public SessionHost(SessionOut output, SessionPack? pack = null)
    {
        _out = output;
        _pack = pack ?? new SessionPack();
    }

    /// <summary>The settings the launcher sent; until it has sent them nothing is started (a default would be a guess: ports another program may hold).</summary>
    public bool Configured { get; private set; }
    public SessionConfig Config { get => _cfg; set { _cfg = value; Configured = true; } }

    private bool ConnectEnabled => !_cfg.ConnectGate || _gateOpen;
    private bool RelayUp { get { try { return _relay is { HasExited: false }; } catch (InvalidOperationException) { return false; } } }

    // ------------------------------------------------------------ the snapshot

    public void Publish(bool force = false)
    {
        string json;
        lock (_lock)
        {
            json = JsonSerializer.Serialize(new
            {
                t = "state",
                stage = StageName(_stage),
                status = _status,
                connect = new { enabled = ConnectEnabled, reason = _gateReason },
                conn = new { text = _connLine, bad = _connBad },
                join = new { state = _joinState, message = _joinMessage, canFresh = _joinCanFresh },
                host = new { relay = RelayUp, error = _hostError, steamState = _steamState, steamMessage = _steamMessage, steamCode = _steamCode, refused = _refusedNotice },
                game = _game is { } g && Alive(g),
            });
            if (!force && json == _lastPublished) return;
            _lastPublished = json;
        }
        _out.SendRaw(json);
    }

    private static bool Alive(Process p) { try { return !p.HasExited; } catch (InvalidOperationException) { return false; } }

    public static string StageName(LaunchStage s) => s switch
    {
        LaunchStage.WaitingForConnect => "waiting-connect", LaunchStage.Injecting => "injecting", LaunchStage.Verifying => "verifying",
        LaunchStage.Connected => "connected", LaunchStage.Failed => "failed", _ => "idle",
    };

    private void Set(Action change) { lock (_lock) change(); Publish(); }

    private void Error(string text) => _out.Send(new { t = "error", text });
    private void Message(string title, string text, string next = "", bool addressFallback = false) => _out.Send(new { t = "message", title, text, next, addressFallback });

    /// <summary>A start Windows refused, or a file gone: one plain message by the file's name, with Report a bug beside it.</summary>
    private void Problem(LaunchBlock b, bool launchAnyway = false, string? textOverride = null, string? nextOverride = null) =>
        _out.Send(new { t = "problem", title = b.Title, text = textOverride ?? b.Message, next = nextOverride ?? b.NextStep, reportBug = true, launchAnyway, log = b.LogLine });

    // ------------------------------------------------------------ the host's relay

    /// <summary>The host's relay is up (this install's own, reused or started), or the reason it is not is in the state.</summary>
    public async Task RelayAsync()
    {
        Set(() => _hostError = "");
        if (!RelayUp)
        {
            string relayPath = _pack.RelayExe(_cfg);
            // gone or empty (a quarantine looks like this): one plain message, no path
            if (BlockedBeforeLaunch(relayPath, Path.ChangeExtension(relayPath, ".dll"))) return;
            // the ports may be held by this install's own relay (a launcher closed with the window's X leaves it running): used again, or replaced.
            // Another program's: told, untouched.
            bool startOurs = !_cfg.RelayReuse || await ApplyRelayDecisionAsync(await DecideRelayAsync(relayPath));
            if (startOurs && !await StartRelayAsync(relayPath)) return;
        }
        StartHostStatusPoll();
        Publish(force: true);
    }

    private string SteamGameArg => string.IsNullOrWhiteSpace(_cfg.GameExe) ? "" : $" --steam-game \"{_cfg.GameExe}\"";

    // CAIRN_RELAY_ARGS: extra arguments for the relay, for tests that must not touch a relay already running on this PC (its info port is otherwise appsettings' fixed one)
    private string RelayArguments() => $"--port {_cfg.HostPort}" + (_cfg.AllowSteam ? $" --steam true --steam-app {_cfg.SteamAppId}{SteamGameArg}" : "")
        + (Environment.GetEnvironmentVariable("CAIRN_RELAY_ARGS") is { Length: > 0 and <= 200 } extra ? " " + extra : "");

    private async Task<RelayDecision> DecideRelayAsync(string relayPath)
    {
        var holders = await Task.Run(() => RelayReuse.Holders(relayPath, _cfg.HostPort, _cfg.InfoPort));
        RelayAnswer? answer = holders.Count > 0 && holders.All(h => h.Ours) ? await AskRelayAsync() : null;
        return RelayReuse.Decide(holders, _cfg.HostPort, answer, _pack.Handshake, _cfg.AllowSteam, _cfg.SteamAppId);
    }

    private async Task<RelayAnswer?> AskRelayAsync()
    {
        var st = await NetClient.GetRelayLocalStatusAsync(_cfg.InfoPort);
        return st is null ? null : new RelayAnswer(st.Release, st.Steam.State, st.Steam.AppId);
    }

    /// <summary>Carries a decision out. True: start this install's relay now.</summary>
    private async Task<bool> ApplyRelayDecisionAsync(RelayDecision d)
    {
        switch (d.Plan)
        {
            case RelayPlan.Reuse:
                try
                {
                    _relay = Process.GetProcessById(d.Pid);
                    Info(d.LogLine);
                    return false;
                }
                catch (ArgumentException)
                {
                    Info($"MP-RELAY the relay to reuse (pid {d.Pid}) has just exited; starting this install's");
                    return true;
                }
            case RelayPlan.Replace:
                Info(d.LogLine);
                await Task.Run(() => { foreach (int pid in d.Pids) StopOldRelay(pid); });
                await WaitPortsFreeAsync(TimeSpan.FromSeconds(5));
                return true;
            case RelayPlan.Refuse:
                Info(d.LogLine);
                Set(() => _hostError = d.PlayerMessage);
                return false;
            default:
                return true;
        }
    }

    private void StopOldRelay(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
            if (!p.WaitForExit(5000)) Info($"MP-RELAY the old relay (pid {pid}) did not exit within 5 s");
        }
        catch (ArgumentException) { }   // gone already
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Info($"MP-RELAY the old relay (pid {pid}) could not be stopped: {ex.GetType().Name}"); }
    }

    private async Task WaitPortsFreeAsync(TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (DateTime.UtcNow < until)
        {
            if (PortOwners.ListeningPids(_cfg.HostPort).Count == 0 && PortOwners.ListeningPids(_cfg.InfoPort).Count == 0) return;
            await Task.Delay(200);
        }
    }

    /// <summary>Starts this install's relay. False only when Windows refused it (the player has been told).</summary>
    private async Task<bool> StartRelayAsync(string relayPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = relayPath, Arguments = RelayArguments(), UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(relayPath) ?? "",
            };
            _relay = StartChecked(psi);
            // until it answers, or 8 s: a relay that failed to bind used to die after a fixed 500 ms and looked hosted
            if (_relay is not null) await WaitForRelayAsync(_relay);
            if (_relay is null || !Alive(_relay))
            {
                Info($"The relay exited right after starting (code {ExitCodeOf(_relay)})");
                Set(() => _hostError = RelayExitedMessage(relayPath));
                _relay = null;
            }
            return true;
        }
        catch (LaunchBlockedException lb)
        {
            Problem(lb.Block);
            _relay = null;
            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Info($"Could not start the relay: {ex.GetType().Name}");
            Set(() => _hostError = "Hosting couldn't start on this computer. Check the relay path in Settings, then try again.");
            _relay = null;
            return true;
        }
    }

    private static string ExitCodeOf(Process? p) { try { return p is null ? "?" : p.ExitCode.ToString(); } catch (InvalidOperationException) { return "?"; } }

    private async Task WaitForRelayAsync(Process relay)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        await Task.Delay(300);
        while (DateTime.UtcNow < until)
        {
            if (!Alive(relay)) return;
            if (await AskRelayAsync() is not null) return;
            await Task.Delay(250);
        }
        Info("MP-RELAY the relay did not answer within 8 s; it keeps running");
    }

    private string RelayExitedMessage(string relayPath)
    {
        var holders = RelayReuse.Holders(relayPath, _cfg.HostPort, _cfg.InfoPort);
        var d = RelayReuse.Decide(holders, _cfg.HostPort, null, _pack.Handshake, _cfg.AllowSteam, _cfg.SteamAppId);
        if (d.Plan == RelayPlan.Refuse) { Info(d.LogLine); return d.PlayerMessage; }
        return $"Hosting stopped right after it started. Another copy may already be running on port {_cfg.HostPort}: close it (or restart the computer) and try again.";
    }

    public void StopRelay()
    {
        try { if (_relay is not null && Alive(_relay)) _relay.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Info($"Failed to stop the hosted relay process: {ex.GetType().Name}"); }
        _relay = null;
        _hostCts?.Cancel();
        Set(() => { _steamState = "off"; _steamMessage = ""; _steamCode = null; _refusedNotice = ""; });
    }

    /// <summary>While this session hosts: the relay's Steam state and code, and refused joiner versions.</summary>
    private void StartHostStatusPoll()
    {
        _hostCts?.Cancel();
        var cts = _hostCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            string? lastKey = null;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    if (!RelayUp) { await Task.Delay(1000, cts.Token); if (_relay is null) break; continue; }
                    var st = await NetClient.GetRelayLocalStatusAsync(_cfg.InfoPort);
                    if (st is not null)
                    {
                        string notice = "";
                        if (st.RefusedRelease is { Length: > 0 } rel && st.RefusedSecondsAgo is >= 0 and < 600)
                        {
                            notice = $"A player tried to join with version {rel}; you run {st.Release}. Both players need the same version: whoever is older opens Cairn Client and updates (the update is offered at start, or under Versions & updates, Check for updates).";
                            string key = rel + "@" + DateTime.UtcNow.AddSeconds(-st.RefusedSecondsAgo).ToString("HHmm");
                            if (key != lastKey) { lastKey = key; _out.Send(new { t = "refused", text = notice, key }); }
                        }
                        Set(() =>
                        {
                            _steamState = st.Steam.State;
                            _steamMessage = _cfg.AllowSteam ? st.Steam.Message : "";
                            _steamCode = st.Steam.Code;
                            _refusedNotice = notice;
                        });
                    }
                    await Task.Delay(1500, cts.Token);
                }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    // ------------------------------------------------------------ files and starts Windows may refuse

    private sealed class LaunchBlockedException(LaunchBlock block, Exception inner) : Exception(block.LogLine, inner) { public LaunchBlock Block { get; } = block; }

    private static Process? StartChecked(ProcessStartInfo psi)
    {
        try { return Process.Start(psi); }
        catch (Exception ex) when (LaunchBlocks.FromStartFailure(ex, Path.GetFileName(psi.FileName), installed: true) is { } b) { throw new LaunchBlockedException(b, ex); }
    }

    /// <summary>The launch's own files, before anything starts. True: told, stop.</summary>
    private bool BlockedBeforeLaunch(params string[] appFiles)
    {
        foreach (var path in appFiles)
        {
            string name = "a file whose path is empty in Settings";
            bool exists = false; long length = 0;
            try
            {
                if (!string.IsNullOrWhiteSpace(path)) { var fi = new FileInfo(path); name = fi.Name; exists = fi.Exists; length = exists ? fi.Length : 0; }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { }
            if (LaunchBlocks.FromFileCheck(name, exists, length, installed: true) is { } b) { Problem(b); return true; }
        }
        return false;
    }

    private enum FileFinding { Ok, Missing, Empty, Unreadable, Different }
    private sealed record FileCheck(string Label, string Path, FileFinding Finding, string Detail, bool ModFile);

    private static FileCheck Look(string label, string path, ManifestEntry? expected, bool modFile)
    {
        if (string.IsNullOrWhiteSpace(path)) return new FileCheck(label, "(empty path in Settings)", FileFinding.Missing, "", modFile);
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return new FileCheck(label, fi.FullName, FileFinding.Missing, "", modFile);
            if (fi.Length == 0 && (expected is null || expected.Size > 0)) return new FileCheck(label, fi.FullName, FileFinding.Empty, "0 bytes", modFile);
            string sha;
            try { sha = GameInjector.Sha256Of(fi.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new FileCheck(label, fi.FullName, FileFinding.Unreadable, $"{ex.GetType().Name} (Windows error {ex.HResult & 0xFFFF})", modFile);
            }
            if (expected is not null && !string.Equals(sha, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                return new FileCheck(label, fi.FullName, FileFinding.Different,
                    $"{fi.Length:N0} bytes, sha256 {sha[..12]}; the pack lists {expected.Size:N0} bytes, sha256 {expected.Sha256[..Math.Min(12, expected.Sha256.Length)]}", modFile);
            return new FileCheck(label, fi.FullName, FileFinding.Ok, $"{fi.Length:N0} bytes", modFile);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new FileCheck(label, path, FileFinding.Missing, ex.GetType().Name, modFile);
        }
    }

    private List<FileCheck> CheckLaunchFiles(IEnumerable<string> appFiles)
    {
        var list = new List<FileCheck>();
        foreach (var f in appFiles) list.Add(Look("the mod's folder", f, _pack.BinEntry(f), modFile: false));
        if (!string.IsNullOrWhiteSpace(_cfg.GameExe))
        {
            string root = GameLocator.InstallRootOf(_cfg.GameExe) ?? GameRootOf(_cfg.GameExe);
            string modDir = ModInstall.TargetDir(root, _pack.Root);
            foreach (var e in ModInstall.ReadManifest(_pack.Root).Where(e => e.Kind == "MOD"))
            {
                var inMods = Look("the Modding Tools' Mods folder", Path.Combine(modDir, e.Rel), e, modFile: true);
                list.Add(inMods);
                if (inMods.Finding == FileFinding.Missing)
                    list.Add(Look("the staged copy in the pack store", ModInstall.EntryPath(_pack.Root, e), e, modFile: true));
            }
        }
        return list;
    }

    private async Task<bool> PreLaunchFilesOkAsync(bool skipMod, params string[] appFiles)
    {
        var checks = await Task.Run(() => CheckLaunchFiles(appFiles));
        foreach (var c in checks.Where(c => c.Finding != FileFinding.Ok))
            Info($"MP-LAUNCH file-check {c.Finding.ToString().ToLowerInvariant()} {c.Label}: {c.Path} {c.Detail}");

        // a different file (not what the pack lists) is told, never blocking: a hand-built pak must not stop a game
        var different = checks.Where(c => c.Finding == FileFinding.Different).ToList();
        var bad = checks.Where(c => c.Finding is FileFinding.Missing or FileFinding.Empty or FileFinding.Unreadable).Where(c => !(skipMod && c.ModFile)).ToList();
        if (bad.Count > 1 && bad[0].ModFile) bad = bad.OrderBy(c => c.Label.StartsWith("the staged", StringComparison.Ordinal) ? 1 : 0).ToList();
        if (bad.Count == 0)
        {
            if (different.Count > 0)
                Error($"{string.Join(", ", different.Select(c => Path.GetFileName(c.Path)))} differ from what the mod's pack lists (an older or hand-copied file?). The game starts anyway; reinstall the mod if it misbehaves.");
            return true;
        }
        var first = bad[0];
        var block = BlockFor(first);
        Info(block.LogLine + $" path={first.Path} finding={first.Finding.ToString().ToLowerInvariant()}");
        string text = FindingText(first) + (bad.Count > 1 ? $" ({bad.Count - 1} more: {string.Join(", ", bad.Skip(1).Select(c => Path.GetFileName(c.Path)))}.)" : "");
        Problem(block, launchAnyway: bad.All(c => c.ModFile), textOverride: text, nextOverride: NextStepText(first));
        return false;
    }

    private static LaunchBlock BlockFor(FileCheck c)
    {
        string name = Path.GetFileName(c.Path);
        return c.Finding switch
        {
            FileFinding.Unreadable => new LaunchBlock(LaunchBlockKind.AccessDenied, name, 5),
            FileFinding.Empty => new LaunchBlock(LaunchBlockKind.Quarantined, name, 0),
            _ => new LaunchBlock(LaunchBlockKind.Quarantined, name, 2),
        };
    }

    private static string FindingText(FileCheck c)
    {
        string name = Path.GetFileName(c.Path);
        return c.Finding switch
        {
            FileFinding.Missing => $"{name} is not in {c.Label}. The launcher checked: {c.Path}",
            FileFinding.Empty => $"{name} in {c.Label} is empty (0 bytes), which is what an antivirus leaves when it takes a file. The launcher checked: {c.Path}",
            FileFinding.Unreadable => $"{name} is in {c.Label} but Windows did not let the launcher read it ({c.Detail}): it is locked by another program or blocked. The launcher checked: {c.Path}",
            FileFinding.Different => $"{name} in {c.Label} is not the file the pack lists ({c.Detail}). The launcher checked: {c.Path}",
            _ => $"{name} is fine. The launcher checked: {c.Path}",
        };
    }

    private static string NextStepText(FileCheck c)
    {
        const string howToTell =
            " To see which program removed or blocked it: Windows Security > Virus & threat protection > Protection history lists " +
            "files Windows Security quarantined (restore and allow it there). Smart App Control blocks a program without quarantining " +
            "it and has no exclusions, so an exclusion does not help against it; its blocks show as a notification \"Smart App Control " +
            "blocked...\" (Windows Security > App & browser control). Another antivirus keeps its own quarantine list.";
        const string again = "Reinstalling the mod puts the file back, but it can be removed again until it is allowed.";
        return c.Finding switch
        {
            FileFinding.Unreadable => "Close any program that may have the file open (an antivirus scan, an archive tool, the game) and try again." + howToTell,
            FileFinding.Different => "Reinstall the mod to put back the file the pack lists.",
            _ when c.ModFile => "If you are sure the file is there, the launcher may be looking at a different Modding Tools folder: check the game path in Settings. " + again + howToTell +
                                " You can also LAUNCH ANYWAY: the game starts, and the mod says in the game if it really is missing.",
            _ => again + howToTell,
        };
    }

    public static string GameRootOf(string gamePath)
    {
        string dir = Path.GetDirectoryName(gamePath) ?? "";
        string fallback = dir;
        for (int i = 0; i < 4 && dir.Length > 0; i++)
        {
            if (File.Exists(Path.Combine(dir, "steam_appid.txt"))) return dir;
            dir = Path.GetDirectoryName(dir) ?? "";
        }
        return fallback;
    }

    // ------------------------------------------------------------ launching the game

    internal const string JoinerReadyText = "Stay at the main menu. Don't load a save. Click CONNECT -- you'll join your host's world automatically.";

    public async Task LaunchAsync(ServerSpec server, bool skipModCheck)
    {
        await _launchLock.WaitAsync();
        try { await LaunchCoreAsync(server, skipModCheck); }
        finally { _launchLock.Release(); }
    }

    private async Task LaunchCoreAsync(ServerSpec server, bool skipModCheck)
    {
        _lastLaunch = server;
        if (_stage != LaunchStage.Idle && _stage != LaunchStage.Failed) { Error("A launch is already in progress."); return; }
        if (string.IsNullOrEmpty(_cfg.GameExe) || !File.Exists(_cfg.GameExe)) { Error("Game Executable not found! Please check Settings."); return; }
        if (!GameLocator.IsModdingToolsBuild(_cfg.GameExe))
        {
            Error("That looks like the retail game. KCD2 must be launched from the Modding Tools build (KCD2Mod), which is the only one with the debug API on port 1403 and the " +
                  "separate module DLLs the plugin hooks. Check Settings.");
            return;
        }

        // a Steam host has no address to probe (the agent dials the code). An unreachable address says why, in plain words, from the same test the TEST CONNECTION button runs.
        if (server.SteamCode is null && !(server.PingMs >= 0 || await NetClient.CanConnectTcpAsync(server.Ip, server.Port)))
        {
            await TestConnectionMessageAsync(server, launching: true);
            return;
        }

        string dll = _pack.NativeDll(_cfg), agentPath = _pack.AgentExe(_cfg);
        if (!await PreLaunchFilesOkAsync(skipModCheck, dll, agentPath, Path.ChangeExtension(agentPath, ".dll"))) return;

        bool hosting = RelayUp;
        try
        {
            var psi = new ProcessStartInfo { FileName = _cfg.GameExe, UseShellExecute = false, WorkingDirectory = GameRootOf(_cfg.GameExe) };
            GameLogs.KeepHistory(GameRootOf(_cfg.GameExe), Path.GetDirectoryName(agentPath) ?? "");
            // the agent's first start of a launch (the keys pak). Windows refusing it stops the launch here, before a game is started for nothing.
            if (await RefreshKeysPakAsync(agentPath, GameLocator.InstallRootOf(_cfg.GameExe) ?? GameRootOf(_cfg.GameExe)) is { } keysBlock)
            {
                Problem(keysBlock);
                return;
            }
            _gameStartLocal = DateTime.Now;   // the gate reads only this game's kcd.log
            var game = StartChecked(psi);
            if (game is null) { Error("The game process could not be started."); return; }
            HideTraceServerWindow(game);   // the Modding Tools open their debug console with the game; Cairn has no use for it
            _game = game; _hosting = hosting; _server = server;
            Set(() => { _stage = LaunchStage.WaitingForConnect; _status = "Waiting for the game to start..."; });
            _out.Send(new { t = "game", pid = game.Id, startedAt = _gameStartLocal.ToString("o"), hosting });
            WatchGameExit(game);
            StartConnectGate(hosting, server);

            if (!await WaitForInjectableAsync(game, _cfg.InjectDelaySeconds))
            {
                Error(Alive(game) ? $"WHGame.dll did not load within {_cfg.InjectDelaySeconds}s, so the DLL was not injected." : "Game process exited unexpectedly before injection.");
                ResetLaunchState();
                return;
            }

            _dllPath = dll;
            // the joiner's flow is the main menu (a joiner who loads his own save plays alone, in a separate world); only the host loads a save first
            Set(() => _status = hosting
                ? (_cfg.MenuTakeover ? "Press Start Game on the game's main menu. The launcher connects by itself once your world has loaded."
                                     : "Load into your save, then click CONNECT once you can see and move your character.")
                : (_cfg.MenuTakeover ? "Press Join Game on the game's main menu. You join as soon as your host is ready." : JoinerReadyText));
        }
        catch (LaunchBlockedException lb)
        {
            Problem(lb.Block);
            ResetLaunchState();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Info($"Launch failed: {ex.GetType().Name}");
            Error("The game couldn't be started. Check the game path in Settings, then try again.");
            ResetLaunchState();
        }
    }

    private static async Task<bool> WaitForInjectableAsync(Process game, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            if (!Alive(game)) return false;
            try
            {
                game.Refresh();
                foreach (ProcessModule module in game.Modules)
                    if (string.Equals(module.ModuleName, "WHGame.dll", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { /* the module list is not readable yet */ }
            await Task.Delay(500);
        }
        return Alive(game);
    }

    /// <summary>The agent builds the dice keys from the player's own game files; a failure costs the keys only. Windows refusing the agent itself is returned.</summary>
    private async Task<LaunchBlock?> RefreshKeysPakAsync(string agentPath, string gameRoot)
    {
        try
        {
            string modDir = Path.Combine(gameRoot, "Mods", ModInstall.ModFolder(_pack.Root));
            if (!Directory.Exists(modDir)) { Info($"keys pak: no mod folder (Mods\\{ModInstall.ModFolder(_pack.Root)}) in the game root; not built"); return null; }
            var r = await AgentHelper.RunAsync<KeysPakResult>(agentPath, $"--keys-pak --game-root \"{gameRoot}\" --mod-dir \"{modDir}\"", "KEYS-PAK", TimeSpan.FromSeconds(20));
            if (r is null) Info("keys pak: the agent gave no answer; the dice keys may be missing");
            else if (r.Ok) Info($"keys pak: {r.Action} from {r.Source} ({r.Detail})");
            else Info($"keys pak: not built -- {r.Detail}; the dice keys may be missing");
        }
        catch (Exception ex) when (LaunchBlocks.FromStartFailure(ex, Path.GetFileName(agentPath), installed: true) is { } b) { return b; }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { Info($"keys pak: failed ({ex.GetType().Name}); the dice keys may be missing"); }
        return null;
    }

    private sealed class KeysPakResult { public bool Ok { get; set; } public string Action { get; set; } = ""; public string Detail { get; set; } = ""; public string? Source { get; set; } }

    private void WatchGameExit(Process game)
    {
        _ = Task.Run(async () =>
        {
            try { await game.WaitForExitAsync(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (!ReferenceEquals(_game, game)) return;
            Info("MP-LAUNCH the game exited");
            // a relay started for the hosted game goes with it (left running it holds the host port); so does the agent (an update of the mod can then replace its files);
            // the Modding Tools' helper programs go too, so Steam stops showing the tools as running
            StopRelay();
            StopAgent();
            CloseModdingTools();
            _game = null;
            ResetLaunchState();
            _out.Send(new { t = "game_exit" });
        });
    }

    public static readonly string[] ModdingToolsProcesses = ["trace_server", "trace_server.run", "trace_server_launcher"];

    public static void CloseModdingTools() => CloseProcesses(ModdingToolsProcesses);

    public static void CloseProcesses(IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); } catch (InvalidOperationException) { continue; }
            foreach (var p in procs)
            {
                try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { p.Dispose(); }
            }
        }
    }

    /// <summary>The game and the Modding Tools' helper programs, closed (the launcher's "close the game" button).</summary>
    public void CloseGameAndTools()
    {
        var names = new List<string>(ModdingToolsProcesses);
        string? exe = null;
        if (_game is { } g && Alive(g)) { try { exe = g.ProcessName; } catch (InvalidOperationException) { } }
        names.Insert(0, string.IsNullOrEmpty(exe) ? "KingdomCome" : exe);
        CloseProcesses(names);
    }

    private static void HideTraceServerWindow(Process game) => TraceServerWindow.HideWhileRunning(game);

    private void ResetLaunchState()
    {
        Info($"MP-LAUNCH launch state reset (stage was {_stage})");
        _versionCts?.Cancel(); _versionCts = null;
        StopConnectGate();
        lock (_lock)
        {
            _stage = LaunchStage.Idle; _status = ""; _dllPath = null; _server = null;
            _gateOpen = true; _gateReason = "";
        }
        Publish();
    }

    public void Cancel() => ResetLaunchState();

    // ------------------------------------------------------------ CONNECT waits until it can work

    private void StartConnectGate(bool hosting, ServerSpec server)
    {
        StopConnectGate();
        lock (_lock) { _gateOpen = true; _gateReason = ""; }
        if (!_cfg.ConnectGate) { Publish(); return; }
        lock (_lock) { _gateOpen = false; _gateReason = hosting ? ConnectGateRule.LoadSaveFirst : ConnectGateRule.HostNotReady; }
        Info($"MP-LAUNCH connect gate closed ({(hosting ? "host: the game's log" : "joiner: the host's relay")}) -- {_gateReason}");
        Publish();
        var cts = _gateCts = new CancellationTokenSource();
        _ = hosting ? HostGateLoopAsync(_gameStartLocal, cts.Token) : JoinerGateLoopAsync(server, cts.Token);
    }

    private void StopConnectGate() { _gateCts?.Cancel(); _gateCts = null; }

    private async Task HostGateLoopAsync(DateTime gameStartLocal, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        KcdLogFollower? follower = null;
        double nextFind = 0;
        string root = string.IsNullOrWhiteSpace(_cfg.GameExe) ? "" : GameRootOf(_cfg.GameExe);
        while (!ct.IsCancellationRequested)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (follower is null && now >= nextFind)
            {
                if (await Task.Run(() => GameLogs.FindKcdLog(root)) is string path) follower = new KcdLogFollower(path, gameStartLocal);
                else nextFind = now + 5;
            }
            if (follower is not null) await Task.Run(() => follower.Poll(now));
            bool reading = follower?.Reading == true;
            var v = ConnectGateRule.ForHost(follower?.State.Stage ?? GameStage.Starting, follower?.State.WorldSinceS ?? double.NaN, reading, (DateTime.Now - gameStartLocal).TotalSeconds, now);
            SetGate(v, reading ? $"game {follower!.State.Stage.ToString().ToLowerInvariant()}" : "the game's log is not readable yet", ct);
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task JoinerGateLoopAsync(ServerSpec server, CancellationToken ct)
    {
        string agent = _pack.AgentExe(_cfg);
        string args = server.SteamCode is null
            ? $"--test-connection --host {server.Ip} --port {server.Port}"
            : $"--test-connection --steam \"{server.SteamCode}\" --steam-app {_cfg.SteamAppId}{SteamGameArg}";
        while (!ct.IsCancellationRequested)
        {
            TestConnectionData? r = null;
            try { r = await AgentHelper.RunAsync<TestConnectionData>(agent, args, "TEST-CONNECTION", TimeSpan.FromSeconds(45)); }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { Info($"MP-LAUNCH connect gate: the host check did not run ({ex.GetType().Name})"); }
            if (ct.IsCancellationRequested) break;
            var answer = r is null ? HostAnswer.Unknown : ConnectGateRule.FromTest(r.Reachable, r.Kind, r.HostConnected);
            var v = ConnectGateRule.ForJoiner(answer);
            SetGate(v, $"host {answer.ToString().ToLowerInvariant()}", ct);
            if (v.Open) break;   // the host is in (or the relay cannot say): CONNECT stays open
            try { await Task.Delay(server.SteamCode is null ? 8000 : 12000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void SetGate(GateView v, string detail, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;   // CONNECT was pressed (or the launch ended): this loop no longer decides
        lock (_lock)
        {
            if (v.Open == _gateOpen && v.Reason == _gateReason) return;
            _gateOpen = v.Open; _gateReason = v.Reason;
        }
        Info($"MP-LAUNCH connect gate {(v.Open ? "open" : "closed")} ({detail}){(v.Open ? "" : " -- " + v.Reason)}");
        Publish();
    }

    // ------------------------------------------------------------ CONNECT: the plugin into the game, then the agent

    public async Task ConnectAsync()
    {
        await _launchLock.WaitAsync();
        try { await ConnectCoreAsync(); }
        finally { _launchLock.Release(); }
    }

    private async Task ConnectCoreAsync()
    {
        var game = _game; var server = _server; var dll = _dllPath;
        if (game is null || server is null || dll is null) return;
        if (!Alive(game)) { Error("The game process has already exited."); ResetLaunchState(); return; }
        if (!ConnectEnabled) { Info($"MP-LAUNCH CONNECT not taken: {_gateReason}"); return; }
        StopConnectGate();
        string waitingText = _status;
        try
        {
            Set(() => { _stage = LaunchStage.Injecting; _status = "Injecting..."; });

            var inject = await InjectAsync(game.Id, dll);
            if (!inject.Ok) { Error(inject.Message); ResetLaunchState(); return; }

            Set(() => { _stage = LaunchStage.Verifying; _status = "Verifying the plugin actually attached..."; });
            if (!await VerifyInjectionAsync(dll, game.Id))
            {
                Error("The plugin loaded but never saw the game running -- you likely clicked Connect before the save had fully loaded. It cannot retry from here: Windows will not " +
                      "reload an already-loaded DLL into the same game process. Close the game, click Launch again, and wait until you can move your character before clicking Connect.");
                Set(() => { _stage = LaunchStage.Failed; _status = "Injection did not take. Restart the game to try again."; });
                return;
            }

            // --hosting is purely cosmetic (Discord Rich Presence text) -- and the relay's host claim: the relay makes this agent the authority whatever order people connect in
            StartAgent(server);
            Set(() => { _stage = LaunchStage.Connected; _status = "The game is ready. The line at the bottom of this window shows the connection; you can close this."; });
            _out.Send(new { t = "toast", text = "Connected. The game is ready." });
        }
        catch (LaunchBlockedException lb)
        {
            // Windows refused the agent. Nothing reached the game (or the plugin is in and waits), so once the player has allowed the file, CONNECT works again in this same game
            Problem(lb.Block);
            lock (_lock) { _stage = LaunchStage.WaitingForConnect; _status = waitingText; _gateOpen = true; _gateReason = ""; }
            Publish();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Info($"Connect failed: {ex.GetType().Name}");
            Error("The multiplayer part couldn't be started. Try again; if it keeps failing, send the logs with Report a bug.");
            ResetLaunchState();
        }
    }

    private async Task<InjectResult> InjectAsync(int pid, string dllPath)
    {
        // the DLL's hash as the pack's own manifest lists it (a path chosen in settings is not the pack's: no hash to compare)
        string? sha = _pack.BinEntry(dllPath)?.Sha256;
        var r = await Task.Run(() => GameInjector.Inject(pid, dllPath, _cfg.GameExe, sha));
        Info(r.LogLine + (r.Ok ? (sha is null ? " (the DLL's hash not compared: not the pack's own file)" : " (sha256 matches the pack's manifest)") : ""));
        return r;
    }

    /// <summary>Confirms the injection took by reading the plugin's own log beside it, rather than trusting that LoadLibrary succeeded.</summary>
    private async Task<bool> VerifyInjectionAsync(string dllPath, int pid)
    {
        var dir = Path.GetDirectoryName(dllPath) ?? "";
        var logPaths = new[] { Path.Combine(dir, _pack.NativeLog), Path.Combine(dir, "cairn-native.log") };
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var attachMarker = $"pid={pid}";
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                foreach (var logPath in logPaths)
                {
                    if (!File.Exists(logPath)) continue;
                    string text;
                    using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(stream)) text = await reader.ReadToEndAsync();
                    var attachIdx = text.LastIndexOf(attachMarker, StringComparison.Ordinal);
                    if (attachIdx < 0) continue;
                    var tail = text[attachIdx..];
                    var match = Regex.Match(tail, @"MAIN: (\d+) frames after ~\d+ ms -- tick is live");
                    if (match.Success) return int.Parse(match.Groups[1].Value) > 0;
                    if (tail.Contains("tick never fired")) return false;
                }
            }
            catch (IOException) { /* the plugin still holds a write handle: retry */ }
            await Task.Delay(300);
        }
        return false;   // inconclusive is a failure: clicked too early, or the save never finished loading
    }

    // ------------------------------------------------------------ the agent

    private string AgentProcessName() => Path.GetFileNameWithoutExtension(_pack.AgentExe(_cfg)) is { Length: > 0 } n ? n : "";

    /// <summary>Starts the session agent for <paramref name="server"/> (address or Steam code), replacing any running one.</summary>
    public void StartAgent(ServerSpec server)
    {
        string agentPath = _pack.AgentExe(_cfg);
        bool isHosting = RelayUp;   // the relay makes this agent the authority whatever order people connect in
        string target = server.SteamCode is { } code
            ? $"--steam \"{code}\" --steam-app {_cfg.SteamAppId}{SteamGameArg}"
            : $"--host {server.Ip} --port {server.Port}";
        var psi = new ProcessStartInfo
        {
            FileName = agentPath, Arguments = target + (isHosting ? " --hosting" : ""), UseShellExecute = false,
            CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(agentPath) ?? "",
        };
        // where the start saves are: the agent builds a host's or a joiner's new character from them, or sends the host's to a joiner
        if (StartSaveRoot() is { Length: > 0 } startSaves && Directory.Exists(startSaves)) psi.Environment["KCDMP_START_SAVE"] = startSaves;

        StopAgent();
        _agent = StartChecked(psi);
        Info($"Agent started via {(server.SteamCode is null ? "address" : "Steam")}");
        _server = server;
        _steamFailedShown = false;
        _versionCts?.Cancel();
        _versionCts = new CancellationTokenSource();
        _ = PollVersionMismatchAsync(_versionCts.Token);
        _agentCts?.Cancel();
        _agentCts = new CancellationTokenSource();
        _banner.Start(server.SteamCode is not null, _clock.Elapsed.TotalSeconds);
        SyncBanner();
        _ = PollAgentStatusAsync(_agent, _agentCts.Token);
    }

    /// <summary>The folder of the start saves (the agent is told, as it runs from the mod's own folder).</summary>
    private string StartSaveRoot() => Environment.GetEnvironmentVariable("CAIRN_START_SAVE") is { Length: > 0 } env ? env : _cfg.StartSaves;

    /// <summary>Kills the agent this session started, plus any other copy by name: only one agent may represent this player to a relay at a time (two fight over one ghost forever).</summary>
    public void StopAgent()
    {
        try { if (_agent is not null && Alive(_agent)) _agent.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Info($"Failed to stop the tracked agent process: {ex.GetType().Name}"); }
        _agent = null;
        string name = AgentProcessName();
        if (name.Length == 0) return;
        try
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try { proc.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Info($"Failed to stop stray {name} (pid {proc.Id})"); }
                finally { proc.Dispose(); }
            }
        }
        catch (InvalidOperationException ex) { Info($"Failed to enumerate {name} processes: {ex.GetType().Name}"); }
    }

    private void SyncBanner()
    {
        lock (_lock)
        {
            _connLine = _banner.ConnLine; _connBad = _banner.ConnBad;
            _joinMessage = _banner.JoinMessage; _joinState = _banner.JoinState; _joinCanFresh = _banner.ShowFreshJoin;
        }
        Publish();
    }

    private async Task PollAgentStatusAsync(Process? agent, CancellationToken ct)
    {
        bool errorLogged = false;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            bool alive = agent is not null && Alive(agent);
            try
            {
                var cs = alive ? await NetClient.GetConnectionStatusAsync(_cfg.VersionPort) : null;
                var js = alive ? await NetClient.GetJoinStatusAsync(_cfg.VersionPort) : null;
                // Steam failed: offer the address right here, in this same launch
                if (cs is { State: "failed", Via: "steam" } && !_steamFailedShown && cs.Kind != "Lost")
                {
                    _steamFailedShown = true;
                    Message("STEAM CONNECTION FAILED", cs.Message, "", addressFallback: true);
                }
                string? change = _banner.Apply(cs, js, alive, _clock.Elapsed.TotalSeconds);
                if (change is not null) { Info(change); SyncBanner(); }
                // a joiner connected from his own save -- a message once, besides the in-game line
                if (_banner.OwnWorld && !_ownWorldShown)
                {
                    _ownWorldShown = true;
                    Info("Agent status: this game is in its own world, not the host's");
                    Message("YOU LOADED YOUR OWN SAVE", _banner.JoinMessage);
                }
                else if (!_banner.OwnWorld) _ownWorldShown = false;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
            {
                if (!errorLogged) { errorLogged = true; Info($"Agent status poll: an iteration failed ({ex.GetType().Name}); the loop goes on"); }
            }
            if (!alive) break;
        }
    }

    /// <summary>The first connected peer whose release differs from this mod's, once, for a bounded window (a hard protocol refusal never gets this far).</summary>
    private async Task PollVersionMismatchAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
            var status = await NetClient.GetVersionStatusAsync(_cfg.VersionPort);
            var peer = status?.Peers.FirstOrDefault();
            if (peer is null) continue;   // the agent is not up yet, or no peer connected yet
            string mine = _pack.Handshake;
            var comparison = ReleaseVersionCompare.Compare(mine, peer.ReleaseVersion);
            if (comparison == ReleaseVersionComparison.Equal) return;
            string text = comparison switch
            {
                ReleaseVersionComparison.LocalIsOlder => $"Your mod is {mine}, your host's is {peer.ReleaseVersion} -- update your mod (the game page offers it).",
                ReleaseVersionComparison.LocalIsNewer => $"Your mod is {mine}, your host's is {peer.ReleaseVersion} -- they'll need to update theirs.",
                _ => "Versions don't match -- make sure everyone's on the latest release.",
            };
            _out.Send(new { t = "version_mismatch", text });
            return;
        }
    }

    /// <summary>The first-join answer ("bring" or "fresh"), sent to the agent; it asks the host only after it.</summary>
    public async Task ChoiceAsync(string choice)
    {
        bool ok = await NetClient.PostJoinChoiceAsync(_cfg.VersionPort, choice);
        lock (_lock)
        {
            _joinMessage = ok ? (choice == "bring" ? "Bringing your character..." : "Starting fresh...") : "The choice did not reach the agent -- try again.";
            _joinState = ok ? "chosen" : _joinState;
        }
        Publish();
    }

    /// <summary>After a Steam failure: the same game, the agent restarted with the host's address.</summary>
    public void FallbackToAddress(string host, int port)
    {
        if (!NetClient.ValidServerAddress(host) || port is <= 0 or > 65535) { Message("STEAM CONNECTION FAILED", "That address doesn't look right. Type it like 203.0.113.7:7778 or myhost.example.net:7778.", "", addressFallback: true); return; }
        try { StartAgent(new ServerSpec(host, port, null)); }
        catch (LaunchBlockedException lb) { Problem(lb.Block); }
    }

    // ------------------------------------------------------------ the agent's one-shot helpers

    public async Task FriendsAsync()
    {
        string agent = _pack.AgentExe(_cfg);
        if (!File.Exists(agent)) { Error("The multiplayer agent is missing. Reinstall the mod, or check the Agent Path in Settings."); return; }
        try
        {
            var r = await AgentHelper.RunAsync<SteamFriendsData>(agent, $"--steam-friends --steam-app {_cfg.SteamAppId}{SteamGameArg}", "STEAM-FRIENDS", TimeSpan.FromSeconds(25));
            _out.Send(new { t = "friends", state = r?.State ?? "no-answer", message = r?.Message ?? "", next = r?.Next ?? "", friends = (r?.Friends ?? new()).Select(f => new { name = f.Name, code = f.Code, release = f.Release }) });
        }
        catch (Exception ex) when (LaunchBlocks.FromStartFailure(ex, Path.GetFileName(agent), installed: true) is { } b) { Problem(b); }
    }

    public async Task TestConnectionAsync(ServerSpec server) => await TestConnectionMessageAsync(server, launching: false);

    private async Task TestConnectionMessageAsync(ServerSpec server, bool launching)
    {
        string agent = _pack.AgentExe(_cfg);
        if (!File.Exists(agent)) { Error("The multiplayer agent is missing. Reinstall the mod, or check the Agent Path in Settings."); return; }
        Message("TESTING...", server.SteamCode is null ? "Reaching the host..." : "Reaching the host through Steam (up to 20 seconds)...");
        try
        {
            var r = await AgentHelper.RunAsync<TestConnectionData>(agent, server.SteamCode is null
                    ? $"--test-connection --host {server.Ip} --port {server.Port}"
                    : $"--test-connection --steam \"{server.SteamCode}\" --steam-app {_cfg.SteamAppId}{SteamGameArg}",
                "TEST-CONNECTION", TimeSpan.FromSeconds(45));
            var (msg, ok) = DescribeTest(r);
            Message(ok ? "CONNECTION OK" : launching ? "CAN'T REACH THE HOST" : "CONNECTION TEST", msg);
        }
        catch (Exception ex) when (LaunchBlocks.FromStartFailure(ex, Path.GetFileName(agent), installed: true) is { } b) { Problem(b); }
    }

    private (string Message, bool Ok) DescribeTest(TestConnectionData? r)
    {
        if (r is null)
        {
            Info("Connection test: no answer from the agent helper");
            return ("The test didn't finish. Try again; if it keeps failing, send the logs with Report a bug.", false);
        }
        Info($"Connection test via={r.Via} kind={r.Kind} reachable={r.Reachable} rttMs={r.RttMs} connectMs={r.ConnectMs} hostRelease={r.HostRelease} hostConnected={r.HostConnected} steamPingMs={r.SteamPingMs} relayed={r.Relayed} detail={r.Detail}");
        bool ok = r.Kind == "None";
        string msg = ok && r.SteamPingMs > 0 ? $"{r.Message} Steam ping {r.SteamPingMs} ms{(r.Relayed == true ? " (through Steam's relays)" : "")}." : r.Message;
        return ((msg + " " + r.Next).Trim(), ok);
    }

    private void Info(string line) => _out.Log(line);
}

/// <summary>Runs the agent as a one-shot helper (<c>--test-connection</c>, <c>--steam-friends</c>, ...) and returns the one JSON line it prints after its tag.
/// stderr is read and thrown away: steam_api prints the account's id there, and nothing of it may reach a log.</summary>
public static class AgentHelper
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<T?> RunAsync<T>(string agentPath, string arguments, string tag, TimeSpan timeout) where T : class
    {
        var psi = new ProcessStartInfo
        {
            FileName = agentPath, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(agentPath) ?? "",
        };
        using var p = Process.Start(psi);
        if (p is null) return null;
        _ = p.StandardError.ReadToEndAsync();   // discarded on purpose
        using var cts = new CancellationTokenSource(timeout);
        string? found = null;
        try
        {
            while (await p.StandardOutput.ReadLineAsync(cts.Token) is { } line)
                if (line.StartsWith(tag + " ", StringComparison.Ordinal)) { found = line[(tag.Length + 1)..]; break; }
        }
        catch (OperationCanceledException) { }
        try { if (!p.HasExited) p.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (found is null) return null;
        try { return JsonSerializer.Deserialize<T>(found, Json); } catch (JsonException) { return null; }
    }
}

/// <summary>The Modding Tools open a debug console window with the game; Cairn has no use for it, so it is hidden while the game runs.</summary>
public static class TraceServerWindow
{
    private const int SW_HIDE = 0;
    private const string ProcessPrefix = "trace_server";
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    public static void HideWhileRunning(Process game)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                int hidden = 0;
                while (!game.HasExited)
                {
                    hidden += HideNow();
                    await Task.Delay(hidden == 0 ? 250 : 2000);   // quick while waiting for it to appear, then a slow watch for a restart
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
    }

    public static int HideNow()
    {
        int count = 0;
        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                using var p = Process.GetProcessById((int)pid);
                if (p.ProcessName.StartsWith(ProcessPrefix, StringComparison.OrdinalIgnoreCase) && ShowWindow(hWnd, SW_HIDE)) count++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* the process went away between the enumeration and the lookup */ }
            return true;
        }, IntPtr.Zero);
        return count;
    }
}
