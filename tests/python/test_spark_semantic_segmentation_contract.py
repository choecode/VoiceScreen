import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SERVICE = ROOT / "deploy" / "spark" / "voicescreen_model_service.py"


class SparkSemanticSegmentationContractTests(unittest.TestCase):
    def setUp(self):
        self.source = SERVICE.read_text(encoding="utf-8")

    def test_segment_endpoint_uses_the_resident_instruction_model(self):
        self.assertIn('@app.post("/segment")', self.source)
        self.assertIn("with translation_lock, torch.inference_mode():", self.source)
        self.assertIn('max_new_tokens=4', self.source)

    def test_classifier_is_constrained_to_break_or_continue(self):
        self.assertIn("Output exactly BREAK or CONTINUE", self.source)
        self.assertIn("is_break_decision(answer)", self.source)

    def test_asr_streaming_keeps_server_side_session_and_rolls_back_tail(self):
        self.assertIn('"asrStreaming": models.asr is not None', self.source)
        self.assertIn("class ASRStreamingSession", self.source)
        self.assertIn("STREAMING_UNFIXED_TOKEN_NUM = 5", self.source)
        self.assertIn("session.raw_decoded = prefix + decoded", self.source)

    def test_asr_streaming_is_bounded_and_final_uses_offline_quality(self):
        self.assertIn("STREAMING_MAX_SAMPLES = 16_000 * 60", self.source)
        self.assertIn("STREAMING_SESSION_TTL_SECONDS = 120", self.source)
        self.assertIn("if is_final:", self.source)
        self.assertIn("streaming_sessions.pop(session_id, None)", self.source)

if __name__ == "__main__":
    unittest.main()
