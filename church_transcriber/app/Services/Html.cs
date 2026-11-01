using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ChurchTranscriber.Services;

public sealed class Run
{
    public string Text = "";
    public bool Bold, Italic, Underline, Highlight, Strike;
}

public sealed class Block
{
    public string Type = "p"; // p | h1 | h2 | h3 | li | quote | note
    public bool Ordered;
    public int Number;
    public string? Timestamp;
    public List<Run> Runs = new();
    public string PlainText => string.Concat(Runs.Select(r => r.Text));
}

/// <summary>Small, tolerant HTML helper for the subset produced by the editor (no external parser needed).</summary>
public static partial class Html
{
    public static string Encode(string s) => WebUtility.HtmlEncode(s);

    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    { "p", "br", "b", "strong", "i", "em", "u", "s", "strike", "mark", "h1", "h2", "h3", "h4", "ul", "ol", "li", "blockquote", "span", "div", "a", "sup", "sub" };
    private static readonly HashSet<string> AllowedAttrs = new(StringComparer.OrdinalIgnoreCase) { "data-t", "data-ts", "class", "href" };

    /// <summary>Keeps only safe formatting tags/attributes (defends against script injection in pasted or AI-translated text).</summary>
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        html = DangerousBlockRx().Replace(html, "");
        return TagRx().Replace(html, m =>
        {
            var closing = m.Groups[1].Value == "/";
            var name = m.Groups[2].Value.ToLowerInvariant();
            if (!AllowedTags.Contains(name)) return "";
            if (closing) return $"</{name}>";
            var sb = new StringBuilder("<").Append(name);
            foreach (Match a in AttrRx().Matches(m.Groups[3].Value))
            {
                var an = a.Groups[1].Value.ToLowerInvariant();
                var av = a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Success ? a.Groups[4].Value : a.Groups[5].Value;
                if (!AllowedAttrs.Contains(an)) continue;
                if (an == "href" && !Regex.IsMatch(av, @"^\s*(https?:|mailto:|#)", RegexOptions.IgnoreCase)) continue;
                sb.Append(' ').Append(an).Append("=\"").Append(Encode(WebUtility.HtmlDecode(av))).Append('"');
            }
            return sb.Append('>').ToString();
        });
    }

    /// <summary>Parses editor HTML into simple blocks with styled runs (used by all exporters).</summary>
    public static List<Block> Parse(string? html)
    {
        var blocks = new List<Block>();
        if (string.IsNullOrWhiteSpace(html)) return blocks;
        Block? cur = null;
        int bold = 0, italic = 0, underline = 0, mark = 0, strike = 0;
        var listStack = new Stack<(bool ordered, int counter)>();
        string? pendingTs = null;
        var blockType = "p";

        void Flush()
        {
            if (cur != null && cur.Runs.Any(r => r.Text.Trim().Length > 0))
            {
                cur.Runs[0].Text = cur.Runs[0].Text.TrimStart();
                cur.Runs[^1].Text = cur.Runs[^1].Text.TrimEnd();
                blocks.Add(cur);
            }
            cur = null;
        }
        Block Cur() => cur ??= new Block { Type = blockType, Timestamp = pendingTs };

        var pos = 0;
        foreach (Match m in TagRx().Matches(html))
        {
            if (m.Index > pos) AddText(html[pos..m.Index]);
            pos = m.Index + m.Length;
            var closing = m.Groups[1].Value == "/";
            var name = m.Groups[2].Value.ToLowerInvariant();
            var attrs = m.Groups[3].Value;
            switch (name)
            {
                case "p" or "div" or "h1" or "h2" or "h3" or "h4" or "blockquote":
                    Flush();
                    if (!closing)
                    {
                        blockType = name switch { "h1" => "h1", "h2" => "h2", "h3" or "h4" => "h3", "blockquote" => "quote", _ => attrs.Contains("note") ? "note" : "p" };
                        var ts = Regex.Match(attrs, "data-ts=\"([^\"]*)\"");
                        pendingTs = ts.Success ? ts.Groups[1].Value : null;
                    }
                    else { blockType = listStack.Count > 0 ? "li" : "p"; pendingTs = null; }
                    break;
                case "ul" or "ol":
                    Flush();
                    if (!closing) listStack.Push((name == "ol", 0));
                    else if (listStack.Count > 0) listStack.Pop();
                    blockType = "p";
                    break;
                case "li":
                    Flush();
                    if (!closing && listStack.Count > 0)
                    {
                        var (ordered, counter) = listStack.Pop();
                        listStack.Push((ordered, counter + 1));
                        blockType = "li";
                        cur = new Block { Type = "li", Ordered = ordered, Number = counter + 1 };
                    }
                    else blockType = "p";
                    break;
                case "br":
                    Flush();
                    break;
                case "b" or "strong": bold += closing ? -1 : 1; break;
                case "i" or "em": italic += closing ? -1 : 1; break;
                case "u": underline += closing ? -1 : 1; break;
                case "mark": mark += closing ? -1 : 1; break;
                case "s" or "strike": strike += closing ? -1 : 1; break;
            }
        }
        if (pos < html.Length) AddText(html[pos..]);
        Flush();
        return blocks;

        void AddText(string raw)
        {
            var text = WhitespaceRx().Replace(WebUtility.HtmlDecode(raw), " ");
            if (text.Length == 0 || (cur == null && text.Trim().Length == 0)) return;
            Cur().Runs.Add(new Run { Text = text, Bold = bold > 0, Italic = italic > 0, Underline = underline > 0, Highlight = mark > 0, Strike = strike > 0 });
        }
    }

    public static string ToPlain(string? html) => string.Join("\n\n", Parse(html).Select(b => b.PlainText));

    [GeneratedRegex(@"<(script|style|iframe|object|embed|svg|math|template)[^>]*>[\s\S]*?</\1\s*>|<!--[\s\S]*?-->", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousBlockRx();
    [GeneratedRegex(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)((?:\s+[^\s=>/]+(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+))?)*)\s*/?>")]
    private static partial Regex TagRx();
    [GeneratedRegex(@"([^\s=>/]+)(\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+)))?")]
    private static partial Regex AttrRx();
    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRx();
}
