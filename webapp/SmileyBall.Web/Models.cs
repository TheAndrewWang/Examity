using System.Text.Json.Serialization;

namespace SmileyBall.Web;

/// <summary>Settings from appsettings.json (section "SmileyBall") and user-secrets.</summary>
public class SmileyOptions
{
    public string BallUrl { get; set; } = "http://AtlistGoon.local:8765";
    public string BallToken { get; set; } = "";              // secret
    public int SyncIntervalSeconds { get; set; } = 60;
    public string LecturesPath { get; set; } = "Lectures";
    public string StudentName { get; set; } = "";
    public List<string> Courses { get; set; } = new();

    public string FishApiKey { get; set; } = "";             // secret
    public string FishVoiceId { get; set; } = "";
    public string FishTtsModel { get; set; } = "s2.1-pro";
    public string FishAsrModel { get; set; } = "transcribe-1-pro";
    public string Language { get; set; } = "en";

    public string AnthropicApiKey { get; set; } = "";        // secret (or ANTHROPIC_API_KEY env var)
    public string ClaudeModel { get; set; } = "claude-opus-5-5";
}

/// <summary>One lecture. Saved as lecture.json in the lecture's folder.</summary>
public class Lecture
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "Untitled lecture";
    public string Course { get; set; } = "Unsorted";
    public bool TitleSetByUser { get; set; }
    public bool CourseSetByUser { get; set; }
    public DateTimeOffset Date { get; set; }
    public int DurationSec { get; set; }
    public List<double> Bookmarks { get; set; } = new();
    public string Source { get; set; } = "ball";             // "ball" or "upload"
    public List<string> AudioFiles { get; set; } = new();

    /// downloading, new, transcribing, writing-notes, writing-briefing, recording-voice, ready, error
    public string Status { get; set; } = "new";
    public string? Error { get; set; }
    public string Overview { get; set; } = "";
    public bool HasBriefingAudio { get; set; }
}

// What the ball's recorder.py sends back.
public record BallFile(string Name, long Size);
public record BallSession(string Id, DateTimeOffset Started, DateTimeOffset? Ended, int Duration,
                          List<double> Bookmarks, bool Finished, List<BallFile> Files);
public record BallStatusDto(bool Recording, string? Mic, [property: JsonPropertyName("free_mb")] long FreeMb);

public class BallState
{
    public bool Reachable { get; set; }
    public bool Recording { get; set; }
    public string? Mic { get; set; }
    public long FreeMb { get; set; }
    public DateTimeOffset? LastContact { get; set; }
    public string? LastError { get; set; }
    public string? Activity { get; set; }
}

// Fish Audio speech-to-text result.
public record AsrSegment(string Text, double Start, double End);
public record AsrResult(string Text, double Duration, List<AsrSegment>? Segments);

// Requests from the web page.
public record AskTurn(string Q, string A);
public record AskRequest(string Question, string? LectureId, List<AskTurn>? History);
public record LectureEdit(string? Title, string? Course);
