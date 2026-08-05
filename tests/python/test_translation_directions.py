"""方向词表与术语表的纯函数测试。

这里覆盖的是之前完全没有测试、并且已经出问题的一块：
用户方向（zh-en / en-zh / th-zh）和模型对（zh-en / en-zh / th-en）被混用，
导致泰语的 glossaryAvailable 恒为 False。
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

# 和 test_translation_eval_web 一样，先把重型模型运行时打桩，保证这组用例
# 不需要安装 ctranslate2 / transformers 就能在 CI 上跑。
ctranslate2 = types.ModuleType("ctranslate2")
setattr(ctranslate2, "Translator", object)
transformers = types.ModuleType("transformers")
setattr(transformers, "MarianTokenizer", object)
sys.modules.setdefault("ctranslate2", ctranslate2)
sys.modules.setdefault("transformers", transformers)

spec = importlib.util.spec_from_file_location("voicescreen_local_models_directions", SERVICE_PATH)
if spec is None or spec.loader is None:
    raise RuntimeError(f"Unable to load local service from {SERVICE_PATH}")
service = importlib.util.module_from_spec(spec)
spec.loader.exec_module(service)


class DirectionVocabularyTests(unittest.TestCase):
    def test_user_directions_match_csharp_contract(self):
        # 必须和 C# 侧 TranslationDirections.ToWireValue 一一对应。
        self.assertEqual(("zh-en", "en-zh", "th-zh"), service.USER_DIRECTIONS)

    def test_model_pairs_are_the_models_that_actually_exist(self):
        # th-zh 不在这里：没有这个 OPUS-MT 模型，必须经英文桥接。
        self.assertEqual(("zh-en", "en-zh", "th-en"), service.MODEL_PAIRS)

    def test_normalize_game_terms_rejects_user_direction(self):
        # 之前传 "th-zh" 会静默返回原文，让调用方以为术语表生效了。
        with self.assertRaises(ValueError):
            service.normalize_game_terms("อะไรก็ได้", "th-zh")


class GlossaryTests(unittest.TestCase):
    def test_chinese_tactical_terms_are_expanded(self):
        self.assertEqual("敌人在三楼，暂时不要进攻", service.normalize_game_terms("敌人在三楼，先别冲", "zh-en"))

    def test_english_push_is_disambiguated(self):
        self.assertEqual("Let's advance now", service.normalize_game_terms("Let's push now", "en-zh"))
        self.assertEqual("don't attack", service.normalize_game_terms("don't push", "en-zh"))

    def test_has_glossary_rules_detects_rewrites(self):
        self.assertTrue(service.has_glossary_rules("先别冲", "zh-en"))
        self.assertTrue(service.has_glossary_rules("请不要介意", "zh-en"))
        self.assertFalse(service.has_glossary_rules("敌人在二楼", "zh-en"))


class ThaiGlossaryRegressionTests(unittest.TestCase):
    """th-zh 的 glossaryAvailable 曾经恒为 False——术语表作用在桥接英文上，
    但代码拿泰语原文去问。"""

    def setUp(self):
        self.original = service.translate_text

        def fake_translate(text, direction, use_glossary=True, beam_size=4, max_decoding_length=96):
            if direction == "th-en":
                return "don't push now"          # 桥接出来的英文里含术语表规则
            if direction == "en-zh":
                return f"zh({text})"
            raise AssertionError(f"unexpected model pair {direction}")

        setattr(service, "translate_text", fake_translate)

    def tearDown(self):
        setattr(service, "translate_text", self.original)

    def test_thai_glossary_is_detected_on_the_bridge_text(self):
        result = service.evaluate_translation("อย่าเพิ่งบุก", "th-zh")
        self.assertTrue(
            result["glossaryAvailable"],
            "桥接英文里含 push 规则，glossaryAvailable 必须为真",
        )
        self.assertEqual("don't push now", result["bridgeText"])

    def test_thai_without_glossary_terms_reports_false(self):
        setattr(service, "translate_text", lambda text, direction, **_:
                "the enemy is upstairs" if direction == "th-en" else f"zh({text})")
        result = service.evaluate_translation("ศัตรูอยู่ชั้นบน", "th-zh")
        self.assertFalse(result["glossaryAvailable"])

    def test_evaluate_rejects_model_pair_as_direction(self):
        # "th-en" 是模型对，不是用户方向，/evaluate 不该接受。
        with self.assertRaises(ValueError):
            service.evaluate_translation("whatever", "th-en")


class SherpaResultParsingTests(unittest.TestCase):
    """回归：sherpa-onnx 的 OnlineRecognizer.get_result() 返回的是 str，
    之前代码只取 .text，导致所有 Sherpa 识别结果被静默丢成空串。"""

    def test_plain_string_result_is_returned(self):
        self.assertEqual("hello world", service.read_sherpa_result("  hello world  "))

    def test_result_object_with_text_attribute(self):
        class Result:
            text = "  敌人在二楼  "

        self.assertEqual("敌人在二楼", service.read_sherpa_result(Result()))

    def test_empty_and_none_are_normalised(self):
        class Empty:
            text = None

        self.assertEqual("", service.read_sherpa_result(""))
        self.assertEqual("", service.read_sherpa_result(Empty()))

    def test_object_without_text_attribute_yields_empty(self):
        self.assertEqual("", service.read_sherpa_result(object()))


class LanguageDetectionTests(unittest.TestCase):
    """和 C# 侧 SpokenLanguage.Detect 用同一套区间，两条链路对同一句话必须同判。"""

    def test_detects_chinese(self):
        self.assertEqual("zh", service.detect_language("敌人在二楼"))

    def test_detects_thai(self):
        self.assertEqual("th", service.detect_language("สวัสดีครับ"))

    def test_detects_english(self):
        self.assertEqual("en", service.detect_language("Enemies upstairs"))

    def test_returns_auto_without_any_signal(self):
        self.assertEqual("auto", service.detect_language("123"))
        self.assertEqual("auto", service.detect_language(""))


if __name__ == "__main__":
    unittest.main()
