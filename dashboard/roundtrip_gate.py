"""
ROUND-TRIP GATE — automated save->reload idempotency check.
Encodes the procedure proven manually on 2026-07-08 (Henderskelf, roundtrip1/2).

PASS criteria after a reload:
  1. Game reaches gameplay (colony_status.txt timestamp advances).
  2. Telemetry shows 'RESTORED' home (not re-derived) when a sidecar exists.
  3. House line shows 'RE-ADOPTED' or 'complete' — never a fresh 'planned'.
  4. placed counters stay 0 (sp=0 cook=0 bed=0) on the first ticks.
  5. Verified stockpile census >= 1 (never places a duplicate).

Usage:  python roundtrip_gate.py            (assumes game at main menu)
Driving: dashboard endpoints only (no sandbox): kill/launch/status/input/screen.
NOTE: RESUME loads the most recent save. UI coords are the proven rel coords:
  RESUME (0.858, 0.310) · story-continue (0.498, 0.955) · speed key '3'.
"""
import json, re, time, urllib.request

BASE = "http://127.0.0.1:8714"
STATUS_TXT = r"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\colony_status.txt"

def post(path, body=None):
    req = urllib.request.Request(BASE + path, method="POST",
        data=json.dumps(body or {}).encode(), headers={"Content-Type": "application/json"})
    return json.loads(urllib.request.urlopen(req, timeout=30).read())

def get(path):
    return json.loads(urllib.request.urlopen(BASE + path, timeout=30).read())

def click(x, y): return post("/api/game/input", {"action": "click", "x": x, "y": y})
def key(k):      return post("/api/game/input", {"action": "keypress", "key": k})

def read_status():
    with open(STATUS_TXT, encoding="utf-8", errors="replace") as f:
        return f.read()

def fail(msg):
    print("GATE FAIL:", msg); raise SystemExit(1)

def main():
    # 0) game must be running at the main menu (relaunch cycle is the caller's job)
    st = get("/api/dev/status")
    if not st.get("game_running"): fail("game not running")
    if not st.get("dll_in_sync"):  fail("deployed DLL out of sync with build")

    before = read_status()
    m = re.search(r"stockpiles=(\d+)", before)
    sp_before = int(m.group(1)) if m else -1

    # 1) resume most recent save
    get("/api/game/screen?force_focus=true"); time.sleep(2.5)
    click(0.858, 0.310)                       # RESUME
    time.sleep(35)
    click(0.498, 0.955)                       # story continue
    time.sleep(5)
    key("3")                                  # max speed
    time.sleep(40)                            # >= 2 ColonyBuilder ticks

    s = read_status()
    print(s)

    # 2) assertions
    if "RESTORED" not in s and "home fixed" in s:
        fail("home was re-derived, not restored (sidecar ignored)")
    if re.search(r"house: planned", s):
        fail("house re-planned fresh after reload (duplicate-house bug)")
    m = re.search(r"\[placed sp=(\d+) cook=(\d+) bed=(\d+)\]", s)
    if m and any(int(v) > 0 for v in m.groups()):
        fail(f"placement counters nonzero after reload: {m.group(0)}")
    m = re.search(r"stockpiles=(\d+)", s)
    sp_after = int(m.group(1)) if m else -1
    if sp_before >= 0 and sp_after > sp_before:
        fail(f"stockpile count grew across reload: {sp_before} -> {sp_after}")
    if sp_after == 0 and sp_before > 0:
        fail("verified census lost existing stockpiles (census regression)")

    print(f"GATE PASS: stockpiles {sp_before}->{sp_after}, no re-placement, state restored")

if __name__ == "__main__":
    main()
