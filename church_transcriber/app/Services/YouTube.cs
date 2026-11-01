using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChurchTranscriber.Services;

public sealed class VideoInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Channel { get; set; }
    public string? Thumbnail { get; set; }
    public double Duration { get; set; }
    public string? UploadDate { get; set; }
    public string? LiveStatus { get; set; }   // is_upcoming | is_live | post_live | was_live | not_live
    public DateTimeOffset? ReleaseTime { get; set; }
    public string? WebpageUrl { get; set; }
    public List<Chapter> Chapters { get; set; } = new();
}

public sealed record PlaylistEntry(string Id, string Title, string Url, string? LiveStatus);

/// <summary>Wraps yt-dlp for metadata, audio/video downloads and channel listings.</summary>
public sealed partial class YouTube(AppPaths paths, AddonOptions opts, ILogger<YouTube> log)
{
    private IEnumerable<string> CommonArgs()
    {
        yield return "--no-warnings";
        yield return "--ignore-config";
        foreach (var a in new[] { "--socket-timeout", "30", "--retries", "10", "--fragment-retries", "10", "--extractor-retries", "3" })
            yield return a;
        if (!string.IsNullOrWhiteSpace(opts.CookiesFile) && File.Exists(opts.CookiesFile))
        {
            yield return "--cookies";
            yield return opts.CookiesFile!;
        }
    }

