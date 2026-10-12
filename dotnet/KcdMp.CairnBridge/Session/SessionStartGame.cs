// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Collections.Concurrent;
using System.Diagnostics;
using KcdMp.CairnBridge.Setup;

namespace KcdMp.CairnBridge.Session;

/// <summary>The names the mod's Lua and log use (pack.json's "lua" object), from the pack this program belongs to.</summary>
public static class ModLua
{
    public static SessionPack Pack { get; set; } = new();
    private static string Name(string key, string fallback) => Pack.Lua(key, fallback);
    public static string EventPrefix => Name("eventPrefix", "[KCD2-MP-EVT] v1 ");
    public static string StartGameEvent => Name("startGameEvent", "w159");
    public static string HelperFlag => Name("helperFlag", "--w159");
    public static string HelperTag => Name("helperTag", "W159");
    public static string StartGameModel => Name("startGameModel", "KCD2MP_W159Model");
    public static string StartGameTick => Name("startGameTick", "KCD2MP_W159Tick");
    public static string StartGameRecapStart => Name("startGameRecapStart", "KCD2MP_W159RecapStart");
    public static string StartGameDisarm => Name("startGameDisarm", "KCD2MP_W159Disarm");
    public static string JoinFlowLoad => Name("joinFlowLoad", "KCD2MP_Wo124LoadGame");
    public static string Call(string function, string args = "") => $"if {function} then {function}({args}) end";
}

public sealed class StartGameWorld { public int Pl { get; set; } public string Name { get; set; } = ""; public string Label { get; set; } = ""; }
public sealed class StartGameStagedData { public int Pl { get; set; } public string Name { get; set; } = ""; public string Path { get; set; } = ""; public string SeedTag { get; set; } = ""; }
public sealed class StartGameSavesData
{
    public bool Ok { get; set; }
    public List<StartGameWorld> Usable { get; set; } = new();
    public string Hidden { get; set; } = "";
    public int HiddenRegular { get; set; }
    public int HiddenCopies { get; set; }
    public List<int> Free { get; set; } = new();
    public StartGameStagedData? Staged { get; set; }
}
public sealed class StartGameStageData { public bool Ok { get; set; } public int Pl { get; set; } public string Name { get; set; } = ""; public string SeedTag { get; set; } = ""; public string Why { get; set; } = ""; }
public sealed class StartGameUnstageData { public bool Ok { get; set; } public int Removed { get; set; } public int Kept { get; set; } public List<string> Lines { get; set; } = new(); }

/// <summary>
/// New Game without the prologue: Start Game / Join Game on the main menu of a game this session started, and the host's start save and the joiner's character behind
/// them. Before the game starts the first-run pages are checked (read only), a host's bundled start save is staged into a free playline with a new playthrough seed, and
/// the player's own worlds are listed by the agent's helper. While the game sits at its main menu the menu is armed and redrawn through the game's own console, its
/// choices are read from kcd.log and each is carried out with the calls that already exist. When the game exits a start save that was never played is taken away again.
/// (This was the launcher's own code until it moved into the pack.)
/// </summary>
public sealed partial class SessionHost
{
    private volatile bool _takeover;
    private string _joinChoice = "";            // "fresh:<style>" / "bring" once the joiner pressed Join
    private string _menuJoin = "idle";         // idle | waiting | joining (what the joiner's menu shows)
    private StartGameSavesData? _saves;
    private string _newWhy = "";
    private readonly ConcurrentQueue<MenuTakeoverRule.MenuChoice> _choices = new();
    private string _startRoot = "";
    private List<string> _styles = new();

    private const int TickMs = 250;
    private const int StopVideoSettleMs = 1500;   // the agent's own settle after wh_ui_StopMovie

    private string StartSaveRootPath() => Environment.GetEnvironmentVariable("CAIRN_START_SAVE") is { Length: > 0 } env ? env : _cfg.StartSaves;

