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

public class StanceAnimationMapping
{
    public string StanceName { get; set; } = "";
    public string AnimBasePath { get; set; } = "";

    public string? StartE { get; set; }
    public string? StartN { get; set; }
    public string? StartS { get; set; }

    public string? StopE { get; set; }
    public string? StopEFR { get; set; }
    public string? StopN { get; set; }
    public string? StopS { get; set; }

    public string? UTurnEW { get; set; }
    public string? UTurnNS { get; set; }
    public string? UTurnSN { get; set; }
}

public static class StancePatcher
{
    public static readonly Dictionary<string, StanceAnimationMapping> Stances = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Yang"] = new StanceAnimationMapping
        {
            StanceName = "Yang",
            AnimBasePath = "Game/Animations/Yang/Locomotion/Transitions/V1/Lockmove",
            StartE = "Yang_barehands_V1_tense_start_E_E_FL",
            StartN = "Yang_barehands_V1_tense_start_N_N_FL",
            StartS = "Yang_barehands_V1_tense_start_S_S_FL",
            StopE = "Yang_barehands_V1_tense_stop_E_E_FL",
            StopEFR = "Yang_barehands_V1_tense_stop_E_E_FR",
            StopN = "Yang_barehands_V1_tense_stop_N_N_FL",
            StopS = "Yang_barehands_V1_tense_stop_S_S_FL",
            UTurnEW = "Yang_barehands_V1_tense_uTurn_E_W_front",
            UTurnNS = "Yang_barehands_V1_tense_uTurn_N_S",
            UTurnSN = "Yang_barehands_V1_tense_uTurn_S_N",
        },
        ["Grunt"] = new StanceAnimationMapping
        {
            StanceName = "Grunt",
            AnimBasePath = "Game/Animations/Grunt/Locomotion/Barehands/Transitions/V1/Lockmove",
            StartE = "Grunt_barehands_V1_tense_start_E_E_FL",
            StartN = "Grunt_barehands_V1_tense_start_N_N_FR",
            StartS = "Grunt_barehands_V1_tense_start_S_S_FL",
            StopE = "Grunt_barehands_V1_tense_stop_E_E_FL",
            StopEFR = "Grunt_barehands_V1_tense_stop_E_E_FR",
            StopN = "Grunt_barehands_V1_tense_stop_N_N_FL",
            StopS = "Grunt_barehands_V1_tense_stop_S_S_FL",
        },
    };

    public static bool HasMapping(string stanceName) => Stances.ContainsKey(stanceName);

    public static void PatchTransitionAnimRequest(string vanillaPath, string outputPath, string stanceName, EngineVersion eng)
    {
        if (!Stances.TryGetValue(stanceName, out var mapping))
            throw new Exception($"No animation mapping for stance '{stanceName}'");

        var asset = new UAsset(vanillaPath, eng, null, CustomSerializationFlags.None);

        var normalExport = asset.Exports.OfType<NormalExport>().LastOrDefault();
        if (normalExport == null)
            throw new Exception("No NormalExport found in BP_TransitionAnimRequest");

        var allCustomAnims = new List<string>();
        void AddIfPresent(string? name) { if (!string.IsNullOrEmpty(name)) allCustomAnims.Add(name); }
        AddIfPresent(mapping.StartE); AddIfPresent(mapping.StartN); AddIfPresent(mapping.StartS);
        AddIfPresent(mapping.StopE); AddIfPresent(mapping.StopEFR); AddIfPresent(mapping.StopN); AddIfPresent(mapping.StopS);
        AddIfPresent(mapping.UTurnEW); AddIfPresent(mapping.UTurnNS); AddIfPresent(mapping.UTurnSN);
        allCustomAnims = allCustomAnims.Distinct().ToList();

        var animNameToImportIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var animName in allCustomAnims)
        {
            var pkgPath = $"{mapping.AnimBasePath}/{animName}";

            var pkgImport = new UAssetAPI.Import();
            pkgImport.ClassPackage = FName.FromString(asset, "/Script/CoreUObject");
            pkgImport.ClassName = FName.FromString(asset, "Package");
            pkgImport.ObjectName = FName.FromString(asset, pkgPath);
            pkgImport.OuterIndex = new FPackageIndex(0);
            pkgImport.PackageName = FName.FromString(asset, "None");
            int pkgIdx = asset.Imports.Count;
            asset.Imports.Add(pkgImport);

            var animImport = new UAssetAPI.Import();
            animImport.ClassPackage = FName.FromString(asset, "/Script/Engine");
            animImport.ClassName = FName.FromString(asset, "AnimSequence");
            animImport.ObjectName = FName.FromString(asset, animName);
            animImport.OuterIndex = FPackageIndex.FromImport(pkgIdx);
            animImport.PackageName = FName.FromString(asset, "None");
            int animIdx = asset.Imports.Count;
            asset.Imports.Add(animImport);

            animNameToImportIdx[animName] = animIdx;
        }

        foreach (var entry in normalExport.Data)
        {
            if (entry is StructPropertyData structProp && structProp.Name?.Value?.ToString() == "m_Transitions")
            {
                foreach (var v1Entry in structProp.Value)
                {
                    if (v1Entry is StructPropertyData v1Struct &&
                        v1Struct.Name?.Value?.ToString() == "m_V1")
                    {
                        PatchV1Section(v1Struct, mapping, animNameToImportIdx);
                    }
                }
            }
        }

        asset.Write(outputPath);
        ErrorLog.Write("EXPORT", new Exception($"[STANCE] StancePatcher wrote .uasset {new FileInfo(outputPath).Length}B for '{stanceName}'"));
        var uexpPath = Path.ChangeExtension(outputPath, ".uexp");
        if (File.Exists(uexpPath))
            ErrorLog.Write("EXPORT", new Exception($"[STANCE] StancePatcher wrote .uexp {new FileInfo(uexpPath).Length}B for '{stanceName}'"));
    }

    private static void PatchV1Section(StructPropertyData v1Struct, StanceAnimationMapping mapping,
        Dictionary<string, int> animNameToImportIdx)
    {
        foreach (var field in v1Struct.Value)
        {
            if (field is not StructPropertyData fieldStruct) continue;
            var fieldName = fieldStruct.Name?.Value?.ToString() ?? "";

            if (fieldName == "m_Start_FL" || fieldName == "m_Start_FR")
            {
                var animForDir = new[] { mapping.StartN, mapping.StartE, mapping.StartS, mapping.StartE };
                PatchCardinalAnimContainer(fieldStruct, animForDir, animNameToImportIdx);
            }
            else if (fieldName == "m_Stop_FL")
            {
                var animForDir = new[] { mapping.StopN, mapping.StopE, mapping.StopS, mapping.StopEFR };
                PatchCardinalAnimContainer(fieldStruct, animForDir, animNameToImportIdx);
            }
            else if (fieldName == "m_Strafe" && mapping.UTurnEW != null)
            {
                var animForDir = new[] { mapping.UTurnNS, mapping.UTurnEW, mapping.UTurnSN, mapping.UTurnEW };
                PatchCardinalAnimContainer(fieldStruct, animForDir, animNameToImportIdx);
            }
        }
    }

    private static void PatchCardinalAnimContainer(StructPropertyData container, string?[] animForDir,
        Dictionary<string, int> animNameToImportIdx)
    {
        int dirIdx = 0;
        foreach (var cardinal in container.Value)
        {
            if (cardinal is not StructPropertyData cardinalStruct) continue;
            if (dirIdx >= animForDir.Length) break;

            var animName = animForDir[dirIdx];
            if (!string.IsNullOrEmpty(animName) && animNameToImportIdx.TryGetValue(animName, out var importIdx))
            {
                SetAnimationReference(cardinalStruct, FPackageIndex.FromImport(importIdx));
            }
            dirIdx++;
        }
    }

    private static void SetAnimationReference(StructPropertyData cardinalStruct, FPackageIndex importRef)
    {
        foreach (var prop in cardinalStruct.Value)
        {
            if (prop is ObjectPropertyData objProp && prop.Name?.Value?.ToString() == "m_animation")
            {
                objProp.Value = importRef;
                return;
            }

            if (prop is StructPropertyData innerStruct)
            {
                foreach (var innerProp in innerStruct.Value)
                {
                    if (innerProp is ObjectPropertyData innerObj && innerProp.Name?.Value?.ToString() == "m_animation")
                    {
                        innerObj.Value = importRef;
                        return;
                    }

                    if (innerProp is StructPropertyData animContainer)
                    {
                        foreach (var animEntry in animContainer.Value)
                        {
                            if (animEntry is ObjectPropertyData animObj && animEntry.Name?.Value?.ToString() == "m_animation")
                            {
                                animObj.Value = importRef;
                                return;
                            }
                            if (animEntry is StructPropertyData animStruct)
                            {
                                foreach (var animProp in animStruct.Value)
                                {
                                    if (animProp is ObjectPropertyData animPropObj && animProp.Name?.Value?.ToString() == "m_animation")
                                    {
                                        animPropObj.Value = importRef;
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
