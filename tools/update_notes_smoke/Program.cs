using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Documents;
using SifuMovesetEditor.Update;

namespace update_notes_smoke;

/// <summary>
/// Pins the behaviour of the "What's new" markdown renderer. The update card only
/// understands a small subset of GitHub markdown, so these checks keep the parser
/// honest: headings, bullets, rules and code spans render as styled inlines, while
/// everything else (including unmatched markers and snake_case) stays literal.
/// Exit code 0 = all passed.
/// </summary>
internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--file")
            return CheckFile(args[1]);

        Console.WriteLine("== update notes renderer ==");

        CheckInlines();
        CheckHeadings();
        CheckBlocks();
        CheckCodeSpans();
        CheckLiterals();
        CheckLayout();

        Console.WriteLine(_failures == 0 ? "ALL UPDATE NOTES SMOKE TESTS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckInlines()
    {
        Console.WriteLine("  -- inline emphasis --");
        var bold = MarkdownInlines.Render("**bold**");
        Check(bold.Count == 1 && bold[0] is Bold, "**bold** becomes one Bold");
        Check(Text(bold) == "bold", "bold keeps its text");

        var star = MarkdownInlines.Render("*italic*");
        Check(star.Count == 1 && star[0] is Italic, "*italic* becomes one Italic");
        Check(Text(star) == "italic", "italic keeps its text");

        var under = MarkdownInlines.Render("an _emphatic_ word");
        Check(Text(under) == "an emphatic word", "_italic_ text round-trips");
        Check(Has(under, i => i is Italic), "_italic_ becomes an Italic");

        var mixed = MarkdownInlines.Render("plain **bold** and *italic* tail");
        Check(Text(mixed) == "plain bold and italic tail", "mixed emphasis keeps surrounding text");
    }

    private static void CheckHeadings()
    {
        Console.WriteLine("  -- headings --");
        var h1 = MarkdownInlines.Render("# Title");
        Check(h1.Count == 1 && h1[0] is Bold, "# heading becomes one Bold");
        Check(Text(h1) == "Title", "# heading drops the hash and space");
        Check(Size(h1[0]) == MarkdownInlines.Heading1Size, "# heading uses the H1 size");

        var h2 = MarkdownInlines.Render("## Section");
        Check(Text(h2) == "Section", "## heading drops the hashes");
        Check(Size(h2[0]) == MarkdownInlines.Heading2Size, "## heading uses the H2 size");

        var h3 = MarkdownInlines.Render("### Feature - detail");
        Check(Text(h3) == "Feature - detail", "### heading keeps inner dashes");
        Check(Size(h3[0]) == MarkdownInlines.Heading3Size, "### heading uses the H3 size");

        Check(MarkdownInlines.Heading1Size > MarkdownInlines.Heading2Size
              && MarkdownInlines.Heading2Size > MarkdownInlines.Heading3Size,
            "heading sizes descend H1 > H2 > H3");

        var inner = MarkdownInlines.Render("### **Bold** feature");
        Check(Text(inner) == "Bold feature", "heading keeps inline emphasis");
        Check(Has(inner, i => Text(new[] { i }) == "Bold feature"), "heading content stays inside the block");
    }

    private static void CheckBlocks()
    {
        Console.WriteLine("  -- bullets & rules --");
        var dash = MarkdownInlines.Render("- item");
        Check(Text(dash) == MarkdownInlines.BulletGlyph + "item", "- bullet gets the bullet glyph");
        Check(dash.Count >= 1 && dash[0] is Run r1 && r1.Text == MarkdownInlines.BulletGlyph,
            "bullet glyph is its own run");

        var star = MarkdownInlines.Render("* item");
        Check(Text(star) == MarkdownInlines.BulletGlyph + "item", "* bullet gets the bullet glyph");

        var boldBullet = MarkdownInlines.Render("- **x** tail");
        Check(Text(boldBullet) == MarkdownInlines.BulletGlyph + "x tail", "bullet keeps inline emphasis");
        Check(Has(boldBullet, i => i is Bold), "bullet content still parses bold");

        var rule = MarkdownInlines.Render("---");
        Check(rule.Count == 1, "--- becomes a single run");
        Check(Text(rule) == MarkdownInlines.RuleGlyph, "--- becomes the rule glyph");

        Check(Text(MarkdownInlines.Render("***")) == MarkdownInlines.RuleGlyph, "*** becomes the rule glyph");
    }

    private static void CheckCodeSpans()
    {
        Console.WriteLine("  -- code spans --");
        var code = MarkdownInlines.Render("see `_HitBoxData` here");
        Check(Text(code) == "see _HitBoxData here", "code span drops its backticks");
        var run = FindRun(code, "Consolas");
        Check(run != null, "code span uses a monospace font");
        Check(run != null && run.Text == "_HitBoxData", "code span keeps its content");

        var trap = MarkdownInlines.Render("`**x**`");
        Check(Text(trap) == "**x**", "code span keeps asterisks literal");
        Check(!Has(trap, i => i is Bold), "code span is never parsed as bold");

        var mixed = MarkdownInlines.Render("plain **b** then `c`");
        Check(Text(mixed) == "plain b then c", "code span and bold coexist");
        Check(Has(mixed, i => i is Bold), "bold before a code span still works");
    }

    private static void CheckLiterals()
    {
        Console.WriteLine("  -- literal text --");
        Check(Text(MarkdownInlines.Render("plain text")) == "plain text", "plain text is untouched");
        Check(Text(MarkdownInlines.Render("**unclosed")) == "**unclosed", "unmatched bold stays literal");
        Check(!Has(MarkdownInlines.Render("**unclosed"), i => i is Bold), "unmatched bold makes no Bold");
        Check(Text(MarkdownInlines.Render("some_var_name")) == "some_var_name", "snake_case is untouched");
        Check(!Has(MarkdownInlines.Render("some_var_name"), i => i is Italic), "snake_case makes no Italic");
        Check(Text(MarkdownInlines.Render("a - b")) == "a - b", "inner dash is not a bullet");
        Check(Text(MarkdownInlines.Render("Start Entries - one pool")) == "Start Entries - one pool",
            "spaced dash mid-line stays literal");
        Check(Text(MarkdownInlines.Render("Σ→100% · 100%")) == "Σ→100% · 100%", "unicode survives");
        Check(MarkdownInlines.Render("").Count == 0, "empty input renders nothing");
    }

    private static void CheckLayout()
    {
        Console.WriteLine("  -- layout --");
        Check(Text(MarkdownInlines.Render("a\nb")) == "a\nb", "LF lines keep their breaks");
        Check(Text(MarkdownInlines.Render("a\r\nb")) == Text(MarkdownInlines.Render("a\nb")),
            "CRLF renders like LF");
        Check(Text(MarkdownInlines.Render("a\r\n\r\nb")) == "a\n\nb", "blank lines survive");
        Check(Text(MarkdownInlines.Render("# H\n- one\n\nplain")) == "H\n" + MarkdownInlines.BulletGlyph + "one\n\nplain",
            "mixed blocks keep their line order");

        // A real release body, cut down: nothing markdown-ish should leak through.
        const string sample =
            "# Sifu Moveset Editor - v1.4 Patch Notes\n" +
            "\n" +
            "## New Features\n" +
            "\n" +
            "### MainChar Mixups\n" +
            "- Player trees roll a **separate chance set per input**.\n" +
            "- Code like `m_Name` and `_2` stays literal.\n" +
            "\n" +
            "## Fixes\n" +
            "\n" +
            "---\n" +
            "\n" +
            "Known issue: **LMB Hold** is not offered.";
        var whole = Text(MarkdownInlines.Render(sample));
        Check(!whole.Contains('#'), "sample body leaks no heading hash");
        Check(!whole.Contains('`'), "sample body leaks no backtick");
        Check(!whole.Split('\n').Any(l => l.StartsWith("- ", StringComparison.Ordinal)),
            "sample body leaks no bullet dash");
        Check(whole.Contains(MarkdownInlines.BulletGlyph), "sample body renders bullets");
        Check(whole.Contains(MarkdownInlines.RuleGlyph), "sample body renders its rule");
        Check(whole.Contains("m_Name"), "sample body keeps code text");
    }

    /// <summary>Renders a real release body and reports any markdown that leaked as literal text.</summary>
    private static int CheckFile(string path)
    {
        var text = File.ReadAllText(path);
        var inlines = MarkdownInlines.Render(text);
        var flat = Text(inlines);
        var lines = flat.Split('\n');

        Console.WriteLine($"== release body: {path} ==");
        Console.WriteLine($"  {text.Length} chars in, {lines.Length} lines out");

        Check(!lines.Any(l => l.StartsWith('#')), "no literal heading hash");
        Check(!flat.Contains('`'), "no literal backtick");
        Check(!lines.Any(l => l.StartsWith("- ", StringComparison.Ordinal)), "no literal bullet dash");
        Check(!flat.Contains("**"), "no literal bold marker");
        Check(lines.Any(l => l.StartsWith(MarkdownInlines.BulletGlyph, StringComparison.Ordinal)), "bullets rendered");
        Check(flat.Trim().Length > text.Length / 2, "most of the body survives rendering");

        Console.WriteLine(_failures == 0 ? "RELEASE BODY RENDERS CLEAN" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Flattens every inline (including Bold/Italic children) to plain text.</summary>
    private static string Text(IEnumerable<Inline> inlines)
    {
        var sb = new StringBuilder();
        Append(sb, inlines);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Span span) Append(sb, span.Inlines);
            else if (inline is Run run) sb.Append(run.Text);
        }
    }

    private static bool Has(IEnumerable<Inline> inlines, Func<Inline, bool> predicate)
    {
        foreach (var inline in inlines)
        {
            if (predicate(inline)) return true;
            if (inline is Span span && Has(span.Inlines, predicate)) return true;
        }
        return false;
    }

    private static Run? FindRun(IEnumerable<Inline> inlines, string fontHint)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run && run.FontFamily.Source.Contains(fontHint, StringComparison.OrdinalIgnoreCase))
                return run;
            if (inline is Span span)
            {
                var found = FindRun(span.Inlines, fontHint);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static double Size(Inline inline) => inline is TextElement te ? te.FontSize : double.NaN;

    private static void Check(bool condition, string message)
    {
        if (condition)
        {
            Console.WriteLine($"  PASS {message}");
            return;
        }
        _failures++;
        Console.WriteLine($"  FAIL {message}");
    }
}
