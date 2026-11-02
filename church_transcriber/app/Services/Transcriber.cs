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
            else if (line.StartsWith("STAGE downloading model"))
                progress(0, "Downloading the Whisper model (first run only, about 1.6 GB)…");
            else if (line.StartsWith("STAGE decoding"))
                progress(0, "Preparing audio…");
        }, captureStdout: false);

        if (!r.Ok || !File.Exists(outJson))
            throw new ToolException(ExplainFailure(r), $"exit code {r.ExitCode}\n{r.StdErrTail}");

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

    private string ExplainFailure(ProcResult r)
    {
        var lines = r.StdErrTail.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        var last = lines.LastOrDefault() ?? "";
        // Judge by the actual error (the last traceback line), not warnings printed earlier in the log.
        var t = (lines.LastOrDefault(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*[\w.]+(Error|Exception|Exit)\b")) ?? last).ToLowerInvariant();
        var all = r.StdErrTail.ToLowerInvariant();
        if (r.ExitCode is 137 or -9 || all.Contains("killed") || t.Contains("memoryerror") || t.Contains("out of memory") || t.Contains("bad_alloc"))
            return $"Whisper ran out of memory. Give the Home Assistant VM more RAM (6 GB or more for '{opts.WhisperModel}'), or set whisper_model to 'medium' or 'small' on the add-on's Configuration tab.";
        if (r.ExitCode is 132 or -4 || t.Contains("illegal instruction"))
            return "This CPU doesn't expose the instructions Whisper needs (AVX/AVX2). In ESXi, set the VM's CPU compatibility / EVC mode to expose AVX2 to the guest, then restart the VM.";
        if (all.Contains("stage downloading model") && (t.Contains("connection") || t.Contains("localentrynotfound") || t.Contains("timeout") || t.Contains("resolve")))
            return "Couldn't download the Whisper model (the first run downloads it from huggingface.co). Check the Home Assistant machine's internet access, then press Try again.";
        if (all.Contains("no space left")) return "The Home Assistant disk is full - free some space and try again.";
        if (r.ExitCode == -1) return "Couldn't start Whisper: " + last;
        return "Transcription failed: " + last;
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
