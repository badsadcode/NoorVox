namespace NoorVox.Core.Audio;

/// <summary>Facts measured from a WAV file's data chunk. Durations are never estimated.</summary>
public sealed record WavInfo(int SampleRate, int Channels, int BitsPerSample, long Samples, long DataBytes)
{
    public double DurationSeconds => Samples / (double)SampleRate;
    public long DurationMs => (long)Math.Round(Samples * 1000.0 / SampleRate);

    /// <summary>Reads and validates a PCM RIFF/WAVE file. Throws <see cref="InvalidDataException"/> when not valid audio.</summary>
    public static WavInfo Read(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        if (br.BaseStream.Length < 44) throw new InvalidDataException("File too small to be a WAV.");
        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("Missing RIFF header.");
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("Missing WAVE format tag.");

        int rate = 0, channels = 0, bits = 0, format = 0;
        long dataBytes = -1;
        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var id = new string(br.ReadChars(4));
            long size = br.ReadUInt32();
            if (id == "fmt ")
            {
                format = br.ReadInt16();
                channels = br.ReadInt16();
                rate = br.ReadInt32();
                br.ReadInt32();
                br.ReadInt16();
                bits = br.ReadInt16();
                br.BaseStream.Seek(size - 16 + (size & 1), SeekOrigin.Current);
            }
            else if (id == "data")
            {
                dataBytes = Math.Min(size, br.BaseStream.Length - br.BaseStream.Position);
                break;
            }
            else
            {
                br.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        if (format != 1) throw new InvalidDataException($"Unsupported WAV format {format}; expected PCM (1).");
        if (rate <= 0 || channels <= 0 || bits <= 0) throw new InvalidDataException("Invalid fmt chunk.");
        if (dataBytes <= 0) throw new InvalidDataException("WAV has no audio data.");
        int frameBytes = channels * bits / 8;
        return new WavInfo(rate, channels, bits, dataBytes / frameBytes, dataBytes);
    }
}

public static class WavWriter
{
    /// <summary>Writes raw little-endian PCM16 samples as a canonical 44-byte-header WAV.</summary>
    public static void WritePcm16(string path, ReadOnlySpan<byte> pcm, int sampleRate, short channels = 1)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        const short bits = 16;
        int byteRate = sampleRate * channels * bits / 8;
        bw.Write("RIFF"u8);
        bw.Write(36 + pcm.Length);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)(channels * bits / 8));
        bw.Write(bits);
        bw.Write("data"u8);
        bw.Write(pcm.Length);
        bw.Write(pcm);
    }
}
