// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Net;

namespace KcdMp.CairnBridge;

/// <summary>The game's console through the Modding Tools' local API (localhost:1403). Lua is sent with the console's '#' prefix; the game answers 503 while it is busy, so a call is retried briefly.</summary>
public sealed class GameConsole : IDisposable
{
    /// <summary>The console's own limit (past about 2,100 encoded characters the rest is cut, silently).</summary>
    public const int MaxEncoded = 1900;

    /// <summary>The console of the game; CAIRN_CONSOLE_URL points it elsewhere (the tests' stand-in for the game).</summary>
    private readonly HttpClient _http = new() { BaseAddress = new Uri(Environment.GetEnvironmentVariable("CAIRN_CONSOLE_URL") is { Length: > 0 } u && Uri.TryCreate(u, UriKind.Absolute, out var parsed) && parsed.IsLoopback ? u : "http://127.0.0.1:1403"), Timeout = TimeSpan.FromSeconds(5) };

    public Task<bool> LuaAsync(string lua, CancellationToken ct = default) => CommandAsync("#" + lua, ct);

    /// <summary>A console command as it is (no Lua prefix).</summary>
    public async Task<bool> CommandAsync(string command, CancellationToken ct = default)
    {
        string enc = Uri.EscapeDataString(command);
        if (enc.Length > MaxEncoded) return false;
        for (int attempt = 0; attempt < 8 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                using var r = await _http.GetAsync("/api/System/Console/ExecuteString?command=" + enc, ct);
                if (r.StatusCode == HttpStatusCode.ServiceUnavailable) { await Task.Delay(50 + 50 * attempt, ct); continue; }
                return r.IsSuccessStatusCode;
            }
            catch (HttpRequestException) { return false; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
        }
        return false;
    }

    public void Dispose() => _http.Dispose();
}
