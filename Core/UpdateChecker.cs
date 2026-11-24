using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SifuMovesetEditor;

public class UpdateInfo
{
    public string Tag = "";
    public string VersionText = "";
    public Version Version = new(0, 0);
    public string Notes = "";
    public string AssetUrl = "";
    public string AssetName = "";
    public long AssetSize;
    public DateTimeOffset? PublishedAt;
}

/// <summary>Outcome of a pre-install backup: where it landed and how much it
/// captured. Dir stays null when there was nothing to back up (fresh install)
/// or when every copy failed - an install never fails because of the backup.</summary>
public class BackupResult
{
    public string? Dir;
    public int Copied;
    public int Skipped;
    public bool Created => Dir != null;
}

/// <summary>
/// Optional launch-time update check against the project's GitHub releases
/// (the tool's only internet use). Compares the running assembly version
/// against the highest non-draft/non-prerelease release that ships the
/// SifuMovesetEditor.zip asset, and applies an update by staging the zip in
/// %TEMP% and swapping it over the app dir IN PLACE - no external script, no
/// cmd/robocopy (which read as suspicious to AV heuristics and failed silently
/// after exit). Windows permits renaming a running image, so locked files are
/// renamed to *.old, the new payload is copied in, a fresh instance is spawned,
/// and this process exits; the next launch deletes the *.old leftovers.
/// The same pipeline runs backwards for the Version history dialog: any older
/// release installs the same way, optionally after backing up the files it
/// replaces so a disappointing version can be undone again.
/// All network failures are silent by design - offline users see nothing.
/// </summary>
public static class UpdateChecker
{
    internal const string RepoOwner = "ChorlsChu";
    internal const string RepoName = "Custom-Moveset-Maker--Sifu-Mod-";
    internal const string ExpectedAssetName = "SifuMovesetEditor.zip";
    internal const string TempDirPrefix = "SifuUpdate_";
    internal const string BackupDirName = "Backups";
    private const int BackupsToKeep = 3;
    private const int CheckTimeoutSeconds = 15;
    private const int DownloadTimeoutMinutes = 15;

    /// <summary>First release that shipped the launch-time update check. Older
    /// builds had no way to get back on their own until the release assets were
    /// retrofitted; the dialog still forces a backup for them as a safety net.</summary>
    internal static readonly Version FirstAutoUpdateVersion = new(1, 3);

    public static Version CurrentVersion()
    {
        var asm = typeof(UpdateChecker).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var raw = string.IsNullOrWhiteSpace(info)
            ? (asm.GetName().Version?.ToString() ?? "0.0")
            : info;
        return ParseTagVersion(raw) ?? new Version(0, 0);
    }

