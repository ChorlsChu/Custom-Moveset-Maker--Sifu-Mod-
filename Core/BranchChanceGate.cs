using System;
using System.Collections.Generic;
using System.Globalization;

namespace SifuMovesetEditor;

/// <summary>
/// One row of a node's outgoing probability pool: a vanilla transition row or a
/// new-link row that only exists after export. Mirrors BranchChancesDialog.ChanceRow
/// for the fields the pooling math needs, without pulling the dialog's UI types in.
/// </summary>
public readonly struct BranchChanceRow
{
    public readonly string InputName;
    public readonly float? Explicit;   // stored user value (null = automatic)
    public readonly float Original;    // vanilla probability (new rows: weight placeholder)
    public readonly bool IsNewLink;

    public BranchChanceRow(string inputName, float? explicitValue, float original, bool isNewLink)
    {
        InputName = inputName ?? "";
        Explicit = explicitValue;
        Original = original;
        IsNewLink = isNewLink;
    }
}

/// <summary>Per-pool total and whether it still resolves to 100% (plus tolerance).</summary>
public sealed class BranchChancePool
{
    public string Label = "";
    public float Total;
    public bool Ok;
}

/// <summary>
/// Result of the cautious post-link check: the pools the dialog would show, the
/// brand-new link's resolved chance, and whether either guard fires.
/// </summary>
public sealed class BranchChanceGateResult
{
    public bool PerInput;
    public List<BranchChancePool> Pools = new();
    /// <summary>The just-added link's row never gets any share of the roll.</summary>
    public bool NewLinkUnreachable;
    public float NewLinkEffective;
    public bool NeedsAttention => NewLinkUnreachable || Pools.Exists(p => !p.Ok);

    /// <summary>Short, human-readable reason(s) for the status line.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            foreach (var p in Pools)
            {
                if (p.Ok) continue;
                parts.Add(PerInput
                    ? $"{p.Label} pool totals {p.Total.ToString("0.#", CultureInfo.InvariantCulture)}%"
                    : $"pool totals {p.Total.ToString("0.#", CultureInfo.InvariantCulture)}%");
            }
            if (NewLinkUnreachable) parts.Add("new link lands at 0%");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Single source of truth for a node's outgoing branch-chance pools. Shared by the
/// Conditions &amp; Chances dialog (so the displayed values and the post-link check can
/// never disagree) and by the cautious auto-open after a link is committed.
///
/// Pooling matches the dialog and export:
///  - player (MainChar) trees roll one set per input, so a Light append never takes
///    share from Heavy (vanilla ships both at 100 on one node);
///  - enemy trees keep one node-wide set, matching their exported shape;
///  - rows without an explicit value share the remainder left by typed rows, and a
///    new-link row defaults to the average of the vanilla rows (NewRowAutoWeight).
/// </summary>
public static class BranchChanceGate
{
    public const float SumTolerance = 0.5f;
    public const float UnreachableThreshold = 0.01f;

    /// <summary>
    /// Effective value for every row, in input order. Identical to the dialog's
    /// Recompute: with no appended rows typed values win outright; with appends the
    /// group is normalized so the roll always reaches the appended rows.
    /// </summary>
    public static float[] ComputeEffective(IReadOnlyList<BranchChanceRow> rows, bool perInput)
    {
        var result = new float[rows.Count];
        if (rows.Count == 0) return result;

        bool hasAppends = false;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].IsNewLink) { hasAppends = true; break; }

        if (!hasAppends)
        {
            for (int i = 0; i < rows.Count; i++)
                result[i] = rows[i].Explicit ?? rows[i].Original;
            return result;
        }

        foreach (var group in GroupIndices(rows, perInput))
        {
            var vanillaChance = new List<TreeChanceMath.Row>();
            foreach (int i in group)
            {
                if (rows[i].IsNewLink) continue;
                vanillaChance.Add(new TreeChanceMath.Row(rows[i].Explicit, rows[i].Original));
            }
            float newWeight = TreeChanceMath.NewRowAutoWeight(vanillaChance);

            var chance = new List<TreeChanceMath.Row>(group.Count);
            foreach (int i in group)
            {
                chance.Add(rows[i].IsNewLink
                    ? new TreeChanceMath.Row(rows[i].Explicit, newWeight)
                    : new TreeChanceMath.Row(rows[i].Explicit, rows[i].Original));
            }
            var values = TreeChanceMath.Compute(chance, normalize: true);
            for (int k = 0; k < group.Count; k++)
                result[group[k]] = values[k];
        }
        return result;
    }

    /// <summary>
    /// Evaluates the source node's pool after <paramref name="newEdge"/> was added.
    /// Flags the dialog when a pool no longer totals 100% (within tolerance) or when
    /// the new link itself resolves to 0% - the "typed rows already consumed the whole
    /// pool, so the branch is dead" case.
    /// </summary>
    public static BranchChanceGateResult Evaluate(ComboGraph graph, ComboNode source, ComboEdge newEdge)
    {
        var gateResult = new BranchChanceGateResult();
        if (graph == null || source == null || newEdge == null) return gateResult;

        var rows = BuildRows(graph, source, newEdge, out int newRowIndex);
        if (rows.Count == 0) return gateResult;

        var effective = ComputePools(graph, gateResult, rows);

        if (newRowIndex >= 0)
        {
            gateResult.NewLinkEffective = effective[newRowIndex];
            gateResult.NewLinkUnreachable = effective[newRowIndex] <= UnreachableThreshold;
        }
        return gateResult;
    }

