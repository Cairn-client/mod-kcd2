-- Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
-- GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
-- content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
--
-- Cairn module: the host's cutscene on the joiner's screen.
--
-- The quest graph runs only on the host, so the joiner used to stand in the world and walk about while the host watched. On the host's
-- Ingame/Rendered cutscene edge the agent calls KCD2MP_CairnCutsceneShare on the joiner, which (a) holds its own input with the
-- engine's "no_input" map (what a fader holds input with: the same hold the join uses), and (b) tries to play the scene: the
-- IngameCutsceneData entity of that name carries the Track View sequence (esSequenceName), started with Movie.PlaySequence. (b) is best
-- effort and logged either way (CAIRN-CUTSHARE). Released by the host's end edge, the partner leaving, or 240 s (the agent's timer).
-- mp_cutscene_share on|off (default on). Loads after kdcmp.lua; one function, no locals added to a chunk it is joined to.

do local function install()
    local function log(msg) pcall(function() System.LogAlways("[CAIRN-MOD] " .. tostring(msg)) end) end

    if not (KCD2MP and KCD2MP_JoinHold and KCD2MP.w123) then
        log("CAIRN-CUTSHARE the join hold is not there: the host's cutscenes are not shared")
        return
    end

    KCD2MP.csShare = KCD2MP.csShare or { enabled = true, held = false, seq = nil, name = nil, at = 0, method = nil }

    function KCD2MP_CairnCutsceneShare(active, name, ctype)
        local s = KCD2MP.csShare
        name = tostring(name or "")
        if active then
            if not s.enabled then return false end
            if s.held or s.seq or KCD2MP.cutsceneActive then
                log(string.format("CAIRN-CUTSHARE start %s ignored (%s)", name, (s.held or s.seq) and "already sharing one" or "this machine is in its own cutscene"))
                return false
            end
            local held, why = false, "join hold in use"
            if not KCD2MP.w123.holdOn then held, why = KCD2MP_JoinHold(true, "noinput") end
            s.held, s.name, s.at, s.method = held == true, name, os.clock(), held and "noinput" or nil
            -- the scene itself: the entity that names its sequence
            local seq, via = nil, "none"
            pcall(function()
                for _, e in ipairs(System.GetEntitiesByClass("IngameCutsceneData") or {}) do
                    local n = nil
                    pcall(function() n = e:GetName() end)
                    if n and (n == name or string.find(n, name, 1, true)) and e.Properties and (e.Properties.esSequenceName or "") ~= "" then
                        seq, via = e.Properties.esSequenceName, "entity " .. n
                        break
                    end
                end
            end)
            local played = "no Movie.PlaySequence"
            if Movie and Movie.PlaySequence then
                local cands = {}
                if seq then cands[#cands + 1] = seq end
                cands[#cands + 1] = name
                for _, c in ipairs(cands) do
                    local ok, r = pcall(Movie.PlaySequence, c)
                    played = string.format("%s -> ok=%s ret=%s", c, tostring(ok), tostring(r))
                    if ok and r ~= false then s.seq = c; break end
                end
            end
            log(string.format("CAIRN-CUTSHARE start type=%s name=%s hold=%s(%s) sequence=%s play: %s",
                tostring(ctype), name, tostring(s.held), tostring(why), via, played))
            return s.held or s.seq ~= nil
        end
        if not s.held and not s.seq then return false end
        if s.seq and Movie and Movie.StopSequence then pcall(Movie.StopSequence, s.seq) end
        if s.held then KCD2MP_JoinHold(false, s.method) end
        log(string.format("CAIRN-CUTSHARE end name=%s after %.1f s (hold released=%s sequence stopped=%s)",
            tostring(s.name), os.clock() - (s.at or os.clock()), tostring(s.held), tostring(s.seq ~= nil)))
        s.held, s.seq, s.name, s.method = false, nil, nil, nil
        return true
    end

    -- mp_cutscene_share on|off (default on): the joiner follows the host's cutscenes; bare = report.
    function KCD2MP_CairnSetCutsceneShare(arg)
        local s = KCD2MP.csShare
        local v = tostring(arg or ""):gsub("^%s+", ""):gsub("%s+$", "")
        if v == "on" then s.enabled = true elseif v == "off" then s.enabled = false; KCD2MP_CairnCutsceneShare(false, "") end
        log(string.format("CAIRN-CUTSHARE share=%s -- the joiner holds its input (and plays the scene) while the host's cutscene runs", s.enabled and "on" or "off"))
        return true
    end

    if System and System.AddCCommand then
        pcall(System.AddCCommand, "mp_cutscene_share", 'KCD2MP_CairnSetCutsceneShare(%line)',
            "(joiner) hold your input and follow the host's cutscenes (default on): mp_cutscene_share on|off")
    end
    log("CAIRN-CUTSHARE the host's cutscenes are shared with the joiner")
end install() end
