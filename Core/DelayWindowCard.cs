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
/// Reads the MC_Attacks_Alt window (the m_AvailabilityLayerWindows entry whose m_Layer
/// import resolves to MC_Attacks_Alt) from an attack DB card. The node info panel uses
/// <see cref="Read"/> to show the vanilla delay window and to decide whether the delay
/// window inputs apply; export uses the same finders and bounds helpers through
/// ExportDialog, so the gate can never drift between UI and export.
/// </summary>
public static class DelayWindowCard
{
    public const string AltLayerName = "MC_Attacks_Alt";

    /// <summary>Attack cards run at 60 fps; windows are stored in frames, the panel edits seconds.</summary>
    public const double FramesPerSecond = 60.0;

    public static int SecondsToFrames(double seconds)
        => (int)Math.Round(seconds * FramesPerSecond, MidpointRounding.AwayFromZero);

    public static double FramesToSeconds(double frames)
        => frames / FramesPerSecond;

    private static readonly Dictionary<string, (long Ticks, (bool Has, float Lo, float Hi) Values)>
        Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Vanilla MC_Attacks_Alt window of the given original card, in frames.
    /// Has = false when the file is missing/unreadable or the card has no such window.
    /// </summary>
    public static (bool Has, float Lo, float Hi) Read(string contentRoot, string dbGamePath)
    {
        try
        {
            var path = AttackDbCard.ResolveUassetPath(contentRoot, dbGamePath);
            if (path == null || !File.Exists(path)) return default;
            long ticks = File.GetLastWriteTimeUtc(path).Ticks;
            if (Cache.TryGetValue(path, out var hit) && hit.Ticks == ticks) return hit.Values;
            var values = ReadCore(path);
            Cache[path] = (ticks, values);
            return values;
        }
        catch
        {
            return default;
        }
    }

    private static (bool Has, float Lo, float Hi) ReadCore(string uassetPath)
    {
        var asset = new UAsset(uassetPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
        var export = asset.Exports.OfType<NormalExport>().FirstOrDefault();
        if (export?.Data == null) return default;
        var windows = FindWindowArray(export.Data);
        if (windows?.Value == null) return default;
        foreach (var w in windows.Value.OfType<StructPropertyData>())
        {
            if (!string.Equals(WindowLayerName(asset, w), AltLayerName, StringComparison.Ordinal)) continue;
            var bounds = GetWindowBounds(w);
            return bounds.HasValue ? (true, bounds.Value.Lo, bounds.Value.Hi) : (true, 0f, 0f);
        }
        return default;
    }

    public static ArrayPropertyData? FindWindowArray(List<PropertyData> props)
    {
        if (props == null) return null;
        foreach (var p in props)
        {
            var hit = FindWindowArrayIn(p);
            if (hit != null) return hit;
        }
        return null;
    }

    public static ArrayPropertyData? FindWindowArrayIn(PropertyData p)
    {
        if (p is ArrayPropertyData ap && string.Equals(p.Name?.Value?.ToString(), "m_AvailabilityLayerWindows", StringComparison.Ordinal))
            return ap;
        if (p is StructPropertyData sp && sp.Value != null)
        {
            foreach (var c in sp.Value)
            {
                var h = FindWindowArrayIn(c);
                if (h != null) return h;
            }
        }
        else if (p is ArrayPropertyData arr && arr.Value != null)
        {
            foreach (var c in arr.Value)
            {
                var h = FindWindowArrayIn(c);
                if (h != null) return h;
            }
        }
        return null;
    }

    public static string WindowLayerName(UAsset asset, StructPropertyData window)
    {
        var layer = window.Value?.OfType<ObjectPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Layer");
        if (layer?.Value == null || layer.Value.Index >= 0) return "";
        int idx = -(layer.Value.Index + 1);
        if (idx < 0 || idx >= asset.Imports.Count) return "";
        return asset.Imports[idx].ObjectName?.Value?.ToString() ?? "";
    }

    /// <summary>(LowerBound, UpperBound) float values of a window's m_FrameRange, if shaped as expected.</summary>
    public static (float Lo, float Hi)? GetWindowBounds(StructPropertyData window)
    {
        var range = FindBoundsProps(window);
        if (range == null) return null;
        var lo = FindBoundFloat(range, "LowerBound");
        var hi = FindBoundFloat(range, "UpperBound");
        if (lo == null || hi == null) return null;
        return (lo.Value, hi.Value);
    }

    /// <summary>Overwrites a window's frame bounds in place. False when the struct shape is unexpected.</summary>
    public static bool TrySetWindowBounds(StructPropertyData window, float lo, float hi)
    {
        var range = FindBoundsProps(window);
        if (range == null) return false;
        var loF = FindBoundFloat(range, "LowerBound");
        var hiF = FindBoundFloat(range, "UpperBound");
        if (loF == null || hiF == null) return false;
        loF.Value = lo;
        hiF.Value = hi;
        return true;
    }

    private static List<PropertyData>? FindBoundsProps(StructPropertyData window)
    {
        // Shape: window -> m_FrameRange -> m_Anim -> LowerBound/UpperBound -> Value.
        var range = window.Value?.OfType<StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == "m_FrameRange")?.Value;
        return range?.OfType<StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == "m_Anim")?.Value;
    }

    private static FloatPropertyData? FindBoundFloat(List<PropertyData>? rangeProps, string boundName)
    {
        if (rangeProps == null) return null;
        var bound = rangeProps.OfType<StructPropertyData>()
            .FirstOrDefault(s => s.Name?.Value?.ToString() == boundName);
        return bound?.Value?.OfType<FloatPropertyData>()
            .FirstOrDefault(f => f.Name?.Value?.ToString() == "Value");
    }
}
