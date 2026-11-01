"""Opt-in actual-source core/MCP test. Own fixture only; stdlib dependencies only."""
from __future__ import annotations
import argparse
import base64
import ctypes
from ctypes import wintypes
import hashlib
import json
import math
import os
from pathlib import Path
import queue
import struct
import subprocess
import sys
import threading
import time
import traceback
import zlib

HERE = Path(__file__).resolve().parent
def find_plugin_root():
    """Find the installed/source plugin without assuming a checkout location."""
    for candidate in (HERE, *HERE.parents):
        if (candidate / "server.py").is_file() and (candidate / "bcu").is_dir():
            return candidate
    return None

def default_run_root():
    base = Path(os.environ.get("BCU_DATA_DIR", Path.home() / ".codex/plugin-data/background-computer-use"))
    return base.expanduser().resolve() / "self-tests"

PS = Path(os.environ["WINDIR"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"
NO_WINDOW = subprocess.CREATE_NO_WINDOW
user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.GetForegroundWindow.restype = wintypes.HWND
user32.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
user32.GetWindow.restype = wintypes.HWND
user32.SetThreadDpiAwarenessContext.argtypes = [ctypes.c_void_p]
user32.SetThreadDpiAwarenessContext.restype = ctypes.c_void_p
user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.IsWindowVisible.argtypes = [wintypes.HWND]

class LastInput(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.UINT), ("dwTime", wintypes.DWORD)]

def physical():
    if not user32.SetThreadDpiAwarenessContext(ctypes.c_void_p(-4)):
        raise ctypes.WinError(ctypes.get_last_error())

def snapshot():
    physical()
    point = wintypes.POINT()
    value = LastInput(ctypes.sizeof(LastInput), 0)
    if not user32.GetCursorPos(ctypes.byref(point)) or not user32.GetLastInputInfo(ctypes.byref(value)):
        raise ctypes.WinError(ctypes.get_last_error())
    return dict(monotonic=time.monotonic(), foreground_hwnd=str(user32.GetForegroundWindow() or 0),
                cursor={"x": point.x, "y": point.y}, last_input_tick=value.dwTime)

def assess(samples):
    first, last = samples[0], samples[-1]
    fg = any(item["foreground_hwnd"] != first["foreground_hwnd"] for item in samples)
    cursor = any(item["cursor"] != first["cursor"] for item in samples)
    activity = any(item["last_input_tick"] != first["last_input_tick"] for item in samples)
    return dict(sample_count=len(samples), foreground_preserved=not fg, cursor_preserved=not cursor,
                external_input_observed=activity, before=first, after=last,
                result="observed-preserved" if not (fg or cursor or activity) else "inconclusive-state-or-external-input-changed",
                note="Sampled observations do not attribute changes to code or exclude between-sample changes.")

def ps(script, *args, timeout=20):
    completed = subprocess.run([str(PS), "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                                "-File", str(HERE / script), *map(str, args)],
                               capture_output=True, text=True, encoding="utf-8-sig", timeout=timeout,
                               creationflags=NO_WINDOW)
    if completed.returncode:
        raise RuntimeError(script + ": " + completed.stderr[-3000:])
    return json.loads(completed.stdout.lstrip("\ufeff"))

def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))

