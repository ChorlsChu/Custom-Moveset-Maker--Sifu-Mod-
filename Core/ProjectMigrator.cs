using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace SifuMovesetEditor;

/// <summary>Per-unit outcome of one legacy-schema migration: edit overlays applied,
/// edge-reconciliation buckets (every old edge lands in exactly one), and warnings.</summary>
public sealed class MigrationUnitReport
{
    public string UnitKey { get; set; } = "";
    public bool Migrated { get; set; }
    public string? Error { get; set; }

    // Node-edit overlays carried onto the fresh parse (by TreeIndex).
    public int AnimSwaps { get; set; }
    public int TuningEdits { get; set; }
    public int DelayWindows { get; set; }
    public int RedirectMods { get; set; }
    public int RemovedFlags { get; set; }
    public int CustomNodes { get; set; }
    public int OldNodesUnmapped { get; set; }
    public int PositionsRemapped { get; set; }

    // Old-side edge buckets (sum == old edge count for a clean migration).
    public int EdgesKept { get; set; }
    public int EdgesRedirectKept { get; set; }
    public int EdgesArtifact { get; set; }
    public int EdgesUserAdded { get; set; }
    public int EdgesUnexplained { get; set; }
    // Fresh-side additions / drops.
    public int EdgesFreshAdded { get; set; }
    public int EdgesOwnerRehomed { get; set; }
    public int EdgesUserDeleted { get; set; }
    public int EdgesRedirectReplaced { get; set; }

    public List<string> Warnings { get; } = new();
}

/// <summary>Aggregate of a migration run (all legacy unit caches in one project).</summary>
public sealed class MigrationReport
{
    public List<MigrationUnitReport> Units { get; } = new();
    public int MigratedCount => Units.Count(u => u.Migrated);
    public int FailureCount => Units.Count(u => u.Error != null);
    public int WarningCount => Units.Sum(u => u.Warnings.Count);
    public int AnimSwaps => Units.Sum(u => u.AnimSwaps);
    public int TuningEdits => Units.Sum(u => u.TuningEdits);
    public int DelayWindows => Units.Sum(u => u.DelayWindows);
    public int RedirectMods => Units.Sum(u => u.RedirectMods);
    public int CustomNodes => Units.Sum(u => u.CustomNodes);

