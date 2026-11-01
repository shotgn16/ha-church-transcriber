using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChurchTranscriber.Services;

public sealed record PlannedSection(double Start, string Title, string Kind, string? Summary);
public sealed record SectionPlan(List<PlannedSection> Sections, string? SummaryHtml, string Method);

/// <summary>Splits a church service into parts: YouTube chapters → local Ollama model → built-in keyword detector.</summary>
public sealed partial class Sectioner(AddonOptions opts, Ollama ai, ILogger<Sectioner> log)
{
    public static readonly Dictionary<string, string> KindNames = new()
    {
        ["prelude"] = "Pre-service",
        ["welcome"] = "Welcome",
        ["worship"] = "Worship",
        ["prayer"] = "Prayer",
        ["announcements"] = "Announcements",
        ["scripture"] = "Scripture Reading",
        ["sermon"] = "Sermon",
        ["offering"] = "Offering",
        ["communion"] = "Communion",
        ["baptism"] = "Baptism",
        ["testimony"] = "Testimony",
        ["response"] = "Response & Altar Call",
        ["benediction"] = "Benediction & Closing",
        ["other"] = "Other",
    };

    private static readonly Dictionary<string, string[]> Keywords = new()
    {
        ["welcome"] = new[] { "welcome", "good morning", "good evening", "glad you're here", "glad you are here", "great to have you", "watching online", "online family", "first time", "visiting with us", "join us" },
        ["announcements"] = new[] { "announcement", "this week", "next week", "next sunday", "sign up", "register", "event", "youth group", "small group", "bulletin", "calendar", "website", "connect card", "volunteer", "meeting", "potluck", "conference" },
        ["prayer"] = new[] { "let's pray", "let us pray", "pray with me", "heavenly father", "dear lord", "father god", "lord we", "father we", "in jesus name", "in jesus' name", "we pray", "bow your heads", "close your eyes", "amen" },
        ["scripture"] = new[] { "reading from", "our reading", "the reading", "the word of the lord", "thanks be to god", "turn with me", "turn in your bible", "open your bible", "gospel of", "the gospel according", "verse", "it says" },
        ["sermon"] = new[] { "sermon", "message today", "this morning i want", "today i want", "series", "the point is", "here's the thing", "big idea", "point number", "first point", "second point", "application" },
        ["offering"] = new[] { "offering", "tithe", "tithes", "giving", "generosity", "give online", "text to give", "envelope", "generous" },
        ["communion"] = new[] { "communion", "lord's supper", "lords supper", "the bread", "the cup", "this is my body", "remembrance of me", "new covenant in my blood", "elements", "eucharist" },
        ["baptism"] = new[] { "baptism", "baptize", "baptized", "baptised", "baptise" },
        ["testimony"] = new[] { "testimony", "share my story", "my story" },
        ["response"] = new[] { "altar", "come forward", "raise your hand", "make a decision", "give your life", "accept jesus", "salvation" },
        ["benediction"] = new[] { "benediction", "go in peace", "bless you and keep you", "may the grace", "see you next", "have a great week", "have a blessed week", "dismissed", "go now" },
        ["worship"] = new[] { "♪", "[music]", "(music)", "[singing]", "hallelujah", "sing", "worship", "praise him", "glory", "hosanna", "oh oh", "holy holy" },
    };

    public async Task<SectionPlan> PlanAsync(Transcript t, Action<string> progress, CancellationToken ct)
    {
        var segs = t.Segments;
        if (segs.Count == 0) return new SectionPlan(new() { new(0, "Service", "other", null) }, null, "empty");
        var useAi = opts.UseAiForSections && ai.Configured;

        // 1. YouTube chapters chosen by the church are the most trustworthy outline.
        if (t.Chapters.Count >= 3)
        {
            var plan = t.Chapters.Select(c => new PlannedSection(c.Start, c.Title, ClassifyTitle(c.Title), null)).ToList();
            var summary = useAi ? await TrySummaryAsync(segs, plan, t.Duration, progress, ct) : null;
            return new SectionPlan(plan, summary, "youtube-chapters");
        }

        // 2. Local AI outline (works in small windows so modest models stay accurate).
        if (useAi)
        {
            try
            {
                var plan = await AiPlanAsync(segs, progress, ct);
                plan = await AiTitlesAsync(segs, plan, t.Duration, progress, ct);
                var summary = await TrySummaryAsync(segs, plan, t.Duration, progress, ct);
                return new SectionPlan(plan, summary, "ai");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.LogWarning(ex, "AI sectioning failed, falling back to built-in detector"); }
        }

        // 3. Built-in keyword detector.
        return new SectionPlan(Heuristic(segs), null, "built-in");
    }

