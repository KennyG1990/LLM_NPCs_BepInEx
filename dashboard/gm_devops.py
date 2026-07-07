"""Dev-iteration + data-maintenance endpoints for the LLM NPCs dashboard.

The dashboard server runs on the host, so these endpoints give any HTTP
client full iteration control without desktop automation:

  GET  /api/dev/status            -> game running? built vs deployed DLL hashes
  POST /api/dev/build             -> dotnet build -c Release (+deploy DLL)
  POST /api/dev/game/launch       -> start Going Medieval (exe or steam://)
  POST /api/dev/game/kill         -> stop the game process
  POST /api/dev/game/restart      -> kill, wait, launch
  POST /api/dev/merge_identities  -> merge per-session settler IDs into the
                                     stable name-hash IDs the C# now emits

Windows-only actions return 501 elsewhere so selftests can run anywhere.
The server binds 127.0.0.1, so these are local-only by design.
"""

import hashlib
import os
import subprocess
import time
from pathlib import Path

IS_WINDOWS = os.name == "nt"
GAME_DIR = Path(r"E:\SteamLibrary\steamapps\common\Going Medieval")
GAME_EXE = GAME_DIR / "Going Medieval.exe"
GAME_IMAGE = "Going Medieval.exe"
PLUGINS_DIR = GAME_DIR / "BepInEx" / "plugins"
BUILD_TIMEOUT = 300
KILL_WAIT = 8
LAUNCH_STEAM_URL = "steam://rungameid/1029780"

def _project_dir():
    return Path(__file__).resolve().parent.parent

def _built_dll():
    return _project_dir() / "bin" / "Release" / "net472" / "LLM_NPCs.dll"

def _deployed_dll():
    return PLUGINS_DIR / "LLM_NPCs.dll"

def _sha256(path):
    try:
        h = hashlib.sha256()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 16), b""):
                h.update(chunk)
        return h.hexdigest()
    except OSError:
        return None

def _run(cmd, cwd=None, timeout=60):
    try:
        proc = subprocess.run(
            cmd, cwd=str(cwd) if cwd else None, capture_output=True,
            text=True, timeout=timeout, shell=False,
        )
        out = (proc.stdout or "") + (proc.stderr or "")
        return {"code": proc.returncode, "output_tail": out[-4000:]}
    except subprocess.TimeoutExpired:
        return {"code": -1, "output_tail": f"timeout after {timeout}s"}
    except Exception as e:  # noqa: BLE001
        return {"code": -1, "output_tail": str(e)}

def _game_running():
    if not IS_WINDOWS:
        return False
    result = _run(["tasklist", "/FI", f"IMAGENAME eq {GAME_IMAGE}", "/FO", "CSV", "/NH"])
    return GAME_IMAGE.lower() in result["output_tail"].lower()

def _status():
    built = _built_dll()
    deployed = _deployed_dll()
    built_hash = _sha256(built)
    deployed_hash = _sha256(deployed)
    return {
        "ok": True,
        "windows": IS_WINDOWS,
        "game_running": _game_running(),
        "game_exe_exists": GAME_EXE.exists(),
        "built_dll": {"path": str(built), "sha256": built_hash,
                      "mtime": built.stat().st_mtime if built.exists() else None},
        "deployed_dll": {"path": str(deployed), "sha256": deployed_hash,
                         "mtime": deployed.stat().st_mtime if deployed.exists() else None},
        "dll_in_sync": bool(built_hash) and built_hash == deployed_hash,
    }

def _not_windows(http):
    http._send_json(501, {"ok": False, "error": "host-only action; requires the dashboard to run on Windows"})
    return True

def _launch(http, payload):
    if not IS_WINDOWS:
        return _not_windows(http)
    if _game_running():
        http._send_json(200, {"ok": True, "already_running": True})
        return True
    via = (payload.get("via") or "exe").lower()
    try:
        if via == "steam":
            os.startfile(payload.get("steam_url") or LAUNCH_STEAM_URL)  # noqa: S606
        else:
            if not GAME_EXE.exists():
                http._send_json(404, {"ok": False, "error": f"game exe not found at {GAME_EXE}"})
                return True
            subprocess.Popen([str(GAME_EXE)], cwd=str(GAME_DIR))  # noqa: S603
    except Exception as e:  # noqa: BLE001
        http._send_json(500, {"ok": False, "error": str(e)})
        return True
    deadline = time.time() + float(payload.get("wait_seconds") or 90)
    running = False
    while time.time() < deadline:
        if _game_running():
            running = True
            break
        time.sleep(2)
    http._send_json(200, {"ok": True, "launched": True, "process_seen": running, "via": via})
    return True

