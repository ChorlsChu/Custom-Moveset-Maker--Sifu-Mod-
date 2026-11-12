using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SifuMovesetEditor;

/// <summary>One combo-slot reference to the card being deleted.</summary>
public sealed class CustomAttackDbRef
{
    public string NodeName = "";
    public string Kind = "";
    public string Result = "";
    /// <summary>True when the referenced node only exists because of the deleted card and
    /// is removed with it instead of being reverted.</summary>
    public bool NodeRemoved;
    internal int NodeId;
}

/// <summary>
/// Everything one unit's graph loses when a custom AttackDB is deleted: the direct card
/// references, plus the Conditions &amp; Chances / Start Entries routes that target the
/// affected slots. Counts mirror exactly what <see cref="CustomAttackDbDelete.Apply"/>
/// rewrites, so the confirmation popup and the mutation never drift apart.
/// </summary>
public sealed class CustomAttackDbUnitUsage
{
    public string UnitKey = "";
    public List<CustomAttackDbRef> Refs = new();
    public int ConditionRows;
    public int DroppedRows;
    public int ChanceOverrides;
    public int RowConditionEdits;
    public int EdgeConditionEdits;
    public int ParamOverrides;
    public int RemovedEdges;
    /// <summary>Authored nodes that exist only because of the deleted card.</summary>
    public int RemovedNodes;
    /// <summary>Merged links whose host row drops but whose target is unrelated - they
    /// survive as standalone links instead of disappearing with the row.</summary>
    public int UnmergedEdges;
    public int StartTransitions;
    public int StartTrimmed;
    public int ConditionalAttackMentions;
    public List<string> ConditionalAttackDetails = new();
    public List<string> ParamMentions = new();

    internal HashSet<int> AffectedNodeIds = new();
    internal HashSet<int> AffectedTreeIndices = new();
    internal HashSet<int> RemovedNodeIds = new();
    internal HashSet<int> DroppedRowInstances = new();
    internal List<RowRoute> RowRoutes = new();

    internal sealed class RowRoute
    {
        public int StructIndex;
        public int RowIndex;
        public List<int> Targets = new();
        public bool DropRow;
    }

    public bool AnyRoutes => ConditionRows > 0 || RemovedEdges > 0 || StartTransitions > 0;
    public bool AnyUsage => Refs.Count > 0 || AnyRoutes || ConditionalAttackMentions > 0;
}

/// <summary>
/// Delete plan for one custom AttackDB card: direct slot references, route usage,
/// private hit-row sharing and the exact CustomAssets files to remove.
/// </summary>
public sealed class CustomAttackDbDeletePlan
{
    public string CardGamePath = "";
    public string CardRel = "";
    public string CardName = "";
    public bool CardFileFound;
    public List<CustomAttackDbUnitUsage> Units = new();
    public List<string> Files = new();
    /// <summary>Private tables kept because another custom card still references them.</summary>
    public List<string> KeptSharedTables = new();
    /// <summary>Card file names that still use a kept shared table.</summary>
    public List<string> SharedTableOwners = new();

    public bool AnyUsage => Units.Any(u => u.AnyUsage);
    public int TotalConditionRows => Units.Sum(u => u.ConditionRows);
    public int TotalDroppedRows => Units.Sum(u => u.DroppedRows);
    public int TotalStartTransitions => Units.Sum(u => u.StartTransitions);
    public int TotalConditionalAttacks => Units.Sum(u => u.ConditionalAttackMentions);
    public int TotalRemovedNodes => Units.Sum(u => u.RemovedNodes);
    public int CardlessSlots => Units.Sum(u => u.Refs.Count(r => !r.NodeRemoved
        && r.Result.Contains("without a card", StringComparison.OrdinalIgnoreCase)));
    public bool HasNonCustomTable { get; internal set; }

    /// <summary>Human-readable usage summary for the confirmation dialog.</summary>
    public string BuildUsageDetail()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Card: " + CardRel);
        sb.AppendLine(Files.Count > 0
            ? $"Files to delete: {Files.Count} (card + companion halves"
              + (KeptSharedTables.Count > 0 ? "; shared hit row kept" : ", private hit row included") + ")"
            : "No card files found on disk - the library entry is removed only.");

        if (KeptSharedTables.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Kept hit row (still shared with another card):");
            foreach (var t in KeptSharedTables) sb.AppendLine("  · " + t);
            int shown = 0;
            foreach (var owner in SharedTableOwners.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (shown++ >= 4) { sb.AppendLine("  · …"); break; }
                sb.AppendLine("    used by " + owner);
            }
        }

