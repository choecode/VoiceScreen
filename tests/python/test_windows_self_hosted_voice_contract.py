import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "src" / "VoiceScreen.App"


class WindowsProviderSelectionContractTests(unittest.TestCase):
    def test_translation_and_tts_can_switch_independently(self):
        settings = (APP / "Models" / "AppSettings.cs").read_text(encoding="utf-8")
        xaml = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
        window = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
        # 文件名此前是 SelfHostedApiService.cs，和里面的 OnlineApiService 类名对不上，已改名。
        service = (APP / "Services" / "OnlineApiService.cs").read_text(encoding="utf-8")
        engine = (APP / "Services" / "TranslationEngine.cs").read_text(encoding="utf-8")
        self_test = (ROOT / "tools" / "VoiceScreen.SelfTest" / "Program.cs").read_text(encoding="utf-8")

        self.assertIn("UseApiTranslation", settings)
        self.assertIn("UseApiTts", settings)
        self.assertIn('x:Name="TranslationProviderCombo"', xaml)
        self.assertIn('x:Name="TtsProviderCombo"', xaml)
        self.assertIn("UpdateProviders", window)
        self.assertIn("private readonly string _englishVoice", service)
        self.assertIn("SynthesizeEnglishAsync", service)
        self.assertIn("_settings.UseApiTranslation", engine)
        self.assertIn("_settings.UseApiTts", engine)
        self.assertIn("ApiEnglishVoice", self_test)


if __name__ == "__main__":
    unittest.main()
