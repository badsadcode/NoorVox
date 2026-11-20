using System.Text.RegularExpressions;
using NoorVox.Core.Audio;
using NoorVox.Core.Util;

namespace NoorVox.Core.Storage;

/// <summary>
/// One recording in the Voice lab library: <c>&lt;recordings&gt;/&lt;id&gt;.wav</c> plus <c>&lt;id&gt;.json</c> (this metadata).
/// The WAV is yours: NoorVox never overwrites it, and deleting sends it to the Recycle Bin.
/// </summary>
public sealed class RecordingInfo
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>What is said, word for word. Needed to clone a voice from it.</summary>
    public string Transcript { get; set; } = "";
    public string? Notes { get; set; }
    /// <summary><c>microphone</c> (recorded in the studio) or <c>imported</c>.</summary>
    public string Source { get; set; } = "microphone";
    public string? SourceFileName { get; set; }
    public long DurationMs { get; set; }
    public int SampleRate { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public string WavPath { get; set; } = "";
}

public static partial class RecordingStore
{
    public const long MaxBytes = 200L * 1024 * 1024;
    public const double MinSeconds = 0.5, MaxSeconds = 15 * 60;

    [GeneratedRegex("^[a-z0-9][a-z0-9_\\-]{0,79}$")]
    private static partial Regex IdPattern();
    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    public static string Dir(StorageContext s) => s.Resolve(StorageKind.Recordings).Path;

    /// <summary>All WAVs in the folder, newest first. A WAV without metadata (copied in by hand) is listed too.</summary>
    public static IReadOnlyList<RecordingInfo> List(StorageContext s)
    {
        var dir = Dir(s);
        if (!Directory.Exists(dir)) return [];
        var list = new List<RecordingInfo>();
        foreach (var wav in Directory.EnumerateFiles(dir, "*.wav"))
        {
            var id = Path.GetFileNameWithoutExtension(wav);
            if (!IsValidId(id)) continue;
            var info = Read(wav);
            if (info is not null) list.Add(info);
        }
        return list.OrderByDescending(r => r.CreatedAt).ToList();
    }

    static RecordingInfo? Read(string wav)
    {
        var id = Path.GetFileNameWithoutExtension(wav);
        var json = Path.ChangeExtension(wav, ".json");
        RecordingInfo info;
        try
        {
            info = File.Exists(json) ? Json.ReadFile<RecordingInfo>(json) : new RecordingInfo { Name = id, Source = "imported", CreatedAt = File.GetCreationTime(wav) };
            var w = WavInfo.Read(wav);
            info.DurationMs = w.DurationMs;
            info.SampleRate = w.SampleRate;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException) { return null; }
        info.Id = id;
        info.WavPath = wav;
        return info;
    }

    public static RecordingInfo Find(StorageContext s, string id)
    {
        if (!IsValidId(id)) throw Invalid($"'{id}' is not a recording id.");
        var wav = Path.Combine(Dir(s), id + ".wav");
        return (File.Exists(wav) ? Read(wav) : null)
            ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.NotFound, $"No recording '{id}' in {Dir(s)}.");
    }

    /// <summary>Saves WAV bytes (from the studio's recorder) as a new recording. Never overwrites.</summary>
    public static RecordingInfo Save(StorageContext s, byte[] wav, string name, string? transcript, string source, string? sourceFileName = null)
    {
        if (wav.Length > MaxBytes) throw Invalid("That recording is too large (200 MB max).");
        var dir = Dir(s);
        Directory.CreateDirectory(dir);
        var cleanName = string.IsNullOrWhiteSpace(name) ? $"Recording {DateTime.Now:yyyy-MM-dd HH.mm}" : name.Trim();
        if (cleanName.Length > 80) cleanName = cleanName[..80];
        var id = UniqueId(dir, Slug(cleanName));
        var tmp = Path.Combine(dir, $".{id}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(tmp, wav);
        WavInfo w;
        try
        {
            w = WavInfo.Read(tmp);
            if (w.DurationSeconds < MinSeconds || w.DurationSeconds > MaxSeconds)
                throw Invalid($"Recordings must be {MinSeconds}-{MaxSeconds / 60:0} minutes long; this one is {w.DurationSeconds:0.0} s.");
        }
        catch (InvalidDataException ex)
        {
            File.Delete(tmp);
            throw Invalid($"Not a PCM WAV NoorVox can read: {ex.Message}");
        }
        catch { File.Delete(tmp); throw; }
        var final = Path.Combine(dir, id + ".wav");
        File.Move(tmp, final); // throws rather than overwrite
        var info = new RecordingInfo
        {
            Id = id, Name = cleanName, Transcript = transcript?.Trim() ?? "", Source = source, SourceFileName = sourceFileName,
            DurationMs = w.DurationMs, SampleRate = w.SampleRate, Sha256 = FileUtil.Sha256File(final), CreatedAt = DateTimeOffset.Now, WavPath = final,
        };
        FileUtil.WriteAllTextAtomic(Path.ChangeExtension(final, ".json"), Json.Serialize(info));
        return info;
    }

    /// <summary>Copies a WAV from anywhere into the library (the original is untouched).</summary>
    public static RecordingInfo Import(StorageContext s, string path, string? name, string? transcript)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw Invalid($"File not found: {full}");
        if (new FileInfo(full).Length > MaxBytes) throw Invalid("That file is too large (200 MB max).");
        return Save(s, File.ReadAllBytes(full), name ?? Path.GetFileNameWithoutExtension(full), transcript, "imported", Path.GetFileName(full));
    }

    /// <summary>Changes name, transcript or notes. The audio never changes.</summary>
    public static RecordingInfo Update(StorageContext s, string id, string? name, string? transcript, string? notes)
    {
        var r = Find(s, id);
        if (name is not null) r.Name = name.Trim().Length == 0 ? r.Name : name.Trim()[..Math.Min(80, name.Trim().Length)];
        if (transcript is not null) r.Transcript = transcript.Trim();
        if (notes is not null) r.Notes = notes.Trim().Length == 0 ? null : notes.Trim();
        if (r.Sha256.Length == 0) r.Sha256 = FileUtil.Sha256File(r.WavPath);
        FileUtil.WriteAllTextAtomic(Path.ChangeExtension(r.WavPath, ".json"), Json.Serialize(r));
        return r;
    }

    /// <summary>Moves the recording (WAV + metadata) to the Windows Recycle Bin, so it can be restored.</summary>
    public static void Delete(StorageContext s, string id)
    {
        var r = Find(s, id);
        foreach (var f in new[] { r.WavPath, Path.ChangeExtension(r.WavPath, ".json") }.Where(File.Exists))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    static string Slug(string name)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0) slug = "recording";
        return slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }

    static string UniqueId(string dir, string slug)
    {
        var id = slug;
        for (int i = 2; File.Exists(Path.Combine(dir, id + ".wav")); i++) id = $"{slug}-{i}";
        return id;
    }

    static NoorVoxException Invalid(string message) => new(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, message);
}
