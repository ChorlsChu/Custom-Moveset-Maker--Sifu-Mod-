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

/// <summary>
/// Cross-family voice bleed fix. Attacks carry no voice links: the performer's Wwise voice
/// switch comes from its own ArchetypeDB (m_VoiceArchetypeSwitchName) plus any per-anim
/// BP_Voice_Preview notify, which force-flips the actor's switch when it fires. Exactly one
/// vanilla anim carries the override (BodyGuard_attack_barehands_disarm, Voice_Archetype =
/// "Bodyguard"), and once it fires the switch sticks for every later move - so a Grunt
/// given that disarm starts growling like a Bodyguard for the rest of the fight. When a
/// unit's exported tree references an anim with an override, stage a same-path copy with
/// the notify entry removed from Notifies[] (pak overlay; the vanilla card points at the
/// anim by path, so no card repoint is needed). Removing the entry is neutral for the
/// owning family - its archetype already sets the same switch - and for every other
/// performer the actor falls back to its own assigned voice, including MainChar (no
/// archetype voice of its own). CustomAssets copies are never touched: the user's file wins.
/// </summary>
public static class VoiceArchetypeFixer
{
    public const string VoiceNotifyObject = "BP_Voice_Preview_C";

    private static readonly EngineVersion Eng = EngineVersion.VER_UE4_26;

    /// <summary>One collected graph reference: an anim to check, or an attack card to expand to its anim.</summary>
    public readonly record struct VoiceRef(string? VariantTag, string ContentRel, bool IsCard);

    public enum Outcome { Patched, NoOverride, Failed }

