// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Diagnostics;
using System.Text.Json;
using KcdMp.CairnBridge.Setup;

namespace KcdMp.CairnBridge;

/// <summary>
/// The game's setup, as the pack's program does it (pack.json's <c>setup</c>, docs/CAIRN.md in this repository; the launcher's side is
/// Cairn.Setup/PackSetup.cs): <c>setup check [--game-exe EXE]</c> prints one JSON line per step, then <c>{"done":true,"ready":...}</c>;
/// <c>setup do ACTION [--game-exe EXE]</c> does one thing, prints <c>{"say":...}</c> and <c>{"progress":0..1}</c> lines while it works and ends with
/// <c>{"ok":...,"message":...}</c>. The launcher draws the list and runs what a step asks for; what the steps ARE (Steam, the game, the Modding Tools,
/// the links between them, the mod in place, the runtimes, the lip-sync data) is this game's, so it is here. It was the launcher's own code.
/// </summary>
public static class SetupCli
{
    public static string StepName(StepId id) => id switch
    {
        StepId.Steam => "steam", StepId.Game => "game", StepId.DiskSpace => "disk", StepId.ModdingTools => "moddingtools", StepId.Workspace => "workspace",
        StepId.Mod => "mod", StepId.VcRuntime => "vcruntime", StepId.Facials => "facials", _ => "ready",
    };

    /// <summary>The steps that concern every PC, not this game alone (the launcher's "System setup" shows only these).</summary>
    public static bool IsSystemStep(StepId id) => id is StepId.Steam or StepId.VcRuntime;

    public static string? ActionName(StepAction a) => a switch
    {
        StepAction.StartSteam => "start-steam",
        StepAction.InstallGame => "install-game",
        StepAction.InstallModdingTools => "install-moddingtools",
        StepAction.LinkWorkspace or StepAction.AskPermission => "link-workspace",
        StepAction.PlaceMod => "place-mod",
        StepAction.InstallVcRuntime => "install-vcruntime",
        StepAction.OpenVcDownload => "open-vc-download",
        StepAction.LinkFacials => "link-facials",
        _ => null,
    };

    /// <summary>
    /// Whether the launcher should start the step's action without a click: <c>"once"</c> for the things it opens or starts a single time per session
    /// (Steam, a Steam install window, the facial links), <c>"while"</c> for the work that is simply the next thing to do (linking, placing the mod).
    /// </summary>
    public static string? AutoOf(StepView s, SetupSnapshot snap, IReadOnlyList<StepView> all)
    {
        StepView Of(StepId id) => all.First(x => x.Id == id);
        return (s.Id, s.Status, s.Action) switch
        {
            (StepId.Steam, StepStatus.NeedsYou, StepAction.StartSteam) => "once",
            (StepId.Game, StepStatus.NeedsYou, StepAction.InstallGame) when snap.SteamRunning => "once",
            (StepId.ModdingTools, StepStatus.NeedsYou, StepAction.InstallModdingTools) when snap.SteamRunning && Of(StepId.Game).Status is StepStatus.Done or StepStatus.Working => "once",
            (StepId.Workspace, StepStatus.Working, StepAction.LinkWorkspace) => "while",
            (StepId.Mod, StepStatus.Working, StepAction.PlaceMod) => "while",
            (StepId.Facials, StepStatus.NeedsYou, StepAction.LinkFacials) => "once",
            _ => null,
        };
    }

    public static string StepJson(StepView s, SetupSnapshot snap, IReadOnlyList<StepView> all)
    {
        var status = s.Status switch { StepStatus.Done => "done", StepStatus.Working => "working", StepStatus.NeedsYou => "needs-you", _ => "waiting" };
        var obj = new Dictionary<string, object?>
        {
            ["id"] = StepName(s.Id), ["title"] = s.Title, ["status"] = status, ["detail"] = Redact.Text(s.Detail),
        };
        if (ActionName(s.Action) is { } action && s.ActionLabel is { Length: > 0 } label) { obj["action"] = action; obj["actionLabel"] = label; }
        if (s.Progress is double p) obj["progress"] = Math.Round(p, 3);
        if (s.Optional) obj["optional"] = true;
        if (IsSystemStep(s.Id)) obj["system"] = true;
        if (AutoOf(s, snap, all) is { } auto) { obj["auto"] = auto; obj["action"] ??= ActionName(s.Action); }
        return JsonSerializer.Serialize(new { step = obj });
    }

