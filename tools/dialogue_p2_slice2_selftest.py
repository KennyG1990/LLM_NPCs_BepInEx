"""P2 slice 2 regression: contradiction matcher v2, deterministic trust
rules, trust_events audit, barter resolution, voice authoring."""

import importlib.util
import json
import tempfile
import threading
import urllib.error
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
    req = urllib.request.Request(url, data=body,
                                 headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=5) as response:
        return json.loads(response.read().decode("utf-8"))


def expect(condition, message):
    if not condition:
        raise AssertionError(message)


def main():
    server = load_server_module()
    with tempfile.TemporaryDirectory() as tmp:
        server.DB_PATH = Path(tmp) / "npc_memory.sqlite3"
        server.init_db()
        httpd = server.ReusableThreadingHTTPServer(("127.0.0.1", 0), server.DashboardHandler)
        threading.Thread(target=httpd.serve_forever, daemon=True).start()
        base = f"http://127.0.0.1:{httpd.server_address[1]}"
        save_id = "slice2_test"
        settler_id = "gunnar"

        try:
            # Profile with traits/role for voice authoring.
            request_json(f"{base}/api/memory/npc", {
                "save_id": save_id, "settler_id": settler_id, "npc_id": "gunnar_p2",
                "name": "Gunnar", "profession": "Steward",
                "traits": "proud, cynical, greedy",
                "stats": "Speechcraft:22, Intellectual:14",
            })

            # 1. Voice authoring: state must include an authored register, not raw traits.
            state = request_json(
                f"{base}/api/dialogue/state?save_id={save_id}&settler_id={settler_id}")["state"]
            expect("Speech register:" in state["voice_profile"],
                   f"voice not authored: {state['voice_profile']!r}")
            expect("medieval register" in state["voice_profile"],
                   "voice missing medieval register instruction")

            # 2. NPC makes a claim; no contradiction yet.
            r1 = request_json(f"{base}/api/dialogue/exchange", {
                "save_id": save_id, "settler_id": settler_id,
                "player_text": "How do the stores look?",
                "npc_text": "The granary is full; we have enough grain for winter.",
                "claims": ["The granary is full and there is enough grain for winter."],
            })
            expect(r1["contradictions_recorded"] == 0, "false positive contradiction on first claim")
            expect(r1["claims_recorded"] == 1, "claim not recorded")
            trust_after_claim = r1["trust"]
            expect(trust_after_claim > 0.5, "consistent claim should nudge trust up")

            # 3. Self-contradiction via negation flip: new claim conflicts with prior claim.
            r2 = request_json(f"{base}/api/dialogue/exchange", {
                "save_id": save_id, "settler_id": settler_id,
                "player_text": "Tell me again about our food.",
                "npc_text": "There is no grain left in the granary, we are out.",
                "claims": ["There is no grain left in the granary."],
            })
            expect(r2["contradictions_recorded"] == 1,
                   f"negation-flip self-contradiction not caught: {r2}")
            expect(r2["trust"] < trust_after_claim, "contradiction must lower trust")

            # 4. Trust events audit trail exists and explains the drop.
            state = request_json(
                f"{base}/api/dialogue/state?save_id={save_id}&settler_id={settler_id}")["state"]
            expect(len(state["trust_events"]) >= 2, "trust events missing")
            expect(any("contradiction" in e["reason"] for e in state["trust_events"]),
                   "no contradiction reason in trust events")

            # 5. Escalation: second offense costs more than the first.
            request_json(f"{base}/api/dialogue/exchange", {
                "save_id": save_id, "settler_id": settler_id,
                "npc_text": "The cellar holds plenty of salted meat.",
                "claims": ["The cellar holds plenty of salted meat."],
            })
            before = request_json(
                f"{base}/api/dialogue/state?save_id={save_id}&settler_id={settler_id}")["state"]["trust"]
            r3 = request_json(f"{base}/api/dialogue/exchange", {
                "save_id": save_id, "settler_id": settler_id,
                "npc_text": "We have no salted meat in the cellar.",
                "claims": ["There is no salted meat in the cellar."],
            })
            expect(r3["contradictions_recorded"] == 1, "second contradiction not caught")
            second_drop = before - r3["trust"]
            expect(second_drop > 0.08 - 1e-9,
                   f"repeat offense should cost more than base: dropped {second_drop:.3f}")

            # 6. Barter intent lifecycle: propose then resolve kept/broken.
            r4 = request_json(f"{base}/api/dialogue/exchange", {
                "save_id": save_id, "settler_id": settler_id,
                "player_text": "Bring me herbs and I will pay in ale.",
                "npc_text": "Agreed. Herbs for ale.",
                "barter_intent": {"intent_type": "trade", "item": "herbs", "terms": "ale for herbs"},
            })
            expect(r4["barter_intents_recorded"] == 1, "barter intent not recorded")
            state = request_json(
                f"{base}/api/dialogue/state?save_id={save_id}&settler_id={settler_id}")["state"]
            intent_id = state["barter_intents"][0]["id"]
            trust_before = state["trust"]
            kept = request_json(f"{base}/api/dialogue/barter/resolve", {
                "save_id": save_id, "settler_id": settler_id,
                "intent_id": intent_id, "resolution": "fulfilled",
            })
            expect(abs(kept["trust"] - (trust_before + 0.06)) < 1e-6 or kept["trust"] == 1.0,
                   f"promise_kept delta wrong: {trust_before} -> {kept['trust']}")
            # double-resolve must 409
            try:
                request_json(f"{base}/api/dialogue/barter/resolve", {
                    "save_id": save_id, "settler_id": settler_id,
                    "intent_id": intent_id, "resolution": "broken",
                })
                raise AssertionError("double resolve should fail")
            except urllib.error.HTTPError as e:
                expect(e.code == 409, f"expected 409, got {e.code}")

            # broken promise costs more than kept gains
            r5 = request_json(f"{base}/api/dialogue/exchange", {
                "save_id": save_id, "settler_id": settler_id,
                "npc_text": "I shall fetch you timber by dusk.",
                "barter_intent": {"intent_type": "request_fulfil", "item": "timber", "terms": "by dusk"},
            })
            state = request_json(
                f"{base}/api/dialogue/state?save_id={save_id}&settler_id={settler_id}")["state"]
            intent2 = next(i for i in state["barter_intents"] if i["status"] == "proposed")
            trust_before = state["trust"]
            broken = request_json(f"{base}/api/dialogue/barter/resolve", {
                "save_id": save_id, "settler_id": settler_id,
                "intent_id": intent2["id"], "resolution": "broken",
            })
            expect(abs((trust_before - broken["trust"]) - 0.10) < 1e-6 or broken["trust"] == 0.0,
                   f"promise_broken delta wrong: {trust_before} -> {broken['trust']}")
            expect(any("broken" in e["reason"] for e in broken["state"]["trust_events"]),
                   "broken promise missing from trust events")

            print("dialogue_p2_slice2_selftest: PASS")
        finally:
            httpd.shutdown()


if __name__ == "__main__":
    main()
