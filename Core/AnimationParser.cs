using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Animations.PSA;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using Newtonsoft.Json;

namespace SifuMovesetEditor;

public class MoveInfo
{
    public string DisplayName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Character { get; set; } = "";
    public string WeaponType { get; set; } = "";
    public string Category { get; set; } = "";
    public bool IsUsed { get; set; } = false;
    public bool IsValid { get; set; } = true;
    public string EnemyType { get; set; } = "";

    public string DisplayNameClean
    {
        get
        {
            var result = DisplayName;

            if (!string.IsNullOrEmpty(Character) && !string.IsNullOrEmpty(WeaponType))
            {
                var prefix = $"{Character}_attack_{WeaponType}_";
                if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result = result[prefix.Length..];
            }

            if (result == DisplayName && !string.IsNullOrEmpty(WeaponType))
            {
                var idx = result.IndexOf(WeaponType, StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    var afterWeapon = idx + WeaponType.Length;
                    if (afterWeapon < result.Length && result[afterWeapon] == '_')
                        afterWeapon++;
                    if (afterWeapon < result.Length)
                        result = result[afterWeapon..];
                }
            }

            if (result == DisplayName && !string.IsNullOrEmpty(Character))
            {
                var charAttackPrefix = $"{Character}_attack_";
                if (result.StartsWith(charAttackPrefix, StringComparison.OrdinalIgnoreCase))
                    result = result[charAttackPrefix.Length..];

                if (result == DisplayName)
                {
                    var charPrefix = $"{Character}_";
                    if (result.StartsWith(charPrefix, StringComparison.OrdinalIgnoreCase))
                        result = result[charPrefix.Length..];
                }
            }

            if (result == DisplayName)
            {
                var lastUnderscore = result.LastIndexOf('_');
                if (lastUnderscore > 0 && lastUnderscore < result.Length - 1)
                {
                    var candidate = result[(lastUnderscore + 1)..];
                    if (candidate.Length > 2)
                        result = candidate;
                }
            }

            if (string.Equals(Character, "MainChar", StringComparison.OrdinalIgnoreCase))
            {
                var mainCharPrefixes = new[] {
                    "Barehands_", "PunishRepulse_", "PunishKD_", "FollowUp_",
                    "LightCombo_", "Engage_", "Pressure_", "Punish_", "Skill_", "KD_"
                };
                foreach (var pfx in mainCharPrefixes)
                {
                    if (result.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)
                        && result.Length > pfx.Length)
                    {
                        result = result[pfx.Length..];
                        break;
                    }
                }
            }

            return result;
        }
    }
}

public class GroupHeader
{
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int Count { get; set; }
    public string Subtitle { get; set; } = "";
    public bool IsExpanded { get; set; }
    public string ParentName { get; set; } = "";
}

public class ComboNode
{
    public int Id { get; set; }
    public int TreeIndex { get; set; } = -1;
    public string Name { get; set; } = "";
    public string AnimPath { get; set; } = "";
    public string DefaultAnimPath { get; set; } = "";
    public string DefaultDBPath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsRoot { get; set; }
    public int Depth { get; set; }
    public string InputLabel { get; set; } = "";
    public string DirectionLabel { get; set; } = "";
    public string VanillaAnimPath { get; set; } = "";
}

public class ComboEdge
{
    public int FromNodeId { get; set; }
    public int ToNodeId { get; set; }
    public string InputName { get; set; } = "";
}

public class ComboGraph
{
    public string WeaponName { get; set; } = "";
    public List<ComboNode> Nodes { get; set; } = [];
    public List<ComboEdge> Edges { get; set; } = [];
}

public class BoneData
{
    public string name { get; set; } = "";
    public int parent { get; set; } = -1;
}

public class BindPoseTransform
{
    public PosVec position { get; set; } = new();
    public RotVec rotation { get; set; } = new();
}

public class PosVec
{
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }
}

public class RotVec
{
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }
    public float w { get; set; } = 1f;
}

public class AnimTrackData
{
    public string boneName { get; set; } = "";
    public int boneIndex { get; set; }
    public float[] times { get; set; } = [];
    public PosVec[] positions { get; set; } = [];
    public RotVec[] rotations { get; set; } = [];
    public PosVec[] scales { get; set; } = [];
}

public class AnimationJsonData
{
    public SkeletonJson skeleton { get; set; } = new();
    public AnimationJson animation { get; set; } = new();
}

public class SkeletonJson
{
    public BoneData[] bones { get; set; } = [];
    public BindPoseTransform[] bindPose { get; set; } = [];
}

public class AnimationJson
{
    public string name { get; set; } = "";
    public float duration { get; set; }
    public int numFrames { get; set; }
    public float fps { get; set; }
    public AnimTrackData[] tracks { get; set; } = [];
}

public class MeshJsonData
{
    public float[] positions { get; set; } = [];
    public float[] normals { get; set; } = [];
    public float[] uvs { get; set; } = [];
    public int[] indices { get; set; } = [];
    public int[] skinIndices { get; set; } = [];
    public float[] skinWeights { get; set; } = [];
    public string[] boneNames { get; set; } = [];
    public float[] bindPose { get; set; } = [];
    public int[] boneParents { get; set; } = [];

}

