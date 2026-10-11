using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace SifuMovesetEditor;

/// <summary>
/// One cross-file condition class available for "Add condition": ClassName is the
/// export's class import name (native ComboAICondition* or a BP_*_C generated class),
/// SourceComboRel is the Content-relative combo file holding a representative instance
/// and SourceExportIndex its 0-based export index, and Params mirrors the same dotted
/// paths CollectFromTags emits (UAssetAPI walk) so edits apply through
/// TrySetConditionParam after the clone lands.
/// </summary>
public class ConditionInfo
{
    public string ClassName { get; set; } = "";
    public string SourceComboRel { get; set; } = "";
    public int SourceExportIndex { get; set; } = -1;
    public List<ConditionParamInfo> Params { get; set; } = new();
    // m_TargetNodes byte keys of the representative row referencing this condition -
    // the outcome keyset a cloned row should carry. Raw (may include 255); consumers
    // normalize. Empty when the source row's map could not be read.
    public List<byte> ExampleKeys { get; set; } = new();
}

/// <summary>
/// Scans every *Combo* asset under DB for condition instances referenced by transition
/// rows (m_ConditionInstance export hops), deduplicating by class with a preference for
/// non-arena files - the same scan shape as ActionNodeCatalog, run on demand by the
/// branch-chances dialog and cached by the caller.
/// </summary>
public static class ConditionCatalog
{
    public static List<ConditionInfo> Scan(string contentDir, Action<string>? progress = null)
    {
        var results = new List<ConditionInfo>();
        if (string.IsNullOrEmpty(contentDir)) return results;
        var dbRoot = Path.Combine(contentDir, "DB");
        if (!Directory.Exists(dbRoot)) return results;

        var comboFiles = Directory.GetFiles(dbRoot, "*.uasset", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).Contains("Combo", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (comboFiles.Count == 0) return results;

        var best = new Dictionary<string, (ConditionInfo info, int penalty)>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < comboFiles.Count; i++)
        {
            if ((i & 7) == 0)
                progress?.Invoke($"Scanning combo files... {i + 1}/{comboFiles.Count}");
            string file = comboFiles[i];
            try
            {
                var asset = new UAsset(file, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
                string rel = Path.GetRelativePath(contentDir, file).Replace('\\', '/');
                if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) rel = rel[..^7];
                int penalty = IsArena(rel) ? 1 : 0;

                var seenInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (condIdx, keys) in ConditionRowRefs(asset))
                {
                    if (condIdx < 0 || condIdx >= asset.Exports.Count) continue;
                    if (asset.Exports[condIdx] is not NormalExport ne || ne.Data == null) continue;
                    string cls = ExportClassName(asset, condIdx);
                    if (cls.Length == 0 || !seenInFile.Add(cls)) continue;
                    if (!best.TryGetValue(cls, out var cur) || penalty < cur.penalty)
                    {
                        best[cls] = (new ConditionInfo
                        {
                            ClassName = cls,
                            SourceComboRel = rel,
                            SourceExportIndex = condIdx,
                            Params = CollectParams(asset, ne.Data),
                            ExampleKeys = keys
                        }, penalty);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Write("COND-CATALOG", new Exception($"scan failed for {Path.GetFileName(file)}: {ex.Message}"));
            }
        }

        foreach (var info in best.Values.Select(v => v.info)
                     .OrderBy(v => v.ClassName, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(info);
        }
        progress?.Invoke($"Found {results.Count} condition classes");
        return results;
    }

    /// <summary>0-based export indices of every condition instance referenced by a
    /// transition row in this asset, paired with that row's m_TargetNodes byte keys
    /// (duplicates included - callers dedupe by class).</summary>
    private static IEnumerable<(int CondIdx, List<byte> Keys)> ConditionRowRefs(UAsset asset)
    {
        var nodes = FindNodesArray(asset);
        if (nodes?.Value == null) yield break;
        foreach (var node in nodes.Value)
        {
            if (node is not StructPropertyData ns || ns.Value == null) continue;
            var trans = ns.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions")?.Value
                ?.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions");
            if (trans?.Value == null) continue;
            foreach (var row in trans.Value)
            {
                if (row is not StructPropertyData rs || rs.Value == null) continue;
                var cond = rs.Value.OfType<ObjectPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ConditionInstance");
                if (cond?.Value == null || cond.Value.Index <= 0) continue;
                yield return (cond.Value.Index - 1, RowTargetKeys(rs));
            }
        }
    }

    /// <summary>Byte keys of a transition row's m_TargetNodes map in iteration order.</summary>
    private static List<byte> RowTargetKeys(StructPropertyData row)
    {
        var keys = new List<byte>();
        var map = row.Value?.OfType<MapPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_TargetNodes");
        if (map?.Value == null) return keys;
        foreach (var kvp in map.Value)
        {
            byte k = kvp.Key is BytePropertyData bb ? bb.Value
                : kvp.Key is IntPropertyData ib ? (byte)ib.Value
                : (byte)255;
            if (!keys.Contains(k)) keys.Add(k);
        }
        return keys;
    }

    private static ArrayPropertyData? FindNodesArray(UAsset asset)
    {
        foreach (var export in asset.Exports)
        {
            if (export is not NormalExport ne || ne.Data == null) continue;
            var nodes = ne.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Nodes");
            if (nodes != null) return nodes;
        }
        return null;
    }

    internal static string ExportClassName(UAsset asset, int exportIdx)
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
        return StripSuffixCopy(exp.ObjectName?.Value?.ToString());
    }

    private static string StripSuffixCopy(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        int cut = name!.IndexOf("_Copy_", StringComparison.Ordinal);
        return cut > 0 ? name[..cut] : name;
    }

    private static bool IsArena(string contentRel) =>
        contentRel.Contains("_Arena", StringComparison.OrdinalIgnoreCase) ||
        contentRel.Contains("ArenaMode", StringComparison.OrdinalIgnoreCase) ||
        contentRel.Contains("RedRoom", StringComparison.OrdinalIgnoreCase);

    /// <summary>Same walk CollectFromTags performs on the parse side: skip list,
    /// object hops into exports, generic struct prefixes, depth-capped, leaf formatting
    /// mirrored onto UAssetAPI property types.</summary>
    internal static List<ConditionParamInfo> CollectParams(UAsset asset, List<PropertyData> data)
    {
        var result = new List<ConditionParamInfo>();
        var seen = new HashSet<int>();
        Walk(data, "", 0);
        return result;

        void Walk(List<PropertyData> props, string prefix, int depth)
        {
            if (depth > 3) return;
            foreach (var p in props)
            {
                string raw = p.Name?.Value?.ToString() ?? "";
                if (raw.Length == 0) continue;
                if (raw is "m_ConditionID" or "m_PerConditionValueToNodeMap" or "m_iSerializeVersion") continue;
                if (p is ObjectPropertyData op && op.Value != null && op.Value.Index > 0
                    && op.Value.Index <= asset.Exports.Count
                    && asset.Exports[op.Value.Index - 1] is NormalExport nested
                    && nested.Data is { Count: > 0 } && seen.Add(op.Value.Index))
                {
                    Walk(nested.Data, prefix + raw + ".", depth + 1);
                    continue;
                }
                if (p is StructPropertyData st && st.Value != null)
                {
                    Walk(st.Value, prefix + raw + ".", depth + 1);
                    continue;
                }
                string v = FormatParam(p);
                if (v.Length > 0)
                    result.Add(new ConditionParamInfo { Path = prefix + raw, Kind = KindOf(p), Value = v });
            }
        }
    }

    private static string KindOf(PropertyData p) => p switch
    {
        EnumPropertyData => "Enum",
        IntPropertyData or Int8PropertyData or Int16PropertyData or Int64PropertyData => "Int",
        UInt16PropertyData or UInt32PropertyData or UInt64PropertyData => "Int",
        FloatPropertyData or DoublePropertyData => "Float",
        BoolPropertyData => "Bool",
        NamePropertyData => "Name",
        StrPropertyData => "Str",
        BytePropertyData => "Byte",
        _ => "ReadOnly"
    };

    private static string FormatParam(PropertyData p) => p switch
    {
        EnumPropertyData en => en.Value?.Value?.ToString() ?? "",
        IntPropertyData i => i.Value.ToString(),
        Int8PropertyData i8 => i8.Value.ToString(),
        Int16PropertyData i16 => i16.Value.ToString(),
        Int64PropertyData i64 => i64.Value.ToString(),
        UInt16PropertyData u16 => u16.Value.ToString(),
        UInt32PropertyData u32 => u32.Value.ToString(),
        UInt64PropertyData u64 => u64.Value.ToString(),
        FloatPropertyData f => f.Value.ToString("0.###", CultureInfo.InvariantCulture),
        DoublePropertyData d => d.Value.ToString("0.###", CultureInfo.InvariantCulture),
        BoolPropertyData b => b.Value.ToString(),
        NamePropertyData n => n.Value?.Value?.ToString() ?? "",
        StrPropertyData s => s.Value?.ToString() ?? "",
        TextPropertyData t => t.Value?.ToString() ?? "",
        BytePropertyData by => by.Value.ToString(),
        ObjectPropertyData o => o.Value == null ? "null" : "idx " + o.Value.Index,
        ArrayPropertyData a => (a.Value?.Length ?? 0) + " entries",
        MapPropertyData m => (m.Value?.Count ?? 0) + " pairs",
        StructPropertyData st => "<" + (st.StructType?.Value?.Value?.ToString() ?? "struct") + ">",
        _ => ""
    };
}
