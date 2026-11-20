"""Phase-0-style spike for a Qwen3-TTS backend (see docs/adr/0002-qwen3-tts-backend.md).

Measures load time, peak VRAM, synthesis time and real audio duration on this PC.
  python spike.py custom <model_dir> <out_dir>                  # CustomVoice: built-in speaker + style instruction
  python spike.py clone  <model_dir> <out_dir> <ref.wav> <ref text>   # Base: voice clone from a reference clip
Run with the isolated runtime: E:\\AI\\NoorVox\\Runtimes\\qwen\\Scripts\\python.exe -I spike.py ...
"""
import json
import os
import sys
import time

import soundfile as sf
import torch
from qwen_tts import Qwen3TTSModel

TEXT = ("It started, as so many terrible ideas do, with a single image. A window, a Vulkan device, and a triangle. "
        "Nothing more. No terrain, no trees, no Alden. Just proof that the engine could draw something on the screen.")


def main():
    mode, model_dir, out_dir = sys.argv[1], sys.argv[2], sys.argv[3]
    os.makedirs(out_dir, exist_ok=True)
    torch.cuda.reset_peak_memory_stats()
    t0 = time.perf_counter()
    model = Qwen3TTSModel.from_pretrained(model_dir, device_map="cuda:0", dtype=torch.bfloat16, attn_implementation="sdpa")
    load_s = time.perf_counter() - t0
    after_load = torch.cuda.memory_allocated() / 2**20

    runs = []
    for i in range(2):  # first run includes warm-up
        t0 = time.perf_counter()
        if mode == "custom":
            wavs, sr = model.generate_custom_voice(text=TEXT, language="English", speaker="Aiden",
                                                   instruct="Calm, warm documentary narration with a hint of humour.")
        else:
            wavs, sr = model.generate_voice_clone(text=TEXT, language="English", ref_audio=sys.argv[4], ref_text=sys.argv[5])
        torch.cuda.synchronize()
        synth_s = time.perf_counter() - t0
        audio_s = len(wavs[0]) / sr
        path = os.path.join(out_dir, f"qwen_{mode}_{i}.wav")
        sf.write(path, wavs[0], sr, subtype="PCM_16")
        runs.append({"synth_s": round(synth_s, 2), "audio_s": round(audio_s, 2), "rtf": round(synth_s / audio_s, 3), "file": path})

    result = {
        "mode": mode, "model": model_dir, "torch": torch.__version__, "gpu": torch.cuda.get_device_name(0),
        "load_s": round(load_s, 1), "vram_after_load_mb": round(after_load), "vram_peak_mb": round(torch.cuda.max_memory_allocated() / 2**20),
        "sample_rate": sr, "runs": runs,
    }
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
