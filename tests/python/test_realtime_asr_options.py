"""低延迟改造在 Python 侧引入的纯函数：设备选择、词级时间戳、流式会话。

这三件事都没有对应的 C# 单元测试能覆盖到，而且都有一个共同的失败模式：
出问题时不会报错，只会悄悄退化成慢的那条路径（GPU 没用上、时间戳是空的、
流式会话每次都重建）。所以必须在这里钉住。
"""

import importlib.util
import sys
import types
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SERVICE_DIR = ROOT / "src" / "VoiceScreen.App" / "LocalService"
SERVICE_PATH = SERVICE_DIR / "local_outgoing_service.py"
sys.path.insert(0, str(SERVICE_DIR))

# 和其他 Python 用例一样把重型运行时打桩，保证不装 ctranslate2 / transformers 也能跑。
ctranslate2 = types.ModuleType("ctranslate2")
setattr(ctranslate2, "Translator", object)
setattr(ctranslate2, "get_cuda_device_count", lambda: 0)
transformers = types.ModuleType("transformers")
setattr(transformers, "MarianTokenizer", object)
sys.modules.setdefault("ctranslate2", ctranslate2)
sys.modules.setdefault("transformers", transformers)

spec = importlib.util.spec_from_file_location("voicescreen_realtime_asr_options", SERVICE_PATH)
if spec is None or spec.loader is None:
    raise RuntimeError(f"Unable to load local service from {SERVICE_PATH}")
service = importlib.util.module_from_spec(spec)
spec.loader.exec_module(service)


class Word:
    def __init__(self, word, start, end):
        self.word = word
        self.start = start
        self.end = end


class Segment:
    def __init__(self, words):
        self.words = words


class AsrDeviceTests(unittest.TestCase):
    def test_gpu_aliases_normalize_to_cuda(self):
        self.assertEqual("cuda", service.normalize_asr_device("cuda"))
        self.assertEqual("cuda", service.normalize_asr_device("GPU"))
        self.assertEqual("cuda", service.normalize_asr_device("  Cuda  "))

    def test_cpu_is_explicit(self):
        self.assertEqual("cpu", service.normalize_asr_device("cpu"))

    def test_unknown_and_empty_fall_back_to_auto(self):
        # auto 意味着「探测到显卡就用」，是唯一安全的默认值：
        # 写错设备名不应该让服务起不来，也不应该悄悄锁死在 CPU 上。
        for value in ("", None, "tpu", "auto"):
            self.assertEqual("auto", service.normalize_asr_device(value))

    def test_cuda_probe_survives_a_broken_runtime(self):
        # CTranslate2 可能是 CPU-only 构建，get_cuda_device_count 会直接抛异常。
        # 探测失败必须当成「没有显卡」，而不是让整个启动流程崩掉。
        original = ctranslate2.get_cuda_device_count

        def explode():
            raise RuntimeError("no CUDA runtime")

        ctranslate2.get_cuda_device_count = explode
        try:
            self.assertFalse(service.cuda_is_usable())
        finally:
            ctranslate2.get_cuda_device_count = original

    def test_cuda_probe_reports_available_devices(self):
        original = ctranslate2.get_cuda_device_count
        ctranslate2.get_cuda_device_count = lambda: 1
        try:
            self.assertTrue(service.cuda_is_usable())
        finally:
            ctranslate2.get_cuda_device_count = original


class WordTimestampTests(unittest.TestCase):
    def test_words_are_flattened_across_segments(self):
        segments = [
            Segment([Word(" Enemies", 0.0, 0.62), Word(" are", 0.62, 0.8)]),
            Segment([Word(" here", 0.8, 1.1)]),
        ]
        self.assertEqual(
            [
                {"t": " Enemies", "s": 0.0, "e": 0.62},
                {"t": " are", "s": 0.62, "e": 0.8},
                {"t": " here", "s": 0.8, "e": 1.1},
            ],
            service.collect_word_timestamps(segments),
        )

    def test_segments_without_words_are_tolerated(self):
        # 静音段的 words 是 None。客户端靠时间戳裁剪音频，这里抛异常等于整条
        # 低延迟链路挂掉，所以必须容忍。
        segments = [Segment(None), Segment([Word("hi", 0.0, 0.2)])]
        self.assertEqual([{"t": "hi", "s": 0.0, "e": 0.2}], service.collect_word_timestamps(segments))

    def test_timestamps_are_rounded_to_milliseconds(self):
        segments = [Segment([Word("x", 0.123456, 0.987654)])]
        self.assertEqual([{"t": "x", "s": 0.123, "e": 0.988}], service.collect_word_timestamps(segments))


class FakeStream:
    counter = 0

    def __init__(self):
        FakeStream.counter += 1
        self.id = FakeStream.counter


class FakeRecognizer:
    def create_stream(self):
        return FakeStream()


class SherpaSessionTests(unittest.TestCase):
    def setUp(self):
        sys.modules.setdefault("sherpa_onnx", types.ModuleType("sherpa_onnx"))
        service.State.sherpa = FakeRecognizer()
        service.State.sherpa_streams = {}
        FakeStream.counter = 0

    def tearDown(self):
        service.State.sherpa = None
        service.State.sherpa_streams = {}

    def test_same_session_reuses_the_decoder_state(self):
        # 这就是流式识别的全部意义：解码器状态留在会话里，每次只喂新音频。
        # 一旦这里每次都新建 stream，Zipformer 就退化成批处理模型。
        first = service.acquire_sherpa_stream("7", reset=True)
        second = service.acquire_sherpa_stream("7", reset=False)
        self.assertIs(first, second)

    def test_reset_starts_a_fresh_stream_for_a_new_utterance(self):
        first = service.acquire_sherpa_stream("7", reset=True)
        second = service.acquire_sherpa_stream("7", reset=True)
        self.assertIsNot(first, second)

    def test_different_utterances_get_different_streams(self):
        self.assertIsNot(
            service.acquire_sherpa_stream("7", reset=True),
            service.acquire_sherpa_stream("8", reset=True),
        )

    def test_release_drops_the_session(self):
        service.acquire_sherpa_stream("7", reset=True)
        service.release_sherpa_stream("7")
        self.assertNotIn("7", service.State.sherpa_streams)

    def test_orphan_sessions_cannot_grow_without_bound(self):
        # 客户端崩溃会留下没人 release 的会话。上限保证内存不会被慢慢吃光。
        for index in range(20):
            service.acquire_sherpa_stream(str(index), reset=True)
        self.assertLessEqual(len(service.State.sherpa_streams), 8)


class KeepAliveTests(unittest.TestCase):
    def test_handler_declares_http_11(self):
        # HTTP/1.0 每个请求都关连接，低延迟模式每 600ms 就要重做一次 TCP 握手。
        self.assertEqual("HTTP/1.1", service.Handler.protocol_version)


if __name__ == "__main__":
    unittest.main()
