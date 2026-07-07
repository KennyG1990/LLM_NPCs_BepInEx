import os
import json  # noqa: F401  (sync marker 2026-07-05)
import sqlite3
import sys
import threading
import time
import urllib.request
import urllib.parse
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from datetime import datetime

# P3+ world systems live in gm_systems.py (orders, entities, world events,
# diplomacy, romance, death history, disease, combat). Loaded by path so the
# selftests' importlib loading of this module keeps working.
def _load_sibling_module(name):
    import importlib.util
    module_path = Path(__file__).resolve().parent / f"{name}.py"
    spec = importlib.util.spec_from_file_location(name, module_path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

gm_systems = _load_sibling_module("gm_systems")
gm_devops = _load_sibling_module("gm_devops")

class _GmCtx:
    """Late-bound view of this module's globals for gm_systems, robust to
    importlib loading where sys.modules lacks this module."""
    def __getattr__(self, name):
        return globals()[name]

GM_CTX = _GmCtx()

# Path to the actual game SQLite database (X4-aligned filename)
DB_PATH = Path(os.path.expandvars(r'%APPDATA%\Going Medieval\LLM_NPCs\memory\npc_memory.sqlite3'))
DASHBOARD_DIR = Path(__file__).resolve().parent
PROJECT_DIR = DASHBOARD_DIR.parent
SERVER_BOOT_ID = uuid.uuid4().hex
SERVER_BOOT_UTC = datetime.utcnow().isoformat(timespec="seconds") + "Z"
WATCH_EXTENSIONS = {".py", ".js", ".css", ".html", ".json", ".csproj"}
WATCH_EXCLUDED_DIRS = {
    ".git",
    ".vs",
    "__pycache__",
    "bin",
    "obj",
    "logs",
    "validation",
}
WATCH_POLL_SECONDS = 1.0
WATCH_DEBOUNCE_SECONDS = 0.5

def watched_files():
    for path in PROJECT_DIR.rglob("*"):
        if any(part in WATCH_EXCLUDED_DIRS for part in path.parts):
            continue
        if path.is_file() and path.suffix.lower() in WATCH_EXTENSIONS:
            yield path

def snapshot_watched_files():
    snapshot = {}
    for path in watched_files():
        try:
            stat = path.stat()
            snapshot[str(path)] = (stat.st_mtime_ns, stat.st_size)
        except OSError:
            continue
    return snapshot

def start_dev_file_watcher():
    if os.environ.get("GM_DASHBOARD_WATCH", "1").lower() in {"0", "false", "no"}:
        print("[watcher] disabled via GM_DASHBOARD_WATCH")
        return

    initial = snapshot_watched_files()

    def watch_loop():
        baseline = initial
        while True:
            time.sleep(WATCH_POLL_SECONDS)
            current = snapshot_watched_files()
            if current != baseline:
                # Let editors finish multi-write saves before replacing the process.
                time.sleep(WATCH_DEBOUNCE_SECONDS)
                current = snapshot_watched_files()
                changed = sorted(set(current.keys()) ^ set(baseline.keys()))
                changed.extend(
                    path for path in sorted(set(current.keys()) & set(baseline.keys()))
                    if current[path] != baseline[path]
                )
                changed_text = ", ".join(Path(path).name for path in changed[:5])
                if len(changed) > 5:
                    changed_text += f", +{len(changed) - 5} more"
                print(f"[watcher] file edit detected ({changed_text}); restarting server...")
                sys.stdout.flush()
                os.execv(sys.executable, [sys.executable] + sys.argv)
            baseline = current

    thread = threading.Thread(target=watch_loop, name="dashboard-file-watcher", daemon=True)
    thread.start()
    print(f"[watcher] enabled for {PROJECT_DIR} ({', '.join(sorted(WATCH_EXTENSIONS))})")

class ReusableThreadingHTTPServer(ThreadingHTTPServer):
    allow_reuse_address = True

def ensure_column(conn, table, column_name, column_sql):
    existing = {row["name"] for row in conn.execute(f"PRAGMA table_info({table})")}
    if column_name not in existing:
        conn.execute(f"ALTER TABLE {table} ADD COLUMN {column_sql}")

def get_db_connection():
    DB_PATH.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    return conn

P1_MEMORY_CATEGORIES = {
    "conversations",
    "secrets",
    "events",
    "promises",
    "betrayals",
    "favors",
    "deaths",
    "relationship_milestones",
    "decisions",
    "health",
    "mood",
    "danger",
    "colony",
    "system",
}

def classify_memory_category(event_type, content):
    text = f"{event_type or ''} {content or ''}".lower()
    if any(token in text for token in ("secret", "confession", "let slip", "private")):
        return "secrets"
    if any(token in text for token in ("promise", "promised", "oath", "vow", "pledge")):
        return "promises"
    if any(token in text for token in ("betray", "betrayed", "betrayal", "contradiction", "lie", "lied", "deceit", "treason")):
        return "betrayals"
    if any(token in text for token in ("favor", "favour", "helped", "owed", "debt")):
        return "favors"
    if any(token in text for token in ("death", "died", "buried", "grave", "slain")):
        return "deaths"
    if event_type in {"dialogue_player", "dialogue_npc", "conversation"}:
        return "conversations"
    if event_type in {"relationship", "marriage", "romance"}:
        return "relationship_milestones"
    if event_type in {"decision"}:
        return "decisions"
    if event_type in {"health"}:
        return "health"
    if event_type in {"mood"}:
        return "mood"
    if event_type in {"danger"}:
        return "danger"
    if event_type in {"colony", "colony_event", "adviser", "advisor"}:
        return "colony"
    if event_type in {"system", "debug"}:
        return "system"
    return "events"

def typed_memory_tier(importance):
    if importance >= 9:
        return "permanent"
    if importance >= 7:
        return "major"
    return "recent"

def infer_role_from_stats(stats):
    if not stats:
        return None
    skill_roles = {
        "intellectual": "Scholar",
        "botany": "Farmer",
        "culinary": "Cook",
        "carpentry": "Builder",
        "construction": "Builder",
        "mining": "Miner",
        "smithing": "Smith",
        "tailoring": "Tailor",
        "animal handling": "Animal Handler",
        "marksman": "Guard",
        "melee": "Guard",
        "speechcraft": "Steward",
        "art": "Artisan",
    }
    top_skill = None
    top_value = -1
    for part in str(stats).replace(";", ",").split(","):
        if ":" not in part:
            continue
        name, value = part.rsplit(":", 1)
        try:
            score = int(float(value.strip()))
        except ValueError:
            continue
        if score > top_value:
            top_skill = name.strip().lower()
            top_value = score
    if not top_skill:
        return None
    for skill, role in skill_roles.items():
        if skill in top_skill:
            return role
    return None

def normalize_profile_role(role, stats):
    role_text = (role or "").strip()
    if role_text.lower() in {"", "none", "no role", "unemployed", "worker", "settler", "unknown"}:
        inferred = infer_role_from_stats(stats)
        if inferred:
            return inferred
    return role

def upsert_memory_profile(conn, save_id, settler_id, name=None, role=None, traits=None, stats=None, description=None):
    if not save_id or not settler_id:
        return
    role = normalize_profile_role(role, stats)
    now = datetime.utcnow().timestamp()
    existing = conn.execute(
        "SELECT * FROM npc_memory_profiles WHERE save_id = ? AND settler_id = ?",
        (save_id, settler_id)
    ).fetchone()
    first_seen = existing["first_seen"] if existing else now
    if description is None and existing:
        description = existing["description"]
    if description is None:
        description_parts = []
        if name:
            description_parts.append(str(name))
        if role:
            description_parts.append(f"works as {role}")
        if traits:
            description_parts.append(f"traits: {traits}")
        description = "; ".join(description_parts)

    if existing:
        conn.execute("""
            UPDATE npc_memory_profiles
            SET display_name = COALESCE(?, display_name),
                role = COALESCE(?, role),
                traits = COALESCE(?, traits),
                stats = COALESCE(?, stats),
                description = COALESCE(NULLIF(?, ''), description),
                last_seen = ?,
                updated_at = ?
            WHERE save_id = ? AND settler_id = ?
        """, (name, role, traits, stats, description or "", now, now, save_id, settler_id))
    else:
        conn.execute("""
            INSERT INTO npc_memory_profiles
            (save_id, settler_id, display_name, role, traits, stats, description, first_seen, last_seen, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """, (save_id, settler_id, name, role, traits, stats, description or "", first_seen, now, now))

def insert_typed_memory(conn, save_id, settler_id, event_type, content, importance=5, metadata=None):
    if not save_id or not settler_id or not content:
        return None
    importance = max(1, min(10, int(importance)))
    category = classify_memory_category(event_type, content)
    tier = typed_memory_tier(importance)
    now = datetime.utcnow().timestamp()
    is_secret = 1 if category == "secrets" else 0
    metadata_json = json.dumps(metadata or {}, ensure_ascii=False)
    cursor = conn.execute("""
        INSERT INTO typed_memories
        (save_id, settler_id, category, tier, event_type, content, importance, confidence, is_secret,
         source, created_at, last_used_at, metadata_json)
        VALUES (?, ?, ?, ?, ?, ?, ?, 1.0, ?, 'game', ?, ?, ?)
    """, (save_id, settler_id, category, tier, event_type or "event", content, importance, is_secret, now, now, metadata_json))
    conn.execute("""
        UPDATE npc_memory_profiles
        SET memories_count = memories_count + 1,
            secrets_count = secrets_count + ?,
            last_seen = ?,
            updated_at = ?
        WHERE save_id = ? AND settler_id = ?
    """, (is_secret, now, now, save_id, settler_id))
    refresh_memory_profile_summary(conn, save_id, settler_id)
    return cursor.lastrowid

def get_memory_category_counts(conn, save_id, settler_id):
    rows = conn.execute("""
        SELECT category, COUNT(*) AS count, MAX(created_at) AS latest_at
        FROM typed_memories
        WHERE save_id = ? AND settler_id = ?
        GROUP BY category
        ORDER BY category
    """, (save_id, settler_id)).fetchall()
    counts = {category: {"count": 0, "latest_at": None} for category in sorted(P1_MEMORY_CATEGORIES)}
    for row in rows:
        counts[row["category"]] = {"count": row["count"], "latest_at": row["latest_at"]}
    return counts

def json_dumps_stable(value):
    return json.dumps(value if value is not None else {}, ensure_ascii=False, sort_keys=True)

def first_present(*values, default=None):
    for value in values:
        if value is not None and value != "":
            return value
    return default

def numeric_or_default(value, default=0):
    if value is None or value == "":
        return default
    try:
        return float(value)
    except (TypeError, ValueError):
        return default

def int_or_default(value, default=0):
    try:
        return int(numeric_or_default(value, default))
    except (TypeError, ValueError):
        return default

def normalize_modifier_entries(value):
    if not value:
        return []
    entries = value if isinstance(value, list) else [value]
    normalized = []
    for entry in entries:
        if isinstance(entry, dict):
            label = first_present(entry.get("label"), entry.get("name"), entry.get("text"))
            score = first_present(entry.get("value"), entry.get("score"), entry.get("amount"))
            if label:
                normalized.append((str(label), numeric_or_default(score, None) if score is not None else None))
        else:
            normalized.append((str(entry), None))
    return normalized

def normalize_schedule_entries(value):
    if not value:
        return []
    if isinstance(value, dict):
        return sorted(
            [(int_or_default(hour), str(activity)) for hour, activity in value.items()],
            key=lambda row: row[0],
        )
    if isinstance(value, list):
        rows = []
        for idx, entry in enumerate(value):
            if isinstance(entry, dict):
                hour = int_or_default(first_present(entry.get("hour"), entry.get("index"), idx))
                activity = first_present(entry.get("activity"), entry.get("type"), entry.get("label"))
            else:
                hour = idx
                activity = entry
            if activity is not None:
                rows.append((hour, str(activity)))
        return sorted(rows, key=lambda row: row[0])
    return []

def normalize_manage_entries(value):
    if not value:
        return []
    if isinstance(value, dict):
        return sorted((str(key), str(val)) for key, val in value.items())
    if isinstance(value, list):
        rows = []
        for entry in value:
            if isinstance(entry, dict):
                name = first_present(entry.get("setting_name"), entry.get("name"), entry.get("key"))
                setting_value = first_present(entry.get("setting_value"), entry.get("value"))
                if name:
                    rows.append((str(name), str(setting_value)))
            elif isinstance(entry, str):
                rows.append((entry, ""))
        return sorted(rows)
    return []

def clamp(value, minimum, maximum):
    return max(minimum, min(maximum, value))

def get_dialogue_trust(conn, save_id, settler_id):
    row = conn.execute(
        "SELECT trust FROM dialogue_states WHERE save_id = ? AND settler_id = ?",
        (save_id, settler_id)
    ).fetchone()
    if row and row["trust"] is not None:
        return float(row["trust"])
    rel = conn.execute(
        "SELECT trust FROM relationships WHERE save_id = ? AND subject = ? AND object IN ('player', 'Player', 'Moshi') ORDER BY updated_at DESC LIMIT 1",
        (save_id, settler_id)
    ).fetchone()
    if rel and rel["trust"] is not None:
        return float(rel["trust"])
    return 0.5

def dialogue_disclosure_level(trust):
    if trust >= 0.75:
        return "high"
    if trust >= 0.4:
        return "normal"
    return "guarded"

def build_dialogue_prompt_context(conn, save_id, settler_id):
    trust = get_dialogue_trust(conn, save_id, settler_id)
    disclosure = dialogue_disclosure_level(trust)
    state = conn.execute(
        "SELECT * FROM dialogue_states WHERE save_id = ? AND settler_id = ?",
        (save_id, settler_id)
    ).fetchone()
    profile = conn.execute(
        "SELECT * FROM npc_memory_profiles WHERE save_id = ? AND settler_id = ?",
        (save_id, settler_id)
    ).fetchone()
    recent_claims = [
        dict(r) for r in conn.execute(
            "SELECT speaker, claim_text, status, created_at FROM dialogue_claims WHERE save_id = ? AND settler_id = ? ORDER BY created_at DESC LIMIT 8",
            (save_id, settler_id)
        )
    ]
    contradictions = [
        dict(r) for r in conn.execute(
            "SELECT claim_text, contradiction_reason, created_at FROM dialogue_claims WHERE save_id = ? AND settler_id = ? AND status = 'contradicted' ORDER BY created_at DESC LIMIT 5",
            (save_id, settler_id)
        )
    ]
    barter_intents = [
        dict(r) for r in conn.execute(
            "SELECT id, intent_type, item, terms, status, created_at FROM dialogue_barter_intents WHERE save_id = ? AND settler_id = ? ORDER BY created_at DESC LIMIT 5",
            (save_id, settler_id)
        )
    ]

    voice = ""
    if state and state["voice_profile"]:
        voice = state["voice_profile"]
    elif profile:
        # P2 slice 2: author a persistent voice profile from traits, role
        # and backstory instead of raw trait cues; persist it so the voice
        # stays stable across conversations.
        voice = build_voice_profile(
            profile["traits"],
            profile["role"],
            state["backstory_voice"] if state else "",
        )
        if not voice and profile["traits"]:
            voice = f"Voice cues from traits: {profile['traits']}"
        if voice and state:
            conn.execute(
                "UPDATE dialogue_states SET voice_profile = ? WHERE save_id = ? AND settler_id = ?",
                (voice, save_id, settler_id)
            )

    trust_events = [
        dict(r) for r in conn.execute(
            "SELECT delta, reason, source, trust_after, created_at FROM trust_events WHERE save_id = ? AND settler_id = ? ORDER BY created_at DESC LIMIT 8",
            (save_id, settler_id)
        )
    ]

    lines = [
        "=== DIALOGUE STATE ===",
        f"Trust toward player: {trust:.2f}",
        f"Disclosure level: {disclosure}",
    ]
    if voice:
        lines.append(f"Voice profile: {voice}")
    if state and state["backstory_voice"]:
        lines.append(f"Backstory voice: {state['backstory_voice']}")
    if contradictions:
        lines.append("Known contradictions:")
        for item in contradictions:
            lines.append(f"- {item['claim_text']} ({item['contradiction_reason'] or 'contradicted'})")
    if recent_claims:
        lines.append("Recent dialogue claims:")
        for item in recent_claims:
            lines.append(f"- {item['speaker']}: {item['claim_text']} [{item['status']}]")
    if barter_intents:
        lines.append("Open barter intents:")
        for item in barter_intents:
            lines.append(f"- {item['intent_type']}: {item['item'] or 'unspecified'}; terms={item['terms'] or 'unspecified'}; status={item['status']}")

    if disclosure == "guarded":
        lines.append("Trust gate: be cautious. Do not reveal secrets, private resentment, scarce supplies, or sensitive colony weaknesses unless directly necessary.")
    elif disclosure == "normal":
        lines.append("Trust gate: answer practical questions, but hold back secrets or dangerous private information.")
    else:
        lines.append("Trust gate: you may share sensitive memories, worries, and specific advice if relevant.")

    lines.append('Response contract: prefer JSON {"dialogue":"...", "claims":["..."], "trust_delta":0.0, "contradiction":null, "barter_intent":null}.')
    return {
        "trust": trust,
        "disclosure_level": disclosure,
        "voice_profile": voice,
        "recent_claims": recent_claims,
        "contradictions": contradictions,
        "barter_intents": barter_intents,
        "trust_events": trust_events,
        "prompt_context": "\n".join(lines),
    }

# --- P2 slice 2: contradiction matching v2 -------------------------------
# Deterministic claim-vs-claim matching. No test-specific vocabulary: a
# conflict requires shared content words plus a negation flip, an antonym
# pair, or a numeric mismatch. Model-provided contradiction objects still
# win when present; this is the automatic fallback the worklog demanded.

DIALOGUE_STOP_WORDS = {
    "the", "and", "but", "you", "said", "say", "that", "this", "with", "from",
    "have", "has", "had", "there", "was", "were", "are", "is", "not", "true",
    "player", "npc", "can", "for", "before", "after", "into", "what", "know",
    "will", "would", "your", "them", "they", "their", "very", "just", "about",
    "want", "wants", "still", "then", "than",
}

NEGATION_TOKENS = {
    "not", "no", "never", "none", "nothing", "isn't", "wasn't", "aren't",
    "don't", "didn't", "doesn't", "cannot", "can't", "won't", "without",
    "lack", "lacks", "lacked", "ran out", "out of",
}

CONTRADICTION_ANTONYM_PAIRS = (
    ("enough", "ravenous"), ("enough", "starving"), ("enough", "hungry"),
    ("plenty", "scarce"), ("plenty", "shortage"), ("abundant", "scarce"),
    ("full", "hungry"), ("fed", "starving"), ("safe", "dangerous"),
    ("alive", "dead"), ("friend", "enemy"), ("healthy", "sick"),
    ("peace", "war"), ("kept", "broke"), ("finished", "unfinished"),
)

CHALLENGE_MARKERS = (
    "you said", "but you said", "that's not true", "that is not true",
    "you lied", "you're lying", "you are lying", "liar", "contradict",
    "that's wrong", "that is wrong", "that's false", "that is false",
    "you told me", "that isn't what",
)

_NUMBER_RE = None

def _claim_numbers(text):
    global _NUMBER_RE
    if _NUMBER_RE is None:
        import re
        _NUMBER_RE = re.compile(r"\b\d+(?:\.\d+)?\b")
    return set(_NUMBER_RE.findall(text or ""))

def _claim_tokens(text):
    words = {w.strip(".,;:!?()[]{}\"'").lower() for w in str(text or "").split()}
    return {w for w in words if len(w) >= 4 and w not in DIALOGUE_STOP_WORDS}

def _has_negation(text):
    lowered = f" {str(text or '').lower()} "
    return any(f" {tok} " in lowered or f" {tok}," in lowered or f" {tok}." in lowered
               for tok in NEGATION_TOKENS)

def _antonym_conflict(text_a, text_b):
    a = str(text_a or "").lower()
    b = str(text_b or "").lower()
    for left, right in CONTRADICTION_ANTONYM_PAIRS:
        if (left in a and right in b) or (right in a and left in b):
            return (left, right)
    return None

def claims_conflict(text_a, text_b):
    """Return a human-readable reason when two claim texts conflict, else None."""
    tokens_a = _claim_tokens(text_a)
    tokens_b = _claim_tokens(text_b)
    overlap = tokens_a.intersection(tokens_b)
    antonyms = _antonym_conflict(text_a, text_b)
    if antonyms and overlap:
        # Antonym pairs are strong signals, but require at least one shared
        # content word to avoid cross-topic false positives.
        return f"Opposite statements ('{antonyms[0]}' vs '{antonyms[1]}') about: {', '.join(sorted(overlap)[:4])}."
    if len(overlap) >= 2:
        neg_a = _has_negation(text_a)
        neg_b = _has_negation(text_b)
        if neg_a != neg_b:
            return f"Negation flip on shared subject: {', '.join(sorted(overlap)[:4])}."
        nums_a = _claim_numbers(text_a)
        nums_b = _claim_numbers(text_b)
        if nums_a and nums_b and nums_a != nums_b and len(overlap) >= 2:
            return f"Conflicting figures ({', '.join(sorted(nums_a)[:2])} vs {', '.join(sorted(nums_b)[:2])}) about: {', '.join(sorted(overlap)[:4])}."
    return None

def detect_dialogue_contradiction(conn, save_id, settler_id, player_text, npc_text, new_claims):
    active_claims = conn.execute("""
        SELECT id, claim_text
        FROM dialogue_claims
        WHERE save_id = ? AND settler_id = ? AND status = 'active'
        ORDER BY created_at DESC
        LIMIT 30
    """, (save_id, settler_id)).fetchall()

    # 1. Self-contradiction: a NEW claim conflicts with a prior active claim.
    for claim in new_claims if isinstance(new_claims, list) else []:
        claim_text = (claim.get("text") or claim.get("claim") or "") if isinstance(claim, dict) else str(claim)
        if not claim_text.strip():
            continue
        for row in active_claims:
            reason = claims_conflict(claim_text, row["claim_text"])
            if reason:
                return {
                    "claim_id": row["id"],
                    "claim": row["claim_text"],
                    "reason": f"New claim '{claim_text.strip()}' conflicts with prior claim. {reason}",
                    "source": "auto_self",
                }

    # 2. Player challenge: player calls out a prior claim explicitly.
    text = f"{player_text or ''} {npc_text or ''}".lower()
    if any(marker in text for marker in CHALLENGE_MARKERS):
        words = _claim_tokens(text)
        for row in active_claims:
            overlap = words.intersection(_claim_tokens(row["claim_text"]))
            if len(overlap) >= 2:
                return {
                    "claim_id": row["id"],
                    "claim": row["claim_text"],
                    "reason": f"Player challenged prior claim; overlapping terms: {', '.join(sorted(overlap)[:5])}.",
                    "source": "auto_challenge",
                }

    # 3. Direct semantic conflict between the player statement and a prior
    # claim (negation flip / antonyms), without an explicit challenge marker.
    if player_text:
        for row in active_claims:
            reason = claims_conflict(player_text, row["claim_text"])
            if reason:
                return {
                    "claim_id": row["id"],
                    "claim": row["claim_text"],
                    "reason": f"Player statement conflicts with prior claim. {reason}",
                    "source": "auto_semantic",
                }
    return None

# --- P2 slice 2: deterministic trust rules --------------------------------

TRUST_RULES = {
    "contradiction_caught": -0.08,
    "repeat_contradiction_step": -0.02,  # escalates with prior offenses
    "contradiction_floor": -0.20,
    "promise_kept": 0.06,
    "promise_broken": -0.10,
    "barter_declined": -0.02,
    "consistent_claims": 0.02,
}
TRUST_MODEL_DELTA_CAP = 0.10           # model-provided delta is advisory
TRUST_EXCHANGE_MIN = -0.25
TRUST_EXCHANGE_MAX = 0.15

def compute_trust_delta(model_delta, contradiction, prior_contradictions, claims_recorded):
    """Blend the model's advisory trust_delta with deterministic rules.
    Returns (delta, reasons)."""
    reasons = []
    model_component = clamp(numeric_or_default(model_delta, 0.0), -TRUST_MODEL_DELTA_CAP, TRUST_MODEL_DELTA_CAP)
    rule_component = 0.0
    if contradiction:
        escalated = TRUST_RULES["contradiction_caught"] + \
            TRUST_RULES["repeat_contradiction_step"] * min(int(prior_contradictions or 0), 6)
        escalated = max(escalated, TRUST_RULES["contradiction_floor"])
        rule_component += escalated
        reasons.append(f"contradiction caught (offense #{int(prior_contradictions or 0) + 1}): {escalated:+.2f}")
        # A caught lie cannot yield a net trust gain from model flattery.
        model_component = min(model_component, 0.0)
    elif claims_recorded:
        rule_component += TRUST_RULES["consistent_claims"]
        reasons.append(f"consistent claims: {TRUST_RULES['consistent_claims']:+.2f}")
    if model_component:
        reasons.append(f"model advisory: {model_component:+.2f}")
    total = clamp(model_component + rule_component, TRUST_EXCHANGE_MIN, TRUST_EXCHANGE_MAX)
    return total, reasons

def record_trust_event(conn, save_id, settler_id, delta, reason, source, trust_after):
    conn.execute("""
        INSERT INTO trust_events (save_id, settler_id, delta, reason, source, trust_after, created_at)
        VALUES (?, ?, ?, ?, ?, ?, ?)
    """, (save_id, settler_id, float(delta), str(reason), str(source),
          float(trust_after), datetime.utcnow().timestamp()))

# --- P2 slice 2: voice authoring -------------------------------------------
# Deterministic personality/backstory voice builder. Replaces the bare
# "Voice cues from traits: ..." string with an authored speech register.

VOICE_TRAIT_STYLES = {
    "proud": "speaks formally and guards their reputation; bristles at slights",
    "reckless": "blunt and quick-tongued; interrupts and overcommits",
    "hungry": "distracted; steers talk toward food and provisions",
    "kind": "warm, patient phrasing; softens bad news",
    "cruel": "cold and cutting; enjoys discomfort",
    "lazy": "drawls, complains about work, bargains to do less",
    "hardworking": "clipped, practical speech; impatient with idle talk",
    "devout": "invokes the saints and scripture; moralizes",
    "cynical": "dry, skeptical asides; doubts fine promises",
    "brave": "steady, direct, unshaken by threats",
    "craven": "hedges, deflects, avoids commitment",
    "curious": "asks questions back; chases tangents",
    "greedy": "circles back to payment and profit",
    "gluttonous": "vivid about meals; trades favors for food",
    "melancholic": "wistful, speaks of losses and old days",
    "sanguine": "cheerful, optimistic turns of phrase",
    "choleric": "short fuse; escalates when contradicted",
    "phlegmatic": "measured, slow to anger, few words",
}

VOICE_ROLE_REGISTERS = {
    "scholar": "learned register with the odd Latin tag",
    "farmer": "earthy rustic idiom, weather-and-soil metaphors",
    "cook": "kitchen metaphors; measures people like recipes",
    "builder": "plain constructive talk; measures twice",
    "miner": "gruff, terse, superstitious about the deep",
    "smith": "hammer-and-forge metaphors; values things well-made",
    "tailor": "precise, fussy about appearances",
    "guard": "clipped watch-report cadence; sizes up threats",
    "steward": "diplomatic, ledger-minded, careful qualifiers",
    "artisan": "flowery about craft, dismissive of shoddy work",
    "animal handler": "gentle cadence, animal similes",
}

def build_voice_profile(traits, role, backstory=""):
    cues = []
    role_key = str(role or "").strip().lower()
    for key, register in VOICE_ROLE_REGISTERS.items():
        if key in role_key:
            cues.append(register)
            break
    seen = set()
    for raw in str(traits or "").replace(";", ",").split(","):
        trait = raw.strip().lower()
        if not trait or trait in seen:
            continue
        seen.add(trait)
        style = VOICE_TRAIT_STYLES.get(trait)
        if style:
            cues.append(style)
        elif trait.isalpha() and 3 <= len(trait) <= 16:
            # Only lexical trait words color the voice; internal tokens like
            # "midageeffector01" from raw game data are skipped.
            cues.append(f"lets their {trait} nature color their words")
    if backstory:
        cues.append(f"draws on their past: {str(backstory).strip()}")
    if not cues:
        return ""
    voice = "Speech register: " + "; ".join(cues[:6]) + ". "
    voice += "Keep a period-appropriate medieval register; avoid modern idioms."
    return voice

def upsert_character_sheet(conn, save_id, settler_id, sheet):
    if not save_id or not settler_id or not isinstance(sheet, dict):
        return
    now = datetime.utcnow().timestamp()
    identity = sheet.get("identity") or {}
    health = sheet.get("health") or {}
    needs = sheet.get("needs") or {}
    activity = sheet.get("current_activity") or {}
    environment = sheet.get("environment") or {}
    equipment = sheet.get("equipment") or {}
    skills = sheet.get("skills") or {}
    skill_xp = sheet.get("skill_experience") or {}
    work_priorities = sheet.get("work_priorities") or {}
    inventory = sheet.get("inventory") or []
    traits = sheet.get("traits") or []
    perks = sheet.get("perks") or []
    mood_logs = sheet.get("mood_logs") or []
    social_logs = sheet.get("social_logs") or []
    belief_logs = sheet.get("belief_logs") or []
    vitals = sheet.get("vitals") or {}
    states = sheet.get("states") or []
    schedule = sheet.get("schedule") or {}
    manage = sheet.get("manage") or sheet.get("manage_settings") or {}
    mood_obj = sheet.get("mood") if isinstance(sheet.get("mood"), dict) else {}
    social_obj = sheet.get("social") if isinstance(sheet.get("social"), dict) else {}
    beliefs_obj = sheet.get("beliefs") if isinstance(sheet.get("beliefs"), dict) else {}
    role_input = first_present(identity.get("profession"), identity.get("role"), sheet.get("profession"), sheet.get("role"))
    background = first_present(identity.get("background_or_role"), identity.get("background"), sheet.get("background_or_role"), sheet.get("background"))
    mood_label = first_present(
        sheet.get("mood") if not isinstance(sheet.get("mood"), dict) else None,
        health.get("mood_state"),
        mood_obj.get("state"),
        mood_obj.get("label"),
    )
    mood_score = first_present(sheet.get("mood_score"), health.get("mood"), mood_obj.get("score"), mood_obj.get("value"))
    health_current = first_present(health.get("current"), health.get("hitpoints"), health.get("hp"), sheet.get("health_current"))
    health_max = first_present(health.get("max"), health.get("max_hitpoints"), health.get("max_hp"), sheet.get("health_max"), health_current)
    activity_description = first_present(activity.get("description"), activity.get("activity"), sheet.get("activity"))
    activity_type = first_present(activity.get("type"), activity.get("state"))
    schedule_label = first_present(activity.get("schedule"), sheet.get("schedule_label"))
    room = first_present(environment.get("room"), activity.get("room"), activity.get("chamber"), sheet.get("room"))
    role = normalize_profile_role(role_input, format_skill_stats(skills))

    conn.execute("""
        INSERT OR REPLACE INTO character_sheets
        (save_id, settler_id, updated_at, name, role, background, pseudonym, age, gender,
         mood, mood_score, health_current, health_max, activity_type, activity_description,
         schedule_label, room, raw_json)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, (
        save_id,
        settler_id,
        now,
        identity.get("name") or sheet.get("name"),
        role,
        background,
        identity.get("pseudonym") or sheet.get("pseudonym"),
        int_or_default(first_present(identity.get("age"), sheet.get("age"))),
        identity.get("gender") or sheet.get("gender"),
        mood_label,
        numeric_or_default(mood_score),
        numeric_or_default(health_current),
        numeric_or_default(health_max),
        activity_type,
        activity_description,
        schedule_label,
        room,
        json_dumps_stable(sheet),
    ))

    name = identity.get("name") or sheet.get("name") or settler_id
    conn.execute("""
        INSERT OR REPLACE INTO npcs
        (npc_key, settler_id, npc_id, save_id, name, role, traits, stats, created_at, last_active, is_alive)
        VALUES (
            ?,
            ?,
            COALESCE((SELECT npc_id FROM npcs WHERE save_id = ? AND settler_id = ?), ?),
            ?,
            ?,
            ?,
            ?,
            ?,
            COALESCE((SELECT created_at FROM npcs WHERE save_id = ? AND settler_id = ?), ?),
            ?,
            1
        )
    """, (
        settler_id,
        settler_id,
        save_id,
        settler_id,
        f"{settler_id}_sheet",
        save_id,
        name,
        role,
        ", ".join(str(t) for t in traits),
        format_skill_stats(skills),
        save_id,
        settler_id,
        now,
        now,
    ))

    upsert_memory_profile(
        conn,
        save_id,
        settler_id,
        name=identity.get("name") or sheet.get("name"),
        role=role_input,
        traits=", ".join(str(t) for t in traits),
        stats=format_skill_stats(skills),
    )

    conn.execute("DELETE FROM character_sheet_skills WHERE save_id = ? AND settler_id = ?", (save_id, settler_id))
    for skill_name, level in skills.items():
        conn.execute("""
            INSERT INTO character_sheet_skills
            (save_id, settler_id, skill_name, level, experience, updated_at)
            VALUES (?, ?, ?, ?, ?, ?)
        """, (save_id, settler_id, str(skill_name), int(level or 0), float(skill_xp.get(skill_name, 0) or 0), now))

    conn.execute("DELETE FROM character_sheet_work_priorities WHERE save_id = ? AND settler_id = ?", (save_id, settler_id))
    for job_name, priority in work_priorities.items():
        conn.execute("""
            INSERT INTO character_sheet_work_priorities
            (save_id, settler_id, job_name, priority, updated_at)
            VALUES (?, ?, ?, ?, ?)
        """, (save_id, settler_id, str(job_name), int(priority or 0), now))

    conn.execute("DELETE FROM character_sheet_needs WHERE save_id = ? AND settler_id = ?", (save_id, settler_id))
    for need_name, value in needs.items():
        conn.execute("""
            INSERT INTO character_sheet_needs
            (save_id, settler_id, need_name, value, updated_at)
            VALUES (?, ?, ?, ?, ?)
        """, (save_id, settler_id, str(need_name), float(value or 0), now))

    conn.execute("DELETE FROM character_sheet_equipment WHERE save_id = ? AND settler_id = ?", (save_id, settler_id))
    for slot, item in equipment.items():
        if item:
            conn.execute("""
                INSERT INTO character_sheet_equipment
                (save_id, settler_id, slot, item, updated_at)
                VALUES (?, ?, ?, ?, ?)
            """, (save_id, settler_id, str(slot), str(item), now))
    for idx, item in enumerate(inventory):
        conn.execute("""
            INSERT INTO character_sheet_equipment
            (save_id, settler_id, slot, item, quantity, updated_at)
            VALUES (?, ?, ?, ?, ?, ?)
        """, (save_id, settler_id, f"inventory_{idx}", str(item), 1, now))

    conn.execute("DELETE FROM character_sheet_traits WHERE save_id = ? AND settler_id = ?", (save_id, settler_id))
    for kind, values in (("trait", traits), ("perk", perks), ("state", states), ("vital", list(vitals.keys()))):
        for value in values:
            conn.execute("""
                INSERT INTO character_sheet_traits
                (save_id, settler_id, kind, value, detail, updated_at)
                VALUES (?, ?, ?, ?, ?, ?)
            """, (save_id, settler_id, kind, str(value), str(vitals.get(value, "")) if kind == "vital" else "", now))

    conn.execute("DELETE FROM character_sheet_mood_modifiers WHERE save_id = ? AND settler_id = ?", (save_id, settler_id))
    modifier_groups = (
        ("mood", normalize_modifier_entries(mood_logs) + normalize_modifier_entries(mood_obj.get("modifiers"))),
        ("social", normalize_modifier_entries(social_logs) + normalize_modifier_entries(social_obj.get("modifiers"))),
        ("belief", normalize_modifier_entries(belief_logs) + normalize_modifier_entries(beliefs_obj.get("modifiers"))),
    )
    for kind, values in modifier_groups:
        for label, score in values:
            conn.execute("""
                INSERT INTO character_sheet_mood_modifiers
                (save_id, settler_id, kind, label, value, updated_at)
                VALUES (?, ?, ?, ?, ?, ?)
            """, (save_id, settler_id, kind, label, score, now))

    conn.execute("DELETE FROM character_sheet_schedule WHERE save_id = ? AND settler_id = ?", (save_id, settler_id))
    for hour, schedule_activity in normalize_schedule_entries(schedule):
        conn.execute("""
            INSERT INTO character_sheet_schedule
            (save_id, settler_id, hour, activity, updated_at)
            VALUES (?, ?, ?, ?, ?)
        """, (save_id, settler_id, hour, schedule_activity, now))

    conn.execute("DELETE FROM character_sheet_manage_settings WHERE save_id = ? AND settler_id = ?", (save_id, settler_id))
    for setting_name, setting_value in normalize_manage_entries(manage):
        conn.execute("""
            INSERT INTO character_sheet_manage_settings
            (save_id, settler_id, setting_name, setting_value, updated_at)
            VALUES (?, ?, ?, ?, ?)
        """, (save_id, settler_id, setting_name, setting_value, now))

def format_skill_stats(skills):
    if not isinstance(skills, dict) or not skills:
        return None
    return ", ".join(f"{name}:{value}" for name, value in sorted(skills.items(), key=lambda kv: (-int(kv[1] or 0), str(kv[0]))))

def refresh_memory_profile_summary(conn, save_id, settler_id):
    if not save_id or not settler_id:
        return
    counts = conn.execute("""
        SELECT category, COUNT(*) AS count
        FROM typed_memories
        WHERE save_id = ? AND settler_id = ?
        GROUP BY category
        ORDER BY count DESC, category ASC
        LIMIT 4
    """, (save_id, settler_id)).fetchall()
    latest = conn.execute("""
        SELECT category, content
        FROM typed_memories
        WHERE save_id = ? AND settler_id = ?
        ORDER BY created_at DESC
        LIMIT 3
    """, (save_id, settler_id)).fetchall()
    if not counts and not latest:
        return

    count_text = ", ".join(f"{row['count']} {row['category']}" for row in counts)
    latest_text = " | ".join(f"{row['category']}: {row['content'][:90]}" for row in latest)
    summary = f"Memory profile: {count_text}."
    if latest_text:
        summary += f" Recent: {latest_text}"
    conn.execute("""
        UPDATE npc_memory_profiles
        SET evolving_summary = ?, updated_at = ?
        WHERE save_id = ? AND settler_id = ?
    """, (summary[:1200], datetime.utcnow().timestamp(), save_id, settler_id))

def find_game_window(gw):
    windows = gw.getWindowsWithTitle("Going Medieval")
    candidates = []
    for window in windows:
        title = (window.title or "").strip()
        if "Going Medieval" not in title:
            continue
        excluded_title_parts = (
            "LLM NPCs",
            "Memory & Decision Dashboard",
            "127.0.0.1",
            "Chrome",
            "File Explorer",
            "BepInEx",
            "LogOutput",
            "Command Prompt",
            "PowerShell",
        )
        if any(part.lower() in title.lower() for part in excluded_title_parts):
            continue
        candidates.append(window)

    if not candidates:
        return None

    visible = [
        w for w in candidates
        if getattr(w, "width", 0) > 300
        and getattr(w, "height", 0) > 200
        and getattr(w, "left", 0) > -30000
        and getattr(w, "top", 0) > -30000
    ]
    exact = [w for w in visible if (w.title or "").strip().lower() == "going medieval"]
    if exact:
        return exact[0]
    return visible[0] if visible else candidates[0]

def seed_lore(conn):
    lore_entries = [
        ("encyclopedia", "farming", "Farming & Agriculture Guide", 
         "Going Medieval Farming Guide:\n"
         "- Crops have growth seasons. Cabbage grows fast in Spring/Summer. Barley is sown in Spring/Autumn and harvested in Summer. Flax is sown in Spring and harvested in Summer.\n"
         "- Soil fertility affects growth speed. Soil has 100% fertility, fertile soil has 120%, and rocky soil has 50% fertility. Sowing on fertile soil is highly recommended.\n"
         "- Temperature: Crops will die if temperature falls below freezing (0C) or exceeds heat limits (around 40C). Plan harvests before winter frost sets in.\n"
         "- Harvest yield depends on settler's Agronomy skill level. Low skill might destroy crops during harvest."),
        
        ("encyclopedia", "cooking", "Cooking & Food Preservation Guide",
         "Going Medieval Culinary & Food Preservation Guide:\n"
         "- Raw foods can cause food poisoning. Always cook raw meat and vegetables into meals (Simple, Lavish) at a Hearth or Cookhouse.\n"
         "- Food decay is driven by temperature and room type. Food decays rapidly in warm rooms. To preserve food, store it in an underground room (Cellar) where temperature naturally stays cool (around 2C to 5C).\n"
         "- Smokehouse: Smoke meat and fish to turn them into packaged provisions that last much longer without refrigeration.\n"
         "- Winter Prep: Always store at least 20 meals per settler before winter starts, as crops cannot grow in winter."),
        
        ("encyclopedia", "combat", "Colony Defense & Combat Guide",
         "Going Medieval Combat & Defense Guide:\n"
         "- High Ground Bonus: Ranged fighters (archers) deal significantly more damage and have longer range when positioned on elevated structures (battlements, towers, walls) relative to their targets.\n"
         "- Equipment: Guards and fighters should equip high-quality armor (mail, plate, leather) and weapons (swords, axes, bows, crossbows) according to their Melee and Marksman skills.\n"
         "- Defensive Chokepoints: Force enemies through narrow gates guarded by traps and surrounded by battlements to easily defeat raids.\n"
         "- Draft State: During a raid alert, all settlers should be drafted, equipped with weapons, and sent to designated defense positions."),
        
        ("encyclopedia", "health", "Medical Care & Recovery Guide",
         "Going Medieval Medical Care Guide:\n"
         "- Injury and bleeding: Wounded settlers bleed out over time. They must be tended immediately by a doctor using herbs or healing kits in a clean bed.\n"
         "- Infections: Open wounds can become infected if untended or treated in dirty rooms. Infections are highly lethal if immunity doesn't outpace the infection level.\n"
         "- Resting: Injured or sick settlers must rest in bed to speed up recovery and boost immunity.\n"
         "- Temperature distress: Extremely cold or hot weather leads to hypothermia or heat stroke. Keep settlers indoors near heat sources (fireplaces, braziers) in winter, and wearing light summer clothes in summer."),
        
        ("encyclopedia", "building", "Structural Stability & Construction Guide",
         "Going Medieval Construction & Stability Guide:\n"
         "- Building stability: Every floor and wall tile requires structural stability to prevent collapse. Stability flows from ground-touching walls or pillars.\n"
         "- Support beams: Use wooden or stone beams to span wide rooms and support ceiling tiles. Wall-attached floor tiles can extend up to 2 tiles without support, but require beams for wider spans.\n"
         "- Materials: Wood is cheap and fast but weak and flammable. Clay and stone are highly durable, fireproof, and provide superior thermal insulation."),
        
        ("encyclopedia", "winter", "Winter Survival Guide",
         "Going Medieval Winter Survival Guide:\n"
         "- Cold protection: Settlers must wear winter clothing (winter clothes, caps) when venturing outside. Suffer hypothermia if exposed without insulation.\n"
         "- Heating: Place clay braziers or stone fireplaces in bedrooms and common halls. Feed them with fuel (wood, coal) to keep indoor temperatures warm (above 15C).\n"
         "- Fuel gathering: Prioritize woodcutting and coal mining in autumn to build a fuel stockpile for winter."),
        
        ("encyclopedia", "mining", "Mining & Underground insulation Guide",
         "Going Medieval Mining & Excavation Guide:\n"
         "- Resources: Mine underground deposits for coal, iron ore, clay, limestone, and salt.\n"
         "- Natural insulation: Underground rooms excavated into soil or rock have natural thermal insulation. They stay naturally cool in summer and warm in winter, making them perfect for food cellar storage.\n"
         "- Support pillars: Always leave natural soil pillars or build wooden support columns when mining large underground chambers to prevent cave-ins.")
    ]
    with conn:
        for kind, key, title, text in lore_entries:
            conn.execute(
                "INSERT OR REPLACE INTO lore (save_id, kind, key, title, text, updated_at) VALUES ('all', ?, ?, ?, ?, ?)",
                (kind, key, title, text, datetime.utcnow().timestamp())
            )

def init_db():
    """Create database tables if they do not exist, matching X4 schemas."""
    conn = get_db_connection()
    try:
        with conn:
            # 1. npcs table (X4 schema)
            conn.execute("""
                CREATE TABLE IF NOT EXISTS npcs (
                    npc_key TEXT PRIMARY KEY,
                    settler_id TEXT,
                    npc_id TEXT,
                    save_id TEXT,
                    game_id TEXT,
                    name TEXT,
                    traits TEXT,
                    faction_id TEXT,
                    summary TEXT DEFAULT '',
                    created_at REAL,
                    last_active REAL,
                    race TEXT,
                    role TEXT,
                    ship_class TEXT,
                    gender TEXT,
                    ship_name TEXT,
                    sector TEXT,
                    skills TEXT,
                    stats TEXT,
                    tier INTEGER DEFAULT 0,
                    authority TEXT,
                    role_in_faction TEXT,
                    bound_entity_id TEXT,
                    bound_entity_type TEXT,
                    is_alive INTEGER DEFAULT 1
                );
            """)
            
            # 2. relationships table (X4 + Going Medieval merged schema)
            conn.execute("""
                CREATE TABLE IF NOT EXISTS relationships (
                    save_id TEXT,
                    subject TEXT,
                    object TEXT,
                    friendship REAL DEFAULT 0.0,
                    romance REAL DEFAULT 0.0,
                    rivalry REAL DEFAULT 0.0,
                    trust REAL DEFAULT 0.5,
                    attraction REAL DEFAULT 0.0,
                    fear REAL DEFAULT 0.0,
                    resentment REAL DEFAULT 0.0,
                    standing TEXT DEFAULT 'neutral',
                    relationship_type TEXT DEFAULT 'strangers',
                    is_married INTEGER DEFAULT 0,
                    marriage_date REAL,
                    total_interactions INTEGER DEFAULT 0,
                    positive_interactions INTEGER DEFAULT 0,
                    negative_interactions INTEGER DEFAULT 0,
                    last_interaction REAL,
                    summary TEXT DEFAULT '',
                    updated_at REAL,
                    PRIMARY KEY (save_id, subject, object)
                );
            """)
            
            # 3. turns table (X4 schema)
            conn.execute("""
                CREATE TABLE IF NOT EXISTS turns (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    npc_key TEXT NOT NULL,
                    role TEXT NOT NULL,
                    text TEXT NOT NULL,
                    ts REAL NOT NULL
                );
            """)
            
            # 4. facts table (X4 schema)
            conn.execute("""
                CREATE TABLE IF NOT EXISTS facts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    npc_key TEXT NOT NULL,
                    text TEXT NOT NULL,
                    category TEXT NOT NULL,
                    tier TEXT NOT NULL,
                    importance INTEGER NOT NULL,
                    verbatim INTEGER NOT NULL,
                    created_at REAL NOT NULL,
                    last_used_at REAL NOT NULL
                );
            """)

            # Legacy dashboard memory tables. C# now writes to facts, but the
            # dashboard simulator and detail views still display these rows.
            conn.execute("""
                CREATE TABLE IF NOT EXISTS memories (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    npc_id TEXT NOT NULL,
                    level INTEGER DEFAULT 0,
                    timestamp REAL NOT NULL,
                    session_seq INTEGER DEFAULT 0,
                    event_type TEXT NOT NULL,
                    content TEXT NOT NULL,
                    importance INTEGER DEFAULT 5,
                    save_id TEXT NOT NULL
                );
            """)

            conn.execute("""
                CREATE TABLE IF NOT EXISTS permanent_memories (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    npc_id TEXT NOT NULL,
                    timestamp REAL NOT NULL,
                    event_type TEXT NOT NULL,
                    content TEXT NOT NULL,
                    importance INTEGER DEFAULT 10,
                    save_id TEXT NOT NULL
                );
            """)
            
            # 5. lore table (X4 schema)
            conn.execute("""
                CREATE TABLE IF NOT EXISTS lore (
                    save_id TEXT,
                    kind TEXT,
                    key TEXT,
                    title TEXT DEFAULT '',
                    text TEXT NOT NULL,
                    updated_at REAL,
                    PRIMARY KEY (save_id, kind, key)
                );
            """)
            
            # 6. conversations table (X4 schema)
            conn.execute("""
                CREATE TABLE IF NOT EXISTS conversations (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    save_id TEXT NOT NULL,
                    request_id TEXT,
                    faction_id TEXT,
                    npc_name TEXT,
                    source_mod TEXT,
                    prompt TEXT DEFAULT '',
                    reply TEXT DEFAULT '',
                    latency_ms INTEGER,
                    status TEXT DEFAULT '',
                    created_at REAL NOT NULL,
                    player_name TEXT DEFAULT ''
                );
            """)
            
            # 7. incidents table (X4 schema)
            conn.execute("""
                CREATE TABLE IF NOT EXISTS incidents (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    save_id TEXT NOT NULL,
                    settler_id TEXT,
                    timestamp REAL,
                    action TEXT,
                    reasoning TEXT,
                    success INTEGER DEFAULT 1,
                    faction_id TEXT,
                    action_type TEXT NOT NULL,
                    target TEXT,
                    confidence REAL DEFAULT 0,
                    priority INTEGER DEFAULT 0,
                    cooldown_until REAL DEFAULT 0,
                    narrative TEXT DEFAULT '',
                    effects_json TEXT,
                    status TEXT DEFAULT 'pending',
                    created_at REAL NOT NULL,
                    applied_at REAL
                );
            """)

            # 8. colony_events table (Going Medieval specific - Phase 3)
            conn.execute("""
                CREATE TABLE IF NOT EXISTS colony_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    save_id TEXT NOT NULL,
                    timestamp REAL NOT NULL,
                    avg_mood REAL NOT NULL,
                    avg_hunger REAL NOT NULL,
                    avg_health REAL NOT NULL,
                    total_settlers INTEGER NOT NULL,
                    idle_settlers INTEGER NOT NULL,
                    colony_wealth REAL NOT NULL,
                    threat_level REAL NOT NULL,
                    pending_blueprints INTEGER NOT NULL,
                    recommendation_type TEXT NOT NULL,
                    recommendation_desc TEXT NOT NULL,
                    narrative TEXT
                );
            """)

            # 9. settler_pressures table (Going Medieval specific - Stage 1 pressures for UI)
            conn.execute("""
                CREATE TABLE IF NOT EXISTS settler_pressures (
                    settler_id           TEXT NOT NULL,
                    save_id              TEXT NOT NULL,
                    timestamp            REAL NOT NULL,
                    hunger_pressure       REAL DEFAULT 0.0,
                    thirst_pressure       REAL DEFAULT 0.0,
                    exhaustion_pressure   REAL DEFAULT 0.0,
                    injury_pressure       REAL DEFAULT 0.0,
                    illness_pressure      REAL DEFAULT 0.0,
                    threat_pressure       REAL DEFAULT 0.0,
                    raid_alert            REAL DEFAULT 0.0,
                    mood_pressure         REAL DEFAULT 0.0,
                    recreation_lag        REAL DEFAULT 0.0,
                    work_skill_mismatch   REAL DEFAULT 0.0,
                    idle_pressure         REAL DEFAULT 0.0,
                    social_debt           REAL DEFAULT 0.0,
                    relationship_tension  REAL DEFAULT 0.0,
                    colony_need_score     REAL DEFAULT 0.0,
                    haul_need             REAL DEFAULT 0.0,
                    attire_mismatch       REAL DEFAULT 0.0,
                    PRIMARY KEY (settler_id, save_id)
                );
            """)

            # 10. P1 NPC personal files. This is the durable "who this
            # character is" record used by memory-aware dialogue and later
            # systems.
            conn.execute("""
                CREATE TABLE IF NOT EXISTS npc_memory_profiles (
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    display_name TEXT,
                    role TEXT,
                    traits TEXT,
                    stats TEXT,
                    description TEXT DEFAULT '',
                    evolving_summary TEXT DEFAULT '',
                    memories_count INTEGER DEFAULT 0,
                    secrets_count INTEGER DEFAULT 0,
                    first_seen REAL NOT NULL,
                    last_seen REAL NOT NULL,
                    updated_at REAL NOT NULL,
                    PRIMARY KEY (save_id, settler_id)
                );
            """)

            # 11. P1 typed memory ledger. This preserves the existing facts and
            # legacy memories tables while adding categories required by the
            # NPC Memory System roadmap.
            conn.execute("""
                CREATE TABLE IF NOT EXISTS typed_memories (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    category TEXT NOT NULL,
                    tier TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    content TEXT NOT NULL,
                    importance INTEGER DEFAULT 5,
                    confidence REAL DEFAULT 1.0,
                    is_secret INTEGER DEFAULT 0,
                    source TEXT DEFAULT 'game',
                    source_table TEXT,
                    source_id INTEGER,
                    created_at REAL NOT NULL,
                    last_used_at REAL NOT NULL,
                    metadata_json TEXT DEFAULT '{}'
                );
            """)

            # 12. Full character-sheet tracking. Every snapshot keeps raw JSON
            # plus normalized rows for the tabs shown by Going Medieval.
            conn.execute("""
                CREATE TABLE IF NOT EXISTS character_sheets (
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    updated_at REAL NOT NULL,
                    name TEXT,
                    role TEXT,
                    background TEXT,
                    pseudonym TEXT,
                    age INTEGER DEFAULT 0,
                    gender TEXT,
                    mood TEXT,
                    mood_score REAL DEFAULT 0,
                    health_current REAL DEFAULT 0,
                    health_max REAL DEFAULT 0,
                    activity_type TEXT,
                    activity_description TEXT,
                    schedule_label TEXT,
                    room TEXT,
                    raw_json TEXT NOT NULL,
                    PRIMARY KEY (save_id, settler_id)
                );
            """)

            conn.execute("""
                CREATE TABLE IF NOT EXISTS character_sheet_skills (
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    skill_name TEXT NOT NULL,
                    level INTEGER DEFAULT 0,
                    experience REAL DEFAULT 0,
                    updated_at REAL NOT NULL,
                    PRIMARY KEY (save_id, settler_id, skill_name)
                );
            """)

            conn.execute("""
                CREATE TABLE IF NOT EXISTS character_sheet_work_priorities (
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    job_name TEXT NOT NULL,
                    priority INTEGER DEFAULT 0,
                    updated_at REAL NOT NULL,
                    PRIMARY KEY (save_id, settler_id, job_name)
                );
            """)

            conn.execute("""
                CREATE TABLE IF NOT EXISTS character_sheet_needs (
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    need_name TEXT NOT NULL,
                    value REAL DEFAULT 0,
                    updated_at REAL NOT NULL,
                    PRIMARY KEY (save_id, settler_id, need_name)
                );
            """)

            conn.execute("""
                CREATE TABLE IF NOT EXISTS character_sheet_equipment (
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    slot TEXT NOT NULL,
                    item TEXT NOT NULL,
                    quantity REAL DEFAULT 0,
                    updated_at REAL NOT NULL,
                    PRIMARY KEY (save_id, settler_id, slot, item)
                );
            """)

            conn.execute("""
                CREATE TABLE IF NOT EXISTS character_sheet_traits (
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    value TEXT NOT NULL,
                    detail TEXT DEFAULT '',
                    updated_at REAL NOT NULL,
                    PRIMARY KEY (save_id, settler_id, kind, value)
                );
            """)

            conn.execute("""
                CREATE TABLE IF NOT EXISTS character_sheet_mood_modifiers (
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    label TEXT NOT NULL,
                    value REAL,
                    updated_at REAL NOT NULL,
                    PRIMARY KEY (save_id, settler_id, kind, label)
                );
            """)

            conn.execute("""
                CREATE TABLE IF NOT EXISTS character_sheet_schedule (
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    hour INTEGER NOT NULL,
                    activity TEXT NOT NULL,
                    updated_at REAL NOT NULL,
                    PRIMARY KEY (save_id, settler_id, hour)
                );
            """)

            conn.execute("""
                CREATE TABLE IF NOT EXISTS character_sheet_manage_settings (
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    setting_name TEXT NOT NULL,
                    setting_value TEXT,
                    updated_at REAL NOT NULL,
                    PRIMARY KEY (save_id, settler_id, setting_name)
                );
            """)

            # 13. P2 dialogue state. These tables make direct player dialogue
            # memory-aware beyond generic transcript rows: trust gates what can
            # be disclosed, NPC voice can evolve, and claims/barter hooks remain
            # queryable after the conversation ends.
            conn.execute("""
                CREATE TABLE IF NOT EXISTS dialogue_states (
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    trust REAL DEFAULT 0.5,
                    disclosure_level TEXT DEFAULT 'normal',
                    voice_profile TEXT DEFAULT '',
                    backstory_voice TEXT DEFAULT '',
                    contradiction_count INTEGER DEFAULT 0,
                    barter_intent_count INTEGER DEFAULT 0,
                    updated_at REAL NOT NULL,
                    PRIMARY KEY (save_id, settler_id)
                );
            """)

            conn.execute("""
                CREATE TABLE IF NOT EXISTS dialogue_claims (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    speaker TEXT NOT NULL,
                    claim_text TEXT NOT NULL,
                    confidence REAL DEFAULT 1.0,
                    status TEXT DEFAULT 'active',
                    contradicted_by_claim_id INTEGER,
                    contradiction_reason TEXT DEFAULT '',
                    created_at REAL NOT NULL
                );
            """)

            conn.execute("""
                CREATE TABLE IF NOT EXISTS dialogue_barter_intents (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    intent_type TEXT DEFAULT 'barter',
                    item TEXT DEFAULT '',
                    terms TEXT DEFAULT '',
                    status TEXT DEFAULT 'proposed',
                    raw_json TEXT DEFAULT '{}',
                    created_at REAL NOT NULL
                );
            """)

            # Migrate older databases created before the dashboard and C# HTTP
            # contracts were aligned.
            ensure_column(conn, "npcs", "settler_id", "settler_id TEXT")
            ensure_column(conn, "npcs", "traits", "traits TEXT")
            ensure_column(conn, "incidents", "settler_id", "settler_id TEXT")
            ensure_column(conn, "incidents", "timestamp", "timestamp REAL")
            ensure_column(conn, "incidents", "action", "action TEXT")
            ensure_column(conn, "incidents", "reasoning", "reasoning TEXT")
            ensure_column(conn, "incidents", "success", "success INTEGER DEFAULT 1")
            ensure_column(conn, "facts", "save_id", "save_id TEXT DEFAULT ''")
            ensure_column(conn, "facts", "settler_id", "settler_id TEXT DEFAULT ''")
            ensure_column(conn, "facts", "source_typed_memory_id", "source_typed_memory_id INTEGER")
            ensure_column(conn, "memories", "typed_memory_id", "typed_memory_id INTEGER")
            ensure_column(conn, "permanent_memories", "typed_memory_id", "typed_memory_id INTEGER")
            ensure_column(conn, "character_sheet_mood_modifiers", "value", "value REAL")

            conn.execute("CREATE INDEX IF NOT EXISTS idx_typed_memories_npc_time ON typed_memories(save_id, settler_id, created_at DESC)")
            conn.execute("CREATE INDEX IF NOT EXISTS idx_typed_memories_npc_category ON typed_memories(save_id, settler_id, category)")
            conn.execute("CREATE INDEX IF NOT EXISTS idx_facts_npc_save ON facts(npc_key, save_id, created_at DESC)")
            conn.execute("CREATE INDEX IF NOT EXISTS idx_character_sheets_save ON character_sheets(save_id, updated_at DESC)")
            conn.execute("CREATE INDEX IF NOT EXISTS idx_dialogue_claims_npc_time ON dialogue_claims(save_id, settler_id, created_at DESC)")
            conn.execute("CREATE INDEX IF NOT EXISTS idx_dialogue_barter_npc_time ON dialogue_barter_intents(save_id, settler_id, created_at DESC)")

            # 14. P2 slice 2: auditable trust changes. Every trust delta is a
            # row so the dashboard can show WHY a settler trusts the player.
            conn.execute("""
                CREATE TABLE IF NOT EXISTS trust_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    save_id TEXT NOT NULL,
                    settler_id TEXT NOT NULL,
                    delta REAL NOT NULL,
                    reason TEXT DEFAULT '',
                    source TEXT DEFAULT 'exchange',
                    trust_after REAL,
                    created_at REAL NOT NULL
                );
            """)
            conn.execute("CREATE INDEX IF NOT EXISTS idx_trust_events_npc_time ON trust_events(save_id, settler_id, created_at DESC)")

            # 15. P3+ world-system tables (orders, entities, events,
            # diplomacy, romance, death history, disease, combat).
            gm_systems.ensure_tables(conn)
        
        # Seed lore entries
        seed_lore(conn)
        print("[DB] SQLite database initialized and seeded successfully.")
    except Exception as e:
        print(f"[DB] Error initializing database: {e}")
    finally:
        conn.close()

# Memory condensation is disabled in favor of full RoleRAG system context.

def get_player2_port():
    """Discover port from %APPDATA%/game.player2.client/api.port or return default 4315."""
    try:
        port_file = Path(os.path.expandvars(r'%APPDATA%\game.player2.client\api.port'))
        if port_file.exists():
            return int(port_file.read_text().strip())
    except:
        pass
    return 4315

def check_player2_health():
    port = get_player2_port()
    try:
        url = f"http://127.0.0.1:{port}/v1/health"
        req = urllib.request.Request(url, method="GET")
        with urllib.request.urlopen(req, timeout=1.5) as response:
            if response.status == 200:
                data = json.loads(response.read().decode("utf-8"))
                return {"online": True, "port": port, "version": data.get("client_version", "unknown")}
    except Exception as e:
        return {"online": False, "port": port, "error": str(e)}
    return {"online": False, "port": port}

class DashboardHandler(BaseHTTPRequestHandler):
    def _send_json(self, status, payload):
        body = json.dumps(payload, indent=2).encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Access-Control-Allow-Origin', '*')
        self.send_header('Access-Control-Allow-Methods', 'GET, POST, OPTIONS')
        self.send_header('Access-Control-Allow-Headers', 'Content-Type')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _send_file(self, path, content_type):
        if not path.exists() or not path.is_file():
            self._send_json(404, {"ok": False, "error": "file not found"})
            return
        body = path.read_bytes()
        self.send_response(200)
        self.send_header('Content-Type', content_type)
        self.send_header('Access-Control-Allow-Origin', '*')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_OPTIONS(self):
        self.send_response(200)
        self.send_header('Access-Control-Allow-Origin', '*')
        self.send_header('Access-Control-Allow-Methods', 'GET, POST, OPTIONS')
        self.send_header('Access-Control-Allow-Headers', 'Content-Type')
        self.end_headers()

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        query = urllib.parse.parse_qs(parsed.query)
        path = parsed.path

        dashboard_dir = Path(__file__).resolve().parent

        # Static file serving
        if path in ("/", "/dashboard", "/index.html"):
            self._send_file(dashboard_dir / "index.html", "text/html; charset=utf-8")
            return
        elif path == "/styles.css":
            self._send_file(dashboard_dir / "styles.css", "text/css")
            return
        elif path == "/app.js":
            self._send_file(dashboard_dir / "app.js", "application/javascript")
            return

        # API Endpoints
        if path == "/health":
            p2_status = check_player2_health()
            self._send_json(200, {
                "ok": True,
                "server_boot_id": SERVER_BOOT_ID,
                "server_boot_utc": SERVER_BOOT_UTC,
                "database_path": str(DB_PATH),
                "database_exists": DB_PATH.exists(),
                "player2": p2_status
            })
            return

        elif path == "/api/game/screen":
            force_focus = "force_focus" in query and query["force_focus"][0] == "true"
            
            import pygetwindow as gw
            import win32gui
            import win32con
            import mss
            from PIL import Image
            import io
            
            game_window = find_game_window(gw)
            
            if not game_window:
                self._send_json(404, {"error": "not found", "message": "Going Medieval window not found"})
                return
            
            hwnd = game_window._hWnd
            
            if force_focus:
                try:
                    if win32gui.IsIconic(hwnd):
                        win32gui.ShowWindow(hwnd, win32con.SW_RESTORE)
                    win32gui.ShowWindow(hwnd, win32con.SW_SHOW)
                    import pyautogui
                    pyautogui.press('alt')
                    win32gui.SetForegroundWindow(hwnd)
                except Exception as e:
                    print("Error focusing window:", e)
            
            try:
                rect = win32gui.GetWindowRect(hwnd)
                left, top, right, bottom = rect
                width = right - left
                height = bottom - top
                if width <= 300 or height <= 200 or left <= -30000 or top <= -30000:
                    self._send_json(409, {
                        "error": "window_not_visible",
                        "message": "Going Medieval window is minimized or too small to capture",
                        "title": game_window.title,
                        "left": left,
                        "top": top,
                        "width": width,
                        "height": height
                    })
                    return
                
                with mss.mss() as sct:
                    monitor = {
                        "left": left,
                        "top": top,
                        "width": width,
                        "height": height
                    }
                    sct_img = sct.grab(monitor)
                    img = Image.frombytes("RGB", sct_img.size, sct_img.bgra, "raw", "BGRX")
                    
                    output = io.BytesIO()
                    img.save(output, format="JPEG", quality=80)
                    jpeg_bytes = output.getvalue()
                    
                    self.send_response(200)
                    self.send_header("Content-Type", "image/jpeg")
                    self.send_header("Access-Control-Allow-Origin", "*")
                    self.send_header("Content-Length", str(len(jpeg_bytes)))
                    self.end_headers()
                    self.wfile.write(jpeg_bytes)
            except Exception as e:
                self._send_json(500, {"error": "capture_failed", "message": str(e)})
            return

        elif path == "/api/memory/saves":
            # Find distinct save IDs
            conn = get_db_connection()
            try:
                saves = set()
                for table in ("npcs", "memories", "permanent_memories", "settler_pressures", "incidents", "npc_memory_profiles", "typed_memories", "character_sheets"):
                    try:
                        cursor = conn.execute(f"SELECT DISTINCT save_id FROM {table}")
                        for row in cursor:
                            if row["save_id"]:
                                saves.add(row["save_id"])
                    except:
                        pass
                self._send_json(200, {"saves": sorted(list(saves))})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/memory/npcs":
            save_id = query.get("save_id", [""])[0]
            conn = get_db_connection()
            try:
                cursor = conn.execute("""
                    SELECT n.settler_id, n.save_id, n.npc_id, n.name, n.role, n.traits, n.stats,
                           p.memories_count, p.secrets_count, p.description, p.evolving_summary
                    FROM npcs n
                    LEFT JOIN npc_memory_profiles p
                      ON p.save_id = n.save_id AND p.settler_id = n.settler_id
                    WHERE n.save_id = ?
                """, (save_id,))
                npcs = []
                for row in cursor:
                    # Fetch current pressures if exist
                    p_cursor = conn.execute(
                        "SELECT hunger_pressure, injury_pressure, exhaustion_pressure, mood_pressure FROM settler_pressures WHERE settler_id = ? AND save_id = ?",
                        (row["settler_id"], save_id)
                    )
                    p_row = p_cursor.fetchone()
                    pressures = dict(p_row) if p_row else {"hunger_pressure": 0.0, "injury_pressure": 0.0, "exhaustion_pressure": 0.0, "mood_pressure": 0.0}

                    npcs.append({
                        "settler_id": row["settler_id"],
                        "npc_id": row["npc_id"],
                        "name": row["name"],
                        "role": row["role"],
                        "traits": row["traits"],
                        "stats": row["stats"],
                        "description": row["description"],
                        "evolving_summary": row["evolving_summary"],
                        "memories_count": row["memories_count"] or 0,
                        "secrets_count": row["secrets_count"] or 0,
                        "pressures": pressures
                    })
                self._send_json(200, {"npcs": npcs})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/memory/npc":
            settler_id = query.get("settler_id", [""])[0]
            save_id = query.get("save_id", [""])[0]
            conn = get_db_connection()
            try:
                npc_row = conn.execute("SELECT * FROM npcs WHERE settler_id = ? AND save_id = ?", (settler_id, save_id)).fetchone()
                if not npc_row:
                    self._send_json(404, {"error": "NPC not found"})
                    return

                pressures_row = conn.execute("SELECT * FROM settler_pressures WHERE settler_id = ? AND save_id = ?", (settler_id, save_id)).fetchone()
                memories_cursor = conn.execute("SELECT * FROM memories WHERE npc_id = ? AND save_id = ? AND level = 0 ORDER BY timestamp DESC LIMIT 20", (settler_id, save_id))
                perm_cursor = conn.execute("SELECT * FROM permanent_memories WHERE npc_id = ? AND save_id = ? ORDER BY timestamp DESC", (settler_id, save_id))
                incidents_cursor = conn.execute("SELECT * FROM incidents WHERE settler_id = ? AND save_id = ? ORDER BY timestamp DESC LIMIT 15", (settler_id, save_id))
                profile_row = conn.execute("SELECT * FROM npc_memory_profiles WHERE settler_id = ? AND save_id = ?", (settler_id, save_id)).fetchone()
                typed_cursor = conn.execute("""
                    SELECT * FROM typed_memories
                    WHERE settler_id = ? AND save_id = ?
                    ORDER BY created_at DESC
                    LIMIT 80
                """, (settler_id, save_id))

                self._send_json(200, {
                    "npc": dict(npc_row),
                    "profile": dict(profile_row) if profile_row else None,
                    "memory_categories": get_memory_category_counts(conn, save_id, settler_id),
                    "typed_memories": [dict(r) for r in typed_cursor],
                    "pressures": dict(pressures_row) if pressures_row else None,
                    "memories": [dict(r) for r in memories_cursor],
                    "permanent_memories": [dict(r) for r in perm_cursor],
                    "incidents": [dict(r) for r in incidents_cursor]
                })
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/relationships":
            save_id = query.get("save_id", [""])[0]
            npc_id = query.get("npc_id", [""])[0]
            conn = get_db_connection()
            try:
                if npc_id:
                    cursor = conn.execute(
                        "SELECT * FROM relationships WHERE save_id = ? AND (subject = ? OR object = ?)",
                        (save_id, npc_id, npc_id)
                    )
                else:
                    cursor = conn.execute("SELECT * FROM relationships WHERE save_id = ?", (save_id,))
                self._send_json(200, {"relationships": [dict(r) for r in cursor]})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/incidents":
            save_id = query.get("save_id", [""])[0]
            conn = get_db_connection()
            try:
                cursor = conn.execute("SELECT * FROM incidents WHERE save_id = ? ORDER BY timestamp DESC LIMIT 100", (save_id,))
                self._send_json(200, {"incidents": [dict(r) for r in cursor]})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/character-sheet":
            save_id = query.get("save_id", [""])[0]
            settler_id = query.get("settler_id", [""])[0]
            conn = get_db_connection()
            try:
                sheet_row = conn.execute(
                    "SELECT * FROM character_sheets WHERE save_id = ? AND settler_id = ?",
                    (save_id, settler_id)
                ).fetchone()
                if not sheet_row:
                    self._send_json(404, {"error": "character sheet not found"})
                    return
                skills = [dict(r) for r in conn.execute(
                    "SELECT * FROM character_sheet_skills WHERE save_id = ? AND settler_id = ? ORDER BY level DESC, skill_name",
                    (save_id, settler_id)
                )]
                priorities = [dict(r) for r in conn.execute(
                    "SELECT * FROM character_sheet_work_priorities WHERE save_id = ? AND settler_id = ? ORDER BY priority ASC, job_name",
                    (save_id, settler_id)
                )]
                needs = [dict(r) for r in conn.execute(
                    "SELECT * FROM character_sheet_needs WHERE save_id = ? AND settler_id = ? ORDER BY need_name",
                    (save_id, settler_id)
                )]
                equipment = [dict(r) for r in conn.execute(
                    "SELECT * FROM character_sheet_equipment WHERE save_id = ? AND settler_id = ? ORDER BY slot, item",
                    (save_id, settler_id)
                )]
                traits = [dict(r) for r in conn.execute(
                    "SELECT * FROM character_sheet_traits WHERE save_id = ? AND settler_id = ? ORDER BY kind, value",
                    (save_id, settler_id)
                )]
                mood_modifiers = [dict(r) for r in conn.execute(
                    "SELECT * FROM character_sheet_mood_modifiers WHERE save_id = ? AND settler_id = ? ORDER BY kind, label",
                    (save_id, settler_id)
                )]
                schedule = [dict(r) for r in conn.execute(
                    "SELECT * FROM character_sheet_schedule WHERE save_id = ? AND settler_id = ? ORDER BY hour",
                    (save_id, settler_id)
                )]
                manage_settings = [dict(r) for r in conn.execute(
                    "SELECT * FROM character_sheet_manage_settings WHERE save_id = ? AND settler_id = ? ORDER BY setting_name",
                    (save_id, settler_id)
                )]
                self._send_json(200, {
                    "sheet": dict(sheet_row),
                    "skills": skills,
                    "work_priorities": priorities,
                    "needs": needs,
                    "equipment": equipment,
                    "traits": traits,
                    "mood_modifiers": mood_modifiers,
                    "schedule": schedule,
                    "manage_settings": manage_settings,
                    "raw": json.loads(sheet_row["raw_json"]) if sheet_row["raw_json"] else {},
                })
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/memory/relationship":
            save_id = query.get("save_id", [""])[0]
            subject = query.get("subject", [""])[0]
            obj = query.get("object", [""])[0]
            conn = get_db_connection()
            try:
                cursor = conn.execute(
                    "SELECT * FROM relationships WHERE save_id = ? AND ((subject = ? AND object = ?) OR (subject = ? AND object = ?))",
                    (save_id, subject, obj, obj, subject)
                )
                row = cursor.fetchone()
                if row:
                    self._send_json(200, {"relationship": dict(row)})
                else:
                    self._send_json(404, {"error": "not found"})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/dialogue/state":
            save_id = query.get("save_id", [""])[0]
            settler_id = query.get("settler_id", [""])[0]
            if not save_id or not settler_id:
                self._send_json(400, {"error": "save_id and settler_id are required"})
                return
            conn = get_db_connection()
            try:
                with conn:
                    now = datetime.utcnow().timestamp()
                    upsert_memory_profile(conn, save_id, settler_id)
                    trust = get_dialogue_trust(conn, save_id, settler_id)
                    disclosure = dialogue_disclosure_level(trust)
                    conn.execute("""
                        INSERT INTO dialogue_states
                        (save_id, settler_id, trust, disclosure_level, updated_at)
                        VALUES (?, ?, ?, ?, ?)
                        ON CONFLICT(save_id, settler_id) DO UPDATE SET
                            disclosure_level = excluded.disclosure_level,
                            updated_at = excluded.updated_at
                    """, (save_id, settler_id, trust, disclosure, now))
                    state = build_dialogue_prompt_context(conn, save_id, settler_id)
                self._send_json(200, {"ok": True, "state": state})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/memory/context":
            npc_id = query.get("npc_id", [""])[0]
            save_id = query.get("save_id", [""])[0]
            role = query.get("role", [""])[0]
            search_query = query.get("query", [""])[0]
            max_tokens = int(query.get("max_tokens", [1600])[0])
            
            conn = get_db_connection()
            try:
                profile_row = conn.execute(
                    "SELECT * FROM npc_memory_profiles WHERE settler_id = ? AND save_id = ?",
                    (npc_id, save_id)
                ).fetchone()

                # 1. Permanent memories
                perm_cursor = conn.execute(
                    "SELECT content AS text, category, created_at FROM typed_memories WHERE settler_id = ? AND save_id = ? and tier = 'permanent' ORDER BY created_at ASC",
                    (npc_id, save_id)
                )
                permanent = [dict(r) for r in perm_cursor]
                
                # 2. Recent L0 memories
                recent_cursor = conn.execute(
                    "SELECT content AS text, category, created_at FROM typed_memories WHERE settler_id = ? AND save_id = ? and tier = 'recent' ORDER BY created_at ASC LIMIT 15",
                    (npc_id, save_id)
                )
                recent = [dict(r) for r in recent_cursor]
                
                # 3. Summaries (L1/L2)
                sum_cursor = conn.execute(
                    "SELECT content AS text, category, created_at FROM typed_memories WHERE settler_id = ? AND save_id = ? and tier = 'major' ORDER BY created_at ASC",
                    (npc_id, save_id)
                )
                summaries = [dict(r) for r in sum_cursor]

                if not permanent:
                    legacy_perm = conn.execute(
                        "SELECT text, category, created_at FROM facts WHERE npc_key = ? AND (save_id = ? OR save_id = '') and tier = 'permanent' ORDER BY created_at ASC",
                        (npc_id, save_id)
                    )
                    permanent = [dict(r) for r in legacy_perm]
                if not recent:
                    legacy_recent = conn.execute(
                        "SELECT text, category, created_at FROM facts WHERE npc_key = ? AND (save_id = ? OR save_id = '') and tier = '0' ORDER BY created_at ASC LIMIT 15",
                        (npc_id, save_id)
                    )
                    recent = [dict(r) for r in legacy_recent]
                if not summaries:
                    legacy_summaries = conn.execute(
                        "SELECT text, category, created_at FROM facts WHERE npc_key = ? AND (save_id = ? OR save_id = '') and tier = '1' ORDER BY created_at ASC",
                        (npc_id, save_id)
                    )
                    summaries = [dict(r) for r in legacy_summaries]
                
                # Assemble base context
                sb = []
                max_chars = max_tokens * 4
                
                if profile_row:
                    sb.append("=== PERSONAL FILE ===")
                    if profile_row["display_name"]:
                        sb.append(f"Name: {profile_row['display_name']}")
                    if profile_row["role"]:
                        sb.append(f"Role: {profile_row['role']}")
                    if profile_row["traits"]:
                        sb.append(f"Traits: {profile_row['traits']}")
                    if profile_row["description"]:
                        sb.append(f"Description: {profile_row['description']}")
                    if profile_row["evolving_summary"]:
                        sb.append(f"Evolving summary: {profile_row['evolving_summary']}")
                    sb.append("")

                sheet_row = conn.execute(
                    "SELECT * FROM character_sheets WHERE settler_id = ? AND save_id = ?",
                    (npc_id, save_id)
                ).fetchone()
                if sheet_row:
                    sb.append("=== CHARACTER SHEET ===")
                    sb.append(f"Role: {sheet_row['role'] or 'unknown'}")
                    sb.append(f"Background: {sheet_row['background'] or 'unknown'}")
                    sb.append(f"Mood: {sheet_row['mood'] or 'unknown'} ({sheet_row['mood_score']:.0f})")
                    sb.append(f"Health: {sheet_row['health_current']:.0f}/{sheet_row['health_max']:.0f}")
                    sb.append(f"Activity: {sheet_row['activity_description'] or sheet_row['activity_type'] or 'unknown'}")
                    skill_rows = conn.execute(
                        "SELECT skill_name, level FROM character_sheet_skills WHERE settler_id = ? AND save_id = ? ORDER BY level DESC, skill_name LIMIT 8",
                        (npc_id, save_id)
                    ).fetchall()
                    if skill_rows:
                        sb.append("Top skills: " + ", ".join(f"{r['skill_name']}:{r['level']}" for r in skill_rows))
                    need_rows = conn.execute(
                        "SELECT need_name, value FROM character_sheet_needs WHERE settler_id = ? AND save_id = ? ORDER BY value ASC",
                        (npc_id, save_id)
                    ).fetchall()
                    if need_rows:
                        sb.append("Needs: " + ", ".join(f"{r['need_name']}:{r['value']:.0f}" for r in need_rows))
                    priority_rows = conn.execute(
                        "SELECT job_name, priority FROM character_sheet_work_priorities WHERE settler_id = ? AND save_id = ? ORDER BY priority ASC, job_name LIMIT 10",
                        (npc_id, save_id)
                    ).fetchall()
                    if priority_rows:
                        sb.append("Job priorities: " + ", ".join(f"{r['job_name']}:{r['priority']}" for r in priority_rows))
                    equipment_rows = conn.execute(
                        "SELECT slot, item FROM character_sheet_equipment WHERE settler_id = ? AND save_id = ? ORDER BY slot, item LIMIT 12",
                        (npc_id, save_id)
                    ).fetchall()
                    if equipment_rows:
                        sb.append("Equipment/inventory: " + ", ".join(f"{r['slot']}={r['item']}" for r in equipment_rows))
                    mood_rows = conn.execute(
                        "SELECT kind, label FROM character_sheet_mood_modifiers WHERE settler_id = ? AND save_id = ? ORDER BY kind, label LIMIT 12",
                        (npc_id, save_id)
                    ).fetchall()
                    if mood_rows:
                        sb.append("Mood/social/religion notes: " + " | ".join(f"{r['kind']}: {r['label']}" for r in mood_rows))
                    sb.append("")

                if permanent:
                    sb.append("=== LIFE EVENTS (always remembered) ===")
                    for m in permanent:
                        dt = datetime.utcfromtimestamp(m["created_at"]).strftime("%Y-%m-%d")
                        sb.append(f"[{dt}] {m['category']}: {m['text']}")
                    sb.append("")
                    
                if recent:
                    sb.append("=== RECENT ===")
                    for m in recent:
                        dt = datetime.utcfromtimestamp(m["created_at"]).strftime("%m-%d %H:%M")
                        sb.append(f"[{dt}] {m['category']}: {m['text']}")
                    sb.append("")
                    
                if summaries:
                    sb.append("=== SUMMARY HISTORY ===")
                    for m in summaries:
                        sb.append(m['text'])
                    sb.append("")
                
                # 4. RAG search
                if search_query:
                    words = set(search_query.lower().split())
                    rag_cursor = conn.execute(
                        "SELECT content AS text, category, created_at, id FROM typed_memories WHERE settler_id = ? AND save_id = ? ORDER BY created_at DESC",
                        (npc_id, save_id)
                    )
                    scored_facts = []
                    for r in rag_cursor:
                        fact_text = r["text"].lower()
                        score = sum(1 for w in words if w in fact_text)
                        if score > 0:
                            scored_facts.append((score, r))
                    scored_facts.sort(key=lambda x: x[0], reverse=True)
                    
                    rag_results = [item[1] for item in scored_facts[:3]]
                    if rag_results:
                        sb.append("=== RELEVANT MEMORIES (RAG) ===")
                        for m in rag_results:
                            dt = datetime.utcfromtimestamp(m["created_at"]).strftime("%Y-%m-%d")
                            sb.append(f"[{dt}] {m['category']}: {m['text']}")
                            conn.execute("UPDATE typed_memories SET last_used_at = ? WHERE id = ?", (datetime.utcnow().timestamp(), m["id"]))
                        sb.append("")

                # 5. RoleRAG (Encyclopedia context)
                if role:
                    role_lower = role.lower()
                    # Map roles to lore keys
                    role_map = {
                        "farmer": ["farming", "cooking"],
                        "woodcutter": ["winter", "farming"],
                        "guard": ["combat", "health"],
                        "scholar": ["health", "building"],
                        "builder": ["building", "mining"],
                        "miner": ["mining", "winter"]
                    }
                    keys = role_map.get(role_lower, [role_lower])
                    placeholders = ",".join("?" for _ in keys)
                    lore_cursor = conn.execute(
                        f"SELECT title, text FROM lore WHERE kind = 'encyclopedia' AND key IN ({placeholders})",
                        keys
                    )
                    lore_items = [dict(r) for r in lore_cursor]
                    if lore_items:
                        sb.append("=== ENCYCLOPEDIA CONTEXT (RoleRAG) ===")
                        for l in lore_items:
                            sb.append(f"[{l['title']}]\n{l['text']}")
                        sb.append("")
                
                context_str = "\n".join(sb)
                if len(context_str) > max_chars:
                    context_str = context_str[:max_chars]
                    
                self._send_json(200, {"context": context_str})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        if gm_systems.dispatch(self, GM_CTX, "GET", path, query=query, payload=None):
            return
        if gm_devops.dispatch(self, GM_CTX, "GET", path, query=query, payload=None):
            return
        self._send_json(404, {"error": "not found"})

    def do_POST(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path

        length = int(self.headers.get('Content-Length', 0))
        body = self.rfile.read(length).decode('utf-8') if length > 0 else "{}"
        try:
            payload = json.loads(body)
        except Exception as e:
            self._send_json(400, {"error": "Invalid JSON: " + str(e)})
            return

        if path == "/api/game/input":
            import pygetwindow as gw
            import win32gui
            import win32con
            import pyautogui
            import pydirectinput
            import time
            
            action = payload.get("action")
            
            game_window = find_game_window(gw)
            
            if not game_window:
                self._send_json(404, {"error": "not found", "message": "Going Medieval window not found"})
                return
            
            hwnd = game_window._hWnd
            
            try:
                if win32gui.IsIconic(hwnd):
                    win32gui.ShowWindow(hwnd, win32con.SW_RESTORE)
                win32gui.ShowWindow(hwnd, win32con.SW_SHOW)
                pyautogui.press('alt')
                win32gui.SetForegroundWindow(hwnd)
                time.sleep(0.1)
            except Exception as e:
                print("Failed to focus window for input:", e)
            
            rect = win32gui.GetWindowRect(hwnd)
            left, top, right, bottom = rect
            width = right - left
            height = bottom - top
            
            if action == "click":
                x_rel = payload.get("x", 0.5)
                y_rel = payload.get("y", 0.5)
                
                click_x = int(left + x_rel * width)
                click_y = int(top + y_rel * height)
                
                pydirectinput.moveTo(click_x, click_y)
                time.sleep(0.1)
                pydirectinput.click()
                self._send_json(200, {"ok": True, "message": f"Clicked at relative {x_rel}, {y_rel} (absolute {click_x}, {click_y})"})
                return
                
            elif action == "text":
                text = payload.get("text", "")
                if text:
                    pyautogui.write(text, interval=0.05)
                self._send_json(200, {"ok": True, "message": f"Typed text: {text}"})
                return
                
            elif action == "keypress":
                key = payload.get("key", "")
                if key:
                    key_map = {
                        "enter": "enter",
                        "escape": "escape",
                        "space": "space",
                        "backspace": "backspace",
                        "up": "up",
                        "down": "down",
                        "left": "left",
                        "right": "right"
                    }
                    mapped_key = key_map.get(key.lower(), key.lower())
                    pydirectinput.press(mapped_key)
                self._send_json(200, {"ok": True, "message": f"Pressed key: {key}"})
                return
            
            self._send_json(400, {"error": "bad_request", "message": f"Unknown input action: {action}"})
            return

        elif path == "/api/universe/seed":
            # Seed the database with demo save game data
            save_id = payload.get("save_id", "demo_save")
            conn = get_db_connection()
            try:
                with conn:
                    # 1. Clear old demo data
                    conn.execute("DELETE FROM npcs WHERE save_id = ?", (save_id,))
                    conn.execute("DELETE FROM settler_pressures WHERE save_id = ?", (save_id,))
                    conn.execute("DELETE FROM memories WHERE save_id = ?", (save_id,))
                    conn.execute("DELETE FROM permanent_memories WHERE save_id = ?", (save_id,))
                    conn.execute("DELETE FROM relationships WHERE save_id = ?", (save_id,))
                    conn.execute("DELETE FROM incidents WHERE save_id = ?", (save_id,))

                    # 2. Insert NPCs
                    settlers = [
                        ("settler_1", "Arthur Pendelton", "Farmer", "hardworking, friendly, loves nature", "Agronomy:25, Cooking:10"),
                        ("settler_2", "Gwendolyn Stone", "Guard", "brave, serious, cautious", "Melee:30, Marksman:20"),
                        ("settler_3", "Brother Luke", "Scholar", "wise, introverted, religious", "Research:35, Medicine:15")
                    ]
                    for s_id, name, prof, traits, stats in settlers:
                        conn.execute(
                            "INSERT INTO npcs (npc_key, settler_id, save_id, npc_id, name, role, traits, stats, created_at, last_active) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                            (s_id, s_id, save_id, s_id + "_p2", name, prof, traits, stats, datetime.utcnow().timestamp(), datetime.utcnow().timestamp())
                        )

                    # 3. Insert Pressures
                    conn.execute(
                        "INSERT INTO settler_pressures (settler_id, save_id, timestamp, hunger_pressure, injury_pressure, exhaustion_pressure, mood_pressure) VALUES (?, ?, ?, ?, ?, ?, ?)",
                        ("settler_1", save_id, datetime.utcnow(), 0.1, 0.0, 0.4, 0.2)
                    )
                    conn.execute(
                        "INSERT INTO settler_pressures (settler_id, save_id, timestamp, hunger_pressure, injury_pressure, exhaustion_pressure, mood_pressure) VALUES (?, ?, ?, ?, ?, ?, ?)",
                        ("settler_2", save_id, datetime.utcnow(), 0.7, 0.45, 0.8, 0.6)
                    )
                    conn.execute(
                        "INSERT INTO settler_pressures (settler_id, save_id, timestamp, hunger_pressure, injury_pressure, exhaustion_pressure, mood_pressure) VALUES (?, ?, ?, ?, ?, ?, ?)",
                        ("settler_3", save_id, datetime.utcnow(), 0.2, 0.0, 0.1, 0.1)
                    )

                    # 4. Insert Relationships
                    conn.execute("INSERT INTO relationships (subject, object, save_id, friendship, standing, trust, fear, resentment, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                                 ("settler_1", "settler_2", save_id, 0.25, "friendly", 0.7, 0.0, 0.0, datetime.utcnow().timestamp()))
                    conn.execute("INSERT INTO relationships (subject, object, save_id, friendship, standing, trust, fear, resentment, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                                 ("settler_2", "settler_1", save_id, 0.20, "friendly", 0.6, 0.0, 0.0, datetime.utcnow().timestamp()))
                    conn.execute("INSERT INTO relationships (subject, object, save_id, friendship, standing, trust, fear, resentment, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                                 ("settler_1", "settler_3", save_id, 0.10, "neutral", 0.5, 0.0, 0.0, datetime.utcnow().timestamp()))
                    conn.execute("INSERT INTO relationships (subject, object, save_id, friendship, rivalry, standing, trust, fear, resentment, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                                 ("settler_2", "settler_3", save_id, -0.15, 0.15, "tense", 0.3, 0.1, 0.2, datetime.utcnow().timestamp()))

                    # 5. Insert memories
                    conn.execute(
                        "INSERT INTO memories (npc_id, level, timestamp, session_seq, event_type, content, importance, save_id) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                        ("settler_1", 0, datetime.utcnow(), 1, "dialogue_player", "Talked about the crop harvest with Moshi.", 5, save_id)
                    )
                    conn.execute(
                        "INSERT INTO memories (npc_id, level, timestamp, session_seq, event_type, content, importance, save_id) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                        ("settler_2", 0, datetime.utcnow(), 1, "danger", "Spotted wolf footprints near the south gate.", 8, save_id)
                    )
                    conn.execute(
                        "INSERT INTO permanent_memories (npc_id, timestamp, event_type, content, importance, save_id) VALUES (?, ?, ?, ?, ?, ?)",
                        ("settler_2", datetime.utcnow(), "injury", "Lost left index finger defending the gate from bandits.", 10, save_id)
                    )

                    # 6. Insert incidents
                    conn.execute(
                        "INSERT INTO incidents (settler_id, save_id, timestamp, action, reasoning, success, faction_id, action_type, target, confidence, priority, status, created_at) VALUES (?, ?, ?, ?, ?, ?, 'player', ?, ?, 1.0, 1, 'success', ?)",
                        ("settler_1", save_id, datetime.utcnow().timestamp(), "continue_job", "I will continue watering the barley fields, it is my duty.", 1, "continue_job", "I will continue watering the barley fields, it is my duty.", datetime.utcnow().timestamp())
                    )
                    conn.execute(
                        "INSERT INTO incidents (settler_id, save_id, timestamp, action, reasoning, success, faction_id, action_type, target, confidence, priority, status, created_at) VALUES (?, ?, ?, ?, ?, ?, 'player', ?, ?, 1.0, 1, 'success', ?)",
                        ("settler_2", save_id, datetime.utcnow().timestamp(), "eat", "Hunger and exhaustion are taking a toll. I must find some bread.", 1, "eat", "Hunger and exhaustion are taking a toll. I must find some bread.", datetime.utcnow().timestamp())
                    )

                self._send_json(200, {"ok": True, "message": "Demo database seeded successfully"})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/simulate/decision":
            # Simulate a full decision run
            settler_id = payload.get("settler_id", "settler_1")
            save_id = payload.get("save_id", "simulated_save")
            name = payload.get("name", "Arthur Pendelton")
            background = payload.get("background", "Farmer")
            traits = payload.get("traits", "hardworking, friendly")
            
            # Pressures
            food = float(payload.get("food", 50.0))
            rest = float(payload.get("rest", 50.0))
            health_curr = float(payload.get("health_current", 100.0))
            health_max = float(payload.get("health_max", 100.0))
            mood_score = float(payload.get("mood_score", 50.0))
            is_injured = bool(payload.get("is_injured", False))
            nearby_threats = int(payload.get("nearby_threats", 0))
            weather = payload.get("weather", "clear")
            recreation = float(payload.get("recreation", 50.0))
            sociability = float(payload.get("sociability", 0.5))
            aggression = float(payload.get("aggression", 0.5))

            # STAGE 1: Pressures & Needs Scoring
            hunger = (100.0 - food) / 100.0
            exhaustion = (100.0 - rest) / 100.0
            
            injury = 0.0
            if health_max > 0:
                injury = (health_max - health_curr) / health_max
            if is_injured:
                injury = max(injury, 0.4)

            mood = (100.0 - mood_score) / 100.0
            threat = 1.0 if nearby_threats > 0 else 0.0

            options = []
            
            # 1. flee
            flee_score = threat * 1.5
            options.append({"name": "flee", "score": flee_score, "description": "Flee immediately from dangerous threats."})

            # 2. defend
            defend_score = 0.0
            if threat > 0:
                defend_score = 0.4 + aggression * 0.8
            options.append({"name": "defend", "score": defend_score, "description": "Equip weapons and prepare for combat to defend the colony."})

            # 3. eat
            eat_score = hunger * 1.2
            if food < 15 or hunger > 0.85:
                eat_score = 2.0
            options.append({"name": "eat", "score": eat_score, "description": "Find and eat food to satisfy hunger."})

            # 4. rest
            rest_score = exhaustion * 1.1
            if rest < 15 or exhaustion > 0.85:
                rest_score = 2.0
            options.append({"name": "rest", "score": rest_score, "description": "Sleep in a bed or resting spot to recover energy."})

            # 5. seek_shelter
            seek_shelter_score = 0.6 if weather != "clear" else 0.0
            options.append({"name": "seek_shelter", "score": seek_shelter_score, "description": "Seek shelter indoors away from bad weather."})

            # Defaults
            options.append({"name": "continue_job", "score": 0.4, "description": "Continue your current job or task."})
            options.append({"name": "switch_job", "score": 0.35, "description": "Switch to a different job."})
            
            socialize_score = ((100.0 - recreation) / 100.0) * sociability * 0.7
            options.append({"name": "socialize", "score": socialize_score, "description": "Talk to and socialize with another settler."})
            
            options.append({"name": "explore", "score": 0.1, "description": "Explore the map."})
            options.append({"name": "gather", "score": 0.3, "description": "Gather raw resources or materials."})
            options.append({"name": "build_special", "score": mood * 0.4, "description": "Construct a special mood-boosting building."})
            options.append({"name": "draft", "score": 0.0, "description": "Draft for combat direction."})
            options.append({"name": "repair", "score": 0.25, "description": "Repair colony buildings."})
            options.append({"name": "haul", "score": 0.2, "description": "Haul battlefield items or resources."})
            options.append({"name": "capture", "score": 0.0, "description": "Capture downed enemies."})
            options.append({"name": "rebrand", "score": 0.15, "description": "Upgrade wooden walls to stone versions."})

            # Sort options
            options.sort(key=lambda x: x["score"], reverse=True)
            top_math_choice = options[0]

            # Save simulated pressures
            conn = get_db_connection()
            try:
                with conn:
                    # Register/update NPC first
                    conn.execute(
                        "INSERT OR REPLACE INTO npcs (npc_key, settler_id, save_id, npc_id, name, role, traits, stats, created_at, last_active) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                        (settler_id, settler_id, save_id, settler_id + "_p2", settler_id, role, traits, f"Aggression:{aggression}, Sociability:{sociability}", datetime.utcnow().timestamp(), datetime.utcnow().timestamp())
                    )
                    # Save pressures
                    conn.execute(
                        "INSERT OR REPLACE INTO settler_pressures (settler_id, save_id, timestamp, hunger_pressure, injury_pressure, exhaustion_pressure, mood_pressure) VALUES (?, ?, ?, ?, ?, ?, ?)",
                        (settler_id, save_id, datetime.utcnow(), hunger, injury, exhaustion, mood)
                    )
            except Exception as e:
                print("Failed to save simulation pressures:", e)
            finally:
                conn.close()

            # STAGE 2: Bounded LLM Selection
            chosen_action = top_math_choice["name"]
            chosen_reasoning = f"Deterministic fallback (offline): {top_math_choice['description']}"
            dialogue_complaint = f"💭 I must focus on {chosen_action} right now..."
            was_llm = False
            p2_error = None
            p2_health = check_player2_health()

            if p2_health["online"]:
                p2_port = p2_health["port"]
                try:
                    # Let's get or spawn NPC first
                    spawn_url = f"http://127.0.0.1:{p2_port}/v1/npc/games/going_medieval/npcs/spawn"
                    system_prompt = (
                        f"You are {name}, a settler in a medieval colony simulation game. "
                        f"Personality Traits: {traits}. "
                        "You think, reason, and react like a medieval settler. Your goal is to survive and work. "
                        "Answer in the first person with short, in-character thoughts (1-2 sentences) and choose the command matching your intent."
                    )
                    
                    commands = [
                        {"name": "continue_job", "description": "Continue doing your current job or task.", "parameters": {"type": "object", "properties": {}}},
                        {"name": "switch_job", "description": "Switch to a different job or task.", "parameters": {"type": "object", "properties": {"job": {"type": "string"}}}},
                        {"name": "rest", "description": "Take a break, rest, or sleep in a bed to recover energy.", "parameters": {"type": "object", "properties": {}}},
                        {"name": "eat", "description": "Find and eat food to satisfy hunger.", "parameters": {"type": "object", "properties": {}}},
                        {"name": "flee", "description": "Flee and run away from dangerous threats or enemies immediately.", "parameters": {"type": "object", "properties": {}}},
                        {"name": "defend", "description": "Equip weapons and prepare for combat or defend the colony.", "parameters": {"type": "object", "properties": {}}},
                        {"name": "seek_shelter", "description": "Go indoors or seek shelter from bad weather or dangerous environment.", "parameters": {"type": "object", "properties": {}}},
                        {"name": "socialize", "description": "Talk to and socialize with another settler.", "parameters": {"type": "object", "properties": {"target": {"type": "string"}}}},
                    ]

                    spawn_payload = {
                        "name": name,
                        "short_name": name.split()[0],
                        "character_description": f"A settler in a medieval colony.",
                        "system_prompt": system_prompt,
                        "voice_id": "01955d76-ed5b-74de-83e5-800a44fee0d1",
                        "keep_game_state": False,
                        "commands": commands
                    }
                    
                    # Call spawn
                    req_spawn = urllib.request.Request(
                        spawn_url, 
                        data=json.dumps(spawn_payload).encode("utf-8"), 
                        headers={"Content-Type": "application/json", "X-Game-Client-Id": "going_medieval"}, 
                        method="POST"
                    )
                    npc_id = None
                    with urllib.request.urlopen(req_spawn, timeout=3.0) as spawn_resp:
                        if spawn_resp.status == 200:
                            npc_id = spawn_resp.read().decode("utf-8").strip('"')

                    if npc_id:
                        # Build scored priorities prompt
                        sb = []
                        sb.append("=== CURRENT BODY STATUS & ENVIRONMENT ===")
                        sb.append(f"- Hunger Level: {hunger:.0%}")
                        sb.append(f"- Tiredness: {exhaustion:.0%}")
                        sb.append(f"- Physical Pain/Injury: {injury:.0%}")
                        sb.append(f"- Mood Distress: {mood:.0%}")
                        if nearby_threats > 0:
                            sb.append(f"- WARNING: {nearby_threats} threat(s) nearby!")
                        sb.append("\n=== EVALUATED ACTION OPTIONS ===")
                        for i, opt in enumerate(options[:5]):
                            sb.append(f"{i+1}. {opt['name']} (Mathematical Score: {opt['score']:.2f}) - {opt['description']}")
                        sb.append("\nDecide which action to take. Select the appropriate command corresponding to your choice, and express your medieval thoughts.")
                        
                        # Call chat
                        chat_url = f"http://127.0.0.1:{p2_port}/v1/npc/games/going_medieval/npcs/{npc_id}/chat"
                        chat_payload = {
                            "sender_name": "System",
                            "sender_message": "\n".join(sb)
                        }
                        
                        req_chat = urllib.request.Request(
                            chat_url,
                            data=json.dumps(chat_payload).encode("utf-8"),
                            headers={"Content-Type": "application/json", "X-Game-Client-Id": "going_medieval"},
                            method="POST"
                        )
                        
                        # Read response from streaming responses
                        # To keep it simple in this standard-library Python simulator without full multi-threading stream read,
                        # we can read the NDJSON stream /responses or directly call chat completion endpoint if `/chat` returns it.
                        # Wait! In Player2 client chat, the `/chat` endpoint accepts the request.
                        # How does X4 or LLMClient read it? It sends the POST and reads the GET stream.
                        # Since we want a robust, quick, non-blocking simulation, we can query Player2 chat completions endpoint `/v1/chat/completions` directly,
                        # which supports standard JSON response rather than reading NDJSON.
                        # Let's mock a chat completions endpoint call!
                        # Let's try to query `/v1/chat/completions` on Player2 daemon. It is fully standard OpenAI format!
                        completions_url = f"http://127.0.0.1:{p2_port}/v1/chat/completions"
                        
                        prompt_messages = [
                            {"role": "system", "content": system_prompt},
                            {"role": "user", "content": "\n".join(sb) + "\nChoose a command from: " + ", ".join([o["name"] for o in options[:5]]) + ". Respond in JSON format: {\"command\": \"command_name\", \"thought\": \"your medieval thought\"}"}
                        ]
                        
                        compl_payload = {
                            "messages": prompt_messages,
                            "stream": False,
                            "temperature": 0.7,
                            "max_tokens": 150
                        }
                        
                        req_compl = urllib.request.Request(
                            completions_url,
                            data=json.dumps(compl_payload).encode("utf-8"),
                            headers={"Content-Type": "application/json"},
                            method="POST"
                        )
                        
                        with urllib.request.urlopen(req_compl, timeout=5.0) as compl_resp:
                            if compl_resp.status == 200:
                                compl_data = json.loads(compl_resp.read().decode("utf-8"))
                                text_content = compl_data["choices"][0]["message"]["content"].strip()
                                
                                # Try parsing JSON response from model
                                try:
                                    # Clean markdown code blocks if any
                                    if "```" in text_content:
                                        text_content = text_content.split("```")[1]
                                        if text_content.startswith("json"):
                                            text_content = text_content[4:]
                                    
                                    parsed_resp = json.loads(text_content.strip())
                                    model_command = parsed_resp.get("command", "").lower().strip()
                                    model_thought = parsed_resp.get("thought", "")
                                    
                                    # Validate action is whitelisted
                                    whitelisted = {"continue_job", "switch_job", "rest", "eat", "socialize", "flee", "defend", "seek_shelter"}
                                    if model_command in whitelisted:
                                        chosen_action = model_command
                                        chosen_reasoning = model_thought
                                        dialogue_complaint = f"💭 {model_thought}"
                                        was_llm = True
                                except Exception as parse_err:
                                    # Fallback simple text parsing
                                    print("Failed to parse JSON completion response:", parse_err, text_content)
                                    chosen_reasoning = text_content
                                    dialogue_complaint = text_content
                                    # Check if any whitelisted action word is in the text
                                    for act in ["flee", "defend", "eat", "rest", "seek_shelter", "continue_job", "switch_job", "socialize"]:
                                        if act in text_content.lower():
                                            chosen_action = act
                                            break
                except Exception as p2_err:
                    p2_error = str(p2_err)
                    print("Simulation Player2 API communication error:", p2_err)

            # STAGE 3: Validation
            validation_passed = True
            # Safety checks matching C# DecisionEngine.cs
            if food < 20 or rest < 20:
                if chosen_action not in ("eat", "rest", "seek_shelter"):
                    validation_passed = False
            if nearby_threats > 0:
                if chosen_action not in ("flee", "defend", "seek_shelter"):
                    validation_passed = False

            if not validation_passed:
                # Force fallback
                chosen_action = top_math_choice["name"]
                chosen_reasoning = f"Overridden by safety validation! Fallback: {top_math_choice['description']}"
                dialogue_complaint = f"💭 I feel overwhelmed. I must {chosen_action} right now..."
                was_llm = False

            # Log incident in SQLite database
            conn = get_db_connection()
            try:
                with conn:
                    conn.execute(
                        "INSERT INTO incidents (settler_id, save_id, timestamp, action, reasoning, success, faction_id, action_type, target, confidence, priority, status, created_at) VALUES (?, ?, ?, ?, ?, ?, 'player', ?, ?, 1.0, 1, ?, ?)",
                        (settler_id, save_id, datetime.utcnow().timestamp(), chosen_action, chosen_reasoning, 1 if validation_passed else 0, chosen_action, chosen_reasoning, "success" if validation_passed else "failed", datetime.utcnow().timestamp())
                    )
                    # Also log the decision as a memory event in level 0
                    conn.execute(
                        "INSERT INTO memories (npc_id, level, timestamp, session_seq, event_type, content, importance, save_id) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                        (settler_id, 0, datetime.utcnow(), 99, "decision", f"Decided to {chosen_action}: {chosen_reasoning}", 5, save_id)
                    )
            except Exception as e:
                print("Failed to save simulation incident:", e)
            finally:
                conn.close()

            # Return full simulation details
            self._send_json(200, {
                "ok": True,
                "settler_id": settler_id,
                "save_id": save_id,
                "pressures": {
                    "hunger": hunger,
                    "exhaustion": exhaustion,
                    "injury": injury,
                    "mood": mood,
                    "threat": threat
                },
                "math_options": options,
                "top_math_choice": top_math_choice,
                "player2_online": p2_health["online"],
                "player2_error": p2_error,
                "was_llm": was_llm,
                "chosen_action": chosen_action,
                "reasoning": chosen_reasoning,
                "dialogue_complaint": dialogue_complaint,
                "validation_passed": validation_passed
            })
            return

        elif path == "/api/memory/npc":
            settler_id = payload.get("settler_id")
            save_id = payload.get("save_id")
            npc_id = payload.get("npc_id")
            name = payload.get("name")
            profession = payload.get("profession")
            traits = payload.get("traits")
            stats = payload.get("stats")

            conn = get_db_connection()
            try:
                with conn:
                    conn.execute("""
                        INSERT OR REPLACE INTO npcs 
                        (npc_key, settler_id, npc_id, save_id, name, role, traits, stats, created_at, last_active, is_alive) 
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1)""",
                        (settler_id, settler_id, npc_id, save_id, name, profession, traits, stats, datetime.utcnow().timestamp(), datetime.utcnow().timestamp())
                    )
                    upsert_memory_profile(conn, save_id, settler_id, name, profession, traits, stats)
                self._send_json(200, {"ok": True, "message": f"NPC {settler_id} saved successfully"})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/character-sheet":
            save_id = payload.get("save_id")
            settler_id = payload.get("settler_id")
            sheet = payload.get("sheet") or {}
            conn = get_db_connection()
            try:
                with conn:
                    upsert_character_sheet(conn, save_id, settler_id, sheet)
                self._send_json(200, {"ok": True, "message": "Character sheet saved successfully"})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/dialogue/barter/resolve":
            save_id = payload.get("save_id")
            settler_id = payload.get("settler_id")
            intent_id = payload.get("intent_id")
            resolution = str(payload.get("resolution") or "").strip().lower()
            if not save_id or not settler_id or not intent_id:
                self._send_json(400, {"error": "save_id, settler_id and intent_id are required"})
                return
            if resolution not in {"fulfilled", "broken", "declined"}:
                self._send_json(400, {"error": "resolution must be fulfilled, broken or declined"})
                return
            conn = get_db_connection()
            try:
                with conn:
                    now = datetime.utcnow().timestamp()
                    intent = conn.execute(
                        "SELECT * FROM dialogue_barter_intents WHERE id = ? AND save_id = ? AND settler_id = ?",
                        (intent_id, save_id, settler_id)
                    ).fetchone()
                    if not intent:
                        self._send_json(404, {"error": "barter intent not found"})
                        return
                    if intent["status"] != "proposed":
                        self._send_json(409, {"error": f"intent already {intent['status']}"})
                        return
                    conn.execute(
                        "UPDATE dialogue_barter_intents SET status = ? WHERE id = ?",
                        (resolution, intent_id)
                    )
                    rule_key = {"fulfilled": "promise_kept", "broken": "promise_broken", "declined": "barter_declined"}[resolution]
                    delta = TRUST_RULES[rule_key]
                    current_trust = get_dialogue_trust(conn, save_id, settler_id)
                    new_trust = clamp(current_trust + delta, 0.0, 1.0)
                    disclosure = dialogue_disclosure_level(new_trust)
                    conn.execute("""
                        INSERT INTO dialogue_states
                        (save_id, settler_id, trust, disclosure_level, updated_at)
                        VALUES (?, ?, ?, ?, ?)
                        ON CONFLICT(save_id, settler_id) DO UPDATE SET
                            trust = excluded.trust,
                            disclosure_level = excluded.disclosure_level,
                            updated_at = excluded.updated_at
                    """, (save_id, settler_id, new_trust, disclosure, now))
                    label = f"{intent['intent_type']} {intent['item']}".strip()
                    record_trust_event(
                        conn, save_id, settler_id, delta,
                        f"barter intent '{label}' {resolution}: {delta:+.2f}",
                        f"barter_{resolution}", new_trust
                    )
                    memory_category = "promises" if resolution == "fulfilled" else ("betrayals" if resolution == "broken" else "events")
                    memory_text = {
                        "fulfilled": f"Kept the bargain: {label}. Terms: {intent['terms'] or 'unspecified'}.",
                        "broken": f"Broke the bargain: {label}. Terms were: {intent['terms'] or 'unspecified'}.",
                        "declined": f"Declined the proposal: {label}.",
                    }[resolution]
                    insert_typed_memory(
                        conn, save_id, settler_id,
                        "promise" if resolution == "fulfilled" else ("betrayal" if resolution == "broken" else "event"),
                        memory_text,
                        7 if resolution != "declined" else 4,
                        metadata={"barter_intent_id": intent_id, "resolution": resolution, "category_hint": memory_category}
                    )
                    state = build_dialogue_prompt_context(conn, save_id, settler_id)
                self._send_json(200, {
                    "ok": True,
                    "resolution": resolution,
                    "trust": new_trust,
                    "trust_delta": delta,
                    "disclosure_level": disclosure,
                    "state": state,
                })
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/dialogue/exchange":
            save_id = payload.get("save_id")
            settler_id = payload.get("settler_id")
            player_text = (payload.get("player_text") or "").strip()
            npc_text = (payload.get("npc_text") or "").strip()
            claims = payload.get("claims") or []
            contradiction = payload.get("contradiction")
            barter_intent = payload.get("barter_intent")
            trust_delta = numeric_or_default(payload.get("trust_delta"), 0.0)
            voice_profile = (payload.get("voice_profile") or "").strip()
            backstory_voice = (payload.get("backstory_voice") or "").strip()
            if not save_id or not settler_id:
                self._send_json(400, {"error": "save_id and settler_id are required"})
                return

            conn = get_db_connection()
            try:
                with conn:
                    now = datetime.utcnow().timestamp()
                    upsert_memory_profile(conn, save_id, settler_id)
                    current_trust = get_dialogue_trust(conn, save_id, settler_id)
                    auto_contradiction = None if contradiction else detect_dialogue_contradiction(
                        conn, save_id, settler_id, player_text, npc_text, claims
                    )
                    if auto_contradiction:
                        contradiction = auto_contradiction
                    prior_contradictions_row = conn.execute(
                        "SELECT contradiction_count FROM dialogue_states WHERE save_id = ? AND settler_id = ?",
                        (save_id, settler_id)
                    ).fetchone()
                    prior_contradictions = prior_contradictions_row["contradiction_count"] if prior_contradictions_row else 0
                    trust_delta, trust_reasons = compute_trust_delta(
                        trust_delta,
                        contradiction=bool(contradiction),
                        prior_contradictions=prior_contradictions,
                        claims_recorded=len(claims) if isinstance(claims, list) else (1 if claims else 0),
                    )
                    new_trust = clamp(current_trust + trust_delta, 0.0, 1.0)
                    disclosure = dialogue_disclosure_level(new_trust)
                    conn.execute("""
                        INSERT INTO dialogue_states
                        (save_id, settler_id, trust, disclosure_level, voice_profile, backstory_voice, updated_at)
                        VALUES (?, ?, ?, ?, ?, ?, ?)
                        ON CONFLICT(save_id, settler_id) DO UPDATE SET
                            trust = excluded.trust,
                            disclosure_level = excluded.disclosure_level,
                            voice_profile = CASE WHEN excluded.voice_profile != '' THEN excluded.voice_profile ELSE dialogue_states.voice_profile END,
                            backstory_voice = CASE WHEN excluded.backstory_voice != '' THEN excluded.backstory_voice ELSE dialogue_states.backstory_voice END,
                            updated_at = excluded.updated_at
                    """, (save_id, settler_id, new_trust, disclosure, voice_profile, backstory_voice, now))

                    if trust_delta != 0 or trust_reasons:
                        record_trust_event(
                            conn, save_id, settler_id, trust_delta,
                            "; ".join(trust_reasons) if trust_reasons else "no rule fired",
                            "exchange", new_trust
                        )

                    if player_text:
                        insert_typed_memory(
                            conn, save_id, settler_id, "dialogue_player", player_text, 6,
                            metadata={"speaker": "player", "p2": True}
                        )
                    if npc_text:
                        insert_typed_memory(
                            conn, save_id, settler_id, "dialogue_npc", npc_text, 6,
                            metadata={"speaker": "npc", "p2": True}
                        )

                    claim_count = 0
                    for claim in claims if isinstance(claims, list) else [claims]:
                        if isinstance(claim, dict):
                            claim_text = (claim.get("text") or claim.get("claim") or "").strip()
                            speaker = claim.get("speaker") or "npc"
                            confidence = numeric_or_default(claim.get("confidence"), 1.0)
                        else:
                            claim_text = str(claim).strip()
                            speaker = "npc"
                            confidence = 1.0
                        if not claim_text:
                            continue
                        conn.execute("""
                            INSERT INTO dialogue_claims
                            (save_id, settler_id, speaker, claim_text, confidence, status, created_at)
                            VALUES (?, ?, ?, ?, ?, 'active', ?)
                        """, (save_id, settler_id, str(speaker), claim_text, confidence, now))
                        claim_count += 1

                    contradiction_count = 0
                    if contradiction:
                        if isinstance(contradiction, dict):
                            claim_text = (contradiction.get("claim") or contradiction.get("claim_text") or "").strip()
                            reason = (contradiction.get("reason") or contradiction.get("contradiction_reason") or "Contradicted in dialogue").strip()
                        else:
                            claim_text = str(contradiction).strip()
                            reason = "Contradicted in dialogue"
                        if claim_text:
                            claim_id = contradiction.get("claim_id") if isinstance(contradiction, dict) else None
                            if claim_id:
                                conn.execute("""
                                    UPDATE dialogue_claims
                                    SET status = 'contradicted',
                                        contradiction_reason = ?
                                    WHERE id = ? AND save_id = ? AND settler_id = ?
                                """, (reason, claim_id, save_id, settler_id))
                            else:
                                conn.execute("""
                                    INSERT INTO dialogue_claims
                                    (save_id, settler_id, speaker, claim_text, confidence, status, contradicted_by_claim_id, contradiction_reason, created_at)
                                    VALUES (?, ?, 'system', ?, 1.0, 'contradicted', ?, ?, ?)
                                """, (save_id, settler_id, claim_text, claim_id, reason, now))
                            conn.execute("""
                                UPDATE dialogue_states
                                SET contradiction_count = contradiction_count + 1
                                WHERE save_id = ? AND settler_id = ?
                            """, (save_id, settler_id))
                            insert_typed_memory(
                                conn, save_id, settler_id, "betrayal",
                                f"Contradiction noticed in dialogue: {claim_text}. {reason}", 8,
                                metadata={"claim": claim_text, "reason": reason}
                            )
                            contradiction_count = 1

                    barter_count = 0
                    if barter_intent:
                        if isinstance(barter_intent, dict):
                            intent_type = barter_intent.get("intent_type") or barter_intent.get("type") or "barter"
                            item = barter_intent.get("item") or barter_intent.get("ware") or ""
                            terms = barter_intent.get("terms") or barter_intent.get("offer") or ""
                        else:
                            intent_type = "barter"
                            item = ""
                            terms = str(barter_intent)
                        conn.execute("""
                            INSERT INTO dialogue_barter_intents
                            (save_id, settler_id, intent_type, item, terms, raw_json, created_at)
                            VALUES (?, ?, ?, ?, ?, ?, ?)
                        """, (save_id, settler_id, str(intent_type), str(item), str(terms), json_dumps_stable(barter_intent), now))
                        conn.execute("""
                            UPDATE dialogue_states
                            SET barter_intent_count = barter_intent_count + 1
                            WHERE save_id = ? AND settler_id = ?
                        """, (save_id, settler_id))
                        insert_typed_memory(
                            conn, save_id, settler_id, "promises",
                            f"Dialogue barter intent: {intent_type} {item} {terms}".strip(), 6,
                            metadata={"barter_intent": barter_intent}
                        )
                        barter_count = 1

                    state = build_dialogue_prompt_context(conn, save_id, settler_id)
                self._send_json(200, {
                    "ok": True,
                    "trust": new_trust,
                    "disclosure_level": disclosure,
                    "claims_recorded": claim_count,
                    "contradictions_recorded": contradiction_count,
                    "barter_intents_recorded": barter_count,
                    "state": state,
                })
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/memory/event":
            npc_id = payload.get("npc_id")
            event_type = payload.get("event_type")
            content = payload.get("content")
            importance = int(payload.get("importance", 5))
            save_id = payload.get("save_id")
            ts = datetime.utcnow().timestamp()

            conn = get_db_connection()
            try:
                with conn:
                    upsert_memory_profile(conn, save_id, npc_id)
                    typed_memory_id = insert_typed_memory(
                        conn,
                        save_id,
                        npc_id,
                        event_type,
                        content,
                        importance,
                        metadata={"legacy_event_type": event_type}
                    )
                    # Save to facts as tier='0' (Recent)
                    conn.execute("""
                        INSERT INTO facts (npc_key, text, category, tier, importance, verbatim, created_at, last_used_at) 
                        VALUES (?, ?, ?, '0', ?, 1, ?, ?)""",
                        (npc_id, content, event_type, importance, ts, ts)
                    )
                    fact_id = conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]
                    conn.execute(
                        "UPDATE facts SET save_id = ?, settler_id = ?, source_typed_memory_id = ? WHERE id = ?",
                        (save_id, npc_id, typed_memory_id, fact_id)
                    )
                    conn.execute("""
                        INSERT INTO memories (npc_id, level, timestamp, session_seq, event_type, content, importance, save_id, typed_memory_id)
                        VALUES (?, 0, ?, 0, ?, ?, ?, ?, ?)
                    """, (npc_id, ts, event_type, content, importance, save_id, typed_memory_id))
                    # If permanent, also save as tier='permanent'
                    if importance >= 9:
                        conn.execute("""
                            INSERT INTO facts (npc_key, text, category, tier, importance, verbatim, created_at, last_used_at) 
                            VALUES (?, ?, ?, 'permanent', ?, 1, ?, ?)""",
                            (npc_id, content, event_type, importance, ts, ts)
                        )
                        perm_fact_id = conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]
                        conn.execute(
                            "UPDATE facts SET save_id = ?, settler_id = ?, source_typed_memory_id = ? WHERE id = ?",
                            (save_id, npc_id, typed_memory_id, perm_fact_id)
                        )
                        conn.execute("""
                            INSERT INTO permanent_memories (npc_id, timestamp, event_type, content, importance, save_id, typed_memory_id)
                            VALUES (?, ?, ?, ?, ?, ?, ?)
                        """, (npc_id, ts, event_type, content, importance, save_id, typed_memory_id))
                    
                    # Memory condensation call is removed in favor of RoleRAG.
                    pass
                self._send_json(200, {"ok": True, "message": "Event recorded successfully", "typed_memory_id": typed_memory_id})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/memory/relationship":
            save_id = payload.get("save_id")
            subject = payload.get("subject")
            obj = payload.get("object")
            friendship = float(payload.get("friendship", 0.0))
            romance = float(payload.get("romance", 0.0))
            rivalry = float(payload.get("rivalry", 0.0))
            trust = float(payload.get("trust", 0.5))
            attraction = float(payload.get("attraction", 0.0))
            fear = float(payload.get("fear", 0.0))
            resentment = float(payload.get("resentment", 0.0))
            standing = payload.get("standing", "neutral")
            rel_type = payload.get("relationship_type", "strangers")
            is_married = int(payload.get("is_married", 0))
            marriage_date = payload.get("marriage_date")
            total_int = int(payload.get("total_interactions", 0))
            pos_int = int(payload.get("positive_interactions", 0))
            neg_int = int(payload.get("negative_interactions", 0))
            last_int = payload.get("last_interaction")
            summary = payload.get("summary", "")

            conn = get_db_connection()
            try:
                with conn:
                    upsert_memory_profile(conn, save_id, subject)
                    upsert_memory_profile(conn, save_id, obj)
                    conn.execute("""
                        INSERT OR REPLACE INTO relationships 
                        (save_id, subject, object, friendship, romance, rivalry, trust, attraction, fear, resentment, 
                         standing, relationship_type, is_married, marriage_date, total_interactions, 
                         positive_interactions, negative_interactions, last_interaction, summary, updated_at) 
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                        (save_id, subject, obj, friendship, romance, rivalry, trust, attraction, fear, resentment,
                         standing, rel_type, is_married, marriage_date, total_int,
                         pos_int, neg_int, last_int, summary, datetime.utcnow().timestamp())
                    )
                    rel_text = summary or f"Relationship with {obj}: {rel_type}, standing={standing}, trust={trust:.2f}, resentment={resentment:.2f}"
                    insert_typed_memory(
                        conn,
                        save_id,
                        subject,
                        "relationship",
                        rel_text,
                        importance=7 if total_int > 0 or is_married else 5,
                        metadata={"object": obj, "relationship_type": rel_type, "standing": standing}
                    )
                self._send_json(200, {"ok": True, "message": "Relationship saved successfully"})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/memory/pressures":
            settler_id = payload.get("settler_id")
            save_id = payload.get("save_id")
            hunger = float(payload.get("hunger", 0.0))
            thirst = float(payload.get("thirst", 0.0))
            exhaustion = float(payload.get("exhaustion", 0.0))
            injury = float(payload.get("injury", 0.0))
            illness = float(payload.get("illness", 0.0))
            threat = float(payload.get("threat", 0.0))
            raid = float(payload.get("raid", 0.0))
            mood = float(payload.get("mood", 0.0))
            recreation = float(payload.get("recreation", 0.0))
            work_skill = float(payload.get("work_skill", 0.0))
            idle = float(payload.get("idle", 0.0))
            social = float(payload.get("social", 0.0))
            rel_tension = float(payload.get("rel_tension", 0.0))
            colony_need = float(payload.get("colony_need", 0.0))
            haul = float(payload.get("haul", 0.0))
            attire = float(payload.get("attire", 0.0))

            conn = get_db_connection()
            try:
                with conn:
                    conn.execute("""
                        INSERT OR REPLACE INTO settler_pressures 
                        (settler_id, save_id, timestamp, hunger_pressure, thirst_pressure, exhaustion_pressure, 
                         injury_pressure, illness_pressure, threat_pressure, raid_alert, mood_pressure, 
                         recreation_lag, work_skill_mismatch, idle_pressure, social_debt, relationship_tension, 
                         colony_need_score, haul_need, attire_mismatch) 
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                        (settler_id, save_id, datetime.utcnow().timestamp(), hunger, thirst, exhaustion, 
                         injury, illness, threat, raid, mood, 
                         recreation, work_skill, idle, social, rel_tension, 
                         colony_need, haul, attire)
                    )
                self._send_json(200, {"ok": True, "message": "Pressures saved successfully"})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/memory/incident":
            settler_id = payload.get("settler_id")
            save_id = payload.get("save_id")
            action = payload.get("action")
            reasoning = payload.get("reasoning", "")
            success = int(payload.get("success", 1))

            conn = get_db_connection()
            try:
                with conn:
                    upsert_memory_profile(conn, save_id, settler_id)
                    conn.execute("""
                        INSERT INTO incidents (settler_id, save_id, timestamp, action, reasoning, success, faction_id, action_type, target, confidence, priority, status, created_at) 
                        VALUES (?, ?, ?, ?, ?, ?, 'player', ?, ?, 1.0, 1, ?, ?)""",
                        (settler_id, save_id, datetime.utcnow().timestamp(), action, reasoning, success, action, reasoning, "success" if success == 1 else "failed", datetime.utcnow().timestamp())
                    )
                    incident_id = conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]
                    insert_typed_memory(
                        conn,
                        save_id,
                        settler_id,
                        "decision",
                        f"Decided to {action}: {reasoning}",
                        importance=6 if success == 1 else 4,
                        metadata={"incident_id": incident_id, "success": success}
                    )
                self._send_json(200, {"ok": True, "message": "Incident logged successfully"})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        elif path == "/api/colony/event":
            save_id = payload.get("save_id")
            state = payload.get("state", {})
            rec = payload.get("rec", {})
            narrative = payload.get("narrative", "")

            conn = get_db_connection()
            try:
                with conn:
                    conn.execute("""
                        INSERT INTO colony_events 
                        (save_id, timestamp, avg_mood, avg_hunger, avg_health, total_settlers, 
                         idle_settlers, colony_wealth, threat_level, pending_blueprints, 
                         recommendation_type, recommendation_desc, narrative) 
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                        (save_id, datetime.utcnow().timestamp(), 
                         float(state.get("AverageMood", 0)), float(state.get("AverageHunger", 0)), float(state.get("AverageHealth", 0)),
                         int(state.get("TotalSettlers", 0)), int(state.get("IdleSettlers", 0)), float(state.get("ColonyWealth", 0)),
                         float(state.get("ThreatLevel", 0)), int(state.get("PendingBlueprints", 0)),
                         rec.get("Type", "none"), rec.get("Description", "none"), narrative)
                    )
                    colony_event_id = conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]
                    for row in conn.execute("SELECT settler_id FROM npcs WHERE save_id = ?", (save_id,)):
                        insert_typed_memory(
                            conn,
                            save_id,
                            row["settler_id"],
                            "colony_event",
                            f"Colony adviser recommended {rec.get('Type', 'none')}: {rec.get('Description', 'none')} {narrative}".strip(),
                            importance=7,
                            metadata={"colony_event_id": colony_event_id}
                        )
                self._send_json(200, {"ok": True, "message": "Colony event saved successfully"})
            except Exception as e:
                self._send_json(500, {"error": str(e)})
            finally:
                conn.close()
            return

        if gm_systems.dispatch(self, GM_CTX, "POST", path, query=None, payload=payload):
            return
        if gm_devops.dispatch(self, GM_CTX, "POST", path, query=None, payload=payload):
            return
        self._send_json(404, {"error": "not found"})

def main():
    init_db()
    start_dev_file_watcher()
    host = "127.0.0.1"
    port = 8714
    server = ReusableThreadingHTTPServer((host, port), DashboardHandler)
    print(f"Going Medieval LLM NPCs Webviewer Dashboard listening on http://{host}:{port}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("Shutting down dashboard server...")
    finally:
        server.server_close()

if __name__ == "__main__":
    main()
