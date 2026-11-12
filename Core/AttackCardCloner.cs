using System;
using System.Collections.Generic;
using System.IO;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace SifuMovesetEditor;

/// <summary>
/// Clones an attack card (the AttackDB uasset+uexp) into CustomAssets under a new name,
/// with the export-proven rename (CardRenamer) and - optionally - a private HitBoxData
/// row (HitRowPrivatizer). The clone lands next to its source's folder so it keeps the
/// archetype/unit grouping, appears in the Custom tab on the next scan and is staged into
/// the export pak by the existing CustomAssets-first pipeline. The clone keeps the
/// source's folder, so a name the game already uses at that path would ship as a
/// same-path override (and the renamed object would break vanilla references) - both
/// ValidateName and Clone refuse such names when the vanilla content root is known.
/// </summary>
public static class AttackCardCloner
{
    private const EngineVersion Eng = EngineVersion.VER_UE4_26;

    /// <summary>Destination content rel (no extension) for a clone of <paramref name="cardRel"/>.</summary>
    public static string DestRel(string cardRel, string newName)
    {
        var rel = cardRel.Replace('\\', '/').Trim('/');
        int slash = rel.LastIndexOf('/');
        return (slash >= 0 ? rel[..(slash + 1)] : "") + newName;
    }

    /// <summary>Destination "/Game/..." path the clone will register under.</summary>
    public static string DestGamePath(string cardRel, string newName) => "Game/" + DestRel(cardRel, newName);

