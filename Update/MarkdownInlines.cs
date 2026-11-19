using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;

namespace SifuMovesetEditor.Update;

/// <summary>
/// Minimal markdown for the update dialog's "What's new" card: the subset a GitHub
/// release body actually uses. Block level: #/##/### headings, -/* bullets and ---
/// rules. Inline: `code`, **bold** and *italic* / _italic_. Everything else stays
/// literal, and unmatched markers never throw - they just show as-is.
/// </summary>
internal static class MarkdownInlines
{
    internal const double Heading1Size = 14.0;
    internal const double Heading2Size = 13.0;
    internal const double Heading3Size = 12.5;
    internal const string BulletGlyph = "\u2022  ";
    internal const string RuleGlyph = "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500";

    private static readonly Brush HeadingBrush = Frozen(0xcd, 0xd6, 0xf4);
    private static readonly Brush CodeBrush = Frozen(0xf9, 0xe2, 0xaf);
    private static readonly Brush RuleBrush = Frozen(0x58, 0x5b, 0x70);
    private static readonly FontFamily CodeFont = new("Consolas, Courier New");

    // Block markers are matched before inline tokens so a "#" or "-" marker never
    // leaks into the rendered text.
    private static readonly Regex HeadingLine = new(
        @"^(?<h>#{1,3})[ \t]+(?<t>.+?)[ \t]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BulletLine = new(
        @"^[ \t]*[-*+][ \t]+(?<t>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RuleLine = new(
        @"^[ \t]*(?:-{3,}|\*{3,}|_{3,})[ \t]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Code first: a `**x**` inside backticks must stay literal instead of turning
    // bold. Bold before italic so ** isn't consumed by the single-star rule; _ only
    // at word boundaries so snake_case_words stay untouched; char classes exclude
    // \n so emphasis never spans lines.
    private static readonly Regex Token = new(
        @"`(?<c>[^`\n]+)`" +
        @"|\*\*(?!\s)(?<b>[^*\n]+?)(?<!\s)\*\*" +
        @"|\*(?!\s)(?<i>[^*\n]+?)(?<!\s)\*" +
        @"|(?<!\w)_(?!\s)(?<u>[^_\n]+?)(?<!\s)_(?!\w)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static List<Inline> Render(string text)
    {
        var result = new List<Inline>();
        if (string.IsNullOrEmpty(text)) return result;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) result.Add(new Run("\n"));
            RenderLine(lines[i], result);
        }
        return result;
    }

    private static void RenderLine(string line, List<Inline> into)
    {
        if (line.Length == 0) return;

        var heading = HeadingLine.Match(line);
        if (heading.Success)
        {
            double size = heading.Groups["h"].Value.Length switch
            {
                1 => Heading1Size,
                2 => Heading2Size,
                _ => Heading3Size
            };
            var block = new Bold { FontSize = size, Foreground = HeadingBrush };
            foreach (var inline in Tokenize(heading.Groups["t"].Value)) block.Inlines.Add(inline);
            into.Add(block);
            return;
        }

        if (RuleLine.IsMatch(line))
        {
            into.Add(new Run(RuleGlyph) { Foreground = RuleBrush });
            return;
        }

        var bullet = BulletLine.Match(line);
        if (bullet.Success)
        {
            into.Add(new Run(BulletGlyph));
            foreach (var inline in Tokenize(bullet.Groups["t"].Value)) into.Add(inline);
            return;
        }

        foreach (var inline in Tokenize(line)) into.Add(inline);
    }

    private static IEnumerable<Inline> Tokenize(string s)
    {
        var pos = 0;
        foreach (Match m in Token.Matches(s))
        {
            if (m.Index > pos)
                yield return new Run(s.Substring(pos, m.Index - pos));
            if (m.Groups["c"].Success)
                yield return new Run(m.Groups["c"].Value) { FontFamily = CodeFont, Foreground = CodeBrush };
            else if (m.Groups["b"].Success)
                yield return new Bold(new Run(m.Groups["b"].Value));
            else if (m.Groups["i"].Success)
                yield return new Italic(new Run(m.Groups["i"].Value));
            else
                yield return new Italic(new Run(m.Groups["u"].Value));
            pos = m.Index + m.Length;
        }
        if (pos < s.Length)
            yield return new Run(s.Substring(pos));
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
