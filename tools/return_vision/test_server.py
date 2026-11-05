import base64
import os
import unittest
from unittest.mock import patch
from server import Engine, compare


class VerificationTests(unittest.TestCase):
    def setUp(self):
        self.expected = [{"snapshotId": 1, "equipmentId": 7, "name": "Remote", "quantity": 1}]
        self.box = {"label": "projector_remote", "confidence": 0.96, "box": [0, 0, 10, 10]}

    def test_absent_model_never_claims_detection(self):
        with patch.dict(os.environ, {}, clear=True):
            result = Engine().analyze({"sessionId": 1, "commandId": "a" * 32,
                "imageBase64": base64.b64encode(b"\xff\xd8\xff\xd9").decode(), "expected": self.expected})
        self.assertEqual(result["status"], "REVIEW")
        self.assertEqual(result["items"], [])

    def test_exact_confident_quantity(self):
        self.assertEqual(compare(self.expected, [self.box], {"7": "projector_remote"})[0], "PASS")

    def test_missing_object_requires_review(self):
        self.assertEqual(compare(self.expected, [], {"7": "projector_remote"})[0], "REVIEW")

    def test_extra_object_requires_review(self):
        self.assertEqual(compare(self.expected, [self.box, self.box], {"7": "projector_remote"})[0], "REVIEW")

    def test_low_confidence_requires_review(self):
        self.assertEqual(compare(self.expected, [dict(self.box, confidence=0.6)], {"7": "projector_remote"})[0], "REVIEW")

    def test_unmapped_item_requires_review(self):
        self.assertEqual(compare(self.expected, [self.box], {})[0], "REVIEW")

    def test_duplicate_class_cannot_identify_distinct_assets(self):
        expected = self.expected + [dict(self.expected[0], snapshotId=2, equipmentId=8)]
        self.assertEqual(compare(expected, [self.box], {"7": "projector_remote", "8": "projector_remote"})[0], "REVIEW")

    def test_no_expected_items_never_pass(self):
        self.assertEqual(compare([], [], {})[0], "REVIEW")

    def test_stale_or_invalid_input_rejected(self):
        with patch.dict(os.environ, {}, clear=True):
            engine = Engine()
        with self.assertRaises(ValueError):
            engine.analyze({"sessionId": 1, "commandId": "old", "expected": [], "imageBase64": "x"})

    def test_no_automatic_model_download(self):
        with patch.dict(os.environ, {"CLASSHUB_VISION_WEIGHTS": "missing-trained-model.pt", "CLASSHUB_VISION_LABELS": "missing.json"}, clear=True):
            with self.assertRaises(ValueError):
                Engine()


if __name__ == "__main__":
    unittest.main()
