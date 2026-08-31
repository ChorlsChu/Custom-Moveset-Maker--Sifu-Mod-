using System;
using System.IO;

namespace SifuMovesetEditor.Setup;

public static class ContentDetector
{
    public record DetectionResult(bool IsValid, string Reason, string? ContentPath);

    /// <summary>
    /// Checks if the tool has the game content it needs.
    /// </summary>
    /// <param name="contentPath">
    /// Either the Sifu game root (e.g., "C:\...\Sifu") or an extracted content root
    /// (e.g., "C:\...\pakchunk0-WindowsNoEditor").
    /// The Content/ subdirectory must exist under this path.
    /// </param>
    public static DetectionResult Detect(string? contentPath)
    {
        if (string.IsNullOrEmpty(contentPath))
            return new DetectionResult(false, "No content path configured.", null);

        if (!Directory.Exists(contentPath))
            return new DetectionResult(false, $"Directory not found: {contentPath}", contentPath);

        // Check for Content/ subdirectory
        var contentDir = Path.Combine(contentPath, "Content");
        if (!Directory.Exists(contentDir))
            return new DetectionResult(false, $"Content subdirectory not found: {contentDir}", contentPath);

        // Check for critical animation directory
        var animsDir = Path.Combine(contentDir, "Animations");
        if (!Directory.Exists(animsDir))
            return new DetectionResult(false, "Animations/ directory not found. Game content may not be extracted.", contentPath);

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

        return new DetectionResult(true, "", contentPath);
    }

    /// <summary>
    /// Checks if a directory looks like a Sifu game installation (has the pak file).
    /// </summary>
    public static bool IsGameInstallation(string path)
    {
        var paksDir = Path.Combine(path, "Sifu", "Content", "Paks");
        if (!Directory.Exists(paksDir)) return false;

        return Directory.GetFiles(paksDir, "pakchunk0-*.pak").Length > 0;
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
