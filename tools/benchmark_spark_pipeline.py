#!/usr/bin/env python3
"""Repeatable VoiceScreen Spark translation/TTS latency benchmark.

The API token is read from VOICESCREEN_API_TOKEN and is never printed. The
script compares the production Qwen clone endpoint with the experimental
CosyVoice full and true-streaming endpoints using identical translated text.
"""

from __future__ import annotations

import argparse
import io
import json
import os
import statistics
import time
import urllib.request
import wave


CASES = [
    {
        "name": "short",
        "chinese": "请保持这个位置，等第一队进入仓库以后再向右移动，不要穿过空地。",
    },
    {
        "name": "medium",
        "chinese": (
            "如果他们从北侧入口进来，你先告诉我人数和装备，不要立刻开火。"
            "等我确认第二个小队已经到达以后，再沿着右边的墙向前推进，并且始终保持隐蔽。"
        ),
    },
    {
        "name": "long",
        "chinese": (
            "我们现在需要先确认仓库里面还有多少人，以及他们是否已经发现我们。"
            "你带两个人留在南门观察，其余人绕到东侧的楼梯口。"
            "如果三分钟内没有新的车辆进入，就由第一组切断照明，第二组同时进入；"
            "但是只要看到平民，所有人立刻停止行动并退回原来的位置，等我重新下命令。"
        ),
    },
]


def post_json(url: str, payload: dict, token: str, timeout: int = 180):
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=body,
        method="POST",
        headers={
            "Content-Type": "application/json",
            "X-VoiceScreen-Token": token,
        },
    )
    started = time.perf_counter()
    response = urllib.request.urlopen(request, timeout=timeout)
    headers_ms = (time.perf_counter() - started) * 1000
    return response, started, headers_ms


def translate(base_url: str, text: str, token: str) -> tuple[str, float]:
    response, started, _ = post_json(
        f"{base_url}:18765/translate",
        {"text": text, "direction": "zh-en"},
        token,
    )
    with response:
        payload = json.loads(response.read())
    return str(payload["text"]).strip(), (time.perf_counter() - started) * 1000


def wav_duration_ms(data: bytes) -> float:
    with wave.open(io.BytesIO(data), "rb") as reader:
        return reader.getnframes() / reader.getframerate() * 1000


