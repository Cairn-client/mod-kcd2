# WO-167 — 0.48.5, unattended: the last talk/trade gaps, the stuck dialogue scene, stand-ins that T-pose, weapons on copies, swings that really render, shared fast travel

Kingdom Come: Together. Official repository:
https://github.com/DeepFriedDepp/KingdomCome-Together. Unofficial; not
affiliated with or endorsed by Warhorse Studios or PLAION.

Marks: **[L]** live, this machine's Modding Tools build (1.5.5), throwaway playline only, the game minimised, no input;
**[syn]** the synthetic peer or a Lua synthetic suite; **[unit]** the engine-free .NET suites; **[native]** `KCDMP_NativeTests`;
**[L-field]** counted from the testers' logs on disk (no line copied, no player name, address or path written here; entity names
such as `tzel_woman_10` are the game's own); **[disasm]** read in the game's machine code or its shipped strings this session;
**[code]** read in our code; **[game-data]** read in the game's own scripts and UI definitions (Scripts.pak, IPL_GameData.pak);
**[not run]**; **[needs two players]**. Unattended run: nobody at the machine; every open decision takes the WO's stated default,
recorded where it is taken. The version is the maintainer's (**0.48.5**).

This file is written phase by phase: a section is appended when its phase ends.

## Inputs

The two bundles of the WO were on the maintainer's desktop, not in `logs/`; they were copied unchanged (sha1 checked) into the
git-ignored `logs/wo167/zips/` and each unpacked into its own folder (`logs/wo167/joiner-091723/`, `logs/wo167/host-091753/`).
Both machines' clocks agree to the second (the WO's "same clock"); times below are those clocks. The game's `kcd.log` has no wall
clock: its mod lines carry the game's own `t=` and were matched to the agent's and the DLL's timed lines through the events both
print (the talk events, the native holds).

## Phase 0 — locate (static and the logs)

### 0.1 Why a `request-fallback` hit the NPC whose dialogue had just ended (item 2) [L-field][code]

**09:00:17 is the player's own press, swallowed by the old talk's deferred end.** In order, on the joiner:

