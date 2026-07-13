"""P7 romance validation — autonomous bond formation, initiative proposals,
milestone world events, decay. Offline, seeded RNG. Doc 06 acceptance."""

import random
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
        relationship_type TEXT DEFAULT 'strangers')""")
    now = gs._now()
    for sid, traits in (("elgiva", "sanguine, gentle"), ("gunnar", "dour"),
                        ("mara", "brave"), ("wilhelm", "meek")):
        conn.execute("INSERT INTO npc_memory_profiles (save_id, settler_id, traits, first_seen, last_seen) "
                     "VALUES (?, ?, ?, ?, ?)", (SAVE, sid, traits, now, now))
    return conn


def _attract(conn, a, b, romance=0.5, attraction=0.5):
    a, b = sorted((a, b))
    conn.execute("INSERT INTO relationships (save_id, subject, object, romance, attraction) "
                 "VALUES (?, ?, ?, ?, ?)", (SAVE, a, b, romance, attraction))


def test_no_signal_no_bond():
    conn = fresh()
    for seed in range(10):
        r = gs.romance_autonomous_tick(CTX, conn, SAVE, rng=random.Random(seed))
        assert not r["interactions"], r["interactions"]


def test_attracted_pair_progresses_to_courting():
    conn = fresh()
    _attract(conn, "elgiva", "gunnar", 0.7, 0.7)
    stages = set()
    for seed in range(60):
        r = gs.romance_autonomous_tick(CTX, conn, SAVE, rng=random.Random(seed))
        for i in r["interactions"]:
            stages.add(i["stage"])
    assert "courting" in stages, stages


def test_initiative_trait_reaches_marriage_and_village_hears():
    conn = fresh()
    _attract(conn, "elgiva", "mara", 0.9, 0.9)   # elgiva sanguine, mara brave — both initiative
    milestones = []
    for seed in range(200):
        r = gs.romance_autonomous_tick(CTX, conn, SAVE, rng=random.Random(seed))
        milestones.extend(r["milestones"])
        if any(m["stage"] == "married" for m in milestones):
            break
    assert any(m["stage"] == "betrothed" for m in milestones), milestones
    assert any(m["stage"] == "married" for m in milestones), milestones
    wed = next(m for m in milestones if m["stage"] == "married")
    known = conn.execute("SELECT COUNT(*) AS c FROM world_event_knowledge WHERE save_id=? AND event_id=?",
                         (SAVE, wed["world_event_id"])).fetchone()["c"]
    assert known >= 4, f"village of 4 should hear of the wedding, got {known}"


def test_no_initiative_trait_no_proposal():
    conn = fresh()
    _attract(conn, "gunnar", "wilhelm", 0.9, 0.9)   # dour + meek: nobody proposes
    for seed in range(200):
        r = gs.romance_autonomous_tick(CTX, conn, SAVE, rng=random.Random(seed))
        assert not any(m["stage"] in ("betrothed", "married") for m in r["milestones"]), \
            "betrothal without an initiative trait"


def test_neglect_cools():
    conn = fresh()
    gs.romance_interact(CTX, conn, SAVE, "elgiva", "gunnar", "courtship")
    gs.romance_interact(CTX, conn, SAVE, "elgiva", "gunnar", "kiss")
    conn.execute("UPDATE romance_states SET last_interaction = ? WHERE save_id = ?",
                 (gs._now() - 40 * 86400, SAVE))
    decayed = gs.romance_decay(CTX, conn, SAVE)
    assert decayed and decayed[0]["intimacy"] < 0.18, decayed


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
