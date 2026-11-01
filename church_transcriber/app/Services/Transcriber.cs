using System.Globalization;
using System.Text.Json;

namespace ChurchTranscriber.Services;

public sealed record TranscriptionResult(string? Language, List<Segment> Segments);

/// <summary>Speech-to-text with faster-whisper, running locally inside the add-on.</summary>
public sealed class Transcriber(AppPaths paths, AddonOptions opts)
{
    public async Task<TranscriptionResult> TranscribeAsync(string id, string audio, string language, string prompt,
        Action<double, string> progress, CancellationToken ct)
    {
        var outJson = Path.Combine(paths.JobMediaDir(id), "transcript.json");
        var args = new List<string>
        {
            "-u", paths.TranscribeScript, "--audio", audio, "--out", outJson,
            "--model", opts.WhisperModel, "--compute", opts.WhisperCompute,
            "--language", string.IsNullOrWhiteSpace(language) ? "auto" : language,
            "--models-dir", paths.ModelsDir, "--threads", opts.WhisperThreads.ToString()
        };
        if (!string.IsNullOrWhiteSpace(prompt)) { args.Add("--prompt"); args.Add(prompt); }

        progress(0, $"Loading Whisper model '{opts.WhisperModel}' (the first run downloads it)…");
        var r = await Proc.RunAsync(paths.Python, args, ct, line =>
        {
            if (line.StartsWith("PROGRESS ") && double.TryParse(line[9..], NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                progress(p, $"Transcribing… {p:P0}");
            else if (line.StartsWith("STAGE transcribing"))
                progress(0, "Transcribing…");
        }, captureStdout: false);

        if (!r.Ok || !File.Exists(outJson))
            throw new InvalidOperationException("Transcription failed: " +
                (r.StdErrTail.Split('\n').LastOrDefault(l => l.Trim().Length > 0) ?? $"exit code {r.ExitCode}"));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outJson, ct));
        var lang = doc.RootElement.TryGetProperty("language", out var l) ? l.GetString() : null;
        var segs = doc.RootElement.GetProperty("segments").EnumerateArray().Select(s => new Segment
        {
            Start = s.GetProperty("start").GetDouble(),
            End = s.GetProperty("end").GetDouble(),
            Text = s.GetProperty("text").GetString() ?? ""
        }).ToList();
        File.Delete(outJson);
        return new TranscriptionResult(lang, CleanHallucinations(segs));
    }

    /// <summary>Removes classic Whisper artefacts that appear over music or silence.</summary>
    private static List<Segment> CleanHallucinations(List<Segment> segs)
    {
        string[] junk = { "thank you for watching", "thanks for watching", "please subscribe", "subtitles by", "amara.org" };
        var result = new List<Segment>();
        foreach (var s in segs)
        {
            var low = s.Text.ToLowerInvariant();
            if (junk.Any(j => low.Contains(j)) && s.Text.Length < 60) continue;
            // Whisper sometimes loops on one line over music/silence. Keep normal chorus repeats, drop long identical runs.
            if (result.Count >= 6 && result.TakeLast(6).All(r => r.Text == s.Text)) continue;
            result.Add(s);
        }
        return result;
    }
}
