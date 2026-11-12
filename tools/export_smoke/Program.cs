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
        Run("BpInstanceOuterGate", () => TestBpInstanceOuterGate(seanP1));
        Run("ClearMixupAppend", () => TestClearMixupAppend(seanP1));
        Run("ReplaceAttackWithAction", () => TestReplaceAttackWithAction(seanP1, "replace-atk-action"));
        Run("ReplaceActionWithAttack", () => TestReplaceActionWithAttack(fajarP2, "replace-action-atk"));
        Run("ReplaceEventWithAction", () => TestReplaceEventWithAction(fajarP2, "replace-event-action"));
        Run("MergeIntoRow", () => TestMergeIntoRow(seanP1));
        Run("StartMultiKeyAddRow", () => TestStartMultiKeyAddRow(fajarP2, seanP1));
        Run("MainCharStartEntries", () => TestMainCharStartEntries(mainCharCombo));
        Run("MainCharConditionalRowAdd", () => TestMainCharConditionalRowAdd(mainCharCombo));
        Run("CloneTemplateRules", TestCloneTemplateRules);
        Run("RehydrateKeyModels", TestRehydrateKeyModels);
        Run("AddedTransitionLegacyCondition", TestAddedTransitionLegacyCondition);
        Run("ResolveRedirectFinal", TestResolveRedirectFinal);
        string yangP1 = Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Yang", "_DB", "Phase1", "Yang_P1_Combo.uasset");
        if (File.Exists(yangP1)) Run("RedirectMirror", () => TestRedirectMirror(yangP1));
        Run("AllZeroWarning", () => TestAllZeroWarning(seanP1));
        Run("ChanceRescale", () => TestChanceRescale(seanP1));
        Run("MainCharChancePerInput", () => TestMainCharChancePerInput(mainCharCombo));
        Run("ChanceGateVanillaAppend", TestChanceGateVanillaAppend);
        Run("ChanceGateTypedFullPool", TestChanceGateTypedFullPool);
        Run("ChanceGateOver100", TestChanceGateOver100);
        Run("ChanceGateMainCharPerInput", TestChanceGateMainCharPerInput);
        Run("ChanceGateRedirectStruct", TestChanceGateRedirectStruct);
        Run("ChanceGateCustomNode", TestChanceGateCustomNode);
        Run("ChanceGateDeletedLinkAuto", TestChanceGateDeletedLinkAuto);
        Run("ChanceGateDeletedLinkTyped", TestChanceGateDeletedLinkTyped);
        Run("ChanceGateDeletedCustomExplicit", TestChanceGateDeletedCustomExplicit);
        Run("ChanceGateDeletedCustomAuto", TestChanceGateDeletedCustomAuto);
        Run("DeleteChanceSources", TestDeleteChanceSources);

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
            // Logs live in logs\error-<date>.log (rotated, 7-day history); slice from
            // the last session banner so the tail shows only this run. Falls back to
            // the legacy error.log path for old sessions.
            string dir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            string path = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "error-*.log")
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .FirstOrDefault() ?? ""
                : "";
            if (path.Length == 0)
                path = Path.Combine(Directory.GetCurrentDirectory(), "error.log");
            if (!File.Exists(path)) return "";
            var lines = File.ReadAllLines(path);
            int start = 0;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].Contains("===== session start")) { start = i; break; }
            }
            var tail = lines.Skip(Math.Max(start, lines.Length - 30));
            return $"---- {Path.GetFileName(path)} tail ----\n" + string.Join("\n", tail);
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

    // Multi-key added-row: a conditioned multi-key template ({0:a, 1:b, 2:c}) is copied
    // verbatim - same condition instance, same full key map - and each key's target is
    // patched from the model. A legacy multi-key row with no per-key lists (old save)
    // still collapses to the proven unconditional key-255 recipe.
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
        static List<PropertyData> Props(PropertyData? p)
            => (p as StructPropertyData)?.Value ?? new List<PropertyData>();
        static string PropName(PropertyData? p) => p?.Name?.Value?.ToString() ?? "";
        static int CondRef(PropertyData? row)
            => Props(row).OfType<ObjectPropertyData>()
                .FirstOrDefault(o => PropName(o) == "m_ConditionInstance")?.Value?.Index ?? 0;
        static string InputOf(PropertyData? row)
            => Props(row).OfType<EnumPropertyData>()
                .FirstOrDefault(e => PropName(e) == "m_eInputTransition")
                ?.Value?.Value?.ToString() ?? "";
        static float ProbOf(PropertyData? row)
            => Props(row).OfType<FloatPropertyData>()
                .FirstOrDefault(f => PropName(f) == "m_fProbability")?.Value ?? 0f;
        static MapPropertyData? TargetMap(PropertyData? row)
            => Props(row).OfType<MapPropertyData>()
                .FirstOrDefault(m => PropName(m) == "m_TargetNodes");
        static (List<byte> keys, List<int> targets) MapPairs(PropertyData? row)
        {
            var keys = new List<byte>();
            var targets = new List<int>();
            var map = TargetMap(row);
            if (map?.Value == null) return (keys, targets);
            foreach (var kvp in map.Value)
            {
                keys.Add(kvp.Key is BytePropertyData b ? b.Value
                    : kvp.Key is IntPropertyData i ? (byte)i.Value : (byte)255);
                targets.Add(kvp.Value is IntPropertyData ti ? ti.Value
                    : kvp.Value is BytePropertyData tb ? tb.Value : -1);
            }
            return (keys, targets);
        }

        UAsset? asset = null;
        NormalExport? combo = null;
        ArrayPropertyData? nodes = null;
        ArrayPropertyData? cont = null;
        int entryIdx = -1, rowCount = 0, keyCount = 0, cond0 = 0;
        float prob0 = 100f;
        string label = "", input0 = "";
        var keys0 = new List<byte>();
        var targets0 = new List<int>();

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
                int candidateCond = CondRef(row0);
                if (candidateCond == 0) continue; // only conditioned maps are cloned verbatim
                (asset, combo, nodes, cont) = (a, c, n, ct);
                entryIdx = i;
                rowCount = arr!.Value.Length;
                keyCount = map.Value.Count;
                cond0 = candidateCond;
                label = en.Value?.OfType<StructPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboTransitions")?.Value
                    ?.OfType<NamePropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ParentNodeName")?
                    .Value?.Value?.ToString() ?? "";
                prob0 = row0!.Value.OfType<FloatPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_fProbability")?.Value ?? 100f;
                input0 = InputOf(row0);
                (keys0, targets0) = MapPairs(row0);
                break;
            }
            if (asset != null) break;
        }
        Expect(asset != null && combo != null && nodes != null && cont != null,
            "no conditioned multi-key start entry found in Fajar P2 or Sean P1");

        // Retarget one outcome so the per-key patch is observable.
        int retargetTree = targets0[0] == 0 ? 1 : 0;
        var expectedTargets = new List<int>(targets0);
        expectedTargets[0] = retargetTree;

        // Model: the vanilla multi-key row passes through untouched (empty per-key model
        // skips the export per-key patch). The added conditioned row is the dialog's
        // verbatim copy at 0%, and the legacy row proves old saves still collapse.
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
            Probability = 40f,
            ConditionClass = "BP_IsTargetKnockedDownComboCondition_C",
            KeyByte = keys0[0],
            KeyCount = keyCount,
            TargetNodeId = -1,
            TargetTreeIndex = retargetTree,
            Keys = new List<byte>(keys0),
            KeyTargetTreeIndices = new List<int>(expectedTargets),
            KeyTargetNodeIds = Enumerable.Repeat(-1, keyCount).ToList(),
            KeyTargetNames = Enumerable.Repeat("", keyCount).ToList(),
            TargetSummary = string.Join(", ", keys0.Zip(expectedTargets, (k, v) => $"{k}→{v}"))
        });
        entry.Transitions.Add(new StartTransition
        {
            OriginalIndex = -1,
            Probability = 60f,
            KeyByte = keys0[0],
            KeyCount = keyCount,
            TargetNodeId = -1,
            TargetTreeIndex = 0
        });
        var graph = new ComboGraph
        {
            Nodes = new List<ComboNode> { BaseNode(1, 0) },
            StartEntries = new List<StartEntry> { entry }
        };

        var r = ExportDialog.AppendEnemyAuthoredContent(graph, asset!, combo!, nodes!, ContentRoot);
        Expect(r.startEntries > 0, "start-entry patch reported nothing");

        var outRows = EntryRows(cont!.Value[entryIdx] as StructPropertyData);
        Expect(outRows?.Value?.Length == rowCount + 2,
            $"rows {outRows?.Value?.Length} != {rowCount + 2}");

        var clone = outRows!.Value[rowCount] as StructPropertyData;
        Expect(clone?.Value != null, "conditioned added row missing");
        Expect(CondRef(clone) == cond0,
            $"added row condition {CondRef(clone)} != template condition {cond0}");
        Expect(Math.Abs(ProbOf(clone) - 40f) < 0.001f,
            $"added row probability {ProbOf(clone)} != 40");
        Expect(InputOf(clone) == input0,
            $"added row input '{InputOf(clone)}' != template input '{input0}'");
        var (cloneKeys, cloneTargets) = MapPairs(clone);
        Expect(cloneKeys.SequenceEqual(keys0),
            $"added row keys [{string.Join(",", cloneKeys)}] != template keys [{string.Join(",", keys0)}]");
        Expect(cloneTargets.SequenceEqual(expectedTargets),
            $"added row targets [{string.Join(",", cloneTargets)}] != patch [{string.Join(",", expectedTargets)}]");

        var legacy = outRows.Value[rowCount + 1] as StructPropertyData;
        Expect(legacy?.Value != null, "legacy added row missing");
        Expect(CondRef(legacy) == 0, "legacy added row kept a condition instance");
        var legacyMap = TargetMap(legacy);
        Expect(legacyMap?.Value?.Count == 1, $"legacy added row map has {legacyMap?.Value?.Count} entries, expected 1");
        var (legacyKeys, legacyTargets) = MapPairs(legacy);
        Expect(legacyKeys.Count == 1 && legacyKeys[0] == 255, $"legacy added row key {legacyKeys.FirstOrDefault()} != 255");
        Expect(legacyTargets.Count == 1 && legacyTargets[0] == 0, $"legacy added row target {legacyTargets.FirstOrDefault()} != 0");

        // Vanilla first row untouched: multi-key map still multi-key. (outRows.Value[0]
        // is a row struct, not an entry - read its map directly.)
        var row0Out = outRows.Value[0] as StructPropertyData;
        var (row0Keys, row0Targets) = MapPairs(row0Out);
        Expect(row0Keys.SequenceEqual(keys0) && row0Targets.SequenceEqual(targets0),
            "vanilla multi-key row map was modified");
        Expect(CondRef(row0Out) == cond0, "vanilla multi-key row condition was modified");

        string outPath = WriteAndValidate(asset!, "start-multikey-addrow");
        var a2 = Load(outPath);
        var (c2, _) = ComboOf(a2);
        var rows2 = EntryRows(StartContainer(c2)?.Value?[entryIdx] as StructPropertyData);
        Expect(rows2?.Value?.Length == rowCount + 2, $"reparse rows {rows2?.Value?.Length} != {rowCount + 2}");
        var clone2 = rows2!.Value[rowCount] as StructPropertyData;
        var (clone2Keys, clone2Targets) = MapPairs(clone2);
        Expect(CondRef(clone2) == cond0, "reparse lost the added row condition instance");
        Expect(clone2Keys.SequenceEqual(keys0) && clone2Targets.SequenceEqual(expectedTargets),
            "reparse lost the added row key map or targets");
        var legacy2 = rows2.Value[rowCount + 1] as StructPropertyData;
        var (legacy2Keys, legacy2Targets) = MapPairs(legacy2);
        Expect(CondRef(legacy2) == 0, "reparse kept a condition on the legacy row");
        Expect(legacy2Keys.Count == 1 && legacy2Keys[0] == 255
            && legacy2Targets.Count == 1 && legacy2Targets[0] == 0,
            "reparse lost the legacy unconditional row shape");
    }

    /// <summary>
    /// MainChar start entries round trip. Player trees bind the container to
    /// BP_MainCharComboStartStates instead of BP_AIAttackTIcketNames; the writer must
    /// patch an existing state in place and, for a new entry, keep the cloned template's
    /// MC m_Enum while rewriting m_Value to the requested state.
    /// </summary>
    private static void TestMainCharStartEntries(string targetFile)
    {
        static ArrayPropertyData? StartContainer(NormalExport c)
            => c.Data?.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboStartNodesContainer");
        static ArrayPropertyData? EntryRows(StructPropertyData? en)
            => en?.Value?.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboTransitions")?.Value
                ?.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions");
        static string NameOf(StructPropertyData? en, string prop)
            => en?.Value?.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboTransitions")?.Value
                ?.OfType<NamePropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == prop)
                ?.Value?.Value?.ToString() ?? "";
        static string EnumValueOf(StructPropertyData en)
            => en.Value?.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_EnumState")?.Value
                ?.OfType<NamePropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Value")
                ?.Value?.Value?.ToString() ?? "";
        static ObjectPropertyData? EnumRefOf(StructPropertyData en)
            => en.Value?.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_EnumState")?.Value
                ?.OfType<ObjectPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Enum");
        static string InputOf(PropertyData? row)
            => (row as StructPropertyData)?.Value?.OfType<EnumPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_eInputTransition")
                ?.Value?.Value?.ToString() ?? "";

        var asset = Load(targetFile);
        var (combo, nodes) = ComboOf(asset);
        var cont = StartContainer(combo);
        Expect(cont?.Value?.Length > 0, "MainChar tree has no start entries");
        int baseEntries = cont!.Value!.Length;

        // "KnockedDown" ships two plain single-key rows with probability 100 and target 0.
        int entryIdx = -1, rowCount = 0;
        string label = "", enumValue = "";
        for (int i = 0; i < baseEntries; i++)
        {
            if (cont.Value[i] is not StructPropertyData en) continue;
            if (NameOf(en, "m_ParentNodeName") != "KnockedDown") continue;
            var rows = EntryRows(en);
            if (rows?.Value?.Length is not > 0) continue;
            entryIdx = i;
            rowCount = rows.Value.Length;
            label = "KnockedDown";
            enumValue = EnumValueOf(en);
            break;
        }
        Expect(entryIdx >= 0, "MainChar tree has no 'KnockedDown' start state");
        Expect(enumValue.StartsWith("BP_MainCharComboStartStates::", StringComparison.Ordinal),
            $"start state enum '{enumValue}' is not BP_MainCharComboStartStates");
        var mcEnumRefIndex = EnumRefOf((StructPropertyData)cont.Value[entryIdx]!)?.Value?.Index ?? 0;
        Expect(mcEnumRefIndex != 0, "start state m_Enum is a null ref");

        // Vanilla input snapshot: row 1 must keep exactly this value (untouched rows
        // are never rewritten), while row 0 gets a different input on purpose.
        var vanillaRows = EntryRows(cont.Value[entryIdx] as StructPropertyData);
        string vanillaRow1Input = InputOf(vanillaRows?.Value?[1]);

        var entry = new StartEntry { VanillaIndex = entryIdx, Label = label, TicketEnumValue = enumValue };
        entry.Transitions.Add(new StartTransition
        {
            OriginalIndex = 0,
            Probability = 55f,
            OriginalProbability = 100f,
            // Input-only edit: proves the row's m_eInputTransition is rewritten in place.
            InputEnum = "Special",
            OriginalInputEnum = "",
            TargetNodeId = 0,
            OriginalTargetNodeId = -1,
            KeyByte = 255,
            KeyCount = 1
        });
        entry.Transitions.Add(new StartTransition
        {
            OriginalIndex = 1,
            Probability = 100f,
            OriginalProbability = 100f,
            InputEnum = "",
            OriginalInputEnum = "",
            TargetNodeId = 0,
            OriginalTargetNodeId = -1,
            KeyByte = 255,
            KeyCount = 1
        });
        // Added row: carries its own input (inherited from row 1 in the dialog).
        entry.Transitions.Add(new StartTransition
        {
            OriginalIndex = -1,
            Probability = 0f,
            InputEnum = "HeavyHold",
            KeyByte = 255,
            KeyCount = 1,
            TargetNodeId = 0,
            TargetTreeIndex = 0,
            TargetName = "Base0"
        });

        // New entry: clone lands from the container template, so this also proves the
        // exported m_Enum keeps pointing at the player start-state enum.
        var added = new StartEntry
        {
            VanillaIndex = -1,
            Label = "SmokeState",
            TicketEnumValue = "BP_MainCharComboStartStates::NewEnumerator99",
            PhaseName = "None",
            MCDominationFlags = 15
        };
        added.Transitions.Add(new StartTransition
        {
            OriginalIndex = -1,
            Probability = 100f,
            // New-entry rows write the model's input (the cloned template row's input
            // must not leak onto every row).
            InputEnum = "Heavy",
            KeyByte = 255,
            KeyCount = 1,
            TargetNodeId = 0,
            TargetTreeIndex = 0,
            TargetName = "Base0"
        });

        var graph = new ComboGraph
        {
            Nodes = new List<ComboNode> { BaseNode(0, 0) },
            StartEntries = new List<StartEntry> { entry, added }
        };

        var r = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodes, ContentRoot);
        Expect(r.startEntries > 0, "start-entry patch reported nothing");
        Expect(cont.Value.Length == baseEntries + 1,
            $"container has {cont.Value.Length} entries, expected {baseEntries + 1}");

        // Existing state: probability and input rewritten in place, the untouched row
        // keeps its exact vanilla input, and the added row lands at the end with its
        // own input.
        var patchedRows = EntryRows(cont.Value[entryIdx] as StructPropertyData);
        Expect(patchedRows?.Value?.Length == rowCount + 1,
            $"patched state rows {patchedRows?.Value?.Length} != {rowCount + 1}");
        var row0Prob = (patchedRows!.Value![0] as StructPropertyData)?.Value
            .OfType<FloatPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_fProbability");
        Expect(row0Prob != null && Math.Abs(row0Prob.Value - 55f) < 0.001f,
            $"patched probability {row0Prob?.Value.ToString() ?? "null"} != 55");
        Expect(InputOf(patchedRows.Value![0]) == "EComboTransition::Special",
            $"patched row 1 input '{InputOf(patchedRows.Value![0])}' != 'EComboTransition::Special'");
        Expect(InputOf(patchedRows.Value![1]) == vanillaRow1Input,
            $"untouched row 2 input '{InputOf(patchedRows.Value![1])}' != vanilla '{vanillaRow1Input}'");
        Expect(InputOf(patchedRows.Value![^1]) == "EComboTransition::HeavyHold",
            $"added row input '{InputOf(patchedRows.Value![^1])}' != 'EComboTransition::HeavyHold'");

        // New state: MC enum preserved, m_Value/name written from the model.
        var newEntry = cont.Value[^1] as StructPropertyData;
        Expect(newEntry != null, "new start entry missing");
        Expect(((ObjectPropertyData?)EnumRefOf(newEntry!))?.Value?.Index == mcEnumRefIndex,
            "new entry m_Enum no longer points at BP_MainCharComboStartStates");
        Expect(EnumValueOf(newEntry!) == "BP_MainCharComboStartStates::NewEnumerator99",
            $"new entry m_Value '{EnumValueOf(newEntry!)}'");
        Expect(NameOf(newEntry!, "m_ParentNodeName") == "SmokeState",
            $"new entry label '{NameOf(newEntry!, "m_ParentNodeName")}'");
        var newRows = EntryRows(newEntry);
        Expect(newRows?.Value?.Length == 1 && InputOf(newRows.Value![0]) == "EComboTransition::Heavy",
            $"new entry row input '{InputOf(newRows?.Value?.FirstOrDefault())}' != 'EComboTransition::Heavy'");

        string outPath = WriteAndValidate(asset, "mainchar-start-entries");
        var a2 = Load(outPath);
        var (c2, _) = ComboOf(a2);
        var cont2 = StartContainer(c2);
        Expect(cont2?.Value?.Length == baseEntries + 1,
            $"reparse container {cont2?.Value?.Length} != {baseEntries + 1}");
        var newEntry2 = cont2!.Value![^1] as StructPropertyData;
        Expect(newEntry2 != null && EnumValueOf(newEntry2) == "BP_MainCharComboStartStates::NewEnumerator99",
            "reparse lost the new MC start state");
        Expect(EnumRefOf(newEntry2!)?.Value?.Index == mcEnumRefIndex,
            "reparse lost the MC enum reference on the new entry");
    }

    // Bug 1 regression: the MainChar "Default" state routes each input through a
    // condition (BP_TargetIsParryVictim_C) on single-key rows, and "Idle" does the same
    // with BP_IsInInvertAttack_C. Those rows used to disable "+ Add Transition" outright
    // because the gate only accepted key 255 or multi-key rows. A new row must clone the
    // template row verbatim - same key, same condition instance, inherited input - and
    // only probability + target are patched.
    private static void TestMainCharConditionalRowAdd(string targetFile)
    {
        static ArrayPropertyData? StartContainer(NormalExport c)
            => c.Data?.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboStartNodesContainer");
        static ArrayPropertyData? EntryRows(StructPropertyData? en)
            => en?.Value?.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboTransitions")?.Value
                ?.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions");
        static string NameOf(StructPropertyData? en, string prop)
            => en?.Value?.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboTransitions")?.Value
                ?.OfType<NamePropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == prop)
                ?.Value?.Value?.ToString() ?? "";
        static List<PropertyData> Props(PropertyData? p)
            => (p as StructPropertyData)?.Value ?? new List<PropertyData>();
        static float ProbOf(PropertyData? p)
            => Props(p).OfType<FloatPropertyData>()
                .FirstOrDefault(f => f.Name?.Value?.ToString() == "m_fProbability")?.Value ?? 0f;
        static string InputOf(PropertyData? p)
            => Props(p).OfType<EnumPropertyData>()
                .FirstOrDefault(f => f.Name?.Value?.ToString() == "m_eInputTransition")
                ?.Value?.Value?.ToString() ?? "";
        static (byte key, int target) MapOf(PropertyData? p)
        {
            var map = Props(p).OfType<MapPropertyData>()
                .FirstOrDefault(m => m.Name?.Value?.ToString() == "m_TargetNodes");
            if (map?.Value == null || map.Value.Count == 0) return ((byte)255, -1);
            // TMap enumerates KeyValuePair<PropertyData,PropertyData>; its int indexer is
            // not positional, so take the first pair from enumeration.
            foreach (var kv in map.Value)
            {
                byte key = kv.Key is BytePropertyData b ? b.Value
                    : kv.Key is IntPropertyData i ? (byte)i.Value : (byte)255;
                int target = kv.Value is IntPropertyData ti ? ti.Value : -1;
                return (key, target);
            }
            return ((byte)255, -1);
        }
        static int CondOf(PropertyData? p)
            => Props(p).OfType<ObjectPropertyData>()
                .FirstOrDefault(o => o.Name?.Value?.ToString() == "m_ConditionInstance")
                ?.Value?.Index ?? 0;

        var asset = Load(targetFile);
        var (combo, nodes) = ComboOf(asset);
        var cont = StartContainer(combo);
        Expect(cont?.Value?.Length > 0, "MainChar tree has no start entries");

        int entryIdx = -1;
        for (int i = 0; i < cont!.Value!.Length; i++)
        {
            if (cont.Value[i] is StructPropertyData en && NameOf(en, "m_ParentNodeName") == "Default")
            {
                entryIdx = i;
                break;
            }
        }
        Expect(entryIdx >= 0, "MainChar tree has no 'Default' start state");
        var entryStruct = (StructPropertyData)cont.Value[entryIdx]!;
        var vanillaRows = EntryRows(entryStruct);
        int rowCount = vanillaRows?.Value?.Length ?? 0;
        Expect(rowCount > 0, "'Default' has no vanilla rows");

        var row0 = vanillaRows!.Value![0];
        var (vKey, _) = MapOf(row0);
        int vCond = CondOf(row0);
        Expect(vKey != 255 && vCond != 0,
            $"'Default' row 1 is not condition-routed (key {vKey}, condition {vCond})");

        var entry = new StartEntry { VanillaIndex = entryIdx, Label = "Default" };
        // Vanilla rows keep their keys/targets; row 1 splits its 100% with the new row so
        // the Light trigger group still totals 100 (the player per-group rule).
        for (int i = 0; i < rowCount; i++)
        {
            var (k, tgt) = MapOf(vanillaRows.Value[i]);
            float p = ProbOf(vanillaRows.Value[i]);
            entry.Transitions.Add(new StartTransition
            {
                OriginalIndex = i,
                Probability = i == 0 ? 60f : p,
                OriginalProbability = p,
                TargetNodeId = -1,
                TargetTreeIndex = tgt,
                OriginalTargetTreeIndex = tgt,
                KeyByte = k,
                KeyCount = 1,
                Keys = new List<byte> { k },
                KeyTargetTreeIndices = new List<int> { tgt },
                OriginalKeyTargetTreeIndices = new List<int> { tgt }
            });
        }
        // The row the dialog's + button now adds: same key/condition as the template
        // (row 1), new target, 40% of the trigger group.
        entry.Transitions.Add(new StartTransition
        {
            OriginalIndex = -1,
            Probability = 40f,
            TargetNodeId = 0,
            TargetTreeIndex = 0,
            KeyByte = vKey,
            KeyCount = 1
        });

        var graph = new ComboGraph
        {
            Nodes = new List<ComboNode> { BaseNode(0, 0) },
            StartEntries = new List<StartEntry> { entry }
        };
        var r = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodes, ContentRoot);
        Expect(r.startEntries > 0, "conditional start row patch reported nothing");

        var patched = EntryRows((StructPropertyData)cont.Value[entryIdx]!);
        Expect(patched?.Value?.Length == rowCount + 1,
            $"patched rows {patched?.Value?.Length} != {rowCount + 1}");
        Expect(Math.Abs(ProbOf(patched!.Value![0]) - 60f) < 0.001f,
            $"row 1 probability {ProbOf(patched.Value![0])} != 60");
        var addedRow = patched.Value[^1];
        var (aKey, aTarget) = MapOf(addedRow);
        Expect(aKey == vKey, $"added row key {aKey} != template key {vKey} (collapsed to unconditional?)");
        Expect(aTarget == 0, $"added row target {aTarget} != 0");
        Expect(CondOf(addedRow) == vCond,
            $"added row condition {CondOf(addedRow)} != template condition {vCond}");
        Expect(InputOf(addedRow) == InputOf(row0),
            $"added row input '{InputOf(addedRow)}' != template input '{InputOf(row0)}'");
        Expect(Math.Abs(ProbOf(addedRow) - 40f) < 0.001f,
            $"added row probability {ProbOf(addedRow)} != 40");

        // Round-trip: the written asset keeps the same shape.
        string outPath = WriteAndValidate(asset, "mainchar-conditional-row");
        var a2 = Load(outPath);
        var (c2, _) = ComboOf(a2);
        var cont2 = StartContainer(c2);
        var patched2 = EntryRows(cont2?.Value?[entryIdx] as StructPropertyData);
        Expect(patched2?.Value?.Length == rowCount + 1,
            $"reparse rows {patched2?.Value?.Length} != {rowCount + 1}");
        var added2 = patched2!.Value![^1];
        var (rKey, rTarget) = MapOf(added2);
        Expect(rKey == vKey && rTarget == 0,
            $"reparse lost the added row key/target ({rKey}/{rTarget})");
        Expect(CondOf(added2) == vCond, "reparse lost the added row condition instance");
    }

    // Pure-model rules shared by the "+ Add Transition" gate and the exporter.
    private static void TestCloneTemplateRules()
    {
        // Idle-shaped entry: two conditioned single-key rows, one per input.
        var idle = new StartEntry { VanillaIndex = 6, Label = "Idle" };
        idle.Transitions.Add(new StartTransition
        {
            OriginalIndex = 0,
            InputEnum = "Light",
            KeyByte = 0,
            KeyCount = 1,
            ConditionClass = "BP_IsInInvertAttack_C",
            Probability = 100,
            OriginalProbability = 100
        });
        idle.Transitions.Add(new StartTransition
        {
            OriginalIndex = 1,
            InputEnum = "Heavy",
            KeyByte = 1,
            KeyCount = 1,
            ConditionClass = "BP_IsInInvertAttack_C",
            Probability = 100,
            OriginalProbability = 100
        });
        var template = idle.CloneTemplateRow();
        Expect(template != null && template.OriginalIndex == 0, "template is not vanilla row 1");
        Expect(StartEntry.CanCloneTemplate(template!), "condition-routed template must be cloneable");
        Expect(template!.ProbabilityGroupKey != idle.Transitions[1].ProbabilityGroupKey,
            "Light and Heavy rows must be separate probability groups");
        var split = new StartTransition
        {
            OriginalIndex = 2,
            InputEnum = "Light",
            KeyByte = 3,
            ConditionClass = "BP_TargetIsParryVictim_C"
        };
        Expect(split.ProbabilityGroupKey == template.ProbabilityGroupKey,
            "same input must share one pool regardless of key/condition (vanilla node 1)");

        // A bare single-key row (no condition) stays blocked.
        var bare = new StartTransition { OriginalIndex = 0, KeyByte = 3, KeyCount = 1, InputEnum = "Light" };
        Expect(!StartEntry.CanCloneTemplate(bare), "bare keyed row without condition must not be cloneable");

        // Multi-key templates are cloneable: the dialog copies their condition + key map
        // verbatim, and export only collapses legacy rows that lack a per-key model.
        var multi = new StartTransition { OriginalIndex = 0, KeyByte = 0, KeyCount = 3 };
        Expect(StartEntry.CanCloneTemplate(multi), "multi-key template must be cloneable");

        // Deleting vanilla row 1 moves the template to the next live vanilla row, never
        // to an added row that happens to sit earlier in the list.
        var deleted = new StartEntry();
        deleted.Transitions.Add(new StartTransition { OriginalIndex = 0, Removed = true });
        deleted.Transitions.Add(new StartTransition { OriginalIndex = -1, Probability = 50 });
        deleted.Transitions.Add(new StartTransition
        {
            OriginalIndex = 2,
            KeyByte = 0,
            KeyCount = 1,
            ConditionClass = "BP_TargetIsParryVictim_C"
        });
        Expect(deleted.CloneTemplateRow()?.OriginalIndex == 2, "deleted row 1 must fall through to row 3");
    }

    // Old saves (pre per-key editing) carry KeyCount + TargetSummary but empty
    // Keys/KeyTarget* lists - RehydrateStartEntryKeyModels rebuilds them from the
    // summary and the graph's TreeIndex-stable nodes. Saves older than the per-row
    // condition change also lost the row's ConditionClass (only the entry kept it),
    // which the same pass restores for rows that are conditioned by construction.
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
        var entry = new StartEntry
        {
            VanillaIndex = 1,
            Label = "Parry",
            ConditionClass = "BP_TargetIsParryVictim_C"
        };

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

        // Unconditional row inside a conditioned entry: must NOT inherit the class.
        var unconditional = new StartTransition
        {
            OriginalIndex = 4,
            KeyCount = 1,
            KeyByte = 255,
            Probability = 0
        };
        entry.Transitions.Add(unconditional);

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

        // Per-row condition backfill: conditioned rows inherit the entry class, the
        // unconditional key-255 row stays unconditional.
        Expect(legacy.ConditionClass == entry.ConditionClass, "legacy row condition not backfilled");
        Expect(condensed.ConditionClass == entry.ConditionClass, "condensed row condition not backfilled");
        Expect(fresh.ConditionClass == entry.ConditionClass, "hydrated legacy row condition not backfilled");
        Expect(unconditional.ConditionClass == "", "key-255 row wrongly inherited the entry condition");

        // Idempotent: a second pass changes nothing.
        graph.RehydrateStartEntryKeyModels();
        Expect(legacy.Keys.Count == 2 && legacy.KeyTargetNodeIds.SequenceEqual(new[] { 1, 2 }),
            "second rehydrate pass changed a hydrated row");
        Expect(legacy.ConditionClass == entry.ConditionClass, "second pass changed the backfilled condition");
    }

    // "+ Add Transition" on a project saved before per-row conditions: the template row
    // has the full key model but no ConditionClass of its own, so the entry's class is
    // the fallback. The added row must still duplicate the whole map at 0%.
    private static void TestAddedTransitionLegacyCondition()
    {
        const string entryCond = "BP_IsTargetKnockedDownComboCondition_C";
        var template = new StartTransition
        {
            OriginalIndex = 0,
            Probability = 100,
            OriginalProbability = 100,
            KeyByte = 0,
            KeyCount = 2,
            TargetNodeId = 10,
            TargetTreeIndex = 0,
            TargetName = "DownAttack",
            InputEnum = "None",
            // Legacy save: row class lost, entry kept it.
            ConditionClass = "",
            Keys = new List<byte> { 0, 1 },
            KeyTargetTreeIndices = new List<int> { 0, 1 },
            KeyTargetNodeIds = new List<int> { 10, 11 },
            KeyTargetNames = new List<string> { "DownAttack", "Hook" },
            TargetSummary = "0→0, 1→1"
        };

        var added = StartEntry.CreateAddedTransition(template, entryCond);
        Expect(added.OriginalIndex == -1, "added row must be new");
        Expect(Math.Abs(added.Probability) < 0.001f, "added row must start at 0%");
        Expect(added.ConditionClass == entryCond, "added row lost the entry condition fallback");
        Expect(added.KeyByte == 0 && added.KeyCount == 2, "added row did not keep the 2-key map");
        Expect(added.Keys.SequenceEqual(template.Keys), "added row keys differ from template");
        Expect(added.KeyTargetTreeIndices.SequenceEqual(template.KeyTargetTreeIndices),
            "added row targets differ from template");
        Expect(added.KeyTargetNodeIds.SequenceEqual(template.KeyTargetNodeIds),
            "added row node ids differ from template");
        Expect(added.KeyTargetNames.SequenceEqual(template.KeyTargetNames),
            "added row node names differ from template");
        Expect(added.TargetSummary == template.TargetSummary, "added row summary differs from template");
        Expect(added.TargetNodeId == template.TargetNodeId
            && added.TargetTreeIndex == template.TargetTreeIndex
            && added.TargetName == template.TargetName,
            "added row top-level target differs from template");
        Expect(added.InputEnum == template.InputEnum, "added row input differs from template");
        Expect(added.IsModified(), "added row must read as modified");

        // A fresh row keeps its own class even when the entry says something else.
        var fresh = template.Clone();
        fresh.ConditionClass = "BP_IsInInvertAttack_C";
        var freshAdded = StartEntry.CreateAddedTransition(fresh, entryCond);
        Expect(freshAdded.ConditionClass == "BP_IsInInvertAttack_C",
            "fresh row class must win over the entry fallback");

        // Legacy conditioned single-key row: copy the one outcome, not key 255.
        var single = new StartTransition
        {
            OriginalIndex = 0,
            Probability = 100,
            KeyByte = 0,
            KeyCount = 1,
            TargetNodeId = 5,
            TargetTreeIndex = 5,
            TargetName = "",
            InputEnum = "None",
            ConditionClass = "",
            TargetSummary = ""
        };
        var singleAdded = StartEntry.CreateAddedTransition(single, entryCond);
        Expect(singleAdded.ConditionClass == entryCond && singleAdded.KeyByte == 0
            && singleAdded.KeyCount == 1, "legacy single-key conditioned row collapsed");
        Expect(singleAdded.TargetNodeId == 5 && singleAdded.TargetTreeIndex == 5,
            "legacy single-key row lost its target");
        Expect(StartEntry.CanCloneTemplate(single, entryCond), "entry-aware gate must allow the legacy row");
        Expect(!StartEntry.CanCloneTemplate(single), "single-arg gate lost its old contract");

        // Legacy multi-key row without a per-key model: export cannot write the map, so
        // the add must still collapse to the unconditional key-255 recipe.
        var noModel = template.Clone();
        noModel.Keys.Clear();
        noModel.KeyTargetTreeIndices.Clear();
        noModel.KeyTargetNodeIds.Clear();
        noModel.KeyTargetNames.Clear();
        var collapsed = StartEntry.CreateAddedTransition(noModel, entryCond);
        Expect(collapsed.ConditionClass == "" && collapsed.KeyByte == 255 && collapsed.KeyCount == 1,
            "legacy model-less multi-key row did not collapse to key 255");
        Expect(collapsed.TargetNodeId == -1 && collapsed.TargetTreeIndex == -1,
            "collapsed row kept a target from the template");

        // Unconditional template stays unconditional even inside a conditioned entry.
        var unconditional = new StartTransition
        {
            OriginalIndex = 0,
            Probability = 100,
            KeyByte = 255,
            KeyCount = 1,
            TargetNodeId = 4,
            TargetTreeIndex = 4,
            TargetName = "Hook",
            InputEnum = "None"
        };
        var unconditionalAdded = StartEntry.CreateAddedTransition(unconditional, entryCond);
        Expect(unconditionalAdded.ConditionClass == "" && unconditionalAdded.KeyByte == 255
            && unconditionalAdded.KeyCount == 1,
            "unconditional template inherited a condition");
        Expect(unconditionalAdded.TargetNodeId == -1,
            "unconditional clone must not seed a single-key target");
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

    /// <summary>0-based export index of the combo object (the export carrying m_Nodes).</summary>
    private static int ComboExportIndex(UAsset asset)
    {
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            if (asset.Exports[i] is not NormalExport ne || ne.Data == null) continue;
            if (ne.Data.OfType<ArrayPropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_Nodes"))
                return i;
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
        // The clone's outer must be the dest combo object - a package outer (0) is
        // what the game deadlocks on ("Missing Dependency ... waiting for serialization").
        int mainIdx = ComboExportIndex(asset);
        Expect(cloneExp.OuterIndex != null && cloneExp.OuterIndex.Index == mainIdx + 1,
            $"in-memory clone outer {cloneExp.OuterIndex?.Index} != combo export {mainIdx + 1}");

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
        int mainIdx2 = ComboExportIndex(a2);
        Expect(cloneExp2.OuterIndex != null && cloneExp2.OuterIndex.Index == mainIdx2 + 1,
            $"reparsed clone outer {cloneExp2.OuterIndex?.Index} != combo export {mainIdx2 + 1}");
        AssertRekeyed("reparsed share row", rowC2, origTC);
        AssertRekeyed("reparsed clone row", rowD2, origTD);
    }

    /// <summary>
    /// Export gate: a BP instance (condition/action/event - class import
    /// BlueprintGeneratedClass) with a package outer (0) is what the game deadlocks on
    /// ("Missing Dependency ... waiting for serialization"), so the validator must fail
    /// and remove such a staged file, while a clean file passes untouched.
    /// </summary>
    private static void TestBpInstanceOuterGate(string file)
    {
        var asset = Load(file);
        int idx = -1;
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            var e = asset.Exports[i];
            if (e.ClassIndex == null || e.ClassIndex.Index >= 0) continue;
            var imp = asset.Imports[-e.ClassIndex.Index - 1];
            if (string.Equals(imp.ClassName?.Value?.ToString(), "BlueprintGeneratedClass", StringComparison.Ordinal)
                && e.OuterIndex != null && e.OuterIndex.Index > 0)
            {
                idx = i;
                break;
            }
        }
        Expect(idx >= 0, "no BP instance export in fixture");

        string good = Path.Combine(OutDir, "bp-outer-good.uasset");
        try { File.Delete(good); } catch { }
        try { File.Delete(Path.ChangeExtension(good, ".uexp")); } catch { }
        asset.Write(good);
        Expect(ComboWriteValidator.Validate(good, asset, "bp-outer-good"),
            "validator rejected a clean file");

        var saved = asset.Exports[idx].OuterIndex;
        asset.Exports[idx].OuterIndex = new FPackageIndex(0);
        string bad = Path.Combine(OutDir, "bp-outer-bad.uasset");
        try { File.Delete(bad); } catch { }
        try { File.Delete(Path.ChangeExtension(bad, ".uexp")); } catch { }
        asset.Write(bad);
        Expect(!ComboWriteValidator.Validate(bad, asset, "bp-outer-bad"),
            "validator accepted a package-outer BP instance");
        Expect(!File.Exists(bad), "validator left the corrupt file on disk");
        asset.Exports[idx].OuterIndex = saved;
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

    /// <summary>
    /// The Yang dead-end class: vanilla keeps an attack's continuation rows on its
    /// owning redirect struct (BellyPalm's rows live on the BellyPalm owner, not the
    /// attack), and the engine continues from the entered struct - falling back to
    /// the redirect target's rows when the entered struct is empty. A custom
    /// redirect into a vanilla-owned attack appended with an empty struct therefore
    /// stopped the combo in-game. The mirror pass must copy the unique owner's rows
    /// onto the appended struct; a redirect into an attack that carries its own
    /// rows must stay empty (engine fallback already covers it).
    /// </summary>
    private static void TestRedirectMirror(string targetFile)
    {
        var asset = Load(targetFile);
        var (combo, nodes) = ComboOf(asset);
        int baseCount = nodes.Value.Length;

        int? RedirectOf(StructPropertyData sp) => sp.Value?.OfType<IntPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_NodeRedirect")?.Value;
        List<List<int>> RowTargets(ArrayPropertyData? rows) => rows?.Value?
            .OfType<StructPropertyData>()
            .Select(r => TargetMapOf(r)?.Value?.Select(kvp => kvp.Value switch
            {
                IntPropertyData ip => ip.Value,
                BytePropertyData bp => bp.Value,
                _ => -1
            }).ToList() ?? new List<int>())
            .ToList() ?? new List<List<int>>();

        // Find an empty attack struct (non-redirect, no rows) whose only
        // rows-carrying redirect is unique - the Yang BellyPalm/ShinKick pattern.
        int attackIdx = -1, ownerIdx = -1;
        for (int i = 0; i < baseCount; i++)
        {
            if (NodeStructAt(nodes, i) is not StructPropertyData sp) continue;
            if (RedirectOf(sp) is int ownRedir && ownRedir >= 0) continue;   // attacks only
            var rows = TransitionsOf(sp);
            if (rows?.Value == null || rows.Value.Length > 0) continue;
            var owners = new List<int>();
            for (int j = 0; j < baseCount; j++)
            {
                if (j == i || NodeStructAt(nodes, j) is not StructPropertyData op) continue;
                if (RedirectOf(op) != i) continue;
                var orows = TransitionsOf(op);
                if (orows?.Value == null || orows.Value.Length == 0) continue;
                owners.Add(j);
            }
            if (owners.Count == 1) { attackIdx = i; ownerIdx = owners[0]; break; }
        }
        Expect(attackIdx >= 0, "no (empty attack, unique owner) pair in tree - mirror case not covered");

        var ownerRows = TransitionsOf(NodeStructAt(nodes, ownerIdx))!;
        int ownerRowCount = ownerRows.Value!.Length;
        var ownerTargets = RowTargets(ownerRows);

        // Case 1: custom redirect into the owned attack - the mirror fills it.
        var redir1 = new ComboNode
        {
            Id = 100,
            TreeIndex = -1,
            Kind = ComboNodeKind.UserCreated,
            IsRedirect = true,
            DisplayName = "SmokeMirror",
            RedirectTargetTreeIndex = -1,
            ResolvedRedirectNodeId = 1
        };
        var graph1 = new ComboGraph { Nodes = new List<ComboNode> { BaseNode(1, attackIdx), redir1 } };
        var r1 = ExportDialog.AppendEnemyAuthoredContent(graph1, asset, combo, nodes, ContentRoot);
        Expect(r1.nodes == 1, $"mirror case appended {r1.nodes} nodes, expected 1");
        var mrows = TransitionsOf(NodeStructAt(nodes, baseCount));
        Expect(mrows?.Value != null && mrows.Value.Length == ownerRowCount,
            $"mirrored rows {mrows?.Value?.Length ?? -1} != owner rows {ownerRowCount}");
        var mTargets = RowTargets(mrows);
        for (int i = 0; i < ownerRowCount; i++)
            Expect(mTargets[i].SequenceEqual(ownerTargets[i]),
                $"mirrored row {i} targets [{string.Join(",", mTargets[i])}] != owner [{string.Join(",", ownerTargets[i])}]");
        string outPath = WriteAndValidate(asset, "redirect-mirror");
        var a2 = Load(outPath);
        var (_, n2) = ComboOf(a2);
        Expect((TransitionsOf(NodeStructAt(n2, baseCount))?.Value?.Length ?? -1) == ownerRowCount,
            "mirrored rows lost on reparse");

        // Case 2: redirect into an attack that carries its own rows - stays empty,
        // the engine falls back to the target's rows, so no mirror is needed.
        int selfRowsIdx = -1;
        for (int i = 0; i < baseCount; i++)
        {
            if (NodeStructAt(nodes, i) is not StructPropertyData sp) continue;
            if (RedirectOf(sp) is int rd && rd >= 0) continue;
            bool isAttack = sp.Value?.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos")?.Value
                ?.OfType<NamePropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_Attacks"
                       && p.Value?.Value?.ToString() is string ap && ap != "None") == true;
            var rows = TransitionsOf(sp);
            if (isAttack && rows?.Value != null && rows.Value.Length > 0) { selfRowsIdx = i; break; }
        }
        Expect(selfRowsIdx >= 0, "no attack-with-own-rows found");
        var redir2 = new ComboNode
        {
            Id = 101,
            TreeIndex = -1,
            Kind = ComboNodeKind.UserCreated,
            IsRedirect = true,
            DisplayName = "SmokeFallback",
            RedirectTargetTreeIndex = -1,
            ResolvedRedirectNodeId = 2
        };
        var graph2 = new ComboGraph { Nodes = new List<ComboNode> { BaseNode(2, selfRowsIdx), redir2 } };
        var r2 = ExportDialog.AppendEnemyAuthoredContent(graph2, asset, combo, nodes, ContentRoot);
        Expect(r2.nodes == 1, $"fallback case appended {r2.nodes} nodes, expected 1");
        var frows = TransitionsOf(NodeStructAt(nodes, baseCount + 1));
        Expect(frows?.Value != null && frows.Value.Length == 0,
            $"fallback redirect should stay empty, has {frows?.Value?.Length ?? -1} rows");
    }

    /// <summary>Fully-dead entry: a struct whose live rows all ship at 0% rolls
    /// nothing in-game (rebalance skips a sum-0 set), so the export warns once per
    /// struct. Partial zeros - the normal "disable this branch" workflow - stay
    /// silent.</summary>
    private static void TestAllZeroWarning(string targetFile)
    {
        // All-zero: every row of a struct overridden to 0% -> one warning.
        var asset = Load(targetFile);
        var (combo, nodes) = ComboOf(asset);
        int idx = -1, rows = 0;
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (NodeStructAt(nodes, i) is not StructPropertyData sp) continue;
            var t = TransitionsOf(sp);
            if (t?.Value == null || t.Value.Length == 0) continue;
            idx = i; rows = t.Value.Length; break;
        }
        Expect(idx >= 0, "no rows-carrying struct found");
        var graph = new ComboGraph { Nodes = new List<ComboNode> { BaseNode(1, idx) } };
        graph.TreeRowProbabilityOverrides[idx] =
            Enumerable.Range(0, rows).ToDictionary(r => r, _ => 0f);
        int before = ErrorLog.Warnings;
        _ = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodes, ContentRoot);
        Expect(ErrorLog.Warnings > before, "all-0% struct did not warn");

        // Partial zeros: one row at 0, the rest untouched -> no warning.
        var asset2 = Load(targetFile);
        var (combo2, nodes2) = ComboOf(asset2);
        int idx2 = -1;
        for (int i = 0; i < nodes2.Value.Length; i++)
        {
            if (NodeStructAt(nodes2, i) is not StructPropertyData sp) continue;
            var t = TransitionsOf(sp);
            if (t?.Value == null || t.Value.Length < 2) continue;
            idx2 = i; break;
        }
        Expect(idx2 >= 0, "no multi-row struct found");
        var graph2 = new ComboGraph { Nodes = new List<ComboNode> { BaseNode(1, idx2) } };
        graph2.TreeRowProbabilityOverrides[idx2] = new Dictionary<int, float> { [0] = 0f };
        int before2 = ErrorLog.Warnings;
        _ = ExportDialog.AppendEnemyAuthoredContent(graph2, asset2, combo2, nodes2, ContentRoot);
        Expect(ErrorLog.Warnings == before2, "partial-0% struct warned unexpectedly");
    }

    /// <summary>Chance-sum guard: a touched struct whose rows don't total 100 (a
    /// link deleted after probabilities were typed) gets the remainder on the
    /// largest row - every other typed value stays exact - while exact-100 sets
    /// pass through untouched, all-zero sets are skipped (the flow warning owns
    /// them), over-100 sets fall back to a proportional scale, and the rescale
    /// logs info-only so it never counts toward the warning total.</summary>
    private static void TestChanceRescale(string targetFile)
    {
        static List<float> Probs(ArrayPropertyData rows) => rows.Value?
            .OfType<StructPropertyData>()
            .Select(r => r.Value?.OfType<FloatPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_fProbability")?.Value ?? float.NaN)
            .ToList() ?? new List<float>();

        // Case A: typed rows summing under 100 -> remainder lands on the largest
        // row only; the other typed row stays exact; warning count unchanged.
        var asset = Load(targetFile);
        var (combo, nodes) = ComboOf(asset);
        int idx = -1, rowCount = 0;
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (NodeStructAt(nodes, i) is not StructPropertyData sp) continue;
            var t = TransitionsOf(sp);
            if (t?.Value == null || t.Value.Length < 2) continue;
            idx = i; rowCount = t.Value.Length; break;
        }
        Expect(idx >= 0, "no multi-row struct found for rescale test");
        var graph = new ComboGraph { Nodes = new List<ComboNode> { BaseNode(1, idx) } };
        var typed = Enumerable.Range(0, rowCount).ToDictionary(r => r, _ => 0f);
        typed[0] = 40f;
        typed[1] = 30f;
        graph.TreeRowProbabilityOverrides[idx] = typed;
        int before = ErrorLog.Warnings;
        _ = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodes, ContentRoot);
        var pa = Probs(TransitionsOf(NodeStructAt(nodes, idx))!);
        Expect(Math.Abs(pa.Sum() - 100f) <= 0.01f, $"under-sum rescale total {pa.Sum():0.###} != 100");
        Expect(Math.Abs(pa[0] - 70f) <= 0.01f, $"largest row {pa[0]:0.###} != 70 (should absorb the missing 30)");
        Expect(Math.Abs(pa[1] - 30f) <= 0.01f, $"second typed row {pa[1]:0.###} != 30 (must stay exact)");
        for (int r = 2; r < rowCount; r++)
            Expect(Math.Abs(pa[r]) <= 0.01f, $"row {r} = {pa[r]:0.###} != 0 (should stay 0)");
        Expect(ErrorLog.Warnings == before, "PROB RESCALE must be info-only (warning count changed)");

        // Case B: exactly 100 -> values pass through untouched, no warning.
        var asset2 = Load(targetFile);
        var (combo2, nodes2) = ComboOf(asset2);
        int idx2 = -1, rowCount2 = 0;
        for (int i = 0; i < nodes2.Value.Length; i++)
        {
            if (NodeStructAt(nodes2, i) is not StructPropertyData sp) continue;
            var t = TransitionsOf(sp);
            if (t?.Value == null || t.Value.Length < 2) continue;
            idx2 = i; rowCount2 = t.Value.Length; break;
        }
        Expect(idx2 >= 0, "no multi-row struct found for exact-100 test");
        var graph2 = new ComboGraph { Nodes = new List<ComboNode> { BaseNode(1, idx2) } };
        var exact = Enumerable.Range(0, rowCount2).ToDictionary(r => r, _ => 0f);
        exact[0] = 100f;
        graph2.TreeRowProbabilityOverrides[idx2] = exact;
        int before2 = ErrorLog.Warnings;
        _ = ExportDialog.AppendEnemyAuthoredContent(graph2, asset2, combo2, nodes2, ContentRoot);
        var pb = Probs(TransitionsOf(NodeStructAt(nodes2, idx2))!);
        Expect(Math.Abs(pb[0] - 100f) <= 0.01f, $"exact-100 set was modified: row0 = {pb[0]:0.###}");
        for (int r = 1; r < rowCount2; r++)
            Expect(Math.Abs(pb[r]) <= 0.01f, $"exact-100 set row {r} = {pb[r]:0.###} != 0");
        Expect(ErrorLog.Warnings == before2, "exact-100 set warned unexpectedly");

        // Case C: all-zero set -> rescale skips it, the flow warning still owns it.
        var asset3 = Load(targetFile);
        var (combo3, nodes3) = ComboOf(asset3);
        int idx3 = -1, rowCount3 = 0;
        for (int i = 0; i < nodes3.Value.Length; i++)
        {
            if (NodeStructAt(nodes3, i) is not StructPropertyData sp) continue;
            var t = TransitionsOf(sp);
            if (t?.Value == null || t.Value.Length == 0) continue;
            idx3 = i; rowCount3 = t.Value.Length; break;
        }
        Expect(idx3 >= 0, "no rows-carrying struct found for all-zero test");
        var graph3 = new ComboGraph { Nodes = new List<ComboNode> { BaseNode(1, idx3) } };
        graph3.TreeRowProbabilityOverrides[idx3] =
            Enumerable.Range(0, rowCount3).ToDictionary(r => r, _ => 0f);
        int before3 = ErrorLog.Warnings;
        _ = ExportDialog.AppendEnemyAuthoredContent(graph3, asset3, combo3, nodes3, ContentRoot);
        var pc = Probs(TransitionsOf(NodeStructAt(nodes3, idx3))!);
        for (int r = 0; r < rowCount3; r++)
            Expect(Math.Abs(pc[r]) <= 0.01f, $"all-zero row {r} = {pc[r]:0.###} (rescale must skip sum-0 sets)");
        Expect(ErrorLog.Warnings > before3, "all-zero FLOW WARNING must still fire");

        // Case D: typed rows over 100 where the largest row can't absorb the
        // excess (150+150) -> proportional fallback scales the set to 100.
        var asset4 = Load(targetFile);
        var (combo4, nodes4) = ComboOf(asset4);
        int idx4 = -1, rowCount4 = 0;
        for (int i = 0; i < nodes4.Value.Length; i++)
        {
            if (NodeStructAt(nodes4, i) is not StructPropertyData sp) continue;
            var t = TransitionsOf(sp);
            if (t?.Value == null || t.Value.Length < 2) continue;
            idx4 = i; rowCount4 = t.Value.Length; break;
        }
        Expect(idx4 >= 0, "no multi-row struct found for over-100 test");
        var graph4 = new ComboGraph { Nodes = new List<ComboNode> { BaseNode(1, idx4) } };
        var over = Enumerable.Range(0, rowCount4).ToDictionary(r => r, _ => 0f);
        over[0] = 150f;
        over[1] = 150f;
        graph4.TreeRowProbabilityOverrides[idx4] = over;
        int before4 = ErrorLog.Warnings;
        _ = ExportDialog.AppendEnemyAuthoredContent(graph4, asset4, combo4, nodes4, ContentRoot);
        var pd = Probs(TransitionsOf(NodeStructAt(nodes4, idx4))!);
        Expect(Math.Abs(pd.Sum() - 100f) <= 0.01f, $"over-100 proportional fallback total {pd.Sum():0.###} != 100");
        Expect(Math.Abs(pd[0] - 50f) <= 0.01f, $"over-100 row0 {pd[0]:0.###} != 50 (proportional scale)");
        Expect(Math.Abs(pd[1] - 50f) <= 0.01f, $"over-100 row1 {pd[1]:0.###} != 50 (proportional scale)");
        Expect(ErrorLog.Warnings == before4, "over-100 rescale must be info-only (warning count changed)");
    }

    /// <summary>Player trees roll one winner per input. An edited Light pool must return
    /// to 100% on its own - vanilla ships Light 100 + Heavy 100 on the same node, so a
    /// node-wide scale would silently halve Heavy. An over-100 input only trims itself,
    /// and the same per-input rule must be wired through AppendEnemyAuthoredContent.</summary>
    private static void TestMainCharChancePerInput(string targetFile)
    {
        static List<PropertyData> RowsOf(StructPropertyData sp)
            => sp.Value?.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions")?.Value
                ?.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions")
                ?.Value?.ToList() ?? new List<PropertyData>();
        static FloatPropertyData? ProbProp(PropertyData? row)
            => (row as StructPropertyData)?.Value?.OfType<FloatPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_fProbability");
        static float ProbOf(PropertyData? row) => ProbProp(row)?.Value ?? float.NaN;
        static void SetProb(PropertyData? row, float value)
        {
            var prob = ProbProp(row);
            if (prob != null) prob.Value = value;
        }
        static string InputOf(PropertyData? row)
            => (row as StructPropertyData)?.Value?.OfType<EnumPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_eInputTransition")
                ?.Value?.Value?.ToString() ?? "";

        // Vanilla MC shape: a node with at least two inputs, every input pool at 100%.
        static (int idx, List<PropertyData> rows, Dictionary<string, List<int>> pools) FindVanillaPools(
            ArrayPropertyData nodes)
        {
            for (int i = 0; i < nodes.Value.Length; i++)
            {
                if (nodes.Value[i] is not StructPropertyData sp) continue;
                var rows = RowsOf(sp);
                if (rows.Count < 2) continue;
                var pools = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                for (int r = 0; r < rows.Count; r++)
                {
                    string input = InputOf(rows[r]);
                    if (!pools.TryGetValue(input, out var list))
                        pools[input] = list = new List<int>();
                    list.Add(r);
                }
                if (pools.Count < 2) continue;
                if (pools.Values.Any(p => Math.Abs(p.Sum(r => ProbOf(rows[r])) - 100f) > 0.01f))
                    continue;
                return (i, rows, pools);
            }
            return (-1, new List<PropertyData>(), new Dictionary<string, List<int>>());
        }

        static void ExpectPoolsAt100(string label, List<PropertyData> rows,
            Dictionary<string, List<int>> pools)
        {
            foreach (var kv in pools)
            {
                float sum = kv.Value.Sum(r => ProbOf(rows[r]));
                Expect(Math.Abs(sum - 100f) <= 0.01f,
                    $"{label}: input '{kv.Key}' pool {sum:0.###} != 100");
            }
        }

        // Case A: Light row typed down to 40 - only the Light pool absorbs the missing
        // 60; Heavy stays exactly 100 (node-wide scaling would ship 60/100).
        {
            var asset = Load(targetFile);
            var (_, nodes) = ComboOf(asset);
            var found = FindVanillaPools(nodes);
            Expect(found.idx >= 0, "no MainChar node with one 100% pool per input found");
            string lightKey = found.pools.Keys.First();
            string heavyKey = found.pools.Keys.Skip(1).First();
            int lightRow = found.pools[lightKey][0];
            SetProb(found.rows[lightRow], 40f);
            int fixedPools = ExportDialog.NormalizeChanceSums(nodes,
                new HashSet<int> { found.idx }, perInput: true);
            Expect(fixedPools == 1, $"per-input rescale reported {fixedPools} structs, expected 1");
            float lightSum = found.pools[lightKey].Sum(r => ProbOf(found.rows[r]));
            Expect(Math.Abs(lightSum - 100f) <= 0.01f,
                $"under-100 Light pool {lightSum:0.###} != 100");
            Expect(Math.Abs(ProbOf(found.rows[lightRow]) - 100f) <= 0.01f,
                $"Light row {ProbOf(found.rows[lightRow]):0.###} != 100");
            foreach (int r in found.pools[heavyKey])
                Expect(Math.Abs(ProbOf(found.rows[r]) - 100f) <= 0.01f,
                    $"Heavy row {ProbOf(found.rows[r]):0.###} was rescaled by the Light edit");
        }

        // Case B: Light typed over 100 - only the Light pool is trimmed; Heavy untouched.
        {
            var asset = Load(targetFile);
            var (_, nodes) = ComboOf(asset);
            var found = FindVanillaPools(nodes);
            Expect(found.idx >= 0, "no MainChar node with one 100% pool per input found");
            string lightKey = found.pools.Keys.First();
            string heavyKey = found.pools.Keys.Skip(1).First();
            int lightRow = found.pools[lightKey][0];
            SetProb(found.rows[lightRow], 125f);
            _ = ExportDialog.NormalizeChanceSums(nodes, new HashSet<int> { found.idx }, perInput: true);
            float lightSum = found.pools[lightKey].Sum(r => ProbOf(found.rows[r]));
            Expect(Math.Abs(lightSum - 100f) <= 0.01f,
                $"over-100 Light pool {lightSum:0.###} != 100");
            foreach (int r in found.pools[heavyKey])
                Expect(Math.Abs(ProbOf(found.rows[r]) - 100f) <= 0.01f,
                    $"Heavy row {ProbOf(found.rows[r]):0.###} was rescaled by the Light edit");
        }

        // Case C: same rule through the real append pass - an MC identity must pick the
        // per-input guard and leave the other input's 100% alone.
        {
            var asset = Load(targetFile);
            var (combo, nodes) = ComboOf(asset);
            var found = FindVanillaPools(nodes);
            Expect(found.idx >= 0, "no MainChar node with one 100% pool per input found");
            string lightKey = found.pools.Keys.First();
            string heavyKey = found.pools.Keys.Skip(1).First();
            int lightRow = found.pools[lightKey][0];
            var graph = new ComboGraph
            {
                SourceGamePath = "Game/DB/_MainChar/Combos/MainChar_ComboTree",
                Nodes = new List<ComboNode> { BaseNode(1, found.idx) }
            };
            graph.TreeRowProbabilityOverrides[found.idx] =
                new Dictionary<int, float> { [lightRow] = 40f };
            _ = ExportDialog.AppendEnemyAuthoredContent(graph, asset, combo, nodes, ContentRoot);
            var patched = (StructPropertyData)nodes.Value[found.idx]!;
            var patchedRows = RowsOf(patched);
            ExpectPoolsAt100("append pass", patchedRows, found.pools);
            foreach (int r in found.pools[heavyKey])
                Expect(Math.Abs(ProbOf(patchedRows[r]) - 100f) <= 0.01f,
                    $"append pass rescaled Heavy row {ProbOf(patchedRows[r]):0.###}");
        }
    }

    // --- Cautious link check (BranchChanceGate) ------------------------------------
    // Synthetic graphs: the gate only needs TreeRows/overrides/edges, so these run
    // without touching a vanilla asset.

    private static TreeRowInfo TRow(int idx, string input, float probability) => new()
    {
        RowIndex = idx,
        InputName = input,
        OriginalProbability = probability
    };

    private static (ComboGraph Graph, ComboNode Source) GateGraph(
        string sourcePath = "", params TreeRowInfo[] rows)
    {
        var graph = new ComboGraph { SourceGamePath = sourcePath };
        var source = BaseNode(1, 0);
        var target = BaseNode(2, 1);
        graph.Nodes.AddRange(new[] { source, target });
        if (rows.Length > 0) graph.TreeRows[0] = rows.ToList();
        return (graph, source);
    }

    /// <summary>Untyped vanilla rows + a new link rebalance to exactly 100 and give the
    /// link a real share - the clean case must not open the dialog.</summary>
    private static void TestChanceGateVanillaAppend()
    {
        var (graph, source) = GateGraph("", TRow(0, "Light", 100f));
        var edge = new ComboEdge { FromNodeId = 1, ToNodeId = 2, InputName = "Light", IsNewLink = true };
        graph.Edges.Add(edge);

        var gate = BranchChanceGate.Evaluate(graph, source, edge);
        Expect(!gate.NeedsAttention, $"clean append flagged: {gate.Summary}");
        Expect(!gate.NewLinkUnreachable, "clean append reported the new link unreachable");
        Expect(gate.Pools.Count == 1, $"enemy node should expose one pool, got {gate.Pools.Count}");
        Expect(Math.Abs(gate.Pools[0].Total - 100f) <= 0.5f,
            $"pool total {gate.Pools[0].Total:0.###} != 100");
        Expect(gate.NewLinkEffective > 0f, "new link got 0% on a clean pool");
    }

    /// <summary>Existing rows typed to consume the whole pool: the sum is still 100 but
    /// the brand-new link resolves to 0%, so the caution check must fire.</summary>
    private static void TestChanceGateTypedFullPool()
    {
        var (graph, source) = GateGraph("", TRow(0, "Light", 100f));
        graph.TreeRowProbabilityOverrides[0] = new Dictionary<int, float> { [0] = 100f };
        var edge = new ComboEdge { FromNodeId = 1, ToNodeId = 2, InputName = "Light", IsNewLink = true };
        graph.Edges.Add(edge);

        var gate = BranchChanceGate.Evaluate(graph, source, edge);
        Expect(gate.NeedsAttention, "typed-100 pool did not flag the dead new link");
        Expect(gate.NewLinkUnreachable, "new link was not reported unreachable");
        Expect(gate.Pools.Count == 1 && gate.Pools[0].Ok,
            "typed-100 pool should still total 100 (only the 0% trigger fires)");
        Expect(gate.Summary.Contains("new link lands at 0%", StringComparison.Ordinal),
            $"summary missing the 0% reason: '{gate.Summary}'");
    }

    /// <summary>Typed rows over 100 both break the pool sum and leave the new link at 0.</summary>
    private static void TestChanceGateOver100()
    {
        var (graph, source) = GateGraph("", TRow(0, "Light", 60f), TRow(1, "Light", 60f));
        graph.TreeRowProbabilityOverrides[0] = new Dictionary<int, float> { [0] = 60f, [1] = 60f };
        var edge = new ComboEdge { FromNodeId = 1, ToNodeId = 2, InputName = "Light", IsNewLink = true };
        graph.Edges.Add(edge);

        var gate = BranchChanceGate.Evaluate(graph, source, edge);
        Expect(gate.NeedsAttention, "over-100 pool did not flag");
        Expect(gate.NewLinkUnreachable, "over-100 pool did not leave the new link at 0%");
        Expect(gate.Pools.Count == 1 && !gate.Pools[0].Ok,
            $"over-100 pool reported ok (total {gate.Pools[0].Total:0.###})");
    }

    /// <summary>A player append only rebalances its own input; Heavy's 100% must not be
    /// touched (the node has no single node-wide pool to scale).</summary>
    private static void TestChanceGateMainCharPerInput()
    {
        var (graph, source) = GateGraph(
            "Game/DB/_MainChar/Combos/MainChar_ComboTree",
            TRow(0, "Light", 50f), TRow(1, "Light", 50f), TRow(2, "Heavy", 100f));
        var edge = new ComboEdge { FromNodeId = 1, ToNodeId = 2, InputName = "Light", IsNewLink = true };
        graph.Edges.Add(edge);

        var gate = BranchChanceGate.Evaluate(graph, source, edge);
        Expect(gate.PerInput, "MainChar graph did not switch to per-input pools");
        Expect(gate.Pools.Count == 2, $"expected Light+Heavy pools, got {gate.Pools.Count}");
        foreach (var p in gate.Pools)
            Expect(Math.Abs(p.Total - 100f) <= 0.5f, $"input '{p.Label}' pool {p.Total:0.###} != 100");
        Expect(!gate.NeedsAttention, $"clean per-input append flagged: {gate.Summary}");

        var rows = new[]
        {
            new BranchChanceRow("Light", null, 50f, false),
            new BranchChanceRow("Light", null, 50f, false),
            new BranchChanceRow("Heavy", null, 100f, false),
            new BranchChanceRow("Light", null, 0f, true)
        };
        var eff = BranchChanceGate.ComputeEffective(rows, perInput: true);
        Expect(Math.Abs(eff[2] - 100f) <= 0.01f,
            $"Heavy row {eff[2]:0.###} was rescaled by the Light append");
        Expect(Math.Abs(eff[0] + eff[1] + eff[3] - 100f) <= 0.01f,
            $"Light pool {eff[0] + eff[1] + eff[3]:0.###} != 100 after the append");
        Expect(eff[3] > 0f, "new Light link got 0% on a clean Light pool");
    }

    /// <summary>An A' owning redirect decides which struct the link writes, so the gate
    /// must evaluate that struct - not the source node's own (typed-100) rows.</summary>
    private static void TestChanceGateRedirectStruct()
    {
        var graph = new ComboGraph();
        var source = BaseNode(1, 0);
        var redirect = new ComboNode
        {
            Id = 3,
            TreeIndex = 1,
            IsRedirect = true,
            OwnedContinuation = true,
            ResolvedRedirectNodeId = 1,
            DisplayName = "redirect"
        };
        var target = BaseNode(2, 2);
        graph.Nodes.AddRange(new[] { source, redirect, target });
        graph.TreeRows[0] = new List<TreeRowInfo> { TRow(0, "Light", 100f) };
        graph.TreeRows[1] = new List<TreeRowInfo> { TRow(0, "Light", 100f) };
        // The source's own struct is fully typed; the owning struct is untouched. Using
        // the wrong struct would report the new link at 0%.
        graph.TreeRowProbabilityOverrides[0] = new Dictionary<int, float> { [0] = 100f };
        var edge = new ComboEdge { FromNodeId = 1, ToNodeId = 2, InputName = "Light", IsNewLink = true };
        graph.Edges.Add(edge);

        var gate = BranchChanceGate.Evaluate(graph, source, edge);
        Expect(gate.Pools.Count == 1, $"expected one owning-struct pool, got {gate.Pools.Count}");
        Expect(Math.Abs(gate.Pools[0].Total - 100f) <= 0.5f,
            $"owning pool total {gate.Pools[0].Total:0.###} != 100");
        Expect(!gate.NewLinkUnreachable && gate.NewLinkEffective > 0f,
            "gate evaluated the source's own typed rows instead of the redirect-owned struct");
    }

    /// <summary>A row-less custom node with one link splits the full 100 on its own.</summary>
    private static void TestChanceGateCustomNode()
    {
        var graph = new ComboGraph();
        var source = new ComboNode
        {
            Id = 1,
            TreeIndex = -1,
            Kind = ComboNodeKind.UserCreated,
            DisplayName = "custom"
        };
        var target = BaseNode(2, 0);
        graph.Nodes.AddRange(new[] { source, target });
        var edge = new ComboEdge { FromNodeId = 1, ToNodeId = 2, InputName = "Light", IsNewLink = true };
        graph.Edges.Add(edge);

        var gate = BranchChanceGate.Evaluate(graph, source, edge);
        Expect(!gate.NeedsAttention, $"custom-node link flagged: {gate.Summary}");
        Expect(gate.Pools.Count == 1 && Math.Abs(gate.Pools[0].Total - 100f) <= 0.5f,
            "custom-node link should own the whole 100% pool");
        Expect(Math.Abs(gate.NewLinkEffective - 100f) <= 0.01f,
            $"custom-node link effective {gate.NewLinkEffective:0.###} != 100");
    }

    /// <summary>
    /// Deleting one of two links from a vanilla node: the row disappears, the automatic
    /// rows split the 100 between them, and the pool check stays silent.
    /// </summary>
    private static void TestChanceGateDeletedLinkAuto()
    {
        var (graph, source) = GateGraph("", TRow(0, "Light", 100f));
        var toB = new ComboEdge { FromNodeId = 1, ToNodeId = 2, InputName = "Light", IsNewLink = true };
        var toC = new ComboEdge { FromNodeId = 1, ToNodeId = 3, InputName = "Light", IsNewLink = true };
        graph.Edges.AddRange(new[] { toB, toC });

        // Simulate deleting B: only its row leaves the model.
        graph.Edges.Remove(toB);
        var gate = BranchChanceGate.EvaluatePool(graph, source);
        Expect(!gate.NeedsAttention, $"clean post-delete pool flagged: {gate.Summary}");
        Expect(gate.Pools.Count == 1 && Math.Abs(gate.Pools[0].Total - 100f) <= 0.5f,
            $"auto rows should absorb the removed share (total {gate.Pools[0].Total:0.###})");
    }

    /// <summary>
    /// Typed rows cannot absorb the removed share: 40 + 40 leaves the pool at 80, so the
    /// post-delete check must flag it for Conditions &amp; Chances.
    /// </summary>
    private static void TestChanceGateDeletedLinkTyped()
    {
        var (graph, source) = GateGraph("",
            TRow(0, "Light", 50f), TRow(1, "Light", 50f));
        graph.TreeRowProbabilityOverrides[0] = new Dictionary<int, float> { [0] = 40f, [1] = 40f };
        var toB = new ComboEdge { FromNodeId = 1, ToNodeId = 2, InputName = "Light", IsNewLink = true };
        graph.Edges.Add(toB);

        graph.Edges.Remove(toB);
        var gate = BranchChanceGate.EvaluatePool(graph, source);
        Expect(gate.NeedsAttention, "typed 40/40 pool did not flag after the delete");
        Expect(gate.Pools.Count == 1 && Math.Abs(gate.Pools[0].Total - 80f) <= 0.5f,
            $"expected an 80% pool, got {gate.Pools[0].Total:0.###}");
        Expect(gate.Summary.Contains("80", StringComparison.Ordinal),
            $"summary should name the 80% total: '{gate.Summary}'");
    }

    /// <summary>
    /// A custom node's rows are its links: removing one leaves the other at its typed
    /// 40%, so the pool is broken exactly like the vanilla case.
    /// </summary>
    private static void TestChanceGateDeletedCustomExplicit()
    {
        var graph = new ComboGraph();
        var source = new ComboNode
        {
            Id = 1,
            TreeIndex = -1,
            Kind = ComboNodeKind.UserCreated,
            DisplayName = "custom"
        };
        graph.Nodes.AddRange(new[] { source, BaseNode(2, 0), BaseNode(3, 1) });
        var toB = new ComboEdge
        {
            FromNodeId = 1, ToNodeId = 2, InputName = "Light", IsNewLink = true, NewLinkProbability = 40f
        };
        var toC = new ComboEdge
        {
            FromNodeId = 1, ToNodeId = 3, InputName = "Light", IsNewLink = true, NewLinkProbability = 40f
        };
        graph.Edges.AddRange(new[] { toB, toC });

        graph.Edges.Remove(toB);
        var gate = BranchChanceGate.EvaluatePool(graph, source);
        Expect(gate.NeedsAttention, "typed custom-node pool did not flag after the delete");
        Expect(gate.Pools.Count == 1 && Math.Abs(gate.Pools[0].Total - 40f) <= 0.5f,
            $"expected a 40% pool, got {gate.Pools[0].Total:0.###}");
    }

    /// <summary>The same custom node with automatic links re-splits the 100 and stays clean.</summary>
    private static void TestChanceGateDeletedCustomAuto()
    {
        var graph = new ComboGraph();
        var source = new ComboNode
        {
            Id = 1,
            TreeIndex = -1,
            Kind = ComboNodeKind.UserCreated,
            DisplayName = "custom"
        };
        graph.Nodes.AddRange(new[] { source, BaseNode(2, 0), BaseNode(3, 1) });
        var toB = new ComboEdge { FromNodeId = 1, ToNodeId = 2, InputName = "Light", IsNewLink = true };
        var toC = new ComboEdge { FromNodeId = 1, ToNodeId = 3, InputName = "Light", IsNewLink = true };
        graph.Edges.AddRange(new[] { toB, toC });

        graph.Edges.Remove(toB);
        var gate = BranchChanceGate.EvaluatePool(graph, source);
        Expect(!gate.NeedsAttention, $"automatic custom pool flagged: {gate.Summary}");
        Expect(Math.Abs(gate.Pools[0].Total - 100f) <= 0.5f,
            $"remaining automatic link should own 100%, got {gate.Pools[0].Total:0.###}");
    }

    /// <summary>
    /// The post-delete source collector only reports surviving rows that really vanish:
    /// merged links (row survives), redirect hops, edges from removed nodes, and
    /// duplicate links to the same target are all excluded.
    /// </summary>
    private static void TestDeleteChanceSources()
    {
        var graph = new ComboGraph();
        var alive = BaseNode(1, 0);
        var removedSource = BaseNode(2, 1);
        removedSource.Removed = true;
        var mergedSource = BaseNode(3, 2);
        var redirectSource = new ComboNode
        {
            Id = 5, TreeIndex = 3, IsRedirect = true, DisplayName = "redirect"
        };
        graph.Nodes.AddRange(new[] { alive, removedSource, mergedSource, redirectSource,
            BaseNode(4, 6), BaseNode(10, 4), BaseNode(11, 5) });

        var normal = new ComboEdge { FromNodeId = 1, ToNodeId = 10, InputName = "Light", IsNewLink = true };
        var duplicate = new ComboEdge { FromNodeId = 1, ToNodeId = 10, InputName = "Heavy", IsNewLink = true };
        var merged = new ComboEdge
        {
            FromNodeId = 3, ToNodeId = 10, InputName = "Light", IsNewLink = true, MergedIntoStruct = 0
        };
        var redirect = new ComboEdge { FromNodeId = 5, ToNodeId = 10, InputName = "Light", IsNewLink = true, IsRedirect = true };
        var fromRemoved = new ComboEdge { FromNodeId = 2, ToNodeId = 10, InputName = "Light", IsNewLink = true };
        var elsewhere = new ComboEdge { FromNodeId = 4, ToNodeId = 11, InputName = "Light", IsNewLink = true };
        graph.Edges.AddRange(new[] { normal, duplicate, merged, redirect, fromRemoved, elsewhere });

        var sources = NodeDeleteChanceCheck.CollectAffectedSources(graph, new[] { 10 });
        Expect(sources.Count == 1 && sources[0] == 1,
            $"expected only source 1, got [{string.Join(", ", sources)}]");

        // Deleting the redirect node 11 as well (the real target is being removed) must
        // also report the surviving link that pointed into the redirect.
        var viaRedirect = NodeDeleteChanceCheck.CollectAffectedSources(graph, new[] { 10, 11 });
        Expect(viaRedirect.Count == 2 && viaRedirect[0] == 1 && viaRedirect[1] == 4,
            $"expected sources [1, 4] through a broken redirect, got [{string.Join(", ", viaRedirect)}]");

        Expect(NodeDeleteChanceCheck.AffectedSourceForEdge(graph, normal) == 1,
            "normal deleted link should report its source");
        Expect(NodeDeleteChanceCheck.AffectedSourceForEdge(graph, merged) == -1,
            "merged link must not report a chance source");
        Expect(NodeDeleteChanceCheck.AffectedSourceForEdge(graph, redirect) == -1,
            "redirect link must not report a chance source");
        Expect(NodeDeleteChanceCheck.AffectedSourceForEdge(graph, fromRemoved) == -1,
            "a removed source must be excluded");
        Expect(NodeDeleteChanceCheck.CollectAffectedSources(graph, Array.Empty<int>()).Count == 0,
            "empty removal set should produce no sources");
    }
}
