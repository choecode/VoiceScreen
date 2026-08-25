"""Offline-only ModelScope shim for a pre-downloaded CosyVoice model."""


def snapshot_download(*_args, **_kwargs):
    raise RuntimeError("CosyVoice model must be downloaded from ModelScope before the service starts")
