"""Minimal torchaudio compatibility surface required by Qwen3-TTS.

The NVIDIA NGC PyTorch image ships a newer development build of torch than
the public torchaudio wheel was compiled against. Qwen3-TTS only needs the
pure-PyTorch Kaldi filter-bank function, so loading torchaudio's native shared
library would add an unnecessary and incompatible ABI dependency.
"""