    /// <summary>Accepts "v1.2", "1.3", "1.4.1", plus informational suffixes
    /// like "1.3+abc" or "1.3 (abc)" - returns null for garbage.</summary>
    internal static Version? ParseTagVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s.Substring(0, plus);
        var paren = s.IndexOf('(');
        if (paren >= 0) s = s.Substring(0, paren);
        s = s.Trim().TrimStart('v', 'V').Trim();
        return Version.TryParse(s, out var v) ? v : null;
    }

    internal static bool IsNewer(Version candidate, Version current) => candidate > current;

    /// <summary>Every eligible published release, newest first: not draft, not
    /// prerelease, tag parses as a version, and the SifuMovesetEditor.zip asset
    /// exists. Duplicate versions keep the first (GitHub lists newest-published
    /// first). Used by the Version history dialog; CheckForUpdateAsync uses it
    /// too so there is a single parser.</summary>
    public static async Task<List<UpdateInfo>> GetReleasesAsync(int perPage = 20)
    {
        using var http = CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CheckTimeoutSeconds));
        var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page={perPage}";
        var json = await http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
        return ParseReleases(json);
    }

    /// <summary>Pure half of GetReleasesAsync, split out so the smoke tests can
    /// pin filtering, dedupe and ordering without a network call.</summary>
    internal static List<UpdateInfo> ParseReleases(string? json)
    {
        var releases = string.IsNullOrWhiteSpace(json)
            ? new List<GitHubRelease>()
            : JsonConvert.DeserializeObject<List<GitHubRelease>>(json) ?? new List<GitHubRelease>();

        var seen = new HashSet<Version>();
        var result = new List<UpdateInfo>();
        foreach (var rel in releases)
        {
            if (rel == null || rel.draft || rel.prerelease) continue;
            var ver = ParseTagVersion(rel.tag_name);
            if (ver == null) continue;
            var asset = (rel.assets ?? new List<GitHubAsset>()).FirstOrDefault(a =>
                a != null &&
                string.Equals(a.name, ExpectedAssetName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(a.browser_download_url));
            if (asset == null) continue;
            if (!seen.Add(ver)) continue;
            result.Add(new UpdateInfo
            {
                Tag = rel.tag_name ?? "",
                VersionText = ver.ToString(),
                Version = ver,
                Notes = rel.body ?? "",
                AssetUrl = asset.browser_download_url ?? "",
                AssetName = asset.name ?? "",
                AssetSize = asset.size,
                PublishedAt = DateTimeOffset.TryParse(rel.published_at,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                    ? when
                    : null,
            });
        }
        return result.OrderByDescending(r => r.Version).ToList();
    }

    /// <summary>Highest release strictly newer than the running version, or
    /// null when up to date. currentOverride exists for the SIFU_UPDATE_FORCE=1
    /// manual test hook.</summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(Version? currentOverride = null)
    {
        var current = currentOverride ?? CurrentVersion();
        var releases = await GetReleasesAsync().ConfigureAwait(false);
        return FindNewest(releases, current);
    }

    /// <summary>Newest entry newer than <paramref name="current"/>; null when
    /// the list holds nothing newer (including the equal-version case).</summary>
    internal static UpdateInfo? FindNewest(IReadOnlyList<UpdateInfo> releases, Version current)
    {
        UpdateInfo? best = null;
        foreach (var info in releases)
        {
            if (!IsNewer(info.Version, current)) continue;
            if (best == null || info.Version > best.Version) best = info;
        }
        return best;
    }

    /// <summary>Downloads the release zip to %TEMP%, extracts it, optionally
    /// backs up the files it will replace, swaps the payload over the app dir in
    /// place, spawns the new instance, and - on success - deletes the staging
    /// dir. Works in either direction: the same call installs v1.5 or reverts to
    /// v1.2. Progress covers download (0-80%, or 0-75% with a backup), the
    /// backup (75-85%) then the file swap (85-100%). Any failure either happens
    /// before the swap (staging deleted, app untouched) or is rolled back by
    /// SwapIntoPlace - so the dialog can truthfully say nothing was changed.
    /// Returns the backup dir used, or null when none was requested/needed.</summary>
    public static async Task<string?> ApplyUpdateAsync(UpdateInfo info, IProgress<double>? progress = null,
        bool backupBeforeInstall = false)
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the running executable path.");
        var exeName = Path.GetFileName(exePath);

        // Split points stay fixed so the bar never jumps backwards between phases.
        var downloadShare = backupBeforeInstall ? 0.75 : 0.8;
        var swapStart = backupBeforeInstall ? 0.85 : 0.8;

        var root = Path.Combine(Path.GetTempPath(), TempDirPrefix + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        string? backupDir = null;
        try
        {
            var zipPath = Path.Combine(root, "update.zip");
            IProgress<double>? downloadProgress = progress == null
                ? null
                : new Progress<double>(v => progress.Report(v * downloadShare));
            await DownloadToFileAsync(info.AssetUrl, zipPath, downloadProgress).ConfigureAwait(false);

            var extractDir = Path.Combine(root, "app");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            // Layouts accepted: exe at the zip root, or inside a single wrapper
            // folder like SifuMovesetEditor/ (easier for users to extract by hand).
            var payloadDir = FindPayloadDir(extractDir, exeName);

            if (backupBeforeInstall)
            {
                var current = CurrentVersion();
                try
                {
                    // progress.Report is safe off the UI thread (Progress<T> marshals),
                    // so the backup can run on this worker continuation.
                    var backup = BackupInstall(payloadDir, appDir, current,
                        v => progress?.Report(downloadShare + v * (swapStart - downloadShare)));
                    backupDir = backup.Dir;
                    ErrorLog.WriteRaw(backup.Created
                        ? $"[UPDATE] backed up v{current} to {backup.Dir} ({backup.Copied} file(s), {backup.Skipped} skipped)"
                        : $"[UPDATE] backup requested but there was nothing to back up");
                }
                catch (Exception ex)
                {
                    // A read-only install folder must not block the install itself.
                    ErrorLog.WriteRaw($"[UPDATE] backup skipped: {ex.Message}");
                }
            }

            progress?.Report(swapStart);
            SwapIntoPlace(payloadDir, appDir, p => progress?.Report(swapStart + p * (1 - swapStart)));
            if (backupBeforeInstall) PruneBackups(appDir);
            ErrorLog.WriteRaw($"[UPDATE] swap complete, relaunching {exeName}");
            try { Directory.Delete(root, true); } catch { }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = appDir,
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Update installed, but the automatic restart failed ({ex.Message}) - start the tool again manually.", ex);
            }
            return backupDir;
        }
        catch
        {
            try { Directory.Delete(root, true); } catch { }
            throw;
        }
    }

    private static async Task DownloadToFileAsync(string url, string destPath, IProgress<double>? progress)
    {
        using var http = CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(DownloadTimeoutMinutes));
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        await using var dst = File.Create(destPath);
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
            copied += read;
            if (total > 0) progress?.Report((double)copied / total);
        }
        if (total > 0 && copied != total)
            throw new InvalidOperationException($"Download truncated ({copied:N0} of {total:N0} bytes).");
    }

    /// <summary>Finds where the app payload landed after extraction: either the
    /// extract dir itself (exe at zip root) or a single top-level wrapper folder
    /// like SifuMovesetEditor/. Throws when neither contains the exe.</summary>
    internal static string FindPayloadDir(string extractDir, string exeName)
    {
        if (File.Exists(Path.Combine(extractDir, exeName))) return extractDir;
        foreach (var dir in Directory.GetDirectories(extractDir))
            if (File.Exists(Path.Combine(dir, exeName))) return dir;
        throw new InvalidOperationException(
            $"Downloaded update is missing {exeName} - could not find it at the zip root or in a top-level folder.");
    }

    /// <summary>True for payload-relative paths that belong to the user, not the
    /// app: settings.json at any depth and the CustomAssets, GameContent,
    /// ExportedMods, logs and Backups folders. Release zips normally ship none
    /// of these, but an older or hand-made zip might - the updater must never
    /// overwrite them.</summary>
    internal static bool IsUserDataPath(string relativePath)
    {
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(rel), "settings.json", StringComparison.OrdinalIgnoreCase))
            return true;

        var first = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return first != null && (
            first.Equals("CustomAssets", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("GameContent", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("ExportedMods", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("Backups", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Copies every file under payloadDir over appDir (settings.json and
    /// the user folders in <see cref="IsUserDataPath"/> skipped; files missing
    /// from the payload are never deleted). Files that
    /// cannot be opened exclusively - the running exe and loaded DLLs - are
    /// renamed to *.old first, which Windows explicitly supports for in-use
    /// files, then the payload is copied into their place. A manifest at
    /// {appDir}\logs\swap.pending lists the renames so CleanupSwapLeftovers in
    /// the freshly spawned instance can delete them. Any failure rolls every
    /// rename back before rethrowing, so the tool still boots as the old
    /// version and the update is re-offered on the next launch.</summary>
    internal static void SwapIntoPlace(string payloadDir, string appDir, Action<double>? progress = null)
    {
        var relFiles = Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(payloadDir, f))
            .Where(rel => !IsUserDataPath(rel))
            .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var toCopy = new List<(string Src, string Dst)>(relFiles.Count);
        var locked = new List<string>();
        foreach (var rel in relFiles)
        {
            var src = Path.Combine(payloadDir, rel);
            var dst = Path.Combine(appDir, rel);
            toCopy.Add((src, dst));
            if (File.Exists(dst) && IsFileLocked(dst)) locked.Add(dst);
        }

        var manifest = Path.Combine(appDir, "logs", "swap.pending");
        Directory.CreateDirectory(Path.Combine(appDir, "logs"));

        // Phase 1: rename locked files, all-or-nothing. The manifest is written
        // first so even a crash mid-swap gets cleaned up by the next launch.
        var renamed = new List<string>();
        if (locked.Count > 0)
        {
            File.WriteAllLines(manifest, locked.Select(d => Path.GetRelativePath(appDir, d)));
            foreach (var dst in locked)
            {
                var bak = dst + ".old";
                try
                {
                    if (File.Exists(bak)) { File.SetAttributes(bak, FileAttributes.Normal); File.Delete(bak); }
                }
                catch { /* stale leftover */ }
                try
                {
                    File.Move(dst, bak);
                    renamed.Add(dst);
                }
                catch (Exception ex)
                {
                    foreach (var r in renamed)
                        try { File.Move(r + ".old", r); } catch { }
                    FinishManifest(manifest, locked);
                    AppendSwapLog(appDir, $"swap FAILED at rename of {Path.GetFileName(dst)}: {ex.Message}");
                    throw new InvalidOperationException(
                        $"Cannot replace {Path.GetFileName(dst)} - it is in use by another program ({ex.Message}). Close it and retry.", ex);
                }
            }
        }

        // Phase 2: copy the payload into place (sorted order keeps behavior
        // deterministic; retries absorb transient AV scan locks).
        var created = new List<string>();
        try
        {
            for (var i = 0; i < toCopy.Count; i++)
            {
                var (src, dst) = toCopy[i];
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                var existed = File.Exists(dst);
                CopyWithRetry(src, dst);
                if (!existed) created.Add(dst);
                progress?.Invoke((double)(i + 1) / toCopy.Count);
            }
        }
        catch (Exception ex)
        {
            // Restore every rename so the old version still boots. Files copied
            // before the failure that had an original keep their new content
            // (unrecoverable without a pre-backup); the old exe notices nothing
            // and the update simply re-offers next launch.
            foreach (var dst in created)
                try { File.Delete(dst); } catch { }
            for (var i = renamed.Count - 1; i >= 0; i--)
            {
                var dst = renamed[i];
                try
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(dst + ".old", dst);
                }
                catch { }
            }
            FinishManifest(manifest, locked);
            ErrorLog.WriteRaw($"[UPDATE] swap failed and was rolled back: {ex.Message}");
            AppendSwapLog(appDir, $"swap FAILED and rolled back: {ex.Message}");
            throw new InvalidOperationException(
                $"Update failed while copying files ({ex.Message}) - changes were rolled back, the tool is unchanged.", ex);
        }

        AppendSwapLog(appDir, $"swap ok: {toCopy.Count} file(s) copied, {renamed.Count} renamed to .old");
    }

    /// <summary>True when the file cannot be opened for WRITE with no sharing -
    /// i.e. the running image, a loaded module (or an unrelated process) holds
    /// it. Must probe WRITE, not read: image-section-held files (WebView2Loader
    /// dll - handle closed after mapping, share=READ) allow readers, so a
    /// read-probe reports them free and File.Copy then dies with a sharing
    /// violation. Write-open never mutates (FileMode.Open, no truncate).</summary>
    internal static bool IsFileLocked(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static void CopyWithRetry(string src, string dst)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Copy(src, dst, overwrite: true); return; }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(150);
            }
        }
    }

    /// <summary>Drops the swap manifest once no *.old files remain; keeps it when
    /// a rollback could not restore everything so the next launch finishes the job.</summary>
    private static void FinishManifest(string manifest, IReadOnlyList<string> lockedDsts)
    {
        try
        {
            if (lockedDsts.Any(d => File.Exists(d + ".old"))) return;
            File.Delete(manifest);
        }
        catch { }
    }

    private static void AppendSwapLog(string appDir, string line)
    {
        try
        {
            var dir = Path.Combine(appDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "update.log"),
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Removes *.old files left by an in-place swap; the new instance
    /// runs this at startup. Retries briefly because the previous process may
    /// still be exiting, and the manifest survives until everything is gone so a
    /// later launch completes the cleanup. Scoped strictly to the manifest -
    /// never globs *.old, which could match user files under CustomAssets etc.</summary>
    public static void CleanupSwapLeftovers(string? appDir = null)
    {
        try
        {
            appDir ??= AppDomain.CurrentDomain.BaseDirectory
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var manifest = Path.Combine(appDir, "logs", "swap.pending");
            if (!File.Exists(manifest)) return;
            var removed = 0;
            var allGone = true;
            foreach (var rel in File.ReadAllLines(manifest))
            {
                if (string.IsNullOrWhiteSpace(rel)) continue;
                var old = Path.Combine(appDir, rel) + ".old";
                var gone = false;
                for (var i = 0; i < 10 && !gone; i++)
                {
                    try
                    {
                        if (File.Exists(old)) { File.SetAttributes(old, FileAttributes.Normal); File.Delete(old); }
                        gone = !File.Exists(old);
                    }
                    catch { Thread.Sleep(300); }
                }
                if (gone) removed++; else allGone = false;
            }
            if (allGone)
            {
                try { File.Delete(manifest); } catch { }
                if (removed > 0) ErrorLog.WriteRaw($"[UPDATE] removed {removed} .old file(s) from the swap");
            }
        }
        catch { }
    }

    /// <summary>Best-effort removal of leftover staging dirs from prior update
    /// attempts. Skips dirs written within the last hour so an in-flight helper
    /// from another session is never robbed of its source files.</summary>
    public static void CleanupStaleTempDirs()
    {
        try
        {
            var temp = Path.GetTempPath();
            foreach (var dir in Directory.GetDirectories(temp, TempDirPrefix + "*"))
            {
                try
                {
                    if ((DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir)).TotalHours < 1) continue;
                    Directory.Delete(dir, true);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Copies the files this payload will overwrite into
    /// {appDir}\Backups\v{current}-{yyyyMMdd-HHmmss}\, so a downgrade (or a bad
    /// update) can be undone by hand. User data is never copied because the
    /// swap never touches it either (see <see cref="IsUserDataPath"/>); the
    /// folder gets a RESTORE.txt with the copy-back steps. A file that cannot
    /// be read is counted as skipped and listed in backup.json; the install
    /// itself is never failed by a backup problem.
    /// </summary>
    internal static BackupResult BackupInstall(string payloadDir, string appDir, Version installedVersion,
        Action<double>? onProgress = null)
    {
        var result = new BackupResult();
        var relFiles = Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(payloadDir, f))
            .Where(rel => !IsUserDataPath(rel))
            .Where(rel => File.Exists(Path.Combine(appDir, rel)))
            .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (relFiles.Count == 0) return result;

        var root = Path.Combine(appDir, BackupDirName);
        Directory.CreateDirectory(root);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dest = Path.Combine(root, $"v{installedVersion}-{stamp}");
        for (var n = 2; Directory.Exists(dest); n++)
            dest = Path.Combine(root, $"v{installedVersion}-{stamp}-{n}");
        Directory.CreateDirectory(dest);

        var skipped = new List<string>();
        for (var i = 0; i < relFiles.Count; i++)
        {
            var rel = relFiles[i];
            try
            {
                var dst = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(Path.Combine(appDir, rel), dst, overwrite: true);
                result.Copied++;
            }
            catch
            {
                skipped.Add(rel.Replace('\\', '/'));
                result.Skipped++;
            }
            onProgress?.Invoke((double)(i + 1) / relFiles.Count);
        }

        if (result.Copied == 0)
        {
            try { Directory.Delete(dest, true); } catch { }
            return result;
        }

        result.Dir = dest;
        try
        {
            File.WriteAllText(Path.Combine(dest, "backup.json"), JsonConvert.SerializeObject(new BackupManifest
            {
                version = installedVersion.ToString(),
                createdAt = DateTime.Now.ToString("o"),
                copied = result.Copied,
                skipped = skipped,
                note = "Files this install replaced, with their relative paths. Copy the folder's contents back over the app folder to undo the install.",
            }, Formatting.Indented));
        }
        catch { /* the copied files are what matter; the manifest is a convenience */ }

        try
        {
            File.WriteAllText(Path.Combine(dest, "RESTORE.txt"),
                $"To go back to v{installedVersion} (the version that was running before this install):" + Environment.NewLine +
                "1. Close Sifu Moveset Editor." + Environment.NewLine +
                "2. Copy everything in this folder into the folder that contains SifuMovesetEditor.exe." + Environment.NewLine +
                "3. Choose \"Replace the files in the destination\" if Windows asks." + Environment.NewLine +
                Environment.NewLine +
                "Ignore RESTORE.txt and backup.json when copying - they are just notes." + Environment.NewLine);
        }
        catch { /* the copied files are what matter; the note is a convenience */ }
        return result;
    }

    /// <summary>Keeps the newest <paramref name="keep"/> backups by the timestamp
    /// embedded in their folder name and deletes the rest. Never throws.</summary>
    internal static int PruneBackups(string appDir, int keep = BackupsToKeep)
    {
        var root = Path.Combine(appDir, BackupDirName);
        if (!Directory.Exists(root)) return 0;
        var dirs = Directory.GetDirectories(root)
            .Select(d => (Path: d, Stamp: BackupStamp(Path.GetFileName(d))))
            .OrderByDescending(d => d.Stamp, StringComparer.Ordinal)
            .ThenByDescending(d => d.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var removed = 0;
        foreach (var dir in dirs.Skip(Math.Max(keep, 1)))
        {
            try
            {
                Directory.Delete(dir.Path, true);
                removed++;
            }
            catch { }
        }
        return removed;
    }

    /// <summary>Sort key from a backup folder name ("v1.4-20261120-143012").
    /// Missing stamps sort last so they are pruned before real backups.</summary>
    internal static string BackupStamp(string dirName)
    {
        var match = Regex.Match(dirName ?? "", @"(\d{8}-\d{6})");
        return match.Success ? match.Groups[1].Value : "00000000-000000";
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SifuMovesetEditor-Updater");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private class BackupManifest
    {
        public string version { get; set; } = "";
        public string createdAt { get; set; } = "";
        public int copied { get; set; }
        public List<string> skipped { get; set; } = new();
        public string note { get; set; } = "";
    }

    private class GitHubRelease
    {
        public string? tag_name { get; set; }
        public bool draft { get; set; }
        public bool prerelease { get; set; }
        public string? body { get; set; }
        public string? published_at { get; set; }
        public List<GitHubAsset>? assets { get; set; }
    }

    private class GitHubAsset
    {
        public string? name { get; set; }
        public string? browser_download_url { get; set; }
        public long size { get; set; }
    }
}
