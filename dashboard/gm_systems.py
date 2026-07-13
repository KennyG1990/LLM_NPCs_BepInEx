"""P3+ world systems for the Going Medieval AI Influence mod.

Implements the backend slices of the blueprint documents, in the locked
PLAN.md order, as pure additions behind two dispatch hooks in
dashboard_server.py:

  P3  09 - AI Actions System       -> ai_orders + bounded NL order parser
  P4  10 - Additional Systems      -> entities, mentions, visits, standings,
                                      recruitment detection
  P5  02 - Dynamic World Events    -> world_events + propagation into memories
  P6  03 - AI Diplomacy            -> faction relations, rounds, proclamations
  P7  06 - Romance & Marriage      -> intimacy progression + decay + initiative
  P8  08 - Death History           -> gated milestone life stories
  P9  04 - Disease & Plague        -> infection state machine + outbreaks
  P10 07 - Settlement Combat       -> incident classification + aftermath

Every write is dashboard-visible through a GET endpoint. The module receives
the dashboard_server module as `ctx` so it reuses get_db_connection,
insert_typed_memory, upsert_memory_profile, clamp, record_trust_event and
dialogue_disclosure_level without circular imports.
"""

import json
import re
from datetime import datetime

def _now():
    return datetime.utcnow().timestamp()

def _rows(cursor):
    return [dict(r) for r in cursor]

def _loads(text, default):
    try:
        value = json.loads(text)
        return value if value is not None else default
    except (TypeError, ValueError):
        return default

# ---------------------------------------------------------------------------
# Schema
# ---------------------------------------------------------------------------

def ensure_tables(conn):
    conn.executescript("""
        CREATE TABLE IF NOT EXISTS ai_orders (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            settler_id TEXT NOT NULL,
            raw_text TEXT NOT NULL,
            steps_json TEXT NOT NULL DEFAULT '[]',
            current_step INTEGER DEFAULT 0,
            status TEXT DEFAULT 'queued',       -- queued|active|completed|failed|cancelled|needs_review
            failure_reason TEXT DEFAULT '',
            created_at REAL NOT NULL,
            updated_at REAL NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_ai_orders_save ON ai_orders(save_id, status, updated_at DESC);

        CREATE TABLE IF NOT EXISTS world_entities (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            kind TEXT NOT NULL,                 -- settler|faction|settlement|region|good
            name TEXT NOT NULL,
            standing TEXT DEFAULT 'neutral',    -- own|allied|enemy|neutral (factions/settlements)
            notes TEXT DEFAULT '',
            first_seen REAL NOT NULL,
            last_seen REAL NOT NULL,
            UNIQUE(save_id, kind, name)
        );

        CREATE TABLE IF NOT EXISTS entity_mentions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            settler_id TEXT NOT NULL,
            entity_id INTEGER NOT NULL,
            context TEXT DEFAULT '',
            created_at REAL NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_entity_mentions ON entity_mentions(save_id, settler_id, created_at DESC);

        CREATE TABLE IF NOT EXISTS visit_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            settler_id TEXT NOT NULL,
            place_entity_id INTEGER NOT NULL,
            visited_at REAL NOT NULL,
            details TEXT DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS recruitment_opportunities (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            candidate_name TEXT NOT NULL,
            source TEXT DEFAULT '',
            reason TEXT DEFAULT '',
            score REAL DEFAULT 0.5,
            status TEXT DEFAULT 'open',         -- open|recruited|dismissed
            created_at REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS world_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            event_type TEXT NOT NULL,           -- political|military|economic|social|mysterious|rumor
            title TEXT NOT NULL,
            description TEXT DEFAULT '',
            origin_entity TEXT DEFAULT '',
            affected_json TEXT DEFAULT '[]',
            confidence REAL DEFAULT 0.8,
            status TEXT DEFAULT 'active',       -- active|evolving|resolved|expired
            updates_json TEXT DEFAULT '[]',
            created_at REAL NOT NULL,
            updated_at REAL NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_world_events_save ON world_events(save_id, updated_at DESC);

        CREATE TABLE IF NOT EXISTS world_event_knowledge (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            event_id INTEGER NOT NULL,
            settler_id TEXT NOT NULL,
            rumor_state TEXT DEFAULT 'rumor',   -- firsthand|secondhand|rumor
            learned_at REAL NOT NULL,
            UNIQUE(save_id, event_id, settler_id)
        );

        CREATE TABLE IF NOT EXISTS faction_relations (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            faction_a TEXT NOT NULL,
            faction_b TEXT NOT NULL,
            relation REAL DEFAULT 0.0,          -- -1.0 war .. 1.0 alliance
            state TEXT DEFAULT 'peace',         -- peace|war|truce|alliance
            trade_pact INTEGER DEFAULT 0,
            tribute_json TEXT DEFAULT '{}',
            war_started_at REAL,
            war_fatigue REAL DEFAULT 0.0,
            stats_json TEXT DEFAULT '{}',
            updated_at REAL NOT NULL,
            UNIQUE(save_id, faction_a, faction_b)
        );

        CREATE TABLE IF NOT EXISTS diplomacy_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            round_no INTEGER DEFAULT 0,
            actor TEXT NOT NULL,
            action TEXT NOT NULL,
            target TEXT DEFAULT '',
            proclamation TEXT DEFAULT '',
            terms_json TEXT DEFAULT '{}',
            created_at REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS romance_states (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            settler_id TEXT NOT NULL,
            partner_id TEXT NOT NULL,
            intimacy REAL DEFAULT 0.0,          -- 0..1, distinct from trust
            stage TEXT DEFAULT 'strangers',     -- strangers|acquainted|courting|betrothed|married
            tradition TEXT DEFAULT '',
            last_interaction REAL,
            updated_at REAL NOT NULL,
            UNIQUE(save_id, settler_id, partner_id)
        );

        CREATE TABLE IF NOT EXISTS death_records (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            settler_id TEXT NOT NULL,
            cause TEXT DEFAULT '',
            interaction_count INTEGER DEFAULT 0,
            qualifies INTEGER DEFAULT 0,
            story TEXT DEFAULT '',
            story_status TEXT DEFAULT 'none',   -- none|offered|generated|declined
            died_at REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS disease_states (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            settler_id TEXT NOT NULL,
            disease TEXT NOT NULL,              -- cold|fever|dysentery|plague
            stage TEXT DEFAULT 'incubating',    -- incubating|sick|critical|recovering|recovered|dead
            source TEXT DEFAULT '',
            quarantined INTEGER DEFAULT 0,
            treated INTEGER DEFAULT 0,
            immunity_until REAL,
            infected_at REAL NOT NULL,
            updated_at REAL NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_disease_save ON disease_states(save_id, stage);

        CREATE TABLE IF NOT EXISTS construction_proposals (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            settler_id TEXT NOT NULL,
            building TEXT NOT NULL,
            reason TEXT DEFAULT '',
            urgency REAL DEFAULT 0.5,
            status TEXT DEFAULT 'proposed',     -- proposed|approved|placed|built|rejected
            order_id INTEGER,
            created_at REAL NOT NULL,
            updated_at REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS combat_incidents (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            save_id TEXT NOT NULL,
            trigger_type TEXT NOT NULL,         -- dialogue_hostility|lethal_incident|player_attack
            aggressor TEXT NOT NULL,
            defender TEXT NOT NULL,
            location TEXT DEFAULT '',
            participants_json TEXT DEFAULT '[]',
            casualties_json TEXT DEFAULT '[]',
            verdict_json TEXT DEFAULT '{}',
            aftermath TEXT DEFAULT '',
            created_at REAL NOT NULL
        );
    """)

# ---------------------------------------------------------------------------
# P3 - AI Actions: bounded natural-language order parser
# ---------------------------------------------------------------------------

ORDER_JOBS = (
    "construction", "carpentry", "farming", "botany", "cooking", "culinary",
    "mining", "smithing", "tailoring", "research", "intellectual", "hauling",
    "medicine", "animal handling", "art", "guard", "harvest",
)

_ORDER_SPLIT_RE = re.compile(r"\s*(?:,?\s+then\s+|;\s*|\.\s+then\s+)\s*", re.IGNORECASE)

def parse_order_text(text):
    """Parse free text into a bounded multi-step plan. Unknown segments become
    'unsupported' steps so nothing free-form ever reaches the game."""
    steps = []
    for segment in _ORDER_SPLIT_RE.split(str(text or "").strip()):
        seg = segment.strip().rstrip(".!")
        if not seg:
            continue
        low = seg.lower()
        step = None
        if re.search(r"\bfollow\b", low):
            step = {"action": "follow_player"}
        elif re.search(r"\breturn to work\b|\bresume\b|\bback to work\b", low):
            step = {"action": "return_to_work"}
        elif re.search(r"\breturn\b|\bcome back\b", low):
            step = {"action": "return_to_player"}
        elif re.search(r"\bhold\b|\bstay\b|\bwait\b|\bstand\b", low):
            step = {"action": "hold_position", "target": _order_target(low, ("hold", "stay", "wait", "stand"))}
        elif re.search(r"\bpatrol\b", low):
            step = {"action": "patrol", "target": _order_target(low, ("patrol",))}
        elif re.search(r"\battack\b|\bfight\b|\bengage\b", low):
            step = {"action": "attack_target", "target": _order_target(low, ("attack", "fight", "engage"))}
        elif re.search(r"\bprioriti[sz]e\b|\bfocus on\b|\bwork on\b", low):
            job = next((j for j in ORDER_JOBS if j in low), None)
            if job:
                step = {"action": "prioritize_job", "job": job}
        elif re.search(r"\bgo to\b|\bmove to\b|\bhead to\b|\btravel to\b|\bscout\b", low):
            step = {"action": "move_to", "target": _order_target(low, ("go to", "move to", "head to", "travel to", "scout"))}
        if step is None:
            step = {"action": "unsupported", "raw": seg}
        step["status"] = "pending"
        steps.append(step)
    return steps

def _order_target(low, verbs):
    for verb in verbs:
        idx = low.find(verb)
        if idx >= 0:
            target = low[idx + len(verb):].strip()
            target = re.sub(r"^(?:the|a|an|at|around|near|to)\s+", "", target).strip()
            if target:
                return target[:80]
    return ""

# ---------------------------------------------------------------------------
# P4 - Entities, visits, standings, recruitment
# ---------------------------------------------------------------------------

GOODS_LEXICON = {
    "grain", "salt", "herbs", "iron", "ale", "wool", "timber", "gold",
    "bread", "meat", "leather", "stone", "coal", "beer", "wine", "cheese",
    "linen", "flax", "barley", "cabbage", "honey",
}

_CAP_PHRASE_RE = re.compile(r"\b([A-Z][a-z]{2,}(?:\s+[A-Z][a-z]{2,}){0,2})\b")
_SENTENCE_START_RE = re.compile(r"(?:^|[.!?]\s+)([A-Z][a-z]*)")

def extract_entities(text):
    """Deterministic entity extraction: goods by lexicon, proper nouns by
    capitalization (minus sentence starts). Returns [{kind, name}]."""
    found = []
    text = str(text or "")
    low = text.lower()
    for good in GOODS_LEXICON:
        if re.search(rf"\b{re.escape(good)}\b", low):
            found.append({"kind": "good", "name": good})
    sentence_starts = set(_SENTENCE_START_RE.findall(text))
    for phrase in _CAP_PHRASE_RE.findall(text):
        if phrase in sentence_starts and " " not in phrase:
            continue
        kind = "settlement" if re.search(
            r"(?:village|town|keep|hold|holding|settlement|abbey|ford|bridge|fen)", phrase.lower()) else "settler"
        if re.search(r"(?:clan|house|faction|tribe|folk|fen)$", phrase.lower().split()[-1]):
            kind = "faction"
        found.append({"kind": kind, "name": phrase})
    seen = set()
    unique = []
    for item in found:
        key = (item["kind"], item["name"].lower())
        if key not in seen:
            seen.add(key)
            unique.append(item)
    return unique

