using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace SifuMovesetEditor;

/// <summary>
/// One cross-unit AI action/event node available to drop on the combo canvas.
/// ClassName is the generated-class short name without "_C" (matches ComboNode.ActionClass
/// / EventName), SourceComboRel is the Content-relative combo file whose node struct the
/// export clones, and PreviewAnimPath (Game/... format) is set when the action class
/// blueprint exposes a default animation - events never carry one.
/// </summary>
public class AiNodeInfo
{
    public string Kind { get; set; } = "Action";
    public string DisplayName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string SourceComboRel { get; set; } = "";
    public string SourceUnit { get; set; } = "";
    public int SourceTreeIndex { get; set; } = -1;
    public string? PreviewAnimPath { get; set; }
    public string Character => SourceUnit;
    public string DisplayNameClean => DisplayName;
    public bool IsValid => true;
    public string WeaponType => Kind;
    public string Category => PreviewAnimPath != null ? "Preview" : "";
    public string SourceFile => System.IO.Path.GetFileName(SourceComboRel);
}

/// <summary>
/// Scans every *Combo* asset under DB/AI for action nodes (m_AIAttackAction.m_DefaultClassObject
/// resolved) and event nodes (m_Event resolved), deduplicating by class with a preference for
/// non-arena files, and resolves each action class blueprint's CDO m_Animation so library
/// cards can preview. Scanned on first library mode switch and cached by the caller.
/// </summary>
public static class ActionNodeCatalog
{
    public static List<AiNodeInfo> Scan(string contentDir, Action<string>? progress = null)
    {
        var results = new List<AiNodeInfo>();
        if (string.IsNullOrEmpty(contentDir)) return results;

        var aiRoot = Path.Combine(contentDir, "DB", "AI");
        if (!Directory.Exists(aiRoot)) return results;

        var comboFiles = Directory.GetFiles(aiRoot, "*.uasset", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).Contains("Combo", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (comboFiles.Count == 0) return results;

        var classFiles = BuildClassFileIndex(contentDir);
        var best = new Dictionary<string, (AiNodeInfo info, int penalty)>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < comboFiles.Count; i++)
        {
            string file = comboFiles[i];
            if ((i & 7) == 0)
                progress?.Invoke($"Scanning combo trees... {i + 1}/{comboFiles.Count}");
            try
            {
                var asset = new UAsset(file, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
                var nodes = FindNodesArray(asset);
                if (nodes?.Value == null) continue;

                string rel = Path.GetRelativePath(contentDir, file).Replace('\\', '/');
                string relNoExt = rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ? rel[..^7] : rel;
                string unit = UnitOf(relNoExt);
                int penalty = IsArena(relNoExt) ? 1 : 0;

                for (int t = 0; t < nodes.Value.Length; t++)
                {
                    if (nodes.Value[t] is not StructPropertyData ns || ns.Value == null) continue;

                    var redirect = ns.Value.OfType<IntPropertyData>()
                        .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_NodeRedirect");
                    if (redirect != null && redirect.Value >= 0) continue;
                    if (HasAttackSlot(ns)) continue;

                    string? actionName = ActionClassName(asset, ns);
                    string? eventName = actionName == null ? EventClassName(asset, ns) : null;
                    string? className = actionName ?? eventName;
                    if (string.IsNullOrEmpty(className)) continue;
                    string clean = StripC(className!);
                    if (clean.Length == 0) continue;

                    bool isEvent = actionName == null;
                    string kind = isEvent ? "Event" : "Action";
                    string key = kind + "|" + clean;

                    if (!best.TryGetValue(key, out var cur) || penalty < cur.penalty)
                    {
                        best[key] = (new AiNodeInfo
                        {
                            Kind = kind,
                            DisplayName = clean,
                            ClassName = clean,
                            SourceComboRel = relNoExt,
                            SourceUnit = unit,
                            SourceTreeIndex = t
                        }, penalty);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Write("AI-CATALOG", new Exception($"scan failed for {Path.GetFileName(file)}: {ex.Message}"));
            }
        }

        foreach (var (info, _) in best.Values.OrderBy(v => v.info.Kind, StringComparer.Ordinal)
                                             .ThenBy(v => v.info.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (info.Kind == "Action")
                info.PreviewAnimPath = ResolveClassAnimPath(contentDir, classFiles, info.ClassName);
            results.Add(info);
        }

        progress?.Invoke($"Found {results.Count} AI actions/events");
        return results;
    }

    private static Dictionary<string, string> BuildClassFileIndex(string contentDir)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void ScanRoot(string root)
        {
            foreach (var sub in new[] { Path.Combine(root, "DB", "AI"), Path.Combine(root, "Blueprints", "AI") })
            {
                if (!Directory.Exists(sub)) continue;
                foreach (var f in Directory.GetFiles(sub, "*.uasset", SearchOption.AllDirectories))
                {
                    string stem = Path.GetFileNameWithoutExtension(f);
                    if (!index.ContainsKey(stem)) index[stem] = f;
                }
            }
        }

        ScanRoot(contentDir);

        // The working copy holds only Animations/Characters/DB - action blueprints hosted
        // under Blueprints\AI (e.g. BP_AiAction_Vanish) live in an extracted pak tree.
        // Fill the gap from any extractedPaks-style root discoverable near the content dir.
        if (!Directory.Exists(Path.Combine(contentDir, "Blueprints")))
        {
            foreach (var root in CandidateClassFileRoots(contentDir))
            {
                ScanRoot(root);
                try
                {
                    foreach (var pakDir in Directory.GetDirectories(root))
                    {
                        if (!Directory.Exists(Path.Combine(pakDir, "Blueprints"))) continue;
                        ScanRoot(pakDir);
                        foreach (var inner in new[]
                        {
                            Path.Combine(pakDir, "Sifu", "Content"),
                            Path.Combine(pakDir, "Content")
                        })
                            if (Directory.Exists(Path.Combine(inner, "Blueprints")))
                                ScanRoot(inner);
                    }
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("AI-CATALOG", new Exception($"class fallback scan failed for {root}: {ex.Message}"));
                }
            }
        }
        return index;
    }

    /// <summary>
    /// Bounded roots that may hold a full extracted content tree: the app-conventional
    /// extractedPaks folder next to the exe, plus extractedPaks children discovered by
    /// walking a few parent levels of the content dir (fixed patterns only - no deep search).
    /// </summary>
    private static List<string> CandidateClassFileRoots(string contentDir)
    {
        var roots = new List<string>();
        void Add(string? r)
        {
            if (string.IsNullOrEmpty(r) || roots.Contains(r!, StringComparer.OrdinalIgnoreCase)) return;
            try { if (Directory.Exists(r!)) roots.Add(r!); } catch { }
        }

        Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extractedPaks"));
        try
        {
            var walk = new DirectoryInfo(contentDir);
            for (int i = 0; i < 7 && walk != null; i++, walk = walk.Parent)
            {
                Add(Path.Combine(walk.FullName, "extractedPaks"));
                foreach (var child in Directory.GetDirectories(walk.FullName))
                    Add(Path.Combine(child, "extractedPaks"));
            }
        }
        catch { }
        return roots;
    }

    private static string? ResolveClassAnimPath(string contentDir, Dictionary<string, string> classFiles, string className)
    {
        try
        {
            if (!classFiles.TryGetValue(className, out var file)) return null;
            var asset = new UAsset(file, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            string defaultName = "Default__" + (className.EndsWith("_C", StringComparison.Ordinal) ? className : className + "_C");
            foreach (var export in asset.Exports)
            {
                if (export is not NormalExport ne || ne.Data == null) continue;
                if (!string.Equals(export.ObjectName?.Value?.ToString(), defaultName, StringComparison.Ordinal)) continue;

                string? pkg = FindAnimPackage(asset, ne.Data, new HashSet<int>(), 0);
                if (pkg == null) return null;
                if (!pkg.StartsWith("/Game/", StringComparison.Ordinal)) return null;
                return "Game/" + pkg["/Game/".Length..];
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("AI-CATALOG", new Exception($"anim resolve failed for {className}: {ex.Message}"));
        }
        return null;
    }

    /// <summary>
    /// Deep search for the first animation package referenced by an action blueprint CDO:
    /// the plain m_Animation.m_animation field, or - as BP_AiAction_Vanish and the
    /// FireDisciple WallJump action do - m_OrderParamsInstances map values pointing at
    /// export instances whose m_Params.m_AnimContainerToPlay.m_animation holds the anim.
    /// </summary>
    private static string? FindAnimPackage(UAsset asset, IEnumerable<PropertyData> props, HashSet<int> visitedExports, int depth)
    {
        if (depth > 12) return null;
        foreach (var p in props)
        {
            if (p == null) continue;
            if (p is ObjectPropertyData op && op.Value != null)
            {
                string? pkg = ResolvePackagePath(asset, op.Value);
                if (pkg != null)
                {
                    if (pkg.Contains("/Animations/", StringComparison.OrdinalIgnoreCase)) return pkg;
                }
                else if (op.Value.Index > 0 && visitedExports.Add(op.Value.Index))
                {
                    int ei = op.Value.Index - 1;
                    if (ei < asset.Exports.Count && asset.Exports[ei] is NormalExport child && child.Data != null)
                    {
                        string? found = FindAnimPackage(asset, child.Data, visitedExports, depth + 1);
                        if (found != null) return found;
                    }
                }
            }
            else if (p is StructPropertyData sp && sp.Value != null)
            {
                string? found = FindAnimPackage(asset, sp.Value, visitedExports, depth + 1);
                if (found != null) return found;
            }
            else if (p is ArrayPropertyData ap && ap.Value != null)
            {
                string? found = FindAnimPackage(asset, ap.Value, visitedExports, depth + 1);
                if (found != null) return found;
            }
            else if (p is MapPropertyData mp && mp.Value != null)
            {
                var vals = mp.Value.Select(kvp => kvp.Value).Where(v => v != null)!;
                string? found = FindAnimPackage(asset, vals, visitedExports, depth + 1);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static string? ResolvePackagePath(UAsset asset, FPackageIndex? idx)
    {
        if (idx == null || idx.Index >= 0) return null;
        int i = -(idx.Index + 1);
        if (i < 0 || i >= asset.Imports.Count) return null;
        var imp = asset.Imports[i];
        int o = imp.OuterIndex.Index;
        if (o >= 0 || -(o + 1) >= asset.Imports.Count) return null;
        var pkg = asset.Imports[-(o + 1)].ObjectName.Value?.ToString();
        if (string.IsNullOrEmpty(pkg) || !pkg!.StartsWith("/Game/", StringComparison.Ordinal)) return null;
        return pkg;
    }

    private static ArrayPropertyData? FindNodesArray(UAsset asset)
    {
        foreach (var export in asset.Exports)
        {
            if (export is not NormalExport ne || ne.Data == null) continue;
            var nodes = ne.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Nodes");
            if (nodes != null) return nodes;
        }
        return null;
    }

    private static bool HasAttackSlot(StructPropertyData node)
    {
        var infos = node.Value?.OfType<StructPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos");
        if (infos?.Value == null) return false;
        foreach (var n in infos.Value)
        {
            if (n is NamePropertyData np && np.Value?.Value?.ToString() is string v &&
                v.Length > 0 && v != "None")
                return true;
            if (n is ArrayPropertyData ap && ap.Value != null)
                foreach (var el in ap.Value)
                    if (el is NamePropertyData np2 && np2.Value?.Value?.ToString() is string v2 &&
                        v2.Length > 0 && v2 != "None")
                        return true;
        }
        return false;
    }

    internal static string? ActionClassName(UAsset asset, StructPropertyData node)
    {
        var action = node.Value?.OfType<StructPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AIAttackAction");
        var classProp = action?.Value?.OfType<ObjectPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_DefaultClassObject");
        return ResolveObjectName(asset, classProp?.Value);
    }

    internal static string? EventClassName(UAsset asset, StructPropertyData node)
    {
        var ev = node.Value?.OfType<ObjectPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Event");
        if (ev?.Value == null || ev.Value.Index == 0) return null;
        int idx = ev.Value.Index;
        if (idx > 0)
        {
            if (idx - 1 >= asset.Exports.Count) return null;
            var exp = asset.Exports[idx - 1];
            if (exp.ClassIndex != null && exp.ClassIndex.Index < 0)
            {
                int ci = -(exp.ClassIndex.Index + 1);
                if (ci >= 0 && ci < asset.Imports.Count)
                    return asset.Imports[ci].ObjectName.Value?.ToString();
            }
            return StripSuffixCopy(exp.ObjectName?.Value?.ToString());
        }
        return ResolveObjectName(asset, ev.Value);
    }

    internal static string? ResolveObjectName(UAsset asset, FPackageIndex? idx)
    {
        if (idx == null || idx.Index == 0) return null;
        if (idx.Index < 0)
        {
            int i = -(idx.Index + 1);
            if (i < 0 || i >= asset.Imports.Count) return null;
            return asset.Imports[i].ObjectName.Value?.ToString();
        }
        if (idx.Index - 1 >= asset.Exports.Count) return null;
        return asset.Exports[idx.Index - 1].ObjectName?.Value?.ToString();
    }

    private static string StripSuffixCopy(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        int cut = name!.IndexOf("_Copy_", StringComparison.Ordinal);
        return cut > 0 ? name[..cut] : name;
    }

    private static string StripC(string name) =>
        name.EndsWith("_C", StringComparison.Ordinal) ? name[..^2] : name;

    private static string UnitOf(string contentRel)
    {
        var parts = contentRel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
            if (string.Equals(parts[i], "Archetypes", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1];
        return parts.Length >= 2 ? parts[^2] : "";
    }

    private static bool IsArena(string contentRel) =>
        contentRel.Contains("_Arena", StringComparison.OrdinalIgnoreCase) ||
        contentRel.Contains("ArenaMode", StringComparison.OrdinalIgnoreCase) ||
        contentRel.Contains("RedRoom", StringComparison.OrdinalIgnoreCase);
}
