using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
    // Set while an export runs whenever anything it stages or references resolves to
    // the CustomAssets folder; gates the bulk StageCustomAssetsFiles at the end.
    private static bool _exportUsesCustomAssets;
    // Display rels (uasset row + companions) the user unchecked on the Custom Files page.
    private readonly HashSet<string> _excludedCustomStems = new(StringComparer.OrdinalIgnoreCase);
    // Custom Files page rows (checkbox, display rel) - built when the page opens.
    private readonly List<(CheckBox box, string rowRel)> _customFileRows = new();
    private double _reviewWidth;

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

        // Several weapon keys share one variant, and therefore one set of ArchetypeDB/ContextDefense
        // files - report and patch the props once per variant instead of once per weapon.
        var propsVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in unitCaches)
        {
            string unitKey = kvp.Key;
            var entry = kvp.Value;
            var graph = entry.Graph;

            // A node is modified if its animation changed OR if only its attack DB changed
            // (an attack swap can keep the same animation). Transition/redirect nodes carry an
            // empty AnimPath and an empty DefaultDBPath, so they are never picked up here.
            var modified = graph.Nodes
                .Where(n => !n.IsRoot && !string.IsNullOrEmpty(n.AnimPath)
                    && (n.TreeIndex == -1 || n.AnimPath != n.DefaultAnimPath
                        || (!string.IsNullOrEmpty(n.VanillaAnimPath) && n.AnimPath != n.VanillaAnimPath)
                        || (!string.IsNullOrEmpty(n.DefaultDBPath) && !string.IsNullOrEmpty(n.SourceDBPath)
                            && !string.Equals(n.SourceDBPath, n.DefaultDBPath, StringComparison.OrdinalIgnoreCase))))
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
                && UnitPropertiesManager.HasChanges(contentPath, variant, entry.Props)
                && propsVariants.Add(variant!);

            // AttackDB field tuning does not touch AnimPath/DefaultAnimPath, so it would never
            // show up in `modified` - the unit still has to be exported for its cards to ship.
            bool hasAttackTuning = ProjectChangeSummary.GraphHasAttackDbTuning(graph);
            if (modified.Count > 0 || hasRetargets || hasUnitProps || hasAttackTuning)
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
                // Intentionally NOT removing _perUnitProps: props are captured per variant and have
                // nothing to do with which graph key PreferUnitKey keeps. Callers that walk
                // _perUnitProps pick up keys no longer present in _perUnitGraphs.
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
        // Tuning lives in shared card files - collected for the top-level AttackDBs category.
        var attackByUnit = new List<(string UnitKey, List<object> Rows)>();

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

                var attackRows = ProjectChangeSummary.BuildAttackDbRows(graph.Nodes, _contentPath);
                if (attackRows.Count > 0) attackByUnit.Add((unitKey, attackRows));

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

            // Graphs that DedupeSharedGraphs collapsed still own props under their own key.
            foreach (var propsKvp in _perUnitProps)
            {
                if (_perUnitGraphs.ContainsKey(propsKvp.Key)) continue;

                var propsParts = propsKvp.Key.Split('|');
                string propsVariant = propsParts.Length > 1 ? propsParts[1] : propsKvp.Key;
                var orphanRows = new List<object>();
                ProjectChangeSummary.AddUnitPropsEntries(orphanRows, propsKvp.Value, _contentPath, propsVariant);
                if (orphanRows.Count == 0) continue;

                var orphan = new UnitReview
                {
                    Key = propsKvp.Key,
                    DisplayName = ProjectChangeSummary.FormatUnitDisplayName(propsKvp.Key),
                    Subtitle = ""
                };
                orphan.Sections.Add(new UnitSectionReview { Name = "Unit Props", Rows = orphanRows });
                units.Add(orphan);
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
                var attackRows = ProjectChangeSummary.BuildAttackDbRows(_graph.Nodes, _contentPath);
                if (attackRows.Count > 0) attackByUnit.Add((unitKey, attackRows));

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

        var ordered = units
            .OrderBy(u => u.Key.StartsWith("MainChar", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var attackCategory = ProjectChangeSummary.BuildAttackDbCategory(attackByUnit);
        if (attackCategory != null) ordered.Add(attackCategory);
        return ordered;
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

        ShowCustomFilesButtonIfRelevant();
    }

    /// <summary>
    /// Shows the optional Custom Files button only when this export actually depends
    /// on the CustomAssets folder: a changed node or tuned card linked to a custom
    /// anim/card, or any same-path override file.
    /// </summary>
    private void ShowCustomFilesButtonIfRelevant()
    {
        try
        {
            if (!DetectCustomDependency()) return;
            var rows = GetCustomRowRels();
            if (rows.Count == 0) return;
            btnCustomFiles.Content = $"Custom Files ({rows.Count})";
            btnCustomFiles.Visibility = Visibility.Visible;
        }
        catch { }
    }

    private static List<string> GetCustomRowRels()
    {
        if (!Directory.Exists(CustomAssets.Root)) return new List<string>();
        return CustomAssets.DisplayNames(
            Directory.GetFiles(CustomAssets.Root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(CustomAssets.Root, f).Replace('\\', '/')));
    }

    private bool DetectCustomDependency()
    {
        bool Linked(ComboNode n) =>
            CustomRefExists(n.AnimPath) || CustomRefExists(n.DefaultDBPath)
            || CustomRefExists(n.SourceDBPath);
        bool TunedCustom(ComboNode n) =>
            ProjectChangeSummary.HasAttackDbTuning(n)
            && CustomRefExists(AttackDbCard.EffectiveCardPath(n));
        try
        {
            if (_unitCaches != null && _perUnitGraphs.Count > 0)
            {
                foreach (var kvp in _perUnitModifiedNodes)
                    foreach (var n in kvp.Value)
                        if (Linked(n)) return true;
                foreach (var kvp in _perUnitGraphs)
                {
                    if (kvp.Value.graph == null) continue;
                    foreach (var n in kvp.Value.graph.Nodes)
                        if (Linked(n) || TunedCustom(n)) return true;
                }
            }
            else
            {
                foreach (var n in _modifiedNodes)
                    if (Linked(n)) return true;
                if (_graph != null)
                    foreach (var n in _graph.Nodes)
                        if (Linked(n) || TunedCustom(n)) return true;
            }
            return CustomAssetsHasOverrides(Path.Combine(_contentPath, "Content"));
        }
        catch { return false; }
    }

    // ---- Custom Files page -----------------------------------------------------------

    private void CustomFiles_Click(object sender, RoutedEventArgs e)
    {
        BuildCustomFilesList();
        _reviewWidth = Width;
        panelReview.Visibility = Visibility.Collapsed;
        panelCustomFiles.Visibility = Visibility.Visible;
        Width = 900;
    }

    private void CustomFilesBack_Click(object sender, RoutedEventArgs e)
    {
        CollectExcludedCustomStems();
        panelCustomFiles.Visibility = Visibility.Collapsed;
        panelReview.Visibility = Visibility.Visible;
        Width = _reviewWidth;
    }

    private void SelectAllCustom_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (box, _) in _customFileRows) box.IsChecked = true;
    }

    private void SelectNoneCustom_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (box, _) in _customFileRows) box.IsChecked = false;
    }

    private void BuildCustomFilesList()
    {
        customFilesList.Children.Clear();
        _customFileRows.Clear();
        foreach (var rowRel in GetCustomRowRels())
        {
            var dir = Path.GetDirectoryName(rowRel.Replace('/', Path.DirectorySeparatorChar));
            var dirText = string.IsNullOrEmpty(dir) ? "" : dir.Replace('\\', '/');

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var nameTb = new TextBlock
            {
                Text = Path.GetFileName(rowRel),
                Foreground = MakeBrush("#cdd6f4"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var pathTb = new TextBlock
            {
                Text = dirText,
                Foreground = MakeBrush("#6c7086"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(nameTb, 0);
            Grid.SetColumn(pathTb, 1);
            g.Children.Add(nameTb);
            g.Children.Add(pathTb);

            var box = new CheckBox
            {
                Content = g,
                IsChecked = true,
                Foreground = MakeBrush("#cdd6f4"),
                Margin = new Thickness(0, 3, 0, 3),
            };
            customFilesList.Children.Add(box);
            _customFileRows.Add((box, rowRel));
        }
    }

    private void CollectExcludedCustomStems()
    {
        _excludedCustomStems.Clear();
        foreach (var (box, rowRel) in _customFileRows)
        {
            if (box.IsChecked == true) continue;
            // The stem covers the .uexp/.ubulk companions - consumers match AssetStemOfRel.
            _excludedCustomStems.Add(CustomAssets.AssetStemOfRel(rowRel));
        }
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

    // ErrorLog.Writes snapshot taken when an export starts - ShowComplete reports the
    // delta so swallowed per-step failures show up in the UI instead of only in error.log.
    private int _errBaseline;

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        CollectExcludedCustomStems();
        _errBaseline = ErrorLog.Writes;
        panelReview.Visibility = Visibility.Collapsed;
        panelExporting.Visibility = Visibility.Visible;

        // Declared outside try so the finally below can clean it up on every exit path.
        var mirrorRoot = Path.Combine(Path.GetTempPath(), "SifuPackSrc");

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
            _exportUsesCustomAssets = false;
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

                // Props are keyed by variant, not by graph, so walk them on their own - DedupeSharedGraphs
                // may have dropped a graph key that still owns props.
                foreach (var propsKvp in _perUnitProps)
                {
                    var propsParts = propsKvp.Key.Split('|');
                    string propsVariant = propsParts.Length > 1 ? propsParts[1] : propsKvp.Key;
                    if (UnitPropertiesManager.HasWritableChanges(_contentPath, propsVariant, propsKvp.Value))
                        PatchUnitProperties(propsKvp.Value, propsVariant, fileEntries, gameRoot, outputPath);
                }
            }
            else
            {
                _currentExportVariant = _activeVariant;
                await PatchComboAndStanceForCurrentState(fileEntries, gameRoot, outputPath);
                _currentExportVariant = null;

                if (_unitProps != null && !string.IsNullOrEmpty(_activeVariant)
                    && UnitPropertiesManager.HasWritableChanges(_contentPath, _activeVariant, _unitProps))
                {
                    PatchUnitProperties(_unitProps, _activeVariant, fileEntries, gameRoot, outputPath);
                }
            }

            // AttackDB tuning lives in the attack cards, not the combo tree, so it gets its own
            // pass: stage each tuned card (unless the combo pass already staged it) and patch the
            // two values into its payload in place. Runs after every unit so a card shared by two
            // units is written exactly once.
            PatchAttackDbFields(fileEntries, gameRoot, outputPath);

            // The flag is family-wide and its owner file is frequently another unit's own ArchetypeDB,
            // so every unit's "copy vanilla into staging" has to land first - patching focus per unit
            // let a later sibling overwrite the staged owner while the dedupe set stopped it being
            // re-applied, dropping the flag from the pak for BigGuy / FireDisciple / Sean.
            var focusProps = new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase);
            foreach (var propsKvp in _perUnitProps)
            {
                var propsParts = propsKvp.Key.Split('|');
                string focusVariant = propsParts.Length > 1 ? propsParts[1] : propsKvp.Key;
                if (!string.IsNullOrEmpty(focusVariant)) focusProps[focusVariant] = propsKvp.Value;
            }
            if (focusProps.Count == 0 && _unitProps != null && !string.IsNullOrEmpty(_activeVariant))
                focusProps[_activeVariant] = _unitProps;

            var focusStagedContent = Path.Combine(outputPath, "Sifu", "Content");
            var focusWritten = UnitPropertiesManager.ApplyFocusFamilies(
                gameRoot, focusStagedContent, focusProps,
                msg => ErrorLog.Write("EXPORT", new Exception(msg)));

            foreach (var focusRel in focusWritten)
            {
                var focusOut = Path.Combine(focusStagedContent,
                    focusRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
                if (fileEntries.Any(e => e.src == focusOut)) continue;
                fileEntries.Add((focusOut, $"../../../Sifu/Content/{focusRel}.uasset"));
                var focusUexp = Path.ChangeExtension(focusOut, ".uexp");
                if (File.Exists(focusUexp))
                    fileEntries.Add((focusUexp, $"../../../Sifu/Content/{focusRel}.uexp"));
            }

            // Ship the persistent CustomAssets library only when this export actually
            // depends on it: a staged source, anim/card reference or changed node that
            // resolved custom, or any same-path override file (always ships). A
            // vanilla-only export never drags the folder along. User-unchecked rows are
            // dropped even when an earlier pass already staged them.
            if (!_exportUsesCustomAssets) _exportUsesCustomAssets = CustomAssetsHasOverrides(gameRoot);
            if (_exportUsesCustomAssets)
            {
                StageCustomAssetsFiles(fileEntries, _excludedCustomStems);
                int uncheckedStaged = fileEntries.RemoveAll(e => IsExcludedCustomDest(e.dest));
                if (uncheckedStaged > 0)
                    ErrorLog.Write("EXPORT", new Exception(
                        $"[CUSTOM-ASSETS] removed {uncheckedStaged} user-unchecked staged file(s)"));
            }
            else
            {
                ErrorLog.Write("EXPORT", new Exception(
                    "[CUSTOM-ASSETS] skipped: no Custom Section dependency"));
            }

            // Deterministic dedup by destination: the first staged entry wins, so the
            // patch/focus passes (which run before the CustomAssets bulk at line ~883)
            // keep their tuned copies instead of leaving it to UnrealPak's collect order.
            var seenDests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int dupEntries = 0;
            var dedupedEntries = new List<(string src, string dest)>();
            foreach (var entry in fileEntries)
            {
                if (seenDests.Add(entry.dest)) dedupedEntries.Add(entry);
                else dupEntries++;
            }
            if (dupEntries > 0)
            {
                fileEntries = dedupedEntries;
                ErrorLog.Write("EXPORT", new Exception(
                    $"[DEDUP] dropped {dupEntries} duplicate destination(s); first entry wins"));
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

            // Sources go into the response file RELATIVE to UnrealPak's working directory,
            // but that does NOT escape the legacy MAX_PATH dead zone: the OS resolves the
            // relative path back to its canonical absolute form before UnrealPak opens it,
            // and a canonical path of exactly 260 characters fails to open (one warning,
            // exit 0, incomplete pak that crashes the game on load). Mirror every
            // exactly-260 source to a short temp path and point the filelist there instead.
            var pakWd = Setup.ContentExtractor.EnsureUnrealPakWorkingDirectory();
            var filelistPath = Path.Combine(Path.GetTempPath(), "SifuPakFilelist.txt");

            fileEntries = MirrorDeadZoneSources(fileEntries, mirrorRoot, out int mirroredCount);
            if (mirroredCount > 0)
                ErrorLog.Write("EXPORT", new Exception(
                    $"[MIRROR] redirected {mirroredCount} source(s) at exactly 260 chars to {mirrorRoot}"));

            var mismatches = FindBasenameMismatches(fileEntries);
            if (mismatches.Count > 0)
            {
                ErrorLog.Write("EXPORT", new Exception(
                    $"ABORT: {mismatches.Count} source/destination file name mismatch(es):"));
                foreach (var m in mismatches)
                    ErrorLog.Write("EXPORT", new Exception($"  {m}"));
                ShowError("Export blocked: source and destination file names differ; UnrealPak "
                    + "would archive the source under its own name and split the asset pair "
                    + "in game:\n\n" + string.Join("\n", mismatches));
                return;
            }

            var (filelistLines, deadZoneLines) = BuildFilelistLines(pakWd, fileEntries);
            if (deadZoneLines.Count > 0)
            {
                ErrorLog.Write("EXPORT", new Exception(
                    $"ABORT: {deadZoneLines.Count} source path(s) are exactly 260 characters (UnrealPak dead zone)"));
                ShowError("Export blocked: source path is exactly 260 characters (UnrealPak dead zone):\n\n"
                    + string.Join("\n", deadZoneLines));
                return;
            }
            var filelistContent = string.Join("\n", filelistLines);
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

            // UnrealPak exits 0 even when it skips files; a skipped file means the pak is
            // incomplete and the game crashes on load ("Serial size mismatch: Got 0").
            var skippedFiles = new List<string>();
            foreach (Match m in Regex.Matches(stdout + "\n" + stderr,
                "(?:Missing file|Unable to create file) \"([^\"]+)\""))
                if (!skippedFiles.Contains(m.Groups[1].Value)) skippedFiles.Add(m.Groups[1].Value);
            var addedMatch = Regex.Match(stdout, @"Added (\d+) files, \d+ bytes total");
            int uniqueDests = fileEntries.Select(e => e.dest).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int addedFiles = addedMatch.Success ? int.Parse(addedMatch.Groups[1].Value) : -1;
            if (skippedFiles.Count > 0 || (addedMatch.Success && addedFiles != uniqueDests))
            {
                ErrorLog.Write("EXPORT", new Exception(
                    $"ABORT: UnrealPak reported {skippedFiles.Count} skipped file(s); Added={addedFiles} expected={uniqueDests}"));
                try { if (File.Exists(_pakPath)) File.Delete(_pakPath); } catch { }
                var detail = skippedFiles.Count > 0
                    ? "UnrealPak skipped these files:\n" + string.Join("\n", skippedFiles)
                    : $"UnrealPak added {addedFiles} files but {uniqueDests} were staged.";
                ShowError("Export aborted - the pak would have been incomplete and crash the game.\n\n" + detail);
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
        finally
        {
            try { if (Directory.Exists(mirrorRoot)) Directory.Delete(mirrorRoot, true); } catch { }
            try
            {
                var fl = Path.Combine(Path.GetTempPath(), "SifuPakFilelist.txt");
                if (File.Exists(fl)) File.Delete(fl);
            }
            catch { }
        }
    }

    /// <summary>
    /// Swaps any source whose CANONICAL absolute path is exactly 260 characters (the
    /// legacy MAX_PATH dead zone - both UnrealPak and plain .NET reads fail on it) for a
    /// byte-identical copy under a short temp root, so the filelist's resolved path can
    /// never land on the dead length. Each copy goes into its own numbered SUBDIRECTORY
    /// and keeps its original file name: this UnrealPak build archives entries as
    /// destdir(dest) + basename(src), so a renamed copy would land in the pak under the
    /// wrong name and split uasset/uexp pairs (in-game crash). Reads the original
    /// through a \\?\ prefix, which bypasses MAX_PATH. Destinations are unchanged.
    /// Throws if a dead-zone source cannot be read, so the export aborts loudly
    /// instead of shipping a broken pak.
    /// </summary>
    internal static List<(string src, string dest)> MirrorDeadZoneSources(
        List<(string src, string dest)> fileEntries, string mirrorRoot, out int mirrored)
    {
        mirrored = 0;
        var result = new List<(string src, string dest)>(fileEntries.Count);
        foreach (var (src, dest) in fileEntries)
        {
            string canonical;
            try { canonical = Path.GetFullPath(src); }
            catch { canonical = src; }
            if (canonical.Length != 260)
            {
                result.Add((src, dest));
                continue;
            }

            var name = Path.GetFileName(canonical);
            if (string.IsNullOrEmpty(name)) name = "file";
            var entryDir = Path.Combine(mirrorRoot, $"{mirrored}");
            Directory.CreateDirectory(entryDir);
            var target = Path.Combine(entryDir, name);
            using (var read = File.OpenRead(@"\\?\" + canonical))
            using (var write = File.Create(target))
                read.CopyTo(write);
            mirrored++;
            result.Add((target, dest));
        }
        return result;
    }

    /// <summary>
    /// Backstop for the packer's archive-name rule (destdir(dest) + basename(src)):
    /// any entry whose source file name differs from its destination file name would
    /// be archived under the wrong name, leaving uasset/uexp half-pairs that crash the
    /// game on load. Returns one "src -> dest" line per offender; the caller aborts.
    /// </summary>
    internal static List<string> FindBasenameMismatches(
        List<(string src, string dest)> fileEntries)
    {
        var bad = new List<string>();
        foreach (var (src, dest) in fileEntries)
        {
            var s = Path.GetFileName(src);
            var d = Path.GetFileName(dest);
            if (!string.Equals(s, d, StringComparison.OrdinalIgnoreCase))
                bad.Add($"{s}  ->  {d}");
        }
        return bad;
    }

    /// <summary>
    /// Response-file lines with every source path expressed RELATIVE to UnrealPak's
    /// working directory (forward slashes). The dead-zone check measures the CANONICAL
    /// absolute path - the OS resolves relative sources before UnrealPak opens them, so
    /// only the resolved length matters. A canonical path of exactly 260 characters
    /// fails to open: UnrealPak logs one warning, exits 0 and ships an incomplete pak
    /// that crashes the game. The caller mirrors exactly-260 sources first; this is the
    /// backstop that refuses to pack any that slipped through. Returns the lines plus
    /// any source whose canonical path still measures exactly 260 characters.
    /// </summary>
    internal static (List<string> lines, List<string> deadZone) BuildFilelistLines(
        string pakWd, List<(string src, string dest)> fileEntries)
    {
        var lines = new List<string>(fileEntries.Count);
        var deadZone = new List<string>();
        foreach (var (src, dest) in fileEntries)
        {
            var rel = Path.GetRelativePath(pakWd, src);
            if (Path.IsPathRooted(rel)) rel = src;
            rel = rel.Replace('\\', '/');
            string canonical;
            try { canonical = Path.GetFullPath(Path.IsPathRooted(src) ? src : Path.Combine(pakWd, src)); }
            catch { canonical = src; }
            if (canonical.Length == 260) deadZone.Add(canonical);
            lines.Add($"\"{rel}\" \"{dest}\"");
        }
        return (lines, deadZone);
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
            var vanillaAssetPath = ResolveStageSource(gameRoot, mainComboRel)
                ?? Path.Combine(gameRoot, mainComboRel + ".uasset");
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
                int comboSwaps = 0;
                var mainSwaps = new List<(string oldShortName, string newShortName, string newFullPath, string oldFullPath)>();
                var swappedSlotShorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var redirectNodes = new List<ComboNode>();
                var patchedDbFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var delayPatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var mainAmbiguousFiles = BuildAmbiguousFilenames(allMaps);
                foreach (var node in _modifiedNodes)
                {
                    // A changed node whose anim or card lives only in CustomAssets makes
                    // this export depend on the folder - checked before any skip so
                    // deferred enemy nodes count too.
                    if (CustomRefExists(node.AnimPath) || CustomRefExists(node.DefaultDBPath)
                        || CustomRefExists(node.SourceDBPath))
                        _exportUsesCustomAssets = true;
                    if (!string.IsNullOrEmpty(node.DefaultDBPath) && node.DefaultDBPath.Contains("/AI/Archetypes/", StringComparison.OrdinalIgnoreCase) && _graph != null && !string.Equals(_graph.WeaponName, "MainChar", StringComparison.OrdinalIgnoreCase))
                    {
                        ErrorLog.Write("EXPORT", new Exception($"  ENEMY DEFER: {node.DisplayName} -> combo tree Import swap"));
                        continue;
                    }
                    if (string.IsNullOrEmpty(node.DefaultDBPath))
                    {
                        if (node.TreeIndex == -1 && !string.IsNullOrEmpty(node.AnimPath))
                        {
                            var templateRel = "DB/_MainChar/Combos/Attacks/BareHands/LightCombo/MainChar_Jab_FR";
                            var templateFile = ResolveStageSource(gameRoot, templateRel)
                                ?? Path.Combine(gameRoot, templateRel + ".uasset");
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
                        var vanillaDbFile = ResolveStageSource(gameRoot, relDbPath);
                        if (!patchedDbFiles.Contains(fallbackDbPath) && vanillaDbFile != null)
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
                        ErrorLog.Write("EXPORT", new Exception($"  SKIP(no anim->db): {node.DisplayName} AnimPath='{node.AnimPath}' DefaultDBPath='{node.DefaultDBPath}'"));
                        continue;
                    }

                    {
                        var inplaceDbPath = node.DefaultDBPath;
                        var inplaceRel = inplaceDbPath.TrimStart('/');
                        if (inplaceRel.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
                            inplaceRel = inplaceRel.Substring(5);
                        var inplaceVanilla = ResolveStageSource(gameRoot, inplaceRel);
                        bool inplaceOk = false;
                        if (!patchedDbFiles.Contains(inplaceDbPath) && inplaceVanilla != null)
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
                                inplaceOk = true;
                            }
                        }
                        if (inplaceOk)
                            continue;
                    }

                    var normalizedSlotKey = NormalizeSlotKey(node.DefaultDBPath);
                    var attackName = Path.GetFileNameWithoutExtension(newDbPath);
                    var attackPath = EnsureLeadingSlash(newDbPath);
                    var oldSlotShort = Path.GetFileNameWithoutExtension(node.DefaultDBPath);
                    var newSlotShort = Path.GetFileNameWithoutExtension(newDbPath);

                    if (string.IsNullOrEmpty(attackName) || string.IsNullOrEmpty(attackPath))
                    {
                        ErrorLog.Write("EXPORT", new Exception($"  SKIP(empty attack name/path): {node.DisplayName} AnimPath='{node.AnimPath}' animToDb='{newDbPath}'"));
                        continue;
                    }

                    bool found = false;
                    foreach (var attacksMap in allMaps)
                    {
                        foreach (var kvp in attacksMap.Value)
                        {
                            string keyStr = GetKeyString(kvp.Key);
                            if (!SlotKeysMatch(keyStr, normalizedSlotKey, mainAmbiguousFiles))
                            {
                                continue;
                            }

                            var valData = kvp.Value as ObjectPropertyData;
                            if (valData == null) continue;

                            int importIdx = FindImport(asset, attackName, attackPath);
                            if (importIdx < 0)
                                importIdx = AddAttackDBImport(asset, attackName, attackPath);

                            valData.Value = FPackageIndex.FromImport(importIdx);
                            patched++;
                            ErrorLog.Write("EXPORT", new Exception($"  PATCHED: {node.DisplayName} -> {attackName} (import[{importIdx}])"));
                            if (!string.Equals(oldSlotShort, newSlotShort, StringComparison.OrdinalIgnoreCase)
                                && swappedSlotShorts.Add(oldSlotShort))
                            {
                                comboSwaps++;
                                mainSwaps.Add((oldSlotShort, newSlotShort, attackPath, node.DefaultDBPath));
                                ErrorLog.Write("EXPORT", new Exception($"  COMBO MAP VALUE: {oldSlotShort} -> {newSlotShort}"));
                                TryPreserveDelayWindow(node.DefaultDBPath, newDbPath, gameRoot, outputPath, fileEntries, EngineVersion.VER_UE4_26, delayPatched, node.DelayWindowLo, node.DelayWindowHi);
                            }
                            found = true;
                            break;
                        }
                        if (found) break;
                    }

                    if (!found)
                    {
                        skippedNoDb++;
                        ErrorLog.Write("EXPORT", new Exception($"  NOT FOUND: {node.DisplayName} slot key '{normalizedSlotKey}' (in-place failed, no map entry)"));
                    }
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

                                // Same-path override: a CustomAssets copy of this DT is the
                                // source the patch applies to; rel stays vanilla-derived.
                                var dtRel = Path.GetRelativePath(gameRoot, dtFile).Replace('\\', '/');
                                var customDt = CustomAssets.FileForContentRel(dtRel);
                                var dtSource = File.Exists(customDt) ? customDt : dtFile;
                                if (dtSource != dtFile) _exportUsesCustomAssets = true;

                                var outDt = Path.Combine(outputPath, "Sifu", "Content", dtRel);
                                if (!dtGroups.TryGetValue(outDt, out var group))
                                {
                                    group = (dtSource, outDt, new List<(string, string)>());
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

                {
                    foreach (var node in _modifiedNodes)
                    {
                        if (string.IsNullOrEmpty(node.DefaultDBPath) || string.IsNullOrEmpty(node.AnimPath))
                            continue;

                        string oldDbShortName = Path.GetFileNameWithoutExtension(node.DefaultDBPath);
                        if (swappedSlotShorts.Contains(oldDbShortName))
                            continue;

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
                                if (!SlotKeysMatch(keyStr, normalizedDefault, mainAmbiguousFiles)) continue;

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
                                if (swappedSlotShorts.Add(oldDbShortName))
                                {
                                    mainSwaps.Add((oldDbShortName, newDbShortName, newDbFullPath, node.DefaultDBPath));
                                    TryPreserveDelayWindow(node.DefaultDBPath, newDbPath, gameRoot, outputPath, fileEntries, EngineVersion.VER_UE4_26, delayPatched, node.DelayWindowLo, node.DelayWindowHi);
                                }
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
                                    if (!SlotKeysMatch(keyStr, NormalizeSlotKey(node.DefaultDBPath), mainAmbiguousFiles)) continue;
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
                                if (swappedSlotShorts.Add(oldDbShortName))
                                {
                                    mainSwaps.Add((oldDbShortName, newDbShortName, newDbFullPath, node.DefaultDBPath));
                                    TryPreserveDelayWindow(node.DefaultDBPath, newDbPath, gameRoot, outputPath, fileEntries, EngineVersion.VER_UE4_26, delayPatched, node.DelayWindowLo, node.DelayWindowHi);
                                }
                                ErrorLog.Write("EXPORT", new Exception($"  COMBO MAP REPOINT: {oldDbShortName} -> {newDbShortName} (import[{importIdx}])"));
                            }
                        }
                    }

                    if (mainSwaps.Count > 0)
                    {
                        var swapDict = mainSwaps
                            .GroupBy(s => s.oldShortName, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                        var exactSwapDict = mainSwaps
                            .GroupBy(s => NormalizeSlotKey(s.oldFullPath), StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                        var nodesProp = comboExport.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Nodes");
                        if (nodesProp is ArrayPropertyData nodesArr)
                        {
                            int fNamesPatched = 0;
                            int fNamesExact = 0;
                            int fNamesCollateral = 0;
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
                                    string newFNamePath;
                                    if (exactSwapDict.TryGetValue(NormalizeSlotKey(curPath), out var exactSwap))
                                    {
                                        newFNamePath = exactSwap.newFullPath + "." + exactSwap.newShortName;
                                        fNamesExact++;
                                    }
                                    else if (swapDict.TryGetValue(curShortName, out var swap))
                                    {
                                        newFNamePath = swap.newFullPath + "." + swap.newShortName;
                                        fNamesCollateral++;
                                    }
                                    else continue;

                                    nameProp.Value = FName.FromString(asset, newFNamePath);
                                    fNamesPatched++;
                                }
                            }
                            if (fNamesPatched > 0)
                            {
                                comboTreeModified = true;
                                ErrorLog.Write("EXPORT", new Exception($"  COMBO FName PATCH: {fNamesPatched} m_Attacks paths in m_Nodes (exact={fNamesExact}, short-collateral={fNamesCollateral})"));
                            }
                        }

                        int mapKeysCollateral = 0;
                        foreach (var attacksMap in allMaps)
                        {
                            if (attacksMap.Value == null) continue;
                            var rebuilt = new TMap<PropertyData, PropertyData>();
                            var seenMapKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            bool mapKeyChanged = false;
                            foreach (var kvp in attacksMap.Value)
                            {
                                string keyStr = GetKeyString(kvp.Key);
                                string shortKey = Path.GetFileNameWithoutExtension(keyStr);
                                string? newFNamePath = null;
                                bool keyExact = false;
                                if (exactSwapDict.TryGetValue(NormalizeSlotKey(keyStr), out var mapSwapExact))
                                {
                                    newFNamePath = mapSwapExact.newFullPath + "." + mapSwapExact.newShortName;
                                    keyExact = true;
                                }
                                else if (swapDict.TryGetValue(shortKey, out var mapSwap))
                                {
                                    newFNamePath = mapSwap.newFullPath + "." + mapSwap.newShortName;
                                    mapKeysCollateral++;
                                }
                                if (newFNamePath != null)
                                {
                                    if (!seenMapKeys.Add(newFNamePath))
                                    {
                                        ErrorLog.Write("EXPORT", new Exception($"  COMBO MAP KEY dedup: dropped duplicate slot -> {newFNamePath}"));
                                        mapKeyChanged = true;
                                        continue;
                                    }
                                    var newKeyNameProp = new NamePropertyData
                                    {
                                        Name = new FName(asset, "m_Attacks"),
                                        Value = FName.FromString(asset, newFNamePath)
                                    };
                                    rebuilt.Add(newKeyNameProp, kvp.Value);
                                    mapKeyChanged = true;
                                    ErrorLog.Write("EXPORT", new Exception($"  COMBO MAP KEY{(keyExact ? "" : " (collateral)")}: {shortKey} -> {newFNamePath}"));
                                }
                                else
                                {
                                    if (!seenMapKeys.Add(keyStr))
                                    {
                                        ErrorLog.Write("EXPORT", new Exception($"  COMBO MAP KEY dedup: dropped duplicate slot {keyStr}"));
                                        continue;
                                    }
                                    rebuilt.Add(kvp.Key, kvp.Value);
                                }
                            }
                            if (mapKeyChanged)
                            {
                                attacksMap.Value = rebuilt;
                                comboTreeModified = true;
                            }
                        }
                        if (mapKeysCollateral > 0)
                            ErrorLog.Write("EXPORT", new Exception($"  COMBO MAP KEY: {mapKeysCollateral} key(s) renamed by short-name match only (different path, same filename)"));

                        {
                            int condReplaced = 0;
                            int condStale = 0;
                            var condNodesProp = comboExport.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Nodes");
                            if (condNodesProp is ArrayPropertyData condNodesArr && condNodesArr.Value != null)
                            {
                                foreach (var elem in condNodesArr.Value)
                                {
                                    if (elem is not StructPropertyData condNodeStruct || condNodeStruct.Value == null) continue;
                                    foreach (var condMap in FindAllMapsNamed(condNodeStruct.Value, "m_ConditionalAttacks"))
                                    {
                                        if (condMap.Value == null) continue;
                                        foreach (var kvp in condMap.Value)
                                            RewriteConditionalAttacksRefs(kvp.Value, exactSwapDict, asset, ref condReplaced, ref condStale);
                                    }
                                }
                            }
                            if (condReplaced > 0)
                            {
                                comboTreeModified = true;
                                ErrorLog.Write("EXPORT", new Exception($"  COND ATTACKS: rewrote {condReplaced} m_Attacks refs in m_ConditionalAttacks ({condStale} stale same-name refs left untouched)"));
                            }
                            else if (condStale > 0)
                            {
                                ErrorLog.Write("EXPORT", new Exception($"  COND ATTACKS: {condStale} m_Attacks refs match a swapped filename but a different path — left untouched"));
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

                var swappedShortNames = new HashSet<string>(swappedSlotShorts, StringComparer.OrdinalIgnoreCase);
                foreach (var node in redirectNodes)
                {
                    string slotShort = Path.GetFileNameWithoutExtension(node.DefaultDBPath);
                    if (swappedShortNames.Contains(slotShort)) continue;

                    var relDbPath = node.DefaultDBPath.TrimStart('/');
                    if (relDbPath.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
                        relDbPath = relDbPath.Substring(5);
                    var vanillaDbFile = ResolveStageSource(gameRoot, relDbPath)
                        ?? Path.Combine(gameRoot, relDbPath + ".uasset");
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
                    if (File.Exists(comboVanillaUexp) && !File.Exists(comboOutUexp))
                        File.Copy(comboVanillaUexp, comboOutUexp, true);
                    ErrorLog.Write("EXPORT", new Exception(
                        $"  COMBO TREE WROTE: .uasset {new FileInfo(outUasset).Length}B" +
                        (File.Exists(comboOutUexp) ? $", .uexp {new FileInfo(comboOutUexp).Length}B" : ", .uexp MISSING")));
                }
            });

            UpdateStep(2, "done");
            SetProgress(50);

            if (comboTreeModified)
            {
                fileEntries.Add((outUasset, "../../../Sifu/Content/" + mainComboRel + ".uasset"));
                var mainComboOutUexp = Path.ChangeExtension(outUasset, ".uexp");
                if (File.Exists(mainComboOutUexp))
                {
                    fileEntries.Add((mainComboOutUexp,
                        "../../../Sifu/Content/" + mainComboRel + ".uexp"));
                }
                else
                {
                    ErrorLog.Write("EXPORT", new Exception($"WARNING: combo tree .uexp missing, not packing: {mainComboOutUexp}"));
                }
            }

            if (!string.IsNullOrEmpty(_enemyComboPath))
            {
                var enemyComboRelPath = GamePathToContentRel(_enemyComboPath);
                var enemyVanillaPath = ResolveStageSource(gameRoot, enemyComboRelPath)
                    ?? Path.Combine(gameRoot, enemyComboRelPath + ".uasset");
                if (File.Exists(enemyVanillaPath))
                {
                    var enemyOutDir = Path.Combine(outputPath, "Sifu", "Content", Path.GetDirectoryName(enemyComboRelPath)!);
                    Directory.CreateDirectory(enemyOutDir);
                    var enemyOutUasset = Path.Combine(enemyOutDir, Path.GetFileName(enemyComboRelPath) + ".uasset");
                    bool enemyComboWritten = false;

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
                        var enemyDelayPatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        {
                            var enemyAllMaps = FindAllMapsNamed(enemyComboExport.Data, "m_Attacks");
                            ErrorLog.Write("EXPORT", new Exception($"Enemy combo tree: found {enemyAllMaps.Count} m_Attacks maps, {enemyAsset.Imports.Count} imports"));
                            var enemyAmbiguousFiles = BuildAmbiguousFilenames(enemyAllMaps);

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
                                        if (!SlotKeysMatch(keyStr, normalizedDefault, enemyAmbiguousFiles)) continue;

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
                                        TryPreserveDelayWindow(node.DefaultDBPath, newDbPath, gameRoot, outputPath, fileEntries, EngineVersion.VER_UE4_26, enemyDelayPatched, node.DelayWindowLo, node.DelayWindowHi);
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
                                var swapResult = ApplyComboImportSwaps(enemyAsset, enemyComboExport, enemyComboBinSwaps, "ENEMY");
                                var swapDict = swapResult.Applied;
                                ErrorLog.Write("EXPORT", new Exception($"  [{swapResult}]"));

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
                                    var seenMapKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    foreach (var kvp in mAttacksMap.Value)
                                    {
                                        string keyStr = GetKeyString(kvp.Key);
                                        string shortKey = Path.GetFileNameWithoutExtension(keyStr);
                                        if (swapDict.TryGetValue(shortKey, out var mapSwap))
                                        {
                                            string newFNamePath = mapSwap.newFullPath + "." + mapSwap.newShortName;
                                            if (!seenMapKeys.Add(newFNamePath))
                                            {
                                                ErrorLog.Write("EXPORT", new Exception($"  UAPI MAP KEY dedup: dropped duplicate slot -> {newFNamePath}"));
                                                continue;
                                            }
                                            var newKeyNameProp = new NamePropertyData
                                            {
                                                Name = new FName(enemyAsset, "m_Attacks"),
                                                Value = FName.FromString(enemyAsset, newFNamePath)
                                            };
                                            if (kvp.Value is ObjectPropertyData objVal
                                                && swapResult.OwnerImportIndex.TryGetValue(shortKey, out var ownerIdx))
                                            {
                                                objVal.Value = FPackageIndex.FromImport(ownerIdx);
                                            }
                                            rebuilt.Add(newKeyNameProp, kvp.Value);
                                            ErrorLog.Write("EXPORT", new Exception($"  UAPI MAP KEY: {shortKey} -> {newFNamePath}"));
                                        }
                                        else
                                        {
                                            if (!seenMapKeys.Add(keyStr))
                                            {
                                                ErrorLog.Write("EXPORT", new Exception($"  UAPI MAP KEY dedup: dropped duplicate slot {keyStr}"));
                                                continue;
                                            }
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

                            int enemyRedirectPatched = 0;
                            if (_graph != null)
                            {
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

                            // Only rewrite the combo tree when this export actually changed it -
                            // with no swaps and no retargets the write was a pointless re-serialize
                            // of vanilla (and, being staged, it shipped an unmodified tree).
                            if (enemyComboBinSwaps.Count > 0 || enemyRedirectPatched > 0)
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(enemyOutUasset)!);
                                enemyAsset.Write(enemyOutUasset);
                                enemyComboWritten = true;
                                ErrorLog.Write("EXPORT", new Exception($"  UAPI: wrote {new FileInfo(enemyOutUasset).Length} bytes .uasset"));
                                try { var dbgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_export"); Directory.CreateDirectory(dbgDir); File.Copy(enemyOutUasset, Path.Combine(dbgDir, "debug_combo.uasset"), true); var uexpSrc = Path.ChangeExtension(enemyOutUasset, ".uexp"); if (File.Exists(uexpSrc)) File.Copy(uexpSrc, Path.Combine(dbgDir, "debug_combo.uexp"), true); } catch { }
                                var outUexpPath = Path.ChangeExtension(enemyOutUasset, ".uexp");
                                if (File.Exists(outUexpPath))
                                    ErrorLog.Write("EXPORT", new Exception($"  UAPI: wrote {new FileInfo(outUexpPath).Length} bytes .uexp"));
                            }
                            else
                            {
                                ErrorLog.Write("EXPORT", new Exception("  ENEMY SKIP: no attack swaps or retargets - combo tree left as vanilla"));
                            }

                        var comboPathParts = enemyComboRelPath.Replace('\\', '/').Split('/');
                        int archetypesIdx = Array.IndexOf(comboPathParts, "Archetypes");
                        if (archetypesIdx >= 0 && archetypesIdx + 1 < comboPathParts.Length)
                        {
                            string charRoot = string.Join("/", comboPathParts.Take(archetypesIdx + 2));
                            string charArenaDir = Path.Combine(gameRoot, charRoot, "_Arena");
                            string customArenaDir = Path.Combine(CustomAssets.Root,
                                charRoot.Replace('/', Path.DirectorySeparatorChar), "_Arena");
                            if (enemyComboBinSwaps.Count == 0 && !hasRetargets)
                            {
                                ErrorLog.Write("EXPORT", new Exception("  ARENA SKIP: no attack swaps or retargets for this unit"));
                            }
                            else if (Directory.Exists(charArenaDir) || Directory.Exists(customArenaDir))
                            {
                                // Vanilla arena combos first, then CustomAssets entries: a
                                // custom-only combo is added, a same-path one replaces its
                                // vanilla row so the override is what gets patched and staged.
                                var arenaSources = new List<(string path, string rel)>();
                                var seenRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                if (Directory.Exists(charArenaDir))
                                {
                                    foreach (var f in Directory.GetFiles(charArenaDir, "*.uasset", SearchOption.AllDirectories))
                                    {
                                        if (!Path.GetFileNameWithoutExtension(f).Contains("Combo", StringComparison.OrdinalIgnoreCase)) continue;
                                        var r = Path.GetRelativePath(gameRoot, f).Replace('\\', '/');
                                        if (seenRels.Add(r)) arenaSources.Add((f, r));
                                    }
                                }
                                if (Directory.Exists(customArenaDir))
                                {
                                    foreach (var f in Directory.GetFiles(customArenaDir, "*.uasset", SearchOption.AllDirectories))
                                    {
                                        if (!Path.GetFileNameWithoutExtension(f).Contains("Combo", StringComparison.OrdinalIgnoreCase)) continue;
                                        var r = Path.GetRelativePath(CustomAssets.Root, f).Replace('\\', '/');
                                        int existing = arenaSources.FindIndex(x =>
                                            string.Equals(x.rel, r, StringComparison.OrdinalIgnoreCase));
                                        if (existing >= 0) arenaSources[existing] = (f, r);
                                        else arenaSources.Add((f, r));
                                        _exportUsesCustomAssets = true;
                                    }
                                }

                                foreach (var (arenaComboVanilla, arenaRelFull) in arenaSources)
                                {
                                    var arenaComboRelFromContent = arenaRelFull;
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

                                        var arenaSwapResult = ApplyComboImportSwaps(arenaAsset, arenaComboExport, enemyComboBinSwaps, "ARENA");
                                        var arenaSwapDict = arenaSwapResult.Applied;
                                        ErrorLog.Write("EXPORT", new Exception($"  [{arenaSwapResult}] in {arenaFileName}"));

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
                                            var seenMapKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                            foreach (var kvp in arenaMap.Value)
                                            {
                                                string keyStr = GetKeyString(kvp.Key);
                                                string shortKey = Path.GetFileNameWithoutExtension(keyStr);
                                                if (arenaSwapDict.TryGetValue(shortKey, out var mapSwap))
                                                {
                                                    string newFNamePath = mapSwap.newFullPath + "." + mapSwap.newShortName;
                                                    if (!seenMapKeys.Add(newFNamePath))
                                                    {
                                                        ErrorLog.Write("EXPORT", new Exception($"  ARENA MAP KEY dedup: dropped duplicate slot -> {newFNamePath}"));
                                                        continue;
                                                    }
                                                    var newKeyNameProp = new NamePropertyData
                                                    {
                                                        Name = new FName(arenaAsset, "m_Attacks"),
                                                        Value = FName.FromString(arenaAsset, newFNamePath)
                                                    };
                                                    if (kvp.Value is ObjectPropertyData objVal
                                                        && arenaSwapResult.OwnerImportIndex.TryGetValue(shortKey, out var ownerIdx))
                                                    {
                                                        objVal.Value = FPackageIndex.FromImport(ownerIdx);
                                                    }
                                                    rebuilt.Add(newKeyNameProp, kvp.Value);
                                                }
                                                else
                                                {
                                                    if (!seenMapKeys.Add(keyStr))
                                                    {
                                                        ErrorLog.Write("EXPORT", new Exception($"  ARENA MAP KEY dedup: dropped duplicate slot {keyStr}"));
                                                        continue;
                                                    }
                                                    rebuilt.Add(kvp.Key, kvp.Value);
                                                }
                                            }
                                            arenaMap.Value = rebuilt;
                                        }

                                        int arenaRedirectPatched = 0;
                                        if (hasRetargets && _graph != null)
                                        {
                                            var arenaNameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                            for (int i = 0; i < arenaNodesArr.Value.Length; i++)
                                            {
                                                if (arenaNodesArr.Value[i] is not StructPropertyData ns || ns.Value == null) continue;
                                                string nm = ns.Value.OfType<NamePropertyData>()
                                                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Name")?.Value?.Value?.ToString() ?? "";
                                                if (!string.IsNullOrEmpty(nm) && !arenaNameToIndex.ContainsKey(nm))
                                                    arenaNameToIndex[nm] = i;
                                            }

                                            for (int i = 0; i < arenaNodesArr.Value.Length; i++)
                                            {
                                                if (arenaNodesArr.Value[i] is not StructPropertyData nodeSp || nodeSp.Value == null) continue;
                                                var redirectProp = nodeSp.Value.OfType<IntPropertyData>()
                                                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_NodeRedirect");
                                                if (redirectProp == null || redirectProp.Value < 0) continue;

                                                string nodeName = nodeSp.Value.OfType<NamePropertyData>()
                                                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Name")?.Value?.Value?.ToString() ?? "";

                                                var graphNode = _graph.Nodes
                                                    .FirstOrDefault(n => n.IsRedirect && !string.IsNullOrEmpty(n.Name) && n.Name == nodeName);
                                                if (graphNode == null) continue;
                                                if (!_graph.RedirectOriginalTargets.TryGetValue(graphNode.Id, out var origTarget)) continue;
                                                int newTarget = graphNode.ResolvedRedirectNodeId;
                                                if (newTarget < 0 || newTarget == origTarget) continue;
                                                var newTargetNode = _graph.Nodes.FirstOrDefault(n => n.Id == newTarget);
                                                if (newTargetNode == null || string.IsNullOrEmpty(newTargetNode.Name)) continue;
                                                if (!arenaNameToIndex.TryGetValue(newTargetNode.Name, out int targetIdx)) continue;

                                                redirectProp.Value = targetIdx;
                                                arenaRedirectPatched++;
                                                ErrorLog.Write("EXPORT", new Exception($"  ARENA REDIRECT PATCH: [{i}] {nodeName} -> [{targetIdx}] {newTargetNode.Name}"));
                                            }
                                            if (arenaRedirectPatched > 0)
                                                ErrorLog.Write("EXPORT", new Exception($"Arena redirect patches: {arenaRedirectPatched} in {arenaFileName}"));
                                        }

                                        if (arenaFNamesPatched == 0 && arenaRedirectPatched == 0
                                            && arenaSwapResult.ImportsRenamed == 0 && arenaSwapResult.ImportsRepointed == 0)
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

                                        ErrorLog.Write("EXPORT", new Exception($"  ARENA: patched {arenaFileName} ({new FileInfo(arenaOutUasset).Length} bytes .uasset, {arenaFNamesPatched} FNames, {arenaRedirectPatched} redirects, {arenaSwapResult}, unit '{_currentExportVariant}')"));
                                    }
                                    catch (Exception ex)
                                    {
                                        ErrorLog.Write("EXPORT", new Exception($"  ARENA FAIL: {arenaFileName}: {ex.Message}"));
                                    }
                                }
                            }
                        }
                        });

                    if (enemyComboWritten && File.Exists(enemyOutUasset))
                    {
                        fileEntries.Add((enemyOutUasset,
                            "../../../Sifu/Content/" + enemyComboRelPath + ".uasset"));
                        var enemyUexpOut = Path.ChangeExtension(enemyOutUasset, ".uexp");
                        if (File.Exists(enemyUexpOut))
                            fileEntries.Add((enemyUexpOut,
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
                var vanillaTransPath = ResolveStageSource(gameRoot, "DB/Movement/Transition/BP_TransitionAnimRequest")
                    ?? Path.Combine(gameRoot, "DB/Movement/Transition/BP_TransitionAnimRequest.uasset");
                var charTransPath = ResolveStageSource(gameRoot, _charTransitionPath)
                    ?? Path.Combine(gameRoot, _charTransitionPath + ".uasset");

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
                var vanillaDbPath = ResolveStageSource(gameRoot, "DB/Movement/BaseMovementDB")
                    ?? Path.Combine(gameRoot, "DB/Movement/BaseMovementDB.uasset");
                var charDbPath = ResolveStageSource(gameRoot, _charBaseMovementDBPath)
                    ?? Path.Combine(gameRoot, _charBaseMovementDBPath + ".uasset");

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

    private static bool SlotKeysMatch(string keyStr, string normalizedSlotKey, HashSet<string>? ambiguousFiles = null)
    {
        if (string.IsNullOrEmpty(keyStr) || string.IsNullOrEmpty(normalizedSlotKey)) return false;
        if (string.Equals(keyStr, normalizedSlotKey, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(NormalizeSlotKey(keyStr), normalizedSlotKey, StringComparison.OrdinalIgnoreCase)) return true;
        string keyFile = Path.GetFileNameWithoutExtension(keyStr);
        if (!string.Equals(keyFile, Path.GetFileNameWithoutExtension(normalizedSlotKey), StringComparison.OrdinalIgnoreCase))
            return false;
        if (ambiguousFiles != null && ambiguousFiles.Contains(keyFile))
            return false;
        return true;
    }

    private static HashSet<string> BuildAmbiguousFilenames(List<MapPropertyData> maps)
    {
        var distinctKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in maps)
        {
            if (m.Value == null) continue;
            foreach (var kvp in m.Value)
            {
                string nk = NormalizeSlotKey(GetKeyString(kvp.Key));
                if (!string.IsNullOrEmpty(nk)) distinctKeys.Add(nk);
            }
        }
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in distinctKeys)
        {
            string fn = Path.GetFileNameWithoutExtension(key);
            if (string.IsNullOrEmpty(fn)) continue;
            counts[fn] = counts.TryGetValue(fn, out var c) ? c + 1 : 1;
        }
        return counts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void RewriteConditionalAttacksRefs(
        PropertyData value,
        Dictionary<string, (string oldShortName, string newShortName, string newFullPath, string oldFullPath)> exactSwapDict,
        UAsset asset,
        ref int replaced,
        ref int stale)
    {
        if (value is NamePropertyData np && string.Equals(np.Name?.Value?.ToString(), "m_Attacks", StringComparison.Ordinal))
        {
            string curPath = np.Value?.Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(curPath) || curPath == "None") return;
            if (exactSwapDict.TryGetValue(NormalizeSlotKey(curPath), out var swap))
            {
                np.Value = FName.FromString(asset, swap.newFullPath + "." + swap.newShortName);
                replaced++;
            }
            else
            {
                string curShort = Path.GetFileNameWithoutExtension(curPath);
                if (exactSwapDict.Values.Any(s =>
                    string.Equals(s.oldShortName, curShort, StringComparison.OrdinalIgnoreCase)))
                    stale++;
            }
            return;
        }
        if (value is StructPropertyData sp && sp.Value != null)
        {
            foreach (var child in sp.Value)
                RewriteConditionalAttacksRefs(child, exactSwapDict, asset, ref replaced, ref stale);
        }
        else if (value is ArrayPropertyData ap && ap.Value != null)
        {
            foreach (var child in ap.Value)
                RewriteConditionalAttacksRefs(child, exactSwapDict, asset, ref replaced, ref stale);
        }
        else if (value is MapPropertyData mp && mp.Value != null)
        {
            foreach (var kvp in mp.Value)
                RewriteConditionalAttacksRefs(kvp.Value, exactSwapDict, asset, ref replaced, ref stale);
        }
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

        int existing = FindImport(asset, attackName, attackPath);
        if (existing >= 0) return existing;

        int pkgIdx = -1;
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var imp = asset.Imports[i];
            if (imp.OuterIndex == null || imp.OuterIndex.Index != 0) continue;
            if (string.Equals(imp.ObjectName?.Value?.ToString() ?? "", attackPath, StringComparison.Ordinal))
            {
                pkgIdx = i;
                break;
            }
        }

        if (pkgIdx < 0)
        {
            var pkgImport = new UAssetAPI.Import();
            pkgImport.ClassPackage = FName.FromString(asset, "/Script/CoreUObject");
            pkgImport.ClassName = FName.FromString(asset, "Package");
            pkgImport.ObjectName = FName.FromString(asset, attackPath);
            pkgImport.OuterIndex = new FPackageIndex(0);
            pkgImport.PackageName = FName.FromString(asset, "None");
            pkgIdx = asset.Imports.Count;
            asset.Imports.Add(pkgImport);
        }
        else
        {
            ErrorLog.Write("EXPORT", new Exception($"  ADD IMPORT: reusing existing package import[{pkgIdx}] {attackPath}"));
        }

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

    private sealed class ComboImportSwapResult
    {
        public Dictionary<string, (string newShortName, string newFullPath)> Applied
            = new(StringComparer.Ordinal);
        public Dictionary<string, int> OwnerImportIndex = new(StringComparer.Ordinal);
        public int ImportsRenamed;
        public int ImportsRepointed;
        public int Skipped;
        public int DroppedDuplicates;

        public override string ToString() =>
            $"applied={Applied.Count} renamed={ImportsRenamed} repointed={ImportsRepointed} skipped={Skipped} dupDropped={DroppedDuplicates}";
    }

    private static ComboImportSwapResult ApplyComboImportSwaps(
        UAsset asset,
        NormalExport comboExport,
        List<(string oldShortName, string newShortName, string newFullPath)> rawSwaps,
        string label)
    {
        var result = new ComboImportSwapResult();
        if (rawSwaps == null || rawSwaps.Count == 0) return result;

        var swaps = new List<(string oldShortName, string newShortName, string newFullPath)>();
        foreach (var g in rawSwaps.GroupBy(s => s.oldShortName, StringComparer.Ordinal))
        {
            swaps.Add(g.First());
            if (g.Count() > 1)
            {
                result.DroppedDuplicates += g.Count() - 1;
                ErrorLog.Write("EXPORT", new Exception(
                    $"  [{label}] DUP SWAP dropped {g.Count() - 1}x '{g.Key}' (kept -> {g.First().newShortName})"));
            }
        }

        string NameOf(int i) => i < 0 || i >= asset.Imports.Count
            ? ""
            : asset.Imports[i].ObjectName?.Value?.ToString() ?? "";

        bool IsPackageImport(int i) => i >= 0 && i < asset.Imports.Count
            && asset.Imports[i].OuterIndex != null && asset.Imports[i].OuterIndex.Index == 0;

        int RawOuter(int i)
        {
            if (i < 0 || i >= asset.Imports.Count) return -1;
            var imp = asset.Imports[i];
            if (imp.OuterIndex == null || imp.OuterIndex.Index >= 0) return -1;
            int o = -(imp.OuterIndex.Index + 1);
            return o >= 0 && o < asset.Imports.Count ? o : -1;
        }

        List<int> FindAllByName(string name)
        {
            var hits = new List<int>();
            for (int i = 0; i < asset.Imports.Count; i++)
                if (NameOf(i) == name) hits.Add(i);
            return hits;
        }

        var renameObj = new Dictionary<int, string>();
        var renamePkg = new Dictionary<int, string>();
        var repointTo = new Dictionary<int, int>();
        var planned = new Dictionary<(string obj, string pkg), int>();

        string FinalObjName(int i) => renameObj.TryGetValue(i, out var t) ? t : NameOf(i);

        string FinalPkgName(int i)
        {
            int p = RawOuter(i);
            if (p < 0) return "";
            return renamePkg.TryGetValue(p, out var t) ? t : NameOf(p);
        }

        var targetPkgs = new HashSet<string>(swaps.Select(s => s.newFullPath), StringComparer.Ordinal);

        bool PlanOne((string oldShortName, string newShortName, string newFullPath) s)
        {
            var identity = (s.newShortName, s.newFullPath);

            int owner = planned.TryGetValue(identity, out var known) ? known : -1;
            if (owner < 0)
            {
                for (int i = 0; i < asset.Imports.Count; i++)
                {
                    if (FinalObjName(i) == s.newShortName && FinalPkgName(i) == s.newFullPath)
                    {
                        owner = i;
                        break;
                    }
                }
            }

            if (owner >= 0)
            {
                planned[identity] = owner;
                foreach (int c in FindAllByName(s.oldShortName))
                    if (c != owner && !repointTo.ContainsKey(c)) repointTo[c] = owner;
                result.OwnerImportIndex[s.oldShortName] = owner;
                return true;
            }

            var candidates = FindAllByName(s.oldShortName);
            if (candidates.Count > 1)
            {
                ErrorLog.Write("EXPORT", new Exception(
                    $"  [{label}] SWAP WARN: {candidates.Count} imports named '{s.oldShortName}', only the first is moved"));
            }

            int mover = -1;
            int moverPkg = -1;
            foreach (int c in candidates)
            {
                if (repointTo.ContainsKey(c)) continue;
                int p = RawOuter(c);
                if (p < 0 || !IsPackageImport(p)) continue;
                if (renamePkg.TryGetValue(p, out var already))
                {
                    if (already != s.newFullPath) continue;
                }
                else if (targetPkgs.Contains(NameOf(p)) && !renamePkg.ContainsValue(NameOf(p)))
                {
                    continue;
                }
                mover = c;
                moverPkg = p;
                break;
            }

            if (mover < 0) return false;

            if (NameOf(moverPkg) != s.newFullPath)
            {
                if (renamePkg.TryGetValue(moverPkg, out var cur) && cur != s.newFullPath) return false;
                renamePkg[moverPkg] = s.newFullPath;
            }

            renameObj[mover] = s.newShortName;
            planned[identity] = mover;
            foreach (int c in candidates)
                if (c != mover && !repointTo.ContainsKey(c)) repointTo[c] = mover;
            result.OwnerImportIndex[s.oldShortName] = mover;
            return true;
        }

        var pending = new List<(string oldShortName, string newShortName, string newFullPath)>(swaps);
        for (int pass = 0; pass < 3 && pending.Count > 0; pass++)
        {
            var next = new List<(string oldShortName, string newShortName, string newFullPath)>();
            foreach (var s in pending)
            {
                if (PlanOne(s))
                    result.Applied[s.oldShortName] = (s.newShortName, s.newFullPath);
                else
                    next.Add(s);
            }

            if (pass == 2)
            {
                foreach (var s in next)
                {
                    result.Skipped++;
                    ErrorLog.Write("EXPORT", new Exception(
                        $"  [{label}] SWAP SKIP: '{s.oldShortName}' -> '{s.newShortName}' ({s.newFullPath}) could not be resolved safely"));
                }
            }
            pending = next;
        }

        if (result.Applied.Count == 0) return result;

        foreach (var kv in renamePkg)
        {
            asset.Imports[kv.Key].ObjectName = FName.FromString(asset, kv.Value);
            result.ImportsRenamed++;
            ErrorLog.Write("EXPORT", new Exception($"  [{label}] SWAP package import[{kv.Key}]: -> {kv.Value}"));
        }
        foreach (var kv in renameObj)
        {
            asset.Imports[kv.Key].ObjectName = FName.FromString(asset, kv.Value);
            result.ImportsRenamed++;
            ErrorLog.Write("EXPORT", new Exception($"  [{label}] SWAP object import[{kv.Key}]: -> {kv.Value}"));
        }

        if (repointTo.Count > 0)
        {
            foreach (int c in repointTo.Keys.ToList())
            {
                int cur = c;
                var seen = new HashSet<int>();
                while (repointTo.TryGetValue(cur, out var nxt) && seen.Add(cur)) cur = nxt;
                repointTo[c] = cur;
            }

            int repointed = 0;
            void Walk(PropertyData p)
            {
                if (p is ObjectPropertyData op && op.Value != null)
                {
                    int raw = op.Value.Index;
                    if (raw < 0)
                    {
                        int idx = -(raw + 1);
                        if (repointTo.TryGetValue(idx, out var target))
                        {
                            op.Value = FPackageIndex.FromImport(target);
                            repointed++;
                        }
                    }
                    return;
                }
                if (p is StructPropertyData sp)
                {
                    if (sp.Value != null) foreach (var c in sp.Value) Walk(c);
                    return;
                }
                if (p is ArrayPropertyData ap)
                {
                    if (ap.Value != null) foreach (var c in ap.Value) Walk(c);
                    return;
                }
                if (p is MapPropertyData mp && mp.Value != null)
                {
                    foreach (var kvp in mp.Value)
                    {
                        Walk(kvp.Key);
                        Walk(kvp.Value);
                    }
                }
            }

            if (comboExport?.Data != null)
                foreach (var p in comboExport.Data) Walk(p);

            result.ImportsRepointed = repointed;
            foreach (var kv in repointTo)
                ErrorLog.Write("EXPORT", new Exception($"  [{label}] SWAP repoint import[{kv.Key}] -> import[{kv.Value}]"));
        }

        return result;
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
                var vanillaPath = ResolveStageSource(gameRoot, archRel)
                    ?? Path.Combine(gameRoot, archRel + ".uasset");
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
                var vanillaDefPath = ResolveStageSource(gameRoot, defRel)
                    ?? Path.Combine(gameRoot, defRel + ".uasset");
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
            // Full exception (message + stack): a swallowed per-unit failure used to be
            // indistinguishable from success in the UI - log the exact line, and the
            // completion panel now shows a warning count (ErrorLog.Writes delta).
            ErrorLog.Write("EXPORT", new Exception($"[UNIT_PROPS] Error patching {variantTag}: {ex}"));
        }
    }

    /// <summary>
    /// Stages and patches every attack card whose node carries AttackDB tuning
    /// (m_iWantedBuildupFrames / m_fGameplayRange). The card actually used in game is
    /// SourceDBPath when the node's slot was swapped to another attack, DefaultDBPath otherwise.
    /// </summary>
    private void PatchAttackDbFields(List<(string src, string dest)> fileEntries, string gameRoot, string outputPath)
    {
        var graphs = new List<ComboGraph>();
        if (_unitCaches != null && _perUnitGraphs.Count > 0)
        {
            foreach (var kvp in _perUnitGraphs)
                if (kvp.Value.graph != null) graphs.Add(kvp.Value.graph);
        }
        else if (_graph != null)
        {
            graphs.Add(_graph);
        }

        var byCard = new Dictionary<string, ComboNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var graph in graphs)
        {
            foreach (var node in graph.Nodes)
            {
                if (!ProjectChangeSummary.HasAttackDbTuning(node)) continue;
                if (node.IsRedirect || string.IsNullOrEmpty(node.DefaultDBPath))
                {
                    ErrorLog.Write("EXPORT", new Exception($"  ATTACKDB SKIP (no card): {node.DisplayName}"));
                    continue;
                }
                string cardDb = AttackDbCard.EffectiveCardPath(node);
                if (byCard.TryGetValue(cardDb, out var other))
                {
                    ErrorLog.Write("EXPORT", new Exception(
                        $"  ATTACKDB CONFLICT: {cardDb} tuned by both '{other.DisplayName}' and '{node.DisplayName}' - keeping first"));
                    continue;
                }
                byCard[cardDb] = node;
            }
        }

        foreach (var kvp in byCard)
        {
            var node = kvp.Value;
            string rel = AttackDbCard.GamePathToContentRel(kvp.Key);
            if (string.IsNullOrEmpty(rel))
            {
                ErrorLog.Write("EXPORT", new Exception($"  ATTACKDB SKIP (bad path): {node.DisplayName} -> '{kvp.Key}'"));
                continue;
            }

            string relFs = rel.Replace('/', Path.DirectorySeparatorChar);
            // Vanilla copy when present, CustomAssets otherwise - custom cards only
            // exist there and still take the buildup/range patch on their copy.
            string vanillaUasset = ResolveStageSource(gameRoot, relFs)
                ?? Path.Combine(gameRoot, relFs + ".uasset");
            string outUasset = Path.Combine(outputPath, "Sifu", "Content", relFs + ".uasset");
            string destBase = "../../../Sifu/Content/" + rel;

            // Resolve the payload base per extension: whichever half (custom or vanilla)
            // exists - the halves of a byte-identical pair are interchangeable, and a half
            // differing from vanilla keeps the mod's bytes under the tuning patch.
            string? baseUexp;
            var customUexp = CustomAssets.FileForContentRel(relFs + ".uexp");
            var trueVanillaUexp = Path.ChangeExtension(Path.Combine(gameRoot, relFs + ".uasset"), ".uexp");
            if (File.Exists(customUexp)) baseUexp = customUexp;
            else if (File.Exists(trueVanillaUexp)) baseUexp = trueVanillaUexp;
            else baseUexp = null;

            if (!AttackDbCard.StageAndPatch(vanillaUasset, outUasset, destBase,
                    node.AttackBuildupFrames, node.AttackGameplayRange, fileEntries, out var err,
                    baseUexp))
            {
                ErrorLog.Write("EXPORT", new Exception($"  ATTACKDB FAIL: {node.DisplayName} ({rel}): {err}"));
                continue;
            }

            ErrorLog.Write("EXPORT", new Exception(
                $"  ATTACKDB: {node.DisplayName} -> {rel} buildup={node.AttackBuildupFrames?.ToString() ?? "-"} range={node.AttackGameplayRange?.ToString() ?? "-"}"));
        }
    }

    private static bool PatchDbAnimation(string vanillaDbPath, string newAnimPath, string outputDbPath, EngineVersion eng)
    {
        try
        {
            NoteAnimRef(newAnimPath);
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

    /// <summary>
    /// Source file for a Content-relative asset: the persistent CustomAssets copy when
    /// it exists (a byte-differing vanilla overlap that Harvest shipped, or a manual
    /// same-path drop - both deliberate overrides), otherwise the vanilla game copy.
    /// Null when neither side has it. A custom hit also trips the export's
    /// CustomAssets dependency flag.
    /// </summary>
    private static string? ResolveStageSource(string gameRoot, string contentRel)
    {
        var rel = contentRel.Replace('\\', '/');
        var custom = CustomAssets.FileForContentRel(rel + ".uasset");
        if (File.Exists(custom))
        {
            _exportUsesCustomAssets = true;
            return custom;
        }
        var vanilla = Path.Combine(gameRoot,
            rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        return File.Exists(vanilla) ? vanilla : null;
    }

    /// <summary>True when a Content-relative asset exists in the CustomAssets folder.</summary>
    private static bool CustomRefExists(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return false;
        var rel = GamePathToContentRel(gamePath).Replace('\\', '/');
        foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk" })
        {
            if (rel.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                rel = rel[..^ext.Length];
                break;
            }
        }
        return File.Exists(CustomAssets.FileForContentRel(rel + ".uasset"));
    }

    private static void NoteAnimRef(string? animPath)
    {
        if (CustomRefExists(animPath)) _exportUsesCustomAssets = true;
    }

    /// <summary>
    /// True when CustomAssets holds a file at a path the vanilla game also has - a
    /// deliberate same-path override. Overrides always ship, even when no changed
    /// node or patch in this export references them.
    /// </summary>
    private static bool CustomAssetsHasOverrides(string gameRoot)
    {
        try
        {
            if (!Directory.Exists(CustomAssets.Root)) return false;
            foreach (var file in Directory.GetFiles(CustomAssets.Root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(CustomAssets.Root, file)
                    .Replace('/', Path.DirectorySeparatorChar);
                if (File.Exists(Path.Combine(gameRoot, rel))) return true;
            }
        }
        catch { }
        return false;
    }

    private bool IsExcludedCustomDest(string dest)
    {
        if (_excludedCustomStems.Count == 0) return false;
        const string Prefix = "../../../Sifu/Content/";
        if (!dest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rel = dest[Prefix.Length..].Replace('\\', '/');
        return _excludedCustomStems.Contains(CustomAssets.AssetStemOfRel(rel));
    }

    /// <summary>
    /// Packs the persistent CustomAssets library into the pak (ship it all - cards,
    /// anims, and the Effects/Blueprints/Characters files they reference). Files a
    /// patching pass already staged keep their patched version; only the unpatched
    /// remainder is added here. Files whose stem the user unchecked on the Custom
    /// Files page are skipped entirely (companions included).
    /// </summary>
    private static void StageCustomAssetsFiles(
        List<(string src, string dest)> fileEntries, ISet<string>? excludedStems = null)
    {
        try
        {
            if (!Directory.Exists(CustomAssets.Root)) return;
            var stagedDests = new HashSet<string>(
                fileEntries.Select(e => e.dest), StringComparer.OrdinalIgnoreCase);
            int added = 0;
            long bytes = 0;
            int excluded = 0;
            foreach (var file in Directory.GetFiles(CustomAssets.Root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(CustomAssets.Root, file).Replace('\\', '/');
                // Defensive twin of the Harvest exclusion: a legacy CustomAssets may
                // still hold a harvested .pak; never pack it back into the export.
                if (rel.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) continue;
                if (excludedStems != null
                    && excludedStems.Contains(CustomAssets.AssetStemOfRel(rel)))
                {
                    excluded++;
                    continue;
                }
                var dest = "../../../Sifu/Content/" + rel;
                if (!stagedDests.Add(dest)) continue;
                fileEntries.Add((file, dest));
                added++;
                try { bytes += new FileInfo(file).Length; } catch { }
            }
            if (added > 0 || excluded > 0)
                ErrorLog.Write("EXPORT", new Exception(
                    $"[CUSTOM-ASSETS] staged {added} file(s) ({bytes / 1024 / 1024} MB) from CustomAssets"
                    + (excluded > 0 ? $", {excluded} unchecked by user" : "")));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", ex);
        }
    }

    private static void TryPreserveDelayWindow(
        string originalDbGamePath, string replacementDbGamePath, string gameRoot,
        string outputPath, List<(string src, string dest)> fileEntries, EngineVersion eng,
        HashSet<string> alreadyPatched, int? winLo = null, int? winHi = null)
    {
        const string LayerName = "MC_Attacks_Alt";
        // Manual per-node window override (frames on the replacement's timeline);
        // anything invalid keeps the bounds copied from the original card.
        bool hasOverride = winLo.HasValue && winHi.HasValue
            && winLo.Value >= 0 && winHi.Value > winLo.Value;
        try
        {
            if (string.IsNullOrEmpty(originalDbGamePath) || string.IsNullOrEmpty(replacementDbGamePath)) return;

            string origRel = GamePathToContentRel(originalDbGamePath);
            string replRel = GamePathToContentRel(replacementDbGamePath);
            string origFile = ResolveStageSource(gameRoot, origRel)
                ?? Path.Combine(gameRoot, origRel + ".uasset");
            string replStaged = Path.Combine(outputPath, "Sifu", "Content", replRel + ".uasset");
            string replVanilla = ResolveStageSource(gameRoot, replRel)
                ?? Path.Combine(gameRoot, replRel + ".uasset");
            if (!File.Exists(origFile)) return;
            string replFile = File.Exists(replStaged) ? replStaged : replVanilla;
            if (!File.Exists(replFile))
            {
                ErrorLog.Write("EXPORT", new Exception(
                    $"  [DELAY-WINDOW] replacement not found (vanilla or CustomAssets): {replRel}"));
                return;
            }
            if (string.Equals(origFile, replFile, StringComparison.OrdinalIgnoreCase)) return;

            var origAsset = new UAsset(origFile, eng, null, CustomSerializationFlags.None);
            var origExport = origAsset.Exports.OfType<NormalExport>().FirstOrDefault();
            if (origExport?.Data == null) return;
            var origWindows = FindWindowArray(origExport.Data);
            if (origWindows?.Value == null) return;
            var altWindows = origWindows.Value.OfType<StructPropertyData>()
                .Where(w => WindowLayerName(origAsset, w) == LayerName).ToList();
            if (altWindows.Count == 0) return;

            if (!alreadyPatched.Add(replacementDbGamePath))
            {
                ErrorLog.Write("EXPORT", new Exception($"  [DELAY-WINDOW] {replRel} already patched for another slot — skipping duplicate"));
                return;
            }

            var replAsset = new UAsset(replFile, eng, null, CustomSerializationFlags.None);
            var replExport = replAsset.Exports.OfType<NormalExport>().FirstOrDefault();
            if (replExport?.Data == null)
            {
                ErrorLog.Write("EXPORT", new Exception($"  [DELAY-WINDOW] FAIL: no NormalExport in {replRel}"));
                return;
            }
            var replWindows = FindWindowArray(replExport.Data);
            if (replWindows?.Value == null)
            {
                ErrorLog.Write("EXPORT", new Exception($"  [DELAY-WINDOW] FAIL: {replRel} has no m_AvailabilityLayerWindows — delay gate not preserved"));
                return;
            }
            if (replWindows.Value.OfType<StructPropertyData>().Any(w => WindowLayerName(replAsset, w) == LayerName))
            {
                ErrorLog.Write("EXPORT", new Exception($"  [DELAY-WINDOW] {replRel} already has a {LayerName} window — nothing to do"));
                if (File.Exists(replStaged))
                {
                    var haveUexp = Path.ChangeExtension(replStaged, ".uexp");
                    if (File.Exists(haveUexp))
                    {
                        fileEntries.RemoveAll(e => string.Equals(e.src, replStaged, StringComparison.OrdinalIgnoreCase));
                        fileEntries.Add((replStaged, "../../../Sifu/Content/" + replRel + ".uasset"));
                        fileEntries.Add((haveUexp, "../../../Sifu/Content/" + replRel + ".uexp"));
                    }
                }
                return;
            }
            int layerIdx = FindOrAddLayerImport(origAsset, replAsset, LayerName);
            if (layerIdx < 0)
            {
                ErrorLog.Write("EXPORT", new Exception($"  [DELAY-WINDOW] FAIL: original {origRel} has no importable {LayerName} layer"));
                return;
            }
            var template = replWindows.Value.OfType<StructPropertyData>().FirstOrDefault();
            if (template == null)
            {
                ErrorLog.Write("EXPORT", new Exception($"  [DELAY-WINDOW] FAIL: {replRel} window array has no struct template to clone"));
                return;
            }

            var appended = new List<PropertyData>(replWindows.Value);
            int added = 0;
            foreach (var origW in altWindows)
            {
                if (CloneWindowWithScalars(origW, template, layerIdx, out var newW, out string cloneErr))
                {
                    if (hasOverride && newW != null
                        && !DelayWindowCard.TrySetWindowBounds(newW, winLo!.Value, winHi!.Value))
                    {
                        ErrorLog.Write("EXPORT", new Exception($"  [DELAY-WINDOW] override {winLo}-{winHi} not applied to {replRel}: m_FrameRange shape unexpected — keeping copied window"));
                    }
                    appended.Add(newW);
                    added++;
                }
                else
                {
                    ErrorLog.Write("EXPORT", new Exception($"  [DELAY-WINDOW] FAIL: {replRel} window clone: {cloneErr}"));
                }
            }
            if (added == 0) return;

            replWindows.Value = appended.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(replStaged)!);
            replAsset.Write(replStaged);
            var stagedUexp = Path.ChangeExtension(replStaged, ".uexp");
            if (!File.Exists(stagedUexp))
            {
                ErrorLog.Write("EXPORT", new Exception($"  [DELAY-WINDOW] FAIL: .uexp missing after Write for {replRel} — replacement not packed (delay gate NOT preserved)"));
                return;
            }
            fileEntries.RemoveAll(e => string.Equals(e.src, replStaged, StringComparison.OrdinalIgnoreCase));
            fileEntries.Add((replStaged, "../../../Sifu/Content/" + replRel + ".uasset"));
            fileEntries.Add((stagedUexp, "../../../Sifu/Content/" + replRel + ".uexp"));
            if (hasOverride)
            {
                ErrorLog.Write("EXPORT", new Exception(
                    $"  [DELAY-WINDOW] override {winLo}-{winHi} ({winLo.Value / 60f:0.00}-{winHi.Value / 60f:0.00}s) applied to {replRel}"));
            }
            ErrorLog.Write("EXPORT", new Exception($"  [DELAY-WINDOW] preserved {LayerName} ({added} window entr{(added == 1 ? "y" : "ies")}) on {replRel}"));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", new Exception($"  [DELAY-WINDOW] FAILED {replacementDbGamePath}: {ex.Message}"));
        }
    }

    // Thin wrappers over DelayWindowCard so the panel gate and export share one finder
    // (the test harness reflects these private names).
    private static ArrayPropertyData? FindWindowArray(List<PropertyData> props)
        => DelayWindowCard.FindWindowArray(props);

    private static ArrayPropertyData? FindWindowArrayIn(PropertyData p)
        => DelayWindowCard.FindWindowArrayIn(p);

    private static string WindowLayerName(UAsset asset, StructPropertyData window)
        => DelayWindowCard.WindowLayerName(asset, window);

    private static int FindOrAddLayerImport(UAsset origAsset, UAsset replAsset, string layerName)
    {
        for (int i = 0; i < origAsset.Imports.Count; i++)
        {
            var imp = origAsset.Imports[i];
            if (!string.Equals(imp.ObjectName?.Value?.ToString(), layerName, StringComparison.Ordinal)) continue;
            int outerRaw = imp.OuterIndex?.Index ?? 0;
            if (outerRaw >= 0) continue;
            int origOuter = -(outerRaw + 1);
            if (origOuter < 0 || origOuter >= origAsset.Imports.Count) continue;
            string pkgPath = origAsset.Imports[origOuter].ObjectName?.Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(pkgPath)) continue;

            int pkgIdx = -1;
            for (int j = 0; j < replAsset.Imports.Count; j++)
            {
                var ri = replAsset.Imports[j];
                if ((ri.OuterIndex?.Index ?? -1) == 0
                    && string.Equals(ri.ObjectName?.Value?.ToString(), pkgPath, StringComparison.Ordinal))
                {
                    pkgIdx = j;
                    break;
                }
            }
            if (pkgIdx < 0)
            {
                var pkgImport = new UAssetAPI.Import
                {
                    ClassPackage = FName.FromString(replAsset, "/Script/CoreUObject"),
                    ClassName = FName.FromString(replAsset, "Package"),
                    ObjectName = FName.FromString(replAsset, pkgPath),
                    OuterIndex = new FPackageIndex(0),
                    PackageName = FName.FromString(replAsset, "None")
                };
                pkgIdx = replAsset.Imports.Count;
                replAsset.Imports.Add(pkgImport);
            }

            for (int j = 0; j < replAsset.Imports.Count; j++)
            {
                var ri = replAsset.Imports[j];
                if (ri.OuterIndex?.Index == -(pkgIdx + 1)
                    && string.Equals(ri.ObjectName?.Value?.ToString(), layerName, StringComparison.Ordinal))
                    return j;
            }
            var objImport = new UAssetAPI.Import
            {
                ClassPackage = FName.FromString(replAsset, imp.ClassPackage?.Value?.ToString() ?? "/Script/CoreUObject"),
                ClassName = FName.FromString(replAsset, imp.ClassName?.Value?.ToString() ?? "Object"),
                ObjectName = FName.FromString(replAsset, layerName),
                OuterIndex = FPackageIndex.FromImport(pkgIdx),
                PackageName = FName.FromString(replAsset, imp.PackageName?.Value?.ToString() ?? "None")
            };
            int objIdx = replAsset.Imports.Count;
            replAsset.Imports.Add(objImport);
            return objIdx;
        }
        return -1;
    }

    private static bool CloneWindowWithScalars(
        StructPropertyData origW, StructPropertyData template, int layerIdx,
        out StructPropertyData? newW, out string err)
    {
        newW = null;
        err = "";
        var clone = ClonePropSameAsset(template) as StructPropertyData;
        if (clone == null) { err = "template clone was not a struct"; return false; }

        var collected = new Dictionary<string, object>(StringComparer.Ordinal);
        CollectScalars(origW, "", collected);
        var (applied, missing) = ApplyScalars(clone, "", collected);
        if (collected.Count == 0 || missing > 0 || applied != collected.Count)
        {
            err = $"scalar mismatch (collected={collected.Count} applied={applied} missing={missing})";
            return false;
        }

        var layer = clone.Value?.OfType<ObjectPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Layer");
        if (layer == null) { err = "cloned window has no m_Layer property"; return false; }
        layer.Value = FPackageIndex.FromImport(layerIdx);
        newW = clone;
        return true;
    }

    private static readonly System.Reflection.MethodInfo _memberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    private static PropertyData ClonePropSameAsset(PropertyData src)
    {
        var clone = (PropertyData)_memberwiseCloneMethod.Invoke(src, null)!;
        if (clone is StructPropertyData ss && ss.Value != null)
        {
            var list = new List<PropertyData>(ss.Value.Count);
            foreach (var c in ss.Value) list.Add(ClonePropSameAsset(c));
            ss.Value = list;
        }
        else if (clone is ArrayPropertyData aa && aa.Value != null)
        {
            var arr = new PropertyData[aa.Value.Length];
            for (int i = 0; i < aa.Value.Length; i++) arr[i] = ClonePropSameAsset(aa.Value[i]);
            aa.Value = arr;
        }
        else if (clone is MapPropertyData mm && mm.Value != null)
        {
            var tm = new TMap<PropertyData, PropertyData>();
            foreach (var kvp in mm.Value)
                tm.Add(ClonePropSameAsset(kvp.Key), ClonePropSameAsset(kvp.Value));
            mm.Value = tm;
        }
        return clone;
    }

    private static void CollectScalars(PropertyData p, string path, Dictionary<string, object> dict)
    {
        if (p is StructPropertyData sp && sp.Value != null)
        {
            foreach (var c in sp.Value)
            {
                string childPath = path.Length == 0
                    ? (c.Name?.Value?.ToString() ?? "")
                    : path + "/" + (c.Name?.Value?.ToString() ?? "");
                CollectScalars(c, childPath, dict);
            }
        }
        else if (p is ArrayPropertyData ap && ap.Value != null)
        {
            for (int i = 0; i < ap.Value.Length; i++)
                CollectScalars(ap.Value[i], $"{path}[{i}]", dict);
        }
        else if (p is FloatPropertyData fp) dict[path] = fp.Value;
        else if (p is IntPropertyData ip) dict[path] = ip.Value;
        else if (p is BytePropertyData bp) dict[path] = bp.Value;
    }

    private static (int applied, int missing) ApplyScalars(PropertyData p, string path, Dictionary<string, object> dict)
    {
        if (p is StructPropertyData sp && sp.Value != null)
        {
            int a = 0, m = 0;
            foreach (var c in sp.Value)
            {
                string childPath = path.Length == 0
                    ? (c.Name?.Value?.ToString() ?? "")
                    : path + "/" + (c.Name?.Value?.ToString() ?? "");
                var r = ApplyScalars(c, childPath, dict);
                a += r.applied;
                m += r.missing;
            }
            return (a, m);
        }
        if (p is ArrayPropertyData ap && ap.Value != null)
        {
            int a = 0, m = 0;
            for (int i = 0; i < ap.Value.Length; i++)
            {
                var r = ApplyScalars(ap.Value[i], $"{path}[{i}]", dict);
                a += r.applied;
                m += r.missing;
            }
            return (a, m);
        }
        if (p is FloatPropertyData fp && dict.TryGetValue(path, out var vf))
        {
            if (vf is float fv) { fp.Value = fv; return (1, 0); }
            return (0, 1);
        }
        if (p is IntPropertyData ip && dict.TryGetValue(path, out var vi))
        {
            if (vi is int iv) { ip.Value = iv; return (1, 0); }
            return (0, 1);
        }
        if (p is BytePropertyData bp && dict.TryGetValue(path, out var vb))
        {
            if (vb is byte bv) { bp.Value = bv; return (1, 0); }
            return (0, 1);
        }
        return (0, 0);
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
        NoteAnimRef(animPath);
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

        var errCount = ErrorLog.Writes - _errBaseline;
        txtResult.Text = errCount > 0
            ? $"{pakName} ({changeCount} changes, {sizeStr}) - {errCount} warning(s), see error.log"
            : $"{pakName} ({changeCount} changes, {sizeStr})";

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
