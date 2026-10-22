using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Documents;

namespace SifuMovesetEditor.Update;

/// <summary>
/// Minimal inline markdown for the update dialog's "What's new" card:
/// GitHub-style **bold** and *italic* / _italic_. Everything else (headings,
/// bullets, links) stays literal text. Newlines are preserved as line breaks;
/// unmatched markers never throw - they just show as-is.
/// </summary>
internal static class MarkdownInlines
{
    // Bold first so ** isn't consumed by the single-star rule; _ only at word
    // boundaries so snake_case_words stay untouched; char classes exclude \n so
    // emphasis never spans lines.
    private static readonly Regex Token = new(
        @"\*\*(?!\s)(?<b>[^*\n]+?)(?<!\s)\*\*" +
        @"|\*(?!\s)(?<i>[^*\n]+?)(?<!\s)\*" +
        @"|(?<!\w)_(?!\s)(?<u>[^_\n]+?)(?<!\s)_(?!\w)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static List<Inline> Render(string text)
    {
        var result = new List<Inline>();
        if (string.IsNullOrEmpty(text)) return result;
        var s = text.Replace("\r\n", "\n");
        var pos = 0;
        foreach (Match m in Token.Matches(s))
        {
            if (m.Index > pos)
                result.Add(new Run(s.Substring(pos, m.Index - pos)));
            if (m.Groups["b"].Success)
                result.Add(new Bold(new Run(m.Groups["b"].Value)));
            else if (m.Groups["i"].Success)
                result.Add(new Italic(new Run(m.Groups["i"].Value)));
            else
                result.Add(new Italic(new Run(m.Groups["u"].Value)));
            pos = m.Index + m.Length;
        }
        if (pos < s.Length)
            result.Add(new Run(s.Substring(pos)));
        return result;
    }
}
