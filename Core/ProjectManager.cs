using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Newtonsoft.Json;

namespace SifuMovesetEditor;

public class SavedEdge
{
    public int FromNodeId { get; set; }
    public int ToNodeId { get; set; }
    public string InputName { get; set; } = "";
    // -1 for user-created links; otherwise the redirect node whose re-sourced rows
    // this edge belongs to (A' pass-through). Absent in old saves (deserializes -1).
    public int OwnerRedirectNodeId { get; set; } = -1;
}

public class SavedPosition
{
    public double X { get; set; }
    public double Y { get; set; }
}

public class SavedCustomNode
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string AnimPath { get; set; } = "";
}

public class SavedUnitCache
{
    public List<ComboNode> Nodes { get; set; } = new();
    public List<ComboEdge> Edges { get; set; } = new();
    public string WeaponName { get; set; } = "";
    public Dictionary<int, int> RedirectOriginalTargets { get; set; } = new();
    public Dictionary<string, SavedPosition> NodePositions { get; set; } = new();
    public string? ActiveVariant { get; set; }
    public string? ActiveWeapon { get; set; }
    public List<SavedCustomNode> CustomNodes { get; set; } = new();
    public Dictionary<string, int> RedirectMods { get; set; } = new();
    public UnitProperties? Props { get; set; }
    public List<StartEntry>? StartEntries { get; set; }
    // Branch chances: vanilla m_Transitions rows per struct tree index + user overrides
    // (struct tree index -> row index -> value). Null on old saves -> dialog falls back
    // to new-link rows only, export still reads probabilities from the asset itself.
    public Dictionary<int, List<TreeRowInfo>>? TreeRows { get; set; }
    // Source combo game path for TreeRows backfill; null on old saves.
    public string? SourceGamePath { get; set; }
    public Dictionary<int, Dictionary<int, float>>? TreeRowProbabilityOverrides { get; set; }
    // Node-level condition data (required tags + conditional attacks); null on old saves.
    public Dictionary<int, TreeNodeConditionInfo>? TreeNodeConditions { get; set; }
    // Condition editing: param overrides keyed by condition export index -> (path ->
    // value), and row-level clear/re-route edits (struct idx -> row idx -> edit).
    // Null on old saves.
    public Dictionary<int, Dictionary<string, string>>? TreeConditionParamOverrides { get; set; }
    public Dictionary<int, Dictionary<int, TreeRowConditionEdit>>? TreeRowConditionOverrides { get; set; }
    // Condition edits on rows that only exist after export (new-link rows), keyed by
    // ComboGraph.EdgeConditionKey. Null on old saves.
    public Dictionary<string, TreeRowConditionEdit>? TreeEdgeConditionOverrides { get; set; }
    // Which redirect last re-routed each row/edge outcome key (retarget data-swap
    // provenance). Null on old saves.
    public Dictionary<string, int>? RetargetProvenance { get; set; }
    // Stripped conduit routing (raw file index -> downstream indices); display
    // annotation only. Null on old saves.
    public Dictionary<int, List<int>>? ConduitTargets { get; set; }
    // Marker written only by builds that persist ComboEdge.OwnerRedirectNodeId /
    // ComboNode.OwnedContinuation; old saves deserialize null -> needs backfill.
    public bool? EdgeOwnersPersisted { get; set; }
}

public class UnitCacheEntry
{
    public ComboGraph Graph { get; set; } = new();
    public Dictionary<int, Point> Positions { get; set; } = new();
    public string? ActiveVariant { get; set; }
    public string? ActiveWeapon { get; set; }
    public UnitProperties? Props { get; set; }
    public bool NeedsEdgeOwnerBackfill { get; set; }
}

/// <summary>
/// One CustomAssets asset embedded in the project file so a save travels without the
/// CustomAssets folder. Rel is the content-relative .uasset stem ('/' separators);
/// Parts maps the companion extension (".uasset"/".uexp"/".ubulk"/".uptnl") to base64.
/// </summary>
public class SavedCustomFile
{
    public string Rel { get; set; } = "";
    public Dictionary<string, string>? Parts { get; set; }
}

