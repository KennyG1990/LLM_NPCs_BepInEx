"""
gm_plans — persisted colony PLAN DOCUMENTS + LAWS (task #25, spec in BACKLOG).
The Player2 Planner (#17) writes plans here; the executor consumes steps and
reports status; laws are the governance layer's durable rules. OUR SQLite —
nothing stored on Player2 servers (Ken directive).

Tiers: 'immediate' (3-5 crisis steps) and 'seasonal' ("winter is coming").
Submitting a new plan for a tier RETIRES the previous one (replaced_at set),
preserving history — the colony's strategy becomes auditable over time.
"""
import json
import time


def ensure_schema(conn):
    conn.executescript("""
        CREATE TABLE IF NOT EXISTS colony_plans (
            plan_id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            tier TEXT NOT NULL CHECK (tier IN ('immediate','seasonal')),
            author TEXT DEFAULT 'player2',
            rationale TEXT DEFAULT '',
            created_at REAL NOT NULL,
            replaced_at REAL
        );
        CREATE TABLE IF NOT EXISTS plan_steps (
            step_id INTEGER PRIMARY KEY AUTOINCREMENT,
            plan_id INTEGER NOT NULL REFERENCES colony_plans(plan_id),
            seq INTEGER NOT NULL,
            what TEXT NOT NULL,
            where_xyz TEXT DEFAULT '',
            why TEXT DEFAULT '',
            how TEXT DEFAULT '',
            status TEXT NOT NULL DEFAULT 'pending'
                CHECK (status IN ('pending','active','done','failed','rejected'))
        );
        CREATE TABLE IF NOT EXISTS colony_laws (
            law_id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            text TEXT NOT NULL,
            domain TEXT DEFAULT 'general',
            active INTEGER NOT NULL DEFAULT 1,
            enacted_at REAL NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_plans_save ON colony_plans(save_id, tier, replaced_at);
        CREATE INDEX IF NOT EXISTS idx_steps_plan ON plan_steps(plan_id, seq);
    """)


def submit_plan(conn, save_id, tier, steps, rationale="", author="player2"):
    """steps: [{what, where_xyz?, why?, how?}] — validated, bounded (max 12).
    Returns plan_id. Raises ValueError on bad input (the NEGATIVE path)."""
    if tier not in ("immediate", "seasonal"):
        raise ValueError(f"invalid tier '{tier}'")
    if not isinstance(steps, list) or not steps or len(steps) > 12:
        raise ValueError("steps must be a non-empty list of at most 12")
    for s in steps:
        if not isinstance(s, dict) or not str(s.get("what") or "").strip():
            raise ValueError("every step needs a non-empty 'what'")
    now = time.time()
    with conn:
        conn.execute(
            "UPDATE colony_plans SET replaced_at=? WHERE save_id=? AND tier=? AND replaced_at IS NULL",
            (now, save_id, tier))
        cur = conn.execute(
            "INSERT INTO colony_plans (save_id, tier, author, rationale, created_at) VALUES (?,?,?,?,?)",
            (save_id, tier, author, rationale or "", now))
        pid = cur.lastrowid
        for i, s in enumerate(steps):
            conn.execute(
                "INSERT INTO plan_steps (plan_id, seq, what, where_xyz, why, how) VALUES (?,?,?,?,?,?)",
                (pid, i, str(s.get("what")).strip(), str(s.get("where_xyz") or ""),
                 str(s.get("why") or ""), str(s.get("how") or "")))
    return pid


def current_plan(conn, save_id, tier=None):
    """Active plan(s) with steps; tier=None returns both tiers."""
    tiers = [tier] if tier else ["immediate", "seasonal"]
    out = {}
    for t in tiers:
        row = conn.execute(
            "SELECT * FROM colony_plans WHERE save_id=? AND tier=? AND replaced_at IS NULL "
            "ORDER BY created_at DESC LIMIT 1", (save_id, t)).fetchone()
        if not row:
            out[t] = None
            continue
        plan = dict(row)
        plan["steps"] = [dict(r) for r in conn.execute(
            "SELECT * FROM plan_steps WHERE plan_id=? ORDER BY seq", (plan["plan_id"],))]
        out[t] = plan
    return out


def set_step_status(conn, step_id, status):
    if status not in ("pending", "active", "done", "failed", "rejected"):
        raise ValueError(f"invalid status '{status}'")
    with conn:
        cur = conn.execute("UPDATE plan_steps SET status=? WHERE step_id=?", (status, step_id))
        if cur.rowcount == 0:
            raise ValueError(f"no step {step_id}")