    public string Summary()
    {
        if (MigratedCount == 0 && FailureCount == 0) return "";
        if (MigratedCount == 0) return $"legacy upgrade failed for {FailureCount} unit(s) - kept old graphs";
        var parts = new List<string> { $"{MigratedCount} unit graph(s) rebuilt (action/event nodes restored)" };
        if (AnimSwaps > 0) parts.Add($"{AnimSwaps} anim swaps");
        if (RedirectMods > 0) parts.Add($"{RedirectMods} redirect retargets");
        if (TuningEdits > 0) parts.Add($"{TuningEdits} buildup/range edits");
        if (CustomNodes > 0) parts.Add($"{CustomNodes} custom nodes");
        if (FailureCount > 0) parts.Add($"{FailureCount} unit(s) kept the legacy graph");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Upgrades v1.2-era .sifu-edit unit caches to the current combo-graph schema.
///
/// Those saves predate Phase 2 (2026-10-03), which surfaced combo action/event nodes
/// as graph nodes - their snapshots hold only the condensed moveset graph (animless
/// conduits were removed and their edges remapped at parse time), and they carry none
/// of the newer fields (TreeRows, StartEntries, SourceGamePath, edge owners). Loading
/// them straight from cache therefore renders a graph missing every event / BP_AiAction
/// node.
///
/// Migration re-parses the unit's source combo (resolved through the import catalog by
/// unit key) and overlays the saved edits onto that fresh graph matched per TreeIndex
/// group (one asset tree node can split into direction variants that share a TreeIndex;
/// variants pair content-first then by order - the old parse's sequential node ids are
/// not stable: Id != TreeIndex for most nodes in old saves):
///   - anim swaps (AnimPath != DefaultAnimPath with a known default),
///   - AttackDB buildup/range tuning and delay windows (null = vanilla on both sides),
///   - Removed flags,
///   - redirect retargets (ResolvedRedirectNodeId remapped through TreeIndex),
///   - user-created nodes (TreeIndex -1, re-id'd on collision),
///   - node positions (nodes without a saved one - new action/event nodes, sparse
///     saves - fall through to the app's existing LayoutComboGraph pass),
///   - user override maps (struct keys are TreeIndex-stable; edge-keyed maps are
///     re-keyed through the id remap).
/// Edges are reconciled old-vs-fresh rather than taken from either side alone: old
/// graphs contracted action/event chains away (A->E->B was saved as A->B), and some
/// fresh edges postdate the save (owner-tagged pass-through rows). Every old edge lands
/// in exactly one bucket - kept, redirect-legacy, contraction artifact (dropped), or
/// user-added (kept as IsNewLink) - and fresh edges are added only when owner-tagged or
/// incident to a new node, so user-deleted links stay deleted. Failed units keep their
/// legacy graph (the caller's existing backfills still run for them).
/// </summary>
public static class ProjectMigrator
{
    /// <summary>Max edges a contraction artifact may span (A->E1->..->B through new nodes).</summary>
    internal const int ArtifactPathMaxDepth = 8;

    /// <summary>True for a cache written before edge-owner persistence (v1.2-era).</summary>
    public static bool NeedsMigration(UnitCacheEntry entry) => entry.NeedsEdgeOwnerBackfill;

    /// <summary>
    /// Migrates every cache flagged <see cref="UnitCacheEntry.NeedsEdgeOwnerBackfill"/>.
    /// Mutates the cache entries in place (Graph/Positions replaced, flag cleared);
    /// untouched caches are left exactly as they were. Parse or resolution failures keep
    /// the legacy graph for that unit and record the error in its report.
    /// </summary>
    public static MigrationReport MigrateLegacyCaches(
        Dictionary<string, UnitCacheEntry> caches,
        AnimationParser parser,
        Action<string>? progress = null)
    {
        var report = new MigrationReport();
        var catalog = MainWindow.BuildImportComboCatalog();
        var targets = caches.Where(kv => kv.Value.NeedsEdgeOwnerBackfill).ToList();
        int done = 0;
        foreach (var kv in targets)
        {
            done++;
            progress?.Invoke($"{done}/{targets.Count} {kv.Key}");
            var unit = new MigrationUnitReport { UnitKey = kv.Key };
            report.Units.Add(unit);
            var entry = kv.Value;
            try
            {
                var cat = catalog.Values.FirstOrDefault(e =>
                    string.Equals(e.UnitKey, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (cat == null)
                {
                    unit.Error = "source combo not found in import catalog";
                    unit.Warnings.Add(unit.Error);
                    continue;
                }
                var fresh = parser.LoadComboTreeFromPath(cat.GamePath, cat.WeaponName);
                if (fresh == null)
                {
                    unit.Error = "source combo parse returned null";
                    unit.Warnings.Add(unit.Error);
                    continue;
                }

                var newPositions = MigrateUnit(entry.Graph, fresh, entry.Positions, unit);
                // The fresh parse re-introduces stance nodes for MainChar weapon
                // variants; the saved cache had already been sanitized pre-migration.
                ProjectManager.SanitizeMainCharCache(kv.Key, fresh, newPositions, entry.ActiveWeapon);
                entry.Graph = fresh;
                entry.Positions = newPositions;
                entry.NeedsEdgeOwnerBackfill = false;
                unit.Migrated = true;
            }
            catch (Exception ex)
            {
                unit.Error = ex.Message;
                unit.Warnings.Add($"migration failed, keeping legacy graph: {ex.Message}");
                ErrorLog.Write("MIGRATE", new Exception($"{kv.Key}: {ex.Message}", ex));
            }
        }
        return report;
    }

    /// <summary>
    /// Overlays the legacy graph's edits onto <paramref name="fresh"/> and reconciles
    /// its edges against <paramref name="old"/>; returns the remapped position table.
    /// Mutates only <paramref name="fresh"/> - the old graph stays intact so a failure
    /// mid-run can fall back to it. Unit report fields are appended to.
    /// </summary>
    public static Dictionary<int, Point> MigrateUnit(
        ComboGraph old, ComboGraph fresh, Dictionary<int, Point> oldPositions, MigrationUnitReport unit)
    {
        // ---- id maps (old parse ids -> fresh parse ids, both via TreeIndex) ----
        var freshById = new Dictionary<int, ComboNode>();
        foreach (var n in fresh.Nodes) freshById[n.Id] = n;

        var oldById = new Dictionary<int, ComboNode>();
        foreach (var n in old.Nodes)
            if (!oldById.ContainsKey(n.Id)) oldById[n.Id] = n;

        // One asset tree node can split into several direction variants that all
        // carry the same TreeIndex (ParseComboNodeVariants), and old saves store the
        // variants too - so old->fresh is matched per TreeIndex group: content first
        // (AnimPath+InputLabel), then order, never first-wins.
        var freshGroups = new Dictionary<int, List<ComboNode>>();
        foreach (var n in fresh.Nodes)
        {
            if (n.TreeIndex < 0 || n.IsRoot) continue;
            if (!freshGroups.TryGetValue(n.TreeIndex, out var list)) freshGroups[n.TreeIndex] = list = new List<ComboNode>();
            list.Add(n);
        }
        var oldGroups = new Dictionary<int, List<ComboNode>>();
        foreach (var n in old.Nodes)
        {
            if (n.TreeIndex < 0 || n.IsRoot) continue;
            if (!oldGroups.TryGetValue(n.TreeIndex, out var list)) oldGroups[n.TreeIndex] = list = new List<ComboNode>();
            list.Add(n);
        }

        var oldToFreshId = new Dictionary<int, int>();
        var pairs = new List<(ComboNode o, ComboNode f)>();

        // Roots (TreeIndex -1, Id usually -1) map to each other so root positions carry.
        var oldRoot = old.Nodes.FirstOrDefault(n => n.IsRoot);
        var freshRoot = fresh.Nodes.FirstOrDefault(n => n.IsRoot);
        if (oldRoot != null && freshRoot != null) oldToFreshId[oldRoot.Id] = freshRoot.Id;

        void Pair(ComboNode o, ComboNode f)
        {
            if (!oldToFreshId.ContainsKey(o.Id)) oldToFreshId[o.Id] = f.Id;
            pairs.Add((o, f));
        }

        foreach (var g in oldGroups)
        {
            if (!freshGroups.TryGetValue(g.Key, out var fg))
            {
                foreach (var o in g.Value)
                {
                    unit.OldNodesUnmapped++;
                    unit.Warnings.Add($"old node Id={o.Id} TreeIndex={g.Key} name='{o.Name}' has no vanilla counterpart");
                }
                continue;
            }
            var pool = new List<ComboNode>(fg);
            var unmatched = new List<ComboNode>();
            foreach (var o in g.Value)
            {
                var hits = pool.FindAll(f => f.AnimPath == o.AnimPath && f.InputLabel == o.InputLabel);
                if (hits.Count == 1)
                {
                    Pair(o, hits[0]);
                    pool.Remove(hits[0]);
                }
                else unmatched.Add(o);
            }
            foreach (var o in unmatched)
            {
                if (pool.Count > 0)
                {
                    Pair(o, pool[0]);
                    pool.RemoveAt(0);
                }
                else
                {
                    unit.OldNodesUnmapped++;
                    unit.Warnings.Add($"old node Id={o.Id} TreeIndex={g.Key} name='{o.Name}' has no vanilla counterpart");
                }
            }
        }

        // User-created nodes (TreeIndex -1): carry over, re-id on collision. The old
        // graph is discarded after migration, so reusing its node objects is safe.
        int nextCustomId = fresh.Nodes.Count > 0 ? fresh.Nodes.Max(n => n.Id) + 1 : 0;
        foreach (var o in old.Nodes)
        {
            if (o.IsRoot || o.TreeIndex >= 0) continue;
            int oldId = o.Id;
            if (oldId < 0 || freshById.ContainsKey(oldId)) oldId = nextCustomId++;
            oldToFreshId[o.Id] = oldId;
            if (o.Id != oldId) o.Id = oldId;
            fresh.Nodes.Add(o);
            freshById[oldId] = o;
            unit.CustomNodes++;
        }

        // ---- node-edit overlays ----
        foreach (var (o, f) in pairs)
        {
            if (!o.IsRoot && o.AnimPath.Length > 0 && o.DefaultAnimPath.Length > 0
                && o.AnimPath != o.DefaultAnimPath)
            {
                f.AnimPath = o.AnimPath;
                f.SourceDBPath = o.SourceDBPath;
                f.ImportedDisplayName = o.ImportedDisplayName;
                unit.AnimSwaps++;
            }
            if (o.AttackBuildupFrames.HasValue) { f.AttackBuildupFrames = o.AttackBuildupFrames; unit.TuningEdits++; }
            if (o.AttackGameplayRange.HasValue) { f.AttackGameplayRange = o.AttackGameplayRange; unit.TuningEdits++; }
            if (o.DelayWindowLo.HasValue) { f.DelayWindowLo = o.DelayWindowLo; unit.DelayWindows++; }
            if (o.DelayWindowHi.HasValue) { f.DelayWindowHi = o.DelayWindowHi; unit.DelayWindows++; }
            if (o.Removed) { f.Removed = true; unit.RemovedFlags++; }
        }

        // ---- redirect retargets ----
        var moddedRedirectFreshIds = new HashSet<int>();
        // vanilla-target fresh id -> new-target fresh id, for owner rows the old save
        // already moved onto the new target when the retarget happened.
        var retargetMove = new Dictionary<int, int>();
        foreach (var o in old.Nodes)
        {
            if (!o.IsRedirect || o.ResolvedRedirectNodeId < 0) continue;
            if (!old.RedirectOriginalTargets.TryGetValue(o.Id, out var orig)) continue;
            if (o.ResolvedRedirectNodeId == orig) continue;
            if (!oldToFreshId.TryGetValue(o.Id, out var fid) || !freshById.TryGetValue(fid, out var fn))
            {
                unit.Warnings.Add($"redirect retarget on old node Id={o.Id} dropped (no vanilla counterpart)");
                continue;
            }
            if (!oldToFreshId.TryGetValue(o.ResolvedRedirectNodeId, out var tid))
            {
                unit.Warnings.Add($"redirect retarget of old node Id={o.Id} dropped (target Id={o.ResolvedRedirectNodeId} has no vanilla counterpart)");
                continue;
            }
            int vanillaTarget = fn.ResolvedRedirectNodeId;
            fn.ResolvedRedirectNodeId = tid;
            moddedRedirectFreshIds.Add(fid);
            if (vanillaTarget >= 0 && vanillaTarget != tid) retargetMove[vanillaTarget] = tid;
            unit.RedirectMods++;
        }

        // ---- user override maps (struct keys are TreeIndex-stable) ----
        foreach (var kv in old.TreeRowProbabilityOverrides)
            if (!fresh.TreeRowProbabilityOverrides.TryGetValue(kv.Key, out var dst))
                fresh.TreeRowProbabilityOverrides[kv.Key] = new Dictionary<int, float>(kv.Value);
            else foreach (var r in kv.Value) dst[r.Key] = r.Value;
        foreach (var kv in old.TreeConditionParamOverrides)
            if (!fresh.TreeConditionParamOverrides.TryGetValue(kv.Key, out var dst))
                fresh.TreeConditionParamOverrides[kv.Key] = new Dictionary<string, string>(kv.Value);
            else foreach (var p in kv.Value) dst[p.Key] = p.Value;
        foreach (var kv in old.TreeRowConditionOverrides)
            if (!fresh.TreeRowConditionOverrides.TryGetValue(kv.Key, out var dst))
                fresh.TreeRowConditionOverrides[kv.Key] = kv.Value.ToDictionary(r => r.Key, r => r.Value);
            else foreach (var r in kv.Value) dst[r.Key] = r.Value;
        foreach (var kv in old.TreeEdgeConditionOverrides)
        {
            var parts = kv.Key.Split('|', 2);
            var ids = parts[0].Split('>');
            if (ids.Length == 2 && int.TryParse(ids[0], out var of) && int.TryParse(ids[1], out var ot)
                && oldToFreshId.TryGetValue(of, out var nf) && oldToFreshId.TryGetValue(ot, out var nt))
            {
                var newKey = ComboGraph.EdgeConditionKey(new ComboEdge
                {
                    FromNodeId = nf,
                    ToNodeId = nt,
                    InputName = parts.Length > 1 ? parts[1] : ""
                });
                fresh.TreeEdgeConditionOverrides[newKey] = kv.Value;
            }
            else
            {
                unit.Warnings.Add($"edge condition override '{kv.Key}' dropped (endpoint missing)");
            }
        }

        // Old saves predate StartEntries; a non-empty old container means user edits -
        // keep it and re-resolve targets against the fresh ids (TreeIndex-stable).
        if (old.StartEntries.Count > 0)
        {
            fresh.StartEntries = old.StartEntries;
            fresh.RemapStartEntryTargets(fresh.StartEntries);
            fresh.RehydrateStartEntryKeyModels();
        }

        // ---- edge reconciliation ----
        var newEdges = ReconcileEdges(old, fresh, oldToFreshId, oldById, moddedRedirectFreshIds, retargetMove, unit);

        // ---- positions (only remap what the save had; nodes without a saved
        // position - new action/event nodes, sparse saves - fall through to the
        // app's existing LayoutComboGraph pass, which computes their columns and
        // restores saved positions over the top) ----
        var newPositions = new Dictionary<int, Point>();
        foreach (var kvp in oldPositions)
        {
            if (oldToFreshId.TryGetValue(kvp.Key, out var fid)) newPositions[fid] = kvp.Value;
        }
        unit.PositionsRemapped = newPositions.Count;
        return newPositions;
    }

    /// <summary>
    /// Rebuilds the fresh graph's edge list: base = old edges mapped through TreeIndex
    /// (kept / redirect-legacy / contraction artifact / user-add), then fresh-only edges
    /// added when owner-tagged or incident to a new node. Returns the final edge list;
    /// assigns it onto <paramref name="fresh"/>.
    /// </summary>
    private static List<ComboEdge> ReconcileEdges(
        ComboGraph old, ComboGraph fresh,
        Dictionary<int, int> oldToFreshId, Dictionary<int, ComboNode> oldById,
        HashSet<int> moddedRedirectFreshIds, Dictionary<int, int> retargetMove,
        MigrationUnitReport unit)
    {
        var freshEdgeByPair = new Dictionary<(int, int), ComboEdge>();
        foreach (var e in fresh.Edges)
            if (!freshEdgeByPair.ContainsKey((e.FromNodeId, e.ToNodeId)))
                freshEdgeByPair[(e.FromNodeId, e.ToNodeId)] = e;
        var freshPairs = new HashSet<(int, int)>(freshEdgeByPair.Keys);

        var mappedFreshIds = new HashSet<int>(oldToFreshId.Values);
        var newFreshIds = new HashSet<int>();
        foreach (var n in fresh.Nodes)
            if (!mappedFreshIds.Contains(n.Id)) newFreshIds.Add(n.Id);

        var adjacency = new Dictionary<int, List<int>>();
        foreach (var e in fresh.Edges)
        {
            if (!adjacency.TryGetValue(e.FromNodeId, out var list))
                adjacency[e.FromNodeId] = list = new List<int>();
            list.Add(e.ToNodeId);
        }

        var result = new List<ComboEdge>();
        var basePairs = new HashSet<(int, int)>();

        foreach (var oe in old.Edges)
        {
            if (!oldToFreshId.TryGetValue(oe.FromNodeId, out var fromId) ||
                !oldToFreshId.TryGetValue(oe.ToNodeId, out var toId))
            {
                unit.EdgesUnexplained++;
                unit.Warnings.Add($"old edge {oe.FromNodeId}->{oe.ToNodeId} dropped (endpoint missing from vanilla parse)");
                continue;
            }
            var pair = (fromId, toId);
            if (freshPairs.Contains(pair))
            {
                // Vanilla match: adopt the fresh edge object (current input names,
                // redirect flag and owner tag come with it).
                if (basePairs.Add(pair)) result.Add(freshEdgeByPair[pair]);
                unit.EdgesKept++;
                continue;
            }
            bool fromIsRedirect = oldById.TryGetValue(oe.FromNodeId, out var ofn) && ofn.IsRedirect;
            if (oe.IsRedirect || fromIsRedirect)
            {
                // Redirect's own edge: either its vanilla target (parse drift) or the
                // retarget swap the editor made (ApplyRetarget replaces the edge).
                // Keep it vanilla-shaped - export follows ResolvedRedirectNodeId, and
                // it must not read as an appended new link.
                if (basePairs.Add(pair)) result.Add(new ComboEdge
                {
                    FromNodeId = fromId,
                    ToNodeId = toId,
                    InputName = oe.InputName,
                    IsRedirect = true
                });
                unit.EdgesRedirectKept++;
                continue;
            }
            if (HasConduitPath(adjacency, newFreshIds, fromId, toId))
            {
                // Old save contracted an action/event chain (A->E->B saved as A->B);
                // the fresh edges (A,E) and (E,B) are added back below.
                unit.EdgesArtifact++;
                continue;
            }
            if (basePairs.Add(pair)) result.Add(new ComboEdge
            {
                FromNodeId = fromId,
                ToNodeId = toId,
                InputName = oe.InputName,
                IsNewLink = true
            });
            unit.EdgesUserAdded++;
        }

        foreach (var fe in fresh.Edges)
        {
            var pair = (fe.FromNodeId, fe.ToNodeId);
            if (basePairs.Contains(pair))
            {
                if (fe.OwnerRedirectNodeId >= 0 && freshByIdOrDefault(fresh, fe.OwnerRedirectNodeId) is { } owner)
                    owner.OwnedContinuation = true;
                continue;
            }
            if (fe.OwnerRedirectNodeId >= 0)
            {
                // Owner-tagged pass-through rows are the reason the edge-owner backfill
                // exists for old saves; they postdate the save or moved on retarget.
                if (retargetMove.TryGetValue(fe.FromNodeId, out var movedFrom) &&
                    basePairs.Contains((movedFrom, fe.ToNodeId)))
                {
                    var moved = result.FirstOrDefault(e => e.FromNodeId == movedFrom && e.ToNodeId == fe.ToNodeId);
                    if (moved != null)
                    {
                        if (moved.OwnerRedirectNodeId < 0 && !moved.IsNewLink)
                            moved.OwnerRedirectNodeId = fe.OwnerRedirectNodeId;
                        if (freshByIdOrDefault(fresh, fe.OwnerRedirectNodeId) is { } owner)
                            owner.OwnedContinuation = true;
                        unit.EdgesOwnerRehomed++;
                        continue;
                    }
                }
                result.Add(fe);
                if (freshByIdOrDefault(fresh, fe.OwnerRedirectNodeId) is { } own)
                    own.OwnedContinuation = true;
                unit.EdgesFreshAdded++;
                continue;
            }
            if (newFreshIds.Contains(fe.FromNodeId) || newFreshIds.Contains(fe.ToNodeId))
            {
                // Incident to an action/event/custom node the save never had.
                result.Add(fe);
                unit.EdgesFreshAdded++;
                continue;
            }
            // Both endpoints existed in the save but the edge is absent there: the user
            // deleted it - unless the From node is a retargeted redirect, whose vanilla
            // edge the retarget swap removed on purpose.
            if (moddedRedirectFreshIds.Contains(fe.FromNodeId)) unit.EdgesRedirectReplaced++;
            else unit.EdgesUserDeleted++;
        }

        fresh.Edges = result;
        return result;
    }

    private static ComboNode? freshByIdOrDefault(ComboGraph graph, int id)
    {
        for (int i = 0; i < graph.Nodes.Count; i++)
            if (graph.Nodes[i].Id == id) return graph.Nodes[i];
        return null;
    }

    /// <summary>True when the fresh graph has a path from->..->to whose internal nodes
    /// are all absent from the save (the contracted action/event chain).</summary>
    private static bool HasConduitPath(
        Dictionary<int, List<int>> adjacency, HashSet<int> newIds, int from, int to, int maxDepth = ArtifactPathMaxDepth)
    {
        var visited = new HashSet<int> { from };
        var frontier = new Queue<(int id, int depth)>();
        frontier.Enqueue((from, 0));
        while (frontier.Count > 0)
        {
            var (id, depth) = frontier.Dequeue();
            if (depth >= maxDepth) continue;
            if (!adjacency.TryGetValue(id, out var nexts)) continue;
            foreach (var next in nexts)
            {
                if (next == to) return true;
                if (newIds.Contains(next) && visited.Add(next)) frontier.Enqueue((next, depth + 1));
            }
        }
        return false;
    }

}
