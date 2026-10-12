// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Diagnostics;
using System.Text.Json;
using KcdMp.CairnBridge.Dlss5Support;

namespace KcdMp.CairnBridge;

/// <summary>
/// The pack's options program (<c>optionsProgram</c> in pack.json, docs/CAIRN.md): the Cairn launcher runs
/// <c>CairnBridgeKcd2.exe options check dlss5 --game-dir D</c> and <c>options apply dlss5 on|off --game-dir D</c> to show and do the game's options.
/// Answers are JSON lines on standard output (the launcher's side is Cairn.Setup/GameOptions.cs). The one option today is DLSS 5, which was the
/// launcher's own code until it moved here.
/// </summary>
public static class OptionsCli
{
    public const string GameId = "kcd2";
    public const string Dlss5Id = "dlss5";

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken ct = default)
    {
        // options check <id> --game-dir <dir>   |   options apply <id> on|off --game-dir <dir>
        if (args.Length < 5 || args[0] != "options" || args[2] != Dlss5Id) { Say(output, new { ok = false, message = "Not an option of this game." }); return 2; }
        string mode = args[1];
        int dirAt = Array.IndexOf(args, "--game-dir");
        if (dirAt < 0 || dirAt + 1 >= args.Length) { Say(output, new { ok = false, message = "No game folder given." }); return 2; }
        string dir = args[dirAt + 1];
        if (!Directory.Exists(dir)) { Say(output, mode == "check" ? new { available = false, on = false, note = "The game's folder was not found." } : new { ok = false, message = "The game's folder was not found." }); return mode == "check" ? 0 : 1; }

        return mode switch
        {
            "check" => Check(dir, output),
            "apply" when args.Length >= 6 && args[3] is "on" or "off" => await ApplyAsync(dir, args[3] == "on", output, ct),
            _ => Fail(output, "Not a request this program knows."),
        };
    }

    private static int Fail(TextWriter output, string message) { Say(output, new { ok = false, message }); return 2; }

    /// <summary>What is so for DLSS 5 here: the card (the option is not available without an RTX card), and whether it is in the game's folder.</summary>
    public static int Check(string gameDir, TextWriter output)
    {
        var (cls, name) = Card();
        bool on = Dlss5Service.IsInstalled(GameId) || Dlss5.FindOptiScalerIni(gameDir) is not null;   // OptiScaler in the folder counts, whoever put it there
        Say(output, new { available = Dlss5.Supported(cls), on, note = CardText(cls, name) });
        return 0;
    }

    public static async Task<int> ApplyAsync(string gameDir, bool want, TextWriter output, CancellationToken ct)
    {
        if (Process.GetProcessesByName("KingdomCome").Length > 0) { Say(output, new { ok = false, message = "Close the game first: DLSS 5 is put in or taken out while it is not running." }); return 1; }
        var (cls, _) = Card();
        if (want && !Dlss5.Supported(cls)) { Say(output, new { ok = false, message = Dlss5.Describe(GpuClass.None) }); return 1; }

        string last = "";
        void SayLine(string s) { last = s; Say(output, new { say = s }); }
        var run = want
            ? await Dlss5Service.InstallAsync(GameId, gameDir, SayLine, ct)
            : await Dlss5Service.RemoveAsync(GameId, gameDir, SayLine, ct);
        if (run.Ok)
            Say(output, new { ok = true, message = want ? "DLSS 5 is in the game. Open its overlay from the game's menu." : "DLSS 5 is out of the game." });
        else
        {
            string tail = string.Join("\n", run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(6));
            Say(output, new { ok = false, message = tail.Length > 0 ? tail : last });
        }
        return run.Ok ? 0 : 1;
    }

    private static (GpuClass Class, string Name) Card()
    {
        var names = Dlss5Service.GpuNames();
        var cls = Dlss5.Best(names);
        return (cls, names.FirstOrDefault(n => Dlss5.ClassifyGpu(n) == cls) ?? names.FirstOrDefault() ?? "");
    }

    private static string CardText(GpuClass cls, string name) => cls == GpuClass.None
        ? Dlss5.Describe(GpuClass.None) + (name.Length > 0 ? $" This PC has: {name}." : "")
        : $"{name}. {Dlss5.Describe(cls)}";

    private static void Say(TextWriter output, object message) { output.WriteLine(JsonSerializer.Serialize(message)); output.Flush(); }
}
