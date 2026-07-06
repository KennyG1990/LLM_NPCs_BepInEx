# P2 AI Dialogues Worklog

Last verified: 2026-07-05 23:29 ET

Priority document: `01 - AI Dialogues with NPCs.md`

## Slice 1 - Memory-Aware Dialogue State

Implemented:

- Added `dialogue_states` for per-settler player trust, disclosure level, voice profile, contradiction count, and barter-intent count.
- Added `dialogue_claims` for DB-backed claims made during dialogue, including contradicted claims.
- Added `dialogue_barter_intents` for proposed request/trade hooks before full trade execution exists.
- Added `GET /api/dialogue/state`.
  - Returns a prompt-ready `=== DIALOGUE STATE ===` block.
  - Includes trust gating, voice cues, recent claims, contradictions, and open barter intents.
- Added `POST /api/dialogue/exchange`.
  - Records player/NPC dialogue text into typed memories.
  - Records model-provided claims.
  - Applies trust deltas.
  - Records contradictions as betrayal-style memories.
  - Records barter/request intents as promise-style memories.
- Updated `DialogueManager` prompts for both floating dialogue and SocialHub inline dialogue.
  - Prompts now include dialogue state in addition to RoleRAG/memory context.
  - Prompts request structured JSON: `dialogue`, `claims`, `trust_delta`, `contradiction`, `barter_intent`.
  - Plain text model responses still work as a fallback.
- Added `MemoryManager.GetDialogueStateForPromptAsync`.
- Added `MemoryManager.RecordDialogueExchange`.
- Added regression coverage in `tools/dialogue_p2_selftest.py`.

## Validation

Automated gates:

```text
PYTHONDONTWRITEBYTECODE=1 python -m py_compile dashboard\dashboard_server.py tools\dialogue_p2_selftest.py
PASS

python tools\dialogue_p2_selftest.py
dialogue_p2_selftest: PASS

python tools\character_sheet_selftest.py
character_sheet_selftest: PASS

dotnet build --configuration Release -t:Rebuild
Build succeeded. 0 Warning(s), 0 Error(s)
```

Live API probe:

```text
save_id=codex_p2_dialogue_validation
settler_id=alison_dialogue

Initial trust: 0.50
POST /api/dialogue/exchange -> ok=true
trust=0.40
claims_recorded=1
contradictions_recorded=1
barter_intents_recorded=1

GET /api/dialogue/state returned:
- Trust toward player: 0.40
- Disclosure level: normal
- Voice cues from traits: reckless, hungry, proud
- Known contradiction: There was enough food for Alison.
- Recent claim: Alison is ravenous and wants food before research work.
- Open barter intent: request_food: meal
```

## Remaining P2 Work

- Prove the floating in-game dialogue UI with the newly rebuilt DLL after the game is closed and the DLL can be redeployed.
- Add dashboard visibility for dialogue claims, contradictions, trust gates, and barter intents.
- Add richer personality/backstory voice authoring instead of deriving voice cues only from traits/profile.
- Add stronger contradiction matching against prior player/NPC claims instead of relying mostly on model-provided contradiction objects.
- Add trust adjustment rules based on relationship history, promises kept/broken, and lie detection outcomes.
- Keep non-Player2 backends deferred.
