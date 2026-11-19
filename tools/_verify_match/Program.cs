#nullable enable
using System;
using System.IO;
using System.Linq;
using SifuMovesetEditor;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

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
var provenanceFailures = new System.Collections.Generic.List<string>();

foreach (var c in cases)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 80));
    Console.WriteLine($"CASE: {c.Label}");

    var vanilla = parser.LoadComboTreeFromPath(c.VanillaGamePath, c.Weapon)!;
    var modded = parser.ReadModdedComboNodes(c.ModdedUasset);

    // Provenance smoke check: every resolved DB path must carry the raw m_Attacks FName
    // it was read from (index-aligned), otherwise export cannot match a mod-keyed tree.
    int slotKeyMismatch = modded.Count(m => m.AttackSlotKeys.Count != m.AttackDbPaths.Count);
    Console.WriteLine($"PROVENANCE READ: nodes={modded.Count} keys={modded.Sum(m => m.AttackSlotKeys.Count)} mismatchedNodes={slotKeyMismatch}");
    foreach (var m in modded.Where(m => m.AttackSlotKeys.Count > 0).Take(3))
    {
        Console.WriteLine($"  m[{m.TreeIndex}] slot='{m.AttackSlotKeys[0]}' -> db='{m.AttackDbPaths[0]}'");
    }

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

    var apply = parser.ApplyModdedComboToVanilla(vanilla, modded, c.Weapon);
    int moves = apply.MoveChanges, retargets = apply.RetargetChanges, unmatched = apply.Unmatched;
    Console.WriteLine($"Apply: moves={moves} retargets={retargets} unmatched={unmatched} imported={vanilla.Nodes.Count(n => n.IsImportedFromMod)}");
    results.Add((c.Label, moves, unmatched));

    // Export-provenance smoke check: imported vanilla nodes must remember the mod tree's
    // own slot key, and that key must exist in the mod tree (that is what export matches).
    var imported = vanilla.Nodes.Where(n => n.IsImportedFromMod).ToList();
    var moddedKeys = new HashSet<string>(
        modded.SelectMany(m => m.AttackSlotKeys).Select(NormalizeSlotKey), StringComparer.OrdinalIgnoreCase);
    int withProv = imported.Count(n => !string.IsNullOrEmpty(n.ImportedSlotKey) && !string.IsNullOrEmpty(n.ImportedDbPath));
    int provInTree = imported.Count(n => moddedKeys.Contains(NormalizeSlotKey(n.ImportedSlotKey)));
    int divergent = imported.Count(n => !string.Equals(
        Path.GetFileNameWithoutExtension(n.ImportedSlotKey),
        Path.GetFileNameWithoutExtension(n.DefaultDBPath), StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"PROVENANCE EXPORT: imported={imported.Count} withProvenance={withProv} " +
        $"keyFoundInModTree={provInTree} vanillaDivergentKeys={divergent}");
    if (imported.Count > 0 && (withProv < imported.Count || provInTree < imported.Count))
        provenanceFailures.Add($"{c.Label}: imported={imported.Count} withProvenance={withProv} keyFound={provInTree}");

    // The user-reported bug: export used to match only the vanilla slot DB name, which
    // misses every entry in a mod-keyed tree (0 swaps, tree never written). Verify the
    // legacy key set fails and the provenance key set matches all imported nodes.
    int legacyMatched = imported.Count(n => moddedKeys.Contains(NormalizeSlotKey(n.DefaultDBPath)));
    int provenanceMatched = imported.Count(n =>
        moddedKeys.Contains(NormalizeSlotKey(n.DefaultDBPath))
        || (!string.IsNullOrEmpty(n.ImportedSlotKey) && moddedKeys.Contains(NormalizeSlotKey(n.ImportedSlotKey)))
        || (!string.IsNullOrEmpty(n.ImportedDbPath) && moddedKeys.Contains(NormalizeSlotKey(n.ImportedDbPath))));
    Console.WriteLine($"EXPORT MATCH: imported={imported.Count} legacyKeyMatches={legacyMatched} provenanceKeyMatches={provenanceMatched}");
    if (imported.Count > 0 && provenanceMatched < imported.Count)
        provenanceFailures.Add($"{c.Label}: provenance matching still misses {imported.Count - provenanceMatched} node(s)");

    // Full export simulation: find the real m_Attacks entry for each imported node with the
    // provenance key set, then run the export's import-name sanity check against the entry's
    // actual import. This is the check that silently broke the reported "replaced move does
    // nothing" export: the tree entry points at the mod's DB, not the vanilla slot DB.
    var entries = ReadAttackEntries(c.ModdedUasset);
    var ambiguous = entries
        .GroupBy(e => Path.GetFileNameWithoutExtension(e.Key), StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    int entryFound = 0, importCheckOk = 0, legacyImportOk = 0;
    foreach (var n in imported)
    {
        var cands = new List<string> { NormalizeSlotKey(n.DefaultDBPath) };
        if (!string.IsNullOrEmpty(n.ImportedSlotKey)) cands.Add(NormalizeSlotKey(n.ImportedSlotKey));
        if (!string.IsNullOrEmpty(n.ImportedDbPath)) cands.Add(NormalizeSlotKey(n.ImportedDbPath));

        var entry = entries.FirstOrDefault(en => cands.Any(k => SlotMatch(en.Key, k, ambiguous)));
        if (entry.Key == null)
        {
            Console.WriteLine($"    EXPORT SIM MISS: no m_Attacks entry for '{n.DisplayName}' cands=[{string.Join(" | ", cands)}]");
            continue;
        }
        entryFound++;

        var shorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in new[] { n.DefaultDBPath, n.ImportedSlotKey, n.ImportedDbPath })
        {
            var s = Path.GetFileNameWithoutExtension(p);
            if (!string.IsNullOrEmpty(s)) shorts.Add(s);
        }
        bool ok = shorts.Any(s => entry.PkgPath.Contains(s, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(entry.ImportName, s, StringComparison.OrdinalIgnoreCase));
        // Legacy check (no ImportedDbPath): proves the import-name sanity check alone also
        // blocked the rewrite on a mod-keyed tree.
        var shortsLegacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in new[] { n.DefaultDBPath, n.ImportedSlotKey })
        {
            var s = Path.GetFileNameWithoutExtension(p);
            if (!string.IsNullOrEmpty(s)) shortsLegacy.Add(s);
        }
        if (shortsLegacy.Any(s => entry.PkgPath.Contains(s, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(entry.ImportName, s, StringComparison.OrdinalIgnoreCase)))
            legacyImportOk++;
        if (ok) importCheckOk++;
        else
            Console.WriteLine($"    EXPORT SIM IMPORT-MISMATCH: {n.DisplayName} key='{entry.Key}' import='{entry.ImportName}' pkg='{entry.PkgPath}' shorts=[{string.Join(", ", shorts)}]");
    }
    Console.WriteLine($"EXPORT SIM: entries={entries.Count} imported={imported.Count} entryFound={entryFound} importCheckOk={importCheckOk} legacyImportCheckOk={legacyImportOk}");
    if (imported.Count > 0 && (entryFound < imported.Count || importCheckOk < imported.Count))
        provenanceFailures.Add($"{c.Label}: export sim entryFound={entryFound}/{imported.Count} importCheckOk={importCheckOk}/{imported.Count}");
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
foreach (var f in provenanceFailures)
{
    allPass = false;
    Console.WriteLine($"FAIL  PROVENANCE {f}");
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

// Mirrors ExportDialog.NormalizeSlotKey: leading slash and a ".Asset" suffix appended
// when the FName is stored without one.
static string NormalizeSlotKey(string path)
{
    if (string.IsNullOrEmpty(path)) return "";
    path = path.Replace("\\", "/").TrimStart('/');
    path = "/" + path;
    var lastSlash = path.LastIndexOf('/');
    var lastDot = path.LastIndexOf('.');
    if (lastDot < lastSlash)
        path = path + "." + path.Substring(lastSlash + 1);
    return path;
}

// Mirrors ExportDialog.SlotKeysMatch (exact, normalized, then unambiguous short name).
static bool SlotMatch(string keyStr, string normalizedSlotKey, HashSet<string> ambiguous)
{
    if (string.IsNullOrEmpty(keyStr) || string.IsNullOrEmpty(normalizedSlotKey)) return false;
    if (string.Equals(keyStr, normalizedSlotKey, StringComparison.OrdinalIgnoreCase)) return true;
    if (string.Equals(NormalizeSlotKey(keyStr), normalizedSlotKey, StringComparison.OrdinalIgnoreCase)) return true;
    var keyFile = Path.GetFileNameWithoutExtension(keyStr);
    if (!string.Equals(keyFile, Path.GetFileNameWithoutExtension(normalizedSlotKey), StringComparison.OrdinalIgnoreCase))
        return false;
    return !ambiguous.Contains(keyFile);
}

// Every m_Attacks TMap entry in the tree: its FName key and the import it points at.
static List<(string Key, string ImportName, string PkgPath)> ReadAttackEntries(string uassetPath)
{
    var list = new List<(string Key, string ImportName, string PkgPath)>();
    var asset = new UAssetAPI.UAsset(uassetPath, UAssetAPI.UnrealTypes.EngineVersion.VER_UE4_26, null, UAssetAPI.CustomSerializationFlags.None);
    var maps = new List<UAssetAPI.PropertyTypes.Objects.MapPropertyData>();
    foreach (var exp in asset.Exports)
    {
        if (exp is not UAssetAPI.ExportTypes.NormalExport ne || ne.Data == null) continue;
        foreach (var p in ne.Data)
        {
            if (p is UAssetAPI.PropertyTypes.Objects.MapPropertyData mp && p.Name?.Value?.ToString() == "m_Attacks")
                maps.Add(mp);
            FindMaps(p, maps);
        }
    }

    foreach (var map in maps)
    {
        if (map.Value == null) continue;
        foreach (var kvp in map.Value)
        {
            string key = kvp.Key is UAssetAPI.PropertyTypes.Objects.NamePropertyData knp
                ? knp.Value?.Value?.ToString() ?? ""
                : kvp.Key?.ToString() ?? "";
            if (string.IsNullOrEmpty(key)) continue;

            if (kvp.Value is not UAssetAPI.PropertyTypes.Objects.ObjectPropertyData op || op.Value == null) continue;
            int idx = -(op.Value.Index + 1);
            string importName = "", pkgPath = "";
            if (idx >= 0 && idx < asset.Imports.Count)
            {
                var imp = asset.Imports[idx];
                importName = imp.ObjectName?.Value?.ToString() ?? "";
                int pkgIdx = -(imp.OuterIndex.Index + 1);
                if (pkgIdx >= 0 && pkgIdx < asset.Imports.Count)
                    pkgPath = asset.Imports[pkgIdx].ObjectName?.Value?.ToString() ?? "";
            }
            list.Add((key, importName, pkgPath));
        }
    }
    return list;

    static void FindMaps(PropertyData p, List<UAssetAPI.PropertyTypes.Objects.MapPropertyData> maps)
    {
        if (p is UAssetAPI.PropertyTypes.Structs.StructPropertyData sp && sp.Value != null)
        {
            foreach (var child in sp.Value)
            {
                if (child is UAssetAPI.PropertyTypes.Objects.MapPropertyData mp && child.Name?.Value?.ToString() == "m_Attacks")
                    maps.Add(mp);
                FindMaps(child, maps);
            }
        }
        else if (p is UAssetAPI.PropertyTypes.Objects.ArrayPropertyData ap && ap.Value != null)
        {
            foreach (var child in ap.Value)
            {
                if (child is UAssetAPI.PropertyTypes.Objects.MapPropertyData mp && child.Name?.Value?.ToString() == "m_Attacks")
                    maps.Add(mp);
                FindMaps(child, maps);
            }
        }
    }
}

Console.WriteLine();
Console.WriteLine("Done.");
return 0;
