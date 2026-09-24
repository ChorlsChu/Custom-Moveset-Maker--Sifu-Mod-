#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SifuMovesetEditor;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

var contentPath = args.Length > 0 ? args[0] :
    @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu";

var contentDir = Path.GetFileName(contentPath.TrimEnd('\\', '/')).Equals("Content", StringComparison.OrdinalIgnoreCase)
    ? contentPath
    : Path.Combine(contentPath, "Content");

var allTags = UnitPropertiesManager.AllVariantTags;
Console.WriteLine($"Content dir: {contentDir}");
Console.WriteLine($"Scanning {allTags.Count} variant tags (raw UAssetAPI reads)");
Console.WriteLine();
Console.WriteLine("=== RAW READ (direct UAssetAPI, no UnitPropertiesManager) ===");
Console.WriteLine();

var rawResults = new Dictionary<string, Dictionary<string, object?>>();
int ok = 0, err = 0;

foreach (var tag in allTags)
{
    Console.Write($"  {tag,-30} ");
    var entry = new Dictionary<string, object?>();

    // ArchetypeDB
    var archRel = UnitPropertiesManager.ResolveArchetypePath(tag);
    if (archRel != null)
    {
        try
        {
            var filePath = Path.Combine(contentDir, archRel + ".uasset");
            var asset = new UAsset(filePath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            if (asset.Exports.Count > 1 && asset.Exports[1] is NormalExport ne)
            {
                var hp = ne.Data.OfType<FloatPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_fHealth");
                var sp = ne.Data.OfType<FloatPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_fStructure");
                entry["Health"] = hp?.Value;
                entry["Structure"] = sp?.Value;
                var parts = new List<string>();
                if (hp != null) parts.Add($"HP={hp.Value:F0}");
                if (sp != null) parts.Add($"Struct={sp.Value:F0}");
                Console.Write(string.Join("  ", parts));
            }
            else { Console.Write("NOT NormalExport[1]"); }
        }
        catch (Exception ex) { Console.Write($"ARCH_ERR: {ex.Message}"); err++; }
    }
    else { Console.Write("(no arch path)"); }

    Console.Write("  |  ");

    // ContextDefense
    var defRel = UnitPropertiesManager.ResolveContextDefensePath(tag);
    if (defRel != null)
    {
        try
        {
            var defPath = Path.Combine(contentDir, defRel + ".uasset");
            var defAsset = new UAsset(defPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            foreach (var exp in defAsset.Exports)
            {
                if (exp is not NormalExport ne2) continue;
                foreach (var d in ne2.Data)
                {
                    if (d is FloatPropertyData fp)
                    {
                        var n = fp.Name.Value.ToString();
                        if (n == "m_fMemoryLimit" && !entry.ContainsKey("MemoryLimit")) { entry["MemoryLimit"] = fp.Value; }
                        else if (n == "m_fMemoryFlushLimit" && !entry.ContainsKey("MemoryFlushLimit")) { entry["MemoryFlushLimit"] = fp.Value; }
                        else if (n == "m_fProbability" && !entry.ContainsKey("Probability")) { entry["Probability"] = fp.Value; }
                    }
                    else if (d is BytePropertyData bp && bp.Name.Value.ToString() == "m_uiHitsCount" && !entry.ContainsKey("HitsCount"))
                    {
                        entry["HitsCount"] = (int)bp.Value;
                    }
                }
            }
            var parts = new List<string>();
            if (entry.ContainsKey("MemoryLimit")) parts.Add($"MemLim={((float)entry["MemoryLimit"]!):F1}");
            if (entry.ContainsKey("HitsCount")) parts.Add($"Hits={entry["HitsCount"]}");
            if (entry.ContainsKey("MemoryFlushLimit")) parts.Add($"Flush={((float)entry["MemoryFlushLimit"]!):F1}");
            if (entry.ContainsKey("Probability")) parts.Add($"Prob={((float)entry["Probability"]!):F2}");
            Console.Write(string.Join("  ", parts));
            ok++;
        }
        catch (Exception ex) { Console.Write($"DEF_ERR: {ex.Message}"); err++; }
    }

    rawResults[tag] = entry;
    Console.WriteLine();
}

// Now compare with UnitPropertiesManager.Read()
Console.WriteLine();
Console.WriteLine("=== COMPARISON: UnitPropertiesManager.Read() vs RAW ===");
Console.WriteLine();

int matches = 0, mismatches = 0;

foreach (var tag in allTags)
{
    var upmProps = UnitPropertiesManager.Read(contentPath, tag);
    var raw = rawResults[tag];

    var diffs = new List<string>();

    Compare("Health", upmProps.Health, raw.TryGetValue("Health", out var rh) ? rh as float? : null);
    Compare("Structure", upmProps.Structure, raw.TryGetValue("Structure", out var rs) ? rs as float? : null);
    Compare("MemoryLimit", upmProps.MemoryLimit, raw.TryGetValue("MemoryLimit", out var rml) ? rml as float? : null);
    Compare("HitsCount", upmProps.HitsCount.HasValue ? (float?)upmProps.HitsCount.Value : null,
                   raw.TryGetValue("HitsCount", out var rhc) ? (rhc is int rc ? (float?)rc : (rhc as float?)) : null);
    Compare("MemoryFlushLimit", upmProps.MemoryFlushLimit, raw.TryGetValue("MemoryFlushLimit", out var rfl) ? rfl as float? : null);
    Compare("Probability", upmProps.Probability, raw.TryGetValue("Probability", out var rp) ? rp as float? : null);

    if (diffs.Count == 0)
    {
        matches++;
    }
    else
    {
        mismatches++;
        Console.WriteLine($"  {tag}: {string.Join(", ", diffs)}");
    }

    void Compare(string prop, float? upm, float? raw)
    {
        if (upm.HasValue && raw.HasValue)
        {
            if (Math.Abs(upm.Value - raw.Value) > 0.01f)
                diffs.Add($"{prop}: UPM={upm.Value:F2} vs RAW={raw.Value:F2}");
        }
        else if (upm.HasValue != raw.HasValue)
        {
            diffs.Add($"{prop}: UPM={(upm.HasValue ? upm.Value.ToString("F2") : "null")} vs RAW={(raw.HasValue ? raw.Value.ToString("F2") : "null")}");
        }
    }
}

Console.WriteLine();
Console.WriteLine($"Results: {matches} MATCH, {mismatches} MISMATCH out of {allTags.Count} tags");
