using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace SifuMovesetEditor;

/// <summary>
/// App-side port of tools/lastman_poc: appends a vanilla Grunt "Last Man" takedown transition
/// to a scenario asset, retargeting the phase archetype at a chosen moveset DB CDO. Proven
/// in-game by LastMan_B.pak. Template indices are resolved by name (not hardcoded), the
/// launcher class import is cloned so m_SyncHitActionLauncher is never null, and every write
/// is re-verified after reopen.
/// </summary>
public static class PhaseSwapPatcher
{
    public const string GruntTemplateRel = "DB/AI/Archetypes/Grunt/_Base/Grunt_Scenario_Base";

    private static readonly EngineVersion Eng = EngineVersion.VER_UE4_26;

    public enum Outcome { Patched, SkippedAlreadyPhasing, Failed }

    /// <summary>Grunt Last Man donor, loaded once per export. Opaque handle for PatchScenario.</summary>
    public sealed class TemplateBundle
    {
        public UAsset Grunt = null!;
        public ArrayPropertyData Nodes = null!;
        public StructPropertyData FightNode = null!;
        public StructPropertyData Transition = null!;
        public StructPropertyData LastManNode = null!;
        public NormalExport Takedown = null!;
        public int LastManClassImport;
        public int TakedownExport;
    }

