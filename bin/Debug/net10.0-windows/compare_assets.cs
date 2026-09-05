using System;
using System.IO;
using System.Security.Cryptography;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

const string VANILLA_PATH = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content\DB\_MainChar\Combos\MainChar_ComboTree.uasset";
const string EXPORTED_PATH = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\MainCharComboMod\_MainChar\Combos\MainChar_ComboTree.uasset";

Console.WriteLine("========================================");
Console.WriteLine("  MainChar_ComboTree.uasset Comparison");
Console.WriteLine("========================================");
Console.WriteLine();

// --- File sizes ---
var vanillaInfo = new FileInfo(VANILLA_PATH);
var exportedInfo = new FileInfo(EXPORTED_PATH);
Console.WriteLine("FILE INFO:");
Console.WriteLine($"  Vanilla  : {vanillaInfo.Length:N0} bytes (last modified: {vanillaInfo.LastWriteTime})");
Console.WriteLine($"  Exported : {exportedInfo.Length:N0} bytes (last modified: {exportedInfo.LastWriteTime})");
Console.WriteLine($"  Size diff: {exportedInfo.Length - vanillaInfo.Length:N0} bytes");
Console.WriteLine();

// --- Binary comparison ---
Console.WriteLine("BINARY COMPARISON:");
byte[] vanillaBytes = File.ReadAllBytes(VANILLA_PATH);
byte[] exportedBytes = File.ReadAllBytes(EXPORTED_PATH);
if (vanillaBytes.Length != exportedBytes.Length)
{
    Console.WriteLine("  Files are DIFFERENT sizes!");
    int minLen = Math.Min(vanillaBytes.Length, exportedBytes.Length);
    for (int i = 0; i < minLen; i++)
    {
        if (vanillaBytes[i] != exportedBytes[i])
        {
            Console.WriteLine($"  First byte difference at offset 0x{i:X4}: vanilla=0x{vanillaBytes[i]:X2} exported=0x{exportedBytes[i]:X2}");
            break;
        }
    }
}
else
{
    bool identical = true;
    int diffCount = 0;
    int firstDiff = -1;
    for (int i = 0; i < vanillaBytes.Length; i++)
    {
        if (vanillaBytes[i] != exportedBytes[i])
        {
            if (firstDiff < 0) firstDiff = i;
            diffCount++;
            identical = false;
        }
    }
    if (identical)
    {
        Console.WriteLine("  Files are IDENTICAL (byte-for-byte).");
    }
    else
    {
        Console.WriteLine($"  Files are DIFFERENT. {diffCount} bytes differ out of {vanillaBytes.Length}.");
        Console.WriteLine($"  First byte difference at offset 0x{firstDiff:X4}: vanilla=0x{vanillaBytes[firstDiff]:X2} exported=0x{exportedBytes[firstDiff]:X2}");
        Console.WriteLine($"  Last byte difference near offset 0x{(vanillaBytes.Length - 1):X4}");
    }
}
Console.WriteLine();

using var sha256 = SHA256.Create();
string vHash = Convert.ToHexString(sha256.ComputeHash(vanillaBytes));
string eHash = Convert.ToHexString(sha256.ComputeHash(exportedBytes));
Console.WriteLine($"  Vanilla  SHA256: {vHash}");
Console.WriteLine($"  Exported SHA256: {eHash}");
Console.WriteLine($"  Hash match: {vHash == eHash}");
Console.WriteLine();

// --- UAsset API comparison ---
Console.WriteLine("UASSET HEADER COMPARISON:");
var eng = EngineVersion.VER_UE4_26;
var vanillaAsset = new UAsset(VANILLA_PATH, eng, null, CustomSerializationFlags.None);
var exportedAsset = new UAsset(EXPORTED_PATH, eng, null, CustomSerializationFlags.None);

Console.WriteLine($"  ObjectVersion:    {vanillaAsset.ObjectVersion} -> {exportedAsset.ObjectVersion}");
Console.WriteLine($"  UseSeparateBulk:  {vanillaAsset.UseSeparateBulkDataFiles} -> {exportedAsset.UseSeparateBulkDataFiles}");
Console.WriteLine($"  FileVersionLic:   {vanillaAsset.FileVersionLicenseeUE} -> {exportedAsset.FileVersionLicenseeUE}");
Console.WriteLine($"  Imports count:    {vanillaAsset.Imports.Count} -> {exportedAsset.Imports.Count}");
Console.WriteLine($"  Exports count:    {vanillaAsset.Exports.Count} -> {exportedAsset.Exports.Count}");
Console.WriteLine($"  DependsMap count: {vanillaAsset.DependsMap?.Count ?? 0} -> {exportedAsset.DependsMap?.Count ?? 0}");
Console.WriteLine($"  Name map count:   {vanillaAsset.GetNameMapIndexList().Count} -> {exportedAsset.GetNameMapIndexList().Count}");
Console.WriteLine();

