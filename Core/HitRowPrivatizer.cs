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

namespace SifuMovesetEditor;

/// <summary>
/// Gives an attack card its own HitBoxData table: copies the vanilla table the card's
/// m_HitBoxes windows reference into CustomAssets (one table file per card, named
/// &lt;CardName&gt;_HitBoxData next to the card), renames the copy's export + self
/// package entry, and repoints the card's m_HitRow.DataTable imports at it. After this
/// the card owns its hit rows - Parriable/damage/etc. edits on the clone never leak to
/// other attacks that used to share the vanilla row. Shared-name detection elsewhere
/// (LooksSharedRow, HITROW SHARED export warnings) treats the card-named table as
/// private by construction.
/// </summary>
public static class HitRowPrivatizer
{
    private const EngineVersion Eng = EngineVersion.VER_UE4_26;

    /// <summary>
    /// True when the table is named after the card (&lt;CardName&gt;_...) - a
    /// privatized copy is private by construction, no row-name heuristic needed.
    /// </summary>
    public static bool IsPrivateTable(string tableRel, string cardFileName)
    {
        var table = Path.GetFileName(tableRel.Replace('/', Path.DirectorySeparatorChar));
        return table.StartsWith(cardFileName + "_", StringComparison.OrdinalIgnoreCase)
            || table.Equals(cardFileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies the card's referenced HitBoxData table into CustomAssets as a private
    /// &lt;CardName&gt;_HitBoxData and repoints the card's m_HitRow imports at it.
    /// Idempotent: a card already using its private table is left untouched.
    /// <paramref name="cardUassetPath"/> is the card's file on disk (CustomAssets copy);
    /// <paramref name="cardRel"/> its content-relative path without extension;
    /// <paramref name="vanillaContentRoot"/> the extracted content root used to find the
    /// source table (AttackDbContentDir()).
    /// </summary>
    public static bool Privatize(string cardUassetPath, string cardRel, string vanillaContentRoot, out string error)
    {
        error = "";
        try
        {
            if (!File.Exists(cardUassetPath))
            {
                error = $"card missing: {cardUassetPath}";
                return false;
            }
            var asset = new UAsset(cardUassetPath, Eng, null, CustomSerializationFlags.None);
            var windows = FindHitWindows(asset);
            if (windows == null || windows.Count == 0)
            {
                error = "card has no m_HitBoxes windows";
                return false;
            }

            string cardStem = Path.GetFileName(cardRel);
            string cardDir = cardRel.Contains('/')
                ? cardRel[..cardRel.LastIndexOf('/')]
                : "";

            // One repoint per distinct DataTable import (windows share the import).
            var byImport = new Dictionary<int, (string TableRel, string RowName)>();
            foreach (var w in windows)
            {
                var (importIndex, tableRel, rowName) = ReadRowRef(asset, w.Value);
                if (importIndex < 0 || string.IsNullOrEmpty(tableRel)) continue;
                if (!byImport.ContainsKey(importIndex))
                    byImport[importIndex] = (tableRel, rowName);
            }
            if (byImport.Count == 0)
            {
                error = "card has no m_HitRow DataTable references to privatize";
                return false;
            }

            int copied = 0;
            foreach (var (importIndex, refInfo) in byImport)
            {
                string oldTableRel = refInfo.TableRel;
                string oldTableName = Path.GetFileName(oldTableRel.Replace('/', Path.DirectorySeparatorChar));
                if (IsPrivateTable(oldTableRel, cardStem)) continue; // already private

                string newTableName = cardStem + "_HitBoxData"
                    + (copied == 0 ? "" : (copied + 1).ToString());
                string newTableRel = (cardDir.Length > 0 ? cardDir + "/" : "") + newTableName;

                // Source table: CustomAssets first (a custom card may already point at a
                // modded table), vanilla content tree otherwise.
                string? srcTable = CustomAssets.FileForContentRel(oldTableRel + ".uasset");
                if (!File.Exists(srcTable))
                    srcTable = AttackDbCard.ResolveUassetPath(vanillaContentRoot, oldTableRel);
                if (srcTable == null || !File.Exists(srcTable))
                {
                    error = $"hit row table not found: {oldTableRel}";
                    return false;
                }

                string dstTable = CustomAssets.FileForContentRel(newTableRel + ".uasset");
                Directory.CreateDirectory(Path.GetDirectoryName(dstTable)!);
                File.Copy(srcTable, dstTable, true);
                string srcUexp = Path.ChangeExtension(srcTable, ".uexp");
                if (File.Exists(srcUexp))
                    File.Copy(srcUexp, Path.ChangeExtension(dstTable, ".uexp")!, true);

                // The table copy must carry its new object name, same as a cloned card.
                // The copy lands next to the card while its source may live in another
                // folder, so pass the source package path explicitly.
                CardRenamer.TryGamePath(srcTable, out string oldTablePkg);
                if (!CardRenamer.RenameFile(dstTable, oldTableName, newTableName,
                        string.IsNullOrEmpty(oldTablePkg) ? null : oldTablePkg,
                        "/Game/" + newTableRel, Eng, out error))
                {
                    error = $"private table rename failed: {error}";
                    return false;
                }

                // Repoint this import chain: object name + outer package path.
                asset.Imports[importIndex].ObjectName = FName.FromString(asset, newTableName);
                int outer = asset.Imports[importIndex].OuterIndex?.Index ?? 0;
                if (outer < 0 && -(outer + 1) < asset.Imports.Count)
                    asset.Imports[-(outer + 1)].ObjectName = FName.FromString(asset, "/Game/" + newTableRel);

                ErrorLog.Write("CLONE", new Exception(
                    $"[HITROW] {cardStem}: private table {oldTableRel} -> {newTableRel} (row '{refInfo.RowName}')"));
                copied++;
            }

            if (copied == 0)
            {
                // Everything already private - nothing to write.
                return true;
            }

            asset.Write(cardUassetPath);
            AttackDbCard.Invalidate(cardUassetPath);

            // Reopen-verify: every window must resolve its (now private) table and row.
            var post = new UAsset(cardUassetPath, Eng, null, CustomSerializationFlags.None);
            var postWindows = FindHitWindows(post);
            if (postWindows == null || postWindows.Count == 0)
            {
                error = "verification failed: card unreadable after privatize";
                return false;
            }
            foreach (var w in postWindows)
            {
                var (_, tableRel, rowName) = ReadRowRef(post, w.Value);
                if (string.IsNullOrEmpty(tableRel) || string.IsNullOrEmpty(rowName)) continue;
                if (!IsPrivateTable(tableRel, cardStem))
                {
                    error = $"verification failed: window still points at shared table {tableRel}";
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Repoints a card's m_HitRow DataTable imports from one set of private table paths
    /// to another (the library-side rename). Unlike <see cref="Privatize"/> this does not
    /// copy or rename any table file; the caller has already placed the new tables. Every
    /// window that referenced a mapped old path must resolve the corresponding new path
    /// after the write, otherwise the card is left untouched and the rename refuses.
    /// </summary>
    public static bool RetargetPrivateTables(string cardUassetPath,
        IReadOnlyDictionary<string, string> oldToNewTableRels, out string error)
    {
        error = "";
        if (oldToNewTableRels == null || oldToNewTableRels.Count == 0) return true;
        try
        {
            if (!File.Exists(cardUassetPath))
            {
                error = $"card missing: {cardUassetPath}";
                return false;
            }
            var asset = new UAsset(cardUassetPath, Eng, null, CustomSerializationFlags.None);
            var windows = FindHitWindows(asset);
            if (windows == null || windows.Count == 0)
            {
                error = "card has no m_HitBoxes windows";
                return false;
            }

            // Collect the pre-mutation view: which import each window uses and which
            // new table it must resolve to afterwards.
            var expected = new Dictionary<int, string>();
            var byImport = new Dictionary<int, (string OldRel, string NewRel)>();
            for (int i = 0; i < windows.Count; i++)
            {
                var (importIndex, tableRel, _) = ReadRowRef(asset, windows[i].Value);
                if (importIndex < 0 || string.IsNullOrEmpty(tableRel)) continue;
                if (!oldToNewTableRels.TryGetValue(tableRel, out var newRel)
                    || string.IsNullOrEmpty(newRel)) continue;
                expected[i] = newRel;
                if (byImport.TryGetValue(importIndex, out var existing))
                {
                    if (!string.Equals(existing.NewRel, newRel, StringComparison.OrdinalIgnoreCase))
                    {
                        error = $"windows map the same DataTable import to two names";
                        return false;
                    }
                    continue;
                }
                byImport[importIndex] = (tableRel, newRel);
            }

            if (byImport.Count == 0) return true; // nothing to retarget

            foreach (var (importIndex, pair) in byImport)
            {
                string newTableName = Path.GetFileName(pair.NewRel.Replace('/', Path.DirectorySeparatorChar));
                asset.Imports[importIndex].ObjectName = FName.FromString(asset, newTableName);
                int outer = asset.Imports[importIndex].OuterIndex?.Index ?? 0;
                if (outer < 0 && -(outer + 1) < asset.Imports.Count)
                    asset.Imports[-(outer + 1)].ObjectName = FName.FromString(asset, "/Game/" + pair.NewRel);
            }

            asset.Write(cardUassetPath);
            AttackDbCard.Invalidate(cardUassetPath);
            AttackHitCard.Invalidate(cardUassetPath);

            var post = new UAsset(cardUassetPath, Eng, null, CustomSerializationFlags.None);
            var postWindows = FindHitWindows(post);
            if (postWindows == null || postWindows.Count != windows.Count)
            {
                error = "verification failed: card unreadable after retarget";
                return false;
            }
            foreach (var (position, expectedRel) in expected)
            {
                var (_, tableRel, _) = ReadRowRef(post, postWindows[position].Value);
                if (!string.Equals(tableRel, expectedRel, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"verification failed: window {position} points at {tableRel}, expected {expectedRel}";
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Window's DataTable import index (-1 = none), table rel and row name.</summary>
    private static (int ImportIndex, string TableRel, string RowName) ReadRowRef(UAsset asset, List<PropertyData>? windowProps)
    {
        var hitRow = windowProps?.OfType<StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == "m_HitRow");
        var rn = hitRow?.Value?.OfType<NamePropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "RowName");
        string rowName = rn?.Value?.ToString() ?? "";

        var dt = hitRow?.Value?.OfType<ObjectPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "DataTable");
        if (dt?.Value == null || dt.Value.Index >= 0) return (-1, "", rowName);
        int ii = -(dt.Value.Index + 1);
        if (ii < 0 || ii >= asset.Imports.Count) return (-1, "", rowName);

        var imp = asset.Imports[ii];
        if (imp.OuterIndex == null || imp.OuterIndex.Index >= 0) return (-1, "", rowName);
        int oi = -(imp.OuterIndex.Index + 1);
        if (oi < 0 || oi >= asset.Imports.Count) return (-1, "", rowName);
        string pkg = asset.Imports[oi].ObjectName?.Value?.ToString() ?? "";
        if (pkg.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            pkg = pkg.Substring(6);
        return (ii, pkg, rowName);
    }

    private static List<StructPropertyData>? FindHitWindows(UAsset asset)
    {
        var export = asset.Exports.OfType<NormalExport>().FirstOrDefault(e => e.Data != null);
        if (export?.Data == null) return null;
        var mAttack = export.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Attack");
        if (mAttack is not StructPropertyData attack || attack.Value == null) return null;
        var arr = attack.Value.OfType<ArrayPropertyData>()
            .FirstOrDefault(a => a.Name?.Value?.ToString() == "m_HitBoxes");
        if (arr?.Value == null) return null;
        return arr.Value.OfType<StructPropertyData>().ToList();
    }
}
