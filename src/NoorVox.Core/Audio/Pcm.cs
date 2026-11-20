namespace NoorVox.Core.Audio;

/// <summary>Mono PCM audio in memory as floats in [-1, 1]. Export processing works on this; takes on disk are never modified.</summary>
public sealed class PcmBuffer(float[] samples, int sampleRate)
{
    public float[] Samples { get; } = samples;
    public int SampleRate { get; } = sampleRate;
    public long DurationMs => (long)Math.Round(Samples.LongLength * 1000.0 / SampleRate);

    /// <summary>Reads a mono 16-bit PCM WAV (the format NoorVox writes).</summary>
    public static PcmBuffer ReadWav(string path)
    {
        var info = WavInfo.Read(path);
        if (info.Channels != 1 || info.BitsPerSample != 16)
            throw new InvalidDataException($"{Path.GetFileName(path)}: expected mono 16-bit PCM, got {info.Channels} ch / {info.BitsPerSample}-bit.");
        var bytes = File.ReadAllBytes(path);
        int dataOffset = FindData(bytes);
        var samples = new float[info.Samples];
        for (long i = 0; i < samples.LongLength; i++)
            samples[i] = BitConverter.ToInt16(bytes, (int)(dataOffset + i * 2)) / 32768f;
        return new PcmBuffer(samples, info.SampleRate);
    }

    static int FindData(byte[] b)
    {
        int off = 12;
        while (off + 8 <= b.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, off, 4);
            int size = BitConverter.ToInt32(b, off + 4);
            if (id == "data") return off + 8;
            off += 8 + size + (size & 1);
        }
        throw new InvalidDataException("WAV has no data chunk.");
    }

    /// <summary>Writes 16-bit mono PCM (rounded, no dither; values beyond full scale are clipped).</summary>
    public void WriteWav(string path)
    {
        var pcm = new byte[Samples.Length * 2];
        for (int i = 0; i < Samples.Length; i++)
        {
            var v = (int)Math.Round(Math.Clamp(Samples[i], -1f, 1f) * 32767f);
            pcm[i * 2] = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        WavWriter.WritePcm16(path, pcm, SampleRate);
    }

    public float Peak()
    {
        float p = 0;
        foreach (var s in Samples) p = Math.Max(p, Math.Abs(s));
        return p;
    }
}

/// <summary>
/// Band-limited resampling by windowed-sinc interpolation (Blackman window, 64 taps per side scaled to the cutoff).
/// Used only for export (e.g. Kokoro's native 24 kHz → 48 kHz for video). Tested for length, frequency and amplitude
/// preservation in ResamplerTests.
/// </summary>
public static class Resampler
{
    const int HalfTaps = 32;

    public static PcmBuffer Resample(PcmBuffer input, int targetRate)
    {
        if (targetRate == input.SampleRate) return input;
        if (targetRate is < 8_000 or > 192_000) throw new ArgumentOutOfRangeException(nameof(targetRate), "Sample rate must be 8–192 kHz.");
        double ratio = (double)targetRate / input.SampleRate;
        // Downsampling must low-pass below the new Nyquist; upsampling keeps the original band.
        double cutoff = Math.Min(1.0, ratio);
        int halfWidth = (int)Math.Ceiling(HalfTaps / cutoff);
        long outLength = (long)Math.Round(input.Samples.LongLength * ratio);
        var src = input.Samples;
        var output = new float[outLength];

        Parallel.For(0, outLength, n =>
        {
            double t = n / ratio; // position in input samples
            long center = (long)Math.Floor(t);
            double sum = 0, weight = 0;
            for (long k = center - halfWidth + 1; k <= center + halfWidth; k++)
            {
                double x = t - k;
                double w = Blackman(x / (halfWidth + 1)) * Sinc(x * cutoff) * cutoff;
                weight += w;
                if (k >= 0 && k < src.LongLength) sum += src[k] * w;
            }
            // Normalise by the kernel's DC gain so constant signals keep their level exactly.
            output[n] = (float)(weight != 0 ? sum / weight : 0);
        });
        return new PcmBuffer(output, targetRate);
    }

    static double Sinc(double x) => Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    /// <summary>Blackman window on [-1, 1].</summary>
    static double Blackman(double u)
    {
        if (Math.Abs(u) >= 1) return 0;
        double a = (u + 1) / 2; // 0..1
        return 0.42 - 0.5 * Math.Cos(2 * Math.PI * a) + 0.08 * Math.Cos(4 * Math.PI * a);
    }
}
