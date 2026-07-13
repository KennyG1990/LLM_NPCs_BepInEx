"""P9 disease validation — spread, seasonal onset, quarantine, progression,
death integration. Offline, in-memory SQLite, seeded RNG. Doc 04 acceptance."""

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


def fresh(pop=6):
    conn = sqlite3.connect(":memory:")
    conn.row_factory = sqlite3.Row
    gs.ensure_tables(conn)
    conn.execute("""CREATE TABLE IF NOT EXISTS npc_memory_profiles (
        save_id TEXT, settler_id TEXT, display_name TEXT, role TEXT, traits TEXT,
        first_seen REAL, last_seen REAL)""")
    conn.execute("""CREATE TABLE IF NOT EXISTS typed_memories (
        id INTEGER PRIMARY KEY AUTOINCREMENT, save_id TEXT, settler_id TEXT,
        category TEXT, content TEXT, importance INTEGER, created_at REAL)""")
    now = gs._now()
    for i in range(pop):
        conn.execute("INSERT INTO npc_memory_profiles (save_id, settler_id, first_seen, last_seen) "
                     "VALUES (?, ?, ?, ?)", (SAVE, f"s{i}", now, now))
    return conn


def _sick(conn, sid, disease="fever", quarantined=0):
    gs.infect(CTX, conn, SAVE, sid, disease, season="winter")
    conn.execute("UPDATE disease_states SET stage='sick', quarantined=? "
                 "WHERE save_id=? AND settler_id=?", (quarantined, SAVE, sid))


def test_spread_in_winter():
    conn = fresh()
    _sick(conn, "s0")
    infections = gs.disease_spread(CTX, conn, SAVE, "winter", rng=random.Random(7))
    assert infections, "winter spread with 5 contacts at 30% produced nothing (seed 7)"
    assert all(i["from"] == "s0" for i in infections)


def test_quarantine_cuts_spread():
    runs = 40
    open_total, quar_total = 0, 0
    for seed in range(runs):
        c1 = fresh(); _sick(c1, "s0")
        open_total += len(gs.disease_spread(CTX, c1, SAVE, "winter", rng=random.Random(seed)))
        c2 = fresh(); _sick(c2, "s0", quarantined=1)
        quar_total += len(gs.disease_spread(CTX, c2, SAVE, "winter", rng=random.Random(seed)))
    assert quar_total < open_total / 2, (open_total, quar_total)


def test_summer_quieter_than_winter():
    runs = 40
    winter, summer = 0, 0
    for seed in range(runs):
        c1 = fresh(); _sick(c1, "s0")
        winter += len(gs.disease_spread(CTX, c1, SAVE, "winter", rng=random.Random(seed)))
        c2 = fresh(); _sick(c2, "s0")
        summer += len(gs.disease_spread(CTX, c2, SAVE, "summer", rng=random.Random(seed)))
    assert summer < winter, (winter, summer)


def test_seasonal_onset_fires():
    conn = fresh(pop=20)
    onsets = []
    for seed in range(30):
        onsets.extend(gs.seasonal_onset(CTX, conn, SAVE, "winter", rng=random.Random(seed)))
        if onsets:
            break
    assert onsets, "no seasonal onset in 30 seeded winters over 20 settlers"
    assert onsets[0]["disease"] in gs.SEASON_DISEASES["winter"]


def test_untreated_plague_kills_and_records_death():
    conn = fresh()
    gs.infect(CTX, conn, SAVE, "s1", "plague", season="winter")
    for _ in range(4):   # incubating -> sick -> critical -> dead
        gs.disease_tick(CTX, conn, SAVE)
    stage = conn.execute("SELECT stage FROM disease_states WHERE save_id=? AND settler_id=?",
                         (SAVE, "s1")).fetchone()["stage"]
    assert stage == "dead", stage
    rec = conn.execute("SELECT cause FROM death_records WHERE save_id=? AND settler_id=?",
                       (SAVE, "s1")).fetchone()
    assert rec and rec["cause"] == "plague", dict(rec) if rec else None


def test_treated_recovers_with_immunity_that_blocks_reinfection():
    conn = fresh()
    gs.infect(CTX, conn, SAVE, "s2", "fever", season="winter")
    conn.execute("UPDATE disease_states SET treated=1 WHERE save_id=? AND settler_id=?", (SAVE, "s2"))
    for _ in range(4):
        gs.disease_tick(CTX, conn, SAVE)
    stage = conn.execute("SELECT stage FROM disease_states WHERE save_id=? AND settler_id=? "
                         "ORDER BY id DESC LIMIT 1", (SAVE, "s2")).fetchone()["stage"]
    assert stage in ("recovering", "recovered"), stage
    r = gs.infect(CTX, conn, SAVE, "s2", "fever", season="winter")
    assert not r["infected"] and "immunity" in r["why"], r


def test_outbreak_event_at_threshold():
    conn = fresh()
    last = None
    for sid in ("s0", "s1", "s2"):
        last = gs.infect(CTX, conn, SAVE, sid, "dysentery", season="autumn")
    assert last["outbreak_event_id"], last
    ev = conn.execute("SELECT title FROM world_events WHERE id=?",
                      (last["outbreak_event_id"],)).fetchone()
    assert "dysentery" in ev["title"], dict(ev)


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