    /// <summary>
    /// Loads and shape-checks Grunt_Scenario_Base: export[1] takedown shell, node[0] fight with
    /// a takedown transition entry, node[1] Last Man node, and the BP_Grunt_LastMan_Db_C import.
    /// </summary>
    public static TemplateBundle? LoadTemplate(string contentDir, out string error)
    {
        error = "";
        var path = Path.Combine(contentDir,
            GruntTemplateRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        if (!File.Exists(path)) { error = "grunt template missing: " + path; return null; }

        UAsset grunt;
        try { grunt = new UAsset(path, Eng, null, CustomSerializationFlags.None); }
        catch (Exception ex) { error = "grunt template load failed: " + ex.Message; return null; }

        if (grunt.Exports.Count < 2 || grunt.Exports[1] is not NormalExport takedown)
        { error = "grunt template shape unexpected: no export[1] takedown shell"; return null; }

        var nodes = FindArray(grunt.Exports[0], "m_PhaseNodes");
        if (nodes?.Value == null || nodes.Value.Length < 2)
        { error = "grunt template shape unexpected: m_PhaseNodes < 2"; return null; }
        if (nodes.Value[0] is not StructPropertyData fightNode)
        { error = "grunt template shape unexpected: node[0] not struct"; return null; }

        var transitions = FindArray(fightNode, "m_PhaseTransitions");
        if (transitions?.Value == null || transitions.Value.Length < 1
            || transitions.Value[0] is not StructPropertyData transition)
        { error = "grunt template shape unexpected: no takedown transition entry"; return null; }
        if (nodes.Value[1] is not StructPropertyData lastManNode)
        { error = "grunt template shape unexpected: node[1] not struct"; return null; }

        int lastManClassImport = -1;
        for (int i = 0; i < grunt.Imports.Count; i++)
        {
            var im = grunt.Imports[i];
            if (im.ClassName?.Value?.ToString() == "BlueprintGeneratedClass"
                && im.ObjectName?.Value?.ToString()!.StartsWith("BP_Grunt_LastMan_Db", StringComparison.Ordinal) == true)
            { lastManClassImport = i; break; }
        }
        if (lastManClassImport < 0)
        { error = "grunt template shape unexpected: no BP_Grunt_LastMan_Db_C import"; return null; }

        return new TemplateBundle
        {
            Grunt = grunt,
            Nodes = nodes,
            FightNode = fightNode,
            Transition = transition,
            LastManNode = lastManNode,
            Takedown = takedown,
            LastManClassImport = lastManClassImport,
            TakedownExport = 1,
        };
    }

    /// <summary>
    /// Appends the Last Man node + takedown transition to a staged scenario and re-verifies.
    /// <paramref name="targetDbRel"/> is the content rel of the moveset DB whose CDO import
    /// must already exist in the scenario (per-level pairing, e.g. FD Advanced H2 imports
    /// MiniBoss_H2). Gauge is always off (proven LastMan_B behavior).
    /// </summary>
    public static Outcome PatchScenario(string stagedUasset, TemplateBundle template,
        string targetDbRel, out string error)
    {
        error = "";
        UAsset asset;
        try { asset = new UAsset(stagedUasset, Eng, null, CustomSerializationFlags.None); }
        catch (Exception ex) { error = "load failed: " + ex.Message; return Outcome.Failed; }

        // already phases? vanilla Last Man scenarios (Grunt roster, bosses) stay untouched
        if (HasTakedownTransition(asset))
        { error = "already contains an AIPhaseTransitionTakedown export"; return Outcome.SkippedAlreadyPhasing; }

        var rootPhase = FindProp(asset.Exports[0], "m_rootPhase") as ObjectPropertyData;
        var nodes = FindArray(asset.Exports[0], "m_PhaseNodes");
        if (rootPhase?.Value == null || nodes?.Value == null || nodes.Value.Length == 0)
        { error = "shape check failed: no m_rootPhase / m_PhaseNodes"; return Outcome.Failed; }

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
        if (fightNode == null) { error = "shape check failed: no struct phase node"; return Outcome.Failed; }

        string targetDbLeaf = DbLeafName(targetDbRel);
        int miniBossImport = -1;
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var imp = asset.Imports[i];
            if (imp.ClassName?.Value?.ToString() == "BlueprintGeneratedClass"
                && imp.ObjectName?.Value?.ToString() == targetDbLeaf + "_C")
            { miniBossImport = i; break; }
        }
        if (miniBossImport < 0)
        { error = $"target CDO import not found: {targetDbLeaf}_C"; return Outcome.Failed; }

        // clone context: grunt -> target; memoize the LastMan class import (retarget -> target CDO).
        // m_SyncHitActionLauncher is NOT memoized: Remap -> EnsureImport clones
        // BP_Grunt_LastManSynchronizedAttacksHitAction_C (+ its Package outer) into the dst,
        // matching vanilla where every takedown transition has a non-null launcher.
        var ctx = new PhaseSwapCloner.Context(template.Grunt, asset);
        ctx.MemoImport(template.LastManClassImport, FPackageIndex.FromImport(miniBossImport));

        // 1. takedown export shell (empty props), outer = target scenario export[0]
        string guidHex = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var newExport = (NormalExport)PhaseSwapCloner.MemberwiseCloneLocal(template.Takedown);
        newExport.Asset = asset;
        var classIdx = ctx.Remap(template.Takedown.ClassIndex);
        var templateIdx = ctx.RemapTolerant(template.Takedown.TemplateIndex);
        if (ctx.Failure != null || classIdx == null || templateIdx == null)
        { error = "clone failed: takedown class/template imports (" + ctx.Failure + ")"; return Outcome.Failed; }
        newExport.ClassIndex = classIdx;
        newExport.OuterIndex = FPackageIndex.FromExport(0);
        newExport.SuperIndex = new FPackageIndex(0);
        newExport.TemplateIndex = templateIdx;
        newExport.ObjectName = FName.FromString(asset, "Takendown - " + guidHex);
        newExport.SerialSize = 0;
        newExport.SerialOffset = 0;
        newExport.Data = new List<PropertyData>();
        PhaseSwapCloner.ResetAlreadySerialized(newExport);
        asset.Exports.Add(newExport);
        int newExportIdx = asset.Exports.Count - 1;
        ctx.MemoExport(template.TakedownExport, FPackageIndex.FromExport(newExportIdx));

        // 2. Last Man phase node (clone grunt node[1]; archetype remapped via memo to target CDO)
        if (PhaseSwapCloner.CloneValue(template.LastManNode, ctx) is not StructPropertyData newNode || newNode.Value == null)
        { error = "clone failed: last man node (" + ctx.Failure + ")"; return Outcome.Failed; }
        SetChildObject(newNode, "m_PhaseArchetype", FPackageIndex.FromImport(miniBossImport));
        var guidProp = FindStruct(newNode, "m_Guid");
        var g = guidProp?.Value?.OfType<GuidPropertyData>().FirstOrDefault();
        if (g == null) { error = "shape check failed: m_Guid.m_Guid not GuidPropertyData"; return Outcome.Failed; }
        g.Value = Guid.NewGuid();
        if (FindProp(newNode, "m_Name") is NamePropertyData nameProp)
            nameProp.Value = FName.FromString(asset, "Last Man");

        var newNodeList = new List<PropertyData>(nodes.Value) { newNode };
        nodes.Value = newNodeList.ToArray();
        int newNodeIdx = newNodeList.Count - 1;

        // 3. takedown transition entry on the fight node (gauge off - proven LastMan_B behavior)
        if (PhaseSwapCloner.CloneValue(template.Transition, ctx) is not StructPropertyData newTrans || newTrans.Value == null)
        { error = "clone failed: takedown transition (" + ctx.Failure + ")"; return Outcome.Failed; }
        SetChildInt(newTrans, "m_iChildNodeIndex", newNodeIdx);
        SetChildBool(newTrans, "m_bNeedsLastManGaugeFull", false);
        // m_SyncHitActionLauncher kept from grunt clone (remapped via EnsureImport -> cloned class import)
        var transObj = FindProp(newTrans, "m_TransitionObject") as ObjectPropertyData;
        if (transObj?.Value == null || transObj.Value.Index != newExportIdx + 1)
        { error = $"verify failed: m_TransitionObject -> {transObj?.Value?.Index} expected {newExportIdx + 1}"; return Outcome.Failed; }

        var fightTransitions = FindArray(fightNode, "m_PhaseTransitions");
        if (fightTransitions == null)
        { error = "shape check failed: fight node has no m_PhaseTransitions"; return Outcome.Failed; }
        var oldTrans = fightTransitions.Value ?? Array.Empty<PropertyData>();
        var newTransList = new List<PropertyData>(oldTrans) { newTrans };
        fightTransitions.Value = newTransList.ToArray();

        try { asset.Write(stagedUasset); }
        catch (Exception ex) { error = "write failed: " + ex.Message; return Outcome.Failed; }

        // 4. reopen + verify
        UAsset post;
        try { post = new UAsset(stagedUasset, Eng, null, CustomSerializationFlags.None); }
        catch (Exception ex) { error = "reopen failed: " + ex.Message; return Outcome.Failed; }
        if (!Verify(post, targetDbLeaf, newNodeIdx, newExportIdx, fightIdx))
        { error = "VERIFY FAILED after write"; return Outcome.Failed; }
        return Outcome.Patched;
    }

