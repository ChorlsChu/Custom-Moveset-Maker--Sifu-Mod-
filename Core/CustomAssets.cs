using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SifuMovesetEditor;

/// <summary>
/// Persistent custom-content root ({BaseDirectory}/CustomAssets). Its layout mirrors
/// the game's Sifu/Content folder (Animations/..., DB/..., CustomDB/..., Effects/...)
/// so entries mount straight into the CUE4Parse provider and stage into the export
/// pak unchanged. Everything a mod import harvests (any pak file whose bytes differ
/// from vanilla) lands here, as does anything the user drops in by hand. The accepted-files manifest
/// (rel|lastWriteUtcTicks) drives the close prompt: Yes keeps the current manifest in
/// settings, No deletes only files that are new or modified against it.
/// </summary>
public static class CustomAssets
{
    public static readonly string Root = ResolveRoot();

    // Tests set SIFU_CUSTOM_ASSETS_ROOT so fixture cleanup can never delete the real library.
    private static string ResolveRoot()
    {
        var env = Environment.GetEnvironmentVariable("SIFU_CUSTOM_ASSETS_ROOT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CustomAssets");
    }

    public static void EnsureRoot()
    {
        try { Directory.CreateDirectory(Root); } catch { }
    }

    /// <summary>Absolute path for a Content-relative path (no extension munging).</summary>
    public static string FileForContentRel(string contentRel) =>
        Path.Combine(Root, contentRel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>"rel|utcTicks" for every file under Root ('/' separators, sorted).</summary>
    public static List<string> BuildManifest()
    {
        var entries = new List<string>();
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (var file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var rel = Path.GetRelativePath(Root, file).Replace('\\', '/');
                        entries.Add(rel + "|" + File.GetLastWriteTimeUtc(file).Ticks);
                    }
                    catch { }
                }
                entries.Sort(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
        return entries;
    }

    public static bool ManifestEquals(List<string>? a, List<string>? b)
    {
        var la = a ?? new List<string>();
        var lb = b ?? new List<string>();
        if (la.Count != lb.Count) return false;
        var set = new HashSet<string>(lb, StringComparer.OrdinalIgnoreCase);
        foreach (var e in la)
            if (!set.Contains(e)) return false;
        return true;
    }

    /// <summary>
    /// Entries of <paramref name="current"/> whose file is new (no accepted entry for
    /// the path) or modified (accepted entry exists but its ticks differ). This is
    /// exactly what a "No" on the close prompt deletes - previously accepted files
    /// whose content still matches stay.
    /// </summary>
    public static List<string> NewOrChanged(List<string> current, List<string>? accepted)
    {
        var acceptedByRel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in accepted ?? new List<string>())
        {
            var rel = RelOf(e);
            if (!string.IsNullOrEmpty(rel)) acceptedByRel[rel] = e;
        }

        var result = new List<string>();
        foreach (var e in current)
        {
            var rel = RelOf(e);
            if (!acceptedByRel.TryGetValue(rel, out var prev)
                || !string.Equals(prev, e, StringComparison.OrdinalIgnoreCase))
                result.Add(e);
        }
        return result;
    }

    public static string RelOf(string manifestEntry)
    {
        int bar = manifestEntry.LastIndexOf('|');
        return bar > 0 ? manifestEntry[..bar] : manifestEntry;
    }

