#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

var contentPath = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content\DB\AI\Archetypes";

var files = Directory.GetFiles(contentPath, "*ContextualDefense*.uasset", SearchOption.AllDirectories)
    .Where(f => !f.Contains("\\Mod\\") && !f.Contains("\\mods\\") && !f.Contains("~mods"))
    .OrderBy(f => f)
    .ToList();

Console.WriteLine($"Found {files.Count} ContextualDefense .uasset files");
Console.WriteLine();

var allResults = new List<Dictionary<string, object>>();

foreach (var filePath in files)
{
    var unitName = Path.GetFileNameWithoutExtension(filePath);
    try
    {
        var asset = new UAsset(filePath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);

        // Find all exports that have m_fProbability
        var probNodes = new List<(int idx, float prob, int nodeIndex, List<int> linkedNodes)>();
        var defenseNodes = new List<(string typeName, int idx, int nodeIndex, Dictionary<string, string> props)>();

        for (int ei = 0; ei < asset.Exports.Count; ei++)
        {
            var exp = asset.Exports[ei];
            if (exp is not NormalExport ne) continue;

            // Check for m_fProbability
            var probProp = ne.Data.OfType<FloatPropertyData>()
                .FirstOrDefault(p => p.Name.Value.ToString() == "m_fProbability");
            if (probProp != null)
            {
                int nodeIndex = ne.Data.OfType<IntPropertyData>()
                    .FirstOrDefault(p => p.Name.Value.ToString() == "m_iNodeIndex")?.Value ?? -1;
                var linkedProp = ne.Data.OfType<ArrayPropertyData>()
                    .FirstOrDefault(p => p.Name.Value.ToString() == "m_LinkedNodes");
                var linkedNodes = new List<int>();
                if (linkedProp?.Value != null)
                {
                    foreach (var elem in linkedProp.Value)
                    {
                        if (elem is IntPropertyData ip) linkedNodes.Add(ip.Value);
                        else if (elem is ObjectPropertyData op)
                        {
                            // FPackageIndex - try to extract the index
                            linkedNodes.Add(-1);
                        }
                    }
                }
                probNodes.Add((ei, probProp.Value, nodeIndex, linkedNodes));
            }

            // Check for DefenseInfluenceNode* exports (look at import class names)
            bool isDefenseNode = false;
            string nodeType = "";
            if (ne.Data.OfType<BoolPropertyData>().Any(p => p.Name.Value.ToString() == "m_bAvoided"))
            { isDefenseNode = true; nodeType = "Avoid"; }
            else if (ne.Data.OfType<BoolPropertyData>().Any(p => p.Name.Value.ToString() == "m_bDodged"))
            { isDefenseNode = true; nodeType = "Dodge"; }
            else if (ne.Data.OfType<EnumPropertyData>().Any(p => p.Name.Value.ToString() == "m_eDirectionTypeOverride"))
            { isDefenseNode = true; nodeType = "Dodge的方向"; }
            else if (ne.Data.OfType<BoolPropertyData>().Any(p => p.Name.Value.ToString() == "m_bInverted"))
            { isDefenseNode = true; nodeType = "Parry/Deflect"; }

            if (isDefenseNode)
            {
                int nodeIndex = ne.Data.OfType<IntPropertyData>()
                    .FirstOrDefault(p => p.Name.Value.ToString() == "m_iNodeIndex")?.Value ?? -1;
                var props = new Dictionary<string, string>();
                foreach (var p in ne.Data)
                {
                    string val = p switch
                    {
                        FloatPropertyData fp => fp.Value.ToString(),
                        IntPropertyData ip => ip.Value.ToString(),
                        BoolPropertyData bp => bp.Value.ToString(),
                        EnumPropertyData ep => ep.Value.ToString(),
                        _ => $"({p.GetType().Name})"
                    };
                    props[p.Name.Value.ToString()] = val;
                }
                defenseNodes.Add((nodeType, ei, nodeIndex, props));
            }
        }

        if (probNodes.Count > 0 || defenseNodes.Count > 0)
        {
            Console.WriteLine($"=== {unitName} ===");
            if (probNodes.Count > 0)
            {
                foreach (var pn in probNodes)
                {
                    Console.WriteLine($"  m_fProbability = {pn.prob:F4}  (export [{pn.idx}], nodeIndex={pn.nodeIndex}, linked={string.Join(",", pn.linkedNodes)})");
                }
            }
            if (defenseNodes.Count > 0)
            {
                foreach (var dn in defenseNodes)
                {
                    Console.WriteLine($"  DefenseInfluenceNode ({dn.typeName}) export [{dn.idx}] nodeIndex={dn.nodeIndex}");
                    foreach (var kv in dn.props)
                        Console.WriteLine($"    {kv.Key} = {kv.Value}");
                }
            }
            Console.WriteLine();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"=== {unitName} === ERROR: {ex.Message}");
        Console.WriteLine();
    }
}

Console.WriteLine("Done.");
