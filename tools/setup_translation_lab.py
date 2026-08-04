#!/usr/bin/env python3
"""Download and convert VoiceScreen OPUS-MT models for the translation lab."""

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

from huggingface_hub import snapshot_download


MODELS = (
    ("Helsinki-NLP/opus-mt-zh-en", "opus-mt-zh-en"),
    ("Helsinki-NLP/opus-mt-en-zh", "opus-mt-en-zh"),
    ("Helsinki-NLP/opus-mt-th-en", "opus-mt-th-en"),
)
COPY_FILES = ("source.spm", "target.spm", "vocab.json", "tokenizer_config.json")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-root", required=True)
    args = parser.parse_args()

    root = Path(args.model_root).expanduser().resolve()
    root.mkdir(parents=True, exist_ok=True)
    converter = Path(sys.executable).parent / "ct2-transformers-converter"
    if not converter.is_file():
        raise RuntimeError(f"Missing converter in active environment: {converter}")

    os.environ["HF_HUB_DISABLE_XET"] = "1"
    for repository, name in MODELS:
        source = root / f"{name}-source"
        target = root / f"{name}-ct2-int8"
        if (target / "model.bin").is_file():
            print(f"READY {target}", flush=True)
            continue

        print(f"DOWNLOAD {repository}", flush=True)
        snapshot_download(
            repo_id=repository,
            local_dir=source,
            allow_patterns=["*.json", "*.spm", "*.bin", "*.safetensors"],
        )
        missing = [file_name for file_name in COPY_FILES if not (source / file_name).is_file()]
        if missing:
            raise RuntimeError(f"{repository} is missing tokenizer files: {', '.join(missing)}")

        building = root / f".{name}-ct2-int8.building"
        shutil.rmtree(building, ignore_errors=True)
        print(f"CONVERT {repository} -> INT8", flush=True)
        try:
            subprocess.run([
                str(converter),
                "--model", str(source),
                "--output_dir", str(building),
                "--quantization", "int8",
                "--copy_files", *COPY_FILES,
            ], check=True)
            if not (building / "model.bin").is_file():
                raise RuntimeError(f"Converter did not create {building / 'model.bin'}")
            building.replace(target)
        except Exception:
            shutil.rmtree(building, ignore_errors=True)
            raise
        print(f"READY {target}", flush=True)

    print(f"ALL_MODELS_READY {root}", flush=True)


if __name__ == "__main__":
    main()