    // ---------------- Local AI ----------------
    private const string KindList = "prelude, welcome, worship, prayer, announcements, scripture, sermon, offering, communion, baptism, testimony, response, benediction, other";

    private sealed record Bucket(double Start, string Text);

    private static List<Bucket> Buckets(List<Segment> segs, double seconds)
    {
        var list = new List<Bucket>();
        double start = -1; var sb = new StringBuilder();
        foreach (var s in segs)
        {
            if (start < 0) start = s.Start;
            if (s.Start - start >= seconds && sb.Length > 0)
            {
                list.Add(new Bucket(start, sb.ToString().Trim())); sb.Clear(); start = s.Start;
            }
            sb.Append(' ').Append(s.Text);
        }
        if (sb.Length > 0) list.Add(new Bucket(start, sb.ToString().Trim()));
        return list;
    }

    private async Task<List<PlannedSection>> AiPlanAsync(List<Segment> segs, Action<string> progress, CancellationToken ct)
    {
        var buckets = Buckets(segs, 30);
        var labels = new string[buckets.Count];
        const int perWindow = 14; // ~7 minutes of service per request
        var windows = (int)Math.Ceiling(buckets.Count / (double)perWindow);
        var previous = "none (this is the start of the service)";

        for (var w = 0; w < windows; w++)
        {
            ct.ThrowIfCancellationRequested();
            progress($"AI is reading the service… part {w + 1} of {windows}");
            var from = w * perWindow; var to = Math.Min(buckets.Count, from + perWindow);
            var lines = new StringBuilder();
            for (var i = from; i < to; i++)
            {
                var txt = buckets[i].Text.Length > 700 ? buckets[i].Text[..700] + "…" : buckets[i].Text;
                lines.Append('[').Append(i).Append("] ").AppendLine(txt);
            }
            var system = $$"""
                You label parts of a Christian church service transcript. Each numbered line is about 30 seconds.
                Give every line exactly one kind from: {{KindList}}.
                Hints: song lyrics, repeated phrases or [Music] = worship. A pastor teaching at length = sermon. Reading a Bible passage aloud = scripture.
                "Let us pray"/addressing God = prayer. Upcoming events and notices = announcements. Talk about giving/tithes = offering.
                Return JSON only: {"labels":[{"line":0,"kind":"welcome"}]}
                """;
            var user = $"The part just before this was: {previous}\n\n{lines}";
            using var doc = await ai.ChatJsonAsync(system, user, ct);
            if (doc.RootElement.TryGetProperty("labels", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var l in arr.EnumerateArray())
                {
                    if (!l.TryGetProperty("line", out var li) || li.ValueKind != JsonValueKind.Number) continue;
                    var idx = li.GetInt32();
                    var kind = (l.TryGetProperty("kind", out var k) ? k.GetString() : null)?.Trim().ToLowerInvariant() ?? "other";
                    if (idx >= from && idx < to) labels[idx] = KindNames.ContainsKey(kind) ? kind : "other";
                }
            for (var i = from; i < to; i++) labels[i] ??= i > 0 ? labels[i - 1] ?? "other" : "welcome";
            previous = labels[to - 1];
        }

        // Smooth isolated blips (except short prayers/readings), then collapse into sections.
        for (var i = 1; i < labels.Length - 1; i++)
            if (labels[i - 1] == labels[i + 1] && labels[i] != labels[i - 1] && labels[i] is not "prayer" and not "scripture")
                labels[i] = labels[i - 1];
        var runs = new List<(double start, string kind)>();
        for (var i = 0; i < labels.Length; i++)
            if (runs.Count == 0 || runs[^1].kind != labels[i]) runs.Add((buckets[i].Start, labels[i]));
        var total = segs[^1].End;
        for (var i = runs.Count - 1; i > 0; i--)
        {
            var len = (i + 1 < runs.Count ? runs[i + 1].start : total) - runs[i].start;
            var isLast = i == runs.Count - 1;
            if (len < (isLast || runs[i].kind is "prayer" or "scripture" or "benediction" or "welcome" or "offering" ? 20 : 60)) runs.RemoveAt(i);
        }
        for (var i = runs.Count - 1; i > 0; i--)
            if (runs[i].kind == runs[i - 1].kind) runs.RemoveAt(i);
        return runs.Select((r, i) => new PlannedSection(i == 0 ? 0 : r.start, KindNames[r.kind], r.kind, null)).ToList();
    }

    private async Task<List<PlannedSection>> AiTitlesAsync(List<Segment> segs, List<PlannedSection> plan, double duration,
        Action<string> progress, CancellationToken ct)
    {
        var result = new List<PlannedSection>();
        for (var i = 0; i < plan.Count; i++)
        {
            progress($"AI is naming sections… {i + 1} of {plan.Count}");
            var end = i + 1 < plan.Count ? plan[i + 1].Start : duration;
            var text = SectionText(segs, plan[i].Start, end, 5000);
            try
            {
                using var doc = await ai.ChatJsonAsync($$"""
                    This is the "{{KindNames[plan[i].Kind]}}" part of a church service.
                    Write a short specific title (max 8 words), e.g. "Worship: Amazing Grace", "Reading: Romans 8:28-39", "Sermon: Living with Hope",
                    and a one-sentence summary of what happens. Return JSON only: {"title":"...","summary":"..."}
                    """, text, ct);
                var title = doc.RootElement.TryGetProperty("title", out var ti) ? ti.GetString() : null;
                var summ = doc.RootElement.TryGetProperty("summary", out var su) ? su.GetString() : null;
                result.Add(plan[i] with { Title = string.IsNullOrWhiteSpace(title) ? plan[i].Title : title!.Trim(), Summary = summ });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.LogWarning("Section title failed: {Msg}", ex.Message); result.Add(plan[i]); }
        }
        return result;
    }

    /// <summary>Overview + sermon notes. Long sermons are summarised in pieces first (map-reduce).</summary>
    private async Task<string?> TrySummaryAsync(List<Segment> segs, List<PlannedSection> plan, double duration,
        Action<string> progress, CancellationToken ct)
    {
        try
        {
            progress("AI is writing the overview and sermon notes…");
            var outline = string.Join("\n", plan.Select(p => $"- {DocumentBuilder.Ts(p.Start)} {p.Title}"));
            var sermonParts = plan.Select((p, i) => (p, end: i + 1 < plan.Count ? plan[i + 1].Start : duration)).Where(x => x.p.Kind == "sermon").ToList();
            var sermonText = sermonParts.Count > 0
                ? string.Join(' ', sermonParts.Select(x => SectionText(segs, x.p.Start, x.end, int.MaxValue)))
                : SectionText(segs, 0, duration, int.MaxValue);

            const int piece = 9000;
            if (sermonText.Length > piece)
            {
                var notes = new List<string>();
                var chunks = Enumerable.Range(0, (sermonText.Length + piece - 1) / piece)
                    .Select(i => sermonText.Substring(i * piece, Math.Min(piece, sermonText.Length - i * piece))).ToList();
                for (var i = 0; i < chunks.Count; i++)
                {
                    progress($"AI is summarising the sermon… part {i + 1} of {chunks.Count}");
                    notes.Add(await ai.ChatAsync("Summarise this part of a sermon as concise bullet-point notes, keeping any Bible references.", chunks[i], false, ct));
                }
                sermonText = string.Join("\n", notes);
            }

            using var doc = await ai.ChatJsonAsync("""
                You write notes for a church service. From the outline and sermon content produce:
                "summary": 2-4 sentences describing the whole service;
                "sermon": {"title": "...", "main_scripture": "...", "key_points": ["3-6 short points"]} (use empty values if there was no sermon).
                Return JSON only.
                """, $"Service outline:\n{outline}\n\nSermon content:\n{sermonText}", ct);
            return BuildSummaryHtml(doc.RootElement);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log.LogWarning(ex, "AI summary failed"); return null; }
    }

    private static string SectionText(List<Segment> segs, double start, double end, int max)
    {
        var text = string.Join(' ', segs.Where(s => s.Start >= start && s.Start < end).Select(s => s.Text));
        if (text.Length <= max) return text;
        var half = max / 2; // keep the beginning and end - that is where titles and conclusions usually are
        return text[..half] + " … " + text[^half..];
    }

    private static string? BuildSummaryHtml(JsonElement root)
    {
        var sb = new StringBuilder();
        if (root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
            sb.Append("<p>").Append(Html.Encode(s.GetString()!)).Append("</p>");
        if (root.TryGetProperty("sermon", out var sermon) && sermon.ValueKind == JsonValueKind.Object)
        {
            string? P(string n) => sermon.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var title = P("title"); var text = P("main_scripture");
            var points = sermon.TryGetProperty("key_points", out var kp) && kp.ValueKind == JsonValueKind.Array
                ? kp.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).Where(p => p.Trim().Length > 0).ToList()
                : new List<string>();
            if (!string.IsNullOrWhiteSpace(title) || points.Count > 0)
            {
                sb.Append("<h3>Sermon").Append(string.IsNullOrWhiteSpace(title) ? "" : ": " + Html.Encode(title!)).Append("</h3>");
                if (!string.IsNullOrWhiteSpace(text)) sb.Append("<p><strong>Main text:</strong> ").Append(Html.Encode(text!)).Append("</p>");
                if (points.Count > 0)
                {
                    sb.Append("<ul>");
                    foreach (var p in points) sb.Append("<li>").Append(Html.Encode(p)).Append("</li>");
                    sb.Append("</ul>");
                }
            }
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    // ---------------- Built-in detector ----------------
    public static string ClassifyTitle(string title)
    {
        var t = title.ToLowerInvariant();
        (string kind, string[] words)[] map =
        {
            ("sermon", new[] { "sermon", "message", "preach", "homily", "teaching", "word" }),
            ("worship", new[] { "worship", "song", "hymn", "praise", "music", "choir" }),
            ("scripture", new[] { "reading", "scripture", "gospel", "psalm", "lesson", "bible" }),
            ("prayer", new[] { "prayer", "pray", "intercession", "confession" }),
            ("announcements", new[] { "announcement", "notices", "news", "update" }),
            ("offering", new[] { "offering", "giving", "tithe", "offertory" }),
            ("communion", new[] { "communion", "lord's supper", "eucharist", "mass" }),
            ("baptism", new[] { "baptism" }),
            ("benediction", new[] { "benediction", "closing", "dismissal", "blessing", "end" }),
            ("welcome", new[] { "welcome", "intro", "opening", "call to worship", "greeting" }),
            ("prelude", new[] { "pre-service", "preservice", "countdown", "prelude", "start" }),
            ("testimony", new[] { "testimony" }),
            ("response", new[] { "altar", "response", "ministry time" }),
        };
        foreach (var (kind, words) in map)
            if (words.Any(w => t.Contains(w))) return kind;
        return "other";
    }

    private const double Window = 45;

    public static List<PlannedSection> Heuristic(List<Segment> segs)
    {
        var total = segs[^1].End;
        var n = Math.Max(1, (int)Math.Ceiling(total / Window));
        var labels = new string[n];
        var density = new double[n];

        for (var i = 0; i < n; i++)
        {
            var from = i * Window; var to = from + Window;
            var inWin = segs.Where(s => s.Start >= from && s.Start < to).ToList();
            var text = string.Join(' ', inWin.Select(s => s.Text)).ToLowerInvariant();
            var words = WordRx().Matches(text).Select(m => m.Value).ToList();
            density[i] = words.Count / (Window / 60.0); // words per minute
            if (words.Count < 8) { labels[i] = inWin.Count == 0 ? "gap" : "worship"; continue; }

            var scores = Keywords.ToDictionary(k => k.Key, k => k.Value.Sum(w => CountOf(text, w) * (w.Contains(' ') ? 2.0 : 1.0)));
            // Repetitive lyrics → worship.
            var uniqueRatio = (double)words.Distinct().Count() / words.Count;
            if (uniqueRatio < 0.45 && words.Count > 25) scores["worship"] += 4;
            if (density[i] < 70) scores["worship"] += 1.5; // sung text is slower than speech
            var best = scores.OrderByDescending(kv => kv.Value).First();
            labels[i] = best.Value >= 2 ? best.Key : "speech";
        }

        // The sermon is the longest run of dense speech (>= 8 minutes) not dominated by other signals.
        bool IsTalk(int k) => labels[k] is "speech" or "sermon" or "scripture" or "prayer" && density[k] > 70;
        int bestStart = -1, bestLen = 0;
        for (var i = 0; i < n;)
        {
            if (!(labels[i] is "speech" or "sermon" or "scripture" && density[i] > 90)) { i++; continue; }
            var j = i;
            while (j < n)
            {
                if (IsTalk(j)) j++;
                else if (j + 1 < n && IsTalk(j + 1)) j += 2; // bridge a single blip (e.g. a quoted song line)
                else break;
            }
            if (j - i > bestLen) { bestLen = j - i; bestStart = i; }
            i = Math.Max(j, i + 1);
        }
        if (bestLen * Window >= 8 * 60)
        {
            // Keep a closing prayer at the end of the sermon as its own part.
            var end = bestStart + bestLen;
            for (var i = bestStart; i < end; i++)
                if (!(labels[i] == "prayer" && i >= end - 2)) labels[i] = "sermon";
        }

        // Unlabeled speech inherits from its neighbour; gaps join the previous part.
        for (var i = 0; i < n; i++)
            if (labels[i] is "speech" or "gap") labels[i] = i > 0 ? labels[i - 1] : "welcome";
        if (labels[0] is "worship" && density[0] < 20) labels[0] = "prelude";

        // Smooth single-window blips.
        for (var i = 1; i < n - 1; i++)
            if (labels[i - 1] == labels[i + 1] && labels[i] != labels[i - 1] && labels[i] is not "prayer" and not "scripture")
                labels[i] = labels[i - 1];

        // Collapse into sections.
        var result = new List<(double start, string kind)>();
        for (var i = 0; i < n; i++)
            if (result.Count == 0 || result[^1].kind != labels[i]) result.Add((i * Window, labels[i]));

        // Merge sections shorter than 90 s into the previous (prayers/readings may be short).
        for (var i = result.Count - 1; i > 0; i--)
        {
            var len = (i + 1 < result.Count ? result[i + 1].start : total) - result[i].start;
            var min = result[i].kind is "prayer" or "scripture" or "benediction" or "welcome" or "offering" ? 30 : 90;
            if (len < min) result.RemoveAt(i);
        }
        for (var i = result.Count - 1; i > 0; i--)
            if (result[i].kind == result[i - 1].kind) result.RemoveAt(i);

        // Snap starts to real segment boundaries and give numbered titles for repeats.
        var counts = new Dictionary<string, int>();
        var plan = new List<PlannedSection>();
        foreach (var (start, kind) in result)
        {
            var snapped = plan.Count == 0 ? 0 : segs.FirstOrDefault(s => s.Start >= start)?.Start ?? start;
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            plan.Add(new PlannedSection(snapped, KindNames[kind], kind, null));
        }
        // Add the number only when a kind repeats.
        var totals = plan.GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>();
        for (var i = 0; i < plan.Count; i++)
        {
            var k = plan[i].Kind;
            seen[k] = seen.GetValueOrDefault(k) + 1;
            if (totals[k] > 1) plan[i] = plan[i] with { Title = $"{KindNames[k]} ({seen[k]})" };
        }
        return plan;
    }

    private static int CountOf(string text, string word)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.Ordinal)) >= 0) { count++; idx += word.Length; }
        return count;
    }

    [GeneratedRegex(@"[\p{L}']+")] private static partial Regex WordRx();
}
