using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

internal static class Program
{
    static readonly EngineVersion Eng = EngineVersion.VER_UE4_26;
    const string GameContentRel = "bin/Debug/net10.0-windows/GameContent/Content";
    const string GruntTemplateRel = "DB/AI/Archetypes/Grunt/_Base/Grunt_Scenario_Base";
    const string TargetSet = "FireDisciple_Advanced";

    static readonly (string Rel, string TargetDb)[] FdAdvancedTargets =
    {
        ("DB/AI/Archetypes/FireDisciple/Variations/Scenario/FireDisciple_Scenario_H2", "BP_FireDisciple_ArchetypeDB_MiniBoss_H2"),
        ("DB/AI/Archetypes/FireDisciple/Variations/Scenario/FireDisciple_Scenario_H5", "BP_FireDisciple_ArchetypeDB_MiniBoss_H5"),
        ("DB/AI/Archetypes/FireDisciple/_Arena/Generic/FireDisciple_Scenario_ArenaAdv", "BP_FireDisciple_ArchetypeDB_ArenaMiniBoss"),
        ("DB/AI/Archetypes/FireDisciple/_Arena/Specific/Mute/FireDisciple_Scenario_H2_Mute_Arena", "BP_FireDisciple_ArchetypeDB_MiniBoss_H2_Mute_Arena"),
    };

