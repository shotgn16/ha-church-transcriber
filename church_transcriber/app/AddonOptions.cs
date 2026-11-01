using System.Text.Json;

namespace ChurchTranscriber;

/// <summary>Options set on the add-on's Configuration tab in Home Assistant (/data/options.json).</summary>
public sealed class AddonOptions
{
    public string WhisperModel { get; set; } = "large-v3-turbo";
    public string WhisperCompute { get; set; } = "int8";
    public int WhisperThreads { get; set; }
    public string OllamaUrl { get; set; } = "http://homeassistant.local:11434";
    public string? OllamaModel { get; set; }
    public int OllamaContext { get; set; } = 16384;
    public bool UseAiForSections { get; set; } = true;
    public string? AccessPassword { get; set; }
    public bool DownloadVideo { get; set; }
    public string MediaFolder { get; set; } = "/share/church_transcriber";
    public string? CookiesFile { get; set; }
    public bool UpdateYtdlpOnStart { get; set; } = true;

    public static AddonOptions Load(string path, ILogger log)
    {
        try
        {
            if (File.Exists(path))
            {
                var o = JsonSerializer.Deserialize<AddonOptions>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true });
                if (o != null) return o;
            }
            else log.LogWarning("No options file at {Path}; using defaults", path);
        }
        catch (Exception ex) { log.LogError(ex, "Could not read add-on options"); }
        return new AddonOptions();
    }
}

/// <summary>Filesystem locations and external tool names.</summary>
public sealed class AppPaths
{
    public string DataDir { get; }
    public string TranscriptsDir => Path.Combine(DataDir, "transcripts");
    public string MediaDir => Path.Combine(DataDir, "media");
    public string ModelsDir => Path.Combine(DataDir, "models");
    public string OptionsFile { get; }
    public string YtDlp { get; } = Environment.GetEnvironmentVariable("YTDLP") ?? "yt-dlp";
    public string Python { get; } = Environment.GetEnvironmentVariable("PYTHON") ?? (File.Exists("/opt/venv/bin/python") ? "/opt/venv/bin/python" : "python3");
    public string Pip { get; } = File.Exists("/opt/venv/bin/pip") ? "/opt/venv/bin/pip" : "pip3";
    public string Ffmpeg { get; } = Environment.GetEnvironmentVariable("FFMPEG") ?? "ffmpeg";
    public string TranscribeScript => Path.Combine(AppContext.BaseDirectory, "scripts", "transcribe.py");

    public AppPaths()
    {
        DataDir = Environment.GetEnvironmentVariable("DATA_DIR")
                  ?? (Directory.Exists("/data") ? "/data" : Path.Combine(AppContext.BaseDirectory, "data"));
        OptionsFile = Environment.GetEnvironmentVariable("OPTIONS_FILE") ?? Path.Combine(DataDir, "options.json");
        Directory.CreateDirectory(TranscriptsDir);
        Directory.CreateDirectory(MediaDir);
        Directory.CreateDirectory(ModelsDir);
    }

    public string JobMediaDir(string id) => Path.Combine(MediaDir, id);
    public string AudioFile(string id) => Path.Combine(JobMediaDir(id), "audio.m4a");
}
