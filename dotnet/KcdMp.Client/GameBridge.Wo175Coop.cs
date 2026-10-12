// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.Client;

// WO-175 S1 -- the co-op playline, the agent's live half (the rules and the launcher's helpers: Wo175Coop.cs).
//
//   the switch   mp_coop_playline on|off (kdcmp.lua) -> w175_coop on|off|status -> CoopPlayline in mod-settings.json
//                beside this exe (SettingsJson, key by key); the answer goes back to the mod (KCD2MP_W175CoopState).
//                The launcher reads the same file through --w175coop, and writes it itself while the game sits at
//                its main menu (a host has no agent there).
//   the joiner   the spliced world (WO-124 mpworld file) goes into the co-op playline for the HOST world's tag, never
//                into a playline holding a save of another playthrough; none free: the join stops, nothing placed.
//   WO175-COOP join to=playlineN/mpworld... seed=<tag> sha256=<12> / refused reason=no-free-playline / off -- ...
public partial class GameBridge
{
    private static string W175ModSettings => Wo175Coop.ModSettingsBesideAgent();

    /// <summary><c>w175_coop on|off</c> (the console command's change) or <c>status</c>: remembered, said back.</summary>
    private void Wo175CoopOnEvent(string? arg)
    {
        string a = (arg ?? "").Trim();
        if (Wo175Coop.TryParseSwitch(a, out bool on))
        {
            var r = Wo175Coop.SetSwitch(W175ModSettings, on);
            Console.WriteLine($"WO175-COOP switch mp_coop_playline={On(on)} -> {Wo154MenuRules.ModSettingsFile} {r.Outcome.ToString().ToLowerInvariant()}"
                              + (r.Outcome == KcdMp.Wire.SettingsJson.Outcome.Refused ? $" ({Wo154MenuRules.Scrub(r.Why, AppContext.BaseDirectory)})" : ""));
        }
        else if (a == "status") Console.WriteLine($"WO175-COOP switch mp_coop_playline={On(Wo175Coop.SwitchOn(W175ModSettings))} (asked)");
        else { Console.WriteLine($"WO175-COOP malformed w175_coop '{W154mTrunc(arg)}'"); return; }
        bool now = Wo175Coop.SwitchOn(W175ModSettings);
        _ = ExecLuaAsync($"if KCD2MP_W175CoopState then KCD2MP_W175CoopState(\"{On(now)}\") end");
    }

    /// <summary>
    /// The playline for the joiner's spliced world: -1 = the switch is off (the WO-124 rule: this player's own
    /// playline), null = refused (no free playline; said and logged), else the co-op playline.
    /// </summary>
    private int? Wo175JoinPlayline(string saves, string hostTag, uint joinId)
    {
        if (!Wo175Coop.SwitchOn(W175ModSettings))
        {
            Console.WriteLine($"WO175-COOP off -- plays in the original (join 0x{joinId:x8}: the host's world goes into this player's own playline, WO-124)");
            return -1;
        }
        if (hostTag.Length == 0)
        {
            Console.WriteLine($"WO175-COOP refused reason=no-seed (join 0x{joinId:x8}: the host's world has no playthrough seed)");
            return null;
        }
        var pick = Wo175Coop.ChooseForJoin(saves, Wo175Coop.LedgerPath(), hostTag);
        if (!pick.Ok)
        {
            Console.WriteLine($"WO175-COOP refused reason=no-free-playline (join 0x{joinId:x8}, world {hostTag})");
            return null;
        }
        Console.WriteLine($"WO175-COOP join 0x{joinId:x8}: world {hostTag} -> co-op playline{pick.Playline} ({(pick.FromLedger ? "recorded for this world" : "the lowest free")})");
        return pick.Playline;
    }

    /// <summary>The joiner's co-op playline recorded once the spliced file is placed and read back.</summary>
    private void Wo175JoinPlaced(uint joinId, string hostTag, int pl, string path, byte[] sha)
    {
        string hex = Convert.ToHexString(sha).ToLowerInvariant();
        try { Wo175Coop.Remember(Wo175Coop.LedgerPath(), hostTag, -1, pl, $"host world (join 0x{joinId:x8})", hex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine($"WO175-COOP ledger not written: {ex.GetType().Name}"); }
        Console.WriteLine($"WO175-COOP join to={Wo175Coop.Display(path)} seed={hostTag} sha256={hex[..12]}");
    }
}
