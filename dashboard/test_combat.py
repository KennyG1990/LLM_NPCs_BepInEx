"""P10 combat-direction validation — verdicts, stances, interventions,
diplomacy feed, casualty records, rumor propagation. Offline. Doc 07."""

import sqlite3
import sys

import gm_systems as gs

SAVE = "test_save"


class _Ctx:
    @staticmethod
    def clamp(v, lo, hi):
        return max(lo, min(hi, v))

    @staticmethod
    def insert_typed_memory(conn, save_id, settler_id, category, content,
                            importance, metadata=None):
        conn.execute("INSERT INTO typed_memories (save_id, settler_id, category, content, "
                     "importance, created_at) VALUES (?,?,?,?,?,?)",
                     (save_id, settler_id, category, content, importance, gs._now()))


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
        category TEXT, content TEXT, importance INTEGER, created_at REAL)""")
    conn.execute("""CREATE TABLE IF NOT EXISTS relationships (
        save_id TEXT, subject TEXT, object TEXT,
        friendship REAL DEFAULT 0, romance REAL DEFAULT 0, rivalry REAL DEFAULT 0,
        trust REAL DEFAULT 0.5, attraction REAL DEFAULT 0, fear REAL DEFAULT 0,
        resentment REAL DEFAULT 0, standing TEXT DEFAULT 'neutral',
        relationship_type TEXT DEFAULT 'strangers', updated_at REAL DEFAULT 0)""")
    now = gs._now()
    for sid in ("mara", "gunnar", "wilhelm"):
        conn.execute("INSERT INTO npc_memory_profiles (save_id, settler_id, first_seen, last_seen) "
                     "VALUES (?, ?, ?, ?)", (SAVE, sid, now, now))
    for f in ("player", "blackfen", "osric"):
        gs.ensure_entity(conn, SAVE, "faction", f)
    return conn


def _trust(conn, settler, value):
    conn.execute("INSERT INTO relationships (save_id, subject, object, trust, updated_at) "
                 "VALUES (?, ?, 'player', ?, ?)", (SAVE, settler, value, gs._now()))


def test_stances_follow_trust():
    conn = fresh()
    _trust(conn, "mara", 0.9)     # loyal — stands with you
    _trust(conn, "gunnar", 0.1)   # resentful — turns on you
    r = gs.classify_combat_incident(CTX, conn, SAVE, "dialogue_hostility",
                                    "blackfen", "player", location="great hall",
                                    participants=["mara", "gunnar", "wilhelm"])
    stances = {s["settler_id"]: s["stance"] for s in r["verdict"]["stances"]}
    assert stances["mara"] == "support_player", stances
    assert stances["gunnar"] == "oppose_player", stances
    assert stances["wilhelm"] == "neutral", stances


def test_defenders_at_gate_and_panic_on_casualties():
    conn = fresh()
    r = gs.classify_combat_incident(CTX, conn, SAVE, "raid", "blackfen", "player",
                                    location="gatehouse", casualties=["wilhelm"])
    assert r["verdict"]["defenders_needed"] and r["verdict"]["defender_type"] == "militia"
    assert r["verdict"]["civilian_panic"]


def test_allied_faction_intervenes():
    conn = fresh()
    rel = gs.get_relation(conn, SAVE, "player", "osric")
    conn.execute("UPDATE faction_relations SET relation=1.0, state='alliance' WHERE id=?", (rel["id"],))
    r = gs.classify_combat_incident(CTX, conn, SAVE, "raid", "blackfen", "player",
                                    location="gate")
    ivs = r["verdict"]["interventions"]
    assert any(i["faction"] == "osric" and i["side"] == "defender" for i in ivs), ivs
    assert "arrival_line" in ivs[0] and ivs[0]["arrival_line"]


def test_combat_feeds_diplomacy_and_deaths_and_rumor():
    conn = fresh()
    r = gs.classify_combat_incident(CTX, conn, SAVE, "raid", "blackfen", "player",
                                    location="courtyard", casualties=["wilhelm", "gunnar"])
    assert r["diplomacy"] and r["diplomacy"]["ok"], r["diplomacy"]
    rel = gs.get_relation(conn, SAVE, "blackfen", "player")
    assert float(rel["relation"]) < 0, dict(rel)
    deaths = conn.execute("SELECT COUNT(*) AS c FROM death_records WHERE save_id=?",
                          (SAVE,)).fetchone()["c"]
    assert deaths == 2, deaths
    known = conn.execute("SELECT COUNT(*) AS c FROM world_event_knowledge WHERE save_id=? "
                         "AND event_id=?", (SAVE, r["world_event_id"])).fetchone()["c"]
    assert known == 3, f"all 3 settlers should hear of the battle, got {known}"


def test_nonfaction_brawl_no_diplomacy():
    conn = fresh()
    r = gs.classify_combat_incident(CTX, conn, SAVE, "dialogue_hostility",
                                    "gunnar", "mara", location="tavern")
    assert r["diplomacy"] is None, r["diplomacy"]


if __name__ == "__main__":
    fails = 0
    for name, fn in sorted([(k, v) for k, v in globals().items() if k.startswith("test_")]):
        try:
            fn()
            print(f"PASS {name}")
        except AssertionError as e:
            fails += 1
            print(f"FAIL {name}: {e}")
        except Exception as e:
            fails += 1
            print(f"ERROR {name}: {type(e).__name__}: {e}")
    print(f"\n{'ALL GREEN' if fails == 0 else str(fails) + ' FAILURES'}")
    sys.exit(1 if fails else 0)
