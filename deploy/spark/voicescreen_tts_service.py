"""Private Qwen3-TTS voice-clone service for VoiceScreen.

The reference recording, transcript, and reusable prompt stay on the Spark.
Only authenticated LAN requests are accepted, and the endpoint returns PCM16
WAV so the Windows client can feed its existing VB-CABLE playback pipeline.
"""

from __future__ import annotations

import io
import os
import threading
import time
from dataclasses import asdict, dataclass

import soundfile as sf
import torch
from flask import Flask, jsonify, request, send_file
from qwen_tts import Qwen3TTSModel, VoiceClonePromptItem


MODEL_PATH = os.environ.get("VOICESCREEN_TTS_MODEL", "/models/Qwen3-TTS-12Hz-0.6B-Base")
REFERENCE_AUDIO = os.environ.get("VOICESCREEN_VOICE_REFERENCE", "/profiles/my-voice-reference.wav")
REFERENCE_TEXT = os.environ.get("VOICESCREEN_VOICE_REFERENCE_TEXT", "").strip()
PROMPT_PATH = os.environ.get("VOICESCREEN_VOICE_PROMPT", "/profiles/my-voice.pt")
VOICE_ID = os.environ.get("VOICESCREEN_VOICE_ID", "my-voice")
API_TOKEN = os.environ.get("VOICESCREEN_API_TOKEN", "")
PORT = int(os.environ.get("VOICESCREEN_TTS_PORT", "18766"))

app = Flask(__name__)
generation_lock = threading.Lock()


@dataclass
class Runtime:
    model: Qwen3TTSModel | None = None
    prompt: list[VoiceClonePromptItem] | None = None
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


def save_prompt(items: list[VoiceClonePromptItem]) -> None:
    os.makedirs(os.path.dirname(PROMPT_PATH), exist_ok=True)
    temporary = PROMPT_PATH + ".tmp"
    torch.save({"items": [asdict(item) for item in items]}, temporary)
    os.replace(temporary, PROMPT_PATH)


def load_prompt() -> list[VoiceClonePromptItem] | None:
    if not os.path.isfile(PROMPT_PATH):
        return None
    payload = torch.load(PROMPT_PATH, map_location="cpu", weights_only=True)
    values = payload.get("items") if isinstance(payload, dict) else None
    if not isinstance(values, list) or not values:
        raise RuntimeError("saved voice prompt is empty or invalid")
    return [VoiceClonePromptItem(**value) for value in values]


@app.get("/health")
def health():
    ready = runtime.model is not None and runtime.prompt is not None
    body = {
        "ready": ready,
        "tts": "qwen3-tts-12hz-0.6b-base" if ready else "loading",
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
    if runtime.model is None or runtime.prompt is None:
        return jsonify(error="cloned voice model is not ready"), 503
    payload = request.get_json(force=True, silent=False)
    text = str(payload.get("text", "")).strip()
    language = str(payload.get("language", "English")).strip() or "English"
    if not text or len(text) > 1200:
        return jsonify(error="TTS text is empty or too long"), 400
    if language.lower() != "english":
        return jsonify(error="VoiceScreen cloned voice currently accepts English only"), 400

    started = time.perf_counter()
    with generation_lock:
        wavs, sample_rate = runtime.model.generate_voice_clone(
            text=text,
            language="English",
            voice_clone_prompt=runtime.prompt,
            do_sample=True,
            top_k=50,
            top_p=1.0,
            temperature=0.9,
            repetition_penalty=1.05,
        )
    if not wavs or wavs[0].size == 0:
        return jsonify(error="voice model returned empty audio"), 500

    output = io.BytesIO()
    sf.write(output, wavs[0], sample_rate, format="WAV", subtype="PCM_16")
    output.seek(0)
    response = send_file(output, mimetype="audio/wav", download_name="voice.wav")
    response.headers["X-VoiceScreen-Synthesis-Ms"] = str(int((time.perf_counter() - started) * 1000))
    response.headers["X-VoiceScreen-Sample-Rate"] = str(sample_rate)
    return response


def load_model() -> None:
    try:
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA is unavailable in the container")
        model = Qwen3TTSModel.from_pretrained(
            MODEL_PATH,
            device_map="cuda:0",
            dtype=torch.bfloat16,
            attn_implementation="flash_attention_2",
            local_files_only=True,
        )
        prompt = load_prompt()
        if prompt is None:
            if not os.path.isfile(REFERENCE_AUDIO):
                raise FileNotFoundError(f"reference audio does not exist: {REFERENCE_AUDIO}")
            if not REFERENCE_TEXT:
                raise RuntimeError("VOICESCREEN_VOICE_REFERENCE_TEXT is empty")
            prompt = model.create_voice_clone_prompt(
                ref_audio=REFERENCE_AUDIO,
                ref_text=REFERENCE_TEXT,
                x_vector_only_mode=False,
            )
            save_prompt(prompt)
        runtime.model = model
        runtime.prompt = prompt
        # Qwen3-TTS 12Hz checkpoints currently emit 24 kHz audio. The exact rate
        # returned by generate_voice_clone is still used for every WAV response.
        runtime.sample_rate = 24_000
        runtime.loaded_at = time.time()
    except Exception as exc:
        runtime.startup_error = f"{type(exc).__name__}: {exc}"
        raise


def create_app():
    if runtime.model is None or runtime.prompt is None:
        load_model()
    return app


if __name__ == "__main__":
    create_app().run(host="0.0.0.0", port=PORT, threaded=True, use_reloader=False)
