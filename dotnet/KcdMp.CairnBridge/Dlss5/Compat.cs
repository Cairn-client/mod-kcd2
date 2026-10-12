// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
namespace KcdMp.CairnBridge.Dlss5Support;

// DLSS 5 used to be the launcher's own code (Cairn.Launcher/Models/Dlss5.cs, Services/Dlss5Service.cs). It lives here now, in the mod's pack, so the launcher
// carries nothing of one game. These are the few shapes it used from the launcher: the rows of a menu, and the log.

/// <summary>What an item of a menu is.</summary>
public enum MenuKind { OnOff, Choice, Key, Action, Info }

/// <summary>One row of a menu, as the launcher draws it (the same shape the bridge protocol has: docs/BRIDGE.md in the launcher repository).</summary>
public sealed record MenuItem(string Id, string Group, MenuKind Kind, bool? On, bool Locked, string Value, string Label, string Help, string Gate = "", bool Disabled = false);

/// <summary>A menu. A <c>Submenu</c> says that Back goes to the program, not out of the menu.</summary>
public sealed record MenuState(string Game, string Title, string Blocked, string Note, IReadOnlyList<MenuItem> Items, bool Submenu = false);

/// <summary>The few log calls the tool's code makes; the program prints what the launcher shows as JSON lines, and keeps the rest to itself.</summary>
internal static class Log
{
    public static void Information(string template, params object?[] args) { }
    public static void Warning(string template, params object?[] args) { }
    public static void Warning(Exception ex, string template, params object?[] args) { }
    public static void Debug(string template, params object?[] args) { }
    public static void Debug(Exception ex, string template, params object?[] args) { }
    public static void Error(Exception ex, string template, params object?[] args) { }
}
