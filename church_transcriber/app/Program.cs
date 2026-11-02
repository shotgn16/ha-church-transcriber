using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChurchTranscriber;
using ChurchTranscriber.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

var paths = new AppPaths();
using var bootLog = LoggerFactory.Create(b => b.AddSimpleConsole());
var opts = AddonOptions.Load(paths.OptionsFile, bootLog.CreateLogger("Options"));

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(opts);
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<YouTube>();
builder.Services.AddSingleton<Transcriber>();
builder.Services.AddSingleton<Ollama>();
builder.Services.AddSingleton<Sectioner>();
builder.Services.AddSingleton<Translator>();
builder.Services.AddSingleton<TranscriptionWorker>();
builder.Services.AddSingleton<Scheduler>();
builder.Services.AddSingleton<Supervisor>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TranscriptionWorker>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Scheduler>());
builder.Services.AddHostedService<TranslationWorker>();
builder.Services.AddHostedService<StartupTasks>();
builder.Services.AddHttpClient("ollama", c => c.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddHttpClient("ollama-short", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient("supervisor", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("webhook", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") == null) builder.WebHost.UseUrls("http://0.0.0.0:8099");

var app = builder.Build();

// ---- Access control: the HA ingress proxy (172.30.32.2) is already authenticated by Home Assistant. ----
app.Use(async (ctx, next) =>
{
    var password = opts.AccessPassword;
    var remote = ctx.Connection.RemoteIpAddress;
    var isIngress = remote != null && (remote.Equals(IPAddress.Parse("172.30.32.2")) || remote.MapToIPv4().Equals(IPAddress.Parse("172.30.32.2")));
    var isLocal = IPAddress.IsLoopback(remote ?? IPAddress.Loopback);

    // Automation API: protected by its own API key (works from HA automations, Node-RED, scripts...).
    if (ctx.Request.Path.StartsWithSegments("/api/v1"))
    {
        if (isIngress || isLocal) { await next(); return; }
        var key = ctx.RequestServices.GetRequiredService<Store>().GetSettings().ApiKey;
        var auth = ctx.Request.Headers.Authorization.ToString();
        var given = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim()
            : ctx.Request.Headers["X-API-Key"].FirstOrDefault() ?? ctx.Request.Query["api_key"].FirstOrDefault() ?? "";
        if (key.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key))) { await next(); return; }
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new { error = "Missing or invalid API key. Send it as 'Authorization: Bearer <key>' - find it on the app's API page." });
        return;
    }
    if (string.IsNullOrEmpty(password) || isIngress || isLocal) { await next(); return; }
    var header = ctx.Request.Headers.Authorization.ToString();
    if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
            var given = decoded[(decoded.IndexOf(':') + 1)..];
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(password))) { await next(); return; }
        }
        catch (FormatException) { }
    }
    ctx.Response.StatusCode = 401;
    ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"Church Transcriber\", charset=\"UTF-8\"";
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache"
});

var api = app.MapGroup("/api");

// ---------- Transcripts ----------
api.MapGet("/transcripts", (Store s) => s.Summaries());

