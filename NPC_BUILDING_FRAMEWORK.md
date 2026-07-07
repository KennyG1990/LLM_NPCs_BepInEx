# NPC Self-Building Framework (design)

Requirement (Ken, 2026-07-06): "at some point we need a framework for the
NPCs to be able to build their own buildings."

Grounded in hooks that already exist and work:

- `GameBridge.TryTriggerBuild(gameObject, buildingName)` — used by the
  `build_special` decision action.
- `DecisionExecutor.ExecutePrioritizeConstruction` — bumps a settler's
  Construct job priority (proven pattern: switch_job set Mine=1 live).
- `GameBridge._buildingsManagerType` — cached reflection handle to the game's
  buildings manager (placement surface for later phases).
- `MoveTo(Vector3)` reflection (in ExecuteDraft) — positions a settler.
- `ai_orders` queue + OrderExecutor — proven C# execution loop.
- `settler_pressures` + `colony_events` — the deterministic needs signal.

## Phase B1 — Proposals (backend only, no game writes)

NPCs "decide" what the colony should build; humans (or later phases) approve.

- Table `construction_proposals(id, save_id, settler_id, building, reason,
  urgency, status: proposed|approved|placed|built|rejected, created_at)`.
- Deterministic proposer: colony alerts + pressures → proposals
  (research table missing → research table; food reserves low → farm plot /
  food storage; settlers sleeping outside → beds/room; hostile animals →
  palisade section).
- Endpoints: `POST /api/construction/propose` (auto-derive or explicit),
  `GET /api/construction`, `POST /api/construction/update` (approve/reject).
- Dialogue tie-in: proposals appear in the settler's prompt context so they
  TALK about wanting to build ("we need a proper research table").
- Validation: selftest + dashboard World Systems card.

## Phase B2 — Approved proposals become orders

- Approving a proposal enqueues an `ai_orders` entry:
  `prioritize_construction` (existing verb) + `build_special {building}`.
- OrderExecutor maps them (two new cases, both verbs already exist in
  DecisionExecutor).
- Outcome: settler stops idling and actively works construction; if
  TryTriggerBuild can propose the specific blueprint, the game handles the
  rest natively (settlers build placed blueprints on their own).
- Validation: order steps complete + JOBS panel shows Construct=1 + BepInEx
  log shows TryTriggerBuild invoked.

## Phase B3 — Blueprint placement (the hard part, host+ILSpy session)

- Reflect the buildings manager to place a real blueprint at a chosen tile:
  needs decompiling NSMedieval placement API (ILSpy is installed on the host).
- Site selection heuristic: near existing structures, on soil 100% / stability
  ≥4 tiles (values already visible in the HUD readout), flat, unoccupied.
- Safety: autonomy-gated (AutonomyManager), max N pending NPC blueprints,
  player veto via dashboard.

## Phase B4 — Autonomous initiative loop

- Settler with sustained high colony_need pressure + relevant skill proposes,
  places (B3), constructs (B2), then records a typed memory + world event
  ("Godstan raised a new granary") feeding P5 events and death histories.

Order of work: B1 (backend, immediate) → B2 (small C# additions to
OrderExecutor) → B3 (needs a decompile session) → B4 (wiring).
