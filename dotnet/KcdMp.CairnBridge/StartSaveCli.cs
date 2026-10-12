// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Text.Json;
using KcdMp.CairnBridge.Session;

namespace KcdMp.CairnBridge;

/// <summary>
/// <c>CairnBridgeKcd2.exe startsave add --file F --start-saves DIR</c>: a joiner's new character (and a host's New adventure) begin from a start save placed in
/// <c>DIR\&lt;playstyle&gt;\</c>. They are not shipped (they are somebody's play data), so a player makes one from his own Modding Tools game: the agent's own checker reads
/// the file, the playstyle it fits is found, and the file goes where it is expected, replacing one put there before. The answer is one <c>message</c> line for the launcher to show.
/// (This was the launcher's own code.)
/// </summary>
public static class StartSaveCli
{
    private sealed class Check { public string Name { get; set; } = ""; public bool Pass { get; set; } public string Detail { get; set; } = ""; }
    private sealed class Validation { public List<Check> Checks { get; set; } = new(); }

    private static readonly string[] Must = { "reads and verifies", "Modding Tools build", "after the prologue, as Henry", "a playthrough seed" };

    public static async Task<int> RunAsync(string[] args, TextWriter output, SessionPack? pack = null)
    {
        string? Get(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        pack ??= new SessionPack();
        string? file = Get("--file"), dir = Get("--start-saves");
        if (args.Length < 2 || args[1] != "add" || file is null || dir is null || !File.Exists(file)) return Say(output, "START SAVE", "That save could not be found.", "", 2);
        string agent = pack.AgentExe(SessionConfig.Defaults);
        if (!File.Exists(agent)) return Say(output, "START SAVE", "The agent is not in the mod's folder, so the save cannot be checked.", "Reinstall the mod from the game's page.", 1);
        ModLua.Pack = pack;
        try
        {
            string? style = null;
            Validation? result = null;
            foreach (var st in new[] { "adviser", "soldier", "scout" })
            {
                var j = await AgentHelper.RunAsync<Validation>(agent, $"{ModLua.HelperFlag} validate --file \"{file}\" --style {st}", ModLua.HelperTag, TimeSpan.FromSeconds(60));
                if (j is null) continue;
                result ??= j;
                if (Passed(j, $"the {st} playstyle")) { style = st; result = j; break; }
            }
            if (result is null) return Say(output, "START SAVE", "The save could not be read.", "Pick a save of your Modding Tools game.", 1);

            // what must hold for a Henry to be taken from it; the rest only warns
            var failed = Must.Where(n => !Passed(result, n)).ToList();
            if (failed.Count > 0)
            {
                string why = string.Join("; ", result.Checks.Where(c => !c.Pass && Must.Contains(c.Name)).Select(c => c.Name + (c.Detail.Length > 0 ? " (" + c.Detail + ")" : "")));
                return Say(output, "START SAVE", "This save cannot be used: " + why + ".", "Pick a save made in the Modding Tools build, as Henry, after the prologue.", 1);
            }

            bool guessed = style is null;
            style ??= "adviser";
            string target = Path.Combine(dir, style);
            Directory.CreateDirectory(target);
            foreach (var old in Directory.GetFiles(target, "*.whs")) File.Delete(old);
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);

            var warn = result.Checks.Where(c => !c.Pass && c.Name is "no partner / mod data in the world" or "where Hans and Henry part ways").Select(c => c.Name).ToList();
            return Say(output, "START SAVE ADDED",
                $"{Path.GetFileName(file)} is now the {style} start save" + (guessed ? " (its playstyle could not be told, so it is filed as adviser)." : "."),
                warn.Count == 0 ? "You can now join with a new character or start a new adventure." : "It works, but it is not a clean start (" + string.Join(", ", warn) + ").", 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        {
            return Say(output, "START SAVE", "The start save could not be added: " + ex.Message, "", 1);
        }
    }

    private static bool Passed(Validation v, string name) => v.Checks.Any(c => c.Name == name && c.Pass);

    private static int Say(TextWriter output, string title, string text, string next, int code)
    {
        output.WriteLine(JsonSerializer.Serialize(new { t = "message", title, text, next, addressFallback = false }));
        output.Flush();
        return code;
    }
}
