using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SifuMovesetEditor;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace phase_swap_smoke;

/// <summary>
/// Smoke tests for the editor's phase-swap port of tools/lastman_poc: token filter,
/// Grunt template load, patch + reopen-verify on a real FD Advanced scenario, skip on
/// an already-phasing Grunt scenario, failure on a target CDO the scenario doesn't
/// import, and the ApplyPhaseSwaps pass end-to-end (token-filtered staging).
/// Exit code 0 = all passed.
/// </summary>
internal static class Program
{
    private static readonly string ContentRoot =
        Environment.GetEnvironmentVariable("SIFU_CONTENT")
        ?? DefaultContentRoot();

    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "phase_swap_smoke");
    private static readonly List<string> Failures = new();

    private const string FdH5Rel = "DB/AI/Archetypes/FireDisciple/Variations/Scenario/FireDisciple_Scenario_H5";
    private const string FdH2Rel = "DB/AI/Archetypes/FireDisciple/Variations/Scenario/FireDisciple_Scenario_H2";
    private const string FdArenaAdvRel = "DB/AI/Archetypes/FireDisciple/_Arena/Generic/FireDisciple_Scenario_ArenaAdv";
    private const string GruntBaseRel = "DB/AI/Archetypes/Grunt/_Base/Grunt_Scenario_Base";

    private static int Main()
    {
        Directory.CreateDirectory(OutDir);

        if (!File.Exists(Vanilla(FdH5Rel)) || !File.Exists(Vanilla(GruntBaseRel)))
        {
            Console.Error.WriteLine($"test assets missing under {ContentRoot} (set SIFU_CONTENT to the game Content dir)");
            return 2;
        }

        Run("TokenFilter", TestTokenFilter);
        Run("LoadTemplate", TestLoadTemplate);
        Run("PatchFdH5", TestPatchFdH5);
        Run("SkipAlreadyPhasing", TestSkipAlreadyPhasing);
        Run("FailMissingTargetCdo", TestFailMissingTargetCdo);
        Run("ApplyPhaseSwapsEndToEnd", TestApplyPhaseSwaps);
        Run("CanonicalVariantTag", TestCanonicalVariantTag);
        Run("PhaseSwapTargetOptions", TestPhaseSwapTargetOptions);

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine("ALL PHASE SWAP SMOKE TESTS PASSED");
            return 0;
        }
        Console.WriteLine($"{Failures.Count} FAILED:");
        foreach (var f in Failures) Console.WriteLine("  " + f);
        return 1;
    }

    private static string DefaultContentRoot()
    {
        var walk = new DirectoryInfo(AppContext.BaseDirectory);
        while (walk != null)
        {
            var probe = Path.Combine(walk.FullName, "bin", "Debug", "net10.0-windows", "GameContent", "Content");
            if (Directory.Exists(probe)) return probe;
            if (walk.Name == "SifuMovesetEditor") break;
            walk = walk.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "bin", "Debug", "net10.0-windows", "GameContent", "Content");
    }

    private static string Vanilla(string rel)
        => Path.Combine(ContentRoot, rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"FAIL {name}: {ex.Message}");
        }
    }

    private static void Check(bool cond, string what)
    {
        if (!cond) throw new Exception(what);
    }

    private static void TestTokenFilter()
    {
        Check(PhaseSwapPatcher.ScenarioMatchesTarget(FdH5Rel, "BP_FireDisciple_ArchetypeDB_MiniBoss_H5"),
            "H5 scenario should match MiniBoss_H5");
        Check(!PhaseSwapPatcher.ScenarioMatchesTarget(FdH2Rel, "BP_FireDisciple_ArchetypeDB_MiniBoss_H5"),
            "H2 scenario should not match MiniBoss_H5");
        Check(!PhaseSwapPatcher.ScenarioMatchesTarget(FdArenaAdvRel, "BP_FireDisciple_ArchetypeDB_MiniBoss_H5"),
            "ArenaAdv scenario should not match MiniBoss_H5");
        Check(PhaseSwapPatcher.ScenarioMatchesTarget(FdArenaAdvRel, "BP_FireDisciple_ArchetypeDB_ArenaMiniBoss"),
            "ArenaAdv scenario should match ArenaMiniBoss");
        Check(!PhaseSwapPatcher.ScenarioMatchesTarget(FdH5Rel, "BP_FireDisciple_ArchetypeDB_ArenaMiniBoss"),
            "H5 scenario should not match ArenaMiniBoss");
        Check(PhaseSwapPatcher.ScenarioMatchesTarget(FdH5Rel, "BP_FireDisciple_ArchetypeDB_Base"),
            "tokenless DB should match any tier scenario");
        Check(PhaseSwapPatcher.ScenarioMatchesTarget(
                "DB/AI/Archetypes/FireDisciple/_Arena/Specific/Mute/FireDisciple_Scenario_H2_Mute_Arena",
                "BP_FireDisciple_ArchetypeDB_MiniBoss_H2"),
            "Mute arena scenario should match MiniBoss_H2 (shared h2 token)");
    }

    private static void TestLoadTemplate()
    {
        var tpl = PhaseSwapPatcher.LoadTemplate(ContentRoot, out var error);
        Check(tpl != null, "template should load: " + error);
        Check(tpl!.LastManClassImport >= 0, "LastMan class import resolved");
        Check(tpl.Grunt.Imports[tpl.LastManClassImport].ObjectName.Value?.ToString()
                ?.StartsWith("BP_Grunt_LastMan_Db") == true,
            "LastMan class import is BP_Grunt_LastMan_Db_C");
    }

    private static string StageCopy(string rel, string tag)
    {
        var dst = Path.Combine(OutDir, tag, rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        var dir = Path.GetDirectoryName(dst)!;
        Directory.CreateDirectory(dir);
        File.Copy(Vanilla(rel), dst, true);
        var srcUexp = Path.ChangeExtension(Vanilla(rel), ".uexp");
        if (File.Exists(srcUexp))
            File.Copy(srcUexp, Path.ChangeExtension(dst, ".uexp"), true);
        return dst;
    }

    private static void TestPatchFdH5()
    {
        var tpl = PhaseSwapPatcher.LoadTemplate(ContentRoot, out var error);
        Check(tpl != null, "template: " + error);

        var staged = StageCopy(FdH5Rel, "patch-fd-h5");
        var outcome = PhaseSwapPatcher.PatchScenario(staged, tpl!,
            "DB/AI/Archetypes/FireDisciple/Variations/DB/BP_FireDisciple_ArchetypeDB_MiniBoss_H5", out error);
        Check(outcome == PhaseSwapPatcher.Outcome.Patched, $"expected Patched, got {outcome}: {error}");

        // independent reopen checks beyond PatchScenario's own verify
        var post = new UAsset(staged, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
        Check(PhaseSwapPatcher.HasTakedownTransition(post), "reopened asset has takedown export");
        Check(post.Imports.Any(i => i.ObjectName.Value?.ToString() == "BP_Grunt_LastManSynchronizedAttacksHitAction_C"),
            "launcher class import cloned into scenario");
    }

    private static void TestSkipAlreadyPhasing()
    {
        var tpl = PhaseSwapPatcher.LoadTemplate(ContentRoot, out var error);
        Check(tpl != null, "template: " + error);

        var staged = StageCopy(GruntBaseRel, "skip-grunt");
        var outcome = PhaseSwapPatcher.PatchScenario(staged, tpl!,
            "DB/AI/Archetypes/Grunt/_MainGame/Generic/BP_Grunt_ArchetypeDB_Adv_Master", out error);
        Check(outcome == PhaseSwapPatcher.Outcome.SkippedAlreadyPhasing,
            $"expected SkippedAlreadyPhasing, got {outcome}: {error}");
    }

    private static void TestFailMissingTargetCdo()
    {
        var tpl = PhaseSwapPatcher.LoadTemplate(ContentRoot, out var error);
        Check(tpl != null, "template: " + error);

        // FD H5 scenario does not import BigGuy's CDO - patch must fail cleanly
        var staged = StageCopy(FdH5Rel, "fail-missing-cdo");
        var outcome = PhaseSwapPatcher.PatchScenario(staged, tpl!,
            "DB/AI/Archetypes/BigGuy/_MainGame/Generic/BP_BigGuy_ArchetypeDB_Adv_Master", out error);
        Check(outcome == PhaseSwapPatcher.Outcome.Failed, $"expected Failed, got {outcome}");
        Check(error.Contains("target CDO import not found"), "error names the missing CDO: " + error);
    }

    private static void TestApplyPhaseSwaps()
    {
        var stagedContent = Path.Combine(OutDir, "apply", "Sifu", "Content");
        if (Directory.Exists(Path.Combine(OutDir, "apply")))
            Directory.Delete(Path.Combine(OutDir, "apply"), true);

        var props = new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase)
        {
            ["FD_Advanced"] = new UnitProperties
            {
                PhaseSwap = true,
                PhaseSwapTarget = "DB/AI/Archetypes/FireDisciple/Variations/DB/BP_FireDisciple_ArchetypeDB_MiniBoss_H5",
            },
        };

        var logs = new List<string>();
        var written = UnitPropertiesManager.ApplyPhaseSwaps(
            ContentRoot, stagedContent, props, null, logs.Add);

        foreach (var l in logs) Console.WriteLine("    " + l);

        Check(written.Count == 1, $"expected exactly 1 staged scenario (token h5), got {written.Count}: [{string.Join(", ", written)}]");
        Check(written[0].Equals(FdH5Rel, StringComparison.OrdinalIgnoreCase),
            "staged scenario should be FireDisciple_Scenario_H5, got " + written[0]);

        var stagedUasset = Path.Combine(stagedContent,
            written[0].Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        Check(File.Exists(stagedUasset), "staged file exists");
        var post = new UAsset(stagedUasset, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
        Check(PhaseSwapPatcher.HasTakedownTransition(post), "staged file carries the takedown");

        // unchecked unit contributes nothing
        var writtenNone = UnitPropertiesManager.ApplyPhaseSwaps(ContentRoot,
            Path.Combine(OutDir, "apply-empty"), new Dictionary<string, UnitProperties>
            {
                ["FD_Advanced"] = new UnitProperties { PhaseSwap = false, PhaseSwapTarget = props["FD_Advanced"].PhaseSwapTarget },
            }, null);
        Check(writtenNone.Count == 0, "unchecked phase swap stages nothing");
    }

    private static void TestCanonicalVariantTag()
    {
        Check(UnitPropertiesManager.CanonicalVariantTag("FD_Base_Staff") == "FD_Base",
            "FD_Base_Staff -> FD_Base");
        Check(UnitPropertiesManager.CanonicalVariantTag("Grunt_Advanced_Bat") == "Grunt_Advanced",
            "Grunt_Advanced_Bat -> Grunt_Advanced");
        Check(UnitPropertiesManager.CanonicalVariantTag("FD_Advanced") == "FD_Advanced",
            "exact tag passes through");
        Check(UnitPropertiesManager.CanonicalVariantTag("MainChar_Stance1") == null,
            "MainChar stances have no unit DB");
    }

    private static void TestPhaseSwapTargetOptions()
    {
        var options = ModelCatalog.PhaseSwapTargetOptions(ContentRoot);
        Check(options.Count > 0, "options scanned from content tree");

        Check(options.Any(o => o.Rel.EndsWith("BP_FireDisciple_ArchetypeDB_MiniBoss_H5", StringComparison.OrdinalIgnoreCase)
                               && o.DisplayName == "MiniBoss_H5"
                               && o.Family == "FireDisciple"),
            "MiniBoss_H5 listed with label FireDisciple / MiniBoss_H5");
        Check(options.Any(o => o.Rel.EndsWith("BP_FireDisciple_ArchetypeDB_ArenaMiniBoss", StringComparison.OrdinalIgnoreCase)),
            "ArenaMiniBoss listed");
        Check(options.Any(o => o.Rel.EndsWith("BP_Grunt_Miniboss_DB", StringComparison.OrdinalIgnoreCase)),
            "Grunt Last Man-style DB listed");
        Check(options.All(o => !o.Rel.Contains("Scenario", StringComparison.OrdinalIgnoreCase)),
            "no scenario assets in the option list");
        Check(options.All(o => !o.Rel.Contains("/_old/", StringComparison.OrdinalIgnoreCase)
                            && !o.Rel.Contains("/_tests/", StringComparison.OrdinalIgnoreCase)),
            "no _old/_tests assets in the option list");

        // cached second call returns same list
        var again = ModelCatalog.PhaseSwapTargetOptions(ContentRoot);
        Check(ReferenceEquals(options, again), "options are cached per content root");
    }
}
