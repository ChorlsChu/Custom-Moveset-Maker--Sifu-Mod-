using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SifuMovesetEditor;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace import_smoke;

/// <summary>
/// Smoke tests for the modded-combo import diff: a vanilla combo file is copied,
/// mutated with UAssetAPI (chance, condition clear, condition param, start entry,
/// AI payload, appended AI node), then run through ReadModdedComboNodes /
/// ReadModdedStartEntries / ApplyModdedComboToVanilla against a fresh vanilla parse
/// of the same tree. Every mutation must surface as the right override/count on the
/// vanilla graph. Exit code 0 = all passed.
/// </summary>
internal static class Program
{
    private static readonly string ContentRoot =
        Environment.GetEnvironmentVariable("SIFU_CONTENT")
        ?? @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content";

    private static readonly string SeanP1Rel = "Game/DB/AI/Archetypes/Sean/Sean_Combo_Phase1";
    private static readonly string FajarP2Rel = "Game/DB/AI/Archetypes/Fajar/Attacks/Fajar_Combo_P2";
    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "import_smoke");
    private static readonly List<string> Failures = new();

    private static readonly MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static int Main()
    {
        ErrorLog.Init();
        Directory.CreateDirectory(OutDir);

        string seanP1 = Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Sean", "Sean_Combo_Phase1.uasset");
        string fajarP2 = Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Fajar", "Attacks", "Fajar_Combo_P2.uasset");
        if (!File.Exists(seanP1) || !File.Exists(fajarP2))
        {
            Console.Error.WriteLine($"test assets missing under {ContentRoot} (set SIFU_CONTENT to the game Content dir)");
            return 2;
        }

        Run("ReaderBasics", TestReaderBasics);
        Run("ChanceOverride", TestChanceOverride);
        Run("ConditionClear", TestConditionClear);
        Run("ConditionParam", TestConditionParam);
        Run("StartEntryProbability", TestStartEntryProbability);
        Run("AiPayloadEventToAttack", TestAiPayloadEventToAttack);
        Run("SurfacedEventNode", TestSurfacedEventNode);

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine("ALL IMPORT SMOKE TESTS PASSED");
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

    // ---------------------------------------------------------------- pipeline

    private static AnimationParser NewParser()
    {
        var parser = new AnimationParser();
        string gameRoot = Path.GetDirectoryName(ContentRoot.TrimEnd(Path.DirectorySeparatorChar))!;
        parser.Initialize(gameRoot, ContentRoot);
        return parser;
    }

    private static (ModdedApplyResult result, ComboGraph graph) RunImport(
        string moddedPath, string gamePath, string weapon, bool withStartEntries = true)
    {
        var parser = NewParser();
        var nodes = parser.ReadModdedComboNodes(moddedPath, Path.GetDirectoryName(moddedPath));
        Expect(nodes.Count > 0, $"no nodes parsed from {Path.GetFileName(moddedPath)}");
        var graph = parser.LoadComboTreeFromPath(gamePath, weapon)
            ?? throw new Exception($"vanilla graph null for {gamePath}");
        var starts = withStartEntries ? parser.ReadModdedStartEntries(moddedPath) : null;
        var result = parser.ApplyModdedComboToVanilla(graph, nodes, weapon, starts);
        return (result, graph);
    }

    /// <summary>Copies the full cooked family (.uasset+.uexp+...) into a _src subfolder
    /// (so loads see payloads) and returns a CLEAN write path at the OutDir root -
    /// writing next to the copied .uexp would leave a stale sidecar shadowing the new
    /// .uasset on re-read.</summary>
    private static (string source, string written) CopyToTemp(string srcFile, string label)
    {
        string stem = Path.GetFileNameWithoutExtension(srcFile);
        string srcDir = Path.Combine(OutDir, $"{stem}_{label}_src");
        Directory.CreateDirectory(srcDir);
        foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk", ".uptnl" })
        {
            string s = Path.Combine(Path.GetDirectoryName(srcFile)!, stem + ext);
            if (File.Exists(s))
                File.Copy(s, Path.Combine(srcDir, stem + ext), true);
        }
        return (Path.Combine(srcDir, stem + ".uasset"), Path.Combine(OutDir, $"{stem}_{label}.uasset"));
    }

    private static UAsset Load(string path) =>
        new(path, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);

    private static (NormalExport combo, ArrayPropertyData nodes) ComboOf(UAsset asset)
    {
        foreach (var exp in asset.Exports)
        {
            if (exp is not NormalExport ne || ne.Data == null) continue;
            var nodes = ne.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Nodes");
            if (nodes != null) return (ne, nodes);
        }
        throw new Exception("no combo export with m_Nodes");
    }

    private static ArrayPropertyData? StartContainerOf(UAsset asset)
    {
        foreach (var exp in asset.Exports)
        {
            if (exp is not NormalExport ne || ne.Data == null) continue;
            var cont = ne.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboStartNodesContainer");
            if (cont != null) return cont;
        }
        return null;
    }

    private static ArrayPropertyData? TransitionsOf(StructPropertyData nodeSp) =>
        nodeSp.Value?.OfType<StructPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions")?.Value
            ?.OfType<ArrayPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions");

    private static int CondIndexOf(StructPropertyData rowSp) =>
        rowSp.Value?.OfType<ObjectPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ConditionInstance")?.Value?.Index ?? 0;

    private static string? NameOf(StructPropertyData sp) =>
        sp.Value?.OfType<NamePropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Name")?.Value?.Value?.ToString();

    private static PropertyData CloneProp(PropertyData src)
    {
        var clone = (PropertyData)MemberwiseCloneMethod.Invoke(src, null)!;
        if (clone is StructPropertyData ss && ss.Value != null)
        {
            var list = new List<PropertyData>(ss.Value.Count);
            foreach (var c in ss.Value) list.Add(CloneProp(c));
            ss.Value = list;
        }
        else if (clone is ArrayPropertyData aa && aa.Value != null)
        {
            var arr = new PropertyData[aa.Value.Length];
            for (int i = 0; i < aa.Value.Length; i++) arr[i] = CloneProp(aa.Value[i]);
            aa.Value = arr;
        }
        else if (clone is MapPropertyData mm && mm.Value != null)
        {
            var tm = new TMap<PropertyData, PropertyData>();
            foreach (var kvp in mm.Value)
                tm.Add(CloneProp(kvp.Key), CloneProp(kvp.Value));
            mm.Value = tm;
        }
        return clone;
    }

    // ---------------------------------------------------------------- finders

    /// <summary>First non-100 probability row on a MATCHED node (attack node with a real
    /// anim path, or an AI action node with a resolved class import - both pair into
    /// groupToModded; redirect/conduit structs do not).</summary>
    private static (int tree, int row, FloatPropertyData prop) FindNon100ProbRow(ArrayPropertyData nodes)
    {
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sp || sp.Value == null) continue;
            if (sp.Value.OfType<IntPropertyData>()
                    .Any(p => p.Name?.Value?.ToString() == "m_NodeRedirect" && p.Value >= 0))
                continue;
            bool hasAnim = sp.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos")?.Value
                ?.OfType<NamePropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_Attacks"
                       && (p.Value?.Value?.ToString() ?? "").Contains('/')) == true;
            bool hasAction = sp.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AIAttackAction")?.Value
                ?.OfType<ObjectPropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_DefaultClassObject"
                       && p.Value is { Index: not 0 }) == true;
            if (!hasAnim && !hasAction) continue;
            var trans = TransitionsOf(sp);
            if (trans?.Value == null) continue;
            for (int r = 0; r < trans.Value.Length; r++)
            {
                if (trans.Value[r] is not StructPropertyData row || row.Value == null) continue;
                var fp = row.Value.OfType<FloatPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_fProbability");
                if (fp != null && Math.Abs(fp.Value - 33.5f) > 0.001f)
                    return (i, r, fp);
            }
        }
        return (-1, -1, null!);
    }

    private static (int tree, int row) FindCondRow(ArrayPropertyData nodes)
    {
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sp || sp.Value == null) continue;
            var trans = TransitionsOf(sp);
            if (trans?.Value == null) continue;
            for (int r = 0; r < trans.Value.Length; r++)
            {
                if (trans.Value[r] is not StructPropertyData row || row.Value == null) continue;
                if (CondIndexOf(row) > 0) return (i, r);
            }
        }
        return (-1, -1);
    }

    private static int FindEventNodeIndex(UAsset asset, ArrayPropertyData nodes)
    {
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sp || sp.Value == null) continue;
            var ev = sp.Value.OfType<ObjectPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Event");
            if (ev?.Value != null && ev.Value.Index != 0) return i;
        }
        return -1;
    }

    private static int FindAttackNodeIndex(ArrayPropertyData nodes)
    {
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sp || sp.Value == null) continue;
            bool hasEvent = sp.Value.OfType<ObjectPropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_Event" && p.Value is { Index: not 0 });
            bool hasAction = sp.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AIAttackAction")?.Value
                ?.OfType<ObjectPropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_DefaultClassObject"
                       && p.Value is { Index: not 0 }) == true;
            if (hasEvent || hasAction) continue;
            bool hasAnim = sp.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos")?.Value
                ?.OfType<NamePropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_Attacks"
                       && (p.Value?.Value?.ToString() ?? "").Contains('/')) == true;
            if (hasAnim) return i;
        }
        return -1;
    }

    private static readonly HashSet<string> SkippedParamProps = new(StringComparer.Ordinal)
    {
        "m_ConditionID", "m_PerConditionValueToNodeMap", "m_iSerializeVersion"
    };

    /// <summary>First (tree, row, condExport1Based, leaf) on a MATCHED node (attack or
    /// action, no redirect) whose condition export carries a mutable numeric/bool leaf
    /// outside the collector's skip set - the param walk ignores m_ConditionID etc.,
    /// so mutating those would never produce an override.</summary>
    private static (int tree, int row, int condIdx, PropertyData leaf) FindCondRowWithMutableParam(
        UAsset asset, ArrayPropertyData nodes)
    {
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sp || sp.Value == null) continue;
            if (sp.Value.OfType<IntPropertyData>()
                    .Any(p => p.Name?.Value?.ToString() == "m_NodeRedirect" && p.Value >= 0))
                continue;
            bool hasAnim = sp.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos")?.Value
                ?.OfType<NamePropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_Attacks"
                       && (p.Value?.Value?.ToString() ?? "").Contains('/')) == true;
            bool hasAction = sp.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AIAttackAction")?.Value
                ?.OfType<ObjectPropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_DefaultClassObject"
                       && p.Value is { Index: not 0 }) == true;
            if (!hasAnim && !hasAction) continue;
            var trans = TransitionsOf(sp);
            if (trans?.Value == null) continue;
            for (int r = 0; r < trans.Value.Length; r++)
            {
                if (trans.Value[r] is not StructPropertyData row || row.Value == null) continue;
                int condIdx = CondIndexOf(row);
                if (condIdx <= 0 || condIdx > asset.Exports.Count) continue;
                if (asset.Exports[condIdx - 1] is not NormalExport { Data: { Count: > 0 } condExp }) continue;
                var leaf = FindMutableLeaf(condExp);
                if (leaf != null) return (i, r, condIdx, leaf);
            }
        }
        return (-1, -1, -1, null!);
    }

    private static PropertyData? FindMutableLeaf(List<PropertyData> props, int depth = 0)
    {
        if (depth > 3) return null;
        foreach (var p in props)
        {
            string name = p.Name?.Value?.ToString() ?? "";
            if (SkippedParamProps.Contains(name)) continue;
            // Safe-to-mutate scalar leaves only (Name/Enum/Byte strings risk corrupting
            // the fixture write; struct/array/map recurse).
            if (p is FloatPropertyData or IntPropertyData or Int8PropertyData
                or UInt16PropertyData or UInt32PropertyData or Int64PropertyData
                or BoolPropertyData)
                return p;
            if (p is StructPropertyData { Value: { } sv })
            {
                var hit = FindMutableLeaf(sv, depth + 1);
                if (hit != null) return hit;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- test cases

    private static void TestReaderBasics()
    {
        string file = Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Sean", "Sean_Combo_Phase1.uasset");
        var parser = NewParser();
        var nodes = parser.ReadModdedComboNodes(file, ContentRoot);
        Expect(nodes.Count > 0, "no nodes parsed");
        Expect(nodes.Any(n => n.AiKind is "Action" or "Event"), "no AI payload nodes read");
        Expect(nodes.Sum(n => n.TransitionRows.Count) > 0, "no transition rows read");
        Expect(nodes.All(n => !string.IsNullOrEmpty(n.ModdedContentRel)), "ModdedContentRel not set");
        var starts = parser.ReadModdedStartEntries(file);
        var graph = parser.LoadComboTreeFromPath(SeanP1Rel, "Staff")
            ?? throw new Exception("vanilla graph null");
        Expect(starts.Count == graph.StartEntries.Count,
            $"start entries {starts.Count} != vanilla {graph.StartEntries.Count}");
    }

    private static void TestChanceOverride()
    {
        var (source, written) = CopyToTemp(Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Sean", "Sean_Combo_Phase1.uasset"), "chance");
        var asset = Load(source);
        var (_, nodes) = ComboOf(asset);
        var (tree, row, prop) = FindNon100ProbRow(nodes);
        Expect(tree >= 0, "no probability row on a matched attack/action node");
        float original = prop.Value;
        prop.Value = 33.5f;
        asset.Write(written);

        var (result, graph) = RunImport(written, SeanP1Rel, "Staff", withStartEntries: false);
        Expect(result.ChanceOverrides >= 1, $"ChanceOverrides={result.ChanceOverrides}, expected >= 1");
        Expect(graph.TreeRowProbabilityOverrides.TryGetValue(tree, out var pMap) &&
               pMap.TryGetValue(row, out var v) && Math.Abs(v - 33.5f) < 0.001f,
            $"override missing at tree[{tree}] row {row} (original {original})");
    }

    private static void TestConditionClear()
    {
        var (source, written) = CopyToTemp(Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Sean", "Sean_Combo_Phase1.uasset"), "condclear");
        var asset = Load(source);
        var (_, nodes) = ComboOf(asset);
        var (tree, row) = FindCondRow(nodes);
        Expect(tree >= 0, "no conditioned row found");
        var rowSp = (StructPropertyData)((StructPropertyData)nodes.Value[tree]).Value!
            .OfType<StructPropertyData>().First(p => p.Name?.Value?.ToString() == "m_Transitions").Value!
            .OfType<ArrayPropertyData>().First(p => p.Name?.Value?.ToString() == "m_Transitions").Value![row];
        var cond = rowSp.Value!.OfType<ObjectPropertyData>()
            .First(p => p.Name?.Value?.ToString() == "m_ConditionInstance");
        cond.Value = new FPackageIndex(0);
        asset.Write(written);

        var (result, graph) = RunImport(written, SeanP1Rel, "Staff", withStartEntries: false);
        Expect(result.CondClears >= 1, $"CondClears={result.CondClears}, expected >= 1");
        Expect(graph.TreeRowConditionOverrides.TryGetValue(tree, out var cMap) &&
               cMap.TryGetValue(row, out var edit) && edit.ClearCondition,
            $"ClearCondition missing at tree[{tree}] row {row}");
    }

    private static void TestConditionParam()
    {
        // Sean P1 conditions are mostly internal-only exports (no editable leaves) -
        // fall back to Fajar P2 when needed.
        foreach (var (file, rel, label) in new[]
        {
            (Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Sean", "Sean_Combo_Phase1.uasset"), SeanP1Rel, "condparam"),
            (Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Fajar", "Attacks", "Fajar_Combo_P2.uasset"), FajarP2Rel, "condparam2"),
        })
        {
            var (source, written) = CopyToTemp(file, label);
            var asset = Load(source);
            var (_, nodes) = ComboOf(asset);
            var (tree, row, condIdx, leaf) = FindCondRowWithMutableParam(asset, nodes);
            if (tree < 0) continue;
            switch (leaf)
            {
                case FloatPropertyData f:
                    f.Value += 7.5f;
                    break;
                case BoolPropertyData b:
                    b.Value = !b.Value;
                    break;
                case IntPropertyData ii:
                    ii.Value += 3;
                    break;
                case Int8PropertyData i8:
                    i8.Value += 1;
                    break;
                case UInt16PropertyData u16:
                    u16.Value += 1;
                    break;
                case UInt32PropertyData u32:
                    u32.Value += 1;
                    break;
                case Int64PropertyData i64:
                    i64.Value += 1;
                    break;
                default:
                    throw new Exception($"unexpected leaf type {leaf.GetType().Name}");
            }
            asset.Write(written);

            var (result, graph) = RunImport(written, rel, "Staff", withStartEntries: false);
            Expect(result.CondParamWrites >= 1, $"CondParamWrites={result.CondParamWrites}, expected >= 1");
            Expect(graph.TreeConditionParamOverrides.ContainsKey(condIdx),
                $"TreeConditionParamOverrides missing export {condIdx}");
            return;
        }
        throw new Exception("no conditioned row with a mutable param leaf in Sean P1 or Fajar P2");
    }

    private static void TestStartEntryProbability()
    {
        var (source, written) = CopyToTemp(Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Fajar", "Attacks", "Fajar_Combo_P2.uasset"), "start");
        var asset = Load(source);
        var cont = StartContainerOf(asset);
        Expect(cont?.Value?.Length > 0, "no start entries in fixture");
        var entry = (StructPropertyData)cont!.Value![0];
        var rows = entry.Value!.OfType<StructPropertyData>()
            .First(p => p.Name?.Value?.ToString() == "m_ComboTransitions").Value!
            .OfType<ArrayPropertyData>().First(p => p.Name?.Value?.ToString() == "m_Transitions").Value;
        Expect(rows!.Length > 0, "start entry has no transitions");
        var row0 = (StructPropertyData)rows[0];
        var fp = row0.Value!.OfType<FloatPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_fProbability");
        Expect(fp != null, "no m_fProbability on start transition");
        fp!.Value = 12.5f;
        asset.Write(written);

        var (result, graph) = RunImport(written, FajarP2Rel, "Staff");
        Expect(result.StartTransitionsChanged >= 1,
            $"StartTransitionsChanged={result.StartTransitionsChanged}, expected >= 1");
        Expect(result.StartEntriesChanged >= 1, $"StartEntriesChanged={result.StartEntriesChanged}, expected >= 1");
        var changed = graph.StartEntries
            .SelectMany(e => e.Transitions.Select(t => (entry: e, t)))
            .Where(x => Math.Abs(x.t.Probability - 12.5f) < 0.001f)
            .ToList();
        Expect(changed.Count > 0, "no start transition shows Probability=12.5");
        Expect(changed.All(x => Math.Abs(x.t.OriginalProbability - 12.5f) > 0.001f),
            "OriginalProbability was overwritten");
    }

    private static void TestAiPayloadEventToAttack()
    {
        // Fajar P2 carries event nodes (Sean P1 is action-only).
        var (source, written) = CopyToTemp(Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Fajar", "Attacks", "Fajar_Combo_P2.uasset"), "aipayload");
        var asset = Load(source);
        var (_, nodes) = ComboOf(asset);
        int evIdx = FindEventNodeIndex(asset, nodes);
        int atkIdx = FindAttackNodeIndex(nodes);
        Expect(evIdx >= 0 && atkIdx >= 0 && evIdx != atkIdx, $"event/attack node indices bad ({evIdx}/{atkIdx})");

        var evNode = (StructPropertyData)nodes.Value[evIdx];
        var evProp = evNode.Value!.OfType<ObjectPropertyData>()
            .First(p => p.Name?.Value?.ToString() == "m_Event");
        var atkNode = (StructPropertyData)nodes.Value[atkIdx];
        // Every node carries a null m_Event property - SET it, never append a second
        // one (the reader takes the first match).
        var atkEv = atkNode.Value!.OfType<ObjectPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Event");
        if (atkEv != null)
            atkEv.Value = evProp.Value;
        else
            atkNode.Value.Add(new ObjectPropertyData
            {
                Name = FName.FromString(asset, "m_Event"),
                Value = evProp.Value
            });
        asset.Write(written);

        var (result, graph) = RunImport(written, FajarP2Rel, "Staff", withStartEntries: false);
        Expect(result.AiChanges >= 1, $"AiChanges={result.AiChanges}, expected >= 1");
        Expect(graph.Nodes.Any(n => n.TreeIndex == atkIdx && n.IsEventNode && !string.IsNullOrEmpty(n.EventName)),
            $"vanilla tree[{atkIdx}] not marked as event node");
    }

    private static void TestSurfacedEventNode()
    {
        // Fajar P2 carries event nodes (Sean P1 is action-only).
        var (source, written) = CopyToTemp(Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Fajar", "Attacks", "Fajar_Combo_P2.uasset"), "surface");
        var asset = Load(source);
        var (_, nodes) = ComboOf(asset);
        int evIdx = FindEventNodeIndex(asset, nodes);
        Expect(evIdx >= 0, "no event node with m_Event export found");
        int appendedIdx = nodes.Value.Length;

        var clone = (StructPropertyData)CloneProp(nodes.Value[evIdx]);
        var nameIdx = clone.Value!.FindIndex(p => p.Name?.Value?.ToString() == "m_Name");
        Expect(nameIdx >= 0, "cloned node has no m_Name");
        clone.Value[nameIdx] = new NamePropertyData
        {
            Name = FName.FromString(asset, "m_Name"),
            Value = FName.FromString(asset, "SmokeExtraAiNode")
        };
        var arr = new PropertyData[nodes.Value.Length + 1];
        Array.Copy(nodes.Value, arr, nodes.Value.Length);
        arr[^1] = clone;
        nodes.Value = arr;
        asset.Write(written);

        var (result, graph) = RunImport(written, FajarP2Rel, "Staff", withStartEntries: false);
        Expect(result.Surfaced >= 1, $"Surfaced={result.Surfaced}, expected >= 1");
        var surfaced = graph.Nodes.FirstOrDefault(n =>
            n.Kind == ComboNodeKind.ImportedNew && n.Name == "SmokeExtraAiNode");
        Expect(surfaced != null, "SmokeExtraAiNode not surfaced");
        Expect(surfaced!.IsEventNode && !string.IsNullOrEmpty(surfaced.EventName),
            "surfaced node has no event payload");
        Expect(!string.IsNullOrEmpty(surfaced.ImportComboRel),
            "surfaced node has no ImportComboRel");
        Expect(surfaced.ImportSourceTreeIndex == appendedIdx,
            $"ImportSourceTreeIndex={surfaced.ImportSourceTreeIndex}, expected {appendedIdx}");
    }
}
