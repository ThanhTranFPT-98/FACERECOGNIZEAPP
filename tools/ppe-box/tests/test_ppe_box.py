"""Unit tests for the parts that decide what the station is told. No camera, no model, no network."""
import os
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from ppe_box.config import Settings                      # noqa: E402
from ppe_box.geometry import anchor, covered, in_polygon, iou, normalise   # noqa: E402
from ppe_box.rois import Roi, RoiStore                   # noqa: E402
from ppe_box.station import ppe_message, roi_payload     # noqa: E402
from ppe_box.tracker import Tracker                      # noqa: E402


class GeometryTests(unittest.TestCase):
    def test_box_is_clipped_to_the_frame(self):
        self.assertEqual(normalise((-0.2, 0.5, 0.5, 0.9)), (0.0, 0.5, 0.3, 0.5))

    def test_iou_of_identical_boxes_is_one(self):
        self.assertAlmostEqual(iou((0.1, 0.1, 0.2, 0.2), (0.1, 0.1, 0.2, 0.2)), 1.0)

    def test_iou_of_separate_boxes_is_zero(self):
        self.assertEqual(iou((0, 0, 0.1, 0.1), (0.5, 0.5, 0.1, 0.1)), 0.0)

    def test_covered_measures_the_inner_box(self):
        # Half of the small box lies inside the big one.
        self.assertAlmostEqual(covered((0.4, 0.0, 0.2, 0.1), (0.0, 0.0, 0.5, 1.0)), 0.5)

    def test_anchor_is_the_feet(self):
        self.assertEqual(anchor((0.2, 0.1, 0.4, 0.5)), (0.4, 0.6))

    def test_point_in_polygon(self):
        square = [(0.2, 0.2), (0.8, 0.2), (0.8, 0.8), (0.2, 0.8)]
        self.assertTrue(in_polygon((0.5, 0.5), square))
        self.assertFalse(in_polygon((0.9, 0.5), square))
        self.assertFalse(in_polygon((0.5, 0.9), square))

    def test_concave_polygon_excludes_the_notch(self):
        # An arrow-shaped lane: the dip in the middle is outside.
        arrow = [(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.5, 0.4), (0.0, 1.0)]
        self.assertTrue(in_polygon((0.1, 0.5), arrow))
        self.assertFalse(in_polygon((0.5, 0.8), arrow))


class TrackerTests(unittest.TestCase):
    def test_a_moving_person_keeps_their_id(self):
        tracker = Tracker(ttl=10, min_iou=0.3)
        first = tracker.update([(0.40, 0.2, 0.2, 0.6)], now=100.0)
        second = tracker.update([(0.43, 0.2, 0.2, 0.6)], now=100.5)
        self.assertEqual(first[0].id, second[0].id)
        self.assertEqual(second[0].frames, 2)

    def test_a_jump_across_the_frame_is_a_new_person(self):
        tracker = Tracker(ttl=10, min_iou=0.3)
        first = tracker.update([(0.0, 0.2, 0.2, 0.6)], now=100.0)
        second = tracker.update([(0.8, 0.2, 0.2, 0.6)], now=100.5)
        self.assertNotEqual(first[0].id, second[0].id)

    def test_an_id_is_never_reused_after_the_track_expires(self):
        tracker = Tracker(ttl=1.0, min_iou=0.3)
        first = tracker.update([(0.4, 0.2, 0.2, 0.6)], now=100.0)
        again = tracker.update([(0.4, 0.2, 0.2, 0.6)], now=200.0)
        self.assertNotEqual(first[0].id, again[0].id)

    def test_two_people_keep_separate_ids(self):
        tracker = Tracker(ttl=10, min_iou=0.3)
        tracker.update([(0.1, 0.2, 0.2, 0.6), (0.7, 0.2, 0.2, 0.6)], now=100.0)
        second = tracker.update([(0.12, 0.2, 0.2, 0.6), (0.72, 0.2, 0.2, 0.6)], now=100.5)
        self.assertEqual(len({t.id for t in second}), 2)

    def test_items_are_remembered_between_frames(self):
        tracker = Tracker(ttl=10, min_iou=0.3)
        tracker.update([(0.4, 0.2, 0.2, 0.6)], [{"helmet": True}], now=100.0)
        # The next frame judged nothing; the last verdict must stand rather than vanish.
        second = tracker.update([(0.4, 0.2, 0.2, 0.6)], [{}], now=100.4)
        self.assertEqual(second[0].items, {"helmet": True})


