#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SifuMovesetEditor;

var contentPath = args.Length > 0 ? args[0] :
    @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu";

Console.WriteLine($"Content path: {contentPath}");
Console.WriteLine();

var allTags = UnitPropertiesManager.AllVariantTags;
Console.WriteLine($"Found {allTags.Count} variant tags to scan");
Console.WriteLine();

var results = new Dictionary<string, Dictionary<string, object?>>();
int scanned = 0, errors = 0;

foreach (var tag in allTags)
{
    Console.Write($"  {tag,-30} ");
    try
    {
        var props = UnitPropertiesManager.Read(contentPath, tag);
        var entry = new Dictionary<string, object?>();
        entry["Health"] = props.Health;
        entry["Structure"] = props.Structure;
        entry["MemoryLimit"] = props.MemoryLimit;
        entry["HitsCount"] = props.HitsCount;
        entry["MemoryFlushLimit"] = props.MemoryFlushLimit;
        results[tag] = entry;

        var parts = new List<string>();
        if (props.Health.HasValue) parts.Add($"HP={props.Health.Value:F0}");
        if (props.Structure.HasValue) parts.Add($"Struct={props.Structure.Value:F0}");
        if (props.MemoryLimit.HasValue) parts.Add($"MemLim={props.MemoryLimit.Value:F1}");
        if (props.HitsCount.HasValue) parts.Add($"Hits={props.HitsCount.Value}");
        if (props.MemoryFlushLimit.HasValue) parts.Add($"Flush={props.MemoryFlushLimit.Value:F1}");
        Console.WriteLine(string.Join("  ", parts));
        scanned++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: {ex.Message}");
        errors++;
    }
}

Console.WriteLine();
Console.WriteLine($"Scanned: {scanned}, Errors: {errors}");

var outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vanilla_unit_properties.json");

var sb = new StringBuilder();
sb.AppendLine("{");
bool firstEntry = true;
foreach (var kv in results)
{
    if (!firstEntry) sb.AppendLine(",");
    firstEntry = false;
    sb.AppendLine($"  \"{kv.Key}\": {{");
    bool firstProp = true;
    foreach (var pkv in kv.Value)
    {
        if (!firstProp) sb.AppendLine(",");
        firstProp = false;
        string val = pkv.Value switch
        {
            float f => f.ToString("F2"),
            int i => i.ToString(),
            null => "null",
            _ => pkv.Value.ToString() ?? "null"
        };
        sb.Append($"    \"{pkv.Key}\": {val}");
    }
    sb.AppendLine();
    sb.Append("  }");
}
sb.AppendLine();
sb.AppendLine("}");

File.WriteAllText(outputPath, sb.ToString());
Console.WriteLine($"Written to: {outputPath}");
