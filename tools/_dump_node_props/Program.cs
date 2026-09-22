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

string baseDir = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Combo";
string uassetPath = Path.Combine(baseDir, "FireDisciple_Base_Combo.uasset");
string uexpPath = Path.Combine(baseDir, "FireDisciple_Base_Combo.uexp");

int uassetLen = (int)new FileInfo(uassetPath).Length;
var uexpBytes = File.ReadAllBytes(uexpPath);

var asset = new UAsset(uassetPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
var ne = asset.Exports.OfType<NormalExport>().First(e => e.SerialSize > 1000);
var nodesProp = ne.Data.First(p => p.Name.Value.Value == "m_Nodes") as ArrayPropertyData;
var nodes = new List<StructPropertyData>();
foreach (var v in nodesProp.Value)
    if (v is StructPropertyData s) nodes.Add(s);

int VALUE_OFFSET = 25;

// Patch Node[1] redirect from 2→99
var patchNodes = new[] { (1, 99), (4, 88) };

// 1. Modify in-memory
foreach (var (nodeIdx, newValue) in patchNodes)
{
    var intProp = nodes[nodeIdx].Value.OfType<IntPropertyData>().FirstOrDefault(p => p.Name.Value.Value == "m_NodeRedirect");
    intProp.Value = newValue;
}

// 2. Write
string outDir = Path.Combine(Path.GetTempPath(), "patch_test");
Directory.CreateDirectory(outDir);
string outUasset = Path.Combine(outDir, "FireDisciple_Base_Combo.uasset");
string outUexp = Path.Combine(outDir, "FireDisciple_Base_Combo.uexp");
asset.Write(outUasset);

Console.WriteLine($"UAsset written: {new FileInfo(outUasset).Length} bytes (original: {uassetLen})");
Console.WriteLine($"UExp exists after Write: {File.Exists(outUexp)}");
if (File.Exists(outUexp))
{
    Console.WriteLine($"UExp written: {new FileInfo(outUexp).Length} bytes (original: {uexpBytes.Length})");
    var writtenUexp = File.ReadAllBytes(outUexp);
    int uexpDiffs = 0;
    for (int i = 0; i < Math.Min(writtenUexp.Length, uexpBytes.Length); i++)
        if (writtenUexp[i] != uexpBytes[i]) uexpDiffs++;
    Console.WriteLine($"UExp differences from original: {uexpDiffs} bytes");
    
    // Check redirect values in written uexp
    Console.WriteLine("\nRedirect values in written uexp:");
    for (int i = 0; i < nodes.Count; i++)
    {
        var intProp = nodes[i].Value.OfType<IntPropertyData>().FirstOrDefault(p => p.Name.Value.Value == "m_NodeRedirect");
        if (intProp == null) continue;
        long apiOffset = intProp.Offset;
        int uexpPos = (int)(apiOffset - uassetLen) + VALUE_OFFSET;
        if (uexpPos + 4 <= writtenUexp.Length)
        {
            int val = BitConverter.ToInt32(writtenUexp, uexpPos);
            var origProp = nodes[i].Value.OfType<IntPropertyData>().FirstOrDefault(p => p.Name.Value.Value == "m_NodeRedirect");
            string isModified = patchNodes.Any(p => p.Item1 == i) ? " (MODIFIED)" : "";
            Console.WriteLine($"  Node[{i}] at uexp[{uexpPos}]: {val}{isModified}");
        }
    }
    
    // 3. Now binary-patch the uexp
    Console.WriteLine("\n=== Binary patching the uexp file ===");
    var patchedBytes = (byte[])writtenUexp.Clone();
    foreach (var (nodeIdx, newValue) in patchNodes)
    {
        var intProp = nodes[nodeIdx].Value.OfType<IntPropertyData>().FirstOrDefault(p => p.Name.Value.Value == "m_NodeRedirect");
        long apiOffset = intProp.Offset;
        int uexpPos = (int)(apiOffset - uassetLen) + VALUE_OFFSET;
        byte[] valBytes = BitConverter.GetBytes(newValue);
        patchedBytes[uexpPos] = valBytes[0];
        patchedBytes[uexpPos + 1] = valBytes[1];
        patchedBytes[uexpPos + 2] = valBytes[2];
        patchedBytes[uexpPos + 3] = valBytes[3];
        Console.WriteLine($"  Patched Node[{nodeIdx}] at uexp[{uexpPos}]: → {newValue}");
    }
    File.WriteAllBytes(outUexp, patchedBytes);
    
    // 4. Verify by re-reading
    var verifyAsset = new UAsset(outUasset, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
    var verifyNe = verifyAsset.Exports.OfType<NormalExport>().First(e => e.SerialSize > 1000);
    var verifyNodesProp = verifyNe.Data.First(p => p.Name.Value.Value == "m_Nodes") as ArrayPropertyData;
    var verifyNodes = new List<StructPropertyData>();
    foreach (var v in verifyNodesProp.Value)
        if (v is StructPropertyData s) verifyNodes.Add(s);
    
    Console.WriteLine("\n=== Verifying patched values after re-read ===");
    for (int i = 0; i < verifyNodes.Count; i++)
    {
        var intProp = verifyNodes[i].Value.OfType<IntPropertyData>().FirstOrDefault(p => p.Name.Value.Value == "m_NodeRedirect");
        if (intProp == null) continue;
        var patch = patchNodes.FirstOrDefault(p => p.Item1 == i);
        if (patch != default)
        {
            string match = intProp.Value == patch.Item2 ? "✓" : $"✗ (got {intProp.Value})";
            Console.WriteLine($"  Node[{i}]: {intProp.Value} (expected {patch.Item2}) {match}");
        }
    }
}

// Cleanup
try { Directory.Delete(outDir, true); } catch { }
