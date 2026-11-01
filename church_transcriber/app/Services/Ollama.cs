using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChurchTranscriber.Services;

public sealed record OllamaModelInfo(string Name, string? Size, string? Family);

/// <summary>Client for the local Ollama server (native /api/chat so the context window can be set explicitly).</summary>
public sealed class Ollama(AddonOptions opts, Store store, IHttpClientFactory http, ILogger<Ollama> log)
{
    private List<OllamaModelInfo>? _models;
    private DateTimeOffset _modelsAt;
    private string Base => opts.OllamaUrl.TrimEnd('/').Replace("/v1", "");

    public bool Configured => !string.IsNullOrWhiteSpace(opts.OllamaUrl);

    public async Task<List<OllamaModelInfo>> ModelsAsync(CancellationToken ct, bool refresh = false)
    {
        if (!refresh && _models != null && DateTimeOffset.UtcNow - _modelsAt < TimeSpan.FromMinutes(5)) return _models;
        var client = http.CreateClient("ollama-short");
        using var res = await client.GetAsync(Base + "/api/tags", ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        _models = doc.RootElement.GetProperty("models").EnumerateArray().Select(m =>
        {
            var details = m.TryGetProperty("details", out var d) ? d : default;
            string? P(string n) => details.ValueKind == JsonValueKind.Object && details.TryGetProperty(n, out var v) ? v.GetString() : null;
            return new OllamaModelInfo(m.GetProperty("name").GetString() ?? "", P("parameter_size"), P("family"));
        }).ToList();
        _modelsAt = DateTimeOffset.UtcNow;
        return _models;
    }

    /// <summary>Settings page choice → add-on option → first Qwen instruct model found → first model.</summary>
    public async Task<string> ResolveModelAsync(CancellationToken ct)
    {
        var chosen = store.GetSettings().OllamaModel;
        if (!string.IsNullOrWhiteSpace(chosen)) return chosen!;
        if (!string.IsNullOrWhiteSpace(opts.OllamaModel)) return opts.OllamaModel!;
        var models = await ModelsAsync(ct);
        var pick = models.FirstOrDefault(m => m.Name.Contains("qwen", StringComparison.OrdinalIgnoreCase) && m.Name.Contains("instruct", StringComparison.OrdinalIgnoreCase))
                   ?? models.FirstOrDefault(m => m.Name.Contains("qwen", StringComparison.OrdinalIgnoreCase) && !m.Name.Contains("coder", StringComparison.OrdinalIgnoreCase))
                   ?? models.FirstOrDefault()
                   ?? throw new InvalidOperationException("Your Ollama server has no models installed.");
        return pick.Name;
    }

    public async Task<string> ChatAsync(string system, string user, bool json, CancellationToken ct, double temperature = 0.2)
    {
        var model = await ResolveModelAsync(ct);
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["keep_alive"] = "15m",
            ["options"] = new JsonObject { ["temperature"] = temperature, ["num_ctx"] = opts.OllamaContext },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }
            }
        };
        if (json) body["format"] = "json";

        var client = http.CreateClient("ollama");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var res = await client.PostAsync(Base + "/api/chat",
                    new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode)
                    throw new HttpRequestException($"Ollama error {(int)res.StatusCode}: {(text.Length > 300 ? text[..300] : text)}");
                using var doc = JsonDocument.Parse(text);
                var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
                return StripThinking(content);
            }
            catch (HttpRequestException ex) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                log.LogWarning("Ollama request failed (attempt {A}): {Msg}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10 * attempt), ct);
            }
        }
    }

    public async Task<JsonDocument> ChatJsonAsync(string system, string user, CancellationToken ct)
    {
        var raw = await ChatAsync(system, user, true, ct);
        try { return JsonDocument.Parse(Unfence(raw)); }
        catch (JsonException)
        {
            // Small models occasionally add prose; extract the outermost JSON object.
            var a = raw.IndexOf('{'); var b = raw.LastIndexOf('}');
            if (a >= 0 && b > a) return JsonDocument.Parse(raw[a..(b + 1)]);
            throw new InvalidOperationException("The AI model did not return valid JSON.");
        }
    }

    /// <summary>Qwen3 "thinking" models emit &lt;think&gt; blocks; drop them.</summary>
    private static string StripThinking(string s)
    {
        var end = s.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        return end >= 0 ? s[(end + 8)..].Trim() : s.Trim();
    }

    public static string Unfence(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```")) return s;
        var firstNl = s.IndexOf('\n');
        var lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
        return firstNl > 0 && lastFence > firstNl ? s[(firstNl + 1)..lastFence].Trim() : s.Trim('`');
    }
}