    public static SetupSnapshot Take(ISetupHost host, string? mtExe, string? packRoot) => SetupProbe.Take(host, new SetupProbeOptions
    {
        ExplicitModdingToolsExe = mtExe,
        PackRoot = packRoot,
        OwnsModFolder = OwnsModFolder,
    });

    public static async Task<int> RunAsync(string[] args, TextWriter output, ISetupHost? host = null, string? packRoot = null, CancellationToken ct = default)
    {
        host ??= RealSetupHost.Instance;
        if (args.Length < 2 || args[0] != "setup") { Say(output, new { ok = false, message = "Not a request this program knows." }); return 2; }
        var opt = Options(args.Skip(2));
        if (!opt.TryGetValue("game-exe", out var mtExe)) opt.TryGetValue("mt-exe", out mtExe);   // the game's executable as the player chose it (mt-exe: the name betas before 0.2.12 sent)

        switch (args[1])
        {
            case "check":
            {
                var snap = Take(host, mtExe, packRoot);
                var steps = SetupChecklist.Evaluate(snap, SetupActivity.Idle).Where(x => x.Id != StepId.Ready).ToList();
                foreach (var s in steps) output.WriteLine(StepJson(s, snap, steps));
                bool ready = SetupChecklist.IsReady(steps);
                Say(output, new { done = true, ready, optionalNeedsYou = SetupChecklist.OptionalNeedsYou(steps) });
                // the executable the player chose is not the right build: said in the pack's words, the launcher shows it beside the setting
                if (!string.IsNullOrWhiteSpace(mtExe) && File.Exists(mtExe) && !GameLocator.IsModdingToolsBuild(mtExe))
                    Say(output, new { gameExeNote = "This does not look like the Modding Tools build. Multiplayer needs the KingdomCome.exe in the Modding Tools' Bin\\Win64ReleaseSteamLTO_DLL folder: the retail executable has no debug API and no module DLLs to hook." });
                if (snap.ModdingToolsExe is { } exe) Say(output, new { found = new { gameExe = exe, gameDir = snap.ModdingToolsRoot, moddingToolsExe = exe, gameRoot = snap.GameRoot, moddingToolsRoot = snap.ModdingToolsRoot } });
                return 0;
            }
            case "do" when args.Length >= 3:
                return await DoAsync(args[2], mtExe, output, host, packRoot, ct);
            case "link-elevated":
                return LinkElevated(opt);
            default:
                Say(output, new { ok = false, message = "Not a request this program knows." });
                return 2;
        }
    }

