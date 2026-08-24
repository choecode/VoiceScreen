"""Small, native-extension-free subset of torchaudio.compliance.kaldi.

This implements the ``fbank`` API used by Qwen3-TTS's speaker encoder. The
math and defaults follow TorchAudio's BSD-licensed Kaldi compatibility module,
but intentionally omit unrelated spectrogram and MFCC entry points.
"""

from __future__ import annotations

import math

import torch
from torch import Tensor


_EPSILON = torch.tensor(torch.finfo(torch.float).eps)


def _epsilon(device: torch.device, dtype: torch.dtype) -> Tensor:
    return _EPSILON.to(device=device, dtype=dtype)


def _next_power_of_two(value: int) -> int:
    return 1 if value == 0 else 2 ** (value - 1).bit_length()


def _frames(waveform: Tensor, window_size: int, window_shift: int, snip_edges: bool) -> Tensor:
    if snip_edges:
        if waveform.numel() < window_size:
            return torch.empty((0, 0), dtype=waveform.dtype, device=waveform.device)
        count = 1 + (waveform.numel() - window_size) // window_shift
    else:
        reversed_waveform = torch.flip(waveform, [0])
        count = (waveform.numel() + window_shift // 2) // window_shift
        pad = window_size // 2 - window_shift // 2
        if pad > 0:
            waveform = torch.cat((reversed_waveform[-pad:], waveform, reversed_waveform), dim=0)
        else:
            waveform = torch.cat((waveform[-pad:], reversed_waveform), dim=0)
    return waveform.as_strided(
        (count, window_size),
        (window_shift * waveform.stride(0), waveform.stride(0)),
    )


def _window_function(
    window_type: str,
    window_size: int,
    blackman_coeff: float,
    device: torch.device,
    dtype: torch.dtype,
) -> Tensor:
    if window_type == "hanning":
        return torch.hann_window(window_size, periodic=False, device=device, dtype=dtype)
    if window_type == "hamming":
        return torch.hamming_window(
            window_size, periodic=False, alpha=0.54, beta=0.46, device=device, dtype=dtype
        )
    if window_type == "povey":
        return torch.hann_window(window_size, periodic=False, device=device, dtype=dtype).pow(0.85)
    if window_type == "rectangular":
        return torch.ones(window_size, device=device, dtype=dtype)
    if window_type == "blackman":
        index = torch.arange(window_size, device=device, dtype=dtype)
        angle = 2 * math.pi * index / (window_size - 1)
        return blackman_coeff - 0.5 * torch.cos(angle) + (0.5 - blackman_coeff) * torch.cos(2 * angle)
    raise ValueError(f"invalid window type: {window_type}")


def _mel_scale_scalar(frequency: float) -> float:
    return 1127.0 * math.log(1.0 + frequency / 700.0)


def _inverse_mel_scale(mel_frequency: Tensor) -> Tensor:
    return 700.0 * ((mel_frequency / 1127.0).exp() - 1.0)


def _mel_banks(
    num_bins: int,
    padded_window_size: int,
    sample_frequency: float,
    low_frequency: float,
    high_frequency: float,
) -> Tensor:
    nyquist = sample_frequency * 0.5
    if high_frequency <= 0:
        high_frequency += nyquist
    if not 0 <= low_frequency < high_frequency <= nyquist:
        raise ValueError("invalid mel frequency range")

    mel_low = _mel_scale_scalar(low_frequency)
    mel_high = _mel_scale_scalar(high_frequency)
    mel_step = (mel_high - mel_low) / (num_bins + 1)
    indices = torch.arange(num_bins).unsqueeze(1)
    left = mel_low + indices * mel_step
    center = mel_low + (indices + 1.0) * mel_step
    right = mel_low + (indices + 2.0) * mel_step

    fft_bin_width = sample_frequency / padded_window_size
    frequencies = fft_bin_width * torch.arange(padded_window_size // 2)
    mel = 1127.0 * (1.0 + frequencies / 700.0).log().unsqueeze(0)
    up = (mel - left) / (center - left)
    down = (right - mel) / (right - center)
    return torch.maximum(torch.zeros(1), torch.minimum(up, down))


def fbank(
    waveform: Tensor,
    blackman_coeff: float = 0.42,
    channel: int = -1,
    dither: float = 0.0,
    energy_floor: float = 1.0,
    frame_length: float = 25.0,
    frame_shift: float = 10.0,
    high_freq: float = 0.0,
    htk_compat: bool = False,
    low_freq: float = 20.0,
    min_duration: float = 0.0,
    num_mel_bins: int = 23,
    preemphasis_coefficient: float = 0.97,
    raw_energy: bool = True,
    remove_dc_offset: bool = True,
    round_to_power_of_two: bool = True,
    sample_frequency: float = 16000.0,
    snip_edges: bool = True,
    subtract_mean: bool = False,
    use_energy: bool = False,
    use_log_fbank: bool = True,
    use_power: bool = True,
    vtln_high: float = -500.0,
    vtln_low: float = 100.0,
    vtln_warp: float = 1.0,
    window_type: str = "povey",
) -> Tensor:
    """Return Kaldi-compatible log Mel filter-bank features.

    Qwen3-TTS invokes the default VTLN settings only. Rejecting a non-default
    warp avoids silently producing an incorrect speaker embedding.
    """
    del vtln_high, vtln_low
    if vtln_warp != 1.0:
        raise NotImplementedError("VTLN warping is not required by VoiceScreen")
    if waveform.ndim != 2:
        raise ValueError("waveform must have shape (channels, samples)")

    selected_channel = max(channel, 0)
    if selected_channel >= waveform.size(0):
        raise ValueError("invalid audio channel")
    signal = waveform[selected_channel]
    if signal.numel() < min_duration * sample_frequency:
        return torch.empty(0, device=waveform.device, dtype=waveform.dtype)

    window_shift = int(sample_frequency * frame_shift * 0.001)
    window_size = int(sample_frequency * frame_length * 0.001)
    padded_size = _next_power_of_two(window_size) if round_to_power_of_two else window_size
    if signal.numel() < window_size:
        raise ValueError("audio is shorter than one analysis window")

    framed = _frames(signal, window_size, window_shift, snip_edges)
    if dither:
        framed = framed + torch.randn_like(framed) * dither
    if remove_dc_offset:
        framed = framed - framed.mean(dim=1, keepdim=True)

    epsilon = _epsilon(framed.device, framed.dtype)
    if raw_energy:
        log_energy = torch.maximum(framed.pow(2).sum(1), epsilon).log()
    if energy_floor:
        floor = torch.tensor(math.log(energy_floor), device=framed.device, dtype=framed.dtype)
        if raw_energy:
            log_energy = torch.maximum(log_energy, floor)

    if preemphasis_coefficient:
        previous = torch.nn.functional.pad(framed.unsqueeze(0), (1, 0), mode="replicate").squeeze(0)
        framed = framed - preemphasis_coefficient * previous[:, :-1]
    framed = framed * _window_function(
        window_type, window_size, blackman_coeff, framed.device, framed.dtype
    ).unsqueeze(0)
    if padded_size != window_size:
        framed = torch.nn.functional.pad(framed, (0, padded_size - window_size))
    if not raw_energy:
        log_energy = torch.maximum(framed.pow(2).sum(1), epsilon).log()

    spectrum = torch.fft.rfft(framed).abs()
    if use_power:
        spectrum = spectrum.pow(2.0)
    banks = _mel_banks(num_mel_bins, padded_size, sample_frequency, low_freq, high_freq)
    banks = torch.nn.functional.pad(banks, (0, 1)).to(device=framed.device, dtype=framed.dtype)
    features = torch.mm(spectrum, banks.T)
    if use_log_fbank:
        features = torch.maximum(features, epsilon).log()
    if use_energy:
        energy = log_energy.unsqueeze(1)
        features = torch.cat((features, energy), dim=1) if htk_compat else torch.cat((energy, features), dim=1)
    if subtract_mean:
        features = features - features.mean(dim=0, keepdim=True)
    return features
