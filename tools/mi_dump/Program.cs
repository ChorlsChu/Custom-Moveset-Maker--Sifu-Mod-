using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

internal static class Program
{
    private static int Main(string[] args)
    {
        var contentDir = args.Length > 0
            ? args[0]
            : @"C:\Users\Charles\Downloads\Sifu Modding\Movesets Prototypes\Moveset Maker\SifuMovesetEditor\bin\Debug\net10.0-windows\GameContent\Content";
        var start = args.Length > 1 ? args[1] : "Characters/Boss/Yang/Materials/MI_Yang_HD05_Head";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? rel = start;
        for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(rel); depth++)
        {
            if (!seen.Add(rel!)) { Console.WriteLine($"(cycle at {rel})"); break; }
            var file = Path.Combine(contentDir, rel!.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!File.Exists(file)) { Console.WriteLine($"MISSING: {file}"); break; }

            Console.WriteLine($"==== [{depth}] {rel} ({new FileInfo(file).Length} B)");
            UAsset asset;
            try { asset = new UAsset(file, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None); }
            catch (Exception ex) { Console.WriteLine("  load error: " + ex.Message); break; }

            string? parentRel = null;
            foreach (var exp in asset.Exports)
            {
                if (exp is not NormalExport ne || ne.Data == null) continue;
                Console.WriteLine($"  export {exp.ObjectName} ({exp.GetType().Name}):");
                Walk(ne.Data, asset, "    ", 0, ref parentRel);
            }

            rel = parentRel;
            if (rel != null) Console.WriteLine($"\n-- following parent -> {rel}\n");
        }
        return 0;
    }

    private static void Walk(List<PropertyData> props, UAsset asset, string ind, int depth, ref string? parentRel)
    {
        if (depth > 6) return;
        foreach (var p in props)
        {
            if (p == null) continue;
            var name = p.Name?.Value?.ToString() ?? "?";
            switch (p)
            {
                case ObjectPropertyData op:
                    var target = ResolveImport(asset, op.Value);
                    Console.WriteLine($"{ind}{name}: -> {target}");
                    if ((name == "Parent" || name == "Material") && op.Value != null && op.Value.Index < 0)
                    {
                        int mi = -(op.Value.Index + 1);
                        if (mi >= 0 && mi < asset.Imports.Count)
                        {
                            var imp = asset.Imports[mi];
                            var o = imp.OuterIndex.Index;
                            if (o < 0 && -(o + 1) < asset.Imports.Count)
                            {
                                var pkg = asset.Imports[-(o + 1)].ObjectName.Value?.ToString() ?? "";
                                if (pkg.StartsWith("/Game/"))
                                    parentRel ??= pkg.Substring("/Game/".Length);
                            }
                        }
                    }
                    break;
                case ArrayPropertyData ap:
                    Console.WriteLine($"{ind}{name}: [{ap.Value?.Length ?? 0}]");
                    if (ap.Value != null)
                        foreach (var e in ap.Value)
                        {
                            if (e is StructPropertyData esp)
                            {
                                Console.WriteLine($"{ind}  {esp.Name}:");
                                Walk(esp.Value ?? new List<PropertyData>(), asset, ind + "    ", depth + 1, ref parentRel);
                            }
                            else if (e is ObjectPropertyData eop)
                                Console.WriteLine($"{ind}  -> {ResolveImport(asset, eop.Value)}");
                            else
                                Console.WriteLine($"{ind}  {e?.Name}: {Fmt(e)}");
                        }
                    break;
                case StructPropertyData sp:
                    Console.WriteLine($"{ind}{name}: {{");
                    Walk(sp.Value ?? new List<PropertyData>(), asset, ind + "  ", depth + 1, ref parentRel);
                    Console.WriteLine($"{ind}}}");
                    break;
                default:
                    Console.WriteLine($"{ind}{name}: {Fmt(p)}");
                    break;
            }
        }
    }

    private static string Fmt(PropertyData? p)
    {
        if (p == null) return "<null>";
        return p switch
        {
            StrPropertyData s => $"\"{s.Value}\"",
            NamePropertyData n => $"\"{n.Value}\"",
            TextPropertyData t => $"\"{t.Value}\"",
            FloatPropertyData f => f.Value.ToString("F4"),
            IntPropertyData i => i.Value.ToString(),
            BoolPropertyData b => b.Value.ToString(),
            BytePropertyData by => by.Value.ToString(),
            _ => $"{p.GetType().Name}"
        };
    }

    private static string ResolveImport(UAsset asset, FPackageIndex? idx)
    {
        if (idx == null || idx.Index >= 0) return idx == null ? "<null>" : $"<export {idx.Index}>";
        int i = -(idx.Index + 1);
        if (i < 0 || i >= asset.Imports.Count) return $"<out-of-range {idx.Index}>";
        var imp = asset.Imports[i];
        var o = imp.OuterIndex.Index;
        var outer = o < 0 && -(o + 1) < asset.Imports.Count
            ? asset.Imports[-(o + 1)].ObjectName.Value?.ToString()
            : (o == 0 ? "" : $"<{o}>");
        return $"{imp.ClassName.Value} {imp.ObjectName.Value} (outer={outer})";
    }
}
