"""VoiceScreen LAN model service for NVIDIA DGX Spark.

Contract-compatible with the Windows client: raw PCM16/16 kHz requests go to
/transcribe, JSON translation requests go to /translate, and /segment uses the
resident instruction model as a constrained subtitle-boundary classifier.
"""

from __future__ import annotations

import os
import re
import threading
import time
from dataclasses import dataclass, field

import numpy as np
import torch
from flask import Flask, jsonify, request
from qwen_asr import Qwen3ASRModel
from qwen_asr.inference.qwen3_asr import parse_asr_output
from transformers import AutoModelForCausalLM, AutoTokenizer


ASR_MODEL_PATH = os.environ.get("VOICESCREEN_ASR_MODEL", "/models/Qwen3-ASR-1.7B")
TRANSLATION_MODEL_PATH = os.environ.get(
    "VOICESCREEN_TRANSLATION_MODEL", "/models/Qwen3-4B-Instruct-2507"
)
API_TOKEN = os.environ.get("VOICESCREEN_API_TOKEN", "")
BIND_HOST = os.environ.get("VOICESCREEN_BIND_HOST", "0.0.0.0")
PORT = int(os.environ.get("VOICESCREEN_PORT", "18765"))
STREAMING_CHUNK_SAMPLES = 16_000  # 1 second at 16 kHz
STREAMING_MAX_SAMPLES = 16_000 * 60
STREAMING_SESSION_TTL_SECONDS = 120
STREAMING_MAX_SESSIONS = 32
STREAMING_UNFIXED_CHUNK_NUM = 2
STREAMING_UNFIXED_TOKEN_NUM = 5

app = Flask(__name__)


@dataclass
class Models:
    asr: Qwen3ASRModel | None = None
    tokenizer: object | None = None
    translator: object | None = None
    loaded_at: float = 0.0
    startup_error: str = ""


models = Models()
asr_lock = threading.Lock()
translation_lock = threading.Lock()


@dataclass
class ASRStreamingSession:
    audio: np.ndarray = field(default_factory=lambda: np.zeros((0,), dtype=np.float32))
    decoded_samples: int = 0
    chunk_id: int = 0
    raw_decoded: str = ""
    text: str = ""
    language: str = ""
    prompt_raw: str = ""
    forced_language: str | None = None
    fallback_language: str = "auto"
    last_seen: float = field(default_factory=time.time)


streaming_sessions: dict[str, ASRStreamingSession] = {}


def require_token() -> None:
    if not API_TOKEN:
        raise RuntimeError("VOICESCREEN_API_TOKEN is not configured")
    provided = request.headers.get("X-VoiceScreen-Token", "")
    if provided != API_TOKEN:
        from werkzeug.exceptions import Unauthorized

        raise Unauthorized("invalid VoiceScreen API token")


def normalize_language(value: str | None) -> tuple[str | None, str]:
    requested = (value or "auto").strip().lower()
    if requested in {"zh", "chinese", "cmn"}:
        return "Chinese", "zh"
    if requested in {"en", "english"}:
        return "English", "en"
    if requested in {"th", "thai"}:
        return "Thai", "th"
    return None, "auto"


def response_language(value: str | None, fallback: str) -> str:
    language = (value or "").strip().lower()
    if language.startswith("chinese") or language in {"zh", "cmn"}:
        return "zh"
    if language.startswith("english") or language == "en":
        return "en"
    if language.startswith("thai") or language == "th":
        return "th"
    return fallback if fallback != "auto" else "unknown"


def clean_translation(text: str) -> str:
    text = text.strip()
    text = re.sub(r"^(translation|译文|翻译)\s*[:：]\s*", "", text, flags=re.I)
    return text.strip().strip('"“”')


def is_break_decision(text: str) -> bool:
    """Accept only the classifier's explicit BREAK token; every other answer is safe-continuation."""
    return (text or "").strip().upper().split(maxsplit=1)[0:1] == ["BREAK"]


@app.get("/health")
def health():
    ready = models.asr is not None and models.translator is not None
    payload = {
        "asr": "qwen3-asr-1.7b" if models.asr is not None else "loading",
        "translation": "qwen3-4b-instruct-2507" if models.translator is not None else "loading",
        "segmentation": "qwen3-4b-instruct-2507" if models.translator is not None else "loading",
        "asrStreaming": models.asr is not None,
        "asrStreamingChunkMs": 1000,
        "asrDevice": "spark-gpu" if torch.cuda.is_available() else "cpu",
        "ready": ready,
        "uptimeSeconds": max(0, int(time.time() - models.loaded_at)) if models.loaded_at else 0,
    }
    if models.startup_error:
        payload["error"] = models.startup_error
    return jsonify(payload), 200 if ready else 503


