"""P6 diplomacy validation — drives the MERGED implementation in gm_systems.py
(the reference module gm_diplomacy.py was retired after reconciliation found
the existing substrate; see BACKLOG 2026-07-12 ~20:45). Offline, in-memory
SQLite, no game, no LLM. Every case is a declared acceptance criterion."""

import sqlite3
import sys

import gm_systems as gs

SAVE = "test_save"


class _Ctx:
    """Minimal ctx stub — diplomacy paths only touch these members."""
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
    # npc_memory_profiles belongs to dashboard_server's main schema (exists in
    # production); tests provide the minimal shape the diplomacy paths touch.
    conn.execute("""CREATE TABLE IF NOT EXISTS npc_memory_profiles (
        save_id TEXT, settler_id TEXT, display_name TEXT, role TEXT, traits TEXT,
        first_seen REAL, last_seen REAL)""")
    for f in ("player", "blackfen", "york", "shewolf"):
        gs.ensure_entity(conn, SAVE, "faction", f)
    return conn


def _set_relation(conn, a, b, value):
    rel = gs.get_relation(conn, SAVE, a, b)
    conn.execute("UPDATE faction_relations SET relation = ? WHERE id = ?", (value, rel["id"]))


def test_neutral_by_default():
    conn = fresh()
    rel = gs.get_relation(conn, SAVE, "player", "blackfen")
    assert rel["state"] == "peace" and float(rel["relation"]) == 0.0


def test_menu_thresholds():
    conn = fresh()
    menu = gs.diplomacy_legal_moves(conn, SAVE, "york")
    assert not any(m["kind"] == "declare_war" for m in menu), "war legal at neutral?!"
    _set_relation(conn, "york", "shewolf", -0.45)
    menu = gs.diplomacy_legal_moves(conn, SAVE, "york")
    assert any(m["kind"] == "declare_war" and m["target"] == "shewolf" for m in menu)
    _set_relation(conn, "york", "player", 0.5)
    menu = gs.diplomacy_legal_moves(conn, SAVE, "york")
    assert any(m["kind"] == "form_alliance" and m["target"] == "player" for m in menu)


def test_raid_feed_and_escalation():
    conn = fresh()
    r1 = gs.report_raid(CTX, conn, SAVE, "blackfen", "player", casualties_target=3)
    assert r1["ok"] and not r1["escalated_to_war"], r1
    r2 = gs.report_raid(CTX, conn, SAVE, "blackfen", "player", casualties_target=2)
    r3 = gs.report_raid(CTX, conn, SAVE, "blackfen", "player", casualties_target=1)
    assert r3["escalated_to_war"], (r2, r3)
    rel = gs.get_relation(conn, SAVE, "blackfen", "player")
    assert rel["state"] == "war"
    stats = gs._loads(rel["stats_json"], {})
    assert stats.get("losses_player") == 6, stats


def test_alliance_shatter_cascade():
    conn = fresh()
    _set_relation(conn, "york", "shewolf", 0.5)
    assert gs.apply_diplomacy_action(CTX, conn, SAVE, "york", "shewolf", "form_alliance")["ok"]
    before = float(gs.get_relation(conn, SAVE, "blackfen", "york")["relation"])
    assert gs.apply_diplomacy_action(CTX, conn, SAVE, "blackfen", "shewolf", "declare_war")["ok"]
    after = float(gs.get_relation(conn, SAVE, "blackfen", "york")["relation"])
    assert abs(after - (before - 0.2)) < 1e-9, (before, after)


def test_trade_pact_cap():
    conn = fresh()
    assert gs.apply_diplomacy_action(CTX, conn, SAVE, "york", "shewolf", "trade_pact")["ok"]
    assert gs.apply_diplomacy_action(CTX, conn, SAVE, "york", "blackfen", "trade_pact")["ok"]
    r = gs.apply_diplomacy_action(CTX, conn, SAVE, "york", "player", "trade_pact")
    assert not r["ok"], "third econ pact accepted"
    menu = gs.diplomacy_legal_moves(conn, SAVE, "york")
    # menu may still offer it (cap enforced at apply); tolerate either, but
    # applying MUST refuse — that's the enforced invariant.


