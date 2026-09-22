using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

string assetPath = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Archetype\BP_FireDisciple_ArchetypeDB_Base_Master.uasset";

Console.WriteLine("==================================================================");
Console.WriteLine("  ArchetypeDB Dumper  -  UAssetAPI read-only inspection tool");
Console.WriteLine("==================================================================");
Console.WriteLine();
Console.WriteLine("File : " + assetPath);
Console.WriteLine("Size : " + new FileInfo(assetPath).Length.ToString("N0") + " bytes");
Console.WriteLine();

var eng = EngineVersion.VER_UE4_26;
var asset = new UAsset(assetPath, eng, null, CustomSerializationFlags.None);

Console.WriteLine("Engine version : " + eng);
Console.WriteLine("Exports count  : " + asset.Exports.Count);
Console.WriteLine("Imports count  : " + asset.Imports.Count);
Console.WriteLine();

string ResolveIndex(FPackageIndex idx, UAsset a)
{
    if (idx == null || idx.IsNull()) return "<null>";
    if (idx.IsImport())
    {
        int importIdx = -(idx.Index + 1);
        if (importIdx >= 0 && importIdx < a.Imports.Count)
        {
            var imp = a.Imports[importIdx];
            return "import[" + importIdx + "]=" + (imp.ObjectName.Value?.ToString() ?? "?");
        }
        return "import[" + importIdx + "]=<out-of-range>";
    }
    if (idx.IsExport())
    {
        int exportIdx = idx.Index - 1;
        if (exportIdx >= 0 && exportIdx < a.Exports.Count)
        {
            var exp = a.Exports[exportIdx];
            return "export[" + exportIdx + "]=" + (exp.ObjectName.Value?.ToString() ?? "?");
        }
        return "export[" + exportIdx + "]=<out-of-range>";
    }
    return "<self index=" + idx.Index + ">";
}

string ResolveSoftObjectPath(FSoftObjectPath fsp, UAsset a)
{
    try
    {
        string pkg = fsp.AssetPath.PackageName.Value?.ToString() ?? "";
        string asset_name = fsp.AssetPath.AssetName.Value?.ToString() ?? "";
        string sub = fsp.SubPathString?.Value?.ToString() ?? "";
        if (!string.IsNullOrEmpty(sub))
            return pkg + "/" + asset_name + ":" + sub;
        if (!string.IsNullOrEmpty(pkg) || !string.IsNullOrEmpty(asset_name))
            return pkg + "/" + asset_name;
    }
    catch { }
    return "<unresolved-soft-path>";
}

string FormatPropVal(PropertyData val, UAsset a)
{
    if (val == null) return "<null>";
    if (val is IntPropertyData ip) return ip.Value.ToString();
    if (val is UInt32PropertyData u32) return u32.Value.ToString();
    if (val is Int64PropertyData i64) return i64.Value.ToString();
    if (val is UInt64PropertyData u64) return u64.Value.ToString();
    if (val is FloatPropertyData fp) return fp.Value.ToString("F4");
    if (val is DoublePropertyData dp) return dp.Value.ToString("F4");
    if (val is BoolPropertyData bp) return bp.Value.ToString();
    if (val is StrPropertyData sp) return "\"" + sp.Value + "\"";
    if (val is NamePropertyData np) return "\"" + (np.Value?.Value?.ToString() ?? "") + "\"";
    if (val is TextPropertyData tp) return "\"" + (tp.Value?.ToString() ?? "") + "\"";
    if (val is EnumPropertyData ep) return ep.Value?.Value?.ToString() ?? "?";
    if (val is ObjectPropertyData op) return ResolveIndex(op.Value, a);
    if (val is SoftObjectPropertyData sop) return ResolveSoftObjectPath(sop.Value, a);
    if (val is BytePropertyData bp2) return bp2.Value.ToString();
    if (val is InterfacePropertyData ifp) return ResolveIndex(ifp.Value, a);
    if (val is WeakObjectPropertyData wop) return ResolveIndex(wop.Value, a);
    return "<" + val.GetType().Name + ">";
}

var interestingProps = new List<(string exportName, string path, string propName, string value)>();

