"""Pure PyTorch/SciPy transforms used by VoiceScreen inference services."""

from __future__ import annotations

import math

import librosa
import numpy as np
from scipy.signal import resample_poly
import torch
from torch import Tensor, nn


class Resample(nn.Module):
    def __init__(self, orig_freq: int, new_freq: int, **_: object) -> None:
        super().__init__()
        self.orig_freq = int(orig_freq)
        self.new_freq = int(new_freq)

    def forward(self, waveform: Tensor) -> Tensor:
        if self.orig_freq == self.new_freq:
            return waveform
        original_device = waveform.device
        original_dtype = waveform.dtype
        divisor = math.gcd(self.orig_freq, self.new_freq)
        up = self.new_freq // divisor
        down = self.orig_freq // divisor
        values = waveform.detach().float().cpu().numpy()
        result = resample_poly(values, up, down, axis=-1).astype(np.float32, copy=False)
        return torch.from_numpy(np.ascontiguousarray(result)).to(
            device=original_device, dtype=original_dtype
        )


class Spectrogram(nn.Module):
    def __init__(self, n_fft: int = 400, win_length: int | None = None,
                 hop_length: int | None = None, pad: int = 0,
                 window_fn=torch.hann_window, power: float | None = 2.0,
                 normalized: bool | str = False, center: bool = True,
                 pad_mode: str = "reflect", onesided: bool = True,
                 **_: object) -> None:
        super().__init__()
        self.n_fft = n_fft
        self.win_length = win_length or n_fft
        self.hop_length = hop_length or self.win_length // 2
        self.pad = pad
        self.power = power
        self.normalized = bool(normalized)
        self.center = center
        self.pad_mode = pad_mode
        self.onesided = onesided
        self.register_buffer("window", window_fn(self.win_length), persistent=False)

    def forward(self, waveform: Tensor) -> Tensor:
        if self.pad:
            waveform = torch.nn.functional.pad(waveform, (self.pad, self.pad))
        spectrum = torch.stft(
            waveform,
            n_fft=self.n_fft,
            hop_length=self.hop_length,
            win_length=self.win_length,
            window=self.window.to(device=waveform.device, dtype=waveform.dtype),
            center=self.center,
            pad_mode=self.pad_mode,
            normalized=self.normalized,
            onesided=self.onesided,
            return_complex=True,
        )
        if self.power is None:
            return spectrum
        return spectrum.abs().pow(self.power)


class MelSpectrogram(Spectrogram):
    def __init__(self, sample_rate: int = 16000, n_fft: int = 400,
                 win_length: int | None = None, hop_length: int | None = None,
                 f_min: float = 0.0, f_max: float | None = None,
                 n_mels: int = 128, power: float = 2.0,
                 normalized: bool | str = False, center: bool = True,
                 pad_mode: str = "reflect", norm: str | None = None,
                 mel_scale: str = "htk", **kwargs: object) -> None:
        super().__init__(
            n_fft=n_fft,
            win_length=win_length,
            hop_length=hop_length,
            power=power,
            normalized=normalized,
            center=center,
            pad_mode=pad_mode,
            **kwargs,
        )
        filters = librosa.filters.mel(
            sr=sample_rate,
            n_fft=n_fft,
            n_mels=n_mels,
            fmin=f_min,
            fmax=f_max,
            htk=mel_scale == "htk",
            norm=norm,
            dtype=np.float32,
        )
        self.register_buffer("mel_filters", torch.from_numpy(filters), persistent=False)

    def forward(self, waveform: Tensor) -> Tensor:
        spectrum = super().forward(waveform)
        filters = self.mel_filters.to(device=spectrum.device, dtype=spectrum.dtype)
        return torch.einsum("mf,...ft->...mt", filters, spectrum)
