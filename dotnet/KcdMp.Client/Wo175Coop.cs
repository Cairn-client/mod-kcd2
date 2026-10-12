// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KcdMp.Wire;

namespace KcdMp.Client;

/// <summary>
/// WO-175 S1: a co-op session never writes into the player's own single-player saves (switch <c>mp_coop_playline</c>,
/// default ON; remembered as <c>CoopPlayline</c> in mod-settings.json beside the agent).
///
/// <list type="bullet">
/// <item><b>The host</b> plays a world of its own in a COPY: the chosen save's bytes unchanged (the same playthrough
///   seed, so the WO-125 world identity stays) in a co-op playline -- the one this ledger (<c>w175-coop.json</c>, beside
///   w159-staged.json) records for the world's seed tag, else the lowest FREE playline. The engine lists saves once, at
///   its start, and the host has no plugin at the menu to rescan (WO-159 0.3), so the copies are PREPARED before the
///   game starts (<see cref="Prepare"/>): one per world that already has a co-op playline (a new file there; never over
///   a save), and one placeholder in a free playline for the worlds that have none yet (the start save's playline when
///   it is the only free one). The menu's pick COMMITS one (<see cref="Commit"/>): the placeholder takes the chosen
///   world's bytes (a file written over a listed one is what the load reads, WO-159 probe), the rest are taken back
///   (only while they are still the bytes written). No free playline: refused in plain words, nothing loaded.</item>
/// <item><b>The menu</b> lists a co-op playline's newest save as "Together: &lt;the original world's label&gt;" (the
///   world to continue: picked, it loads as it is); the original stays listed (picked again: a fresh copy, same
///   co-op playline).</item>
/// <item><b>The joiner</b>'s spliced world (WO-124 mpworld file) goes into the co-op playline for the HOST's world tag,
///   never into a playline holding a save of another playthrough (<see cref="ChooseForJoin"/>).</item>
/// </list>
/// A co-op playline is usable only while every save in it is of that world's seed (the mod's own transient
/// mpworld / mpexit files aside): a playline that came to hold anything else is the player's again. Never an index
/// above 4 (wh_sys_LoadGame 5 crashes the game).
/// </summary>
public static class Wo175Coop
{
    public const string CliTag = "W175C";

    /// <summary>The switch's key in mod-settings.json (beside the agent; the WO-154 file).</summary>
    public const string SwitchKey = "CoopPlayline";

    public const string NoFreeText =
        "To keep your own saves safe, co-op plays in a copy of your world and needs one free save slot (playline). All five are in use. Free one, or turn off mp_coop_playline to play in the original.";

    public const string JoinerNoFreeText =
        "To keep your own saves safe, co-op puts your host's world in a save slot (playline) of its own and needs one free slot. All five are in use. Free one, or turn off mp_coop_playline.";

    public const string NotPreparedText =
        "The co-op copy of this world was not prepared before the game started. Close the game and start it again from the launcher.";

    // ------------------------------------------------------------------ the switch

    /// <summary>mod-settings.json beside this agent (the WO-154 menu's file).</summary>
    public static string ModSettingsBesideAgent() => Path.Combine(AppContext.BaseDirectory, Wo154MenuRules.ModSettingsFile);

    /// <summary>On unless the file says off (missing file, missing key, a value it cannot read: on).</summary>
    public static bool SwitchOn(string modSettings)
    {
        if (SettingsJson.Read(modSettings) is not JsonObject f || !f.TryGetPropertyValue(SwitchKey, out JsonNode? n) || n is not JsonValue v) return true;
        if (v.TryGetValue(out bool b)) return b;
        if (v.TryGetValue(out string? s)) return s.Trim().ToLowerInvariant() is not ("off" or "false" or "0");
        return true;
    }

    /// <summary>"on" / "off" (the mod's <c>w175_coop</c> event, the console command's word).</summary>
    public static bool TryParseSwitch(string? arg, out bool on)
    {
        string a = (arg ?? "").Trim().ToLowerInvariant();
        on = a == "on";
        return a is "on" or "off";
    }

    public static SettingsJson.Result SetSwitch(string modSettings, bool on) =>
        SettingsJson.Update(modSettings, [SettingsJson.Edit.Set(SwitchKey, JsonValue.Create(on))], createIfMissing: true);

