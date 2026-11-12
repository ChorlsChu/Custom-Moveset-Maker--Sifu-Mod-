using System;
using System.Collections.Generic;
using System.Linq;

namespace SifuMovesetEditor;

/// <summary>
/// Finds the moves whose outgoing chance pool actually shrinks when graph nodes or a
/// link are deleted. Only standalone new-link rows count: a merged link rides an
/// existing conditioned row and redirect hops are routing, so removing either leaves
/// every pool total unchanged. Used by the post-delete caution check to decide which
/// Conditions &amp; Chances dialogs to open.
/// </summary>
public static class NodeDeleteChanceCheck
{
    /// <summary>
    /// Distinct surviving source nodes that lose a chance row when every node in
    /// <paramref name="removedNodeIds"/> disappears. Order follows graph edge order
    /// (= export append order), so the dialogs open in a stable sequence.
    /// </summary>
    public static List<int> CollectAffectedSources(ComboGraph? graph,
        IEnumerable<int>? removedNodeIds)
    {
        var result = new List<int>();
        if (graph == null || removedNodeIds == null) return result;

        var removed = new HashSet<int>(removedNodeIds);
        if (removed.Count == 0) return result;

        var alive = new HashSet<int>(graph.Nodes.Where(n => !n.Removed).Select(n => n.Id));
        var seen = new HashSet<int>();
        foreach (var edge in graph.Edges)
        {
            if (!CountsAsChanceRow(edge)) continue;
            if (!removed.Contains(edge.ToNodeId)) continue;
            if (removed.Contains(edge.FromNodeId)) continue;
            if (!alive.Contains(edge.FromNodeId)) continue;
            if (seen.Add(edge.FromNodeId)) result.Add(edge.FromNodeId);
        }
        return result;
    }

    /// <summary>
    /// Source node of a single deleted link when that link owned a standalone chance
    /// row; -1 for merged/redirect links, a missing source, or a source already removed.
    /// </summary>
    public static int AffectedSourceForEdge(ComboGraph? graph, ComboEdge? edge)
    {
        if (graph == null || edge == null || !CountsAsChanceRow(edge)) return -1;
        return graph.Nodes.Any(n => n.Id == edge.FromNodeId && !n.Removed)
            ? edge.FromNodeId
            : -1;
    }

    private static bool CountsAsChanceRow(ComboEdge edge)
        => edge.IsNewLink && !edge.IsRedirect && edge.MergedIntoStruct < 0;
}
