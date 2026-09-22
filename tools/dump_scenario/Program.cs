using System.Reflection;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

string[] files = {
    @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Scenario\FireDisciple_Scenario_Base.uasset",
    @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Scenario\FireDisciple_Scenario_Base_H2.uasset"
};

var eng = EngineVersion.VER_UE4_26;

foreach (var path in files)
{
    Console.WriteLine("================================================================================");
    Console.WriteLine($"FILE: {Path.GetFileName(path)}");
    Console.WriteLine($"PATH: {path}");
    Console.WriteLine("================================================================================");

    UAsset asset = new UAsset(path, eng, null, CustomSerializationFlags.None);

    Console.WriteLine($"ObjectVersion: {asset.ObjectVersion}");
    Console.WriteLine($"IsUnversioned: {asset.IsUnversioned}");
    Console.WriteLine($"Imports: {asset.Imports.Count}  Exports: {asset.Exports.Count}");
    Console.WriteLine($"NameMap entries: {asset.GetNameMapIndexList().Count}");
    Console.WriteLine();

    // ======================================================================
    // SECTION 1: ALL IMPORTS
    // ======================================================================
    Console.WriteLine("=== ALL IMPORTS ===");
    for (int i = 0; i < asset.Imports.Count; i++)
    {
        var im = asset.Imports[i];
        string className = im.ClassName?.Value?.ToString() ?? "";
        string objectName = im.ObjectName?.Value?.ToString() ?? "";
        int outerVal = (int)im.OuterIndex.Index;
        string packagePath = "";
        if (outerVal < 0)
        {
            int outerAbs = -outerVal - 1;
            if (outerAbs >= 0 && outerAbs < asset.Imports.Count)
                packagePath = asset.Imports[outerAbs].ObjectName?.Value?.ToString() ?? "";
        }
        Console.WriteLine($"  [{i,4}] {className}.{objectName}  Outer={outerVal}  Package={packagePath}");
    }
    Console.WriteLine();

    // ======================================================================
    // SECTION 2: ALL EXPORTS
    // ======================================================================
    Console.WriteLine("=== ALL EXPORTS ===");
    for (int i = 0; i < asset.Exports.Count; i++)
    {
        var ex = asset.Exports[i];
        string exType = ex.GetType().Name;
        string objName = ex.ObjectName?.Value?.ToString() ?? "";
        string resolved = ResolveIndex(asset, ex.ClassIndex);
        Console.WriteLine($"  [{i,4}] {exType,-18} {objName,-50} Class={resolved}  Size={ex.SerialSize}");
    }
    Console.WriteLine();

    // ======================================================================
    // SECTION 3: DEFAULT EXPORT (Exports[0]) - FULL PROPERTY DUMP
    // ======================================================================
    Console.WriteLine("================================================================================");
    Console.WriteLine("=== DEFAULT EXPORT (Exports[0]) - FULL PROPERTY DUMP ===");
    Console.WriteLine("================================================================================");
    if (asset.Exports.Count > 0)
    {
        var def = asset.Exports[0];
        Console.WriteLine($"Export Type: {def.GetType().Name}");
        Console.WriteLine($"ObjectName:  {def.ObjectName?.Value}");
        Console.WriteLine($"ClassIndex:  {def.ClassIndex.Index} -> {ResolveIndex(asset, def.ClassIndex)}");
        Console.WriteLine($"OuterIndex:  {def.OuterIndex.Index} -> {ResolveIndex(asset, def.OuterIndex)}");
        Console.WriteLine($"SuperIndex:  {def.SuperIndex.Index} -> {ResolveIndex(asset, def.SuperIndex)}");
        Console.WriteLine($"TemplateIndex: {def.TemplateIndex.Index} -> {ResolveIndex(asset, def.TemplateIndex)}");
        Console.WriteLine($"SerialSize:  {def.SerialSize} bytes");
        Console.WriteLine();

        if (def is NormalExport ne)
        {
            Console.WriteLine($"--- NormalExport.Data count: {ne.Data.Count} ---");
            for (int i = 0; i < ne.Data.Count; i++)
            {
                var p = ne.Data[i];
                Console.WriteLine();
                Console.WriteLine($"  [{i}] PropertyName: \"{p.Name?.Value}\"  CSharpType: {p.GetType().Name}");
                DumpPropertyFull(asset, p, "    ", maxDepth: 4);
            }
        }
        else
        {
            Console.WriteLine($"  (Not a NormalExport - type is {def.GetType().Name})");
        }
    }
    Console.WriteLine();

    // ======================================================================
    // SECTION 4: ALL EXPORTS - PROPERTY DETAILS
    // ======================================================================
    Console.WriteLine("================================================================================");
    Console.WriteLine("=== ALL EXPORTS - PROPERTY DETAILS ===");
    Console.WriteLine("================================================================================");
    for (int i = 0; i < asset.Exports.Count; i++)
    {
        var ex = asset.Exports[i];
        Console.WriteLine();
        Console.WriteLine($"--- Export[{i}] ---");
        Console.WriteLine($"  Type: {ex.GetType().Name}");
        Console.WriteLine($"  ObjectName: {ex.ObjectName?.Value}");
        Console.WriteLine($"  ClassIndex: {ex.ClassIndex.Index} -> {ResolveIndex(asset, ex.ClassIndex)}");
        Console.WriteLine($"  OuterIndex: {ex.OuterIndex.Index} -> {ResolveIndex(asset, ex.OuterIndex)}");
        Console.WriteLine($"  SerialSize: {ex.SerialSize} bytes");

        if (ex is NormalExport ne2)
        {
            Console.WriteLine($"  Data count: {ne2.Data.Count}");
            for (int j = 0; j < ne2.Data.Count; j++)
            {
                var p = ne2.Data[j];
                Console.WriteLine();
                Console.WriteLine($"    [{j}] \"{p.Name?.Value}\" [{p.GetType().Name}]");
                DumpPropertyFull(asset, p, "      ", maxDepth: 3);
            }
        }
        else if (ex is FunctionExport fe)
        {
            Console.WriteLine($"  FunctionFlags: 0x{(uint)fe.FunctionFlags:X8}");
            Console.WriteLine($"  LoadedProperties: {(fe.LoadedProperties == null ? "null" : $"[{fe.LoadedProperties.Length}]")}");
            if (fe.LoadedProperties != null)
            {
                for (int j = 0; j < fe.LoadedProperties.Length; j++)
                {
                    var lp = fe.LoadedProperties[j];
                    Console.WriteLine($"    Local[{j}]: \"{lp.Name?.Value}\" [{lp.GetType().Name}]");
                }
            }
            Console.WriteLine($"  ScriptBytecode: {(fe.ScriptBytecode == null ? "null" : $"[{fe.ScriptBytecode.Length} instructions]")}");
        }
        else if (ex is ClassExport ce)
        {
            Console.WriteLine($"  ClassFlags: 0x{Convert.ToUInt64(ce.ClassFlags):X8}");
            Console.WriteLine($"  ClassDefaultObject: {ce.ClassDefaultObject.Index} -> {ResolveIndex(asset, ce.ClassDefaultObject)}");
            if (ce.Children != null)
                Console.WriteLine($"  Children: [{ce.Children.Length}]");
            if (ce.FuncMap != null && ce.FuncMap.Keys != null)
                Console.WriteLine($"  FuncMap: [{ce.FuncMap.Keys.Count} entries]");
            if (ce.LoadedProperties != null)
            {
                Console.WriteLine($"  LoadedProperties: [{ce.LoadedProperties.Length}]");
                for (int j = 0; j < ce.LoadedProperties.Length; j++)
                    Console.WriteLine($"    [{j}] \"{ce.LoadedProperties[j].Name?.Value}\" [{ce.LoadedProperties[j].GetType().Name}]");
            }
        }
    }
    Console.WriteLine();

    Console.WriteLine("================================================================================");
    Console.WriteLine($"END OF FILE: {Path.GetFileName(path)}");
    Console.WriteLine("================================================================================");
    Console.WriteLine();
    Console.WriteLine();
}

