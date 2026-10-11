using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SifuMovesetEditor;
using SifuMovesetEditor.Export;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace export_smoke;

/// <summary>
/// Round-trip smoke tests for the combo authoring export pipeline: node removal
/// (RemoveNodesFromComboAsset) and authored-node append (AppendEnemyAuthoredContent)
/// against real vanilla trees, each written out and gated by ComboWriteValidator.
/// Every mutation is checked against an independent reference implementation of the
/// index reindex rules (drop removed, shift survivors, chain-follow removed redirects).
/// Exit code 0 = all passed.
/// </summary>
internal static class Program
{
    private static readonly string ContentRoot =
        Environment.GetEnvironmentVariable("SIFU_CONTENT")
        ?? @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content";

    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "export_smoke");
    private static readonly List<string> Failures = new();
    private static List<AiNodeInfo>? _catalog;

    private static int Main()
    {
        ErrorLog.Init();
        Directory.CreateDirectory(OutDir);

        string seanP1 = Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Sean", "Sean_Combo_Phase1.uasset");
        string fajarP2 = Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Fajar", "Attacks", "Fajar_Combo_P2.uasset");
        string mainCharCombo = Path.Combine(ContentRoot, "DB", "_MainChar", "Combos", "MainChar_ComboTree.uasset");
        if (!File.Exists(seanP1) || !File.Exists(fajarP2) || !File.Exists(mainCharCombo))
        {
            Console.Error.WriteLine($"test assets missing under {ContentRoot} (set SIFU_CONTENT to the game Content dir)");
            return 2;
        }

        Run("RemoveMiddle", () => TestRemove(fajarP2, "remove-middle", (n, _) => new List<int> { n / 2 }));
        Run("RemoveFirst", () => TestRemove(fajarP2, "remove-first", (_, _) => new List<int> { 1 }));
        Run("RemoveLast", () => TestRemove(fajarP2, "remove-last", (n, _) => new List<int> { n - 1 }));
        Run("RemoveRedirectTarget", () => TestRemoveRedirectTarget(fajarP2));
        Run("RemoveRedirectChain", () => TestRemoveRedirectChain(fajarP2));
        Run("RemoveEntryTarget", () => TestRemoveEntryTarget(fajarP2));
        Run("AppendActionCrossUnit", () => TestAppendAction(seanP1, "append-action-cross", crossUnit: true));
        Run("AppendEventSameFile", () => TestAppendEvent(fajarP2, sameFile: true));
        Run("AppendEventCrossUnit", () => TestAppendEvent(fajarP2, sameFile: false));
        Run("AppendAttack", () => TestAppendAttack(fajarP2));
        Run("AppendMainChar", () => TestAppendMainChar(mainCharCombo));
        Run("AddAndDelete", () => TestAddAndDelete(seanP1));
        Run("ConditionEdits", () => TestConditionEdits(seanP1));
        Run("AddCondition", () => TestAddCondition(seanP1, fajarP2));
        Run("AddEdgeCondition", () => TestAddEdgeCondition(seanP1, fajarP2));
        Run("ClearMixupAppend", () => TestClearMixupAppend(seanP1));
        Run("ReplaceAttackWithAction", () => TestReplaceAttackWithAction(seanP1, "replace-atk-action"));
        Run("ReplaceActionWithAttack", () => TestReplaceActionWithAttack(fajarP2, "replace-action-atk"));
        Run("ReplaceEventWithAction", () => TestReplaceEventWithAction(fajarP2, "replace-event-action"));
        Run("MergeIntoRow", () => TestMergeIntoRow(seanP1));
        Run("StartMultiKeyAddRow", () => TestStartMultiKeyAddRow(fajarP2, seanP1));
        Run("RehydrateKeyModels", TestRehydrateKeyModels);
        Run("ResolveRedirectFinal", TestResolveRedirectFinal);

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine("ALL EXPORT SMOKE TESTS PASSED");
            return 0;
        }
        Console.WriteLine($"{Failures.Count} FAILED:");
        foreach (var f in Failures) Console.WriteLine("  " + f);
        return 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"  PASS {name}");
        }
        catch (Exception ex)
        {
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
            Console.WriteLine(LogTail());
        }
    }

    private static void Expect(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    private static string LogTail()
    {
        try
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), "error.log");
            if (!File.Exists(path)) return "";
            var lines = File.ReadAllLines(path);
            return "---- error.log tail ----\n" + string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 30)));
        }
        catch { return ""; }
    }

    // ---------------------------------------------------------------- test cases

    private static void TestRemove(string file, string label, Func<int, List<(int owner, int value)>, List<int>> pick)
    {
        var asset = Load(file);
        var (_, nodes) = ComboOf(asset);
        int n = nodes.Value.Length;
        var removed = pick(n, SnapshotRedirects(asset))
            .Where(i => i > 0 && i < n)
            .Except(EntryTargets(asset))
            .Distinct()
            .ToHashSet();
        if (removed.Count == 0)
        {
            var entry = EntryTargets(asset).ToHashSet();
            for (int i = n - 1; i >= 1; i--)
            {
                if (!entry.Contains(i)) { removed.Add(i); break; }
            }
        }
        Expect(removed.Count > 0, $"{label}: no removable index picked");
        RemoveAndAssert(asset, label, removed);
    }

    private static void TestRemoveRedirectTarget(string file)
    {
        var asset = Load(file);
        var (_, nodes) = ComboOf(asset);
        int n = nodes.Value.Length;
        var entry = EntryTargets(asset).ToHashSet();
        int target = SnapshotRedirects(asset)
            .Where(r => r.value >= 2 && r.value < n && !entry.Contains(r.value))
            .Select(r => r.value)
            .FirstOrDefault();
        Expect(target != 0, $"{file}: no redirect target available to remove");
        RemoveAndAssert(asset, "remove-redirect-target", new HashSet<int> { target });
    }

    private static void TestRemoveRedirectChain(string file)
    {
        var asset = Load(file);
        var (_, nodes) = ComboOf(asset);
        int n = nodes.Value.Length;
        var redirs = SnapshotRedirects(asset);
        var byOwner = redirs.ToDictionary(r => r.owner, r => r.value);
        var entry = EntryTargets(asset).ToHashSet();

        // B is itself a redirect and some other node A points at B: removing B (and
        // ideally A too) forces A's value through B's own raw redirect (chain-follow).
        var pair = redirs.FirstOrDefault(r =>
            r.value >= 1 && r.value < n && !entry.Contains(r.value) && !entry.Contains(r.owner)
            && r.owner != r.value && byOwner.ContainsKey(r.value));
        var removed = new HashSet<int>();
        if (pair != default && pair.value != 0)
        {
            removed.Add(pair.value);
            removed.Add(pair.owner);
        }
        else
        {
            var single = redirs.FirstOrDefault(r =>
                r.value >= 1 && r.value < n && !entry.Contains(r.value) && byOwner.ContainsKey(r.value) && r.owner != r.value);
            Expect(single != default && single.value != 0, $"{file}: no redirect chain pair found");
            removed.Add(single.value);
        }
        removed.RemoveWhere(i => i <= 0 || i >= n);
        Expect(removed.Count > 0, $"{file}: chain pick empty");
        RemoveAndAssert(asset, "remove-redirect-chain", removed);
    }

    private static void TestRemoveEntryTarget(string file)
    {
        var asset = Load(file);
        var (_, nodes) = ComboOf(asset);
        int n = nodes.Value.Length;
        int target = EntryTargets(asset).FirstOrDefault(i => i > 0 && i < n);
        Expect(target != 0, $"{file}: no non-root entry target to remove (API-level defensive test)");
        RemoveAndAssert(asset, "remove-entry-target", new HashSet<int> { target });
    }

    // Multi-key added-row: appending a probability row to an entry whose vanilla first
    // row is a condition-outcome map ({0:a, 1:b, 2:c}) must collapse the export clone
    // to a single unconditional key-255 target instead of failing the
    // ApplyStartTargetValue Count==1 gate (the proven new-entry recipe).
    private static void TestStartMultiKeyAddRow(string fajarP2, string seanP1)
    {
        static ArrayPropertyData? StartContainer(NormalExport c)
            => c.Data?.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboStartNodesContainer");
        static ArrayPropertyData? EntryRows(StructPropertyData? en)
            => en?.Value?.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboTransitions")?.Value
                ?.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions");

        UAsset? asset = null;
        NormalExport? combo = null;
        ArrayPropertyData? nodes = null;
        ArrayPropertyData? cont = null;
        int entryIdx = -1, rowCount = 0, keyCount = 0;
        float prob0 = 100f;
        string label = "";

        foreach (var file in new[] { fajarP2, seanP1 })
        {
            var a = Load(file);
            var (c, n) = ComboOf(a);
            var ct = StartContainer(c);
            if (ct?.Value == null) continue;
            for (int i = 0; i < ct.Value.Length; i++)
            {
                if (ct.Value[i] is not StructPropertyData en) continue;
                var arr = EntryRows(en);
                var row0 = arr?.Value?.OfType<StructPropertyData>().FirstOrDefault();
                var map = row0?.Value?.OfType<MapPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_TargetNodes");
                if (map?.Value == null || map.Value.Count <= 1) continue;
                (asset, combo, nodes, cont) = (a, c, n, ct);
                entryIdx = i;
                rowCount = arr!.Value.Length;
                keyCount = map.Value.Count;
                label = en.Value?.OfType<StructPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboTransitions")?.Value
                    ?.OfType<NamePropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ParentNodeName")?
                    .Value?.Value?.ToString() ?? "";
                prob0 = row0!.Value.OfType<FloatPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_fProbability")?.Value ?? 100f;
                break;
            }
            if (asset != null) break;
        }
        Expect(asset != null && combo != null && nodes != null && cont != null,
            "no multi-key start entry found in Fajar P2 or Sean P1");

        // Model: the vanilla multi-key row passes through untouched (empty per-key model
        // skips the export per-key patch), plus one added row targeting base node 1.
        var entry = new StartEntry { VanillaIndex = entryIdx, Label = label };
        entry.Transitions.Add(new StartTransition
        {
            OriginalIndex = 0,
            Probability = prob0,
            OriginalProbability = prob0,
            TargetNodeId = -1,
            OriginalTargetNodeId = -1,
            KeyCount = keyCount
        });
        entry.Transitions.Add(new StartTransition
        {
            OriginalIndex = -1,
            Probability = 100f - prob0,
            KeyByte = 255,
            KeyCount = 1,
            TargetNodeId = 1,
            TargetTreeIndex = 0,
            TargetName = "Base0"
        });
        var graph = new ComboGraph
        {
            Nodes = new List<ComboNode> { BaseNode(1, 0) },
            StartEntries = new List<StartEntry> { entry }
        };

        var r = ExportDialog.AppendEnemyAuthoredContent(graph, asset!, combo!, nodes!, ContentRoot);
        Expect(r.startEntries > 0, "start-entry patch reported nothing");

        var outRows = EntryRows(cont!.Value[entryIdx] as StructPropertyData);
        Expect(outRows?.Value?.Length == rowCount + 1,
            $"rows {outRows?.Value?.Length} != {rowCount + 1}");

        var last = outRows!.Value[^1] as StructPropertyData;
        Expect(last?.Value != null, "added row missing");
        Expect(!last!.Value.Any(p => p.Name?.Value?.ToString() == "m_ConditionInstance"),
            "added row kept a condition instance");
        var lastProb = last.Value.OfType<FloatPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_fProbability");
        Expect(lastProb != null && Math.Abs(lastProb.Value - (100f - prob0)) < 0.001f,
            $"added row probability {lastProb?.Value} != {100f - prob0:0.###}");
        var lastMap = last.Value.OfType<MapPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_TargetNodes");
        Expect(lastMap?.Value?.Count == 1, $"added row map has {lastMap?.Value?.Count} entries, expected 1");
        byte lastKey = 0;
        int lastVal = -1;
        foreach (var kvp in lastMap!.Value)
        {
            lastKey = kvp.Key switch { BytePropertyData b => b.Value, IntPropertyData i => (byte)i.Value, _ => (byte)0 };
            lastVal = kvp.Value switch { IntPropertyData i => i.Value, BytePropertyData b => b.Value, _ => -1 };
        }
        Expect(lastKey == 255, $"added row key {lastKey} != 255");
        Expect(lastVal == 0, $"added row target {lastVal} != tree 0");

        // Vanilla first row untouched: multi-key map still multi-key. (outRows.Value[0]
        // is a row struct, not an entry - read its map directly.)
        var row0Out = outRows.Value[0] as StructPropertyData;
        var row0Map = row0Out?.Value?.OfType<MapPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_TargetNodes");
        Expect(row0Map?.Value?.Count == keyCount, "vanilla multi-key row map was modified");

        string outPath = WriteAndValidate(asset!, "start-multikey-addrow");
        var a2 = Load(outPath);
        var (c2, _) = ComboOf(a2);
        var rows2 = EntryRows(StartContainer(c2)?.Value?[entryIdx] as StructPropertyData);
        Expect(rows2?.Value?.Length == rowCount + 1, $"reparse rows {rows2?.Value?.Length} != {rowCount + 1}");
        var last2 = rows2!.Value[^1] as StructPropertyData;
        var lastMap2 = last2?.Value?.OfType<MapPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_TargetNodes");
        Expect(lastMap2?.Value?.Count == 1, "reparse added row map is not single-entry");
        byte reKey = 0;
        foreach (var kvp in lastMap2!.Value)
            reKey = kvp.Key switch { BytePropertyData b => b.Value, IntPropertyData i => (byte)i.Value, _ => (byte)0 };
        Expect(reKey == 255, $"reparse added row key {reKey} != 255");
    }

    // Old saves (pre per-key editing) carry KeyCount + TargetSummary but empty
    // Keys/KeyTarget* lists - RehydrateStartEntryKeyModels rebuilds them from the
    // summary and the graph's TreeIndex-stable nodes.
    private static void TestRehydrateKeyModels()
    {
        var graph = new ComboGraph
        {
            Nodes = new List<ComboNode>
            {
                new() { Id = 1, TreeIndex = 7, Name = "Sweep" },
                new() { Id = 2, TreeIndex = 9, Name = "WhiffFollow" }
            }
        };
        var entry = new StartEntry { VanillaIndex = 1, Label = "Parry" };

        // Old-save multi-key row: empty lists, summary from the vanilla map.
        var legacy = new StartTransition
        {
            OriginalIndex = 0,
            KeyCount = 2,
            KeyByte = 0,
            TargetSummary = "0→7, 1→9",
            Probability = 60,
            OriginalProbability = 60,
            TargetTreeIndex = 7,
            OriginalTargetTreeIndex = 7
        };
        entry.Transitions.Add(legacy);

        // Condensed target (tree index with no graph node) keeps the index, id -1.
        var condensed = new StartTransition
        {
            OriginalIndex = 1,
            KeyCount = 2,
            KeyByte = 0,
            TargetSummary = "0→7, 1→99",
            Probability = 40,
            OriginalProbability = 40
        };
        entry.Transitions.Add(condensed);

        // Fresh-model row: must be left untouched.
        var fresh = new StartTransition
        {
            OriginalIndex = 2,
            KeyCount = 2,
            KeyByte = 0,
            TargetSummary = "stale",
            Keys = new List<byte> { 0, 1 },
            KeyTargetTreeIndices = new List<int> { 7, 9 },
            KeyTargetNodeIds = new List<int> { 1, 2 },
            KeyTargetNames = new List<string> { "Sweep", "WhiffFollow" },
            OriginalKeyTargetTreeIndices = new List<int> { 7, 9 },
            Probability = 0
        };
        entry.Transitions.Add(fresh);

        // Unparsable summary: stays view-only.
        var junk = new StartTransition
        {
            OriginalIndex = 3,
            KeyCount = 2,
            KeyByte = 0,
            TargetSummary = "not a summary",
            Probability = 0
        };
        entry.Transitions.Add(junk);

        graph.StartEntries = new List<StartEntry> { entry };
        graph.RehydrateStartEntryKeyModels();

        Expect(legacy.Keys.SequenceEqual(new byte[] { 0, 1 }), "legacy keys not rehydrated");
        Expect(legacy.KeyTargetTreeIndices.SequenceEqual(new[] { 7, 9 }), "legacy tree indices not rehydrated");
        Expect(legacy.KeyTargetNodeIds.SequenceEqual(new[] { 1, 2 }), "legacy node ids not resolved");
        Expect(legacy.KeyTargetNames.SequenceEqual(new[] { "Sweep", "WhiffFollow" }), "legacy names not resolved");
        Expect(legacy.OriginalKeyTargetTreeIndices.SequenceEqual(new[] { 7, 9 }),
            "Original* not mirrored - rehydrated rows would read as modified");
        Expect(!legacy.IsModified(), "rehydrated legacy row reads as modified");

        Expect(condensed.Keys.SequenceEqual(new byte[] { 0, 1 }), "condensed keys not rehydrated");
        Expect(condensed.KeyTargetNodeIds.SequenceEqual(new[] { 1, -1 }), "condensed id resolution wrong");
        Expect(condensed.KeyTargetNames.SequenceEqual(new[] { "Sweep", "" }), "condensed name resolution wrong");

        Expect(fresh.Keys.Count == 2 && fresh.TargetSummary == "stale", "fresh-model row was touched");
        Expect(junk.Keys.Count == 0, "unparsable summary produced keys");

        // Idempotent: a second pass changes nothing.
        graph.RehydrateStartEntryKeyModels();
        Expect(legacy.Keys.Count == 2 && legacy.KeyTargetNodeIds.SequenceEqual(new[] { 1, 2 }),
            "second rehydrate pass changed a hydrated row");
    }

    // ResolveRedirectFinal walks a redirect's ResolvedRedirectNodeId chain to the
    // final non-redirect node - the Start Entries "(via redirect)" display and the
    // dead-redirect jump guard both depend on it.
    private static void TestResolveRedirectFinal()
    {
        var graph = new ComboGraph
        {
            Nodes = new List<ComboNode>
            {
                new() { Id = 1, TreeIndex = 1, Name = "Move" },
                new() { Id = 2, TreeIndex = 2, Name = "RedirA", IsRedirect = true, ResolvedRedirectNodeId = 3 },
                new() { Id = 3, TreeIndex = 3, Name = "RedirB", IsRedirect = true, ResolvedRedirectNodeId = 4 },
                new() { Id = 4, TreeIndex = 4, Name = "Final" },
                new() { Id = 5, TreeIndex = 5, Name = "SelfLoop", IsRedirect = true, ResolvedRedirectNodeId = 6 },
                new() { Id = 6, TreeIndex = 6, Name = "LoopBack", IsRedirect = true, ResolvedRedirectNodeId = 5 },
                new() { Id = 7, TreeIndex = 7, Name = "DeadRedir", IsRedirect = true, ResolvedRedirectNodeId = -1 },
                new() { Id = 8, TreeIndex = 8, Name = "PhantomRedir", IsRedirect = true, ResolvedRedirectNodeId = 99 }
            }
        };

        // Two-hop redirect chain resolves to the final move.
        Expect(graph.ResolveRedirectFinal(2) == 4, "chain A->B->move did not resolve to the move");
        // Mid-chain node resolves too (single hop).
        Expect(graph.ResolveRedirectFinal(3) == 4, "mid-chain redirect did not resolve");
        // Plain node passes through unchanged.
        Expect(graph.ResolveRedirectFinal(1) == 1, "plain node passthrough changed the id");
        // Final move itself passes through.
        Expect(graph.ResolveRedirectFinal(4) == 4, "final move passthrough changed the id");
        // Cycle: self-loop redirect and two-node loop both fail safe.
        Expect(graph.ResolveRedirectFinal(5) == -1, "self-loop redirect did not fail safe");
        Expect(graph.ResolveRedirectFinal(6) == -1, "two-node redirect loop did not fail safe");
        // Unresolved redirect (no target) fails safe.
        Expect(graph.ResolveRedirectFinal(7) == -1, "unresolved redirect did not fail safe");
        // Redirect pointing at a node missing from the graph fails safe.
        Expect(graph.ResolveRedirectFinal(8) == -1, "redirect to missing node did not fail safe");
        // Negative / absent ids fail safe.
        Expect(graph.ResolveRedirectFinal(-1) == -1, "negative id did not fail safe");
        Expect(graph.ResolveRedirectFinal(99) == -1, "absent id did not fail safe");

        // User RedirectMods are live state: retarget the chain head at node 1.
        graph.Nodes.First(n => n.Id == 2).ResolvedRedirectNodeId = 1;
        Expect(graph.ResolveRedirectFinal(2) == 1, "retargeted redirect did not follow the new destination");
    }

    private static void TestAppendAction(string targetFile, string label, bool crossUnit)
    {
        var ai = PickAction(crossUnit);
        var asset = Load(targetFile);
        var (combo, nodes) = ComboOf(asset);
        int baseCount = nodes.Value.Length;

        var graph = new ComboGraph { Nodes = new List<ComboNode> { BaseNode(1, 0), AiNode(100, ai, isEvent: false, "SmokeAction") } };
        graph.Edges.Add(NewEdge(1, 100, "LMB"));
        graph.Edges.Add(NewEdge(100, 1, "RMB"));

        var r = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodes, ContentRoot);
        int treeExports = asset.Exports.Count(e => e is NormalExport ne2 && ne2.Data != null
            && ne2.Data.OfType<ArrayPropertyData>().Any(p => p.Name?.Value?.ToString() == "m_Nodes"));
        Expect(treeExports == 1, $"{label}: expected exactly 1 combo tree export, got {treeExports}");
        Expect(r.nodes == 1, $"appended {r.nodes} nodes, expected 1");
        Expect(r.links == 2, $"appended {r.links} links, expected 2");
        Expect(r.skipped == 0, $"{r.skipped} nodes skipped");
        Expect(nodes.Value.Length == baseCount + 1, "m_Nodes not extended");

        string outPath = WriteAndValidate(asset, label);
        var a2 = Load(outPath);
        var (_, n2) = ComboOf(a2);
        Expect(n2.Value.Length == baseCount + 1, $"reparse node count {n2.Value.Length} != {baseCount + 1}");

        var last = n2.Value[^1] as StructPropertyData;
        Expect(last?.Value != null, "appended struct missing");
        string name = NameOf(last!) ?? "";
        Expect(name.Contains("SmokeAction"), $"appended node name '{name}'");
        var slots = last!.Value.OfType<StructPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos")?.Value
            ?.OfType<NamePropertyData>()
            .Where(p => p.Name?.Value?.ToString() == "m_Attacks")
            .ToList() ?? new List<NamePropertyData>();
        Expect(slots.Count > 0 && slots.All(s => s.Value?.Value?.ToString() == "None"),
            "action node attack slots are not all None");
    }

    private static void TestAppendEvent(string targetFile, bool sameFile)
    {
        string label = sameFile ? "append-event-same" : "append-event-cross";
        ComboNode node;
        UAsset asset;
        NormalExport combo;
        ArrayPropertyData nodesArr;
        int baseCount;
        int exportsBefore;

        if (sameFile)
        {
            asset = Load(targetFile);
            (combo, nodesArr) = ComboOf(asset);
            baseCount = nodesArr.Value.Length;
            int idx = FindEventNodeIndex(asset);
            Expect(idx >= 0, $"{targetFile}: no event node with m_Event export found");
            node = AiNode(100, new AiNodeInfo { Kind = "Event", DisplayName = "SmokeEvent", ClassName = "SmokeEvent" },
                isEvent: true, "SmokeEvent");
            node.ImportComboRel = RelNoExt(targetFile);
            node.ImportSourceTreeIndex = idx;
            exportsBefore = asset.Exports.Count;
        }
        else
        {
            var ai = PickEvent();
            asset = Load(targetFile);
            (combo, nodesArr) = ComboOf(asset);
            baseCount = nodesArr.Value.Length;
            node = AiNode(100, ai, isEvent: true, "SmokeEvent");
            exportsBefore = asset.Exports.Count;
        }

        var graph = new ComboGraph { Nodes = new List<ComboNode> { BaseNode(1, 0), node } };
        var r = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodesArr, ContentRoot);
        Expect(r.nodes == 1, $"appended {r.nodes} nodes, expected 1");
        Expect(r.skipped == 0, $"{r.skipped} nodes skipped");
        Expect(nodesArr.Value.Length == baseCount + 1, "m_Nodes not extended");

        string outPath = WriteAndValidate(asset, label);
        var a2 = Load(outPath);
        var (_, n2) = ComboOf(a2);
        Expect(n2.Value.Length == baseCount + 1, $"reparse node count {n2.Value.Length} != {baseCount + 1}");

        var last = n2.Value[^1] as StructPropertyData;
        Expect(last?.Value != null, "appended struct missing");
        string name = NameOf(last!) ?? "";
        Expect(name.Contains("SmokeEvent"), $"appended node name '{name}'");
        var ev = last!.Value.OfType<ObjectPropertyData>().FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Event");
        Expect(ev?.Value != null && ev.Value.Index != 0, "appended event node has null m_Event");
        if (!sameFile)
            Expect(a2.Exports.Count > exportsBefore, "cross-file event exports were not cloned");
    }

    private static void TestAppendAttack(string targetFile)
    {
        var asset = Load(targetFile);
        var (combo, nodes) = ComboOf(asset);
        int baseCount = nodes.Value.Length;
        string key = FirstAttackKey(combo);
        Expect(!string.IsNullOrEmpty(key), "no m_Attacks registry key in target tree");
        int dot = key.LastIndexOf('.');
        Expect(dot > 0, $"unexpected registry key '{key}'");
        string db = key[..dot];

        var node = new ComboNode
        {
            Id = 100,
            TreeIndex = -1,
            Kind = ComboNodeKind.UserCreated,
            DisplayName = "SmokeAttack",
            SourceDBPath = db,
            AnimPath = db
        };
        var graph = new ComboGraph { Nodes = new List<ComboNode> { BaseNode(1, 0), node } };

        var r = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodes, ContentRoot);
        Expect(r.nodes == 1, $"appended {r.nodes} nodes, expected 1");
        Expect(r.skipped == 0, $"{r.skipped} nodes skipped");
        Expect(nodes.Value.Length == baseCount + 1, "m_Nodes not extended");

        string outPath = WriteAndValidate(asset, "append-attack");
        var a2 = Load(outPath);
        var (_, n2) = ComboOf(a2);
        Expect(n2.Value.Length == baseCount + 1, $"reparse node count {n2.Value.Length} != {baseCount + 1}");

        var last = n2.Value[^1] as StructPropertyData;
        Expect(last?.Value != null, "appended struct missing");
        string name = NameOf(last!) ?? "";
        Expect(name.Contains("SmokeAttack"), $"appended node name '{name}'");
        var slots = last!.Value.OfType<StructPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos")?.Value
            ?.OfType<NamePropertyData>()
            .Where(p => p.Name?.Value?.ToString() == "m_Attacks")
            .ToList() ?? new List<NamePropertyData>();
        Expect(slots.Count > 0 && slots.All(s => s.Value?.Value?.ToString() == key),
            $"attack slots not all '{key}'");
    }

    /// <summary>
    /// MainChar append round trip: a new attack node whose DB is a clone staged only
    /// in this export's output (registered through stagedExists, like the bare-anim
    /// drop's MainChar_Custom card), a new redirect node, links from and to existing
    /// nodes, and a branch-chance override - written out, gated by ComboWriteValidator,
    /// and re-parsed.
    /// </summary>
    private static void TestAppendMainChar(string targetFile)
    {
        var asset = Load(targetFile);
        var (combo, nodes) = ComboOf(asset);
        int baseCount = nodes.Value.Length;
        Expect(baseCount > 3, $"{targetFile}: tree too small ({baseCount} nodes)");

        // tree[1] is the link source and the redirect target: it needs transition rows.
        var baseStruct = NodeStructAt(nodes, 1);
        var baseTrans = TransitionsOf(baseStruct);
        Expect(baseTrans?.Value != null && baseTrans.Value.Length > 0, "tree[1] has no transition rows");
        int rowsBaseBefore = baseTrans!.Value!.Length;

        // Pick a row on tree[1] carrying m_fProbability for the chance override.
        int probRow = -1;
        float curProb = 0f;
        for (int i = 0; i < baseTrans.Value.Length; i++)
        {
            if (baseTrans.Value[i] is not StructPropertyData rs || rs.Value == null) continue;
            var fp = rs.Value.OfType<FloatPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_fProbability");
            if (fp != null) { probRow = i; curProb = fp.Value; break; }
        }
        Expect(probRow >= 0, "tree[1] has no row with m_fProbability");
        float newProb = Math.Abs(curProb - 34.5f) < 0.001f ? 45.5f : 34.5f;

        // Stage the clone card exactly like the export's bare-anim drop: it exists only
        // in the staging output, so registration has to come through stagedExists.
        string cloneRel = "DB/_MainChar/Combos/Attacks/Custom/MainChar_Custom_Smoke";
        string cloneKey = "/Game/" + cloneRel + ".MainChar_Custom_Smoke";
        string stagedPath = Path.Combine(OutDir, "Sifu", "Content",
            cloneRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
        string template = Path.Combine(ContentRoot, "DB", "_MainChar", "Combos", "Attacks",
            "BareHands", "LightCombo", "MainChar_Jab_FR.uasset");
        if (File.Exists(template)) File.Copy(template, stagedPath, true);
        else File.WriteAllText(stagedPath, "staged");

        var attack = new ComboNode
        {
            Id = 100,
            TreeIndex = -1,
            Kind = ComboNodeKind.UserCreated,
            DisplayName = "SmokeAttack",
            SourceDBPath = "/Game/" + cloneRel
        };
        var redirect = new ComboNode
        {
            Id = 101,
            TreeIndex = -1,
            Kind = ComboNodeKind.UserCreated,
            IsRedirect = true,
            DisplayName = "SmokeRedirect",
            RedirectTargetTreeIndex = -1,
            ResolvedRedirectNodeId = 1
        };
        var graph = new ComboGraph { Nodes = new List<ComboNode> { BaseNode(1, 1), attack, redirect } };
        graph.Edges.Add(NewEdge(1, 100, "LMB"));
        graph.Edges.Add(NewEdge(100, 101, "RMB"));
        graph.TreeRowProbabilityOverrides[1] = new Dictionary<int, float> { [probRow] = newProb };

        var r = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodes, ContentRoot,
            customReplace: false,
            stagedExists: rel => File.Exists(Path.Combine(OutDir, "Sifu", "Content",
                rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset")));
        Expect(r.nodes == 2, $"appended {r.nodes} nodes, expected 2");
        Expect(r.links == 2, $"appended {r.links} links, expected 2");
        Expect(r.skipped == 0, $"{r.skipped} nodes skipped");
        Expect(r.chances >= 1, $"chance override not applied (chances={r.chances})");
        Expect(nodes.Value.Length == baseCount + 2, "m_Nodes not extended by 2");

        string outPath = WriteAndValidate(asset, "append-mainchar");
        var a2 = Load(outPath);
        var (_, n2) = ComboOf(a2);
        Expect(n2.Value.Length == baseCount + 2, $"reparse node count {n2.Value.Length} != {baseCount + 2}");

        // New attack node: every slot points at the registered clone card.
        var atk = NodeStructAt(n2, baseCount);
        Expect((NameOf(atk) ?? "").Contains("SmokeAttack"), $"appended node name '{NameOf(atk)}'");
        var slots = atk.Value.OfType<StructPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos")?.Value
            ?.OfType<NamePropertyData>()
            .Where(p => p.Name?.Value?.ToString() == "m_Attacks")
            .ToList() ?? new List<NamePropertyData>();
        Expect(slots.Count > 0 && slots.All(s => s.Value?.Value?.ToString() == cloneKey),
            $"attack slots not all '{cloneKey}'");
        Expect(TransitionRowCount(atk) == 1, $"attack node has {TransitionRowCount(atk)} rows, expected 1");

        // New redirect node: points at tree[1], no rows of its own.
        var redir = NodeStructAt(n2, baseCount + 1);
        Expect((NameOf(redir) ?? "").Contains("SmokeRedirect"), $"redirect name '{NameOf(redir)}'");
        Expect(RedirectOf(redir) == 1, $"redirect target {RedirectOf(redir)} != 1");
        Expect(TransitionRowCount(redir) == 0, $"redirect has {TransitionRowCount(redir)} rows, expected 0");

        // Links reparsed: tree[1] gained one row (LMB/Light -> attack), attack -> redirect
        // (RMB/Heavy), and the override on tree[1] landed at its typed value.
        var baseTrans2 = TransitionsOf(NodeStructAt(n2, 1))!;
        Expect(baseTrans2.Value!.Length == rowsBaseBefore + 1,
            $"tree[1] rows {baseTrans2.Value.Length} != {rowsBaseBefore + 1}");
        var lastRow = baseTrans2.Value[^1] as StructPropertyData;
        Expect(lastRow?.Value != null, "appended base row missing");
        AssertRow(lastRow!, "EComboTransition::Light", baseCount, "base link");

        var atkRow = TransitionsOf(atk)!.Value![0] as StructPropertyData;
        Expect(atkRow?.Value != null, "attack row missing");
        AssertRow(atkRow!, "EComboTransition::Heavy", baseCount + 1, "attack link");

        var prob2 = NodeStructAt(n2, 1).Value.OfType<StructPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions")?.Value
            ?.OfType<ArrayPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions")?.Value;
        var probRowSp = prob2 is { Length: > 0 } && probRow < prob2.Length
            ? prob2[probRow] as StructPropertyData : null;
        var probFp = probRowSp?.Value?.OfType<FloatPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_fProbability");
        Expect(probFp != null && Math.Abs(probFp.Value - newProb) < 0.001f,
            $"chance override {newProb} not written (got {probFp?.Value.ToString() ?? "null"})");

        static void AssertRow(StructPropertyData row, string expectInput, int expectTarget, string label)
        {
            var input = row.Value!.OfType<EnumPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_eInputTransition");
            Expect(input?.Value?.Value?.ToString() == expectInput,
                $"{label}: input '{input?.Value?.Value?.ToString()}' != '{expectInput}'");
            var map = row.Value!.OfType<MapPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_TargetNodes");
            Expect(map != null, $"{label}: m_TargetNodes missing");
            var vals = MapValues(map!);
            Expect(vals.Count == 1 && vals[0] == expectTarget,
                $"{label}: target [{string.Join(", ", vals)}] != [{expectTarget}]");
        }
    }

    private static void TestAddAndDelete(string targetFile)
    {
        var ai = PickAction(crossUnit: true);
        var asset = Load(targetFile);
        var (combo, nodes) = ComboOf(asset);
        int baseCount = nodes.Value.Length;

        var graph = new ComboGraph { Nodes = new List<ComboNode> { BaseNode(1, 0), AiNode(100, ai, isEvent: false, "SmokeCombo") } };
        var r = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodes, ContentRoot);
        Expect(r.nodes == 1 && r.skipped == 0, $"append: {r.nodes} nodes, {r.skipped} skipped");
        Expect(nodes.Value.Length == baseCount + 1, "m_Nodes not extended by append");

        int mid = Math.Min(baseCount / 2, baseCount - 1);
        var entry = EntryTargets(asset).ToHashSet();
        if (entry.Contains(mid)) mid = Enumerable.Range(1, baseCount - 1).First(i => !entry.Contains(i));
        string outPath = RemoveAndAssert(asset, "add-and-delete", new HashSet<int> { mid });

        var a2 = Load(outPath);
        var (_, n2) = ComboOf(a2);
        Expect(n2.Value.Length == baseCount, $"final node count {n2.Value.Length} != {baseCount}");
        Expect(Enumerable.Range(0, n2.Value.Length)
                .Any(i => (n2.Value[i] as StructPropertyData) is StructPropertyData sp && (NameOf(sp) ?? "").Contains("SmokeCombo")),
            "appended node lost after removal");
    }

    // ------------------------------------------------------- removal core + asserts

    // ------------------------------------------------------- payload replacement

    private static ComboNode ReplaceNode(int treeIndex, AiNodeInfo? ai, string? attackCardRel, string kind, string label)
    {
        var n = new ComboNode
        {
            Id = 100,
            TreeIndex = treeIndex,
            Kind = ComboNodeKind.Vanilla,
            DisplayName = label,
            ReplacedByAiNode = true,
            ReplacedAiKind = kind,
            ReplacedAiClass = ai?.ClassName ?? label,
            ReplacedImportComboRel = ai?.SourceComboRel ?? "",
            ReplacedSourceTreeIndex = ai?.SourceTreeIndex ?? -1,
            ReplacedAttackCardRel = attackCardRel ?? ""
        };
        return n;
    }

    private static void TestReplaceAttackWithAction(string targetFile, string label)
    {
        var ai = PickAction(crossUnit: true);
        var asset = Load(targetFile);
        var (combo, nodes) = ComboOf(asset);
        int idx = FindAttackNodeIndex(asset);
        Expect(idx >= 0, $"{targetFile}: no attack node with non-None slots");

        var graph = new ComboGraph { Nodes = new List<ComboNode> { ReplaceNode(idx, ai, null, ai.Kind, "SmokeReplace") } };
        var r = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodes, ContentRoot);
        Expect(r.replaced == 1, $"replaced {r.replaced}, expected 1");
        Expect(r.skipped == 0, $"{r.skipped} nodes skipped");

        string outPath = WriteAndValidate(asset, label);
        var a2 = Load(outPath);
        var (_, n2) = ComboOf(a2);
        var sp = NodeStructAt(n2, idx);
        AssertActionPayload(sp, true, label);
        AssertEventPayload(sp, false, label);
        AssertSlotsValue(sp, "None", label);
    }

    private static void TestReplaceActionWithAttack(string targetFile, string label)
    {
        var asset = Load(targetFile);
        var (combo, nodes) = ComboOf(asset);
        int idx = FindActionNodeIndex(asset);
        Expect(idx >= 0, $"{targetFile}: no action node with a resolved class");
        string key = FirstAttackKey(combo);
        Expect(!string.IsNullOrEmpty(key), "no m_Attacks registry key in target tree");
        int dot = key.LastIndexOf('.');
        Expect(dot > 0, $"unexpected registry key '{key}'");
        string db = key[..dot];

        var graph = new ComboGraph { Nodes = new List<ComboNode> { ReplaceNode(idx, null, db, "Attack", "SmokeReplace") } };
        var r = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodes, ContentRoot);
        Expect(r.replaced == 1, $"replaced {r.replaced}, expected 1");
        Expect(r.skipped == 0, $"{r.skipped} nodes skipped");

        string outPath = WriteAndValidate(asset, label);
        var a2 = Load(outPath);
        var (_, n2) = ComboOf(a2);
        var sp = NodeStructAt(n2, idx);
        AssertActionPayload(sp, false, label);
        AssertEventPayload(sp, false, label);
        AssertSlotsValue(sp, key, label);
    }

    private static void TestReplaceEventWithAction(string targetFile, string label)
    {
        var ai = PickAction(crossUnit: true);
        var asset = Load(targetFile);
        var (combo, nodes) = ComboOf(asset);
        int idx = FindEventNodeIndex(asset);
        Expect(idx >= 0, $"{targetFile}: no event node found");

        var graph = new ComboGraph { Nodes = new List<ComboNode> { ReplaceNode(idx, ai, null, ai.Kind, "SmokeReplace") } };
        var r = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodes, ContentRoot);
        Expect(r.replaced == 1, $"replaced {r.replaced}, expected 1");
        Expect(r.skipped == 0, $"{r.skipped} nodes skipped");

        string outPath = WriteAndValidate(asset, label);
        var a2 = Load(outPath);
        var (_, n2) = ComboOf(a2);
        var sp = NodeStructAt(n2, idx);
        AssertActionPayload(sp, true, label);
        AssertEventPayload(sp, false, label);
        AssertSlotsValue(sp, "None", label);
    }

    private static void AssertActionPayload(StructPropertyData sp, bool expectAction, string label)
    {
        var act = sp.Value?.OfType<StructPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AIAttackAction");
        if (!expectAction)
        {
            bool resolved = act?.Value?.OfType<ObjectPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_DefaultClassObject")?.Value?.Index is int ix && ix != 0;
            Expect(!resolved, $"{label}: action payload survived the replace");
            return;
        }
        Expect(act?.Value != null, $"{label}: m_AIAttackAction missing after replace");
        var cls = act!.Value.OfType<ObjectPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_DefaultClassObject");
        Expect(cls?.Value != null && cls.Value.Index != 0, $"{label}: action class object not resolved");
    }

    private static void AssertEventPayload(StructPropertyData sp, bool expectEvent, string label)
    {
        var ev = sp.Value?.OfType<ObjectPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Event");
        bool resolved = ev?.Value != null && ev.Value.Index != 0;
        Expect(resolved == expectEvent, $"{label}: m_Event resolved={resolved}, expected {expectEvent}");
    }

    private static void AssertSlotsValue(StructPropertyData sp, string expect, string label)
    {
        var slots = sp.Value?.OfType<StructPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos")?.Value
            ?.OfType<NamePropertyData>()
            .Where(p => p.Name?.Value?.ToString() == "m_Attacks")
            .ToList() ?? new List<NamePropertyData>();
        Expect(slots.Count > 0, $"{label}: no attack slots on node");
        Expect(slots.All(s => string.Equals(s.Value?.Value?.ToString(), expect, StringComparison.OrdinalIgnoreCase)),
            $"{label}: slots are [{string.Join(", ", slots.Select(s => s.Value?.Value?.ToString()))}], expected all '{expect}'");
    }

    private static string RemoveAndAssert(UAsset asset, string label, HashSet<int> removed)
    {
        var (combo, nodes) = ComboOf(asset);
        int n = nodes.Value.Length;
        Expect(removed.All(i => i >= 0 && i < n), $"{label}: removed index out of range");

        var redirs = SnapshotRedirects(asset);
        var rows = SnapshotTargetRows(asset);
        var defaults = SnapshotDefaults(asset);
        var probArrays = SnapshotProbArrays(asset);
        var raw = redirs.Where(r => removed.Contains(r.owner)).ToDictionary(r => r.owner, r => r.value);

        var graph = new ComboGraph { Nodes = new List<ComboNode> { new() { Id = 0, TreeIndex = 0, IsRoot = true } } };
        int id = 1;
        foreach (var i in removed.OrderBy(x => x))
            graph.Nodes.Add(new ComboNode { Id = id++, TreeIndex = i, Kind = ComboNodeKind.Vanilla, Removed = true });

        int got = ExportDialog.RemoveNodesFromComboAsset(asset, combo, graph);
        Expect(got == removed.Count, $"{label}: remover returned {got}, expected {removed.Count}");

        string outPath = WriteAndValidate(asset, label);
        var a2 = Load(outPath);

        var postRedirs = SnapshotRedirects(a2);
        var expRedirs = redirs
            .Where(r => !removed.Contains(r.owner))
            .Select(r => (r.owner - CountBelow(removed, r.owner), ChainMap(r.value, removed, raw)))
            .ToList();
        Expect(RedirsEqual(postRedirs, expRedirs),
            $"{label}: redirect mismatch\n    post: {Fmt(postRedirs)}\n    exp:  {Fmt(expRedirs)}");

        var postRows = SnapshotTargetRows(a2);
        var expRows = new List<(int owner, bool cond, List<int> values)>();
        foreach (var r in rows)
        {
            if (r.owner >= 0 && removed.Contains(r.owner)) continue;
            var filtered = r.values.Where(v => !removed.Contains(v)).Select(v => v - CountBelow(removed, v)).ToList();
            if (!r.cond && r.owner >= 0 && r.values.Count > 0 && filtered.Count == 0) continue;
            expRows.Add((r.owner < 0 ? -1 : r.owner - CountBelow(removed, r.owner), r.cond, filtered));
        }
        Expect(RowsEqual(postRows, expRows),
            $"{label}: target rows mismatch ({postRows.Count} vs {expRows.Count})\n    post: {Fmt(postRows)}\n    exp:  {Fmt(expRows)}");

        var postDefaults = SnapshotDefaults(a2);
        var expDefaults = defaults
            .Where(d => d.owner < 0 || !removed.Contains(d.owner))
            .Select(d => (
                d.owner < 0 ? -1 : d.owner - CountBelow(removed, d.owner),
                d.value < 0 ? d.value : removed.Contains(d.value) ? -1 : d.value - CountBelow(removed, d.value)))
            .ToList();
        Expect(DefaultsEqual(postDefaults, expDefaults),
            $"{label}: m_DefaultAttacks mismatch\n    post: {Fmt(postDefaults)}\n    exp:  {Fmt(expDefaults)}");

        var postProb = SnapshotProbArrays(a2);
        var expProb = probArrays.Where(p => p.owner < 0 || !removed.Contains(p.owner)).ToList();
        Expect(postProb.Count == expProb.Count,
            $"{label}: m_Transitions array count {postProb.Count} != {expProb.Count}");
        for (int i = 0; i < postProb.Count; i++)
        {
            var pre = expProb[i];
            var post = postProb[i];
            if (post.sum is null) continue;
            bool dropped = post.rowCount < pre.rowCount;
            bool vanilla100 = pre.sum is float ps && Math.Abs(ps - 100f) <= 0.5f;
            if (dropped || vanilla100)
                Expect(post.sum is float qs && Math.Abs(qs - 100f) <= 0.5f,
                    $"{label}: transitions array {i} sum {post.sum:0.###} after {(dropped ? "row drop" : "vanilla-100")} (was {pre.sum:0.###})");
        }

        return outPath;
    }

    private static string WriteAndValidate(UAsset asset, string label)
    {
        string path = Path.Combine(OutDir, label + ".uasset");
        try { File.Delete(path); } catch { }
        try { File.Delete(Path.ChangeExtension(path, ".uexp")); } catch { }
        asset.Write(path);
        Expect(ComboWriteValidator.Validate(path, asset, label), $"validator rejected {label}");
        return path;
    }

    // ------------------------------------------------------------- snapshots/walk

    private static UAsset Load(string path) =>
        new(path, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);

    private static (NormalExport combo, ArrayPropertyData nodes) ComboOf(UAsset asset)
    {
        foreach (var ex in asset.Exports)
        {
            if (ex is not NormalExport ne || ne.Data == null) continue;
            var arr = ne.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Nodes");
            if (arr?.Value != null) return (ne, arr);
        }
        throw new Exception("no m_Nodes array in asset");
    }

    private static void WalkOwned(NormalExport ne, Action<int, List<PropertyData>> onProps)
    {
        foreach (var p in ne.Data)
        {
            if (p is ArrayPropertyData na
                && string.Equals(na.Name?.Value?.ToString(), "m_Nodes", StringComparison.Ordinal)
                && na.Value != null)
            {
                for (int i = 0; i < na.Value.Length; i++)
                {
                    if (na.Value[i] is StructPropertyData ns && ns.Value != null)
                        onProps(i, ns.Value);
                }
            }
            else
            {
                onProps(-1, new List<PropertyData> { p });
            }
        }
    }

    private static void ForEachOwnedProps(UAsset asset, Action<int, List<PropertyData>> onProps)
    {
        foreach (var ex in asset.Exports)
            if (ex is NormalExport ne && ne.Data != null)
                WalkOwned(ne, onProps);
    }

    private static int ReadIdx(PropertyData p) => p switch
    {
        IntPropertyData ip => ip.Value,
        BytePropertyData bp => bp.Value,
        _ => -1
    };

    private static List<int> MapValues(MapPropertyData mp) =>
        mp.Value == null
            ? new List<int>()
            : mp.Value.Select(kvp => ReadIdx(kvp.Value)).Where(v => v >= 0).ToList();

    private static List<(int owner, int value)> SnapshotRedirects(UAsset asset)
    {
        var list = new List<(int, int)>();
        ForEachOwnedProps(asset, (owner, props) =>
        {
            if (owner < 0) return;
            var rp = props.OfType<IntPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_NodeRedirect");
            list.Add((owner, rp?.Value ?? -1));
        });
        return list;
    }

    private static void CollectRows(IEnumerable<PropertyData> props, int owner, bool underCond, List<(int owner, bool cond, List<int> values)> outRows)
    {
        foreach (var p in props)
        {
            string nm = p.Name?.Value?.ToString() ?? "";
            switch (p)
            {
                case MapPropertyData mp when nm == "m_TargetNodes":
                    outRows.Add((owner, underCond, MapValues(mp)));
                    break;
                case MapPropertyData cm when nm == "m_PerConditionValueToNodeMap":
                    if (cm.Value != null)
                    {
                        foreach (var kvp in cm.Value)
                        {
                            if (kvp.Value is StructPropertyData vs && vs.Value != null)
                                CollectRows(vs.Value, owner, true, outRows);
                        }
                    }
                    break;
                case MapPropertyData im when underCond && nm == "m_Map":
                    outRows.Add((owner, true, MapValues(im)));
                    break;
                case StructPropertyData st when st.Value != null:
                    CollectRows(st.Value, owner, underCond, outRows);
                    break;
                case ArrayPropertyData ar when ar.Value != null:
                    CollectRows(ar.Value, owner, underCond, outRows);
                    break;
                case MapPropertyData gm when gm.Value != null:
                    foreach (var kvp in gm.Value)
                        CollectRows(new List<PropertyData> { kvp.Value }, owner, underCond, outRows);
                    break;
            }
        }
    }

    private static List<(int owner, bool cond, List<int> values)> SnapshotTargetRows(UAsset asset)
    {
        var list = new List<(int, bool, List<int>)>();
        ForEachOwnedProps(asset, (owner, props) => CollectRows(props, owner, false, list));
        return list;
    }

    private static void CollectDefaults(IEnumerable<PropertyData> props, int owner, List<(int owner, int value)> outList)
    {
        foreach (var p in props)
        {
            string nm = p.Name?.Value?.ToString() ?? "";
            switch (p)
            {
                case IntPropertyData ip when nm == "m_DefaultAttacks":
                    outList.Add((owner, ip.Value));
                    break;
                case ArrayPropertyData da when nm == "m_DefaultAttacks" && da.Value != null:
                    foreach (var el in da.Value) outList.Add((owner, ReadIdx(el)));
                    break;
                case StructPropertyData st when st.Value != null:
                    CollectDefaults(st.Value, owner, outList);
                    break;
                case ArrayPropertyData ar when ar.Value != null:
                    CollectDefaults(ar.Value, owner, outList);
                    break;
                case MapPropertyData gm when gm.Value != null:
                    foreach (var kvp in gm.Value)
                        CollectDefaults(new List<PropertyData> { kvp.Value }, owner, outList);
                    break;
            }
        }
    }

    private static List<(int owner, int value)> SnapshotDefaults(UAsset asset)
    {
        var list = new List<(int, int)>();
        ForEachOwnedProps(asset, (owner, props) => CollectDefaults(props, owner, list));
        return list;
    }

    private static void CollectProb(IEnumerable<PropertyData> props, int owner, List<(int owner, float? sum, int rowCount, int fpCount)> outList)
    {
        foreach (var p in props)
        {
            string nm = p.Name?.Value?.ToString() ?? "";
            switch (p)
            {
                case ArrayPropertyData tr when nm == "m_Transitions":
                {
                    float sum = 0;
                    int fpCount = 0;
                    int rowCount = tr.Value?.Length ?? 0;
                    if (tr.Value != null)
                    {
                        foreach (var row in tr.Value)
                        {
                            if (row is not StructPropertyData rs || rs.Value == null) continue;
                            var fp = rs.Value.OfType<FloatPropertyData>()
                                .FirstOrDefault(x => x.Name?.Value?.ToString() == "m_fProbability");
                            if (fp == null) continue;
                            sum += fp.Value;
                            fpCount++;
                        }
                    }
                    outList.Add((owner, fpCount > 0 ? sum : (float?)null, rowCount, fpCount));
                    if (tr.Value != null)
                        CollectProb(tr.Value, owner, outList);
                    break;
                }
                case StructPropertyData st when st.Value != null:
                    CollectProb(st.Value, owner, outList);
                    break;
                case ArrayPropertyData ar when ar.Value != null:
                    CollectProb(ar.Value, owner, outList);
                    break;
                case MapPropertyData gm when gm.Value != null:
                    foreach (var kvp in gm.Value)
                        CollectProb(new List<PropertyData> { kvp.Value }, owner, outList);
                    break;
            }
        }
    }

    private static List<(int owner, float? sum, int rowCount, int fpCount)> SnapshotProbArrays(UAsset asset)
    {
        var list = new List<(int, float?, int, int)>();
        ForEachOwnedProps(asset, (owner, props) => CollectProb(props, owner, list));
        return list;
    }

    private static HashSet<int> EntryTargets(UAsset asset)
    {
        var set = new HashSet<int>();
        foreach (var ex in asset.Exports)
        {
            if (ex is not NormalExport ne || ne.Data == null) continue;
            var cont = ne.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboStartNodesContainer");
            if (cont?.Value == null) continue;
            foreach (var entry in cont.Value)
            {
                if (entry is not StructPropertyData es || es.Value == null) continue;
                var rows = new List<(int owner, bool cond, List<int> values)>();
                CollectRows(es.Value, -1, false, rows);
                foreach (var r in rows)
                    foreach (var v in r.values)
                        set.Add(v);
            }
        }
        return set;
    }

    private static int FindEventNodeIndex(UAsset asset)
    {
        var (_, nodes) = ComboOf(asset);
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sp || sp.Value == null) continue;
            var ev = sp.Value.OfType<ObjectPropertyData>().FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Event");
            if (ev?.Value != null && ev.Value.Index != 0) return i;
        }
        return -1;
    }

    private static int FindAttackNodeIndex(UAsset asset)
    {
        var (_, nodes) = ComboOf(asset);
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sp || sp.Value == null) continue;
            var slots = sp.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos")?.Value
                ?.OfType<NamePropertyData>()
                .Where(p => p.Name?.Value?.ToString() == "m_Attacks")
                ?? Enumerable.Empty<NamePropertyData>();
            if (slots.Any(s => s.Value?.Value?.ToString() is string v && !string.Equals(v, "None", StringComparison.Ordinal)))
                return i;
        }
        return -1;
    }

    private static int FindActionNodeIndex(UAsset asset)
    {
        var (_, nodes) = ComboOf(asset);
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sp || sp.Value == null) continue;
            var act = sp.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AIAttackAction");
            var cls = act?.Value?.OfType<ObjectPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_DefaultClassObject");
            if (cls?.Value != null && cls.Value.Index != 0) return i;
        }
        return -1;
    }

    private static string FirstAttackKey(NormalExport combo)
    {
        foreach (var map in FindAllMapsNamed(combo.Data, "m_Attacks"))
        {
            if (map.Value == null) continue;
            foreach (var kvp in map.Value)
            {
                string k = kvp.Key is NamePropertyData np ? np.Value?.Value?.ToString() ?? "" : "";
                if (!string.IsNullOrEmpty(k)) return k;
            }
        }
        return "";
    }

    private static List<MapPropertyData> FindAllMapsNamed(List<PropertyData> props, string name)
    {
        var found = new List<MapPropertyData>();
        void Walk(IEnumerable<PropertyData> list)
        {
            foreach (var p in list)
            {
                switch (p)
                {
                    case MapPropertyData mp when string.Equals(mp.Name?.Value?.ToString(), name, StringComparison.Ordinal):
                        found.Add(mp);
                        break;
                    case StructPropertyData sp when sp.Value != null:
                        Walk(sp.Value);
                        break;
                    case ArrayPropertyData ap when ap.Value != null:
                        Walk(ap.Value);
                        break;
                    case MapPropertyData gm when gm.Value != null:
                        Walk(gm.Value.Select(kvp => kvp.Key));
                        Walk(gm.Value.Select(kvp => kvp.Value));
                        break;
                }
            }
        }
        Walk(props);
        return found;
    }

    // ---------------------------------------------------------- reference helpers

    private static int CountBelow(HashSet<int> removed, int v) => removed.Count(x => x < v);

    private static int ChainMap(int v, HashSet<int> removed, Dictionary<int, int> raw)
    {
        int t = v;
        var seen = new HashSet<int>();
        while (removed.Contains(t))
        {
            if (!seen.Add(t) || !raw.TryGetValue(t, out var next) || next < 0) return -1;
            t = next;
        }
        return t - CountBelow(removed, t);
    }

    private static bool RedirsEqual(List<(int owner, int value)> a, List<(int owner, int value)> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.owner == p.Second.owner && p.First.value == p.Second.value);

    private static bool RowsEqual(List<(int owner, bool cond, List<int> values)> a, List<(int owner, bool cond, List<int> values)> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (x, y) in a.Zip(b))
        {
            if (x.owner != y.owner) return false;
            if (x.cond != y.cond) return false;
            if (x.values.Count != y.values.Count) return false;
            if (!x.values.SequenceEqual(y.values)) return false;
        }
        return true;
    }

    private static bool DefaultsEqual(List<(int owner, int value)> a, List<(int owner, int value)> b) =>
        RedirsEqual(a, b);

    private static string Fmt(List<(int owner, int value)> list) =>
        string.Join(", ", list.Take(12).Select(t => $"{t.owner}->{t.value}")) + (list.Count > 12 ? " ..." : "");

    private static string Fmt(List<(int owner, bool cond, List<int> values)> list) =>
        string.Join(" | ", list.Take(8).Select(t => $"[{(t.cond ? "~" : "")}{t.owner}:{string.Join(",", t.values)}]")) + (list.Count > 8 ? " ..." : "");

    // --------------------------------------------------------------- graph builders

    private static List<AiNodeInfo> Catalog() => _catalog ??= ActionNodeCatalog.Scan(ContentRoot);

    private static AiNodeInfo PickAction(bool crossUnit)
    {
        var cat = Catalog();
        if (crossUnit)
        {
            var ai = cat.FirstOrDefault(x => x.Kind == "Action"
                       && x.SourceUnit.Equals("Fajar", StringComparison.OrdinalIgnoreCase))
                ?? cat.FirstOrDefault(x => x.Kind == "Action" && x.SourceComboRel.Contains("/Fajar/", StringComparison.OrdinalIgnoreCase))
                ?? cat.FirstOrDefault(x => x.Kind == "Action" && !x.SourceComboRel.Contains("Sean", StringComparison.OrdinalIgnoreCase));
            Expect(ai != null, "no cross-unit action in catalog");
            return ai!;
        }
        var any = cat.FirstOrDefault(x => x.Kind == "Action");
        Expect(any != null, "no action in catalog");
        return any!;
    }

    private static AiNodeInfo PickEvent()
    {
        var ai = Catalog().FirstOrDefault(x => x.Kind == "Event"
                   && !x.SourceComboRel.StartsWith("DB/AI/Archetypes/Fajar/", StringComparison.OrdinalIgnoreCase))
            ?? Catalog().FirstOrDefault(x => x.Kind == "Event");
        Expect(ai != null, "no cross-file event in catalog");
        return ai!;
    }

    private static ComboNode BaseNode(int id, int treeIndex) => new()
    {
        Id = id,
        TreeIndex = treeIndex,
        Kind = ComboNodeKind.Vanilla,
        DisplayName = $"Base{treeIndex}"
    };

    private static ComboNode AiNode(int id, AiNodeInfo ai, bool isEvent, string display) => new()
    {
        Id = id,
        TreeIndex = -1,
        Kind = ComboNodeKind.UserCreated,
        DisplayName = display,
        IsActionNode = !isEvent,
        IsEventNode = isEvent,
        ActionClass = isEvent ? "" : ai.ClassName,
        EventName = isEvent ? ai.ClassName : "",
        ImportComboRel = ai.SourceComboRel,
        ImportSourceTreeIndex = ai.SourceTreeIndex
    };

    private static ComboEdge NewEdge(int from, int to, string input) => new()
    {
        FromNodeId = from,
        ToNodeId = to,
        InputName = input,
        IsNewLink = true
    };

    private static string RelNoExt(string file)
    {
        string rel = Path.GetRelativePath(ContentRoot, file).Replace('\\', '/');
        return rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ? rel[..^7] : rel;
    }

    private static string? NameOf(StructPropertyData sp) =>
        sp.Value?.OfType<NamePropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Name")?.Value?.Value?.ToString();

    private static StructPropertyData NodeStructAt(ArrayPropertyData nodes, int idx)
    {
        Expect(idx >= 0 && idx < nodes.Value.Length, $"node index {idx} out of range");
        var sp = nodes.Value[idx] as StructPropertyData;
        Expect(sp?.Value != null, $"node {idx} struct missing");
        return sp!;
    }

    private static int TransitionRowCount(StructPropertyData sp)
    {
        var tr = sp.Value?.OfType<StructPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions");
        var arr = tr?.Value?.OfType<ArrayPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions");
        return arr?.Value?.Length ?? -1;
    }

    private static int RedirectOf(StructPropertyData sp) =>
        sp.Value?.OfType<IntPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_NodeRedirect")?.Value ?? int.MinValue;

    // ------------------------------------------------------- condition-edit tests

    /// <summary>
    /// Condition-edit round trip: a param write through the m_ConditionInstance hop,
    /// an outcome re-route, and a clear (instance emptied to a null ref - the vanilla
    /// "instance ref empty" layout - + outcome map collapsed to a
    /// single 255 entry) - each asserted in memory and again after write + validator +
    /// reparse.
    /// </summary>
    /// <summary>
    /// Merge into an existing row: a new link carrying MergedInto* + an AddedKeys edit
    /// gets NO appended chance row - its target joins the conditioned row's m_TargetNodes
    /// as a new outcome key (add-only: every vanilla key keeps its target), survives the
    /// write, and reparses. Covers the row-pass insert and both append-phase skips.
    /// </summary>
    private static void TestMergeIntoRow(string file)
    {
        var asset = Load(file);
        var (combo, nodes) = ComboOf(asset);
        int nodeCount = nodes.Value.Length;

        // A struct with a CONDITIONED row carrying a keyed m_TargetNodes map.
        int srcStruct = -1, condRow = -1, instBefore = 0;
        var beforeKeys = new Dictionary<byte, int>();
        for (int s = 0; s < nodeCount && srcStruct < 0; s++)
        {
            var trans = TransitionsOf(NodeStructAt(nodes, s));
            if (trans?.Value == null) continue;
            for (int ri = 0; ri < trans.Value!.Length; ri++)
            {
                if (trans.Value[ri] is not StructPropertyData rs || rs.Value == null) continue;
                if (CondIndexOf(rs) <= 0) continue;
                var m = TargetMapOf(rs);
                if (m?.Value == null || m.Value.Count == 0) continue;
                srcStruct = s;
                condRow = ri;
                instBefore = CondIndexOf(rs);
                foreach (var kv in m.Value) beforeKeys[KeyOf(kv.Key)] = ValOf(kv.Value);
                break;
            }
        }
        Expect(srcStruct >= 0 && condRow >= 0, "no conditioned row with a target map");

        var srcTrans = TransitionsOf(NodeStructAt(nodes, srcStruct))!;
        int rowsBefore = srcTrans.Value!.Length;
        int exportsBefore = asset.Exports.Count;

        // Target: a struct this row's map does not already route to.
        var used = new HashSet<int>(beforeKeys.Values.Where(v => v >= 0)) { srcStruct };
        int tgt = -1;
        for (int t = 0; t < nodeCount && tgt < 0; t++)
            if (!used.Contains(t)) tgt = t;
        Expect(tgt >= 0, "no unused target struct");

        // Lowest free outcome key - add-only, never a vanilla key.
        byte freeKey = 0;
        while (freeKey < 255 && beforeKeys.ContainsKey(freeKey)) freeKey++;
        Expect(freeKey < 255, "row map holds every possible key");

        var graph = new ComboGraph
        {
            Nodes = new List<ComboNode> { BaseNode(1, srcStruct), BaseNode(2, tgt) }
        };
        var edge = NewEdge(1, 2, "LMB");
        edge.MergedIntoStruct = srcStruct;
        edge.MergedIntoRow = condRow;
        edge.MergedIntoKey = freeKey;
        graph.Edges.Add(edge);
        graph.TreeRowConditionOverrides[srcStruct] = new Dictionary<int, TreeRowConditionEdit>
        {
            [condRow] = new TreeRowConditionEdit
            {
                AddedKeys = new Dictionary<byte, int> { [freeKey] = 2 }
            }
        };

        var r = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodes, ContentRoot);
        Expect(r.links == 0, $"appended {r.links} links, expected 0 (merged link must not append)");
        Expect(r.skipped == 0, $"skipped {r.skipped}");
        Expect(r.chances >= 1, $"chances {r.chances}, expected the merge edit to apply");
        Expect(asset.Exports.Count == exportsBefore, "merge cloned a condition export");

        var after = TransitionsOf(NodeStructAt(nodes, srcStruct))!;
        Expect(after.Value!.Length == rowsBefore,
            $"row count {after.Value!.Length} != {rowsBefore} (merged link must not add a row)");
        if (after.Value![condRow] is not StructPropertyData rowA || rowA.Value == null)
            throw new Exception("conditioned row missing after merge");
        Expect(CondIndexOf(rowA) == instBefore,
            $"row instance {CondIndexOf(rowA)} != {instBefore} (merge must not touch it)");
        var mapA = TargetMapOf(rowA)!;
        Expect(mapA.Value is { Count: > 0 }, "merged row lost its target map");
        var got = new Dictionary<byte, int>();
        foreach (var kv in mapA.Value!) got[KeyOf(kv.Key)] = ValOf(kv.Value);
        Expect(got.Count == beforeKeys.Count + 1,
            $"map holds {got.Count} keys, expected {beforeKeys.Count + 1}");
        Expect(got.GetValueOrDefault(freeKey) == tgt,
            $"merged key {freeKey} -> {got.GetValueOrDefault(freeKey)}, expected {tgt}");
        foreach (var kv in beforeKeys)
            Expect(got.GetValueOrDefault(kv.Key) == kv.Value,
                $"vanilla key {kv.Key} changed: {got.GetValueOrDefault(kv.Key)} != {kv.Value}");

        string outPath = WriteAndValidate(asset, "merge-into-row");
        var a2 = Load(outPath);
        var (_, n2) = ComboOf(a2);
        var reT = TransitionsOf(NodeStructAt(n2, srcStruct))!;
        Expect(reT.Value!.Length == rowsBefore, "reparsed row count changed");
        if (reT.Value![condRow] is not StructPropertyData rowA2 || rowA2.Value == null)
            throw new Exception("reparsed conditioned row missing");
        Expect(CondIndexOf(rowA2) == instBefore, "reparsed row instance changed");
        var mapA2 = TargetMapOf(rowA2)!;
        Expect(mapA2.Value is { Count: > 0 }, "reparsed merged row lost its target map");
        var got2 = new Dictionary<byte, int>();
        foreach (var kv in mapA2.Value!) got2[KeyOf(kv.Key)] = ValOf(kv.Value);
        Expect(got2.Count == beforeKeys.Count + 1, "reparsed merged key count changed");
        Expect(got2.GetValueOrDefault(freeKey) == tgt, "reparsed merged target changed");
        foreach (var kv in beforeKeys)
            Expect(got2.GetValueOrDefault(kv.Key) == kv.Value,
                $"reparsed vanilla key {kv.Key} changed");
    }

    private static void TestConditionEdits(string file)
    {
        var asset = Load(file);
        var (_, nodes) = ComboOf(asset);
        int nodeCount = nodes.Value.Length;

        // every transition row carrying a condition instance export
        var condRows = new List<(int structIdx, int rowIdx, StructPropertyData rowSp)>();
        for (int s = 0; s < nodeCount; s++)
        {
            var trans = TransitionsOf(NodeStructAt(nodes, s));
            if (trans?.Value == null) continue;
            for (int r = 0; r < trans.Value.Length; r++)
            {
                if (trans.Value[r] is not StructPropertyData row || row.Value == null) continue;
                if (CondIndexOf(row) > 0) condRows.Add((s, r, row));
            }
        }
        Expect(condRows.Count >= 2, $"need >=2 condition rows, found {condRows.Count}");
        var rowA = condRows[0];
        var rowB = condRows[1];

        // --- param edit on row A's condition instance
        int expIdxA = CondIndexOf(rowA.rowSp);
        Expect(expIdxA <= asset.Exports.Count, $"instance index {expIdxA} out of range");
        if (asset.Exports[expIdxA - 1] is not NormalExport expA)
            throw new Exception("instance export is not a NormalExport");
        if (expA.Data == null) throw new Exception("instance export has no Data");
        string path = FindEditableParam(asset, expA.Data!)
            ?? throw new Exception("no editable param found on condition export");
        PropertyData origProp = ResolvePath(asset, expA.Data!, path)
            ?? throw new Exception($"path {path} did not resolve");
        string newVal = NextParamValue(origProp);

        // --- outcome re-route on row A
        var mapA = TargetMapOf(rowA.rowSp);
        Expect(mapA?.Value is { Count: > 0 }, "row A m_TargetNodes missing");
        byte rKey = 0;
        int origT = -1;
        foreach (var kv in mapA!.Value!)
        {
            rKey = KeyOf(kv.Key);
            origT = ValOf(kv.Value);
            break;
        }
        Expect(origT >= 0, "row A original target is not an int");
        int newT = (origT + 1) % nodeCount;
        Expect(newT != origT, "retarget picked the same value");

        // --- clear on row B (target kept = outcome 0's value, else the first entry)
        var mapB = TargetMapOf(rowB.rowSp);
        Expect(mapB?.Value is { Count: > 0 }, "row B m_TargetNodes missing");
        int keepT = -1;
        foreach (var kv in mapB!.Value!)
            if (KeyOf(kv.Key) == 0) { keepT = ValOf(kv.Value); break; }
        if (keepT < 0) keepT = ValOf(mapB.Value!.First().Value);
        Expect(keepT >= 0, "row B keep-target is not an int");

        var graph = new ComboGraph();
        graph.TreeConditionParamOverrides[expIdxA] = new Dictionary<string, string> { [path] = newVal };
        void AddEdit(int s, int r, TreeRowConditionEdit e)
        {
            if (!graph.TreeRowConditionOverrides.TryGetValue(s, out var m))
                graph.TreeRowConditionOverrides[s] = m = new Dictionary<int, TreeRowConditionEdit>();
            m[r] = e;
        }
        AddEdit(rowA.structIdx, rowA.rowIdx,
            new TreeRowConditionEdit { Retarget = new Dictionary<byte, int> { [rKey] = newT } });
        AddEdit(rowB.structIdx, rowB.rowIdx, new TreeRowConditionEdit { ClearCondition = true });

        int applied = ExportDialog.ApplyConditionOverrides(graph, asset, nodes);
        Expect(applied >= 3, $"applied {applied} edits, expected >=3");

        // in-memory asserts
        AssertTypedValue(ResolvePath(asset, expA.Data!, path)!, newVal, "in-memory param");
        Expect(ValOf(mapA.Value!.First(kv => KeyOf(kv.Key) == rKey).Value) == newT,
            "retarget not applied in memory");
        Expect(rowB.rowSp.Value.OfType<ObjectPropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_ConditionInstance"
                       && (p.Value == null || p.Value.Index == 0)),
            "clear did not empty m_ConditionInstance to a null ref");
        var mapB2 = TargetMapOf(rowB.rowSp)!;
        Expect(mapB2.Value!.Count == 1 && KeyOf(mapB2.Value!.First().Key) == 255
            && ValOf(mapB2.Value!.First().Value) == keepT,
            "clear did not collapse the outcome map to one 255 entry");

        string outPath = WriteAndValidate(asset, "condition-edits");

        // reparse asserts (indices stable - nothing was removed)
        var a2 = Load(outPath);
        var (_, nodes2) = ComboOf(a2);
        var spA2 = NodeStructAt(nodes2, rowA.structIdx);
        var rowA2 = (StructPropertyData)TransitionsOf(spA2)!.Value![rowA.rowIdx];
        int expIdxA2 = CondIndexOf(rowA2);
        Expect(expIdxA2 == expIdxA, $"reparsed instance index {expIdxA2} != {expIdxA}");
        var instA2 = (NormalExport)a2.Exports[expIdxA2 - 1];
        AssertTypedValue(ResolvePath(a2, instA2.Data!, path)!, newVal, "reparsed param");
        var mapA2 = TargetMapOf(rowA2)!;
        Expect(ValOf(mapA2.Value!.First(kv => KeyOf(kv.Key) == rKey).Value) == newT,
            "reparsed retarget lost");

        var spB2 = NodeStructAt(nodes2, rowB.structIdx);
        var rowB2 = (StructPropertyData)TransitionsOf(spB2)!.Value![rowB.rowIdx];
        Expect(rowB2.Value.OfType<ObjectPropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_ConditionInstance"
                       && (p.Value == null || p.Value.Index == 0)),
            "reparsed clear lost the emptied instance ref");
        var mapB2r = TargetMapOf(rowB2)!;
        Expect(mapB2r.Value!.Count == 1 && KeyOf(mapB2r.Value!.First().Key) == 255
            && ValOf(mapB2r.Value!.First().Value) == keepT,
            "reparsed clear lost the collapsed map");

        // --- seeded collapse target: clear + explicit Retarget[255] wins over the
        // key-0 rule (the dialog seeds this so the surviving route stays editable).
        int altT = (keepT + 1) % nodeCount;
        Expect(altT != keepT, "alt target picked the same value");
        AddEdit(rowB.structIdx, rowB.rowIdx, new TreeRowConditionEdit
        {
            ClearCondition = true,
            Retarget = new Dictionary<byte, int> { [255] = altT }
        });
        ExportDialog.ApplyConditionOverrides(graph, asset, nodes);
        var mapB3 = TargetMapOf(rowB.rowSp)!;
        Expect(mapB3.Value!.Count == 1 && KeyOf(mapB3.Value!.First().Key) == 255
            && ValOf(mapB3.Value!.First().Value) == altT,
            "Retarget[255] did not override the collapse default in memory");

        string outPath2 = WriteAndValidate(asset, "condition-edits-retarget");
        var a3 = Load(outPath2);
        var (_, nodes3) = ComboOf(a3);
        var spB3 = NodeStructAt(nodes3, rowB.structIdx);
        var rowB3 = (StructPropertyData)TransitionsOf(spB3)!.Value![rowB.rowIdx];
        var mapB3r = TargetMapOf(rowB3)!;
        Expect(mapB3r.Value!.Count == 1 && KeyOf(mapB3r.Value!.First().Key) == 255
            && ValOf(mapB3r.Value!.First().Value) == altT,
            "reparsed Retarget[255] lost");

        // --- user-authored target: RetargetNodeIds (graph node id, the shape the
        // dialog stores for [new] picks) resolves through resolveNodeIndex - the same
        // resolver AddedKeys/TargetNatural use - and lands as the resolved export index.
        int resolvedT = (altT + 1) % nodeCount;
        Expect(resolvedT != altT, "resolved target picked the same value");
        int fakeNodeId = 424242;
        AddEdit(rowB.structIdx, rowB.rowIdx, new TreeRowConditionEdit
        {
            ClearCondition = true,
            Retarget = new Dictionary<byte, int> { [255] = altT },
            RetargetNodeIds = new Dictionary<byte, int> { [255] = fakeNodeId }
        });
        ExportDialog.ApplyConditionOverrides(graph, asset, nodes,
            resolveNodeIndex: id => id == fakeNodeId ? resolvedT : null);
        var mapB4 = TargetMapOf(rowB.rowSp)!;
        Expect(mapB4.Value!.Count == 1 && KeyOf(mapB4.Value!.First().Key) == 255
            && ValOf(mapB4.Value!.First().Value) == resolvedT,
            "RetargetNodeIds did not route through resolveNodeIndex in memory");

        string outPath3 = WriteAndValidate(asset, "condition-edits-node-retarget");
        var a4 = Load(outPath3);
        var (_, nodes4) = ComboOf(a4);
        var spB4 = NodeStructAt(nodes4, rowB.structIdx);
        var rowB4 = (StructPropertyData)TransitionsOf(spB4)!.Value![rowB.rowIdx];
        var mapB4r = TargetMapOf(rowB4)!;
        Expect(mapB4r.Value!.Count == 1 && KeyOf(mapB4r.Value!.First().Key) == 255
            && ValOf(mapB4r.Value!.First().Value) == resolvedT,
            "reparsed RetargetNodeIds lost");

        // unresolvable node id: the node-id key is skipped and the plain Retarget
        // entry still wins.
        AddEdit(rowB.structIdx, rowB.rowIdx, new TreeRowConditionEdit
        {
            ClearCondition = true,
            Retarget = new Dictionary<byte, int> { [255] = altT },
            RetargetNodeIds = new Dictionary<byte, int> { [255] = 999999 }
        });
        ExportDialog.ApplyConditionOverrides(graph, asset, nodes,
            resolveNodeIndex: id => id == fakeNodeId ? resolvedT : null);
        var mapB5 = TargetMapOf(rowB.rowSp)!;
        Expect(ValOf(mapB5.Value!.First(kv => KeyOf(kv.Key) == 255).Value) == altT,
            "unresolvable RetargetNodeIds entry should leave the Retarget value");
    }

    /// <summary>
    /// Add-condition round trip: share - point an unconditional row at an existing
    /// same-file condition export; clone - append a condition export from another combo
    /// file via AssetCloner onto a second row, with its param edit keyed by
    /// CloneVirtualKey resolved to the clone's new index. Asserted in memory and again
    /// after write + validator + reparse.
    /// </summary>
    private static void TestAddCondition(string file, string otherFile)
    {
        var asset = Load(file);
        var (_, nodes) = ComboOf(asset);
        int nodeCount = nodes.Value.Length;
        int exportsBefore = asset.Exports.Count;

        int shareInstance = -1;
        var uncond = new List<(int structIdx, int rowIdx, StructPropertyData rowSp)>();
        for (int s = 0; s < nodeCount; s++)
        {
            var trans = TransitionsOf(NodeStructAt(nodes, s));
            if (trans?.Value == null) continue;
            for (int r = 0; r < trans.Value.Length; r++)
            {
                if (trans.Value[r] is not StructPropertyData row || row.Value == null) continue;
                int ci = CondIndexOf(row);
                if (ci > 0)
                {
                    if (shareInstance < 0) shareInstance = ci;
                }
                else if (TargetMapOf(row)?.Value is { Count: > 0 })
                {
                    uncond.Add((s, r, row));
                }
            }
        }
        Expect(shareInstance > 0 && shareInstance <= asset.Exports.Count,
            "no conditional row to share its instance from");
        Expect(uncond.Count >= 2, $"need >=2 unconditional rows, found {uncond.Count}");
        var rowC = uncond[0];
        var rowD = uncond[1];

        // Original unconditional targets: the {255} map re-key must preserve them.
        static int OrigTargetOf(StructPropertyData rowSp)
        {
            var m = TargetMapOf(rowSp);
            Expect(m?.Value != null, "unconditional row has no m_TargetNodes map");
            foreach (var mk in m!.Value!)
                if (KeyOf(mk.Key) == 255) return ValOf(mk.Value);
            throw new Exception("unconditional row map has no 255 entry");
        }
        int origTC = OrigTargetOf(rowC.rowSp);
        int origTD = OrigTargetOf(rowD.rowSp);

        var srcAsset = Load(otherFile);
        var (_, srcNodes) = ComboOf(srcAsset);
        int srcExp0 = -1;
        for (int s = 0; srcExp0 < 0 && s < srcNodes.Value.Length; s++)
        {
            var trans = TransitionsOf(NodeStructAt(srcNodes, s));
            if (trans?.Value == null) continue;
            foreach (var row in trans.Value)
            {
                if (row is not StructPropertyData rs || rs.Value == null) continue;
                if (CondIndexOf(rs) > 0) { srcExp0 = CondIndexOf(rs) - 1; break; }
            }
        }
        Expect(srcExp0 >= 0 && srcExp0 < srcAsset.Exports.Count, "no condition export in clone source");
        if (srcAsset.Exports[srcExp0] is not NormalExport srcCond || srcCond.Data == null)
            throw new Exception("clone source condition export has no Data");
        string clsName = ExportClassNameOf(srcAsset, srcExp0);
        Expect(clsName.Length > 0, "clone source class name is empty");
        string paramPath = FindEditableParam(srcAsset, srcCond.Data)
            ?? throw new Exception("no editable param on clone source");
        PropertyData srcProp = ResolvePath(srcAsset, srcCond.Data, paramPath)
            ?? throw new Exception($"clone source path {paramPath} did not resolve");
        string cloneVal = NextParamValue(srcProp);
        string rel = Path.GetRelativePath(ContentRoot, otherFile).Replace('\\', '/');
        if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) rel = rel[..^7];

        var graph = new ComboGraph();
        void AddEdit(int s, int r, TreeRowConditionEdit e)
        {
            if (!graph.TreeRowConditionOverrides.TryGetValue(s, out var m))
                graph.TreeRowConditionOverrides[s] = m = new Dictionary<int, TreeRowConditionEdit>();
            m[r] = e;
        }
        // rowC simulates a dialog-seeded edit (outcome keys 0/1, each keeping the
        // row's original target); rowD keeps an empty Retarget - the older-save
        // fallback where export seeds {0,1} at the original target itself.
        var editC = new TreeRowConditionEdit
        {
            SetInstance = shareInstance,
            CloneClass = ExportClassNameOf(asset, shareInstance - 1)
        };
        editC.Retarget[0] = origTC;
        editC.Retarget[1] = origTC;
        AddEdit(rowC.structIdx, rowC.rowIdx, editC);
        AddEdit(rowD.structIdx, rowD.rowIdx, new TreeRowConditionEdit
        {
            CloneSourceRel = rel,
            CloneSourceExport = srcExp0,
            CloneClass = clsName
        });
        int vk = TreeRowConditionEdit.CloneVirtualKey(rel, srcExp0);
        Expect(vk < 0, $"virtual key {vk} must be negative");
        graph.TreeConditionParamOverrides[vk] = new Dictionary<string, string> { [paramPath] = cloneVal };

        int applied = ExportDialog.ApplyConditionOverrides(graph, asset, nodes, ContentRoot);
        Expect(applied >= 3, $"applied {applied} edits, expected >=3 (share + clone + param)");

        Expect(CondIndexOf(rowC.rowSp) == shareInstance,
            $"share set instance {CondIndexOf(rowC.rowSp)}, expected {shareInstance}");

        int clonedIdx = CondIndexOf(rowD.rowSp);
        Expect(clonedIdx > exportsBefore && clonedIdx <= asset.Exports.Count,
            $"clone index {clonedIdx} not appended (before {exportsBefore}, now {asset.Exports.Count})");
        if (asset.Exports[clonedIdx - 1] is not NormalExport cloneExp || cloneExp.Data == null)
            throw new Exception("cloned export is missing property data");
        Expect(ExportClassNameOf(asset, clonedIdx - 1) == clsName,
            $"cloned class {ExportClassNameOf(asset, clonedIdx - 1)} != {clsName}");
        AssertTypedValue(ResolvePath(asset, cloneExp.Data, paramPath)!, cloneVal, "in-memory clone param");

        // The added condition only gates in game when the map is keyed by outcome -
        // a lingering {255} entry would make every outcome fall through unused.
        void AssertRekeyed(string label, StructPropertyData rowSp, int expected)
        {
            var m = TargetMapOf(rowSp);
            Expect(m?.Value is { Count: 2 },
                $"{label}: map has {m?.Value?.Count ?? 0} entries, expected 2");
            var ks = m!.Value!.Select(mk => KeyOf(mk.Key)).OrderBy(k => k).ToList();
            Expect(!ks.Contains(255), $"{label}: map still contains 255");
            Expect(ks.SequenceEqual(new byte[] { 0, 1 }),
                $"{label}: keys [{string.Join(",", ks)}] != [0,1]");
            foreach (var mk in m.Value)
                Expect(ValOf(mk.Value) == expected,
                    $"{label}: key {KeyOf(mk.Key)} target {ValOf(mk.Value)} != original {expected}");
        }
        AssertRekeyed("in-memory share row", rowC.rowSp, origTC);
        AssertRekeyed("in-memory clone row", rowD.rowSp, origTD);

        string outPath = WriteAndValidate(asset, "add-condition");

        var a2 = Load(outPath);
        var (_, nodes2) = ComboOf(a2);
        var rowC2 = (StructPropertyData)TransitionsOf(NodeStructAt(nodes2, rowC.structIdx))!.Value![rowC.rowIdx];
        Expect(CondIndexOf(rowC2) == shareInstance, "reparsed share lost the instance index");
        var rowD2 = (StructPropertyData)TransitionsOf(NodeStructAt(nodes2, rowD.structIdx))!.Value![rowD.rowIdx];
        int cloned2 = CondIndexOf(rowD2);
        Expect(cloned2 > 0 && cloned2 <= a2.Exports.Count, $"reparsed clone index {cloned2} invalid");
        if (a2.Exports[cloned2 - 1] is not NormalExport cloneExp2 || cloneExp2.Data == null)
            throw new Exception("reparsed clone export is missing property data");
        Expect(ExportClassNameOf(a2, cloned2 - 1) == clsName, "reparsed clone class mismatch");
        AssertTypedValue(ResolvePath(a2, cloneExp2.Data, paramPath)!, cloneVal, "reparsed clone param");
        AssertRekeyed("reparsed share row", rowC2, origTC);
        AssertRekeyed("reparsed clone row", rowD2, origTD);
    }

    /// <summary>
    /// Edge-condition round trip: a clone condition (with its virtual-key param) written
    /// onto the row a new link appends - one outcome key routing to the link's own target
    /// (TargetNatural), one blocked (TargetBlocked), one explicitly re-routed - plus a
    /// second, condition-less link asserting an appended row never inherits the template's
    /// m_ConditionInstance or keyed outcome map (the template is picked arbitrarily and
    /// may be conditional). In memory and after write + validator + reparse.
    /// </summary>
    private static void TestAddEdgeCondition(string file, string otherFile)
    {
        var asset = Load(file);
        var (combo, nodes) = ComboOf(asset);
        int nodeCount = nodes.Value.Length;
        int exportsBefore = asset.Exports.Count;

        // Prefer a struct whose FIRST row is conditional - that is the row Phase B picks
        // as the transition template, and the inherited-instance case the export fix
        // guards. Falls back to the first struct with transitions.
        int srcStruct = -1, fallback = -1;
        for (int s = 0; s < nodeCount; s++)
        {
            var trans = TransitionsOf(NodeStructAt(nodes, s));
            if (trans?.Value == null || trans.Value.Length == 0) continue;
            if (fallback < 0) fallback = s;
            if (trans.Value[0] is StructPropertyData row0 && row0.Value != null && CondIndexOf(row0) > 0)
            {
                srcStruct = s;
                break;
            }
        }
        if (srcStruct < 0) srcStruct = fallback;
        Expect(srcStruct >= 0, "no struct with transitions");

        var srcTrans = TransitionsOf(NodeStructAt(nodes, srcStruct))!;
        int rowsBefore = srcTrans.Value!.Length;
        var used = new HashSet<int> { srcStruct };
        foreach (var rw in srcTrans.Value!)
        {
            if (rw is not StructPropertyData rs || rs.Value == null) continue;
            var m = TargetMapOf(rs);
            if (m?.Value == null) continue;
            foreach (var kv in m.Value)
            {
                int v = ValOf(kv.Value);
                if (v >= 0) used.Add(v);
            }
        }
        int PickUnused()
        {
            for (int t = 0; t < nodeCount; t++)
                if (!used.Contains(t)) { used.Add(t); return t; }
            return -1;
        }
        int tgtStruct = PickUnused();
        int tgtPlain = PickUnused();
        Expect(tgtStruct >= 0 && tgtPlain >= 0, "not enough unused target structs");
        int explicitTgt = (tgtStruct + 1) % nodeCount;
        Expect(explicitTgt != tgtStruct, "explicit target equals natural target");

        // clone source condition (other file) + its editable param
        var srcAsset = Load(otherFile);
        var (_, srcNodes) = ComboOf(srcAsset);
        int srcExp0 = -1;
        for (int s = 0; srcExp0 < 0 && s < srcNodes.Value.Length; s++)
        {
            var trans = TransitionsOf(NodeStructAt(srcNodes, s));
            if (trans?.Value == null) continue;
            foreach (var row in trans.Value)
            {
                if (row is not StructPropertyData rs || rs.Value == null) continue;
                if (CondIndexOf(rs) > 0) { srcExp0 = CondIndexOf(rs) - 1; break; }
            }
        }
        Expect(srcExp0 >= 0 && srcExp0 < srcAsset.Exports.Count, "no condition export in clone source");
        if (srcAsset.Exports[srcExp0] is not NormalExport srcCond || srcCond.Data == null)
            throw new Exception("clone source condition export has no Data");
        string clsName = ExportClassNameOf(srcAsset, srcExp0);
        Expect(clsName.Length > 0, "clone source class name is empty");
        string paramPath = FindEditableParam(srcAsset, srcCond.Data)
            ?? throw new Exception("no editable param on clone source");
        PropertyData srcProp = ResolvePath(srcAsset, srcCond.Data, paramPath)
            ?? throw new Exception($"clone source path {paramPath} did not resolve");
        string cloneVal = NextParamValue(srcProp);
        string rel = RelNoExt(otherFile);
        int vk = TreeRowConditionEdit.CloneVirtualKey(rel, srcExp0);
        Expect(vk < 0, $"virtual key {vk} must be negative");

        var graph = new ComboGraph
        {
            Nodes = new List<ComboNode>
            {
                BaseNode(1, srcStruct), BaseNode(2, tgtStruct), BaseNode(3, tgtPlain)
            }
        };
        var edgeCond = NewEdge(1, 2, "LMB");
        var edgePlain = NewEdge(1, 3, "RMB");
        graph.Edges.Add(edgeCond);
        graph.Edges.Add(edgePlain);
        graph.TreeEdgeConditionOverrides[ComboGraph.EdgeConditionKey(edgeCond)] = new TreeRowConditionEdit
        {
            CloneSourceRel = rel,
            CloneSourceExport = srcExp0,
            CloneClass = clsName,
            Retarget = new Dictionary<byte, int>
            {
                [0] = TreeRowConditionEdit.TargetNatural,
                [1] = TreeRowConditionEdit.TargetBlocked,
                [2] = explicitTgt
            }
        };
        graph.TreeConditionParamOverrides[vk] = new Dictionary<string, string> { [paramPath] = cloneVal };

        var r = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodes, ContentRoot);
        Expect(r.links == 2, $"appended {r.links} links, expected 2");
        Expect(r.skipped == 0, $"skipped {r.skipped}");

        var after = TransitionsOf(NodeStructAt(nodes, srcStruct))!;
        Expect(after.Value!.Length == rowsBefore + 2,
            $"row count {after.Value!.Length} != {rowsBefore + 2}");

        // conditioned link row: clone instance + exactly the requested outcome keys
        if (after.Value![rowsBefore] is not StructPropertyData rowA || rowA.Value == null)
            throw new Exception("conditioned appended row missing");
        int instIdx = CondIndexOf(rowA);
        Expect(instIdx > exportsBefore && instIdx <= asset.Exports.Count,
            $"edge instance {instIdx} not the appended clone (exports before {exportsBefore})");
        if (asset.Exports[instIdx - 1] is not NormalExport instExp || instExp.Data == null)
            throw new Exception("edge clone export missing data");
        Expect(ExportClassNameOf(asset, instIdx - 1) == clsName,
            $"edge clone class {ExportClassNameOf(asset, instIdx - 1)} != {clsName}");
        AssertTypedValue(ResolvePath(asset, instExp.Data, paramPath)!, cloneVal, "in-memory edge clone param");

        var mapA = TargetMapOf(rowA)!;
        Expect(mapA.Value is { Count: 2 }, $"edge row key count {mapA.Value?.Count}, expected 2");
        var got = new Dictionary<byte, int>();
        foreach (var kv in mapA.Value!) got[KeyOf(kv.Key)] = ValOf(kv.Value);
        Expect(!got.ContainsKey(1), "blocked key 1 present in edge row map");
        Expect(got.GetValueOrDefault((byte)0) == tgtStruct,
            $"natural key 0 -> {got.GetValueOrDefault((byte)0)}, expected link target {tgtStruct}");
        Expect(got.GetValueOrDefault((byte)2) == explicitTgt,
            $"explicit key 2 -> {got.GetValueOrDefault((byte)2)}, expected {explicitTgt}");

        // condition-less link: appended row must never inherit the template's condition
        // instance or keyed outcome map - single 255 entry pointing at its own target.
        if (after.Value![rowsBefore + 1] is not StructPropertyData rowB || rowB.Value == null)
            throw new Exception("plain appended row missing");
        Expect(CondIndexOf(rowB) == 0, $"plain row inherited template instance {CondIndexOf(rowB)}");
        var mapB = TargetMapOf(rowB)!;
        Expect(mapB.Value is { Count: 1 } && KeyOf(mapB.Value!.First().Key) == 255,
            $"plain row map not a single 255 entry (count {mapB.Value?.Count})");
        Expect(ValOf(mapB.Value!.First().Value) == tgtPlain,
            $"plain row target {ValOf(mapB.Value!.First().Value)} != {tgtPlain}");

        string outPath = WriteAndValidate(asset, "edge-condition");

        var a2 = Load(outPath);
        var (_, n2) = ComboOf(a2);
        var reT = TransitionsOf(NodeStructAt(n2, srcStruct))!;
        Expect(reT.Value!.Length == rowsBefore + 2, "reparsed row count changed");

        if (reT.Value![rowsBefore] is not StructPropertyData rowA2 || rowA2.Value == null)
            throw new Exception("reparsed conditioned row missing");
        int inst2 = CondIndexOf(rowA2);
        Expect(inst2 == instIdx, $"reparsed instance {inst2} != {instIdx}");
        if (a2.Exports[inst2 - 1] is not NormalExport ie2 || ie2.Data == null)
            throw new Exception("reparsed clone export missing");
        Expect(ExportClassNameOf(a2, inst2 - 1) == clsName, "reparsed clone class mismatch");
        AssertTypedValue(ResolvePath(a2, ie2.Data, paramPath)!, cloneVal, "reparsed edge clone param");
        var mapA2 = TargetMapOf(rowA2)!;
        Expect(mapA2.Value is { Count: 2 }, "reparsed edge row key count changed");
        var got2 = new Dictionary<byte, int>();
        foreach (var kv in mapA2.Value!) got2[KeyOf(kv.Key)] = ValOf(kv.Value);
        Expect(!got2.ContainsKey(1) && got2.GetValueOrDefault((byte)0) == tgtStruct
            && got2.GetValueOrDefault((byte)2) == explicitTgt, "reparsed edge row targets lost");

        if (reT.Value![rowsBefore + 1] is not StructPropertyData rowB2 || rowB2.Value == null)
            throw new Exception("reparsed plain row missing");
        Expect(CondIndexOf(rowB2) == 0, "reparsed plain row gained an instance");
        var mapB2 = TargetMapOf(rowB2)!;
        Expect(mapB2.Value is { Count: 1 } && KeyOf(mapB2.Value!.First().Key) == 255
            && ValOf(mapB2.Value!.First().Value) == tgtPlain, "reparsed plain row map changed");
    }

    /// <summary>
    /// Clear/mixup dedup: ExistingTargets must read POST-override - a link to an
    /// outcome a clear collapses away still appends (the dialog stages it as a chance
    /// row), while the same link with no edit stays deduped because a row serves the
    /// target. Covers the retarget-uncover variant (same math) as a fallback, and the
    /// shared TreeChanceMath.PostOverrideTargets rule directly.
    /// </summary>
    private static void TestClearMixupAppend(string file)
    {
        // --- shared math: clear collapse, seeded 255, in-place retarget, negative skip
        static List<int> P(List<byte> k, List<int> v, TreeRowConditionEdit? e)
            => TreeChanceMath.PostOverrideTargets(k, v, e);
        var keys = new List<byte> { 0, 1 };
        var vals = new List<int> { 10, 20 };
        Expect(P(keys, vals, null).SequenceEqual(new[] { 10, 20 }), "no edit: raw target set wrong");
        Expect(P(keys, vals, new TreeRowConditionEdit { ClearCondition = true })
                .SequenceEqual(new[] { 10 }),
            "clear: survivor is not outcome 0's value");
        Expect(P(keys, vals, new TreeRowConditionEdit
            {
                ClearCondition = true,
                Retarget = new Dictionary<byte, int> { [255] = 33 }
            }).SequenceEqual(new[] { 33 }),
            "clear + Retarget[255]: seeded survivor lost");
        Expect(P(keys, vals, new TreeRowConditionEdit
            {
                Retarget = new Dictionary<byte, int> { [1] = 44, [0] = -5 }
            }).SequenceEqual(new[] { 10, 44 }),
            "retarget: rewrite/negative-skip rule wrong");

        // --- asset: a conditional row whose edit uncovers one of its targets. The
        // uncovered target must be served ONLY by that row/key (another row keeping it
        // would - correctly - keep the link deduped).
        var asset = Load(file);
        var (combo, nodes) = ComboOf(asset);
        int nodeCount = nodes.Value.Length;
        int clearStruct = -1, clearRow = -1, clearKeep = -1, clearDrop = -1;
        int reStruct = -1, reRow = -1, reOld = -1, reNew = -1, reKey = -1;
        for (int s = 0; s < nodeCount; s++)
        {
            var trans = TransitionsOf(NodeStructAt(nodes, s));
            if (trans?.Value == null) continue;
            for (int r = 0; r < trans.Value.Length; r++)
            {
                if (trans.Value[r] is not StructPropertyData row || row.Value == null) continue;
                if (CondIndexOf(row) <= 0) continue;
                var m = TargetMapOf(row);
                if (m?.Value is not { Count: > 0 }) continue;
                var pairs = new List<(byte key, int val)>();
                foreach (var kv in m.Value)
                {
                    int v = ValOf(kv.Value);
                    if (v >= 0) pairs.Add((KeyOf(kv.Key), v));
                }
                if (pairs.Count == 0) continue;

                // targets every OTHER row of this struct still serves
                var others = new HashSet<int>();
                for (int j = 0; j < trans.Value.Length; j++)
                {
                    if (j == r || trans.Value[j] is not StructPropertyData oj || oj.Value == null) continue;
                    var om = TargetMapOf(oj);
                    if (om?.Value == null) continue;
                    foreach (var kv in om.Value)
                    {
                        int v = ValOf(kv.Value);
                        if (v >= 0) others.Add(v);
                    }
                }

                int keep = -1;
                foreach (var p in pairs)
                    if (p.key == 0) { keep = p.val; break; }
                if (keep < 0) keep = pairs[0].val;

                // clear path: a drop target no other row serves (and not a self-target -
                // Phase B would skip that link as a self link).
                if (clearStruct < 0)
                {
                    foreach (var p in pairs)
                    {
                        if (p.val == keep || p.val == s || others.Contains(p.val)) continue;
                        clearStruct = s; clearRow = r; clearKeep = keep; clearDrop = p.val;
                        break;
                    }
                }

                // retarget fallback: rewrite one key away from a value served only there
                // (no sibling key, no other row) - the old value then needs its own row.
                if (reStruct < 0 && keep >= 0 && keep != s && !others.Contains(keep))
                {
                    foreach (var p in pairs)
                    {
                        if (p.val != keep || others.Contains(p.val)) continue;
                        if (pairs.Count(q => q.val == p.val) > 1) continue;
                        int nt = (keep + 1) % nodeCount;
                        if (nt == s) nt = (nt + 1) % nodeCount;
                        if (nt == keep) continue;
                        reStruct = s; reRow = r; reOld = keep; reNew = nt; reKey = p.key;
                        break;
                    }
                }
                if (clearStruct >= 0 && reStruct >= 0) break;
            }
            if (clearStruct >= 0 && reStruct >= 0) break;
        }

        bool clearPath = clearStruct >= 0;
        Expect(clearPath || reStruct >= 0,
            "no usable conditional row (multi-key clear or retarget candidate)");
        int srcStruct = clearPath ? clearStruct : reStruct;
        int rowIdx = clearPath ? clearRow : reRow;
        int serveT = clearPath ? clearKeep : reOld;   // what the row serves pre-edit
        int uncoverT = clearPath ? clearDrop : reOld; // what the edit stops serving
        var edit = clearPath
            ? new TreeRowConditionEdit { ClearCondition = true }
            : new TreeRowConditionEdit
            {
                Retarget = new Dictionary<byte, int> { [(byte)reKey] = reNew }
            };

        var srcTrans = TransitionsOf(NodeStructAt(nodes, srcStruct))!;
        int rowsBefore = srcTrans.Value!.Length;

        // --- guard: the same link with no edit - a row still serves the target, so the
        // dedup must skip it (the pre-fix behavior this test pins for the non-edit case).
        var graphA = new ComboGraph
        {
            Nodes = new List<ComboNode> { BaseNode(1, srcStruct), BaseNode(2, uncoverT) }
        };
        graphA.Edges.Add(NewEdge(1, 2, ""));
        var rA = ExportDialog.AppendEnemyAuthoredContent(graphA, asset, combo, nodes, ContentRoot);
        Expect(rA.links == 0, $"guard: appended {rA.links} link(s) with no edit, expected 0 (dedup)");
        Expect(rA.skipped == 0, $"guard: skipped {rA.skipped}");
        Expect(TransitionsOf(NodeStructAt(nodes, srcStruct))!.Value!.Length == rowsBefore,
            "guard: row count changed");

        // --- clear/retarget edit + FromClearMixup link: the uncovered target now needs
        // its own row - append must land (old raw-asset dedup refused it).
        var graphB = new ComboGraph
        {
            Nodes = new List<ComboNode> { BaseNode(1, srcStruct), BaseNode(2, uncoverT) }
        };
        graphB.Edges.Add(new ComboEdge
        {
            FromNodeId = 1,
            ToNodeId = 2,
            InputName = "",
            IsNewLink = true,
            FromClearMixup = true
        });
        if (!graphB.TreeRowConditionOverrides.TryGetValue(srcStruct, out var rowMap))
            graphB.TreeRowConditionOverrides[srcStruct] = rowMap = new Dictionary<int, TreeRowConditionEdit>();
        rowMap[rowIdx] = edit;

        var rB = ExportDialog.AppendEnemyAuthoredContent(graphB, asset, combo, nodes, ContentRoot);
        Expect(rB.links == 1, $"{(clearPath ? "clear" : "retarget")}+mixup: appended {rB.links} link(s), expected 1 (post-override dedup)");
        Expect(rB.skipped == 0, $"{(clearPath ? "clear" : "retarget")}+mixup: skipped {rB.skipped}");

        var after = TransitionsOf(NodeStructAt(nodes, srcStruct))!;
        Expect(after.Value!.Length == rowsBefore + 1,
            $"row count {after.Value!.Length} != {rowsBefore + 1}");

        // appended mixup row: unconditional single 255 entry at the link's target
        if (after.Value![rowsBefore] is not StructPropertyData newRow || newRow.Value == null)
            throw new Exception("appended mixup row missing");
        Expect(CondIndexOf(newRow) == 0, $"mixup row inherited a condition instance ({CondIndexOf(newRow)})");
        var newMap = TargetMapOf(newRow)!;
        Expect(newMap.Value is { Count: 1 } && KeyOf(newMap.Value!.First().Key) == 255,
            $"mixup row map is not a single 255 entry (count {newMap.Value?.Count})");
        Expect(ValOf(newMap.Value!.First().Value) == uncoverT,
            $"mixup row target {ValOf(newMap.Value!.First().Value)} != {uncoverT}");

        // the edit itself landed on the source row
        if (after.Value![rowIdx] is not StructPropertyData srcRow || srcRow.Value == null)
            throw new Exception("source row missing");
        if (clearPath)
        {
            Expect(srcRow.Value.OfType<ObjectPropertyData>()
                    .Any(p => p.Name?.Value?.ToString() == "m_ConditionInstance"
                           && (p.Value == null || p.Value.Index == 0)),
                "clear did not empty m_ConditionInstance");
            var sm = TargetMapOf(srcRow)!;
            Expect(sm.Value is { Count: 1 } && KeyOf(sm.Value!.First().Key) == 255
                   && ValOf(sm.Value!.First().Value) == serveT,
                "clear did not collapse the row to its survivor");
        }
        else
        {
            var sm = TargetMapOf(srcRow)!;
            Expect(ValOf(sm.Value!.First(kv => KeyOf(kv.Key) == (byte)reKey).Value) == reNew,
                "retarget not applied to the source row");
        }

        string outPath = WriteAndValidate(asset, "clear-mixup");

        // --- reparse asserts (indices stable - nothing removed)
        var a2 = Load(outPath);
        var (_, n2) = ComboOf(a2);
        var reT = TransitionsOf(NodeStructAt(n2, srcStruct))!;
        Expect(reT.Value!.Length == rowsBefore + 1, "reparsed row count changed");
        if (reT.Value![rowsBefore] is not StructPropertyData newRow2 || newRow2.Value == null)
            throw new Exception("reparsed mixup row missing");
        Expect(CondIndexOf(newRow2) == 0, "reparsed mixup row gained a condition");
        var newMap2 = TargetMapOf(newRow2)!;
        Expect(newMap2.Value is { Count: 1 } && KeyOf(newMap2.Value!.First().Key) == 255
               && ValOf(newMap2.Value!.First().Value) == uncoverT,
            "reparsed mixup row map changed");
        if (reT.Value![rowIdx] is not StructPropertyData srcRow2 || srcRow2.Value == null)
            throw new Exception("reparsed source row missing");
        if (clearPath)
        {
            Expect(srcRow2.Value.OfType<ObjectPropertyData>()
                    .Any(p => p.Name?.Value?.ToString() == "m_ConditionInstance"
                           && (p.Value == null || p.Value.Index == 0)),
                "reparsed clear lost the emptied instance ref");
            var sm2 = TargetMapOf(srcRow2)!;
            Expect(sm2.Value is { Count: 1 } && KeyOf(sm2.Value!.First().Key) == 255
                   && ValOf(sm2.Value!.First().Value) == serveT,
                "reparsed clear lost the collapsed map");
        }
        else
        {
            var sm2 = TargetMapOf(srcRow2)!;
            Expect(ValOf(sm2.Value!.First(kv => KeyOf(kv.Key) == (byte)reKey).Value) == reNew,
                "reparsed retarget lost");
        }
    }

    private static ArrayPropertyData? TransitionsOf(StructPropertyData nodeSp)
        => nodeSp.Value?.OfType<StructPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions")?.Value
            ?.OfType<ArrayPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions");

    private static MapPropertyData? TargetMapOf(StructPropertyData rowSp)
        => rowSp.Value?.OfType<MapPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_TargetNodes");

    private static int CondIndexOf(StructPropertyData rowSp)
        => rowSp.Value?.OfType<ObjectPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ConditionInstance")?.Value?.Index ?? 0;

    private static string ExportClassNameOf(UAsset asset, int exportIdx)
    {
        var exp = asset.Exports[exportIdx];
        if (exp.ClassIndex != null && exp.ClassIndex.Index < 0)
        {
            int ci = -(exp.ClassIndex.Index + 1);
            if (ci >= 0 && ci < asset.Imports.Count)
            {
                string name = asset.Imports[ci].ObjectName?.Value?.ToString() ?? "";
                if (name.Length > 0) return name;
            }
        }
        string objName = exp.ObjectName?.Value?.ToString() ?? "";
        int cut = objName.IndexOf("_Copy_", StringComparison.Ordinal);
        return cut > 0 ? objName[..cut] : objName;
    }

    private static byte KeyOf(PropertyData k) => k switch
    {
        BytePropertyData b => b.Value,
        IntPropertyData i => (byte)i.Value,
        _ => (byte)255
    };

    private static int ValOf(PropertyData v) => v switch
    {
        IntPropertyData i => i.Value,
        BytePropertyData b => b.Value,
        _ => -1
    };

    /// <summary>First directly-editable leaf on the condition export, or one nested
    /// ObjectProperty hop deep (the same shapes CollectFromTags emits).</summary>
    private static string? FindEditableParam(UAsset asset, List<PropertyData> data)
    {
        static bool Editable(PropertyData p) => p is IntPropertyData or Int8PropertyData
            or Int16PropertyData or Int64PropertyData or UInt16PropertyData or UInt32PropertyData
            or UInt64PropertyData or FloatPropertyData or DoublePropertyData or BoolPropertyData
            or EnumPropertyData or NamePropertyData or StrPropertyData;
        static bool Skipped(string? name) => name is "m_ConditionID" or "m_iSerializeVersion";

        foreach (var p in data)
        {
            string n = p.Name?.Value?.ToString() ?? "";
            if (!Skipped(n) && Editable(p)) return n;
        }
        foreach (var p in data.OfType<ObjectPropertyData>())
        {
            string n = p.Name?.Value?.ToString() ?? "";
            if (p.Value == null || p.Value.Index <= 0 || p.Value.Index > asset.Exports.Count) continue;
            if (asset.Exports[p.Value.Index - 1] is not NormalExport ne || ne.Data == null) continue;
            foreach (var q in ne.Data)
            {
                string qn = q.Name?.Value?.ToString() ?? "";
                if (!Skipped(qn) && Editable(q)) return n + "." + qn;
            }
        }
        return null;
    }

    private static PropertyData? ResolvePath(UAsset asset, List<PropertyData> root, string path)
    {
        var segs = path.Split('.');
        List<PropertyData> cur = root;
        PropertyData? target = null;
        for (int i = 0; i < segs.Length; i++)
        {
            var p = cur.FirstOrDefault(x => x.Name?.Value?.ToString() == segs[i]);
            if (p == null) return null;
            if (i == segs.Length - 1) { target = p; break; }
            if (p is ObjectPropertyData op && op.Value != null && op.Value.Index > 0
                && op.Value.Index <= asset.Exports.Count
                && asset.Exports[op.Value.Index - 1] is NormalExport ne && ne.Data != null)
            {
                cur = ne.Data;
            }
            else if (p is StructPropertyData sp && sp.Value != null)
            {
                cur = sp.Value;
            }
            else return null;
        }
        return target;
    }

    private static string NextParamValue(PropertyData p)
    {
        switch (p)
        {
            case IntPropertyData i: return (i.Value + 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case Int8PropertyData i8: return (i8.Value + 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case Int16PropertyData i16: return (i16.Value + 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case Int64PropertyData i64: return (i64.Value + 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case UInt16PropertyData u16: return (u16.Value + 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case UInt32PropertyData u32: return (u32.Value + 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case UInt64PropertyData u64: return (u64.Value + 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case FloatPropertyData f: return (f.Value + 1.5f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            case DoublePropertyData d: return (d.Value + 1.5).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            case BoolPropertyData b: return (!b.Value).ToString();
            case BytePropertyData by: return ((by.Value + 3) % 256).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case EnumPropertyData en: return (en.Value.Value?.ToString() ?? "Smoke") + "_Smoke";
            case NamePropertyData np: return (np.Value.Value?.ToString() ?? "smoke") + "_smoke";
            case StrPropertyData s: return (s.Value?.Value?.ToString() ?? "") + "_smoke";
            default: throw new Exception($"unsupported param type {p.GetType().Name}");
        }
    }

    private static void AssertTypedValue(PropertyData p, string newVal, string label)
    {
        bool ok = p switch
        {
            IntPropertyData i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) == newVal,
            Int8PropertyData i8 => i8.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) == newVal,
            Int16PropertyData i16 => i16.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) == newVal,
            Int64PropertyData i64 => i64.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) == newVal,
            UInt16PropertyData u16 => u16.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) == newVal,
            UInt32PropertyData u32 => u32.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) == newVal,
            UInt64PropertyData u64 => u64.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) == newVal,
            FloatPropertyData f => f.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) == newVal,
            DoublePropertyData d => d.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) == newVal,
            BoolPropertyData b => b.Value.ToString() == newVal,
            BytePropertyData by => by.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) == newVal,
            EnumPropertyData en => (en.Value.Value?.ToString() ?? "") == newVal,
            NamePropertyData np => (np.Value.Value?.ToString() ?? "") == newVal,
            StrPropertyData s => (s.Value?.Value?.ToString() ?? "") == newVal,
            _ => false
        };
        Expect(ok, $"{label}: value {newVal} not found on {p.GetType().Name} ({p.Name?.Value})");
    }
}