    /// <summary>
    /// Display rows for a popup: .uasset/.uexp/.ubulk of one asset collapse into a
    /// single ".uasset" row, anything else shows as-is.
    /// </summary>
    public static List<string> DisplayNames(IEnumerable<string> manifestEntries)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in manifestEntries)
            set.Add(DisplayNameForRel(RelOf(e)));
        return set.ToList();
    }

    /// <summary>One display row for a Content-relative path (companions collapse).</summary>
    public static string DisplayNameForRel(string rel)
    {
        if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
            || rel.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase)
            || rel.EndsWith(".ubulk", StringComparison.OrdinalIgnoreCase))
        {
            int dot = rel.LastIndexOf('.');
            return (dot > 0 ? rel[..dot] : rel) + ".uasset";
        }
        return rel;
    }

    /// <summary>Content-relative path without the .uasset/.uexp/.ubulk companion suffix.</summary>
    public static string AssetStemOfRel(string rel)
    {
        if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
            || rel.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase)
            || rel.EndsWith(".ubulk", StringComparison.OrdinalIgnoreCase))
        {
            int dot = rel.LastIndexOf('.');
            return dot > 0 ? rel[..dot] : rel;
        }
        return rel;
    }

    /// <summary>Deletes the given manifest entries' files and trims emptied folders.
    /// Returns how many files were removed.</summary>
    public static int DeleteEntries(IEnumerable<string> manifestEntries)
    {
        int removed = 0;
        foreach (var entry in manifestEntries)
        {
            try
            {
                var rel = RelOf(entry);
                var path = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) continue;
                File.Delete(path);
                removed++;
                TrimEmptyDirs(Path.GetDirectoryName(path));
            }
            catch (Exception ex)
            {
                ErrorLog.Write("CUSTOM", ex);
            }
        }
        return removed;
    }

    private static void TrimEmptyDirs(string? startDir)
    {
        try
        {
            var rootFull = Path.GetFullPath(Root);
            var dir = startDir;
            while (!string.IsNullOrEmpty(dir))
            {
                var dirFull = Path.GetFullPath(dir);
                if (!dirFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) break;
                if (string.Equals(dirFull, rootFull, StringComparison.OrdinalIgnoreCase)) break;
                if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) break;
                Directory.Delete(dir, false);
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
    }

    /// <summary>
    /// Copies every file under the pak extract's content root(s) that does not exist
    /// in the vanilla content into Root, preserving layout. All folders are taken -
    /// Effects/Blueprints/Characters and friends can be referenced by the moveset
    /// files and must survive export. Returns files copied.
    /// </summary>
    public static int Harvest(string extractRoot, string vanillaContentDir)
    {
        int copied = 0;
        try
        {
            EnsureRoot();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wanted = new List<(string file, string rel)>();
            foreach (var contentRoot in FindContentRoots(extractRoot))
            {
                foreach (var file in Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories))
                {
                    if (!seen.Add(file)) continue;

                    // The extracted pak itself (a mod's .pak dropped at the extract
                    // root) is not content - harvesting it packs a pak-inside-pak
                    // (~599 MB of junk) into every export.
                    if (file.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) continue;

                    var rel = Path.GetRelativePath(contentRoot, file).Replace('\\', '/');
                    if (!string.IsNullOrEmpty(vanillaContentDir))
                    {
                        var vanillaPath = Path.Combine(vanillaContentDir,
                            rel.Replace('/', Path.DirectorySeparatorChar));
                        // Skip only files byte-identical to vanilla. An existence check here
                        // silently dropped every mod change that overlaps a vanilla path
                        // (the WW re-export lost ~1166 modified files that way).
                        if (FilesIdentical(vanillaPath, file)) continue;
                    }

                    wanted.Add((file, rel));
                }
            }

            // A cooked asset ships as a .uasset + .uexp (+ .ubulk/.uptnl) family that must
            // stay together: UAssetAPI resolves export payloads through the sibling .uexp,
            // so harvesting only the half that differs from vanilla leaves a lone .uasset
            // whose parse reads garbage (it silently broke unit-prop patching on the Kuroki
            // defenses). Any half that qualifies therefore drags its companions in too,
            // even when they are byte-identical to vanilla.
            var rels = new HashSet<string>(wanted.Select(w => w.rel), StringComparer.OrdinalIgnoreCase);
            var extras = new List<(string file, string rel)>();
            foreach (var (file, rel) in wanted)
            {
                if (!IsCompanionExt(Path.GetExtension(rel))) continue;
                var stem = rel[..^Path.GetExtension(rel).Length];
                var dir = Path.GetDirectoryName(file)!;
                foreach (var cExt in CompanionExts)
                {
                    var sibRel = stem + cExt;
                    if (!rels.Add(sibRel)) continue;
                    var sibFile = Path.Combine(dir, Path.GetFileName(stem) + cExt);
                    if (File.Exists(sibFile)) extras.Add((sibFile, sibRel));
                }
            }

            foreach (var (file, rel) in wanted.Concat(extras))
            {
                var dest = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(file),
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, true);
                copied++;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("CUSTOM", ex);
        }
        return copied;
    }

    private static readonly string[] CompanionExts = { ".uasset", ".uexp", ".ubulk", ".uptnl" };

    private static bool IsCompanionExt(string ext) =>
        ext.Equals(".uasset", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".uexp", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".ubulk", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".uptnl", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when both files exist with identical content. On any read error returns
    /// false (harvest it) - losing a file is worse than shipping a duplicate.
    /// </summary>
    private static bool FilesIdentical(string vanillaPath, string modPath)
    {
        try
        {
            var fa = new FileInfo(vanillaPath);
            var fb = new FileInfo(modPath);
            if (!fa.Exists || !fb.Exists) return false;
            if (fa.Length != fb.Length) return false;
            if (fa.Length == 0) return true;

            const int bufSize = 65536;
            using var sa = File.OpenRead(vanillaPath);
            using var sb = File.OpenRead(modPath);
            var ba = new byte[bufSize];
            var bb = new byte[bufSize];
            int ra, rb;
            while ((ra = ReadFull(sa, ba)) > 0)
            {
                rb = ReadFull(sb, bb);
                if (ra != rb) return false;
                for (int i = 0; i < ra; i++)
                    if (ba[i] != bb[i]) return false;
            }
            return ReadFull(sb, bb) == 0;
        }
        catch { return false; }

        static int ReadFull(Stream s, byte[] buf)
        {
            int off = 0, n;
            while (off < buf.Length && (n = s.Read(buf, off, buf.Length - off)) > 0)
                off += n;
            return off;
        }
    }

    /// <summary>
    /// Every "Content" root holding Animations/ or DB/ (fallback: the parent of any
    /// Animations/ or DB/ directory), plus the extract root itself so stray files
    /// outside the content tree still travel with the mod.
    /// </summary>
    private static List<string> FindContentRoots(string extractRoot)
    {
        var roots = new List<string>();
        try
        {
            foreach (var dir in Directory.GetDirectories(extractRoot, "Content", SearchOption.AllDirectories))
            {
                if (Directory.Exists(Path.Combine(dir, "Animations"))
                    || Directory.Exists(Path.Combine(dir, "DB")))
                    roots.Add(dir);
            }

            if (roots.Count == 0)
            {
                foreach (var marker in new[] { "Animations", "DB" })
                {
                    foreach (var dir in Directory.GetDirectories(extractRoot, marker, SearchOption.AllDirectories))
                    {
                        var parent = Directory.GetParent(dir)?.FullName;
                        if (parent != null && !roots.Contains(parent, StringComparer.OrdinalIgnoreCase))
                            roots.Add(parent);
                    }
                }
            }

            roots.Add(extractRoot);
        }
        catch { }
        if (roots.Count == 0) roots.Add(extractRoot);
        return roots;
    }
}