def split_english(text: str, preferred: int = 60, maximum: int = 80) -> list[str]:
    """Mirror VoiceScreen.Core.SpeechChunker for remote benchmark runs."""
    dangling = {
        "a", "an", "the", "and", "or", "but", "if", "because", "while", "when",
        "after", "before", "until", "unless", "whether", "that", "which", "who",
        "whose", "where", "to", "of", "for", "from", "with", "without", "into",
        "onto", "at", "by", "as", "is", "are", "was", "were", "be", "been",
        "being", "do", "does", "did", "don't", "doesn't", "didn't", "not", "no",
        "can", "could", "should", "would", "will", "won't", "may", "might", "must",
        "have", "has", "had",
    }

    def dangling_ending(value: str) -> bool:
        token = value.rstrip()
        end = len(token) - 1
        while end >= 0 and not (token[end].isalpha() or token[end] == "'"):
            end -= 1
        start = end
        while start >= 0 and (token[start].isalpha() or token[start] == "'"):
            start -= 1
        return token[start + 1 : end + 1].lower() in dangling

    remaining = " ".join(text.split())
    chunks: list[str] = []
    while len(remaining) > maximum:
        minimum = max(12, preferred // 2)
        split = -1
        for boundaries in (".?!", ",;:", None):
            fallback = -1
            for index in range(min(maximum, len(remaining) - 1), minimum - 1, -1):
                value = remaining[index]
                matched = value.isspace() if boundaries is None else value in boundaries
                if matched:
                    candidate = index if value.isspace() else index + 1
                    fallback = candidate if fallback < 0 else fallback
                    if boundaries is not None or not dangling_ending(remaining[:candidate]):
                        split = candidate
                        break
            if split <= 0 and boundaries is None:
                split = fallback
            if split > 0:
                break
        if split <= 0:
            split = maximum
        chunks.append(remaining[:split].strip())
        remaining = remaining[split:].lstrip()
    if remaining:
        chunks.append(remaining)
    return chunks


def full_tts(url: str, text: str, token: str) -> dict:
    response, started, headers_ms = post_json(
        url,
        {"text": text, "language": "English"},
        token,
        timeout=300,
    )
    with response:
        headers = dict(response.headers.items())
        audio = response.read()
    total_ms = (time.perf_counter() - started) * 1000
    duration_ms = wav_duration_ms(audio)
    server_ms = float(headers.get("X-VoiceScreen-Synthesis-Ms", total_ms))
    first_chunk = headers.get("X-VoiceScreen-First-Chunk-Ms")
    return {
        "headersMs": round(headers_ms),
        "totalMs": round(total_ms),
        "serverMs": round(server_ms),
        "modelFirstChunkMs": round(float(first_chunk)) if first_chunk else None,
        "audioMs": round(duration_ms),
        "rtf": round(server_ms / duration_ms, 3) if duration_ms else None,
        "bytes": len(audio),
    }


def streaming_tts(url: str, text: str, token: str) -> dict:
    response, started, headers_ms = post_json(
        url,
        {"text": text, "language": "English"},
        token,
        timeout=300,
    )
    sample_rate = int(response.headers.get("X-VoiceScreen-Sample-Rate", "24000"))
    first = response.read(4096)
    first_audio_ms = (time.perf_counter() - started) * 1000
    remainder = response.read()
    response.close()
    total_ms = (time.perf_counter() - started) * 1000
    byte_count = len(first) + len(remainder)
    audio_ms = byte_count / 2 / sample_rate * 1000
    return {
        "headersMs": round(headers_ms),
        "firstAudioMs": round(first_audio_ms),
        "totalMs": round(total_ms),
        "audioMs": round(audio_ms),
        "rtf": round(total_ms / audio_ms, 3) if audio_ms else None,
        "bytes": byte_count,
    }


def percentile(values: list[float], ratio: float) -> float:
    ordered = sorted(values)
    if not ordered:
        return 0
    return ordered[min(len(ordered) - 1, round((len(ordered) - 1) * ratio))]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="http://127.0.0.1")
    parser.add_argument("--warmup", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument(
        "--qwen-first-only",
        action="store_true",
        help="measure only the first chunk used by the current Windows client",
    )
    args = parser.parse_args()
    token = os.environ.get("VOICESCREEN_API_TOKEN", "").strip()
    if not token:
        parser.error("VOICESCREEN_API_TOKEN is not set")

    if args.warmup:
        warmup_text = "Please hold this position."
        full_tts(f"{args.host}:18766/synthesize", warmup_text, token)
        streaming_tts(f"{args.host}:18767/synthesize-stream", warmup_text, token)

    if args.qwen_first_only:
        output = []
        for case in CASES:
            english, translation_ms = translate(args.host, case["chinese"], token)
            chunks = split_english(english)
            first = full_tts(
                f"{args.host}:18766/synthesize", chunks[0], token
            )
            output.append(
                {
                    "name": case["name"],
                    "translationMs": round(translation_ms),
                    "englishChars": len(english),
                    "chunks": len(chunks),
                    "firstChunkChars": len(chunks[0]),
                    "firstChunk": chunks[0],
                    "qwenFirstChunk": first,
                    "estimatedFirstAudioMs": round(translation_ms + first["totalMs"]),
                }
            )
        print(json.dumps({"results": output}, ensure_ascii=False, indent=2))
        return 0

    results = []
    for case in CASES:
        english, translation_ms = translate(args.host, case["chinese"], token)
        qwen = full_tts(f"{args.host}:18766/synthesize", english, token)
        cosy = full_tts(f"{args.host}:18767/synthesize", english, token)
        cosy_stream = streaming_tts(
            f"{args.host}:18767/synthesize-stream", english, token
        )
        results.append(
            {
                "name": case["name"],
                "chineseChars": len(case["chinese"]),
                "englishChars": len(english),
                "english": english,
                "translationMs": round(translation_ms),
                "qwenFull": qwen,
                "cosyFull": cosy,
                "cosyStream": cosy_stream,
                "estimatedFirstAudioMs": {
                    "qwenWholeSentence": round(translation_ms + qwen["totalMs"]),
                    "cosyStreaming": round(translation_ms + cosy_stream["firstAudioMs"]),
                },
            }
        )

    summary = {
        "translationMedianMs": round(
            statistics.median(item["translationMs"] for item in results)
        ),
        "qwenTotalMedianMs": round(
            statistics.median(item["qwenFull"]["totalMs"] for item in results)
        ),
        "cosyFirstAudioMedianMs": round(
            statistics.median(item["cosyStream"]["firstAudioMs"] for item in results)
        ),
        "cosyTotalMedianMs": round(
            statistics.median(item["cosyStream"]["totalMs"] for item in results)
        ),
        "cosyFirstAudioP95Ms": round(
            percentile([item["cosyStream"]["firstAudioMs"] for item in results], 0.95)
        ),
    }
    print(
        json.dumps(
            {
                "host": args.host,
                "generatedAtUnix": int(time.time()),
                "results": results,
                "summary": summary,
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