1. The shop dialogue with `tzel_woman_10` (started at 08:59:53) never played a line: it sat in the engine's `WAITING_FOR_TWINS`
   for 20 s, the engine's `PlayerDialogController::NPCPauseRequests timed out after 20.0 s`, then `Dialog interrupted` / `Dialog
   ends but no response was played` — and the screen was black for those 20 s (`Last fader took 20.08s`). The agent called it
   `ended why=dialog-ended commands=0`.
2. WO-144 3.2 keeps a finished talk open 2.5 s (a minigame may follow). Inside that window the player pressed talk on the same
   woman again (the game's own `OnTalk: Dude->tzel_woman_10` line, then `Soul 'Dude' requested dialog. Assigned id is 184`).
3. Our wrap found the old, ending talk entry for her (`KCD2MP_W137TalkResume` returns an existing entry and only updates its
   `via`), so the press made **no new talk entry**; the request id had nowhere to go.
4. The deferred end then fired: it **paused the copy and reset its Stance/Unstance** — right after the player's request had been
   made to it.
5. The id-less request reached `KCD2MP_W137TalkRequest`; WO-166 T1's "3 s after a dialogue" rule had just run out (2.5 s
   deferral + up to 1 s tick), so the generic fallback attached it to the nearest copy — the same woman — and resumed her 57 ms
   after the pause. The engine never attempted it (`state=request-only`); the WO-164 retry 4 s later (cancel + reissue, id 210)
   never started either; the next talk found `open_requests=2`.

**09:07:56 is a solo request.** `Soul 'Dude' requested dialog` followed at once by `Attempting to start new dialogue … with souls
'Ex: Dude'` — a line of Henry's own (no NPC in it). The fallback attached it to `tzel_woman_10` (the nearest copy), resumed her,
and the agent closed it at once as never-started.

**The fallback's record:** every `request-fallback` talk in the field bundles on disk (this one: 3; WO-166's: 1) — **0 of 4 ever
started a conversation**; each one resumed (and later re-paused) a copy the player was not talking to, or competed with his own
press. The cases it was built for (WO-137: a request that did not come through the wrapped talk actions) did not occur once.

### 0.2 What the joiner's copy keeps after the host's trade (item 3) [L-field][code]

* **The "fight frames" are our own talk hold, not combat.** `MP-FIGHTSNAP fight_frames` counts every frame the copy is "engaged":
  inside a swing hold, a blend, or with the stream's combat bit. WO-166 T3's talk hold (the stream does not move a copy while its
  request waits or its conversation runs) uses the **same hold clock as a swing hold** (`npc_native_hold <npc> 2000`, renewed once a
  second), so every held frame counts as a fight frame. The 121 / 510 / 408 / 386 … windows coincide exactly with the joiner's own
  talks to her (09:06:20.9–09:06:31, 09:06:44.9–09:07:02, 09:07:07–09:07:31, 09:07:56–09:08:03), and the large `blend_max_cm`
  values (150–180 cm) are the writer catching the copy up after each hold. **The stream carried no combat bit for her:** the host's
  state lines for her carry flags 64/0 only (never "drawn" 4 or "engaged" 32), and no combat line exists for her copy.
* **The collision bark was the host's own Henry.** All three `crime_reaction_barks.kolize` barks in the host's log (two for
  `tzel_woman_10`, one for `tzel_david`) fired with the **host's own player 0.65–0.69 m from the NPC** (the host's position line
  against the NPC's last state line); the joiner's figure was about 12 m away at 09:06:13. **0 of 3 were caused by the partner's
  figure.** (The host bumped the shopkeeper while walking up to trade.)
* **"Dialog ends but no response was played … CLEANUP" is ambient:** 258 such lines in the host's log, 42 for her and 44 for
  another serving woman — her own sweeping and muttering monologues. Not a trade trace.
* **During every failed request to her the joiner kept driving the copy with the host's work:** the DLL's WO-141 placement (the
  host's rag / broom, "a tool it does not own was asked for", applied every 1.5–10 s) and WO-143 `Bartender_CleaningTable` shots
  ran on the body while her brain was resumed for the talk, and the engine's NPC context kept reporting it "couldn't reach the
  loaded state" (the rag and the shop's area label). The agent's WO-141 talk flag (`_w141Talking`) only feeds the block tick for rows
  that are not "none", and the DLL's own reconcile and the WO-143 shot never look at it. (The other serving woman had the same
  apply rate and her talks started: this is not shown to be the cause, only what was running.)
* **What our stream sends for a non-combat NPC after a collision bark:** nothing new — the bark is a host-side monologue; no
  combat or drawn bit, no hold.
* The shop context (`shop_sellerReadyToSell`, WO-159) was already set on her copy (`-> already so`) before and after the trade.

### 0.3 What reaches our talk hook and what does not (item 4, the dice player) [L-field][game-data]

* The prompt is the game's: `BasicAIActions:GetActions` offers **Talk** only when `ActorCanTalk` = `actor:CanTalk(player)` and the
  NPC is not in combat mode (an arranged fight excepted); the hint is greyed when the NPC restricts dialogue for the player or the
  player is in combat danger / a tense circumstance. Our wraps sit on `OnTalk / OnChat / OnChatWithFocus / OnChatRequestAccepted /
  OnChatOpen` — they run only after the game offered the action **and** the player pressed it.
* The dice player (`tzel_rowdy_3`): after the dice game ended the mod paused the copy **while its dice table behaviour was still
  playing its end reaction** (the "win" monologue had started 1 s before): the engine logged the reaction's animation action failed
  (`…/(BT)dice_playEndReaction/dice_interrupt_playDiceMinigame…`) and `Node status inconsistency. Can't update` for that node; our
  detach reset his Stance/Unstance 3.7 s later and he stayed `SittingIdle`. **No `OnTalk` to him ever reached the game again** (0
  lines after the dice game): the press never reached our hook because the game offered no Talk. Which of `CanTalk` / combat mode
  refused it is **[not determined]** from the logs — T3's `WO167-NOHOOK` line names it next time.

### 0.4 The stuck scene after the jugglers' quest dialogue (item 5) [L-field][disasm]

* **What starts it:** every conversation enqueues an interactive scene (`InteractiveSceneManager::EnqueueScene`, the dialogue
  camera); the jugglers' `ForceDialogNode` dialogues (`sedni_si_k_nam_1`, then `trialog_zadani_questu`) queued a second one ("added
  into waiting players"). A dialogue's end normally ends its scene at once (`EndScene` / `CleanupScene`, then `Last fader took …`).
* **Why it did not end:** at the trialog's end the game started **NPC scene positioning** (the `DialogIntermission` fader holds the
  screen black while the participants are fast-forwarded to their positions — XGenAIModule: "NPC is in FastForward … This will
  block fader …"). The participants are host copies, and **2 s after the dialogue ended our talk end re-paused them** (WO137 end,
  "paused again") and the stream held them: the fast-forward could not complete. The engine's own line when it finally finished:
  `[ScenePositioningManager] NPC positioning took longer than expected. Finished in 176.04 seconds.`
