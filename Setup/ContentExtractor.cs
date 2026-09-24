using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SifuMovesetEditor.Setup;

public static class ContentExtractor
{
    public static string? CustomUnrealPakPath { get; set; }
    public static string? CustomCryptoJsonPath { get; set; }

    // Sifu pak AES-256 key (publicly shared by modders). Hex 0x40A266F41FDBCE91312FBB86060D2E9425B7D922C0CF0031F634CAD9AECB49DA
    // matches base64 below.
    private const string EmbeddedPakKeysJson =
        """
        {
           "$types":{
              "UnrealBuildTool.EncryptionAndSigning+CryptoSettings, UnrealBuildTool, Version=4.0.0.0, Culture=neutral, PublicKeyToken=null":"1",
              "UnrealBuildTool.EncryptionAndSigning+EncryptionKey, UnrealBuildTool, Version=4.0.0.0, Culture=neutral, PublicKeyToken=null":"2",
              "UnrealBuildTool.EncryptionAndSigning+SigningKeyPair, UnrealBuildTool, Version=4.0.0.0, Culture=neutral, PublicKeyToken=null":"3",
              "UnrealBuildTool.EncryptionAndSigning+SigningKey, UnrealBuildTool, Version=4.0.0.0, Culture=neutral, PublicKeyToken=null":"4"
           },
           "$type":"1",
           "EncryptionKey":{
              "$type":"2",
              "Name":"null",
              "Guid":"null",
              "Key":"QKJm9B/bzpExL7uGBg0ulCW32SLAzwAx9jTK2a7LSdo="
           },
           "SigningKey": null,
           "bEnablePakSigning":true,
           "bEnablePakIndexEncryption":true,
           "bEnablePakIniEncryption":true,
           "bEnablePakUAssetEncryption":true,
           "bEnablePakFullAssetEncryption":false,
           "bDataCryptoRequired":true,
           "PakEncryptionRequired":true,
           "PakSigningRequired":true,
           "SecondaryEncryptionKeys":[

           ]
        }
        """;

    /// <summary>
    /// Returns a path to a usable pak-keys file for UnrealPak -CryptoKeys.
    /// Prefers an existing external file; otherwise writes the embedded key to the app folder.
    /// </summary>
    public static string? EnsurePakKeysFile()
    {
        var existing = FindCryptoJson(FindUnrealPak());
        if (existing != null)
            return existing;

        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Crypto.json");
            File.WriteAllText(path, EmbeddedPakKeysJson);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Copies needed files from an already-extracted content directory.
    /// Returns number of asset files (uasset) actually copied; throws if source has nothing.
    /// </summary>
    public static int CopyFromExtracted(string sourceRoot, string targetDir,
        IProgress<(int copied, int total, string currentFile)>? progress = null)
    {
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Source not found: {sourceRoot}");

        var contentDir = Path.Combine(sourceRoot, "Content");

        var stageAnim = CountUassets(Path.Combine(contentDir, "Animations"));
        var stageArch = CountUassets(Path.Combine(contentDir, "DB", "AI", "Archetypes"));
        var stageCombo = CountUassets(Path.Combine(contentDir, "DB", "_MainChar", "Combos"));
        var stageSk = CountMeshUassets(contentDir);
        var manifestCount = ExtractionManifest.GetAllNeededPaths(sourceRoot).Count;

        var jobs = new List<(string Source, string Rel)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFile(string sourceFile)
        {
            var rel = Path.GetRelativePath(sourceRoot, sourceFile).Replace('\\', '/');
            if (!seen.Add(rel)) return;
            jobs.Add((sourceFile, rel));
        }

        void ScanDir(string dir, string searchPattern)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (var file in Directory.GetFiles(dir, searchPattern, SearchOption.AllDirectories))
                    AddFile(file);
            }
            catch (Exception ex)
            {
                SetupLog($"Enumerate failed ({dir}): {ex.Message}");
            }
        }

        foreach (var pattern in ExtractionManifest.DirectoryPatterns)
            ScanDir(Path.Combine(contentDir, pattern.Replace('/', Path.DirectorySeparatorChar)), "*.uasset");

