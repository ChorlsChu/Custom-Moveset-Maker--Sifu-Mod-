using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace SifuMovesetEditor;

/// <summary>Why a window's vanilla row values could not be read (all fields null).</summary>
public enum VanillaLookupStatus
{
    /// <summary>Row was found and values were read (individual fields may still be absent from the row struct).</summary>
    Ok,
    /// <summary>The window has no m_HitRow DataTable/RowName reference at all.</summary>
    NoRowRef,
    /// <summary>The referenced HitBoxData table file could not be opened (path resolution failed or file missing).</summary>
    TableNotFound,
    /// <summary>The table opened but the named row does not exist in it (stale reference, e.g. _old cards).</summary>
    RowNotFound,
}

/// <summary>One m_HitBoxes window of an attack card plus its HitBoxData row's vanilla values.</summary>
public sealed class AttackHitWindowInfo
{
    public int Index;
    public string RowName = "";
    /// <summary>Content-relative path (no extension) of the HitBoxData table this window's row lives in.</summary>
    public string TableRel = "";
    public int FrameLo;
    public int FrameHi;
    /// <summary>EAttackMovementType suffix, display only (Horizontal / Thrust / Grab / ...).</summary>
    public string Movement = "";
    /// <summary>Why the vanilla row values are missing when they are.</summary>
    public VanillaLookupStatus LookupStatus = VanillaLookupStatus.Ok;
    // Vanilla row values; null = the field is absent from the row.
    public float? Damage;
    public float? StructureDamage;
    /// <summary>m_OnGuardImpact.m_fDamage (applies while the victim is guarding).</summary>
    public float? GuardDamage;
    /// <summary>m_OnGuardImpact.m_fStructureDamage.</summary>
    public float? GuardStructureDamage;
    /// <summary>m_OnDeflectImpact.m_fDamage (applies when the victim deflects).</summary>
    public float? DeflectDamage;
    /// <summary>m_OnDeflectImpact.m_fStructureDamage.</summary>
    public float? DeflectStructureDamage;
    public float? KnockbackDistance;
    public float? SuperDizzyDamage;
    public int? StunFrames;
    public int? FreezeFrames;
    public bool? Parriable;
    public bool? Lethal;
    public bool? BreakStructure;
    public bool? DizzyOnParry;
    public bool? DizzyOnAvoid;
    /// <summary>m_eAttackPower suffix (Light / Medium / Strong); null = absent from row.</summary>
    public string? AttackPower;
    /// <summary>First m_AttackPropertyLevel.m_Level (0 flinch, 1+ knockdown tiers); null = absent.</summary>
    public int? PropertyLevel;
    /// <summary>Row name shares no token with the card file name - likely reused by other attacks.</summary>
    public bool SharedRow;
}

public sealed class AttackHitCardData
{
    public string CardRel = "";
    public List<AttackHitWindowInfo> Windows = new();
}

/// <summary>
/// UAssetAPI reader/writer for an attack card's m_HitBoxes windows and their
/// HitBoxData table rows. Frame bounds live on the card (m_FrameRange.m_Anim);
/// damage/parry/stun/freeze/knockback live on the row referenced by m_HitRow.
/// </summary>
public static class AttackHitCard
{
    // The row carries three impact structs, each with its own m_fDamage /
    // m_fStructureDamage (+ use-flags): the game reads OnHit on a clean hit,
    // OnGuard while the victim guards, OnDeflect on deflect.
    private const string HitStructName = "m_OnHitImpact";
    private const string GuardStructName = "m_OnGuardImpact";
    private const string DeflectStructName = "m_OnDeflectImpact";

    private sealed class CacheEntry
    {
        public long Ticks;
        public AttackHitCardData? Values;
    }

    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static void Invalidate(string uassetPath)
    {
        try { Cache.Remove(uassetPath); } catch { }
    }

    public static AttackHitCardData? ReadDbPath(string contentRoot, string dbGamePath)
    {
        var path = AttackDbCard.ResolveUassetPath(contentRoot, dbGamePath);
        return path == null ? null : Read(path);
    }