@app.post("/transcribe")
def transcribe():
    require_token()
    if models.asr is None:
        return jsonify(error="ASR model is not ready"), 503
    session_id = str(request.args.get("session", "")).strip()
    if session_id and not re.fullmatch(r"[A-Za-z0-9_.-]{1,128}", session_id):
        return jsonify(error="invalid streaming session id"), 400
    is_final = str(request.args.get("mode", "preview")).lower() == "final"
    reset_session = str(request.args.get("reset", "0")).lower() in {"1", "true", "yes"}

    pcm = request.get_data(cache=False)
    if not pcm or (not session_id and len(pcm) < 3200):
        return jsonify(text="", language="unknown", words=[])

    audio = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
    forced_language, fallback = normalize_language(request.args.get("language"))

    if session_id:
        with asr_lock, torch.inference_mode():
            cleanup_streaming_sessions()
            session = streaming_sessions.get(session_id)
            if session is None or reset_session:
                session = create_streaming_session(forced_language, fallback)
                streaming_sessions[session_id] = session
                trim_streaming_session_count()
            session.audio = np.concatenate((session.audio, audio))
            session.last_seen = time.time()
            if session.audio.shape[0] > STREAMING_MAX_SAMPLES:
                streaming_sessions.pop(session_id, None)
                return jsonify(error="streaming utterance exceeds 60 seconds"), 413

            if is_final:
                # The preview path favors latency and prefix stability. At the sentence boundary,
                # run the original full-context decoder once so permanent history keeps offline quality.
                result = models.asr.transcribe(
                    audio=(session.audio, 16000), language=session.forced_language
                )[0]
                streaming_sessions.pop(session_id, None)
                text = (result.text or "").strip()
                language = response_language(result.language, session.fallback_language)
            elif session.audio.shape[0] - session.decoded_samples < STREAMING_CHUNK_SAMPLES:
                text = session.text
                language = response_language(session.language, session.fallback_language)
            else:
                decode_streaming_preview(session)
                text = session.text
                language = response_language(session.language, session.fallback_language)
        return jsonify(text=text, language=language, words=[])

    # Silence gates avoid generative ASR hallucinations between video sentences.
    rms = float(np.sqrt(np.mean(np.square(audio), dtype=np.float64)))
    if rms < 0.002:
        return jsonify(text="", language="unknown", words=[])

    with asr_lock, torch.inference_mode():
        result = models.asr.transcribe(audio=(audio, 16000), language=forced_language)[0]
    return jsonify(
        text=(result.text or "").strip(),
        language=response_language(result.language, fallback),
        words=[],
    )


def create_streaming_session(forced_language: str | None, fallback: str) -> ASRStreamingSession:
    assert models.asr is not None
    return ASRStreamingSession(
        prompt_raw=models.asr._build_text_prompt(context="", force_language=forced_language),
        forced_language=forced_language,
        fallback_language=fallback,
    )


def decode_streaming_preview(session: ASRStreamingSession) -> None:
    """Transformers equivalent of Qwen's official prefix-rollback streaming state machine.

    Qwen's public helper artificially restricts this algorithm to its vLLM backend. The actual
    decode is backend-independent: re-read the bounded utterance, commit the old stable prefix,
    and roll back five tokens so the live tail stays revisable. VoiceScreen resets sessions at
    semantic boundaries and at 20 seconds, avoiding the unbounded accumulation reported upstream.
    """
    assert models.asr is not None
    processor = models.asr.processor
    model = models.asr.model
    prefix = ""
    if session.chunk_id >= STREAMING_UNFIXED_CHUNK_NUM and session.raw_decoded:
        token_ids = processor.tokenizer.encode(session.raw_decoded)
        rollback = STREAMING_UNFIXED_TOKEN_NUM
        while True:
            end = max(0, len(token_ids) - rollback)
            prefix = processor.tokenizer.decode(token_ids[:end]) if end > 0 else ""
            if "\ufffd" not in prefix or end == 0:
                break
            rollback += 1

    prompt = session.prompt_raw + prefix
    inputs = processor(text=[prompt], audio=[session.audio], return_tensors="pt", padding=True)
    inputs = inputs.to(model.device).to(model.dtype)
    generated = model.generate(**inputs, max_new_tokens=models.asr.max_new_tokens)
    decoded = processor.batch_decode(
        generated.sequences[:, inputs["input_ids"].shape[1] :],
        skip_special_tokens=True,
        clean_up_tokenization_spaces=False,
    )[0]
    session.raw_decoded = prefix + decoded
    session.language, session.text = parse_asr_output(
        session.raw_decoded, user_language=session.forced_language
    )
    session.decoded_samples = session.audio.shape[0]
    session.chunk_id += 1