public class EditorProject
{
    public string Name { get; set; } = "CustomMoveset";
    public string? ActiveStance { get; set; }
    public string? ActiveVariant { get; set; }
    public string? ActiveWeapon { get; set; }
    public Dictionary<string, string> NodeSwaps { get; set; } = new();
    public List<SavedEdge> Edges { get; set; } = new();
    public Dictionary<string, SavedPosition> NodePositions { get; set; } = new();
    public List<SavedCustomNode> CustomNodes { get; set; } = new();
    public Dictionary<string, int> RedirectMods { get; set; } = new();
    public List<int> RemovedNodes { get; set; } = new();
    public Dictionary<string, SavedUnitCache>? UnitCaches { get; set; }
    // Present only while the project is a custom moveset; old saves deserialize null.
    public CustomMovesetInfo? CustomMoveset { get; set; }
    // CustomAssets files the saved graphs reference (anims, attack cards, the
    // HitBoxData tables behind them). Null/absent on old saves - load is a no-op.
    public List<SavedCustomFile>? CustomFilePayloads { get; set; }
}

public static class ProjectManager
{
    public static void Save(EditorProject project, ComboGraph graph, Dictionary<int, Point> positions, string activeStance, string? activeVariant, string filePath)
    {
        project.ActiveStance = activeStance;
        project.ActiveVariant = activeVariant;

        project.NodeSwaps.Clear();
        foreach (var node in graph.Nodes)
        {
            if (!node.IsRoot && node.AnimPath != node.DefaultAnimPath)
                project.NodeSwaps[node.Id.ToString()] = node.AnimPath;
        }

        project.Edges = graph.Edges.Select(e => new SavedEdge
        {
            FromNodeId = e.FromNodeId,
            ToNodeId = e.ToNodeId,
            InputName = e.InputName,
            OwnerRedirectNodeId = e.OwnerRedirectNodeId
        }).ToList();

        project.NodePositions = new Dictionary<string, SavedPosition>();
        if (positions != null)
        {
            foreach (var kvp in positions)
                project.NodePositions[kvp.Key.ToString()] = new SavedPosition { X = kvp.Value.X, Y = kvp.Value.Y };
        }

        project.CustomNodes = graph.Nodes
            .Where(n => n.TreeIndex == -1 && !n.IsRoot)
            .Select(n => new SavedCustomNode
            {
                Id = n.Id,
                DisplayName = n.DisplayName,
                AnimPath = n.AnimPath
            })
            .ToList();

        project.RedirectMods = graph.Nodes
            .Where(n => n.IsRedirect && graph.RedirectOriginalTargets.TryGetValue(n.Id, out var orig) && n.ResolvedRedirectNodeId >= 0 && n.ResolvedRedirectNodeId != orig)
            .ToDictionary(n => n.Id.ToString(), n => n.ResolvedRedirectNodeId);

        project.RemovedNodes = graph.Nodes.Where(n => n.Removed).Select(n => n.Id).ToList();

        string json = JsonConvert.SerializeObject(project, Formatting.Indented);
        File.WriteAllText(filePath, json);
    }

