using System;
using System.Collections.Generic;

namespace SifuMovesetEditor;

/// <summary>
/// How a tutorial step moves on. <see cref="Continue"/> steps show Next. <see cref="Event"/>
/// steps are required actions: Next is hidden and the step only finishes when the named
/// editor event fires. Esc still leaves the walkthrough.
/// </summary>
public enum TutorialAdvance
{
    Continue,
    Event
}

/// <summary>Runtime work the runner does before a step is shown.</summary>
public enum TutorialFocus
{
    None,

    /// <summary>
    /// Pick a random vanilla AttackDB card, expand its library group, scroll it into view and
    /// spotlight it so the user has something concrete to drag.
    /// </summary>
    RandomAttackCard
}

/// <summary>Extra condition an action step's event has to satisfy before it advances.</summary>
public enum TutorialRequirement
{
    None,

    /// <summary>The drop must land on a node that is still vanilla (blue/yellow), not an edited one.</summary>
    VanillaNode
}

/// <summary>
/// Extra state a scenario needs before it can run. Kept as an enum (not a delegate) so the
/// scripts stay data-only and the smoke test can validate them.
/// </summary>
public enum TutorialSetup
{
    None,
    /// <summary>
    /// Find a vanilla AttackDB node whose transition struct has a real multi-row chance
    /// pool and spotlight it. Used by the Conditions walkthrough (Grunt base) so the dialog
    /// shows an actual weighted split rather than a single row fanning out to many targets.
    /// </summary>
    MixupNode
}

/// <summary>One bubble in a walkthrough. Numbering and navigation are handled by the runner.</summary>
public sealed class TutorialStep
{
    public string Title { get; }
    public string Body { get; }

    /// <summary>Key from <see cref="TutorialAnchors"/>, or null for a centred bubble.</summary>
    public string? Anchor { get; }

    /// <summary>Editor event id from <see cref="TutorialEvents"/>; only set for Event steps.</summary>
    public string? EventId { get; }

    public TutorialAdvance Advance { get; }

    /// <summary>Optional runtime preparation (for example pick a card to spotlight).</summary>
    public TutorialFocus Focus { get; }

    /// <summary>Extra check applied to the event that completes this step.</summary>
    public TutorialRequirement Requirement { get; }

    /// <summary>Optional "try this" line. Action steps get a default hint when none is given.</summary>
    public string? Hint { get; }

    public TutorialStep(string title, string body, string? anchor = null,
        string? eventId = null, string? hint = null,
        TutorialFocus focus = TutorialFocus.None,
        TutorialRequirement requirement = TutorialRequirement.None)
    {
        Title = title;
        Body = body;
        Anchor = anchor;
        EventId = eventId;
        Advance = eventId == null ? TutorialAdvance.Continue : TutorialAdvance.Event;
        Hint = hint;
        Focus = focus;
        Requirement = requirement;

        var defaultHint = Advance == TutorialAdvance.Event
            ? "This step finishes when you do it — Esc leaves the walkthrough."
            : null;
        EffectiveHint = Hint ?? defaultHint;
    }

    public string? EffectiveHint { get; }

    public bool HasHint => !string.IsNullOrWhiteSpace(EffectiveHint);

    /// <summary>True when the step hides Next and waits for a real editor action.</summary>
    public bool IsRequiredAction => Advance == TutorialAdvance.Event;

    public static TutorialStep Do(string title, string body, string anchor,
        string eventId, string? hint = null,
        TutorialFocus focus = TutorialFocus.None,
        TutorialRequirement requirement = TutorialRequirement.None)
        => new(title, body, anchor, eventId, hint, focus, requirement);

    public static TutorialStep Say(string title, string body, string? anchor = null,
        string? hint = null)
        => new(title, body, anchor, null, hint);
}

/// <summary>
/// A guided walkthrough launched from a help article. <see cref="Id"/> matches
/// <see cref="HelpTopic.TutorialId"/>; a guide without a matching scenario simply has no
/// launch button.
/// </summary>
public sealed class TutorialScenario
{
    public string Id { get; }
    public string Title { get; }
    public string? Summary { get; }

    /// <summary>Archetype the walkthrough starts on, for example MainChar or Grunt.</summary>
    public string EntryStance { get; }

    /// <summary>Optional variant tag, for example Grunt_Base. Null keeps the default.</summary>
    public string? EntryVariant { get; }

    public TutorialSetup Setup { get; }
    public IReadOnlyList<TutorialStep> Steps { get; }

    public TutorialScenario(string id, string title, string entryStance,
        IReadOnlyList<TutorialStep> steps, string? summary = null,
        string? entryVariant = null, TutorialSetup setup = TutorialSetup.None)
    {
        Id = id;
        Title = title;
        EntryStance = entryStance;
        EntryVariant = entryVariant;
        Setup = setup;
        Steps = steps ?? Array.Empty<TutorialStep>();
        Summary = summary;
    }

    public int StepCount => Steps.Count;
}
