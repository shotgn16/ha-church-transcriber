using System.IO.Compression;
using System.Security;
using System.Text;

namespace ChurchTranscriber.Services;

public sealed record ExportSection(string Title, string Kind, double Start, double End, string Html, string Id = "");
public sealed record ExportDoc(string Title, string Meta, string? Url, string? SummaryHtml, List<ScriptureRef> Scriptures,
    List<ExportSection> Sections, string Lang, bool Timestamps);

/// <summary>Exports transcripts to Word (.docx), Markdown, plain text, HTML and subtitles (.srt).</summary>
public static class Exporter
{
    public static ExportDoc Compose(Transcript t, string? lang, bool timestamps)
    {
        var meta = string.Join(" · ", new[] { t.Channel, t.UploadDate, t.Duration > 0 ? DocumentBuilder.Ts(t.Duration) : null }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (!string.IsNullOrEmpty(lang) && t.Translations.TryGetValue(lang, out var tr) && tr.Status == "done")
        {
            var map = tr.Sections.ToDictionary(s => s.Id);
            var secs = t.Sections.Select(s => map.TryGetValue(s.Id, out var x)
                ? new ExportSection(x.Title, s.Kind, s.Start, s.End, x.Html, s.Id)
                : new ExportSection(s.Title, s.Kind, s.Start, s.End, s.Html, s.Id)).ToList();
            return new ExportDoc(string.IsNullOrWhiteSpace(tr.Title) ? t.Title : tr.Title, meta + $" · {Translator.NameOf(lang)}",
                t.Url, tr.SummaryHtml, t.Scriptures, secs, lang, timestamps);
        }
        return new ExportDoc(t.Title, meta, t.Url, t.SummaryHtml, t.Scriptures,
            t.Sections.Select(s => new ExportSection(s.Title, s.Kind, s.Start, s.End, s.Html, s.Id)).ToList(), t.DetectedLanguage ?? "en", timestamps);
    }

    // ---------------- Markdown / text ----------------
    public static string Markdown(ExportDoc d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {d.Title}").AppendLine();
        if (d.Meta.Length > 0) sb.AppendLine($"_{d.Meta}_").AppendLine();
        if (d.Url != null) sb.AppendLine($"<{d.Url}>").AppendLine();
        if (!string.IsNullOrWhiteSpace(d.SummaryHtml)) { sb.AppendLine("## Overview").AppendLine(); AppendMd(sb, Html.Parse(d.SummaryHtml), false); }
        if (d.Scriptures.Count > 0)
        {
            sb.AppendLine("## Scripture references").AppendLine();
            foreach (var s in d.Scriptures) sb.AppendLine($"- {s.Reference} ({DocumentBuilder.Ts(s.Time)})");
            sb.AppendLine();
        }
        sb.AppendLine("## Contents").AppendLine();
        foreach (var s in d.Sections) sb.AppendLine($"- {DocumentBuilder.Ts(s.Start)} — {s.Title}");
        sb.AppendLine();
        foreach (var s in d.Sections)
        {
            sb.AppendLine($"## {s.Title}").AppendLine();
            if (d.Timestamps) sb.AppendLine($"_{DocumentBuilder.Ts(s.Start)} – {DocumentBuilder.Ts(s.End)}_").AppendLine();
            AppendMd(sb, Html.Parse(s.Html), d.Timestamps);
        }
        return sb.ToString();
    }

    private static void AppendMd(StringBuilder sb, List<Block> blocks, bool ts)
    {
        foreach (var b in blocks)
        {
            var text = string.Concat(b.Runs.Select(r =>
            {
                var t = r.Text;
                if (t.Trim().Length == 0) return t;
                var core = t.Trim(); var lead = t[..(t.Length - t.TrimStart().Length)]; var trail = t[t.TrimEnd().Length..];
                if (r.Bold) core = $"**{core}**";
                if (r.Italic) core = $"*{core}*";
                if (r.Highlight) core = $"=={core}==";
                if (r.Strike) core = $"~~{core}~~";
                return lead + core + trail;
            }));
            var prefix = ts && b.Timestamp != null ? $"`[{b.Timestamp}]` " : "";
            sb.AppendLine(b.Type switch
            {
                "h1" or "h2" => $"### {text}",
                "h3" => $"#### {text}",
                "li" => b.Ordered ? $"{b.Number}. {text}" : $"- {text}",
                "quote" => $"> {text}",
                _ => prefix + text
            });
            if (b.Type != "li") sb.AppendLine();
        }
        if (blocks.Count > 0 && blocks[^1].Type == "li") sb.AppendLine();
    }

    public static string PlainText(ExportDoc d)
    {
        var sb = new StringBuilder();
        sb.AppendLine(d.Title).AppendLine(new string('=', Math.Min(d.Title.Length, 80)));
        if (d.Meta.Length > 0) sb.AppendLine(d.Meta);
        if (d.Url != null) sb.AppendLine(d.Url);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(d.SummaryHtml)) { sb.AppendLine("OVERVIEW").AppendLine(); AppendText(sb, Html.Parse(d.SummaryHtml), false); }
        if (d.Scriptures.Count > 0) sb.AppendLine("SCRIPTURE: " + string.Join("; ", d.Scriptures.Select(s => s.Reference))).AppendLine();
        foreach (var s in d.Sections)
        {
            sb.AppendLine(s.Title.ToUpperInvariant() + (d.Timestamps ? $"  [{DocumentBuilder.Ts(s.Start)}]" : "")).AppendLine();
            AppendText(sb, Html.Parse(s.Html), d.Timestamps);
        }
        return sb.ToString();
    }

