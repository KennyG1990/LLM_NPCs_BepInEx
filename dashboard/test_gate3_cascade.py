"""GATE 3 CASCADE — the doc-11 scenario-3 chain proven end-to-end offline,
in one continuous flow, against in-memory SQLite (no game, no LLM, no live DB).

Chain: real raid (link 0, live in-game) -> report_raid -> world event (link 2)
-> propagate to settlers (link 3) -> known_events serves it to the dialogue
prompt (link 4) -> diplomatic consequence: relation/war state (link 5).

Link 0 (the in-game raid firing) and the LLM actually SPEAKING the rumor are
the only pieces this can't cover — those are the live Gate 3 run. Everything
between is proven here, so the live attempt only has to trigger the front.

The ctx stub mirrors the real insert_typed_memory so the rumor lands in a
typed_memories row exactly as production does — the same row build_dialogue_
prompt_context reads."""

import sqlite3
import sys

import gm_systems as gs

SAVE = "gate3_save"
SETTLERS = ["s_alfred", "s_mariota", "s_donald"]


class _Ctx:
    @staticmethod
    def clamp(v, lo, hi):
        return max(lo, min(hi, v))

    @staticmethod
    def insert_typed_memory(conn, save_id, settler_id, category, content,
                            importance, metadata=None):
        # Match production's typed_memories shape so the rumor is queryable.
        conn.execute("""
            INSERT INTO typed_memories (save_id, settler_id, category, tier,
                event_type, content, importance, confidence, source, created_at, last_used_at)
            VALUES (?, ?, ?, 'recent', 'event', ?, ?, 1.0, 'game', ?, ?)
        """, (save_id, settler_id, category, content, importance,
              gs._now(), gs._now()))


CTX = _Ctx()


def fresh():
    conn = sqlite3.connect(":memory:")
    conn.row_factory = sqlite3.Row
    gs.ensure_tables(conn)
    conn.execute("""CREATE TABLE IF NOT EXISTS npc_memory_profiles (
        save_id TEXT, settler_id TEXT, display_name TEXT, role TEXT, traits TEXT,
        first_seen REAL, last_seen REAL)""")
    conn.execute("""CREATE TABLE IF NOT EXISTS typed_memories (
        id INTEGER PRIMARY KEY AUTOINCREMENT, save_id TEXT, settler_id TEXT,
        category TEXT, tier TEXT, event_type TEXT, content TEXT, importance INTEGER,
        confidence REAL, is_secret INTEGER DEFAULT 0, source TEXT, source_table TEXT,
        source_id TEXT, created_at REAL, last_used_at REAL, metadata_json TEXT)""")
    for s in SETTLERS:
        conn.execute("INSERT INTO npc_memory_profiles (save_id, settler_id) VALUES (?, ?)",
                     (SAVE, s))
    for f in ("bandits", "player", "osric"):
        gs.ensure_entity(conn, SAVE, "faction", f)
    return conn


def _war_events_for(conn, settler):
    return [e for e in gs.known_events(conn, SAVE, settler)
            if e["event_type"] == "military"]


def test_raid_by_already_at_war_faction_still_propagates():
    """THE GAP THIS TEST WAS BORN FROM: Tranent's hostile factions seed already
    at war (relation -1.0). A raid by them must STILL become a rumor a settler
    can voice — not vanish into a log line."""
    conn = fresh()
    # bandits already at war with the player (the live Tranent condition).
    gs.apply_diplomacy_action(CTX, conn, SAVE, "bandits", "player", "declare_war")
    before = len(_war_events_for(conn, SETTLERS[0]))

    res = gs.report_raid(CTX, conn, SAVE, "bandits", "player", casualties_target=2)
    assert res["ok"] and res["world_event_id"], res
    assert not res["escalated_to_war"], "already at war — no NEW declaration expected"

    # link 2: a raid world event exists
    ev = conn.execute("SELECT * FROM world_events WHERE id = ?",
                      (res["world_event_id"],)).fetchone()
    assert ev and ev["event_type"] == "military" and "Raid" in ev["title"], dict(ev) if ev else None

    # link 3: every settler learned it
    reached = conn.execute(
        "SELECT COUNT(*) c FROM world_event_knowledge WHERE save_id=? AND event_id=?",
        (SAVE, res["world_event_id"])).fetchone()["c"]
    assert reached == len(SETTLERS), reached

    # link 4: known_events serves the raid to the dialogue prompt for a settler
    after = _war_events_for(conn, SETTLERS[0])
    assert len(after) == before + 1, (before, len(after))
    assert any("Raid" in e["title"] for e in after)

    # the rumor is a real typed_memories row (what the prompt builder reads)
    mem = conn.execute(
        "SELECT content FROM typed_memories WHERE save_id=? AND settler_id=? "
        "AND content LIKE '%Raid%'", (SAVE, SETTLERS[0])).fetchone()
    assert mem and "bandits" in mem["content"], dict(mem) if mem else None
    print("  [1/3] already-at-war raid -> world event -> 3 settlers -> known_events + typed_memory  OK")


def test_neutral_raider_escalates_and_propagates_both():
    """A NEUTRAL faction turning raider: the raid event AND the war-declaration
    event both reach settlers (two military rumors), and the consequence is a
    real war state."""
    conn = fresh()
    rel0 = gs.get_relation(conn, SAVE, "osric", "player")
    conn.execute("UPDATE faction_relations SET relation=-0.4 WHERE id=?", (rel0["id"],))

    res = gs.report_raid(CTX, conn, SAVE, "osric", "player", casualties_target=4)
    assert res["escalated_to_war"], res  # -0.4 - 0.25 = -0.65 <= -0.6

    # consequence (link 5): declared war, relation floored
    rel = gs.get_relation(conn, SAVE, "osric", "player")
    assert rel["state"] == "war" and float(rel["relation"]) == -1.0, dict(rel)

    # BOTH a raid event and a war event propagated -> settler knows two militaries
    militaries = _war_events_for(conn, SETTLERS[1])
    titles = sorted(e["title"] for e in militaries)
    assert any("Raid" in t for t in titles) and any(t.startswith("War") for t in titles), titles
    print("  [2/3] neutral raider -> raid+war events -> settler holds both rumors -> war state  OK")


def test_diplomacy_actions_unregressed():
    """The refactor (extracted _propagate_to_all_settlers) must not change the
    existing propagation behaviour of ordinary diplomacy actions."""
    conn = fresh()
    res = gs.apply_diplomacy_action(CTX, conn, SAVE, "osric", "player", "trade_pact")
    assert res["ok"] and res["world_event_id"]
    reached = conn.execute(
        "SELECT COUNT(*) c FROM world_event_knowledge WHERE event_id=?",
        (res["world_event_id"],)).fetchone()["c"]
    assert reached == len(SETTLERS), reached
    assert any("Trade pact" in e["title"] for e in gs.known_events(conn, SAVE, SETTLERS[2]))
    print("  [3/3] ordinary diplomacy action still propagates to all settlers (no regression)  OK")


def main():
    tests = [test_raid_by_already_at_war_faction_still_propagates,
             test_neutral_raider_escalates_and_propagates_both,
             test_diplomacy_actions_unregressed]
    for t in tests:
        t()
    print(f"ALL GREEN ({len(tests)}/{len(tests)}) — Gate 3 cascade proven end-to-end offline")


if __name__ == "__main__":
    try:
        main()
    except AssertionError as e:
        print("FAILED:", e)
        sys.exit(1)
