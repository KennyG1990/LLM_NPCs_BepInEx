# Deferred Backlog

Last updated: 2026-07-05

This backlog records intentional deferrals from the AI Influence design documents. Deferral means "not in the next implementation slice," not "rejected."

## Deferred Until After `05 - NPC Memory System.md`

- Trust-gated disclosure from `01 - AI Dialogues with NPCs.md`.
- Lie detection and contradiction handling from `01 - AI Dialogues with NPCs.md`.
- NPC-initiated conversations from `10 - Additional Systems.md`.
- Romance/intimacy progression from `06 - Romance & Marriage System.md`.
- Death-history generation from `08 - Death History System.md`.

Reason: these need reliable durable memory, stable identity, and retrieval rules first.

## Deferred Until After `01 - AI Dialogues with NPCs.md`

- Dialogue barter execution from `01 - AI Dialogues with NPCs.md`.
- Natural-language multi-step orders from `09 - AI Actions System.md`.
- NPCs discussing world rumors from `02 - Dynamic World Events.md`.
- NPCs exposing faction or war intelligence through dialogue from `03 - AI Diplomacy.md`.

Reason: these need a player-visible conversation surface that can safely persist intent and context.

## Deferred Until After `09 - AI Actions System.md`

- Long-distance travel orders.
- Form-a-band or warband behavior.
- Raid village, besiege settlement, and attack group orders.
- Automatic companion-band management.

Reason: Going Medieval does not naturally expose Bannerlord/X4-style map agents. These need local action/order primitives first, then a design pass to translate impossible concepts into Going Medieval equivalents.

## Deferred Until After `10 - Additional Systems.md`

- Full dynamic world-event propagation.
- Economy ripple effects.
- Recruitment-opportunity detection beyond advisory/dashboard output.
- Settlement/region/faction standing beyond schema and prompt grounding.

Reason: these need grounded entity tracking for settlements, factions, goods, regions, and visits.

## Deferred Until After `02 - Dynamic World Events.md`

- AI diplomacy rounds.
- War/peace/alliance/trade-pact state changes.
- Tribute and reparations.
- Banishment and pardon systems.
- War-fatigue simulation.
- Disease outbreaks as regional rumors.

Reason: these should be event-driven, not isolated tables with generated prose.

## Deferred Until After `03 - AI Diplomacy.md`

- Faction intervention in combat.
- Political consequences from romance, betrayal, banishment, or death.
- Trade pact impact on prices or caravan behavior.

Reason: these depend on faction state that does not exist yet.

## Deferred Until After `06 - Romance & Marriage System.md`

- Courtship initiative.
- AI-managed proposals and betrothals.
- Relationship decay affecting combat loyalty.
- Marriage-driven inheritance/faction consequences.

Reason: current relationship data exists, but romance needs its own lifecycle and visible player-facing interactions.

## Deferred Until After `08 - Death History System.md`

- Battle casualty obituaries.
- Graveside story display.
- Death-history impact on diplomacy or romance.
- Memorialized settler milestones in later dialogue.

Reason: death history should be generated from real accumulated milestones, not generic obituary text.

## Deferred Until After `04 - Disease & Plague System.md`

- Disease affecting combat stats.
- Infected travelers spreading disease between settlements.
- Apothecary inventory and herb economy.
- AI faction members traveling to infirmaries.
- Player travel restrictions while infected.

Reason: these require stable disease state, treatment state, and world-event propagation first.

## Deferred Until After `07 - Settlement Combat System.md`

- Defender spawning.
- Civilian panic behavior.
- Location-transition fights.
- Player capture/rescue consequences.
- Raiding/burning aftermath actions.

Reason: these are high-risk direct game-simulation interventions and need safe reflection/API proof before implementation.

## Long-Term Backend Deferrals

- OpenRouter backend restoration.
- DeepSeek backend support.
- Ollama backend support.
- KoboldCpp backend support.
- Optional TTS voice acting.

Reason: Player2 is the current proven backend. Multi-backend support should wait until memory, dialogue, and action semantics are stable.

## Long-Term UX Deferrals

- Polished player notification stack.
- In-game colony adviser feed.
- Player-facing world event journal.
- Rumor map or diplomacy map.
- Disease/quarantine overlays.
- Combat aftermath report screen.

Reason: the current UI surfaces are development-grade. Polished UI should follow stable data contracts.
