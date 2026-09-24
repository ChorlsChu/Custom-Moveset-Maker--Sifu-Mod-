using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SifuMovesetEditor;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace SifuMovesetEditor.Export;

public partial class ExportDialog : Window
{
    private List<ComboNode> _modifiedNodes;
    private readonly string _contentPath;
    private readonly string _outputPath;
    private readonly ConcurrentDictionary<string, string> _animToDbPath;
    private string? _activeStance;
    private string? _charTransitionPath;
    private string? _charBaseMovementDBPath;
    private readonly string? _referenceModDir;
    private string? _enemyComboPath;
    private readonly Dictionary<string, (float hitFrame, int buildupFrame)> _animToTiming;
    private ComboGraph? _graph;
    private string? _pakPath;
    private string? _outputDir;
    private string? _mainCharComboPath;
    private const string DefaultMainCharComboPath = "Game/DB/_MainChar/Combos/MainChar_ComboTree";

    private readonly Dictionary<string, UnitCacheEntry>? _unitCaches;
    private readonly Dictionary<string, List<ComboNode>> _perUnitModifiedNodes = new();
    private readonly Dictionary<string, (ComboGraph graph, string? comboPath, string? charTransitionPath, string? charBaseMovementDBPath)> _perUnitGraphs = new();
    private readonly Dictionary<string, UnitProperties> _perUnitProps = new();
    private UnitProperties? _unitProps;
    private string? _activeVariant;
    private int _reviewChangeCount;
    private readonly HashSet<string> _expandedUnits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _expandedSections = new(StringComparer.OrdinalIgnoreCase);
    private string? _currentExportVariant;
    private readonly HashSet<string> _writtenArenaPaths = new(StringComparer.OrdinalIgnoreCase);

    private static Brush MakeBrush(string hex) =>
        (Brush)new BrushConverter().ConvertFrom(hex);

    public ExportDialog(
        List<ComboNode> modifiedNodes,
        string contentPath,
        string outputPath,
        ConcurrentDictionary<string, string> animToDbPath,
        string? activeStance = null,
        string? charTransitionPath = null,
        string? charBaseMovementDBPath = null,
        string? referenceModDir = null,
        ComboGraph? graph = null,
        string? enemyComboPath = null,
        Dictionary<string, (float hitFrame, int buildupFrame)>? animToTiming = null,
        UnitProperties? unitProps = null,
        string? activeVariant = null,
        string? mainCharComboPath = null)
    {
        InitializeComponent();

        _modifiedNodes = modifiedNodes;
        _contentPath = contentPath;
        _outputPath = outputPath;
        _animToDbPath = animToDbPath;
        _activeStance = activeStance;
        _charTransitionPath = charTransitionPath;
        _charBaseMovementDBPath = charBaseMovementDBPath;
        _referenceModDir = referenceModDir;
        _enemyComboPath = enemyComboPath;
        _animToTiming = animToTiming ?? new();
        _graph = graph;
        _unitProps = unitProps;
        _activeVariant = activeVariant;
        _mainCharComboPath = string.IsNullOrEmpty(mainCharComboPath) ? DefaultMainCharComboPath : mainCharComboPath;
        _outputPath = ResolveOutputPath(_outputPath);

        LoadReview();
    }

    public ExportDialog(
        Dictionary<string, UnitCacheEntry> unitCaches,
        string contentPath,
        string outputPath,
        ConcurrentDictionary<string, string> animToDbPath,
        string? referenceModDir = null,
        Dictionary<string, (float hitFrame, int buildupFrame)>? animToTiming = null)
    {
        InitializeComponent();
        _unitCaches = unitCaches;
        _contentPath = contentPath;
        _outputPath = ResolveOutputPath(outputPath);
        _animToDbPath = animToDbPath;
        _animToTiming = animToTiming ?? new();
        _referenceModDir = referenceModDir;
        _modifiedNodes = new();
        _graph = null;
        _enemyComboPath = null;
        _activeStance = null;
        _charTransitionPath = null;
        _charBaseMovementDBPath = null;

        foreach (var kvp in unitCaches)
        {
            string unitKey = kvp.Key;
            var entry = kvp.Value;
            var graph = entry.Graph;

            var modified = graph.Nodes
                .Where(n => !n.IsRoot && !string.IsNullOrEmpty(n.AnimPath)
                    && (n.TreeIndex == -1 || n.AnimPath != n.DefaultAnimPath
                        || (!string.IsNullOrEmpty(n.VanillaAnimPath) && n.AnimPath != n.VanillaAnimPath)))
                .ToList();

            bool hasRetargets = graph.RedirectOriginalTargets.Any(rd =>
            {
                var node = graph.Nodes.FirstOrDefault(n => n.Id == rd.Key);
                return node != null && node.ResolvedRedirectNodeId >= 0 && node.ResolvedRedirectNodeId != rd.Value;
            });

            var keyParts = unitKey.Split('|');
            string arch = keyParts[0];
            string? variant = keyParts.Length > 1 ? keyParts[1] : null;

            bool hasUnitProps = entry.Props != null && !string.IsNullOrEmpty(variant)
                && UnitPropertiesManager.HasChanges(contentPath, variant, entry.Props);

            if (modified.Count > 0 || hasRetargets || hasUnitProps)
            {
                _perUnitModifiedNodes[unitKey] = modified;
                if (hasUnitProps) _perUnitProps[unitKey] = entry.Props!;

                string? comboPath = null;
                string? transitionPath = null;
                string? movementDbPath = null;
                string? weapon = keyParts.Length > 2 ? keyParts[2] : null;

                if (string.Equals(arch, "MainChar", StringComparison.OrdinalIgnoreCase))
                {
                    string? weaponTag = weapon
                        ?? (keyParts.Length > 1 && keyParts[1].StartsWith("MainChar_", StringComparison.OrdinalIgnoreCase)
                            ? keyParts[1]
                            : null)
                        ?? "MainChar_Barehands";
                    comboPath = ResolveComboFilePathForUnit(weaponTag) ?? DefaultMainCharComboPath;
                }
                else if (!string.IsNullOrEmpty(weapon) && !string.IsNullOrEmpty(variant))
                {
                    string weaponSuffix = weapon.Contains('_') ? weapon.Substring(weapon.IndexOf('_') + 1) : weapon;
                    string weaponComboKey = $"{variant}_{weaponSuffix}";
                    comboPath = ResolveComboFilePathForUnit(weaponComboKey);
                    if (comboPath == null && weaponSuffix == "Barehands")
                        comboPath = ResolveComboFilePathForUnit(variant);
                }
                else if (!string.IsNullOrEmpty(variant))
                {
                    comboPath = ResolveComboFilePathForUnit(variant);
                }

                _perUnitGraphs[unitKey] = (graph, comboPath, transitionPath, movementDbPath);
            }
        }

        DedupeSharedGraphs();

        LoadReview();
    }

