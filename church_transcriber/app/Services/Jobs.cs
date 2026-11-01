using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ChurchTranscriber.Services;

/// <summary>Queues for transcription and translation work plus per-job cancellation.</summary>
public sealed class JobQueue
{
    private readonly Channel<string> _jobs = Channel.CreateUnbounded<string>();
    private readonly Channel<(string id, string lang)> _translations = Channel.CreateUnbounded<(string, string)>();
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public ChannelReader<string> Jobs => _jobs.Reader;
    public ChannelReader<(string id, string lang)> Translations => _translations.Reader;
    public int Pending => _pending.Count;

    public void Enqueue(string id)
    {
        if (_pending.TryAdd(id, 0)) _jobs.Writer.TryWrite(id);
    }

    public void Dequeued(string id) => _pending.TryRemove(id, out _);

    public void EnqueueTranslation(string id, string lang) => _translations.Writer.TryWrite((id, lang));

    public CancellationTokenSource Begin(string key, CancellationToken stopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _running[key] = cts;
        return cts;
    }

    public void End(string key) { if (_running.TryRemove(key, out var c)) c.Dispose(); }

    public void Cancel(string id)
    {
        foreach (var k in _running.Keys.Where(k => k == id || k.StartsWith(id + ":")))
            if (_running.TryGetValue(k, out var c)) c.Cancel();
    }
}

