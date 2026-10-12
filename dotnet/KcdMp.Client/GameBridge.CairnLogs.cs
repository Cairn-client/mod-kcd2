// Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    // Cairn: the host collects everyone's logs (ProtocolCairnLogs.cs). The host's menu asks (KCD2MP_CairnCollectLogs -> the
    // "cairn_logs_collect" event); every player's agent zips its own logs and sends them back in chunks; the host's agent saves each
    // zip in one folder:
    //   <agent folder>\collected-logs\<yyyyMMdd-HHmmss>\<name>.zip     (the host's own too)
    // One switch for everyone: when the host collects, every player in the session is asked, and their agent answers. There is no
    // separate choice on the joiner's side; the joiner is told on screen that the host asked and that the logs are being sent.
    private const int CairnLogsRoundsKept = 12;
    private static readonly Regex CairnLogsRoundName = new(@"^\d{8}-\d{6}$", RegexOptions.CultureInvariant);

    private int _cairnLogsSending;                                 // one bundle at a time
    private readonly Dictionary<uint, CairnLogRound> _cairnLogsRounds = new();                         // host: the requests out
    private readonly Dictionary<(byte Src, uint Req), CairnLogRx> _cairnLogsRx = new();                // host: the bundles coming in
    private readonly object _cairnLogsGate = new();

    private sealed class CairnLogRound
    {
        public string Folder = "";
        public readonly HashSet<byte> Waiting = new();
    }

    private sealed class CairnLogRx : IDisposable
    {
        public string Path = "", Final = "", Name = "";
        public int Size, Chunks, Next;
        public byte[] Sha = Array.Empty<byte>();
        public FileStream? File;
        public void Dispose() { try { File?.Dispose(); } catch (IOException) { } }
    }

    private Task CairnLogsNoteAsync(string text) =>
        ExecLuaAsync($"if KCD2MP_CairnLogsNote then KCD2MP_CairnLogsNote(\"{EscapeLua(text)}\") end");

    // ---------------------------------------------------------------- host: ask

    /// <summary>The host's menu: "Collect everyone's logs". Only the host's agent (the damage authority) does it.</summary>
    private async Task CairnLogsCollectAsync()
    {
        if (!_isDamageAuthority) { await CairnLogsNoteAsync("Only the host can collect everyone's logs."); return; }
        var partners = LivePartners();
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string folder = Path.Combine(AppContext.BaseDirectory, "collected-logs", stamp);
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await CairnLogsNoteAsync("The logs folder could not be made."); return; }
        CairnLogsPruneOldRounds(Path.GetDirectoryName(folder)!, CairnLogsRoundsKept);

        // the host's own, at once
        try
        {
            var zip = CairnBuildLogZip(out string why);
            if (zip is null) Console.WriteLine($"MP-Logs the host's own logs could not be zipped: {why}");
            else await File.WriteAllBytesAsync(Path.Combine(folder, "host.zip"), zip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine($"MP-Logs the host's own logs: {ex.GetType().Name}"); }

        if (partners.Count == 0)
        {
            await CairnLogsNoteAsync("Your logs are saved (no other player is here): " + folder);
            return;
        }
        uint req = (uint)Random.Shared.Next(1, int.MaxValue);
        var round = new CairnLogRound { Folder = folder };
        foreach (byte g in partners) round.Waiting.Add(g);
        lock (_cairnLogsGate) _cairnLogsRounds[req] = round;
        foreach (byte g in partners)
        {
            try { await WriteJoinAsync(LogMsg.Request(req).BuildUp(g)); }
            catch (Exception ex) { Console.WriteLine($"MP-Logs the request to ghost {g} was not sent ({ex.GetType().Name})"); lock (_cairnLogsGate) round.Waiting.Remove(g); }
        }
        Console.WriteLine($"MP-Logs asked {partners.Count} player(s) for their logs (request {req}) -> {folder}");
        await CairnLogsNoteAsync($"Asked {partners.Count} player(s) for their logs; they are saved in {folder}");
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), _wo122Ct); } catch (OperationCanceledException) { return; }
            lock (_cairnLogsGate) _cairnLogsRounds.Remove(req);
        });
    }

    // ---------------------------------------------------------------- frames in

    private async Task CairnLogsOnFrameAsync(byte src, byte[] body)
    {
        if (!LogMsg.TryDecode(body, out var m)) { Console.WriteLine($"MP-Logs a malformed message from ghost {src} (len={body.Length}) dropped"); return; }
        try
        {
            switch (m.Kind)
            {
                case Protocol.LogRequest when !_isDamageAuthority:
                    await CairnLogsOnRequestAsync(src, m.Req);
                    break;
                case Protocol.LogOffer when _isDamageAuthority:
                    await CairnLogsOnOfferAsync(src, m);
                    break;
                case Protocol.LogChunk when _isDamageAuthority:
                    await CairnLogsOnChunkAsync(src, m);
                    break;
                case Protocol.LogDecline when _isDamageAuthority:
                {
                    string who = _ghostNames.TryGetValue(src, out var n) ? n : $"player {src}";
                    string why = m.Data.Length > 0 && m.Data[0] == Protocol.LogDeclineBusy ? "is busy sending an earlier bundle" : "had no logs to send";
                    Console.WriteLine($"MP-Logs ghost {src} ({who}) declined: {why}");
                    await CairnLogsNoteAsync($"{who} {why}.");
                    break;
                }
                case Protocol.LogDone when !_isDamageAuthority:
                    await CairnLogsNoteAsync(m.Data.Length > 0 && m.Data[0] == Protocol.LogDoneSaved ? "The host has your logs." : "The host could not take your logs.");
                    break;
                default:
                    Console.WriteLine($"MP-Logs {Protocol.LogKindName(m.Kind)} from ghost {src}: not for this role, dropped");
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.WriteLine($"MP-Logs {Protocol.LogKindName(m.Kind)} from ghost {src}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- player: answer a request

    private async Task CairnLogsOnRequestAsync(byte host, uint req)
    {
        if (Interlocked.Exchange(ref _cairnLogsSending, 1) == 1) { await CairnLogsSendAsync(new LogMsg(Protocol.LogDecline, req, new[] { Protocol.LogDeclineBusy })); return; }
        try
        {
            var zip = CairnBuildLogZip(out string why);
            if (zip is null) { Console.WriteLine($"MP-Logs nothing to send: {why}"); await CairnLogsSendAsync(new LogMsg(Protocol.LogDecline, req, new[] { Protocol.LogDeclineNothing })); return; }
            string name = LogMsg.SafeName(config.PlayerName ?? "player");
            int chunks = (zip.Length + Protocol.LogChunkMax - 1) / Protocol.LogChunkMax;
            var sha = SHA256.HashData(zip);
            await CairnLogsNoteAsync("The host asked for everyone's logs: sending yours (the agent's, the game's and the plugin's logs).");
            Console.WriteLine($"MP-Logs sending {zip.Length} bytes in {chunks} chunk(s) to the host (request {req})");
            await CairnLogsSendAsync(LogMsg.Offer(req, zip.Length, chunks, sha, name));
            for (int i = 0; i < chunks; i++)
            {
                int at = i * Protocol.LogChunkMax;
                await CairnLogsSendAsync(LogMsg.Chunk(req, i, zip.AsSpan(at, Math.Min(Protocol.LogChunkMax, zip.Length - at))));
                await Task.Delay(Protocol.LogChunkPauseMs, _wo122Ct);
            }
        }
        finally { Interlocked.Exchange(ref _cairnLogsSending, 0); }
    }

    private Task CairnLogsSendAsync(LogMsg m) => WriteJoinAsync(m.BuildUp(Protocol.JoinTargetHost));

    // ---------------------------------------------------------------- host: receive

    private async Task CairnLogsOnOfferAsync(byte src, LogMsg m)
    {
        CairnLogRound? round;
        lock (_cairnLogsGate) { _cairnLogsRounds.TryGetValue(m.Req, out round); if (round is not null && !round.Waiting.Contains(src)) round = null; }
        if (round is null || !LogMsg.TryOffer(m.Data, out int size, out int chunks, out byte[] sha, out string name))
        {
            Console.WriteLine($"MP-Logs an offer from ghost {src} refused (no such request, or a bad offer)");
            await WriteJoinAsync(new LogMsg(Protocol.LogDone, m.Req, new[] { Protocol.LogDoneRefused }).BuildUp(src));
            return;
        }
        string who = _ghostNames.TryGetValue(src, out var n) ? n : "";
        string file = LogMsg.SafeName(name.Length > 0 ? name : who) + "-" + src;
        var rx = new CairnLogRx { Size = size, Chunks = chunks, Sha = sha, Name = file, Path = Path.Combine(round.Folder, file + ".part"), Final = Path.Combine(round.Folder, file + ".zip") };
        rx.File = new FileStream(rx.Path, FileMode.Create, FileAccess.Write, FileShare.Read);
        lock (_cairnLogsGate) { if (_cairnLogsRx.Remove((src, m.Req), out var old)) old.Dispose(); _cairnLogsRx[(src, m.Req)] = rx; }
        Console.WriteLine($"MP-Logs ghost {src} ({who}) sends {size} bytes in {chunks} chunk(s) -> {file}.zip");
    }

    private async Task CairnLogsOnChunkAsync(byte src, LogMsg m)
    {
        CairnLogRx? rx;
        lock (_cairnLogsGate) _cairnLogsRx.TryGetValue((src, m.Req), out rx);
        if (rx is null || rx.File is null || !LogMsg.TryChunkBytes(m.Data, out int index, out byte[] bytes) || index != rx.Next) return;   // order is kept by TCP: anything else is stray
        rx.File.Write(bytes);
        rx.Next++;
        if (rx.Next < rx.Chunks) return;

        rx.File.Dispose();
        rx.File = null;
        lock (_cairnLogsGate) _cairnLogsRx.Remove((src, m.Req));
        bool ok = false;
        try
        {
            var all = await File.ReadAllBytesAsync(rx.Path);
            ok = all.Length == rx.Size && SHA256.HashData(all).AsSpan().SequenceEqual(rx.Sha);
            if (ok) File.Move(rx.Path, rx.Final, overwrite: true); else File.Delete(rx.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine($"MP-Logs {rx.Name}: {ex.GetType().Name}"); }
        await WriteJoinAsync(new LogMsg(Protocol.LogDone, m.Req, new[] { ok ? Protocol.LogDoneSaved : Protocol.LogDoneRefused }).BuildUp(src));
        string who = _ghostNames.TryGetValue(src, out var n) ? n : $"player {src}";
        Console.WriteLine($"MP-Logs {rx.Name}.zip from {who}: {(ok ? "saved" : "FAILED (size or hash)")} in {Path.GetDirectoryName(rx.Final)}");
        await CairnLogsNoteAsync(ok ? $"Logs from {who} saved in {Path.GetDirectoryName(rx.Final)}" : $"The logs from {who} did not arrive intact.");
    }

    // ---------------------------------------------------------------- the bundle

    /// <summary>
    /// This player's logs as one zip: the agent's (agent.log and the two before it), the game's kcd.log (its last 16 MB), the plugin's
    /// kcdmp-native.log, and a short info.txt. Null (and why) when there is nothing to send.
    /// </summary>
    private byte[]? CairnBuildLogZip(out string why)
    {
        why = "";
        var files = new List<(string Path, string Entry, long Tail)>();
        string baseDir = AppContext.BaseDirectory;
        foreach (string n in new[] { "agent.log", "agent.prev.log", "agent.prev2.log" })
            files.Add((Path.Combine(baseDir, n), n, 16L * 1024 * 1024));
        // the plugin writes its log beside the agent (the game folder only holds a mirror)
        files.Add((Path.Combine(baseDir, "kcdmp-native.log"), "kcdmp-native.log", 8L * 1024 * 1024));
        string? kcd = KcdLogLocator.Find();
        if (!string.IsNullOrEmpty(kcd))
        {
            files.Add((kcd, "kcd.log", 16L * 1024 * 1024));
            string? gameDir = Path.GetDirectoryName(kcd);
            if (gameDir is not null && !File.Exists(Path.Combine(baseDir, "kcdmp-native.log")))
                files.Add((Path.Combine(gameDir, "kcdmp-native.log"), "kcdmp-native.log", 8L * 1024 * 1024));
        }
        using var ms = new MemoryStream();
        int added = 0;
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var info = zip.CreateEntry("info.txt");
            using (var w = new StreamWriter(info.Open(), new UTF8Encoding(false)))
            {
                w.WriteLine($"player: {config.PlayerName}");
                w.WriteLine($"version: {ReleaseVersionInfo.Current}");
                w.WriteLine($"role: {(_isDamageAuthority ? "host" : "joiner")}");
                w.WriteLine($"collected: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            foreach (var (path, entry, tail) in files)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length > tail) fs.Seek(-tail, SeekOrigin.End);
                    var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
                    using var es = e.Open();
                    fs.CopyTo(es);
                    added++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine($"MP-Logs {entry} skipped: {ex.GetType().Name}"); }
            }
        }
        if (added == 0) { why = "no log file was found"; return null; }
        if (ms.Length > Protocol.LogMaxBytes) { why = $"the zip is {ms.Length} bytes, over the {Protocol.LogMaxBytes} limit"; return null; }
        return ms.ToArray();
    }

    /// <summary>The collected-logs rounds older than the newest <paramref name="keep"/> (timestamp-named folders only).</summary>
    public static IReadOnlyList<string> CairnLogsRoundsToPrune(IEnumerable<string> names, int keep) =>
        names.Where(n => CairnLogsRoundName.IsMatch(n)).OrderByDescending(n => n, StringComparer.Ordinal).Skip(Math.Max(0, keep)).ToList();

    private static void CairnLogsPruneOldRounds(string root, int keep)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var names = Directory.EnumerateDirectories(root).Select(p => Path.GetFileName(p) ?? "").ToList();
            foreach (string old in CairnLogsRoundsToPrune(names, keep))
            {
                try { Directory.Delete(Path.Combine(root, old), recursive: true); Console.WriteLine($"MP-Logs removed the old round {old} (the newest {keep} are kept)"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine($"MP-Logs could not remove the old round {old}: {ex.GetType().Name}"); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine($"MP-Logs could not list {root}: {ex.GetType().Name}"); }
    }
}
