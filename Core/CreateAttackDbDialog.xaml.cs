using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SifuMovesetEditor;

/// <summary>
/// Everything the Create AttackDB dialog needs to reopen exactly as the user left it
/// after a trip into the library to pick a template.
/// </summary>
public sealed class CreateAttackDbDialogState
{
    public string Name { get; set; } = "";
    public AttackDbTemplateOption? Template { get; set; }
    public bool PrivateHitRow { get; set; } = true;
    public bool ScaleHitWindows { get; set; }
    public bool AdvancedOpen { get; set; }
    public string BuildupText { get; set; } = "";
    public string RangeText { get; set; } = "";
}

/// <summary>
/// The Create AttackDB wizard: pick a working template card, name the clone and
/// optionally scale/private-copy its hit data. MainWindow performs the actual clone,
/// retarget and rescans once this returns true. "Choose AttackDB Card in Library…"
/// closes the dialog (the owner window is disabled while a modal dialog is open) and
/// asks MainWindow for a pick; the caller reopens a new dialog with the saved state.
/// </summary>
public partial class CreateAttackDbDialog : Window
{
    private readonly string _animationGamePath;
    private readonly float? _animationDuration;
    private readonly List<AttackDbTemplateOption> _templates;
    private readonly string? _mappedDbPath;
    private readonly Func<string, float?>? _templateDurationLookup;
    private readonly IReadOnlyList<string> _existingGamePaths;
    private readonly string? _vanillaContentRoot;
    private AttackDbTemplateOption? _selectedTemplate;
    private AttackDbTemplateOption? _recommended;
    private bool _ready;

    /// <summary>True when the user asked to pick a template in the Move Library.</summary>
    public bool PickTemplateRequested { get; private set; }

    public string NewName => txtName.Text.Trim();
    public AttackDbTemplateOption? Template => _selectedTemplate;
    public bool PrivateHitRow => chkPrivate.IsChecked == true;
    public bool ScaleHitWindows => SelectedHitMode == "scale";
    public float? ScaleRatio { get; private set; }
    public int? BuildupOverride { get; private set; }
    public float? RangeOverride { get; private set; }
    public string DestinationFolderRel => Template?.ParentFolderRel ?? "";

    public CreateAttackDbDialog(
        string animationGamePath,
        string animationDisplayName,
        float? animationDuration,
        IReadOnlyList<AttackDbTemplateOption> templates,
        string? mappedDbPath,
        Func<string, float?>? templateDurationLookup,
        IEnumerable<string> existingGamePaths,
        string? vanillaContentRoot,
        CreateAttackDbDialogState? initial = null)
    {
        InitializeComponent();
        _animationGamePath = animationGamePath;
        _animationDuration = animationDuration;
        _templates = templates?.ToList() ?? new List<AttackDbTemplateOption>();
        _mappedDbPath = mappedDbPath;
        _templateDurationLookup = templateDurationLookup;
        _existingGamePaths = existingGamePaths?.ToList() ?? new List<string>();
        _vanillaContentRoot = vanillaContentRoot;
        _recommended = _templates.FirstOrDefault(t => t.IsRecommended) ?? _templates.FirstOrDefault();

        runAnimation.Text = animationDisplayName;
        txtAnimation.Text = animationGamePath;
        txtAnimation.ToolTip = animationGamePath;

        if (initial != null)
        {
            cboHitMode.SelectedIndex = initial.ScaleHitWindows ? 1 : 0;
            chkPrivate.IsChecked = initial.PrivateHitRow;
            chkAdvanced.IsChecked = initial.AdvancedOpen;
            advancedPanel.Visibility = initial.AdvancedOpen ? Visibility.Visible : Visibility.Collapsed;
            txtBuildup.Text = initial.BuildupText ?? "";
            txtRange.Text = initial.RangeText ?? "";
            txtName.Text = initial.Name ?? "";
        }
        else
        {
            cboHitMode.SelectedIndex = 0;
        }

        var initialTemplate = initial?.Template;
        if (initialTemplate != null
            && !_templates.Any(t => string.Equals(t.GamePath, initialTemplate.GamePath,
                StringComparison.OrdinalIgnoreCase)))
        {
            _templates.Add(initialTemplate);
        }

        _ready = true;
        Loaded += (_, _) => txtName.Focus();
        SetTemplate(initialTemplate ?? _recommended);
        if (string.IsNullOrWhiteSpace(txtName.Text)) PrefillName();
    }

    private string SelectedHitMode =>
        string.Equals((cboHitMode.SelectedItem as ComboBoxItem)?.Tag as string, "scale",
            StringComparison.Ordinal)
            ? "scale" : "copy";

    private void Name_TextChanged(object sender, TextChangedEventArgs e) => Validate();
    private void HitMode_Changed(object sender, SelectionChangedEventArgs e) => Validate();
    private void Private_Changed(object sender, RoutedEventArgs e) => Validate();

    private void Advanced_Changed(object sender, RoutedEventArgs e)
    {
        if (advancedPanel == null) return;
        advancedPanel.Visibility = chkAdvanced.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        Validate();
    }

