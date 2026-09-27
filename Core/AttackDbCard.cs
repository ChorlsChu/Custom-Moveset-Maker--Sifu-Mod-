using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SifuMovesetEditor;

/// <summary>
/// Values read from an AttackDB card's m_Attack struct:
/// m_iWantedBuildupFrames (IntProperty) and m_fGameplayRange (FloatProperty).
/// Offsets are absolute byte offsets into the file that carries the export payload
/// (the .uexp when the package was cooked with separate bulk data, otherwise the .uasset),
/// so a patch is always a fixed 4-byte overwrite - the file size never changes.
/// </summary>
public sealed class AttackDbCardValues
{
    public int BuildupOffset = -1;
    public int RangeOffset = -1;
    public int Buildup;
    public float Range;
    public string PayloadPath = "";

    public bool HasBuildup => BuildupOffset >= 0;
    public bool HasRange => RangeOffset >= 0;
    public bool Any => HasBuildup || HasRange;
}

/// <summary>
/// Byte-level reader/writer for the two tunable AttackDB card fields.
/// Reading resolves property tags by name-table index (the proven header layout:
/// NameCount@41, NameOffset@45), writing patches the value in place and re-reads to verify.
/// </summary>
public static class AttackDbCard
{
    private sealed class CacheEntry
    {
        public long Ticks;
        public AttackDbCardValues? Values;
    }

    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string GamePathToContentRel(string gamePath)
    {
        var rel = gamePath.Replace("\\", "/").TrimStart('/');
        if (rel.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
            rel = rel.Substring(5);
        if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            rel = rel[..^7];
        else if (rel.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
            rel = rel[..^4];
        return rel;
    }

    public static string? ResolveUassetPath(string contentRoot, string dbPath)
    {
        if (string.IsNullOrWhiteSpace(contentRoot) || string.IsNullOrWhiteSpace(dbPath)) return null;
        var rel = GamePathToContentRel(dbPath).Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(rel)) return null;
        return Path.Combine(contentRoot, rel + ".uasset");
    }

    /// <summary>
    /// The card a node actually points at: the swapped source when it differs from the node's
    /// default, otherwise the default. Every baseline (panel, rows, import compare, export
    /// target) must use this or a swapped node keeps showing its old card's values.
    /// </summary>
    public static string EffectiveCardPath(ComboNode? node)
    {
        if (node == null) return "";
        return !string.IsNullOrEmpty(node.SourceDBPath)
            && !string.Equals(node.SourceDBPath, node.DefaultDBPath, StringComparison.OrdinalIgnoreCase)
            ? node.SourceDBPath
            : node.DefaultDBPath;
    }

    public static AttackDbCardValues? ReadDbPath(string contentRoot, string dbPath)
    {
        var path = ResolveUassetPath(contentRoot, dbPath);
        return path == null ? null : Read(path);
    }

    public static void Invalidate(string uassetPath)
    {
        try { Cache.Remove(uassetPath); } catch { }
    }

    public static AttackDbCardValues? Read(string uassetPath)
    {
        try
        {
            if (string.IsNullOrEmpty(uassetPath) || !File.Exists(uassetPath)) return null;
            long ticks = File.GetLastWriteTimeUtc(uassetPath).Ticks;
            if (Cache.TryGetValue(uassetPath, out var hit) && hit.Ticks == ticks)
                return hit.Values;

            var values = ReadCore(uassetPath);
            Cache[uassetPath] = new CacheEntry { Ticks = ticks, Values = values };
            return values;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Overwrites the given fields in the card's payload file. Returns false (with a reason)
    /// when the card or a requested field cannot be found, or when the re-read verification
    /// does not show exactly what was requested.
    /// </summary>
    public static bool Write(string uassetPath, int? buildup, float? range, out string error)
    {
        error = "";
        var values = Read(uassetPath);
        if (values == null || !values.Any)
        {
            error = "card not readable or has no attack fields";
            return false;
        }
        if (string.IsNullOrEmpty(values.PayloadPath) || !File.Exists(values.PayloadPath))
        {
            error = "payload file missing";
            return false;
        }
        if (buildup.HasValue && !values.HasBuildup)
        {
            error = "m_iWantedBuildupFrames not present in card";
            return false;
        }
        if (range.HasValue && !values.HasRange)
        {
            error = "m_fGameplayRange not present in card";
            return false;
        }

        var bytes = File.ReadAllBytes(values.PayloadPath);
        bool changed = false;

        if (buildup.HasValue)
        {
            var nb = BitConverter.GetBytes(buildup.Value);
            if (!Matches(bytes, values.BuildupOffset, nb))
            {
                Array.Copy(nb, 0, bytes, values.BuildupOffset, 4);
                changed = true;
            }
        }
        if (range.HasValue)
        {
            var nb = BitConverter.GetBytes(range.Value);
            if (!Matches(bytes, values.RangeOffset, nb))
            {
                Array.Copy(nb, 0, bytes, values.RangeOffset, 4);
                changed = true;
            }
        }

        if (changed)
        {
            File.WriteAllBytes(values.PayloadPath, bytes);
            Invalidate(uassetPath);
        }

        var again = Read(uassetPath);
        if (again == null || !again.Any)
        {
            error = "verification failed: card unreadable after write";
            return false;
        }
        if (buildup.HasValue && (!again.HasBuildup || again.Buildup != buildup.Value))
        {
            error = $"verification failed: buildup re-read as {(again.HasBuildup ? again.Buildup.ToString() : "missing")}";
            return false;
        }
        if (range.HasValue && (!again.HasRange
            || BitConverter.SingleToInt32Bits(again.Range) != BitConverter.SingleToInt32Bits(range.Value)))
        {
            error = $"verification failed: range re-read as {(again.HasRange ? again.Range.ToString() : "missing")}";
            return false;
        }
        return true;
    }

    private static bool Matches(byte[] bytes, int offset, byte[] expected)
    {
        if (offset < 0 || offset + expected.Length > bytes.Length) return false;
        for (int i = 0; i < expected.Length; i++)
            if (bytes[offset + i] != expected[i]) return false;
        return true;
    }

    /// <summary>
    /// Copies a card into the export staging area (unless the export already staged it), patches
    /// the given fields into its payload and registers both files in <paramref name="fileEntries"/>.
    /// A card staged earlier in the same export keeps whatever earlier pass wrote to it - the
    /// patch is applied on top; a file left over from a previous export is rebuilt from vanilla.
    /// </summary>
    public static bool StageAndPatch(
        string vanillaUasset,
        string outUasset,
        string destBase,
        int? buildup,
        float? range,
        List<(string src, string dest)> fileEntries,
        out string error)
    {
        error = "";
        if (!File.Exists(vanillaUasset))
        {
            error = $"vanilla card missing: {vanillaUasset}";
            return false;
        }

        try
        {
            var outDir = Path.GetDirectoryName(outUasset);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            string? outUexp = Path.ChangeExtension(outUasset, ".uexp");
            if (!fileEntries.Any(e => e.src == outUasset))
            {
                File.Copy(vanillaUasset, outUasset, true);
                string? vanillaUexp = Path.ChangeExtension(vanillaUasset, ".uexp");
                if (File.Exists(vanillaUexp)) File.Copy(vanillaUexp, outUexp!, true);
                fileEntries.Add((outUasset, destBase + ".uasset"));
                if (File.Exists(outUexp!)) fileEntries.Add((outUexp!, destBase + ".uexp"));
            }
            else if (!File.Exists(outUexp!) && File.Exists(Path.ChangeExtension(vanillaUasset, ".uexp")))
            {
                File.Copy(Path.ChangeExtension(vanillaUasset, ".uexp")!, outUexp!, true);
                fileEntries.Add((outUexp!, destBase + ".uexp"));
            }

            Invalidate(outUasset);
            if (!Write(outUasset, buildup, range, out error))
                return false;

            if (!fileEntries.Any(e => e.src == outUasset))
                fileEntries.Add((outUasset, destBase + ".uasset"));
            if (File.Exists(outUexp!) && !fileEntries.Any(e => e.src == outUexp!))
                fileEntries.Add((outUexp!, destBase + ".uexp"));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static AttackDbCardValues? ReadCore(string uassetPath)
    {
        var head = File.ReadAllBytes(uassetPath);
        if (head.Length < 73 || head[0] != 0xC1 || head[1] != 0x83 || head[2] != 0x2A || head[3] != 0x9E)
            return null;

        int nameCount = BitConverter.ToInt32(head, 41);
        int nameOffset = BitConverter.ToInt32(head, 45);
        if (nameCount <= 0 || nameCount > 2_000_000 || nameOffset <= 0 || nameOffset >= head.Length)
            return null;

        var names = ReadNames(head, nameCount, nameOffset);
        if (names == null) return null;

        int intType = names.IndexOf("IntProperty");
        int floatType = names.IndexOf("FloatProperty");
        if (intType < 0 && floatType < 0) return null;

        int buildupName = names.IndexOf("m_iWantedBuildupFrames");
        int rangeName = names.IndexOf("m_fGameplayRange");
        if (buildupName < 0 && rangeName < 0) return null;

        var result = new AttackDbCardValues();
        string? buildupPath = null;
        string? rangePath = null;

        var candidates = new List<string>();
        var uexpPath = Path.ChangeExtension(uassetPath, ".uexp");
        if (!string.IsNullOrEmpty(uexpPath) && File.Exists(uexpPath)) candidates.Add(uexpPath);
        if (!candidates.Contains(uassetPath)) candidates.Add(uassetPath);

        foreach (var payloadPath in candidates)
        {
            var payload = File.ReadAllBytes(payloadPath);
            if (payload.Length < 45) continue;

            if (!result.HasBuildup && buildupName >= 0 && intType >= 0)
            {
                int off = FindTagValueOffset(payload, buildupName, intType);
                if (off >= 0)
                {
                    result.BuildupOffset = off;
                    result.Buildup = BitConverter.ToInt32(payload, off);
                    buildupPath = payloadPath;
                }
            }
            if (!result.HasRange && rangeName >= 0 && floatType >= 0)
            {
                int off = FindTagValueOffset(payload, rangeName, floatType);
                if (off >= 0)
                {
                    result.RangeOffset = off;
                    result.Range = BitConverter.ToSingle(payload, off);
                    rangePath = payloadPath;
                }
            }
        }

        if (result.HasBuildup && result.HasRange && buildupPath != rangePath)
            return null;

        result.PayloadPath = buildupPath ?? rangePath ?? "";
        return result.Any ? result : null;
    }

    /// <summary>
    /// Locates a property tag (Name FName | Type FName | Size i32 | ArrayIndex i32 | ... ) and
    /// returns the absolute offset of its value: payload starts at tag+24 for Int/Float, the
    /// first byte there is HasPropertyGuid (0/1), so the value is at +25 or +25+16.
    /// </summary>
    private static int FindTagValueOffset(byte[] b, int nameIdx, int typeIdx)
    {
        int limit = b.Length - 45;
        for (int p = 0; p < limit; p++)
        {
            if (BitConverter.ToInt32(b, p) != nameIdx) continue;
            if (BitConverter.ToInt32(b, p + 8) != typeIdx) continue;
            if (BitConverter.ToInt32(b, p + 16) != 4) continue;
            if (BitConverter.ToInt32(b, p + 20) != 0) continue;
            int hasGuid = b[p + 24];
            if (hasGuid != 0 && hasGuid != 1) continue;
            return p + 25 + (hasGuid == 1 ? 16 : 0);
        }
        return -1;
    }

    private static List<string>? ReadNames(byte[] bb, int nameCount, int nameOffset)
    {
        try
        {
            var list = new List<string>(nameCount);
            int p = nameOffset;
            for (int i = 0; i < nameCount; i++)
            {
                if (p + 4 > bb.Length) return null;
                int len = BitConverter.ToInt32(bb, p);
                p += 4;
                if (len >= 0)
                {
                    if (p + len + 4 > bb.Length) return null;
                    list.Add(len > 0 ? Encoding.UTF8.GetString(bb, p, len).TrimEnd('\0') : "");
                    p += len;
                }
                else
                {
                    int n = -len;
                    if (p + n * 2 + 4 > bb.Length) return null;
                    list.Add(Encoding.Unicode.GetString(bb, p, n * 2).TrimEnd('\0'));
                    p += n * 2;
                }
                p += 4;
            }
            return list;
        }
        catch
        {
            return null;
        }
    }
}
