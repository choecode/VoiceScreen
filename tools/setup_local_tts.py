#!/usr/bin/env python3
"""Download the Piper voices used by the server evaluation lab."""

import argparse
import shutil
import sys
from pathlib import Path

from huggingface_hub import hf_hub_download


REPOSITORY = "rhasspy/piper-voices"
SERVICE_DIR = Path(__file__).resolve().parents[1] / "src" / "VoiceScreen.App" / "LocalService"
sys.path.insert(0, str(SERVICE_DIR))

from local_tts_provider import VOICE_CATALOG


FILES = {
    f"{voice}{suffix}": f"{metadata['repositoryPath']}{suffix}"
    for voice, metadata in VOICE_CATALOG.items()
    for suffix in (".onnx", ".onnx.json")
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-root", required=True)
    args = parser.parse_args()
    root = Path(args.model_root).expanduser().resolve() / "piper"
    root.mkdir(parents=True, exist_ok=True)

    for destination_name, repository_path in FILES.items():
        destination = root / destination_name
        if destination.is_file():
            print(f"READY {destination}")
            continue
        downloaded = Path(hf_hub_download(repo_id=REPOSITORY, filename=repository_path))
        temporary = destination.with_suffix(destination.suffix + ".downloading")
        shutil.copyfile(downloaded, temporary)
        temporary.replace(destination)
        print(f"READY {destination}")

    print(f"PIPER_MODELS_READY {root}")


if __name__ == "__main__":
    main()