void DumpProperty(PropertyData prop, string indent, int depth, string exportName,
    List<(string, string, string, string)> hits, UAsset a)
{
    if (prop == null) return;
    string name = prop.Name.Value?.ToString() ?? "?";
    string typeName = prop.PropertyType?.Value?.ToString() ?? prop.GetType().Name;
    string prefix = indent + "[" + typeName + "] " + name;

    interestingProps.Add((exportName, depth > 0 ? name + "." : "", name, ""));

    if (prop is IntPropertyData intProp)
        Console.WriteLine(prefix + " = " + intProp.Value);
    else if (prop is UInt32PropertyData uint32Prop)
        Console.WriteLine(prefix + " = " + uint32Prop.Value);
    else if (prop is Int64PropertyData int64Prop)
        Console.WriteLine(prefix + " = " + int64Prop.Value);
    else if (prop is UInt64PropertyData uint64Prop)
        Console.WriteLine(prefix + " = " + uint64Prop.Value);
    else if (prop is FloatPropertyData floatProp)
        Console.WriteLine(prefix + " = " + floatProp.Value.ToString("F4"));
    else if (prop is DoublePropertyData doubleProp)
        Console.WriteLine(prefix + " = " + doubleProp.Value.ToString("F4"));
    else if (prop is BoolPropertyData boolProp)
        Console.WriteLine(prefix + " = " + boolProp.Value);
    else if (prop is StrPropertyData strProp)
        Console.WriteLine(prefix + " = \"" + strProp.Value + "\"");
    else if (prop is NamePropertyData nameProp)
        Console.WriteLine(prefix + " = \"" + (nameProp.Value?.Value?.ToString() ?? "") + "\"");
    else if (prop is TextPropertyData textProp)
        Console.WriteLine(prefix + " = \"" + (textProp.Value?.ToString() ?? "") + "\"");
    else if (prop is BytePropertyData byteProp)
        Console.WriteLine(prefix + " = " + byteProp.Value);
    else if (prop is EnumPropertyData enumProp)
        Console.WriteLine(prefix + " = " + (enumProp.Value?.Value?.ToString() ?? "?") + " (type=" + (enumProp.EnumType?.Value?.ToString() ?? "?") + ")");
    else if (prop is ObjectPropertyData objProp)
    {
        string resolved = ResolveIndex(objProp.Value, a);
        Console.WriteLine(prefix + " -> " + resolved);
        hits.Add((exportName, "", name, resolved));
    }
    else if (prop is SoftObjectPropertyData softProp)
    {
        string resolved = ResolveSoftObjectPath(softProp.Value, a);
        Console.WriteLine(prefix + " -> " + resolved);
        hits.Add((exportName, "", name, resolved));
    }
    else if (prop is InterfacePropertyData ifaceProp)
    {
        string resolved = ResolveIndex(ifaceProp.Value, a);
        Console.WriteLine(prefix + " -> " + resolved);
        hits.Add((exportName, "", name, resolved));
    }
    else if (prop is WeakObjectPropertyData weakProp)
    {
        string resolved = ResolveIndex(weakProp.Value, a);
        Console.WriteLine(prefix + " -> " + resolved);
        hits.Add((exportName, "", name, resolved));
    }
    else if (prop is ArrayPropertyData arrProp)
    {
        // Value is PropertyData[] -- use .Length
        var arr = arrProp.Value;
        int count = arr == null ? 0 : arr.Length;
        string arrType = arrProp.ArrayType?.Value?.ToString() ?? "?";
        Console.WriteLine(prefix + " [" + count + " elements] (inner: " + arrType + ")");
        if (arr != null && depth < 6)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                Console.WriteLine(indent + "  [" + i + "]");
                DumpProperty(arr[i], indent + "    ", depth + 1, exportName, hits, a);
            }
        }
    }
    else if (prop is MapPropertyData mapProp)
    {
        // Value is a Field of type TMap
        var mapVal = mapProp.Value;
        int count = mapVal == null ? 0 : mapVal.Count;
        string keyType = mapProp.KeyType?.Value?.ToString() ?? "?";
        string valType = mapProp.ValueType?.Value?.ToString() ?? "?";
        Console.WriteLine(prefix + " [" + count + " key-value pairs] (key: " + keyType + ", value: " + valType + ")");
        if (mapVal != null && depth < 6)
        {
            try
            {
                // TMap implements IOrderedDictionary
                var dict = mapVal as IDictionary;
                if (dict != null)
                {
                    int iter = 0;
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (iter >= 200) { Console.WriteLine(indent + "    ... (truncated at 200)"); break; }
                        string keyStr = FormatPropVal(entry.Key as PropertyData, a);
                        string valStr = FormatPropVal(entry.Value as PropertyData, a);
                        Console.WriteLine(indent + "    '" + keyStr + "' -> " + valStr);
                        if (entry.Value is ObjectPropertyData mop)
                        {
                            string resolved = ResolveIndex(mop.Value, a);
                            hits.Add((exportName, name + "[" + keyStr + "].", "", resolved));
                        }
                        iter++;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(indent + "    (map iteration error: " + ex.Message + ")");
            }
        }
    }
    else if (prop is SetPropertyData setProp)
    {
        var setArr = setProp.Value;
        int count = setArr == null ? 0 : setArr.Length;
        Console.WriteLine(prefix + " [" + count + " elements]");
    }
    else if (prop is StructPropertyData structProp)
    {
        string structType = structProp.StructType?.Value?.ToString() ?? "?";
        var structVal = structProp.Value;
        int innerCount = structVal == null ? 0 : structVal.Count;
        Console.WriteLine(prefix + " (struct: " + structType + ", " + innerCount + " fields)");
        if (structVal != null && depth < 8)
        {
            foreach (var inner in structVal)
            {
                DumpProperty(inner, indent + "  ", depth + 1, exportName, hits, a);
            }
        }
    }
    else if (prop is DelegatePropertyData)
    {
        Console.WriteLine(prefix + " = delegate");
    }
    else if (prop is MulticastDelegatePropertyData)
    {
        Console.WriteLine(prefix + " = multicast delegate");
    }
    else
    {
        Console.WriteLine(prefix + " = <" + prop.GetType().Name + ">");
    }
}

