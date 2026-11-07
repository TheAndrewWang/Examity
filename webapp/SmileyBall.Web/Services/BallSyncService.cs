using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SmileyBall.Web.Services;

/// <summary>
/// Every minute: asks the ball for finished recordings, downloads them, then tells the ball it can delete them.
/// </summary>
public partial class BallSyncService : BackgroundService
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly SmileyOptions _o;
    readonly LectureStore _store;
    readonly ProcessingQueue _queue;
    readonly ILogger<BallSyncService> _log;
    readonly HttpClient _http;
    readonly SemaphoreSlim _gate = new(1, 1);

    public BallState State { get; } = new();

    public BallSyncService(IOptions<SmileyOptions> options, LectureStore store, ProcessingQueue queue,
                           ILogger<BallSyncService> log)
    {
        _o = options.Value;
        _store = store;
        _queue = queue;
        _log = log;
        _http = new HttpClient
        {
            BaseAddress = new Uri(_o.BallUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(10),
        };
        _http.DefaultRequestHeaders.Add("X-Ball-Token", _o.BallToken);
    }

    [GeneratedRegex(@"^part(-\d+)?\.wav$")] private static partial Regex BallFileRe();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SyncOnceAsync(ct);
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _o.SyncIntervalSeconds)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task SyncOnceAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_o.BallToken))
        {
            State.Reachable = false;
            State.LastError = "Add the ball's token (SmileyBall:BallToken) to connect.";
            return;
        }
        if (!await _gate.WaitAsync(0, ct)) return;   // a sync is already running
        try
        {
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quick.CancelAfter(TimeSpan.FromSeconds(8));
            var status = await _http.GetFromJsonAsync<BallStatusDto>("api/status", Json, quick.Token);
            var sessions = await _http.GetFromJsonAsync<List<BallSession>>("api/recordings", Json, quick.Token) ?? new();
            State.Reachable = true;
            State.Recording = status?.Recording ?? false;
            State.Mic = status?.Mic;
            State.FreeMb = status?.FreeMb ?? 0;
            State.LastContact = DateTimeOffset.Now;
            State.LastError = null;

            foreach (var s in sessions.Where(s => s.Finished))
                await CollectAsync(s, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            State.Reachable = false;
            State.LastError = ex is TaskCanceledException ? "The ball didn't answer (is it on the same Wi-Fi?)" : ex.Message;
        }
        finally
        {
            State.Activity = null;
            _gate.Release();
        }
    }

    async Task CollectAsync(BallSession s, CancellationToken ct)
    {
        if (!LectureStore.ValidId(s.Id)) return;
        var files = s.Files.Where(f => BallFileRe().IsMatch(f.Name)).ToList();

        if (!_store.Exists(s.Id))
        {
            _store.Create(new Lecture
            {
                Id = s.Id,
                Title = $"Lecture on {s.Started.LocalDateTime:ddd, MMM d}",
                Date = s.Started,
                DurationSec = s.Duration,
                Bookmarks = s.Bookmarks ?? new(),
                Source = "ball",
                Status = "downloading",
            });
        }

        var audioDir = _store.FilePath(s.Id, "audio");
        Directory.CreateDirectory(audioDir);
        foreach (var f in files)
        {
            var dest = Path.Combine(audioDir, f.Name);
            if (File.Exists(dest) && new FileInfo(dest).Length == f.Size) continue;
            State.Activity = $"Downloading {s.Id}/{f.Name}";
            _log.LogInformation("Downloading {Id}/{File} ({Mb:0.0} MB)", s.Id, f.Name, f.Size / 1048576.0);

            var tmp = dest + ".part";
            using (var res = await _http.GetAsync($"api/recordings/{s.Id}/{f.Name}", HttpCompletionOption.ResponseHeadersRead, ct))
            {
                res.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await res.Content.CopyToAsync(fs, ct);
            }
            if (new FileInfo(tmp).Length != f.Size)
                throw new HttpRequestException($"Download of {f.Name} was incomplete - will try again.");
            File.Move(tmp, dest, overwrite: true);
        }

        _store.Update(s.Id, l =>
        {
            l.AudioFiles = files.Select(f => f.Name).ToList();
            if (l.Status == "downloading") l.Status = "new";
        });

        // Everything is safely on the laptop: free up space on the ball.
        using (var del = await _http.DeleteAsync($"api/recordings/{s.Id}", ct))
            if (!del.IsSuccessStatusCode) _log.LogWarning("Ball didn't delete {Id}: {Code}", s.Id, del.StatusCode);

        _log.LogInformation("Collected lecture {Id} from the ball", s.Id);
        _queue.Enqueue(s.Id);
    }
}
