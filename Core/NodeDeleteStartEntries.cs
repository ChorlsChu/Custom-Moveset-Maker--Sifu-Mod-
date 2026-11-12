using System.Collections.Generic;
using System.Linq;

namespace SifuMovesetEditor;

/// <summary>
/// Start-entry cleanup for node deletion. Lives in Core (not the window) so the
/// reset-vs-drop rule can be exercised by a smoke test without a running editor.
///
/// Rule: authored targets are deleted, while vanilla rows the user retargeted snap
/// back to their <see cref="StartTransition.OriginalTargetTreeIndex"/> only when that
/// original target still exists in the graph; otherwise the key/row is dropped.
/// Multi-key rows reset only the keys that pointed at the deleted node.
/// </summary>
public static class NodeDeleteStartEntries
{
    /// <summary>True when a start transition (or one of its condition keys) targets
    /// the given graph node id.</summary>
    public static bool TargetsNode(StartTransition t, int nodeId)
        => t.TargetNodeId == nodeId
           || (t.KeyTargetNodeIds?.Count > 0 && t.KeyTargetNodeIds.Contains(nodeId));

    /// <summary>
    /// Cleans every live start row that targets <paramref name="nodeId"/> before the
    /// node is removed. Returns the number of entries that had at least one row touched.
    /// </summary>
    public static int Strip(ComboGraph graph, ComboNode deleted)
    {
        if (graph?.StartEntries == null || deleted == null) return 0;

        int touchedEntries = 0;
        foreach (var entry in graph.StartEntries.ToList())
        {
            if (entry.Removed) continue;

            var rows = entry.Transitions
                .Where(t => !t.Removed && TargetsNode(t, deleted.Id))
                .ToList();
            if (rows.Count == 0) continue;

            touchedEntries++;
            foreach (var t in rows)
            {
                if (t.HasKeyModel)
                {
                    t.KeyTargetNames ??= new List<string>();
                    bool droppedKey = false;
                    for (int ki = t.KeyTargetNodeIds.Count - 1; ki >= 0; ki--)
                    {
                        if (t.KeyTargetNodeIds[ki] != deleted.Id) continue;

                        if (TryResolveOriginalKey(graph, t, ki, deleted, out int origId,
                                out int origTree, out string origName))
                        {
                            t.KeyTargetNodeIds[ki] = origId;
                            t.KeyTargetTreeIndices[ki] = origTree;
                            if (t.KeyTargetNames != null && ki < t.KeyTargetNames.Count)
                                t.KeyTargetNames[ki] = origName;
                        }
                        else
                        {
                            t.Keys.RemoveAt(ki);
                            t.KeyTargetTreeIndices.RemoveAt(ki);
                            t.KeyTargetNodeIds.RemoveAt(ki);
                            t.KeyTargetNames.RemoveAt(ki);
                            droppedKey = true;
                        }
                    }

                    if (t.KeyTargetNodeIds.Count == 0)
                    {
                        DropRow(entry, t);
                        continue;
                    }

                    if (droppedKey)
                    {
                        t.KeyCount = t.Keys.Count;
                        t.TargetSummary = string.Join(", ",
                            t.Keys.Zip(t.KeyTargetTreeIndices, (k, v) => $"{k}→{v}"));
                    }

                    // Single-target fallback follows the first surviving key (same rule
                    // the dialog uses when a key is re-routed).
                    if (t.TargetNodeId == deleted.Id || droppedKey)
                    {
                        t.TargetNodeId = t.KeyTargetNodeIds[0];
                        t.TargetTreeIndex = t.KeyTargetTreeIndices[0];
                        t.TargetName = t.KeyTargetNames.Count > 0 ? t.KeyTargetNames[0] : "";
                    }
                    continue;
                }

                if (t.TargetNodeId != deleted.Id) continue;
                if (TryResolveOriginalTarget(graph, t, deleted, out int targetId,
                        out int targetTree, out string targetName))
                {
                    t.TargetNodeId = targetId;
                    t.TargetTreeIndex = targetTree;
                    t.TargetName = targetName;
                }
                else
                {
                    DropRow(entry, t);
                }
            }

            if (entry.Transitions.Count(t => !t.Removed) == 0)
            {
                if (entry.VanillaIndex >= 0) entry.Removed = true;
                else graph.StartEntries.Remove(entry);
            }
        }

        return touchedEntries;
    }

    private static void DropRow(StartEntry entry, StartTransition t)
    {
        // Vanilla rows stay in the model marked Removed so export knows to drop them;
        // rows the user added can leave the list outright.
        if (t.OriginalIndex >= 0) t.Removed = true;
        else entry.Transitions.Remove(t);
    }

    private static bool TryResolveOriginalKey(ComboGraph graph, StartTransition t, int keyIndex,
        ComboNode deleted, out int nodeId, out int treeIndex, out string name)
    {
        nodeId = -1;
        treeIndex = -1;
        name = "";
        // Keys added in the editor have no vanilla baseline at this index - drop them.
        if (keyIndex >= t.OriginalKeyTargetTreeIndices.Count) return false;
        return TryResolve(graph, t.OriginalKeyTargetTreeIndices[keyIndex], -1, deleted,
            out nodeId, out treeIndex, out name);
    }

    private static bool TryResolveOriginalTarget(ComboGraph graph, StartTransition t,
        ComboNode deleted, out int nodeId, out int treeIndex, out string name)
        => TryResolve(graph, t.OriginalTargetTreeIndex, t.OriginalTargetNodeId, deleted,
            out nodeId, out treeIndex, out name);

    /// <summary>
    /// Resolves a vanilla target. A live non-redirect node wins; a tree index with no
    /// node is a condensed (conduit) slot that still exists in the asset and can be
    /// restored as-is. The node being deleted and rows/nodes already marked Removed
    /// count as gone.
    /// </summary>
    private static bool TryResolve(ComboGraph graph, int treeIndex, int nodeId,
        ComboNode deleted, out int resolvedId, out int resolvedTree, out string name)
    {
        resolvedId = -1;
        resolvedTree = -1;
        name = "";

        if (nodeId >= 0 && nodeId == deleted.Id) return false;
        if (treeIndex >= 0 && treeIndex == deleted.TreeIndex) return false;

        ComboNode? node = null;
        if (nodeId >= 0)
            node = graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null && treeIndex >= 0)
            node = graph.Nodes.FirstOrDefault(n => n.TreeIndex == treeIndex);

        if (node != null)
        {
            if (node.Removed || node.IsRedirect) return false;
            resolvedId = node.Id;
            resolvedTree = node.TreeIndex;
            name = !string.IsNullOrEmpty(node.DisplayName) ? node.DisplayName
                : !string.IsNullOrEmpty(node.Name) ? node.Name
                : $"#{node.Id}";
            return true;
        }

        // No graph node but a real tree index: condensed/conduit target kept in the asset.
        if (treeIndex >= 0)
        {
            resolvedTree = treeIndex;
            return true;
        }
        return false;
    }
}
