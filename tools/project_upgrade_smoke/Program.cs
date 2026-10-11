using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using SifuMovesetEditor;

internal static class Program
{
    private static readonly string DefaultProject =
        @"C:\Users\Charles\Downloads\Sifu Modding\EnemiesHarderer.sifu-edit";
    private static readonly string DefaultGameRoot =
        @"C:\Users\Charles\Downloads\Sifu Modding\Movesets Prototypes\Moveset Maker\SifuMovesetEditor\bin\Debug\net10.0-windows\GameContent";

    private sealed class NodeSnap
    {
        public int Id;
        public int TreeIndex = -1;
        public bool IsRoot;
        public bool IsRedirect;
        public bool Removed;
        public string AnimPath = "";
        public string DefaultAnimPath = "";
        public string InputLabel = "";
        public int? Buildup;
        public float? Range;
        public int? DelayLo;
        public int? DelayHi;
        public int Resolved = -1;
    }

    private sealed class UnitSnap
    {
        public string Key = "";
        public int NodeCount;
        public int EdgeCount;
        public List<NodeSnap> Nodes = new();
        public Dictionary<int, int> RedirectOriginals = new();
        public HashSet<int> PositionKeys = new();
    }

    private static int Main(string[] args)
    {
        string projectPath = args.Length > 0 ? args[0] : DefaultProject;
        string gameRoot = args.Length > 1 ? args[1] : DefaultGameRoot;
        if (!File.Exists(projectPath)) { Console.WriteLine($"FAIL: project not found: {projectPath}"); return 1; }
        if (!Directory.Exists(gameRoot)) { Console.WriteLine($"FAIL: game content not found: {gameRoot}"); return 1; }

        int fail = 0;
        void Check(bool ok, string what)
        {
            if (!ok) { Console.WriteLine($"  FAIL: {what}"); fail++; }
        }

        var (project, _, _, _, _, _) = ProjectManager.Load(projectPath);
        var caches = ProjectManager.LoadUnitCaches(project);
        if (caches == null || caches.Count == 0) { Console.WriteLine("FAIL: no unit caches in project"); return 1; }

        // Snapshot the legacy graphs before migration mutates anything; the expected
        // edit totals are recounted from the snapshots so the asserts are self-contained.
        var snaps = new List<UnitSnap>();
        int expSwaps = 0, expTuning = 0, expDelay = 0, expMods = 0;
        foreach (var kv in caches)
        {
            var g = kv.Value.Graph;
            var s = new UnitSnap { Key = kv.Key, NodeCount = g.Nodes.Count, EdgeCount = g.Edges.Count };
            foreach (var r in g.RedirectOriginalTargets) s.RedirectOriginals[r.Key] = r.Value;
            foreach (var p in kv.Value.Positions.Keys) s.PositionKeys.Add(p);
            foreach (var n in g.Nodes)
            {
                s.Nodes.Add(new NodeSnap
                {
                    Id = n.Id,
                    TreeIndex = n.TreeIndex,
                    IsRoot = n.IsRoot,
                    IsRedirect = n.IsRedirect,
                    Removed = n.Removed,
                    AnimPath = n.AnimPath,
                    DefaultAnimPath = n.DefaultAnimPath,
                    InputLabel = n.InputLabel,
                    Buildup = n.AttackBuildupFrames,
                    Range = n.AttackGameplayRange,
                    DelayLo = n.DelayWindowLo,
                    DelayHi = n.DelayWindowHi,
                    Resolved = n.ResolvedRedirectNodeId
                });
                if (n.TreeIndex >= 0 && !n.IsRoot && n.AnimPath.Length > 0
                    && n.DefaultAnimPath.Length > 0 && n.AnimPath != n.DefaultAnimPath) expSwaps++;
                if (n.AttackBuildupFrames.HasValue) expTuning++;
                if (n.AttackGameplayRange.HasValue) expTuning++;
                if (n.DelayWindowLo.HasValue) expDelay++;
                if (n.DelayWindowHi.HasValue) expDelay++;
                if (n.IsRedirect && n.ResolvedRedirectNodeId >= 0
                    && s.RedirectOriginals.TryGetValue(n.Id, out var orig)
                    && n.ResolvedRedirectNodeId != orig) expMods++;
            }
            snaps.Add(s);
        }

        using var parser = new AnimationParser();
        parser.Initialize(gameRoot, Path.Combine(gameRoot, "Content"));
        var report = ProjectMigrator.MigrateLegacyCaches(caches, parser);

        Console.WriteLine($"project: {projectPath}");
        Console.WriteLine($"units={caches.Count} migrated={report.MigratedCount} failures={report.FailureCount} warnings={report.WarningCount}");
        Console.WriteLine($"expected edits: swaps={expSwaps} buildupRange={expTuning} delayWindows={expDelay} redirectMods={expMods}");

        // Already-current projects (every cache has EdgeOwnersPersisted=true) migrate
        // 0 units by design - the fixture gets re-saved by in-app loads, so accept
        // "nothing flagged" and only enforce full coverage when migration ran.
        bool alreadyCurrent = report.MigratedCount == 0 && report.FailureCount == 0;
        if (alreadyCurrent)
            Console.WriteLine("  NOTE: project already schema-current (0 units flagged) - migration path not exercised");

        Check(report.FailureCount == 0, $"{report.FailureCount} unit(s) failed to migrate");
        if (!alreadyCurrent)
        {
            Check(report.MigratedCount == caches.Count, $"migrated {report.MigratedCount} of {caches.Count} units");
            Check(report.AnimSwaps == expSwaps, $"anim swaps preserved {report.AnimSwaps} != expected {expSwaps}");
            Check(report.TuningEdits == expTuning, $"buildup/range edits {report.TuningEdits} != expected {expTuning}");
            Check(report.DelayWindows == expDelay, $"delay windows {report.DelayWindows} != expected {expDelay}");
            Check(report.RedirectMods == expMods, $"redirect retargets {report.RedirectMods} != expected {expMods}");
        }

        foreach (var u in report.Units)
        {
            Console.WriteLine($"  {u.UnitKey}: migrated={u.Migrated} err={u.Error ?? "-"} swaps={u.AnimSwaps} tuning={u.TuningEdits} mods={u.RedirectMods} custom={u.CustomNodes} unmapped={u.OldNodesUnmapped} | edges kept={u.EdgesKept} legacy={u.EdgesRedirectKept} art={u.EdgesArtifact} add={u.EdgesUserAdded} unexplained={u.EdgesUnexplained} freshAdd={u.EdgesFreshAdded} rehomed={u.EdgesOwnerRehomed} del={u.EdgesUserDeleted} redirRepl={u.EdgesRedirectReplaced} | pos remapped={u.PositionsRemapped} warn={u.Warnings.Count}");
            foreach (var w in u.Warnings.Take(3)) Console.WriteLine($"      warn: {w}");
            if (!u.Migrated) continue;

            var snap = snaps.First(s => s.Key == u.UnitKey);
            var entry = caches[u.UnitKey];
            var g = entry.Graph;

            var byId = new Dictionary<int, ComboNode>();
            foreach (var n in g.Nodes) byId[n.Id] = n;

            // Old->fresh pairing per TreeIndex group (direction variants share one
            // TreeIndex): content first (AnimPath+InputLabel), then order. Independent
            // mirror of the migrator's spec, so a wrong pairing fails these checks.
            var freshGroups = new Dictionary<int, List<ComboNode>>();
            foreach (var n in g.Nodes)
            {
                if (n.TreeIndex < 0 || n.IsRoot) continue;
                if (!freshGroups.TryGetValue(n.TreeIndex, out var fl)) freshGroups[n.TreeIndex] = fl = new List<ComboNode>();
                fl.Add(n);
            }
            var oldGroups = new Dictionary<int, List<NodeSnap>>();
            foreach (var n in snap.Nodes)
            {
                if (n.TreeIndex < 0 || n.IsRoot) continue;
                if (!oldGroups.TryGetValue(n.TreeIndex, out var ol)) oldGroups[n.TreeIndex] = ol = new List<NodeSnap>();
                ol.Add(n);
            }
            var pair = new Dictionary<int, ComboNode>();   // old node Id -> fresh node
            foreach (var og in oldGroups)
            {
                if (!freshGroups.TryGetValue(og.Key, out var fl)) continue;
                var pool = new List<ComboNode>(fl);
                var unmatched = new List<NodeSnap>();
                foreach (var o in og.Value)
                {
                    var hits = pool.FindAll(f => f.AnimPath == o.AnimPath && f.InputLabel == o.InputLabel);
                    if (hits.Count == 1) { pair[o.Id] = hits[0]; pool.Remove(hits[0]); }
                    else unmatched.Add(o);
                }
                foreach (var o in unmatched)
                {
                    if (pool.Count > 0) { pair[o.Id] = pool[0]; pool.RemoveAt(0); }
                }
            }

            Check(u.OldNodesUnmapped == 0, $"{u.UnitKey}: {u.OldNodesUnmapped} old node(s) missing from vanilla parse");
            Check(u.EdgesUnexplained == 0, $"{u.UnitKey}: {u.EdgesUnexplained} old edge(s) dropped unexplained");
            int bucketSum = u.EdgesKept + u.EdgesRedirectKept + u.EdgesArtifact + u.EdgesUserAdded + u.EdgesUnexplained;
            Check(bucketSum == snap.EdgeCount, $"{u.UnitKey}: edge buckets {bucketSum} != old edge count {snap.EdgeCount}");

            int oldCustom = snap.Nodes.Count(n => n.TreeIndex < 0 && !n.IsRoot);
            Check(u.CustomNodes == oldCustom, $"{u.UnitKey}: custom nodes {u.CustomNodes} != old {oldCustom}");
            Check(g.Nodes.Count(n => n.TreeIndex < 0 && !n.IsRoot) == oldCustom,
                $"{u.UnitKey}: migrated custom node count mismatch");

            foreach (var n in snap.Nodes)
            {
                if (n.IsRoot || n.TreeIndex < 0) continue;
                if (!pair.TryGetValue(n.Id, out var fn))
                {
                    Check(false, $"{u.UnitKey}: old node Id={n.Id} TreeIndex={n.TreeIndex} name missing after migration");
                    continue;
                }
                bool expectSwap = n.AnimPath.Length > 0 && n.DefaultAnimPath.Length > 0
                    && n.AnimPath != n.DefaultAnimPath;
                if (expectSwap)
                    Check(fn.AnimPath == n.AnimPath,
                        $"{u.UnitKey}: swap TreeIndex={n.TreeIndex} not preserved ('{fn.AnimPath}' != '{n.AnimPath}')");
                if (n.Buildup.HasValue)
                    Check(fn.AttackBuildupFrames == n.Buildup,
                        $"{u.UnitKey}: buildup on TreeIndex={n.TreeIndex} not preserved");
                if (n.Range.HasValue)
                    Check(fn.AttackGameplayRange == n.Range,
                        $"{u.UnitKey}: range on TreeIndex={n.TreeIndex} not preserved");
                if (n.DelayLo.HasValue) Check(fn.DelayWindowLo == n.DelayLo, $"{u.UnitKey}: delay lo on TreeIndex={n.TreeIndex} not preserved");
                if (n.DelayHi.HasValue) Check(fn.DelayWindowHi == n.DelayHi, $"{u.UnitKey}: delay hi on TreeIndex={n.TreeIndex} not preserved");
                if (n.Removed) Check(fn.Removed, $"{u.UnitKey}: Removed flag on TreeIndex={n.TreeIndex} not preserved");

                if (n.IsRedirect && n.Resolved >= 0
                    && snap.RedirectOriginals.TryGetValue(n.Id, out var orig) && n.Resolved != orig)
                {
                    var targetOld = snap.Nodes.FirstOrDefault(o => o.Id == n.Resolved);
                    if (targetOld == null || targetOld.TreeIndex < 0)
                        Check(false, $"{u.UnitKey}: redirect target Id={n.Resolved} missing from snapshot");
                    else if (!pair.TryGetValue(targetOld.Id, out var tgt))
                        Check(false, $"{u.UnitKey}: redirect target TreeIndex={targetOld.TreeIndex} missing after migration");
                    else
                        Check(fn.ResolvedRedirectNodeId == tgt.Id,
                            $"{u.UnitKey}: redirect retarget TreeIndex={n.TreeIndex} not preserved ({fn.ResolvedRedirectNodeId} != {tgt.Id})");
                }
            }

            foreach (var e in g.Edges)
                Check(byId.ContainsKey(e.FromNodeId) && byId.ContainsKey(e.ToNodeId),
                    $"{u.UnitKey}: dangling edge {e.FromNodeId}->{e.ToNodeId}");

            // Saved positions must carry over 1:1 (stale keys dropped); nodes without
            // one - new action/event nodes - are intentionally left to the app's
            // LayoutComboGraph pass, which restores saved positions over computed ones.
            int expectedPos = snap.PositionKeys.Count(id => snap.Nodes.Any(n => n.Id == id));
            Check(entry.Positions.Count == expectedPos,
                $"{u.UnitKey}: positions {entry.Positions.Count} != expected remapped {expectedPos}");

            Check(!string.IsNullOrEmpty(g.SourceGamePath), $"{u.UnitKey}: SourceGamePath empty");
            Check(g.TreeRows.Count > 0 || g.Edges.Count == 0, $"{u.UnitKey}: TreeRows empty");
            Check(!entry.NeedsEdgeOwnerBackfill, $"{u.UnitKey}: backfill flag still set");
        }

        // MainChar trees carry no action/event nodes, and migration must not lose
        // content. Counts may only GROW versus the legacy snapshot: the parse keeps
        // the vanilla redirect nodes now (the snapshot was saved while they were
        // stripped), so every node with a TreeIndex the snapshot lacked must be one.
        var mc = snaps.FirstOrDefault(s => s.Key.StartsWith("MainChar", StringComparison.OrdinalIgnoreCase));
        if (mc != null)
        {
            var mcg = caches[mc.Key].Graph;
            var snapTreeIdx = mc.Nodes.Where(n => n.TreeIndex >= 0).Select(n => n.TreeIndex).ToHashSet();
            var added = mcg.Nodes.Where(n => n.TreeIndex >= 0 && !snapTreeIdx.Contains(n.TreeIndex)).ToList();
            Check(mcg.Nodes.Count == mc.NodeCount + added.Count,
                $"{mc.Key}: MainChar counts changed {mc.NodeCount}/{mc.EdgeCount} -> {mcg.Nodes.Count}/{mcg.Edges.Count}");
            Check(mcg.Edges.Count >= mc.EdgeCount,
                $"{mc.Key}: MainChar edges shrank {mc.EdgeCount} -> {mcg.Edges.Count}");
            Check(added.All(n => n.IsRedirect),
                $"{mc.Key}: MainChar gained {added.Count} node(s), non-redirect: "
                + string.Join(", ", added.Where(n => !n.IsRedirect).Select(n => n.DisplayName)));
            Check(!mcg.Nodes.Any(n => n.IsActionNode || n.IsEventNode),
                $"{mc.Key}: MainChar graph carries action/event nodes");
        }

        int actionEvents = caches.Values.Sum(c => c.Graph.Nodes.Count(n => n.IsActionNode || n.IsEventNode));
        int enemyStartEntries = caches
            .Where(kv => !kv.Key.StartsWith("MainChar", StringComparison.OrdinalIgnoreCase))
            .Sum(kv => kv.Value.Graph.StartEntries.Count);
        int totalNodes = caches.Values.Sum(c => c.Graph.Nodes.Count);
        int totalEdges = caches.Values.Sum(c => c.Graph.Edges.Count);
        Console.WriteLine($"migrated totals: nodes={totalNodes} edges={totalEdges} action/event={actionEvents} enemyStartEntries={enemyStartEntries}");
        Check(actionEvents > 0, "no action/event nodes restored (Phase-2 nodes missing)");
        Check(enemyStartEntries > 0, "no enemy start entries recovered");

        // Idempotency: a migrated project must not re-migrate on the next load.
        var again = ProjectMigrator.MigrateLegacyCaches(caches, parser);
        Check(again.Units.Count == 0, $"second pass re-migrated {again.Units.Count} unit(s)");

        // ---- anchored overlap pass (the load-time saved-vs-computed fix) ----
        // Constants below mirror MainWindow: NODE_WIDTH=140, row = NODE_HEIGHT+V_SPACING=52.
        var layoutPos = new Dictionary<int, Point>
        {
            [1] = new Point(30, 30),    // anchor
            [2] = new Point(30, 40),    // computed, overlaps the anchor
            [3] = new Point(600, 30),   // computed, already clear
        };
        MainWindow.PushComputedNodesOffAnchors(layoutPos, new HashSet<int> { 1 });
        Check(layoutPos[1] == new Point(30, 30), "layout: anchor moved");
        Check(layoutPos[3] == new Point(600, 30), "layout: clear computed node moved");
        Check(Math.Abs(layoutPos[1].X - layoutPos[2].X) >= 140
            || Math.Abs(layoutPos[1].Y - layoutPos[2].Y) >= 52,
            $"layout: computed node still overlaps anchor (y={layoutPos[2].Y})");

        var stackPos = new Dictionary<int, Point>
        {
            [1] = new Point(0, 0),      // anchor
            [2] = new Point(0, 52),     // anchor
            [3] = new Point(0, 10),     // computed, squeezed between the anchors
            [4] = new Point(0, 200),    // computed, clear
        };
        MainWindow.PushComputedNodesOffAnchors(stackPos, new HashSet<int> { 1, 2 });
        Check(stackPos[1] == new Point(0, 0) && stackPos[2] == new Point(0, 52),
            "layout: stacked anchors moved");
        Check(stackPos[3].Y >= 104, $"layout: squeezed node not pushed clear (y={stackPos[3].Y})");
        Check(stackPos[4] == new Point(0, 200), "layout: distant node moved");

        Console.WriteLine(fail == 0 ? "ALL PASS" : $"FAILURES: {fail}");
        return fail == 0 ? 0 : 1;
    }
}