def ensure_entity(conn, save_id, kind, name, standing=None):
    now = _now()
    conn.execute("""
        INSERT INTO world_entities (save_id, kind, name, standing, first_seen, last_seen)
        VALUES (?, ?, ?, COALESCE(?, 'neutral'), ?, ?)
        ON CONFLICT(save_id, kind, name) DO UPDATE SET
            last_seen = excluded.last_seen,
            standing = COALESCE(?, world_entities.standing)
    """, (save_id, kind, name, standing, now, now, standing))
    return conn.execute(
        "SELECT id FROM world_entities WHERE save_id = ? AND kind = ? AND name = ?",
        (save_id, kind, name)
    ).fetchone()["id"]

def record_mentions(ctx, conn, save_id, settler_id, text, context=""):
    entities = extract_entities(text)
    recorded = []
    for item in entities:
        entity_id = ensure_entity(conn, save_id, item["kind"], item["name"])
        conn.execute("""
            INSERT INTO entity_mentions (save_id, settler_id, entity_id, context, created_at)
            VALUES (?, ?, ?, ?, ?)
        """, (save_id, settler_id, entity_id, context[:200], _now()))
        recorded.append({"entity_id": entity_id, **item})
    return recorded

def detect_recruitment(conn, save_id, candidate_name, description):
    """Deterministic recruitment scoring from description keywords."""
    low = str(description or "").lower()
    score = 0.3
    reasons = []
    for keyword, weight, why in (
        ("skilled", 0.2, "described as skilled"),
        ("fighter", 0.25, "combat capable"),
        ("soldier", 0.25, "soldier background"),
        ("smith", 0.2, "craft mastery"),
        ("healer", 0.2, "medical value"),
        ("strong", 0.1, "physically strong"),
        ("veteran", 0.25, "veteran experience"),
        ("hunter", 0.15, "provisioning skill"),
    ):
        if keyword in low:
            score += weight
            reasons.append(why)
    score = min(score, 0.95)
    if score < 0.5:
        return None
    conn.execute("""
        INSERT INTO recruitment_opportunities (save_id, candidate_name, source, reason, score, created_at)
        VALUES (?, ?, 'dialogue', ?, ?, ?)
    """, (save_id, candidate_name, "; ".join(reasons), score, _now()))
    return {"candidate": candidate_name, "score": score, "reasons": reasons}

# ---------------------------------------------------------------------------
# P5 - World events
# ---------------------------------------------------------------------------

EVENT_TYPES = {"political", "military", "economic", "social", "mysterious", "rumor"}

def propagate_event(ctx, conn, save_id, event_id, settler_ids, rumor_state="rumor"):
    event = conn.execute(
        "SELECT * FROM world_events WHERE id = ? AND save_id = ?", (event_id, save_id)
    ).fetchone()
    if not event:
        return None
    reached = 0
    for settler_id in settler_ids:
        cur = conn.execute("""
            INSERT OR IGNORE INTO world_event_knowledge (save_id, event_id, settler_id, rumor_state, learned_at)
            VALUES (?, ?, ?, ?, ?)
        """, (save_id, event_id, settler_id, rumor_state, _now()))
        if cur.rowcount:
            reached += 1
            confidence = float(event["confidence"] or 0.8)
            importance = 7 if rumor_state == "firsthand" else (6 if rumor_state == "secondhand" else 5)
            prefix = {
                "firsthand": "Witnessed",
                "secondhand": "Heard from a witness",
                "rumor": "Heard a rumor",
            }[rumor_state if rumor_state in ("firsthand", "secondhand") else "rumor"]
            ctx.insert_typed_memory(
                conn, save_id, settler_id, "event",
                f"{prefix}: {event['title']}. {event['description']}".strip(),
                importance,
                metadata={"world_event_id": event_id, "rumor_state": rumor_state, "confidence": confidence},
            )
    return reached

def known_events(conn, save_id, settler_id, limit=10):
    return _rows(conn.execute("""
        SELECT we.id, we.event_type, we.title, we.description, we.status,
               we.confidence, wek.rumor_state, wek.learned_at
        FROM world_event_knowledge wek
        JOIN world_events we ON we.id = wek.event_id AND we.save_id = wek.save_id
        WHERE wek.save_id = ? AND wek.settler_id = ? AND we.status != 'expired'
        ORDER BY wek.learned_at DESC LIMIT ?
    """, (save_id, settler_id, limit)))

EVENT_EVOLVE_AGE = 2 * 86400     # active -> evolving after 2 days
EVENT_RESOLVE_AGE = 6 * 86400    # evolving -> resolved after 6 days total
EVENT_EXPIRE_AGE = 14 * 86400    # resolved -> expired (leaves dialogue context)

_EVOLUTION_NOTES = {
    "military": ("the fighting drags on and word of fresh skirmishes arrives",
                 "the matter is settled; the survivors count their dead"),
    "political": ("positions harden and new proclamations circulate",
                  "the affair concludes and talk moves on"),
    "economic": ("prices shift as the news works through the markets",
                 "trade finds its new level"),
    "social": ("the story grows in the telling",
                "folk speak of it less with each passing day"),
    "mysterious": ("stranger details attach themselves to the tale",
                   "no answer ever came, and the matter faded"),
    "rumor": ("the rumor mutates as it passes from mouth to mouth",
              "the rumor is worn out and dies"),
}


def events_evolve(ctx, conn, save_id, now=None):
    """Deterministic event lifecycle (doc 02: 'events evolve and update over
    time rather than firing once and vanishing'). Age drives active ->
    evolving -> resolved -> expired, each transition appending an update the
    dialogue layer can narrate. War events resolve EARLY when their war ends
    (state truth beats age)."""
    now = now or _now()
    transitions = []
    for ev in _rows(conn.execute(
            "SELECT * FROM world_events WHERE save_id = ? AND status IN ('active','evolving','resolved')",
            (save_id,))):
        age = now - float(ev["created_at"])
        status = ev["status"]
        new_status = None
        # war events resolve the moment their war ends
        if status in ("active", "evolving") and ev["event_type"] == "military" \
           and str(ev["title"] or "").startswith("War:"):
            affected = _loads(ev["affected_json"], [])
            if len(affected) >= 2:
                rel = conn.execute(
                    "SELECT state FROM faction_relations WHERE save_id=? AND faction_a=? AND faction_b=?",
                    (save_id, *sorted(affected[:2]))).fetchone()
                if rel and rel["state"] != "war":
                    new_status = "resolved"
        if new_status is None:
            if status == "active" and age >= EVENT_EVOLVE_AGE:
                new_status = "evolving"
            elif status == "evolving" and age >= EVENT_RESOLVE_AGE:
                new_status = "resolved"
            elif status == "resolved" and age >= EVENT_EXPIRE_AGE:
                new_status = "expired"
        if new_status is None:
            continue
        notes = _EVOLUTION_NOTES.get(ev["event_type"], _EVOLUTION_NOTES["social"])
        note = notes[0] if new_status == "evolving" else notes[1]
        updates = _loads(ev["updates_json"], [])
        updates.append({"at": now, "status": new_status, "note": note})
        conn.execute("UPDATE world_events SET status = ?, updates_json = ?, updated_at = ? WHERE id = ?",
                     (new_status, json.dumps(updates, ensure_ascii=False), now, ev["id"]))
        transitions.append({"event_id": ev["id"], "title": ev["title"],
                            "from": status, "to": new_status, "note": note})
    return transitions


# ---------------------------------------------------------------------------
# P6 - Diplomacy
# ---------------------------------------------------------------------------

WAR_FATIGUE_PEACE_THRESHOLD = 5.0
TRADE_PACT_LIMIT = 2

def _relation_key(a, b):
    return tuple(sorted((str(a), str(b))))

def get_relation(conn, save_id, faction_a, faction_b):
    a, b = _relation_key(faction_a, faction_b)
    row = conn.execute(
        "SELECT * FROM faction_relations WHERE save_id = ? AND faction_a = ? AND faction_b = ?",
        (save_id, a, b)
    ).fetchone()
    if row:
        return dict(row)
    conn.execute("""
        INSERT INTO faction_relations (save_id, faction_a, faction_b, updated_at)
        VALUES (?, ?, ?, ?)
    """, (save_id, a, b, _now()))
    return dict(conn.execute(
        "SELECT * FROM faction_relations WHERE save_id = ? AND faction_a = ? AND faction_b = ?",
        (save_id, a, b)
    ).fetchone())

def _log_diplomacy(conn, save_id, actor, action, target, proclamation, terms=None, round_no=0):
    conn.execute("""
        INSERT INTO diplomacy_log (save_id, round_no, actor, action, target, proclamation, terms_json, created_at)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    """, (save_id, round_no, actor, action, target, proclamation,
          json.dumps(terms or {}, ensure_ascii=False), _now()))

def _diplomacy_world_event(conn, save_id, event_type, title, description, actors):
    conn.execute("""
        INSERT INTO world_events (save_id, event_type, title, description, origin_entity,
                                  affected_json, confidence, status, created_at, updated_at)
        VALUES (?, ?, ?, ?, ?, ?, 0.9, 'active', ?, ?)
    """, (save_id, event_type, title, description, actors[0],
          json.dumps(actors, ensure_ascii=False), _now(), _now()))
    return conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]

