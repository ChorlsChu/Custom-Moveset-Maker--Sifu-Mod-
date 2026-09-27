using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace SifuMovesetEditor;

public class UnitProperties
{
    public float? Health { get; set; }
    public float? Structure { get; set; }
    public float? MemoryLimit { get; set; }
    public int? HitsCount { get; set; }
    public float? MemoryFlushLimit { get; set; }
    public bool? ImmuneToFocus { get; set; }
}

public static class UnitPropertiesManager
{
    private static readonly Dictionary<string, string> ArchetypePaths = new()
    {
        ["Grunt_Base"] = "DB/AI/Archetypes/Grunt/_Base/BP_Grunt_ArchetypeDB_Base",
        ["Grunt_Advanced"] = "DB/AI/Archetypes/Grunt/_MainGame/Generic/BP_Grunt_ArchetypeDB_Adv_Master",
        ["Grunt_Miniboss"] = "DB/AI/Archetypes/Grunt/_Miniboss/BP_Grunt_Miniboss_DB",
        ["Bodyguard_Base"] = "DB/AI/Archetypes/Bodyguard/_Base/Bodyguard_Base_ArchetypeDB",
        ["Bodyguard_Advanced"] = "DB/AI/Archetypes/Bodyguard/_MainGame/Generic/Bodyguard_ArchetypeDB_Adv_Master",
        ["Bodyguard_Miniboss"] = "DB/AI/Archetypes/Bodyguard/_Miniboss/BP_Bodyguard_Miniboss_ArchetypeDB",
        ["BigGuy_Base"] = "DB/AI/Archetypes/BigGuy/_MainGame/Generic/BP_BigGuy_ArchetypeDB_Base_Master",
        ["BigGuy_Advanced"] = "DB/AI/Archetypes/BigGuy/_MainGame/Generic/BP_BigGuy_ArchetypeDB_Adv_Master",
        ["BigGuy_Miniboss"] = "DB/AI/Archetypes/BigGuy/_MainGame/Generic/BP_BigGuy_ArchetypeDB_Miniboss_Master",
        ["FlashKick_Base"] = "DB/AI/Archetypes/FlashKick/_MainGame/Generic/BP_FlashKick_Base_ArchetypeDB",
        ["FlashKick_Advanced"] = "DB/AI/Archetypes/FlashKick/_MainGame/Generic/BP_FlashKick_ArchetypeDB_Adv_Master",
        ["FlashKick_Miniboss"] = "DB/AI/Archetypes/FlashKick/_MainGame/Generic/BP_FlashKick_ArchetypeDB_MiniBoss_Master",
        ["FD_Base"] = "DB/AI/Archetypes/FireDisciple/Variations/Archetype/BP_FireDisciple_ArchetypeDB_Base_Master",
        ["FD_Advanced"] = "DB/AI/Archetypes/FireDisciple/Variations/Archetype/BP_FireDisciple_ArchetypeDB_Adv_Master",
        ["FD_Miniboss"] = "DB/AI/Archetypes/FireDisciple/Variations/Archetype/BP_FireDisciple_ArchetypeDB_MiniBoss_Master",
        ["Fajar_P1"] = "DB/AI/Archetypes/Fajar/DB/BP_Fajar_Phase1_Offensive_ArchetypeDB",
        ["Fajar_P2"] = "DB/AI/Archetypes/Fajar/DB/BP_Fajar_Phase2_ArchetypeDB",
        ["Fengjie_P1"] = "DB/AI/Archetypes/Fengjie/Phase1/BP_Fengjie_Phase1_ArchetypeDB",
        ["Fengjie_P2"] = "DB/AI/Archetypes/Fengjie/Phase2/BP_Fengjie_Phase2_ArchetypeDB",
        ["Kuroki_P1"] = "DB/AI/Archetypes/Kuroki/BP_Kuroki_P1_ArchetypeDB_Melee",
        ["Kuroki_P2"] = "DB/AI/Archetypes/Kuroki/BP_Kuroki_P2_ArchetypeDB_ShiroizuMelee",
        ["Sean_P1"] = "DB/AI/Archetypes/Sean/BP_Sean_P1_ArchetypeDB",
        ["Sean_P2"] = "DB/AI/Archetypes/Sean/BP_Sean_P1_ArchetypeDB",
        ["Sean_Burst"] = "DB/AI/Archetypes/Sean/BP_Sean_P1_ArchetypeDB",
        ["Yang_P1"] = "DB/AI/Archetypes/Yang/_DB/Phase1/BP_Yang_P1_ArchetypeDB",
        ["Yang_P2"] = "DB/AI/Archetypes/Yang/_DB/Phase2/BP_Yang_P2_ArchetypeDB",
        ["Yang_P3"] = "DB/AI/Archetypes/Yang/_DB/Phase3/BP_Yang_P3_Wude_ArchetypeDB",
        ["Sifu"] = "DB/AI/Archetypes/Sifu/BP_Sifu_P1_ArchetypeDB",
        ["Servant"] = "DB/AI/Archetypes/Servant/BP_Servant_ArchetypeDB",
    };

    public static IReadOnlyCollection<string> AllVariantTags => ArchetypePaths.Keys;

