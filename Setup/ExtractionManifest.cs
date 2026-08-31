using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SifuMovesetEditor.Setup;

public static class ExtractionManifest
{
    // Directories to scan recursively (relative to Content/)
    // Each entry produces .uasset + .uexp pairs
    public static readonly string[] DirectoryPatterns =
    [
        "Animations",           // All character animations (~405 MB)
        "DB/_MainChar/Combos",  // Combo tree definitions (~3.3 MB)
        "DB/AI/Archetypes",     // NPC/Boss attack tables (~14.6 MB)
        "DB/Attacks",           // Global attack DBs like WUGUAN (~0.1 MB)
    ];

    // Specific directories to scan for character meshes
    // (we only need SK_ files, not the entire Characters/ tree)
    public static readonly string[] MeshDirectories =
    [
        "Characters/MainChar/M/Meshes",
        "Characters/PNJ/Grunt/M/Meshes",
        "Characters/PNJ/Disicple/M/Meshes",
        "Characters/PNJ/FlashKick/Meshes",
        "Characters/PNJ/BigGuy/Meshes",
        "Characters/PNJ/BodyGuards/M/Meshes",
        "Characters/PNJ/Servant/M/Meshes",
        "Characters/Boss/Fajar/Meshes",
        "Characters/Boss/Sean/Meshes",
        "Characters/Boss/Kuroki/Meshes",
        "Characters/Boss/Yang/Meshes",
        "Characters/Boss/Fengjie/Meshes",
    ];

    // Engine content needed for animation decompression
    public static readonly string[] EnginePaths =
    [
        "Engine/Content/Animation/DefaultAnimBoneCompressionSettings",
        "Engine/Content/Animation/DefaultAnimCurveCompressionSettings",
    ];

    /// <summary>
    /// Returns all file paths (relative to the Content/ or Engine/ parent)
    /// that the tool needs. Each path should have .uasset and .uexp appended.
    /// </summary>
    public static List<string> GetAllNeededPaths(string extractedRoot)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contentDir = Path.Combine(extractedRoot, "Content");

        // 1. Scan directory patterns recursively
        foreach (var pattern in DirectoryPatterns)
        {
            var dir = Path.Combine(contentDir, pattern);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*.uasset", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(extractedRoot, file)
                    .Replace('\\', '/')
                    .Replace(".uasset", "");
                paths.Add(relative);
            }
        }

        // 2. Scan mesh directories (SK_ files only)
        foreach (var meshDir in MeshDirectories)
        {
            var dir = Path.Combine(contentDir, meshDir);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "SK_*.uasset", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(extractedRoot, file)
                    .Replace('\\', '/')
                    .Replace(".uasset", "");
                paths.Add(relative);
            }
        }

        // 3. Engine content
        foreach (var enginePath in EnginePaths)
        {
            var fullPath = Path.Combine(extractedRoot, enginePath + ".uasset");
            if (File.Exists(fullPath))
            {
                paths.Add(enginePath);
            }
        }

        return paths.OrderBy(p => p).ToList();
    }

    /// <summary>
    /// Estimates total bytes needed by counting existing files.
    /// </summary>
    public static (int fileCount, long totalBytes) EstimateSize(string extractedRoot)
    {
        int count = 0;
        long total = 0;

        var paths = GetAllNeededPaths(extractedRoot);
        foreach (var path in paths)
        {
            var uassetPath = Path.Combine(extractedRoot, path.Replace("Game/", "Content/") + ".uasset");
            var uexpPath = Path.Combine(extractedRoot, path.Replace("Game/", "Content/") + ".uexp");

            if (File.Exists(uassetPath))
            {
                count++;
                total += new FileInfo(uassetPath).Length;
            }
            if (File.Exists(uexpPath))
            {
                count++;
                total += new FileInfo(uexpPath).Length;
            }
        }

        return (count, total);
    }
}
