import importlib.util
import sqlite3
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
            with conn:
                server.upsert_memory_profile(
                    conn,
                    "test_save",
                    "settler_a",
                    name="Aldith",
                    role="Builder",
                    traits="careful, stubborn",
                    stats="Construction:12",
                )
                first = conn.execute(
                    "SELECT * FROM npc_memory_profiles WHERE save_id='test_save' AND settler_id='settler_a'"
                ).fetchone()
                assert first["display_name"] == "Aldith"
                assert first["role"] == "Builder"
                assert first["memories_count"] == 0

                secret_id = server.insert_typed_memory(
                    conn,
                    "test_save",
                    "settler_a",
                    "dialogue_player",
                    "The player let slip a private secret about hidden barley stores.",
                    importance=8,
                )
                promise_id = server.insert_typed_memory(
                    conn,
                    "test_save",
                    "settler_a",
                    "dialogue_npc",
                    "Aldith promised to repair the western wall before winter.",
                    importance=9,
                )
                decision_id = server.insert_typed_memory(
                    conn,
                    "test_save",
                    "settler_a",
                    "decision",
                    "Decided to prioritize construction: the roof may collapse.",
                    importance=6,
                )

                assert secret_id and promise_id and decision_id

                rows = conn.execute(
                    "SELECT category, tier, is_secret FROM typed_memories WHERE save_id='test_save' AND settler_id='settler_a' ORDER BY id"
                ).fetchall()
                assert [r["category"] for r in rows] == ["secrets", "promises", "decisions"]
                assert rows[0]["is_secret"] == 1
                assert rows[1]["tier"] == "permanent"
                assert rows[2]["tier"] == "recent"

                counts = server.get_memory_category_counts(conn, "test_save", "settler_a")
                assert counts["secrets"]["count"] == 1
                assert counts["promises"]["count"] == 1
                assert counts["decisions"]["count"] == 1
                assert counts["deaths"]["count"] == 0

                profile = conn.execute(
                    "SELECT * FROM npc_memory_profiles WHERE save_id='test_save' AND settler_id='settler_a'"
                ).fetchone()
                assert profile["memories_count"] == 3
                assert profile["secrets_count"] == 1
                assert "Memory profile:" in profile["evolving_summary"]
                assert "promises" in profile["evolving_summary"]

                server.upsert_memory_profile(
                    conn,
                    "test_save",
                    "settler_a",
                    name="Aldith",
                    role="Master Builder",
                    traits=None,
                    stats=None,
                )
                preserved = conn.execute(
                    "SELECT * FROM npc_memory_profiles WHERE save_id='test_save' AND settler_id='settler_a'"
                ).fetchone()
                assert preserved["role"] == "Master Builder"
                assert preserved["memories_count"] == 3
                assert preserved["secrets_count"] == 1
                assert preserved["evolving_summary"] == profile["evolving_summary"]

                server.upsert_memory_profile(
                    conn,
                    "test_save",
                    "settler_b",
                    name="Alison",
                    role="unemployed",
                    traits="AdultAgeEffector01",
                    stats="Animal Handling:12, Art:10, Botany:3, Carpentry:11, Construction:1, Culinary:3, Intellectual:25, Marksman:8, Medicine:2, Melee:0, Mining:10, Smithing:13, Speechcraft:5, Tailoring:9",
                )
                alison = conn.execute(
                    "SELECT * FROM npc_memory_profiles WHERE save_id='test_save' AND settler_id='settler_b'"
                ).fetchone()
                assert alison["role"] == "Scholar"
                assert "works as Scholar" in alison["description"]

                facts_columns = {r["name"] for r in conn.execute("PRAGMA table_info(facts)")}
                assert {"save_id", "settler_id", "source_typed_memory_id"}.issubset(facts_columns)
        finally:
            conn.close()

    print("memory_p1_selftest: PASS")


if __name__ == "__main__":
    main()
