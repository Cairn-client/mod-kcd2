// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KCDMP_launcher.Models;
using KcdMp.Wire;
using Serilog;

namespace KCDMP_launcher.Pages
{
    /// <summary>
    /// WO-175 S1: a co-op session never writes into the player's own single-player saves (mp_coop_playline, default
    /// ON). The agent's helper (<c>--w175coop</c>, KcdMp.Client/Wo175Coop.cs) does the files; this side calls it at the
    /// three moments that matter: before the game starts (the host's worlds copied into their co-op playlines -- the
    /// engine lists saves once, at its start), at the menu's pick (the copy committed, the rest taken back; refused in
    /// plain words when no playline is free) and when the game exits (copies never picked taken back).
    /// The switch lives in mod-settings.json beside the agent; while the game sits at its main menu this side
    /// remembers the console command's change itself (a host has no agent there to do it).
    /// </summary>
    public partial class Home
    {
        private const string W175Tag = "W175C";
        private readonly System.Collections.Concurrent.ConcurrentQueue<(long Seq, bool On)> w175Switches = new();   // mp_coop_playline read from kcd.log

        public sealed class W175PlanItem { public int Pl { get; set; } public string Name { get; set; } = ""; public string Kind { get; set; } = ""; public string To { get; set; } = ""; }
        public sealed class W175PrepareData { public bool Ok { get; set; } public bool On { get; set; } public string Why { get; set; } = ""; public List<W175PlanItem> Plan { get; set; } = new(); public List<string> Lines { get; set; } = new(); }
        public sealed class W175CommitData { public bool Ok { get; set; } public string Action { get; set; } = ""; public int Pl { get; set; } public string Name { get; set; } = ""; public string Why { get; set; } = ""; public string Line { get; set; } = ""; }
        public sealed class W175DropData { public bool Ok { get; set; } public int Removed { get; set; } public int Kept { get; set; } public List<string> Lines { get; set; } = new(); }

        /// <summary>Before the game starts, after the start save is staged and before the worlds are listed.</summary>
        private async Task Wo175CoopBeforeLaunchAsync(bool hosting)
        {
            string agent = ResolveAgainstLauncher(settings.AgentPath);
            if (!hosting) { await Wo175CoopDropAsync("before the game (joining)"); return; }
            var p = await AgentHelper.RunAsync<W175PrepareData>(agent, "--w175coop prepare", W175Tag, TimeSpan.FromSeconds(120));
            if (p is null) { Log.Warning("WO175-COOP the helper did not answer: no co-op copies this launch (a world's pick is refused; your own saves are not touched)"); return; }
            foreach (var l in p.Lines) Log.Information("WO175-COOP {Line}", l);
            if (!p.On) { Log.Information("WO175-COOP off -- plays in the original (mp_coop_playline off)"); return; }
            Log.Information("WO175-COOP prepared: {Plan}", p.Plan.Count == 0 ? "no world to host"
                : string.Join("; ", p.Plan.Select(x => $"playline{x.Pl}/{x.Name} -> {x.Kind}{(x.To.Length > 0 ? " " + x.To : "")}")));
        }

        /// <summary>The menu picked a world to host: the co-op copy committed and loaded, the co-op save continued, the
        /// original (switch off), or refused in plain words (nothing loaded, the menu stays).</summary>
        private async Task<bool> Wo175HostLoadAsync(GameConsole console, int pl, string name)
        {
            if (!MenuTakeoverRule.IsSaveName(name) || pl is < 0 or > 4) return false;
            string agent = ResolveAgainstLauncher(settings.AgentPath);
            var r = await AgentHelper.RunAsync<W175CommitData>(agent, $"--w175coop commit --pl {pl} --name {name}", W175Tag, TimeSpan.FromSeconds(120));
            if (r is null)
            {
                Log.Warning("WO175-COOP refused reason=helper-silent (playline{Pl}/{Name}: the helper did not answer; not loaded)", pl, name);
                await console.LuaAsync(MenuTakeoverRule.HudLine("Your world could not be prepared for co-op. Switch to the launcher window for details."));
                return false;
            }
            Log.Information("{Line}", r.Line);
            switch (r.Action)
            {
                case "original":
                    return await Wo159LoadAsync(console, pl, name, false);
                case "continue":
                case "copy" when r.Ok && r.Pl is >= 0 and <= 4:
                    return await Wo159LoadAsync(console, r.Pl, r.Name, false);
            }
            string why = r.Why.Length > 0 ? r.Why : "Your world could not be prepared for co-op.";
            await console.LuaAsync(MenuTakeoverRule.HudLine(why));
            UiService.ShowError(why);
            return false;
        }

        /// <summary>The co-op copies not picked, taken back (still as written only): the New adventure's pick, the game's exit.</summary>
        private async Task Wo175CoopDropAsync(string why)
        {
            var d = await AgentHelper.RunAsync<W175DropData>(ResolveAgainstLauncher(settings.AgentPath), "--w175coop drop", W175Tag, TimeSpan.FromSeconds(30));
            if (d is { } && d.Lines.Count > 0) Log.Information("WO175-COOP copies not picked ({Why}): {Lines}", why, string.Join("; ", d.Lines));
        }

        /// <summary>mp_coop_playline typed while the game sits at its main menu: remembered in mod-settings.json beside the
        /// agent (in a world the agent does it), and said on the game's screen.</summary>
        private async Task Wo175CoopSwitchAsync(GameConsole console, bool on)
        {
            string file = Path.Combine(Path.GetDirectoryName(ResolveAgainstLauncher(settings.AgentPath)) ?? "", "mod-settings.json");
            var r = await Task.Run(() => SettingsJson.Update(file, new[] { SettingsJson.Edit.Set("CoopPlayline", JsonValue.Create(on)) }, createIfMissing: true));
            bool ok = r.Outcome != SettingsJson.Outcome.Refused;
            Log.Information("WO175-COOP switch mp_coop_playline={On} -> mod-settings.json {Outcome}", on ? "on" : "off", r.Outcome.ToString().ToLowerInvariant());
            await console.LuaAsync(MenuTakeoverRule.HudLine(!ok ? "The co-op save slot setting could not be saved. Switch to the launcher window for details."
                : on ? "Co-op plays in a copy of your world (mp_coop_playline on)."
                : "Co-op plays in your original world (mp_coop_playline off)."));
        }
    }
}