def _kill(http, payload):
    if not IS_WINDOWS:
        return _not_windows(http)
    if not _game_running():
        http._send_json(200, {"ok": True, "already_stopped": True})
        return True
    image = payload.get("image") or GAME_IMAGE
    graceful = _run(["taskkill", "/IM", image])
    time.sleep(2)
    forced = None
    if _game_running():
        forced = _run(["taskkill", "/IM", image, "/F"])
        deadline = time.time() + KILL_WAIT
        while time.time() < deadline and _game_running():
            time.sleep(1)
    http._send_json(200, {
        "ok": True, "stopped": not _game_running(),
        "graceful": graceful, "forced": forced,
    })
    return True

def _build(http, payload):
    if not IS_WINDOWS:
        return _not_windows(http)
    project = _project_dir()
    cmd = ["dotnet", "build", "--configuration", "Release"]
    if payload.get("rebuild", True):
        cmd.append("-t:Rebuild")
    result = _run(cmd, cwd=project, timeout=BUILD_TIMEOUT)
    success = result["code"] == 0 and _built_dll().exists()
    deployed = None
    if success and payload.get("deploy", True):
        if _game_running() and not payload.get("force_deploy_while_running"):
            deployed = {"skipped": "game is running; kill it first or pass force_deploy_while_running"}
        else:
            try:
                PLUGINS_DIR.mkdir(parents=True, exist_ok=True)
                import shutil
                shutil.copy2(_built_dll(), _deployed_dll())
                newtonsoft = _built_dll().parent / "Newtonsoft.Json.dll"
                if newtonsoft.exists():
                    shutil.copy2(newtonsoft, PLUGINS_DIR / "Newtonsoft.Json.dll")
                deployed = {"copied": True}
            except Exception as e:  # noqa: BLE001
                deployed = {"error": str(e)}
    http._send_json(200 if success else 500, {
        "ok": success, "build": result, "deploy": deployed, "status": _status(),
    })
    return True

# ---------------------------------------------------------------------------
# Identity migration: per-session numeric settler IDs -> stable name-hash IDs
# ---------------------------------------------------------------------------

def canonical_settler_id(display_name):
    """MUST match GameBridge.ComputeStableSettlerId: gm_ + first 12 hex chars
    of SHA1(trimmed lowercased display name)."""
    if not display_name:
        return None
    normalized = str(display_name).strip().lower()
    if normalized in ("", "unknown") or len(normalized) < 3:
        return None
    return "gm_" + hashlib.sha1(normalized.encode("utf-8")).hexdigest()[:12]

# (table, column) pairs re-pointed during a merge. OR IGNORE + cleanup handles
# unique-constrained state tables; plain event tables never conflict.
_MERGE_COLUMNS = (
    ("typed_memories", "settler_id"),
    ("facts", "settler_id"),
    ("facts", "npc_key"),
    ("memories", "npc_id"),
    ("permanent_memories", "npc_id"),
    ("character_sheets", "settler_id"),
    ("settler_pressures", "settler_id"),
    ("incidents", "settler_id"),
    ("npcs", "settler_id"),
    ("npcs", "npc_key"),
    ("dialogue_claims", "settler_id"),
    ("dialogue_barter_intents", "settler_id"),
    ("trust_events", "settler_id"),
    ("relationships", "subject"),
    ("relationships", "object"),
    ("entity_mentions", "settler_id"),
    ("visit_history", "settler_id"),
    ("world_event_knowledge", "settler_id"),
    ("romance_states", "settler_id"),
    ("romance_states", "partner_id"),
    ("death_records", "settler_id"),
    ("disease_states", "settler_id"),
    ("ai_orders", "settler_id"),
)

