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

string srcCombo = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\FireDisc\FireDisciple_Advanced_Combo";
string srcArch = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Archetype\BP_FireDisciple_ArchetypeDB_Adv_Master";
string outDir = @"C:\Users\Charles\Downloads\Sifu Modding\Movesets Prototypes\Moveset Maker\SifuMovesetEditor\tools\_dump_conduit\full_replace_test";
Directory.CreateDirectory(outDir);

// STEP 1: Create custom combo from FireDisc base
Console.WriteLine("=== STEP 1: Create custom combo from FireDisc ===");
File.Copy(srcCombo + ".uasset", Path.Combine(outDir, "FireDisciple_Custom_Combo.uasset"), true);
File.Copy(srcCombo + ".uexp", Path.Combine(outDir, "FireDisciple_Custom_Combo.uexp"), true);

var comboAsset = new UAsset(Path.Combine(outDir, "FireDisciple_Custom_Combo.uasset"), EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
Console.WriteLine($"Loaded: {comboAsset.Imports.Count} imports, {comboAsset.Exports.Count} exports");

ArrayPropertyData nodesArray = null;
foreach (var exp in comboAsset.Exports)
    if (exp is NormalExport ne)
        foreach (var p in ne.Data)
            if (p.Name.Value.Value == "m_Nodes" && p is ArrayPropertyData ap)
            { nodesArray = ap; break; }

if (nodesArray?.Value == null) { Console.WriteLine("ERROR: m_Nodes not found"); return; }

int origNodeCount = nodesArray.Value.Length;
Console.WriteLine($"Original nodes: {origNodeCount}");

var sourceNode = nodesArray.Value[1] as StructPropertyData;
if (sourceNode == null) { Console.WriteLine("ERROR: node[1] not found"); return; }

var newNode1 = DeepCloneStruct(sourceNode, comboAsset);
SetName(newNode1, "m_Name", "CustomMixUp1");
SetInt(newNode1, "m_NodeRedirect", -1);
SetBool(newNode1, "m_bSkip", false);

var newNode2 = DeepCloneStruct(sourceNode, comboAsset);
SetName(newNode2, "m_Name", "CustomMixUp2");
SetInt(newNode2, "m_NodeRedirect", -1);
SetBool(newNode2, "m_bSkip", false);

var newArray = new PropertyData[origNodeCount + 2];
Array.Copy(nodesArray.Value, newArray, origNodeCount);
newArray[origNodeCount] = newNode1;
newArray[origNodeCount + 1] = newNode2;
nodesArray.Value = newArray;

Console.WriteLine($"Added 2 nodes → {nodesArray.Value.Length} total");

string comboOut = Path.Combine(outDir, "FireDisciple_Custom_Combo.uasset");
comboAsset.Write(comboOut);
Console.WriteLine($"Saved combo: {comboOut} ({new FileInfo(comboOut).Length} bytes)");

// STEP 2: Modify ArchetypeDB with CORRECT FPackageIndex
Console.WriteLine("\n=== STEP 2: Modify ArchetypeDB ===");
File.Copy(srcArch + ".uasset", Path.Combine(outDir, "BP_FireDisciple_ArchetypeDB_Adv_Master.uasset"), true);
File.Copy(srcArch + ".uexp", Path.Combine(outDir, "BP_FireDisciple_ArchetypeDB_Adv_Master.uexp"), true);

var archAsset = new UAsset(Path.Combine(outDir, "BP_FireDisciple_ArchetypeDB_Adv_Master.uasset"), EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
int origImportCount = archAsset.Imports.Count;

// Add Package import (root level)
var newPkgImport = new Import(
    "/Script/CoreUObject", "Package",
    new FPackageIndex(0),
    "/Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Custom_Combo",
    false, archAsset
);
archAsset.Imports.Add(newPkgImport);
int pkgImportIdx = archAsset.Imports.Count - 1; // index 24

// Add Combo class import (outer = Package import)
// CORRECT FPackageIndex for outer: -(pkgImportIdx + 1)
var newComboImport = new Import(
    "/Script/Engine", "Combo",
    new FPackageIndex(-(pkgImportIdx + 1)), // CORRECT: -(24+1) = -25
    "FireDisciple_Custom_Combo",
    false, archAsset
);
archAsset.Imports.Add(newComboImport);
int comboImportIdx = archAsset.Imports.Count - 1; // index 25

int finalCount = archAsset.Imports.Count;
Console.WriteLine($"Imports: {origImportCount} → {finalCount}");
Console.WriteLine($"Package import at index {pkgImportIdx}: FPackageIndex = -(pkgImportIdx+1) = {-(pkgImportIdx + 1)}");
Console.WriteLine($"Combo class import at index {comboImportIdx}: FPackageIndex = -(comboImportIdx+1) = {-(comboImportIdx + 1)}");
Console.WriteLine($"Combo outer points to Package: FPackageIndex = {-(pkgImportIdx + 1)}");

// Update m_difficultyLevels[0].m_Combo to point to new Combo class import
// CORRECT FPackageIndex: -(comboImportIdx + 1)
foreach (var exp in archAsset.Exports)
{
    if (exp is NormalExport ne)
        foreach (var p in ne.Data)
            if (p.Name.Value.Value == "m_difficultyLevels" && p is ArrayPropertyData ap && ap.Value != null)
                if (ap.Value[0] is StructPropertyData sp)
                    foreach (var child in sp.Value)
                        if (child.Name.Value.Value == "m_Combo" && child is ObjectPropertyData op)
                        {
                            int correctFpkgIdx = -(comboImportIdx + 1); // CORRECT: -(25+1) = -26
                            Console.WriteLine($"\nm_Combo: {op.Value.Index} → {correctFpkgIdx}");
                            Console.WriteLine($"  Verify: import[-({-correctFpkgIdx})-1] = import[{(-correctFpkgIdx) - 1}]");
                            op.Value = new FPackageIndex(correctFpkgIdx);
                        }
}

string archOut = Path.Combine(outDir, "BP_FireDisciple_ArchetypeDB_Adv_Master.uasset");
archAsset.Write(archOut);
Console.WriteLine($"Saved arch: {archOut} ({new FileInfo(archOut).Length} bytes)");

// STEP 3: Verify with CORRECT resolution
Console.WriteLine("\n=== STEP 3: Verify ===");
var vArch = new UAsset(archOut, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
Console.WriteLine($"ArchetypeDB imports: {vArch.Imports.Count}");

// Show all imports for context
for (int i = 0; i < vArch.Imports.Count; i++)
{
    var imp = vArch.Imports[i];
    int fpkgIdx = -(i + 1); // CORRECT FPackageIndex for this import
    string marker = i >= origImportCount ? " [NEW]" : "";
    if (i == pkgImportIdx || i == comboImportIdx || i == 9 || imp.ClassName?.Value?.Value == "Combo")
        Console.WriteLine($"  [{i}] fpkg={fpkgIdx} {imp.ClassName?.Value?.Value}: {imp.ObjectName?.Value?.Value}{marker}");
}

// Verify m_Combo resolution
foreach (var exp in vArch.Exports)
    if (exp is NormalExport ne)
        foreach (var p in ne.Data)
            if (p.Name.Value.Value == "m_difficultyLevels" && p is ArrayPropertyData ap && ap.Value != null)
                if (ap.Value[0] is StructPropertyData sp)
                    foreach (var c in sp.Value)
                        if (c.Name.Value.Value == "m_Combo" && c is ObjectPropertyData op)
                        {
                            int fpkgIdx = op.Value.Index;
                            int resolvedIdx = (-fpkgIdx) - 1; // CORRECT formula
                            string name = resolvedIdx >= 0 && resolvedIdx < vArch.Imports.Count
                                ? $"{vArch.Imports[resolvedIdx].ClassName?.Value?.Value}: {vArch.Imports[resolvedIdx].ObjectName?.Value?.Value}"
                                : "OUT OF RANGE";
                            Console.WriteLine($"\nm_Combo FPackageIndex = {fpkgIdx}");
                            Console.WriteLine($"  Resolved: -({-fpkgIdx})-1 = {resolvedIdx} → {name}");
                        }

// Verify combo
var vCombo = new UAsset(Path.Combine(outDir, "FireDisciple_Custom_Combo.uasset"), EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
foreach (var exp in vCombo.Exports)
    if (exp is NormalExport ne)
        foreach (var p in ne.Data)
            if (p.Name.Value.Value == "m_Nodes" && p is ArrayPropertyData ap && ap.Value != null)
                Console.WriteLine($"\nCustom combo: {ap.Value.Length} nodes");

Console.WriteLine("\n=== Files ready ===");
foreach (var f in Directory.GetFiles(outDir))
    if (f.EndsWith(".uasset") || f.EndsWith(".uexp"))
        Console.WriteLine($"  {Path.GetFileName(f)} [{new FileInfo(f).Length}]");

string GetName(StructPropertyData s, string n) { foreach (var c in s.Value) if (c is NamePropertyData np && c.Name.Value.Value == n) return np.Value?.Value?.Value ?? ""; return ""; }
int GetInt(StructPropertyData s, string n) { foreach (var c in s.Value) if (c is IntPropertyData ip && c.Name.Value.Value == n) return ip.Value; return -999; }
bool GetBool(StructPropertyData s, string n) { foreach (var c in s.Value) if (c is BoolPropertyData bp && c.Name.Value.Value == n) return bp.Value; return false; }
void SetName(StructPropertyData s, string n, string v) { foreach (var c in s.Value) if (c is NamePropertyData np && c.Name.Value.Value == n) { np.Value = new FName(comboAsset, v); return; } }
void SetInt(StructPropertyData s, string n, int v) { foreach (var c in s.Value) if (c is IntPropertyData ip && c.Name.Value.Value == n) { ip.Value = v; return; } }
void SetBool(StructPropertyData s, string n, bool v) { foreach (var c in s.Value) if (c is BoolPropertyData bp && c.Name.Value.Value == n) { bp.Value = v; return; } }

StructPropertyData DeepCloneStruct(StructPropertyData source, UAsset asset)
{
    var clone = new StructPropertyData(new FName(asset, source.Name.Value.Value), source.StructType);
    foreach (var child in source.Value)
    {
        if (child is IntPropertyData ip) { var c = new IntPropertyData(new FName(asset, ip.Name.Value.Value)); c.Value = ip.Value; clone.Value.Add(c); }
        else if (child is BoolPropertyData bp) { var c = new BoolPropertyData(new FName(asset, bp.Name.Value.Value)); c.Value = bp.Value; clone.Value.Add(c); }
        else if (child is NamePropertyData np) { var c = new NamePropertyData(new FName(asset, np.Name.Value.Value)); c.Value = new FName(asset, np.Value?.Value?.Value ?? ""); clone.Value.Add(c); }
        else if (child is FloatPropertyData fp) { var c = new FloatPropertyData(new FName(asset, fp.Name.Value.Value)); c.Value = fp.Value; clone.Value.Add(c); }
        else if (child is BytePropertyData bp2) { var c = new BytePropertyData(new FName(asset, bp2.Name.Value.Value)); c.Value = bp2.Value; clone.Value.Add(c); }
        else if (child is UInt32PropertyData u32) { var c = new UInt32PropertyData(new FName(asset, u32.Name.Value.Value)); c.Value = u32.Value; clone.Value.Add(c); }
        else if (child is ObjectPropertyData op) { var c = new ObjectPropertyData(new FName(asset, op.Name.Value.Value)); c.Value = new FPackageIndex(op.Value.Index); clone.Value.Add(c); }
        else if (child is StructPropertyData sp) { clone.Value.Add(DeepCloneStruct(sp, asset)); }
        else if (child is ArrayPropertyData ap) { var c = new ArrayPropertyData(new FName(asset, ap.Name.Value.Value)); c.Value = ap.Value; clone.Value.Add(c); }
        else if (child is MapPropertyData mp) { var c = new MapPropertyData(new FName(asset, mp.Name.Value.Value)); c.Value = mp.Value; clone.Value.Add(c); }
        else if (child is EnumPropertyData ep) { var c = new EnumPropertyData(new FName(asset, ep.Name.Value.Value)); c.Value = ep.Value; clone.Value.Add(c); }
        else { clone.Value.Add(child); }
    }
    return clone;
}
