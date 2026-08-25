"""Experimental streaming CosyVoice 3 sidecar for VoiceScreen.

The regular endpoint mirrors the Qwen service for total-latency comparisons.
The streaming endpoint emits raw little-endian PCM16 as soon as each model
chunk is ready so clients can measure and use actual time-to-first-audio.
"""

from __future__ import annotations

import io
import os
import threading
import time
from dataclasses import dataclass

import numpy as np
import soundfile as sf
import torch
from flask import Flask, Response, jsonify, request, send_file, stream_with_context

from cosyvoice.cli.cosyvoice import AutoModel


MODEL_PATH = os.environ.get(
    "VOICESCREEN_COSYVOICE_MODEL", "/models/Fun-CosyVoice3-0.5B-2512"
)
REFERENCE_AUDIO = os.environ.get(
    "VOICESCREEN_VOICE_REFERENCE", "/profiles/my-voice-reference.wav"
)
REFERENCE_TEXT = os.environ.get("VOICESCREEN_VOICE_REFERENCE_TEXT", "").strip()
VOICE_ID = os.environ.get("VOICESCREEN_VOICE_ID", "my-voice")
API_TOKEN = os.environ.get("VOICESCREEN_API_TOKEN", "")
PORT = int(os.environ.get("VOICESCREEN_COSYVOICE_PORT", "18767"))
PROMPT_PREFIX = "You are a helpful assistant.<|endofprompt|>"

app = Flask(__name__)
generation_lock = threading.Lock()


@dataclass
class Runtime:
    model: object | None = None
    sample_rate: int = 0
    loaded_at: float = 0.0
    startup_error: str = ""


runtime = Runtime()


def require_token() -> None:
    if not API_TOKEN:
        raise RuntimeError("VOICESCREEN_API_TOKEN is not configured")
    if request.headers.get("X-VoiceScreen-Token", "") != API_TOKEN:
        from werkzeug.exceptions import Unauthorized

        raise Unauthorized("invalid VoiceScreen API token")


def request_text() -> str:
    payload = request.get_json(force=True, silent=False)
    text = str(payload.get("text", "")).strip()
    language = str(payload.get("language", "English")).strip() or "English"
    if not text or len(text) > 1200:
        from werkzeug.exceptions import BadRequest

        raise BadRequest("TTS text is empty or too long")
    if language.lower() != "english":
        from werkzeug.exceptions import BadRequest

        raise BadRequest("VoiceScreen CosyVoice experiment currently accepts English only")
    return text


def inference(text: str, stream: bool):
    # The reference prompt is cached under VOICE_ID during startup, so requests
    # do not rerun the 970 MB speech-tokenizer ONNX model.
    return runtime.model.inference_zero_shot(
        text,
        "",
        "",
        zero_shot_spk_id=VOICE_ID,
        stream=stream,
    )


def pcm16_bytes(speech: torch.Tensor) -> bytes:
    samples = speech.detach().float().reshape(-1).cpu().numpy()
    samples = np.clip(samples, -1.0, 1.0)
    return (samples * 32767.0).astype("<i2", copy=False).tobytes()


@app.get("/health")
def health():
    ready = runtime.model is not None
    body = {
        "ready": ready,
        "tts": "fun-cosyvoice3-0.5b-2512" if ready else "loading",
        "streaming": True,
        "voiceId": VOICE_ID,
        "device": "spark-gpu" if torch.cuda.is_available() else "cpu",
        "sampleRate": runtime.sample_rate,
        "uptimeSeconds": max(0, int(time.time() - runtime.loaded_at)) if runtime.loaded_at else 0,
    }
    if runtime.startup_error:
        body["error"] = runtime.startup_error
    return jsonify(body), 200 if ready else 503


@app.post("/synthesize")
def synthesize():
    require_token()
    if runtime.model is None:
        return jsonify(error="CosyVoice model is not ready"), 503
    text = request_text()
    started = time.perf_counter()
    chunks: list[torch.Tensor] = []
    first_chunk_ms = 0
    with generation_lock:
        for output in inference(text, stream=True):
            if not chunks:
                first_chunk_ms = int((time.perf_counter() - started) * 1000)
            chunks.append(output["tts_speech"].detach().float().cpu())
    if not chunks:
        return jsonify(error="CosyVoice returned empty audio"), 500
    speech = torch.cat(chunks, dim=-1).reshape(-1).numpy()
    output = io.BytesIO()
    sf.write(output, speech, runtime.sample_rate, format="WAV", subtype="PCM_16")
    output.seek(0)
    response = send_file(output, mimetype="audio/wav", download_name="voice.wav")
    response.headers["X-VoiceScreen-First-Chunk-Ms"] = str(first_chunk_ms)
    response.headers["X-VoiceScreen-Synthesis-Ms"] = str(
        int((time.perf_counter() - started) * 1000)
    )
    response.headers["X-VoiceScreen-Sample-Rate"] = str(runtime.sample_rate)
    return response


@app.post("/synthesize-stream")
def synthesize_stream():
    require_token()
    if runtime.model is None:
        return jsonify(error="CosyVoice model is not ready"), 503
    text = request_text()

    @stream_with_context
    def generate():
        with generation_lock:
            for output in inference(text, stream=True):
                audio = pcm16_bytes(output["tts_speech"])
                if audio:
                    yield audio

    return Response(
        generate(),
        mimetype="audio/L16",
        headers={
            "X-VoiceScreen-PCM-Format": "s16le",
            "X-VoiceScreen-Sample-Rate": str(runtime.sample_rate),
            "X-VoiceScreen-Channels": "1",
            "Cache-Control": "no-store",
        },
    )


def load_model() -> None:
    try:
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA is unavailable in the container")
        if not os.path.isfile(REFERENCE_AUDIO):
            raise FileNotFoundError(f"reference audio does not exist: {REFERENCE_AUDIO}")
        if not REFERENCE_TEXT:
            raise RuntimeError("VOICESCREEN_VOICE_REFERENCE_TEXT is empty")
        model = AutoModel(
            model_dir=MODEL_PATH,
            load_trt=False,
            load_vllm=False,
            fp16=True,
        )
        prompt_text = PROMPT_PREFIX + REFERENCE_TEXT
        if not model.add_zero_shot_spk(prompt_text, REFERENCE_AUDIO, VOICE_ID):
            raise RuntimeError("failed to cache the reference voice")
        runtime.model = model
        runtime.sample_rate = int(model.sample_rate)
        runtime.loaded_at = time.time()
    except Exception as exc:
        runtime.startup_error = f"{type(exc).__name__}: {exc}"
        raise


def create_app():
    if runtime.model is None:
        load_model()
    return app


if __name__ == "__main__":
    create_app().run(host="0.0.0.0", port=PORT, threaded=True, use_reloader=False)
