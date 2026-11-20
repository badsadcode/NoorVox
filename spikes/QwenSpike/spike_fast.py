"""Same measurement as spike.py, with faster-qwen3-tts (CUDA graphs). Separate runtime: E:\\AI\\NoorVox\\Runtimes\\qwen-fast.
  python -I spike_fast.py custom <model_dir> <out_dir>
  python -I spike_fast.py clone  <model_dir> <out_dir> <ref.wav> <ref text>
"""
import json
import os
import sys
import time

import numpy as np
import soundfile as sf
import torch
from faster_qwen3_tts import FasterQwen3TTS

TEXT = ("It started, as so many terrible ideas do, with a single image. A window, a Vulkan device, and a triangle. "
        "Nothing more. No terrain, no trees, no Alden. Just proof that the engine could draw something on the screen.")


def main():
    mode, model_dir, out_dir = sys.argv[1], sys.argv[2], sys.argv[3]
    os.makedirs(out_dir, exist_ok=True)
    torch.cuda.reset_peak_memory_stats()
    t0 = time.perf_counter()
    model = FasterQwen3TTS.from_pretrained(model_dir)
    load_s = time.perf_counter() - t0
    t0 = time.perf_counter()
    model.warmup(prefill_len=100)
    warmup_s = time.perf_counter() - t0

    runs = []
    for i in range(3):
        t0 = time.perf_counter()
        if mode == "custom":
            wavs, sr = model.generate_custom_voice(text=TEXT, language="English", speaker="aiden",
                                                   instruct="Calm, warm documentary narration with a hint of humour.")
        else:
            wavs, sr = model.generate_voice_clone(text=TEXT, language="English", ref_audio=sys.argv[4], ref_text=sys.argv[5])
        torch.cuda.synchronize()
        synth_s = time.perf_counter() - t0
        audio = np.asarray(wavs[0]).reshape(-1)
        audio_s = len(audio) / sr
        path = os.path.join(out_dir, f"fast_{mode}_{i}.wav")
        sf.write(path, audio, sr, subtype="PCM_16")
        runs.append({"synth_s": round(synth_s, 2), "audio_s": round(audio_s, 2), "rtf": round(synth_s / audio_s, 3), "file": path})

    print(json.dumps({
        "mode": mode, "engine": "faster-qwen3-tts", "torch": torch.__version__, "load_s": round(load_s, 1), "warmup_s": round(warmup_s, 1),
        "vram_peak_mb": round(torch.cuda.max_memory_allocated() / 2**20), "sample_rate": sr, "runs": runs,
    }, indent=2))


if __name__ == "__main__":
    main()
