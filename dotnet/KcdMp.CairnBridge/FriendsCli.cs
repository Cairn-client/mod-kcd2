// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using KcdMp.CairnBridge.Session;

namespace KcdMp.CairnBridge;

/// <summary>
/// <c>CairnBridgeKcd2.exe friends --steam-app N [--game-exe PATH]</c>: which Steam friends are hosting, once, as the session's <c>friends</c> line, with no session running
/// (the launcher's Home page asks every minute while it is showing).
/// </summary>
public static class FriendsCli
{
    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        string? Get(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        uint app = uint.TryParse(Get("--steam-app"), out var a) && a > 0 ? a : SessionConfig.Defaults.SteamAppId;
        string exe = Get("--game-exe") is { Length: > 0 and <= 260 } g && !g.Any(char.IsControl) ? g : "";
        var host = new SessionHost(new SessionOut(output)) { Config = SessionConfig.Defaults with { SteamAppId = app, GameExe = exe } };
        await host.FriendsAsync();
        return 0;
    }
}
