using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace SmileyBall.Web.Services;

/// <summary>
/// Turns a recording into a finished lecture, one lecture at a time:
///   audio -> transcript.md (Fish Audio) -> notes.md (Claude) -> briefing.md (Claude) -> briefing.mp3 (Fish Audio)
/// Each step saves its result, so a failed lecture can be retried without paying for the earlier steps again.
/// </summary>
public partial class ProcessingQueue(LectureStore store, FishAudioClient fish, ClaudeService claude,
                                     ILogger<ProcessingQueue> log) : BackgroundService
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly Channel<string> _channel = Channel.CreateUnbounded<string>();
    readonly ConcurrentDictionary<string, bool> _queued = new();

    public string? CurrentId { get; private set; }
    public int Pending => _queued.Count;

    public void Enqueue(string id)
    {
        if (_queued.TryAdd(id, true)) _channel.Writer.TryWrite(id);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Pick up anything that was half-done when the app last closed.
        foreach (var l in store.All())
            if (l.Status is not ("ready" or "error" or "downloading")) Enqueue(l.Id);

        await foreach (var id in _channel.Reader.ReadAllAsync(ct))
        {
            CurrentId = id;
            try
            {
                await ProcessAsync(id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Processing {Id} failed", id);
                store.Update(id, l => { l.Status = "error"; l.Error = ex.Message; });
            }
            finally
            {
                CurrentId = null;
                _queued.TryRemove(id, out _);
            }
        }
    }

    async Task ProcessAsync(string id, CancellationToken ct)
    {
        var lecture = store.Get(id);
        if (lecture is null || lecture.Status == "downloading") return;
        store.Update(id, l => l.Error = null);

        // 1. Transcript
        var transcript = store.ReadText(id, "transcript.md");
        if (transcript is null)
        {
            SetStatus(id, "transcribing");
            transcript = await TranscribeAsync(lecture, ct);
            store.WriteText(id, "transcript.md", transcript);
            lecture = store.Get(id)!;
        }

        // 2. Notes
        var notes = store.ReadText(id, "notes.md");
        if (notes is null)
        {
            SetStatus(id, "writing-notes");
            var result = await claude.MakeNotesAsync(lecture, transcript, ct);
            var known = store.KnownCourses();
            var course = known.FirstOrDefault(c => c.Equals(result.Course, StringComparison.OrdinalIgnoreCase)) ?? result.Course;
            lecture = store.Update(id, l =>
            {
                if (!l.TitleSetByUser) l.Title = result.Title;
                if (!l.CourseSetByUser) l.Course = string.IsNullOrWhiteSpace(course) ? "Unsorted" : course;
                l.Overview = result.Overview;
            })!;
            notes = $"# {lecture.Title}\n\n_{lecture.Course} · {lecture.Date.LocalDateTime:dddd, MMMM d, yyyy}_\n\n" +
                    $"> {result.Overview}\n\n{result.NotesMarkdown}\n";
            store.WriteText(id, "notes.md", notes);
        }

        // 3. Study talk script
        var briefing = store.ReadText(id, "briefing.md");
        if (briefing is null)
        {
            SetStatus(id, "writing-briefing");
            var previous = store.All()
                .Where(l => l.Id != id && l.Course == lecture.Course && l.Date < lecture.Date && l.Status == "ready")
                .OrderByDescending(l => l.Date)
                .FirstOrDefault();
            var previousNotes = previous is null ? null : store.ReadText(previous.Id, "notes.md");
            briefing = await claude.MakeBriefingAsync(lecture, notes, previous, previousNotes, ct);
            store.WriteText(id, "briefing.md", briefing);
        }

        // 4. Study talk audio
        if (!File.Exists(store.FilePath(id, "briefing.mp3")))
        {
            SetStatus(id, "recording-voice");
            await fish.SpeakToFileAsync(briefing, store.FilePath(id, "briefing.mp3"), ct);
        }

        store.Update(id, l => { l.Status = "ready"; l.HasBriefingAudio = true; l.Error = null; });
        log.LogInformation("Lecture {Id} is ready: {Title}", id, lecture.Title);
    }

    void SetStatus(string id, string status) => store.Update(id, l => l.Status = status);

    async Task<string> TranscribeAsync(Lecture lecture, CancellationToken ct)
    {
        var audioDir = store.FilePath(lecture.Id, "audio");
        var workDir = store.FilePath(lecture.Id, "work");
        Directory.CreateDirectory(workDir);
        var files = Directory.Exists(audioDir)
            ? Directory.GetFiles(audioDir).Select(Path.GetFileName).OfType<string>()
                .Where(n => !n.EndsWith(".part")).OrderBy(PartNumber).ThenBy(n => n).ToList()
            : new List<string>();
        if (files.Count == 0) throw new InvalidOperationException("This lecture has no audio files.");

        var bookmarks = new Queue<double>(lecture.Bookmarks.OrderBy(b => b));
        var sb = new StringBuilder();
        sb.AppendLine("# Transcript");
        sb.AppendLine();
        sb.AppendLine($"_Recorded {lecture.Date.LocalDateTime:dddd, MMMM d, yyyy h:mm tt}. " +
                      "[mm:ss] marks show time from the start. ⭐ marks moments bookmarked in class._");
        sb.AppendLine();

        double offset = 0, nextStamp = 0;
        foreach (var name in files)
        {
            // Save each chunk's result so a retry doesn't pay to transcribe it again.
            var cache = Path.Combine(workDir, name + ".asr.json");
            AsrResult result;
            if (File.Exists(cache))
            {
                result = JsonSerializer.Deserialize<AsrResult>(await File.ReadAllTextAsync(cache, ct), Json)!;
            }
            else
            {
                log.LogInformation("Transcribing {Id}/{File}", lecture.Id, name);
                result = await fish.TranscribeAsync(Path.Combine(audioDir, name), ct);
                await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(result, Json), ct);
            }

            var segments = result.Segments ?? new List<AsrSegment>();
            if (segments.Count == 0)
            {
                AddBookmarks(sb, bookmarks, offset);
                sb.Append($"\n\n[{Stamp(offset)}] ").Append(result.Text.Trim()).Append(' ');
            }
            foreach (var seg in segments)
            {
                var t = offset + seg.Start;
                AddBookmarks(sb, bookmarks, t);
                if (t >= nextStamp)
                {
                    sb.Append($"\n\n[{Stamp(t)}] ");
                    nextStamp = t + 60;   // a time mark about once a minute
                }
                sb.Append(seg.Text.Trim()).Append(' ');
            }
            offset += result.Duration;
        }
        AddBookmarks(sb, bookmarks, double.MaxValue);

        store.Update(lecture.Id, l => l.DurationSec = Math.Max(l.DurationSec, (int)offset));
        var text = SpaceBeforePunctuation().Replace(sb.ToString(), "$1");
        text = MultiSpace().Replace(text, " ");
        text = TrailingSpace().Replace(text, "\n");
        return ExtraBlankLines().Replace(text, "\n\n").Trim() + "\n";
    }

    static void AddBookmarks(StringBuilder sb, Queue<double> bookmarks, double upTo)
    {
        while (bookmarks.Count > 0 && bookmarks.Peek() <= upTo)
            sb.Append($"\n\n⭐ BOOKMARK [{Stamp(bookmarks.Dequeue())}]: marked as important in class.\n\n");
    }

    static string Stamp(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    static int PartNumber(string name) =>
        PartNumberRe().Match(name) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    [GeneratedRegex(@"-(\d+)\.\w+$")] private static partial Regex PartNumberRe();
    [GeneratedRegex(@" +([,.!?;:])")] private static partial Regex SpaceBeforePunctuation();
    [GeneratedRegex(@"[ \t]{2,}")] private static partial Regex MultiSpace();
    [GeneratedRegex(@"[ \t]+\n")] private static partial Regex TrailingSpace();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex ExtraBlankLines();
}
