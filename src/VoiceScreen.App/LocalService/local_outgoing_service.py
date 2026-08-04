"""VoiceScreen local ASR and deterministic OPUS-MT translation service."""

import argparse
import asyncio
import importlib.util
import ipaddress
import json
import os
import re
import secrets
import threading
import time
from pathlib import Path

os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault("HF_HUB_DISABLE_XET", "1")
os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

import ctranslate2
from transformers import MarianTokenizer

from online_providers import synthesize_edge, target_voice, translate_mymemory
from local_tts_provider import (
    piper_available,
    selected_voice as local_tts_voice,
    synthesize_piper,
    voice_availability as local_voice_availability,
    voice_labels as local_voice_labels,
    voice_licenses as local_voice_licenses,
    voices_for_direction as local_voices_for_direction,
)


WEB_ROOT = Path(__file__).resolve().parent / "web"


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
    local_tts_lock = threading.Lock()
    audio_cache = {}
    audio_cache_lock = threading.Lock()


def model_root():
    explicit_root = os.environ.get("VOICESCREEN_MODEL_ROOT")
    if explicit_root:
        return os.path.abspath(os.path.expanduser(explicit_root))
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise RuntimeError("Set VOICESCREEN_MODEL_ROOT when LOCALAPPDATA is not available")
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


def translate_text(text, direction, use_glossary=True, beam_size=4, max_decoding_length=96):
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

    normalized = normalize_game_terms(text.strip(), direction) if use_glossary else text.strip()
    clauses = split_clauses(normalized, direction)
    batches = [tokenizer.convert_ids_to_tokens(tokenizer.encode(clause)) for clause in clauses]
    results = translator.translate_batch(
        batches,
        beam_size=beam_size,
        max_decoding_length=max_decoding_length,
    )
    translated = []
    for result in results:
        value = tokenizer.decode(
            tokenizer.convert_tokens_to_ids(result.hypotheses[0]), skip_special_tokens=True
        ).strip()
        if value:
            translated.append(value)
    output = " ".join(translated).strip()

    # OPUS-MT translates this polite phrase too literally when it is isolated as a clause.
    if use_glossary and direction == "zh-en" and "不要介意" in text:
        output = re.sub(r"Please don['’]t bother\.?", "Please don't mind.", output, flags=re.IGNORECASE)
    return output


def has_glossary_rules(text, direction):
    return normalize_game_terms(text.strip(), direction) != text.strip() or (
        direction == "zh-en" and "不要介意" in text
    )


def evaluate_translation(text, direction, use_glossary=True, beam_size=4, max_decoding_length=96):
    """Run one translation experiment and return enough trace data for evaluation."""
    if direction not in ("zh-en", "en-zh", "th-zh"):
        raise ValueError("direction must be zh-en, en-zh, or th-zh")
    if not isinstance(beam_size, int) or not 1 <= beam_size <= 8:
        raise ValueError("beamSize must be an integer between 1 and 8")
    if not isinstance(max_decoding_length, int) or not 32 <= max_decoding_length <= 256:
        raise ValueError("maxDecodingLength must be an integer between 32 and 256")

    started = time.perf_counter()
    bridge_text = None
    if direction == "th-zh":
        bridge_text = translate_text(
            text,
            "th-en",
            use_glossary=False,
            beam_size=beam_size,
            max_decoding_length=max_decoding_length,
        )
        translated = translate_text(
            bridge_text,
            "en-zh",
            use_glossary=use_glossary,
            beam_size=beam_size,
            max_decoding_length=max_decoding_length,
        )
        normalized = normalize_game_terms(bridge_text, "en-zh") if use_glossary else bridge_text
        model = "opus-mt-th-en + opus-mt-en-zh"
    else:
        normalized = normalize_game_terms(text.strip(), direction) if use_glossary else text.strip()
        translated = translate_text(
            text,
            direction,
            use_glossary=use_glossary,
            beam_size=beam_size,
            max_decoding_length=max_decoding_length,
        )
        model = f"opus-mt-{direction}"

    translation_latency_ms = round((time.perf_counter() - started) * 1000, 2)
    source_chars = len(text)
    output_chars = len(translated)
    return {
        "providerId": "local-opus",
        "sourceText": text,
        "normalizedText": normalized,
        "translatedText": translated,
        "bridgeText": bridge_text,
        "direction": direction,
        "useGlossary": use_glossary,
        "beamSize": beam_size,
        "maxDecodingLength": max_decoding_length,
        "latencyMs": translation_latency_ms,
        "translationLatencyMs": translation_latency_ms,
        "totalPipelineLatencyMs": translation_latency_ms,
        "tts": None,
        "model": model,
        "glossaryAvailable": has_glossary_rules(text, direction),
        "qualitySignals": {
            "sourceCharacters": source_chars,
            "outputCharacters": output_chars,
            "outputSourceLengthRatio": round(output_chars / max(source_chars, 1), 3),
            "sourceCharactersPerSecond": round(source_chars / max(translation_latency_ms / 1000, 0.001), 2),
        },
    }