def enact_law(conn, save_id, text, domain="general"):
    if not str(text or "").strip():
        raise ValueError("law text required")
    with conn:
        cur = conn.execute(
            "INSERT INTO colony_laws (save_id, text, domain, enacted_at) VALUES (?,?,?,?)",
            (save_id, str(text).strip(), domain, time.time()))
    return cur.lastrowid


def active_laws(conn, save_id):
    return [dict(r) for r in conn.execute(
        "SELECT * FROM colony_laws WHERE save_id=? AND active=1 ORDER BY enacted_at", (save_id,))]


def dispatch(http, ctx, method, path, query=None, payload=None):
    """Route handler, mirroring the gm_systems.dispatch pattern. True if handled."""
    if not path.startswith("/api/plan") and not path.startswith("/api/laws"):
        return False
    conn = ctx.get_db_connection()
    try:
        ensure_schema(conn)
        q = query or {}
        p = payload or {}
        save_id = (q.get("save_id", [""])[0] if isinstance(q.get("save_id"), list) else q.get("save_id")) \
                  or p.get("save_id") or ""
        if not save_id:
            http._send_json(400, {"ok": False, "error": "save_id required"})
            return True
        if path == "/api/plan" and method == "GET":
            http._send_json(200, {"ok": True, "plans": current_plan(conn, save_id)})
        elif path == "/api/plan" and method == "POST":
            pid = submit_plan(conn, save_id, p.get("tier"), p.get("steps"),
                              p.get("rationale", ""), p.get("author", "player2"))
            http._send_json(200, {"ok": True, "plan_id": pid})
        elif path == "/api/plan/step_status" and method == "POST":
            set_step_status(conn, int(p.get("step_id", -1)), p.get("status"))
            http._send_json(200, {"ok": True})
        elif path == "/api/laws" and method == "GET":
            http._send_json(200, {"ok": True, "laws": active_laws(conn, save_id)})
        elif path == "/api/laws" and method == "POST":
            lid = enact_law(conn, save_id, p.get("text"), p.get("domain", "general"))
            http._send_json(200, {"ok": True, "law_id": lid})
        else:
            return False
        return True
    except ValueError as ve:
        http._send_json(400, {"ok": False, "error": str(ve)})
        return True
    except Exception as e:
        http._send_json(500, {"ok": False, "error": str(e)})
        return True
    finally:
        conn.close()


def run_selftest():
    import sqlite3
    conn = sqlite3.connect(":memory:")
    conn.row_factory = sqlite3.Row
    ensure_schema(conn)
    checks = {}
    pid = submit_plan(conn, "sv", "immediate",
                      [{"what": "roof the house", "why": "rain ruins beds"},
                       {"what": "craft sling", "how": "fletchers_table"}], "crisis")
    plans = current_plan(conn, "sv")
    checks["submit_and_read"] = plans["immediate"]["plan_id"] == pid and len(plans["immediate"]["steps"]) == 2
    pid2 = submit_plan(conn, "sv", "immediate", [{"what": "hunt sheep"}])
    plans2 = current_plan(conn, "sv")
    checks["replacement"] = plans2["immediate"]["plan_id"] == pid2 and \
        conn.execute("SELECT replaced_at FROM colony_plans WHERE plan_id=?", (pid,)).fetchone()[0] is not None
    sid = plans2["immediate"]["steps"][0]["step_id"]
    set_step_status(conn, sid, "done")
    checks["step_status"] = conn.execute(
        "SELECT status FROM plan_steps WHERE step_id=?", (sid,)).fetchone()[0] == "done"
    enact_law(conn, "sv", "Leisure 17-20 is protected", "schedule")
    checks["laws"] = active_laws(conn, "sv")[0]["domain"] == "schedule"
    # NEGATIVE paths (workflow step 5): bad input must be refused.
    neg = 0
    for bad in [lambda: submit_plan(conn, "sv", "bogus", [{"what": "x"}]),
                lambda: submit_plan(conn, "sv", "immediate", []),
                lambda: submit_plan(conn, "sv", "immediate", [{"nope": 1}]),
                lambda: set_step_status(conn, 99999, "done"),
                lambda: enact_law(conn, "sv", "  ")]:
        try:
            bad()
        except ValueError:
            neg += 1
    checks["negative_paths_refused"] = neg == 5
    return {"ok": all(checks.values()), "checks": checks}


if __name__ == "__main__":
    print(run_selftest())