/// <summary>Processes one service at a time: probe → (wait if scheduled/live) → download → transcribe → organise.</summary>
public sealed class TranscriptionWorker(JobQueue queue, Store store, YouTube yt, Transcriber stt, Sectioner sectioner,
    AppPaths paths, AddonOptions opts, Notifier notifier, ILogger<TranscriptionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        // Resume anything interrupted by a restart.
        foreach (var t in store.SnapshotAll(t => t.IsActive))
        {
            store.Update(t.Id, x => { x.Status = JobStatus.Queued; x.StatusMessage = "Resuming after restart"; });
            queue.Enqueue(t.Id);
        }
        foreach (var t in store.SnapshotAll(t => t.Translations.Values.Any(x => x.Status is "pending" or "running")))
            foreach (var tr in t.Translations.Values.Where(x => x.Status is "pending" or "running"))
                queue.EnqueueTranslation(t.Id, tr.Lang);

        await foreach (var id in queue.Jobs.ReadAllAsync(stopping))
        {
            queue.Dequeued(id);
            var cts = queue.Begin(id, stopping);
            try
            {
                var wasReady = store.Snapshot(id)?.Status == JobStatus.Ready;
                await ProcessAsync(id, cts.Token);
                if (!wasReady && store.Snapshot(id)?.Status == JobStatus.Ready) _ = notifier.TranscriptReadyAsync(id);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
            catch (OperationCanceledException)
            {
                store.Update(id, t => { t.Status = JobStatus.Cancelled; t.StatusMessage = "Cancelled"; });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Job {Id} failed", id);
                store.Update(id, t => { t.Status = JobStatus.Failed; t.Error = ex.Message; t.StatusMessage = "Failed"; });
                _ = notifier.FailedAsync(id, ex.Message);
            }
            finally { queue.End(id); }
        }
    }

    private async Task ProcessAsync(string id, CancellationToken ct)
    {
        var t = store.Snapshot(id);
        if (t == null || t.Status is JobStatus.Ready or JobStatus.Cancelled) return;
        var settings = store.GetSettings();

        // ---- 1. Look up the video ----
        Set(id, JobStatus.Downloading, 0, "Checking the video on YouTube…");
        var info = await yt.ProbeAsync(t.Url, ct);
        store.Update(id, x =>
        {
            x.VideoId = info.Id; x.Title = string.IsNullOrWhiteSpace(info.Title) ? x.Title : info.Title; x.Channel = info.Channel;
            x.Thumbnail = info.Thumbnail; x.Duration = info.Duration; x.UploadDate = info.UploadDate; x.LiveStatus = info.LiveStatus;
            x.ScheduledStart = info.ReleaseTime ?? x.ScheduledStart; x.Chapters = info.Chapters; x.Error = null;
        });

        // ---- 2. Scheduled premieres / live streams: wait until the recording is available ----
        if (info.LiveStatus is "is_upcoming" or "is_live" or "post_live")
        {
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset next; string msg;
            if (info.LiveStatus == "is_upcoming")
            {
                var start = info.ReleaseTime ?? now.AddMinutes(30);
                next = start > now.AddMinutes(10) ? start.AddMinutes(5) : now.AddMinutes(10);
                msg = info.ReleaseTime.HasValue ? "Scheduled - will start automatically after the service begins" : "Scheduled - start time not published yet";
            }
            else
            {
                next = now.AddMinutes(info.LiveStatus == "post_live" ? 15 : 10);
                msg = info.LiveStatus == "is_live" ? "Live now - will transcribe when the stream ends" : "Stream ended - waiting for YouTube to finish processing";
            }
            store.Update(id, x => { x.Status = JobStatus.Waiting; x.NextCheck = next; x.StatusMessage = msg; x.Progress = 0; });
            return;
        }

        // ---- 3. Download audio ----
        var lastSave = DateTime.MinValue;
        await yt.DownloadAudioAsync(t.Url, id, p =>
        {
            if ((DateTime.UtcNow - lastSave).TotalSeconds < 2) return;
            lastSave = DateTime.UtcNow;
            Set(id, JobStatus.Downloading, p, $"Downloading audio… {p:P0}");
        }, ct);
        store.Update(id, x => x.HasAudio = true);

        if (opts.DownloadVideo)
        {
            Set(id, JobStatus.Downloading, 1, "Saving the full video to your media folder…");
            var file = await yt.DownloadVideoAsync(t.Url, info.Title, info.UploadDate, ct);
            store.Update(id, x => x.VideoFile = file);
        }

        // ---- 4. Transcribe ----
        var prompt = BuildPrompt(settings, info);
        var lang = string.IsNullOrWhiteSpace(t.Language) ? settings.DefaultLanguage : t.Language;
        Set(id, JobStatus.Transcribing, 0, "Starting transcription…");
        lastSave = DateTime.MinValue;
        var result = await stt.TranscribeAsync(id, paths.AudioFile(id), lang, prompt, (p, msg) =>
        {
            if ((DateTime.UtcNow - lastSave).TotalSeconds < 3 && p < 1) return;
            lastSave = DateTime.UtcNow;
            Set(id, JobStatus.Transcribing, p, msg);
        }, ct);
        if (result.Segments.Count == 0) throw new InvalidOperationException("No speech was found in this video.");
        store.Update(id, x => { x.Segments = result.Segments; x.DetectedLanguage = result.Language; });

        // ---- 5. Organise into sections, summary and scripture list ----
        await OrganiseAsync(id, ct);

        // ---- 6. Automatic translations ----
        var auto = store.Snapshot(id)!.AutoTranslateTo;
        foreach (var l in auto.Where(l => l != result.Language)) RequestTranslation(store, queue, id, l);
    }

    public async Task OrganiseAsync(string id, CancellationToken ct)
    {
        Set(id, JobStatus.Sectioning, 0, "Organising the service into sections…");
        var t = store.Snapshot(id)!;
        var settings = store.GetSettings();
        var plan = await sectioner.PlanAsync(t, msg => Set(id, JobStatus.Sectioning, 0, msg), ct);
        var sections = DocumentBuilder.Build(t.Segments, plan.Sections, settings, t.Duration);
        var scriptures = Scripture.Find(t.Segments);
        // Use detected scripture references to enrich generic reading titles.
        foreach (var s in sections.Where(s => s.Kind == "scripture" && s.Title == Sectioner.KindNames["scripture"]))
        {
            var r = scriptures.FirstOrDefault(x => x.Time >= s.Start && x.Time < s.End);
            if (r != null) s.Title = $"Reading: {r.Reference}";
        }
        store.Update(id, x =>
        {
            x.Sections = sections; x.SummaryHtml = plan.SummaryHtml; x.Scriptures = scriptures; x.Edited = false;
            x.Status = JobStatus.Ready; x.Progress = 1; x.CompletedAt = DateTimeOffset.UtcNow;
            x.StatusMessage = plan.Method switch
            {
                "youtube-chapters" => "Ready - sections taken from the video's chapters",
                "ai" => "Ready - organised by your local AI model",
                _ => "Ready - organised by the built-in detector"
            };
        });
        log.LogInformation("Transcript {Id} ready with {N} sections ({Method})", id, sections.Count, plan.Method);
    }

    public static void RequestTranslation(Store store, JobQueue queue, string id, string lang)
    {
        store.Update(id, x =>
        {
            if (x.Translations.TryGetValue(lang, out var existing) && existing.Status is "pending" or "running") return;
            x.Translations[lang] = new Translation { Lang = lang, Status = "pending" };
        });
        queue.EnqueueTranslation(id, lang);
    }

    private static string BuildPrompt(Settings s, VideoInfo info)
    {
        // Whisper uses the prompt as context: names and terms here get spelled correctly.
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.ChurchName)) parts.Add(s.ChurchName.Trim());
        if (!string.IsNullOrWhiteSpace(s.Vocabulary)) parts.Add(s.Vocabulary.Replace('\n', ',').Trim());
        parts.Add("A church service with worship, prayer, Bible readings and a sermon.");
        var p = string.Join(". ", parts);
        return p.Length > 800 ? p[..800] : p;
    }

    private void Set(string id, JobStatus status, double progress, string message) =>
        store.Update(id, x => { x.Status = status; x.Progress = Math.Clamp(progress, 0, 1); x.StatusMessage = message; });
}

