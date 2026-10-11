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
                if (export is NormalExport ne && ne.Data != null)
                    WalkProps(ne.Data, actual, errors, export.ObjectName?.Value?.ToString() ?? "export",
                        exportCount, importCount);
            }

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
