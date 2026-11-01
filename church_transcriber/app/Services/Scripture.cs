using System.Text.RegularExpressions;

namespace ChurchTranscriber.Services;

/// <summary>Finds Bible references (e.g. "John 3:16", "Romans chapter 8 verse 28", "First Corinthians 13") in transcripts.</summary>
public static partial class Scripture
{
    private static readonly (string Name, string Pattern)[] Books =
    {
        ("Genesis","genesis|gen"),("Exodus","exodus"),("Leviticus","leviticus"),("Numbers","numbers"),("Deuteronomy","deuteronomy"),
        ("Joshua","joshua"),("Judges","judges"),("Ruth","ruth"),("1 Samuel","{1}\\s*samuel"),("2 Samuel","{2}\\s*samuel"),
        ("1 Kings","{1}\\s*kings"),("2 Kings","{2}\\s*kings"),("1 Chronicles","{1}\\s*chronicles"),("2 Chronicles","{2}\\s*chronicles"),
        ("Ezra","ezra"),("Nehemiah","nehemiah"),("Esther","esther"),("Job","job"),("Psalm","psalms?"),("Proverbs","proverbs"),
        ("Ecclesiastes","ecclesiastes"),("Song of Songs","song\\s+of\\s+(?:songs|solomon)"),("Isaiah","isaiah"),("Jeremiah","jeremiah"),
        ("Lamentations","lamentations"),("Ezekiel","ezekiel"),("Daniel","daniel"),("Hosea","hosea"),("Joel","joel"),("Amos","amos"),
        ("Obadiah","obadiah"),("Jonah","jonah"),("Micah","micah"),("Nahum","nahum"),("Habakkuk","habakkuk"),("Zephaniah","zephaniah"),
        ("Haggai","haggai"),("Zechariah","zechariah"),("Malachi","malachi"),("Matthew","matthew"),("Mark","mark"),("Luke","luke"),
        ("John","john"),("Acts","acts"),("Romans","romans"),("1 Corinthians","{1}\\s*corinthians"),("2 Corinthians","{2}\\s*corinthians"),
        ("Galatians","galatians"),("Ephesians","ephesians"),("Philippians","philippians"),("Colossians","colossians"),
        ("1 Thessalonians","{1}\\s*thessalonians"),("2 Thessalonians","{2}\\s*thessalonians"),("1 Timothy","{1}\\s*timothy"),
        ("2 Timothy","{2}\\s*timothy"),("Titus","titus"),("Philemon","philemon"),("Hebrews","hebrews"),("James","james"),
        ("1 Peter","{1}\\s*peter"),("2 Peter","{2}\\s*peter"),("1 John","{1}\\s*john"),("2 John","{2}\\s*john"),("3 John","{3}\\s*john"),
        ("Jude","jude"),("Revelation","revelations?")
    };

    // Book names that are also common words/names: only accept them with an explicit verse or the word "chapter".
    private static readonly HashSet<string> Ambiguous = new() { "Mark", "Job", "Numbers", "James", "John", "Ruth", "Acts", "Daniel", "Joel", "Amos", "Jude", "Titus" };

    private static readonly List<(string Name, Regex Rx)> Compiled = Books.Select(b =>
    {
        var p = b.Pattern.Replace("{1}", "(?:1|1st|first|i)").Replace("{2}", "(?:2|2nd|second|ii)").Replace("{3}", "(?:3|3rd|third|iii)");
        var rx = new Regex(
            $@"\b(?:{p})\s+(?:(?<chap>chapter)\s+)?(?<c>\d{{1,3}})(?:\s*[:.]\s*(?<v>\d{{1,3}})|,?\s+(?:verses?|v\.)\s+(?<v>\d{{1,3}}))?(?:\s*(?:-|–|to|through)\s*(?<v2>\d{{1,3}}))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        return (b.Name, rx);
    }).ToList();

    public static List<ScriptureRef> Find(IEnumerable<Segment> segments)
    {
        var found = new List<ScriptureRef>();
        var seen = new HashSet<string>();
        foreach (var s in segments)
        {
            foreach (var r in FindInText(s.Text))
                if (seen.Add(r)) found.Add(new ScriptureRef { Reference = r, Time = s.Start });
        }
        return found;
    }

    public static IEnumerable<string> FindInText(string text)
    {
        // Longer names first so "1 John" wins over "John".
        var taken = new List<(int, int)>();
        foreach (var (name, rx) in Compiled.OrderByDescending(c => c.Name.Length))
        {
            foreach (Match m in rx.Matches(text))
            {
                if (taken.Any(t => m.Index < t.Item2 && m.Index + m.Length > t.Item1)) continue;
                var hasVerse = m.Groups["v"].Success;
                if (Ambiguous.Contains(name) && !hasVerse && !m.Groups["chap"].Success) continue;
                var chapter = int.Parse(m.Groups["c"].Value);
                if (chapter == 0 || chapter > 150) continue;
                taken.Add((m.Index, m.Index + m.Length));
                var reference = $"{name} {chapter}";
                if (hasVerse)
                {
                    reference += ":" + m.Groups["v"].Value;
                    if (m.Groups["v2"].Success) reference += "–" + m.Groups["v2"].Value;
                }
                yield return reference;
            }
        }
    }
}