def apply_diplomacy_action(ctx, conn, save_id, faction_a, faction_b, action, terms=None):
    terms = terms or {}
    rel = get_relation(conn, save_id, faction_a, faction_b)
    a, b = rel["faction_a"], rel["faction_b"]
    now = _now()
    stats = _loads(rel["stats_json"], {})
    proclamation = ""
    event = None

    if action == "declare_war":
        # Alliances shatter when war breaks out.
        conn.execute("""
            UPDATE faction_relations SET relation = -1.0, state = 'war', trade_pact = 0,
                   war_started_at = ?, war_fatigue = 0.0, updated_at = ?
            WHERE id = ?
        """, (now, now, rel["id"]))
        stats["wars_declared"] = stats.get("wars_declared", 0) + 1
        proclamation = f"Let it be known: {faction_a} declares war upon {faction_b}. Old pacts are ash."
        event = ("military", f"War: {a} vs {b}", proclamation)
        # ALLIANCE-SHATTER CASCADE (doc 03 / scenario 1: "Osric declares war,
        # their alliance shatters"): every ally of the DEFENDER now thinks
        # worse of the aggressor — attack my friend, lose my goodwill.
        for ally_rel in _rows(conn.execute(
                "SELECT * FROM faction_relations WHERE save_id = ? AND state = 'alliance' "
                "AND (faction_a = ? OR faction_b = ?)", (save_id, faction_b, faction_b))):
            ally = ally_rel["faction_a"] if ally_rel["faction_b"] == faction_b else ally_rel["faction_b"]
            if ally == faction_a:
                continue
            cascade = get_relation(conn, save_id, faction_a, ally)
            new_rel = max(-1.0, float(cascade["relation"] or 0.0) - 0.2)
            conn.execute("UPDATE faction_relations SET relation = ?, updated_at = ? WHERE id = ?",
                         (new_rel, now, cascade["id"]))
            _log_diplomacy(conn, save_id, ally, "ally_grievance", faction_a,
                           f"{ally} condemns {faction_a}'s attack on their ally {faction_b}.")
    elif action == "make_peace":
        reparations = terms.get("reparations", 0)
        conn.execute("""
            UPDATE faction_relations SET relation = 0.1, state = 'peace', war_started_at = NULL,
                   war_fatigue = 0.0, updated_at = ?
            WHERE id = ?
        """, (now, rel["id"]))
        stats["peaces_made"] = stats.get("peaces_made", 0) + 1
        proclamation = f"{faction_a} and {faction_b} lay down arms." + (
            f" Reparations of {reparations} gold shall be paid." if reparations else "")
        event = ("political", f"Peace between {a} and {b}", proclamation)
    elif action == "form_alliance":
        conn.execute("""
            UPDATE faction_relations SET relation = 1.0, state = 'alliance', updated_at = ?
            WHERE id = ?
        """, (now, rel["id"]))
        proclamation = f"{faction_a} and {faction_b} swear alliance, come raid or ruin."
        event = ("political", f"Alliance: {a} & {b}", proclamation)
    elif action == "trade_pact":
        pacts_a = conn.execute("""
            SELECT COUNT(*) AS c FROM faction_relations
            WHERE save_id = ? AND trade_pact = 1 AND (faction_a = ? OR faction_b = ?)
        """, (save_id, faction_a, faction_a)).fetchone()["c"]
        pacts_b = conn.execute("""
            SELECT COUNT(*) AS c FROM faction_relations
            WHERE save_id = ? AND trade_pact = 1 AND (faction_a = ? OR faction_b = ?)
        """, (save_id, faction_b, faction_b)).fetchone()["c"]
        if pacts_a >= TRADE_PACT_LIMIT or pacts_b >= TRADE_PACT_LIMIT:
            return {"ok": False, "error": f"trade pact limit ({TRADE_PACT_LIMIT}) reached"}
        conn.execute("""
            UPDATE faction_relations SET trade_pact = 1,
                   relation = MIN(1.0, relation + 0.2), updated_at = ?
            WHERE id = ?
        """, (now, rel["id"]))
        proclamation = f"{faction_a} and {faction_b} seal a trade pact: caravans move freely."
        event = ("economic", f"Trade pact: {a} & {b}", proclamation)
    elif action == "tribute":
        tribute = {"payer": terms.get("payer", faction_b), "amount": terms.get("amount", 10),
                   "kind": terms.get("kind", "gold"), "recurring": True}
        conn.execute("""
            UPDATE faction_relations SET tribute_json = ?, updated_at = ? WHERE id = ?
        """, (json.dumps(tribute, ensure_ascii=False), now, rel["id"]))
        proclamation = (f"{tribute['payer']} shall render {tribute['amount']} {tribute['kind']} "
                        f"in tribute each season.")
        event = ("economic", f"Tribute owed between {a} and {b}", proclamation)
    elif action == "banish":
        house = terms.get("house", "an unnamed house")
        proclamation = f"By decree of {faction_a}: the house of {house} is cast out. Let none shelter them."
        event = ("political", f"Banishment in {faction_a}", proclamation)
    elif action == "pardon":
        house = terms.get("house", "an unnamed house")
        proclamation = f"{faction_a} grants pardon: the house of {house} may return to hearth and hall."
        event = ("political", f"Pardon in {faction_a}", proclamation)
    else:
        return {"ok": False, "error": f"unknown diplomacy action '{action}'"}

    conn.execute("UPDATE faction_relations SET stats_json = ? WHERE id = ?",
                 (json.dumps(stats, ensure_ascii=False), rel["id"]))
    _log_diplomacy(conn, save_id, faction_a, action, faction_b, proclamation, terms)
    event_id = None
    if event:
        event_id = _diplomacy_world_event(conn, save_id, event[0], event[1], event[2], [a, b])
        # PROPAGATE (scenario 1's rumor loop): a proclamation nobody hears
        # never reaches dialogue. Every known settler learns it as rumor —
        # word of wars and pacts travels; the depth of detail stays gated by
        # trust at dialogue time.
        settlers = [row["settler_id"] for row in _rows(conn.execute(
            "SELECT settler_id FROM npc_memory_profiles WHERE save_id = ?", (save_id,)))]
        if settlers:
            try:
                propagate_event(ctx, conn, save_id, event_id, settlers, "rumor")
            except Exception:  # noqa: BLE001 - propagation must never break the action
                pass
    ensure_entity(conn, save_id, "faction", faction_a)
    ensure_entity(conn, save_id, "faction", faction_b)
    return {"ok": True, "action": action, "proclamation": proclamation, "world_event_id": event_id}

# Legal-move thresholds in the relation float domain (-1.0 .. 1.0)
WAR_LEGAL_BELOW = -0.4
ALLIANCE_LEGAL_ABOVE = 0.4
TRIBUTE_LEGAL_BAND = (-0.4, -0.2)
PEACE_LEGAL_FATIGUE = 3.0


def known_factions(conn, save_id):
    names = set()
    for row in _rows(conn.execute(
            "SELECT faction_a, faction_b FROM faction_relations WHERE save_id = ?", (save_id,))):
        names.add(row["faction_a"]); names.add(row["faction_b"])
    for row in _rows(conn.execute(
            "SELECT name FROM world_entities WHERE save_id = ? AND kind = 'faction'", (save_id,))):
        names.add(row["name"])
    return sorted(names)


def diplomacy_legal_moves(conn, save_id, faction):
    """The BOUNDED MENU (design doc P6): every move the state permits this
    faction right now. The LLM may only ever pick from this list — an
    invalid pick falls back deterministically. Pure state -> menu."""
    moves = []
    for other in known_factions(conn, save_id):
        if other == faction:
            continue
        rel = get_relation(conn, save_id, faction, other)
        relation = float(rel["relation"] or 0.0)
        if rel["state"] == "war":
            if float(rel["war_fatigue"] or 0.0) >= PEACE_LEGAL_FATIGUE:
                moves.append({"kind": "make_peace", "target": other})
        else:
            if relation < WAR_LEGAL_BELOW:
                moves.append({"kind": "declare_war", "target": other})
            if relation >= ALLIANCE_LEGAL_ABOVE and rel["state"] != "alliance":
                moves.append({"kind": "form_alliance", "target": other})
            if relation >= 0.0 and not rel["trade_pact"]:
                moves.append({"kind": "trade_pact", "target": other})
            if TRIBUTE_LEGAL_BAND[0] <= relation < TRIBUTE_LEGAL_BAND[1]:
                moves.append({"kind": "tribute", "target": other})
    moves.append({"kind": "no_move", "target": None})
    return moves


def report_raid(ctx, conn, save_id, raider, target, casualties_raider=0, casualties_target=0):
    """GROUND-TRUTH FEED: a real in-game raid. Worsens relations, records
    losses in the pair's war stats, and — because a raid IS an act of war —
    escalates to declared war once relations collapse past the threshold."""
    rel = get_relation(conn, save_id, raider, target)
    relation = max(-1.0, float(rel["relation"] or 0.0) - 0.25)
    stats = _loads(rel["stats_json"], {})
    stats["losses_" + str(raider)] = stats.get("losses_" + str(raider), 0) + int(casualties_raider)
    stats["losses_" + str(target)] = stats.get("losses_" + str(target), 0) + int(casualties_target)
    stats["raids"] = stats.get("raids", 0) + 1
    conn.execute("UPDATE faction_relations SET relation = ?, stats_json = ?, updated_at = ? WHERE id = ?",
                 (relation, json.dumps(stats, ensure_ascii=False), _now(), rel["id"]))
    _log_diplomacy(conn, save_id, raider, "raid", target,
                   f"{raider} raided {target}" +
                   (f" ({casualties_target} defenders fell)" if casualties_target else "."))
    escalated = None
    if rel["state"] != "war" and relation <= -0.6:
        escalated = apply_diplomacy_action(ctx, conn, save_id, raider, target, "declare_war")
    return {"ok": True, "relation": round(relation, 2), "escalated_to_war": bool(escalated)}


def _reparations_amount(stats, a, b):
    """Loss-ratio reparations (doc 03): the bloodier loser pays more."""
    la = int(stats.get("losses_" + str(a), 0))
    lb = int(stats.get("losses_" + str(b), 0))
    return 25 + 25 * abs(la - lb), (a if la >= lb else b)


def run_diplomacy_round(ctx, conn, save_id, choose_move=None):
    """One deterministic diplomacy round: fatigue ticks, exhausted wars sue
    for peace, relations drift toward their state's baseline — and ONE
    faction (round-robin by least-recent mover) makes a menu move.
    choose_move(faction, menu) -> move injects the LLM; None or an illegal
    answer falls back to the first non-trivial legal move."""
    round_no = (conn.execute(
        "SELECT COALESCE(MAX(round_no), 0) AS r FROM diplomacy_log WHERE save_id = ?", (save_id,)
    ).fetchone()["r"] or 0) + 1
    moves = []
    for rel in _rows(conn.execute(
            "SELECT * FROM faction_relations WHERE save_id = ?", (save_id,))):
        a, b = rel["faction_a"], rel["faction_b"]
        if rel["state"] == "war":
            fatigue = float(rel["war_fatigue"] or 0.0) + 1.0
            conn.execute("UPDATE faction_relations SET war_fatigue = ?, updated_at = ? WHERE id = ?",
                         (fatigue, _now(), rel["id"]))
            stats = _loads(rel["stats_json"], {})
            stats["rounds_at_war"] = stats.get("rounds_at_war", 0) + 1
            conn.execute("UPDATE faction_relations SET stats_json = ? WHERE id = ?",
                         (json.dumps(stats, ensure_ascii=False), rel["id"]))
            if fatigue >= WAR_FATIGUE_PEACE_THRESHOLD:
                amount, payer = _reparations_amount(_loads(rel["stats_json"], {}), a, b)
                result = apply_diplomacy_action(ctx, conn, save_id, a, b, "make_peace",
                                                {"reparations": amount, "payer": payer,
                                                 "why": "war fatigue"})
                _log_diplomacy(conn, save_id, a, "war_fatigue_peace", b,
                               result["proclamation"], {"fatigue": fatigue}, round_no)
                moves.append({"actor": a, "action": "war_fatigue_peace", "target": b, "fatigue": fatigue})
            else:
                moves.append({"actor": a, "action": "war_continues", "target": b, "fatigue": fatigue})
        else:
            baseline = {"peace": 0.0, "truce": 0.0, "alliance": 1.0}.get(rel["state"], 0.0)
            relation = float(rel["relation"] or 0.0)
            drifted = relation + (0.05 if relation < baseline else (-0.05 if relation > baseline else 0.0))
            if abs(drifted - relation) > 1e-9:
                conn.execute("UPDATE faction_relations SET relation = ?, updated_at = ? WHERE id = ?",
                             (drifted, _now(), rel["id"]))
                moves.append({"actor": a, "action": "relation_drift", "target": b, "relation": round(drifted, 2)})
    # ONE agent move per round (doc 03: "factions take turns... at a
    # believable pace"): round-robin by least-recent mover, bounded menu,
    # injectable chooser (the LLM), deterministic fallback on garbage.
    factions = known_factions(conn, save_id)
    if factions:
        recency = {row["actor"]: row["last"] for row in _rows(conn.execute(
            "SELECT actor, MAX(created_at) AS last FROM diplomacy_log "
            "WHERE save_id = ? GROUP BY actor", (save_id,)))}
        mover = sorted(factions, key=lambda f: recency.get(f, 0))[0]
        menu = diplomacy_legal_moves(conn, save_id, mover)
        move = None
        if choose_move is not None:
            try:
                move = choose_move(mover, menu)
            except Exception:  # noqa: BLE001 - LLM chooser must never break the round
                move = None
        if move is None or not any(m["kind"] == move.get("kind") and m["target"] == move.get("target")
                                   for m in menu):
            real = [m for m in menu if m["kind"] != "no_move"]
            move = real[0] if real else {"kind": "no_move", "target": None}
        if move["kind"] != "no_move":
            terms = {"payer": move["target"], "amount": 5} if move["kind"] == "tribute" else None
            agent_result = apply_diplomacy_action(ctx, conn, save_id, mover, move["target"],
                                                  move["kind"], terms)
            if agent_result.get("ok"):
                moves.append({"actor": mover, "action": move["kind"], "target": move["target"]})
        else:
            _log_diplomacy(conn, save_id, mover, "no_move", "",
                           f"{mover} held their counsel this round.", None, round_no)
            moves.append({"actor": mover, "action": "no_move", "target": None})
    _log_diplomacy(conn, save_id, "world", "round_completed", "",
                   f"Diplomacy round {round_no}: {len(moves)} moves.", {"moves": len(moves)}, round_no)
    return {"round": round_no, "moves": moves}

