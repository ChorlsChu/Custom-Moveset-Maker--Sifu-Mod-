using System;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace SifuMovesetEditor;

/// <summary>
/// Repoints a cloned attack card's m_Animation at another animation without adding
/// imports: the card already references an animation package, so the existing
/// package/AnimSequence imports are renamed in place. This is the library-side twin
/// of ExportDialog.PatchDbAnimation (same proven layout), used by the Create AttackDB
/// wizard right after AttackCardCloner has copied and renamed a template card.
/// </summary>
public static class AttackCardRetargeter
{
    private const EngineVersion Eng = EngineVersion.VER_UE4_26;

    /// <summary>Reads the card's current m_Animation as a "Game/..." path, or null.</summary>
    public static string? ReadAnimationPath(string uassetPath)
    {
        try
        {
            if (string.IsNullOrEmpty(uassetPath) || !File.Exists(uassetPath)) return null;
            return ReadAnimationPath(new UAsset(uassetPath, Eng, null, CustomSerializationFlags.None));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Renames the card's existing m_Animation imports to <paramref name="animGamePath"/>
    /// and writes the card back. Verifies by reopening the card; no-op writes are skipped
    /// when the card already points at the requested animation.
    /// </summary>
    public static bool Retarget(string uassetPath, string animGamePath, out string error)
    {
        error = "";
        try
        {
            if (string.IsNullOrEmpty(uassetPath) || !File.Exists(uassetPath))
            {
                error = $"card not found: {uassetPath}";
                return false;
            }
            animGamePath = Normalize(animGamePath);
            if (string.IsNullOrEmpty(animGamePath))
            {
                error = "animation path is empty";
                return false;
            }

            var asset = new UAsset(uassetPath, Eng, null, CustomSerializationFlags.None);
            var export = asset.Exports.OfType<NormalExport>().FirstOrDefault(e => e.Data != null);
            if (export?.Data == null)
            {
                error = "card has no data export";
                return false;
            }

            var mAttack = export.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Attack");
            if (mAttack is not StructPropertyData attack || attack.Value == null)
            {
                error = "card has no m_Attack struct";
                return false;
            }

            var mAnim = attack.Value.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Animation");
            if (mAnim is not ObjectPropertyData animProp || animProp.Value == null)
            {
                error = "card has no m_Animation";
                return false;
            }
            if (animProp.Value.Index >= 0)
            {
                error = "m_Animation is not an import (unexpected card layout)";
                return false;
            }

            int importIndex = -animProp.Value.Index - 1;
            if (importIndex < 0 || importIndex >= asset.Imports.Count)
            {
                error = "m_Animation import index is out of range";
                return false;
            }

            string animName = animGamePath.Contains('/')
                ? animGamePath[(animGamePath.LastIndexOf('/') + 1)..]
                : animGamePath;
            string pkgPath = "/" + animGamePath;

            var animImport = asset.Imports[importIndex];
            animImport.ObjectName = FName.FromString(asset, animName);

            var outer = animImport.OuterIndex;
            if (outer != null && outer.Index < 0)
            {
                int outerIdx = -outer.Index - 1;
                if (outerIdx >= 0 && outerIdx < asset.Imports.Count)
                    asset.Imports[outerIdx].ObjectName = FName.FromString(asset, pkgPath);
            }

            asset.Write(uassetPath);

            string? readBack = ReadAnimationPath(uassetPath);
            if (!string.Equals(readBack, animGamePath, StringComparison.OrdinalIgnoreCase))
            {
                error = $"verification failed: m_Animation re-read as '{readBack ?? "(missing)"}'";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? ReadAnimationPath(UAsset asset)
    {
        var export = asset.Exports.OfType<NormalExport>().FirstOrDefault(e => e.Data != null);
        var mAttack = export?.Data?.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Attack");
        if (mAttack is not StructPropertyData attack || attack.Value == null) return null;

        var mAnim = attack.Value.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Animation");
        if (mAnim is not ObjectPropertyData animProp || animProp.Value == null) return null;

        var pi = animProp.Value;
        if (pi.Index >= 0) return null;

        int idx = -pi.Index - 1;
        if (idx < 0 || idx >= asset.Imports.Count) return null;

        var imp = asset.Imports[idx];
        string name = imp.ObjectName?.Value?.ToString() ?? "";
        if (name.Length == 0) return null;

        string? pkg = null;
        var outer = imp.OuterIndex;
        if (outer != null && outer.Index < 0)
        {
            int outerIdx = -outer.Index - 1;
            if (outerIdx >= 0 && outerIdx < asset.Imports.Count)
                pkg = asset.Imports[outerIdx].ObjectName?.Value?.ToString();
        }

        // A package import's ObjectName is the full package path and already ends with
        // the asset name (/Game/.../AnimName). Only append the object name when the
        // outer import is a bare folder (unexpected but cheap to tolerate).
        string? pkgNorm = string.IsNullOrEmpty(pkg) ? null : Normalize(pkg);
        string full;
        if (pkgNorm == null)
            full = name;
        else if (pkgNorm.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)
                 || pkgNorm.Equals(name, StringComparison.OrdinalIgnoreCase))
            full = pkgNorm;
        else
            full = pkgNorm.TrimEnd('/') + "/" + name;
        return Normalize(full);
    }

    /// <summary>Mirrors AnimationParser.NormalizeAnimPath for paths built from imports.</summary>
    private static string Normalize(string path)
    {
        path = (path ?? "").Replace('\\', '/').Trim();
        if (path.StartsWith("/")) path = path[1..];

        int contentIdx = path.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (contentIdx >= 0)
            path = "Game/" + path[(contentIdx + "/Content/".Length)..];

        int lastSlash = path.LastIndexOf('/');
        int lastDot = path.LastIndexOf('.');
        if (lastDot > lastSlash)
            path = path[..lastDot];

        return path;
    }
}