    public static void SaveWithCaches(EditorProject project, Dictionary<string, UnitCacheEntry> caches, string activeStance, string? activeVariant, string? activeWeapon, string filePath)
    {
        project.ActiveStance = activeStance;
        project.ActiveVariant = activeVariant;
        project.ActiveWeapon = activeWeapon;
        project.UnitCaches = new Dictionary<string, SavedUnitCache>();
        project.RemovedNodes = new List<int>();

        foreach (var kvp in caches)
        {
            var graph = kvp.Value.Graph;
            var pos = kvp.Value.Positions;

            var saved = new SavedUnitCache
            {
                Nodes = graph.Nodes,
                Edges = graph.Edges,
                WeaponName = graph.WeaponName,
                RedirectOriginalTargets = graph.RedirectOriginalTargets,
                ActiveVariant = kvp.Value.ActiveVariant,
                ActiveWeapon = kvp.Value.ActiveWeapon,
                Props = kvp.Value.Props,
                StartEntries = graph.StartEntries,
                TreeRows = graph.TreeRows,
                SourceGamePath = graph.SourceGamePath,
                TreeRowProbabilityOverrides = graph.TreeRowProbabilityOverrides,
                TreeNodeConditions = graph.TreeNodeConditions,
                TreeConditionParamOverrides = graph.TreeConditionParamOverrides,
                TreeRowConditionOverrides = graph.TreeRowConditionOverrides,
                TreeEdgeConditionOverrides = graph.TreeEdgeConditionOverrides,
                RetargetProvenance = graph.RetargetProvenance,
                ConduitTargets = graph.ConduitTargets,
                CustomNodes = graph.Nodes
                    .Where(n => n.TreeIndex == -1 && !n.IsRoot)
                    .Select(n => new SavedCustomNode { Id = n.Id, DisplayName = n.DisplayName, AnimPath = n.AnimPath })
                    .ToList(),
                RedirectMods = graph.Nodes
                    .Where(n => n.IsRedirect && graph.RedirectOriginalTargets.TryGetValue(n.Id, out var orig) && n.ResolvedRedirectNodeId >= 0 && n.ResolvedRedirectNodeId != orig)
                    .ToDictionary(n => n.Id.ToString(), n => n.ResolvedRedirectNodeId),
                EdgeOwnersPersisted = true
            };

            if (pos != null)
            {
                saved.NodePositions = new Dictionary<string, SavedPosition>();
                foreach (var p in pos)
                    saved.NodePositions[p.Key.ToString()] = new SavedPosition { X = p.Value.X, Y = p.Value.Y };
            }

            project.UnitCaches[kvp.Key] = saved;
        }

        project.CustomFilePayloads = CollectCustomFilePayloads(caches);

        string json = JsonConvert.SerializeObject(project, Formatting.Indented);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Embeds every CustomAssets file the saved graphs reference - node anims, attack
    /// cards (DefaultDBPath/SourceDBPath/ReplacedAttackCardRel) and the HitBoxData
    /// tables behind those cards - so the project restores on a machine without the
    /// CustomAssets folder. References that don't resolve to a CustomAssets file are
    /// vanilla content and are skipped. Returns null when nothing is embedded.
    /// </summary>
    public static List<SavedCustomFile>? CollectCustomFilePayloads(Dictionary<string, UnitCacheEntry>? caches)
    {
        try
        {
            if (caches == null || caches.Count == 0 || !Directory.Exists(CustomAssets.Root))
                return null;

            var stems = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in caches)
            {
                var graph = kvp.Value.Graph;
                if (graph?.Nodes == null) continue;
                foreach (var node in graph.Nodes)
                {
                    void AddRef(string gamePath)
                    {
                        if (string.IsNullOrEmpty(gamePath)) return;
                        var rel = AttackDbCard.GamePathToContentRel(gamePath);
                        if (string.IsNullOrEmpty(rel)) return;
                        if (File.Exists(CustomAssets.FileForContentRel(rel + ".uasset")))
                            stems.Add(rel);
                    }

                    AddRef(node.AnimPath);
                    AddRef(node.DefaultDBPath);
                    AddRef(node.SourceDBPath);
                    AddRef(node.ReplacedAttackCardRel);

                    var card = AttackDbCard.EffectiveCardPath(node);
                    if (string.IsNullOrEmpty(card)) continue;
                    var hit = AttackHitCard.ReadDbPath(CustomAssets.Root, card);
                    if (hit == null) continue;
                    foreach (var w in hit.Windows)
                        if (!string.IsNullOrEmpty(w.TableRel))
                            AddRef(w.TableRel);
                }
            }
            if (stems.Count == 0) return null;

            var payload = new List<SavedCustomFile>();
            foreach (var stem in stems)
            {
                var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk", ".uptnl" })
                {
                    var file = CustomAssets.FileForContentRel(stem + ext);
                    if (!File.Exists(file)) continue;
                    parts[ext] = Convert.ToBase64String(File.ReadAllBytes(file));
                }
                if (parts.Count > 0)
                    payload.Add(new SavedCustomFile { Rel = stem, Parts = parts });
            }
            return payload.Count > 0 ? payload : null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("PROJECT", ex);
            return null;
        }
    }

    public static (EditorProject project, Dictionary<string, string> swaps, List<ComboEdge> edges, Dictionary<int, Point> positions, List<SavedCustomNode> customNodes, Dictionary<string, int> redirectMods) Load(string filePath)
    {
        string json = File.ReadAllText(filePath);
        var project = JsonConvert.DeserializeObject<EditorProject>(json)
            ?? throw new Exception($"Failed to load project: {filePath}");

        var edges = project.Edges.Select(se => new ComboEdge
        {
            FromNodeId = se.FromNodeId,
            ToNodeId = se.ToNodeId,
            InputName = se.InputName,
            OwnerRedirectNodeId = se.OwnerRedirectNodeId
        }).ToList();

        var positions = new Dictionary<int, Point>();
        foreach (var kvp in project.NodePositions)
        {
            if (int.TryParse(kvp.Key, out int nodeId))
                positions[nodeId] = new Point(kvp.Value.X, kvp.Value.Y);
        }

        return (project, project.NodeSwaps, edges, positions, project.CustomNodes, project.RedirectMods);
    }

