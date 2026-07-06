# P1 NPC Memory System Release Candidate

Last verified: 2026-07-05 23:10 ET

Priority document: `05 - NPC Memory System.md`

## Scope

P1 turns the previous generic memory log into a first-class NPC personal-file system.

Implemented:

- Durable `npc_memory_profiles` table keyed by `(save_id, settler_id)`.
- Durable `typed_memories` table keyed to save, settler, category, tier, event type, source, timestamps, and metadata.
- Typed memory categories:
  - `conversations`
  - `secrets`
  - `events`
  - `promises`
  - `betrayals`
  - `favors`
  - `deaths`
  - `relationship_milestones`
  - `decisions`
  - `health`
  - `mood`
  - `danger`
  - `colony`
  - `system`
- Automatic classification from existing C# event types and content text.
- Profile summary evolution whenever typed memories are added.
- Backward-compatible mirroring into legacy `facts`, `memories`, and `permanent_memories`.
- Prompt-context retrieval now includes:
  - `=== PERSONAL FILE ===`
  - permanent typed memories
  - recent typed memories
  - major typed memories
  - typed-memory RAG hits
  - RoleRAG lore
- Dashboard memory tab now shows:
  - personal file
  - memory and secret counts
  - category counts
  - typed personal memory ledger
  - existing permanent/recent legacy lists
- Save switching now clears stale selected settler state so memory panels cannot show data from the previous save.

## Release Candidate Evidence

Automated gates:

```text
python -m py_compile dashboard\dashboard_server.py
PASS

node --check dashboard\app.js
PASS

python tools\memory_p1_selftest.py
memory_p1_selftest: PASS

python tools\player2_command_parser_selftest.py
player2_command_parser_selftest: PASS

dotnet build --configuration Release -t:Rebuild
Build succeeded. 0 Warning(s), 0 Error(s)
```

Live backend:

```text
GET http://127.0.0.1:8714/health
ok=true
database_exists=true
player2.online=true
player2.port=4315
player2.version=0.10.66
server_boot_id=5d8d3360f81843d6a2cc9a7c05ebcd81
```

Live DB migration:

```text
npc_memory_profiles exists=True
typed_memories exists=True
facts columns include save_id, settler_id, source_typed_memory_id
```

Live API validation save:

```text
save_id=codex_p1_validation
settler_id=p1_settler
name=Aldith Memorywright
role=Builder

profile.memories_count=3
profile.secrets_count=1
categories.decisions=1
categories.promises=1
categories.secrets=1
typed memories:
- decisions/recent: Decided to prioritize construction because the roof may collapse.
- promises/permanent: Aldith promised to repair the western wall before winter.
- secrets/major: The player let slip a private secret about hidden barley stores.
```

Live prompt context proof:

```text
GET /api/memory/context?npc_id=p1_settler&save_id=codex_p1_validation&role=builder&query=barley%20western%20wall&max_tokens=900

Returned:
=== PERSONAL FILE ===
Name: Aldith Memorywright
Role: Builder
Traits: careful, stubborn
Description: Aldith Memorywright; works as Builder; traits: careful, stubborn
Evolving summary: Memory profile: 1 decisions, 1 promises, 1 secrets...

=== LIFE EVENTS (always remembered) ===
promises: Aldith promised to repair the western wall before winter.

=== RELEVANT MEMORIES (RAG) ===
promises: Aldith promised to repair the western wall before winter.
secrets: The player let slip a private secret about hidden barley stores.

=== ENCYCLOPEDIA CONTEXT (RoleRAG) ===
builder construction/support-beam guidance
```

Browser/dashboard proof:

```text
Dashboard save codex_p1_validation rendered:
- Settler row: Aldith Memorywright, Builder, 3 memories, 1 secrets.
- Personal file: description and evolving summary.
- Category strip: Decisions 1, Promises 1, Secrets 1.
- Typed ledger: decisions/recent, promises/permanent, secrets/major.
```

## Acceptance Gate Status

| Gate | Status | Evidence |
| --- | --- | --- |
| NPC personal file exists | PASS | `npc_memory_profiles`, `/api/memory/npc`, dashboard personal-file card |
| Full interaction history is categorized | PASS | `typed_memories`, category counts, typed ledger |
| NPCs remember conversations/secrets/info | PASS | `secrets`, `promises`, `decisions` validation rows and prompt context |
| Relationships and event history are tracked | PASS for P1 foundation | relationship events classify into `relationship_milestones`; incidents/colony events write typed memories |
| Personalized description evolves | PASS for deterministic RC | profile `evolving_summary` refreshes from latest typed memories |
| Dashboard visibility | PASS | browser validation of memory card, categories, typed ledger |
| Regression coverage | PASS | `tools/memory_p1_selftest.py` |

