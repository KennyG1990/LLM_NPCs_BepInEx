# GATE 2 EVIDENCE — Living World: rumor reaches settler dialogue (2026-07-13 ~05:35, autonomous)

## The chain (real seeded factions -> diplomacy -> world event -> settler rumor -> DIALOGUE)
Save: Tranent. Settler: Mariota Ros (gm_aed826d36d91) — an actual colony settler (the builder in the labor split).

**DB rows (proof of propagation):**
- faction_relations: 75 rows on Tranent (17 real factions seeded from the game's WorldMap.FactionInstances + their pairwise relations).
- Diplomacy rounds executing every 20 min (GameTruthBridge heartbeat: 05:31, 05:51, 06:11, 06:31 ...).
- world_events: 56 on Tranent (incl. "Peace between The River Bandits and Tranent" — a diplomacy-round outcome, reparations 25 gold).
- world_event_knowledge: 1662 rows (rumors propagated to settlers). Mariota holds the River-Bandits-peace rumor + a hailstorm rumor.
- typed_memories: 1662 "Heard a rumor" entries.

**Dialogue context (build_dialogue_prompt_context — the exact builder the game uses for player-chat) for Mariota included:**
- "=== WORLD EVENTS YOU KNOW OF ===\n - [political] Peace between The River Bandits and Tranent ... (you heard this as rumor)\n - [social] ... hailstorm ..."

**TRANSCRIPT (Mariota's generated line via Player2, using her real context):**
> "Aye, the hail did fall hard yesternight, and word spreads oft that the River Bandits hath set aside swords for peace, though I trust the wind to carry rumors true."
> claims: ["hailstorm struck Tranent recently", "rumor of peace with River Bandits"]

## Gate 2 checklist (all met)
- [x] diplomacy round executes with REAL seeded factions (River Bandits et al., 75 faction_relations, 20-min rounds)
- [x] a world event propagates to a settler (Mariota's world_event_knowledge + typed_memories rows)
- [x] appears in that settler's dialogue CONTEXT (her build_dialogue_prompt_context WORLD EVENTS section)
- [x] proven by DB rows PLUS a dialogue transcript (above)