// --- Import table comparison ---
Console.WriteLine("IMPORT TABLE COMPARISON:");
Console.WriteLine($"  Vanilla imports:  {vanillaAsset.Imports.Count}");
Console.WriteLine($"  Exported imports: {exportedAsset.Imports.Count}");
if (vanillaAsset.Imports.Count != exportedAsset.Imports.Count)
    Console.WriteLine($"  *** DIFFERENT IMPORT COUNT: {vanillaAsset.Imports.Count} vs {exportedAsset.Imports.Count} ***");
Console.WriteLine();

Console.WriteLine("--- Vanilla Imports ---");
for (int i = 0; i < vanillaAsset.Imports.Count; i++)
{
    var imp = vanillaAsset.Imports[i];
    Console.WriteLine($"  [{i}] ObjectName={imp.ObjectName.Value}  ClassName={imp.ClassName.Value}  ClassPackage={imp.ClassPackage.Value}  OuterIdx={imp.OuterIndex.Index}");
}

Console.WriteLine();
Console.WriteLine("--- Exported Imports ---");
for (int i = 0; i < exportedAsset.Imports.Count; i++)
{
    var imp = exportedAsset.Imports[i];
    Console.WriteLine($"  [{i}] ObjectName={imp.ObjectName.Value}  ClassName={imp.ClassName.Value}  ClassPackage={imp.ClassPackage.Value}  OuterIdx={imp.OuterIndex.Index}");
}

Console.WriteLine();
Console.WriteLine("--- Import Differences ---");
int maxImp = Math.Max(vanillaAsset.Imports.Count, exportedAsset.Imports.Count);
int importDiffCount = 0;
for (int i = 0; i < maxImp; i++)
{
    if (i >= vanillaAsset.Imports.Count)
    {
        var imp = exportedAsset.Imports[i];
        Console.WriteLine($"  [+] NEW import in exported [{i}]: {imp.ObjectName.Value} (class={imp.ClassName.Value})");
        importDiffCount++;
    }
    else if (i >= exportedAsset.Imports.Count)
    {
        var imp = vanillaAsset.Imports[i];
        Console.WriteLine($"  [-] REMOVED import from exported [{i}]: {imp.ObjectName.Value} (class={imp.ClassName.Value})");
        importDiffCount++;
    }
    else
    {
        var vImp = vanillaAsset.Imports[i];
        var eImp = exportedAsset.Imports[i];
        bool diff = vImp.ObjectName.Value?.ToString() != eImp.ObjectName.Value?.ToString()
                  || vImp.ClassName.Value?.ToString() != eImp.ClassName.Value?.ToString()
                  || vImp.ClassPackage.Value?.ToString() != eImp.ClassPackage.Value?.ToString()
                  || vImp.OuterIndex.Index != eImp.OuterIndex.Index;
        if (diff)
        {
            Console.WriteLine($"  [~] CHANGED import [{i}]:");
            Console.WriteLine($"       Vanilla:  {vImp.ObjectName.Value} (class={vImp.ClassName.Value}, pkg={vImp.ClassPackage.Value}, outer={vImp.OuterIndex.Index})");
            Console.WriteLine($"       Exported: {eImp.ObjectName.Value} (class={eImp.ClassName.Value}, pkg={eImp.ClassPackage.Value}, outer={eImp.OuterIndex.Index})");
            importDiffCount++;
        }
    }
}
if (importDiffCount == 0)
    Console.WriteLine("  No import differences found.");
Console.WriteLine();

// --- Export table comparison ---
Console.WriteLine("EXPORT TABLE COMPARISON:");
Console.WriteLine($"  Vanilla exports:  {vanillaAsset.Exports.Count}");
Console.WriteLine($"  Exported exports: {exportedAsset.Exports.Count}");
if (vanillaAsset.Exports.Count != exportedAsset.Exports.Count)
    Console.WriteLine($"  *** DIFFERENT EXPORT COUNT ***");
Console.WriteLine();

Console.WriteLine("--- Vanilla Exports ---");
for (int i = 0; i < vanillaAsset.Exports.Count; i++)
{
    var exp = vanillaAsset.Exports[i];
    Console.WriteLine($"  [{i}] Type={exp.GetType().Name}  ObjectName={exp.ObjectName}  SerialSize={exp.SerialSize}  ClassIndex={exp.ClassIndex}");
}

Console.WriteLine();
Console.WriteLine("--- Exported Exports ---");
for (int i = 0; i < exportedAsset.Exports.Count; i++)
{
    var exp = exportedAsset.Exports[i];
    Console.WriteLine($"  [{i}] Type={exp.GetType().Name}  ObjectName={exp.ObjectName}  SerialSize={exp.SerialSize}  ClassIndex={exp.ClassIndex}");
}

