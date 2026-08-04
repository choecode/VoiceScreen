import json
import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SERVICE_DIR = ROOT / "src" / "VoiceScreen.App" / "LocalService"
sys.path.insert(0, str(SERVICE_DIR))

from online_providers import target_voice, translate_mymemory  # noqa: E402


class FakeResponse:
    def __init__(self, payload):
        self.payload = json.dumps(payload).encode("utf-8")

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False

    def read(self, maximum):
        return self.payload[:maximum]


class OnlineProviderTests(unittest.TestCase):
    def test_mymemory_contract_parses_translation_and_match(self):
        seen = {}

        def opener(request, timeout):
            seen["url"] = request.full_url
            seen["timeout"] = timeout
            return FakeResponse({
                "responseStatus": 200,
                "responseData": {"translatedText": "Hold &amp; wait.", "match": 0.85},
            })

        result = translate_mymemory("等等", "zh-en", opener=opener)
        self.assertEqual(result["translatedText"], "Hold & wait.")
        self.assertEqual(result["providerMatch"], 0.85)
        self.assertEqual(result["languagePair"], "zh-CN|en")
        self.assertIn("langpair=zh-CN%7Cen", seen["url"])
        self.assertEqual(seen["timeout"], 25)

    def test_mymemory_limits_payload_by_utf8_bytes(self):
        with self.assertRaisesRegex(ValueError, "480 UTF-8 bytes"):
            translate_mymemory("中" * 161, "zh-en", opener=lambda *_: None)

    def test_target_voice_is_allowlisted_by_target_language(self):
        self.assertEqual(target_voice("zh-en"), "en-US-JennyNeural")
        self.assertEqual(target_voice("en-zh", "zh-CN-YunxiNeural"), "zh-CN-YunxiNeural")
        with self.assertRaisesRegex(ValueError, "voice is not allowed"):
            target_voice("zh-en", "zh-CN-XiaoxiaoNeural")


if __name__ == "__main__":
    unittest.main()
