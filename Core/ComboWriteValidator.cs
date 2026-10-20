using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace SifuMovesetEditor;

/// <summary>
/// Post-write gate for combo-tree files: reparse the staged .uasset with UAssetAPI and
/// assert the node array survived the round-trip and that every file-local index ref
/// (m_TargetNodes values, m_NodeRedirect, condition per-value maps, m_DefaultAttacks,
/// m_ConditionInstance) stays inside the written node count / index tables. A failed
/// file is deleted so a corrupt tree can never ship - the game then simply keeps the
/// previous staged copy (or vanilla).
/// </summary>
public static class ComboWriteValidator
{
    /// <summary>
    /// Validates a just-written combo file against the in-memory asset it came from.
    /// Assets with no m_Nodes array are not combo trees and pass trivially.
    /// </summary>
    public static bool Validate(string uassetPath, UAsset written, string tag)
    {
        int expected = CountNodes(written);
        if (expected < 0) return true;

        try
        {
            if (!File.Exists(uassetPath))
            {
                Fail(tag, uassetPath, $"file missing after write (expected {expected} nodes)");
                return false;
            }

            var check = new UAsset(uassetPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            int actual = CountNodes(check);
            if (actual != expected)
            {
                Fail(tag, uassetPath, $"node count {actual} != written {expected}");
                return false;
            }

            var errors = new List<string>();
            int exportCount = check.Exports.Count;
            int importCount = check.Imports.Count;
            foreach (var export in check.Exports)
            {
                // BP instances (condition/action/event clones) must outer to an export -
                // typically the combo object. A package outer (0) is what the game
                // deadlocks on ("Missing Dependency ... waiting for serialization").
                if (export.ClassIndex != null && export.ClassIndex.Index < 0
                    && -export.ClassIndex.Index - 1 < importCount)
                {
                    var cls = check.Imports[-export.ClassIndex.Index - 1];
                    if (string.Equals(cls.ClassName?.Value?.ToString(), "BlueprintGeneratedClass",
                            StringComparison.Ordinal))
                    {
                        int oi = export.OuterIndex?.Index ?? 0;
                        if (oi <= 0 || oi > exportCount)
                            errors.Add($"{export.ObjectName?.Value?.ToString() ?? "export"}: "
                                + $"BP instance outer {oi} (must be an export, {exportCount} present)");
                    }
                }
                if (export is NormalExport ne && ne.Data != null)
                    WalkProps(ne.Data, actual, errors, export.ObjectName?.Value?.ToString() ?? "export",
                        exportCount, importCount);
            }

            // Preload slice integrity: an export's Class/Template (and any import Super)
            // must sit inside its own sc/cc slices - the class import create-arc is what
            // UE's event-driven loader orders creation by; a donor-indexed slice ships
            // the wrong arcs and races ("Missing Dependency ... waiting for serialization"
            // -> fatal AsyncLoading.cpp:2752). Every dep must also be in range.
            CheckPreloadSlices(check, errors);

            if (errors.Count > 0)
            {
                Fail(tag, uassetPath, $"{errors.Count} out-of-range index ref(s): {string.Join("; ", errors.Take(8))}");
                return false;
            }

            ErrorLog.Write("EXPORT", new Exception($"  VALIDATED: {tag} {expected} nodes, all index refs in range"));
            return true;
        }
        catch (Exception ex)
        {
            Fail(tag, uassetPath, $"reparse failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Preload slice integrity per export. On reparse only the 4 dependency lists are
    /// reachable (fed/size tail fields are internal to UAssetAPI); the writer rebuilds
    /// fed/sizes from the lists, so contiguity is structural - what can still go wrong
    /// is a donor-indexed slice (the AssetCloner bug): the class/template import then
    /// names a different import than the destination's, UE builds no create-arc for the
    /// clone, and loading fatals ("Missing Dependency ... waiting for serialization").
    /// Empty slices (fed=-1 on disk) are allowed - other exports' arcs cover the shared
    /// imports - but a non-empty slice MUST contain the export's own Class/Template
    /// (and import Super), and every dep must be in range.
    /// </summary>
    private static void CheckPreloadSlices(UAsset asset, List<string> errors)
    {
        int importCount = asset.Imports.Count;
        int exportCount = asset.Exports.Count;
        foreach (var export in asset.Exports)
        {
            string name = export.ObjectName?.Value?.ToString() ?? "export";
            var slice = new HashSet<int>();
            CollectSlice(export.SerializationBeforeSerializationDependencies, "sc");
            CollectSlice(export.CreateBeforeSerializationDependencies, "cc");
            CollectSlice(export.SerializationBeforeCreateDependencies, "scc");
            CollectSlice(export.CreateBeforeCreateDependencies, "ccc");
            if (slice.Count == 0) continue;

            CheckArc(export.ClassIndex?.Index ?? 0, "ClassIndex");
            CheckArc(export.TemplateIndex?.Index ?? 0, "TemplateIndex");
            CheckArc(export.SuperIndex?.Index ?? 0, "SuperIndex");

            void CollectSlice(List<FPackageIndex>? deps, string label)
            {
                if (deps == null) return;
                foreach (var dep in deps)
                {
                    int v = dep.Index;
                    bool ok = v < 0 ? -v - 1 < importCount : v > 0 && v <= exportCount;
                    if (!ok)
                        errors.Add($"{name}: {label} dependency {v} out of range "
                            + $"(imports {importCount}, exports {exportCount})");
                    slice.Add(v);
                }
            }

            void CheckArc(int idx, string label)
            {
                if (idx < 0 && !slice.Contains(idx))
                    errors.Add($"{name}: {label} {idx} missing from preload slice "
                        + "(class/template create-arc - loader would race the clone)");
            }
        }
    }

    private static void Fail(string tag, string path, string why)
    {
        ErrorLog.Write("EXPORT", new Exception(
            $"  VALIDATE FAIL [{tag}]: {why} - staged file removed"));
        try { File.Delete(path); } catch { }
        try
        {
            var uexp = Path.ChangeExtension(path, ".uexp");
            if (File.Exists(uexp)) File.Delete(uexp);
        }
        catch { }
    }

    private static int CountNodes(UAsset asset)
    {
        foreach (var export in asset.Exports)
        {
            if (export is not NormalExport ne || ne.Data == null) continue;
            var nodes = ne.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Nodes");
            if (nodes?.Value != null) return nodes.Value.Length;
        }
        return -1;
    }

    private static void WalkProps(List<PropertyData> props, int nodeCount, List<string> errors, string where,
        int exportCount, int importCount)
    {
        foreach (var p in props)
        {
            switch (p)
            {
                case MapPropertyData mp when mp.Name?.Value?.ToString() == "m_TargetNodes":
                    if (mp.Value != null)
                        foreach (var kvp in mp.Value)
                            CheckValue(kvp.Value, nodeCount, false, where, "m_TargetNodes", errors);
                    break;
                case MapPropertyData mp2 when mp2.Name?.Value?.ToString() == "m_PerConditionValueToNodeMap":
                    if (mp2.Value != null)
                        foreach (var kvp in mp2.Value)
                        {
                            if (kvp.Value is StructPropertyData cond && cond.Value != null)
                                foreach (var inner in cond.Value.OfType<MapPropertyData>())
                                    if (inner.Value != null)
                                        foreach (var iv in inner.Value)
                                            CheckValue(iv.Value, nodeCount, false, where, "m_PerConditionValueToNodeMap", errors);
                        }
                    break;
                case MapPropertyData mp3:
                    if (mp3.Value != null)
                        foreach (var kvp in mp3.Value)
                            if (kvp.Value is PropertyData child && child is StructPropertyData or MapPropertyData or ArrayPropertyData)
                                WalkOne(child, nodeCount, errors, where, exportCount, importCount);
                    break;
                case IntPropertyData ip when ip.Name?.Value?.ToString() == "m_NodeRedirect":
                    CheckValue(ip, nodeCount, true, where, "m_NodeRedirect", errors);
                    break;
                case ObjectPropertyData op when op.Name?.Value?.ToString() == "m_DefaultClassObject":
                    if (op.Value != null && op.Value.Index > 0)
                        errors.Add($"{where}.m_DefaultClassObject -> export {op.Value.Index} (class refs must be imports)");
                    break;
                case ObjectPropertyData cop when cop.Name?.Value?.ToString() == "m_ConditionInstance":
                    if (cop.Value != null)
                    {
                        int ci = cop.Value.Index;
                        bool ok = ci > 0 ? ci <= exportCount : ci >= -importCount;
                        if (!ok)
                            errors.Add($"{where}.m_ConditionInstance -> index {ci} (exports {exportCount}, imports {importCount})");
                    }
                    break;
                case ArrayPropertyData ap when ap.Name?.Value?.ToString() == "m_DefaultAttacks":
                    if (ap.Value != null)
                        foreach (var el in ap.Value)
                            CheckValue(el, nodeCount, true, where, "m_DefaultAttacks", errors);
                    break;
                case StructPropertyData sp when sp.Value != null:
                    WalkProps(sp.Value, nodeCount, errors, where, exportCount, importCount);
                    break;
                case ArrayPropertyData ap2 when ap2.Value != null:
                    foreach (var el in ap2.Value)
                        WalkOne(el, nodeCount, errors, where, exportCount, importCount);
                    break;
            }
        }
    }

    private static void WalkOne(PropertyData p, int nodeCount, List<string> errors, string where,
        int exportCount, int importCount)
    {
        switch (p)
        {
            case StructPropertyData sp when sp.Value != null:
                WalkProps(sp.Value, nodeCount, errors, where, exportCount, importCount);
                break;
            case ArrayPropertyData ap when ap.Value != null:
                foreach (var el in ap.Value)
                    WalkOne(el, nodeCount, errors, where, exportCount, importCount);
                break;
            case MapPropertyData mp when mp.Value != null:
                foreach (var kvp in mp.Value)
                    WalkOne(kvp.Value, nodeCount, errors, where, exportCount, importCount);
                break;
        }
    }

    private static void CheckValue(PropertyData value, int nodeCount, bool allowNegativeOne, string where, string field, List<string> errors)
    {
        int? v = value switch
        {
            IntPropertyData ip => ip.Value,
            BytePropertyData bp => bp.Value,
            _ => null
        };
        if (v == null) return;
        bool ok = allowNegativeOne ? v.Value == -1 || (v.Value >= 0 && v.Value < nodeCount)
                                   : v.Value >= 0 && v.Value < nodeCount;
        if (!ok)
            errors.Add($"{where}.{field}={v.Value} (count {nodeCount})");
    }
}
