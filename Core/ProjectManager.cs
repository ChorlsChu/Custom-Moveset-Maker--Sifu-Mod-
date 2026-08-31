using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SifuMovesetEditor;

public class EditorProject
{
    public string Name { get; set; } = "CustomMoveset";
    public Dictionary<string, string> NodeSwaps { get; set; } = new(); // nodeId -> animPath
}

public static class ProjectManager
{
    public static void Save(EditorProject project, ComboGraph graph, string filePath)
    {
        project.NodeSwaps.Clear();
        foreach (var node in graph.Nodes)
        {
            if (!node.IsRoot && node.AnimPath != node.DefaultAnimPath)
                project.NodeSwaps[node.Id.ToString()] = node.AnimPath;
        }

        string json = JsonConvert.SerializeObject(project, Formatting.Indented);
        File.WriteAllText(filePath, json);
    }

    public static (EditorProject project, Dictionary<string, string> swaps) Load(string filePath)
    {
        string json = File.ReadAllText(filePath);
        var project = JsonConvert.DeserializeObject<EditorProject>(json)
            ?? throw new Exception($"Failed to load project: {filePath}");
        return (project, project.NodeSwaps);
    }
}
