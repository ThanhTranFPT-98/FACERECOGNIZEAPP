"""Model-free tests: pass detection, gallery, message format and the station profile. Run: python -m unittest discover -s tests"""
import json
import tempfile
import unittest
from pathlib import Path

import numpy as np

from faceid_sim.config import Settings
from faceid_sim.debounce import NO_FACE, PassDetector
from faceid_sim.device import heartbeat_message, verify_message
from faceid_sim.gallery import STRANGER, Gallery

PROFILE = json.loads((Path(__file__).parents[1] / "station/faceid-sim.profile.json").read_text(encoding="utf-8"))


class Clock:
    def __init__(self): self.t = 1000.0
    def __call__(self): return self.t


def detector(clock, stable=3):
    return PassDetector(stable, cooldown=10, stranger_cooldown=15, stranger=STRANGER, clock=clock)


class PassDetectorTests(unittest.TestCase):
    def test_fires_once_after_stable_frames(self):
        clock = Clock(); d = detector(clock)
        self.assertEqual([d.observe("0007") for _ in range(6)], [None, None, "0007", None, None, None])

    def test_flicker_restarts_the_count(self):
        d = detector(Clock())
        self.assertEqual([d.observe(x) for x in ["0007", "0007", STRANGER, "0007", "0007", "0007"]], [None] * 5 + ["0007"])

    def test_same_person_inside_cooldown_is_the_same_pass(self):
        clock = Clock(); d = detector(clock)
        for _ in range(3): d.observe("0007")
        d.observe(NO_FACE); clock.t += 5
        self.assertEqual([d.observe("0007") for _ in range(3)], [None, None, None])
        d.observe(NO_FACE); clock.t += 6
        self.assertEqual([d.observe("0007") for _ in range(3)], [None, None, "0007"])

    def test_suppressed_presentation_does_not_fire_later_while_still_in_view(self):
        clock = Clock(); d = detector(clock)
        for _ in range(3): d.observe("0007")
        d.observe(NO_FACE); clock.t += 1
        for _ in range(3): d.observe("0007")
        clock.t += 60
        self.assertIsNone(d.observe("0007"))

    def test_other_person_is_not_blocked_by_cooldown(self):
        d = detector(Clock())
        for _ in range(3): d.observe("0007")
        self.assertEqual([d.observe("0008") for _ in range(3)], [None, None, "0008"])

    def test_stranger_has_its_own_cooldown(self):
        clock = Clock(); d = detector(clock)
        for _ in range(3): d.observe(STRANGER)
        d.observe(NO_FACE); clock.t += 12
        self.assertEqual([d.observe(STRANGER) for _ in range(3)], [None, None, None])


def unit(seed):
    v = np.random.default_rng(seed).normal(size=512).astype(np.float32)
    return v / np.linalg.norm(v)


class GalleryTests(unittest.TestCase):
    def test_enroll_match_reload_remove(self):
        with tempfile.TemporaryDirectory() as tmp:
            g = Gallery(Path(tmp))
            g.enroll(" ab7 ", "Nguyễn Văn A", [unit(1), unit(2)])
            self.assertEqual(g.match(unit(2), 0.5).pin, "AB7")
            self.assertIsNone(g.match(unit(3), 0.5).pin)
            again = Gallery(Path(tmp))
            self.assertEqual([p.name for p in again.people()], ["Nguyễn Văn A"])
            self.assertEqual(again.match(unit(1), 0.5).pin, "AB7")
            self.assertTrue(again.remove("ab7"))
            self.assertIsNone(Gallery(Path(tmp)).match(unit(1), 0.5).pin)

    def test_rejects_reserved_and_invalid_pins(self):
        with tempfile.TemporaryDirectory() as tmp:
            g = Gallery(Path(tmp))
            for pin in ["STRANGER", "a-b", "", "x" * 21]:
                with self.assertRaises(ValueError): g.enroll(pin, "", [unit(1)])
            with self.assertRaises(ValueError): g.enroll("0007", "", [])


class MessageTests(unittest.TestCase):
    def test_verify_message_carries_what_the_profile_reads(self):
        s = Settings()
        m = verify_message(s, "0007", 0.734, b"\xff\xd8jpeg")
        self.assertEqual(m["event"], PROFILE["match"][0]["equals"])
        self.assertEqual(m["device"]["sn"], s.device_id)
        self.assertEqual((m["pin"], m["score"]), ("0007", 73.4))
        self.assertRegex(m["time"], r"[+-]\d\d:\d\d$")      # offset present: the profile reads it as ISO
        self.assertTrue(m["photo"] and m["eventId"])
        for name, rule in PROFILE["fields"].items():
            path = rule if isinstance(rule, str) else rule["path"]
            node = m
            for part in path.removeprefix("$.").split("."):
                node = node[part]
            self.assertIsNotNone(node, name)

    def test_score_is_clamped_to_the_profile_scale(self):
        self.assertEqual(verify_message(Settings(), STRANGER, -0.2, b"")["score"], 0.0)
        self.assertEqual(verify_message(Settings(), "1", 1.3, b"")["score"], 100.0)

    def test_heartbeat_is_skipped_by_match_but_keeps_device_id(self):
        h = heartbeat_message(Settings())
        self.assertNotEqual(h["event"], PROFILE["match"][0]["equals"])
        self.assertEqual(PROFILE["fields"]["deviceId"], "$.device.sn")
        self.assertIn("sn", h["device"])


if __name__ == "__main__":
    unittest.main()