def online_tts_available():
    return importlib.util.find_spec("edge_tts") is not None and importlib.util.find_spec("mutagen") is not None


def local_tts_available():
    try:
        return piper_available(model_root())
    except Exception:
        return False


def local_tts_voice_availability():
    try:
        return local_voice_availability(model_root())
    except Exception:
        voices = {
            voice
            for direction in ("zh-en", "en-zh", "th-zh")
            for voice in local_voices_for_direction(direction)
        }
        return {voice: False for voice in voices}


def cache_audio(audio, metadata):
    now = time.monotonic()
    token = secrets.token_urlsafe(24)
    with State.audio_cache_lock:
        expired = [key for key, value in State.audio_cache.items() if value[0] <= now]
        for key in expired:
            State.audio_cache.pop(key, None)
        while len(State.audio_cache) >= 24:
            State.audio_cache.pop(next(iter(State.audio_cache)))
        State.audio_cache[token] = (now + 15 * 60, audio, metadata)
    return token


def get_cached_audio(token):
    now = time.monotonic()
    with State.audio_cache_lock:
        value = State.audio_cache.get(token)
        if not value or value[0] <= now:
            State.audio_cache.pop(token, None)
            return None
        return value[1], value[2]


def evaluate_online(text, direction, use_glossary=True, include_tts=False, voice=None):
    started = time.perf_counter()
    normalized = normalize_game_terms(text.strip(), direction) if use_glossary else text.strip()
    translation = translate_mymemory(normalized, direction)
    translated = translation["translatedText"]
    source_chars = len(text)
    output_chars = len(translated)
    translation_latency_ms = translation["translationLatencyMs"]
    tts = None
    if include_tts:
        selected_voice = target_voice(direction, voice)
        audio, tts = asyncio.run(synthesize_edge(translated, selected_voice))
        audio_token = cache_audio(audio, tts)
        tts["audioUrl"] = f"/audio/{audio_token}.mp3"

    total_latency_ms = round((time.perf_counter() - started) * 1000, 2)
    return {
        "providerId": "mymemory-edge",
        "sourceText": text,
        "normalizedText": normalized,
        "translatedText": translated,
        "bridgeText": None,
        "direction": direction,
        "useGlossary": use_glossary,
        "beamSize": None,
        "maxDecodingLength": None,
        "latencyMs": translation_latency_ms,
        "translationLatencyMs": translation_latency_ms,
        "totalPipelineLatencyMs": total_latency_ms,
        "tts": tts,
        "model": "MyMemory public translation + Microsoft Edge TTS" if include_tts else "MyMemory public translation",
        "glossaryAvailable": has_glossary_rules(text, direction),
        "qualitySignals": {
            "providerMatch": translation["providerMatch"],
            "sourceCharacters": source_chars,
            "outputCharacters": output_chars,
            "outputSourceLengthRatio": round(output_chars / max(source_chars, 1), 3),
            "sourceCharactersPerSecond": round(source_chars / max(translation_latency_ms / 1000, 0.001), 2),
        },
    }


