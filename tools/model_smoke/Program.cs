using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SifuMovesetEditor;

internal static class Program
{
    private static int _fails;

    private static void Check(bool ok, string label, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}" +
                          (string.IsNullOrEmpty(detail) ? "" : $" -> {detail}"));
        if (!ok) _fails++;
    }

    private static int Main()
    {
        var content = @"C:\Users\Charles\Downloads\Sifu Modding\Movesets Prototypes\Moveset Maker\SifuMovesetEditor\bin\Debug\net10.0-windows\GameContent\Content";
        var bp = Path.Combine(Path.GetTempPath(), "SifuBPS", "Sifu", "Content");
        var outDir = Path.Combine(Path.GetTempPath(), "SifuModelSmoke");

        Console.WriteLine("== 1. Mesh catalog ==");
        var meshes = ModelCatalog.ScanMeshes(content);
        Check(meshes.Count > 0, "meshes found", meshes.Count.ToString());
        var groups = meshes
            .Select(m => m.Group)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var g in groups)
        {
            int n = meshes.Count(m => m.Group.Equals(g, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"    group {g}: {n}");
        }
        var yang = meshes.FirstOrDefault(m =>
            m.GamePath.Contains("SK_Yang_Hideout05", StringComparison.OrdinalIgnoreCase));
        Check(yang != null, "Yang Hideout05 in catalog", yang?.GamePath ?? "<missing>");
        if (yang != null)
            Console.WriteLine($"    sample entry: {yang.Group} / {yang.DisplayName} / {yang.GamePath}");
        var dupPaths = meshes.GroupBy(m => m.GamePath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).ToList();
        Check(dupPaths.Count == 0, "no duplicate GamePaths",
            string.Join(", ", dupPaths.Select(d => d.Key)));

        Console.WriteLine("== 2. Family helpers ==");
        Check(ModelCatalog.FamilyDirForVariant("Grunt_Base") == "Grunt",
            "FamilyDirForVariant(Grunt_Base)", ModelCatalog.FamilyDirForVariant("Grunt_Base") ?? "<null>");
        Check(ModelCatalog.FamilyDirForVariant("Servant") == "Servant",
            "FamilyDirForVariant(Servant)", ModelCatalog.FamilyDirForVariant("Servant") ?? "<null>");
        Check(ModelCatalog.FamilyDirForVariant(null) == null,
            "FamilyDirForVariant(null) -> null");
        var gruntTags = ModelCatalog.GetFamilyVariantTags("Grunt");
        Console.WriteLine($"    Grunt variant tags ({gruntTags.Count}): {string.Join(", ", gruntTags)}");
        Check(gruntTags.Count > 0, "Grunt family has variant tags");
        Check(UnitPropertiesManager.AllVariantTags.Count > 0,
            "AllVariantTags non-empty", UnitPropertiesManager.AllVariantTags.Count.ToString());

        Console.WriteLine("== 2b. Focus family keys vs scenario files ==");
        int focusScenarioOverlap = 0;
        foreach (var tag in UnitPropertiesManager.AllVariantTags)
        {
            var key = UnitPropertiesManager.GetFocusFamilyKey(content, tag);
            if (key != null && key.Contains("Scenario", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"    OVERLAP: {tag} -> {key}");
                focusScenarioOverlap++;
            }
        }
        Check(focusScenarioOverlap == 0, "no focus family key is a Scenario file");

        Console.WriteLine("== 3. Scenario counts ==");
        var expectedLocal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["BigGuy"] = 17, ["Bodyguard"] = 9, ["Fajar"] = 6, ["Fengjie"] = 4,
            ["FireDisciple"] = 15, ["FlashKick"] = 17, ["Grunt"] = 24, ["Kuroki"] = 5,
            ["Sean"] = 10, ["Servant"] = 11, ["Sifu"] = 5, ["Yang"] = 6, ["Shared"] = 0,
        };
        foreach (var fam in expectedLocal.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var local = ModelCatalog.LocalScenarioRels(content, fam);
            var bpRels = ModelCatalog.BlueprintScenarioRels(bp, fam);
            Console.WriteLine($"    {fam}: local={local.Count} bp={bpRels.Count}");
            Check(local.Count == expectedLocal[fam], $"{fam} local count",
                $"{local.Count} (expected {expectedLocal[fam]})");
        }
        int gruntBp = ModelCatalog.BlueprintScenarioRels(bp, "Grunt").Count;
        Check(gruntBp == 4, "Grunt bp count", $"{gruntBp} (expected 4)");
        int servantBp = ModelCatalog.BlueprintScenarioRels(bp, "Servant").Count;
        Check(servantBp == 0, "Servant bp count", $"{servantBp} (expected 0)");

        var gruntBpList = ModelCatalog.BlueprintScenarioRels(bp, "Grunt");
        foreach (var r in gruntBpList) Console.WriteLine($"      bp: {r}");

        Console.WriteLine("== 3b. ScenarioRelsForVariant (per-variant tier mapping) ==");
        var expectedVariant = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Grunt_Base"] = 11, ["Grunt_Advanced"] = 9, ["Grunt_Miniboss"] = 4,
            ["Bodyguard_Base"] = 3, ["Bodyguard_Advanced"] = 5, ["Bodyguard_Miniboss"] = 1,
            ["BigGuy_Base"] = 6, ["BigGuy_Advanced"] = 8, ["BigGuy_Miniboss"] = 3,
            ["FlashKick_Base"] = 5, ["FlashKick_Advanced"] = 8, ["FlashKick_Miniboss"] = 3,
            ["FD_Base"] = 5, ["FD_Advanced"] = 4, ["FD_Miniboss"] = 5,
            ["Servant"] = 11, ["Fajar_P1"] = 6, ["Sean_P2"] = 10,
        };
        foreach (var tag in expectedVariant.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var rels = ModelCatalog.ScenarioRelsForVariant(content, tag);
            Console.WriteLine($"    {tag}: {rels.Count}");
            Check(rels.Count == expectedVariant[tag], $"{tag} scenario count",
                $"{rels.Count} (expected {expectedVariant[tag]})");
        }
        var gruntTierSum = new[] { "Grunt_Base", "Grunt_Advanced", "Grunt_Miniboss" }
            .Sum(t => ModelCatalog.ScenarioRelsForVariant(content, t).Count);
        Check(gruntTierSum == ModelCatalog.LocalScenarioRels(content, "Grunt").Count,
            "Grunt tiers partition all 24 scenarios", gruntTierSum.ToString());
        var fdTierSum = new[] { "FD_Base", "FD_Advanced", "FD_Miniboss" }
            .Sum(t => ModelCatalog.ScenarioRelsForVariant(content, t).Count);
        Check(fdTierSum == 14, "FireDisciple tiers cover 14 of 15 (excludes _OLD)", fdTierSum.ToString());
        var ghostHit = ModelCatalog.ScenarioRelsForVariant(content, "FlashKick_Advanced")
            .Any(r => r.Contains("Ghost", StringComparison.OrdinalIgnoreCase));
        Check(!ghostHit, "ArenaGhost stays vanilla (unclassified)");
        Check(!ModelCatalog.IsFamilyWideScenarioFamily("Grunt")
            && ModelCatalog.IsFamilyWideScenarioFamily("Sean"),
            "family-wide classification");

        Console.WriteLine("== 4. ApplyModelOverrides (Grunt + Servant -> Yang Hideout05) ==");
        if (yang == null)
        {
            Check(false, "aborting: no target mesh");
            return 1;
        }
        try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        Directory.CreateDirectory(outDir);

        string? Resolve(string rel)
        {
            var v = Path.Combine(content, rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (File.Exists(v)) return v;
            var b = Path.Combine(bp, rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (File.Exists(b)) return b;
            return null;
        }

        var props = new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase)
        {
            ["Grunt_Base"] = new UnitProperties { ModelOverride = yang.GamePath },
            ["Servant_Base"] = new UnitProperties { ModelOverride = yang.GamePath },
        };

        var written = UnitPropertiesManager.ApplyModelOverrides(
            content, outDir, props, bp, Resolve, Console.WriteLine);
        Console.WriteLine($"  written: {written.Count}");
        Check(written.Count == 26, "26 scenarios written (Grunt_Base 11, Servant 11, bp 4)",
            written.Count.ToString());

        var localWritten = written.Where(r => r.StartsWith("DB/", StringComparison.OrdinalIgnoreCase)).ToList();
        var bpWritten = written.Where(r => r.StartsWith("Blueprints/", StringComparison.OrdinalIgnoreCase)).ToList();
        Check(localWritten.Count == 22, "22 local DB scenarios", localWritten.Count.ToString());
        Check(bpWritten.Count == 4, "4 Blueprint scenarios", bpWritten.Count.ToString());
        Check(written.All(r => !r.Contains("ArenaAdv", StringComparison.OrdinalIgnoreCase)),
            "no Advanced scenarios staged for a Base-only pick");

        var sample = written.FirstOrDefault(r =>
            r.Contains("Grunt", StringComparison.OrdinalIgnoreCase) &&
            r.StartsWith("DB/", StringComparison.OrdinalIgnoreCase));
        Check(sample != null, "have a Grunt local sample", sample ?? "<none>");
        if (sample != null)
        {
            var stagedU = Path.Combine(outDir, sample.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            var text = Encoding.ASCII.GetString(File.ReadAllBytes(stagedU));
            Check(text.Contains("SK_Yang_Hideout05", StringComparison.Ordinal),
                "staged Grunt scenario references SK_Yang_Hideout05");
        }
        var bpSample = bpWritten.FirstOrDefault();
        if (bpSample != null)
        {
            var stagedB = Path.Combine(outDir, bpSample.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            Check(File.Exists(stagedB), "bp scenario staged on disk", bpSample);
            var text = Encoding.ASCII.GetString(File.ReadAllBytes(stagedB));
            Check(text.Contains("SK_Yang_Hideout05", StringComparison.Ordinal),
                "staged bp scenario references SK_Yang_Hideout05");
        }
        int stagedUassets = Directory.GetFiles(outDir, "*.uasset", SearchOption.AllDirectories).Length;
        Check(stagedUassets == written.Count, "on-disk uasset count matches written",
            $"{stagedUassets} vs {written.Count}");

        Console.WriteLine("== 5. Per-variant picks do not conflict ==");
        var tierProps = new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase)
        {
            ["Grunt_Base"] = new UnitProperties { ModelOverride = yang.GamePath },
            ["Grunt_Advanced"] = new UnitProperties { ModelOverride = "/Game/Characters/PNJ/SK_SomeoneElse" },
        };
        var tierOut = Path.Combine(Path.GetTempPath(), "SifuModelSmoke2");
        try { if (Directory.Exists(tierOut)) Directory.Delete(tierOut, true); } catch { }
        Directory.CreateDirectory(tierOut);
        var tierWritten = UnitPropertiesManager.ApplyModelOverrides(
            content, tierOut, tierProps, bp, Resolve, Console.WriteLine);
        Check(tierWritten.Count == 24, "Grunt_Base + Grunt_Advanced stage 11+9+4 bp",
            tierWritten.Count.ToString());

        var baseSample = tierWritten.FirstOrDefault(r =>
            r.Contains("Grunt_Scenario_Base", StringComparison.OrdinalIgnoreCase));
        var advSample = tierWritten.FirstOrDefault(r =>
            r.Contains("_Advanced", StringComparison.OrdinalIgnoreCase));
        Check(baseSample != null && advSample != null, "have Base and Advanced samples",
            $"{baseSample ?? "<none>"} / {advSample ?? "<none>"}");
        if (baseSample != null)
        {
            var text = Encoding.ASCII.GetString(File.ReadAllBytes(
                Path.Combine(tierOut, baseSample.Replace('/', Path.DirectorySeparatorChar) + ".uasset")));
            Check(text.Contains("SK_Yang_Hideout05", StringComparison.Ordinal),
                "Base scenario got the Base pick");
        }
        if (advSample != null)
        {
            var text = Encoding.ASCII.GetString(File.ReadAllBytes(
                Path.Combine(tierOut, advSample.Replace('/', Path.DirectorySeparatorChar) + ".uasset")));
            Check(text.Contains("SK_SomeoneElse", StringComparison.Ordinal),
                "Advanced scenario got the Advanced pick");
        }

        Console.WriteLine("== 6. Boss family keeps first pick on conflict ==");
        var bossProps = new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sean_P1"] = new UnitProperties { ModelOverride = yang.GamePath },
            ["Sean_P2"] = new UnitProperties { ModelOverride = "/Game/Characters/PNJ/SK_SomeoneElse" },
        };
        var bossOut = Path.Combine(Path.GetTempPath(), "SifuModelSmoke3");
        try { if (Directory.Exists(bossOut)) Directory.Delete(bossOut, true); } catch { }
        Directory.CreateDirectory(bossOut);
        var bossWritten = UnitPropertiesManager.ApplyModelOverrides(
            content, bossOut, bossProps, null, Resolve, Console.WriteLine);
        Check(bossWritten.Count == 10, "Sean family-wide stages all 10 scenarios once",
            bossWritten.Count.ToString());
        var bossSample = bossWritten.FirstOrDefault();
        if (bossSample != null)
        {
            var text = Encoding.ASCII.GetString(File.ReadAllBytes(
                Path.Combine(bossOut, bossSample.Replace('/', Path.DirectorySeparatorChar) + ".uasset")));
            Check(text.Contains("SK_Yang_Hideout05", StringComparison.Ordinal),
                "conflicting boss picks keep the first pick");
        }

        Console.WriteLine("== 7. Roster helpers ==");
        Check(ModelCatalog.MeshGroupForKey("FireDisciple") == "Disicple",
            "MeshGroupForKey(FireDisciple) -> Disicple");
        Check(ModelCatalog.MeshGroupForKey("Bodyguard") == "BodyGuards",
            "MeshGroupForKey(Bodyguard) -> BodyGuards");
        Check(ModelCatalog.MeshGroupForKey("Grunt") == "Grunt", "MeshGroupForKey(Grunt)");
        Check(ModelCatalog.MeshGroupForKey("Nope") == null, "MeshGroupForKey(unknown) -> null");
        Check(ModelCatalog.AIFamilyDirForKey("FlashKick") == "Flashkick",
            "AIFamilyDirForKey(FlashKick) -> Flashkick (lowercase k)");
        Check(ModelCatalog.AIFamilyDirForKey("MainChar") == null,
            "MainChar is source-only (no target dir)");
        Check(ModelCatalog.IsModelSetKey("Servant") && !ModelCatalog.IsModelSetKey(null),
            "IsModelSetKey");

        Check(ModelCatalog.HideoutOfPath("/Game/Characters/PNJ/Grunt/Hideout02/Male/SK_X") == 2,
            "HideoutOfPath from /Hideout02/ dir");
        Check(ModelCatalog.HideoutOfPath(
            "/Game/Characters/PNJ/Servant/M/Meshes/Hideout00/SK_Servant_M_Hideout00_X") == 0,
            "HideoutOfPath zero-padded Hideout00 -> 0");
        Check(ModelCatalog.HideoutOfPath("SK_Servant_HD3_X") == 3, "HideoutOfPath from _HD3 token");
        Check(ModelCatalog.HideoutOfPath("SK_NoHideout") == null, "HideoutOfPath none -> null");

        Check(ModelCatalog.GenderOfMesh("/Game/Characters/MainChar/M/SK_MainChar_01", "Grunt") == "M",
            "GenderOfMesh M segment");
        Check(ModelCatalog.GenderOfMesh("/Game/Characters/MainChar/W/SK_MainChar_01", "Grunt") == "W",
            "GenderOfMesh W segment");
        Check(ModelCatalog.GenderOfMesh("/Game/Characters/PNJ/Servant/SK_Servant_W_01", "Grunt") == "W",
            "GenderOfMesh _W_ name token");
        Check(ModelCatalog.GenderOfMesh("/Game/Characters/PNJ/BigGuy/SK_BigGuy_01", "Grunt") == "M",
            "GenderOfMesh default M");

        Check(ModelCatalog.BPGender("Blueprints/AI/Grunt/Hideout0/Female/BP_AICharacter_X") == "W",
            "BPGender /Female/ -> W");
        Check(ModelCatalog.BPGender("Blueprints/AI/Grunt/Hideout0/Male/BP_AICharacter_X") == "M",
            "BPGender /Male/ -> M");
        Check(ModelCatalog.BPGender("Blueprints/AI/BigGuy/Hideout1/BP_AICharacter_BigGuy_01") == null,
            "BPGender unknown -> null (treated as M downstream)");

        var rr = new List<ModelCatalog.RosterMesh>
        {
            new() { GamePath = "/Game/A/M/SK_A", Name = "SK_A", Gender = "M", Hideout = 1 },
            new() { GamePath = "/Game/A/M/SK_B", Name = "SK_B", Gender = "M", Hideout = 1 },
            new() { GamePath = "/Game/A/W/SK_C", Name = "SK_C", Gender = "W", Hideout = 1 },
            new() { GamePath = "/Game/A/M/SK_D", Name = "SK_D", Gender = "M", Hideout = 2 },
        };
        var ctr = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? Pick(string? g, int? h) => ModelCatalog.AssignRosterMesh(rr, g, h, ctr)?.Name;
        Check(Pick("M", 1) == "SK_A" && Pick("M", 1) == "SK_B" && Pick("M", 1) == "SK_A",
            "same-bucket round-robin wraps");
        Check(Pick("W", 1) == "SK_C", "W bucket rotates on its own counter");
        Check(Pick("M", 3) == "SK_A", "hideout miss falls back to same-gender bucket");
        Check(Pick(null, null) == "SK_B", "null gender treated as M with its own M|* counter");
        Check(Pick("W", 9) == "SK_C", "W hideout miss uses W|* bucket");
        var mOnly = rr.Where(x => x.Gender == "M").ToList();
        Check(ModelCatalog.AssignRosterMesh(mOnly, "W", null,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)) == null,
            "no mesh of that gender -> null (BP kept)");
        Check(ModelCatalog.AssignRosterMesh(new List<ModelCatalog.RosterMesh>(), "M", null, ctr) == null,
            "empty roster -> null");

        var servRoster = ModelCatalog.BuildRoster(content, "Servant");
        Check(servRoster.Count > 0, "Servant roster non-empty", servRoster.Count.ToString());
        Check(servRoster.Any(m => m.Gender == "M") && servRoster.Any(m => m.Gender == "W"),
            "Servant roster has M and W meshes");
        Check(servRoster.Any(m => m.Hideout == 0) && servRoster.Any(m => m.Hideout == 5),
            "Servant roster covers hideouts 0 and 5");
        Check(servRoster.Select(m => m.GamePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            == servRoster.Count, "no duplicate roster entries");
        var mainRoster = ModelCatalog.BuildRoster(content, "MainChar");
        Check(mainRoster.Count > 0, "MainChar roster (Phase 0 meshes reachable)",
            mainRoster.Count.ToString());
        Check(mainRoster.All(m => !m.GamePath.Contains("/Childs/", StringComparison.OrdinalIgnoreCase)),
            "Childs excluded from roster");

        Console.WriteLine("== 8. ApplyModelRosterSwap (Grunt -> Servant) ==");
        var fixtureRoot = Path.GetFullPath(Path.Combine(content,
            "..", "..", "..", "..", "..", "tools", "model_smoke", "Fixtures"));

        string? MakeStage(string name)
        {
            var dir = Path.Combine(Path.GetTempPath(), name);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            Directory.CreateDirectory(dir);
            return dir;
        }

        Check(Directory.Exists(fixtureRoot), "fixtures present", fixtureRoot);
        if (Directory.Exists(fixtureRoot))
        {
            string? FixtureSource(string rel)
        {
            var f = Path.Combine(fixtureRoot,
                rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            return File.Exists(f) ? f : null;
        }

        var rosterStage = MakeStage("SifuModelSmokeRoster");
        var rosterProps = new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase)
        {
            ["Grunt_Base"] = new UnitProperties { ModelSet = "Servant" },
        };
        var rosterLogs = new List<string>();
        var swapWritten = UnitPropertiesManager.ApplyModelRosterSwap(
            content, rosterStage, rosterProps, fixtureRoot, FixtureSource, rosterLogs.Add);
        foreach (var l in rosterLogs) Console.WriteLine("  " + l);
        Check(swapWritten.Count == 26, "26 fixture BPs staged",
            $"{swapWritten.Count} (expected 26)");

        var fixtureBpFiles = Directory.GetFiles(
                Path.Combine(fixtureRoot, "Blueprints", "AI", "Grunt"),
                "BP_AICharacter_*.uasset", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        Check(fixtureBpFiles.Length == 26, "fixture file count", fixtureBpFiles.Length.ToString());

        var oracleCtr = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var mismatches = new List<string>();
        foreach (var bpFile in fixtureBpFiles)
        {
            var rel = bpFile.Substring(fixtureRoot.Length)
                .TrimStart('\\', '/').Replace('\\', '/');
            if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                rel = rel[..^".uasset".Length];
            var expect = ModelCatalog.AssignRosterMesh(servRoster,
                ModelCatalog.BPGender(rel), ModelCatalog.HideoutOfPath(rel), oracleCtr);
            if (expect == null) continue;
            var staged = Path.Combine(rosterStage,
                rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!File.Exists(staged)) { mismatches.Add(rel + " -> staged file missing"); continue; }
            var text = Encoding.ASCII.GetString(File.ReadAllBytes(staged));
            if (!text.Contains(expect.GamePath, StringComparison.Ordinal))
                mismatches.Add(rel + " -> expected " + expect.GamePath);
        }
        Check(mismatches.Count == 0, "every staged BP references its expected roster mesh",
            string.Join("; ", mismatches.Take(3)));

        var rosterStage2 = MakeStage("SifuModelSmokeRoster2");
        var swap2 = UnitPropertiesManager.ApplyModelRosterSwap(
            content, rosterStage2, rosterProps, fixtureRoot, FixtureSource, null);
        Check(swap2.Count == swapWritten.Count, "second run stages the same BPs",
            $"{swap2.Count} vs {swapWritten.Count}");
        int byteDiff = 0;
        foreach (var rel in swapWritten)
        {
            var a = Path.Combine(rosterStage,
                rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            var b = Path.Combine(rosterStage2,
                rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!File.Exists(b) || !File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)))
                byteDiff++;
        }
        Check(byteDiff == 0, "byte-identical output across runs", byteDiff.ToString());

        var noSwapStage = MakeStage("SifuModelSmokeRosterEmpty");
        var noSwap = UnitPropertiesManager.ApplyModelRosterSwap(
            content, noSwapStage,
            new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase),
            fixtureRoot, FixtureSource, null);
        Check(noSwap.Count == 0, "no ModelSet picks -> nothing staged");
        }

        Console.WriteLine("== 9. ValidateCustomMesh ==");
        var vdir = MakeStage("SifuModelSmokeValidate");
        var noPrefix = Path.Combine(vdir, "NotMesh.uasset");
        File.WriteAllText(noPrefix, "x");
        var v1 = ModelCatalog.ValidateCustomMesh(noPrefix);
        Check(v1 != null && v1.Contains("SK_", StringComparison.Ordinal),
            "rejects name without SK_ prefix", v1 ?? "<null>");

        var noUexp = Path.Combine(vdir, "SK_NoUexp.uasset");
        File.WriteAllText(noUexp, "x");
        var v2 = ModelCatalog.ValidateCustomMesh(noUexp);
        Check(v2 != null && v2.Contains(".uexp", StringComparison.Ordinal),
            "rejects missing .uexp sibling", v2 ?? "<null>");

        var garbage = Path.Combine(vdir, "SK_Garbage.uasset");
        File.WriteAllBytes(garbage, new byte[] { 1, 2, 3, 4 });
        File.WriteAllText(Path.Combine(vdir, "SK_Garbage.uexp"), "x");
        var v3 = ModelCatalog.ValidateCustomMesh(garbage);
        Check(v3 != null && v3.Contains("Base_skeleton", StringComparison.Ordinal),
            "rejects non-Base_skeleton bytes", v3 ?? "<null>");

        var realSrc = Path.Combine(content,
            yang.GamePath.Substring("/Game/".Length).Replace('/', Path.DirectorySeparatorChar)
            + ".uasset");
        var realDst = Path.Combine(vdir, "SK_ValidateReal.uasset");
        File.Copy(realSrc, realDst, true);
        File.Copy(Path.ChangeExtension(realSrc, ".uexp"),
            Path.ChangeExtension(realDst, ".uexp"), true);
        Check(ModelCatalog.ValidateCustomMesh(realDst) == null,
            "accepts a real Base_skeleton mesh");

        var badName = Path.Combine(vdir, "SK_Real_PhysicsAsset.uasset");
        File.Copy(realSrc, badName, true);
        File.Copy(Path.ChangeExtension(realSrc, ".uexp"),
            Path.ChangeExtension(badName, ".uexp"), true);
        var v5 = ModelCatalog.ValidateCustomMesh(badName);
        Check(v5 != null && v5.Contains("PhysicsAsset", StringComparison.Ordinal),
            "rejects PhysicsAsset-style name", v5 ?? "<null>");

        Console.WriteLine("== 10. ApplyPlayerModelOverride (BP_FightingPlayer) ==");
        var pawnFixture = Path.Combine(fixtureRoot, "Blueprints", "BP_FightingPlayer.uasset");
        Check(File.Exists(pawnFixture), "pawn fixture present", pawnFixture);
        if (File.Exists(pawnFixture))
        {
            string? PawnSource(string rel2)
            {
                var f = Path.Combine(fixtureRoot,
                    rel2.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
                return File.Exists(f) ? f : null;
            }

            const string pawnRel = "Blueprints/BP_FightingPlayer";
            const string pickPath = "/Game/Characters/PNJ/Grunt/Meshes/SK_Grunt_Servant01";
            const string pickObj = "SK_Grunt_Servant01";
            var pickProps = new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase)
            {
                ["MainChar_Base"] = new UnitProperties { ModelOverride = pickPath },
            };

            var playerStage = MakeStage("SifuModelSmokePlayer");
            var playerLogs = new List<string>();
            var playerWritten = UnitPropertiesManager.ApplyPlayerModelOverride(
                playerStage, pickProps, PawnSource, playerLogs.Add);
            foreach (var l in playerLogs) Console.WriteLine("  " + l);
            Check(playerWritten.Count == 1 && playerWritten[0] == pawnRel,
                "pawn staged once", string.Join(", ", playerWritten));

            var stagedPawn = Path.Combine(playerStage,
                pawnRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            Check(File.Exists(stagedPawn), "staged pawn file exists", stagedPawn);
            Check(File.Exists(Path.ChangeExtension(stagedPawn, ".uexp")),
                "staged pawn uexp copied");
            if (File.Exists(stagedPawn))
            {
                var text = Encoding.ASCII.GetString(File.ReadAllBytes(stagedPawn));
                Check(text.Contains(pickPath, StringComparison.Ordinal),
                    "staged pawn carries new mesh path", pickPath);
                Check(text.Contains(pickPath + "." + pickObj, StringComparison.Ordinal),
                    "staged pawn carries new path.object name");
                Check(text.Contains(pickObj, StringComparison.Ordinal),
                    "staged pawn carries new bare object name");
                Check(!text.Contains("SK_M_MainChar_01", StringComparison.Ordinal),
                    "vanilla M base name gone");
                Check(!text.Contains("SK_W_MainChar_01", StringComparison.Ordinal),
                    "vanilla W base name gone");
            }

            var playerStage2 = MakeStage("SifuModelSmokePlayer2");
            var playerWritten2 = UnitPropertiesManager.ApplyPlayerModelOverride(
                playerStage2, pickProps, PawnSource, null);
            Check(playerWritten2.Count == playerWritten.Count, "second run stages the same pawn",
                $"{playerWritten2.Count} vs {playerWritten.Count}");
            int pawnByteDiff = 0;
            foreach (var rel2 in playerWritten)
            {
                foreach (var ext in new[] { ".uasset", ".uexp" })
                {
                    var a = Path.Combine(playerStage, rel2.Replace('/', Path.DirectorySeparatorChar) + ext);
                    var b = Path.Combine(playerStage2, rel2.Replace('/', Path.DirectorySeparatorChar) + ext);
                    if (!File.Exists(b) || !File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)))
                        pawnByteDiff++;
                }
            }
            Check(pawnByteDiff == 0, "byte-identical pawn output across runs", pawnByteDiff.ToString());

            // An outfit pick: its path already sits in the pawn's name table (the outfit
            // list), so the rename creates a duplicate entry - still allowed.
            var outfitProps = new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase)
            {
                ["MainChar_Base"] = new UnitProperties
                {
                    ModelOverride = "/Game/Characters/MainChar/M/TU02/GrandmasterOutfit/Meshes/SK_M_MainChar_GrandmasterOutfit",
                },
            };
            var outfitStage = MakeStage("SifuModelSmokePlayerOutfit");
            var outfitLogs = new List<string>();
            var outfitWritten = UnitPropertiesManager.ApplyPlayerModelOverride(
                outfitStage, outfitProps, PawnSource, outfitLogs.Add);
            foreach (var l in outfitLogs) Console.WriteLine("  " + l);
            Check(outfitWritten.Count == 1, "outfit pick (path already in table) staged",
                outfitWritten.Count.ToString());
            var outfitStaged = Path.Combine(outfitStage,
                pawnRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (File.Exists(outfitStaged))
            {
                var text = Encoding.ASCII.GetString(File.ReadAllBytes(outfitStaged));
                Check(!text.Contains("SK_M_MainChar_01", StringComparison.Ordinal),
                    "vanilla M base name gone after outfit pick");
            }

            // Pick equals the vanilla base mesh -> nothing to write, staged copy removed.
            var noopProps = new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase)
            {
                ["MainChar_Base"] = new UnitProperties
                {
                    ModelOverride = "/Game/Characters/MainChar/M/Meshes/SK_M_MainChar_01",
                },
            };
            var noopStage = MakeStage("SifuModelSmokePlayerNoop");
            var noopWritten = UnitPropertiesManager.ApplyPlayerModelOverride(
                noopStage, noopProps, PawnSource, null);
            Check(noopWritten.Count == 0, "vanilla pick -> nothing staged", noopWritten.Count.ToString());
            Check(!File.Exists(Path.Combine(noopStage,
                    pawnRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset")),
                "no-op staged copy removed");

            // A file that is not the player pawn (an AI fixture BP) lacks the entries -> skip.
            var aiFixtureDir = Path.Combine(fixtureRoot, "Blueprints", "AI");
            var aiFixture = Directory.Exists(aiFixtureDir)
                ? Directory.GetFiles(aiFixtureDir, "BP_AICharacter_*.uasset", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
            if (aiFixture != null)
            {
                var wrongStage = MakeStage("SifuModelSmokePlayerWrong");
                var wrongLogs = new List<string>();
                var wrongWritten = UnitPropertiesManager.ApplyPlayerModelOverride(
                    wrongStage, pickProps, _ => aiFixture, wrongLogs.Add);
                foreach (var l in wrongLogs) Console.WriteLine("  " + l);
                Check(wrongWritten.Count == 0, "non-pawn source -> not staged",
                    wrongWritten.Count.ToString());
                Check(!File.Exists(Path.Combine(wrongStage,
                        pawnRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset")),
                    "failed patch copy removed");
            }

            var noSrcWritten = UnitPropertiesManager.ApplyPlayerModelOverride(
                MakeStage("SifuModelSmokePlayerNoSrc"), pickProps, _ => null, null);
            Check(noSrcWritten.Count == 0, "missing source -> nothing staged",
                noSrcWritten.Count.ToString());

            var emptyWritten = UnitPropertiesManager.ApplyPlayerModelOverride(
                MakeStage("SifuModelSmokePlayerEmpty"),
                new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase),
                PawnSource, null);
            Check(emptyWritten.Count == 0, "no picks -> nothing staged", emptyWritten.Count.ToString());

            // Two MainChar variants disagree -> first pick wins, conflict logged.
            var conflictProps = new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase)
            {
                ["MainChar_Base"] = new UnitProperties { ModelOverride = pickPath },
                ["MainChar_Barehands"] = new UnitProperties
                {
                    ModelOverride = "/Game/Characters/PNJ/Grunt/Meshes/SK_Grunt_Servant00",
                },
            };
            var conflictLogs = new List<string>();
            var conflictWritten = UnitPropertiesManager.ApplyPlayerModelOverride(
                MakeStage("SifuModelSmokePlayerConflict"), conflictProps, PawnSource, conflictLogs.Add);
            Check(conflictWritten.Count == 1, "conflicting picks -> one pawn staged",
                conflictWritten.Count.ToString());
            Check(conflictLogs.Any(l => l.Contains("Conflicting", StringComparison.Ordinal)),
                "conflict logged", string.Join("; ", conflictLogs));
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ALL PASS" : $"{_fails} FAILURES");
        return _fails == 0 ? 0 : 1;
    }
}