    /// <summary>Before the game starts. False = the launch stops here (the first-run pages are not accepted: the game would show them first, under a menu this redraws).</summary>
    private async Task<bool> StartGameBeforeLaunchAsync(bool hosting)
    {
        var consent = await Task.Run(() => ConsentFlags.Read(ConsentFlags.UserFolder()));
        Info($"MP-StartGame first-run pages: {consent.State} ({consent.Detail})");
        if (consent.State is ConsentFlags.State.Missing or ConsentFlags.State.Stale) { Error(ConsentFlags.AcceptMessage); return false; }
        _joinChoice = ""; _menuJoin = "idle"; _saves = null; _newWhy = "";
        while (_choices.TryDequeue(out _)) { }
        if (!_cfg.MenuTakeover) return true;

        string agent = _pack.AgentExe(_cfg);
        _startRoot = StartSaveRootPath();
        _styles = MenuTakeoverRule.Styles.Where(st => _startRoot.Length > 0 && Directory.Exists(Path.Combine(_startRoot, st))
            && Directory.GetFiles(Path.Combine(_startRoot, st), "*.whs").Length == 1).ToList();
        // a joiner may pick any playstyle: a start save it does not have is asked of the host, whose own it is (the agent does it)
        if (!hosting) _styles = MenuTakeoverRule.Styles.ToList();
        Info($"MP-StartGame start saves installed: {(_styles.Count == 0 ? "none" : string.Join(", ", _styles))}");
        // a start save an earlier run staged and never played (the launcher closed with its game still open)
        if (await AgentHelper.RunAsync<StartGameUnstageData>(agent, $"{ModLua.HelperFlag} unstage", ModLua.HelperTag, TimeSpan.FromSeconds(20)) is { } u && u.Lines.Count > 0)
            Info($"MP-StartGame earlier start saves: {string.Join("; ", u.Lines)}");
        if (hosting)
        {
            var st = await AgentHelper.RunAsync<StartGameStageData>(agent, $"{ModLua.HelperFlag} stage --source \"{_startRoot}\"", ModLua.HelperTag, TimeSpan.FromSeconds(30));
            if (st is { Ok: true }) Info($"MP-StartGame start save staged: playline{st.Pl}/{st.Name} (new world {st.SeedTag})");
            else
            {
                _newWhy = st is null ? "The start save could not be prepared (see the launcher log)." : PlainNewWhy(st.Why);
                Info($"MP-StartGame no new adventure this launch: {st?.Why ?? "the helper did not answer"}");
            }
        }
        PlaceLogo();
        _saves = await AgentHelper.RunAsync<StartGameSavesData>(agent, $"{ModLua.HelperFlag} saves", ModLua.HelperTag, TimeSpan.FromSeconds(60));
        Info($"MP-StartGame worlds: {_saves?.Usable.Count ?? -1} usable, staged={(_saves?.Staged is { } s ? $"playline{s.Pl}/{s.Name}" : "none")}, hidden=\"{_saves?.Hidden ?? ""}\"");
        return true;
    }

    private static string PlainNewWhy(string why) =>
        why.StartsWith("All five", StringComparison.Ordinal) ? why
        : why.StartsWith("no start save", StringComparison.Ordinal) ? "This installation has no start saves (they come with the mod)."
        : "The start save could not be prepared: " + why;

    private string StartGameModel(bool hosting)
    {
        var s = _saves;
        var worlds = (s?.Usable ?? new()).Select(w => new MenuTakeoverRule.WorldEntry(w.Pl, w.Name, w.Label)).ToList();
        var adv = s?.Staged is { } st && st.Pl is >= 0 and <= 4 ? new MenuTakeoverRule.WorldEntry(st.Pl, st.Name, "") : null;
        string why = adv is not null ? "" : _newWhy.Length > 0 ? _newWhy : "No new adventure is available.";
        var (bring, msg) = MenuTakeoverRule.JoinChoice(s?.Usable.Count ?? 0, s?.HiddenRegular ?? 0, s?.HiddenCopies ?? 0);
        return MenuTakeoverRule.ModelCall(hosting, worlds, s?.Hidden ?? "", adv, why, _menuJoin, bring, msg, _styles, _menuJoin == "joining" ? _joinMessage : "");
    }

