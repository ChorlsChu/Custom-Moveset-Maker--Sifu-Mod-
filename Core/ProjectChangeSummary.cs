using System;
using System.Collections.Generic;
using System.Linq;

namespace SifuMovesetEditor;

public class ChangeRow
{
    public string Title { get; set; } = "";
    public string DetailLeft { get; set; } = "";
    public string DetailRight { get; set; } = "";
    public bool ShowDetail { get; set; }
    public string ToolTip { get; set; } = "";
}

public class UnitSectionReview
{
    public string Name = "";
    public List<object> Rows = new();
    public int Count => Rows.Count;
}

public class UnitReview
{
    public string Key = "";
    public string DisplayName = "";
    public string Subtitle = "";
    public List<UnitSectionReview> Sections = new();
    public int Total => Sections.Sum(s => s.Count);
}

public static class ProjectChangeSummary
{
    /// <summary>Key of the top-level category that collects every unit's tuned AttackDB rows.</summary>
    public const string AttackDbCategoryKey = "__attackdbs";

    public static bool HasAttackDbTuning(ComboNode n)
        => n != null && (n.AttackBuildupFrames.HasValue || n.AttackGameplayRange.HasValue);

    public static bool GraphHasAttackDbTuning(ComboGraph? graph)
        => graph != null && graph.Nodes.Any(HasAttackDbTuning);

    /// <summary>
    /// One row per tuned field, showing the vanilla value it was changed from.
    /// Rows whose override already equals vanilla are dropped so a type-in of the
    /// current value never shows up as a change. Baseline is the card the node
    /// actually points at (swapped source counts), not its original default.
    /// </summary>
    public static List<object> BuildAttackDbRows(IEnumerable<ComboNode>? nodes, string contentPath)
    {
        var rows = new List<object>();
        if (nodes == null) return rows;

        var contentDir = Setup.ContentDetector.ResolveContentDir(contentPath);
        foreach (var n in nodes.Where(n => !n.IsRedirect && HasAttackDbTuning(n))
            .OrderBy(n => n.TreeIndex)
            .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var vanilla = AttackDbCard.ReadDbPath(contentDir, AttackDbCard.EffectiveCardPath(n));
            string name = string.IsNullOrEmpty(n.DisplayName) ? n.Name : n.DisplayName;

            if (n.AttackBuildupFrames.HasValue
                && (vanilla == null || !vanilla.HasBuildup || vanilla.Buildup != n.AttackBuildupFrames.Value))
            {
                rows.Add(MakeValueRow($"{name} buildup",
                    vanilla != null && vanilla.HasBuildup ? vanilla.Buildup.ToString() : "?",
                    n.AttackBuildupFrames.Value.ToString()));
            }
            if (n.AttackGameplayRange.HasValue
                && (vanilla == null || !vanilla.HasRange
                    || Math.Abs(vanilla.Range - n.AttackGameplayRange.Value) > 0.0001f))
            {
                rows.Add(MakeValueRow($"{name} gameplay range",
                    vanilla != null && vanilla.HasRange ? vanilla.Range.ToString("0.###") : "?",
                    n.AttackGameplayRange.Value.ToString("0.###")));
            }
        }
        return rows;
    }

