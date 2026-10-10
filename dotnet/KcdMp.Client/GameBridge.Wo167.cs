// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Globalization;
using KcdMp.Wire;

namespace KcdMp.Client;

// WO-167 (docs/WO-167-findings.md) -- 0.48.5, the agent's half. The Lua half is kdcmp.lua's WO-167 section.
//   T2 the host: a collision bark (crime_reaction_barks.kolize) on its NPC is attributed by the mod (WO167-BUMP); the joiner: a copy the
//      host just used and that showed a combat state is let go of by the WO-132 engagement (w167_clear)
//   T3 the timeout line gains the copy's NPC-state (its behaviour: the unstance the DLL reads), its minigame and the last behaviour-tree
//      path the engine printed for it
//   T4 mp_scene_release (joiner, default ON): a dialogue's interactive scene still running 20 s after the dialogue ended is released the
//      engine's own way -- the WO-151 rescue save (the engine cancels every NPC's fast-forward and the scene positioning ends); the mod
//      then brings the faders back, stands the player up and tells him
//   C1 a stand-in body (the mod's w167_standin) gets the WO-155 walk pulse from the DLL once it is bound (avatar event kind 3)
//   C2 mp_copy_weapons (joiner, default ON): the host NPC's hand rows (WO-143) name the weapon class in its hand; in a fight that class
//      (or nothing) is told to the mod, which holds it on the copy; every host attack row played on a copy also says armed or unarmed
//   C3 mp_swing_watch (joiner, default ON): a row played on a copy counts as shown only when the DLL saw its action installed; one that
//      never animated is taken out of the played rows, so the hit's verdict plays the WO-163 lunge before its damage
//   C4 mp_sync_fallback (joiner, default ON): a paired (sync) row for a stand-in or an NPC the engine said has no sync data is played as
//      the plain attack row of the same weapon; the engine's sync-guard line is counted against the rows we sent
//   WO167-SCENE stuck|ended|not-ended|faders ...   WO167-PULSE / WO167-SHOWN / WO167-SYNCFALLBACK / WO167-SYNCGUARD   MP-WO167-STATS ...
public partial class GameBridge
{
    private volatile bool _w167SceneRelease = Wo167Rules.DefaultSceneRelease;
    private volatile bool _w167TalkFallback;   // the Lua decides; mirrored for the stats line

    // T4: the interactive scene's lines since the last load
    private long _w167IsmEnq, _w167IsmEnd, _w167DlgEndMs, _w167ProgressMs, _w167ReleasedForMs, _w167FaderHeldMs;
    private int _w167ReleaseBusy;
    private long _w167SceneReleases, _w167SceneEnded, _w167SceneNotEnded, _w167FaderRestores, _w167Bumps, _w167Clears, _w167BtLines;

    // T3: the last behaviour-tree path the engine printed per NPC (and when)
    private readonly ConcurrentDictionary<string, (string Path, long AtMs)> _w167Bt = new(StringComparer.Ordinal);

    private static long W167NowMs() => Environment.TickCount64;

    // C1-C4
    private volatile bool _w167CopyWeapons = Wo167Rules.DefaultCopyWeapons, _w167SwingWatch = Wo167Rules.DefaultSwingWatch, _w167SyncFallback = Wo167Rules.DefaultSyncFallback;
    private readonly ConcurrentDictionary<string, long> _w167StandinPulse = new(StringComparer.Ordinal);   // name -> spawn ms (pulse not asked yet)
    private readonly ConcurrentDictionary<string, bool> _w167StandinSet = new(StringComparer.Ordinal);     // every stand-in this session (C4)
    private readonly ConcurrentDictionary<string, long> _w167NoSync = new(StringComparer.Ordinal);         // C4: the engine said: no sync data
    private readonly ConcurrentDictionary<string, long> _w167SyncSentAt = new(StringComparer.Ordinal);     // C4: our last sync row per NPC
    private readonly ConcurrentDictionary<string, string> _w167HostWeapon = new(StringComparer.Ordinal);   // C2: the host NPC's weapon class in hand, "-" none
    private long _w167Shown, _w167NoAnim, _w167ShownUnknown, _w167SyncFallbacks, _w167SyncNoPlain, _w167SyncGuardAfterRow, _w167SyncGuardOther,
                 _w167Pulses, _w167WeaponCalls, _w167CfgTickN;
    private bool _w167WatchFaultTold;

