using System.Text;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace SmileyBall.Web.Services;

public record NotesResult(string Title, string Course, string Overview, string NotesMarkdown);

/// <summary>Claude writes the notes and the study-talk script, and answers questions about class.</summary>
public partial class ClaudeService
{
    readonly AnthropicClient _client;
    readonly SmileyOptions _o;
    readonly LectureStore _store;

    public ClaudeService(IOptions<SmileyOptions> options, LectureStore store)
    {
        _o = options.Value;
        _store = store;
        // Uses SmileyBall:AnthropicApiKey if set, otherwise the ANTHROPIC_API_KEY environment variable.
        _client = string.IsNullOrWhiteSpace(_o.AnthropicApiKey)
            ? new AnthropicClient()
            : new AnthropicClient { ApiKey = _o.AnthropicApiKey };
    }

    public bool Configured =>
        !string.IsNullOrWhiteSpace(_o.AnthropicApiKey) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

    // ------------------------------------------------------------------ notes

    const string NotesSystem = """
        You turn a college lecture transcript into clear, well-organized study notes for the student who recorded it.

        The transcript comes from a small microphone in a classroom, so expect speech-recognition mistakes, crosstalk,
        and missing words. Fix obvious recognition errors silently (for example, a misheard technical term) when the
        context makes the right word clear. If something important is unclear, say so instead of guessing.
        Only include facts, dates, and assignments that are actually in the transcript. Never invent deadlines.

        Lines marked with ⭐ BOOKMARK are moments the student pressed a button because they thought it was important.
        Give those moments special attention.

        Reply in exactly this format:

        TITLE: <a short lecture title, at most 8 words>
        COURSE: <the course name: reuse one from the known list if it fits, otherwise your best short guess>
        OVERVIEW: <2 or 3 plain sentences: what this lecture covered and why it matters>
        ---
        <the notes in Markdown, using these sections in this order:>
        ## Key ideas
        ## Important terms
        ## Examples and worked problems
        ## ⭐ Bookmarked moments
        ## Homework, deadlines and exam hints
        ## Questions to review

        Under "Bookmarked moments", explain what was being taught at each ⭐ mark, with its [mm:ss] time.
        If a section has nothing in this lecture, write "Nothing mentioned." under it.
        Use LaTeX-free plain notation for math (e.g. x^2, sqrt(x), integral of f(x) dx).
        """;