    private static readonly Dictionary<string, string> ContextDefensePaths = new()
    {
        ["Grunt_Base"] = "DB/AI/Archetypes/Grunt/Defense/Grunt_Base_ContextualDefense",
        ["Grunt_Advanced"] = "DB/AI/Archetypes/Grunt/Defense/Grunt_Adv_ContextualDefense",
        ["Grunt_Miniboss"] = "DB/AI/Archetypes/Grunt/Defense/Grunt_Adv_ContextualDefense",
        ["Bodyguard_Base"] = "DB/AI/Archetypes/Bodyguard/_Base/Bodyguard_Base_ContextualDefense",
        ["Bodyguard_Advanced"] = "DB/AI/Archetypes/Bodyguard/_Advanced/Bodyguard_Advanced_ContextualDefense",
        ["Bodyguard_Miniboss"] = "DB/AI/Archetypes/Bodyguard/_Miniboss/Bodyguard_Miniboss_ContextualDefense",
        ["BigGuy_Base"] = "DB/AI/Archetypes/BigGuy/_MainGame/Generic/BigGuy_Base_ContextualDefense",
        ["BigGuy_Advanced"] = "DB/AI/Archetypes/BigGuy/_MainGame/Generic/BigGuy_Advanced_ContextualDefense",
        ["BigGuy_Miniboss"] = "DB/AI/Archetypes/BigGuy/_MainGame/Generic/BigGuy_MiniBoss_ContextualDefense",
        ["FlashKick_Base"] = "DB/AI/Archetypes/FlashKick/_MainGame/Generic/FlashKick_ContextualDefense",
        ["FlashKick_Advanced"] = "DB/AI/Archetypes/FlashKick/_MainGame/Generic/FlashKick_ContextualDefense",
        ["FlashKick_Miniboss"] = "DB/AI/Archetypes/FlashKick/_MainGame/Generic/FlashKick_ContextualDefense",
        ["FD_Base"] = "DB/AI/Archetypes/FireDisciple/Variations/Defense/FireDisciple_Base_ContextualDefense",
        ["FD_Advanced"] = "DB/AI/Archetypes/FireDisciple/Variations/Defense/FireDisciple_Adv_ContextualDefense",
        ["FD_Miniboss"] = "DB/AI/Archetypes/FireDisciple/Variations/Defense/FireDisciple_Adv_ContextualDefense",
        ["Fajar_P1"] = "DB/AI/Archetypes/Fajar/Defense/Fajar_ContextualDefense_Phase1_Offensive",
        ["Fajar_P2"] = "DB/AI/Archetypes/Fajar/Defense/Fajar_ContextualDefense_Phase2",
        ["Fengjie_P1"] = "DB/AI/Archetypes/Fengjie/Phase1/Fengjie_Phase1_ContextualDefense",
        ["Fengjie_P2"] = "DB/AI/Archetypes/Fengjie/Phase2/Fengjie_Phase2_ContextualDefense",
        ["Kuroki_P1"] = "DB/AI/Archetypes/Kuroki/Kuroki_ContextualDefense",
        ["Kuroki_P2"] = "DB/AI/Archetypes/Kuroki/Shiroizu_ContextualDefense",
        ["Sean_P1"] = "DB/AI/Archetypes/Sean/Defenses/Sean_ContextualDefense",
        ["Sean_P2"] = "DB/AI/Archetypes/Sean/Defenses/Sean_ContextualDefense",
        ["Sean_Burst"] = "DB/AI/Archetypes/Sean/Defenses/Sean_ContextualDefense",
        ["Yang_P1"] = "DB/AI/Archetypes/Yang/_DB/Phase1/Yang_P1_Offense_ContextualDefense",
        ["Yang_P2"] = "DB/AI/Archetypes/Yang/_DB/Phase2/Yang_P2_ContextualDefense",
        ["Yang_P3"] = "DB/AI/Archetypes/Yang/_DB/Phase3/Yang_P3_ContextualDefense",
        ["Sifu"] = "DB/AI/Archetypes/Sifu/Sifu_ContextualDefense",
        ["Servant"] = "DB/AI/Archetypes/Servant/Servant_ContextualDefense",
    };

    public static string? ResolveArchetypePath(string variantTag)
        => ResolveByLongestPrefix(ArchetypePaths, variantTag);

    public static string? ResolveContextDefensePath(string variantTag)
        => ResolveByLongestPrefix(ContextDefensePaths, variantTag);

    /// <summary>
    /// Exact match first, then progressively shorter '_'-prefixed keys, so weapon-suffixed tags
    /// (FD_Base_Staff, Grunt_Advanced_Bat) resolve to their unit instead of collapsing to "FD".
    /// </summary>
    private static string? ResolveByLongestPrefix(Dictionary<string, string> map, string variantTag)
    {
        if (string.IsNullOrEmpty(variantTag)) return null;
        if (map.TryGetValue(variantTag, out var exact)) return exact;

        var parts = variantTag.Split('_');
        for (int i = parts.Length - 1; i >= 1; i--)
        {
            var prefix = string.Join("_", parts, 0, i);
            if (map.TryGetValue(prefix, out var match)) return match;
        }
        return null;
    }

    private static string ResolveContentDir(string contentPath)
    {
        var leaf = Path.GetFileName(contentPath.TrimEnd('\\', '/'));
        return leaf.Equals("Content", StringComparison.OrdinalIgnoreCase)
            ? contentPath
            : Path.Combine(contentPath, "Content");
    }

