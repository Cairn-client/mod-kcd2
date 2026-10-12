// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Text.Json;
using KcdMp.CairnBridge;

namespace KcdMp.CairnBridge.Tests;

public class TranslatorTests
{
    private const string P = "[KCD2-MP-EVT] v1 ";

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void AMenuStateLineBecomesABridgeMenu()
    {
        string line = "12:00:01 " + P + "7 menu_state Kingdom Come~Mod menu~~Host: Ann"
                    + "|friendly_fire~Rules~onoff~1~0~~Friendly fire~Players can hurt each other~f3"
                    + "|ping~Session~info~-~0~23 ms~Ping~~"
                    + "|leave~Session~action~-~1~~Leave~~";
        var m = Parse(Translator.ToBridge(line, P)!);
        Assert.Equal("menu", m.GetProperty("t").GetString());
        Assert.Equal("Mod menu", m.GetProperty("title").GetString());
        Assert.Equal("Host: Ann", m.GetProperty("note").GetString());
        var items = m.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.True(items[0].GetProperty("on").GetBoolean());
        Assert.Equal("f3", items[0].GetProperty("gate").GetString());
        Assert.False(items[1].TryGetProperty("on", out _));   // "-" is no switch at all
        Assert.Equal("23 ms", items[1].GetProperty("value").GetString());
        Assert.True(items[2].GetProperty("locked").GetBoolean());
    }

    [Fact]
    public void ADamagedMenuItemIsLeftOutNotTheWholeMenu()
    {
        var m = Parse(Translator.ToBridge(P + "1 menu_state G~T~~N|too~few|ok~g~action~-~0~~Fine~help", P)!);
        Assert.Equal(new[] { "ok" }, m.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
    }

    [Fact]
    public void NoticesAndSubtitlesAreTranslated()
    {
        Assert.Equal("""{"t":"notice","text":"Ann joined^^Welcome"}""", Translator.ToBridge(P + "3 notice Ann joined^^Welcome", P));
        Assert.Equal("""{"t":"notice_hide"}""", Translator.ToBridge(P + "4 notice_hide", P));
        Assert.Equal("""{"t":"subtitle","seconds":4,"text":"Hello there"}""", Translator.ToBridge(P + "5 subtitle 4~Hello there", P));
        Assert.Null(Translator.ToBridge(P + "6 notice ", P));
        Assert.Null(Translator.ToBridge(P + "7 subtitle x~no seconds", P));
    }

    [Theory]
    [InlineData("just a line of the game's log")]
    [InlineData("[KCD2-MP-EVT] v1 notanumber menu_state G~T~~N")]
    [InlineData("[KCD2-MP-EVT] v1 9 something_else here")]
    [InlineData("[KCD2-MP-EVT] v1 9 menu_state too~short")]
    public void WhatIsNotAMenuEventIsIgnored(string line) => Assert.Null(Translator.ToBridge(line, P));

    [Fact]
    public void ThePacksOwnPrefixIsUsed() =>
        Assert.NotNull(Translator.ToBridge("[OTHER] v2 1 notice hi", "[OTHER] v2 "));

    [Fact]
    public void TheLaunchersRequestsBecomeGuardedMenuCalls()
    {
        const string F = "KCD2MP_MenuRequest";
        Assert.Equal("if KCD2MP_MenuRequest then KCD2MP_MenuRequest(\"attach\") end", Translator.ToLua(F, Parse("""{"t":"attach"}""")));
        Assert.Equal("if KCD2MP_MenuRequest then KCD2MP_MenuRequest(\"state\") end", Translator.ToLua(F, Parse("""{"t":"state"}""")));
        Assert.Equal("if KCD2MP_MenuRequest then KCD2MP_MenuRequest(\"do friendly_fire nudge\") end", Translator.ToLua(F, Parse("""{"t":"do","id":"friendly_fire","verb":"nudge"}""")));
    }

    [Theory]
    [InlineData("""{"t":"do","id":"x\"); os.exit(); (\"","verb":"enter"}""")]   // an id that could become code
    [InlineData("""{"t":"do","id":"ok","verb":"delete"}""")]
    [InlineData("""{"t":"do","verb":"enter"}""")]
    [InlineData("""{"t":"welcome","protocol":1}""")]
    [InlineData("""{"t":"somethingnew"}""")]
    [InlineData("""{"id":"ok"}""")]
    public void NothingButAKnownRequestWithAPlainIdReachesTheConsole(string json) => Assert.Null(Translator.ToLua("KCD2MP_MenuRequest", Parse(json)));
}

public class LogTailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cairn-logtail-" + Guid.NewGuid().ToString("N")[..8]);
    public LogTailTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Header(DateTime t) => $"Log Started at {t:yyyy-MM-dd HH:mm:ss} (local)\n";

    private List<string> Read(LogTail tail) { var l = new List<string>(); tail.Poll(l.Add); return l; }

    [Fact]
    public void TheLogIsFoundInTheGameFolderOrAboveIt()
    {
        var deep = Path.Combine(_dir, "bin", "Win64");
        Directory.CreateDirectory(deep);
        Assert.Null(LogTail.FindLog(deep));
        File.WriteAllText(Path.Combine(_dir, "kcd.log"), "x");
        Assert.Equal(Path.Combine(_dir, "kcd.log"), LogTail.FindLog(deep));
    }

    [Fact]
    public void ThePreviousGamesLogIsNotReplayed()
    {
        var log = Path.Combine(_dir, "kcd.log");
        File.WriteAllText(log, Header(DateTime.Now.AddHours(-2)) + "[KCD2-MP-EVT] v1 1 notice old game\n");
        var tail = new LogTail(_dir, DateTime.Now);
        Assert.Empty(Read(tail));
    }

    [Fact]
    public void ThisGamesLogIsReadLineByLineAndOnlyWhatIsNew()
    {
        var log = Path.Combine(_dir, "kcd.log");
        File.WriteAllText(log, Header(DateTime.Now) + "first\nsecond\n");
        var tail = new LogTail(_dir, DateTime.Now.AddSeconds(-5));
        Assert.Equal(new[] { Header(DateTime.Now).TrimEnd(), "first", "second" }.Skip(1), Read(tail).Skip(1));
        Assert.Empty(Read(tail));
        File.AppendAllText(log, "third\n");
        Assert.Equal(new[] { "third" }, Read(tail));
    }

    [Fact]
    public void AnUnfinishedLastLineIsReadOnceAndNotAgain()
    {
        var log = Path.Combine(_dir, "kcd.log");
        File.WriteAllText(log, Header(DateTime.Now) + "done\ncut off");
        var tail = new LogTail(_dir, DateTime.Now.AddSeconds(-5));
        Assert.Contains("cut off", Read(tail));
        Assert.Empty(Read(tail));
        File.AppendAllText(log, " and finished\n");
        Assert.Equal(new[] { "cut off and finished" }, Read(tail));
    }

    [Fact]
    public void ANewLogFileStartsOver()
    {
        var log = Path.Combine(_dir, "kcd.log");
        File.WriteAllText(log, Header(DateTime.Now) + new string('x', 200) + "\n");
        var tail = new LogTail(_dir, DateTime.Now.AddSeconds(-5));
        Read(tail);
        File.WriteAllText(log, Header(DateTime.Now) + "fresh\n");   // smaller: the engine started a new log
        Assert.Contains("fresh", Read(tail));
    }
}
