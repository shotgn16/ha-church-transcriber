using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChurchTranscriber.Services;

/// <summary>Talks to the Home Assistant Supervisor (events, own hostname). Only available when running as an add-on.</summary>
public sealed class Supervisor(IHttpClientFactory http, ILogger<Supervisor> log)
{
    private static readonly string? Token = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN");
    private string? _hostname;
    public bool Available => !string.IsNullOrEmpty(Token);

    private HttpRequestMessage Req(HttpMethod m, string path, JsonNode? body = null)
    {
        var r = new HttpRequestMessage(m, (Environment.GetEnvironmentVariable("SUPERVISOR_URL") ?? "http://supervisor") + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (body != null) r.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return r;
    }

    public async Task<bool> FireEventAsync(string eventType, JsonNode data, CancellationToken ct)
    {
        if (!Available) return false;
        try
        {
            using var res = await http.CreateClient("supervisor").SendAsync(Req(HttpMethod.Post, $"/core/api/events/{eventType}", data), ct);
            if (!res.IsSuccessStatusCode) log.LogWarning("Home Assistant event {Event} failed: {Code}", eventType, (int)res.StatusCode);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) { log.LogWarning("Home Assistant event {Event} failed: {Msg}", eventType, ex.Message); return false; }
    }

    /// <summary>The add-on's hostname on Home Assistant's internal network (what automations should call).</summary>
    public async Task<string> HostnameAsync(CancellationToken ct)
    {
        if (_hostname != null) return _hostname;
        if (Available)
        {
            try
            {
                using var res = await http.CreateClient("supervisor").SendAsync(Req(HttpMethod.Get, "/addons/self/info"), ct);
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.GetProperty("data").TryGetProperty("hostname", out var h) && h.GetString() is { Length: > 0 } name)
                    return _hostname = name;
            }
            catch (Exception ex) { log.LogDebug("Supervisor info failed: {Msg}", ex.Message); }
        }
        return _hostname = Environment.MachineName.ToLowerInvariant();
    }
}

/// <summary>Announces finished services/translations to Home Assistant (events) and an optional webhook.</summary>
public sealed class Notifier(Store store, Supervisor supervisor, IHttpClientFactory http, ILogger<Notifier> log)
{
    public const string ReadyEvent = "church_transcriber_transcript_ready";
    public const string TranslationEvent = "church_transcriber_translation_ready";
    public const string FailedEvent = "church_transcriber_failed";

    public async Task<string> InternalBaseUrlAsync(CancellationToken ct) => $"http://{await supervisor.HostnameAsync(ct)}:8099";

    public Task TranscriptReadyAsync(string id) => SendAsync(ReadyEvent, id, null);
    public Task TranslationReadyAsync(string id, string lang) => SendAsync(TranslationEvent, id, lang);

    public async Task FailedAsync(string id, string error)
    {
        var t = store.Snapshot(id);
        if (t == null) return;
        var data = new JsonObject { ["id"] = t.Id, ["title"] = t.Title, ["url"] = t.Url, ["error"] = error, ["event"] = FailedEvent };
        await DeliverAsync(FailedEvent, data);
    }

    /// <summary>Builds the payload exactly as the API would for the saved notification selection.</summary>
    public async Task<JsonObject?> PayloadAsync(string eventType, string id, string? lang, CancellationToken ct)
    {
        var t = store.Snapshot(id);
        if (t == null) return null;
        var settings = store.GetSettings();
        var query = settings.NotifySelection;
        if (lang != null) query = string.Join('&', new[] { query, "lang=" + lang }.Where(x => !string.IsNullOrEmpty(x)));
        var sel = ContentSelection.Parse(query);
        var data = ContentApi.Json(t, sel, await InternalBaseUrlAsync(ct));
        data["event"] = eventType;
        return data;
    }

    private async Task SendAsync(string eventType, string id, string? lang)
    {
        try
        {
            var data = await PayloadAsync(eventType, id, lang, CancellationToken.None);
            if (data != null) await DeliverAsync(eventType, data);
        }
        catch (Exception ex) { log.LogWarning(ex, "Notification {Event} failed", eventType); }
    }

    private async Task DeliverAsync(string eventType, JsonObject data)
    {
        var settings = store.GetSettings();
        if (settings.HaEvents && supervisor.Available)
        {
            // HA stores events in its database - keep HTML out of event data to save space.
            var lean = (JsonObject)data.DeepClone();
            StripHtml(lean);
            if (await supervisor.FireEventAsync(eventType, lean, CancellationToken.None))
                log.LogInformation("Fired Home Assistant event {Event}", eventType);
        }
        if (!string.IsNullOrWhiteSpace(settings.WebhookUrl))
        {
            try
            {
                using var res = await http.CreateClient("webhook").PostAsync(settings.WebhookUrl,
                    new StringContent(data.ToJsonString(), Encoding.UTF8, "application/json"));
                log.LogInformation("Webhook {Event} → {Code}", eventType, (int)res.StatusCode);
            }
            catch (Exception ex) { log.LogWarning("Webhook failed: {Msg}", ex.Message); }
        }
    }

    private static void StripHtml(JsonObject o)
    {
        if (o["summary"] is JsonObject s) s.Remove("html");
        if (o["sections"] is JsonArray arr) foreach (var x in arr.OfType<JsonObject>()) x.Remove("html");
    }
}
