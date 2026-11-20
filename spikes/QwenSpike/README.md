# Qwen3-TTS spike (ADR-0002)

Throwaway measurement scripts. The runtimes below were deleted after 0.3.0 shipped (NoorVox installs its own engine pack); recreate them to re-run:

- `E:\AI\NoorVox\Runtimes\qwen`: official `qwen-tts` 0.1.1 + torch 2.11.0+cu128 (Python 3.12 venv)
- `E:\AI\NoorVox\Runtimes\qwen-fast`: `faster-qwen3-tts` 0.5.4 + torch 2.11.0+cu128 + **transformers==5.15.1** (5.23 breaks it)

```
python -I download.py E:\AI\NoorVox\Models 1.7B-CustomVoice 1.7B-Base
python -I spike.py custom E:\AI\NoorVox\Models\qwen3-tts\1.7B-CustomVoice out\
python -I spike_fast.py clone E:\AI\NoorVox\Models\qwen3-tts\1.7B-Base out\ ref.wav "reference transcript"
```

Kokoro on CUDA: `spikes\KokoroSpike` with `-p:Ort=Cuda`, run with `PATH` including `<torch>\lib` (CUDA 12 / cuDNN 9 DLLs).
