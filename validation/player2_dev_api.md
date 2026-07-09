# Player2 DEV API — full surface (live from http://127.0.0.1:4315/v1/openapi.json, v0.1.0, 53 ops)
Ground-truthed 2026-07-08. Swagger UI: http://127.0.0.1:4315/docs/

## Used by the mod today (call sites in LLMClient.cs)
- GET  /health                                   — CheckHealthAsync (liveness gate)
- POST /npc/games/{game_id}/npcs/spawn           — persona registration
- GET  /npc/games/{game_id}/npcs/responses       — streamed responses
- POST /npc/games/{game_id}/npcs/{npc_id}/chat   — decisions + dialogue (main spend)
- POST /chat/completions                         — raw completions (x2 call sites)

## 🔥 HIGH-VALUE UNUSED (map to roadmap systems)
- /games/{game_id}/data/global + /data/user (GET/PUT/DELETE + /batch)
    → Player2-side persistent KV store: PLANS, LAWS, LINEAGES, death histories
      survive save-scumming & live outside the game process. (Planner #17,
      governance, doc 08.)
- POST /embeddings
    → semantic memory retrieval for the RAG layer (doc 05 NPC Memory).
- POST /npc/games/{game_id}/npcs/{npc_id}/kill
    → NPC lifecycle on settler death → Death History trigger (doc 08).
- POST /tts/speak, /tts/eleven/text-to-speech/{voice_id}(+/stream), GET /tts/voices
    → voiced settlers (reference overview: built-in TTS).
- POST /stt/start|stop, /stt/whisper/audio/transcriptions
    → player talks to settlers by voice (doc 01 dialogues).
- GET /joules
    → LLM budget telemetry for the call-budget governor (#23).
- POST /image/generate|edit, /sprite/*, /model3d/*, /video/*(+job pollers)
    → portraits, heraldry, chronicle illustrations (nice-to-have).
- GET /ai_profiles, /selected_characters, /models
    → model/profile selection surface (ModelSelectionManager).
- POST /login/web/{game_client_id}, /logs/upload — auth + diagnostics.

## Full op list (53)
GET /ai_profiles; POST /chat/completions; POST /embeddings;
GET|PUT|DELETE /games/{g}/data/global; POST /games/{g}/data/global/batch;
GET|PUT|DELETE /games/{g}/data/user;   POST /games/{g}/data/user/batch;
GET /health; POST /image/edit; POST /image/generate; GET /joules;
POST /login/web/{game_client_id}; POST /logs/upload;
POST /model3d/generate_from_image; GET /model3d/job/{job_id};
GET /npc/games/{g}/npcs/responses; POST /npc/games/{g}/npcs/spawn;
POST /npc/games/{g}/npcs/{n}/chat; POST /npc/games/{g}/npcs/{n}/kill;
GET /selected_characters; POST /sprite/animate; POST /sprite/generate;
GET /sprite/job/{job_id}; GET|POST /stt/language; GET /stt/languages;
POST /stt/start; POST /stt/stop; POST /stt/whisper/audio/transcriptions;
POST /stt/whisper/audio/translations; GET /stt/whisper/models(+/{model});
GET /tts/eleven/models; POST /tts/eleven/text-to-speech/{voice_id}(+/stream);
GET /tts/eleven/user(+/subscription); GET /tts/eleven/voices(+/{id}, +/settings/default, +/{id}/settings);
POST /tts/speak; POST /tts/stop; GET /tts/voices; GET|POST /tts/volume;
POST /video/generate; POST /video/generate_from_image; GET /video/job/{job_id}
(All under base /v1; auth headers: player2-game-key + X-Game-Client-Id.)
