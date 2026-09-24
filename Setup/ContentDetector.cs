using System;
using System.IO;
using System.Linq;

namespace SifuMovesetEditor.Setup;

public static class ContentDetector
{
    public record DetectionResult(bool IsValid, string Reason, string? ContentPath);

    /// <summary>
    /// Returns the folder that holds Animations/DB/etc.
    /// Accepts a content root (e.g. ...\GameContent or ...\Sifu) or the Content folder itself.
    /// Only treats the path as already-Content when the last segment is exactly "Content"
    /// (so "GameContent" is NOT mistaken for Content).
    /// </summary>
    public static string ResolveContentDir(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return path ?? "";

        var leaf = Path.GetFileName(path.TrimEnd('\\', '/'));
        return leaf.Equals("Content", StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.Combine(path, "Content");
    }

    /// <summary>
    /// Finds the folder that actually contains Content\Animations.
    /// Accepts either the Sifu/content root itself or a parent that holds Sifu\Content\Animations
    /// (e.g. pakchunk0-WindowsNoEditor with both Engine\ and Sifu\ side by side).
    /// </summary>
    public static string? ResolveContentRoot(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return null;

        if (Directory.Exists(Path.Combine(path, "Content", "Animations")))
            return path;

        var sifu = Path.Combine(path, "Sifu");
        if (Directory.Exists(Path.Combine(sifu, "Content", "Animations")))
            return sifu;

        return null;
    }

    /// <summary>
    /// Checks if the tool has the game content it needs.
    /// </summary>
    /// <param name="contentPath">
    /// Either the Sifu game root (e.g., "C:\...\Sifu") or an extracted content root
    /// (e.g., "C:\...\pakchunk0-WindowsNoEditor").
    /// Content/ may sit directly under this path or under a Sifu\ child.
    /// </param>
    public static DetectionResult Detect(string? contentPath)
    {
        if (string.IsNullOrEmpty(contentPath))
            return new DetectionResult(false, "No content path configured.", null);

        if (!Directory.Exists(contentPath))
            return new DetectionResult(false, $"Directory not found: {contentPath}", contentPath);

        var originalPath = contentPath;
        var resolved = ResolveContentRoot(contentPath);
        if (resolved != null)
            contentPath = resolved;

        // Check for Content/ subdirectory
        var contentDir = Path.Combine(contentPath, "Content");
        if (!Directory.Exists(contentDir))
        {
            // Pak present but never unpacked?
            if (IsGameInstallation(originalPath) ||
                IsGameInstallation(Path.GetDirectoryName(originalPath) ?? originalPath) ||
                ContentExtractor.FindPakFile(originalPath) != null)
                return new DetectionResult(false,
                    "Pak file found but content is not extracted. Use Setup to unpack pakchunk0 (UnrealPak).",
                    originalPath);

            return new DetectionResult(false, $"Content subdirectory not found: {contentDir}", originalPath);
        }

        // Check for critical animation directory
        var animsDir = Path.Combine(contentDir, "Animations");
        if (!Directory.Exists(animsDir))
        {
            if (ContentExtractor.FindPakFile(contentPath) != null ||
                ContentExtractor.FindPakFile(Path.GetDirectoryName(contentPath) ?? contentPath) != null)
                return new DetectionResult(false,
                    "Pak file found but Animations/ missing. Re-extract vanilla content via Setup.",
                    contentPath);

            return new DetectionResult(false, "Animations/ directory not found. Game content may not be extracted.", contentPath);
        }

        // Check for combo tree DB
        var comboTree = Path.Combine(contentDir, "DB", "_MainChar", "Combos", "MainChar_ComboTree.uasset");
        if (!File.Exists(comboTree))
            return new DetectionResult(false, "MainChar_ComboTree.uasset not found.", contentPath);

        // Check that there are actual animation files (not just empty dirs)
        var mainCharAttacks = Path.Combine(animsDir, "MainChar", "Attacks");
        if (!Directory.Exists(mainCharAttacks))
            return new DetectionResult(false, "Animations/MainChar/Attacks/ not found.", contentPath);

        var animFiles = Directory.GetFiles(mainCharAttacks, "*.uasset", SearchOption.AllDirectories);
        if (animFiles.Length == 0)
            return new DetectionResult(false, "No animation files found in MainChar/Attacks/.", contentPath);

        // Check for archetype DBs (Unit Properties silently reads empty without these)
        var archDir = Path.Combine(contentDir, "DB", "AI", "Archetypes");
        if (!Directory.Exists(archDir) ||
            !Directory.EnumerateFiles(archDir, "*.uasset", SearchOption.AllDirectories).Any())
            return new DetectionResult(false, "DB/AI/Archetypes not found (needed for Unit Properties). Re-extract game content.", contentPath);

        // Check for movement DB (stance switching needs BaseMovementDB)
        var movementDb = Path.Combine(contentDir, "DB", "Movement", "BaseMovementDB.uasset");
        if (!File.Exists(movementDb))
            return new DetectionResult(false, "DB/Movement/BaseMovementDB.uasset not found (needed for stance switching). Re-extract game content.", contentPath);

        // Check for global attack DBs
        var attacksDbDir = Path.Combine(contentDir, "DB", "Attacks");
        if (!Directory.Exists(attacksDbDir) ||
            !Directory.EnumerateFiles(attacksDbDir, "*.uasset", SearchOption.AllDirectories).Any())
            return new DetectionResult(false, "DB/Attacks not found (needed for attack data). Re-extract game content.", contentPath);

        // Base skeleton — every animation imports Game/Characters/Skeleton/Base_skeleton
        var skeleton = Path.Combine(contentDir, "Characters", "Skeleton", "Base_skeleton.uasset");
        if (!File.Exists(skeleton))
            return new DetectionResult(false,
                "Characters/Skeleton/Base_skeleton.uasset not found (animations need it). Re-extract game content.", contentPath);

        // Engine animation compression settings — may sit under this root or its parent
        if (!HasEngineAnimSettings(contentPath) &&
            !HasEngineAnimSettings(Directory.GetParent(contentPath)?.FullName))
            return new DetectionResult(false,
                "Engine animation compression settings not found (DefaultAnimBoneCompressionSettings). Re-extract with Engine/ included.", contentPath);

        return new DetectionResult(true, "", contentPath);
    }

    private static bool HasEngineAnimSettings(string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        return File.Exists(Path.Combine(root, "Engine", "Content", "Animation",
            "DefaultAnimBoneCompressionSettings.uasset"));
    }

    /// <summary>
    /// Checks if a directory looks like a Sifu game installation (has the pak file).
    /// Accepts: parent of Sifu, Sifu install root, or a Paks folder.
    /// </summary>
    public static bool IsGameInstallation(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return false;

        var paksCandidates = new[]
        {
            Path.Combine(path, "Sifu", "Content", "Paks"),
            Path.Combine(path, "Content", "Paks"),
            path,
        };

        foreach (var paksDir in paksCandidates)
        {
            if (Directory.Exists(paksDir) &&
                Directory.GetFiles(paksDir, "pakchunk0-*.pak").Length > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a directory looks like extracted game content (has Content/ with animations).
    /// </summary>
    public static bool IsExtractedContent(string path)
    {
        var contentDir = Path.Combine(path, "Content");
        if (!Directory.Exists(contentDir)) return false;

        var animsDir = Path.Combine(contentDir, "Animations");
        return Directory.Exists(animsDir);
    }
}