Console.WriteLine("=== DUMP COMPLETE ===");
return 0;

// ======================================================================
// HELPER: Resolve an FPackageIndex to a human-readable name
// ======================================================================
static string ResolveIndex(UAsset asset, FPackageIndex idx)
{
    try
    {
        int i = (int)idx.Index;
        if (i == 0) return "(null)";
        if (i > 0 && i <= asset.Exports.Count)
            return $"[exp {i - 1}] {asset.Exports[i - 1].ObjectName?.Value}";
        if (i < 0)
        {
            int ri = -i - 1;
            if (ri >= 0 && ri < asset.Imports.Count)
            {
                var im = asset.Imports[ri];
                return $"[imp {ri}] {im.ClassName?.Value}.{im.ObjectName?.Value}";
            }
        }
    }
    catch { }
    return idx.Index.ToString();
}

// ======================================================================
// HELPER: Deep-dump a property including MapProperty and ArrayProperty
// ======================================================================
static void DumpPropertyFull(UAsset asset, PropertyData prop, string indent, int maxDepth)
{
    if (prop == null || maxDepth <= 0) return;

    // Print SerializedType via reflection (it's on FProperty base)
    string serializedType = "";
    try
    {
        var stProp = prop.GetType().GetProperty("SerializedType");
        if (stProp != null)
        {
            var stVal = stProp.GetValue(prop);
            if (stVal is FName stFn) serializedType = stFn.Value?.ToString() ?? "";
            else serializedType = stVal?.ToString() ?? "";
        }
    }
    catch { }
    if (!string.IsNullOrEmpty(serializedType))
        Console.WriteLine($"{indent}SerializedType: {serializedType}");

    // MapPropertyData - dump all entries
    if (prop is MapPropertyData mapProp)
    {
        Console.WriteLine($"{indent}MapProperty entries: {mapProp.Value?.Count ?? 0}");
        if (mapProp.Value != null)
        {
            int entryIdx = 0;
            foreach (var kvp in mapProp.Value)
            {
                var keyProp = kvp.Key;
                var valEntry = kvp.Value;
                string keyStr = FormatSimpleValue(asset, keyProp);
                Console.WriteLine($"{indent}  [{entryIdx}] Key: {keyStr}");
                if (valEntry != null)
                {
                    Console.WriteLine($"{indent}       ValType: {valEntry.GetType().Name}  ValName: \"{valEntry.Name?.Value}\"");
                    DumpPropertyFull(asset, valEntry, $"{indent}       ", maxDepth - 1);
                }
                entryIdx++;
            }
        }
        return;
    }

    // ArrayPropertyData - dump all entries
    if (prop is ArrayPropertyData arrProp)
    {
        string arrayType = "";
        try
        {
            var atProp = arrProp.GetType().GetProperty("ArrayType");
            if (atProp != null)
            {
                var atVal = atProp.GetValue(arrProp);
                if (atVal is FName atFn) arrayType = atFn.Value?.ToString() ?? "";
            }
        }
        catch { }
        Console.WriteLine($"{indent}ArrayProperty: ArrayType=\"{arrayType}\"  Entries: {arrProp.Value?.Length ?? 0}");
        if (arrProp.Value != null)
        {
            for (int i = 0; i < arrProp.Value.Length; i++)
            {
                var elem = arrProp.Value[i];
                if (elem == null)
                {
                    Console.WriteLine($"{indent}  [{i}] null");
                    continue;
                }
                Console.WriteLine($"{indent}  [{i}] Type: {elem.GetType().Name}  Name: \"{elem.Name?.Value}\"");
                DumpPropertyFull(asset, elem, $"{indent}    ", maxDepth - 1);
            }
        }
        return;
    }

    // StructPropertyData - dump inner properties
    if (prop is StructPropertyData spd)
    {
        Console.WriteLine($"{indent}StructProperty:");
        if (spd.Value != null)
        {
            foreach (var inner in spd.Value)
            {
                Console.WriteLine($"{indent}    \"{inner.Name?.Value}\" [{inner.GetType().Name}]");
                DumpPropertyFull(asset, inner, $"{indent}      ", maxDepth - 1);
            }
        }
        return;
    }

    // Try to get Value property
    var valProp = prop.GetType().GetProperty("Value");
    if (valProp == null)
    {
        Console.WriteLine($"{indent}(No 'Value' property)");
        return;
    }

    object? val = null;
    try { val = valProp.GetValue(prop); } catch { return; }

    if (val == null)
    {
        Console.WriteLine($"{indent}Value: null");
        return;
    }

    // ObjectPropertyData - resolve the reference
    if (val is FPackageIndex fpi)
    {
        Console.WriteLine($"{indent}Value: FPackageIndex({fpi.Index}) -> {ResolveIndex(asset, fpi)}");
        return;
    }

    // FName
    if (val is FName fn)
    {
        Console.WriteLine($"{indent}Value: FName(\"{fn.Value}\")");
        return;
    }

    // Simple types
    if (val is string s) { Console.WriteLine($"{indent}Value: \"{s}\""); return; }
    if (val is bool b) { Console.WriteLine($"{indent}Value: {b}"); return; }
    if (val is int iv) { Console.WriteLine($"{indent}Value: {iv}"); return; }
    if (val is float fv) { Console.WriteLine($"{indent}Value: {fv}"); return; }
    if (val is double dv) { Console.WriteLine($"{indent}Value: {dv}"); return; }
    if (val is uint uv) { Console.WriteLine($"{indent}Value: 0x{uv:X8}"); return; }
    if (val is long lv) { Console.WriteLine($"{indent}Value: {lv}"); return; }
    if (val is ulong ulv) { Console.WriteLine($"{indent}Value: 0x{ulv:X16}"); return; }
    if (val is byte btv) { Console.WriteLine($"{indent}Value: 0x{btv:X2}"); return; }
    if (val is Enum) { Console.WriteLine($"{indent}Value: ({val.GetType().Name}){val}"); return; }

    // Fallback
    string vs = val.ToString() ?? "";
    if (vs.Length > 300) vs = vs[..300] + "...";
    Console.WriteLine($"{indent}Value: ({val.GetType().Name}){vs}");
}

// ======================================================================
// HELPER: Format a simple property value to a string
// ======================================================================
static string FormatSimpleValue(UAsset asset, PropertyData? prop)
{
    if (prop == null) return "(null)";
    var t = prop.GetType();
    var valProp = t.GetProperty("Value");
    if (valProp == null) return $"({t.Name})";

    try
    {
        object? val = valProp.GetValue(prop);
        if (val == null) return "null";
        if (val is FName fn) return $"FName(\"{fn.Value}\")";
        if (val is FPackageIndex fpi) return $"FPackageIndex({fpi.Index}) -> {ResolveIndex(asset, fpi)}";
        if (val is string s) return $"\"{s}\"";
        if (val is int iv) return iv.ToString();
        if (val is float fv) return fv.ToString();
        if (val is bool bv) return bv.ToString();
        if (val is Enum) return $"({val.GetType().Name}){val}";
        string vs = val.ToString() ?? "";
        return vs.Length > 200 ? vs[..200] + "..." : vs;
    }
    catch { return "(error)"; }
}
