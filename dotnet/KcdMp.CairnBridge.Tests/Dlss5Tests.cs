// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Text.Json;
using KcdMp.CairnBridge;
using KcdMp.CairnBridge.Dlss5Support;

namespace KcdMp.CairnBridge.Tests;

/// <summary>DLSS 5 as the pack's own code: its menu (the row, the submenu, OptiScaler.ini's values) and the options command's answers.</summary>
public class Dlss5MenuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cairn-dlss-" + Guid.NewGuid().ToString("N")[..8]);
    public Dlss5MenuTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const string ModMenu = """{"t":"menu","title":"Mod menu","blocked":"","note":"Host","items":[{"id":"friendly_fire","group":"Rules","kind":"onoff","on":true,"locked":false,"value":"","label":"Friendly fire","help":"h"}]}""";

    private string Ini(string content = "[Menu]\r\nShortcutKey=45\r\n[DlssNr]\r\nEnabled=false\r\nWorkingScale=auto\r\n")
    {
        var p = Path.Combine(_dir, "OptiScaler.ini"); File.WriteAllText(p, content); return p;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void WithoutDlssInTheGameTheRowIsThereButDisabled()
    {
        // no OptiScaler.ini in this folder (and the marker of this PC is not asked: the row's state is what it is told)
        var menu = new Dlss5Menu(_dir);
        var items = Parse(menu.OnModMenu(ModMenu)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { "friendly_fire", Dlss5.MenuItemId }, items.Select(i => i.GetProperty("id").GetString()));
        var row = items[1];
        Assert.Equal("action", row.GetProperty("kind").GetString());
        if (!menu.Active) Assert.True(row.GetProperty("disabled").GetBoolean());
    }

    [Fact]
    public void WithOptiScalerInTheGameTheRowOpensTheSubmenu()
    {
        Ini();
        var menu = new Dlss5Menu(_dir);
        Assert.True(menu.Active);
        var row = Parse(menu.OnModMenu(ModMenu)).GetProperty("items").EnumerateArray().Last();
        Assert.False(row.GetProperty("disabled").GetBoolean());
        Assert.Contains("DLSS 5 overlay", row.GetProperty("label").GetString());

        var (handled, send, askMod) = menu.OnDo(Dlss5.MenuItemId, "enter", 1);
        Assert.True(handled); Assert.False(askMod);
        var sub = Parse(send!);
        Assert.True(sub.GetProperty("back").GetBoolean());                                     // Back goes to the bridge, not out of the menu
        Assert.Contains(sub.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == "dlss5_nr_enabled");
        Assert.Contains(sub.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == "dlss5_back");
        Assert.True(menu.InSubmenu);
        Assert.Contains("ShortcutKey=-1", File.ReadAllText(Path.Combine(_dir, "OptiScaler.ini")));   // OptiScaler's own key no longer opens its own menu
    }

    [Fact]
    public void InTheSubmenuARowChangesTheIniAndTheModsMenuIsNotSent()
    {
        Ini();
        var menu = new Dlss5Menu(_dir);
        menu.OnDo(Dlss5.MenuItemId, "enter", 1);

        var (handled, send, _) = menu.OnDo("dlss5_nr_enabled", "nudge", 1);
        Assert.True(handled);
        Assert.Contains("Enabled=true", File.ReadAllText(Path.Combine(_dir, "OptiScaler.ini")));
        Assert.True(Parse(send!).GetProperty("back").GetBoolean());

        var (_, next, _) = menu.OnDo("dlss5_nr_scale", "nudge", 1);            // a choice steps to the next value...
        string after = File.ReadAllText(Path.Combine(_dir, "OptiScaler.ini"));
        Assert.DoesNotContain("WorkingScale=auto", after);
        menu.OnDo("dlss5_nr_scale", "nudge", -1);                              // ...and Left steps back
        Assert.Contains("WorkingScale=auto", File.ReadAllText(Path.Combine(_dir, "OptiScaler.ini")));

        // while it is open, the mod's own menu lines become the submenu: the player never falls out of it by a refresh
        Assert.True(Parse(menu.OnModMenu(ModMenu)).GetProperty("back").GetBoolean());
        Assert.NotNull(next);
    }

    [Fact]
    public void BackLeavesTheSubmenuAndAsksForTheModsMenu()
    {
        Ini();
        var menu = new Dlss5Menu(_dir);
        Assert.False(menu.OnBack());                                           // nothing to leave: the launcher closes the menu itself
        menu.OnDo(Dlss5.MenuItemId, "enter", 1);
        Assert.True(menu.OnBack());
        Assert.False(menu.InSubmenu);
        Assert.False(Parse(menu.OnModMenu(ModMenu)).TryGetProperty("back", out var b) && b.GetBoolean());

        menu.OnDo(Dlss5.MenuItemId, "enter", 1);
        var (handled, send, askMod) = menu.OnDo("dlss5_back", "enter", 1);      // the submenu's own Back row does the same
        Assert.True(handled); Assert.Null(send); Assert.True(askMod);
        Assert.False(menu.InSubmenu);
    }

    [Fact]
    public void ARowOfTheModIsNotDlss5sToHandle()
    {
        Ini();
        var menu = new Dlss5Menu(_dir);
        Assert.False(menu.OnDo("friendly_fire", "nudge", 1).Handled);           // it goes to the game's console as before
        menu.OnDo(Dlss5.MenuItemId, "enter", 1);
        Assert.True(menu.OnDo("friendly_fire", "nudge", 1).Handled);            // in the submenu every row is DLSS 5's: nothing leaks to the mod
    }

    [Fact]
    public void ADisabledRowDoesNotOpenAnything()
    {
        var menu = new Dlss5Menu(_dir);                                         // no OptiScaler in this folder
        if (menu.Active) return;                                                // (this PC's marker says DLSS 5 is installed: nothing to prove here)
        var (handled, send, _) = menu.OnDo(Dlss5.MenuItemId, "enter", 1);
        Assert.True(handled); Assert.Null(send); Assert.False(menu.InSubmenu);
    }
}

