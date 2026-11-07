using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmileyBall.Web.Services;

/// <summary>Fish Audio: speech-to-text for the lecture, text-to-speech for the study briefing.</summary>
public class FishAudioClient
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly HttpClient _http;
    readonly SmileyOptions _o;
    readonly ILogger<FishAudioClient> _log;

    public FishAudioClient(IOptions<SmileyOptions> options, ILogger<FishAudioClient> log)
    {
        _o = options.Value;
        _log = log;
        // Long recordings can take several minutes to transcribe.
        _http = new HttpClient { BaseAddress = new Uri("https://api.fish.audio/"), Timeout = TimeSpan.FromMinutes(20) };
    }

    public bool Configured => !string.IsNullOrWhiteSpace(_o.FishApiKey);

    /// <summary>Transcribes one audio file (up to ~50 MB / 60 min) with time stamps.</summary>
    public Task<AsrResult> TranscribeAsync(string audioPath, CancellationToken ct) =>
        WithRetries(async () =>
        {
            using var form = new MultipartFormDataContent();
            var file = new StreamContent(File.OpenRead(audioPath));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "audio", Path.GetFileName(audioPath));
            form.Add(new StringContent(_o.Language), "language");
            form.Add(new StringContent("false"), "ignore_timestamps");   // we want time stamps
            form.Add(new StringContent("false"), "diarize");

            using var req = NewRequest(HttpMethod.Post, "v1/asr", _o.FishAsrModel);
            req.Content = form;
            using var res = await _http.SendAsync(req, ct);
            await ThrowIfFailed(res, "speech-to-text", ct);
            return (await res.Content.ReadFromJsonAsync<AsrResult>(Json, ct))
                   ?? throw new InvalidOperationException("Fish Audio returned an empty transcript.");
        }, ct);

    /// <summary>Reads the text aloud with your chosen voice and saves it as an MP3.</summary>
    public Task SpeakToFileAsync(string text, string mp3Path, CancellationToken ct) =>
        WithRetries(async () =>
        {
            var body = new Dictionary<string, object?>
            {
                ["text"] = text,
                ["format"] = "mp3",
                ["mp3_bitrate"] = 128,
                ["normalize"] = true,
                ["latency"] = "normal",
                ["language"] = _o.Language,
            };
            if (!string.IsNullOrWhiteSpace(_o.FishVoiceId)) body["reference_id"] = _o.FishVoiceId.Trim();

            using var req = NewRequest(HttpMethod.Post, "v1/tts", _o.FishTtsModel);
            req.Content = JsonContent.Create(body);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            await ThrowIfFailed(res, "text-to-speech", ct);

            var tmp = mp3Path + ".tmp";
            await using (var fs = File.Create(tmp))
                await res.Content.CopyToAsync(fs, ct);
            if (new FileInfo(tmp).Length < 1000)
                throw new InvalidOperationException("Fish Audio returned almost no audio.");
            File.Move(tmp, mp3Path, overwrite: true);
            return true;
        }, ct);

    HttpRequestMessage NewRequest(HttpMethod method, string path, string model)
    {
        if (!Configured) throw new InvalidOperationException("Set your Fish Audio API key (SmileyBall:FishApiKey).");
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.FishApiKey);
        req.Headers.Add("model", model);
        return req;
    }

    static async Task ThrowIfFailed(HttpResponseMessage res, string what, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var detail = await res.Content.ReadAsStringAsync(ct);
        if (detail.Length > 400) detail = detail[..400];
        var hint = res.StatusCode switch
        {
            HttpStatusCode.Unauthorized => " Check your Fish Audio API key.",
            HttpStatusCode.PaymentRequired => " Your Fish Audio account is out of credit.",
            HttpStatusCode.RequestEntityTooLarge => " The audio file is too big (limit about 50 MB).",
            _ => "",
        };
        throw new FishAudioException(res.StatusCode, $"Fish Audio {what} failed ({(int)res.StatusCode}).{hint} {detail}");
    }

    /// <summary>Retries busy-server errors and dropped connections, not bad requests.</summary>
    async Task<T> WithRetries<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await action(); }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested &&
                                       (ex is HttpRequestException ||
                                        ex is TaskCanceledException ||
                                        ex is FishAudioException { Status: >= HttpStatusCode.InternalServerError }))
            {
                _log.LogWarning("Fish Audio attempt {Attempt} failed: {Message}. Retrying...", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
    }
}

public class FishAudioException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
