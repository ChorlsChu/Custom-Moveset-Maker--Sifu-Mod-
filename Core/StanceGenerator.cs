using System;
using System.Collections.Generic;
using System.IO;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace SifuMovesetEditor;

public class StanceAnimData
{
    public string V0Anim1 { get; set; } = "";
    public string V0Anim1Pkg { get; set; } = "";
    public string V0Anim2 { get; set; } = "";
    public string V0Anim2Pkg { get; set; } = "";

    public string V1BackBs { get; set; } = "";
    public string V1BackBsPkg { get; set; } = "";
    public string V1BackUpperBs { get; set; } = "";
    public string V1BackUpperBsPkg { get; set; } = "";
    public string V1FrontBs { get; set; } = "";
    public string V1FrontBsPkg { get; set; } = "";
    public string V1FrontUpperBs { get; set; } = "";
    public string V1FrontUpperBsPkg { get; set; } = "";

    public string StartE { get; set; } = "";
    public string StartEPkg { get; set; } = "";
    public string StartN { get; set; } = "";
    public string StartNPkg { get; set; } = "";
    public string StartS { get; set; } = "";
    public string StartSPkg { get; set; } = "";

    public string StopE { get; set; } = "";
    public string StopEPkg { get; set; } = "";
    public string StopEFR { get; set; } = "";
    public string StopEFRPkg { get; set; } = "";
    public string StopN { get; set; } = "";
    public string StopNPkg { get; set; } = "";
    public string StopS { get; set; } = "";
    public string StopSPkg { get; set; } = "";
}

