"""Background Computer Use MCP server and deterministic command-line access."""
from __future__ import annotations
import argparse
import base64
import json
from pathlib import Path
import sys

from bcu.core import BackgroundError, BackgroundLab, VERSION


def obj(properties, required=()):
    return {"type": "object", "properties": properties, "required": list(required), "additionalProperties": False}


def string(description, maximum=1024):
    return {"type": "string", "description": description, "maxLength": maximum, "minLength": 1}


def integer(low, high):
    return {"type": "integer", "minimum": low, "maximum": high}


REF = string("Opaque reference returned by this plugin.", 32)
DEFINITIONS = [
    ("background_status", True, "Read background backend configuration and strict capture/input capabilities. Never activates a window.", obj({})),
    ("configure_backend", False, "Configure the trusted published native helper from this plugin's build; records executable hash. Does not download tools or change PATH.", obj({"helper_path": string("Absolute path to BackgroundComputerUse.Native.exe.", 8000)}, ["helper_path"])),
    ("list_background_windows", False, "Enumerate current Windows targets and cache short-lived window bindings without activating them or moving the pointer.", obj({})),
    ("observe_background_window", False, "Read controls and capture an occluded window through strict Windows Graphics Capture. Never restores, activates or falls back to screen capture. Returns fresh action references and foreground/cursor samples.", obj({"window_id": REF, "include_image": {"type": "boolean"}, "depth": integer(1, 10), "max_nodes": integer(1, 300)}, ["window_id"])),
    ("act_on_control", False, "Perform one UIA pattern action on a freshly observed control, with identity/geometry checks and a fresh observation afterward. No global input or focus call; app behavior may still change focus and is reported.", obj({"observation_id": REF, "element_id": string("Exact control ID from the observation.", 2048), "action": {"type": "string", "enum": ["invoke", "set_value", "toggle", "select", "expand", "collapse", "scroll"]}, "value": {"type": "string", "maxLength": 8192}, "direction": {"type": "string", "enum": ["up", "down", "left", "right"]}}, ["observation_id", "element_id", "action"])),
    ("click_background_window", False, "Send a single left/right window-message click using this observation's verified image pixels. Does not move the host mouse. Many custom/GPU/raw-input apps ignore these messages; inspect the returned fresh observation.", obj({"observation_id": REF, "x": integer(0, 16383), "y": integer(0, 16383), "button": {"type": "string", "enum": ["left", "right"]}}, ["observation_id", "x", "y"])),
    ("prepare_foreground_mouse", True, "Prepare a validated target handoff to the existing Computer Use mouse API when the user chooses foreground mouse control. This tool sends no input and grants no permission.", obj({"observation_id": REF}, ["observation_id"])),
]
TOOLS = {name: (read_only, description, schema) for name, read_only, description, schema in DEFINITIONS}
LAB = BackgroundLab()


def validate(value, schema, name="arguments"):
    kinds = {"object": dict, "string": str, "integer": int, "boolean": bool}
    kind = schema["type"]
    if type(value) is not kinds[kind]:
        raise ValueError(name + " must be " + kind)
    if "enum" in schema and value not in schema["enum"]:
        raise ValueError("Unsupported " + name)
    if kind == "object":
        if set(value) - set(schema["properties"]) or set(schema["required"]) - set(value):
            raise ValueError("Unknown or missing fields in " + name)
        for key, item in value.items():
            validate(item, schema["properties"][key], name + "." + key)
    elif kind == "string" and not schema.get("minLength", 0) <= len(value) <= schema.get("maxLength", 1000000):
        raise ValueError("Invalid length in " + name)
    elif kind == "integer" and not schema["minimum"] <= value <= schema["maximum"]:
        raise ValueError("Invalid range in " + name)


def call(name, arguments):
    if name not in TOOLS:
        raise ValueError("Unknown tool")
    validate(arguments, TOOLS[name][2])
    return getattr(LAB, name)(**arguments)


