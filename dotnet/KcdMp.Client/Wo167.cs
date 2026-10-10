// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;

namespace KcdMp.Client;

/// <summary>WO-167 (docs/WO-167-findings.md): the pure rules of 0.48.5's agent half. The bridge is GameBridge.Wo167.cs.</summary>
public static class Wo167Rules
{
    // ------------------------------------------------------------------ T4: a dialogue's interactive scene that never ends

    /// <summary>T4 mp_scene_release: a new mechanism ships on, with its switch (the maintainer's standing rule).</summary>
    public const bool DefaultSceneRelease = true;

    /// <summary>T4: a dialogue's scene still running this long after the dialogue ended, with no progress, is released (the WO's 20 s).</summary>
    public const long SceneStuckMs = 20_000;

    /// <summary>T4: after the release asked the engine, the scene's end is waited for this long before the black is lifted instead.</summary>
    public const long SceneEndWaitMs = 6_000;

    /// <summary>T4: a lifted black (wh_ui_FaderSuspend 1) is given back after this long even when the scene never says it ended.</summary>
    public const long FaderHoldMaxMs = 60_000;

    /// <summary>
    /// T4: is the interactive scene stuck after a dialogue? <paramref name="enqueues"/> / <paramref name="ends"/>: the engine's
    /// "InteractiveSceneManager::EnqueueScene" and "::EndScene" (or Interrupt) lines since the last load; <paramref name="dlgEndMs"/>:
    /// the last dialogue with this player that ended (0 = none since the last progress); <paramref name="progressMs"/>: the last sign of
    /// progress (a scene end, a new dialogue with this player, a fader that finished); <paramref name="releasedForMs"/>: the dialogue end
    /// a release already ran for (one release per stuck dialogue).
    /// </summary>
    public static bool SceneStuck(long enqueues, long ends, long dlgEndMs, long progressMs, long releasedForMs, long nowMs)
    {
        if (dlgEndMs <= 0 || enqueues <= ends) return false;           // nothing ended, or no scene is running
        if (releasedForMs == dlgEndMs) return false;                     // released already
        long since = Math.Max(dlgEndMs, progressMs);
        return nowMs - since >= SceneStuckMs;
    }

    /// <summary>T4: the InteractiveSceneManager lines (EnqueueScene / EndScene / CleanupScene / Interrupt) -> a short stage word, or null.</summary>
    public static string? IsmStage(ReadOnlySpan<char> line)
    {
        const string ism = "InteractiveSceneManager::";
        int a = line.IndexOf(ism.AsSpan(), StringComparison.Ordinal);
        if (a < 0) return null;
        var rest = line[(a + ism.Length)..];
        if (rest.StartsWith("EnqueueScene called".AsSpan(), StringComparison.Ordinal)) return "IsmEnqueue";
        if (rest.StartsWith("EndScene called".AsSpan(), StringComparison.Ordinal)) return "IsmEnd";
        if (rest.StartsWith("CleanupScene called".AsSpan(), StringComparison.Ordinal)) return "IsmCleanup";
        if (rest.StartsWith("Interrupt called".AsSpan(), StringComparison.Ordinal)) return "IsmInterrupt";
        return null;
    }

    /// <summary>T4: "Last fader took 102.99s" -> seconds, or null.</summary>
    public static double? FaderTook(string line)
    {
        const string p = "Last fader took ";
        if (!line.StartsWith(p, StringComparison.Ordinal)) return null;
        var rest = line[p.Length..];
        int s = rest.IndexOf('s');
        if (s <= 0) return null;
        return double.TryParse(rest[..s], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    // ------------------------------------------------------------------ T2: who bumped the NPC (the host's collision bark)

    /// <summary>T2: the game's collision bark ("crime_reaction_barks.kolize") starting on an NPC -> its soul name, or null.</summary>
    public static string? KolizeSoul(string line)
    {
        if (!line.StartsWith("New dialogue '", StringComparison.Ordinal)) return null;
        if (line.IndexOf("crime_reaction_barks.kolize", StringComparison.Ordinal) < 0) return null;
        const string souls = "souls = 'Ex: ";
        int a = line.IndexOf(souls, StringComparison.Ordinal);
        if (a < 0) return null;
        int b = a + souls.Length, e = b;
        while (e < line.Length && (char.IsAsciiLetterOrDigit(line[e]) || line[e] == '_')) e++;
        if (e == b || e - b > 63) return null;
        string n = line[b..e];
        return n == "Dude" ? null : n;
    }

    // ------------------------------------------------------------------ T3: the brain's behaviour tree, from the engine's own lines

    /// <summary>
    /// T3: an engine line naming an NPC's behaviour-tree path ("/ai//(NPC)tzel_rowdy_3/(Base-SUBB)npc_basic_scheduler/(BT)dice_...") ->
    /// (npc, "subbrain/tree"), or null. The tree is the last (BT) segment before any (BT tmpl) / (N) part.
    /// </summary>
    public static (string Npc, string Path)? BtPath(string line)
    {
        int a = line.IndexOf("(NPC)", StringComparison.Ordinal);
        if (a < 0) return null;
        int b = a + 5, e = b;
        while (e < line.Length && (char.IsAsciiLetterOrDigit(line[e]) || line[e] == '_')) e++;
        if (e == b || e >= line.Length || line[e] != '/') return null;
        string npc = line[b..e];
        string sub = Segment(line, "SUBB)", e) ?? "-", tree = "";
        int t = e;
        while ((t = line.IndexOf("/(BT)", t, StringComparison.Ordinal)) >= 0)
        {
            tree = Segment(line, "/(BT)", t) ?? tree;
            t += 5;
        }
        if (tree.Length == 0) return null;
        string path = sub + "/" + tree;
        return (npc, path.Length > 96 ? path[..96] : path);
    }

    /// <summary>The name right after <paramref name="marker"/> (from <paramref name="from"/>) up to '/', ' ' or a quote; null when absent or empty.</summary>
    private static string? Segment(string line, string marker, int from)
    {
        int m = line.IndexOf(marker, from, StringComparison.Ordinal);
        if (m < 0) return null;
        int q = m + marker.Length, r = q;
        while (r < line.Length && line[r] != '/' && line[r] != ' ' && line[r] != '\'') r++;
        return r > q ? line[q..r] : null;
    }
}
