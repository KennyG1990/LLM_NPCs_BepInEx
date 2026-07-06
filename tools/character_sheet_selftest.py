import importlib.util
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVER_PATH = ROOT / "dashboard" / "dashboard_server.py"


def load_server_module():
    spec = importlib.util.spec_from_file_location("gm_dashboard_server", SERVER_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    server = load_server_module()
    with tempfile.TemporaryDirectory() as tmp:
        server.DB_PATH = Path(tmp) / "npc_memory.sqlite3"
        server.init_db()
        conn = server.get_db_connection()
        try:
            sheet = {
                "identity": {
                    "name": "Alison Ridge",
                    "role": "None",
                    "age": 51,
                    "gender": "female",
                    "background": "Reckless scribe",
                    "pseudonym": "The Bastard of Wyck",
                },
                "health": {
                    "hitpoints": 100,
                    "max_hitpoints": 100,
                    "food_state": "Ravenous",
                    "sleep_state": "Well rested",
                    "mood_state": "Neutral",
                    "mood": 45.1,
                },
                "vitals": {"mass_carried": "0/60kg", "temperature_range": "-1C to 26C"},
                "states": ["Idle", "Ravenous", "Well rested"],
                "needs": {
                    "food": 5,
                    "sleep": 80,
                    "religious_activities": 20,
                    "entertainment": 35,
                    "aesthetics": 45,
                    "comfort": 25,
                },
                "skills": {
                    "Animal Handling": 12,
                    "Art": 10,
                    "Botany": 3,
                    "Carpentry": 11,
                    "Construction": 1,
                    "Culinary": 3,
                    "Intellectual": 25,
                    "Marksman": 8,
                    "Medicine": 2,
                    "Melee": 0,
                    "Mining": 10,
                    "Smithing": 13,
                    "Speechcraft": 5,
                    "Tailoring": 9,
                },
                "skill_experience": {"Intellectual": 0.25},
                "traits": ["Thin", "Tall", "Middle Aged", "Night time"],
                "perks": ["Night owl"],
                "equipment": {"clothing": "Winter Clothes (Fine)"},
                "inventory": ["Simple Healing Kit x1"],
                "current_activity": {
                    "type": "idle",
                    "description": "Idle",
                    "schedule": "Leisure",
                    "room": "None",
                },
                "work_priorities": {
                    "Firefight": 3,
                    "Patient": 3,
                    "Mine": 1,
                    "Steward": 2,
                    "Haul": 3,
                },
                "mood": {
                    "modifiers": [
                        {"label": "Initial Optimism", "value": 25},
                        {"label": "Low expectation", "value": 8},
                        {"label": "Ravenous", "value": -18},
                    ]
                },
                "social_logs": [],
                "beliefs": {"modifiers": [{"label": "Deprived of religious activities", "value": -8}]},
                "schedule": {str(hour): "Sleep" if hour < 7 else "Role Duties" if hour < 17 else "Leisure" for hour in range(24)},
                "manage": {
                    "draft_stance": "Flee",
                    "self_tend": False,
                    "use_rally_points": True,
                    "weapon_policy": "All Weapons",
                    "apparel_policy": "All Apparel",
                    "food_policy": "All Food",
                    "stimulants_policy": "All Stimulants",
                },
            }

            with conn:
                server.upsert_character_sheet(conn, "sheet_test", "alison", sheet)

            row = conn.execute(
                "SELECT * FROM character_sheets WHERE save_id='sheet_test' AND settler_id='alison'"
            ).fetchone()
            assert row is not None
            assert row["name"] == "Alison Ridge"
            assert row["role"] == "Scholar"
            assert row["background"] == "Reckless scribe"
            assert row["schedule_label"] == "Leisure"
            assert row["room"] == "None"

            skills = {
                r["skill_name"]: r["level"]
                for r in conn.execute("SELECT * FROM character_sheet_skills WHERE save_id='sheet_test' AND settler_id='alison'")
            }
            assert skills["Intellectual"] == 25
            assert skills["Smithing"] == 13

            needs = {
                r["need_name"]: r["value"]
                for r in conn.execute("SELECT * FROM character_sheet_needs WHERE save_id='sheet_test' AND settler_id='alison'")
            }
            assert needs["food"] == 5
            assert needs["comfort"] == 25

            priorities = {
                r["job_name"]: r["priority"]
                for r in conn.execute("SELECT * FROM character_sheet_work_priorities WHERE save_id='sheet_test' AND settler_id='alison'")
            }
            assert priorities["Mine"] == 1

            equipment = [r["item"] for r in conn.execute("SELECT * FROM character_sheet_equipment WHERE save_id='sheet_test' AND settler_id='alison'")]
            assert "Winter Clothes (Fine)" in equipment
            assert "Simple Healing Kit x1" in equipment

            traits = [r["value"] for r in conn.execute("SELECT * FROM character_sheet_traits WHERE save_id='sheet_test' AND settler_id='alison'")]
            assert "Night owl" in traits
            assert "Ravenous" in traits

            mood = [r["label"] for r in conn.execute("SELECT * FROM character_sheet_mood_modifiers WHERE save_id='sheet_test' AND settler_id='alison'")]
            assert "Ravenous" in mood
            assert "Deprived of religious activities" in mood

            mood_scores = {
                r["label"]: r["value"]
                for r in conn.execute("SELECT * FROM character_sheet_mood_modifiers WHERE save_id='sheet_test' AND settler_id='alison'")
            }
            assert mood_scores["Ravenous"] == -18

            schedule = {
                r["hour"]: r["activity"]
                for r in conn.execute("SELECT * FROM character_sheet_schedule WHERE save_id='sheet_test' AND settler_id='alison'")
            }
            assert len(schedule) == 24
            assert schedule[0] == "Sleep"
            assert schedule[12] == "Role Duties"
            assert schedule[18] == "Leisure"

            manage = {
                r["setting_name"]: r["setting_value"]
                for r in conn.execute("SELECT * FROM character_sheet_manage_settings WHERE save_id='sheet_test' AND settler_id='alison'")
            }
            assert manage["draft_stance"] == "Flee"
            assert manage["self_tend"] == "False"
            assert manage["use_rally_points"] == "True"
            assert manage["weapon_policy"] == "All Weapons"

            profile = conn.execute(
                "SELECT * FROM npc_memory_profiles WHERE save_id='sheet_test' AND settler_id='alison'"
            ).fetchone()
            assert profile["role"] == "Scholar"
        finally:
            conn.close()

    print("character_sheet_selftest: PASS")


if __name__ == "__main__":
    main()
