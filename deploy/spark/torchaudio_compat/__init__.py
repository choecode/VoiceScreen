"""Native-extension-free torchaudio compatibility surface for Spark ARM64.

The NVIDIA NGC PyTorch image ships a newer development build of torch than
the public torchaudio wheel was compiled against. VoiceScreen only needs WAV
I/O, resampling, spectrogram transforms, and Kaldi filter banks for inference,
so loading torchaudio's native shared library would add an unnecessary and
incompatible ABI dependency.
"""

from __future__ import annotations

from types import SimpleNamespace
from typing import BinaryIO

import numpy as np
import soundfile as sf
import torch

from . import transforms


def load(uri: str | BinaryIO, normalize: bool = True, channels_first: bool = True,
         format: str | None = None, buffer_size: int = 4096, backend: str | None = None):
    """Load audio with SoundFile and return ``(channels, frames)`` float tensor."""
    del normalize, format, buffer_size, backend
    samples, sample_rate = sf.read(uri, always_2d=True, dtype="float32")
    if channels_first:
        samples = samples.T
    return torch.from_numpy(np.ascontiguousarray(samples)), int(sample_rate)


def save(uri: str | BinaryIO, src: torch.Tensor, sample_rate: int,
         channels_first: bool = True, format: str | None = None,
         encoding: str | None = None, bits_per_sample: int | None = None,
         buffer_size: int = 4096, backend: str | None = None) -> None:
    """Save PCM16 audio with SoundFile."""
    del format, encoding, bits_per_sample, buffer_size, backend
    samples = src.detach().float().cpu().numpy()
    if channels_first:
        samples = samples.T
    sf.write(uri, samples, sample_rate, subtype="PCM_16")


def info(uri: str | BinaryIO, format: str | None = None,
         buffer_size: int = 4096, backend: str | None = None):
    del format, buffer_size, backend
    value = sf.info(uri)
    return SimpleNamespace(
        sample_rate=int(value.samplerate),
        num_frames=int(value.frames),
        num_channels=int(value.channels),
        bits_per_sample=16,
        encoding=str(value.subtype),
    )


__version__ = "0.0-voicescreen-compat"
