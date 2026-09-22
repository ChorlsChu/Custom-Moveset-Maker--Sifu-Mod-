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
    {
        if (ArchetypePaths.TryGetValue(variantTag, out var path))
            return path;

        var arch = variantTag.Split('_')[0];
        return ArchetypePaths.TryGetValue(arch, out var fallback) ? fallback : null;
    }

    public static string? ResolveContextDefensePath(string variantTag)
    {
        if (ContextDefensePaths.TryGetValue(variantTag, out var path))
            return path;

        var arch = variantTag.Split('_')[0];
        return ContextDefensePaths.TryGetValue(arch, out var fallback) ? fallback : null;
    }

    private static string ResolveContentDir(string contentPath)
    {
        return contentPath.EndsWith("Content", StringComparison.OrdinalIgnoreCase)
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
            || (props.MemoryFlushLimit.HasValue && (!vanilla.MemoryFlushLimit.HasValue || Math.Abs(props.MemoryFlushLimit.Value - vanilla.MemoryFlushLimit.Value) > 0.01f));
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

        return props;
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
    }

    public static bool HasAnyValue(UnitProperties props) =>
        props.Health.HasValue || props.Structure.HasValue || props.MemoryLimit.HasValue
        || props.HitsCount.HasValue || props.MemoryFlushLimit.HasValue;

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
