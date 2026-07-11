"""
gm_colony — colony-level STRATEGIC status for the dashboard (Ken, 07-09:
"what data are we not tracking — fix it"). The mod's ColonyBuilder (the Strategic
layer) POSTs its per-tick state here so the dashboard has a COLONY window, not just
per-settler sheets. Latest-wins per save. OUR SQLite — nothing external.

Mirrors the gm_plans.dispatch pattern (verified live).
"""
import json
import time


def ensure_schema(conn):
    conn.executescript("""
        CREATE TABLE IF NOT EXISTS colony_status (
            save_id TEXT PRIMARY KEY,
            data_json TEXT NOT NULL,
            updated_at REAL NOT NULL
        );
    """)


def upsert_status(conn, save_id, data):
    now = time.time()
    with conn:
        conn.execute(
            "INSERT INTO colony_status (save_id, data_json, updated_at) VALUES (?,?,?) "
            "ON CONFLICT(save_id) DO UPDATE SET data_json=excluded.data_json, updated_at=excluded.updated_at",
            (save_id, json.dumps(data), now))
    return now


def get_status(conn, save_id):
    row = conn.execute(
        "SELECT data_json, updated_at FROM colony_status WHERE save_id=?", (save_id,)).fetchone()
    if not row:
        return None
    try:
        data = json.loads(row["data_json"])
    except Exception:
        data = {}
    data["updated_at"] = row["updated_at"]
    return data


def dispatch(http, ctx, method, path, query=None, payload=None):
    """True when handled. Routes /api/colony/status."""
    if path != "/api/colony/status":
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
        if method == "GET":
            http._send_json(200, {"ok": True, "status": get_status(conn, save_id)})
            return True
        if method == "POST":
            # store everything except save_id as the status blob
            data = {k: v for k, v in p.items() if k != "save_id"}
            ts = upsert_status(conn, save_id, data)
            http._send_json(200, {"ok": True, "updated_at": ts})
            return True
        return False
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
    upsert_status(conn, "sv", {"census": "pop=4", "food": "adequate"})
    s1 = get_status(conn, "sv")
    checks["write_read"] = s1 and s1["census"] == "pop=4"
    upsert_status(conn, "sv", {"census": "pop=3", "food": "scarce"})
    s2 = get_status(conn, "sv")
    checks["latest_wins"] = s2["census"] == "pop=3" and s2["food"] == "scarce"
    checks["missing_none"] = get_status(conn, "nope") is None
    return {"ok": all(checks.values()), "checks": checks}


if __name__ == "__main__":
    print(run_selftest())