        int refBudget = 10;
        foreach (var unit in Units.Where(u => u.AnyUsage))
        {
            sb.AppendLine();
            sb.AppendLine(unit.UnitKey);
            foreach (var r in unit.Refs)
            {
                if (refBudget-- <= 0) { sb.AppendLine("  · …"); refBudget = int.MinValue; break; }
                sb.AppendLine($"  · {r.NodeName} — {r.Kind}: {r.Result}");
            }
            if (unit.RemovedNodes > 0)
                sb.AppendLine($"  · Nodes: {unit.RemovedNodes} custom node(s) removed with the card");

            var bits = new List<string>();
            if (unit.ConditionRows > 0)
                bits.Add($"{unit.ConditionRows} row(s) lose a route"
                    + (unit.DroppedRows > 0 ? $" ({unit.DroppedRows} row(s) drop out)" : ""));
            if (unit.ChanceOverrides > 0) bits.Add($"{unit.ChanceOverrides} chance override(s)");
            if (unit.RowConditionEdits > 0) bits.Add($"{unit.RowConditionEdits} row condition edit(s)");
            if (unit.EdgeConditionEdits > 0) bits.Add($"{unit.EdgeConditionEdits} link condition edit(s)");
            if (unit.ParamOverrides > 0) bits.Add($"{unit.ParamOverrides} condition parameter edit(s)");
            if (unit.RemovedEdges > 0) bits.Add($"{unit.RemovedEdges} link(s)");
            if (unit.UnmergedEdges > 0)
                bits.Add($"{unit.UnmergedEdges} merged link(s) become standalone");
            if (bits.Count > 0)
                sb.AppendLine("  · Conditions & Chances: " + string.Join(", ", bits));

            if (unit.StartTransitions > 0)
                sb.AppendLine($"  · Start Entries: {unit.StartTransitions} transition(s)"
                    + (unit.StartTrimmed > 0 ? $" ({unit.StartTrimmed} key(s) trimmed)" : ""));

            if (unit.ConditionalAttackMentions > 0)
            {
                sb.AppendLine("  · Conditional attacks: " + unit.ConditionalAttackMentions + " entry(ies) still name this card");
                foreach (var d in unit.ConditionalAttackDetails.Take(3))
                    sb.AppendLine("      " + d);
            }
            foreach (var p in unit.ParamMentions.Take(2))
                sb.AppendLine("  · Condition parameter mentions: " + p);
        }

