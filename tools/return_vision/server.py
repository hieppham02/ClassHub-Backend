"""Local CV adapter. Without trained weights it returns REVIEW, never fabricated detections."""
import base64
import binascii
import hashlib
import hmac
import io
import json
import math
import os
from pathlib import Path
import re
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MAX_IMAGE = 640 * 480 * 2
MAX_BODY = 1000000


def compare(expected, detections, labels, threshold=0.8):
    """Compare quantities only when every expected item maps to a distinct trained class."""
    rows, used = [], set()
    if not expected:
        return "REVIEW", "Phiếu chưa có danh sách thiết bị bàn giao.", rows
    complete = True
    for item in expected:
        label = labels.get(str(item.get("equipmentId")))
        quantity = item.get("quantity")
        mapped = isinstance(label, str) and bool(label) and label not in used
        if not mapped or type(quantity) is not int or quantity <= 0:
            complete = False
            rows.append({"snapshotId": item.get("snapshotId"), "name": item.get("name"),
                         "expected": quantity, "observed": None, "status": "UNMAPPED"})
            continue
        used.add(label)
        boxes = [d for d in detections if d["label"] == label]
        confident = [d for d in boxes if d["confidence"] >= threshold]
        matched = len(confident) == quantity and len(confident) == len(boxes)
        complete = complete and matched
        rows.append({"snapshotId": item.get("snapshotId"), "name": item.get("name"),
                     "label": label, "expected": quantity, "observed": len(confident),
                     "status": "MATCH" if matched else "REVIEW",
                     "detections": boxes})
    return ("PASS", "AI nhận diện đủ loại và số lượng đã cấu hình; cần quản trị viên xác nhận.", rows) if complete else (
        "REVIEW", "Có thiết bị chưa nhận diện chắc chắn hoặc số lượng chưa khớp; cần kiểm tra ảnh/thực tế.", rows)


