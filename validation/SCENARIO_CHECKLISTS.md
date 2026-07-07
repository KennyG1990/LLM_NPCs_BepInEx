# P11 — Gameplay Scenario Acceptance Suite

Converted from `11 - Gameplay Examples.md`. A scenario goes green only with
current runtime evidence (dashboard screenshot or API transcript), never from
code inspection. Backend demo scripts can drive every step below through the
dashboard API; in-game proof additionally needs the C# bridge live.

Status legend: ✅ green (runtime evidence) · ◐ backend-proven only · ☐ not run

## 1. The Rumor and the War

Systems: AI Dialogues · NPC Memory · World Events · AI Actions · AI Diplomacy · Additional Systems

| # | Step | API/dashboard evidence expected | Status |
|---|------|-------------------------------|--------|
| 1 | Merchant Aldric profile + 2 prior dealings | `/api/memory/npc` + typed memories | ◐ backend path proven in `gm_systems_selftest` (profiles, memories) |
| 2 | Low trust hedges muster details | `/api/dialogue/state` trust gate = guarded/normal | ◐ trust gating proven in `dialogue_p2_selftest` |
| 3 | Muster event exists and reached Aldric | `/api/events/create` + `/api/events/known` | ◐ proven (`Blackfen musters levies` case) |
| 4 | Multi-step scout order to Mara | `/api/orders/issue` → move_to/patrol/return steps | ◐ proven (exact parse asserted) |
| 5 | Osric declares war, alliance shatters | `/api/diplomacy/relation declare_war` | ◐ proven |
| 6 | In-game: Aldric mentions the muster in floating dialogue | game UI + debuglog | ☐ needs live game |

## 2. The Winter Plague

Systems: Disease · World Events · Additional Systems · Death History · NPC Memory

| # | Step | Evidence | Status |
|---|------|----------|--------|
| 1 | Traveller infects settlers, winter odds | `/api/disease/infect` season=winter | ◐ proven |
| 2 | Quarantine + treatment change outcome | `/api/disease/treat` then `/api/disease/tick` → recovering vs critical | ◐ proven (elgiva vs wilhelm) |
| 3 | Outbreak becomes a world event | outbreak_event_id + `/api/events` | ◐ proven |
| 4 | Wilhelm dies; 50+ interactions gate | `/api/death/record` qualifies flag | ◐ proven (gate both ways) |
| 5 | Life story from milestones | `/api/death/history` story text | ◐ proven |
| 6 | In-game outbreak notification + graveside story UI | game UI | ☐ needs live game + C# |

## 3. The Siege at the Gate

Systems: Settlement Combat · Romance · AI Diplomacy · World Events · Death History

| # | Step | Evidence | Status |
|---|------|----------|--------|
| 1 | Raid classified: aggressor/defenders/panic | `/api/combat/incident` verdict | ◐ proven (gatehouse militia case) |
| 2 | Companion stances from trust | verdict.stances support/oppose/neutral | ◐ proven |
| 3 | Aftermath world event + casualties → death records | `/api/events`, `/api/death` | ◐ proven |
| 4 | War fatigue ticks toward peace | `/api/diplomacy/round` ×N → war_fatigue_peace | ◐ proven |
| 5 | In-game combat staging | game UI | ☐ needs live game + C# (P10 in-game deferred) |

## 4. Courtship and Betrayal

Systems: Romance · NPC Memory · AI Dialogues · AI Diplomacy

| # | Step | Evidence | Status |
|---|------|----------|--------|
| 1 | Elgiva initiates courtship (trait-driven) | `/api/romance` initiative list | ◐ proven |
| 2 | Betrothal via managed proposal, early proposal rejected | `/api/romance/interact` proposal gating | ◐ proven both ways |
| 3 | Gunnar's lie caught against prior claim | auto contradiction, trust drop, escalation | ◐ proven (`dialogue_p2_slice2_selftest`) |
| 4 | House banishment + later pardon | `/api/diplomacy/relation` banish/pardon proclamations | ◐ endpoint proven (selftest lacks explicit case — add) |
| 5 | Relationship decay from neglect | `/api/romance/decay` with elapsed days | ◐ proven |
| 6 | In-game courtship dialogue | game UI | ☐ needs live game |

## 5. Trade, Tribute, and the Warband

Systems: AI Diplomacy · Additional Systems · AI Actions

| # | Step | Evidence | Status |
|---|------|----------|--------|
| 1 | Tribute agreement recorded | `/api/diplomacy/relation` tribute terms | ◐ endpoint proven (add explicit selftest case) |
| 2 | Trade pact, limit of 2 enforced | trade_pact + 400 on third | ◐ proven |
| 3 | Recruitment lead flagged | `/api/entities/recruitment` score ≥ 0.5 | ◐ proven (Torsten) |
| 4 | Form-band/raid orders | deferred per DEFERRED_BACKLOG (needs P3 in-game primitives first) | ☐ deferred |
| 5 | Orders survive save/reload | ai_orders table persistence + C# reload hook | ◐ DB persistence proven; reload hook needs C# |

## Runtime evidence log

- 2026-07-06: all six selftests PASS in sandbox (memory_p1, character_sheet,
  player2_command_parser, dialogue_p2, dialogue_p2_slice2, gm_systems)
  plus devops_wiring_probe.
- 2026-07-06 LIVE (save Wolferlow, Alison Ridge -1015528, driven via
  dashboard game stream, no desktop automation):
  - Floating dialogue UI opened by remote click+Enter; thread rendered in-game.
  - Player: "Do you remember what you told me about our food stores?"
    NPC: "Aye, the stores are near empty; scarce crumbs remain..." — recorded
    as dialogue_player/dialogue_npc typed memories.
  - Claim persisted: "The food stores are low and insufficient for the settlers."
  - Player challenge auto-detected (overlap: food, stores) → claim status
    'contradicted'; NPC pushed back in-game ("Nay, the stores have been scarce...").
  - Trust audit: 0.50 → 0.52 (+0.02 consistent claims) → 0.44 (-0.08
    contradiction offense #1) in trust_events.
  - Scenario 4 step 3 (lie caught + trust drop): ✅ GREEN live.
