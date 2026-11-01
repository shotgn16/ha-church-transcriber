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
        if (!string.IsNullOrWhiteSpace(opts.CookiesFile) && File.Exists(opts.CookiesFile))
        {
            yield return "--cookies";
            yield return opts.CookiesFile!;
        }
    }

    public async Task<VideoInfo> ProbeAsync(string url, CancellationToken ct)
    {
        var args = CommonArgs().Concat(new[] { "-J", "--no-playlist", "--ignore-no-formats-error", url });
        var r = await Proc.RunAsync(paths.YtDlp, args, ct);
        if (!r.Ok || string.IsNullOrWhiteSpace(r.StdOut))
            throw new InvalidOperationException("Could not read the video from YouTube: " + LastError(r.StdErrTail));

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
        if (!r.Ok) throw new InvalidOperationException("Download failed: " + LastError(r.StdErrTail));

        var src = Directory.EnumerateFiles(dir, "source.*").FirstOrDefault(f => !f.EndsWith(".part"))
                  ?? throw new InvalidOperationException("Download finished but no audio file was found.");
        var outFile = paths.AudioFile(id);
        var conv = await Proc.RunAsync(paths.Ffmpeg, new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-i", src, "-vn", "-ac", "1", "-ar", "16000",
            "-c:a", "aac", "-b:a", "48k", "-movflags", "+faststart", outFile
        }, ct, captureStdout: false);
        if (!conv.Ok) throw new InvalidOperationException("Audio conversion failed: " + LastError(conv.StdErrTail));
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
