using System;
using System.IO;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;

namespace SifuMovesetEditor;

/// <summary>
/// The rename a cloned card must carry, extracted from ExportDialog.PatchDbAnimation so
/// export-time and library-time cloning share the exact proven logic. Tree slots resolve
/// Package.Object, so a card copied to a new filename that still carries the template's
/// object name leaves the game with no object to build the attack from (the player
/// idles). The rename touches two places: the export's own FName (a fresh name entry, so
/// name-table strings shared with other values like the m_AnimationRow row keep theirs)
/// and the card's self /Game/... package-path entry in the name table when present.
/// </summary>
public static class CardRenamer
{
    /// <summary>
    /// Content-root file path to its /Game/ package path: "...\Sifu\Content\DB\X.uasset"
    /// -> "/Game/DB/X". False when the path has no /Content/ segment or an empty root.
    /// </summary>
    public static bool TryGamePath(string filePath, out string gamePath)
    {
        gamePath = "";
        string norm = filePath.Replace('\\', '/');
        int i = norm.LastIndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        string rel = norm[(i + "/Content/".Length)..];
        if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            rel = rel[..^".uasset".Length];
        rel = rel.TrimEnd('/');
        if (rel.Length == 0) return false;
        gamePath = "/Game/" + rel;
        return true;
    }

    /// <summary>
    /// Applies the clone rename on a loaded asset: the export's ObjectName plus the
    /// name-table self package-path entry. Same-name renames (old == new) are no-ops.
    /// <paramref name="oldGamePath"/>/<paramref name="newGamePath"/> are the "/Game/..."
    /// package paths of the source and destination files.
    /// </summary>
    public static void Apply(UAsset asset, NormalExport cardExport,
        string oldObj, string newObj, string oldGamePath, string newGamePath)
    {
        if (string.Equals(oldObj, newObj, StringComparison.Ordinal)) return;
        cardExport.ObjectName = FName.FromString(asset, newObj);
        if (!string.IsNullOrEmpty(oldGamePath) && !string.IsNullOrEmpty(newGamePath)
            && asset.ContainsNameReference((FString)oldGamePath))
            asset.SetNameReference(asset.SearchNameReference((FString)oldGamePath), new FString(newGamePath));
    }

    /// <summary>
    /// Loads <paramref name="uassetPath"/>, applies the rename for a
    /// <paramref name="newObj"/>/<paramref name="newGamePath"/> move, and writes it back.
    /// Used by the library-side card cloner and the hit-row privatizer on their
    /// CustomAssets copies. <paramref name="oldGamePath"/> is the source package path
    /// (null/empty when unknown - every name-table entry ending in the old leaf is
    /// rewritten instead, which covers a copy whose source lived in another folder).
    /// Writes only when something actually changed, so an untouched round trip cannot
    /// perturb the bytes.
    /// </summary>
    public static bool RenameFile(string uassetPath, string oldObj, string newObj,
        string? oldGamePath, string newGamePath, EngineVersion eng, out string error)
    {
        error = "";
        try
        {
            var asset = new UAsset(uassetPath, eng, null, CustomSerializationFlags.None);
            NormalExport? cardExport = null;
            foreach (var exp in asset.Exports)
            {
                if (exp is NormalExport ne)
                {
                    cardExport = ne;
                    break;
                }
            }
            if (cardExport == null)
            {
                error = "no data export found to rename";
                return false;
            }

            // The destination package path: prefer the file's own /Game/ path (a copy
            // under a Content root), else the path the caller derived.
            if (!TryGamePath(uassetPath, out string newPkgActual))
                newPkgActual = newGamePath;

            bool changed = false;
            string currentObj = cardExport.ObjectName?.Value?.ToString() ?? "";
            if (!string.Equals(currentObj, newObj, StringComparison.Ordinal))
            {
                cardExport.ObjectName = FName.FromString(asset, newObj);
                changed = true;
            }

            if (!string.IsNullOrEmpty(oldGamePath)
                && asset.ContainsNameReference((FString)oldGamePath))
            {
                asset.SetNameReference(asset.SearchNameReference((FString)oldGamePath),
                    new FString(newPkgActual));
                changed = true;
            }
            else
            {
                // Source package path unknown (or absent from the table): rewrite every
                // name entry that ends in the old leaf - the self path and any soft ref
                // to the old package both need to follow the file.
                string suffix = "/" + oldObj;
                var nameMap = asset.GetNameMapIndexList();
                for (int i = 0; i < nameMap.Count; i++)
                {
                    string s = nameMap[i]?.ToString() ?? "";
                    if (s.Length > suffix.Length && s.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        asset.SetNameReference(i, new FString(newPkgActual));
                        changed = true;
                    }
                }
            }

            if (changed) asset.Write(uassetPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