    public async Task<VideoInfo> ProbeAsync(string url, CancellationToken ct)
    {
        var args = CommonArgs().Concat(new[] { "-J", "--no-playlist", "--ignore-no-formats-error", url });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        ProcResult r;
        try { r = await Proc.RunAsync(paths.YtDlp, args, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ToolException("YouTube didn't respond within 5 minutes. Check the Home Assistant machine's internet connection and try again.", "yt-dlp -J timed out");
        }
        if (!r.Ok || string.IsNullOrWhiteSpace(r.StdOut))
            throw new ToolException("Couldn't read the video from YouTube. " + Explain(r.StdErrTail), r.StdErrTail);

        using var doc = JsonDocument.Parse(r.StdOut);
        var root = doc.RootElement;
        var info = new VideoInfo
        {
            Id = Str(root, "id") ?? "",
            Title = Str(root, "title") ?? "Untitled",
            Channel = Str(root, "channel") ?? Str(root, "uploader"),
            Thumbnail = Str(root, "thumbnail"),
            Duration = Num(root, "duration") ?? 0,
            LiveStatus = Str(root, "live_status"),
            WebpageUrl = Str(root, "webpage_url"),
        };
        var ud = Str(root, "upload_date") ?? Str(root, "release_date");
        if (ud is { Length: 8 } && DateTime.TryParseExact(ud, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            info.UploadDate = d.ToString("yyyy-MM-dd");
        var rel = Num(root, "release_timestamp") ?? (info.LiveStatus == "is_upcoming" ? Num(root, "timestamp") : null);
        if (rel is > 0) info.ReleaseTime = DateTimeOffset.FromUnixTimeSeconds((long)rel.Value);

        if (root.TryGetProperty("chapters", out var ch) && ch.ValueKind == JsonValueKind.Array)
            foreach (var c in ch.EnumerateArray())
                info.Chapters.Add(new Chapter
                {
                    Start = Num(c, "start_time") ?? 0,
                    End = Num(c, "end_time") ?? 0,
                    Title = Str(c, "title") ?? ""
                });
        return info;
    }

    /// <summary>Downloads the best audio and converts it to a compact mono AAC file playable on every device.</summary>
    public async Task DownloadAudioAsync(string url, string id, Action<double> progress, CancellationToken ct)
    {
        var dir = paths.JobMediaDir(id);
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "source.*")) File.Delete(old);

        var args = CommonArgs().Concat(new[]
        {
            "-f", "bestaudio/best", "--no-playlist", "--newline",
            "--progress-template", "download:DL %(progress._percent_str)s",
            "-o", Path.Combine(dir, "source.%(ext)s"), url
        });
        var r = await Proc.RunAsync(paths.YtDlp, args, ct, line =>
        {
            var m = PercentRx().Match(line);
            if (line.StartsWith("DL") && m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                progress(pct / 100.0);
        }, captureStdout: false);
        if (!r.Ok) throw new ToolException("The download failed. " + Explain(r.StdErrTail), r.StdErrTail);

        var src = Directory.EnumerateFiles(dir, "source.*").FirstOrDefault(f => !f.EndsWith(".part"))
                  ?? throw new ToolException("The download finished but no audio file was found.", string.Join('\n', Directory.GetFiles(dir)));
        var outFile = paths.AudioFile(id);
        var conv = await Proc.RunAsync(paths.Ffmpeg, new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-i", src, "-vn", "-ac", "1", "-ar", "16000",
            "-c:a", "aac", "-b:a", "48k", "-movflags", "+faststart", outFile
        }, ct, captureStdout: false);
        if (!conv.Ok) throw new ToolException("Converting the audio failed.", conv.StdErrTail);
        File.Delete(src);
    }

    /// <summary>Optionally saves the full video to the shared media folder.</summary>
    public async Task<string?> DownloadVideoAsync(string url, string title, string? date, CancellationToken ct)
    {
        var dir = Path.Combine(opts.MediaFolder, "videos");
        Directory.CreateDirectory(dir);
        var name = $"{date ?? DateTime.Now.ToString("yyyy-MM-dd")} - {SafeName(title)}";
        var args = CommonArgs().Concat(new[]
        {
            "-f", "bv*[height<=1080]+ba/b", "--merge-output-format", "mp4", "--no-playlist",
            "-o", Path.Combine(dir, name + ".%(ext)s"), url
        });
        var r = await Proc.RunAsync(paths.YtDlp, args, ct, captureStdout: false);
        if (!r.Ok) { log.LogWarning("Video download failed: {Err}", LastError(r.StdErrTail)); return null; }
        return Directory.EnumerateFiles(dir, name + ".*").FirstOrDefault();
    }

    /// <summary>Lists the most recent entries of a channel tab or playlist.</summary>
    public async Task<List<PlaylistEntry>> ListAsync(string url, int max, CancellationToken ct)
    {
        var args = CommonArgs().Concat(new[] { "--flat-playlist", "-J", "--playlist-end", max.ToString(), url });
        var r = await Proc.RunAsync(paths.YtDlp, args, ct);
        if (!r.Ok || string.IsNullOrWhiteSpace(r.StdOut))
            throw new InvalidOperationException("Could not read the channel: " + LastError(r.StdErrTail));
        using var doc = JsonDocument.Parse(r.StdOut);
        var list = new List<PlaylistEntry>();
        if (doc.RootElement.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            foreach (var e in entries.EnumerateArray())
            {
                var id = Str(e, "id");
                if (string.IsNullOrEmpty(id) || id.Length != 11) continue; // skip nested playlists/shelves
                list.Add(new PlaylistEntry(id, Str(e, "title") ?? "", $"https://www.youtube.com/watch?v={id}", Str(e, "live_status")));
            }
        return list;
    }

    public async Task<string> VersionAsync()
    {
        try
        {
            var r = await Proc.RunAsync(paths.YtDlp, new[] { "--version" }, CancellationToken.None);
            return r.Ok ? r.StdOut.Trim() : "not available";
        }
        catch { return "not available"; }
    }

    /// <summary>Turns a bare channel URL into its live-streams tab, where church services usually live.</summary>
    public static string NormalizeWatchUrl(string url, bool useStreamsTab)
    {
        url = url.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        if (!useStreamsTab) return url;
        var u = new Uri(url);
        var path = u.AbsolutePath.TrimEnd('/');
        var isChannelRoot = ChannelRootRx().IsMatch(path);
        return isChannelRoot ? $"{u.Scheme}://{u.Host}{path}/streams" : url;
    }

    /// <summary>Turns mobile/share/tracking links (m.youtube.com, youtu.be, /live/, ?pp=, ?si=) into a clean watch URL.</summary>
    public static string CanonicalUrl(string url)
    {
        url = url.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        var id = ExtractVideoId(url);
        return id == null ? url : $"https://www.youtube.com/watch?v={id}";
    }

    /// <summary>Plain-English explanation of common yt-dlp failures.</summary>
    public static string Explain(string tail)
    {
        var t = tail.ToLowerInvariant();
        var last = LastError(tail);
        if (t.Contains("confirm you") && t.Contains("bot") || t.Contains("sign in to confirm"))
            return "YouTube asked to confirm this isn't a bot. Export YouTube cookies to /share/church_transcriber/cookies.txt (see Documentation → Troubleshooting), then try again.";
        if (t.Contains("private video")) return "This video is private.";
        if (t.Contains("members-only") || t.Contains("join this channel")) return "This video is for channel members only.";
        if (t.Contains("age") && t.Contains("restrict") || t.Contains("inappropriate for some users")) return "This video is age-restricted - add a cookies file from a signed-in account.";
        if (t.Contains("video unavailable") || t.Contains("has been removed")) return "YouTube says this video is unavailable.";
        if (t.Contains("javascript runtime") || t.Contains("n challenge") || t.Contains("signature") && t.Contains("fail"))
            return "The downloader couldn't solve YouTube's player challenge. Restart the add-on (it updates the downloader on start) and try again. Detail: " + last;
        if (t.Contains("http error 403") || t.Contains("403: forbidden"))
            return "YouTube refused the download (HTTP 403). Restart the add-on to update the downloader, then press Try again. Detail: " + last;
        if (t.Contains("http error 429") || t.Contains("too many requests"))
            return "YouTube is rate-limiting this connection. Wait an hour, or add a cookies file, then try again.";
        if (t.Contains("name resolution") || t.Contains("timed out") || t.Contains("unable to connect") || t.Contains("network is unreachable"))
            return "Couldn't connect to YouTube - check the Home Assistant machine's internet/DNS. Detail: " + last;
        if (t.Contains("requested format is not available")) return "YouTube offered no downloadable audio for this video yet - if it was just streamed, try again later.";
        return "Detail: " + last;
    }

    public static string? ExtractVideoId(string url)
    {
        var m = VideoIdRx().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { ':', '?', '*', '"', '<', '>', '|' }).ToHashSet();
        var clean = new string(s.Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim();
        return clean.Length > 120 ? clean[..120] : clean;
    }

    private static string LastError(string tail)
    {
        var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var err = lines.LastOrDefault(l => l.Contains("ERROR")) ?? lines.LastOrDefault() ?? "unknown error";
        return err.Replace("ERROR: ", "").Trim();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    [GeneratedRegex(@"([\d.]+)%")] private static partial Regex PercentRx();
    [GeneratedRegex(@"^/(@[^/]+|channel/[^/]+|c/[^/]+|user/[^/]+)$")] private static partial Regex ChannelRootRx();
    [GeneratedRegex(@"(?:v=|youtu\.be/|/live/|/shorts/|/embed/)([A-Za-z0-9_-]{11})")] private static partial Regex VideoIdRx();
}
