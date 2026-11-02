using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChurchTranscriber.Services;

/// <summary>Thread-safe JSON-file persistence for transcripts, channel watches and settings.</summary>
public sealed class Store
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly object _lock = new();
    private readonly Dictionary<string, Transcript> _transcripts = new();
    private readonly Dictionary<string, Watch> _watches = new();
    private Settings _settings = new();
    private readonly AppPaths _paths;
    private readonly ILogger<Store> _log;

    private string WatchesFile => Path.Combine(_paths.DataDir, "watches.json");
    private string SettingsFile => Path.Combine(_paths.DataDir, "settings.json");

    public Store(AppPaths paths, ILogger<Store> log)
    {
        _paths = paths;
        _log = log;
        foreach (var f in Directory.EnumerateFiles(paths.TranscriptsDir, "*.json"))
        {
            try
            {
                var t = JsonSerializer.Deserialize<Transcript>(File.ReadAllText(f), Json);
                if (t != null) _transcripts[t.Id] = t;
            }
            catch (Exception ex) { _log.LogError(ex, "Could not load {File}", f); }
        }
        try
        {
            if (File.Exists(WatchesFile))
                foreach (var w in JsonSerializer.Deserialize<List<Watch>>(File.ReadAllText(WatchesFile), Json) ?? new())
                    _watches[w.Id] = w;
            if (File.Exists(SettingsFile))
                _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile), Json) ?? new();
        }
        catch (Exception ex) { _log.LogError(ex, "Could not load watches/settings"); }
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            _settings.ApiKey = NewApiKey();
            WriteAtomic(SettingsFile, JsonSerializer.Serialize(_settings, Json));
        }
        _log.LogInformation("Loaded {T} transcripts and {W} channel watches", _transcripts.Count, _watches.Count);
    }

    // ---------- Transcripts ----------
    public void Add(Transcript t)
    {
        lock (_lock) { _transcripts[t.Id] = t; SaveTranscript(t); }
    }

    public Transcript? Snapshot(string id)
    {
        lock (_lock)
            return _transcripts.TryGetValue(id, out var t) ? Clone(t) : null;
    }

    public List<Transcript> SnapshotAll(Func<Transcript, bool>? filter = null)
    {
        lock (_lock)
            return _transcripts.Values.Where(filter ?? (_ => true)).Select(Clone).ToList();
    }

    public List<object> Summaries()
    {
        lock (_lock)
            return _transcripts.Values.OrderByDescending(t => t.ScheduledStart ?? t.CreatedAt).Select(t => (object)new
            {
                t.Id, t.Url, t.VideoId, t.Title, t.Channel, t.Thumbnail, t.Duration, t.UploadDate, t.LiveStatus,
                t.ScheduledStart, t.Status, t.Progress, t.StatusMessage, t.Error, t.RetryCount, t.CreatedAt, t.UpdatedAt,
                t.CompletedAt, t.Language, t.DetectedLanguage, t.WatchId, t.NextCheck,
                SectionCount = t.Sections.Count,
                Translations = t.Translations.Values.Select(x => new { x.Lang, x.Status, x.Progress }).ToList()
            }).ToList();
    }

    public bool Update(string id, Action<Transcript> change, bool save = true)
    {
        lock (_lock)
        {
            if (!_transcripts.TryGetValue(id, out var t)) return false;
            change(t);
            t.UpdatedAt = DateTimeOffset.UtcNow;
            if (save) SaveTranscript(t);
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (!_transcripts.Remove(id)) return false;
            TryDelete(Path.Combine(_paths.TranscriptsDir, id + ".json"));
        }
        try { if (Directory.Exists(_paths.JobMediaDir(id))) Directory.Delete(_paths.JobMediaDir(id), true); } catch { }
        return true;
    }

    public bool HasVideo(string videoId)
    {
        lock (_lock) return _transcripts.Values.Any(t => t.VideoId == videoId);
    }

    // ---------- Watches ----------
    public List<Watch> Watches()
    {
        lock (_lock) return _watches.Values.OrderBy(w => w.CreatedAt).Select(Clone).ToList();
    }

    public Watch? GetWatch(string id)
    {
        lock (_lock) return _watches.TryGetValue(id, out var w) ? Clone(w) : null;
    }

    public void UpsertWatch(Watch w)
    {
        lock (_lock) { _watches[w.Id] = w; SaveWatches(); }
    }

    public bool UpdateWatch(string id, Action<Watch> change)
    {
        lock (_lock)
        {
            if (!_watches.TryGetValue(id, out var w)) return false;
            change(w);
            SaveWatches();
            return true;
        }
    }

    public bool DeleteWatch(string id)
    {
        lock (_lock) { var ok = _watches.Remove(id); if (ok) SaveWatches(); return ok; }
    }

    // ---------- Settings ----------
    public Settings GetSettings()
    {
        lock (_lock) return Clone(_settings);
    }

    public void SaveSettings(Settings s)
    {
        lock (_lock) { _settings = s; WriteAtomic(SettingsFile, JsonSerializer.Serialize(s, Json)); }
    }

    // ---------- helpers ----------
    private void SaveTranscript(Transcript t) =>
        WriteAtomic(Path.Combine(_paths.TranscriptsDir, t.Id + ".json"), JsonSerializer.Serialize(t, Json));

    private void SaveWatches() => WriteAtomic(WatchesFile, JsonSerializer.Serialize(_watches.Values.ToList(), Json));

    private void WriteAtomic(string path, string content)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, true);
        }
        catch (Exception ex) { _log.LogError(ex, "Failed to save {Path}", path); }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    public static string NewApiKey() => "ct_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public static T Clone<T>(T obj) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(obj, Json), Json)!;
}