    public static AttackHitCardData? Read(string uassetPath)
    {
        try
        {
            if (string.IsNullOrEmpty(uassetPath) || !File.Exists(uassetPath)) return null;
            long ticks = File.GetLastWriteTimeUtc(uassetPath).Ticks;
            if (Cache.TryGetValue(uassetPath, out var hit) && hit.Ticks == ticks)
                return hit.Values;
            var values = ReadCore(uassetPath);
            Cache[uassetPath] = new CacheEntry { Ticks = ticks, Values = values };
            return values;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the edit touches any field stored on the HitBoxData row (not the card).</summary>
    public static bool HasRowFields(AttackHitEdit? e)
        => e != null && (e.Damage.HasValue || e.StructureDamage.HasValue || e.KnockbackDistance.HasValue
            || e.DamageOnHit.HasValue || e.DamageOnGuard.HasValue || e.DamageOnDeflect.HasValue
            || e.StructureDamageOnHit.HasValue || e.StructureDamageOnGuard.HasValue || e.StructureDamageOnDeflect.HasValue
            || e.SuperDizzyDamage.HasValue
            || e.StunFrames.HasValue || e.FreezeFrames.HasValue || e.Parriable.HasValue
            || e.Lethal.HasValue || e.BreakStructure.HasValue
            || e.DizzyOnParry.HasValue || e.DizzyOnAvoid.HasValue
            || !string.IsNullOrEmpty(e.AttackPower) || e.PropertyLevel.HasValue);

    /// <summary>True when the edit touches any field stored on the card itself (frame bounds).</summary>
    public static bool HasCardFields(AttackHitEdit? e)
        => e != null && (e.FrameLo.HasValue || e.FrameHi.HasValue);

    /// <summary>
    /// Per-window diff of a modded card against vanilla: every field that differs becomes
    /// an AttackHitEdit, mirroring a manual edit in the Hit Windows dialog. Windows pair by
    /// index up to the shorter list (HitEdits cannot add or remove slots). A missing vanilla
    /// baseline means the card exists only in the mod - its bytes (hit data included) are
    /// harvested whole by CustomAssets, so no per-window edits are needed. Returns null when
    /// nothing differs.
    /// </summary>
    public static List<AttackHitEdit>? DiffAgainst(AttackHitCardData? vanilla, AttackHitCardData? mod)
    {
        if (vanilla == null || mod == null || mod.Windows.Count == 0) return null;

        int n = Math.Min(vanilla.Windows.Count, mod.Windows.Count);
        List<AttackHitEdit>? edits = null;
        for (int i = 0; i < n; i++)
        {
            var v = vanilla.Windows[i];
            var m = mod.Windows[i];
            var e = new AttackHitEdit { Index = m.Index };

            if (v.FrameLo != m.FrameLo) e.FrameLo = m.FrameLo;
            if (v.FrameHi != m.FrameHi) e.FrameHi = m.FrameHi;

            // Row fields need the mod's row resolved; a vanilla lookup failure leaves its
            // fields null so every present mod value imports (mod-added table/row case).
            if (m.LookupStatus == VanillaLookupStatus.Ok)
            {
                if (!SameValue(v.Damage, m.Damage)) e.Damage = m.Damage;
                if (!SameValue(v.GuardDamage, m.GuardDamage)) e.DamageOnGuard = m.GuardDamage;
                if (!SameValue(v.DeflectDamage, m.DeflectDamage)) e.DamageOnDeflect = m.DeflectDamage;
                if (!SameValue(v.StructureDamage, m.StructureDamage)) e.StructureDamage = m.StructureDamage;
                if (!SameValue(v.GuardStructureDamage, m.GuardStructureDamage))
                    e.StructureDamageOnGuard = m.GuardStructureDamage;
                if (!SameValue(v.DeflectStructureDamage, m.DeflectStructureDamage))
                    e.StructureDamageOnDeflect = m.DeflectStructureDamage;
                if (!SameValue(v.KnockbackDistance, m.KnockbackDistance)) e.KnockbackDistance = m.KnockbackDistance;
                if (!SameValue(v.SuperDizzyDamage, m.SuperDizzyDamage)) e.SuperDizzyDamage = m.SuperDizzyDamage;
                if (v.StunFrames != m.StunFrames) e.StunFrames = m.StunFrames;
                if (v.FreezeFrames != m.FreezeFrames) e.FreezeFrames = m.FreezeFrames;
                if (v.Parriable != m.Parriable) e.Parriable = m.Parriable;
                if (v.Lethal != m.Lethal) e.Lethal = m.Lethal;
                if (v.BreakStructure != m.BreakStructure) e.BreakStructure = m.BreakStructure;
                if (v.DizzyOnParry != m.DizzyOnParry) e.DizzyOnParry = m.DizzyOnParry;
                if (v.DizzyOnAvoid != m.DizzyOnAvoid) e.DizzyOnAvoid = m.DizzyOnAvoid;
                if (m.AttackPower != null
                    && !string.Equals(v.AttackPower, m.AttackPower, StringComparison.OrdinalIgnoreCase))
                    e.AttackPower = m.AttackPower;
                if (m.PropertyLevel.HasValue && v.PropertyLevel != m.PropertyLevel)
                    e.PropertyLevel = m.PropertyLevel;
            }

            if (!e.IsEmpty())
                (edits ??= new List<AttackHitEdit>()).Add(e);
        }
        return AttackHitEdit.Prune(edits);
    }

    /// <summary>Bit-exact float compare - matches the export write/verify path.</summary>
    private static bool SameValue(float? a, float? b)
        => a.HasValue && b.HasValue
            ? BitConverter.SingleToInt32Bits(a.Value) == BitConverter.SingleToInt32Bits(b.Value)
            : a == b;

    /// <summary>
    /// Row names that share no name token with the card file are plausibly reused by
    /// other attacks in the same family table - flagged in the dialog and export log.
    /// </summary>
    public static bool LooksSharedRow(string cardFileName, string rowName)
    {
        if (string.IsNullOrEmpty(rowName)) return true;
        var tokens = cardFileName.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 5).ToList();
        if (tokens.Count == 0) return true;
        return !tokens.Any(t => rowName.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Stages the card (unless already staged) and applies frame-bound edits via a
    /// full UAssetAPI rewrite, then reopens to verify. Byte buildup/range patches
    /// (if any) must already be on the staged file - this loads the staged bytes.
    /// </summary>
    public static bool PatchCardFrames(
        string baseUasset,
        string outUasset,
        string destBase,
        IReadOnlyList<AttackHitEdit> edits,
        List<(string src, string dest)> fileEntries,
        out string error,
        string? baseUexp = null)
    {
        error = "";
        if (!File.Exists(baseUasset))
        {
            error = $"base card missing: {baseUasset}";
            return false;
        }
        try
        {
            var outDir = Path.GetDirectoryName(outUasset);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            string? outUexp = Path.ChangeExtension(outUasset, ".uexp");
            string? srcUexp = baseUexp ?? Path.ChangeExtension(baseUasset, ".uexp");
            if (!fileEntries.Any(e => e.src == outUasset))
            {
                File.Copy(baseUasset, outUasset, true);
                if (File.Exists(srcUexp)) File.Copy(srcUexp, outUexp!, true);
                fileEntries.Add((outUasset, destBase + ".uasset"));
                if (File.Exists(outUexp!)) fileEntries.Add((outUexp!, destBase + ".uexp"));
            }
            else if (!File.Exists(outUexp!) && File.Exists(srcUexp))
            {
                File.Copy(srcUexp, outUexp!, true);
                fileEntries.Add((outUexp!, destBase + ".uexp"));
            }

            var asset = new UAsset(outUasset, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            var windows = FindHitWindows(asset);
            if (windows == null || windows.Count == 0)
            {
                error = "card has no m_HitBoxes windows";
                return false;
            }

            int applied = 0;
            foreach (var edit in edits)
            {
                if (!HasCardFields(edit)) continue;
                if (edit.Index < 0 || edit.Index >= windows.Count)
                {
                    error = $"window index {edit.Index} out of range ({windows.Count} windows)";
                    return false;
                }
                var w = windows[edit.Index];
                var bounds = DelayWindowCard.GetWindowBounds(w);
                if (bounds == null)
                {
                    error = $"window {edit.Index} has no readable m_FrameRange";
                    return false;
                }
                float lo = edit.FrameLo.HasValue ? edit.FrameLo.Value : bounds.Value.Lo;
                float hi = edit.FrameHi.HasValue ? edit.FrameHi.Value : bounds.Value.Hi;
                if (hi < lo)
                {
                    error = $"window {edit.Index}: frame hi {hi} < lo {lo}";
                    return false;
                }
                if (!DelayWindowCard.TrySetWindowBounds(w, lo, hi))
                {
                    error = $"window {edit.Index}: failed to set frame bounds";
                    return false;
                }
                applied++;
            }
            if (applied == 0)
            {
                error = "no frame edits to apply";
                return false;
            }

            asset.Write(outUasset);
            Invalidate(outUasset);

            // Reopen-verify every applied edit.
            var post = new UAsset(outUasset, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            var postWindows = FindHitWindows(post);
            if (postWindows == null)
            {
                error = "verification failed: staged card unreadable after write";
                return false;
            }
            foreach (var edit in edits)
            {
                if (!HasCardFields(edit)) continue;
                var b = DelayWindowCard.GetWindowBounds(postWindows[edit.Index]);
                if (b == null)
                {
                    error = $"verification failed: window {edit.Index} bounds missing";
                    return false;
                }
                if (edit.FrameLo.HasValue && (int)b.Value.Lo != edit.FrameLo.Value)
                {
                    error = $"verification failed: window {edit.Index} lo re-read as {b.Value.Lo}";
                    return false;
                }
                if (edit.FrameHi.HasValue && (int)b.Value.Hi != edit.FrameHi.Value)
                {
                    error = $"verification failed: window {edit.Index} hi re-read as {b.Value.Hi}";
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Stages the HitBoxData table (unless already staged) and applies every row edit,
    /// then reopens to verify. <paramref name="rowEdits"/> maps row name -> edit.
    /// </summary>
    public static bool PatchRows(
        string baseDtUasset,
        string outDtUasset,
        string destBase,
        IReadOnlyDictionary<string, AttackHitEdit> rowEdits,
        List<(string src, string dest)> fileEntries,
        out string error)
    {
        error = "";
        if (!File.Exists(baseDtUasset))
        {
            error = $"base table missing: {baseDtUasset}";
            return false;
        }
        try
        {
            var outDir = Path.GetDirectoryName(outDtUasset);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            string? outUexp = Path.ChangeExtension(outDtUasset, ".uexp");
            string? srcUexp = Path.ChangeExtension(baseDtUasset, ".uexp");
            if (!fileEntries.Any(e => e.src == outDtUasset))
            {
                File.Copy(baseDtUasset, outDtUasset, true);
                if (File.Exists(srcUexp)) File.Copy(srcUexp, outUexp!, true);
                fileEntries.Add((outDtUasset, destBase + ".uasset"));
                if (File.Exists(outUexp!)) fileEntries.Add((outUexp!, destBase + ".uexp"));
            }
            else if (!File.Exists(outUexp!) && File.Exists(srcUexp))
            {
                File.Copy(srcUexp, outUexp!, true);
                fileEntries.Add((outUexp!, destBase + ".uexp"));
            }

            var asset = new UAsset(outDtUasset, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            foreach (var kvp in rowEdits)
            {
                if (!PatchRowFields(asset, kvp.Key, kvp.Value, out error))
                    return false;
            }
            asset.Write(outDtUasset);

            var post = new UAsset(outDtUasset, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            foreach (var kvp in rowEdits)
            {
                var row = FindRow(post, kvp.Key);
                if (row == null)
                {
                    error = $"verification failed: row '{kvp.Key}' missing after write";
                    return false;
                }
                if (!VerifyRow(post, row, kvp.Value, out error))
                    return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Applies an edit's row-field values onto an open HitBoxData asset.</summary>
    public static bool PatchRowFields(UAsset asset, string rowName, AttackHitEdit edit, out string error)
    {
        error = "";
        var row = FindRow(asset, rowName);
        if (row?.Value == null)
        {
            error = $"row '{rowName}' not found in table";
            return false;
        }

        var hit = row.Value.OfType<StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == HitStructName);
        if (HasRowFieldsInImpact(edit) && hit?.Value == null)
        {
            error = $"row '{rowName}' has no {HitStructName} struct";
            return false;
        }

        // Damage / structure write into all three impact structs (the game reads
        // OnHit on a clean hit, OnGuard while guarding, OnDeflect on deflect).
        // An explicit per-impact override wins; otherwise the mirror value
        // (Damage / StructureDamage) applies to every path.
        if (!PatchImpact(row.Value, HitStructName, isHit: true,
                edit.DamageOnHit ?? edit.Damage, edit.StructureDamageOnHit ?? edit.StructureDamage,
                edit.DamageOnHit, edit.StructureDamageOnHit, rowName, out error))
            return false;
        if (!PatchImpact(row.Value, GuardStructName, isHit: false,
                edit.DamageOnGuard ?? edit.Damage, edit.StructureDamageOnGuard ?? edit.StructureDamage,
                edit.DamageOnGuard, edit.StructureDamageOnGuard, rowName, out error))
            return false;
        if (!PatchImpact(row.Value, DeflectStructName, isHit: false,
                edit.DamageOnDeflect ?? edit.Damage, edit.StructureDamageOnDeflect ?? edit.StructureDamage,
                edit.DamageOnDeflect, edit.StructureDamageOnDeflect, rowName, out error))
            return false;

        if (edit.KnockbackDistance.HasValue)
        {
            if (!TrySetFloat(hit!.Value, "m_fKnockBackDistance", edit.KnockbackDistance.Value, out error)
                || !TrySetBool(hit.Value, "m_bUseKnockBackDistance", true, out error)) return false;
        }
        if (edit.SuperDizzyDamage.HasValue)
        {
            if (!TrySetFloat(hit!.Value, "m_fSuperDizzyDamage", edit.SuperDizzyDamage.Value, out error)
                || !TrySetBool(hit.Value, "m_bUseSuperDizzyDamage", true, out error)) return false;
        }
        if (edit.StunFrames.HasValue)
        {
            int v = Math.Clamp(edit.StunFrames.Value, 0, 255);
            if (!TrySetByte(hit!.Value, "m_iNbFrameStuned", (byte)v, out error)
                || !TrySetBool(hit.Value, "m_bUseNbFrameStuned", true, out error)) return false;
        }
        if (edit.FreezeFrames.HasValue)
        {
            if (!TrySetInt(hit!.Value, "m_iFreezeFrames", edit.FreezeFrames.Value, out error)
                || !TrySetBool(hit.Value, "m_bUseFreezeFrames", true, out error)) return false;
        }
        if (edit.Parriable.HasValue && !TrySetBool(row.Value, "m_bParriable", edit.Parriable.Value, out error))
            return false;
        if (edit.Lethal.HasValue && !TrySetBool(row.Value, "m_bCanBeLethal", edit.Lethal.Value, out error))
            return false;
        if (edit.BreakStructure.HasValue && !TrySetBool(row.Value, "m_bCanBreakStructure", edit.BreakStructure.Value, out error))
            return false;
        if (edit.DizzyOnParry.HasValue && !TrySetBool(row.Value, "m_bDizzyOnParry", edit.DizzyOnParry.Value, out error))
            return false;
        if (edit.DizzyOnAvoid.HasValue && !TrySetBool(row.Value, "m_bDizzyOnAvoid", edit.DizzyOnAvoid.Value, out error))
            return false;
        if (!string.IsNullOrEmpty(edit.AttackPower)
            && !TrySetEnum(asset, row.Value, "m_eAttackPower", $"EAttackPowers::{edit.AttackPower}", out error))
            return false;
        if (edit.PropertyLevel.HasValue
            && !TrySetAttackPropertyLevel(row.Value, (uint)Math.Max(0, edit.PropertyLevel.Value), out error))
            return false;
        return true;
    }

    private static bool HasRowFieldsInImpact(AttackHitEdit e)
        => e.Damage.HasValue || e.StructureDamage.HasValue || e.KnockbackDistance.HasValue
        || e.DamageOnHit.HasValue || e.DamageOnGuard.HasValue || e.DamageOnDeflect.HasValue
        || e.StructureDamageOnHit.HasValue || e.StructureDamageOnGuard.HasValue || e.StructureDamageOnDeflect.HasValue
        || e.SuperDizzyDamage.HasValue
        || e.StunFrames.HasValue || e.FreezeFrames.HasValue;

    /// <summary>
    /// Writes resolved damage/structure into one impact struct of a row. The hit
    /// struct is required whenever any impact value resolves; guard/deflect only
    /// when an explicit per-impact override targets them (mirror-only writes skip
    /// silently if the struct is absent, keeping old rows exportable).
    /// </summary>
    private static bool PatchImpact(
        List<PropertyData> rowProps, string structName, bool isHit,
        float? damage, float? structure,
        float? explicitDamage, float? explicitStructure,
        string rowName, out string error)
    {
        error = "";
        if (!damage.HasValue && !structure.HasValue) return true;
        var imp = rowProps.OfType<StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == structName);
        if (imp?.Value == null)
        {
            if (isHit || explicitDamage.HasValue || explicitStructure.HasValue)
            {
                error = $"row '{rowName}' has no {structName} struct";
                return false;
            }
            return true;
        }
        if (damage.HasValue)
        {
            if (!TrySetFloat(imp.Value, "m_fDamage", damage.Value, out error)
                || !TrySetBool(imp.Value, "m_bUseDamage", true, out error)) return false;
        }
        if (structure.HasValue)
        {
            if (!TrySetFloat(imp.Value, "m_fStructureDamage", structure.Value, out error)
                || !TrySetBool(imp.Value, "m_bUseStructureDamage", true, out error)) return false;
        }
        return true;
    }

    private static bool VerifyRow(UAsset asset, StructPropertyData row, AttackHitEdit edit, out string error)
    {
        error = "";
        var hit = row.Value?.OfType<StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == HitStructName);
        if (!VerifyImpact(row, HitStructName, isHit: true,
                edit.DamageOnHit ?? edit.Damage, edit.StructureDamageOnHit ?? edit.StructureDamage,
                edit.DamageOnHit, edit.StructureDamageOnHit, out error))
            return false;
        if (!VerifyImpact(row, GuardStructName, isHit: false,
                edit.DamageOnGuard ?? edit.Damage, edit.StructureDamageOnGuard ?? edit.StructureDamage,
                edit.DamageOnGuard, edit.StructureDamageOnGuard, out error))
            return false;
        if (!VerifyImpact(row, DeflectStructName, isHit: false,
                edit.DamageOnDeflect ?? edit.Damage, edit.StructureDamageOnDeflect ?? edit.StructureDamage,
                edit.DamageOnDeflect, edit.StructureDamageOnDeflect, out error))
            return false;
        if (edit.KnockbackDistance.HasValue && GetFloat(hit?.Value, "m_fKnockBackDistance") != edit.KnockbackDistance.Value)
        { error = $"verification failed: knockback re-read"; return false; }
        if (edit.SuperDizzyDamage.HasValue && GetFloat(hit?.Value, "m_fSuperDizzyDamage") != edit.SuperDizzyDamage.Value)
        { error = $"verification failed: super dizzy re-read as {GetFloat(hit?.Value, "m_fSuperDizzyDamage")}"; return false; }
        if (edit.StunFrames.HasValue && GetByte(hit?.Value, "m_iNbFrameStuned") != (byte)Math.Clamp(edit.StunFrames.Value, 0, 255))
        { error = $"verification failed: stun frames re-read as {GetByte(hit?.Value, "m_iNbFrameStuned")}"; return false; }
        if (edit.FreezeFrames.HasValue && GetInt(hit?.Value, "m_iFreezeFrames") != edit.FreezeFrames.Value)
        { error = $"verification failed: freeze frames re-read as {GetInt(hit?.Value, "m_iFreezeFrames")}"; return false; }
        if (edit.Parriable.HasValue && GetBool(row.Value, "m_bParriable") != edit.Parriable.Value)
        { error = "verification failed: parriable re-read"; return false; }
        if (edit.Lethal.HasValue && GetBool(row.Value, "m_bCanBeLethal") != edit.Lethal.Value)
        { error = "verification failed: lethal re-read"; return false; }
        if (edit.BreakStructure.HasValue && GetBool(row.Value, "m_bCanBreakStructure") != edit.BreakStructure.Value)
        { error = "verification failed: break structure re-read"; return false; }
        if (edit.DizzyOnParry.HasValue && GetBool(row.Value, "m_bDizzyOnParry") != edit.DizzyOnParry.Value)
        { error = "verification failed: dizzy-on-parry re-read"; return false; }
        if (edit.DizzyOnAvoid.HasValue && GetBool(row.Value, "m_bDizzyOnAvoid") != edit.DizzyOnAvoid.Value)
        { error = "verification failed: dizzy-on-avoid re-read"; return false; }
        if (!string.IsNullOrEmpty(edit.AttackPower)
            && !string.Equals(GetEnum(row.Value, "m_eAttackPower"), $"EAttackPowers::{edit.AttackPower}", StringComparison.Ordinal))
        { error = $"verification failed: attack power re-read as {GetEnum(row.Value, "m_eAttackPower")}"; return false; }
        if (edit.PropertyLevel.HasValue
            && GetAttackPropertyLevel(row.Value) != edit.PropertyLevel.Value)
        { error = $"verification failed: property level re-read as {GetAttackPropertyLevel(row.Value)}"; return false; }
        return true;
    }

    /// <summary>
    /// Re-reads one impact struct's damage/structure after a write. Mirrors
    /// PatchImpact's skip rules: nothing to check -> ok; absent hit struct or an
    /// absent struct targeted by an explicit override -> fail; absent guard/
    /// deflect struct on a mirror-only write -> skip.
    /// </summary>
    private static bool VerifyImpact(
        StructPropertyData row, string structName, bool isHit,
        float? damage, float? structure,
        float? explicitDamage, float? explicitStructure, out string error)
    {
        error = "";
        if (!damage.HasValue && !structure.HasValue) return true;
        var imp = row.Value?.OfType<StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == structName);
        if (imp?.Value == null)
        {
            if (isHit || explicitDamage.HasValue || explicitStructure.HasValue)
            { error = $"verification failed: row has no {structName} struct"; return false; }
            return true;
        }
        if (damage.HasValue && GetFloat(imp.Value, "m_fDamage") != damage.Value)
        { error = $"verification failed: {structName} damage re-read as {GetFloat(imp.Value, "m_fDamage")}"; return false; }
        if (structure.HasValue && GetFloat(imp.Value, "m_fStructureDamage") != structure.Value)
        { error = $"verification failed: {structName} structure damage re-read as {GetFloat(imp.Value, "m_fStructureDamage")}"; return false; }
        return true;
    }

    private static AttackHitCardData? ReadCore(string uassetPath)
    {
        var asset = new UAsset(uassetPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
        var windows = FindHitWindows(asset);
        if (windows == null || windows.Count == 0) return null;

        var cardRel = AttackDbCard.GamePathToContentRel(uassetPath);
        var cardFileName = Path.GetFileNameWithoutExtension(uassetPath);
        var result = new AttackHitCardData { CardRel = cardRel };

        // Stage one open per distinct table so multi-window cards read rows cheaply.
        var tableCache = new Dictionary<string, (UAsset asset, string path)?>(StringComparer.OrdinalIgnoreCase);
        string contentRoot = ResolveContentRootFromCardPath(uassetPath);

        for (int i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            var info = new AttackHitWindowInfo { Index = i };

            var bounds = DelayWindowCard.GetWindowBounds(w);
            if (bounds.HasValue)
            {
                info.FrameLo = (int)bounds.Value.Lo;
                info.FrameHi = (int)bounds.Value.Hi;
            }

            var move = w.Value?.OfType<EnumPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_eMovementType");
            string moveStr = move?.Value?.ToString() ?? "";
            int colon = moveStr.LastIndexOf("::", StringComparison.Ordinal);
            info.Movement = colon >= 0 ? moveStr.Substring(colon + 2) : moveStr;

            var hitRow = w.Value?.OfType<StructPropertyData>()
                .FirstOrDefault(s => s.Name?.Value?.ToString() == "m_HitRow");
            var rn = hitRow?.Value?.OfType<NamePropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "RowName");
            info.RowName = rn?.Value?.ToString() ?? "";

            var dt = hitRow?.Value?.OfType<ObjectPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "DataTable");
            info.TableRel = TableRelFromObjectProperty(asset, dt);
            if (string.IsNullOrEmpty(info.TableRel) || string.IsNullOrEmpty(info.RowName))
            {
                info.LookupStatus = VanillaLookupStatus.NoRowRef;
                result.Windows.Add(info);
                continue;
            }

            if (!tableCache.TryGetValue(info.TableRel, out var entry))
            {
                entry = null;
                var dtPath = AttackDbCard.ResolveUassetPath(contentRoot, info.TableRel)
                    ?? AttackDbCard.ResolveUassetPath(CustomAssets.Root, info.TableRel);
                if (dtPath != null && File.Exists(dtPath))
                {
                    try
                    {
                        entry = (new UAsset(dtPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None), dtPath);
                    }
                    catch { entry = null; }
                }
                tableCache[info.TableRel] = entry;
            }
            if (entry.HasValue)
            {
                var row = FindRow(entry.Value.asset, info.RowName);
                if (row?.Value != null)
                {
                    ReadRowValues(row.Value, info);
                }
                else
                {
                    info.LookupStatus = VanillaLookupStatus.RowNotFound;
                }
            }
            else
            {
                info.LookupStatus = VanillaLookupStatus.TableNotFound;
            }

            info.SharedRow = LooksSharedRow(cardFileName, info.RowName);
            result.Windows.Add(info);
        }
        return result.Windows.Count > 0 ? result : null;
    }

    private static void ReadRowValues(List<PropertyData> rowProps, AttackHitWindowInfo info)
    {
        var hit = rowProps.OfType<StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == HitStructName);
        var hitProps = hit?.Value;
        info.Damage = GetFloat(hitProps, "m_fDamage");
        info.StructureDamage = GetFloat(hitProps, "m_fStructureDamage");
        var guard = rowProps.OfType<StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == GuardStructName);
        info.GuardDamage = GetFloat(guard?.Value, "m_fDamage");
        info.GuardStructureDamage = GetFloat(guard?.Value, "m_fStructureDamage");
        var deflect = rowProps.OfType<StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == DeflectStructName);
        info.DeflectDamage = GetFloat(deflect?.Value, "m_fDamage");
        info.DeflectStructureDamage = GetFloat(deflect?.Value, "m_fStructureDamage");
        info.KnockbackDistance = GetFloat(hitProps, "m_fKnockBackDistance");
        info.SuperDizzyDamage = GetFloat(hitProps, "m_fSuperDizzyDamage");
        info.StunFrames = GetByte(hitProps, "m_iNbFrameStuned");
        info.FreezeFrames = GetInt(hitProps, "m_iFreezeFrames");
        info.Parriable = GetBool(rowProps, "m_bParriable");
        info.Lethal = GetBool(rowProps, "m_bCanBeLethal");
        info.BreakStructure = GetBool(rowProps, "m_bCanBreakStructure");
        info.DizzyOnParry = GetBool(rowProps, "m_bDizzyOnParry");
        info.DizzyOnAvoid = GetBool(rowProps, "m_bDizzyOnAvoid");
        string? power = GetEnum(rowProps, "m_eAttackPower");
        if (power != null)
        {
            const string prefix = "EAttackPowers::";
            info.AttackPower = power.StartsWith(prefix, StringComparison.Ordinal) ? power[prefix.Length..] : power;
        }
        info.PropertyLevel = GetAttackPropertyLevel(rowProps);
    }

    /// <summary>
    /// Best-effort content root for resolving a table path found on a card: walk up from
    /// the card's directory looking for a Content root (a dir containing DB/_MainChar).
    /// The _MainChar marker is required because some archetype folders (Fajar) contain
    /// their own DB/ subfolder of ArchetypeDB blueprints which would be a false positive.
    /// Falls back to the empty string so callers can still try CustomAssets.
    /// </summary>
    private static string ResolveContentRootFromCardPath(string uassetPath)
    {
        var dir = Path.GetDirectoryName(uassetPath);
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            if (Directory.Exists(Path.Combine(dir, "DB", "_MainChar"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        // Card lives under <Content>/DB/... - strip at the first DB segment.
        var full = Path.GetFullPath(uassetPath);
        int db = full.IndexOf($"{Path.DirectorySeparatorChar}DB{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
        return db > 0 ? full.Substring(0, db) : "";
    }

    private static string TableRelFromObjectProperty(UAsset asset, ObjectPropertyData? dt)
    {
        if (dt?.Value == null) return "";
        if (dt.Value.Index >= 0) return "";
        int ii = -(dt.Value.Index + 1);
        if (ii < 0 || ii >= asset.Imports.Count) return "";
        var imp = asset.Imports[ii];
        if (imp.OuterIndex == null || imp.OuterIndex.Index >= 0) return "";
        int oi = -(imp.OuterIndex.Index + 1);
        if (oi < 0 || oi >= asset.Imports.Count) return "";
        string pkg = asset.Imports[oi].ObjectName?.Value?.ToString() ?? "";
        if (pkg.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            pkg = pkg.Substring(6);
        return pkg;
    }

    private static List<StructPropertyData>? FindHitWindows(UAsset asset)
    {
        var export = asset.Exports.OfType<NormalExport>().FirstOrDefault(e => e.Data != null);
        if (export?.Data == null) return null;
        var mAttack = export.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Attack");
        if (mAttack is not StructPropertyData attack || attack.Value == null) return null;
        var arr = attack.Value.OfType<ArrayPropertyData>()
            .FirstOrDefault(a => a.Name?.Value?.ToString() == "m_HitBoxes");
        if (arr?.Value == null) return null;
        return arr.Value.OfType<StructPropertyData>().ToList();
    }

    private static StructPropertyData? FindRow(UAsset asset, string rowName)
    {
        var dte = asset.Exports.OfType<DataTableExport>().FirstOrDefault();
        if (dte == null) return null;
        try
        {
            var row = dte[FName.FromString(asset, rowName)];
            if (row is StructPropertyData sp) return sp;
        }
        catch { }
        try
        {
            foreach (var prop in dte.Data)
            {
                if (prop is ArrayPropertyData arr && arr.Name?.Value?.ToString() == "RowMap" && arr.Value != null)
                {
                    foreach (var elem in arr.Value)
                    {
                        if (elem is StructPropertyData s && s.Name?.Value?.ToString() == rowName)
                            return s;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static bool TrySetFloat(List<PropertyData>? props, string name, float value, out string error)
    {
        error = "";
        var p = props?.OfType<FloatPropertyData>().FirstOrDefault(f => f.Name?.Value?.ToString() == name);
        if (p == null) { error = $"field '{name}' not present"; return false; }
        p.Value = value;
        return true;
    }

    private static bool TrySetInt(List<PropertyData>? props, string name, int value, out string error)
    {
        error = "";
        var p = props?.OfType<IntPropertyData>().FirstOrDefault(f => f.Name?.Value?.ToString() == name);
        if (p == null) { error = $"field '{name}' not present"; return false; }
        p.Value = value;
        return true;
    }

    private static bool TrySetByte(List<PropertyData>? props, string name, byte value, out string error)
    {
        error = "";
        var p = props?.OfType<BytePropertyData>().FirstOrDefault(f => f.Name?.Value?.ToString() == name);
        if (p == null) { error = $"field '{name}' not present"; return false; }
        p.Value = value;
        return true;
    }

    private static bool TrySetBool(List<PropertyData>? props, string name, bool value, out string error)
    {
        error = "";
        var p = props?.OfType<BoolPropertyData>().FirstOrDefault(f => f.Name?.Value?.ToString() == name);
        if (p == null) { error = $"field '{name}' not present"; return false; }
        p.Value = value;
        return true;
    }

    private static bool TrySetEnum(UAsset asset, List<PropertyData>? props, string name, string fullValue, out string error)
    {
        error = "";
        var p = props?.OfType<EnumPropertyData>().FirstOrDefault(f => f.Name?.Value?.ToString() == name);
        if (p == null) { error = $"field '{name}' not present"; return false; }
        p.Value = FName.FromString(asset, fullValue);
        return true;
    }

    /// <summary>Sets the first m_AttackPropertyLevel.m_Level (the only one vanilla varies).</summary>
    private static bool TrySetAttackPropertyLevel(List<PropertyData>? rowProps, uint level, out string error)
    {
        error = "";
        var apl = rowProps?.OfType<StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == "m_AttackPropertyLevel");
        var lvl = apl?.Value?.OfType<UInt32PropertyData>()
            .FirstOrDefault(u => u.Name?.Value?.ToString() == "m_Level");
        if (lvl == null) { error = "field 'm_AttackPropertyLevel.m_Level' not present"; return false; }
        lvl.Value = level;
        return true;
    }

    private static string? GetEnum(List<PropertyData>? props, string name)
        => props?.OfType<EnumPropertyData>().FirstOrDefault(f => f.Name?.Value?.ToString() == name)?.Value?.ToString();

    private static int? GetAttackPropertyLevel(List<PropertyData>? rowProps)
    {
        var apl = rowProps?.OfType<StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == "m_AttackPropertyLevel");
        var lvl = apl?.Value?.OfType<UInt32PropertyData>()
            .FirstOrDefault(u => u.Name?.Value?.ToString() == "m_Level");
        return lvl == null ? null : (int)lvl.Value;
    }

    private static float? GetFloat(List<PropertyData>? props, string name)
        => props?.OfType<FloatPropertyData>().FirstOrDefault(f => f.Name?.Value?.ToString() == name)?.Value;

    private static int? GetInt(List<PropertyData>? props, string name)
        => props?.OfType<IntPropertyData>().FirstOrDefault(f => f.Name?.Value?.ToString() == name)?.Value;

    private static byte? GetByte(List<PropertyData>? props, string name)
        => props?.OfType<BytePropertyData>().FirstOrDefault(f => f.Name?.Value?.ToString() == name)?.Value;

    private static bool? GetBool(List<PropertyData>? props, string name)
        => props?.OfType<BoolPropertyData>().FirstOrDefault(f => f.Name?.Value?.ToString() == name)?.Value;
}
