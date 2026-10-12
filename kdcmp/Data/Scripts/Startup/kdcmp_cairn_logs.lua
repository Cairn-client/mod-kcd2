-- Copyright (C) 2026 the Cairn contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
-- GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
-- content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
--
-- Cairn module: the host collects everyone's logs.
--
-- The host's mod menu gets one row, "Collect everyone's logs" (also the console command mp_collect_logs). It asks the agent
-- ("cairn_logs_collect" event); every player's agent zips their own logs and sends them to the host's agent, which saves them in
-- one folder (GameBridge.CairnLogs.cs). One switch for everyone: there is no separate choice for the joiner. Loads after kdcmp.lua and
-- does nothing when the mod menu is not there. Everything sits in one function (no locals added to a chunk it is joined to).

do local function install()
    local function log(msg) pcall(function() System.LogAlways("[CAIRN-MOD] " .. tostring(msg)) end) end

    local M = KCD2MP and KCD2MP.w154menu
    if not (M and M.ITEMS and KCD2MP_EmitEvent) then
        log("CAIRN-LOGS the mod menu is not there: no row for collecting the logs")
        return
    end

    -- what the agent says to the player about a collection (asked, saved, declined)
    function KCD2MP_CairnLogsNote(text)
        text = tostring(text or "")
        if text == "" then return false end
        log("CAIRN-LOGS " .. text)
        if M.setNote then M.setNote(text) end
        pcall(KCD2MP_ShowNativeToast, text)
        return true
    end

    -- the menu row's action, and the console command's
    function KCD2MP_CairnCollectLogs()
        if M.roleNow and M.roleNow() ~= "host" then
            KCD2MP_CairnLogsNote("Only the host can collect everyone's logs.")
            return false
        end
        KCD2MP_EmitEvent("cairn_logs_collect", "")
        return true
    end

    local have = false
    for _, it in ipairs(M.ITEMS) do if it.id == "collect_logs" then have = true end end
    if not have then
        local at = #M.ITEMS + 1
        for i, it in ipairs(M.ITEMS) do if it.id == "unstuck" then at = i end end
        table.insert(M.ITEMS, at, { id = "collect_logs", group = "Help", label = "Collect everyone's logs", kind = "action",
            cmd = "mp_collect_logs", fn = "KCD2MP_CairnCollectLogs",
            help = "Host only: asks every player for their logs (the helper's, the game's, the plugin's) and saves them together on your PC. Send them to the person who fixes the bug." })
    end

    if System and System.AddCCommand then
        pcall(System.AddCCommand, "mp_collect_logs", 'KCD2MP_CairnCollectLogs()',
            "(host) ask every player for their logs and save them together on this PC: mp_collect_logs")
    end
    log("CAIRN-LOGS the host can collect everyone's logs (menu row and mp_collect_logs)")
end install() end
