// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KcdMp.CairnBridge;

/// <summary>
/// The mod's event lines in kcd.log (<c>[KCD2-MP-EVT] v1 &lt;seq&gt; menu_state ...</c>, <c>notice</c>, <c>notice_hide</c>, <c>subtitle</c>)
/// as the launcher's bridge protocol says them (one JSON object per line). Everything else in the log is ignored.
/// </summary>
public static class Translator
{
    public const char ItemSep = '|';
    public const char FieldSep = '~';

    /// <summary>The bridge message for one log line, or null when the line is not one of the mod's menu events.</summary>
    public static string? ToBridge(string line, string eventPrefix)
    {
        int at = line.IndexOf(eventPrefix, StringComparison.Ordinal);
        if (at < 0) return null;
        string rest = line[(at + eventPrefix.Length)..];
        int s1 = rest.IndexOf(' ');
        if (s1 < 0 || !long.TryParse(rest.AsSpan(0, s1), NumberStyles.None, CultureInfo.InvariantCulture, out _)) return null;
        rest = rest[(s1 + 1)..].TrimEnd();

        if (rest == "notice_hide") return Json(w => w.WriteString("t", "notice_hide"));
        if (rest.StartsWith("notice ", StringComparison.Ordinal))
        {
            string text = rest[7..].Trim();
            return text.Length == 0 ? null : Json(w => { w.WriteString("t", "notice"); w.WriteString("text", text); });
        }
        if (rest.StartsWith("subtitle ", StringComparison.Ordinal))
        {
            string payload = rest[9..];
            int sep = payload.IndexOf(FieldSep);
            if (sep <= 0 || !double.TryParse(payload.AsSpan(0, sep), NumberStyles.Float, CultureInfo.InvariantCulture, out double secs)) return null;
            string text = payload[(sep + 1)..].Trim();
            return text.Length == 0 ? null : Json(w => { w.WriteString("t", "subtitle"); w.WriteNumber("seconds", secs); w.WriteString("text", text); });
        }
        if (rest.StartsWith("menu_state ", StringComparison.Ordinal)) return Menu(rest["menu_state ".Length..]);
        return null;
    }

    // <game>~<title>~<blocked>~<note>|id~group~kind~on~locked~value~label~help~gate|...
    private static string? Menu(string payload)
    {
        var parts = payload.Split(ItemSep);
        var head = parts[0].Split(FieldSep);
        if (head.Length < 4) return null;
        return Json(w =>
        {
            w.WriteString("t", "menu");
            w.WriteString("title", head[1]);
            w.WriteString("blocked", head[2]);
            w.WriteString("note", head[3]);
            w.WriteStartArray("items");
            for (int i = 1; i < parts.Length; i++)
            {
                var f = parts[i].Split(FieldSep);
                if (f.Length < 8 || f[0].Length == 0) continue;   // a damaged item is left out, not the whole menu
                w.WriteStartObject();
                w.WriteString("id", f[0]);
                w.WriteString("group", f[1]);
                w.WriteString("kind", f[2]);
                if (f[3] == "1") w.WriteBoolean("on", true); else if (f[3] == "0") w.WriteBoolean("on", false);
                w.WriteBoolean("locked", f[4] == "1");
                w.WriteString("value", f[5]);
                w.WriteString("label", f[6]);
                w.WriteString("help", f[7]);
                if (f.Length > 8) w.WriteString("gate", f[8]);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    /// <summary>The Lua the console runs for a launcher message: a guarded call of the mod's menu function. Null for a message that asks nothing of the mod.</summary>
    public static string? ToLua(string function, JsonElement message)
    {
        if (!message.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return null;
        string? arg = t.GetString() switch
        {
            "attach" => "attach",
            "state" => "state",
            "do" when Plain(message, "id") is { } id && message.TryGetProperty("verb", out var v) && v.GetString() is "enter" or "nudge"
                      => $"do {id} {v.GetString()}",
            _ => null,
        };
        return arg is null ? null : $"if {function} then {function}(\"{arg}\") end";
    }

    private static string? Plain(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= 40 } s
        && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? s : null;

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