    /// <summary>
    /// Evaluates a source node's CURRENT outgoing pool without a just-added link. Used
    /// after a node or link is deleted: automatic rows absorb the missing share, so a
    /// pool only ends up off 100% when typed values leave no room for them. Flags the
    /// same pools <see cref="Evaluate"/> would (no "new link" reason).
    /// </summary>
    public static BranchChanceGateResult EvaluatePool(ComboGraph graph, ComboNode source)
    {
        var gateResult = new BranchChanceGateResult();
        if (graph == null || source == null) return gateResult;

        var rows = BuildRows(graph, source, null, out _);
        if (rows.Count == 0) return gateResult;

        ComputePools(graph, gateResult, rows);
        return gateResult;
    }

    /// <summary>
    /// Row set shared by the link and pool evaluations - exactly what the dialog builds:
    /// vanilla rows for the write struct (with overrides), then new links writing back
    /// to it in edge order (= export append order). Merged links ride the target row
    /// instead of adding a row. <paramref name="newRowIndex"/> is the position of
    /// <paramref name="newEdge"/> (or -1 when none was supplied).
    /// </summary>
    private static List<BranchChanceRow> BuildRows(ComboGraph graph, ComboNode source,
        ComboEdge? newEdge, out int newRowIndex)
    {
        int structIdx = source.TreeIndex < 0
            ? -1
            : TreeChanceMath.ResolveWriteStruct(graph, source);

        var rows = new List<BranchChanceRow>();
        if (structIdx >= 0 && graph.TreeRows.TryGetValue(structIdx, out var vanillaRows))
        {
            graph.TreeRowProbabilityOverrides.TryGetValue(structIdx, out var overrides);
            foreach (var vr in vanillaRows)
            {
                float? explicitValue = null;
                if (overrides != null && overrides.TryGetValue(vr.RowIndex, out var value))
                    explicitValue = value;
                rows.Add(new BranchChanceRow(vr.InputName, explicitValue, vr.OriginalProbability, false));
            }
        }

        newRowIndex = -1;
        int fallbackIndex = -1;
        foreach (var e in graph.Edges)
        {
            if (!e.IsNewLink || e.IsRedirect || e.MergedIntoStruct >= 0) continue;
            var from = graph.Nodes.Find(n => n.Id == e.FromNodeId);
            if (from == null) continue;
            if (structIdx < 0)
            {
                if (e.FromNodeId != source.Id) continue;
            }
            else if (TreeChanceMath.ResolveWriteStruct(graph, from) != structIdx)
            {
                continue;
            }

            if (newEdge != null && ReferenceEquals(e, newEdge)) newRowIndex = rows.Count;
            else if (newEdge != null && fallbackIndex < 0
                && e.FromNodeId == newEdge.FromNodeId
                && e.ToNodeId == newEdge.ToNodeId
                && string.Equals(e.InputName, newEdge.InputName, StringComparison.Ordinal))
                fallbackIndex = rows.Count;

            rows.Add(new BranchChanceRow(e.InputName, e.NewLinkProbability, 0f, true));
        }
        if (newEdge != null && newRowIndex < 0) newRowIndex = fallbackIndex;
        return rows;
    }

    private static float[] ComputePools(ComboGraph graph, BranchChanceGateResult gateResult,
        List<BranchChanceRow> rows)
    {
        bool perInput = GraphIdentity.IsMainChar(graph);
        gateResult.PerInput = perInput;
        var effective = ComputeEffective(rows, perInput);

        foreach (var group in GroupIndices(rows, perInput))
        {
            float total = 0f;
            foreach (int i in group) total += effective[i];
            string key = perInput ? InputCatalog.GroupKey(rows[group[0]].InputName) : "";
            gateResult.Pools.Add(new BranchChancePool
            {
                Label = perInput ? InputCatalog.GroupLabel(key) : "",
                Total = total,
                Ok = Math.Abs(total - TreeChanceMath.SumTarget) <= SumTolerance
            });
        }
        return effective;
    }

    private static List<List<int>> GroupIndices(IReadOnlyList<BranchChanceRow> rows, bool perInput)
    {
        var groups = new List<List<int>>();
        if (!perInput)
        {
            var all = new List<int>(rows.Count);
            for (int i = 0; i < rows.Count; i++) all.Add(i);
            groups.Add(all);
            return groups;
        }

        // First-seen order keeps header chips in row order (same as the dialog).
        var order = new List<string>();
        var byKey = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows.Count; i++)
        {
            string key = InputCatalog.GroupKey(rows[i].InputName);
            if (!byKey.TryGetValue(key, out var list))
            {
                byKey[key] = list = new List<int>();
                order.Add(key);
            }
            list.Add(i);
        }
        foreach (var key in order) groups.Add(byKey[key]);
        return groups;
    }
}
