using System;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using SifuMovesetEditor.Export;

namespace SifuMovesetEditor;

/// <summary>
/// Carries a card's aim data across assets. Aim lives in the hit windows: each
/// m_HitBoxes[i] entry carries m_eAimTarget (EIKAnimTargetTypes), and a throw notify
/// asks the attack for that target while it fires. A card with no m_HitBoxes has no aim
/// at all, so swapping a MainChar slot onto a hitless card (Kuroki dagger throws, the
/// bottle throw) makes the thrown weapon fly at the actor's default facing instead of at
/// an enemy - the montage's own pitch offset never gets a target direction to apply to.
/// <para/>
/// This copies the slot's original card windows (the donor) onto the replacement card
/// when the replacement has none, remapping every import the windows reference
/// (HitBoxData table, hit-detection objects) into the destination package.
/// </summary>
internal static class AimWindowCarrier
{
    private const string HitBoxesName = "m_HitBoxes";
    private const string AimTargetName = "m_eAimTarget";
    private const string AttackName = "m_Attack";

    /// <summary>The card's m_HitBoxes array, or null when the card has no such property.</summary>
    public static ArrayPropertyData? FindHitBoxes(UAsset asset)
    {
        var export = asset.Exports.OfType<NormalExport>().FirstOrDefault(e => e.Data != null);
        if (export?.Data == null) return null;
        var attack = export.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == AttackName);
        if (attack is not StructPropertyData atk || atk.Value == null) return null;
        return atk.Value.OfType<ArrayPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == HitBoxesName);
    }

    /// <summary>Number of hit windows on the card (0 when the property is missing/empty).</summary>
    public static int WindowCount(UAsset asset) => FindHitBoxes(asset)?.Value?.Length ?? 0;

    /// <summary>How many of those windows carry an aim target (0 means no aim data at all).</summary>
    public static int AimWindowCount(UAsset asset)
    {
        var boxes = FindHitBoxes(asset);
        if (boxes?.Value == null) return 0;
        int aimed = 0;
        foreach (var w in boxes.Value.OfType<StructPropertyData>())
        {
            if (w.Value == null) continue;
            var aim = w.Value.OfType<EnumPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == AimTargetName);
            string value = aim?.Value?.ToString() ?? "";
            if (value.Length > 0
                && !value.EndsWith("::None", StringComparison.OrdinalIgnoreCase))
                aimed++;
        }
        return aimed;
    }

    /// <summary>
    /// Appends a remapped copy of the donor's whole m_HitBoxes array to the target's
    /// m_Attack struct. Returns false (and leaves the target untouched) when either card
    /// lacks the shape this needs or any import cannot be re-created in the destination.
    /// </summary>
    public static bool Carry(UAsset target, UAsset donor, out string error)
    {
        error = "";
        var targetExport = target.Exports.OfType<NormalExport>().FirstOrDefault(e => e.Data != null);
        if (targetExport?.Data == null)
        {
            error = "destination card has no property data";
            return false;
        }
        var targetAttack = targetExport.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == AttackName)
            as StructPropertyData;
        if (targetAttack?.Value == null)
        {
            error = "destination card has no m_Attack struct";
            return false;
        }
        if (targetAttack.Value.OfType<ArrayPropertyData>()
            .Any(p => p.Name?.Value?.ToString() == HitBoxesName))
        {
            error = "destination card already has a m_HitBoxes property";
            return false;
        }

        var donorBoxes = FindHitBoxes(donor);
        if (donorBoxes?.Value == null || donorBoxes.Value.Length == 0)
        {
            error = "donor card has no hit windows";
            return false;
        }

        var ctx = new AssetCloner.Context(donor, target);
        if (AssetCloner.CloneValue(donorBoxes, ctx) is not ArrayPropertyData clone
            || clone.Value == null || clone.Value.Length == 0)
        {
            error = ctx.Failure ?? "window clone produced no entries";
            return false;
        }
        if (ctx.Failure != null)
        {
            error = ctx.Failure;
            return false;
        }

        targetAttack.Value.Add(clone);
        return true;
    }
}