class Engine:
    def __init__(self):
        self.model = None
        self.model_version = None
        self.labels = {}
        self.threshold = float(os.environ.get("CLASSHUB_VISION_CONFIDENCE", "0.8"))
        if not math.isfinite(self.threshold) or not 0.5 <= self.threshold <= 1:
            raise ValueError("CLASSHUB_VISION_CONFIDENCE must be between 0.5 and 1")
        self.lock = threading.Lock()
        weights = os.environ.get("CLASSHUB_VISION_WEIGHTS")
        labels = os.environ.get("CLASSHUB_VISION_LABELS")
        self.reason = "Chưa cấu hình mô hình đã huấn luyện bằng ảnh đồ thật trong hộc. Cần kiểm tra thủ công."
        if weights and labels:
            # Refuse an absent path: the inference library must not download generic weights.
            path = Path(weights)
            if not path.is_file():
                raise ValueError("Trained weights file does not exist")
            from ultralytics import YOLO
            self.labels = json.loads(Path(labels).read_text(encoding="utf-8-sig"))
            if not isinstance(self.labels, dict) or not all(isinstance(v, str) for v in self.labels.values()):
                raise ValueError("Labels must map equipment IDs to detector class names")
            self.model = YOLO(str(path), task="detect")
            self.model_version = hashlib.sha256(path.read_bytes()).hexdigest()

    def analyze(self, request):
        session = request.get("sessionId")
        command = request.get("commandId")
        expected = request.get("expected")
        if type(session) is not int or session <= 0 or not isinstance(command, str) or not re.fullmatch(r"[a-fA-F0-9]{32}", command):
            raise ValueError("Invalid session/command")
        if not isinstance(expected, list) or len(expected) > 100 or not all(isinstance(e, dict) for e in expected):
            raise ValueError("Invalid expected equipment")
        encoded = request.get("imageBase64")
        if not isinstance(encoded, str) or len(encoded) > (MAX_IMAGE * 4 // 3 + 4):
            raise ValueError("Invalid image length")
        try:
            image = base64.b64decode(encoded, validate=True)
        except (binascii.Error, ValueError) as exc:
            raise ValueError("Invalid base64 image") from exc
        if not 4 <= len(image) <= MAX_IMAGE or image[:2] != b"\xff\xd8" or image[-2:] != b"\xff\xd9":
            raise ValueError("Expected a bounded JPEG image")
        result = {"sessionId": session, "commandId": command, "status": "REVIEW", "reason": self.reason,
                  "modelVersion": self.model_version, "imageSha256": hashlib.sha256(image).hexdigest(), "items": []}
        if self.model is None:
            return result
        from PIL import Image, ImageStat
        with Image.open(io.BytesIO(image)) as opened:
            if opened.size != (640, 480):
                raise ValueError("Expected a VGA camera image")
            rgb = opened.convert("RGB")
        gray = rgb.convert("L")
        stats = ImageStat.Stat(gray)
        # A coarse reject gate, not a proof of adequate visibility/absence of occlusion.
        if stats.mean[0] < 20 or stats.mean[0] > 240 or stats.stddev[0] < 8:
            result["reason"] = "Ảnh quá tối/sáng hoặc thiếu chi tiết. Kiểm tra đèn và chụp lại."
            return result
        with self.lock:
            predictions = self.model.predict(rgb, conf=0.25, imgsz=640, device="cpu", verbose=False)
        detections = []
        for prediction in predictions:
            for box in prediction.boxes:
                detections.append({"label": prediction.names[int(box.cls.item())],
                                   "confidence": round(float(box.conf.item()), 5),
                                   "box": [round(float(x), 1) for x in box.xyxy[0].tolist()]})
        status, reason, items = compare(expected, detections, self.labels, self.threshold)
        result.update(status=status, reason=reason, items=items)
        return result


class Handler(BaseHTTPRequestHandler):
    engine = None
    key = ""
    # Limit concurrent inference/body allocations; the backend already queues captures.
    slot = threading.BoundedSemaphore(2)
    def send_json(self, status, value):
        body = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path != "/health":
            return self.send_json(404, {"message": "Not found"})
        self.send_json(200, {"ready": self.engine.model is not None, "mode": "assistive", "modelVersion": self.engine.model_version})

    def do_POST(self):
        self.connection.settimeout(15)
        if self.path != "/analyze":
            return self.send_json(404, {"message": "Not found"})
        if not self.key or not hmac.compare_digest(self.headers.get("X-ClassHub-Vision-Key", "").encode(), self.key.encode()):
            return self.send_json(401, {"message": "Invalid service key"})
        if not self.slot.acquire(blocking=False):
            return self.send_json(429, {"message": "Vision service busy"})
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= MAX_BODY:
                return self.send_json(413, {"message": "Invalid body length"})
            body = self.rfile.read(length)
            if len(body) != length:
                raise ValueError("Truncated request")
            value = json.loads(body)
            if not isinstance(value, dict):
                raise ValueError("Expected an object")
            self.send_json(200, self.engine.analyze(value))
        except (ValueError, KeyError, OSError):
            self.send_json(400, {"message": "Invalid image or analysis request"})
        except Exception:
            # No image, credentials, tokens, or model internals in HTTP errors/logs.
            self.send_json(503, {"message": "Inference unavailable; manual review required"})
        finally:
            self.slot.release()

    def log_message(self, fmt, *args):
        pass


def main():
    key = os.environ.get("CLASSHUB_VISION_KEY", "")
    if len(key) < 32:
        raise SystemExit("Set CLASSHUB_VISION_KEY to a random service secret of at least 32 characters")
    Handler.key = key
    Handler.engine = Engine()
    port = int(os.environ.get("CLASSHUB_VISION_PORT", "5158"))
    print(f"ClassHub vision listening on 127.0.0.1:{port}; trained model ready: {Handler.engine.model is not None}")
    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()


if __name__ == "__main__":
    main()
