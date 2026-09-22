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

string moddedPath = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\FireDisc\FireDisciple_Advanced_Combo.uasset";
string backupPath = moddedPath + ".backup2";

Console.WriteLine("=== FIX REDIRECT NODES m_TargetNodes ===");
Console.WriteLine($"File: {moddedPath}");
Console.WriteLine($"Size: {new FileInfo(moddedPath).Length} bytes");

if (!File.Exists(backupPath))
{
    File.Copy(moddedPath, backupPath);
    Console.WriteLine($"Backup: {backupPath}");
}

var asset = new UAsset(moddedPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
NormalExport? ne = null;
for (int i = 0; i < asset.Exports.Count; i++)
{
    if (asset.Exports[i] is NormalExport n && n.SerialSize > 1000)
    { ne = n; break; }
}
if (ne == null) { Console.WriteLine("ERROR"); return; }

var nodesArr = ne.Data.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_Nodes");
if (nodesArr?.Value == null) { Console.WriteLine("ERROR"); return; }

Console.WriteLine($"Total nodes: {nodesArr.Value.Length}");

// Show ALL nodes first
Console.WriteLine("\n--- ALL NODES ---");
for (int i = 0; i < nodesArr.Value.Length; i++)
{
    var ns = nodesArr.Value[i] as StructPropertyData;
    if (ns == null) continue;
    string name = ns.Value.OfType<NamePropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_Name")?.Value.ToString() ?? "?";
    int redir = ns.Value.OfType<IntPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_NodeRedirect")?.Value ?? -1;
    var ts = ns.Value.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_Transitions");
    var ta = ts?.Value.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_Transitions");
    int tc = ta?.Value?.Length ?? 0;
    string tag = i >= 31 ? " <<<NEW" : "";
    if (redir >= 0) tag += $" REDIR->{redir}";

    string targets = "";
    if (tc > 0 && ta.Value[0] is StructPropertyData first)
    {
        var map = first.Value.OfType<MapPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_TargetNodes");
        if (map != null)
        {
            var parts = new List<string>();
            foreach (var kvp in map.Value)
            {
                string kv = kvp.Key is BytePropertyData bp ? bp.Value.ToString() : kvp.Key is IntPropertyData ip ? ip.Value.ToString() : "?";
                int tv = kvp.Value is IntPropertyData iv ? iv.Value : -1;
                string tn = tv >= 0 && tv < nodesArr.Value.Length ? (nodesArr.Value[tv] as StructPropertyData)?.Value.OfType<NamePropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_Name")?.Value.ToString() ?? "?" : "INV";
                parts.Add($"{kv}->{tv}[{tn}]");
            }
            targets = " [" + string.Join(", ", parts) + "]";
        }
    }
    Console.WriteLine($"  [{i}] {name} Trans={tc}{tag}{targets}");
}

// Now fix redirect nodes 31+
Console.WriteLine("\n--- FIXING REDIRECT NODES ---");
int fixedCount = 0;
for (int i = 31; i < nodesArr.Value.Length; i++)
{
    var ns = nodesArr.Value[i] as StructPropertyData;
    if (ns == null) continue;

    string name = ns.Value.OfType<NamePropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_Name")?.Value.ToString() ?? "?";
    int redir = ns.Value.OfType<IntPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_NodeRedirect")?.Value ?? -1;

    if (redir < 0) continue;

    Console.WriteLine($"\n  [{i}] {name} Redirect={redir}");

    // Resolve redirect target name
    string redirTargetName = "?";
    if (redir >= 0 && redir < nodesArr.Value.Length)
    {
        redirTargetName = (nodesArr.Value[redir] as StructPropertyData)?.Value.OfType<NamePropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_Name")?.Value.ToString() ?? "?";
    }
    Console.WriteLine($"    Should point to: [{redir}] {redirTargetName}");

    // Find m_TargetNodes on first transition
    var transStruct = ns.Value.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_Transitions");
    var transArr = transStruct?.Value.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_Transitions");
    if (transArr == null || transArr.Value.Length == 0)
    {
        Console.WriteLine($"    No transitions to fix - skipping");
        continue;
    }

    var firstTrans = transArr.Value[0] as StructPropertyData;
    if (firstTrans == null) { Console.WriteLine($"    First transition is null"); continue; }

    var map = firstTrans.Value.OfType<MapPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_TargetNodes");
    if (map == null)
    {
        Console.WriteLine($"    No m_TargetNodes on first transition! Skipping.");
        continue;
    }

    Console.WriteLine($"    BEFORE: {map.Value.Count} entries");
    foreach (var kvp in map.Value)
    {
        string kv = kvp.Key is BytePropertyData bp ? bp.Value.ToString() : kvp.Key is IntPropertyData ip ? ip.Value.ToString() : "?";
        int tv = kvp.Value is IntPropertyData iv ? iv.Value : -1;
        Console.WriteLine($"      key={kv} -> val={tv}");
    }

    // Remove all existing entries and add correct one
    var entriesToRemove = map.Value.ToList();
    foreach (var entry in entriesToRemove)
        map.Value.Remove(entry.Key);
    map.Value.Add(
        new BytePropertyData { Name = new FName(asset, "m_TargetNodes"), Value = 255 },
        new IntPropertyData { Name = new FName(asset, "m_TargetNodes"), Value = redir }
    );
    fixedCount++;
    Console.WriteLine($"    AFTER: 1 entry: key=255 -> {redir} [{redirTargetName}]");
}

if (fixedCount > 0)
{
    asset.Write(moddedPath);
    Console.WriteLine($"\nWrote patched file: {new FileInfo(moddedPath).Length} bytes ({fixedCount} nodes fixed)");
    Console.WriteLine("Repack the pak and test!");
}
else
{
    Console.WriteLine("\nNo redirect nodes needed fixing.");
}
