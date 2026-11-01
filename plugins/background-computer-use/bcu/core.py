from __future__ import annotations

from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import sys
import time
import uuid


VERSION = "0.1.2"
OBSERVATION_SECONDS = 30
WINDOW_SECONDS = 600
MAX_PIXELS = 20_000_000
TOKEN = re.compile(r"^[0-9a-f]{32}$")


class BackgroundError(Exception):
    def __init__(self, code, message):
        self.code = code
        super().__init__(message)


def utc_now():
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def data_root():
    value = os.environ.get("BCU_DATA_DIR")
    return Path(value).expanduser().resolve() if value else Path.home() / ".codex" / "plugin-data" / "background-computer-use"


def atomic_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    stage = path.with_name("." + path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with stage.open("x", encoding="utf-8") as output:
            json.dump(value, output, ensure_ascii=False)
            output.flush()
            os.fsync(output.fileno())
        os.replace(stage, path)
    finally:
        stage.unlink(missing_ok=True)


def read_json(path):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise BackgroundError("unknown_reference", "Reference is unavailable; enumerate or observe again.") from exc


def file_hash(path):
    with Path(path).open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def desktop_delta(before, after):
    def has_samples(value):
        return (isinstance(value, dict) and value.get("foreground_hwnd") is not None
                and isinstance(value.get("cursor"), dict)
                and type(value["cursor"].get("x")) is int and type(value["cursor"].get("y")) is int)
    if not has_samples(before) or not has_samples(after):
        return {"sample_available": False, "preservation_verified": False}
    foreground_changed = before.get("foreground_hwnd") != after.get("foreground_hwnd")
    cursor_changed = before.get("cursor") != after.get("cursor")
    return {
        "sample_available": True,
        "foreground_changed": foreground_changed,
        "cursor_changed": cursor_changed,
        "input_activity_changed": before.get("last_input_tick") != after.get("last_input_tick"),
        "preservation_verified": not foreground_changed and not cursor_changed,
        "note": "Samples report changes, not causality. User activity or the app can change focus or the cursor. No focus-restoration or global-input fallback is performed.",
    }


class NativeBackend:
    def __init__(self, helper_path, expected_hash=None):
        self.helper_path = Path(helper_path)
        self.expected_hash = expected_hash

    def call(self, request):
        if sys.platform != "win32":
            raise BackgroundError("unsupported_platform", "The native backend requires Windows.")
        if not self.helper_path.is_absolute() or not self.helper_path.is_file():
            raise BackgroundError("backend_missing", "Configure the published native helper first.")
        if self.expected_hash and file_hash(self.helper_path) != self.expected_hash:
            raise BackgroundError("backend_changed", "The configured helper changed. Review and configure the new build.")
        try:
            completed = subprocess.run(
                [str(self.helper_path)], input=json.dumps(request, ensure_ascii=False),
                capture_output=True, text=True, encoding="utf-8", timeout=12,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except subprocess.TimeoutExpired as exc:
            raise BackgroundError("backend_timeout", "The bounded helper timed out. An action may have happened; observe again before another action.") from exc
        except OSError as exc:
            raise BackgroundError("backend_start_failed", str(exc)) from exc
        try:
            result = json.loads(completed.stdout)
        except ValueError as exc:
            raise BackgroundError("invalid_backend_response", "Native helper did not return JSON: " + completed.stderr[-1200:]) from exc
        if not isinstance(result, dict):
            raise BackgroundError("invalid_backend_response", "Expected an object from the native helper.")
        if result.get("error"):
            error = result["error"]
            raise BackgroundError(error.get("code", "native_error"), error.get("message", "Native operation failed."))
        if completed.returncode:
            raise BackgroundError("native_error", "Native operation failed: " + completed.stderr[-1200:])
        return result


class BackgroundLab:
    def __init__(self, root=None, backend=None, clock=None):
        self.root = Path(root) if root else data_root()
        self.backend_override = backend
        self.clock = clock or time.time

    def _backend(self):
        if self.backend_override is not None:
            return self.backend_override
        path = self.root / "tools.json"
        if not path.is_file():
            raise BackgroundError("backend_missing", "Build the bundled native project and call configure_backend with its published executable path.")
        config = read_json(path)
        return NativeBackend(config["helper_path"], config["sha256"])

    def _call(self, request):
        result = self._backend().call(request)
        if not isinstance(result, dict):
            raise BackgroundError("invalid_backend_response", "Expected an object from the native helper.")
        if result.get("error"):
            error = result["error"]
            raise BackgroundError(error.get("code", "native_error"), error.get("message", "Native operation failed."))
        return result

    def background_status(self):
        configured = self.backend_override is not None or (self.root / "tools.json").is_file()
        result = {"version": VERSION, "platform": sys.platform, "data_root": str(self.root), "configured": configured,
                  "background_policy": "No activation, restore, global mouse/keyboard injection, or clipboard fallback.",
                  "observation_lifetime_seconds": OBSERVATION_SECONDS,
                  "foreground_mouse": "Available through the existing Computer Use skill when the user chooses that mode."}
        if configured:
            result["backend"] = self._call({"verb": "status"})
        return result

    def configure_backend(self, helper_path):
        path = Path(helper_path)
        if not path.is_absolute() or not path.is_file() or path.name.lower() != "backgroundcomputeruse.native.exe":
            raise BackgroundError("invalid_helper_path", "Select the trusted published BackgroundComputerUse.Native.exe using an absolute path.")
        digest = file_hash(path)
        status = NativeBackend(path, digest).call({"verb": "status"})
        if status.get("name") != "background-computer-use-native" or not status.get("version"):
            raise BackgroundError("invalid_backend_response", "Native helper identity/version is unavailable.")
        config = {"helper_path": str(path.resolve()), "sha256": digest, "version": status["version"], "configured_at": utc_now()}
        atomic_json(self.root / "tools.json", config)
        return {"configured": True, "config": config, "backend": status}

    def _path(self, kind, token):
        if not isinstance(token, str) or not TOKEN.fullmatch(token):
            raise BackgroundError("invalid_reference", "Use the opaque reference returned by this plugin.")
        return self.root / kind / (token + ".json")

    def _window(self, window_id):
        binding = read_json(self._path("windows", window_id))
        if self.clock() - binding["created_at_epoch"] > WINDOW_SECONDS:
            raise BackgroundError("stale_window", "Window reference expired; enumerate again.")
        return binding

    @staticmethod
    def _identity(binding):
        return {key: binding["window"][key] for key in ("hwnd", "pid", "start_time")}

    @staticmethod
    def _checked_window(result, binding):
        window = result.get("window", result)
        for key, value in BackgroundLab._identity(binding).items():
            if str(window.get(key)) != str(value):
                raise BackgroundError("window_identity_changed", "The window/process identity changed; enumerate again.")
        return window

    def list_background_windows(self):
        result = self._call({"verb": "list"})
        windows = []
        for window in result.get("windows", [])[:256]:
            if not all(key in window for key in ("hwnd", "pid", "start_time")):
                continue
            token = uuid.uuid4().hex
            binding = {"window_id": token, "window": window, "created_at_epoch": self.clock()}
            atomic_json(self._path("windows", token), binding)
            windows.append({"window_id": token, **window})
        return {"windows": windows, "desktop": result.get("desktop"), "truncated": len(result.get("windows", [])) > 256}

    def observe_background_window(self, window_id, include_image=True, depth=5, max_nodes=200):
        binding = self._window(window_id)
        request = {"verb": "inspect", **self._identity(binding), "depth": depth, "max_nodes": max_nodes}
        inspected = self._call(request)
        window = self._checked_window(inspected, binding)
        controls = inspected.get("elements", [])
        if not isinstance(controls, list):
            raise BackgroundError("invalid_backend_response", "Native controls must be a list.")
        controls = controls[:max_nodes]
        for control in controls:
            if control.get("is_password"):
                control.pop("value", None)
        image = None
        image_error = None
        capture = None
        if include_image:
            image_path = self.root / "captures" / (uuid.uuid4().hex + ".png")
            image_path.parent.mkdir(parents=True, exist_ok=True)
            try:
                capture = self._call({"verb": "capture", **self._identity(binding), "path": str(image_path), "timeout_ms": 4000})
                with image_path.open("rb") as stream:
                    header = stream.read(24)
                if len(header) != 24 or header[:8] != b"\x89PNG\r\n\x1a\n" or header[12:16] != b"IHDR":
                    raise BackgroundError("invalid_capture", "Native capture did not produce a PNG.")
                width, height = struct.unpack(">II", header[16:24])
                if not width or not height or width * height > MAX_PIXELS:
                    raise BackgroundError("invalid_capture", "Capture exceeds the pixel budget.")
                if capture.get("width") != width or capture.get("height") != height or capture.get("method") != "wgc-only":
                    raise BackgroundError("invalid_capture", "Capture dimensions or strict backend identity do not match.")
                image = {"path": str(image_path), "sha256": file_hash(image_path), "width": width, "height": height,
                         "method": capture["method"], "geometry": capture.get("geometry", {}), "captured_at": utc_now()}
            except (BackgroundError, OSError) as exc:
                image_error = {"code": getattr(exc, "code", "capture_failed"), "message": str(exc), "fallback_performed": False}
        before = inspected.get("before", inspected.get("desktop"))
        after = capture.get("after") if capture else inspected.get("after", inspected.get("desktop"))
        token = uuid.uuid4().hex
        observation = {
            "observation_id": token, "window_id": window_id, "window": window, "controls": controls,
            "truncated": bool(inspected.get("truncated")), "depth": depth, "max_nodes": max_nodes,
            "image": image, "image_error": image_error, "observed_at": utc_now(), "created_at_epoch": self.clock(),
            "consumed": False, "desktop_before": before, "desktop_after": after,
            "desktop_observation": desktop_delta(before, after),
        }
        atomic_json(self._path("observations", token), observation)
        return observation

    @contextmanager
    def _window_lock(self, binding):
        key = hashlib.sha256(json.dumps(self._identity(binding), sort_keys=True).encode()).hexdigest()
        path = self.root / "locks" / (key + ".lock")
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a+b") as handle:
            handle.seek(0, 2)
            if handle.tell() == 0:
                handle.write(b"0")
                handle.flush()
            handle.seek(0)
            try:
                if sys.platform == "win32":
                    import msvcrt
                    msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except (OSError, BlockingIOError) as exc:
                raise BackgroundError("window_busy", "Another plugin action owns this window; observe again after it finishes.") from exc
            try:
                yield
            finally:
                handle.seek(0)
                if sys.platform == "win32":
                    msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    fcntl.flock(handle, fcntl.LOCK_UN)

    def _observation(self, observation_id):
        observation = read_json(self._path("observations", observation_id))
        age = self.clock() - observation["created_at_epoch"]
        if age < 0 or age > OBSERVATION_SECONDS:
            raise BackgroundError("stale_observation", "Observe the window again before acting.")
        if observation.get("consumed"):
            raise BackgroundError("observation_consumed", "This observation already supplied one action. Use the returned fresh observation.")
        return observation

    def _claim(self, observation_id):
        observation = self._observation(observation_id)
        observation["consumed"] = True
        observation["consumed_at"] = utc_now()
        atomic_json(self._path("observations", observation_id), observation)
        return observation

    def _current(self, observation, binding):
        result = self._call({"verb": "inspect", **self._identity(binding), "depth": observation["depth"], "max_nodes": observation["max_nodes"]})
        current = self._checked_window(result, binding)
        for key in ("window_rect", "client_rect", "title", "class_name"):
            if current.get(key) != observation["window"].get(key):
                raise BackgroundError("window_changed", "Window geometry or context changed; observe again.")
        return result

    @staticmethod
    def _element(elements, element_id):
        matches = [item for item in elements if item.get("element_id") == element_id]
        if len(matches) != 1:
            raise BackgroundError("stale_element", "Control is missing or ambiguous; observe again.")
        return matches[0]

    @staticmethod
    def _hit_control(elements, x, y):
        candidates = []
        for element in elements:
            bounds = element.get("bounds", {})
            if (all(key in bounds for key in ("x", "y", "width", "height"))
                    and bounds["width"] > 0 and bounds["height"] > 0
                    and bounds["x"] <= x < bounds["x"] + bounds["width"]
                    and bounds["y"] <= y < bounds["y"] + bounds["height"]):
                candidates.append(element)
        return min(candidates, key=lambda item: (-item.get("depth", 0), item["bounds"]["width"] * item["bounds"]["height"])) if candidates else None

    def _verify_after(self, observation, action):
        result = {"action": action, "desktop_observation": desktop_delta(action.get("before"), action.get("after")),
                  "meaning": "Delivery/pattern execution is reported. Inspect the fresh observation to verify the intended result."}
        try:
            result["observation"] = self.observe_background_window(observation["window_id"], bool(observation.get("image")), observation["depth"], observation["max_nodes"])
        except BackgroundError as exc:
            result["verification_error"] = {"code": exc.code, "message": str(exc), "action_may_have_happened": True}
        return result

    def act_on_control(self, observation_id, element_id, action, value=None, direction=None):
        allowed = {"invoke", "set_value", "toggle", "select", "expand", "collapse", "scroll"}
        if action not in allowed:
            raise BackgroundError("unsupported_action", "Use a supported UI Automation pattern action.")
        if action == "set_value" and (not isinstance(value, str) or len(value) > 8192):
            raise BackgroundError("invalid_value", "Set-value needs text no longer than 8192 characters.")
        if action != "set_value" and value is not None:
            raise BackgroundError("invalid_value", "Value is only supported for set_value.")
        if action == "scroll" and direction not in ("up", "down", "left", "right"):
            raise BackgroundError("invalid_direction", "Scroll requires up, down, left or right.")
        if action != "scroll" and direction is not None:
            raise BackgroundError("invalid_direction", "Direction is only supported for scroll.")
        first = self._observation(observation_id)
        binding = self._window(first["window_id"])
        with self._window_lock(binding):
            observation = self._claim(observation_id)
            original = self._element(observation["controls"], element_id)
            current = self._element(self._current(observation, binding).get("elements", []), element_id)
            for key in ("runtime_id", "name", "automation_id", "control_type", "bounds"):
                if original.get(key) != current.get(key):
                    raise BackgroundError("stale_element", "Control identity or position changed; observe again.")
            if current.get("is_password"):
                raise BackgroundError("protected_control", "Password controls are excluded from background automation.")
            if not current.get("enabled", True):
                raise BackgroundError("disabled_control", "This control is disabled.")
            request = {
                "verb": "act", **self._identity(binding), "runtime_id": current["runtime_id"], "action": action,
                "expected": {key: current.get(key) for key in ("name", "automation_id", "control_type")},
                "expected_bounds": current.get("bounds"), "expected_title": observation["window"].get("title"),
                "expected_class_name": observation["window"].get("class_name"),
            }
            if value is not None:
                request["value"] = value
            if direction is not None:
                request["direction"] = direction
            executed = self._call(request)
            return self._verify_after(observation, executed)

    def click_background_window(self, observation_id, x, y, button="left"):
        if button not in ("left", "right") or type(x) is not int or type(y) is not int:
            raise BackgroundError("invalid_coordinates", "Use integer image pixels and left/right button.")
        first = self._observation(observation_id)
        binding = self._window(first["window_id"])
        with self._window_lock(binding):
            observation = self._claim(observation_id)
            image = observation.get("image")
            if not image or not 0 <= x < image["width"] or not 0 <= y < image["height"]:
                raise BackgroundError("invalid_coordinates", "Click needs pixels inside this observation's captured image.")
            if not Path(image["path"]).is_file() or file_hash(image["path"]) != image["sha256"]:
                raise BackgroundError("capture_changed", "The observed image changed; capture again.")
            geometry = image.get("geometry", {})
            screen = geometry.get("screen_rect")
            client = observation["window"].get("client_rect")
            if not geometry.get("geometry_verified") or not isinstance(screen, dict) or not isinstance(client, dict):
                raise BackgroundError("coordinate_mapping_unavailable", "Capture has no verified physical coordinate mapping. Use a semantic control action.")
            if screen.get("width") != image["width"] or screen.get("height") != image["height"]:
                raise BackgroundError("coordinate_mapping_unavailable", "Captured frame dimensions do not match the physical mapping.")
            current = self._current(observation, binding)
            screen_x, screen_y = screen["x"] + x, screen["y"] + y
            for control in current.get("elements", []):
                bounds = control.get("bounds", {})
                if (control.get("is_password") and all(key in bounds for key in ("x", "y", "width", "height"))
                        and bounds["x"] <= screen_x < bounds["x"] + bounds["width"]
                        and bounds["y"] <= screen_y < bounds["y"] + bounds["height"]):
                    raise BackgroundError("protected_control", "Password controls are excluded from background mouse actions.")
            previous_target = self._hit_control(observation["controls"], screen_x, screen_y)
            current_target = self._hit_control(current.get("elements", []), screen_x, screen_y)
            if previous_target is not None:
                if current_target is None or any(previous_target.get(key) != current_target.get(key)
                                                 for key in ("element_id", "runtime_id", "name", "automation_id", "control_type", "bounds")):
                    raise BackgroundError("stale_element", "The control under the image pixel changed; observe again.")
            if current_target is not None and not current_target.get("enabled", True):
                raise BackgroundError("disabled_control", "The control under this pixel is disabled.")
            client_x = screen["x"] + x - client["x"]
            client_y = screen["y"] + y - client["y"]
            if not 0 <= client_x < client["width"] or not 0 <= client_y < client["height"]:
                raise BackgroundError("outside_client", "Background messages are limited to the client content, not window chrome.")
            request = {"verb": "click", **self._identity(binding), "client_x": client_x, "client_y": client_y,
                       "button": button, "expected_window_rect": observation["window"].get("window_rect"),
                       "expected_client_rect": client, "expected_title": observation["window"].get("title"),
                       "expected_class_name": observation["window"].get("class_name")}
            executed = self._call(request)
            return self._verify_after(observation, executed)

    def prepare_foreground_mouse(self, observation_id):
        observation = self._observation(observation_id)
        binding = self._window(observation["window_id"])
        self._current(observation, binding)
        return {"input_executed": False, "mode": "foreground_mouse", "window": observation["window"],
                "requires_task_authorization": True, "existing_task_authorization_remains_valid": True,
                "next": "When the user chooses foreground mouse control, select the matching returned window through Computer Use's list_windows/get_window APIs, take a fresh Sky observation and use its mouse API. Sky IDs are opaque and its screenshot coordinates must not be substituted for this plugin's image pixels."}