# ---------------------------------------------------------------------------
# P7 - Romance
# ---------------------------------------------------------------------------

ROMANCE_INTERACTIONS = {
    "courtship": 0.08,
    "gift": 0.06,
    "shared_meal": 0.04,
    "kiss": 0.10,
    "proposal": 0.0,      # gated below
    "neglect": -0.08,
}
ROMANCE_STAGES = (
    (0.00, "strangers"),
    (0.15, "acquainted"),
    (0.40, "courting"),
    (0.70, "betrothed"),
    (0.90, "married"),
)
ROMANCE_DECAY_PER_DAY = 0.02
ROMANCE_INITIATIVE_TRAITS = {"sanguine", "brave", "reckless", "proud"}

def romance_stage_for(intimacy, current_stage="strangers", proposal_accepted=False):
    stage = "strangers"
    for threshold, name in ROMANCE_STAGES:
        if intimacy >= threshold:
            stage = name
    # marriage requires an accepted proposal, not just a number
    if stage in ("betrothed", "married") and current_stage not in ("betrothed", "married") and not proposal_accepted:
        stage = "courting"
    if stage == "married" and current_stage != "married" and not proposal_accepted:
        stage = "betrothed" if current_stage == "betrothed" else "courting"
    return stage

def romance_interact(ctx, conn, save_id, settler_id, partner_id, interaction, tradition=""):
    if interaction not in ROMANCE_INTERACTIONS:
        return {"ok": False, "error": f"unknown interaction '{interaction}'"}
    row = conn.execute("""
        SELECT * FROM romance_states WHERE save_id = ? AND settler_id = ? AND partner_id = ?
    """, (save_id, settler_id, partner_id)).fetchone()
    now = _now()
    intimacy = float(row["intimacy"]) if row else 0.0
    stage = row["stage"] if row else "strangers"
    proposal_accepted = False
    if interaction == "proposal":
        if intimacy < 0.6:
            return {"ok": False, "error": "proposal rejected: intimacy too low", "intimacy": intimacy}
        proposal_accepted = True
        intimacy = min(1.0, intimacy + 0.1)
    else:
        intimacy = ctx.clamp(intimacy + ROMANCE_INTERACTIONS[interaction], 0.0, 1.0)
    new_stage = romance_stage_for(intimacy, stage, proposal_accepted)
    conn.execute("""
        INSERT INTO romance_states (save_id, settler_id, partner_id, intimacy, stage, tradition, last_interaction, updated_at)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?)
        ON CONFLICT(save_id, settler_id, partner_id) DO UPDATE SET
            intimacy = excluded.intimacy, stage = excluded.stage,
            tradition = CASE WHEN excluded.tradition != '' THEN excluded.tradition ELSE romance_states.tradition END,
            last_interaction = excluded.last_interaction, updated_at = excluded.updated_at
    """, (save_id, settler_id, partner_id, intimacy, new_stage, tradition, now, now))
    if new_stage != stage:
        ctx.insert_typed_memory(
            conn, save_id, settler_id, "relationship",
            f"Relationship with {partner_id} deepened to {new_stage}." if intimacy > 0
            else f"Relationship with {partner_id} cooled to {new_stage}.",
            8 if new_stage in ("betrothed", "married") else 6,
            metadata={"partner": partner_id, "stage": new_stage, "intimacy": round(intimacy, 2)},
        )
    return {"ok": True, "intimacy": round(intimacy, 3), "stage": new_stage, "stage_changed": new_stage != stage}

def romance_decay(ctx, conn, save_id, now=None):
    now = now or _now()
    decayed = []
    for row in _rows(conn.execute(
            "SELECT * FROM romance_states WHERE save_id = ? AND stage NOT IN ('married')", (save_id,))):
        last = float(row["last_interaction"] or row["updated_at"])
        days_idle = (now - last) / 86400.0
        if days_idle < 1.0:
            continue
        loss = ROMANCE_DECAY_PER_DAY * days_idle
        intimacy = max(0.0, float(row["intimacy"]) - loss)
        new_stage = romance_stage_for(intimacy, row["stage"])
        # decay never re-marries or re-betroths anyone; it only cools
        if new_stage in ("betrothed", "married") and intimacy < 0.7:
            new_stage = "courting"
        conn.execute("UPDATE romance_states SET intimacy = ?, stage = ?, updated_at = ? WHERE id = ?",
                     (intimacy, new_stage, now, row["id"]))
        if new_stage != row["stage"]:
            ctx.insert_typed_memory(
                conn, save_id, row["settler_id"], "relationship",
                f"The bond with {row['partner_id']} cooled from neglect ({row['stage']} -> {new_stage}).",
                6, metadata={"partner": row["partner_id"], "stage": new_stage, "decay": True},
            )
        decayed.append({"settler_id": row["settler_id"], "partner_id": row["partner_id"],
                        "intimacy": round(intimacy, 3), "stage": new_stage})
    return decayed

def romance_initiative_candidates(conn, save_id):
    """Settlers whose intimacy and traits suggest they would initiate."""
    candidates = []
    for row in _rows(conn.execute("""
        SELECT rs.*, p.traits FROM romance_states rs
        LEFT JOIN npc_memory_profiles p ON p.save_id = rs.save_id AND p.settler_id = rs.settler_id
        WHERE rs.save_id = ? AND rs.intimacy >= 0.3 AND rs.stage IN ('acquainted', 'courting')
    """, (save_id,))):
        traits = {t.strip().lower() for t in str(row["traits"] or "").replace(";", ",").split(",")}
        if traits & ROMANCE_INITIATIVE_TRAITS:
            candidates.append({
                "settler_id": row["settler_id"], "partner_id": row["partner_id"],
                "intimacy": row["intimacy"], "stage": row["stage"],
                "because": sorted(traits & ROMANCE_INITIATIVE_TRAITS),
            })
    return candidates

ROMANCE_SIGNAL_THRESHOLD = 0.3    # (romance+attraction)/2 needed for autonomy
ROMANCE_STAGE_INTERACTIONS = {    # what a stage's courtship looks like
    "strangers": ("shared_meal",),
    "acquainted": ("shared_meal", "gift"),
    "courting": ("courtship", "gift", "kiss"),
    "betrothed": ("courtship", "kiss"),
}


def romance_autonomous_tick(ctx, conn, save_id, rng=None):
    """Bonds form BY THEMSELVES (doc 06 'forged by the hearth, not the
    spreadsheet' + doc 01 NPC initiative): pairs whose live social state
    (relationships.romance/attraction, fed by the mod's social hub) carries a
    real signal roll courtship interactions; initiative-trait settlers with
    deep enough intimacy propose. Betrothals and marriages become world
    events the whole village hears about (scenario 4). Decay runs first —
    neglect cools what autonomy doesn't tend."""
    import random as _random
    rng = rng or _random
    decayed = romance_decay(ctx, conn, save_id)
    interactions = []
    milestones = []
    pairs = _rows(conn.execute("""
        SELECT subject, object,
               (COALESCE(romance, 0) + COALESCE(attraction, 0)) / 2.0 AS signal
        FROM relationships
        WHERE save_id = ? AND subject < object
          AND (COALESCE(romance, 0) + COALESCE(attraction, 0)) / 2.0 >= ?
    """, (save_id, ROMANCE_SIGNAL_THRESHOLD)))
    for pair in pairs:
        a, b, signal = pair["subject"], pair["object"], float(pair["signal"])
        if rng.random() >= 0.25 + signal * 0.5:
            continue
        state = conn.execute(
            "SELECT stage, intimacy FROM romance_states WHERE save_id=? AND settler_id=? AND partner_id=?",
            (save_id, a, b)).fetchone()
        stage = state["stage"] if state else "strangers"
        intimacy = float(state["intimacy"]) if state else 0.0
        # proposal: only an initiative-trait settler with deep intimacy —
        # once at "courting" (the betrothal ask) and again at "betrothed"
        # with deeper intimacy still (the wedding vows).
        interaction = None
        if (stage == "courting" and intimacy >= 0.6) or \
           (stage == "betrothed" and intimacy >= 0.8):
            traits_row = conn.execute(
                "SELECT traits FROM npc_memory_profiles WHERE save_id=? AND settler_id=?",
                (save_id, a)).fetchone()
            traits = {t.strip().lower() for t in
                      str(traits_row["traits"] if traits_row else "").replace(";", ",").split(",")}
            if traits & ROMANCE_INITIATIVE_TRAITS:
                interaction = "proposal"
        if interaction is None:
            options = ROMANCE_STAGE_INTERACTIONS.get(stage, ("shared_meal",))
            interaction = options[rng.randrange(len(options))]
        result = romance_interact(ctx, conn, save_id, a, b, interaction)
        if not result.get("ok"):
            continue
        interactions.append({"pair": (a, b), "interaction": interaction,
                             "stage": result["stage"]})
        if result.get("stage_changed") and result["stage"] in ("betrothed", "married"):
            verb = "are betrothed" if result["stage"] == "betrothed" else "are wed"
            event_id = _diplomacy_world_event(
                conn, save_id, "social", f"{a} and {b} {verb}",
                f"Word spreads through the settlement: {a} and {b} {verb}.", [a, b])
            settlers = [r["settler_id"] for r in _rows(conn.execute(
                "SELECT settler_id FROM npc_memory_profiles WHERE save_id = ?", (save_id,)))]
            if settlers:
                try:
                    propagate_event(ctx, conn, save_id, event_id, settlers, "secondhand")
                except Exception:  # noqa: BLE001
                    pass
            milestones.append({"pair": (a, b), "stage": result["stage"],
                               "world_event_id": event_id})
    return {"decayed": decayed, "interactions": interactions, "milestones": milestones}


# ---------------------------------------------------------------------------
# P8 - Death history
# ---------------------------------------------------------------------------

DEATH_HISTORY_INTERACTION_GATE = 50

def record_death(ctx, conn, save_id, settler_id, cause=""):
    interactions = conn.execute(
        "SELECT COUNT(*) AS c FROM typed_memories WHERE save_id = ? AND settler_id = ?",
        (save_id, settler_id)
    ).fetchone()["c"]
    qualifies = 1 if interactions >= DEATH_HISTORY_INTERACTION_GATE else 0
    conn.execute("""
        INSERT INTO death_records (save_id, settler_id, cause, interaction_count, qualifies, story_status, died_at)
        VALUES (?, ?, ?, ?, ?, ?, ?)
    """, (save_id, settler_id, cause, interactions, qualifies,
          "offered" if qualifies else "none", _now()))
    record_id = conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]
    # every settler death is a world event candidate and a memory for others
    for other in _rows(conn.execute(
            "SELECT settler_id FROM npc_memory_profiles WHERE save_id = ? AND settler_id != ?",
            (save_id, settler_id))):
        ctx.insert_typed_memory(
            conn, save_id, other["settler_id"], "death",
            f"{settler_id} died{f' of {cause}' if cause else ''}.", 8,
            metadata={"death_record_id": record_id},
        )
    return {"death_record_id": record_id, "interaction_count": interactions, "qualifies": bool(qualifies)}