class Handler(BaseHTTPRequestHandler):
    server_version = "VoiceScreenLocalModels/2.0"

    def log_message(self, fmt, *args):
        return

    def send_json(self, status, value):
        data = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_bytes(status, data, "application/json; charset=utf-8")

    def send_bytes(self, status, data, content_type):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'")
        self.end_headers()
        self.wfile.write(data)

    def read_body(self, maximum):
        length = int(self.headers.get("Content-Length", "0"))
        if length <= 0 or length > maximum:
            raise ValueError("invalid request payload")
        return self.rfile.read(length)

    def do_GET(self):
        path = urlparse(self.path).path
        if path == "/health":
            self.send_json(200, {
                "status": "ready",
                "asr": "faster-whisper-base-preview+small-final-cpu-int8" if State.whisper else "disabled",
                "translation": "opus-mt-zh-en+en-zh+th-en-cpu-int8",
                "localTts": "piper-tts" if local_tts_available() else "disabled",
                "evaluationUi": "/",
            })
            return
        if path == "/providers":
            self.send_json(200, {"providers": [{
                "id": "local-opus",
                "name": "本地 OPUS-MT + Piper",
                "kind": "local",
                "privacy": "local-only",
                "translation": True,
                "tts": local_tts_available(),
                "directions": ["zh-en", "en-zh", "th-zh"],
                "voices": {
                    direction: local_voices_for_direction(direction)
                    for direction in ("zh-en", "en-zh", "th-zh")
                },
                "voiceLabels": local_voice_labels(),
                "voiceLicenses": local_voice_licenses(),
                "voiceAvailability": local_tts_voice_availability(),
                "licenses": {"ttsRuntime": "GPL-3.0", "voices": "See voiceLicenses"},
            }, {
                "id": "mymemory-edge",
                "name": "MyMemory + Edge TTS",
                "kind": "online",
                "privacy": "text-sent-to-third-party",
                "translation": True,
                "tts": online_tts_available(),
                "directions": ["zh-en", "en-zh", "th-zh"],
                "voices": {
                    "zh-en": ["en-US-JennyNeural", "en-US-GuyNeural"],
                    "en-zh": ["zh-CN-XiaoxiaoNeural", "zh-CN-YunxiNeural"],
                    "th-zh": ["zh-CN-XiaoxiaoNeural", "zh-CN-YunxiNeural"],
                },
            }]})
            return

        audio_match = re.fullmatch(r"/audio/([A-Za-z0-9_-]{32})\.(mp3|wav)", path)
        if audio_match:
            cached = get_cached_audio(audio_match.group(1))
            if cached is None:
                self.send_json(404, {"error": "audio expired or not found"})
                return
            audio, metadata = cached
            self.send_bytes(200, audio, metadata.get("contentType", "application/octet-stream"))
            return

        static_files = {
            "/": ("index.html", "text/html; charset=utf-8"),
            "/index.html": ("index.html", "text/html; charset=utf-8"),
            "/assets/eval.css": ("eval.css", "text/css; charset=utf-8"),
            "/assets/eval.js": ("eval.js", "application/javascript; charset=utf-8"),
        }
        static_file = static_files.get(path)
        if static_file:
            file_name, content_type = static_file
            file_path = WEB_ROOT / file_name
            if not file_path.is_file():
                self.send_json(503, {"error": "translation evaluation UI is not installed"})
                return
            self.send_bytes(200, file_path.read_bytes(), content_type)
            return
        self.send_json(404, {"error": "not found"})

    def do_POST(self):
        parsed = urlparse(self.path)
        try:
            if parsed.path == "/transcribe":
                self.transcribe(parsed)
            elif parsed.path == "/translate":
                self.translate()
            elif parsed.path == "/evaluate":
                self.evaluate()
            else:
                self.send_json(404, {"error": "not found"})
        except (BrokenPipeError, ConnectionResetError):
            # The desktop client may abandon a timed-out segment while local inference
            # is finishing. Do not turn that normal disconnect into another response.
            return
        except (ValueError, json.JSONDecodeError) as exc:
            try:
                self.send_json(400, {"error": str(exc)})
            except (BrokenPipeError, ConnectionResetError):
                return
        except Exception as exc:
            print(f"request failed: {type(exc).__name__}: {exc}", flush=True)
            try:
                self.send_json(500, {"error": "local model inference failed"})
            except (BrokenPipeError, ConnectionResetError):
                return

    def transcribe(self, parsed):
        if State.whisper is None:
            raise RuntimeError("speech recognition is disabled in translation-only mode")
        import numpy as np

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

    def evaluate(self):
        request = json.loads(self.read_body(64 * 1024).decode("utf-8"))
        allowed = {"provider", "text", "direction", "useGlossary", "beamSize", "maxDecodingLength", "includeTts", "voice"}
        unknown = set(request) - allowed
        if unknown:
            raise ValueError(f"unknown evaluation fields: {', '.join(sorted(unknown))}")
        text = request.get("text")
        if not isinstance(text, str) or not text.strip() or len(text.strip()) > 1000:
            raise ValueError("translation text is empty or too long")
        direction = request.get("direction")
        if not isinstance(direction, str) or direction not in ("zh-en", "en-zh", "th-zh"):
            raise ValueError("direction must be zh-en, en-zh, or th-zh")
        provider = request.get("provider", "local-opus")
        if provider not in ("local-opus", "mymemory-edge"):
            raise ValueError("provider must be local-opus or mymemory-edge")
        use_glossary = request.get("useGlossary", True)
        beam_size = request.get("beamSize", 4)
        max_decoding_length = request.get("maxDecodingLength", 96)
        include_tts = request.get("includeTts", False)
        voice = request.get("voice")
        if not isinstance(use_glossary, bool):
            raise ValueError("useGlossary must be a boolean")
        if not isinstance(include_tts, bool):
            raise ValueError("includeTts must be a boolean")
        if voice is not None and not isinstance(voice, str):
            raise ValueError("voice must be a string")

        if provider == "local-opus":
            local_started = time.perf_counter()
            with State.translation_lock:
                result = evaluate_translation(
                    text.strip(),
                    direction,
                    use_glossary=use_glossary,
                    beam_size=beam_size,
                    max_decoding_length=max_decoding_length,
                )
            if include_tts:
                if not local_tts_available():
                    raise ValueError("local Piper TTS is not installed")
                selected = local_tts_voice(direction, voice)
                with State.local_tts_lock:
                    audio, tts = synthesize_piper(
                        result["translatedText"], direction, model_root(), selected
                    )
                audio_token = cache_audio(audio, tts)
                tts["audioUrl"] = f"/audio/{audio_token}.wav"
                result["tts"] = tts
                result["totalPipelineLatencyMs"] = round((time.perf_counter() - local_started) * 1000, 2)
                result["model"] = f"{result['model']} + Piper {selected}"
        else:
            result = evaluate_online(
                text.strip(),
                direction,
                use_glossary=use_glossary,
                include_tts=include_tts,
                voice=voice,
            )
        self.send_json(200, result)


