namespace SifuMovesetEditor;

/// <summary>
/// What a dropped AttackDB card does to a node's label. Authored nodes are the card, so
/// the drop rewrites their identity (graph label, inspector, exported m_Name). Vanilla
/// and import-owned nodes keep their slot name - export patches the existing slot, so
/// the swap is recorded separately as <see cref="ComboNode.ImportedDisplayName"/>.
/// </summary>
public static class NodeCardIdentity
{
    /// <summary>The name the node had before the drop, for status messages.</summary>
    public static string LabelOf(ComboNode node)
        => !string.IsNullOrEmpty(node.DisplayName) ? node.DisplayName
            : !string.IsNullOrEmpty(node.Name) ? node.Name
            : $"#{node.Id}";

    /// <summary>
    /// Applies a card drop's naming rule in place. Returns true when the node adopted the
    /// card as its identity (authored nodes), false when it kept its slot label.
    /// </summary>
    public static bool ApplyDropName(ComboNode node, string? newName)
    {
        if (node.Kind == ComboNodeKind.UserCreated)
        {
            if (string.IsNullOrEmpty(newName)) return false;
            node.DisplayName = newName;
            node.ImportedDisplayName = "";
            return true;
        }

        var vanillaPath = !string.IsNullOrEmpty(node.VanillaAnimPath)
            ? node.VanillaAnimPath
            : node.DefaultAnimPath;
        if (!string.IsNullOrEmpty(vanillaPath) &&
            !string.Equals(node.AnimPath, vanillaPath, System.StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(newName))
            node.ImportedDisplayName = newName;
        else
            node.ImportedDisplayName = "";
        return false;
    }
}
