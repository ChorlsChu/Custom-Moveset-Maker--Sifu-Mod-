using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SifuMovesetEditor;

/// <summary>
/// Character-level filtering for numeric TextBoxes: blocks non-numeric keystrokes
/// and sanitizes paste. Range checks stay at commit time (TryParseInt/Float);
/// this only keeps garbage characters out of the box while typing.
/// </summary>
public static class InputFilter
{
    /// <summary>Digits only; optional leading '-' when allowNegative.</summary>
    public static void ApplyInt(TextBox box, bool allowNegative = false)
        => Apply(box, allowNegative, allowDot: false);

    /// <summary>Digits + one '.'; optional leading '-' when allowNegative.</summary>
    public static void ApplyFloat(TextBox box, bool allowNegative = false)
        => Apply(box, allowNegative, allowDot: true);

    /// <summary>
    /// Digits, '.', and ',' (max 2 commas) for the hit-window damage/structure
    /// mirror boxes: one value applies to all three impact paths, or type
    /// "hit,guard,deflect". One dot per comma-segment. Range checks stay at
    /// commit time; this only keeps garbage characters out while typing.
    /// </summary>
    public static void ApplyFloatTriplet(TextBox box)
    {
        box.PreviewTextInput += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return; // Ctrl+A/C/V/X/Z etc
            // Validate the prospective text (selection replaced by typed chars)
            // so mid-edit states like "13," are allowed but "1,2,3,4" is not.
            string next = box.Text.Remove(box.SelectionStart, box.SelectionLength)
                .Insert(box.SelectionStart, e.Text);
            e.Handled = !IsTripletValid(next);
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) e.Handled = true;
        };
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (e.DataObject.GetDataPresent(DataFormats.Text))
            {
                string raw = (string)e.DataObject.GetData(DataFormats.Text)!;
                string clean = SanitizeTriplet(raw);
                if (clean.Length == 0 && raw.Trim().Length > 0) { e.CancelCommand(); return; }
                if (clean != raw) e.DataObject.SetData(DataFormats.Text, clean);
            }
        });
    }

    /// <summary>Only digits, at most one '.' per comma-segment, at most 2 ','.</summary>
    private static bool IsTripletValid(string s)
    {
        int commas = 0;
        bool dotInSegment = false;
        foreach (char c in s)
        {
            if (c >= '0' && c <= '9') continue;
            if (c == '.') { if (dotInSegment) return false; dotInSegment = true; continue; }
            if (c == ',') { if (++commas > 2) return false; dotInSegment = false; continue; }
            return false;
        }
        return true;
    }

    /// <summary>Strip spaces/letters/symbols; cap at 2 commas and one '.' per segment.</summary>
    private static string SanitizeTriplet(string raw)
    {
        var sb = new System.Text.StringBuilder();
        int commas = 0;
        bool dotInSegment = false;
        foreach (char c in raw)
        {
            if (c >= '0' && c <= '9') { sb.Append(c); continue; }
            if (c == '.' && !dotInSegment) { sb.Append(c); dotInSegment = true; continue; }
            if (c == ',' && commas < 2) { sb.Append(c); commas++; dotInSegment = false; continue; }
        }
        return sb.ToString();
    }

    private static void Apply(TextBox box, bool allowNegative, bool allowDot)
    {
        box.PreviewTextInput += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return; // Ctrl+A/C/V/X/Z etc
            e.Handled = !IsAllowed(e.Text, box, allowNegative, allowDot);
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) e.Handled = true;
        };
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (e.DataObject.GetDataPresent(DataFormats.Text))
            {
                string raw = (string)e.DataObject.GetData(DataFormats.Text)!;
                string clean = Sanitize(raw, box, allowNegative, allowDot);
                if (clean.Length == 0 && raw.Trim().Length > 0) { e.CancelCommand(); return; }
                if (clean != raw) e.DataObject.SetData(DataFormats.Text, clean);
            }
        });
    }

    private static bool IsAllowed(string text, TextBox box, bool allowNegative, bool allowDot)
    {
        foreach (char c in text)
        {
            if (c >= '0' && c <= '9') continue;
            if (allowDot && c == '.' && !box.Text.Contains('.')) continue;
            if (allowNegative && c == '-' && box.SelectionStart == 0 && !box.Text.Contains('-')) continue;
            return false;
        }
        return true;
    }

    private static string Sanitize(string raw, TextBox box, bool allowNegative, bool allowDot)
    {
        var sb = new System.Text.StringBuilder();
        bool seenDot = box.Text.Contains('.');
        bool seenMinus = box.Text.Contains('-');
        foreach (char c in raw)
        {
            if (c >= '0' && c <= '9') { sb.Append(c); continue; }
            if (allowDot && c == '.' && !seenDot) { sb.Append(c); seenDot = true; continue; }
            if (allowNegative && c == '-' && !seenMinus && sb.Length == 0) { sb.Append(c); seenMinus = true; }
        }
        return sb.ToString();
    }
}