public class OptionsCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cairn-opt-" + Guid.NewGuid().ToString("N")[..8]);
    public OptionsCliTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static async Task<(int Code, List<JsonElement> Lines)> Run(params string[] args)
    {
        var sw = new StringWriter();
        int code = await OptionsCli.RunAsync(args, sw);
        return (code, sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList());
    }

    [Fact]
    public async Task CheckAnswersWithTheShapeTheLauncherReads()
    {
        var (code, lines) = await Run("options", "check", "dlss5", "--game-dir", _dir);
        Assert.Equal(0, code);
        var r = Assert.Single(lines);
        Assert.True(r.TryGetProperty("available", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(r.TryGetProperty("on", out _));
        Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("note").GetString()));
    }

    [Fact]
    public async Task OptiScalerInTheFolderCountsAsDlssBeingIn()
    {
        File.WriteAllText(Path.Combine(_dir, "OptiScaler.ini"), "[Menu]\r\n");
        var (_, lines) = await Run("options", "check", "dlss5", "--game-dir", _dir);
        Assert.True(lines[0].GetProperty("on").GetBoolean());
    }

    [Fact]
    public async Task AFolderThatIsNotThereIsSaidPlainly()
    {
        var (code, lines) = await Run("options", "check", "dlss5", "--game-dir", Path.Combine(_dir, "nope"));
        Assert.Equal(0, code);
        Assert.False(lines[0].GetProperty("available").GetBoolean());
        var (applyCode, apply) = await Run("options", "apply", "dlss5", "on", "--game-dir", Path.Combine(_dir, "nope"));
        Assert.Equal(1, applyCode);
        Assert.False(apply[0].GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("options", "check", "other", "--game-dir", "x")]
    [InlineData("options", "check", "dlss5")]
    [InlineData("options", "apply", "dlss5", "maybe", "--game-dir", "x")]
    [InlineData("notoptions", "check", "dlss5", "--game-dir", "x")]
    public async Task ARequestItDoesNotKnowIsRefusedWithoutDoingAnything(params string[] args)
    {
        var (code, lines) = await Run(args);
        Assert.NotEqual(0, code);
        Assert.False(lines[0].GetProperty("ok").GetBoolean());
    }
}
