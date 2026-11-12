using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace SifuMovesetEditor;

/// <summary>
/// The interactive walkthroughs. Data only: MainWindow owns the sandbox, the overlay and
/// the event hooks, so the scripts can be validated by a smoke test without game content.
/// Not every guide has one - topics without a matching <see cref="TutorialScenario.Id"/>
/// simply don't show a launch button.
///
/// "Do" steps are required actions: the Next button is hidden and the step only finishes
/// when the real editor event fires. "Say" steps explain and move on with Next.
/// </summary>
public static class TutorialContent
{
    public static IReadOnlyList<TutorialScenario> Scenarios { get; } = BuildScenarios();

    public static TutorialScenario? Find(string? id)
        => id == null
            ? null
            : Scenarios.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    private static List<TutorialScenario> BuildScenarios() => new()
    {
        new TutorialScenario(
            "replace-move",
            "Replace a move",
            "MainChar",
            new[]
            {
                TutorialStep.Say(
                    "This is the Move Library",
                    "Every card here is a real move. Vanilla Attacks holds the game's own attacks, "
                    + "the Animations tab lists every animation (including ones without a card), and "
                    + "Custom Moves holds the cards you clone or create.",
                    TutorialAnchors.Library,
                    "Search the list at the top if you already know the name."),

                TutorialStep.Do(
                    "Drag this card onto a blue node",
                    "We highlighted one vanilla attack card for you. Drag it onto any blue node on the "
                    + "combo graph. Blue means the node is still vanilla; dropping the card replaces that "
                    + "move. The rest of the editor is locked until you do it.",
                    TutorialAnchors.LibraryCard,
                    TutorialEvents.CardDropped,
                    "Drag the glowing card across to the graph. Any blue node works.",
                    TutorialFocus.RandomAttackCard,
                    TutorialRequirement.VanillaNode),

                TutorialStep.Say(
                    "That node is green now",
                    "Green means the slot plays your card instead of the vanilla move. Blue nodes are "
                    + "the ones you haven't touched yet.",
                    TutorialAnchors.LastTarget),

                TutorialStep.Do(
                    "Put the vanilla move back",
                    "Press Ctrl+Z. Undo belongs to the unit you're editing, so it always puts the move "
                    + "you just replaced back where it was.",
                    TutorialAnchors.LastTarget,
                    TutorialEvents.MoveUndone,
                    "Ctrl+Z — the node turns blue again."),

                TutorialStep.Say(
                    "Swap, undo, repeat",
                    "Dropping a different card on the same node swaps it again. After the walkthrough, "
                    + "double-click any node to preview it in the 3D viewer.",
                    TutorialAnchors.Graph)
            },
            summary: "Practise dragging a library card onto a combo node."),

        new TutorialScenario(
            "link-mainchar",
            "Link two MainChar moves",
            "MainChar",
            new[]
            {
                TutorialStep.Say(
                    "You're on MainChar",
                    "This is the player's barehand moveset. Every line between two moves is a transition "
                    + "the player can trigger with an input.",
                    TutorialAnchors.ChangeUnit,
                    "Use Change Unit whenever you want to edit a different character."),

                TutorialStep.Do(
                    "Start a link",
                    "Right-click the move you want to extend and choose Connect to. The editor waits "
                    + "until a link is actually started.",
                    TutorialAnchors.Graph,
                    TutorialEvents.LinkStarted,
                    "Right-click any move on the graph."),

                TutorialStep.Do(
                    "Click the follow-up, then pick an input",
                    "Click the move that should come after it. A popup then asks which input triggers the "
                    + "link — Light, Heavy, Rush, Dodge, and so on.",
                    TutorialAnchors.Graph,
                    TutorialEvents.LinkCreated,
                    "Enemy graphs only offer Default (None); MainChar graphs offer real player inputs."),

                TutorialStep.Say(
                    "One pool per input",
                    "Light 100% and Heavy 100% on the same move is valid — those are separate pools. Each "
                    + "input keeps its own total, and every total has to reach exactly 100%.",
                    TutorialAnchors.Graph)
            },
            summary: "Practise connecting two MainChar moves and choosing the input."),

        new TutorialScenario(
            "link-enemy",
            "Link two enemy moves",
            "Grunt",
            new[]
            {
                TutorialStep.Say(
                    "You're looking at an enemy",
                    "The walkthrough switched you to Grunt, base variant, with no weapon — one of the "
                    + "smallest enemy movesets in the game.",
                    TutorialAnchors.ChangeUnit,
                    "Change Unit also offers phases and weapon variants for other enemies."),

                TutorialStep.Do(
                    "Start a link",
                    "Right-click a move and choose Connect to.",
                    TutorialAnchors.Graph,
                    TutorialEvents.LinkStarted,
                    "Pick any move with a follow-up you want to change."),

                TutorialStep.Do(
                    "Click the follow-up, then pick Default (None)",
                    "Enemy links have no player input. The picker still appears so the flow matches "
                    + "MainChar, but Default (None) is the only real choice.",
                    TutorialAnchors.Graph,
                    TutorialEvents.LinkCreated,
                    "A player input here would be a lie — the AI never reads it."),

                TutorialStep.Say(
                    "The AI picks the branch",
                    "Enemy transitions are chosen by the AI, not by a button press. That's why the input "
                    + "is empty and why the weighting lives in the chance pool.",
                    TutorialAnchors.Graph),

                TutorialStep.Say(
                    "Steer the AI",
                    "Open Conditions & Chances on the source move to say how often each branch is picked, "
                    + "and to gate branches behind conditions.",
                    TutorialAnchors.Graph)
            },
            summary: "Practise linking enemy moves, where every link is AI-gated.",
            entryVariant: "Grunt_Base"),

        new TutorialScenario(
            "conditions",
            "Conditions & Chances",
            "Grunt",
            new[]
            {
                TutorialStep.Say(
                    "Here's a move with two branches",
                    "This walkthrough switched you to Grunt, base variant. We picked a move that already "
                    + "has two weighted follow-ups, so the chance pool has something real to show. The AI "
                    + "rolls these pools — there is no button press behind them.",
                    TutorialAnchors.LastTarget,
                    "Enemy movesets are smaller than MainChar's, so each pool is easier to read."),

                TutorialStep.Do(
                    "Open Conditions & Chances",
                    "Right-click the spotlighted move and choose Conditions & Chances. Read the header, "
                    + "then close the dialog to continue. The walkthrough waits for you to close it.",
                    TutorialAnchors.LastTarget,
                    TutorialEvents.ChancesClosed,
                    "The walkthrough hides while the dialog is open and comes back when you close it."),

                TutorialStep.Say(
                    "Each row is a branch the AI can take",
                    "A row's probability is how often the AI picks that follow-up. The spotlighted move "
                    + "splits its pool 30/70, so one route is the common one and the other is occasional. "
                    + "Automatic rows share whatever the typed rows leave.",
                    TutorialAnchors.LastTarget),

                TutorialStep.Say(
                    "Some rows are condition-gated",
                    "A row can carry a condition, for example target knocked down or a behaviour check. "
                    + "The AI only considers that route when the condition passes — the probability then "
                    + "applies inside the matching cases.",
                    TutorialAnchors.LastTarget),

                TutorialStep.Say(
                    "Even split and Σ→100%",
                    "Even split spreads the pool evenly across the rows sharing it. Σ→100% scales the typed "
                    + "values proportionally so the total lands exactly on 100%. If a link or delete leaves "
                    + "a pool off 100%, the editor opens this dialog instead of silently rewriting your numbers.",
                    TutorialAnchors.Graph)
            },
            summary: "Practise reading an enemy chance pool and rebalancing it.",
            entryVariant: "Grunt_Base",
            setup: TutorialSetup.MixupNode)
    };
}