def test_fatigue_forces_peace_with_loss_ratio_reparations():
    conn = fresh()
    gs.report_raid(CTX, conn, SAVE, "york", "shewolf", casualties_target=4)
    _set_relation(conn, "york", "shewolf", -0.7)
    assert gs.apply_diplomacy_action(CTX, conn, SAVE, "york", "shewolf", "declare_war")["ok"]
    for _ in range(6):
        gs.run_diplomacy_round(CTX, conn, SAVE,
                               choose_move=lambda f, m: {"kind": "no_move", "target": None})
    rel = gs.get_relation(conn, SAVE, "york", "shewolf")
    assert rel["state"] != "war", rel["state"]
    # reparations proclamation logged with loss-scaled amount (25 + 25*4 = 125)
    row = conn.execute("SELECT proclamation FROM diplomacy_log WHERE save_id=? AND "
                       "action='make_peace' ORDER BY id DESC LIMIT 1", (SAVE,)).fetchone()
    assert row and "125" in row["proclamation"], row["proclamation"] if row else None


def test_round_agent_move_with_llm_garbage_fallback():
    conn = fresh()
    _set_relation(conn, "player", "blackfen", -0.5)
    result = gs.run_diplomacy_round(CTX, conn, SAVE,
                                    choose_move=lambda f, m: {"kind": "besiege_moon", "target": "sun"})
    agent_moves = [m for m in result["moves"] if m["action"] not in
                   ("war_continues", "relation_drift", "war_fatigue_peace")]
    assert agent_moves, result
    assert agent_moves[0]["action"] != "besiege_moon"


def test_round_robin_rotates():
    conn = fresh()
    movers = []
    for _ in range(4):
        r = gs.run_diplomacy_round(CTX, conn, SAVE,
                                   choose_move=lambda f, m: {"kind": "no_move", "target": None})
        acted = [m["actor"] for m in r["moves"] if m["action"] == "no_move"]
        movers.extend(acted)
    assert len(set(movers)) == 4, movers


def test_proclamation_becomes_world_event():
    conn = fresh()
    _set_relation(conn, "blackfen", "player", -0.5)
    r = gs.apply_diplomacy_action(CTX, conn, SAVE, "blackfen", "player", "declare_war")
    assert r["world_event_id"], r
    ev = conn.execute("SELECT event_type, title FROM world_events WHERE id=?",
                      (r["world_event_id"],)).fetchone()
    assert ev["event_type"] == "military" and "blackfen" in ev["title"], dict(ev)


def test_proclamation_propagates_to_settlers():
    """Scenario 1's rumor loop: a war declaration must reach settler
    knowledge (world_event_knowledge) so dialogue context can carry it."""
    conn = fresh()
    now = gs._now()
    for sid in ("mara", "aldric"):
        conn.execute("INSERT INTO npc_memory_profiles (save_id, settler_id, first_seen, last_seen) "
                     "VALUES (?, ?, ?, ?)", (SAVE, sid, now, now))
    _set_relation(conn, "blackfen", "player", -0.5)
    r = gs.apply_diplomacy_action(CTX, conn, SAVE, "blackfen", "player", "declare_war")
    known = conn.execute("SELECT COUNT(*) AS c FROM world_event_knowledge WHERE save_id=? "
                         "AND event_id=?", (SAVE, r["world_event_id"])).fetchone()["c"]
    assert known == 2, f"expected 2 settlers to hear the rumor, got {known}"
    events = gs.known_events(conn, SAVE, "mara", limit=5)
    assert any("War" in (e.get("title") or "") for e in events), events


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
