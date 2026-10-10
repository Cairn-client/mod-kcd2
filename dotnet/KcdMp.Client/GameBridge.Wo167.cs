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
//   WO167-SCENE stuck|ended|not-ended|faders ...   MP-WO167-STATS ...
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
                }
                return;
            case "w167_clear":   // <npc>: T2 -- the host used this copy and it showed a combat state here: the WO-132 engagement lets go
                if (f.Length >= 1 && Wo137Text.IsNpc(f[0])) _ = Wo167ClearAsync(f[0]);
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

    private string Wo167StatsText() =>
        $"scene_release={(_w167SceneRelease ? "on" : "off")} talk_fallback={(_w167TalkFallback ? "on" : "off")} scene_releases={Interlocked.Read(ref _w167SceneReleases)} ended={Interlocked.Read(ref _w167SceneEnded)} not_ended={Interlocked.Read(ref _w167SceneNotEnded)} fader_restores={Interlocked.Read(ref _w167FaderRestores)} bumps={Interlocked.Read(ref _w167Bumps)} clears={Interlocked.Read(ref _w167Clears)} bt_lines={Interlocked.Read(ref _w167BtLines)}";
}
