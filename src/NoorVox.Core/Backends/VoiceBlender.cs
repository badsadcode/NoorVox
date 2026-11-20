using System.Text;
using System.Text.RegularExpressions;
using NoorVox.Core.Models;
using NoorVox.Core.Util;

namespace NoorVox.Core.Backends;

/// <summary>Minimal NumPy .npy (v1.0, little-endian float32, C order) reader/writer for Kokoro voice tensors.</summary>
public static class NpyFile
{
    public static (int[] Shape, float[] Data) Read(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 10 || b[0] != 0x93 || Encoding.ASCII.GetString(b, 1, 5) != "NUMPY")
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a .npy file.");
        int major = b[6];
        int headerLen = major == 1 ? BitConverter.ToUInt16(b, 8) : BitConverter.ToInt32(b, 8);
        int dataStart = (major == 1 ? 10 : 12) + headerLen;
        var header = Encoding.ASCII.GetString(b, major == 1 ? 10 : 12, headerLen);
        if (!header.Contains("'<f4'") || header.Contains("'fortran_order': True"))
            throw new InvalidDataException($"{Path.GetFileName(path)}: only little-endian float32 C-order arrays are supported.");
        var m = Regex.Match(header, @"'shape':\s*\(([^)]*)\)");
        var shape = m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
        long count = shape.Aggregate(1L, (a, x) => a * x);
        if (dataStart + count * 4 != b.Length) throw new InvalidDataException($"{Path.GetFileName(path)}: size doesn't match its shape.");
        var data = new float[count];
        Buffer.BlockCopy(b, dataStart, data, 0, (int)(count * 4));
        return (shape, data);
    }

    public static void Write(string path, int[] shape, float[] data)
    {
        var dict = $"{{'descr': '<f4', 'fortran_order': False, 'shape': ({string.Join(", ", shape)}{(shape.Length == 1 ? "," : "")}), }}";
        int unpadded = 10 + dict.Length + 1;
        int pad = (64 - unpadded % 64) % 64;
        var header = dict + new string(' ', pad) + "\n";
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        fs.Write([0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0]);
        fs.Write(BitConverter.GetBytes((ushort)header.Length));
        fs.Write(Encoding.ASCII.GetBytes(header));
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        fs.Write(bytes);
    }
}

public sealed record BlendPart(string VoiceId, double Weight);

/// <summary>Recipe stored next to a blended voice (<c>voices/&lt;id&gt;.blend.json</c>).</summary>
public sealed record BlendRecipe(int SchemaVersion, string VoiceId, string Name, IReadOnlyList<BlendPart> Parts, DateTimeOffset CreatedAt, string Generator);

public sealed record BlendResult(string VoiceId, string Name, string Path, string Sha256, IReadOnlyList<BlendPart> Parts, string Language);

/// <summary>
/// Kokoro voices are style vectors; a weighted average of them is a valid new voice (KokoroSharp's own mixing does
/// the same). Blends are saved into the model's local <c>custom-voices</c> pack with a sha256, so they're installed,
/// verified and listed like any other voice. They're immutable: takes are matched by voice id, so changing a
/// blend under an existing id would make old takes look current. Make a new name instead.
/// </summary>
public static partial class VoiceBlender
{
    public const string CustomPackId = "custom-voices";

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    public static BlendResult Blend(InstalledModel model, string name, IReadOnlyList<BlendPart> parts)
    {
        if (string.IsNullOrWhiteSpace(name)) throw Invalid("Give the blended voice a name.");
        if (parts.Count is < 2 or > 4) throw Invalid("Blend 2 to 4 voices.");
        if (parts.Any(p => p.Weight <= 0 || double.IsNaN(p.Weight))) throw Invalid("Every weight must be greater than 0.");
        if (parts.Select(p => p.VoiceId).Distinct().Count() != parts.Count) throw Invalid("Each voice can appear only once in a blend.");
        foreach (var p in parts)
            if (!model.Voices.Any(v => v.VoiceId == p.VoiceId))
                throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"Voice '{p.VoiceId}' is not installed for {model.Reference}.");

