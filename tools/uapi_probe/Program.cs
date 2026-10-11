using System;
using System.IO;
using System.Linq;
using System.Reflection;

// One-off reflection probe over libs\UAssetAPI.dll: lists every PropertyData
// subclass with its Value property type (write-side ground truth for editing
// condition params). Output: %TEMP%\opencode\uapi_probe_out.txt
internal static class Program
{
    private static int Main()
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "libs", "UAssetAPI.dll");
        dll = Path.GetFullPath(dll);
        var asm = Assembly.LoadFrom(dll);
        Console.WriteLine("loaded: " + dll);

        Type[] allTypes;
        try { allTypes = asm.GetTypes(); }
        catch (ReflectionTypeLoadException rtle) { allTypes = rtle.Types.Where(t => t != null).Cast<Type>().ToArray(); }

        var baseType = allTypes.FirstOrDefault(t => t.Name == "PropertyData" && t.Namespace != null && t.Namespace.StartsWith("UAssetAPI"));
        Console.WriteLine("PropertyData: " + (baseType?.FullName ?? "NULL"));
        foreach (var hit in allTypes.Where(t => t.Name is "Asset" or "NormalExport" or "FPackageIndex" or "TMap`2" or "FString" or "FName").Take(20))
            Console.WriteLine("type: " + hit.FullName);

        var types = allTypes
            .Where(t => t.IsClass && !t.IsAbstract && baseType != null && baseType.IsAssignableFrom(t))
            .OrderBy(t => t.Name);

        var outLines = new System.Text.StringBuilder();
        foreach (var t in types)
        {
            var valueProps = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.Name is "Value" or "_value")
                .ToList();
            var valueFields = t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(f => f.Name.Contains("Value"))
                .ToList();
            string vp = string.Join("; ", valueProps.Select(p => $"{p.Name}: {p.PropertyType.FullName}"));
            string vf = string.Join("; ", valueFields.Select(f => $"{f.Name}: {f.FieldType.FullName}"));
            outLines.AppendLine($"{t.Name} | ValueProp[{vp}] | ValueField[{vf}]");
        }

        // Full member chain (incl. base types) for the types we will mutate when editing conditions.
        outLines.AppendLine("--- value members (full chain) ---");
        foreach (var tname in new[] { "IntPropertyData", "Int8PropertyData", "Int16PropertyData", "Int64PropertyData",
            "UInt16PropertyData", "UInt32PropertyData", "UInt64PropertyData", "FloatPropertyData", "DoublePropertyData",
            "BoolPropertyData", "BytePropertyData", "EnumPropertyData", "NamePropertyData", "StrPropertyData",
            "TextPropertyData", "ObjectPropertyData", "StructPropertyData", "ArrayPropertyData", "MapPropertyData" })
        {
            var t = types.FirstOrDefault(x => x.Name == tname);
            if (t == null) { outLines.AppendLine($"{tname}: NOT FOUND"); continue; }
            var members = new System.Text.StringBuilder();
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                foreach (var p in cur.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (p.Name.Contains("Value") || p.Name == "PropertyType")
                        members.Append($"prop {p.Name}: {p.PropertyType.FullName} (decl {cur.Name}); ");
                foreach (var f in cur.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (f.Name.Contains("Value") || f.Name == "PropertyType")
                        members.Append($"field {f.Name}: {f.FieldType.FullName} (decl {cur.Name}); ");
            }
            outLines.AppendLine($"{tname}: {members}");
        }

        // Also dump interesting non-PropertyData types we mutate.
        foreach (var name in new[] { "UAssetAPI.UAsset", "UAssetAPI.UnrealTypes.FPackageIndex",
            "UAssetAPI.UnrealTypes.FName", "UAssetAPI.UnrealTypes.FString",
            "UAssetAPI.UnrealTypes.TMap`2", "UAssetAPI.ExportTypes.NormalExport" })
        {
            var t = allTypes.FirstOrDefault(x => x.FullName == name) ?? asm.GetType(name);
            if (t == null) { outLines.AppendLine($"{name}: NOT FOUND"); continue; }
            var members = new System.Text.StringBuilder();
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                foreach (var p in cur.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (p.Name is "Index" or "Value" or "Data" or "Exports" or "Imports" or "ObjectName" or "ClassIndex" or "Name")
                        members.Append($"prop {p.Name}: {p.PropertyType.FullName} (decl {cur.Name}); ");
                foreach (var f in cur.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (f.Name is "Index" or "Value" or "Data" or "Exports" or "Imports" or "ObjectName" or "ClassIndex" or "Name")
                        members.Append($"field {f.Name}: {f.FieldType.FullName} (decl {cur.Name}); ");
            }
            outLines.AppendLine($"{name}: {members}");
        }

        string path = Path.Combine(Path.GetTempPath(), "opencode", "uapi_probe_out.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, outLines.ToString());
        Console.WriteLine($"wrote {path} ({types.Count()} PropertyData types)");
        return 0;
    }
}
