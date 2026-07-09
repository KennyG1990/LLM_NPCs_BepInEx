"""
GM RoleRAG — graph-guided, boundary-aware retrieval for settler minds.
Port of the PROVEN x4_neural_link/bridge/rolerag.py onto the GM schema,
implementing arXiv 2505.18541 §3.4 (analyze -> three-route retrieve) with the
x4 improvement Ken's crew added: LOCAL FACTS (the settler's own name, role,
spouse, colony) OUTRANK the out-of-scope refusal guard.

Replaces the old 'RoleRAG' in /api/memory/context, which was keyword-overlap
top-3 + a hardcoded role->lore dict (assessed 2026-07-08: no entities, no
graph, no boundaries — the paper's name without its mechanism).

Entity graph substrate (already in our SQLite):
  npcs(settler_id, save_id, name, role...)          -> PERSON nodes
  relationships(subject, object, trust/fear/... )   -> edges w/ strengths
  typed_memories / permanent_memories               -> episodic knowledge
  lore(kind='encyclopedia', key, title, text)       -> world knowledge
Boundary rule: a proper noun that matches NO entity the settler could know
produces a REFUSAL line — the anti-hallucination core of the paper.
"""
import re
import sqlite3


# ── Entity index (paper §3.1-3.3, deterministic flavor) ──────────────────────

class EntityIndex:
    def __init__(self, conn, save_id):
        self.entities = {}          # lower name -> {name, type, key}
        self._build(conn, save_id)

    def _add(self, name, etype, key):
        nm = (name or "").strip()
        if nm and nm.lower() not in self.entities:
            self.entities[nm.lower()] = {"name": nm, "type": etype, "key": key}

    def _build(self, conn, save_id):
        try:
            for r in conn.execute("SELECT settler_id, name FROM npcs WHERE save_id = ?", (save_id,)):
                self._add(r["name"], "settler", f"settler:{r['settler_id']}")
                # first name alone is how villagers actually speak
                first = (r["name"] or "").split(" ")[0]
                if first and first.lower() not in self.entities:
                    self._add(first, "settler", f"settler:{r['settler_id']}")
        except Exception:
            pass
        try:
            for r in conn.execute("SELECT DISTINCT key, title FROM lore WHERE kind = 'encyclopedia'"):
                self._add(r["title"], "lore", f"lore:{r['key']}")
                self._add(r["key"], "lore", f"lore:{r['key']}")
        except Exception:
            pass

    def match(self, message):
        """Word-bounded deterministic matches (paper: entity linking without spend)."""
        out, seen = [], set()
        msg = (message or "").lower()
        for lname, e in self.entities.items():
            if e["key"] in seen:
                continue
            if re.search(r"(?<![a-z0-9])" + re.escape(lname) + r"(?![a-z0-9])", msg):
                out.append(e)
                seen.add(e["key"])
        return out

    def unknown_proper_nouns(self, message):
        """Capitalized tokens matching NO known entity -> boundary candidates."""
        out = []
        for m in re.finditer(r"\b[A-Z][a-z]{2,}\b", message or ""):
            w = m.group(0)
            if w.lower() not in self.entities and w not in out and \
               w not in ("The", "What", "Where", "When", "Who", "How", "Why", "You", "Your"):
                out.append(w)
        return out


# ── Analyze (paper §3.4 step 1) + retrieve (step 2, three routes) ────────────

def analyze(index, message, self_name, self_role):
    res = {"specific": [], "general": [], "out_of_scope": []}
    # LOCAL FACTS first (x4 improvement): yourself is always in scope.
    for e in index.match(message):
        item = dict(e)
        if self_name and e["name"].lower() in (self_name.lower(), self_name.split(" ")[0].lower()):
            item["local"] = True
        (res["specific"] if e["type"] == "settler" else res["general"]).append(item)
    for w in index.unknown_proper_nouns(message):
        res["out_of_scope"].append(w)
    return res


