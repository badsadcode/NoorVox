// Phase 0 spike — see docs/adr/0001-kokoro-backend-and-worker-model.md
// Usage: KokoroSpike --model <kokoro.onnx> --voices <dir> --out <dir> [--voice af_heart] [--runs 3]
using System.Diagnostics;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using KokoroSharp.Utilities;
using Microsoft.ML.OnnxRuntime;

string Arg(string name, string? def = null)
{
    int i = Array.IndexOf(args, name);
    if (i >= 0 && i + 1 < args.Length) return args[i + 1];
    return def ?? throw new ArgumentException($"missing {name}");
}

var modelPath = Path.GetFullPath(Arg("--model"));
var voicesPath = Path.GetFullPath(Arg("--voices"));
var outDir = Path.GetFullPath(Arg("--out"));
var voiceName = Arg("--voice", "af_heart");
int runs = int.Parse(Arg("--runs", "3"));
#if ORT_DML
const string flavour = "DirectML";
#else
const string flavour = "CPU";
#endif
Directory.CreateDirectory(outDir);
var proc = Process.GetCurrentProcess();
double MB(long b) => Math.Round(b / 1048576.0, 1);

Console.WriteLine($"flavour={flavour} pid={proc.Id} model={modelPath}");
Console.WriteLine($"ws_before_load_mb={MB(proc.WorkingSet64)}");

var sw = Stopwatch.StartNew();
var opts = new SessionOptions();
#if ORT_DML
opts.EnableMemoryPattern = false; opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
opts.AppendExecutionProvider_DML(int.Parse(Arg("--dml-device", "0")));
#endif
var synth = KokoroWavSynthesizer.LoadModel(modelPath, opts);
KokoroVoiceManager.LoadVoicesFromPath(voicesPath);
var voice = KokoroVoiceManager.GetVoice(voiceName);
Console.WriteLine($"load_ms={sw.ElapsedMilliseconds} voices={KokoroVoiceManager.Voices.Count}");
proc.Refresh();
Console.WriteLine($"ws_after_load_mb={MB(proc.WorkingSet64)}");

string text = """
It started, as so many terrible ideas do, with a single image. A window, a Vulkan device, and a triangle.
Nothing more. No terrain, no trees, no Alden. Just proof that the engine could draw something on the screen.
It wasn't beautiful. It wasn't even a game yet. But it was real. The first spark of NoorEngine.
""".ReplaceLineEndings(" ");

for (int r = 0; r < runs; r++)
{
    sw.Restart();
    byte[] wav = await synth.SynthesizeAsync(text, voice, new KokoroTTSPipelineConfig { Speed = 1f });
    long ms = sw.ElapsedMilliseconds;
    var file = Path.Combine(outDir, $"spike_{flavour.ToLowerInvariant()}_{r}.wav");
    KokoroWavSynthesizer.SaveAudioToFile(wav, file);
    var info = WavProbe.Read(file);
    proc.Refresh();
    Console.WriteLine($"run={r} synth_ms={ms} audio_s={info.DurationSeconds:F2} rtf={(ms / 1000.0) / info.DurationSeconds:F3} " +
                      $"rate={info.SampleRate} ch={info.Channels} bits={info.Bits} samples={info.Samples} ws_mb={MB(proc.WorkingSet64)} file={file}");
}

synth.Dispose();
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
proc.Refresh();
Console.WriteLine($"ws_after_dispose_mb={MB(proc.WorkingSet64)} private_mb={MB(proc.PrivateMemorySize64)}");

/// Minimal RIFF/WAVE reader: duration is measured from the data chunk, never estimated.
static class WavProbe
{
    public record Info(int SampleRate, int Channels, int Bits, long Samples, double DurationSeconds);
    public static Info Read(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("not RIFF");
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("not WAVE");
        int rate = 0, ch = 0, bits = 0; long dataBytes = -1;
        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var id = new string(br.ReadChars(4)); int size = br.ReadInt32();
            if (id == "fmt ") { br.ReadInt16(); ch = br.ReadInt16(); rate = br.ReadInt32(); br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16(); br.BaseStream.Seek(size - 16, SeekOrigin.Current); }
            else if (id == "data") { dataBytes = size; break; }
            else br.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
        }
        if (dataBytes <= 0 || rate == 0) throw new InvalidDataException("no audio data");
        long samples = dataBytes / (ch * bits / 8);
        return new Info(rate, ch, bits, samples, samples / (double)rate);
    }
}
