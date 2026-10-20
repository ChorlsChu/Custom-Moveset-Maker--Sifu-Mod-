using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SifuMovesetEditor;

public class MeshCatalogEntry
{
    public string DisplayName { get; set; } = "";
    public string GamePath { get; set; } = "";
    public string Group { get; set; } = "";
}

/// <summary>
/// Model override catalog: every skeletal mesh under Content/Characters that shares the
/// PNJ skeleton (Base_skeleton), plus the per-family scenario file sets an override
/// is written to (local ArchetypeDB scenarios + Blueprint training/tutorial scenarios).
/// </summary>
public static class ModelCatalog
{
    private static readonly Dictionary<string, List<MeshCatalogEntry>> MeshCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Blueprint scenarios are not part of the extracted content tree - they are pulled
    // out of the original pak at export time. Keys are the Archetypes/<family> dir names.
    private static readonly Dictionary<string, string[]> BlueprintScenarioSources =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grunt"] =
        [
            "Blueprints/TrainingRoom/AI/Archetypes/Grunt",
            "Blueprints/TrainingRoom/AI/TrainingAI_Scenario",
            "Blueprints/Tutorial/AI/TutorialGrunt_Scenario",
        ],
        ["Bodyguard"] = ["Blueprints/TrainingRoom/AI/Archetypes/Bodyguard"],
        ["BigGuy"] = ["Blueprints/TrainingRoom/AI/Archetypes/BigGuy"],
        ["FlashKick"] = ["Blueprints/TrainingRoom/AI/Archetypes/Flashkick"],
        ["FireDisciple"] = ["Blueprints/TrainingRoom/AI/Archetypes/Disciple"],
        ["Fajar"] = ["Blueprints/TrainingRoom/AI/Bosses/Fajar"],
        ["Sean"] = ["Blueprints/TrainingRoom/AI/Bosses/Sean"],
        ["Kuroki"] = ["Blueprints/TrainingRoom/AI/Bosses/Kuroki"],
        ["Fengjie"] = ["Blueprints/TrainingRoom/AI/Bosses/Fengjie"],
        ["Yang"] = ["Blueprints/TrainingRoom/AI/Bosses/Yang"],
    };

    private static readonly string[] ExcludedMeshNameParts =
    {
        "PhysicsAsset", "Cloth", "Cine", "Skeleton", "T_Prop", "SKL_",
    };

    public static List<MeshCatalogEntry> ScanMeshes(string contentPath)
    {
        var contentDir = Setup.ContentDetector.ResolveContentDir(contentPath);
        if (MeshCache.TryGetValue(contentDir, out var cached)) return cached;

        var list = new List<MeshCatalogEntry>();
        try
        {
            CollectMeshEntries(Path.Combine(contentDir, "Characters"), contentDir, list);
            CollectMeshEntries(Path.Combine(CustomAssets.Root, "Characters"), CustomAssets.Root, list);

            // A CustomAssets copy at the same path is a deliberate override - it wins.
            var byPath = new Dictionary<string, MeshCatalogEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in list) byPath[e.GamePath] = e;
            list = byPath.Values.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModelCatalog] mesh scan error: {ex.Message}");
        }

        list = list
            .OrderBy(e => e.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        MeshCache[contentDir] = list;
        return list;
    }

    private static void CollectMeshEntries(string charsDir, string rootForRel, List<MeshCatalogEntry> list)
    {
        if (!Directory.Exists(charsDir)) return;

        foreach (var file in Directory.GetFiles(charsDir, "SK_*.uasset", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Equals("Base_skeleton", StringComparison.OrdinalIgnoreCase)) continue;
            bool excluded = false;
            foreach (var part in ExcludedMeshNameParts)
            {
                if (name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) { excluded = true; break; }
            }
            if (excluded) continue;
            if (!ReferencesBaseSkeleton(file)) continue;

            var rel = file.Substring(rootForRel.Length)
                .TrimStart('\\', '/').Replace('\\', '/');
            if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                rel = rel[..^".uasset".Length];

            list.Add(new MeshCatalogEntry
            {
                DisplayName = PrettyMeshName(name),
                GamePath = "/Game/" + rel,
                Group = GroupFor(rel),
            });
        }
    }

    /// <summary>
    /// Compatibility gate for the custom-model import: a cooked SK_ bundle whose import table
    /// targets Sifu's Base_skeleton. Returns null when the file passes, else the reason.
    /// The caller still has to prove it parses (real CUE4Parse load) after copying it in.
    /// </summary>
    public static string? ValidateCustomMesh(string uassetPath)
    {
        var name = Path.GetFileNameWithoutExtension(uassetPath);
        if (!name.StartsWith("SK_", StringComparison.Ordinal))
            return $"File name must start with SK_ (got '{name}').";
        foreach (var part in ExcludedMeshNameParts)
        {
            if (name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                return $"File name contains '{part}' - that is not a usable character mesh.";
        }

        try
        {
            var info = new FileInfo(uassetPath);
            if (info.Length > 64L * 1024 * 1024)
                return "Mesh is larger than 64 MB.";
            var uexp = Path.ChangeExtension(uassetPath, ".uexp");
            if (!File.Exists(uexp))
                return "Missing .uexp next to the .uasset - this is not a cooked Sifu mesh bundle.";
            if (!ReferencesBaseSkeleton(uassetPath))
                return "Does not reference Sifu's Base_skeleton - incompatible skeleton. Convert the model to Sifu's Base_skeleton first.";
        }
        catch (Exception ex)
        {
            return $"Could not read the file: {ex.Message}";
        }
        return null;
    }

    /// <summary>
    /// Cheap skeleton check: the import name table of a .uasset (bulk data lives in the
    /// .uexp) carries "Base_skeleton" for every asset on the shared PNJ skeleton.
    /// </summary>
    private static bool ReferencesBaseSkeleton(string uassetPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(uassetPath);
            var needle = "Base_skeleton"u8;
            for (int i = 0; i + needle.Length <= bytes.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && bytes[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
        }
        catch { }
        return false;
    }

    private static string PrettyMeshName(string fileName)
    {
        var name = fileName;
        if (name.StartsWith("SK_", StringComparison.OrdinalIgnoreCase))
            name = name[3..];
        return name.Replace('_', ' ');
    }

    private static string GroupFor(string contentRel)
    {
        var parts = contentRel.Split('/');
        if (parts.Length >= 3 && parts[0].Equals("Characters", StringComparison.OrdinalIgnoreCase))
        {
            if (parts[1].Equals("PNJ", StringComparison.OrdinalIgnoreCase) ||
                parts[1].Equals("Boss", StringComparison.OrdinalIgnoreCase))
                return parts[2];
            if (parts[1].Equals("MainChar", StringComparison.OrdinalIgnoreCase))
                return "MainChar";
            return parts[1];
        }
        return parts.Length > 0 ? parts[0] : "";
    }

    public static string DisplayNameForGamePath(string? gamePath)
    {
        if (string.IsNullOrEmpty(gamePath)) return "(default)";
        var idx = gamePath.LastIndexOf('/');
        var file = idx >= 0 ? gamePath[(idx + 1)..] : gamePath;
        return PrettyMeshName(file);
    }

    /// <summary>The Archetypes/&lt;family&gt; directory this variant's scenarios live under.</summary>
    public static string? FamilyDirForVariant(string? variantTag)
    {
        var archRel = UnitPropertiesManager.ResolveArchetypePath(variantTag ?? "");
        if (string.IsNullOrEmpty(archRel)) return null;

        var parts = archRel.Replace('\\', '/').Split('/');
        if (parts.Length >= 4 &&
            parts[0].Equals("DB", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("AI", StringComparison.OrdinalIgnoreCase) &&
            parts[2].Equals("Archetypes", StringComparison.OrdinalIgnoreCase))
            return parts[3];
        return null;
    }

    /// <summary>Every variant tag that shares this unit's scenario family.</summary>
    public static IReadOnlyList<string> GetFamilyVariantTags(string? familyDir)
    {
        if (string.IsNullOrEmpty(familyDir)) return Array.Empty<string>();
        var tags = new List<string>();
        foreach (var tag in UnitPropertiesManager.AllVariantTags)
        {
            var f = FamilyDirForVariant(tag);
            if (f != null && f.Equals(familyDir, StringComparison.OrdinalIgnoreCase))
                tags.Add(tag);
        }
        return tags;
    }

    /// <summary>
    /// Scenario uasset rels (content-relative, no extension) for a family in the local
    /// content tree: DB/AI/Archetypes/&lt;family&gt;/**/*Scenario*.
    /// </summary>
    public static List<string> LocalScenarioRels(string contentPath, string familyDir)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(familyDir)) return result;

        try
        {
            var contentDir = Setup.ContentDetector.ResolveContentDir(contentPath);
            var familyDirPath = Path.Combine(contentDir, "DB", "AI", "Archetypes",
                familyDir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(familyDirPath)) return result;

            foreach (var f in Directory.GetFiles(familyDirPath, "*Scenario*.uasset", SearchOption.AllDirectories))
            {
                var rel = f.Substring(contentDir.Length)
                    .TrimStart('\\', '/').Replace('\\', '/');
                if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                    rel = rel[..^".uasset".Length];
                result.Add(rel);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModelCatalog] scenario scan error: {ex.Message}");
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static readonly HashSet<string> FamilyWideScenarioFamilies =
        new(StringComparer.OrdinalIgnoreCase) { "Fajar", "Fengjie", "Kuroki", "Sean", "Yang", "Sifu" };

    /// <summary>
    /// Boss-style families whose P1/P2/P3 variant tags share one scenario file with per-phase
    /// nodes: a pick on any variant stages the whole family (conflicts keep the first pick).
    /// </summary>
    public static bool IsFamilyWideScenarioFamily(string? familyDir)
        => familyDir != null && FamilyWideScenarioFamilies.Contains(familyDir);

    /// <summary>
    /// Scenario rels whose spawns this variant's mesh pick should cover: the tier-matched
    /// subset of the family's scenarios (folder rules _Base/_Advanced/_Miniboss, then
    /// filename tokens incl. arena tiers). Family-wide and single-variant families map
    /// every scenario; unclassifiable files stay vanilla.
    /// </summary>
    public static List<string> ScenarioRelsForVariant(string contentPath, string? variantTag)
    {
        var result = new List<string>();
        var family = FamilyDirForVariant(variantTag);
        if (string.IsNullOrEmpty(family)) return result;

        var all = LocalScenarioRels(contentPath, family!);
        if (all.Count == 0) return all;

        if (GetFamilyVariantTags(family!).Count <= 1 || IsFamilyWideScenarioFamily(family!))
            return all;

        var tier = TierForVariantTag(variantTag!);
        if (tier.Length == 0) return result;

        foreach (var rel in all)
            if (string.Equals(ScenarioTierFor(rel), tier, StringComparison.OrdinalIgnoreCase))
                result.Add(rel);
        return result;
    }

    private static string TierForVariantTag(string variantTag)
    {
        if (variantTag.EndsWith("_Base", StringComparison.OrdinalIgnoreCase)) return "Base";
        if (variantTag.EndsWith("_Advanced", StringComparison.OrdinalIgnoreCase)) return "Advanced";
        if (variantTag.EndsWith("_Miniboss", StringComparison.OrdinalIgnoreCase)) return "Miniboss";
        return "";
    }

    private static string ScenarioTierFor(string rel)
    {
        var p = "/" + rel.ToLowerInvariant() + "/";
        if (p.Contains("/_old/") || p.Contains("/_tests/")) return "";
        if (p.Contains("/_base/")) return "Base";
        if (p.Contains("/_advanced/")) return "Advanced";
        if (p.Contains("/_miniboss/")) return "Miniboss";
        if (p.Contains("miniboss")) return "Miniboss";
        if (p.Contains("arenabase")) return "Base";
        if (p.Contains("arenaadv")) return "Advanced";
        if (p.Contains("_base")) return "Base";
        if (HasLevelToken(p)) return "Advanced";
        return "";
    }

    private static bool HasLevelToken(string lowerPathWithSlashes)
    {
        for (int i = 1; i < lowerPathWithSlashes.Length - 1; i++)
        {
            if (lowerPathWithSlashes[i] != 'h') continue;
            char prev = lowerPathWithSlashes[i - 1];
            char next = lowerPathWithSlashes[i + 1];
            if ((prev == '_' || prev == '/') && next >= '0' && next <= '9')
                return true;
        }
        return false;
    }

    /// <summary>
    /// Scenario rels for a family inside an extracted Blueprint content root
    /// (the temp dir holding Blueprints/TrainingRoom + Blueprints/Tutorial).
    /// </summary>
    public static List<string> BlueprintScenarioRels(string blueprintContentDir, string familyDir)    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(familyDir) ||
            !BlueprintScenarioSources.TryGetValue(familyDir, out var sources))
            return result;

        foreach (var srcRel in sources)
        {
            var asDir = Path.Combine(blueprintContentDir,
                srcRel.Replace('/', Path.DirectorySeparatorChar));
            var asFile = asDir + ".uasset";

            if (Directory.Exists(asDir))
            {
                foreach (var f in Directory.GetFiles(asDir, "*Scenario*.uasset", SearchOption.AllDirectories))
                {
                    var rel = f.Substring(blueprintContentDir.Length)
                        .TrimStart('\\', '/').Replace('\\', '/');
                    if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                        rel = rel[..^".uasset".Length];
                    result.Add(rel);
                }
            }
            else if (File.Exists(asFile))
            {
                result.Add(srcRel);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    // ---- Model set (roster swap) ----------------------------------------------------------

    /// <summary>Roster-swap keys: the six PNJ families (sources and targets) plus MainChar (source only).</summary>
    public static readonly string[] ModelSetKeys =
        ["Grunt", "Servant", "FireDisciple", "BigGuy", "Bodyguard", "FlashKick", "MainChar"];

    private static readonly Dictionary<string, string> ModelSetMeshGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grunt"] = "Grunt",
        ["Servant"] = "Servant",
        ["FireDisciple"] = "Disicple",
        ["BigGuy"] = "BigGuy",
        ["Bodyguard"] = "BodyGuards",
        ["FlashKick"] = "FlashKick",
        ["MainChar"] = "MainChar",
    };

    private static readonly Dictionary<string, string> ModelSetAIFamilyDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grunt"] = "Grunt",
        ["Servant"] = "Servant",
        ["FireDisciple"] = "FireDisciple",
        ["BigGuy"] = "BigGuy",
        ["Bodyguard"] = "Bodyguard",
        ["FlashKick"] = "Flashkick",
        ["MainChar"] = "",
    };

    /// <summary>The mesh catalog group a roster-swap key draws its meshes from.</summary>
    public static string? MeshGroupForKey(string key)
        => !string.IsNullOrEmpty(key) && ModelSetMeshGroups.TryGetValue(key, out var g) ? g : null;

    /// <summary>Blueprints/AI/&lt;dir&gt; folder holding the family's Character BPs; null when the key is not a swap target (MainChar).</summary>
    public static string? AIFamilyDirForKey(string key)
    {
        if (string.IsNullOrEmpty(key) || !ModelSetAIFamilyDirs.TryGetValue(key, out var dir)) return null;
        return string.IsNullOrEmpty(dir) ? null : dir;
    }

    public static bool IsModelSetKey(string? key)
        => !string.IsNullOrEmpty(key) && ModelSetMeshGroups.ContainsKey(key!);

    public sealed class RosterMesh
    {
        public string GamePath { get; set; } = "";
        public string Name { get; set; } = "";
        public string Gender { get; set; } = "M";
        public int? Hideout { get; set; }
    }

    /// <summary>
    /// Deterministic roster for a swap source: the group's catalog meshes (already filtered
    /// to Base_skeleton references, no physics/cloth/cine), Childs excluded, ordered by
    /// hideout then path so round-robin assignment is stable across runs.
    /// </summary>
    public static List<RosterMesh> BuildRoster(string contentPath, string sourceKey)
    {
        var result = new List<RosterMesh>();
        var group = MeshGroupForKey(sourceKey ?? "");
        if (string.IsNullOrEmpty(group)) return result;

        foreach (var entry in ScanMeshes(contentPath))
        {
            if (!entry.Group.Equals(group, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.GamePath.Contains("/Childs/", StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(new RosterMesh
            {
                GamePath = entry.GamePath,
                Name = entry.GamePath[(entry.GamePath.LastIndexOf('/') + 1)..],
                Gender = GenderOfMesh(entry.GamePath, sourceKey ?? ""),
                Hideout = HideoutOfPath(entry.GamePath),
            });
        }

        result.Sort((a, b) =>
        {
            int ha = a.Hideout ?? int.MaxValue;
            int hb = b.Hideout ?? int.MaxValue;
            int c = ha.CompareTo(hb);
            return c != 0 ? c : string.Compare(a.GamePath, b.GamePath, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    /// <summary>Mesh gender: M/W path segment, then a _M_/_W_ name token; family default M (BigGuy/FlashKick are all-male, the rest always carry a segment).</summary>
    public static string GenderOfMesh(string gamePath, string sourceKey)
    {
        foreach (var seg in gamePath.Split('/'))
        {
            if (seg.Equals("W", StringComparison.OrdinalIgnoreCase)) return "W";
            if (seg.Equals("M", StringComparison.OrdinalIgnoreCase)) return "M";
        }
        foreach (var tok in gamePath[(gamePath.LastIndexOf('/') + 1)..].Split('_'))
        {
            if (tok.Equals("W", StringComparison.OrdinalIgnoreCase)) return "W";
            if (tok.Equals("M", StringComparison.OrdinalIgnoreCase)) return "M";
        }
        return "M";
    }

    /// <summary>BP gender: /Female/ or /Male/ path segment, then a _W_ or _M_ name token; null means unknown (BigGuy/Flashkick - treated as male).</summary>
    public static string? BPGender(string relPath)
    {
        foreach (var seg in relPath.Split('/'))
        {
            if (seg.Equals("Female", StringComparison.OrdinalIgnoreCase)) return "W";
            if (seg.Equals("Male", StringComparison.OrdinalIgnoreCase)) return "M";
        }
        var name = relPath[(relPath.LastIndexOf('/') + 1)..];
        foreach (var tok in name.Split('_'))
        {
            if (tok.Equals("W", StringComparison.OrdinalIgnoreCase)) return "W";
            if (tok.Equals("M", StringComparison.OrdinalIgnoreCase)) return "M";
        }
        return null;
    }

    /// <summary>Hideout number from a path or asset name: /HideoutNN/, then _HDNN, then HideoutNN.</summary>
    public static int? HideoutOfPath(string pathAndName)
    {
        foreach (var pattern in new[] { @"/Hideout(\d+)", @"_HD(\d+)", @"Hideout(\d+)" })
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                pathAndName, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n)) return n;
        }
        return null;
    }

    /// <summary>
    /// Picks the roster entry for one target BP: same-gender + same-hideout bucket first,
    /// same-gender any-hideout as fallback, null when the source has no mesh of that gender.
    /// Counters are keyed per bucket so every bucket rotates on its own deterministic cycle.
    /// </summary>
    public static RosterMesh? AssignRosterMesh(
        IReadOnlyList<RosterMesh> roster, string? bpGender, int? bpHideout,
        IDictionary<string, int> counters)
    {
        if (roster.Count == 0) return null;
        string gender = string.IsNullOrEmpty(bpGender) ? "M" : bpGender!;

        var sameGender = new List<RosterMesh>();
        foreach (var m in roster)
            if (m.Gender.Equals(gender, StringComparison.OrdinalIgnoreCase)) sameGender.Add(m);
        if (sameGender.Count == 0) return null;

        var bucket = sameGender;
        string key = gender + "|*";
        if (bpHideout.HasValue)
        {
            var sameHideout = new List<RosterMesh>();
            foreach (var m in sameGender)
                if (m.Hideout == bpHideout.Value) sameHideout.Add(m);
            if (sameHideout.Count > 0)
            {
                bucket = sameHideout;
                key = gender + "|" + bpHideout.Value;
            }
        }

        int idx = counters.TryGetValue(key, out var c) ? c : 0;
        counters[key] = idx + 1;
        return bucket[idx % bucket.Count];
    }

    /// <summary>Drops the cached mesh scan so a fresh scan (e.g. after a custom import) sees new files.</summary>
    public static void InvalidateMeshCache() => MeshCache.Clear();

    // ---- Phase-swap target options --------------------------------------------------------

    /// <summary>One selectable phase-swap target moveset DB.</summary>
    public sealed class PhaseSwapTargetOption
    {
        public string Rel { get; init; } = "";       // content rel, no extension
        public string Family { get; init; } = "";    // Archetypes/<family> dir
        public string DisplayName { get; init; } = "";
        public string Label => $"{Family} / {DisplayName}";
    }

    private static readonly object PhaseOptionsLock = new();
    private static string? _phaseOptionsContentDir;
    private static List<PhaseSwapTargetOption>? _phaseOptionsCache;

    /// <summary>
    /// Every enemy/boss moveset DB under DB/AI/Archetypes/**: files starting with BP_ whose
    /// name carries ArchetypeDB, a LastMan Db or a Miniboss Db, excluding scenario assets,
    /// defense/vital-point data and the _old/_tests trees. Cached per content root.
    /// </summary>
    public static IReadOnlyList<PhaseSwapTargetOption> PhaseSwapTargetOptions(string contentPath)
    {
        var contentDir = Setup.ContentDetector.ResolveContentDir(contentPath);
        lock (PhaseOptionsLock)
        {
            if (_phaseOptionsCache != null
                && string.Equals(_phaseOptionsContentDir, contentDir, StringComparison.OrdinalIgnoreCase))
                return _phaseOptionsCache;

            var result = new List<PhaseSwapTargetOption>();
            try
            {
                var archRoot = Path.Combine(contentDir, "DB", "AI", "Archetypes");
                if (Directory.Exists(archRoot))
                {
                    foreach (var f in Directory.GetFiles(archRoot, "BP_*.uasset", SearchOption.AllDirectories))
                    {
                        var rel = f.Substring(contentDir.Length)
                            .TrimStart('\\', '/').Replace('\\', '/');
                        var lowerPath = "/" + rel.ToLowerInvariant() + "/";
                        if (lowerPath.Contains("/_old/") || lowerPath.Contains("/_tests/")) continue;

                        var leaf = Path.GetFileNameWithoutExtension(f);
                        var lowerLeaf = leaf.ToLowerInvariant();
                        if (lowerLeaf.Contains("scenario")) continue;
                        bool isDb = lowerLeaf.Contains("archetypedb")
                            || (lowerLeaf.Contains("lastman") && lowerLeaf.Contains("db"))
                            || (lowerLeaf.Contains("miniboss") && lowerLeaf.Contains("db"));
                        if (!isDb) continue;

                        var parts = rel.Split('/');
                        if (parts.Length < 5) continue; // DB/AI/Archetypes/<family>/...
                        string family = parts[3];

                        result.Add(new PhaseSwapTargetOption
                        {
                            Rel = rel[..^".uasset".Length],
                            Family = family,
                            DisplayName = PhaseTargetDisplayName(leaf, family),
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ModelCatalog] phase options scan error: {ex.Message}");
            }

            result.Sort((a, b) =>
            {
                int c = string.Compare(a.Family, b.Family, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            _phaseOptionsCache = result;
            _phaseOptionsContentDir = contentDir;
            return _phaseOptionsCache;
        }
    }

    /// <summary>
    /// "BP_FireDisciple_ArchetypeDB_MiniBoss_H5" -> "H5 (MiniBoss)"; "Yang_P3_Wude_ArchetypeDB" -> "P3 (Wude)".
    /// Drops the BP_ prefix and ArchetypeDB / LastMan Db / Miniboss Db marker segments, splits Arena*
    /// compounds, strips the leading family token, then formats the rest as
    /// "&lt;level&gt; &lt;extras&gt; (&lt;tier&gt;)" - a bare H-level implies "(Advanced)".
    /// </summary>
    private static string PhaseTargetDisplayName(string leaf, string family)
    {
        var name = leaf.StartsWith("BP_", StringComparison.OrdinalIgnoreCase) ? leaf[3..] : leaf;

        // marker segments: ArchetypeDB (removed), LastManDB -> LastMan, standalone DB/Db (removed)
        var tokens = new List<string>();
        foreach (var tok in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(tok, "ArchetypeDB", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(tok, "DB", StringComparison.OrdinalIgnoreCase)) continue;
            if (tok.Length > 2 && tok.EndsWith("DB", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(tok[..^2]);
                continue;
            }
            tokens.Add(tok);
        }

        // Arena compounds: ArenaMiniBoss -> Arena + MiniBoss
        var split = new List<string>(tokens.Count);
        foreach (var tok in tokens)
        {
            if (tok.Length > 5 && tok.StartsWith("Arena", StringComparison.OrdinalIgnoreCase))
            {
                split.Add("Arena");
                split.Add(tok[5..]);
            }
            else split.Add(tok);
        }
        tokens = split;

        // Strip one leading family (or stray non-tier) token, but never empty the list.
        if (tokens.Count > 1
            && (string.Equals(tokens[0], family, StringComparison.OrdinalIgnoreCase)
                || (!IsPhaseTierToken(tokens[0]) && !IsPhaseLevelToken(tokens[0]))))
            tokens.RemoveAt(0);

        if (tokens.Count == 0) return name;

        // tier token -> parenthesised suffix
        string? tier = null;
        int tierIdx = tokens.FindIndex(IsPhaseTierToken);
        if (tierIdx >= 0)
        {
            tier = PhaseTierDisplay(tokens[tierIdx]);
            tokens.RemoveAt(tierIdx);
        }

        // level tokens sort to the front of the head, the rest keep their order
        var head = tokens.Where(IsPhaseLevelToken)
            .Concat(tokens.Where(t => !IsPhaseLevelToken(t)))
            .ToList();

        if (tier == null)
        {
            var levels = head.Where(IsPhaseLevelToken).ToList();
            var others = head.Where(t => !IsPhaseLevelToken(t)).ToList();
            if (head.Count == 1 && levels.Count == 1) return $"{head[0]} (Advanced)"; // bare H-level
            if (levels.Count == 1 && others.Count == 1) return $"{levels[0]} ({others[0]})";
            return string.Join(" ", head);
        }
        return head.Count == 0 ? tier : $"{string.Join(" ", head)} ({tier})";
    }

    private static bool IsPhaseTierToken(string t) =>
        string.Equals(t, "Base", StringComparison.OrdinalIgnoreCase)
        || string.Equals(t, "Adv", StringComparison.OrdinalIgnoreCase)
        || string.Equals(t, "Advanced", StringComparison.OrdinalIgnoreCase)
        || string.Equals(t, "MiniBoss", StringComparison.OrdinalIgnoreCase)
        || string.Equals(t, "LastMan", StringComparison.OrdinalIgnoreCase)
        || string.Equals(t, "Stronger", StringComparison.OrdinalIgnoreCase);

    private static string PhaseTierDisplay(string t) =>
        string.Equals(t, "Adv", StringComparison.OrdinalIgnoreCase) ? "Advanced"
        : string.Equals(t, "Advanced", StringComparison.OrdinalIgnoreCase) ? "Advanced"
        : string.Equals(t, "MiniBoss", StringComparison.OrdinalIgnoreCase) ? "MiniBoss"
        : string.Equals(t, "LastMan", StringComparison.OrdinalIgnoreCase) ? "LastMan"
        : string.Equals(t, "Stronger", StringComparison.OrdinalIgnoreCase) ? "Stronger"
        : "Base";

    private static bool IsPhaseLevelToken(string t)
    {
        if (t.Length < 2) return false;
        if (t.StartsWith("Phase", StringComparison.OrdinalIgnoreCase))
            return t.Length > 5 && t.Skip(5).All(char.IsDigit);
        char c = char.ToUpperInvariant(t[0]);
        if (c != 'H' && c != 'P') return false;
        return t.Skip(1).All(char.IsDigit);
    }
}