    private static async Task<int> DoAsync(string action, string? mtExe, TextWriter output, ISetupHost host, string? packRoot, CancellationToken ct)
    {
        var snap = Take(host, mtExe, packRoot);
        bool ok = false; string message = "Not a request this program knows."; int pending = 0;
        switch (action)
        {
            case "start-steam":
            {
                var exe = snap.Steam.Root is null ? null : Path.Combine(snap.Steam.Root, "steam.exe");
                if (exe is null || !File.Exists(exe)) { message = "Steam was not found on this PC."; break; }
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = exe, Arguments = "-silent", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized })?.Dispose();
                    ok = true; message = "Starting Steam in the background..."; pending = 90;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { message = "Steam could not be started."; }
                break;
            }
            case "install-game":
            case "install-moddingtools":
            {
                uint app = action == "install-game" ? SteamApp.Game : SteamApp.ModdingTools;
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = $"steam://install/{app}", UseShellExecute = true })?.Dispose();
                    ok = true; message = "Steam's install window is open: click Install there. This list updates by itself.";
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { message = "Steam's install window could not be opened."; }
                break;
            }
            case "link-workspace":
                (ok, message) = await LinkWorkspaceAsync(snap, output, ct);
                break;
            case "place-mod":
                (ok, message) = await PlaceModAsync(snap, packRoot, output);
                break;
            case "unplace-mod":
                (ok, message) = snap.ModdingToolsRoot is not string mtRoot ? (true, "nothing to remove")
                    : snap.GameProcessRunning ? (false, "Close the game first.")
                    : ModInstall.Unplace(mtRoot, packRoot) is var u ? (u.Ok, u.Ok ? u.Message : "The mod could not be taken out of the game (" + u.Message + ").") : (false, "");
                break;
            case "install-vcruntime":
                (ok, message) = await InstallRuntimesAsync(snap, output, ct);
                break;
            case "open-vc-download":
                try { Process.Start(new ProcessStartInfo { FileName = VcRuntime2013.DownloadPage, UseShellExecute = true })?.Dispose(); ok = true; message = "Microsoft's page is open."; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { message = "The page could not be opened."; }
                break;
            case "link-facials":
                (ok, message) = LinkFacials(snap, output);
                break;
        }
        Say(output, pending > 0 ? new { ok, message, pending } : (object)new { ok, message });
        return ok ? 0 : 1;
    }

    // ---- the actions ------------------------------------------------------------------------------------------------------------------

    private static async Task<(bool, string)> LinkWorkspaceAsync(SetupSnapshot snap, TextWriter output, CancellationToken ct)
    {
        if (snap.GameRoot is not string game || snap.ModdingToolsRoot is not string mt) return (false, "The game and the Modding Tools have to be installed first.");
        if (snap.GameProcessRunning) return (false, "Close the game first: its files can't be linked while it is running.");
        string status = Path.Combine(Path.GetTempPath(), "cairn-kcd2-link-status.txt");
        var options = new WorkspaceFlowOptions
        {
            Log = line => Say(output, new { say = Redact.Text(line) }),
            Progress = new Progress<LinkProgress>(p => Say(output, new { progress = p.Total == 0 ? 1.0 : Math.Round((double)p.Done / p.Total, 3), say = p.Total == 0 ? null : $"Linking {p.Done} of {p.Total} game files..." })),
            RunElevated = c => ElevatedLinker.RunAsync(Environment.ProcessPath!, game, mt, status, c),
            BeforeElevation = () => Say(output, new { say = "Windows needs your permission to link the game's files into the Modding Tools. Choose Yes in the Windows prompt." }),
        };
        WorkspaceFlowOutcome outcome;
        try { outcome = await Task.Run(() => WorkspaceSetupFlow.RunAsync(game, mt, options), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, "The game's files couldn't be linked. Click Try again; if it keeps failing, send the logs with Report a bug.");
        }
        try { if (File.Exists(status)) File.Delete(status); } catch (IOException) { }
        return (outcome.Result == WorkspaceFlowResult.Linked, outcome.Sentence);
    }

    private static async Task<(bool, string)> PlaceModAsync(SetupSnapshot snap, string? packRoot, TextWriter output)
    {
        if (snap.ModdingToolsRoot is not string mt || snap.Mod is not { } mod) return (false, "The Modding Tools have to be installed first.");
        if (mod.ForeignFolder) return (false, "A different mod is in the Modding Tools' Mods folder where this one goes. Remove it first.");
        if (!mod.StagedAvailable) return (false, "The mod's files are damaged or missing. Reinstall the mod from the game's page.");
        if (snap.GameProcessRunning) return (false, "Close the game first, then click Try again.");
        Say(output, new { say = "Placing the mod into the Modding Tools..." });
        var r = await Task.Run(() => ModInstall.Place(mt, packRoot));
        if (!r.Ok) return (false, $"The mod couldn't be placed ({r.Message}). Click Try again.");
        RecordModsPath(ModInstall.TargetDir(mt, packRoot), snap.ModdingToolsExe);
        await RefreshKeysPakAsync(packRoot ?? ModInstall.DefaultPackRoot, mt);
        return (true, "The mod is in place.");
    }

    /// <summary>The agent builds the dice keys from the player's own game files (best effort: a failure costs the keys only).</summary>
    private static async Task RefreshKeysPakAsync(string packRoot, string moddingToolsRoot)
    {
        string agent = Path.Combine(packRoot, "files", "bin", "KcdMpClient.exe");
        string modDir = ModInstall.TargetDir(moddingToolsRoot, packRoot);
        if (!File.Exists(agent) || !Directory.Exists(modDir)) return;
        try
        {
            var psi = new ProcessStartInfo(agent, $"--keys-pak --game-root \"{moddingToolsRoot}\" --mod-dir \"{modDir}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(agent)! };
            using var p = Process.Start(psi);
            if (p is null) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _ = p.StandardOutput.ReadToEndAsync(cts.Token);
            try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
    }

    private static async Task<(bool, string)> InstallRuntimesAsync(SetupSnapshot snap, TextWriter output, CancellationToken ct)
    {
        var todo = new List<(string Name, string Exe, string Args)>();
        if (!snap.VcRuntime2010 && snap.VcRedist2010 is { } e10 && File.Exists(e10)) todo.Add(("2010", e10, VcRuntime2013.InstallArguments2010));
        if (!snap.VcRuntime2013 && snap.VcRedist2013 is { } e13 && File.Exists(e13)) todo.Add(("2013", e13, VcRuntime2013.InstallArguments));
        if (todo.Count == 0)
        {
            try { Process.Start(new ProcessStartInfo { FileName = VcRuntime2013.DownloadPage, UseShellExecute = true })?.Dispose(); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return (true, "Microsoft's page is open.");
        }
        foreach (var (name, exe, args) in todo)
        {
            Say(output, new { say = $"Installing Visual C++ {name}..." });
            try
            {
                using var p = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = true });
                if (p is null) continue;
                await p.WaitForExitAsync(ct);
                if (!VcRuntime2013.InstalledByExitCode(p.ExitCode)) return (false, $"Microsoft's installer for Visual C++ {name} stopped (code {p.ExitCode}).");
            }
            catch (System.ComponentModel.Win32Exception w) when (w.NativeErrorCode == 1223) { return (false, "Windows' permission prompt was declined."); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (false, $"Microsoft's installer for Visual C++ {name} could not be started."); }
        }
        return (true, "Installed.");
    }

    private static (bool, string) LinkFacials(SetupSnapshot snap, TextWriter output)
    {
        if (snap.Facials.Complete) return (true, "Linked.");
        Say(output, new { say = "Linking the facial data (no copy, no extra disk space)." });
        try
        {
            var run = FacialsLink.Link(snap.Facials);
            return run.Failure is null ? (true, "Linked: NPC mouths move and their voices play.") : (false, run.Failure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (false, "The facial data could not be linked."); }
    }

    /// <summary>The one elevated step: started by <see cref="ElevatedLinker"/> through UAC. It touches nothing but the game's *.pak links, and only between a real game and a real Modding Tools.</summary>
    private static int LinkElevated(Dictionary<string, string> opt)
    {
        opt.TryGetValue("game", out var game); opt.TryGetValue("mt", out var mt); opt.TryGetValue("status", out var status);
        void Write(string text) { if (!string.IsNullOrWhiteSpace(status)) { try { File.WriteAllText(status, text); } catch (IOException) { } } }
        if (!GameLocator.GameFilesPresent(game) || mt is null || GameLocator.FindModdingToolsExe(mt) is null)
        {
            Write("result=refused\r\nreason=not a game and a Modding Tools install\r\n");
            return 2;
        }
        var report = Workspace.Verify(game!, mt);
        bool symOnly = opt.TryGetValue("kind", out var kind) && string.Equals(kind, "symlink", StringComparison.OrdinalIgnoreCase);
        var run = WorkspaceLinker.Link(report, symlinksOnly: symOnly);
        var after = Workspace.Verify(game!, mt);
        Write(Redact.Text($"result={(after.Complete ? "linked" : "incomplete")}\r\nlinked={run.Linked}\r\nfailed={run.Failed}\r\nin_place={after.OkCount}/{after.Entries.Count}\r\n"));
        return after.Complete ? 0 : 1;
    }

    // ---- small things -----------------------------------------------------------------------------------------------------------------

    // Cairn is the key the launcher's uninstaller looks at: the mod's folder is ours when HKCU\Software\Cairn\ModsPath names it.
    private static bool OwnsModFolder(string target)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Cairn");
            return key?.GetValue("ModsPath") is string p && SetupPaths.SamePath(p, target);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    private static void RecordModsPath(string target, string? gameExe)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Cairn");
            key.SetValue("ModsPath", target);
            if (gameExe is not null) key.SetValue("GamePath", gameExe);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    private static Dictionary<string, string> Options(IEnumerable<string> rest)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? key = null;
        foreach (var a in rest)
        {
            if (a.StartsWith("--", StringComparison.Ordinal)) { key = a[2..]; d[key] = ""; }
            else if (key is not null) { d[key] = a; key = null; }
        }
        return d;
    }

    private static void Say(TextWriter output, object message) { output.WriteLine(JsonSerializer.Serialize(message)); output.Flush(); }
}