api.MapPost("/transcripts", (NewJobRequest req, Store s, JobQueue q) =>
{
    var urls = (req.Urls ?? "").Split(new[] { '\n', '\r', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(u => u.Contains("youtu", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
    if (urls.Count == 0) return Results.BadRequest(new { error = "Paste at least one YouTube link." });
    var settings = s.GetSettings();
    var created = new List<string>();
    foreach (var raw in urls)
    {
        var url = YouTube.CanonicalUrl(raw);
        var t = new Transcript
        {
            Url = url, VideoId = YouTube.ExtractVideoId(url),
            Language = string.IsNullOrWhiteSpace(req.Language) ? settings.DefaultLanguage : req.Language!,
            AutoTranslateTo = req.TranslateTo ?? settings.AutoTranslateTo.ToList(), StatusMessage = "Waiting in queue"
        };
        s.Add(t);
        q.Enqueue(t.Id);
        created.Add(t.Id);
    }
    return Results.Ok(new { ids = created });
});

api.MapGet("/transcripts/{id}", (string id, Store s) =>
{
    var t = s.Snapshot(id);
    if (t == null) return Results.NotFound();
    t.Segments = new(); // raw segments are large and only needed for SRT export
    return Results.Json(t, Store.Json);
});

api.MapPut("/transcripts/{id}", (string id, TranscriptEdit edit, Store s) =>
{
    var ok = s.Update(id, t =>
    {
        if (edit.Title != null) t.Title = edit.Title.Trim();
        if (edit.SummaryHtml != null) t.SummaryHtml = Html.Sanitize(edit.SummaryHtml);
        if (edit.Sections != null)
        {
            foreach (var sec in edit.Sections)
            {
                sec.Html = Html.Sanitize(sec.Html);
                sec.Title = sec.Title.Trim();
                if (!Sectioner.KindNames.ContainsKey(sec.Kind)) sec.Kind = "other";
            }
            t.Sections = edit.Sections;
        }
        t.Edited = true;
    });
    return ok ? Results.Ok(new { saved = DateTimeOffset.UtcNow }) : Results.NotFound();
});

api.MapDelete("/transcripts/{id}", (string id, Store s, JobQueue q) =>
{
    q.Cancel(id);
    return s.Delete(id) ? Results.Ok() : Results.NotFound();
});

api.MapPost("/transcripts/{id}/cancel", (string id, Store s, JobQueue q) =>
{
    q.Cancel(id);
    s.Update(id, t => { if (t.Status is JobStatus.Waiting or JobStatus.Queued) { t.Status = JobStatus.Cancelled; t.StatusMessage = "Cancelled"; } });
    return Results.Ok();
});

api.MapPost("/transcripts/{id}/retry", (string id, Store s, JobQueue q, AppPaths p) =>
{
    var full = s.Snapshot(id)?.Status == JobStatus.Ready; // "transcribe again" on a finished service = start from scratch
    if (!s.Update(id, t =>
    {
        t.Status = JobStatus.Queued; t.Error = null; t.ErrorDetails = null; t.Progress = 0; t.RetryCount = 0;
        t.StatusMessage = full ? "Waiting in queue" : "Resuming - waiting in queue";
        if (full) { t.Segments = new(); t.Sections = new(); t.HasAudio = false; }
    })) return Results.NotFound();
    if (full) try { File.Delete(p.AudioFile(id)); } catch { }
    q.Enqueue(id);
    return Results.Ok();
});

api.MapPost("/transcripts/{id}/resection", async (string id, Store s, TranscriptionWorker w) =>
{
    var t = s.Snapshot(id);
    if (t == null) return Results.NotFound();
    if (t.Segments.Count == 0) return Results.BadRequest(new { error = "This service has not been transcribed yet." });
    _ = Task.Run(async () =>
    {
        try { await w.OrganiseAsync(id, CancellationToken.None); }
        catch (Exception ex) { s.Update(id, x => { x.Status = JobStatus.Ready; x.StatusMessage = "Re-organising failed: " + ex.Message; }); }
    });
    await Task.CompletedTask;
    return Results.Ok();
});

api.MapPost("/transcripts/{id}/translate", (string id, TranslateRequest req, Store s, JobQueue q) =>
{
    var lang = req.Lang?.Trim();
    if (string.IsNullOrWhiteSpace(lang)) return Results.BadRequest(new { error = "Choose a language." });
    var t = s.Snapshot(id);
    if (t == null) return Results.NotFound();
    if (t.Status != JobStatus.Ready) return Results.BadRequest(new { error = "Wait until transcription has finished." });
    s.Update(id, x => x.Translations.Remove(lang));
    TranscriptionWorker.RequestTranslation(s, q, id, lang);
    return Results.Ok();
});

api.MapPut("/transcripts/{id}/translations/{lang}", (string id, string lang, TranslationEdit edit, Store s) =>
{
    var ok = false;
    s.Update(id, t =>
    {
        if (!t.Translations.TryGetValue(lang, out var tr)) return;
        if (edit.Title != null) tr.Title = edit.Title.Trim();
        if (edit.SummaryHtml != null) tr.SummaryHtml = Html.Sanitize(edit.SummaryHtml);
        if (edit.Sections != null) tr.Sections = edit.Sections.Select(x => new SectionText { Id = x.Id, Title = x.Title.Trim(), Html = Html.Sanitize(x.Html) }).ToList();
        tr.UpdatedAt = DateTimeOffset.UtcNow;
        ok = true;
    });
    return ok ? Results.Ok() : Results.NotFound();
});

api.MapDelete("/transcripts/{id}/translations/{lang}", (string id, string lang, Store s, JobQueue q) =>
{
    q.Cancel($"{id}:{lang}");
    s.Update(id, t => t.Translations.Remove(lang));
    return Results.Ok();
});

api.MapGet("/transcripts/{id}/audio", (string id, AppPaths p) =>
{
    var file = p.AudioFile(id);
    return File.Exists(file) ? Results.File(file, "audio/mp4", enableRangeProcessing: true) : Results.NotFound();
});

api.MapGet("/transcripts/{id}/export", (string id, string? format, string? lang, int? ts, Store s) => ExportResult(s, id, format, lang, ts));

// ---------- Channel watches ----------
api.MapGet("/watches", (Store s) => s.Watches().Select(w => new
{
    w.Id, w.Name, w.Url, w.TitleFilter, w.Language, w.AutoTranslateTo, w.IntervalMinutes, w.Enabled,
    w.IncludeLatestExisting, w.LastChecked, w.LastError, w.QueuedCount, w.CreatedAt
}));

api.MapPost("/watches", async (WatchRequest req, Store s, Scheduler sch) =>
{
    if (string.IsNullOrWhiteSpace(req.Url)) return Results.BadRequest(new { error = "Enter the church's YouTube channel or playlist link." });
    var w = new Watch
    {
        Url = YouTube.NormalizeWatchUrl(req.Url, req.UseStreamsTab ?? true),
        Name = string.IsNullOrWhiteSpace(req.Name) ? "Church channel" : req.Name.Trim(),
        TitleFilter = req.TitleFilter?.Trim() ?? "", Language = req.Language ?? "",
        AutoTranslateTo = req.TranslateTo ?? new(), IntervalMinutes = Math.Clamp(req.IntervalMinutes ?? 30, 5, 1440),
        IncludeLatestExisting = req.IncludeLatestExisting ?? false
    };
    s.UpsertWatch(w);
    var queued = await sch.CheckWatchAsync(w.Id, CancellationToken.None);
    return Results.Ok(new { w.Id, queued, error = s.GetWatch(w.Id)?.LastError });
});

api.MapPut("/watches/{id}", (string id, WatchRequest req, Store s) =>
    s.UpdateWatch(id, w =>
    {
        if (req.Name != null) w.Name = req.Name.Trim();
        if (req.TitleFilter != null) w.TitleFilter = req.TitleFilter.Trim();
        if (req.Language != null) w.Language = req.Language;
        if (req.TranslateTo != null) w.AutoTranslateTo = req.TranslateTo;
        if (req.IntervalMinutes != null) w.IntervalMinutes = Math.Clamp(req.IntervalMinutes.Value, 5, 1440);
        if (req.Enabled != null) w.Enabled = req.Enabled.Value;
    }) ? Results.Ok() : Results.NotFound());

api.MapPost("/watches/{id}/check", async (string id, Store s, Scheduler sch) =>
{
    var queued = await sch.CheckWatchAsync(id, CancellationToken.None);
    return Results.Ok(new { queued, error = s.GetWatch(id)?.LastError });
});

api.MapDelete("/watches/{id}", (string id, Store s) => s.DeleteWatch(id) ? Results.Ok() : Results.NotFound());

// ---------- Settings & status ----------
api.MapGet("/settings", (Store s) => s.GetSettings());
api.MapPut("/settings", (Settings settings, Store s) =>
{
    // The settings page doesn't edit API/notification fields - keep them.
    var current = s.GetSettings();
    settings.ApiKey = current.ApiKey; settings.HaEvents = current.HaEvents;
    settings.WebhookUrl = current.WebhookUrl; settings.NotifySelection = current.NotifySelection;
    s.SaveSettings(settings);
    return Results.Ok();
});

// ---------- Automation settings (used by the API page) ----------
api.MapGet("/automation", async (Store s, Supervisor sup, Notifier n) =>
{
    var st = s.GetSettings();
    return Results.Ok(new
    {
        apiKey = st.ApiKey, haEvents = st.HaEvents, webhookUrl = st.WebhookUrl, notifySelection = st.NotifySelection,
        internalBaseUrl = await n.InternalBaseUrlAsync(CancellationToken.None), runningInHomeAssistant = sup.Available,
        events = new[] { Notifier.ReadyEvent, Notifier.TranslationEvent, Notifier.FailedEvent }
    });
});

api.MapPut("/automation", (AutomationEdit e, Store s) =>
{
    var st = s.GetSettings();
    if (e.HaEvents != null) st.HaEvents = e.HaEvents.Value;
    if (e.WebhookUrl != null)
    {
        var url = e.WebhookUrl.Trim();
        if (url.Length > 0 && !Uri.TryCreate(url, UriKind.Absolute, out var u) | (u != null && u.Scheme is not ("http" or "https")))
            return Results.BadRequest(new { error = "The webhook must be an http(s) address." });
        st.WebhookUrl = url;
    }
    if (e.NotifySelection != null) st.NotifySelection = ContentSelection.Parse(e.NotifySelection).ToQuery();
    s.SaveSettings(st);
    return Results.Ok(new { st.NotifySelection });
});

api.MapPost("/automation/regenerate-key", (Store s) =>
{
    var st = s.GetSettings();
    st.ApiKey = Store.NewApiKey();
    s.SaveSettings(st);
    return Results.Ok(new { apiKey = st.ApiKey });
});

api.MapPost("/automation/test", async (Store s, Notifier n) =>
{
    var latest = LatestReady(s, null, null);
    if (latest == null) return Results.BadRequest(new { error = "Finish transcribing at least one service first." });
    await n.TranscriptReadyAsync(latest.Id);
    return Results.Ok(new { sent = latest.Title });
});

// =====================================================================
// Automation API v1 - for Home Assistant automations, Node-RED, scripts.
// Requires the API key unless called through the HA sidebar (ingress).
// =====================================================================
var v1 = app.MapGroup("/api/v1");

v1.MapGet("/status", (Store s, JobQueue q) => Results.Ok(new
{
    version = typeof(Program).Assembly.GetName().Version?.ToString(3),
    queue = q.Pending,
    processing = s.SnapshotAll(t => t.IsActive).Select(t => new { t.Id, t.Title, status = t.Status.ToString().ToLowerInvariant(), progress = Math.Round(t.Progress, 2) }),
    waiting = s.SnapshotAll(t => t.Status == JobStatus.Waiting).Select(t => new { t.Id, t.Title, t.ScheduledStart }),
    ready = s.SnapshotAll(t => t.Status == JobStatus.Ready).Count
}));

v1.MapGet("/transcripts", (Store s, string? status, string? q, int? limit) =>
{
    var items = s.SnapshotAll(t => (string.IsNullOrEmpty(status) || t.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase))
                                && (string.IsNullOrEmpty(q) || (t.Title + " " + t.Channel).Contains(q, StringComparison.OrdinalIgnoreCase)))
        .OrderByDescending(SortKey).Take(Math.Clamp(limit ?? 20, 1, 200))
        .Select(t => new
        {
            id = t.Id, title = t.Title, channel = t.Channel, date = t.UploadDate, url = t.Url, duration = Math.Round(t.Duration),
            status = t.Status.ToString().ToLowerInvariant(), completed_at = t.CompletedAt, scheduled_start = t.ScheduledStart,
            language = t.DetectedLanguage, translations = t.Translations.Values.Where(x => x.Status == "done").Select(x => x.Lang),
            sections = t.Sections.Select((x, i) => new { index = i + 1, id = x.Id, kind = x.Kind, title = x.Title })
        });
    return Results.Ok(items);
});

v1.MapGet("/transcripts/{id}", async (string id, HttpRequest req, Store s, Notifier n, string? channel, string? watch) =>
{
    var t = id.Equals("latest", StringComparison.OrdinalIgnoreCase) ? LatestReady(s, channel, watch) : s.Snapshot(id);
    if (t == null) return Results.NotFound(new { error = id == "latest" ? "No finished transcripts yet." : "Transcript not found." });
    if (t.Status != JobStatus.Ready)
        return Results.Json(new { id = t.Id, title = t.Title, status = t.Status.ToString().ToLowerInvariant(), progress = t.Progress, message = t.StatusMessage }, statusCode: 202);
    var sel = ContentSelection.Parse(req.Query);
    return sel.Format switch
    {
        "text" => Results.Text(ContentApi.Truncate(ContentApi.Text(t, sel), sel.MaxChars).Text, "text/plain; charset=utf-8"),
        "markdown" => Results.Text(ContentApi.Markdown(t, sel), "text/markdown; charset=utf-8"),
        "html" => Results.Content(ContentApi.HtmlPage(t, sel), "text/html; charset=utf-8"),
        _ => Results.Text(ContentApi.Json(t, sel, await n.InternalBaseUrlAsync(CancellationToken.None)).ToJsonString(), "application/json; charset=utf-8")
    };
});

v1.MapGet("/transcripts/{id}/export", (string id, string? format, string? lang, int? ts, Store s, string? channel, string? watch) =>
{
    var t = id.Equals("latest", StringComparison.OrdinalIgnoreCase) ? LatestReady(s, channel, watch) : s.Snapshot(id);
    return t == null ? Results.NotFound() : ExportResult(s, t.Id, format, lang, ts);
});

v1.MapPost("/transcripts", (ApiSubmit body, Store s, JobQueue q) =>
{
    var urls = new[] { body.Url }.Concat(body.Urls ?? new()).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u!.Trim())
        .Where(u => u.Contains("youtu", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
    if (urls.Count == 0) return Results.BadRequest(new { error = "Send 'url' (or 'urls') with a YouTube link." });
    var settings = s.GetSettings();
    var ids = new List<string>();
    foreach (var raw in urls)
    {
        var url = YouTube.CanonicalUrl(raw);
        var vid = YouTube.ExtractVideoId(url);
        var existing = vid == null ? null : s.SnapshotAll(t => t.VideoId == vid).FirstOrDefault();
        if (existing != null) { ids.Add(existing.Id); continue; }
        var t = new Transcript
        {
            Url = url, VideoId = vid, Language = string.IsNullOrWhiteSpace(body.Language) ? settings.DefaultLanguage : body.Language!,
            AutoTranslateTo = body.TranslateTo ?? settings.AutoTranslateTo.ToList(), StatusMessage = "Added via API"
        };
        s.Add(t); q.Enqueue(t.Id); ids.Add(t.Id);
    }
    return Results.Ok(new { ids });
});

api.MapGet("/status", async (Store s, JobQueue q, YouTube yt, Ollama ai) =>
{
    object ollama;
    try
    {
        var models = await ai.ModelsAsync(CancellationToken.None, refresh: true);
        ollama = new { reachable = true, url = opts.OllamaUrl, active = await ai.ResolveModelAsync(CancellationToken.None), models, error = (string?)null };
    }
    catch (Exception ex) { ollama = new { reachable = false, url = opts.OllamaUrl, active = (string?)null, models = Array.Empty<OllamaModelInfo>(), error = ex.Message }; }
    var network = await Network.CheckAsync(CancellationToken.None);
    return Results.Ok(new
    {
        version = typeof(Program).Assembly.GetName().Version?.ToString(3),
        network,
        whisper = new { model = opts.WhisperModel, compute = opts.WhisperCompute },
        ollama, useAiForSections = opts.UseAiForSections, downloadVideo = opts.DownloadVideo,
        ytdlp = await yt.VersionAsync(), queue = q.Pending,
        languages = Translator.Languages, kinds = Sectioner.KindNames
    });
});

app.MapFallbackToFile("index.html");
app.Run();

static IResult ExportResult(Store s, string id, string? format, string? lang, int? ts)
{
    var t = s.Snapshot(id);
    if (t == null) return Results.NotFound();
    var settings = s.GetSettings();
    var doc = Exporter.Compose(t, lang, ts.HasValue ? ts == 1 : settings.ExportTimestamps);
    var baseName = SafeFileName($"{t.UploadDate} {doc.Title}".Trim()) + (string.IsNullOrEmpty(lang) ? "" : $" ({lang})");
    return (format ?? "docx").ToLowerInvariant() switch
    {
        "md" => Results.File(Encoding.UTF8.GetBytes(Exporter.Markdown(doc)), "text/markdown; charset=utf-8", baseName + ".md"),
        "txt" => Results.File(Encoding.UTF8.GetBytes(Exporter.PlainText(doc)), "text/plain; charset=utf-8", baseName + ".txt"),
        "html" => Results.File(Encoding.UTF8.GetBytes(Exporter.HtmlPage(doc)), "text/html; charset=utf-8", baseName + ".html"),
        "print" => Results.Content(Exporter.HtmlPage(doc).Replace("</body>", "<script>onload=()=>print()</script></body>"), "text/html; charset=utf-8"),
        "srt" => Results.File(Encoding.UTF8.GetBytes(Exporter.Srt(t.Segments)), "application/x-subrip", baseName + ".srt"),
        _ => Results.File(Exporter.Docx(doc), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", baseName + ".docx"),
    };
}

static DateTimeOffset SortKey(Transcript t) =>
    t.UploadDate != null && DateTimeOffset.TryParse(t.UploadDate, out var d) ? d.AddSeconds((t.CompletedAt ?? t.CreatedAt).TimeOfDay.TotalSeconds) : t.CompletedAt ?? t.CreatedAt;

static Transcript? LatestReady(Store s, string? channel, string? watch) =>
    s.SnapshotAll(t => t.Status == JobStatus.Ready
                       && (string.IsNullOrEmpty(channel) || (t.Channel ?? "").Contains(channel, StringComparison.OrdinalIgnoreCase))
                       && (string.IsNullOrEmpty(watch) || t.WatchId == watch))
     .OrderByDescending(SortKey).FirstOrDefault();

static string SafeFileName(string s)
{
    var bad = Path.GetInvalidFileNameChars().Concat(":?*\"<>|/\\").ToHashSet();
    var clean = new string(s.Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim();
    return clean.Length > 100 ? clean[..100] : clean.Length == 0 ? "transcript" : clean;
}

public partial class Program;
