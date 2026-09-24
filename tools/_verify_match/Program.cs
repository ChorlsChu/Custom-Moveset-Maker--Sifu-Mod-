#nullable enable
using System;
using System.IO;
using System.Linq;
using SifuMovesetEditor;

var gameRoot = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu";
var contentDir = Path.Combine(gameRoot, "Content");
var extractedRoot = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks";

var cases = new (string Label, string ModdedUasset, string VanillaGamePath, string Weapon)[]
{
    ("FT Staff",
        Path.Combine(extractedRoot, "Final_Test_Then_I_Go_Sleep_I_Pramise", "_MainChar", "Combos", "Attacks", "Weapons", "Staff", "MainChar_Staff_ComboTree.uasset"),
        "Game/DB/_MainChar/Combos/Attacks/Weapons/Staff/MainChar_Staff_ComboTree",
        "MainChar_Staff"),
    ("FT Blades",
        Path.Combine(extractedRoot, "Final_Test_Then_I_Go_Sleep_I_Pramise", "_MainChar", "Combos", "Attacks", "Weapons", "Blades", "MainChar_Blades_ComboTree.uasset"),
        "Game/DB/_MainChar/Combos/Attacks/Weapons/Blades/MainChar_Blades_ComboTree",
        "MainChar_Blade"),
    ("FT Barehands",
        Path.Combine(extractedRoot, "Final_Test_Then_I_Go_Sleep_I_Pramise", "_MainChar", "Combos", "MainChar_ComboTree.uasset"),
        "Game/DB/_MainChar/Combos/MainChar_ComboTree",
        "MainChar_Barehands"),
    ("FD MainChar",
        Path.Combine(extractedRoot, "Fire Disciple", "MainChar_ComboTree.uasset"),
        "Game/DB/_MainChar/Combos/MainChar_ComboTree",
        "MainChar_Barehands"),
};

Console.WriteLine("Initializing AnimationParser...");
var parser = new AnimationParser();
parser.Initialize(gameRoot, contentDir);

var results = new System.Collections.Generic.List<(string Label, int Moves, int Unmatched)>();

foreach (var c in cases)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 80));
    Console.WriteLine($"CASE: {c.Label}");

    var vanilla = parser.LoadComboTreeFromPath(c.VanillaGamePath, c.Weapon)!;
    var modded = parser.ReadModdedComboNodes(c.ModdedUasset);

    var vanillaMoves = vanilla.Nodes
        .Where(n => n.TreeIndex >= 0 && !n.IsRedirect && !n.IsRoot)
        .GroupBy(n => n.TreeIndex)
        .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Id).ToList());

    Console.WriteLine($"vanillaMoves groups={vanillaMoves.Count} totalNodes={vanillaMoves.Values.Sum(v => v.Count)} modded={modded.Count}");
    Console.WriteLine("ALL MODDED NODES:");
    foreach (var m in modded)
    {
        var enemy = m.AttackDbPaths.Where(p => p.Contains("/AI/Archetypes/", StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"  m[{m.TreeIndex}] name='{m.Name}' paths={m.AttackDbPaths.Count}" +
            (enemy.Count > 0 ? $" ENEMY={enemy.Count}" : ""));
        foreach (var p in m.AttackDbPaths)
            Console.WriteLine($"      {p}");
    }

    Console.WriteLine("--- vanilla tree groups vs matched modded ---");
    foreach (var kvp in vanillaMoves.OrderBy(k => k.Key))
    {
        var vList = kvp.Value;
        var nameRaw = vList.Select(n => n.Name).FirstOrDefault(s => !string.IsNullOrEmpty(s) && s != "None") ?? "";
        var nk = nameRaw;
        foreach (var prefix in new[] { "MainChar_Attack_barehands_", "MainChar_attack_barehands_", "MainChar_Attack_", "MainChar_attack_", "MainChar_", "MC_", "Attack barehands ", "attack barehands " })
        {
            if (nk.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { nk = nk[prefix.Length..]; break; }
        }
        nk = nk.Replace("_", " ").Replace("-", " ").Trim();
        while (nk.Contains("  ")) nk = nk.Replace("  ", " ");

        ModdedComboNodeInfo? matched = null;
        string matchHow = "NONE";
        if (!string.IsNullOrEmpty(nk))
        {
            matched = modded.FirstOrDefault(m =>
            {
                var k = m.Name ?? "";
                foreach (var prefix in new[] { "MainChar_Attack_barehands_", "MainChar_attack_barehands_", "MainChar_Attack_", "MainChar_attack_", "MainChar_", "MC_", "Attack barehands ", "attack barehands " })
                {
                    if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { k = k[prefix.Length..]; break; }
                }
                k = k.Replace("_", " ").Replace("-", " ").Trim();
                while (k.Contains("  ")) k = k.Replace("  ", " ");
                return string.Equals(k, nk, StringComparison.OrdinalIgnoreCase);
            });
            if (matched != null) matchHow = "name";
        }
        if (matched == null)
        {
            matched = modded.FirstOrDefault(m => m.TreeIndex == kvp.Key);
            if (matched != null) matchHow = "index";
        }

        var vDbs = vList
            .Select(n => Norm(string.IsNullOrEmpty(n.DefaultDBPath) ? n.SourceDBPath : n.DefaultDBPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mPaths = matched?.AttackDbPaths.Select(Norm).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();

        var onlyVan = vDbs.Except(mPaths, StringComparer.OrdinalIgnoreCase).ToList();
        var onlyMod = mPaths.Except(vDbs, StringComparer.OrdinalIgnoreCase).ToList();

        Console.WriteLine($"  tree[{kvp.Key}] '{nameRaw}' match={matchHow}" +
            (matched != null ? $" mIdx={matched.TreeIndex} mName='{matched.Name}'" : "") +
            $" vanOnly={onlyVan.Count} modOnly={onlyMod.Count}");
        if (onlyMod.Count > 0 || onlyVan.Count > 0)
        {
            foreach (var p in onlyVan) Console.WriteLine($"    +van {p}");
            foreach (var p in onlyMod) Console.WriteLine($"    +mod {p}");
        }
    }

    var (moves, retargets, unmatched) = parser.ApplyModdedComboToVanilla(vanilla, modded, c.Weapon);
    Console.WriteLine($"Apply: moves={moves} retargets={retargets} unmatched={unmatched} imported={vanilla.Nodes.Count(n => n.IsImportedFromMod)}");
    results.Add((c.Label, moves, unmatched));
}

Console.WriteLine();
Console.WriteLine("=== SUMMARY ===");
bool allPass = true;
foreach (var r in results)
{
    bool pass = r.Moves > 0 && r.Unmatched == 0;
    if (!pass) allPass = false;
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {r.Label}: moves={r.Moves} unmatched={r.Unmatched}");
}
Console.WriteLine(allPass ? "ALL PASS" : "SOME FAILED");

static string Norm(string p)
{
    if (string.IsNullOrEmpty(p)) return "";
    p = p.Replace("\\", "/");
    if (p.StartsWith("/")) p = p.Substring(1);
    var contentIdx = p.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
    if (contentIdx >= 0) p = "Game/" + p.Substring(contentIdx + "/Content/".Length);
    var lastSlash = p.LastIndexOf('/');
    var lastDot = p.LastIndexOf('.');
    if (lastDot > lastSlash) p = p[..lastDot];
    return p;
}

Console.WriteLine();
Console.WriteLine("Done.");
return 0;