def _merge_one(conn, save_id, source_id, target_id):
    counts = {}
    for table, column in _MERGE_COLUMNS:
        try:
            cur = conn.execute(
                f"UPDATE OR IGNORE {table} SET {column} = ? WHERE {column} = ? AND save_id = ?",
                (target_id, source_id, save_id),
            )
            moved = cur.rowcount
        except Exception:
            # some legacy tables lack save_id; retry unscoped for those
            try:
                cur = conn.execute(
                    f"UPDATE OR IGNORE {table} SET {column} = ? WHERE {column} = ?",
                    (target_id, source_id),
                )
                moved = cur.rowcount
            except Exception:
                continue
        # rows blocked by unique constraints stay on the source id; the
        # target's row wins and the stale source state row is dropped.
        try:
            cur = conn.execute(
                f"DELETE FROM {table} WHERE {column} = ? AND save_id = ?",
                (source_id, save_id),
            )
            dropped = cur.rowcount
        except Exception:
            dropped = 0
        if moved or dropped:
            counts[f"{table}.{column}"] = {"moved": moved, "dropped_conflicts": dropped}

    # dialogue_states: accumulate counters into the target row.
    src_state = conn.execute(
        "SELECT * FROM dialogue_states WHERE save_id = ? AND settler_id = ?",
        (save_id, source_id)).fetchone()
    if src_state:
        conn.execute("""
            INSERT INTO dialogue_states (save_id, settler_id, trust, disclosure_level,
                voice_profile, backstory_voice, contradiction_count, barter_intent_count, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(save_id, settler_id) DO UPDATE SET
                contradiction_count = dialogue_states.contradiction_count + excluded.contradiction_count,
                barter_intent_count = dialogue_states.barter_intent_count + excluded.barter_intent_count,
                voice_profile = CASE WHEN dialogue_states.voice_profile = '' THEN excluded.voice_profile ELSE dialogue_states.voice_profile END,
                updated_at = MAX(dialogue_states.updated_at, excluded.updated_at)
        """, (save_id, target_id, src_state["trust"], src_state["disclosure_level"],
              src_state["voice_profile"], src_state["backstory_voice"],
              src_state["contradiction_count"], src_state["barter_intent_count"],
              src_state["updated_at"]))
        conn.execute("DELETE FROM dialogue_states WHERE save_id = ? AND settler_id = ?",
                     (save_id, source_id))
        counts["dialogue_states"] = {"merged": 1}

    # npc_memory_profiles: sum counts into target, keep earliest first_seen.
    src_prof = conn.execute(
        "SELECT * FROM npc_memory_profiles WHERE save_id = ? AND settler_id = ?",
        (save_id, source_id)).fetchone()
    if src_prof:
        tgt_prof = conn.execute(
            "SELECT * FROM npc_memory_profiles WHERE save_id = ? AND settler_id = ?",
            (save_id, target_id)).fetchone()
        if tgt_prof:
            conn.execute("""
                UPDATE npc_memory_profiles SET
                    memories_count = memories_count + ?,
                    secrets_count = secrets_count + ?,
                    first_seen = MIN(first_seen, ?),
                    last_seen = MAX(last_seen, ?)
                WHERE save_id = ? AND settler_id = ?
            """, (src_prof["memories_count"] or 0, src_prof["secrets_count"] or 0,
                  src_prof["first_seen"], src_prof["last_seen"], save_id, target_id))
        else:
            conn.execute(
                "UPDATE npc_memory_profiles SET settler_id = ? WHERE save_id = ? AND settler_id = ?",
                (target_id, save_id, source_id))
        conn.execute("DELETE FROM npc_memory_profiles WHERE save_id = ? AND settler_id = ?",
                     (save_id, source_id))
        counts["npc_memory_profiles"] = {"merged": 1}
    return counts

