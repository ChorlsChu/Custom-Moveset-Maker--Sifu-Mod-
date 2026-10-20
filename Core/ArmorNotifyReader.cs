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

/// <summary>Which ResilienceOrderService an OrderServiceNotifyState points at.</summary>
public enum ArmorTier
{
    /// <summary>BP_ResilienceOrderService_C - light super armor (Grunt/BodyGuard/FlashKick heavies).</summary>
    Resilience,
    /// <summary>BP_SuperResilienceOrderService_C - strong armor (BigGuy/Fajar/FireDisciple; player Resilient skill).</summary>
    Super,
    /// <summary>BP_UnstoppableResilienceOrderService_C - full hyper armor (Kuroki/Sean/Yang; FalconPunch).</summary>
    Unstoppable,
}

/// <summary>One OrderServiceNotifyState entry on an attack anim (display-only; Phase 1).</summary>
public sealed class ArmorNotifyInfo
{
    public ArmorTier Tier;
    /// <summary>Start time, seconds: FAnimLinkableElement.LinkValue (in-sequence, SegmentBeginTime=0); null = absent.</summary>
    public float? StartTime;
    /// <summary>End time, seconds: EndLink.LinkValue; falls back to Start+Duration; null = absent.</summary>
    public float? EndTime;
    /// <summary>Stock FAnimNotifyEvent Duration, seconds; null = absent.</summary>
    public float? Duration;
}

/// <summary>
/// Read-only scanner for attacker-side super armor: the OrderServiceNotifyState entries
/// in an attack AnimSequence's Notifies[] array, whose m_ServiceClass points at one of
/// three ResilienceOrderService blueprints (tier). Independent of hit windows / AttackDB
/// cards - armor lives on the anim asset itself (VoiceArchetypeFixer owns the only write
/// precedent for Notifies[]). Phase 1: display only.
/// </summary>
public static class ArmorNotifyReader
{
    private static readonly EngineVersion Eng = EngineVersion.VER_UE4_26;

    private const string NotifyStateClass = "OrderServiceNotifyState";
    private const string SvcResilience = "BP_ResilienceOrderService_C";
    private const string SvcSuper = "BP_SuperResilienceOrderService_C";
    private const string SvcUnstoppable = "BP_UnstoppableResilienceOrderService_C";

    /// <summary>
    /// The anim file for a node's AnimPath ("Game/Animations/..."): CustomAssets copy wins,
    /// then the content dir (same resolution order the voice fixer uses for staged anims).
    /// Null when no file exists.
    /// </summary>
    public static string? ResolveAnimFile(string contentDir, string animPath)
    {
        string rel = VoiceArchetypeFixer.NormalizeRel(animPath);
        if (string.IsNullOrEmpty(rel)) return null;
        var custom = CustomAssets.FileForContentRel(rel + ".uasset");
        if (File.Exists(custom)) return custom;
        var vanilla = Path.Combine(contentDir,
            rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        return File.Exists(vanilla) ? vanilla : null;
    }

    /// <summary>
    /// Every OrderServiceNotifyState on the anim, in Notifies[] order.
    /// Null = the file failed to load; empty list = loaded, no armor (interruptible).
    /// Unknown m_ServiceClass targets are skipped, never guessed.
    /// </summary>
    public static List<ArmorNotifyInfo>? Read(string animUassetPath)
    {
        if (string.IsNullOrEmpty(animUassetPath) || !File.Exists(animUassetPath)) return null;
        UAsset asset;
        try { asset = new UAsset(animUassetPath, Eng, null, CustomSerializationFlags.None); }
        catch { return null; }

        var notifies = VoiceArchetypeFixer.FindNotifies(asset);
        if (notifies?.Value == null) return new List<ArmorNotifyInfo>();

        var result = new List<ArmorNotifyInfo>();
        foreach (var entry in notifies.Value)
        {
            if (entry is not StructPropertyData se || se.Value == null) continue;
            // State notifies (OrderServiceNotifyState included) keep their export ref in
            // NotifyStateClass; regular single notifies keep it in Notify (VoiceArchetypeFixer's
            // BP_Voice_Preview pattern). Both are 1-based positive export indices.
            var notify = se.Value.FirstOrDefault(p => p.Name?.Value?.ToString() == "Notify") as ObjectPropertyData;
            var stateClass = se.Value.FirstOrDefault(p => p.Name?.Value?.ToString() == "NotifyStateClass") as ObjectPropertyData;
            int expIdx = notify?.Value is { Index: > 0 } n
                ? n.Index - 1
                : stateClass?.Value is { Index: > 0 } s ? s.Index - 1 : -1;
            if (expIdx < 0 || expIdx >= asset.Exports.Count) continue;
            var exp = asset.Exports[expIdx];
            if (!IsOrderServiceNotifyExport(asset, exp)) continue;

            var tier = ResolveTier(asset, exp);
            if (tier == null) continue; // unknown service class - skip, don't guess

            result.Add(new ArmorNotifyInfo
            {
                Tier = tier.Value,
                StartTime = GetFloat(se.Value, "LinkValue") ?? GetFloat(se.Value, "TriggerTimeOffset"),
                EndTime = GetEndLink(se.Value) ?? Add(GetFloat(se.Value, "LinkValue"), GetFloat(se.Value, "Duration")),
                Duration = GetFloat(se.Value, "Duration"),
            });
        }
        return result;
    }

    /// <summary>Export is an OrderServiceNotifyState instance (class import name check).</summary>
    private static bool IsOrderServiceNotifyExport(UAsset asset, UAssetAPI.ExportTypes.Export exp)
    {
        int classIdx = exp.ClassIndex?.Index ?? 0;
        if (classIdx >= 0) return false;
        int i = -(classIdx + 1);
        return i >= 0 && i < asset.Imports.Count
            && asset.Imports[i].ObjectName?.Value?.ToString() == NotifyStateClass;
    }

    /// <summary>The notify's m_ServiceClass import name mapped to a tier; null = unknown class.</summary>
    private static ArmorTier? ResolveTier(UAsset asset, UAssetAPI.ExportTypes.Export exp)
    {
        if (exp is not NormalExport ne || ne.Data == null) return null;
        if (ne.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ServiceClass")
            is not ObjectPropertyData svc || svc.Value == null || svc.Value.Index >= 0) return null;
        int impIdx = -(svc.Value.Index + 1);
        if (impIdx < 0 || impIdx >= asset.Imports.Count) return null;
        return asset.Imports[impIdx].ObjectName?.Value?.ToString() switch
        {
            SvcResilience => ArmorTier.Resilience,
            SvcSuper => ArmorTier.Super,
            SvcUnstoppable => ArmorTier.Unstoppable,
            _ => null,
        };
    }

    private static float? GetFloat(List<PropertyData> props, string name)
        => props.FirstOrDefault(p => p.Name?.Value?.ToString() == name) is FloatPropertyData fp
            ? fp.Value : null;

    /// <summary>EndLink.LinkValue - the FAnimLinkableElement end time of the notify window.</summary>
    private static float? GetEndLink(List<PropertyData> props)
        => props.FirstOrDefault(p => p.Name?.Value?.ToString() == "EndLink") is StructPropertyData sp && sp.Value != null
            ? GetFloat(sp.Value, "LinkValue")
            : null;

    private static float? Add(float? a, float? b)
        => a != null && b != null ? a.Value + b.Value : null;
}
