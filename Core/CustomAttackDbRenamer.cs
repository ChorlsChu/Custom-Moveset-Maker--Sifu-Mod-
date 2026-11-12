using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;

namespace SifuMovesetEditor;

/// <summary>One private HitBoxData table that follows the card to its new name.</summary>
public sealed class CustomAttackDbRenameTable
{
    public string OldRel = "";
    public string OldName = "";
    public string NewRel = "";
    public string NewName = "";
}

/// <summary>
/// Everything the rename dialog needs to validate a new name without re-reading the
/// card on every keystroke: the current name, the card's private hit-row tables and
/// which of them are still referenced by another custom card (a rename must not break
/// that card, so those block the operation).
/// </summary>
public sealed class CustomAttackDbInspect
{
    public string CardRel = "";
    public string CardName = "";
    public bool CardFileFound;
    public List<string> PrivateTables = new();
    public List<string> SharedTables = new();
    public List<string> SharedTableOwners = new();
}

/// <summary>Outcome of a library-side AttackDB rename.</summary>
public sealed class CustomAttackDbRenameResult
{
    public bool Success;
    public string Error = "";
    public string Warning = "";
    public string OldRel = "";
    public string OldName = "";
    public string NewRel = "";
    public string NewName = "";
    public string NewGamePath = "";
    public string NewUassetPath = "";
    public List<CustomAttackDbRenameTable> Tables = new();
    /// <summary>Content-relative files created by the rename (card + table companions).</summary>
    public List<string> NewFiles = new();
    /// <summary>Old content-relative files that were actually removed.</summary>
    public List<string> RemovedFiles = new();
}

/// <summary>
/// Renames a custom AttackDB in place: copies the card and its private HitBoxData
/// table(s) to the new name, renames the copies' internal object/package names, repoints
/// the card's m_HitRow imports at the renamed tables, verifies the result, and only then
/// removes the old files. Copy-first means a failed step leaves the original card intact.
/// The private table is only renamed when this card exclusively owns it - a table still
/// referenced by another custom card blocks the rename rather than silently breaking it.
/// Vanilla/shared hit rows are never touched; only the card name changes.
/// </summary>
public static class CustomAttackDbRenamer
{
    private const EngineVersion Eng = EngineVersion.VER_UE4_26;
    private static readonly string[] CompanionExts = { ".uasset", ".uexp", ".ubulk", ".uptnl" };

