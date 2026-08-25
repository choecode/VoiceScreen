"""Training-only pyworld placeholder for the CosyVoice inference image."""


def __getattr__(name):
    raise RuntimeError(f"pyworld.{name} is unavailable in the inference-only image")
