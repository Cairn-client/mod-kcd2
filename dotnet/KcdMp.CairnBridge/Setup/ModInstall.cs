// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Security.Cryptography;
using System.Text.Json;

namespace KcdMp.CairnBridge.Setup;

public sealed record ManifestEntry(string Kind, string Rel, long Size, string Sha256);

/// <summary>What is so about the mod in the Modding Tools' Mods folder, read against this pack's own files.</summary>
public sealed class ModReport
{
    /// <summary>Mod files that are not in &lt;MT&gt;\Mods\&lt;mod folder&gt; at all.</summary>
    public List<string> ModMissing { get; init; } = new();
    /// <summary>Mod files that are there but are not the bytes of this pack (sha256): a hand-built pak, typically. A warning, never a stop.</summary>
    public List<string> ModDifferent { get; init; } = new();
    /// <summary>The pack's own mod files are on this PC and intact, so they can be placed.</summary>
    public bool StagedAvailable { get; init; }
    /// <summary>An earlier mod of ours is still in a folder this pack replaces (e.g. &lt;MT&gt;\Mods\cairn); with both there, the game loads two mods.</summary>
    public bool LegacyFolder { get; init; }
    /// <summary>The mod folder is there but its mod.manifest is not the pack's, and nothing says we put it there.</summary>
    public bool ForeignFolder { get; init; }
    public string TargetDir { get; init; } = "";

    public bool Placed => !LegacyFolder && ModMissing.Count == 0 && !ForeignFolder;
    public bool Ok => Placed;
    public IEnumerable<string> Warnings => ModDifferent;
}

/// <summary>
/// The mod half of setup, done from the pack this program belongs to: the pack's <c>manifest.txt</c> lists the MOD files (relative to
/// &lt;MT&gt;\Mods\&lt;mod folder&gt;), the files themselves are in <c>files\mod</c>, and <c>pack.json</c> names the mod folder and the
/// earlier folders it replaces. (This was the launcher's code until the setup moved into the pack.)
/// </summary>
public static class ModInstall
{
    public const string DefaultModFolder = "kdcmp";