def _merge_identities(http, ctx, payload):
    save_id = payload.get("save_id")
    if not save_id:
        http._send_json(400, {"ok": False, "error": "save_id is required"})
        return True
    dry_run = bool(payload.get("dry_run"))
    conn = ctx.get_db_connection()
    try:
        profiles = [dict(r) for r in conn.execute(
            "SELECT settler_id, display_name, memories_count FROM npc_memory_profiles WHERE save_id = ?",
            (save_id,))]
        seen_ids = {p["settler_id"] for p in profiles}
        try:
            for r in conn.execute(
                    "SELECT DISTINCT settler_id, name FROM npcs WHERE save_id = ?", (save_id,)):
                if r["settler_id"] and r["settler_id"] not in seen_ids:
                    profiles.append({"settler_id": r["settler_id"],
                                     "display_name": r["name"], "memories_count": 0})
                    seen_ids.add(r["settler_id"])
        except Exception:
            pass
        groups = {}
        for p in profiles:
            canon = canonical_settler_id(p.get("display_name"))
            if not canon:
                continue
            groups.setdefault(canon, []).append(p)
        plan = []
        for canon, members in groups.items():
            sources = [m["settler_id"] for m in members if m["settler_id"] != canon]
            if not sources:
                continue
            plan.append({
                "target": canon,
                "display_name": members[0]["display_name"],
                "sources": sources,
                "total_memories": sum(m["memories_count"] or 0 for m in members),
            })
        if dry_run:
            http._send_json(200, {"ok": True, "dry_run": True, "plan": plan})
            return True
        report = []
        with conn:
            for item in plan:
                merged = {}
                for source in item["sources"]:
                    counts = _merge_one(conn, save_id, source, item["target"])
                    for key, val in counts.items():
                        agg = merged.setdefault(key, {})
                        for stat, n in val.items():
                            agg[stat] = agg.get(stat, 0) + n
                report.append({"target": item["target"], "display_name": item["display_name"],
                               "sources_merged": len(item["sources"]), "tables": merged})
        http._send_json(200, {"ok": True, "dry_run": False, "merged": report})
        return True
    finally:
        conn.close()

def _managed_dir():
    return GAME_DIR / "Going Medieval_Data" / "Managed"

def _ensure_ilspycmd():
    """Return a runnable ilspycmd command list, installing the dotnet tool if
    needed. Returns (cmd_prefix, note)."""
    candidates = [
        ["ilspycmd"],
        [str(Path(os.path.expandvars(r"%USERPROFILE%")) / ".dotnet" / "tools" / "ilspycmd.exe")],
    ]
    for c in candidates:
        try:
            r = _run(c + ["--version"], timeout=30)
            if r["code"] == 0:
                return c, "found"
        except Exception:
            pass
    # try to install as a global dotnet tool. The newest package can ship
    # without DotnetToolSettings.xml on some SDKs, so try pinned known-good
    # versions before falling back to latest.
    tool = str(Path(os.path.expandvars(r"%USERPROFILE%")) / ".dotnet" / "tools" / "ilspycmd.exe")
    tails = []
    for ver in ["8.2.0.7535", "7.2.1.6856", "9.0.0.7889", None]:
        cmd_i = ["dotnet", "tool", "install", "--global", "ilspycmd"]
        if ver:
            cmd_i += ["--version", ver]
        inst = _run(cmd_i, timeout=180)
        tails.append(f"{ver or 'latest'}:{inst['output_tail'][-150:]}")
        r = _run([tool, "--version"], timeout=30)
        if r["code"] == 0:
            return [tool], f"installed {ver or 'latest'}"
        # uninstall any half-state before trying the next version
        _run(["dotnet", "tool", "uninstall", "--global", "ilspycmd"], timeout=60)
    return None, "ilspycmd unavailable: " + " || ".join(tails)

def _decompile(http, ctx, payload):
    if not IS_WINDOWS:
        return _not_windows(http)
    dll = payload.get("dll")
    dll_path = Path(dll) if dll else (_managed_dir() / "Assembly-CSharp.dll")
    if not dll_path.exists():
        http._send_json(404, {"ok": False, "error": f"assembly not found at {dll_path}"})
        return True
    types = payload.get("types") or []
    if isinstance(types, str):
        types = [types]
    if not types:
        http._send_json(400, {"ok": False, "error": "provide 'types': [fullTypeName, ...]"})
        return True
    cmd, note = _ensure_ilspycmd()
    if cmd is None:
        http._send_json(200, {"ok": False, "error": note})
        return True
    out_dir = _project_dir() / "validation" / "decompiled"
    out_dir.mkdir(parents=True, exist_ok=True)
    written, errors = [], {}
    for t in types:
        r = _run(cmd + [str(dll_path), "-t", t], timeout=120)
        if r["code"] == 0 and r["output_tail"].strip():
            safe = t.replace(".", "_").replace("`", "_").replace("<", "").replace(">", "")
            fp = out_dir / f"{safe}.cs"
            try:
                # output_tail is truncated to 4000; re-run capturing full stdout
                proc = subprocess.run(cmd + [str(dll_path), "-t", t], capture_output=True,
                                      text=True, timeout=120)
                fp.write_text(proc.stdout or r["output_tail"], encoding="utf-8")
                written.append(str(fp))
            except Exception as e:  # noqa: BLE001
                errors[t] = str(e)
        else:
            errors[t] = r["output_tail"][-300:]
    http._send_json(200, {"ok": bool(written), "ilspycmd": note,
                          "written": written, "errors": errors})
    return True

