using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using SmileyBall.Web;
using SmileyBall.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Keys live in user-secrets (dotnet user-secrets set "SmileyBall:FishApiKey" "...") so they never end up in a file you share.
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Services.Configure<SmileyOptions>(builder.Configuration.GetSection("SmileyBall"));

// Allow uploading long lecture recordings by hand.
const long MaxUpload = 1L << 30; // 1 GB
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxUpload);
builder.Services.Configure<FormOptions>(f => f.MultipartBodyLengthLimit = MaxUpload);

builder.Services.AddSingleton<LectureStore>();
builder.Services.AddSingleton<FishAudioClient>();
builder.Services.AddSingleton<ClaudeService>();
builder.Services.AddSingleton<ProcessingQueue>();
builder.Services.AddSingleton<BallSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessingQueue>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BallSyncService>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

// ---------------------------------------------------------------- status
api.MapGet("/status", (BallSyncService ball, ProcessingQueue queue, LectureStore store, FishAudioClient fish,
                       ClaudeService claude, Microsoft.Extensions.Options.IOptions<SmileyOptions> o) =>
{
    var current = queue.CurrentId is { } id ? store.Get(id) : null;
    return Results.Ok(new
    {
        ball = ball.State,
        ballUrl = o.Value.BallUrl,
        processing = current is null ? null : new { current.Id, current.Title, current.Status },
        pending = queue.Pending,
        lecturesFolder = store.Root,
        setup = new
        {
            ballToken = !string.IsNullOrWhiteSpace(o.Value.BallToken),
            fishKey = fish.Configured,
            fishVoice = !string.IsNullOrWhiteSpace(o.Value.FishVoiceId),
            claudeKey = claude.Configured,
        },
    });
});

api.MapPost("/sync", async (BallSyncService ball, CancellationToken ct) =>
{
    await ball.SyncOnceAsync(ct);
    return Results.Ok(ball.State);
});

// ---------------------------------------------------------------- lectures
api.MapGet("/lectures", (LectureStore store) => store.All());

api.MapGet("/lectures/{id}", (string id, LectureStore store) =>
{
    if (store.Get(id) is not { } l) return Results.NotFound();
    return Results.Ok(new
    {
        lecture = l,
        notes = store.ReadText(id, "notes.md"),
        briefing = store.ReadText(id, "briefing.md"),
        transcript = store.ReadText(id, "transcript.md"),
    });
});

api.MapPut("/lectures/{id}", (string id, LectureEdit edit, LectureStore store) =>
{
    var l = store.Update(id, l =>
    {
        if (!string.IsNullOrWhiteSpace(edit.Title)) { l.Title = edit.Title.Trim(); l.TitleSetByUser = true; }
        if (!string.IsNullOrWhiteSpace(edit.Course)) { l.Course = edit.Course.Trim(); l.CourseSetByUser = true; }
    });
    return l is null ? Results.NotFound() : Results.Ok(l);
});

api.MapPost("/lectures/{id}/retry", (string id, LectureStore store, ProcessingQueue queue) =>
{
    if (store.Update(id, l => { if (l.Status == "error") l.Status = "new"; }) is null) return Results.NotFound();
    queue.Enqueue(id);
    return Results.Ok();
});

// Makes new notes + talk (for example after you fix the course name), keeping the paid-for transcript.
api.MapPost("/lectures/{id}/regenerate", (string id, string? what, LectureStore store, ProcessingQueue queue) =>
{
    if (!store.Exists(id)) return Results.NotFound();
    if (what == "notes") store.DeleteFile(id, "notes.md");
    store.DeleteFile(id, "briefing.md");
    store.DeleteFile(id, "briefing.mp3");
    store.Update(id, l => { l.Status = "new"; l.HasBriefingAudio = false; });
    queue.Enqueue(id);
    return Results.Ok();
});

