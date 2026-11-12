using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SifuMovesetEditor;

/// <summary>
/// Editor for m_ComboStartNodesContainer: view start states / tickets, edit transition
/// probability / target, add transitions (vanilla Option C pattern), remove transitions.
/// Works for both enemy trees (BP_AIAttackTIcketNames) and the player tree
/// (BP_MainCharComboStartStates) - the container layout is identical. Edits apply to a
/// deep copy - OK commits, Cancel discards.
/// </summary>
public partial class StartEntriesDialog : Window
{
    private readonly ComboGraph _graph;
    private readonly List<StartEntry> _entries;
    // Beginner view setting: when false, transitions targeting AI action/event nodes
    // are hidden from this dialog (footnote keeps their probability share visible).
    private readonly bool _showAiNodes;
    // Player tree (DB/_MainChar) vs enemy archetype tree. Only changes wording and which
    // start-state enum the picker writes - the container structure is identical.
    private readonly bool _isMainCharGraph;

    /// <summary>Edited entries (deep copy). Set only when OK confirms actual changes.</summary>
    public List<StartEntry>? Result { get; private set; }

    private StartEntry? _focusNewIn;

    /// <summary>Owner callback that selects + centres the target node on the combo
    /// canvas behind this modal dialog. Null = no jump feature (owner not wired).</summary>
    private readonly Action<int>? _revealNode;

    /// <summary>Pick-on-graph suspend state: set when the user asks to choose the target
    /// by clicking a node on the canvas. The dialog closes like Cancel (DialogResult
    /// false, no OK validation) but carries the live entries copy; the owner applies
    /// the picked node via <see cref="ApplyPickedTarget"/> and reopens with the copy.</summary>
    public List<StartEntry>? SuspendedEntries { get; private set; }
    public StartTransition? PickTransition { get; private set; }
    /// <summary>-1 = single-target transition; >= 0 = multi-key index being retargeted.</summary>
    public int PickKeyIndex { get; private set; } = -1;

    private void RequestPick(StartTransition t, int keyIndex)
    {
        SuspendedEntries = _entries;
        PickTransition = t;
        PickKeyIndex = keyIndex;
        DialogResult = false;
    }