    /// <summary>Card + private-table state used by the dialog and by <see cref="Rename"/>.</summary>
    public static CustomAttackDbInspect Inspect(string cardRel)
    {
        var info = new CustomAttackDbInspect
        {
            CardRel = NormalizeRel(cardRel)
        };
        info.CardName = Path.GetFileName(info.CardRel.Replace('/', Path.DirectorySeparatorChar));
        string cardUasset = CustomAssets.FileForContentRel(info.CardRel + ".uasset");
        info.CardFileFound = File.Exists(cardUasset);
        if (!info.CardFileFound) return info;

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var hit = AttackHitCard.Read(cardUasset);
            if (hit != null)
            {
                foreach (var w in hit.Windows)
                {
                    if (string.IsNullOrEmpty(w.TableRel)) continue;
                    if (!HitRowPrivatizer.IsPrivateTable(w.TableRel, info.CardName)) continue;
                    // Only a table that actually lives in the custom root can follow the
                    // card. A vanilla table that merely shares the card's name prefix stays
                    // untouched (the delete flow treats it as non-custom too).
                    if (!File.Exists(CustomAssets.FileForContentRel(w.TableRel + ".uasset")))
                        continue;
                    tables.Add(NormalizeRel(w.TableRel));
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("CUSTOM", ex);
        }

        info.PrivateTables.AddRange(tables.OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
        if (tables.Count == 0) return info;

        var otherCards = CustomAttackDbDelete.FindOtherCustomCards(CustomAssets.Root, cardUasset);
        foreach (var tableRel in info.PrivateTables)
        {
            string tableName = Path.GetFileName(tableRel.Replace('/', Path.DirectorySeparatorChar));
            string? owner = null;
            foreach (var other in otherCards)
            {
                if (!AttackDbCard.ReferencesName(other, tableName)) continue;
                owner = Path.GetFileNameWithoutExtension(other);
                break;
            }
            if (owner == null) continue;
            info.SharedTables.Add(tableRel);
            if (!info.SharedTableOwners.Contains(owner, StringComparer.OrdinalIgnoreCase))
                info.SharedTableOwners.Add(owner);
        }
        return info;
    }

    /// <summary>
    /// Validation for the rename dialog: syntax, length, case-only/no-op names, library
    /// and vanilla collisions (card and every private table it would rename), and the
    /// shared-private-table guard. Returns null when the name is usable.
    /// </summary>
    public static string? ValidateName(string newName, CustomAttackDbInspect info,
        IEnumerable<string>? existingGamePaths, string? vanillaContentRoot)
    {
        newName = newName?.Trim() ?? "";
        if (newName.Length == 0) return "Enter a new name for the card.";
        if (!CustomMovesetInfo.IsValidName(newName))
            return "Use only letters, digits and underscores.";
        // Case-only renames are intentionally treated as unchanged: the Windows file
        // system makes a same-name recase fragile and it buys the user nothing.
        if (string.Equals(newName, info.CardName, StringComparison.OrdinalIgnoreCase))
            return $"\"{newName}\" is already the card's name.";

        string destRel = AttackCardCloner.DestRel(info.CardRel, newName);
        string destUasset = CustomAssets.FileForContentRel(destRel + ".uasset");
        if (destUasset.Length > 240)
            return "That name is too long - the export pipeline rejects long paths.";
        if (File.Exists(destUasset))
            return $"\"{newName}\" already exists in the custom library.";

        if (existingGamePaths != null)
        {
            foreach (var p in existingGamePaths)
            {
                var norm = (p ?? "").Replace('\\', '/').TrimStart('/');
                if (norm.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
                    norm = norm.Substring(5);
                if (string.Equals(norm, destRel, StringComparison.OrdinalIgnoreCase))
                    return $"\"{newName}\" is already in the library.";
            }
        }

        if (VanillaAssetExists(vanillaContentRoot, destRel))
            return $"\"{newName}\" already exists in the game's files at {destRel} - renaming there would overwrite a vanilla asset. Pick another name.";

        foreach (var oldTableRel in info.PrivateTables)
        {
            string tableName = Path.GetFileName(oldTableRel.Replace('/', Path.DirectorySeparatorChar));
            if (info.SharedTables.Contains(oldTableRel, StringComparer.OrdinalIgnoreCase))
            {
                string owners = string.Join(", ", info.SharedTableOwners.Take(3));
                if (string.IsNullOrEmpty(owners)) owners = "another custom card";
                return $"The private hit row table \"{tableName}\" is still used by {owners}. "
                    + "Rename or delete that card first - renaming this one would break its hit rows.";
            }

            string newTableRel = RenamedTableRel(oldTableRel, info.CardName, newName);
            string newTableName = Path.GetFileName(newTableRel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(CustomAssets.FileForContentRel(newTableRel + ".uasset")))
                return $"The private hit row table \"{newTableName}\" already exists in the custom library.";
            if (VanillaAssetExists(vanillaContentRoot, newTableRel))
                return $"Renaming the private hit row table to \"{newTableName}\" would overwrite a vanilla asset. Pick another name.";
        }

        return null;
    }

    /// <summary>Convenience overload that inspects the card first (defensive callers).</summary>
    public static string? ValidateName(string newName, string cardRel,
        IEnumerable<string>? existingGamePaths, string? vanillaContentRoot)
        => ValidateName(newName, Inspect(cardRel), existingGamePaths, vanillaContentRoot);

    /// <summary>
    /// Performs the real rename. Copies the card/table families to the new path, renames
    /// their internals, repoints the card's private-table imports, verifies the copies,
    /// then deletes the old files (best effort; a leftover is reported as a warning).
    /// On any failure every new file is rolled back and the original card keeps working.
    /// </summary>
    public static bool Rename(string cardRel, string newName, string? vanillaContentRoot,
        out CustomAttackDbRenameResult result)
    {
        result = new CustomAttackDbRenameResult();
        var newFiles = new List<string>();
        try
        {
            var info = Inspect(cardRel);
            result.OldRel = info.CardRel;
            result.OldName = info.CardName;

            string? validation = ValidateName(newName, info, null, vanillaContentRoot);
            if (validation != null)
            {
                result.Error = validation;
                return false;
            }
            if (!info.CardFileFound)
            {
                result.Error = $"Card file not found: {info.CardRel}";
                return false;
            }

            newName = newName.Trim();
            result.NewName = newName;
            result.NewRel = AttackCardCloner.DestRel(info.CardRel, newName);
            result.NewGamePath = "Game/" + result.NewRel;
            result.NewUassetPath = CustomAssets.FileForContentRel(result.NewRel + ".uasset");

            foreach (var oldTableRel in info.PrivateTables)
            {
                string oldTableName = Path.GetFileName(oldTableRel.Replace('/', Path.DirectorySeparatorChar));
                string newTableRel = RenamedTableRel(oldTableRel, info.CardName, newName);
                result.Tables.Add(new CustomAttackDbRenameTable
                {
                    OldRel = oldTableRel,
                    OldName = oldTableName,
                    NewRel = newTableRel,
                    NewName = Path.GetFileName(newTableRel.Replace('/', Path.DirectorySeparatorChar))
                });
            }

            // 1. Copy every file first. The original stays untouched until the copies
            //    have been renamed, retargeted and verified.
            CopyFamily(info.CardRel + ".uasset", result.NewRel + ".uasset", newFiles);
            foreach (var table in result.Tables)
                CopyFamily(table.OldRel + ".uasset", table.NewRel + ".uasset", newFiles);
            result.NewFiles = newFiles;

            // 2. Rename the card copy's object/self package name.
            if (!CardRenamer.RenameFile(result.NewUassetPath, info.CardName, newName,
                    "/Game/" + info.CardRel, "/Game/" + result.NewRel, Eng, out var error))
            {
                error = $"rename failed: {error}";
                Rollback(newFiles);
                result.NewFiles = new List<string>();
                result.Error = error;
                return false;
            }

            // 3. Rename each private table copy, then repoint the card's imports.
            foreach (var table in result.Tables)
            {
                string tableUasset = CustomAssets.FileForContentRel(table.NewRel + ".uasset");
                if (!CardRenamer.RenameFile(tableUasset, table.OldName, table.NewName,
                        "/Game/" + table.OldRel, "/Game/" + table.NewRel, Eng, out error))
                {
                    error = $"hit row table rename failed: {error}";
                    Rollback(newFiles);
                    result.NewFiles = new List<string>();
                    result.Error = error;
                    return false;
                }
            }

            var tableMap = result.Tables.ToDictionary(t => t.OldRel, t => t.NewRel,
                StringComparer.OrdinalIgnoreCase);
            if (tableMap.Count > 0
                && !HitRowPrivatizer.RetargetPrivateTables(result.NewUassetPath, tableMap, out error))
            {
                error = $"hit row retarget failed: {error}";
                Rollback(newFiles);
                result.NewFiles = new List<string>();
                result.Error = error;
                return false;
            }

            if (!Verify(result.NewUassetPath, newName, result.NewGamePath, result.Tables, out error))
            {
                Rollback(newFiles);
                result.NewFiles = new List<string>();
                result.Error = error;
                return false;
            }

            // 4. The new card is proven good - remove the old families.
            var oldFiles = new List<string>();
            oldFiles.AddRange(ExistingFamily(info.CardRel + ".uasset"));
            foreach (var table in result.Tables)
                oldFiles.AddRange(ExistingFamily(table.OldRel + ".uasset"));
            oldFiles = oldFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (oldFiles.Count > 0)
            {
                CustomAssets.DeleteEntries(oldFiles);
                result.RemovedFiles = oldFiles
                    .Where(rel => !File.Exists(CustomAssets.FileForContentRel(rel)))
                    .ToList();
                var leftover = oldFiles
                    .Where(rel => File.Exists(CustomAssets.FileForContentRel(rel)))
                    .ToList();
                if (leftover.Count > 0)
                    result.Warning = "The card was renamed, but these old files could not be removed: "
                        + string.Join(", ", leftover);
            }

            string oldUasset = CustomAssets.FileForContentRel(info.CardRel + ".uasset");
            AttackDbCard.Invalidate(oldUasset);
            AttackDbCard.Invalidate(result.NewUassetPath);
            AttackHitCard.Invalidate(oldUasset);
            AttackHitCard.Invalidate(result.NewUassetPath);

            ErrorLog.Write("CUSTOM", new Exception(
                $"[RENAME] {info.CardRel} -> {result.NewRel}"
                + (result.Tables.Count > 0
                    ? " (private hit row: " + string.Join(", ", result.Tables.Select(t => t.NewRel)) + ")"
                    : "")));
            result.Success = true;
            return true;
        }
        catch (Exception ex)
        {
            if (newFiles.Count > 0)
            {
                Rollback(newFiles);
                result.NewFiles = new List<string>();
            }
            result.Error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Repoints one unit graph at the renamed card: default/source card paths, payload
    /// replacement card paths, animation slots that held the card, and auto-generated
    /// labels that still exactly match the old card name. A customised label is left
    /// alone. Returns true when anything changed.
    /// </summary>
    public static bool RepointGraph(ComboGraph? graph, string oldCardRel, string newCardGamePath,
        string oldName, string newName)
    {
        if (graph == null) return false;
        bool changed = false;
        foreach (var n in graph.Nodes)
        {
            if (CustomAttackDbDelete.SameCard(n.DefaultDBPath, oldCardRel))
            { n.DefaultDBPath = newCardGamePath; changed = true; }
            if (CustomAttackDbDelete.SameCard(n.SourceDBPath, oldCardRel))
            { n.SourceDBPath = newCardGamePath; changed = true; }
            if (CustomAttackDbDelete.SameCard(n.ReplacedAttackCardRel, oldCardRel))
            { n.ReplacedAttackCardRel = newCardGamePath; changed = true; }
            if (CustomAttackDbDelete.SameCard(n.AnimPath, oldCardRel))
            { n.AnimPath = newCardGamePath; changed = true; }
            if (CustomAttackDbDelete.SameCard(n.DefaultAnimPath, oldCardRel))
            { n.DefaultAnimPath = newCardGamePath; changed = true; }

            if (!string.IsNullOrEmpty(oldName))
            {
                if (string.Equals(n.DisplayName, oldName, StringComparison.Ordinal))
                { n.DisplayName = newName; changed = true; }
                if (string.Equals(n.ImportedDisplayName, oldName, StringComparison.Ordinal))
                { n.ImportedDisplayName = newName; changed = true; }
            }
        }
        return changed;
    }

    /// <summary>New content-relative path of a private table after the card is renamed.</summary>
    public static string RenamedTableRel(string oldTableRel, string oldCardName, string newCardName)
    {
        oldTableRel = NormalizeRel(oldTableRel);
        string tableName = Path.GetFileName(oldTableRel.Replace('/', Path.DirectorySeparatorChar));
        string newTableName = RenamedTableName(tableName, oldCardName, newCardName);
        int slash = oldTableRel.LastIndexOf('/');
        return (slash >= 0 ? oldTableRel[..(slash + 1)] : "") + newTableName;
    }

    /// <summary>
    /// Private tables are named &lt;CardName&gt;_HitBoxData (plus a numeric suffix for
    /// extra imports). Only the card-name prefix changes; the suffix is preserved.
    /// </summary>
    public static string RenamedTableName(string tableName, string oldCardName, string newCardName)
    {
        if (string.Equals(tableName, oldCardName, StringComparison.OrdinalIgnoreCase))
            return newCardName;
        if (tableName.StartsWith(oldCardName + "_", StringComparison.OrdinalIgnoreCase))
            return newCardName + tableName[oldCardName.Length..];
        return tableName;
    }

    // ---- helpers --------------------------------------------------------------

    private static string NormalizeRel(string rel)
        => (rel ?? "").Replace('\\', '/').Trim('/');

    private static bool VanillaAssetExists(string? vanillaContentRoot, string relNoExt)
    {
        if (string.IsNullOrEmpty(vanillaContentRoot) || string.IsNullOrEmpty(relNoExt)) return false;
        string file = Path.Combine(vanillaContentRoot,
            relNoExt.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        return File.Exists(file);
    }

    private static void CopyFamily(string sourceUassetRel, string destUassetRel, List<string> newFiles)
    {
        string srcStem = CustomAssets.FileForContentRel(NormalizeRel(sourceUassetRel));
        string dstStem = CustomAssets.FileForContentRel(NormalizeRel(destUassetRel));
        string dir = Path.GetDirectoryName(dstStem) ?? "";
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string destStemRel = NormalizeRel(destUassetRel);
        destStemRel = destStemRel[..^".uasset".Length];
        bool copiedUasset = false;
        foreach (var ext in CompanionExts)
        {
            string src = Path.ChangeExtension(srcStem, ext);
            if (!File.Exists(src)) continue;
            string dst = Path.ChangeExtension(dstStem, ext);
            if (File.Exists(dst))
                throw new IOException($"Destination already exists: {dst}");
            File.Copy(src, dst);
            newFiles.Add(destStemRel + ext);
            if (ext == ".uasset") copiedUasset = true;
        }
        if (!copiedUasset)
            throw new FileNotFoundException($"Source card missing: {srcStem}");
    }

    private static List<string> ExistingFamily(string uassetRel)
    {
        var list = new List<string>();
        string stemRel = NormalizeRel(uassetRel);
        if (!stemRel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) return list;
        stemRel = stemRel[..^".uasset".Length];
        foreach (var ext in CompanionExts)
        {
            string rel = stemRel + ext;
            if (File.Exists(CustomAssets.FileForContentRel(rel))) list.Add(rel);
        }
        return list;
    }

    private static void Rollback(IEnumerable<string> newFiles)
        => CustomAssets.DeleteEntries(newFiles);

    /// <summary>Reopen-verify: object name, self package and every private window follow.</summary>
    private static bool Verify(string uassetPath, string newName, string newGamePath,
        List<CustomAttackDbRenameTable> tables, out string error)
    {
        error = "";
        try
        {
            var asset = new UAsset(uassetPath, Eng, null, CustomSerializationFlags.None);
            var cardExport = asset.Exports.OfType<NormalExport>().FirstOrDefault(e => e.Data != null)
                ?? asset.Exports.OfType<NormalExport>().FirstOrDefault();
            if (cardExport == null)
            {
                error = "verification failed: no data export after rename";
                return false;
            }
            if (!string.Equals(cardExport.ObjectName?.Value?.ToString(), newName, StringComparison.Ordinal))
            {
                error = $"verification failed: export object is '{cardExport.ObjectName?.Value}', expected '{newName}'";
                return false;
            }
            if (!asset.ContainsNameReference((FString)newGamePath)
                && !asset.ContainsNameReference((FString)("/" + newGamePath)))
            {
                error = $"verification failed: self package path did not move to {newGamePath}";
                return false;
            }

            foreach (var table in tables)
            {
                if (!File.Exists(CustomAssets.FileForContentRel(table.NewRel + ".uasset")))
                {
                    error = $"verification failed: renamed table missing ({table.NewRel})";
                    return false;
                }
            }

            var hit = AttackHitCard.Read(uassetPath);
            if (hit == null)
            {
                error = "verification failed: card unreadable after rename";
                return false;
            }
            var oldTables = new HashSet<string>(tables.Select(t => t.OldRel),
                StringComparer.OrdinalIgnoreCase);
            foreach (var w in hit.Windows)
            {
                if (oldTables.Contains(w.TableRel))
                {
                    error = $"verification failed: window {w.Index} still points at old table {w.TableRel}";
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "verification failed: " + ex.Message;
            return false;
        }
    }
}
