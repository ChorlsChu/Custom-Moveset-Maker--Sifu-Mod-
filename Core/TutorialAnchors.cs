using System;
using System.Collections.Generic;
using System.Linq;

namespace SifuMovesetEditor;

/// <summary>
/// Named UI regions a tutorial bubble can point at. The scripts stay data-only, so a typo
/// can't silently point at nothing: the smoke test requires every anchor to appear in
/// <see cref="All"/>, and MainWindow maps each key to a live element.
/// </summary>
public static class TutorialAnchors
{
    public const string Library = "library";
    public const string LibraryTabs = "library-tabs";
    public const string Search = "search";
    public const string Graph = "graph";
    public const string Toolbar = "toolbar";
    public const string ChangeUnit = "change-unit";
    public const string StartEntries = "start-entries";

    /// <summary>
    /// Resolved at render time to whatever the walkthrough just focused (a dropped node, a
    /// mixup branch). Falls back to the graph when nothing has been focused yet.
    /// </summary>
    public const string LastTarget = "last-target";

    /// <summary>
    /// Library and graph together: the dim hole spans both so a card can be dragged from one
    /// to the other without the overlay swallowing the drop.
    /// </summary>
    public const string LibraryAndGraph = "library-and-graph";

    /// <summary>
    /// The specific card the runner picked for a drop step, plus the graph. Only the card is
    /// ringed; both remain usable so the drag has a source and a drop target.
    /// </summary>
    public const string LibraryCard = "library-card";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Library, LibraryTabs, Search, Graph, Toolbar, ChangeUnit, StartEntries,
        LastTarget, LibraryAndGraph, LibraryCard
    };

    public static bool IsKnown(string? key)
        => key == null || All.Contains(key, StringComparer.Ordinal);
}

/// <summary>Editor events that can auto-advance a <see cref="TutorialAdvance.Event"/> step.</summary>
public static class TutorialEvents
{
    public const string CardDropped = "card-dropped";
    public const string LinkStarted = "link-started";
    public const string LinkCreated = "link-created";
    public const string ChancesClosed = "chances-closed";
    public const string MoveUndone = "move-undone";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        CardDropped, LinkStarted, LinkCreated, ChancesClosed, MoveUndone
    };
}