        if (HasNonCustomTable)
            sb.AppendLine("\nA vanilla hit-row table is referenced by this card and is never deleted.");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Builds and applies a custom AttackDB delete. Deletion is permanent (matching Clear
/// Custom Library). Direct card references are reverted - card swaps fall back to the
/// node's default card (or left cardless when there is none), payload replacements
/// return to their original action/event, and animation slots restore their default.
/// Conditions &amp; Chances and Start Entries store node routes rather than card paths,
/// so every route that targets an affected slot is removed: incoming graph links, branch
/// row targets, chance/condition overrides, and start transitions (multi-key rows only
/// lose the affected keys). Vanilla m_Transitions rows keep the removed targets in
/// ComboGraph.RemovedRouteRows so the export prunes them from the asset.
/// </summary>
public static class CustomAttackDbDelete
{
    public static CustomAttackDbDeletePlan Build(
        string cardGamePath,
        IEnumerable<(string UnitKey, ComboGraph Graph)> units,
        string customRoot)
    {
        var plan = new CustomAttackDbDeletePlan
        {
            CardGamePath = cardGamePath,
            CardRel = AttackDbCard.GamePathToContentRel(cardGamePath)
        };
        plan.CardName = Path.GetFileName(plan.CardRel.Replace('/', Path.DirectorySeparatorChar));

        foreach (var (unitKey, graph) in units)
        {
            if (graph == null) continue;
            var usage = Analyze(unitKey, graph, plan.CardRel, plan.CardName);
            if (usage.AnyUsage) plan.Units.Add(usage);
        }

        PlanFiles(plan, customRoot);
        return plan;
    }

    /// <summary>
    /// Rewrites one unit's graph for the plan. Returns true when anything changed.
    /// Safe to call only while the plan's analysis still matches the graph (the delete
    /// flow applies immediately after building it).
    /// </summary>
    public static bool Apply(CustomAttackDbDeletePlan plan, string unitKey, ComboGraph graph)
    {
        if (graph == null || string.IsNullOrEmpty(plan.CardRel)) return false;
        var usage = Analyze(unitKey, graph, plan.CardRel, plan.CardName);

        // 1. Revert direct card references. Tuning fields belonged to the deleted card,
        // so a slot that falls back must not keep them (they would patch the fallback).
        foreach (var n in graph.Nodes.Where(n => usage.AffectedNodeIds.Contains(n.Id)))
        {
            if (SameCard(n.ReplacedAttackCardRel, plan.CardRel))
            {
                n.ReplacedByAiNode = false;
                n.ReplacedAiKind = "";
                n.ReplacedAiClass = "";
                n.ReplacedImportComboRel = "";
                n.ReplacedSourceTreeIndex = -1;
                n.ReplacedAttackCardRel = "";
            }
            if (SameCard(n.AnimPath, plan.CardRel))
                n.AnimPath = !string.IsNullOrEmpty(n.DefaultAnimPath)
                    ? n.DefaultAnimPath
                    : n.VanillaAnimPath;
            if (SameCard(n.SourceDBPath, plan.CardRel))
                n.SourceDBPath = SameCard(n.DefaultDBPath, plan.CardRel) ? "" : n.DefaultDBPath;
            if (SameCard(n.DefaultDBPath, plan.CardRel))
                n.DefaultDBPath = "";
            n.AttackBuildupFrames = null;
            n.AttackGameplayRange = null;
            n.HitEdits = null;
        }

        var droppedRows = usage.RowRoutes.Where(r => r.DropRow)
            .Select(r => (r.StructIndex, r.RowIndex)).ToHashSet();

        // 2. Links. Incoming links to an affected slot plus every link that touches a
        // node removed with the card (outgoing links included). Merged links also own an
        // AddedKeys entry on the row edit.
        var removedEdges = graph.Edges
            .Where(e => usage.AffectedNodeIds.Contains(e.ToNodeId)
                || usage.RemovedNodeIds.Contains(e.FromNodeId)
                || usage.RemovedNodeIds.Contains(e.ToNodeId))
            .ToList();
        foreach (var edge in removedEdges)
        {
            if (edge.MergedIntoStruct >= 0 && edge.MergedIntoKey >= 0
                && graph.TreeRowConditionOverrides.TryGetValue(edge.MergedIntoStruct, out var mergedRows)
                && mergedRows.TryGetValue(edge.MergedIntoRow, out var mergedEdit))
            {
                mergedEdit.AddedKeys.Remove((byte)edge.MergedIntoKey);
                // A merge was often the row's only edit; drop the husk so export stops
                // treating the row as user-modified.
                if (IsEmpty(mergedEdit))
                {
                    mergedRows.Remove(edge.MergedIntoRow);
                    if (mergedRows.Count == 0)
                        graph.TreeRowConditionOverrides.Remove(edge.MergedIntoStruct);
                }
            }
            graph.Edges.Remove(edge);
        }

        // A merged link whose host row drops but whose own target is unrelated must not
        // vanish with the row: unmerge it so export appends a standalone chance row.
        foreach (var edge in graph.Edges.Where(e => e.MergedIntoStruct >= 0
                     && droppedRows.Contains((e.MergedIntoStruct, e.MergedIntoRow))).ToList())
        {
            edge.MergedIntoStruct = -1;
            edge.MergedIntoRow = -1;
            edge.MergedIntoKey = -1;
        }

        // 3. Vanilla branch-row targets: record what export must prune, then remove the
        // target from the snapshot and drop rows that no longer route anywhere.
        foreach (var group in usage.RowRoutes.GroupBy(r => r.StructIndex))
        {
            if (!graph.TreeRows.TryGetValue(group.Key, out var rows)) continue;
            if (!graph.RemovedRouteRows.TryGetValue(group.Key, out var exportRows))
                graph.RemovedRouteRows[group.Key] = exportRows = new List<RemovedRouteRow>();

            foreach (var route in group)
            {
                var snapshot = rows.FirstOrDefault(r => r.RowIndex == route.RowIndex);
                if (snapshot != null)
                {
                    var drop = new HashSet<int>(route.Targets);
                    snapshot.TargetTreeIndices.RemoveAll(drop.Contains);
                    if (route.DropRow || snapshot.TargetTreeIndices.Count == 0)
                        rows.Remove(snapshot);
                }

                var entry = exportRows.FirstOrDefault(r => r.RowIndex == route.RowIndex);
                if (entry == null)
                    exportRows.Add(entry = new RemovedRouteRow { RowIndex = route.RowIndex });
                foreach (var t in route.Targets)
                    if (!entry.TargetTreeIndices.Contains(t)) entry.TargetTreeIndices.Add(t);
            }
            if (rows.Count == 0) graph.TreeRows.Remove(group.Key);
        }

        // 4. Chance overrides on rows that vanished.
        foreach (var route in usage.RowRoutes.Where(r => r.DropRow))
        {
            if (graph.TreeRowProbabilityOverrides.TryGetValue(route.StructIndex, out var map))
            {
                map.Remove(route.RowIndex);
                if (map.Count == 0) graph.TreeRowProbabilityOverrides.Remove(route.StructIndex);
            }
        }

        // 5. Row condition edits: dropped rows lose the whole edit, survivors only lose
        // the outcome keys routed at an affected slot.
        foreach (var (structIdx, rowMap) in graph.TreeRowConditionOverrides.ToList())
        {
            foreach (var (rowIdx, edit) in rowMap.ToList())
            {
                if (droppedRows.Contains((structIdx, rowIdx)))
                {
                    rowMap.Remove(rowIdx);
                    continue;
                }
                bool touched = CleanEdit(edit, usage.AffectedNodeIds, usage.AffectedTreeIndices);
                if (touched && IsEmpty(edit)) rowMap.Remove(rowIdx);
            }
            if (rowMap.Count == 0) graph.TreeRowConditionOverrides.Remove(structIdx);
        }

        // 6. Edge condition edits: removed links lose their edit; surviving links only
        // lose retargets aimed at an affected slot.
        var removedEdgeKeys = new HashSet<string>(
            removedEdges.Select(ComboGraph.EdgeConditionKey), StringComparer.Ordinal);
        // Orphan edits keyed by a vanished node id are dead even if the edge object was
        // already gone (DeleteAuthoredNode purges the same shape).
        foreach (var key in graph.TreeEdgeConditionOverrides.Keys.ToList())
        {
            var ends = key.Split('|')[0].Split('>');
            if (ends.Length != 2) continue;
            bool fromRemoved = int.TryParse(ends[0], out int from)
                && usage.RemovedNodeIds.Contains(from);
            bool toRemoved = int.TryParse(ends[1], out int to)
                && usage.RemovedNodeIds.Contains(to);
            if (fromRemoved || toRemoved)
                graph.TreeEdgeConditionOverrides.Remove(key);
        }
        foreach (var (key, edit) in graph.TreeEdgeConditionOverrides.ToList())
        {
            if (removedEdgeKeys.Contains(key))
            {
                graph.TreeEdgeConditionOverrides.Remove(key);
                continue;
            }
            bool touched = CleanEdit(edit, usage.AffectedNodeIds, usage.AffectedTreeIndices);
            if (touched && IsEmpty(edit)) graph.TreeEdgeConditionOverrides.Remove(key);
        }

        // 7. Condition param edits for condition instances that only dropped rows used.
        var survivingInstances = new HashSet<int>();
        foreach (var (structIdx, rows) in graph.TreeRows)
            foreach (var row in rows)
                if (!droppedRows.Contains((structIdx, row.RowIndex)) && row.ConditionInstanceIndex > 0)
                    survivingInstances.Add(row.ConditionInstanceIndex);
        foreach (int inst in usage.DroppedRowInstances)
            if (!survivingInstances.Contains(inst))
                graph.TreeConditionParamOverrides.Remove(inst);

        // 8. Start Entries: mirror DeleteAuthoredNode's cleanup - multi-key rows lose only
        // the affected keys, single-key rows are marked Removed (vanilla) or dropped.
        var startEntries = graph.StartEntries ?? new List<StartEntry>();
        foreach (var en in startEntries.ToList())
        {
            if (en.Removed) continue;
            foreach (var t in en.Transitions
                .Where(t => !t.Removed && StartTransitionTargetsNode(t, usage.AffectedNodeIds)).ToList())
            {
                bool canTrim = t.KeyCount > 1
                    && t.Keys.Count == t.KeyCount
                    && t.KeyTargetNodeIds.Count == t.KeyCount
                    && t.KeyTargetTreeIndices.Count == t.KeyCount
                    && t.KeyTargetNames.Count == t.KeyCount;
                if (canTrim)
                {
                    for (int ki = t.KeyTargetNodeIds.Count - 1; ki >= 0; ki--)
                    {
                        if (!usage.AffectedNodeIds.Contains(t.KeyTargetNodeIds[ki])) continue;
                        t.Keys.RemoveAt(ki);
                        t.KeyTargetTreeIndices.RemoveAt(ki);
                        t.KeyTargetNodeIds.RemoveAt(ki);
                        t.KeyTargetNames.RemoveAt(ki);
                    }
                    if (t.Keys.Count == 0)
                    {
                        if (t.OriginalIndex >= 0) t.Removed = true;
                        else en.Transitions.Remove(t);
                        continue;
                    }
                    if (t.Keys.Count != t.KeyCount)
                    {
                        t.KeyCount = t.Keys.Count;
                        t.TargetSummary = string.Join(", ",
                            t.Keys.Zip(t.KeyTargetTreeIndices, (k, v) => $"{k}→{v}"));
                    }
                    if (usage.AffectedNodeIds.Contains(t.TargetNodeId))
                    {
                        t.TargetNodeId = t.KeyTargetNodeIds[0];
                        t.TargetTreeIndex = t.KeyTargetTreeIndices[0];
                        t.TargetName = t.KeyTargetNames[0];
                    }
                }
                else
                {
                    if (t.OriginalIndex >= 0) t.Removed = true;
                    else en.Transitions.Remove(t);
                }
            }
            if (en.Transitions.Count(t => !t.Removed) == 0)
            {
                if (en.VanillaIndex >= 0) en.Removed = true;
                else startEntries.Remove(en);
            }
        }

        // 9. Provenance rows/edge keys that no longer exist. A provenance key is only
        // meaningful while the edit it describes still routes that outcome key, so
        // dropped rows, removed edges and cleaned retargets all lose their entry.
        if (graph.RetargetProvenance != null)
        {
            foreach (var key in graph.RetargetProvenance.Keys.ToList())
            {
                if (key.StartsWith("E:", StringComparison.Ordinal))
                {
                    int bar = key.IndexOf('|');
                    if (bar <= 0) { graph.RetargetProvenance.Remove(key); continue; }
                    var parts = key.Split(':');
                    string edgeKey = parts.Length >= 2 ? parts[1] : "";
                    if (removedEdgeKeys.Contains(edgeKey))
                    {
                        graph.RetargetProvenance.Remove(key);
                        continue;
                    }
                    if (parts.Length < 3 || !byte.TryParse(parts[2], out byte edgeOutcome)
                        || !graph.TreeEdgeConditionOverrides.TryGetValue(edgeKey, out var edgeEdit)
                        || !EditHasKey(edgeEdit, edgeOutcome))
                        graph.RetargetProvenance.Remove(key);
                    continue;
                }

                var rp = key.Split(':');
                if (rp.Length < 4 || rp[0] != "R"
                    || !int.TryParse(rp[1], out int st)
                    || !int.TryParse(rp[2], out int rw)
                    || !byte.TryParse(rp[3], out byte outcome))
                {
                    graph.RetargetProvenance.Remove(key);
                    continue;
                }
                if (droppedRows.Contains((st, rw))
                    || !graph.TreeRowConditionOverrides.TryGetValue(st, out var rowMap)
                    || !rowMap.TryGetValue(rw, out var edit)
                    || !EditHasKey(edit, outcome))
                    graph.RetargetProvenance.Remove(key);
            }
        }

        // 10. Nodes that only existed because of the card. Every edge touching one was
        // removed in step 2; drop the node, its redirect provenance and any stale
        // resolved target left on a vanilla redirect.
        if (usage.RemovedNodeIds.Count > 0)
        {
            foreach (int id in usage.RemovedNodeIds)
                graph.RedirectOriginalTargets.Remove(id);
            graph.Nodes.RemoveAll(n => usage.RemovedNodeIds.Contains(n.Id));
            foreach (var n in graph.Nodes)
                if (n.IsRedirect && usage.RemovedNodeIds.Contains(n.ResolvedRedirectNodeId))
                    n.ResolvedRedirectNodeId = -1;
            // A surviving authored redirect may still remember a removed node as its
            // "original" target (create-then-retarget). Re-baseline it onto the live
            // target so a later reset cannot resurrect a ghost.
            foreach (var n in graph.Nodes.Where(n => n.IsRedirect))
            {
                if (!graph.RedirectOriginalTargets.TryGetValue(n.Id, out int orig)
                    || !usage.RemovedNodeIds.Contains(orig)) continue;
                if (n.ResolvedRedirectNodeId >= 0)
                    graph.RedirectOriginalTargets[n.Id] = n.ResolvedRedirectNodeId;
                else
                    graph.RedirectOriginalTargets.Remove(n.Id);
            }
        }

        return usage.AffectedNodeIds.Count > 0
            || usage.ConditionRows > 0
            || removedEdges.Count > 0
            || usage.StartTransitions > 0
            || usage.RemovedNodeIds.Count > 0;
    }

    // ── analysis ────────────────────────────────────────────────────────────────

    private static CustomAttackDbUnitUsage Analyze(
        string unitKey, ComboGraph graph, string cardRel, string cardName)
    {
        var u = new CustomAttackDbUnitUsage { UnitKey = unitKey };

        // A user-created node whose own card is the deleted card only exists because of
        // it, so deleting the card deletes the node. That covers a card dragged from the
        // library (default = source = card) and an animation dropped without a default
        // card (only the swap source points at it). Vanilla/imported slots that merely
        // swapped to the card keep a fallback and are reverted instead, and a user node
        // later retargeted to another card survives (its SourceDBPath no longer matches).
        foreach (var n in graph.Nodes)
        {
            if (n.Kind != ComboNodeKind.UserCreated || n.TreeIndex >= 0 || n.IsRedirect) continue;
            bool ownDefault = SameCard(n.DefaultDBPath, cardRel);
            bool ownSource = string.IsNullOrEmpty(n.DefaultDBPath) && SameCard(n.SourceDBPath, cardRel);
            if (!ownDefault && !ownSource) continue;
            if (!string.IsNullOrEmpty(n.SourceDBPath) && !SameCard(n.SourceDBPath, cardRel)) continue;
            u.RemovedNodeIds.Add(n.Id);
        }

        foreach (var n in graph.Nodes)
        {
            bool isSource = SameCard(n.SourceDBPath, cardRel);
            bool isDefault = SameCard(n.DefaultDBPath, cardRel);
            bool isPayload = SameCard(n.ReplacedAttackCardRel, cardRel);
            bool isAnim = SameCard(n.AnimPath, cardRel);
            if (!(isSource || isDefault || isPayload || isAnim)) continue;

            u.AffectedNodeIds.Add(n.Id);
            if (n.TreeIndex >= 0) u.AffectedTreeIndices.Add(n.TreeIndex);

            if (isPayload)
                u.Refs.Add(new CustomAttackDbRef
                {
                    NodeId = n.Id,
                    NodeName = NodeLabel(n),
                    Kind = "payload replacement",
                    Result = "reverts to the original action/event"
                });
            if (isSource && !isDefault)
                u.Refs.Add(new CustomAttackDbRef
                {
                    NodeId = n.Id,
                    NodeName = NodeLabel(n),
                    Kind = "card swap",
                    Result = !string.IsNullOrEmpty(n.DefaultDBPath)
                        ? "falls back to its default card"
                        : "slot is left without a card"
                });
            if (isDefault)
                u.Refs.Add(new CustomAttackDbRef
                {
                    NodeId = n.Id,
                    NodeName = NodeLabel(n),
                    Kind = "default card",
                    Result = !string.IsNullOrEmpty(n.SourceDBPath) && !SameCard(n.SourceDBPath, cardRel)
                        ? "falls back to its swap card"
                        : "slot is left without a card"
                });
            if (isAnim && !isDefault && !isSource)
                u.Refs.Add(new CustomAttackDbRef
                {
                    NodeId = n.Id,
                    NodeName = NodeLabel(n),
                    Kind = "animation slot",
                    Result = !string.IsNullOrEmpty(n.DefaultAnimPath)
                        ? "restores its default animation"
                        : "slot is left without an animation"
                });
        }

        // Authored redirects that forward into one of the removed nodes become orphans
        // (they would render hidden with no target), so they go with the node. Vanilla
        // redirects are kept - apply only clears their resolved target id.
        bool grewRemoved;
        do
        {
            grewRemoved = false;
            foreach (var n in graph.Nodes)
            {
                if (u.RemovedNodeIds.Contains(n.Id)) continue;
                if (!n.IsRedirect || n.Kind != ComboNodeKind.UserCreated) continue;
                if (!u.RemovedNodeIds.Contains(n.ResolvedRedirectNodeId)) continue;
                u.RemovedNodeIds.Add(n.Id);
                grewRemoved = true;
            }
        } while (grewRemoved);

        u.RemovedNodes = u.RemovedNodeIds.Count;
        foreach (var r in u.Refs)
        {
            if (!u.RemovedNodeIds.Contains(r.NodeId)) continue;
            r.NodeRemoved = true;
            r.Result = "node is removed with the card";
        }

        // Redirect rows forward straight into the affected slot - routes to them are
        // routes to the slot, so the cleanup set grows through the redirect chain.
        bool grew;
        do
        {
            grew = false;
            foreach (var n in graph.Nodes)
            {
                if (u.AffectedNodeIds.Contains(n.Id)) continue;
                if (!n.IsRedirect || !u.AffectedNodeIds.Contains(n.ResolvedRedirectNodeId)) continue;
                u.AffectedNodeIds.Add(n.Id);
                if (n.TreeIndex >= 0) u.AffectedTreeIndices.Add(n.TreeIndex);
                grew = true;
            }
        } while (grew);

        // Branch rows: every target value that lands on an affected slot.
        foreach (var (structIdx, rows) in graph.TreeRows)
        {
            foreach (var row in rows)
            {
                var hits = row.TargetTreeIndices
                    .Where(u.AffectedTreeIndices.Contains).Distinct().ToList();
                if (hits.Count == 0) continue;

                bool drop = row.TargetTreeIndices
                    .Where(t => t >= 0).All(u.AffectedTreeIndices.Contains);
                u.ConditionRows++;
                if (drop)
                {
                    u.DroppedRows++;
                    if (row.ConditionInstanceIndex > 0) u.DroppedRowInstances.Add(row.ConditionInstanceIndex);
                }
                u.RowRoutes.Add(new CustomAttackDbUnitUsage.RowRoute
                {
                    StructIndex = structIdx,
                    RowIndex = row.RowIndex,
                    Targets = hits,
                    DropRow = drop
                });
            }
        }

        var droppedRows = u.RowRoutes.Where(r => r.DropRow)
            .Select(r => (r.StructIndex, r.RowIndex)).ToHashSet();

        foreach (var route in u.RowRoutes.Where(r => r.DropRow))
            if (graph.TreeRowProbabilityOverrides.TryGetValue(route.StructIndex, out var probs)
                && probs.ContainsKey(route.RowIndex))
                u.ChanceOverrides++;

        foreach (var (structIdx, rowMap) in graph.TreeRowConditionOverrides)
            foreach (var (rowIdx, edit) in rowMap)
            {
                if (droppedRows.Contains((structIdx, rowIdx))) { u.RowConditionEdits++; continue; }
                if (EditTouches(edit, u.AffectedNodeIds, u.AffectedTreeIndices)) u.RowConditionEdits++;
            }

        var removedEdges = graph.Edges
            .Where(e => u.AffectedNodeIds.Contains(e.ToNodeId)
                || u.RemovedNodeIds.Contains(e.FromNodeId)
                || u.RemovedNodeIds.Contains(e.ToNodeId))
            .ToList();
        u.RemovedEdges = removedEdges.Count;
        // Merged links whose host row drops but which target something else stay as
        // standalone links (Apply clears the merge).
        u.UnmergedEdges = graph.Edges.Count(e => !u.AffectedNodeIds.Contains(e.ToNodeId)
            && e.MergedIntoStruct >= 0
            && droppedRows.Contains((e.MergedIntoStruct, e.MergedIntoRow)));
        var removedEdgeKeys = new HashSet<string>(
            removedEdges.Select(ComboGraph.EdgeConditionKey), StringComparer.Ordinal);
        foreach (var (key, edit) in graph.TreeEdgeConditionOverrides)
        {
            if (removedEdgeKeys.Contains(key)) { u.EdgeConditionEdits++; continue; }
            if (EditTouches(edit, u.AffectedNodeIds, u.AffectedTreeIndices)) u.EdgeConditionEdits++;
        }

        foreach (int inst in u.DroppedRowInstances)
        {
            bool sharedBySurvivor = graph.TreeRows
                .SelectMany(kv => kv.Value.Select(r => (kv.Key, Row: r)))
                .Any(x => x.Row.ConditionInstanceIndex == inst
                    && !droppedRows.Contains((x.Key, x.Row.RowIndex)));
            if (!sharedBySurvivor && graph.TreeConditionParamOverrides.ContainsKey(inst))
                u.ParamOverrides++;
        }

        foreach (var en in graph.StartEntries ?? new List<StartEntry>())
        {
            if (en.Removed) continue;
            foreach (var t in en.Transitions)
            {
                if (t.Removed || !StartTransitionTargetsNode(t, u.AffectedNodeIds)) continue;
                u.StartTransitions++;
                bool canTrim = t.KeyCount > 1
                    && t.Keys.Count == t.KeyCount
                    && t.KeyTargetNodeIds.Count == t.KeyCount
                    && t.KeyTargetTreeIndices.Count == t.KeyCount
                    && t.KeyTargetNames.Count == t.KeyCount;
                if (canTrim && t.KeyTargetNodeIds.Count(id => u.AffectedNodeIds.Contains(id)) < t.Keys.Count)
                    u.StartTrimmed++;
            }
        }

        foreach (var n in graph.Nodes.Where(n => u.AffectedNodeIds.Contains(n.Id) && n.TreeIndex >= 0))
        {
            if (!graph.TreeNodeConditions.TryGetValue(n.TreeIndex, out var cond)) continue;
            foreach (var ca in cond.ConditionalAttacks)
            {
                if (!ca.AttackNames.Any(a => NameMatches(a, cardName))) continue;
                u.ConditionalAttackMentions++;
                u.ConditionalAttackDetails.Add(
                    $"{NodeLabel(n)} · {ca.TagName} lists {string.Join(", ", ca.AttackNames.Where(a => NameMatches(a, cardName)))}");
            }
        }

        foreach (var row in graph.TreeRows.Values.SelectMany(r => r))
            foreach (var p in row.ConditionParams)
                if (p.Contains(cardName, StringComparison.OrdinalIgnoreCase))
                {
                    u.ParamMentions.Add($"row {row.RowIndex}: {p}");
                    break;
                }

        return u;
    }

    // ── file plan ───────────────────────────────────────────────────────────────

    private static void PlanFiles(CustomAttackDbDeletePlan plan, string customRoot)
    {
        if (string.IsNullOrEmpty(customRoot)) return;
        string cardUasset = CustomAssets.FileForContentRel(plan.CardRel + ".uasset");
        plan.CardFileFound = File.Exists(cardUasset);
        AddAssetFamily(plan, cardUasset);

        // Private tables this card references: named after the card, next to it. A table
        // another custom card still points at is kept (its rows are shared by design).
        var privateTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hit = AttackHitCard.Read(cardUasset);
        if (hit != null)
            foreach (var w in hit.Windows)
                if (!string.IsNullOrEmpty(w.TableRel)
                    && HitRowPrivatizer.IsPrivateTable(w.TableRel, plan.CardName))
                    privateTables.Add(w.TableRel);
                else if (!string.IsNullOrEmpty(w.TableRel)
                         && !File.Exists(CustomAssets.FileForContentRel(w.TableRel + ".uasset")))
                    plan.HasNonCustomTable = true;

        if (privateTables.Count == 0) return;

        var otherCards = FindOtherCustomCards(customRoot, cardUasset);
        foreach (var tableRel in privateTables)
        {
            string tableName = Path.GetFileName(tableRel.Replace('/', Path.DirectorySeparatorChar));
            string? owner = null;
            foreach (var other in otherCards)
            {
                if (!AttackDbCard.ReferencesName(other, tableName)) continue;
                owner = Path.GetFileNameWithoutExtension(other);
                break;
            }
            if (owner != null)
            {
                plan.KeptSharedTables.Add(tableRel);
                plan.SharedTableOwners.Add($"{owner} ({tableRel})");
                continue;
            }
            AddAssetFamily(plan, CustomAssets.FileForContentRel(tableRel + ".uasset"));
        }
    }

    internal static List<string> FindOtherCustomCards(string customRoot, string cardUasset)
    {
        var cards = new List<string>();
        try
        {
            var dbRoot = Path.Combine(customRoot, "DB");
            if (!Directory.Exists(dbRoot)) return cards;
            string cardFull = Path.GetFullPath(cardUasset);
            foreach (var file in Directory.EnumerateFiles(dbRoot, "*.uasset", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFullPath(file), cardFull, StringComparison.OrdinalIgnoreCase))
                    continue;
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains("HitBoxData", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("datatable", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("playratecurve", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!AttackDbCard.LooksLikeCard(file)) continue;
                cards.Add(file);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("CUSTOM", ex);
        }
        return cards;
    }

    /// <summary>Adds every existing companion half (.uasset/.uexp/.ubulk/.uptnl).</summary>
    private static void AddAssetFamily(CustomAttackDbDeletePlan plan, string uassetPath)
    {
        if (string.IsNullOrEmpty(uassetPath)) return;
        string stem = Path.ChangeExtension(uassetPath, null) ?? uassetPath;
        foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk", ".uptnl" })
        {
            var file = stem + ext;
            if (!File.Exists(file)) continue;
            var rel = Path.GetRelativePath(CustomAssets.Root, file).Replace('\\', '/');
            if (!plan.Files.Contains(rel, StringComparer.OrdinalIgnoreCase))
                plan.Files.Add(rel);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    public static bool SameCard(string? gamePath, string cardRel)
        => !string.IsNullOrWhiteSpace(gamePath)
           && string.Equals(AttackDbCard.GamePathToContentRel(gamePath), cardRel,
               StringComparison.OrdinalIgnoreCase);

    private static string NodeLabel(ComboNode n)
        => !string.IsNullOrEmpty(n.DisplayName) ? n.DisplayName
           : !string.IsNullOrEmpty(n.Name) ? n.Name
           : "node #" + n.Id;

    private static bool NameMatches(string assetName, string cardName)
    {
        if (string.IsNullOrEmpty(assetName)) return false;
        var name = Path.GetFileName(assetName.Replace('\\', '/'));
        if (name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            name = name[..^7];
        return string.Equals(name, cardName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartTransitionTargetsNode(StartTransition t, HashSet<int> nodeIds)
        => nodeIds.Contains(t.TargetNodeId)
           || (t.KeyTargetNodeIds?.Count > 0 && t.KeyTargetNodeIds.Any(nodeIds.Contains));

    private static bool EditTouches(TreeRowConditionEdit edit, HashSet<int> nodeIds, HashSet<int> treeIndices)
        => edit.Retarget.Any(kv => treeIndices.Contains(kv.Value))
           || edit.RetargetNodeIds.Any(kv => nodeIds.Contains(kv.Value))
           || edit.AddedKeys.Any(kv => nodeIds.Contains(kv.Value));

    /// <summary>Drops outcome keys that route to an affected slot. Returns true when
    /// anything was removed.</summary>
    private static bool CleanEdit(TreeRowConditionEdit edit, HashSet<int> nodeIds, HashSet<int> treeIndices)
    {
        bool touched = false;
        foreach (var key in edit.Retarget.Where(kv => treeIndices.Contains(kv.Value))
                     .Select(kv => kv.Key).ToList())
        {
            edit.Retarget.Remove(key);
            touched = true;
        }
        foreach (var key in edit.RetargetNodeIds.Where(kv => nodeIds.Contains(kv.Value))
                     .Select(kv => kv.Key).ToList())
        {
            edit.RetargetNodeIds.Remove(key);
            touched = true;
        }
        foreach (var key in edit.AddedKeys.Where(kv => nodeIds.Contains(kv.Value))
                     .Select(kv => kv.Key).ToList())
        {
            edit.AddedKeys.Remove(key);
            touched = true;
        }
        return touched;
    }

    private static bool IsEmpty(TreeRowConditionEdit edit)
        => !edit.ClearCondition
           && edit.Retarget.Count == 0
           && edit.RetargetNodeIds.Count == 0
           && edit.AddedKeys.Count == 0
           && edit.SetInstance <= 0
           && string.IsNullOrEmpty(edit.CloneSourceRel);

    private static bool EditHasKey(TreeRowConditionEdit edit, byte outcome)
        => edit.Retarget.ContainsKey(outcome)
           || edit.RetargetNodeIds.ContainsKey(outcome)
           || edit.AddedKeys.ContainsKey(outcome);
}
