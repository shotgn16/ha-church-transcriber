using System.Text;

namespace ChurchTranscriber.Services;

/// <summary>Turns raw timed segments + a section plan into editable rich-text sections.</summary>
public static class DocumentBuilder
{
    public static string Ts(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static List<Section> Build(List<Segment> segs, List<PlannedSection> plan, Settings settings, double duration)
    {
        var ordered = plan.OrderBy(p => p.Start).ToList();
        var end = Math.Max(duration, segs.Count > 0 ? segs[^1].End : 0);
        var sections = new List<Section>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            var sEnd = i + 1 < ordered.Count ? ordered[i + 1].Start : end;
            var inRange = segs.Where(s => s.Start >= p.Start - 0.01 && s.Start < sEnd - 0.01).ToList();
            if (inRange.Count == 0 && i > 0 && sEnd - p.Start < 5) continue;
            string html;
            if (p.Kind == "worship" && settings.OmitWorshipLyrics && inRange.Count > 0)
                html = $"<p class=\"note\"><em>[Sung worship · {Math.Round((sEnd - p.Start) / 60)} min - lyrics not transcribed]</em></p>";
            else
                html = Paragraphs(inRange);
            sections.Add(new Section
            {
                Title = p.Title, Kind = p.Kind, Start = p.Start, End = sEnd, Summary = p.Summary,
                Html = html.Length > 0 ? html : "<p class=\"note\"><em>[No speech detected]</em></p>"
            });
        }
        return sections;
    }

    /// <summary>Groups segments into readable paragraphs, breaking on pauses and length.</summary>
    public static string Paragraphs(List<Segment> segs)
    {
        var sb = new StringBuilder();
        var para = new StringBuilder();
        double paraStart = 0, lastEnd = 0;
        void Flush()
        {
            if (para.Length == 0) return;
            sb.Append($"<p data-t=\"{(int)paraStart}\" data-ts=\"{Ts(paraStart)}\">")
              .Append(Html.Encode(para.ToString().Trim())).Append("</p>");
            para.Clear();
        }
        foreach (var s in segs)
        {
            var text = s.Text.Trim();
            if (text.Length == 0) continue;
            var endsSentence = para.Length > 0 && ".?!\"”".Contains(para.ToString().TrimEnd()[^1]);
            var pause = para.Length > 0 && s.Start - lastEnd > 2.5;
            if (para.Length > 0 && (pause && endsSentence || pause && para.Length > 300 || endsSentence && para.Length > 650 || para.Length > 1000))
                Flush();
            if (para.Length == 0) paraStart = s.Start;
            para.Append(' ').Append(text);
            lastEnd = s.End;
        }
        Flush();
        return sb.ToString();
    }
}
