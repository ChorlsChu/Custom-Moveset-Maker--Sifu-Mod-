using System;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

if (args.Length < 1)
{
    Console.WriteLine("Usage: _pak_extract <pakfile>");
    return;
}

string pakPath = args[0];
byte[] pak = File.ReadAllBytes(pakPath);
Console.WriteLine($"Pak size: {pak.Length}");

// Find FPakInfo at end of file
long magicOffset = -1;
for (long pos = pak.Length - 4; pos >= Math.Max(0, pak.Length - 512); pos--)
{
    if (pak[pos] == 0xE1 && pak[pos + 1] == 0x12 && pak[pos + 2] == 0x6F && pak[pos + 3] == 0x5A)
    { magicOffset = pos; break; }
}
long indexOffset = BitConverter.ToInt64(pak, (int)magicOffset + 8);
Console.WriteLine($"Data section: 0 to {indexOffset - 1} ({indexOffset} bytes)");

// Find UAsset magic
int uassetStart = -1;
for (int i = 0; i < indexOffset - 4; i++)
{
    if (pak[i] == 0xC1 && pak[i + 1] == 0x83 && pak[i + 2] == 0x2A && pak[i + 3] == 0x9E)
    {
        int lv = BitConverter.ToInt32(pak, i + 4);
        if (lv == -7) { uassetStart = i; break; }
    }
}
Console.WriteLine($"UAsset at offset {uassetStart}");

// Save the entire data after uasset start
byte[] allData = new byte[indexOffset - uassetStart];
Array.Copy(pak, uassetStart, allData, 0, allData.Length);

// Try loading with UAssetAPI to determine where uasset ends
string tempFile = Path.Combine(Path.GetTempPath(), "temp_combo.uasset");
File.WriteAllBytes(tempFile, allData);

try
{
    var asset = new UAsset(tempFile, EngineVersion.VER_UE4_26);
    Console.WriteLine($"UAsset loaded: {asset.Exports.Count} exports, engine={asset.EngineVersion}");
    Console.WriteLine($"  AssetName: {asset.AssetName?.Value}");
    Console.WriteLine($"  HeadersSize: {asset.HeadersSize}");
    
    // Save uasset
    File.WriteAllBytes("modded_combo.uasset", allData);
    Console.WriteLine($"Saved modded_combo.uasset ({allData.Length} bytes total, contains both uasset+uexp)");
}
catch (Exception ex)
{
    Console.WriteLine($"UAssetAPI error: {ex.Message}");
    File.WriteAllBytes("modded_combo_raw.bin", allData);
    Console.WriteLine($"Saved modded_combo_raw.bin ({allData.Length} bytes)");
}
