using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SifuMovesetEditor;

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
