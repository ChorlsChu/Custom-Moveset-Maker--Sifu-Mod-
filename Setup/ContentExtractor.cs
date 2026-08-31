using System;
using System.IO;
using System.Linq;

namespace SifuMovesetEditor.Setup;

public static class ContentExtractor
{
    /// <summary>
    /// Copies needed files from an already-extracted content directory.
    /// </summary>
    /// <param name="sourceRoot">
    /// The extracted content root (e.g., "...\pakchunk0-WindowsNoEditor").
    /// Should contain Content/ subdirectory.
    /// </param>
    /// <param name="targetDir">Where to copy files (e.g., "{appDir}/GameContent").</param>
    /// <param name="progress">Reports (copied, total, currentFilePath).</param>
    /// <returns>Number of files copied.</returns>
    public static int CopyFromExtracted(string sourceRoot, string targetDir,
        IProgress<(int copied, int total, string currentFile)>? progress = null)
    {
        var paths = ExtractionManifest.GetAllNeededPaths(sourceRoot);
        int copied = 0;

        Directory.CreateDirectory(targetDir);

        foreach (var gamePath in paths)
        {
            var relativePath = gamePath
                .Replace("Game/", "Content/")
                .Replace('/', Path.DirectorySeparatorChar);

            var sourceUasset = Path.Combine(sourceRoot, relativePath + ".uasset");
            var sourceUexp = Path.Combine(sourceRoot, relativePath + ".uexp");

            var targetUasset = Path.Combine(targetDir, relativePath + ".uasset");
            var targetUexp = Path.Combine(targetDir, relativePath + ".uexp");

            CopyFileIfExists(sourceUasset, targetUasset);
            CopyFileIfExists(sourceUexp, targetUexp);

            copied++;

            // Show the relative path for display
            var displayPath = gamePath.Replace("Game/", "");
            progress?.Report((copied, paths.Count, displayPath));
        }

        return copied;
    }

    /// <summary>
    /// Attempts to extract files directly from a .pak file using UnrealPak.
    /// Returns true if extraction succeeded.
    /// </summary>
    public static bool TryExtractFromPak(string gameDir, string targetDir,
        IProgress<(int copied, int total, string currentFile)>? progress = null)
    {
        try
        {
            var paksDir = Path.Combine(gameDir, "Sifu", "Content", "Paks");
            if (!Directory.Exists(paksDir)) return false;

            var pakFiles = Directory.GetFiles(paksDir, "pakchunk0-*.pak");
            if (pakFiles.Length == 0) return false;

            var pakFile = pakFiles[0];
            var unrealPakPath = FindUnrealPak();
            if (unrealPakPath == null) return false;

            progress?.Report((0, 1, "Starting UnrealPak..."));

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = unrealPakPath,
                Arguments = $"\"{pakFile}\" -Extract=\"{targetDir}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return false;

            process.WaitForExit(60000);

            var extractedContent = Path.Combine(targetDir, "Sifu", "Content");
            var success = Directory.Exists(extractedContent) &&
                          Directory.Exists(Path.Combine(extractedContent, "Animations"));

            if (success)
                progress?.Report((1, 1, "Extraction complete"));

            return success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Generates manual extraction instructions.
    /// </summary>
    public static string GetManualExtractionInstructions()
    {
        return @"Automatic extraction failed. To set up manually:

1. Download FModel from https://fmodel.app/
2. Open FModel → Settings → set UE Version to 4.26
3. Go to Settings → Accounts → add ""Unreal Engine"" account
4. Open pakchunk0-WindowsNoEditor.pak from:
   C:\Program Files\Epic Games\Sifu\Sifu\Content\Paks\
5. Extract these folders to a new directory:
   - Animations/ (all characters)
   - DB/_MainChar/Combos/
   - DB/AI/Archetypes/
   - DB/Attacks/
   - Characters/MainChar/M/Meshes/SK_M_MainChar_01.*
   - Characters/PNJ/ (Grunt, Disicple, FlashKick, BigGuy, BodyGuards, Servant)
   - Characters/Boss/ (Fajar, Sean, Kuroki, Yang, Fengjie)
6. Point this tool to the extracted folder";
    }

    private static void CopyFileIfExists(string source, string target)
    {
        if (!File.Exists(source)) return;

        var targetDir = Path.GetDirectoryName(target);
        if (targetDir != null) Directory.CreateDirectory(targetDir);

        File.Copy(source, target, overwrite: true);
    }

    private static string? FindUnrealPak()
    {
        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "UnrealPak.exe"),
            @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\4.26\UE4\UnrealPak\UnrealPak.exe",
            "UnrealPak",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
