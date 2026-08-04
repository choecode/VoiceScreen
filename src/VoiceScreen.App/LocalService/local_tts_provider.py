"""Local Piper TTS adapter for the server-side evaluation lab."""

import os
import subprocess
import sys
import tempfile
import time
import wave
from pathlib import Path


VOICE_CATALOG = {
    "en_US-lessac-medium": {
        "label": "Lessac · 美式英文（默认）",
        "license": "Blizzard 2013 Lessac dataset license",
        "repositoryPath": "en/en_US/lessac/medium/en_US-lessac-medium",
    },
    "en_US-joe-medium": {
        "label": "Joe · 美式英文男声",
        "license": "CC0",
        "repositoryPath": "en/en_US/joe/medium/en_US-joe-medium",
    },
    "en_US-mike-medium": {
        "label": "Mike · 美式英文男声",
        "license": "CC0",
        "repositoryPath": "en/en_US/mike/medium/en_US-mike-medium",
    },
    "en_US-john-medium": {
        "label": "John · 美式英文男声",
        "license": "Public Domain",
        "repositoryPath": "en/en_US/john/medium/en_US-john-medium",
    },
    "zh_CN-huayan-medium": {
        "label": "Huayan · 普通话",
        "license": "Unknown dataset license",
        "repositoryPath": "zh/zh_CN/huayan/medium/zh_CN-huayan-medium",
    },
}

TARGET_VOICES = {
    "zh-en": "en_US-lessac-medium",
    "en-zh": "zh_CN-huayan-medium",
    "th-zh": "zh_CN-huayan-medium",
}
ALLOWED_VOICES = {
    "zh-en": {
        "en_US-lessac-medium",
        "en_US-joe-medium",
        "en_US-mike-medium",
        "en_US-john-medium",
    },
    "en-zh": {"zh_CN-huayan-medium"},
    "th-zh": {"zh_CN-huayan-medium"},
}


def voices_for_direction(direction):
    if direction not in ALLOWED_VOICES:
        raise ValueError("direction must be zh-en, en-zh, or th-zh")
    default = TARGET_VOICES[direction]
    return [default, *[
        voice for voice in VOICE_CATALOG
        if voice in ALLOWED_VOICES[direction] and voice != default
    ]]


def voice_labels():
    return {voice: metadata["label"] for voice, metadata in VOICE_CATALOG.items()}


def voice_licenses():
    return {voice: metadata["license"] for voice, metadata in VOICE_CATALOG.items()}


class LocalTtsError(RuntimeError):
    pass


def piper_executable():
    candidate = Path(sys.executable).parent / "piper"
    return candidate if candidate.is_file() and os.access(candidate, os.X_OK) else None


def voice_availability(model_root):
    root = Path(model_root) / "piper"
    runtime_ready = piper_executable() is not None
    return {
        voice: runtime_ready
        and (root / f"{voice}.onnx").is_file()
        and (root / f"{voice}.onnx.json").is_file()
        for voice in VOICE_CATALOG
    }


def selected_voice(direction, requested=None):
    default = TARGET_VOICES.get(direction)
    if not default:
        raise ValueError("direction must be zh-en, en-zh, or th-zh")
    voice = requested or default
    if voice not in ALLOWED_VOICES[direction]:
        raise ValueError("local Piper voice is not allowed for the selected direction")
    return voice


def piper_available(model_root):
    availability = voice_availability(model_root)
    return all(availability[voice] for voice in set(TARGET_VOICES.values()))


def synthesize_piper(text, direction, model_root, voice=None):
    executable = piper_executable()
    if executable is None:
        raise LocalTtsError("Piper runtime is not installed")
    voice_name = selected_voice(direction, voice)
    root = Path(model_root) / "piper"
    model = root / f"{voice_name}.onnx"
    config = root / f"{voice_name}.onnx.json"
    if not model.is_file() or not config.is_file():
        raise LocalTtsError(f"Piper voice is not installed: {voice_name}")

    output_path = None
    started = time.perf_counter()
    try:
        with tempfile.NamedTemporaryFile(prefix="voicescreen-piper-", suffix=".wav", delete=False) as output:
            output_path = Path(output.name)
        subprocess.run([
            str(executable),
            "--model", str(model),
            "--config", str(config),
            "--output_file", str(output_path),
            "--sentence-silence", "0.15",
        ], input=(text.strip() + "\n").encode("utf-8"), stdout=subprocess.DEVNULL,
           stderr=subprocess.PIPE, check=True, timeout=60)
        latency_ms = round((time.perf_counter() - started) * 1000, 2)
        audio = output_path.read_bytes()
        with wave.open(str(output_path), "rb") as wav:
            duration_ms = round(wav.getnframes() / wav.getframerate() * 1000, 2)
    except (subprocess.SubprocessError, OSError, wave.Error) as exc:
        raise LocalTtsError("Piper synthesis failed") from exc
    finally:
        if output_path:
            output_path.unlink(missing_ok=True)

    if not audio or duration_ms <= 0:
        raise LocalTtsError("Piper returned invalid audio")
    return audio, {
        "provider": "piper-tts",
        "voice": voice_name,
        "latencyMs": latency_ms,
        "firstByteLatencyMs": None,
        "audioDurationMs": duration_ms,
        "realTimeFactor": round(latency_ms / duration_ms, 3),
        "charactersPerSecond": round(len(text) / max(latency_ms / 1000, 0.001), 2),
        "audioBytes": len(audio),
        "contentType": "audio/wav",
        "runtimeLicense": "GPL-3.0",
        "voiceLicense": VOICE_CATALOG[voice_name]["license"],
    }
