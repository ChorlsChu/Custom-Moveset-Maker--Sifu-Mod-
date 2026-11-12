using System;

namespace SifuMovesetEditor;

/// <summary>Which side a combo graph belongs to, from the tree's own identity only.</summary>
public enum GraphSide
{
    /// <summary>No stable signal (old save with no SourceGamePath and no player tag).</summary>
    Unknown,
    MainChar,
    Enemy
}

/// <summary>
/// Graph-level identity: the combo tree's own source path and weapon tag. Never the
/// cards sitting on the canvas - a MainChar card dropped onto an enemy graph (or an AI
/// attack card dropped onto an MC graph) must not flip the graph's identity. That
/// node-content sniffing used to switch the link picker to the wrong inputs and made
/// the unit cache skip MainChar saves as soon as a library move was added.
///
/// Order matters: an AI path wins over a player-sounding weapon tag, because some
/// enemy trees report "BareHands" as their weapon type.
/// </summary>
public static class GraphIdentity
{
    public static GraphSide Classify(ComboGraph? graph)
    {
        if (graph == null) return GraphSide.Unknown;
        var path = (graph.SourceGamePath ?? "").Replace('\\', '/');
        if (path.Contains("DB/AI/Archetypes", StringComparison.OrdinalIgnoreCase))
            return GraphSide.Enemy;
        if (path.Contains("DB/_MainChar", StringComparison.OrdinalIgnoreCase))
            return GraphSide.MainChar;

        // Old saves carry no SourceGamePath yet: the player weapon/stance tags are the
        // next-stable signal (they come from the combo file name the graph was loaded
        // from, not from the nodes).
        var w = graph.WeaponName ?? "";
        if (string.Equals(w, "MainChar", StringComparison.OrdinalIgnoreCase)
            || string.Equals(w, "BareHands", StringComparison.OrdinalIgnoreCase)
            || w.StartsWith("MainChar_", StringComparison.OrdinalIgnoreCase))
            return GraphSide.MainChar;
        return GraphSide.Unknown;
    }

    /// <summary>True when the graph is a player combo tree. Unknown stays false so an
    /// identity-less enemy graph keeps its AI ticket/input wording.</summary>
    public static bool IsMainChar(ComboGraph? graph) => Classify(graph) == GraphSide.MainChar;
}

/// <summary>
/// Whether the graph currently on the canvas may be snapshotted into the active unit's
/// cache. Kept out of MainWindow so the decision is headless-testable.
/// </summary>
public static class UnitCachePolicy
{
    /// <summary>
    /// Skips only on a genuine identity/key conflict (an enemy tree under a MainChar
    /// key, or a player tree under an enemy key). Unknown identity falls back to the
    /// active key: the key comes from the unit picker, so it is the reliable signal,
    /// and refusing to save there is how edits used to disappear.
    /// </summary>
    public static bool ShouldCache(string? arch, ComboGraph? graph, out string reason)
    {
        bool keyIsMainChar = string.Equals(arch, "MainChar", StringComparison.OrdinalIgnoreCase);
        var side = GraphIdentity.Classify(graph);
        bool hasPath = !string.IsNullOrEmpty(graph?.SourceGamePath);
        if (!keyIsMainChar && !hasPath)
        {
            // The weapon tag is the old-save fallback for *player* trees. Under an enemy
            // key with no SourceGamePath the tag cannot prove the graph is a player tree,
            // so treat it as unknown rather than a conflict - refusing to save is exactly
            // how edits used to disappear.
            side = GraphSide.Unknown;
        }
        string path = string.IsNullOrEmpty(graph?.SourceGamePath) ? "(no SourceGamePath)" : graph!.SourceGamePath;

        if (keyIsMainChar && side == GraphSide.Enemy)
        {
            reason = $"MainChar key but graph identity is an enemy tree ({path})";
            return false;
        }
        if (!keyIsMainChar && side == GraphSide.MainChar)
        {
            reason = $"enemy key '{arch}' but graph identity is a MainChar tree ({path})";
            return false;
        }
        reason = side == GraphSide.Unknown
            ? $"identity unknown ({path}) - cached under the active key"
            : $"identity {side} matches the active key";
        return true;
    }
}
