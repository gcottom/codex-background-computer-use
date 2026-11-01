from __future__ import annotations

import base64
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

from bcu.core import BackgroundLab
import server
from test_background_core import FakeBackend


ENTRY = Path(server.__file__).resolve()


class BackgroundServerTests(unittest.TestCase):
    def test_real_stdio_handshake_catalog_and_unconfigured_status(self):
        with tempfile.TemporaryDirectory() as tmp:
            env = dict(os.environ, BCU_DATA_DIR=str(Path(tmp) / "isolated plugin data"), PYTHONIOENCODING="utf-8")
            proc = subprocess.Popen([sys.executable, "-B", str(ENTRY)], stdin=subprocess.PIPE,
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                                    encoding="utf-8", env=env)
            replies = queue.Queue()

            def read_replies():
                try:
                    for line in proc.stdout:
                        replies.put(json.loads(line))
                except Exception as exc:
                    replies.put(exc)

            reader = threading.Thread(target=read_replies, daemon=True)
            reader.start()

            def send(message):
                proc.stdin.write(json.dumps(message) + "\n")
                proc.stdin.flush()

            def request(request_id, method, params):
                send({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
                result = replies.get(timeout=5)
                if isinstance(result, Exception):
                    raise result
                self.assertEqual(result["id"], request_id)
                self.assertNotIn("error", result)
                return result["result"]

            try:
                initialized = request(1, "initialize", {
                    "protocolVersion": "2025-06-18", "capabilities": {},
                    "clientInfo": {"name": "background-computer-use-regression", "version": "1.0"},
                })
                self.assertEqual(initialized["protocolVersion"], "2025-06-18")
                self.assertEqual(initialized["serverInfo"]["name"], "background-computer-use")
                send({"jsonrpc": "2.0", "method": "notifications/initialized"})
                catalog = request(2, "tools/list", {})["tools"]
                self.assertEqual(len(catalog), 7)
                self.assertEqual(len(set(t["name"] for t in catalog)), 7)
                self.assertIn("click_background_window", {t["name"] for t in catalog})
                self.assertIn("prepare_foreground_mouse", {t["name"] for t in catalog})
                status = request(3, "tools/call", {"name": "background_status", "arguments": {}})
                self.assertFalse(status["isError"])
                self.assertFalse(status["structuredContent"]["configured"])
                self.assertEqual(status["structuredContent"]["data_root"], env["BCU_DATA_DIR"])
                self.assertNotIn("backend", status["structuredContent"])
                send({"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": []})
                malformed = replies.get(timeout=5)
                self.assertEqual(malformed["id"], 4)
                self.assertEqual(malformed["error"]["code"], -32602)
                # A malformed request cannot kill the server or corrupt later UTF-8 traffic.
                self.assertEqual(request("接続-Δ-🎮", "ping", {}), {})
                proc.stdin.close()
                proc.stdin = None
                proc.wait(timeout=5)
                reader.join(timeout=2)
                self.assertEqual(proc.returncode, 0)
                self.assertEqual(proc.stderr.read(), "")
                self.assertFalse(reader.is_alive())
            finally:
                if proc.poll() is None:
                    proc.kill()
                    proc.wait(timeout=5)
                if proc.stdin is not None:
                    proc.stdin.close()
                reader.join(timeout=2)
                proc.stdout.close()
                proc.stderr.close()

    def test_dispatch_rejects_unsafe_arguments_and_emits_the_verified_png(self):
        with tempfile.TemporaryDirectory() as tmp:
            backend = FakeBackend()
            lab = BackgroundLab(root=tmp, backend=backend, clock=lambda: 1000.0)
            window_id = lab.list_background_windows()["windows"][0]["window_id"]
            observed = lab.observe_background_window(window_id, include_image=True)
            with patch.object(server, "LAB", lab):
                malformed = [
                    ("observe_background_window", {"window_id": window_id, "depth": True}),
                    ("click_background_window", {"observation_id": observed["observation_id"], "x": True, "y": 40}),
                    ("act_on_control", {"observation_id": observed["observation_id"], "element_id": "increment", "action": "invoke", "activate": True}),
                    ("act_on_control", {"observation_id": observed["observation_id"], "element_id": "increment", "action": "invoke", "restore_foreground": "yes"}),
                    ("act_on_control", {"observation_id": observed["observation_id"], "element_id": "increment", "action": "invoke", "minimize_activating_window": True}),
                    ("click_background_window", {"observation_id": observed["observation_id"], "x": 30, "y": 30, "minimize_activating_window": True}),
                    ("act_on_control", {"observation_id": observed["observation_id"], "element_id": "increment", "action": "global_click"}),
                ]
                for name, arguments in malformed:
                    with self.subTest(tool=name, arguments=arguments):
                        before = len(backend.calls)
                        result = server.dispatch({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                                                  "params": {"name": name, "arguments": arguments}})["result"]
                        self.assertTrue(result["isError"])
                        error = json.loads(result["content"][0]["text"])["error"]
                        self.assertEqual(error["code"], "invalid_arguments")
                        self.assertFalse(error["foreground_fallback_performed"])
                        self.assertEqual(len(backend.calls), before)
                self.assertEqual(backend.mutations, [])
                # Bad request validation leaves the original one-action token usable.
                action = server.dispatch({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {
                    "name": "act_on_control", "arguments": {
                        "observation_id": observed["observation_id"], "element_id": "increment", "action": "invoke"}}})["result"]
                self.assertFalse(action["isError"])
                self.assertEqual(backend.counter, 1)
                image = next(c for c in action["content"] if c["type"] == "image")
                self.assertEqual(image["mimeType"], "image/png")
                emitted = base64.b64decode(image["data"], validate=True)
                image_metadata = action["structuredContent"]["observation"]["image"]
                self.assertEqual(hashlib.sha256(emitted).hexdigest(), image_metadata["sha256"])
                self.assertEqual(emitted, Path(image_metadata["path"]).read_bytes())
                changed = server.dispatch({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {
                    "name": "act_on_control", "arguments": {
                        "observation_id": action["structuredContent"]["observation"]["observation_id"],
                        "element_id": "value", "action": "set_value", "value": "café Δ 日本語 🎮"}}})["result"]
                self.assertFalse(changed["isError"])
                self.assertEqual(backend.controls[1]["value"], "café Δ 日本語 🎮")
                self.assertIn("café Δ 日本語 🎮", changed["content"][0]["text"])

    def test_tool_result_emits_root_and_child_surface_images(self):
        with tempfile.TemporaryDirectory() as tmp:
            backend = FakeBackend()
            backend.surfaces = [{
                "hwnd": "90001", "pid": 27182, "start_time": "638973936000000001", "title": "Embedded surface",
                "class_name": "WebViewChild", "route": "uia_provider_process_descendant",
                "window_rect": {"x": -1180, "y": 110, "width": 80, "height": 60},
                "client_rect": {"x": -1180, "y": 110, "width": 80, "height": 60},
            }]
            lab = BackgroundLab(root=tmp, backend=backend, clock=lambda: 1000.0)
            window_id = lab.list_background_windows()["windows"][0]["window_id"]
            observed = lab.observe_background_window(window_id, include_image=True)
            result = server.tool_result(observed)
            images = [item for item in result["content"] if item["type"] == "image"]
            self.assertEqual(len(images), 2)
            labels = [item["text"] for item in result["content"] if item["type"] == "text"]
            self.assertTrue(any("Verified child surface image" in item for item in labels))
            child_bytes = base64.b64decode(images[1]["data"], validate=True)
            child_image = observed["surface_images"][0]["image"]
            self.assertEqual(hashlib.sha256(child_bytes).hexdigest(), child_image["sha256"])

    def test_cli_status_and_invalid_arguments_do_not_start_a_native_backend(self):
        with tempfile.TemporaryDirectory() as tmp:
            env = dict(os.environ, BCU_DATA_DIR=str(Path(tmp) / "no configured helper"), PYTHONIOENCODING="utf-8")
            status = subprocess.run([sys.executable, "-B", str(ENTRY), "--call", "background_status"],
                                    capture_output=True, text=True, encoding="utf-8", env=env, timeout=10)
            self.assertEqual(status.returncode, 0, status.stderr)
            self.assertFalse(json.loads(status.stdout)["configured"])
            self.assertEqual(status.stderr, "")
            args_file = Path(tmp) / "bad request with spaces Δ_日本語.json"
            args_file.write_text(json.dumps({"activate": True}), encoding="utf-8-sig")
            invalid = subprocess.run([sys.executable, "-B", str(ENTRY), "--call", "list_background_windows",
                                      "--args-file", str(args_file)], capture_output=True, text=True,
                                     encoding="utf-8", env=env, timeout=10)
            self.assertEqual(invalid.returncode, 1)
            self.assertEqual(invalid.stdout, "")
            self.assertEqual(json.loads(invalid.stderr)["error"]["code"], "invalid_arguments")
            self.assertFalse(Path(env["BCU_DATA_DIR"]).exists())


if __name__ == "__main__":
    unittest.main()