    private static void AppendText(StringBuilder sb, List<Block> blocks, bool ts)
    {
        foreach (var b in blocks)
        {
            var prefix = b.Type == "li" ? (b.Ordered ? $"  {b.Number}. " : "  • ") : ts && b.Timestamp != null ? $"[{b.Timestamp}] " : "";
            sb.AppendLine(prefix + b.PlainText);
            if (b.Type != "li") sb.AppendLine();
        }
    }

    // ---------------- HTML (also used for print → PDF) ----------------
    public static string HtmlPage(ExportDoc d)
    {
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html><html lang="{{d.Lang}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{Html.Encode(d.Title)}}</title>
            <style>
            body{font-family:Georgia,'Iowan Old Style',serif;max-width:46rem;margin:2rem auto;padding:0 1rem;line-height:1.65;color:#222}
            h1{font-size:2rem;margin-bottom:.2rem} .meta{color:#666;font-style:italic} h2{border-bottom:1px solid #ddd;padding-bottom:.2rem;margin-top:2.2rem}
            .time{color:#888;font:0.8rem system-ui,sans-serif} p[data-ts]::before{content:attr(data-ts);color:#999;font:0.75rem system-ui,sans-serif;margin-right:.6rem}
            .note{color:#777} mark{background:#fff2a8} @media print{body{margin:0} a{color:inherit} }
            {{(d.Timestamps ? "" : "p[data-ts]::before{display:none}")}}
            </style></head><body>
            """);
        sb.Append($"<h1>{Html.Encode(d.Title)}</h1><p class=\"meta\">{Html.Encode(d.Meta)}</p>");
        if (d.Url != null) sb.Append($"<p class=\"meta\"><a href=\"{Html.Encode(d.Url)}\">{Html.Encode(d.Url)}</a></p>");
        if (!string.IsNullOrWhiteSpace(d.SummaryHtml)) sb.Append("<h2>Overview</h2>").Append(Html.Sanitize(d.SummaryHtml));
        if (d.Scriptures.Count > 0)
            sb.Append("<h2>Scripture references</h2><p>").Append(string.Join(" · ", d.Scriptures.Select(s => Html.Encode(s.Reference)))).Append("</p>");
        foreach (var s in d.Sections)
        {
            sb.Append($"<h2>{Html.Encode(s.Title)}</h2>");
            if (d.Timestamps) sb.Append($"<div class=\"time\">{DocumentBuilder.Ts(s.Start)} – {DocumentBuilder.Ts(s.End)}</div>");
            sb.Append(Html.Sanitize(s.Html));
        }
        return sb.Append("</body></html>").ToString();
    }

    // ---------------- SRT subtitles (original language, from raw segments) ----------------
    public static string Srt(List<Segment> segs)
    {
        static string T(double s) { var t = TimeSpan.FromSeconds(s); return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}"; }
        var sb = new StringBuilder();
        for (var i = 0; i < segs.Count; i++)
            sb.AppendLine((i + 1).ToString()).AppendLine($"{T(segs[i].Start)} --> {T(segs[i].End)}").AppendLine(segs[i].Text).AppendLine();
        return sb.ToString();
    }

    // ---------------- Word (.docx), written as OpenXML by hand ----------------
    public static byte[] Docx(ExportDoc d)
    {
        var body = new StringBuilder();
        void Para(string style, IEnumerable<Run> runs, string? ts = null, bool keepNext = false, string? numbering = null)
        {
            body.Append("<w:p><w:pPr>").Append($"<w:pStyle w:val=\"{style}\"/>");
            if (keepNext) body.Append("<w:keepNext/>");
            body.Append("</w:pPr>");
            if (ts != null) body.Append($"<w:r><w:rPr><w:color w:val=\"8A8A8A\"/><w:sz w:val=\"16\"/></w:rPr><w:t xml:space=\"preserve\">[{X(ts)}]  </w:t></w:r>");
            if (numbering != null) body.Append($"<w:r><w:t xml:space=\"preserve\">{X(numbering)}</w:t></w:r>");
            foreach (var r in runs)
            {
                body.Append("<w:r><w:rPr>");
                if (r.Bold) body.Append("<w:b/>");
                if (r.Italic) body.Append("<w:i/>");
                if (r.Strike) body.Append("<w:strike/>");
                if (r.Underline) body.Append("<w:u w:val=\"single\"/>");
                if (r.Highlight) body.Append("<w:highlight w:val=\"yellow\"/>");
                body.Append($"</w:rPr><w:t xml:space=\"preserve\">{X(r.Text)}</w:t></w:r>");
            }
            body.Append("</w:p>");
        }
        IEnumerable<Run> Plain(string s, bool italic = false) => new[] { new Run { Text = s, Italic = italic } };
        void Blocks(string? html)
        {
            foreach (var b in Html.Parse(html))
            {
                switch (b.Type)
                {
                    case "h1" or "h2": Para("Heading2", b.Runs, keepNext: true); break;
                    case "h3": Para("Heading3", b.Runs, keepNext: true); break;
                    case "li": Para("ListParagraph", b.Runs, numbering: b.Ordered ? $"{b.Number}.\t" : "•\t"); break;
                    case "quote": Para("Quote", b.Runs); break;
                    case "note": Para("Note", b.Runs); break;
                    default: Para("Normal", b.Runs, d.Timestamps ? b.Timestamp : null); break;
                }
            }
        }

        Para("Title", Plain(d.Title));
        if (d.Meta.Length > 0) Para("Subtitle", Plain(d.Meta));
        if (d.Url != null) Para("Subtitle", Plain(d.Url));
        if (!string.IsNullOrWhiteSpace(d.SummaryHtml)) { Para("Heading1", Plain("Overview"), keepNext: true); Blocks(d.SummaryHtml); }
        if (d.Scriptures.Count > 0)
        {
            Para("Heading1", Plain("Scripture references"), keepNext: true);
            Para("Normal", Plain(string.Join("  ·  ", d.Scriptures.Select(s => s.Reference))));
        }
        Para("Heading1", Plain("Contents"), keepNext: true);
        foreach (var s in d.Sections) Para("ListParagraph", Plain($"{DocumentBuilder.Ts(s.Start)}\t{s.Title}"));
        foreach (var s in d.Sections)
        {
            Para("Heading1", Plain(s.Title), keepNext: true);
            if (d.Timestamps) Para("Note", Plain($"{DocumentBuilder.Ts(s.Start)} – {DocumentBuilder.Ts(s.End)}"), keepNext: true);
            Blocks(s.Html);
        }

        var document = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1300" w:right="1300" w:bottom="1300" w:left="1300" w:header="708" w:footer="708" w:gutter="0"/></w:sectPr></w:body></w:document>
            """;

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string path, string content)
            {
                var e = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Add("[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/></Types>
                """);
            Add("_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>
                """);
            Add("word/_rels/document.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                """);
            Add("docProps/core.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:title>{X(d.Title)}</dc:title><dc:language>{X(d.Lang)}</dc:language><dc:creator>Church Service Transcriber</dc:creator><dcterms:created xsi:type="dcterms:W3CDTF">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created></cp:coreProperties>
                """);
            Add("word/document.xml", document);
            Add("word/styles.xml", Styles);
        }
        return ms.ToArray();
    }

    private static string X(string s) => SecurityElement.Escape(s) ?? "";

    private const string Styles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
        <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Georgia" w:hAnsi="Georgia" w:cs="Arial" w:eastAsia="Arial"/><w:sz w:val="22"/><w:lang w:val="en-GB"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="300" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>
        <w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:qFormat/><w:pPr><w:spacing w:after="80"/></w:pPr><w:rPr><w:sz w:val="48"/><w:color w:val="1F2A44"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Subtitle"><w:name w:val="Subtitle"/><w:basedOn w:val="Normal"/><w:qFormat/><w:pPr><w:spacing w:after="40"/></w:pPr><w:rPr><w:i/><w:color w:val="6B6B6B"/><w:sz w:val="20"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="360" w:after="120"/><w:pBdr><w:bottom w:val="single" w:sz="4" w:space="2" w:color="D9D2C3"/></w:pBdr><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:sz w:val="30"/><w:color w:val="1F2A44"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="240" w:after="80"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:b/><w:sz w:val="26"/><w:color w:val="1F2A44"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Heading3"><w:name w:val="heading 3"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="200" w:after="60"/><w:outlineLvl w:val="2"/></w:pPr><w:rPr><w:b/><w:sz w:val="23"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="ListParagraph"><w:name w:val="List Paragraph"/><w:basedOn w:val="Normal"/><w:pPr><w:tabs><w:tab w:val="left" w:pos="720"/></w:tabs><w:spacing w:after="60"/><w:ind w:left="720" w:hanging="360"/></w:pPr></w:style>
        <w:style w:type="paragraph" w:styleId="Quote"><w:name w:val="Quote"/><w:basedOn w:val="Normal"/><w:pPr><w:ind w:left="567" w:right="567"/></w:pPr><w:rPr><w:i/><w:color w:val="404040"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Note"><w:name w:val="Note"/><w:basedOn w:val="Normal"/><w:rPr><w:i/><w:color w:val="8A8A8A"/><w:sz w:val="18"/></w:rPr></w:style>
        </w:styles>
        """;
}
