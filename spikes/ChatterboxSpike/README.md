# Chatterbox spike (ADR-0003)

Measurement scripts that drive NoorVox's real Chatterbox sidecar (`src/NoorVox.Worker/engines/chatterbox/noorvox_chatterbox.py`)
the way the worker does. Runtime used: a throwaway venv with `chatterbox-tts==0.1.7` (torch 2.6.0+cu124) and `setuptools==78.1.0`
(see ADR-0003 for why); NoorVox itself installs the hash-locked `chatterbox` engine pack instead.

```
python -I drive.py <engine python.exe> english|multilingual|turbo <model dir> <out dir> <ref.wav> [<ref2.wav>]
python -I pace.py  <engine python.exe> <model dir> <out dir> [<ref.wav>]
```
