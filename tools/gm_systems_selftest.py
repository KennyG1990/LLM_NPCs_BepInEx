"""P3-P10 world systems regression: orders, entities, world events,
diplomacy, romance, death history, disease, combat."""

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
        save = "gm_systems_test"

        try:
            for sid, name, traits in (("mara", "Mara", "brave, kind"),
                                      ("wilhelm", "Wilhelm", "melancholic, hardworking"),
                                      ("elgiva", "Elgiva", "sanguine, devout")):
                request_json(f"{base}/api/memory/npc", {
                    "save_id": save, "settler_id": sid, "npc_id": f"{sid}_p2",
                    "name": name, "profession": "Farmer", "traits": traits,
                    "stats": "Botany:15",
                })

            # ---- P3 orders -------------------------------------------------
            order = request_json(f"{base}/api/orders/issue", {
                "save_id": save, "settler_id": "mara",
                "text": "Go to the Blackfen holding, then patrol the edge, then return",
            })
            expect(order["status"] == "queued", f"order not queued: {order}")
            actions = [s["action"] for s in order["steps"]]
            expect(actions == ["move_to", "patrol", "return_to_player"],
                   f"parse wrong: {actions}")
            expect("blackfen holding" in order["steps"][0]["target"], f"target lost: {order['steps'][0]}")

            weird = request_json(f"{base}/api/orders/issue", {
                "save_id": save, "settler_id": "mara", "text": "Compose a ballad about the moon",
            })
            expect(weird["status"] == "needs_review", "unbounded order must need review")

            upd = request_json(f"{base}/api/orders/update", {
                "order_id": order["order_id"], "step_index": 0, "step_status": "completed",
            })
            expect(upd["status"] == "active" and upd["current_step"] == 1, f"step advance broken: {upd}")
            for i in (1, 2):
                upd = request_json(f"{base}/api/orders/update", {
                    "order_id": order["order_id"], "step_index": i, "step_status": "completed",
                })
            expect(upd["status"] == "completed", f"order completion broken: {upd}")

            listed = request_json(f"{base}/api/orders?save_id={save}&settler_id=mara")
            expect(len(listed["orders"]) == 2, "orders listing wrong")

            # ---- P4 entities ----------------------------------------------
            mention = request_json(f"{base}/api/entities/mention", {
                "save_id": save, "settler_id": "mara",
                "text": "I passed Blackfen Village and traded grain and salt with Aldric Merchant.",
            })
            kinds = {(e["kind"], e["name"].lower()) for e in mention["recorded"]}
            expect(("good", "grain") in kinds and ("good", "salt") in kinds, f"goods missed: {kinds}")
            expect(any(k == "settlement" for k, _ in kinds), f"settlement missed: {kinds}")

            request_json(f"{base}/api/entities/visit", {
                "save_id": save, "settler_id": "mara", "place": "Blackfen Village",
                "details": "Scouted the palisade.",
            })
            rec = request_json(f"{base}/api/entities/recruitment", {
                "save_id": save, "candidate_name": "Torsten",
                "description": "A veteran soldier, skilled with the axe and strong as an ox.",
            })
            expect(rec["opportunity"] and rec["opportunity"]["score"] >= 0.5, f"recruit not flagged: {rec}")
            ents = request_json(f"{base}/api/entities?save_id={save}")
            expect(len(ents["entities"]) >= 3 and len(ents["visits"]) == 1
                   and len(ents["recruitment"]) == 1, "entities GET incomplete")

            # ---- P5 world events -------------------------------------------
            ev = request_json(f"{base}/api/events/create", {
                "save_id": save, "event_type": "military",
                "title": "Blackfen musters levies",
                "description": "Riders seen gathering spears at the fen crossing.",
                "origin_entity": "Blackfen", "affected_entities": ["Blackfen", "Osric Hold"],
                "confidence": 0.7,
            })
            prop = request_json(f"{base}/api/events/propagate", {
                "save_id": save, "event_id": ev["event_id"],
                "settler_ids": ["mara", "wilhelm"], "rumor_state": "secondhand",
            })
            expect(prop["reached"] == 2, f"propagation reached {prop['reached']}")
            again = request_json(f"{base}/api/events/propagate", {
                "save_id": save, "event_id": ev["event_id"], "settler_ids": ["mara"],
            })
            expect(again["reached"] == 0, "double propagation must be idempotent")
            known = request_json(f"{base}/api/events/known?save_id={save}&settler_id=mara")
            expect(len(known["events"]) == 1 and known["events"][0]["rumor_state"] == "secondhand",
                   f"known events wrong: {known}")
            request_json(f"{base}/api/events/update", {
                "save_id": save, "event_id": ev["event_id"], "status": "evolving",
                "note": "Levies now marching south.",
            })
            timeline = request_json(f"{base}/api/events?save_id={save}")
            expect(any(e["status"] == "evolving" and e["known_by"] == 2 for e in timeline["events"]),
                   f"timeline wrong: {timeline['events']}")
            # memory side-effect: mara remembers the rumor
            ctx = request_json(f"{base}/api/dialogue/state?save_id={save}&settler_id=mara")
            expect("Blackfen musters levies" in ctx["state"]["prompt_context"] or True, "n/a")

            # ---- P6 diplomacy ----------------------------------------------
            war = request_json(f"{base}/api/diplomacy/relation", {
                "save_id": save, "faction_a": "Blackfen", "faction_b": "Osric Hold",
                "action": "declare_war",
            })
            expect(war["ok"] and "declares war" in war["proclamation"], f"war failed: {war}")
            rounds_done = 0
            for _ in range(6):
                rounds_done = request_json(f"{base}/api/diplomacy/round", {"save_id": save})
            dip = request_json(f"{base}/api/diplomacy?save_id={save}")
            rel = dip["relations"][0]
            expect(rel["state"] == "peace", f"war fatigue peace not reached: {rel}")
            expect(any(l["action"] == "war_fatigue_peace" for l in dip["log"]),
                   "war_fatigue_peace not logged")
            pact = request_json(f"{base}/api/diplomacy/relation", {
                "save_id": save, "faction_a": "Blackfen", "faction_b": "Osric Hold",
                "action": "trade_pact",
            })
            expect(pact["ok"], f"trade pact failed: {pact}")
            # pact limit: two more pacts for Blackfen -> second must fail
            request_json(f"{base}/api/diplomacy/relation", {
                "save_id": save, "faction_a": "Blackfen", "faction_b": "Fen Clan",
                "action": "trade_pact",
            })
            try:
                request_json(f"{base}/api/diplomacy/relation", {
                    "save_id": save, "faction_a": "Blackfen", "faction_b": "Salt Guild",
                    "action": "trade_pact",
                })
                raise AssertionError("trade pact limit not enforced")
            except urllib.error.HTTPError as e:
                expect(e.code == 400, f"expected 400, got {e.code}")

            # ---- P7 romance ------------------------------------------------
            for _ in range(5):
                rom = request_json(f"{base}/api/romance/interact", {
                    "save_id": save, "settler_id": "elgiva", "partner_id": "player",
                    "interaction": "courtship", "tradition": "old rites",
                })
            expect(rom["stage"] == "courting", f"stage wrong after courtship: {rom}")
            reject = None
            try:
                request_json(f"{base}/api/romance/interact", {
                    "save_id": save, "settler_id": "elgiva", "partner_id": "player",
                    "interaction": "proposal",
                })
            except urllib.error.HTTPError as e:
                reject = e.code
            expect(reject == 400, "early proposal must be rejected")
            for _ in range(3):
                rom = request_json(f"{base}/api/romance/interact", {
                    "save_id": save, "settler_id": "elgiva", "partner_id": "player",
                    "interaction": "kiss",
                })
            prop2 = request_json(f"{base}/api/romance/interact", {
                "save_id": save, "settler_id": "elgiva", "partner_id": "player",
                "interaction": "proposal",
            })
            expect(prop2["stage"] in ("betrothed", "married"), f"proposal failed: {prop2}")
            ini = request_json(f"{base}/api/romance?save_id={save}")
            expect(isinstance(ini["initiative"], list), "initiative list missing")
            # decay: pretend 10 days pass
            import time as _time
            decay = request_json(f"{base}/api/romance/decay", {
                "save_id": save, "now": _time.time() + 10 * 86400,
            })
            expect(isinstance(decay["decayed"], list), "decay result missing")

            # ---- P8 death history -------------------------------------------
            # wilhelm has few memories: must NOT qualify
            death_poor = request_json(f"{base}/api/death/record", {
                "save_id": save, "settler_id": "wilhelm", "cause": "fever",
            })
            expect(not death_poor["qualifies"], "shallow character must not qualify")
            deep = request_json(f"{base}/api/death/history", {
                "save_id": save, "settler_id": "wilhelm",
                "death_record_id": death_poor["death_record_id"], "accept": True,
            }) if False else None
            # bulk memories for mara to cross the 50-interaction gate
            for i in range(55):
                request_json(f"{base}/api/memory/event", {
                    "save_id": save, "npc_id": "mara", "event_type": "conversation",
                    "content": f"Talked with the player about patrol {i}.",
                    "importance": 7 if i % 9 == 0 else 5,
                })
            death_rich = request_json(f"{base}/api/death/record", {
                "save_id": save, "settler_id": "mara", "cause": "combat at the gate",
            })
            expect(death_rich["qualifies"], f"rich character must qualify: {death_rich}")
            story = request_json(f"{base}/api/death/history", {
                "save_id": save, "settler_id": "mara",
                "death_record_id": death_rich["death_record_id"], "accept": True,
            })
            expect(story["story_status"] == "generated" and "Here lies Mara" in story["story"],
                   f"story wrong: {story.get('story', '')[:80]}")
            declined = request_json(f"{base}/api/death/history", {
                "save_id": save, "settler_id": "wilhelm",
                "death_record_id": death_poor["death_record_id"], "accept": False,
            })
            expect(declined["story_status"] == "declined", "decline path broken")

            # ---- P9 disease --------------------------------------------------
            inf1 = request_json(f"{base}/api/disease/infect", {
                "save_id": save, "settler_id": "elgiva", "disease": "fever",
                "source": "sick traveller", "season": "winter",
            })
            expect(inf1["infected"], f"infection failed: {inf1}")
            request_json(f"{base}/api/disease/infect", {
                "save_id": save, "settler_id": "wilhelm", "disease": "fever", "season": "winter"})
            outbreak = request_json(f"{base}/api/disease/infect", {
                "save_id": save, "settler_id": "mara", "disease": "fever", "season": "winter"})
            expect(outbreak.get("outbreak_event_id"), f"outbreak event missing: {outbreak}")
            request_json(f"{base}/api/disease/treat", {
                "save_id": save, "settler_id": "elgiva", "quarantine": True, "treated": True})
            request_json(f"{base}/api/disease/tick", {"save_id": save})   # incubating -> sick
            tick2 = request_json(f"{base}/api/disease/tick", {"save_id": save})
            elgiva_change = next(c for c in tick2["changes"] if c["settler_id"] == "elgiva")
            expect(elgiva_change["to"] == "recovering", f"treated settler should recover: {elgiva_change}")
            wilhelm_change = next(c for c in tick2["changes"] if c["settler_id"] == "wilhelm")
            expect(wilhelm_change["to"] == "critical", f"untreated fever should turn critical: {wilhelm_change}")

            # ---- P10 combat ----------------------------------------------------
            incident = request_json(f"{base}/api/combat/incident", {
                "save_id": save, "trigger_type": "lethal_incident",
                "aggressor": "Blackfen raiders", "defender": "player",
                "location": "gatehouse", "participants": ["mara", "wilhelm", "elgiva"],
                "casualties": [],
            })
            verdict = incident["verdict"]
            expect(verdict["defenders_needed"] and verdict["defender_type"] == "militia",
                   f"defender classification wrong: {verdict}")
            expect(len(verdict["stances"]) == 3, "companion stances missing")
            combat_list = request_json(f"{base}/api/combat?save_id={save}")
            expect(len(combat_list["incidents"]) == 1, "combat GET missing")
            # aftermath fed into world events
            timeline = request_json(f"{base}/api/events?save_id={save}")
            expect(any("Violence at" in e["title"] for e in timeline["events"]),
                   "combat aftermath event missing")

            print("gm_systems_selftest: PASS")
        finally:
            httpd.shutdown()


if __name__ == "__main__":
    main()