    public static bool HasChanges(string contentPath, string variantTag, UnitProperties props)
    {
        var vanilla = Read(contentPath, variantTag);
        return (props.Health.HasValue && (!vanilla.Health.HasValue || Math.Abs(props.Health.Value - vanilla.Health.Value) > 0.01f))
            || (props.Structure.HasValue && (!vanilla.Structure.HasValue || Math.Abs(props.Structure.Value - vanilla.Structure.Value) > 0.01f))
            || (props.MemoryLimit.HasValue && (!vanilla.MemoryLimit.HasValue || Math.Abs(props.MemoryLimit.Value - vanilla.MemoryLimit.Value) > 0.01f))
            || (props.HitsCount.HasValue && (!vanilla.HitsCount.HasValue || props.HitsCount.Value != vanilla.HitsCount.Value))
            || (props.MemoryFlushLimit.HasValue && (!vanilla.MemoryFlushLimit.HasValue || Math.Abs(props.MemoryFlushLimit.Value - vanilla.MemoryFlushLimit.Value) > 0.01f))
            || FocusChanged(contentPath, variantTag, props);
    }

    public static bool HasWritableChanges(string contentPath, string variantTag, UnitProperties props)
    {
        var vanilla = Read(contentPath, variantTag);
        return (props.Health.HasValue && (!vanilla.Health.HasValue || Math.Abs(props.Health.Value - vanilla.Health.Value) > 0.01f))
            || (props.Structure.HasValue && (!vanilla.Structure.HasValue || Math.Abs(props.Structure.Value - vanilla.Structure.Value) > 0.01f))
            || (props.MemoryLimit.HasValue && (!vanilla.MemoryLimit.HasValue || Math.Abs(props.MemoryLimit.Value - vanilla.MemoryLimit.Value) > 0.01f))
            || (props.HitsCount.HasValue && (!vanilla.HitsCount.HasValue || props.HitsCount.Value != vanilla.HitsCount.Value))
            || (props.MemoryFlushLimit.HasValue && (!vanilla.MemoryFlushLimit.HasValue || Math.Abs(props.MemoryFlushLimit.Value - vanilla.MemoryFlushLimit.Value) > 0.01f))
            || FocusChanged(contentPath, variantTag, props);
    }

    /// <summary>
    /// True when the focus flag this export would actually write differs from what is on disk.
    /// The flag is family-wide, so it is evaluated from the pending/effective value rather than
    /// trusting a single unit's copy - <paramref name="props"/> only acts as a fallback for callers
    /// that carry an intent without having registered it as pending.
    /// </summary>
    private static bool FocusChanged(string contentPath, string variantTag, UnitProperties? props)
    {
        var disk = ReadFocusImmunity(contentPath, variantTag);
        bool now = GetEffectiveFocus(contentPath, variantTag) || (props?.ImmuneToFocus ?? false);
        return now != disk;
    }