def retrieve(conn, save_id, self_id, self_name, analysis, message, k=4):
    context, boundary = [], []
    for it in analysis["specific"]:
        key = it["key"]
        if it.get("local"):
            context.append(f"{it['name']} is YOU — answer from your own life, first person.")
            continue
        if key.startswith("settler:"):
            other = key.split(":", 1)[1]
            # relationship edge (the graph): typed strengths + summary
            try:
                r = conn.execute(
                    "SELECT * FROM relationships WHERE save_id=? AND subject=? AND object=?",
                    (save_id, self_id, other)).fetchone()
                if r:
                    rel = dict(r)
                    context.append(
                        f"Your relationship with {it['name']}: {rel.get('relationship_type','strangers')}"
                        f"{' (married)' if rel.get('is_married') else ''}; trust {float(rel.get('trust') or 0):.0%},"
                        f" fear {float(rel.get('fear') or 0):.0%}, resentment {float(rel.get('resentment') or 0):.0%}."
                        + (f" {rel['summary']}" if rel.get("summary") else ""))
            except Exception:
                pass
            # episodic memories mentioning them (graph-anchored, not global keyword soup)
            try:
                like = f"%{it['name'].split(' ')[0]}%"
                for m in conn.execute(
                        "SELECT content FROM permanent_memories WHERE save_id=? AND npc_id=? AND content LIKE ? "
                        "ORDER BY importance DESC, timestamp DESC LIMIT ?",
                        (save_id, self_id, like, k)):
                    context.append(f"You remember: {m['content']}")
            except Exception:
                pass
    for it in analysis["general"]:
        if it["key"].startswith("lore:"):
            try:
                r = conn.execute("SELECT title, text FROM lore WHERE kind='encyclopedia' AND key=?",
                                 (it["key"].split(":", 1)[1],)).fetchone()
                if r:
                    context.append(f"[{r['title']}] {r['text']}")
            except Exception:
                pass
    for w in analysis["out_of_scope"]:
        boundary.append(
            f"You have never heard of '{w}'. If asked, say so plainly — do NOT invent details about it.")
    return {"context": context, "boundary": boundary}


def build_context_block(conn, save_id, self_id, message, max_items=10):
    """One-call interface for /api/memory/context. Returns prompt-ready text."""
    try:
        me = conn.execute("SELECT name, role FROM npcs WHERE save_id=? AND settler_id=?",
                          (save_id, self_id)).fetchone()
        self_name = me["name"] if me else ""
        self_role = (me["role"] if me else "") or ""
        idx = EntityIndex(conn, save_id)
        res = retrieve(conn, save_id, self_id, self_name,
                       analyze(idx, message or "", self_name, self_role), message or "")
        lines = []
        if res["context"]:
            lines.append("=== WHAT YOU KNOW (graph-retrieved, in your cognitive scope) ===")
            lines.extend(res["context"][:max_items])
        if res["boundary"]:
            lines.append("=== KNOWLEDGE BOUNDARIES ===")
            lines.extend(res["boundary"][:5])
        return "\n".join(lines)
    except Exception as e:
        return f"(rolerag error: {e})"


# ── Selftest (house rule: every engine ships with its oracle) ────────────────

def run_selftest():
    conn = sqlite3.connect(":memory:")
    conn.row_factory = sqlite3.Row
    conn.execute("CREATE TABLE npcs (settler_id TEXT, save_id TEXT, name TEXT, role TEXT)")
    conn.execute("""CREATE TABLE relationships (save_id TEXT, subject TEXT, object TEXT,
        trust REAL, fear REAL, resentment REAL, relationship_type TEXT, is_married INTEGER, summary TEXT)""")
    conn.execute("CREATE TABLE permanent_memories (save_id TEXT, npc_id TEXT, content TEXT, importance INT, timestamp REAL)")
    conn.execute("CREATE TABLE lore (kind TEXT, key TEXT, title TEXT, text TEXT)")
    conn.execute("INSERT INTO npcs VALUES ('s1','sv','Eve Oldham','miner'), ('s2','sv','Godser Witcomb','artist')")
    conn.execute("INSERT INTO relationships VALUES ('sv','s1','s2',0.8,0.0,0.1,'friends',0,'You dig together.')")
    conn.execute("INSERT INTO permanent_memories VALUES ('sv','s1','Godser saved you from a wolf.',9,1.0)")
    conn.execute("INSERT INTO lore VALUES ('encyclopedia','mining','Mining','Dig ore below ground.')")
    checks = {}
    blk = build_context_block(conn, "sv", "s1", "What do you think of Godser? Tell me about mining and Zanzibar.")
    checks["relationship_edge"] = "friends" in blk and "80%" in blk
    checks["graph_memory"] = "saved you from a wolf" in blk
    checks["lore_route"] = "Dig ore" in blk
    checks["boundary_refusal"] = "Zanzibar" in blk and "never heard" in blk
    checks["self_local"] = "is YOU" in build_context_block(conn, "sv", "s1", "Eve, are you well?")
    return {"ok": all(checks.values()), "checks": checks}


if __name__ == "__main__":
    print(run_selftest())
