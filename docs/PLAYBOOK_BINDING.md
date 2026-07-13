# Playbook → Actuator Binding Map (2026-07-12)

**Source:** Ken's `F:\Downskies\going-medieval-ai-playbook.md` (the strategic rule layer).
**Binds to:** `docs/ACTUATOR_CATALOG.md` (the validated action space) + the Read Model (telemetry/census/alerts).
**Status legend:** ✅ BOUND & LIVE · 🟡 STAGED (built, awaiting live validation) · 📋 SPECCED (design exists) · ❌ UNBOUND (needs new read/actuator).

## §2 Colony economy
| Playbook rule | Binding | Status |
|---|---|---|
| tick-0 research queue [Architecture, Agriculture, Tailoring] | ResearchPlanner.Needs order — implemented tonight in exactly this order | 🟡 |
| food buffer < 12×3×settlers → raise farming/hunting | Read: ColonyAlerts.LastNutrition. Crisis (1-day, pop×12) staged; **3-day WARNING tier = gap** (raise food jobs pre-crisis, no suspensions) | 🟡 + gap |
| cellar depth<2 or unwalled → spoilage/rat risk | V3 cellar spec carries walls/depth/flood rules verbatim | 📋 |
| spring/autumn soil room unwalled → flood risk | Same V3 spec (marsh flooding noted) | 📋 |
| malnutrition_stage > 0 → deprioritize combat/skilled | ❌ no malnutrition read yet (decompile lookup: malnutrition stage field on settler) | ❌ |
| book stock < next research cost → raise book_writing | ProductionPlanner.Tick(TableId, "basic_research_book") keeps the queue fed (cruder than cost-aware; cost-aware = gap) | ✅ partial |
| flimsy starting clothes → tailoring | Sewing station + winter/summer clothes queues | 🟡 |

## §3 Structural
| Rule | Binding | Status |
|---|---|---|
| stability ≤ 0 → reject placement | Game's own CanPlace refuses; our V2 upper-floor spec adds the pre-check (stability 4, −1/step) | 📋 (V2) |
| span > beam max → support wall | V2 spec (beams 15 wood, post-Architecture) | 📋 |
| library = table + 2 wall bookshelves, no beds/stations | Room-type formation slice (wall-mount placement semantics need one live probe) | 📋 |
| underground span > 3 tiles, no beam → cave-in risk | V3 cellar spec (beams every ~3 tiles) | 📋 |

## §4 Combat/defense (the RTS core)
| Rule | Binding | Status |
|---|---|---|
| maintain controlled opening / never full-turtle (trebuchet risk) | DefenseBuilder palisade has exactly ONE gate by design | 🟡 |
| moat stops AI pathing | Marsh palisade skips water cells — the moat is free on wet maps | 🟡 |
| merlon+window archer positions | Defense v2 (research-gated) | 📋 |
| prefer ranged over melee | EquipManager (hunters get ranged); combat-wide preference = gap | ✅ partial |
| blood→0 projected → retreat & tend | ❌ needs blood/bleed read (settler health panel fields — decompile lookup) + tend-order actuator (PrioritiseTending menu item exists in game: `AdditionalMenuItems`) | ❌ |
| post-undraft melee re-order bug | Draft-then-order slice (MovementOrders found the order slot; draft toggle is the missing prerequisite — banked) | 📋 |
| wolf raid in winter → shoot from walls | Emergent from palisade + ranged preference once both live | 🟡 |

## §5 Medical
| Rule | Binding | Status |
|---|---|---|
| unconscious → carry to bed | ❌ (game does this via jobs when beds exist; explicit actuator ungrounded) | ❌ |
| wounds + infirmary → route | Infirmary room formation = room-type slice; P9 dashboard treat/quarantine routes are LIVE for the simulation layer | 📋 / ✅ (sim) |
| rotten ingredients → reject recipe | ❌ needs recipe-ingredient read | ❌ |

## §6 Temperature/seasonal
| Rule | Binding | Status |
|---|---|---|
| winter → winter clothes | Sewing queues make the sets; auto-equip = game's own seasonal profiles (1.0 feature — verify the game handles it once clothes EXIST) | 🟡 |
| cellar floor material preferences | V3 spec (wicker/grated coldest) | 📋 |

## §7 Trade/diplomacy/Renown
| Rule | Binding | Status |
|---|---|---|
| alignment ≥75 → weapon trade | Read exists (game alignment); acting = trade slice (unbuilt) | ❌ |
| skewed trades raise alignment | Same trade slice | ❌ |
| renown 100 → grand objective decision point | Late-game; flag-only rule — cheap to add to alerts once renown read exists | ❌ |
| P6 faction sim (our own layer) | LIVE: rounds, menus, wars, treaties, fatigue, proclamations→rumor | ✅ |

## §8 Unresolved values — resolution paths
- mood breakpoints / combat formulas / malnutrition stages / blood-loss rates: **decompile lookups** (GameApiIndex — one grep each, extract when a rule needs to branch).
- faction co-spawn roster: read live from FactionsController at runtime (roster feed slice).
- Read Model field names + actuator signatures: **RESOLVED — this document + ACTUATOR_CATALOG.md are that binding.**

## Priority gaps this map surfaces (ordered)
1. **3-day food WARNING tier** (one rule, reads exist) — trivial, high value.
2. **Draft-then-order** (unlocks §4 combat orders + movement leg).
3. **Malnutrition + blood reads** (decompile lookups) → medical/combat priority rules.
4. Room-type formation (library/infirmary) — one live probe session.
5. Trade actuator (merchant barter via dialogue = doc-01 trading too).
