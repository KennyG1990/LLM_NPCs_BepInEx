# JSON Memory Migration — SPECIFIED (2026-07-13)

**Directive (Ken):** "document the next work with the player2 database, so we can
officially move to json memory." Miliardo's tip: Player2 added an OpenAI-compatible
`/v1/embeddings` endpoint; Ken has already moved his X4 mod to JSON memory ("json
memory over sql3, no http bridge required"). This doc is the workflow DOCUMENT step
for doing the same in the Going Medieval mod. **SPECIFIED — not started.**

## RECONCILE — what exists today

**Current architecture (verified in code):**
- Mod `MemoryManager.cs` writes memory over HTTP to the dashboard:
  `POST /api/memory/{event,npc,pressures,incident,relationship}` and reads context
  via `GET /api/memory/context`.
- Dashboard `dashboard_server.py` persists to **`npc_memory.sqlite3`** (SQLite):
  `typed_memories`, `npc_memory_profiles`, `world_events`, `world_event_knowledge`,
  `relationships`, `diplomacy_log`, `faction_relations`, etc.
- Retrieval today is **keyword**: `gm_rolerag.py` (entity-link + `LIKE`) +
  `build_dialogue_prompt_context` (also injects `known_events` rumors).
- **Player2 surface (probed 2026-07-13):** `/v1/models` (200) + `/v1/embeddings`
  (200, `text-embedding-3-small`, 1536 dims). **No** hosted DB — `/v1/memory`,
  `/v1/storage`, `/v1/collections` all 404. So "player2 database" = OUR store,
  powered by Player2 embeddings; there is nothing Player2-hosted to migrate onto.
- Already built this session: `dashboard/gm_embeddings.py` (embed/cosine/rank,
  base64 f32, None-on-failure fallback), validated against the live endpoint.

**The load-bearing coupling (the crux of this migration):** the LIVING-WORLD layer
(diplomacy, disease, romance, combat, world events) is **dashboard/Python-side** and
writes rumors into `typed_memories` via `propagate_event`. Gate 2/3 depend on those
rumors reaching a settler's DIALOGUE context. Any move of memory to mod-local JSON
MUST keep that rumor delivery path intact — this is the main risk, not the storage
swap itself.

## TARGET — mod-local JSON memory + embedding retrieval

- **Store:** `%APPDATA%/Going Medieval/LLM_NPCs/memory_json/<save_id>/<settler_id>.json`
  — one file per settler, a JSON array of typed-memory objects mirroring the
  `typed_memories` shape: `{category, tier, event_type, content, importance,
  confidence, created_at, metadata, embedding?}`. Atomic writes (temp + rename).
- **Ingestion:** on memory add, compute the embedding via Player2 `/v1/embeddings`
  and cache the vector inline (base64 f32). Lazy + batched; a failed/slow embed
  stores the memory WITHOUT a vector and back-fills later (never blocks gameplay).
- **Retrieval:** embed the query, cosine-rank the settler's cached vectors, return
  top-K; fall back to keyword/entity (the existing `gm_rolerag` boundary rule for
  proper nouns) when embeddings are unavailable. Enhancement over a floor, never a
  hard dependency (mod law).
- **"No HTTP bridge":** memory read/write becomes in-process (C# reads/writes JSON
  directly) — no dashboard round-trip for memory. Player2 `/v1/embeddings` calls
  remain (that's a direct LLM-daemon call, same class as chat; not a dashboard
  bridge). Net effect: memory survives with the dashboard OFF.

## THE RUMOR COUPLING — three options (decision required before build)

The dashboard's diplomacy/events still generate world events + rumors. Mod-local
JSON memory must still receive them. Options:
1. **Thin sync (recommended):** keep the living-world layer dashboard-side; the mod
   POLLS `known_events`/new `world_event_knowledge` and MERGES fresh rumors into the
   settler's JSON file (as `event`-category memories, embedded on merge). One
   read-only pull; dashboard stays the world-sim authority. Smallest change; keeps
   30+ green dashboard tests valid.
2. **Move living-world into the mod:** port diplomacy/events to C#. Large; discards
   the tested Python substrate. Not now.
3. **Hybrid push:** dashboard pushes rumors to the mod's JSON via a local write.
   Couples the two processes to a shared path — fragile. Avoid.
**Recommendation: Option 1** — it satisfies "no http bridge for MEMORY WRITES/READS"
(the dialogue path goes local) while keeping the one cheap read-pull for world rumors,
and preserves the validated living-world layer whole.

## PHASED PLAN

- **P0 (done):** `gm_embeddings.py` core + validation. `dashboard/gm_embeddings.py`.
- **P1 — dashboard-side semantic retrieval (safe, reversible):** wire `gm_embeddings`
  into `build_dialogue_prompt_context`/`/api/memory/context` to RANK `typed_memories`
  by cosine (keyword fallback). Proves embedding retrieval end-to-end WITHOUT moving
  storage. Needs a dashboard restart (defer past any live Gate run).
- **P2 — mod JSON store (parallel-write):** `MemoryManager` writes memory to JSON
  files IN ADDITION to the HTTP posts (dual-write, no reader change). Verify files
  match SQLite. Reversible (delete the JSON dir).
- **P3 — mod-local read + embed:** `MemoryManager` reads dialogue context from JSON
  (embedding-ranked, keyword fallback), stops calling `/api/memory/context`. HTTP
  memory-writes remain for the living-world layer's benefit until P5.
- **P4 — rumor sync (Option 1):** mod pulls world-event rumors into JSON. Gate 2/3
  transcripts now source from JSON.
- **P5 — cut the bridge:** drop the mod's `/api/memory/*` writes; SQLite becomes
  world-sim-only (diplomacy/events), memory is JSON. One-time export script
  (`sqlite typed_memories -> per-settler JSON`) for existing saves, or start fresh
  (memory is per-save and regenerates).

## ACCEPTANCE / RISKS / ROLLBACK

- **Acceptance:** a settler's dialogue context is built from JSON with embedding
  ranking; a known rumor still surfaces (Gate 2 path holds); memory survives with
  the dashboard stopped; embedder-down falls back to keyword with no dialogue break;
  no per-memory gameplay stall (embeds are async/batched).
- **Risks:** (1) the rumor coupling (mitigated by Option 1 + a P-level gate);
  (2) embed latency on the hot path (mitigated: lazy/batched, store-without-vector);
  (3) JSON file growth per long save (mitigated: tier caps, same as `typed_memories`
  tiers); (4) losing the tested Python retrieval (mitigated: keep it as the P1-P3
  fallback, cut only at P5).
- **Rollback:** every phase is additive/parallel until P5; revert = stop reading JSON
  and delete the dir. P5 keeps the SQLite export as the restore point.

## NOT IN SCOPE / OPEN DECISIONS FOR KEN
- Rumor-coupling option (recommend Option 1).
- Migrate existing saves' memory vs start-fresh at P5.
- Embedding model pin (`text-embedding-3-small` default) vs `-large` (3072 dims) —
  small is cheaper and enough for topical recall (validated this session).