public class AnimationParser : IDisposable
{
    private DefaultFileProvider? _provider;
    private string _gameRootPath = "";
    private string _contentPath = "";
    public bool IsLoaded => _provider != null;
    public Dictionary<string, string> AnimToDbPath { get; } = new();

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "01_FacialPoseAsset", "Weapon", "Activities",
        "Barks", "Dash", "Death", "Deflect", "Detection",
        "DialogGestures", "DizzyState", "Dizzy", "Fidget", "GiveUp",
        "Guard", "HitReactions", "HitsAnims", "IncapacitatedState",
        "KnockDownState", "Locomotion", "Parry", "PushedState",
        "StructureBrokenState", "Traversal", "Turns", "WeaponActions",
        "WeaponAction", "Conditions", "GenericData", "VitalPointsDBs",
        "Variations", "_Arena", "_OLD", "_Advanced", "_Base",
        "_MainGame", "_Miniboss", "_Tests", "_old"
    };

    private static readonly Dictionary<string, string> CharacterMeshPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grunt"] = "Game/Characters/PNJ/Grunt/M/Meshes/Hideout3/SK_Grunt_M_Hideout_03_Fighter_01",
        ["FireDisciple"] = "Game/Characters/PNJ/Disicple/M/Meshes/SK_Disciple_M_Hideout02_Fighter_01",
        ["FlashKick"] = "Game/Characters/PNJ/FlashKick/Meshes/Hideout03/SK_FlashKick_Hideout_03_FlashBlade_01",
        ["BigGuy"] = "Game/Characters/PNJ/BigGuy/Meshes/Hideout03/SK_BigGuy_Hideout03_Fighter_01",
        ["BodyGuard"] = "Game/Characters/PNJ/BodyGuards/M/Meshes/Hideout03/SK_BodyGuard_M_Hideout03_Fighter_01",
        ["Servant"] = "Game/Characters/PNJ/Servant/M/Meshes/Hideout03/SK_Servant_M_Hideout03_Fighter_01",
        ["Fajar"] = "Game/Characters/Boss/Fajar/Meshes/SK_Fajar_HD0",
        ["Sean"] = "Game/Characters/Boss/Sean/Meshes/SK_Sean_HD0",
        ["Kuroki"] = "Game/Characters/Boss/Kuroki/Meshes/SK_Kuroki_HD0",
        ["Yang"] = "Game/Characters/Boss/Yang/Meshes/SK_Yang_Hideout00",
        ["Fengjie"] = "Game/Characters/Boss/Fengjie/Meshes/SK_Fengjie_HD0",
        ["MainChar"] = "Game/Characters/MainChar/M/Meshes/SK_M_MainChar_01",
    };

    public void Initialize(string gameRootPath, string contentPath)
    {
        _gameRootPath = gameRootPath;
        _contentPath = contentPath;
        _provider = null;

        _provider = new DefaultFileProvider(
            gameRootPath,
            SearchOption.AllDirectories,
            new VersionContainer(EGame.GAME_UE4_26)
        );
        _provider.Initialize();

        LoadEngineContent(gameRootPath);
    }

    private void LoadEngineContent(string gameRootPath)
    {
        try
        {
            var parentDir = Directory.GetParent(gameRootPath)?.FullName;
            if (parentDir == null) return;

            var engineContentDir = Path.Combine(parentDir, "Engine", "Content");
            if (!Directory.Exists(engineContentDir))
            {
                LogDebug($"Engine content not found at: {engineContentDir}");
                return;
            }

            var engineRoot = Path.Combine(parentDir, "Engine");
            var baseDir = new DirectoryInfo(engineRoot);
            var filesAdded = 0;

            foreach (var file in Directory.GetFiles(engineContentDir, "*.uasset", SearchOption.AllDirectories))
            {
                var fileInfo = new FileInfo(file);
                var gameFile = new OsGameFile(baseDir, fileInfo, "Engine/", _provider!.Versions);
                _provider.Files.AddFiles(new Dictionary<string, GameFile> { [gameFile.Path] = gameFile });
                filesAdded++;
            }

            foreach (var file in Directory.GetFiles(engineContentDir, "*.uexp", SearchOption.AllDirectories))
            {
                var fileInfo = new FileInfo(file);
                var gameFile = new OsGameFile(baseDir, fileInfo, "Engine/", _provider!.Versions);
                _provider.Files.AddFiles(new Dictionary<string, GameFile> { [gameFile.Path] = gameFile });
                filesAdded++;
            }

            LogDebug($"Loaded {filesAdded} engine content files from {engineContentDir}");
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to load engine content: {ex.Message}");
        }
    }

    private static void LogDebug(string message)
    {
        try { File.AppendAllText(Path.Combine(Directory.GetCurrentDirectory(), "error.log"), $"[{DateTime.Now:HH:mm:ss}] {message}\n"); } catch { }
    }

    public List<MoveInfo> ScanAnimations()
    {
        var moves = new List<MoveInfo>();
        var animsPath = Path.Combine(_contentPath, "Animations");
        if (!Directory.Exists(animsPath)) return moves;

        foreach (var characterDir in Directory.GetDirectories(animsPath))
        {
            var character = Path.GetFileName(characterDir);
            if (SkipDirectories.Contains(character)) continue;

            var attacksDir = Path.Combine(characterDir, "Attacks");
            if (!Directory.Exists(attacksDir)) continue;

            foreach (var weaponDir in Directory.GetDirectories(attacksDir))
            {
                var weaponType = NormalizeWeaponType(Path.GetFileName(weaponDir));
                if (SkipDirectories.Contains(weaponType)) continue;

                ScanAttackMoves(weaponDir, character, weaponType, "", moves);
            }
        }

        return moves.OrderBy(m => m.Character)
                     .ThenBy(m => m.WeaponType)
                     .ThenBy(m => m.Category)
                     .ThenBy(m => m.DisplayName)
                      .ToList();
    }

    public List<MoveInfo> ScanGetUpAnims()
    {
        var moves = new List<MoveInfo>();
        var animsPath = Path.Combine(_contentPath, "Animations");
        if (!Directory.Exists(animsPath)) return moves;

        var getUpFiles = new (string character, string relDir, string fileName)[]
        {
            ("FireDisciple", "KnockDownState/Barehands/GetUp", "FireDisciple_barehands_getUp_front"),
            ("FlashKick", "KnockDownState/GetUp", "Flashkick_barehands_KnockDown_getUp_front"),
        };

        foreach (var (character, relDir, fileName) in getUpFiles)
        {
            var filePath = Path.Combine(animsPath, character, relDir, fileName + ".uasset");
            if (!File.Exists(filePath)) continue;

            var gamePath = "Game/Animations/" + character + "/" + relDir + "/" + fileName;
            moves.Add(new MoveInfo
            {
                DisplayName = fileName,
                FullPath = gamePath,
                Character = character,
                WeaponType = "BareHands",
                Category = "GetUp",
                IsUsed = false
            });
        }

        return moves;
    }

    public List<MoveInfo> ScanStanceAnims()
    {
        var moves = new List<MoveInfo>();
        var animsPath = Path.Combine(_contentPath, "Animations");
        if (!Directory.Exists(animsPath)) return moves;

        foreach (var characterDir in Directory.GetDirectories(animsPath))
        {
            var character = Path.GetFileName(characterDir);
            if (SkipDirectories.Contains(character)) continue;

            var locoDir = Path.Combine(characterDir, "Locomotion");
            if (!Directory.Exists(locoDir)) continue;

            var found = FindStanceAnim(locoDir, character);
            if (found != null)
                moves.Add(found);
        }

        return moves.OrderBy(m => m.Character).ToList();
    }

    private MoveInfo? FindStanceAnim(string dir, string character)
    {
        var candidates = new List<(string file, string name, bool isV1, bool isNorth)>();
        CollectStanceCandidates(dir, candidates);

        if (candidates.Count == 0) return null;

        var best = candidates
            .OrderByDescending(c => c.isV1)
            .ThenByDescending(c => c.isNorth)
            .First();

        var relPath = best.file.Substring(_contentPath.Length).TrimStart('\\', '/').Replace('\\', '/');
        var gamePath = "Game/" + relPath.Replace(".uasset", "");
        var weapon = Path.GetFileName(Path.GetDirectoryName(dir)) ?? "";

        return new MoveInfo
        {
            DisplayName = $"{character}_stance",
            FullPath = gamePath,
            Character = character,
            WeaponType = weapon,
            Category = "Combat Stance"
        };
    }

    private void CollectStanceCandidates(string dir, List<(string file, string name, bool isV1, bool isNorth)> candidates)
    {
        foreach (var file in Directory.GetFiles(dir, "*.uasset"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var lower = name.ToLowerInvariant();
            if (lower.StartsWith("blend")) continue;
            if (!lower.Contains("north_tense") && !lower.Contains("front_tense")) continue;

            var isV1 = file.Contains("\\V1\\") || file.Contains("/V1/");
            var isNorth = lower.Contains("north");
            candidates.Add((file, name, isV1, isNorth));
        }

        foreach (var subDir in Directory.GetDirectories(dir))
            CollectStanceCandidates(subDir, candidates);
    }

    public void BuildAnimToDbMapping()
    {
        AnimToDbPath.Clear();
        if (_provider == null) return;

        var dbPath = Path.Combine(_contentPath, "DB");
        if (!Directory.Exists(dbPath)) return;

        foreach (var charDir in Directory.GetDirectories(Path.Combine(dbPath, "AI", "Archetypes")))
        {
            var attacksDir = Path.Combine(charDir, "Attacks");
            if (!Directory.Exists(attacksDir)) continue;

            foreach (var weaponDir in Directory.GetDirectories(attacksDir))
            {
                foreach (var subDir in Directory.GetDirectories(weaponDir))
                    ScanDbDirForMapping(subDir);
                ScanDbDirForMapping(weaponDir);
            }
        }

        var mainCharCombos = Path.Combine(dbPath, "_MainChar", "Combos", "Attacks");
        if (Directory.Exists(mainCharCombos))
        {
            foreach (var weaponDir in Directory.GetDirectories(mainCharCombos))
            {
                foreach (var subDir in Directory.GetDirectories(weaponDir))
                    ScanDbDirForMapping(subDir);
                ScanDbDirForMapping(weaponDir);
            }
        }

        LogDebug($"[MAP] Built anim→DB mapping: {AnimToDbPath.Count} entries");
    }

    private void ScanDbDirForMapping(string dir)
    {
        foreach (var file in Directory.GetFiles(dir, "*.uasset"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var relPath = file.Substring(_contentPath.Length).TrimStart('\\', '/').Replace('\\', '/');
            var gamePath = "Game/" + relPath.Replace(".uasset", "");

            try
            {
                var dbObj = _provider?.SafeLoadPackageObject<UObject>(gamePath);
                if (dbObj == null) continue;

                var mAttack = dbObj.Properties.FirstOrDefault(p => p.Name.Text == "m_Attack");
                if (mAttack?.Tag is StructProperty attackStruct &&
                    attackStruct.Value.StructType is FStructFallback attackData)
                {
                    var mAnimation = attackData.Properties.FirstOrDefault(p => p.Name.Text == "m_Animation");
                    if (mAnimation?.Tag is ObjectProperty animObj && animObj.Value != null)
                    {
                        var resolved = animObj.Value.ResolvedObject;
                        if (resolved != null)
                        {
                            var animPath = NormalizeAnimPath(resolved.GetPathName());
                            if (!string.IsNullOrEmpty(animPath) && !AnimToDbPath.ContainsKey(animPath))
                                AnimToDbPath[animPath] = gamePath;
                        }
                    }
                }
            }
            catch { }
        }

        foreach (var subDir in Directory.GetDirectories(dir))
            ScanDbDirForMapping(subDir);
    }

    private static readonly string[] AttackDataTablePaths =
    [
        "Game/DB/AI/Archetypes/Grunt/Attacks/Grunt_AttacksDatatable",
        "Game/DB/AI/Archetypes/Grunt/Attacks/Grunt_WeaponsDatatable",
        "Game/DB/AI/Archetypes/FlashKick/Attacks/FlashKick_AttackData",
        "Game/DB/AI/Archetypes/BigGuy/Attacks/BigGuy_AttackData",
        "Game/DB/AI/Archetypes/Bodyguard/Attacks/BodyGuard_AttackData",
        "Game/DB/AI/Archetypes/FireDisciple/Attacks/FireDisciple_AttackData",
        "Game/DB/AI/Archetypes/FireDisciple/Attacks/FireDisciple_Staff_AttackData",
        "Game/DB/AI/Archetypes/Fajar/Attacks/Fajar_AttacksDatatable",
        "Game/DB/AI/Archetypes/Sean/Attacks/Sean_AttacksDatatable",
        "Game/DB/AI/Archetypes/Kuroki/Kuroki_AttacksDatatable",
        "Game/DB/AI/Archetypes/Fengjie/Attacks/Fengjie_AttacksDatatable",
        "Game/DB/AI/Archetypes/Yang/Attacks/Yang_Attacks",
        "Game/DB/Attacks/WUGUAN_Attacks",
    ];

    public HashSet<string> ScanUsedAnimations()
    {
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_provider == null) return usedPaths;

        foreach (var dtPath in AttackDataTablePaths)
        {
            try
            {
                var table = _provider.SafeLoadPackageObject<UDataTable>(dtPath);
                if (table == null)
                {
                    LogDebug($"[DT] Could not load: {dtPath}");
                    continue;
                }

                LogDebug($"[DT] Loaded {dtPath}: {table.RowMap.Count} rows, struct={table.RowStructName}");

                if (table.RowMap.Count == 0) continue;

                var firstRow = table.RowMap.Values.First();
                var fieldNames = firstRow.Properties.Select(p => $"{p.Name.Text}[{p.PropertyType.Text}]").ToList();
                LogDebug($"[DT]   Fields: {string.Join(", ", fieldNames)}");

                foreach (var (rowName, rowData) in table.RowMap)
                {
                    ScanRowForAnimPaths(rowData, usedPaths);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"[DT] Error reading {dtPath}: {ex.Message}");
            }
        }

        LogDebug($"[DT] Total used animation paths found: {usedPaths.Count}");
        foreach (var p in usedPaths.Take(10))
            LogDebug($"[DT]   Sample: {p}");
        return usedPaths;
    }

    private void ScanRowForAnimPaths(FStructFallback row, HashSet<string> usedPaths)
    {
        foreach (var prop in row.Properties)
        {
            try
            {
                if (prop.Tag == null) continue;

                if (prop.PropertyType.Text == "SoftObjectProperty" && prop.Tag is SoftObjectProperty softObj)
                {
                    var assetPath = softObj.Value.AssetPathName.Text;
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        var normalized = NormalizeAnimPath(assetPath);
                        if (normalized.Contains("Anim", StringComparison.OrdinalIgnoreCase))
                            usedPaths.Add(normalized);
                    }
                }
                else if (prop.PropertyType.Text == "StrProperty" && prop.Tag is StrProperty strVal)
                {
                    var val = strVal.Value;
                    if (!string.IsNullOrEmpty(val) && val.Contains("Anim", StringComparison.OrdinalIgnoreCase))
                    {
                        usedPaths.Add(NormalizeAnimPath(val));
                    }
                }
                else if (prop.PropertyType.Text == "NameProperty" && prop.Tag is NameProperty nameVal)
                {
                    var val = nameVal.Value.Text;
                    if (!string.IsNullOrEmpty(val) && val.Contains("Anim", StringComparison.OrdinalIgnoreCase))
                    {
                        usedPaths.Add(NormalizeAnimPath(val));
                    }
                }
                else if (prop.PropertyType.Text == "ObjectProperty" && prop.Tag is ObjectProperty objProp)
                {
                    var val = objProp.Value;
                    if (val != null)
                    {
                        var text = val.ToString();
                        if (!string.IsNullOrEmpty(text) && text.Contains("Anim", StringComparison.OrdinalIgnoreCase))
                        {
                            usedPaths.Add(NormalizeAnimPath(text));
                        }
                    }
                }
            }
            catch { }
        }
    }

    internal static string NormalizeAnimPath(string path)
    {
        path = path.Replace("\\", "/");
        if (path.StartsWith("/"))
            path = path.Substring(1);

        var contentIdx = path.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (contentIdx >= 0)
            path = "Game/" + path.Substring(contentIdx + "/Content/".Length);

        var lastSlash = path.LastIndexOf('/');
        var lastDot = path.LastIndexOf('.');
        if (lastDot > lastSlash)
            path = path[..lastDot];

        return path;
    }

    internal static string NormalizeWeaponType(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "barehands" or "bare_hands" => "BareHands",
            "staff" => "Staff",
            "blade" or "blades" or "machete" or "dagger" => "Blades",
            "bat" or "bats" or "blunt" => "Bats",
            "meteor" or "hammer" or "meteorhammer" or "meteor_hammer" => "MeteorHammer",
            "tristaff" => "TriStaff",
            _ => raw
        };
    }

    private void ScanAttackMoves(string dir, string character, string weaponType, string category, List<MoveInfo> moves)
    {
        foreach (var file in Directory.GetFiles(dir, "*.uasset"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var relPath = file.Substring(_contentPath.Length).TrimStart('\\', '/').Replace('\\', '/');
            var gamePath = "Game/" + relPath.Replace(".uasset", "");

            moves.Add(new MoveInfo
            {
                DisplayName = name,
                FullPath = gamePath,
                Character = character,
                WeaponType = weaponType,
                Category = category
            });
        }

        foreach (var subDir in Directory.GetDirectories(dir))
        {
            var subCat = Path.GetFileName(subDir);
            if (SkipDirectories.Contains(subCat)) continue;
            ScanAttackMoves(subDir, character, weaponType, subCat, moves);
        }
    }

    public List<string> GetAvailableCharacters()
    {
        return CharacterMeshPaths.Keys.OrderBy(k => k).ToList();
    }

    public ComboGraph? LoadMainCharComboTree()
    {
        if (_provider == null) return null;

        try
        {
            var obj = _provider.SafeLoadPackageObject<UObject>("Game/DB/_MainChar/Combos/MainChar_ComboTree");
            if (obj == null)
            {
                LogDebug("[COMBO] Could not load MainChar_ComboTree");
                return null;
            }

            return ParseComboTreeFromObject(obj);
        }
        catch (Exception ex)
        {
            LogDebug($"[COMBO] Error loading MainChar combo tree: {ex.Message}");
            return null;
        }
    }

    public ComboGraph? LoadModdedComboTree(string moddedComboTreeDir)
    {
        if (_provider == null || string.IsNullOrEmpty(_contentPath))
        {
            LogDebug("[IMPORT] Vanilla parser not initialized");
            return null;
        }

        var moddedUasset = Path.Combine(moddedComboTreeDir, "MainChar_ComboTree.uasset");
        var moddedUexp = Path.Combine(moddedComboTreeDir, "MainChar_ComboTree.uexp");

        if (!File.Exists(moddedUasset))
        {
            LogDebug($"[IMPORT] Modded combo tree not found: {moddedUasset}");
            return null;
        }

        var vanillaDir = Path.Combine(_contentPath, "DB", "_MainChar", "Combos");
        var vanillaUasset = Path.Combine(vanillaDir, "MainChar_ComboTree.uasset");
        var vanillaUexp = Path.Combine(vanillaDir, "MainChar_ComboTree.uexp");

        if (!File.Exists(vanillaUasset))
        {
            LogDebug($"[IMPORT] Vanilla combo tree not found: {vanillaUasset}");
            return null;
        }

        var backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VanillaBackup");
        Directory.CreateDirectory(backupDir);

        try
        {
            File.Copy(vanillaUasset, Path.Combine(backupDir, "MainChar_ComboTree.uasset"), true);
            if (File.Exists(vanillaUexp))
                File.Copy(vanillaUexp, Path.Combine(backupDir, "MainChar_ComboTree.uexp"), true);

            LogDebug($"[IMPORT] Backed up vanilla combo tree to {backupDir}");

            File.Copy(moddedUasset, vanillaUasset, true);
            if (File.Exists(moddedUexp))
                File.Copy(moddedUexp, vanillaUexp, true);

            LogDebug("[IMPORT] Swapped in modded combo tree, creating fresh parser (no cache)...");

            using var freshParser = new AnimationParser();
            freshParser.Initialize(_gameRootPath, _contentPath);
            return freshParser.LoadMainCharComboTree();
        }
        finally
        {
            try
            {
                var backupUasset = Path.Combine(backupDir, "MainChar_ComboTree.uasset");
                var backupUexp = Path.Combine(backupDir, "MainChar_ComboTree.uexp");

                if (File.Exists(backupUasset))
                    File.Copy(backupUasset, vanillaUasset, true);
                if (File.Exists(backupUexp) && File.Exists(vanillaUexp))
                    File.Copy(backupUexp, vanillaUexp, true);

                LogDebug("[IMPORT] Restored vanilla combo tree from backup");
            }
            catch (Exception ex)
            {
                LogDebug($"[IMPORT] WARNING: Failed to restore vanilla combo tree: {ex.Message}");
                LogDebug($"[IMPORT] Manual restore available at: {backupDir}");
            }
        }
    }

    private ComboGraph? ParseComboTreeFromObject(UObject obj)
    {
        try
        {
            LogDebug($"[COMBO] Loaded MainChar_ComboTree: exportType={obj.ExportType}");

            var allNodeStructs = new List<FStructFallback>();
            var nodesProp = obj.Properties.FirstOrDefault(p => p.Name.Text == "m_Nodes");
            if (nodesProp?.Tag is ArrayProperty nodesArray)
            {
                foreach (var nodeTag in nodesArray.Value.Properties)
                {
                    if (nodeTag is StructProperty nodeStruct &&
                        nodeStruct.Value.StructType is FStructFallback nodeData)
                        allNodeStructs.Add(nodeData);
                }
            }

            var rawNodes = new List<ComboNode>();
            var treeIndexToNodeIds = new Dictionary<int, List<int>>();
            int nextId = 0;
            for (int i = 0; i < allNodeStructs.Count; i++)
            {
                var variants = ParseComboNodeVariants(allNodeStructs[i], nextId, i);
                treeIndexToNodeIds[i] = new List<int>();
                foreach (var variant in variants)
                {
                    treeIndexToNodeIds[i].Add(variant.Id);
                    rawNodes.Add(variant);
                    nextId++;
                }
                if (variants.Count > 1)
                    LogDebug($"[COMBO] Split tree node {i} into {variants.Count} direction variants: {string.Join(", ", variants.Select(v => $"{v.Name} [{v.DirectionLabel}]"))}");
            }

            LogDebug($"[COMBO] Parsed {rawNodes.Count} raw nodes from {allNodeStructs.Count} tree nodes");

            var rawEdges = new List<ComboEdge>();
            for (int ti = 0; ti < allNodeStructs.Count; ti++)
            {
                var nodeData = allNodeStructs[ti];
                var transitionsProp = nodeData.Properties.FirstOrDefault(p => p.Name.Text == "m_Transitions");
                if (transitionsProp?.Tag is not StructProperty transStruct ||
                    transStruct.Value.StructType is not FStructFallback transData)
                    continue;

                var transArr = transData.Properties.FirstOrDefault(p => p.Name.Text == "m_Transitions");
                if (transArr?.Tag is not ArrayProperty tArr)
                    continue;

                var sourceIds = treeIndexToNodeIds[ti];
                foreach (var elem in tArr.Value.Properties)
                {
                    if (elem is not StructProperty elemStruct ||
                        elemStruct.Value.StructType is not FStructFallback elemData)
                        continue;

                    var inputName = ExtractInputName(elemData);
                    var targetNodesProp = elemData.Properties.FirstOrDefault(p => p.Name.Text == "m_TargetNodes");
                    if (targetNodesProp?.Tag is not MapProperty targetMap)
                        continue;

                    foreach (var kv in targetMap.Value.Properties)
                    {
                        if (kv.Value is IntProperty intProp)
                        {
                            var targetTreeIndex = intProp.Value;
                            if (targetTreeIndex >= 0 && targetTreeIndex < allNodeStructs.Count &&
                                treeIndexToNodeIds.ContainsKey(targetTreeIndex))
                            {
                                var targetIds = treeIndexToNodeIds[targetTreeIndex];
                                foreach (var srcId in sourceIds)
                                {
                                    foreach (var tgtId in targetIds)
                                    {
                                        rawEdges.Add(new ComboEdge
                                        {
                                            FromNodeId = srcId,
                                            ToNodeId = tgtId,
                                            InputName = inputName
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }

            LogDebug($"[COMBO] Parsed {rawEdges.Count} raw edges");

            var conduitIds = new HashSet<int>(
                rawNodes.Where(n => string.IsNullOrEmpty(n.AnimPath) && !n.IsRoot).Select(n => n.Id));

            var remap = new Dictionary<int, List<int>>();
            foreach (var cid in conduitIds)
            {
                var targets = new List<int>();
                var visited = new HashSet<int>();
                var queue = new Queue<int>();
                queue.Enqueue(cid);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (!visited.Add(current)) continue;
                    foreach (var e in rawEdges.Where(e => e.FromNodeId == current))
                    {
                        if (conduitIds.Contains(e.ToNodeId))
                            queue.Enqueue(e.ToNodeId);
                        else
                            targets.Add(e.ToNodeId);
                    }
                }
                remap[cid] = targets.Distinct().ToList();
            }

            var finalEdges = new List<ComboEdge>();
            foreach (var edge in rawEdges)
            {
                var fromIsConduit = conduitIds.Contains(edge.FromNodeId);
                var toIsConduit = conduitIds.Contains(edge.ToNodeId);
                if (fromIsConduit) continue;

                if (toIsConduit && remap.TryGetValue(edge.ToNodeId, out var targets))
                {
                    foreach (var targetId in targets)
                    {
                        finalEdges.Add(new ComboEdge
                        {
                            FromNodeId = edge.FromNodeId,
                            ToNodeId = targetId,
                            InputName = edge.InputName
                        });
                    }
                }
                else if (!toIsConduit)
                {
                    finalEdges.Add(new ComboEdge
                    {
                        FromNodeId = edge.FromNodeId,
                        ToNodeId = edge.ToNodeId,
                        InputName = edge.InputName
                    });
                }
            }

            var keptNodes = rawNodes.Where(n => !conduitIds.Contains(n.Id)).ToList();
            var oldToNew = new Dictionary<int, int>();
            for (int i = 0; i < keptNodes.Count; i++)
            {
                oldToNew[keptNodes[i].Id] = i;
                keptNodes[i].Id = i;
            }

            var graph = new ComboGraph { WeaponName = "BareHands" };
            graph.Nodes.AddRange(keptNodes);

            foreach (var edge in finalEdges)
            {
                if (oldToNew.TryGetValue(edge.FromNodeId, out var newFrom) &&
                    oldToNew.TryGetValue(edge.ToNodeId, out var newTo))
                {
                    graph.Edges.Add(new ComboEdge
                    {
                        FromNodeId = newFrom,
                        ToNodeId = newTo,
                        InputName = edge.InputName
                    });
                }
            }

            LogDebug($"[COMBO] Final: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges (removed {conduitIds.Count} conduits)");

            var incomingInputs = new Dictionary<int, HashSet<string>>();
            foreach (var edge in graph.Edges)
            {
                if (!string.IsNullOrEmpty(edge.InputName))
                {
                    if (!incomingInputs.ContainsKey(edge.ToNodeId))
                        incomingInputs[edge.ToNodeId] = new HashSet<string>();
                    incomingInputs[edge.ToNodeId].Add(edge.InputName);
                }
            }
            foreach (var node in graph.Nodes)
            {
                if (incomingInputs.TryGetValue(node.Id, out var inputs))
                    node.InputLabel = string.Join(" / ", inputs.OrderBy(x => x));
            }

            var firstMoveInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Jab"] = "LMB",
                ["BellyPalm"] = "RMB",
                ["3Hits"] = "LMB after parry",
                ["Disengage"] = "RMB + LMB",
                ["FrontKick"] = "RMB",
                ["GroundPunch"] = "W W + LMB",
                ["Palm Strike"] = "S W + LMB",
                ["2Hits"] = "RMB after parry",
                ["CrookedFoot"] = "Hold RMB after parry",
                ["GetUp"] = "LMB/RMB when knocked",
                ["GuardBreak"] = "Hold LMB",
                ["Invert"] = "Shift + W/A/S/D",
                ["LowKick"] = "S W + RMB",
                ["ResilientAttack"] = "Space + RMB",
                ["Rush Attack Heavy"] = "RMB while running",
                ["Rush Attack Light"] = "LMB while running",
                ["Thrust Kick"] = "W W + RMB",
                ["3Hits 02"] = "LMB after parry",
                ["3Hits 03"] = "LMB after parry",
                ["MultiHit"] = "RMB delay",
                ["Hook"] = "",
                ["PalmPunch02"] = "",
                ["HeadExtPunch02"] = "",
                ["GroinHead_DoubleHit"] = "",
                ["ElbowPlexus"] = "",
                ["BellySlice"] = "",
                ["TripleHit"] = "",
                ["ThroatPlexus_DoubleHit"] = "",
                ["PushKick"] = "",
                ["DoubleElbowStrike"] = "",
                ["MonoElbowStrike"] = "",
                ["DoubleHit_Finisher"] = "",
                ["ShinKick"] = "",
                ["ThroatPunch"] = "",
                ["SpinHighKick"] = "",
                ["HighCrush"] = "",
                ["AoeKnockdown"] = "",
                ["PostGroundPunch"] = "",
                ["PostPushback"] = "",
                ["DizzyPunch"] = "",
                ["GRAB"] = ""
            };
            foreach (var node in graph.Nodes)
            {
                if (!node.IsRoot)
                {
                    foreach (var (key, label) in firstMoveInputs)
                    {
                        if (node.DisplayName.Contains(key, StringComparison.OrdinalIgnoreCase))
                        {
                            node.InputLabel = label;
                            break;
                        }
                    }
                }
            }

            var stanceNode = new ComboNode
            {
                Id = -1,
                Name = "MainChar_Stance",
                DisplayName = "Combat Stance",
                AnimPath = "Game/Animations/MainChar/Locomotion/Man/Barehands/Moving/V1/Lockmove/North/MC_man_barehands_V1_north_tense",
                DefaultAnimPath = "Game/Animations/MainChar/Locomotion/Man/Barehands/Moving/V1/Lockmove/North/MC_man_barehands_V1_north_tense",
                IsRoot = true,
                Depth = -1
            };

            var existingRootIds = graph.Nodes
                .Where(n => n.IsRoot || !graph.Edges.Any(e => e.ToNodeId == n.Id))
                .Select(n => n.Id).ToList();

            foreach (var rootId in existingRootIds)
                graph.Edges.Add(new ComboEdge { FromNodeId = -1, ToNodeId = rootId, InputName = "" });

            graph.Nodes.Insert(0, stanceNode);

            return graph;
        }
        catch (Exception ex)
        {
            LogDebug($"[COMBO] Error parsing combo tree: {ex.Message}");
            return null;
        }
    }

    private static readonly HashSet<string> DirectionSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FL", "FR", "BL", "BR", "F", "B", "L", "R"
    };

    private static string ExtractDirectionLabel(string dbPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(dbPath);
        var lastUnderscore = fileName.LastIndexOf('_');
        if (lastUnderscore < 0) return "";
        var suffix = fileName[(lastUnderscore + 1)..];
        return DirectionSuffixes.Contains(suffix) ? suffix.ToUpperInvariant() : "";
    }

    private string? ResolveAnimationFromDB(string dbPath)
    {
        try
        {
            var dbObj = _provider?.SafeLoadPackageObject<UObject>(dbPath);
            if (dbObj == null) return null;

            var mAttack = dbObj.Properties.FirstOrDefault(p => p.Name.Text == "m_Attack");
            if (mAttack?.Tag is StructProperty attackStruct &&
                attackStruct.Value.StructType is FStructFallback attackData)
            {
                var mAnimation = attackData.Properties.FirstOrDefault(p => p.Name.Text == "m_Animation");
                if (mAnimation?.Tag is ObjectProperty animObj && animObj.Value != null)
                {
                    var resolved = animObj.Value.ResolvedObject;
                    if (resolved != null)
                    {
                        var fullPath = resolved.GetPathName();
                        if (!string.IsNullOrEmpty(fullPath) && fullPath != "None")
                            return NormalizeAnimPath(fullPath);
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private List<ComboNode> ParseComboNodeVariants(FStructFallback nodeStruct, int nextId, int treeIndex)
    {
        var nameProp = nodeStruct.Properties.FirstOrDefault(p => p.Name.Text == "m_Name");
        var nodeName = nameProp?.Tag is NameProperty np ? np.Value.Text : "";
        var isRoot = nodeName.Contains("Conduit", StringComparison.OrdinalIgnoreCase) ||
                     nodeName.Contains("Root", StringComparison.OrdinalIgnoreCase);

        var baseDisplayName = nodeName;
        if (string.IsNullOrEmpty(nodeName) || nodeName == "None")
            baseDisplayName = $"Node_{treeIndex}";
        else
            baseDisplayName = nodeName
                .Replace("MainChar_", "")
                .Replace("_", " ")
                .Replace("Attack barehands ", "")
                .Replace("attack barehands ", "");

        var attacks = new List<(string dbPath, string animPath)>();
        var attackInfosProp = nodeStruct.Properties.FirstOrDefault(p => p.Name.Text == "m_AttackInfos");
        if (attackInfosProp?.Tag is StructProperty attackInfosStruct &&
            attackInfosStruct.Value.StructType is FStructFallback attackInfosData)
        {
            foreach (var ap in attackInfosData.Properties)
            {
                if (ap.Name.Text == "m_Attacks" && ap.Tag is NameProperty apName &&
                    apName.Value.Text != "None" && apName.Value.Text.Contains("/"))
                {
                    var db = NormalizeAnimPath(apName.Value.Text);
                    var anim = ResolveAnimationFromDB(db) ?? "";
                    attacks.Add((db, anim));
                }
            }
        }

        attacks = attacks.GroupBy(a => a.dbPath).Select(g => g.First()).ToList();

        var distinctDirs = attacks
            .Select(a => ExtractDirectionLabel(a.dbPath))
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        bool showDirLabels = distinctDirs.Count > 1;

        if (attacks.Count == 0)
        {
            return new List<ComboNode>
            {
                new ComboNode
                {
                    Id = nextId,
                    TreeIndex = treeIndex,
                    Name = nodeName == "None" ? "" : nodeName,
                    AnimPath = "",
                    DefaultAnimPath = "",
                    DefaultDBPath = "",
                    DisplayName = baseDisplayName,
                    IsRoot = isRoot,
                    DirectionLabel = ""
                }
            };
        }

        var variants = new List<ComboNode>();
        int id = nextId;
        foreach (var (db, anim) in attacks)
        {
            var dir = ExtractDirectionLabel(db);
            var displayName = baseDisplayName;
            if (showDirLabels && !string.IsNullOrEmpty(dir) && !displayName.EndsWith(dir, StringComparison.OrdinalIgnoreCase))
                displayName = $"{baseDisplayName} {dir}";

            variants.Add(new ComboNode
            {
                Id = id++,
                TreeIndex = treeIndex,
                Name = nodeName == "None" ? "" : nodeName,
                AnimPath = anim,
                DefaultAnimPath = anim,
                DefaultDBPath = db,
                DisplayName = displayName,
                IsRoot = isRoot,
                DirectionLabel = dir
            });
        }

        return variants;
    }

    private string ExtractInputName(FStructFallback transitionData)
    {
        var inputProp = transitionData.Properties.FirstOrDefault(p => p.Name.Text == "m_eInputTransition");
        if (inputProp?.Tag is EnumProperty inputEnum)
        {
            var raw = inputEnum.Value.Text;
            var name = raw.Contains("::") ? raw.Split("::")[^1] : raw;

            return name switch
            {
                "Light" => "LMB",
                "Heavy" => "RMB",
                "HeavyHold" => "RMB Hold",
                "HeavyAlt" => "RMB",
                "Special" => "S",
                "Dodge" => "Shift",
                "Throw" => "Q",
                "None" or "" => "",
                _ => name
            };
        }

        return "";
    }

    private static readonly Dictionary<string, string> AnimFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Game/Animations/MainChar/Attacks/Man/Barehands/Skills/LightCombo/MultiHit/MainChar_Attack_Man_Barehands_Skill_LightCombo_MultiHit_FL"] =
            "Game/Animations/MainChar/Attacks/SpecialCombos/AltPakMei/MainChar_Attack_SpecialCombo_AltPakMei_MultiHitClaws",
        ["Game/Animations/MainChar/Attacks/Man/Barehands/Skills/LightCombo/MultiHit/MainChar_Attack_Man_Barehands_Skill_LightCombo_MultiHit_BR"] =
            "Game/Animations/MainChar/Attacks/Man/Barehands/LightCombo/MainChar_Attack_Man_Barehands_Pressure_TripleHit_BR"
    };

    public bool ValidateAnimation(string gamePath)
    {
        if (_provider == null) return false;
        try
        {
            if (gamePath.Contains("DB/AI/Archetypes", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = ResolveAnimationFromDB(gamePath);
                if (string.IsNullOrEmpty(resolved)) return false;
                var obj = _provider.SafeLoadPackageObject<UAnimSequence>(resolved);
                if (obj == null) return false;
                var skeletonObj = obj.Skeleton?.Load<USkeleton>();
                return skeletonObj != null;
            }

            var animObj = _provider.SafeLoadPackageObject<UAnimSequence>(gamePath);
            if (animObj == null && AnimFallbacks.TryGetValue(gamePath, out var fallback))
                animObj = _provider.SafeLoadPackageObject<UAnimSequence>(fallback);
            if (animObj == null) return false;

            var skeleton = animObj.Skeleton?.Load<USkeleton>();
            return skeleton != null;
        }
        catch { return false; }
    }

    public AnimationJsonData? LoadAnimation(string gamePath)
    {
        if (_provider == null) return null;

        if (gamePath.Contains("DB/AI/Archetypes", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = ResolveAnimationFromDB(gamePath);
            if (string.IsNullOrEmpty(resolved)) return null;
            gamePath = resolved;
        }

        try
        {
            LogDebug($"[ANIM] Loading: {gamePath}");
            var obj = _provider.SafeLoadPackageObject<UAnimSequence>(gamePath);
            LogDebug($"[ANIM] Direct load: {(obj != null ? "OK" : "null")}");

            if (obj == null && AnimFallbacks.TryGetValue(gamePath, out var fallback))
                obj = _provider.SafeLoadPackageObject<UAnimSequence>(fallback);

            if (obj == null) return null;

            var skeletonObj = obj.Skeleton?.Load<USkeleton>();
            if (skeletonObj == null)
            {
                if (gamePath.Contains("CrookedFoot", StringComparison.OrdinalIgnoreCase))
                {
                    var grabPath = "Game/Animations/MainChar/Attacks/Man/Barehands/PostParry/PostParry_Knockdown/MainChar_Attack_Man_Barehands_PunishKD_CrookedFoot_Grab_attacker";
                    var grabObj = _provider.SafeLoadPackageObject<UAnimSequence>(grabPath);
                    if (grabObj != null)
                    {
                        var grabSkeleton = grabObj.Skeleton?.Load<USkeleton>();
                        if (grabSkeleton != null)
                        {
                            var grabAnimSet = grabSkeleton.ConvertAnims(grabObj);
                            if (grabAnimSet.Sequences.Count > 0)
                                return BuildAnimationJson(grabAnimSet.Sequences[0], grabSkeleton);
                        }
                    }
                }
                return null;
            }

            var csType = obj.CompressedDataStructure?.GetType().Name ?? "null";
            var hasRaw = obj.RawAnimationData is { Length: > 0 };
            var boneSettingsPath = obj.BoneCompressionSettings?.GetPathName() ?? "null";
            var codecHandle = obj.BoneCodecDDCHandle ?? "null";
            var debugMsg = $"[{DateTime.Now:HH:mm:ss}] [DEBUG] Anim={gamePath}, CompressedType={csType}, HasRaw={hasRaw}, NumFrames={obj.NumFrames}, BoneSettings={boneSettingsPath}, CodecHandle={codecHandle}";
            try { File.AppendAllText(Path.Combine(Directory.GetCurrentDirectory(), "error.log"), debugMsg + "\n"); } catch { }

            var animSet = skeletonObj.ConvertAnims(obj);
            if (animSet.Sequences.Count == 0) return null;

            return BuildAnimationJson(animSet.Sequences[0], skeletonObj);
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            throw new Exception($"Failed to load {gamePath}: {inner.Message}", inner);
        }
    }

    private AnimationJsonData BuildAnimationJson(CAnimSequence seq, USkeleton skeleton)
    {
        var refSkeleton = skeleton.ReferenceSkeleton;
        var boneCount = refSkeleton.FinalRefBoneInfo.Length;

        var boneDataList = new BoneData[boneCount];
        var bindPoseList = new BindPoseTransform[boneCount];

        for (int i = 0; i < boneCount; i++)
        {
            var info = refSkeleton.FinalRefBoneInfo[i];
            var pose = refSkeleton.FinalRefBonePose[i];

            boneDataList[i] = new BoneData
            {
                name = info.Name.Text,
                parent = info.ParentIndex
            };

            bindPoseList[i] = new BindPoseTransform
            {
                position = new PosVec
                {
                    x = pose.Translation.X,
                    y = pose.Translation.Z,
                    z = pose.Translation.Y
                },
                rotation = new RotVec
                {
                    x = -pose.Rotation.X,
                    y = -pose.Rotation.Z,
                    z = -pose.Rotation.Y,
                    w = pose.Rotation.W
                }
            };
        }

        int numFrames = seq.NumFrames;
        float fps = seq.FramesPerSecond > 0 ? seq.FramesPerSecond : 30f;
        float duration = seq.AnimEndTime > 0 ? seq.AnimEndTime : (numFrames > 1 ? (numFrames - 1) / fps : 1f / fps);

        var tracksList = new List<AnimTrackData>();

        for (int boneIdx = 0; boneIdx < boneCount && boneIdx < seq.Tracks.Count; boneIdx++)
        {
            var track = seq.Tracks[boneIdx];
            if (track == null || !track.HasKeys()) continue;

            if (track.KeyQuat.Length == 0 && track.KeyPos.Length == 0) continue;

            var boneName = refSkeleton.FinalRefBoneInfo[boneIdx].Name.Text;
            var trackData = new AnimTrackData
            {
                boneName = boneName,
                boneIndex = boneIdx,
                times = new float[numFrames],
                positions = new PosVec[numFrames],
                rotations = new RotVec[numFrames],
                scales = new PosVec[numFrames]
            };

            var dstQuat = new CUE4Parse.UE4.Objects.Core.Math.FQuat(0, 0, 0, 1);
            var dstPos = new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0);
            var dstScale = new CUE4Parse.UE4.Objects.Core.Math.FVector(1, 1, 1);

            for (int frame = 0; frame < numFrames; frame++)
            {
                dstQuat = new CUE4Parse.UE4.Objects.Core.Math.FQuat(0, 0, 0, 1);
                dstPos = new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0);
                dstScale = new CUE4Parse.UE4.Objects.Core.Math.FVector(1, 1, 1);

                track.GetBoneTransform(frame, numFrames, ref dstQuat, ref dstPos, ref dstScale);

                trackData.times[frame] = (float)frame / fps;
                trackData.positions[frame] = new PosVec
                {
                    x = dstPos.X,
                    y = dstPos.Z,
                    z = dstPos.Y
                };
                trackData.rotations[frame] = new RotVec
                {
                    x = -dstQuat.X,
                    y = -dstQuat.Z,
                    z = -dstQuat.Y,
                    w = dstQuat.W
                };
                trackData.scales[frame] = new PosVec
                {
                    x = dstScale.X,
                    y = dstScale.Z,
                    z = dstScale.Y
                };
            }

            tracksList.Add(trackData);
        }

        var animData = new AnimationJson
        {
            name = seq.Name,
            duration = duration,
            numFrames = numFrames,
            fps = fps,
            tracks = tracksList.ToArray()
        };

        return new AnimationJsonData
        {
            skeleton = new SkeletonJson
            {
                bones = boneDataList,
                bindPose = bindPoseList
            },
            animation = animData
        };
    }

    public MeshJsonData? LoadMesh(string character)
    {
        if (_provider == null) return null;

        string? meshPath = null;
        if (CharacterMeshPaths.TryGetValue(character, out var mappedPath))
        {
            meshPath = mappedPath;
        }
        else
        {
            meshPath = FindMeshPath(character);
        }

        if (meshPath == null)
        {
            LogDebug($"[MESH] No mesh path found for character '{character}'");
            return null;
        }

        try
        {
            var obj = _provider.SafeLoadPackageObject<USkeletalMesh>(meshPath);
            if (obj == null)
            {
                LogDebug($"[MESH] Failed to load package: {meshPath}");
                return null;
            }

            if (!obj.TryConvert(out var convertedMesh))
            {
                LogDebug($"[MESH] TryConvert failed: {meshPath}");
                return null;
            }

            var lod = convertedMesh.LODs.FirstOrDefault();
            if (lod == null) return null;

            var skelLod = lod as CSkelMeshLod;
            if (skelLod == null) return null;

            var vertexCount = skelLod.Verts.Length;
            var positions = new float[vertexCount * 3];
            var normals = new float[vertexCount * 3];
            var uvs = new float[vertexCount * 2];
            var skinIndicesArr = new int[vertexCount * 4];
            var skinWeightsArr = new float[vertexCount * 4];

            for (int i = 0; i < vertexCount; i++)
            {
                var v = skelLod.Verts[i];
                positions[i * 3] = v.Position.X;
                positions[i * 3 + 1] = v.Position.Z;
                positions[i * 3 + 2] = v.Position.Y;

                normals[i * 3] = v.Normal.X;
                normals[i * 3 + 1] = v.Normal.Z;
                normals[i * 3 + 2] = v.Normal.Y;

                uvs[i * 2] = v.UV.U;
                uvs[i * 2 + 1] = 1.0f - v.UV.V;

                int infCount = Math.Min(4, v.Influences.Count);
                float totalWeight = 0;
                for (int j = 0; j < infCount; j++)
                {
                    skinIndicesArr[i * 4 + j] = v.Influences[j].Bone;
                    skinWeightsArr[i * 4 + j] = v.Influences[j].Weight;
                    totalWeight += v.Influences[j].Weight;
                }
                if (totalWeight > 0)
                {
                    for (int j = 0; j < infCount; j++)
                        skinWeightsArr[i * 4 + j] /= totalWeight;
                }
            }

            var allIndices = new List<int>();
            foreach (var idx in skelLod.Indices.Value)
            {
                allIndices.Add((int)idx);
            }

            var skeleton = convertedMesh.RefSkeleton;
            var boneNames = new string[skeleton.Count];
            var boneParents = new int[skeleton.Count];
            var bindPoseArr = new float[skeleton.Count * 7];

            for (int i = 0; i < skeleton.Count; i++)
            {
                var bone = skeleton[i];
                boneNames[i] = bone.Name.Text;
                boneParents[i] = bone.ParentIndex;

                bindPoseArr[i * 7 + 0] = bone.Position.X;
                bindPoseArr[i * 7 + 1] = bone.Position.Z;
                bindPoseArr[i * 7 + 2] = bone.Position.Y;
                bindPoseArr[i * 7 + 3] = -bone.Orientation.X;
                bindPoseArr[i * 7 + 4] = -bone.Orientation.Z;
                bindPoseArr[i * 7 + 5] = -bone.Orientation.Y;
                bindPoseArr[i * 7 + 6] = bone.Orientation.W;
            }

            LogDebug($"[MESH] Loaded '{character}' from {meshPath} ({vertexCount} verts, {skeleton.Count} bones)");
            return new MeshJsonData
            {
                positions = positions,
                normals = normals,
                uvs = uvs,
                indices = allIndices.ToArray(),
                skinIndices = skinIndicesArr,
                skinWeights = skinWeightsArr,
                boneNames = boneNames,
                bindPose = bindPoseArr,
                boneParents = boneParents
            };
        }
        catch (Exception ex)
        {
            LogDebug($"[MESH] Error loading mesh for {character}: {ex.Message}");
            return null;
        }
    }

    private string? FindMeshPath(string character)
    {
        try
        {
            var charactersDir = Path.Combine(_contentPath, "Characters");
            if (!Directory.Exists(charactersDir)) return null;

            var searchPatterns = new[]
            {
                $"SK_*{character}*",
                $"SK_Puppet_{character}*"
            };

            foreach (var pattern in searchPatterns)
            {
                var files = Directory.GetFiles(charactersDir, pattern + ".uasset", SearchOption.AllDirectories);
                var match = files.FirstOrDefault(f => !f.Contains("PhysicsAsset") && !f.Contains("Cloth") && !f.Contains("Cine") && !f.Contains("Skeleton"));
                if (match != null)
                {
                    var relPath = match.Substring(_contentPath.Length).TrimStart('\\', '/').Replace('\\', '/');
                    relPath = relPath.Replace(".uasset", "");
                    return "Game/" + relPath;
                }
            }

            var fallback = Directory.GetFiles(charactersDir, $"SK_{character}*.uasset", SearchOption.AllDirectories)
                .FirstOrDefault(f => !f.Contains("PhysicsAsset") && !f.Contains("Cloth") && !f.Contains("Cine") && !f.Contains("Skeleton"));
            if (fallback != null)
            {
                var relPath = fallback.Substring(_contentPath.Length).TrimStart('\\', '/').Replace('\\', '/');
                relPath = relPath.Replace(".uasset", "");
                return "Game/" + relPath;
            }
        }
        catch { }
        return null;
    }

    public string ToJson(AnimationJsonData data)
    {
        return JsonConvert.SerializeObject(data, Formatting.None);
    }

    public string ToJson(MeshJsonData data)
    {
        return JsonConvert.SerializeObject(data, Formatting.None);
    }

    public void Dispose()
    {
        _provider = null;
    }
}