    // ---------------------------------------------------------------- the mod's events

    private void Wo167OnEvent(string name, string? arg)
    {
        var f = (arg ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (name)
        {
            case "w167_cfg":   // scene_release=on|off, talk_fallback=on|off
                foreach (var kv in f)
                {
                    if (kv is "scene_release=on" or "scene_release=off")
                    {
                        bool v = kv.EndsWith("=on", StringComparison.Ordinal);
                        if (v != _w167SceneRelease) Console.WriteLine($"WO167-CFG mp_scene_release {(v ? "on" : "off")}");
                        _w167SceneRelease = v;
                    }
                    else if (kv is "talk_fallback=on" or "talk_fallback=off") _w167TalkFallback = kv.EndsWith("=on", StringComparison.Ordinal);
                    else if (kv is "copy_weapons=on" or "copy_weapons=off") _w167CopyWeapons = kv.EndsWith("=on", StringComparison.Ordinal);
                    else if (kv is "swing_watch=on" or "swing_watch=off") { _w167SwingWatch = kv.EndsWith("=on", StringComparison.Ordinal); _w167CfgTickN = 9; }
                    else if (kv is "sync_fallback=on" or "sync_fallback=off") _w167SyncFallback = kv.EndsWith("=on", StringComparison.Ordinal);
                }
                return;
            case "w167_clear":   // <npc>: T2 -- the host used this copy and it showed a combat state here: the WO-132 engagement lets go
                if (f.Length >= 1 && Wo137Text.IsNpc(f[0])) _ = Wo167ClearAsync(f[0]);
                return;
            case "w167_standin":   // <npc>: C1 -- a stand-in body was spawned here (its walk pulse once bound; C4's set)
                if (f.Length >= 1 && Wo137Text.IsNpc(f[0])) { _w167StandinPulse[f[0]] = W167NowMs(); _w167StandinSet[f[0]] = true; }
                return;
        }
    }

    private async Task Wo167ClearAsync(string npc)
    {
        Interlocked.Increment(ref _w167Clears);
        bool engaged = _w132Engaged.TryRemove(npc, out var eg);
        if (engaged) { try { await _combat.Wo132EngageAsync(false, eg.Eid, default); } catch { } }
        Console.WriteLine($"WO167-T2 clear npc={npc} engagement={(engaged ? "released" : "none")} -- the host's conversation with it is over and its copy showed a combat state here");
    }

    // ---------------------------------------------------------------- the engine's lines

    /// <summary>T2 (host): the collision bark; T3: behaviour-tree paths; T4: the faders. Never consumes a line others need.</summary>
    private void Wo167OnEngineLine(string line)
    {
        if (line.IndexOf("/(BT)", StringComparison.Ordinal) >= 0 && Wo167Rules.BtPath(line) is { } bt)
        {
            _w167Bt[bt.Npc] = (bt.Path, W167NowMs());
            Interlocked.Increment(ref _w167BtLines);
            if (_w167Bt.Count > 512) foreach (var k in _w167Bt.Where(kv => W167NowMs() - kv.Value.AtMs > 600_000).Select(kv => kv.Key).ToList()) _w167Bt.TryRemove(k, out _);
            return;
        }
        if (Wo167Rules.SyncGuardNpc(line) is { } sg && Wo137Text.IsNpc(sg))   // C4: the engine has no sync data for this NPC
        {
            long now = W167NowMs();
            _w167NoSync[sg] = now;
            bool after = _w167SyncSentAt.TryGetValue(sg, out long at) && now - at <= Wo167Rules.SyncGuardAfterRowMs;
            long n = after ? Interlocked.Increment(ref _w167SyncGuardAfterRow) : Interlocked.Increment(ref _w167SyncGuardOther);
            if (n <= 20 || n % 50 == 0)
                Console.WriteLine($"WO167-SYNCGUARD npc={sg} followed_row={(after ? "yes" : "no")} -- the engine has no sync data for it: its paired rows play as plain attacks from now on [n={n}]");
            return;
        }
        if (W137Host && Wo167Rules.KolizeSoul(line) is { } npc && Wo137Text.IsNpc(npc))
        {
            Interlocked.Increment(ref _w167Bumps);
            _ = ExecLuaAsync($"if KCD2MP_W167Bump then KCD2MP_W167Bump(\"{npc}\") end");
            return;
        }
        if (Wo167Rules.FaderTook(line) is double secs) Wo167OnSceneStage("FaderDone", "", secs.ToString("F2", CultureInfo.InvariantCulture));
    }

    /// <summary>T3: the behaviour-tree path the engine last printed for <paramref name="npc"/> (within 2 minutes), or "-".</summary>
    private string Wo167BtFor(string npc) =>
        _w167Bt.TryGetValue(npc, out var b) && W167NowMs() - b.AtMs < 120_000 ? $"{b.Path}@{(W167NowMs() - b.AtMs) / 1000}s" : "-";

    /// <summary>T3: the timeout line's tail -- the copy's NPC-state (the DLL's read), its minigame, the engine's last tree path.</summary>
    private async Task<string> Wo167ReadinessTailAsync(string npc)
    {
        string behaviour = "?", mg = "?";
        try
        {
            var a = await _combat.Wo141ReadAsync(npc);
            if (a is { } s)
            {
                behaviour = s.Unstance == ActivityState.NoUnstance ? "none" : (await _combat.Wo141UnstanceNameAsync(s.Unstance) ?? $"id{s.Unstance}");
                if (s.Stance != 0) behaviour += $"+stance{s.Stance}";
                mg = s.Minigame == ActivityState.NoMinigame ? "none" : s.Minigame.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch { }
        return $"behaviour={behaviour} npc_minigame={mg} bt={Wo167BtFor(npc)}";
    }

    // ---------------------------------------------------------------- T4: the scene

    /// <summary>The log tail's scene stages (the WO-151 feed) plus the InteractiveSceneManager lines and the faders.</summary>
    private void Wo167OnSceneStage(string stage, string type, string name)
    {
        long now = W167NowMs();
        switch (stage)
        {
            case "IsmEnqueue":
                Interlocked.Increment(ref _w167IsmEnq);
                break;
            case "IsmEnd":
            case "IsmInterrupt":
                long ends = Interlocked.Increment(ref _w167IsmEnd);
                if (ends > Interlocked.Read(ref _w167IsmEnq)) Interlocked.Exchange(ref _w167IsmEnd, Interlocked.Read(ref _w167IsmEnq));   // never below zero running
                Volatile.Write(ref _w167ProgressMs, now);
                if (Interlocked.Read(ref _w167IsmEnd) >= Interlocked.Read(ref _w167IsmEnq)) Volatile.Write(ref _w167DlgEndMs, 0);   // nothing runs: done
                if (Volatile.Read(ref _w167FaderHeldMs) > 0) _ = Wo167FaderRestoreAsync("the scene ended");
                break;
            case "IsmCleanup":
            case "FaderDone":
                Volatile.Write(ref _w167ProgressMs, now);
                break;
        }
    }

    /// <summary>T4: a dialogue with this player ended / started (Wo137OnQuestLine): the scene's clock.</summary>
    private void Wo167OnDudeDialogue(bool ended)
    {
        long now = W167NowMs();
        if (ended) Volatile.Write(ref _w167DlgEndMs, now);
        else { Volatile.Write(ref _w167DlgEndMs, 0); Volatile.Write(ref _w167ProgressMs, now); }
    }

    /// <summary>A load: the engine interrupts every scene; nothing counted before it means anything after.</summary>
    private void Wo167SceneReset()
    {
        Interlocked.Exchange(ref _w167IsmEnq, 0);
        Interlocked.Exchange(ref _w167IsmEnd, 0);
        Volatile.Write(ref _w167DlgEndMs, 0);
    }

    /// <summary>Once a second (the WO-151 loop).</summary>
    private async Task Wo167TickAsync()
    {
        try { await Wo167StandInTickAsync(); await Wo167PushConfigAsync(); } catch (Exception ex) { Console.WriteLine($"WO167 tick: {ex.GetType().Name}: {ex.Message}"); }
        long now = W167NowMs();
        long held = Volatile.Read(ref _w167FaderHeldMs);
        if (held > 0 && now - held >= Wo167Rules.FaderHoldMaxMs) await Wo167FaderRestoreAsync("60 s after the black was lifted");
        if (!_w167SceneRelease || !W137Joiner) return;
        if (!_w151Scenes.IsEmpty) return;   // a cutscene runs: the WO-151 guard's own business
        long dlgEnd = Volatile.Read(ref _w167DlgEndMs);
        if (!Wo167Rules.SceneStuck(Interlocked.Read(ref _w167IsmEnq), Interlocked.Read(ref _w167IsmEnd), dlgEnd,
                                   Volatile.Read(ref _w167ProgressMs), Volatile.Read(ref _w167ReleasedForMs), now)) return;
        if (Interlocked.CompareExchange(ref _w167ReleaseBusy, 1, 0) != 0) return;
        Volatile.Write(ref _w167ReleasedForMs, dlgEnd);
        _ = Wo167ReleaseSceneAsync(dlgEnd, now);
    }

    private async Task Wo167ReleaseSceneAsync(long dlgEnd, long now)
    {
        try
        {
            Interlocked.Increment(ref _w167SceneReleases);
            long endsBefore = Interlocked.Read(ref _w167IsmEnd);
            Console.WriteLine(FormattableString.Invariant(
                $"WO167-SCENE stuck dialogue_ended_s={(now - dlgEnd) / 1000.0:F0} scenes_running={Interlocked.Read(ref _w167IsmEnq) - endsBefore} -- the conversation's scene never ended (its NPCs are the host's copies): released the engine's own way, a save request (every NPC's fast-forward is cancelled)"));
            await Wo151SceneRescueSaveAsync("a conversation's scene (WO-167 T4)");
            long until = W167NowMs() + Wo167Rules.SceneEndWaitMs;
            while (W167NowMs() < until && Interlocked.Read(ref _w167IsmEnd) <= endsBefore) await Task.Delay(250);
            bool ended = Interlocked.Read(ref _w167IsmEnd) > endsBefore;
            if (ended) Interlocked.Increment(ref _w167SceneEnded);
            else { Interlocked.Increment(ref _w167SceneNotEnded); Volatile.Write(ref _w167FaderHeldMs, W167NowMs()); }
            Console.WriteLine($"WO167-SCENE {(ended ? "ended" : "not-ended")} -- {(ended ? "the engine ended the scene; the faders back, the player stood up and told" : "the engine did not end it: the black is lifted (wh_ui_FaderSuspend 1, given back when it ends or in 60 s), the player stood up and told")}");
            await ExecLuaAsync($"if KCD2MP_W167SceneReleased then KCD2MP_W167SceneReleased({(ended ? "true" : "false")}, \"stuck-20s\") end");
        }
        catch (Exception ex) { Console.WriteLine($"WO167-SCENE release failed: {ex.GetType().Name}: {ex.Message}"); }
        finally { Volatile.Write(ref _w167ReleaseBusy, 0); }
    }

    private async Task Wo167FaderRestoreAsync(string why)
    {
        if (Interlocked.Exchange(ref _w167FaderHeldMs, 0) == 0) return;
        Interlocked.Increment(ref _w167FaderRestores);
        Console.WriteLine($"WO167-SCENE faders given back ({why})");
        await ExecLuaAsync($"if KCD2MP_W167FaderRestore then KCD2MP_W167FaderRestore(\"{EscapeLua(why)}\") end");
    }

    // ---------------------------------------------------------------- C1-C4

    /// <summary>C4: the row to play for a host attack row on <paramref name="npc"/>'s copy -- a sync row it cannot play becomes its plain attack.</summary>
    private ActionRowCatalog.Row Wo167RowFor(string npc, Guid rowGuid, ActionRowCatalog.Row row, ActionRowCatalog catalog)
    {
        if (!Wo167Rules.IsSyncFragment(row.Fragment)) return row;
        bool noSync = _w167StandinSet.ContainsKey(npc) || _w167NoSync.ContainsKey(npc);
        if (!_w167SyncFallback || !noSync) { _w167SyncSentAt[npc] = W167NowMs(); return row; }
        if (catalog.PlainForSync(row) is { } plain)
        {
            long n = Interlocked.Increment(ref _w167SyncFallbacks);
            if (n <= 30 || n % 50 == 0)
                Console.WriteLine($"WO167-SYNCFALLBACK npc={npc} row={rowGuid} plain={plain.Guid} why={(_w167StandinSet.ContainsKey(npc) ? "stand-in" : "engine-said-no-sync-data")} -- a paired attack it cannot play here is shown as its plain attack [n={n}]");
            return plain.Row;
        }
        Interlocked.Increment(ref _w167SyncNoPlain);
        _w167SyncSentAt[npc] = W167NowMs();
        return row;
    }

    /// <summary>C3: was the swing just queued on this copy animated? A row that never was is taken out of the played rows (the lunge plays).</summary>
    private async Task Wo167CheckShownAsync(string npc, uint eid, Guid rowGuid, long playedAt)
    {
        if (!_w167SwingWatch) return;
        try
        {
            await Task.Delay(Wo167Rules.ShownFirstLookMs);
            var s = await _combat.Wo167SwingSeenAsync(eid);
            if (s is { Seen: Wo167Rules.SeenPending }) { await Task.Delay(Wo167Rules.ShownSecondLookMs - Wo167Rules.ShownFirstLookMs); s = await _combat.Wo167SwingSeenAsync(eid); }
            if (s is { Seen: Wo167Rules.SeenPending }) { await Task.Delay(250); s = await _combat.Wo167SwingSeenAsync(eid); }
            byte seen = s?.Seen ?? Wo167Rules.SeenOff;
            if (seen == Wo167Rules.SeenInstalled) { Interlocked.Increment(ref _w167Shown); return; }
            if (seen != Wo167Rules.SeenNoAnim) { Interlocked.Increment(ref _w167ShownUnknown); return; }   // off / none / still pending: as 0.48.2
            long n = Interlocked.Increment(ref _w167NoAnim);
            bool taken = _w161Played.TryGetValue(npc, out var pl) && pl.Retract(playedAt);
            Wo161NoteRowRefused(npc, Wo167Rules.ReasonNoAnim);
            if (n <= 30 || n % 50 == 0)
                Console.WriteLine($"WO167-SHOWN npc={npc} row={rowGuid} shown=no reason=no-anim wait_ms={s?.WaitMs} taken_out={(taken ? "yes" : "no")} -- the copy never animated this swing: its blow comes with the lunge [n={n}]");
        }
        catch (Exception ex) { Console.WriteLine($"WO167-SHOWN check failed: {ex.GetType().Name}"); }
    }

    /// <summary>C2: the host NPC's hand row (WO-143): the weapon class in its hand; in a fight the copy is told at once.</summary>
    private async Task Wo167OnHostHandsAsync(ExtraRow r)
    {
        if (!_w167CopyWeapons || !W137Joiner) return;
        string right = ExtraRow.ClassText(r.Right);
        string w = "-";
        if (right != "-")
        {
            var idx = await SoulIndexAsync();
            if (idx is null) return;                                   // the tables are not read: nothing is told
            if (!(Guid.TryParse(right, out var g) && idx.TryGetItem(g, out var it) && Wo167Rules.IsWeaponKind(it.Kind))) return;   // a tool: WO-143's
            w = right;
        }
        string was = _w167HostWeapon.TryGetValue(r.Name, out var pw) ? pw : "";
        _w167HostWeapon[r.Name] = w;
        if (w == was) return;
        if (!(W151InFight(r.Name) || W167HostFought(r.Name))) return;   // out of a fight the stream's drawn flag decides, once
        Interlocked.Increment(ref _w167WeaponCalls);
        await ExecLuaAsync($"if KCD2MP_W167Weapon then KCD2MP_W167Weapon(\"{r.Name}\", \"{w}\", \"hands\") end");
    }

    /// <summary>C2: a host attack row played on a copy says armed or unarmed -- the copy follows (with the host's class when known).</summary>
    private void Wo167RowWeapon(string npc, ActionRowCatalog.Row row)
    {
        if (!_w167CopyWeapons) return;
        bool? armed = Wo167Rules.RowArmed(row.Tags);
        if (armed is null) return;
        string cls = armed == false ? "-" : (_w167HostWeapon.TryGetValue(npc, out var w) && w != "-" ? w : "?");
        Interlocked.Increment(ref _w167WeaponCalls);
        _ = ExecLuaAsync($"if KCD2MP_W167Weapon then KCD2MP_W167Weapon(\"{npc}\", \"{cls}\", \"row\") end");
    }

    /// <summary>C2: the host reported its NPC in combat in the last 10 s (WO-164's own record of the host's combat events).</summary>
    private bool W167HostFought(string npc) => _w164HostCombatTold.TryGetValue(npc, out long at) && Environment.TickCount64 - at < 10_000;

    /// <summary>C1, once a second: a stand-in that is bound now gets its walk pulse (asked once).</summary>
    private async Task Wo167StandInTickAsync()
    {
        if (_w167StandinPulse.IsEmpty) return;
        long now = W167NowMs();
        foreach (var (name, at) in _w167StandinPulse.ToArray())
        {
            if (now - at > Wo167Rules.StandInPulseWaitMs) { _w167StandinPulse.TryRemove(name, out _); Console.WriteLine($"WO167-PULSE npc={name} not bound within 10 s -- no pulse asked"); continue; }
            if (!_npcEntityIds.TryGetValue(name, out uint eid) || eid == 0) continue;
            _w167StandinPulse.TryRemove(name, out _);
            var r = await _combat.AvatarEventAsync(3, eid);
            Interlocked.Increment(ref _w167Pulses);
            Console.WriteLine($"WO167-PULSE npc={name} eid=0x{eid:X} asked={(r.Ok ? "ok" : r.ReasonTag)} -- a stand-in (a fresh spawn) gets the walk pulse a fresh avatar gets (no T-pose)");
        }
    }

    /// <summary>C3: the DLL's switch, every 10 s; a fault that switched the watch off is said once.</summary>
    private async Task Wo167PushConfigAsync()
    {
        if (++_w167CfgTickN % 10 != 0) return;
        var c = await _combat.Wo167ConfigAsync((byte)(_w167SwingWatch ? 1 : 0));
        if (c is { Faulted: true } && !_w167WatchFaultTold)
        {
            _w167WatchFaultTold = true;
            Console.WriteLine("WO167-SHOWN the DLL switched the swing check off for this session (a game error): every swing counts as shown, as in 0.48.2 -- told on screen once");
            await ExecLuaAsync("if KCD2MP_ShowNativeToast then KCD2MP_ShowNativeToast(\"Swing check is off this session (a game error).\") end");
        }
    }

    private string Wo167StatsText() =>
        $"scene_release={(_w167SceneRelease ? "on" : "off")} talk_fallback={(_w167TalkFallback ? "on" : "off")} scene_releases={Interlocked.Read(ref _w167SceneReleases)} ended={Interlocked.Read(ref _w167SceneEnded)} not_ended={Interlocked.Read(ref _w167SceneNotEnded)} fader_restores={Interlocked.Read(ref _w167FaderRestores)} bumps={Interlocked.Read(ref _w167Bumps)} clears={Interlocked.Read(ref _w167Clears)} bt_lines={Interlocked.Read(ref _w167BtLines)}"
        + $" | copy_weapons={(_w167CopyWeapons ? "on" : "off")} swing_watch={(_w167SwingWatch ? "on" : "off")} sync_fallback={(_w167SyncFallback ? "on" : "off")} shown={Interlocked.Read(ref _w167Shown)} no_anim={Interlocked.Read(ref _w167NoAnim)} shown_unknown={Interlocked.Read(ref _w167ShownUnknown)} sync_fallbacks={Interlocked.Read(ref _w167SyncFallbacks)} sync_no_plain={Interlocked.Read(ref _w167SyncNoPlain)} syncguard_after_row={Interlocked.Read(ref _w167SyncGuardAfterRow)} syncguard_other={Interlocked.Read(ref _w167SyncGuardOther)} pulses={Interlocked.Read(ref _w167Pulses)} weapon_calls={Interlocked.Read(ref _w167WeaponCalls)}";
}