class RoiTests(unittest.TestCase):
    def test_an_empty_polygon_covers_the_whole_frame(self):
        self.assertTrue(Roi("L1", ()).contains((0.01, 0.99)))

    def test_parsing_the_station_payload(self):
        rois = RoiStore._parse({"rois": [
            {"roiId": "L3", "label": "Cửa lò vào", "polygon": [[0.1, 0.1], [0.9, 0.1], [0.9, 0.9]]},
            {"roiId": "", "polygon": []},
        ]})
        self.assertEqual([r.id for r in rois], ["L3", ""])
        self.assertEqual(len(rois[0].polygon), 3)
        self.assertEqual(rois[1].polygon, ())

    def test_a_malformed_polygon_falls_back_to_the_whole_frame(self):
        rois = RoiStore._parse({"rois": [{"roiId": "L1", "polygon": [["x", 0.1], [0.9, 0.1]]}]})
        self.assertEqual(rois[0].polygon, ())

    def test_people_are_assigned_by_where_they_stand(self):
        store = RoiStore(Settings())
        store._rois = (Roi("LEFT", ((0.0, 0.0), (0.5, 0.0), (0.5, 1.0), (0.0, 1.0))),
                       Roi("RIGHT", ((0.5, 0.0), (1.0, 0.0), (1.0, 1.0), (0.5, 1.0))))
        # Head leaning right, feet still on the left: the lane is decided by the feet.
        self.assertEqual(store.assign((0.30, 0.1, 0.3, 0.8)).id, "LEFT")
        self.assertEqual(store.assign((0.6, 0.1, 0.2, 0.8)).id, "RIGHT")

    def test_someone_outside_every_region_belongs_to_none(self):
        store = RoiStore(Settings())
        store._rois = (Roi("MID", ((0.4, 0.4), (0.6, 0.4), (0.6, 0.6), (0.4, 0.6))),)
        self.assertIsNone(store.assign((0.0, 0.0, 0.05, 0.05)))


class MessageTests(unittest.TestCase):
    def test_the_message_matches_what_the_station_parses(self):
        settings = Settings()
        message = ppe_message(settings, [roi_payload("L3", [{"trackId": "t-1", "items": {"helmet": False}}])])
        self.assertEqual(message["deviceId"], settings.device_id)
        self.assertTrue(message["capturedUtc"].endswith("Z"))
        roi = message["rois"][0]
        self.assertEqual(roi["roiId"], "L3")
        self.assertEqual(roi["peopleInRoi"], 1)
        self.assertEqual(roi["tracks"][0]["items"], {"helmet": False})

    def test_an_empty_region_still_reports_nobody(self):
        # The station needs "nobody is there" as much as it needs a reading: it is what clears a lane.
        roi = roi_payload("L3", [])
        self.assertEqual(roi["peopleInRoi"], 0)
        self.assertEqual(roi["tracks"], [])


class SettingsTests(unittest.TestCase):
    def test_class_mapping_reads_the_negative_marker(self):
        settings = Settings()
        self.assertEqual(settings.item_classes["helmet"], "helmet")
        self.assertEqual(settings.item_classes["no-helmet"], "!helmet")

    def test_items_come_from_the_environment(self):
        os.environ["PPEBOX_ITEMS"] = "helmet, boots"
        try:
            self.assertEqual(Settings().items, ("helmet", "boots"))
        finally:
            del os.environ["PPEBOX_ITEMS"]


if __name__ == "__main__":
    unittest.main()
