import json


WHITELIST = {
    "eat",
    "drink",
    "rest",
    "seek_medic",
    "flee",
    "defend",
    "seek_shelter",
    "socialize",
    "complain",
    "continue_job",
    "switch_job",
    "explore",
    "gather",
    "haul",
    "repair",
    "build_special",
    "prioritize_construction",
    "rebrand",
    "change_clothing",
    "draft",
    "capture",
}


def parse_command(token):
    if token is None:
        raise ValueError("Player2 command was empty")

    if isinstance(token, str):
        return token.strip(), {}

    if isinstance(token, list):
        if not token:
            raise ValueError("Player2 command array was empty")
        return parse_command(token[0])

    if not isinstance(token, dict):
        raise ValueError(f"Unsupported Player2 command token type: {type(token).__name__}")

    action = (
        token.get("name")
        or token.get("type")
        or token.get("command")
        or token.get("action")
    )
    action = action.strip() if isinstance(action, str) else action

    params = {}
    for key in ("parameters", "params"):
        value = token.get(key)
        if isinstance(value, dict):
            params.update(value)
            break

    if "arguments" in token:
        args = token["arguments"]
        if isinstance(args, str) and args.strip():
            try:
                parsed = json.loads(args)
                if isinstance(parsed, dict):
                    params.update(parsed)
                else:
                    params["arguments"] = args
            except json.JSONDecodeError:
                params["arguments"] = args
        elif isinstance(args, dict):
            params.update(args)

    for key, value in token.items():
        if key.lower() in {"name", "type", "command", "action", "arguments", "parameters", "params"}:
            continue
        params.setdefault(key, value)

    return action, params


def assert_case(label, token, expected_action, expected_params=None):
    action, params = parse_command(token)
    assert action == expected_action, f"{label}: expected action {expected_action!r}, got {action!r}"
    assert action in WHITELIST, f"{label}: action {action!r} is not whitelisted"
    for key, value in (expected_params or {}).items():
        assert params.get(key) == value, f"{label}: expected param {key}={value!r}, got {params.get(key)!r}"


def main():
    assert_case("string", "eat", "eat")
    assert_case("object-name", {"name": "drink"}, "drink")
    assert_case("object-action", {"action": "continue_job"}, "continue_job")
    assert_case("array", [{"name": "rest"}], "rest")
    assert_case(
        "arguments-json",
        {"name": "complain", "arguments": "{\"complaint\":\"The hall is freezing.\"}"},
        "complain",
        {"complaint": "The hall is freezing."},
    )
    assert_case(
        "extra-fields",
        {"command": "prioritize_construction", "building_type": "bedroom"},
        "prioritize_construction",
        {"building_type": "bedroom"},
    )

    for label, token in (("null", None), ("empty-array", [])):
        try:
            parse_command(token)
        except ValueError:
            continue
        raise AssertionError(f"{label}: expected parser failure")

    print("player2_command_parser_selftest: PASS")


if __name__ == "__main__":
    main()
