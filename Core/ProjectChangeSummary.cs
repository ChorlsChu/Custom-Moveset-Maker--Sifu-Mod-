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
        => n != null && (n.AttackBuildupFrames.HasValue || n.AttackGameplayRange.HasValue
            || (n.HitEdits != null && n.HitEdits.Any(e => e != null && !e.IsEmpty())));

    public static bool GraphHasAttackDbTuning(ComboGraph? graph)
        => graph != null && graph.Nodes.Any(HasAttackDbTuning);

    /// <summary>
    /// True when any start entry (m_ComboStartNodesContainer) row was added, removed,
    /// retargeted or reweighted in the editor. Diffed against the parse-time Original* snapshot.
    /// </summary>
    public static bool GraphHasStartEntryChanges(ComboGraph? graph)
        => graph?.StartEntries != null
           && graph.StartEntries.Any(e => e.Removed || e.Transitions.Any(t => t.IsModified()));

    /// <summary>
    /// One row per edited start transition: added/removed rows plus vanilla -> edited
    /// pairs for probability and target changes.
    /// </summary>
    public static List<object> BuildStartEntryRows(ComboGraph? graph)
    {
        var rows = new List<object>();
        if (graph?.StartEntries == null) return rows;

        foreach (var entry in graph.StartEntries)
        {
            if (entry.Removed)
            {
                rows.Add(MakeValueRow($"start entry '{entry.Label}': removed",
                    TicketShort(entry.TicketEnumValue), "removed",
                    "The entry is dropped from m_ComboStartNodesContainer on export."));
                continue;
            }
            if (entry.VanillaIndex < 0)
            {
                rows.Add(MakeValueRow($"start entry '{entry.Label}': added",
                    "new", TicketShort(entry.TicketEnumValue),
                    "New entry appended to m_ComboStartNodesContainer on export."));
                continue;
            }
            for (int i = 0; i < entry.Transitions.Count; i++)
            {
                var t = entry.Transitions[i];
                if (!t.IsModified()) continue;
                string num = t.OriginalIndex >= 0 ? $"#{t.OriginalIndex + 1}" : "#new";
                string title = string.IsNullOrEmpty(entry.Label) ? $"start {num}" : $"{entry.Label} {num}";

                if (t.OriginalIndex < 0 && !t.Removed)
                {
                    rows.Add(MakeValueRow($"{title}: new transition",
                        "added", $"→ {StartTargetName(graph, t, false)} ({t.Probability:0.###}%)"));
                    continue;
                }
                if (t.Removed)
                {
                    rows.Add(MakeValueRow($"{title}: transition removed",
                        $"→ {StartTargetName(graph, t, true)} ({t.OriginalProbability:0.###}%)", "removed"));
                    continue;
                }
                if (Math.Abs(t.Probability - t.OriginalProbability) > 0.001f)
                {
                    rows.Add(MakeValueRow($"{title}: probability",
                        $"{t.OriginalProbability:0.###}%", $"{t.Probability:0.###}%",
                        $"{entry.Label}: transition {num} probability"));
                }
                if (t.KeyCount > 1
                    && t.Keys.Count == t.KeyCount
                    && t.KeyTargetTreeIndices.Count == t.KeyCount
                    && t.OriginalKeyTargetTreeIndices.Count == t.KeyCount)
                {
                    // Multi-key conditional row: one change line per re-routed key (the
                    // primary Target* fields only mirror the first key's target).
                    for (int ki = 0; ki < t.KeyCount; ki++)
                    {
                        int was = t.OriginalKeyTargetTreeIndices[ki];
                        int now = t.KeyTargetTreeIndices[ki];
                        if (was == now) continue;
                        string nm = t.KeyTargetNames[ki];
                        rows.Add(MakeValueRow($"{title}: start target (key {t.Keys[ki]})",
                            $"tree[{was}]", $"tree[{now}]" + (string.IsNullOrEmpty(nm) ? "" : $" {nm}"),
                            $"{entry.Label}: transition {num} key {t.Keys[ki]} retargeted"));
                    }
                }
                else if (t.TargetNodeId != t.OriginalTargetNodeId)
                {
                    rows.Add(MakeValueRow($"{title}: start target",
                        StartTargetName(graph, t, true),
                        StartTargetName(graph, t, false),
                        $"{entry.Label}: transition {num} retargeted"));
                }
            }
        }
        return rows;
    }

    private static string TicketShort(string ticketEnumValue)
        => string.IsNullOrEmpty(ticketEnumValue) ? "?"
            : ticketEnumValue.Contains("::") ? ticketEnumValue.Split("::")[^1] : ticketEnumValue;

    private static string StartTargetName(ComboGraph graph, StartTransition t, bool original)
    {
        int id = original ? t.OriginalTargetNodeId : t.TargetNodeId;
        int tree = original ? t.OriginalTargetTreeIndex : t.TargetTreeIndex;
        string nm = original ? t.OriginalTargetName : t.TargetName;
        if (id >= 0)
        {
            var n = graph.Nodes.FirstOrDefault(x => x.Id == id);
            if (n != null) return NodeLabel(n, id);
        }
        if (tree >= 0) return string.IsNullOrEmpty(nm) ? $"tree[{tree}]" : $"tree[{tree}] {nm}";
        return string.IsNullOrEmpty(nm) ? "?" : nm;
    }

    /// <summary>
    /// Shared change detector used by the export gates and both change lists.
    /// Authored content (new attack/redirect nodes) always counts - those nodes may
    /// carry an empty AnimPath, so they are checked before the vanilla rules.
    /// </summary>
    public static bool IsModifiedNode(ComboNode? n)
        => n != null && !n.IsRoot && (n.Removed
            || n.Kind == ComboNodeKind.UserCreated
            || n.ReplacedByAiNode
            || (!string.IsNullOrEmpty(n.AnimPath)
                && (n.TreeIndex == -1 || n.AnimPath != n.DefaultAnimPath
                    || (!string.IsNullOrEmpty(n.VanillaAnimPath) && n.AnimPath != n.VanillaAnimPath)
                    || (!string.IsNullOrEmpty(n.DefaultDBPath) && !string.IsNullOrEmpty(n.SourceDBPath)
                        && !string.Equals(n.SourceDBPath, n.DefaultDBPath, StringComparison.OrdinalIgnoreCase)))));

    /// <summary>
    /// One row per tuned field, showing the vanilla value it was changed from.
    /// Rows whose override already equals vanilla are dropped so a type-in of the
    /// current value never shows up as a change - "equals" is display equality at
    /// the UI's "0.###" precision (the textbox round-trip loses vanilla's 4th decimal,
    /// and both sides of a row render at 3 decimals anyway). Baseline is the card the
    /// node actually points at (swapped source counts), not its original default; the
    /// CustomAssets copy is the fallback for custom cards, mirroring the panel/commit.
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
            string cardPath = AttackDbCard.EffectiveCardPath(n);
            var vanilla = AttackDbCard.ReadDbPath(contentDir, cardPath);
            if (vanilla == null || !vanilla.Any)
                vanilla = AttackDbCard.ReadDbPath(CustomAssets.Root, cardPath);
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
                    || !AttackDbCard.SameRange(vanilla.Range, n.AttackGameplayRange.Value)))
            {
                rows.Add(MakeValueRow($"{name} gameplay range",
                    vanilla != null && vanilla.HasRange ? AttackDbCard.FormatRange(vanilla.Range) : "?",
                    AttackDbCard.FormatRange(n.AttackGameplayRange.Value)));
            }
            if (n.HitEdits != null)
            {
                var hitCard = AttackHitCard.ReadDbPath(contentDir, cardPath);
                if (hitCard == null || hitCard.Windows.Count == 0)
                    hitCard = AttackHitCard.ReadDbPath(CustomAssets.Root, cardPath);
                foreach (var e in n.HitEdits.Where(e => e != null && !e.IsEmpty()).OrderBy(e => e.Index))
                {
                    var vanillaWin = hitCard != null && e.Index >= 0 && e.Index < hitCard.Windows.Count
                        ? hitCard.Windows[e.Index] : null;
                    rows.Add(BuildHitEditRow($"{name} hit {e.Index + 1}", e, vanillaWin));
                }
            }
        }
        return rows;
    }

    private static object BuildHitEditRow(string title, AttackHitEdit e, AttackHitWindowInfo? vanillaWin)
    {
        var parts = new List<string>();
        void Field(string label, string? now, string? was)
        {
            if (now == null) return;
            parts.Add(was == null ? $"{label} {now}" : $"{label} {was} → {now}");
        }
        if (e.FrameLo.HasValue || e.FrameHi.HasValue)
        {
            string was = vanillaWin != null ? $"{vanillaWin.FrameLo}-{vanillaWin.FrameHi}" : "?";
            string now = $"{e.FrameLo ?? (vanillaWin?.FrameLo ?? 0)}-{e.FrameHi ?? (vanillaWin?.FrameHi ?? 0)}";
            parts.Add($"frames {was} → {now}");
        }
        Field("damage", e.Damage?.ToString("0.###"), vanillaWin?.Damage?.ToString("0.###"));
        Field("structure", e.StructureDamage?.ToString("0.###"), vanillaWin?.StructureDamage?.ToString("0.###"));
        Field("dmg on hit", e.DamageOnHit?.ToString("0.###"), vanillaWin?.Damage?.ToString("0.###"));
        Field("dmg on guard", e.DamageOnGuard?.ToString("0.###"), vanillaWin?.GuardDamage?.ToString("0.###"));
        Field("dmg on deflect", e.DamageOnDeflect?.ToString("0.###"), vanillaWin?.DeflectDamage?.ToString("0.###"));
        Field("struct on hit", e.StructureDamageOnHit?.ToString("0.###"), vanillaWin?.StructureDamage?.ToString("0.###"));
        Field("struct on guard", e.StructureDamageOnGuard?.ToString("0.###"), vanillaWin?.GuardStructureDamage?.ToString("0.###"));
        Field("struct on deflect", e.StructureDamageOnDeflect?.ToString("0.###"), vanillaWin?.DeflectStructureDamage?.ToString("0.###"));
        Field("knockback", e.KnockbackDistance?.ToString("0.###"), vanillaWin?.KnockbackDistance?.ToString("0.###"));
        Field("super dizzy", e.SuperDizzyDamage?.ToString("0.###"), vanillaWin?.SuperDizzyDamage?.ToString("0.###"));
        Field("stun", e.StunFrames?.ToString(), vanillaWin?.StunFrames?.ToString());
        Field("freeze", e.FreezeFrames?.ToString(), vanillaWin?.FreezeFrames?.ToString());
        void BoolField(string label, bool? now, bool? was)
        {
            if (!now.HasValue) return;
            string n = now.Value ? "On" : "Off";
            parts.Add(was.HasValue ? $"{label} {(was.Value ? "On" : "Off")} → {n}" : $"{label} {n}");
        }
        BoolField("parriable", e.Parriable, vanillaWin?.Parriable);
        BoolField("lethal", e.Lethal, vanillaWin?.Lethal);
        BoolField("break structure", e.BreakStructure, vanillaWin?.BreakStructure);
        BoolField("dizzy on parry", e.DizzyOnParry, vanillaWin?.DizzyOnParry);
        BoolField("dizzy on avoid", e.DizzyOnAvoid, vanillaWin?.DizzyOnAvoid);
        Field("attack power", e.AttackPower, vanillaWin?.AttackPower);
        Field("property level", e.PropertyLevel?.ToString(), vanillaWin?.PropertyLevel?.ToString());
        return MakeValueRow(title, "", string.Join(", ", parts));
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
                .Where(IsModifiedNode)
                .OrderBy(n => n.TreeIndex)
                .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var newNodes = modified.Where(n => n.Kind == ComboNodeKind.UserCreated).ToList();
            var moveNodes = modified.Where(n => n.Kind != ComboNodeKind.UserCreated && !n.ReplacedByAiNode).ToList();
            var linkRows = BuildLinkRows(graph);
            var importedNodes = BuildImportedNodeRows(graph);
            var importedLinkRows = BuildImportedLinkRows(graph);

            var retargetRows = BuildRetargetRows(graph);
            var aiReplaceRows = BuildAiReplaceRows(graph);
            var attackRows = BuildAttackDbRows(graph.Nodes, contentPath);
            if (attackRows.Count > 0) attackByUnit.Add((unitKey, attackRows));
            var startEntryRows = BuildStartEntryRows(graph);
            var chanceRows = BuildBranchChanceRows(graph);
            var condRows = BuildConditionOverrideRows(graph);

            var keyParts = unitKey.Split('|');
            string variant = keyParts.Length > 1 ? keyParts[1] : unitKey;
            var propRows = new List<object>();
            if (entry.Props != null)
                AddUnitPropsEntries(propRows, entry.Props, contentPath, variant);
            if (propRows.Count > 0 && !propsVariants.Add(variant))
                propRows.Clear();

            if (modified.Count == 0 && retargetRows.Count == 0 && propRows.Count == 0
                && linkRows.Count == 0 && importedNodes.Count == 0 && importedLinkRows.Count == 0
                && startEntryRows.Count == 0 && chanceRows.Count == 0 && condRows.Count == 0)
                continue;

            var unit = new UnitReview
            {
                Key = unitKey,
                DisplayName = FormatUnitDisplayName(unitKey),
                Subtitle = ""
            };

            if (moveNodes.Count > 0)
                unit.Sections.Add(new UnitSectionReview
                {
                    Name = "Moves",
                    Rows = moveNodes.Select(ToChangeRow).Cast<object>().ToList()
                });

            if (newNodes.Count > 0)
                unit.Sections.Add(new UnitSectionReview
                {
                    Name = "New Nodes",
                    Rows = newNodes.Select(ToNewNodeRow).ToList()
                });

            if (linkRows.Count > 0)
                unit.Sections.Add(new UnitSectionReview { Name = "New Links", Rows = linkRows });

            if (importedNodes.Count > 0)
                unit.Sections.Add(new UnitSectionReview { Name = "Imported Nodes", Rows = importedNodes });

            if (importedLinkRows.Count > 0)
                unit.Sections.Add(new UnitSectionReview { Name = "Imported Links", Rows = importedLinkRows });

            if (retargetRows.Count > 0)
                unit.Sections.Add(new UnitSectionReview { Name = "Retargets", Rows = retargetRows });

            if (aiReplaceRows.Count > 0)
                unit.Sections.Add(new UnitSectionReview { Name = "AI Replaces", Rows = aiReplaceRows });

            if (startEntryRows.Count > 0)
                unit.Sections.Add(new UnitSectionReview { Name = "Start Entries", Rows = startEntryRows });

            if (chanceRows.Count > 0)
                unit.Sections.Add(new UnitSectionReview { Name = "Branch chances", Rows = chanceRows });

            if (condRows.Count > 0)
                unit.Sections.Add(new UnitSectionReview { Name = "Conditions", Rows = condRows });

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

        if (n.Removed)
        {
            return new ChangeRow
            {
                Title = $"{oldName} (removed)",
                ShowDetail = false,
                ToolTip = string.IsNullOrEmpty(oldPath)
                    ? $"{oldName} removed from tree"
                    : $"{oldName} removed from tree (was {oldPath})"
            };
        }

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

    public static object ToNewNodeRow(ComboNode n)
    {
        string name = string.IsNullOrEmpty(n.DisplayName)
            ? (string.IsNullOrEmpty(n.Name) ? $"Node {n.Id}" : n.Name)
            : n.DisplayName;
        string right = n.IsRedirect ? "redirect"
            : string.IsNullOrEmpty(n.AnimPath) ? "node"
            : n.AnimPath;
        // Action/event nodes read as bare "node" without their kind + class/event name,
        // and cloned nodes carry provenance the anim path alone does not show.
        if (n.IsActionNode)
            right += $" [action{(string.IsNullOrEmpty(n.ActionClass) ? "" : " " + n.ActionClass)}]";
        else if (n.IsEventNode)
            right += $" [event{(string.IsNullOrEmpty(n.EventName) ? "" : " " + n.EventName)}]";
        string source = string.IsNullOrEmpty(n.ImportComboRel)
            ? ""
            : $"; cloned from {n.ImportComboRel} node {n.ImportSourceTreeIndex}";
        return new ChangeRow
        {
            Title = name,
            ShowDetail = true,
            DetailLeft = "added",
            DetailRight = right,
            ToolTip = n.IsRedirect
                ? $"{name} (new redirect node)"
                : string.IsNullOrEmpty(n.AnimPath)
                    ? $"{name} (new node){source}"
                    : $"{name} (new node) → {n.AnimPath}{source}"
        };
    }

    public static object ToImportedNodeRow(ComboNode n)
    {
        string name = string.IsNullOrEmpty(n.DisplayName)
            ? (string.IsNullOrEmpty(n.Name) ? $"Node {n.Id}" : n.Name)
            : n.DisplayName;
        string right = n.IsRedirect ? "redirect"
            : string.IsNullOrEmpty(n.AnimPath) ? "node"
            : n.AnimPath;
        return new ChangeRow
        {
            Title = name,
            ShowDetail = true,
            DetailLeft = "imported",
            DetailRight = right,
            ToolTip = n.IsRedirect
                ? $"{name} (imported redirect node)"
                : string.IsNullOrEmpty(n.AnimPath)
                    ? $"{name} (imported node)"
                    : $"{name} (imported node) → {n.AnimPath}"
        };
    }

    public static List<object> BuildLinkRows(ComboGraph? graph)
    {
        var rows = new List<object>();
        if (graph?.Edges == null || graph.Nodes == null) return rows;

        foreach (var e in graph.Edges.Where(ed => ed.IsNewLink))
            rows.Add(MakeLinkRow(graph, e, e.MergedIntoStruct >= 0
                ? $"merged into tree[{e.MergedIntoStruct}] row {e.MergedIntoRow + 1} "
                  + $"key {e.MergedIntoKey} (no separate chance row)"
                : "new link"));
        return rows;
    }

    /// <summary>
    /// Mod-surfaced nodes (Kind=ImportedNew). Display-only: these never satisfy
    /// IsModifiedNode, so export gates and append behavior stay untouched.
    /// </summary>
    public static List<object> BuildImportedNodeRows(ComboGraph? graph)
    {
        var rows = new List<object>();
        if (graph?.Nodes == null) return rows;

        foreach (var n in graph.Nodes
            .Where(n => n.Kind == ComboNodeKind.ImportedNew)
            .OrderBy(n => n.TreeIndex)
            .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase))
            rows.Add(ToImportedNodeRow(n));
        return rows;
    }

    /// <summary>
    /// Links touching mod-surfaced nodes. Surfaced edges carry IsNewLink=false so the
    /// export never re-appends content the staged file already has; they are listed
    /// here purely so an imported tree reads complete in the change list.
    /// </summary>
    public static List<object> BuildImportedLinkRows(ComboGraph? graph)
    {
        var rows = new List<object>();
        if (graph?.Edges == null || graph.Nodes == null) return rows;

        bool TouchesImported(int nodeId)
            => graph.Nodes.Any(n => n.Id == nodeId && n.Kind == ComboNodeKind.ImportedNew);

        foreach (var e in graph.Edges.Where(ed => !ed.IsNewLink
            && (TouchesImported(ed.FromNodeId) || TouchesImported(ed.ToNodeId))))
            rows.Add(MakeLinkRow(graph, e, "imported link"));
        return rows;
    }

    /// <summary>
    /// Nodes whose payload export replaces at write time (ReplacedByAiNode): the model
    /// keeps anim/class fields untouched, so these satisfy IsModifiedNode on their own
    /// and are listed under their own section rather than as moves.
    /// </summary>
    public static List<object> BuildAiReplaceRows(ComboGraph? graph)
    {
        var rows = new List<object>();
        if (graph?.Nodes == null) return rows;

        foreach (var n in graph.Nodes
            .Where(n => n.ReplacedByAiNode && !n.Removed)
            .OrderBy(n => n.TreeIndex)
            .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase))
            rows.Add(ToAiReplaceRow(n));
        return rows;
    }

    public static object ToAiReplaceRow(ComboNode n)
    {
        string name = string.IsNullOrEmpty(n.DisplayName)
            ? (string.IsNullOrEmpty(n.Name) ? $"Node {n.Id}" : n.Name)
            : n.DisplayName;
        string orig = n.IsActionNode ? $"action {n.ActionClass}"
            : n.IsEventNode ? $"event {n.EventName}"
            : "attack";
        string next = n.ReplacedAiKind == "Attack"
            ? $"attack card {n.ReplacedAiClass}"
            : $"{n.ReplacedAiKind.ToLowerInvariant()} {n.ReplacedAiClass}";
        return MakeValueRow($"AI replace: {name}", orig, next,
            $"{name}: payload {orig} -> {next} (applied on export)");
    }

    private static object MakeLinkRow(ComboGraph graph, ComboEdge e, string kindLabel)
    {
        var src = graph.Nodes.FirstOrDefault(n => n.Id == e.FromNodeId);
        var dst = graph.Nodes.FirstOrDefault(n => n.Id == e.ToNodeId);
        string srcName = NodeLabel(src, e.FromNodeId);
        string dstName = NodeLabel(dst, e.ToNodeId);
        string title = $"{srcName} → {dstName}"
            + (string.IsNullOrEmpty(e.InputName) ? "" : $" [{e.InputName}]");
        return new ChangeRow
        {
            Title = title,
            ShowDetail = false,
            ToolTip = $"{srcName} → {dstName} ({kindLabel})"
        };
    }

    private static string NodeLabel(ComboNode? n, int id) => n == null ? $"#{id}"
        : !string.IsNullOrEmpty(n.DisplayName) ? n.DisplayName
        : !string.IsNullOrEmpty(n.Name) ? n.Name : $"#{n.Id}";

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

    /// <summary>
    /// Branch-chance edits (m_fProbability): explicit row overrides with their vanilla
    /// baseline, plus a note per struct that receives appended links (export rebalances
    /// those sets so the struct sums to 100 and the appended rows stay reachable).
    /// </summary>
    public static List<object> BuildBranchChanceRows(ComboGraph? graph)
    {
        var rows = new List<object>();
        if (graph?.TreeRowProbabilityOverrides == null || graph.Nodes == null) return rows;

        string StructName(int treeIdx)
        {
            var n = graph.Nodes.FirstOrDefault(x => x.TreeIndex == treeIdx && !x.IsRedirect)
                ?? graph.Nodes.FirstOrDefault(x => x.TreeIndex == treeIdx);
            return n != null ? NodeLabel(n, n.Id) : $"tree[{treeIdx}]";
        }

        foreach (var kvp in graph.TreeRowProbabilityOverrides.OrderBy(kv => kv.Key))
        {
            if (kvp.Value == null || kvp.Value.Count == 0) continue;
            var originals = graph.TreeRows != null && graph.TreeRows.TryGetValue(kvp.Key, out var orows)
                ? orows : null;
            string nodeName = StructName(kvp.Key);
            foreach (var row in kvp.Value.OrderBy(kv => kv.Key))
            {
                float? orig = originals != null && row.Key >= 0 && row.Key < originals.Count
                    ? originals[row.Key].OriginalProbability
                    : null;
                rows.Add(MakeValueRow(
                    $"Branch chance: {nodeName}",
                    $"row {row.Key + 1}: {(orig.HasValue ? orig.Value.ToString("0.###") + "%" : "?")}",
                    $"{row.Value:0.###}%"));
            }
        }

        var appendStructs = new SortedSet<int>();
        foreach (var e in graph.Edges.Where(ed => ed.IsNewLink && !ed.IsRedirect
            && ed.MergedIntoStruct < 0))
        {
            var from = graph.Nodes.FirstOrDefault(n => n.Id == e.FromNodeId);
            if (from == null) continue;
            int writeIdx = TreeChanceMath.ResolveWriteStruct(graph, from);
            if (writeIdx >= 0) appendStructs.Add(writeIdx);
        }
        foreach (int treeIdx in appendStructs)
        {
            int rowCount = graph.TreeRows != null && graph.TreeRows.TryGetValue(treeIdx, out var trows)
                ? trows.Count : 0;
            rows.Add(MakeValueRow(
                $"Branch chances: {StructName(treeIdx)}",
                $"{rowCount} existing row(s)",
                "new link rows rebalanced to 100% on export"));
        }
        return rows;
    }

    /// <summary>
    /// Condition edits: parameter overrides per condition instance (node label resolved
    /// through the rows referencing it) plus row-level clear/re-route edits with their
    /// vanilla target baseline.
    /// </summary>
    public static List<object> BuildConditionOverrideRows(ComboGraph? graph)
    {
        var rows = new List<object>();
        if (graph == null) return rows;

        string StructName(int treeIdx)
        {
            var n = graph.Nodes.FirstOrDefault(x => x.TreeIndex == treeIdx && !x.IsRedirect)
                ?? graph.Nodes.FirstOrDefault(x => x.TreeIndex == treeIdx);
            return n != null ? NodeLabel(n, n.Id) : $"tree[{treeIdx}]";
        }

        if (graph.TreeConditionParamOverrides != null)
        {
            foreach (var kvp in graph.TreeConditionParamOverrides.OrderBy(kv => kv.Key))
            {
                if (kvp.Value == null || kvp.Value.Count == 0) continue;
                string nodeName = kvp.Key < 0
                    ? "new condition (clone)"
                    : $"condition export {kvp.Key}";
                string condClass = "";
                if (graph.TreeRows != null)
                {
                    foreach (var trows in graph.TreeRows)
                    {
                        var refRow = trows.Value.FirstOrDefault(r => r.ConditionInstanceIndex == kvp.Key);
                        if (refRow == null) continue;
                        nodeName = StructName(trows.Key);
                        condClass = refRow.ConditionClass;
                        break;
                    }
                }
                foreach (var p in kvp.Value.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    rows.Add(MakeValueRow(
                        $"Condition param: {nodeName}",
                        string.IsNullOrEmpty(condClass) ? p.Key : $"{condClass}.{p.Key}",
                        p.Value));
            }
        }

        if (graph.TreeRowConditionOverrides != null)
        {
            foreach (var kvp in graph.TreeRowConditionOverrides.OrderBy(kv => kv.Key))
            {
                if (kvp.Value == null || kvp.Value.Count == 0) continue;
                string nodeName = StructName(kvp.Key);
                var originals = graph.TreeRows != null && graph.TreeRows.TryGetValue(kvp.Key, out var orows)
                    ? orows : null;
                foreach (var row in kvp.Value.OrderBy(kv => kv.Key))
                {
                    var parts = new List<string>();
                    if (row.Value.ClearCondition)
                        parts.Add("condition cleared (collapses to key 255)");
                    else if (row.Value.SetInstance > 0)
                        parts.Add($"condition added (export {row.Value.SetInstance})");
                    else if (!string.IsNullOrEmpty(row.Value.CloneSourceRel))
                        parts.Add($"condition cloned ({row.Value.CloneClass} from "
                            + $"{System.IO.Path.GetFileName(row.Value.CloneSourceRel)})");
                    var origRow = originals != null && row.Key >= 0 && row.Key < originals.Count
                        ? originals[row.Key] : null;
                    foreach (var rt in row.Value.Retarget.OrderBy(kv => kv.Key))
                    {
                        int origTarget = -1;
                        if (origRow != null)
                        {
                            int ki = origRow.Keys.IndexOf(rt.Key);
                            if (ki >= 0 && ki < origRow.TargetTreeIndices.Count)
                                origTarget = origRow.TargetTreeIndices[ki];
                        }
                        parts.Add($"target {rt.Key}: {(origTarget >= 0 ? origTarget.ToString() : "?")} -> {rt.Value}");
                    }
                    foreach (var ak in row.Value.AddedKeys.OrderBy(kv => kv.Key))
                    {
                        var tn = graph.Nodes.FirstOrDefault(n => n.Id == ak.Value);
                        parts.Add($"merged link key {ak.Key} → "
                            + (tn != null ? NodeLabel(tn, ak.Value) : $"#{ak.Value}"));
                    }
                    if (parts.Count == 0) continue;
                    rows.Add(MakeValueRow(
                        $"Condition row: {nodeName}",
                        $"row {row.Key + 1}",
                        string.Join(", ", parts)));
                }
            }
        }

        if (graph.TreeEdgeConditionOverrides != null && graph.TreeEdgeConditionOverrides.Count > 0)
        {
            foreach (var edge in graph.Edges
                .OrderBy(e => e.FromNodeId).ThenBy(e => e.ToNodeId)
                .ThenBy(e => e.InputName, StringComparer.Ordinal))
            {
                // Orphaned edits (link deleted) are ignored by export - skip them here too.
                string edgeKey = ComboGraph.EdgeConditionKey(edge);
                if (!graph.TreeEdgeConditionOverrides.TryGetValue(edgeKey, out var ec) || ec == null)
                    continue;
                var fromNode = graph.Nodes.FirstOrDefault(n => n.Id == edge.FromNodeId);
                var toNode = graph.Nodes.FirstOrDefault(n => n.Id == edge.ToNodeId);
                string fromLabel = fromNode != null ? NodeLabel(fromNode, fromNode.Id) : $"node {edge.FromNodeId}";
                string toLabel = toNode != null ? NodeLabel(toNode, toNode.Id) : $"node {edge.ToNodeId}";
                var parts = new List<string>();
                if (ec.SetInstance > 0)
                    parts.Add($"condition added (export {ec.SetInstance})");
                else if (!string.IsNullOrEmpty(ec.CloneSourceRel))
                    parts.Add($"condition cloned ({ec.CloneClass} from "
                        + $"{System.IO.Path.GetFileName(ec.CloneSourceRel)})");
                foreach (var rt in ec.Retarget.OrderBy(kv => kv.Key))
                {
                    string dest = rt.Value == TreeRowConditionEdit.TargetBlocked ? "blocked"
                        : rt.Value == TreeRowConditionEdit.TargetNatural ? "link target"
                        : rt.Value.ToString();
                    parts.Add($"target {rt.Key}: {dest}");
                }
                if (parts.Count == 0) continue;
                rows.Add(MakeValueRow(
                    $"Condition on new link: {fromLabel} -> {toLabel}",
                    string.IsNullOrEmpty(edge.InputName) ? "input None" : $"input {edge.InputName}",
                    string.Join(", ", parts)));
            }
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
        if (UnitPropertiesManager.EnableModelFeature && !string.IsNullOrEmpty(props.ModelOverride)) count++;
        if (UnitPropertiesManager.EnableModelFeature && !string.IsNullOrEmpty(props.ModelSet)) count++;
        if (UnitPropertiesManager.PhaseSwapActive(props)) count++;
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

        if (UnitPropertiesManager.EnableModelFeature && !string.IsNullOrEmpty(props.ModelOverride))
        {
            var familyDir = ModelCatalog.FamilyDirForVariant(variantTag);
            string scope = ModelCatalog.IsFamilyWideScenarioFamily(familyDir)
                ? "the family's"
                : "this variant's";
            entries.Add(MakeValueRow("Model",
                "(default)",
                ModelCatalog.DisplayNameForGamePath(props.ModelOverride),
                $"Points {scope} scenario files (levels, training, void, tutorial) "
                + $"at {props.ModelOverride} on export."));
        }

        if (UnitPropertiesManager.EnableModelFeature && !string.IsNullOrEmpty(props.ModelSet))
        {
            var familyDir = ModelCatalog.FamilyDirForVariant(variantTag);
            entries.Add(MakeValueRow("Model set",
                "(default)",
                props.ModelSet!,
                $"Repoints every {(familyDir ?? "unit")} Character BP (Blueprints/AI) at the "
                + $"{props.ModelSet} roster on export - same hideout and gender first, round-robin, "
                + "keeping per-spawn variety."));
        }

        if (UnitPropertiesManager.PhaseSwapActive(props))
        {
            var dbLeaf = PhaseSwapPatcher.DbLeafName(props.PhaseSwapTarget!);
            entries.Add(MakeValueRow("Phase Swap",
                "Off",
                $"On \u2192 {dbLeaf}",
                $"Appends a Last Man takedown transition to this unit's tier-matched scenario "
                + $"files: taking the unit down phase-swaps it into {dbLeaf} (Last Man node, "
                + "Last Man gauge not required). Per-level targets only patch same-token scenarios."));
        }
    }
}
