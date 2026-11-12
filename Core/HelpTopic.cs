using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SifuMovesetEditor;

/// <summary>
/// One numbered step in a help guide. The number is assigned when the topic is built,
/// so the XAML side only has to render a two-column row.
/// </summary>
public sealed class HelpStep
{
    public string Number { get; init; } = "";
    public string Text { get; init; } = "";
}

/// <summary>
/// A single feature guide shown in the Help window. Steps are stored as plain strings
/// and numbered here; <see cref="SearchText"/> flattens every searchable field so the
/// dialog's search box can do simple case-insensitive matching without a tokenizer.
/// </summary>
public sealed class HelpTopic
{
    public string Id { get; }
    public string Category { get; }
    public string Title { get; }
    public string Summary { get; }
    public string WhereToFind { get; }
    public IReadOnlyList<HelpStep> Steps { get; }
    public IReadOnlyList<string> Tips { get; }
    public IReadOnlyList<string> Keywords { get; }
    /// <summary>Non-null when an interactive walkthrough will be attached later.</summary>
    public string? TutorialId { get; }
    public string SearchText { get; }

    public bool HasTips => Tips.Count > 0;
    public bool HasWhereToFind => !string.IsNullOrWhiteSpace(WhereToFind);

    public HelpTopic(string id, string category, string title, string summary,
        string whereToFind, IReadOnlyList<string> steps,
        IReadOnlyList<string>? tips = null, IReadOnlyList<string>? keywords = null,
        string? tutorialId = null)
    {
        Id = id;
        Category = category;
        Title = title;
        Summary = summary;
        WhereToFind = whereToFind ?? "";
        Steps = (steps ?? Array.Empty<string>())
            .Select((text, i) => new HelpStep
            {
                Number = (i + 1).ToString(CultureInfo.InvariantCulture),
                Text = text
            })
            .ToArray();
        Tips = tips ?? Array.Empty<string>();
        Keywords = keywords ?? Array.Empty<string>();
        TutorialId = tutorialId;

        var searchParts = new List<string> { Id, Category, Title, Summary, WhereToFind };
        searchParts.AddRange(Steps.Select(s => s.Text));
        searchParts.AddRange(Tips);
        searchParts.AddRange(Keywords);
        SearchText = string.Join("\n", searchParts);
    }

    public bool Matches(string term)
        => !string.IsNullOrEmpty(term)
           && SearchText.Contains(term, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One sidebar category with its guides in authoring order.</summary>
public sealed class HelpCategory
{
    public string Name { get; }
    public IReadOnlyList<HelpTopic> Topics { get; }

    public HelpCategory(string name, IReadOnlyList<HelpTopic> topics)
    {
        Name = name;
        Topics = topics;
    }
}
