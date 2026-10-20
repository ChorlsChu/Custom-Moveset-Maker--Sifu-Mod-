using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SifuMovesetEditor;

/// <summary>
/// Advanced per-window editor for an attack card's m_HitBoxes: frame bounds (card),
/// damage / structure / knockback / stun / freeze (m_OnHitImpact on the HitBoxData
/// row) and the row's gameplay bools. Edits apply to a working copy - OK commits,
/// Cancel discards; empty field / unchecked-equals-vanilla checkbox = keep vanilla.
/// Each field label carries a "?" that opens a short help overlay.
/// </summary>
public partial class AttackDbEditorDialog : Window
{
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x6c, 0x70, 0x86));
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0xa6, 0xad, 0xc8));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xcd, 0xd6, 0xf4));
    private static readonly Brush BlueBrush = new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xf9, 0xe2, 0xaf));
    private static readonly Brush HeadBrush = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));

    private readonly AttackHitCardData _card;
    private readonly List<AttackHitEdit> _initial;
    private readonly List<ArmorNotifyInfo>? _armor;

    private sealed class WindowFields
    {
        public AttackHitWindowInfo Info = null!;
        public TextBox FrameLo = null!;
        public TextBox FrameHi = null!;
        public TextBox Damage = null!;
        public TextBox Structure = null!;
        // Per-impact fields shown in the Damage/Structure edit popups. These
        // are the source of truth; the mirror textbox (Damage / StructureDamage)
        // stays two-way synced to them. Empty field = keep vanilla for that path.
        public TextBox DamageOnHit = null!;
        public TextBox DamageOnGuard = null!;
        public TextBox DamageOnDeflect = null!;
        public TextBox StructureOnHit = null!;
        public TextBox StructureOnGuard = null!;
        public TextBox StructureOnDeflect = null!;
        public TextBox Knockback = null!;
        public TextBox SuperDizzy = null!;
        public TextBox Stun = null!;
        public TextBox Freeze = null!;
        public ComboBox Reaction = null!;
        public CheckBox Parriable = null!;
        public CheckBox Lethal = null!;
        public CheckBox BreakStructure = null!;
        public CheckBox DizzyParry = null!;
        public CheckBox DizzyAvoid = null!;
    }

    /// <summary>One selectable Hit reaction preset; the dropdown writes power + property level together.</summary>
    private sealed class ReactionOption
    {
        // Must be properties: DisplayMemberPath binds via reflection and only
        // resolves CLR properties - public fields render blank (white-on-white).
        public string Label { get; set; } = "";
        public string Power { get; set; } = "";
        public int Level { get; set; }
        public override string ToString() => Label;
    }

    /// <summary>Vanilla-QA'd power + property-level combos only; unlisted mixes are never shipped.</summary>
    private static readonly ReactionOption[] ReactionPresets =
    {
        new() { Label = "Light", Power = "Light", Level = 0 },
        new() { Label = "Medium", Power = "Medium", Level = 0 },
        new() { Label = "Strong", Power = "Strong", Level = 0 },
        new() { Label = "Light knockdown", Power = "Strong", Level = 1 },
        new() { Label = "Knockdown", Power = "Strong", Level = 3 },
        new() { Label = "Heavy knockdown", Power = "Strong", Level = 4 },
        new() { Label = "Launch / special", Power = "Strong", Level = 15 },
    };

    private readonly List<WindowFields> _fields = new();

    /// <summary>
    /// Armor / Resilience section in the Hit Windows dialog. Implemented and verified, but
    /// hidden until the next major update - flip to true to re-enable (also gates the anim
    /// read in MainWindow.AttackHitWindows_Click).
    /// </summary>
    internal static readonly bool ShowArmorSection = false;

    /// <summary>Committed edits (pruned, vanilla-equal fields nulled). Null = no changes.</summary>
    public List<AttackHitEdit>? Result { get; private set; }

    /// <summary>Display-only constructor (no armor section); delegates with a null armor list.</summary>
    public AttackDbEditorDialog(AttackHitCardData card, List<AttackHitEdit>? current)
        : this(card, current, null) { }

    /// <param name="armor">
    /// The attack anim's OrderServiceNotifyState resilience entries (null = anim unreadable,
    /// empty = no armor). Display-only - Phase 1 never writes these back.
    /// </param>
    public AttackDbEditorDialog(AttackHitCardData card, List<AttackHitEdit>? current,
        List<ArmorNotifyInfo>? armor)
    {
        InitializeComponent();
        _card = card;
        _initial = current ?? new List<AttackHitEdit>();
        _armor = armor;

        string cardName = System.IO.Path.GetFileNameWithoutExtension(card.CardRel);
        txtSubtitle.Text = $"{cardName} · {card.Windows.Count} window(s)";

        BuildWindows();

        var shared = card.Windows.Where(w => w.SharedRow).Select(w => w.RowName).Distinct().ToList();
        if (shared.Count > 0)
        {
            txtSharedWarning.Text = "Shared row warning: these rows are not named after this attack and "
                + "may be used by other attacks in the same family table - edits affect every user: "
                + string.Join(", ", shared);
            txtSharedWarning.Visibility = Visibility.Visible;
        }
    }

    private void BuildWindows()
    {
        if (ShowArmorSection) BuildArmorSection();
        foreach (var w in _card.Windows)
        {
            var edit = _initial.FirstOrDefault(e => e.Index == w.Index);
            var fields = new WindowFields { Info = w };

            var root = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x1b)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var panel = new StackPanel();

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var reset = new Button
            {
                Content = "Reset",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 10,
                Tag = fields
            };
            reset.Click += ResetWindow_Click;
            DockPanel.SetDock(reset, Dock.Right);
            header.Children.Add(reset);

            var title = new TextBlock
            {
                Text = $"Hit {w.Index + 1} — {w.RowName} ({w.FrameLo}–{w.FrameHi})"
                    + (string.IsNullOrEmpty(w.Movement) ? "" : $" · {w.Movement}"),
                Foreground = w.SharedRow ? WarnBrush : BlueBrush,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            header.Children.Add(title);
            if (w.LookupStatus != VanillaLookupStatus.Ok)
            {
                header.Children.Add(new TextBlock
                {
                    Text = MissingVanillaHint(w),
                    Foreground = WarnBrush,
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                });
            }
            panel.Children.Add(header);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < 14; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            fields.FrameLo = MakeBox();
            fields.FrameHi = MakeBox();
            fields.Damage = MakeBox();
            fields.Structure = MakeBox();
            fields.DamageOnHit = MakeBox();
            fields.DamageOnGuard = MakeBox();
            fields.DamageOnDeflect = MakeBox();
            fields.StructureOnHit = MakeBox();
            fields.StructureOnGuard = MakeBox();
            fields.StructureOnDeflect = MakeBox();
            fields.Knockback = MakeBox();
            fields.SuperDizzy = MakeBox();
            fields.Stun = MakeBox();
            fields.Freeze = MakeBox();
            fields.Reaction = MakeCombo();
            fields.Parriable = MakeCheck();
            fields.Lethal = MakeCheck();
            fields.BreakStructure = MakeCheck();
            fields.DizzyParry = MakeCheck();
            fields.DizzyAvoid = MakeCheck();

            InputFilter.ApplyInt(fields.FrameLo);
            InputFilter.ApplyInt(fields.FrameHi);
            // Mirror boxes accept "50" or "hit,guard,deflect" (max 2 commas).
            InputFilter.ApplyFloatTriplet(fields.Damage);
            InputFilter.ApplyFloatTriplet(fields.Structure);
            InputFilter.ApplyFloat(fields.DamageOnHit);
            InputFilter.ApplyFloat(fields.DamageOnGuard);
            InputFilter.ApplyFloat(fields.DamageOnDeflect);
            InputFilter.ApplyFloat(fields.StructureOnHit);
            InputFilter.ApplyFloat(fields.StructureOnGuard);
            InputFilter.ApplyFloat(fields.StructureOnDeflect);
            InputFilter.ApplyFloat(fields.Knockback);
            InputFilter.ApplyFloat(fields.SuperDizzy);
            InputFilter.ApplyInt(fields.Stun);
            InputFilter.ApplyInt(fields.Freeze);

            AddIntRow(grid, 0, "Frames lo:", "Frames", w.FrameLo, edit?.FrameLo, fields.FrameLo, info: w);
            AddIntRow(grid, 1, "Frames hi:", "Frames", w.FrameHi, edit?.FrameHi, fields.FrameHi, info: w);
            AddImpactFloatRow(grid, 2, "Damage:", "Damage", w.Damage, edit?.Damage, fields.Damage,
                w.Damage, w.GuardDamage, w.DeflectDamage,
                edit?.DamageOnHit, edit?.DamageOnGuard, edit?.DamageOnDeflect,
                fields.DamageOnHit, fields.DamageOnGuard, fields.DamageOnDeflect, w);
            AddImpactFloatRow(grid, 3, "Structure:", "Structure", w.StructureDamage, edit?.StructureDamage, fields.Structure,
                w.StructureDamage, w.GuardStructureDamage, w.DeflectStructureDamage,
                edit?.StructureDamageOnHit, edit?.StructureDamageOnGuard, edit?.StructureDamageOnDeflect,
                fields.StructureOnHit, fields.StructureOnGuard, fields.StructureOnDeflect, w);
            AddFloatRow(grid, 4, "Knockback:", "Knockback", w.KnockbackDistance, edit?.KnockbackDistance, fields.Knockback, w);
            AddFloatRow(grid, 5, "Super dizzy:", "SuperDizzy", w.SuperDizzyDamage, edit?.SuperDizzyDamage, fields.SuperDizzy, w);
            AddIntRow(grid, 6, "Stun frames:", "StunFrames", w.StunFrames, edit?.StunFrames, fields.Stun, 0, 255, w);
            AddIntRow(grid, 7, "Freeze frames:", "FreezeFrames", w.FreezeFrames, edit?.FreezeFrames, fields.Freeze, 0, 1000, w);
            AddReactionRow(grid, 8, "Hit reaction:", "Reaction", w, edit, fields.Reaction);
            AddBoolRow(grid, 9, "Parriable:", "Parriable", w.Parriable, edit?.Parriable, fields.Parriable, w);
            AddBoolRow(grid, 10, "Lethal:", "Lethal", w.Lethal, edit?.Lethal, fields.Lethal, w);
            AddBoolRow(grid, 11, "Break structure:", "BreakStructure", w.BreakStructure, edit?.BreakStructure, fields.BreakStructure, w);
            AddBoolRow(grid, 12, "Dizzy on parry:", "DizzyOnParry", w.DizzyOnParry, edit?.DizzyOnParry, fields.DizzyParry, w);
            AddBoolRow(grid, 13, "Dizzy on avoid:", "DizzyOnAvoid", w.DizzyOnAvoid, edit?.DizzyOnAvoid, fields.DizzyAvoid, w);

            panel.Children.Add(grid);
            root.Child = panel;
            windowsPanel.Children.Add(root);
            _fields.Add(fields);
        }

        if (_card.Windows.Count == 0)
        {
            windowsPanel.Children.Add(new TextBlock
            {
                Text = "This card has no hit windows.",
                Foreground = DimBrush,
                FontSize = 12
            });
        }
    }

    /// <summary>
    /// Read-only "Armor / Resilience" card at the top of the list: the attack anim's
    /// OrderServiceNotifyState entries (tier + timing in seconds). Phase 1 never edits.
    /// </summary>
    private void BuildArmorSection()
    {
        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x1b)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var panel = new StackPanel();

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6)
        };
        header.Children.Add(new TextBlock
        {
            Text = "Armor / Resilience",
            Foreground = BlueBrush,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var armorHelp = HelpButton("Armor");
        armorHelp.Click += Help_Click;
        header.Children.Add(armorHelp);
        panel.Children.Add(header);

        if (_armor == null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Anim not found / unreadable - armor unknown.",
                Foreground = DimBrush,
                FontSize = 11
            });
        }
        else if (_armor.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "None - this attack is interruptible.",
                Foreground = DimBrush,
                FontSize = 11
            });
        }
        else
        {
            foreach (var a in _armor)
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                row.Children.Add(new TextBlock
                {
                    Text = a.Tier.ToString(),
                    Foreground = TierBrush(a.Tier),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new TextBlock
                {
                    Text = "  " + ArmorTimingText(a),
                    Foreground = GrayBrush,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                });
                panel.Children.Add(row);
            }
            panel.Children.Add(new TextBlock
            {
                Text = "Times in seconds (Sifu anims are 60 fps).",
                Foreground = GrayBrush,
                FontSize = 10,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        root.Child = panel;
        windowsPanel.Children.Add(root);
    }

    private static Brush TierBrush(ArmorTier tier) => tier switch
    {
        ArmorTier.Resilience => WarnBrush,      // yellow - light armor
        ArmorTier.Super => new SolidColorBrush(Color.FromRgb(0xfa, 0xb3, 0x87)), // peach - strong
        ArmorTier.Unstoppable => new SolidColorBrush(Color.FromRgb(0xf3, 0x8b, 0xa8)), // red - hyper
        _ => DimBrush,
    };

    /// <summary>"0.45s → 1.20s (dur 0.75s)" with '?' for absent fields.</summary>
    private static string ArmorTimingText(ArmorNotifyInfo a)
    {
        static string Sec(float? v) => v is null ? "?" : v.Value.ToString("0.###", CultureInfo.InvariantCulture) + "s";
        string dur = a.Duration is null ? "" : $" (dur {Sec(a.Duration)})";
        return $"{Sec(a.StartTime)} → {Sec(a.EndTime)}{dur}";
    }

    private TextBox MakeBox() => new()
    {
        Style = (Style)FindResource("DarkTextBox"),
        Margin = new Thickness(0, 1, 12, 1),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static CheckBox MakeCheck() => new()
    {
        Margin = new Thickness(0, 1, 12, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = TextBrush,
        FontSize = 11
    };

    private ComboBox MakeCombo() => new()
    {
        Style = (Style)FindResource("DarkCombo"),
        Margin = new Thickness(0, 1, 12, 1),
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 11,
        MaxDropDownHeight = 220,
        DisplayMemberPath = nameof(ReactionOption.Label)
    };

    /// <summary>
    /// Single "Hit reaction" dropdown that writes m_eAttackPower + the first
    /// m_AttackPropertyLevel.m_Level together (vanilla always ships them coupled).
    /// Disabled when the row lacks either field; a vanilla combo no preset covers
    /// (modded/unsampled table) gets an exact-match item so it still displays.
    /// </summary>
    private void AddReactionRow(Grid grid, int row, string label, string helpKey,
        AttackHitWindowInfo info, AttackHitEdit? edit, ComboBox combo)
    {
        bool hasBoth = info.AttackPower != null && info.PropertyLevel.HasValue;
        if (hasBoth)
        {
            foreach (var p in ReactionPresets) combo.Items.Add(p);
            if (!ReactionPresets.Any(p => p.Power == info.AttackPower && p.Level == info.PropertyLevel))
                combo.Items.Add(new ReactionOption
                {
                    Label = $"{info.AttackPower} + {info.PropertyLevel} (vanilla)",
                    Power = info.AttackPower!,
                    Level = info.PropertyLevel!.Value
                });

            string curPower = edit?.AttackPower ?? info.AttackPower!;
            int curLevel = edit?.PropertyLevel ?? info.PropertyLevel!.Value;
            combo.SelectedItem = combo.Items.OfType<ReactionOption>()
                .FirstOrDefault(p => p.Power == curPower && p.Level == curLevel);
            combo.IsEnabled = true;
            combo.ToolTip = $"Vanilla: {info.AttackPower} + {info.PropertyLevel} - pick a preset to change";
        }
        else
        {
            combo.IsEnabled = false;
            combo.ToolTip = MissingVanillaTip(info);
        }
        HelpLabel(grid, row, 0, label, helpKey);
        Place(grid, combo, row, 1);
        Hint(grid, row, 2, hasBoth ? $"vanilla {info.AttackPower}+{info.PropertyLevel}" : MissingVanillaHint(info));
    }

    /// <summary>Per-field help texts, shared by every window group (same idea as Unit PropsHelp).</summary>
    private static readonly Dictionary<string, (string Title, string Desc)> FieldHelp = new()
    {
        ["Frames"] = ("Hit frames",
            "Animation frames (60 fps, stored on the card) during which this hit window is active. A hit only connects while the current frame is within this range."),
        ["Damage"] = ("Damage",
            "HP damage per impact path. Type one number to apply it to hit, guard and deflect alike, or type hit,guard,deflect (e.g. 13,0,5) to set each path. The ≡ button opens the per-path editor; an empty path keeps its vanilla value. The game reads OnGuard while the victim is blocking."),
        ["Structure"] = ("Structure damage",
            "Posture/structure damage per impact path. Type one number for all three paths, or hit,guard,deflect to set each. The ≡ button opens the per-path editor; an empty path keeps its vanilla value. The game reads OnGuard while the victim is blocking, so a guarding victim only takes structure damage from the OnGuard path; a full bar staggers them."),
        ["Knockback"] = ("Knockback",
            "How far the victim is pushed back when hit."),
        ["SuperDizzy"] = ("Super dizzy damage",
            "How much this hit fills the victim's dizzy gauge (m_fSuperDizzyDamage). The gauge is hidden; when full the victim becomes super-dizzy and vulnerable to finishers. Vanilla Jab = 15, heavier dizzy moves 23-45. Higher = fills the gauge faster; editing also enables m_bUseSuperDizzyDamage."),
        ["StunFrames"] = ("Stun frames",
            "How long the victim is locked in hit-stun after this window connects (0-255). During stun they cannot act."),
        ["FreezeFrames"] = ("Freeze frames",
            "Hit-stop: both fighters freeze briefly on connect. Pure game-feel, no gameplay effect beyond the pause."),
        ["Reaction"] = ("Hit reaction",
            "Sets this window's attack power (Light/Medium/Strong damage feel) and reaction level together - the two fields vanilla always ships coupled:\n\n"
            + "- Light / Medium / Strong (level 0): normal hits of increasing weight, victim flinches\n"
            + "- Light knockdown (Strong + level 1): drops the victim, e.g. FD spin kick 2nd hit\n"
            + "- Knockdown (Strong + level 3): standard knockdown, e.g. LowKick / spin high kick\n"
            + "- Heavy knockdown (Strong + level 4): heavier drop\n"
            + "- Launch / special (Strong + level 15): thrown/synced-style reactions (grab follow-ups)\n\n"
            + "Vanilla always pairs knockdown levels (1+) with Strong - any other combination was never shipped and is untested. The dropdown keeps the row's vanilla combo unless you pick another preset."),
        ["Parriable"] = ("Parriable",
            "Whether the victim can parry this window. This is per window, not per attack - e.g. QuickHookPunch's opener (frames 26-28) is parriable while its follow-up (48-50) is not."),
        ["Lethal"] = ("Lethal",
            "Whether this hit is allowed to be a killing blow. When off, the hit cannot finish the victim."),
        ["BreakStructure"] = ("Break structure",
            "Whether this hit breaks the victim's guard/structure on connect."),
        ["DizzyOnParry"] = ("Dizzy on parry",
            "When the victim parries this attack, they are put into super-dizzy - the vulnerable dizzy state, not a normal stagger."),
        ["DizzyOnAvoid"] = ("Dizzy on avoid",
            "When the victim avoids/dodges this attack, they are put into super-dizzy."),
        ["Armor"] = ("Armor / Resilience",
            "Attacker-side super armor, carried as an OrderServiceNotifyState on the attack's animation asset (not the hit windows or the AttackDB card). While the notify is active the attacker cannot be interrupted by flinches.\n\n"
            + "Tiers (selected by the notify's m_ServiceClass):\n"
            + "- Resilience: light armor - Grunt / BodyGuard / FlashKick heavies\n"
            + "- Super: strong armor - BigGuy / Fajar / FireDisciple strikes; the player's Resilient skill\n"
            + "- Unstoppable: full hyper armor - Kuroki / Sean / Yang; FalconPunch\n\n"
            + "Some anims stack two notifies (e.g. BigGuy heavies = Super + Regular). Times are seconds on a 60 fps anim; read-only here (editing comes in a later phase).")
    };

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key) return;
        if (!FieldHelp.TryGetValue(key, out var info)) return;
        infoTitle.Text = info.Title;
        infoDesc.Text = info.Desc;
        infoPopup.Visibility = Visibility.Visible;
    }

    private void InfoPopup_Click(object sender, MouseButtonEventArgs e)
    {
        infoPopup.Visibility = Visibility.Collapsed;
    }

    /// <summary>The Unit-Props-style 16x16 "?" button; Tag = FieldHelp key.</summary>
    private Button HelpButton(string key) => new()
    {
        Tag = key,
        Width = 16,
        Height = 16,
        Padding = new Thickness(0),
        Margin = new Thickness(6, 0, 0, 0),
        Cursor = System.Windows.Input.Cursors.Help,
        Content = new TextBlock
        {
            Text = "?",
            FontWeight = FontWeights.Bold,
            Foreground = BlueBrush,
            Background = HeadBrush,
            Width = 16,
            Height = 16,
            TextAlignment = TextAlignment.Center,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private void HelpLabel(Grid grid, int row, int col, string text, string helpKey)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        sp.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = TextBrush,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });
        var btn = HelpButton(helpKey);
        btn.Click += Help_Click;
        sp.Children.Add(btn);
        Grid.SetRow(sp, row);
        Grid.SetColumn(sp, col);
        grid.Children.Add(sp);
    }

    private static void Hint(Grid grid, int row, int col, string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = GrayBrush,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 12, 1),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, col);
        grid.Children.Add(tb);
    }

    private static void Place(Grid grid, FrameworkElement el, int row, int col)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        grid.Children.Add(el);
    }

    private static string InvariantFloat(float v)
        => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Why the vanilla row values are missing, for tooltips (empty when the row loaded fine).</summary>
    private static string MissingVanillaTip(AttackHitWindowInfo? info) => info?.LookupStatus switch
    {
        VanillaLookupStatus.TableNotFound => "HitBoxData table file not found",
        VanillaLookupStatus.RowNotFound => "Row name not in table (stale reference - card is likely an old/unused asset)",
        VanillaLookupStatus.NoRowRef => "Card has no table row reference",
        _ => "Not present in vanilla row",
    };

    /// <summary>Short status text for the hint column (empty when the row loaded fine).</summary>
    private static string MissingVanillaHint(AttackHitWindowInfo? info) => info?.LookupStatus switch
    {
        VanillaLookupStatus.TableNotFound => "table not found",
        VanillaLookupStatus.RowNotFound => "row not in table",
        VanillaLookupStatus.NoRowRef => "no row ref",
        _ => "absent in vanilla",
    };

    private void AddIntRow(Grid grid, int row, string label, string helpKey, int? vanilla, int? edit, TextBox box,
        int min = int.MinValue, int max = int.MaxValue, AttackHitWindowInfo? info = null)
    {
        box.Tag = (min, max);
        if (edit.HasValue) box.Text = edit.Value.ToString(CultureInfo.InvariantCulture);
        box.ToolTip = vanilla.HasValue
            ? $"Vanilla: {vanilla.Value} - leave empty to keep"
            : MissingVanillaTip(info);
        HelpLabel(grid, row, 0, label, helpKey);
        Place(grid, box, row, 1);
        Hint(grid, row, 2, vanilla.HasValue ? $"vanilla {vanilla.Value}" : MissingVanillaHint(info));
    }

    private void AddFloatRow(Grid grid, int row, string label, string helpKey, float? vanilla, float? edit, TextBox box,
        AttackHitWindowInfo? info = null)
    {
        if (edit.HasValue) box.Text = InvariantFloat(edit.Value);
        box.ToolTip = vanilla.HasValue
            ? $"Vanilla: {InvariantFloat(vanilla.Value)} - leave empty to keep"
            : MissingVanillaTip(info);
        HelpLabel(grid, row, 0, label, helpKey);
        Place(grid, box, row, 1);
        Hint(grid, row, 2, vanilla.HasValue ? $"vanilla {InvariantFloat(vanilla.Value)}" : MissingVanillaHint(info));
    }

    /// <summary>
    /// Damage/Structure row: a mirror textbox plus a small edit button that
    /// opens a popup with the three impact paths (On hit / On guard / On
    /// deflect). The popup fields are the source of truth; the mirror box is
    /// two-way synced: a single value writes through to all three paths, a
    /// comma list "hit,guard,deflect" distributes in order, and editing a
    /// popup field re-renders the mirror (collapsing to one number when all
    /// three match). An empty popup field keeps that path's vanilla value.
    /// </summary>
    private void AddImpactFloatRow(Grid grid, int row, string label, string helpKey,
        float? vanilla, float? edit, TextBox mirrorBox,
        float? vanillaHit, float? vanillaGuard, float? vanillaDeflect,
        float? editHit, float? editGuard, float? editDeflect,
        TextBox hitBox, TextBox guardBox, TextBox deflectBox,
        AttackHitWindowInfo? info)
    {
        // Load-time: per-impact override wins, else the mirror value.
        float? effHit = editHit ?? edit;
        float? effGuard = editGuard ?? edit;
        float? effDeflect = editDeflect ?? edit;
        if (effHit.HasValue) hitBox.Text = InvariantFloat(effHit.Value);
        if (effGuard.HasValue) guardBox.Text = InvariantFloat(effGuard.Value);
        if (effDeflect.HasValue) deflectBox.Text = InvariantFloat(effDeflect.Value);
        mirrorBox.Text = FormatMirror(effHit, effGuard, effDeflect);

        string vanillaHint = VanillaTripletHint(vanillaHit, vanillaGuard, vanillaDeflect, vanilla);
        mirrorBox.ToolTip = vanilla.HasValue
            ? $"Vanilla (on hit): {InvariantFloat(vanilla.Value)} - one value applies to all three paths, or type hit,guard,deflect; leave empty to keep"
            : MissingVanillaTip(info);

        var popup = BuildImpactPopup(label.TrimEnd(':'),
            (hitBox, vanillaHit), (guardBox, vanillaGuard), (deflectBox, vanillaDeflect), info);
        var editBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "≡",
                FontSize = 11,
                Foreground = BlueBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 1, 0, 1),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Per-impact editor (On hit / On guard / On deflect) - empty fields keep vanilla"
        };
        editBtn.Click += (_, _) =>
        {
            popup.PlacementTarget = editBtn;
            popup.IsOpen = !popup.IsOpen;
        };

        // Two-way sync between the mirror box and the three popup fields.
        // syncing guards against TextChanged feedback loops.
        bool syncing = false;
        void MirrorToFields()
        {
            if (syncing) return;
            syncing = true;
            try
            {
                string raw = mirrorBox.Text?.Trim() ?? "";
                if (raw.Length == 0)
                {
                    hitBox.Text = guardBox.Text = deflectBox.Text = "";
                    return;
                }
                string[] parts = raw.Split(',');
                if (parts.Length == 1)
                {
                    // Single value: applies to all three paths.
                    hitBox.Text = guardBox.Text = deflectBox.Text = parts[0].Trim();
                }
                else
                {
                    // Comma list: hit,guard,deflect (missing tails = empty = keep vanilla).
                    hitBox.Text = parts[0].Trim();
                    guardBox.Text = parts.Length > 1 ? parts[1].Trim() : "";
                    deflectBox.Text = parts.Length > 2 ? parts[2].Trim() : "";
                }
            }
            finally { syncing = false; }
        }
        void FieldsToMirror()
        {
            if (syncing) return;
            syncing = true;
            try
            {
                mirrorBox.Text = FormatMirror(
                    ParseBox(hitBox), ParseBox(guardBox), ParseBox(deflectBox));
            }
            finally { syncing = false; }
        }
        mirrorBox.TextChanged += (_, _) => MirrorToFields();
        hitBox.TextChanged += (_, _) => FieldsToMirror();
        guardBox.TextChanged += (_, _) => FieldsToMirror();
        deflectBox.TextChanged += (_, _) => FieldsToMirror();

        HelpLabel(grid, row, 0, label, helpKey);
        var dock = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(editBtn, Dock.Right);
        dock.Children.Add(editBtn);
        mirrorBox.Margin = new Thickness(0, 1, 6, 1);
        dock.Children.Add(mirrorBox);
        Place(grid, dock, row, 1);
        Hint(grid, row, 2, vanilla.HasValue ? vanillaHint : MissingVanillaHint(info));
    }

    /// <summary>Mirror display: single number when all three paths match, else "hit,guard,deflect".</summary>
    private static string FormatMirror(float? hit, float? guard, float? deflect)
    {
        if (!hit.HasValue && !guard.HasValue && !deflect.HasValue) return "";
        if (hit.HasValue && guard.HasValue && deflect.HasValue
            && hit.Value == guard.Value && guard.Value == deflect.Value)
            return InvariantFloat(hit.Value);
        return $"{(hit.HasValue ? InvariantFloat(hit.Value) : "")},"
             + $"{(guard.HasValue ? InvariantFloat(guard.Value) : "")},"
             + $"{(deflect.HasValue ? InvariantFloat(deflect.Value) : "")}";
    }

    /// <summary>Parse one popup field; whitespace/empty = null (keep vanilla).</summary>
    private static float? ParseBox(TextBox box)
    {
        string raw = box.Text?.Trim() ?? "";
        return raw.Length == 0
            ? null
            : float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : null;
    }

    /// <summary>Hint text: "vanilla 10 / 0 / 5" (hit / guard / deflect).</summary>
    private static string VanillaTripletHint(float? hit, float? guard, float? deflect, float? fallback)
    {
        static string Part(float? v) => v.HasValue ? InvariantFloat(v.Value) : "?";
        if (!hit.HasValue && !guard.HasValue && !deflect.HasValue)
            return fallback.HasValue ? $"vanilla {InvariantFloat(fallback.Value)}" : "vanilla ?";
        return $"vanilla {Part(hit)} / {Part(guard)} / {Part(deflect)}";
    }

    /// <summary>Small dark popup: three override fields (label / box / vanilla), each empty = keep that path's vanilla.</summary>
    private Popup BuildImpactPopup(string title,
        (TextBox Box, float? Vanilla) hit,
        (TextBox Box, float? Vanilla) guard,
        (TextBox Box, float? Vanilla) deflect,
        AttackHitWindowInfo? info)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        for (int i = 0; i < 3; i++)
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Row(int r, string text, (TextBox Box, float? Vanilla) f)
        {
            f.Box.Width = 52;
            f.Box.ToolTip = f.Vanilla.HasValue
                ? $"Vanilla: {InvariantFloat(f.Vanilla.Value)} - empty = keep vanilla"
                : MissingVanillaTip(info);
            var tb = new TextBlock
            {
                Text = text,
                Foreground = DimBrush,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 6, 1)
            };
            var van = new TextBlock
            {
                Text = f.Vanilla.HasValue ? $"vanilla {InvariantFloat(f.Vanilla.Value)}" : "?",
                Foreground = GrayBrush,
                FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 1, 0, 1)
            };
            Grid.SetRow(tb, r); Grid.SetColumn(tb, 0); g.Children.Add(tb);
            Grid.SetRow(f.Box, r); Grid.SetColumn(f.Box, 1); g.Children.Add(f.Box);
            Grid.SetRow(van, r); Grid.SetColumn(van, 2); g.Children.Add(van);
        }
        Row(0, "On hit", hit);
        Row(1, "On guard", guard);
        Row(2, "On deflect", deflect);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = BlueBrush,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(g);
        panel.Children.Add(new TextBlock
        {
            Text = "Empty = keep vanilla for that path.\nGuard applies while the victim blocks.",
            Foreground = GrayBrush,
            FontSize = 9,
            Margin = new Thickness(0, 5, 0, 0)
        });

        return new Popup
        {
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Child = panel
            },
            StaysOpen = false,
            Placement = PlacementMode.Right,
            AllowsTransparency = true,
            MinWidth = 200
        };
    }

    private void AddBoolRow(Grid grid, int row, string label, string helpKey, bool? vanilla, bool? edit, CheckBox check,
        AttackHitWindowInfo? info = null)
    {
        check.Tag = vanilla;
        check.IsChecked = edit.HasValue ? edit.Value : vanilla ?? false;
        check.ToolTip = vanilla.HasValue
            ? $"Vanilla: {(vanilla.Value ? "On" : "Off")} - checked = On"
            : MissingVanillaTip(info);
        HelpLabel(grid, row, 0, label, helpKey);
        Place(grid, check, row, 1);
    }

    private void ResetWindow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not WindowFields f) return;
            foreach (var box in new[] { f.FrameLo, f.FrameHi, f.Damage, f.Structure, f.Knockback, f.SuperDizzy, f.Stun, f.Freeze,
                    f.DamageOnHit, f.DamageOnGuard, f.DamageOnDeflect,
                    f.StructureOnHit, f.StructureOnGuard, f.StructureOnDeflect })
                box.Text = "";
        foreach (var check in new[] { f.Parriable, f.Lethal, f.BreakStructure, f.DizzyParry, f.DizzyAvoid })
            check.IsChecked = check.Tag is bool v ? v : false;
        if (f.Reaction.IsEnabled)
            f.Reaction.SelectedItem = f.Reaction.Items.OfType<ReactionOption>()
                .FirstOrDefault(p => p.Power == f.Info.AttackPower && p.Level == f.Info.PropertyLevel);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var inv = CultureInfo.InvariantCulture;
        var edits = new List<AttackHitEdit>();

        foreach (var f in _fields)
        {
            string hit = $"Hit {f.Info.Index + 1}";

            if (!TryParseInt(f.FrameLo, inv, out int? flo)) return;
            if (!TryParseInt(f.FrameHi, inv, out int? fhi)) return;
            if (flo.HasValue && flo.Value < 0)
            { txtStatus.Text = $"{hit}: frames lo must be >= 0"; return; }
            if (fhi.HasValue && fhi.Value < 0)
            { txtStatus.Text = $"{hit}: frames hi must be >= 0"; return; }
            if (flo.HasValue && fhi.HasValue && fhi.Value < flo.Value)
            { txtStatus.Text = $"{hit}: frames hi must be >= lo"; return; }
            if (flo.HasValue && flo.Value == f.Info.FrameLo) flo = null;
            if (fhi.HasValue && fhi.Value == f.Info.FrameHi) fhi = null;

            // Damage: the three popup fields are the source of truth (the
            // mirror box stays two-way synced to them, so it is not parsed
            // here). Empty field = keep vanilla for that path; a value equal
            // to that path's vanilla is not a change either.
            if (!TryParseFloat(f.DamageOnHit, out float? dmgHit)) return;
            if (dmgHit.HasValue && dmgHit.Value < 0)
            { txtStatus.Text = $"{hit}: on-hit damage must be >= 0"; return; }
            if (dmgHit.HasValue && f.Info.Damage.HasValue && dmgHit.Value == f.Info.Damage.Value) dmgHit = null;
            if (!TryParseFloat(f.DamageOnGuard, out float? dmgGuard)) return;
            if (dmgGuard.HasValue && dmgGuard.Value < 0)
            { txtStatus.Text = $"{hit}: on-guard damage must be >= 0"; return; }
            if (dmgGuard.HasValue && f.Info.GuardDamage.HasValue && dmgGuard.Value == f.Info.GuardDamage.Value) dmgGuard = null;
            if (!TryParseFloat(f.DamageOnDeflect, out float? dmgDeflect)) return;
            if (dmgDeflect.HasValue && dmgDeflect.Value < 0)
            { txtStatus.Text = $"{hit}: on-deflect damage must be >= 0"; return; }
            if (dmgDeflect.HasValue && f.Info.DeflectDamage.HasValue && dmgDeflect.Value == f.Info.DeflectDamage.Value) dmgDeflect = null;

            // Compact: all three paths equal -> single mirror value (keeps the
            // saved shape backward compatible and patches all three structs).
            float? dmg = null;
            if (dmgHit.HasValue && dmgHit == dmgGuard && dmgGuard == dmgDeflect)
            { dmg = dmgHit; dmgHit = dmgGuard = dmgDeflect = null; }

            if (!TryParseFloat(f.StructureOnHit, out float? strHit)) return;
            if (strHit.HasValue && strHit.Value < 0)
            { txtStatus.Text = $"{hit}: on-hit structure must be >= 0"; return; }
            if (strHit.HasValue && f.Info.StructureDamage.HasValue && strHit.Value == f.Info.StructureDamage.Value) strHit = null;
            if (!TryParseFloat(f.StructureOnGuard, out float? strGuard)) return;
            if (strGuard.HasValue && strGuard.Value < 0)
            { txtStatus.Text = $"{hit}: on-guard structure must be >= 0"; return; }
            if (strGuard.HasValue && f.Info.GuardStructureDamage.HasValue && strGuard.Value == f.Info.GuardStructureDamage.Value) strGuard = null;
            if (!TryParseFloat(f.StructureOnDeflect, out float? strDeflect)) return;
            if (strDeflect.HasValue && strDeflect.Value < 0)
            { txtStatus.Text = $"{hit}: on-deflect structure must be >= 0"; return; }
            if (strDeflect.HasValue && f.Info.DeflectStructureDamage.HasValue && strDeflect.Value == f.Info.DeflectStructureDamage.Value) strDeflect = null;

            float? str = null;
            if (strHit.HasValue && strHit == strGuard && strGuard == strDeflect)
            { str = strHit; strHit = strGuard = strDeflect = null; }

            if (!TryParseFloat(f.Knockback, out float? kb)) return;
            if (kb.HasValue && kb.Value < 0)
            { txtStatus.Text = $"{hit}: knockback must be >= 0"; return; }
            if (kb.HasValue && f.Info.KnockbackDistance.HasValue && kb.Value == f.Info.KnockbackDistance.Value) kb = null;

            if (!TryParseFloat(f.SuperDizzy, out float? sd)) return;
            if (sd.HasValue && sd.Value < 0)
            { txtStatus.Text = $"{hit}: super dizzy must be >= 0"; return; }
            if (sd.HasValue && f.Info.SuperDizzyDamage.HasValue && sd.Value == f.Info.SuperDizzyDamage.Value) sd = null;

            if (!TryParseInt(f.Stun, inv, out int? stun)) return;
            if (stun.HasValue && (stun.Value < 0 || stun.Value > 255))
            { txtStatus.Text = $"{hit}: stun frames must be 0-255"; return; }
            if (stun.HasValue && f.Info.StunFrames.HasValue && stun.Value == f.Info.StunFrames.Value) stun = null;

            if (!TryParseInt(f.Freeze, inv, out int? freeze)) return;
            if (freeze.HasValue && freeze.Value < 0)
            { txtStatus.Text = $"{hit}: freeze frames must be >= 0"; return; }
            if (freeze.HasValue && f.Info.FreezeFrames.HasValue && freeze.Value == f.Info.FreezeFrames.Value) freeze = null;

            string? power = null;
            int? level = null;
            if (f.Reaction.IsEnabled && f.Reaction.SelectedItem is ReactionOption ro)
            {
                if (!string.Equals(ro.Power, f.Info.AttackPower, StringComparison.Ordinal)) power = ro.Power;
                if (f.Info.PropertyLevel.HasValue && ro.Level != f.Info.PropertyLevel.Value) level = ro.Level;
            }

            var edit = new AttackHitEdit
            {
                Index = f.Info.Index,
                FrameLo = flo,
                FrameHi = fhi,
                Damage = dmg,
                StructureDamage = str,
                DamageOnHit = dmgHit,
                DamageOnGuard = dmgGuard,
                DamageOnDeflect = dmgDeflect,
                StructureDamageOnHit = strHit,
                StructureDamageOnGuard = strGuard,
                StructureDamageOnDeflect = strDeflect,
                KnockbackDistance = kb,
                SuperDizzyDamage = sd,
                StunFrames = stun,
                FreezeFrames = freeze,
                Parriable = BoolOrNull(f.Parriable, f.Info.Parriable),
                Lethal = BoolOrNull(f.Lethal, f.Info.Lethal),
                BreakStructure = BoolOrNull(f.BreakStructure, f.Info.BreakStructure),
                DizzyOnParry = BoolOrNull(f.DizzyParry, f.Info.DizzyOnParry),
                DizzyOnAvoid = BoolOrNull(f.DizzyAvoid, f.Info.DizzyOnAvoid),
                AttackPower = power,
                PropertyLevel = level
            };
            if (!edit.IsEmpty()) edits.Add(edit);
        }

        Result = edits.Count == 0 ? null : edits;
        DialogResult = true;
    }

    /// <summary>
    /// Two-state checkbox: checked = On, unchecked = Off. Matching vanilla (or
    /// unchecked on a row that lacks the bool) is not a change - store null.
    /// </summary>
    private static bool? BoolOrNull(CheckBox check, bool? vanilla)
    {
        bool now = check.IsChecked == true;
        if (vanilla.HasValue) return now == vanilla.Value ? null : now;
        return now ? true : null;
    }

    private bool TryParseInt(TextBox box, CultureInfo inv, out int? value)
    {
        value = null;
        string raw = box.Text?.Trim() ?? "";
        if (raw.Length == 0) return true;
        if (!int.TryParse(raw, NumberStyles.Integer, inv, out int v))
        {
            txtStatus.Text = $"Enter a whole number (got '{raw}')";
            return false;
        }
        value = v;
        return true;
    }

    private bool TryParseFloat(TextBox box, out float? value)
    {
        value = null;
        string raw = box.Text?.Trim() ?? "";
        if (raw.Length == 0) return true;
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
        {
            txtStatus.Text = $"Enter a number (got '{raw}')";
            return false;
        }
        value = v;
        return true;
    }
}