// ================================================================
// SECTION 1: IMPORTS
// ================================================================
Console.WriteLine("==================================================================");
Console.WriteLine("  SECTION 1: IMPORTS (referenced assets / classes)");
Console.WriteLine("==================================================================");
for (int i = 0; i < asset.Imports.Count; i++)
{
    var imp = asset.Imports[i];
    string objName = imp.ObjectName.Value?.ToString() ?? "";
    int outerRaw = imp.OuterIndex.Index;
    int outerIdx = -(outerRaw + 1);
    string outerName = "";
    if (outerIdx >= 0 && outerIdx < asset.Imports.Count)
        outerName = asset.Imports[outerIdx].ObjectName.Value?.ToString() ?? "";
    string classPrefix = imp.ClassName.Value?.ToString() ?? "?";
    Console.WriteLine("  [" + i.ToString("D4") + "] Class=" + classPrefix.PadRight(30) + " Object=" + objName);
    Console.WriteLine("              Outer='" + outerName + "'");
}
Console.WriteLine();

// ================================================================
// SECTION 2: EXPORTS (full property dump)
// ================================================================
Console.WriteLine("==================================================================");
Console.WriteLine("  SECTION 2: EXPORTS (full property dump)");
Console.WriteLine("==================================================================");

for (int expIdx = 0; expIdx < asset.Exports.Count; expIdx++)
{
    var exp = asset.Exports[expIdx];
    Console.WriteLine();
    Console.WriteLine("  +--- Export[" + expIdx + "] -----------------------------------------------------------");
    Console.WriteLine("  | Class     : " + (exp.GetExportClassType().Value?.ToString() ?? "?"));
    Console.WriteLine("  | Name      : " + (exp.ObjectName.Value?.ToString() ?? "?"));
    Console.WriteLine("  | OuterIndex: " + exp.OuterIndex.Index);
    Console.WriteLine("  | SerialSize: " + exp.SerialSize);

    if (exp is NormalExport ne)
    {
        Console.WriteLine("  | Data props: " + (ne.Data?.Count ?? 0));
        Console.WriteLine("  |");
        Console.WriteLine("  | -- PROPERTIES --");
        if (ne.Data != null)
        {
            foreach (var prop in ne.Data)
            {
                DumpProperty(prop, "  | ", 0, exp.ObjectName.Value?.ToString() ?? "Export[" + expIdx + "]", interestingProps, asset);
            }
        }
    }
    else if (exp is ClassExport clsExp)
    {
        Console.WriteLine("  | (ClassExport)");
    }
    else if (exp is FunctionExport funcExp)
    {
        Console.WriteLine("  | (FunctionExport)");
    }
    else
    {
        Console.WriteLine("  | (type: " + exp.GetType().Name + ")");
    }
    Console.WriteLine("  +----------------------------------------------------------------------");
}
Console.WriteLine();

// ================================================================
// SECTION 3: ASSET REFERENCE SUMMARY
// ================================================================
Console.WriteLine("==================================================================");
Console.WriteLine("  SECTION 3: ASSET REFERENCE SUMMARY (Object/SoftObject/Interface props)");
Console.WriteLine("==================================================================");

var refProps = new List<(string exportName, string path, string propName, string value)>();

