from __future__ import annotations

from collections import Counter
import copy
import json
from pathlib import Path
import struct
import tempfile
import threading
import unittest
import zlib

from bcu.core import BackgroundError, BackgroundLab, desktop_delta


def png_bytes(width, height):
    """Produce a complete small RGBA PNG without optional image packages."""
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)

    rows = b"".join(b"\x00" + b"\x36\x78\xa1\xff" * width for _ in range(height))
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


class FakeBackend:
    """Stateful target app: calls and app outcomes can be inspected independently."""

    def __init__(self):
        self.window = {
            "hwnd": "0x12345", "pid": 31415, "start_time": "638973936000000000",
            "title": "Owned background test", "class_name": "FixtureWindow",
            "window_rect": {"x": -1200, "y": 80, "width": 120, "height": 100},
            "client_rect": {"x": -1192, "y": 108, "width": 104, "height": 64},
            "is_minimized": False,
        }
        self.controls = [
            {"element_id": "increment", "runtime_id": [42, 31415, 7], "name": "Increment",
             "automation_id": "Increment", "control_type": "Button", "enabled": True,
             "is_password": False, "patterns": ["invoke"],
             "bounds": {"x": -1180, "y": 120, "width": 40, "height": 20}},
            {"element_id": "value", "runtime_id": [42, 31415, 8], "name": "Test value",
             "automation_id": "TestValue", "control_type": "Edit", "enabled": True,
             "is_password": False, "patterns": ["set_value"], "value": "initial",
             "bounds": {"x": -1180, "y": 146, "width": 80, "height": 16}},
        ]
        self.desktop = {"foreground_hwnd": "0x98765", "cursor": {"x": 700, "y": 330}, "last_input_tick": 1234}
        self.calls = []
        self.counter = 0
        self.capture_method = "wgc-only"
        self.capture_error = None
        self.capture_geometry_verified = True
        self.capture_geometry_width = None
        self.capture_bytes = None
        self.surfaces = []
        self.surface_capture_error = None
        self.action_error = None
        self.action_after = None
        self.restore_focus_result = None
        self.action_entered = None
        self.release_action = None

    @property
    def mutations(self):
        return [request for request in self.calls if request["verb"] in ("act", "click")]

    def call(self, request):
        self.calls.append(copy.deepcopy(request))
        verb = request["verb"]
        if verb == "status":
            return {"version": "test-fixture", "desktop": copy.deepcopy(self.desktop)}
        if verb == "list":
            return {"windows": [copy.deepcopy(self.window)], "desktop": copy.deepcopy(self.desktop)}
        if verb == "inspect":
            return {"window": copy.deepcopy(self.window), "elements": copy.deepcopy(self.controls),
                    "surfaces": copy.deepcopy(self.surfaces),
                    "counter": self.counter, "before": copy.deepcopy(self.desktop),
                    "after": copy.deepcopy(self.desktop), "truncated": False}
        if verb == "capture":
            surface = None
            if request.get("surface_hwnd") is not None:
                if self.surface_capture_error:
                    raise self.surface_capture_error
                matches = [item for item in self.surfaces if item["hwnd"] == request["surface_hwnd"]
                           and item["pid"] == request["surface_pid"] and item["start_time"] == request["surface_start_time"]]
                if len(matches) != 1:
                    raise BackgroundError("stale_surface", "Fixture surface identity changed.")
                surface = copy.deepcopy(matches[0])
                rect = copy.deepcopy(surface["window_rect"])
            else:
                if self.capture_error:
                    raise self.capture_error
                rect = copy.deepcopy(self.window["window_rect"])
            width = rect["width"]
            height = rect["height"]
            Path(request["path"]).write_bytes(self.capture_bytes if self.capture_bytes is not None and surface is None else png_bytes(width, height))
            if self.capture_geometry_width is not None:
                rect["width"] = self.capture_geometry_width
            return {"width": width, "height": height, "method": self.capture_method,
                    "geometry": {"geometry_verified": self.capture_geometry_verified, "screen_rect": rect,
                                 "client_rect": copy.deepcopy(surface["client_rect"] if surface else self.window["client_rect"])},
                    "surface": surface,
                    "before": copy.deepcopy(self.desktop), "after": copy.deepcopy(self.desktop)}
        if verb in ("act", "click"):
            desktop_before = copy.deepcopy(self.desktop)
            if self.action_entered is not None:
                self.action_entered.set()
                if not self.release_action.wait(5):
                    raise AssertionError("Test failed to release its owned backend call.")
            if self.action_error:
                raise self.action_error
            if verb == "act" and request["action"] == "set_value":
                self.controls[1]["value"] = request["value"]
            else:
                self.counter += 1
            foreground_before = {
                "hwnd": desktop_before["foreground_hwnd"], "pid": 27182, "start_time": "638973936000000001",
                "title": "Previous foreground", "class_name": "FixtureForeground", "session_id": 1,
                "virtual_desktop_id": "00000000-0000-0000-0000-000000000001", "on_current_virtual_desktop": True,
            }
            desktop_after = copy.deepcopy(self.action_after or desktop_before)
            if self.action_after is not None:
                self.desktop = copy.deepcopy(desktop_after)
            focus_restoration = None
            if request.get("restore_foreground") and self.restore_focus_result is not None:
                focus_restoration = copy.deepcopy(self.restore_focus_result)
                self.desktop = copy.deepcopy(focus_restoration.get("after", self.desktop))
            return {"executed": True, "method": "fixture-pattern" if verb == "act" else "fixture-message",
                    "before": desktop_before, "after": copy.deepcopy(self.desktop), "action_after": desktop_after,
                    "foreground_before": foreground_before,
                    "action_process_path": r"C:\\Program Files\\Oracle\\VirtualBox\\VirtualBox.exe",
                    "minimize_activating_window": bool(request.get("minimize_activating_window")),
                    "focus_restoration": focus_restoration,
                    "counter": self.counter}
        if verb == "restore_foreground":
            if self.restore_focus_result is not None:
                self.desktop = copy.deepcopy(self.restore_focus_result.get("after", self.desktop))
                return copy.deepcopy(self.restore_focus_result)
            before = copy.deepcopy(self.desktop)
            if self.desktop.get("foreground_hwnd") != request["expected_foreground_hwnd"]:
                return {"attempted": False, "restored": False, "reason": "foreground_changed_again", "before": before, "after": before}
            if self.desktop.get("cursor") != request["expected_cursor"]:
                return {"attempted": False, "restored": False, "reason": "cursor_moved_since_observation", "before": before, "after": before}
            user_input_since_observation = self.desktop.get("last_input_tick") != request["expected_last_input_tick"]
            self.desktop["foreground_hwnd"] = request["previous_foreground"]["hwnd"]
            after = copy.deepcopy(self.desktop)
            return {"attempted": True, "restored": True, "reason": "restored",
                    "user_input_since_observation": user_input_since_observation, "before": before, "after": after}
        raise AssertionError("Unpermitted backend route: " + str(request))


class BackgroundCoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.backend = FakeBackend()
        self.now = 1000.0
        self.lab = BackgroundLab(root=self.root, backend=self.backend, clock=lambda: self.now)
        self.window_id = self.lab.list_background_windows()["windows"][0]["window_id"]

    def tearDown(self):
        self.temp.cleanup()

    def observe(self, image=False):
        return self.lab.observe_background_window(self.window_id, include_image=image)

    def assert_blocked(self, code, function):
        count = len(self.backend.mutations)
        with self.assertRaises(BackgroundError) as raised:
            function()
        self.assertEqual(raised.exception.code, code)
        self.assertEqual(len(self.backend.mutations), count, "Rejected input reached the app mutation route.")

    def test_one_semantic_action_returns_new_observation_and_old_token_cannot_replay(self):
        before = self.observe()
        result = self.lab.act_on_control(before["observation_id"], "value", "set_value", value="accepted text")
        self.assertEqual(self.backend.controls[1]["value"], "accepted text")
        current = result["observation"]
        self.assertNotEqual(current["observation_id"], before["observation_id"])
        self.assertEqual(next(e for e in current["controls"] if e["element_id"] == "value")["value"], "accepted text")
        self.assertTrue(result["desktop_observation"]["preservation_verified"])
        self.assert_blocked("observation_consumed", lambda: self.lab.act_on_control(before["observation_id"], "increment", "invoke"))
        self.lab.act_on_control(current["observation_id"], "increment", "invoke")
        self.assertEqual(self.backend.counter, 1)

    def test_observation_consumption_is_durable_across_server_instances(self):
        observed = self.observe()
        other = BackgroundLab(root=self.root, backend=self.backend, clock=lambda: self.now)
        other.act_on_control(observed["observation_id"], "increment", "invoke")
        self.assert_blocked("observation_consumed", lambda: self.lab.act_on_control(observed["observation_id"], "increment", "invoke"))
        self.assertEqual(self.backend.counter, 1)

    def test_expired_or_future_observation_cannot_mutate(self):
        for elapsed in (31, -1):
            with self.subTest(elapsed=elapsed):
                self.now = 1000.0
                observed = self.observe()
                self.now += elapsed
                self.assert_blocked("stale_observation", lambda: self.lab.act_on_control(observed["observation_id"], "increment", "invoke"))

    def test_invalid_reference_and_expired_window_never_reach_mutation(self):
        self.assert_blocked("invalid_reference", lambda: self.lab.act_on_control("../../other", "increment", "invoke"))
        self.now += 601
        self.assert_blocked("stale_window", lambda: self.observe())

    def test_recycled_window_or_process_identity_is_rejected(self):
        original = copy.deepcopy(self.backend.window)
        for field, changed in (("hwnd", "0x54321"), ("pid", 31416), ("start_time", "638973936000000001")):
            with self.subTest(field=field):
                self.backend.window = copy.deepcopy(original)
                observed = self.observe()
                self.backend.window[field] = changed
                self.assert_blocked("window_identity_changed", lambda: self.lab.act_on_control(observed["observation_id"], "increment", "invoke"))

    def test_window_geometry_title_and_class_changes_are_rejected(self):
        original = copy.deepcopy(self.backend.window)
        for field in ("window_rect", "client_rect", "title", "class_name"):
            with self.subTest(field=field):
                self.backend.window = copy.deepcopy(original)
                observed = self.observe()
                if field.endswith("_rect"):
                    self.backend.window[field]["x"] += 2
                else:
                    self.backend.window[field] += " changed"
                self.assert_blocked("window_changed", lambda: self.lab.act_on_control(observed["observation_id"], "increment", "invoke"))

    def test_changed_control_identity_or_bounds_is_rejected(self):
        original = copy.deepcopy(self.backend.controls)
        for field in ("runtime_id", "name", "automation_id", "control_type", "bounds"):
            with self.subTest(field=field):
                self.backend.controls = copy.deepcopy(original)
                observed = self.observe()
                current = self.backend.controls[0]
                if field == "runtime_id":
                    current[field][-1] += 1
                elif field == "bounds":
                    current[field]["width"] += 1
                else:
                    current[field] += " changed"
                self.assert_blocked("stale_element", lambda: self.lab.act_on_control(observed["observation_id"], "increment", "invoke"))

    def test_missing_and_ambiguous_control_is_rejected(self):
        original = copy.deepcopy(self.backend.controls)
        for replacement in ([], [original[0], copy.deepcopy(original[0])]):
            with self.subTest(count=len(replacement)):
                self.backend.controls = copy.deepcopy(original)
                observed = self.observe()
                self.backend.controls = copy.deepcopy(replacement)
                self.assert_blocked("stale_element", lambda: self.lab.act_on_control(observed["observation_id"], "increment", "invoke"))

    def test_password_value_is_redacted_and_semantic_action_is_rejected(self):
        self.backend.controls[1].update(is_password=True, value="fixture-password-never-persist")
        observed = self.observe()
        self.assertNotIn("fixture-password-never-persist", json.dumps(observed))
        stored = (self.root / "observations" / (observed["observation_id"] + ".json")).read_text()
        self.assertNotIn("fixture-password-never-persist", stored)
        self.assert_blocked("protected_control", lambda: self.lab.act_on_control(observed["observation_id"], "value", "set_value", value="replacement"))

    def test_disabled_control_is_rejected(self):
        self.backend.controls[0]["enabled"] = False
        observed = self.observe()
        self.assert_blocked("disabled_control", lambda: self.lab.act_on_control(observed["observation_id"], "increment", "invoke"))

    def test_failed_strict_capture_keeps_semantic_observation_usable(self):
        self.backend.capture_error = BackgroundError("capture_unavailable", "Fixture has no WGC frame.")
        observed = self.observe(image=True)
        self.assertIsNone(observed["image"])
        self.assertEqual(observed["image_error"]["code"], "capture_unavailable")
        self.assertFalse(observed["image_error"]["fallback_performed"])
        self.assert_blocked("invalid_coordinates", lambda: self.lab.click_background_window(observed["observation_id"], 25, 40))
        fresh = self.observe(image=True)
        self.lab.act_on_control(fresh["observation_id"], "value", "set_value", value="semantic-only")
        self.assertEqual(self.backend.controls[1]["value"], "semantic-only")
        self.assertLessEqual(set(r["verb"] for r in self.backend.calls), {"list", "inspect", "capture", "act"})

    def test_child_process_surface_capture_and_click_work_when_bound_parent_is_minimized(self):
        self.backend.window["window_rect"] = {"x": -32000, "y": -32000, "width": 199, "height": 34}
        self.backend.window["client_rect"] = {"x": -32000, "y": -32000, "width": 0, "height": 0}
        self.backend.window["is_minimized"] = True
        self.backend.capture_error = BackgroundError("window_minimized", "Parent is minimized.")
        self.backend.surfaces = [{
            "hwnd": "90001", "pid": 27182, "start_time": "638973936000000001", "title": "Embedded surface",
            "class_name": "WebViewChild", "route": "uia_provider_process_descendant",
            "window_rect": {"x": 10, "y": 20, "width": 80, "height": 60},
            "client_rect": {"x": 10, "y": 20, "width": 80, "height": 60},
        }]
        observed = self.observe(image=True)
        self.assertIsNone(observed["image"])
        self.assertEqual(observed["image_error"]["code"], "window_minimized")
        self.assertEqual(len(observed["surface_images"]), 1)
        self.assertIsNotNone(observed["surface_images"][0]["image"])
        self.assertEqual(self.backend.calls[-1]["surface_hwnd"], "90001")

        self.lab.click_background_window(observed["observation_id"], 20, 20, surface_hwnd="90001")
        click = self.backend.mutations[0]
        self.assertEqual((click["screen_x"], click["screen_y"]), (30, 40))
        self.assertEqual(click["surface_hwnd"], "90001")
        self.assertEqual(click["surface_pid"], 27182)

    def test_capture_backend_cannot_silently_change_to_printwindow(self):
        self.backend.capture_method = "printwindow"
        observed = self.observe(image=True)
        self.assertIsNone(observed["image"])
        self.assertEqual(observed["image_error"]["code"], "invalid_capture")
        self.assertFalse(observed["image_error"]["fallback_performed"])
        self.assert_blocked("invalid_coordinates", lambda: self.lab.click_background_window(observed["observation_id"], 30, 40))

    def test_invalid_png_does_not_create_clickable_evidence(self):
        self.backend.capture_bytes = b"Not a PNG"
        observed = self.observe(image=True)
        self.assertIsNone(observed["image"])
        self.assertEqual(observed["image_error"]["code"], "invalid_capture")

    def test_capture_hash_change_prevents_click(self):
        observed = self.observe(image=True)
        path = Path(observed["image"]["path"])
        path.write_bytes(path.read_bytes() + b"changed evidence")
        self.assert_blocked("capture_changed", lambda: self.lab.click_background_window(observed["observation_id"], 30, 40))

    def test_control_moved_replaced_or_covered_inside_same_window_prevents_pixel_click(self):
        original = copy.deepcopy(self.backend.controls)
        for change in ("moved", "replaced", "overlay"):
            with self.subTest(change=change):
                self.backend.controls = copy.deepcopy(original)
                observed = self.observe(image=True)
                if change == "moved":
                    self.backend.controls[0]["bounds"]["x"] += 20
                elif change == "replaced":
                    self.backend.controls[0]["element_id"] = "replacement-button"
                    self.backend.controls[0]["runtime_id"] = [42, 31415, 90]
                else:
                    overlay = copy.deepcopy(self.backend.controls[0])
                    overlay.update(element_id="overlay-button", runtime_id=[42, 31415, 91],
                                   name="Different action", automation_id="Overlay", depth=2,
                                   bounds={"x": -1174, "y": 118, "width": 16, "height": 16})
                    self.backend.controls.append(overlay)
                self.assert_blocked("stale_element", lambda: self.lab.click_background_window(observed["observation_id"], 30, 40))
        self.assertEqual(self.backend.counter, 0)

    def test_control_disabled_after_observation_prevents_pixel_click(self):
        observed = self.observe(image=True)
        self.backend.controls[0]["enabled"] = False
        self.assert_blocked("disabled_control", lambda: self.lab.click_background_window(observed["observation_id"], 30, 40))
        self.assertEqual(self.backend.counter, 0)

    def test_verified_physical_image_pixels_map_to_client_with_negative_origin(self):
        observed = self.observe(image=True)
        result = self.lab.click_background_window(observed["observation_id"], 30, 40)
        request = self.backend.mutations[0]
        self.assertEqual((request["client_x"], request["client_y"]), (22, 12))
        self.assertEqual(request["button"], "left")
        self.assertEqual(self.backend.counter, 1)
        self.assertTrue(result["desktop_observation"]["preservation_verified"])

    def test_unverified_or_scaled_capture_geometry_prevents_click(self):
        for bad_width in (None, 60):
            with self.subTest(width=bad_width):
                self.backend.capture_geometry_verified = bad_width is not None
                self.backend.capture_geometry_width = bad_width
                observed = self.observe(image=True)
                self.assert_blocked("coordinate_mapping_unavailable", lambda: self.lab.click_background_window(observed["observation_id"], 30, 40))

    def test_window_chrome_and_out_of_image_pixels_are_rejected(self):
        for x, y, code in ((5, 5, "outside_client"), (-1, 40, "invalid_coordinates"), (120, 40, "invalid_coordinates"), (30, 100, "invalid_coordinates"), (True, 40, "invalid_coordinates")):
            with self.subTest(x=x, y=y):
                observed = self.observe(image=True)
                self.assert_blocked(code, lambda: self.lab.click_background_window(observed["observation_id"], x, y))

    def test_password_bounds_prevent_background_pixel_click(self):
        self.backend.controls[0]["is_password"] = True
        observed = self.observe(image=True)
        self.assert_blocked("protected_control", lambda: self.lab.click_background_window(observed["observation_id"], 30, 45))

    def test_control_becoming_password_after_observation_prevents_pixel_click(self):
        observed = self.observe(image=True)
        self.backend.controls[0]["is_password"] = True
        self.assert_blocked("protected_control", lambda: self.lab.click_background_window(observed["observation_id"], 30, 45))

    def test_backend_timeout_consumes_observation_and_does_not_retry_action(self):
        observed = self.observe()
        self.backend.action_error = BackgroundError("backend_timeout", "Outcome is unknown.")
        with self.assertRaises(BackgroundError) as raised:
            self.lab.act_on_control(observed["observation_id"], "increment", "invoke")
        self.assertEqual(raised.exception.code, "backend_timeout")
        self.assertEqual(len(self.backend.mutations), 1)
        self.assert_blocked("observation_consumed", lambda: self.lab.act_on_control(observed["observation_id"], "increment", "invoke"))

    def test_unsupported_pattern_does_not_fall_back_to_mouse_or_keyboard(self):
        observed = self.observe()
        self.backend.action_error = BackgroundError("unsupported_pattern", "No programmatic pattern.")
        with self.assertRaises(BackgroundError) as raised:
            self.lab.act_on_control(observed["observation_id"], "increment", "invoke")
        self.assertEqual(raised.exception.code, "unsupported_pattern")
        self.assertEqual(Counter(r["verb"] for r in self.backend.mutations), {"act": 1})
        self.assertLessEqual(set(r["verb"] for r in self.backend.calls), {"list", "inspect", "act"})

    def test_provider_foreground_change_is_reported_without_restoration(self):
        observed = self.observe()
        self.backend.action_after = copy.deepcopy(self.backend.desktop)
        self.backend.action_after["foreground_hwnd"] = "0x22222"
        result = self.lab.act_on_control(observed["observation_id"], "increment", "invoke")
        delta = result["desktop_observation"]
        self.assertTrue(delta["foreground_changed"])
        self.assertFalse(delta["cursor_changed"])
        self.assertFalse(delta["preservation_verified"])
        self.assertEqual(self.backend.counter, 1)
        self.assertLessEqual(set(r["verb"] for r in self.backend.calls), {"list", "inspect", "act"})

    def test_opt_in_focus_handback_restores_previous_window_after_fresh_observation(self):
        observed = self.observe()
        self.backend.action_after = copy.deepcopy(self.backend.desktop)
        self.backend.action_after["foreground_hwnd"] = "0x22222"
        result = self.lab.act_on_control(observed["observation_id"], "increment", "invoke", restore_foreground=True)
        self.assertTrue(result["focus_restoration"]["restored"])
        self.assertTrue(result["focus_restoration"]["foreground_verified"])
        self.assertEqual(self.backend.desktop["foreground_hwnd"], "0x98765")
        restore_call = next(call for call in self.backend.calls if call["verb"] == "restore_foreground")
        self.assertEqual(restore_call["expected_foreground_hwnd"], "0x22222")
        self.assertEqual(restore_call["previous_foreground"]["hwnd"], "0x98765")
        self.assertTrue(result["focus_restoration"]["foreground_observation"]["preservation_verified"])

    def test_native_in_action_focus_handback_is_used_before_fresh_capture(self):
        observed = self.observe()
        self.backend.action_after = copy.deepcopy(self.backend.desktop)
        self.backend.action_after["foreground_hwnd"] = "0x22222"
        restored = copy.deepcopy(self.backend.desktop)
        self.backend.restore_focus_result = {"attempted": True, "restored": True, "reason": "restored",
                                             "before": self.backend.action_after, "after": restored}
        result = self.lab.act_on_control(observed["observation_id"], "increment", "invoke", restore_foreground=True)
        self.assertTrue(result["focus_restoration"]["restored"])
        self.assertEqual(result["focus_restoration"]["attempts"][0]["phase"], "inside_action")
        self.assertFalse(any(call["verb"] == "restore_foreground" for call in self.backend.calls))
        action_index = next(i for i, call in enumerate(self.backend.calls) if call["verb"] == "act")
        fresh_capture_index = max(i for i, call in enumerate(self.backend.calls) if call["verb"] == "inspect")
        self.assertLess(action_index, fresh_capture_index)

    def test_desktop_restore_is_success_even_if_foreground_snapshot_lags(self):
        observed = self.observe()
        self.backend.action_after = copy.deepcopy(self.backend.desktop)
        self.backend.action_after["foreground_hwnd"] = "0x22222"
        self.backend.restore_focus_result = {
            "attempted": True, "restored": True, "desktop_restored": True,
            "foreground_verified": False, "reason": "desktop_restored_foreground_pending",
            "before": copy.deepcopy(self.backend.action_after), "after": copy.deepcopy(self.backend.action_after),
        }
        result = self.lab.act_on_control(observed["observation_id"], "increment", "invoke", restore_foreground=True)
        self.assertTrue(result["focus_restoration"]["restored"])
        self.assertTrue(result["focus_restoration"]["desktop_restored"])
        self.assertFalse(result["focus_restoration"]["foreground_verified"])
        self.assertEqual([attempt["phase"] for attempt in result["focus_restoration"]["attempts"]],
                         ["inside_action", "immediate_retry", "after_observation"])
        call_indices = [i for i, call in enumerate(self.backend.calls) if call["verb"] == "restore_foreground"]
        first_fresh_observation = next(i for i, call in enumerate(self.backend.calls)
                                       if call["verb"] == "inspect" and i > call_indices[0])
        self.assertLess(call_indices[0], first_fresh_observation)

    def test_opt_in_focus_handback_returns_to_saved_window_during_typing(self):
        observed = self.observe()
        self.backend.action_after = copy.deepcopy(self.backend.desktop)
        self.backend.action_after["foreground_hwnd"] = "0x22222"
        self.backend.action_after["last_input_tick"] += 1
        result = self.lab.act_on_control(observed["observation_id"], "increment", "invoke", restore_foreground=True)
        self.assertTrue(result["focus_restoration"]["restored"])
        self.assertTrue(result["focus_restoration"]["attempts"][0]["user_input_since_observation"])
        self.assertEqual(self.backend.desktop["foreground_hwnd"], "0x98765")

    def test_opt_in_focus_handback_still_stops_after_pointer_movement(self):
        observed = self.observe()
        self.backend.action_after = copy.deepcopy(self.backend.desktop)
        self.backend.action_after["foreground_hwnd"] = "0x22222"
        self.backend.action_after["cursor"]["x"] += 1
        result = self.lab.act_on_control(observed["observation_id"], "increment", "invoke", restore_foreground=True)
        self.assertFalse(result["focus_restoration"]["restored"])
        self.assertEqual(result["focus_restoration"]["reason"], "cursor_moved_since_observation")
        self.assertEqual(self.backend.desktop["foreground_hwnd"], "0x22222")

    def test_delayed_handback_does_not_override_a_different_foreground_window(self):
        observed = self.observe()
        self.backend.action_after = copy.deepcopy(self.backend.desktop)
        self.backend.action_after["foreground_hwnd"] = "0x22222"
        other = copy.deepcopy(self.backend.action_after)
        other["foreground_hwnd"] = "0x33333"
        self.backend.restore_focus_result = {
            "attempted": False, "restored": False, "reason": "foreground_changed_again",
            "before": copy.deepcopy(other), "after": copy.deepcopy(other),
        }
        result = self.lab.act_on_control(observed["observation_id"], "increment", "invoke", restore_foreground=True)
        self.assertFalse(result["focus_restoration"]["restored"])
        self.assertEqual(result["focus_restoration"]["reason"], "foreground_changed_again")
        self.assertFalse(any(call["verb"] == "restore_foreground" for call in self.backend.calls))

    def test_minimize_fallback_is_explicit_and_passed_to_native_handback(self):
        observed = self.observe()
        self.backend.action_after = copy.deepcopy(self.backend.desktop)
        self.backend.action_after["foreground_hwnd"] = "0x22222"
        self.backend.restore_focus_result = {
            "attempted": True, "restored": False, "foreground_verified": False,
            "reason": "set_foreground_denied", "before": copy.deepcopy(self.backend.action_after),
            "after": copy.deepcopy(self.backend.action_after),
        }
        with self.assertRaises(BackgroundError) as caught:
            self.lab.act_on_control(observed["observation_id"], "increment", "invoke",
                                    minimize_activating_window=True)
        self.assertEqual(caught.exception.code, "invalid_arguments")
        self.assertFalse(any(call["verb"] == "act" for call in self.backend.calls))

        observed = self.observe()
        result = self.lab.act_on_control(observed["observation_id"], "increment", "invoke",
                                         restore_foreground=True, minimize_activating_window=True)
        action_call = next(call for call in self.backend.calls if call["verb"] == "act")
        retry_call = [call for call in self.backend.calls if call["verb"] == "restore_foreground"][-1]
        self.assertTrue(action_call["minimize_activating_window"])
        self.assertTrue(retry_call["minimize_activating_window"])
        self.assertEqual(retry_call["action_process_path"], r"C:\\Program Files\\Oracle\\VirtualBox\\VirtualBox.exe")
        self.assertTrue(result["focus_restoration"]["requested"])

    def test_foreground_handoff_sends_no_input_and_does_not_consume_observation(self):
        observed = self.observe()
        result = self.lab.prepare_foreground_mouse(observed["observation_id"])
        self.assertFalse(result["input_executed"])
        self.assertTrue(result["requires_task_authorization"])
        self.assertTrue(result["existing_task_authorization_remains_valid"])
        self.assertEqual(self.backend.mutations, [])
        self.lab.act_on_control(observed["observation_id"], "increment", "invoke")
        self.assertEqual(self.backend.counter, 1)

    def test_parallel_instances_cannot_mutate_same_window_at_once(self):
        first = self.observe()
        second = self.observe()
        entered, release = threading.Event(), threading.Event()
        self.backend.action_entered = entered
        self.backend.release_action = release
        errors = []

        def run_first():
            try:
                self.lab.act_on_control(first["observation_id"], "increment", "invoke")
            except BaseException as exc:
                errors.append(exc)

        thread = threading.Thread(target=run_first)
        thread.start()
        try:
            self.assertTrue(entered.wait(3), "First action never reached its owned target.")
            other = BackgroundLab(root=self.root, backend=self.backend, clock=lambda: self.now)
            self.assert_blocked("window_busy", lambda: other.act_on_control(second["observation_id"], "increment", "invoke"))
        finally:
            release.set()
            thread.join(5)
        self.assertFalse(thread.is_alive())
        self.assertEqual(errors, [])
        self.assertEqual(self.backend.counter, 1)
        self.backend.action_entered = self.backend.release_action = None
        self.lab.act_on_control(second["observation_id"], "increment", "invoke")
        self.assertEqual(self.backend.counter, 2)

    def test_missing_desktop_fields_cannot_certify_preservation(self):
        for before, after in ((None, None), ({}, {}), ({"cursor": {}}, {"cursor": {}})):
            with self.subTest(before=before):
                self.assertFalse(desktop_delta(before, after)["preservation_verified"])


if __name__ == "__main__":
    unittest.main()