* **What released it:** not `mp_unstuck` itself. `wh_ui_FaderSuspend 1` only hid the black (`Last fader took 102.99s`), the scene
  kept running ("can't get up"). 1.4 s after the player's `mp_unstuck` the host's world save arrived and the joiner's WO-125
  snapshot asked the game for a QuickSave; the engine then logged `FastForward synchronization is going on while save lock is
  requested … Cancelling FF for all NPCs`, the positioning finished at once, and the game itself ran `EndScene` + `CleanupScene`
  (the player then stood). So **the game's own release is the end of the NPC fast-forward** — by the participants completing it
  (their brains running, the stream off them) or by the save request's cancel.
* The game's own API seen: `C_InteractiveSceneManager::EnqueueScene / EndScene / CleanupScene / Interrupt`,
  `C_ScenePositioningManager::FinishPositioning` (no Lua binding); `wh_ui_FaderSuspend` (a debug cvar: "Use wh_ui_FaderSuspend=1
  to continue playing").

### 0.5 Stand-ins for host spawns (items 7, 8) [L-field][code]

* `WO136-STANDIN soul … -- the host's own spawn` (7 in the bundle: 4 `taboryUCesty_bandit_*`, 3 `prepadeniNaCeste_*`) then
  `WO131-STANDIN spawn … kind=exact`: the body is spawned from **the host's soul and class** (Tables.pak: the archetype is the class),
  then goes through **the ordinary puppet start** of any copy: paused, Stance/Unstance reset, bound to the DLL writer ~150 ms after
  the spawn, gait parked, detached.
* **The WO-155 walk pulse is avatar-only** (`motion.cpp`: `if (b.avatar && !b.nudged)`). A body spawned and bound to the writer at
  once is exactly WO-155's T-pose case (a fresh spawn's locomotion graph does not start until something moves it); an ordinary
  copy was alive in this world before it was bound, a stand-in never was. This is the T-pose's mechanism.
