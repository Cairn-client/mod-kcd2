
-- Part 2: the Cairn module that lets the launcher draw the mod menu (kdcmp_cairn_menu.lua, joined to kdcmp.lua by the driver).

local RESULTS = {}
local function check(name, ok, detail)
    RESULTS[#RESULTS + 1] = (ok and "PASS  " or "FAIL  ") .. name .. (detail and ("  [" .. tostring(detail) .. "]") or "")
end
local function logCount(pat, from)
    local n = 0
    for i = (from or 0) + 1, #LOG do if string.find(LOG[i], pat, 1, true) then n = n + 1 end end
    return n
end
local function evts(name, from)
    local out = {}
    for i = (from or 0) + 1, #LOG do
        local a = LOG[i]:match("%[KCD2%-MP%-EVT%] v1 %d+ " .. name:gsub("_", "%%_") .. " (.*)$")
        if a then out[#out + 1] = a end
    end
    return out
end
local function frame(dt)
    NOW = NOW + (dt or 0.016)
    local due = TIMERS
    TIMERS = {}
    for _, t in ipairs(due) do
        if NOW - t.at >= (t.ms / 1000) - 1e-6 then pcall(t.f) else TIMERS[#TIMERS + 1] = t end
    end
end
local function key(action)
    Player.OnAction(nil, action, "press", 1)
    Player.OnAction(nil, action, "release", 0)
end

local M = KCD2MP.w154menu
local function session(role, partner, ft, crime, leash)
    KCD2MP_W154MenuSession(role, partner, ft, crime, leash, "connected", "0.1.0", false, false)
end
local function itemsOf(line)
    local out = {}
    local first = true
    for part in (line .. "|"):gmatch("([^|]*)|") do
        if first then first = false else out[#out + 1] = part end
    end
    return out
end
local function fields(s)
    local out = {}
    for f in (s .. "~"):gmatch("([^~]*)~") do out[#out + 1] = f end
    return out
end

do
    frame(1)
    check("a: the module loaded and the request function exists", type(KCD2MP_MenuRequest) == "function")
    check("a: its load line is logged", logCount("CAIRN-MENU the launcher can draw the mod menu") == 1)
    check("a: the mod's own dispatcher is still the menu's (wrapped, not replaced)", type(KCD2MP_W154Menu) == "function" and M.overlay == false)
end

do
    local mark = #LOG
    check("b: state is answered", KCD2MP_MenuRequest("state") == true)
    local e = evts("menu_state", mark)
    check("b: exactly one menu_state line", #e == 1, #e)
    local line = e[1] or ""
    local head = fields((line:match("^([^|]*)")) or "")
    check("b: the header names the game and the menu", head[1] == "Kingdom Come: Deliverance II" and head[2] == "Mod menu", line:sub(1, 60))
    local items = itemsOf(line)
    local hiddenN = 0
    for _ in pairs(M.CAIRN_HIDDEN) do hiddenN = hiddenN + 1 end
    check("b: every item of the mod menu is there, but the eight the launcher leaves out", hiddenN == 8 and #items == #M.ITEMS - hiddenN and #items >= 10, #items)
    local ok9, kinds = true, {}
    for _, it in ipairs(items) do
        local f = fields(it)
        if #f ~= 9 then ok9 = false end
        kinds[f[3] or "?"] = true
    end
    check("b: every item has its nine fields", ok9)
    check("b: the kinds are the launcher's words", kinds.onoff and kinds.action and kinds.info and kinds.choice and not kinds.key and not kinds.voice, next(kinds))
    local byId = {}
    for _, it in ipairs(items) do local f = fields(it); byId[f[1]] = f end
    local gone = true
    for _, id in ipairs({ "clean_screen", "partner_marker", "sleep_vote", "whistle", "partner_herbs", "voice", "menu_key", "report" }) do if byId[id] ~= nil then gone = false end end
    check("b: the eight left-out rows are not sent, the rest are", gone and byId.friendly_fire ~= nil and byId.crime_mode ~= nil and byId.name_badges ~= nil)
    check("b: there is no teleport-the-partner item and no F3 item", byId.bring_partner == nil and byId.noclip == nil)
    check("b: a switch reads on/off in field 4", byId.name_badges[4] == "1", byId.name_badges[4])
    check("b: no Lua errors", #ERRS == 0, ERRS[1])
end

do
    local mark = #LOG
    check("c: attach", KCD2MP_MenuRequest("attach") == true and M.overlay == true and KCD2MP.launcherDraws == true)
    check("c: attach says the state at once", #evts("menu_state", mark) == 1)
    session("host", true, true, "joint", true)
    local opened = KCD2MP_W154Menu("toggle", "key")
    check("c: the mod's own key does not open its panel while the launcher draws", opened == false and M.menu.open == false)
    check("c: the ignored key is logged", logCount("CAIRN-MENU toggle ignored") == 1)
    check("c: status still answers", KCD2MP_W154Menu("status") == true)
    check("c: the console's overlay off hands the menu back", KCD2MP_W154Menu("overlay off") == true and M.overlay == false)
    check("c: with it handed back the key opens the mod's panel", KCD2MP_W154Menu("toggle", "key") == true and M.menu.open == true)
    KCD2MP_W154Menu("close")
    check("c: attach again through the console", KCD2MP_W154Menu("overlay on") == true and M.overlay == true)
    check("c: no Lua errors", #ERRS == 0, ERRS[1])
end

do
    local mark = #LOG
    local before = M.nameBadges
    check("d: do <item> enter flips a switch", KCD2MP_MenuRequest("do name_badges enter") == true and M.nameBadges == (not before))
    local e = evts("menu_state", mark)
    check("d: and the new state is told", #e >= 1 and e[#e]:find("name_badges~Display~onoff~" .. (before and "0" or "1"), 1, true) ~= nil, e[#e])
    check("d: nudge works on a switch the same way", KCD2MP_MenuRequest("do name_badges nudge") == true and M.nameBadges == before)
    check("d: a request for an item that does not exist is refused", KCD2MP_MenuRequest("do nothing_here enter") == false)
    check("d: a malformed request is refused and logged", KCD2MP_MenuRequest("do name_badges") == false and logCount("CAIRN-MENU request: bad") == 1)
    check("d: an unknown verb is refused and logged", KCD2MP_MenuRequest("dance") == false and logCount("CAIRN-MENU request: expected") == 1)
    check("d: no Lua errors", #ERRS == 0, ERRS[1])
end

do
    session("joiner", true, true, "joint", true)
    local mark = #LOG
    local ff = M.ITEMS[1]
    for _, it in ipairs(M.ITEMS) do if it.id == "friendly_fire" then ff = it end end
    local was = ff.get()
    KCD2MP_MenuRequest("do friendly_fire enter")
    check("e: a joiner cannot change the host's lever through the launcher either", ff.get() == was)
    local line = evts("menu_state", mark)
    local last = line[#line] or ""
    check("e: the host's lever is told as locked", last:find("friendly_fire~Gameplay~onoff~", 1, true) ~= nil and last:match("friendly_fire~Gameplay~onoff~[01%-]~1~") ~= nil, last:match("friendly_fire[^|]*"))
    check("e: no Lua errors", #ERRS == 0, ERRS[1])
end

do
    check("f: detach", KCD2MP_MenuRequest("detach") == true and M.overlay == false and KCD2MP.launcherDraws == false)
end

do
    local mark = #LOG
    local it
    for _, x in ipairs(M.ITEMS) do if x.id == "collect_logs" then it = x end end
    check("g: the host's menu has a row for collecting everyone's logs", it ~= nil and it.kind == "action" and it.group == "Help")
    check("g: and it is the console command's function too", it ~= nil and it.fn == "KCD2MP_CairnCollectLogs" and CCMDS.mp_collect_logs ~= nil
        and CCMDS.mp_collect_logs.body:find("KCD2MP_CairnCollectLogs", 1, true) ~= nil)
    check("g: there is no separate row for the joiner to share or hold back their logs", (function() for _, x in ipairs(M.ITEMS) do if x.id == "share_logs" then return false end end return true end)())
    session("joiner", true, true, "joint", true)
    check("g: a joiner cannot collect", KCD2MP_CairnCollectLogs() == false and #evts("cairn_logs_collect", mark) == 0)
    session("host", true, true, "joint", true)
    check("g: the host's asks the agent once", KCD2MP_CairnCollectLogs() == true and #evts("cairn_logs_collect", mark) == 1)
    check("g: the agent's note is shown to the player", KCD2MP_CairnLogsNote("Asked 1 player(s) for their logs") == true and M.note ~= nil and M.note.text:find("Asked 1", 1, true) ~= nil)
    check("g: an empty note says nothing", KCD2MP_CairnLogsNote("") == false)
    check("g: no Lua errors", #ERRS == 0, ERRS[1])
end

do
    MOVIE = { played = {}, stopped = {} }
    Movie = { PlaySequence = function(n) MOVIE.played[#MOVIE.played + 1] = n; return true end,
              StopSequence = function(n) MOVIE.stopped[#MOVIE.stopped + 1] = n end }
    local s = KCD2MP.csShare
    check("h: the cutscene module loaded (its state and its functions exist)", s ~= nil and s.enabled == true and type(KCD2MP_CairnCutsceneShare) == "function"
        and CCMDS.mp_cutscene_share ~= nil and CCMDS.mp_cutscene_share.body:find("KCD2MP_CairnSetCutsceneShare", 1, true) ~= nil)
    check("h: the host's cutscene starts the scene on the joiner", KCD2MP_CairnCutsceneShare(true, "cs_test", "Ingame") == true and MOVIE.played[1] == "cs_test" and s.seq == "cs_test")
    check("h: a second edge while sharing one is ignored", KCD2MP_CairnCutsceneShare(true, "cs_other", "Ingame") == false and #MOVIE.played == 1)
    check("h: the end edge stops the scene and lets go", KCD2MP_CairnCutsceneShare(false, "") == true and MOVIE.stopped[1] == "cs_test" and s.seq == nil and s.held == false)
    check("h: an end edge with nothing running does nothing", KCD2MP_CairnCutsceneShare(false, "") == false)
    KCD2MP.cutsceneActive = true
    check("h: a machine in its own cutscene does not start the host's", KCD2MP_CairnCutsceneShare(true, "cs_own", "Rendered") == false and #MOVIE.played == 1)
    KCD2MP.cutsceneActive = false
    KCD2MP_CairnSetCutsceneShare("off")
    check("h: switched off, nothing is shared", s.enabled == false and KCD2MP_CairnCutsceneShare(true, "cs_off", "Ingame") == false and #MOVIE.played == 1)
    KCD2MP_CairnSetCutsceneShare("on")
    check("h: and back on", s.enabled == true)
    check("h: no Lua errors", #ERRS == 0, ERRS[1])
end

do
    -- the mod's on-screen lines go to the launcher as one notice
    local N = KCD2MP.cairnNotice
    check("i: the notice module loaded", N ~= nil and type(KCD2MP_CairnRow) == "function" and type(KCD2MP_CairnNotice) == "function")
    KCD2MP.launcherDraws = false
    check("i: while the mod draws itself, rows and toasts are left to it", KCD2MP_CairnRow("msg", 110, "x") == false and KCD2MP_CairnNotice("toast", "x", 5) == false)
    KCD2MP.launcherDraws = true
    local mark = #LOG
    check("i: while the launcher draws, a row is taken", KCD2MP_CairnRow("w140_prompt_keys", 262, "F11 yes / F12 no  (or mp_sleep_yes / mp_sleep_no)") == true)
    KCD2MP_CairnRow("w140_prompt", 236, "Sleep until morning? (23s)")
    check("i: and a toast", KCD2MP_CairnNotice("toast", "Your logs are saved", 6) == true)
    N.lastAt = -1; KCD2MP_W154MenuBackstop()
    local e = evts("notice", mark)
    check("i: one notice, the toast first, then the rows from the top, console hints cut", #e == 1 and e[1] == "Your logs are saved^^Sleep until morning? (23s)^^F11 yes / F12 no", e[1])
    N.lastAt = -1; N.toasts = {}; KCD2MP_W154MenuBackstop()
    local h = evts("notice_hide", mark)
    check("i: when nothing is left the notice is hidden", #h == 1)
    N.lastAt = -1; KCD2MP_W154MenuBackstop()
    check("i: and said once", #evts("notice_hide", mark) == 1)
    KCD2MP.launcherDraws = false
    check("i: no Lua errors", #ERRS == 0, ERRS[1])
end

do
    -- a partner joining: the host's join panel and the old bar are one short line, who it is
    local N = KCD2MP.cairnNotice
    KCD2MP.launcherDraws = true
    N.toasts, N.rows, N.join = {}, {}, nil
    local mark = #LOG
    local ret = KCD2MP_W154JoinPanel("ZipetteBabinks", "loading", 10, 3)
    check("j: the panel is taken (the game's own card is not pushed)", ret == true and N.join ~= nil and KCD2MP.w154.joinPanelOn == true)
    N.lastAt = -1; KCD2MP_W154MenuBackstop()
    local e = evts("notice", mark)
    check("j: the card says who joins and nothing more", #e == 1 and e[1] == "ZipetteBabinks is joining...", e[1])
    -- the old bar's rows: only its title, shortened to the same line; its ladder and hint stay out
    KCD2MP_W154JoinPanelHide("test")
    check("j: hiding the panel ends the line", N.join == nil)
    N.lastAt = -1; KCD2MP_W154MenuBackstop()   -- the card goes (notice_hide) before the old bar shows its own
    mark = #LOG
    KCD2MP_CairnRow("join_title", 480, "ZipetteBabinks is loading your world... 12 s")
    KCD2MP_CairnRow("join_bar", 520, "[x] save   [x] send   [>] load 12 s   [ ] ready")
    KCD2MP_CairnRow("join_hint", 556, "The world is paused until they arrive (mp_join_cancel to stop).")
    N.lastAt = -1; KCD2MP_W154MenuBackstop()
    e = evts("notice", mark)
    check("j: the old bar is the same line: its ladder and its hint are left out", #e == 1 and e[1] == "ZipetteBabinks is joining...", e[1])
    KCD2MP.launcherDraws = false
    check("j: no Lua errors", #ERRS == 0, ERRS[1])
end

local pass, fail = 0, 0
for _, r in ipairs(RESULTS) do if r:sub(1, 4) == "PASS" then pass = pass + 1 else fail = fail + 1 end end
OUT = table.concat(RESULTS, "\n") .. string.format("\n%d passed, %d failed", pass, fail)
