using System;
using System.IO;
using System.Linq;

string vanillaPath = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Combo\FireDisciple_Advanced_Combo.uasset";
string moddedPath = @"C:\Users\Charles\Downloads\Sifu Modding\Movesets Prototypes\Moveset Maker\SifuMovesetEditor\bin\Debug\net10.0-windows\debug_export\debug_combo.uasset";

byte[] v = File.ReadAllBytes(vanillaPath);
byte[] m = File.ReadAllBytes(moddedPath);

Console.WriteLine($"Vanilla: {v.Length} bytes, Modded: {m.Length} bytes, Diff: {m.Length - v.Length}");

// Find all FName entries "m_TargetNodes" in both files and dump the surrounding bytes
Console.WriteLine("\n=== Searching for 'm_TargetNodes' in vanilla ===");
var vPositions = FindFName(v, "m_TargetNodes");
Console.WriteLine($"Found {vPositions.Count} occurrences");
foreach (var pos in vPositions)
{
    Console.WriteLine($"\n  Offset 0x{pos:X4} ({pos}):");
    // Show 80 bytes before and 200 bytes after
    int start = Math.Max(0, pos - 20);
    int end = Math.Min(v.Length, pos + 200);
    DumpBytes("  VANILLA", v, start, end, pos);
}

Console.WriteLine("\n=== Searching for 'm_TargetNodes' in modded ===");
var mPositions = FindFName(m, "m_TargetNodes");
Console.WriteLine($"Found {mPositions.Count} occurrences");
foreach (var pos in mPositions)
{
    Console.WriteLine($"\n  Offset 0x{pos:X4} ({pos}):");
    int start = Math.Max(0, pos - 20);
    int end = Math.Min(m.Length, pos + 200);
    DumpBytes("  MODDED ", m, start, end, pos);
}

// Now compare: for the QuickHookPunch node (which has existing 1 entry + new 1 entry)
// Find the BYTE(1) entries in modded m_TargetNodes maps (our new entries)
Console.WriteLine("\n\n=== Detailed map comparison: modded Byte(1) entries ===");
foreach (var pos in mPositions)
{
    // Look for the Byte(1) key pattern after m_TargetNodes
    for (int i = pos; i < Math.Min(pos + 300, m.Length - 8); i++)
    {
        // Pattern: ByteProperty marker, then Byte(1) value
        if (m[i] == 0x01 && m[i + 1] == 0x00 && m[i + 2] == 0x00 && m[i + 3] == 0x00)
        {
            // Check if this looks like a Byte(1) key in a map
            // The pattern should be: ... key_type(1=Byte) key(1) value_type(1=Int) value(4 bytes) ...
            Console.Write($"  Byte(1) candidate at 0x{i:X4}: ");
            for (int j = i - 8; j < i + 16 && j < m.Length; j++)
                Console.Write($"{m[j]:X2} ");
            Console.WriteLine();
        }
    }
}

List<int> FindFName(byte[] data, string name)
{
    var results = new List<int>();
    // FName is stored as an index into the name table, not as raw ASCII
    // But the name table entries end with the ASCII string
    // Search for the raw ASCII name
    byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
    for (int i = 0; i < data.Length - nameBytes.Length; i++)
    {
        bool match = true;
        for (int j = 0; j < nameBytes.Length; j++)
        {
            if (data[i + j] != nameBytes[j]) { match = false; break; }
        }
        if (match) results.Add(i);
    }
    return results;
}

void DumpBytes(string label, byte[] data, int start, int end, int highlight)
{
    for (int i = start; i < end; i += 16)
    {
        string hex = "";
        string ascii = "";
        for (int j = 0; j < 16 && i + j < end; j++)
        {
            int offset = i + j;
            hex += $"{data[offset]:X2} ";
            ascii += (data[offset] >= 32 && data[offset] <= 126) ? (char)data[offset] : '.';
            if (offset == highlight) hex += ">>>";
        }
        Console.WriteLine($"  {label} {i:X4}: {hex,-50} {ascii}");
    }
}
