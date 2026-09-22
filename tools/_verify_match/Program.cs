#nullable enable
using System;
using System.IO;
using System.Linq;
using SifuMovesetEditor;

var gameRoot = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu";
var contentDir = Path.Combine(gameRoot, "Content");
var moddedUasset = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\Fire Disciple\MainChar_ComboTree.uasset";
var vanillaGamePath = "Game/DB/_MainChar/Combos/MainChar_ComboTree";

if (!Directory.Exists(contentDir))
{
    Console.WriteLine($"FAIL: content dir missing: {contentDir}");
    return 1;
}
if (!File.Exists(moddedUasset))
{
    Console.WriteLine($"FAIL: modded uasset missing: {moddedUasset}");
    return 1;
}

Console.WriteLine("Initializing AnimationParser...");
var parser = new AnimationParser();
parser.Initialize(gameRoot, contentDir);

Console.WriteLine("Loading vanilla MainChar combo tree...");
var vanilla = parser.LoadComboTreeFromPath(vanillaGamePath, "BareHands");
if (vanilla == null)
{
    Console.WriteLine("FAIL: vanilla combo tree null");
    return 1;
}

int vanillaTreeCount = vanilla.Nodes
    .Where(n => n.TreeIndex >= 0)
    .Select(n => n.TreeIndex)
    .Distinct()
    .Count();
int vanillaMoves = vanilla.Nodes.Count(n => n.TreeIndex >= 0 && !n.IsRedirect && !n.IsRoot);
Console.WriteLine($"Vanilla: {vanilla.Nodes.Count} nodes, {vanillaTreeCount} tree indices, {vanillaMoves} non-root moves");

Console.WriteLine("Reading modded combo nodes...");
var modded = parser.ReadModdedComboNodes(moddedUasset);
Console.WriteLine($"Modded: {modded.Count} nodes");
if (modded.Count == 0)
{
    Console.WriteLine("FAIL: no modded nodes");
    return 1;
}

Console.WriteLine("Applying modded combo to vanilla...");
var (moves, retargets, unmatched) = parser.ApplyModdedComboToVanilla(vanilla, modded, "BareHands");
Console.WriteLine($"Apply result: {moves} move change(s), {retargets} retarget(s), {unmatched} unmatched");
if (modded.Count != vanillaTreeCount)
    Console.WriteLine($"Tree size mismatch: {modded.Count} mod node(s) vs {vanillaTreeCount} vanilla");

var imported = vanilla.Nodes.Where(n => n.IsImportedFromMod).ToList();
Console.WriteLine($"IsImportedFromMod=true: {imported.Count}");
Console.WriteLine();
Console.WriteLine("--- Imported moves (changed nodes) ---");
foreach (var n in imported.OrderBy(n => n.TreeIndex).ThenBy(n => n.Id))
{
    Console.WriteLine($"  tree[{n.TreeIndex}] '{n.Name}' display='{n.DisplayName}' changed='{n.ImportedDisplayName}'");
    Console.WriteLine($"    db={n.SourceDBPath}");
    Console.WriteLine($"    anim={n.AnimPath}");
}

var stillVanilla = vanilla.Nodes
    .Where(n => n.TreeIndex >= 0 && !n.IsRedirect && !n.IsRoot && !n.IsImportedFromMod)
    .ToList();
Console.WriteLine();
Console.WriteLine($"Unchanged vanilla non-root nodes: {stillVanilla.Count}");
foreach (var n in stillVanilla.OrderBy(n => n.TreeIndex).ThenBy(n => n.Id).Take(25))
{
    var db = string.IsNullOrEmpty(n.DefaultDBPath) ? n.SourceDBPath : n.DefaultDBPath;
    Console.WriteLine($"  tree[{n.TreeIndex}] '{n.Name}' db={db}");
}
if (stillVanilla.Count > 25)
    Console.WriteLine($"  ... and {stillVanilla.Count - 25} more");

Console.WriteLine();
Console.WriteLine("Done.");
return moves > 0 ? 0 : 2;