def png_stats(data):
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    offset, chunks, info = 8, [], None
    while offset < len(data):
        length = struct.unpack_from(">I", data, offset)[0]
        kind, value = data[offset+4:offset+8], data[offset+8:offset+8+length]
        assert zlib.crc32(kind + value) & 0xFFFFFFFF == struct.unpack_from(">I", data, offset+8+length)[0]
        if kind == b"IHDR": info = struct.unpack(">IIBBBBB", value)
        if kind == b"IDAT": chunks.append(value)
        offset += 12 + length
        if kind == b"IEND": break
    width, height, bits, color, compression, filtering, interlace = info
    assert bits == 8 and color in (2, 6) and compression == filtering == interlace == 0
    bpp = 4 if color == 6 else 3
    raw, previous, counts = zlib.decompress(b"".join(chunks)), bytearray(width*bpp), {}
    position = 0
    for _ in range(height):
        filter_type = raw[position]; position += 1
        row = bytearray(raw[position:position+width*bpp]); position += width*bpp
        for index in range(len(row)):
            a = row[index-bpp] if index >= bpp else 0
            b = previous[index]
            c = previous[index-bpp] if index >= bpp else 0
            if filter_type == 1: row[index] = (row[index] + a) & 255
            elif filter_type == 2: row[index] = (row[index] + b) & 255
            elif filter_type == 3: row[index] = (row[index] + (a+b)//2) & 255
            elif filter_type == 4:
                p = a+b-c; distances = (abs(p-a), abs(p-b), abs(p-c))
                predictor = a if distances[0] <= distances[1] and distances[0] <= distances[2] else b if distances[1] <= distances[2] else c
                row[index] = (row[index] + predictor) & 255
            else: assert filter_type == 0
        for index in range(0, len(row), bpp):
            rgb = bytes(row[index:index+3]).hex()
            if rgb in ("249cb7", "ffc44a", "263445", "e5883a"):
                counts[rgb] = counts.get(rgb, 0) + 1
        previous = row
    return {"width": width, "height": height, "known_color_counts": counts, "sha256": hashlib.sha256(data).hexdigest()}

class McpClient:
    def __init__(self, plugin, data_root):
        env = {**os.environ, "PYTHONPATH": str(plugin), "BCU_DATA_DIR": str(data_root), "PYTHONIOENCODING": "utf-8"}
        self.process = subprocess.Popen([sys.executable, str(plugin / "server.py")], stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8",
                                        bufsize=1, env=env, creationflags=NO_WINDOW)
        self.output, self.errors = queue.Queue(), []
        threading.Thread(target=self._read, daemon=True).start()
        threading.Thread(target=lambda: self.errors.append(self.process.stderr.read()), daemon=True).start()
        self.counter = 0
    def _read(self):
        for line in self.process.stdout:
            self.output.put(line)
    def rpc(self, method, params):
        self.counter += 1
        self.process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.counter, "method": method, "params": params}) + "\n")
        self.process.stdin.flush()
        result = json.loads(self.output.get(timeout=15))
        assert result["id"] == self.counter and "error" not in result
        return result["result"]
    def close(self):
        self.process.stdin.close()
        try: self.process.wait(timeout=3)
        except subprocess.TimeoutExpired: self.process.kill(); self.process.wait(timeout=3)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--plugin-root", type=Path, default=find_plugin_root(), help="Plugin root; defaults to the nearest ancestor containing server.py and bcu/.")
    parser.add_argument("--helper", type=Path, default=Path(r"D:\Tools\BackgroundComputerUse\native\BackgroundComputerUse.Native.exe"), help="Published native helper executable; no helper is downloaded or installed.")
    parser.add_argument("--sha256", required=True, help="Reviewed SHA-256 of the helper executable; checked before launch and by every native call.")
    parser.add_argument("--run-root", type=Path, default=default_run_root(), help="External receipt root; defaults to BCU_DATA_DIR/self-tests or ~/.codex/plugin-data/background-computer-use/self-tests.")
    args = parser.parse_args()
    if args.plugin_root is None:
        parser.error("No plugin ancestor found. Supply --plugin-root pointing to server.py and bcu/.")
    args.plugin_root = args.plugin_root.resolve()
    args.run_root = args.run_root.expanduser().resolve()
    if not (args.plugin_root / "server.py").is_file() or not (args.plugin_root / "bcu").is_dir():
        parser.error("--plugin-root must contain server.py and bcu/.")
    sys.path.insert(0, str(args.plugin_root))
    from bcu.core import BackgroundLab, NativeBackend, BackgroundError
    assert hashlib.sha256(args.helper.read_bytes()).hexdigest() == args.sha256.lower()
    samples, receipts, stop_signal = [], [], threading.Event()
    def sample_loop():
        physical()
        while not stop_signal.is_set():
            samples.append(snapshot()); stop_signal.wait(.005)
    sampler = threading.Thread(target=sample_loop, daemon=True); sampler.start()
    launch = None; run = None; client = None
    summary = {"passed": False, "helper_sha256": args.sha256.lower(), "scope": "Actual Python core and stdio MCP backed by native helper, owned fixture only."}
    def record(name, operation):
        before = snapshot(); started = time.monotonic()
        result = operation()
        receipts.append({"step": name, "elapsed_seconds": time.monotonic()-started, "before": before, "after": snapshot(), "result": result})
        return result
    def control(obs, name):
        matches = [item for item in obs["controls"] if item["name"] == name]
        assert len(matches) == 1, (name, matches)
        return matches[0]
    def verify_image(obs):
        image = obs["image"]
        assert not obs["image_error"] and image["method"] == "wgc-only"
        stats = png_stats(Path(image["path"]).read_bytes())
        assert stats["sha256"] == image["sha256"]
        assert stats["width"] == image["width"] and stats["height"] == image["height"]
        assert image["geometry"]["geometry_verified"] is True
        assert image["geometry"]["coordinate_space"] == "physical-screen-pixels"
        assert stats["known_color_counts"].get("249cb7", 0) > 500
        assert stats["known_color_counts"].get("ffc44a", 0) > 100
        assert stats["known_color_counts"].get("263445", 0) == 0
        return stats
    try:
        launch = record("launch-owned-fixture", lambda: ps("Start-Fixture.ps1", "-LifetimeSeconds", 120, "-RunRoot", args.run_root))
        run = Path(launch["runDirectory"])
        summary["run_directory"] = str(run)
        ready = read_json(run / "ready.json")
        assert ready["coordinateSpace"] == "physical-screen-pixels"
        target, cover = int(ready["targetIdentity"]["hwnd"]), int(ready["coverIdentity"]["hwnd"])
        assert user32.GetWindow(cover, 4) == target and user32.IsWindowVisible(target) and user32.IsWindowVisible(cover)
        target_rect, cover_rect = wintypes.RECT(), wintypes.RECT()
        assert user32.GetWindowRect(target, ctypes.byref(target_rect)) and user32.GetWindowRect(cover, ctypes.byref(cover_rect))
        assert bytes(target_rect) == bytes(cover_rect)
        data_root = run / "core-data"
        lab = BackgroundLab(root=data_root, backend=NativeBackend(args.helper, args.sha256.lower()))
        record("configure-temporary-backend", lambda: lab.configure_backend(str(args.helper)))
        windows = record("core-list-fixture-binding", lab.list_background_windows)
        target_windows = [window for window in windows["windows"] if str(window["hwnd"]) == ready["targetIdentity"]["hwnd"] and window["pid"] == ready["pid"]]
        assert len(target_windows) == 1
        window_id = target_windows[0]["window_id"]
        # Do not persist unrelated desktop window metadata in test receipts.
        receipts[-1]["result"] = {"target": target_windows[0], "returned_window_count": len(windows["windows"])}
        observation = record("core-observe-covered-fixture", lambda: lab.observe_background_window(window_id, True, 6, 100))
        summary["initial_image"] = verify_image(observation)
        assert control(observation, "Test value").get("value") == "fixture-initial"
        old_observation = observation
        set_result = record("core-set-value", lambda: lab.act_on_control(observation["observation_id"], control(observation, "Test value")["element_id"], "set_value", "python-core-background-verified"))
        observation = set_result["observation"]
        assert control(observation, "Test value").get("value") == "python-core-background-verified"
        assert read_json(run / "ready.json")["value"] == "python-core-background-verified"
        try:
            lab.act_on_control(old_observation["observation_id"], control(old_observation, "Increment")["element_id"], "invoke")
        except BackgroundError as exc:
            assert exc.code == "observation_consumed"
            receipts.append({"step": "core-reject-consumed-observation", "error": exc.code})
        else: raise AssertionError("Consumed observation was accepted")
        invoke_result = record("core-invoke", lambda: lab.act_on_control(observation["observation_id"], control(observation, "Increment")["element_id"], "invoke"))
        observation = invoke_result["observation"]
        assert read_json(run / "ready.json")["counter"] == 1
        verify_image(observation)
        button = control(observation, "Increment")["bounds"]
        screen = observation["image"]["geometry"]["screen_rect"]
        x, y = math.floor(button["x"] + button["width"]/2 - screen["x"]), math.floor(button["y"] + button["height"]/2 - screen["y"])
        click_result = record("core-coordinate-click", lambda: lab.click_background_window(observation["observation_id"], x, y))
        observation = click_result["observation"]
        assert read_json(run / "ready.json")["counter"] == 2
        summary["final_image"] = verify_image(observation)
        handoff = record("core-prepare-foreground-handoff-no-input", lambda: lab.prepare_foreground_mouse(observation["observation_id"]))
        assert handoff["input_executed"] is False and handoff["requires_task_authorization"] is True
        assert handoff["existing_task_authorization_remains_valid"] is True
        client = McpClient(args.plugin_root, data_root)
        initialization = record("mcp-initialize", lambda: client.rpc("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "Owned fixture acceptance", "version": "1"}}))
        assert initialization["serverInfo"]["name"] == "background-computer-use"
        client.process.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n"); client.process.stdin.flush()
        tools = record("mcp-tools-list", lambda: client.rpc("tools/list", {}))
        assert len(tools["tools"]) == 7
        mcp_observation = record("mcp-observe-covered-fixture", lambda: client.rpc("tools/call", {"name": "observe_background_window", "arguments": {"window_id": window_id, "include_image": True, "depth": 6, "max_nodes": 100}}))
        assert mcp_observation["isError"] is False
        payload = mcp_observation["structuredContent"]
        assert control(payload, "Test value").get("value") == "python-core-background-verified"
        images = [item for item in mcp_observation["content"] if item["type"] == "image"]
        assert len(images) == 1 and images[0]["mimeType"] == "image/png"
        decoded = base64.b64decode(images[0]["data"], validate=True)
        image_stats = png_stats(decoded)
        assert image_stats["sha256"] == payload["image"]["sha256"]
        summary["mcp_image"] = image_stats
        receipts[-1]["result"] = {"isError": mcp_observation["isError"], "structuredContent": payload,
                                   "content": [{"type": "image", "mimeType": "image/png", "decoded_bytes": len(decoded), "sha256": image_stats["sha256"]}, {"type": "text", "payload_matches_structured_content": json.loads(mcp_observation["content"][0]["text"]) == payload}]}
        mcp_handoff = record("mcp-prepare-foreground-handoff-no-input", lambda: client.rpc("tools/call", {"name": "prepare_foreground_mouse", "arguments": {"observation_id": payload["observation_id"]}}))
        assert not mcp_handoff["isError"] and mcp_handoff["structuredContent"]["input_executed"] is False
        summary.update(functional_passed=True, final_readback={key: read_json(run / "ready.json")[key] for key in ("value", "counter")},
                       mcp_tool_count=7, consumed_observation_rejected=True, handoff_input_executed=False,
                       core_action_routes=[set_result["action"]["delivery_route"], invoke_result["action"]["delivery_route"], click_result["action"]["delivery_route"]])
    except BaseException:
        summary["error"] = traceback.format_exc()
    finally:
        if client is not None: client.close()
        if launch is not None:
            try: summary["stop"] = ps("Stop-Fixture.ps1", "-RunDirectory", launch["runDirectory"])
            except BaseException: summary["stop_error"] = traceback.format_exc()
        stop_signal.set(); sampler.join(timeout=2)
        samples.append(snapshot())
        summary["observation"] = assess(samples)
        summary["passed"] = bool(summary.get("functional_passed") and summary["observation"]["result"] == "observed-preserved" and not summary.get("stop_error"))
        if run:
            (run / "core-e2e-tool-results.json").write_text(json.dumps(receipts, indent=2), encoding="utf-8")
            (run / "core-e2e-samples.json").write_text(json.dumps(samples, indent=2), encoding="utf-8")
            (run / "core-e2e-summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
            (run.parent / "last-core-run-path.txt").write_text(str(run), encoding="utf-8")
        print(json.dumps(summary, indent=2))
    return 0 if summary["passed"] else 1

if __name__ == "__main__":
    raise SystemExit(main())