    /// <summary>Applies a canvas-picked node to a transition exactly like the target
    /// combos do (single-target or per-key), including the multi-key summary refresh.</summary>
    public static void ApplyPickedTarget(StartTransition t, int keyIndex, ComboNode node)
    {
        string name = NodeName(node);
        if (keyIndex >= 0)
        {
            t.KeyTargetNodeIds[keyIndex] = node.Id;
            t.KeyTargetTreeIndices[keyIndex] = node.TreeIndex;
            t.KeyTargetNames[keyIndex] = name;
            t.TargetSummary = string.Join(", ",
                t.Keys.Zip(t.KeyTargetTreeIndices, (k2, v2) => $"{k2}→{v2}"));
        }
        else
        {
            t.TargetNodeId = node.Id;
            t.TargetTreeIndex = node.TreeIndex;
            t.TargetName = name;
        }
    }

    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x6c, 0x70, 0x86));
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0xa6, 0xad, 0xc8));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xcd, 0xd6, 0xf4));
    private static readonly Brush BlueBrush = new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa));
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xf3, 0x8b, 0xa8));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xf9, 0xe2, 0xaf));
    private static readonly Brush CardBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25));
    private static readonly Brush HeadBrush = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));
    private static readonly Brush BorderBrushDark = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));
    private static readonly Brush BorderBrushInput = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5a));

    private sealed class TargetOption
    {
        public int Id { get; init; } = -1;
        public int TreeIndex { get; init; } = -1;
        public string Name { get; init; } = "";
        public string Display { get; init; } = "";
        public override string ToString() => Display;
    }

    private sealed class TicketOption
    {
        public string EnumName { get; init; } = AiTicketEnum;
        public int EnumValue { get; init; }
        public string Label { get; init; } = "";
        public string Value => $"{EnumName}::NewEnumerator{EnumValue}";
        public override string ToString() => $"{EnumValue} · {Label}";
    }

    private const string AiTicketEnum = "BP_AIAttackTIcketNames";
    private const string MainCharStartStateEnum = "BP_MainCharComboStartStates";

    // Player start states (BP_MainCharComboStartStates). The enum asset itself is not in
    // the extracted paks, so these are the seven values vanilla player trees reference:
    // GetAllEntries over DB/_MainChar/Combos. Labels are overridden by each entry's own
    // m_ParentNodeName when the tree carries one.
    private static readonly (int Enum, string Label)[] MainCharStartStateCatalog =
    {
        (0, "Idle"),
        (4, "Dodge"),
        (5, "Grab"),
        (6, "Rush"),
        (7, "KnockedDown"),
        (10, "PushInstigator"),
        (12, "Default"),
    };

    // The 51 BP_AIAttackTIcketNames values observed in enemy combo start entries
    // (docs/TICKETS.md). Other enum numbers are never routed to a combo.
    private static readonly (int Enum, string Label)[] TicketCatalog =
    {
        (0, "Direct Opponent Regular Attacks"),
        (1, "Indirect Opponent Interruption Attacks"),
        (2, "MC Dodge Close"),
        (3, "MC Cornering me"),
        (5, "Idle"),
        (6, "Dodge"),
        (7, "Grab"),
        (8, "Rush"),
        (9, "KnockedDown"),
        (10, "Parry"),
        (12, "Counter"),
        (13, "AttackAsDefense"),
        (14, "Traversal Attack"),
        (15, "Raging Bull"),
        (16, "Fajar Wall Jump"),
        (19, "Sean Dodge"),
        (20, "Sean Idle"),
        (23, "Avoid"),
        (24, "SeanDash"),
        (29, "Fajar Regular Attacks"),
        (30, "Custom Combo 1"),
        (31, "Custom Combo 2"),
        (32, "Custom Combo 3"),
        (34, "Custom Combo 5"),
        (35, "Guard"),
        (41, "Fengjie LongRange Attacks"),
        (45, "KurokiDelayGapCloser"),
        (46, "Sean Avoid"),
        (47, "SeanBurstActive"),
        (48, "Fengjie ShortRange Attacks"),
        (49, "ThrowFromGroundOpportunity"),
        (52, "FireDisciple WallJump GoTo"),
        (53, "Fengjie OnDizzy"),
        (54, "On MC Knocked down"),
        (55, "Fengjie OnPushback"),
        (56, "Yang Offense Attacks"),
        (57, "Yang Defense Attacks"),
        (58, "Dizzy Reaction"),
        (60, "Yang Defense Close Reaction"),
        (64, "MC Structure Broken"),
        (65, "Whiff Reaction"),
        (68, "KurokiInstantAttackBis"),
        (69, "FallbackPostDefense"),
        (71, "Yang WallJump GoTo"),
        (72, "Yang Ticket Phase 2"),
        (73, "Yang Ticket Phase 3"),
        (74, "Fajar Wall Jump Horizontal"),
        (76, "Fengjie On Whiff"),
        (78, "Big Guy Raging Bull Instant"),
        (83, "Direct Opponent Boss Version"),
        (84, "Indirect Opponent Boss Version"),
    };

    /// <summary>True for a player combo tree. Shared GraphIdentity helper: the tree's
    /// own SourceGamePath first, the player weapon/stance tags as the old-save fallback
    /// (a graph restored from an old save may not carry a SourceGamePath yet).</summary>
    private static bool IsMainCharGraph(ComboGraph graph) => GraphIdentity.IsMainChar(graph);

    /// <summary>Enum the container's m_EnumState.m_Value names are written against.
    /// Read from the tree's own entries first so a cloned or mod-imported tree keeps its
    /// enum; otherwise fall back to the AI ticket enum or the player start-state enum.</summary>
    private static string ResolveTicketEnumName(ComboGraph graph)
    {
        var existing = graph.StartEntries?
            .Select(e => e.TicketEnumValue)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v) && v.Contains("::"));
        if (existing != null) return existing.Split("::")[0];
        return IsMainCharGraph(graph) ? MainCharStartStateEnum : AiTicketEnum;
    }

    /// <summary>Parses "Enum::NewEnumeratorN" into N.</summary>
    private static bool TryParseEnumValue(string ticketEnumValue, out int value)
    {
        value = -1;
        if (string.IsNullOrEmpty(ticketEnumValue)) return false;
        var parts = ticketEnumValue.Split("::");
        if (parts.Length < 2) return false;
        const string prefix = "NewEnumerator";
        string tail = parts[^1];
        if (!tail.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return int.TryParse(tail[prefix.Length..], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Picker options for this graph's enum. Enemy trees use the researched AI
    /// ticket list; player trees list the known start states. On player trees a label the
    /// container itself carries (m_ParentNodeName) wins over the built-in guess - enemy
    /// entry labels may carry designer suffixes, so those keep the catalog text. Values a
    /// tree already uses that the catalog does not list stay selectable.</summary>
    private static List<TicketOption> BuildTicketOptions(ComboGraph graph, string enumName)
    {
        var labelByValue = new Dictionary<int, string>();
        foreach (var entry in graph.StartEntries ?? Enumerable.Empty<StartEntry>())
        {
            if (TryParseEnumValue(entry.TicketEnumValue, out int value) && !string.IsNullOrEmpty(entry.Label))
                labelByValue[value] = entry.Label;
        }

        bool isMainChar = IsMainCharGraph(graph);
        var catalog = isMainChar ? MainCharStartStateCatalog : TicketCatalog;
        var options = new List<TicketOption>();
        var seen = new HashSet<int>();
        foreach (var (num, label) in catalog)
        {
            options.Add(new TicketOption
            {
                EnumName = enumName,
                EnumValue = num,
                Label = isMainChar && labelByValue.TryGetValue(num, out var own) ? own : label
            });
            seen.Add(num);
        }
        foreach (var kv in labelByValue.Where(kv => !seen.Contains(kv.Key)).OrderBy(kv => kv.Key))
            options.Add(new TicketOption { EnumName = enumName, EnumValue = kv.Key, Label = kv.Value });
        return options;
    }

    public StartEntriesDialog(ComboGraph graph, bool showAiNodes, Action<int>? revealNode = null,
        List<StartEntry>? resumeEntries = null)
    {
        InitializeComponent();
        _graph = graph;
        _showAiNodes = showAiNodes;
        _revealNode = revealNode;
        // Resume path: the caller passes the live deep copy from a suspended dialog
        // (pick-on-graph flow) so every edit survives; otherwise clone from the graph.
        _entries = resumeEntries ?? graph.StartEntries.Select(e => e.Clone()).ToList();
        _isMainCharGraph = IsMainCharGraph(graph);
        string noun = _isMainCharGraph ? "start states" : "tickets";
        txtSubtitle.Text = $"{_entries.Count} {noun} · {graph.WeaponName} · {graph.Nodes.Count} nodes{AiHiddenSuffix()}";
        foreach (var opt in BuildTicketOptions(graph, ResolveTicketEnumName(graph)))
            cmbTicket.Items.Add(opt);
        ApplyGraphWording();
        Rebuild();
        RefreshGlobalStatus();
    }

    /// <summary>Player trees are keyed by the state the player is in, enemy trees by the
    /// ticket the AI brain requests - same container, different runtime source.</summary>
    private void ApplyGraphWording()
    {
        if (_isMainCharGraph)
        {
            txtNote.Text = "Each entry is one player start state "
                + "(BP_MainCharComboStartStates::NewEnumeratorN): the state the player is in when "
                + "this combo can begin. Each row carries the input that fires it (LMB/RMB/Rush/…) "
                + "and rows for the same input share one 100% selection pool - "
                + "untouched entries are never rebalanced. Targets are limited to attack nodes "
                + "already in this moveset.";
            txtEntryLabel.ToolTip = "Entry label (m_ParentNodeName). Defaults to the start-state "
                + "label when left empty. Must be unique in this container.";
            cmbTicket.ToolTip = "Player start state this entry belongs to. Written as "
                + MainCharStartStateEnum + "::NewEnumeratorN.";
            // New entries clone the container's first single-key row, then export rewrites
            // the state enum, label and every row's input from the model.
            btnAddEntry.ToolTip = "Adds an entry cloned from the container's template row. "
                + "Its state, label and row inputs are then written from what you pick here. "
                + "Retarget an existing start state first - the player only has these 7 states.";
            if (string.IsNullOrEmpty(txtAuthorHint.Text))
                txtAuthorHint.Text = "Tip: retarget an existing state - the player has 7 start states.";
        }
        else
        {
            txtNote.Text = "Each entry is one AI ticket: when the brain requests it, one of its "
                + "transitions is picked by probability (vanilla always sums to 100%). Targets are "
                + "limited to attack nodes already in this moveset - create new attack nodes on the "
                + "canvas first. Tickets only fire when the AI brain requests them.";
            txtEntryLabel.ToolTip = "Entry label (m_ParentNodeName). Defaults to the ticket label "
                + "when left empty. Must be unique in this container.";
            cmbTicket.ToolTip = "AI ticket the brain must request for this entry to fire. Written as "
                + AiTicketEnum + "::NewEnumeratorN.";
        }
    }

    private void Rebuild()
    {
        entriesPanel.Children.Clear();
        foreach (var entry in _entries.Where(e => !e.Removed))
            entriesPanel.Children.Add(BuildEntryCard(entry));
        RefreshGlobalStatus();
    }

    private UIElement BuildEntryCard(StartEntry entry)
    {
        var border = new Border
        {
            Background = CardBrush,
            BorderBrush = BorderBrushDark,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(0, 0, 0, 6)
        };
        var sp = new StackPanel();

        // --- header: label + ticket + condition chip + live sum ---
        var header = new Border
        {
            Background = HeadBrush,
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(4, 4, 0, 0)
        };
        var headerDock = new DockPanel();

        var sumText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Margin = new Thickness(10, 0, 0, 0)
        };
        DockPanel.SetDock(sumText, Dock.Right);
        headerDock.Children.Add(sumText);

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(entry.Label) ? $"entry {entry.VanillaIndex + 1}" : entry.Label,
            FontWeight = FontWeights.Bold,
            Foreground = BlueBrush,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrEmpty(entry.TicketEnumValue))
        {
            string ticket = entry.TicketEnumValue.Contains("::")
                ? entry.TicketEnumValue.Split("::")[^1]
                : entry.TicketEnumValue;
            var ticketTb = new TextBlock
            {
                Text = $"· {ticket}",
                Foreground = GrayBrush,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = entry.TicketEnumValue + (_isMainCharGraph
                    ? "\nThe player must be in this state for these starters to fire."
                    : "\nThe AI brain must request this ticket for it to fire.")
            };
            left.Children.Add(ticketTb);
        }

        string cond = string.IsNullOrEmpty(entry.ConditionClass) ? "always" : $"cond: {entry.ConditionClass}";
        var chip = new Border
        {
            Background = BorderBrushInput,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = string.IsNullOrEmpty(entry.ConditionClass)
                ? (_isMainCharGraph
                    ? "No condition: any row of this state can fire."
                    : "No condition: this ticket can fire whenever the brain requests it.")
                : $"Condition class: {entry.ConditionClass}\n(Instanced condition, not editable in this version.)"
                + (string.IsNullOrEmpty(entry.PhaseName) || entry.PhaseName == "None" ? "" : $"\nPhase: {entry.PhaseName}")
        };
        chip.Child = new TextBlock { Text = cond, FontSize = 10, Foreground = TextBrush };
        left.Children.Add(chip);

        headerDock.Children.Add(left);
        header.Child = headerDock;
        sp.Children.Add(header);

        // --- transition rows ---
        var rowsPanel = new StackPanel { Margin = new Thickness(10, 6, 10, 0) };
        var liveRows = entry.Transitions.Where(t => !t.Removed).ToList();
        // Add-row is allowed when the row the exporter would clone can carry a new
        // transition: unconditional (key 255), multi-key (a conditioned map is copied
        // with its full key set; a legacy row with no per-key data is collapsed by
        // export), or a conditioned single-key row (cloned as-is - the new row joins
        // the same input pool and competes by probability, e.g. MainChar Idle's
        // Light->28 / Heavy->29 rows). A bare single-key row with no condition stays
        // blocked: cloning it would duplicate that key route instead of splitting a
        // probability.
        var templateRow = entry.CloneTemplateRow();
        bool canAdd = templateRow != null && StartEntry.CanCloneTemplate(templateRow, entry.ConditionClass);
        bool canDelete = liveRows.Count > 1;

        // Rows sharing input + key + condition are a legitimate vanilla pattern: node 1
        // ships three Light rows at 33/34/33 so one press can open three different
        // combos. Surface it (informational, never blocking) so an accidental duplicate
        // does not silently bias the starter.
        var dupGroups = liveRows
            .GroupBy(t => (Input: InputCatalog.Normalize(t.InputEnum), t.KeyByte,
                Cond: (t.ConditionClass ?? "").Trim()))
            .Where(g => g.Count() > 1)
            .ToList();
        if (dupGroups.Count > 0)
        {
            int dupRows = dupGroups.Sum(g => g.Count());
            string inputs = string.Join(", ", dupGroups
                .Select(g => string.IsNullOrEmpty(InputCatalog.LabelFor(g.Key.Input))
                    ? "(no input)" : InputCatalog.LabelFor(g.Key.Input))
                .Distinct());
            rowsPanel.Children.Add(new TextBlock
            {
                Text = $"ⓘ {dupRows} rows share the same input + key ({inputs}) - they "
                    + "compete by probability. This is how vanilla splits a starter "
                    + "(node 1: three Light rows at 33/34/33); check the split is what you want.",
                Foreground = DimBrush,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 2)
            });
        }

        var normBtn = new Button
        {
            Content = "Σ→100%",
            Style = (Style)FindResource("DarkButton"),
            FontSize = 11,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Scale the rows proportionally so the entry (enemy) or each "
                + "input pool (player) totals exactly 100% - keeps your "
                + "relative values. Only active when a total is off."
        };
        var evenBtn = new Button
        {
            Content = "Even split",
            Style = (Style)FindResource("DarkButton"),
            FontSize = 11,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Divide 100% evenly across all rows (or across each player trigger "
                + "group). Overwrites manually typed values - use Σ→100% to keep ratios instead."
        };
        normBtn.Click += (_, _) =>
        {
            NormalizeTo100(entry, _isMainCharGraph);
            Rebuild();
        };
        evenBtn.Click += (_, _) =>
        {
            EvenSplit(entry, _isMainCharGraph);
            Rebuild();
        };
        var resetBtn = new Button
        {
            Content = "Reset",
            Style = (Style)FindResource("DarkButton"),
            FontSize = 11,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6, 0, 0, 0),
            IsEnabled = entry.VanillaIndex >= 0 && (entry.Removed || entry.IsModified()),
            ToolTip = "Restore this entry to vanilla: rows keep their original "
                + "probability/targets/keys, deleted rows come back, new rows are dropped."
        };
        resetBtn.Click += (_, _) => ResetEntry(entry);

        foreach (var t in entry.Transitions)
        {
            if (t.Removed) continue;
            if (IsHiddenAiRow(t)) continue;
            rowsPanel.Children.Add(BuildRow(entry, t, canDelete, sumText, normBtn, evenBtn));
        }

        // Beginner view: make hidden AI transitions + key targets visible as counts so
        // the header sum (which still includes their probability) adds up on screen.
        if (!_showAiNodes)
        {
            int hiddenRows = entry.Transitions.Count(t => !t.Removed && IsHiddenAiRow(t));
            // AI keys inside rows that still render (fully hidden rows count as one).
            int hiddenKeys = entry.Transitions
                .Where(t => !t.Removed && !IsHiddenAiRow(t))
                .Sum(t => HiddenAiKeyCount(t));
            float hiddenProb = entry.Transitions
                .Where(t => !t.Removed && IsHiddenAiRow(t)).Sum(t => t.Probability);
            if (hiddenRows > 0 || hiddenKeys > 0)
            {
                var parts = new List<string>();
                if (hiddenRows > 0)
                    parts.Add($"{hiddenRows} AI transition{(hiddenRows > 1 ? "s" : "")} hidden"
                        + (hiddenProb > 0 ? $" ({FormatProb(hiddenProb)}%)" : ""));
                if (hiddenKeys > 0)
                    parts.Add($"{hiddenKeys} AI key target{(hiddenKeys > 1 ? "s" : "")} hidden");
                rowsPanel.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", parts)
                        + " — enable \"Show AI Actions & Events\" in Settings",
                    Foreground = GrayBrush,
                    FontSize = 10,
                    FontStyle = FontStyles.Italic,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 2)
                });
            }
        }

        var addBtn = new Button
        {
            Content = "+ Add Transition",
            Style = (Style)FindResource("DarkButton"),
            FontSize = 11,
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = canAdd
        };
        if (!canAdd)
        {
            addBtn.ToolTip = templateRow == null
                ? "Vanilla-empty ticket (no transitions to clone). Export builds new rows by cloning "
                  + "the entry's template row, so there is nothing to add here."
                : $"Row {templateRow.OriginalIndex + 1} is this entry's template (first live row) and is a "
                  + "single-key route without a condition - cloning it would duplicate that key instead of "
                  + "splitting a probability. Give it a condition or key 255 first.";
        }
        else
        {
            addBtn.ToolTip = $"Give this ticket another candidate. The new row starts at 0% and copies "
                + $"row {templateRow!.OriginalIndex + 1}'s input, condition and full key map (every condition "
                + "outcome and its target). Retarget the outcomes you want, then give it a probability "
                + "(or click Σ→100% / Even split). Rows sharing the same input, key and condition compete "
                + "by probability, which is how vanilla splits a starter.";
            addBtn.Click += (_, _) => AddTransition(entry);
        }
        var delEntryBtn = new Button
        {
            Content = "Delete Entry",
            Style = (Style)FindResource("DarkButton"),
            FontSize = 11,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = RedBrush,
            ToolTip = entry.VanillaIndex >= 0
                ? "Drop this whole entry from m_ComboStartNodesContainer on export (Cancel in this dialog discards)."
                : "Remove this new entry."
        };
        delEntryBtn.Click += (_, _) => DeleteEntry(entry);

        var actionBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0)
        };
        actionBar.Children.Add(delEntryBtn);
        actionBar.Children.Add(addBtn);
        actionBar.Children.Add(normBtn);
        actionBar.Children.Add(evenBtn);
        actionBar.Children.Add(resetBtn);
        rowsPanel.Children.Add(actionBar);
        sp.Children.Add(rowsPanel);

        border.Child = sp;
        RefreshEntryHeader(entry, sumText, normBtn, evenBtn);
        return border;
    }

    private UIElement BuildRow(StartEntry entry, StartTransition t, bool canDelete, TextBlock sumText, Button normBtn, Button evenBtn)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        // 84 (was 72): the 62px probability box + % label need ~75px - at 72 the %
        // bled into the target column. Width comes from the flexible chain column.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        // m_eInputTransition: the button that fires this row (LMB/RMB/Rush/...).
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

        var num = new TextBlock
        {
            Text = t.OriginalIndex >= 0 ? (t.OriginalIndex + 1).ToString() : "new",
            Foreground = t.OriginalIndex >= 0 ? GrayBrush : WarnBrush,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        Grid.SetColumn(num, 0);
        grid.Children.Add(num);

        // probability
        var probBox = new TextBox
        {
            Style = (Style)FindResource("DarkTextBox"),
            Width = 62,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Text = FormatProb(t.Probability),
            ToolTip = _isMainCharGraph
                ? "Probability this target is picked when the player's input fires. Rows sharing "
                  + "the same input form one selection pool that totals 100%."
                : "Probability this target is picked when the ticket fires. Vanilla entries always sum to 100% per entry."
        };
        var normalBorder = BorderBrushInput;
        InputFilter.ApplyFloat(probBox);
        probBox.TextChanged += (_, _) =>
        {
            if (float.TryParse(probBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0)
            {
                t.Probability = v;
                probBox.BorderBrush = normalBorder;
                RefreshEntryHeader(entry, sumText, normBtn, evenBtn);
            }
            else
            {
                probBox.BorderBrush = RedBrush;
            }
            RefreshGlobalStatus();
        };
        var probCell = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(probBox, Dock.Left);
        probCell.Children.Add(probBox);
        probCell.Children.Add(new TextBlock
        {
            Text = "%",
            Foreground = GrayBrush,
            FontSize = 10,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(probCell, 1);
        grid.Children.Add(probCell);

        // input (m_eInputTransition). Stored as the bare enum name; "" and "None" are
        // the same choice. Unknown names load as a "(custom)" item instead of being
        // rounded down to None.
        var inputCombo = new ComboBox
        {
            Style = (Style)FindResource("DarkCombo"),
            Width = 118,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        inputCombo.PreviewMouseWheel += (_, e) => { if (!inputCombo.IsDropDownOpen) e.Handled = true; };
        var inputWarning = new TextBlock
        {
            FontSize = 11,
            Foreground = WarnBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0)
        };
        bool suppressInput = false;
        string CustomItemLabel() => $"{t.InputEnum} (custom)";

        void ShowInputState()
        {
            var name = InputCatalog.Normalize(t.InputEnum);
            if (!InputCatalog.IsKnown(name))
            {
                inputWarning.Text = "⚠";
                inputWarning.ToolTip = $"'{t.InputEnum}' is not a known EComboTransition value. "
                    + "It is written verbatim - the engine only reacts to names it declares.";
            }
            else if (!InputCatalog.IsVanillaUsed(name))
            {
                inputWarning.Text = "⚠";
                inputWarning.ToolTip = $"{name} is declared by EComboTransition but no vanilla combo "
                    + "asset uses it. It will be written, but the engine may ignore it.";
            }
            else
            {
                inputWarning.Text = "";
                inputWarning.ToolTip = null;
            }
            inputCombo.ToolTip = string.IsNullOrEmpty(t.InputEnum) || InputCatalog.Normalize(t.InputEnum) == "None"
                ? "No input: this row fires when its condition matches (or unconditionally when key 255)."
                : $"Input that fires this row: {name}.";
        }

        void FillInputItems()
        {
            inputCombo.Items.Clear();
            foreach (var c in InputCatalog.Choices) inputCombo.Items.Add(c.MenuLabel);
            if (!InputCatalog.IsKnown(t.InputEnum) && !string.IsNullOrEmpty(t.InputEnum))
                inputCombo.Items.Insert(0, CustomItemLabel());
            inputCombo.Items.Add(InputCatalog.CustomMenuLabel);
        }

        FillInputItems();
        suppressInput = true;
        inputCombo.SelectedItem = InputCatalog.IsKnown(t.InputEnum)
            ? InputCatalog.MenuLabelFor(t.InputEnum)
            : CustomItemLabel();
        suppressInput = false;
        ShowInputState();

        inputCombo.SelectionChanged += (_, _) =>
        {
            if (suppressInput || inputCombo.SelectedItem is not string sel) return;
            if (sel == InputCatalog.CustomMenuLabel)
            {
                var raw = InputPrompt.Show(this, "Custom input value",
                    "Raw EComboTransition name (for example Special or Dodge).\n"
                        + "Only names EComboTransition declares do anything; anything else is written verbatim.",
                    t.InputEnum);
                if (raw == null)
                {
                    // Cancel: put the picker back on the row's current value.
                    suppressInput = true;
                    inputCombo.SelectedItem = InputCatalog.IsKnown(t.InputEnum)
                        ? InputCatalog.MenuLabelFor(t.InputEnum)
                        : CustomItemLabel();
                    suppressInput = false;
                    return;
                }
                t.InputEnum = raw.Length == 0 ? InputCatalog.None.EnumName : raw;
                Rebuild();
                return;
            }
            var choice = InputCatalog.Choices.FirstOrDefault(c => c.MenuLabel == sel);
            if (choice == null) return;
            t.InputEnum = choice.EnumName;
            ShowInputState();
            RefreshGlobalStatus();
        };

        var inputCell = new DockPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        DockPanel.SetDock(inputCombo, Dock.Left);
        inputCell.Children.Add(inputCombo);
        inputCell.Children.Add(inputWarning);
        Grid.SetColumn(inputCell, 2);
        grid.Children.Add(inputCell);

        // key badge (built first so per-key combo edits can refresh its tooltip)
        var key = new TextBlock
        {
            FontSize = 10,
            Foreground = GrayBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        void RefreshKeyBadge()
        {
            key.Text = t.KeyCount == 1 ? $"key {t.KeyByte}" : $"{t.KeyCount} keys";
            key.ToolTip = t.KeyCount > 1
                ? $"Multi-key conditional map: {t.TargetSummary}\n"
                    + "Use each key's dropdown to re-route a starter (your [new] nodes are listed). "
                    + "+ duplicates this row's condition and every key at 0% so you can retarget them."
                : t.KeyByte == 255
                    ? "Unconditional (key 255): chosen purely by probability."
                    : $"Condition outcome {t.KeyByte}: the engine routes to this target when the condition returns {t.KeyByte}.";
        }
        RefreshKeyBadge();
        Grid.SetColumn(key, 4);
        grid.Children.Add(key);

        // target
        if (t.KeyCount > 1 && HasKeyModel(t))
        {
            // One editable combo per map key: the engine routes each condition outcome
            // to its own target, so every key is its own retarget.
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < t.KeyCount; i++)
            {
                int ki = i;
                // Beginner view: this condition outcome routes to an AI node - hide
                // its line (the row only renders when at least one key remains).
                if (IsHiddenAiNodeId(t.KeyTargetNodeIds[ki])) continue;
                var line = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                line.Children.Add(new TextBlock
                {
                    Text = t.Keys[ki] == 255 ? "255" : $"k{t.Keys[ki]}",
                    Width = 34,
                    FontSize = 10,
                    Foreground = GrayBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = t.Keys[ki] == 255
                        ? "Unconditional fallback (key 255)."
                        : $"Condition outcome {t.Keys[ki]}: routed when the condition returns {t.Keys[ki]}."
                });
                var combo = new ComboBox
                {
                    Style = (Style)FindResource("DarkCombo"),
                    // 166 (was 218): leaves room for the ◎ jump + ⌖ pick buttons so the
                    // line still fits the 260px target column without hitting the key badge.
                    Width = 166,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = $"Target when the condition returns {t.Keys[ki]}."
                };
                combo.PreviewMouseWheel += (_, e) => { if (!combo.IsDropDownOpen) e.Handled = true; };
                var options = BuildTargetOptions(t.KeyTargetNodeIds[ki], t.KeyTargetTreeIndices[ki], t.KeyTargetNames[ki]);
                foreach (var o in options) combo.Items.Add(o);
                combo.SelectedItem = options.FirstOrDefault(o =>
                    o.Id == t.KeyTargetNodeIds[ki]
                    && (t.KeyTargetNodeIds[ki] >= 0 || o.TreeIndex == t.KeyTargetTreeIndices[ki]));
                var jumpBtn = CreateJumpButton(() => t.KeyTargetNodeIds[ki]);
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is not TargetOption opt) return;
                    if (opt.Id != t.KeyTargetNodeIds[ki] || opt.TreeIndex != t.KeyTargetTreeIndices[ki])
                    {
                        t.KeyTargetNodeIds[ki] = opt.Id;
                        t.KeyTargetTreeIndices[ki] = opt.TreeIndex;
                        t.KeyTargetNames[ki] = opt.Name;
                        t.TargetSummary = string.Join(", ",
                            t.Keys.Zip(t.KeyTargetTreeIndices, (k2, v2) => $"{k2}→{v2}"));
                        RefreshKeyBadge();
                        RefreshGlobalStatus();
                    }
                    RefreshJumpButton(jumpBtn, t.KeyTargetNodeIds[ki]);
                };
                line.Children.Add(combo);
                line.Children.Add(jumpBtn);
                line.Children.Add(CreatePickButton(t, ki));
                stack.Children.Add(line);
            }
            Grid.SetColumn(stack, 3);
            grid.Children.Add(stack);
        }
        else if (t.KeyCount > 1)
        {
            var info = new TextBlock
            {
                Text = string.IsNullOrEmpty(t.TargetSummary) ? "multiple keys" : t.TargetSummary,
                Foreground = WarnBrush,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = "Conditional routing: one target per condition outcome (key → node). "
                    + "Loaded from a save made before per-key editing - re-parse the tree to edit.\n" + t.TargetSummary
            };
            Grid.SetColumn(info, 3);
            grid.Children.Add(info);
        }
        else
        {
            var combo = new ComboBox
            {
                Style = (Style)FindResource("DarkCombo"),
                // 190: the ◎ jump + ⌖ pick buttons now share the target column with the
                // combo (the input column took the old button column's space).
                Width = 190,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            combo.PreviewMouseWheel += (_, e) => { if (!combo.IsDropDownOpen) e.Handled = true; };
            combo.ToolTip = t.OriginalIndex < 0
                ? "New row: export clones the entry's first row, then applies this row's input, key "
                    + "and target. A conditioned template keeps its condition; an old multi-key template "
                    + "without a per-key model is collapsed to one unconditional key-255 row. "
                    + "Pick where this row's combo starts."
                : _showAiNodes
                    ? "Target node (attack, action or event nodes; 'new' = created in this editor, assigned a tree index on export)."
                    : "Target node (attack nodes only; 'new' = created in this editor, assigned a tree index on export).";
            var options = BuildTargetOptions(t);
            foreach (var o in options) combo.Items.Add(o);
            combo.SelectedItem = options.FirstOrDefault(o =>
                o.Id == t.TargetNodeId && (t.TargetNodeId >= 0 || o.TreeIndex == t.TargetTreeIndex));

            var chain = new TextBlock
            {
                FontSize = 10,
                Foreground = DimBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 0, 0, 0)
            };
            void UpdateChain()
            {
                string preview = ChainPreview(t.TargetNodeId, t.TargetTreeIndex, t.TargetName);
                chain.Text = t.OriginalIndex < 0 && t.KeyByte == 255 && t.TargetNodeId >= 0
                    ? $"(unconditional) {preview}"
                    : preview;
            }

            var jumpBtn = CreateJumpButton(() => t.TargetNodeId);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is not TargetOption opt) return;
                if (opt.Id != t.TargetNodeId)
                {
                    t.TargetNodeId = opt.Id;
                    t.TargetTreeIndex = opt.TreeIndex;
                    t.TargetName = opt.Name;
                    RefreshGlobalStatus();
                }
                RefreshJumpButton(jumpBtn, t.TargetNodeId);
                UpdateChain();
            };
            UpdateChain();

            var btnCell = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            btnCell.Children.Add(combo);
            btnCell.Children.Add(jumpBtn);
            btnCell.Children.Add(CreatePickButton(t, -1));
            Grid.SetColumn(btnCell, 3);
            grid.Children.Add(btnCell);
            Grid.SetColumn(chain, 5);
            grid.Children.Add(chain);

            if (_focusNewIn == entry && t.OriginalIndex < 0)
            {
                _focusNewIn = null;
                combo.Dispatcher.BeginInvoke(new Action(() =>
                {
                    combo.Focus();
                }));
            }
        }

        // delete
        var del = new Button
        {
            Content = "✕",
            Width = 24,
            Height = 22,
            FontSize = 10,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("DarkButton"),
            IsEnabled = canDelete,
            ToolTip = canDelete
                ? "Remove this transition from the ticket."
                : "An entry must keep at least one transition."
        };
        if (canDelete)
            del.Click += (_, _) => RemoveTransition(entry, t);
        Grid.SetColumn(del, 6);
        grid.Children.Add(del);

        return grid;
    }

    /// <summary>True when the per-key parallel lists line up with KeyCount (they are
    /// empty on saves made before per-key editing existed).</summary>
    private static bool HasKeyModel(StartTransition t) => t.HasKeyModel;

    private static bool IsAiNode(ComboNode n) => n.IsActionNode || n.IsEventNode;

    private ComboNode? NodeById(int nodeId)
        => nodeId < 0 ? null : _graph.Nodes.FirstOrDefault(x => x.Id == nodeId);

    private const string JumpTooltipOn =
        "Jump to this node on the combo graph (selects + centres it). Close this dialog to interact.";
    private const string JumpTooltipCondensed =
        "Condensed node (pre-save) - not on the canvas, cannot jump.";
    private const string JumpTooltipDeadRedirect =
        "Dead redirect - its destination no longer exists in the graph, cannot jump.";

    private void RefreshJumpButton(Button btn, int targetId)
    {
        // Dead redirects (destination deleted) stay hidden on the canvas even after
        // start-entry redirects were un-hidden, so the jump would find no box.
        bool deadRedirect = targetId >= 0
            && NodeById(targetId) is { } n && n.IsRedirect
            && _graph.ResolveRedirectFinal(targetId) < 0;
        btn.IsEnabled = targetId >= 0 && !deadRedirect;
        btn.ToolTip = targetId < 0 ? JumpTooltipCondensed
            : deadRedirect ? JumpTooltipDeadRedirect
            : JumpTooltipOn;
    }

    /// <summary>Small ◎ button that jumps to the transition's current target node on the
    /// combo canvas via the owner callback (reveals behind this modal dialog). The id is
    /// read live on click so retargeting the combo keeps the button correct; condensed
    /// targets (id -1) disable it.</summary>
    private Button CreateJumpButton(Func<int> getTargetId)
    {
        var btn = new Button
        {
            Content = "◎",
            Width = 24,
            Height = 22,
            FontSize = 10,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("DarkButton")
        };
        RefreshJumpButton(btn, getTargetId());
        btn.Click += (_, _) =>
        {
            int id = getTargetId();
            if (id >= 0 && _revealNode != null) _revealNode(id);
        };
        return btn;
    }

    // Resolvable node id pointing at an AI action/event node while the beginner view
    // is active. Condensed targets (nodeId < 0) and deleted nodes never resolve, so
    // they always stay visible (safe default - we cannot prove they are AI).
    private bool IsHiddenAiNodeId(int nodeId)
        => !_showAiNodes && NodeById(nodeId) is { } n && IsAiNode(n);

    /// <summary>Small ⌖ button that suspends the dialog (keeps every edit) so the user
    /// can click the target node directly on the combo canvas - retarget-style pick
    /// with a confirmation popup, applied via the owner.</summary>
    private Button CreatePickButton(StartTransition t, int keyIndex)
    {
        var btn = new Button
        {
            Content = "⌖",
            Width = 24,
            Height = 22,
            FontSize = 10,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("DarkButton"),
            ToolTip = "Pick the target on the combo graph: click this, then click the node. "
                + "Escape cancels; all edits in this dialog are kept.",
            Tag = keyIndex
        };
        btn.Click += (_, _) => RequestPick(t, keyIndex);
        return btn;
    }

    /// <summary>Per-key targets aiming at AI nodes (0 unless the row renders key lines).</summary>
    private int HiddenAiKeyCount(StartTransition t)
    {
        if (_showAiNodes || t.KeyCount <= 1 || !HasKeyModel(t)) return 0;
        int c = 0;
        for (int ki = 0; ki < t.KeyCount; ki++)
            if (IsHiddenAiNodeId(t.KeyTargetNodeIds[ki])) c++;
        return c;
    }

    /// <summary>True when the whole transition row must not render under the beginner view:
    /// a single-target row aimed at an AI node, or a multi-key row whose every key is AI.
    /// Legacy multi-key rows (no per-key model) keep their targets unknown - they stay.</summary>
    private bool IsHiddenAiRow(StartTransition t)
    {
        if (_showAiNodes) return false;
        if (t.KeyCount <= 1)
            return NodeById(t.TargetNodeId) is { } n && IsAiNode(n);
        if (HasKeyModel(t))
        {
            for (int ki = 0; ki < t.KeyCount; ki++)
                if (!IsHiddenAiNodeId(t.KeyTargetNodeIds[ki])) return false;
            return true;
        }
        return false;
    }

    private string AiHiddenSuffix()
    {
        if (_showAiNodes) return "";
        int n = _graph.Nodes.Count(x => !x.IsRoot && IsAiNode(x));
        return n > 0 ? $" (+{n} AI hidden)" : "";
    }

    private List<TargetOption> BuildTargetOptions(StartTransition t)
        => BuildTargetOptions(t.TargetNodeId, t.TargetTreeIndex, t.TargetName);

    private List<TargetOption> BuildTargetOptions(int nodeId, int treeIndex, string name)
    {
        var opts = new List<TargetOption>();

        // Current-value options: the choosable list below deliberately excludes
        // redirects and condensed (stripped conduit) targets, so a stored target of
        // either kind would otherwise render as a blank combo while holding real
        // data. Prepend a synthetic entry that carries the stored id/tree (selecting
        // it is a no-op - data stays vanilla-faithful) but displays the resolved move.
        if (nodeId < 0 && treeIndex >= 0)
        {
            string display = $"[tree {treeIndex}] {name} (condensed node)";
            if (_graph.ConduitTargets.TryGetValue(treeIndex, out var cTargets) && cTargets.Count > 0)
            {
                var resolved = string.Join(", ", cTargets
                    .Select(ti => NodeName(_graph.Nodes.FirstOrDefault(n => n.TreeIndex == ti))));
                display = $"[tree {treeIndex}] {(string.IsNullOrEmpty(name) ? resolved : name)} (condensed → {resolved})";
            }
            opts.Add(new TargetOption
            {
                Id = -1,
                TreeIndex = treeIndex,
                Name = name,
                Display = display
            });
        }
        else if (nodeId >= 0 && NodeById(nodeId) is { } cur && cur.IsRedirect)
        {
            int finalId = _graph.ResolveRedirectFinal(nodeId);
            var finalNode = finalId >= 0 ? NodeById(finalId) : null;
            string idx = treeIndex >= 0 ? treeIndex.ToString()
                : cur.TreeIndex >= 0 ? cur.TreeIndex.ToString() : "new";
            opts.Add(new TargetOption
            {
                Id = nodeId,
                TreeIndex = treeIndex,
                Name = name,
                Display = finalNode != null
                    ? $"[{idx}] {NodeName(finalNode)} (via redirect)"
                    : $"[{idx}] (dead redirect - target missing)"
            });
        }

        foreach (var n in _graph.Nodes
            .Where(n => !n.IsRoot && !n.IsRedirect
                && (!string.IsNullOrEmpty(n.AnimPath) || n.IsActionNode || n.IsEventNode)
                // Beginner view: AI targets are not choosable (rows targeting them
                // are hidden, so no visible combo can lose its current selection).
                && (_showAiNodes || !(n.IsActionNode || n.IsEventNode)))
            .OrderBy(n => n.TreeIndex < 0 ? int.MaxValue : n.TreeIndex)
            .ThenBy(n => NodeName(n), StringComparer.OrdinalIgnoreCase))
        {
            string idx = n.TreeIndex >= 0 ? n.TreeIndex.ToString() : "new";
            string chain = ChainShort(n.Id);
            opts.Add(new TargetOption
            {
                Id = n.Id,
                TreeIndex = n.TreeIndex,
                Name = NodeName(n),
                Display = $"[{idx}] {NodeName(n)}" + (chain.Length > 0 ? $" → {chain}" : "")
            });
        }

        // Safety net: a stored target that matched no option above (e.g. a node
        // missing from the graph, or an AI node while beginner view is active on a
        // row that still renders) must not blank the combo - render it as-is.
        if ((nodeId >= 0 || treeIndex >= 0)
            && !opts.Any(o => o.Id == nodeId
                && (nodeId >= 0 || o.TreeIndex == treeIndex)))
        {
            string idx = treeIndex >= 0 ? treeIndex.ToString() : "new";
            opts.Insert(0, new TargetOption
            {
                Id = nodeId,
                TreeIndex = treeIndex,
                Name = name,
                Display = $"[{idx}] {name} (current target - not choosable)"
            });
        }
        return opts;
    }

    private bool IsAttackNode(ComboNode n)
        => !n.IsRoot && !n.IsRedirect
           && (!string.IsNullOrEmpty(n.AnimPath) || n.IsActionNode || n.IsEventNode);

    private static string NodeName(ComboNode? n)
        => n == null ? "?"
            : !string.IsNullOrEmpty(n.DisplayName) ? n.DisplayName
            : !string.IsNullOrEmpty(n.Name) ? n.Name
            : $"#{n.Id}";

    private string ChainShort(int nodeId)
    {
        // Beginner view: only name visible successors (AI next-nodes are hidden).
        var outs = _graph.Edges.Where(e => e.FromNodeId == nodeId
            && !IsHiddenAiNodeId(e.ToNodeId)).ToList();
        if (outs.Count == 0) return "";
        return string.Join(", ", outs.Take(2).Select(e =>
            NodeName(_graph.Nodes.FirstOrDefault(n => n.Id == e.ToNodeId))));
    }

    private string ChainPreview(int nodeId, int treeIndex, string name)
    {
        if (nodeId < 0)
        {
            if (treeIndex < 0) return "(no target yet)";
            // Condensed conduit target: show where the passthrough actually routes.
            string via = _graph.ConduitTargets.TryGetValue(treeIndex, out var cTargets) && cTargets.Count > 0
                ? " → " + string.Join(", ", cTargets.Select(ti =>
                    NodeName(_graph.Nodes.FirstOrDefault(n => n.TreeIndex == ti))))
                : "";
            return $"tree[{treeIndex}] {(string.IsNullOrEmpty(name) ? "" : name + " ")}(condensed node{via}, kept as tree index)";
        }

        var n = _graph.Nodes.FirstOrDefault(x => x.Id == nodeId);
        if (n == null) return "";
        if (n.IsRedirect)
        {
            int finalId = _graph.ResolveRedirectFinal(nodeId);
            var tgt = finalId >= 0 ? _graph.Nodes.FirstOrDefault(x => x.Id == finalId) : null;
            return tgt != null ? $"redirects → {NodeName(tgt)}" : "redirects → (dead redirect - target missing)";
        }
        var rawOuts = _graph.Edges.Where(e => e.FromNodeId == nodeId).ToList();
        // Beginner view: distinguish "links exist but all go to hidden AI nodes" from
        // a genuinely terminal node.
        string preview;
        if (!_showAiNodes && rawOuts.Count > 0
            && rawOuts.All(e => IsHiddenAiNodeId(e.ToNodeId)))
        {
            preview = "→ (AI targets hidden)";
        }
        else
        {
            var outs = rawOuts.Where(e => !IsHiddenAiNodeId(e.ToNodeId)).ToList();
            if (outs.Count == 0) preview = "→ (no outgoing links - ends the combo)";
            else
            {
                var names = outs.Take(3).Select(e =>
                    NodeName(_graph.Nodes.FirstOrDefault(x => x.Id == e.ToNodeId))
                    + (string.IsNullOrEmpty(e.InputName) ? "" : $" [{e.InputName}]"));
                preview = "→ " + string.Join(", ", names) + (outs.Count > 3 ? " …" : "");
            }
        }
        // A target nothing links to can only be entered from this container. Say so
        // instead of leaving the row looking unfinished - the row's own input column
        // says which button fires it.
        bool hasIncoming = _graph.Edges.Any(e => e.ToNodeId == nodeId);
        return hasIncoming ? preview : "start entry only · " + preview;
    }

    private void AddEntry_Click(object sender, RoutedEventArgs e)
    {
        if (cmbTicket.SelectedItem is not TicketOption opt)
        {
            txtAuthorHint.Text = _isMainCharGraph ? "Pick a start state first." : "Pick a ticket first.";
            txtAuthorHint.Foreground = RedBrush;
            return;
        }

        var label = txtEntryLabel.Text.Trim();
        if (label.Length == 0) label = opt.Label;

        if (_entries.Any(en => !en.Removed && en.TicketEnumValue == opt.Value))
        {
            txtAuthorHint.Text = $"Ticket {opt.EnumValue} · {opt.Label} already has an entry - use + Add Transition on it instead.";
            txtAuthorHint.Foreground = RedBrush;
            return;
        }
        if (_entries.Any(en => !en.Removed && string.Equals(en.Label, label, StringComparison.Ordinal)))
        {
            txtAuthorHint.Text = $"Label '{label}' is already used - labels must be unique (export matches entries by label).";
            txtAuthorHint.Foreground = RedBrush;
            return;
        }

        _entries.Add(new StartEntry
        {
            VanillaIndex = -1,
            Label = label,
            TicketEnumValue = opt.Value,
            ConditionClass = "",
            PhaseName = "None",
            MCDominationFlags = 15,
            Transitions = new List<StartTransition>
            {
                new()
                {
                    OriginalIndex = -1,
                    Probability = 100,
                    // Explicit None: the exported entry clones a template row that may
                    // carry an input (e.g. Light), and "" would keep that clone's value.
                    InputEnum = InputCatalog.None.EnumName,
                    KeyByte = 255,
                    KeyCount = 1,
                    TargetNodeId = -1,
                    TargetTreeIndex = -1
                }
            }
        });
        txtEntryLabel.Text = "";
        txtAuthorHint.Text = $"Added entry '{label}' - pick a target for its transition.";
        txtAuthorHint.Foreground = GreenBrush;
        Rebuild();
    }

    private void DeleteEntry(StartEntry entry)
    {
        if (entry.VanillaIndex >= 0) entry.Removed = true;
        else _entries.Remove(entry);
        txtAuthorHint.Text = $"Deleted entry '{entry.Label}'.";
        txtAuthorHint.Foreground = WarnBrush;
        Rebuild();
    }

    /// <summary>Restores one entry to its vanilla baseline: un-deletes removed rows,
    /// drops new rows, and reverts every surviving row's probability/targets/keys
    /// from the Original* parse-time snapshots (per-key lists included).</summary>
    private void ResetEntry(StartEntry entry)
    {
        entry.Removed = false;
        entry.Transitions.RemoveAll(t => t.OriginalIndex < 0);
        foreach (var t in entry.Transitions)
        {
            t.Probability = t.OriginalProbability;
            t.InputEnum = t.OriginalInputEnum;
            t.TargetNodeId = t.OriginalTargetNodeId;
            t.TargetTreeIndex = t.OriginalTargetTreeIndex;
            t.TargetName = t.OriginalTargetName;
            t.Removed = false;
            // Per-key restore only when the key list itself is intact - a row whose
            // keys were trimmed by node deletion can't safely re-grow to the full
            // original list (the parallel lists would misalign).
            if (t.KeyCount > 1
                && t.Keys.Count == t.KeyCount
                && t.OriginalKeyTargetTreeIndices.Count == t.KeyCount)
            {
                t.KeyTargetTreeIndices = new List<int>(t.OriginalKeyTargetTreeIndices);
                t.KeyTargetNodeIds = t.KeyTargetTreeIndices
                    .Select(ti => _graph.Nodes.FirstOrDefault(n => n.TreeIndex == ti)?.Id ?? -1)
                    .ToList();
                t.KeyTargetNames = t.KeyTargetTreeIndices
                    .Select(ti => NodeName(_graph.Nodes.FirstOrDefault(n => n.TreeIndex == ti)))
                    .ToList();
                t.TargetSummary = string.Join(", ",
                    t.Keys.Zip(t.KeyTargetTreeIndices, (k2, v2) => $"{k2}→{v2}"));
            }
        }
        txtAuthorHint.Text = $"Reset '{entry.Label}' to vanilla.";
        txtAuthorHint.Foreground = GreenBrush;
        Rebuild();
    }

    private void AddTransition(StartEntry entry)
    {
        // Export clones this entry's template row (lowest live vanilla index) for added
        // rows, so inherit that row's profile - the badge and the written row agree even
        // when vanilla row 1 was deleted. A previous added row may come first in the
        // list, so CloneTemplateRow sorts by OriginalIndex (added rows last).
        var template = entry.CloneTemplateRow();
        if (template == null) return;
        // A group already at 100% leaves a 0% row valid but dead, so point the author at
        // Even split / Σ→100% (both are group-aware on player trees).
        float groupSum = UsesGroupSums
            ? entry.Transitions.Where(t => !t.Removed
                && t.ProbabilityGroupKey == template.ProbabilityGroupKey).Sum(t => t.Probability)
            : LiveSum(entry);
        bool alreadyFull = Math.Abs(groupSum - 100f) <= 0.5f;
        // A conditioned template is duplicated verbatim: same condition instance, same
        // full key map and the same per-key targets at 0%. The author then retargets the
        // outcomes they care about and splits the probability. Copying the real key set
        // matters because condition keys are not boolean - vanilla ships maps of 1-5
        // keys, and the key bytes come from the instance, never from its class.
        // Unconditional (key 255) templates keep the old single-key shape, and a legacy
        // multi-key row with no per-key model is collapsed by export to {255} - inheriting
        // a key set the export cannot write would only mislead. Saves made before
        // conditions became per-row keep the class on the entry, so the entry's class is
        // the fallback that keeps a legacy conditioned row from collapsing.
        var added = StartEntry.CreateAddedTransition(template, entry.ConditionClass);
        entry.Transitions.Add(added);
        _focusNewIn = entry;
        Rebuild();
        string copied = added.ConditionClass.Length > 0
            ? added.KeyCount > 1
                ? $"New row copies {added.ConditionClass}'s {added.KeyCount} condition outcome(s) "
                    + "at 0% - retarget each key, then "
                : $"New row copies {added.ConditionClass}'s condition and key {added.KeyByte} "
                    + "at 0% - pick a new target, then "
            : "";
        if (alreadyFull)
        {
            string where = UsesGroupSums ? "its input pool" : $"'{entry.Label}'";
            txtAuthorHint.Text = copied.Length > 0
                ? copied + $"the {where} already totals 100% - "
                    + "click Even split or Σ→100% or it will never be picked."
                : $"New row added at 0% and {where} already totals 100% - "
                    + "click Even split or Σ→100% or it will never be picked.";
            txtAuthorHint.Foreground = WarnBrush;
        }
        else if (copied.Length > 0)
        {
            txtAuthorHint.Text = copied + "click Even split (or Σ→100%) to give it probability.";
            txtAuthorHint.Foreground = GreenBrush;
        }
    }

    private void RemoveTransition(StartEntry entry, StartTransition t)
    {
        if (entry.Transitions.Count(x => !x.Removed) <= 1) return;
        if (t.OriginalIndex >= 0) t.Removed = true;
        else entry.Transitions.Remove(t);
        Rebuild();
    }

    /// <summary>Rows for one input are one selection group: the engine picks one of
    /// them when that input fires. Player entries key probability per input (vanilla
    /// Idle ships 100% on its Light row and 100% on its Heavy row, and node 1 splits
    /// three Light rows 33/34/33), so sums, warnings and rebalancing follow inputs
    /// there; enemy tickets are one group per entry. First-seen order keeps the header
    /// chips in row order.</summary>
    private static List<(string Key, List<StartTransition> Rows)> LiveInputGroups(StartEntry entry)
        => entry.Transitions
            .Where(t => !t.Removed)
            .GroupBy(t => t.ProbabilityGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.ToList()))
            .ToList();

    private static List<List<StartTransition>> LiveGroups(StartEntry entry)
        => LiveInputGroups(entry).Select(g => g.Rows).ToList();

    /// <summary>Player trees sum/rebalance per input, enemy trees per entry.</summary>
    private bool UsesGroupSums => _isMainCharGraph;

    /// <summary>True when the entry's probability makeup is valid: every player input
    /// pool totals 100% (or the whole entry, for enemy tickets).</summary>
    private bool EntrySumOk(StartEntry entry)
    {
        var live = entry.Transitions.Where(t => !t.Removed).ToList();
        if (live.Count == 0) return true;
        if (!UsesGroupSums) return Math.Abs(live.Sum(t => t.Probability) - 100f) <= 0.5f;
        return LiveGroups(entry).All(g => Math.Abs(g.Sum(t => t.Probability) - 100f) <= 0.5f);
    }

    private static List<List<StartTransition>> SelectionGroups(StartEntry entry, bool perGroup)
        => perGroup
            ? LiveGroups(entry)
            : new List<List<StartTransition>> { entry.Transitions.Where(t => !t.Removed).ToList() };

    private static void NormalizeTo100(StartEntry entry, bool perGroup)
    {
        foreach (var group in SelectionGroups(entry, perGroup))
            NormalizeGroup(group);
    }

    private static void NormalizeGroup(List<StartTransition> live)
    {
        if (live.Count == 0) return;
        float sum = live.Sum(t => t.Probability);
        if (sum <= 0f)
        {
            EvenSplitGroup(live);
            return;
        }
        float factor = 100f / sum;
        foreach (var t in live) t.Probability *= factor;
        float end = live.Sum(t => t.Probability);
        var largest = live.OrderByDescending(t => t.Probability).First();
        largest.Probability += 100f - end;
    }

    private static void EvenSplit(StartEntry entry, bool perGroup)
    {
        foreach (var group in SelectionGroups(entry, perGroup))
            EvenSplitGroup(group);
    }

    private static void EvenSplitGroup(List<StartTransition> live)
    {
        if (live.Count == 0) return;
        float each = 100f / live.Count;
        foreach (var t in live) t.Probability = each;
    }

    private static float LiveSum(StartEntry entry)
        => entry.Transitions.Where(t => !t.Removed).Sum(t => t.Probability);

    /// <summary>True when any live row is new or its probability differs from the
    /// vanilla value - the OK gate validates sums only for entries the user actually
    /// touched, so vanilla oddities (e.g. a 200% total) never block.</summary>
    private static bool ProbabilitiesTouched(StartEntry entry)
        => entry.Transitions.Any(t => !t.Removed
            && (t.OriginalIndex < 0 || Math.Abs(t.Probability - t.OriginalProbability) > 0.001f));

    private void RefreshEntryHeader(StartEntry entry, TextBlock sumText, Button normBtn, Button evenBtn)
    {
        float sum = LiveSum(entry);
        int live = entry.Transitions.Count(t => !t.Removed);
        sumText.Inlines.Clear();
        if (live == 0)
        {
            // Vanilla-empty entry (some units ship tickets with an empty
            // m_Transitions array) - not an error, nothing to rebalance.
            sumText.Text = "no transitions (vanilla)";
            sumText.Foreground = GrayBrush;
            sumText.ToolTip = "Vanilla ships this ticket with an empty transitions array. "
                + "The entry is kept as-is on export.";
            normBtn.IsEnabled = false;
            evenBtn.IsEnabled = false;
            return;
        }
        bool ok = EntrySumOk(entry);
        // Only flag a non-100 total once this entry's probabilities were actually edited.
        // Player entries are keyed per input (vanilla sits at 100 on every input, e.g.
        // 300% across three inputs), so untouched totals are correct data, not a mistake.
        bool touched = ProbabilitiesTouched(entry);
        if (UsesGroupSums)
        {
            var groups = LiveInputGroups(entry);
            var tips = new List<string>();
            for (int i = 0; i < groups.Count; i++)
            {
                var (key, rows) = groups[i];
                float groupSum = rows.Sum(t => t.Probability);
                bool groupOk = Math.Abs(groupSum - 100f) <= 0.5f;
                string label = InputCatalog.GroupLabel(key);
                sumText.Inlines.Add(new Run($"{label} {groupSum:0.###}%{(groupOk ? "" : " ⚠")}")
                {
                    Foreground = groupOk ? GreenBrush : touched ? RedBrush : GrayBrush,
                    FontWeight = FontWeights.SemiBold
                });
                if (i < groups.Count - 1)
                    sumText.Inlines.Add(new Run("  ·  ") { Foreground = DimBrush });
                string values = string.Join(" + ", rows.Select(t =>
                    t.OriginalIndex >= 0 ? $"{t.Probability:0.###}" : $"{t.Probability:0.###} (new)"));
                tips.Add($"{label} ({rows.Count} row{(rows.Count == 1 ? "" : "s")}): "
                    + $"{values} = {groupSum:0.###}%{(groupOk ? " ✓" : " ✗")}");
            }
            sumText.Inlines.Add(new Run($"   ·   {live} row{(live == 1 ? "" : "s")}")
            {
                Foreground = DimBrush
            });
            sumText.ToolTip = string.Join("\n", tips)
                + (ok
                    ? "\nEvery input totals 100% - export writes exactly these values."
                    : touched
                        ? "\nEdited inputs must total 100% before OK."
                        : $"\nUnchanged vanilla total of {sum:0.###}% across {groups.Count} "
                          + "input pool(s) - untouched entries are never rebalanced or blocked.");
            normBtn.IsEnabled = !ok;
            evenBtn.IsEnabled = true;
            return;
        }
        sumText.Text = $"Σ {sum:0.###}%  ·  {live} row{(live == 1 ? "" : "s")}";
        sumText.Foreground = ok ? GreenBrush : touched ? RedBrush : GrayBrush;
        sumText.ToolTip = ok
            ? "Probabilities sum to 100% (vanilla pattern)."
            : touched
                ? $"Probabilities sum to {sum:0.###}% - edited entries must total 100% before OK."
                : $"Unchanged vanilla total of {sum:0.###}% across {live} rows - "
                  + "untouched entries are never rebalanced or blocked.";
        normBtn.IsEnabled = !ok;
        evenBtn.IsEnabled = live > 0;
    }

    private void RefreshGlobalStatus()
    {
        var liveEntries = _entries.Where(e => !e.Removed).ToList();
        int noTarget = liveEntries.Sum(e => e.Transitions.Count(t =>
            !t.Removed && t.TargetNodeId < 0 && t.TargetTreeIndex < 0));
        // Vanilla-empty entries (0 transitions, kept as-is on export) are excluded
        // from the sum warning - their Σ is legitimately 0, not a user edit. Entries the
        // user did not touch keep their vanilla total too (player trees key rows per input
        // and sit at 100 each), so only edited entries can trip the warning.
        int badSum = liveEntries.Count(e =>
            e.Transitions.Count(t => !t.Removed) > 0
            && ProbabilitiesTouched(e)
            && !EntrySumOk(e));
        int untouchedOdd = liveEntries.Count(e =>
            e.Transitions.Count(t => !t.Removed) > 0
            && !ProbabilitiesTouched(e)
            && !EntrySumOk(e));
        int edited = _entries.Sum(e => e.Removed ? 1 : e.Transitions.Count(t => t.IsModified()));
        // Rows the user just added start at 0%; on an input pool that already totals 100
        // that keeps the total valid, so only this counter surfaces the dead row.
        int deadNew = liveEntries.Sum(e => e.Transitions.Count(t =>
            !t.Removed && t.OriginalIndex < 0 && t.Probability <= 0.001f));
        int added = liveEntries.Count(e => e.VanillaIndex < 0);
        int deleted = _entries.Count(e => e.Removed);
        string delta = added + deleted > 0
            ? $" · {added} new entr{(added == 1 ? "y" : "ies")}, {deleted} deleted"
            : "";
        string oddSuffix = untouchedOdd > 0
            ? $" · {untouchedOdd} vanilla entr{(untouchedOdd == 1 ? "y keeps" : "ies keep")} its own total"
            : "";

        if (noTarget > 0)
        {
            txtStatus.Text = $"⚠ {noTarget} new transition{(noTarget == 1 ? "" : "s")} still need a target before OK.{delta}";
            txtStatus.Foreground = RedBrush;
        }
        else if (badSum > 0)
        {
            string scope = UsesGroupSums ? " per input" : "";
            txtStatus.Text = $"⚠ {badSum} edited entr{(badSum == 1 ? "y does" : "ies do")} not total 100%{scope} - "
                + $"fix before OK. {edited} row(s) edited.{delta}{oddSuffix}";
            txtStatus.Foreground = WarnBrush;
        }
        else if (deadNew > 0)
        {
            txtStatus.Text = $"⚠ {deadNew} new transition{(deadNew == 1 ? " is" : "s are")} at 0% and "
                + $"will never fire - click Even split or Σ→100%. {edited} row(s) edited.{delta}{oddSuffix}";
            txtStatus.Foreground = WarnBrush;
        }
        else
        {
            txtStatus.Text = (edited > 0
                ? $"{edited} row(s) edited · targets are limited to attack nodes already in this moveset.{delta}"
                : "No changes yet. Targets are limited to attack nodes already in this moveset.") + oddSuffix;
            txtStatus.Foreground = GrayBrush;
        }
    }

    private static string FormatProb(float v)
        => Math.Abs(v - Math.Round(v)) < 0.0001f
            ? ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool Same(List<StartEntry> a, List<StartEntry> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].VanillaIndex != b[i].VanillaIndex) return false;
            if (a[i].Removed != b[i].Removed) return false;
            if (!string.Equals(a[i].Label, b[i].Label, StringComparison.Ordinal)) return false;
            if (!string.Equals(a[i].TicketEnumValue, b[i].TicketEnumValue, StringComparison.Ordinal)) return false;
            var ta = a[i].Transitions;
            var tb = b[i].Transitions;
            if (ta.Count != tb.Count) return false;
            for (int j = 0; j < ta.Count; j++)
            {
                if (ta[j].Removed != tb[j].Removed || ta[j].OriginalIndex != tb[j].OriginalIndex) return false;
                if (Math.Abs(ta[j].Probability - tb[j].Probability) > 0.0001f) return false;
                if (!string.Equals(InputCatalog.Normalize(ta[j].InputEnum),
                        InputCatalog.Normalize(tb[j].InputEnum), StringComparison.Ordinal)) return false;
                if (ta[j].TargetNodeId != tb[j].TargetNodeId) return false;
                if (ta[j].TargetTreeIndex != tb[j].TargetTreeIndex) return false;
                if (!ta[j].Keys.SequenceEqual(tb[j].Keys)) return false;
                if (!ta[j].KeyTargetTreeIndices.SequenceEqual(tb[j].KeyTargetTreeIndices)) return false;
                if (!ta[j].KeyTargetNodeIds.SequenceEqual(tb[j].KeyTargetNodeIds)) return false;
            }
        }
        return true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _entries.Where(en => !en.Removed))
        {
            foreach (var t in entry.Transitions.Where(t => !t.Removed))
            {
                if (t.KeyCount > 1 && HasKeyModel(t))
                {
                    // Multi-key rows route per condition outcome; the single-target
                    // fields are unused (and stay -1 on a verbatim clone).
                    for (int ki = 0; ki < t.KeyCount; ki++)
                    {
                        if (t.KeyTargetNodeIds[ki] < 0 && t.KeyTargetTreeIndices[ki] < 0)
                        {
                            txtStatus.Text = $"'{entry.Label}': pick a target for key {t.Keys[ki]} first.";
                            txtStatus.Foreground = RedBrush;
                            return;
                        }
                    }
                }
                else if (t.TargetNodeId < 0 && t.TargetTreeIndex < 0)
                {
                    txtStatus.Text = $"'{entry.Label}': pick a target for the new transition first.";
                    txtStatus.Foreground = RedBrush;
                    return;
                }
            }
        }

        // OK gate: touched entries must total 100% - per entry for enemy tickets, per
        // input for player trees (vanilla Idle is 100% on its Light row and 100% on its
        // Heavy row, so a per-entry 200% total is correct). Only probability-edited or
        // new rows count as touched, so untouched vanilla entries never block. One
        // MessageBox lists every offender.
        var touchedBad = new List<string>();
        foreach (var entry in _entries.Where(en => !en.Removed))
        {
            if (!ProbabilitiesTouched(entry)) continue;
            string name = string.IsNullOrEmpty(entry.Label) ? $"Entry {entry.VanillaIndex + 1}" : entry.Label;
            if (!UsesGroupSums)
            {
                float sum = LiveSum(entry);
                if (Math.Abs(sum - 100f) <= 0.5f) continue;
                touchedBad.Add($"  {name}: {sum:0.###}%");
                continue;
            }
            foreach (var (key, group) in LiveInputGroups(entry))
            {
                float groupSum = group.Sum(t => t.Probability);
                if (Math.Abs(groupSum - 100f) <= 0.5f) continue;
                touchedBad.Add($"  {name} · {InputCatalog.GroupLabel(key)}: {groupSum:0.###}%");
            }
        }
        if (touchedBad.Count > 0)
        {
            txtStatus.Text = UsesGroupSums
                ? "Fix start-entry probabilities (each input must total 100%) before saving."
                : "Fix start-entry probabilities (must sum to 100%) before saving.";
            txtStatus.Foreground = RedBrush;
            MessageBox.Show(this,
                (UsesGroupSums
                    ? "These start entries were edited but some input pools don't total 100%:\n\n"
                    : "These start entries were edited but their probabilities don't sum to 100%:\n\n")
                + string.Join("\n", touchedBad)
                + (UsesGroupSums
                    ? "\n\nPlayer entries roll one starter per input (LMB/RMB/…), so all rows with the same "
                      + "input must total 100%. Use Σ→100% or Even split on the entry, then press OK again. "
                    : "\n\nUse Σ→100% or Even split on each, then press OK again. ")
                + "Untouched entries are never blocked.",
                "Start entry probabilities", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Same(_graph.StartEntries, _entries))
        {
            DialogResult = false;
            return;
        }
        Result = _entries;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
