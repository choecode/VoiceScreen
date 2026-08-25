# Third-party models and runtimes

This file records the model dependencies used by the current VoiceScreen architecture. It is not legal advice; review every upstream model card and license again before redistributing model weights or a prebuilt appliance.

## What is stored in this repository

VoiceScreen commits one small inference model:

- `src/VoiceScreen.App/Models/silero_vad_16k_op15.onnx`

The Windows publish output also includes that model and `THIRD_PARTY_NOTICES.md`.

The repository does **not** commit Qwen, Whisper, OPUS-MT, Sherpa-ONNX, Qwen3-TTS or CosyVoice weights. Those models are downloaded and stored on the machine that performs inference.

Private reference recordings and generated voice-clone prompts are never part of the source repository.

## Production Spark models

### Qwen3-ASR-1.7B

- Model: [Qwen/Qwen3-ASR-1.7B](https://huggingface.co/Qwen/Qwen3-ASR-1.7B)
- Project: [QwenLM/Qwen3-ASR](https://github.com/QwenLM/Qwen3-ASR)
- Purpose: streaming and final speech recognition on the DGX Spark.
- Runtime path: `/opt/voicescreen/models/voicescreen/Qwen3-ASR-1.7B`.
- Upstream model-card license: Apache-2.0 at the time of the 2026-08-25 review.

### Qwen3-4B-Instruct-2507

- Model: [Qwen/Qwen3-4B-Instruct-2507](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507)
- Purpose: Chinese/English/Thai translation and the constrained `BREAK` / `CONTINUE` subtitle-boundary classifier.
- Runtime path: `/opt/voicescreen/models/voicescreen/Qwen3-4B-Instruct-2507`.
- Upstream model-card license: Apache-2.0 at the time of the 2026-08-25 review.

### Qwen3-TTS-12Hz-0.6B-Base

- Model: [Qwen/Qwen3-TTS-12Hz-0.6B-Base](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-0.6B-Base)
- Project: [QwenLM/Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS)
- Purpose: English voice cloning from a user-authorized reference recording.
- Runtime path: `/opt/voicescreen/models/voicescreen/Qwen3-TTS-12Hz-0.6B-Base`.
- Upstream model-card license: Apache-2.0 at the time of the 2026-08-25 review.

The generated voice output does not grant a right to imitate another person. Deployers must obtain consent for the reference recording and comply with applicable voice, likeness, privacy and disclosure rules.

## Windows client model

### Silero VAD v6.2

- Project: [snakers4/silero-vad](https://github.com/snakers4/silero-vad)
- File: `silero_vad_16k_op15.onnx`
- Purpose: local streaming voice activity detection before ASR.
- License: MIT.
- SHA-256: `7ed98ddbad84ccac4cd0aeb3099049280713df825c610a8ed34543318f1b2c49`.

The full required MIT notice is reproduced in `THIRD_PARTY_NOTICES.md`.

## Windows local fallback models

`tools/setup_local_models.ps1` prepares the following optional fallback stack.

### faster-whisper base / small

- Project: [SYSTRAN/faster-whisper](https://github.com/SYSTRAN/faster-whisper)
- Runtime models: `Systran/faster-whisper-base` for provisional subtitles and `Systran/faster-whisper-small` for final transcription.
- Purpose: local Chinese, English and Thai ASR when Spark is unavailable.
- Storage: the current Windows user's Hugging Face cache.
- License: review the runtime repository and each downloaded model card.

### Helsinki-NLP OPUS-MT Chinese to English

- Model: [Helsinki-NLP/opus-mt-zh-en](https://huggingface.co/Helsinki-NLP/opus-mt-zh-en)
- Purpose: deterministic local Chinese-to-English fallback translation.
- Converted runtime: CTranslate2 INT8.
- Model-card license: CC-BY-4.0 at the time of the last review.

### Helsinki-NLP OPUS-MT English to Chinese

- Model: [Helsinki-NLP/opus-mt-en-zh](https://huggingface.co/Helsinki-NLP/opus-mt-en-zh)
- Purpose: deterministic local English-to-Chinese fallback translation.
- Converted runtime: CTranslate2 INT8.
- Model-card license: Apache-2.0 at the time of the last review.

### Helsinki-NLP OPUS-MT Thai to English

- Model: [Helsinki-NLP/opus-mt-th-en](https://huggingface.co/Helsinki-NLP/opus-mt-th-en)
- Purpose: first stage of the local Thai to English to Chinese bridge.
- Converted runtime: CTranslate2 INT8.
- Model-card license: Apache-2.0 at the time of the last review.

### CTranslate2

- Project: [OpenNMT/CTranslate2](https://github.com/OpenNMT/CTranslate2)
- Purpose: CPU INT8 inference for OPUS-MT and faster-whisper.
- License: MIT.

### Sherpa-ONNX Zipformer, optional

- Package: [k2-fsa/sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx), Apache-2.0.
- Model: [csukuangfj/sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20](https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20)
- Purpose: optional local streaming Chinese/English ASR.
- Downloaded files: `tokens.txt` and the INT8 encoder, decoder and joiner ONNX graphs.
- Limitation: this checkpoint does not support Thai.
- Redistribution: review the model card and the WenetSpeech/GigaSpeech training-corpus licenses.

## Experimental model

### Fun-CosyVoice3-0.5B-2512

- Project: [FunAudioLLM/CosyVoice](https://github.com/FunAudioLLM/CosyVoice)
- Purpose: isolated streaming TTS A/B against the production Qwen3-TTS path.
- Runtime path: `/opt/voicescreen/models/voicescreen/Fun-CosyVoice3-0.5B-2512`.
- Source checkout: supplied as a Docker named build context; it is not vendored here.
- Deployment: Compose profile `experiments`, port `18767`; not started in production.
- License: review both the pinned source revision and downloaded model card before use or redistribution.

## Voice profiles and generated data

The following files are deployment data, not redistributable project assets:

```text
/opt/voicescreen/models/voicescreen/voice-profiles/my-voice-reference.wav
/opt/voicescreen/models/voicescreen/voice-profiles/my-voice.pt
```

- `my-voice-reference.wav` is a user-authorized reference recording.
- `my-voice.pt` is a reusable prompt derived from that recording.
- Both must stay out of Git, public container images, logs and general release archives.
- Replacing the recording or its exact transcript requires regenerating the prompt.

## Online providers

MyMemory and Microsoft Edge TTS are optional network providers rather than bundled models. Selecting them sends recognized text outside the local network. Their service terms, data handling, rate limits and availability can change independently of VoiceScreen and must be reviewed before production use.
