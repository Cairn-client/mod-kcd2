// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Diagnostics;

namespace KcdMp.CairnBridge;

/// <summary>
/// The game's saves for the launcher's settings window, with the game closed (while it runs the bridge answers the same questions over its pipe):
/// <c>CairnBridgeKcd2.exe saves list</c> prints one line, the bridge's <c>saves</c> message, and <c>saves delete &lt;id&gt;</c> prints a <c>toast</c> line, then the new list.
/// The pack's one program is also its options program (pack.json's <c>optionsProgram</c>), which is how the launcher finds it.
/// </summary>
public static class SavesCli
{
    public static int Run(string[] args, TextWriter output, SaveCatalog? catalog = null, Func<bool>? gameRunning = null)
    {
        catalog ??= SaveCatalog.Default();
        gameRunning ??= () => Process.GetProcessesByName("KingdomCome").Length > 0;
        if (args.Length == 2 && args[0] == "saves" && args[1] == "list") { Say(output, SaveCatalog.ToJson(catalog.List())); return 0; }
        if (args.Length == 3 && args[0] == "saves" && args[1] == "delete" && args[2].Length is > 0 and <= 60)
        {
            var (ok, message) = catalog.Delete(args[2], DateTime.UtcNow, gameRunning());
            Say(output, SaveCatalog.ToastJson(message, error: !ok));
            Say(output, SaveCatalog.ToJson(catalog.List()));
            return ok ? 0 : 1;
        }
        Say(output, SaveCatalog.ToastJson("Not a request this program knows.", error: true));
        return 2;
    }

    private static void Say(TextWriter output, string line) { output.WriteLine(line); output.Flush(); }
}
