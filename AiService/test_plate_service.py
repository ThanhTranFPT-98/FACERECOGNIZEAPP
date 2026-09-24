import unittest
from unittest.mock import patch
from fastapi.testclient import TestClient
import plate_service as service

class PlateTests(unittest.TestCase):
    def test_vietnam_layouts(self):
        for original in ("59-X1\n123.45", "30A-123.45", "29-AA 123.45", "59X1-1234"):
            self.assertTrue(service.common_plate(service.normalize(original)))
    def test_ambiguous_text_is_not_corrected(self):
        self.assertFalse(service.common_plate(service.normalize("3OA-123.45")))
        self.assertFalse(service.common_plate("UNKNOWN"))
    def test_missing_engine_is_not_success(self):
        with patch.object(service, "tesseract_path", return_value=None):
            client = TestClient(service.app)
            self.assertEqual(client.get('/health').status_code, 503)
            self.assertEqual(client.post('/v1/recognition/plate', files={'file': ('a.jpg', b'x', 'image/jpeg')}).status_code, 503)
    def test_upload_limit(self):
        client = TestClient(service.app)
        self.assertEqual(client.post('/v1/recognition/plate', files={'file': ('a.jpg', b'x' * 5_000_001, 'image/jpeg')}).status_code, 400)

if __name__ == '__main__':
    unittest.main()
