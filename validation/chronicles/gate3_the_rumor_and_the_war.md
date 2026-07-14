# GATE 3 — "The Rumor and the War" (doc-11 scenario 1) captured end-to-end (2026-07-13 ~05:40, autonomous)

A complete scenario slice: a real regional situation runs itself, produces a consequence, and a colony settler hears
and voices it — documented from DB rows + log evidence + transcript. Maps to doc 11 §1 "The Rumor and the War"
(AI Diplomacy running on its own; war/peace shifts; rumors spread to your people).

## The chain (each link with its evidence)

1. **Real in-game footing — war with a real faction.** Tranent's factions were seeded from the game's OWN roster
   (WorldMap.Data.FactionInstances -> /api/diplomacy/seed): 17 factions, 75 faction_relations. The bandit clans
   (incl. The River Bandits) seeded HOSTILE -> state=war (relation -1.0), matching their in-game friendliness.

2. **Autonomous diplomacy (the living world runs itself).** GameTruthBridge fires a diplomacy round every 20 real
   min (log: rounds at 05:31, 05:51, 06:11, 06:31 ...). War-fatigue accumulates each round the war persists.

3. **The consequence — a relation change.** diplomacy_log round_no 18: actor "The River Bandits" action
   **war_fatigue_peace** target "Tranent" -> "The River Bandits and Tranent lay down arms. Reparations of 25 gold
   shall be paid." faction_relations NOW: "The River Bandits <-> Tranent: relation 0.0, state=PEACE" (was war -1.0).
   THE CONSEQUENCE = relation flips war->peace + 25g reparations. (doc 11 §5 economic reparations also touched.)

4. **World event created.** world_events: "Peace between The River Bandits and Tranent" (political).

5. **Propagated to a settler.** world_event_knowledge: Mariota Ros (gm_aed826d36d91, a real colony settler) learned
   it as a rumor; typed_memories holds the "Heard a rumor" entry. (1662 such rows colony-wide.)

6. **Voiced in dialogue (the transcript).** Her real dialogue context (build_dialogue_prompt_context — the game's own
   builder) carried the rumor; generated via Player2 on that context:
   > "Aye, the hail did fall hard yesternight, and word spreads oft that the River Bandits hath set aside swords for
   >  peace, though I trust the wind to carry rumors true."
   > claims: ["hailstorm struck Tranent recently", "rumor of peace with River Bandits"]
   (She also voices the game_event_hailstorm — a REAL in-game environmental event the mod captured, made a world
   event, and propagated: a second real-event->world-event->rumor->dialogue chain in the same breath.)

## Believability
A settler skeptically relaying regional news she can't fully verify ("I trust the wind to carry rumors true") —
period-appropriate voice, grounded in events that actually happened in her world. This is what doc 11 §1 asks for.

## Gate 3 checklist
- [x] a real in-game event (game_event_hailstorm; + a real-faction war footing)
- [x] -> world event (Peace between River Bandits and Tranent; the hailstorm world event)
- [x] -> rumor in dialogue (Mariota's line, above)
- [x] -> a consequence: RELATION CHANGE war->peace + reparations (faction_relations + diplomacy_log round 18)
- [x] documented from log evidence + DB rows + transcript
- [ ] KEN EYEBALLS THE TRANSCRIPT and agrees a player would find it believable  <-- the one human gate (his to give)

## Evidence artifacts
- validation/chronicles/gate2_transcript.md (Gate 2), this file (Gate 3)
- DB: npc_memory.sqlite3 (faction_relations, diplomacy_log, world_events, world_event_knowledge, typed_memories, save=Tranent)
- Log: GameTruthBridge diplomacy-round heartbeat; colony screenshots gate1_shots/working_colony.png etc.

---

## SECOND SLICE — doc-11 §2 "The Winter Plague" (disease reaches dialogue) + multi-system synthesis
The living world exercises MANY doc-11 systems live on Tranent (world_events): AI Diplomacy (33 war_fatigue_peace,
33 make_peace, 3 trade_pact), Dynamic World Events (hailstorm, bard/beggar/trader visits, new settlers), and
**Disease & Plague** (Outbreak of cold, Outbreak of fever). Second transcript — Alfred Benson (a colony settler) on
his real dialogue context, voicing the disease outbreak AND weaving in the hailstorm + bandits (emergent synthesis
across three systems, exactly what AI Influence should produce):
> "Aye, I've heard whispers of a strange malaise spreading - some say it's the lingering chill from that recent
>  hailstorm, others blame the roving bandits' camps; the healers are busier than ever."
> claims: ["There is an illness affecting settlers recently", "Possible causes include the hailstorm or contact with bandits"]
This shows the AI Influence integration (doc 00: systems reacting to one another) is LIVE, not just single-system.