    private void PickTemplate_Click(object sender, RoutedEventArgs e)
    {
        PickTemplateRequested = true;
        DialogResult = false;
    }

    /// <summary>Captures every field so the caller can reopen after a library pick.</summary>
    public CreateAttackDbDialogState SnapshotState() => new()
    {
        Name = txtName.Text.Trim(),
        Template = _selectedTemplate,
        PrivateHitRow = PrivateHitRow,
        ScaleHitWindows = ScaleHitWindows,
        AdvancedOpen = chkAdvanced.IsChecked == true,
        BuildupText = txtBuildup.Text,
        RangeText = txtRange.Text
    };

    private void SetTemplate(AttackDbTemplateOption? option)
    {
        _selectedTemplate = option;

        string name = option == null
            ? "No template"
            : (string.IsNullOrWhiteSpace(option.DisplayNameClean) ? option.DisplayName : option.DisplayNameClean);
        txtTemplateName.Text = name;

        var bits = option == null
            ? Array.Empty<string>()
            : new[] { option.Character, option.WeaponType, option.Category }
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        txtTemplateMeta.Text = string.Join(" · ", bits);
        txtTemplateMeta.ToolTip = option?.GamePath;

        if (option == null)
            txtTemplateReason.Text = "";
        else if (option.IsRecommended)
            txtTemplateReason.Text = $"Recommended — {option.MatchReason}.";
        else
            txtTemplateReason.Text = "Chosen from the library.";

        UpdateTemplateDerived();
    }

    private void UpdateTemplateDerived()
    {
        ScaleRatio = null;
        string? templateDurationText = null;
        if (_animationDuration is > 0 && Template != null && _templateDurationLookup != null)
        {
            float? templateDuration = _templateDurationLookup(Template.GamePath);
            if (templateDuration is > 0)
            {
                ScaleRatio = _animationDuration.Value / templateDuration.Value;
                templateDurationText = templateDuration.Value.ToString("0.###", CultureInfo.InvariantCulture);
            }
        }

        itemScale.IsEnabled = ScaleRatio.HasValue;
        if (!ScaleRatio.HasValue && SelectedHitMode == "scale")
            cboHitMode.SelectedIndex = 0;

        txtHitHint.Text = ScaleRatio.HasValue
            ? $"Template animation: {templateDurationText}s · this animation: "
              + $"{_animationDuration!.Value.ToString("0.###", CultureInfo.InvariantCulture)}s "
              + $"(scale ×{ScaleRatio.Value.ToString("0.###", CultureInfo.InvariantCulture)}). "
              + "Final tuning still happens in Hit Windows."
            : "Animation length unavailable for the template or this animation - "
              + "copy the template's frames, then tune them in Hit Windows.";

        UpdateExistingHint();
        UpdateTemplateFieldHints();
        Validate();
    }

