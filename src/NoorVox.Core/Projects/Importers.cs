using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoorVox.Core.Projects;

public sealed record ImportedSection(string Title, IReadOnlyList<ProjectStore.ImportedScene> Scenes, string? SectionId = null);

/// <summary>
/// Turns scripts into project structure. Import never invents words: it only splits, trims and strips formatting.
/// <list type="bullet">
/// <item>Markdown / text: <c># heading</c> → section (episode/chapter), <c>## heading</c> → scene, blank-line paragraphs and list items → segments,
/// <c>&lt;!-- note --&gt;</c> → editorial note on the next segment (never spoken).</item>
/// <item>Dialogue CSV / JSON: one row per line with a stable line id, character and text; optional set and notes.</item>
/// </list>
/// </summary>
public static partial class ScriptImporter
{
    public static IReadOnlyList<ImportedSection> Import(string path, string? format = null)
    {
        var fmt = (format ?? Path.GetExtension(path).TrimStart('.')).ToLowerInvariant();
        var text = File.ReadAllText(path, Encoding.UTF8);
        var fallbackTitle = Path.GetFileNameWithoutExtension(path);
        return fmt switch
        {
            "md" or "markdown" or "txt" or "text" => ParseMarkdown(text, fallbackTitle),
            "csv" => ParseDialogueCsv(text, fallbackTitle),
            "json" => ParseDialogueJson(text, fallbackTitle),
            _ => throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments,
                $"Unsupported import format '{fmt}'. Use .md, .txt, .csv or .json."),
        };
    }

    // ------------------------------------------------------------------ markdown / text

    [GeneratedRegex(@"<!--(.*?)-->", RegexOptions.Singleline)] private static partial Regex Comment();
    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")] private static partial Regex Image();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")] private static partial Regex Link();
    [GeneratedRegex(@"(\*\*|__|\*|_|`)(?=\S)(.+?)(?<=\S)\1")] private static partial Regex Emphasis();
    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+")] private static partial Regex ListMarker();

    public static IReadOnlyList<ImportedSection> ParseMarkdown(string text, string fallbackTitle)
    {
        var sections = new List<(string Title, List<(string Title, List<ProjectStore.ImportedSegment> Segs)> Scenes)>();
        (string Title, List<(string Title, List<ProjectStore.ImportedSegment> Segs)> Scenes)? section = null;
        (string Title, List<ProjectStore.ImportedSegment> Segs)? scene = null;
        var paragraph = new StringBuilder();
        string? pendingNote = null;

        void EnsureSection() { if (section is null) { section = (fallbackTitle, []); sections.Add(section.Value); } }
        void EnsureScene() { EnsureSection(); if (scene is null) { scene = ("", []); section!.Value.Scenes.Add(scene.Value); } }
        void Flush()
        {
            var p = Clean(paragraph.ToString());
            paragraph.Clear();
            if (p.Length == 0) return;
            EnsureScene();
            scene!.Value.Segs.Add(new ProjectStore.ImportedSegment(p, Notes: pendingNote));
            pendingNote = null;
        }

        // Pull notes out first so multi-line comments work; leave a marker where they were.
        var notes = new List<string>();
        text = Comment().Replace(text.Replace("\r\n", "\n"), m => { notes.Add(m.Groups[1].Value.Trim()); return $"\n\u0001{notes.Count - 1}\n"; });

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith('\u0001')) { Flush(); pendingNote = notes[int.Parse(line[1..])]; continue; }
            if (line.StartsWith("# ")) { Flush(); section = (Clean(line[2..]), []); sections.Add(section.Value); scene = null; continue; }
            if (line.StartsWith("##")) { Flush(); EnsureSection(); scene = (Clean(line.TrimStart('#')), []); section!.Value.Scenes.Add(scene.Value); continue; }
            if (line.Trim().Length == 0 || line.Trim() is "---" or "***") { Flush(); continue; }
            if (ListMarker().IsMatch(line)) { Flush(); paragraph.Append(ListMarker().Replace(line, "")); Flush(); continue; }
            if (line.TrimStart().StartsWith('>')) line = line.TrimStart().TrimStart('>');
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(line.Trim());
        }
        Flush();

        return sections
            .Select(s => new ImportedSection(s.Title, s.Scenes.Where(sc => sc.Segs.Count > 0)
                .Select(sc => new ProjectStore.ImportedScene(sc.Title, sc.Segs)).ToList()))
            .Where(s => s.Scenes.Count > 0)
            .ToList();
    }

    static string Clean(string s)
    {
        s = Image().Replace(s, "");
        s = Link().Replace(s, "$1");
        for (int i = 0; i < 3; i++) s = Emphasis().Replace(s, "$2");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    // ------------------------------------------------------------------ dialogue

    sealed record DialogueRow(string LineId, string Character, string Text, string? Set, string? Notes);

    static IReadOnlyList<ImportedSection> Group(IEnumerable<DialogueRow> rows, string fallbackTitle)
    {
        var list = rows.ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in list)
        {
            if (!ProjectStore.IsValidId(r.LineId))
                throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput,
                    $"Line id '{r.LineId}' is not valid. Use letters, digits, '_', '-' or '.', e.g. npc_vendor_greeting_001.");
            if (!seen.Add(r.LineId))
                throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, $"Line id '{r.LineId}' appears more than once.");
        }
        return list.GroupBy(r => string.IsNullOrWhiteSpace(r.Set) ? fallbackTitle : r.Set!.Trim())
            .Select(g => new ImportedSection(g.Key, [new ProjectStore.ImportedScene("Lines",
                g.Select(r => new ProjectStore.ImportedSegment(r.Text, r.LineId, r.Character, r.Notes)).ToList())]))
            .ToList();
    }

    public static IReadOnlyList<ImportedSection> ParseDialogueCsv(string text, string fallbackTitle)
    {
        var rows = ParseCsv(text);
        if (rows.Count < 2) throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, "The CSV needs a header row and at least one line.");
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant().Replace(" ", "_")).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        int id = Col("line_id", "lineid", "id"), ch = Col("character", "speaker", "character_id"), tx = Col("text", "line", "dialogue");
        int set = Col("set", "dialogue_set", "scene", "group"), notes = Col("notes", "note", "direction");
        if (id < 0 || ch < 0 || tx < 0)
            throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput,
                "The CSV header must contain line_id, character and text columns (optional: set, notes).", new { header });
        string Cell(List<string> r, int i) => i >= 0 && i < r.Count ? r[i].Trim() : "";
        return Group(rows.Skip(1).Where(r => r.Any(c => c.Trim().Length > 0))
            .Select(r => new DialogueRow(Cell(r, id), Cell(r, ch), Cell(r, tx), set >= 0 ? Cell(r, set) : null, notes >= 0 && Cell(r, notes).Length > 0 ? Cell(r, notes) : null)), fallbackTitle);
    }

    public static IReadOnlyList<ImportedSection> ParseDialogueJson(string text, string fallbackTitle)
    {
        List<JsonElement> items;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var arr = root.ValueKind == JsonValueKind.Array ? root : root.TryGetProperty("lines", out var l) ? l : default;
            if (arr.ValueKind != JsonValueKind.Array) throw new JsonException("expected an array of lines or { \"lines\": [...] }");
            items = arr.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch (JsonException ex)
        {
            throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, $"Dialogue JSON is invalid: {ex.Message}");
        }
        string? Str(JsonElement e, params string[] names)
        {
            foreach (var n in names)
                foreach (var p in e.EnumerateObject())
                    if (p.Name.Equals(n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
            return null;
        }
        return Group(items.Select(e => new DialogueRow(
            Str(e, "lineId", "line_id", "id") ?? throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, "Every dialogue line needs a lineId."),
            Str(e, "character", "speaker") ?? "",
            Str(e, "text", "line") ?? "",
            Str(e, "set", "dialogueSet", "scene"),
            Str(e, "notes", "note", "direction"))), fallbackTitle);
    }

    /// <summary>RFC 4180-style CSV (quoted fields, doubled quotes, newlines inside quotes).</summary>
    static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        char sep = text.Split('\n')[0].Count(c => c == ';') > text.Split('\n')[0].Count(c => c == ',') ? ';' : ',';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { row.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); rows.Add(row); row = []; }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString().TrimEnd('\r')); rows.Add(row); }
        return rows;
    }

    /// <summary>Imports a file into a project: keeps the original in references/ and appends one section per imported section.</summary>
    public static (IReadOnlyList<SectionFile> Sections, string ReferencePath) ImportInto(ProjectStore store, string path, string? format = null)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"File not found: {full}");
        var parsed = Import(full, format);
        if (parsed.Count == 0) throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, "Nothing to import: the file contains no speakable text.");
        // Validate everything before writing anything: a rejected import leaves the project exactly as it was.
        var existing = store.AllSegmentIds();
        foreach (var id in parsed.SelectMany(s => s.Scenes).SelectMany(sc => sc.Segments).Select(g => g.SegmentId).Where(id => id is not null))
            if (existing.Contains(id!))
                throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, $"Segment id '{id}' already exists in this project. Nothing was imported.");
        var reference = store.KeepReference(full);
        var added = parsed.Select(s => store.AddSection(s.Title, s.Scenes, s.SectionId)).ToList();
        return (added, reference);
    }
}
