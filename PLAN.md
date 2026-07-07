# Going Medieval AI Influence Roadmap

Last updated: 2026-07-06

> 2026-07-06 sweep (Cowork session): P2 slice 2 complete (contradiction
> matcher v2, deterministic trust rules + trust_events, voice authoring,
> barter resolution, dashboard panels). P3-P10 BACKEND slices implemented in
> `dashboard/gm_systems.py` behind dispatch hooks, verified by
> `tools/gm_systems_selftest.py` and `tools/dialogue_p2_slice2_selftest.py`
> (all six selftests PASS). Dashboard gained a World Systems tab. In-game C#
> surfacing for P3+ remains open (host dotnet gate). See `BACKLOG.md` and
> `validation/SCENARIO_CHECKLISTS.md`.

This roadmap locks the implementation order for the design documents in:

`C:\Users\Moshi\Desktop\X4 AI Influence\AI Influence - Systems - Going Medieval`

The current validated prototype already proves the Player2-backed settler decision loop, dashboard backend, memory DB, RoleRAG endpoint, relationship/pressure APIs, game stream/control, and basic colony adviser persistence. The next work should build outward from that foundation instead of jumping to broad world simulation.

## Locked Priority Order

### P0 - Keep Current Prototype Green

Before each roadmap update:

- `dotnet build --configuration Release -t:Rebuild` must pass.
- `python -m py_compile dashboard\dashboard_server.py` must pass.
- `python tools\player2_command_parser_selftest.py` must pass.
- `/health` must report `ok=true` and `player2.online=true` when live validation is required.
- New features must preserve canonical settler IDs in DB writes.

### P1 - `05 - NPC Memory System.md` - Release Candidate

Reason: every later feature depends on durable identity, interaction history, relationship state, secrets, event memories, and evolving character files.

Deliverables:

- Formalize NPC personal files in SQLite.
- Add typed memory categories for conversations, secrets, events, promises, betrayals, favors, deaths, and relationship milestones.
- Add memory retrieval rules for player dialogue, NPC decisions, adviser output, and future world events.
- Add regression tests for memory writes and retrieval shape.
- Add dashboard visibility for memory categories per settler.

Acceptance gates:

- A selected settler can recall a prior player conversation in a later dialogue prompt.
- DB rows show stable settler identity across reload/session changes.
- Dashboard shows recent and durable memories separately.

Status: release-candidate implementation completed on 2026-07-05. See `P1_MEMORY_RELEASE_CANDIDATE.md` for validation evidence and known non-blockers.

### P2 - `01 - AI Dialogues with NPCs.md`

Reason: dialogue is the main player-facing interface and the best visible proof that memory, trust, personality, and RoleRAG matter.

Deliverables:

- Upgrade direct settler dialogue from prototype chat to memory-aware conversation.
- Add trust-gated disclosure.
- Add personality/backstory voice fields.
- Add lie/contradiction tracking as DB-backed claims, not just prompt text.
- Add dialogue barter hooks as stubbed intent records before full trade execution.
- Keep backend-provider abstraction compatible with Player2 first; other providers are deferred.

Acceptance gates:

- Player can open a visible in-game dialogue UI and see a settler reference prior events.
- Trust level changes what the settler will reveal.
- Dialogue-generated claims are persisted and can be contradicted later.