        foreach (var meshDir in ExtractionManifest.MeshDirectories)
            ScanDir(Path.Combine(contentDir, meshDir.Replace('/', Path.DirectorySeparatorChar)), "SK_*.uasset");

        foreach (var enginePath in ExtractionManifest.EnginePaths)
        {
            var src = ExtractionManifest.ResolveSourceFile(sourceRoot, enginePath, ".uasset");
            if (FileExistsLong(src) && seen.Add(enginePath))
                jobs.Add((src, enginePath + ".uasset"));
        }

        SetupLog(
            $"Pre-copy stageAnim={stageAnim} stageArch={stageArch} stageCombo={stageCombo} " +
            $"stageSK={stageSk} manifestPaths={manifestCount} jobs={jobs.Count}");

        if (jobs.Count == 0)
            throw new InvalidOperationException(
                "No source files found — extract the pak first, or point to a complete extracted Content tree.");

        Directory.CreateDirectory(targetDir);

        int copied = 0;
        int total = jobs.Count;

        foreach (var (source, rel) in jobs)
        {
            var target = Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar));
            var any = CopyFileIfExists(source, target);
            any |= CopyFileIfExists(Path.ChangeExtension(source, ".uexp"), Path.ChangeExtension(target, ".uexp"));

            if (any)
                copied++;

            var display = rel.StartsWith("Game/", StringComparison.OrdinalIgnoreCase)
                ? rel.Substring(5)
                : rel;
            progress?.Report((copied, total, display));
        }

        if (copied == 0)
            throw new InvalidOperationException(
                "No source files found — extract the pak first, or point to a complete extracted Content tree.");

        return copied;
    }

    /// <summary>
    /// Finds pakchunk0 under a Sifu install root ({root}/Sifu/Content/Paks) or Paks folder itself.
    /// </summary>
    public static string? FindPakFile(string path)
    {
        try
        {
            if (File.Exists(path) &&
                path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                return path;

            var paksDirs = new[]
            {
                Path.Combine(path, "Sifu", "Content", "Paks"),
                Path.Combine(path, "Content", "Paks"),
                path,
            };

            foreach (var paksDir in paksDirs)
            {
                if (!Directory.Exists(paksDir)) continue;
                var paks = Directory.GetFiles(paksDir, "pakchunk0-*.pak");
                if (paks.Length > 0)
                    return paks.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }
        }
        catch { }
        return null;
    }

    public static string? FindCryptoJson(string? unrealPakPath = null)
    {
        if (!string.IsNullOrEmpty(CustomCryptoJsonPath) && File.Exists(CustomCryptoJsonPath))
            return CustomCryptoJsonPath;

        if (!string.IsNullOrEmpty(unrealPakPath))
        {
            var beside = Path.Combine(Path.GetDirectoryName(unrealPakPath) ?? "", "Crypto.json");
            if (File.Exists(beside)) return beside;
        }

        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var local = Path.Combine(appDir, "Crypto.json");
        if (File.Exists(local)) return local;

        return null;
    }

    // Path wildcard filters for UnrealPak -Filter (verified against pakchunk0).
    // Multi-pass: only needed assets land in stage (~700MB vs ~29GB full extract).
    // Mesh filters must cover direct Meshes/SK_* and nested Meshes/<sub>/SK_* layouts.
    private static readonly string[] SelectiveExtractFilters =
    [
        "*Content/Animations*",
        "*Content/DB*",
        "*Characters/Skeleton*",
        "*Engine/Content/Animation*",
        "*Characters*/Meshes/SK_*",
        "*Characters*/Meshes/*/SK_*",
        "*Characters*/Meshes/*/*/SK_*",
    ];

    // Completeness baselines from the full pakchunk0 reference extract.
    // Stage must meet these (± slack) or we fall back / fail instead of shipping a partial tree.
    private const int MinAnimationUassets = 3370;      // REF 3379
    private const int MinArchetypeUassets = 1105;      // REF 1110
    private const int MinComboTreeUassets = 280;       // REF 283
    private const int MinMeshUassets = 200;            // REF 256

    /// <summary>
    /// Short stage path under %TEMP%. The deep bin\Debug\...\VanillaExtract path exceeded
    /// MAX_PATH (260) for UnrealPak (native UE4), causing silent "Unable to create file"
    /// failures while still exiting 0.
    /// </summary>
    public static string GetDefaultStageDir() =>
        Path.Combine(Path.GetTempPath(), "SifuPakStage");

    /// <summary>
    /// Removes Content/ and Engine/ under targetDir so a pak extract reflects this run only.
    /// Called only after the stage completeness gate has passed.
    /// </summary>
    public static string? WipeGameContentTrees(string targetDir)
    {
        try
        {
            foreach (var name in new[] { "Content", "Engine" })
            {
                var dir = Path.Combine(targetDir, name);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            SetupLog($"Wiped GameContent trees under {targetDir}");
            return null;
        }
        catch (Exception ex)
        {
            SetupLog($"Wipe GameContent failed: {ex.Message}");
            return ex.Message;
        }
    }

    private static void SetupLog(string message)
    {
        ErrorLog.Write("SETUP", new Exception(message));
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setup.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }

    /// <summary>
    /// Extracts needed assets from pak with UnrealPak (+CryptoKeys when available) into stageDir.
    /// Uses -Filter multi-pass (no pak file copy). Falls back to full extract if filters fail
    /// or the stage fails completeness checks.
    /// Returns null on success, or an error message.
    /// </summary>
    public static async Task<string?> ExtractPakToStageAsync(
        string pakFile,
        string stageDir,
        IProgress<string>? status = null,
        IProgress<double>? progress = null,
        int timeoutMs = 900000)
    {
        if (!File.Exists(pakFile))
            return $"Pak file not found: {pakFile}";

        var unrealPakPath = FindUnrealPak();
        if (unrealPakPath == null)
            return "UnrealPak not found. Browse to UnrealPak.exe (Settings → Open Setup), or place it in tools\\ue4\\UnrealPak\\UnrealPak.exe.";

        var crypto = EnsurePakKeysFile();
        SetupLog($"Extract start pak={pakFile} stage={stageDir} timeoutMs={timeoutMs} crypto={(crypto != null)}");

        try
        {
            if (Directory.Exists(stageDir))
                Directory.Delete(stageDir, true);
            Directory.CreateDirectory(stageDir);

            status?.Report("Extracting needed assets (filtered)...");
            progress?.Report(0.05);

            int passes = SelectiveExtractFilters.Length;
            string? lastFilterError = null;
            bool anyFilterFailed = false;

            for (int i = 0; i < passes; i++)
            {
                var filter = SelectiveExtractFilters[i];
                status?.Report($"Extracting needed assets ({i + 1}/{passes})...");
                progress?.Report(0.05 + 0.60 * (i + 1) / passes);

                var filterErr = await RunUnrealPakExtractAsync(
                    unrealPakPath, pakFile, crypto, stageDir, filter, timeoutMs);
                if (filterErr != null)
                {
                    anyFilterFailed = true;
                    lastFilterError = $"[{filter}] {filterErr}";
                    SetupLog($"Filter pass failed: {lastFilterError}");
                }
            }

            progress?.Report(0.70);
            status?.Report("Verifying extracted content...");

            var stageIssue = DescribeStageIssues(stageDir);
            if (!anyFilterFailed && stageIssue == null)
            {
                progress?.Report(0.85);
                SetupLog("Selective extract OK (all filters clean + completeness gate passed).");
                return null;
            }

            var reason = anyFilterFailed
                ? $"filter error: {lastFilterError}"
                : $"completeness: {stageIssue}";
            SetupLog($"Selective extract incomplete ({reason}); attempting full extract fallback.");

            if (!HasFreeSpaceForFullExtract(pakFile, stageDir))
            {
                return "Selective extract was incomplete (" + reason + ") and there is not enough free disk space " +
                       "for a full pak extract (~30 GB). Free up space and retry, or point Setup at an " +
                       "already-extracted content folder.";
            }

            status?.Report("Selective extract incomplete — running full extract...");
            progress?.Report(0.10);

            try { Directory.Delete(stageDir, true); } catch { }
            Directory.CreateDirectory(stageDir);

            var fullErr = await RunUnrealPakExtractAsync(
                unrealPakPath, pakFile, crypto, stageDir, filter: null, timeoutMs);
            if (fullErr != null)
            {
                SetupLog($"Full extract failed: {fullErr}");
                return fullErr;
            }

            progress?.Report(0.75);
            status?.Report("Verifying extracted content...");

            var fullIssue = DescribeStageIssues(stageDir);
            if (fullIssue == null)
            {
                progress?.Report(0.85);
                SetupLog("Full extract OK (completeness gate passed).");
                return null;
            }

            SetupLog($"Full extract incomplete: {fullIssue}");
            return "Extraction finished but required content was missing from the stage: " + fullIssue;
        }
        catch (Exception ex)
        {
            SetupLog($"Extract exception: {ex}");
            return ex.Message;
        }
    }

    /// <summary>
    /// Returns null if stage has enough Animations / Archetypes / Combos / skeleton / meshes,
    /// otherwise a short description of what is missing.
    /// </summary>
    private static string? DescribeStageIssues(string stageDir)
    {
        var root = ResolveExtractedRoot(stageDir);
        if (root == null)
            return "Animations folder not found in stage";

        var content = Path.Combine(root, "Content");

        var animCount = CountUassets(Path.Combine(content, "Animations"));
        if (animCount < MinAnimationUassets)
            return $"Animations has {animCount} uassets (need ≥ {MinAnimationUassets})";

        var archCount = CountUassets(Path.Combine(content, "DB", "AI", "Archetypes"));
        if (archCount < MinArchetypeUassets)
            return $"DB/AI/Archetypes has {archCount} uassets (need ≥ {MinArchetypeUassets})";

        var comboCount = CountUassets(Path.Combine(content, "DB", "_MainChar", "Combos"));
        if (comboCount < MinComboTreeUassets)
            return $"DB/_MainChar/Combos has {comboCount} uassets (need ≥ {MinComboTreeUassets})";

        var skeleton = Path.Combine(content, "Characters", "Skeleton", "Base_skeleton.uasset");
        if (!FileExistsLong(skeleton))
            return "Characters/Skeleton/Base_skeleton.uasset missing";

        var skCount = CountMeshUassets(content);
        if (skCount < MinMeshUassets)
            return $"Character meshes incomplete ({skCount} SK uassets; need ≥ {MinMeshUassets})";

        foreach (var enginePath in ExtractionManifest.EnginePaths)
        {
            var engineUasset = ExtractionManifest.ResolveSourceFile(root, enginePath, ".uasset");
            if (!FileExistsLong(engineUasset))
                return $"Engine asset missing from stage: {enginePath}";
        }

        return null;
    }

    private static int CountUassets(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        try
        {
            return Directory.GetFiles(dir, "*.uasset", SearchOption.AllDirectories).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountMeshUassets(string contentRoot)
    {
        int skCount = 0;
        foreach (var meshDir in ExtractionManifest.MeshDirectories)
        {
            var dir = Path.Combine(contentRoot, meshDir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) continue;
            try { skCount += Directory.GetFiles(dir, "SK_*.uasset", SearchOption.AllDirectories).Length; }
            catch { }
        }
        return skCount;
    }

    /// <summary>
    /// Cheap free-space check before optional full (~30GB) extract fallback.
    /// </summary>
    private static bool HasFreeSpaceForFullExtract(string pakFile, string stageDir)
    {
        try
        {
            var stageRoot = Path.GetPathRoot(Path.GetFullPath(stageDir));
            if (string.IsNullOrEmpty(stageRoot)) return true;
            var drive = new DriveInfo(stageRoot);
            var pakBytes = new FileInfo(pakFile).Length;
            // full extract ≈ pak size + existing stage + margin
            long required = pakBytes + (2L * 1024 * 1024 * 1024);
            return drive.AvailableFreeSpace >= required;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Runs one UnrealPak -Extract (optional -Filter) against the original pak (read-only).
    /// Returns null on success or an error message.
    /// </summary>
    private static async Task<string?> RunUnrealPakExtractAsync(
        string unrealPakPath,
        string pakFile,
        string? crypto,
        string stageDir,
        string? filter,
        int timeoutMs)
    {
        var extractArgs = crypto != null
            ? $"\"{pakFile}\" -CryptoKeys=\"{crypto}\" -Extract \"{stageDir}\""
            : $"\"{pakFile}\" -Extract \"{stageDir}\"";
        if (!string.IsNullOrEmpty(filter))
            extractArgs += $" -Filter=\"{filter}\"";

        SetupLog($"UnrealPak args: {extractArgs}");
        var sw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = unrealPakPath,
            Arguments = extractArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = EnsureUnrealPakWorkingDirectory(),
        };

        using var process = Process.Start(psi);
        if (process == null)
            return "Failed to start UnrealPak.";

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var exited = await Task.Run(() => process.WaitForExit(timeoutMs));
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            SetupLog($"UnrealPak timed out after {timeoutMs}ms filter={filter ?? "(none)"}");
            return $"UnrealPak timed out after {timeoutMs / 1000}s. Check setup.log.";
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        SetupLog($"UnrealPak exit={process.ExitCode} elapsed={sw.ElapsedMilliseconds}ms filter={filter ?? "(none)"}");
        if (!string.IsNullOrWhiteSpace(stdout))
            SetupLog($"UnrealPak stdout: {stdout.Trim()}");
        if (!string.IsNullOrWhiteSpace(stderr))
            SetupLog($"UnrealPak stderr: {stderr.Trim()}");

        if (process.ExitCode == 0)
        {
            // UnrealPak can exit 0 even when it cannot write files (MAX_PATH / disk).
            if (stdout.Contains("Unable to create file", StringComparison.OrdinalIgnoreCase)
                || stdout.Contains("LogPakFile: Error:", StringComparison.OrdinalIgnoreCase))
            {
                var writeErrs = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Count(l => l.Contains("Unable to create file", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("LogPakFile: Error:", StringComparison.OrdinalIgnoreCase));
                SetupLog($"UnrealPak exit=0 but reported {writeErrs} write error(s).");
                return $"UnrealPak reported {writeErrs} write error(s) " +
                       $"(path too long or disk). Stage path: {stageDir}";
            }
            return null;
        }

        if (crypto == null)
            return "UnrealPak failed and pak decryption keys are unavailable. " +
                   "Check setup.log and retry.";

        var errLines = string.Join("\n", (stderr + "\n" + stdout)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            .Take(6));

        if (string.IsNullOrWhiteSpace(errLines))
            errLines = $"UnrealPak exit {process.ExitCode}";

        return $"Extraction failed (exit {process.ExitCode}):\n{errLines}";
    }

    /// <summary>
    /// Returns the Sifu content root inside a stage dir (…\Sifu or … itself if it has Content/).
    /// </summary>
    public static string? ResolveExtractedRoot(string stageDir)
    {
        var withSifu = Path.Combine(stageDir, "Sifu");
        if (Directory.Exists(Path.Combine(withSifu, "Content", "Animations")))
            return withSifu;
        if (Directory.Exists(Path.Combine(stageDir, "Content", "Animations")))
            return stageDir;
        return null;
    }

    /// <summary>
    /// Finds pak under gameDir and extracts into GameContent via UnrealPak + selective copy.
    /// Returns null on success or an error message.
    /// </summary>
    public static async Task<string?> ExtractGameDirToGameContentAsync(
        string gameDir,
        string targetDir,
        IProgress<(int copied, int total, string currentFile)>? copyProgress = null,
        IProgress<string>? status = null,
        IProgress<double>? progress = null)
    {
        var pak = FindPakFile(gameDir);
        if (pak == null)
            return "Could not find pakchunk0*.pak under the selected folder.";

        var stageDir = GetDefaultStageDir();
        var extractError = await ExtractPakToStageAsync(pak, stageDir, status, progress);
        if (extractError != null)
            return extractError;

        var sourceRoot = ResolveExtractedRoot(stageDir);
        if (sourceRoot == null)
            return "Extraction finished but Animations folder was not found in the stage.";

        try
        {
            var wipeErr = WipeGameContentTrees(targetDir);
            if (wipeErr != null)
                return wipeErr;

            status?.Report("Copying needed files into GameContent...");
            progress?.Report(0.85);
            var copied = await Task.Run(() => CopyFromExtracted(sourceRoot, targetDir, copyProgress));
            SetupLog($"CopyFromExtracted copied={copied} target={targetDir}");

            var copyIssue = DescribeCopyIssues(sourceRoot, targetDir);
            if (copyIssue != null)
            {
                SetupLog($"Post-copy verification failed: {copyIssue}");
                return "Copy finished but GameContent is incomplete: " + copyIssue;
            }

            var contentIssue = DescribeContentIssues(targetDir);
            if (contentIssue != null)
            {
                SetupLog($"Absolute completeness check failed: {contentIssue}");
                return "GameContent is incomplete after copy: " + contentIssue;
            }

            progress?.Report(1.0);
            return null;
        }
        catch (Exception ex)
        {
            SetupLog($"Copy exception: {ex}");
            return ex.Message;
        }
        finally
        {
            CleanupStage(stageDir);
        }
    }

    /// <summary>
    /// Deletes the short temp stage and any legacy VanillaExtract stage under the app dir.
    /// </summary>
    public static void CleanupStage(string? stageDir = null)
    {
        foreach (var root in new[] { stageDir, GetDefaultStageDir(),
                 Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VanillaExtract") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch (Exception ex) { SetupLog($"Stage cleanup failed ({root}): {ex.Message}"); }
        }
    }

    /// <summary>
    /// Independent stage/source → target check: per-pattern uasset counts must match
    /// (does not walk GetAllNeededPaths, so missing paths cannot hide behind ResolveSourceFile).
    /// Returns null if OK, else a short description of gaps.
    /// </summary>
    public static string? DescribeCopyIssues(string sourceRoot, string targetDir)
    {
        var sourceContent = Path.Combine(sourceRoot, "Content");
        var targetContent = Path.Combine(targetDir, "Content");

        foreach (var pattern in ExtractionManifest.DirectoryPatterns)
        {
            var srcDir = Path.Combine(sourceContent, pattern.Replace('/', Path.DirectorySeparatorChar));
            var dstDir = Path.Combine(targetContent, pattern.Replace('/', Path.DirectorySeparatorChar));
            var srcCount = CountUassets(srcDir);
            var dstCount = CountUassets(dstDir);
            SetupLog($"Copy verify {pattern}: stage={srcCount} target={dstCount}");
            if (dstCount < srcCount)
                return $"{pattern}: GameContent has {dstCount} uassets, stage/source has {srcCount}";
        }

        var srcSk = CountMeshUassets(sourceContent);
        var dstSk = CountMeshUassets(targetContent);
        SetupLog($"Copy verify meshes: stage={srcSk} target={dstSk}");
        if (dstSk < srcSk)
            return $"Character meshes: GameContent has {dstSk} SK uassets, stage/source has {srcSk}";

        foreach (var enginePath in ExtractionManifest.EnginePaths)
        {
            var stageUasset = ExtractionManifest.ResolveSourceFile(sourceRoot, enginePath, ".uasset");
            if (!FileExistsLong(stageUasset))
                continue;

            var targetUasset = Path.Combine(targetDir,
                enginePath.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!FileExistsLong(targetUasset))
                return $"Engine asset missing under GameContent: {enginePath}";
        }

        return null;
    }

    /// <summary>
    /// Absolute completeness check on GameContent itself (not stage→target),
    /// using the same REF baselines as DescribeStageIssues.
    /// </summary>
    public static string? DescribeContentIssues(string targetDir)
    {
        var content = Path.Combine(targetDir, "Content");
        if (!Directory.Exists(content))
            return "Content folder missing under GameContent";

        var animCount = CountUassets(Path.Combine(content, "Animations"));
        if (animCount < MinAnimationUassets)
            return $"Animations has {animCount} uassets (need ≥ {MinAnimationUassets})";

        var archCount = CountUassets(Path.Combine(content, "DB", "AI", "Archetypes"));
        if (archCount < MinArchetypeUassets)
            return $"DB/AI/Archetypes has {archCount} uassets (need ≥ {MinArchetypeUassets})";

        var comboCount = CountUassets(Path.Combine(content, "DB", "_MainChar", "Combos"));
        if (comboCount < MinComboTreeUassets)
            return $"DB/_MainChar/Combos has {comboCount} uassets (need ≥ {MinComboTreeUassets})";

        var skeleton = Path.Combine(content, "Characters", "Skeleton", "Base_skeleton.uasset");
        if (!FileExistsLong(skeleton))
            return "Characters/Skeleton/Base_skeleton.uasset missing";

        var skCount = CountMeshUassets(content);
        if (skCount < MinMeshUassets)
            return $"Character meshes incomplete ({skCount} SK uassets; need ≥ {MinMeshUassets})";

        foreach (var enginePath in ExtractionManifest.EnginePaths)
        {
            var engineUasset = Path.Combine(targetDir,
                enginePath.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!FileExistsLong(engineUasset))
                return $"Engine asset missing under GameContent: {enginePath}";
        }

        return null;
    }

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
   - DB/Movement/
   - Characters/MainChar/M/Meshes/SK_M_MainChar_01.*
   - Characters/PNJ/ (Grunt, Disicple, FlashKick, BigGuy, BodyGuards, Servant)
   - Characters/Boss/ (Fajar, Sean, Kuroki, Yang, Fengjie)
6. Point this tool to the extracted folder";
    }

    private static bool CopyFileIfExists(string source, string target)
    {
        if (!FileExistsLong(source)) return false;

        var targetDir = Path.GetDirectoryName(target);
        if (targetDir != null) Directory.CreateDirectory(MaybeLongPath(targetDir));

        File.Copy(MaybeLongPath(source), MaybeLongPath(target), overwrite: true);
        return true;
    }

    private static string MaybeLongPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        if (Path.IsPathRooted(path) && path.Length >= 248)
            return @"\\?\" + path;
        return path;
    }

    private static bool FileExistsLong(string path)
    {
        try { return File.Exists(MaybeLongPath(path)); }
        catch { return false; }
    }

    internal static string EnsureUnrealPakWorkingDirectory()
    {
        var app = AppDomain.CurrentDomain.BaseDirectory;
        var baseIni = Path.Combine(app, "Engine", "Config", "BaseEngine.ini");
        var progIni = Path.Combine(app, "Engine", "Programs", "UnrealPak", "Saved", "Config", "Windows", "Engine.ini");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baseIni)!);
            if (!File.Exists(baseIni))
            {
                var seed = FindSeedFile(@"Engine\Config\BaseEngine.ini");
                if (seed != null) File.Copy(seed, baseIni, true);
                else File.WriteAllText(baseIni, "[DerivedDataBackendGraph]\nNone=(Type=None)\n");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(progIni)!);
            if (!File.Exists(progIni)) File.WriteAllText(progIni, "");
        }
        catch { }

        var pakExe = FindUnrealPak();
        if (!string.IsNullOrEmpty(pakExe))
        {
            var exeDir = Path.GetDirectoryName(pakExe);
            if (!string.IsNullOrEmpty(exeDir) && Directory.Exists(exeDir))
                return exeDir;
        }

        var wd = Path.Combine(app, "tools", "ue4", "UnrealPak");
        Directory.CreateDirectory(wd);
        return wd;
    }

    private static string? FindSeedFile(string relative)
    {
        try
        {
            var walk = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (walk != null)
            {
                var p = Path.Combine(walk.FullName, relative);
                if (File.Exists(p)) return p;
                walk = walk.Parent;
            }
        }
        catch { }
        return null;
    }

    internal static string? FindUnrealPak()
    {
        if (!string.IsNullOrEmpty(CustomUnrealPakPath) && File.Exists(CustomUnrealPakPath))
            return CustomUnrealPakPath;

        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ue4", "UnrealPak", "UnrealPak.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "ue4", "UnrealPak", "UnrealPak.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "UnrealPak.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "UnrealPak.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        var fromPath = FindOnPath("UnrealPak.exe");
        return fromPath;
    }

    private static string? FindOnPath(string fileName)
    {
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full)) return full;
            }
        }
        catch { }
        return null;
    }
}