def _place_test(http, ctx, payload):
    """Inject a single-action ai_order so the live in-game OrderExecutor picks
    it up next poll and reports the real result back into ai_orders. Used to
    exercise C# actions (esp. B3 place_stockpile) end-to-end without the full
    proposal/plan plumbing. Read the outcome from GET /api/orders."""
    import json as _json
    save_id = payload.get("save_id")
    settler_id = payload.get("settler_id")
    action = payload.get("action") or "place_stockpile"
    if not save_id or not settler_id:
        http._send_json(400, {"ok": False, "error": "save_id and settler_id are required"})
        return True
    step = {"action": action, "status": "pending"}
    if payload.get("building"):
        step["building"] = payload["building"]
    if payload.get("target"):
        step["target"] = payload["target"]
    if payload.get("job"):
        step["job"] = payload["job"]
    conn = ctx.get_db_connection()
    try:
        now = int(time.time())
        with conn:
            try:
                ctx.upsert_memory_profile(conn, save_id, settler_id)
            except Exception:
                pass
            conn.execute(
                "INSERT INTO ai_orders (save_id, settler_id, raw_text, steps_json, status, "
                "current_step, created_at, updated_at) VALUES (?, ?, ?, ?, 'queued', 0, ?, ?)",
                (save_id, settler_id, f"[dev test] {action}",
                 _json.dumps([step], ensure_ascii=False), now, now),
            )
            order_id = conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]
        http._send_json(200, {"ok": True, "order_id": order_id, "action": action,
                              "poll_hint": "GET /api/orders?save_id=...&status=queued then &status=completed/failed"})
        return True
    finally:
        conn.close()

