"""P5 event-lifecycle validation — aging transitions, war-truth early
resolution, update trail. Offline, injected clock. Doc 02 acceptance."""

import sqlite3
import sys

import gm_systems as gs

SAVE = "test_save"
T0 = 1_000_000.0
DAY = 86400.0


class _Ctx:
    @staticmethod
    def clamp(v, lo, hi):
        return max(lo, min(hi, v))

    @staticmethod
    def insert_typed_memory(conn, save_id, settler_id, category, content,
                            importance, metadata=None):
        pass


CTX = _Ctx()


def fresh():
    conn = sqlite3.connect(":memory:")
    conn.row_factory = sqlite3.Row
    gs.ensure_tables(conn)
    conn.execute("""CREATE TABLE IF NOT EXISTS npc_memory_profiles (
        save_id TEXT, settler_id TEXT, display_name TEXT, role TEXT, traits TEXT,
        first_seen REAL, last_seen REAL)""")
    for f in ("blackfen", "player"):
        gs.ensure_entity(conn, SAVE, "faction", f)
    return conn


def _event(conn, event_type="social", title="A tale", affected=None, created=T0):
    conn.execute("""INSERT INTO world_events (save_id, event_type, title, description,
        origin_entity, affected_json, confidence, status, created_at, updated_at)
        VALUES (?, ?, ?, '', '', ?, 0.9, 'active', ?, ?)""",
                 (SAVE, event_type, title,
                  gs.json.dumps(affected or []), created, created))
    return conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]


def _status(conn, eid):
    return conn.execute("SELECT status FROM world_events WHERE id=?", (eid,)).fetchone()["status"]


def test_young_event_untouched():
    conn = fresh()
    eid = _event(conn)
    t = gs.events_evolve(CTX, conn, SAVE, now=T0 + DAY)
    assert not t and _status(conn, eid) == "active"


def test_full_lifecycle_with_update_trail():
    conn = fresh()
    eid = _event(conn)
    assert gs.events_evolve(CTX, conn, SAVE, now=T0 + 3 * DAY)[0]["to"] == "evolving"
    assert gs.events_evolve(CTX, conn, SAVE, now=T0 + 7 * DAY)[0]["to"] == "resolved"
    assert gs.events_evolve(CTX, conn, SAVE, now=T0 + 15 * DAY)[0]["to"] == "expired"
    updates = gs._loads(conn.execute("SELECT updates_json FROM world_events WHERE id=?",
                                     (eid,)).fetchone()["updates_json"], [])
    assert len(updates) == 3 and all(u["note"] for u in updates), updates


def test_war_event_resolves_when_war_ends():
    conn = fresh()
    rel = gs.get_relation(conn, SAVE, "blackfen", "player")
    conn.execute("UPDATE faction_relations SET state='war', relation=-1.0 WHERE id=?", (rel["id"],))
    eid = _event(conn, "military", "War: blackfen vs player", ["blackfen", "player"])
    # war ongoing + young: stays active
    assert not gs.events_evolve(CTX, conn, SAVE, now=T0 + DAY)
    # peace concluded -> resolves immediately regardless of age
    conn.execute("UPDATE faction_relations SET state='peace', relation=0.1 WHERE id=?", (rel["id"],))
    t = gs.events_evolve(CTX, conn, SAVE, now=T0 + DAY)
    assert t and t[0]["to"] == "resolved", t
    assert _status(conn, eid) == "resolved"


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