/// <summary>Translates finished transcripts section by section with the local model.</summary>
public sealed class TranslationWorker(JobQueue queue, Store store, Translator translator, Notifier notifier, ILogger<TranslationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        await foreach (var (id, lang) in queue.Translations.ReadAllAsync(stopping))
        {
            var key = $"{id}:{lang}";
            var cts = queue.Begin(key, stopping);
            try { await TranslateAsync(id, lang, cts.Token); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { store.Update(id, x => x.Translations.Remove(lang)); }
            catch (Exception ex)
            {
                log.LogError(ex, "Translation {Key} failed", key);
                store.Update(id, x => { if (x.Translations.TryGetValue(lang, out var tr)) { tr.Status = "error"; tr.Error = ex.Message; } });
            }
            finally { queue.End(key); }
        }
    }

    private async Task TranslateAsync(string id, string lang, CancellationToken ct)
    {
        var t = store.Snapshot(id);
        if (t == null || !t.Translations.TryGetValue(lang, out var tr) || tr.Status is "done" or "error") return;
        if (!translator.Available) throw new InvalidOperationException("Ollama is not configured.");
        void Progress(double p) => store.Update(id, x => { if (x.Translations.TryGetValue(lang, out var y)) { y.Status = "running"; y.Progress = p; } });

        Progress(0);
        var result = new Translation { Lang = lang, Status = "running" };
        result.Title = await translator.TranslateTextAsync(t.Title, lang, ct);
        if (!string.IsNullOrWhiteSpace(t.SummaryHtml)) result.SummaryHtml = await translator.TranslateHtmlAsync(t.SummaryHtml!, lang, ct);
        var totalChars = Math.Max(1, t.Sections.Sum(s => s.Html.Length));
        var done = 0;
        foreach (var s in t.Sections)
        {
            ct.ThrowIfCancellationRequested();
            result.Sections.Add(new SectionText
            {
                Id = s.Id,
                Title = await translator.TranslateTextAsync(s.Title, lang, ct),
                Html = await translator.TranslateHtmlAsync(s.Html, lang, ct)
            });
            done += s.Html.Length;
            Progress((double)done / totalChars);
        }
        result.Status = "done"; result.Progress = 1; result.UpdatedAt = DateTimeOffset.UtcNow;
        store.Update(id, x => x.Translations[lang] = result);
        _ = notifier.TranslationReadyAsync(id, lang);
    }
}

