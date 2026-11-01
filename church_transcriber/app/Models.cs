using System.Text.Json.Serialization;

namespace ChurchTranscriber;

[JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
public enum JobStatus { Queued, Waiting, Downloading, Transcribing, Sectioning, Ready, Failed, Cancelled }

public sealed class Transcript
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Url { get; set; } = "";
    public string? VideoId { get; set; }
    public string Title { get; set; } = "New service";
    public string? Channel { get; set; }
    public string? Thumbnail { get; set; }
    public double Duration { get; set; }
    public string? UploadDate { get; set; }
    public string? LiveStatus { get; set; }
    public DateTimeOffset? ScheduledStart { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;
    public double Progress { get; set; }
    public string? StatusMessage { get; set; }
    public string? Error { get; set; }
    public string? ErrorDetails { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? NextCheck { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public string Language { get; set; } = "auto";
    public string? DetectedLanguage { get; set; }
    public List<string> AutoTranslateTo { get; set; } = new();
    public string? WatchId { get; set; }

    public bool HasAudio { get; set; }
    public string? VideoFile { get; set; }

    public List<Chapter> Chapters { get; set; } = new();
    public List<Segment> Segments { get; set; } = new();
    public List<Section> Sections { get; set; } = new();
    public string? SummaryHtml { get; set; }
    public List<ScriptureRef> Scriptures { get; set; } = new();
    public Dictionary<string, Translation> Translations { get; set; } = new();
    public bool Edited { get; set; }

    public bool IsActive => Status is JobStatus.Queued or JobStatus.Downloading or JobStatus.Transcribing or JobStatus.Sectioning;
}

public sealed class Chapter
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Title { get; set; } = "";
}

public sealed class Segment
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
}

public sealed class Section
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "other";
    public double Start { get; set; }
    public double End { get; set; }
    public string Html { get; set; } = "";
    public string? Summary { get; set; }
}

public sealed class SectionText
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Html { get; set; } = "";
}

public sealed class Translation
{
    public string Lang { get; set; } = "";
    public string Status { get; set; } = "pending"; // pending | running | done | error
    public double Progress { get; set; }
    public string? Error { get; set; }
    public string Title { get; set; } = "";
    public string? SummaryHtml { get; set; }
    public List<SectionText> Sections { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ScriptureRef
{
    public string Reference { get; set; } = "";
    public double Time { get; set; }
}

public sealed class Watch
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string TitleFilter { get; set; } = "";
    public string Language { get; set; } = "";
    public List<string> AutoTranslateTo { get; set; } = new();
    public int IntervalMinutes { get; set; } = 30;
    public bool Enabled { get; set; } = true;
    public bool IncludeLatestExisting { get; set; }
    public DateTimeOffset? LastChecked { get; set; }
    public string? LastError { get; set; }
    public int QueuedCount { get; set; }
    public bool Initialized { get; set; }
    public HashSet<string> SeenIds { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Settings
{
    public string DefaultLanguage { get; set; } = "en";
    public string Vocabulary { get; set; } = "";
    public List<string> AutoTranslateTo { get; set; } = new();
    public bool OmitWorshipLyrics { get; set; }
    public bool ExportTimestamps { get; set; } = true;
    public string ChurchName { get; set; } = "";
    public string? OllamaModel { get; set; }

    // ---- Automation API & notifications ----
    public string ApiKey { get; set; } = "";
    public bool HaEvents { get; set; } = true;
    public string WebhookUrl { get; set; } = "";
    /// <summary>Query-string selection of what notifications carry (see ContentSelection).</summary>
    public string NotifySelection { get; set; } = "parts=summary,scriptures,outline";
}

// ---- Request bodies ----
public sealed record NewJobRequest(string? Urls, string? Language, List<string>? TranslateTo);
public sealed record TranscriptEdit(string? Title, string? SummaryHtml, List<Section>? Sections);
public sealed record TranslationEdit(string? Title, string? SummaryHtml, List<SectionText>? Sections);
public sealed record TranslateRequest(string Lang);
public sealed record AutomationEdit(bool? HaEvents, string? WebhookUrl, string? NotifySelection);
public sealed record ApiSubmit(string? Url, List<string>? Urls, string? Language,
    [property: System.Text.Json.Serialization.JsonPropertyName("translate_to")] List<string>? TranslateTo);
public sealed record WatchRequest(string? Name, string? Url, string? TitleFilter, string? Language, List<string>? TranslateTo,
    int? IntervalMinutes, bool? UseStreamsTab, bool? IncludeLatestExisting, bool? Enabled);
