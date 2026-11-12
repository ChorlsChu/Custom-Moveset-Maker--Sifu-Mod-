using System;
using System.Collections.Generic;
using System.Linq;

namespace SifuMovesetEditor;

/// <summary>
/// Canonical EComboTransition values, shared by node links and start-entry rows.
///
/// The model always stores the bare enum name ("Light", "HeavyHold") - never a
/// display label. Display labels are for the graph/dialog only, and an unknown
/// enum value passes through verbatim so an imported tree never silently renders
/// as "None".
///
/// "UsedByVanilla" is false for enum values that exist on the type but appear in
/// no shipped *Combo* asset (280 files scanned: only None, Light, Heavy, HeavyAlt,
/// HeavyHold, LightRushAttack and HeavyRushAttack occur). Those choices stay
/// selectable but the dialog warns that the engine never sees them in vanilla.
/// </summary>
internal static class InputCatalog
{
    internal sealed record Choice(string Label, string EnumName, string MenuLabel, bool UsedByVanilla);

    /// <summary>
    /// Ordered picker catalog. The "Default" choice doubles as the empty/None
    /// entry: its label is "" so it displays as "(no input)" in row combos.
    /// </summary>
    internal static readonly IReadOnlyList<Choice> Choices = new List<Choice>
    {
        new("", "None", "Default (no input)", true),
        new("LMB", "Light", "LMB (Light)", true),
        new("RMB", "Heavy", "RMB (Heavy)", true),
        // HeavyAlt must never display as plain "RMB": a rewritten HeavyAlt row that
        // reads as "RMB" would silently export as Heavy.
        new("RMB Alt", "HeavyAlt", "RMB Alt (HeavyAlt)", true),
        new("RMB Hold", "HeavyHold", "RMB Hold (HeavyHold)", true),
        new("LMB Rush", "LightRushAttack", "LMB Rush (LightRushAttack)", true),
        new("RMB Rush", "HeavyRushAttack", "RMB Rush (HeavyRushAttack)", true),
        new("S", "Special", "S (Special) - unused by vanilla combos", false),
        new("Shift", "Dodge", "Shift (Dodge) - unused by vanilla combos", false),
        new("Q", "Throw", "Q (Throw) - unused by vanilla combos", false)
    };

    /// <summary>The "no input" choice (None).</summary>
    internal static Choice None => Choices[0];

    /// <summary>Menu entry that switches a combo into raw-name entry mode.</summary>
    internal const string CustomMenuLabel = "Custom…";

    /// <summary>Raw enum name for a label/menu label, or the label itself verbatim
    /// when it is not in the catalog (a custom raw name). "" maps to "None".</summary>
    internal static string EnumFromLabel(string? label)
    {
        if (string.IsNullOrEmpty(label)) return None.EnumName;
        foreach (var c in Choices)
        {
            if (string.Equals(c.Label, label, StringComparison.Ordinal)) return c.EnumName;
            if (string.Equals(c.MenuLabel, label, StringComparison.Ordinal)) return c.EnumName;
        }
        // Lenient aliases for callers that pass an enum name straight through.
        // Case-insensitive so a typed "light" still exports the canonical "Light".
        foreach (var c in Choices)
            if (string.Equals(c.EnumName, label, StringComparison.OrdinalIgnoreCase)) return c.EnumName;
        return label.Trim();
    }

    /// <summary>Display label for a bare enum name (or an "Enum::Value" FName).
    /// Unknown names pass through verbatim; empty/None map to "".</summary>
    internal static string LabelFor(string? enumName)
    {
        if (string.IsNullOrEmpty(enumName)) return "";
        var name = enumName.Contains("::") ? enumName.Split("::")[^1] : enumName;
        var choice = Choices.FirstOrDefault(c => string.Equals(c.EnumName, name, StringComparison.OrdinalIgnoreCase));
        return choice?.Label ?? name;
    }

    /// <summary>Menu label for an enum name (used by the start-entry picker).</summary>
    internal static string MenuLabelFor(string? enumName)
    {
        if (string.IsNullOrEmpty(enumName)) return None.MenuLabel;
        var name = enumName.Contains("::") ? enumName.Split("::")[^1] : enumName;
        var choice = Choices.FirstOrDefault(c => string.Equals(c.EnumName, name, StringComparison.OrdinalIgnoreCase));
        return choice?.MenuLabel ?? name;
    }

    /// <summary>Comparison form: "" and "None" are the same input (no input).</summary>
    internal static string Normalize(string? enumName)
        => string.IsNullOrEmpty(enumName) ? None.EnumName : enumName;

    /// <summary>
    /// Canonical grouping key for the player's per-input probability sums. Accepts a
    /// bare enum ("Light"), an "Enum::Value" FName, or a display label ("LMB") - the
    /// graph rows carry labels, start-entry rows carry bare enums, and both must group
    /// together. "" / "None" both map to "None"; an unknown name passes through so an
    /// imported tree keeps its own input instead of being merged into another group.
    /// </summary>
    internal static string GroupKey(string? input)
    {
        if (string.IsNullOrEmpty(input)) return None.EnumName;
        var name = input.Contains("::") ? input.Split("::")[^1] : input;
        return Normalize(EnumFromLabel(name));
    }

    /// <summary>Human label for a grouping key returned by <see cref="GroupKey"/>.
    /// None reads "(no input)" so a header chip is never blank.</summary>
    internal static string GroupLabel(string? groupKey)
    {
        string label = LabelFor(groupKey);
        return string.IsNullOrEmpty(label) || string.Equals(groupKey, None.EnumName,
            StringComparison.OrdinalIgnoreCase) ? "(no input)" : label;
    }

    /// <summary>False only for a known value no vanilla combo asset uses.</summary>
    internal static bool IsVanillaUsed(string? enumName)
    {
        if (string.IsNullOrEmpty(enumName)) return true;
        var name = enumName.Contains("::") ? enumName.Split("::")[^1] : enumName;
        var choice = Choices.FirstOrDefault(c => string.Equals(c.EnumName, name, StringComparison.OrdinalIgnoreCase));
        return choice?.UsedByVanilla ?? false;
    }

    /// <summary>True when the name is one the catalog knows (including None).</summary>
    internal static bool IsKnown(string? enumName)
    {
        if (string.IsNullOrEmpty(enumName)) return true;
        var name = enumName.Contains("::") ? enumName.Split("::")[^1] : enumName;
        return Choices.Any(c => string.Equals(c.EnumName, name, StringComparison.OrdinalIgnoreCase));
    }
}
