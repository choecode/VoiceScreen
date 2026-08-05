"""ASR 引擎对比基准。

用 LibriSpeech（CC BY 4.0，自带官方转写）测词错误率，因为只有带标准答案的语料
才能给出可比较的数字。两种条件：

  clean   —— 原始录音，代表安静环境下的上限。
  discord —— 叠加白噪到约 10dB SNR 并做 300-3400Hz 带限，粗略模拟游戏语音链路
             （背景噪声 + 窄带编解码）。这一列比 clean 更接近实际使用场景。

推理参数刻意和 LocalIncomingAudioProcessor 走的那套保持一致，否则测出来的数字
和应用里的实际表现对不上。

用法：
    python tools/benchmark_asr.py --download     # 首次：抓取基准集再评测
    python tools/benchmark_asr.py                # 复用已下载的基准集
    python tools/benchmark_asr.py --engines small sherpa
"""

import argparse
import json
import re
import tempfile
import time
import urllib.parse
import urllib.request
from pathlib import Path

import numpy as np

SAMPLE_RATE = 16_000

DEFAULT_DATA_DIR = Path(tempfile.gettempdir()) / "voicescreen-asrbench"

# LibriSpeech test-clean 的一个小子集，CC BY 4.0，带官方转写。
DATASET = "hf-internal-testing/librispeech_asr_dummy"
ROWS_ENDPOINT = "https://datasets-server.huggingface.co/rows"


def download_dataset(root, count):
    """抓取基准音频与参考文本，让整个评测可以从零复现。"""
    audio_dir = root / "audio"
    audio_dir.mkdir(parents=True, exist_ok=True)
    query = urllib.parse.urlencode({
        "dataset": DATASET, "config": "clean", "split": "validation",
        "offset": 0, "length": count,
    })
    with urllib.request.urlopen(f"{ROWS_ENDPOINT}?{query}", timeout=120) as response:
        payload = json.load(response)

    manifest = []
    for entry in payload.get("rows", []):
        row = entry["row"]
        index = entry["row_idx"]
        relative = f"audio/{index:03d}.flac"
        urllib.request.urlretrieve(row["audio"][0]["src"], root / relative)
        manifest.append({"id": index, "path": relative, "text": row["text"]})

    (root / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=1),
                                        encoding="utf-8")
    print(f"已下载 {len(manifest)} 条基准音频到 {root}")

# 与 local_outgoing_service.py 中的 transcribe 保持一致。
APP_WHISPER_OPTIONS = dict(
    beam_size=1,
    best_of=1,
    temperature=0,
    vad_filter=False,
    condition_on_previous_text=False,
    without_timestamps=True,
)


def normalize(text):
    """WER 前的文本规范化：忽略大小写、标点和多余空白。"""
    text = text.lower()
    text = re.sub(r"[^a-z0-9一-鿿\s']", " ", text)
    return re.sub(r"\s+", " ", text).strip()


def word_error_rate(reference, hypothesis):
    """标准 Levenshtein 词错误率。返回 (WER, 参考词数)。"""
    ref = normalize(reference).split()
    hyp = normalize(hypothesis).split()
    if not ref:
        return (0.0 if not hyp else 1.0), 0

    previous = list(range(len(hyp) + 1))
    for i, ref_word in enumerate(ref, start=1):
        current = [i]
        for j, hyp_word in enumerate(hyp, start=1):
            cost = 0 if ref_word == hyp_word else 1
            current.append(min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + cost))
        previous = current
    return previous[-1] / len(ref), len(ref)


def degrade_to_discord(audio, rng):
    """叠噪 + 带限，粗略模拟游戏语音聊天链路。"""
    speech_power = float(np.mean(audio ** 2)) or 1e-12
    noise = rng.normal(0, np.sqrt(speech_power / 10 ** (10 / 10)), audio.shape).astype(np.float32)
    noisy = audio + noise

    # 频域截取 300-3400Hz，模拟窄带编解码。
    spectrum = np.fft.rfft(noisy)
    freqs = np.fft.rfftfreq(len(noisy), 1 / SAMPLE_RATE)
    spectrum[(freqs < 300) | (freqs > 3400)] = 0
    filtered = np.fft.irfft(spectrum, n=len(noisy)).astype(np.float32)

    peak = float(np.max(np.abs(filtered))) or 1.0
    return (filtered / peak * 0.9).astype(np.float32)


