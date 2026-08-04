import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "src" / "VoiceScreen.App"


class WindowsSelfHostedVoiceContractTests(unittest.TestCase):
    def test_piper_voice_selection_is_separate_persisted_and_used(self):
        settings = (APP / "Models" / "AppSettings.cs").read_text(encoding="utf-8")
        xaml = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
        window = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
        service = (APP / "Services" / "SelfHostedApiService.cs").read_text(encoding="utf-8")
        engine = (APP / "Services" / "TranslationEngine.cs").read_text(encoding="utf-8")
        self_test = (ROOT / "tools" / "VoiceScreen.SelfTest" / "Program.cs").read_text(encoding="utf-8")

        self.assertIn("RemoteEnglishVoice", settings)
        self.assertIn('x:Name="RemoteEnglishVoiceCombo"', xaml)
        self.assertIn("RemoteEnglishVoiceCombo.ItemsSource", window)
        self.assertIn("RemoteEnglishVoice =", window)
        self.assertIn("private readonly string _englishVoice", service)
        self.assertIn("GetEnglishVoicesAsync", service)
        self.assertIn("voiceAvailability", service)
        self.assertIn("voice = includeTts ? _englishVoice : null", service)
        self.assertIn("_settings.RemoteEnglishVoice", engine)
        self.assertIn("remoteSettings.RemoteEnglishVoice", self_test)
        self.assertNotIn('voice = includeTts ? "en_US-lessac-medium" : null', service)


if __name__ == "__main__":
    unittest.main()