Status: RELEASE CANDIDATE 2026-07-06. Slice 1 (2026-07-05) + slice 2 (2026-07-06: contradiction matcher v2, deterministic trust rules + trust_events audit, barter resolution, authored voice). LIVE IN-GAME PROOF on save `Wolferlow`, settler Alison Ridge (-1015528), driven entirely through the dashboard game stream: floating dialogue UI opened, memory-referencing reply recorded as typed memories, claim persisted ("food stores are low..."), player challenge auto-detected -> claim contradicted, trust 0.50 -> 0.52 (+0.02 consistent claims) -> 0.44 (-0.08 contradiction offense #1), all in trust_events. Known RC issues: (1) settler numeric IDs change per game session (5 Alison Ridge IDs in Wolferlow) - cross-session memory continuity depends on unstable identity, needs canonical identity mapping in C#; (2) voice builder ingests internal trait tokens (e.g. "midageeffector01") - needs trait sanitization.

### P3 - `09 - AI Actions System.md`

Reason: this turns dialogue into agency. It connects "talking to NPCs" with "NPCs do things."

Deliverables:

- Add a persistent AI order table.
- Parse natural-language player orders into bounded action plans.
- Implement Going Medieval-appropriate first actions before non-native X4/Bannerlord concepts:
  - follow selected settler/player proxy where possible
  - move/hold position
  - prioritize job category
  - patrol local area
  - attack nearby hostile target when valid
  - return to work
- Add save/reload persistence for queued orders.
- Add dashboard order inspection.

Status 2026-07-06: backend + C# OrderExecutor live. Acceptance proven in-game:
"Prioritize mining, then return to work" set Alison's Mine priority to 1
(JOBS panel evidence) and completed through the queue; construction pipeline
(propose→approve→order→execute) also ran live. Remaining for RC: true
move/patrol/attack pathing via MoveTo reflection, follow_player, and the
dialogue→order bridge. See BACKLOG.md and NPC_BUILDING_FRAMEWORK.md.

Acceptance gates:

- Player gives a settler an order through dialogue.
- The order is persisted in SQLite.
- The game attempts a validated action or safely records why it could not execute.

### P4 - `10 - Additional Systems.md`

Reason: world grounding is required before rumors, diplomacy, travel, and recruitment can behave coherently.

Deliverables:

- Track mentioned settlers, factions, settlements, goods, and regions as entities.
- Add visit/location history where Going Medieval exposes usable state.
- Add faction/standing schema even if initially sparse.
- Add recruitment-opportunity detection as dashboard/adviser output first.
- Add economic ripple placeholders as event records, not full simulation.

Acceptance gates:

- Dialogue can persist a mentioned entity and later retrieve it.
- Dashboard can show known entities and visit/history records.
- Adviser output can cite grounded entities instead of free-floating narrative.

### P5 - `02 - Dynamic World Events.md`

Reason: events need memory and grounded entities before they can spread believably.

Deliverables:

- Add world event schema with event type, origin, affected entities, rumor state, confidence, and lifecycle.
- Add event propagation through NPC memories.
- Add dashboard event timeline.
- Feed events into dialogue prompts and colony adviser prompts.

Acceptance gates:

- An event can be created, updated, remembered by specific NPCs, and surfaced in dialogue.
- Event state evolves over time instead of being a one-shot log row.

### P6 - `03 - AI Diplomacy.md`

Reason: diplomacy depends on world events, factions, standing, and delayed state transitions.

Deliverables:

- Add faction state tables for relations, war/peace, alliances, trade agreements, tribute, reparations, banishment, and war fatigue.
- Add scheduled diplomacy rounds.
- Add AI-generated proclamations as persisted faction events.
- Keep first implementation dashboard/adviser-visible before trying to alter game-native factions.

Acceptance gates:

- Two factions can change relation state through a scheduled diplomacy round.
- A proclamation is generated and visible in dashboard/dialogue.
- War fatigue or trade pact state affects later dialogue/adviser output.

### P7 - `06 - Romance & Marriage System.md`

Reason: romance builds on memory, dialogue, trust, relationship state, and initiative.

Deliverables:

- Add romance/intimacy progression distinct from friendship/trust.
- Add relationship decay.
- Add NPC-initiated courtship/proposal events.
- Connect marriage UI to persisted state.

Acceptance gates:

- Relationship state changes through interactions.
- Neglect decays a relationship over time.
- Marriage/proposal events persist and appear in dialogue/social UI.

### P8 - `08 - Death History System.md`

Reason: death histories need accumulated memories and milestones or they will be shallow generated text.

Deliverables:

- Detect settler death where game state permits.
- Gate history generation on meaningful relationship/history thresholds.
- Generate life story from persisted milestones.
- Present the history in a visible in-game or dashboard UI.

Acceptance gates:

- A qualifying dead settler gets a generated life history.
- A non-qualifying character does not generate a fake deep history.
- The player can decline or skip generation.

### P9 - `04 - Disease & Plague System.md`

Reason: disease has a large simulation footprint and should become world-event aware.

Deliverables:

- Add disease state, immunity, infection source, quarantine state, and treatment records.
- Add seasonal/weather modifiers where accessible.
- Add infirmary/herbal-care abstractions.
- Add outbreak events that feed dialogue, trade, and adviser output.
- Add visible notifications before deep mechanical penalties.

Acceptance gates:

- Infection state persists and changes over time.
- Quarantine/treatment changes infection or recovery odds.
- Outbreak appears as a world event and is discussed by NPCs.

### P10 - `07 - Settlement Combat System.md`

Reason: this is the highest-risk system. It touches hostility, defenders, civilians, reinforcements, faction intervention, aftermath, and death history.

Deliverables:

- Start with event/adviser orchestration for combat, not uncontrolled spawning.
- Add combat incident classification: aggressor, defender, witnesses, casualties, location.
- Add companion/settler stance decisions.
- Add aftermath events and death-history hooks.
- Only add defender spawning or direct combat manipulation after safe game API/reflection proof.

Acceptance gates:

- A combat incident is classified and persisted.
- NPCs discuss the aftermath.
- Death-history and diplomacy/event systems receive combat outcomes.

### P11 - `11 - Gameplay Examples.md`

Reason: this is an integration acceptance suite, not a feature update.

Deliverables:

- Convert the five scenarios into demo scripts and validation checklists.
- Track which systems each scenario exercises.
- Mark a scenario green only with current runtime evidence.

Acceptance gates:

- Each scenario has a reproducible setup, expected dashboard evidence, expected in-game output, and pass/fail notes.

### Reference - `00 - Overview.md`

Reason: this is the product north star, not a single implementation slice.

Use it to keep scope aligned:

- AI-generated conversations.
- Memory-retentive characters.
- AI influence over diplomacy, events, actions, combat, disease, romance, and death history.
- Bring-your-own-backend is a long-term goal, but Player2 remains the first-class backend until the core loop is mature.

## Active Next Update

Start with `05 - NPC Memory System.md`.

Do not start diplomacy, plague, settlement combat, or broad world events until the memory and dialogue foundations are demonstrably player-visible.