    /// <summary>
    /// The unit's own Wwise voice switch, read from m_VoiceArchetypeSwitchName on its
    /// ArchetypeDB CDO (same export[1] the health reads use). Null for MainChar stances
    /// (no archetype) or DBs that don't carry the property.
    /// </summary>
    public static string? ReadVoiceSwitchName(string contentDir, string variantTag)
    {
        var canon = UnitPropertiesManager.CanonicalVariantTag(variantTag) ?? variantTag;
        var archRel = UnitPropertiesManager.ResolveArchetypePath(canon);
        if (string.IsNullOrEmpty(archRel)) return null;
        var path = Path.Combine(contentDir,
            archRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        if (!File.Exists(path)) return null;
        try
        {
            var asset = new UAsset(path, Eng, null, CustomSerializationFlags.None);
            if (asset.Exports.Count > 1 && asset.Exports[1] is NormalExport cdo && cdo.Data != null)
            {
                var v = cdo.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_VoiceArchetypeSwitchName");
                if (v is NamePropertyData np && np.Value != null) return np.Value.Value?.ToString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>The anim's BP_Voice_Preview override value (Voice_Archetype), or null when it has none.</summary>
    public static string? FindVoiceOverride(UAsset asset)
    {
        var notifies = FindNotifies(asset);
        if (notifies?.Value == null) return null;
        foreach (var entry in notifies.Value)
        {
            if (entry is not StructPropertyData se || se.Value == null) continue;
            var notify = se.Value.FirstOrDefault(p => p.Name?.Value?.ToString() == "Notify") as ObjectPropertyData;
            if (notify?.Value == null || notify.Value.Index <= 0) continue;
            int expIdx = notify.Value.Index - 1;
            if (expIdx < 0 || expIdx >= asset.Exports.Count) continue;
            var exp = asset.Exports[expIdx];
            if (!IsVoiceNotifyExport(asset, exp)) continue;
            if (exp is NormalExport ne && ne.Data != null)
            {
                var va = ne.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "Voice_Archetype");
                if (va is NamePropertyData np && np.Value != null) return np.Value.Value?.ToString();
            }
            return ""; // override present but its value unreadable - still a voice flip
        }
        return null;
    }

    /// <summary>
    /// Copies <paramref name="srcUasset"/> to <paramref name="dstUasset"/> with the
    /// BP_Voice_Preview entry removed from the Notifies[] array, then re-opens and
    /// verifies: entry gone, every other notify (AkEvents included) intact.
    /// </summary>
    public static Outcome NeutralizeCopy(string srcUasset, string dstUasset,
        out string? overrideValue, out string error)
    {
        overrideValue = null;
        error = "";
        UAsset asset;
        try { asset = new UAsset(srcUasset, Eng, null, CustomSerializationFlags.None); }
        catch (Exception ex) { error = "load failed: " + ex.Message; return Outcome.Failed; }

        var notifies = FindNotifies(asset);
        if (notifies?.Value == null) { error = "no Notifies array"; return Outcome.Failed; }
        int beforeCount = notifies.Value.Length;
        int voiceIdx = -1;
        for (int i = 0; i < notifies.Value.Length; i++)
        {
            if (notifies.Value[i] is not StructPropertyData se || se.Value == null) continue;
            var notify = se.Value.FirstOrDefault(p => p.Name?.Value?.ToString() == "Notify") as ObjectPropertyData;
            if (notify?.Value == null || notify.Value.Index <= 0) continue;
            int expIdx = notify.Value.Index - 1;
            if (expIdx < 0 || expIdx >= asset.Exports.Count) continue;
            if (!IsVoiceNotifyExport(asset, asset.Exports[expIdx])) continue;
            voiceIdx = i;
            if (asset.Exports[expIdx] is NormalExport ne && ne.Data != null)
            {
                var va = ne.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "Voice_Archetype");
                if (va is NamePropertyData np && np.Value != null) overrideValue = np.Value.Value?.ToString();
            }
            break;
        }
        if (voiceIdx < 0) return Outcome.NoOverride;

        var kept = new List<PropertyData>(beforeCount - 1);
        for (int i = 0; i < notifies.Value.Length; i++)
            if (i != voiceIdx) kept.Add(notifies.Value[i]);
        notifies.Value = kept.ToArray();

        int akBefore = CountAkEventExports(asset);

        Directory.CreateDirectory(Path.GetDirectoryName(dstUasset)!);
        var srcUexp = Path.ChangeExtension(srcUasset, ".uexp");
        var dstUexp = Path.ChangeExtension(dstUasset, ".uexp");
        try
        {
            if (File.Exists(srcUexp)) File.Copy(srcUexp, dstUexp, true);
            asset.Write(dstUasset);
        }
        catch (Exception ex) { error = "write failed: " + ex.Message; return Outcome.Failed; }

        UAsset post;
        try { post = new UAsset(dstUasset, Eng, null, CustomSerializationFlags.None); }
        catch (Exception ex) { error = "reopen failed: " + ex.Message; return Outcome.Failed; }
        var postNotifies = FindNotifies(post);
        if (postNotifies?.Value == null || postNotifies.Value.Length != beforeCount - 1)
        {
            error = $"verify failed: Notifies count {postNotifies?.Value?.Length} expected {beforeCount - 1}";
            return Outcome.Failed;
        }
        if (FindVoiceOverride(post) != null)
        { error = "verify failed: BP_Voice_Preview entry still present"; return Outcome.Failed; }
        if (CountAkEventExports(post) != akBefore)
        { error = "verify failed: AkEvent count changed"; return Outcome.Failed; }
        return Outcome.Patched;
    }

    /// <summary>
    /// Walks the collected refs, expands cards to their anim, and stages a neutralized
    /// same-path copy for every anim that carries a BP_Voice_Preview override. One copy
    /// per anim path even when several units reference it. Returns the content rels staged.
    /// </summary>
    public static List<string> ApplyVoiceFixes(string vanillaContentDir, string stagedContentDir,
        IReadOnlyCollection<VoiceRef> refs, Action<string> onLog)
    {
        var written = new List<string>();
        if (refs.Count == 0) return written;

        var animRefs = new Dictionary<string, HashSet<string?>>(StringComparer.OrdinalIgnoreCase);
        void AddRef(string animRel, string? variant)
        {
            if (string.IsNullOrEmpty(animRel)) return;
            if (!animRefs.TryGetValue(animRel, out var set))
            { set = new HashSet<string?>(); animRefs[animRel] = set; }
            set.Add(variant);
        }

        foreach (var r in refs)
        {
            var rel = NormalizeRel(r.ContentRel);
            if (string.IsNullOrEmpty(rel)) continue;
            if (r.IsCard)
            {
                var cardPath = ResolveCardSource(vanillaContentDir, rel);
                if (cardPath == null) continue;
                AddRef(AnimRelFromCard(cardPath), r.VariantTag);
            }
            else AddRef(rel, r.VariantTag);
        }

        foreach (var kv in animRefs)
        {
            string animRel = kv.Key;
            var custom = CustomAssets.FileForContentRel(animRel + ".uasset");
            if (File.Exists(custom))
            { onLog($"[VOICE] skipped {animRel}: CustomAssets copy present (user file wins)"); continue; }

            var vanilla = Path.Combine(vanillaContentDir,
                animRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!File.Exists(vanilla))
            { onLog($"[VOICE] skipped {animRel}: vanilla anim missing"); continue; }

            var staged = Path.Combine(stagedContentDir,
                animRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (File.Exists(staged))
            { onLog($"[VOICE] skipped {animRel}: already staged this export"); continue; }

            var outcome = NeutralizeCopy(vanilla, staged, out var ov, out string err);
            switch (outcome)
            {
                case Outcome.NoOverride:
                    onLog($"[VOICE] {animRel}: no BP_Voice_Preview override");
                    break;
                case Outcome.Failed:
                    try { if (File.Exists(staged)) File.Delete(staged); } catch { }
                    try
                    {
                        var uexp = Path.ChangeExtension(staged, ".uexp");
                        if (File.Exists(uexp)) File.Delete(uexp);
                    }
                    catch { }
                    onLog($"[VOICE] FAILED {animRel}: {err}");
                    break;
                case Outcome.Patched:
                    var units = string.Join(",", kv.Value.Where(v => !string.IsNullOrEmpty(v)));
                    onLog($"[VOICE] staged {animRel}: neutralized '{ov}' override (units: {units})");
                    written.Add(animRel);
                    break;
            }
        }
        return written;
    }

    /// <summary>
    /// The anim a staged attack card plays: m_Attack.m_Animation's import, resolved to its
    /// content rel via the import's outer package (/Game/Animations/...). Null when the
    /// card has no readable animation link.
    /// </summary>
    public static string? AnimRelFromCard(string cardUassetPath)
    {
        try
        {
            var asset = new UAsset(cardUassetPath, Eng, null, CustomSerializationFlags.None);
            foreach (var exp in asset.Exports)
            {
                if (exp is not NormalExport ne || ne.Data == null) continue;
                if (ne.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Attack")
                    is not StructPropertyData attack || attack.Value == null) continue;
                if (attack.Value.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Animation")
                    is not ObjectPropertyData anim || anim.Value == null || anim.Value.Index >= 0) continue;
                int impIdx = -(anim.Value.Index + 1);
                if (impIdx < 0 || impIdx >= asset.Imports.Count) continue;
                var imp = asset.Imports[impIdx];
                string objName = imp.ObjectName?.Value?.ToString() ?? "";
                if (string.IsNullOrEmpty(objName)) continue;
                int outerIdx = imp.OuterIndex != null ? -(imp.OuterIndex.Index + 1) : -1;
                if (outerIdx < 0 || outerIdx >= asset.Imports.Count) continue;
                string pkg = asset.Imports[outerIdx].ObjectName?.Value?.ToString() ?? "";
                if (!pkg.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) continue;
                return pkg.Substring("/Game/".Length);
            }
        }
        catch { }
        return null;
    }

    /// <summary>"Game/X" / "/Game/X.uasset" / "X\Y" -> "X/Y".</summary>
    public static string NormalizeRel(string rel)
    {
        var r = rel.Replace('\\', '/').Trim();
        if (r.StartsWith("/")) r = r.Substring(1);
        if (r.StartsWith("Game/", StringComparison.OrdinalIgnoreCase)) r = r.Substring(5);
        if (r.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) r = r[..^7];
        return r.TrimEnd('/');
    }

    private static string? ResolveCardSource(string contentDir, string contentRel)
    {
        var custom = CustomAssets.FileForContentRel(contentRel + ".uasset");
        if (File.Exists(custom)) return custom;
        var vanilla = Path.Combine(contentDir,
            contentRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        return File.Exists(vanilla) ? vanilla : null;
    }

    public static ArrayPropertyData? FindNotifies(UAsset asset)
    {
        foreach (var exp in asset.Exports)
        {
            if (exp is not NormalExport ne || ne.Data == null) continue;
            var arr = ne.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "Notifies") as ArrayPropertyData;
            if (arr?.Value != null && arr.Value.Length > 0) return arr;
        }
        return null;
    }

    private static bool IsVoiceNotifyExport(UAsset asset, UAssetAPI.ExportTypes.Export exp)
    {
        if (exp.ObjectName?.Value?.ToString() == VoiceNotifyObject) return true;
        int classIdx = exp.ClassIndex?.Index ?? 0;
        if (classIdx < 0)
        {
            int i = -(classIdx + 1);
            if (i >= 0 && i < asset.Imports.Count
                && asset.Imports[i].ObjectName?.Value?.ToString() == VoiceNotifyObject) return true;
        }
        return false;
    }

    private static int CountAkEventExports(UAsset asset)
    {
        int n = 0;
        foreach (var exp in asset.Exports)
        {
            int classIdx = exp.ClassIndex?.Index ?? 0;
            if (classIdx < 0)
            {
                int i = -(classIdx + 1);
                if (i >= 0 && i < asset.Imports.Count
                    && asset.Imports[i].ObjectName?.Value?.ToString() == "AnimNotify_AkEvent_C") n++;
            }
        }
        return n;
    }
}
