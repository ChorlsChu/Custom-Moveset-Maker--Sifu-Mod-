using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SifuMovesetEditor;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace phase_swap_smoke;

/// <summary>
/// Smoke tests for the editor's phase-swap port: Grunt template load, append-patch on a
/// real FD Advanced scenario, cross-level patch with target-import creation, cross-family
/// retarget of the vanilla Grunt Last Man transition, retarget self-swap skip, the
/// per-variant pairing probe, and the ApplyPhaseSwaps pass end-to-end (per-variant
/// tier scan, no level-token filter). Exit code 0 = all passed.
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
    private const string FdMuteH2Rel = "DB/AI/Archetypes/FireDisciple/_Arena/Specific/Mute/FireDisciple_Scenario_H2_Mute_Arena";
    private const string FdMiniBossH4Rel = "DB/AI/Archetypes/FireDisciple/Variations/Scenario/FireDisciple_Scenario_MiniBoss_H4";
    private const string FdMiniBossH5Rel = "DB/AI/Archetypes/FireDisciple/Variations/Scenario/FireDisciple_Scenario_MiniBoss_H5";
    private const string GruntH3Rel = "DB/AI/Archetypes/Grunt/_Advanced/Hideout3/Grunt_Scenario_H3";

    private const string MiniBossH5Db = "DB/AI/Archetypes/FireDisciple/Variations/Archetype/BP_FireDisciple_ArchetypeDB_MiniBoss_H5";
    private const string MiniBossH4Db = "DB/AI/Archetypes/FireDisciple/Variations/Archetype/BP_FireDisciple_ArchetypeDB_MiniBoss_H4";
    private const string BigGuyAdvDb = "DB/AI/Archetypes/BigGuy/_MainGame/Generic/BP_BigGuy_ArchetypeDB_Adv_Master";
    private const string GruntLastManH3Db = "DB/AI/Archetypes/Grunt/_Miniboss/Hideout3/BP_Grunt_LastMan_Db_H3";

    private static int Main()
    {
        Directory.CreateDirectory(OutDir);

        if (Environment.GetEnvironmentVariable("SIFU_DUMP_SCENARIO") is string dumpPath)
        {
            DebugDump.DumpScenario(dumpPath);
            return 0;
        }

        if (!File.Exists(Vanilla(FdH5Rel)) || !File.Exists(Vanilla(GruntH3Rel)))
        {
            Console.Error.WriteLine($"test assets missing under {ContentRoot} (set SIFU_CONTENT to the game Content dir)");
            return 2;
        }

        Run("LoadTemplate", TestLoadTemplate);
        Run("PatchFdH5", TestPatchFdH5);
        Run("CreateMissingImport", TestCreateMissingImport);
        Run("RetargetGruntH3", TestRetargetGruntH3);
        Run("RetargetSelfSwap", TestRetargetSelfSwap);
        Run("ProbeScenario", TestProbeScenario);
        Run("TierScope", TestTierScope);
        Run("ApplyPhaseSwapsEndToEnd", TestApplyPhaseSwaps);
        Run("ApplyPhaseSwapsRetarget", TestApplyPhaseSwapsRetarget);
        Run("ApplyPhaseSwapsSelfSwap", TestApplyPhaseSwapsSelfSwap);
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

    /// <summary>Reopen helper; resolves the import index of the target CDO ({leaf}_C).</summary>
    private static (UAsset Asset, int TargetImport) Reopen(string staged, string targetDbRel)
    {
        var post = new UAsset(staged, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
        string needle = PhaseSwapPatcher.DbLeafName(targetDbRel) + "_C";
        int idx = post.Imports.FindIndex(i =>
            i.ClassName.Value?.ToString() == "BlueprintGeneratedClass"
            && i.ObjectName.Value?.ToString() == needle);
        return (post, idx);
    }

    private static void TestPatchFdH5()
    {
        var tpl = PhaseSwapPatcher.LoadTemplate(ContentRoot, out var error);
        Check(tpl != null, "template: " + error);

        var staged = StageCopy(FdH5Rel, "patch-fd-h5");
        var outcome = PhaseSwapPatcher.PatchScenario(staged, tpl!, MiniBossH5Db, out error);
        Check(outcome == PhaseSwapPatcher.Outcome.Patched, $"expected Patched, got {outcome}: {error}");

        var (post, targetImport) = Reopen(staged, MiniBossH5Db);
        Check(PhaseSwapPatcher.HasTakedownTransition(post), "reopened asset has takedown export");
        Check(targetImport >= 0, "target CDO import present");
        Check(post.Imports.Any(i => i.ObjectName.Value?.ToString() == "BP_Grunt_LastManSynchronizedAttacksHitAction_C"),
            "launcher class import cloned into scenario");
    }

    /// <summary>
    /// Cross-level append: the H2 scenario doesn't reference MiniBoss_H5 at all, so
    /// PatchScenario must create the Package + BlueprintGeneratedClass import pair
    /// (vanilla shape) and still pass reopen-verify.
    /// </summary>
    private static void TestCreateMissingImport()
    {
        var tpl = PhaseSwapPatcher.LoadTemplate(ContentRoot, out var error);
        Check(tpl != null, "template: " + error);

        var staged = StageCopy(FdH2Rel, "missing-import");
        var outcome = PhaseSwapPatcher.PatchScenario(staged, tpl!, MiniBossH5Db, out error);
        Check(outcome == PhaseSwapPatcher.Outcome.Patched, $"expected Patched, got {outcome}: {error}");

        var (post, targetImport) = Reopen(staged, MiniBossH5Db);
        Check(PhaseSwapPatcher.HasTakedownTransition(post), "reopened asset has takedown export");
        Check(targetImport >= 0, "created target CDO import present");
        var pkg = post.Imports[targetImport].OuterIndex;
        Check(pkg.Index < 0, "CDO import outer is a package import");
        Check(post.Imports[-pkg.Index - 1].ClassName.Value?.ToString() == "Package"
              && post.Imports[-pkg.Index - 1].ObjectName.Value?.ToString()
                 == "/Game/DB/AI/Archetypes/FireDisciple/Variations/Archetype/BP_FireDisciple_ArchetypeDB_MiniBoss_H5",
            "package import is /Game/DB/.../BP_FireDisciple_ArchetypeDB_MiniBoss_H5");
    }

    /// <summary>
    /// Cross-family retarget: the Grunt H3 scenario already phases (vanilla Last Man to
    /// BP_Grunt_LastMan_Db_H3); targeting the FD MiniBoss_H5 DB repoints the takedown
    /// child node instead of appending a second transition.
    /// </summary>
    private static void TestRetargetGruntH3()
    {
        var tpl = PhaseSwapPatcher.LoadTemplate(ContentRoot, out var error);
        Check(tpl != null, "template: " + error);

        var staged = StageCopy(GruntH3Rel, "retarget-grunt");
        var outcome = PhaseSwapPatcher.PatchScenario(staged, tpl!, MiniBossH5Db, out error);
        Check(outcome == PhaseSwapPatcher.Outcome.Retargeted, $"expected Retargeted, got {outcome}: {error}");

        var (post, targetImport) = Reopen(staged, MiniBossH5Db);
        Check(targetImport >= 0, "created target CDO import present");

        // exactly one takedown transition export (no append), child node points at target
        int takendowns = post.Exports.Count(e =>
            e.ClassIndex.Index < 0
            && post.Imports[-e.ClassIndex.Index - 1].ObjectName.Value?.ToString() == "AIPhaseTransitionTakedown");
        Check(takendowns == 1, $"expected 1 takedown export (retarget, not append), got {takendowns}");

        var nodes = (UAssetAPI.ExportTypes.NormalExport)post.Exports[0];
        var nodeArr = (ArrayPropertyData)nodes.Data!.First(p => p.Name.Value?.ToString() == "m_PhaseNodes");
        bool childTargets = false;
        foreach (var sn in nodeArr.Value!.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>())
        {
            var trans = sn.Value!.FirstOrDefault(p => p.Name.Value?.ToString() == "m_PhaseTransitions") as ArrayPropertyData;
            if (trans?.Value == null) continue;
            foreach (var t in trans.Value.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>())
            {
                var child = t.Value!.FirstOrDefault(p => p.Name.Value?.ToString() == "m_iChildNodeIndex") as IntPropertyData;
                if (child == null) continue;
                if (nodeArr.Value![child.Value] is not UAssetAPI.PropertyTypes.Structs.StructPropertyData cn) continue;
                var cnArch = cn.Value!.FirstOrDefault(p => p.Name.Value?.ToString() == "m_PhaseArchetype") as ObjectPropertyData;
                if (cnArch?.Value?.Index == -targetImport - 1) childTargets = true;
            }
        }
        Check(childTargets, "takedown child node's m_PhaseArchetype points at target CDO import");
    }

    /// <summary>Retargeting a scenario whose Last Man phase already IS the target skips cleanly.</summary>
    private static void TestRetargetSelfSwap()
    {
        var tpl = PhaseSwapPatcher.LoadTemplate(ContentRoot, out var error);
        Check(tpl != null, "template: " + error);

        var staged = StageCopy(GruntH3Rel, "retarget-selfswap");
        var outcome = PhaseSwapPatcher.PatchScenario(staged, tpl!, GruntLastManH3Db, out error);
        Check(outcome == PhaseSwapPatcher.Outcome.SkippedAlreadyPhasing,
            $"expected SkippedAlreadyPhasing, got {outcome}: {error}");
        Check(error.Contains("already targets"), "error names the self-swap: " + error);

        var (post, targetImport) = Reopen(staged, GruntLastManH3Db);
        Check(targetImport >= 0, "vanilla LastMan import present");
    }

    private static void TestProbeScenario()
    {
        // no longer blocked by a missing target import - pairing is per variant
        Check(PhaseSwapPatcher.ProbeScenario(Vanilla(FdH2Rel), MiniBossH5Db)
                == PhaseSwapPatcher.ProbeResult.Ok,
            "FD H2 scenario probes Ok with MiniBoss_H5 (import created at patch time)");
        Check(PhaseSwapPatcher.ProbeScenario(Vanilla(FdH5Rel), BigGuyAdvDb)
                == PhaseSwapPatcher.ProbeResult.Ok,
            "cross-family target probes Ok");
        // The MiniBoss_H4 scenario's root phase IS MiniBoss_H4 - targeting it there is a self-swap.
        Check(PhaseSwapPatcher.ProbeScenario(Vanilla(FdMiniBossH4Rel), MiniBossH4Db)
                == PhaseSwapPatcher.ProbeResult.SelfSwap,
            "FD MiniBoss_H4 scenario root phase is MiniBoss_H4 - self-swap");
        Check(PhaseSwapPatcher.ProbeScenario(Vanilla(FdMiniBossH4Rel), MiniBossH5Db)
                == PhaseSwapPatcher.ProbeResult.Ok,
            "same file probes Ok with a different target");
        Check(PhaseSwapPatcher.ProbeScenario(Path.Combine(OutDir, "no-such-file.uasset"), MiniBossH5Db)
                == PhaseSwapPatcher.ProbeResult.Ok,
            "missing file probes Ok (reported at stage time)");
    }

    /// <summary>Per-variant tier scope: Advanced sees hideout H2/H5 + arena/mute arena only.</summary>
    private static void TestTierScope()
    {
        var adv = ModelCatalog.ScenarioRelsForVariant(ContentRoot, "FD_Advanced");
        foreach (var rel in new[] { FdH2Rel, FdH5Rel, FdArenaAdvRel, FdMuteH2Rel })
            Check(adv.Any(r => r.Equals(rel, StringComparison.OrdinalIgnoreCase)),
                $"FD_Advanced tier includes {rel}; got [{string.Join(", ", adv)}]");
        Check(!adv.Any(r => r.Contains("MiniBoss", StringComparison.OrdinalIgnoreCase)),
            "FD_Advanced tier excludes MiniBoss scenarios");
        Check(!adv.Any(r => r.Contains("_Base", StringComparison.OrdinalIgnoreCase)),
            "FD_Advanced tier excludes Base scenarios");

        var mini = ModelCatalog.ScenarioRelsForVariant(ContentRoot, "FD_Miniboss");
        Check(mini.Any(r => r.Equals(FdMiniBossH5Rel, StringComparison.OrdinalIgnoreCase)),
            "FD_Miniboss tier includes MiniBoss_H5");
        Check(!mini.Any(r => r.Equals(FdH5Rel, StringComparison.OrdinalIgnoreCase)),
            "FD_Miniboss tier excludes Advanced H5");
    }

    /// <summary>
    /// End-to-end: FD_Advanced + MiniBoss_H5 patches every Advanced-tier scenario -
    /// H2, H5, arena and mute arena (no level-token filter; imports created when missing).
    /// </summary>
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
                PhaseSwapTarget = MiniBossH5Db,
            },
        };

        var logs = new List<string>();
        var written = UnitPropertiesManager.ApplyPhaseSwaps(
            ContentRoot, stagedContent, props, null, logs.Add);

        foreach (var l in logs) Console.WriteLine("    " + l);

        var expected = new[] { FdH2Rel, FdH5Rel, FdArenaAdvRel, FdMuteH2Rel };
        Check(written.Count == expected.Length,
            $"expected {expected.Length} staged scenarios [{string.Join(", ", expected)}], got [{string.Join(", ", written)}]");
        foreach (var rel in expected)
            Check(written.Any(w => w.Equals(rel, StringComparison.OrdinalIgnoreCase)),
                "missing staged scenario " + rel);

        foreach (var rel in written)
        {
            var stagedUasset = Path.Combine(stagedContent,
                rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            Check(File.Exists(stagedUasset), "staged file exists: " + rel);
            var (post, targetImport) = Reopen(stagedUasset, MiniBossH5Db);
            Check(PhaseSwapPatcher.HasTakedownTransition(post), "staged file carries the takedown: " + rel);
            Check(targetImport >= 0, "target CDO import present: " + rel);
        }

        // unchecked unit contributes nothing
        var writtenNone = UnitPropertiesManager.ApplyPhaseSwaps(ContentRoot,
            Path.Combine(OutDir, "apply-empty"), new Dictionary<string, UnitProperties>
            {
                ["FD_Advanced"] = new UnitProperties { PhaseSwap = false, PhaseSwapTarget = MiniBossH5Db },
            }, null);
        Check(writtenNone.Count == 0, "unchecked phase swap stages nothing");
    }

    /// <summary>
    /// End-to-end retarget: Grunt_Advanced + FD MiniBoss_H5 target - every Advanced-tier
    /// Grunt scenario (all already phase) is retargeted, cross-family.
    /// </summary>
    private static void TestApplyPhaseSwapsRetarget()
    {
        var stagedContent = Path.Combine(OutDir, "apply-retarget", "Sifu", "Content");
        if (Directory.Exists(Path.Combine(OutDir, "apply-retarget")))
            Directory.Delete(Path.Combine(OutDir, "apply-retarget"), true);

        var props = new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase)
        {
            ["Grunt_Advanced"] = new UnitProperties
            {
                PhaseSwap = true,
                PhaseSwapTarget = MiniBossH5Db,
            },
        };

        var logs = new List<string>();
        var written = UnitPropertiesManager.ApplyPhaseSwaps(
            ContentRoot, stagedContent, props, null, logs.Add);

        foreach (var l in logs) Console.WriteLine("    " + l);

        Check(written.Count >= 5,
            $"expected most Grunt Advanced scenarios staged, got {written.Count}: [{string.Join(", ", written)}]");
        Check(written.Any(w => w.Equals(GruntH3Rel, StringComparison.OrdinalIgnoreCase)),
            "Grunt_Scenario_H3 staged");
        Check(logs.Any(l => l.Contains("retargeted")), "at least one retargeted log line");

        foreach (var rel in written)
        {
            var stagedUasset = Path.Combine(stagedContent,
                rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            var (post, targetImport) = Reopen(stagedUasset, MiniBossH5Db);
            Check(targetImport >= 0, "target CDO import present: " + rel);
            Check(PhaseSwapPatcher.HasTakedownTransition(post), "takedown present: " + rel);
        }
    }

    /// <summary>
    /// Per-variant self-swap: FD_Miniboss + MiniBoss_H4 - the MiniBoss_H4 scenario is
    /// skipped (root phase already the target) while the other Miniboss-tier scenarios patch.
    /// </summary>
    private static void TestApplyPhaseSwapsSelfSwap()
    {
        var stagedContent = Path.Combine(OutDir, "apply-selfswap", "Sifu", "Content");
        if (Directory.Exists(Path.Combine(OutDir, "apply-selfswap")))
            Directory.Delete(Path.Combine(OutDir, "apply-selfswap"), true);

        var props = new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase)
        {
            ["FD_Miniboss"] = new UnitProperties
            {
                PhaseSwap = true,
                PhaseSwapTarget = MiniBossH4Db,
            },
        };

        var logs = new List<string>();
        var written = UnitPropertiesManager.ApplyPhaseSwaps(
            ContentRoot, stagedContent, props, null, logs.Add);

        foreach (var l in logs) Console.WriteLine("    " + l);

        Check(!written.Any(w => w.Equals(FdMiniBossH4Rel, StringComparison.OrdinalIgnoreCase)),
            "MiniBoss_H4 scenario must not be staged (self-swap)");
        Check(logs.Any(l => l.Contains("root phase already BP_FireDisciple_ArchetypeDB_MiniBoss_H4")),
            "log names the self-swap reason");
        Check(written.Any(w => w.Equals(FdMiniBossH5Rel, StringComparison.OrdinalIgnoreCase)),
            "MiniBoss_H5 scenario still patches (its root phase is H5)");
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
                               && o.DisplayName == "H5 (MiniBoss)"
                               && o.Family == "FireDisciple"),
            "MiniBoss_H5 listed with label FireDisciple / H5 (MiniBoss)");
        Check(options.Any(o => o.Rel.EndsWith("BP_FireDisciple_ArchetypeDB_ArenaMiniBoss", StringComparison.OrdinalIgnoreCase)
                               && o.DisplayName == "Arena (MiniBoss)"),
            "ArenaMiniBoss -> Arena (MiniBoss)");
        Check(options.Any(o => o.Rel.EndsWith("BP_Grunt_ArchetypeDB_Base_H2", StringComparison.OrdinalIgnoreCase)
                               && o.DisplayName == "H2 (Base)"),
            "Base_H2 -> H2 (Base)");
        Check(options.Any(o => o.Rel.EndsWith("BP_Grunt_ArchetypeDB_Adv_Master", StringComparison.OrdinalIgnoreCase)
                               && o.DisplayName == "Master (Advanced)"),
            "Adv_Master -> Master (Advanced)");
        Check(options.Any(o => o.Rel.EndsWith("BP_Grunt_Miniboss_DB_H2", StringComparison.OrdinalIgnoreCase)
                               && o.DisplayName == "H2 (MiniBoss)"),
            "Grunt_Miniboss_DB_H2 -> H2 (MiniBoss)");
        Check(options.Any(o => o.Rel.EndsWith("BP_BigGuy_Base_ArchetypeDB_H1", StringComparison.OrdinalIgnoreCase)
                               && o.DisplayName == "H1 (Base)"),
            "BigGuy Base_H1 -> H1 (Base)");
        Check(options.Any(o => o.Rel.EndsWith("BP_Yang_P3_Wude_ArchetypeDB", StringComparison.OrdinalIgnoreCase)
                               && o.DisplayName == "P3 (Wude)"),
            "Yang P3 Wude -> P3 (Wude)");
        Check(options.Any(o => o.Rel.EndsWith("BP_FireDisciple_ArchetypeDB_H3", StringComparison.OrdinalIgnoreCase)
                               && o.DisplayName == "H3 (Advanced)"),
            "bare H-level implies (Advanced)");
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
