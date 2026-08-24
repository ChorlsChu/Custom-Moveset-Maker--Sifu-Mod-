using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
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
        ["Grunt"] = "Game/Characters/PNJ/Grunt/M/Meshes/SK_Grunt_M_Hideout03_BodyGuard_A",
        ["FireDisciple"] = "Game/Characters/PNJ/Disicple/M/Meshes/SK_Disicple_M",
        ["FlashKick"] = "Game/Characters/PNJ/FlashKick/M/Meshes/SK_FlashKick_M",
        ["BigGuy"] = "Game/Characters/PNJ/BigGuy/M/Meshes/SK_BigGuy_M",
        ["BodyGuard"] = "Game/Characters/PNJ/BodyGuards/M/Meshes/SK_BodyGuards_M",
        ["Servant"] = "Game/Characters/PNJ/Servant/M/Meshes/SK_Servant_M",
        ["Fajar"] = "Game/Characters/Boss/Fajar/Meshes/SK_Fajar_M",
        ["Sean"] = "Game/Characters/Boss/Sean/Meshes/SK_Sean_M",
        ["Kuroki"] = "Game/Characters/Boss/Kuroki/Meshes/SK_Kuroki_M",
        ["Yang"] = "Game/Characters/Boss/Yang/Meshes/SK_Yang_M",
        ["Fengjie"] = "Game/Characters/Boss/Fengjie/Meshes/SK_Fengjie_M",
        ["MainChar"] = "Game/Characters/MainChar/Meshes/SK_MainChar_M",
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
                var weaponType = Path.GetFileName(weaponDir);
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

    public AnimationJsonData? LoadAnimation(string gamePath)
    {
        if (_provider == null) return null;

        try
        {
            var obj = _provider.LoadPackageObject<UAnimSequence>(gamePath);
            if (obj == null) return null;

            var skeletonObj = obj.Skeleton?.Load<USkeleton>();
            if (skeletonObj == null) return null;

            var csType = obj.CompressedDataStructure?.GetType().Name ?? "null";
            var hasRaw = obj.RawAnimationData is { Length: > 0 };
            var debugMsg = $"[{DateTime.Now:HH:mm:ss}] [DEBUG] Anim={gamePath}, CompressedType={csType}, HasRaw={hasRaw}, NumFrames={obj.NumFrames}";
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
        if (!CharacterMeshPaths.TryGetValue(character, out var meshPath)) return null;

        try
        {
            var obj = _provider.LoadPackageObject<USkeletalMesh>(meshPath);
            if (obj == null) return null;

            if (!obj.TryConvert(out var convertedMesh)) return null;

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
            System.Diagnostics.Debug.WriteLine($"Error loading mesh for {character}: {ex.Message}");
            return null;
        }
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
