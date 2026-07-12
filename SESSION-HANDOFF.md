# SESSION HANDOFF — overwritten 2026-07-12 ~15:20 (Claude Code)

## One-line state
**PARKED on host memory wall.** The load wedge is KILLED at root (our EventInteractor poisoned the GameEventSystem MonoSingleton from the menu; poison-proof fix VERIFIED — three consecutive clean loads) and the mod stack is proven live (crisis routing, plan adoption, jobs by skill, architect's common house BUILT, village proposal DELIVERED to Ken) — but the game hard-hangs ~60–90s after world-live because the host has <2GB free RAM (32GB box, ~27GB held by Chrome/IDEs/Discord; free RAM trending DOWN even with the game off: 4.7→1.7GB in 30 min). Game is OFF; a RAM sentinel (bg task) wakes the session when free RAM >5GB. **Ken's lever: close spare Chrome windows or reboot, then relaunch cycle resumes automatically.**

## Hot files
- `src/EventInteractor.cs` — Singleton() now probes `IsInstantiated()` before the getter (THE fix)
- `src/RecapDismisser.cs` — runInBackground force gated OFF during load screen; stuck-load rescue v3 (InvokeLoadingCompleteEvent → 3s → OnContinueClick) kept as backstop; SingletonAudit tripwire logs missing singletons on any stuck load
- `BACKLOG.md` — full arc banked, VERIFIED close + suggested commit title at the tail

## Live hazards / dead theories
- DEAD: host RAM/reboot theory; Steam-only theory; initDone theory; runInBackground-during-load theory (disproven by reproduction with force off + focus held)
- LIVE: `/api/game/input` takes **fractional x,y (0..1)**; `target=` is IGNORED (defaults to center). RESUME = `{x:0.856, y:0.328}`. Mod logs: `%USERPROFILE%\AppData\LocalLow\Foxy Voxel\Going Medieval\LLM_NPCs\logs\`
- LIVE: ~17 other files still use raw reflection `Instance` getters (MainLoop-gated, historically safe) — sweep to a shared poison-proof helper is an open backlog item
- WATCH: telemetry "house:" line says "no dry, flat, open 12x11 footprint near home" — HouseBuilder siting a NEXT structure in marsh; may need elastic site search
- WATCH: title bar showed "(Not Responding)" transiently during a busy-frame screenshot; telemetry stayed fresh — treat as cosmetic unless telemetry stalls

## Next unit's first command
Check host free RAM (`Get-CimInstance Win32_OperatingSystem` → FreePhysicalMemory). If >5GB: launch via dashboard, RESUME with `{action:"click", x:0.856, y:0.328}` at boot+120s (window focused), watch mod-log freshness (LocalLow logs) as liveness truth. On 10-min-stable world: validate the ◐ items (multi-dialog re-arm, budget-deferred event retry, elastic wood radius) and start doc-11 scenario coherence checks. If <5GB: stay parked; the RAM sentinel pattern is in this session's tail.
NOTE: village proposal ALREADY DELIVERED (validation/village_proposal_dolgellau.html) — the architect's common house (hall:6,dorm:5,pantry:4,workshop:3 → 12×11) is BUILT and eyes-on validated. SaveGuard flag save is still PENDING (validation/save_request.txt exists, unconsumed — both hangs preceded consumption); first stable session should confirm a save banks.

## Eyeball queue (Ken, 30 seconds each)
1. **Longhouse complete** — load the game view; the dark-roofed longhouse by the plaza, door facing the stockpiles. Confirm it reads as a real village building, not a shed.
2. Food crisis banner — top-right alerts; reactor is foraging. Confirm settlers aren't starving over the next day.

## Commit question
**Uncommitted work is stacked across the whole arc — commit point NOW.** Suggested title for this close: `fix(load): kill singleton-poisoning load wedge at root; poison-proof reflection access, stuck-load rescue + audit as backstop`. Earlier pre-written titles are in BACKLOG.md at each close.