    public static List<UnitReview> BuildFromUnitCaches(
        Dictionary<string, UnitCacheEntry> caches,
        string contentPath)
    {
        var units = new List<UnitReview>();
        if (caches == null || caches.Count == 0) return units;

        // Weapon keys share a variant, so they share one set of ArchetypeDB files - one props row each.
        var propsVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Tuning lives in shared card files, so it gets its own top-level category with one
        // subcategory per unit instead of sitting inside each unit's sections.
        var attackByUnit = new List<(string UnitKey, List<object> Rows)>();

        foreach (var kvp in caches)
        {
            string unitKey = kvp.Key;
            var entry = kvp.Value;
            var graph = entry.Graph;
            if (graph == null) continue;

            var modified = graph.Nodes
                .Where(n => !n.IsRoot && !string.IsNullOrEmpty(n.AnimPath)
                    && (n.TreeIndex == -1 || n.AnimPath != n.DefaultAnimPath
                        || (!string.IsNullOrEmpty(n.VanillaAnimPath) && n.AnimPath != n.VanillaAnimPath)
                        || (!string.IsNullOrEmpty(n.DefaultDBPath) && !string.IsNullOrEmpty(n.SourceDBPath)
                            && !string.Equals(n.SourceDBPath, n.DefaultDBPath, StringComparison.OrdinalIgnoreCase))))
                .OrderBy(n => n.TreeIndex)
                .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var retargetRows = BuildRetargetRows(graph);
            var attackRows = BuildAttackDbRows(graph.Nodes, contentPath);
            if (attackRows.Count > 0) attackByUnit.Add((unitKey, attackRows));

            var keyParts = unitKey.Split('|');
            string variant = keyParts.Length > 1 ? keyParts[1] : unitKey;
            var propRows = new List<object>();
            if (entry.Props != null)
                AddUnitPropsEntries(propRows, entry.Props, contentPath, variant);
            if (propRows.Count > 0 && !propsVariants.Add(variant))
                propRows.Clear();

            if (modified.Count == 0 && retargetRows.Count == 0 && propRows.Count == 0)
                continue;

            var unit = new UnitReview
            {
                Key = unitKey,
                DisplayName = FormatUnitDisplayName(unitKey),
                Subtitle = ""
            };

            if (modified.Count > 0)
                unit.Sections.Add(new UnitSectionReview
                {
                    Name = "Moves",
                    Rows = modified.Select(ToChangeRow).Cast<object>().ToList()
                });

            if (retargetRows.Count > 0)
                unit.Sections.Add(new UnitSectionReview { Name = "Retargets", Rows = retargetRows });

            if (propRows.Count > 0)
                unit.Sections.Add(new UnitSectionReview { Name = "Unit Props", Rows = propRows });

            if (unit.Total > 0)
                units.Add(unit);
        }

        var ordered = units
            .OrderBy(u => u.Key.StartsWith("MainChar", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var attackCategory = BuildAttackDbCategory(attackByUnit);
        if (attackCategory != null) ordered.Add(attackCategory);
        return ordered;
    }

    /// <summary>
    /// The top-level "AttackDBs" category: one subcategory per unit that owns tuned nodes,
    /// rows inside showing vanilla → tuned value. Appended after the unit categories.
    /// </summary>
    public static UnitReview? BuildAttackDbCategory(List<(string UnitKey, List<object> Rows)>? attackByUnit)
    {
        if (attackByUnit == null || attackByUnit.Count == 0) return null;

        var category = new UnitReview
        {
            Key = AttackDbCategoryKey,
            DisplayName = "AttackDBs",
            Subtitle = ""
        };
        foreach (var (unitKey, rows) in attackByUnit
            .OrderBy(u => u.UnitKey.StartsWith("MainChar", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(u => FormatUnitDisplayName(u.UnitKey), StringComparer.OrdinalIgnoreCase))
        {
            category.Sections.Add(new UnitSectionReview
            {
                Name = FormatUnitDisplayName(unitKey),
                Rows = rows
            });
        }
        return category;
    }

    public static List<object> BuildList(
        List<UnitReview> units,
        HashSet<string> expandedUnits,
        Dictionary<string, HashSet<string>> expandedSections)
    {
        var entries = new List<object>();

        foreach (var unit in units)
        {
            bool unitOpen = expandedUnits != null && expandedUnits.Contains(unit.Key);
            entries.Add(new GroupHeader
            {
                Name = unit.DisplayName,
                Level = 1,
                Count = unit.Total,
                Subtitle = unit.Subtitle,
                IsExpanded = unitOpen,
                ParentName = unit.Key
            });

            if (!unitOpen) continue;

            foreach (var section in unit.Sections)
            {
                bool sectionOpen = expandedSections != null
                    && expandedSections.TryGetValue(unit.Key, out var set)
                    && set.Contains(section.Name);
                entries.Add(new GroupHeader
                {
                    Name = section.Name,
                    Level = 2,
                    Count = section.Count,
                    IsExpanded = sectionOpen,
                    ParentName = unit.Key
                });

                if (sectionOpen)
                    entries.AddRange(section.Rows);
            }
        }

        return entries;
    }

    public static object ToChangeRow(ComboNode n)
    {
        string oldName = string.IsNullOrEmpty(n.DisplayName) ? n.Name : n.DisplayName;
        string newName = string.IsNullOrEmpty(n.ImportedDisplayName) ? oldName : n.ImportedDisplayName;
        string oldPath = n.DefaultAnimPath;
        string newPath = n.AnimPath;
        bool renamed = !string.Equals(oldName, newName, StringComparison.Ordinal);

        if (renamed)
        {
            return new ChangeRow
            {
                Title = $"{oldName} → {newName}",
                ShowDetail = false,
                ToolTip = string.IsNullOrEmpty(oldPath) && string.IsNullOrEmpty(newPath)
                    ? newName
                    : $"{oldPath} → {newPath}"
            };
        }

        return new ChangeRow
        {
            Title = oldName,
            ShowDetail = !string.IsNullOrEmpty(oldPath) || !string.IsNullOrEmpty(newPath),
            DetailLeft = oldPath,
            DetailRight = newPath,
            ToolTip = $"{oldPath} → {newPath}"
        };
    }

    public static ChangeRow MakeValueRow(string title, string left, string right, string? toolTip = null)
    {
        return new ChangeRow
        {
            Title = title,
            DetailLeft = left,
            DetailRight = right,
            ShowDetail = true,
            ToolTip = toolTip ?? $"{left} → {right}"
        };
    }

    public static List<object> BuildRetargetRows(ComboGraph graph)
    {
        var rows = new List<object>();
        if (graph?.RedirectOriginalTargets == null) return rows;

        foreach (var rd in graph.RedirectOriginalTargets)
        {
            var node = graph.Nodes.FirstOrDefault(n => n.Id == rd.Key);
            if (node == null || node.ResolvedRedirectNodeId < 0 || node.ResolvedRedirectNodeId == rd.Value)
                continue;

            var origTarget = graph.Nodes.FirstOrDefault(n => n.Id == rd.Value);
            var newTarget = graph.Nodes.FirstOrDefault(n => n.Id == node.ResolvedRedirectNodeId);
            rows.Add(MakeValueRow(
                $"Retarget: {node.DisplayName}",
                $"m_NodeRedirect {origTarget?.TreeIndex ?? -1} → {origTarget?.DisplayName ?? "?"}",
                $"m_NodeRedirect {newTarget?.TreeIndex ?? -1} → {newTarget?.DisplayName ?? "?"}"));
        }
        return rows;
    }

    public static string FormatUnitDisplayName(string unitKey)
    {
        var parts = unitKey.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return unitKey;

        if (parts[0].Equals("MainChar", StringComparison.OrdinalIgnoreCase))
        {
            string weapon = parts.Length > 1 ? parts[1] : "";
            if (weapon.StartsWith("MainChar_", StringComparison.OrdinalIgnoreCase))
                weapon = weapon["MainChar_".Length..];
            return string.IsNullOrEmpty(weapon) ? "MainChar" : $"MainChar | {weapon}";
        }

        if (parts.Length == 1) return parts[0];

        string variant = parts[1];
        if (variant.StartsWith(parts[0] + "_", StringComparison.OrdinalIgnoreCase))
            variant = variant[(parts[0].Length + 1)..];

        string label = $"{parts[0]} | {variant}";
        if (parts.Length > 2)
        {
            string weaponPart = parts[2];
            if (weaponPart.Contains('_'))
                weaponPart = weaponPart[(weaponPart.IndexOf('_') + 1)..];
            if (!weaponPart.Equals(variant, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(weaponPart, "Barehands", StringComparison.OrdinalIgnoreCase))
                label += $" | {weaponPart}";
        }
        return label;
    }

    public static int CountUnitPropsChanges(UnitProperties props, string contentPath, string variantTag)
    {
        var vanilla = UnitPropertiesManager.Read(contentPath, variantTag);
        int count = 0;
        if (props.Health.HasValue && (!vanilla.Health.HasValue || Math.Abs(props.Health.Value - vanilla.Health.Value) > 0.01f)) count++;
        if (props.Structure.HasValue && (!vanilla.Structure.HasValue || Math.Abs(props.Structure.Value - vanilla.Structure.Value) > 0.01f)) count++;
        if (props.MemoryLimit.HasValue && (!vanilla.MemoryLimit.HasValue || Math.Abs(props.MemoryLimit.Value - vanilla.MemoryLimit.Value) > 0.01f)) count++;
        if (props.HitsCount.HasValue && (!vanilla.HitsCount.HasValue || props.HitsCount.Value != vanilla.HitsCount.Value)) count++;
        if (props.MemoryFlushLimit.HasValue && (!vanilla.MemoryFlushLimit.HasValue || Math.Abs(props.MemoryFlushLimit.Value - vanilla.MemoryFlushLimit.Value) > 0.01f)) count++;
        if (UnitPropertiesManager.GetEffectiveFocus(contentPath, variantTag) != (vanilla.ImmuneToFocus ?? false)) count++;
        return count;
    }

    public static void AddUnitPropsEntries(List<object> entries, UnitProperties props, string contentPath, string variantTag)
    {
        var vanilla = UnitPropertiesManager.Read(contentPath, variantTag);
        if (props.Health.HasValue && (!vanilla.Health.HasValue || Math.Abs(props.Health.Value - vanilla.Health.Value) > 0.01f))
            entries.Add(MakeValueRow("Health", $"{vanilla.Health?.ToString("F1") ?? "null"}", $"{props.Health.Value:F1}"));
        if (props.Structure.HasValue && (!vanilla.Structure.HasValue || Math.Abs(props.Structure.Value - vanilla.Structure.Value) > 0.01f))
            entries.Add(MakeValueRow("Structure", $"{vanilla.Structure?.ToString("F1") ?? "null"}", $"{props.Structure.Value:F1}"));
        if (props.MemoryLimit.HasValue && (!vanilla.MemoryLimit.HasValue || Math.Abs(props.MemoryLimit.Value - vanilla.MemoryLimit.Value) > 0.01f))
            entries.Add(MakeValueRow("MemoryLimit", $"{vanilla.MemoryLimit?.ToString("F1") ?? "null"}", $"{props.MemoryLimit.Value:F1}"));
        if (props.HitsCount.HasValue && (!vanilla.HitsCount.HasValue || props.HitsCount.Value != vanilla.HitsCount.Value))
            entries.Add(MakeValueRow("HitsCount", $"{vanilla.HitsCount?.ToString() ?? "null"}", $"{props.HitsCount.Value}"));
        if (props.MemoryFlushLimit.HasValue && (!vanilla.MemoryFlushLimit.HasValue || Math.Abs(props.MemoryFlushLimit.Value - vanilla.MemoryFlushLimit.Value) > 0.01f))
            entries.Add(MakeValueRow("FlushLimit", $"{vanilla.MemoryFlushLimit?.ToString("F1") ?? "null"}", $"{props.MemoryFlushLimit.Value:F1}"));
        // Family-wide flag: compare the effective value, not this unit's (possibly stale) copy.
        bool focusNow = UnitPropertiesManager.GetEffectiveFocus(contentPath, variantTag);
        bool focusVanilla = vanilla.ImmuneToFocus ?? false;
        if (focusNow != focusVanilla)
        {
            entries.Add(MakeValueRow("ImmuneToFocus",
                focusVanilla ? "Off" : "On",
                focusNow ? "On" : "Off",
                "Repoints the family's shared ArchetypeDB at Yang's empty VitalPointDB, so the unit has no Focus target. Applied to the exported pak."));

            var family = UnitPropertiesManager.GetFocusFamilyMembers(contentPath, variantTag)
                .Where(m => !m.Equals(variantTag, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (focusNow && family.Count > 0)
                entries.Add(MakeValueRow("ImmuneToFocus (shared family)", "-", "-",
                    "One flag covers the whole family, so these variants become immune too: "
                    + string.Join(", ", family)));
        }
    }
}