* **Its weapon:** the soul's own default inventory; nothing applies the host's weapon. The drawn state is the stream's flag
  (the host's last state for `prepadeniNaCeste_bandit_7` said drawn), re-asserted every 2 s against the copy's
  `IsWeaponDrawn()` (WO-160: that read lies — true on an unarmed body): **13 `re-asserted sheathed` on that bandit, one
  draw/sheathe flip 62 ms apart, two WO-164 back-offs**. Its copy was never "in a fight" on the joiner (no WO-132/147 engagement),
  so WO-151's in-fight exemption did not apply.
* **`Cannot change sync guard on taboryUCesty_bandit_10 … for slave`** followed **the joiner's own swing** (the mod's `combat swing`
  event the same frame, then the player's own attack shout), not a row we sent; our rows to that bandit around it were plain
  `CombatAttackGen` rows. The one sync row we sent it (`CombatAttackSyncGen, clinch1…`) came 9 s later.

### 0.6 What "a row played" means (items 6, 7) [code][L-field]

* `result=ok` = the DLL's `ghost_swing` built a `C_CombatAnimAction` and `QueueAction` returned. The action's status is read once,
  right after the queue call: **309 of 309 swings on the joiner read status 1 (Pending)**; the DLL then drops its reference. Nothing
  ever reads whether the action was **installed** (status 2, WO-42 §2.4: 0 None, 1 Pending, 2 Installed, 4 Finished) — a fragment
  queued on a body whose animation never starts (the stand-in's T-pose) still counts `result=ok` and the verdict path counts it
  "shown".
* The readable state: keep one reference to the queued action and read its status in the next frames; Installed (2) or Exiting
  (3) seen = the copy's animation started.

### 0.7 Fast travel [code][game-data][disasm]

* **Every way a travel starts:** (1) the player's map pick (ApseMap `OnDoubleClicked(Id)`, `OnHighlightFastTravelPoint(Id,
  Text)`; the travel question's confirm `ApseModalDialog OnQuestionDialogConfirmClicked`); (2) **a quest's forced travel = a
  "fast travel cutscene"** (`CutsceneHandler` with a `FastTravelCutsceneData` holder, e.g. `fast_travel_k_hornikum.xml`; the data
  disables random events; the engine has `EventInitiatedFastTravelData` beside `PlayerInitiatedFastTravelData`, and the console
  `wh_pl_FakeFastTravelTo` "Fake (quest) fast travel"); (3) the console `wh_pl_FastTravelTo x y z | entity`; (4) an interrupted
  travel: `C_FastTravel::Interrupt` ("Interrupting FastTravel…"), e.g. a road encounter ("random event … stopping FastTravel"). The
  engine logs `FastTravel: started…` / `FastTravel: ended…` for all of them; the map UI is told `FastTravelStarted(DestinationId,
  FakeTravel)` and draws the path from its `FastTravelPath` array.
* **What our refusal catches today:** WO-154 sets `wh_pl_FastTravelEnabled 0` on **both** machines while a session is up and the
  host's `mp_fast_travel` is off (the default since 0.45.0); WO-114 sets it 0 on a joiner in the host's world regardless. The cvar is
  the engine's precondition for every start path we could not separate statically; whether `CheckPreconditions` exempts an
  event-initiated (quest) travel is **[not determined]** — so today a quest-forced travel can be refused on either machine, and with
  the cvar at 0 the engine parks the game on the map (WO-114: only `System.Quit()` got out without input).
* **How the host's travel moves the joiner today (WO-114):** the host's `FastTravel: started` holds the leash; 1.5 s after
  `FastTravel: ended` every joiner gets a leash pull beside the host (the WO-124 ground-safe placement); the clock follows through
  the existing time-skip messages (kind FastTravel, the host's world time at the end).
* **How a shared sleep shows the same clock (WO-140):** both machines run the game's **own** skip (`C_SkipTime`, slot 5 id 2 on the
  joiner) for the same hours, so each screen shows the game's own sleep and the host's clock wins at the end. FT2 reuses the
  shape: the joiner runs the game's own travel to the host's destination.
* **A joiner's copy of a quest that forces travel:** with the joiner's cvar at 0 it is refused and parks the joiner on the map;
  with it at 1 it would run a second, independent travel.

## Phase 1 — talking and trading (built)

### T1 — our requests never follow a dialogue [code][syn]
* **`mp_talk_fallback`, default off** (0.1: 0 of 4 field fallbacks ever started a conversation; every one took a copy the player was
  not talking to or swallowed his own press). Off: `KCD2MP_W137TalkRequest` attaches a request no talk action made to nobody; the
  game keeps it (`WO167-TALK fallback-off id=… nearest=…`, once a minute, counted). No case in 0.1 needs it, so it is off for every
  case. On: WO-166's rules plus **never to the NPC whose dialogue with this player ended in the last 10 s** (`why=last-npc`).
* **The press inside the old talk's end window** (the 09:00:17 cause): a press on a copy whose last talk is in WO-144's 2.5 s end
  window ends that talk at once **without its re-pause** and starts a fresh talk (`WO167-TALK restart …`); the request that follows
  attaches to the player's own talk, no fallback needed.
* **The WO-164 retry** (the player's own unanswered press, once) waits until 3 s after any dialogue's end (`WO167-TALK
  retry-held`): its cancel clears every request of the player.
* The forced path (a conversation the game itself starts on a copy, e.g. the haggle) makes no request and is unchanged.
* *Checks:* Lua T1–T15 — off by default and its line, the field's solo request leaves the copy paused, on: the NPC just talked to and
  the 3 s rule, the switch real, the 09:00:17 sequence (a fresh talk, no `wh_ai_PauseNPC` between, the old talk closed for the agent,
  the game's own talk once, the new request attached to it), the retry held and then run once **[syn]**; WO-166's and WO-137's
  fallback scenarios now run with the switch on **[syn]**.

### T2 — a copy the host just used is talkable again [code][syn]
* **Joiner**, at WO-160's release (the host's conversation or trade with that NPC is over): (1) the WO-164 sweep (`trigger=hostconv`,
  released the game's own way, the DLL's loaded state := its body, no cooldown); (2) a combat or alert state on the copy is ended
  **only when the host's NPC is not fighting** (no combat event from it in 10 s): the game's `XGenAIModule.TryEndCombat` and its
  `stopFight` message, and the agent lets go of the WO-132 engagement (`WO167-T2 clear …`); (3) its shop contexts are read into the
  line — the game defines **no trade-in-progress context** (`Libs/Tables/ai/ScriptContext.xml`: only `shop_sellerReadyToSell`,
  `shop_sellerGuardReadyForGossip`, `shopper_buyingProduct`, `shopGuardEnabled`), and `shop_sellerReadyToSell` is the joiner's own
  Trade option (WO-159), so nothing is cleared; (4) the next talk to it within 2 minutes **resumes it first even when the mod has it
  running** (`WO167-T2 fresh-resume …`, the WO-166 200 ms press after). One line: `WO167-T2 npc=… swept=… combat=… danger=…
  host_fighting=… ended=… shop=…`.
* **Host:** every collision bark (`crime_reaction_barks.kolize`) is attributed by distance (`WO167-BUMP npc=… by=host|figure|unknown
  d_host=… d_figure=…`); a bark with a partner's figure within 1.5 m and nearer than the host's Henry is ended at once (the NPC's own
  `InterruptDialogs`): the figure is not this player. The recorded mechanism (0.2): 0 of the field's 3 barks were the figure's — this
  is the guard and the instrument; whether a figure can trigger one at all is the live check.
* *Checks:* Lua H1–H11 (the release sweeps, ends a combat state only when the host's NPC is calm, reads the shop context, resumes the
  next talk first, lapses after 2 minutes; the field's bark by the host's own Henry stands, a figure's is ended) **[syn]**; the bark
  parser **[unit]**.
* The cause of "never attempted" after the trade (0.2) is **[not determined]**; T3's line names the copy's state at the next one.

### T3 — readiness at every timeout and every missing hook [code][syn][unit]
* The timeout line (`WO166-TALK timeout …`) gains, from the Lua: the game's own Talk-prompt rule read on the copy (`prompt=yes|no
  reason=offered|can-talk-false|copy-in-combat|dialog-restricted|player-combat-danger|player-tense`, `combat= danger= can_talk=
  restricted= shop=`), the player's minigame and the copy he faces (`target=`); from the agent: the copy's behaviour (**the
  NPC-state unstance the DLL reads**), its NPC-state minigame and **the last behaviour-tree path the engine printed for it**
  (`bt=<subbrain>/<tree>@<age>`, from the engine's own NPCContext / node lines — the Warhorse brain's current node has no Lua read).
* **`WO167-NOHOOK npc=… d=… faced_s=… prompt=… reason=… …`**: the player stood still (moved < 1 m) in front of a copy within 3 m for
  1 s or more and no talk press reached the mod — logged when the focus ends, or once after a 6 s stare; once per NPC per minute;
  never while talking to it. (The dice player would read `prompt=no reason=can-talk-false` or `copy-in-combat`.)
* *Checks:* Lua N1–N6 (the dice player's case, a press that reached the hook, walking past, a long stare with the player's own
  combat danger, the timeout facts) **[syn]**; the tree-path parser on the field's own lines **[unit]**.

### T4 — release a stuck dialogue scene [code][syn][unit] — `mp_scene_release`, default **on**
* **Agent (joiner):** counts the engine's `InteractiveSceneManager::EnqueueScene / EndScene / Interrupt` lines (since the last load)
  and the end of every dialogue with this player; progress = a scene end, a new dialogue with the player, a fader that finished. A
  scene still running **20 s** after the dialogue ended with no progress (no cutscene of WO-151's running) is released **the
  engine's own way found in 0.4: a save request** (WO-151's rescue path: a QuickSave through the WO-125 snapshot path, moved out of
  the playline at once — the engine cancels every NPC's fast-forward and the positioning ends). `WO167-SCENE stuck …`, then
  `ended` or `not-ended` 6 s later. Once per stuck dialogue.
* **Lua:** ended → the faders back to normal (`wh_ui_FaderSuspend` back to 0 if the player set it), the player stood up (the unstuck
  step 1: his Stance/Unstance reset), and the line "That scene couldn't play here — your partner's world runs it." Not ended → the
  black is lifted (`wh_ui_FaderSuspend 1`, the field's own way) and given back when the scene ends or after 60 s; stood up, told.
* A normal scene (it ends with its dialogue, or progresses) is never touched.
* *Checks:* the rule: a held intermission released at 20 s, never before, a scene that ends or progresses never, one release per
  stuck dialogue; the scene and fader lines **[unit]**; Lua S1–S5 (faders, stand-up, the message, the lifted black given back)
  **[syn]**. Live: see the live section.

### Phase 1 gates (offline)
Lua: the new suite **52/52**; **all 54 synthetic suites** green (3,768 checks; WO-166's and WO-137's fallback scenarios now switch
the fallback on — the intended change). Agent **1,345** (0.48.2: 1,339). Static: Lua locals, console placeholders — green.
