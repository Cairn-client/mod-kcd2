// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KcdMp.CairnBridge;

/// <summary>One save the launcher can list: a world save of the game, or a character the host keeps for a joiner.</summary>
public sealed record SaveSlot(string Id, string Name, string Detail, string Kind, DateTime WhenUtc, string Path, bool IsDirectory);

/// <summary>
/// The saves Kingdom Come: Deliverance II and its mod have on this PC, for the launcher's Saves row (docs/BRIDGE.md in the launcher repository):
/// the game's world saves (<c>Saved Games\kingdomcome2\saves\playline*\*.whs</c>) and the characters this PC's agent keeps for the joiners it hosted
/// (<c>&lt;data&gt;\hosted-henry\&lt;player&gt;\&lt;world&gt;\</c>). A save is named to the launcher by an id made from its place, never by a path, and a
/// delete is looked up in the current list: a message cannot name a file that is not one of these.
/// </summary>
public sealed class SaveCatalog
{
    /// <summary>A world save written less than this long ago may be the one the game is playing: it is not deleted from here.</summary>
    public static readonly TimeSpan RecentlyWritten = TimeSpan.FromMinutes(10);

    private readonly string _savesRoot;
    private readonly IReadOnlyList<string> _dataRoots;

    public SaveCatalog(string savesRoot, IReadOnlyList<string> dataRoots) { _savesRoot = savesRoot; _dataRoots = dataRoots; }

    /// <summary>The game's saves folder and the mod's data folders, as the launcher's "Delete all saves" knows them (KCDMP_DATA_DIR moves the mod's).</summary>
    public static SaveCatalog Default()
    {
        // the tests' stand-in folders (CAIRN_SAVES_ROOT, and CAIRN_DATA_ROOTS separated by ;)
        if (Environment.GetEnvironmentVariable("CAIRN_SAVES_ROOT") is { Length: > 0 } sr)
            return new SaveCatalog(sr, (Environment.GetEnvironmentVariable("CAIRN_DATA_ROOTS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var data = new List<string> { Path.Combine(local, "Cairn"), Path.Combine(local, "KCDMP") };
        if (Environment.GetEnvironmentVariable("KCDMP_DATA_DIR") is { Length: > 0 } d && !data.Contains(d, StringComparer.OrdinalIgnoreCase)) data.Add(d);
        return new SaveCatalog(Path.Combine(profile, "Saved Games", "kingdomcome2", "saves"), data);
    }

    public IReadOnlyList<SaveSlot> List()
    {
        var slots = new List<SaveSlot>();
        try
        {
            if (Directory.Exists(_savesRoot))
                foreach (var line in Directory.EnumerateDirectories(_savesRoot, "playline*").OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    foreach (var f in Directory.EnumerateFiles(line, "*.whs").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        var fi = new FileInfo(f);
                        slots.Add(new SaveSlot(IdOf("w", _savesRoot, f), $"{System.IO.Path.GetFileName(line)}: {System.IO.Path.GetFileNameWithoutExtension(f)}",
                            Size(fi.Length), "world", fi.LastWriteTimeUtc, f, false));
                    }
            foreach (var root in _dataRoots.Where(Directory.Exists))
            {
                string hosted = System.IO.Path.Combine(root, "hosted-henry");
                if (!Directory.Exists(hosted)) continue;
                foreach (var player in Directory.EnumerateDirectories(hosted).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    foreach (var world in Directory.EnumerateDirectories(player).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    {
                        var files = new DirectoryInfo(world).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                        if (files.Count == 0) continue;
                        slots.Add(new SaveSlot(IdOf("c", hosted, world), $"{System.IO.Path.GetFileName(player)}'s character",
                            $"world {System.IO.Path.GetFileName(world)}, {Size(files.Sum(f => f.Length))}", "character", files.Max(f => f.LastWriteTimeUtc), world, true));
                    }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* what could not be read is not listed */ }
        return slots;
    }

    /// <summary>Deletes one listed save. False with the reason, in plain words, when it cannot be: unknown, in use, or not deletable now.</summary>
    public (bool Ok, string Message) Delete(string id, DateTime nowUtc)
    {
        var slot = List().FirstOrDefault(s => s.Id == id);
        if (slot is null) return (false, "That save is not there any more.");
        if (slot.Kind == "world" && nowUtc - slot.WhenUtc < RecentlyWritten)
            return (false, "That save was written a moment ago, so the game may be using it. Delete it after you leave the game.");
        try
        {
            if (slot.IsDirectory) Directory.Delete(slot.Path, recursive: true);
            else { new FileInfo(slot.Path).IsReadOnly = false; File.Delete(slot.Path); }
            return (true, $"Deleted {slot.Name}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, "That save could not be deleted (is it in use?).");
        }
    }

    /// <summary>The bridge message that lists the saves (docs/BRIDGE.md).</summary>
    public static string ToJson(IReadOnlyList<SaveSlot> slots)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", "saves");
            w.WriteStartArray("slots");
            foreach (var s in slots.Take(100))
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("name", s.Name);
                w.WriteString("detail", s.Detail);
                w.WriteString("kind", s.Kind);
                w.WriteString("when", s.WhenUtc.ToString("o", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>A short message the launcher shows the player (a refused delete, a deleted save).</summary>
    public static string ToastJson(string text, bool error)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", "toast");
            w.WriteString("text", text);
            w.WriteBoolean("error", error);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // the id comes from where the save is (relative to its root), so it is the same on the next listing, and is plain letters and digits
    private static string IdOf(string prefix, string root, string path) =>
        prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetRelativePath(root, path).ToLowerInvariant())))[..12].ToLowerInvariant();

    private static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