    public static Dictionary<string, UnitCacheEntry>? LoadUnitCaches(EditorProject project)
    {
        if (project.UnitCaches == null || project.UnitCaches.Count == 0)
            return null;

        var result = new Dictionary<string, UnitCacheEntry>();
        foreach (var kvp in project.UnitCaches)
        {
            var saved = kvp.Value;
            var graph = new ComboGraph
            {
                WeaponName = saved.WeaponName,
                Nodes = saved.Nodes,
                Edges = saved.Edges,
                RedirectOriginalTargets = saved.RedirectOriginalTargets,
                StartEntries = saved.StartEntries ?? new List<StartEntry>(),
                TreeRows = saved.TreeRows ?? new Dictionary<int, List<TreeRowInfo>>(),
                SourceGamePath = saved.SourceGamePath ?? "",
                TreeRowProbabilityOverrides = saved.TreeRowProbabilityOverrides ?? new Dictionary<int, Dictionary<int, float>>(),
                TreeNodeConditions = saved.TreeNodeConditions ?? new Dictionary<int, TreeNodeConditionInfo>(),
                TreeConditionParamOverrides = saved.TreeConditionParamOverrides ?? new Dictionary<int, Dictionary<string, string>>(),
                TreeRowConditionOverrides = saved.TreeRowConditionOverrides ?? new Dictionary<int, Dictionary<int, TreeRowConditionEdit>>(),
                TreeEdgeConditionOverrides = saved.TreeEdgeConditionOverrides ?? new Dictionary<string, TreeRowConditionEdit>(),
                RetargetProvenance = saved.RetargetProvenance ?? new Dictionary<string, int>(),
                ConduitTargets = saved.ConduitTargets ?? new Dictionary<int, List<int>>()
            };

            var positions = new Dictionary<int, Point>();
            if (saved.NodePositions != null)
            {
                foreach (var p in saved.NodePositions)
                    if (int.TryParse(p.Key, out int nodeId))
                        positions[nodeId] = new Point(p.Value.X, p.Value.Y);
            }

            SanitizeMainCharCache(kvp.Key, graph, positions, saved.ActiveWeapon);
            graph.RehydrateStartEntryKeyModels();

            result[kvp.Key] = new UnitCacheEntry
            {
                Graph = graph,
                Positions = positions,
                ActiveVariant = saved.ActiveVariant,
                ActiveWeapon = saved.ActiveWeapon,
                Props = saved.Props,
                NeedsEdgeOwnerBackfill = saved.EdgeOwnersPersisted != true
            };
        }
        return result;
    }

    // Internal: ProjectMigrator re-applies this to a freshly parsed graph after a
    // legacy-schema migration (the fresh parse re-introduces stance nodes the saved
    // cache had already been sanitized of).
    internal static void SanitizeMainCharCache(string unitKey, ComboGraph graph, Dictionary<int, Point> positions, string? activeWeapon)
    {
        var arch = unitKey.Contains('|') ? unitKey.Split('|')[0] : unitKey;
        if (!string.Equals(arch, "MainChar", StringComparison.OrdinalIgnoreCase))
            return;

        var weaponTag = !string.IsNullOrEmpty(activeWeapon)
            ? activeWeapon
            : (unitKey.Contains('|') ? unitKey.Split('|').Last() : null);

        bool isBarehands = string.IsNullOrEmpty(weaponTag)
            || string.Equals(weaponTag, "MainChar", StringComparison.OrdinalIgnoreCase)
            || string.Equals(weaponTag, "BareHands", StringComparison.OrdinalIgnoreCase)
            || weaponTag.EndsWith("Barehands", StringComparison.OrdinalIgnoreCase);

        graph.WeaponName = isBarehands
            ? "BareHands"
            : weaponTag!;

        if (isBarehands)
            return;

        var stanceIds = graph.Nodes
            .Where(n => string.Equals(n.Name, "MainChar_Stance", StringComparison.Ordinal))
            .Select(n => n.Id)
            .ToHashSet();
        if (stanceIds.Count == 0)
            return;

        graph.Nodes.RemoveAll(n => stanceIds.Contains(n.Id));
        graph.Edges.RemoveAll(e => stanceIds.Contains(e.FromNodeId) || stanceIds.Contains(e.ToNodeId));
        foreach (var id in stanceIds)
            positions.Remove(id);
    }
}
