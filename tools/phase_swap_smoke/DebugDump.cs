using System;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace phase_swap_smoke;

internal static class DebugDump
{
    public static void DumpScenario(string uassetPath)
    {
        var asset = new UAsset(uassetPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
        Console.WriteLine($"== {uassetPath}");
        var root = asset.Exports[0] as NormalExport;
        var rootPhase = root?.Data?.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_rootPhase") as ObjectPropertyData;
        Console.WriteLine($"m_rootPhase -> {Describe(asset, rootPhase?.Value?.Index)}");
        var nodes = root?.Data?.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_PhaseNodes") as ArrayPropertyData;
        if (nodes?.Value == null) { Console.WriteLine("no m_PhaseNodes"); return; }
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sn) continue;
            var name = sn.Value?.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Name") as NamePropertyData;
            var arch = sn.Value?.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_PhaseArchetype") as ObjectPropertyData;
            var trans = sn.Value?.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_PhaseTransitions") as ArrayPropertyData;
            Console.WriteLine($"  node[{i}] name={name?.Value.Value?.ToString() ?? "?"} arch={Describe(asset, arch?.Value?.Index)} transitions={trans?.Value?.Length ?? 0}");
            if (trans?.Value != null)
            {
                foreach (var t in trans.Value.OfType<StructPropertyData>())
                {
                    var to = t.Value?.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_TransitionObject") as ObjectPropertyData;
                    var child = t.Value?.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_iChildNodeIndex") as IntPropertyData;
                    Console.WriteLine($"    transition -> obj={Describe(asset, to?.Value?.Index)} childNode={child?.Value}");
                }
            }
        }
        Console.WriteLine("imports:");
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var im = asset.Imports[i];
            Console.WriteLine($"  [{i}] {im.ClassName?.Value?.ToString()} {im.ObjectName?.Value?.ToString()}");
        }
    }

    private static string Describe(UAsset asset, int? idx)
    {
        if (idx == null) return "null";
        if (idx.Value < 0)
        {
            var im = asset.Imports[-idx.Value - 1];
            return $"import[{ -idx.Value - 1}] {im.ObjectName?.Value?.ToString()}";
        }
        if (idx.Value < asset.Exports.Count)
            return $"export[{idx.Value}] {asset.Exports[idx.Value].ObjectName?.Value?.ToString()}";
        return $"idx {idx.Value}";
    }
}
