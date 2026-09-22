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
}

public class UnitCacheEntry
{
    public ComboGraph Graph { get; set; } = new();
    public Dictionary<int, Point> Positions { get; set; } = new();
    public string? ActiveVariant { get; set; }
    public string? ActiveWeapon { get; set; }
    public UnitProperties? Props { get; set; }
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
    public Dictionary<string, SavedUnitCache>? UnitCaches { get; set; }
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
            InputName = e.InputName
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

        string json = JsonConvert.SerializeObject(project, Formatting.Indented);
        File.WriteAllText(filePath, json);
    }

    public static void SaveWithCaches(EditorProject project, Dictionary<string, UnitCacheEntry> caches, string activeStance, string? activeVariant, string filePath)
    {
        project.ActiveStance = activeStance;
        project.ActiveVariant = activeVariant;
        project.UnitCaches = new Dictionary<string, SavedUnitCache>();

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
                CustomNodes = graph.Nodes
                    .Where(n => n.TreeIndex == -1 && !n.IsRoot)
                    .Select(n => new SavedCustomNode { Id = n.Id, DisplayName = n.DisplayName, AnimPath = n.AnimPath })
                    .ToList(),
                RedirectMods = graph.Nodes
                    .Where(n => n.IsRedirect && graph.RedirectOriginalTargets.TryGetValue(n.Id, out var orig) && n.ResolvedRedirectNodeId >= 0 && n.ResolvedRedirectNodeId != orig)
                    .ToDictionary(n => n.Id.ToString(), n => n.ResolvedRedirectNodeId)
            };

            if (pos != null)
            {
                saved.NodePositions = new Dictionary<string, SavedPosition>();
                foreach (var p in pos)
                    saved.NodePositions[p.Key.ToString()] = new SavedPosition { X = p.Value.X, Y = p.Value.Y };
            }

            project.UnitCaches[kvp.Key] = saved;
        }

        string json = JsonConvert.SerializeObject(project, Formatting.Indented);
        File.WriteAllText(filePath, json);
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
            InputName = se.InputName
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
                RedirectOriginalTargets = saved.RedirectOriginalTargets
            };

            var positions = new Dictionary<int, Point>();
            if (saved.NodePositions != null)
            {
                foreach (var p in saved.NodePositions)
                    if (int.TryParse(p.Key, out int nodeId))
                        positions[nodeId] = new Point(p.Value.X, p.Value.Y);
            }

            result[kvp.Key] = new UnitCacheEntry
            {
                Graph = graph,
                Positions = positions,
                ActiveVariant = saved.ActiveVariant,
                ActiveWeapon = saved.ActiveWeapon,
                Props = saved.Props
            };
        }
        return result;
    }
}