        double total = parts.Sum(p => p.Weight);
        var normalized = parts.Select(p => new BlendPart(p.VoiceId, Math.Round(p.Weight / total, 4))).ToList();
        // The dominant voice decides language and gender (Kokoro picks pronunciation rules from the id prefix).
        var dominant = normalized.OrderByDescending(p => p.Weight).First().VoiceId;
        var slug = NonAlnum().Replace(name.Trim().ToLowerInvariant(), "");
        if (slug.Length == 0) throw Invalid("The name needs at least one letter or digit.");
        if (slug.Length > 24) slug = slug[..24];
        var voiceId = $"{dominant[..2]}_{slug}";
        if (model.Voices.Any(v => v.VoiceId == voiceId))
            throw new NoorVoxException(ErrorCodes.OutputExists, ExitCode.IoError,
                $"A voice '{voiceId}' already exists. Blends can't be changed after creation (takes rely on them); pick a new name.", new { voiceId });

        float[]? mix = null;
        int[]? shape = null;
        foreach (var p in normalized)
        {
            var file = model.Voices.First(v => v.VoiceId == p.VoiceId).File
                ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"'{p.VoiceId}' is not a Kokoro voice file and can't be blended.");
            var (sh, data) = NpyFile.Read(Path.Combine(model.Directory, file.Path));
            if (shape is null) { shape = sh; mix = new float[data.Length]; }
            else if (!sh.SequenceEqual(shape)) throw Invalid($"Voice '{p.VoiceId}' has a different shape and can't be blended.");
            var w = (float)p.Weight;
            for (int i = 0; i < data.Length; i++) mix![i] += data[i] * w;
        }

        var rel = $"voices/{voiceId}.npy";
        var full = Path.Combine(model.Directory, rel);
        var tmp = full + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        NpyFile.Write(tmp, shape!, mix!);
        File.Move(tmp, full);
        var sha = FileUtil.Sha256File(full);

        var recipe = new BlendRecipe(1, voiceId, name.Trim(), normalized, DateTimeOffset.Now, $"NoorVox {NoorVoxInfo.Version}");
        FileUtil.WriteAllTextAtomic(Path.Combine(model.Directory, "voices", $"{voiceId}.blend.json"), Json.Serialize(recipe));

        // Register in the model's local custom pack (manifest written last).
        var packPath = Path.Combine(model.Directory, "voicepacks", $"{CustomPackId}.json");
        var pack = File.Exists(packPath) ? Json.ReadFile<PackageManifest>(packPath) : new PackageManifest
        {
            Kind = "voicePack", Id = CustomPackId, Version = "local", DisplayName = "My blended voices",
            ModelId = model.Id, ModelVersion = model.Version, License = "derived from the blended voices' licenses",
            Source = "created locally with NoorVox voice blending", Origin = "blend", InstalledAt = DateTimeOffset.Now,
        };
        pack.Files.Add(new PackageFile(rel, new FileInfo(full).Length, sha));
        FileUtil.WriteAllTextAtomic(packPath, Json.Serialize(pack));
        return new BlendResult(voiceId, recipe.Name, full, sha, normalized, KokoroVoiceNames.Describe(voiceId, CustomPackId).LanguageName);
    }

    /// <summary>The recipe of a blended voice, or null for an official voice.</summary>
    public static BlendRecipe? Recipe(InstalledModel model, string voiceId)
    {
        var p = Path.Combine(model.Directory, "voices", $"{voiceId}.blend.json");
        return KokoroVoiceNames.IsValidId(voiceId) && File.Exists(p) ? Json.ReadFile<BlendRecipe>(p) : null;
    }

    static NoorVoxException Invalid(string message) => new(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, message);
}
