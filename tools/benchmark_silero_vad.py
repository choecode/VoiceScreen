#!/usr/bin/env python3
"""Compare Silero speech probabilities with VoiceScreen's legacy RMS gate."""

from __future__ import annotations

import argparse
import json
import time

import numpy as np
import onnxruntime
from scipy.signal import resample_poly
import soundfile as sf


SAMPLE_RATE = 16_000
WINDOW = 512
CONTEXT = 64
RMS_THRESHOLD = 120 / 32768


def load_audio(path: str) -> np.ndarray:
    audio, sample_rate = sf.read(path, always_2d=True, dtype="float32")
    mono = audio.mean(axis=1)
    if sample_rate != SAMPLE_RATE:
        divisor = np.gcd(sample_rate, SAMPLE_RATE)
        mono = resample_poly(
            mono, SAMPLE_RATE // divisor, sample_rate // divisor
        ).astype(np.float32)
    return mono


def evaluate(session: onnxruntime.InferenceSession, audio: np.ndarray) -> dict:
    if len(audio) % WINDOW:
        audio = np.pad(audio, (0, WINDOW - len(audio) % WINDOW))
    state = np.zeros((2, 1, 128), dtype=np.float32)
    context = np.zeros((CONTEXT,), dtype=np.float32)
    probabilities = []
    rms_values = []
    inference_ms = []
    for offset in range(0, len(audio), WINDOW):
        window = audio[offset : offset + WINDOW].astype(np.float32, copy=False)
        model_input = np.concatenate((context, window))[None, :]
        started = time.perf_counter()
        output, state = session.run(
            None,
            {
                "input": model_input,
                "state": state,
                "sr": np.array(SAMPLE_RATE, dtype=np.int64),
            },
        )
        inference_ms.append((time.perf_counter() - started) * 1000)
        probabilities.append(float(output.reshape(-1)[0]))
        rms_values.append(float(np.sqrt(np.mean(np.square(window), dtype=np.float64))))
        context = window[-CONTEXT:]
    values = np.array(probabilities)
    rms = np.array(rms_values)
    return {
        "durationMs": round(len(audio) / SAMPLE_RATE * 1000),
        "frames": len(values),
        "sileroSpeechPercent": round(float(np.mean(values >= 0.5) * 100), 1),
        "rmsVoicedPercent": round(float(np.mean(rms >= RMS_THRESHOLD) * 100), 1),
        "probabilityMedian": round(float(np.median(values)), 4),
        "probabilityP95": round(float(np.percentile(values, 95)), 4),
        "inferenceMedianMs": round(float(np.median(inference_ms)), 3),
        "inferenceP95Ms": round(float(np.percentile(inference_ms, 95)), 3),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--speech", required=True)
    args = parser.parse_args()

    speech = load_audio(args.speech)
    seconds = max(3, min(10, len(speech) / SAMPLE_RATE))
    samples = int(seconds * SAMPLE_RATE)
    time_axis = np.arange(samples, dtype=np.float32) / SAMPLE_RATE
    random = np.random.default_rng(20260825)
    fixtures = {
        "referenceSpeech": speech[:samples],
        "pureTone": (0.03 * np.sin(2 * np.pi * 440 * time_axis)).astype(np.float32),
        "whiteNoise": (0.03 * random.standard_normal(samples)).astype(np.float32),
        "silence": np.zeros(samples, dtype=np.float32),
    }
    session = onnxruntime.InferenceSession(
        args.model,
        providers=["CPUExecutionProvider"],
        sess_options=onnxruntime.SessionOptions(),
    )
    results = {name: evaluate(session, audio) for name, audio in fixtures.items()}
    print(json.dumps(results, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