    private void DedupeSharedGraphs()
    {
        if (_perUnitGraphs.Count < 2) return;

        var groups = _perUnitGraphs
            .GroupBy(kvp => kvp.Value.graph)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            var keys = group.Select(k => k.Key).ToList();
            string keep = keys[0];
            foreach (var key in keys.Skip(1))
                keep = PreferUnitKey(keep, key);

            foreach (var key in keys)
            {
                if (key == keep) continue;
                _perUnitGraphs.Remove(key);
                _perUnitModifiedNodes.Remove(key);
                _perUnitProps.Remove(key);
            }
        }
    }

    private static string PreferUnitKey(string a, string b)
    {
        bool aMain = a.StartsWith("MainChar", StringComparison.OrdinalIgnoreCase);
        bool bMain = b.StartsWith("MainChar", StringComparison.OrdinalIgnoreCase);
        if (aMain != bMain) return aMain ? a : b;

        bool aHasVariant = a.Contains('|');
        bool bHasVariant = b.Contains('|');
        if (aHasVariant != bHasVariant) return aHasVariant ? a : b;

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? a : b;
    }

    private static string? ResolveComboFilePathForUnit(string variantTag)
    {
        var comboFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MainChar_Barehands"] = "Game/DB/_MainChar/Combos/MainChar_ComboTree",
            ["MainChar_Bat"] = "Game/DB/_MainChar/Combos/Attacks/Weapons/Bats/MainChar_Bats_ComboTree",
            ["MainChar_Staff"] = "Game/DB/_MainChar/Combos/Attacks/Weapons/Staff/MainChar_Staff_ComboTree",
            ["MainChar_Blade"] = "Game/DB/_MainChar/Combos/Attacks/Weapons/Blades/MainChar_Blades_ComboTree",
            ["Yang_P1"] = "Game/DB/AI/Archetypes/Yang/_DB/Phase1/Yang_P1_Combo",
            ["Yang_P2"] = "Game/DB/AI/Archetypes/Yang/_DB/Phase2/Yang_P2_Combo",
            ["Yang_P3"] = "Game/DB/AI/Archetypes/Yang/_DB/Phase3/Yang_P3_Combo",
            ["Sean_P1"] = "Game/DB/AI/Archetypes/Sean/Sean_Combo_Phase1",
            ["Sean_P2"] = "Game/DB/AI/Archetypes/Sean/Sean_Combo_Phase2",
            ["Sean_Burst"] = "Game/DB/AI/Archetypes/Sean/Sean_BurstCombo",
            ["Kuroki_P1"] = "Game/DB/AI/Archetypes/Kuroki/Kuroki_ComboPhase1_NEW",
            ["Kuroki_P2"] = "Game/DB/AI/Archetypes/Kuroki/Kuroki_ComboPhase2_Shiroizu",
            ["Fengjie_P1"] = "Game/DB/AI/Archetypes/Fengjie/Phase1/Fengjie_Phase1_Combo",
            ["Fengjie_P2"] = "Game/DB/AI/Archetypes/Fengjie/Phase2/Fengjie_Phase2_Combo",
            ["Fajar_P1"] = "Game/DB/AI/Archetypes/Fajar/Attacks/Fajar_Combo_P1",
            ["Fajar_P2"] = "Game/DB/AI/Archetypes/Fajar/Attacks/Fajar_Combo_P2",
            ["Grunt_Base"] = "Game/DB/AI/Archetypes/Grunt/_Base/Grunt_Base_Combo",
            ["Grunt_Advanced"] = "Game/DB/AI/Archetypes/Grunt/_Advanced/Grunt_Advanced_Combo",
            ["Grunt_Miniboss"] = "Game/DB/AI/Archetypes/Grunt/_Miniboss/Grunt_Miniboss_Combo",
            ["BigGuy_Base"] = "Game/DB/AI/Archetypes/BigGuy/_MainGame/Generic/BigGuy_Base_Combo",
            ["BigGuy_Advanced"] = "Game/DB/AI/Archetypes/BigGuy/_MainGame/Generic/BigGuy_Advanced_Combo",
            ["BigGuy_Miniboss"] = "Game/DB/AI/Archetypes/BigGuy/_MainGame/Generic/BigGuy_Miniboss_Combo",
            ["Bodyguard_Base"] = "Game/DB/AI/Archetypes/Bodyguard/_Base/Bodyguard_Base_Combo",
            ["Bodyguard_Advanced"] = "Game/DB/AI/Archetypes/Bodyguard/_Advanced/Bodyguard_Advanced_Combo",
            ["Bodyguard_Miniboss"] = "Game/DB/AI/Archetypes/Bodyguard/_Miniboss/Bodyguard_Miniboss_Combo",
            ["FlashKick_Base"] = "Game/DB/AI/Archetypes/FlashKick/_MainGame/Generic/FlashKick_Base_Combo",
            ["FlashKick_Advanced"] = "Game/DB/AI/Archetypes/FlashKick/_MainGame/Generic/FlashKick_Advanced_Combo",
            ["FlashKick_Miniboss"] = "Game/DB/AI/Archetypes/FlashKick/_MainGame/Generic/FlashKick_Miniboss_Combo",
            ["FD_Base"] = "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Base_Combo",
            ["FD_Advanced"] = "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Advanced_Combo",
            ["FD_Miniboss"] = "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_MiniBoss_Combo",
            ["Grunt_Base_Bat"] = "Game/DB/AI/Archetypes/Grunt/_Base/Grunt_Base_BatCombo",
            ["Grunt_Base_Blade"] = "Game/DB/AI/Archetypes/Grunt/_Base/Grunt_Base_BladeCombo",
            ["Grunt_Advanced_Bat"] = "Game/DB/AI/Archetypes/Grunt/_Advanced/Grunt_Advanced_BatCombo",
            ["Grunt_Advanced_Blade"] = "Game/DB/AI/Archetypes/Grunt/_Advanced/Grunt_Advanced_BladeCombo",
            ["Grunt_Miniboss_Bat"] = "Game/DB/AI/Archetypes/Grunt/_Miniboss/Grunt_Miniboss_BatCombo",
            ["Grunt_Miniboss_Blade"] = "Game/DB/AI/Archetypes/Grunt/_Miniboss/Grunt_Miniboss_BladeCombo",
            ["FD_Base_Staff"] = "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Base_StaffCombo",
            ["FD_Advanced_Staff"] = "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Advanced_StaffCombo",
            ["FD_Miniboss_Staff"] = "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_MiniBoss_StaffCombo",
            ["Servant"] = "Game/DB/AI/Archetypes/Servant/Servant_Combo",
            ["Sifu"] = "Game/DB/AI/Archetypes/Sifu/Sifu_Combo",
        };
        return comboFiles.TryGetValue(variantTag, out var path) ? path : null;
    }

    private List<UnitReview> BuildUnitReviews()
    {
        var units = new List<UnitReview>();

        if (_unitCaches != null && _perUnitGraphs.Count > 0)
        {
            foreach (var kvp in _perUnitGraphs)
            {
                string unitKey = kvp.Key;
                var (graph, comboPath, _, _) = kvp.Value;
                var modified = _perUnitModifiedNodes.TryGetValue(unitKey, out var nodes)
                    ? nodes
                    : new List<ComboNode>();

                var unit = new UnitReview
                {
                    Key = unitKey,
                    DisplayName = ProjectChangeSummary.FormatUnitDisplayName(unitKey),
                    Subtitle = string.IsNullOrEmpty(comboPath)
                        ? ""
                        : comboPath[(comboPath.LastIndexOf('/') + 1)..]
                };

                if (modified.Count > 0)
                {
                    var moveRows = modified
                        .OrderBy(n => n.TreeIndex)
                        .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .Select(ProjectChangeSummary.ToChangeRow)
                        .ToList();
                    unit.Sections.Add(new UnitSectionReview { Name = "Moves", Rows = moveRows });
                }

                var retargetRows = ProjectChangeSummary.BuildRetargetRows(graph);
                if (retargetRows.Count > 0)
                    unit.Sections.Add(new UnitSectionReview { Name = "Retargets", Rows = retargetRows });

                if (_perUnitProps.TryGetValue(unitKey, out var unitProps))
                {
                    var keyParts = unitKey.Split('|');
                    string variant = keyParts.Length > 1 ? keyParts[1] : unitKey;
                    var propRows = new List<object>();
                    ProjectChangeSummary.AddUnitPropsEntries(propRows, unitProps, _contentPath, variant);
                    if (propRows.Count > 0)
                        unit.Sections.Add(new UnitSectionReview { Name = "Unit Props", Rows = propRows });
                }

                if (unit.Total > 0)
                    units.Add(unit);
            }
        }
        else
        {
            var unitKey = !string.IsNullOrEmpty(_activeVariant)
                ? string.IsNullOrEmpty(_activeStance) || _activeStance == "MainChar"
                    ? $"MainChar|{_activeVariant}"
                    : _activeStance
                : _activeStance ?? "MainChar";

            var unit = new UnitReview
            {
                Key = unitKey,
                DisplayName = ProjectChangeSummary.FormatUnitDisplayName(unitKey),
                Subtitle = _mainCharComboPath?.Split('/').LastOrDefault() ?? ""
            };

            if (!string.IsNullOrEmpty(_activeStance) && _activeStance != "MainChar")
            {
                unit.Sections.Add(new UnitSectionReview
                {
                    Name = "Stance",
                    Rows = new List<object>
                    {
                        MakeValueRow(
                            $"Combat Stance → {_activeStance}",
                            "MainChar (vanilla)",
                            $"{_activeStance} (BaseMovementDB + BP_TransitionAnimRequest)")
                    }
                });
            }

            if (_modifiedNodes is { Count: > 0 })
            {
                var moveRows = _modifiedNodes
                    .OrderBy(n => n.TreeIndex)
                    .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(ProjectChangeSummary.ToChangeRow)
                    .ToList();
                unit.Sections.Add(new UnitSectionReview { Name = "Moves", Rows = moveRows });
            }

            if (_graph != null)
            {
                var retargetRows = ProjectChangeSummary.BuildRetargetRows(_graph);
                if (retargetRows.Count > 0)
                    unit.Sections.Add(new UnitSectionReview { Name = "Retargets", Rows = retargetRows });
            }

            if (_unitProps != null && !string.IsNullOrEmpty(_activeVariant))
            {
                var propRows = new List<object>();
                ProjectChangeSummary.AddUnitPropsEntries(propRows, _unitProps, _contentPath, _activeVariant);
                if (propRows.Count > 0)
                    unit.Sections.Add(new UnitSectionReview { Name = "Unit Props", Rows = propRows });
            }

            if (unit.Total > 0)
                units.Add(unit);
        }

        return units
            .OrderBy(u => u.Key.StartsWith("MainChar", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static object ToChangeRow(ComboNode n) => ProjectChangeSummary.ToChangeRow(n);

    private static ChangeRow MakeValueRow(string title, string left, string right, string? toolTip = null)
        => ProjectChangeSummary.MakeValueRow(title, left, right, toolTip);

    private static List<object> BuildRetargetRows(ComboGraph graph)
        => ProjectChangeSummary.BuildRetargetRows(graph);

    private static string FormatUnitDisplayName(string unitKey)
        => ProjectChangeSummary.FormatUnitDisplayName(unitKey);

    private static int CountUnitPropsChanges(UnitProperties props, string contentPath, string variantTag)
        => ProjectChangeSummary.CountUnitPropsChanges(props, contentPath, variantTag);

    private static void AddUnitPropsEntries(List<object> entries, UnitProperties props, string contentPath, string variantTag)
        => ProjectChangeSummary.AddUnitPropsEntries(entries, props, contentPath, variantTag);

    private bool IsSectionExpanded(string unitKey, string sectionName)
    {
        return _expandedSections.TryGetValue(unitKey, out var set) && set.Contains(sectionName);
    }

    private List<object> BuildReviewList()
    {
        var entries = new List<object>();

        foreach (var unit in BuildUnitReviews())
        {
            bool unitOpen = _expandedUnits.Contains(unit.Key);
            entries.Add(new GroupHeader
            {
                Name = unit.DisplayName,
                Level = 1,
                Count = unit.Total,
                Subtitle = unit.Subtitle,
                IsExpanded = unitOpen,
                ParentName = unit.Key
            });

            if (!unitOpen) continue;

            foreach (var section in unit.Sections)
            {
                bool sectionOpen = IsSectionExpanded(unit.Key, section.Name);
                entries.Add(new GroupHeader
                {
                    Name = section.Name,
                    Level = 2,
                    Count = section.Count,
                    IsExpanded = sectionOpen,
                    ParentName = unit.Key
                });

                if (sectionOpen)
                    entries.AddRange(section.Rows);
            }
        }

        return entries;
    }

    private void LoadReview()
    {
        var units = BuildUnitReviews();
        _reviewChangeCount = units.Sum(u => u.Total);

        bool isMulti = _unitCaches != null && _perUnitGraphs.Count > 0;
        txtChangeCount.Text = isMulti
            ? $"{_reviewChangeCount} change(s) across {units.Count} unit(s):"
            : $"{_reviewChangeCount} change(s) detected:";

        lstChanges.ItemsSource = BuildReviewList();

        var outputDir = ResolveOutputPath(_outputPath);

        txtModName.Text = isMulti ? "MultiUnitComboMod" : "MainCharComboMod";
        UpdateOutputPreview(outputDir);
    }

    private static string ResolveOutputPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExportedMods");
        return path;
    }

    private void RebuildReviewList()
    {
        lstChanges.ItemsSource = BuildReviewList();
    }

    private void UnitHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not GroupHeader header) return;
        var key = string.IsNullOrEmpty(header.ParentName) ? header.Name : header.ParentName;
        if (!_expandedUnits.Remove(key))
            _expandedUnits.Add(key);
        RebuildReviewList();
    }

    private void SectionHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not GroupHeader header) return;
        var unitKey = header.ParentName;
        if (string.IsNullOrEmpty(unitKey)) return;

        if (!_expandedSections.TryGetValue(unitKey, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _expandedSections[unitKey] = set;
        }

        if (!set.Remove(header.Name))
            set.Add(header.Name);
        RebuildReviewList();
    }

    private void UpdateOutputPreview(string outputDir)
    {
        var name = GetModFileName();
        txtOutput.Text = $"Output: {outputDir}/{name}.pak + .sig";
    }

    private string GetModBaseName()
    {
        var raw = txtModName?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw)) return "MainCharComboMod";

        foreach (var c in Path.GetInvalidFileNameChars())
            raw = raw.Replace(c, '_');

        if (raw.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            raw = raw.Substring(0, raw.Length - 4);

        return string.IsNullOrEmpty(raw) ? "MainCharComboMod" : raw;
    }

    private string GetModFileName() => GetModBaseName() + ".pak";

    private void txtModName_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var outputDir = ResolveOutputPath(_outputPath);
        UpdateOutputPreview(outputDir);
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        panelReview.Visibility = Visibility.Collapsed;
        panelExporting.Visibility = Visibility.Visible;

        try
        {
            var contentPath = _contentPath;
            if (string.IsNullOrEmpty(contentPath) || !Directory.Exists(contentPath))
            {
                ShowError("Content path not set or invalid. Check Settings.");
                return;
            }

            var outputPath = ResolveOutputPath(_outputPath);
            _outputDir = outputPath;
            Directory.CreateDirectory(outputPath);

            var gameRoot = Path.Combine(contentPath, "Content");
            if (!Directory.Exists(gameRoot))
            {
                ShowError($"Game content not found at: {gameRoot}");
                return;
            }

            var fileEntries = new List<(string src, string dest)>();
            _writtenArenaPaths.Clear();
            _currentExportVariant = null;

            if (_unitCaches != null && _perUnitGraphs.Count > 0)
            {
                var savedMod = _modifiedNodes;
                var savedGraph = _graph;
                var savedCombo = _enemyComboPath;
                var savedStance = _activeStance;
                var savedTrans = _charTransitionPath;
                var savedMove = _charBaseMovementDBPath;
                var savedMainCharCombo = _mainCharComboPath;
                var savedVariant = _activeVariant;

                foreach (var kvp in _perUnitGraphs)
                {
                    _modifiedNodes = _perUnitModifiedNodes.TryGetValue(kvp.Key, out var n) ? n : new();
                    _graph = kvp.Value.graph;

                    string arch = kvp.Key.Contains('|') ? kvp.Key.Split('|')[0] : kvp.Key;
                    if (string.Equals(arch, "MainChar", StringComparison.OrdinalIgnoreCase))
                    {
                        _mainCharComboPath = string.IsNullOrEmpty(kvp.Value.comboPath) ? DefaultMainCharComboPath : kvp.Value.comboPath;
                        _enemyComboPath = null;
                    }
                    else
                    {
                        _enemyComboPath = kvp.Value.comboPath;
                    }

                    _charTransitionPath = kvp.Value.charTransitionPath;
                    _charBaseMovementDBPath = kvp.Value.charBaseMovementDBPath;
                    _activeStance = string.Equals(arch, "MainChar", StringComparison.OrdinalIgnoreCase) ? null : arch;

                    var keyParts = kvp.Key.Split('|');
                    _currentExportVariant = keyParts.Length > 1 ? keyParts[1] : kvp.Key;
                    _activeVariant = _currentExportVariant;

                    await PatchComboAndStanceForCurrentState(fileEntries, gameRoot, outputPath);

                    if (_perUnitProps.TryGetValue(kvp.Key, out var unitProps))
                    {
                        string variant = keyParts.Length > 1 ? keyParts[1] : kvp.Key;
                        PatchUnitProperties(unitProps, variant, fileEntries, gameRoot, outputPath);
                    }
                }

                _modifiedNodes = savedMod;
                _graph = savedGraph;
                _enemyComboPath = savedCombo;
                _activeStance = savedStance;
                _charTransitionPath = savedTrans;
                _charBaseMovementDBPath = savedMove;
                _mainCharComboPath = savedMainCharCombo;
                _activeVariant = savedVariant;
                _currentExportVariant = null;
            }
            else
            {
                _currentExportVariant = _activeVariant;
                await PatchComboAndStanceForCurrentState(fileEntries, gameRoot, outputPath);
                _currentExportVariant = null;

                if (_unitProps != null && !string.IsNullOrEmpty(_activeVariant))
                {
                    PatchUnitProperties(_unitProps, _activeVariant, fileEntries, gameRoot, outputPath);
                }
            }

            if (fileEntries.Count == 0)
            {
                ShowError("No files to export.");
                return;
            }

            ErrorLog.Write("EXPORT", new Exception($"Total files for pak: {fileEntries.Count}"));
            SetProgress(65);

            // Step 3: Build pak
            UpdateStep(3, "active");
            txtCurrentAction.Text = "Building pak file...";

            var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp_mod");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            foreach (var (src, dest) in fileEntries)
            {
                var tempDest = Path.Combine(tempDir, dest.TrimStart('/').Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(tempDest)!);
                if (File.Exists(src))
                    File.Copy(src, tempDest, true);
            }

            var filelistPath = Path.Combine(tempDir, "filelist.txt");
            var filelistContent = string.Join("\n", fileEntries.Select(f => $"\"{f.src}\" \"{f.dest}\""));
            File.WriteAllText(filelistPath, filelistContent);

            ErrorLog.Write("EXPORT", new Exception($"Filelist content:\n{filelistContent}"));

            var pakExe = Setup.ContentExtractor.FindUnrealPak();
            var pakFileName = GetModFileName();
            _pakPath = Path.Combine(outputPath, pakFileName);

            if (string.IsNullOrEmpty(pakExe) || !File.Exists(pakExe))
            {
                ShowError("UnrealPak not found. Place it in tools\\ue4\\UnrealPak\\UnrealPak.exe or set path via Settings → Open Setup.");
                return;
            }

            SetProgress(75);

            var pakArgs = $"\"{_pakPath}\" -create=\"{filelistPath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = pakExe,
                Arguments = pakArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Setup.ContentExtractor.EnsureUnrealPakWorkingDirectory()
            };

            using var proc = Process.Start(psi);
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            ErrorLog.Write("EXPORT", new Exception($"UnrealPak stdout:\n{stdout}"));
            ErrorLog.Write("EXPORT", new Exception($"UnrealPak stderr:\n{stderr}"));
            ErrorLog.Write("EXPORT", new Exception($"UnrealPak exit code: {proc.ExitCode}"));

            if (proc.ExitCode != 0)
            {
                if (proc.ExitCode == -1073741515)
                    ShowError("UnrealPak failed to start (missing DLLs).\n\nEnsure all UnrealPak-*.dll files sit next to UnrealPak.exe in tools\\ue4\\UnrealPak\\.");
                else
                    ShowError($"UnrealPak failed (exit {proc.ExitCode}):\n\n{stderr}\n{stdout}");
                return;
            }

            if (File.Exists(_pakPath))
            {
                var pakSize = new FileInfo(_pakPath).Length;
                ErrorLog.Write("EXPORT", new Exception($"Created pak size: {pakSize} bytes"));
            }
            else
            {
                ErrorLog.Write("EXPORT", new Exception("ERROR: Pak file was not created!"));
            }

            SetProgress(90);

            try
            {
                var stagingDir = Path.Combine(outputPath, "Sifu");
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            }
            catch { }

            try { Directory.Delete(tempDir, true); } catch { }

            string gameInstallDir = null;
            var searchDir = _contentPath;
            while (searchDir != null)
            {
                if (File.Exists(Path.Combine(searchDir, "Content", "Paks", "pakchunk0-WindowsNoEditor.sig")))
                {
                    gameInstallDir = searchDir;
                    break;
                }
                searchDir = Path.GetDirectoryName(searchDir);
            }

            string sigSource = null;
            var pakExeDir = Path.GetDirectoryName(pakExe);
            if (!string.IsNullOrEmpty(pakExeDir))
            {
                var beside = Path.Combine(pakExeDir, "pakchunk0-WindowsNoEditor.sig");
                if (File.Exists(beside)) sigSource = beside;
            }
            if (sigSource == null && gameInstallDir != null)
            {
                var gameSig = Path.Combine(gameInstallDir, "Content", "Paks", "pakchunk0-WindowsNoEditor.sig");
                if (File.Exists(gameSig)) sigSource = gameSig;
            }
            if (sigSource == null)
            {
                var appSig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ue4", "UnrealPak", "pakchunk0-WindowsNoEditor.sig");
                if (File.Exists(appSig)) sigSource = appSig;
                if (sigSource == null)
                {
                    appSig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "pakchunk0-WindowsNoEditor.sig");
                    if (File.Exists(appSig)) sigSource = appSig;
                }
            }

            var sigDest = Path.Combine(outputPath, Path.ChangeExtension(pakFileName, ".sig"));
            if (sigSource != null)
                File.Copy(sigSource, sigDest, true);

            SetProgress(100);
            UpdateStep(3, "done");

            var installedTo = "";
            if (gameInstallDir != null)
            {
                var modsDir = Path.Combine(gameInstallDir, "Content", "Paks", "~mods");
                Directory.CreateDirectory(modsDir);
                var destPak = Path.Combine(modsDir, pakFileName);
                File.Copy(_pakPath, destPak, true);
                installedTo = destPak;

                var gameSig = Path.Combine(gameInstallDir, "Content", "Paks", "pakchunk0-WindowsNoEditor.sig");
                var destSig = Path.Combine(modsDir, Path.ChangeExtension(pakFileName, ".sig"));
                if (File.Exists(gameSig))
                    File.Copy(gameSig, destSig, true);
            }

            ShowComplete(pakFileName, _reviewChangeCount, installedTo);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", ex);
            ShowError($"Export failed: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task PatchComboAndStanceForCurrentState(
        List<(string src, string dest)> fileEntries,
        string gameRoot,
        string outputPath)
    {
        var hasRetargets = _graph != null && _graph.RedirectOriginalTargets.Any(kvp =>
        {
            var node = _graph.Nodes.FirstOrDefault(n => n.Id == kvp.Key);
            return node != null && node.ResolvedRedirectNodeId >= 0 && node.ResolvedRedirectNodeId != kvp.Value;
        });
        var hasComboChanges = _modifiedNodes.Count > 0 || hasRetargets;
        var hasStanceChange = !string.IsNullOrEmpty(_activeStance) && _activeStance != "MainChar";

        ErrorLog.Write("EXPORT", new Exception($"=== EXPORT START: {_modifiedNodes.Count} modified nodes, stance={_activeStance ?? "MainChar"} ==="));

        if (hasComboChanges)
        {
            UpdateStep(1, "active");
            txtCurrentAction.Text = "Locating vanilla combo tree...";
            SetProgress(10);

            UpdateStep(1, "done");
            SetProgress(25);

            UpdateStep(2, "active");
            txtCurrentAction.Text = "Patching combo tree imports...";

            var mainComboGamePath = string.IsNullOrEmpty(_mainCharComboPath) ? DefaultMainCharComboPath : _mainCharComboPath;
            var mainComboRel = GamePathToContentRel(mainComboGamePath);
            var vanillaAssetPath = Path.Combine(gameRoot, mainComboRel + ".uasset");
            if (!File.Exists(vanillaAssetPath))
            {
                ShowError($"Vanilla asset not found: {vanillaAssetPath}");
                return;
            }
            var outputDirForAsset = Path.GetDirectoryName(Path.Combine(outputPath, "Sifu", "Content", mainComboRel + ".uasset"))!;
            Directory.CreateDirectory(outputDirForAsset);
            var outUasset = Path.Combine(outputPath, "Sifu", "Content", mainComboRel + ".uasset");

            bool comboTreeModified = false;
            await System.Threading.Tasks.Task.Run(() =>
            {
                var eng = EngineVersion.VER_UE4_26;
                var asset = new UAsset(vanillaAssetPath, eng, null, CustomSerializationFlags.None);

                NormalExport? comboExport = null;
                for (int i = 0; i < asset.Exports.Count; i++)
                {
                    if (asset.Exports[i] is NormalExport ne &&
                        (ne.Data?.Any(p => p.Name?.Value?.ToString() == "m_Nodes") == true ||
                         ne.SerialSize > 50000))
                    {
                        comboExport = ne;
                        break;
                    }
                }
                if (comboExport == null)
                {
                    NormalExport? largest = null;
                    foreach (var exp in asset.Exports)
                    {
                        if (exp is NormalExport ne && (largest == null || ne.SerialSize > largest.SerialSize))
                            largest = ne;
                    }
                    comboExport = largest;
                }

                if (comboExport == null)
                    throw new Exception($"Could not find Combo export in {Path.GetFileName(vanillaAssetPath)}");

                var allMaps = FindAllMapsNamed(comboExport.Data, "m_Attacks");
                ErrorLog.Write("EXPORT", new Exception($"Found {allMaps.Count} m_Attacks maps (tree={mainComboGamePath})"));

                int patched = 0;
                int skippedEmpty = 0;
                int skippedNoDb = 0;
                int patchedFallback = 0;
                int redirectSkipped = 0;
                var redirectNodes = new List<ComboNode>();
                var patchedDbFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in _modifiedNodes)
                {
                    if (!string.IsNullOrEmpty(node.DefaultDBPath) && node.DefaultDBPath.Contains("/AI/Archetypes/", StringComparison.OrdinalIgnoreCase) && _graph != null && !string.Equals(_graph.WeaponName, "MainChar", StringComparison.OrdinalIgnoreCase))
                    {
                        ErrorLog.Write("EXPORT", new Exception($"  ENEMY DEFER: {node.DisplayName} -> combo tree Import swap"));
                        continue;
                    }
                    if (string.IsNullOrEmpty(node.DefaultDBPath))
                    {
                        if (node.TreeIndex == -1 && !string.IsNullOrEmpty(node.AnimPath))
                        {
                            var templateRel = "DB/_MainChar/Combos/Attacks/BareHands/LightCombo/MainChar_Jab_FR.uasset";
                            var templateFile = Path.Combine(gameRoot, templateRel);
                            if (!File.Exists(templateFile))
                                templateFile = Directory.GetFiles(Path.Combine(gameRoot, "DB"), "MainChar_Jab*.uasset", SearchOption.AllDirectories).FirstOrDefault();
                            if (templateFile != null && File.Exists(templateFile))
                            {
                                var cloneRel = $"DB/_MainChar/Combos/Attacks/Custom/MainChar_Custom_{node.Id}";
                                var cloneOut = Path.Combine(outputPath, "Sifu", "Content", cloneRel + ".uasset");
                                Directory.CreateDirectory(Path.GetDirectoryName(cloneOut)!);
                                if (PatchDbAnimation(templateFile, node.AnimPath, cloneOut, EngineVersion.VER_UE4_26))
                                {
                                    var cloneOutUexp = Path.ChangeExtension(cloneOut, ".uexp");
                                    var templateUexp = Path.ChangeExtension(templateFile, ".uexp");
                                    if (File.Exists(templateUexp) && !File.Exists(cloneOutUexp))
                                        File.Copy(templateUexp, cloneOutUexp, true);
                                    fileEntries.Add((cloneOut, $"../../../Sifu/Content/{cloneRel}.uasset"));
                                    fileEntries.Add((cloneOutUexp, $"../../../Sifu/Content/{cloneRel}.uexp"));
                                    patched++;
                                    ErrorLog.Write("EXPORT", new Exception($"  CLONED DB for {node.DisplayName} -> {cloneRel} (anim {node.AnimPath})"));
                                    continue;
                                }
                            }
                        }
                        skippedEmpty++;
                        if (skippedEmpty <= 3)
                            ErrorLog.Write("EXPORT", new Exception($"  SKIP(empty DB): {node.DisplayName} AnimPath='{node.AnimPath}' DefaultDBPath='{node.DefaultDBPath}' DefaultAnimPath='{node.DefaultAnimPath}'"));
                        continue;
                    }

                    if (NeedsComboRedirect(node))
                    {
                        redirectNodes.Add(node);
                        redirectSkipped++;
                        ErrorLog.Write("EXPORT", new Exception($"  REDIRECT (combo tree, skip DB patch): {node.DisplayName} {Path.GetFileNameWithoutExtension(node.DefaultDBPath)} -> {Path.GetFileNameWithoutExtension(node.SourceDBPath!)}"));
                        continue;
                    }

                    if (!_animToDbPath.TryGetValue(node.AnimPath, out var newDbPath))
                    {
                        var fallbackDbPath = node.DefaultDBPath;
                        var relDbPath = fallbackDbPath.TrimStart('/');
                        if (relDbPath.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
                            relDbPath = relDbPath.Substring(5);
                        var vanillaDbFile = Path.Combine(gameRoot, relDbPath + ".uasset");
                        if (!patchedDbFiles.Contains(fallbackDbPath) && File.Exists(vanillaDbFile))
                        {
                            var outDbPath = Path.Combine(outputPath, "Sifu", "Content", relDbPath + ".uasset");

                            ErrorLog.Write("EXPORT", new Exception($"  FALLBACK ATTEMPT: {node.DisplayName} vanillaDb='{vanillaDbFile}' (source={fallbackDbPath})"));

                            if (PatchDbAnimation(vanillaDbFile, node.AnimPath, outDbPath, EngineVersion.VER_UE4_26))
                            {
                                patchedDbFiles.Add(fallbackDbPath);
                                var outDbUexp = Path.ChangeExtension(outDbPath, ".uexp");
                                var vanillaDbUexp = Path.ChangeExtension(vanillaDbFile, ".uexp");
                                if (File.Exists(vanillaDbUexp) && !File.Exists(outDbUexp))
                                    File.Copy(vanillaDbUexp, outDbUexp, true);

                                fileEntries.Add((outDbPath, "../../../Sifu/Content/" + relDbPath + ".uasset"));
                                fileEntries.Add((outDbUexp, "../../../Sifu/Content/" + relDbPath + ".uexp"));

                                patchedFallback++;
                                patched++;
                                ErrorLog.Write("EXPORT", new Exception($"  PATCHED (fallback): {node.DisplayName} -> {node.AnimPath} (modified {Path.GetFileName(fallbackDbPath)})"));
                                continue;
                            }
                        }
                        else
                        {
                            ErrorLog.Write("EXPORT", new Exception($"  FALLBACK SKIP: {node.DisplayName} vanillaDb='{vanillaDbFile}' exists={File.Exists(vanillaDbFile)} alreadyPatched={patchedDbFiles.Contains(fallbackDbPath)}"));
                        }

                        skippedNoDb++;
                        if (skippedNoDb <= 3)
                            ErrorLog.Write("EXPORT", new Exception($"  SKIP(no anim->db): {node.DisplayName} AnimPath='{node.AnimPath}' DefaultDBPath='{node.DefaultDBPath}'"));
                        continue;
                    }

                    {
                        var inplaceDbPath = node.DefaultDBPath;
                        var inplaceRel = inplaceDbPath.TrimStart('/');
                        if (inplaceRel.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
                            inplaceRel = inplaceRel.Substring(5);
                        var inplaceVanilla = Path.Combine(gameRoot, inplaceRel + ".uasset");
                        if (!patchedDbFiles.Contains(inplaceDbPath) && File.Exists(inplaceVanilla))
                        {
                            var inplaceOut = Path.Combine(outputPath, "Sifu", "Content", inplaceRel + ".uasset");
                            if (PatchDbAnimation(inplaceVanilla, node.AnimPath, inplaceOut, EngineVersion.VER_UE4_26))
                            {
                                patchedDbFiles.Add(inplaceDbPath);
                                var inplaceUexp = Path.ChangeExtension(inplaceOut, ".uexp");
                                var inplaceVanillaUexp = Path.ChangeExtension(inplaceVanilla, ".uexp");
                                if (File.Exists(inplaceVanillaUexp) && !File.Exists(inplaceUexp))
                                    File.Copy(inplaceVanillaUexp, inplaceUexp, true);
                                fileEntries.Add((inplaceOut, "../../../Sifu/Content/" + inplaceRel + ".uasset"));
                                fileEntries.Add((inplaceUexp, "../../../Sifu/Content/" + inplaceRel + ".uexp"));
                                patched++;
                                ErrorLog.Write("EXPORT", new Exception($"  PATCHED (in-place): {node.DisplayName} -> {node.AnimPath} (modified {Path.GetFileName(inplaceDbPath)})"));
                                continue;
                            }
                        }
                        skippedNoDb++;
                        if (skippedNoDb <= 3)
                            ErrorLog.Write("EXPORT", new Exception($"  SKIP(in-place failed): {node.DisplayName} AnimPath='{node.AnimPath}' DB='{inplaceDbPath}' exists={File.Exists(inplaceVanilla)}"));
                        continue;
                    }

                    var normalizedSlotKey = NormalizeSlotKey(node.DefaultDBPath);
                    var attackName = Path.GetFileNameWithoutExtension(newDbPath);
                    var attackPath = EnsureLeadingSlash(newDbPath);

                    if (string.IsNullOrEmpty(attackName) || string.IsNullOrEmpty(attackPath)) continue;

                    bool found = false;
                    foreach (var attacksMap in allMaps)
                    {
                        foreach (var kvp in attacksMap.Value)
                        {
                            string keyStr = GetKeyString(kvp.Key);
                            if (keyStr == normalizedSlotKey)
                            {
                                var valData = kvp.Value as ObjectPropertyData;
                                if (valData == null) continue;

                                int importIdx = FindImport(asset, attackName, attackPath);
                                if (importIdx < 0)
                                    importIdx = AddAttackDBImport(asset, attackName, attackPath);

                                valData.Value = FPackageIndex.FromImport(importIdx);
                                patched++;
                                ErrorLog.Write("EXPORT", new Exception($"  PATCHED: {node.DisplayName} -> {attackName} (import[{importIdx}])"));
                                found = true;
                                break;
                            }
                        }
                        if (found) break;
                    }

                    if (!found)
                        ErrorLog.Write("EXPORT", new Exception($"  NOT FOUND: {node.DisplayName} slot key '{normalizedSlotKey}'"));
                }

                if (false)
                {
                    var enemyNodes = _modifiedNodes
                        .Where(n => !string.IsNullOrEmpty(n.DefaultDBPath) && n.DefaultDBPath.Contains("/AI/Archetypes/", StringComparison.OrdinalIgnoreCase) && _graph != null && !string.Equals(_graph.WeaponName, "MainChar", StringComparison.OrdinalIgnoreCase))
                        .Select(n =>
                        {
                            var targetRelDb = n.DefaultDBPath.TrimStart('/');
                            if (targetRelDb.StartsWith("Game/", StringComparison.OrdinalIgnoreCase)) targetRelDb = targetRelDb.Substring(5);
                            var gameAttackDir = Path.GetDirectoryName(Path.Combine(gameRoot, targetRelDb + ".uasset"));
                            return (node: n, targetRelDb, gameAttackDir, targetAttackName: Path.GetFileNameWithoutExtension(targetRelDb));
                        })
                        .Where(x => x.gameAttackDir != null)
                        .ToList();

                    var dtGroups = new Dictionary<string, (string vanillaDtPath, string outDtPath, List<(string rowName, string animPath)> rows)>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (node, targetRelDb, gameAttackDir, targetAttackName) in enemyNodes)
                    {
                        var searchDir = gameAttackDir;
                        for (int level = 0; level < 4 && !string.IsNullOrEmpty(searchDir); level++)
                        {
                            if (!Directory.Exists(searchDir)) break;
                            foreach (var dtFile in Directory.GetFiles(searchDir, "*.uasset"))
                            {
                                var dtName = Path.GetFileNameWithoutExtension(dtFile);
                                if (dtName.Equals(targetAttackName, StringComparison.OrdinalIgnoreCase)) continue;
                                if (dtName.Contains("HitBoxData", StringComparison.OrdinalIgnoreCase)) continue;
                                if (!dtName.Contains("Datatable", StringComparison.OrdinalIgnoreCase) && !dtName.Contains("AttackData", StringComparison.OrdinalIgnoreCase) && !dtName.EndsWith("_Attacks", StringComparison.OrdinalIgnoreCase)) continue;

                                var outDt = Path.Combine(outputPath, "Sifu", "Content", Path.GetRelativePath(gameRoot, dtFile));
                                if (!dtGroups.TryGetValue(outDt, out var group))
                                {
                                    group = (dtFile, outDt, new List<(string, string)>());
                                    dtGroups[outDt] = group;
                                }
                                group.rows.Add((targetAttackName, node.AnimPath));
                                ErrorLog.Write("EXPORT", new Exception($"  DT CANDIDATE: {dtName} row '{targetAttackName}' anim '{node.AnimPath}' (found at level {level})"));
                            }
                            var parent = Path.GetDirectoryName(searchDir);
                            if (parent == searchDir) break;
                            searchDir = parent;
                        }
                    }

                    foreach (var (outDt, (vanillaDtPath, outDtPath, rows)) in dtGroups)
                    {
                        try
                        {
                            var dtAsset = new UAsset(vanillaDtPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
                            NormalExport? dtExport = null;
                            foreach (var exp in dtAsset.Exports)
                            {
                                if (exp is NormalExport ne && ne.Data != null) { dtExport = ne; break; }
                            }
                            if (dtExport == null) { ErrorLog.Write("EXPORT", new Exception($"  DT SKIP: No NormalExport in {Path.GetFileName(vanillaDtPath)}")); continue; }

                            var dataProps = dtExport.Data;
                            if (dataProps == null) { ErrorLog.Write("EXPORT", new Exception($"  DT SKIP: Data is null in {Path.GetFileName(vanillaDtPath)}")); continue; }

                            ErrorLog.Write("EXPORT", new Exception($"  DT STRUCTURE in {Path.GetFileNameWithoutExtension(vanillaDtPath)}: {string.Join(", ", dataProps.Select(p => $"{p.Name?.Value}({p.GetType().Name})"))}"));

                            var allRowNames = new List<string>();
                            foreach (var prop in dataProps)
                            {
                                if (prop is ArrayPropertyData arr && arr.Name?.Value?.ToString() == "RowMap")
                                {
                                    ErrorLog.Write("EXPORT", new Exception($"  DT RowMap is ArrayPropertyData with {arr.Value?.Length} elements"));
                                    foreach (var elem in arr.Value)
                                    {
                                        if (elem is StructPropertyData rowStruct)
                                        {
                                            var rn = rowStruct.Name?.Value?.ToString() ?? "(null)";
                                            allRowNames.Add(rn);
                                        }
                                        else
                                        {
                                            ErrorLog.Write("EXPORT", new Exception($"  DT RowMap elem type: {elem?.GetType().Name} name={elem?.Name?.Value}"));
                                        }
                                    }
                                }
                                else if (prop is MapPropertyData map && map.Name?.Value?.ToString() == "RowMap")
                                {
                                    ErrorLog.Write("EXPORT", new Exception($"  DT RowMap is MapPropertyData, type={map.GetType().FullName}"));
                                    try
                                    {
                                        foreach (var kvp in map.Value)
                                        {
                                            allRowNames.Add(kvp.Key?.ToString() ?? "(null)");
                                        }
                                    }
                                    catch (Exception mapEx)
                                    {
                                        ErrorLog.Write("EXPORT", new Exception($"  DT MapPropertyData iteration failed: {mapEx.Message}"));
                                    }
                                }
                                else if (prop is SetPropertyData set && set.Name?.Value?.ToString() == "RowMap")
                                {
                                    ErrorLog.Write("EXPORT", new Exception($"  DT RowMap is SetPropertyData"));
                                }
                            }
                            ErrorLog.Write("EXPORT", new Exception($"  DT ROWS in {Path.GetFileNameWithoutExtension(vanillaDtPath)}: [{string.Join(", ", allRowNames.Take(20))}{(allRowNames.Count > 20 ? ", ..." : "")}] ({allRowNames.Count} total)"));

                            int patchedRows = 0;
                            var binaryPatchRows = new List<(string rowName, string animPath)>();
                            foreach (var (rowName, animPath) in rows)
                            {
                                bool found = false;
                                foreach (var prop in dataProps)
                                {
                                    if (prop is ArrayPropertyData arr && arr.Name?.Value?.ToString() == "RowMap")
                                    {
                                        foreach (var elem in arr.Value)
                                        {
                                            if (elem is StructPropertyData rowStruct)
                                            {
                                                var rn = rowStruct.Name?.Value?.ToString();
                                                if (rn == rowName)
                                                {
                                                    if (PatchRowAnim(rowStruct, animPath, dtAsset))
                                                    {
                                                        if (_animToTiming != null) PatchRowTiming(rowStruct, animPath, _animToTiming, dtAsset);
                                                        patchedRows++;
                                                        ErrorLog.Write("EXPORT", new Exception($"  PATCHED DataTable row '{rowName}' -> {Path.GetFileName(animPath)}"));
                                                    }
                                                    found = true;
                                                    break;
                                                }
                                            }
                                        }
                                        if (found) break;
                                    }
                                }
                                if (!found)
                                {
                                    if (_animToTiming != null && _animToTiming.TryGetValue(animPath, out _))
                                    {
                                        binaryPatchRows.Add((rowName, animPath));
                                    }
                                    else
                                    {
                                        ErrorLog.Write("EXPORT", new Exception($"  DT ROW NOT FOUND: '{rowName}' in {Path.GetFileNameWithoutExtension(vanillaDtPath)}"));
                                    }
                                }
                            }

                            if (patchedRows > 0 || binaryPatchRows.Count > 0)
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(outDtPath)!);
                                dtAsset.Write(outDtPath);
                                var outDtUexp = Path.ChangeExtension(outDtPath, ".uexp");
                                var inDtUexp = Path.ChangeExtension(vanillaDtPath, ".uexp");
                                if (File.Exists(inDtUexp) && !File.Exists(outDtUexp)) File.Copy(inDtUexp, outDtUexp, true);

                                foreach (var (rowName, animPath) in binaryPatchRows)
                                {
                                    BinaryPatchDataTableTiming(vanillaDtPath, outDtPath, rowName, 0, 0, _animToTiming!, animPath);
                                    patchedRows++;
                                }

                                var stagingContentDir = Path.Combine(outputPath, "Sifu", "Content");
                                var relDt = Path.GetRelativePath(stagingContentDir, outDtPath).Replace('\\', '/');
                                fileEntries.Add((outDtPath, "../../../Sifu/Content/" + relDt));
                                fileEntries.Add((outDtUexp, "../../../Sifu/Content/" + relDt.Replace(".uasset", ".uexp")));
                                ErrorLog.Write("EXPORT", new Exception($"  Wrote DataTable {Path.GetFileNameWithoutExtension(outDtPath)}: {patchedRows}/{rows.Count} rows patched ({new FileInfo(outDtPath).Length}B)"));
                            }
                        }
                        catch (Exception ex)
                        {
                            ErrorLog.Write("EXPORT", new Exception($"  DT FAIL: {Path.GetFileName(vanillaDtPath)}: {ex.Message}"));
                        }
                    }
                }

                ErrorLog.Write("EXPORT", new Exception($"Patched {patched}/{_modifiedNodes.Count} nodes (direct: {patched - patchedFallback}, fallback DB: {patchedFallback}, combo redirects: {redirectSkipped}, skipped empty DB: {skippedEmpty}, skipped no anim->db: {skippedNoDb})"));

                int comboSwaps = 0;
                var mainSwaps = new List<(string oldShortName, string newShortName, string newFullPath)>();
                {
                    foreach (var node in _modifiedNodes)
                    {
                        if (string.IsNullOrEmpty(node.DefaultDBPath) || string.IsNullOrEmpty(node.AnimPath))
                            continue;

                        string oldDbShortName = Path.GetFileNameWithoutExtension(node.DefaultDBPath);
                        string newDbPath = string.IsNullOrEmpty(node.SourceDBPath) ? "" : node.SourceDBPath;
                        if (string.IsNullOrEmpty(newDbPath)) continue;
                        string newDbShortName = Path.GetFileNameWithoutExtension(newDbPath);
                        string newDbFullPath = "/" + newDbPath.TrimStart('/');
                        if (newDbFullPath.StartsWith("//", StringComparison.Ordinal))
                            newDbFullPath = newDbFullPath.Substring(1);

                        if (oldDbShortName == newDbShortName) continue;

                        bool swappedThis = false;
                        foreach (var attacksMap in allMaps)
                        {
                            foreach (var kvp in attacksMap.Value)
                            {
                                string keyStr = GetKeyString(kvp.Key);
                                string normalizedDefault = NormalizeSlotKey(node.DefaultDBPath);
                                if (keyStr != normalizedDefault) continue;

                                var valData = kvp.Value as ObjectPropertyData;
                                if (valData?.Value == null) continue;

                                int curImportIdx = -(valData.Value.Index + 1);
                                if (curImportIdx < 0 || curImportIdx >= asset.Imports.Count) continue;

                                var curImport = asset.Imports[curImportIdx];
                                string curDbName = curImport.ObjectName?.Value?.ToString() ?? "";

                                int pkgOuterIdx = -(curImport.OuterIndex.Index + 1);
                                string curPkgPath = "";
                                if (pkgOuterIdx >= 0 && pkgOuterIdx < asset.Imports.Count)
                                    curPkgPath = asset.Imports[pkgOuterIdx].ObjectName?.Value?.ToString() ?? "";

                                if (!curPkgPath.Contains(oldDbShortName, StringComparison.OrdinalIgnoreCase) &&
                                    curDbName != oldDbShortName)
                                {
                                    ErrorLog.Write("EXPORT", new Exception($"  COMBO SKIP: {node.DisplayName} import[{curImportIdx}] name='{curDbName}' pkg='{curPkgPath}' != expected '{oldDbShortName}'"));
                                    break;
                                }

                                curImport.ObjectName = FName.FromString(asset, newDbShortName);

                                if (pkgOuterIdx >= 0 && pkgOuterIdx < asset.Imports.Count)
                                    asset.Imports[pkgOuterIdx].ObjectName = FName.FromString(asset, newDbFullPath);

                                comboSwaps++;
                                swappedThis = true;
                                mainSwaps.Add((oldDbShortName, newDbShortName, newDbFullPath));
                                ErrorLog.Write("EXPORT", new Exception($"  COMBO IMPORT SWAP: {oldDbShortName} -> {newDbShortName} (import[{curImportIdx}])"));
                                break;
                            }
                            if (swappedThis) break;
                        }

                        if (!swappedThis && redirectNodes.Any(r => Path.GetFileNameWithoutExtension(r.DefaultDBPath) == oldDbShortName))
                        {
                            string attackName = newDbShortName;
                            string attackPath = newDbFullPath;
                            int importIdx = FindImport(asset, attackName, attackPath);
                            if (importIdx < 0)
                                importIdx = AddAttackDBImport(asset, attackName, attackPath);

                            bool mapPatched = false;
                            foreach (var attacksMap in allMaps)
                            {
                                foreach (var kvp in attacksMap.Value)
                                {
                                    string keyStr = GetKeyString(kvp.Key);
                                    if (keyStr != NormalizeSlotKey(node.DefaultDBPath)) continue;
                                    if (kvp.Value is not ObjectPropertyData od || od.Value == null) continue;
                                    od.Value = FPackageIndex.FromImport(importIdx);
                                    mapPatched = true;
                                    break;
                                }
                                if (mapPatched) break;
                            }

                            if (mapPatched)
                            {
                                comboSwaps++;
                                mainSwaps.Add((oldDbShortName, newDbShortName, newDbFullPath));
                                ErrorLog.Write("EXPORT", new Exception($"  COMBO MAP REPOINT: {oldDbShortName} -> {newDbShortName} (import[{importIdx}])"));
                            }
                        }
                    }

                    if (mainSwaps.Count > 0)
                    {
                        var swapDict = mainSwaps
                            .GroupBy(s => s.oldShortName, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                        var nodesProp = comboExport.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Nodes");
                        if (nodesProp is ArrayPropertyData nodesArr)
                        {
                            int fNamesPatched = 0;
                            foreach (var elem in nodesArr.Value)
                            {
                                if (elem is not StructPropertyData nodeStruct || nodeStruct.Value == null) continue;

                                var attackInfos = nodeStruct.Value
                                    .OfType<StructPropertyData>()
                                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos");
                                if (attackInfos?.Value == null) continue;

                                foreach (var field in attackInfos.Value)
                                {
                                    if (field is not NamePropertyData nameProp) continue;
                                    if (nameProp.Name?.Value?.ToString() != "m_Attacks") continue;

                                    string curPath = nameProp.Value?.Value?.ToString() ?? "";
                                    if (string.IsNullOrEmpty(curPath) || curPath == "None") continue;

                                    string curShortName = Path.GetFileNameWithoutExtension(curPath);
                                    if (!swapDict.TryGetValue(curShortName, out var swap)) continue;

                                    string newFNamePath = swap.newFullPath + "." + swap.newShortName;
                                    nameProp.Value = FName.FromString(asset, newFNamePath);
                                    fNamesPatched++;
                                }
                            }
                            if (fNamesPatched > 0)
                            {
                                comboTreeModified = true;
                                ErrorLog.Write("EXPORT", new Exception($"  COMBO FName PATCH: {fNamesPatched} m_Attacks paths in m_Nodes"));
                            }
                        }

                        foreach (var attacksMap in allMaps)
                        {
                            if (attacksMap.Value == null) continue;
                            var rebuilt = new TMap<PropertyData, PropertyData>();
                            bool mapKeyChanged = false;
                            foreach (var kvp in attacksMap.Value)
                            {
                                string keyStr = GetKeyString(kvp.Key);
                                string shortKey = Path.GetFileNameWithoutExtension(keyStr);
                                if (swapDict.TryGetValue(shortKey, out var mapSwap))
                                {
                                    string newFNamePath = mapSwap.newFullPath + "." + mapSwap.newShortName;
                                    var newKeyNameProp = new NamePropertyData
                                    {
                                        Name = new FName(asset, "m_Attacks"),
                                        Value = FName.FromString(asset, newFNamePath)
                                    };
                                    rebuilt.Add(newKeyNameProp, kvp.Value);
                                    mapKeyChanged = true;
                                    ErrorLog.Write("EXPORT", new Exception($"  COMBO MAP KEY: {shortKey} -> {newFNamePath}"));
                                }
                                else
                                {
                                    rebuilt.Add(kvp.Key, kvp.Value);
                                }
                            }
                            if (mapKeyChanged)
                            {
                                attacksMap.Value = rebuilt;
                                comboTreeModified = true;
                            }
                        }
                    }
                }

                ErrorLog.Write("EXPORT", new Exception($"Combo tree swaps: {comboSwaps}"));

                if (_graph != null && string.IsNullOrEmpty(_enemyComboPath))
                {
                    var nodesProp = comboExport.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Nodes");
                    if (nodesProp is ArrayPropertyData nodesArr)
                    {
                        int redirectPatched = 0;
                        foreach (var nodeTag in nodesArr.Value)
                        {
                            if (nodeTag is StructPropertyData nodeSp && nodeSp.Value != null)
                            {
                                var nameProp = nodeSp.Value.OfType<NamePropertyData>().FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Name");
                                var nodeName = nameProp?.Value?.Value?.ToString() ?? "";
                                var redirectProp = nodeSp.Value.OfType<IntPropertyData>().FirstOrDefault(p => p.Name?.Value?.ToString() == "m_NodeRedirect");
                                if (redirectProp != null && redirectProp.Value >= 0)
                                {
                                    int treeIndex = Array.IndexOf(nodesArr.Value, nodeTag);
                                    var graphNode = _graph.Nodes.FirstOrDefault(n => n.TreeIndex == treeIndex && n.IsRedirect);
                                    if (graphNode != null && _graph.RedirectOriginalTargets.TryGetValue(graphNode.Id, out var origTarget))
                                    {
                                        int newTarget = graphNode.ResolvedRedirectNodeId;
                                        if (newTarget >= 0 && newTarget != origTarget)
                                        {
                                            var newTargetNode = _graph.Nodes.FirstOrDefault(n => n.Id == newTarget);
                                            if (newTargetNode != null && newTargetNode.TreeIndex >= 0)
                                            {
                                                redirectProp.Value = newTargetNode.TreeIndex;
                                                redirectPatched++;
                                                ErrorLog.Write("EXPORT", new Exception($"  REDIRECT PATCH: [{treeIndex}] {nodeName} redirect {origTarget} -> {newTargetNode.TreeIndex} ({newTargetNode.DisplayName})"));
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        if (redirectPatched > 0)
                        {
                            comboTreeModified = true;
                            ErrorLog.Write("EXPORT", new Exception($"Redirect patches: {redirectPatched}"));
                        }
                    }
                }

                comboTreeModified = comboSwaps > 0 || comboTreeModified;

                var swappedShortNames = new HashSet<string>(mainSwaps.Select(s => s.oldShortName), StringComparer.OrdinalIgnoreCase);
                foreach (var node in redirectNodes)
                {
                    string slotShort = Path.GetFileNameWithoutExtension(node.DefaultDBPath);
                    if (swappedShortNames.Contains(slotShort)) continue;

                    var relDbPath = node.DefaultDBPath.TrimStart('/');
                    if (relDbPath.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
                        relDbPath = relDbPath.Substring(5);
                    var vanillaDbFile = Path.Combine(gameRoot, relDbPath + ".uasset");
                    if (patchedDbFiles.Contains(node.DefaultDBPath) || !File.Exists(vanillaDbFile))
                    {
                        ErrorLog.Write("EXPORT", new Exception($"  REDIRECT FAILED (no combo slot): {node.DisplayName} slot={slotShort} — move may not apply"));
                        continue;
                    }

                    var outDbPath = Path.Combine(outputPath, "Sifu", "Content", relDbPath + ".uasset");
                    if (PatchDbAnimation(vanillaDbFile, node.AnimPath, outDbPath, EngineVersion.VER_UE4_26))
                    {
                        patchedDbFiles.Add(node.DefaultDBPath);
                        var outUexp = Path.ChangeExtension(outDbPath, ".uexp");
                        var vanillaUexp = Path.ChangeExtension(vanillaDbFile, ".uexp");
                        if (File.Exists(vanillaUexp) && !File.Exists(outUexp))
                            File.Copy(vanillaUexp, outUexp, true);
                        fileEntries.Add((outDbPath, "../../../Sifu/Content/" + relDbPath + ".uasset"));
                        fileEntries.Add((outUexp, "../../../Sifu/Content/" + relDbPath + ".uexp"));
                        patched++;
                        ErrorLog.Write("EXPORT", new Exception($"  REDIRECT FALLBACK DB PATCH: {node.DisplayName} -> {node.AnimPath} (combo slot '{slotShort}' not found in tree)"));
                    }
                }

                if (redirectSkipped > 0 && comboSwaps == 0)
                {
                    ErrorLog.Write("EXPORT", new Exception($"WARNING: {redirectSkipped} redirect(s) requested but 0 combo tree swaps applied — weapon tree may be missing slot keys for this graph"));
                }
                if (comboTreeModified)
                {
                    asset.Write(outUasset);

                    var comboVanillaUexp = Path.ChangeExtension(vanillaAssetPath, ".uexp");
                    var comboOutUexp = Path.ChangeExtension(outUasset, ".uexp");
                    if (File.Exists(comboVanillaUexp))
                        File.Copy(comboVanillaUexp, comboOutUexp, true);
                }
            });

            UpdateStep(2, "done");
            SetProgress(50);

            if (comboTreeModified)
            {
                fileEntries.Add((outUasset, "../../../Sifu/Content/" + mainComboRel + ".uasset"));
                fileEntries.Add((Path.ChangeExtension(outUasset, ".uexp"),
                    "../../../Sifu/Content/" + mainComboRel + ".uexp"));
            }

            if (!string.IsNullOrEmpty(_enemyComboPath))
            {
                var enemyComboRelPath = GamePathToContentRel(_enemyComboPath);
                var enemyVanillaPath = Path.Combine(gameRoot, enemyComboRelPath + ".uasset");
                if (File.Exists(enemyVanillaPath))
                {
                    var enemyOutDir = Path.Combine(outputPath, "Sifu", "Content", Path.GetDirectoryName(enemyComboRelPath)!);
                    Directory.CreateDirectory(enemyOutDir);
                    var enemyOutUasset = Path.Combine(enemyOutDir, Path.GetFileName(enemyComboRelPath) + ".uasset");

                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        var eng = EngineVersion.VER_UE4_26;
                        var enemyAsset = new UAsset(enemyVanillaPath, eng, null, CustomSerializationFlags.None);

                        NormalExport? enemyComboExport = null;
                        for (int i = 0; i < enemyAsset.Exports.Count; i++)
                        {
                            if (enemyAsset.Exports[i] is NormalExport ne && ne.SerialSize > 1000)
                            {
                                enemyComboExport = ne;
                                break;
                            }
                        }

                        if (enemyComboExport == null)
                        {
                            ErrorLog.Write("EXPORT", new Exception($"Could not find enemy Combo export in {enemyVanillaPath}"));
                            return;
                        }

                        ErrorLog.Write("EXPORT", new Exception($"Loaded enemy combo tree: {enemyVanillaPath}"));

                        var enemyNodesArr = enemyComboExport.Data.OfType<ArrayPropertyData>()
                            .FirstOrDefault(p => p.Name.Value.ToString() == "m_Nodes");
                        if (enemyNodesArr == null || enemyNodesArr.Value == null || enemyNodesArr.Value.Length == 0)
                        {
                            ErrorLog.Write("EXPORT", new Exception("Enemy combo tree has no m_Nodes array"));
                            return;
                        }

                        var enemyComboBinSwaps = new List<(string oldShortName, string newShortName, string newFullPath)>();
                        {
                            var enemyAllMaps = FindAllMapsNamed(enemyComboExport.Data, "m_Attacks");
                            ErrorLog.Write("EXPORT", new Exception($"Enemy combo tree: found {enemyAllMaps.Count} m_Attacks maps, {enemyAsset.Imports.Count} imports"));

                            var enemyModifiedNodes = _modifiedNodes
                                .Where(n => !string.IsNullOrEmpty(n.DefaultDBPath)
                                    && n.DefaultDBPath.Contains("/AI/Archetypes/", StringComparison.OrdinalIgnoreCase)
                                    && n.TreeIndex >= 0)
                                .ToList();
                            ErrorLog.Write("EXPORT", new Exception($"Enemy combo Import swap: {enemyModifiedNodes.Count} modified existing nodes"));

                            foreach (var node in enemyModifiedNodes)
                            {
                                string oldDbShortName = Path.GetFileNameWithoutExtension(node.DefaultDBPath);
                                string newDbPath = string.IsNullOrEmpty(node.SourceDBPath) ? "" : node.SourceDBPath;
                                if (string.IsNullOrEmpty(newDbPath)) continue;
                                string newDbShortName = Path.GetFileNameWithoutExtension(newDbPath);
                                string newDbFullPath = "/" + newDbPath.TrimStart('/');

                                if (oldDbShortName == newDbShortName) continue;

                                bool swapped = false;
                                foreach (var attacksMap in enemyAllMaps)
                                {
                                    foreach (var kvp in attacksMap.Value)
                                    {
                                        string keyStr = GetKeyString(kvp.Key);
                                        string normalizedDefault = NormalizeSlotKey(node.DefaultDBPath);
                                        if (keyStr != normalizedDefault) continue;

                                        var valData = kvp.Value as ObjectPropertyData;
                                        if (valData?.Value == null) continue;

                                        int curImportIdx = -(valData.Value.Index + 1);
                                        if (curImportIdx < 0 || curImportIdx >= enemyAsset.Imports.Count) continue;

                                        var curImport = enemyAsset.Imports[curImportIdx];
                                        string curDbName = curImport.ObjectName?.Value?.ToString() ?? "";

                                        int pkgOuterIdx = -(curImport.OuterIndex.Index + 1);
                                        string curPkgPath = "";
                                        if (pkgOuterIdx >= 0 && pkgOuterIdx < enemyAsset.Imports.Count)
                                            curPkgPath = enemyAsset.Imports[pkgOuterIdx].ObjectName?.Value?.ToString() ?? "";

                                        if (!curPkgPath.Contains(oldDbShortName, StringComparison.OrdinalIgnoreCase) &&
                                            curDbName != oldDbShortName)
                                        {
                                            ErrorLog.Write("EXPORT", new Exception($"  ENEMY COMBO SKIP: {node.DisplayName} import[{curImportIdx}] name='{curDbName}' pkg='{curPkgPath}' != expected '{oldDbShortName}'"));
                                            break;
                                        }

                                        enemyComboBinSwaps.Add((oldDbShortName, newDbShortName, newDbFullPath));
                                        swapped = true;
                                        ErrorLog.Write("EXPORT", new Exception($"  ENEMY COMBO IMPORT SWAP: {oldDbShortName} -> {newDbShortName} (import[{curImportIdx}])"));
                                        break;
                                    }
                                    if (swapped) break;
                                }
                            }
                        }

                        ErrorLog.Write("EXPORT", new Exception($"Enemy combo tree swaps: {enemyComboBinSwaps.Count}"));

                        if (enemyComboBinSwaps.Count > 0)
                        {
                            try
                            {
                                var processedSwaps = new HashSet<string>();
                                foreach (var (oldShortName, newShortName, newFullPath) in enemyComboBinSwaps)
                                {
                                    for (int i = 0; i < enemyAsset.Imports.Count; i++)
                                    {
                                        var imp = enemyAsset.Imports[i];
                                        string objName = imp.ObjectName?.Value?.ToString() ?? "";

                                        if (objName == oldShortName)
                                        {
                                            int outerIdx = -(imp.OuterIndex.Index + 1);

                                            if (!processedSwaps.Contains(oldShortName))
                                            {
                                                imp.ObjectName = FName.FromString(enemyAsset, newShortName);
                                                ErrorLog.Write("EXPORT", new Exception($"  UAPI SWAP import[{i}]: {oldShortName} -> {newShortName}"));
                                            }

                                            if (outerIdx >= 0 && outerIdx < enemyAsset.Imports.Count && !processedSwaps.Contains(newFullPath))
                                            {
                                                enemyAsset.Imports[outerIdx].ObjectName = FName.FromString(enemyAsset, newFullPath);
                                                ErrorLog.Write("EXPORT", new Exception($"  UAPI SWAP outer[{outerIdx}]: -> {newFullPath}"));
                                            }

                                            processedSwaps.Add(oldShortName);
                                            processedSwaps.Add(newFullPath);
                                            break;
                                        }
                                    }
                                }

                                var swapDict = enemyComboBinSwaps.ToDictionary(
                                    s => s.oldShortName,
                                    s => (s.newShortName, s.newFullPath));

                                int fNamesPatched = 0;
                                foreach (var elem in enemyNodesArr.Value)
                                {
                                    if (elem is not StructPropertyData nodeStruct || nodeStruct.Value == null) continue;

                                    var attackInfos = nodeStruct.Value
                                        .OfType<StructPropertyData>()
                                        .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos");
                                    if (attackInfos?.Value == null) continue;

                                    foreach (var field in attackInfos.Value)
                                    {
                                        if (field is not NamePropertyData nameProp) continue;
                                        if (nameProp.Name?.Value?.ToString() != "m_Attacks") continue;

                                        string curPath = nameProp.Value?.Value?.ToString() ?? "";
                                        if (string.IsNullOrEmpty(curPath) || curPath == "None") continue;

                                        string curShortName = Path.GetFileNameWithoutExtension(curPath);
                                        if (!swapDict.TryGetValue(curShortName, out var swap)) continue;

                                        string newFNamePath = swap.newFullPath + "." + swap.newShortName;
                                        nameProp.Value = FName.FromString(enemyAsset, newFNamePath);
                                        fNamesPatched++;
                                    }
                                }
                                ErrorLog.Write("EXPORT", new Exception($"  UAPI: patched {fNamesPatched} FName paths in m_Nodes"));

                                var mAttacksMap = enemyComboExport.Data
                                    .OfType<MapPropertyData>()
                                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Attacks");
                                if (mAttacksMap?.Value != null)
                                {
                                    var rebuilt = new TMap<PropertyData, PropertyData>();
                                    foreach (var kvp in mAttacksMap.Value)
                                    {
                                        string keyStr = GetKeyString(kvp.Key);
                                        string shortKey = Path.GetFileNameWithoutExtension(keyStr);
                                        if (swapDict.TryGetValue(shortKey, out var mapSwap))
                                        {
                                            string newFNamePath = mapSwap.newFullPath + "." + mapSwap.newShortName;
                                            var newKeyNameProp = new NamePropertyData
                                            {
                                                Name = new FName(enemyAsset, "m_Attacks"),
                                                Value = FName.FromString(enemyAsset, newFNamePath)
                                            };
                                            rebuilt.Add(newKeyNameProp, kvp.Value);
                                            ErrorLog.Write("EXPORT", new Exception($"  UAPI MAP KEY: {shortKey} -> {newFNamePath}"));
                                        }
                                        else
                                        {
                                            rebuilt.Add(kvp.Key, kvp.Value);
                                        }
                                    }
                                    mAttacksMap.Value = rebuilt;
                                }
                            }
                            catch (Exception ex)
                            {
                                ErrorLog.Write("EXPORT", new Exception($"  UAPI SWAP FAIL: {ex.Message}\n{ex.StackTrace}"));
                            }
                            }

                            if (_graph != null)
                            {
                                int enemyRedirectPatched = 0;
                                foreach (var nodeTag in enemyNodesArr.Value)
                                {
                                    if (nodeTag is StructPropertyData nodeSp && nodeSp.Value != null)
                                    {
                                        var nameProp = nodeSp.Value.OfType<NamePropertyData>().FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Name");
                                        var nodeName = nameProp?.Value?.Value?.ToString() ?? "";
                                        var redirectProp = nodeSp.Value.OfType<IntPropertyData>().FirstOrDefault(p => p.Name?.Value?.ToString() == "m_NodeRedirect");
                                        if (redirectProp != null && redirectProp.Value >= 0)
                                        {
                                            int treeIndex = Array.IndexOf(enemyNodesArr.Value, nodeTag);
                                            var graphNode = _graph.Nodes.FirstOrDefault(n => n.TreeIndex == treeIndex && n.IsRedirect);
                                            if (graphNode != null && _graph.RedirectOriginalTargets.TryGetValue(graphNode.Id, out var origTarget))
                                            {
                                                int newTarget = graphNode.ResolvedRedirectNodeId;
                                                if (newTarget >= 0 && newTarget != origTarget)
                                                {
                                                    var newTargetNode = _graph.Nodes.FirstOrDefault(n => n.Id == newTarget);
                                                    if (newTargetNode != null && newTargetNode.TreeIndex >= 0)
                                                    {
                                                        redirectProp.Value = newTargetNode.TreeIndex;
                                                        enemyRedirectPatched++;
                                                        ErrorLog.Write("EXPORT", new Exception($"  ENEMY REDIRECT PATCH: [{treeIndex}] {nodeName} redirect {origTarget} -> {newTargetNode.TreeIndex} ({newTargetNode.DisplayName})"));
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                if (enemyRedirectPatched > 0)
                                    ErrorLog.Write("EXPORT", new Exception($"Enemy redirect patches: {enemyRedirectPatched}"));
                            }

                            Directory.CreateDirectory(Path.GetDirectoryName(enemyOutUasset)!);
                            enemyAsset.Write(enemyOutUasset);
                            ErrorLog.Write("EXPORT", new Exception($"  UAPI: wrote {new FileInfo(enemyOutUasset).Length} bytes .uasset"));
                            try { var dbgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_export"); Directory.CreateDirectory(dbgDir); File.Copy(enemyOutUasset, Path.Combine(dbgDir, "debug_combo.uasset"), true); var uexpSrc = Path.ChangeExtension(enemyOutUasset, ".uexp"); if (File.Exists(uexpSrc)) File.Copy(uexpSrc, Path.Combine(dbgDir, "debug_combo.uexp"), true); } catch { }
                            var outUexpPath = Path.ChangeExtension(enemyOutUasset, ".uexp");
                            if (File.Exists(outUexpPath))
                                ErrorLog.Write("EXPORT", new Exception($"  UAPI: wrote {new FileInfo(outUexpPath).Length} bytes .uexp"));

                        var comboPathParts = enemyComboRelPath.Replace('\\', '/').Split('/');
                        int archetypesIdx = Array.IndexOf(comboPathParts, "Archetypes");
                        if (archetypesIdx >= 0 && archetypesIdx + 1 < comboPathParts.Length)
                        {
                            string charRoot = string.Join("/", comboPathParts.Take(archetypesIdx + 2));
                            string charArenaDir = Path.Combine(gameRoot, charRoot, "_Arena");
                            if (enemyComboBinSwaps.Count == 0)
                            {
                                ErrorLog.Write("EXPORT", new Exception("  ARENA SKIP: no attack swaps for this unit"));
                            }
                            else if (Directory.Exists(charArenaDir))
                            {
                                var arenaComboFiles = Directory.GetFiles(charArenaDir, "*.uasset", SearchOption.AllDirectories)
                                    .Where(f => Path.GetFileNameWithoutExtension(f).Contains("Combo", StringComparison.OrdinalIgnoreCase))
                                    .ToList();

                                foreach (var arenaComboVanilla in arenaComboFiles)
                                {
                                    var arenaComboRelFromContent = Path.GetRelativePath(Path.Combine(gameRoot), arenaComboVanilla).Replace('\\', '/');
                                    if (arenaComboRelFromContent.EndsWith(".uasset"))
                                        arenaComboRelFromContent = arenaComboRelFromContent[..^7];

                                    string arenaFileName = Path.GetFileNameWithoutExtension(arenaComboVanilla);
                                    var arenaOutDirCheck = Path.Combine(outputPath, "Sifu", "Content", Path.GetDirectoryName(arenaComboRelFromContent)!);
                                    var arenaOutUassetCheck = Path.Combine(arenaOutDirCheck, Path.GetFileName(arenaComboRelFromContent) + ".uasset");

                                    if (_writtenArenaPaths.Contains(arenaOutUassetCheck))
                                    {
                                        ErrorLog.Write("EXPORT", new Exception($"  ARENA SKIP already written: {arenaFileName}"));
                                        continue;
                                    }

                                    int arenaPhase = ExtractPhaseFromName(arenaFileName);
                                    int unitPhase = ExtractPhaseFromName(_currentExportVariant ?? "");
                                    if (arenaPhase != 0 && arenaPhase != unitPhase)
                                    {
                                        ErrorLog.Write("EXPORT", new Exception($"  ARENA SKIP phase mismatch: {arenaFileName} (arena P{arenaPhase} vs unit '{_currentExportVariant}' P{unitPhase})"));
                                        continue;
                                    }

                                    try
                                    {
                                        var arenaEng = EngineVersion.VER_UE4_26;
                                        var arenaAsset = new UAsset(arenaComboVanilla, arenaEng, null, CustomSerializationFlags.None);

                                        NormalExport? arenaComboExport = null;
                                        for (int i = 0; i < arenaAsset.Exports.Count; i++)
                                        {
                                            if (arenaAsset.Exports[i] is NormalExport ne && ne.SerialSize > 1000)
                                            {
                                                arenaComboExport = ne;
                                                break;
                                            }
                                        }
                                        if (arenaComboExport == null)
                                        {
                                            ErrorLog.Write("EXPORT", new Exception($"  ARENA SKIP: no combo export in {arenaFileName}"));
                                            continue;
                                        }

                                        var arenaNodesArr = arenaComboExport.Data.OfType<ArrayPropertyData>()
                                            .FirstOrDefault(p => p.Name.Value.ToString() == "m_Nodes");
                                        if (arenaNodesArr?.Value == null || arenaNodesArr.Value.Length == 0)
                                        {
                                            ErrorLog.Write("EXPORT", new Exception($"  ARENA SKIP: no m_Nodes in {arenaFileName}"));
                                            continue;
                                        }

                                        var arenaSwapDict = enemyComboBinSwaps.ToDictionary(
                                            s => s.oldShortName,
                                            s => (s.newShortName, s.newFullPath));

                                        var arenaProcessedSwaps = new HashSet<string>();
                                        foreach (var (oldShortName, newShortName, newFullPath) in enemyComboBinSwaps)
                                        {
                                            for (int i = 0; i < arenaAsset.Imports.Count; i++)
                                            {
                                                var imp = arenaAsset.Imports[i];
                                                string objName = imp.ObjectName?.Value?.ToString() ?? "";
                                                if (objName == oldShortName)
                                                {
                                                    int outerIdx = -(imp.OuterIndex.Index + 1);
                                                    if (!arenaProcessedSwaps.Contains(oldShortName))
                                                    {
                                                        imp.ObjectName = FName.FromString(arenaAsset, newShortName);
                                                    }
                                                    if (outerIdx >= 0 && outerIdx < arenaAsset.Imports.Count && !arenaProcessedSwaps.Contains(newFullPath))
                                                    {
                                                        arenaAsset.Imports[outerIdx].ObjectName = FName.FromString(arenaAsset, newFullPath);
                                                    }
                                                    arenaProcessedSwaps.Add(oldShortName);
                                                    arenaProcessedSwaps.Add(newFullPath);
                                                    break;
                                                }
                                            }
                                        }

                                        int arenaFNamesPatched = 0;
                                        foreach (var elem in arenaNodesArr.Value)
                                        {
                                            if (elem is not StructPropertyData nodeStruct || nodeStruct.Value == null) continue;
                                            var attackInfos = nodeStruct.Value
                                                .OfType<StructPropertyData>()
                                                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos");
                                            if (attackInfos?.Value == null) continue;
                                            foreach (var field in attackInfos.Value)
                                            {
                                                if (field is not NamePropertyData nameProp) continue;
                                                if (nameProp.Name?.Value?.ToString() != "m_Attacks") continue;
                                                string curPath = nameProp.Value?.Value?.ToString() ?? "";
                                                if (string.IsNullOrEmpty(curPath) || curPath == "None") continue;
                                                string curShortName = Path.GetFileNameWithoutExtension(curPath);
                                                if (!arenaSwapDict.TryGetValue(curShortName, out var swap)) continue;
                                                string newFNamePath = swap.newFullPath + "." + swap.newShortName;
                                                nameProp.Value = FName.FromString(arenaAsset, newFNamePath);
                                                arenaFNamesPatched++;
                                            }
                                        }

                                        var arenaMap = arenaComboExport.Data
                                            .OfType<MapPropertyData>()
                                            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Attacks");
                                        if (arenaMap?.Value != null)
                                        {
                                            var rebuilt = new TMap<PropertyData, PropertyData>();
                                            foreach (var kvp in arenaMap.Value)
                                            {
                                                string keyStr = GetKeyString(kvp.Key);
                                                string shortKey = Path.GetFileNameWithoutExtension(keyStr);
                                                if (arenaSwapDict.TryGetValue(shortKey, out var mapSwap))
                                                {
                                                    string newFNamePath = mapSwap.newFullPath + "." + mapSwap.newShortName;
                                                    var newKeyNameProp = new NamePropertyData
                                                    {
                                                        Name = new FName(arenaAsset, "m_Attacks"),
                                                        Value = FName.FromString(arenaAsset, newFNamePath)
                                                    };
                                                    rebuilt.Add(newKeyNameProp, kvp.Value);
                                                }
                                                else
                                                {
                                                    rebuilt.Add(kvp.Key, kvp.Value);
                                                }
                                            }
                                            arenaMap.Value = rebuilt;
                                        }

                                        if (arenaFNamesPatched == 0 && arenaProcessedSwaps.Count == 0)
                                        {
                                            ErrorLog.Write("EXPORT", new Exception($"  ARENA SKIP no matching attacks: {arenaFileName}"));
                                            continue;
                                        }

                                        var arenaOutDir = Path.Combine(outputPath, "Sifu", "Content", Path.GetDirectoryName(arenaComboRelFromContent)!);
                                        Directory.CreateDirectory(arenaOutDir);
                                        var arenaOutUasset = Path.Combine(arenaOutDir, Path.GetFileName(arenaComboRelFromContent) + ".uasset");
                                        arenaAsset.Write(arenaOutUasset);
                                        _writtenArenaPaths.Add(arenaOutUasset);
                                        var arenaOutUexp = Path.ChangeExtension(arenaOutUasset, ".uexp");

                                        fileEntries.Add((arenaOutUasset,
                                            "../../../Sifu/Content/" + arenaComboRelFromContent + ".uasset"));
                                        fileEntries.Add((arenaOutUexp,
                                            "../../../Sifu/Content/" + arenaComboRelFromContent + ".uexp"));

                                        ErrorLog.Write("EXPORT", new Exception($"  ARENA: patched {arenaFileName} ({new FileInfo(arenaOutUasset).Length} bytes .uasset, {arenaFNamesPatched} FNames, {arenaProcessedSwaps.Count} import swaps, unit '{_currentExportVariant}')"));
                                    }
                                    catch (Exception ex)
                                    {
                                        ErrorLog.Write("EXPORT", new Exception($"  ARENA FAIL: {arenaFileName}: {ex.Message}"));
                                    }
                                }
                            }
                        }
                        });

                    if (File.Exists(enemyOutUasset))
                    {
                        fileEntries.Add((enemyOutUasset,
                            "../../../Sifu/Content/" + enemyComboRelPath + ".uasset"));
                        fileEntries.Add((Path.ChangeExtension(enemyOutUasset, ".uexp"),
                            "../../../Sifu/Content/" + enemyComboRelPath + ".uexp"));
                    }
                }
                else
                {
                    ErrorLog.Write("EXPORT", new Exception($"Enemy combo file not found: {enemyVanillaPath}"));
                }
            }
        }
        else
        {
            UpdateStep(1, "done");
            UpdateStep(2, "done");
            SetProgress(50);
        }

        if (hasStanceChange)
        {
            if (!string.IsNullOrEmpty(_charTransitionPath))
            {
                txtCurrentAction.Text = "Patching BP_TransitionAnimRequest...";
                var vanillaTransPath = Path.Combine(gameRoot, "DB/Movement/Transition/BP_TransitionAnimRequest.uasset");
                var charTransPath = Path.Combine(gameRoot, _charTransitionPath + ".uasset");

                if (File.Exists(vanillaTransPath) && File.Exists(charTransPath))
                {
                    var outTransDir = Path.Combine(outputPath, "Sifu", "Content", "DB", "Movement", "Transition");
                    Directory.CreateDirectory(outTransDir);
                    var outTransAsset = Path.Combine(outTransDir, "BP_TransitionAnimRequest.uasset");

                    var eng = EngineVersion.VER_UE4_26;
                    await System.Threading.Tasks.Task.Run(() =>
                        PatchStanceAsset(vanillaTransPath, charTransPath, outTransAsset, eng, _referenceModDir, _activeStance ?? "", "BP_TransitionAnimRequest"));

                    fileEntries.Add((outTransAsset,
                        "../../../Sifu/Content/DB/Movement/Transition/BP_TransitionAnimRequest.uasset"));
                    fileEntries.Add((Path.ChangeExtension(outTransAsset, ".uexp"),
                        "../../../Sifu/Content/DB/Movement/Transition/BP_TransitionAnimRequest.uexp"));

                    ErrorLog.Write("EXPORT", new Exception($"Patched BP_TransitionAnimRequest for stance '{_activeStance}'"));
                }
                else
                {
                    ErrorLog.Write("EXPORT", new Exception(
                        $"Transition patch skipped: vanilla={File.Exists(vanillaTransPath)}, char={File.Exists(charTransPath)}"));
                }
            }

            if (!string.IsNullOrEmpty(_charBaseMovementDBPath))
            {
                txtCurrentAction.Text = "Patching BaseMovementDB...";
                var vanillaDbPath = Path.Combine(gameRoot, "DB/Movement/BaseMovementDB.uasset");
                var charDbPath = Path.Combine(gameRoot, _charBaseMovementDBPath + ".uasset");

                if (File.Exists(vanillaDbPath) && File.Exists(charDbPath))
                {
                    var outDbDir = Path.Combine(outputPath, "Sifu", "Content", "DB", "Movement");
                    Directory.CreateDirectory(outDbDir);
                    var outDbAsset = Path.Combine(outDbDir, "BaseMovementDB.uasset");

                    var eng = EngineVersion.VER_UE4_26;
                    await System.Threading.Tasks.Task.Run(() =>
                        PatchStanceAsset(vanillaDbPath, charDbPath, outDbAsset, eng, _referenceModDir, _activeStance ?? "", "BaseMovementDB"));

                    fileEntries.Add((outDbAsset,
                        "../../../Sifu/Content/DB/Movement/BaseMovementDB.uasset"));
                    fileEntries.Add((Path.ChangeExtension(outDbAsset, ".uexp"),
                        "../../../Sifu/Content/DB/Movement/BaseMovementDB.uexp"));

                    ErrorLog.Write("EXPORT", new Exception($"Patched BaseMovementDB for stance '{_activeStance}'"));
                }
                else
                {
                    ErrorLog.Write("EXPORT", new Exception(
                        $"BaseMovementDB patch skipped: vanilla={File.Exists(vanillaDbPath)}, char={File.Exists(charDbPath)}"));
                }
            }
        }
    }

    private static string NormalizeSlotKey(string path)
    {
        path = path.Replace("\\", "/").TrimStart('/');
        if (!path.StartsWith("/"))
            path = "/" + path;
        var lastSlash = path.LastIndexOf('/');
        var lastDot = path.LastIndexOf('.');
        if (lastDot < lastSlash)
            path = path + "." + path.Substring(lastSlash + 1);
        return path;
    }

    private static string GamePathToContentRel(string gamePath)
    {
        var rel = gamePath.Replace("\\", "/").TrimStart('/');
        if (rel.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
            rel = rel.Substring(5);
        return rel;
    }

    private static int ExtractPhaseFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        if (name.Contains("phase3", StringComparison.OrdinalIgnoreCase) || HasPhaseToken(name, "P3")) return 3;
        if (name.Contains("phase2", StringComparison.OrdinalIgnoreCase) || HasPhaseToken(name, "P2")) return 2;
        if (name.Contains("phase1", StringComparison.OrdinalIgnoreCase) || HasPhaseToken(name, "P1")) return 1;
        return 0;
    }

    private static bool HasPhaseToken(string name, string token)
    {
        int i = 0;
        while (i <= name.Length - token.Length)
        {
            int found = name.IndexOf(token, i, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            bool startOk = found == 0 || !char.IsLetterOrDigit(name[found - 1]);
            int after = found + token.Length;
            bool endOk = after >= name.Length || !char.IsLetterOrDigit(name[after]);
            if (startOk && endOk) return true;
            i = found + 1;
        }
        return false;
    }

    private static bool NeedsComboRedirect(ComboNode node)
    {
        if (string.IsNullOrEmpty(node.DefaultDBPath) || string.IsNullOrEmpty(node.SourceDBPath))
            return false;
        return !string.Equals(
            Path.GetFileNameWithoutExtension(node.DefaultDBPath),
            Path.GetFileNameWithoutExtension(node.SourceDBPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureLeadingSlash(string path)
    {
        path = path.Replace("\\", "/").TrimStart('/');
        if (!path.StartsWith("/"))
            path = "/" + path;
        var lastDot = path.LastIndexOf('.');
        var lastSlash = path.LastIndexOf('/');
        if (lastDot > lastSlash)
            path = path[..lastDot];
        return path;
    }

    private static string GetKeyString(PropertyData key)
    {
        if (key is StrPropertyData sp) return sp.Value?.ToString() ?? "";
        if (key is NamePropertyData np) return np.Value?.Value?.ToString() ?? "";
        return key.ToString() ?? "";
    }

    private static int FindImport(UAsset asset, string attackName, string attackPath)
    {
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var imp = asset.Imports[i];
            string objName = imp.ObjectName?.Value?.ToString() ?? "";
            if (objName == attackName)
            {
                int outerRaw = imp.OuterIndex.Index;
                if (outerRaw < 0)
                {
                    int outerImpIdx = -(outerRaw + 1);
                    if (outerImpIdx < asset.Imports.Count)
                    {
                        string pkgName = asset.Imports[outerImpIdx].ObjectName?.Value?.ToString() ?? "";
                        if (pkgName == attackPath) return i;
                    }
                }
            }
        }
        return -1;
    }

    private static int AddAttackDBImport(UAsset asset, string attackName, string attackPath)
    {
        if (string.IsNullOrEmpty(attackName))
            attackName = Path.GetFileNameWithoutExtension(attackPath);
        if (string.IsNullOrEmpty(attackName))
            attackName = "UnknownAttack";

        var pkgImport = new UAssetAPI.Import();
        pkgImport.ClassPackage = FName.FromString(asset, "/Script/CoreUObject");
        pkgImport.ClassName = FName.FromString(asset, "Package");
        pkgImport.ObjectName = FName.FromString(asset, attackPath);
        pkgImport.OuterIndex = new FPackageIndex(0);
        pkgImport.PackageName = FName.FromString(asset, "None");
        int pkgIdx = asset.Imports.Count;
        asset.Imports.Add(pkgImport);

        var atkImport = new UAssetAPI.Import();
        atkImport.ClassPackage = FName.FromString(asset, "/Script/Sifu");
        atkImport.ClassName = FName.FromString(asset, "AttackDB");
        atkImport.ObjectName = FName.FromString(asset, attackName);
        atkImport.OuterIndex = FPackageIndex.FromImport(pkgIdx);
        atkImport.PackageName = FName.FromString(asset, "None");
        int atkIdx = asset.Imports.Count;
        asset.Imports.Add(atkImport);

        return atkIdx;
    }

    private static List<MapPropertyData> FindAllMapsNamed(List<PropertyData> props, string mapName)
    {
        var results = new List<MapPropertyData>();
        foreach (var p in props)
        {
            if (p is MapPropertyData mp && p.Name.Value?.ToString() == mapName)
                results.Add(mp);
            RecurseFindMaps(p, mapName, results);
        }
        return results;
    }

    private static void RecurseFindMaps(PropertyData p, string mapName, List<MapPropertyData> results)
    {
        if (p is StructPropertyData sp && sp.Value != null)
        {
            foreach (var child in sp.Value)
            {
                if (child is MapPropertyData mp2 && child.Name.Value?.ToString() == mapName)
                    results.Add(mp2);
                RecurseFindMaps(child, mapName, results);
            }
        }
        else if (p is ArrayPropertyData ap && ap.Value != null)
        {
            foreach (var child in ap.Value)
            {
                if (child is MapPropertyData mp3 && child.Name.Value?.ToString() == mapName)
                    results.Add(mp3);
                RecurseFindMaps(child, mapName, results);
            }
        }
    }

    private static void PatchStanceAsset(string vanillaPath, string charSpecificPath, string outputPath, EngineVersion eng, string? referenceDir, string stanceName, string fileName)
    {
        var vanillaSize = new FileInfo(vanillaPath).Length;
        ErrorLog.Write("EXPORT", new Exception($"[STANCE] Vanilla: {vanillaSize}B"));

        if (fileName == "BP_TransitionAnimRequest" && StanceGenerator.HasStance(stanceName))
        {
            try
            {
                string templatePath = Path.Combine(referenceDir ?? "", "StanceTemplateBase", fileName + ".uasset");
                if (!File.Exists(templatePath))
                    templatePath = vanillaPath;
                StanceGenerator.GenerateTransitionAnimRequest(templatePath, outputPath, stanceName, eng);
                var genUexp = Path.ChangeExtension(outputPath, ".uexp");
                if (!File.Exists(genUexp))
                {
                    var srcUexp = Path.Combine(referenceDir ?? "", "StanceTemplateBase", fileName + ".uexp");
                    if (File.Exists(srcUexp))
                        File.Copy(srcUexp, genUexp, overwrite: true);
                }
                return;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("EXPORT", new Exception($"[STANCE] StanceGenerator failed for '{stanceName}' transition: {ex.Message}"));
            }
        }

        if (fileName == "BaseMovementDB" && StanceGenerator.HasStance(stanceName))
        {
            try
            {
                string templatePath = Path.Combine(referenceDir ?? "", "StanceTemplateBase", fileName + ".uasset");
                if (!File.Exists(templatePath))
                    templatePath = vanillaPath;
                StanceGenerator.GenerateBaseMovementDB(templatePath, outputPath, stanceName, eng);
                var genUexp = Path.ChangeExtension(outputPath, ".uexp");
                if (!File.Exists(genUexp))
                {
                    var srcUexp = Path.Combine(referenceDir ?? "", "StanceTemplateBase", fileName + ".uexp");
                    if (File.Exists(srcUexp))
                        File.Copy(srcUexp, genUexp, overwrite: true);
                }
                return;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("EXPORT", new Exception($"[STANCE] StanceGenerator failed for '{stanceName}' BaseMovementDB: {ex.Message}"));
            }
        }

        if (!string.IsNullOrEmpty(referenceDir))
        {
            var refDir = Path.Combine(referenceDir, stanceName);
            var refFile = Path.Combine(refDir, fileName + ".uasset");
            if (File.Exists(refFile))
            {
                File.Copy(refFile, outputPath, overwrite: true);
                var refUexp = Path.Combine(refDir, fileName + ".uexp");
                if (File.Exists(refUexp))
                    File.Copy(refUexp, Path.ChangeExtension(outputPath, ".uexp"), overwrite: true);
                ErrorLog.Write("EXPORT", new Exception($"[STANCE] Used pre-made reference: {refFile} ({new FileInfo(refFile).Length}B)"));
                return;
            }
        }

        ErrorLog.Write("EXPORT", new Exception($"[STANCE] No reference for '{stanceName}', copying vanilla as-is"));
        File.Copy(vanillaPath, outputPath, overwrite: true);
        var vanillaUexp = Path.ChangeExtension(vanillaPath, ".uexp");
        if (File.Exists(vanillaUexp))
            File.Copy(vanillaUexp, Path.ChangeExtension(outputPath, ".uexp"), overwrite: true);
    }

    private void PatchUnitProperties(UnitProperties props, string variantTag, List<(string src, string dest)> fileEntries, string gameRoot, string outputPath)
    {
        try
        {
            var eng = EngineVersion.VER_UE4_26;

            var archRel = UnitPropertiesManager.ResolveArchetypePath(variantTag);
            if (archRel != null)
            {
                var vanillaPath = Path.Combine(gameRoot, archRel + ".uasset");
                if (File.Exists(vanillaPath))
                {
                    var outDir = Path.Combine(outputPath, "Sifu", "Content", Path.GetDirectoryName(archRel)!);
                    Directory.CreateDirectory(outDir);
                    var outPath = Path.Combine(outDir, Path.GetFileName(archRel) + ".uasset");
                    File.Copy(vanillaPath, outPath, true);

                    var asset = new UAsset(vanillaPath, eng, null, CustomSerializationFlags.None);
                    if (asset.Exports.Count > 1 && asset.Exports[1] is UAssetAPI.ExportTypes.NormalExport ne)
                    {
                        if (props.Health.HasValue)
                        {
                            var hp = ne.Data.OfType<UAssetAPI.PropertyTypes.Objects.FloatPropertyData>()
                                .FirstOrDefault(p => p.Name.Value.ToString() == "m_fHealth");
                            if (hp != null) hp.Value = props.Health.Value;
                        }
                        if (props.Structure.HasValue)
                        {
                            var sp = ne.Data.OfType<UAssetAPI.PropertyTypes.Objects.FloatPropertyData>()
                                .FirstOrDefault(p => p.Name.Value.ToString() == "m_fStructure");
                            if (sp != null) sp.Value = props.Structure.Value;
                        }
                        asset.Write(outPath);

                        var relFromContent = archRel;
                        fileEntries.Add((outPath, $"../../../Sifu/Content/{relFromContent}.uasset"));
                        var uexpOut = Path.ChangeExtension(outPath, ".uexp");
                        if (File.Exists(uexpOut))
                            fileEntries.Add((uexpOut, $"../../../Sifu/Content/{relFromContent}.uexp"));
                    }
                }
            }

            var defRel = UnitPropertiesManager.ResolveContextDefensePath(variantTag);
            if (defRel != null)
            {
                var vanillaDefPath = Path.Combine(gameRoot, defRel + ".uasset");
                if (File.Exists(vanillaDefPath))
                {
                    var outDir = Path.Combine(outputPath, "Sifu", "Content", Path.GetDirectoryName(defRel)!);
                    Directory.CreateDirectory(outDir);
                    var outPath = Path.Combine(outDir, Path.GetFileName(defRel) + ".uasset");
                    File.Copy(vanillaDefPath, outPath, true);

                    var defAsset = new UAsset(vanillaDefPath, eng, null, CustomSerializationFlags.None);
                    bool modified = false;
                    foreach (var exp in defAsset.Exports)
                    {
                        if (exp is not UAssetAPI.ExportTypes.NormalExport ne2) continue;
                        foreach (var d in ne2.Data)
                        {
                            if (d is UAssetAPI.PropertyTypes.Objects.FloatPropertyData fp)
                            {
                                var n = fp.Name.Value.ToString();
                                if (n == "m_fMemoryLimit" && props.MemoryLimit.HasValue) { fp.Value = props.MemoryLimit.Value; modified = true; }
                                else if (n == "m_fMemoryFlushLimit" && props.MemoryFlushLimit.HasValue) { fp.Value = props.MemoryFlushLimit.Value; modified = true; }
                            }
                            else if (d is UAssetAPI.PropertyTypes.Objects.BytePropertyData bp && bp.Name.Value.ToString() == "m_uiHitsCount" && props.HitsCount.HasValue)
                            {
                                bp.Value = (byte)props.HitsCount.Value;
                                modified = true;
                            }
                        }
                    }
                    if (modified)
                    {
                        defAsset.Write(outPath);

                        var relFromContent = defRel;
                        fileEntries.Add((outPath, $"../../../Sifu/Content/{relFromContent}.uasset"));
                        var uexpOut = Path.ChangeExtension(outPath, ".uexp");
                        if (File.Exists(uexpOut))
                            fileEntries.Add((uexpOut, $"../../../Sifu/Content/{relFromContent}.uexp"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", new Exception($"[UNIT_PROPS] Error patching {variantTag}: {ex.Message}"));
        }
    }

    private static bool PatchDbAnimation(string vanillaDbPath, string newAnimPath, string outputDbPath, EngineVersion eng)
    {
        try
        {
            var asset = new UAsset(vanillaDbPath, eng, null, CustomSerializationFlags.None);

            NormalExport? dbExport = null;
            foreach (var exp in asset.Exports)
            {
                if (exp is NormalExport ne)
                {
                    dbExport = ne;
                    break;
                }
            }
            if (dbExport == null) return false;

            var mAttack = dbExport.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Attack");
            if (mAttack is not StructPropertyData attackStruct || attackStruct.Value == null) return false;

            var mAnim = attackStruct.Value.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Animation");
            if (mAnim is not ObjectPropertyData animProp || animProp.Value == null) return false;

            if (animProp.Value.Index >= 0) return false;

            int importIndex = -(animProp.Value.Index + 1);
            if (importIndex < 0 || importIndex >= asset.Imports.Count) return false;

            string animName = newAnimPath.Contains('/') ? newAnimPath.Substring(newAnimPath.LastIndexOf('/') + 1) : newAnimPath;
            string pkgPath = "/" + newAnimPath;

            var currentImport = asset.Imports[importIndex];
            currentImport.ObjectName = FName.FromString(asset, animName);

            int outerIdx = -(currentImport.OuterIndex.Index + 1);
            if (outerIdx >= 0 && outerIdx < asset.Imports.Count)
            {
                asset.Imports[outerIdx].ObjectName = FName.FromString(asset, pkgPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputDbPath)!);
            asset.Write(outputDbPath);
            ErrorLog.Write("EXPORT", new Exception($"[DB PATCH] Modified {Path.GetFileName(vanillaDbPath)}: anim -> {animName} ({new FileInfo(outputDbPath).Length}B)"));
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", new Exception($"[DB PATCH] Failed to patch {Path.GetFileName(vanillaDbPath)}: {ex.Message}"));
            return false;
        }
    }

    private static bool PatchDataTableAnim(string vanillaDtPath, string attackName, string newAnimPath, string outputDtPath, EngineVersion eng, Dictionary<string, (float hitFrame, int buildupFrame)>? animToTiming = null)
    {
        try
        {
            var asset = new UAsset(vanillaDtPath, eng, null, CustomSerializationFlags.None);

            DataTableExport? dtExport = null;
            foreach (var exp in asset.Exports)
            {
                if (exp is DataTableExport dte) { dtExport = dte; break; }
            }
            if (dtExport == null) return false;

            string animPath = newAnimPath.Contains('/') ? newAnimPath : "/" + newAnimPath;

            // Try Item indexer first (by FName key)
            bool patched = false;
            try
            {
                var row = dtExport[FName.FromString(asset, attackName)];
                if (row != null)
                {
                    patched = PatchRowAnim(row, animPath, asset);
                    if (patched && animToTiming != null) PatchRowTiming(row, animPath, animToTiming, asset);
                }
            }
            catch { }

            // Fallback: scan Data list for RowMap array
            if (!patched)
            {
                try
                {
                    var dataProps = dtExport.Data;
                    if (dataProps != null)
                    {
                        foreach (var prop in dataProps)
                        {
                            if (prop is ArrayPropertyData arr && arr.Name?.Value?.ToString() == "RowMap")
                            {
                                foreach (var elem in arr.Value)
                                {
                                    if (elem is StructPropertyData rowStruct)
                                    {
                                        var rowName = rowStruct.Name?.Value?.ToString();
                                        if (rowName == attackName)
                                        {
                                            patched = PatchRowAnim(rowStruct, animPath, asset);
                                            if (patched && animToTiming != null) PatchRowTiming(rowStruct, animPath, animToTiming, asset);
                                            if (patched) break;
                                        }
                                    }
                                }
                                if (patched) break;
                            }
                        }
                    }
                }
                catch { }
            }

            if (!patched) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(outputDtPath)!);
            asset.Write(outputDtPath);
            ErrorLog.Write("EXPORT", new Exception($"[DT PATCH] Modified {Path.GetFileName(vanillaDtPath)}: {attackName} -> {Path.GetFileName(animPath)} ({new FileInfo(outputDtPath).Length}B)"));
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", new Exception($"[DT PATCH] Failed to patch {Path.GetFileName(vanillaDtPath)}: {ex.Message}"));
            return false;
        }
    }

    private static bool PatchRowAnim(PropertyData rowProp, string animPath, UAsset asset)
    {
        if (rowProp is not StructPropertyData rowStruct || rowStruct.Value == null) return false;

        foreach (var prop in rowStruct.Value)
        {
            if (prop.Name?.Value?.ToString() == "m_Anim" && prop is SoftObjectPropertyData sop)
            {
                // For UE4.26 (pre-5.1), FTopLevelAssetPath.AssetName stores the full path
                var newPath = new FSoftObjectPath
                {
                    AssetPath = new FTopLevelAssetPath { PackageName = default, AssetName = FName.FromString(asset, animPath) },
                    SubPathString = sop.Value.SubPathString
                };
                sop.Value = newPath;
                return true;
            }
        }
        return false;
    }

    private static bool PatchRowTiming(PropertyData rowProp, string sourceAnimPath,
        Dictionary<string, (float hitFrame, int buildupFrame)> animToTiming, UAsset asset)
    {
        if (rowProp is not StructPropertyData rowStruct || rowStruct.Value == null) return false;
        if (!animToTiming.TryGetValue(sourceAnimPath, out var timing)) return false;

        bool patched = false;
        foreach (var prop in rowStruct.Value)
        {
            if (prop.Name?.Value?.ToString() == "m_fHitFrame" && prop is FloatPropertyData fp)
            {
                fp.Value = timing.hitFrame;
                patched = true;
            }
            else if (prop.Name?.Value?.ToString() == "m_iLastBuildupFrame" && prop is IntPropertyData ip)
            {
                ip.Value = timing.buildupFrame;
                patched = true;
            }
        }
        return patched;
    }

    private static void BinaryPatchDataTableTiming(
        string vanillaDtPath, string outDtPath,
        string rowName, float newHitFrame, int newBuildupFrame,
        Dictionary<string, (float hitFrame, int buildupFrame)> animToTiming, string animPath)
    {
        try
        {
            if (!animToTiming.TryGetValue(animPath, out var timing)) return;
            newHitFrame = timing.hitFrame;
            newBuildupFrame = timing.buildupFrame;

            var vanillaUexp = Path.ChangeExtension(vanillaDtPath, ".uexp");
            var outUexp = Path.ChangeExtension(outDtPath, ".uexp");
            if (!File.Exists(vanillaUexp)) return;

            var nameTable = ReadNameTable(vanillaDtPath);
            if (nameTable.Count == 0)
            {
                ErrorLog.Write("EXPORT", new Exception($"  BIN-DT SKIP: empty name table for {Path.GetFileName(vanillaDtPath)}"));
                return;
            }

            int rowNameIdx = -1, hitFrameIdx = -1, buildupIdx = -1;
            int floatTypeIdx = -1, intTypeIdx = -1;
            for (int i = 0; i < nameTable.Count; i++)
            {
                if (nameTable[i] == rowName) rowNameIdx = i;
                if (nameTable[i] == "m_fHitFrame") hitFrameIdx = i;
                if (nameTable[i] == "m_iLastBuildupFrame") buildupIdx = i;
                if (nameTable[i] == "FloatProperty") floatTypeIdx = i;
                if (nameTable[i] == "IntProperty") intTypeIdx = i;
            }

            if (rowNameIdx < 0 || hitFrameIdx < 0 || buildupIdx < 0 || floatTypeIdx < 0 || intTypeIdx < 0)
            {
                ErrorLog.Write("EXPORT", new Exception($"  BIN-DT SKIP: name indices not found (row={rowNameIdx} hit={hitFrameIdx} build={buildupIdx})"));
                return;
            }

            var bytes = File.ReadAllBytes(vanillaUexp);
            int patched = 0;

            byte[] rowKey = BitConverter.GetBytes(rowNameIdx);

            int rowStart = FindBytes(bytes, rowKey, 0);
            if (rowStart < 0)
            {
                ErrorLog.Write("EXPORT", new Exception($"  BIN-DT SKIP: row '{rowName}' FName[{rowNameIdx}] not found in .uexp"));
                return;
            }

            int searchEnd = Math.Min(bytes.Length, rowStart + 2000);

            int hitFrameTag = FindPropertyTag(bytes, hitFrameIdx, floatTypeIdx, rowStart, searchEnd);
            if (hitFrameTag >= 0)
            {
                int valueOffset = hitFrameTag + 25;
                if (valueOffset + 4 <= bytes.Length)
                {
                    BitConverter.GetBytes(newHitFrame).CopyTo(bytes, valueOffset);
                    patched++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-DT: {rowName} m_fHitFrame = {newHitFrame} (offset {valueOffset})"));
                }
            }

            int buildupTag = FindPropertyTag(bytes, buildupIdx, intTypeIdx, rowStart, searchEnd);
            if (buildupTag >= 0)
            {
                int valueOffset = buildupTag + 25;
                if (valueOffset + 4 <= bytes.Length)
                {
                    BitConverter.GetBytes(newBuildupFrame).CopyTo(bytes, valueOffset);
                    patched++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-DT: {rowName} m_iLastBuildupFrame = {newBuildupFrame} (offset {valueOffset})"));
                }
            }

            if (patched > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outUexp)!);
                File.WriteAllBytes(outUexp, bytes);
                ErrorLog.Write("EXPORT", new Exception($"  BIN-DT PATCHED {patched}/2 timing values for '{rowName}' in {Path.GetFileName(outUexp)}"));
            }
            else
            {
                ErrorLog.Write("EXPORT", new Exception($"  BIN-DT: no timing tags found for '{rowName}' (rowStart={rowNameIdx}, search {rowStart}-{searchEnd})"));
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", new Exception($"  BIN-DT FAIL: {rowName}: {ex.Message}"));
        }
    }

    private static bool BinaryPatchComboImports(
        UAsset vanillaAsset, string vanillaUassetPath, string outUassetPath,
        List<(string oldShortName, string newShortName, string newFullPath)> swaps)
    {
        try
        {
            var bytes = File.ReadAllBytes(vanillaUassetPath);

            int GetField(string name)
            {
                var f = typeof(UAsset).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return f != null ? (int)f.GetValue(vanillaAsset)! : 0;
            }

            int nameOffset  = GetField("NameOffset");
            int nameCount   = GetField("NameCount");
            int importOffset = GetField("ImportOffset");
            int importCount  = GetField("ImportCount");
            int exportOffset = GetField("ExportOffset");
            int dependsOffset = GetField("DependsOffset");
            int preloadDepOffset = GetField("PreloadDependencyOffset");
            int sectionSixOffset = GetField("SectionSixOffset");
            int assetRegOffset = GetField("AssetRegistryDataOffset");

            ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: header offsets: NameOff={nameOffset} NameCnt={nameCount} ImpOff={importOffset} ImpCnt={importCount} ExpOff={exportOffset} DepOff={dependsOffset} PreDepOff={preloadDepOffset} Sec6Off={sectionSixOffset}"));

            var nameMap = vanillaAsset.GetNameMapIndexList();

            var names = new List<string>();
            var nameToIdx = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < nameMap.Count; i++)
            {
                string name = nameMap[i].ToString() ?? "";
                names.Add(name);
                nameToIdx[name] = i;
            }

            var nameEntrySizes = new List<int>();
            int pos = nameOffset;
            for (int i = 0; i < nameCount && pos + 4 < bytes.Length; i++)
            {
                int entryStart = pos;
                int strLen = BitConverter.ToInt32(bytes, pos);
                pos += 4;
                if (strLen < 0) pos += -strLen * 2;
                else pos += strLen;
                pos += 4;
                nameEntrySizes.Add(pos - entryStart);
            }
            int oldNameTableEnd = pos;

            const int IMPORT_ENTRY_SIZE = 28;

            var swapsToMake = new List<(int importIndex, int outerImportIndex, string newShortName, string newFullPath)>();
            for (int i = 0; i < importCount; i++)
            {
                int impOff = importOffset + i * IMPORT_ENTRY_SIZE;
                int objNameIdxOff = impOff + 20;
                if (objNameIdxOff + 4 > bytes.Length) continue;
                int objNameIdx = BitConverter.ToInt32(bytes, objNameIdxOff);
                if (objNameIdx < 0 || objNameIdx >= names.Count) continue;
                string objName = names[objNameIdx];
                foreach (var (oldShort, newShort, newFull) in swaps)
                {
                    if (objName == oldShort)
                    {
                        int rawOuterIdx = BitConverter.ToInt32(bytes, impOff + 16);
                        int outerIdx = rawOuterIdx < 0 ? (-rawOuterIdx - 1) : rawOuterIdx;
                        swapsToMake.Add((i, outerIdx, newShort, newFull));
                        break;
                    }
                }
            }

            if (swapsToMake.Count == 0)
            {
                ErrorLog.Write("EXPORT", new Exception("  BIN-IMP: no matching imports to swap"));
                return false;
            }

            var namesToAdd = new List<string>();
            foreach (var (_, _, newShort, newFull) in swapsToMake)
            {
                if (!nameToIdx.ContainsKey(newShort)) namesToAdd.Add(newShort);
                if (!nameToIdx.ContainsKey(newFull)) namesToAdd.Add(newFull);
            }

            using var newNameTableMs = new MemoryStream();
            pos = nameOffset;
            for (int i = 0; i < nameCount; i++)
            {
                newNameTableMs.Write(bytes, pos, nameEntrySizes[i]);
                pos += nameEntrySizes[i];
            }
            foreach (var newName in namesToAdd)
            {
                nameToIdx[newName] = names.Count;
                names.Add(newName);
                int strLen = newName.Length + 1;
                byte[] strBytes = System.Text.Encoding.ASCII.GetBytes(newName + "\0");
                newNameTableMs.Write(BitConverter.GetBytes(strLen), 0, 4);
                newNameTableMs.Write(strBytes, 0, strBytes.Length);
                newNameTableMs.Write(new byte[4], 0, 4);
            }
            byte[] newNameTableBytes = newNameTableMs.ToArray();
            int nameTableDelta = newNameTableBytes.Length - (oldNameTableEnd - nameOffset);

            int oldImportTableEnd = importOffset + importCount * IMPORT_ENTRY_SIZE;

            var swapsDict = new Dictionary<int, string>();
            var outerSwapsDict = new Dictionary<int, string>();
            foreach (var (impIdx, outerIdx, newShort, newFull) in swapsToMake)
            {
                swapsDict[impIdx] = newShort;
                if (outerIdx >= 0 && outerIdx < importCount)
                    outerSwapsDict[outerIdx] = newFull;
            }

            using var newImportMs = new MemoryStream();
            for (int i = 0; i < importCount; i++)
            {
                int impOff = importOffset + i * IMPORT_ENTRY_SIZE;
                newImportMs.Write(bytes, impOff, 20);
                if (swapsDict.TryGetValue(i, out var newShort))
                {
                    newImportMs.Write(BitConverter.GetBytes(nameToIdx[newShort]), 0, 4);
                    newImportMs.Write(BitConverter.GetBytes(0), 0, 4);
                }
                else if (outerSwapsDict.TryGetValue(i, out var newFull))
                {
                    newImportMs.Write(BitConverter.GetBytes(nameToIdx[newFull]), 0, 4);
                    newImportMs.Write(BitConverter.GetBytes(0), 0, 4);
                }
                else
                {
                    newImportMs.Write(bytes, impOff + 20, 8);
                }
            }
            byte[] newImportTableBytes = newImportMs.ToArray();

            using var outMs = new MemoryStream(bytes.Length + nameTableDelta);
            outMs.Write(bytes, 0, nameOffset);
            outMs.Write(newNameTableBytes, 0, newNameTableBytes.Length);
            if (oldNameTableEnd < importOffset)
                outMs.Write(bytes, oldNameTableEnd, importOffset - oldNameTableEnd);
            outMs.Write(newImportTableBytes, 0, newImportTableBytes.Length);
            if (oldImportTableEnd < bytes.Length)
                outMs.Write(bytes, oldImportTableEnd, bytes.Length - oldImportTableEnd);

            var resultBytes = outMs.ToArray();

            int patchedNameCount = 0, patchedImportOffset = 0, patchedExportOffset = 0;
            int patchedDependsOffset = 0, patchedPreloadDepOffset = 0, patchedSectionSix = 0;
            int patchedAssetReg = 0;

            for (int i = 8; i < nameOffset - 3; i++)
            {
                int val = BitConverter.ToInt32(resultBytes, i);
                if (val == nameCount)
                {
                    BitConverter.GetBytes(nameCount + namesToAdd.Count).CopyTo(resultBytes, i);
                    patchedNameCount++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched NameCount at 0x{i:X}"));
                }
                if (val == importOffset)
                {
                    BitConverter.GetBytes(importOffset + nameTableDelta).CopyTo(resultBytes, i);
                    patchedImportOffset++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched ImportOffset at 0x{i:X}"));
                }
                if (val == exportOffset && exportOffset != importOffset)
                {
                    BitConverter.GetBytes(exportOffset + nameTableDelta).CopyTo(resultBytes, i);
                    patchedExportOffset++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched ExportOffset at 0x{i:X}"));
                }
                if (val == dependsOffset && dependsOffset != 0 && dependsOffset != exportOffset)
                {
                    BitConverter.GetBytes(dependsOffset + nameTableDelta).CopyTo(resultBytes, i);
                    patchedDependsOffset++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched DependsOffset at 0x{i:X}"));
                }
                if (val == preloadDepOffset && preloadDepOffset != 0 && preloadDepOffset != dependsOffset)
                {
                    BitConverter.GetBytes(preloadDepOffset + nameTableDelta).CopyTo(resultBytes, i);
                    patchedPreloadDepOffset++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched PreloadDepOffset at 0x{i:X}"));
                }
                if (val == sectionSixOffset && sectionSixOffset != 0 && sectionSixOffset != preloadDepOffset)
                {
                    BitConverter.GetBytes(sectionSixOffset + nameTableDelta).CopyTo(resultBytes, i);
                    patchedSectionSix++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched SectionSixOffset at 0x{i:X}"));
                }
                if (val == assetRegOffset && assetRegOffset != 0)
                {
                    BitConverter.GetBytes(assetRegOffset + nameTableDelta).CopyTo(resultBytes, i);
                    patchedAssetReg++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched AssetRegistryDataOffset at 0x{i:X}"));
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outUassetPath)!);
            File.WriteAllBytes(outUassetPath, resultBytes);
            ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched {swapsToMake.Count} imports, added {namesToAdd.Count} names, {resultBytes.Length}B, delta={nameTableDelta}"));
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP FAIL: {ex.Message}\n{ex.StackTrace}"));
            return false;
        }
    }

    private static int FindBytes(byte[] haystack, byte[] needle, int startOffset)
    {
        for (int i = startOffset; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static int FindPropertyTag(byte[] bytes, int nameIdx, int typeIdx, int startOffset, int endOffset)
    {
        byte[] nameBytes = BitConverter.GetBytes(nameIdx);
        byte[] typeBytes = BitConverter.GetBytes(typeIdx);

        for (int i = startOffset; i <= endOffset - 17; i++)
        {
            if (bytes[i] == nameBytes[0] && bytes[i + 1] == nameBytes[1] &&
                bytes[i + 2] == nameBytes[2] && bytes[i + 3] == nameBytes[3] &&
                bytes[i + 8] == typeBytes[0] && bytes[i + 9] == typeBytes[1] &&
                bytes[i + 10] == typeBytes[2] && bytes[i + 11] == typeBytes[3])
            {
                return i;
            }
        }
        return -1;
    }

    private static List<string> ReadNameTable(string uassetPath)
    {
        var result = new List<string>();
        try
        {
            var bytes = File.ReadAllBytes(uassetPath);
            if (bytes.Length < 0x1C) return result;

            int legacyVersion = BitConverter.ToInt32(bytes, 0x04);

            int offset = 0x1C;
            if (legacyVersion < -2)
            {
                offset = BitConverter.ToInt32(bytes, 0x1C);
                if (offset < 0x1C || offset > bytes.Length) offset = 0x1C;
            }

            if (offset + 8 > bytes.Length) return result;
            int nameCount = BitConverter.ToInt32(bytes, offset);
            offset += 4;
            int nameOffset = BitConverter.ToInt32(bytes, offset);

            if (nameOffset < 0 || nameOffset >= bytes.Length) return result;
            int pos = nameOffset;

            for (int i = 0; i < nameCount && pos + 4 < bytes.Length; i++)
            {
                int strLen = BitConverter.ToInt32(bytes, pos);
                pos += 4;

                if (strLen < 0)
                {
                    strLen = -strLen * 2;
                    if (pos + strLen > bytes.Length) break;
                    var s = System.Text.Encoding.Unicode.GetString(bytes, pos, strLen - 2);
                    result.Add(s);
                    pos += strLen;
                }
                else
                {
                    if (pos + strLen > bytes.Length) break;
                    var s = System.Text.Encoding.ASCII.GetString(bytes, pos, strLen - 1);
                    result.Add(s);
                    pos += strLen;
                }

                pos += 4;
            }
        }
        catch { }
        return result;
    }

    private static void CopyDirectory(string srcDir, string destDir)
    {
        foreach (var srcFile in Directory.GetFiles(srcDir, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(srcFile);
            if (ext == ".bak") continue;
            if (ext != ".uasset" && ext != ".uexp") continue;

            string relPath = Path.GetRelativePath(srcDir, srcFile);
            string dest = Path.Combine(destDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(srcFile, dest, overwrite: true);
        }
    }

    private static void CopyDirectorySkipPatched(string srcDir, string destDir)
    {
        foreach (var srcFile in Directory.GetFiles(srcDir, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(srcFile);
            if (ext == ".bak") continue;
            if (ext != ".uasset" && ext != ".uexp") continue;

            string relPath = Path.GetRelativePath(srcDir, srcFile);
            string dest = Path.Combine(destDir, relPath);
            if (File.Exists(dest)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(srcFile, dest, overwrite: false);
        }
    }

    private void SetProgress(double percent)
    {
        var parentWidth = ((FrameworkElement)progressFill.Parent).RenderSize.Width;
        var targetWidth = (percent / 100.0) * parentWidth;
        var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(300));
        progressFill.BeginAnimation(WidthProperty, anim);
        txtProgress.Text = $"{(int)percent}%";
    }

    private void UpdateStep(int step, string state)
    {
        var stepControl = step switch
        {
            1 => step1,
            2 => step2,
            3 => step3,
            _ => step1
        };

        var (symbol, color) = state switch
        {
            "active" => ("⏳", "#89b4fa"),
            "done" => ("✓", "#a6e3a1"),
            _ => ("○", "#6c7086")
        };

        stepControl.Text = $"{symbol} {stepControl.Text[2..]}";
        stepControl.Foreground = MakeBrush(color);
    }

    private void ShowError(string message)
    {
        panelExporting.Visibility = Visibility.Collapsed;
        panelReview.Visibility = Visibility.Visible;
        txtStatus.Text = message;
        txtStatus.Foreground = MakeBrush("#f38ba8");
        btnConfirm.IsEnabled = true;
    }

    private void ShowComplete(string pakName, int changeCount, string installedTo)
    {
        panelExporting.Visibility = Visibility.Collapsed;
        panelComplete.Visibility = Visibility.Visible;

        var pakSize = File.Exists(_pakPath) ? new FileInfo(_pakPath).Length : 0;
        var sizeStr = pakSize > 1024 * 1024
            ? $"{pakSize / (1024.0 * 1024.0):F1} MB"
            : $"{pakSize / 1024.0:F1} KB";

        txtResult.Text = $"{pakName} ({changeCount} changes, {sizeStr})";

        if (!string.IsNullOrEmpty(installedTo))
        {
            txtInstallPath.Text = $"Installed to:\n{installedTo}";
        }
        else
        {
            txtInstallPath.Text = $"Saved to: {_outputDir}\nCopy to your Sifu/Content/Paks/~mods/ folder.";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_outputDir) && Directory.Exists(_outputDir))
        {
            Process.Start("explorer.exe", _outputDir);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
