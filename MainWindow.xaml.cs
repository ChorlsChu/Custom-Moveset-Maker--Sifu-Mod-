using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using SifuMovesetEditor;
using SifuMovesetEditor.Setup;
using SifuMovesetEditor.Export;
using SifuMovesetEditor.Import;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SifuMovesetEditor;

public static class ErrorLog
{
    private static readonly string LogPath = Path.Combine(
        Directory.GetCurrentDirectory(), "error.log");

    public static void Init()
    {
        try { File.WriteAllText(LogPath, ""); } catch { }
    }

    public static void Write(string tag, Exception ex)
    {
        try
        {
            var msg = $"[{DateTime.Now:HH:mm:ss}] [{tag}] {ex}\n";
            File.AppendAllText(LogPath, msg);
        }
        catch { }
    }
}

public partial class MainWindow : Window
{
    private AnimationParser _parser = new();
    private List<MoveInfo> _allMoves = [];
    private List<MoveInfo> _allLocomotion = [];
    private string _settingsPath;
    private string _contentPath = "";
    private string _outputPath = "";
    private bool _initialized = false;
    private ComboGraph? _comboGraph;
    private Dictionary<int, Point> _nodePositions = new();
    private const double NODE_WIDTH = 140;
    private const double NODE_HEIGHT = 32;
    private const double INPUT_HEIGHT = 22;
    private const double H_SPACING = 80;
    private const double V_SPACING = 20;

    private bool _isPanning;
    private Point _panStart;
    private bool _keyW, _keyA, _keyS, _keyD;
    private DispatcherTimer _panTimer;
    private DateTime _lastPanTick = DateTime.UtcNow;
    private readonly DispatcherTimer _searchDebounceTimer;

    private readonly HashSet<string> _expandedEnemies = new();
    private readonly Dictionary<string, HashSet<string>> _expandedWeapons = new();

    private Border? _selectedMoveBorder;
    private int _selectedNodeId = -1;
    private int _lastClickedNodeId = -1;
    private DateTime _lastClickTime = DateTime.MinValue;
    private Popup? _dragPopup;

    private ComboGraph? _vanillaGraph;
    private ComboGraph? _moddedGraph;
    private Dictionary<int, (string vanilla, string modded)> _nodeDiffs = new();
    private bool _isModLoaded = false;
    private bool _isResetMode = false;
    private ComboGraph? _originalVanillaComboGraph;
    private string _activeStance = "MainChar";
    private List<MoveInfo> _comboTreeMoves = new();

