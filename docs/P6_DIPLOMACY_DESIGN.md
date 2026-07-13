# P6 — AI Diplomacy: Design for Sign-off (SPECIFIED 2026-07-12)

**Target:** reference doc `03 - AI Diplomacy.md` + its load in scenarios 1/3/4/5 of doc 11.
**Principle (3-layer):** deterministic simulation decides WHAT IS TRUE (relations, wars, treaties, fatigue); the LLM decides only WITHIN BOUNDED MENUS (which proposal to make, proclamation text); the game supplies ground truth (faction roster, alignment, raid attributions).

## What the game gives us (reconciled)
- Faction roster + player alignment per faction (`FactionsController`, alignment %, raid attribution — the game picks a hostile faction as raider).
- The game does NOT simulate faction↔faction politics. Everything inter-faction is OURS.

## Data model (new tables, dashboard DB — pure Python, offline-testable)
- `dip_factions(faction_id, name, philosophy, is_player, first_seen)` — seeded from game roster via mod telemetry feed; player settlement is a faction.
- `dip_relations(a_id, b_id, score(-100..100), state(war|hostile|neutral|friendly|allied), since_ts)` — symmetric pairs.
- `dip_wars(war_id, aggressor, defender, started_ts, ended_ts, fatigue_a, fatigue_b, losses_a, losses_b, lands_delta)` — war stats per doc.
- `dip_treaties(treaty_id, kind(alliance|trade|tribute|reparations|peace), a_id, b_id, terms_json, active, started_ts, ends_ts)` — max 2 econ agreements per faction (doc).
- `dip_rounds(round_id, ts, mover_faction, move_kind, target, summary, proclamation_text)` — the audit ledger; feeds P5 events.
- `dip_banishments(house_id, faction_id, banished_ts, pardoned_ts)`.

## The round ticker (deterministic heart)
- Real-time cadence: one diplomacy ROUND every N real minutes (default 20; config). Each round ONE faction moves (round-robin with jitter) — "politics unfold at a believable pace" (doc).
- Legal move menu per faction, computed deterministically from state: propose_alliance / declare_war / sue_for_peace / offer_trade_pact / demand_tribute / pay_reparations / banish_house / pardon_house / no_move. Preconditions are code (e.g. declare_war requires score < -40 and no active war; sue_for_peace requires fatigue > threshold).
- **War fatigue math:** fatigue += base_per_round + casualties_weight × losses_this_round; peace becomes a legal move at fatigue ≥ 60; AUTO-peace forced at 100 (both sides) with reparations sized by loss ratio. Alliance auto-shatters when an ally declares war on the other ally's friend (score cascade).
- Score dynamics: passive drift toward 0 (±1/round), event deltas (raids −25 vs raided party, tribute +5/period to payer's standing, gifts +, broken treaty −40), clamped; state derived from score bands (war<−60<hostile<−20<neutral<+40<friendly<+75<allied — allied also requires treaty).

## LLM lanes (budget-governed, existing governor)
- Lane `diplomacy` (critical): when a faction's legal-move menu has >1 option, the LLM picks ONE + writes the proclamation (1 call per round MAX; menu + world context in, choice + text out; invalid choice → deterministic fallback = highest-precondition-margin move). Validation clamps identical to the planner pattern (never act on ids not in the menu).
- Proclamations post to the P5 events API (`type=political`) → propagate to NPCs → surface in dialogue ("Blackfen musters levies") — closing scenario 1's rumor loop.

## Mod-side feed (deploy-gated, small)
- Telemetry additions: faction roster + alignment dump each N ticks → `validation/factions.txt` + POST to dashboard (same pattern as colony_status). Raid start/end already read by EventInteractor → POST as war-relevant events (casualty counts from DeathChronicler).

## Player surface
- Dashboard panel (later slice): relations map, active wars/treaties, proclamation feed. Dialogue integration is automatic via P5 context.

## Validation plan (declared now)
1. OFFLINE unit tests (no game): round ticker determinism, fatigue→peace path, alliance-shatter cascade, treaty caps, banishment/pardon, reparations sizing. Negative: illegal move rejected + fallback fires; LLM returning garbage → deterministic fallback; round idempotency on reload.
2. LIVE (deploy window): factions seeded from real roster; a raid changes relations and starts/updates a war with casualties from the real DeathChronicler; a proclamation reaches a settler's dialogue context (scenario-1 slice).
3. EXPERIENCE gate (Ken): reads a proclamation in dialogue that matches actual events.

## Out of scope (this unit)
Territory transfers (needs map ownership model), faction leader characters, scenario-3 combat direction, player-initiated diplomacy UI.

## Sign-off asks (Ken)
- Round cadence default 20 real minutes? (config `dip_round_minutes`)
- OK that the player settlement participates as a faction (can be warred on / allied)?
- Proclamation tone: period-flavored short (1-2 sentences) vs longer missives?