    public static UnitProperties Read(string contentPath, string variantTag)
    {
        var props = new UnitProperties();
        var contentDir = ResolveContentDir(contentPath);

        var archPath = ResolveArchetypePath(variantTag);
        if (archPath != null)
        {
            var fullPath = Path.Combine(contentDir, archPath + ".uasset");
            if (File.Exists(fullPath))
            {
                try
                {
                    var asset = new UAsset(fullPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
                    if (asset.Exports.Count > 1 && asset.Exports[1] is NormalExport ne)
                    {
                        props.Health = FindFloatProperty(ne, "m_fHealth");
                        props.Structure = FindFloatProperty(ne, "m_fStructure");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[UnitProps] ArchetypeDB read error for {variantTag}: {ex.Message}");
                }
            }
        }

        var defPath = ResolveContextDefensePath(variantTag);
        if (defPath != null)
        {
            var uassetPath = Path.Combine(contentDir, defPath + ".uasset");
            if (File.Exists(uassetPath))
            {
                try
                {
                    var asset = new UAsset(uassetPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
                    foreach (var exp in asset.Exports)
                    {
                        if (exp is not NormalExport ne) continue;

                        if (!props.MemoryLimit.HasValue)
                        {
                            var ml = ne.Data.OfType<FloatPropertyData>()
                                .FirstOrDefault(p => p.Name.Value.ToString() == "m_fMemoryLimit");
                            if (ml != null) props.MemoryLimit = ml.Value;
                        }

                        if (!props.HitsCount.HasValue)
                        {
                            var hc = ne.Data.OfType<BytePropertyData>()
                                .FirstOrDefault(p => p.Name.Value.ToString() == "m_uiHitsCount");
                            if (hc != null && int.TryParse(hc.Value.ToString(), out int parsed))
                                props.HitsCount = parsed;
                        }

                        if (!props.MemoryFlushLimit.HasValue)
                        {
                            var fl = ne.Data.OfType<FloatPropertyData>()
                                .FirstOrDefault(p => p.Name.Value.ToString() == "m_fMemoryFlushLimit");
                            if (fl != null) props.MemoryFlushLimit = fl.Value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[UnitProps] ContextDefense read error for {variantTag}: {ex.Message}");
                }
            }
        }

        props.ImmuneToFocus = ReadFocusImmunity(contentPath, variantTag);

        return props;
    }

    private const string RootArchetypeRelPath = "DB/AI/_Shared/BP_Base_ArchetypeDB";
    private const string FocusImmuneProp = "m_VitalPointDB";
    private const string FocusImmunePackage = "/Game/DB/AI/Archetypes/Yang/_DB/Generic/Yang_VitalPointDefinition";
    private const string FocusImmuneObject = "Yang_VitalPointDefinition";
    private static readonly Dictionary<string, bool> FocusImmunityCache = new();

    public static bool ReadFocusImmunity(string contentPath, string variantTag)
    {
        var key = contentPath + "|" + variantTag;
        if (FocusImmunityCache.TryGetValue(key, out var cached)) return cached;

        bool result = false;
        try
        {
            var archRel = ResolveArchetypePath(variantTag);
            if (!string.IsNullOrEmpty(archRel))
            {
                var contentDir = ResolveContentDir(contentPath);
                var vpPath = ResolveVitalPointDbAssetPath(contentDir, archRel!);
                if (vpPath != null && File.Exists(vpPath) && TryReadVitalPointArray(vpPath, out var isEmpty))
                    result = isEmpty;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FocusImmunity] error for {variantTag}: {ex.Message}");
        }

        FocusImmunityCache[key] = result;
        return result;
    }

    /// <summary>
    /// Reads m_VitalPointDefinitionArray straight off a VitalPointDB asset. An empty (or absent)
    /// array is what makes a unit immune to focus attacks.
    /// </summary>
    private static bool TryReadVitalPointArray(string vpUassetPath, out bool isEmpty)
    {
        isEmpty = false;
        try
        {
            var asset = new UAsset(vpUassetPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            if (asset.Exports.Count == 0 || asset.Exports[0] is not NormalExport ne) return false;

            var arr = ne.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name.Value?.ToString() == "m_VitalPointDefinitionArray");
            isEmpty = arr?.Value == null || arr.Value.Length == 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Focus flag as it exists in a shipped ArchetypeDB asset, resolving m_VitalPointDB against
    /// <paramref name="vanillaContentDir"/> - the VitalPointDB target itself is almost never
    /// carried by a pak, so the import tree alone cannot answer this. Null when the asset
    /// serializes no such link or the target cannot be read.
    /// </summary>
    public static bool? ReadFocusImmunityFromArchetypeAsset(string archUassetPath, string vanillaContentDir)
    {
        try
        {
            if (string.IsNullOrEmpty(archUassetPath) || !File.Exists(archUassetPath)) return null;

            var asset = new UAsset(archUassetPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);

            string? rel = null;
            foreach (var exp in asset.Exports)
            {
                if (exp is not NormalExport ne) continue;
                var vp = ne.Data.OfType<ObjectPropertyData>()
                    .FirstOrDefault(p => p.Name.Value?.ToString() == FocusImmuneProp);
                if (vp?.Value == null || vp.Value.Index >= 0) continue;

                rel = ImportPackagePath(asset, -(vp.Value.Index + 1));
                if (rel != null) break;
            }
            if (rel == null) return null;

            var vpPath = Path.Combine(ResolveContentDir(vanillaContentDir),
                rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!File.Exists(vpPath)) return null;

            return TryReadVitalPointArray(vpPath, out var isEmpty) ? isEmpty : (bool?)null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FocusImmunity] archetype read error for {archUassetPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Every ArchetypeDB rel a focus flag can be written to: one per owner family plus the shared
    /// root that covers Grunt / Bodyguard / FlashKick / Fajar / Sifu / Servant.
    /// </summary>
    public static IReadOnlyCollection<string> AllArchetypeRelPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in ArchetypePaths.Values)
            if (!string.IsNullOrEmpty(rel))
                seen.Add(rel);
        seen.Add(RootArchetypeRelPath);
        return seen;
    }

    private static readonly Dictionary<string, Dictionary<string, string?>> FocusFamilyCache = new();
    private static readonly Dictionary<string, Dictionary<string, List<string>>> FocusFamilyMembersCache = new();
    private static readonly Dictionary<string, Dictionary<string, bool>> FocusPending = new();

    /// <summary>
    /// The archetype file whose m_VitalPointDB governs this unit's focus target, falling back to the
    /// shared root. Every variant that resolves to the same file forms one family and shares one flag.
    /// </summary>
    public static string? GetFocusFamilyKey(string contentPath, string variantTag)
        => FamilyKeyFor(ResolveContentDir(contentPath), variantTag);

    private static string? FamilyKeyFor(string contentDir, string variantTag)
    {
        if (string.IsNullOrEmpty(variantTag)) return null;

        if (!FocusFamilyCache.TryGetValue(contentDir, out var map))
            FocusFamilyCache[contentDir] = map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!map.TryGetValue(variantTag, out var family))
        {
            var archRel = ResolveArchetypePath(variantTag);
            family = string.IsNullOrEmpty(archRel)
                ? null
                : WalkVitalPointOwner(contentDir, archRel!).ownerRel ?? RootArchetypeRelPath;
            map[variantTag] = family;
        }
        return family;
    }

    /// <summary>Every variant tag sharing this unit's focus family, including the unit itself.</summary>
    public static IReadOnlyList<string> GetFocusFamilyMembers(string contentPath, string variantTag)
    {
        var contentDir = ResolveContentDir(contentPath);
        var family = FamilyKeyFor(contentDir, variantTag);
        if (family == null) return Array.Empty<string>();

        if (!FocusFamilyMembersCache.TryGetValue(contentDir, out var map))
        {
            map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in ArchetypePaths.Keys)
            {
                var f = FamilyKeyFor(contentDir, tag);
                if (f == null) continue;
                if (!map.TryGetValue(f, out var list)) map[f] = list = new List<string>();
                list.Add(tag);
            }
            FocusFamilyMembersCache[contentDir] = map;
        }

        var members = map.TryGetValue(family, out var found) ? new List<string>(found) : new List<string>();
        bool hasSelf = false;
        foreach (var m in members)
            if (m.Equals(variantTag, StringComparison.OrdinalIgnoreCase)) { hasSelf = true; break; }
        if (!hasSelf) members.Add(variantTag);
        return members;
    }

    /// <summary>
    /// Pending family flag if one was set this session, otherwise what is on disk. This - not
    /// <see cref="UnitProperties.ImmuneToFocus"/> - is the value the UI shows and the export writes.
    /// </summary>
    public static bool GetEffectiveFocus(string contentPath, string variantTag)
    {
        var contentDir = ResolveContentDir(contentPath);
        var family = FamilyKeyFor(contentDir, variantTag);
        if (family != null
            && FocusPending.TryGetValue(contentDir, out var pending)
            && pending.TryGetValue(family, out var value))
            return value;

        return ReadFocusImmunity(contentPath, variantTag);
    }

    /// <summary>
    /// Sets the flag for the whole family, so toggling it on any variant flips every member.
    /// </summary>
    public static void SetPendingFocus(string contentPath, string variantTag, bool immune)
    {
        var contentDir = ResolveContentDir(contentPath);
        var family = FamilyKeyFor(contentDir, variantTag);
        if (family == null) return;

        if (!FocusPending.TryGetValue(contentDir, out var pending))
            FocusPending[contentDir] = pending = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        pending[family] = immune;
    }

    /// <summary>
    /// Drops every pending family flag for this content tree - used when the session returns to vanilla,
    /// since pending flags are session state that was never written to disk.
    /// </summary>
    public static void ClearPendingFocus(string? contentPath)
    {
        if (string.IsNullOrEmpty(contentPath)) return;
        FocusPending.Remove(ResolveContentDir(contentPath));
    }

    private static void InvalidateFocusReadCache(string contentPath)
    {
        var prefix = contentPath + "|";
        foreach (var key in FocusImmunityCache.Keys
                     .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            FocusImmunityCache.Remove(key);
    }

    /// <summary>
    /// Repoints an archetype's m_VitalPointDB at Yang's vanilla VitalPointDB (0 focus points)
    /// so the archetype has no focus target. Returns true only when the asset was rewritten;
    /// un-checking is a no-op because exporting vanilla == leaving the link alone.
    /// </summary>
    public static bool ApplyFocusImmunity(string contentPath, string variantTag, bool immune)
        => ApplyFocusImmunityRel(contentPath, ResolveArchetypePath(variantTag), immune);

    /// <summary>
    /// Same as <see cref="ApplyFocusImmunity"/> but aimed at an explicit archetype file - the
    /// family root, so every member inherits the repointed link from one write.
    /// </summary>
    public static bool ApplyFocusImmunityRel(string contentPath, string? archRel, bool immune)
    {
        if (!immune || string.IsNullOrEmpty(archRel)) return false;

        var contentDir = ResolveContentDir(contentPath);

        var uassetPath = Path.Combine(contentDir,
            archRel!.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        if (!File.Exists(uassetPath)) return false;

        try
        {
            var asset = new UAsset(uassetPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            var ne = asset.Exports.Count > 1 && asset.Exports[1] is NormalExport direct
                ? direct
                : asset.Exports.OfType<NormalExport>().FirstOrDefault(e => e.Data.Count > 0);
            if (ne == null) return false;

            var vp = ne.Data.OfType<ObjectPropertyData>()
                .FirstOrDefault(p => p.Name.Value?.ToString() == FocusImmuneProp);

            if (vp != null)
            {
                if (vp.Value == null || vp.Value.Index >= 0) return false;

                int objIdx = -(vp.Value.Index + 1);
                if (objIdx < 0 || objIdx >= asset.Imports.Count) return false;

                int outerRaw = asset.Imports[objIdx].OuterIndex.Index;
                if (outerRaw >= 0) return false;
                int pkgIdx = -(outerRaw + 1);
                if (pkgIdx < 0 || pkgIdx >= asset.Imports.Count) return false;

                var objImp = asset.Imports[objIdx];
                var pkgImp = asset.Imports[pkgIdx];

                bool alreadyThere =
                    pkgImp.ObjectName?.Value?.ToString() == FocusImmunePackage &&
                    objImp.ObjectName?.Value?.ToString() == FocusImmuneObject;
                if (alreadyThere) return false;

                pkgImp.ObjectName = FName.FromString(asset, FocusImmunePackage);
                objImp.ObjectName = FName.FromString(asset, FocusImmuneObject);

                asset.Write(uassetPath);
                InvalidateFocusReadCache(contentPath);
                return true;
            }

            int newPkgIdx = asset.Imports.Count;
            asset.Imports.Add(new UAssetAPI.Import
            {
                ClassPackage = FName.FromString(asset, "/Script/CoreUObject"),
                ClassName = FName.FromString(asset, "Package"),
                ObjectName = FName.FromString(asset, FocusImmunePackage),
                OuterIndex = new FPackageIndex(0),
                PackageName = FName.FromString(asset, "None")
            });

            int newObjIdx = asset.Imports.Count;
            asset.Imports.Add(new UAssetAPI.Import
            {
                ClassPackage = FName.FromString(asset, "/Script/Sifu"),
                ClassName = FName.FromString(asset, "VitalPointDB"),
                ObjectName = FName.FromString(asset, FocusImmuneObject),
                OuterIndex = FPackageIndex.FromImport(newPkgIdx),
                PackageName = FName.FromString(asset, "None")
            });

            ne.Data.Insert(InsertFocusAnchor(ne), new ObjectPropertyData
            {
                Name = new FName(asset, FocusImmuneProp),
                Value = FPackageIndex.FromImport(newObjIdx)
            });

            asset.Write(uassetPath);
            InvalidateFocusReadCache(contentPath);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FocusImmunity] write error for {archRel}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The export stages a unit's own ArchetypeDB before patching it, but the family's focus file is
    /// frequently a *different* asset - DB/AI/_Shared/BP_Base_ArchetypeDB for the thirteen shared-root
    /// units, for one. Copy it into the staging tree first so ApplyFocusImmunityRel finds it instead
    /// of bailing on File.Exists and dropping the flag silently.
    /// </summary>
    public static bool EnsureFocusFileStaged(string vanillaContentDir, string stagedContentDir, string archRel)
    {
        if (string.IsNullOrEmpty(archRel)) return false;

        var rel = archRel.Replace('/', Path.DirectorySeparatorChar);
        var src = Path.Combine(ResolveContentDir(vanillaContentDir), rel + ".uasset");
        if (!File.Exists(src)) return false;

        var dst = Path.Combine(ResolveContentDir(stagedContentDir), rel + ".uasset");
        var srcUexp = Path.ChangeExtension(src, ".uexp");
        var dstUexp = Path.ChangeExtension(dst, ".uexp");

        try
        {
            if (File.Exists(dst))
            {
                // A staged .uasset without its .uexp cannot be reloaded, which would make the
                // repoint below fail for a reason that has nothing to do with the flag.
                if (!File.Exists(dstUexp) && File.Exists(srcUexp))
                    File.Copy(srcUexp, dstUexp, true);
                return true;
            }

            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.Copy(src, dst, true);
            if (File.Exists(srcUexp))
                File.Copy(srcUexp, dstUexp, true);

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FocusImmunity] stage error for {archRel}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Final focus pass. Repoints each distinct focus family owner exactly once, *after* every
    /// unit's own ArchetypeDB has been staged - a per-unit patch cannot do this, because a later
    /// unit's "copy vanilla over the staged copy" would erase a repoint its sibling already wrote
    /// while the dedupe set silently prevented it from being re-applied.
    /// </summary>
    /// <returns>The staged archetype rel-paths that were rewritten (only true writes, so an
    /// already-correct file is never re-added to the pak).</returns>
    public static List<string> ApplyFocusFamilies(
        string vanillaContentDir,
        string stagedContentDir,
        IReadOnlyDictionary<string, UnitProperties>? propsByVariant,
        Action<string>? onLog = null)
    {
        var written = new List<string>();
        if (propsByVariant == null || propsByVariant.Count == 0) return written;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in propsByVariant)
        {
            var variantTag = kvp.Key;
            if (string.IsNullOrEmpty(variantTag)) continue;

            var focusRel = GetFocusFamilyKey(vanillaContentDir, variantTag);
            if (string.IsNullOrEmpty(focusRel)) continue;

            var wanted = kvp.Value?.ImmuneToFocus ?? false;
            var effective = GetEffectiveFocus(vanillaContentDir, variantTag);
            if (!wanted && !effective) continue;

            // Mark the family attempted only once we know we are going to write it, so an
            // un-flagged member cannot swallow a flagged sibling that shares the same owner.
            if (!seen.Add(focusRel!)) continue;

            if (!EnsureFocusFileStaged(vanillaContentDir, stagedContentDir, focusRel!))
            {
                onLog?.Invoke($"[FOCUS] Could not stage {focusRel} (family of {variantTag}) - vanilla file missing under {vanillaContentDir}; flag not written");
                continue;
            }

            if (ApplyFocusImmunityRel(stagedContentDir, focusRel, true))
            {
                written.Add(focusRel!);
                onLog?.Invoke($"[FOCUS] Repointed VitalPointDB for {focusRel} (family of {variantTag})");
            }
            else
            {
                onLog?.Invoke($"[FOCUS] Could not repoint VitalPointDB for {focusRel} (family of {variantTag}) - staged file is present but the write was rejected (already patched, or the asset serializes no m_VitalPointDB)");
            }
        }

        return written;
    }

    private static int InsertFocusAnchor(NormalExport ne)
    {
        for (int i = 0; i < ne.Data.Count; i++)
            if (ne.Data[i].Name.Value?.ToString() == "m_iCirclePerCombatRoles")
                return i;

        int lastBarks = -1;
        for (int i = 0; i < ne.Data.Count; i++)
        {
            var n = ne.Data[i].Name.Value?.ToString();
            if (n == "m_BarksDBArena" || n == "m_BarksDb") lastBarks = i;
        }
        return lastBarks >= 0 ? lastBarks + 1 : ne.Data.Count;
    }

    /// <summary>
    /// Walks the archetype parent chain and reports the first file that actually serializes
    /// m_VitalPointDB (the owner), plus the VitalPointDB asset it points at.
    /// </summary>
    private static (string? ownerRel, string? vpAssetPath) WalkVitalPointOwner(string contentDir, string archRel)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = archRel.Replace('\\', '/');
        int guard = 0;

        while (!string.IsNullOrEmpty(current) && guard++ < 16 && seen.Add(current))
        {
            var uasset = Path.Combine(contentDir,
                current.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!File.Exists(uasset)) break;

            UAsset asset;
            try
            {
                asset = new UAsset(uasset, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            }
            catch
            {
                break;
            }

            foreach (var exp in asset.Exports)
            {
                if (exp is not NormalExport ne) continue;
                var vp = ne.Data.OfType<ObjectPropertyData>()
                    .FirstOrDefault(p => p.Name.Value?.ToString() == FocusImmuneProp);
                if (vp?.Value != null && vp.Value.Index < 0)
                {
                    var rel = ImportPackagePath(asset, -(vp.Value.Index + 1));
                    if (rel != null)
                        return (current, Path.Combine(contentDir,
                            rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset"));
                }
            }

            current = FindArchetypeParent(asset);
        }

        return (null, null);
    }

    private static string? ResolveVitalPointDbAssetPath(string contentDir, string archRel)
    {
        var vpPath = WalkVitalPointOwner(contentDir, archRel).vpAssetPath;
        return vpPath ?? Path.Combine(contentDir,
            RootArchetypeRelPath.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
    }

    private static string? FindArchetypeParent(UAsset asset)
    {
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var imp = asset.Imports[i];
            string cls = imp.ClassName?.Value?.ToString() ?? "";
            if (!cls.Equals("BlueprintGeneratedClass", StringComparison.Ordinal)) continue;

            string name = imp.ObjectName?.Value?.ToString() ?? "";
            if (name.StartsWith("Default__", StringComparison.Ordinal)) continue;
            if (name.IndexOf("ArchetypeDB", StringComparison.OrdinalIgnoreCase) < 0) continue;

            string? pkg = ImportPackagePath(asset, i);
            if (pkg != null) return pkg;
        }
        return null;
    }

    private static string? ImportPackagePath(UAsset asset, int importIndex)
    {
        if (importIndex < 0 || importIndex >= asset.Imports.Count) return null;

        var imp = asset.Imports[importIndex];
        int outerRaw = imp.OuterIndex.Index;
        if (outerRaw >= 0) return null;

        int outerIdx = -(outerRaw + 1);
        if (outerIdx < 0 || outerIdx >= asset.Imports.Count) return null;

        var outer = asset.Imports[outerIdx];
        string outerCls = outer.ClassName?.Value?.ToString() ?? "";
        if (!outerCls.Equals("Package", StringComparison.Ordinal)) return null;

        string pkg = outer.ObjectName?.Value?.ToString() ?? "";
        if (!pkg.StartsWith("/Game/", StringComparison.Ordinal)) return null;
        return pkg.Substring(6);
    }

    private static readonly Dictionary<string, Dictionary<string, List<string>>> InheritanceCache = new();

    /// <summary>
    /// Other variant tags whose archetype chain passes through this variant's archetype file,
    /// so re-pointing <paramref name="variantTag"/> also immunises them. Path-equality counts,
    /// which is how Sean_P2 / Sean_Burst (same file as Sean_P1) are picked up.
    /// </summary>
    public static IReadOnlyList<string> GetInheritingVariants(string contentPath, string variantTag)
    {
        var contentDir = ResolveContentDir(contentPath);
        if (!InheritanceCache.TryGetValue(contentDir, out var map))
        {
            map = BuildInheritanceMap(contentDir);
            InheritanceCache[contentDir] = map;
        }
        return map.TryGetValue(variantTag, out var list) ? list : Array.Empty<string>();
    }

    private static Dictionary<string, List<string>> BuildInheritanceMap(string contentDir)
    {
        var tags = ArchetypePaths.Keys.ToList();
        var chains = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
            chains[tag] = ArchetypeChain(contentDir, ArchetypePaths[tag]);

        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in tags)
        {
            var affected = new List<string>();
            foreach (var other in tags)
            {
                if (string.Equals(owner, other, StringComparison.OrdinalIgnoreCase)) continue;
                if (chains[other].Contains(ArchetypePaths[owner]))
                    affected.Add(other);
            }
            map[owner] = affected;
        }
        return map;
    }

    private static HashSet<string> ArchetypeChain(string contentDir, string startRel)
    {
        var chain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = startRel.Replace('\\', '/');
        int guard = 0;

        while (!string.IsNullOrEmpty(current) && guard++ < 16 && seen.Add(current))
        {
            chain.Add(current);

            var uasset = Path.Combine(contentDir,
                current.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!File.Exists(uasset)) break;

            UAsset asset;
            try
            {
                asset = new UAsset(uasset, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            }
            catch
            {
                break;
            }

            current = FindArchetypeParent(asset);
        }
        return chain;
    }

    public static UnitProperties ReadArchetypeAsset(string uassetPath)
    {
        var props = new UnitProperties();
        try
        {
            var asset = new UAsset(uassetPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            if (asset.Exports.Count > 1 && asset.Exports[1] is NormalExport ne)
            {
                props.Health = FindFloatProperty(ne, "m_fHealth");
                props.Structure = FindFloatProperty(ne, "m_fStructure");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UnitProps] ReadArchetypeAsset error for {uassetPath}: {ex.Message}");
        }
        return props;
    }

    public static UnitProperties ReadDefenseAsset(string uassetPath)
    {
        var props = new UnitProperties();
        try
        {
            var asset = new UAsset(uassetPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            foreach (var exp in asset.Exports)
            {
                if (exp is not NormalExport ne) continue;

                if (!props.MemoryLimit.HasValue)
                {
                    var ml = ne.Data.OfType<FloatPropertyData>()
                        .FirstOrDefault(p => p.Name.Value.ToString() == "m_fMemoryLimit");
                    if (ml != null) props.MemoryLimit = ml.Value;
                }

                if (!props.HitsCount.HasValue)
                {
                    var hc = ne.Data.OfType<BytePropertyData>()
                        .FirstOrDefault(p => p.Name.Value.ToString() == "m_uiHitsCount");
                    if (hc != null && int.TryParse(hc.Value.ToString(), out int parsed))
                        props.HitsCount = parsed;
                }

                if (!props.MemoryFlushLimit.HasValue)
                {
                    var fl = ne.Data.OfType<FloatPropertyData>()
                        .FirstOrDefault(p => p.Name.Value.ToString() == "m_fMemoryFlushLimit");
                    if (fl != null) props.MemoryFlushLimit = fl.Value;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UnitProps] ReadDefenseAsset error for {uassetPath}: {ex.Message}");
        }
        return props;
    }

    public static void MergeInto(UnitProperties target, UnitProperties source)
    {
        if (source.Health.HasValue) target.Health = source.Health;
        if (source.Structure.HasValue) target.Structure = source.Structure;
        if (source.MemoryLimit.HasValue) target.MemoryLimit = source.MemoryLimit;
        if (source.HitsCount.HasValue) target.HitsCount = source.HitsCount;
        if (source.MemoryFlushLimit.HasValue) target.MemoryFlushLimit = source.MemoryFlushLimit;
        if (source.ImmuneToFocus.HasValue) target.ImmuneToFocus = source.ImmuneToFocus;
    }

    public static bool HasAnyValue(UnitProperties props) =>
        props.Health.HasValue || props.Structure.HasValue || props.MemoryLimit.HasValue
        || props.HitsCount.HasValue || props.MemoryFlushLimit.HasValue
        || props.ImmuneToFocus.HasValue;

    public static void Write(string contentPath, string variantTag, UnitProperties props)
    {
        var contentDir = ResolveContentDir(contentPath);

        var archPath = ResolveArchetypePath(variantTag);
        if (archPath != null && (props.Health.HasValue || props.Structure.HasValue))
        {
            var fullPath = Path.Combine(contentDir, archPath + ".uasset");
            if (File.Exists(fullPath))
            {
                try
                {
                    var asset = new UAsset(fullPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
                    if (asset.Exports.Count > 1 && asset.Exports[1] is NormalExport ne)
                    {
                        if (props.Health.HasValue)
                            SetFloatProperty(ne, "m_fHealth", props.Health.Value);
                        if (props.Structure.HasValue)
                            SetFloatProperty(ne, "m_fStructure", props.Structure.Value);
                        asset.Write(fullPath);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[UnitProps] ArchetypeDB write error for {variantTag}: {ex.Message}");
                }
            }
        }

        var defPath = ResolveContextDefensePath(variantTag);
        if (defPath != null)
        {
            var uassetPath = Path.Combine(contentDir, defPath + ".uasset");
            if (File.Exists(uassetPath))
            {
                try
                {
                    var asset = new UAsset(uassetPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
                    bool modified = false;

                    foreach (var exp in asset.Exports)
                    {
                        if (exp is not NormalExport ne) continue;

                        if (props.MemoryLimit.HasValue)
                        {
                            var ml = ne.Data.OfType<FloatPropertyData>()
                                .FirstOrDefault(p => p.Name.Value.ToString() == "m_fMemoryLimit");
                            if (ml != null) { ml.Value = props.MemoryLimit.Value; modified = true; }
                        }

                        if (props.HitsCount.HasValue)
                        {
                            var hc = ne.Data.OfType<BytePropertyData>()
                                .FirstOrDefault(p => p.Name.Value.ToString() == "m_uiHitsCount");
                            if (hc != null) { hc.Value = (byte)props.HitsCount.Value; modified = true; }
                        }

                        if (props.MemoryFlushLimit.HasValue)
                        {
                            var fl = ne.Data.OfType<FloatPropertyData>()
                                .FirstOrDefault(p => p.Name.Value.ToString() == "m_fMemoryFlushLimit");
                            if (fl != null) { fl.Value = props.MemoryFlushLimit.Value; modified = true; }
                        }
                    }

                    if (modified)
                        asset.Write(uassetPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[UnitProps] ContextDefense write error for {variantTag}: {ex.Message}");
                }
            }
        }
    }

    private static float? FindFloatProperty(NormalExport ne, string propName)
    {
        var prop = ne.Data.OfType<FloatPropertyData>()
            .FirstOrDefault(p => p.Name.Value.ToString() == propName);
        return prop?.Value;
    }

    private static void SetFloatProperty(NormalExport ne, string propName, float value)
    {
        var prop = ne.Data.OfType<FloatPropertyData>()
            .FirstOrDefault(p => p.Name.Value.ToString() == propName);
        if (prop != null)
            prop.Value = value;
    }
}
