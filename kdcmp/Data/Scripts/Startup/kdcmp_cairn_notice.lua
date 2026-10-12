-- Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
-- GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
-- content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
--
-- Cairn module: the mod's notices, drawn by the launcher.
--
-- While the launcher draws the in-game menu (KCD2MP.launcherDraws, set by kdcmp_cairn_menu.lua), the lines the mod used to draw on
-- the game's screen -- the rows at the top left (a partner joining, the sleep question with its keys, an invite, a quest prompt) and
-- the game-style toasts in the middle -- are collected here and sent to the launcher as one notice,
--   [KCD2-MP-EVT] v1 <seq> notice <line>^^<line>...        (and notice_hide when nothing is left)
-- which it draws as a card in the menu's look at the top left. kdcmp.lua hands its rows (mp_draw_row) and toasts
-- (KCD2MP_ShowNativeToast) to KCD2MP_CairnRow / KCD2MP_CairnNotice, marked CAIRN:. Without the launcher nothing changes: both return false.
-- Everything sits in one function, so it adds no locals to a chunk it is joined to.

do local function install()
    local KCD2MP = KCD2MP
    if not (KCD2MP and KCD2MP_EmitEvent) then return end

    local N = { rows = {}, toasts = {}, last = "", lastAt = 0 }
    KCD2MP.cairnNotice = N

    -- plain words for a card: the console hints go (the card shows the keys), no line breaks, and the line separator is ours alone
    local function clean(s)
        s = tostring(s or "")
        s = s:gsub("%s*%(or mp_[^)]*%)", ""):gsub("%s*%(mp_[%w_]+[^)]*%)", "")
        s = s:gsub("[\r\n]+", " "):gsub("%^%^", " ")
        return (s:gsub("^%s+", ""):gsub("%s+$", ""))
    end

    function KCD2MP_CairnNotice(kind, text, secs)
        if not KCD2MP.launcherDraws then return false end
        text = clean(text)
        if text == "" then return true end
        for i = #N.toasts, 1, -1 do if N.toasts[i].text == text then table.remove(N.toasts, i) end end
        N.toasts[#N.toasts + 1] = { text = text, expires = os.clock() + (tonumber(secs) or 5) }
        return true
    end

    function KCD2MP_CairnRow(key, y, text)
        if not KCD2MP.launcherDraws then return false end
        N.rows[tostring(key)] = { y = tonumber(y) or 0, text = clean(text), key = tostring(key) }
        return true
    end

    -- once a frame, after the rows were drawn: what is on screen now, as one notice when it changed
    local function flush()
        local now = os.clock()
        local lines = {}
        for i = #N.toasts, 1, -1 do if N.toasts[i].expires < now then table.remove(N.toasts, i) end end
        for _, t in ipairs(N.toasts) do lines[#lines + 1] = t.text end
        local rows = {}
        for _, r in pairs(N.rows) do if r.text ~= "" then rows[#rows + 1] = r end end
        N.rows = {}
        table.sort(rows, function(a, b) if a.y ~= b.y then return a.y < b.y end return a.key < b.key end)
        for _, r in ipairs(rows) do lines[#lines + 1] = r.text end
        local text = table.concat(lines, "^^")
        if #text > 380 then text = text:sub(1, 380) end
        if text ~= N.last and (now - N.lastAt) >= 0.2 then
            N.last, N.lastAt = text, now
            if text == "" then KCD2MP_EmitEvent("notice_hide", "") else KCD2MP_EmitEvent("notice", text) end
        end
    end
    N.flush = flush

    -- the frame loop calls this once a frame, after every row (kdcmp.lua: KCD2MP_W154MenuBackstop)
    local backstopOrig = KCD2MP_W154MenuBackstop
    function KCD2MP_W154MenuBackstop(...)
        local r = backstopOrig and backstopOrig(...)
        pcall(flush)
        return r
    end
end install() end