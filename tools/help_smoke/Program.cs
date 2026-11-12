using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SifuMovesetEditor;

namespace help_smoke;

/// <summary>
/// Content checks for the Help feature guide. The window itself is pure presentation;
/// these tests keep the data honest: unique ids/titles, complete steps, known
/// categories, working search and tutorial hooks that resolve. No game content needed.
/// Exit code 0 = all passed.
/// </summary>
internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--screenshot")
            return RenderScreenshot(args[1], args.Length >= 3 ? args[2] : null);
        if (args.Length >= 2 && args[0] == "--tutorial-screenshot")
            return RenderTutorialScreenshot(args[1], args.Length >= 3 ? args[2] : "replace-move");

        Console.WriteLine("Help content smoke tests");

        // Construct the real window once so XAML resource lookups, sidebar building and
        // article rendering are exercised without needing an interactive session.
        try
        {
            _ = new Application();
            var dlg = new HelpDialog();
            Check(dlg.Title == "Help & Tutorials", "HelpDialog constructs from compiled XAML");
            Check(dlg.Content != null, "HelpDialog has rendered content");
        }
        catch (Exception ex)
        {
            Check(false, $"HelpDialog constructs without throwing: {ex.GetType().Name}: {ex.Message}");
        }

        var topics = HelpContent.Topics;
        Check(topics.Count > 0, "help content has topics");
        Check(topics.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() == topics.Count,
            "topic ids are unique");
        Check(topics.Select(t => t.Title).Distinct(StringComparer.Ordinal).Count() == topics.Count,
            "topic titles are unique");

        foreach (var topic in topics)
        {
            Check(!string.IsNullOrWhiteSpace(topic.Title), $"{topic.Id}: title is set");
            Check(!string.IsNullOrWhiteSpace(topic.Summary), $"{topic.Id}: summary is set");
            Check(topic.Steps.Count >= 2, $"{topic.Id}: has at least 2 steps");
            Check(topic.Steps.Select((s, i) => s.Number == (i + 1).ToString()).All(ok => ok),
                $"{topic.Id}: steps are numbered in order");
            Check(topic.Steps.All(s => !string.IsNullOrWhiteSpace(s.Text)), $"{topic.Id}: no empty step text");
            Check(topic.HasWhereToFind, $"{topic.Id}: has a 'where to find it' line");
            Check(topic.Keywords.Count > 0, $"{topic.Id}: has search keywords");
        }

        // Every topic must belong to a rendered category, in the declared order.
        var categories = HelpContent.Categories;
        Check(categories.Count > 0, "categories are built");
        Check(categories[0].Name == "Getting Started", "sidebar starts with Getting Started");
        var grouped = categories.SelectMany(c => c.Topics).ToList();
        Check(grouped.Count == topics.Count, "every topic appears in exactly one category");
        Check(grouped.All(t => topics.Contains(t)), "category topics come from the master list");
        foreach (var category in categories)
        {
            Check(category.Topics.Count > 0, $"category {category.Name}: is not empty");
            Check(category.Topics.All(t => t.Category == category.Name),
                $"category {category.Name}: topics match their group");
        }

        // Search: single terms, multi-term AND, case-insensitivity, empty and no-match.
        Check(HelpContent.Search(null).Count == topics.Count, "empty search returns every topic");
        Check(HelpContent.Search("").Count == topics.Count, "blank search returns every topic");
        Check(Find("replace-move", "drag"), "search 'drag' finds the replace-move guide");
        Check(Find("enemy-combos", "enemy"), "search 'enemy' finds the enemy linking guide");
        Check(Find("mainchar-combos", "mainchar input"), "multi-term search finds MainChar links");
        Check(Find("post-link-delete", "DELETE"), "search is case-insensitive");
        Check(HelpContent.Search("zzz-no-such-term-zzz").Count == 0, "gibberish search returns nothing");

        // Tutorial hooks are present for the four planned walkthroughs and resolvable.
        var tutorialIds = topics.Where(t => t.TutorialId != null).Select(t => t.TutorialId!).ToList();
        Check(tutorialIds.Count == 4, $"four tutorial hooks are declared (found {tutorialIds.Count})");
        Check(tutorialIds.Distinct(StringComparer.Ordinal).Count() == tutorialIds.Count,
            "tutorial ids are unique");
        foreach (string tutorialId in new[] { "replace-move", "link-mainchar", "link-enemy", "conditions" })
        {
            Check(tutorialIds.Contains(tutorialId, StringComparer.Ordinal),
                $"tutorial hook '{tutorialId}' is attached to a topic");
        }

        // Lookup used by future launchers.
        Check(HelpContent.Find("replace-move") != null, "Find returns a known topic");
        Check(HelpContent.Find("nope") == null, "Find returns null for an unknown topic");

        // Copy changes for v1.4: first-run setup is covered by the setup prompt itself, and
        // the projects article is short enough without tips.
        Check(HelpContent.Find("first-launch") == null, "first launch guide is gone");
        Check(HelpContent.Find("projects") is { HasTips: false }, "projects guide has no tips");

        CheckTutorials(topics);
        CheckTutorialOverlay();

        Console.WriteLine(_failures == 0 ? "ALL HELP SMOKE TESTS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Scenario data checks: every tutorial topic has a scenario and vice versa, anchors and
    /// events are declared, and no step is empty. Catches a renamed anchor long before it
    /// turns into a spotlight pointing at nothing.
    /// </summary>
    private static void CheckTutorials(IReadOnlyList<HelpTopic> topics)
    {
        var scenarios = TutorialContent.Scenarios;
        Console.WriteLine("  -- tutorial scenarios --");

        Check(scenarios.Count == 4, $"four scenarios exist (found {scenarios.Count})");
        Check(scenarios.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() == scenarios.Count,
            "scenario ids are unique");

        var hookedTopics = topics.Where(t => t.TutorialId != null).ToList();
        foreach (var topic in hookedTopics)
        {
            var scenario = TutorialContent.Find(topic.TutorialId);
            Check(scenario != null, $"{topic.Id}: tutorial '{topic.TutorialId}' has a scenario");
            if (scenario != null)
                Check(string.Equals(scenario.Id, topic.TutorialId, StringComparison.Ordinal),
                    $"{topic.Id}: scenario id matches the hook");
        }

        foreach (var scenario in scenarios)
        {
            Check(hookedTopics.Any(t => t.TutorialId == scenario.Id),
                $"{scenario.Id}: is reachable from a help topic");
            Check(!string.IsNullOrWhiteSpace(scenario.Title), $"{scenario.Id}: title is set");
            Check(!string.IsNullOrWhiteSpace(scenario.EntryStance), $"{scenario.Id}: entry stance is set");
            Check(scenario.Steps.Count >= 3, $"{scenario.Id}: has at least 3 steps");

            foreach (var step in scenario.Steps)
            {
                Check(!string.IsNullOrWhiteSpace(step.Title), $"{scenario.Id}: step title is set");
                Check(!string.IsNullOrWhiteSpace(step.Body), $"{scenario.Id}: step body is set");
                Check(TutorialAnchors.IsKnown(step.Anchor),
                    $"{scenario.Id}: anchor '{step.Anchor}' is declared");
                if (step.Advance == TutorialAdvance.Event)
                {
                    Check(step.EventId != null && TutorialEvents.All.Contains(step.EventId, StringComparer.Ordinal),
                        $"{scenario.Id}: event '{step.EventId}' is declared");
                    // Required actions hide Next, so they must explain how to finish.
                    Check(step.IsRequiredAction, $"{scenario.Id}: '{step.Title}' is a required action");
                    Check(step.HasHint, $"{scenario.Id}: '{step.Title}' tells the user what to do");
                }
                else
                {
                    Check(step.EventId == null, $"{scenario.Id}: Continue steps carry no event");
                    Check(!step.IsRequiredAction, $"{scenario.Id}: '{step.Title}' keeps its Next button");
                }

                // The card spotlight only makes sense as a drag-onto-a-vanilla-node step.
                if (step.Focus == TutorialFocus.RandomAttackCard)
                {
                    Check(step.Anchor == TutorialAnchors.LibraryCard,
                        $"{scenario.Id}: card focus anchors on '{TutorialAnchors.LibraryCard}'");
                    Check(step.Requirement == TutorialRequirement.VanillaNode,
                        $"{scenario.Id}: card focus requires a vanilla drop target");
                    Check(step.EventId == TutorialEvents.CardDropped,
                        $"{scenario.Id}: card focus waits for '{TutorialEvents.CardDropped}'");
                }
                else
                {
                    Check(step.Anchor != TutorialAnchors.LibraryCard,
                        $"{scenario.Id}: '{step.Title}' has a card anchor but no card focus");
                }

                if (step.Requirement == TutorialRequirement.VanillaNode)
                    Check(step.Focus == TutorialFocus.RandomAttackCard,
                        $"{scenario.Id}: vanilla-node rule only applies to the card slide");
            }
        }

        // Conditions runs on Grunt base: the smallest enemy tree, with real weighted rows
        // (Hook 30/70) rather than a MainChar node chosen by edge count.
        var conditions = TutorialContent.Find("conditions");
        Check(conditions != null, "conditions scenario exists");
        if (conditions != null)
        {
            Check(string.Equals(conditions.EntryStance, "Grunt", StringComparison.Ordinal),
                "conditions runs on an enemy unit (Grunt)");
            Check(string.Equals(conditions.EntryVariant, "Grunt_Base", StringComparison.Ordinal),
                "conditions uses the Grunt base variant");
            Check(conditions.Setup == TutorialSetup.MixupNode,
                "conditions spotlights a node with a real chance pool");
            Check(conditions.Steps.Any(s => s.EventId == TutorialEvents.ChancesClosed),
                "conditions waits for the user to close Conditions & Chances");
        }
        var enemyLink = TutorialContent.Find("link-enemy");
        Check(enemyLink != null
              && string.Equals(enemyLink.EntryStance, "Grunt", StringComparison.Ordinal)
              && string.Equals(enemyLink.EntryVariant, "Grunt_Base", StringComparison.Ordinal),
            "enemy linking uses the same Grunt base sandbox as conditions");

        // The auto-advance hooks the runner relies on must stay wired to real steps.
        var allSteps = scenarios.SelectMany(s => s.Steps).ToList();
        foreach (string required in new[]
                 {
                     TutorialEvents.CardDropped, TutorialEvents.LinkStarted,
                     TutorialEvents.LinkCreated, TutorialEvents.ChancesClosed,
                     TutorialEvents.MoveUndone
                 })
        {
            Check(allSteps.Any(s => s.EventId == required), $"event '{required}' is used by a step");
        }

        // Every walkthrough that teaches a real action must contain at least one, otherwise
        // it has silently turned back into a slideshow.
        foreach (var scenario in scenarios)
        {
            Check(scenario.Steps.Any(s => s.IsRequiredAction),
                $"{scenario.Id}: has at least one step the user performs");
        }

        Check(TutorialContent.Find("nope") == null, "tutorial lookup returns null for an unknown id");
    }

    /// <summary>Constructs the real overlay and pins the bubble placement maths.</summary>
    private static void CheckTutorialOverlay()
    {
        Console.WriteLine("  -- tutorial overlay --");
        try
        {
            var overlay = new TutorialOverlay();
            Check(overlay.Content != null, "TutorialOverlay constructs from compiled XAML");
        }
        catch (Exception ex)
        {
            Check(false, $"TutorialOverlay constructs without throwing: {ex.GetType().Name}: {ex.Message}");
        }

        var viewport = new Size(1400, 800);
        var bubble = new Size(366, 180);

        var below = TutorialOverlay.PlaceBubble(new Rect(100, 50, 200, 100), bubble, viewport);
        Check(Math.Abs(below.Y - 164) < 0.1, "bubble sits below a high spotlight");
        Check(below.X >= 16 && below.X + below.Width <= viewport.Width - 16, "bubble stays inside the viewport");

        var above = TutorialOverlay.PlaceBubble(new Rect(100, 650, 200, 100), bubble, viewport);
        Check(above.Y < 650, "bubble flips above a low spotlight");
        Check(Math.Abs(above.Y - 456) < 0.1, "flipped bubble keeps the gap");

        var centred = TutorialOverlay.PlaceBubble(null, bubble, viewport);
        Check(Math.Abs(centred.X - (1400 - 366) / 2.0) < 0.1, "no spotlight centres the bubble");

        var narrow = TutorialOverlay.PlaceBubble(new Rect(10, 10, 280, 60), bubble, new Size(300, 600));
        Check(narrow.Width <= 300 - 32, "bubble width clamps to a narrow window");

        var wide = TutorialOverlay.PlaceBubble(new Rect(0, 0, 1400, 700), bubble, viewport);
        Check(wide.Y + wide.Height <= viewport.Height - 16 + 0.1,
            "a spotlight that fills the window still leaves the bubble on screen");

        var spot = TutorialOverlay.SpotlightRect(new Rect(0, 0, 100, 100), new Rect(0, 0, 1400, 800));
        Check(spot.X == 0 && spot.Y == 0, "spotlight clipping keeps the hole on screen");
        Check(Math.Abs(spot.Width - 106) < 0.1, "spotlight is inflated for the ring");
        Check(TutorialOverlay.SpotlightRect(null, new Rect(0, 0, 1400, 800)).IsEmpty,
            "no target means no hole");
    }

    private static bool Find(string id, string query)
        => HelpContent.Search(query).Any(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Developer helper: renders the window content to a PNG so layout can be reviewed
    /// without opening the app. Usage: help_smoke --screenshot &lt;path.png&gt; [topicId]
    /// </summary>
    private static int RenderScreenshot(string path, string? topicId)
    {
        try
        {
            _ = new Application();
            var dlg = new HelpDialog(topicId) { Width = 900, Height = 640 };
            var root = (FrameworkElement)dlg.Content;
            if (root is Panel panel)
                panel.Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e));

            var size = new Size(900, 640);
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();

            var bitmap = new RenderTargetBitmap(900, 640, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var stream = File.Create(full);
            encoder.Save(stream);
            Console.WriteLine($"Rendered {full}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Screenshot failed: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Developer helper: renders the walkthrough overlay over a mock editor so the dim, the
    /// ring and the bubble can be reviewed without launching the app.
    /// Usage: help_smoke --tutorial-screenshot &lt;path.png&gt; [scenarioId]
    /// </summary>
    private static int RenderTutorialScreenshot(string path, string scenarioId)
    {
        try
        {
            _ = new Application();
            var scenario = TutorialContent.Find(scenarioId) ?? TutorialContent.Scenarios[0];

            const double width = 1100, height = 660;
            var host = new Grid
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e))
            };

            // Mock editor furniture so the dim has something to dim.
            var cardStack = new StackPanel { Margin = new Thickness(12, 34, 12, 0) };
            Border? targetCard = null;
            for (int i = 0; i < 5; i++)
            {
                var card = new Border
                {
                    Height = 54,
                    Margin = new Thickness(0, 0, 0, 8),
                    CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x58, 0x5b, 0x70)),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = i == 2 ? "Light Combo 03" : $"Attack {i + 1:00}",
                        Foreground = new SolidColorBrush(Color.FromRgb(0xcd, 0xd6, 0xf4)),
                        FontSize = 12,
                        Margin = new Thickness(10, 8, 0, 0)
                    }
                };
                if (i == 2) targetCard = card;
                cardStack.Children.Add(card);
            }

            var mockLibrary = new Border
            {
                Width = 300,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25)),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Move Library",
                            Foreground = new SolidColorBrush(Color.FromRgb(0xcb, 0xa6, 0xf7)),
                            FontSize = 14,
                            Margin = new Thickness(12, 12, 0, 0)
                        },
                        cardStack
                    }
                }
            };
            var mockGraph = new Border
            {
                Width = 240,
                Height = 130,
                Margin = new Thickness(80, 110, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
                CornerRadius = new CornerRadius(6)
            };
            var mockNode = new Border
            {
                Width = 96,
                Height = 42,
                Margin = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa)),
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock
                {
                    Text = "Attack 03",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e)),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            mockGraph.Child = mockNode;

            host.Children.Add(mockGraph);
            host.Children.Add(mockLibrary);

            var overlay = new TutorialOverlay();
            host.Children.Add(overlay);

            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();

            // Step 2 is a "do it" step, so the tip callout and the waiting line are visible.
            int index = Math.Min(1, scenario.Steps.Count - 1);
            var step = scenario.Steps[index];
            if (step.Anchor == TutorialAnchors.LibraryCard && targetCard != null)
            {
                // Two holes: the card to grab and the graph to drop onto, ring on the card.
                overlay.ShowStep(scenario, index,
                    new FrameworkElement[] { targetCard, mockGraph }, targetCard);
            }
            else
            {
                bool nodeStep = step.Anchor == TutorialAnchors.LastTarget;
                overlay.ShowStep(scenario, index,
                    new FrameworkElement[] { nodeStep ? mockNode : mockGraph },
                    nodeStep ? mockNode : null);
            }
            overlay.UpdateLayout();
            host.UpdateLayout();

            var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var stream = File.Create(full);
            encoder.Save(stream);
            Console.WriteLine($"Rendered {full} ({scenario.Id} step {index + 1})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Tutorial screenshot failed: {ex}");
            return 1;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (condition)
        {
            Console.WriteLine($"  PASS {message}");
            return;
        }
        _failures++;
        Console.WriteLine($"  FAIL {message}");
    }
}
