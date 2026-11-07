using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SmileyBall.Web.Services;

/// <summary>
/// Keeps every lecture in its own folder under Lectures/:
///   Lectures/CLAUDE.md          - tells Claude (Claude Code) how this folder is laid out
///   Lectures/INDEX.md           - every lecture by course, newest first
///   Lectures/&lt;id&gt;/lecture.json - metadata
///   Lectures/&lt;id&gt;/audio/       - original recordings
///   Lectures/&lt;id&gt;/transcript.md, notes.md, briefing.md, briefing.mp3
/// </summary>
public partial class LectureStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    readonly object _lock = new();

    public string Root { get; }

    public LectureStore(IOptions<SmileyOptions> options, IWebHostEnvironment env)
    {
        Root = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.LecturesPath));
        Directory.CreateDirectory(Root);
        var claudeMd = Path.Combine(Root, "CLAUDE.md");
        if (!File.Exists(claudeMd)) File.WriteAllText(claudeMd, ClaudeMd);
        lock (_lock) WriteIndex();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,80}$")]
    private static partial Regex IdRegex();

    public static bool ValidId(string? id) => id is not null && IdRegex().IsMatch(id);

    public string Dir(string id) =>
        ValidId(id) ? Path.Combine(Root, id) : throw new ArgumentException($"Bad lecture id: {id}");

    public string FilePath(string id, string name) => Path.Combine(Dir(id), name);

    public bool Exists(string id) => ValidId(id) && File.Exists(FilePath(id, "lecture.json"));

    public Lecture? Get(string id)
    {
        if (!Exists(id)) return null;
        try { return JsonSerializer.Deserialize<Lecture>(File.ReadAllText(FilePath(id, "lecture.json")), Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>All lectures, newest first.</summary>
    public List<Lecture> All()
    {
        var list = new List<Lecture>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var l = Get(Path.GetFileName(dir));
            if (l is not null) list.Add(l);
        }
        return list.OrderByDescending(l => l.Date).ToList();
    }

    public void Create(Lecture lecture)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.Combine(Dir(lecture.Id), "audio"));
            WriteAtomic(FilePath(lecture.Id, "lecture.json"), JsonSerializer.Serialize(lecture, Json));
            WriteIndex();
        }
    }

    /// <summary>Reads the latest copy, applies the change, and saves - so two edits never overwrite each other.</summary>
    public Lecture? Update(string id, Action<Lecture> change)
    {
        lock (_lock)
        {
            var l = Get(id);
            if (l is null) return null;
            change(l);
            WriteAtomic(FilePath(id, "lecture.json"), JsonSerializer.Serialize(l, Json));
            WriteIndex();
            return l;
        }
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            if (Directory.Exists(Dir(id))) Directory.Delete(Dir(id), recursive: true);
            WriteIndex();
        }
    }

    public string? ReadText(string id, string name)
    {
        var p = FilePath(id, name);
        return File.Exists(p) ? File.ReadAllText(p) : null;
    }

    public void WriteText(string id, string name, string text) => WriteAtomic(FilePath(id, name), text);

    public void DeleteFile(string id, string name)
    {
        var p = FilePath(id, name);
        if (File.Exists(p)) File.Delete(p);
    }

    /// <summary>Course names already in use, so Claude can reuse the same spelling.</summary>
    public List<string> KnownCourses() =>
        All().Select(l => l.Course).Where(c => c != "Unsorted").Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    void WriteIndex()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Lecture index");
        sb.AppendLine();
        sb.AppendLine("_Updated automatically by the Smiley Ball app. Newest first in each course._");
        foreach (var course in All().GroupBy(l => l.Course).OrderBy(g => g.Key == "Unsorted").ThenBy(g => g.Key))
        {
            sb.AppendLine();
            sb.AppendLine($"## {course.Key}");
            sb.AppendLine();
            foreach (var l in course)
            {
                var mins = Math.Max(1, (int)Math.Round(l.DurationSec / 60.0));
                var notes = File.Exists(FilePath(l.Id, "notes.md")) ? $" · [notes]({l.Id}/notes.md)" : "";
                var transcript = File.Exists(FilePath(l.Id, "transcript.md")) ? $" · [transcript]({l.Id}/transcript.md)" : "";
                var status = l.Status == "ready" ? "" : $" · _{l.Status}_";
                sb.AppendLine($"- **{l.Date.LocalDateTime:yyyy-MM-dd ddd} — {l.Title}** · {mins} min{notes}{transcript}{status}");
                if (!string.IsNullOrWhiteSpace(l.Overview)) sb.AppendLine($"  {l.Overview.ReplaceLineEndings(" ")}");
            }
        }
        WriteAtomic(Path.Combine(Root, "INDEX.md"), sb.ToString());
    }

    const string ClaudeMd = """
        # My class notes (Smiley Ball)

        This folder holds my recorded lectures. The Smiley Ball app creates and updates it.

        - `INDEX.md`: every lecture grouped by course, newest first, with a short overview. **Read this first.**
        - `<id>/notes.md`: structured notes for one lecture (key ideas, terms, examples, homework and exam dates, bookmarked moments, review questions).
        - `<id>/transcript.md`: full transcript with `[mm:ss]` time marks. ⭐ marks moments I bookmarked in class as important.
        - `<id>/briefing.md`: the 2–3 minute study talk script for that lecture.
        - `<id>/lecture.json`: metadata (date, course, duration, status).

        When I ask about class:
        1. Start from `INDEX.md` to find the right lecture(s), then read their `notes.md`.
        2. Open the `transcript.md` when exact wording, an example, or a detail matters. Say which lecture (course, date) and roughly what time it came from.
        3. If something isn't in my lectures, say so before answering from general knowledge.
        4. Don't edit these files unless I ask. The app rewrites `INDEX.md` on its own.
        """;
}
