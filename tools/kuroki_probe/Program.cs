#nullable enable
using System;
using System.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.UnrealTypes;

// Arg-driven single parse so a stack overflow in one combination can't kill the matrix.
// usage: kuroki_probe <file> <EngineVersion> [flags] [--stack]
//        kuroki_probe --list
if (args.Length == 0 || args[0] == "--list")
{
    Console.WriteLine("versions: " + string.Join(",", Enum.GetNames(typeof(EngineVersion))));
    Console.WriteLine("flags:    " + string.Join(",", Enum.GetNames(typeof(CustomSerializationFlags))));
    return 0;
}

var file = args[0];
if (!Enum.TryParse<EngineVersion>(args[1], out var ver))
{
    Console.WriteLine($"bad version '{args[1]}'");
    return 2;
}
var flags = CustomSerializationFlags.None;
if (args.Length > 2 && args[2] != "--stack")
{
    if (!Enum.TryParse(args[2], true, out flags)) { Console.WriteLine($"bad flags '{args[2]}'"); return 2; }
}

try
{
    var asset = new UAsset(file, ver, null, flags);
    Console.WriteLine($"PASS exports={asset.Exports.Count}");
    if (args.Contains("--write"))
    {
        // Round-trip: same Write() PatchUnitProperties does, into a temp dir.
        var outDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kuroki_probe_out",
            System.IO.Path.GetFileNameWithoutExtension(file));
        System.IO.Directory.CreateDirectory(outDir);
        var outPath = System.IO.Path.Combine(outDir, System.IO.Path.GetFileName(file));
        asset.Write(outPath);
        var uexp = System.IO.Path.ChangeExtension(outPath, ".uexp");
        Console.WriteLine($"WRITE OK uasset={new System.IO.FileInfo(outPath).Length}"
            + (System.IO.File.Exists(uexp) ? $" uexp={new System.IO.FileInfo(uexp).Length}" : " uexp=none"));
        try { System.IO.Directory.Delete(outDir, true); } catch { }
    }
    if (args.Contains("--patch"))
    {
        // Exact PatchUnitProperties body: set the props it sets, then Write.
        var outDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kuroki_probe_out",
            System.IO.Path.GetFileNameWithoutExtension(file));
        System.IO.Directory.CreateDirectory(outDir);
        var outPath = System.IO.Path.Combine(outDir, System.IO.Path.GetFileName(file));
        System.IO.File.Copy(file, outPath, true);

        if (asset.Exports.Count > 1 && asset.Exports[1] is UAssetAPI.ExportTypes.NormalExport ne)
        {
            var hp = ne.Data.OfType<UAssetAPI.PropertyTypes.Objects.FloatPropertyData>()
                .FirstOrDefault(p => p.Name.Value.ToString() == "m_fHealth");
            if (hp != null) hp.Value = 9999f;
            var sp = ne.Data.OfType<UAssetAPI.PropertyTypes.Objects.FloatPropertyData>()
                .FirstOrDefault(p => p.Name.Value.ToString() == "m_fStructure");
            if (sp != null) sp.Value = 9999f;
            asset.Write(outPath);
        }
        else Console.WriteLine("(archetype path not applicable: not 2-export NormalExport)");

        foreach (var exp in asset.Exports)
        {
            if (exp is not UAssetAPI.ExportTypes.NormalExport ne2) continue;
            bool modified = false;
            foreach (var d in ne2.Data)
            {
                if (d is UAssetAPI.PropertyTypes.Objects.FloatPropertyData fp)
                {
                    var n = fp.Name.Value.ToString();
                    if (n == "m_fMemoryLimit") { fp.Value = 123f; modified = true; }
                    else if (n == "m_fMemoryFlushLimit") { fp.Value = 456f; modified = true; }
                }
                else if (d is UAssetAPI.PropertyTypes.Objects.BytePropertyData bp
                         && bp.Name.Value.ToString() == "m_uiHitsCount")
                {
                    bp.Value = (byte)7; modified = true;
                }
            }
            if (modified) asset.Write(outPath);
        }
        Console.WriteLine("PATCH OK");
        try { System.IO.Directory.Delete(outDir, true); } catch { }
    }
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL {ex.GetType().Name}: {ex.Message}");
    if (args.Contains("--stack")) Console.WriteLine(ex.ToString());
    return 1;
}
