using System;
using System.Collections.Generic;
using System.IO;

namespace SifuMovesetEditor;

/// <summary>How <see cref="CustomFileRestore.Apply"/> resolves files that already exist in CustomAssets with different content.</summary>
public enum CustomRestoreStrategy
{
    /// <summary>Invoke the caller-provided picker (the conflict dialog). Falls back to Skip when no picker is given.</summary>
    Ask,
    ReplaceAll,
    Skip,
    Cancel,
}

public sealed class CustomRestoreResult
{
    public bool Cancelled;
    /// <summary>Missing on disk, written from the embedded payload.</summary>
    public int Restored;
    /// <summary>Present with different content, overwritten (ReplaceAll).</summary>
    public int Replaced;
    /// <summary>Present with identical content - nothing to do.</summary>
    public int SkippedIdentical;
    /// <summary>Present with different content, kept as-is (Skip).</summary>
    public int SkippedConflicts;
    /// <summary>Display names of every conflicting asset (companions collapsed).</summary>
    public List<string> Conflicts = new();
}

/// <summary>
/// Writes project-embedded CustomAssets payloads (EditorProject.CustomFilePayloads)
/// back into a CustomAssets root on project load. Missing files are restored,
/// byte-identical files are silently skipped, and differing files are conflicts
/// resolved all-or-nothing: ReplaceAll overwrites every conflict, Skip keeps the
/// on-disk versions, Cancel aborts (zero writes - classification runs first).
/// </summary>
public static class CustomFileRestore
{
    public static CustomRestoreResult Apply(
        IReadOnlyList<SavedCustomFile>? payloads,
        string customRoot,
        CustomRestoreStrategy strategy,
        Func<IReadOnlyList<string>, CustomRestoreStrategy>? ask = null)
    {
        var result = new CustomRestoreResult();
        if (payloads == null || payloads.Count == 0 || string.IsNullOrEmpty(customRoot))
            return result;

        // Classify every stem before the first write so Cancel leaves the disk untouched.
        var missing = new List<SavedCustomFile>();
        var conflicts = new List<SavedCustomFile>();
        int identical = 0;
        foreach (var p in payloads)
        {
            if (p?.Parts == null || p.Parts.Count == 0 || string.IsNullOrEmpty(p.Rel)) continue;
            bool anyMissing = false, anyDiffer = false;
            foreach (var kv in p.Parts)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                var disk = Path.Combine(customRoot, (p.Rel + kv.Key).Replace('/', Path.DirectorySeparatorChar));
                byte[] want;
                try { want = Convert.FromBase64String(kv.Value); } catch { continue; }
                if (!File.Exists(disk)) { anyMissing = true; continue; }
                byte[] have;
                try { have = File.ReadAllBytes(disk); } catch { anyMissing = true; continue; }
                if (!BytesEqual(have, want)) anyDiffer = true;
            }
            if (anyDiffer) conflicts.Add(p);
            else if (anyMissing) missing.Add(p);
            else identical++;
        }

        result.SkippedIdentical = identical;
        foreach (var c in conflicts)
            result.Conflicts.Add(CustomAssets.DisplayNameForRel(c.Rel + ".uasset"));

        if (conflicts.Count > 0 && strategy == CustomRestoreStrategy.Ask)
            strategy = ask?.Invoke(result.Conflicts) ?? CustomRestoreStrategy.Skip;

        if (strategy == CustomRestoreStrategy.Cancel)
        {
            result.Cancelled = true;
            return result;
        }

        foreach (var p in missing)
            result.Restored += WriteParts(customRoot, p);
        if (strategy == CustomRestoreStrategy.ReplaceAll)
        {
            foreach (var p in conflicts)
                result.Replaced += WriteParts(customRoot, p);
        }
        else
        {
            result.SkippedConflicts = conflicts.Count;
        }
        return result;
    }

    private static int WriteParts(string customRoot, SavedCustomFile p)
    {
        int written = 0;
        foreach (var kv in p.Parts!)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            try
            {
                var dest = Path.Combine(customRoot, (p.Rel + kv.Key).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, Convert.FromBase64String(kv.Value));
                written++;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("CUSTOM", ex);
            }
        }
        return written;
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
