using System;
using System.Collections.Generic;
using System.IO;

namespace SifuMovesetEditor;

/// <summary>
/// Cheap "what does this attack card carry?" probe. Reads only the package header's name
/// table (no UAsset object model), so ranking a whole card library and warning on a drop
/// stay instant. A package with no m_HitBoxes tag has zero hit windows - no hit rows and no
/// aim target - which is why a slot swapped onto such a card cannot aim a throw.
/// </summary>
public static class CardHitProbe
{
    public readonly struct Info
    {
        /// <summary>The package was found and its name table read.</summary>
        public bool Found { get; init; }
        /// <summary>The card has an m_HitBoxes property (hit windows - and aim - live there).</summary>
        public bool HasHitWindows { get; init; }
        /// <summary>At least one window carries m_eAimTarget.</summary>
        public bool HasAimTarget { get; init; }
        public string UassetPath { get; init; }
    }

    private static readonly Dictionary<string, (long Ticks, Info Info)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves the card in CustomAssets first, then the vanilla content tree.</summary>
    public static Info Probe(string gamePath, string? vanillaContentRoot)
    {
        try
        {
            var rel = AttackDbCard.GamePathToContentRel(gamePath);
            if (string.IsNullOrEmpty(rel)) return default;

            string file = CustomAssets.FileForContentRel(rel + ".uasset");
            if (!File.Exists(file))
                file = AttackDbCard.ResolveUassetPath(vanillaContentRoot ?? "", gamePath) ?? "";
            if (file.Length == 0 || !File.Exists(file)) return default;

            long ticks = File.GetLastWriteTimeUtc(file).Ticks;
            if (Cache.TryGetValue(file, out var hit) && hit.Ticks == ticks) return hit.Info;

            var names = AttackDbCard.ReadNameTable(file);
            var info = new Info
            {
                Found = names != null,
                HasHitWindows = names?.Contains("m_HitBoxes") == true,
                HasAimTarget = names?.Contains("m_eAimTarget") == true,
                UassetPath = file
            };
            Cache[file] = (ticks, info);
            return info;
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// False only when the package was read and provably has no hit-window property;
    /// null when the card file could not be found/read (unknown stays unknown).
    /// </summary>
    public static bool? HasHitWindows(string gamePath, string? vanillaContentRoot)
    {
        var info = Probe(gamePath, vanillaContentRoot);
        return info.Found ? info.HasHitWindows : null;
    }
}
