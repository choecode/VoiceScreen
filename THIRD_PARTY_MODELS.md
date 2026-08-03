# Third-party local models

VoiceScreen does not commit model weights into this repository. `tools/setup_local_models.ps1` downloads the following models from their official repositories and converts the translation weights to CTranslate2 INT8 for local CPU inference.

## faster-whisper small

- Project: [faster-whisper](https://github.com/SYSTRAN/faster-whisper)
- Runtime model: `Systran/faster-whisper-small`
- Purpose: local Chinese/English/Thai speech recognition and incoming-language detection.
- License: see the upstream repository and downloaded model card.

## Helsinki-NLP OPUS-MT Chinese → English

- Model: [Helsinki-NLP/opus-mt-zh-en](https://huggingface.co/Helsinki-NLP/opus-mt-zh-en)
- Purpose: deterministic Chinese-to-English machine translation.
- Model card license: CC-BY-4.0.

## Helsinki-NLP OPUS-MT English → Chinese

- Model: [Helsinki-NLP/opus-mt-en-zh](https://huggingface.co/Helsinki-NLP/opus-mt-en-zh)
- Purpose: deterministic English-to-Chinese machine translation.
- Model card license: Apache-2.0.

## Helsinki-NLP OPUS-MT Thai → English

- Model: [Helsinki-NLP/opus-mt-th-en](https://huggingface.co/Helsinki-NLP/opus-mt-th-en)
- Purpose: first stage of the fully local Thai → English → Chinese bridge translation.
- Model card license: Apache-2.0.

## CTranslate2

- Project: [OpenNMT/CTranslate2](https://github.com/OpenNMT/CTranslate2)
- Purpose: CPU INT8 inference for the OPUS-MT models and faster-whisper.
- License: MIT.

Review the current upstream model cards before redistributing converted weights. VoiceScreen's source-code license, if one is later added, does not replace third-party model licenses.