## Known Non-Blockers

- The evolving summary is deterministic, not LLM-authored. That is acceptable for P1 RC because it is stable, testable, and prompt-visible.
- Existing old saves will only gain typed memories for new events after this migration. Legacy `facts` remain available as fallback context.
- Relationship UI still has broader display cleanup available, but P1 relationship memory writes are present.

## Follow-Up: Skill-Grounded Roles

Added after reviewing an in-game Alison skills panel where `Intellectual` was the highest visible skill.

Implemented:

- C# `NPCContextExtractor` infers a profession from top skills only when the game role is generic or missing, such as `unemployed`, `worker`, `settler`, or `unknown`.
- Backend profile upsert also infers a role from `stats` strings for dashboard/API-created profiles.
- Top skills are now prepared for memory profile refresh when an existing Player2 binding is available.

Validation:

```text
dotnet build --configuration Release -t:Rebuild
Build succeeded. 0 Warning(s), 0 Error(s)

PYTHONDONTWRITEBYTECODE=1 python -m py_compile dashboard\dashboard_server.py
PASS

node --check dashboard\app.js
PASS

python tools\memory_p1_selftest.py
memory_p1_selftest: PASS

Live API probe:
save_id=codex_p1_validation
settler_id=alison_skill_probe
profession input=unemployed
stats include Intellectual:25
stored profile.role=Scholar
```

Deployment caveat:

- The backend/dashboard inference is live.
- The C# DLL build contains the same inference logic, but the running game currently has the deployed DLL memory-mapped. The next game restart/deploy is required before in-game extraction uses the new C# inference.

## Follow-Up: Character Sheet Snapshots

Added after reviewing the in-game character-sheet pages for Alison Ridge.

Implemented:

- Backend `/api/character-sheet` accepts full per-settler snapshots keyed by `(save_id, settler_id)`.
- Raw character-sheet JSON is preserved in `character_sheets.raw_json` so newly exposed game fields are not lost.
- Normalized tables now cover the visible character sheet pages:
  - identity/status/profile fields
  - skills
  - needs
  - equipment and inventory
  - traits, perks, states, and vital modifiers
  - mood/social/religion modifiers with numeric values
  - work priorities
  - 24-hour schedule blocks
  - manage policies such as draft stance, self-tend, rally points, weapon/apparel/food/stimulants policies
- Prompt context now includes a `=== CHARACTER SHEET ===` block before RoleRAG so NPCs can reason from current personal state.
- Dashboard has a `Character Sheet` tab that renders the stored sheet for the selected settler.
- `Role: None` and `No role` are treated as generic roles and can be replaced by skill-grounded inference, e.g. `Intellectual:25` -> `Scholar`.

Validation:

```text
PYTHONDONTWRITEBYTECODE=1 python -m py_compile dashboard\dashboard_server.py
PASS

node --check dashboard\app.js
PASS

python tools\character_sheet_selftest.py
character_sheet_selftest: PASS

dotnet build --configuration Release -t:Rebuild
Build succeeded. 0 Warning(s), 0 Error(s)

Live API probe:
save_id=codex_sheet_validation
settler_id=alison_sheet_full
POST /api/character-sheet -> ok=true
GET /api/character-sheet -> role=Scholar, scheduleRows=24, manageRows=7
```

Browser/dashboard proof:

```text
Dashboard save codex_sheet_validation rendered:
- Character Sheet - Alison Ridge
- Role: Scholar
- Intellectual 25
- food 5
- Winter Clothes (Fine)
- movement_speed vital: 3.75m/s
- Ravenous mood: -18
- 24 hourly schedule rows including Role Duties and Leisure
- Manage settings including draft_stance=Flee, use_rally_points=True, weapon_policy=All Weapons
```

Current caveat:

- The database/API/dashboard can now store and show the full exposed sheet shape.
- The C# build sends the available `NPCContext` fields into this endpoint, but deeper reflection extraction for every live game UI field still needs in-game verification after the DLL can be redeployed. The deployed game DLL may remain old while Going Medieval is running and memory-mapping `BepInEx\plugins\LLM_NPCs.dll`.
