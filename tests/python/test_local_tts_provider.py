import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
SERVICE_DIR = ROOT / "src" / "VoiceScreen.App" / "LocalService"
sys.path.insert(0, str(SERVICE_DIR))

from local_tts_provider import (
    ALLOWED_VOICES,
    LocalTtsError,
    piper_available,
    selected_voice,
    synthesize_piper,
    voice_availability,
)


class LocalTtsProviderTests(unittest.TestCase):
    def test_target_voice_is_allowlisted(self):
        self.assertEqual("en_US-lessac-medium", selected_voice("zh-en"))
        self.assertEqual("zh_CN-huayan-medium", selected_voice("en-zh"))
        expected_us_male_voices = {
            "en_US-joe-medium",
            "en_US-mike-medium",
            "en_US-john-medium",
        }
        self.assertTrue(expected_us_male_voices.issubset(ALLOWED_VOICES["zh-en"]))
        for voice in expected_us_male_voices:
            self.assertEqual(voice, selected_voice("zh-en", voice))
        with self.assertRaisesRegex(ValueError, "not allowed"):
            selected_voice("en-zh", "en_US-lessac-medium")
        for direction in ("en-zh", "th-zh"):
            for voice in expected_us_male_voices:
                with self.assertRaisesRegex(ValueError, "not allowed"):
                    selected_voice(direction, voice)

    def test_availability_requires_runtime_and_both_voice_models(self):
        with tempfile.TemporaryDirectory() as directory:
            piper_root = Path(directory) / "piper"
            piper_root.mkdir()
            for voice in ("en_US-lessac-medium", "zh_CN-huayan-medium"):
                (piper_root / f"{voice}.onnx").touch()
                (piper_root / f"{voice}.onnx.json").touch()
            with patch("local_tts_provider.piper_executable", return_value=Path("/bin/true")):
                availability = voice_availability(directory)
                self.assertTrue(piper_available(directory))
                self.assertTrue(availability["en_US-lessac-medium"])
                self.assertFalse(availability["en_US-joe-medium"])

    def test_synthesis_fails_before_subprocess_when_selected_voice_is_missing(self):
        with tempfile.TemporaryDirectory() as directory:
            (Path(directory) / "piper").mkdir()
            with patch("local_tts_provider.piper_executable", return_value=Path("/bin/true")):
                with self.assertRaisesRegex(LocalTtsError, "not installed: en_US-joe-medium"):
                    synthesize_piper("Test", "zh-en", directory, "en_US-joe-medium")


if __name__ == "__main__":
    unittest.main()
