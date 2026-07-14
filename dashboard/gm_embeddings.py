"""gm_embeddings — semantic retrieval via Player2's OpenAI-compatible embeddings
endpoint (added to the Player2 API 2026-07-13; Miliardo's tip). Upgrades RoleRAG /
memory retrieval from entity-link + LIKE (keyword) to cosine similarity over
vectors, so a settler recalls a RELEVANT memory even when the wording differs.

DISCIPLINE (mod laws): the endpoint is best-effort. Every entry point degrades to
the existing deterministic retrieval when embeddings are unavailable (daemon down,
timeout, dims mismatch) — semantic search is an ENHANCEMENT layered over the
keyword floor, never a hard dependency. No settler dialogue may break because the
embedder blinked.

Endpoint: POST http://127.0.0.1:4315/v1/embeddings  (base64 float32 for cheap
large batches). model=None uses the launcher-selected model (text-embedding-3-small,
1536 dims, at time of writing)."""

import base64
import json
import math
import struct
import urllib.request

EMBED_URL = "http://127.0.0.1:4315/v1/embeddings"
_TIMEOUT = 8
_MAX_BATCH = 100          # endpoint limit: 100 inputs/request


def available(timeout=3):
    """Cheap liveness probe — one tiny embed. Callers gate on this so a dead
    daemon costs one fast failure, not a per-memory stall."""
    return embed(["ping"], timeout=timeout) is not None


def embed(texts, model=None, dimensions=None, timeout=_TIMEOUT):
    """Return a list of float-vectors (one per input) or None on ANY failure.
    None is the fallback signal — callers drop to deterministic retrieval.
    Uses base64 transport (little-endian f32) — smaller/faster to parse."""
    if not texts:
        return []
    vectors = []
    for start in range(0, len(texts), _MAX_BATCH):
        chunk = texts[start:start + _MAX_BATCH]
        body = {"input": chunk, "encoding_format": "base64"}
        if model:
            body["model"] = model
        if dimensions:
            body["dimensions"] = dimensions
        try:
            req = urllib.request.Request(
                EMBED_URL, data=json.dumps(body).encode("utf-8"),
                headers={"Content-Type": "application/json"}, method="POST")
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                payload = json.loads(resp.read().decode("utf-8"))
            rows = sorted(payload["data"], key=lambda d: d["index"])
            for d in rows:
                vectors.append(_decode(d["embedding"]))
        except Exception:  # noqa: BLE001 - any failure => fallback signal
            return None
    return vectors


def _decode(emb):
    """A vector arrives as base64 (float32 LE bytes) or a plain number array."""
    if isinstance(emb, str):
        raw = base64.b64decode(emb)
        return list(struct.unpack("<%df" % (len(raw) // 4), raw))
    return [float(x) for x in emb]


def cosine(a, b):
    if not a or not b or len(a) != len(b):
        return -1.0
    dot = s1 = s2 = 0.0
    for x, y in zip(a, b):
        dot += x * y
        s1 += x * x
        s2 += y * y
    if s1 == 0.0 or s2 == 0.0:
        return -1.0
    return dot / (math.sqrt(s1) * math.sqrt(s2))


def rank(query_vec, candidate_vecs, top_k=None, min_score=None):
    """Return [(index, score), ...] sorted best-first. Candidates that are None
    (un-embeddable) are skipped, not ranked."""
    scored = [(i, cosine(query_vec, v)) for i, v in enumerate(candidate_vecs) if v]
    scored.sort(key=lambda t: t[1], reverse=True)
    if min_score is not None:
        scored = [s for s in scored if s[1] >= min_score]
    return scored[:top_k] if top_k else scored


def semantic_pick(query, candidate_texts, top_k=6, min_score=0.20):
    """One-shot convenience: embed the query + candidates together, return the
    top-K candidate INDICES by similarity. Returns None if embeddings are
    unavailable (caller falls back to keyword retrieval). One request: query
    prepended, so ranking is self-consistent within the same model call."""
    if not candidate_texts:
        return []
    vecs = embed([query] + list(candidate_texts))
    if vecs is None or len(vecs) != len(candidate_texts) + 1:
        return None
    qv, cvs = vecs[0], vecs[1:]
    return [i for i, _ in rank(qv, cvs, top_k=top_k, min_score=min_score)]
