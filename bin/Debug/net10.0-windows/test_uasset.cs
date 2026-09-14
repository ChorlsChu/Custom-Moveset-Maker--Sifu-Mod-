using System;
using System.Reflection;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

string path = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content\DB\AI\Archetypes\Yang\Attacks\Punch\Combo\P1_Combo.uasset";
var asset = new UAsset(path, EngineVersion.VER_UE4_26, null, UAssetAPI.CustomSerializationFlags.None);

Console.WriteLine("=== UAsset public properties ===");
var props = asset.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
foreach (var p in props.OrderBy(x => x.Name))
{
    try
    {
        var val = p.GetValue(asset);
        string display = val switch
        {
            null => "null",
            System.Collections.IList list => $"List({list.Count})",
            System.Collections.IDictionary dict => $"Dict({dict.Count})",
            string s => $"\"{s}\"",
            int i => i.ToString(),
            long l => l.ToString(),
            _ => p.PropertyType.Name
        };
        Console.WriteLine($"  {p.Name}: {display}");
    }
    catch { Console.WriteLine($"  {p.Name}: <error>"); }
}

Console.WriteLine("\n=== UAsset public fields ===");
var fields = asset.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
foreach (var f in fields.OrderBy(x => x.Name))
{
    try
    {
        var val = f.GetValue(asset);
        string display = val switch
        {
            null => "null",
            int i => $"0x{i:X}",
            long l => $"0x{l:X}",
            _ => f.FieldType.Name
        };
        Console.WriteLine($"  {f.Name}: {display}");
    }
    catch { Console.WriteLine($"  {f.Name}: <error>"); }
}
