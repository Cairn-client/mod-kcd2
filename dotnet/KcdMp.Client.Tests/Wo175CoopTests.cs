// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using KCDMP_launcher.Models;
using Spec = KcdMp.Client.Tests.WhsSaveTests.Spec;

namespace KcdMp.Client.Tests;

/// <summary>
/// WO-175 S1: a co-op session never writes into the player's own single-player saves (mp_coop_playline, default on) --
/// the host's world copied (bytes unchanged) into a co-op playline before the game starts and committed at the menu's
/// pick, the menu's "Together:" entry, the joiner's spliced world in a co-op playline, never an index above 4, the
/// switch. Synthetic saves in a temp folder only (WhsSaveTests' builder).
/// </summary>
public class Wo175CoopTests : IDisposable
{
    private const uint WorldA = 0xA11CE, WorldB = 0xB0B, Other = 0x0DD;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kcdmp-wo175coop-" + Guid.NewGuid().ToString("N"));
    private string Saves => Path.Combine(_root, "saves");
    private string Data => Path.Combine(_root, "data");
    private string Ledger => Wo175Coop.LedgerPath(Data);
    private string W159Ledger => Wo159.LedgerPath(Data);

    public Wo175CoopTests() => Directory.CreateDirectory(Saves);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Put(int pl, string name, uint seed, long t)
    {
        string dir = Path.Combine(Saves, $"playline{pl}");
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, name);
        File.WriteAllBytes(f, WhsSaveTests.File(new Spec { Seed = seed, SaveTime = t }));
        return f;
    }

    private static bool IsHenry(string p) => WhsSave.PlayerOf(WhsSave.Inflate(File.ReadAllBytes(p)).Raw).IsHenry;

    /// <summary>The menu's list as the launcher gets it (the prepared copies skipped, co-op playlines labelled).</summary>
    private Wo159.SavesReport Worlds() =>
        Wo175Coop.LabelTogether(Wo159.MenuSaves(Saves, _ => false, IsHenry, Wo175Coop.PendingPaths(Ledger)), Saves, Wo175Coop.ReadLedger(Ledger));

    private Wo175Coop.PrepareResult Prepare(bool on = true, string? staged = null)
    {
        Wo175Coop.Drop(Ledger);
        return Wo175Coop.Prepare(Saves, Ledger, Worlds(), on, staged);
    }

    private Dictionary<string, byte[]> Snapshot() =>
        Directory.GetFiles(Saves, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertSame(Dictionary<string, byte[]> before, params string[] paths)
    {
        foreach (var p in paths) Assert.Equal(before[p], File.ReadAllBytes(p));
    }

    private static string Tag(uint seed) => WhsSave.SeedTag(seed);

    // ------------------------------------------------------------------ the host

    [Fact]
    public void A_first_host_copies_its_world_into_the_lowest_free_playline_and_the_original_stays_byte_identical()
    {
        string a = Put(0, "autosave005.whs", WorldA, 200);
        string mine = Put(1, "quicksave003.whs", Other, 100);   // another playthrough of the player's
        var before = Snapshot();

        var p = Prepare();
        Assert.True(p.On);
        var e = Assert.Single(p.Plan, x => x.Playline == 0);
        Assert.Equal("copy", e.Kind);
        Assert.Equal(2, Wo175Coop.PlaylineOf(e.Target));   // 0 and 1 hold saves: the lowest free is 2

        var c = Wo175Coop.Commit(Saves, Ledger, 0, "autosave005", on: true, W159Ledger);
        Assert.True(c.Ok, c.Line);
        Assert.Equal(("copy", 2), (c.Action, c.Playline));
        string copy = Path.Combine(Saves, "playline2", c.Name + ".whs");
        Assert.Equal(before[a], File.ReadAllBytes(copy));   // the bytes unchanged: the same playthrough seed
        Assert.Equal(WorldA, WhsSave.ReadSeedFromFile(copy));
        Assert.Contains(GameBridge.ListOwnSaves(Saves), s => s.FullPath == copy);   // an engine-named save the game lists
        Assert.StartsWith($"WO175-COOP copy from=playline0/autosave005 to=playline2/{c.Name} seed={Tag(WorldA)} sha256=", c.Line);
        Assert.Matches("sha256=[0-9a-f]{12}$", c.Line);

        AssertSame(before, a, mine);   // the player's own saves: not a byte changed, nothing beside them
        Assert.Single(Directory.GetFiles(Path.Combine(Saves, "playline0")));
        Assert.Single(Directory.GetFiles(Path.Combine(Saves, "playline1")));
        Assert.Single(Directory.GetFiles(Path.Combine(Saves, "playline2")));
        Assert.False(Directory.Exists(Path.Combine(Saves, "playline3")));

        var rec = Assert.Single(Wo175Coop.ReadLedger(Ledger));
        Assert.Equal((Tag(WorldA), 0, 2, "playline0/autosave005"), (rec.SeedTag, rec.OriginPlayline, rec.CoopPlayline, rec.LastCopiedFrom));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before[a])).ToLowerInvariant(), rec.LastCopiedSha256);
        Assert.Empty(Wo175Coop.ReadPending(Ledger).Files);   // committed: a normal save, never taken back
    }

    [Fact]
    public void The_placeholder_takes_the_picked_worlds_bytes_and_the_copies_not_picked_are_taken_back()
    {
        string a = Put(0, "autosave005.whs", WorldA, 300);   // newest: the placeholder starts as its copy
        string b = Put(1, "permanent002.whs", WorldB, 100);
        var before = Snapshot();
        var p = Prepare();
        Assert.All(p.Plan, x => Assert.Equal("copy", x.Kind));
        Assert.Single(p.Plan.Select(x => x.Target).Distinct());   // one placeholder for both: a free playline is kept for each pick

        var c = Wo175Coop.Commit(Saves, Ledger, 1, "permanent002", true, W159Ledger);
        Assert.True(c.Ok, c.Line);
        Assert.Equal(2, c.Playline);
        Assert.Equal(before[b], File.ReadAllBytes(Path.Combine(Saves, "playline2", c.Name + ".whs")));
        AssertSame(before, a, b);
        Assert.Equal(Tag(WorldB), Assert.Single(Wo175Coop.ReadLedger(Ledger)).SeedTag);
    }

    [Fact]
    public void A_second_host_of_the_same_world_reuses_its_coop_playline_adds_a_file_and_the_menu_says_Together()
    {
        string a = Put(0, "autosave005.whs", WorldA, 200);
        Prepare();
        var c1 = Wo175Coop.Commit(Saves, Ledger, 0, "autosave005", true, W159Ledger);
        Assert.Equal(1, c1.Playline);   // the lowest free
        string first = Path.Combine(Saves, "playline1", c1.Name + ".whs");
        string played = Put(1, "autosave006.whs", WorldA, 400);   // the co-op session saved in its playline
        var before = Snapshot();

        // the menu: the original as it was, the co-op playline's newest as "Together: <the original's label>"
        var w = Worlds();
        Assert.Equal(2, w.Usable.Count);
        var together = Assert.Single(w.Usable, u => u.Playline == 1);
        Assert.Equal("autosave006", together.Name);
        Assert.Equal("Together: " + Wo159.Label(0, 200), together.Label);
        Assert.Equal(Wo159.Label(0, 200), Assert.Single(w.Usable, u => u.Playline == 0).Label);

        var p = Prepare();
        Assert.Equal("together", Assert.Single(p.Plan, x => x.Playline == 1).Kind);
        var again = Assert.Single(p.Plan, x => x.Playline == 0);
        Assert.Equal(("copy", 1), (again.Kind, Wo175Coop.PlaylineOf(again.Target)));
        Assert.Equal(1, w.Usable.Count(u => u.Playline == 1));
        Assert.Equal(2, Worlds().Usable.Count);   // the prepared copy is not a world of its own on the menu
        Assert.Equal("autosave006", Assert.Single(Worlds().Usable, u => u.Playline == 1).Name);

        var c2 = Wo175Coop.Commit(Saves, Ledger, 0, "autosave005", true, W159Ledger);
        Assert.True(c2.Ok, c2.Line);
        Assert.Equal(1, c2.Playline);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(Saves, "playline1"), "*.whs").Length);   // added, never over a save
        AssertSame(before, a, first, played);
        Assert.Equal(before[a], File.ReadAllBytes(Path.Combine(Saves, "playline1", c2.Name + ".whs")));
        Assert.False(Directory.Exists(Path.Combine(Saves, "playline2")));
        Assert.Single(Wo175Coop.ReadLedger(Ledger));
    }

    [Fact]
    public void Picking_Together_continues_the_coop_save_and_takes_the_prepared_copy_back()
    {
        string a = Put(0, "autosave005.whs", WorldA, 200);
        Prepare();
        Wo175Coop.Commit(Saves, Ledger, 0, "autosave005", true, W159Ledger);
        Put(1, "autosave006.whs", WorldA, 400);
        var p = Prepare();
        string pending = Assert.Single(Wo175Coop.ReadPending(Ledger).Files).Path;
        Assert.True(File.Exists(pending));

        var c = Wo175Coop.Commit(Saves, Ledger, 1, "autosave006", true, W159Ledger);
        Assert.Equal(("continue", 1, "autosave006"), (c.Action, c.Playline, c.Name));
        Assert.Equal("WO175-COOP continue playline1/autosave006", c.Line);
        Assert.False(File.Exists(pending));   // still as written: taken back
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void No_free_playline_is_refused_in_plain_words_and_nothing_is_written()
    {
        Put(0, "autosave005.whs", WorldA, 200);
        for (int pl = 1; pl <= 4; pl++) Put(pl, "quicksave001.whs", Other + (uint)pl, 100 + pl);
        var before = Snapshot();

        var p = Prepare();
        Assert.All(p.Plan, x => Assert.Equal("refused", x.Kind));
        var c = Wo175Coop.Commit(Saves, Ledger, 0, "autosave005", true, W159Ledger);
        Assert.False(c.Ok);
        Assert.Equal("refused", c.Action);
        Assert.Equal("WO175-COOP refused reason=no-free-playline", c.Line);
        Assert.Equal(Wo175Coop.NoFreeText, c.Why);
        Assert.Contains("needs one free save slot (playline). All five are in use.", c.Why);

        var after = Snapshot();
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));   // not one file more
        foreach (var k in before.Keys) Assert.Equal(before[k], after[k]);
        Assert.Empty(Wo175Coop.ReadLedger(Ledger));
    }

    [Fact]
    public void A_coop_playline_that_came_to_hold_another_playthrough_is_the_players_again()
    {
        Put(0, "autosave005.whs", WorldA, 200);
        Prepare();
        Wo175Coop.Commit(Saves, Ledger, 0, "autosave005", true, W159Ledger);   // co-op playline 1
        string theirs = Put(1, "quicksave009.whs", Other, 500);   // the player saved another playthrough there
        var before = Snapshot();
        Assert.Equal(-1, Wo175Coop.RecordedCoop(Saves, Wo175Coop.ReadLedger(Ledger), Tag(WorldA), 0));
        Assert.DoesNotContain(Worlds().Usable, u => u.Label.StartsWith("Together", StringComparison.Ordinal));

        var p = Prepare();
        Assert.Equal(2, Wo175Coop.PlaylineOf(Assert.Single(p.Plan, x => x.Playline == 0).Target));   // a free one instead
        var c = Wo175Coop.Commit(Saves, Ledger, 0, "autosave005", true, W159Ledger);
        Assert.Equal(2, c.Playline);
        AssertSame(before, theirs);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Saves, "playline1"), "*.whs").Length);
    }

    [Fact]
    public void With_only_the_start_saves_playline_free_the_unplayed_start_save_is_the_placeholder()
    {
        string src = Path.Combine(_root, "start-save");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "permanent002.whs"), WhsSaveTests.File(new Spec { Seed = 0x5717, Pristine = true }));
        string a = Put(0, "autosave005.whs", WorldA, 200);
        for (int pl = 1; pl <= 3; pl++) Put(pl, "quicksave001.whs", Other + (uint)pl, 100 + pl);
        var st = Wo159.Stage(Saves, src, W159Ledger);
        Assert.Equal(4, st.Playline);
        string staged = Path.Combine(Saves, "playline4", "permanent002.whs");
        var before = Snapshot();

        Wo175Coop.Drop(Ledger);
        var p = Wo175Coop.Prepare(Saves, Ledger, Wo159.MenuSaves(Saves, _ => false, IsHenry, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { staged }), true, staged);
        var e = Assert.Single(p.Plan, x => x.Playline == 0);
        Assert.Equal(("copy", staged), (e.Kind, e.Target));
        var c = Wo175Coop.Commit(Saves, Ledger, 0, "autosave005", true, W159Ledger);
        Assert.True(c.Ok, c.Line);
        Assert.Equal((4, "permanent002"), (c.Playline, c.Name));
        Assert.Equal(before[a], File.ReadAllBytes(staged));
        Assert.Empty(Wo159.ReadLedger(W159Ledger));   // a normal save now: the start save's exit check never takes it back
        Assert.Equal(0, Wo159.Unstage(W159Ledger).Removed);
        Assert.True(File.Exists(staged));
        AssertSame(before, a);
    }

    [Fact]
    public void Prepared_copies_are_taken_back_only_while_they_are_still_the_bytes_written()
    {
        Put(0, "autosave005.whs", WorldA, 300);
        Put(1, "permanent002.whs", WorldB, 100);
        Prepare();
        string spare = Assert.Single(Wo175Coop.ReadPending(Ledger).Files).Path;
        var d = Wo175Coop.Drop(Ledger);
        Assert.Equal((1, 0), (d.Removed, d.Kept));
        Assert.False(File.Exists(spare));
        Assert.False(Directory.Exists(Path.GetDirectoryName(spare)));   // the folder it made goes too

        Prepare();
        spare = Assert.Single(Wo175Coop.ReadPending(Ledger).Files).Path;
        File.WriteAllBytes(spare, WhsSaveTests.File(new Spec { Seed = WorldA, SaveTime = 999 }));   // the game saved over it
        d = Wo175Coop.Drop(Ledger);
        Assert.Equal((0, 1), (d.Removed, d.Kept));
        Assert.True(File.Exists(spare));
    }

    [Fact]
    public void Never_an_index_above_four()
    {
        Put(0, "autosave005.whs", WorldA, 200);
        for (int pl = 1; pl <= 4; pl++) Put(pl, "quicksave001.whs", Other + (uint)pl, 100 + pl);
        Directory.CreateDirectory(Path.Combine(Saves, "playline5"));   // empty: "free" if anything looked past 4
        Directory.CreateDirectory(Data);
        File.WriteAllText(Ledger, "[{\"SeedTag\":\"" + Tag(WorldA) + "\",\"OriginPlayline\":0,\"CoopPlayline\":5}," +
                                  "{\"SeedTag\":\"" + Tag(WorldB) + "\",\"OriginPlayline\":-1,\"CoopPlayline\":7}]");
        Assert.Equal(-1, Wo175Coop.RecordedCoop(Saves, Wo175Coop.ReadLedger(Ledger), Tag(WorldA), 0));
        Assert.False(Wo175Coop.IsCoopUsable(Saves, 5, Tag(WorldA)));
        Assert.False(Wo175Coop.ChooseForJoin(Saves, Ledger, Tag(WorldB)).Ok);
        Assert.Equal(-1, Wo175Coop.PlaylineOf(Path.Combine(Saves, "playline5", "x.whs")));
        Assert.All(Wo159.FreePlaylines(Saves), pl => Assert.InRange(pl, 0, 4));

        var p = Prepare();
        Assert.All(p.Plan, x => Assert.Equal("refused", x.Kind));   // every world (the other four are Henry worlds too)
        Assert.False(Wo175Coop.Commit(Saves, Ledger, 0, "autosave005", true, W159Ledger).Ok);
        Assert.Empty(Directory.GetFiles(Path.Combine(Saves, "playline5")));

        // with room: every target 0..4
        Directory.Delete(Path.Combine(Saves, "playline3"), true);
        p = Prepare();
        Assert.All(p.Plan.Where(x => x.Target.Length > 0), x => Assert.InRange(Wo175Coop.PlaylineOf(x.Target), 0, 4));
    }

    // ------------------------------------------------------------------ the switch

    [Fact]
    public void Switch_off_plays_in_the_original_and_writes_nothing()
    {
        string a = Put(0, "autosave005.whs", WorldA, 200);
        var before = Snapshot();
        var p = Prepare(on: false);
        Assert.False(p.On);
        Assert.Empty(p.Plan);
        Assert.Contains("WO175-COOP off -- plays in the original", p.Lines);
        var c = Wo175Coop.Commit(Saves, Ledger, 0, "autosave005", on: false, W159Ledger);
        Assert.Equal((true, "original", 0, "autosave005"), (c.Ok, c.Action, c.Playline, c.Name));
        Assert.Equal("WO175-COOP off -- plays in the original", c.Line);
        Assert.Equal(before.Keys.Count, Snapshot().Count);
        AssertSame(before, a);

        // turned off at the menu after the copies were prepared: the original, the copies taken back
        Prepare(on: true);
        Assert.NotEmpty(Wo175Coop.ReadPending(Ledger).Files);
        c = Wo175Coop.Commit(Saves, Ledger, 0, "autosave005", on: false, W159Ledger);
        Assert.Equal("original", c.Action);
        Assert.Equal(before.Keys.Count, Snapshot().Count);
        // turned on after a launch prepared nothing: refused, never the original
        Prepare(on: false);
        c = Wo175Coop.Commit(Saves, Ledger, 0, "autosave005", on: true, W159Ledger);
        Assert.Equal("refused", c.Action);
        Assert.StartsWith("WO175-COOP refused reason=not-prepared", c.Line);
    }

    [Fact]
    public void The_switch_is_on_unless_mod_settings_says_off()
    {
        string f = Path.Combine(_root, "mod-settings.json");
        Assert.True(Wo175Coop.SwitchOn(f));   // no file
        File.WriteAllText(f, "{ \"NameBadges\": true }");
        Assert.True(Wo175Coop.SwitchOn(f));   // no key
        File.WriteAllText(f, "{ \"CoopPlayline\": false }");
        Assert.False(Wo175Coop.SwitchOn(f));
        File.WriteAllText(f, "{ \"CoopPlayline\": \"off\" }");
        Assert.False(Wo175Coop.SwitchOn(f));
        File.WriteAllText(f, "{ \"NameBadges\": true, \"CoopPlayline\": false }");
        Wo175Coop.SetSwitch(f, true);
        Assert.True(Wo175Coop.SwitchOn(f));
        Assert.Contains("NameBadges", File.ReadAllText(f));   // the player's other keys kept
        Wo175Coop.SetSwitch(f, false);
        Assert.False(Wo175Coop.SwitchOn(f));
        Assert.True(Wo175Coop.TryParseSwitch(" ON ", out bool on) && on);
        Assert.False(Wo175Coop.TryParseSwitch("maybe", out _));
    }

    [Fact]
    public void The_launcher_reads_the_switch_from_the_mods_event_line()
    {
        Assert.Equal((12L, true), MenuTakeoverRule.ParseCoopSwitch("<12:00:01> [KCD2-MP-EVT] v1 12 w175_coop on"));
        Assert.Equal((13L, false), MenuTakeoverRule.ParseCoopSwitch("[KCD2-MP-EVT] v1 13 w175_coop off"));
        Assert.Null(MenuTakeoverRule.ParseCoopSwitch("[KCD2-MP-EVT] v1 14 w175_coop status"));
        Assert.Null(MenuTakeoverRule.ParseCoopSwitch("[KCD2-MP-EVT] v1 15 w159 load 0 autosave005"));
        Assert.Null(MenuTakeoverRule.Parse("[KCD2-MP-EVT] v1 12 w175_coop on"));   // not a menu choice
    }

    // ------------------------------------------------------------------ names

    [Fact]
    public void A_copys_name_never_meets_a_save_there_and_stays_below_the_engines_next_name()
    {
        string dir = Path.Combine(Saves, "playline3");
        Directory.CreateDirectory(dir);
        Assert.Equal("autosave041.whs", Wo175Coop.CopyName(dir, "autosave041.whs", 41));   // the original's, when free
        File.WriteAllBytes(Path.Combine(dir, "autosave041.whs"), [1]);
        Assert.Equal("autosave001.whs", Wo175Coop.CopyName(dir, "autosave041.whs", 41));
        Assert.Equal("quicksave001.whs", Wo175Coop.CopyName(dir, "quicksave050.whs", 7));   // 050 > its SaveId 7: the engine could reach it
        for (int k = 1; k <= 3; k++) File.WriteAllBytes(Path.Combine(dir, $"permanent{k:D3}.whs"), [1]);
        Assert.Equal("save001.whs", Wo175Coop.CopyName(dir, "permanent002.whs", 3));
        Assert.Equal(7, Wo175Coop.ReadSaveId(WhsSaveTests.File(new Spec())));   // the synthetic header's SaveId
    }

    // ------------------------------------------------------------------ the joiner

    [Fact]
    public void The_joiners_copy_of_the_hosts_world_goes_into_a_coop_playline_never_its_own()
    {
        Put(0, "quicksave010.whs", Other, 100);   // the joiner's own playthrough
        var pick = Wo175Coop.ChooseForJoin(Saves, Ledger, Tag(WorldA));
        Assert.Equal((true, 1, false), (pick.Ok, pick.Playline, pick.FromLedger));
        Wo175Coop.Remember(Ledger, Tag(WorldA), -1, 1, "host world", "00");
        Put(1, "mpworld12345678.whs", WorldA, 300);   // the transient file of the host's world
        Put(1, "quicksave011.whs", WorldA, 310);      // a copy of the host's world
        pick = Wo175Coop.ChooseForJoin(Saves, Ledger, Tag(WorldA));
        Assert.Equal((true, 1, true), (pick.Ok, pick.Playline, pick.FromLedger));
        // another host's world: its own co-op playline
        Assert.Equal(2, Wo175Coop.ChooseForJoin(Saves, Ledger, Tag(WorldB)).Playline);
        // the recorded playline came to hold the player's own new adventure: not used again
        Put(1, "permanent002.whs", Other + 1, 400);
        Assert.Equal(2, Wo175Coop.ChooseForJoin(Saves, Ledger, Tag(WorldA)).Playline);
        for (int pl = 2; pl <= 4; pl++) Put(pl, "quicksave001.whs", Other + 10 + (uint)pl, 100);
        Assert.False(Wo175Coop.ChooseForJoin(Saves, Ledger, Tag(WorldA)).Ok);
    }
}
