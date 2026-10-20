using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SifuMovesetEditor;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace voice_fix_smoke;

/// <summary>
/// Smoke tests for the cross-family voice-bleed fix: override detection on the one
/// vanilla anim that carries it (BodyGuard disarm), no-op on clean anims, unit voice
/// switch reads, neutralize + reopen-verify, card->anim expansion, and the
/// ApplyVoiceFixes pass end-to-end (mixed-unit dedupe, staged same-path copy).
/// Exit code 0 = all passed.
/// </summary>
internal static class Program
{
    private static readonly string ContentRoot =
        Environment.GetEnvironmentVariable("SIFU_CONTENT")
        ?? DefaultContentRoot();

    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "voice_fix_smoke");
    private static readonly List<string> Failures = new();

    private const string DisarmAnimRel =
        "Animations/BodyGuard/Attacks/Barehands/Disarm/BodyGuard_attack_barehands_disarm";
    private const string DisarmCardRel =
        "DB/AI/Archetypes/Bodyguard/Attacks/Barehands/Disarm/BodyGuard_barehands_Disarm";
    private const string GruntAnimRel =
        "Animations/Grunt/Attacks/Barehands/540Kick/Grunt_Attack_Barehands_540Kick";

    private static int Main()
    {
        Environment.SetEnvironmentVariable("SIFU_CUSTOM_ASSETS_ROOT",
            Path.Combine(OutDir, "CustomAssetsFixture"));
        Directory.CreateDirectory(OutDir);

        if (!File.Exists(Vanilla(DisarmAnimRel)) || !File.Exists(Vanilla(GruntAnimRel))
            || !File.Exists(Vanilla(DisarmCardRel)))
        {
            Console.Error.WriteLine($"test assets missing under {ContentRoot} (set SIFU_CONTENT to the game Content dir)");
            return 2;
        }

        Run("FindOverrideDisarm", TestFindOverrideDisarm);
        Run("NoOverrideGruntAnim", TestNoOverrideGruntAnim);
        Run("ReadVoiceSwitch", TestReadVoiceSwitch);
        Run("NeutralizeCopy", TestNeutralizeCopy);
        Run("NoOverrideCopy", TestNoOverrideCopy);
        Run("NormalizeRel", TestNormalizeRel);
        Run("AnimRelFromCard", TestAnimRelFromCard);
        Run("ApplyVoiceFixesEndToEnd", TestApplyVoiceFixesEndToEnd);
        Run("ApplyVoiceFixesCardRef", TestApplyVoiceFixesCardRef);

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine("ALL VOICE FIX SMOKE TESTS PASSED");
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

    private static UAsset Load(string rel) =>
        new(Vanilla(rel), EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);

    private static void TestFindOverrideDisarm()
    {
        var ov = VoiceArchetypeFixer.FindVoiceOverride(Load(DisarmAnimRel));
        Check(ov == "Bodyguard", $"override was '{ov}' expected 'Bodyguard'");
    }

    private static void TestNoOverrideGruntAnim()
    {
        var ov = VoiceArchetypeFixer.FindVoiceOverride(Load(GruntAnimRel));
        Check(ov == null, $"override was '{ov}' expected null");
    }

    private static void TestReadVoiceSwitch()
    {
        string? grunt = VoiceArchetypeFixer.ReadVoiceSwitchName(ContentRoot, "Grunt_Base");
        string? bodyguard = VoiceArchetypeFixer.ReadVoiceSwitchName(ContentRoot, "Bodyguard_Base");
        string? fd = VoiceArchetypeFixer.ReadVoiceSwitchName(ContentRoot, "FD_Base");
        string? sean = VoiceArchetypeFixer.ReadVoiceSwitchName(ContentRoot, "Sean_P1");
        Check(grunt == "Grunt", $"Grunt_Base voice was '{grunt}'");
        Check(bodyguard == "BodyGuard", $"Bodyguard_Base voice was '{bodyguard}'");
        Check(fd == "FireDisciple", $"FD_Base voice was '{fd}'");
        Check(sean == "Sean", $"Sean_P1 voice was '{sean}'");
        string? mainChar = VoiceArchetypeFixer.ReadVoiceSwitchName(ContentRoot, "MainChar");
        Check(mainChar == null, $"MainChar voice was '{mainChar}' expected null");
    }

    private static void TestNeutralizeCopy()
    {
        var dst = Path.Combine(OutDir, "neutralized", "disarm.uasset");
        if (File.Exists(dst)) File.Delete(dst);
        var outcome = VoiceArchetypeFixer.NeutralizeCopy(Vanilla(DisarmAnimRel), dst,
            out var ov, out string err);
        Check(outcome == VoiceArchetypeFixer.Outcome.Patched, $"outcome {outcome}: {err}");
        Check(ov == "Bodyguard", $"override value was '{ov}'");
        Check(File.Exists(dst), "staged file missing");

        var post = new UAsset(dst, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
        Check(VoiceArchetypeFixer.FindVoiceOverride(post) == null,
            "reopen still finds a BP_Voice_Preview override");
    }

    private static void TestNoOverrideCopy()
    {
        var dst = Path.Combine(OutDir, "no_override", "grunt.uasset");
        if (File.Exists(dst)) File.Delete(dst);
        var outcome = VoiceArchetypeFixer.NeutralizeCopy(Vanilla(GruntAnimRel), dst,
            out _, out string err);
        Check(outcome == VoiceArchetypeFixer.Outcome.NoOverride, $"outcome {outcome}: {err}");
        Check(!File.Exists(dst), "NoOverride must not create a staged file");
    }

    private static void TestNormalizeRel()
    {
        Check(VoiceArchetypeFixer.NormalizeRel("Game/Animations/X") == "Animations/X", "Game/ prefix");
        Check(VoiceArchetypeFixer.NormalizeRel("/Game/Animations/X.uasset") == "Animations/X", "/Game + .uasset");
        Check(VoiceArchetypeFixer.NormalizeRel(@"DB\AI\Y") == "DB/AI/Y", "backslash");
    }

    private static void TestAnimRelFromCard()
    {
        var animRel = VoiceArchetypeFixer.AnimRelFromCard(Vanilla(DisarmCardRel));
        Check(animRel == DisarmAnimRel, $"card anim was '{animRel}' expected '{DisarmAnimRel}'");
    }

    private static void TestApplyVoiceFixesEndToEnd()
    {
        var stagedDir = Path.Combine(OutDir, "e2e", "Content");
        if (Directory.Exists(Path.Combine(OutDir, "e2e"))) Directory.Delete(Path.Combine(OutDir, "e2e"), true);
        Directory.CreateDirectory(stagedDir);

        var refs = new List<VoiceArchetypeFixer.VoiceRef>
        {
            new("Grunt_Base", DisarmAnimRel, IsCard: false),
            new("Bodyguard_Base", DisarmAnimRel, IsCard: false),   // same anim, second unit
            new("Grunt_Base", GruntAnimRel, IsCard: false),        // no override - no stage
        };
        var written = VoiceArchetypeFixer.ApplyVoiceFixes(ContentRoot, stagedDir, refs, _ => { });

        Check(written.Count == 1 && written[0] == DisarmAnimRel,
            $"written was [{string.Join(",", written)}]");
        var staged = Path.Combine(stagedDir,
            DisarmAnimRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        Check(File.Exists(staged), "neutralized disarm not staged");
        var gruntStaged = Path.Combine(stagedDir,
            GruntAnimRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        Check(!File.Exists(gruntStaged), "clean grunt anim must not be staged");
    }

    private static void TestApplyVoiceFixesCardRef()
    {
        var stagedDir = Path.Combine(OutDir, "e2e_card", "Content");
        if (Directory.Exists(Path.Combine(OutDir, "e2e_card"))) Directory.Delete(Path.Combine(OutDir, "e2e_card"), true);
        Directory.CreateDirectory(stagedDir);

        var refs = new List<VoiceArchetypeFixer.VoiceRef>
        {
            new("Grunt_Base", DisarmCardRel, IsCard: true),
        };
        var written = VoiceArchetypeFixer.ApplyVoiceFixes(ContentRoot, stagedDir, refs, _ => { });
        Check(written.Count == 1 && written[0] == DisarmAnimRel,
            $"card expanded to [{string.Join(",", written)}]");
    }
}