def generate_death_history(ctx, conn, save_id, settler_id, record_id, accept=True):
    record = conn.execute(
        "SELECT * FROM death_records WHERE id = ? AND save_id = ? AND settler_id = ?",
        (record_id, save_id, settler_id)
    ).fetchone()
    if not record:
        return {"ok": False, "error": "death record not found"}
    if not accept:
        conn.execute("UPDATE death_records SET story_status = 'declined' WHERE id = ?", (record_id,))
        return {"ok": True, "story_status": "declined"}
    if not record["qualifies"]:
        return {"ok": False, "error": "does not qualify for a deep history "
                f"({record['interaction_count']} interactions < {DEATH_HISTORY_INTERACTION_GATE})"}
    profile = conn.execute(
        "SELECT * FROM npc_memory_profiles WHERE save_id = ? AND settler_id = ?",
        (save_id, settler_id)
    ).fetchone()
    milestones = _rows(conn.execute("""
        SELECT category, content, importance FROM typed_memories
        WHERE save_id = ? AND settler_id = ? AND importance >= 7
        ORDER BY importance DESC, created_at ASC LIMIT 12
    """, (save_id, settler_id)))
    relationships = _rows(conn.execute("""
        SELECT object, relationship_type, trust FROM relationships
        WHERE save_id = ? AND subject = ? ORDER BY trust DESC LIMIT 5
    """, (save_id, settler_id)))
    name = (profile["display_name"] if profile else None) or settler_id
    role = (profile["role"] if profile else None) or "settler"
    lines = [f"Here lies {name}, {role} of this settlement."]
    if record["cause"]:
        lines.append(f"Taken by {record['cause']}.")
    if milestones:
        lines.append("Their days held moments worth the telling:")
        for m in milestones[:8]:
            lines.append(f"- {m['content']}")
    if relationships:
        bonds = ", ".join(f"{r['object']} ({r['relationship_type']})" for r in relationships[:3])
        lines.append(f"They were bound closest to {bonds}.")
    lines.append(f"Remembered through {record['interaction_count']} moments lived alongside us.")
    story = "\n".join(lines)
    conn.execute("UPDATE death_records SET story = ?, story_status = 'generated' WHERE id = ?",
                 (story, record_id))
    return {"ok": True, "story_status": "generated", "story": story}

# ---------------------------------------------------------------------------
# P9 - Disease
# ---------------------------------------------------------------------------

DISEASES = {"cold", "fever", "dysentery", "plague"}
SEASON_RISK = {"winter": 0.30, "autumn": 0.20, "spring": 0.15, "summer": 0.05}
OUTBREAK_THRESHOLD = 3
IMMUNITY_DAYS = 21

def _medicine_skill(conn, save_id, settler_id):
    try:
        row = conn.execute(
            "SELECT * FROM character_sheets WHERE save_id = ? AND settler_id = ?",
            (save_id, settler_id)
        ).fetchone()
    except Exception:  # noqa: BLE001 - table shape varies across saves
        return 0
    if not row:
        return 0
    keys = row.keys()
    skills = _loads(row["skills_json"], {}) if "skills_json" in keys else {}
    for key, value in (skills or {}).items():
        if "medic" in str(key).lower():
            try:
                return int(float(value))
            except (TypeError, ValueError):
                return 0
    return 0

def infect(ctx, conn, save_id, settler_id, disease, source="", season="winter"):
    if disease not in DISEASES:
        return {"ok": False, "error": f"unknown disease '{disease}'"}
    now = _now()
    immune = conn.execute("""
        SELECT 1 FROM disease_states
        WHERE save_id = ? AND settler_id = ? AND immunity_until > ?
        LIMIT 1
    """, (save_id, settler_id, now)).fetchone()
    if immune and disease != "plague":
        return {"ok": True, "infected": False, "why": "temporary immunity held"}
    skill = _medicine_skill(conn, save_id, settler_id)
    if disease != "plague" and skill >= 7:
        return {"ok": True, "infected": False, "why": f"resisted (Medicine {skill})"}
    already = conn.execute("""
        SELECT 1 FROM disease_states
        WHERE save_id = ? AND settler_id = ? AND disease = ? AND stage IN ('incubating','sick','critical')
    """, (save_id, settler_id, disease)).fetchone()
    if already:
        return {"ok": True, "infected": False, "why": "already infected"}
    conn.execute("""
        INSERT INTO disease_states (save_id, settler_id, disease, stage, source, infected_at, updated_at)
        VALUES (?, ?, ?, 'incubating', ?, ?, ?)
    """, (save_id, settler_id, disease, source, now, now))
    ctx.insert_typed_memory(
        conn, save_id, settler_id, "health",
        f"Fell ill with {disease}{f' after contact with {source}' if source else ''}.",
        7, metadata={"disease": disease, "season": season},
    )
    active = conn.execute("""
        SELECT COUNT(DISTINCT settler_id) AS c FROM disease_states
        WHERE save_id = ? AND disease = ? AND stage IN ('incubating','sick','critical')
    """, (save_id, disease)).fetchone()["c"]
    outbreak_event_id = None
    if active >= OUTBREAK_THRESHOLD:
        existing = conn.execute("""
            SELECT id FROM world_events
            WHERE save_id = ? AND event_type = 'social' AND title = ? AND status = 'active'
        """, (save_id, f"Outbreak of {disease}")).fetchone()
        if not existing:
            outbreak_event_id = _diplomacy_world_event(
                conn, save_id, "social", f"Outbreak of {disease}",
                f"{active} settlers are down with {disease}. Word of it spreads.",
                ["settlement"])
            # scenario 2: "a visiting envoy hears of the outbreak and it
            # becomes a talking point" — everyone learns the rumor.
            settlers = [r["settler_id"] for r in _rows(conn.execute(
                "SELECT settler_id FROM npc_memory_profiles WHERE save_id = ?", (save_id,)))]
            if settlers:
                try:
                    propagate_event(ctx, conn, save_id, outbreak_event_id, settlers, "rumor")
                except Exception:  # noqa: BLE001
                    pass
    return {"ok": True, "infected": True, "stage": "incubating", "active_cases": active,
            "outbreak_event_id": outbreak_event_id}

DISEASE_PROGRESSION = {
    "incubating": "sick",
    "sick": "recovering",       # default optimistic path, modified below
    "critical": "recovering",
    "recovering": "recovered",
}

def disease_tick(ctx, conn, save_id):
    """One deterministic progression tick. Untreated+unquarantined sickness
    escalates to critical; treated settlers recover; plague untreated is
    critical then flagged dead."""
    now = _now()
    changes = []
    for row in _rows(conn.execute("""
        SELECT * FROM disease_states WHERE save_id = ? AND stage IN ('incubating','sick','critical')
    """, (save_id,))):
        stage = row["stage"]
        treated = bool(row["treated"])
        quarantined = bool(row["quarantined"])
        disease = row["disease"]
        if stage == "incubating":
            new_stage = "sick"
        elif stage == "sick":
            if treated or (quarantined and disease != "plague"):
                new_stage = "recovering"
            elif disease in ("plague", "dysentery"):
                new_stage = "critical"
            else:
                new_stage = "recovering" if quarantined else "sick_worsening"
                if new_stage == "sick_worsening":
                    new_stage = "critical" if disease == "fever" else "recovering"
        elif stage == "critical":
            new_stage = "recovering" if treated else "dead"
        else:
            continue
        if new_stage == "recovering":
            pass
        if new_stage == "dead":
            conn.execute("UPDATE disease_states SET stage = 'dead', updated_at = ? WHERE id = ?",
                         (now, row["id"]))
            record_death(ctx, conn, save_id, row["settler_id"], cause=disease)
        elif new_stage == "recovering" and stage == "recovering":
            pass
        else:
            immunity = now + IMMUNITY_DAYS * 86400 if new_stage == "recovering" and disease != "plague" else row["immunity_until"]
            conn.execute("""
                UPDATE disease_states SET stage = ?, immunity_until = ?, updated_at = ? WHERE id = ?
            """, (new_stage, immunity, now, row["id"]))
        changes.append({"settler_id": row["settler_id"], "disease": disease,
                        "from": stage, "to": new_stage})
    # recovering -> recovered on the following tick
    for row in _rows(conn.execute("""
        SELECT * FROM disease_states WHERE save_id = ? AND stage = 'recovering' AND updated_at < ?
    """, (save_id, now - 1))):
        conn.execute("UPDATE disease_states SET stage = 'recovered', updated_at = ? WHERE id = ?",
                     (now, row["id"]))
        ctx.insert_typed_memory(
            conn, save_id, row["settler_id"], "health",
            f"Recovered from {row['disease']}; hardier for {IMMUNITY_DAYS} days.",
            6, metadata={"disease": row["disease"], "immune_days": IMMUNITY_DAYS},
        )
        changes.append({"settler_id": row["settler_id"], "disease": row["disease"],
                        "from": "recovering", "to": "recovered"})
    return changes

SPREAD_QUARANTINE_FACTOR = 0.25   # quarantine cuts spread chance to a quarter
ONSET_FACTOR = 0.10               # seasonal onset = SEASON_RISK * this, per tick
SEASON_DISEASES = {               # what the season itself brings (doc 04)
    "winter": ("cold", "fever"),
    "autumn": ("cold", "dysentery"),
    "spring": ("fever",),
    "summer": ("dysentery",),
}


def disease_spread(ctx, conn, save_id, season="winter", rng=None):
    """Person-to-person spread (doc 04 bullet 1 / scenario 2: 'a sick
    traveller infects two settlers'). Each contagious, UNQUARANTINED case
    rolls SEASON_RISK against every other settler; quarantine cuts the roll
    to a quarter. infect() itself still applies immunity and Medicine-skill
    resistance. rng injectable for deterministic tests."""
    import random as _random
    rng = rng or _random
    risk = SEASON_RISK.get(season, 0.15)
    infections = []
    carriers = _rows(conn.execute("""
        SELECT DISTINCT settler_id, disease, quarantined FROM disease_states
        WHERE save_id = ? AND stage IN ('sick', 'critical')
    """, (save_id,)))
    if not carriers:
        return infections
    settlers = [row["settler_id"] for row in _rows(conn.execute(
        "SELECT settler_id FROM npc_memory_profiles WHERE save_id = ?", (save_id,)))]
    for carrier in carriers:
        chance = risk * (SPREAD_QUARANTINE_FACTOR if carrier["quarantined"] else 1.0)
        for target in settlers:
            if target == carrier["settler_id"]:
                continue
            if rng.random() < chance:
                result = infect(ctx, conn, save_id, target, carrier["disease"],
                                source=carrier["settler_id"], season=season)
                if result.get("infected"):
                    infections.append({"from": carrier["settler_id"], "to": target,
                                       "disease": carrier["disease"]})
    return infections


def seasonal_onset(ctx, conn, save_id, season="winter", rng=None):
    """The season itself sickens people (doc 04: 'odds driven by the
    season, cold and wet weather'): a small per-tick chance that one settler
    catches the season's illness with no carrier involved."""
    import random as _random
    rng = rng or _random
    chance = SEASON_RISK.get(season, 0.15) * ONSET_FACTOR
    diseases = SEASON_DISEASES.get(season, ("cold",))
    onsets = []
    for row in _rows(conn.execute(
            "SELECT settler_id FROM npc_memory_profiles WHERE save_id = ?", (save_id,))):
        if rng.random() < chance:
            disease = diseases[rng.randrange(len(diseases))] if hasattr(rng, "randrange") \
                else diseases[0]
            result = infect(ctx, conn, save_id, row["settler_id"], disease,
                            source=f"the {season} air", season=season)
            if result.get("infected"):
                onsets.append({"settler_id": row["settler_id"], "disease": disease})
    return onsets