    /// <summary>The menu's driver, from the game's start until it exits.</summary>
    private void StartGameDriver(Process game, bool hosting, DateTime gameStartLocal)
    {
        _takeover = _cfg.MenuTakeover;
        if (!_takeover) { Info("MP-StartGame MenuTakeover off: the game's own menu"); return; }
        _ = Task.Run(() => StartGameDriverAsync(game, hosting, gameStartLocal));
    }

    private async Task StartGameDriverAsync(Process game, bool hosting, DateTime gameStartLocal)
    {
        using var console = new GameConsole();
        var clock = Stopwatch.StartNew();
        KcdLogFollower? follower = null;
        double nextFind = 0, nextBeat = 0;
        long lastSeq = -1;
        string pushed = "";
        bool loadIssued = false, armedLogged = false, choicePosted = false, menuLoad = false, hostConnectTried = false;
        bool recapPending = false, recapSent = false;
        GameStage lastStage = GameStage.Starting;
        DateTime connectedAt = DateTime.MinValue;
        string root = string.IsNullOrWhiteSpace(_cfg.GameExe) ? "" : GameRootOf(_cfg.GameExe);
        Info($"MP-StartGame menu driver on ({(hosting ? "host" : "joiner")})");
        try
        {
            while (!game.HasExited)
            {
                double now = clock.Elapsed.TotalSeconds;
                if (follower is null && now >= nextFind)
                {
                    if (await Task.Run(() => GameLogs.FindKcdLog(root)) is string path)
                    {
                        follower = new KcdLogFollower(path, gameStartLocal);
                        follower.OnLine = line =>
                        {
                            if (MenuTakeoverRule.Parse(line) is { } c) { Info($"MP-StartGame menu line read: {c.Kind} #{c.Seq}"); _choices.Enqueue(c); }
                        };
                        Info($"MP-StartGame reading the game's log: {path}");
                    }
                    else nextFind = now + 5;
                }
                follower?.Poll(now);
                var stage = follower?.State.Stage ?? GameStage.Starting;
                if (now >= nextBeat) { nextBeat = now + 30; Info($"MP-StartGame driver alive: stage={stage} takeover={_takeover} loadIssued={loadIssued} follower={follower is not null}"); }
                if (stage != lastStage)
                {
                    if (stage == GameStage.Menu) { loadIssued = false; pushed = ""; }   // back at the menu (a failed load too): draw again
                    lastStage = stage;
                }

                while (_choices.TryDequeue(out var c))
                {
                    if (c.Seq <= lastSeq) continue;   // a line read twice
                    lastSeq = c.Seq;
                    if (!_takeover) { Info($"MP-StartGame a menu choice ({c.Kind}) was read, but this launch's menu is the game's own again: not acted on"); continue; }
                    if (c.Kind is "load" or "newadv" && stage != GameStage.Menu)
                    {
                        Info($"MP-StartGame a menu choice ({c.Kind}) read while the game is not at its menu ({stage}): not acted on");
                        continue;
                    }
                    Info($"MP-StartGame the menu chose {c.Kind} {c.Playline} {c.Name}{c.Join}");
                    switch (c.Kind)
                    {
                        case "load" when hosting && !loadIssued:
                            loadIssued = await StartGameLoadAsync(console, c.Playline, c.Name, false);
                            menuLoad |= loadIssued;
                            break;
                        case "newadv" when hosting && !loadIssued && _saves?.Staged is { } st:
                        {
                            // the chosen playstyle's start save into the staged slot, then the load
                            var sw = await AgentHelper.RunAsync<StartGameStageData>(_pack.AgentExe(_cfg),
                                $"{ModLua.HelperFlag} swap --path \"{st.Path}\" --source \"{_startRoot}\" --style {c.Style}", ModLua.HelperTag, TimeSpan.FromSeconds(30));
                            if (sw is not { Ok: true })
                            {
                                Info($"MP-StartGame the {c.Style} start save could not be put in place: {sw?.Why ?? "the helper did not answer"}");
                                await console.LuaAsync(MenuTakeoverRule.HudLine("The new adventure could not be prepared. Switch to the launcher window for details."));
                                break;
                            }
                            Info($"MP-StartGame new adventure: {c.Style}, a new world {sw.SeedTag}; prologue {(c.Watch ? "watched" : "skipped")}");
                            loadIssued = await StartGameLoadAsync(console, st.Pl, st.Name, true);
                            menuLoad |= loadIssued;
                            recapPending = loadIssued && c.Watch;
                            break;
                        }
                        case "recap":
                            Info($"MP-StartGame the prologue's cutscenes {c.Name}");
                            break;
                        case "join" when !hosting:
                            _joinChoice = c.Join == "fresh" ? "fresh:" + c.Style : "bring";
                            _menuJoin = "waiting";
                            break;
                        case "cancel" when !hosting && _menuJoin == "waiting":
                            _joinChoice = "";
                            _menuJoin = "idle";
                            break;
                    }
                }

                // "Watch the prologue": its rendered cutscenes start once the New adventure's world is there
                if (recapPending && !recapSent && stage == GameStage.World && follower is not null && now - follower.State.WorldSinceS >= 2)
                {
                    recapSent = await console.LuaAsync(MenuTakeoverRule.RecapCall());
                    Info($"MP-StartGame the prologue's cutscenes {(recapSent ? "started" : "NOT started")} ({MenuTakeoverRule.RecapMinutes} min, skipping one skips them all)");
                }

                // the host: CONNECT once the world Start Game loaded has settled (no Alt+Tab)
                if (_takeover && !hostConnectTried && MenuTakeoverRule.HostAutoConnect(hosting, menuLoad, stage, follower?.State.WorldSinceS ?? double.NaN, now,
                        _stage == LaunchStage.WaitingForConnect, ConnectEnabled))
                {
                    hostConnectTried = true;
                    Info("MP-StartGame the world Start Game loaded has settled: CONNECT for the host");
                    await ConnectAsync();
                    if (_stage != LaunchStage.Connected) await console.LuaAsync(MenuTakeoverRule.HudLine(MenuTakeoverRule.ConnectFailedText));
                }

                // the joiner: CONNECT as soon as the host is ready (the gate), then the first-join answer
                if (!hosting && _takeover && _menuJoin == "waiting" && _stage == LaunchStage.WaitingForConnect && ConnectEnabled)
                {
                    _menuJoin = "joining";
                    Info($"MP-StartGame the host is ready: CONNECT for the menu's Join ({_joinChoice})");
                    await ConnectAsync();
                    if (_stage != LaunchStage.Connected) await console.LuaAsync(MenuTakeoverRule.HudLine(MenuTakeoverRule.ConnectFailedText));
                    connectedAt = DateTime.UtcNow;
                }
                if (!hosting && _menuJoin == "joining" && !choicePosted && _stage == LaunchStage.Connected && _joinChoice.Length > 0)
                {
                    if (await NetClient.PostJoinChoiceAsync(_cfg.VersionPort, _joinChoice))
                    {
                        choicePosted = true;
                        Info($"MP-StartGame first-join answer sent to the agent: {_joinChoice}");
                    }
                    else if ((DateTime.UtcNow - connectedAt).TotalSeconds > 90)
                    {
                        choicePosted = true;
                        Info("MP-StartGame the agent did not take the first-join answer in 90 s; the launcher's own buttons ask again");
                    }
                }
                if (!hosting && _menuJoin == "joining" && _stage is LaunchStage.Idle or LaunchStage.Failed)
                    _menuJoin = "idle";   // the connect did not happen (refused by Windows, reset): the choice page again

                if (_takeover && stage == GameStage.Menu && !loadIssued)
                {
                    string model = StartGameModel(hosting);
                    if (model != pushed && await console.LuaAsync(model)) pushed = model;
                    if (await console.LuaAsync(MenuTakeoverRule.TickCall) && !armedLogged)
                    {
                        armedLogged = true;
                        Info("MP-StartGame menu armed: Start Game / Join Game");
                    }
                }
                await Task.Delay(TickMs);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Info($"MP-StartGame menu driver stopped: {ex.GetType().Name}: {ex.Message}"); }
        try { await game.WaitForExitAsync(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        Info("MP-StartGame the game exited");
        await RemoveLogoAsync();
        if (await AgentHelper.RunAsync<StartGameUnstageData>(_pack.AgentExe(_cfg), $"{ModLua.HelperFlag} unstage", ModLua.HelperTag, TimeSpan.FromSeconds(20)) is { } u && u.Lines.Count > 0)
            Info($"MP-StartGame start saves after the game: {string.Join("; ", u.Lines)}");
    }

    // ------------------------------------------------------------ our logo on the main menu (session-started games only)
    //
    // The game draws its menu logo from Libs/UI/Textures/KCDLogo.dds. The mod's brand pak (part of the pack) carries ours under that name. It is put into the mod's Data
    // folder only for a game this session starts and taken out when that game exits, so a game started any other way shows the game's own logo.

    private string BrandPakName => Path.GetFileName(_pack.BrandPak) is { Length: > 0 } n ? n : "brand.pak";

    private string? BrandTarget()
    {
        if (string.IsNullOrEmpty(_cfg.GameExe)) return null;
        string root = GameLocator.InstallRootOf(_cfg.GameExe) ?? GameRootOf(_cfg.GameExe);
        return Path.Combine(ModInstall.TargetDir(root, _pack.Root), "Data", BrandPakName);
    }

    private void PlaceLogo()
    {
        string src = _pack.BrandPak;
        if (src.Length == 0 || !File.Exists(src) || BrandTarget() is not string dst) { Info("MP-StartGame no menu logo pak in the pack: the game's own logo"); return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            Info($"MP-StartGame our logo placed for this game ({BrandPakName})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Info($"MP-StartGame our logo could not be placed: {ex.GetType().Name}"); }
    }

    private async Task RemoveLogoAsync()
    {
        if (BrandTarget() is not string dst || !File.Exists(dst)) return;
        // the game can hold the pak for a moment after its process ended
        for (int attempt = 1; ; attempt++)
        {
            try { File.Delete(dst); Info("MP-StartGame our logo taken out again (the game's own for a normal start)"); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 15) { Info($"MP-StartGame our logo could not be taken out: {ex.GetType().Name}"); return; }
                await Task.Delay(1000);
            }
        }
    }

    /// <summary>The menu's load: the menu's video stopped first (the freeze), then the join flow load.</summary>
    private async Task<bool> StartGameLoadAsync(GameConsole console, int pl, string name, bool newAdventure)
    {
        if (!MenuTakeoverRule.IsSaveName(name) || pl is < 0 or > 4) return false;
        await console.LuaAsync(ModLua.Call(ModLua.StartGameDisarm, "\"load\""));
        await console.CommandAsync("wh_ui_StopMovie");
        await Task.Delay(StopVideoSettleMs);
        bool ok = await console.LuaAsync(MenuTakeoverRule.LoadCall(pl, name));
        Info($"MP-StartGame {(newAdventure ? "new adventure" : "load")}: wh_sys_LoadGame {pl} {name} -> {(ok ? "sent" : "NOT sent")}");
        if (ok && newAdventure && _saves?.Staged is { } st)
        {
            // a normal save from now on: never taken back, even before its first save
            await AgentHelper.RunAsync<StartGameStageData>(_pack.AgentExe(_cfg), $"{ModLua.HelperFlag} used --path \"{st.Path}\"", ModLua.HelperTag, TimeSpan.FromSeconds(20));
        }
        return ok;
    }
}
