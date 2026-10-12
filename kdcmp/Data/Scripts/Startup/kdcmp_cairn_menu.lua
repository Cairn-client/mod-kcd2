-- Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
-- GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
-- content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
--
-- Cairn module: the launcher draws the mod menu.
--
-- The mod menu (KCD2MP.w154menu, in kdcmp.lua) draws its own panel and takes the Insert key. The Cairn launcher can draw the same menu
-- over the game instead, in the look every game's mod shares, and take its keys. This file is that bridge, and only that: it adds a
-- request function and one log line to the menu and changes nothing else in it. It loads after kdcmp.lua ("kdcmp_" + "cairn_" sorts
-- after "kdcmp.lua") and does nothing at all when the menu is not there. Everything sits in one function, so it adds no locals to a
-- chunk it is joined to (the synthetic tests join it to kdcmp.lua).
--
-- The mod tells the launcher what the menu holds with one log line,
--   [KCD2-MP-EVT] v1 <seq> menu_state <game>~<title>~<blocked>~<note>|<item>|...
-- (an item is id~group~kind~on~locked~value~label~help~gate, kind = onoff|choice|key|action|info, on = 1|0|-, locked = 1|0), and the
-- launcher asks with KCD2MP_MenuRequest:
--   attach            the launcher draws it from now on (the mod's own panel and keys step aside)
--   detach            the mod's own panel again (also: mp_menu overlay off)
--   state             say the state now
--   do <id> <verb>    verb enter = do the item (flip a switch, run an action), nudge = change a value only

do local function install()
    -- kdcmp.lua's mp_log is local to that file: this module logs the same way, with its own tag
    local function log(msg) pcall(function() System.LogAlways("[CAIRN-MOD] " .. tostring(msg)) end) end

    local KCD2MP = KCD2MP
    local M = KCD2MP and KCD2MP.w154menu
    if not (M and M.ITEMS and KCD2MP_W154Menu and KCD2MP_EmitEvent) then
        log("CAIRN-MENU the mod menu is not there: the launcher cannot draw it")
        return
    end

    M.overlay = M.overlay == true
    M.CAIRN_CONTENT = M.CAIRN_CONTENT or { game = "Kingdom Come: Deliverance II", title = "Mod menu" }

    local KIND_WORD = { onoff = "onoff", crime = "choice", menukey = "key", voice = "onoff", action = "action", info = "info" }
    local function field(s) return (tostring(s or ""):gsub("[|~\r\n]", " ")) end

    function M.stateLine()
        local note = (M.note and (os.clock() - M.note.at) < 8) and M.note.text or ""
        local blocked = ""
        pcall(function() blocked = M.blockedWhy() or "" end)
        local parts = { table.concat({ field(M.CAIRN_CONTENT.game), field(M.CAIRN_CONTENT.title), field(blocked), field(note) }, "~") }
        for _, it in ipairs(M.ITEMS) do
            if not it.hidden or (it.showWhen and it.showWhen()) then
                local v, locked = M.valueOf(it)
                local on = "-"
                if it.kind == "onoff" or it.kind == "voice" then on = (v == true) and "1" or (v == false) and "0" or "-" end
                local shown = (it.kind ~= "action") and (M.shown(it, v) or "") or ""
                parts[#parts + 1] = table.concat({ field(it.id), field(it.group or ""), KIND_WORD[it.kind] or "info", on,
                    locked and "1" or "0", field(shown), field(it.label), field(it.help or ""), field(it.gate or "") }, "~")
            end
        end
        return table.concat(parts, "|")
    end

    function M.emitState() KCD2MP_EmitEvent("menu_state", M.stateLine()) end

    function KCD2MP_MenuRequest(arg)
        local verb, rest = tostring(arg or ""):match("^(%S+)%s*(.-)$")
        if verb == "attach" then
            M.overlay = true
            KCD2MP.launcherDraws = true
            if M.menu.open then M.close("the launcher draws the menu now") end
            log("CAIRN-MENU the launcher draws the menu from now on")
            M.emitState()
            return true
        elseif verb == "detach" then
            M.overlay = false
            KCD2MP.launcherDraws = false
            log("CAIRN-MENU the mod draws its own menu again")
            return true
        elseif verb == "state" then
            M.emitState()
            return true
        elseif verb == "do" then
            local id, how = rest:match("^(%S+)%s+(%S+)$")
            if not id or (how ~= "enter" and how ~= "nudge") then log("CAIRN-MENU request: bad '" .. tostring(rest) .. "'"); return false end
            local why = M.blockedWhy()
            if why then
                M.setNote("The menu cannot do that now (" .. why .. ").")
                M.emitState()
                return false
            end
            if not M.select(id) then M.emitState(); return false end
            local ok = M.change("overlay")   -- the mod's menu has one way to change a value; enter and nudge both use it
            M.emitState()
            return ok
        end
        log("CAIRN-MENU request: expected attach|detach|state|do <id> <enter|nudge>, got '" .. tostring(arg) .. "'")
        return false
    end

    -- The mod's own keys and console words step aside while the launcher draws the menu (the keys reach the dispatcher as verbs; only
    -- status and select stay: a console check must still work). "overlay" is the console way to attach or detach.
    local menuOrig = KCD2MP_W154Menu
    function KCD2MP_W154Menu(arg, src)
        local s = tostring(arg or ""):lower():gsub("^%s+", ""):gsub("%s+$", "")
        local verb, rest = s:match("^(%S+)%s*(.-)$")
        if verb == "overlay" then return KCD2MP_MenuRequest(rest == "off" and "detach" or "attach") end
        if M.overlay and verb and verb ~= "status" and verb ~= "select" then
            log("CAIRN-MENU " .. tostring(verb) .. " ignored: the launcher draws the menu (mp_menu overlay off for the mod's own)")
            return false
        end
        return menuOrig(arg, src)
    end

    log("CAIRN-MENU the launcher can draw the mod menu (KCD2MP_MenuRequest)")
end install() end