    /// <summary>True when the content tree already ships an asset at this content-relative
    /// path (no extension). A null/empty root means "no opinion" (no content detected).</summary>
    private static bool VanillaAssetExists(string? vanillaContentRoot, string relNoExt)
    {
        if (string.IsNullOrEmpty(vanillaContentRoot) || string.IsNullOrEmpty(relNoExt)) return false;
        string file = Path.Combine(vanillaContentRoot,
            relNoExt.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        return File.Exists(file);
    }

    /// <summary>Blocking message when the clone would land exactly on a vanilla asset
    /// path, or null when the name is free. A clone keeps the source's folder, so its
    /// relative path is deterministically the collision surface: shipping there would
    /// override the vanilla asset for every unit (and the renamed object would break
    /// references to the original). The optional private hit-row table is checked too -
    /// it is written next to the card as &lt;Name&gt;_HitBoxData.</summary>
    private static string? VanillaPathCollision(string? vanillaContentRoot, string cardRel,
        string newName, bool cloneHitWindow)
    {
        string destRel = DestRel(cardRel, newName);
        if (VanillaAssetExists(vanillaContentRoot, destRel))
            return $"\"{newName}\" already exists in the game's files at {destRel} - a clone there would overwrite a vanilla asset. Pick another name.";
        if (cloneHitWindow && VanillaAssetExists(vanillaContentRoot, destRel + "_HitBoxData"))
            return $"\"{newName}\" would overwrite the game's {newName}_HitBoxData table at this path. Pick another name.";
        return null;
    }

    /// <summary>
    /// Validation for the clone dialog: name syntax, export path length and uniqueness
    /// (against the library's card paths, the files already on disk, and - when
    /// <paramref name="vanillaContentRoot"/> is supplied - the game's own content).
    /// Returns null when the name is usable, otherwise the message to show.
    /// </summary>
    public static string? ValidateName(string newName, string cardRel, IEnumerable<string>? existingGamePaths,
        string? vanillaContentRoot = null, bool cloneHitWindow = false)
    {
        newName = newName?.Trim() ?? "";
        if (!CustomMovesetInfo.IsValidName(newName))
            return "Use only letters, digits and underscores.";

        string destRel = DestRel(cardRel, newName);
        string destUasset = CustomAssets.FileForContentRel(destRel + ".uasset");
        // Export stages CustomAssets into the pak; staged paths past ~260 chars abort,
        // so a long card name must be caught here rather than at export time.
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

        // Library check above only knows indexed cards; this one catches any vanilla
        // asset at the same path (unindexed cards, combo trees, tables, blueprints).
        return VanillaPathCollision(vanillaContentRoot, cardRel, newName, cloneHitWindow);
    }

    /// <summary>
    /// Copies the card to CustomAssets, renames it, optionally privatizes its hit row,
    /// and returns the new file/game paths. Rolls the card copy back when the hit-window
    /// step fails so a half-clone never lingers in the library.
    /// </summary>
    public static bool Clone(string sourceUassetPath, string cardRel, string newName,
        bool cloneHitWindow, string vanillaContentRoot, out string newUassetPath,
        out string newGamePath, out string error)
    {
        newUassetPath = "";
        newGamePath = "";
        error = "";
        try
        {
            newName = newName?.Trim() ?? "";
            if (!CustomMovesetInfo.IsValidName(newName))
            {
                error = "Use only letters, digits and underscores.";
                return false;
            }
            if (!File.Exists(sourceUassetPath))
            {
                error = $"Source card not found: {sourceUassetPath}";
                return false;
            }

            string destRel = DestRel(cardRel, newName);
            newUassetPath = CustomAssets.FileForContentRel(destRel + ".uasset");
            newGamePath = DestGamePath(cardRel, newName);
            if (File.Exists(newUassetPath))
            {
                error = $"\"{newName}\" already exists in the custom library.";
                return false;
            }

            // Defense in depth for callers that skip ValidateName: never write onto a
            // path the game itself ships (see VanillaPathCollision).
            string? vanillaCollision = VanillaPathCollision(vanillaContentRoot, cardRel, newName, cloneHitWindow);
            if (vanillaCollision != null)
            {
                error = vanillaCollision;
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(newUassetPath)!);
            File.Copy(sourceUassetPath, newUassetPath, true);
            string srcUexp = Path.ChangeExtension(sourceUassetPath, ".uexp");
            string dstUexp = Path.ChangeExtension(newUassetPath, ".uexp");
            if (File.Exists(srcUexp)) File.Copy(srcUexp, dstUexp!, true);
            else if (File.Exists(dstUexp)) File.Delete(dstUexp);

            // The clone must carry its own object name: tree slots resolve Package.Object,
            // so a copy still named after the template leaves the game with no object to
            // build the attack from (the player idles).
            string oldStem = Path.GetFileNameWithoutExtension(sourceUassetPath);
            CardRenamer.TryGamePath(sourceUassetPath, out string oldPkg);
            if (!CardRenamer.RenameFile(newUassetPath, oldStem, newName,
                    string.IsNullOrEmpty(oldPkg) ? null : oldPkg,
                    "/Game/" + destRel, Eng, out error))
            {
                error = $"rename failed: {error}";
                try { File.Delete(newUassetPath); if (File.Exists(dstUexp)) File.Delete(dstUexp); } catch { }
                return false;
            }

            if (cloneHitWindow)
            {
                if (!HitRowPrivatizer.Privatize(newUassetPath, destRel, vanillaContentRoot, out error))
                {
                    error = $"hit window copy failed: {error}";
                    // Roll the card (and any table the privatizer got as far as
                    // copying) back so the user can retry cleanly.
                    try { File.Delete(newUassetPath); if (File.Exists(dstUexp)) File.Delete(dstUexp); } catch { }
                    string dir = destRel.Contains('/') ? destRel[..destRel.LastIndexOf('/')] : "";
                    string orphan = CustomAssets.FileForContentRel(
                        (dir.Length > 0 ? dir + "/" : "") + newName + "_HitBoxData.uasset");
                    try
                    {
                        if (File.Exists(orphan)) File.Delete(orphan);
                        string orphanUexp = Path.ChangeExtension(orphan, ".uexp");
                        if (File.Exists(orphanUexp)) File.Delete(orphanUexp);
                    }
                    catch { }
                    return false;
                }
            }

            ErrorLog.Write("CLONE", new Exception(
                $"[CLONE] {oldStem} -> {destRel} (private hit window: {cloneHitWindow})"));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
