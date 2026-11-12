using System;
using System.Collections.Generic;
using System.Linq;

namespace SifuMovesetEditor;

/// <summary>
/// Orders the library paths a double-clicked combo node searches for its card.
///
/// Authored (drag-dropped) nodes are created with an empty <see cref="ComboNode.DefaultAnimPath"/>,
/// so a naive "the drop's own identity first" rule always matches the animation file and
/// never reaches the node's attack card. Those nodes put their card paths first; parsed
/// vanilla / imported / swapped nodes keep the replacement-aware order (a swapped node must
/// land on the animation or card that replaced it, never the stale vanilla card).
/// </summary>
public static class LibraryCardLocator
{
    /// <summary>
    /// Search order for <see cref="MainWindow.FindCardInLibrary"/>. Empty and duplicate
    /// paths are dropped (case-insensitive), so the caller can try each entry in order and
    /// stop at the first one the library contains.
    /// </summary>
    /// <param name="node">Node being double-clicked, or null when searching by path only.</param>
    /// <param name="animPath">The node's animation path (also the caller's fallback path).</param>
    /// <param name="mappedDb">Anim→DB mapping for <paramref name="animPath"/>, when known.</param>
    public static List<string> SearchPaths(ComboNode? node, string animPath, string? mappedDb)
    {
        var paths = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            path = path.Trim();
            if (!paths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                paths.Add(path);
        }

        Add(node?.ReplacedAttackCardRel);

        // A user-created node records its card in SourceDBPath/DefaultDBPath while AnimPath
        // is only the animation that card plays. DefaultAnimPath is empty on every authored
        // node, so the drop-identity step below would always win - card paths go first.
        bool authored = node != null && node.Kind == ComboNodeKind.UserCreated;
        if (authored)
        {
            Add(AttackDbCard.EffectiveCardPath(node));
            Add(node!.DefaultDBPath);
            Add(mappedDb);
        }

        if (node != null
            && !string.Equals(node.AnimPath, node.DefaultAnimPath, StringComparison.OrdinalIgnoreCase))
            Add(node.AnimPath);

        if (!authored)
        {
            Add(AttackDbCard.EffectiveCardPath(node));
            Add(mappedDb);
            Add(node?.DefaultDBPath);
        }

        Add(animPath);
        return paths;
    }
}
