// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Globalization;

namespace KcdMp.Client;

public partial class GameBridge
{
    // ================================================================ WO-171 M: the partner's own colour and facing on the map

    private volatile Wo171Rules.Style _w171Style = Wo171Rules.Style.Rotating;   // mp_partner_marker_style
    private volatile int _w171Offset;                                          // mp_marker_dir_offset 0..7
    private volatile bool _w171Flip;                                           // mp_marker_dir_flip
    private readonly Wo171Rules.SlotTable _w171Slots = new();

    private sealed class W171Pin
    {
        public readonly Wo171Rules.MoveGate Gate = new();
        public int Slot, RawOctant = -1, SentCode = -1, Octant;
        public long LastTurnMs = long.MinValue / 2, LastSendMs = long.MinValue / 2, Moves;
        public float X, Y, Z;
        public string Name = "player";
    }

    private readonly ConcurrentDictionary<byte, W171Pin> _w171Pins = new();
    private long _w171Turns, _w171MoveSends, _w171Heartbeats, _w171Adds, _w171Removes;

    /// <summary>The Lua's <c>w171_cfg style=rotating|flat|companion offset=0..7 flip=on|off</c> (any subset).</summary>
    private void Wo171OnCfg(string? arg)
    {
        foreach (var part in (arg ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1) continue;
            string k = part[..eq], v = part[(eq + 1)..];
            if (k == "style" && Wo171Rules.TryParseStyle(v, out var st)) _w171Style = st;
            else if (k == "offset" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int off) && off is >= 0 and <= 7) _w171Offset = off;
            else if (k == "flip" && v is "on" or "off") _w171Flip = v == "on";
        }
        Console.WriteLine($"WO171-CFG marker style={Wo171Rules.StyleWord(_w171Style)} offset={_w171Offset} flip={(_w171Flip ? "on" : "off")}");
    }

    /// <summary>This player's id of the host, when this machine is a joiner (the host is always slot 1, red).</summary>
    private byte Wo171HostId() => _leashHostId != 0xFF ? _leashHostId : _hostModeFrom;

    /// <summary>One partner's pin this second: sets it, moves it, turns it or just keeps it alive.</summary>
    private async Task W171PinOneAsync(byte g, float x, float y, float z, long nowMs)
    {
        var st = _w171Pins.GetOrAdd(g, _ => new W171Pin());
        string? who = _ghostNames.TryGetValue(g, out var nm) ? nm : null;
        st.Name = Wo171Rules.SanitizeLabel(who);
        string identity = who is { Length: > 0 } ? who : "id" + g.ToString(CultureInfo.InvariantCulture);
        bool isHost = !W137Host && g == Wo171HostId();
        int slot = _w171Slots.Slot(identity, isHost);
        float yaw = _ghostLastYaw.TryGetValue(g, out var yv) ? yv : 0f;   // a mounted partner's stream is the horse's too (the saddle turns with it)
        st.RawOctant = Wo171Rules.PickOctant(st.RawOctant, Wo171Rules.HeadingDeg(yaw));
        int oct = Wo171Rules.ApplyOffsetFlip(st.RawOctant, _w171Offset, _w171Flip);
        int code = Wo171Rules.CodeFor(_w171Style, slot, oct, _w171Offset);

        bool first = st.SentCode < 0;
        bool moveDue = first || st.Gate.Due(x, y, nowMs);
        bool turnDue = !first && Wo171Rules.TurnDue(st.SentCode, code, st.LastTurnMs, nowMs);
        bool beat = !first && nowMs - st.LastSendMs >= 5000;
        if (!moveDue && !turnDue && !beat) return;

        int sendCode = first || turnDue ? code : st.SentCode;
        float sx = moveDue ? x : st.X, sy = moveDue ? y : st.Y, sz = moveDue ? z : st.Z;
        if (!await _combat.MirrorGraveAsync(3, g, (ulong)sendCode, sx, sy, sz)) return;
        Interlocked.Increment(ref _w164PinSets);
        _w164PinAt[g] = (sx, sy);
        st.LastSendMs = nowMs;
        st.Slot = slot;
        string colour = slot is >= 1 and <= Wo171Rules.Colours ? Wo171Rules.ColourNames[slot - 1] : "none";
        string dir = Wo171Rules.Compass[oct];
        if (first)
        {
            st.SentCode = sendCode; st.LastTurnMs = nowMs; st.X = sx; st.Y = sy; st.Z = sz; st.Octant = oct;
            st.Gate.Sent(sx, sy, nowMs, counted: false);
            Interlocked.Increment(ref _w171Adds);
            Console.WriteLine(FormattableString.Invariant($"WO171-MARK slot={slot} colour={colour} dir={dir} name=\"{st.Name}\" picture={Wo171Rules.PictureName(sendCode)} style={Wo171Rules.StyleWord(_w171Style)} added"));
            return;
        }
        if (moveDue)
        {
            st.X = sx; st.Y = sy; st.Z = sz;
            st.Gate.Sent(sx, sy, nowMs, counted: true);
            Interlocked.Increment(ref _w171MoveSends);
            if (++st.Moves % 20 == 1)
                Console.WriteLine(FormattableString.Invariant($"WO171-MARK slot={slot} colour={colour} dir={dir} moved (move {st.Moves}; {st.Gate.InLastMinute(nowMs)} in the last minute, cap {Wo171Rules.MoveCapPerMinute}; one line per 20)"));
        }
        if (turnDue)
        {
            st.SentCode = sendCode; st.LastTurnMs = nowMs; st.Octant = oct;
            Interlocked.Increment(ref _w171Turns);
            Console.WriteLine(FormattableString.Invariant($"WO171-MARK slot={slot} colour={colour} dir={dir} picture={Wo171Rules.PictureName(sendCode)} turned"));
        }
        else if (!moveDue) Interlocked.Increment(ref _w171Heartbeats);
    }

    private void W171Removed(byte g)
    {
        if (!_w171Pins.TryRemove(g, out var st)) return;
        Interlocked.Increment(ref _w171Removes);
        string colour = st.Slot is >= 1 and <= Wo171Rules.Colours ? Wo171Rules.ColourNames[st.Slot - 1] : "none";
        Console.WriteLine($"WO171-MARK slot={st.Slot} colour={colour} dir={Wo171Rules.Compass[st.Octant & 7]} name=\"{st.Name}\" removed");
    }

    private string Wo171StatsText() => FormattableString.Invariant(
        $"style={Wo171Rules.StyleWord(_w171Style)} offset={_w171Offset} flip={(_w171Flip ? "on" : "off")} added={Interlocked.Read(ref _w171Adds)} turns={Interlocked.Read(ref _w171Turns)} move_sends={Interlocked.Read(ref _w171MoveSends)} heartbeats={Interlocked.Read(ref _w171Heartbeats)} removed={Interlocked.Read(ref _w171Removes)} slots={_w171Slots.Count}");
}