    static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 1 && args[0] == "dump")
                return Dump(args.Skip(1).ToArray());

            if (args.Length >= 1 && args[0] == "patch")
                return Patch(args.Skip(1).ToArray());

            if (args.Length >= 1 && args[0] == "pak")
                return Pak(args.Skip(1).ToArray());

            Console.WriteLine("Usage:");
            Console.WriteLine("  lastman_poc dump <contentRoot>              dump phase structure of Grunt template + 4 FD Advanced scenarios");
            Console.WriteLine("  lastman_poc dump <contentRoot> <uasset>     dump one scenario file");
            Console.WriteLine("  lastman_poc patch <contentRoot> <outDir> [--no-gauge]");
            Console.WriteLine("  lastman_poc pak <outDir> <pakName> <gameContentRoot>   build overlay pak + sig from outDir staging tree");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex.Message);
            Console.WriteLine(ex.ToString());
            return 1;
        }
    }

    // ============================== dump ==============================

    static int Dump(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("dump needs <contentRoot>"); return 2; }
        string contentRoot = Path.GetFullPath(args[0]);
        if (args.Length >= 2)
        {
            string f = Path.GetFullPath(args[1]);
            if (!File.Exists(f)) { Console.WriteLine("missing: " + f); return 1; }
            DumpScenario(f);
            return 0;
        }

        foreach (var rel in new[] { GruntTemplateRel }.Concat(FdAdvancedTargets.Select(t => t.Rel)))
        {
            var f = Path.Combine(contentRoot, rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!File.Exists(f)) { Console.WriteLine("missing: " + f); return 1; }
            DumpScenario(f);
        }
        return 0;
    }

    static void DumpScenario(string path)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("FILE: " + path);
        Console.WriteLine("================================================================================");
        var asset = new UAsset(path, Eng, null, CustomSerializationFlags.None);
        Console.WriteLine($"Imports: {asset.Imports.Count}  Exports: {asset.Exports.Count}");

        Console.WriteLine("--- IMPORTS ---");
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var im = asset.Imports[i];
            int outer = im.OuterIndex.Index;
            string outerName = outer < 0 && -(outer + 1) < asset.Imports.Count
                ? asset.Imports[-(outer + 1)].ObjectName.Value?.ToString() ?? "?"
                : outer == 0 ? "" : $"<{outer}>";
            Console.WriteLine($"  [{i,4}] {im.ClassName.Value}.{im.ObjectName.Value}  Outer={outer} ({outerName})");
        }

        Console.WriteLine("--- EXPORTS ---");
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            var ex = asset.Exports[i];
            string cls = ResolveIndex(asset, ex.ClassIndex);
            string outer = ResolveIndex(asset, ex.OuterIndex);
            string super = ResolveIndex(asset, ex.SuperIndex);
            string tmpl = ResolveIndex(asset, ex.TemplateIndex);
            Console.WriteLine($"  [{i,4}] {ex.GetType().Name,-16} {ex.ObjectName.Value,-48} Class={cls} Outer={outer} Super={super} Template={tmpl} Size={ex.SerialSize} Flags={ex.ObjectFlags}");
        }

        Console.WriteLine("--- EXPORT PROPERTIES ---");
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            var ex = asset.Exports[i];
            if (ex is not NormalExport ne || ne.Data == null) continue;
            Console.WriteLine($"  === Export[{i}] {ex.ObjectName.Value} ({ne.Data.Count} props) ===");
            foreach (var p in ne.Data)
                DumpProp(asset, p, "    ");
        }
        Console.WriteLine();
    }

    static void DumpProp(UAsset asset, PropertyData p, string indent)
    {
        if (p == null) return;
        string name = p.Name.Value?.ToString() ?? "?";
        string meta = Meta(p);
        switch (p)
        {
            case ObjectPropertyData op:
                Console.WriteLine($"{indent}{name} -> {ResolveIndex(asset, op.Value)}  {meta}");
                break;
            case BoolPropertyData bp:
                Console.WriteLine($"{indent}{name} = {bp.Value}  {meta}");
                break;
            case IntPropertyData ip:
                Console.WriteLine($"{indent}{name} = {ip.Value}  {meta}");
                break;
            case FloatPropertyData fp:
                Console.WriteLine($"{indent}{name} = {fp.Value}  {meta}");
                break;
            case NamePropertyData np:
                Console.WriteLine($"{indent}{name} = FName({np.Value.Value})  {meta}");
                break;
            case EnumPropertyData ep:
                Console.WriteLine($"{indent}{name} = Enum({ep.Value.Value})  {meta}");
                break;
            case ArrayPropertyData ap:
                Console.WriteLine($"{indent}{name}[] ({ap.Value?.Length ?? 0})  {meta}");
                if (ap.Value != null)
                    for (int i = 0; i < ap.Value.Length; i++)
                    {
                        Console.WriteLine($"{indent}  [{i}]");
                        if (ap.Value[i] is StructPropertyData esp)
                            foreach (var inner in esp.Value ?? new List<PropertyData>())
                                DumpProp(asset, inner, indent + "    ");
                        else if (ap.Value[i] != null)
                            DumpProp(asset, ap.Value[i]!, indent + "    ");
                    }
                break;
            case StructPropertyData sp:
                Console.WriteLine($"{indent}{name} {{  {meta}");
                foreach (var inner in sp.Value ?? new List<PropertyData>())
                    DumpProp(asset, inner, indent + "  ");
                Console.WriteLine($"{indent}}}");
                break;
            case MapPropertyData mp:
                Console.WriteLine($"{indent}{name} map({mp.Value?.Count ?? 0})  {meta}");
                break;
            case StrPropertyData sp2:
                Console.WriteLine($"{indent}{name} = \"{sp2.Value}\"  {meta}");
                break;
            default:
            {
                var valProp = p.GetType().GetProperty("Value");
                object? v = null;
                try { v = valProp?.GetValue(p); } catch { }
                Console.WriteLine($"{indent}{name} [{p.GetType().Name}] = {v}  {meta}");
                break;
            }
        }
    }

    static string Meta(PropertyData p)
    {
        var bits = new List<string>();
        void Add(string prop)
        {
            try
            {
                var v = p.GetType().GetProperty(prop)?.GetValue(p);
                if (v is FName fn)
                {
                    string? s = fn.Value?.ToString();
                    if (!string.IsNullOrEmpty(s) && s != "None")
                        bits.Add($"{prop}={s}");
                }
            }
            catch { }
        }
        Add("SerializedType");
        Add("StructType");
        Add("ArrayType");
        Add("InnerType");
        var guidProp = p.GetType().GetProperty("StructGuid");
        try { if (guidProp?.GetValue(p) is FName g && !string.IsNullOrEmpty(g.Value?.ToString())) bits.Add("StructGuid=" + g.Value); } catch { }
        return bits.Count == 0 ? "" : "(" + string.Join(", ", bits) + ")";
    }

    static string ResolveIndex(UAsset asset, FPackageIndex? idx)
    {
        if (idx == null) return "<null>";
        int i = idx.Index;
        if (i == 0) return "(null)";
        if (i > 0 && i <= asset.Exports.Count) return $"exp{i - 1}:{asset.Exports[i - 1].ObjectName.Value}";
        if (i < 0)
        {
            int ri = -i - 1;
            if (ri >= 0 && ri < asset.Imports.Count)
                return $"imp{ri}:{asset.Imports[ri].ClassName.Value}.{asset.Imports[ri].ObjectName.Value}";
        }
        return $"<{i}>";
    }

    // ============================== patch ==============================

    static int Patch(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("patch needs <contentRoot> <outDir> [--no-gauge]"); return 2; }
        string contentRoot = Path.GetFullPath(args[0]);
        string outDir = Path.GetFullPath(args[1]);
        bool noGauge = args.Any(a => a == "--no-gauge");
        bool gaugeFull = !noGauge;

        string gruntPath = Path.Combine(contentRoot, GruntTemplateRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        if (!File.Exists(gruntPath)) { Console.WriteLine("grunt template missing: " + gruntPath); return 1; }
        var grunt = new UAsset(gruntPath, Eng, null, CustomSerializationFlags.None);

        if (grunt.Exports.Count < 2 || grunt.Exports[1] is not NormalExport gruntTakedown)
        { Console.WriteLine("grunt template shape unexpected: no export[1] takedown shell"); return 1; }
        var gruntNodes = FindArray(grunt.Exports[0], "m_PhaseNodes");
        if (gruntNodes == null || gruntNodes.Value == null || gruntNodes.Value.Length < 2)
        { Console.WriteLine("grunt template shape unexpected: m_PhaseNodes < 2"); return 1; }
        if (gruntNodes.Value[0] is not StructPropertyData gruntFightNode)
        { Console.WriteLine("grunt template shape unexpected: node[0] not struct"); return 1; }
        var gruntTransitions = FindArray(gruntFightNode, "m_PhaseTransitions");
        if (gruntTransitions == null || gruntTransitions.Value == null || gruntTransitions.Value.Length < 1
            || gruntTransitions.Value[0] is not StructPropertyData gruntTransition)
        { Console.WriteLine("grunt template shape unexpected: no takedown transition entry"); return 1; }
        if (gruntNodes.Value[1] is not StructPropertyData gruntLastManNode)
        { Console.WriteLine("grunt template shape unexpected: node[1] not struct"); return 1; }

        // Grunt import indices used for memoization (locked from dump):
        // imp3 = BlueprintGeneratedClass.BP_Grunt_LastMan_Db_C  (retarget -> target MiniBoss CDO)
        // imp4 = BlueprintGeneratedClass.BP_Grunt_LastManSynchronizedAttacksHitAction_C (clone into dst; vanilla always non-null)
        const int GruntLastManClassImport = 3;
        const int GruntTakedownExport = 1;

        Directory.CreateDirectory(outDir);
        int ok = 0;
        foreach (var (rel, targetDb) in FdAdvancedTargets)
        {
            var srcUasset = Path.Combine(contentRoot, rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!File.Exists(srcUasset)) { Console.WriteLine("missing target: " + srcUasset); return 1; }

            var stagedUasset = Path.Combine(outDir, "Sifu", "Content", rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(stagedUasset)!);
            File.Copy(srcUasset, stagedUasset, true);
            var srcUexp = Path.ChangeExtension(srcUasset, ".uexp");
            if (File.Exists(srcUexp)) File.Copy(srcUexp, Path.ChangeExtension(stagedUasset, ".uexp"), true);

            Console.WriteLine($"=== {rel}  -> {targetDb}  gauge={gaugeFull}");
            int rc = PatchOne(grunt, gruntNodes, gruntFightNode, gruntTransition, gruntLastManNode, gruntTakedown,
                GruntLastManClassImport, GruntTakedownExport,
                stagedUasset, targetDb, gaugeFull);
            if (rc < 0) return 1;
            ok++;
        }
        Console.WriteLine($"patched {ok}/{FdAdvancedTargets.Length} scenario(s), gauge={gaugeFull}");
        return 0;
    }

    static int PatchOne(
        UAsset grunt, ArrayPropertyData gruntNodes, StructPropertyData gruntFightNode,
        StructPropertyData gruntTransition, StructPropertyData gruntLastManNode, NormalExport gruntTakedown,
        int gruntLastManClassImport, int gruntTakedownExport,
        string stagedUasset, string targetDb, bool gaugeFull)
    {
        var asset = new UAsset(stagedUasset, Eng, null, CustomSerializationFlags.None);

        // find root phase + phase nodes
        var rootPhase = FindProp(asset.Exports[0], "m_rootPhase") as ObjectPropertyData;
        var nodes = FindArray(asset.Exports[0], "m_PhaseNodes");
        if (rootPhase?.Value == null || nodes?.Value == null || nodes.Value.Length == 0)
        { Console.WriteLine("  shape check failed: no m_rootPhase / m_PhaseNodes"); return -1; }

        // fight node = node whose m_PhaseArchetype == m_rootPhase, else node[0]
        StructPropertyData? fightNode = null;
        int fightIdx = 0;
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sn) continue;
            var arch = FindProp(sn, "m_PhaseArchetype") as ObjectPropertyData;
            if (arch?.Value != null && arch.Value.Index == rootPhase.Value.Index)
            { fightNode = sn; fightIdx = i; break; }
        }
        if (fightNode == null && nodes.Value[0] is StructPropertyData n0) { fightNode = n0; fightIdx = 0; }
        if (fightNode == null) { Console.WriteLine("  shape check failed: no struct phase node"); return -1; }

        // guard: already patched?
        foreach (var p in asset.Exports)
        {
            if (p is not NormalExport ne || ne.Data == null) continue;
            var cls = p.ClassIndex;
            if (cls.Index < 0)
            {
                var imp = asset.Imports[-cls.Index - 1];
                if (imp.ClassName.Value?.ToString() == "Class" && imp.ObjectName.Value?.ToString() == "AIPhaseTransitionTakedown")
                {
                    Console.WriteLine("  already patched (AIPhaseTransitionTakedown export present) - abort");
                    return -1;
                }
            }
        }

        // find target MiniBoss CDO class import (BlueprintGeneratedClass.<TargetDb>_C)
        int miniBossImport = -1;
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var imp = asset.Imports[i];
            if (imp.ClassName.Value?.ToString() == "BlueprintGeneratedClass"
                && imp.ObjectName.Value?.ToString() == targetDb + "_C")
            { miniBossImport = i; break; }
        }
        if (miniBossImport < 0) { Console.WriteLine($"  MiniBoss CDO import not found: {targetDb}_C"); return -1; }

        // clone context: grunt -> target; memoize the LastMan class import (retarget -> MiniBoss CDO).
        // m_SyncHitActionLauncher is NOT memoized: Remap -> EnsureImport clones
        // BP_Grunt_LastManSynchronizedAttacksHitAction_C (+ its Package outer) into the dst,
        // matching vanilla where every takedown transition has a non-null launcher.
        var ctx = new Cloner.Context(grunt, asset);
        ctx.MemoImport(gruntLastManClassImport, FPackageIndex.FromImport(miniBossImport));

        // 1. takedown export shell (empty props), outer = target scenario export[0]
        string guidHex = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var newExport = (NormalExport)Cloner.MemberwiseCloneLocal(gruntTakedown);
        newExport.Asset = asset;
        var classIdx = ctx.Remap(gruntTakedown.ClassIndex);
        var templateIdx = ctx.RemapTolerant(gruntTakedown.TemplateIndex);
        if (ctx.Failure != null || classIdx == null || templateIdx == null)
        { Console.WriteLine("  clone failed: takedown class/template imports (" + ctx.Failure + ")"); return -1; }
        newExport.ClassIndex = classIdx;
        newExport.OuterIndex = FPackageIndex.FromExport(0);
        newExport.SuperIndex = new FPackageIndex(0);
        newExport.TemplateIndex = templateIdx;
        newExport.ObjectName = FName.FromString(asset, "Takendown - " + guidHex);
        newExport.SerialSize = 0;
        newExport.SerialOffset = 0;
        newExport.Data = new List<PropertyData>();
        Cloner.ResetAlreadySerialized(newExport);
        asset.Exports.Add(newExport);
        int newExportIdx = asset.Exports.Count - 1;
        ctx.MemoExport(gruntTakedownExport, FPackageIndex.FromExport(newExportIdx));

        // 2. Last Man phase node (clone grunt node[1]; archetype remapped via memo to target MiniBoss CDO)
        if (Cloner.CloneValue(gruntLastManNode, ctx) is not StructPropertyData newNode || newNode.Value == null)
        { Console.WriteLine("  clone failed: last man node (" + ctx.Failure + ")"); return -1; }
        SetChildObject(newNode, "m_PhaseArchetype", FPackageIndex.FromImport(miniBossImport));
        var guidProp = FindStruct(newNode, "m_Guid");
        if (guidProp != null)
        {
            var g = guidProp.Value?.OfType<GuidPropertyData>().FirstOrDefault();
            if (g != null) g.Value = Guid.NewGuid();
            else { Console.WriteLine("  shape check failed: m_Guid.m_Guid not GuidPropertyData"); return -1; }
        }
        else { Console.WriteLine("  shape check failed: no m_Guid struct"); return -1; }
        var nameProp = FindProp(newNode, "m_Name") as NamePropertyData;
        if (nameProp != null) nameProp.Value = FName.FromString(asset, "Last Man");

        var newNodeList = new List<PropertyData>(nodes.Value) { newNode };
        nodes.Value = newNodeList.ToArray();
        int newNodeIdx = newNodeList.Count - 1;

        // 3. takedown transition entry on the fight node
        if (Cloner.CloneValue(gruntTransition, ctx) is not StructPropertyData newTrans || newTrans.Value == null)
        { Console.WriteLine("  clone failed: takedown transition (" + ctx.Failure + ")"); return -1; }
        SetChildInt(newTrans, "m_iChildNodeIndex", newNodeIdx);
        SetChildBool(newTrans, "m_bNeedsLastManGaugeFull", gaugeFull);
        // m_SyncHitActionLauncher kept from grunt clone (remapped via EnsureImport -> cloned class import)
        // m_TransitionObject remapped via export memo -> newExportIdx; verify:
        var transObj = FindProp(newTrans, "m_TransitionObject") as ObjectPropertyData;
        if (transObj?.Value == null || transObj.Value.Index != newExportIdx + 1)
        { Console.WriteLine($"  verify failed: m_TransitionObject -> {transObj?.Value?.Index} expected {newExportIdx + 1}"); return -1; }

        var fightTransitions = FindArray(fightNode, "m_PhaseTransitions");
        if (fightTransitions == null)
        { Console.WriteLine("  shape check failed: fight node has no m_PhaseTransitions"); return -1; }
        var oldTrans = fightTransitions.Value ?? Array.Empty<PropertyData>();
        var newTransList = new List<PropertyData>(oldTrans) { newTrans };
        fightTransitions.Value = newTransList.ToArray();

        asset.Write(stagedUasset);

        // 4. reopen + verify
        var post = new UAsset(stagedUasset, Eng, null, CustomSerializationFlags.None);
        if (!Verify(post, targetDb, gaugeFull, newNodeIdx, newExportIdx, fightIdx))
        { Console.WriteLine("  VERIFY FAILED after write"); return -1; }
        Console.WriteLine($"  ok: fight node[{fightIdx}] + takedown -> node[{newNodeIdx}] {targetDb}_C, export[{newExportIdx}] gauge={gaugeFull}");
        return 1;
    }

    static bool Verify(UAsset post, string targetDb, bool gaugeFull, int expectNodeIdx, int expectExportIdx, int expectFightIdx)
    {
        if (post.Exports.Count <= expectExportIdx) return false;
        var te = post.Exports[expectExportIdx];
        if (te is not NormalExport tne || tne.Data == null || tne.Data.Count != 0) return false;
        var tcls = te.ClassIndex;
        if (tcls.Index >= 0) return false;
        var tclsImp = post.Imports[-tcls.Index - 1];
        if (tclsImp.ObjectName.Value?.ToString() != "AIPhaseTransitionTakedown") return false;
        if (te.OuterIndex.Index != 1) return false;

        var nodes = FindArray(post.Exports[0], "m_PhaseNodes");
        if (nodes?.Value == null || nodes.Value.Length != expectNodeIdx + 1) return false;
        if (nodes.Value[expectNodeIdx] is not StructPropertyData ln) return false;
        var arch = FindProp(ln, "m_PhaseArchetype") as ObjectPropertyData;
        if (arch?.Value == null || arch.Value.Index >= 0) return false;
        var archImp = post.Imports[-arch.Value.Index - 1];
        if (archImp.ObjectName.Value?.ToString() != targetDb + "_C") return false;
        var lmName = FindProp(ln, "m_Name") as NamePropertyData;
        if (lmName?.Value.Value?.ToString() != "Last Man") return false;

        if (post.Exports[0] is not NormalExport root || root.Data == null) return false;
        var rootNodes = FindArray(root, "m_PhaseNodes");
        if (rootNodes?.Value == null || expectFightIdx >= rootNodes.Value.Length) return false;
        if (rootNodes.Value[expectFightIdx] is not StructPropertyData fn) return false;
        var fts = FindArray(fn, "m_PhaseTransitions");
        if (fts?.Value == null || fts.Value.Length == 0) return false;
        if (fts.Value[^1] is not StructPropertyData last) return false;
        var to = FindProp(last, "m_TransitionObject") as ObjectPropertyData;
        if (to?.Value?.Index != expectExportIdx + 1) return false;
        var idx = FindProp(last, "m_iChildNodeIndex") as IntPropertyData;
        if (idx?.Value != expectNodeIdx) return false;
        var gauge = FindProp(last, "m_bNeedsLastManGaugeFull") as BoolPropertyData;
        if (gauge?.Value != gaugeFull) return false;
        var sync = FindProp(last, "m_SyncHitActionLauncher") as ObjectPropertyData;
        if (sync?.Value == null || sync.Value.Index >= 0) return false;
        var syncImp = post.Imports[-sync.Value.Index - 1];
        if (syncImp.ObjectName.Value?.ToString() != "BP_Grunt_LastManSynchronizedAttacksHitAction_C") return false;
        var rh = FindProp(last, "m_bResetHealth") as BoolPropertyData;
        var rs = FindProp(last, "m_bResetStructure") as BoolPropertyData;
        var rd = FindProp(last, "m_bResetSuperDizzy") as BoolPropertyData;
        if (rh?.Value != true || rs?.Value != true || rd?.Value != true) return false;
        return true;
    }

    // ---- property helpers ----

    static PropertyData? FindProp(UAssetAPI.ExportTypes.Export exp, string name)
    {
        if (exp is not NormalExport ne || ne.Data == null) return null;
        return ne.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == name);
    }

    static PropertyData? FindProp(StructPropertyData s, string name)
    {
        return s.Value?.FirstOrDefault(p => p.Name?.Value?.ToString() == name);
    }

    static ArrayPropertyData? FindArray(UAssetAPI.ExportTypes.Export exp, string name) => FindProp(exp, name) as ArrayPropertyData;
    static ArrayPropertyData? FindArray(StructPropertyData s, string name) => FindProp(s, name) as ArrayPropertyData;
    static StructPropertyData? FindStruct(StructPropertyData s, string name) => FindProp(s, name) as StructPropertyData;

    static void SetChildObject(StructPropertyData s, string name, FPackageIndex v)
    {
        if (FindProp(s, name) is ObjectPropertyData op) op.Value = v;
        else throw new Exception($"missing ObjectPropertyData {name}");
    }

    static void SetChildInt(StructPropertyData s, string name, int v)
    {
        if (FindProp(s, name) is IntPropertyData ip) ip.Value = v;
        else throw new Exception($"missing IntPropertyData {name}");
    }

    static void SetChildBool(StructPropertyData s, string name, bool v)
    {
        if (FindProp(s, name) is BoolPropertyData bp) bp.Value = v;
        else throw new Exception($"missing BoolPropertyData {name}");
    }

    // ============================== pak ==============================

    static int Pak(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("pak needs <stagingDir> <pakName> <contentRootForSig>"); return 2; }
        string stagingDir = Path.GetFullPath(args[0]); // contains Sifu/Content/...
        string pakName = args[1];
        string contentRoot = Path.GetFullPath(args[2]);

        var stagedContent = Path.Combine(stagingDir, "Sifu", "Content");
        if (!Directory.Exists(stagedContent)) { Console.WriteLine("staging content missing: " + stagedContent); return 1; }
        var files = Directory.GetFiles(stagedContent, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) { Console.WriteLine("staging empty"); return 1; }

        if (!TryFindUnrealPak(out var pakExe, out var pakWd))
        {
            Console.WriteLine("UnrealPak.exe not found (expected at <repo>\\tools\\ue4\\UnrealPak\\UnrealPak.exe)");
            return 1;
        }
        Console.WriteLine("UnrealPak: " + pakExe);

        var pakPath = Path.Combine(stagingDir, pakName);
        var filelistPath = Path.Combine(Path.GetTempPath(), "LastManPakFilelist.txt");
        var lines = new List<string>(files.Count);
        foreach (var src in files)
        {
            var rel = Path.GetRelativePath(pakWd, src);
            if (Path.IsPathRooted(rel)) rel = src;
            rel = rel.Replace('\\', '/');
            string canonical;
            try { canonical = Path.GetFullPath(src); } catch { canonical = src; }
            if (canonical.Length == 260) { Console.WriteLine("dead-zone source path: " + canonical); return 1; }
            var relUnderContent = Path.GetRelativePath(stagedContent, src).Replace('\\', '/');
            lines.Add($"\"{rel}\" \"../../../Sifu/Content/{relUnderContent}\"");
        }
        File.WriteAllText(filelistPath, string.Join("\n", lines));
        Console.WriteLine("filelist:");
        foreach (var l in lines) Console.WriteLine("  " + l);

        if (File.Exists(pakPath)) File.Delete(pakPath);

        var psi = new ProcessStartInfo
        {
            FileName = pakExe,
            Arguments = $"\"{pakPath}\" -create=\"{filelistPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = pakWd
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        Console.WriteLine(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) Console.WriteLine(stderr);
        Console.WriteLine("exit code: " + proc.ExitCode);
        if (proc.ExitCode != 0) { Console.WriteLine("UnrealPak failed"); return 1; }

        var skipped = new List<string>();
        foreach (Match m in Regex.Matches(stdout + "\n" + stderr, "(?:Missing file|Unable to create file) \"([^\"]+)\""))
            if (!skipped.Contains(m.Groups[1].Value)) skipped.Add(m.Groups[1].Value);
        var addedMatch = Regex.Match(stdout, @"Added (\d+) files, \d+ bytes total");
        int uniqueDests = lines.Count;
        int added = addedMatch.Success ? int.Parse(addedMatch.Groups[1].Value) : -1;
        if (skipped.Count > 0 || !addedMatch.Success || added != uniqueDests)
        {
            Console.WriteLine($"incomplete pak: Added={added} expected={uniqueDests} skipped={skipped.Count}");
            foreach (var s in skipped) Console.WriteLine("  skipped: " + s);
            try { if (File.Exists(pakPath)) File.Delete(pakPath); } catch { }
            return 1;
        }

        var sigSource = FindSig(pakExe, contentRoot);
        if (sigSource != null)
        {
            File.Copy(sigSource, Path.ChangeExtension(pakPath, ".sig"), true);
            Console.WriteLine("sig: " + sigSource);
        }
        else Console.WriteLine("sig: not found (pak will be unsigned)");

        Console.WriteLine();
        Console.WriteLine("PAK READY: " + pakPath);
        if (File.Exists(Path.ChangeExtension(pakPath, ".sig"))) Console.WriteLine("SIG READY: " + Path.ChangeExtension(pakPath, ".sig"));
        return 0;
    }

    static bool TryFindUnrealPak(out string pakExe, out string pakWd)
    {
        pakExe = "";
        pakWd = "";
        var walk = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (walk != null)
        {
            var p = Path.Combine(walk.FullName, "tools", "ue4", "UnrealPak", "UnrealPak.exe");
            if (File.Exists(p)) { pakExe = p; pakWd = Path.GetDirectoryName(p)!; return true; }
            walk = walk.Parent;
        }
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir.Trim(), "UnrealPak.exe");
            if (File.Exists(p)) { pakExe = p; pakWd = Path.GetDirectoryName(p)!; return true; }
        }
        return false;
    }

    static string? FindSig(string pakExe, string contentRoot)
    {
        var candidates = new List<string>
        {
            Path.Combine(contentRoot, "Paks", "pakchunk0-WindowsNoEditor.sig"),
            @"C:\Program Files\Epic Games\Sifu\Sifu\Content\Paks\pakchunk0-WindowsNoEditor.sig",
            Path.Combine(Path.GetDirectoryName(pakExe)!, "pakchunk0-WindowsNoEditor.sig"),
        };
        var walk2 = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (walk2 != null)
        {
            foreach (var rel in new[]
            {
                Path.Combine("bin", "Debug", "net10.0-windows", "tools", "ue4", "UnrealPak", "pakchunk0-WindowsNoEditor.sig"),
                Path.Combine("bin", "Release", "net10.0-windows", "tools", "ue4", "UnrealPak", "pakchunk0-WindowsNoEditor.sig"),
            })
            {
                candidates.Add(Path.Combine(walk2.FullName, rel));
            }
            if (walk2.Name == "SifuMovesetEditor") break;
            walk2 = walk2.Parent;
        }
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }
}