# ---------------------------------------------------------------------------
# P10 - Combat incidents
# ---------------------------------------------------------------------------

DEFENDER_LOCATIONS = {"gate", "gatehouse", "wall", "courtyard", "great hall", "granary", "market"}

def classify_combat_incident(ctx, conn, save_id, trigger_type, aggressor, defender,
                             location="", participants=None, casualties=None):
    participants = participants or []
    casualties = casualties or []
    low_loc = str(location or "").lower()
    defenders_needed = any(token in low_loc for token in DEFENDER_LOCATIONS) or len(participants) >= 3
    civilian_panic = trigger_type != "dialogue_hostility" or len(casualties) > 0
    stances = []
    for participant in participants:
        rel = conn.execute("""
            SELECT trust FROM relationships
            WHERE save_id = ? AND subject = ? AND object IN ('player', 'Player', 'Moshi')
            ORDER BY updated_at DESC LIMIT 1
        """, (save_id, participant)).fetchone()
        trust = float(rel["trust"]) if rel and rel["trust"] is not None else 0.5
        stance = "support_player" if trust > 0.6 else ("oppose_player" if trust < 0.3 else "neutral")
        stances.append({"settler_id": participant, "trust": trust, "stance": stance})
    # FACTION INTERVENTION (doc 07: "a nearby lord you're allied with
    # intervenes... and joins the defense"): any faction allied to the
    # DEFENDER (or strongly friendly, relation >= 0.4) sends aid.
    interventions = []
    for rel in _rows(conn.execute(
            "SELECT * FROM faction_relations WHERE save_id = ? AND (faction_a = ? OR faction_b = ?)",
            (save_id, defender, defender))):
        other = rel["faction_a"] if rel["faction_b"] == defender else rel["faction_b"]
        if other == aggressor:
            continue
        if rel["state"] == "alliance" or float(rel["relation"] or 0.0) >= 0.4:
            interventions.append({
                "faction": other, "side": "defender",
                "arrival_line": f"Riders of {other} crest the hill — '{defender} does not stand alone this day!'",
            })
    verdict = {
        "aggressor": aggressor,
        "defender": defender,
        "defenders_needed": defenders_needed,
        "defender_type": "militia" if defenders_needed else "none",
        "civilian_panic": civilian_panic,
        "stances": stances,
        "interventions": interventions,
    }
    aftermath = (f"{len(casualties)} casualties at {location or 'the settlement'}; "
                 f"{'militia raised, ' if defenders_needed else ''}"
                 f"{'townsfolk panicked' if civilian_panic else 'calm held'}.")
    conn.execute("""
        INSERT INTO combat_incidents (save_id, trigger_type, aggressor, defender, location,
                                      participants_json, casualties_json, verdict_json, aftermath, created_at)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, (save_id, trigger_type, aggressor, defender, location,
          json.dumps(participants, ensure_ascii=False), json.dumps(casualties, ensure_ascii=False),
          json.dumps(verdict, ensure_ascii=False), aftermath, _now()))
    incident_id = conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]
    event_id = _diplomacy_world_event(
        conn, save_id, "military", f"Violence at {location or 'the settlement'}",
        f"{aggressor} attacked {defender}. {aftermath}", [aggressor, defender])
    for stance in stances:
        ctx.insert_typed_memory(
            conn, save_id, stance["settler_id"], "danger",
            f"Fight broke out: {aggressor} against {defender} at {location or 'the settlement'}. "
            f"I chose to {stance['stance'].replace('_', ' ')}.",
            8, metadata={"combat_incident_id": incident_id, "stance": stance["stance"]},
        )
    for casualty in casualties:
        record_death(ctx, conn, save_id, casualty, cause=f"combat at {location or 'the settlement'}")
    # WORD TRAVELS (scenario 3's aftermath feeding the world): everyone hears.
    settlers = [r["settler_id"] for r in _rows(conn.execute(
        "SELECT settler_id FROM npc_memory_profiles WHERE save_id = ?", (save_id,)))]
    if settlers:
        try:
            propagate_event(ctx, conn, save_id, event_id, settlers, "secondhand")
        except Exception:  # noqa: BLE001
            pass
    # COMBAT FEEDS DIPLOMACY (doc 07: "casualties and the failed raid feed
    # straight back into AI Diplomacy"): when both sides are known factions,
    # the incident IS a raid — relations sour, war stats accrue, escalation
    # to declared war happens past the threshold.
    diplomacy = None
    faction_names = set(known_factions(conn, save_id))
    if aggressor in faction_names and defender in faction_names:
        diplomacy = report_raid(ctx, conn, save_id, aggressor, defender,
                                casualties_target=len(casualties))
    return {"incident_id": incident_id, "verdict": verdict, "aftermath": aftermath,
            "world_event_id": event_id, "diplomacy": diplomacy}

# ---------------------------------------------------------------------------
# Dispatch
# ---------------------------------------------------------------------------

def _q(query, key, default=""):
    return (query.get(key, [default]) or [default])[0]

def dispatch(http, ctx, method, path, query=None, payload=None):
    """Route /api/* paths for the P3+ systems. Returns True when handled."""
    payload = payload or {}
    query = query or {}
    try:
        if method == "GET":
            return _dispatch_get(http, ctx, path, query)
        return _dispatch_post(http, ctx, path, payload)
    except Exception as e:  # noqa: BLE001 - endpoint boundary
        http._send_json(500, {"error": str(e)})
        return True

def _dispatch_get(http, ctx, path, query):
    save_id = _q(query, "save_id")

    if path == "/api/orders":
        conn = ctx.get_db_connection()
        try:
            sql = "SELECT * FROM ai_orders WHERE save_id = ?"
            args = [save_id]
            settler_id = _q(query, "settler_id")
            status = _q(query, "status")
            if settler_id:
                sql += " AND settler_id = ?"
                args.append(settler_id)
            if status:
                sql += " AND status = ?"
                args.append(status)
            sql += " ORDER BY updated_at DESC LIMIT 50"
            orders = _rows(conn.execute(sql, args))
            for order in orders:
                order["steps"] = _loads(order.pop("steps_json"), [])
            http._send_json(200, {"ok": True, "orders": orders})
        finally:
            conn.close()
        return True

    if path == "/api/entities":
        conn = ctx.get_db_connection()
        try:
            entities = _rows(conn.execute("""
                SELECT we.*, COUNT(em.id) AS mentions
                FROM world_entities we
                LEFT JOIN entity_mentions em ON em.entity_id = we.id
                WHERE we.save_id = ?
                GROUP BY we.id ORDER BY we.last_seen DESC LIMIT 100
            """, (save_id,)))
            visits = _rows(conn.execute("""
                SELECT vh.*, we.name AS place, we.kind
                FROM visit_history vh JOIN world_entities we ON we.id = vh.place_entity_id
                WHERE vh.save_id = ? ORDER BY vh.visited_at DESC LIMIT 30
            """, (save_id,)))
            recruits = _rows(conn.execute(
                "SELECT * FROM recruitment_opportunities WHERE save_id = ? ORDER BY created_at DESC LIMIT 20",
                (save_id,)))
            http._send_json(200, {"ok": True, "entities": entities, "visits": visits,
                                  "recruitment": recruits})
        finally:
            conn.close()
        return True

    if path == "/api/events":
        conn = ctx.get_db_connection()
        try:
            events = _rows(conn.execute("""
                SELECT we.*, COUNT(wek.id) AS known_by
                FROM world_events we
                LEFT JOIN world_event_knowledge wek ON wek.event_id = we.id AND wek.save_id = we.save_id
                WHERE we.save_id = ?
                GROUP BY we.id ORDER BY we.updated_at DESC LIMIT 50
            """, (save_id,)))
            for event in events:
                event["affected"] = _loads(event.pop("affected_json"), [])
                event["updates"] = _loads(event.pop("updates_json"), [])
            http._send_json(200, {"ok": True, "events": events})
        finally:
            conn.close()
        return True

    if path == "/api/events/known":
        conn = ctx.get_db_connection()
        try:
            http._send_json(200, {"ok": True, "settler_id": _q(query, "settler_id"),
                                  "events": known_events(conn, save_id, _q(query, "settler_id"))})
        finally:
            conn.close()
        return True

    if path == "/api/diplomacy":
        conn = ctx.get_db_connection()
        try:
            relations = _rows(conn.execute(
                "SELECT * FROM faction_relations WHERE save_id = ? ORDER BY updated_at DESC", (save_id,)))
            for rel in relations:
                rel["tribute"] = _loads(rel.pop("tribute_json"), {})
                rel["stats"] = _loads(rel.pop("stats_json"), {})
            log = _rows(conn.execute(
                "SELECT * FROM diplomacy_log WHERE save_id = ? ORDER BY created_at DESC LIMIT 40", (save_id,)))
            http._send_json(200, {"ok": True, "relations": relations, "log": log})
        finally:
            conn.close()
        return True

    if path == "/api/romance":
        conn = ctx.get_db_connection()
        try:
            states = _rows(conn.execute(
                "SELECT * FROM romance_states WHERE save_id = ? ORDER BY updated_at DESC LIMIT 50", (save_id,)))
            http._send_json(200, {"ok": True, "romance": states,
                                  "initiative": romance_initiative_candidates(conn, save_id)})
        finally:
            conn.close()
        return True

    if path == "/api/death":
        conn = ctx.get_db_connection()
        try:
            http._send_json(200, {"ok": True, "deaths": _rows(conn.execute(
                "SELECT * FROM death_records WHERE save_id = ? ORDER BY died_at DESC LIMIT 30", (save_id,)))})
        finally:
            conn.close()
        return True

    if path == "/api/disease":
        conn = ctx.get_db_connection()
        try:
            http._send_json(200, {"ok": True, "disease": _rows(conn.execute(
                "SELECT * FROM disease_states WHERE save_id = ? ORDER BY updated_at DESC LIMIT 60", (save_id,)))})
        finally:
            conn.close()
        return True

    if path == "/api/construction":
        conn = ctx.get_db_connection()
        try:
            http._send_json(200, {"ok": True, "proposals": _rows(conn.execute(
                "SELECT * FROM construction_proposals WHERE save_id = ? ORDER BY updated_at DESC LIMIT 40",
                (save_id,)))})
        finally:
            conn.close()
        return True

    if path == "/api/combat":
        conn = ctx.get_db_connection()
        try:
            incidents = _rows(conn.execute(
                "SELECT * FROM combat_incidents WHERE save_id = ? ORDER BY created_at DESC LIMIT 30", (save_id,)))
            for incident in incidents:
                incident["participants"] = _loads(incident.pop("participants_json"), [])
                incident["casualties"] = _loads(incident.pop("casualties_json"), [])
                incident["verdict"] = _loads(incident.pop("verdict_json"), {})
            http._send_json(200, {"ok": True, "incidents": incidents})
        finally:
            conn.close()
        return True

    return False

def _dispatch_post(http, ctx, path, payload):
    save_id = payload.get("save_id")

    if path == "/api/orders/issue":
        settler_id = payload.get("settler_id")
        text = (payload.get("text") or "").strip()
        if not save_id or not settler_id or not text:
            http._send_json(400, {"error": "save_id, settler_id and text are required"})
            return True
        steps = parse_order_text(text)
        unsupported = [s for s in steps if s["action"] == "unsupported"]
        status = "needs_review" if unsupported else "queued"
        conn = ctx.get_db_connection()
        try:
            with conn:
                now = _now()
                ctx.upsert_memory_profile(conn, save_id, settler_id)
                conn.execute("""
                    INSERT INTO ai_orders (save_id, settler_id, raw_text, steps_json, status, created_at, updated_at)
                    VALUES (?, ?, ?, ?, ?, ?, ?)
                """, (save_id, settler_id, text, json.dumps(steps, ensure_ascii=False), status, now, now))
                order_id = conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]
                ctx.insert_typed_memory(
                    conn, save_id, settler_id, "decision",
                    f"Received orders: {text}", 6,
                    metadata={"order_id": order_id, "steps": len(steps), "status": status},
                )
            http._send_json(200, {"ok": True, "order_id": order_id, "status": status,
                                  "steps": steps, "unsupported": len(unsupported)})
        finally:
            conn.close()
        return True

    if path == "/api/orders/update":
        order_id = payload.get("order_id")
        new_status = payload.get("status")
        step_index = payload.get("step_index")
        step_status = payload.get("step_status")
        reason = payload.get("reason") or ""
        if not order_id:
            http._send_json(400, {"error": "order_id is required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                order = conn.execute("SELECT * FROM ai_orders WHERE id = ?", (order_id,)).fetchone()
                if not order:
                    http._send_json(404, {"error": "order not found"})
                    return True
                steps = _loads(order["steps_json"], [])
                current = order["current_step"]
                new_settler = payload.get("settler_id")
                if new_settler:
                    conn.execute("UPDATE ai_orders SET settler_id = ? WHERE id = ?",
                                 (new_settler, order_id))
                if step_index is not None and step_status:
                    idx = int(step_index)
                    if 0 <= idx < len(steps):
                        steps[idx]["status"] = step_status
                        if reason:
                            steps[idx]["note"] = reason
                        if step_status == "completed" and idx == current:
                            current = idx + 1
                if new_status is None:
                    if all(s.get("status") == "completed" for s in steps):
                        new_status = "completed"
                    elif any(s.get("status") == "failed" for s in steps):
                        new_status = "failed"
                    else:
                        new_status = "active" if current > 0 else order["status"]
                failure_reason = reason if new_status == "failed" or step_status == "failed" else order["failure_reason"]
                conn.execute("""
                    UPDATE ai_orders SET steps_json = ?, current_step = ?, status = ?,
                           failure_reason = ?, updated_at = ?
                    WHERE id = ?
                """, (json.dumps(steps, ensure_ascii=False), current, new_status,
                      failure_reason or "", _now(), order_id))
            http._send_json(200, {"ok": True, "order_id": order_id, "status": new_status,
                                  "current_step": current, "steps": steps})
        finally:
            conn.close()
        return True

    if path == "/api/entities/mention":
        settler_id = payload.get("settler_id")
        text = payload.get("text") or ""
        if not save_id or not settler_id:
            http._send_json(400, {"error": "save_id and settler_id are required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                recorded = record_mentions(ctx, conn, save_id, settler_id, text,
                                           context=payload.get("context") or text[:200])
                explicit = payload.get("entities") or []
                for item in explicit:
                    if isinstance(item, dict) and item.get("name"):
                        entity_id = ensure_entity(conn, save_id, item.get("kind") or "settler",
                                                  item["name"], item.get("standing"))
                        conn.execute("""
                            INSERT INTO entity_mentions (save_id, settler_id, entity_id, context, created_at)
                            VALUES (?, ?, ?, ?, ?)
                        """, (save_id, settler_id, entity_id, (payload.get("context") or "")[:200], _now()))
                        recorded.append({"entity_id": entity_id, "kind": item.get("kind") or "settler",
                                         "name": item["name"]})
            http._send_json(200, {"ok": True, "recorded": recorded})
        finally:
            conn.close()
        return True

    if path == "/api/entities/visit":
        settler_id = payload.get("settler_id")
        place = payload.get("place")
        kind = payload.get("kind") or "settlement"
        if not save_id or not settler_id or not place:
            http._send_json(400, {"error": "save_id, settler_id and place are required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                entity_id = ensure_entity(conn, save_id, kind, place)
                conn.execute("""
                    INSERT INTO visit_history (save_id, settler_id, place_entity_id, visited_at, details)
                    VALUES (?, ?, ?, ?, ?)
                """, (save_id, settler_id, entity_id, _now(), payload.get("details") or ""))
                ctx.insert_typed_memory(
                    conn, save_id, settler_id, "event",
                    f"Visited {place}. {payload.get('details') or ''}".strip(), 5,
                    metadata={"place": place, "kind": kind},
                )
            http._send_json(200, {"ok": True, "entity_id": entity_id})
        finally:
            conn.close()
        return True

    if path == "/api/entities/recruitment":
        candidate = payload.get("candidate_name")
        if not save_id or not candidate:
            http._send_json(400, {"error": "save_id and candidate_name are required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                result = detect_recruitment(conn, save_id, candidate, payload.get("description") or "")
            http._send_json(200, {"ok": True, "opportunity": result})
        finally:
            conn.close()
        return True

    if path == "/api/events/create":
        event_type = payload.get("event_type")
        title = payload.get("title")
        if not save_id or event_type not in EVENT_TYPES or not title:
            http._send_json(400, {"error": f"save_id, title and event_type in {sorted(EVENT_TYPES)} required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                now = _now()
                conn.execute("""
                    INSERT INTO world_events (save_id, event_type, title, description, origin_entity,
                                              affected_json, confidence, status, created_at, updated_at)
                    VALUES (?, ?, ?, ?, ?, ?, ?, 'active', ?, ?)
                """, (save_id, event_type, title, payload.get("description") or "",
                      payload.get("origin_entity") or "",
                      json.dumps(payload.get("affected_entities") or [], ensure_ascii=False),
                      ctx.numeric_or_default(payload.get("confidence"), 0.8), now, now))
                event_id = conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]
                for name in payload.get("affected_entities") or []:
                    ensure_entity(conn, save_id, "faction" if "faction" in str(name).lower() else "settlement",
                                  str(name))
            http._send_json(200, {"ok": True, "event_id": event_id})
        finally:
            conn.close()
        return True

    if path == "/api/events/propagate":
        event_id = payload.get("event_id")
        settler_ids = payload.get("settler_ids") or []
        if not save_id or not event_id:
            http._send_json(400, {"error": "save_id and event_id are required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                if not settler_ids:
                    settler_ids = [r["settler_id"] for r in conn.execute(
                        "SELECT settler_id FROM npc_memory_profiles WHERE save_id = ?", (save_id,))]
                reached = propagate_event(ctx, conn, save_id, event_id, settler_ids,
                                          payload.get("rumor_state") or "rumor")
                if reached is None:
                    http._send_json(404, {"error": "event not found"})
                    return True
            http._send_json(200, {"ok": True, "reached": reached})
        finally:
            conn.close()
        return True

    if path == "/api/events/update":
        event_id = payload.get("event_id")
        if not save_id or not event_id:
            http._send_json(400, {"error": "save_id and event_id are required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                event = conn.execute(
                    "SELECT * FROM world_events WHERE id = ? AND save_id = ?", (event_id, save_id)
                ).fetchone()
                if not event:
                    http._send_json(404, {"error": "event not found"})
                    return True
                updates = _loads(event["updates_json"], [])
                note = payload.get("note") or ""
                new_status = payload.get("status") or event["status"]
                updates.append({"at": _now(), "status": new_status, "note": note})
                conn.execute("""
                    UPDATE world_events SET status = ?, updates_json = ?,
                           description = COALESCE(NULLIF(?, ''), description), updated_at = ?
                    WHERE id = ?
                """, (new_status, json.dumps(updates, ensure_ascii=False),
                      payload.get("description") or "", _now(), event_id))
            http._send_json(200, {"ok": True, "event_id": event_id, "status": new_status,
                                  "updates": len(updates)})
        finally:
            conn.close()
        return True

    if path == "/api/diplomacy/relation":
        faction_a = payload.get("faction_a")
        faction_b = payload.get("faction_b")
        action = payload.get("action")
        if not save_id or not faction_a or not faction_b or not action:
            http._send_json(400, {"error": "save_id, faction_a, faction_b and action are required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                result = apply_diplomacy_action(ctx, conn, save_id, faction_a, faction_b,
                                                action, payload.get("terms"))
            http._send_json(200 if result.get("ok") else 400, result)
        finally:
            conn.close()
        return True

    if path == "/api/events/evolve":
        if not save_id:
            http._send_json(400, {"error": "save_id is required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                transitions = events_evolve(ctx, conn, save_id, payload.get("now"))
            http._send_json(200, {"ok": True, "transitions": transitions})
        finally:
            conn.close()
        return True

    if path == "/api/diplomacy/seed":
        # FACTION ROSTER FEED (Chronicle Test Gate 2): the mod posts the game's
        # REAL factions + player friendliness (0..100). Seeds entities and the
        # player↔faction relations so agent rounds have actual players.
        player = payload.get("player_faction")
        factions = payload.get("factions") or []
        if not save_id or not player or not factions:
            http._send_json(400, {"error": "save_id, player_faction and factions are required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                ensure_entity(conn, save_id, "faction", player)
                seeded = []
                for f in factions:
                    name = (f.get("name") or "").strip()
                    if not name:
                        continue
                    ensure_entity(conn, save_id, "faction", name)
                    rel = get_relation(conn, save_id, player, name)
                    # game friendliness 0..100 → relation -1..1 (50 = neutral)
                    relation = max(-1.0, min(1.0, (float(f.get("friendliness", 50)) - 50.0) / 50.0))
                    state = "war" if relation <= -0.6 else (
                        "alliance" if relation >= 0.9 else "peace")
                    conn.execute("UPDATE faction_relations SET relation=?, state=?, updated_at=? WHERE id=?",
                                 (relation, state, _now(), rel["id"]))
                    seeded.append({"name": name, "relation": round(relation, 2), "state": state})
                _log_diplomacy(conn, save_id, "world", "roster_seeded", player,
                               f"The known powers of the region: {', '.join(s['name'] for s in seeded)}.")
            http._send_json(200, {"ok": True, "seeded": seeded})
        finally:
            conn.close()
        return True

    if path == "/api/diplomacy/raid":
        # GROUND-TRUTH FEED from the game: a real raid happened. Worsens
        # relations, records losses, escalates to declared war past threshold.
        raider = payload.get("raider")
        target = payload.get("target")
        if not save_id or not raider or not target:
            http._send_json(400, {"error": "save_id, raider and target are required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                result = report_raid(ctx, conn, save_id, raider, target,
                                     int(payload.get("casualties_raider") or 0),
                                     int(payload.get("casualties_target") or 0))
            http._send_json(200, result)
        finally:
            conn.close()
        return True

    if path == "/api/diplomacy/round":
        if not save_id:
            http._send_json(400, {"error": "save_id is required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                result = run_diplomacy_round(ctx, conn, save_id)
            http._send_json(200, {"ok": True, **result})
        finally:
            conn.close()
        return True

    if path == "/api/romance/interact":
        conn = ctx.get_db_connection()
        try:
            with conn:
                result = romance_interact(
                    ctx, conn, save_id, payload.get("settler_id"), payload.get("partner_id"),
                    payload.get("interaction"), payload.get("tradition") or "")
            http._send_json(200 if result.get("ok") else 400, result)
        finally:
            conn.close()
        return True

    if path == "/api/romance/tick":
        # Full autonomous romance pass: decay + bond formation + milestones.
        if not save_id:
            http._send_json(400, {"error": "save_id is required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                result = romance_autonomous_tick(ctx, conn, save_id)
            http._send_json(200, {"ok": True, **result})
        finally:
            conn.close()
        return True

    if path == "/api/romance/decay":
        conn = ctx.get_db_connection()
        try:
            with conn:
                decayed = romance_decay(ctx, conn, save_id, payload.get("now"))
            http._send_json(200, {"ok": True, "decayed": decayed})
        finally:
            conn.close()
        return True

    if path == "/api/death/record":
        settler_id = payload.get("settler_id")
        if not save_id or not settler_id:
            http._send_json(400, {"error": "save_id and settler_id are required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                result = record_death(ctx, conn, save_id, settler_id, payload.get("cause") or "")
            http._send_json(200, {"ok": True, **result})
        finally:
            conn.close()
        return True

    if path == "/api/death/history":
        conn = ctx.get_db_connection()
        try:
            with conn:
                result = generate_death_history(
                    ctx, conn, save_id, payload.get("settler_id"),
                    payload.get("death_record_id"), payload.get("accept", True))
            http._send_json(200 if result.get("ok") else 400, result)
        finally:
            conn.close()
        return True

    if path == "/api/disease/infect":
        conn = ctx.get_db_connection()
        try:
            with conn:
                result = infect(ctx, conn, save_id, payload.get("settler_id"),
                                payload.get("disease"), payload.get("source") or "",
                                payload.get("season") or "winter")
            http._send_json(200 if result.get("ok") else 400, result)
        finally:
            conn.close()
        return True

    if path == "/api/disease/tick":
        conn = ctx.get_db_connection()
        try:
            with conn:
                season = (payload.get("season") or "winter").lower()
                changes = disease_tick(ctx, conn, save_id)
                spread = disease_spread(ctx, conn, save_id, season)
                onsets = seasonal_onset(ctx, conn, save_id, season)
            http._send_json(200, {"ok": True, "changes": changes,
                                  "spread": spread, "onsets": onsets, "season": season})
        finally:
            conn.close()
        return True

    if path == "/api/disease/treat":
        settler_id = payload.get("settler_id")
        conn = ctx.get_db_connection()
        try:
            with conn:
                cur = conn.execute("""
                    UPDATE disease_states SET quarantined = ?, treated = ?, updated_at = ?
                    WHERE save_id = ? AND settler_id = ? AND stage IN ('incubating','sick','critical')
                """, (1 if payload.get("quarantine") else 0, 1 if payload.get("treated") else 0,
                      _now(), save_id, settler_id))
            http._send_json(200, {"ok": True, "updated": cur.rowcount})
        finally:
            conn.close()
        return True

    if path == "/api/construction/plan":
        # Colony planner v1: deterministic needs -> proposals (+orders when
        # auto_approve). Reads colony_events + settler pressures + profiles;
        # assigns work by role affinity. Idempotent per building type while a
        # matching proposal is still open.
        if not save_id:
            http._send_json(400, {"error": "save_id is required"})
            return True
        auto_approve = bool(payload.get("auto_approve"))
        conn = ctx.get_db_connection()
        try:
            with conn:
                colony = conn.execute(
                    "SELECT * FROM colony_events WHERE save_id = ? ORDER BY timestamp DESC LIMIT 1",
                    (save_id,)).fetchone()
                # Only settlers actually seen in the last 15 minutes are
                # assignable — stale profiles caused "not present" failures.
                profiles = _rows(conn.execute("""
                    SELECT settler_id, display_name, role, last_seen FROM npc_memory_profiles
                    WHERE save_id = ? AND settler_id LIKE 'gm_%' AND last_seen > ?
                    ORDER BY last_seen DESC
                """, (save_id, _now() - 900)))
                if not profiles:
                    http._send_json(409, {"error": "no canonical settler profiles for this save"})
                    return True

                def worker_for(*roles):
                    for role in roles:
                        for p in profiles:
                            if role.lower() in str(p.get("role") or "").lower():
                                return p["settler_id"]
                    return profiles[0]["settler_id"]

                open_buildings = {r["building"].lower() for r in conn.execute(
                    "SELECT building FROM construction_proposals WHERE save_id = ? AND status IN ('proposed','approved','placed')",
                    (save_id,))}

                avg_hunger = float(colony["avg_hunger"]) if colony and colony["avg_hunger"] is not None else 0.5
                avg_mood = float(colony["avg_mood"]) if colony and colony["avg_mood"] is not None else 0.5
                threat = float(colony["threat_level"]) if colony and colony["threat_level"] is not None else 0.0

                wants = []
                if avg_hunger >= 0.3:
                    wants.append(("farm plot", worker_for("Farmer", "Cook"),
                                  f"Average hunger {avg_hunger:.2f}; the colony needs food production.", 0.9))
                    wants.append(("food stockpile", worker_for("Cook", "Steward"),
                                  "Harvested food needs a dedicated stockpile before it rots in the open.", 0.8))
                wants.append(("stockpile zone", worker_for("Steward", "Builder"),
                              "General goods are lying in the open; a stockpile zone keeps them dry.", 0.7))
                wants.append(("research table", worker_for("Scholar"),
                              "No research table; progress is stalled without study.", 0.75))
                wants.append(("wooden bed", worker_for("Builder", "Carpenter"),
                              "Settlers need proper beds under a roof; sleeping rough wrecks mood.", 0.7 if avg_mood < 0.6 else 0.5))
                wants.append(("mine shaft", worker_for("Miner", "Builder"),
                              "Stone and ore reserves are thin; designate mining.", 0.6))
                if threat >= 0.3:
                    wants.append(("palisade section", worker_for("Guard", "Builder"),
                                  f"Threat level {threat:.2f}; the perimeter has gaps.", 0.85))

                created = []
                now = _now()
                for building, settler_id, reason, urgency in wants:
                    if building.lower() in open_buildings:
                        continue
                    conn.execute("""
                        INSERT INTO construction_proposals
                        (save_id, settler_id, building, reason, urgency, created_at, updated_at)
                        VALUES (?, ?, ?, ?, ?, ?, ?)
                    """, (save_id, settler_id, building, reason, urgency, now, now))
                    proposal_id = conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]
                    ctx.insert_typed_memory(
                        conn, save_id, settler_id, "decision",
                        f"Planned to build a {building}. {reason}", 6,
                        metadata={"construction_proposal_id": proposal_id, "planner": True},
                    )
                    order_id = None
                    if auto_approve:
                        if "stockpile" in building.lower():
                            steps = [{"action": "place_stockpile", "building": building, "status": "pending"}]
                        else:
                            steps = [
                                {"action": "prioritize_construction", "building": building, "status": "pending"},
                                {"action": "build_special", "building": building, "status": "pending"},
                            ]
                        conn.execute("""
                            INSERT INTO ai_orders (save_id, settler_id, raw_text, steps_json, status, created_at, updated_at)
                            VALUES (?, ?, ?, ?, 'queued', ?, ?)
                        """, (save_id, settler_id, f"[planner] build {building}",
                              json.dumps(steps, ensure_ascii=False), now, now))
                        order_id = conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]
                        conn.execute(
                            "UPDATE construction_proposals SET status = 'approved', order_id = ? WHERE id = ?",
                            (order_id, proposal_id))
                    created.append({"proposal_id": proposal_id, "building": building,
                                    "settler_id": settler_id, "urgency": urgency,
                                    "order_id": order_id})

                # food gathering + mining as direct work orders
                work_orders = []
                if auto_approve and avg_hunger >= 0.3:
                    for text, sid in (("Prioritize harvest, then return to work", worker_for("Farmer", "Cook")),
                                      ("Prioritize mining, then return to work", worker_for("Miner", "Builder"))):
                        steps = parse_order_text(text)
                        for s in steps:
                            s["status"] = "pending"
                        conn.execute("""
                            INSERT INTO ai_orders (save_id, settler_id, raw_text, steps_json, status, created_at, updated_at)
                            VALUES (?, ?, ?, ?, 'queued', ?, ?)
                        """, (save_id, sid, f"[planner] {text}",
                              json.dumps(steps, ensure_ascii=False), now, now))
                        work_orders.append({"order_id": conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"],
                                            "settler_id": sid, "text": text})
            http._send_json(200, {"ok": True, "proposals": created, "work_orders": work_orders,
                                  "colony_snapshot": {"avg_hunger": avg_hunger, "avg_mood": avg_mood, "threat": threat}})
        finally:
            conn.close()
        return True

    if path == "/api/construction/propose":
        settler_id = payload.get("settler_id")
        building = (payload.get("building") or "").strip()
        if not save_id or not settler_id or not building:
            http._send_json(400, {"error": "save_id, settler_id and building are required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                now = _now()
                conn.execute("""
                    INSERT INTO construction_proposals
                    (save_id, settler_id, building, reason, urgency, created_at, updated_at)
                    VALUES (?, ?, ?, ?, ?, ?, ?)
                """, (save_id, settler_id, building, payload.get("reason") or "",
                      ctx.numeric_or_default(payload.get("urgency"), 0.5), now, now))
                proposal_id = conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]
                ctx.insert_typed_memory(
                    conn, save_id, settler_id, "decision",
                    f"Proposed building a {building}. {payload.get('reason') or ''}".strip(), 6,
                    metadata={"construction_proposal_id": proposal_id},
                )
            http._send_json(200, {"ok": True, "proposal_id": proposal_id})
        finally:
            conn.close()
        return True

    if path == "/api/construction/update":
        proposal_id = payload.get("proposal_id")
        new_status = str(payload.get("status") or "").strip()
        if not proposal_id or new_status not in {"approved", "rejected", "placed", "built"}:
            http._send_json(400, {"error": "proposal_id and status in approved/rejected/placed/built required"})
            return True
        conn = ctx.get_db_connection()
        try:
            with conn:
                prop = conn.execute(
                    "SELECT * FROM construction_proposals WHERE id = ?", (proposal_id,)
                ).fetchone()
                if not prop:
                    http._send_json(404, {"error": "proposal not found"})
                    return True
                order_id = prop["order_id"]
                if new_status == "approved" and not order_id:
                    # Phase B2/B3: approval enqueues a real order for the
                    # executor. Stockpile-type buildings use the direct
                    # placement verb; everything else the build proposal path.
                    if "stockpile" in prop["building"].lower():
                        steps = [
                            {"action": "place_stockpile", "building": prop["building"], "status": "pending"},
                        ]
                    else:
                        steps = [
                            {"action": "prioritize_construction", "building": prop["building"], "status": "pending"},
                            {"action": "build_special", "building": prop["building"], "status": "pending"},
                        ]
                    now = _now()
                    conn.execute("""
                        INSERT INTO ai_orders (save_id, settler_id, raw_text, steps_json, status, created_at, updated_at)
                        VALUES (?, ?, ?, ?, 'queued', ?, ?)
                    """, (prop["save_id"], prop["settler_id"],
                          f"[construction] build {prop['building']}",
                          json.dumps(steps, ensure_ascii=False), now, now))
                    order_id = conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"]
                conn.execute("""
                    UPDATE construction_proposals SET status = ?, order_id = ?, updated_at = ?
                    WHERE id = ?
                """, (new_status, order_id, _now(), proposal_id))
            http._send_json(200, {"ok": True, "proposal_id": proposal_id,
                                  "status": new_status, "order_id": order_id})
        finally:
            conn.close()
        return True

    if path == "/api/combat/incident":
        conn = ctx.get_db_connection()
        try:
            with conn:
                result = classify_combat_incident(
                    ctx, conn, save_id, payload.get("trigger_type") or "player_attack",
                    payload.get("aggressor") or "unknown", payload.get("defender") or "unknown",
                    payload.get("location") or "", payload.get("participants"),
                    payload.get("casualties"))
            http._send_json(200, {"ok": True, **result})
        finally:
            conn.close()
        return True

    return False
