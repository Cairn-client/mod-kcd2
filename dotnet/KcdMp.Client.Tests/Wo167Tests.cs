// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using KcdMp.Client;
using Xunit;

namespace KcdMp.Client.Tests;

/// <summary>
/// WO-167 (docs/WO-167-findings.md): 0.48.5's agent rules -- T2 (the host's collision bark), T3 (the engine's behaviour-tree paths),
/// T4 (a conversation's interactive scene that never ends). The Lua halves are tools/Test-WO167Synthetic.lua.
/// </summary>
public class Wo167Tests
{
    // ---------------------------------------------------------------- T4: the scene

    [Fact]
    public void A_held_intermission_scene_is_released_at_20_seconds()
    {
        // the jugglers: two scenes queued, one ended, the trialog ended at t=1000 and nothing since
        Assert.False(Wo167Rules.SceneStuck(2, 1, 1000, 0, 0, 1000 + 19_999));
        Assert.True(Wo167Rules.SceneStuck(2, 1, 1000, 0, 0, 1000 + 20_000));
        Assert.True(Wo167Rules.SceneStuck(2, 1, 1000, 0, 0, 1000 + 176_000));
    }

    [Fact]
    public void A_scene_that_ends_with_its_dialogue_is_never_touched()
    {
        Assert.False(Wo167Rules.SceneStuck(1, 1, 1000, 1000, 0, 1000 + 60_000));   // ended: nothing runs
        Assert.False(Wo167Rules.SceneStuck(3, 3, 1000, 0, 0, 1000 + 60_000));
        Assert.False(Wo167Rules.SceneStuck(2, 1, 0, 0, 0, 1000 + 60_000));         // no dialogue ended (a cutscene's own scene)
    }

    [Fact]
    public void Progress_restarts_the_20_seconds_and_a_scene_is_released_once()
    {
        Assert.False(Wo167Rules.SceneStuck(2, 1, 1000, 15_000, 0, 30_000));   // a fader finished at 15 s: 15 s of quiet only
        Assert.True(Wo167Rules.SceneStuck(2, 1, 1000, 15_000, 0, 35_000));
        Assert.False(Wo167Rules.SceneStuck(2, 1, 1000, 0, 1000, 60_000));      // released already for this dialogue end
    }

    [Fact]
    public void The_scene_manager_lines_are_read()
    {
        Assert.Equal("IsmEnqueue", Wo167Rules.IsmStage("InteractiveSceneManager::EnqueueScene called"));
        Assert.Equal("IsmEnd", Wo167Rules.IsmStage("InteractiveSceneManager::EndScene called for "));
        Assert.Equal("IsmCleanup", Wo167Rules.IsmStage("InteractiveSceneManager::CleanupScene called for Fader cutscene 'dice_preMinigameFader' with holder 'x'"));
        Assert.Equal("IsmInterrupt", Wo167Rules.IsmStage("InteractiveSceneManager::Interrupt called for x"));
        Assert.Null(Wo167Rules.IsmStage("InteractiveSceneManager is running scene"));
        Assert.Null(Wo167Rules.IsmStage("Readiness observer 'InteractiveScenePlayer' with category bitmask '16273' is ready"));
        Assert.Equal(102.99, Wo167Rules.FaderTook("Last fader took 102.99s")!.Value, 3);
        Assert.Equal(0.41, Wo167Rules.FaderTook("Last fader took 0.41s")!.Value, 3);
        Assert.Null(Wo167Rules.FaderTook("Duration profile was not saved because fader took less than 2.00s"));
    }

    // ---------------------------------------------------------------- T2: the collision bark

    [Fact]
    public void The_collision_bark_names_its_npc()
    {
        const string line = "New dialogue 'Barbora.open_world.crime_reaction_barks.kolize.npc_je_s_hracem_pratelske_a_hrac_do_nej_vrazil__zena (ingame monolog - Id: 1845) (AI::DoMonologue)' is starting. Params: souls = 'Ex: tzel_woman_10 - meta override: PRATELSKE_NPC_DOSTALO_ZASAH_(KOLIZE)' forced = true, forced decision = none";
        Assert.Equal("tzel_woman_10", Wo167Rules.KolizeSoul(line));
        Assert.Null(Wo167Rules.KolizeSoul("New dialogue 'Barbora.open_world.shop.nakupovani_z_dialogu_zena (normal - Id: 166) (Actor::RequestDialog)' is starting. Params: souls = 'Ex: Dude; Ex: tzel_woman_10' forced = false"));
        Assert.Null(Wo167Rules.KolizeSoul("Attempting to start new dialogue (runtime id '1845') with souls 'Ex: tzel_woman_10 - meta override: PRATELSKE_NPC_DOSTALO_ZASAH_(KOLIZE)'"));
    }

    // ---------------------------------------------------------------- T3: the behaviour tree

    [Fact]
    public void A_behaviour_tree_path_is_read_from_the_engines_lines()
    {
        var a = Wo167Rules.BtPath("/ai//(NPC)tzel_rowdy_3/(Base-SUBB)npc_basic_scheduler/(BT)dice_interrupt_playDiceMinigame/(BT tmpl)world:so_dicetable_new.xml:dice_interrupt_playDiceMinigame/(N)Root:1 [Node]:Node status inconsistency. Can't update");
        Assert.NotNull(a);
        Assert.Equal("tzel_rowdy_3", a!.Value.Npc);
        Assert.Equal("npc_basic_scheduler/dice_interrupt_playDiceMinigame", a.Value.Path);
        var b = Wo167Rules.BtPath("/ai//(NPC)tzel_rowdy_3/NPC Context [NPCContext]:Execution of action NonAlignedAnimAction has failed! Action for request '/(NPC)tzel_rowdy_3/(Base-SUBB)npc_basic_scheduler/(BT)dice_playEndReaction/dice_interrupt_pla");
        Assert.Equal("npc_basic_scheduler/dice_playEndReaction", b!.Value.Path);
        Assert.Null(Wo167Rules.BtPath("[Error] /ai//(NPC)tzel_adam/NPC Context [NPCContext]:Couldn't find actions to get NPC into game loaded state"));
        Assert.Null(Wo167Rules.BtPath("no npc here (BT)x"));
    }
}
