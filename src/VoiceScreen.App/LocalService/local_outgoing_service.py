"""VoiceScreen local ASR and deterministic OPUS-MT translation service."""

import argparse
import json
import os
import re
import threading

os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault("HF_HUB_DISABLE_XET", "1")
os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

import ctranslate2
import numpy as np
from faster_whisper import WhisperModel
from transformers import MarianTokenizer


class State:
    whisper = None
    whisper_preview = None
    zh_en = None
    en_zh = None
    th_en = None
    zh_en_tokenizer = None
    en_zh_tokenizer = None
    th_en_tokenizer = None
    asr_lock = threading.Lock()
    translation_lock = threading.Lock()


def model_root():
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise RuntimeError("LOCALAPPDATA is not available")
    return os.path.join(local_app_data, "VoiceScreen", "Models")


def require_model(name):
    path = os.path.join(model_root(), name)
    if not os.path.isfile(os.path.join(path, "model.bin")):
        raise RuntimeError(
            f"Missing local translation model: {name}. Run tools/setup_local_models.ps1 first."
        )
    return path


def normalize_game_terms(text, direction):
    if direction == "zh-en":
        replacements = {
            "先别冲": "暂时不要进攻",
            "不要冲": "不要急着进攻",
            "别冲": "不要急着进攻",
        }
        for source, target in replacements.items():
            text = text.replace(source, target)
        return text

    if direction == "en-zh":
        text = re.sub(r"\bdon['’]?t\s+push\b", "don't attack", text, flags=re.IGNORECASE)
        text = re.sub(r"\bdo\s+not\s+push\b", "do not attack", text, flags=re.IGNORECASE)
        text = re.sub(r"\bpush\b", "advance", text, flags=re.IGNORECASE)
    return text


def split_clauses(text, direction):
    pattern = r"[\uFF0C\u3002\uFF01\uFF1F\uFF1B,!?;]+" if direction == "zh-en" else r"(?<=[.!?;])\s+|[,;]+"
    parts = [part.strip() for part in re.split(pattern, text) if part.strip()]
    return parts or [text.strip()]


def translate_text(text, direction):
    if direction == "zh-en":
        translator = State.zh_en
        tokenizer = State.zh_en_tokenizer
    elif direction == "en-zh":
        translator = State.en_zh
        tokenizer = State.en_zh_tokenizer
    elif direction == "th-en":
        translator = State.th_en
        tokenizer = State.th_en_tokenizer
    else:
        raise ValueError("direction must be zh-en, en-zh, or th-en")

    normalized = normalize_game_terms(text.strip(), direction)
    clauses = split_clauses(normalized, direction)
    batches = [tokenizer.convert_ids_to_tokens(tokenizer.encode(clause)) for clause in clauses]
    results = translator.translate_batch(batches, beam_size=4, max_decoding_length=96)
    translated = []
    for result in results:
        value = tokenizer.decode(
            tokenizer.convert_tokens_to_ids(result.hypotheses[0]), skip_special_tokens=True
        ).strip()
        if value:
            translated.append(value)
    output = " ".join(translated).strip()

    # OPUS-MT translates this polite phrase too literally when it is isolated as a clause.
    if direction == "zh-en" and "不要介意" in text:
        output = re.sub(r"Please don['’]t bother\.?", "Please don't mind.", output, flags=re.IGNORECASE)
    return output


class Handler(BaseHTTPRequestHandler):
    server_version = "VoiceScreenLocalModels/2.0"

    def log_message(self, fmt, *args):
        return

    def send_json(self, status, value):
        data = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def read_body(self, maximum):
        length = int(self.headers.get("Content-Length", "0"))
        if length <= 0 or length > maximum:
            raise ValueError("invalid request payload")
        return self.rfile.read(length)

    def do_GET(self):
        if self.path == "/health":
            self.send_json(200, {
                "status": "ready",
                "asr": "faster-whisper-base-preview+small-final-cpu-int8",
                "translation": "opus-mt-zh-en+en-zh+th-en-cpu-int8",
            })
        else:
            self.send_json(404, {"error": "not found"})

    def do_POST(self):
        parsed = urlparse(self.path)
        try:
            if parsed.path == "/transcribe":
                self.transcribe(parsed)
            elif parsed.path == "/translate":
                self.translate()
            else:
                self.send_json(404, {"error": "not found"})
        except (BrokenPipeError, ConnectionResetError):
            # The desktop client may abandon a timed-out segment while local inference
            # is finishing. Do not turn that normal disconnect into another response.
            return
        except Exception as exc:
            try:
                self.send_json(500, {"error": str(exc)})
            except (BrokenPipeError, ConnectionResetError):
                return

    def transcribe(self, parsed):
        pcm = self.read_body(16 * 1024 * 1024)
        audio = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
        requested = parse_qs(parsed.query).get("language", ["auto"])[0]
        preview_mode = parse_qs(parsed.query).get("mode", ["final"])[0] == "preview"
        if requested not in ("zh", "en", "auto"):
            raise ValueError("language must be zh, en, or auto")
        options = dict(
            beam_size=1,
            best_of=1,
            temperature=0,
            # The desktop app has already segmented Discord PCM. Running Whisper's
            # VAD again can discard clauses around normal pauses in a long sentence.
            vad_filter=False,
            condition_on_previous_text=False,
            without_timestamps=True,
        )
        if requested != "auto":
            options["language"] = requested
        # Whisper requests stay serialized. Translation uses separate model objects
        # and a separate lock, so incremental ASR and OPUS can run as a CPU pipeline.
        with State.asr_lock:
            model = State.whisper_preview if preview_mode else State.whisper
            segments, info = model.transcribe(audio, **options)
            text = "".join(segment.text for segment in segments).strip()
        self.send_json(200, {"text": text, "language": info.language})

    def translate(self):
        request = json.loads(self.read_body(64 * 1024).decode("utf-8"))
        text = str(request.get("text", "")).strip()
        direction = str(request.get("direction", ""))
        if not text or len(text) > 1000:
            raise ValueError("translation text is empty or too long")
        with State.translation_lock:
            translated = translate_text(text, direction)
        self.send_json(200, {"text": translated})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=18765)
    parser.add_argument("--asr-only", action="store_true")
    args = parser.parse_args()

    root = model_root()
    State.whisper = WhisperModel(
        "small", device="cpu", compute_type="int8", cpu_threads=8, num_workers=1,
        local_files_only=True,
    )
    State.whisper_preview = WhisperModel(
        "base", device="cpu", compute_type="int8", cpu_threads=6, num_workers=1,
        local_files_only=True,
    )
    if not args.asr_only:
        zh_en_model = require_model("opus-mt-zh-en-ct2-int8")
        en_zh_model = require_model("opus-mt-en-zh-ct2-int8")
        th_en_model = require_model("opus-mt-th-en-ct2-int8")
        State.zh_en = ctranslate2.Translator(
            zh_en_model, device="cpu", compute_type="int8", inter_threads=1, intra_threads=8
        )
        State.en_zh = ctranslate2.Translator(
            en_zh_model, device="cpu", compute_type="int8", inter_threads=1, intra_threads=8
        )
        State.th_en = ctranslate2.Translator(
            th_en_model, device="cpu", compute_type="int8", inter_threads=1, intra_threads=8
        )
        State.zh_en_tokenizer = MarianTokenizer.from_pretrained(zh_en_model, local_files_only=True)
        State.en_zh_tokenizer = MarianTokenizer.from_pretrained(en_zh_model, local_files_only=True)
        State.th_en_tokenizer = MarianTokenizer.from_pretrained(th_en_model, local_files_only=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
