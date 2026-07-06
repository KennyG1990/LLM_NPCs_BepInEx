import importlib.util
import json
import tempfile
import threading
import urllib.parse
import urllib.request
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVER_PATH = ROOT / "dashboard" / "dashboard_server.py"


def load_server_module():
    spec = importlib.util.spec_from_file_location("gm_dashboard_server", SERVER_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def request_json(url, payload=None):
    if payload is None:
        with urllib.request.urlopen(url, timeout=5) as response:
            return json.loads(response.read().decode("utf-8"))
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=5) as response:
        return json.loads(response.read().decode("utf-8"))


def main():
    server = load_server_module()
    with tempfile.TemporaryDirectory() as tmp:
        server.DB_PATH = Path(tmp) / "npc_memory.sqlite3"
        server.init_db()

        httpd = server.ReusableThreadingHTTPServer(("127.0.0.1", 0), server.DashboardHandler)
        thread = threading.Thread(target=httpd.serve_forever, daemon=True)
        thread.start()
        base = f"http://127.0.0.1:{httpd.server_address[1]}"

        try:
            save_id = "dialogue_test"
            settler_id = "alison"

            request_json(f"{base}/api/memory/npc", {
                "save_id": save_id,
                "settler_id": settler_id,
                "npc_id": "alison_p2",
                "name": "Alison Ridge",
                "profession": "Scholar",
                "traits": "reckless, hungry, proud",
                "stats": "Intellectual:25, Smithing:13",
            })

            state = request_json(
                f"{base}/api/dialogue/state?{urllib.parse.urlencode({'save_id': save_id, 'settler_id': settler_id})}"
            )
            assert state["ok"] is True
            assert state["state"]["trust"] == 0.5
            assert "Trust gate:" in state["state"]["prompt_context"]

            result = request_json(f"{base}/api/dialogue/exchange", {
                "save_id": save_id,
                "settler_id": settler_id,
                "player_text": "You said there was food, but Alison is ravenous.",
                "npc_text": "I spoke too quickly. I am ravenous and need food.",
                "claims": ["Alison is ravenous and wants food before research work."],
                "trust_delta": -0.1,
                "contradiction": {
                    "claim": "There was enough food for Alison.",
                    "reason": "Current character sheet says Ravenous.",
                },
                "barter_intent": {
                    "intent_type": "request_food",
                    "item": "meal",
                    "terms": "asks player to provide food before intellectual work",
                },
            })
            assert result["ok"] is True
            assert result["trust"] == 0.4
            assert result["claims_recorded"] == 1
            assert result["contradictions_recorded"] == 1
            assert result["barter_intents_recorded"] == 1

            auto_result = request_json(f"{base}/api/dialogue/exchange", {
                "save_id": save_id,
                "settler_id": settler_id,
                "player_text": "But you said Alison had enough food, and that is not true.",
                "npc_text": "You are right to challenge that. I should not have said there was enough food.",
                "claims": [],
            })
            assert auto_result["ok"] is True
            assert auto_result["contradictions_recorded"] == 1
            assert auto_result["trust"] < result["trust"]

            state = request_json(
                f"{base}/api/dialogue/state?{urllib.parse.urlencode({'save_id': save_id, 'settler_id': settler_id})}"
            )
            context = state["state"]["prompt_context"]
            assert "Trust toward player: 0.32" in context
            assert "Alison is ravenous and wants food" in context
            assert "Known contradictions:" in context
            assert "Open barter intents:" in context
            assert "request_food: meal" in context

            conn = server.get_db_connection()
            try:
                claims = conn.execute(
                    "SELECT COUNT(*) AS count FROM dialogue_claims WHERE save_id = ? AND settler_id = ?",
                    (save_id, settler_id),
                ).fetchone()["count"]
                barter = conn.execute(
                    "SELECT COUNT(*) AS count FROM dialogue_barter_intents WHERE save_id = ? AND settler_id = ?",
                    (save_id, settler_id),
                ).fetchone()["count"]
                memories = conn.execute(
                    "SELECT COUNT(*) AS count FROM typed_memories WHERE save_id = ? AND settler_id = ? AND category IN ('conversations', 'betrayals', 'promises')",
                    (save_id, settler_id),
                ).fetchone()["count"]
                assert claims == 2
                assert barter == 1
                assert memories >= 3
            finally:
                conn.close()
        finally:
            httpd.shutdown()
            httpd.server_close()

    print("dialogue_p2_selftest: PASS")


if __name__ == "__main__":
    main()
