// Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.Client;

public partial class GameBridge
{
    /// <summary>The longest the joiner's input stays held for one host cutscene, whatever the host's end edge does.</summary>
    private const int CairnShareCutsceneMaxMs = 240_000;
    private int _cairnShareGen;

    /// <summary>
    /// Cairn: the host's cutscene on the joiner. The quest graph runs only on the host, so the joiner used to stand in the world
    /// and walk about while the host watched. On the host's Ingame/Rendered edge the joiner's Lua (KCD2MP_CairnCutsceneShare) holds the
    /// input and tries to play the scene; the end edge, the partner leaving, or <see cref="CairnShareCutsceneMaxMs"/> releases it.
    /// A host (or a machine not in the host's world) does nothing here. Fader, Text and SkipTime edges are too short to share.
    /// </summary>
    private void CairnShareHostCutscene(bool active, string type, string name)
    {
        if (type != "Ingame" && type != "Rendered") return;   // a fade ending inside a scene is not the scene's end
        if (!active)
        {
            Interlocked.Increment(ref _cairnShareGen);
            _ = ExecLuaAsync("if KCD2MP_CairnCutsceneShare then KCD2MP_CairnCutsceneShare(false, \"\") end");
            return;
        }
        if (!W137Joiner) return;
        int gen = Interlocked.Increment(ref _cairnShareGen);
        _ = ExecLuaAsync($"if KCD2MP_CairnCutsceneShare then KCD2MP_CairnCutsceneShare(true, \"{EscapeLua(name)}\", \"{EscapeLua(type)}\") end");
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(CairnShareCutsceneMaxMs, _wo122Ct); }
            catch (OperationCanceledException) { return; }
            if (gen != Volatile.Read(ref _cairnShareGen)) return;   // a newer edge took over
            Console.WriteLine($"MP-CUTSCENE share for {name} ran {CairnShareCutsceneMaxMs / 1000} s with no end edge -- input given back");
            _ = ExecLuaAsync("if KCD2MP_CairnCutsceneShare then KCD2MP_CairnCutsceneShare(false, \"\") end");
        });
    }
}