    // ------------------------------------------------------------------ the ledger

    public sealed class CoopRecord
    {
        public string SeedTag { get; set; } = "";
        public int OriginPlayline { get; set; } = -1;   // -1: a joiner's (the host's world has no playline here)
        public int CoopPlayline { get; set; } = -1;
        public DateTime CreatedUtc { get; set; }
        public string LastCopiedFrom { get; set; } = "";
        public string LastCopiedSha256 { get; set; } = "";
    }

    /// <summary>&lt;data&gt;/w175-coop.json, beside w159-staged.json and the Henry store.</summary>
    public static string LedgerPath(string? dataRoot = null) =>
        Path.Combine(dataRoot ?? Path.GetDirectoryName(HenryStore.DefaultRoot())!, "w175-coop.json");

    public static List<CoopRecord> ReadLedger(string ledger)
    {
        try { return File.Exists(ledger) ? JsonSerializer.Deserialize<List<CoopRecord>>(File.ReadAllText(ledger)) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or JsonException) { return []; }
    }

    private static void WriteJson<T>(string path, T o)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".part", JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".part", path, overwrite: true);
    }

    /// <summary>
    /// This world plays in <paramref name="coopPl"/> from now on: its record updated (or made); a record of another
    /// world that named the same playline is dropped (that playline holds this world now).
    /// </summary>
    public static void Remember(string ledger, string tag, int originPl, int coopPl, string from, string sha)
    {
        var l = ReadLedger(ledger);
        l.RemoveAll(r => r.CoopPlayline == coopPl && r.SeedTag != tag);
        var rec = l.LastOrDefault(r => r.SeedTag == tag && r.CoopPlayline == coopPl) ?? l.LastOrDefault(r => r.SeedTag == tag);
        if (rec is null) { rec = new CoopRecord { SeedTag = tag, CreatedUtc = DateTime.UtcNow }; l.Add(rec); }
        else if (rec.CoopPlayline != coopPl) rec.CreatedUtc = DateTime.UtcNow;
        rec.OriginPlayline = originPl;
        rec.CoopPlayline = coopPl;
        rec.LastCopiedFrom = from;
        rec.LastCopiedSha256 = sha;
        WriteJson(ledger, l);
    }

    // ------------------------------------------------------------------ the rules

    /// <summary>The mod's own transient files (WO-124 mpworld, WO-125 mpexit): not a save of anybody's.</summary>
    private static bool IsTransient(string file)
    {
        string n = Path.GetFileName(file);
        return n.StartsWith("mpworld", StringComparison.OrdinalIgnoreCase) || n.StartsWith("mpexit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Playline <paramref name="pl"/> may hold world <paramref name="tag"/>'s co-op saves: 0..4, and every save in it
    /// is of that seed (an empty or missing folder is fine; a save that cannot be read is not).</summary>
    public static bool IsCoopUsable(string saves, int pl, string tag, ISet<string>? ignore = null)
    {
        if (pl is < 0 or > 4 || tag.Length == 0) return false;
        string dir = Path.Combine(saves, $"playline{pl}");
        if (!Directory.Exists(dir)) return true;
        foreach (var f in Directory.EnumerateFiles(dir, "*.whs"))
        {
            if (IsTransient(f) || (ignore?.Contains(f) ?? false)) continue;
            if (WhsSave.ReadSeedFromFile(f) is not uint s || WhsSave.SeedTag(s) != tag) return false;
        }
        return true;
    }

    /// <summary>The co-op playline the ledger records for this world, still usable and not its origin; -1 for none.</summary>
    public static int RecordedCoop(string saves, IReadOnlyList<CoopRecord> records, string tag, int originPl, ISet<string>? ignore = null)
    {
        for (int i = records.Count - 1; i >= 0; i--)
        {
            var r = records[i];
            if (r.SeedTag == tag && r.CoopPlayline is >= 0 and <= 4 && r.CoopPlayline != originPl && IsCoopUsable(saves, r.CoopPlayline, tag, ignore))
                return r.CoopPlayline;
        }
        return -1;
    }

    private static readonly Regex EngineName = new(@"^(autosave|quicksave|crucialdecision|permanent|save)(\d{3})\.whs$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SaveIdRx = new("\\bSaveId=\"(\\d+)\"", RegexOptions.CultureInvariant);

    /// <summary>SaveId="n" from a save's description header, or null.</summary>
    public static int? ReadSaveId(byte[] file)
    {
        if (file.Length < 8 || BitConverter.ToUInt32(file, 0) != 0xFFFFFFFFu) return null;
        int n = BitConverter.ToInt32(file, 4);
        if (n <= 0 || n > 64 * 1024 || 8 + n > file.Length) return null;
        var m = SaveIdRx.Match(Encoding.UTF8.GetString(file, 8, n));
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : null;
    }

    /// <summary>
    /// A name for a copy in <paramref name="dir"/> that no file there has. The engine names its next save after the
    /// playline's highest SaveId + 1 and writes over a file of that name (WO-125, WO-135), so the copy's number stays at
    /// or below its own SaveId (<paramref name="maxNumber"/>): the engine's own names are always above it. The original's
    /// name when it fits, else the lowest free number with its prefix, else with "save". Null when none is free.
    /// </summary>
    public static string? CopyName(string dir, string originFile, int maxNumber)
    {
        bool Free(string n) => !File.Exists(Path.Combine(dir, n)) && !File.Exists(Path.Combine(dir, n + ".part"));
        int cap = Math.Clamp(maxNumber, 1, 999);
        var m = EngineName.Match(Path.GetFileName(originFile));
        string prefix = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "save";
        if (m.Success && int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) is int num && num >= 1 && num <= cap && Free(Path.GetFileName(originFile).ToLowerInvariant()))
            return Path.GetFileName(originFile).ToLowerInvariant();
        foreach (var p in prefix == "save" ? new[] { "save" } : new[] { prefix, "save" })
            for (int k = 1; k <= cap; k++)
            {
                string n = string.Create(CultureInfo.InvariantCulture, $"{p}{k:D3}.whs");
                if (Free(n)) return n;
            }
        return null;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string? FileSha(string path)
    {
        try { return Sha(WhsSave.ReadShared(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>"playline2/autosave041" (no extension).</summary>
    public static string Display(string path) =>
        $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileNameWithoutExtension(path)}";

    /// <summary>A NEW file (never over one): .part, rename without overwrite, read back. The sha256, or null (nothing left behind).</summary>
    private static string? WriteNew(string final, byte[] bytes)
    {
        string part = final + ".part";
        try
        {
            using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write)) fs.Write(bytes);
            File.Move(part, final, overwrite: false);
        }
        catch (IOException) { try { File.Delete(part); } catch (IOException) { } return null; }
        string sha = Sha(bytes);
        if (FileSha(final) != sha) { try { File.Delete(final); } catch (IOException) { } return null; }
        return sha;
    }

    // ------------------------------------------------------------------ the prepared copies (before the game starts)

    public sealed class PendingFile
    {
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public bool CreatedDir { get; set; }
        public DateTime PreparedUtc { get; set; }
    }

    /// <summary>What the menu's pick of one world does: copy (into <see cref="Target"/>), together (load it as it is), refused.</summary>
    public sealed class PlanEntry
    {
        public int Playline { get; set; }
        public string Name { get; set; } = "";
        public string SeedTag { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Target { get; set; } = "";
    }

    public sealed class PendingState
    {
        public bool On { get; set; }
        public bool Prepared { get; set; }
        public List<PendingFile> Files { get; set; } = [];
        public List<PlanEntry> Plan { get; set; } = [];
    }

    /// <summary>&lt;data&gt;/w175-pending.json, beside the ledger: this launch's prepared copies and the plan.</summary>
    public static string PendingPath(string ledger) => Path.Combine(Path.GetDirectoryName(ledger)!, "w175-pending.json");

    public static PendingState ReadPending(string ledger)
    {
        string p = PendingPath(ledger);
        try { return File.Exists(p) ? JsonSerializer.Deserialize<PendingState>(File.ReadAllText(p)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }

    /// <summary>The prepared copies' paths (the menu does not list them as worlds).</summary>
    public static HashSet<string> PendingPaths(string ledger) =>
        ReadPending(ledger).Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public sealed record PrepareResult(bool On, List<PlanEntry> Plan, List<string> Lines);

    /// <summary>
    /// Before the game starts: per world of <paramref name="worlds"/> (the menu's list) what its pick will do, and the
    /// copies that needs, written now (the engine lists them at its start). <paramref name="staged"/>: the unplayed start
    /// save (WO-159), the placeholder when no playline is free. Run <see cref="Drop"/> first.
    /// </summary>
    public static PrepareResult Prepare(string saves, string ledger, Wo159.SavesReport worlds, bool on, string? staged = null)
    {
        var st = new PendingState { On = on, Prepared = true };
        var lines = new List<string>();
        if (!on)
        {
            WriteJson(PendingPath(ledger), st);
            lines.Add("WO175-COOP off -- plays in the original");
            return new(false, st.Plan, lines);
        }
        var records = ReadLedger(ledger);
        var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = worlds.Usable.Select(u => (u, Path: Path.Combine(saves, $"playline{u.Playline}", u.Name + ".whs"))).ToList();
        var tags = entries.ToDictionary(e => e.Path, e => WhsSave.ReadSeedFromFile(e.Path) is uint s ? WhsSave.SeedTag(s) : "", StringComparer.OrdinalIgnoreCase);
        // the co-op playlines first: their newest save is the world to continue
        var together = entries.Where(e => tags[e.Path].Length > 0 && records.Any(r => r.CoopPlayline == e.u.Playline && r.SeedTag == tags[e.Path])
                                          && IsCoopUsable(saves, e.u.Playline, tags[e.Path])).Select(e => e.u.Playline).ToHashSet();
        string? spare = null;
        foreach (var (u, path) in entries)
        {
            string tag = tags[path];
            var e = new PlanEntry { Playline = u.Playline, Name = u.Name, SeedTag = tag };
            st.Plan.Add(e);
            if (together.Contains(u.Playline)) { e.Kind = "together"; e.Target = path; lines.Add($"{Display(path)}: Together (co-op playline{u.Playline}: continue)"); continue; }
            if (tag.Length == 0) { e.Kind = "refused"; lines.Add($"{Display(path)}: no playthrough seed -- not offered for co-op"); continue; }
            byte[] bytes;
            try { bytes = WhsSave.ReadShared(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { e.Kind = "refused"; lines.Add($"{Display(path)}: unreadable ({ex.GetType().Name})"); continue; }
            int coop = RecordedCoop(saves, records, tag, u.Playline, mine);
            if (coop >= 0)
            {
                string dir = Path.Combine(saves, $"playline{coop}");
                bool created = !Directory.Exists(dir);
                Directory.CreateDirectory(dir);
                string? name = CopyName(dir, u.Name + ".whs", ReadSaveId(bytes) ?? 1);
                string? sha = name is null ? null : WriteNew(Path.Combine(dir, name), bytes);
                if (sha is not null)
                {
                    string f = Path.Combine(dir, name!);
                    st.Files.Add(new PendingFile { Path = f, Sha256 = sha, CreatedDir = created, PreparedUtc = DateTime.UtcNow });
                    mine.Add(f);
                    e.Kind = "copy"; e.Target = f;
                    lines.Add($"{Display(path)}: a copy prepared in its co-op playline as {Display(f)}");
                    continue;
                }
                lines.Add($"{Display(path)}: its co-op playline{coop} took no new file -- a free playline instead");
            }
            if (spare is null)
            {
                var free = Wo159.FreePlaylines(saves);
                if (free.Count > 0)
                {
                    string dir = Path.Combine(saves, $"playline{free[0]}");
                    bool created = !Directory.Exists(dir);
                    Directory.CreateDirectory(dir);
                    // the placeholder may take another world's bytes at the pick: number 001, below any engine name
                    string? name = CopyName(dir, u.Name + ".whs", 1);
                    string? sha = name is null ? null : WriteNew(Path.Combine(dir, name), bytes);
                    if (sha is not null)
                    {
                        spare = Path.Combine(dir, name!);
                        st.Files.Add(new PendingFile { Path = spare, Sha256 = sha, CreatedDir = created, PreparedUtc = DateTime.UtcNow });
                        mine.Add(spare);
                        lines.Add($"the placeholder for a world's first co-op copy: {Display(spare)} (free playline{free[0]})");
                    }
                    else if (created) { try { Directory.Delete(dir); } catch (IOException) { } }
                }
                else if (staged is not null && File.Exists(staged))
                {
                    spare = staged;
                    lines.Add($"no free playline: the unplayed start save {Display(staged)} is the placeholder (New adventure or a co-op copy, whichever is picked)");
                }
            }
            if (spare is not null) { e.Kind = "copy"; e.Target = spare; lines.Add($"{Display(path)}: first co-op copy into {Display(spare)} at the pick"); }
            else { e.Kind = "refused"; lines.Add($"{Display(path)}: refused at the pick (no free playline)"); }
        }
        WriteJson(PendingPath(ledger), st);
        return new(true, st.Plan, lines);
    }

    public sealed record DropResult(int Removed, int Kept, List<string> Lines);

    /// <summary>
    /// Every prepared copy not committed: removed while it is still the bytes written (an exact copy of a save that
    /// stays where it was), kept otherwise; a folder made for it goes when empty. The plan is cleared.
    /// </summary>
    public static DropResult Drop(string ledger, string? keep = null)
    {
        var st = ReadPending(ledger);
        int removed = 0, kept = 0;
        var lines = new List<string>();
        foreach (var f in st.Files)
        {
            if (keep is not null && string.Equals(f.Path, keep, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(f.Path)) { lines.Add($"{Display(f.Path)}: gone already"); continue; }
            if (FileSha(f.Path) == f.Sha256)
            {
                try
                {
                    File.Delete(f.Path);
                    string dir = Path.GetDirectoryName(f.Path)!;
                    if (f.CreatedDir && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                    removed++;
                    lines.Add($"{Display(f.Path)}: prepared, not picked -- removed");
                }
                catch (IOException ex) { kept++; lines.Add($"{Display(f.Path)}: could not be removed ({ex.GetType().Name}) -- kept"); }
            }
            else { kept++; lines.Add($"{Display(f.Path)}: changed since it was prepared -- kept as a normal save"); }
        }
        if (File.Exists(PendingPath(ledger)) || st.Files.Count > 0) WriteJson(PendingPath(ledger), new PendingState());
        return new(removed, kept, lines);
    }

    // ------------------------------------------------------------------ the menu's pick

    /// <summary>Action: original (switch off), continue (a co-op playline's save, as it is), copy (into To), refused.</summary>
    public sealed record CommitResult(bool Ok, string Action, int Playline, string Name, string Why, string Line);

    /// <summary>
    /// The host picked world (<paramref name="pl"/>, <paramref name="name"/>) at the menu: what to load. A copy commits
    /// the prepared file (the world's bytes written over the placeholder when it holds another's), records the co-op
    /// playline, and takes every other prepared copy back. A refusal writes nothing.
    /// </summary>
    public static CommitResult Commit(string saves, string ledger, int pl, string name, bool on, string? w159Ledger = null)
    {
        var st = ReadPending(ledger);
        if (!on)
        {
            Drop(ledger);
            return new(true, "original", pl, name, "", "WO175-COOP off -- plays in the original");
        }
        var e = st.Plan.FirstOrDefault(p => p.Playline == pl && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        // picked again after a load that failed (the plan went with the first commit): the copy made then, as it is
        if (e is null && SameCopyInCoop(saves, ledger, pl, name) is string again)
            return new(true, "continue", PlaylineOf(again), Path.GetFileNameWithoutExtension(again), "", $"WO175-COOP continue {Display(again)} (the copy of playline{pl}/{name} made this launch)");
        if (e is null)
            return new(false, "refused", -1, "", NotPreparedText, $"WO175-COOP refused reason=not-prepared (playline{pl}/{name} {(st.Prepared ? "was not in this launch's list" : "-- nothing was prepared this launch")})");
        if (e.Kind == "together")
        {
            Drop(ledger);
            return new(true, "continue", pl, name, "", $"WO175-COOP continue playline{pl}/{name}");
        }
        if (e.Kind != "copy" || e.Target.Length == 0)
            return new(false, "refused", -1, "", NoFreeText, "WO175-COOP refused reason=no-free-playline");

        string origin = Path.Combine(saves, $"playline{pl}", name + ".whs");
        byte[] bytes;
        try { bytes = WhsSave.ReadShared(origin); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "refused", -1, "", "Your world could not be read.", $"WO175-COOP refused reason=unreadable ({ex.GetType().Name})"); }
        string sha = Sha(bytes), target = e.Target;
        var pf = st.Files.FirstOrDefault(f => string.Equals(f.Path, target, StringComparison.OrdinalIgnoreCase));
        Wo159.Staged? stg = null;
        if (pf is null && w159Ledger is not null)
            stg = Wo159.ReadLedger(w159Ledger).FirstOrDefault(x => string.Equals(x.Path, target, StringComparison.OrdinalIgnoreCase));
        string? cur = File.Exists(target) ? FileSha(target) : null;
        // only a file of ours, still as written: a prepared copy, or the start save never played
        bool ours = cur is not null && ((pf is not null && cur == pf.Sha256) || (stg is not null && cur == stg.Sha256));
        if (!ours)
            return new(false, "refused", -1, "", NotPreparedText, $"WO175-COOP refused reason=prepared-copy-changed ({Display(target)})");
        if (cur != sha)
        {
            string part = target + ".part";
            try
            {
                File.WriteAllBytes(part, bytes);
                File.Move(part, target, overwrite: true);
            }
            catch (IOException ex) { try { File.Delete(part); } catch (IOException) { } return new(false, "refused", -1, "", NotPreparedText, $"WO175-COOP refused reason=write-failed ({ex.GetType().Name})"); }
            if (FileSha(target) != sha) return new(false, "refused", -1, "", NotPreparedText, $"WO175-COOP refused reason=read-back ({Display(target)})");
        }
        int coopPl = PlaylineOf(target);
        // a normal save from now on: out of the pending list (and the start save's ledger), never taken back
        Drop(ledger, keep: target);
        if (stg is not null && w159Ledger is not null) Wo159.MarkUsed(w159Ledger, target);
        Remember(ledger, e.SeedTag, pl, coopPl, $"playline{pl}/{name}", sha);
        string to = Path.GetFileNameWithoutExtension(target);
        return new(true, "copy", coopPl, to, "",
            $"WO175-COOP copy from=playline{pl}/{name} to=playline{coopPl}/{to} seed={e.SeedTag} sha256={sha[..12]}");
    }

    /// <summary>A file in the world's recorded co-op playline with exactly the bytes of the original's save (the copy
    /// committed earlier), or null.</summary>
    private static string? SameCopyInCoop(string saves, string ledger, int pl, string name)
    {
        string origin = Path.Combine(saves, $"playline{pl}", name + ".whs");
        if (WhsSave.ReadSeedFromFile(origin) is not uint seed || FileSha(origin) is not string sha) return null;
        int coop = RecordedCoop(saves, ReadLedger(ledger), WhsSave.SeedTag(seed), pl);
        if (coop < 0) return null;
        string dir = Path.Combine(saves, $"playline{coop}");
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "*.whs").FirstOrDefault(f => !IsTransient(f) && FileSha(f) == sha);
    }

    /// <summary>N of a ".../playlineN/file" path (0..4), or -1.</summary>
    public static int PlaylineOf(string path)
    {
        string d = Path.GetFileName(Path.GetDirectoryName(path) ?? "").ToLowerInvariant();
        return d.Length == 9 && d.StartsWith("playline", StringComparison.Ordinal) && d[8] is >= '0' and <= '4' ? d[8] - '0' : -1;
    }

    // ------------------------------------------------------------------ the joiner

    public sealed record JoinPick(bool Ok, int Playline, bool FromLedger);

    /// <summary>
    /// Where the joiner's spliced copy of the host's world (<paramref name="hostTag"/>, the plain seed tag) goes: the
    /// recorded co-op playline when it still holds nothing but that world, else the lowest free playline; none: refused.
    /// </summary>
    public static JoinPick ChooseForJoin(string saves, string ledger, string hostTag)
    {
        int rec = RecordedCoop(saves, ReadLedger(ledger), hostTag, -1);
        if (rec >= 0) return new(true, rec, true);
        var free = Wo159.FreePlaylines(saves);
        return free.Count > 0 ? new(true, free[0], false) : new(false, -1, false);
    }

    /// <summary>
    /// The menu's list with every co-op playline's entry labelled "Together: &lt;the original world's label&gt;" (the
    /// original's own entry when it is listed, else its playline).
    /// </summary>
    public static Wo159.SavesReport LabelTogether(Wo159.SavesReport r, string saves, IReadOnlyList<CoopRecord> records)
    {
        if (records.Count == 0) return r;
        var usable = new List<Wo159.MenuSave>();
        foreach (var u in r.Usable)
        {
            string path = Path.Combine(saves, $"playline{u.Playline}", u.Name + ".whs");
            string tag = WhsSave.ReadSeedFromFile(path) is uint s ? WhsSave.SeedTag(s) : "";
            var rec = tag.Length == 0 ? null : records.LastOrDefault(x => x.CoopPlayline == u.Playline && x.SeedTag == tag);
            if (rec is null || !IsCoopUsable(saves, u.Playline, tag)) { usable.Add(u); continue; }
            var orig = r.Usable.FirstOrDefault(o => o.Playline == rec.OriginPlayline && o.Playline != u.Playline);
            string of = orig is not null ? orig.Label : rec.OriginPlayline is >= 0 and <= 4 ? $"Playline {rec.OriginPlayline}" : u.Label;
            usable.Add(u with { Label = "Together: " + of });
        }
        return r with { Usable = usable };
    }

    // ------------------------------------------------------------------ CLI

    /// <summary>
    /// <c>--w175coop prepare [--saves DIR] [--data DIR]</c> · <c>commit --pl N --name NAME</c> · <c>drop</c> ·
    /// <c>switch [on|off]</c>. One line: <c>W175C {json}</c>.
    /// </summary>
    public static int RunCli(string[] args, TextWriter w)
    {
        string? Opt(string k) { int i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        int at = Array.IndexOf(args, "--w175coop");
        string cmd = at >= 0 && at + 1 < args.Length ? args[at + 1] : "";
        string? saves = Opt("--saves") ?? GameBridge.ResolveSavesDir();
        string w159Ledger = Wo159.LedgerPath(Opt("--data"));
        string ledger = LedgerPath(Opt("--data"));
        string settings = Opt("--settings") ?? ModSettingsBesideAgent();
        void Out(object o) => w.WriteLine(CliTag + " " + JsonSerializer.Serialize(o));
        switch (cmd)
        {
            case "prepare":
            {
                var d = Drop(ledger);
                if (saves is null) { Out(new { ok = false, on = SwitchOn(settings), why = "no saves folder", lines = d.Lines }); return 1; }
                var staged = Wo159.ReadLedger(w159Ledger).LastOrDefault(e => File.Exists(e.Path))?.Path;
                var r = Prepare(saves, ledger, Wo159.HostWorlds(saves, w159Ledger), SwitchOn(settings), staged);
                Out(new
                {
                    ok = true,
                    on = r.On,
                    plan = r.Plan.Select(p => new { pl = p.Playline, name = p.Name, kind = p.Kind, to = p.Target.Length == 0 ? "" : Display(p.Target) }),
                    lines = d.Lines.Concat(r.Lines),
                });
                return 0;
            }
            case "commit":
            {
                if (saves is null) { Out(new { ok = false, action = "refused", why = "no saves folder", line = "WO175-COOP refused reason=no-saves-folder" }); return 1; }
                int pl = int.TryParse(Opt("--pl"), NumberStyles.None, CultureInfo.InvariantCulture, out int p) ? p : -1;
                string name = Opt("--name") ?? "";
                if (pl is < 0 or > 4 || name.Length == 0) { Out(new { ok = false, action = "refused", why = "usage", line = "WO175-COOP refused reason=bad-arguments" }); return 2; }
                var r = Commit(saves, ledger, pl, name, SwitchOn(settings), w159Ledger);
                Out(new { ok = r.Ok, action = r.Action, pl = r.Playline, name = r.Name, why = r.Why, line = r.Line });
                return r.Ok ? 0 : 1;
            }
            case "drop":
            {
                var r = Drop(ledger);
                Out(new { ok = true, removed = r.Removed, kept = r.Kept, lines = r.Lines });
                return 0;
            }
            case "switch":
            {
                string? v = at + 2 < args.Length ? args[at + 2] : null;
                if (TryParseSwitch(v, out bool on))
                {
                    var r = SetSwitch(settings, on);
                    Out(new { ok = r.Outcome != SettingsJson.Outcome.Refused, on = SwitchOn(settings), outcome = r.Outcome.ToString().ToLowerInvariant() });
                    return 0;
                }
                Out(new { ok = true, on = SwitchOn(settings) });
                return 0;
            }
        }
        Out(new { ok = false, why = "usage: --w175coop prepare|commit|drop|switch" });
        return 2;
    }
}