def parse_bind_host(value):
    try:
        address = ipaddress.ip_address(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("--host must be a valid IPv4 address") from exc
    if address.version != 4:
        raise argparse.ArgumentTypeError("--host currently supports IPv4 addresses only")
    return str(address)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", type=parse_bind_host, default="127.0.0.1",
                        help="IPv4 listen address; use 0.0.0.0 only with a restrictive firewall")
    parser.add_argument("--port", type=int, default=18765)
    parser.add_argument("--asr-only", action="store_true",
                        help="Load Whisper only and skip OPUS-MT translation models")
    parser.add_argument("--translation-only", action="store_true",
                        help="Load OPUS-MT only and serve the browser evaluation lab without Whisper/WPF")
    parser.add_argument("--model-root",
                        help="Model directory; overrides VOICESCREEN_MODEL_ROOT and Windows LOCALAPPDATA")
    args = parser.parse_args()

    if args.asr_only and args.translation_only:
        parser.error("--asr-only and --translation-only cannot be used together")

    if args.model_root:
        os.environ["VOICESCREEN_MODEL_ROOT"] = args.model_root

    if not args.translation_only:
        from faster_whisper import WhisperModel
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

    service_url = f"http://{args.host}:{args.port}/"
    if args.translation_only:
        print(f"VoiceScreen translation evaluation lab: {service_url}", flush=True)
    else:
        print(f"VoiceScreen local service: {service_url}", flush=True)
    ThreadingHTTPServer((args.host, args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
