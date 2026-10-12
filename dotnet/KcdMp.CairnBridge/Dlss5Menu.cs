// Copyright (C) 2026 the Cairn contributors (AUTHORS.md). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: AUTHORS.md. Unofficial, free, not affiliated with or endorsed by the studios or publishers
// of the games it supports. Portions derive from Kingdom Come: Together and kcd2-multiplayer (AUTHORS.md).
using System.Text.Json;
using System.Text.Json.Nodes;
using KcdMp.CairnBridge.Dlss5Support;

namespace KcdMp.CairnBridge;

/// <summary>
/// DLSS 5's row in the in-game menu and its submenu (OptiScaler's settings), which the launcher used to draw itself. Now the bridge owns them: it adds the
/// row to the mod's menu, and while the submenu is open it sends the submenu in place of the mod's own menu, marked <c>back</c> so the launcher sends Back to
/// it. The values are OptiScaler.ini's, read each time, so what the player sees is what the file holds.
/// </summary>
public sealed class Dlss5Menu
{
    public const string GameTitle = "Kingdom Come: Deliverance II";

    private readonly string _gameDir;
    private readonly object _gate = new();
    private string? _ini;
    private bool _inSubmenu;

    public Dlss5Menu(string gameDir) { _gameDir = gameDir; }

    public bool InSubmenu { get { lock (_gate) return _inSubmenu; } }

    /// <summary>
    /// DLSS 5 is in the game's folder (put there by Cairn, or OptiScaler is there by any hand). Looking for OptiScaler.ini walks the whole game folder, so the
    /// answer is kept for a minute: the menu is refreshed about once a second while it is open.
    /// </summary>
    public bool Active => Dlss5Service.IsInstalled(OptionsCli.GameId) || IniNearby() is not null;

    private string? _scanned;
    private DateTime _scannedAt = DateTime.MinValue;
    private string? IniNearby()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _scannedAt > TimeSpan.FromSeconds(60)) { _scanned = Dlss5.FindOptiScalerIni(_gameDir); _scannedAt = DateTime.UtcNow; }
            return _scanned;
        }
    }

    /// <summary>What goes to the launcher when the mod sends its menu: the same menu with DLSS 5's row added, or the submenu while it is open.</summary>
    public string OnModMenu(string menuJson)
    {
        lock (_gate)
        {
            if (_inSubmenu) return SubmenuJson();
            try
            {
                var node = JsonNode.Parse(menuJson) as JsonObject;
                if (node?["items"] is not JsonArray items) return menuJson;
                if (items.Any(i => i?["id"]?.GetValue<string>() == Dlss5.MenuItemId)) return menuJson;
                items.Add(ItemNode(Dlss5.MenuItem(inSubmenu: false, active: Active)));
                return node.ToJsonString();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return menuJson; }
        }
    }

    /// <summary>The player did something to a row. <c>Handled</c>: it was DLSS 5's, nothing goes to the game. <c>Send</c>: a message for the launcher. <c>AskMod</c>: the mod's own menu is wanted again.</summary>
    public (bool Handled, string? Send, bool AskMod) OnDo(string id, string verb, int dir)
    {
        lock (_gate)
        {
            if (!_inSubmenu)
            {
                if (id != Dlss5.MenuItemId) return (false, null, false);
                if (verb != "enter" || !Active) return (true, null, false);
                _ini = Dlss5.FindOptiScalerIni(_gameDir);
                if (!string.IsNullOrEmpty(_ini)) Dlss5.EnsureShortcutKeyDisabled(_ini);
                _inSubmenu = true;
                return (true, SubmenuJson(), false);
            }
            if (id == "dlss5_back") { if (verb == "enter") { _inSubmenu = false; return (true, null, true); } return (true, null, false); }
            _ini ??= Dlss5.FindOptiScalerIni(_gameDir);
            Dlss5.StepSetting(_ini, id, verb == "nudge" && dir < 0 ? -1 : 1);
            return (true, SubmenuJson(), false);
        }
    }

    /// <summary>The player pressed Back: out of the submenu, to the mod's menu. False when there is no submenu to leave (the launcher closes the menu itself then).</summary>
    public bool OnBack()
    {
        lock (_gate)
        {
            if (!_inSubmenu) return false;
            _inSubmenu = false;
            return true;
        }
    }

    /// <summary>The game ended or the bridge restarted: the submenu is not open.</summary>
    public void Reset() { lock (_gate) _inSubmenu = false; }

    private string SubmenuJson()
    {
        _ini ??= Dlss5.FindOptiScalerIni(_gameDir);
        var state = Dlss5.BuildSubmenu(GameTitle, _ini);
        var items = new JsonArray();
        foreach (var i in state.Items) items.Add(ItemNode(i));
        var node = new JsonObject { ["t"] = "menu", ["title"] = state.Title, ["blocked"] = state.Blocked, ["note"] = state.Note, ["back"] = true, ["items"] = items };
        return node.ToJsonString();
    }

    private static JsonObject ItemNode(MenuItem i)
    {
        var o = new JsonObject
        {
            ["id"] = i.Id,
            ["group"] = i.Group,
            ["kind"] = i.Kind switch { MenuKind.OnOff => "onoff", MenuKind.Choice => "choice", MenuKind.Key => "key", MenuKind.Action => "action", _ => "info" },
            ["locked"] = i.Locked,
            ["value"] = i.Value,
            ["label"] = i.Label,
            ["help"] = i.Help,
            ["disabled"] = i.Disabled,
        };
        if (i.On is bool on) o["on"] = on;
        if (i.Gate.Length > 0) o["gate"] = i.Gate;
        return o;
    }
}
