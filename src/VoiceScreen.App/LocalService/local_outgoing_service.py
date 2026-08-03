"""VoiceScreen local English/Chinese ASR worker. Binds to loopback only."""

import argparse
import json
import os
os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault("HF_HUB_DISABLE_XET", "1")
os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

import numpy as np
from faster_whisper import WhisperModel


class State:
    model = None


class Handler(BaseHTTPRequestHandler):
    server_version = "VoiceScreenLocalASR/1.0"

    def log_message(self, fmt, *args):
        return

    def send_json(self, status, value):
        data = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/health":
            self.send_json(200, {"status": "ready", "model": "small", "device": "cpu", "compute": "int8"})
        else:
            self.send_json(404, {"error": "not found"})

    def do_POST(self):
        parsed = urlparse(self.path)
        if parsed.path != "/transcribe":
            self.send_json(404, {"error": "not found"})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length <= 0 or length > 16 * 1024 * 1024:
                self.send_json(400, {"error": "invalid PCM payload"})
                return
            pcm = self.rfile.read(length)
            audio = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
            requested_language = parse_qs(parsed.query).get("language", ["zh"])[0]
            if requested_language not in ("zh", "en"):
                self.send_json(400, {"error": "language must be zh or en"})
                return
            segments, info = State.model.transcribe(
                audio,
                language=requested_language,
                beam_size=1,
                best_of=1,
                temperature=0,
                vad_filter=True,
                vad_parameters={"min_silence_duration_ms": 250},
                condition_on_previous_text=False,
                without_timestamps=True,
            )
            text = "".join(segment.text for segment in segments).strip()
            self.send_json(200, {"text": text, "language": info.language})
        except Exception as exc:
            self.send_json(500, {"error": str(exc)})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=18765)
    args = parser.parse_args()
    State.model = WhisperModel(
        "small",
        device="cpu",
        compute_type="int8",
        cpu_threads=8,
        num_workers=1,
        local_files_only=True,
    )
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    server.serve_forever()


if __name__ == "__main__":
    main()