class WhisperEngine:
    def __init__(self, size):
        from faster_whisper import WhisperModel

        self.name = f"whisper-{size}"
        threads = 8 if size == "small" else 6
        self.model = WhisperModel(size, device="cpu", compute_type="int8",
                                  cpu_threads=threads, num_workers=1, local_files_only=True)

    def transcribe(self, audio):
        segments, _ = self.model.transcribe(audio, **APP_WHISPER_OPTIONS)
        return "".join(segment.text for segment in segments).strip()


class SherpaEngine:
    def __init__(self, model_root):
        import sys

        sys.path.insert(0, str(Path(__file__).resolve().parent.parent
                              / "src" / "VoiceScreen.App" / "LocalService"))
        import importlib.util

        service_path = (Path(__file__).resolve().parent.parent / "src" / "VoiceScreen.App"
                        / "LocalService" / "local_outgoing_service.py")
        spec = importlib.util.spec_from_file_location("svc", service_path)
        self.service = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.service)
        self.service.initialize_sherpa_asr()
        self.recognizer = self.service.State.sherpa
        self.name = "sherpa-zipformer"

    def transcribe(self, audio):
        stream = self.recognizer.create_stream()
        stream.accept_waveform(SAMPLE_RATE, audio)
        # 尾部补静音，让流式解码器吐出最后一个词。
        stream.accept_waveform(SAMPLE_RATE, np.zeros(int(0.66 * SAMPLE_RATE), dtype=np.float32))
        stream.input_finished()
        while self.recognizer.is_ready(stream):
            self.recognizer.decode_streams([stream])
        return self.service.read_sherpa_result(self.recognizer.get_result(stream))


def load_audio(path):
    from faster_whisper.audio import decode_audio

    return decode_audio(str(path), sampling_rate=SAMPLE_RATE)


def evaluate(engine, clips, condition, rng_seed=1234):
    rng = np.random.default_rng(rng_seed)
    total_errors = 0.0
    total_words = 0
    total_audio_seconds = 0.0
    total_compute_seconds = 0.0
    samples = []

    for clip in clips:
        audio = clip["audio"] if condition == "clean" else degrade_to_discord(clip["audio"], rng)
        started = time.perf_counter()
        hypothesis = engine.transcribe(audio)
        elapsed = time.perf_counter() - started

        rate, words = word_error_rate(clip["text"], hypothesis)
        total_errors += rate * words
        total_words += words
        total_audio_seconds += len(audio) / SAMPLE_RATE
        total_compute_seconds += elapsed
        samples.append({"reference": clip["text"], "hypothesis": hypothesis, "wer": rate})

    return {
        "engine": engine.name,
        "condition": condition,
        "wer": total_errors / max(total_words, 1),
        "rtf": total_compute_seconds / max(total_audio_seconds, 1e-9),
        "audioSeconds": total_audio_seconds,
        "computeSeconds": total_compute_seconds,
        "samples": samples,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", default=str(DEFAULT_DATA_DIR), help="基准集目录")
    parser.add_argument("--download", action="store_true", help="先抓取基准集")
    parser.add_argument("--clips", type=int, default=20, help="下载的音频条数")
    parser.add_argument("--engines", nargs="*", default=["base", "small", "sherpa"])
    parser.add_argument("--conditions", nargs="*", default=["clean", "discord"])
    parser.add_argument("--out", default=None, help="结果 JSON 路径，默认写到基准集目录")
    args = parser.parse_args()

    root = Path(args.data)
    output = Path(args.out) if args.out else root / "results.json"
    if args.download or not (root / "manifest.json").exists():
        download_dataset(root, args.clips)

    manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
    clips = [{"text": item["text"], "audio": load_audio(root / item["path"])} for item in manifest]
    print(f"基准集：{len(clips)} 条，共 "
          f"{sum(len(c['audio']) for c in clips) / SAMPLE_RATE:.1f} 秒音频", flush=True)

    results = []
    for key in args.engines:
        print(f"\n加载 {key} ...", flush=True)
        engine = WhisperEngine(key) if key in ("base", "small") else SherpaEngine(None)
        for condition in args.conditions:
            result = evaluate(engine, clips, condition)
            results.append(result)
            print(f"  {result['engine']:18s} {condition:8s} "
                  f"WER={result['wer'] * 100:6.2f}%  RTF={result['rtf']:.3f}", flush=True)

    output.write_text(json.dumps(results, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n明细已写入 {output}")


if __name__ == "__main__":
    main()