    /// <summary>The pack's folder (pack.json, manifest.txt, files\) from this program's place: &lt;pack&gt;\files\bin\CairnBridgeKcd2.exe.</summary>
    public static string DefaultPackRoot => Environment.GetEnvironmentVariable("CAIRN_PACK_ROOT") is { Length: > 0 } e ? e : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));

    public static string FilesDir(string packRoot) => Path.Combine(packRoot, "files");
    public static string EntryPath(string packRoot, ManifestEntry e) =>
        Path.Combine(FilesDir(packRoot), string.Equals(e.Kind, "BIN", StringComparison.OrdinalIgnoreCase) ? "bin" : "mod", e.Rel);

    /// <summary>The pack's manifest lines (MOD and BIN files); empty when there is no manifest.</summary>
    public static List<ManifestEntry> ReadManifest(string packRoot)
    {
        var list = new List<ManifestEntry>();
        string path = Path.Combine(packRoot, "manifest.txt");
        try
        {
            if (!File.Exists(path)) return list;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('|');
                if (f.Length < 4 || f[1].Length == 0 || !long.TryParse(f[2], out long size)) continue;
                list.Add(new ManifestEntry(f[0].ToUpperInvariant(), f[1], size, f[3].ToLowerInvariant()));
            }
        }
        catch (IOException) { }
        return list;
    }

    private static JsonElement? Contract(string packRoot)
    {
        try
        {
            string path = Path.Combine(packRoot, "pack.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    public static string ModFolder(string? packRoot = null) =>
        Contract(packRoot ?? DefaultPackRoot) is { } c && c.TryGetProperty("modFolder", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } s ? s : DefaultModFolder;

    /// <summary>The folders the pack's contract says it replaces.</summary>
    public static IReadOnlyList<string> ReplacedFolders(string? packRoot = null) =>
        Contract(packRoot ?? DefaultPackRoot) is { } c && c.TryGetProperty("replacesFolders", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToList()
            : new List<string>();

    /// <summary>Built by the agent from the player's own game files; ours, kept across a re-place.</summary>
    public static string KeysPak(string? packRoot = null) => @"Data\" + ModFolder(packRoot) + "_keys.pak";

    public static string TargetDir(string moddingToolsRoot, string? packRoot = null) => Path.Combine(moddingToolsRoot, "Mods", ModFolder(packRoot));

    public static string LegacyDir(string moddingToolsRoot, string folder) => Path.Combine(moddingToolsRoot, "Mods", folder);

    /// <summary>An earlier mod's folder is provably ours: its manifest names the folder as its own id and our mod's name, and it holds only its three files.</summary>
    private static bool IsOurOldFolder(string moddingToolsRoot, string folder)
    {
        var dir = LegacyDir(moddingToolsRoot, folder);
        var manifest = Path.Combine(dir, "mod.manifest");
        try
        {
            if (!File.Exists(manifest)) return false;
            var text = File.ReadAllText(manifest);
            if (!text.Contains($"<modid>{folder}</modid>") || !text.Contains("<name>KCD2 Multiplayer</name>")) return false;
            var ours = new HashSet<string>(new[] { "mod.manifest", $@"Data\{folder}.pak", $@"Data\{folder}_keys.pak" }
                .Select(f => Path.GetFullPath(Path.Combine(dir, f))), StringComparer.OrdinalIgnoreCase);
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).All(f => ours.Contains(Path.GetFullPath(f)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool HasLegacyFolder(string moddingToolsRoot, string? packRoot = null) =>
        ReplacedFolders(packRoot).Any(f => IsOurOldFolder(moddingToolsRoot, f));

    /// <summary>Removes the replaced folders that are ours; true when they are gone or were never there.</summary>
    public static bool RemoveLegacyFolder(string moddingToolsRoot, string? packRoot = null)
    {
        bool all = true;
        foreach (var folder in ReplacedFolders(packRoot))
        {
            if (!IsOurOldFolder(moddingToolsRoot, folder)) continue;
            try
            {
                var dir = LegacyDir(moddingToolsRoot, folder);
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) new FileInfo(f).IsReadOnly = false;
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { all = false; }
        }
        return all;
    }

    /// <summary>Whether the mod is really in &lt;MT&gt;\Mods\&lt;mod folder&gt;: every MOD file is there at its size, and no earlier copy sits beside it. Sizes only.</summary>
    public static bool IsPlaced(string moddingToolsRoot, string? packRoot = null)
    {
        packRoot ??= DefaultPackRoot;
        var mods = ReadManifest(packRoot).Where(e => e.Kind == "MOD").ToList();
        if (mods.Count == 0 || HasLegacyFolder(moddingToolsRoot, packRoot)) return false;
        var target = TargetDir(moddingToolsRoot, packRoot);
        return mods.All(f => { var fi = new FileInfo(Path.Combine(target, f.Rel)); return fi.Exists && fi.Length == f.Size; });
    }

    public static ModReport Check(string moddingToolsRoot, bool ownsModFolder, string? packRoot = null)
    {
        packRoot ??= DefaultPackRoot;
        var target = TargetDir(moddingToolsRoot, packRoot);
        var mods = ReadManifest(packRoot).Where(e => e.Kind == "MOD").ToList();
        var missing = new List<string>();
        var different = new List<string>();
        foreach (var e in mods)
        {
            if (Matches(Path.Combine(target, e.Rel), e, out var why)) continue;
            if (why == "missing") missing.Add(e.Rel); else different.Add($"{e.Rel} ({why})");
        }
        bool staged = mods.Count > 0 && mods.All(e => Matches(EntryPath(packRoot, e), e, out _));

        // Ours by content: our mod.manifest has never changed between releases, so a mod folder whose mod.manifest is not those bytes was
        // put there by someone else, unless the registry says it was us.
        var manifestEntry = mods.FirstOrDefault(e => string.Equals(e.Rel, "mod.manifest", StringComparison.OrdinalIgnoreCase));
        var targetManifest = Path.Combine(target, "mod.manifest");
        bool foreign = !ownsModFolder && manifestEntry is not null && File.Exists(targetManifest) && !Matches(targetManifest, manifestEntry, out _);

        return new ModReport
        {
            ModMissing = missing,
            ModDifferent = foreign ? new List<string>() : different,
            StagedAvailable = staged,
            LegacyFolder = HasLegacyFolder(moddingToolsRoot, packRoot),
            ForeignFolder = foreign,
            TargetDir = target,
        };
    }

    private static bool Matches(string path, ManifestEntry e, out string why)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) { why = "missing"; return false; }
        if (fi.Length != e.Size) { why = "wrong size"; return false; }
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant() != e.Sha256) { why = "wrong content"; return false; }
        }
        catch (IOException) { why = "could not be read"; return false; }
        why = "";
        return true;
    }

    public sealed record PlaceResult(bool Ok, string Message);

    /// <summary>
    /// Copies the pack's mod into &lt;MT&gt;\Mods\&lt;mod folder&gt;. Never over a foreign folder (the caller checks first). Everything in the folder
    /// but our files goes (loose pak sources there break the game outright), and each file is written under a temporary name and moved into place.
    /// </summary>
    public static PlaceResult Place(string moddingToolsRoot, string? packRoot = null)
    {
        packRoot ??= DefaultPackRoot;
        var target = TargetDir(moddingToolsRoot, packRoot);
        try
        {
            var mods = ReadManifest(packRoot).Where(e => e.Kind == "MOD").ToList();
            if (mods.Count == 0) return new PlaceResult(false, "the pack lists no mod files");
            foreach (var e in mods)
                if (!Matches(EntryPath(packRoot, e), e, out var why))
                    return new PlaceResult(false, $"the downloaded copy of {e.Rel} is {why}");
            if (!RemoveLegacyFolder(moddingToolsRoot, packRoot))
                return new PlaceResult(false, "an earlier copy of the mod could not be removed (is the game running?)");

            Directory.CreateDirectory(Path.Combine(target, "Data"));
            Prune(target, mods.Select(m => m.Rel).Append(KeysPak(packRoot)).ToList());

            foreach (var e in mods)
            {
                var dest = Path.Combine(target, e.Rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var tmp = dest + ".cairn-new";
                File.Copy(EntryPath(packRoot, e), tmp, overwrite: true);
                if (File.Exists(dest)) new FileInfo(dest).IsReadOnly = false;
                File.Move(tmp, dest, overwrite: true);
            }
            foreach (var e in mods)
                if (!Matches(Path.Combine(target, e.Rel), e, out var why))
                    return new PlaceResult(false, $"{e.Rel} is {why} after copying");
            return new PlaceResult(true, $"placed {mods.Count} file(s)");
        }
        catch (Exception ex)
        {
            return new PlaceResult(false, ex is IOException ? "a file is in use (is the game running?)" : ex.GetType().Name);
        }
    }

    /// <summary>Takes the mod back out: only the pack's files (and the keys pak), then the folders left empty. Anything else in the folder stays.</summary>
    public static PlaceResult Unplace(string moddingToolsRoot, string? packRoot = null)
    {
        packRoot ??= DefaultPackRoot;
        var target = TargetDir(moddingToolsRoot, packRoot);
        try
        {
            if (!Directory.Exists(target)) return new PlaceResult(true, "nothing to remove");
            int removed = 0;
            foreach (var rel in ReadManifest(packRoot).Where(f => f.Kind == "MOD").Select(f => f.Rel).Append(KeysPak(packRoot)))
            {
                var path = Path.GetFullPath(Path.Combine(target, rel));
                if (!path.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
                new FileInfo(path).IsReadOnly = false;
                File.Delete(path);
                removed++;
            }
            RemoveEmptyDirs(new DirectoryInfo(target));
            return new PlaceResult(true, $"removed {removed} file(s)");
        }
        catch (Exception ex)
        {
            return new PlaceResult(false, ex is IOException ? "a file is in use (is the game running?)" : ex.GetType().Name);
        }
    }

    private static void RemoveEmptyDirs(DirectoryInfo dir)
    {
        foreach (var sub in dir.EnumerateDirectories().ToList())
        {
            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // never follow a link
            RemoveEmptyDirs(sub);
        }
        if (!dir.EnumerateFileSystemInfos().Any()) dir.Delete();
    }

    private static void Prune(string target, List<string> keepRels)
    {
        var keep = new HashSet<string>(keepRels.Select(r => Path.GetFullPath(Path.Combine(target, r))), StringComparer.OrdinalIgnoreCase);
        PruneDir(new DirectoryInfo(target), keep, isRoot: true);
    }

    /// <summary>Never follows a junction or a directory symlink: one is removed as a link, its target untouched.</summary>
    private static void PruneDir(DirectoryInfo dir, HashSet<string> keep, bool isRoot)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos().ToList())
        {
            if (entry is DirectoryInfo sub)
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) { sub.Delete(); continue; }
                PruneDir(sub, keep, isRoot: false);
                bool isData = isRoot && string.Equals(sub.Name, "Data", StringComparison.OrdinalIgnoreCase);
                if (!isData && !sub.EnumerateFileSystemInfos().Any()) sub.Delete();
                continue;
            }
            if (keep.Contains(Path.GetFullPath(entry.FullName))) continue;
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes &= ~FileAttributes.ReadOnly;
            entry.Delete();
        }
    }
}
