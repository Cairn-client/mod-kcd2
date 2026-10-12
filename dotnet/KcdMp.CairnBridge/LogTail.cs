// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KcdMp.CairnBridge;

/// <summary>
/// Follows the kcd.log of the game that was just started. The engine starts a new kcd.log at each game start; until the file in place
/// is this game's ("Log Started at" no earlier than the start, less a slack), nothing is read from it: the previous game's log would
/// replay its menu.
/// </summary>
public sealed class LogTail
{
    public static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(30);
    private static readonly Regex Header = new(@"Log Started at (\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2}:\d{2})", RegexOptions.CultureInvariant);

    private readonly string _gameDir;
    private readonly DateTime _gameStartLocal;
    private string? _path;
    private long _offset;
    private bool _ours;
    private readonly StringBuilder _partial = new();
    private string? _tailFed;
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();

    public LogTail(string gameDir, DateTime gameStartLocal) { _gameDir = gameDir; _gameStartLocal = gameStartLocal; }

    /// <summary>kcd.log in the game's folder or one of its parents (the game's exe sits a few folders below it).</summary>
    public static string? FindLog(string gameDir)
    {
        string dir = gameDir;
        for (int i = 0; i < 4 && !string.IsNullOrWhiteSpace(dir); i++)
        {
            string candidate = Path.Combine(dir, "kcd.log");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir) ?? "";
        }
        return null;
    }

    public static DateTime? StartedAt(string head)
    {
        var m = Header.Match(head);
        return m.Success && DateTime.TryParseExact($"{m.Groups[1].Value} {m.Groups[2].Value}", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t : null;
    }

    /// <summary>Reads what the game added since the last poll and gives each line to <paramref name="onLine"/>. Never throws; an unreadable file is tried again next time.</summary>
    public void Poll(Action<string> onLine)
    {
        try
        {
            _path ??= FindLog(_gameDir);
            if (_path is null) return;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _offset) Restart();   // a new file: the engine started over
            if (!_ours)
            {
                var head = new byte[(int)Math.Min(fs.Length, 8192)];
                int got = fs.Read(head, 0, head.Length);
                var started = StartedAt(Encoding.UTF8.GetString(head, 0, got));
                if (started is null || started.Value < _gameStartLocal - StartSlack) return;   // not this game's (yet)
                _ours = true;
                _offset = 0;
            }
            fs.Seek(_offset, SeekOrigin.Begin);
            var buf = new byte[64 * 1024];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(buf.Length)];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                _offset += n;
                int c = _utf8.GetChars(buf, 0, n, chars, 0);
                for (int i = 0; i < c; i++)
                {
                    if (chars[i] == '\n')
                    {
                        string line = _partial.ToString().TrimEnd('\r');
                        if (line != _tailFed) onLine(line);
                        _tailFed = null;
                        _partial.Clear();
                    }
                    else _partial.Append(chars[i]);
                }
            }
            // the engine writes a line's end only when it writes the next one: the last line is read as it stands, once
            string tail = _partial.ToString().TrimEnd('\r');
            if (tail.Length > 0 && tail != _tailFed) { onLine(tail); _tailFed = tail; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Restart()
    {
        _ours = false;
        _offset = 0;
        _partial.Clear();
        _tailFed = null;
        _utf8.Reset();
    }
}