def tool_result(payload):
    content = [{"type": "text", "text": json.dumps(payload, ensure_ascii=False)}]
    observation = payload.get("observation", payload)
    image = observation.get("image") if isinstance(observation, dict) else None
    if image and Path(image["path"]).is_file() and Path(image["path"]).stat().st_size <= 12 * 1024 * 1024:
        content.append({"type": "image", "mimeType": "image/png", "data": base64.b64encode(Path(image["path"]).read_bytes()).decode("ascii")})
    return {"content": content, "structuredContent": payload, "isError": False}


def dispatch(request):
    request_id = request.get("id")
    if request_id is None:
        return None
    method = request.get("method")
    params = request.get("params", {})
    if not isinstance(params, dict):
        return {"jsonrpc": "2.0", "id": request_id, "error": {"code": -32602, "message": "Params must be an object"}}
    if method == "initialize":
        version = params.get("protocolVersion")
        result = {"protocolVersion": version if version in ("2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25") else "2025-03-26",
                  "capabilities": {"tools": {}}, "serverInfo": {"name": "background-computer-use", "version": VERSION},
                  "instructions": "Default to background operation. Enumerate, observe, inspect, perform one action, then inspect the returned fresh observation. Capture is WGC-only and never restores/activates. Semantic actions and window-message clicks never inject global input; provider/app effects are reported. No silent foreground fallback. User chooses foreground mouse mode through the existing Computer Use skill. Read bundled background-computer-use skill. CLI --list-tools/--call offers the same tools if a chat defers them."}
    elif method == "ping":
        result = {}
    elif method == "tools/list":
        result = {"tools": [{"name": name, "description": description, "inputSchema": schema,
                             "annotations": {"readOnlyHint": read_only,
                                             "destructiveHint": name in ("act_on_control", "click_background_window"),
                                             "openWorldHint": name in ("act_on_control", "click_background_window")}}
                            for name, read_only, description, schema in DEFINITIONS]}
    elif method == "tools/call":
        try:
            result = tool_result(call(params.get("name"), params.get("arguments", {})))
        except (BackgroundError, ValueError, OSError) as exc:
            error = {"code": getattr(exc, "code", "invalid_arguments"), "message": str(exc), "foreground_fallback_performed": False}
            result = {"content": [{"type": "text", "text": json.dumps({"error": error})}], "isError": True}
    else:
        return {"jsonrpc": "2.0", "id": request_id, "error": {"code": -32601, "message": "Unknown method"}}
    return {"jsonrpc": "2.0", "id": request_id, "result": result}


def main():
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--list-tools", action="store_true")
    group.add_argument("--call", choices=list(TOOLS))
    parser.add_argument("--args-file", type=Path)
    arguments = parser.parse_args()
    if arguments.list_tools:
        print(json.dumps(dispatch({"id": 1, "method": "tools/list"})["result"], indent=2))
        return 0
    if arguments.call:
        try:
            if arguments.args_file and arguments.args_file.stat().st_size > 1024 * 1024:
                raise ValueError("Arguments file exceeds 1 MiB.")
            values = json.loads(arguments.args_file.read_text(encoding="utf-8-sig")) if arguments.args_file else {}
            print(json.dumps(call(arguments.call, values), indent=2, ensure_ascii=False))
            return 0
        except (BackgroundError, ValueError, OSError) as exc:
            print(json.dumps({"error": {"code": getattr(exc, "code", "invalid_arguments"), "message": str(exc)}}), file=sys.stderr)
            return 1
    for line in sys.stdin:
        try:
            message = json.loads(line)
            if not isinstance(message, dict) or message.get("jsonrpc") != "2.0":
                raise ValueError("Invalid JSON-RPC")
            response = dispatch(message)
        except (TypeError, ValueError):
            response = {"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "Invalid input"}}
        if response is not None:
            print(json.dumps(response, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