    private void UpdateExistingHint()
    {
        if (string.IsNullOrEmpty(_mappedDbPath))
        {
            existingPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var mapped = _templates.FirstOrDefault(t => string.Equals(t.GamePath, _mappedDbPath,
            StringComparison.OrdinalIgnoreCase));
        string mappedName = mapped?.DisplayNameClean;
        if (string.IsNullOrWhiteSpace(mappedName))
            mappedName = Path.GetFileNameWithoutExtension(_mappedDbPath);
        if (string.IsNullOrWhiteSpace(mappedName)) mappedName = "an existing card";

        existingPanel.Visibility = Visibility.Visible;
        bool usingMapped = string.Equals(Template?.GamePath, _mappedDbPath,
            StringComparison.OrdinalIgnoreCase);
        txtExisting.Text = usingMapped
            ? $"This animation already maps to {mappedName}. It is preselected as the template; "
              + "Create makes another card and leaves the existing one untouched."
            : $"This animation already maps to {mappedName}. Choose it as the template to copy its "
              + "settings, or keep the current pick.";
    }

    /// <summary>Template values are shown as hints only: blank fields inherit them.</summary>
    private void UpdateTemplateFieldHints()
    {
        var values = ReadTemplateValues(Template?.GamePath);
        if (values?.HasBuildup == true)
        {
            txtBuildup.IsEnabled = true;
            txtBuildupHint.Text = $"Template: {values.Buildup}. Leave blank to keep it.";
        }
        else
        {
            txtBuildup.IsEnabled = false;
            txtBuildup.Text = "";
            txtBuildupHint.Text = "Template has no buildup field.";
        }

        if (values?.HasRange == true)
        {
            txtRange.IsEnabled = true;
            txtRangeHint.Text = $"Template: {AttackDbCard.FormatRange(values.Range)}. Leave blank to keep it.";
        }
        else
        {
            txtRange.IsEnabled = false;
            txtRange.Text = "";
            txtRangeHint.Text = "Template has no gameplay range field.";
        }
    }

    private AttackDbCardValues? ReadTemplateValues(string? gamePath)
    {
        if (string.IsNullOrEmpty(gamePath)) return null;
        var custom = AttackDbCard.ReadDbPath(CustomAssets.Root, gamePath);
        if (custom != null) return custom;
        return AttackDbCard.ReadDbPath(_vanillaContentRoot ?? "", gamePath);
    }

    private void Validate()
    {
        if (!_ready) return;

        if (Template == null)
        {
            SetError("Pick a template card.");
            return;
        }

        string name = txtName.Text.Trim();
        string destCardRel = Template.ParentFolderRel.Trim('/') + "/" + name;
        string? err = null;

        if (name.Length == 0)
        {
            err = "Enter a name for the card.";
        }
        else if (!CustomMovesetInfo.IsValidName(name))
        {
            err = "Use only letters, digits and underscores.";
        }
        else if (FindTemplateFile(Template.GamePath) == null)
        {
            err = "The selected template card file was not found.";
        }
        else
        {
            err = AttackCardCloner.ValidateName(name, destCardRel, _existingGamePaths,
                _vanillaContentRoot, PrivateHitRow);
        }

        BuildupOverride = null;
        if (txtBuildup.IsEnabled && !string.IsNullOrWhiteSpace(txtBuildup.Text))
        {
            if (!int.TryParse(txtBuildup.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int buildup))
            {
                err ??= "Buildup frames must be a whole number.";
            }
            else
            {
                BuildupOverride = buildup;
            }
        }

        RangeOverride = null;
        if (txtRange.IsEnabled && !string.IsNullOrWhiteSpace(txtRange.Text))
        {
            if (!float.TryParse(txtRange.Text.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float range))
            {
                err ??= "Gameplay range must be a number.";
            }
            else
            {
                RangeOverride = range;
            }
        }

        UpdateDestinationPreview(name, destCardRel);
        SetError(err);
    }

    private void UpdateDestinationPreview(string name, string destCardRel)
    {
        string displayName = string.IsNullOrWhiteSpace(name) ? "<name>" : name;
        string folder = Template?.ParentFolderRel.Replace('/', '\\') ?? "";
        string destFile = $"CustomAssets\\{folder}\\{displayName}.uasset";

        var lines = new List<string> { "Card: " + destFile };
        if (PrivateHitRow)
            lines.Add($"Hit row: private copy ({folder}\\{displayName}_HitBoxData)");
        else
            lines.Add("Hit row: shared with the template (can be made private later)");

        if (ScaleHitWindows && ScaleRatio.HasValue)
            lines.Add($"Hit windows: scaled ×{ScaleRatio.Value.ToString("0.###", CultureInfo.InvariantCulture)} "
                      + "to this animation's length");
        else
            lines.Add("Hit windows: copied from the template");

        if (BuildupOverride.HasValue) lines.Add($"Buildup frames: {BuildupOverride.Value}");
        if (RangeOverride.HasValue)
            lines.Add($"Gameplay range: {AttackDbCard.FormatRange(RangeOverride.Value)}");

        txtPreview.Text = string.Join("\n", lines);
    }

    private void SetError(string? error)
    {
        txtError.Text = error ?? "";
        btnCreate.IsEnabled = error == null;
    }

    /// <summary>
    /// Offers the animation's own name as the card name when that name is free. The
    /// user is expected to type their own name; this only removes the blank-field step
    /// for the common case (and never fills in a name the validator would reject).
    /// </summary>
    private void PrefillName()
    {
        var template = Template;
        if (template == null) return;

        string suggestion = SuggestCardName(_animationGamePath);
        if (string.IsNullOrEmpty(suggestion)) return;

        string destCardRel = template.ParentFolderRel.Trim('/') + "/" + suggestion;
        if (AttackCardCloner.ValidateName(suggestion, destCardRel, _existingGamePaths,
                _vanillaContentRoot, PrivateHitRow) != null)
            return;

        txtName.Text = suggestion;
        txtName.SelectAll();
    }

    private static string SuggestCardName(string animationGamePath)
    {
        var name = (animationGamePath ?? "").Replace('\\', '/');
        if (name.Contains('/')) name = name[(name.LastIndexOf('/') + 1)..];

        const string mainCharAttack = "MainChar_Attack_";
        if (name.StartsWith(mainCharAttack, StringComparison.OrdinalIgnoreCase))
            name = name[mainCharAttack.Length..];

        var chars = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
            chars[i] = char.IsLetterOrDigit(name[i]) ? name[i] : '_';
        var clean = new string(chars).Trim('_');
        while (clean.Contains("__", StringComparison.Ordinal))
            clean = clean.Replace("__", "_", StringComparison.Ordinal);

        if (clean.Length > 48) clean = clean[..48].TrimEnd('_');
        return clean;
    }

    private string? FindTemplateFile(string gamePath)
    {
        var rel = AttackDbCard.GamePathToContentRel(gamePath);
        var custom = CustomAssets.FileForContentRel(rel + ".uasset");
        if (File.Exists(custom)) return custom;
        var vanilla = AttackDbCard.ResolveUassetPath(_vanillaContentRoot ?? "", gamePath);
        return vanilla != null && File.Exists(vanilla) ? vanilla : null;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        Validate();
        if (!btnCreate.IsEnabled) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
