using System.Text;
using System.Text.RegularExpressions;

namespace ChurchTranscriber.Services;

/// <summary>Translates section HTML with the local Ollama model, preserving formatting and timestamps.</summary>
public sealed partial class Translator(Ollama ollama)
{
    public static readonly Dictionary<string, string> Languages = new()
    {
        ["en"] = "English", ["es"] = "Spanish", ["fr"] = "French", ["de"] = "German", ["pt"] = "Portuguese", ["it"] = "Italian",
        ["nl"] = "Dutch", ["pl"] = "Polish", ["ro"] = "Romanian", ["uk"] = "Ukrainian", ["ru"] = "Russian", ["el"] = "Greek",
        ["cy"] = "Welsh", ["ga"] = "Irish", ["sv"] = "Swedish", ["no"] = "Norwegian", ["da"] = "Danish", ["fi"] = "Finnish",
        ["hu"] = "Hungarian", ["cs"] = "Czech", ["tr"] = "Turkish", ["ar"] = "Arabic", ["fa"] = "Persian", ["he"] = "Hebrew",
        ["hi"] = "Hindi", ["ur"] = "Urdu", ["pa"] = "Punjabi", ["ta"] = "Tamil", ["bn"] = "Bengali", ["ml"] = "Malayalam",
        ["zh"] = "Chinese (Simplified)", ["zh-TW"] = "Chinese (Traditional)", ["ja"] = "Japanese", ["ko"] = "Korean",
        ["vi"] = "Vietnamese", ["th"] = "Thai", ["id"] = "Indonesian", ["tl"] = "Tagalog", ["sw"] = "Swahili", ["yo"] = "Yoruba",
        ["ig"] = "Igbo", ["ha"] = "Hausa", ["am"] = "Amharic", ["af"] = "Afrikaans", ["zu"] = "Zulu", ["tw"] = "Twi",
    };

    public static string NameOf(string code) => Languages.TryGetValue(code, out var n) ? n : code;

    public bool Available => ollama.Configured;

    public async Task<string> TranslateHtmlAsync(string html, string lang, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;
        var parts = new List<string>();
        // Small chunks keep a local model accurate and stop it summarising instead of translating.
        foreach (var chunk in Chunk(html, 2500))
            parts.Add(await TranslateChunkAsync(chunk, lang, ct));
        return Html.Sanitize(string.Concat(parts));
    }

    public async Task<string> TranslateTextAsync(string text, string lang, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var r = await ollama.ChatAsync(
            $"Translate this heading from a church service into {NameOf(lang)}. Reply with only the translated heading, nothing else.", text, false, ct);
        return Ollama.Unfence(r).Trim().Trim('"').Split('\n')[0].Trim();
    }

    private async Task<string> TranslateChunkAsync(string html, string lang, CancellationToken ct)
    {
        var system = $"""
            You are a professional translator of Christian church content. Translate the HTML the user sends into {NameOf(lang)}.
            Rules:
            - Keep every HTML tag and attribute exactly as it is (including data-t and data-ts). Translate only the visible text.
            - Use the Bible book names, scripture wording and church terms normally used in {NameOf(lang)}.
            - Translate everything. Do not summarise, explain, add or omit anything.
            - Reply with only the translated HTML.
            """;
        var result = Ollama.Unfence(await ollama.ChatAsync(system, html, false, ct, temperature: 0.1));
        // If the model dropped the markup entirely, wrap its text so the document still renders.
        if (html.TrimStart().StartsWith('<') && !result.TrimStart().StartsWith('<'))
            result = string.Concat(result.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(p => $"<p>{Html.Encode(p.Trim())}</p>"));
        return result;
    }

    /// <summary>Splits HTML at block boundaries so each request stays a manageable size.</summary>
    private static IEnumerable<string> Chunk(string html, int max)
    {
        var sb = new StringBuilder();
        foreach (var p in BlockEndRx().Split(html).Where(p => p.Length > 0))
        {
            if (sb.Length > 0 && sb.Length + p.Length > max) { yield return sb.ToString(); sb.Clear(); }
            sb.Append(p);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    [GeneratedRegex(@"(?<=</(?:p|ul|ol|h[1-4]|blockquote|div)>)", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRx();
}