Console.WriteLine();
Console.WriteLine("--- Export Differences ---");
int maxExp = Math.Max(vanillaAsset.Exports.Count, exportedAsset.Exports.Count);
int exportDiffCount = 0;
for (int i = 0; i < maxExp; i++)
{
    if (i >= vanillaAsset.Exports.Count)
    {
        Console.WriteLine($"  [+] NEW export in exported [{i}]: {exportedAsset.Exports[i].ObjectName}");
        exportDiffCount++;
    }
    else if (i >= exportedAsset.Exports.Count)
    {
        Console.WriteLine($"  [-] REMOVED export from exported [{i}]: {vanillaAsset.Exports[i].ObjectName}");
        exportDiffCount++;
    }
    else
    {
        var vExp = vanillaAsset.Exports[i];
        var eExp = exportedAsset.Exports[i];
        if (vExp.SerialSize != eExp.SerialSize || vExp.GetType().Name != eExp.GetType().Name)
        {
            Console.WriteLine($"  [~] CHANGED export [{i}]: Type {vExp.GetType().Name}->{eExp.GetType().Name}, SerialSize {vExp.SerialSize}->{eExp.SerialSize}  ({vExp.ObjectName})");
            exportDiffCount++;
        }
    }
}
if (exportDiffCount == 0)
    Console.WriteLine("  No export differences found.");
Console.WriteLine();

// --- Name table comparison ---
Console.WriteLine("NAME TABLE COMPARISON:");
var vNames = vanillaAsset.GetNameMapIndexList();
var eNames = exportedAsset.GetNameMapIndexList();

Console.WriteLine($"  Vanilla name count:  {vNames.Count}");
Console.WriteLine($"  Exported name count: {eNames.Count}");

var vNameSet = new HashSet<string>(vNames.Select(n => n.ToString()));
var eNameSet = new HashSet<string>(eNames.Select(n => n.ToString()));
var onlyInVanilla = vNameSet.Except(eNameSet).ToList();
var onlyInExported = eNameSet.Except(vNameSet).ToList();

if (onlyInVanilla.Count > 0 || onlyInExported.Count > 0)
{
    if (onlyInVanilla.Count > 0)
    {
        Console.WriteLine($"  Names only in VANILLA ({onlyInVanilla.Count}):");
        foreach (var n in onlyInVanilla.Take(30))
            Console.WriteLine($"    - {n}");
        if (onlyInVanilla.Count > 30) Console.WriteLine($"    ... and {onlyInVanilla.Count - 30} more");
    }
    if (onlyInExported.Count > 0)
    {
        Console.WriteLine($"  Names only in EXPORTED ({onlyInExported.Count}):");
        foreach (var n in onlyInExported.Take(30))
            Console.WriteLine($"    - {n}");
        if (onlyInExported.Count > 30) Console.WriteLine($"    ... and {onlyInExported.Count - 30} more");
    }
}
else
{
    Console.WriteLine("  Name tables are identical (as sets).");
}

// Also show full name diffs by index
Console.WriteLine();
Console.WriteLine("--- Name Table by Index ---");
int maxNames = Math.Max(vNames.Count, eNames.Count);
int nameDiffCount = 0;
for (int i = 0; i < maxNames; i++)
{
    if (i >= vNames.Count)
    {
        Console.WriteLine($"  [+] NEW [{i}]: {eNames[i]}");
        nameDiffCount++;
    }
    else if (i >= eNames.Count)
    {
        Console.WriteLine($"  [-] REMOVED [{i}]: {vNames[i]}");
        nameDiffCount++;
    }
    else if (vNames[i].ToString() != eNames[i].ToString())
    {
        Console.WriteLine($"  [~] CHANGED [{i}]: {vNames[i]} -> {eNames[i]}");
        nameDiffCount++;
    }
}
if (nameDiffCount == 0)
    Console.WriteLine("  All name entries match by index.");
Console.WriteLine();

// --- Binary equality check using UAssetAPI ---
Console.WriteLine("UASSETAPI BINARY EQUALITY CHECK:");
try
{
    bool binEq = vanillaAsset.VerifyBinaryEquality();
    Console.WriteLine($"  VerifyBinaryEquality: {binEq}");
}
catch (Exception ex)
{
    Console.WriteLine($"  VerifyBinaryEquality threw: {ex.Message}");
}
Console.WriteLine();

// --- Conclusion ---
Console.WriteLine("========================================");
Console.WriteLine("  CONCLUSION");
Console.WriteLine("========================================");
bool filesIdentical = vHash == eHash;
if (filesIdentical)
{
    Console.WriteLine("  The exported file is IDENTICAL to vanilla.");
    Console.WriteLine("  The export is NOT modifying the combo tree data.");
    Console.WriteLine("  This likely means the combo tree patching is failing silently.");
}
else
{
    Console.WriteLine("  The exported file DIFFERS from vanilla.");
    Console.WriteLine($"  Import changes: {importDiffCount}, Export changes: {exportDiffCount}, Name changes: {nameDiffCount}");
    if (importDiffCount == 0 && exportDiffCount == 0 && nameDiffCount == 0)
    {
        Console.WriteLine("  WARNING: No structural differences found, but binary content differs.");
        Console.WriteLine("  This could mean serialization order or padding changed.");
    }
    else
    {
        Console.WriteLine("  The export IS modifying the combo tree data.");
    }
}