public static class StanceGenerator
{
    public static readonly Dictionary<string, StanceAnimData> Stances = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Yang"] = new StanceAnimData
        {
            V0Anim1 = "Yang_barehands_V0_tense_FL",
            V0Anim1Pkg = "/Game/Animations/Yang/Locomotion/Moving/V0/Yang_barehands_V0_tense_FL",
            V0Anim2 = "Yang_barehands_V0_tense_FR",
            V0Anim2Pkg = "/Game/Animations/Yang/Locomotion/Moving/V0/Yang_barehands_V0_tense_FR",
            V1BackBs = "BlendSpace_Yang_barehands_V1_back_tense",
            V1BackBsPkg = "/Game/Animations/Yang/Locomotion/Moving/V1/Lockmove/BlendSpace_Yang_barehands_V1_back_tense",
            V1BackUpperBs = "BlendSpace_Yang_barehands_V1_backUpper_tense",
            V1BackUpperBsPkg = "/Game/Animations/Yang/Locomotion/Moving/V1/Lockmove/BlendSpace_Yang_barehands_V1_backUpper_tense",
            V1FrontBs = "BlendSpace_Yang_barehands_V1_front_tense",
            V1FrontBsPkg = "/Game/Animations/Yang/Locomotion/Moving/V1/Lockmove/BlendSpace_Yang_barehands_V1_front_tense",
            V1FrontUpperBs = "BlendSpace_Yang_barehands_V1_frontUpper_tense",
            V1FrontUpperBsPkg = "/Game/Animations/Yang/Locomotion/Moving/V1/Lockmove/BlendSpace_Yang_barehands_V1_frontUpper_tense",
            StartE = "Yang_barehands_V1_tense_start_E_E_FL",
            StartEPkg = "/Game/Animations/Yang/Locomotion/Transitions/V1/Lockmove/Starts/Yang_barehands_V1_tense_start_E_E_FL",
            StartN = "Yang_barehands_V1_tense_start_N_N_FL",
            StartNPkg = "/Game/Animations/Yang/Locomotion/Transitions/V1/Lockmove/Starts/Yang_barehands_V1_tense_start_N_N_FL",
            StartS = "Yang_barehands_V1_tense_start_S_S_FL",
            StartSPkg = "/Game/Animations/Yang/Locomotion/Transitions/V1/Lockmove/Starts/Yang_barehands_V1_tense_start_S_S_FL",
            StopE = "Yang_barehands_V1_tense_stop_E_E_FL",
            StopEPkg = "/Game/Animations/Yang/Locomotion/Transitions/V1/Lockmove/Stops/Yang_barehands_V1_tense_stop_E_E_FL",
            StopEFR = "Yang_barehands_V1_tense_stop_E_E_FR",
            StopEFRPkg = "/Game/Animations/Yang/Locomotion/Transitions/V1/Lockmove/Stops/Yang_barehands_V1_tense_stop_E_E_FR",
            StopN = "Yang_barehands_V1_tense_stop_N_N_FL",
            StopNPkg = "/Game/Animations/Yang/Locomotion/Transitions/V1/Lockmove/Stops/Yang_barehands_V1_tense_stop_N_N_FL",
            StopS = "Yang_barehands_V1_tense_stop_S_S_FL",
            StopSPkg = "/Game/Animations/Yang/Locomotion/Transitions/V1/Lockmove/Stops/Yang_barehands_V1_tense_stop_S_S_FL",
        },
        ["Grunt"] = new StanceAnimData
        {
            V0Anim1 = "Grunt_barehands_V0_north_tense",
            V0Anim1Pkg = "/Game/Animations/Grunt/Locomotion/Barehands/Moving/V0/Lockmove/North/Grunt_barehands_V0_north_tense",
            V0Anim2 = "Grunt_barehands_V0_north_tense",
            V0Anim2Pkg = "/Game/Animations/Grunt/Locomotion/Barehands/Moving/V0/Lockmove/North/Grunt_barehands_V0_north_tense",
            V1BackBs = "BlendSpace_Grunt_barehands_V1_back_tense",
            V1BackBsPkg = "/Game/Animations/Grunt/Locomotion/Barehands/Moving/V1/Lockmove/BlendSpace_Grunt_barehands_V1_back_tense",
            V1BackUpperBs = "BlendSpace_Grunt_barehands_V1_backUpper_tense",
            V1BackUpperBsPkg = "/Game/Animations/Grunt/Locomotion/Barehands/Moving/V1/Lockmove/BlendSpace_Grunt_barehands_V1_backUpper_tense",
            V1FrontBs = "BlendSpace_Grunt_barehands_V1_front_tense",
            V1FrontBsPkg = "/Game/Animations/Grunt/Locomotion/Barehands/Moving/V1/Lockmove/BlendSpace_Grunt_barehands_V1_front_tense",
            V1FrontUpperBs = "BlendSpace_Grunt_barehands_V1_frontUpper_tense",
            V1FrontUpperBsPkg = "/Game/Animations/Grunt/Locomotion/Barehands/Moving/V1/Lockmove/BlendSpace_Grunt_barehands_V1_frontUpper_tense",
            StartE = "Grunt_barehands_V1_tense_start_E_E_FL",
            StartEPkg = "/Game/Animations/Grunt/Locomotion/Barehands/Transitions/V1/Lockmove/Starts/Grunt_barehands_V1_tense_start_E_E_FL",
            StartN = "Grunt_barehands_V1_tense_start_N_N_FR",
            StartNPkg = "/Game/Animations/Grunt/Locomotion/Barehands/Transitions/V1/Lockmove/Starts/Grunt_barehands_V1_tense_start_N_N_FR",
            StartS = "Grunt_barehands_V1_tense_start_S_S_FL",
            StartSPkg = "/Game/Animations/Grunt/Locomotion/Barehands/Transitions/V1/Lockmove/Starts/Grunt_barehands_V1_tense_start_S_S_FL",
            StopE = "Grunt_barehands_V1_tense_stop_E_E_FL",
            StopEPkg = "/Game/Animations/Grunt/Locomotion/Barehands/Transitions/V1/Lockmove/Stops/Grunt_barehands_V1_tense_stop_E_E_FL",
            StopEFR = "Grunt_barehands_V1_tense_stop_E_E_FR",
            StopEFRPkg = "/Game/Animations/Grunt/Locomotion/Barehands/Transitions/V1/Lockmove/Stops/Grunt_barehands_V1_tense_stop_E_E_FR",
            StopN = "Grunt_barehands_V1_tense_stop_N_N_FL",
            StopNPkg = "/Game/Animations/Grunt/Locomotion/Barehands/Transitions/V1/Lockmove/Stops/Grunt_barehands_V1_tense_stop_N_N_FL",
            StopS = "Grunt_barehands_V1_tense_stop_S_S_FL",
            StopSPkg = "/Game/Animations/Grunt/Locomotion/Barehands/Transitions/V1/Lockmove/Stops/Grunt_barehands_V1_tense_stop_S_S_FL",
        },
        ["FireDisciple"] = new StanceAnimData
        {
            V0Anim1 = "FireDisciple_barehands_V0_north_tense",
            V0Anim1Pkg = "/Game/Animations/FireDisciple/Locomotion/Barehands/V0/LockMove/North/FireDisciple_barehands_V0_north_tense",
            V0Anim2 = "FireDisciple_barehands_V0_north_FrontRight_tense",
            V0Anim2Pkg = "/Game/Animations/FireDisciple/Locomotion/Barehands/V0/LockMove/North/FireDisciple_barehands_V0_north_FrontRight_tense",
            V1BackBs = "BlendSpace_FireDisciple_barehands_V1_back_tense",
            V1BackBsPkg = "/Game/Animations/FireDisciple/Locomotion/Barehands/V1/Lockmove/Tense/BlendSpace_FireDisciple_barehands_V1_back_tense",
            V1BackUpperBs = "BlendSpace_Yang_barehands_V1_backUpper_tense",
            V1BackUpperBsPkg = "/Game/Animations/Yang/Locomotion/Moving/V1/Lockmove/BlendSpace_Yang_barehands_V1_backUpper_tense",
            V1FrontBs = "BlendSpace_FireDisciple_barehands_V1_front_tense",
            V1FrontBsPkg = "/Game/Animations/FireDisciple/Locomotion/Barehands/V1/Lockmove/Tense/BlendSpace_FireDisciple_barehands_V1_front_tense",
            V1FrontUpperBs = "BlendSpace_Yang_barehands_V1_frontUpper_tense",
            V1FrontUpperBsPkg = "/Game/Animations/Yang/Locomotion/Moving/V1/Lockmove/BlendSpace_Yang_barehands_V1_frontUpper_tense",
            StartE = "Disciple_barehands_tense_start_E_E_FL",
            StartEPkg = "/Game/Animations/FireDisciple/Locomotion/Barehands/Transitions/Starts/Disciple_barehands_tense_start_E_E_FL",
            StartN = "Disciple_barehands_tense_start_N_N_FR",
            StartNPkg = "/Game/Animations/FireDisciple/Locomotion/Barehands/Transitions/Starts/Disciple_barehands_tense_start_N_N_FR",
            StartS = "Disciple_barehands_tense_start_S_S_FL",
            StartSPkg = "/Game/Animations/FireDisciple/Locomotion/Barehands/Transitions/Starts/Disciple_barehands_tense_start_S_S_FL",
            StopE = "Disciple_barehands_tense_stop_E_E_FL",
            StopEPkg = "/Game/Animations/FireDisciple/Locomotion/Barehands/Transitions/Stops/Disciple_barehands_tense_stop_E_E_FL",
            StopEFR = "Disciple_barehands_tense_stop_E_E_FR",
            StopEFRPkg = "/Game/Animations/FireDisciple/Locomotion/Barehands/Transitions/Stops/Disciple_barehands_tense_stop_E_E_FR",
            StopN = "Disciple_barehands_tense_stop_N_N_FL",
            StopNPkg = "/Game/Animations/FireDisciple/Locomotion/Barehands/Transitions/Stops/Disciple_barehands_tense_stop_N_N_FL",
            StopS = "Disciple_barehands_tense_stop_S_S_FL",
            StopSPkg = "/Game/Animations/FireDisciple/Locomotion/Barehands/Transitions/Stops/Disciple_barehands_tense_stop_S_S_FL",
        },
        ["Flashkick"] = new StanceAnimData
        {
            V0Anim1 = "FlashKick_barehands_V0_FR_tense",
            V0Anim1Pkg = "/Game/Animations/FlashKick/Locomotion/Barehands/Moving/V0/FlashKick_barehands_V0_FR_tense",
            V0Anim2 = "FlashKick_barehands_V0_FR_tense",
            V0Anim2Pkg = "/Game/Animations/FlashKick/Locomotion/Barehands/Moving/V0/FlashKick_barehands_V0_FR_tense",
            V1BackBs = "BlendSpace_FlashKick_barehands_V1_back_tense",
            V1BackBsPkg = "/Game/Animations/FlashKick/Locomotion/Barehands/Moving/V1/Lockmove/BlendSpace_FlashKick_barehands_V1_back_tense",
            V1BackUpperBs = "BlendSpace_FlashKick_barehands_V1_backUpper_tense",
            V1BackUpperBsPkg = "/Game/Animations/FlashKick/Locomotion/Barehands/Moving/V1/Lockmove/BlendSpace_FlashKick_barehands_V1_backUpper_tense",
            V1FrontBs = "BlendSpace_FlashKick_barehands_V1_front_tense",
            V1FrontBsPkg = "/Game/Animations/FlashKick/Locomotion/Barehands/Moving/V1/Lockmove/BlendSpace_FlashKick_barehands_V1_front_tense",
            V1FrontUpperBs = "BlendSpace_FlashKick_barehands_V1_frontUpper_tense",
            V1FrontUpperBsPkg = "/Game/Animations/FlashKick/Locomotion/Barehands/Moving/V1/Lockmove/BlendSpace_FlashKick_barehands_V1_frontUpper_tense",
            StartE = "Flashkick_barehands_V1_tense_start_E_E_FL",
            StartEPkg = "/Game/Animations/FlashKick/Locomotion/Barehands/Moving/Transitions/Starts/Flashkick_barehands_V1_tense_start_E_E_FL",
            StartN = "Flashkick_barehands_V1_tense_start_N_N_FR",
            StartNPkg = "/Game/Animations/FlashKick/Locomotion/Barehands/Moving/Transitions/Starts/Flashkick_barehands_V1_tense_start_N_N_FR",
            StartS = "Yang_barehands_V1_tense_start_S_S_FL",
            StartSPkg = "/Game/Animations/Yang/Locomotion/Transitions/V1/Lockmove/Starts/Yang_barehands_V1_tense_start_S_S_FL",
            StopE = "Flashkick_barehands_V1_tense_stop_E_E_FL",
            StopEPkg = "/Game/Animations/FlashKick/Locomotion/Barehands/Moving/Transitions/Stops/Flashkick_barehands_V1_tense_stop_E_E_FL",
            StopEFR = "Flashkick_barehands_V1_tense_stop_E_E_FR",
            StopEFRPkg = "/Game/Animations/FlashKick/Locomotion/Barehands/Moving/Transitions/Stops/Flashkick_barehands_V1_tense_stop_E_E_FR",
            StopN = "Flashkick_barehands_V1_tense_stop_N_N_FL",
            StopNPkg = "/Game/Animations/FlashKick/Locomotion/Barehands/Moving/Transitions/Stops/Flashkick_barehands_V1_tense_stop_N_N_FL",
            StopS = "Flashkick_barehands_V1_tense_stop_S_S_FL",
            StopSPkg = "/Game/Animations/FlashKick/Locomotion/Barehands/Moving/Transitions/Stops/Flashkick_barehands_V1_tense_stop_S_S_FL",
        },
        ["Bodyguard"] = new StanceAnimData
        {
            V0Anim1 = "BodyGuard_barehands_V0_north_tense",
            V0Anim1Pkg = "/Game/Animations/BodyGuard/Locomotion/Barehands/Moving/V0/LockMove/BodyGuard_barehands_V0_north_tense",
            V0Anim2 = "BodyGuard_barehands_V0_north_tense",
            V0Anim2Pkg = "/Game/Animations/BodyGuard/Locomotion/Barehands/Moving/V0/LockMove/BodyGuard_barehands_V0_north_tense",
            V1BackBs = "BlendSpace_BodyGuard_barehands_V1_back_tense",
            V1BackBsPkg = "/Game/Animations/BodyGuard/Locomotion/Barehands/Moving/V1/LockMove/BlendSpace_BodyGuard_barehands_V1_back_tense",
            V1BackUpperBs = "BlendSpace_BodyGuard_barehands_V1_backUpper_tense",
            V1BackUpperBsPkg = "/Game/Animations/BodyGuard/Locomotion/Barehands/Moving/V1/LockMove/BlendSpace_BodyGuard_barehands_V1_backUpper_tense",
            V1FrontBs = "BlendSpace_BodyGuard_barehands_V1_front_tense",
            V1FrontBsPkg = "/Game/Animations/BodyGuard/Locomotion/Barehands/Moving/V1/LockMove/BlendSpace_BodyGuard_barehands_V1_front_tense",
            V1FrontUpperBs = "BlendSpace_BodyGuard_barehands_V1_frontUpper_tense",
            V1FrontUpperBsPkg = "/Game/Animations/BodyGuard/Locomotion/Barehands/Moving/V1/LockMove/BlendSpace_BodyGuard_barehands_V1_frontUpper_tense",
            StartE = "BodyGuard_barehands_tense_start_E_E_FL",
            StartEPkg = "/Game/Animations/BodyGuard/Locomotion/Barehands/Moving/Transitions/Starts/BodyGuard_barehands_tense_start_E_E_FL",
            StartN = "BodyGuard_barehands_tense_start_N_N_FR",
            StartNPkg = "/Game/Animations/BodyGuard/Locomotion/Barehands/Moving/Transitions/Starts/BodyGuard_barehands_tense_start_N_N_FR",
            StartS = "BodyGuard_barehands_tense_start_S_S_FL",
            StartSPkg = "/Game/Animations/BodyGuard/Locomotion/Barehands/Moving/Transitions/Starts/BodyGuard_barehands_tense_start_S_S_FL",
            StopE = "BodyGuard_barehands_tense_stop_E_E_FL",
            StopEPkg = "/Game/Animations/BodyGuard/Locomotion/Barehands/Moving/Transitions/Stops/BodyGuard_barehands_tense_stop_E_E_FL",
            StopEFR = "BodyGuard_barehands_tense_stop_E_E_FR",
            StopEFRPkg = "/Game/Animations/BodyGuard/Locomotion/Barehands/Moving/Transitions/Stops/BodyGuard_barehands_tense_stop_E_E_FR",
            StopN = "Bodyguard_barehands_tense_stop_N_N_FL",
            StopNPkg = "/Game/Animations/BodyGuard/Locomotion/Barehands/Moving/Transitions/Stops/Bodyguard_barehands_tense_stop_N_N_FL",
            StopS = "BodyGuard_barehands_tense_stop_S_S_FL",
            StopSPkg = "/Game/Animations/BodyGuard/Locomotion/Barehands/Moving/Transitions/Stops/BodyGuard_barehands_tense_stop_S_S_FL",
        },
        ["Juggernaut"] = new StanceAnimData
        {
            V0Anim1 = "BigGuy_barehands_V0_FrontLeft_tense",
            V0Anim1Pkg = "/Game/Animations/BigGuy/Locomotion/Barehands/Moving/V0/BigGuy_barehands_V0_FrontLeft_tense",
            V0Anim2 = "BigGuy_barehands_V0_front",
            V0Anim2Pkg = "/Game/Animations/BigGuy/Locomotion/Barehands/Moving/V0/BigGuy_barehands_V0_front",
            V1BackBs = "BlendSpace_BigGuy_barehands_V1_back_tense",
            V1BackBsPkg = "/Game/Animations/BigGuy/Locomotion/Barehands/Moving/V1/Lockmove/BlendSpace_BigGuy_barehands_V1_back_tense",
            V1BackUpperBs = "BlendSpace_BigGuy_barehands_V1_backUpper_tense",
            V1BackUpperBsPkg = "/Game/Animations/BigGuy/Locomotion/Barehands/Moving/V1/Lockmove/BlendSpace_BigGuy_barehands_V1_backUpper_tense",
            V1FrontBs = "BlendSpace_BigGuy_barehands_V1_front_tense",
            V1FrontBsPkg = "/Game/Animations/BigGuy/Locomotion/Barehands/Moving/V1/Lockmove/BlendSpace_BigGuy_barehands_V1_front_tense",
            V1FrontUpperBs = "BlendSpace_BigGuy_barehands_V1_frontUpper_tense",
            V1FrontUpperBsPkg = "/Game/Animations/BigGuy/Locomotion/Barehands/Moving/V1/Lockmove/BlendSpace_BigGuy_barehands_V1_frontUpper_tense",
            StartE = "BigGuy_barehands_tense_start_E_E_FL",
            StartEPkg = "/Game/Animations/BigGuy/Locomotion/Barehands/Moving/Transitions/Starts/BigGuy_barehands_tense_start_E_E_FL",
            StartN = "BigGuy_barehands_tense_start_N_N_FL",
            StartNPkg = "/Game/Animations/BigGuy/Locomotion/Barehands/Moving/Transitions/Starts/BigGuy_barehands_tense_start_N_N_FL",
            StartS = "BigGuy_barehands_tense_start_S_S_FL",
            StartSPkg = "/Game/Animations/BigGuy/Locomotion/Barehands/Moving/Transitions/Starts/BigGuy_barehands_tense_start_S_S_FL",
            StopE = "BigGuy_barehands_tense_stop_E_E_FL",
            StopEPkg = "/Game/Animations/BigGuy/Locomotion/Barehands/Moving/Transitions/Stops/BigGuy_barehands_tense_stop_E_E_FL",
            StopEFR = "BigGuy_barehands_tense_stop_E_E_FR",
            StopEFRPkg = "/Game/Animations/BigGuy/Locomotion/Barehands/Moving/Transitions/Stops/BigGuy_barehands_tense_stop_E_E_FR",
            StopN = "BigGuy_barehands_tense_stop_N_N_FL",
            StopNPkg = "/Game/Animations/BigGuy/Locomotion/Barehands/Moving/Transitions/Stops/BigGuy_barehands_tense_stop_N_N_FL",
            StopS = "BigGuy_barehands_tense_stop_S_S_FL",
            StopSPkg = "/Game/Animations/BigGuy/Locomotion/Barehands/Moving/Transitions/Stops/BigGuy_barehands_tense_stop_S_S_FL",
        },
    };

    public static bool HasStance(string stanceName) => Stances.ContainsKey(stanceName);

    public static void GenerateTransitionAnimRequest(string templatePath, string outputPath, string stanceName, EngineVersion eng)
    {
        if (!Stances.TryGetValue(stanceName, out var data))
            throw new Exception($"No animation mapping for stance '{stanceName}'");

        var asset = new UAsset(templatePath, eng, null, CustomSerializationFlags.None);

        void SetImport(int idx, string name, string pkgPath)
        {
            if (idx >= asset.Imports.Count) return;
            asset.Imports[idx].ObjectName = FName.FromString(asset, name);
            int pkgIdx = -asset.Imports[idx].OuterIndex.Index - 1;
            if (pkgIdx >= 0 && pkgIdx < asset.Imports.Count)
            {
                asset.Imports[pkgIdx].ObjectName = FName.FromString(asset, pkgPath);
            }
        }

        SetImport(8, data.StartE, data.StartEPkg);
        SetImport(9, data.StartN, data.StartNPkg);
        SetImport(10, data.StartS, data.StartSPkg);
        SetImport(12, data.StopE, data.StopEPkg);
        SetImport(13, data.StopEFR, data.StopEFRPkg);
        SetImport(14, data.StopN, data.StopNPkg);
        SetImport(15, data.StopS, data.StopSPkg);

        asset.Write(outputPath);
        ErrorLog.Write("EXPORT", new Exception($"[STANCE] Generated BP_TransitionAnimRequest for '{stanceName}' ({new FileInfo(outputPath).Length}B)"));
    }

    public static void GenerateBaseMovementDB(string templatePath, string outputPath, string stanceName, EngineVersion eng)
    {
        if (!Stances.TryGetValue(stanceName, out var data))
            throw new Exception($"No animation mapping for stance '{stanceName}'");

        var asset = new UAsset(templatePath, eng, null, CustomSerializationFlags.None);

        void SetImport(int idx, string name, string pkgPath)
        {
            if (idx >= asset.Imports.Count) return;
            asset.Imports[idx].ObjectName = FName.FromString(asset, name);
            int pkgIdx = -asset.Imports[idx].OuterIndex.Index - 1;
            if (pkgIdx >= 0 && pkgIdx < asset.Imports.Count)
            {
                asset.Imports[pkgIdx].ObjectName = FName.FromString(asset, pkgPath);
            }
        }

        SetImport(6, data.V0Anim1, data.V0Anim1Pkg);
        SetImport(7, data.V0Anim2, data.V0Anim2Pkg);
        SetImport(12, data.V1BackBs, data.V1BackBsPkg);
        SetImport(13, data.V1BackUpperBs, data.V1BackUpperBsPkg);
        SetImport(14, data.V1FrontBs, data.V1FrontBsPkg);
        SetImport(15, data.V1FrontUpperBs, data.V1FrontUpperBsPkg);

        asset.Write(outputPath);
        ErrorLog.Write("EXPORT", new Exception($"[STANCE] Generated BaseMovementDB for '{stanceName}' ({new FileInfo(outputPath).Length}B)"));
    }
}