void CollectRefs(List<PropertyData> props, string basePath, string exportName,
    List<(string, string, string, string)> hits, UAsset a, int d)
{
    if (props == null || d > 8) return;
    foreach (var prop in props)
    {
        if (prop == null) continue;
        string nm = prop.Name.Value?.ToString() ?? "?";
        string cp = basePath + nm + ".";
        if (prop is ObjectPropertyData op2 && !op2.Value.IsNull())
            hits.Add((exportName, basePath, nm, ResolveIndex(op2.Value, a)));
        else if (prop is SoftObjectPropertyData sop2)
            hits.Add((exportName, basePath, nm, ResolveSoftObjectPath(sop2.Value, a)));
        else if (prop is InterfacePropertyData i2 && !i2.Value.IsNull())
            hits.Add((exportName, basePath, nm, ResolveIndex(i2.Value, a)));
        else if (prop is WeakObjectPropertyData w2 && !w2.Value.IsNull())
            hits.Add((exportName, basePath, nm, ResolveIndex(w2.Value, a)));

        if (prop is ArrayPropertyData a2)
        {
            var arr = a2.Value;
            if (arr != null)
                CollectRefs(new List<PropertyData>(arr), cp, exportName, hits, a, d + 1);
        }
        else if (prop is MapPropertyData m2)
        {
            try
            {
                var dict = m2.Value as IDictionary;
                if (dict != null)
                {
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (entry.Value is PropertyData mvp)
                            CollectRefs(new List<PropertyData> { mvp }, cp, exportName, hits, a, d + 1);
                    }
                }
            }
            catch { }
        }
        else if (prop is StructPropertyData s2 && s2.Value != null)
            CollectRefs(s2.Value, cp, exportName, hits, a, d + 1);
    }
}

for (int expIdx = 0; expIdx < asset.Exports.Count; expIdx++)
{
    var exp = asset.Exports[expIdx];
    if (exp is NormalExport ne && ne.Data != null)
        CollectRefs(ne.Data, "", exp.ObjectName.Value?.ToString() ?? "Export[" + expIdx + "]", refProps, asset, 0);
}

if (refProps.Count > 0)
{
    Console.WriteLine("  Found " + refProps.Count + " asset reference(s):");
    foreach (var (exportName, path, propName, value) in refProps)
        Console.WriteLine("    [" + exportName + "] " + path + propName + " -> " + value);
}
else
{
    Console.WriteLine("  (no object/soft-object property references found)");
}
Console.WriteLine();

// ================================================================
// SECTION 4: COMBO / TRANSITION / ATTACK SEARCH
// ================================================================
Console.WriteLine("==================================================================");
Console.WriteLine("  SECTION 4: PROPERTIES WITH Combo/ComboTree/Transition/Attack");
Console.WriteLine("==================================================================");

string[] searchTerms = { "combo", "comboTree", "transition", "attack" };
var matches = interestingProps.Where(ip =>
{
    string pn = ip.propName;
    return searchTerms.Any(s => pn.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
}).ToList();

var importMatches = new List<(int index, string className, string objectName)>();
for (int i = 0; i < asset.Imports.Count; i++)
{
    var imp = asset.Imports[i];
    string nm = imp.ObjectName.Value?.ToString() ?? "";
    string cls = imp.ClassName.Value?.ToString() ?? "";
    if (searchTerms.Any(s => nm.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 || cls.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
        importMatches.Add((i, cls, nm));
}

if (matches.Count > 0)
{
    Console.WriteLine("  Found " + matches.Count + " property match(es):");
    var seen = new HashSet<string>();
    foreach (var (exportName, path, propName, value) in matches)
    {
        string key = exportName + "|" + path + propName;
        if (seen.Add(key))
            Console.WriteLine("    [" + exportName + "] " + path + propName);
    }
}
else
{
    Console.WriteLine("  (no property names matched - this is an ArchetypeDB, not a ComboTree)");
}

if (importMatches.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("  Found " + importMatches.Count + " import name match(es):");
    foreach (var (idx, cls, nm) in importMatches)
        Console.WriteLine("    [" + idx + "] Class=" + cls + " Name=" + nm);
}

// ================================================================
// SECTION 5: FULL IMPORT TABLE
// ================================================================
Console.WriteLine();
Console.WriteLine("==================================================================");
Console.WriteLine("  SECTION 5: FULL IMPORT TABLE (numbered reference)");
Console.WriteLine("==================================================================");
for (int i = 0; i < asset.Imports.Count; i++)
{
    Console.WriteLine("  [" + i.ToString("D4") + "] " + (asset.Imports[i].ObjectName.Value?.ToString() ?? ""));
}

Console.WriteLine();
Console.WriteLine("==================================================================");
Console.WriteLine("  DUMP COMPLETE");
Console.WriteLine("==================================================================");
