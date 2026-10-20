using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SifuMovesetEditor;
using SifuMovesetEditor.Setup;

internal static class Program
{
    private static int _fail;

    private static void Check(bool ok, string what)
    {
        if (!ok) { Console.WriteLine($"  FAIL: {what}"); _fail++; }
    }

    private static void Run(string name, Action test)
    {
        Console.WriteLine(name);
        try { test(); Console.WriteLine(_fail == 0 ? "  PASS" : "  FAIL"); }
        catch (Exception ex) { Console.WriteLine($"  FAIL: exception {ex.Message}"); _fail++; }
    }

    private static int Main()
    {
        // CustomAssets.Root resolves at first type touch - pin it to a fixture dir
        // before anything else touches the library.
        var envRoot = Path.Combine(Path.GetTempPath(), "SifuCov" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(envRoot);
        Environment.SetEnvironmentVariable("SIFU_CUSTOM_ASSETS_ROOT", envRoot);
        try
        {
            Run("DetectValidFixture", TestDetectValid);
            Run("DetectMissingHitBoxData", () => TestDetectFails(
                "HitBoxData", root => File.Delete(Path.Combine(root, "Content", "DB", "AI", "Archetypes", "FireDisciple", "Attacks", "FireDisciple_HitBoxData.uasset"))));
            Run("DetectMissingGruntScenario", () => TestDetectFails(
                "Grunt_Scenario_Base", root => File.Delete(Path.Combine(root, "Content", "DB", "AI", "Archetypes", "Grunt", "_Base", "Grunt_Scenario_Base.uasset"))));
            Run("DetectMissingMovementArchetypes", () => TestDetectFails(
                "DB/Movement/Archetypes", root => Directory.Delete(Path.Combine(root, "Content", "DB", "Movement", "Archetypes"), true)));
            Run("DetectMissingMovementTransition", () => TestDetectFails(
                "DB/Movement/Transition", root => Directory.Delete(Path.Combine(root, "Content", "DB", "Movement", "Transition"), true)));

            Run("RestoreMissingFiles", TestRestoreMissing);
            Run("RestoreIdenticalSkipped", TestRestoreIdentical);
            Run("RestoreReplaceAll", TestRestoreReplaceAll);
            Run("RestoreSkipKeepsExisting", TestRestoreSkip);
            Run("RestoreCancelZeroWrites", TestRestoreCancel);
            Run("RestoreAskCallback", TestRestoreAsk);
            Run("CollectPayloadsRoundTrip", TestCollectRoundTrip);
        }
        finally
        {
            try { Directory.Delete(envRoot, true); } catch { }
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILURE(S)");
        return _fail == 0 ? 0 : 1;
    }

    // ---- ContentDetector fixtures ----

    private static string BuildDetectFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "SifuDet" + Guid.NewGuid().ToString("N").Substring(0, 6));
        void Touch(string rel)
        {
            var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new byte[] { 0 });
        }
        Touch("Engine/Content/Animation/DefaultAnimBoneCompressionSettings.uasset");
        Touch("Content/Animations/MainChar/Attacks/anim.uasset");
        Touch("Content/Characters/Skeleton/Base_skeleton.uasset");
        Touch("Content/DB/_MainChar/Combos/MainChar_ComboTree.uasset");
        Touch("Content/DB/AI/Archetypes/FireDisciple/Attacks/FireDisciple_HitBoxData.uasset");
        Touch("Content/DB/AI/Archetypes/Grunt/_Base/Grunt_Scenario_Base.uasset");
        Touch("Content/DB/AI/Archetypes/Grunt/Grunt_ArchetypeDB.uasset");
        Touch("Content/DB/AI/_Shared/Focus/VitalPointDefinitionDataAsset.uasset");
        Touch("Content/DB/AI/_Shared/BP_Base_ArchetypeDB.uasset");
        Touch("Content/DB/Movement/BaseMovementDB.uasset");
        Touch("Content/DB/Movement/Archetypes/Grunt_BaseMovementDB.uasset");
        Touch("Content/DB/Movement/Transition/BP_TransitionAnimRequest.uasset");
        Touch("Content/DB/Attacks/SomeAttack.uasset");
        return root;
    }

    private static void TestDetectValid()
    {
        var root = BuildDetectFixture();
        try
        {
            var result = ContentDetector.Detect(root);
            Check(result.IsValid, "fixture should be valid: " + result.Reason);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void TestDetectFails(string expectedMarker, Action<string> breakFixture)
    {
        var root = BuildDetectFixture();
        try
        {
            breakFixture(root);
            var result = ContentDetector.Detect(root);
            Check(!result.IsValid, "should be invalid after removing " + expectedMarker);
            Check(result.Reason.Contains(expectedMarker, StringComparison.OrdinalIgnoreCase),
                $"reason should mention {expectedMarker}, got: {result.Reason}");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ---- CustomFileRestore ----

    private static SavedCustomFile Payload(string rel, byte[] bytes) => new()
    {
        Rel = rel,
        Parts = new Dictionary<string, string> { [".uasset"] = Convert.ToBase64String(bytes) }
    };

    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SifuRest" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TestRestoreMissing()
    {
        var root = TempRoot();
        try
        {
            var payload = new List<SavedCustomFile> { Payload("DB/Test/Card", new byte[] { 1, 2, 3, 4 }) };
            var r = CustomFileRestore.Apply(payload, root, CustomRestoreStrategy.Skip);
            Check(!r.Cancelled && r.Restored == 1 && r.Conflicts.Count == 0, "missing file restored once");
            var disk = Path.Combine(root, "DB", "Test", "Card.uasset");
            Check(File.Exists(disk) && File.ReadAllBytes(disk).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "bytes written");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void TestRestoreIdentical()
    {
        var root = TempRoot();
        try
        {
            var file = Path.Combine(root, "DB", "Test", "Card.uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[] { 9, 9 });
            var r = CustomFileRestore.Apply(new List<SavedCustomFile> { Payload("DB/Test/Card", new byte[] { 9, 9 }) },
                root, CustomRestoreStrategy.ReplaceAll);
            Check(r.Restored == 0 && r.Replaced == 0 && r.SkippedIdentical == 1 && r.Conflicts.Count == 0,
                "identical file skipped silently");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void TestRestoreReplaceAll()
    {
        var root = TempRoot();
        try
        {
            var file = Path.Combine(root, "DB", "Test", "Card.uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[] { 7, 7 });
            var r = CustomFileRestore.Apply(new List<SavedCustomFile> { Payload("DB/Test/Card", new byte[] { 8, 8, 8 }) },
                root, CustomRestoreStrategy.ReplaceAll);
            Check(r.Replaced == 1 && r.Restored == 0 && !r.Cancelled, "conflict replaced");
            Check(File.ReadAllBytes(file).SequenceEqual(new byte[] { 8, 8, 8 }), "disk holds embedded bytes");
            Check(r.Conflicts.Count == 1 && r.Conflicts[0].EndsWith(".uasset"), "conflict listed for display");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void TestRestoreSkip()
    {
        var root = TempRoot();
        try
        {
            var file = Path.Combine(root, "DB", "Test", "Card.uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[] { 7, 7 });
            var r = CustomFileRestore.Apply(new List<SavedCustomFile> { Payload("DB/Test/Card", new byte[] { 8 }) },
                root, CustomRestoreStrategy.Skip);
            Check(r.SkippedConflicts == 1 && r.Replaced == 0, "conflict counted as skipped");
            Check(File.ReadAllBytes(file).SequenceEqual(new byte[] { 7, 7 }), "disk untouched on Skip");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void TestRestoreCancel()
    {
        var root = TempRoot();
        try
        {
            var conflictFile = Path.Combine(root, "DB", "Test", "Card.uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(conflictFile)!);
            File.WriteAllBytes(conflictFile, new byte[] { 7 });
            // A second, missing payload: Cancel must not write it either.
            var r = CustomFileRestore.Apply(new List<SavedCustomFile>
                {
                    Payload("DB/Test/Card", new byte[] { 8 }),
                    Payload("DB/Test/Missing", new byte[] { 1 }),
                },
                root, CustomRestoreStrategy.Cancel);
            Check(r.Cancelled, "cancelled flagged");
            Check(!File.Exists(Path.Combine(root, "DB", "Test", "Missing.uasset")), "zero writes on Cancel");
            Check(File.ReadAllBytes(conflictFile).SequenceEqual(new byte[] { 7 }), "conflict file untouched");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void TestRestoreAsk()
    {
        var root = TempRoot();
        try
        {
            var file = Path.Combine(root, "DB", "Test", "Card.uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[] { 7 });
            var asked = new List<string>();
            var r = CustomFileRestore.Apply(new List<SavedCustomFile> { Payload("DB/Test/Card", new byte[] { 8 }) },
                root, CustomRestoreStrategy.Ask,
                conflicts => { asked.AddRange(conflicts); return CustomRestoreStrategy.ReplaceAll; });
            Check(asked.Count == 1, "picker invoked with the conflict list");
            Check(r.Replaced == 1, "picker decision applied");
            Check(File.ReadAllBytes(file).SequenceEqual(new byte[] { 8 }), "replaced bytes");

            // Ask without a picker falls back to Skip (safe default).
            File.WriteAllBytes(file, new byte[] { 5 });
            var r2 = CustomFileRestore.Apply(new List<SavedCustomFile> { Payload("DB/Test/Card", new byte[] { 6 }) },
                root, CustomRestoreStrategy.Ask);
            Check(r2.SkippedConflicts == 1 && File.ReadAllBytes(file).SequenceEqual(new byte[] { 5 }),
                "Ask without picker falls back to Skip");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ---- Collect + save/load round trip ----

    private static void TestCollectRoundTrip()
    {
        // CustomAssets.Root is the env fixture (set in Main).
        var cardRel = "DB/Test/CustomCard";
        var animRel = "Animations/Custom/CustomAnim";
        var cardBytes = new byte[] { 10, 20, 30, 40 };
        var animBytes = new byte[] { 50, 60 };
        void WriteCustom(string rel, byte[] bytes)
        {
            var p = CustomAssets.FileForContentRel(rel + ".uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bytes);
        }
        WriteCustom(cardRel, cardBytes);
        WriteCustom(animRel, animBytes);

        var caches = new Dictionary<string, UnitCacheEntry>
        {
            ["Test|Var"] = new UnitCacheEntry
            {
                Graph = new ComboGraph
                {
                    Nodes = new List<ComboNode>
                    {
                        new()
                        {
                            Id = 1,
                            SourceDBPath = "Game/" + cardRel,
                            AnimPath = "Game/" + animRel,
                        },
                        new()
                        {
                            Id = 2,
                            // Vanilla-style ref (no CustomAssets file) must not embed.
                            SourceDBPath = "Game/DB/_MainChar/Combos/Attacks/MainChar_Jab_FR",
                            AnimPath = "Game/Animations/MainChar/Attacks/SomeVanilla",
                        },
                    }
                }
            }
        };

        var payloads = ProjectManager.CollectCustomFilePayloads(caches);
        Check(payloads != null, "payloads collected");
        Check(payloads!.Any(p => p.Rel.Equals(cardRel, StringComparison.OrdinalIgnoreCase)), "custom card embedded");
        Check(payloads!.Any(p => p.Rel.Equals(animRel, StringComparison.OrdinalIgnoreCase)), "custom anim embedded");
        Check(payloads.Count == 2, $"vanilla refs excluded, got {payloads.Count} payload(s)");

        // Save -> JSON round trip.
        var saveFile = Path.Combine(Path.GetTempPath(), "SifuCovSave" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".sifu-edit");
        try
        {
            ProjectManager.SaveWithCaches(new EditorProject { Name = "Coverage" }, caches, "MainChar", null, null, saveFile);
            var json = File.ReadAllText(saveFile);
            Check(json.Contains("CustomFilePayloads"), "save embeds CustomFilePayloads");
            Check(json.Contains(cardRel.Replace('/', '\\')) || json.Contains(cardRel), "save holds the card rel");

            var (project, _, _, _, _, _) = ProjectManager.Load(saveFile);
            Check(project.CustomFilePayloads != null && project.CustomFilePayloads.Count == 2,
                "load restores payload list");

            // Restore into a fresh root (another machine without CustomAssets).
            var otherRoot = TempRoot();
            try
            {
                var r = CustomFileRestore.Apply(project.CustomFilePayloads, otherRoot, CustomRestoreStrategy.Ask);
                Check(r.Restored == 2 && !r.Cancelled, "both files restored on the new root");
                var cardDisk = Path.Combine(otherRoot, "DB", "Test", "CustomCard.uasset");
                Check(File.ReadAllBytes(cardDisk).SequenceEqual(cardBytes), "card bytes round-tripped");
            }
            finally { try { Directory.Delete(otherRoot, true); } catch { } }
        }
        finally { try { File.Delete(saveFile); } catch { } }
    }
}