    private static bool Verify(UAsset post, string targetDbLeaf, int expectNodeIdx, int expectExportIdx, int expectFightIdx)
    {
        if (post.Exports.Count <= expectExportIdx) return false;
        var te = post.Exports[expectExportIdx];
        if (te is not NormalExport tne || tne.Data == null || tne.Data.Count != 0) return false;
        var tcls = te.ClassIndex;
        if (tcls.Index >= 0) return false;
        if (post.Imports[-tcls.Index - 1].ObjectName.Value?.ToString() != "AIPhaseTransitionTakedown") return false;
        if (te.OuterIndex.Index != 1) return false;

        var nodes = FindArray(post.Exports[0], "m_PhaseNodes");
        if (nodes?.Value == null || nodes.Value.Length != expectNodeIdx + 1) return false;
        if (nodes.Value[expectNodeIdx] is not StructPropertyData ln) return false;
        var arch = FindProp(ln, "m_PhaseArchetype") as ObjectPropertyData;
        if (arch?.Value == null || arch.Value.Index >= 0) return false;
        if (post.Imports[-arch.Value.Index - 1].ObjectName.Value?.ToString() != targetDbLeaf + "_C") return false;
        if (FindProp(ln, "m_Name") is not NamePropertyData lmName
            || lmName.Value.Value?.ToString() != "Last Man") return false;

        if (post.Exports[0] is not NormalExport root || root.Data == null) return false;
        var rootNodes = FindArray(root, "m_PhaseNodes");
        if (rootNodes?.Value == null || expectFightIdx >= rootNodes.Value.Length) return false;
        if (rootNodes.Value[expectFightIdx] is not StructPropertyData fn) return false;
        var fts = FindArray(fn, "m_PhaseTransitions");
        if (fts?.Value == null || fts.Value.Length == 0) return false;
        if (fts.Value[^1] is not StructPropertyData last) return false;
        if ((FindProp(last, "m_TransitionObject") as ObjectPropertyData)?.Value?.Index != expectExportIdx + 1) return false;
        if ((FindProp(last, "m_iChildNodeIndex") as IntPropertyData)?.Value != expectNodeIdx) return false;
        if ((FindProp(last, "m_bNeedsLastManGaugeFull") as BoolPropertyData)?.Value != false) return false;
        var sync = FindProp(last, "m_SyncHitActionLauncher") as ObjectPropertyData;
        if (sync?.Value == null || sync.Value.Index >= 0) return false;
        if (post.Imports[-sync.Value.Index - 1].ObjectName.Value?.ToString() != "BP_Grunt_LastManSynchronizedAttacksHitAction_C") return false;
        if ((FindProp(last, "m_bResetHealth") as BoolPropertyData)?.Value != true) return false;
        if ((FindProp(last, "m_bResetStructure") as BoolPropertyData)?.Value != true) return false;
        if ((FindProp(last, "m_bResetSuperDizzy") as BoolPropertyData)?.Value != true) return false;
        return true;
    }