    public async Task<NotesResult> MakeNotesAsync(Lecture lecture, string transcript, CancellationToken ct)
    {
        var courses = _o.Courses.Concat(_store.KnownCourses()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var user = $"""
            Known courses: {(courses.Count > 0 ? string.Join("; ", courses) : "(none yet)")}
            Recorded: {lecture.Date.LocalDateTime:dddd, MMMM d, yyyy h:mm tt}
            Length: about {Math.Max(1, lecture.DurationSec / 60)} minutes

            <transcript>
            {transcript}
            </transcript>
            """;

        var text = await StreamTextAsync(NotesSystem, user, Effort.High, 32000, null, ct);
        return ParseNotes(text);
    }

    [GeneratedRegex(@"^\s*TITLE:\s*(.+)$", RegexOptions.Multiline)] private static partial Regex TitleRe();
    [GeneratedRegex(@"^\s*COURSE:\s*(.+)$", RegexOptions.Multiline)] private static partial Regex CourseRe();
    [GeneratedRegex(@"^\s*OVERVIEW:\s*(.+?)(?=^\s*---\s*$)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex OverviewRe();
    [GeneratedRegex(@"^\s*---\s*$", RegexOptions.Multiline)] private static partial Regex SplitRe();

    static NotesResult ParseNotes(string text)
    {
        var title = TitleRe().Match(text) is { Success: true } t ? t.Groups[1].Value.Trim() : "Untitled lecture";
        var course = CourseRe().Match(text) is { Success: true } c ? c.Groups[1].Value.Trim() : "Unsorted";
        var overview = OverviewRe().Match(text) is { Success: true } o ? o.Groups[1].Value.Trim() : "";
        var split = SplitRe().Match(text);
        var notes = split.Success ? text[(split.Index + split.Length)..].Trim() : text.Trim();
        return new NotesResult(title.Trim('*', '"', ' '), course.Trim('*', '"', ' '), overview, notes);
    }

    // ------------------------------------------------------------------ study briefing

    const string BriefingSystem = """
        You are Smiley, a warm, upbeat study buddy who lives inside a little ball with a smiley face on it.
        The student is sitting down to study and do homework. Write a script that a text-to-speech voice will
        read aloud. It must take 2 to 3 minutes to say: between 330 and 420 words.

        This is spoken audio, so:
        - Write plain, natural spoken sentences. No Markdown, headings, bullet points, emojis, or stage directions.
        - Say math and symbols in words ("x squared", "the integral of f of x").
        - Keep sentences short and easy to follow by ear.

        Structure:
        1. A quick friendly hello that names the course and the lecture.
        2. A summary of that lecture: the three or four big ideas in plain language.
        3. If notes from the lecture before it are given, one or two sentences on how they connect.
        4. A short mini-lecture that teaches the single most important idea, with one concrete example.
        5. Before-homework tips: what to review, any homework or deadlines that were mentioned (only real ones),
           and one quick self-check question to think about (give the answer right after a short pause phrase
           like "Got it? Here's the answer.").
        6. A short, encouraging sign-off.
        Reply with only the script.
        """;

    public async Task<string> MakeBriefingAsync(Lecture lecture, string notes, Lecture? previous, string? previousNotes,
                                                CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(_o.StudentName)) sb.AppendLine($"Student's name: {_o.StudentName}");
        sb.AppendLine($"Course: {lecture.Course}");
        sb.AppendLine($"Lecture: {lecture.Title} ({lecture.Date.LocalDateTime:dddd, MMMM d})");
        sb.AppendLine();
        sb.AppendLine("<lecture_notes>");
        sb.AppendLine(notes);
        sb.AppendLine("</lecture_notes>");
        if (previous is not null && previousNotes is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"<previous_lecture title=\"{previous.Title}\" date=\"{previous.Date.LocalDateTime:MMMM d}\">");
            sb.AppendLine(previousNotes);
            sb.AppendLine("</previous_lecture>");
        }
        return (await StreamTextAsync(BriefingSystem, sb.ToString(), Effort.Medium, 8000, null, ct)).Trim();
    }

    // ------------------------------------------------------------------ questions

    const string AskSystem = """
        You are Smiley, the student's friendly study helper. You answer questions about their classes using the
        lecture notes below, which come from their own recorded lectures.

        - Base your answers on the notes. Say which lecture the information came from (course and date).
        - If the answer isn't in the notes, say so clearly, then help from general knowledge and label it as such.
        - Be concise and clear. Use Markdown (short paragraphs, lists, bold for key terms) when it helps.
        - If they ask for practice, make questions that match what their class actually covered.
        """;

    /// <summary>Streams an answer. All notes go in the system prompt, which is cached between questions.</summary>
    public async Task AskAsync(AskRequest req, Func<string, Task> onText, CancellationToken ct)
    {
        // Oldest first, so the cached part stays the same as new lectures are added at the end.
        var lectures = _store.All().Where(l => l.Status == "ready").OrderBy(l => l.Date).ToList();
        var sys = new StringBuilder(AskSystem);
        sys.AppendLine();
        sys.AppendLine("<lecture_notes>");
        foreach (var l in lectures)
        {
            sys.AppendLine($"=== {l.Course} · {l.Date.LocalDateTime:ddd yyyy-MM-dd} · {l.Title} (id {l.Id}) ===");
            sys.AppendLine(_store.ReadText(l.Id, "notes.md") ?? "(no notes)");
            sys.AppendLine();
        }
        if (lectures.Count == 0) sys.AppendLine("(No lectures have been processed yet.)");
        sys.AppendLine("</lecture_notes>");

        var user = new StringBuilder();
        if (req.LectureId is { } id && _store.Get(id) is { } focus)
        {
            user.AppendLine($"I'm asking about this lecture: {focus.Course} · {focus.Title} ({focus.Date.LocalDateTime:MMMM d}).");
            user.AppendLine("Its full transcript, in case you need exact details:");
            user.AppendLine("<transcript>");
            user.AppendLine(_store.ReadText(id, "transcript.md") ?? "(no transcript)");
            user.AppendLine("</transcript>");
            user.AppendLine();
        }
        if (req.History is { Count: > 0 } history)
        {
            user.AppendLine("Our conversation so far:");
            foreach (var turn in history.TakeLast(8))
            {
                user.AppendLine($"Me: {turn.Q}");
                user.AppendLine($"You: {turn.A}");
            }
            user.AppendLine();
        }
        user.AppendLine($"My question: {req.Question}");

        await StreamTextAsync(sys.ToString(), user.ToString(), Effort.Medium, 16000, onText, ct);
    }

    // ------------------------------------------------------------------ shared

    async Task<string> StreamTextAsync(string system, string user, Effort effort, int maxTokens,
                                       Func<string, Task>? onText, CancellationToken ct)
    {
        if (!Configured) throw new InvalidOperationException("Set your Claude API key (SmileyBall:AnthropicApiKey or ANTHROPIC_API_KEY).");

        var parameters = new MessageCreateParams
        {
            Model = _o.ClaudeModel,
            MaxTokens = maxTokens,
            System = new List<TextBlockParam>
            {
                new() { Text = system, CacheControl = new CacheControlEphemeral() },
            },
            OutputConfig = new OutputConfig { Effort = effort },
            Messages = [new UserMessageParam { Content = user }],
        };

        var sb = new StringBuilder();
        await foreach (var ev in _client.Messages.CreateStreaming(parameters).WithCancellation(ct))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
            {
                sb.Append(text.Text);
                if (onText is not null) await onText(text.Text);
            }
        }
        if (sb.Length == 0)
            throw new InvalidOperationException("Claude returned no text (the request may have been declined).");
        return sb.ToString();
    }
}