def dispatch(http, ctx, method, path, query=None, payload=None):
    payload = payload or {}
    try:
        if method == "GET" and path == "/api/dev/status":
            http._send_json(200, _status())
            return True
        if method == "GET" and path == "/api/dev/log":
            lines = int((query or {}).get("lines", ["120"])[0])
            sources = {
                "bepinex": GAME_DIR / "BepInEx" / "LogOutput.log",
                "bepinex_logs": GAME_DIR / "BepInEx" / "logs" / "LogOutput.log",
                "player": Path(os.path.expandvars(
                    r"%USERPROFILE%\AppData\LocalLow\Foxy Voxel\Going Medieval\Player.log")),
            }
            # The mod's own decision log (GetDecisionAsync / ForceProcessSettler /
            # ExecuteBuildStockpile detail) lives here, not in bepinex.
            try:
                mod_log_dir = Path(os.path.expandvars(
                    r"%USERPROFILE%\AppData\LocalLow\Foxy Voxel\Going Medieval\LLM_NPCs\logs"))
                mod_logs = sorted(mod_log_dir.glob("mod_*.log"), key=lambda p: p.stat().st_mtime)
                if mod_logs:
                    sources["mod"] = mod_logs[-1]
            except Exception:
                pass
            out = {}
            for name, p in sources.items():
                try:
                    if p.exists():
                        text = p.read_text(encoding="utf-8", errors="replace")
                        out[name] = text.splitlines()[-lines:]
                except Exception as e:  # noqa: BLE001
                    out[name] = [f"read error: {e}"]
            http._send_json(200, {"ok": True, "logs": out})
            return True
        if method == "GET" and path == "/api/dev/last_order":
            conn = ctx.get_db_connection()
            try:
                row = conn.execute(
                    "SELECT id, save_id, settler_id, status, current_step, steps_json, "
                    "failure_reason FROM ai_orders ORDER BY id DESC LIMIT 1").fetchone()
                if not row:
                    http._send_json(200, {"ok": True, "order": None})
                    return True
                import json as _json
                steps = _json.loads(row["steps_json"] or "[]")
                note = ""
                if steps and isinstance(steps[0], dict):
                    note = steps[0].get("note") or ""
                http._send_json(200, {"ok": True, "id": row["id"], "save_id": row["save_id"],
                                      "settler_id": row["settler_id"], "status": row["status"],
                                      "failure_reason": row["failure_reason"] or "", "step_note": note})
                return True
            finally:
                conn.close()
        if method == "GET" and path == "/api/dev/sheets_debug":
            conn = ctx.get_db_connection()
            try:
                rows = conn.execute(
                    "SELECT save_id, settler_id, name, age, height, weight, updated_at "
                    "FROM character_sheets ORDER BY updated_at DESC LIMIT 12").fetchall()
                total = conn.execute("SELECT COUNT(*) AS c FROM character_sheets").fetchone()["c"]
                skill_total = conn.execute("SELECT COUNT(*) AS c FROM character_sheet_skills").fetchone()["c"]
                http._send_json(200, {"ok": True, "total_sheets": total, "total_skill_rows": skill_total,
                                      "recent": [dict(r) for r in rows]})
                return True
            finally:
                conn.close()
        if method == "GET" and path == "/api/dev/decisions":
            import re as _re
            mod_log_dir = Path(os.path.expandvars(
                r"%USERPROFILE%\AppData\LocalLow\Foxy Voxel\Going Medieval\LLM_NPCs\logs"))
            pat = _re.compile(
                r"ForceProcessSettler|Decision received|build_stockpile|Built a stockpile|"
                r"Stockpile build|Executing decision|Selected action|Player2 returned command|"
                r"Autonomy Disabled|is sleeping. Skipping",
                _re.IGNORECASE)
            out = []
            try:
                mod_logs = sorted(mod_log_dir.glob("mod_*.log"), key=lambda p: p.stat().st_mtime)
                if mod_logs:
                    text = mod_logs[-1].read_text(encoding="utf-8", errors="replace")
                    out = [l for l in text.splitlines() if pat.search(l)][-80:]
            except Exception as e:  # noqa: BLE001
                out = [f"read error: {e}"]
            http._send_json(200, {"ok": True, "decisions": out})
            return True
        if method == "GET" and path == "/api/dev/api_surface":
            surface_path = _project_dir() / "validation" / "api_surface.json"
            if not surface_path.exists():
                http._send_json(404, {"ok": False, "error": "no api surface captured yet"})
                return True
            import json as _json
            http._send_json(200, _json.loads(surface_path.read_text(encoding="utf-8")))
            return True
        if method != "POST":
            return False
        if path == "/api/dev/build":
            return _build(http, payload)
        if path == "/api/dev/game/launch":
            return _launch(http, payload)
        if path == "/api/dev/game/kill":
            return _kill(http, payload)
        if path == "/api/dev/game/restart":
            if not IS_WINDOWS:
                return _not_windows(http)
            if _game_running():
                _run(["taskkill", "/IM", payload.get("image") or GAME_IMAGE, "/F"])
                deadline = time.time() + KILL_WAIT
                while time.time() < deadline and _game_running():
                    time.sleep(1)
            return _launch(http, payload)
        if path == "/api/dev/merge_identities":
            return _merge_identities(http, ctx, payload)
        if path == "/api/dev/place_test":
            return _place_test(http, ctx, payload)
        if path == "/api/dev/decompile":
            return _decompile(http, ctx, payload)
        if path == "/api/dev/api_surface":
            # The in-game scanner posts the NSMedieval construction API here.
            surface_path = _project_dir() / "validation" / "api_surface.json"
            surface_path.parent.mkdir(parents=True, exist_ok=True)
            import json as _json
            surface_path.write_text(_json.dumps(payload, indent=1), encoding="utf-8")
            http._send_json(200, {"ok": True, "stored": str(surface_path),
                                  "type_count": payload.get("type_count")})
            return True
        return False
    except Exception as e:  # noqa: BLE001 - endpoint boundary
        http._send_json(500, {"ok": False, "error": str(e)})
        return True
