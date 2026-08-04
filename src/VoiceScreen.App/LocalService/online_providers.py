"""Optional no-key online providers for VoiceScreen's comparison lab."""

import html
import json
import time
from io import BytesIO
from urllib.parse import urlencode
from urllib.request import Request, urlopen


MYMEMORY_URL = "https://api.mymemory.translated.net/get"
LANGUAGE_PAIRS = {
    "zh-en": "zh-CN|en",
    "en-zh": "en|zh-CN",
    "th-zh": "th|zh-CN",
}
TARGET_VOICES = {
    "zh-en": "en-US-JennyNeural",
    "en-zh": "zh-CN-XiaoxiaoNeural",
    "th-zh": "zh-CN-XiaoxiaoNeural",
}


class OnlineProviderError(RuntimeError):
    pass


def translate_mymemory(text, direction, timeout=25, opener=urlopen):
    language_pair = LANGUAGE_PAIRS.get(direction)
    if not language_pair:
        raise ValueError("direction must be zh-en, en-zh, or th-zh")
    if len(text.encode("utf-8")) > 480:
        raise ValueError("MyMemory evaluation text must be at most 480 UTF-8 bytes")

    url = f"{MYMEMORY_URL}?{urlencode({'q': text, 'langpair': language_pair})}"
    request = Request(url, headers={
        "Accept": "application/json",
        "User-Agent": "VoiceScreen-Evaluation-Lab/1.0",
    })
    started = time.perf_counter()
    try:
        with opener(request, timeout=timeout) as response:
            payload = json.loads(response.read(256 * 1024).decode("utf-8"))
    except Exception as exc:
        raise OnlineProviderError("MyMemory translation request failed") from exc

    status = payload.get("responseStatus")
    translated = payload.get("responseData", {}).get("translatedText")
    if status != 200 or not isinstance(translated, str) or not translated.strip():
        raise OnlineProviderError("MyMemory did not return a usable translation")
    translated = html.unescape(translated).strip()
    latency_ms = round((time.perf_counter() - started) * 1000, 2)
    match = payload.get("responseData", {}).get("match")
    return {
        "translatedText": translated,
        "translationLatencyMs": latency_ms,
        "providerMatch": match if isinstance(match, (int, float)) else None,
        "languagePair": language_pair,
    }


async def synthesize_edge(text, voice):
    try:
        import edge_tts
        from mutagen.mp3 import MP3
    except ModuleNotFoundError as exc:
        raise OnlineProviderError("Edge TTS dependencies are not installed") from exc

    started = time.perf_counter()
    first_byte_ms = None
    chunks = []
    try:
        communicate = edge_tts.Communicate(text, voice)
        async for chunk in communicate.stream():
            if chunk.get("type") != "audio":
                continue
            if first_byte_ms is None:
                first_byte_ms = round((time.perf_counter() - started) * 1000, 2)
            chunks.append(chunk["data"])
    except Exception as exc:
        raise OnlineProviderError("Edge TTS synthesis failed") from exc

    audio = b"".join(chunks)
    if not audio:
        raise OnlineProviderError("Edge TTS returned empty audio")
    latency_ms = round((time.perf_counter() - started) * 1000, 2)
    try:
        duration_ms = round(MP3(BytesIO(audio)).info.length * 1000, 2)
    except Exception as exc:
        raise OnlineProviderError("Unable to measure Edge TTS audio") from exc
    if duration_ms <= 0:
        raise OnlineProviderError("Edge TTS returned invalid audio duration")

    return audio, {
        "provider": "edge-tts",
        "voice": voice,
        "latencyMs": latency_ms,
        "firstByteLatencyMs": first_byte_ms,
        "audioDurationMs": duration_ms,
        "realTimeFactor": round(latency_ms / duration_ms, 3),
        "charactersPerSecond": round(len(text) / max(latency_ms / 1000, 0.001), 2),
        "audioBytes": len(audio),
        "contentType": "audio/mpeg",
    }


def target_voice(direction, requested=None):
    default = TARGET_VOICES.get(direction)
    if not default:
        raise ValueError("direction must be zh-en, en-zh, or th-zh")
    allowed = {
        "zh-en": {"en-US-JennyNeural", "en-US-GuyNeural"},
        "en-zh": {"zh-CN-XiaoxiaoNeural", "zh-CN-YunxiNeural"},
        "th-zh": {"zh-CN-XiaoxiaoNeural", "zh-CN-YunxiNeural"},
    }[direction]
    voice = requested or default
    if voice not in allowed:
        raise ValueError("voice is not allowed for the selected translation direction")
    return voice