/// <summary>Every minute: re-checks scheduled/live services and polls watched channels for new services.</summary>
public sealed class Scheduler(JobQueue queue, Store store, YouTube yt, ILogger<Scheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                foreach (var t in store.SnapshotAll(t => t.Status == JobStatus.Waiting && (t.NextCheck ?? DateTimeOffset.MinValue) <= DateTimeOffset.UtcNow))
                {
                    store.Update(t.Id, x => { x.Status = JobStatus.Queued; x.StatusMessage = "Checking if the service is available…"; });
                    queue.Enqueue(t.Id);
                }
                foreach (var w in store.Watches().Where(w => w.Enabled && (w.LastChecked ?? DateTimeOffset.MinValue).AddMinutes(Math.Max(5, w.IntervalMinutes)) <= DateTimeOffset.UtcNow))
                    await CheckWatchAsync(w.Id, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Scheduler tick failed"); }
        } while (await timer.WaitForNextTickAsync(stopping));
    }

    public async Task<int> CheckWatchAsync(string watchId, CancellationToken ct)
    {
        var w = store.GetWatch(watchId);
        if (w == null) return 0;
        var queued = 0;
        try
        {
            var entries = await yt.ListAsync(w.Url, 10, ct);
            var filter = w.TitleFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var newOnes = entries.Where(e => !w.SeenIds.Contains(e.Id)).ToList();

            for (var i = 0; i < newOnes.Count; i++)
            {
                var e = newOnes[i];
                var matches = filter.Length == 0 || filter.Any(f => e.Title.Contains(f, StringComparison.OrdinalIgnoreCase));
                // First check: skip the back-catalogue, but keep upcoming/live services and (optionally) the latest one.
                var take = matches && (w.Initialized
                    || e.LiveStatus is "is_upcoming" or "is_live"
                    || (w.IncludeLatestExisting && newOnes.Take(i).All(x => !(filter.Length == 0 || filter.Any(f => x.Title.Contains(f, StringComparison.OrdinalIgnoreCase))))));
                if (take && !store.HasVideo(e.Id))
                {
                    var t = new Transcript
                    {
                        Url = e.Url, VideoId = e.Id, Title = e.Title, WatchId = w.Id,
                        Language = string.IsNullOrWhiteSpace(w.Language) ? "" : w.Language,
                        AutoTranslateTo = w.AutoTranslateTo.ToList(), StatusMessage = $"Found by watch '{w.Name}'"
                    };
                    store.Add(t);
                    queue.Enqueue(t.Id);
                    queued++;
                }
            }
            store.UpdateWatch(w.Id, x =>
            {
                foreach (var e in entries) x.SeenIds.Add(e.Id);
                if (x.SeenIds.Count > 500) x.SeenIds = x.SeenIds.TakeLast(300).ToHashSet();
                x.Initialized = true; x.LastChecked = DateTimeOffset.UtcNow; x.LastError = null; x.QueuedCount += queued;
            });
            if (queued > 0) log.LogInformation("Watch {Name} queued {N} new services", w.Name, queued);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            store.UpdateWatch(w.Id, x => { x.LastChecked = DateTimeOffset.UtcNow; x.LastError = ex.Message; });
            log.LogWarning("Watch {Name} check failed: {Msg}", w.Name, ex.Message);
        }
        return queued;
    }
}

/// <summary>Keeps yt-dlp current at start-up - YouTube changes frequently.</summary>
public sealed class StartupTasks(AddonOptions opts, AppPaths paths, ILogger<StartupTasks> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opts.UpdateYtdlpOnStart || !File.Exists(paths.Pip)) return;
        log.LogInformation("Updating yt-dlp…");
        var r = await Proc.RunAsync(paths.Pip, new[] { "install", "--no-cache-dir", "-U", "yt-dlp[default]" }, ct, captureStdout: false);
        log.LogInformation(r.Ok ? "yt-dlp is up to date" : "yt-dlp update failed: {Err}", r.StdErrTail);
    }
}