def cleanup_streaming_sessions() -> None:
    deadline = time.time() - STREAMING_SESSION_TTL_SECONDS
    expired = [key for key, value in streaming_sessions.items() if value.last_seen < deadline]
    for key in expired:
        streaming_sessions.pop(key, None)


def trim_streaming_session_count() -> None:
    while len(streaming_sessions) > STREAMING_MAX_SESSIONS:
        oldest = min(streaming_sessions, key=lambda key: streaming_sessions[key].last_seen)
        streaming_sessions.pop(oldest, None)


@app.post("/translate")
def translate():
    require_token()
    if models.translator is None or models.tokenizer is None:
        return jsonify(error="translation model is not ready"), 503
    payload = request.get_json(force=True, silent=False)
    source = str(payload.get("text", "")).strip()
    direction = str(payload.get("direction", "")).strip().lower()
    if not source:
        return jsonify(text="")

    prompts = {
        "zh-en": "Translate the Chinese text to concise natural English.",
        "en-zh": "把英文准确翻译为简洁自然的中文。",
        "th-en": "Translate the Thai text to concise natural English.",
    }
    instruction = prompts.get(direction)
    if instruction is None:
        return jsonify(error="direction must be zh-en, en-zh, or th-en"), 400

    messages = [
        {
            "role": "system",
            "content": (
                "You are a real-time speech translator. Preserve names, numbers, negation, "
                "directions and locations exactly. Output only the translation, with no notes."
            ),
        },
        {"role": "user", "content": f"{instruction}\n\n{source}"},
    ]
    tokenizer = models.tokenizer
    with translation_lock, torch.inference_mode():
        encoded = tokenizer.apply_chat_template(
            messages, tokenize=True, add_generation_prompt=True, return_tensors="pt"
        ).to(models.translator.device)
        generated = models.translator.generate(
            encoded,
            max_new_tokens=256,
            do_sample=False,
            use_cache=True,
        )
        answer = tokenizer.decode(generated[0, encoded.shape[-1] :], skip_special_tokens=True)
    return jsonify(text=clean_translation(answer))


@app.post("/segment")
def segment():
    require_token()
    if models.translator is None or models.tokenizer is None:
        return jsonify(error="segmentation model is not ready"), 503
    payload = request.get_json(force=True, silent=False)
    source = str(payload.get("text", "")).strip()
    if not source:
        return jsonify({"break": False})

    messages = [
        {
            "role": "system",
            "content": (
                "You are a boundary classifier for live ASR subtitles. Reply BREAK only when "
                "the transcript ends at a complete, natural thought. Reply CONTINUE when it ends "
                "with a dangling article, preposition, conjunction, unfinished clause, or uncertain "
                "temporary punctuation. Do not rewrite or explain. Output exactly BREAK or CONTINUE."
            ),
        },
        {"role": "user", "content": source},
    ]
    tokenizer = models.tokenizer
    with translation_lock, torch.inference_mode():
        encoded = tokenizer.apply_chat_template(
            messages, tokenize=True, add_generation_prompt=True, return_tensors="pt"
        ).to(models.translator.device)
        generated = models.translator.generate(
            encoded,
            max_new_tokens=4,
            do_sample=False,
            use_cache=True,
        )
        answer = tokenizer.decode(generated[0, encoded.shape[-1] :], skip_special_tokens=True)
    return jsonify({"break": is_break_decision(answer)})


def load_models() -> None:
    try:
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA is unavailable in the container")
        models.asr = Qwen3ASRModel.from_pretrained(
            ASR_MODEL_PATH,
            dtype=torch.bfloat16,
            device_map="cuda:0",
            max_inference_batch_size=4,
            max_new_tokens=512,
            local_files_only=True,
        )
        models.tokenizer = AutoTokenizer.from_pretrained(
            TRANSLATION_MODEL_PATH, local_files_only=True, trust_remote_code=False
        )
        models.translator = AutoModelForCausalLM.from_pretrained(
            TRANSLATION_MODEL_PATH,
            dtype=torch.bfloat16,
            device_map="cuda:0",
            local_files_only=True,
            trust_remote_code=False,
        ).eval()
        models.loaded_at = time.time()
    except Exception as exc:
        models.startup_error = f"{type(exc).__name__}: {exc}"
        raise


def create_app():
    """Gunicorn application factory; one worker owns one resident model pair."""
    if models.asr is None or models.translator is None:
        load_models()
    return app


if __name__ == "__main__":
    create_app().run(host=BIND_HOST, port=PORT, threaded=True, use_reloader=False)