    /// <summary>True when the asset already carries a Last Man takedown transition export.</summary>
    public static bool HasTakedownTransition(UAsset asset)
    {
        foreach (var p in asset.Exports)
        {
            if (p is not NormalExport || p.ClassIndex.Index >= 0) continue;
            var imp = asset.Imports[-p.ClassIndex.Index - 1];
            if (imp.ClassName.Value?.ToString() == "Class"
                && imp.ObjectName.Value?.ToString() == "AIPhaseTransitionTakedown")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Target-vs-scenario token match: a per-level DB (name has H\d) only pairs with scenarios
    /// carrying the same level token; an arena-only DB (name has Arena, no H\d) only with arena
    /// scenarios; a tokenless DB pairs with every tier scenario of the family.
    /// </summary>
    public static bool ScenarioMatchesTarget(string scenarioRel, string targetDbRel)
    {
        string rel = scenarioRel.ToLowerInvariant();
        string leaf = DbLeafName(targetDbRel).ToLowerInvariant();

        var level = Regex.Match(leaf, @"h\d");
        if (level.Success) return rel.Contains(level.Value);

        if (leaf.Contains("arena")) return rel.Contains("arena");
        return true;
    }

    /// <summary>"DB/.../BP_X_Y" -> "BP_X_Y".</summary>
    public static string DbLeafName(string relOrName)
    {
        var s = relOrName.Replace('\\', '/').TrimEnd('/');
        int idx = s.LastIndexOf('/');
        return idx >= 0 ? s[(idx + 1)..] : s;
    }

    // ---- property helpers ----

    private static PropertyData? FindProp(UAssetAPI.ExportTypes.Export exp, string name)
    {
        if (exp is not NormalExport ne || ne.Data == null) return null;
        return ne.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == name);
    }

    private static PropertyData? FindProp(StructPropertyData s, string name)
        => s.Value?.FirstOrDefault(p => p.Name?.Value?.ToString() == name);

    private static ArrayPropertyData? FindArray(UAssetAPI.ExportTypes.Export exp, string name)
        => FindProp(exp, name) as ArrayPropertyData;
    private static ArrayPropertyData? FindArray(StructPropertyData s, string name)
        => FindProp(s, name) as ArrayPropertyData;
    private static StructPropertyData? FindStruct(StructPropertyData s, string name)
        => FindProp(s, name) as StructPropertyData;

    private static void SetChildObject(StructPropertyData s, string name, FPackageIndex v)
    {
        if (FindProp(s, name) is ObjectPropertyData op) op.Value = v;
        else throw new InvalidOperationException($"missing ObjectPropertyData {name}");
    }

    private static void SetChildInt(StructPropertyData s, string name, int v)
    {
        if (FindProp(s, name) is IntPropertyData ip) ip.Value = v;
        else throw new InvalidOperationException($"missing IntPropertyData {name}");
    }

    private static void SetChildBool(StructPropertyData s, string name, bool v)
    {
        if (FindProp(s, name) is BoolPropertyData bp) bp.Value = v;
        else throw new InvalidOperationException($"missing BoolPropertyData {name}");
    }
}

/// <summary>
/// Cross-asset UAsset deep cloner ported from tools/lastman_poc/Cloner.cs. Retargets FNames
/// into the destination asset's name table and remaps import/export references; used to copy
/// the Grunt Last Man node + transition into a scenario asset.
/// </summary>
public static class PhaseSwapCloner
{
    private static readonly MethodInfo _memberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo _alreadySerializedField =
        typeof(UAssetAPI.ExportTypes.Export).GetField("alreadySerialized", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public sealed class Context
    {
        public readonly UAsset Src;
        public readonly UAsset Dst;
        public string? Failure { get; private set; }
        public bool SrcSameAsDst => _sameAsset;

        private readonly Dictionary<int, FPackageIndex> _exportMemo = new();
        private readonly Dictionary<int, FPackageIndex> _importMemo = new();
        private readonly bool _sameAsset;

        public Context(UAsset src, UAsset dst)
        {
            Src = src;
            Dst = dst;
            _sameAsset = ReferenceEquals(src, dst) || SamePath(src, dst);
        }

        public void Fail(string why) => Failure ??= why;

        public void MemoImport(int srcIdx, FPackageIndex dst) => _importMemo[srcIdx] = dst;
        public void MemoExport(int srcIdx, FPackageIndex dst) => _exportMemo[srcIdx] = dst;

        private static bool SamePath(UAsset a, UAsset b)
        {
            string pa = a.FilePath ?? "";
            string pb = b.FilePath ?? "";
            if (pa.Length == 0 || pb.Length == 0) return false;
            try { pa = Path.GetFullPath(pa); pb = Path.GetFullPath(pb); } catch { }
            return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
        }

        public FPackageIndex? Remap(FPackageIndex? pi)
        {
            if (pi == null) return null;
            if (_sameAsset) return pi;
            if (pi.Index == 0) return new FPackageIndex(0);
            if (pi.Index < 0)
            {
                var imp = EnsureImport(-pi.Index - 1);
                if (imp == null) Fail($"source import {-pi.Index} cannot be re-created");
                return imp ?? new FPackageIndex(0);
            }
            var exp = EnsureExport(pi.Index - 1);
            if (exp == null) Fail($"source export {pi.Index} cannot be cloned");
            return exp ?? new FPackageIndex(0);
        }

        public FPackageIndex? RemapTolerant(FPackageIndex? pi)
        {
            if (pi == null) return new FPackageIndex(0);
            if (_sameAsset) return pi;
            if (pi.Index == 0) return new FPackageIndex(0);
            if (pi.Index < 0) return EnsureImport(-pi.Index - 1) ?? new FPackageIndex(0);
            return EnsureExport(pi.Index - 1) ?? new FPackageIndex(0);
        }

        public FPackageIndex? EnsureImport(int srcIdx)
        {
            if (srcIdx < 0 || srcIdx >= Src.Imports.Count) return null;
            if (_sameAsset) return FPackageIndex.FromImport(srcIdx);
            if (_importMemo.TryGetValue(srcIdx, out var memo)) return memo;

            var imp = Src.Imports[srcIdx];
            string classPackage = imp.ClassPackage?.Value?.ToString() ?? "/Script/CoreUObject";
            string className = imp.ClassName?.Value?.ToString() ?? "Object";
            string objectName = imp.ObjectName?.Value?.ToString() ?? "";
            string packageName = imp.PackageName?.Value?.ToString() ?? "None";
            if (objectName.Length == 0) return null;

            FPackageIndex? outerIdx;
            if (imp.OuterIndex == null || imp.OuterIndex.Index == 0)
            {
                outerIdx = new FPackageIndex(0);
            }
            else if (imp.OuterIndex.Index < 0)
            {
                outerIdx = EnsureImport(-imp.OuterIndex.Index - 1);
                if (outerIdx == null) return null;
            }
            else
            {
                Fail($"import {srcIdx} has an export outer");
                return null;
            }
            FPackageIndex outer = outerIdx;

            for (int i = 0; i < Dst.Imports.Count; i++)
            {
                var d = Dst.Imports[i];
                if ((d.OuterIndex?.Index ?? int.MinValue) != outer.Index) continue;
                if (!string.Equals(d.ObjectName?.Value?.ToString(), objectName, StringComparison.Ordinal)) continue;
                if (!string.Equals(d.ClassName?.Value?.ToString(), className, StringComparison.Ordinal)) continue;
                if (!string.Equals(d.ClassPackage?.Value?.ToString(), classPackage, StringComparison.Ordinal)) continue;
                _importMemo[srcIdx] = FPackageIndex.FromImport(i);
                return _importMemo[srcIdx];
            }

            var add = new UAssetAPI.Import
            {
                ClassPackage = FName.FromString(Dst, classPackage),
                ClassName = FName.FromString(Dst, className),
                ObjectName = FName.FromString(Dst, objectName),
                OuterIndex = outer,
                PackageName = FName.FromString(Dst, packageName)
            };
            int idx = Dst.Imports.Count;
            Dst.Imports.Add(add);
            _importMemo[srcIdx] = FPackageIndex.FromImport(idx);
            return _importMemo[srcIdx];
        }

        public FPackageIndex? EnsureExport(int srcIdx)
        {
            if (srcIdx < 0 || srcIdx >= Src.Exports.Count) return null;
            if (_sameAsset) return FPackageIndex.FromExport(srcIdx);
            if (_exportMemo.TryGetValue(srcIdx, out var memo)) return memo;

            var exp = Src.Exports[srcIdx];
            if (exp is not NormalExport srcExp)
            {
                Fail($"export {srcIdx} ({exp.ObjectName?.Value}) has no property data");
                return null;
            }

            var clone = (NormalExport)CloneExportShell(srcExp, Dst, srcIdx)!;
            if (Failure != null) return null;

            _exportMemo[srcIdx] = FPackageIndex.FromExport(Dst.Exports.Count);
            Dst.Exports.Add(clone);

            var data = new List<PropertyData>(srcExp.Data?.Count ?? 0);
            if (srcExp.Data != null)
            {
                foreach (var p in srcExp.Data)
                {
                    if (CloneValue(p, this) is PropertyData cp) data.Add(cp);
                }
            }
            clone.Data = data;
            if (Failure != null)
            {
                Dst.Exports.RemoveAt(Dst.Exports.Count - 1);
                _exportMemo.Remove(srcIdx);
                return null;
            }
            return _exportMemo[srcIdx];
        }

        internal NormalExport? CloneExportShell(NormalExport srcExp, UAsset dst, int srcIdx)
        {
            var clone = (NormalExport)_memberwiseCloneMethod.Invoke(srcExp, null)!;
            clone.Asset = dst;
            clone.ClassIndex = Remap(srcExp.ClassIndex) ?? new FPackageIndex(0);
            clone.OuterIndex = Remap(srcExp.OuterIndex) ?? new FPackageIndex(0);
            clone.SuperIndex = RemapTolerant(srcExp.SuperIndex);
            clone.TemplateIndex = RemapTolerant(srcExp.TemplateIndex);
            if (Failure != null) return null;
            clone.ObjectName = FName.FromString(dst, UniqueObjectName(srcExp.ObjectName));
            clone.SerialSize = 0;
            clone.SerialOffset = 0;
            _alreadySerializedField.SetValue(clone, false);
            return clone;
        }

        private string UniqueObjectName(FName? srcName)
        {
            string baseName = srcName?.Value?.Value ?? "";
            if (baseName.Length == 0) baseName = "ExportedObject";
            else if (srcName!.Number > 0) baseName = $"{baseName}_{srcName.Number}";
            if (!IsNameTaken(Dst, baseName)) return baseName;
            for (int i = 0; i < 16; i++)
            {
                string candidate = baseName + "_Copy_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
                if (!IsNameTaken(Dst, candidate)) return candidate;
            }
            return baseName + "_Copy_2";
        }

        private static bool IsNameTaken(UAsset asset, string name)
        {
            foreach (var e in asset.Exports)
                if (string.Equals(e.ObjectName?.Value?.ToString(), name, StringComparison.Ordinal))
                    return true;
            foreach (var im in asset.Imports)
                if (string.Equals(im.ObjectName?.Value?.ToString(), name, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }

    internal static object MemberwiseCloneLocal(object src) => _memberwiseCloneMethod.Invoke(src, null)!;

    internal static void ResetAlreadySerialized(object exp) => _alreadySerializedField.SetValue(exp, false);

    internal static object? CloneValue(object? val, Context ctx)
    {
        if (val == null) return null;
        if (val is FName fn) return RetargetName(fn, ctx);
        if (val is FPackageIndex pi) return ctx.Remap(pi);
        if (val is PropertyData pd) return CloneProperty(pd, ctx);
        if (val is FPropertyTypeName tn) return CloneTypeName(tn, ctx);
        if (val is FPropertyTypeNameNode node)
            return new FPropertyTypeNameNode
            {
                Name = RetargetName(node.Name, ctx) ?? node.Name,
                InnerCount = node.InnerCount
            };
        if (val is TMap<PropertyData, PropertyData> map)
        {
            var tm = new TMap<PropertyData, PropertyData>();
            foreach (var kvp in map)
                tm.Add(CloneValue(kvp.Key, ctx) as PropertyData ?? kvp.Key,
                       CloneValue(kvp.Value, ctx) as PropertyData ?? kvp.Value);
            return tm;
        }
        if (val is List<PropertyData> list)
        {
            var nl = new List<PropertyData>(list.Count);
            foreach (var p in list)
                nl.Add(CloneValue(p, ctx) as PropertyData ?? p);
            return nl;
        }
        if (val is List<FPropertyTypeNameNode> nodes)
        {
            var nl = new List<FPropertyTypeNameNode>(nodes.Count);
            foreach (var n in nodes)
                nl.Add(new FPropertyTypeNameNode
                {
                    Name = RetargetName(n.Name, ctx) ?? n.Name,
                    InnerCount = n.InnerCount
                });
            return nl;
        }
        if (val is Array arr)
        {
            var na = Array.CreateInstance(arr.GetType().GetElementType()!, arr.Length);
            for (int i = 0; i < arr.Length; i++)
                na.SetValue(CloneValue(arr.GetValue(i), ctx), i);
            return na;
        }
        return val;
    }

    private static PropertyData CloneProperty(PropertyData src, Context ctx)
    {
        if (!ctx.SrcSameAsDst)
        {
            string? pt = src.PropertyType?.Value;
            if (!string.IsNullOrEmpty(pt)) FName.FromString(ctx.Dst, pt!);
            if (src is UnknownPropertyData unk)
            {
                string? ut = unk.SerializingPropertyType?.Value;
                if (!string.IsNullOrEmpty(ut)) FName.FromString(ctx.Dst, ut!);
            }
        }
        var clone = (PropertyData)_memberwiseCloneMethod.Invoke(src, null)!;
        WalkFields(clone, src.GetType(), ctx);
        return clone;
    }

    private static void WalkFields(object target, Type startType, Context ctx)
    {
        for (var t = startType; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (f.IsStatic) continue;
                object? val;
                try { val = f.GetValue(target); } catch { continue; }
                var nv = CloneValue(val, ctx);
                if (ReferenceEquals(val, nv)) continue;
                f.SetValue(target, nv);
            }
        }
    }

    private static FName? RetargetName(FName? fn, Context ctx)
    {
        if (fn == null) return null;
        string? s = fn.Value?.Value;
        if (s == null) return fn;
        if (ctx.SrcSameAsDst) return fn;
        return new FName(ctx.Dst, s, fn.Number);
    }

    private static FPropertyTypeName CloneTypeName(FPropertyTypeName tn, Context ctx)
    {
        var nodes = new List<FPropertyTypeNameNode>(tn.Nodes?.Count ?? 0);
        if (tn.Nodes != null)
        {
            foreach (var n in tn.Nodes)
                nodes.Add(new FPropertyTypeNameNode
                {
                    Name = RetargetName(n.Name, ctx) ?? n.Name,
                    InnerCount = n.InnerCount
                });
        }
        return new FPropertyTypeName(nodes, tn.ShouldSerializeNodes);
    }
}
