using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace ChurchTranscriber.Services;

/// <summary>
/// Which parts of a transcript to return through the automation API or include in notifications.
/// Expressed as a query string so the same selection works in URLs, settings and the UI builder, e.g.
/// <c>parts=summary,sections&amp;kinds=sermon&amp;format=text&amp;lang=es</c>.
/// </summary>
public sealed class ContentSelection
{
    public static readonly string[] AllParts = { "summary", "scriptures", "outline", "sections" };

    public HashSet<string> Parts { get; private set; } = new(AllParts);
    public HashSet<string> Kinds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<int> Indexes { get; } = new();      // 1-based section numbers
    public HashSet<string> Ids { get; } = new();       // section ids
    public string Format { get; private set; } = "json";
    public string? Lang { get; private set; }
    public bool Timestamps { get; private set; }
    public int MaxChars { get; private set; }

    public bool FiltersSections => Kinds.Count > 0 || Indexes.Count > 0 || Ids.Count > 0;

    public static ContentSelection Parse(string? query) =>
        Parse(QueryHelpers.ParseQuery(string.IsNullOrWhiteSpace(query) ? "" : query.TrimStart('?')));

    public static ContentSelection Parse(IEnumerable<KeyValuePair<string, StringValues>> query)
    {
        var q = query.ToDictionary(k => k.Key.ToLowerInvariant(), v => string.Join(',', v.Value.ToArray()));
        var s = new ContentSelection();
        string? Get(params string[] names) => names.Select(n => q.GetValueOrDefault(n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        static IEnumerable<string> Split(string? v) =>
            (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant());

        var parts = Split(Get("parts", "part", "include")).ToList();
        if (parts.Count > 0 && !parts.Contains("all"))
        {
            s.Parts = new HashSet<string>(parts.Select(p => p switch
            {
                "transcript" or "full" or "text" or "section" => "sections",
                "notes" or "overview" or "sermon_notes" => "summary",
                "scripture" or "bible" or "references" => "scriptures",
                "toc" or "contents" => "outline",
                _ => p
            }).Where(AllParts.Contains));
        }
        foreach (var k in Split(Get("kinds", "kind", "type"))) s.Kinds.Add(k);
        foreach (var token in Split(Get("sections", "section")))
        {
            var range = token.Split('-');
            if (range.Length == 2 && int.TryParse(range[0], out var a) && int.TryParse(range[1], out var b))
                for (var i = Math.Min(a, b); i <= Math.Max(a, b) && i - Math.Min(a, b) < 500; i++) s.Indexes.Add(i);
            else if (int.TryParse(token, out var n)) s.Indexes.Add(n);
            else s.Ids.Add(token);
        }
        // Asking for particular sections implies wanting their text.
        if (s.FiltersSections && parts.Count == 0) s.Parts = new() { "sections" };

        var fmt = Get("format")?.ToLowerInvariant();
        s.Format = fmt switch { "text" or "txt" or "plain" => "text", "md" or "markdown" => "markdown", "html" => "html", _ => "json" };
        s.Lang = Get("lang", "language");
        if (s.Lang is "original" or "orig") s.Lang = null;
        s.Timestamps = Get("timestamps", "ts") is "1" or "true" or "yes";
        if (int.TryParse(Get("max_chars", "maxchars", "max"), out var max) && max > 0) s.MaxChars = Math.Max(40, max);
        return s;
    }

    public string ToQuery()
    {
        var q = new List<string>();
        if (!Parts.SetEquals(AllParts)) q.Add("parts=" + string.Join(',', AllParts.Where(Parts.Contains)));
        if (Kinds.Count > 0) q.Add("kinds=" + string.Join(',', Kinds));
        var secs = Indexes.OrderBy(i => i).Select(i => i.ToString()).Concat(Ids).ToList();
        if (secs.Count > 0) q.Add("sections=" + string.Join(',', secs));
        if (Format != "json") q.Add("format=" + Format);
        if (Lang != null) q.Add("lang=" + Uri.EscapeDataString(Lang));
        if (Timestamps) q.Add("timestamps=1");
        if (MaxChars > 0) q.Add("max_chars=" + MaxChars);
        return string.Join('&', q);
    }
}

/// <summary>Builds automation-friendly content (JSON / text / Markdown / HTML) from a transcript and a selection.</summary>
public static class ContentApi
{
    private sealed record Picked(int Index, ExportSection Section);

    public static string ResolveLang(Transcript t, ContentSelection sel) =>
        sel.Lang != null && t.Translations.TryGetValue(sel.Lang, out var tr) && tr.Status == "done" ? sel.Lang : "";

    private static List<Picked> PickSections(ExportDoc doc, ContentSelection sel) =>
        doc.Sections.Select((s, i) => new Picked(i + 1, s))
            .Where(p => !sel.FiltersSections || sel.Kinds.Contains(p.Section.Kind) || sel.Indexes.Contains(p.Index) || sel.Ids.Contains(p.Section.Id))
            .ToList();

    public static JsonObject Json(Transcript t, ContentSelection sel, string? appBaseUrl = null)
    {
        var lang = ResolveLang(t, sel);
        var doc = Exporter.Compose(t, lang, sel.Timestamps);
        var picked = PickSections(doc, sel);
        var (text, truncated) = Truncate(Text(t, sel, includeHeader: false), sel.MaxChars);

        var o = new JsonObject
        {
            ["id"] = t.Id,
            ["title"] = doc.Title,
            ["channel"] = t.Channel,
            ["date"] = t.UploadDate,
            ["url"] = t.Url,
            ["duration"] = Math.Round(t.Duration),
            ["status"] = t.Status.ToString().ToLowerInvariant(),
            ["completed_at"] = t.CompletedAt?.ToString("o"),
            ["language"] = lang == "" ? t.DetectedLanguage : lang,
            ["translation"] = lang == "" ? null : lang,
            ["available_languages"] = new JsonArray(new[] { (JsonNode?)JsonValue.Create(t.DetectedLanguage ?? "original") }
                .Concat(t.Translations.Values.Where(x => x.Status == "done").Select(x => (JsonNode?)JsonValue.Create(x.Lang))).ToArray()),
            ["selection"] = sel.ToQuery(),
        };
        if (sel.Lang != null && lang == "") o["warning"] = $"No finished '{sel.Lang}' translation - returning the original language.";

        if (sel.Parts.Contains("summary"))
            o["summary"] = string.IsNullOrWhiteSpace(doc.SummaryHtml) ? null
                : new JsonObject { ["text"] = BlocksToText(Html.Parse(doc.SummaryHtml), false), ["html"] = doc.SummaryHtml };
        if (sel.Parts.Contains("scriptures"))
            o["scriptures"] = new JsonArray(t.Scriptures.Select(r => (JsonNode)new JsonObject
                { ["reference"] = r.Reference, ["time"] = Math.Round(r.Time), ["timestamp"] = DocumentBuilder.Ts(r.Time) }).ToArray());
        if (sel.Parts.Contains("outline"))
            o["outline"] = new JsonArray(doc.Sections.Select((s, i) => (JsonNode)new JsonObject
            {
                ["index"] = i + 1, ["id"] = s.Id, ["kind"] = s.Kind, ["title"] = s.Title,
                ["start"] = Math.Round(s.Start), ["end"] = Math.Round(s.End), ["timestamp"] = DocumentBuilder.Ts(s.Start)
            }).ToArray());
        if (sel.Parts.Contains("sections"))
            o["sections"] = new JsonArray(picked.Select(p => (JsonNode)new JsonObject
            {
                ["index"] = p.Index, ["id"] = p.Section.Id, ["kind"] = p.Section.Kind, ["title"] = p.Section.Title,
                ["start"] = Math.Round(p.Section.Start), ["end"] = Math.Round(p.Section.End), ["timestamp"] = DocumentBuilder.Ts(p.Section.Start),
                ["text"] = BlocksToText(Html.Parse(p.Section.Html), sel.Timestamps), ["html"] = p.Section.Html
            }).ToArray());
        o["text"] = text;
        o["truncated"] = truncated;
        if (appBaseUrl != null)
            o["links"] = new JsonObject
            {
                ["api"] = $"{appBaseUrl}/api/v1/transcripts/{t.Id}",
                ["docx"] = $"{appBaseUrl}/api/v1/transcripts/{t.Id}/export?format=docx" + (lang == "" ? "" : "&lang=" + lang),
            };
        return o;
    }

    /// <summary>Plain text of the selected parts - ideal for notifications, TTS or e-mail.</summary>
    public static string Text(Transcript t, ContentSelection sel, bool includeHeader = true)
    {
        var lang = ResolveLang(t, sel);
        var doc = Exporter.Compose(t, lang, sel.Timestamps);
        var sb = new StringBuilder();
        if (includeHeader)
        {
            sb.AppendLine(doc.Title);
            if (doc.Meta.Length > 0) sb.AppendLine(doc.Meta);
            sb.AppendLine();
        }
        if (sel.Parts.Contains("summary") && !string.IsNullOrWhiteSpace(doc.SummaryHtml))
            sb.AppendLine(BlocksToText(Html.Parse(doc.SummaryHtml), false)).AppendLine();
        if (sel.Parts.Contains("scriptures") && t.Scriptures.Count > 0)
            sb.AppendLine("Scripture: " + string.Join("; ", t.Scriptures.Select(r => r.Reference))).AppendLine();
        if (sel.Parts.Contains("outline") && doc.Sections.Count > 0)
        {
            foreach (var s in doc.Sections) sb.AppendLine($"{DocumentBuilder.Ts(s.Start)}  {s.Title}");
            sb.AppendLine();
        }
        if (sel.Parts.Contains("sections"))
            foreach (var p in PickSections(doc, sel))
            {
                sb.AppendLine(p.Section.Title + (sel.Timestamps ? $" ({DocumentBuilder.Ts(p.Section.Start)})" : ""));
                sb.AppendLine(BlocksToText(Html.Parse(p.Section.Html), sel.Timestamps)).AppendLine();
            }
        return sb.ToString().Trim();
    }

    public static string Markdown(Transcript t, ContentSelection sel)
    {
        var lang = ResolveLang(t, sel);
        var doc = Exporter.Compose(t, lang, sel.Timestamps);
        var picked = PickSections(doc, sel);
        var filtered = doc with
        {
            SummaryHtml = sel.Parts.Contains("summary") ? doc.SummaryHtml : null,
            Scriptures = sel.Parts.Contains("scriptures") ? doc.Scriptures : new(),
            Sections = sel.Parts.Contains("sections") ? picked.Select(p => p.Section).ToList() : new()
        };
        var md = Exporter.Markdown(filtered);
        if (!sel.Parts.Contains("outline"))
        {
            // Remove the contents list Exporter.Markdown always adds.
            var start = md.IndexOf("## Contents", StringComparison.Ordinal);
            if (start >= 0)
            {
                var end = md.IndexOf("\n## ", start + 5, StringComparison.Ordinal);
                md = end > 0 ? md[..start] + md[(end + 1)..] : md[..start];
            }
        }
        return Truncate(md.Trim(), sel.MaxChars).Text;
    }

    public static string HtmlPage(Transcript t, ContentSelection sel)
    {
        var lang = ResolveLang(t, sel);
        var doc = Exporter.Compose(t, lang, sel.Timestamps);
        return Exporter.HtmlPage(doc with
        {
            SummaryHtml = sel.Parts.Contains("summary") ? doc.SummaryHtml : null,
            Scriptures = sel.Parts.Contains("scriptures") ? doc.Scriptures : new(),
            Sections = sel.Parts.Contains("sections") ? PickSections(doc, sel).Select(p => p.Section).ToList() : new()
        });
    }

    public static string BlocksToText(List<Block> blocks, bool timestamps) =>
        string.Join("\n", blocks.Select(b => b.Type switch
        {
            "li" => (b.Ordered ? $"{b.Number}. " : "• ") + b.PlainText,
            "h1" or "h2" or "h3" => "\n" + b.PlainText,
            _ => (timestamps && b.Timestamp != null ? $"[{b.Timestamp}] " : "") + b.PlainText
        })).Trim();

    public static (string Text, bool Truncated) Truncate(string text, int max)
    {
        if (max <= 0 || text.Length <= max) return (text, false);
        var cut = text.LastIndexOf(' ', Math.Max(0, max - 1), Math.Min(max, 200));
        return (text[..(cut > max / 2 ? cut : max - 1)].TrimEnd() + "…", true);
    }
}
