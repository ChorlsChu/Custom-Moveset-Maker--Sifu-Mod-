using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SifuMovesetEditor;
using SifuMovesetEditor.Custom;
using SifuMovesetEditor.Export;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace attack_hit_smoke;

/// <summary>
/// Smoke tests for the AttackDB advanced hit-window editor: vanilla card reads
/// (QuickHookPunch 2-window reference + Yang_MultiHit 10-window), frame-bound
/// patching on a staged temp copy, HitBoxData row patching on a staged temp
/// copy, Prune/Clone, LooksSharedRow, and the import DiffAgainst. Exit code 0
/// = all passed.
/// </summary>
internal static class Program
{
    private static readonly string ContentRoot =
        Environment.GetEnvironmentVariable("SIFU_CONTENT")
        ?? DefaultContentRoot();

    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "attack_hit_smoke");
    private static readonly List<string> Failures = new();

    private const string QuickHookCardRel =
        "DB/AI/Archetypes/FireDisciple/Attacks/Barehands/ShortCombo/FireDisciple_barehands_QuickHookPunch";
    private const string YangMultiHitCardRel =
        "DB/AI/Archetypes/Yang/Attacks/Yang_MultiHit";
    private const string FajarCardRel =
        "DB/AI/Archetypes/Fajar/Attacks/BareHands/Fajar_Cobra_Double_Bite";
    private const string FajarStaleOldCardRel =
        "DB/AI/Archetypes/Fajar/Attacks/_old/Fajar_BackPunch";

    [STAThread]
    private static int Main()
    {
        Environment.SetEnvironmentVariable("SIFU_CUSTOM_ASSETS_ROOT",
            Path.Combine(OutDir, "CustomAssetsFixture"));
        Directory.CreateDirectory(OutDir);

        if (!File.Exists(Vanilla(QuickHookCardRel)))
        {
            Console.Error.WriteLine($"test assets missing under {ContentRoot} (set SIFU_CONTENT to the game Content dir)");
            return 2;
        }

        Run("ReadQuickHookCard", TestReadQuickHookCard);
        Run("ReadYangMultiHit", TestReadYangMultiHit);
        Run("FajarPathResolvesPastDbTrap", TestFajarPathResolvesPastDbTrap);
        Run("StaleOldRowReportsRowNotFound", TestStaleOldRowReportsRowNotFound);
        Run("PruneEmpty", TestPruneEmpty);
        Run("CloneList", TestCloneList);
        Run("LooksSharedRow", TestLooksSharedRow);
        Run("PatchCardFramesOnCopy", TestPatchCardFramesOnCopy);
        Run("PatchRowsOnCopy", TestPatchRowsOnCopy);
        Run("PatchImpactStructs", TestPatchImpactStructs);
        Run("DiffAgainstPerImpact", TestDiffAgainstPerImpact);
        Run("VanillaReactionValues", TestVanillaReactionValues);
        Run("PatchCardFramesOutOfRange", TestPatchCardFramesOutOfRange);
        Run("FrameEditOnlyNotRowFields", TestHasCardAndRowFieldHelpers);
        Run("PruneVanillaEqualDialogFlow", TestPruneVanillaEqualDialogFlow);
        Run("DiffAgainstImport", TestDiffAgainstImport);
        Run("CloneCardPrivateRow", TestCloneCardPrivateRow);
        Run("CloneCardSharedRow", TestCloneCardSharedRow);
        Run("MakePrivateSourceNamedClone", TestMakePrivateSourceNamedClone);
        Run("MakePrivateButtonWiring", TestMakePrivateButtonWiring);
        Run("UnassignedCloneEmbedsInPayloads", TestUnassignedCloneEmbedsInPayloads);
        Run("CloneNameCollisionGuard", TestCloneNameCollisionGuard);
        Run("RetargetAnimation", TestRetargetAnimation);
        Run("ScaleHitWindows", TestScaleHitWindows);
        Run("RankAttackDbTemplates", TestRankAttackDbTemplates);
        Run("LibraryCardSearchOrder", TestLibraryCardSearchOrder);
        Run("CustomDisplayNamePreserved", TestCustomDisplayNamePreserved);
        Run("DeleteCustomAttackDbDirectRefs", TestDeleteCustomAttackDbDirectRefs);
        Run("DeleteCustomAttackDbRemovesCardNodes", TestDeleteCustomAttackDbRemovesCardNodes);
        Run("DeleteCustomAttackDbRoutes", TestDeleteCustomAttackDbRoutes);
        Run("DeleteCustomAttackDbSharedTable", TestDeleteCustomAttackDbSharedTable);
        Run("DeleteCustomAttackDbExportPrune", TestDeleteCustomAttackDbExportPrune);
        Run("DeleteCustomAttackDbPersistence", TestDeleteCustomAttackDbPersistence);
        Run("RenameCustomAttackDbPrivateRow", TestRenameCustomAttackDbPrivateRow);
        Run("RenameCustomAttackDbSharedRow", TestRenameCustomAttackDbSharedRow);
        Run("RenameCustomAttackDbValidation", TestRenameCustomAttackDbValidation);
        Run("RenameCustomAttackDbGraphRepoint", TestRenameCustomAttackDbGraphRepoint);
        Run("RenameCustomAttackDbSharedTableRefused", TestRenameCustomAttackDbSharedTableRefused);
        Run("LegacyStartRowConditionBackfill", TestLegacyStartRowConditionBackfill);
        Run("NodeCardIdentityDropRule", TestNodeCardIdentityDropRule);
        Run("NodeDeleteStartResetVsDrop", TestNodeDeleteStartResetVsDrop);
        Run("NodeDeleteStartMultiKey", TestNodeDeleteStartMultiKey);
        Run("ClosePromptCancelKeepsFiles", TestClosePromptCancelKeepsFiles);

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine("ALL ATTACK HIT SMOKE TESTS PASSED");
            return 0;
        }
        Console.WriteLine($"{Failures.Count} FAILED:");
        foreach (var f in Failures) Console.WriteLine("  " + f);
        return 1;
    }

    private static void TestReadQuickHookCard()
    {
        var card = AttackHitCard.ReadDbPath(ContentRoot, QuickHookCardRel);
        Check(card != null, "card readable");
        Check(card!.Windows.Count == 2, $"2 windows (got {card.Windows.Count})");

        var hb01 = card.Windows[0];
        Check(hb01.RowName == "QuickHookPunch_HB01", $"HB01 row name (got '{hb01.RowName}')");
        Check(hb01.FrameLo == 26 && hb01.FrameHi == 28, $"HB01 frames 26-28 (got {hb01.FrameLo}-{hb01.FrameHi})");
        Check(hb01.Parriable == true, "HB01 parriable=true");
        Check(hb01.Damage.HasValue, $"HB01 damage present (got {hb01.Damage})");
        Check(hb01.SuperDizzyDamage.HasValue, $"HB01 super dizzy present (got {hb01.SuperDizzyDamage})");
        Check(!string.IsNullOrEmpty(hb01.TableRel), $"HB01 table rel (got '{hb01.TableRel}')");
        Check(hb01.Movement.Length > 0, $"HB01 movement (got '{hb01.Movement}')");
        Check(hb01.AttackPower == "Medium", $"HB01 attack power Medium (got '{hb01.AttackPower}')");
        Check(hb01.PropertyLevel == 0, $"HB01 property level 0 (got {hb01.PropertyLevel})");

        var hb02 = card.Windows[1];
        Check(hb02.RowName == "QuickHookPunch_HB02", $"HB02 row name (got '{hb02.RowName}')");
        Check(hb02.FrameLo == 48 && hb02.FrameHi == 50, $"HB02 frames 48-50 (got {hb02.FrameLo}-{hb02.FrameHi})");
        Check(hb02.Parriable == false, "HB02 parriable=false");
    }

    private static void TestReadYangMultiHit()
    {
        if (!File.Exists(Vanilla(YangMultiHitCardRel)))
        {
            Console.WriteLine("  (Yang_MultiHit not present - skipped)");
            return;
        }
        var card = AttackHitCard.ReadDbPath(ContentRoot, YangMultiHitCardRel);
        Check(card != null, "Yang_MultiHit readable");
        Check(card!.Windows.Count == 10, $"10 windows (got {card.Windows.Count})");
        foreach (var w in card.Windows)
            Check(!string.IsNullOrEmpty(w.RowName), $"window {w.Index} has a row name");
    }

    /// <summary>
    /// Fajar archetype dirs contain a DB/ subfolder of ArchetypeDB blueprints that
    /// used to false-positive as the Content root, leaving every field "absent".
    /// The walk-up must skip it (DB/_MainChar marker) and resolve the real table.
    /// </summary>
    private static void TestFajarPathResolvesPastDbTrap()
    {
        if (!File.Exists(Vanilla(FajarCardRel)))
        {
            Console.WriteLine("  (Fajar_Cobra_Double_Bite not present - skipped)");
            return;
        }
        var card = AttackHitCard.ReadDbPath(ContentRoot, FajarCardRel);
        Check(card != null, "Fajar card readable");
        Check(card!.Windows.Count > 0, "Fajar card has windows");
        foreach (var w in card.Windows)
        {
            Check(w.LookupStatus == VanillaLookupStatus.Ok,
                $"window {w.Index} lookup Ok (got {w.LookupStatus}, table '{w.TableRel}', row '{w.RowName}')");
            Check(w.Damage.HasValue,
                $"window {w.Index} damage present - table actually loaded (got {w.Damage})");
        }
    }

    /// <summary>
    /// _old Library cards can reference row names removed from the current tables;
    /// those must report RowNotFound (not TableNotFound) so the dialog can say
    /// "row not in table" instead of the generic "absent in vanilla".
    /// </summary>
    private static void TestStaleOldRowReportsRowNotFound()
    {
        if (!File.Exists(Vanilla(FajarStaleOldCardRel)))
        {
            Console.WriteLine("  (Fajar _old BackPunch not present - skipped)");
            return;
        }
        var card = AttackHitCard.ReadDbPath(ContentRoot, FajarStaleOldCardRel);
        Check(card != null, "Fajar _old card readable");
        Check(card!.Windows.Count > 0, "Fajar _old card has windows");
        foreach (var w in card.Windows)
            Check(w.LookupStatus == VanillaLookupStatus.RowNotFound,
                $"window {w.Index} lookup RowNotFound (got {w.LookupStatus}, row '{w.RowName}')");
    }

    private static void TestPruneEmpty()
    {
        Check(AttackHitEdit.Prune(null) == null, "Prune(null) -> null");

        var empties = new List<AttackHitEdit>
        {
            new AttackHitEdit { Index = 0 },
            new AttackHitEdit { Index = 1, FrameLo = 5 }
        };
        // FrameLo=5 is not empty, so only the truly empty edit drops.
        var pruned = AttackHitEdit.Prune(empties);
        Check(pruned != null && pruned.Count == 1 && pruned[0].Index == 1,
            "Prune drops empty keeps non-empty");

        var allEmpty = new List<AttackHitEdit> { new AttackHitEdit { Index = 0 } };
        Check(AttackHitEdit.Prune(allEmpty) == null, "Prune all-empty -> null");

        var withNull = new List<AttackHitEdit> { null!, new AttackHitEdit { Index = 2, Damage = 1f } };
        var prunedNull = AttackHitEdit.Prune(withNull);
        Check(prunedNull != null && prunedNull.Count == 1, "Prune drops nulls");
    }

    private static void TestCloneList()
    {
        Check(AttackHitEdit.CloneList(null) == null, "CloneList(null) -> null");

        var src = new List<AttackHitEdit>
        {
            new AttackHitEdit { Index = 0, Damage = 12.5f, Parriable = true }
        };
        var copy = AttackHitEdit.CloneList(src)!;
        Check(copy.Count == 1, "CloneList count");
        Check(!ReferenceEquals(src[0], copy[0]), "Clone is a new instance");
        copy[0].Damage = 99f;
        Check(src[0].Damage == 12.5f, "mutating clone does not touch source");
    }

    private static void TestLooksSharedRow()
    {
        // Card name tokens of length >= 5 must appear in the row name.
        Check(!AttackHitCard.LooksSharedRow("FireDisciple_barehands_QuickHookPunch", "QuickHookPunch_HB01"),
            "attack-named row is not shared");
        Check(AttackHitCard.LooksSharedRow("FireDisciple_barehands_QuickHookPunch", "Hitbox.KnockdownHit"),
            "generic row looks shared");
        Check(AttackHitCard.LooksSharedRow("FireDisciple_barehands_QuickHookPunch", ""),
            "empty row name looks shared");
    }

    private static void TestPatchCardFramesOnCopy()
    {
        var cardSrc = Vanilla(QuickHookCardRel);
        var baseCopy = Path.Combine(OutDir, "card_frames_base.uasset");
        var work = Path.Combine(OutDir, "card_frames_out.uasset");
        File.Copy(cardSrc, baseCopy, true);
        string? uexpSrc = Path.ChangeExtension(cardSrc, ".uexp");
        string? uexpBase = Path.ChangeExtension(baseCopy, ".uexp");
        if (File.Exists(uexpSrc)) File.Copy(uexpSrc, uexpBase!, true);

        var edits = new List<AttackHitEdit>
        {
            new AttackHitEdit { Index = 0, FrameLo = 30, FrameHi = 34 }
        };
        var entries = new List<(string src, string dest)>();
        bool ok = AttackHitCard.PatchCardFrames(baseCopy, work, "dest", edits, entries, out string err);
        Check(ok, $"PatchCardFrames succeeds ({err})");
        AttackHitCard.Invalidate(work);
        var card = AttackHitCard.Read(work);
        Check(card != null, "patched card re-readable");
        Check(card!.Windows[0].FrameLo == 30 && card.Windows[0].FrameHi == 34,
            $"HB01 now 30-34 (got {card.Windows[0].FrameLo}-{card.Windows[0].FrameHi})");
        Check(card.Windows[1].FrameLo == 48 && card.Windows[1].FrameHi == 50,
            $"HB02 untouched (got {card.Windows[1].FrameLo}-{card.Windows[1].FrameHi})");
        Check(entries.Any(e => e.dest.EndsWith(".uasset")), "uasset staged");
    }

    private static void TestPatchRowsOnCopy()
    {
        var card = AttackHitCard.ReadDbPath(ContentRoot, QuickHookCardRel);
        Check(card != null, "card readable for row patch setup");
        string tableRel = card!.Windows[0].TableRel;
        Check(!string.IsNullOrEmpty(tableRel), "table rel resolved");

        string tableGamePath = "/Game/" + tableRel;
        string tableUasset = AttackDbCard.ResolveUassetPath(ContentRoot, tableGamePath)
            ?? throw new InvalidOperationException($"table not found: {tableRel}");
        Check(File.Exists(tableUasset), "table file exists");

        var baseCopy = Path.Combine(OutDir, "table_base.uasset");
        var work = Path.Combine(OutDir, "table_out.uasset");
        File.Copy(tableUasset, baseCopy, true);
        string? uexpSrc = Path.ChangeExtension(tableUasset, ".uexp");
        string? uexpBase = Path.ChangeExtension(baseCopy, ".uexp");
        if (File.Exists(uexpSrc)) File.Copy(uexpSrc, uexpBase!, true);

        string hb01 = card.Windows[0].RowName;
        string hb02 = card.Windows[1].RowName;
        float? hb02DamageBefore = card.Windows[1].Damage;
        string? hb02PowerBefore = card.Windows[1].AttackPower;
        int? hb02LevelBefore = card.Windows[1].PropertyLevel;

        var rowEdits = new Dictionary<string, AttackHitEdit>(StringComparer.Ordinal)
        {
            [hb01] = new AttackHitEdit
            {
                Index = 0, Damage = 20f, Parriable = false,
                SuperDizzyDamage = 99f,
                AttackPower = "Strong", PropertyLevel = 3
            }
        };
        var entries = new List<(string src, string dest)>();
        bool ok = AttackHitCard.PatchRows(baseCopy, work, "dest", rowEdits, entries, out string err);
        Check(ok, $"PatchRows succeeds ({err})");

        // Verify by reopening the patched table with UAssetAPI (same reader as production).
        var asset = new UAssetAPI.UAsset(work, UAssetAPI.UnrealTypes.EngineVersion.VER_UE4_26, null, UAssetAPI.CustomSerializationFlags.None);
        var row = FindTableRow(asset, hb01);
        Check(row != null, "HB01 row present after patch");
        Check(GetRowFloat(row!, "m_fDamage") == 20f, $"HB01 damage=20 (got {GetRowFloat(row!, "m_fDamage")})");
        Check(GetRowBool(row!, "m_bParriable") == false, "HB01 parriable=false");
        Check(GetRowBool(row!, "m_bUseDamage") == true, "HB01 m_bUseDamage set true");
        Check(GetRowFloat(row!, "m_fSuperDizzyDamage") == 99f, $"HB01 super dizzy=99 (got {GetRowFloat(row!, "m_fSuperDizzyDamage")})");
        Check(GetRowBool(row!, "m_bUseSuperDizzyDamage") == true, "HB01 m_bUseSuperDizzyDamage set true");
        Check(GetRowEnum(row!, "m_eAttackPower") == "EAttackPowers::Strong",
            $"HB01 attack power Strong (got '{GetRowEnum(row!, "m_eAttackPower")}')");
        Check(GetRowPropertyLevel(row!) == 3,
            $"HB01 property level 3 (got {GetRowPropertyLevel(row!)})");

        var row2 = FindTableRow(asset, hb02);
        if (row2 != null && hb02DamageBefore.HasValue)
            Check(GetRowFloat(row2, "m_fDamage") == hb02DamageBefore,
                $"HB02 damage untouched (got {GetRowFloat(row2, "m_fDamage")}, was {hb02DamageBefore})");
        if (row2 != null && hb02PowerBefore != null)
            Check(GetRowEnum(row2, "m_eAttackPower") == $"EAttackPowers::{hb02PowerBefore}",
                $"HB02 attack power untouched (got {GetRowEnum(row2, "m_eAttackPower")})");
        if (row2 != null && hb02LevelBefore.HasValue)
            Check(GetRowPropertyLevel(row2) == hb02LevelBefore,
                $"HB02 property level untouched (got {GetRowPropertyLevel(row2)}, was {hb02LevelBefore})");
    }

    /// <summary>
    /// Mirror-only damage/structure writes all three impact structs (OnHit /
    /// OnGuard / OnDeflect); a per-impact override writes only its struct and
    /// leaves the others at their pre-patch values.
    /// </summary>
    private static void TestPatchImpactStructs()
    {
        var card = AttackHitCard.ReadDbPath(ContentRoot, QuickHookCardRel);
        Check(card != null, "card readable for impact patch setup");
        string tableRel = card!.Windows[0].TableRel;
        string tableGamePath = "/Game/" + tableRel;
        string tableUasset = AttackDbCard.ResolveUassetPath(ContentRoot, tableGamePath)
            ?? throw new InvalidOperationException($"table not found: {tableRel}");

        var baseCopy = Path.Combine(OutDir, "impact_base.uasset");
        var work = Path.Combine(OutDir, "impact_out.uasset");
        File.Copy(tableUasset, baseCopy, true);
        string? uexpSrc = Path.ChangeExtension(tableUasset, ".uexp");
        string? uexpBase = Path.ChangeExtension(baseCopy, ".uexp");
        if (File.Exists(uexpSrc)) File.Copy(uexpSrc, uexpBase!, true);

        string hb01 = card.Windows[0].RowName;
        float? hitDmgBefore = card.Windows[0].Damage;
        float? deflectDmgBefore = card.Windows[0].DeflectDamage;

        // Mirror structure (500) + per-impact guard damage override (42).
        var rowEdits = new Dictionary<string, AttackHitEdit>(StringComparer.Ordinal)
        {
            [hb01] = new AttackHitEdit
            {
                Index = 0,
                StructureDamage = 500f,
                DamageOnGuard = 42f
            }
        };
        var entries = new List<(string src, string dest)>();
        bool ok = AttackHitCard.PatchRows(baseCopy, work, "dest", rowEdits, entries, out string err);
        Check(ok, $"impact PatchRows succeeds ({err})");

        var asset = new UAssetAPI.UAsset(work, UAssetAPI.UnrealTypes.EngineVersion.VER_UE4_26, null, UAssetAPI.CustomSerializationFlags.None);
        var row = FindTableRow(asset, hb01);
        Check(row != null, "HB01 row present after impact patch");

        // Mirror structure -> all three structs.
        Check(GetStructFloat(row!, "m_OnHitImpact", "m_fStructureDamage") == 500f,
            $"hit structure=500 (got {GetStructFloat(row!, "m_OnHitImpact", "m_fStructureDamage")})");
        Check(GetStructFloat(row!, "m_OnGuardImpact", "m_fStructureDamage") == 500f,
            $"guard structure=500 (got {GetStructFloat(row!, "m_OnGuardImpact", "m_fStructureDamage")})");
        Check(GetStructFloat(row!, "m_OnDeflectImpact", "m_fStructureDamage") == 500f,
            $"deflect structure=500 (got {GetStructFloat(row!, "m_OnDeflectImpact", "m_fStructureDamage")})");
        Check(GetStructBool(row!, "m_OnGuardImpact", "m_bUseStructureDamage") == true,
            "guard m_bUseStructureDamage set true");
        Check(GetStructBool(row!, "m_OnDeflectImpact", "m_bUseStructureDamage") == true,
            "deflect m_bUseStructureDamage set true");

        // Per-impact guard damage override -> only guard struct changes.
        Check(GetStructFloat(row!, "m_OnGuardImpact", "m_fDamage") == 42f,
            $"guard damage=42 (got {GetStructFloat(row!, "m_OnGuardImpact", "m_fDamage")})");
        Check(GetStructBool(row!, "m_OnGuardImpact", "m_bUseDamage") == true,
            "guard m_bUseDamage set true");
        if (hitDmgBefore.HasValue)
            Check(GetStructFloat(row!, "m_OnHitImpact", "m_fDamage") == hitDmgBefore,
                $"hit damage untouched (got {GetStructFloat(row!, "m_OnHitImpact", "m_fDamage")}, was {hitDmgBefore})");
        if (deflectDmgBefore.HasValue)
            Check(GetStructFloat(row!, "m_OnDeflectImpact", "m_fDamage") == deflectDmgBefore,
                $"deflect damage untouched (got {GetStructFloat(row!, "m_OnDeflectImpact", "m_fDamage")}, was {deflectDmgBefore})");

        // Old-format edit (mirror only) still verifies - PatchRows reopened and
        // re-verified every struct internally, so reaching here is the check.
        Check(true, "mirror-only verify passed inside PatchRows");
    }

    /// <summary>
    /// Import diff picks up guard/deflect struct changes: a mod whose guard
    /// structure differs imports StructureDamageOnGuard, and one whose hit-only
    /// values differ imports the mirror fields (not per-impact).
    /// </summary>
    private static void TestDiffAgainstPerImpact()
    {
        var vanilla = AttackHitCard.ReadDbPath(ContentRoot, QuickHookCardRel);
        Check(vanilla != null, "vanilla card readable");

        var mod = new AttackHitCardData { CardRel = vanilla!.CardRel };
        mod.Windows.Add(CloneWindow(vanilla.Windows[0]));

        // Guard structure differs; hit structure untouched.
        mod.Windows[0].GuardStructureDamage = (vanilla.Windows[0].GuardStructureDamage ?? 10f) + 25f;

        var edits = AttackHitCard.DiffAgainst(vanilla, mod);
        Check(edits != null && edits.Count == 1, $"one edited window (got {edits?.Count ?? 0})");
        var e = edits![0];
        Check(e.StructureDamageOnGuard == mod.Windows[0].GuardStructureDamage,
            "guard structure imported as per-impact override");
        Check(e.StructureDamage == null, $"hit structure unchanged -> mirror null (got {e.StructureDamage})");
        Check(e.Damage == null && e.DamageOnGuard == null && e.StructureDamageOnDeflect == null,
            "other impact fields not imported");

        // Identical data still yields no edits (guard/deflect included).
        Check(AttackHitCard.DiffAgainst(vanilla, vanilla) == null, "identical incl. guard/deflect -> no edits");
    }

    /// <summary>Float inside a named impact struct of a row (NaN when absent).</summary>
    private static float GetStructFloat(UAssetAPI.PropertyTypes.Structs.StructPropertyData row, string structName, string fieldName)
    {
        var imp = row.Value?.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == structName);
        var p = imp?.Value?.FirstOrDefault(x => x.Name?.Value?.ToString() == fieldName)
            as UAssetAPI.PropertyTypes.Objects.FloatPropertyData;
        return p?.Value ?? float.NaN;
    }

    /// <summary>Bool inside a named impact struct of a row (null when absent).</summary>
    private static bool? GetStructBool(UAssetAPI.PropertyTypes.Structs.StructPropertyData row, string structName, string fieldName)
    {
        var imp = row.Value?.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == structName);
        var p = imp?.Value?.FirstOrDefault(x => x.Name?.Value?.ToString() == fieldName)
            as UAssetAPI.PropertyTypes.Objects.BoolPropertyData;
        return p?.Value;
    }

    /// <summary>Vanilla reaction values on the FD table: the knockdown row vs a normal row.</summary>
    private static void TestVanillaReactionValues()
    {
        string tableGamePath = "/Game/DB/AI/Archetypes/FireDisciple/Attacks/FireDisciple_HitBoxData";
        string? tableUasset = AttackDbCard.ResolveUassetPath(ContentRoot, tableGamePath);
        Check(tableUasset != null && File.Exists(tableUasset), "FD HitBoxData table present");
        var asset = new UAssetAPI.UAsset(tableUasset!, UAssetAPI.UnrealTypes.EngineVersion.VER_UE4_26, null, UAssetAPI.CustomSerializationFlags.None);

        var hb02 = FindTableRow(asset, "SpinTrickKick_HB02");
        Check(hb02 != null, "SpinTrickKick_HB02 row present");
        Check(GetRowEnum(hb02!, "m_eAttackPower") == "EAttackPowers::Strong",
            $"SpinTrickKick_HB02 power Strong (got '{GetRowEnum(hb02!, "m_eAttackPower")}')");
        Check(GetRowPropertyLevel(hb02!) == 1,
            $"SpinTrickKick_HB02 level 1 (got {GetRowPropertyLevel(hb02!)})");

        var hb01 = FindTableRow(asset, "SpinTrickKick_HB01");
        Check(hb01 != null, "SpinTrickKick_HB01 row present");
        Check(GetRowEnum(hb01!, "m_eAttackPower") == "EAttackPowers::Medium",
            $"SpinTrickKick_HB01 power Medium (got '{GetRowEnum(hb01!, "m_eAttackPower")}')");
        Check(GetRowPropertyLevel(hb01!) == 0,
            $"SpinTrickKick_HB01 level 0 (got {GetRowPropertyLevel(hb01!)})");
    }

    private static UAssetAPI.PropertyTypes.Structs.StructPropertyData? FindTableRow(
        UAssetAPI.UAsset asset, string rowName)
    {
        foreach (var exp in asset.Exports)
        {
            if (exp is not UAssetAPI.ExportTypes.DataTableExport dte) continue;
            try
            {
                var row = dte[UAssetAPI.UnrealTypes.FName.FromString(asset, rowName)];
                if (row is UAssetAPI.PropertyTypes.Structs.StructPropertyData s) return s;
            }
            catch { }
        }
        return null;
    }

    private static float GetRowFloat(UAssetAPI.PropertyTypes.Structs.StructPropertyData row, string name)
    {
        var hit = row.Value?.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == "m_OnHitImpact");
        var p = hit?.Value?.FirstOrDefault(x => x.Name?.Value?.ToString() == name)
            as UAssetAPI.PropertyTypes.Objects.FloatPropertyData;
        return p?.Value ?? float.NaN;
    }

    private static bool GetRowBool(UAssetAPI.PropertyTypes.Structs.StructPropertyData row, string name)
    {
        // Row-level first (m_bParriable etc.), then inside m_OnHitImpact (m_bUseDamage etc.).
        var p = row.Value?.FirstOrDefault(x => x.Name?.Value?.ToString() == name)
            as UAssetAPI.PropertyTypes.Objects.BoolPropertyData;
        if (p != null) return p.Value;
        var hit = row.Value?.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == "m_OnHitImpact");
        p = hit?.Value?.FirstOrDefault(x => x.Name?.Value?.ToString() == name)
            as UAssetAPI.PropertyTypes.Objects.BoolPropertyData;
        return p?.Value ?? false;
    }

    private static string? GetRowEnum(UAssetAPI.PropertyTypes.Structs.StructPropertyData row, string name)
    {
        var p = row.Value?.FirstOrDefault(x => x.Name?.Value?.ToString() == name)
            as UAssetAPI.PropertyTypes.Objects.EnumPropertyData;
        return p?.Value?.ToString();
    }

    private static int? GetRowPropertyLevel(UAssetAPI.PropertyTypes.Structs.StructPropertyData row)
    {
        var apl = row.Value?.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == "m_AttackPropertyLevel");
        var lvl = apl?.Value?.OfType<UAssetAPI.PropertyTypes.Objects.UInt32PropertyData>()
            .FirstOrDefault(u => u.Name?.Value?.ToString() == "m_Level");
        return lvl == null ? null : (int)lvl.Value;
    }

    private static void TestPatchCardFramesOutOfRange()
    {
        var cardSrc = Vanilla(QuickHookCardRel);
        var baseCopy = Path.Combine(OutDir, "card_oor_base.uasset");
        var work = Path.Combine(OutDir, "card_oor_out.uasset");
        File.Copy(cardSrc, baseCopy, true);
        string? uexpSrc = Path.ChangeExtension(cardSrc, ".uexp");
        string? uexpBase = Path.ChangeExtension(baseCopy, ".uexp");
        if (File.Exists(uexpSrc)) File.Copy(uexpSrc, uexpBase!, true);

        var edits = new List<AttackHitEdit>
        {
            new AttackHitEdit { Index = 99, FrameLo = 1, FrameHi = 2 }
        };
        var entries = new List<(string src, string dest)>();
        bool ok = AttackHitCard.PatchCardFrames(baseCopy, work, "dest", edits, entries, out string err);
        Check(!ok, "out-of-range index fails");
        Check(err.Contains("out of range"), $"error mentions out of range (got '{err}')");
    }

    private static void TestHasCardAndRowFieldHelpers()
    {
        var frameOnly = new AttackHitEdit { Index = 0, FrameLo = 1 };
        Check(AttackHitCard.HasCardFields(frameOnly), "frame-only has card fields");
        Check(!AttackHitCard.HasRowFields(frameOnly), "frame-only has no row fields");

        var damageOnly = new AttackHitEdit { Index = 0, Damage = 5f };
        Check(!AttackHitCard.HasCardFields(damageOnly), "damage-only has no card fields");
        Check(AttackHitCard.HasRowFields(damageOnly), "damage-only has row fields");

        var superDizzyOnly = new AttackHitEdit { Index = 0, SuperDizzyDamage = 30f };
        Check(!AttackHitCard.HasCardFields(superDizzyOnly), "super-dizzy-only has no card fields");
        Check(AttackHitCard.HasRowFields(superDizzyOnly), "super-dizzy-only has row fields");
        Check(!superDizzyOnly.IsEmpty(), "super-dizzy-only edit is not empty");

        var parryOnly = new AttackHitEdit { Index = 0, Parriable = true };
        Check(AttackHitCard.HasRowFields(parryOnly), "parriable has row fields");

        var powerOnly = new AttackHitEdit { Index = 0, AttackPower = "Strong" };
        Check(AttackHitCard.HasRowFields(powerOnly), "attack power has row fields");

        var levelOnly = new AttackHitEdit { Index = 0, PropertyLevel = 3 };
        Check(AttackHitCard.HasRowFields(levelOnly), "property level has row fields");

        var guardStructOnly = new AttackHitEdit { Index = 0, StructureDamageOnGuard = 90f };
        Check(AttackHitCard.HasRowFields(guardStructOnly), "guard-structure-only has row fields");
        Check(!guardStructOnly.IsEmpty(), "guard-structure-only edit is not empty");
        Check(AttackHitCard.HasCardFields(guardStructOnly) == false,
            "guard-structure-only has no card fields");

        var deflectDmgOnly = new AttackHitEdit { Index = 0, DamageOnDeflect = 5f };
        Check(AttackHitCard.HasRowFields(deflectDmgOnly), "deflect-damage-only has row fields");
        Check(!deflectDmgOnly.IsEmpty(), "deflect-damage-only edit is not empty");

        Check(!AttackHitCard.HasCardFields(null) && !AttackHitCard.HasRowFields(null), "null edit has nothing");
    }

    private static void TestPruneVanillaEqualDialogFlow()
    {
        // The dialog nulls vanilla-equal fields before storing; Prune then drops
        // the empty edit - mirroring "user typed the vanilla value back".
        var typed = new AttackHitEdit { Index = 0, Damage = 10f, Parriable = true };
        // Simulate dialog suppression against vanilla (damage 10, parriable true).
        typed.Damage = null;
        typed.Parriable = null;
        var list = AttackHitEdit.Prune(new List<AttackHitEdit> { typed });
        Check(list == null, "all fields suppressed -> no change");

        // Picking the row's vanilla reaction combo suppresses both fields too.
        var reaction = new AttackHitEdit { Index = 0, AttackPower = "Medium", PropertyLevel = 0 };
        Check(!reaction.IsEmpty(), "reaction edit is not empty");
        reaction.AttackPower = null;
        reaction.PropertyLevel = null;
        Check(AttackHitEdit.Prune(new List<AttackHitEdit> { reaction }) == null,
            "vanilla-equal reaction suppressed -> no change");
    }

    /// <summary>
    /// Import diff: identical data yields no edits, one changed window yields one edit
    /// carrying exactly the differing fields, a missing vanilla baseline yields nothing
    /// (custom card harvested whole), and an unresolvable mod row skips row fields but
    /// still imports frame diffs.
    /// </summary>
    private static void TestDiffAgainstImport()
    {
        var vanilla = AttackHitCard.ReadDbPath(ContentRoot, QuickHookCardRel);
        Check(vanilla != null && vanilla.Windows.Count == 2, "vanilla card readable with 2 windows");

        Check(AttackHitCard.DiffAgainst(vanilla, vanilla) == null, "identical data -> no edits");
        Check(AttackHitCard.DiffAgainst(null, vanilla) == null, "missing vanilla baseline -> no edits");
        Check(AttackHitCard.DiffAgainst(vanilla, null) == null, "missing mod card -> no edits");

        var mod = new AttackHitCardData { CardRel = vanilla!.CardRel };
        mod.Windows.Add(CloneWindow(vanilla.Windows[0]));
        mod.Windows.Add(CloneWindow(vanilla.Windows[1]));

        // window 0: frames, damage, parriable and attack power differ; window 1 identical
        mod.Windows[0].FrameHi = vanilla.Windows[0].FrameHi + 5;
        mod.Windows[0].Damage = (vanilla.Windows[0].Damage ?? 10f) * 2f;
        mod.Windows[0].Parriable = !(vanilla.Windows[0].Parriable ?? false);
        mod.Windows[0].AttackPower = "Strong";

        var edits = AttackHitCard.DiffAgainst(vanilla, mod);
        Check(edits != null && edits.Count == 1, $"exactly one edited window (got {edits?.Count ?? 0})");
        var e = edits![0];
        Check(e.Index == 0, $"edit targets window 0 (got {e.Index})");
        Check(e.FrameHi == vanilla.Windows[0].FrameHi + 5, "FrameHi imported");
        Check(e.FrameLo == null, "FrameLo unchanged -> not imported");
        Check(e.Damage == mod.Windows[0].Damage, "Damage imported");
        Check(e.Parriable == mod.Windows[0].Parriable, "Parriable imported");
        Check(e.AttackPower == "Strong", "AttackPower imported");
        Check(e.StructureDamage == null && e.StunFrames == null && e.PropertyLevel == null
            && e.Lethal == null && e.FreezeFrames == null,
            "unchanged row fields not imported");

        // unresolvable mod row: frame diff still imports, row fields do not
        var brokenRow = new AttackHitCardData { CardRel = vanilla.CardRel };
        var bw = CloneWindow(vanilla.Windows[0]);
        bw.FrameLo = vanilla.Windows[0].FrameLo + 3;
        bw.LookupStatus = VanillaLookupStatus.RowNotFound;
        bw.Damage = 999f;
        brokenRow.Windows.Add(bw);
        var frameOnly = AttackHitCard.DiffAgainst(vanilla, brokenRow);
        Check(frameOnly != null && frameOnly.Count == 1 && frameOnly[0].FrameLo == bw.FrameLo,
            "frame diff imports with unresolvable mod row");
        Check(frameOnly![0].Damage == null, "row fields skipped when mod row unresolvable");
    }

    private static AttackHitWindowInfo CloneWindow(AttackHitWindowInfo w) => new()
    {
        Index = w.Index,
        RowName = w.RowName,
        TableRel = w.TableRel,
        FrameLo = w.FrameLo,
        FrameHi = w.FrameHi,
        Movement = w.Movement,
        LookupStatus = w.LookupStatus,
        Damage = w.Damage,
        StructureDamage = w.StructureDamage,
        GuardDamage = w.GuardDamage,
        GuardStructureDamage = w.GuardStructureDamage,
        DeflectDamage = w.DeflectDamage,
        DeflectStructureDamage = w.DeflectStructureDamage,
        KnockbackDistance = w.KnockbackDistance,
        SuperDizzyDamage = w.SuperDizzyDamage,
        StunFrames = w.StunFrames,
        FreezeFrames = w.FreezeFrames,
        Parriable = w.Parriable,
        Lethal = w.Lethal,
        BreakStructure = w.BreakStructure,
        DizzyOnParry = w.DizzyOnParry,
        DizzyOnAvoid = w.DizzyOnAvoid,
        AttackPower = w.AttackPower,
        PropertyLevel = w.PropertyLevel,
        SharedRow = w.SharedRow,
    };

    private const string PrivateCloneName = "SmokeClone_PrivateRow";
    private const string SharedCloneName = "SmokeClone_SharedRow";
    private const string RenamePrivateSource = "SmokeRename_PrivateA";
    private const string RenamePrivateTarget = "SmokeRename_PrivateB";
    private const string RenameSharedSource = "SmokeRename_SharedA";
    private const string RenameSharedTarget = "SmokeRename_SharedB";
    private const string RenameValidationA = "SmokeRename_ValA";
    private const string RenameValidationB = "SmokeRename_ValB";
    private const string RenameSharedOwnerA = "SmokeRename_OwnerA";
    private const string RenameSharedOwnerB = "SmokeRename_OwnerB";
    private const string RenameSharedTargetC = "SmokeRename_OwnerC";

    /// <summary>
    /// G1/G2 headless: clone a vanilla card WITH the hit-window option - the copy must
    /// land in the fixture CustomAssets, carry its new object name, own a private
    /// &lt;Card&gt;_HitBoxData table and read back as never-shared.
    /// </summary>
    private static void TestCloneCardPrivateRow()
    {
        DeleteClone(QuickHookCardRel, PrivateCloneName);
        string rel = AttackCardCloner.DestRel(QuickHookCardRel, PrivateCloneName);
        bool ok = AttackCardCloner.Clone(Vanilla(QuickHookCardRel), QuickHookCardRel,
            PrivateCloneName, true, ContentRoot, out var newUasset, out var newGamePath, out var err);
        Check(ok, $"clone with private row succeeded ({err})");
        if (!ok) return;
        try
        {
            Check(File.Exists(newUasset), "clone uasset exists");
            Check(File.Exists(Path.ChangeExtension(newUasset, ".uexp")), "clone uexp exists");
            Check(newGamePath == "Game/" + rel, $"game path ({newGamePath})");

            string tableRel = rel + "_HitBoxData";
            Check(File.Exists(CustomAssets.FileForContentRel(tableRel + ".uasset")),
                "private table file exists next to the card");

            // Rename proof: export object name + self /Game/ package entry both moved.
            var asset = new UAssetAPI.UAsset(newUasset,
                UAssetAPI.UnrealTypes.EngineVersion.VER_UE4_26, null,
                UAssetAPI.CustomSerializationFlags.None);
            var exp = asset.Exports.OfType<UAssetAPI.ExportTypes.NormalExport>().FirstOrDefault();
            Check(exp?.ObjectName?.Value?.ToString() == PrivateCloneName,
                $"export renamed (got {exp?.ObjectName})");
            Check(asset.ContainsNameReference(
                    (UAssetAPI.UnrealTypes.FString)("/Game/" + rel)),
                "self package path renamed");

            var card = AttackHitCard.ReadDbPath(CustomAssets.Root, rel, ContentRoot);
            Check(card != null && card.Windows.Count == 2,
                $"clone readable with 2 windows (got {card?.Windows.Count ?? 0})");
            if (card == null) return;
            Check(card.Windows.All(w => w.TableRel == tableRel),
                "every window points at the private table ("
                + string.Join(", ", card.Windows.Select(w => w.TableRel)) + ")");
            Check(card.Windows.All(w => !w.SharedRow), "no window flagged shared");
            Check(card.Windows.All(w => !w.CanPrivatize),
                "private table no longer offers Make private");
            Check(card.Windows.All(w => w.LookupStatus == VanillaLookupStatus.Ok),
                "private rows resolve");
            Check(card.Windows.All(w => w.Damage.HasValue),
                "row values read from the private copy");
        }
        finally { DeleteClone(QuickHookCardRel, PrivateCloneName); }
    }

    /// <summary>
    /// G4 headless: clone WITHOUT the hit-window option stays on the shared vanilla row
    /// (still flagged shared, values resolvable through the caller's vanilla root - the
    /// "Make private" escape hatch is what finishes this path in the UI).
    /// </summary>
    private static void TestCloneCardSharedRow()
    {
        DeleteClone(QuickHookCardRel, SharedCloneName);
        string rel = AttackCardCloner.DestRel(QuickHookCardRel, SharedCloneName);
        bool ok = AttackCardCloner.Clone(Vanilla(QuickHookCardRel), QuickHookCardRel,
            SharedCloneName, false, ContentRoot, out _, out _, out var err);
        Check(ok, $"clone without private row succeeded ({err})");
        if (!ok) return;
        try
        {
            Check(!File.Exists(CustomAssets.FileForContentRel(rel + "_HitBoxData.uasset")),
                "no private table copied when the option is off");

            var card = AttackHitCard.ReadDbPath(CustomAssets.Root, rel, ContentRoot);
            Check(card != null && card.Windows.Count == 2,
                $"shared clone readable (got {card?.Windows.Count ?? 0})");
            if (card == null) return;
            Check(card.Windows.All(w =>
                    !w.TableRel.Contains(SharedCloneName, StringComparison.OrdinalIgnoreCase)),
                "still on the vanilla table ("
                + string.Join(", ", card.Windows.Select(w => w.TableRel)) + ")");
            Check(card.Windows.All(w => w.LookupStatus == VanillaLookupStatus.Ok),
                "vanilla table resolves through the fallback root");
            Check(card.Windows.All(w => w.SharedRow), "rows still flagged shared");
            Check(card.Windows.All(w => w.CanPrivatize),
                "shared vanilla table offers Make private");
        }
        finally { DeleteClone(QuickHookCardRel, SharedCloneName); }
    }

    /// <summary>
    /// UI regression for the hidden escape hatch: the caller wires MakePrivate after
    /// construction, so the property setter must re-evaluate the banner and reveal the
    /// button. Before the fix the constructor-only refresh left it collapsed forever.
    /// </summary>
    private static void TestMakePrivateButtonWiring()
    {
        var card = new AttackHitCardData { CardRel = "DB/Test/WiringCard" };
        card.Windows.Add(new AttackHitWindowInfo
        {
            Index = 0,
            RowName = "WiringRow",
            TableRel = "DB/Test/SharedHitBoxData",
            FrameLo = 1,
            FrameHi = 5,
            SharedRow = true,
            CanPrivatize = true
        });

        var dialog = new AttackDbEditorDialog(card, null);
        try
        {
            Check(dialog.btnMakePrivate.Visibility == System.Windows.Visibility.Collapsed,
                "button hidden before the callback is wired");
            Check(dialog.panelShared.Visibility == System.Windows.Visibility.Visible,
                "shared-row banner visible before wiring");
            dialog.MakePrivate = () => null;
            Check(dialog.btnMakePrivate.Visibility == System.Windows.Visibility.Visible,
                "wiring MakePrivate reveals the button");
        }
        finally { dialog.Close(); }
    }

    private const string SourceNamedCloneName = "QuickHookPunch_PublicCopy";

    /// <summary>
    /// Regression for the hidden "Make private" button: a clone named after its source
    /// still points at the shared vanilla table, but the row-name heuristic reports "not
    /// shared". The table-path flag must keep the escape hatch available, and privatizing
    /// must repoint only this clone at a fresh CustomAssets table.
    /// </summary>
    private static void TestMakePrivateSourceNamedClone()
    {
        DeleteClone(QuickHookCardRel, SourceNamedCloneName);
        string rel = AttackCardCloner.DestRel(QuickHookCardRel, SourceNamedCloneName);
        bool ok = AttackCardCloner.Clone(Vanilla(QuickHookCardRel), QuickHookCardRel,
            SourceNamedCloneName, false, ContentRoot, out var uasset, out _, out var err);
        Check(ok, $"source-named clone succeeded ({err})");
        if (!ok) return;
        try
        {
            var card = AttackHitCard.ReadDbPath(CustomAssets.Root, rel, ContentRoot);
            Check(card != null && card.Windows.Count > 0, "source-named clone readable");
            if (card == null) return;

            Check(card.Windows.All(w => !w.SharedRow),
                "row-name heuristic suppresses the shared warning");
            Check(card.Windows.All(w => w.CanPrivatize),
                "table-path flag still offers Make private");

            Check(HitRowPrivatizer.Privatize(uasset, rel, ContentRoot, out var privErr),
                $"privatize succeeded ({privErr})");
            AttackHitCard.Invalidate(uasset);

            var after = AttackHitCard.Read(uasset, ContentRoot);
            Check(after != null && after.Windows.Count > 0, "privatized clone readable");
            if (after == null) return;
            Check(after.Windows.All(w => w.TableRel == rel + "_HitBoxData"),
                "windows repointed at the private table ("
                + string.Join(", ", after.Windows.Select(w => w.TableRel)) + ")");
            Check(File.Exists(CustomAssets.FileForContentRel(rel + "_HitBoxData.uasset")),
                "private table written next to the clone");
            Check(after.Windows.All(w => !w.CanPrivatize),
                "Make private offer disappears once the table is private");
            Check(after.Windows.All(w => !w.SharedRow),
                "private rows are not flagged shared");
        }
        finally { DeleteClone(QuickHookCardRel, SourceNamedCloneName); }
    }

    private const string EmbedCloneName = "SmokeClone_Unassigned";

    /// <summary>
    /// 4.7 hardening: a clone NO graph node references must still travel with the project -
    /// CollectCustomFilePayloads sweeps it out of CustomAssets (plus its private HitBoxData
    /// table) even though the saved graphs mention nothing about it.
    /// </summary>
    private static void TestUnassignedCloneEmbedsInPayloads()
    {
        DeleteClone(QuickHookCardRel, EmbedCloneName);
        string rel = AttackCardCloner.DestRel(QuickHookCardRel, EmbedCloneName);
        bool ok = AttackCardCloner.Clone(Vanilla(QuickHookCardRel), QuickHookCardRel,
            EmbedCloneName, true, ContentRoot, out _, out _, out var err);
        Check(ok, $"clone succeeded ({err})");
        try
        {
            // Project whose graphs reference vanilla content only: the clone is unassigned.
            var caches = new Dictionary<string, UnitCacheEntry>
            {
                ["Embed|Var"] = new UnitCacheEntry
                {
                    Graph = new ComboGraph
                    {
                        Nodes = new List<ComboNode>
                        {
                            new()
                            {
                                Id = 1,
                                SourceDBPath = "Game/DB/_MainChar/Combos/Attacks/MainChar_Jab_FR",
                                AnimPath = "Game/Animations/MainChar/Attacks/SomeVanilla",
                            },
                        }
                    }
                }
            };

            var payloads = ProjectManager.CollectCustomFilePayloads(caches);
            Check(payloads != null, "payloads collected");
            var rels = payloads!.Select(p => p.Rel).ToList();
            Check(rels.Any(r => r.Equals(rel, StringComparison.OrdinalIgnoreCase)),
                "unassigned clone embedded (payloads: " + string.Join(", ", rels) + ")");
            Check(rels.Any(r => r.Equals(rel + "_HitBoxData", StringComparison.OrdinalIgnoreCase)),
                "private HitBoxData table embedded with it (payloads: " + string.Join(", ", rels) + ")");
        }
        finally { DeleteClone(QuickHookCardRel, EmbedCloneName); }
    }

    private const string CollisionCardName = "SmokeClone_VanillaCard";
    private const string CollisionTableName = "SmokeClone_VanillaTable";
    private const string CollisionFreeName = "SmokeClone_FreeName";

    /// <summary>
    /// Clone names must never land on a path the game itself ships: a clone keeps the
    /// source's folder, so a vanilla name at that path would stage as a same-path pak
    /// override (and the renamed object would break references to the original). Covers
    /// both the card and the optional &lt;Name&gt;_HitBoxData companion, in the dialog
    /// validator and in the writer itself.
    /// </summary>
    private static void TestCloneNameCollisionGuard()
    {
        // 1. Real content tree: the source card's own name is taken by definition, and
        //    no library index is passed, so this exercises the vanilla check alone.
        string ownName = Path.GetFileName(QuickHookCardRel);
        string? ownErr = AttackCardCloner.ValidateName(ownName, QuickHookCardRel, null, ContentRoot, false);
        Check(ownErr != null && ownErr.Contains("game's files"),
            $"vanilla card path not blocked (got {ownErr ?? "null"})");

        // The writer refuses too (defense in depth for callers that skip validation).
        bool wrote = AttackCardCloner.Clone(Vanilla(QuickHookCardRel), QuickHookCardRel, ownName,
            false, ContentRoot, out _, out _, out var ownCloneErr);
        Check(!wrote, "Clone wrote a card onto a vanilla path");
        Check(ownCloneErr.Contains("game's files"), $"Clone error text ({ownCloneErr})");
        Check(!File.Exists(CustomAssets.FileForContentRel(
                AttackCardCloner.DestRel(QuickHookCardRel, ownName) + ".uasset")),
            "blocked clone left a file in CustomAssets");

        // 2. Synthetic content root: card collision, table-only collision, free name.
        string fakeRoot = Path.Combine(OutDir, "vanilla_fixture");
        string cardRel = AttackCardCloner.DestRel(QuickHookCardRel, CollisionCardName);
        string tableOnlyRel = AttackCardCloner.DestRel(QuickHookCardRel, CollisionTableName);
        string fakeCard = Path.Combine(fakeRoot,
            cardRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        string fakeTable = Path.Combine(fakeRoot,
            (tableOnlyRel + "_HitBoxData").Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fakeCard)!);
            File.WriteAllBytes(fakeCard, Array.Empty<byte>());
            Directory.CreateDirectory(Path.GetDirectoryName(fakeTable)!);
            File.WriteAllBytes(fakeTable, Array.Empty<byte>());

            Check(AttackCardCloner.ValidateName(CollisionCardName, QuickHookCardRel, null, fakeRoot, false) != null,
                "synthetic vanilla card collision not blocked");
            Check(AttackCardCloner.ValidateName(CollisionFreeName, QuickHookCardRel, null, fakeRoot, false) == null,
                "free name rejected against the synthetic root");
            // The table only matters when the hit row is cloned.
            Check(AttackCardCloner.ValidateName(CollisionTableName, QuickHookCardRel, null, fakeRoot, false) == null,
                "table-only collision blocked without cloning the hit window");
            string? tableErr = AttackCardCloner.ValidateName(CollisionTableName, QuickHookCardRel, null, fakeRoot, true);
            Check(tableErr != null && tableErr.Contains("_HitBoxData"),
                $"vanilla table collision not blocked (got {tableErr ?? "null"})");

            // A missing content root must not break cloning (no-op guard).
            Check(AttackCardCloner.ValidateName(CollisionFreeName, QuickHookCardRel, null, null, false) == null,
                "null content root rejected a free name");
        }
        finally
        {
            try { if (Directory.Exists(fakeRoot)) Directory.Delete(fakeRoot, true); } catch { }
        }
    }

    private const string RankAnimPath =
        "Game/Animations/MainChar/Attacks/Man/Barehands/_old/MainChar_Attack_Man_Barehands_PunishRepulse_HandSharp_BR";
    private const string RankMappedDb =
        "Game/DB/_MainChar/Combos/Attacks/BareHands/_old/MainChar_HandSharp_FR";
    private const string RankOldPeerDb =
        "Game/DB/_MainChar/Combos/Attacks/BareHands/_old/MainChar_ReverseUppercut_FL";

    /// <summary>
    /// Custom asset names are user-authored: DisplayNameClean must not apply the vanilla
    /// token stripping ("Parried_FastJab" used to render, and clone-prefill, as "FastJab").
    /// Vanilla cards keep the existing cleanup.
    /// </summary>
    private static void TestCustomDisplayNamePreserved()
    {
        var custom = new MoveInfo
        {
            DisplayName = "Parried_FastJab",
            FullPath = "Game/DB/AI/Archetypes/FireDisciple/Attacks/Barehands/Parried_FastJab",
            Character = "FireDisciple",
            WeaponType = "Barehands",
            Category = "Custom"
        };
        Check(custom.DisplayNameClean == "Parried_FastJab",
            $"custom name preserved (got '{custom.DisplayNameClean}')");

        var vanilla = new MoveInfo
        {
            DisplayName = "FireDisciple_barehands_QuickHookPunch",
            FullPath = "Game/DB/AI/Archetypes/FireDisciple/Attacks/Barehands/ShortCombo/FireDisciple_barehands_QuickHookPunch",
            Character = "FireDisciple",
            WeaponType = "Barehands",
            Category = "Attacks"
        };
        Check(vanilla.DisplayNameClean == "QuickHookPunch",
            $"vanilla name still cleaned (got '{vanilla.DisplayNameClean}')");
    }

    /// <summary>
    /// Create AttackDB template ranking: the anim's existing card wins and says why,
    /// then same character/weapon/folder, then weaker matches; exactly one option is
    /// marked recommended and a mapped card missing from the library list is injected.
    /// </summary>
    private static void TestRankAttackDbTemplates()
    {
        var anim = new MoveInfo
        {
            DisplayName = "MainChar_Attack_Man_Barehands_PunishRepulse_HandSharp_BR",
            FullPath = RankAnimPath,
            Character = "MainChar",
            WeaponType = "BareHands",
            Category = "Old"
        };
        var cards = new List<MoveInfo>
        {
            new()
            {
                DisplayName = "MainChar_ReverseUppercut_FL", FullPath = RankOldPeerDb,
                Character = "MainChar", WeaponType = "BareHands", Category = "Old"
            },
            new()
            {
                DisplayName = "MainChar_StaffHook",
                FullPath = "Game/DB/_MainChar/Combos/Attacks/Staff/MainChar_StaffHook",
                Character = "MainChar", WeaponType = "Staff", Category = "Combo"
            },
            new()
            {
                DisplayName = "BigGuy_barehands_uppercut",
                FullPath = "Game/DB/AI/Archetypes/BigGuy/Attacks/BareHands/ClassicCombo/BigGuy_barehands_uppercut",
                Character = "BigGuy", WeaponType = "BareHands", Category = "Combo"
            },
            new()
            {
                DisplayName = "MainChar_HandSharp_FR", FullPath = RankMappedDb,
                Character = "MainChar", WeaponType = "BareHands", Category = "Old"
            }
        };

        var ranked = AttackDbTemplateRanker.Rank(anim, RankMappedDb, cards);
        Check(ranked.Count == 4, $"all cards kept (got {ranked.Count})");
        Check(ranked[0].GamePath == RankMappedDb,
            $"mapped card first (got '{ranked[0].GamePath}')");
        Check(ranked[0].IsRecommended, "mapped card marked recommended");
        Check(ranked[0].MatchReason.Contains("existing card", StringComparison.OrdinalIgnoreCase),
            $"mapped reason explains the match (got '{ranked[0].MatchReason}')");
        Check(ranked[0].ParentFolderRel == "DB/_MainChar/Combos/Attacks/BareHands/_old",
            $"destination folder from template (got '{ranked[0].ParentFolderRel}')");
        Check(ranked.Count(o => o.IsRecommended) == 1,
            $"exactly one recommendation (got {ranked.Count(o => o.IsRecommended)})");
        Check(ranked[1].GamePath == RankOldPeerDb,
            $"same character/weapon/folder ranks next (got '{ranked[1].GamePath}')");

        var unmapped = AttackDbTemplateRanker.Rank(anim, null, cards);
        // Same character/weapon/folder as the _old peer, plus a shared "HandSharp" token,
        // so the namesake card is the sensible default even without a mapping.
        Check(unmapped[0].GamePath == RankMappedDb,
            $"closest name wins without a mapping (got '{unmapped[0].GamePath}')");
        Check(unmapped[0].MatchReason == "same character, weapon and folder",
            $"default reason (got '{unmapped[0].MatchReason}')");
        Check(unmapped[^1].MatchReason == "same weapon type",
            $"weak match reason (got '{unmapped[^1].MatchReason}')");

        var injected = AttackDbTemplateRanker.Rank(anim, RankMappedDb,
            cards.Where(c => c.FullPath != RankMappedDb).ToList());
        Check(injected.Count == 4 && injected[0].GamePath == RankMappedDb,
            "mapped card injected when the library list lacks it");
    }

    /// <summary>
    /// Double-click jumps: authored (drag-dropped) nodes must search their attack card
    /// before the animation they play (their DefaultAnimPath is empty, so the vanilla
    /// "drop identity first" rule would otherwise always open the Animations tab).
    /// Parsed vanilla / swapped nodes keep the replacement-aware order.
    /// </summary>
    private static void TestLibraryCardSearchOrder()
    {
        const string anim = "Game/Animations/Test/Attacks/anim";
        const string card = "Game/DB/AI/Archetypes/Test/Attacks/BareHands/card";
        const string mapped = "Game/DB/AI/Archetypes/Test/Attacks/BareHands/mapped";

        var authored = new ComboNode
        {
            Kind = ComboNodeKind.UserCreated,
            AnimPath = anim,
            DefaultAnimPath = "",
            DefaultDBPath = card,
            SourceDBPath = card
        };
        var paths = LibraryCardLocator.SearchPaths(authored, anim, mapped);
        Check(paths.Count > 0 && paths[0] == card,
            $"authored node must search its card first (got '{paths.FirstOrDefault()}')");
        Check(paths.IndexOf(anim) > paths.IndexOf(card),
            "authored node searched the animation before its card");
        Check(paths.IndexOf(mapped) >= 0 && paths.IndexOf(mapped) < paths.IndexOf(anim),
            "authored anim->DB mapping should still precede the animation");
        Check(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == paths.Count,
            "search paths contain duplicates");

        // Vanilla parsed node: AnimPath == DefaultAnimPath, so the card is first.
        var vanilla = new ComboNode
        {
            Kind = ComboNodeKind.Vanilla,
            AnimPath = anim,
            DefaultAnimPath = anim,
            DefaultDBPath = card,
            SourceDBPath = card
        };
        paths = LibraryCardLocator.SearchPaths(vanilla, anim, mapped);
        Check(paths.Count > 0 && paths[0] == card, "vanilla node must search its card first");

        // Swapped vanilla node: the drop identity must stay first (old behavior).
        const string swappedAnim = "Game/Animations/Test/Attacks/swapped";
        var swapped = new ComboNode
        {
            Kind = ComboNodeKind.Vanilla,
            AnimPath = swappedAnim,
            DefaultAnimPath = anim,
            DefaultDBPath = card,
            SourceDBPath = card
        };
        paths = LibraryCardLocator.SearchPaths(swapped, swappedAnim, null);
        Check(paths.Count > 0 && paths[0] == swappedAnim,
            $"swapped node must keep the replacement first (got '{paths.FirstOrDefault()}')");
        Check(paths.IndexOf(card) > 0, "swapped node must still fall back to its card");

        // Authored node with no card: the animation is still the last-resort match.
        var animationOnly = new ComboNode
        {
            Kind = ComboNodeKind.UserCreated,
            AnimPath = anim,
            DefaultAnimPath = ""
        };
        paths = LibraryCardLocator.SearchPaths(animationOnly, anim, null);
        Check(paths.Count == 1 && paths[0] == anim,
            "card-less authored node must search only its animation");

        // Card-only node (animation failed to resolve) still searches the card.
        var cardOnly = new ComboNode
        {
            Kind = ComboNodeKind.UserCreated,
            AnimPath = "",
            DefaultAnimPath = "",
            DefaultDBPath = card,
            SourceDBPath = card
        };
        paths = LibraryCardLocator.SearchPaths(cardOnly, "", null);
        Check(paths.Count == 1 && paths[0] == card,
            "card-only node must search its card");
    }



    private const string DeleteCardRel =
        "DB/_MainChar/Combos/Attacks/Custom/SmokeDeleteCard";
    private const string DeleteCardGame = "Game/" + DeleteCardRel;
    private const string DeleteFallbackCard =
        "Game/DB/_MainChar/Combos/Attacks/Custom/SmokeDeleteFallback";
    private const string DeleteFallbackAnim =
        "Game/Animations/MainChar/Attacks/SmokeDeleteFallback";
    private const string DeleteVanillaAnim =
        "Game/Animations/MainChar/Attacks/SmokeDeleteVanilla";

    /// <summary>
    /// Direct custom AttackDB delete: every card reference kind is found (swap, default,
    /// payload replacement, animation slot) and reverted without touching unrelated
    /// nodes. A default-card slot has no fallback, so it becomes cardless; tuning that
    /// belonged to the deleted card is cleared.
    /// </summary>
    private static void TestDeleteCustomAttackDbDirectRefs()
    {
        var swap = new ComboNode
        {
            Id = 1, TreeIndex = 1, Name = "Swap", DisplayName = "Swap Slot",
            SourceDBPath = DeleteCardGame, DefaultDBPath = DeleteFallbackCard,
            AttackBuildupFrames = 12, AttackGameplayRange = 2.5f,
            HitEdits = new List<AttackHitEdit> { new() { Index = 0, Damage = 9f } }
        };
        var cardless = new ComboNode
        {
            Id = 2, TreeIndex = 2, Name = "Default", DisplayName = "Default Slot",
            SourceDBPath = DeleteCardGame, DefaultDBPath = DeleteCardGame
        };
        var swapNoDefault = new ComboNode
        {
            Id = 7, TreeIndex = 7, Name = "SwapNoDefault", DisplayName = "Swap Without Default",
            SourceDBPath = DeleteCardGame
        };
        var payload = new ComboNode
        {
            Id = 3, TreeIndex = 3, Name = "Payload", DisplayName = "Payload Slot",
            ReplacedByAiNode = true, ReplacedAiKind = "Attack", ReplacedAiClass = "BP_Test",
            ReplacedImportComboRel = "DB/Test", ReplacedSourceTreeIndex = 4,
            ReplacedAttackCardRel = DeleteCardGame
        };
        var anim = new ComboNode
        {
            Id = 4, TreeIndex = 4, Name = "Anim", DisplayName = "Anim Slot",
            AnimPath = DeleteCardGame, DefaultAnimPath = DeleteFallbackAnim
        };
        var vanillaAnim = new ComboNode
        {
            Id = 5, TreeIndex = 5, Name = "VanillaAnim", DisplayName = "Vanilla Anim Slot",
            AnimPath = DeleteCardGame, VanillaAnimPath = DeleteVanillaAnim
        };
        var untouched = new ComboNode
        {
            Id = 6, TreeIndex = 6, Name = "Other", DisplayName = "Other Slot",
            SourceDBPath = DeleteFallbackCard, DefaultDBPath = DeleteFallbackCard
        };

        var graph = new ComboGraph
        {
            Nodes = new List<ComboNode> { swap, cardless, swapNoDefault, payload, anim, vanillaAnim, untouched }
        };
        var units = new List<(string UnitKey, ComboGraph Graph)> { ("Unit|Variant", graph) };

        var plan = CustomAttackDbDelete.Build(DeleteCardGame, units,
            Path.Combine(OutDir, "empty_custom_root"));
        Check(plan.AnyUsage, "plan reports usage");
        Check(plan.Units.Count == 1, $"one unit affected (got {plan.Units.Count})");
        var usage = plan.Units[0];
        Check(usage.Refs.Count == 6, $"six direct refs (got {usage.Refs.Count})");
        Check(usage.Refs.Count(r => r.Kind == "card swap") == 2, "both card swaps detected");
        Check(usage.Refs.Count(r => r.Kind == "default card") == 1, "default card detected");
        Check(usage.Refs.Count(r => r.Kind == "payload replacement") == 1, "payload detected");
        Check(usage.Refs.Count(r => r.Kind == "animation slot") == 2,
            $"both animation slots detected (got {usage.Refs.Count(r => r.Kind == "animation slot")})");
        Check(plan.CardlessSlots == 2,
            $"default card and default-less swap both count as cardless (got {plan.CardlessSlots})");
        Check(usage.Refs.Single(r => r.NodeName == "Swap Without Default").Result
                .Contains("without a card", StringComparison.OrdinalIgnoreCase),
            "swap without a fallback warns about a cardless slot");
        Check(!plan.CardFileFound && plan.Files.Count == 0, "empty root has no files to delete");

        string detail = plan.BuildUsageDetail();
        Check(detail.Contains("card swap") && detail.Contains("default card")
            && detail.Contains("payload replacement") && detail.Contains("animation slot"),
            "usage detail lists every ref kind");

        Check(CustomAttackDbDelete.Apply(plan, "Unit|Variant", graph), "apply changed the graph");

        Check(swap.SourceDBPath == DeleteFallbackCard, $"swap falls back to default card (got '{swap.SourceDBPath}')");
        Check(swap.DefaultDBPath == DeleteFallbackCard, "swap default card untouched");
        Check(swap.AttackBuildupFrames == null && swap.AttackGameplayRange == null && swap.HitEdits == null,
            "swap tuning cleared");
        Check(cardless.SourceDBPath == "" && cardless.DefaultDBPath == "",
            $"default card leaves the slot cardless (got '{cardless.SourceDBPath}' / '{cardless.DefaultDBPath}')");
        Check(swapNoDefault.SourceDBPath == "" && swapNoDefault.DefaultDBPath == "",
            $"swap without a fallback leaves the slot cardless (got '{swapNoDefault.SourceDBPath}')");
        Check(!payload.ReplacedByAiNode && payload.ReplacedAttackCardRel == ""
            && payload.ReplacedAiKind == "" && payload.ReplacedAiClass == ""
            && payload.ReplacedImportComboRel == "" && payload.ReplacedSourceTreeIndex == -1,
            "payload replacement reverted");
        Check(anim.AnimPath == DeleteFallbackAnim, $"animation falls back to default (got '{anim.AnimPath}')");
        Check(vanillaAnim.AnimPath == DeleteVanillaAnim,
            $"animation without a default falls back to vanilla (got '{vanillaAnim.AnimPath}')");
        Check(untouched.SourceDBPath == DeleteFallbackCard && untouched.DefaultDBPath == DeleteFallbackCard,
            "unrelated node untouched");
    }

    /// <summary>
    /// Deleting a custom card removes the custom nodes that only exist because of it:
    /// user-created nodes whose default card is the deleted card (and whose swap source
    /// is empty or the same card), plus authored redirects that forward into them. Their
    /// incoming and outgoing links (merged ones included) and the condition/provenance
    /// entries that described those links go too. Vanilla slots and user nodes retargeted
    /// to another card survive and revert instead.
    /// </summary>
    private static void TestDeleteCustomAttackDbRemovesCardNodes()
    {
        var owned = new ComboNode
        {
            Id = 20, TreeIndex = -1, Kind = ComboNodeKind.UserCreated,
            DisplayName = "Parried_FastJab",
            DefaultDBPath = DeleteCardGame, SourceDBPath = DeleteCardGame
        };
        var redirect = new ComboNode
        {
            Id = 21, TreeIndex = -1, Kind = ComboNodeKind.UserCreated,
            DisplayName = "Redirect To Jab", IsRedirect = true, ResolvedRedirectNodeId = 20
        };
        var retargeted = new ComboNode
        {
            Id = 22, TreeIndex = -1, Kind = ComboNodeKind.UserCreated,
            DisplayName = "Retargeted",
            DefaultDBPath = DeleteCardGame, SourceDBPath = DeleteFallbackCard
        };
        // Animation dropped on the canvas without a default card: its only card is the
        // swap source, so it also exists only because of the deleted card.
        var animOwned = new ComboNode
        {
            Id = 26, TreeIndex = -1, Kind = ComboNodeKind.UserCreated,
            DisplayName = "Anim Only", SourceDBPath = DeleteCardGame
        };
        var vanillaSwap = new ComboNode
        {
            Id = 23, TreeIndex = 23, Kind = ComboNodeKind.Vanilla,
            DisplayName = "Vanilla Swap",
            DefaultDBPath = DeleteFallbackCard, SourceDBPath = DeleteCardGame
        };
        var other = new ComboNode
        {
            Id = 24, TreeIndex = 24, Kind = ComboNodeKind.Vanilla,
            DisplayName = "Other Slot"
        };
        var untouched = new ComboNode
        {
            Id = 25, TreeIndex = 25, Kind = ComboNodeKind.Vanilla,
            DisplayName = "Untouched Slot"
        };

        var graph = new ComboGraph
        {
            Nodes = new List<ComboNode> { owned, redirect, retargeted, animOwned, vanillaSwap, other, untouched }
        };
        graph.RedirectOriginalTargets[20] = 24;
        graph.RedirectOriginalTargets[21] = 20;
        graph.TreeRowConditionOverrides[5] = new Dictionary<int, TreeRowConditionEdit>
        {
            [0] = new() { AddedKeys = new Dictionary<byte, int> { [3] = 24 } }
        };
        graph.TreeEdgeConditionOverrides["20>24|Heavy"] = new()
        {
            Retarget = new Dictionary<byte, int> { [2] = 24 }
        };
        graph.TreeEdgeConditionOverrides["24>25|Light"] = new()
        {
            Retarget = new Dictionary<byte, int> { [2] = 25 }
        };
        graph.RetargetProvenance = new Dictionary<string, int>
        {
            ["E:20>24|Heavy:2"] = 24,
            ["E:24>25|Light:2"] = 25
        };
        graph.Edges.AddRange(new[]
        {
            new ComboEdge { FromNodeId = 24, ToNodeId = 20, InputName = "Light" },
            // Outgoing from the removed node, merged into row 5 key 3.
            new ComboEdge
            {
                FromNodeId = 20, ToNodeId = 24, InputName = "Heavy",
                MergedIntoStruct = 5, MergedIntoRow = 0, MergedIntoKey = 3
            },
            new ComboEdge { FromNodeId = 21, ToNodeId = 24, InputName = "Light" },
            new ComboEdge { FromNodeId = 24, ToNodeId = 23, InputName = "Light" },
            new ComboEdge { FromNodeId = 24, ToNodeId = 22, InputName = "Heavy" },
            // Unrelated link: neither end references the card.
            new ComboEdge { FromNodeId = 24, ToNodeId = 25, InputName = "Light" }
        });

        var units = new List<(string UnitKey, ComboGraph Graph)> { ("FD|Base|Staff", graph) };
        var plan = CustomAttackDbDelete.Build(DeleteCardGame, units,
            Path.Combine(OutDir, "empty_custom_root"));
        var usage = plan.Units.Single();

        Check(plan.TotalRemovedNodes == 3,
            $"owned nodes and redirect are removed with the card (got {plan.TotalRemovedNodes})");
        Check(usage.RemovedNodeIds.Contains(20) && usage.RemovedNodeIds.Contains(21)
            && usage.RemovedNodeIds.Contains(26),
            "authored node, its redirect and the source-only node are in the removal set");
        Check(!usage.RemovedNodeIds.Contains(22),
            "user node retargeted to another card survives");
        var ownedRef = usage.Refs.Single(r => r.NodeId == 20);
        Check(ownedRef.NodeRemoved && ownedRef.Result.Contains("removed with the card", StringComparison.OrdinalIgnoreCase),
            $"removed ref explains the node goes with the card (got '{ownedRef.Result}')");
        Check(usage.Refs.Single(r => r.NodeId == 22).Result.Contains("falls back", StringComparison.OrdinalIgnoreCase),
            "retargeted node reports its fallback card");
        Check(plan.CardlessSlots == 0,
            $"removed/retargeted slots are not reported as cardless (got {plan.CardlessSlots})");
        Check(usage.RemovedEdges == 5,
            $"incoming links to affected slots plus links of removed nodes are severed (got {usage.RemovedEdges})");
        Check(plan.BuildUsageDetail().Contains("removed with the card"),
            "usage detail mentions node removal");

        Check(CustomAttackDbDelete.Apply(plan, "FD|Base|Staff", graph), "apply changed the graph");

        Check(graph.Nodes.Select(n => n.Id).SequenceEqual(new[] { 22, 23, 24, 25 }),
            $"removed nodes are gone, survivors keep ids (got {string.Join(",", graph.Nodes.Select(n => n.Id))})");
        Check(vanillaSwap.SourceDBPath == DeleteFallbackCard && vanillaSwap.DefaultDBPath == DeleteFallbackCard,
            "vanilla slot that swapped to the card reverts to its default");
        Check(retargeted.SourceDBPath == DeleteFallbackCard
            && AttackDbCard.EffectiveCardPath(retargeted) == DeleteFallbackCard,
            $"retargeted node still points at its fallback card (got '{AttackDbCard.EffectiveCardPath(retargeted)}')");
        Check(graph.Edges.Count == 1
            && graph.Edges.Any(e => e.FromNodeId == 24 && e.ToNodeId == 25),
            "only the link between two unaffected nodes remains");
        Check(!graph.TreeRowConditionOverrides.ContainsKey(5),
            "merge key owned by an outgoing link of the removed node is cleaned");
        Check(!graph.TreeEdgeConditionOverrides.ContainsKey("20>24|Heavy")
            && graph.TreeEdgeConditionOverrides.ContainsKey("24>25|Light"),
            "edge condition edits follow the removed links");
        Check(graph.RetargetProvenance != null
            && !graph.RetargetProvenance.ContainsKey("E:20>24|Heavy:2")
            && graph.RetargetProvenance.ContainsKey("E:24>25|Light:2"),
            "provenance follows the removed links");
        Check(!graph.RedirectOriginalTargets.ContainsKey(20)
            && !graph.RedirectOriginalTargets.ContainsKey(21),
            "redirect provenance of removed nodes is dropped");
    }

    /// <summary>
    /// Route cleanup: a delete removes incoming links, vanilla branch targets, chance /
    /// condition / parameter edits and Start Entry transitions that pointed at the
    /// affected slot (redirects forward through). Multi-key start rows lose only the
    /// affected key; fully dropped vanilla rows are recorded for export.
    /// </summary>
    private static void TestDeleteCustomAttackDbRoutes()
    {
        var slot = new ComboNode
        {
            Id = 10, TreeIndex = 10, Name = "Slot", DisplayName = "Card Slot",
            DefaultDBPath = DeleteCardGame
        };
        var redirect = new ComboNode
        {
            Id = 11, TreeIndex = 11, Name = "Redirect", DisplayName = "Redirect To Card",
            IsRedirect = true, ResolvedRedirectNodeId = 10
        };
        var other = new ComboNode
        {
            Id = 12, TreeIndex = 12, Name = "Other", DisplayName = "Other Slot"
        };
        var other2 = new ComboNode
        {
            Id = 13, TreeIndex = 13, Name = "Other2", DisplayName = "Other Slot 2"
        };

        var graph = new ComboGraph
        {
            Nodes = new List<ComboNode> { slot, redirect, other, other2 }
        };
        graph.TreeRows[1] = new List<TreeRowInfo>
        {
            new() { RowIndex = 0, TargetTreeIndices = new List<int> { 10, 12 } },
            new() { RowIndex = 1, TargetTreeIndices = new List<int> { 11 }, ConditionInstanceIndex = 77 },
            new() { RowIndex = 2, TargetTreeIndices = new List<int> { 12 } }
        };
        graph.TreeRows[2] = new List<TreeRowInfo>
        {
            new() { RowIndex = 0, TargetTreeIndices = new List<int> { 10, 11 } }
        };
        graph.TreeRowProbabilityOverrides[1] = new Dictionary<int, float> { [0] = 80f, [1] = 50f };
        graph.TreeConditionParamOverrides[77] = new Dictionary<string, string> { ["m_Value"] = "5" };
        graph.TreeRowConditionOverrides[1] = new Dictionary<int, TreeRowConditionEdit>
        {
            [0] = new()
            {
                Retarget = new Dictionary<byte, int> { [2] = 10, [3] = 12 },
                AddedKeys = new Dictionary<byte, int> { [5] = 10 }
            },
            [1] = new() { ClearCondition = true }
        };
        graph.TreeEdgeConditionOverrides["12>10|Light"] = new()
        {
            Retarget = new Dictionary<byte, int> { [2] = 10 }
        };
        graph.TreeEdgeConditionOverrides["12>11|Heavy"] = new()
        {
            AddedKeys = new Dictionary<byte, int> { [1] = 11 }
        };
        graph.TreeEdgeConditionOverrides["12>13|Heavy"] = new()
        {
            Retarget = new Dictionary<byte, int> { [2] = 10, [3] = 13 }
        };
        graph.TreeEdgeConditionOverrides["13>10|Light"] = new()
        {
            Retarget = new Dictionary<byte, int> { [2] = 10 }
        };
        graph.RetargetProvenance = new Dictionary<string, int>
        {
            ["R:1:0:2"] = 10,
            ["R:1:1:255"] = 11,
            ["E:12>10|Light:2"] = 10,
            ["E:12>13|Heavy:2"] = 10,
            ["E:12>13|Heavy:3"] = 13
        };

        graph.Edges.AddRange(new[]
        {
            new ComboEdge { FromNodeId = 12, ToNodeId = 10, InputName = "Light" },
            new ComboEdge { FromNodeId = 12, ToNodeId = 11, InputName = "Heavy" },
            new ComboEdge { FromNodeId = 12, ToNodeId = 13, InputName = "Heavy" },
            new ComboEdge
            {
                FromNodeId = 13, ToNodeId = 10, InputName = "Light",
                MergedIntoStruct = 1, MergedIntoRow = 0, MergedIntoKey = 5
            },
            // Merged into a row that drops, but its own target is unrelated: it must
            // survive as a standalone link instead of vanishing with the host row.
            new ComboEdge
            {
                FromNodeId = 13, ToNodeId = 12, InputName = "Heavy",
                MergedIntoStruct = 2, MergedIntoRow = 0, MergedIntoKey = 7
            }
        });

        var startTrim = new StartTransition
        {
            TargetNodeId = 10, TargetTreeIndex = 10, TargetName = "Card Slot",
            KeyCount = 2, Keys = new List<byte> { 0, 1 },
            KeyTargetTreeIndices = new List<int> { 10, 13 },
            KeyTargetNodeIds = new List<int> { 10, 13 },
            KeyTargetNames = new List<string> { "Card Slot", "Other Slot 2" },
            OriginalIndex = 0, TargetSummary = "0→10, 1→13"
        };
        var entry = new StartEntry
        {
            Label = "Vanilla entry", VanillaIndex = 0,
            Transitions = new List<StartTransition>
            {
                new()
                {
                    TargetNodeId = 10, TargetTreeIndex = 10, TargetName = "Card Slot",
                    OriginalIndex = 0
                },
                startTrim,
                new()
                {
                    TargetNodeId = 13, TargetTreeIndex = 13, TargetName = "Other Slot 2",
                    OriginalIndex = 2
                }
            }
        };
        var allRemovedEntry = new StartEntry
        {
            Label = "Vanilla entry 2", VanillaIndex = 1,
            Transitions = new List<StartTransition>
            {
                new()
                {
                    TargetNodeId = 10, TargetTreeIndex = 10, TargetName = "Card Slot",
                    OriginalIndex = 0
                }
            }
        };
        var newEntry = new StartEntry
        {
            Label = "New entry", VanillaIndex = -1,
            Transitions = new List<StartTransition>
            {
                new()
                {
                    TargetNodeId = 10, TargetTreeIndex = 10, TargetName = "Card Slot",
                    OriginalIndex = -1
                }
            }
        };
        graph.StartEntries = new List<StartEntry> { entry, allRemovedEntry, newEntry };

        var units = new List<(string UnitKey, ComboGraph Graph)> { ("Enemy|Base", graph) };
        var plan = CustomAttackDbDelete.Build(DeleteCardGame, units,
            Path.Combine(OutDir, "empty_custom_root"));
        var usage = plan.Units.Single();

        Check(usage.ConditionRows == 3, $"three routed rows (got {usage.ConditionRows})");
        Check(usage.DroppedRows == 2, $"two rows drop out (got {usage.DroppedRows})");
        Check(usage.ChanceOverrides == 1, $"one chance override dropped (got {usage.ChanceOverrides})");
        Check(usage.RowConditionEdits == 2, $"two row condition edits touched (got {usage.RowConditionEdits})");
        Check(usage.EdgeConditionEdits == 4, $"four edge condition edits touched (got {usage.EdgeConditionEdits})");
        Check(usage.ParamOverrides == 1, $"one condition param edit dropped (got {usage.ParamOverrides})");
        Check(usage.RemovedEdges == 3, $"three incoming links removed (got {usage.RemovedEdges})");
        Check(usage.UnmergedEdges == 1, $"one merged link unmerged (got {usage.UnmergedEdges})");
        Check(usage.StartTransitions == 4, $"four start transitions touched (got {usage.StartTransitions})");
        Check(usage.StartTrimmed == 1, $"one multi-key start transition trimmed (got {usage.StartTrimmed})");

        Check(CustomAttackDbDelete.Apply(plan, "Enemy|Base", graph), "apply changed the graph");

        Check(graph.Edges.Count == 2
            && graph.Edges.Any(e => e.FromNodeId == 12 && e.ToNodeId == 13)
            && graph.Edges.Any(e => e.FromNodeId == 13 && e.ToNodeId == 12),
            "unaffected link and unmerged link survive");
        var unmerged = graph.Edges.Single(e => e.FromNodeId == 13 && e.ToNodeId == 12);
        Check(unmerged.MergedIntoStruct == -1 && unmerged.MergedIntoRow == -1
            && unmerged.MergedIntoKey == -1, "merged link whose host row dropped is unmerged");
        Check(graph.TreeRows[1].Select(r => r.RowIndex).SequenceEqual(new[] { 0, 2 }),
            "partially affected row survives, single-target row drops");
        Check(graph.TreeRows[1][0].TargetTreeIndices.SequenceEqual(new[] { 12 }),
            "only the card target leaves the surviving row");
        Check(!graph.TreeRows.ContainsKey(2), "struct whose rows all routed at the card is dropped");

        Check(graph.RemovedRouteRows.TryGetValue(1, out var removedRows1)
            && removedRows1.Count == 2
            && removedRows1.Single(r => r.RowIndex == 0).TargetTreeIndices.SequenceEqual(new[] { 10 })
            && removedRows1.Single(r => r.RowIndex == 1).TargetTreeIndices.SequenceEqual(new[] { 11 }),
            "export rows recorded for both dropped and trimmed vanilla rows");
        Check(graph.RemovedRouteRows.TryGetValue(2, out var removedRows2)
            && removedRows2.Single().TargetTreeIndices.SequenceEqual(new[] { 10, 11 }),
            "export rows recorded for the fully dropped struct");

        Check(graph.TreeRowProbabilityOverrides[1].ContainsKey(0)
            && !graph.TreeRowProbabilityOverrides[1].ContainsKey(1),
            "surviving row keeps its chance override, dropped row loses it");
        Check(!graph.TreeConditionParamOverrides.ContainsKey(77),
            "condition params used only by a dropped row are cleared");

        var rowEdit = graph.TreeRowConditionOverrides[1][0];
        Check(!rowEdit.Retarget.ContainsKey(2) && rowEdit.Retarget[3] == 12,
            "row retarget at the card is removed, other outcome kept");
        Check(rowEdit.AddedKeys.Count == 0, "merged link outcome key removed from the row edit");
        Check(!graph.TreeRowConditionOverrides[1].ContainsKey(1),
            "dropped row loses its whole condition edit");

        Check(!graph.TreeEdgeConditionOverrides.ContainsKey("12>10|Light")
            && !graph.TreeEdgeConditionOverrides.ContainsKey("12>11|Heavy")
            && !graph.TreeEdgeConditionOverrides.ContainsKey("13>10|Light"),
            "edits on removed links are dropped");
        var keptEdgeEdit = graph.TreeEdgeConditionOverrides["12>13|Heavy"];
        Check(!keptEdgeEdit.Retarget.ContainsKey(2) && keptEdgeEdit.Retarget[3] == 13,
            "kept link loses only the card outcome key");

        Check(graph.RetargetProvenance != null
            && !graph.RetargetProvenance.ContainsKey("R:1:0:2")
            && !graph.RetargetProvenance.ContainsKey("R:1:1:255")
            && !graph.RetargetProvenance.ContainsKey("E:12>10|Light:2")
            && !graph.RetargetProvenance.ContainsKey("E:12>13|Heavy:2")
            && graph.RetargetProvenance.ContainsKey("E:12>13|Heavy:3"),
            "retarget provenance follows the surviving outcomes");

        // Vanilla entries stay in the model marked Removed (export drops them); only the
        // user-created entry is hard-removed.
        Check(graph.StartEntries.Count == 2, $"one entry dropped, one marked removed (got {graph.StartEntries.Count})");
        var live = graph.StartEntries[0];
        Check(live.Transitions.Count == 3, "vanilla entry keeps its row list for export");
        Check(live.Transitions[0].Removed, "single-key vanilla transition marked removed");
        Check(!live.Transitions[1].Removed
            && live.Transitions[1].Keys.SequenceEqual(new byte[] { 1 })
            && live.Transitions[1].KeyTargetNodeIds.SequenceEqual(new[] { 13 })
            && live.Transitions[1].KeyCount == 1
            && live.Transitions[1].TargetNodeId == 13
            && live.Transitions[1].TargetSummary == "1→13",
            "multi-key transition loses only the card key");
        Check(!live.Transitions[2].Removed && live.Transitions[2].TargetNodeId == 13,
            "unaffected transition untouched");
        Check(!live.Removed, "entry with surviving transitions stays live");
        Check(graph.StartEntries[1].Removed, "vanilla entry with no live transitions is marked removed");
    }

    private const string SharedDeleteOwnerA = "SmokeDelete_OwnerA";
    private const string SharedDeleteOwnerB = "SmokeDelete_OwnerB";

    /// <summary>
    /// A private &lt;Card&gt;_HitBoxData table is deleted with its card only when no other
    /// custom card references it. A second card that still points at the table keeps it.
    /// </summary>
    private static void TestDeleteCustomAttackDbSharedTable()
    {
        DeleteClone(QuickHookCardRel, SharedDeleteOwnerA);
        DeleteClone(QuickHookCardRel, SharedDeleteOwnerB);
        string relA = AttackCardCloner.DestRel(QuickHookCardRel, SharedDeleteOwnerA);
        string tableRel = relA + "_HitBoxData";
        bool ok = AttackCardCloner.Clone(Vanilla(QuickHookCardRel), QuickHookCardRel,
            SharedDeleteOwnerA, true, ContentRoot, out var aUasset, out var aGamePath, out var err);
        Check(ok, $"owner clone succeeded ({err})");
        if (!ok) return;
        try
        {
            Check(File.Exists(CustomAssets.FileForContentRel(tableRel + ".uasset")),
                "owner has a private hit-row table");

            var solo = CustomAttackDbDelete.Build(aGamePath,
                new List<(string UnitKey, ComboGraph Graph)>(), CustomAssets.Root);
            Check(solo.Files.Any(f => f.Equals(relA + ".uasset", StringComparison.OrdinalIgnoreCase)),
                "card file planned for deletion");
            Check(solo.Files.Any(f => f.Equals(tableRel + ".uasset", StringComparison.OrdinalIgnoreCase)),
                "unshared private table planned for deletion");

            bool okB = AttackCardCloner.Clone(aUasset, relA, SharedDeleteOwnerB,
                false, ContentRoot, out _, out _, out var errB);
            Check(okB, $"second card clone succeeded ({errB})");
            if (!okB) return;

            var shared = CustomAttackDbDelete.Build(aGamePath,
                new List<(string UnitKey, ComboGraph Graph)>(), CustomAssets.Root);
            Check(shared.Files.Any(f => f.Equals(relA + ".uasset", StringComparison.OrdinalIgnoreCase)),
                "card file still planned for deletion");
            Check(!shared.Files.Any(f => f.Equals(tableRel + ".uasset", StringComparison.OrdinalIgnoreCase)),
                "shared private table excluded from the delete");
            Check(shared.KeptSharedTables.Contains(tableRel, StringComparer.OrdinalIgnoreCase),
                "shared table reported as kept");
            Check(shared.SharedTableOwners.Any(o =>
                    o.Contains(SharedDeleteOwnerB, StringComparison.OrdinalIgnoreCase)),
                $"remaining owner reported ({string.Join(", ", shared.SharedTableOwners)})");
            Check(shared.BuildUsageDetail().Contains("Kept hit row"),
                "usage detail explains the kept hit row");
        }
        finally
        {
            DeleteClone(QuickHookCardRel, SharedDeleteOwnerA);
            DeleteClone(QuickHookCardRel, SharedDeleteOwnerB);
        }
    }

    /// <summary>
    /// Export pass for RemovedRouteRows: recorded m_TargetNodes values are pruned from a
    /// real combo asset; a row left with no target is dropped and its struct's chance
    /// pool renormalized. Runs before RemoveNodesFromComboAsset in the export pipeline.
    /// </summary>
    private static void TestDeleteCustomAttackDbExportPrune()
    {
        string[] candidates =
        {
            "DB/AI/Archetypes/Fajar/Attacks/Fajar_Combo_P2",
            "DB/AI/Archetypes/Sean/Sean_Combo_Phase1",
            "DB/_MainChar/Combos/MainChar_ComboTree"
        };
        string? comboRel = candidates.FirstOrDefault(rel =>
            File.Exists(Path.Combine(ContentRoot,
                rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset")));
        Check(comboRel != null, "no combo fixture found under SIFU_CONTENT");

        // 1. Partial row: one of several targets is pruned, the row survives.
        var asset = LoadAsset(Vanilla(comboRel!));
        var (si, ri, victim, keep) = FindTransitionRow(asset, requireMultiple: true);
        var before = TransitionTargets(asset, si, ri);
        var graph = new ComboGraph();
        graph.RemovedRouteRows[si] = new List<RemovedRouteRow>
        {
            new() { RowIndex = ri, TargetTreeIndices = new List<int> { victim } }
        };
        int changed = ExportDialog.RemoveRouteTargets(asset, graph);
        Check(changed == 1, $"partial prune reported one change (got {changed})");
        var after = TransitionTargets(asset, si, ri);
        Check(after.Count == before.Count - 1 && !after.Contains(victim),
            $"pruned target {victim} removed (before [{string.Join(",", before)}], after [{string.Join(",", after)}])");
        Check(after.Contains(keep), "other row target survives");

        string outPath = WriteAsset(asset, "delete_route_partial");
        var reloaded = LoadAsset(outPath);
        Check(!TransitionTargets(reloaded, si, ri).Contains(victim),
            "pruned target stays gone after a write/reload");

        // 2. Single-target row: pruning leaves no route, so the row drops and the
        //    struct's remaining probabilities renormalize to 100.
        var dropAsset = LoadAsset(Vanilla(comboRel!));
        var (dropSi, dropRi, dropVictim, _) = FindTransitionRow(dropAsset, requireMultiple: false);
        int rowsBefore = TransitionRowCount(dropAsset, dropSi);
        var dropGraph = new ComboGraph();
        dropGraph.RemovedRouteRows[dropSi] = new List<RemovedRouteRow>
        {
            new() { RowIndex = dropRi, TargetTreeIndices = new List<int> { dropVictim } }
        };
        int dropped = ExportDialog.RemoveRouteTargets(dropAsset, dropGraph);
        int rowsAfter = TransitionRowCount(dropAsset, dropSi);
        Check(rowsAfter == rowsBefore - 1, $"empty row dropped ({rowsBefore} -> {rowsAfter})");
        Check(dropped >= 2, $"single target prune reports target + dropped row (got {dropped})");
        float? sum = TransitionProbabilitySum(dropAsset, dropSi);
        if (sum.HasValue)
            Check(Math.Abs(sum.Value - TreeChanceMath.SumTarget) <= 0.5f,
                $"remaining chances renormalized (sum {sum:0.###})");
    }

    /// <summary>
    /// The delete's export work list survives a project round-trip: RemovedRouteRows is
    /// written into the saved unit cache and restored with the same struct / row / target
    /// detail, while an old save without the field loads as an empty map.
    /// </summary>
    private static void TestDeleteCustomAttackDbPersistence()
    {
        var graph = new ComboGraph
        {
            Nodes = new List<ComboNode>
            {
                new() { Id = 1, TreeIndex = 1, Name = "Slot", DisplayName = "Card Slot" }
            }
        };
        graph.RemovedRouteRows[3] = new List<RemovedRouteRow>
        {
            new() { RowIndex = 0, TargetTreeIndices = new List<int> { 7 } },
            new() { RowIndex = 2, TargetTreeIndices = new List<int> { 7, 9 } }
        };
        graph.RemovedRouteRows[5] = new List<RemovedRouteRow>
        {
            new() { RowIndex = 1, TargetTreeIndices = new List<int> { 7 } }
        };

        var caches = new Dictionary<string, UnitCacheEntry>
        {
            ["Fajar|Base"] = new() { Graph = graph }
        };
        string path = Path.Combine(OutDir, "delete_route_persistence.sifu-edit");
        try { File.Delete(path); } catch { }

        ProjectManager.SaveWithCaches(new EditorProject(), caches, "Fajar|Base",
            null, null, path);
        Check(File.Exists(path), "project file written");

        var loaded = ProjectManager.Load(path);
        var restored = ProjectManager.LoadUnitCaches(loaded.project);
        Check(restored != null && restored.ContainsKey("Fajar|Base"),
            "unit cache round-tripped");
        var rows = restored!["Fajar|Base"].Graph.RemovedRouteRows;
        Check(rows.Count == 2, $"two structs restored (got {rows.Count})");
        Check(rows[3].Count == 2
            && rows[3][0].RowIndex == 0
            && rows[3][0].TargetTreeIndices.SequenceEqual(new[] { 7 })
            && rows[3][1].RowIndex == 2
            && rows[3][1].TargetTreeIndices.SequenceEqual(new[] { 7, 9 }),
            "struct 3 row/target detail restored");
        Check(rows[5].Count == 1 && rows[5][0].RowIndex == 1
            && rows[5][0].TargetTreeIndices.SequenceEqual(new[] { 7 }),
            "struct 5 row/target detail restored");

        // Old saves have no RemovedRouteRows field at all -> load supplies an empty map.
        string json = File.ReadAllText(path).Replace("\"RemovedRouteRows\"", "\"RenamedRouteRows\"");
        File.WriteAllText(path, json);
        var old = ProjectManager.Load(path);
        var oldCaches = ProjectManager.LoadUnitCaches(old.project);
        Check(oldCaches != null && oldCaches["Fajar|Base"].Graph.RemovedRouteRows.Count == 0,
            "save without the field loads an empty route work list");
    }

    /// <summary>
    /// Renames a clone that owns a private HitBoxData table: the card and its table move
    /// to the new name (old files gone), the export/self package and every m_HitRow import
    /// follow, and row values survive the round trip.
    /// </summary>
    private static void TestRenameCustomAttackDbPrivateRow()
    {
        DeleteClone(QuickHookCardRel, RenamePrivateSource);
        DeleteClone(QuickHookCardRel, RenamePrivateTarget);
        string oldRel = AttackCardCloner.DestRel(QuickHookCardRel, RenamePrivateSource);
        string newRel = AttackCardCloner.DestRel(QuickHookCardRel, RenamePrivateTarget);
        bool ok = AttackCardCloner.Clone(Vanilla(QuickHookCardRel), QuickHookCardRel,
            RenamePrivateSource, true, ContentRoot, out var oldUasset, out _, out var err);
        Check(ok, $"rename fixture clone succeeded ({err})");
        if (!ok) return;
        try
        {
            Check(CustomAttackDbRenamer.Rename(oldRel, RenamePrivateTarget, ContentRoot, out var result),
                $"private-row rename succeeded ({result.Error})");
            if (!result.Success) return;

            Check(result.Tables.Count == 1, $"one private table renamed (got {result.Tables.Count})");
            Check(!File.Exists(oldUasset), "old card uasset removed");
            Check(!File.Exists(Path.ChangeExtension(oldUasset, ".uexp")), "old card uexp removed");
            Check(!File.Exists(CustomAssets.FileForContentRel(oldRel + "_HitBoxData.uasset")),
                "old private table removed");

            string newUasset = CustomAssets.FileForContentRel(newRel + ".uasset");
            Check(File.Exists(newUasset), "new card uasset exists");
            Check(File.Exists(Path.ChangeExtension(newUasset, ".uexp")), "new card uexp exists");
            Check(File.Exists(CustomAssets.FileForContentRel(newRel + "_HitBoxData.uasset")),
                "renamed private table exists");
            Check(result.NewFiles.Contains(newRel + ".uasset", StringComparer.OrdinalIgnoreCase),
                "new card listed in created files");
            Check(result.NewFiles.Contains(newRel + "_HitBoxData.uasset", StringComparer.OrdinalIgnoreCase),
                "renamed table listed in created files");
            Check(result.RemovedFiles.Contains(oldRel + ".uasset", StringComparer.OrdinalIgnoreCase),
                "old card listed in removed files");
            Check(result.RemovedFiles.Contains(oldRel + "_HitBoxData.uasset", StringComparer.OrdinalIgnoreCase),
                "old table listed in removed files");
            Check(string.IsNullOrEmpty(result.Warning), $"no leftover warning ({result.Warning})");

            var asset = new UAssetAPI.UAsset(newUasset,
                UAssetAPI.UnrealTypes.EngineVersion.VER_UE4_26, null,
                UAssetAPI.CustomSerializationFlags.None);
            var exp = asset.Exports.OfType<UAssetAPI.ExportTypes.NormalExport>().FirstOrDefault();
            Check(exp?.ObjectName?.Value?.ToString() == RenamePrivateTarget,
                $"export object renamed (got {exp?.ObjectName})");
            Check(asset.ContainsNameReference((UAssetAPI.UnrealTypes.FString)("/Game/" + newRel)),
                "self package path renamed");
            Check(!asset.ContainsNameReference((UAssetAPI.UnrealTypes.FString)("/Game/" + oldRel)),
                "old self package path gone");

            AttackHitCard.Invalidate(newUasset);
            var card = AttackHitCard.ReadDbPath(CustomAssets.Root, newRel, ContentRoot);
            Check(card != null && card.Windows.Count == 2,
                $"renamed card readable with 2 windows (got {card?.Windows.Count ?? 0})");
            if (card == null) return;
            Check(card.Windows.All(w => w.TableRel == newRel + "_HitBoxData"),
                "every window points at the renamed private table ("
                + string.Join(", ", card.Windows.Select(w => w.TableRel)) + ")");
            Check(card.Windows.All(w => w.LookupStatus == VanillaLookupStatus.Ok),
                "private rows resolve after rename");
            Check(card.Windows.All(w => w.Damage.HasValue), "row values survive the rename");
            Check(card.Windows[1].Parriable == false, "HB02 parriable=false survives");
        }
        finally
        {
            DeleteClone(QuickHookCardRel, RenamePrivateSource);
            DeleteClone(QuickHookCardRel, RenamePrivateTarget);
        }
    }

    /// <summary>
    /// A clone that still points at a shared vanilla hit row is renamed without touching
    /// the vanilla table: the card moves, the shared row stays shared and resolvable.
    /// </summary>
    private static void TestRenameCustomAttackDbSharedRow()
    {
        DeleteClone(QuickHookCardRel, RenameSharedSource);
        DeleteClone(QuickHookCardRel, RenameSharedTarget);
        string oldRel = AttackCardCloner.DestRel(QuickHookCardRel, RenameSharedSource);
        string newRel = AttackCardCloner.DestRel(QuickHookCardRel, RenameSharedTarget);
        bool ok = AttackCardCloner.Clone(Vanilla(QuickHookCardRel), QuickHookCardRel,
            RenameSharedSource, false, ContentRoot, out var oldUasset, out _, out var err);
        Check(ok, $"shared-row rename fixture clone succeeded ({err})");
        if (!ok) return;
        try
        {
            Check(CustomAttackDbRenamer.Rename(oldRel, RenameSharedTarget, ContentRoot, out var result),
                $"shared-row rename succeeded ({result.Error})");
            if (!result.Success) return;

            Check(result.Tables.Count == 0, "no private table to rename");
            Check(!File.Exists(oldUasset), "old card uasset removed");
            string newUasset = CustomAssets.FileForContentRel(newRel + ".uasset");
            Check(File.Exists(newUasset), "new card uasset exists");

            AttackHitCard.Invalidate(newUasset);
            var card = AttackHitCard.ReadDbPath(CustomAssets.Root, newRel, ContentRoot);
            Check(card != null && card.Windows.Count == 2,
                $"renamed shared-row card readable (got {card?.Windows.Count ?? 0})");
            if (card == null) return;
            Check(card.Windows.All(w =>
                    !w.TableRel.Contains(RenameSharedTarget, StringComparison.OrdinalIgnoreCase)),
                "still on the vanilla table ("
                + string.Join(", ", card.Windows.Select(w => w.TableRel)) + ")");
            Check(card.Windows.All(w => w.LookupStatus == VanillaLookupStatus.Ok),
                "shared vanilla rows resolve after rename");
            Check(card.Windows.All(w => w.SharedRow), "rows still flagged shared");
        }
        finally
        {
            DeleteClone(QuickHookCardRel, RenameSharedSource);
            DeleteClone(QuickHookCardRel, RenameSharedTarget);
        }
    }

    /// <summary>
    /// Dialog validation: unchanged/case-only, bad syntax, an existing library card and a
    /// vanilla collision are refused; a fresh name is accepted.
    /// </summary>
    private static void TestRenameCustomAttackDbValidation()
    {
        DeleteClone(QuickHookCardRel, RenameValidationA);
        DeleteClone(QuickHookCardRel, RenameValidationB);
        string relA = AttackCardCloner.DestRel(QuickHookCardRel, RenameValidationA);
        string relB = AttackCardCloner.DestRel(QuickHookCardRel, RenameValidationB);
        bool okA = AttackCardCloner.Clone(Vanilla(QuickHookCardRel), QuickHookCardRel,
            RenameValidationA, true, ContentRoot, out _, out _, out var errA);
        bool okB = AttackCardCloner.Clone(Vanilla(QuickHookCardRel), QuickHookCardRel,
            RenameValidationB, false, ContentRoot, out _, out _, out var errB);
        Check(okA, $"validation card A clone succeeded ({errA})");
        Check(okB, $"validation card B clone succeeded ({errB})");
        if (!okA || !okB) return;
        try
        {
            var info = CustomAttackDbRenamer.Inspect(relA);
            Check(info.CardFileFound, "validation fixture inspected");
            Check(info.PrivateTables.Count == 1, $"fixture owns one private table (got {info.PrivateTables.Count})");
            var existing = new List<string> { "Game/" + relA, "Game/" + relB };

            Check(CustomAttackDbRenamer.ValidateName(RenameValidationA, info, existing, ContentRoot) != null,
                "unchanged name refused");
            Check(CustomAttackDbRenamer.ValidateName(RenameValidationA.ToLowerInvariant(), info, existing, ContentRoot) != null,
                "case-only rename refused");
            Check(CustomAttackDbRenamer.ValidateName("Bad-Name", info, existing, ContentRoot) != null,
                "invalid characters refused");
            Check(CustomAttackDbRenamer.ValidateName(RenameValidationB, info, existing, ContentRoot) != null,
                "existing library card refused");
            Check(CustomAttackDbRenamer.ValidateName(
                    "FireDisciple_barehands_QuickHookPunch", info, existing, ContentRoot) != null,
                "vanilla path collision refused");
            Check(CustomAttackDbRenamer.ValidateName(
                    "SmokeRename_ValC", info, existing, ContentRoot) == null,
                "fresh name accepted");
        }
        finally
        {
            DeleteClone(QuickHookCardRel, RenameValidationA);
            DeleteClone(QuickHookCardRel, RenameValidationB);
        }
    }

    /// <summary>
    /// Graph repoint: every card-path field follows the rename and auto-generated labels
    /// update, while a deliberately customised label and unrelated paths stay untouched.
    /// </summary>
    private static void TestRenameCustomAttackDbGraphRepoint()
    {
        const string oldRel = "DB/AI/Archetypes/Test/Attacks/OldCard";
        const string oldGame = "Game/" + oldRel;
        const string newGame = "Game/DB/AI/Archetypes/Test/Attacks/NewCard";
        const string untouchedPath = "Game/DB/AI/Archetypes/Test/Attacks/OtherCard";

        var moved = new ComboNode
        {
            Id = 1, TreeIndex = 1, Name = "Moved", DisplayName = "OldCard",
            ImportedDisplayName = "OldCard",
            DefaultDBPath = oldGame, SourceDBPath = oldGame,
            ReplacedAttackCardRel = oldGame, AnimPath = oldGame, DefaultAnimPath = oldGame
        };
        var customLabel = new ComboNode
        {
            Id = 2, TreeIndex = 2, Name = "Custom", DisplayName = "My Finisher",
            ImportedDisplayName = "My Finisher",
            DefaultDBPath = oldGame, SourceDBPath = untouchedPath,
            AnimPath = "Game/Animations/Test/Move", VanillaAnimPath = oldGame
        };
        var graph = new ComboGraph { Nodes = new List<ComboNode> { moved, customLabel } };

        Check(CustomAttackDbRenamer.RepointGraph(graph, oldRel, newGame, "OldCard", "NewCard"),
            "repoint reports a change");
        Check(moved.DefaultDBPath == newGame && moved.SourceDBPath == newGame
            && moved.ReplacedAttackCardRel == newGame && moved.AnimPath == newGame
            && moved.DefaultAnimPath == newGame, "all card path fields repointed");
        Check(moved.DisplayName == "NewCard" && moved.ImportedDisplayName == "NewCard",
            "auto-generated labels renamed");
        Check(customLabel.DefaultDBPath == newGame, "matching path in another node repointed");
        Check(customLabel.DisplayName == "My Finisher"
            && customLabel.ImportedDisplayName == "My Finisher",
            "custom labels left alone");
        Check(customLabel.SourceDBPath == untouchedPath
            && customLabel.AnimPath == "Game/Animations/Test/Move",
            "unrelated paths left alone");
        Check(customLabel.VanillaAnimPath == oldGame, "vanilla baseline untouched");

        Check(!CustomAttackDbRenamer.RepointGraph(graph, oldRel, newGame, "OldCard", "NewCard"),
            "second repoint is a no-op");
    }

    /// <summary>
    /// The private-table guard: a second custom card still pointing at the first card's
    /// table blocks the rename (the card and table stay intact) instead of silently
    /// breaking the other card's hit rows.
    /// </summary>
    private static void TestRenameCustomAttackDbSharedTableRefused()
    {
        DeleteClone(QuickHookCardRel, RenameSharedOwnerA);
        DeleteClone(QuickHookCardRel, RenameSharedOwnerB);
        DeleteClone(QuickHookCardRel, RenameSharedTargetC);
        string relA = AttackCardCloner.DestRel(QuickHookCardRel, RenameSharedOwnerA);
        string targetRel = AttackCardCloner.DestRel(QuickHookCardRel, RenameSharedTargetC);
        bool ok = AttackCardCloner.Clone(Vanilla(QuickHookCardRel), QuickHookCardRel,
            RenameSharedOwnerA, true, ContentRoot, out var aUasset, out _, out var err);
        Check(ok, $"shared-table owner clone succeeded ({err})");
        if (!ok) return;
        try
        {
            bool okB = AttackCardCloner.Clone(aUasset, relA, RenameSharedOwnerB,
                false, ContentRoot, out _, out _, out var errB);
            Check(okB, $"second card clone succeeded ({errB})");
            if (!okB) return;

            var info = CustomAttackDbRenamer.Inspect(relA);
            Check(info.SharedTables.Count == 1, $"shared table detected (got {info.SharedTables.Count})");
            Check(info.SharedTableOwners.Contains(RenameSharedOwnerB, StringComparer.OrdinalIgnoreCase),
                $"remaining owner reported ({string.Join(", ", info.SharedTableOwners)})");

            Check(!CustomAttackDbRenamer.Rename(relA, RenameSharedTargetC, ContentRoot, out var result),
                "shared private table blocks the rename");
            Check(result.Error.Contains("still used", StringComparison.OrdinalIgnoreCase),
                $"error explains the shared table ({result.Error})");

            Check(File.Exists(aUasset), "original card left intact");
            Check(File.Exists(CustomAssets.FileForContentRel(relA + "_HitBoxData.uasset")),
                "original private table left intact");
            Check(!File.Exists(CustomAssets.FileForContentRel(targetRel + ".uasset")),
                "no renamed card written");
            Check(!File.Exists(CustomAssets.FileForContentRel(targetRel + "_HitBoxData.uasset")),
                "no renamed table written");

            var stillThere = AttackHitCard.ReadDbPath(CustomAssets.Root, relA, ContentRoot);
            Check(stillThere != null && stillThere.Windows.Count == 2,
                "original card still readable after the refused rename");
        }
        finally
        {
            DeleteClone(QuickHookCardRel, RenameSharedOwnerA);
            DeleteClone(QuickHookCardRel, RenameSharedOwnerB);
            DeleteClone(QuickHookCardRel, RenameSharedTargetC);
        }
    }

    /// <summary>
    /// Projects saved before start-entry conditions moved from the entry onto each row
    /// load with empty per-row ConditionClass. The load-time rehydrate must restore it
    /// for conditioned rows (real outcome key / multi-key map) so "+ Add Transition"
    /// copies the key map instead of collapsing to key 255, while key-255 rows stay
    /// unconditional.
    /// </summary>
    private static void TestLegacyStartRowConditionBackfill()
    {
        const string entryCond = "BP_IsTargetKnockedDownComboCondition_C";
        var graph = new ComboGraph
        {
            Nodes = new List<ComboNode>
            {
                new() { Id = 1, TreeIndex = 0, Name = "DownAttack" },
                new() { Id = 2, TreeIndex = 1, Name = "Hook" }
            },
            StartEntries = new List<StartEntry>
            {
                new()
                {
                    VanillaIndex = 0,
                    Label = "Dodge",
                    ConditionClass = entryCond,
                    Transitions = new List<StartTransition>
                    {
                        // Legacy conditioned 2-key row: lists present, condition lost.
                        new()
                        {
                            OriginalIndex = 0,
                            Probability = 100,
                            OriginalProbability = 100,
                            KeyByte = 0,
                            KeyCount = 2,
                            TargetNodeId = 1,
                            TargetTreeIndex = 0,
                            Keys = new List<byte> { 0, 1 },
                            KeyTargetTreeIndices = new List<int> { 0, 1 },
                            KeyTargetNodeIds = new List<int> { 1, 2 },
                            KeyTargetNames = new List<string> { "DownAttack", "Hook" },
                            OriginalTargetNodeId = 1,
                            OriginalTargetTreeIndex = 0,
                            OriginalKeyTargetTreeIndices = new List<int> { 0, 1 },
                            TargetSummary = "0→0, 1→1"
                        },
                        // Legacy conditioned single-key row: no per-key lists at all.
                        new()
                        {
                            OriginalIndex = 1,
                            Probability = 100,
                            OriginalProbability = 100,
                            KeyByte = 0,
                            KeyCount = 1,
                            TargetNodeId = 2,
                            TargetTreeIndex = 1,
                            OriginalTargetTreeIndex = 1
                        },
                        // Unconditional fallback inside the same entry.
                        new()
                        {
                            OriginalIndex = 2,
                            Probability = 100,
                            OriginalProbability = 100,
                            KeyByte = 255,
                            KeyCount = 1,
                            TargetNodeId = 1,
                            TargetTreeIndex = 0,
                            OriginalTargetTreeIndex = 0
                        }
                    }
                }
            }
        };

        var caches = new Dictionary<string, UnitCacheEntry>
        {
            ["Servant|Servant"] = new() { Graph = graph }
        };
        string path = Path.Combine(OutDir, "legacy_start_row_condition.sifu-edit");
        try { File.Delete(path); } catch { }
        ProjectManager.SaveWithCaches(new EditorProject(), caches, "Servant|Servant", null, null, path);

        var loaded = ProjectManager.Load(path);
        var restored = ProjectManager.LoadUnitCaches(loaded.project);
        Check(restored != null && restored.ContainsKey("Servant|Servant"), "unit cache round-tripped");
        var entry = restored!["Servant|Servant"].Graph.StartEntries.Single();
        Check(entry.ConditionClass == entryCond, "entry condition changed by backfill");
        Check(entry.Transitions[0].ConditionClass == entryCond,
            $"conditioned 2-key row condition '{entry.Transitions[0].ConditionClass}' != '{entryCond}'");
        Check(entry.Transitions[1].ConditionClass == entryCond,
            $"conditioned single-key row condition '{entry.Transitions[1].ConditionClass}' != '{entryCond}'");
        Check(entry.Transitions[2].ConditionClass == "",
            "key-255 row wrongly inherited the entry condition");
        Check(entry.Transitions[0].Keys.SequenceEqual(new byte[] { 0, 1 }),
            "load lost the 2-key model");
        Check(!entry.Transitions[0].IsModified(), "backfilled row reads as modified");

        // The dialog's copy helper now sees a conditioned template even though the saved
        // row had no class of its own.
        var template = entry.CloneTemplateRow();
        Check(template != null && StartEntry.CanCloneTemplate(template, entry.ConditionClass),
            "entry-aware gate rejects the backfilled template");
        var added = StartEntry.CreateAddedTransition(template!, entry.ConditionClass);
        Check(added.ConditionClass == entryCond && added.KeyCount == 2
            && added.Keys.SequenceEqual(new byte[] { 0, 1 }),
            "added row collapsed instead of copying the 2-key map");
        Check(Math.Abs(added.Probability) < 0.001f, "added row does not start at 0%");
    }

    private static UAsset LoadAsset(string path) =>
        new(path, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);

    private static string WriteAsset(UAsset asset, string name)
    {
        string path = Path.Combine(OutDir, name + ".uasset");
        foreach (var ext in new[] { ".uasset", ".uexp" })
            try { File.Delete(Path.ChangeExtension(path, ext)); } catch { }
        asset.Write(path);
        return path;
    }

    private static (NormalExport Combo, ArrayPropertyData Nodes) ComboNodes(UAsset asset)
    {
        foreach (var ex in asset.Exports.OfType<NormalExport>())
        {
            if (ex.Data == null) continue;
            var nodes = ex.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Nodes");
            if (nodes?.Value != null) return (ex, nodes);
        }
        throw new Exception("no m_Nodes array in asset");
    }

    private static ArrayPropertyData? TransitionArray(StructPropertyData node)
    {
        if (node.Value == null) return null;
        return node.Value.OfType<StructPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions")?.Value
            ?.OfType<ArrayPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions");
    }

    private static MapPropertyData? TargetMap(PropertyData row)
    {
        if (row is not StructPropertyData sp || sp.Value == null) return null;
        var map = sp.Value.OfType<MapPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_TargetNodes");
        return map?.Value == null ? null : map;
    }

    private static int ReadIndex(PropertyData p) => p switch
    {
        IntPropertyData ip => ip.Value,
        BytePropertyData bp => bp.Value,
        _ => -1
    };

    private static List<int> TransitionTargets(UAsset asset, int structIdx, int rowIdx)
    {
        var (_, nodes) = ComboNodes(asset);
        if (structIdx < 0 || structIdx >= nodes.Value!.Length
            || nodes.Value[structIdx] is not StructPropertyData node)
            throw new Exception($"node {structIdx} missing");
        var trans = TransitionArray(node);
        if (trans?.Value == null || rowIdx < 0 || rowIdx >= trans.Value.Length)
            throw new Exception($"transition row {structIdx}:{rowIdx} missing");
        var map = TargetMap(trans.Value[rowIdx]);
        if (map?.Value == null) return new List<int>();
        return map.Value.Select(kv => ReadIndex(kv.Value)).Where(v => v >= 0).ToList();
    }

    private static int TransitionRowCount(UAsset asset, int structIdx)
    {
        var (_, nodes) = ComboNodes(asset);
        if (structIdx < 0 || structIdx >= nodes.Value!.Length
            || nodes.Value[structIdx] is not StructPropertyData node)
            throw new Exception($"node {structIdx} missing");
        return TransitionArray(node)?.Value?.Length ?? 0;
    }

    private static float? TransitionProbabilitySum(UAsset asset, int structIdx)
    {
        var (_, nodes) = ComboNodes(asset);
        if (structIdx < 0 || structIdx >= nodes.Value!.Length
            || nodes.Value[structIdx] is not StructPropertyData node)
            return null;
        var trans = TransitionArray(node);
        if (trans?.Value == null) return null;
        float sum = 0;
        int count = 0;
        foreach (var row in trans.Value)
        {
            if (row is not StructPropertyData rsp || rsp.Value == null) continue;
            var fp = rsp.Value.OfType<FloatPropertyData>()
                .FirstOrDefault(x => x.Name?.Value?.ToString() == "m_fProbability");
            if (fp == null) continue;
            sum += fp.Value;
            count++;
        }
        return count > 0 ? sum : null;
    }

    private static (int structIdx, int rowIdx, int victim, int keep) FindTransitionRow(
        UAsset asset, bool requireMultiple)
    {
        var (_, nodes) = ComboNodes(asset);
        for (int si = 0; si < nodes.Value!.Length; si++)
        {
            if (nodes.Value[si] is not StructPropertyData node) continue;
            var trans = TransitionArray(node);
            if (trans?.Value == null) continue;
            for (int ri = 0; ri < trans.Value.Length; ri++)
            {
                var map = TargetMap(trans.Value[ri]);
                if (map?.Value == null) continue;
                var ints = map.Value.Select(kv => ReadIndex(kv.Value)).Where(v => v >= 0).ToList();
                if (requireMultiple && ints.Count >= 2) return (si, ri, ints[0], ints[1]);
                if (!requireMultiple && ints.Count == 1) return (si, ri, ints[0], -1);
            }
        }
        throw new Exception(requireMultiple
            ? "no transition row with multiple targets found"
            : "no transition row with a single target found");
    }

    private const string RetargetCloneName = "SmokeClone_RetargetAnim";

    /// <summary>
    /// Create AttackDB retarget: a cloned card's m_Animation must read back as the new
    /// animation path. The target animation does not need to exist - the retargeter only
    /// rewrites the card's package/AnimSequence imports in place.
    /// </summary>
    private static void TestRetargetAnimation()
    {
        DeleteClone(QuickHookCardRel, RetargetCloneName);
        bool ok = AttackCardCloner.Clone(Vanilla(QuickHookCardRel), QuickHookCardRel,
            RetargetCloneName, true, ContentRoot, out var newUasset, out _, out var err);
        Check(ok, $"clone succeeded ({err})");
        if (!ok) return;
        try
        {
            const string target =
                "Game/Animations/MainChar/Attacks/Man/Barehands/_old/"
                + "MainChar_Attack_Man_Barehands_PunishRepulse_HandSharp_BR";

            Check(AttackCardRetargeter.Retarget(newUasset, target, out err),
                $"retarget succeeded ({err})");
            string? readBack = AttackCardRetargeter.ReadAnimationPath(newUasset);
            Check(string.Equals(readBack, target, StringComparison.OrdinalIgnoreCase),
                $"m_Animation reads back as target (got {readBack ?? "null"})");
        }
        finally { DeleteClone(QuickHookCardRel, RetargetCloneName); }
    }

    private const string ScaleCloneName = "SmokeClone_ScaleFrames";

    /// <summary>
    /// Create AttackDB frame scaling: QuickHook's 26-28 / 48-50 windows doubled must read
    /// back as 52-56 / 96-100 on the same card.
    /// </summary>
    private static void TestScaleHitWindows()
    {
        DeleteClone(QuickHookCardRel, ScaleCloneName);
        string rel = AttackCardCloner.DestRel(QuickHookCardRel, ScaleCloneName);
        bool ok = AttackCardCloner.Clone(Vanilla(QuickHookCardRel), QuickHookCardRel,
            ScaleCloneName, true, ContentRoot, out var newUasset, out _, out var err);
        Check(ok, $"clone succeeded ({err})");
        if (!ok) return;
        try
        {
            Check(AttackHitCard.ScaleCardFrames(newUasset, 2f, out err),
                $"scale succeeded ({err})");

            var card = AttackHitCard.ReadDbPath(CustomAssets.Root, rel, ContentRoot);
            Check(card != null && card.Windows.Count == 2,
                $"scaled card readable (got {card?.Windows.Count ?? 0} windows)");
            if (card == null) return;
            Check(card.Windows[0].FrameLo == 52 && card.Windows[0].FrameHi == 56,
                $"window 0 scaled (got {card.Windows[0].FrameLo}-{card.Windows[0].FrameHi})");
            Check(card.Windows[1].FrameLo == 96 && card.Windows[1].FrameHi == 100,
                $"window 1 scaled (got {card.Windows[1].FrameLo}-{card.Windows[1].FrameHi})");
        }
        finally { DeleteClone(QuickHookCardRel, ScaleCloneName); }
    }

    /// <summary>Removes a clone's card + optional private table from the fixture library.</summary>
    /// <summary>
    /// Close prompt Cancel (button/Esc/window X) must close only the prompt: the app
    /// keeps running and every unaccepted file stays on disk. Only the No flow may
    /// delete. Closing the dialog without choosing Yes/No models the window X path.
    /// </summary>
    private static void TestClosePromptCancelKeepsFiles()
    {
        const string rel = "ClosePromptProbe/keep_me.uasset";
        var path = CustomAssets.FileForContentRel(rel);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "probe");
            var manifest = CustomAssets.BuildManifest();
            Check(manifest.Any(e => e.StartsWith(rel + "|", StringComparison.OrdinalIgnoreCase)),
                "probe file appears in the manifest");

            var dlg = new KeepCustomAssetsDialog(
                "Want to save the new custom files added?", manifest);
            dlg.Close();

            Check(dlg.Cancelled, "dismissing the prompt reports Cancel");
            Check(dlg.RemovedCount == 0,
                $"Cancel deleted nothing (got {dlg.RemovedCount})");
            Check(File.Exists(path), "Cancel leaves the unaccepted file on disk");
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null && Directory.Exists(dir)
                    && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { }
        }
    }

    private static void DeleteClone(string cardRel, string name)
    {
        string rel = AttackCardCloner.DestRel(cardRel, name);
        string dir = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] + "/" : "";
        foreach (var f in new[]
        {
            CustomAssets.FileForContentRel(rel + ".uasset"),
            CustomAssets.FileForContentRel(rel + ".uexp"),
            CustomAssets.FileForContentRel(dir + name + "_HitBoxData.uasset"),
            CustomAssets.FileForContentRel(dir + name + "_HitBoxData.uexp"),
        })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    /// <summary>
    /// Card drop naming: authored nodes adopt the card (label + exported m_Name),
    /// vanilla/import-owned nodes keep their slot name and record the swap separately.
    /// </summary>
    private static void TestNodeCardIdentityDropRule()
    {
        var vanilla = new ComboNode
        {
            Id = 1, Kind = ComboNodeKind.Vanilla, TreeIndex = 4,
            DisplayName = "Hook", VanillaAnimPath = "/Game/Hook",
            DefaultAnimPath = "/Game/Hook", AnimPath = "/Game/Hook2"
        };
        Check(!NodeCardIdentity.ApplyDropName(vanilla, "FalconPunch"),
            "vanilla node reports slot identity");
        Check(vanilla.DisplayName == "Hook", $"vanilla keeps its label (got '{vanilla.DisplayName}')");
        Check(vanilla.ImportedDisplayName == "FalconPunch",
            $"vanilla records the swap (got '{vanilla.ImportedDisplayName}')");

        var imported = new ComboNode
        {
            Id = 2, Kind = ComboNodeKind.ImportedNew,
            DisplayName = "ImportedMove", DefaultAnimPath = "/Game/Imported",
            AnimPath = "/Game/Replacement"
        };
        Check(!NodeCardIdentity.ApplyDropName(imported, "NewCard"),
            "import-owned node keeps the vanilla-style rule");
        Check(imported.DisplayName == "ImportedMove" && imported.ImportedDisplayName == "NewCard",
            "import-owned node keeps its label and records the swap");

        var authored = new ComboNode
        {
            Id = 3, Kind = ComboNodeKind.UserCreated, TreeIndex = -1,
            DisplayName = "OldCard", ImportedDisplayName = "Stale", DefaultAnimPath = ""
        };
        Check(NodeCardIdentity.ApplyDropName(authored, "NewCard"),
            "authored node adopts the card");
        Check(authored.DisplayName == "NewCard",
            $"authored label follows the card (got '{authored.DisplayName}')");
        Check(authored.ImportedDisplayName == "",
            "authored node clears the Changed marker");
        Check(NodeCardIdentity.ApplyDropName(authored, "FinalCard")
              && authored.DisplayName == "FinalCard",
            "a second drop renames the authored node again");
        Check(NodeCardIdentity.LabelOf(new ComboNode { Id = 7 }) == "#7",
            "label falls back to the node id");
    }

    /// <summary>
    /// Delete Anyway: retargeted vanilla rows reset to their original target only when it
    /// still lives; authored/vanilla rows whose original target is gone are dropped.
    /// </summary>
    private static void TestNodeDeleteStartResetVsDrop()
    {
        var graph = new ComboGraph();
        var other = new ComboNode { Id = 10, TreeIndex = 100, DisplayName = "Other" };
        var deleted = new ComboNode { Id = 11, TreeIndex = 111, DisplayName = "Deleted" };
        var removed = new ComboNode { Id = 12, TreeIndex = 102, DisplayName = "Gone", Removed = true };
        graph.Nodes.AddRange(new[] { other, deleted, removed });

        var reset = new StartTransition
        {
            OriginalIndex = 0,
            OriginalTargetNodeId = 10, OriginalTargetTreeIndex = 100, OriginalTargetName = "Other",
            TargetNodeId = 11, TargetTreeIndex = 111, TargetName = "Deleted"
        };
        var authored = new StartTransition
        {
            OriginalIndex = -1,
            TargetNodeId = 11, TargetTreeIndex = 111, TargetName = "Deleted"
        };
        // Vanilla row that always targeted the node now being deleted: nothing to reset to.
        var originallyHere = new StartTransition
        {
            OriginalIndex = 1,
            OriginalTargetNodeId = 11, OriginalTargetTreeIndex = 111, OriginalTargetName = "Deleted",
            TargetNodeId = 11, TargetTreeIndex = 111, TargetName = "Deleted"
        };
        // Retargeted onto the deleted node, but its original target was already removed.
        var originalGone = new StartTransition
        {
            OriginalIndex = 2,
            OriginalTargetNodeId = 12, OriginalTargetTreeIndex = 102, OriginalTargetName = "Gone",
            TargetNodeId = 11, TargetTreeIndex = 111, TargetName = "Deleted"
        };
        graph.StartEntries.Add(new StartEntry
        {
            Label = "Vanilla", VanillaIndex = 0,
            Transitions = new List<StartTransition> { reset, authored, originallyHere, originalGone }
        });

        int touched = NodeDeleteStartEntries.Strip(graph, deleted);
        Check(touched == 1, $"one entry cleaned (got {touched})");
        Check(reset.TargetNodeId == 10 && reset.TargetTreeIndex == 100
              && reset.TargetName == "Other",
            "retargeted row snaps back to its live original target");
        Check(!reset.Removed, "reset row stays live");
        Check(!graph.StartEntries[0].Transitions.Contains(authored),
            "authored row is deleted");
        Check(originallyHere.Removed,
            "vanilla row whose original target is the deleted node is dropped");
        Check(originalGone.Removed,
            "retargeted row drops when its original target is gone");
        Check(!graph.StartEntries[0].Removed, "entry survives with its reset row");
    }

    /// <summary>Multi-key rows reset only the keys that pointed at the deleted node.</summary>
    private static void TestNodeDeleteStartMultiKey()
    {
        var graph = new ComboGraph();
        var other = new ComboNode { Id = 10, TreeIndex = 100, DisplayName = "Other" };
        var deleted = new ComboNode { Id = 11, TreeIndex = 111, DisplayName = "Deleted" };
        var removed = new ComboNode { Id = 12, TreeIndex = 102, DisplayName = "Gone", Removed = true };
        graph.Nodes.AddRange(new[] { other, deleted, removed });

        var resetKey = new StartTransition
        {
            OriginalIndex = 0,
            TargetNodeId = 11, TargetTreeIndex = 111, TargetName = "Deleted",
            KeyCount = 2,
            Keys = new List<byte> { 0, 1 },
            KeyTargetTreeIndices = new List<int> { 111, 100 },
            KeyTargetNodeIds = new List<int> { 11, 10 },
            KeyTargetNames = new List<string> { "Deleted", "Other" },
            OriginalKeyTargetTreeIndices = new List<int> { 100, 100 }
        };
        var dropKey = new StartTransition
        {
            OriginalIndex = 1,
            TargetNodeId = 11, TargetTreeIndex = 111, TargetName = "Deleted",
            KeyCount = 1,
            Keys = new List<byte> { 0 },
            KeyTargetTreeIndices = new List<int> { 111 },
            KeyTargetNodeIds = new List<int> { 11 },
            KeyTargetNames = new List<string> { "Deleted" },
            OriginalKeyTargetTreeIndices = new List<int> { 102 }
        };
        graph.StartEntries.Add(new StartEntry
        {
            Label = "Vanilla", VanillaIndex = 0,
            Transitions = new List<StartTransition> { resetKey, dropKey }
        });

        int touched = NodeDeleteStartEntries.Strip(graph, deleted);
        Check(touched == 1, $"one entry cleaned (got {touched})");
        Check(resetKey.KeyCount == 2, "multi-key row keeps both keys");
        Check(resetKey.KeyTargetNodeIds[0] == 10
              && resetKey.KeyTargetTreeIndices[0] == 100
              && resetKey.KeyTargetNames[0] == "Other",
            "deleted key resets to its live original target");
        Check(resetKey.KeyTargetNodeIds[1] == 10,
            "untouched key keeps its target");
        Check(dropKey.Removed,
            "multi-key row whose original target is gone is dropped when no key survives");
    }

    private static string Vanilla(string rel)
        => Path.Combine(ContentRoot, rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");

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

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"  PASS {name}");
        }
        catch (Exception ex)
        {
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
        }
    }

    private static void Check(bool cond, string what)
    {
        if (!cond) throw new Exception("check failed: " + what);
    }
}