api.MapDelete("/lectures/{id}", (string id, LectureStore store) =>
{
    if (!store.Exists(id)) return Results.NotFound();
    store.Delete(id);
    return Results.Ok();
});

api.MapGet("/lectures/{id}/briefing.mp3", (string id, LectureStore store) =>
{
    if (!store.Exists(id)) return Results.NotFound();
    var path = store.FilePath(id, "briefing.mp3");
    return File.Exists(path) ? Results.File(path, "audio/mpeg", enableRangeProcessing: true) : Results.NotFound();
});

api.MapGet("/lectures/{id}/audio/{file}", (string id, string file, LectureStore store) =>
{
    if (!store.Exists(id) || file != Path.GetFileName(file) || file.Contains("..")) return Results.NotFound();
    var path = Path.Combine(store.FilePath(id, "audio"), file);
    if (!File.Exists(path)) return Results.NotFound();
    var type = Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4",
        ".ogg" => "audio/ogg", ".webm" => "audio/webm", ".flac" => "audio/flac", _ => "application/octet-stream",
    };
    return Results.File(path, type, enableRangeProcessing: true);
});

// "Study now": the newest lecture whose talk is ready.
api.MapGet("/study", (LectureStore store) =>
{
    var l = store.All().FirstOrDefault(l => l.Status == "ready" && l.HasBriefingAudio);
    return l is null ? Results.NoContent() : Results.Ok(new { lecture = l, briefing = store.ReadText(l.Id, "briefing.md") });
});

// Add a recording by hand (phone recording, or when the ball isn't reachable).
api.MapPost("/upload", async (HttpRequest req, LectureStore store, ProcessingQueue queue, CancellationToken ct) =>
{
    var form = await req.ReadFormAsync(ct);
    if (form.Files.GetFile("file") is not { Length: > 0 } file) return Results.BadRequest("No file.");
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!Regex.IsMatch(ext, @"^\.(wav|mp3|m4a|ogg|webm|flac)$")) return Results.BadRequest("Use a wav, mp3, m4a, ogg, webm or flac file.");

    var now = DateTimeOffset.Now;
    var id = $"{now:yyyy-MM-dd_HHmmss}_upload";
    var course = form["course"].ToString().Trim();
    store.Create(new Lecture
    {
        Id = id,
        Title = Path.GetFileNameWithoutExtension(file.FileName),
        Course = string.IsNullOrWhiteSpace(course) ? "Unsorted" : course,
        CourseSetByUser = !string.IsNullOrWhiteSpace(course),
        Date = now,
        Source = "upload",
        Status = "downloading",
        AudioFiles = [$"upload{ext}"],
    });
    await using (var fs = File.Create(Path.Combine(store.FilePath(id, "audio"), $"upload{ext}")))
        await file.CopyToAsync(fs, ct);
    store.Update(id, l => l.Status = "new");
    queue.Enqueue(id);
    return Results.Ok(new { id });
});

// ---------------------------------------------------------------- questions
api.MapPost("/ask", async (AskRequest req, HttpContext ctx, ClaudeService claude, ILogger<Program> log) =>
{
    if (string.IsNullOrWhiteSpace(req.Question)) return Results.BadRequest("Ask a question.");
    ctx.Response.ContentType = "text/plain; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";
    var ct = ctx.RequestAborted;
    try
    {
        await claude.AskAsync(req, async text =>
        {
            await ctx.Response.WriteAsync(text, ct);
            await ctx.Response.Body.FlushAsync(ct);
        }, ct);
    }
    catch (Exception ex) when (!ct.IsCancellationRequested)
    {
        log.LogError(ex, "Ask failed");
        await ctx.Response.WriteAsync($"\n\n⚠️ Sorry, that didn't work: {ex.Message}", ct);
    }
    return Results.Empty;
});

app.Logger.LogInformation("Smiley Ball is running. Open http://localhost:5080  ·  Lectures folder: {Folder}",
    app.Services.GetRequiredService<LectureStore>().Root);
app.Run();
