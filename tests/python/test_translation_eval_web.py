import argparse
import importlib.util
import json
import sys
import threading
import types
import unittest
from http.server import ThreadingHTTPServer
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen


ROOT = Path(__file__).resolve().parents[2]
SERVICE_DIR = ROOT / "src" / "VoiceScreen.App" / "LocalService"
SERVICE_PATH = SERVICE_DIR / "local_outgoing_service.py"
sys.path.insert(0, str(SERVICE_DIR))

# The HTTP contract and static UI are stdlib-only. Stub heavyweight model modules
# before import so this suite does not download or install model runtimes.
ctranslate2 = types.ModuleType("ctranslate2")
setattr(ctranslate2, "Translator", object)
transformers = types.ModuleType("transformers")
setattr(transformers, "MarianTokenizer", object)
sys.modules.setdefault("ctranslate2", ctranslate2)
sys.modules.setdefault("transformers", transformers)

spec = importlib.util.spec_from_file_location("voicescreen_local_models", SERVICE_PATH)
if spec is None or spec.loader is None:
    raise RuntimeError(f"Unable to load local service from {SERVICE_PATH}")
service = importlib.util.module_from_spec(spec)
spec.loader.exec_module(service)


class TranslationEvaluationWebTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.original_evaluate = service.evaluate_translation
        cls.original_online_evaluate = service.evaluate_online
        cls.original_translate = service.translate_text

        def fake_evaluate(text, direction, use_glossary=True, beam_size=4, max_decoding_length=96):
            return {
                "providerId": "local-opus",
                "sourceText": text,
                "normalizedText": f"normalized:{text}" if use_glossary else text,
                "translatedText": f"{direction}:{text}:{'glossary' if use_glossary else 'raw'}",
                "bridgeText": "thai bridge" if direction == "th-zh" else None,
                "direction": direction,
                "useGlossary": use_glossary,
                "beamSize": beam_size,
                "maxDecodingLength": max_decoding_length,
                "latencyMs": 12.34,
                "translationLatencyMs": 12.34,
                "tts": None,
                "model": "fixture-model",
                "glossaryAvailable": "冲" in text,
            }

        setattr(service, "evaluate_translation", fake_evaluate)
        setattr(service, "evaluate_online", lambda text, direction, use_glossary=True, include_tts=False, voice=None: {
            **fake_evaluate(text, direction, use_glossary),
            "providerId": "mymemory-edge",
            "tts": {"latencyMs": 50, "audioUrl": "/audio/fixture.mp3"} if include_tts else None,
        })
        setattr(service, "translate_text", lambda text, direction, **_: f"legacy:{direction}:{text}")
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), service.Handler)
        cls.port = cls.server.server_address[1]
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=3)
        setattr(service, "evaluate_translation", cls.original_evaluate)
        setattr(service, "evaluate_online", cls.original_online_evaluate)
        setattr(service, "translate_text", cls.original_translate)

    def request(self, path, payload=None):
        data = None if payload is None else json.dumps(payload).encode("utf-8")
        request = Request(
            f"http://127.0.0.1:{self.port}{path}",
            data=data,
            headers={"Content-Type": "application/json"} if data else {},
            method="POST" if data else "GET",
        )
        try:
            with urlopen(request, timeout=3) as response:
                return response.status, response.headers, response.read()
        except HTTPError as error:
            return error.code, error.headers, error.read()

    def test_bind_host_accepts_ipv4_and_rejects_names_or_ipv6(self):
        self.assertEqual(service.parse_bind_host("127.0.0.1"), "127.0.0.1")
        self.assertEqual(service.parse_bind_host("0.0.0.0"), "0.0.0.0")
        with self.assertRaises(argparse.ArgumentTypeError):
            service.parse_bind_host("localhost")
        with self.assertRaises(argparse.ArgumentTypeError):
            service.parse_bind_host("::1")

    def test_root_serves_evaluation_lab_with_security_headers(self):
        status, headers, body = self.request("/")
        self.assertEqual(status, 200)
        self.assertIn("text/html", headers["Content-Type"])
        self.assertEqual(headers["Cache-Control"], "no-store")
        self.assertIn("default-src 'self'", headers["Content-Security-Policy"])
        text = body.decode("utf-8")
        self.assertIn("翻译质量评测台", text)
        self.assertIn("/assets/eval.js", text)
        self.assertIn('id="include-tts" type="checkbox" checked disabled', text)

    def test_static_assets_are_allowlisted_and_path_traversal_is_rejected(self):
        status, _, script = self.request("/assets/eval.js")
        self.assertEqual(status, 200)
        self.assertIn(b"voicescreen.translation-evaluation.v1", script)
        status, _, body = self.request("/../README.md")
        self.assertEqual(status, 404)
        self.assertEqual(json.loads(body), {"error": "not found"})

    def test_health_advertises_translation_only_mode_and_ui(self):
        status, _, body = self.request("/health")
        payload = json.loads(body)
        self.assertEqual(status, 200)
        self.assertEqual(payload["asr"], "disabled")
        self.assertEqual(payload["evaluationUi"], "/")

    def test_providers_exposes_stable_local_provider_contract(self):
        status, _, body = self.request("/providers")
        payload = json.loads(body)
        self.assertEqual(status, 200)
        self.assertEqual(payload["providers"][0]["id"], "local-opus")
        self.assertFalse(payload["providers"][0]["tts"])
        self.assertEqual(payload["providers"][0]["voices"]["zh-en"], [
            "en_US-lessac-medium",
            "en_US-joe-medium",
            "en_US-mike-medium",
            "en_US-john-medium",
        ])
        self.assertEqual(payload["providers"][0]["voiceLicenses"]["en_US-joe-medium"], "CC0")
        self.assertEqual(payload["providers"][0]["voiceLicenses"]["en_US-mike-medium"], "CC0")
        self.assertEqual(payload["providers"][0]["voiceLicenses"]["en_US-john-medium"], "Public Domain")
        self.assertEqual(payload["providers"][1]["id"], "mymemory-edge")

    def test_evaluate_returns_trace_and_accepts_thai_bridge_direction(self):
        status, _, body = self.request("/evaluate", {
            "text": "ศัตรูอยู่ชั้นสอง",
            "direction": "th-zh",
            "useGlossary": False,
            "beamSize": 3,
            "maxDecodingLength": 128,
        })
        payload = json.loads(body)
        self.assertEqual(status, 200)
        self.assertEqual(payload["direction"], "th-zh")
        self.assertEqual(payload["bridgeText"], "thai bridge")
        self.assertEqual(payload["beamSize"], 3)
        self.assertEqual(payload["maxDecodingLength"], 128)

    def test_evaluate_is_strict_and_returns_400_for_bad_input(self):
        status, _, body = self.request("/evaluate", {
            "text": "hello",
            "direction": "en-zh",
            "unexpected": True,
        })
        self.assertEqual(status, 400)
        self.assertIn("unknown evaluation fields", json.loads(body)["error"])

    def test_evaluate_dispatches_online_provider_with_tts_flag(self):
        status, _, body = self.request("/evaluate", {
            "provider": "mymemory-edge",
            "text": "hello",
            "direction": "en-zh",
            "useGlossary": False,
            "includeTts": True,
            "voice": "zh-CN-XiaoxiaoNeural",
        })
        payload = json.loads(body)
        self.assertEqual(status, 200)
        self.assertEqual(payload["providerId"], "mymemory-edge")
        self.assertEqual(payload["tts"]["latencyMs"], 50)

    def test_cached_audio_is_served_while_token_is_valid(self):
        token = service.cache_audio(b"fixture-mp3", {"contentType": "audio/mpeg"})
        status, headers, body = self.request(f"/audio/{token}.mp3")
        self.assertEqual(status, 200)
        self.assertEqual(headers["Content-Type"], "audio/mpeg")
        self.assertEqual(body, b"fixture-mp3")

        wav_token = service.cache_audio(b"RIFF-fixture-wav", {"contentType": "audio/wav"})
        status, headers, body = self.request(f"/audio/{wav_token}.wav")
        self.assertEqual(status, 200)
        self.assertEqual(headers["Content-Type"], "audio/wav")
        self.assertEqual(body, b"RIFF-fixture-wav")

    def test_legacy_translate_contract_remains_compatible(self):
        status, _, body = self.request("/translate", {"text": "你好", "direction": "zh-en"})
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(body), {"text": "legacy:zh-en:你好"})


if __name__ == "__main__":
    unittest.main()