    private readonly record struct StanceEntry(string MovementDb, string? Transition, string DisplayAnim);
    private readonly Dictionary<string, StanceEntry> _stanceMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MainChar"] = new(
            "DB/Movement/BaseMovementDB",
            "DB/Movement/Transition/BP_TransitionAnimRequest",
            "Game/Animations/MainChar/Locomotion/Man/Barehands/Moving/V1/Lockmove/North/MC_man_barehands_V1_north_tense"),
        ["FireDisciple"] = new(
            "DB/Movement/Archetypes/FireDisciple_BaseMovementDB",
            "DB/Movement/Transition/BP_TransitionAnimRequest_FireDisciple",
            "Game/Animations/FireDisciple/Locomotion/Barehands/V1/Lockmove/Tense/North/Disciple_barehands_V1_North_tense"),
        ["Grunt"] = new(
            "DB/Movement/Archetypes/Grunt_BaseMovementDB",
            "DB/Movement/Transition/BP_TransitionAnimRequest_Grunt",
            "Game/Animations/Grunt/Locomotion/Barehands/Moving/V1/Lockmove/North/Grunt_barehands_V1_North_tense"),
        ["FlashKick"] = new(
            "DB/Movement/Archetypes/FlashKick_BaseMovementDB",
            "DB/Movement/Transition/BP_TransitionAnimRequest_Flashkick",
            "Game/Animations/FlashKick/Locomotion/Barehands/Moving/V1/Lockmove/North/FlashKick_barehands_V1_North_tense"),
        ["BigGuy"] = new(
            "DB/AI/Archetypes/BigGuy/BigGuy_BaseMovementDB",
            "DB/Movement/Transition/BP_TransitionAnimRequest_BigGuy",
            "Game/Animations/BigGuy/Locomotion/Barehands/Moving/V1/Lockmove/South/BigGuy_barehands_V1_south_tense"),
        ["BodyGuard"] = new(
            "DB/Movement/Archetypes/Bodyguard_MovementDB",
            "DB/Movement/Transition/BP_TransitionAnimRequest_Bodyguard",
            "Game/Animations/BodyGuard/Locomotion/Barehands/Moving/V1/LockMove/North/BodyGuard_barehands_V1_North_tense"),
        ["Fajar"] = new(
            "Animations/Fajar/Fajar_BaseMovementDB",
            null,
            "Game/Animations/Fajar/Locomotion/Barehands/Moving/V0/Fajar_barehands_V0_FL_north_tense"),
        ["Fengjie"] = new(
            "DB/Movement/Archetypes/Fengjie_BaseMovementDB",
            "DB/Movement/Transition/BP_TransitionAnimRequestFengjie",
            "Game/Animations/Fengjie/Locomotion/MeteorHammer/Moving/V1/Lockmove/North/Fengjie_MeteorHammer_V1_North_tense"),
        ["Kuroki"] = new(
            "DB/Movement/Archetypes/Kuroki_BaseMovementDB",
            null,
            "Game/Animations/Kuroki/Locomotion/TriStaff/Moving/V1/Lockmove/North/Kuroki_TriStaff_V1_North_tense"),
        ["Sean"] = new(
            "DB/Movement/Archetypes/SeanBarehands_BaseMovementDB",
            "DB/Movement/Transition/BP_TransitionAnimRequestSeanStaff",
            "Game/Animations/Sean/Locomotion/Barehands/Moving/V1/North/Sean_barehands_V1_north_tense"),
        ["Servant"] = new(
            "DB/Movement/Archetypes/Servant_BaseMovementDB",
            "DB/Movement/Transition/BP_TransitionAnimRequest_Servant",
            "Game/Animations/Servant/Locomotion/Barehands/Moving/V1/Lockmove/North/Servant_barehands_V1_North_tense"),
        ["Yang"] = new(
            "DB/Movement/Archetypes/Yang_BaseMovementDB",
            "DB/Movement/Transition/BP_TransitionAnimRequest_Yang",
            "Game/Animations/Yang/Locomotion/Moving/V1/Lockmove/North/Yang_barehands_V1_north_tense"),
        ["Juggernaut"] = new(
            "DB/AI/Archetypes/BigGuy/BigGuy_BaseMovementDB",
            "DB/Movement/Transition/BP_TransitionAnimRequest_BigGuy",
            "Game/Animations/BigGuy/Locomotion/Barehands/Moving/V1/Lockmove/South/BigGuy_barehands_V1_south_tense"),
    };

    public MainWindow()
    {
        InitializeComponent();
        _settingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounceTimer.Tick += (s, args) =>
        {
            _searchDebounceTimer.Stop();
            FilterMoves();
        };
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ErrorLog.Init();
        await webView.EnsureCoreWebView2Async(null);
        webView.CoreWebView2.WebMessageReceived += OnWebViewMessage;
        webView.NavigationCompleted += WebView_NavigationCompleted;

        var viewerPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "viewer", "index.html");
        webView.CoreWebView2.Navigate(new Uri(viewerPath).AbsoluteUri);

        cmbSpeed.SelectedIndex = 2;

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += MainWindow_PreviewKeyUp;

        _panTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _panTimer.Tick += PanTimer_Tick;
        _panTimer.Start();

        await LoadSettingsAsync();
        _initialized = true;
    }

    private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        ApplySavedSettingsToViewer();
    }

    private void ApplySavedSettingsToViewer()
    {
        if (webView?.CoreWebView2 == null) return;

        webView.CoreWebView2.ExecuteScriptAsync(
            $"window.setSkeletonVisible({(chkShowLines.IsChecked == true ? "true" : "false")})");

        if (_savedCameraPos != null && _savedCameraPos.Length == 3 &&
            _savedCameraTarget != null && _savedCameraTarget.Length == 3)
        {
            webView.CoreWebView2.ExecuteScriptAsync(
                $"window.setCameraState({_savedCameraPos[0]},{_savedCameraPos[1]},{_savedCameraPos[2]},{_savedCameraTarget[0]},{_savedCameraTarget[1]},{_savedCameraTarget[2]})");
        }
    }

    private async Task<Settings> LoadSettingsAsync()
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                var json = File.ReadAllText(_settingsPath);
                var settings = JsonConvert.DeserializeObject<Settings>(json);
                if (settings != null && !string.IsNullOrEmpty(settings.ContentPath))
                {
                    _contentPath = settings.ContentPath;
                    if (!string.IsNullOrEmpty(settings.OutputPath))
                        _outputPath = settings.OutputPath;

                    chkShowLines.IsChecked = settings.ShowLines;
                    _savedCameraPos = settings.CameraPosition;
                    _savedCameraTarget = settings.CameraTarget;

                    var detection = ContentDetector.Detect(_contentPath);
                    if (detection.IsValid)
                    {
                        await InitializeParserAsync();
                        return settings;
                    }

                    var localContent = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameContent");
                    var localDetection = ContentDetector.Detect(localContent);
                    if (localDetection.IsValid)
                    {
                        _contentPath = localContent;
                        await InitializeParserAsync();
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Error loading settings: {ex.Message}";
            }
        }

        return await ShowSetupWizardAsync();
    }

    private async Task<Settings> ShowSetupWizardAsync()
    {
        var wizard = new SetupWizard
        {
            Owner = this,
        };

        if (wizard.ShowDialog() == true && !string.IsNullOrEmpty(wizard.SelectedContentPath))
        {
            _contentPath = wizard.SelectedContentPath;
            SaveSettings();
            await InitializeParserAsync();
            return new Settings { ContentPath = _contentPath, OutputPath = _outputPath };
        }

        // User skipped or cancelled — app will run with limited functionality
        txtStatus.Text = "No game content loaded. Some features won't be available.";
        return new Settings { ContentPath = _contentPath };
    }

    private double[]? _savedCameraPos;
    private double[]? _savedCameraTarget;

    private void SaveSettings()
    {
        var settings = new Settings
        {
            ContentPath = _contentPath,
            OutputPath = _outputPath,
            ShowLines = chkShowLines.IsChecked == true,
            CameraPosition = _savedCameraPos,
            CameraTarget = _savedCameraTarget
        };
        File.WriteAllText(_settingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
    }

    private void UpdateLoading(string text, string detail)
    {
        loadingText.Text = text;
        loadingDetail.Text = detail;
    }

    private async Task InitializeParserAsync()
    {
        try
        {
            var contentPath = Path.Combine(_contentPath, "Content");

            UpdateLoading("Initializing parser...", _contentPath);
            txtStatus.Text = $"Loading from: {_contentPath}...";
            _parser.Initialize(_contentPath, contentPath);

            UpdateLoading("Scanning attack animations...", "Walking Animations/ directory...");
            _allMoves = await Task.Run(() => _parser.ScanAnimations());

            UpdateLoading("Scanning combo tree data...", "Reading attack data tables...");
            var usedPaths = await Task.Run(() => _parser.ScanUsedAnimations());
            foreach (var move in _allMoves)
                move.IsUsed = usedPaths.Contains(move.FullPath);

            UpdateLoading("Scanning get-up animations...", "Finding enemy get-up moves...");
            var getUpMoves = await Task.Run(() => _parser.ScanGetUpAnims());
            _allMoves.AddRange(getUpMoves);
            _allMoves = _allMoves.GroupBy(m => m.FullPath).Select(g => g.First()).ToList();

            UpdateLoading($"Validating animations (0/{_allMoves.Count})...", "");
            var totalMoves = _allMoves.Count;
            var movesSnapshot = _allMoves.ToList();
            await Task.Run(() =>
            {
                int validated = 0;
                foreach (var move in movesSnapshot)
                {
                    move.IsValid = _parser.ValidateAnimation(move.FullPath);
                    validated++;
                    if (validated % 20 == 0 || validated == totalMoves)
                        Dispatcher.BeginInvoke(() => UpdateLoading(
                            $"Validating animations ({validated}/{totalMoves})...", ""));
                }
            });

            UpdateLoading("Loading locomotion + building mappings...", "");
            var locoResult = await Task.Run(() =>
            {
                var loco = _parser.ScanStanceAnims();
                _parser.BuildAnimToDbMapping();
                return loco;
            });
            _allLocomotion = locoResult;

            UpdateLoading($"Building library ({_allMoves.Count} moves)...", "");
            BuildTree(_allMoves);
            PopulateCharacterDropdown();

            UpdateLoading("Loading combo graph...", "Parsing MainChar combo tree...");
            await LoadComboGraphAsync();

            txtStatus.Text = $"Loaded {_allMoves.Count} animations from {_contentPath}";
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Error: {ex.Message}";
            MessageBox.Show($"Failed to initialize:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            loadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Sifu Game Folder (NOT Content folder)",
            InitialDirectory = string.IsNullOrEmpty(_contentPath)
                ? @"C:\"
                : _contentPath
        };

        if (dialog.ShowDialog() == true)
        {
            var selectedPath = dialog.FolderName;

            if (selectedPath.EndsWith("\\Content", StringComparison.OrdinalIgnoreCase) ||
                selectedPath.EndsWith("/Content", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Please select the Sifu game folder, NOT the Content folder.\n\n" +
                    $"You selected: {selectedPath}\n\n" +
                    $"Select the parent folder instead:\n{Path.GetDirectoryName(selectedPath)}",
                    "Wrong Folder Selected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var contentDir = Path.Combine(selectedPath, "Content");
            if (!Directory.Exists(contentDir))
            {
                MessageBox.Show(
                    $"Could not find 'Content' folder inside:\n{selectedPath}\n\n" +
                    "Please select the folder that contains the Content directory.",
                    "Content Folder Not Found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _contentPath = selectedPath;
            SaveSettings();
            loadingOverlay.Visibility = Visibility.Visible;
            await InitializeParserAsync();
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        FilterMoves();
    }

    private void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        FilterMoves();
    }

    private void FilterMoves()
    {
        if (_allMoves == null || _allMoves.Count == 0) return;

        var searchText = txtSearch.Text.ToLower();
        var filterIndex = cmbFilter.SelectedIndex;

        var filtered = _allMoves.FindAll(m =>
        {
            bool matchesSearch = string.IsNullOrEmpty(searchText) ||
                                 m.DisplayName.ToLower().Contains(searchText) ||
                                 m.FullPath.ToLower().Contains(searchText) ||
                                 m.Character.ToLower().Contains(searchText);

            bool matchesFilter = filterIndex switch
            {
                0 => true,
                1 => m.Character == "MainChar",
                2 => m.Character == "Grunt",
                3 => m.Character == "FireDisciple",
                4 => m.Character == "FlashKick",
                5 => m.Character == "BigGuy",
                6 => m.Character == "BodyGuard",
                7 => m.Character == "Fajar",
                8 => m.Character == "Fengjie",
                9 => m.Character == "Kuroki",
                10 => m.Character == "Sean",
                11 => m.Character == "Yang",
                12 => m.Character == "Sifu",
                13 => m.Character == "Servant",
                14 => m.Character is "Fajar" or "Sean" or "Kuroki" or "Yang" or "Fengjie",
                _ => true
            };

            return matchesSearch && matchesFilter;
        });

        var vanillaMoves = filtered.Where(m => m.IsUsed && (chkShowFailed.IsChecked == true || m.IsValid) && !string.IsNullOrWhiteSpace(m.DisplayName)).ToList();
        var unusedMoves = filtered.Where(m => !m.IsUsed && (chkShowFailed.IsChecked == true || m.IsValid) && !string.IsNullOrWhiteSpace(m.DisplayName)).ToList();

        bool useAccordion = string.IsNullOrEmpty(searchText) && filterIndex == 0;

        if (useAccordion)
        {
            listVanilla.ItemsSource = BuildAccordionList(vanillaMoves);
            listUnused.ItemsSource = BuildAccordionList(unusedMoves);
        }
        else
        {
            listVanilla.ItemsSource = vanillaMoves;
            listUnused.ItemsSource = unusedMoves;
        }

        var filteredLoco = _allLocomotion.FindAll(m =>
        {
            bool matchesSearch = string.IsNullOrEmpty(searchText) ||
                                 m.DisplayName.ToLower().Contains(searchText) ||
                                 m.Character.ToLower().Contains(searchText);
            return matchesSearch;
        });
        listLoco.ItemsSource = filteredLoco;

        tabHeaderVanilla.Text = $"Vanilla ({vanillaMoves.Count})";
        tabHeaderUnused.Text = $"Unused ({unusedMoves.Count})";
        tabHeaderLoco.Text = $"Locomotion ({filteredLoco.Count})";
        txtMoveCount.Text = $"{vanillaMoves.Count} vanilla / {unusedMoves.Count} unused / {filteredLoco.Count} locomotion";
    }

    private List<object> BuildAccordionList(List<MoveInfo> moves)
    {
        var result = new List<object>();

        if (_comboTreeMoves.Count > 0)
        {
            var mainCharExpanded = _expandedEnemies.Contains("MainChar");
            result.Add(new GroupHeader
            {
                Name = "MainChar",
                Level = 1,
                Count = _comboTreeMoves.Count,
                Subtitle = "Default moves from your combo graph",
                IsExpanded = mainCharExpanded
            });

            if (mainCharExpanded)
            {
                var categoryGroups = _comboTreeMoves
                    .GroupBy(m => string.IsNullOrEmpty(m.Category) ? "Other" : m.Category)
                    .OrderBy(g => g.Key);

                foreach (var catGroup in categoryGroups)
                {
                    var catKey = $"MainChar|{catGroup.Key}";
                    var catExpanded = _expandedWeapons.TryGetValue("MainChar", out var wSet)
                        && wSet.Contains(catGroup.Key);

                    result.Add(new GroupHeader
                    {
                        Name = catGroup.Key,
                        Level = 2,
                        Count = catGroup.Count(),
                        IsExpanded = catExpanded,
                        ParentName = "MainChar"
                    });

                    if (catExpanded)
                    {
                        result.AddRange(catGroup.Cast<object>());
                    }
                }
            }
        }

        var enemyGroups = moves
            .GroupBy(m => m.Character)
            .OrderBy(g => g.Key);

        foreach (var enemyGroup in enemyGroups)
        {
            var enemyExpanded = _expandedEnemies.Contains(enemyGroup.Key);

            result.Add(new GroupHeader
            {
                Name = enemyGroup.Key,
                Level = 1,
                Count = enemyGroup.Count(),
                Subtitle = "",
                IsExpanded = enemyExpanded
            });

            if (!enemyExpanded) continue;

            var weaponGroups = enemyGroup
                .GroupBy(m => m.WeaponType)
                .OrderBy(g => g.Key);

            foreach (var weaponGroup in weaponGroups)
            {
                var weaponKey = $"{enemyGroup.Key}|{weaponGroup.Key}";
                var weaponExpanded = _expandedWeapons.TryGetValue(enemyGroup.Key, out var wSet)
                    && wSet.Contains(weaponGroup.Key);

                result.Add(new GroupHeader
                {
                    Name = weaponGroup.Key,
                    Level = 2,
                    Count = weaponGroup.Count(),
                    IsExpanded = weaponExpanded,
                    ParentName = enemyGroup.Key
                });

                if (weaponExpanded)
                {
                    result.AddRange(weaponGroup.Cast<object>());
                }
            }
        }
        return result;
    }

    private void BuildTree(List<MoveInfo> moves)
    {
        var vanillaMoves = moves.Where(m => m.IsUsed && !string.IsNullOrWhiteSpace(m.DisplayName)).ToList();
        var unusedMoves = moves.Where(m => !m.IsUsed && !string.IsNullOrWhiteSpace(m.DisplayName)).ToList();

        listVanilla.ItemsSource = BuildAccordionList(vanillaMoves);
        listUnused.ItemsSource = BuildAccordionList(unusedMoves);

        listLoco.ItemsSource = _allLocomotion;

        tabHeaderVanilla.Text = $"Vanilla ({vanillaMoves.Count})";
        tabHeaderUnused.Text = $"Unused ({unusedMoves.Count})";
        tabHeaderLoco.Text = $"Locomotion ({_allLocomotion.Count})";
        txtMoveCount.Text = $"{vanillaMoves.Count} vanilla / {unusedMoves.Count} unused / {_allLocomotion.Count} locomotion";
    }

    private static List<object> BuildGroupedList(List<MoveInfo> moves)
    {
        var result = new List<object>();
        var grouped = moves
            .GroupBy(m => m.Character)
            .OrderBy(g => g.Key);

        foreach (var charGroup in grouped)
        {
            var charMoves = charGroup.ToList();
            result.Add(new GroupHeader { Name = charGroup.Key, Level = 1, Count = charMoves.Count });

            var weaponGroups = charMoves
                .GroupBy(m => m.WeaponType)
                .OrderBy(g => g.Key);

            foreach (var weaponGroup in weaponGroups)
            {
                var weaponMoves = weaponGroup.ToList();
                result.Add(new GroupHeader { Name = weaponGroup.Key, Level = 2, Count = weaponMoves.Count });
                result.AddRange(weaponMoves);
            }
        }

        return result;
    }

    private static readonly SolidColorBrush SelectedCardBg = new(Color.FromRgb(0x45, 0x47, 0x5a));
    private static readonly SolidColorBrush SelectedCardBorder = new(Color.FromRgb(0x89, 0xb4, 0xfa));
    private static readonly SolidColorBrush DefaultCardBg = new(Color.FromRgb(0x31, 0x32, 0x44));
    private static readonly SolidColorBrush DefaultCardBorder = Brushes.Transparent;

    private static readonly SolidColorBrush SelectedNodeBorderBrush = new(Color.FromRgb(0x89, 0xb4, 0xfa));
    private static readonly SolidColorBrush SelectedNodeBg = new(Color.FromArgb(0x80, 0x89, 0xb4, 0xfa));
    private static readonly SolidColorBrush SelectedGreenBorder = new(Color.FromRgb(0xa6, 0xe3, 0xa1));
    private static readonly SolidColorBrush SelectedGreenBg = new(Color.FromArgb(0x80, 0xa6, 0xe3, 0xa1));
    private static readonly SolidColorBrush SelectedOrangeBorder = new(Color.FromRgb(0xfa, 0xb3, 0x87));
    private static readonly SolidColorBrush SelectedOrangeBg = new(Color.FromArgb(0x80, 0xfa, 0xb3, 0x87));

    private void SelectMoveCard(Border border)
    {
        if (_selectedMoveBorder != null && _selectedMoveBorder != border)
        {
            _selectedMoveBorder.Background = DefaultCardBg;
            _selectedMoveBorder.BorderBrush = DefaultCardBorder;
        }
        _selectedMoveBorder = border;
        border.Background = SelectedCardBg;
        border.BorderBrush = SelectedCardBorder;
    }

    private void ClearMoveSelection()
    {
        if (_selectedMoveBorder != null)
        {
            _selectedMoveBorder.Background = DefaultCardBg;
            _selectedMoveBorder.BorderBrush = DefaultCardBorder;
            _selectedMoveBorder = null;
        }
    }

    private void ClearNodeSelection()
    {
        if (_selectedNodeId >= 0 && _comboGraph != null)
        {
            var prev = _comboGraph.Nodes.FirstOrDefault(n => n.Id == _selectedNodeId);
            if (prev != null)
            {
                var prevBorder = comboCanvas.Children
                    .OfType<Border>()
                    .FirstOrDefault(b => b.Tag is ComboNode cn && cn.Id == _selectedNodeId);
                if (prevBorder != null)
                {
                    prevBorder.BorderBrush = GetNodeColor(prev);
                    prevBorder.Background = GetNodeBackground(prev);
                }
            }
            _selectedNodeId = -1;
        }
    }

    private void SelectNode(Border border, ComboNode node)
    {
        ClearNodeSelection();
        _selectedNodeId = node.Id;
        var nodeColor = GetNodeColor(node);
        var color = nodeColor.Color;
        if (color.R == 0xa6 && color.G == 0xe3 && color.B == 0xa1)
        {
            border.BorderBrush = SelectedGreenBorder;
            border.Background = SelectedGreenBg;
        }
        else if (color.R == 0xfa && color.G == 0xb3 && color.B == 0x87)
        {
            border.BorderBrush = SelectedOrangeBorder;
            border.Background = SelectedOrangeBg;
        }
        else
        {
            border.BorderBrush = SelectedNodeBorderBrush;
            border.Background = SelectedNodeBg;
        }
    }

    private async void MoveCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is MoveInfo move)
        {
            ClearNodeSelection();
            SelectMoveCard(border);

            txtStatus.Text = $"Loading: {move.DisplayName}...";
            txtDisplayName.Text = "Display: -";
            ShowPreviewOverlay();

            if (!string.IsNullOrEmpty(move.Character))
                await LoadMeshAsync(move.Character);

            await LoadAnimationAsync(move.FullPath);
        }
    }

    private void MoveCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            sender is Border border && border.Tag is MoveInfo move)
        {
            ShowDragVisual(move);
            var data = new DataObject(typeof(MoveInfo), move);
            border.GiveFeedback += OnGiveFeedback;
            try
            {
                DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
            }
            finally
            {
                border.GiveFeedback -= OnGiveFeedback;
                HideDragVisual();
            }
        }
    }

    private void ShowDragVisual(MoveInfo move)
    {
        var visual = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Child = new TextBlock
            {
                Text = move.DisplayNameClean,
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        };

        _dragPopup = new Popup
        {
            Child = visual,
            AllowsTransparency = true,
            Placement = PlacementMode.Absolute,
            IsOpen = true,
            StaysOpen = true
        };
    }

    private void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (_dragPopup == null) return;
        var pos = Mouse.GetPosition(null);
        _dragPopup.PlacementRectangle = new Rect(pos.X + 12, pos.Y + 12, 0, 0);
    }

    private void HideDragVisual()
    {
        if (_dragPopup != null)
        {
            _dragPopup.IsOpen = false;
            _dragPopup = null;
        }
    }

    private void EnemyHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is GroupHeader header)
        {
            if (_expandedEnemies.Contains(header.Name))
                _expandedEnemies.Remove(header.Name);
            else
                _expandedEnemies.Add(header.Name);
            FilterMoves();
        }
    }

    private void WeaponHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is GroupHeader header)
        {
            var enemyName = header.ParentName;
            if (string.IsNullOrEmpty(enemyName)) return;

            if (!_expandedWeapons.TryGetValue(enemyName, out var weapons))
            {
                weapons = new HashSet<string>();
                _expandedWeapons[enemyName] = weapons;
            }

            if (weapons.Contains(header.Name))
                weapons.Remove(header.Name);
            else
                weapons.Add(header.Name);
            FilterMoves();
        }
    }

    private async System.Threading.Tasks.Task LoadAnimationAsync(string gamePath)
    {
        try
        {
            var data = await System.Threading.Tasks.Task.Run(() =>
                _parser.LoadAnimation(gamePath));

            if (data == null)
            {
                var nullMsg = $"Animation returned no data for: {gamePath}";
                txtStatus.Text = nullMsg;
                ErrorLog.Write("ANIMATION", new Exception(nullMsg));

                var failedMove = _allMoves.FirstOrDefault(m => m.FullPath == gamePath);
                if (failedMove != null)
                {
                    failedMove.IsValid = false;
                    FilterMoves();
                }

                HidePreviewOverlay();
                var escapedNull = nullMsg.Replace("'", "\\'");
                await webView.CoreWebView2.ExecuteScriptAsync($"window.showError('{escapedNull}')");
                return;
            }

            var json = _parser.ToJson(data);

            // Update info panels
            txtAnimName.Text = $"Name: {data.animation.name}";
            txtAnimFrames.Text = $"Frames: {data.animation.numFrames}";
            txtAnimDuration.Text = $"Duration: {data.animation.duration:F2}s";
            txtAnimFPS.Text = $"FPS: {data.animation.fps}";
            txtBoneCount.Text = $"Bones: {data.skeleton.bones.Length}";
            txtFilePath.Text = $"Path: {gamePath}";
            txtFrameInfo.Text = $"Frame: 0 / {data.animation.numFrames}";
            txtTimeInfo.Text = $"0.00s / {data.animation.duration:F2}s";

            // Send to WebView
            await webView.CoreWebView2.ExecuteScriptAsync(
                $"window.loadAnimation({json})");

            HidePreviewOverlay();
            txtStatus.Text = $"Loaded: {data.animation.name} ({data.animation.numFrames} frames, {data.animation.tracks.Length} tracks)";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ANIMATION ERROR] {ex}");
            ErrorLog.Write("ANIMATION", ex);
            HidePreviewOverlay();
            txtStatus.Text = $"Error: {ex.Message}";
            var escaped = ex.Message.Replace("'", "\\'").Replace("\\", "\\\\").Replace("\n", " ").Replace("\r", "");
            await webView.CoreWebView2.ExecuteScriptAsync($"window.showError('{escaped}')");
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        webView.CoreWebView2?.ExecuteScriptAsync("window.play()");
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        webView.CoreWebView2?.ExecuteScriptAsync("window.stop()");
    }

    private void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (webView?.CoreWebView2 == null) return;
        if (cmbSpeed.SelectedItem is ComboBoxItem item && item.Tag is string speedStr)
        {
            if (float.TryParse(speedStr, out float speed))
            {
                webView.CoreWebView2.ExecuteScriptAsync(
                    $"window.setPlaybackSpeed({speed})");
            }
        }
    }

    private void Timeline_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sliderTimeline.IsMouseCaptureWithin)
        {
            var progress = sliderTimeline.Value / 100.0;
            webView.CoreWebView2?.ExecuteScriptAsync(
                $"window.seekTo({progress})");
        }
    }

    private void OnWebViewMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var message = args.TryGetWebMessageAsString();
            var data = JsonConvert.DeserializeObject<WebViewMessage>(message);

            if (data == null) return;

            Dispatcher.Invoke(() =>
            {
                switch (data.action)
                {
                    case "animationLoaded":
                        txtStatus.Text = $"Playing: {data.name} ({data.trackCount} tracks)";
                        break;

                    case "timeUpdate":
                        if (data.duration > 0)
                        {
                            sliderTimeline.Value = (data.progress ?? 0) * 100;
                            txtFrameInfo.Text = $"Frame: {(int)((data.progress ?? 0) * (data.numFrames ?? 0))} / {data.numFrames ?? 0}";
                            txtTimeInfo.Text = $"{data.time:F2}s / {data.duration:F2}s";
                        }
                        break;

                    case "animationError":
                        txtFrameInfo.Text = "Frame: 0 / 0";
                        txtTimeInfo.Text = "0.00s / 0.00s";
                        sliderTimeline.Value = 0;
                        break;
                }
            });
        }
        catch { }
    }

    private void PopulateCharacterDropdown()
    {
        cmbCharacter.Items.Clear();
        cmbCharacter.Items.Add(new ComboBoxItem { Content = "None", Tag = "" });
        foreach (var character in _parser.GetAvailableCharacters())
        {
            cmbCharacter.Items.Add(new ComboBoxItem { Content = character, Tag = character });
        }
        cmbCharacter.SelectedIndex = 0;
    }

    private void ChkShowLines_Changed(object sender, RoutedEventArgs e)
    {
        if (webView?.CoreWebView2 != null)
            webView.CoreWebView2.ExecuteScriptAsync(
                $"window.setSkeletonVisible({(chkShowLines.IsChecked == true ? "true" : "false")})");
        if (_initialized)
            SaveSettings();
    }

    private void ShowFailed_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            FilterMoves();
    }

    private async void Character_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (cmbCharacter.SelectedItem is not ComboBoxItem item) return;

        var character = item.Tag?.ToString() ?? "";
        if (string.IsNullOrEmpty(character))
        {
            await webView.CoreWebView2.ExecuteScriptAsync("window.clearMesh()");
            return;
        }

        txtStatus.Text = $"Loading mesh for {character}...";
        await LoadMeshAsync(character);
    }

    private async System.Threading.Tasks.Task LoadMeshAsync(string character)
    {
        try
        {
            var meshData = await System.Threading.Tasks.Task.Run(() => _parser.LoadMesh(character));
            if (meshData == null)
            {
                txtStatus.Text = $"No mesh found for {character}";
                return;
            }

            var json = _parser.ToJson(meshData);
            var escapedChar = character.Replace("'", "\\'");
            await webView.CoreWebView2.ExecuteScriptAsync($"window.loadMesh({json}, '{escapedChar}')");
            txtStatus.Text = $"Loaded mesh for {character} ({meshData.positions.Length / 3} vertices)";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MESH ERROR] {ex}");
            ErrorLog.Write("MESH", ex);
            txtStatus.Text = $"Error loading mesh: {ex.Message}";
        }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_comboGraph == null)
        {
            txtStatus.Text = "No combo graph loaded";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Sifu Edit Files (*.sifu-edit)|*.sifu-edit|JSON Files (*.json)|*.json",
            Title = "Save Project",
            FileName = "CustomMoveset.sifu-edit"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var project = new EditorProject { Name = Path.GetFileNameWithoutExtension(dialog.FileName) };
                ProjectManager.Save(project, _comboGraph, dialog.FileName);
                txtStatus.Text = $"Saved project: {dialog.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save project:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void LoadProject_Click(object sender, RoutedEventArgs e)
    {
        if (_comboGraph == null)
        {
            txtStatus.Text = "No combo graph loaded";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Sifu Edit Files (*.sifu-edit)|*.sifu-edit|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            Title = "Open Project"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var (project, swaps) = ProjectManager.Load(dialog.FileName);

                foreach (var node in _comboGraph.Nodes)
                {
                    if (swaps.TryGetValue(node.Id.ToString(), out string? animPath) && !string.IsNullOrEmpty(animPath))
                    {
                        node.AnimPath = animPath;
                        var matchedMove = _allMoves.FirstOrDefault(m => m.FullPath == animPath);
                        if (matchedMove != null)
                            node.DisplayName = matchedMove.DisplayName;
                    }
                }

                RenderComboGraph();
                txtStatus.Text = $"Loaded project: {project.Name} ({swaps.Count} swaps)";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load project:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ExportPak_Click(object sender, RoutedEventArgs e)
    {
        if (_comboGraph == null)
        {
            txtStatus.Text = "No combo graph loaded";
            return;
        }

        var contentDir = Path.Combine(_contentPath, "Content");

        ErrorLog.Write("EXPORT", new Exception($"[_contentPath] {_contentPath}"));
        ErrorLog.Write("EXPORT", new Exception($"[contentDir] {contentDir}"));

        // Auto-detect stance from the Combat Stance node's AnimPath
        var stanceNode = _comboGraph.Nodes.FirstOrDefault(n => n.Name == "MainChar_Stance");
        var detectedStance = "MainChar";
        if (stanceNode != null && !string.IsNullOrEmpty(stanceNode.AnimPath))
        {
            foreach (var kvp in _stanceMap)
            {
                if (kvp.Key == "MainChar") continue;
                if (kvp.Value.DisplayAnim != null && stanceNode.AnimPath == kvp.Value.DisplayAnim)
                {
                    detectedStance = kvp.Key;
                    break;
                }
            }
        }
        ErrorLog.Write("EXPORT", new Exception($"[detectedStance] {detectedStance} (node AnimPath={stanceNode?.AnimPath})"));

        var stanceChanged = detectedStance != "MainChar";
        var modified = _comboGraph.Nodes
            .Where(n => !n.IsRoot && !string.IsNullOrEmpty(n.AnimPath)
                && (n.AnimPath != n.DefaultAnimPath
                    || (!string.IsNullOrEmpty(n.VanillaAnimPath) && n.AnimPath != n.VanillaAnimPath)))
            .ToList();

        if (modified.Count == 0 && !stanceChanged)
        {
            txtStatus.Text = "No changes to export. Drag animations onto combo nodes first.";
            return;
        }

        string? charTransitionPath = null;
        string? charBaseMovementDBPath = null;
        if (stanceChanged && _stanceMap.TryGetValue(detectedStance, out var stanceEntry))
        {
            charTransitionPath = stanceEntry.Transition;
            charBaseMovementDBPath = stanceEntry.MovementDb;
            ErrorLog.Write("EXPORT", new Exception($"[stance] charTransitionPath={charTransitionPath}, charBaseMovementDBPath={charBaseMovementDBPath}"));
        }

        var referenceModDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Template");

        var dialog = new ExportDialog(
            modified,
            _contentPath,
            _outputPath,
            _parser.AnimToDbPath,
            detectedStance,
            charTransitionPath,
            charBaseMovementDBPath,
            referenceModDir)
        {
            Owner = this,
        };

        dialog.ShowDialog();
    }

    private async void ImportMod_Click(object sender, RoutedEventArgs e)
    {
        var fileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Pak Files (*.pak)|*.pak|All Files (*.*)|*.*",
            Title = "Import Moveset Mod Pak",
        };

        if (fileDialog.ShowDialog() != true) return;

        var pakPath = fileDialog.FileName;
        var importTempRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImportTemp");
        var stagingDir = Path.Combine(importTempRoot, "a", "b", "c");

        var importDialog = new Import.ImportDialog
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        importDialog.Show();

        try
        {
            var unrealPakPath = Setup.ContentExtractor.FindUnrealPak();
            if (unrealPakPath == null)
            {
                importDialog.ShowError("UnrealPak not found. Cannot extract mod pak.");
                return;
            }

            await Task.Run(() =>
            {
                void UI(Action a) => Dispatcher.Invoke(a);

                // Step 1: Clean and create staging dir
                UI(() => { importDialog.UpdateStep(1, "active"); importDialog.SetCurrentAction("Preparing temp folder..."); importDialog.SetProgress(5); });

                if (Directory.Exists(importTempRoot))
                    Directory.Delete(importTempRoot, true);
                Directory.CreateDirectory(importTempRoot);
                Directory.CreateDirectory(stagingDir);

                UI(() => importDialog.SetProgress(10));

                // Step 2: Copy pak to temp (never let UnrealPak touch the original)
                UI(() => { importDialog.SetCurrentAction("Copying pak file..."); importDialog.SetProgress(12); });

                var pakCopy = Path.Combine(importTempRoot, "original.pak");
                File.Copy(pakPath, pakCopy, true);
                var pakSize = new FileInfo(pakCopy).Length;
                ErrorLog.Write("IMPORT", new Exception($"Copied pak to temp ({pakSize} bytes): {pakCopy}"));

                UI(() => importDialog.SetProgress(15));

                // Step 3: Extract pak with CryptoKeys
                UI(() => { importDialog.SetCurrentAction("Extracting pak file..."); importDialog.SetProgress(18); });

                var unrealPakDir = Path.GetDirectoryName(unrealPakPath) ?? "";
                var cryptoKeysPath = Path.Combine(unrealPakDir, "Crypto.json");
                var hasCryptoKeys = File.Exists(cryptoKeysPath);

                var extractArgs = hasCryptoKeys
                    ? $"\"{pakCopy}\" -CryptoKeys=\"{cryptoKeysPath}\" -Extract \"{stagingDir}\""
                    : $"\"{pakCopy}\" -Extract \"{stagingDir}\"";

                ErrorLog.Write("IMPORT", new Exception($"UnrealPak args: {extractArgs}"));

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = unrealPakPath,
                    Arguments = extractArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null)
                    throw new Exception("Failed to start UnrealPak.");

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(60000);

                ErrorLog.Write("IMPORT", new Exception($"UnrealPak exit: {process.ExitCode}"));
                if (!string.IsNullOrWhiteSpace(stdout))
                    ErrorLog.Write("IMPORT", new Exception($"UnrealPak stdout: {stdout.Trim()}"));
                if (!string.IsNullOrWhiteSpace(stderr))
                    ErrorLog.Write("IMPORT", new Exception($"UnrealPak stderr: {stderr.Trim()}"));

                UI(() => importDialog.SetProgress(30));
                UI(() => { importDialog.UpdateStep(1, "done"); });

                // Step 2: Search for combo tree and stance files
                UI(() => { importDialog.UpdateStep(2, "active"); importDialog.SetCurrentAction("Searching for files..."); importDialog.SetProgress(35); });

                var allExtracted = Directory.GetFiles(importTempRoot, "*", SearchOption.AllDirectories);
                ErrorLog.Write("IMPORT", new Exception($"Extracted {allExtracted.Length} files to {importTempRoot}"));
                foreach (var f in allExtracted)
                    ErrorLog.Write("IMPORT", new Exception($"  {Path.GetRelativePath(importTempRoot, f)}"));

                var comboTreeFiles = Directory.GetFiles(importTempRoot, "MainChar_ComboTree.uasset", SearchOption.AllDirectories);
                bool hasComboTree = comboTreeFiles.Length > 0;
                string? comboTreeDir = null;
                if (hasComboTree)
                {
                    comboTreeDir = Path.GetDirectoryName(comboTreeFiles[0])!;
                    ErrorLog.Write("IMPORT", new Exception($"Found combo tree at: {comboTreeFiles[0]}"));
                }
                UI(() => { importDialog.UpdateStep(2, "done"); importDialog.SetProgress(40); });

                // Step 3: Detect stance from BP_TransitionAnimRequest
                UI(() => { importDialog.UpdateStep(3, "active"); importDialog.SetCurrentAction("Detecting stance..."); importDialog.SetProgress(42); });
                string? detectedStance = null;
                var transitionFiles = Directory.GetFiles(importTempRoot, "BP_TransitionAnimRequest.uasset", SearchOption.AllDirectories);
                if (transitionFiles.Length > 0)
                {
                    detectedStance = DetectStanceFromAsset(transitionFiles[0]);
                    ErrorLog.Write("IMPORT", new Exception($"Stance detection result: {detectedStance ?? "(none)"}"));
                }
                else
                {
                    var dbFiles = Directory.GetFiles(importTempRoot, "*_BaseMovementDB.uasset", SearchOption.AllDirectories);
                    foreach (var dbFile in dbFiles)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(dbFile);
                        var stanceName = fileName.Replace("_BaseMovementDB", "");
                        if (_stanceMap.ContainsKey(stanceName))
                        {
                            detectedStance = stanceName;
                            ErrorLog.Write("IMPORT", new Exception($"Stance detected from filename: {detectedStance}"));
                            break;
                        }
                    }
                }
                var detectedStanceLocal = detectedStance;
                UI(() => { importDialog.UpdateStep(3, "done"); importDialog.SetProgress(44); });

                if (!hasComboTree && detectedStance == null)
                    throw new Exception("This pak does not contain a combo tree or stance files.");

                ComboGraph? moddedGraph = null;
                Dictionary<int, (string vanilla, string modded)>? nodeDiffs = null;

                if (hasComboTree && comboTreeDir != null)
                {
                    // Step 4: Parse modded tree
                    UI(() => { importDialog.UpdateStep(4, "active"); importDialog.SetCurrentAction("Parsing modded animations..."); importDialog.SetProgress(45); });
                    moddedGraph = _parser.LoadModdedComboTree(comboTreeDir);
                    if (moddedGraph == null)
                        throw new Exception("Failed to parse modded combo tree.");
                    UI(() => { importDialog.UpdateStep(4, "done"); importDialog.SetProgress(70); });

                    // Step 5: Compare with vanilla
                    UI(() => { importDialog.UpdateStep(5, "active"); importDialog.SetCurrentAction("Comparing with vanilla..."); importDialog.SetProgress(75); });

                    var localModded = moddedGraph;
                    nodeDiffs = new Dictionary<int, (string, string)>();

                    UI(() =>
                    {
                        _moddedGraph = localModded;
                        _vanillaGraph = _originalVanillaComboGraph ?? _comboGraph;

                        var vanillaByName = _vanillaGraph?.Nodes
                            .Where(n => n.TreeIndex >= 0)
                            .GroupBy(n => n.Name)
                            .ToDictionary(g => g.Key, g => g.ToList()) ?? new();

                        var moddedNameIdx = new Dictionary<string, int>();
                        foreach (var modNode in localModded.Nodes.Where(n => n.TreeIndex >= 0))
                        {
                            if (!moddedNameIdx.ContainsKey(modNode.Name))
                                moddedNameIdx[modNode.Name] = 0;
                            int idx = moddedNameIdx[modNode.Name]++;

                            if (vanillaByName.TryGetValue(modNode.Name, out var vanillaNodes)
                                && idx < vanillaNodes.Count)
                            {
                                var vanillaNode = vanillaNodes[idx];
                                modNode.VanillaAnimPath = vanillaNode.AnimPath;
                                modNode.DisplayName = vanillaNode.DisplayName;
                                modNode.DirectionLabel = vanillaNode.DirectionLabel;
                                if (vanillaNode.AnimPath != modNode.AnimPath)
                                    nodeDiffs[modNode.Id] = (vanillaNode.AnimPath, modNode.AnimPath);
                            }
                            else
                            {
                                modNode.VanillaAnimPath = modNode.AnimPath;
                                nodeDiffs[modNode.Id] = ("", modNode.AnimPath);
                            }
                        }
                    });

                    UI(() => { importDialog.UpdateStep(5, "done"); importDialog.SetProgress(90); });
                }
                else
                {
                    UI(() => { importDialog.UpdateStep(4, "done"); importDialog.SetProgress(70); });
                    UI(() => { importDialog.UpdateStep(5, "done"); importDialog.SetProgress(90); });
                }

                // Step 6: Finalize
                UI(() => { importDialog.UpdateStep(6, "active"); importDialog.SetCurrentAction("Finalizing..."); importDialog.SetProgress(95); });

                var finalModdedGraph = moddedGraph;
                var finalNodeDiffs = nodeDiffs;
                bool isStanceOnly = !hasComboTree && detectedStanceLocal != null;

                UI(() =>
                {
                    if (finalModdedGraph != null)
                    {
                        _comboGraph = finalModdedGraph;
                        _isModLoaded = true;
                    }

                    if (detectedStanceLocal != null && _stanceMap.TryGetValue(detectedStanceLocal, out var stanceEntry))
                    {
                        var stanceNode = _comboGraph?.Nodes.FirstOrDefault(n => n.Name == "MainChar_Stance");
                        if (stanceNode != null)
                        {
                            stanceNode.AnimPath = stanceEntry.DisplayAnim;
                        }
                        _activeStance = detectedStanceLocal;
                        cmbStance.SelectedItem = detectedStanceLocal;
                    }

                    int changedCount = finalNodeDiffs?.Count ?? 0;
                    if (finalModdedGraph != null)
                    {
                        txtComboInfo.Text = $"MOD ({_activeStance}): {finalModdedGraph.WeaponName} ({finalModdedGraph.Nodes.Count} nodes, {changedCount} changed)";
                    }
                    else
                    {
                        txtComboInfo.Text = $"MOD ({_activeStance}): Stance only ({changedCount} nodes changed)";
                    }
                    LayoutComboGraph();
                    RenderComboGraph();
                });

                UI(() => { importDialog.UpdateStep(6, "done"); importDialog.SetProgress(100); });

                Thread.Sleep(200);

                int diffCount = finalNodeDiffs?.Count ?? 0;
                string weaponInfo = finalModdedGraph?.WeaponName ?? "N/A";
                var successMsg = isStanceOnly
                    ? $"Stance: {detectedStanceLocal}"
                    : detectedStanceLocal != null
                        ? $"Stance: {detectedStanceLocal}\n{diffCount} nodes changed"
                        : $"{diffCount} nodes changed";
                UI(() => importDialog.ShowSuccess(diffCount, weaponInfo, successMsg));
            });
        }
        catch (Exception ex)
        {
            ErrorLog.Write("IMPORT", ex);
            importDialog.ShowError(ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(importTempRoot)) Directory.Delete(importTempRoot, true); } catch { }
        }
    }

    private string? DetectStanceFromAsset(string uassetPath)
    {
        try
        {
            var asset = new UAsset(uassetPath, EngineVersion.VER_UE4_26);
            var importNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var imp in asset.Imports)
            {
                var name = imp.ObjectName.Value?.ToString();
                if (!string.IsNullOrEmpty(name))
                    importNames.Add(name);
            }

            string? bestStance = null;
            int bestScore = 0;

                foreach (var kvp in StanceGenerator.Stances)
            {
                int score = 0;
                var data = kvp.Value;
                var animNames = new[] { data.StartE, data.StartN, data.StartS, data.StopE, data.StopEFR, data.StopN, data.StopS };
                foreach (var anim in animNames)
                {
                    if (!string.IsNullOrEmpty(anim) && importNames.Contains(anim))
                        score++;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestStance = kvp.Key;
                }
            }

            if (bestStance != null && bestScore >= 2)
            {
                ErrorLog.Write("IMPORT", new Exception($"Stance detected: {bestStance} ({bestScore} matching imports)"));
                return bestStance;
            }

            foreach (var impName in importNames)
            {
                foreach (var kvp in _stanceMap)
                {
                    if (kvp.Key == "MainChar") continue;
                    if (kvp.Value.DisplayAnim != null && impName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        ErrorLog.Write("IMPORT", new Exception($"Stance detected from import name: {kvp.Key}"));
                        return kvp.Key;
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("IMPORT", new Exception($"Failed to detect stance from {uassetPath}: {ex.Message}"));
            return null;
        }
    }

    private static void DumpProperty(object prop, string name, string type, int depth)
    {
        var indent = new string(' ', depth);
        try
        {
            if (prop is UAssetAPI.PropertyTypes.Structs.StructPropertyData spd && spd.Value != null)
            {
                ErrorLog.Write("EXPORT", new Exception($"{indent}[Struct] {name} ({spd.Value.Count} props)"));
                foreach (var inner in spd.Value)
                    DumpProperty(inner, inner.Name?.Value?.ToString() ?? "?", inner.GetType().Name, depth + 2);
            }
            else if (prop is UAssetAPI.PropertyTypes.Objects.MapPropertyData mpd && mpd.Value != null)
            {
                ErrorLog.Write("EXPORT", new Exception($"{indent}[Map] {name} ({mpd.Value.Count} entries)"));
                int i = 0;
                foreach (var entry in mpd.Value)
                {
                    if (i >= 5) { ErrorLog.Write("EXPORT", new Exception($"{indent}  ... {mpd.Value.Count - 5} more")); break; }
                    DumpProperty(entry.Key, $"key[{i}]", entry.Key?.GetType()?.Name ?? "?", depth + 2);
                    DumpProperty(entry.Value, $"val[{i}]", entry.Value?.GetType()?.Name ?? "?", depth + 2);
                    i++;
                }
            }
            else
            {
                var valStr = prop.ToString() ?? "(null)";
                if (valStr.Length > 200) valStr = valStr.Substring(0, 200) + "...";
                ErrorLog.Write("EXPORT", new Exception($"{indent}[{type}] {name} = {valStr}"));
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", new Exception($"{indent}[{type}] {name} = ERR: {ex.Message}"));
        }
    }

    private static void PatchNodeAttacks(object nodeStruct, UAssetAPI.UAsset asset, Dictionary<string, string> swaps, ref int patched)
    {
        if (nodeStruct is UAssetAPI.PropertyTypes.Structs.StructPropertyData spd && spd.Value != null)
        {
            foreach (var prop in spd.Value)
            {
                if (prop.Name.Value.ToString() == "m_Attacks" && prop is UAssetAPI.PropertyTypes.Objects.NamePropertyData npd)
                {
                    var currentPath = AnimationParser.NormalizeAnimPath(npd.Value.Value.ToString());
                    ErrorLog.Write("PATCH", new Exception(
                        $"Level1 m_Attacks='{currentPath}', swapsHas={swaps.ContainsKey(currentPath)}, swapsCount={swaps.Count}"));
                    if (swaps.TryGetValue(currentPath, out var newPath))
                    {
                        npd.Value = new UAssetAPI.UnrealTypes.FName(asset, newPath);
                        patched++;
                    }
                }
                else if (prop is UAssetAPI.PropertyTypes.Structs.StructPropertyData innerSpd && innerSpd.Value != null)
                {
                    foreach (var innerProp in innerSpd.Value)
                    {
                        if (innerProp.Name.Value.ToString() == "m_Attacks" && innerProp is UAssetAPI.PropertyTypes.Objects.NamePropertyData innerNpd)
                        {
                            var currentPath = AnimationParser.NormalizeAnimPath(innerNpd.Value.Value.ToString());
                            ErrorLog.Write("PATCH", new Exception(
                                $"Level2 m_Attacks='{currentPath}', swapsHas={swaps.ContainsKey(currentPath)}, swapsCount={swaps.Count}"));
                            if (swaps.TryGetValue(currentPath, out var newPath))
                            {
                                innerNpd.Value = new UAssetAPI.UnrealTypes.FName(asset, newPath);
                                patched++;
                            }
                        }
                    }
                }
            }
        }
    }


    private async Task LoadComboGraphAsync()
    {
        try
        {
            _comboGraph = await Task.Run(() => _parser.LoadMainCharComboTree());
            if (_comboGraph == null)
            {
                txtComboInfo.Text = "No combo data";
                return;
            }

            if (_originalVanillaComboGraph == null)
                _originalVanillaComboGraph = _comboGraph;

            txtComboInfo.Text = $"MainChar - {_comboGraph.WeaponName} ({_comboGraph.Nodes.Count} nodes, {_comboGraph.Edges.Count} edges)";
            _comboTranslate.X = 0;
            _comboTranslate.Y = 0;
            LayoutComboGraph();
            RenderComboGraph();
            btnExport.IsEnabled = true;
            btnResetMode.Visibility = Visibility.Visible;

            cmbStance.Items.Clear();
            foreach (var key in _stanceMap.Keys)
                cmbStance.Items.Add(key);
            cmbStance.SelectedItem = _activeStance;
            cmbStance.Visibility = Visibility.Visible;

            _comboTreeMoves.Clear();
            foreach (var node in _comboGraph.Nodes.Where(n => !n.IsRoot && !string.IsNullOrEmpty(n.DefaultAnimPath)))
            {
                var match = _allMoves.FirstOrDefault(m => m.FullPath == node.DefaultAnimPath);
                if (match != null && !_comboTreeMoves.Contains(match))
                    _comboTreeMoves.Add(match);
            }
            if (_comboTreeMoves.Count > 0)
                FilterMoves();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("COMBO", ex);
            txtComboInfo.Text = $"Error: {ex.Message}";
        }
    }

    private void LayoutComboGraph()
    {
        if (_comboGraph == null) return;
        _nodePositions.Clear();

        foreach (var node in _comboGraph.Nodes)
            node.Depth = -1;

        var incoming = new Dictionary<int, int>();
        var outgoing = new Dictionary<int, List<int>>();
        foreach (var node in _comboGraph.Nodes)
        {
            incoming[node.Id] = 0;
            outgoing[node.Id] = new List<int>();
        }
        foreach (var edge in _comboGraph.Edges)
        {
            if (incoming.ContainsKey(edge.ToNodeId))
                incoming[edge.ToNodeId]++;
            if (outgoing.ContainsKey(edge.FromNodeId))
                outgoing[edge.FromNodeId].Add(edge.ToNodeId);
        }

        var queue = new Queue<int>();
        foreach (var node in _comboGraph.Nodes)
        {
            if (node.IsRoot || incoming[node.Id] == 0)
            {
                node.Depth = 0;
                queue.Enqueue(node.Id);
            }
        }

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            var current = _comboGraph.Nodes.FirstOrDefault(n => n.Id == currentId);
            if (current == null) continue;

            foreach (var targetId in outgoing[currentId])
            {
                var target = _comboGraph.Nodes.FirstOrDefault(n => n.Id == targetId);
                if (target != null && target.Depth < current.Depth + 1)
                {
                    target.Depth = current.Depth + 1;
                    queue.Enqueue(target.Id);
                }
            }
        }

        foreach (var node in _comboGraph.Nodes)
        {
            if (node.Depth < 0)
                node.Depth = 0;
        }

        var forwardDepth = new Dictionary<int, int>();
        foreach (var node in _comboGraph.Nodes.OrderByDescending(n => n.Depth))
        {
            var maxChild = 0;
            foreach (var childId in outgoing[node.Id])
            {
                if (forwardDepth.TryGetValue(childId, out var cd) && cd > maxChild)
                    maxChild = cd;
            }
            forwardDepth[node.Id] = maxChild + 1;
        }

        var maxDepth = _comboGraph.Nodes.Max(n => n.Depth);
        var columns = new List<List<ComboNode>>();
        for (int d = 0; d <= maxDepth; d++)
        {
            var col = _comboGraph.Nodes
                .Where(n => n.Depth == d)
                .OrderByDescending(n => forwardDepth.GetValueOrDefault(n.Id, 1))
                .ThenBy(n => n.DisplayName)
                .ToList();
            columns.Add(col);
        }

        double colWidth = NODE_WIDTH + H_SPACING;
        double rowHeight = NODE_HEIGHT + V_SPACING;

        for (int d = 0; d < columns.Count; d++)
        {
            var nodesInCol = columns[d];
            double x = 30 + d * colWidth;

            for (int i = 0; i < nodesInCol.Count; i++)
            {
                double y = 30 + i * rowHeight;
                _nodePositions[nodesInCol[i].Id] = new Point(x, y);
            }
        }
    }

    private void RenderComboGraph()
    {
        comboCanvas.Children.Clear();
        if (_comboGraph == null) return;

        var edgeColors = new Dictionary<string, SolidColorBrush>
        {
            ["LMB"] = new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1)),
            ["RMB"] = new SolidColorBrush(Color.FromRgb(0xf3, 0x8b, 0xa8)),
            ["RMB Hold"] = new SolidColorBrush(Color.FromRgb(0xf5, 0xc2, 0xe7)),
            ["S"] = new SolidColorBrush(Color.FromRgb(0xf9, 0xe2, 0xaf)),
            ["Shift"] = new SolidColorBrush(Color.FromRgb(0x89, 0xdc, 0xeb)),
            ["Q"] = new SolidColorBrush(Color.FromRgb(0xcb, 0xa6, 0xf7)),
        };

        foreach (var edge in _comboGraph.Edges)
        {
            if (!_nodePositions.TryGetValue(edge.FromNodeId, out var fromPos)) continue;
            if (!_nodePositions.TryGetValue(edge.ToNodeId, out var toPos)) continue;

            var fromRight = new Point(fromPos.X + NODE_WIDTH, fromPos.Y + NODE_HEIGHT / 2);
            var toLeft = new Point(toPos.X, toPos.Y + NODE_HEIGHT / 2);

            var color = edgeColors.TryGetValue(edge.InputName, out var c) ? c :
                new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa));

            var line = new System.Windows.Shapes.Line
            {
                X1 = fromRight.X, Y1 = fromRight.Y,
                X2 = toLeft.X, Y2 = toLeft.Y,
                Stroke = color,
                StrokeThickness = 2
            };
            comboCanvas.Children.Add(line);

            var arrowSize = 6;
            var dx = toLeft.X - fromRight.X;
            var dy = toLeft.Y - fromRight.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 0)
            {
                var ux = dx / len;
                var uy = dy / len;
                var arrowTip = toLeft;
                var arrowP1 = new Point(arrowTip.X - ux * arrowSize + uy * arrowSize / 2, arrowTip.Y - uy * arrowSize - ux * arrowSize / 2);
                var arrowP2 = new Point(arrowTip.X - ux * arrowSize - uy * arrowSize / 2, arrowTip.Y - uy * arrowSize + ux * arrowSize / 2);

                var arrow = new System.Windows.Shapes.Polygon
                {
                    Fill = color,
                    Points = new PointCollection { arrowTip, arrowP1, arrowP2 }
                };
                comboCanvas.Children.Add(arrow);
            }
        }

        foreach (var node in _comboGraph.Nodes)
        {
            if (!_nodePositions.TryGetValue(node.Id, out var pos)) continue;

            var nodeColor = GetNodeColor(node);
            var nodeBg = GetNodeBackground(node);

            var border = new Border
            {
                Width = NODE_WIDTH,
                Height = NODE_HEIGHT,
                BorderBrush = nodeColor,
                BorderThickness = new Thickness(2),
                Background = nodeBg,
                CornerRadius = new CornerRadius(4),
                Tag = node
            };
            border.MouseLeftButtonDown += ComboNode_Click;
            border.PreviewMouseRightButtonDown += ComboNode_RightClick;
            border.AllowDrop = true;
            border.DragEnter += ComboNode_DragEnter;
            border.DragLeave += ComboNode_DragLeave;
            border.Drop += ComboNode_Drop;

            if (!node.IsRoot)
            {
                var ctxMenu = new ContextMenu();
                var resetItem = new MenuItem { Header = "Reset to Vanilla", Tag = node };
                resetItem.Click += ResetNodeToVanilla_Click;
                ctxMenu.Items.Add(resetItem);
                border.ContextMenu = ctxMenu;
            }

            Canvas.SetLeft(border, pos.X);
            Canvas.SetTop(border, pos.Y);
            comboCanvas.Children.Add(border);

            if (!string.IsNullOrEmpty(node.InputLabel) && !node.IsRoot)
            {
                var inputColor = GetInputColor(node.InputLabel);
                var inputBg = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x18, 0x18, 0x25)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(3, 1, 3, 1),
                    IsHitTestVisible = false
                };
                var inputText = new TextBlock
                {
                    Text = node.InputLabel,
                    Foreground = inputColor,
                    FontSize = 8,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Left
                };
                inputBg.Child = inputText;
                Canvas.SetLeft(inputBg, pos.X + 2);
                Canvas.SetTop(inputBg, pos.Y - 10);
                comboCanvas.Children.Add(inputBg);
            }

            var label = new TextBlock
            {
                Text = node.DisplayName.Length > 14 ? node.DisplayName[..14] + ".." : node.DisplayName,
                Foreground = Brushes.White,
                FontSize = 9,
                TextAlignment = TextAlignment.Center,
                Width = NODE_WIDTH,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, pos.X);
            Canvas.SetTop(label, pos.Y + (NODE_HEIGHT - 14) / 2);
            comboCanvas.Children.Add(label);

            if (node.Id == _selectedNodeId)
            {
                var selColor = GetNodeColor(node);
                var sc = selColor.Color;
                if (sc.R == 0xa6 && sc.G == 0xe3 && sc.B == 0xa1)
                {
                    border.BorderBrush = SelectedGreenBorder;
                    border.Background = SelectedGreenBg;
                }
                else if (sc.R == 0xfa && sc.G == 0xb3 && sc.B == 0x87)
                {
                    border.BorderBrush = SelectedOrangeBorder;
                    border.Background = SelectedOrangeBg;
                }
                else
                {
                    border.BorderBrush = SelectedNodeBorderBrush;
                    border.Background = SelectedNodeBg;
                }
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    private void ComboGraphBorder_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetFocus(hwnd);
            border.Focus();
        }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isResetMode || e.ChangedButton != MouseButton.Left) return;

        var source = e.OriginalSource as DependencyObject;
        if (source == null || !IsDescendantOf(source, comboBorder))
        {
            SetResetMode(false);
            return;
        }

        var hit = VisualTreeHelper.HitTest(comboCanvas, e.GetPosition(comboCanvas));
        if (hit == null)
            SetResetMode(false);
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
    {
        var current = child;
        while (current != null)
        {
            if (current == parent) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void ComboCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hit = VisualTreeHelper.HitTest(comboCanvas, e.GetPosition(comboCanvas));
        if (hit != null)
        {
            var visual = hit.VisualHit;
            while (visual != null && visual != comboCanvas)
            {
                if (visual is Border border && border.Tag is ComboNode) return;
                visual = VisualTreeHelper.GetParent(visual);
            }
        }

        _isPanning = true;
        _panStart = e.GetPosition(this);
        comboCanvas.CaptureMouse();
        PreviewMouseLeftButtonUp += ComboPan_PreviewMouseLeftButtonUp;
        e.Handled = true;
    }

    private void ComboCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _panStart.X;
        var dy = pos.Y - _panStart.Y;
        _panStart = pos;
        _comboTranslate.X += dx;
        _comboTranslate.Y += dy;
    }

    private void ComboCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning) return;
        StopPanning();
    }

    private void ComboCanvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isPanning) StopPanning();
    }

    private void ComboPan_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning) StopPanning();
    }

    private void StopPanning()
    {
        _isPanning = false;
        comboCanvas.ReleaseMouseCapture();
        PreviewMouseLeftButtonUp -= ComboPan_PreviewMouseLeftButtonUp;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (webView?.CoreWebView2 != null)
        {
            webView.CoreWebView2.ExecuteScriptAsync("window.getCameraState()")
                .ContinueWith(t =>
                {
                    var result = t.Result;
                    if (!string.IsNullOrEmpty(result) && result != "null")
                    {
                        try
                        {
                            var cam = JsonConvert.DeserializeObject<CameraState>(result);
                            if (cam != null)
                            {
                                _savedCameraPos = new[] { cam.px, cam.py, cam.pz };
                                _savedCameraTarget = new[] { cam.tx, cam.ty, cam.tz };
                            }
                        }
                        catch { }
                    }
                    Dispatcher.BeginInvoke(() => SaveSettings());
                });
        }
        else
        {
            SaveSettings();
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_initialized) return;

        var focused = FocusManager.GetFocusedElement(this);
        if (focused is TextBox) return;

        switch (e.Key)
        {
            case Key.W: _keyW = true; e.Handled = true; break;
            case Key.A: _keyA = true; e.Handled = true; break;
            case Key.S: _keyS = true; e.Handled = true; break;
            case Key.D: _keyD = true; e.Handled = true; break;
        }
    }

    private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement(this);
        if (focused is TextBox) return;

        switch (e.Key)
        {
            case Key.W: _keyW = false; break;
            case Key.A: _keyA = false; break;
            case Key.S: _keyS = false; break;
            case Key.D: _keyD = false; break;
        }
    }

    private void PanTimer_Tick(object? sender, EventArgs e)
    {
        if (!_keyW && !_keyA && !_keyS && !_keyD)
        {
            _lastPanTick = DateTime.UtcNow;
            return;
        }

        var now = DateTime.UtcNow;
        var dt = (now - _lastPanTick).TotalSeconds;
        _lastPanTick = now;

        double speed = 500;
        if (_keyW) _comboTranslate.Y += speed * dt;
        if (_keyS) _comboTranslate.Y -= speed * dt;
        if (_keyA) _comboTranslate.X += speed * dt;
        if (_keyD) _comboTranslate.X -= speed * dt;
    }

    private static SolidColorBrush GetInputColor(string input)
    {
        if (input.Contains("LMB")) return new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1));
        if (input.Contains("RMB Hold")) return new SolidColorBrush(Color.FromRgb(0xf5, 0xc2, 0xe7));
        if (input.Contains("RMB")) return new SolidColorBrush(Color.FromRgb(0xf3, 0x8b, 0xa8));
        if (input.Contains("S")) return new SolidColorBrush(Color.FromRgb(0xf9, 0xe2, 0xaf));
        if (input.Contains("Shift")) return new SolidColorBrush(Color.FromRgb(0x89, 0xdc, 0xeb));
        if (input.Contains("Q")) return new SolidColorBrush(Color.FromRgb(0xcb, 0xa6, 0xf7));
        return new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa));
    }

    private static SolidColorBrush GetNodeColor(ComboNode node)
    {
        if (!string.IsNullOrEmpty(node.VanillaAnimPath) && node.AnimPath != node.VanillaAnimPath)
            return new SolidColorBrush(Color.FromRgb(0xfa, 0xb3, 0x87));
        if (node.AnimPath != node.DefaultAnimPath)
            return new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1));
        if (node.IsRoot)
            return new SolidColorBrush(Color.FromRgb(0xf9, 0xe2, 0xaf));
        return new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa));
    }

    private static SolidColorBrush GetNodeBackground(ComboNode node)
    {
        if (!string.IsNullOrEmpty(node.VanillaAnimPath) && node.AnimPath != node.VanillaAnimPath)
            return new SolidColorBrush(Color.FromArgb(0x40, 0xfa, 0xb3, 0x87));
        if (node.AnimPath != node.DefaultAnimPath)
            return new SolidColorBrush(Color.FromArgb(0x40, 0xa6, 0xe3, 0xa1));
        if (node.IsRoot)
            return new SolidColorBrush(Color.FromArgb(0x40, 0xf9, 0xe2, 0xaf));
        return new SolidColorBrush(Color.FromArgb(0x40, 0x89, 0xb4, 0xfa));
    }

    private async System.Threading.Tasks.Task FindCardInLibrary(string animPath)
    {
        if (string.IsNullOrEmpty(animPath) || (_allMoves.Count == 0 && _allLocomotion.Count == 0)) return;

        await System.Threading.Tasks.Task.Delay(100);

        var match = _allMoves.FirstOrDefault(m =>
            !string.IsNullOrEmpty(m.FullPath) && m.FullPath.Equals(animPath, StringComparison.OrdinalIgnoreCase) && m.IsUsed)
            ?? _allMoves.FirstOrDefault(m =>
            !string.IsNullOrEmpty(m.FullPath) && m.FullPath.Equals(animPath, StringComparison.OrdinalIgnoreCase));

        if (match != null)
        {
            if (match.IsUsed)
            {
                _expandedEnemies.Add(match.Character);
                if (!_expandedWeapons.ContainsKey(match.Character))
                    _expandedWeapons[match.Character] = new HashSet<string>();
                _expandedWeapons[match.Character].Add(match.WeaponType);
            }
            else
            {
                _expandedEnemies.Add(match.Character);
                if (!_expandedWeapons.ContainsKey(match.Character))
                    _expandedWeapons[match.Character] = new HashSet<string>();
                _expandedWeapons[match.Character].Add(match.WeaponType);
            }

            FilterMoves();
            tabMoves.SelectedIndex = match.IsUsed ? 0 : 1;

            await System.Threading.Tasks.Task.Delay(50);

            var targetList = match.IsUsed ? listVanilla : listUnused;
            var targetBorder = FindVisualChild<Border>(targetList, b =>
            {
                if (b.Tag is MoveInfo mi)
                    return mi.FullPath != null && mi.FullPath.Equals(animPath, StringComparison.OrdinalIgnoreCase);
                return false;
            });

            if (targetBorder != null)
            {
                HighlightCard(targetBorder);
                ScrollToElement(targetBorder);
                txtStatus.Text = $"Found: {match.DisplayNameClean} ({match.Character}/{match.WeaponType})";
            }
            return;
        }

        var locoMatch = _allLocomotion.FirstOrDefault(m =>
            !string.IsNullOrEmpty(m.FullPath) && m.FullPath.Equals(animPath, StringComparison.OrdinalIgnoreCase));

        if (locoMatch != null)
        {
            tabMoves.SelectedIndex = 2;

            await System.Threading.Tasks.Task.Delay(50);

            var locoBorder = FindVisualChild<Border>(listLoco, b =>
            {
                if (b.Tag is MoveInfo mi)
                    return mi.FullPath != null && mi.FullPath.Equals(animPath, StringComparison.OrdinalIgnoreCase);
                return false;
            });

            if (locoBorder != null)
            {
                HighlightCard(locoBorder);
                ScrollToElement(locoBorder);
                txtStatus.Text = $"Found: {locoMatch.DisplayNameClean} (Locomotion)";
            }
            return;
        }

        ShowToast($"Not found in library: {animPath}");
    }

    private void HighlightCard(Border border)
    {
        var glow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.LimeGreen,
            Direction = 0,
            ShadowDepth = 0,
            BlurRadius = 20,
            Opacity = 0.9
        };
        border.Effect = glow;

        var fadeOut = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        fadeOut.Tick += (s, e) =>
        {
            fadeOut.Stop();
            var fade = new System.Windows.Media.Animation.Storyboard();
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0.9, 0, TimeSpan.FromSeconds(0.5));
            System.Windows.Media.Animation.Storyboard.SetTarget(anim, border);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(anim, new System.Windows.PropertyPath("(Border.Effect).(DropShadowEffect.Opacity)"));
            fade.Children.Add(anim);
            fade.Completed += (s2, e2) => { border.Effect = null; };
            fade.Begin();
        };
        fadeOut.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    private void ShowToast(string message)
    {
        _toastTimer?.Stop();
        toastText.Text = message;
        toastBorder.Visibility = Visibility.Visible;
        toastBorder.Opacity = 0;

        var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.2));
        toastBorder.BeginAnimation(OpacityProperty, fadeIn);

        _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (s, e) =>
        {
            _toastTimer.Stop();
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.4));
            fadeOut.Completed += (s2, e2) => { toastBorder.Visibility = Visibility.Collapsed; };
            toastBorder.BeginAnimation(OpacityProperty, fadeOut);
        };
        _toastTimer.Start();
    }

    private Popup? _previewPopup;

    private void ShowPreviewOverlay()
    {
        if (_previewPopup == null)
        {
            var overlay = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 10, 16, 10),
                Child = new StackPanel
                {
                    Children =
                    {
                        new ProgressBar
                        {
                            IsIndeterminate = true,
                            Width = 120,
                            Height = 3,
                            Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa)),
                            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44))
                        },
                        new TextBlock
                        {
                            Text = "Loading animation...",
                            Foreground = new SolidColorBrush(Color.FromRgb(0x6c, 0x70, 0x86)),
                            FontSize = 11,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 6, 0, 0)
                        }
                    }
                }
            };
            _previewPopup = new Popup
            {
                Child = overlay,
                AllowsTransparency = true,
                PlacementTarget = webView,
                Placement = PlacementMode.Center,
                StaysOpen = true,
                IsHitTestVisible = true,
                IsOpen = true
            };
        }
        else
        {
            _previewPopup.IsOpen = true;
        }
    }

    private void HidePreviewOverlay()
    {
        if (_previewPopup != null)
            _previewPopup.IsOpen = false;
    }

    private static T FindVisualChild<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && predicate(typed))
                return typed;
            var result = FindVisualChild<T>(child, predicate);
            if (result != null) return result;
        }
        return null;
    }

    private void ScrollToElement(FrameworkElement element)
    {
        var parent = VisualTreeHelper.GetParent(element);
        while (parent != null)
        {
            if (parent is ScrollViewer sv)
            {
                var transform = element.TransformToAncestor(sv);
                var rect = transform.TransformBounds(new Rect(new Point(0, 0), element.RenderSize));
                var viewportHeight = sv.ViewportHeight;
                var offset = sv.VerticalOffset;
                if (rect.Top < 0)
                    sv.ScrollToVerticalOffset(offset + rect.Top - 20);
                else if (rect.Bottom > viewportHeight)
                    sv.ScrollToVerticalOffset(offset + rect.Bottom - viewportHeight + 20);
                return;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
        element.BringIntoView();
    }

    private async void ComboNode_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is ComboNode node)
        {
            var now = DateTime.UtcNow;
            bool isDoubleClick = node.Id == _lastClickedNodeId && (now - _lastClickTime).TotalMilliseconds < 400;
            _lastClickedNodeId = node.Id;
            _lastClickTime = now;

            if (isDoubleClick)
            {
                await FindCardInLibrary(node.AnimPath);
                return;
            }

            if (_isResetMode)
            {
                ResetNodeToVanilla(node);
                return;
            }

            ClearMoveSelection();
            SelectNode(border, node);

            txtDisplayName.Text = $"Display: {node.DisplayName}";

            if (!string.IsNullOrEmpty(node.AnimPath))
            {
                txtStatus.Text = $"Loading: {node.DisplayName} ({node.AnimPath})...";
                ShowPreviewOverlay();
                try
                {
                    await LoadMeshAsync("MainChar");
                    await LoadAnimationAsync(node.AnimPath);
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("COMBO_CLICK", new Exception($"Node '{node.DisplayName}' (Name={node.Name}, Path={node.AnimPath}): {ex.Message}"));
                    txtStatus.Text = $"Error loading {node.DisplayName}: {ex.Message}";
                }
            }
            else
            {
                txtStatus.Text = $"Node '{node.DisplayName}' has no animation path";
                ErrorLog.Write("COMBO_CLICK", new Exception($"Node '{node.DisplayName}' (Name={node.Name}): no AnimPath"));
            }
        }
    }

    private async void CmbStance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (cmbStance.SelectedItem is not string stance || stance == _activeStance) return;
        await SwitchStanceAsync(stance);
    }

    private async Task SwitchStanceAsync(string stance)
    {
        var contentDir = Path.Combine(_contentPath, "Content");
        if (!Directory.Exists(contentDir))
        {
            ErrorLog.Write("STANCE", new Exception($"Content dir not found: {contentDir}"));
            return;
        }

        var movementDbSrc = Path.Combine(contentDir, _stanceMap[stance].MovementDb + ".uasset");
        var movementDbExp = Path.Combine(contentDir, _stanceMap[stance].MovementDb + ".uexp");

        var srcInfo = new FileInfo(movementDbSrc);
        ErrorLog.Write("STANCE", new Exception($"[{stance}] Source DB: {movementDbSrc} (exists={srcInfo.Exists}, size={srcInfo.Length} bytes)"));

        if (!File.Exists(movementDbSrc))
        {
            txtStatus.Text = $"Stance '{stance}': BaseMovementDB not found at {_stanceMap[stance].MovementDb}";
            cmbStance.SelectedItem = _activeStance;
            return;
        }

        try
        {
            txtStatus.Text = $"Switching to {stance} stance...";

            var backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VanillaBackup");
            Directory.CreateDirectory(backupDir);

            var vanillaDbSrc = Path.Combine(contentDir, "DB/Movement/BaseMovementDB.uasset");
            var vanillaDbExp = Path.Combine(contentDir, "DB/Movement/BaseMovementDB.uexp");
            var backupDbSrc = Path.Combine(backupDir, "BaseMovementDB.uasset");
            var backupDbExp = Path.Combine(backupDir, "BaseMovementDB.uexp");

            if (!File.Exists(backupDbSrc) && File.Exists(vanillaDbSrc))
            {
                File.Copy(vanillaDbSrc, backupDbSrc, true);
                if (File.Exists(vanillaDbExp))
                    File.Copy(vanillaDbExp, backupDbExp, true);
            }

            if (_activeStance == "MainChar" && File.Exists(vanillaDbSrc))
            {
            }
            else if (File.Exists(backupDbSrc))
            {
                File.Copy(backupDbSrc, vanillaDbSrc, true);
                if (File.Exists(backupDbExp))
                    File.Copy(backupDbExp, vanillaDbExp, true);
            }

            File.Copy(movementDbSrc, vanillaDbSrc, true);
            if (File.Exists(movementDbExp))
                File.Copy(movementDbExp, vanillaDbExp, true);

            var transition = _stanceMap[stance].Transition;
            if (transition != null)
            {
                var transSrc = Path.Combine(contentDir, transition + ".uasset");
                var transExp = Path.Combine(contentDir, transition + ".uexp");
                var vanillaTransSrc = Path.Combine(contentDir, "DB/Movement/Transition/BP_TransitionAnimRequest.uasset");
                var vanillaTransExp = Path.Combine(contentDir, "DB/Movement/Transition/BP_TransitionAnimRequest.uexp");
                var backupTransSrc = Path.Combine(backupDir, "BP_TransitionAnimRequest.uasset");
                var backupTransExp = Path.Combine(backupDir, "BP_TransitionAnimRequest.uexp");

                if (!File.Exists(backupTransSrc) && File.Exists(vanillaTransSrc))
                {
                    File.Copy(vanillaTransSrc, backupTransSrc, true);
                    if (File.Exists(vanillaTransExp))
                        File.Copy(vanillaTransExp, backupTransExp, true);
                }

                if (_activeStance == "MainChar" && File.Exists(vanillaTransSrc))
                {
                }
                else if (File.Exists(backupTransSrc))
                {
                    File.Copy(backupTransSrc, vanillaTransSrc, true);
                    if (File.Exists(backupTransExp))
                        File.Copy(backupTransExp, vanillaTransExp, true);
                }

                if (File.Exists(transSrc))
                {
                    File.Copy(transSrc, vanillaTransSrc, true);
                    if (File.Exists(transExp))
                        File.Copy(transExp, vanillaTransExp, true);
                }
            }

            _activeStance = stance;

            var freshParser = new AnimationParser();
            freshParser.Initialize(_contentPath, contentDir);
            _parser = freshParser;

            var graph = await Task.Run(() => _parser.LoadMainCharComboTree());
            if (graph != null)
            {
                _comboGraph = graph;

                var stanceNode = _comboGraph.Nodes.FirstOrDefault(n => n.Name == "MainChar_Stance");
                if (stanceNode != null)
                {
                    stanceNode.AnimPath = _stanceMap[stance].DisplayAnim;
                }

                txtComboInfo.Text = $"{stance} - {_comboGraph.WeaponName} ({_comboGraph.Nodes.Count} nodes, {_comboGraph.Edges.Count} edges)";
                _comboTranslate.X = 0;
                _comboTranslate.Y = 0;
                LayoutComboGraph();
                RenderComboGraph();
                txtStatus.Text = $"Switched to {stance} stance";
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("STANCE", ex);
            txtStatus.Text = $"Error switching stance: {ex.Message}";
            cmbStance.SelectedItem = _activeStance;
        }
    }

    private void ToggleResetMode_Click(object sender, RoutedEventArgs e)
    {
        SetResetMode(!_isResetMode);
    }

    private void SetResetMode(bool enabled)
    {
        _isResetMode = enabled;
        if (enabled)
        {
            btnResetMode.Background = new SolidColorBrush(Color.FromRgb(0xf9, 0xe2, 0xaf));
            btnResetMode.Foreground = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e));
            comboBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xf9, 0xe2, 0xaf));
            comboBorder.BorderThickness = new Thickness(2);
            txtStatus.Text = "Reset mode: Click a node to reset it to vanilla. Click canvas to exit.";
        }
        else
        {
            btnResetMode.ClearValue(Button.BackgroundProperty);
            btnResetMode.ClearValue(Button.ForegroundProperty);
            comboBorder.BorderBrush = Brushes.Transparent;
            comboBorder.BorderThickness = new Thickness(0);
        }
    }

    private void ResetNodeToVanilla(ComboNode node)
    {
        var vanillaPath = !string.IsNullOrEmpty(node.VanillaAnimPath)
            ? node.VanillaAnimPath
            : node.DefaultAnimPath;

        if (string.IsNullOrEmpty(vanillaPath)) return;

        node.AnimPath = vanillaPath;
        RenderComboGraph();
        txtStatus.Text = $"Reset {node.DisplayName} to vanilla";
    }

    private void ResetNodeToVanilla_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu ctxMenu &&
            ctxMenu.PlacementTarget is Border border && border.Tag is ComboNode node)
        {
            ResetNodeToVanilla(node);
        }
    }

    private void ComboNode_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) &&
            sender is Border border && border.Tag is ComboNode node)
        {
            var vanillaPath = !string.IsNullOrEmpty(node.VanillaAnimPath)
                ? node.VanillaAnimPath
                : node.DefaultAnimPath;

            if (!string.IsNullOrEmpty(vanillaPath) && node.AnimPath != vanillaPath)
            {
                node.AnimPath = vanillaPath;
                RenderComboGraph();
                txtStatus.Text = $"Reset {node.DisplayName} to vanilla";
            }
            e.Handled = true;
        }
    }

    private void ComboNode_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1));
            border.Background = new SolidColorBrush(Color.FromArgb(0x60, 0xa6, 0xe3, 0xa1));
            e.Handled = true;
        }
    }

    private void ComboNode_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border && border.Tag is ComboNode node)
        {
            border.BorderBrush = GetNodeColor(node);
            border.Background = GetNodeBackground(node);
            e.Handled = true;
        }
    }

    private void ComboNode_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border border && border.Tag is ComboNode node)
        {
            border.BorderBrush = GetNodeColor(node);
            border.Background = GetNodeBackground(node);

            if (e.Data.GetDataPresent(typeof(MoveInfo)))
            {
                var move = e.Data.GetData(typeof(MoveInfo)) as MoveInfo;
                if (move != null)
                {
                    if (node.Name == "MainChar_Stance" && !string.IsNullOrEmpty(move.Character)
                        && _stanceMap.ContainsKey(move.Character))
                    {
                        node.AnimPath = move.FullPath;
                        RenderComboGraph();
                        txtStatus.Text = $"Combat Stance -> {move.Character} ({move.DisplayName})";
                    }
                    else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        var linkedNodes = _comboGraph.Nodes
                            .Where(n => n.DefaultAnimPath == node.DefaultAnimPath)
                            .ToList();
                        foreach (var ln in linkedNodes)
                            ln.AnimPath = move.FullPath;
                        RenderComboGraph();
                        txtStatus.Text = $"Replaced {linkedNodes.Count} linked nodes -> {move.DisplayName}";
                    }
                    else
                    {
                        node.AnimPath = move.FullPath;
                        RenderComboGraph();
                        txtStatus.Text = $"Replaced: {node.Name} -> {move.DisplayName}";
                    }
                }
            }
            e.Handled = true;
        }
    }
}

public class Settings
{
    public string ContentPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public bool ShowLines { get; set; } = true;
    public double[]? CameraPosition { get; set; }
    public double[]? CameraTarget { get; set; }
}

public class WebViewMessage
{
    public string action { get; set; } = "";
    public string? name { get; set; }
    public float? duration { get; set; }
    public int? numFrames { get; set; }
    public int? fps { get; set; }
    public int? trackCount { get; set; }
    public float? time { get; set; }
    public float? progress { get; set; }
}

public class CameraState
{
    public double px { get; set; }
    public double py { get; set; }
    public double pz { get; set; }
    public double tx { get; set; }
    public double ty { get; set; }
    public double tz { get; set; }
}
