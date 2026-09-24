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
    private static readonly string TempCustomMovesRoot =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TempCustomMoves");
    private List<MoveInfo> _allLocomotion = [];
    private string _settingsPath;
    private string _contentPath = "";
    private string _outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExportedMods");
    private bool _initialized = false;
    private ComboGraph? _comboGraph;
    private Dictionary<int, Point> _nodePositions = new();
    private const double NODE_WIDTH = 140;
    private const double NODE_HEIGHT = 32;
    private const double INPUT_HEIGHT = 22;
    private const double H_SPACING = 80;
    private const double V_SPACING = 20;

    private bool _isPanning;
    private bool _panMoved;
    private bool _canvasHitNode;
    private Point _panStart;
    private bool _keyW, _keyA, _keyS, _keyD;
    private DispatcherTimer _panTimer;
    private DateTime _lastPanTick = DateTime.UtcNow;
    private readonly DispatcherTimer _searchDebounceTimer;

    private readonly HashSet<string> _expandedEnemies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _expandedWeapons = new(StringComparer.OrdinalIgnoreCase);

    private Border? _selectedMoveBorder;
    private int _selectedNodeId = -1;
    private int _lastClickedNodeId = -1;
    private DateTime _lastClickTime = DateTime.MinValue;
    private Popup? _dragPopup;
    private Border? _draggedCard;
    private DispatcherTimer? _dragTimer;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point lpPoint);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    private bool _isDraggingNode;
    private bool _dragCandidate;
    private int _dragNodeId = -1;
    private Point _dragStartCanvasPos;
    private Point _dragNodeStartPos;
    private DateTime _dragStartTime;
    private readonly Dictionary<int, TextBlock> _nodeLabels = new();
    private readonly Dictionary<int, Border> _nodeInputBgs = new();

    private ComboGraph? _vanillaGraph;
    private ComboGraph? _moddedGraph;
    private Dictionary<int, (string vanilla, string modded)> _nodeDiffs = new();
    private bool _isModLoaded = false;

    private const int TabOther = 0;
    private const int TabCustom = 1;
    private const int TabVanilla = 2;
    private const int TabStances = 3;
    private bool _isResetMode = false;
    private bool _isRetargetMode = false;
    private ComboNode? _retargetSourceNode = null;
    private ComboGraph? _originalVanillaComboGraph;
    private string _activeStance = "MainChar";
    private string? _activeVariant;
    private string? _activeWeapon;
    private readonly Dictionary<string, List<MoveInfo>> _mainCharWeaponMoves = new();
    private Dictionary<string, UnitCacheEntry> _unitCaches = new();
    private UnitProperties? _currentUnitProps;
    private UnitProperties? _unitPropsDefaults;

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

        _panTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _panTimer.Tick += PanTimer_Tick;
        _panTimer.Start();

        await LoadSettingsAsync();
        if (!_initInProgress)
            loadingOverlay.Visibility = Visibility.Collapsed;
        _initialized = true;
        tabMoves.SelectedIndex = TabVanilla;
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
                if (settings != null)
                {
                    if (!string.IsNullOrEmpty(settings.UnrealPakPath))
                        Setup.ContentExtractor.CustomUnrealPakPath = settings.UnrealPakPath;
                    if (!string.IsNullOrEmpty(settings.CryptoJsonPath))
                        Setup.ContentExtractor.CustomCryptoJsonPath = settings.CryptoJsonPath;

                    if (!string.IsNullOrEmpty(settings.ContentPath))
                    {
                        _contentPath = settings.ContentPath;
                        if (!string.IsNullOrWhiteSpace(settings.OutputPath) && Directory.Exists(settings.OutputPath))
                            _outputPath = settings.OutputPath;
                        else
                            _outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExportedMods");

                        chkShowLines.IsChecked = settings.ShowLines;
                        _savedCameraPos = settings.CameraPosition;
                        _savedCameraTarget = settings.CameraTarget;

                        var detection = ContentDetector.Detect(_contentPath);
                        if (detection.IsValid)
                        {
                            await InitializeParserAsync();
                            return settings;
                        }

                        var failedReason = detection.Reason;
                        var failedPath = _contentPath;

                        var localContent = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameContent");
                        var localDetection = ContentDetector.Detect(localContent);
                        if (localDetection.IsValid)
                        {
                            _contentPath = localContent;
                            SaveSettings();
                            await InitializeParserAsync();
                            return settings;
                        }

                        return await ShowSetupWizardAsync(SetupMode.Repair, failedReason, failedPath);
                    }

                    // Empty ContentPath — still try local GameContent before first-run wizard
                    var emptyFallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameContent");
                    if (ContentDetector.Detect(emptyFallback).IsValid)
                    {
                        _contentPath = emptyFallback;
                        if (!string.IsNullOrWhiteSpace(settings.OutputPath) && Directory.Exists(settings.OutputPath))
                            _outputPath = settings.OutputPath;
                        else
                            _outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExportedMods");
                        chkShowLines.IsChecked = settings.ShowLines;
                        _savedCameraPos = settings.CameraPosition;
                        _savedCameraTarget = settings.CameraTarget;
                        SaveSettings();
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

        return await ShowSetupWizardAsync(SetupMode.FirstRun);
    }

    private async Task<Settings> ShowSetupWizardAsync(
        SetupMode mode = SetupMode.FirstRun,
        string? repairReason = null,
        string? previousPath = null)
    {
        var wizard = new SetupWizard
        {
            Owner = this,
            Mode = mode,
            RepairReason = repairReason,
            PreviousContentPath = previousPath,
        };

        if (wizard.ShowDialog() == true && !string.IsNullOrEmpty(wizard.SelectedContentPath))
        {
            _contentPath = wizard.SelectedContentPath;
            if (string.IsNullOrEmpty(_outputPath) || !Directory.Exists(_outputPath))
            {
                _outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExportedMods");
                Directory.CreateDirectory(_outputPath);
            }
            SaveSettings();
            await InitializeParserAsync();
            return new Settings { ContentPath = _contentPath, OutputPath = _outputPath };
        }

        if (mode == SetupMode.Repair)
        {
            txtStatus.Text = "Vanilla files still missing. Some features won't be available.";
            ShowToast("Vanilla files missing — open Setup anytime from Settings");
        }
        else
        {
            txtStatus.Text = "No game content loaded. Some features won't be available.";
        }
        loadingOverlay.Visibility = Visibility.Collapsed;
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
            CameraTarget = _savedCameraTarget,
            UnrealPakPath = Setup.ContentExtractor.CustomUnrealPakPath ?? "",
            CryptoJsonPath = Setup.ContentExtractor.CustomCryptoJsonPath ?? "",
        };
        File.WriteAllText(_settingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
    }

    private void UpdateLoading(string text, string detail)
    {
        loadingText.Text = text;
        loadingDetail.Text = detail;
    }

    private bool _initInProgress;
    private async Task InitializeParserAsync()
    {
        if (_initInProgress) return;
        _initInProgress = true;
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
            await Task.Run(() => _parser.ScanDataTableTiming());
            foreach (var move in _allMoves)
                move.IsUsed = usedPaths.Contains(move.FullPath);

            UpdateLoading("Scanning attack DBs...", "Finding archetype attack moves...");
            var attackDbMoves = await Task.Run(() => _parser.ScanArchetypeAttackDbs());
            _allMoves.AddRange(attackDbMoves);

            UpdateLoading("Scanning get-up animations...", "Finding enemy get-up moves...");
            var getUpMoves = await Task.Run(() => _parser.ScanGetUpAnims());
            _allMoves.AddRange(getUpMoves);
            _allMoves = _allMoves.GroupBy(m => m.FullPath).Select(g => g.First()).ToList();
            _allMoves = PreferAttackDbEntries(_allMoves);

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

            DeleteTempCustomMoves();
            UpdateLoading($"Building library ({_allMoves.Count} moves)...", "");
            await Task.Run(BuildMainCharLibraryMoves);
            await Task.Run(() =>
            {
                _parser.MountCustomIntoProvider(TempCustomMovesRoot);
                RegisterCustomMoves();
            });
            BuildTree(_allMoves);

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
            _initInProgress = false;
            loadingOverlay.Visibility = Visibility.Collapsed;
            SetWebViewVisible(true);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        txtSettingsContentPath.Text = _contentPath;
        txtSettingsOutputPath.Text = string.IsNullOrEmpty(_outputPath) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExportedMods") : _outputPath;
        SetWebViewVisible(false);
        settingsOverlay.Visibility = Visibility.Visible;
    }

    private void SettingsOverlay_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource == settingsOverlay)
        {
            settingsOverlay.Visibility = Visibility.Collapsed;
            SetWebViewVisible(true);
        }
    }

    private void CancelSettings_Click(object sender, RoutedEventArgs e)
    {
        settingsOverlay.Visibility = Visibility.Collapsed;
        SetWebViewVisible(true);
    }

    private async void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(
            "Delete settings.json and vanilla game files in app directory to save space?\n\n" +
            "This will remove settings.json and folders: Vanilla, VanillaBackup, extractedPaks.\n\n" +
            "Setup will open so you can re-extract vanilla files (folder or pak).",
            "Confirm Reset", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;
        try {
            if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
            var vanillaDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Vanilla");
            var vanillaAlt = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VanillaBackup");
            var extractedDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extractedPaks");
            var vanillaExtract = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VanillaExtract");
            foreach (var dir in new[]{ vanillaDir, vanillaAlt, extractedDir, vanillaExtract })
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            try
            {
                var tempStage = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SifuPakStage");
                if (Directory.Exists(tempStage)) Directory.Delete(tempStage, true);
            }
            catch { }
        } catch (Exception ex) { ErrorLog.Write("RESET", ex); }
        _contentPath = "";
        _outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExportedMods");
        txtSettingsContentPath.Text = "";
        txtSettingsOutputPath.Text = _outputPath;
        SaveSettings();
        settingsOverlay.Visibility = Visibility.Collapsed;
        SetWebViewVisible(true);
        txtStatus.Text = "Settings reset — open Setup to restore vanilla files";
        ShowToast("Settings reset — Setup will open to re-extract vanilla files");
        try { _parser?.Dispose(); } catch {}

        var wizard = new Setup.SetupWizard
        {
            Owner = this,
            Mode = Setup.SetupMode.FirstRun,
            RepairReason = "Settings and vanilla files were reset.",
        };
        if (wizard.ShowDialog() == true && !string.IsNullOrEmpty(wizard.SelectedContentPath))
        {
            _contentPath = wizard.SelectedContentPath;
            SaveSettings();
            await InitializeParserAsync();
        }
        else
        {
            txtStatus.Text = "No game content loaded. Open Settings → Open Setup to restore.";
            loadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void BrowseContent_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select Content Path (Sifu/Content - with pakchunk0)", InitialDirectory = _contentPath };
        if (dlg.ShowDialog() == true)
        {
            var sel = dlg.FolderName;
            txtSettingsContentPath.Text = sel;
        }
    }

    private async void OpenSetup_Click(object sender, RoutedEventArgs e)
    {
        settingsOverlay.Visibility = Visibility.Collapsed;
        SetWebViewVisible(true);

        var detect = ContentDetector.Detect(_contentPath);
        var mode = Setup.SetupMode.Repair;
        var reason = detect.IsValid
            ? "Re-point or re-extract vanilla content."
            : detect.Reason;

        var wizard = new Setup.SetupWizard
        {
            Owner = this,
            Mode = mode,
            RepairReason = reason,
            PreviousContentPath = _contentPath,
        };

        if (wizard.ShowDialog() == true && !string.IsNullOrEmpty(wizard.SelectedContentPath))
        {
            _contentPath = wizard.SelectedContentPath;
            txtSettingsContentPath.Text = _contentPath;
            SaveSettings();
            try { _parser?.Dispose(); } catch { }
            await InitializeParserAsync();
            txtStatus.Text = $"Content path updated: {_contentPath}";
            ShowToast("Vanilla content refreshed");
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select Output Path (ExportedMods)", InitialDirectory = _outputPath };
        if (dlg.ShowDialog() == true) txtSettingsOutputPath.Text = dlg.FolderName;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var newContent = txtSettingsContentPath.Text.Trim();
        var newOutput = txtSettingsOutputPath.Text.Trim();
        if (!string.IsNullOrEmpty(newContent) && !Directory.Exists(newContent))
        {
            ShowToast("Content Path does not exist");
            return;
        }
        if (!string.IsNullOrEmpty(newContent) && !File.Exists(Path.Combine(Setup.ContentDetector.ResolveContentDir(newContent), "DB/_MainChar/Combos/MainChar_ComboTree.uasset")) && !Directory.Exists(Path.Combine(newContent, "Sifu")))
        {
            // validation: try to find pakchunk0 marker
            if (!Directory.Exists(Path.Combine(newContent, "Engine")) && !File.Exists(Path.Combine(newContent, "DB/_MainChar/Combos/MainChar_ComboTree.uasset")))
                ShowToast("Content Path validation: MainChar_ComboTree not found");
        }
        _contentPath = newContent;
        _outputPath = newOutput;
        SaveSettings();
        try { _parser?.Dispose(); var cDir = Setup.ContentDetector.ResolveContentDir(_contentPath); var fresh = new AnimationParser(); fresh.Initialize(_contentPath, cDir); fresh.MountCustomIntoProvider(TempCustomMovesRoot); _parser = fresh; } catch (Exception ex) { ErrorLog.Write("SETTINGS", ex); }
        settingsOverlay.Visibility = Visibility.Collapsed;
        SetWebViewVisible(true);
        txtStatus.Text = "Settings saved — provider re-initialized";
    }

    private async void Settings_Click_OldForReference(object sender, RoutedEventArgs e)
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
            SetWebViewVisible(false);
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

    private void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        FilterMoves();
    }

    private void FilterMoves()
    {
        if (_allMoves == null || _allMoves.Count == 0) return;

        var searchText = txtSearch.Text.ToLower();

        var filtered = _allMoves.FindAll(m =>
        {
            bool matchesSearch = string.IsNullOrEmpty(searchText) ||
                                 m.DisplayName.ToLower().Contains(searchText) ||
                                 m.FullPath.ToLower().Contains(searchText) ||
                                 m.Character.ToLower().Contains(searchText) ||
                                 m.Category.ToLower().Contains(searchText);

            return matchesSearch;
        });

        bool IsCustom(MoveInfo m) => string.Equals(m.Category, "Custom", StringComparison.OrdinalIgnoreCase);
        var customMoves = filtered.Where(m => IsCustom(m) && !string.IsNullOrWhiteSpace(m.DisplayName)).ToList();
        var vanillaMoves = filtered.Where(m =>
            !IsCustom(m) && m.IsUsed && m.IsValid && !string.IsNullOrWhiteSpace(m.DisplayName) &&
            IsVanillaLibraryCard(m)).ToList();
        // Other: everything non-custom that isn't a valid used vanilla card
        // (includes !IsValid so a missing dependency can't blank the whole library)
        var unusedMoves = filtered.Where(m =>
            !IsCustom(m) && !string.IsNullOrWhiteSpace(m.DisplayName) &&
            !(m.IsUsed && m.IsValid && IsVanillaLibraryCard(m))).ToList();

        bool useAccordion = string.IsNullOrEmpty(searchText);

        if (useAccordion)
        {
            listVanilla.ItemsSource = BuildAccordionList(vanillaMoves);
            listUnused.ItemsSource = BuildAccordionList(unusedMoves, includeComboTreeMoves: false);
            listCustom.ItemsSource = BuildAccordionList(customMoves, includeComboTreeMoves: false);
        }
        else
        {
            listVanilla.ItemsSource = vanillaMoves;
            listUnused.ItemsSource = unusedMoves;
            listCustom.ItemsSource = customMoves;
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
        tabHeaderUnused.Text = $"Other ({unusedMoves.Count})";
        tabHeaderLoco.Text = $"Stances ({filteredLoco.Count})";
        tabHeaderCustom.Text = $"Custom ({customMoves.Count})";
        txtMoveCount.Text = $"{vanillaMoves.Count} vanilla / {unusedMoves.Count} other / {filteredLoco.Count} stances / {customMoves.Count} custom";
    }

    private static readonly string[] MainCharWeaponOrder = ["BareHands", "Bat", "Staff", "Knife", "Special", "Special Combos"];

    private void BuildMainCharLibraryMoves()
    {
        _mainCharWeaponMoves.Clear();
        var trees = new (string label, string comboPath, string weaponTag)[]
        {
            ("BareHands", "Game/DB/_MainChar/Combos/MainChar_ComboTree", "MainChar_Barehands"),
            ("Bat", "Game/DB/_MainChar/Combos/Attacks/Weapons/Bats/MainChar_Bats_ComboTree", "MainChar_Bat"),
            ("Staff", "Game/DB/_MainChar/Combos/Attacks/Weapons/Staff/MainChar_Staff_ComboTree", "MainChar_Staff"),
            ("Knife", "Game/DB/_MainChar/Combos/Attacks/Weapons/Blades/MainChar_Blades_ComboTree", "MainChar_Blade"),
        };

        foreach (var (label, comboPath, weaponTag) in trees)
        {
            var graph = _parser.LoadComboTreeFromPath(comboPath, weaponTag);
            if (graph == null) continue;

            var list = new List<MoveInfo>();
            foreach (var node in graph.Nodes.Where(n => !n.IsRoot &&
                (!string.IsNullOrEmpty(n.DefaultDBPath) || !string.IsNullOrEmpty(n.DefaultAnimPath))))
            {
                var match =
                    (!string.IsNullOrEmpty(node.DefaultDBPath)
                        ? _allMoves.FirstOrDefault(m => m.FullPath == node.DefaultDBPath)
                        : null)
                    ?? (!string.IsNullOrEmpty(node.DefaultAnimPath)
                        ? _allMoves.FirstOrDefault(m => m.FullPath == node.DefaultAnimPath)
                        : null);
                if (match != null && !list.Contains(match) && !IsMainCharSpecialGroupCard(match))
                    list.Add(match);
            }
            if (list.Count > 0)
                _mainCharWeaponMoves[label] = list;
        }

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in _mainCharWeaponMoves.Values)
        {
            foreach (var m in list)
            {
                if (!string.IsNullOrEmpty(m.FullPath))
                    claimed.Add(m.FullPath);
            }
        }

        var specials = new List<MoveInfo>();
        var specialCombos = new List<MoveInfo>();
        foreach (var m in _allMoves)
        {
            if (string.IsNullOrEmpty(m.FullPath) || claimed.Contains(m.FullPath)) continue;
            if (IsSpecialComboDb(m))
                specialCombos.Add(m);
            else if (IsSpecialDb(m))
                specials.Add(m);
        }

        if (specials.Count > 0)
            _mainCharWeaponMoves["Special"] = specials
                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        if (specialCombos.Count > 0)
            _mainCharWeaponMoves["Special Combos"] = specialCombos
                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        ErrorLog.Write("LIBRARY", new Exception(
            $"MainChar library: {string.Join(", ", _mainCharWeaponMoves.Select(kv => $"{kv.Key}={kv.Value.Count}"))}"));
    }

    private static bool IsAttackDbPath(string fullPath) =>
        MoveInfo.IsAttackDbGamePath(fullPath);

    private static bool IsMainCharSpecialCombo(MoveInfo m)
    {
        var p = m.FullPath ?? "";
        if (p.Contains("/MainChar/Attacks/SpecialCombos/", StringComparison.OrdinalIgnoreCase))
            return true;

        var file = p[(p.LastIndexOf('/') + 1)..];
        return file.StartsWith("MainChar_Attack_SpecialCombo", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("MainChar_Special_Combo", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSpecialComboDb(MoveInfo m)
    {
        var p = m.FullPath ?? "";
        if (!p.Contains("DB/_MainChar", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsMainCharSpecialCombo(m)) return true;
        return p.Contains("_SpecialCombo", StringComparison.OrdinalIgnoreCase)
            || p.Contains("SpecialCombos", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSpecialDb(MoveInfo m)
    {
        var p = m.FullPath ?? "";
        if (!p.Contains("DB/_MainChar", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsSpecialComboDb(m)) return false;
        return p.Contains("/_Special/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMainCharSpecialGroupCard(MoveInfo m) =>
        IsSpecialComboDb(m) || IsSpecialDb(m) || IsMainCharSpecialCombo(m);

    private static bool IsVanillaLibraryCard(MoveInfo m) =>
        IsAttackDbPath(m.FullPath ?? "")
        || string.Equals(m.Category, "GetUp", StringComparison.OrdinalIgnoreCase);

    private static List<MoveInfo> PreferAttackDbEntries(List<MoveInfo> moves)
    {
        var dbKeys = new HashSet<(string character, string name)>(
            moves
                .Where(m => IsAttackDbPath(m.FullPath))
                .Select(m => (m.Character.ToLowerInvariant(), m.DisplayName.ToLowerInvariant())));

        return moves
            .Where(m =>
            {
                if (IsAttackDbPath(m.FullPath)) return true;
                var key = (m.Character.ToLowerInvariant(), m.DisplayName.ToLowerInvariant());
                return !dbKeys.Contains(key);
            })
            .ToList();
    }

    private List<object> BuildAccordionList(List<MoveInfo> moves, bool includeComboTreeMoves = true)
    {
        var result = new List<object>();

        if (includeComboTreeMoves && _mainCharWeaponMoves.Count > 0)
        {
            var mainCharExpanded = _expandedEnemies.Contains("MainChar");
            result.Add(new GroupHeader
            {
                Name = "MainChar",
                Level = 1,
                Count = _mainCharWeaponMoves.Values.Sum(v => v.Count),
                Subtitle = "Default moves from your combo graphs",
                IsExpanded = mainCharExpanded
            });

            if (mainCharExpanded)
            {
                foreach (var label in MainCharWeaponOrder)
                {
                    if (!_mainCharWeaponMoves.TryGetValue(label, out var weaponMoves))
                        continue;

                    var expanded = _expandedWeapons.TryGetValue("MainChar", out var wSet)
                        && wSet.Contains(label);

                    result.Add(new GroupHeader
                    {
                        Name = label,
                        Level = 2,
                        Count = weaponMoves.Count,
                        IsExpanded = expanded,
                        ParentName = "MainChar"
                    });

                    if (expanded)
                    {
                        result.AddRange(weaponMoves.Cast<object>());
                    }
                }
            }
        }

        var enemyGroups = moves
            .GroupBy(m => m.Character, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var enemyGroup in enemyGroups)
        {
            if (includeComboTreeMoves &&
                string.Equals(enemyGroup.Key, "MainChar", StringComparison.OrdinalIgnoreCase))
                continue;

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
        bool IsCustom(MoveInfo m) => string.Equals(m.Category, "Custom", StringComparison.OrdinalIgnoreCase);
        var customMoves = moves.Where(m => IsCustom(m) && !string.IsNullOrWhiteSpace(m.DisplayName)).ToList();
        var vanillaMoves = moves.Where(m =>
            !IsCustom(m) && m.IsUsed && !string.IsNullOrWhiteSpace(m.DisplayName) &&
            IsVanillaLibraryCard(m)).ToList();
        var unusedMoves = moves.Where(m =>
            !IsCustom(m) && !string.IsNullOrWhiteSpace(m.DisplayName) &&
            (!m.IsUsed || !IsVanillaLibraryCard(m))).ToList();

        listVanilla.ItemsSource = BuildAccordionList(vanillaMoves);
        listUnused.ItemsSource = BuildAccordionList(unusedMoves, includeComboTreeMoves: false);
        listCustom.ItemsSource = BuildAccordionList(customMoves, includeComboTreeMoves: false);

        listLoco.ItemsSource = _allLocomotion;

        tabHeaderVanilla.Text = $"Vanilla ({vanillaMoves.Count})";
        tabHeaderUnused.Text = $"Other ({unusedMoves.Count})";
        tabHeaderLoco.Text = $"Stances ({_allLocomotion.Count})";
        tabHeaderCustom.Text = $"Custom ({customMoves.Count})";
        txtMoveCount.Text = $"{vanillaMoves.Count} vanilla / {unusedMoves.Count} other / {_allLocomotion.Count} stances / {customMoves.Count} custom";
    }

    private static List<object> BuildGroupedList(List<MoveInfo> moves)
    {
        var result = new List<object>();
        var grouped = moves
            .GroupBy(m => m.Character, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

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
    private static readonly SolidColorBrush SelectedRedirectBorder = new(Color.FromArgb(0x80, 0x89, 0xb4, 0x86));
    private static readonly SolidColorBrush SelectedRedirectBg = new(Color.FromArgb(0x30, 0x89, 0xb4, 0x86));

    private static readonly Color EdgeDefaultColor = Color.FromRgb(0x89, 0xb4, 0xfa);
    private static readonly Color EdgeStanceDim = Color.FromRgb(0x58, 0x5b, 0x70);
    private static readonly Dictionary<string, Color> EdgeColorByInput = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LMB"] = Color.FromRgb(0xa6, 0xe3, 0xa1),
        ["RMB"] = Color.FromRgb(0xf3, 0x8b, 0xa8),
        ["RMB Hold"] = Color.FromRgb(0xf5, 0xc2, 0xe7),
        ["RMB Delay"] = Color.FromRgb(0xf3, 0x8b, 0xa8),
        ["S"] = Color.FromRgb(0xf9, 0xe2, 0xaf),
        ["Shift"] = Color.FromRgb(0x89, 0xdc, 0xeb),
        ["Q"] = Color.FromRgb(0xcb, 0xa6, 0xf7),
    };
    private static readonly Dictionary<string, SolidColorBrush> EdgeBrushByInput = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SolidColorBrush EdgeDefaultBrush;
    private static readonly SolidColorBrush EdgeStanceDimBrush;

    static MainWindow()
    {
        foreach (var kvp in EdgeColorByInput)
        {
            var b = new SolidColorBrush(kvp.Value);
            b.Freeze();
            EdgeBrushByInput[kvp.Key] = b;
        }
        EdgeDefaultBrush = new SolidColorBrush(EdgeDefaultColor);
        EdgeDefaultBrush.Freeze();
        EdgeStanceDimBrush = new SolidColorBrush(EdgeStanceDim);
        EdgeStanceDimBrush.Freeze();
    }

    private class EdgeVisuals
    {
        public System.Windows.Shapes.Path Path;
        public System.Windows.Shapes.Path HitPath;
        public System.Windows.Shapes.Polygon Arrow;
        public bool IsConnected;
        public bool IsConflict;
        public int LastZIndex;
        public System.Windows.Media.Effects.DropShadowEffect? Glow;
    }
    private Dictionary<ComboEdge, EdgeVisuals> _edgeVisuals = new();
    private Dictionary<int, Border> _nodeBorders = new();

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
            if (prev != null && _nodeBorders.TryGetValue(_selectedNodeId, out var prevBorder))
            {
                prevBorder.BorderBrush = GetNodeColor(prev);
                prevBorder.Background = GetNodeBackground(prev);
            }
            _selectedNodeId = -1;
        }
    }


    private void SelectNode(Border border, ComboNode node)
    {
        if (_selectedNodeId >= 0 && _comboGraph != null)
        {
            var prev = _comboGraph.Nodes.FirstOrDefault(n => n.Id == _selectedNodeId);
            if (prev != null && _nodeBorders.TryGetValue(_selectedNodeId, out var prevBorder))
            {
                prevBorder.BorderBrush = GetNodeColor(prev);
                prevBorder.Background = GetNodeBackground(prev);
            }
        }
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
        else if (node.IsRedirect)
        {
            border.BorderBrush = SelectedRedirectBorder;
            border.Background = SelectedRedirectBg;
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
            SetSourceTypeDisplay(move.SourceType);
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
            _draggedCard = border;
            border.Opacity = 0.15;
            ShowDragVisual(move);
            var data = new DataObject(typeof(MoveInfo), move);
            try
            {
                DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
            }
            finally
            {
                HideDragVisual();
                border.Opacity = 1;
                _draggedCard = null;
            }
        }
    }

    private void ShowDragVisual(MoveInfo move)
    {
        var greenBar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 10, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1)),
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var badges = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        if (!string.IsNullOrEmpty(move.Character))
            badges.Children.Add(CreateTagBadge(move.Character));
        if (!string.IsNullOrEmpty(move.WeaponType))
            badges.Children.Add(CreateTagBadge(move.WeaponType));
        if (!string.IsNullOrEmpty(move.Category))
            badges.Children.Add(CreateTagBadge(move.Category));
        if (string.Equals(move.SourceType, "Animation File", StringComparison.OrdinalIgnoreCase))
        {
            var animBadge = CreateTagBadge("Anim");
            animBadge.Background = new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1));
            if (animBadge.Child is TextBlock animLabel)
                animLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e));
            badges.Children.Add(animBadge);
        }

        var nameBlock = new TextBlock
        {
            Text = move.DisplayNameClean,
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        };

        var stack = new StackPanel();
        stack.Children.Add(nameBlock);
        stack.Children.Add(badges);

        var dock = new DockPanel();
        DockPanel.SetDock(greenBar, Dock.Left);
        dock.Children.Add(greenBar);
        dock.Children.Add(stack);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            Width = 280,
            Child = dock
        };

        _dragPopup = new Popup
        {
            Child = card,
            AllowsTransparency = true,
            Placement = PlacementMode.Absolute,
            IsOpen = true,
            StaysOpen = true
        };

        _dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _dragTimer.Tick += (_, _) =>
        {
            if (_dragPopup != null && GetCursorPos(out var pt))
                _dragPopup.PlacementRectangle = new Rect(pt.X + 16, pt.Y + 16, 0, 0);
        };
        _dragTimer.Start();
    }

    private void SetSourceTypeDisplay(string sourceType)
    {
        txtSourceType.Text = $"Type: {sourceType}";
        txtSourceType.Foreground = sourceType switch
        {
            "Attack DB" => new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa)),
            "Animation File" => new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1)),
            "Custom" => new SolidColorBrush(Color.FromRgb(0xcb, 0xa6, 0xf7)),
            _ => new SolidColorBrush(Color.FromRgb(0xa6, 0xad, 0xc8)),
        };
    }

    private static string ComboNodeTypePath(ComboNode node)
    {
        if (!string.IsNullOrEmpty(node.DefaultDBPath)) return node.DefaultDBPath;
        if (!string.IsNullOrEmpty(node.SourceDBPath)) return node.SourceDBPath;
        return node.AnimPath;
    }

    private static Border CreateTagBadge(string text)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x80, 0x58, 0x5b, 0x70)),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 4, 0),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(0xcd, 0xcd, 0xd6)),
                FontSize = 8,
                FontWeight = FontWeights.Normal
            }
        };
    }

    private void HideDragVisual()
    {
        _dragTimer?.Stop();
        _dragTimer = null;
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

            if (data == null && Directory.Exists(TempCustomMovesRoot))
            {
                try { _parser.MountCustomIntoProvider(TempCustomMovesRoot); } catch { }
                data = await System.Threading.Tasks.Task.Run(() =>
                    _parser.LoadAnimation(gamePath));
            }

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

    private void ChkShowLines_Changed(object sender, RoutedEventArgs e)
    {
        if (webView?.CoreWebView2 != null)
            webView.CoreWebView2.ExecuteScriptAsync(
                $"window.setSkeletonVisible({(chkShowLines.IsChecked == true ? "true" : "false")})");
        if (_initialized)
            SaveSettings();
    }

    private void ResetCamera_Click(object sender, RoutedEventArgs e)
    {
        if (webView?.CoreWebView2 != null)
        {
            webView.CoreWebView2.ExecuteScriptAsync("window.resetCamera()");
            _savedCameraPos = null;
            _savedCameraTarget = null;
        }
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

    private static bool IsBarehandsWeaponTag(string? weaponTag) =>
        !string.IsNullOrEmpty(weaponTag) && weaponTag.EndsWith("Barehands", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeWeaponForCache(string arch, string? weaponTag)
    {
        if (string.IsNullOrEmpty(weaponTag)) return null;
        if (string.Equals(arch, "MainChar", StringComparison.OrdinalIgnoreCase)) return weaponTag;
        return IsBarehandsWeaponTag(weaponTag) ? null : weaponTag;
    }

    private static bool IsActiveBarehands(string arch, string weaponTag)
    {
        if (string.Equals(arch, "MainChar", StringComparison.OrdinalIgnoreCase))
            return string.Equals(weaponTag, "MainChar_Barehands", StringComparison.OrdinalIgnoreCase);
        return IsBarehandsWeaponTag(weaponTag);
    }

    private string GetUnitCacheKey()
    {
        var arch = _activeStance?.Split('|')[0] ?? "MainChar";
        var weapon = NormalizeWeaponForCache(arch, _activeWeapon);
        var parts = new List<string> { arch };
        if (!string.IsNullOrEmpty(_activeVariant)) parts.Add(_activeVariant);
        if (!string.IsNullOrEmpty(weapon)) parts.Add(weapon);
        return string.Join("|", parts);
    }

    private string? ResolveProjectUnitCacheKey(
        Dictionary<string, UnitCacheEntry> caches,
        string savedStance,
        string? savedVariant,
        string? savedWeapon)
    {
        if (caches == null || caches.Count == 0) return null;

        string arch = savedStance.Split('|')[0];

        var preferredParts = new List<string> { arch };
        if (!string.IsNullOrEmpty(savedVariant)) preferredParts.Add(savedVariant);
        if (!string.IsNullOrEmpty(savedWeapon)) preferredParts.Add(savedWeapon);
        string preferredKey = string.Join("|", preferredParts);
        if (caches.ContainsKey(preferredKey)) return preferredKey;

        if (!string.IsNullOrEmpty(_activeStance))
        {
            string currentArch = _activeStance.Split('|')[0];
            if (string.Equals(currentArch, arch, StringComparison.OrdinalIgnoreCase))
            {
                string currentKey = GetUnitCacheKey();
                if (caches.ContainsKey(currentKey)) return currentKey;
            }
        }

        var candidates = caches.Keys
            .Where(k => k.Equals(arch, StringComparison.OrdinalIgnoreCase)
                || k.StartsWith(arch + "|", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0) return null;

        if (!string.IsNullOrEmpty(savedWeapon))
        {
            var byWeapon = candidates.FirstOrDefault(k =>
                k.EndsWith("|" + savedWeapon, StringComparison.OrdinalIgnoreCase)
                || k.IndexOf(savedWeapon, StringComparison.OrdinalIgnoreCase) >= 0);
            if (byWeapon != null) return byWeapon;
        }

        if (!string.IsNullOrEmpty(savedVariant))
        {
            var byVariant = candidates.FirstOrDefault(k =>
                k.IndexOf(savedVariant, StringComparison.OrdinalIgnoreCase) >= 0);
            if (byVariant != null) return byVariant;
        }

        var byDefault = candidates.FirstOrDefault(k =>
            k.EndsWith("Barehands", StringComparison.OrdinalIgnoreCase)
            || k.EndsWith("_Base", StringComparison.OrdinalIgnoreCase));
        if (byDefault != null) return byDefault;

        return candidates[0];
    }

    private Dictionary<string, Dictionary<int, Point>> CaptureAllUnitPositions()
    {
        var snapshot = new Dictionary<string, Dictionary<int, Point>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _unitCaches)
        {
            if (kvp.Value?.Positions != null)
                snapshot[kvp.Key] = new Dictionary<int, Point>(kvp.Value.Positions);
        }

        if (_comboGraph != null && !string.IsNullOrEmpty(_activeStance))
        {
            var key = GetUnitCacheKey();
            snapshot[key] = new Dictionary<int, Point>(_nodePositions);
        }

        return snapshot;
    }

    private static Dictionary<int, Point> BuildPositionsForGraph(
        string unitKey,
        ComboGraph? graph,
        Dictionary<string, Dictionary<int, Point>>? snapshot)
    {
        var result = new Dictionary<int, Point>();
        if (graph?.Nodes == null) return result;

        Dictionary<int, Point>? source = null;
        if (snapshot != null && !snapshot.TryGetValue(unitKey, out source))
            source = null;

        if (source == null) return result;

        foreach (var node in graph.Nodes)
        {
            if (source.TryGetValue(node.Id, out var pt))
                result[node.Id] = pt;
        }

        return result;
    }

    private void SaveCurrentUnitToCache()
    {
        if (_comboGraph == null || string.IsNullOrEmpty(_activeStance)) return;

        bool keyIsMainChar = string.Equals(_activeStance.Split('|')[0], "MainChar", StringComparison.OrdinalIgnoreCase);
        bool graphHasStance = _comboGraph.Nodes.Any(n => n.Name == "MainChar_Stance");
        bool graphIsEnemy = _comboGraph.Nodes.Any(n =>
            !string.IsNullOrEmpty(n.DefaultDBPath) &&
            n.DefaultDBPath.Contains("/AI/Archetypes/", StringComparison.OrdinalIgnoreCase));
        bool graphIsMainCharWeapon = !keyIsMainChar && _comboGraph.Nodes.Any(n =>
            !string.IsNullOrEmpty(n.DefaultDBPath) &&
            n.DefaultDBPath.Contains("/_MainChar/", StringComparison.OrdinalIgnoreCase));

        if (keyIsMainChar && graphIsEnemy) return;
        if (!keyIsMainChar && (graphHasStance || graphIsMainCharWeapon)) return;

        var key = GetUnitCacheKey();
        string archForWeapon = _activeStance.Split('|')[0];
        _unitCaches[key] = new UnitCacheEntry
        {
            Graph = _comboGraph,
            Positions = new Dictionary<int, Point>(_nodePositions),
            ActiveVariant = _activeVariant,
            ActiveWeapon = NormalizeWeaponForCache(archForWeapon, _activeWeapon),
            Props = _currentUnitProps
        };
    }

    private bool TryRestoreUnitFromCache(string key)
    {
        if (!_unitCaches.TryGetValue(key, out var cached)) return false;
        _comboGraph = cached.Graph;
        _nodePositions = new Dictionary<int, Point>(cached.Positions);
        _activeVariant = cached.ActiveVariant;
        _activeWeapon = NormalizeWeaponForCache(
            key.Contains('|') ? key.Split('|')[0] : key,
            cached.ActiveWeapon);
        _currentUnitProps = cached.Props;
        _activeStance = key.Contains('|') ? key.Split('|')[0] : key;
        txtComboInfo.Text = $"{key} - {_comboGraph.WeaponName} ({_comboGraph.Nodes.Count} nodes, {_comboGraph.Edges.Count} edges)";
        UpdateUnitPropertiesButtonVisibility();
        if (_comboGraph.Nodes.Count > 0 &&
            _comboGraph.Nodes.Any(n => !_nodePositions.ContainsKey(n.Id)))
        {
            LayoutComboGraph();
        }
        RenderComboGraph();
        txtStatus.Text = $"Restored cached state for {key}";
        return true;
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_comboGraph == null)
        {
            txtStatus.Text = "No combo graph loaded";
            return;
        }

        SaveCurrentUnitToCache();

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
                string saveArch = _activeStance?.Split('|')[0] ?? "MainChar";
                ProjectManager.SaveWithCaches(project, _unitCaches, _activeStance, _activeVariant,
                    NormalizeWeaponForCache(saveArch, _activeWeapon), dialog.FileName);
                txtStatus.Text = $"Saved project: {dialog.FileName} ({_unitCaches.Count} units)";
                ShowProjectFeedbackDialog(true, dialog.FileName, project.Name);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save project:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void LoadProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Sifu Edit Files (*.sifu-edit)|*.sifu-edit|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            Title = "Open Project"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var (project, swaps, savedEdges, savedPositions, customNodes, redirectMods) = ProjectManager.Load(dialog.FileName);

                var loadedCaches = ProjectManager.LoadUnitCaches(project);
                if (loadedCaches != null)
                    _unitCaches = loadedCaches;

                string savedStance = project.ActiveStance ?? "MainChar";
                string? savedVariant = project.ActiveVariant;
                string? savedWeapon = project.ActiveWeapon;

                bool restoredFromCache = false;
                string? restoredKey = null;
                if (loadedCaches != null && _unitCaches.Count > 0)
                {
                    string? targetKey = ResolveProjectUnitCacheKey(_unitCaches, savedStance, savedVariant, savedWeapon);
                    if (targetKey != null && TryRestoreUnitFromCache(targetKey))
                    {
                        restoredFromCache = true;
                        restoredKey = targetKey;
                        _comboTranslate.X = 0;
                        _comboTranslate.Y = 0;
                        SetStanceSelection(_activeStance);
                        UpdateVariantButtonVisibility();
                        UpdateWeaponButtonVisibility();
                        UpdateUnitPropertiesButtonVisibility();
                    }
                }

                if (!restoredFromCache)
                {
                    bool stanceMatches = string.Equals(_activeStance, savedStance, StringComparison.OrdinalIgnoreCase);
                    bool variantMatches = string.Equals(_activeVariant, savedVariant, StringComparison.OrdinalIgnoreCase);

                    if (!stanceMatches || (!variantMatches && savedVariant != null))
                    {
                        if (string.Equals(savedStance, "MainChar", StringComparison.OrdinalIgnoreCase))
                        {
                            await SwitchStanceAsync(savedStance);
                        }
                        else
                        {
                            SetStanceSelection(savedStance);
                            _activeStance = savedStance;
                            _activeVariant = savedVariant;
                            _activeWeapon = savedWeapon;
                            UpdateVariantButtonVisibility();
                            UpdateWeaponButtonVisibility();
                            UpdateUnitPropertiesButtonVisibility();
                            if (!string.IsNullOrEmpty(savedVariant))
                            {
                                await LoadVariantComboGraphAsync(savedVariant, saveCurrent: false);
                            }
                            else
                            {
                                await LoadArchetypeGraphAsync(savedStance);
                            }
                        }
                    }
                }

                if (_comboGraph == null)
                {
                    txtStatus.Text = "Failed to load graph for project";
                    return;
                }

                foreach (var sc in customNodes)
                {
                    if (_comboGraph.Nodes.Any(n => n.Id == sc.Id)) continue;
                    var newNode = new ComboNode
                    {
                        Id = sc.Id,
                        TreeIndex = -1,
                        Name = "",
                        AnimPath = sc.AnimPath,
                        DefaultAnimPath = "",
                        DefaultDBPath = "",
                        DisplayName = sc.DisplayName,
                        IsRoot = false,
                        Depth = 0,
                        InputLabel = "",
                        DirectionLabel = "",
                        VanillaAnimPath = ""
                    };
                    _comboGraph.Nodes.Add(newNode);
                }

                int maxId = _comboGraph.Nodes.Count > 0 ? _comboGraph.Nodes.Max(n => n.Id) : -1;
                foreach (var edge in savedEdges)
                {
                    if (_comboGraph.Edges.Any(e => e.FromNodeId == edge.FromNodeId && e.ToNodeId == edge.ToNodeId && e.InputName == edge.InputName))
                        continue;
                    var realEdge = new ComboEdge
                    {
                        FromNodeId = edge.FromNodeId,
                        ToNodeId = edge.ToNodeId,
                        InputName = edge.InputName
                    };
                    _comboGraph.Edges.Add(realEdge);
                }

                foreach (var node in _comboGraph.Nodes)
                {
                    if (swaps.TryGetValue(node.Id.ToString(), out string? animPath) && !string.IsNullOrEmpty(animPath))
                    {
                        node.AnimPath = animPath;
                        if (_parser.AnimToDbPath.TryGetValue(animPath, out var srcDb))
                            node.SourceDBPath = srcDb;
                        var matchedMove = _allMoves.FirstOrDefault(m => m.FullPath == animPath);
                        if (matchedMove != null)
                        {
                            node.ImportedDisplayName = matchedMove.DisplayName;
                            var vanillaPath = !string.IsNullOrEmpty(node.VanillaAnimPath)
                                ? node.VanillaAnimPath
                                : node.DefaultAnimPath;
                            if (!string.IsNullOrEmpty(vanillaPath) &&
                                !string.Equals(animPath, vanillaPath, StringComparison.OrdinalIgnoreCase))
                                node.IsImportedFromMod = true;
                        }
                    }
                }

                foreach (var kvp in redirectMods)
                {
                    if (int.TryParse(kvp.Key, out int nodeId))
                    {
                        var redirectNode = _comboGraph.Nodes.FirstOrDefault(n => n.Id == nodeId && n.IsRedirect);
                        var targetNode = _comboGraph.Nodes.FirstOrDefault(n => n.Id == kvp.Value);
                        if (redirectNode != null && targetNode != null)
                        {
                            var oldEdge = _comboGraph.Edges.FirstOrDefault(e => e.FromNodeId == nodeId && e.IsRedirect);
                            if (oldEdge != null) _comboGraph.Edges.Remove(oldEdge);
                            _comboGraph.Edges.Add(new ComboEdge
                            {
                                FromNodeId = nodeId,
                                ToNodeId = kvp.Value,
                                InputName = oldEdge?.InputName ?? "",
                                IsRedirect = true
                            });
                            redirectNode.ResolvedRedirectNodeId = kvp.Value;
                        }
                    }
                }


                if (savedPositions.Count > 0)
                {
                    foreach (var kvp in savedPositions)
                        _nodePositions[kvp.Key] = kvp.Value;
                }

                LayoutComboGraph();
                RenderComboGraph();
                string restoredNote = restoredKey != null ? $", restored {restoredKey}" : "";
                txtStatus.Text = $"Loaded project: {project.Name} ({_unitCaches.Count} units{restoredNote}, {swaps.Count} swaps, {savedEdges.Count} edges, {customNodes.Count} custom nodes)";
                ShowProjectFeedbackDialog(false, dialog.FileName, project.Name, restoredKey);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("PROJECT", ex);
                MessageBox.Show($"Failed to load project:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ShowProjectFeedbackDialog(bool isSave, string filePath, string projectName, string? restoredKey = null)
    {
        try
        {
            var units = ProjectChangeSummary.BuildFromUnitCaches(_unitCaches, _contentPath);
            var mode = isSave ? "Save" : "Load";
            var dlg = new ProjectFeedbackDialog(
                mode,
                isSave ? "Saved project changes" : "Loaded project changes",
                projectName,
                units,
                restoredKey)
            {
                Owner = this
            };
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(isSave ? "PROJECT_FEEDBACK_SAVE" : "PROJECT_FEEDBACK_LOAD", ex);
        }
    }

    private void ExportPak_Click(object sender, RoutedEventArgs e)
    {
        if (_comboGraph == null)
        {
            txtStatus.Text = "No combo graph loaded";
            return;
        }

        if (string.IsNullOrWhiteSpace(_outputPath) || !Directory.Exists(_outputPath))
        {
            _outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExportedMods");
            Directory.CreateDirectory(_outputPath);
            SaveSettings();
        }

        SaveCurrentUnitToCache();

        bool hasMultiUnitChanges = _unitCaches.Count > 1 && _unitCaches.Values.Any(c =>
            {
                var modified = c.Graph.Nodes.Any(n => !n.IsRoot && !string.IsNullOrEmpty(n.AnimPath)
                    && (n.TreeIndex == -1 || n.AnimPath != n.DefaultAnimPath
                        || (!string.IsNullOrEmpty(n.VanillaAnimPath) && n.AnimPath != n.VanillaAnimPath)));
                var hasRetargets = c.Graph.RedirectOriginalTargets.Any(rd =>
                {
                    var node = c.Graph.Nodes.FirstOrDefault(n => n.Id == rd.Key);
                    return node != null && node.ResolvedRedirectNodeId >= 0 && node.ResolvedRedirectNodeId != rd.Value;
                });
                bool hasUnitProps = c.Props != null && UnitPropertiesManager.HasChanges(_contentPath, c.ActiveVariant ?? "", c.Props);
                return modified || hasRetargets || hasUnitProps;
            });

        if (hasMultiUnitChanges)
        {
            var referenceModDirPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Template");
            var multiDialog = new ExportDialog(
                _unitCaches,
                _contentPath,
                _outputPath,
                _parser.AnimToDbPath,
                referenceModDirPath,
                _parser.AnimToTiming)
            {
                Owner = this,
            };
            multiDialog.ShowDialog();
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
                && (n.TreeIndex == -1 || n.AnimPath != n.DefaultAnimPath
                    || (!string.IsNullOrEmpty(n.VanillaAnimPath) && n.AnimPath != n.VanillaAnimPath)))
            .ToList();

        bool hasRetargets = _comboGraph.RedirectOriginalTargets.Any(kvp =>
        {
            var node = _comboGraph.Nodes.FirstOrDefault(n => n.Id == kvp.Key);
            return node != null && node.ResolvedRedirectNodeId >= 0 && node.ResolvedRedirectNodeId != kvp.Value;
        });

        bool hasUnitProps = _currentUnitProps != null && _activeVariant != null
            && UnitPropertiesManager.HasChanges(_contentPath, _activeVariant, _currentUnitProps);

        if (modified.Count == 0 && !stanceChanged && !hasRetargets && !hasUnitProps)
        {
            txtStatus.Text = "No changes to export. Drag animations onto combo nodes first, or connect existing nodes.";
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

        string? enemyComboPath = null;
        if (_activeVariant != null && !string.Equals(_activeStance, "MainChar", StringComparison.OrdinalIgnoreCase))
            enemyComboPath = ResolveComboFilePath(_activeVariant);

        string? mainCharComboPath = null;
        var mainArch = _activeStance?.Split('|')[0] ?? "MainChar";
        if (string.Equals(mainArch, "MainChar", StringComparison.OrdinalIgnoreCase))
        {
            var weaponTag = string.IsNullOrEmpty(_activeWeapon) ? "MainChar_Barehands" : _activeWeapon;
            mainCharComboPath = ResolveWeaponComboPath("MainChar", weaponTag, weaponTag)
                ?? "Game/DB/_MainChar/Combos/MainChar_ComboTree";
        }

        var dialog = new ExportDialog(
            modified,
            _contentPath,
            _outputPath,
            _parser.AnimToDbPath,
            detectedStance,
            charTransitionPath,
            charBaseMovementDBPath,
            referenceModDir,
            _comboGraph,
            enemyComboPath,
            _parser.AnimToTiming,
            _currentUnitProps,
            _activeVariant,
            mainCharComboPath)
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

        var positionSnapshot = CaptureAllUnitPositions();
        Dictionary<string, UnitCacheEntry>? fullCacheSnapshot = null;

        if (ProjectHasExistingChanges())
        {
            var choiceDialog = new ImportChoiceDialog { Owner = this };
            choiceDialog.ShowDialog();
            switch (choiceDialog.Choice)
            {
                case ImportChoice.Cancel:
                    return;
                case ImportChoice.Fresh:
                    fullCacheSnapshot = new Dictionary<string, UnitCacheEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in _unitCaches)
                        fullCacheSnapshot[kvp.Key] = kvp.Value;
                    await ResetProjectToVanillaAsync("Import — start fresh");
                    break;
                case ImportChoice.Merge:
                default:
                    break;
            }
        }

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
                importDialog.ShowError("UnrealPak not found. Cannot extract mod pak.",
                    "Missing: UnrealPak executable — place it in tools\\ue4\\UnrealPak\\UnrealPak.exe or set path via Settings → Open Setup");
                return;
            }

            await Task.Run(() =>
            {
                void UI(Action a) => Dispatcher.Invoke(a);
                void Fail(string message, string? detail = null)
                {
                    ErrorLog.Write("IMPORT", new Exception($"FAIL: {message}\n{detail}"));
                    UI(() => importDialog.ShowError(message, detail));
                }

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

                // Step 3: Extract pak (decryption keys auto-provisioned)
                UI(() => { importDialog.SetCurrentAction("Extracting pak file..."); importDialog.SetProgress(18); });

                var cryptoKeysPath = Setup.ContentExtractor.EnsurePakKeysFile();
                var hasCryptoKeys = cryptoKeysPath != null && File.Exists(cryptoKeysPath);

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
                    WorkingDirectory = Setup.ContentExtractor.EnsureUnrealPakWorkingDirectory(),
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

                if (process.ExitCode != 0)
                {
                    var errLines = string.Join("\n", (stderr + "\n" + stdout)
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Where(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase)
                                 || l.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                                 || l.Contains("Warning", StringComparison.OrdinalIgnoreCase))
                        .Take(6));
                    if (string.IsNullOrWhiteSpace(errLines))
                        errLines = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                    Fail($"Failed to extract pak (UnrealPak exit {process.ExitCode}).",
                        string.IsNullOrWhiteSpace(errLines) ? null : errLines);
                    return;
                }

                UI(() => importDialog.SetProgress(30));
                UI(() => { importDialog.UpdateStep(1, "done"); });

                // Step 2: Discover combo trees + stance + unit properties
                UI(() => { importDialog.UpdateStep(2, "active"); importDialog.SetCurrentAction("Finding combo trees / unit props..."); importDialog.SetProgress(35); });

                var allExtracted = Directory.GetFiles(importTempRoot, "*", SearchOption.AllDirectories);
                ErrorLog.Write("IMPORT", new Exception($"Extracted {allExtracted.Length} files to {importTempRoot}"));
                foreach (var f in allExtracted)
                    ErrorLog.Write("IMPORT", new Exception($"  {Path.GetRelativePath(importTempRoot, f)}"));

                var extractedUassets = Directory.GetFiles(importTempRoot, "*.uasset", SearchOption.AllDirectories);
                var extractedBasenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in extractedUassets)
                    extractedBasenames.TryAdd(Path.GetFileNameWithoutExtension(f), f);

                var comboCatalog = BuildImportComboCatalog();
                var discoveredTrees = new List<(string path, ImportComboEntry entry)>();
                foreach (var f in extractedUassets)
                {
                    var baseName = Path.GetFileNameWithoutExtension(f);
                    if (comboCatalog.TryGetValue(baseName, out var entry))
                        discoveredTrees.Add((f, entry));
                }
                foreach (var (path, entry) in discoveredTrees)
                    ErrorLog.Write("IMPORT", new Exception($"Discovered combo tree: {Path.GetFileName(path)} → {entry.UnitKey} ({entry.GamePath})"));
                bool hasComboTree = discoveredTrees.Count > 0;

                // Unit properties candidates (ArchetypeDB / ContextualDefense by basename)
                var importedProps = new Dictionary<string, UnitProperties>(StringComparer.OrdinalIgnoreCase);
                var propsFilesFound = new List<string>();
                try
                {
                    foreach (var variantTag in UnitPropertiesManager.AllVariantTags)
                    {
                        string? archFile = null;
                        string? defFile = null;
                        var archPath = UnitPropertiesManager.ResolveArchetypePath(variantTag);
                        var defPath = UnitPropertiesManager.ResolveContextDefensePath(variantTag);
                        if (archPath != null)
                        {
                            var archBase = Path.GetFileNameWithoutExtension(archPath);
                            if (extractedBasenames.TryGetValue(archBase, out archFile))
                                propsFilesFound.Add(archFile);
                        }
                        if (defPath != null)
                        {
                            var defBase = Path.GetFileNameWithoutExtension(defPath);
                            if (extractedBasenames.TryGetValue(defBase, out defFile))
                                propsFilesFound.Add(defFile);
                        }
                        if (archFile == null && defFile == null) continue;

                        var props = new UnitProperties();
                        if (archFile != null)
                            UnitPropertiesManager.MergeInto(props, UnitPropertiesManager.ReadArchetypeAsset(archFile));
                        if (defFile != null)
                            UnitPropertiesManager.MergeInto(props, UnitPropertiesManager.ReadDefenseAsset(defFile));

                        if (!UnitPropertiesManager.HasAnyValue(props))
                        {
                            ErrorLog.Write("IMPORT", new Exception($"Unit props candidate {variantTag}: no readable fields"));
                            continue;
                        }

                        importedProps[variantTag] = props;
                        ErrorLog.Write("IMPORT", new Exception(
                            $"Unit props candidate {variantTag}: arch={(archFile != null ? Path.GetFileName(archFile) : "-")} " +
                            $"def={(defFile != null ? Path.GetFileName(defFile) : "-")} " +
                            $"H={props.Health} S={props.Structure} M={props.MemoryLimit} Hits={props.HitsCount} Flush={props.MemoryFlushLimit}"));
                    }
                }
                catch (Exception propEx)
                {
                    ErrorLog.Write("IMPORT", propEx);
                    Fail("Found candidate files but failed to read unit properties.", propEx.Message);
                    return;
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

                if (!hasComboTree && detectedStance == null && importedProps.Count == 0)
                {
                    var foundList = extractedUassets
                        .Select(f => Path.GetRelativePath(importTempRoot, f))
                        .Take(8)
                        .ToList();
                    var foundPart = foundList.Count > 0
                        ? "Found: " + string.Join(", ", foundList) + (extractedUassets.Length > foundList.Count ? ", …" : "")
                        : "Found: (no .uasset files)";
                    var missing = new List<string>();
                    if (!hasComboTree) missing.Add("combo tree");
                    if (detectedStance == null) missing.Add("stance");
                    if (importedProps.Count == 0) missing.Add("unit properties");
                    Fail("This pak does not contain a combo tree, stance, or unit properties.",
                        foundPart + "\nMissing: " + string.Join(", ", missing));
                    return;
                }

                var importedUnits = new List<(string unitKey, ComboGraph graph, int moves, int retargets, ImportComboEntry entry)>();
                var treeMismatches = new List<string>();

                if (hasComboTree)
                {
                    // Step 4: Parse modded combo nodes (UAssetAPI)
                    UI(() => { importDialog.UpdateStep(4, "active"); importDialog.SetCurrentAction("Parsing modded animations..."); importDialog.SetProgress(45); });
                    var moddedByUnit = new Dictionary<string, List<ModdedComboNodeInfo>>(StringComparer.OrdinalIgnoreCase);
                    int parseIdx = 0;
                    foreach (var (path, entry) in discoveredTrees)
                    {
                        parseIdx++;
                        var pct = 45 + (int)(25.0 * parseIdx / discoveredTrees.Count);
                        UI(() => importDialog.SetCurrentAction($"Parsing {Path.GetFileName(path)}..."));
                        var nodes = _parser.ReadModdedComboNodes(path);
                        if (nodes.Count == 0)
                        {
                            ErrorLog.Write("IMPORT", new Exception($"No nodes parsed from {path}"));
                            continue;
                        }
                        moddedByUnit[entry.UnitKey] = nodes;
                        UI(() => importDialog.SetProgress(pct));
                    }
                    if (hasComboTree && moddedByUnit.Count == 0)
                    {
                        var names = string.Join(", ", discoveredTrees.Select(t => Path.GetFileName(t.path)).Take(8));
                        Fail("Could not parse modded combo tree(s).",
                            "Found combo file(s): " + names + "\nMissing: readable m_Nodes data");
                        return;
                    }
                    UI(() => { importDialog.UpdateStep(4, "done"); importDialog.SetProgress(70); });

                    // Step 5: Compare with vanilla graphs
                    UI(() => { importDialog.UpdateStep(5, "active"); importDialog.SetCurrentAction("Comparing with vanilla..."); importDialog.SetProgress(75); });

                    string? overlayContentRoot = FindContentRootUnder(importTempRoot);
                    if (overlayContentRoot != null)
                        _parser.SetOverlayProvider(_parser.CreateOverlayProvider(overlayContentRoot));

                    int cmpIdx = 0;
                    foreach (var (path, entry) in discoveredTrees)
                    {
                        cmpIdx++;
                        if (!moddedByUnit.TryGetValue(entry.UnitKey, out var moddedNodes)) continue;
                        var pct = 75 + (int)(15.0 * cmpIdx / discoveredTrees.Count);
                        UI(() => importDialog.SetCurrentAction($"Comparing {entry.UnitKey}..."));

                        var vanilla = _parser.LoadComboTreeFromPath(entry.GamePath, entry.WeaponName);
                        if (vanilla == null)
                        {
                            ErrorLog.Write("IMPORT", new Exception($"Vanilla combo not found for {entry.UnitKey} at {entry.GamePath}"));
                            continue;
                        }

                        var (moves, retargets, unmatched) = _parser.ApplyModdedComboToVanilla(vanilla, moddedNodes, entry.WeaponName);
                        importedUnits.Add((entry.UnitKey, vanilla, moves, retargets, entry));
                        int vanillaTreeCount = vanilla.Nodes
                            .Where(n => n.TreeIndex >= 0)
                            .Select(n => n.TreeIndex)
                            .Distinct()
                            .Count();
                        ErrorLog.Write("IMPORT", new Exception($"Applied {entry.UnitKey}: {moves} move(s), {retargets} retarget(s), {unmatched} unmatched (mod={moddedNodes.Count} vanillaTree={vanillaTreeCount})"));
                        if (moddedNodes.Count != vanillaTreeCount)
                            treeMismatches.Add($"{entry.UnitKey}: {moddedNodes.Count} mod node(s) vs {vanillaTreeCount} vanilla");
                        UI(() => importDialog.SetProgress(pct));
                    }
                    if (hasComboTree && importedUnits.Count == 0)
                    {
                        var paths = string.Join("\n", discoveredTrees.Select(t => t.entry.GamePath).Take(6));
                        Fail("Vanilla combo tree not found for imported units.",
                            "Vanilla path(s):\n" + paths);
                        return;
                    }
                    _parser.SetOverlayProvider(null);
                    UI(() => { importDialog.UpdateStep(5, "done"); importDialog.SetProgress(90); });
                }
                else
                {
                    UI(() => { importDialog.UpdateStep(4, "done"); importDialog.SetProgress(70); });
                    UI(() => { importDialog.UpdateStep(5, "done"); importDialog.SetProgress(90); });
                }

                // Step 6: Finalize — merge caches and activate preferred unit
                UI(() => { importDialog.UpdateStep(6, "active"); importDialog.SetCurrentAction("Finalizing..."); importDialog.SetProgress(95); });

                var finalImported = importedUnits;
                bool isStanceOnly = !hasComboTree && detectedStanceLocal != null && importedProps.Count == 0;
                int totalMoves = finalImported.Sum(u => u.moves);
                int totalRetargets = finalImported.Sum(u => u.retargets);
                string? activatedPropsVariant = null;

                UI(() =>
                {
                    if (finalImported.Count > 0 || importedProps.Count > 0)
                        SaveCurrentUnitToCache();

                    foreach (var (unitKey, graph, moves, retargets, entry) in finalImported)
                    {
                        if (graph != null && unitKey.StartsWith("MainChar", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(entry.ActiveWeapon))
                        {
                            graph.WeaponName = string.Equals(entry.ActiveWeapon, "MainChar_Barehands", StringComparison.OrdinalIgnoreCase)
                                ? "BareHands"
                                : entry.ActiveWeapon;
                        }

                        _unitCaches[unitKey] = new UnitCacheEntry
                        {
                            Graph = graph,
                            Positions = BuildPositionsForGraph(unitKey, graph, positionSnapshot),
                            ActiveVariant = entry.ActiveVariant,
                            ActiveWeapon = entry.ActiveWeapon,
                            Props = _unitCaches.TryGetValue(unitKey, out var prev)
                                ? prev.Props
                                : fullCacheSnapshot != null && fullCacheSnapshot.TryGetValue(unitKey, out var old)
                                    ? old.Props
                                    : null
                        };
                        ErrorLog.Write("IMPORT", new Exception(
                            $"CACHE {unitKey}: {moves} move(s), {retargets} retarget(s), " +
                            $"{graph?.Nodes?.Count ?? 0} nodes, weapon='{graph?.WeaponName}', " +
                            $"activeWeapon='{entry.ActiveWeapon}'"));
                    }

                    if (fullCacheSnapshot != null)
                    {
                        var importedKeys = finalImported
                            .Select(u => u.unitKey)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (var kvp in fullCacheSnapshot)
                        {
                            if (!importedKeys.Contains(kvp.Key) && !_unitCaches.ContainsKey(kvp.Key))
                                _unitCaches[kvp.Key] = kvp.Value;
                        }
                    }

                    foreach (var (unitKey, graph, _, _, entry) in finalImported)
                    {
                        if (graph?.Nodes == null) continue;
                        bool isMc = unitKey.StartsWith("MainChar", StringComparison.OrdinalIgnoreCase);
                        string character = isMc
                            ? "MainChar"
                            : (unitKey.Contains('|') ? unitKey.Split('|')[0] : unitKey);
                        string weaponLabel;
                        if (isMc)
                        {
                            weaponLabel = entry.ActiveWeapon switch
                            {
                                "MainChar_Barehands" => "BareHands",
                                "MainChar_Bat" => "Bat",
                                "MainChar_Staff" => "Staff",
                                "MainChar_Blade" => "Knife",
                                _ => "BareHands"
                            };
                        }
                        else
                        {
                            var segs = unitKey.Split('|');
                            weaponLabel = segs.Length >= 3
                                ? AnimationParser.NormalizeWeaponType(segs[2])
                                : "BareHands";
                        }

                        foreach (var node in graph.Nodes)
                        {
                            if (node.IsRoot || string.IsNullOrEmpty(node.AnimPath)) continue;
                            var animPath = node.AnimPath;
                            if (animPath.Contains("/DB/", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!animPath.Contains("/Animations/", StringComparison.OrdinalIgnoreCase)) continue;
                            if (_allMoves.Any(m => !string.IsNullOrEmpty(m.FullPath) &&
                                    m.FullPath.Equals(animPath, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            _allMoves.Add(new MoveInfo
                            {
                                DisplayName = !string.IsNullOrEmpty(node.ImportedDisplayName)
                                    ? node.ImportedDisplayName
                                    : string.IsNullOrEmpty(node.DisplayName) ? node.Name : node.DisplayName,
                                FullPath = animPath,
                                Character = character,
                                WeaponType = weaponLabel,
                                Category = "Custom",
                                IsUsed = true,
                                IsValid = true
                            });
                        }
                    }
                    FilterMoves();

                    foreach (var (variant, props) in importedProps)
                    {
                        var arch = GetArchFromVariant(variant);
                        var unitKey = $"{arch}|{variant}";
                        if (_unitCaches.TryGetValue(unitKey, out var existing))
                        {
                            existing.Props = props;
                        }
                        else
                        {
                            ComboGraph? graph = null;
                            try
                            {
                                var comboPath = ResolveComboFilePath(variant);
                                if (comboPath != null)
                                    graph = _parser.LoadComboTreeFromPath(comboPath, arch);
                            }
                            catch (Exception ex)
                            {
                                ErrorLog.Write("IMPORT", new Exception($"Props-only vanilla load failed for {variant}: {ex.Message}"));
                            }

                            _unitCaches[unitKey] = new UnitCacheEntry
                            {
                                Graph = graph ?? new ComboGraph(),
                                Positions = new Dictionary<int, Point>(),
                                ActiveVariant = variant,
                                ActiveWeapon = null,
                                Props = props
                            };
                        }
                        ErrorLog.Write("IMPORT", new Exception($"Unit props applied to {unitKey}"));
                    }

                    if (finalImported.Count > 0)
                    {
                        var preferred = finalImported.FirstOrDefault(u => u.unitKey.StartsWith("MainChar", StringComparison.OrdinalIgnoreCase));
                        if (preferred.unitKey == null)
                            preferred = finalImported[0];

                        _comboGraph = preferred.graph;
                        _activeStance = preferred.entry.UnitKey.Contains('|')
                            ? preferred.entry.UnitKey.Split('|')[0]
                            : preferred.entry.UnitKey;
                        _activeVariant = preferred.entry.ActiveVariant;
                        _activeWeapon = preferred.entry.ActiveWeapon;
                        _currentUnitProps = _unitCaches.TryGetValue(preferred.unitKey, out var p) ? p.Props : null;
                        _nodePositions = _unitCaches.TryGetValue(preferred.unitKey, out var preferredCache)
                            && preferredCache.Positions != null
                            ? new Dictionary<int, Point>(preferredCache.Positions)
                            : BuildPositionsForGraph(preferred.unitKey, preferred.graph, positionSnapshot);
                        _isModLoaded = true;
                        _vanillaGraph = null;
                        _moddedGraph = null;
                        _nodeDiffs = new();

                        UpdateVariantButtonVisibility();
                        UpdateWeaponButtonVisibility();
                        UpdateUnitPropertiesButtonVisibility();

                        if (!hasComboTree && detectedStanceLocal != null && _stanceMap.ContainsKey(detectedStanceLocal))
                        {
                            cmbStance.SelectedItem = detectedStanceLocal;
                            var stanceNode = _comboGraph.Nodes.FirstOrDefault(n => n.Name == "MainChar_Stance");
                            if (stanceNode != null)
                                stanceNode.AnimPath = _stanceMap[detectedStanceLocal].DisplayAnim;
                        }
                        else if (_activeStance == "MainChar")
                        {
                            cmbStance.SelectedItem = "MainChar";
                        }

                        txtComboInfo.Text = $"MOD ({_activeStance}): {preferred.graph.WeaponName} ({preferred.graph.Nodes.Count} nodes, {totalMoves} moves, {totalRetargets} retargets, {finalImported.Count} unit(s))";
                        LayoutComboGraph();
                        RenderComboGraph();
                    }
                    else if (importedProps.Count > 0)
                    {
                        var variant = importedProps.Keys.First();
                        activatedPropsVariant = variant;
                        var arch = GetArchFromVariant(variant);
                        var unitKey = $"{arch}|{variant}";
                        var entry = _unitCaches[unitKey];

                        SaveCurrentUnitToCache();
                        _comboGraph = entry.Graph ?? new ComboGraph();
                        _nodePositions = entry.Positions != null
                            ? new Dictionary<int, Point>(entry.Positions)
                            : new Dictionary<int, Point>();
                        _activeStance = arch;
                        _activeVariant = variant;
                        _activeWeapon = null;
                        _currentUnitProps = entry.Props;
                        _isModLoaded = true;
                        _vanillaGraph = null;
                        _moddedGraph = null;
                        _nodeDiffs = new();

                        UpdateVariantButtonVisibility();
                        UpdateWeaponButtonVisibility();
                        UpdateUnitPropertiesButtonVisibility();
                        txtComboInfo.Text = $"MOD ({arch} {variant}): Unit Properties only ({importedProps.Count})";
                        LayoutComboGraph();
                        RenderComboGraph();
                    }
                    else if (detectedStanceLocal != null && _stanceMap.TryGetValue(detectedStanceLocal, out var stanceEntry))
                    {
                        _isModLoaded = true;
                        var stanceNode = _comboGraph?.Nodes.FirstOrDefault(n => n.Name == "MainChar_Stance");
                        if (stanceNode != null)
                            stanceNode.AnimPath = stanceEntry.DisplayAnim;
                        _activeStance = detectedStanceLocal;
                        cmbStance.SelectedItem = detectedStanceLocal;
                        txtComboInfo.Text = $"MOD ({_activeStance}): Stance only";
                        LayoutComboGraph();
                        RenderComboGraph();
                    }
                });

                UI(() => { importDialog.UpdateStep(6, "done"); importDialog.SetProgress(100); });

                Thread.Sleep(200);

                string summary;
                if (finalImported.Count > 0)
                    summary = $"{totalMoves} move(s), {totalRetargets} retarget(s) across {finalImported.Count} unit tree(s)";
                else if (importedProps.Count > 0)
                    summary = $"Unit Properties: {importedProps.Count}";
                else if (isStanceOnly)
                    summary = $"Stance: {detectedStanceLocal}";
                else
                    summary = "Imported";

                string? details = null;
                if (detectedStanceLocal != null && !isStanceOnly)
                    details = $"Stance: {detectedStanceLocal}";
                if (finalImported.Count > 0)
                {
                    var unitList = string.Join(", ", finalImported.Select(u => u.unitKey));
                    details = details != null ? $"{details}\nUnits: {unitList}" : $"Units: {unitList}";
                }
                if (treeMismatches.Count > 0)
                {
                    var mismatchLine = "Tree size mismatch:\n  " + string.Join("\n  ", treeMismatches);
                    details = details != null ? $"{details}\n{mismatchLine}" : mismatchLine;
                }
                if (importedProps.Count > 0)
                {
                    var propLine = $"Unit Properties: {importedProps.Count}";
                    details = details != null ? $"{details}\n{propLine}" : propLine;
                }
                UI(() => importDialog.ShowSuccess(summary, details));
            });
        }
        catch (Exception ex)
        {
            ErrorLog.Write("IMPORT", ex);
            importDialog.ShowError(ex.Message, ex.InnerException?.Message);
        }
        finally
        {
            try { _parser.SetOverlayProvider(null); } catch { }
            try
            {
                if (Directory.Exists(importTempRoot))
                {
                    int harvested = HarvestCustomAnims(importTempRoot);
                    if (harvested > 0)
                    {
                        _parser.MountCustomIntoProvider(TempCustomMovesRoot);
                        RegisterCustomMoves();
                        Dispatcher.Invoke(FilterMoves);
                        ErrorLog.Write("IMPORT", new Exception($"Harvested {harvested} custom anim file(s) → TempCustomMoves"));
                    }
                }
            }
            catch (Exception hex) { ErrorLog.Write("IMPORT", hex); }
            try { if (Directory.Exists(importTempRoot)) Directory.Delete(importTempRoot, true); } catch { }
        }
    }

    private static string? FindContentRootUnder(string root)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(root, "Content", SearchOption.AllDirectories))
            {
                if (Directory.Exists(Path.Combine(dir, "Animations")) || Directory.Exists(Path.Combine(dir, "DB")))
                    return dir;
            }
            var anims = Directory.GetDirectories(root, "Animations", SearchOption.AllDirectories);
            if (anims.Length > 0) return Directory.GetParent(anims[0])?.FullName;
        }
        catch { }
        return null;
    }

    private int HarvestCustomAnims(string extractRoot)
    {
        try
        {
            var vanillaContent = Setup.ContentDetector.ResolveContentDir(_contentPath);

            int copied = 0;
            foreach (var animsDir in Directory.GetDirectories(extractRoot, "Animations", SearchOption.AllDirectories))
            {
                var contentRoot = Directory.GetParent(animsDir)?.FullName;
                if (contentRoot == null) continue;

                foreach (var file in Directory.GetFiles(animsDir, "*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".uasset" && ext != ".uexp" && ext != ".ubulk") continue;

                    var rel = Path.GetRelativePath(contentRoot, file);
                    var vanillaPath = Path.Combine(vanillaContent, rel);
                    if (File.Exists(vanillaPath)) continue;

                    var dest = Path.Combine(TempCustomMovesRoot, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, true);
                    copied++;
                }
            }
            return copied;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("IMPORT", ex);
            return 0;
        }
    }

    private void RegisterCustomMoves()
    {
        if (_allMoves == null || !Directory.Exists(TempCustomMovesRoot)) return;

        var animsRoot = Path.Combine(TempCustomMovesRoot, "Animations");
        if (!Directory.Exists(animsRoot)) return;

        foreach (var file in Directory.GetFiles(animsRoot, "*.uasset", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(TempCustomMovesRoot, file).Replace('\\', '/');
            if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                rel = rel[..^".uasset".Length];
            var gamePath = "Game/" + rel;

            if (_allMoves.Any(m => !string.IsNullOrEmpty(m.FullPath) &&
                    m.FullPath.Equals(gamePath, StringComparison.OrdinalIgnoreCase)))
                continue;

            var parts = rel.Split('/');
            string character = "Custom";
            if (parts.Length >= 2)
            {
                int idx = parts[0].Equals("Animations", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (idx < parts.Length && parts[idx].Equals("Custom", StringComparison.OrdinalIgnoreCase))
                    idx++;
                if (idx < parts.Length) character = parts[idx];
            }

            string weapon = "BareHands";
            foreach (var p in parts)
            {
                var w = AnimationParser.NormalizeWeaponType(p);
                if (w is "BareHands" or "Bats" or "Blades" or "Staff" or "MeteorHammer" or "TriStaff")
                {
                    weapon = w;
                    break;
                }
            }

            _allMoves.Add(new MoveInfo
            {
                DisplayName = Path.GetFileNameWithoutExtension(file),
                FullPath = gamePath,
                Character = character,
                WeaponType = weapon,
                Category = "Custom",
                IsUsed = true,
                IsValid = true
            });
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
            _activeWeapon = "MainChar_Barehands";
            UpdateWeaponButtonVisibility();
            LayoutComboGraph();
            RenderComboGraph();
            btnExport.IsEnabled = true;
            btnResetMode.Visibility = Visibility.Visible;
            btnResetAll.Visibility = Visibility.Visible;
            UpdateUnitPropertiesButtonVisibility();

            cmbStance.Items.Clear();
            foreach (var key in _stanceMap.Keys)
                cmbStance.Items.Add(key);
            cmbStance.SelectedItem = _activeStance;
            cmbStance.Visibility = Visibility.Visible;
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

        var savedPositions = new Dictionary<int, Point>(_nodePositions);
        _nodePositions.Clear();

        foreach (var node in _comboGraph.Nodes)
            node.Depth = -1;

        LayoutComboGraph_Topological();

        var orphanIds = new HashSet<int>(_comboGraph.Nodes
            .Where(n => n.IsRedirect && (n.ResolvedRedirectNodeId < 0
                || !_comboGraph.Nodes.Any(x => x.Id == n.ResolvedRedirectNodeId)))
            .Select(n => n.Id));

        foreach (var kvp in savedPositions)
        {
            if (!orphanIds.Contains(kvp.Key))
                _nodePositions[kvp.Key] = kvp.Value;
        }
    }

    private void LayoutComboGraph_Topological()
    {
        var layoutNodes = _comboGraph!.Nodes.Where(n => !n.IsRedirect).ToList();
        var layoutIds = new HashSet<int>(layoutNodes.Select(n => n.Id));

        var incoming = new Dictionary<int, int>();
        var outgoing = new Dictionary<int, List<int>>();
        foreach (var node in layoutNodes)
        {
            incoming[node.Id] = 0;
            outgoing[node.Id] = new List<int>();
        }
        foreach (var edge in _comboGraph.Edges)
        {
            if (!layoutIds.Contains(edge.FromNodeId) || !layoutIds.Contains(edge.ToNodeId))
                continue;
            if (incoming.ContainsKey(edge.ToNodeId))
                incoming[edge.ToNodeId]++;
            if (outgoing.ContainsKey(edge.FromNodeId))
                outgoing[edge.FromNodeId].Add(edge.ToNodeId);
        }

        var baseDepth = new Dictionary<int, int>();
        var baseInDegree = new Dictionary<int, int>(incoming);
        var bfsQueue = new Queue<int>();
        foreach (var node in layoutNodes)
        {
            if (baseInDegree[node.Id] == 0)
            {
                baseDepth[node.Id] = 0;
                bfsQueue.Enqueue(node.Id);
            }
        }
        var bfsProcessed = new HashSet<int>();
        while (bfsQueue.Count > 0)
        {
            var cid = bfsQueue.Dequeue();
            if (!bfsProcessed.Add(cid)) continue;
            var cd = baseDepth[cid];
            foreach (var tid in outgoing[cid])
            {
                if (!baseDepth.TryGetValue(tid, out var ed) || ed < cd + 1)
                    baseDepth[tid] = cd + 1;
                baseInDegree[tid]--;
                if (baseInDegree[tid] <= 0)
                    bfsQueue.Enqueue(tid);
            }
        }

        foreach (var edge in _comboGraph.Edges)
        {
            if (!layoutIds.Contains(edge.FromNodeId)) continue;
            var toNode = _comboGraph.Nodes.FirstOrDefault(n => n.Id == edge.ToNodeId);
            if (toNode?.IsRedirect == true && toNode.ResolvedRedirectNodeId >= 0 && layoutIds.Contains(toNode.ResolvedRedirectNodeId))
            {
                var srcDepth = baseDepth.GetValueOrDefault(edge.FromNodeId, 0);
                var tgtDepth = baseDepth.GetValueOrDefault(toNode.ResolvedRedirectNodeId, 0);
                if (srcDepth < tgtDepth && !outgoing[edge.FromNodeId].Contains(toNode.ResolvedRedirectNodeId))
                {
                    outgoing[edge.FromNodeId].Add(toNode.ResolvedRedirectNodeId);
                    if (incoming.ContainsKey(toNode.ResolvedRedirectNodeId))
                        incoming[toNode.ResolvedRedirectNodeId]++;
                }
            }
        }

        var depth = new Dictionary<int, int>();
        var inDegree = new Dictionary<int, int>(incoming);

        var queue = new Queue<int>();
        foreach (var node in layoutNodes)
        {
            if (inDegree[node.Id] == 0)
            {
                depth[node.Id] = 0;
                queue.Enqueue(node.Id);
            }
        }

        var processed = new HashSet<int>();
        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            if (!processed.Add(currentId)) continue;

            var currentDepth = depth[currentId];
            foreach (var targetId in outgoing[currentId])
            {
                if (!depth.TryGetValue(targetId, out var existing) || existing < currentDepth + 1)
                    depth[targetId] = currentDepth + 1;

                inDegree[targetId]--;
                if (inDegree[targetId] <= 0)
                    queue.Enqueue(targetId);
            }
        }

        foreach (var node in layoutNodes)
        {
            node.Depth = depth.TryGetValue(node.Id, out var d) ? d : 0;
        }

        // ── Post-pass: longest-path layering — enforce depth[child] > depth[parent] for every edge ──
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in layoutNodes.OrderBy(n => n.Depth))
            {
                foreach (var childId in outgoing[node.Id])
                {
                    var child = layoutNodes.FirstOrDefault(n => n.Id == childId);
                    if (child != null && child.Depth <= node.Depth)
                    {
                        child.Depth = node.Depth + 1;
                        changed = true;
                    }
                }
            }
        }

        var forwardDepth = new Dictionary<int, int>();
        foreach (var node in layoutNodes.OrderByDescending(n => n.Depth))
        {
            var maxChild = 0;
            foreach (var childId in outgoing[node.Id])
            {
                if (forwardDepth.TryGetValue(childId, out var cd) && cd > maxChild)
                    maxChild = cd;
            }
            forwardDepth[node.Id] = maxChild + 1;
        }

        var maxDepth = layoutNodes.Count > 0 ? layoutNodes.Max(n => n.Depth) : 0;
        bool isEnemy = !string.Equals(_activeStance, "MainChar", StringComparison.OrdinalIgnoreCase);
        var columns = new List<List<ComboNode>>();
        for (int d = 0; d <= maxDepth; d++)
        {
            var colQuery = layoutNodes.Where(n => n.Depth == d);
            var col = isEnemy
                ? colQuery.OrderBy(n => n.TreeIndex).ThenBy(n => n.Id).ToList()
                : colQuery.OrderByDescending(n => forwardDepth.GetValueOrDefault(n.Id, 1)).ThenBy(n => n.TreeIndex).ThenBy(n => n.Id).ToList();
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

        ResolveNodeOverlaps();

        var connectedNodeIds = new HashSet<int>();
        foreach (var edge in _comboGraph.Edges)
        {
            connectedNodeIds.Add(edge.FromNodeId);
            connectedNodeIds.Add(edge.ToNodeId);
        }
        var validRedirects = _comboGraph.Nodes.Where(n => n.IsRedirect && n.ResolvedRedirectNodeId >= 0 && connectedNodeIds.Contains(n.Id)).ToList();

        // Build redirect → source (non-redirect parent) map
        var redirectSources = new Dictionary<int, int>();
        foreach (var edge in _comboGraph.Edges)
        {
            if (edge.IsRedirect) continue;
            var toNode = _comboGraph.Nodes.FirstOrDefault(n => n.Id == edge.ToNodeId);
            if (toNode?.IsRedirect == true && !redirectSources.ContainsKey(edge.ToNodeId))
                redirectSources[edge.ToNodeId] = edge.FromNodeId;
        }

        // Group redirects by source node for dynamic overflow
        var redirectsBySource = new Dictionary<int, List<ComboNode>>();
        foreach (var r in validRedirects)
        {
            if (!redirectSources.TryGetValue(r.Id, out var sourceId)) continue;
            if (!redirectsBySource.TryGetValue(sourceId, out var list))
            {
                list = new List<ComboNode>();
                redirectsBySource[sourceId] = list;
            }
            list.Add(r);
        }

        // Place redirects: first 3 per source in col+1, overflow to col+2
        foreach (var srcKvp in redirectsBySource)
        {
            var sourceId = srcKvp.Key;
            var redirects = srcKvp.Value;
            if (!_nodePositions.TryGetValue(sourceId, out var sourcePos)) continue;

            for (int i = 0; i < redirects.Count; i++)
            {
                // Sources with >3 redirects: first 3 in col+1, rest in col+2
                int colOffset = (redirects.Count > 3 && i >= 3) ? 2 : 1;
                double x = sourcePos.X + colWidth * colOffset;
                double y = sourcePos.Y;

                // Resolve vertical overlap with existing nodes at this X
                var existingAtX = _nodePositions.Where(p => Math.Abs(p.Value.X - x) < 1)
                    .OrderBy(p => p.Value.Y).ToList();
                bool overlaps = true;
                while (overlaps)
                {
                    overlaps = false;
                    foreach (var kv in existingAtX)
                    {
                        if (Math.Abs(kv.Value.Y - y) < rowHeight - 1)
                        {
                            y = kv.Value.Y + rowHeight;
                            overlaps = true;
                            break;
                        }
                    }
                }

                _nodePositions[redirects[i].Id] = new Point(x, y);
            }
        }
    }

    private void ResolveNodeOverlaps()
    {
        if (_comboGraph == null) return;

        // Build parent map for graph-aware Y positioning
        var parents = new Dictionary<int, List<int>>();
        foreach (var edge in _comboGraph.Edges)
        {
            if (!parents.TryGetValue(edge.ToNodeId, out var list))
            {
                list = new List<int>();
                parents[edge.ToNodeId] = list;
            }
            list.Add(edge.FromNodeId);
        }

        // Group nodes by column (same X)
        var nodesByColumn = new SortedDictionary<double, List<int>>();
        foreach (var kvp in _nodePositions)
        {
            var colX = kvp.Value.X;
            if (!nodesByColumn.TryGetValue(colX, out var list))
            {
                list = new List<int>();
                nodesByColumn[colX] = list;
            }
            list.Add(kvp.Key);
        }

        double rowHeight = NODE_HEIGHT + V_SPACING;

        // Process columns left to right
        foreach (var colKvp in nodesByColumn)
        {
            var nodeIds = colKvp.Value;

            // Sort by initial Y within column
            nodeIds.Sort((a, b) => _nodePositions[a].Y.CompareTo(_nodePositions[b].Y));

            // Pull each node toward parent Y average (if parents exist in earlier columns)
            foreach (var nodeId in nodeIds)
            {
                if (!parents.TryGetValue(nodeId, out var parentIds) || parentIds.Count == 0) continue;

                double parentYSum = 0;
                int parentCount = 0;
                foreach (var pid in parentIds)
                {
                    if (_nodePositions.TryGetValue(pid, out var ppos))
                    {
                        parentYSum += ppos.Y;
                        parentCount++;
                    }
                }
                if (parentCount > 0)
                {
                    var pos = _nodePositions[nodeId];
                    double targetY = parentYSum / parentCount;
                    double currentY = pos.Y;
                    // Pull 60% toward parent center, keep minimum of current Y (don't move up past initial)
                    double newY = currentY + (targetY - currentY) * 0.6;
                    _nodePositions[nodeId] = new Point(pos.X, Math.Max(currentY, newY));
                }
            }

            // Re-sort after Y adjustment
            nodeIds.Sort((a, b) => _nodePositions[a].Y.CompareTo(_nodePositions[b].Y));

            // Resolve vertical overlaps within column — push down only
            for (int i = 1; i < nodeIds.Count; i++)
            {
                var prevPos = _nodePositions[nodeIds[i - 1]];
                var curPos = _nodePositions[nodeIds[i]];
                double minRequiredY = prevPos.Y + rowHeight;
                if (curPos.Y < minRequiredY)
                {
                    _nodePositions[nodeIds[i]] = new Point(curPos.X, minRequiredY);
                }
            }
        }
    }

    private void RenderComboGraph()
    {
        comboCanvas.Children.Clear();
        _edgeVisuals.Clear();
        _nodeBorders.Clear();
        _nodeLabels.Clear();
        _nodeInputBgs.Clear();
        if (_comboGraph == null)
        {
            UpdateComboCanvasSize();
            return;
        }

        var existingNodeIds = new HashSet<int>(_comboGraph.Nodes.Select(n => n.Id));
        var nodeIdsWithEdges = new HashSet<int>();
        foreach (var edge in _comboGraph.Edges)
        {
            nodeIdsWithEdges.Add(edge.FromNodeId);
            nodeIdsWithEdges.Add(edge.ToNodeId);
        }
        var orphanRedirectIds = new HashSet<int>(_comboGraph.Nodes
            .Where(n => n.IsRedirect && (n.ResolvedRedirectNodeId < 0
                || !existingNodeIds.Contains(n.ResolvedRedirectNodeId)
                || !nodeIdsWithEdges.Contains(n.Id)))
            .Select(n => n.Id));

        var edgesBySource = new Dictionary<int, List<ComboEdge>>();
        foreach (var edge in _comboGraph.Edges)
        {
            if (orphanRedirectIds.Contains(edge.FromNodeId) || orphanRedirectIds.Contains(edge.ToNodeId))
                continue;
            if (!edgesBySource.TryGetValue(edge.FromNodeId, out var list))
            {
                list = new List<ComboEdge>();
                edgesBySource[edge.FromNodeId] = list;
            }
            list.Add(edge);
        }

        var conflictEdges = new HashSet<ComboEdge>();
        int conflictCount = 0;
        if (!string.Equals(_activeStance, "MainChar", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var kvp in edgesBySource)
            {
                var byInput = new Dictionary<string, List<ComboEdge>>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in kvp.Value)
                {
                    if (string.IsNullOrEmpty(e.InputName)) continue;
                    if (!byInput.TryGetValue(e.InputName, out var list))
                    {
                        list = new List<ComboEdge>();
                        byInput[e.InputName] = list;
                    }
                    list.Add(e);
                }
                foreach (var inputKvp in byInput)
                {
                    if (inputKvp.Value.Count > 2)
                    {
                        foreach (var e in inputKvp.Value)
                            conflictEdges.Add(e);
                        conflictCount += inputKvp.Value.Count;
                    }
                }
            }
        }

        foreach (var edge in _comboGraph.Edges)
        {
            if (!_nodePositions.TryGetValue(edge.FromNodeId, out var fromPos)) continue;
            if (!_nodePositions.TryGetValue(edge.ToNodeId, out var toPos)) continue;

            if (orphanRedirectIds.Contains(edge.FromNodeId) || orphanRedirectIds.Contains(edge.ToNodeId))
                continue;

            if (_isRetargetMode && edge.IsRedirect && _retargetSourceNode != null && edge.FromNodeId == _retargetSourceNode.Id)
                continue;

            if (edge.IsRedirect)
                continue;

            bool isStanceEdge = edge.FromNodeId == -1;
            bool isIncomingToRedirect = _comboGraph.Nodes.Any(n => n.Id == edge.ToNodeId && n.IsRedirect);
            var color = isStanceEdge
                ? EdgeStanceDimBrush
                : (EdgeBrushByInput.TryGetValue(edge.InputName, out var c) ? c : EdgeDefaultBrush);
            if (isIncomingToRedirect)
                color = new SolidColorBrush(Color.FromArgb(0x60, 0x6c, 0x70, 0x86));

            var siblings = edgesBySource[edge.FromNodeId];
            int siblingIndex = siblings.IndexOf(edge);
            int siblingCount = siblings.Count;

            double fanY = siblingCount > 1
                ? fromPos.Y + NODE_HEIGHT * (siblingIndex + 0.5) / siblingCount
                : fromPos.Y + NODE_HEIGHT / 2;

            var fromPoint = new Point(fromPos.X + NODE_WIDTH, fanY);
            var toPoint = new Point(toPos.X, toPos.Y + NODE_HEIGHT / 2);

            double dx = toPoint.X - fromPoint.X;
            double curveOffset = Math.Max(40, dx * 0.5);
            var cp1 = new Point(fromPoint.X + curveOffset, fromPoint.Y);
            var cp2 = new Point(toPoint.X - curveOffset, toPoint.Y);

            var pathGeom = new PathGeometry();
            var figure = new PathFigure { StartPoint = fromPoint, IsClosed = false };
            figure.Segments.Add(new BezierSegment(cp1, cp2, toPoint, isStroked: true));
            pathGeom.Figures.Add(figure);

            bool hasMultipleBranches = siblingCount > 1;
            double thickness = isStanceEdge ? 1.5 : (hasMultipleBranches ? 2 : 2);

            var path = new System.Windows.Shapes.Path
            {
                Stroke = color,
                StrokeThickness = thickness,
                Data = pathGeom,
                StrokeDashArray = isIncomingToRedirect ? new DoubleCollection { 4, 2 } : null
            };
            path.Tag = edge;
            comboCanvas.Children.Add(path);

            var hitPath = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                StrokeThickness = Math.Max(thickness, 14),
                Data = pathGeom
            };
            hitPath.Tag = edge;
            hitPath.Cursor = Cursors.Hand;
            Canvas.SetZIndex(hitPath, -1);
            comboCanvas.Children.Add(hitPath);

            System.Windows.Shapes.Polygon? arrow = null;
            var arrowSize = 6;
            double tanX = toPoint.X - cp2.X;
            double tanY = toPoint.Y - cp2.Y;
            double tanLen = Math.Sqrt(tanX * tanX + tanY * tanY);
            if (tanLen > 0)
            {
                double ux = tanX / tanLen;
                double uy = tanY / tanLen;
                var arrowTip = toPoint;
                var arrowP1 = new Point(arrowTip.X - ux * arrowSize + uy * arrowSize / 2, arrowTip.Y - uy * arrowSize - ux * arrowSize / 2);
                var arrowP2 = new Point(arrowTip.X - ux * arrowSize - uy * arrowSize / 2, arrowTip.Y - uy * arrowSize + ux * arrowSize / 2);

                arrow = new System.Windows.Shapes.Polygon
                {
                    Fill = color,
                    Points = new PointCollection { arrowTip, arrowP1, arrowP2 }
                };
                arrow.Tag = edge;
                comboCanvas.Children.Add(arrow);
            }

            _edgeVisuals[edge] = new EdgeVisuals
            {
                Path = path,
                HitPath = hitPath,
                Arrow = arrow,
                IsConnected = false,
                IsConflict = conflictEdges.Contains(edge),
                LastZIndex = 0,
                Glow = null
            };
        }

        if (conflictCount > 0)
            ShowToast($"⚠ {conflictCount} input conflict{(conflictCount > 1 ? "s" : "")} detected — duplicate input on same node");

        foreach (var node in _comboGraph.Nodes)
        {
            if (orphanRedirectIds.Contains(node.Id)) continue;
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
            border.PreviewMouseLeftButtonDown += Node_PreviewMouseLeftButtonDown;
            border.AllowDrop = true;
            border.DragEnter += ComboNode_DragEnter;
            border.DragOver += ComboNode_DragOver;
            border.DragLeave += ComboNode_DragLeave;
            border.Drop += ComboNode_Drop;
            if (!node.IsRoot)
            {
                border.MouseEnter += RetargetNode_MouseEnter;
                border.MouseLeave += RetargetNode_MouseLeave;
            }

            if (!node.IsRoot)
            {
                var ctxMenu = new ContextMenu();
                var resetItem = new MenuItem { Header = "Reset to Vanilla", Tag = node };
                resetItem.Click += ResetNodeToVanilla_Click;
                ctxMenu.Items.Add(resetItem);

                if (node.IsRedirect)
                {
                    var retargetItem = new MenuItem { Header = "Retarget", Tag = node };
                    retargetItem.Click += RetargetRedirect_Click;
                    ctxMenu.Items.Add(retargetItem);
                }


                border.ContextMenu = ctxMenu;
            }

            Canvas.SetLeft(border, pos.X);
            Canvas.SetTop(border, pos.Y);
            comboCanvas.Children.Add(border);
            _nodeBorders[node.Id] = border;

            if (!string.IsNullOrEmpty(node.InputLabel) && !node.IsRoot)
            {
                var inputColor = GetInputColor(node.InputLabel);
                var inputBg = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x18, 0x18, 0x25)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(3, 1, 3, 1),
                    IsHitTestVisible = false,
                    Tag = node.Id
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
                _nodeInputBgs[node.Id] = inputBg;
            }

            var labelText = node.DisplayName.Length > 14 ? node.DisplayName[..14] + ".." : node.DisplayName;
            if (node.IsRedirect && node.ResolvedRedirectNodeId >= 0 && _comboGraph != null)
            {
                var targetNode = _comboGraph.Nodes.FirstOrDefault(n => n.Id == node.ResolvedRedirectNodeId);
                if (targetNode != null)
                {
                    var targetName = targetNode.DisplayName.Length > 10 ? targetNode.DisplayName[..10] + ".." : targetNode.DisplayName;
                    labelText = $"→ {targetName}";
                }
            }
            var label = new TextBlock
            {
                Text = labelText,
                Foreground = Brushes.White,
                FontSize = 9,
                TextAlignment = TextAlignment.Center,
                Width = NODE_WIDTH,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = node.Id
            };
            var contentGrid = new Grid();
            contentGrid.Children.Add(label);


            border.Child = contentGrid;
            _nodeLabels[node.Id] = label;

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
                else if (node.IsRedirect)
                {
                    border.BorderBrush = SelectedRedirectBorder;
                    border.Background = SelectedRedirectBg;
                }
                else
                {
                    border.BorderBrush = SelectedNodeBorderBrush;
                    border.Background = SelectedNodeBg;
                }
            }
        }

        UpdateComboCanvasSize();
    }

    private void UpdateEdgesForNode(int nodeId)
    {
        if (_comboGraph == null) return;
        foreach (var kvp in _edgeVisuals)
        {
            var edge = kvp.Key;
            if (edge.FromNodeId != nodeId && edge.ToNodeId != nodeId) continue;
            if (!_nodePositions.TryGetValue(edge.FromNodeId, out var fromPos)) continue;
            if (!_nodePositions.TryGetValue(edge.ToNodeId, out var toPos)) continue;
            var vis = kvp.Value;
            var siblings = _comboGraph.Edges.Where(e => e.FromNodeId == edge.FromNodeId).ToList();
            int siblingIndex = siblings.IndexOf(edge);
            int siblingCount = siblings.Count;
            double fanY = siblingCount > 1
                ? fromPos.Y + NODE_HEIGHT * (siblingIndex + 0.5) / siblingCount
                : fromPos.Y + NODE_HEIGHT / 2;
            var fromPoint = new Point(fromPos.X + NODE_WIDTH, fanY);
            var toPoint = new Point(toPos.X, toPos.Y + NODE_HEIGHT / 2);
            double dx = toPoint.X - fromPoint.X;
            double curveOffset = Math.Max(40, dx * 0.5);
            var cp1 = new Point(fromPoint.X + curveOffset, fromPoint.Y);
            var cp2 = new Point(toPoint.X - curveOffset, toPoint.Y);
            var pathGeom = new PathGeometry();
            var figure = new PathFigure { StartPoint = fromPoint, IsClosed = false };
            figure.Segments.Add(new BezierSegment(cp1, cp2, toPoint, isStroked: true));
            pathGeom.Figures.Add(figure);
            if (vis.Path != null) vis.Path.Data = pathGeom;
            if (vis.HitPath != null) vis.HitPath.Data = pathGeom;
            if (vis.Arrow != null)
            {
                double tanX = toPoint.X - cp2.X;
                double tanY = toPoint.Y - cp2.Y;
                double tanLen = Math.Sqrt(tanX * tanX + tanY * tanY);
                if (tanLen > 0)
                {
                    double ux = tanX / tanLen;
                    double uy = tanY / tanLen;
                    int arrowSize = 6;
                    var arrowTip = toPoint;
                    var arrowP1 = new Point(arrowTip.X - ux * arrowSize + uy * arrowSize / 2, arrowTip.Y - uy * arrowSize - ux * arrowSize / 2);
                    var arrowP2 = new Point(arrowTip.X - ux * arrowSize - uy * arrowSize / 2, arrowTip.Y - uy * arrowSize + ux * arrowSize / 2);
                    vis.Arrow.Points = new PointCollection { arrowTip, arrowP1, arrowP2 };
                }
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    private void ComboGraphBorder_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (source != null && !IsDescendantOf(source, comboCanvas))
            return;

        if (sender is Border border)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetFocus(hwnd);
            border.Focus();
        }
    }

    private void ComboBorder_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isRetargetMode)
        {
            ExitRetargetMode();
            txtStatus.Text = "Retarget cancelled";
            e.Handled = true;
            return;
        }


        if (e.Key == Key.Delete && _comboGraph != null)
        {
            if (_selectedNodeId >= 0)
            {
                var node = _comboGraph.Nodes.FirstOrDefault(n => n.Id == _selectedNodeId);
                if (node == null || node.IsRoot || node.TreeIndex >= 0) return;

                _comboGraph.Edges.RemoveAll(ed => ed.FromNodeId == node.Id || ed.ToNodeId == node.Id);
                foreach (var n in _comboGraph.Nodes)
                {
                    if (n.IsRedirect && n.ResolvedRedirectNodeId == node.Id)
                        n.ResolvedRedirectNodeId = -1;
                }
                _comboGraph.Nodes.Remove(node);
                _nodePositions.Remove(node.Id);
                _nodeBorders.Remove(node.Id);
                ClearNodeSelection();
                RenderComboGraph();
                txtStatus.Text = $"Deleted node: {node.DisplayName}";
                e.Handled = true;
            }
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

    private void ComboCanvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _canvasHitNode = false;
        if (!comboCanvas.IsHitTestVisible) return;
        var hit = VisualTreeHelper.HitTest(comboCanvas, e.GetPosition(comboCanvas));
        if (hit != null)
        {
            var visual = hit.VisualHit;
            while (visual != null && visual != comboCanvas)
            {
                if (visual is Border border && border.Tag is ComboNode)
                {
                    _canvasHitNode = true;
                    return;
                }
                visual = VisualTreeHelper.GetParent(visual);
            }
        }
    }

    private void ComboCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!comboCanvas.IsHitTestVisible || _canvasHitNode) return;

        _isPanning = true;
        _panMoved = false;
        _panStart = e.GetPosition(this);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetCapture(hwnd);
        comboBorder.CaptureMouse();
        comboToolbar.IsHitTestVisible = false;
        PreviewMouseLeftButtonUp += ComboPan_PreviewMouseLeftButtonUp;
        e.Handled = true;
    }

    private void ComboCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate && !_isDraggingNode && _dragNodeId >= 0)
        {
            if ((DateTime.UtcNow - _dragStartTime).TotalMilliseconds > 150)
            {
                _isDraggingNode = true;
                comboCanvas.CaptureMouse();
            }
        }
        if (_isDraggingNode && _dragNodeId >= 0)
        {
            var cur = e.GetPosition(comboCanvas);
            var ddx = cur.X - _dragStartCanvasPos.X;
            var ddy = cur.Y - _dragStartCanvasPos.Y;
            var newPos = new Point(_dragNodeStartPos.X + ddx, _dragNodeStartPos.Y + ddy);
            _nodePositions[_dragNodeId] = newPos;
            if (_nodeBorders.TryGetValue(_dragNodeId, out var b))
            {
                Canvas.SetLeft(b, newPos.X);
                Canvas.SetTop(b, newPos.Y);
            }
            if (_nodeInputBgs.TryGetValue(_dragNodeId, out var ib))
            {
                Canvas.SetLeft(ib, newPos.X + 2);
                Canvas.SetTop(ib, newPos.Y - 10);
            }
            UpdateEdgesForNode(_dragNodeId);
            e.Handled = true;
            return;
        }
        if (!_isPanning) return;
        if (!comboBorder.IsMouseCaptured)
            comboBorder.CaptureMouse();
        var panPos = e.GetPosition(this);
        var dx = panPos.X - _panStart.X;
        var dy = panPos.Y - _panStart.Y;
        if (Math.Abs(dx) > 2 || Math.Abs(dy) > 2) _panMoved = true;
        _panStart = panPos;
        _comboTranslate.X += dx;
        _comboTranslate.Y += dy;
    }

    private void ComboCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingNode)
        {
            _isDraggingNode = false;
            _dragCandidate = false;
            _dragNodeId = -1;
            if (comboCanvas.IsMouseCaptured) comboCanvas.ReleaseMouseCapture();
            UpdateComboCanvasSize();
            e.Handled = true;
            return;
        }
        if (_dragCandidate)
        {
            _dragCandidate = false;
            _dragNodeId = -1;
        }
        if (!_isPanning) return;
        StopPanning();

        if (_selectedNodeId >= 0 && _nodeBorders.TryGetValue(_selectedNodeId, out var selBorder))
        {
            var upPos = e.GetPosition(comboCanvas);
            double left = Canvas.GetLeft(selBorder);
            double top = Canvas.GetTop(selBorder);
            if (left <= upPos.X && upPos.X <= left + selBorder.ActualWidth
                && top <= upPos.Y && upPos.Y <= top + selBorder.ActualHeight)
                return;
        }
        if (_selectedNodeId >= 0)
            ClearNodeSelection();
    }

    private void ComboBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateComboCanvasSize();
    }

    private void UpdateComboCanvasSize()
    {
        double maxRight = 0;
        double maxBottom = 0;
        foreach (var pos in _nodePositions.Values)
        {
            maxRight = Math.Max(maxRight, pos.X + NODE_WIDTH);
            maxBottom = Math.Max(maxBottom, pos.Y + NODE_HEIGHT);
            maxBottom = Math.Max(maxBottom, pos.Y - 10 + INPUT_HEIGHT);
        }

        const double pad = 160;
        double viewportW = comboBorder.ActualWidth;
        double viewportH = comboBorder.ActualHeight;
        double w = Math.Max(Math.Max(maxRight + pad, viewportW), 600);
        double h = Math.Max(Math.Max(maxBottom + pad, viewportH), 400);
        comboCanvas.Width = w;
        comboCanvas.Height = h;
    }

    private int? HitTestNode(Point canvasPos)
    {
        foreach (var kvp in _nodeBorders)
        {
            var b = kvp.Value;
            double left = Canvas.GetLeft(b);
            double top = Canvas.GetTop(b);
            if (left <= canvasPos.X && canvasPos.X <= left + b.ActualWidth
                && top <= canvasPos.Y && canvasPos.Y <= top + b.ActualHeight)
                return kvp.Key;
        }
        return null;
    }

    private void ComboCanvas_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isRetargetMode)
        {
            ExitRetargetMode();
            txtStatus.Text = "Retarget cancelled";
            e.Handled = true;
        }
    }

    private void Node_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is ComboNode node)
        {
            _dragCandidate = true;
            _isDraggingNode = false;
            _dragNodeId = node.Id;
            _dragStartCanvasPos = e.GetPosition(comboCanvas);
            _dragNodeStartPos = _nodePositions.TryGetValue(node.Id, out var p) ? p : new Point(Canvas.GetLeft(border), Canvas.GetTop(border));
            _dragStartTime = DateTime.UtcNow;
            e.Handled = false;
        }
    }

    private void ComboPan_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning) StopPanning();
    }

    private void StopPanning()
    {
        _isPanning = false;
        ReleaseCapture();
        if (comboBorder.IsMouseCaptured) comboBorder.ReleaseMouseCapture();
        comboToolbar.IsHitTestVisible = true;
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
                    Dispatcher.BeginInvoke(() =>
                    {
                        SaveSettings();
                        DeleteTempCustomMoves();
                    });
                });
        }
        else
        {
            SaveSettings();
            DeleteTempCustomMoves();
        }
    }

    private static void DeleteTempCustomMoves()
    {
        try
        {
            if (Directory.Exists(TempCustomMovesRoot))
                Directory.Delete(TempCustomMovesRoot, true);
        }
        catch { }
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
        if (node.IsRedirect)
            return new SolidColorBrush(Color.FromArgb(0x60, 0x6c, 0x70, 0x86));
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
        if (node.IsRedirect)
            return new SolidColorBrush(Color.FromArgb(0x15, 0x6c, 0x70, 0x86));
        if (!string.IsNullOrEmpty(node.VanillaAnimPath) && node.AnimPath != node.VanillaAnimPath)
            return new SolidColorBrush(Color.FromArgb(0x40, 0xfa, 0xb3, 0x87));
        if (node.AnimPath != node.DefaultAnimPath)
            return new SolidColorBrush(Color.FromArgb(0x40, 0xa6, 0xe3, 0xa1));
        if (node.IsRoot)
            return new SolidColorBrush(Color.FromArgb(0x40, 0xf9, 0xe2, 0xaf));
        return new SolidColorBrush(Color.FromArgb(0x40, 0x89, 0xb4, 0xfa));
    }

    private async System.Threading.Tasks.Task FindCardInLibrary(string animPath, ComboNode? sourceNode = null)
    {
        if (string.IsNullOrEmpty(animPath) || (_allMoves.Count == 0 && _allLocomotion.Count == 0)) return;

        await System.Threading.Tasks.Task.Delay(100);

        static bool CardMatches(Border b, string path) =>
            b.Tag is MoveInfo mi && mi.FullPath != null &&
            mi.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase);

        var searchPaths = new List<string>();
        void AddSearchPath(string? p)
        {
            if (!string.IsNullOrEmpty(p) &&
                !searchPaths.Any(s => s.Equals(p, StringComparison.OrdinalIgnoreCase)))
                searchPaths.Add(p);
        }

        AddSearchPath(sourceNode?.DefaultDBPath);
        AddSearchPath(sourceNode?.SourceDBPath);
        if (_parser.AnimToDbPath.TryGetValue(animPath, out var mappedDb))
            AddSearchPath(mappedDb);
        AddSearchPath(animPath);

        if (!string.IsNullOrEmpty(txtSearch.Text))
        {
            txtSearch.Text = "";
            FilterMoves();
        }

        void ExpandGroups(MoveInfo move)
        {
            if (string.IsNullOrEmpty(move.Character)) return;
            _expandedEnemies.Add(move.Character);
            if (!_expandedWeapons.TryGetValue(move.Character, out var weapons))
            {
                weapons = new HashSet<string>();
                _expandedWeapons[move.Character] = weapons;
            }
            if (string.Equals(move.Character, "MainChar", StringComparison.OrdinalIgnoreCase))
            {
                if (IsSpecialComboDb(move))
                {
                    weapons.Add("Special Combos");
                    return;
                }
                if (IsSpecialDb(move))
                {
                    weapons.Add("Special");
                    return;
                }
            }
            if (!string.IsNullOrEmpty(move.WeaponType))
                weapons.Add(move.WeaponType);
        }

        async System.Threading.Tasks.Task<bool> TryHighlightAsync(
            System.Windows.Controls.ItemsControl list, MoveInfo move, int tabIndex)
        {
            tabMoves.SelectedIndex = tabIndex;
            await System.Threading.Tasks.Task.Delay(50);
            var border = FindVisualChild<Border>(list, b =>
                b.Tag is MoveInfo mi && mi.FullPath != null &&
                mi.FullPath.Equals(move.FullPath, StringComparison.OrdinalIgnoreCase));
            if (border == null) return false;
            HighlightCard(border);
            ScrollToElement(border);
            txtStatus.Text = $"Found: {move.DisplayNameClean} ({move.Character}/{move.WeaponType})";
            return true;
        }

        if (sourceNode is { IsImportedFromMod: true } ||
            (!string.IsNullOrEmpty(sourceNode?.ImportedDisplayName)))
        {
            bool IsCustom(MoveInfo m) =>
                string.Equals(m.Category, "Custom", StringComparison.OrdinalIgnoreCase);

            var animFile = Path.GetFileNameWithoutExtension(animPath);
            var nodeNames = new[]
            {
                sourceNode.Name,
                sourceNode.DisplayName,
                sourceNode.ImportedDisplayName,
                animFile
            }.Where(s => !string.IsNullOrEmpty(s)).ToList();

            var customHit =
                _allMoves.FirstOrDefault(m => IsCustom(m) &&
                    !string.IsNullOrEmpty(m.FullPath) &&
                    m.FullPath.Equals(animPath, StringComparison.OrdinalIgnoreCase))
                ?? _allMoves.FirstOrDefault(m => IsCustom(m) &&
                    !string.IsNullOrEmpty(m.FullPath) &&
                    string.Equals(Path.GetFileNameWithoutExtension(m.FullPath), animFile,
                        StringComparison.OrdinalIgnoreCase))
                ?? nodeNames.SelectMany(key => _allMoves.Where(m =>
                    IsCustom(m) &&
                    (string.Equals(m.DisplayName, key, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(m.DisplayNameClean, key, StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrEmpty(m.FullPath) &&
                      string.Equals(Path.GetFileNameWithoutExtension(m.FullPath), key,
                          StringComparison.OrdinalIgnoreCase)))))
                    .FirstOrDefault();

            if (customHit != null)
            {
                ExpandGroups(customHit);
                FilterMoves();
                if (await TryHighlightAsync(listCustom, customHit, TabCustom))
                    return;

                tabMoves.SelectedIndex = TabCustom;
                txtStatus.Text = $"In library but not visible: {customHit.DisplayNameClean}";
                return;
            }
        }

        MoveInfo? match = null;
        string? matchPath = null;
        foreach (var path in searchPaths)
        {
            var samePath = _allMoves
                .Where(m => !string.IsNullOrEmpty(m.FullPath) && m.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (samePath.Count == 0) continue;

            match =
                samePath.FirstOrDefault(m => string.Equals(m.Category, "Custom", StringComparison.OrdinalIgnoreCase))
                ?? samePath.FirstOrDefault(m => m.IsUsed)
                ?? samePath.FirstOrDefault();
            matchPath = path;
            break;
        }

        if (match != null)
        {
            SetSourceTypeDisplay(match.SourceType);
            var highlightPath = matchPath ?? animPath;

            bool inMainCharTree = _mainCharWeaponMoves.Any(kv =>
                kv.Value.Any(m => m.FullPath != null &&
                    m.FullPath.Equals(highlightPath, StringComparison.OrdinalIgnoreCase)));

            int preferredTab;
            if (string.Equals(match.Category, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                ExpandGroups(match);
                FilterMoves();
                preferredTab = TabCustom;
            }
            else if (inMainCharTree)
            {
                _expandedEnemies.Add("MainChar");
                if (!_expandedWeapons.TryGetValue("MainChar", out var mcWeapons))
                {
                    mcWeapons = new HashSet<string>();
                    _expandedWeapons["MainChar"] = mcWeapons;
                }

                var weaponLabel = "";
                foreach (var kv in _mainCharWeaponMoves)
                {
                    if (kv.Value.Any(m => m.FullPath != null &&
                            m.FullPath.Equals(highlightPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        weaponLabel = kv.Key;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(weaponLabel))
                {
                    weaponLabel = match.WeaponType switch
                    {
                        "Bats" => "Bat",
                        "Blades" => "Knife",
                        _ => match.WeaponType
                    };
                }

                if (!string.IsNullOrEmpty(weaponLabel))
                    mcWeapons.Add(weaponLabel);

                FilterMoves();
                preferredTab = TabVanilla;
            }
            else
            {
                ExpandGroups(match);
                FilterMoves();
                preferredTab = (match.IsUsed && IsVanillaLibraryCard(match))
                    ? TabVanilla : TabOther;
            }

            await System.Threading.Tasks.Task.Delay(50);

            Border? FindIn(System.Windows.Controls.ItemsControl list) =>
                FindVisualChild<Border>(list, b => CardMatches(b, highlightPath));

            var targetBorder = preferredTab switch
            {
                TabVanilla => FindIn(listVanilla),
                TabOther => FindIn(listUnused),
                TabCustom => FindIn(listCustom),
                _ => FindIn(listLoco)
            };
            var activeTab = preferredTab;

            if (targetBorder == null)
            {
                var candidates = new (System.Windows.Controls.ItemsControl list, int tab)[]
                {
                    (listCustom, TabCustom),
                    (listVanilla, TabVanilla),
                    (listUnused, TabOther),
                    (listLoco, TabStances),
                };

                foreach (var (list, tab) in candidates)
                {
                    targetBorder = FindIn(list);
                    if (targetBorder != null)
                    {
                        activeTab = tab;
                        tabMoves.SelectedIndex = tab;
                        break;
                    }
                }
            }

            if (targetBorder != null)
            {
                HighlightCard(targetBorder);
                ScrollToElement(targetBorder);
                txtStatus.Text = $"Found: {match.DisplayNameClean} ({match.Character}/{match.WeaponType})";
            }
            else
            {
                tabMoves.SelectedIndex = activeTab;
                txtStatus.Text = $"In library but not visible: {match.DisplayNameClean}";
            }
            return;
        }

        var locoMatch = _allLocomotion.FirstOrDefault(m =>
            !string.IsNullOrEmpty(m.FullPath) && m.FullPath.Equals(animPath, StringComparison.OrdinalIgnoreCase));

        if (locoMatch != null)
        {
            tabMoves.SelectedIndex = TabStances;

            await System.Threading.Tasks.Task.Delay(50);

            var locoBorder = FindVisualChild<Border>(listLoco, b => CardMatches(b, animPath));

            if (locoBorder != null)
            {
                HighlightCard(locoBorder);
                ScrollToElement(locoBorder);
                txtStatus.Text = $"Found: {locoMatch.DisplayNameClean} (Stances)";
            }
            else
            {
                txtStatus.Text = $"In library but not visible: {locoMatch.DisplayNameClean}";
            }
            return;
        }

        ShowToast("Card not found");
    }

    private string? GetDisplayNameForPath(string animPath)
    {
        if (_comboGraph == null || string.IsNullOrEmpty(animPath)) return null;
        var node = _comboGraph.Nodes.FirstOrDefault(n =>
            !string.IsNullOrEmpty(n.AnimPath) && n.AnimPath.Equals(animPath, StringComparison.OrdinalIgnoreCase));
        return node != null && !string.IsNullOrEmpty(node.DisplayName) ? node.DisplayName : null;
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

    private void PlayFusionAnimation(Border nodeBorder)
    {
        var scaleAnim = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        scaleAnim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1.0, System.Windows.Media.Animation.KeyTime.FromPercent(0)));
        scaleAnim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1.15, System.Windows.Media.Animation.KeyTime.FromPercent(0.4)));
        scaleAnim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1.0, System.Windows.Media.Animation.KeyTime.FromPercent(1.0)));
        scaleAnim.Duration = TimeSpan.FromMilliseconds(300);

        var scaleTransform = new ScaleTransform(1, 1);
        nodeBorder.RenderTransform = scaleTransform;
        nodeBorder.RenderTransformOrigin = new Point(0.5, 0.5);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

        var glow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.LimeGreen,
            BlurRadius = 20,
            ShadowDepth = 0,
            Opacity = 0.8
        };
        nodeBorder.Effect = glow;

        var fadeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        fadeTimer.Tick += (s, e) =>
        {
            fadeTimer.Stop();
            var fade = new System.Windows.Media.Animation.Storyboard();
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0.8, 0, TimeSpan.FromMilliseconds(300));
            System.Windows.Media.Animation.Storyboard.SetTarget(anim, nodeBorder);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(anim, new System.Windows.PropertyPath("(Border.Effect).(DropShadowEffect.Opacity)"));
            fade.Children.Add(anim);
            fade.Completed += (s2, e2) => { nodeBorder.Effect = null; };
            fade.Begin();
        };
        fadeTimer.Start();
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

    private void SetWebViewVisible(bool visible)
    {
        if (webView != null)
            webView.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private Popup? _previewPopup;

    private void ShowPreviewOverlay()
    {
        SetWebViewVisible(false);
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
                PlacementTarget = previewAreaGrid,
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
        SetWebViewVisible(true);
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
            if (_isRetargetMode)
            {
                if (node.Id != _retargetSourceNode?.Id && !node.IsRoot && !node.IsRedirect)
                    ApplyRetarget(_retargetSourceNode!, node);
                ExitRetargetMode();
                RenderComboGraph();
                return;
            }

            var now = DateTime.UtcNow;
            bool isDoubleClick = node.Id == _lastClickedNodeId && (now - _lastClickTime).TotalMilliseconds < 400;
            _lastClickedNodeId = node.Id;
            _lastClickTime = now;

            if (isDoubleClick)
            {
                _dragCandidate = false;
                _dragNodeId = -1;
                if (node.IsRedirect && node.ResolvedRedirectNodeId >= 0 && _comboGraph != null)
                {
                    var targetNode = _comboGraph.Nodes.FirstOrDefault(n => n.Id == node.ResolvedRedirectNodeId);
                    if (targetNode != null && _nodeBorders.TryGetValue(targetNode.Id, out var targetBorder))
                    {
                        SelectNode(targetBorder, targetNode);
                        if (_nodePositions.TryGetValue(targetNode.Id, out var targetPos))
                        {
                            var viewportW = comboBorder.ActualWidth;
                            var viewportH = comboBorder.ActualHeight;
                            _comboTranslate.X = viewportW / 2 - targetPos.X - NODE_WIDTH / 2;
                            _comboTranslate.Y = viewportH / 2 - targetPos.Y - NODE_HEIGHT / 2;
                        }
                        txtStatus.Text = $"Jumped to redirect target: {targetNode.DisplayName}";
                        if (!string.IsNullOrEmpty(targetNode.AnimPath) || !string.IsNullOrEmpty(targetNode.DefaultDBPath))
                            SetSourceTypeDisplay(MoveInfo.SourceTypeFromPath(ComboNodeTypePath(targetNode)));
                        if (!string.IsNullOrEmpty(targetNode.AnimPath))
                        {
                            txtStatus.Text = $"Loading: {targetNode.DisplayName} ({targetNode.AnimPath})...";
                            ShowPreviewOverlay();
                            try
                            {
                                string previewChar = "MainChar";
                                if (!string.Equals(_activeStance, "MainChar", StringComparison.OrdinalIgnoreCase))
                                    previewChar = _activeStance.Split('|')[0];
                                else if (targetNode.AnimPath.Contains("/"))
                                {
                                    var segs = targetNode.AnimPath.Split('/');
                                    var idx = Array.FindIndex(segs, s => s.Equals("Animations", StringComparison.OrdinalIgnoreCase));
                                    if (idx >= 0 && idx + 1 < segs.Length) previewChar = segs[idx + 1];
                                }
                                await LoadMeshAsync(previewChar);
                                await LoadAnimationAsync(targetNode.AnimPath);
                            }
                            catch (Exception ex)
                            {
                                ErrorLog.Write("REDIRECT_DBLCLK", new Exception($"Node '{targetNode.DisplayName}' (Path={targetNode.AnimPath}): {ex.Message}"));
                                txtStatus.Text = $"Error loading {targetNode.DisplayName}: {ex.Message}";
                            }
                        }
                        else
                        {
                            txtStatus.Text = $"Target '{targetNode.DisplayName}' has no animation path";
                        }
                        if (targetNode.Name == "MainChar_Stance")
                            ShowToast("Combat Stance — not in library");
                        else
                            await FindCardInLibrary(targetNode.AnimPath, targetNode);
                        return;
                    }
                }
                if (node.Name == "MainChar_Stance")
                    ShowToast("Combat Stance — not in library");
                else
                    await FindCardInLibrary(node.AnimPath, node);
                return;
            }

            if (_isResetMode)
            {
                ResetNodeToVanilla(node);
                return;
            }

            ClearMoveSelection();
            SelectNode(border, node);

            var typePath = ComboNodeTypePath(node);
            if (!string.IsNullOrEmpty(typePath))
                SetSourceTypeDisplay(MoveInfo.SourceTypeFromPath(typePath));
            else
                SetSourceTypeDisplay("Other");

            txtDisplayName.Text = node.IsRedirect
                ? $"Redirect → {(_comboGraph?.Nodes.FirstOrDefault(n => n.Id == node.ResolvedRedirectNodeId)?.DisplayName ?? "?")}"
                : !string.IsNullOrEmpty(node.ImportedDisplayName) &&
                  !string.Equals(node.ImportedDisplayName, node.DisplayName, StringComparison.OrdinalIgnoreCase)
                    ? $"Display: {node.DisplayName} → Changed: {node.ImportedDisplayName}"
                    : $"Display: {node.DisplayName}";

            if (!string.IsNullOrEmpty(node.AnimPath))
            {
                txtStatus.Text = $"Loading: {node.DisplayName} ({node.AnimPath})...";
                ShowPreviewOverlay();
                try
                {
                    string previewChar = "MainChar";
                    if (!string.Equals(_activeStance, "MainChar", StringComparison.OrdinalIgnoreCase))
                        previewChar = _activeStance.Split('|')[0];
                    else if (!string.IsNullOrEmpty(node.AnimPath) && node.AnimPath.Contains("/"))
                    {
                        // fallback: infer from anim path containing arch name
                        var segs = node.AnimPath.Split('/');
                        var idx = Array.FindIndex(segs, s=> s.Equals("Animations", StringComparison.OrdinalIgnoreCase));
                        if (idx>=0 && idx+1 < segs.Length) previewChar = segs[idx+1];
                    }
                    await LoadMeshAsync(previewChar);
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

    private void RetargetRedirect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is ComboNode redirectNode && redirectNode.IsRedirect)
        {
            _isRetargetMode = true;
            _retargetSourceNode = redirectNode;
            txtStatus.Text = "Retarget mode: click an attack node to retarget, Escape to cancel";
        }
    }

    private void ApplyRetarget(ComboNode redirectNode, ComboNode newTarget)
    {
        if (_comboGraph == null) return;

        var oldEdge = _comboGraph.Edges.FirstOrDefault(e => e.FromNodeId == redirectNode.Id && e.IsRedirect);
        if (oldEdge != null) _comboGraph.Edges.Remove(oldEdge);

        _comboGraph.Edges.Add(new ComboEdge
        {
            FromNodeId = redirectNode.Id,
            ToNodeId = newTarget.Id,
            InputName = oldEdge?.InputName ?? "",
            IsRedirect = true
        });

        redirectNode.ResolvedRedirectNodeId = newTarget.Id;

        RenderComboGraph();
        txtStatus.Text = $"Retargeted '{redirectNode.DisplayName}' → '{newTarget.DisplayName}'";
    }

    private void ExitRetargetMode()
    {
        _isRetargetMode = false;
        _retargetSourceNode = null;
    }


    private void RetargetNode_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border border || border.Tag is not ComboNode node) return;
        if (_isRetargetMode)
        {
            if (node.Id == _retargetSourceNode?.Id || node.IsRedirect) return;
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1));
            border.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xa6, 0xe3, 0xa1));
        }
    }

    private void RetargetNode_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border border || border.Tag is not ComboNode node) return;
        if (_selectedNodeId == node.Id)
        {
            SelectNode(border, node);
        }
        else
        {
            border.BorderBrush = GetNodeColor(node);
            border.Background = GetNodeBackground(node);
        }
    }

    private async void CmbStance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string stance = "";
        if (cmbStance.SelectedItem is ComboBoxItem cbi && cbi.Tag is string t) stance = t;
        else if (cmbStance.SelectedItem is string s) stance = s;
        if (string.IsNullOrEmpty(stance) || stance == _activeStance) return;
        if (stance == "MainChar")
        {
            await SwitchStanceAsync(stance);
            return;
        }
        await LoadArchetypeGraphAsync(stance);
    }

    private void BtnChangeUnit_Click(object sender, RoutedEventArgs e)
    {
        if (unitPopupBorder.Visibility == Visibility.Visible)
        {
            unitPopupBorder.Visibility = Visibility.Collapsed;
            SetWebViewVisible(true);
            return;
        }
        PopulateUnitCards();
        SetWebViewVisible(false);
        unitPopupBorder.Visibility = Visibility.Visible;
    }

    private void UnitPopupBackground_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == unitPopupBorder)
        {
            unitPopupBorder.Visibility = Visibility.Collapsed;
            SetWebViewVisible(true);
        }
    }

    private void UpdateVariantButtonVisibility()
    {
        var hasVariants = GetVariantsForArchetype(_activeStance.Split('|')[0]).Length > 0;
        btnVariant.Visibility = hasVariants ? Visibility.Visible : Visibility.Collapsed;
        if (!hasVariants) { _activeVariant = null; variantPopupBorder.Visibility = Visibility.Collapsed; SetWebViewVisible(true); }
    }

    private void BtnVariant_Click(object sender, RoutedEventArgs e)
    {
        if (variantPopupBorder.Visibility == Visibility.Visible)
        {
            variantPopupBorder.Visibility = Visibility.Collapsed;
            SetWebViewVisible(true);
            return;
        }
        PopulateVariantCards();
        SetWebViewVisible(false);
        variantPopupBorder.Visibility = Visibility.Visible;
    }

    private void VariantPopupBackground_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == variantPopupBorder)
        {
            variantPopupBorder.Visibility = Visibility.Collapsed;
            SetWebViewVisible(true);
        }
    }

    private void PopulateVariantCards()
    {
        string arch = _activeStance.Split('|')[0];
        variantPopupTitle.Text = $"Select {arch} Variant";
        variantWrapPanel.Children.Clear();

        var variants = GetVariantsForArchetype(arch);
        if (variants.Length == 0) { variantPopupTitle.Text = $"No variants for {arch}"; return; }

        foreach (var (name, tag) in variants)
        {
            var card = new Border
            {
                Width = 90, Height = 130, Background = new SolidColorBrush(Color.FromRgb(0x1e,0x1e,0x2e)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x58,0x5b,0x70)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Margin = new Thickness(6), Cursor = Cursors.Hand, Tag = tag
            };
            var stack = new StackPanel();
            var imgBorder = new Border{ Height=90, CornerRadius=new CornerRadius(6,6,0,0), Background=new SolidColorBrush(Color.FromRgb(0x31,0x32,0x44)), ClipToBounds=true };
            var initials = new TextBlock{ Text=name.Substring(0, Math.Min(2, name.Length)).ToUpper(), Foreground=Brushes.White, FontSize=28, FontWeight=FontWeights.Bold, HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(0,28,0,0)};
            imgBorder.Child = initials;
            var nameBlock = new Border{ Height=40, Background=new SolidColorBrush(Color.FromRgb(0x31,0x32,0x44)), CornerRadius=new CornerRadius(0,0,6,6), Padding=new Thickness(4)};
            nameBlock.Child = new TextBlock{ Text=name, Foreground=new SolidColorBrush(Color.FromRgb(0xcd,0xd6,0xf4)), FontSize=10, FontWeight=FontWeights.SemiBold, TextAlignment=TextAlignment.Center, VerticalAlignment=VerticalAlignment.Center, TextWrapping=TextWrapping.Wrap};
            stack.Children.Add(imgBorder);
            stack.Children.Add(nameBlock);
            card.Child = stack;
            string variantTag = tag;
            card.MouseLeftButtonDown += async (s, ev) =>
            {
                variantPopupBorder.Visibility = Visibility.Collapsed;
                SetWebViewVisible(true);
                if (variantTag == _activeVariant) return;
                SaveCurrentUnitToCache();
                await LoadVariantComboGraphAsync(variantTag, saveCurrent: false);
            };
            if (tag == _activeVariant)
            {
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x89,0xb4,0xfa));
                card.BorderThickness = new Thickness(2);
            }
            variantWrapPanel.Children.Add(card);
        }
    }

    private void UpdateWeaponButtonVisibility()
    {
        var arch = _activeStance?.Split('|')[0];
        if (string.IsNullOrEmpty(arch))
        {
            btnWeaponVariant.Visibility = Visibility.Collapsed;
            UpdateUnitPropertiesButtonVisibility();
            return;
        }
        var hasWeapons = GetWeaponVariantsForArchetype(arch).Length > 0;
        btnWeaponVariant.Visibility = hasWeapons ? Visibility.Visible : Visibility.Collapsed;
        if (!hasWeapons)
        {
            _activeWeapon = null;
            weaponPopupBorder.Visibility = Visibility.Collapsed;
        }
        UpdateUnitPropertiesButtonVisibility();
    }

    private void UpdateUnitPropertiesButtonVisibility()
    {
        var arch = _activeStance?.Split('|')[0] ?? "";
        bool isMainChar = string.Equals(arch, "MainChar", StringComparison.OrdinalIgnoreCase);
        btnUnitProperties.Visibility = isMainChar ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BtnWeaponVariant_Click(object sender, RoutedEventArgs e)
    {
        if (weaponPopupBorder.Visibility == Visibility.Visible)
        {
            weaponPopupBorder.Visibility = Visibility.Collapsed;
            SetWebViewVisible(true);
            return;
        }
        PopulateWeaponCards();
        SetWebViewVisible(false);
        weaponPopupBorder.Visibility = Visibility.Visible;
    }

    private void WeaponPopupBackground_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == weaponPopupBorder)
        {
            weaponPopupBorder.Visibility = Visibility.Collapsed;
            SetWebViewVisible(true);
        }
    }

    private void PopulateWeaponCards()
    {
        string arch = _activeStance.Split('|')[0];
        weaponPopupTitle.Text = $"Select {arch} Weapon";
        weaponWrapPanel.Children.Clear();

        var weapons = GetWeaponVariantsForArchetype(arch);
        if (weapons.Length == 0) { weaponPopupTitle.Text = $"No weapons for {arch}"; return; }

        foreach (var (name, tag) in weapons)
        {
            var card = new Border
            {
                Width = 90, Height = 130, Background = new SolidColorBrush(Color.FromRgb(0x1e,0x1e,0x2e)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x58,0x5b,0x70)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Margin = new Thickness(6), Cursor = Cursors.Hand, Tag = tag
            };
            var stack = new StackPanel();
            var imgBorder = new Border{ Height=90, CornerRadius=new CornerRadius(6,6,0,0), Background=new SolidColorBrush(Color.FromRgb(0x31,0x32,0x44)), ClipToBounds=true };
            var initials = new TextBlock{ Text=name.Substring(0, Math.Min(2, name.Length)).ToUpper(), Foreground=Brushes.White, FontSize=28, FontWeight=FontWeights.Bold, HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(0,28,0,0)};
            imgBorder.Child = initials;
            var nameBlock = new Border{ Height=40, Background=new SolidColorBrush(Color.FromRgb(0x31,0x32,0x44)), CornerRadius=new CornerRadius(0,0,6,6), Padding=new Thickness(4)};
            nameBlock.Child = new TextBlock{ Text=name, Foreground=new SolidColorBrush(Color.FromRgb(0xcd,0xd6,0xf4)), FontSize=10, FontWeight=FontWeights.SemiBold, TextAlignment=TextAlignment.Center, VerticalAlignment=VerticalAlignment.Center, TextWrapping=TextWrapping.Wrap};
            stack.Children.Add(imgBorder);
            stack.Children.Add(nameBlock);
            card.Child = stack;
            string weaponTag = tag;
            card.MouseLeftButtonDown += async (s, ev) =>
            {
                weaponPopupBorder.Visibility = Visibility.Collapsed;
                SetWebViewVisible(true);
                bool alreadyActive = string.Equals(weaponTag, _activeWeapon, StringComparison.OrdinalIgnoreCase)
                    || (IsActiveBarehands(arch, weaponTag) && string.IsNullOrEmpty(_activeWeapon));
                if (alreadyActive) return;
                await LoadWeaponComboGraphAsync(weaponTag);
            };
            bool highlight = string.Equals(tag, _activeWeapon, StringComparison.OrdinalIgnoreCase)
                || (IsActiveBarehands(arch, tag) && string.IsNullOrEmpty(_activeWeapon));
            if (highlight)
            {
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa));
                card.BorderThickness = new Thickness(2);
            }
            weaponWrapPanel.Children.Add(card);
        }
    }

    private async Task LoadWeaponComboGraphAsync(string weaponTag)
    {
        ClearNodeSelection();
        string arch = _activeStance.Split('|')[0];
        string variantForPath = _activeVariant ?? $"{arch}_Base";

        if (arch == "MainChar")
            variantForPath = weaponTag;

        var comboPath = ResolveWeaponComboPath(arch, variantForPath, weaponTag);
        if (comboPath == null && weaponTag.Contains("Barehands"))
            comboPath = ResolveComboFilePath(variantForPath);
        if (comboPath == null) { txtStatus.Text = $"No weapon combo for {weaponTag}"; return; }

        SaveCurrentUnitToCache();
        _currentUnitProps = null;
        _activeWeapon = NormalizeWeaponForCache(arch, weaponTag);

        string cacheKey = GetUnitCacheKey();
        if (TryRestoreUnitFromCache(cacheKey))
        {
            UpdateWeaponButtonVisibility();
            _comboTranslate.X = 0;
            _comboTranslate.Y = 0;
            return;
        }

        if (IsActiveBarehands(arch, weaponTag))
        {
            var barehandsParts = new List<string> { arch };
            if (!string.IsNullOrEmpty(_activeVariant)) barehandsParts.Add(_activeVariant);
            string barehandsKey = string.Join("|", barehandsParts);
            if (!string.Equals(barehandsKey, cacheKey, StringComparison.OrdinalIgnoreCase)
                && TryRestoreUnitFromCache(barehandsKey))
            {
                _activeWeapon = NormalizeWeaponForCache(arch, weaponTag);
                UpdateWeaponButtonVisibility();
                _comboTranslate.X = 0;
                _comboTranslate.Y = 0;
                return;
            }
        }

        ErrorLog.Write("WEAPON", new Exception($"Cache miss for {cacheKey} (imported keys: {string.Join(", ", _unitCaches.Keys)}). Loading vanilla {comboPath}"));

        graphLoadingText.Text = $"Loading {arch} {weaponTag}...";
        graphLoadingBorder.Visibility = Visibility.Visible;
        comboCanvas.IsHitTestVisible = false;
        try
        {
            var graph = await Task.Run(() => _parser.LoadComboTreeFromPath(comboPath, arch == "MainChar" ? weaponTag : arch));
            if (graph == null || graph.Nodes.Count == 0)
            {
                txtStatus.Text = $"{weaponTag}: no combo data at {comboPath}";
                return;
            }
            _comboGraph = graph;
            _activeWeapon = NormalizeWeaponForCache(arch, weaponTag);
            UpdateWeaponButtonVisibility();
            txtComboInfo.Text = $"{arch} {weaponTag} - {graph.Nodes.Count} nodes";
            _comboTranslate.X = 0;
            _comboTranslate.Y = 0;
            _nodePositions.Clear();
            LayoutComboGraph();
            RenderComboGraph();
            txtStatus.Text = $"Loaded {arch} {weaponTag} ({graph.Nodes.Count} nodes)";
            UpdateUnitPropertiesButtonVisibility();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("WEAPON", ex);
            txtStatus.Text = $"Error loading {weaponTag}: {ex.Message}";
        }
        finally
        {
            graphLoadingBorder.Visibility = Visibility.Collapsed;
            comboCanvas.IsHitTestVisible = true;
        }
    }

    private static (string name, string tag)[] GetVariantsForArchetype(string arch) => arch switch
    {
        "Yang" => new[]{ ("Phase 1","Yang_P1"), ("Phase 2","Yang_P2"), ("Phase 3","Yang_P3") },
        "Sean" => new[]{ ("Phase 1","Sean_P1"), ("Phase 2","Sean_P2"), ("Burst","Sean_Burst") },
        "Kuroki" => new[]{ ("Phase 1","Kuroki_P1"), ("Phase 2","Kuroki_P2") },
        "Fengjie" => new[]{ ("Phase 1","Fengjie_P1"), ("Phase 2","Fengjie_P2") },
        "Fajar" => new[]{ ("Phase 1","Fajar_P1"), ("Phase 2","Fajar_P2") },
        "Grunt" => new[]{ ("Base","Grunt_Base"), ("Advanced","Grunt_Advanced"), ("Miniboss","Grunt_Miniboss") },
        "BigGuy" => new[]{ ("Base","BigGuy_Base"), ("Advanced","BigGuy_Advanced"), ("Miniboss","BigGuy_Miniboss") },
        "BodyGuard" => new[]{ ("Base","Bodyguard_Base"), ("Advanced","Bodyguard_Advanced"), ("Miniboss","Bodyguard_Miniboss") },
        "FlashKick" => new[]{ ("Base","FlashKick_Base"), ("Advanced","FlashKick_Advanced"), ("Miniboss","FlashKick_Miniboss") },
        "FireDisciple" => new[]{ ("Base","FD_Base"), ("Advanced","FD_Advanced"), ("Miniboss","FD_Miniboss") },
        "Servant" => new[]{ ("Default","Servant") },
        "Sifu" => new[]{ ("Default","Sifu") },
        _ => Array.Empty<(string, string)>()
    };

    private static (string name, string tag)[] GetWeaponVariantsForArchetype(string arch) => arch switch
    {
        "MainChar" => new[]{ ("Barehands","MainChar_Barehands"), ("Bat","MainChar_Bat"), ("Staff","MainChar_Staff"), ("Blade","MainChar_Blade") },
        "Grunt" => new[]{ ("Barehands","Grunt_Barehands"), ("Bat","Grunt_Bat"), ("Blade","Grunt_Blade") },
        "FireDisciple" => new[]{ ("Barehands","FD_Barehands"), ("Staff","FD_Staff") },
        _ => Array.Empty<(string, string)>()
    };

    private static string? ResolveWeaponComboPath(string arch, string variantTag, string weaponTag) => (arch, weaponTag) switch
    {
        ("MainChar", "MainChar_Barehands") => "Game/DB/_MainChar/Combos/MainChar_ComboTree",
        ("MainChar", "MainChar_Bat") => "Game/DB/_MainChar/Combos/Attacks/Weapons/Bats/MainChar_Bats_ComboTree",
        ("MainChar", "MainChar_Staff") => "Game/DB/_MainChar/Combos/Attacks/Weapons/Staff/MainChar_Staff_ComboTree",
        ("MainChar", "MainChar_Blade") => "Game/DB/_MainChar/Combos/Attacks/Weapons/Blades/MainChar_Blades_ComboTree",
        ("Grunt", "Grunt_Bat") => variantTag switch
        {
            "Grunt_Advanced" => "Game/DB/AI/Archetypes/Grunt/_Advanced/Grunt_Advanced_BatCombo",
            "Grunt_Miniboss" => "Game/DB/AI/Archetypes/Grunt/_Miniboss/Grunt_Miniboss_BatCombo",
            _ => "Game/DB/AI/Archetypes/Grunt/_Base/Grunt_Base_BatCombo"
        },
        ("Grunt", "Grunt_Blade") => variantTag switch
        {
            "Grunt_Advanced" => "Game/DB/AI/Archetypes/Grunt/_Advanced/Grunt_Advanced_BladeCombo",
            "Grunt_Miniboss" => "Game/DB/AI/Archetypes/Grunt/_Miniboss/Grunt_Miniboss_BladeCombo",
            _ => "Game/DB/AI/Archetypes/Grunt/_Base/Grunt_Base_BladeCombo"
        },
        ("FireDisciple", "FD_Staff") => variantTag switch
        {
            "FD_Advanced" => "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Advanced_StaffCombo",
            "FD_Miniboss" => "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_MiniBoss_StaffCombo",
            _ => "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Base_StaffCombo"
        },
        _ => null
    };

    private sealed record ImportComboEntry(
        string UnitKey,
        string GamePath,
        string WeaponName,
        string? ActiveVariant,
        string? ActiveWeapon);

    private static Dictionary<string, ImportComboEntry> BuildImportComboCatalog()
    {
        var cat = new Dictionary<string, ImportComboEntry>(StringComparer.OrdinalIgnoreCase);

        void MC(string basename, string weaponTag, string gamePath) =>
            cat[basename] = new ImportComboEntry($"MainChar|{weaponTag}", gamePath, weaponTag, null, weaponTag);

        void EN(string basename, string arch, string variant, string gamePath, string? weapon = null) =>
            cat[basename] = new ImportComboEntry(
                weapon != null ? $"{arch}|{variant}|{weapon}" : $"{arch}|{variant}",
                gamePath, arch, variant, weapon);

        MC("MainChar_ComboTree", "MainChar_Barehands", "Game/DB/_MainChar/Combos/MainChar_ComboTree");
        MC("MainChar_Bats_ComboTree", "MainChar_Bat", "Game/DB/_MainChar/Combos/Attacks/Weapons/Bats/MainChar_Bats_ComboTree");
        MC("MainChar_Staff_ComboTree", "MainChar_Staff", "Game/DB/_MainChar/Combos/Attacks/Weapons/Staff/MainChar_Staff_ComboTree");
        MC("MainChar_Blades_ComboTree", "MainChar_Blade", "Game/DB/_MainChar/Combos/Attacks/Weapons/Blades/MainChar_Blades_ComboTree");

        EN("Yang_P1_Combo", "Yang", "Yang_P1", "Game/DB/AI/Archetypes/Yang/_DB/Phase1/Yang_P1_Combo");
        EN("Yang_P2_Combo", "Yang", "Yang_P2", "Game/DB/AI/Archetypes/Yang/_DB/Phase2/Yang_P2_Combo");
        EN("Yang_P3_Combo", "Yang", "Yang_P3", "Game/DB/AI/Archetypes/Yang/_DB/Phase3/Yang_P3_Combo");
        EN("Sean_Combo_Phase1", "Sean", "Sean_P1", "Game/DB/AI/Archetypes/Sean/Sean_Combo_Phase1");
        EN("Sean_Combo_Phase2", "Sean", "Sean_P2", "Game/DB/AI/Archetypes/Sean/Sean_Combo_Phase2");
        EN("Sean_BurstCombo", "Sean", "Sean_Burst", "Game/DB/AI/Archetypes/Sean/Sean_BurstCombo");
        EN("Kuroki_ComboPhase1_NEW", "Kuroki", "Kuroki_P1", "Game/DB/AI/Archetypes/Kuroki/Kuroki_ComboPhase1_NEW");
        EN("Kuroki_ComboPhase2_Shiroizu", "Kuroki", "Kuroki_P2", "Game/DB/AI/Archetypes/Kuroki/Kuroki_ComboPhase2_Shiroizu");
        EN("Fengjie_Phase1_Combo", "Fengjie", "Fengjie_P1", "Game/DB/AI/Archetypes/Fengjie/Phase1/Fengjie_Phase1_Combo");
        EN("Fengjie_Phase2_Combo", "Fengjie", "Fengjie_P2", "Game/DB/AI/Archetypes/Fengjie/Phase2/Fengjie_Phase2_Combo");
        EN("Fajar_Combo_P1", "Fajar", "Fajar_P1", "Game/DB/AI/Archetypes/Fajar/Attacks/Fajar_Combo_P1");
        EN("Fajar_Combo_P2", "Fajar", "Fajar_P2", "Game/DB/AI/Archetypes/Fajar/Attacks/Fajar_Combo_P2");
        EN("Grunt_Base_Combo", "Grunt", "Grunt_Base", "Game/DB/AI/Archetypes/Grunt/_Base/Grunt_Base_Combo");
        EN("Grunt_Advanced_Combo", "Grunt", "Grunt_Advanced", "Game/DB/AI/Archetypes/Grunt/_Advanced/Grunt_Advanced_Combo");
        EN("Grunt_Miniboss_Combo", "Grunt", "Grunt_Miniboss", "Game/DB/AI/Archetypes/Grunt/_Miniboss/Grunt_Miniboss_Combo");
        EN("BigGuy_Base_Combo", "BigGuy", "BigGuy_Base", "Game/DB/AI/Archetypes/BigGuy/_MainGame/Generic/BigGuy_Base_Combo");
        EN("BigGuy_Advanced_Combo", "BigGuy", "BigGuy_Advanced", "Game/DB/AI/Archetypes/BigGuy/_MainGame/Generic/BigGuy_Advanced_Combo");
        EN("BigGuy_Miniboss_Combo", "BigGuy", "BigGuy_Miniboss", "Game/DB/AI/Archetypes/BigGuy/_MainGame/Generic/BigGuy_Miniboss_Combo");
        EN("Bodyguard_Base_Combo", "BodyGuard", "Bodyguard_Base", "Game/DB/AI/Archetypes/Bodyguard/_Base/Bodyguard_Base_Combo");
        EN("Bodyguard_Advanced_Combo", "BodyGuard", "Bodyguard_Advanced", "Game/DB/AI/Archetypes/Bodyguard/_Advanced/Bodyguard_Advanced_Combo");
        EN("Bodyguard_Miniboss_Combo", "BodyGuard", "Bodyguard_Miniboss", "Game/DB/AI/Archetypes/Bodyguard/_Miniboss/Bodyguard_Miniboss_Combo");
        EN("FlashKick_Base_Combo", "FlashKick", "FlashKick_Base", "Game/DB/AI/Archetypes/FlashKick/_MainGame/Generic/FlashKick_Base_Combo");
        EN("FlashKick_Advanced_Combo", "FlashKick", "FlashKick_Advanced", "Game/DB/AI/Archetypes/FlashKick/_MainGame/Generic/FlashKick_Advanced_Combo");
        EN("FlashKick_Miniboss_Combo", "FlashKick", "FlashKick_Miniboss", "Game/DB/AI/Archetypes/FlashKick/_MainGame/Generic/FlashKick_Miniboss_Combo");
        EN("FireDisciple_Base_Combo", "FireDisciple", "FD_Base", "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Base_Combo");
        EN("FireDisciple_Advanced_Combo", "FireDisciple", "FD_Advanced", "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Advanced_Combo");
        EN("FireDisciple_MiniBoss_Combo", "FireDisciple", "FD_Miniboss", "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_MiniBoss_Combo");
        EN("Grunt_Base_BatCombo", "Grunt", "Grunt_Base", "Game/DB/AI/Archetypes/Grunt/_Base/Grunt_Base_BatCombo", "Grunt_Bat");
        EN("Grunt_Base_BladeCombo", "Grunt", "Grunt_Base", "Game/DB/AI/Archetypes/Grunt/_Base/Grunt_Base_BladeCombo", "Grunt_Blade");
        EN("Grunt_Advanced_BatCombo", "Grunt", "Grunt_Advanced", "Game/DB/AI/Archetypes/Grunt/_Advanced/Grunt_Advanced_BatCombo", "Grunt_Bat");
        EN("Grunt_Advanced_BladeCombo", "Grunt", "Grunt_Advanced", "Game/DB/AI/Archetypes/Grunt/_Advanced/Grunt_Advanced_BladeCombo", "Grunt_Blade");
        EN("Grunt_Miniboss_BatCombo", "Grunt", "Grunt_Miniboss", "Game/DB/AI/Archetypes/Grunt/_Miniboss/Grunt_Miniboss_BatCombo", "Grunt_Bat");
        EN("Grunt_Miniboss_BladeCombo", "Grunt", "Grunt_Miniboss", "Game/DB/AI/Archetypes/Grunt/_Miniboss/Grunt_Miniboss_BladeCombo", "Grunt_Blade");
        EN("FireDisciple_Base_StaffCombo", "FireDisciple", "FD_Base", "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Base_StaffCombo", "FD_Staff");
        EN("FireDisciple_Advanced_StaffCombo", "FireDisciple", "FD_Advanced", "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Advanced_StaffCombo", "FD_Staff");
        EN("FireDisciple_MiniBoss_StaffCombo", "FireDisciple", "FD_Miniboss", "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_MiniBoss_StaffCombo", "FD_Staff");
        EN("Servant_Combo", "Servant", "Servant", "Game/DB/AI/Archetypes/Servant/Servant_Combo");
        EN("Sifu_Combo", "Sifu", "Sifu", "Game/DB/AI/Archetypes/Sifu/Sifu_Combo");

        return cat;
    }

    private static string GetArchFromVariant(string variantTag) => variantTag switch
    {
        "FD_Base" or "FD_Advanced" or "FD_Miniboss" => "FireDisciple",
        "Bodyguard_Base" or "Bodyguard_Advanced" or "Bodyguard_Miniboss" => "BodyGuard",
        "Sifu" => "Sifu",
        "Servant" => "Servant",
        _ when variantTag.StartsWith("Bodyguard", StringComparison.OrdinalIgnoreCase) => "BodyGuard",
        _ => variantTag.Split('_')[0]
    };

    private static string? ResolveComboFilePath(string variantTag) => variantTag switch
    {
        "MainChar_Barehands" => "Game/DB/_MainChar/Combos/MainChar_ComboTree",
        "MainChar_Bat" => "Game/DB/_MainChar/Combos/Attacks/Weapons/Bats/MainChar_Bats_ComboTree",
        "MainChar_Staff" => "Game/DB/_MainChar/Combos/Attacks/Weapons/Staff/MainChar_Staff_ComboTree",
        "MainChar_Blade" => "Game/DB/_MainChar/Combos/Attacks/Weapons/Blades/MainChar_Blades_ComboTree",
        "Yang_P1" => "Game/DB/AI/Archetypes/Yang/_DB/Phase1/Yang_P1_Combo",
        "Yang_P2" => "Game/DB/AI/Archetypes/Yang/_DB/Phase2/Yang_P2_Combo",
        "Yang_P3" => "Game/DB/AI/Archetypes/Yang/_DB/Phase3/Yang_P3_Combo",
        "Sean_P1" => "Game/DB/AI/Archetypes/Sean/Sean_Combo_Phase1",
        "Sean_P2" => "Game/DB/AI/Archetypes/Sean/Sean_Combo_Phase2",
        "Sean_Burst" => "Game/DB/AI/Archetypes/Sean/Sean_BurstCombo",
        "Kuroki_P1" => "Game/DB/AI/Archetypes/Kuroki/Kuroki_ComboPhase1_NEW",
        "Kuroki_P2" => "Game/DB/AI/Archetypes/Kuroki/Kuroki_ComboPhase2_Shiroizu",
        "Fengjie_P1" => "Game/DB/AI/Archetypes/Fengjie/Phase1/Fengjie_Phase1_Combo",
        "Fengjie_P2" => "Game/DB/AI/Archetypes/Fengjie/Phase2/Fengjie_Phase2_Combo",
        "Fajar_P1" => "Game/DB/AI/Archetypes/Fajar/Attacks/Fajar_Combo_P1",
        "Fajar_P2" => "Game/DB/AI/Archetypes/Fajar/Attacks/Fajar_Combo_P2",
        "Grunt_Base" => "Game/DB/AI/Archetypes/Grunt/_Base/Grunt_Base_Combo",
        "Grunt_Advanced" => "Game/DB/AI/Archetypes/Grunt/_Advanced/Grunt_Advanced_Combo",
        "Grunt_Miniboss" => "Game/DB/AI/Archetypes/Grunt/_Miniboss/Grunt_Miniboss_Combo",
        "BigGuy_Base" => "Game/DB/AI/Archetypes/BigGuy/_MainGame/Generic/BigGuy_Base_Combo",
        "BigGuy_Advanced" => "Game/DB/AI/Archetypes/BigGuy/_MainGame/Generic/BigGuy_Advanced_Combo",
        "BigGuy_Miniboss" => "Game/DB/AI/Archetypes/BigGuy/_MainGame/Generic/BigGuy_Miniboss_Combo",
        "Bodyguard_Base" => "Game/DB/AI/Archetypes/Bodyguard/_Base/Bodyguard_Base_Combo",
        "Bodyguard_Advanced" => "Game/DB/AI/Archetypes/Bodyguard/_Advanced/Bodyguard_Advanced_Combo",
        "Bodyguard_Miniboss" => "Game/DB/AI/Archetypes/Bodyguard/_Miniboss/Bodyguard_Miniboss_Combo",
        "FlashKick_Base" => "Game/DB/AI/Archetypes/FlashKick/_MainGame/Generic/FlashKick_Base_Combo",
        "FlashKick_Advanced" => "Game/DB/AI/Archetypes/FlashKick/_MainGame/Generic/FlashKick_Advanced_Combo",
        "FlashKick_Miniboss" => "Game/DB/AI/Archetypes/FlashKick/_MainGame/Generic/FlashKick_Miniboss_Combo",
        "FD_Base" => "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Base_Combo",
        "FD_Advanced" => "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Advanced_Combo",
        "FD_Miniboss" => "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_MiniBoss_Combo",
        "Grunt_Base_Bat" => "Game/DB/AI/Archetypes/Grunt/_Base/Grunt_Base_BatCombo",
        "Grunt_Base_Blade" => "Game/DB/AI/Archetypes/Grunt/_Base/Grunt_Base_BladeCombo",
        "Grunt_Advanced_Bat" => "Game/DB/AI/Archetypes/Grunt/_Advanced/Grunt_Advanced_BatCombo",
        "Grunt_Advanced_Blade" => "Game/DB/AI/Archetypes/Grunt/_Advanced/Grunt_Advanced_BladeCombo",
        "Grunt_Miniboss_Bat" => "Game/DB/AI/Archetypes/Grunt/_Miniboss/Grunt_Miniboss_BatCombo",
        "Grunt_Miniboss_Blade" => "Game/DB/AI/Archetypes/Grunt/_Miniboss/Grunt_Miniboss_BladeCombo",
        "FD_Base_Staff" => "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Base_StaffCombo",
        "FD_Advanced_Staff" => "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Advanced_StaffCombo",
        "FD_Miniboss_Staff" => "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_MiniBoss_StaffCombo",
        "Servant" => "Game/DB/AI/Archetypes/Servant/Servant_Combo",
        "Sifu" => "Game/DB/AI/Archetypes/Sifu/Sifu_Combo",
        _ => null
    };

    private async Task LoadVariantComboGraphAsync(string variantTag, bool saveCurrent = true)
    {
        ClearNodeSelection();
        var comboPath = ResolveComboFilePath(variantTag);
        if (comboPath == null) { txtStatus.Text = $"No combo file for {variantTag}"; return; }
        string arch = _activeStance.Split('|')[0];

        if (saveCurrent)
            SaveCurrentUnitToCache();
        _currentUnitProps = null;
        _activeWeapon = null;
        _activeVariant = variantTag;
        _activeStance = arch;

        string cacheKey = GetUnitCacheKey();
        if (TryRestoreUnitFromCache(cacheKey))
        {
            if (!string.IsNullOrEmpty(_activeWeapon) && !GetWeaponVariantsForArchetype(arch).Any(w => w.tag == _activeWeapon))
                _activeWeapon = null;
            UpdateVariantButtonVisibility();
            UpdateWeaponButtonVisibility();
            UpdateUnitPropertiesButtonVisibility();
            _comboTranslate.X = 0;
            _comboTranslate.Y = 0;
            return;
        }

        graphLoadingText.Text = $"Loading {arch} {variantTag}...";
        graphLoadingBorder.Visibility = Visibility.Visible;
        comboCanvas.IsHitTestVisible = false;
        try
        {
            var graph = await Task.Run(() => _parser.LoadComboTreeFromPath(comboPath, arch));
            if (graph == null || graph.Nodes.Count == 0)
            {
                txtStatus.Text = $"{variantTag}: no combo data found at {comboPath}";
                return;
            }
            _comboGraph = graph;
            if (!string.IsNullOrEmpty(_activeWeapon) && !GetWeaponVariantsForArchetype(arch).Any(w => w.tag == _activeWeapon))
                _activeWeapon = null;
            UpdateWeaponButtonVisibility();
            txtComboInfo.Text = $"{arch} {variantTag} - {graph.Nodes.Count} nodes";
            _comboTranslate.X = 0;
            _comboTranslate.Y = 0;
            _nodePositions.Clear();
            LayoutComboGraph();
            RenderComboGraph();
            txtStatus.Text = $"Loaded {arch} {variantTag} ({graph.Nodes.Count} nodes)";
            UpdateUnitPropertiesButtonVisibility();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("VARIANT", ex);
            txtStatus.Text = $"Error loading {variantTag}: {ex.Message}";
        }
        finally
        {
            graphLoadingBorder.Visibility = Visibility.Collapsed;
            comboCanvas.IsHitTestVisible = true;
        }
    }

    private void PopulateUnitCards()
    {
        var units = new[] { "MainChar","Yang","Sean","Kuroki","Fengjie","Fajar","Grunt","FireDisciple","FlashKick","BigGuy","BodyGuard","Servant","Sifu" };
        unitWrapPanel.Children.Clear();
        foreach (var arch in units)
        {
            var card = new Border
            {
                Width = 90, Height = 130, Background = new SolidColorBrush(Color.FromRgb(0x1e,0x1e,0x2e)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x58,0x5b,0x70)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Margin = new Thickness(6), Cursor = Cursors.Hand, Tag = arch
            };
            var stack = new StackPanel();
            var imgBorder = new Border{ Height=90, CornerRadius=new CornerRadius(6,6,0,0), Background=new SolidColorBrush(Color.FromRgb(0x31,0x32,0x44)), ClipToBounds=true };
            var initials = new TextBlock{ Text=arch.Substring(0, Math.Min(2, arch.Length)).ToUpper(), Foreground=Brushes.White, FontSize=28, FontWeight=FontWeights.Bold, HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(0,28,0,0)};
            imgBorder.Child = initials;
            var nameBlock = new Border{ Height=40, Background=new SolidColorBrush(Color.FromRgb(0x31,0x32,0x44)), CornerRadius=new CornerRadius(0,0,6,6), Padding=new Thickness(4)};
            nameBlock.Child = new TextBlock{ Text=arch, Foreground=new SolidColorBrush(Color.FromRgb(0xcd,0xd6,0xf4)), FontSize=10, FontWeight=FontWeights.SemiBold, TextAlignment=TextAlignment.Center, VerticalAlignment=VerticalAlignment.Center, TextWrapping=TextWrapping.Wrap};
            stack.Children.Add(imgBorder);
            stack.Children.Add(nameBlock);
            card.Child = stack;
            card.MouseLeftButtonDown += async (s, ev) =>
            {
                ClearNodeSelection();
                unitPopupBorder.Visibility = Visibility.Collapsed;
                SetWebViewVisible(true);
                string selArch = (string)((Border)s).Tag;
                if (selArch == _activeStance.Split('|')[0]) return;
                if (selArch == "MainChar")
                {
                    await SwitchStanceAsync(selArch);
                }
                else
                {
                    SaveCurrentUnitToCache();
                    _activeStance = selArch;
                    _activeVariant = null;
                    _activeWeapon = null;
                    UpdateVariantButtonVisibility();
                    UpdateWeaponButtonVisibility();
                    UpdateUnitPropertiesButtonVisibility();
                    var variants = GetVariantsForArchetype(selArch);
                    if (variants.Length > 0)
                        await LoadVariantComboGraphAsync(variants[0].tag, saveCurrent: false);
                }
            };
            string activeArch = _activeStance.Split('|')[0];
            if (arch == activeArch)
            {
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x89,0xb4,0xfa));
                card.BorderThickness = new Thickness(2);
            }
            unitWrapPanel.Children.Add(card);
        }
    }

    private void SetStanceSelection(string tag)
    {
        foreach (var item in cmbStance.Items)
            if (item is ComboBoxItem cbi && (cbi.Tag as string) == tag) { cmbStance.SelectedItem = item; return; }
    }

    private async Task SwitchStanceAsync(string stance)
    {
        ClearNodeSelection();
        var contentDir = Path.Combine(_contentPath, "Content");
        if (!Directory.Exists(contentDir))
        {
            ErrorLog.Write("STANCE", new Exception($"Content dir not found: {contentDir}"));
            return;
        }

        SaveCurrentUnitToCache();
        _currentUnitProps = null;

        graphLoadingText.Text = "Loading MainChar...";
        graphLoadingBorder.Visibility = Visibility.Visible;
        comboCanvas.IsHitTestVisible = false;
        btnChangeUnit.IsEnabled = false;

        // Release file handles that may lock BaseMovementDB (CUE4Parse FileProvider holds FileShare.None)
        try { _parser?.Dispose(); } catch { }
        GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(100);

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
                await CopyWithRetryAsync(vanillaDbSrc, backupDbSrc);
                if (File.Exists(vanillaDbExp))
                    await CopyWithRetryAsync(vanillaDbExp, backupDbExp);
            }

            if (_activeStance == "MainChar" && File.Exists(vanillaDbSrc))
            {
            }
            else if (File.Exists(backupDbSrc))
            {
                await CopyWithRetryAsync(backupDbSrc, vanillaDbSrc);
                if (File.Exists(backupDbExp))
                    await CopyWithRetryAsync(backupDbExp, vanillaDbExp);
            }

            await CopyWithRetryAsync(movementDbSrc, vanillaDbSrc);
            if (File.Exists(movementDbExp))
                await CopyWithRetryAsync(movementDbExp, vanillaDbExp);

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
                    await CopyWithRetryAsync(vanillaTransSrc, backupTransSrc);
                    if (File.Exists(vanillaTransExp))
                        await CopyWithRetryAsync(vanillaTransExp, backupTransExp);
                }

                if (_activeStance == "MainChar" && File.Exists(vanillaTransSrc))
                {
                }
                else if (File.Exists(backupTransSrc))
                {
                    await CopyWithRetryAsync(backupTransSrc, vanillaTransSrc);
                    if (File.Exists(backupTransExp))
                        await CopyWithRetryAsync(backupTransExp, vanillaTransExp);
                }

                if (File.Exists(transSrc))
                {
                    await CopyWithRetryAsync(transSrc, vanillaTransSrc);
                    if (File.Exists(transExp))
                        await CopyWithRetryAsync(transExp, vanillaTransExp);
                }
            }

            _activeStance = stance;
            _activeVariant = null;
            _activeWeapon = "MainChar_Barehands";
            UpdateVariantButtonVisibility();
            UpdateWeaponButtonVisibility();
            UpdateUnitPropertiesButtonVisibility();

            string cacheKey = GetUnitCacheKey();
            if (!TryRestoreUnitFromCache(cacheKey))
            {
                var freshParser = new AnimationParser();
                freshParser.Initialize(_contentPath, contentDir);
                freshParser.MountCustomIntoProvider(TempCustomMovesRoot);
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
                    _nodePositions.Clear();
                    UpdateWeaponButtonVisibility();
                    LayoutComboGraph();
                    RenderComboGraph();
                    txtStatus.Text = $"Switched to {stance} stance";
                }
            }
            else
            {
                _comboTranslate.X = 0;
                _comboTranslate.Y = 0;
                txtComboInfo.Text = $"{stance} - {_comboGraph.WeaponName} ({_comboGraph.Nodes.Count} nodes, {_comboGraph.Edges.Count} edges)";
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("STANCE", ex);
            txtStatus.Text = $"Error switching stance: {ex.Message}";
            SetStanceSelection(_activeStance);
        }
        finally
        {
            // re-initialize provider that was disposed at start
            try { var fresh = new AnimationParser(); var cDir = Setup.ContentDetector.ResolveContentDir(_contentPath); fresh.Initialize(_contentPath, cDir); fresh.MountCustomIntoProvider(TempCustomMovesRoot); _parser = fresh; } catch { }
            graphLoadingBorder.Visibility = Visibility.Collapsed;
            comboCanvas.IsHitTestVisible = true;
            btnChangeUnit.IsEnabled = true;
        }
    }

    private async Task CopyWithRetryAsync(string src, string dst)
    {
        for (int i=0;i<5;i++)
        {
            try
            {
                using (var srcFs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dstFs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                    await srcFs.CopyToAsync(dstFs);
                return;
            }
            catch (IOException) when (i<4) { await Task.Delay(200); GC.Collect(); GC.WaitForPendingFinalizers(); }
        }
        using (var srcFs2 = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var dstFs2 = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            await srcFs2.CopyToAsync(dstFs2);
    }

    private async Task LoadArchetypeGraphAsync(string arch)
    {
        ClearNodeSelection();
        var parts = arch.Split('|');
        string baseArch = parts[0];
        string diff = parts.Length>1 ? parts[1] : "Normal";

        SaveCurrentUnitToCache();
        _currentUnitProps = null;
        _activeStance = arch;
        _activeWeapon = null;
        UpdateUnitPropertiesButtonVisibility();

        string cacheKey = GetUnitCacheKey();
        if (TryRestoreUnitFromCache(cacheKey))
        {
            _activeStance = arch;
            UpdateVariantButtonVisibility();
            UpdateWeaponButtonVisibility();
            UpdateUnitPropertiesButtonVisibility();
            _comboTranslate.X = 0;
            _comboTranslate.Y = 0;
            return;
        }

        graphLoadingText.Text = $"Loading {arch}...";
        graphLoadingBorder.Visibility = Visibility.Visible;
        comboCanvas.IsHitTestVisible = false;
        try
        {
            var graph = await Task.Run(() => _parser.LoadArchetypeAttackGraph(baseArch, diff));
            if (graph == null || graph.Nodes.Count == 0)
            {
                txtStatus.Text = $"{arch}: no attack data found";
                return;
            }
            _comboGraph = graph;
            UpdateVariantButtonVisibility();
            UpdateWeaponButtonVisibility();
            UpdateUnitPropertiesButtonVisibility();
            txtComboInfo.Text = $"{arch} - {graph.Nodes.Count} attacks (DataTable rows)";
            _comboTranslate.X = 0;
            _comboTranslate.Y = 0;
            _nodePositions.Clear();
            LayoutComboGraph();
            RenderComboGraph();
            txtStatus.Text = $"Loaded {arch} ({graph.Nodes.Count} attacks)";
        }
        catch (Exception ex)
        {
            ErrorLog.Write("ARCH", ex);
            txtStatus.Text = $"Error loading {arch}: {ex.Message}";
        }
        finally
        {
            graphLoadingBorder.Visibility = Visibility.Collapsed;
            comboCanvas.IsHitTestVisible = true;
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

    private void ApplyLocalChangeName(ComboNode node, string? newName)
    {
        var vanillaPath = !string.IsNullOrEmpty(node.VanillaAnimPath)
            ? node.VanillaAnimPath
            : node.DefaultAnimPath;
        if (!string.IsNullOrEmpty(vanillaPath) &&
            !string.Equals(node.AnimPath, vanillaPath, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(newName))
            node.ImportedDisplayName = newName;
        else
            node.ImportedDisplayName = "";
    }

    private void ApplyMoveSourceDbPath(ComboNode node, MoveInfo move)
    {
        if (IsAttackDbPath(move.FullPath ?? ""))
        {
            node.SourceDBPath = move.FullPath;
            return;
        }

        if (_parser.AnimToDbPath.TryGetValue(move.FullPath, out var srcDb))
            node.SourceDBPath = srcDb;
    }

    private void ResetNodeToVanilla(ComboNode node)
    {
        var vanillaPath = !string.IsNullOrEmpty(node.VanillaAnimPath)
            ? node.VanillaAnimPath
            : node.DefaultAnimPath;

        if (!string.IsNullOrEmpty(vanillaPath))
            node.AnimPath = vanillaPath;
        node.IsImportedFromMod = false;
        node.ImportedDisplayName = "";

        if (node.IsRedirect && _comboGraph != null
            && _comboGraph.RedirectOriginalTargets.TryGetValue(node.Id, out var origTreeIndex))
        {
            var origTarget = _comboGraph.Nodes.FirstOrDefault(n => n.TreeIndex == origTreeIndex);
            if (origTarget != null && node.ResolvedRedirectNodeId != origTarget.Id)
            {
                node.ResolvedRedirectNodeId = origTarget.Id;

                var oldEdge = _comboGraph.Edges.FirstOrDefault(e => e.FromNodeId == node.Id && e.IsRedirect);
                if (oldEdge != null) _comboGraph.Edges.Remove(oldEdge);

                string inputName = oldEdge?.InputName ?? "";
                if (_comboGraph.Edges.All(e => !(e.FromNodeId == node.Id && e.ToNodeId == origTarget.Id)))
                {
                    _comboGraph.Edges.Add(new ComboEdge
                    {
                        FromNodeId = node.Id,
                        ToNodeId = origTarget.Id,
                        InputName = inputName,
                        IsRedirect = true
                    });
                }
            }
        }

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

    private bool ProjectHasExistingChanges()
    {
        if (_unitCaches.Count > 0) return true;
        if (_isModLoaded) return true;
        if (_allMoves != null && _allMoves.Any(m =>
                string.Equals(m.Category, "Custom", StringComparison.OrdinalIgnoreCase)))
            return true;
        try
        {
            if (Directory.Exists(TempCustomMovesRoot) &&
                Directory.EnumerateFileSystemEntries(TempCustomMovesRoot).Any())
                return true;
        }
        catch { }
        return false;
    }

    private async System.Threading.Tasks.Task ResetProjectToVanillaAsync(string statusMessage)
    {
        _comboGraph = null;
        _unitCaches.Clear();
        _currentUnitProps = null;
        _activeVariant = null;
        _activeWeapon = null;
        _isModLoaded = false;
        _vanillaGraph = null;
        _moddedGraph = null;
        _nodeDiffs = new();
        ClearNodeSelection();
        UpdateUnitPropertiesButtonVisibility();

        try { _parser.SetOverlayProvider(null); } catch { }

        DeleteTempCustomMoves();

        if (_allMoves != null)
            _allMoves.RemoveAll(m => string.Equals(m.Category, "Custom", StringComparison.OrdinalIgnoreCase));

        try
        {
            var contentDir = Setup.ContentDetector.ResolveContentDir(_contentPath);
            var fresh = new AnimationParser();
            fresh.Initialize(_contentPath, contentDir);
            _parser = fresh;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("IMPORT", ex);
        }

        await SwitchStanceAsync("MainChar");
        FilterMoves();
        txtStatus.Text = statusMessage;
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Discard all changes and imported mods and reset the project to vanilla?",
            "New Project", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        await ResetProjectToVanillaAsync("New project — reset to vanilla");
    }

    private void ResetComboCamera_Click(object sender, RoutedEventArgs e)
    {
        _comboTranslate.X = 0;
        _comboTranslate.Y = 0;
        txtStatus.Text = "Combo graph camera reset";
    }

    private void ResetAllToVanilla_Click(object sender, RoutedEventArgs e)
    {
        if (_comboGraph == null) return;

        int resetCount = 0;
        foreach (var node in _comboGraph.Nodes)
        {
            if (node.IsRoot) continue;

            var vanillaPath = !string.IsNullOrEmpty(node.VanillaAnimPath)
                ? node.VanillaAnimPath
                : node.DefaultAnimPath;

            if (!string.IsNullOrEmpty(vanillaPath) && node.AnimPath != vanillaPath)
            {
                node.AnimPath = vanillaPath;
                node.ImportedDisplayName = "";
                node.IsImportedFromMod = false;
                resetCount++;
            }
            else if (node.AnimPath == vanillaPath && !string.IsNullOrEmpty(node.ImportedDisplayName))
            {
                node.ImportedDisplayName = "";
                node.IsImportedFromMod = false;
            }

            if (node.IsRedirect && _comboGraph.RedirectOriginalTargets.TryGetValue(node.Id, out var origTreeIndex))
            {
                var origTarget = _comboGraph.Nodes.FirstOrDefault(n => n.TreeIndex == origTreeIndex);
                if (origTarget != null && node.ResolvedRedirectNodeId != origTarget.Id)
                {
                    node.ResolvedRedirectNodeId = origTarget.Id;
                    var oldEdge = _comboGraph.Edges.FirstOrDefault(e2 => e2.FromNodeId == node.Id && e2.IsRedirect);
                    if (oldEdge != null) _comboGraph.Edges.Remove(oldEdge);
                    if (_comboGraph.Edges.All(e2 => !(e2.FromNodeId == node.Id && e2.ToNodeId == origTarget.Id)))
                    {
                        _comboGraph.Edges.Add(new ComboEdge
                        {
                            FromNodeId = node.Id,
                            ToNodeId = origTarget.Id,
                            InputName = oldEdge?.InputName ?? "",
                            IsRedirect = true
                        });
                    }
                    resetCount++;
                }
            }
        }

        RenderComboGraph();
        txtStatus.Text = resetCount > 0
            ? $"Reset {resetCount} nodes to vanilla"
            : "All nodes already at vanilla";
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

    private void ComboNode_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(MoveInfo))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
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
                    node.IsImportedFromMod = false;
                    if (node.Name == "MainChar_Stance" && !string.IsNullOrEmpty(move.Character)
                        && _stanceMap.ContainsKey(move.Character))
                    {
                        node.AnimPath = move.FullPath;
                        ApplyLocalChangeName(node, move.DisplayName);
                        RenderComboGraph();
                        txtStatus.Text = $"Combat Stance -> {move.Character} ({move.DisplayName})";
                    }
                    else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        var linkedNodes = _comboGraph.Nodes
                            .Where(n => n.DefaultAnimPath == node.DefaultAnimPath)
                            .ToList();
                        foreach (var ln in linkedNodes)
                        {
                            ln.IsImportedFromMod = false;
                            ln.AnimPath = move.FullPath;
                            ApplyLocalChangeName(ln, move.DisplayName);
                            ApplyMoveSourceDbPath(ln, move);
                        }
                        RenderComboGraph();
                        txtStatus.Text = $"Replaced {linkedNodes.Count} linked nodes -> {move.DisplayName}";
                    }
                    else
                    {
                        node.AnimPath = move.FullPath;
                        ApplyLocalChangeName(node, move.DisplayName);
                        ApplyMoveSourceDbPath(node, move);
                        RenderComboGraph();
                        txtStatus.Text = $"Replaced: {node.Name} -> {move.DisplayName}";
                    }

                    if (_nodeBorders.TryGetValue(node.Id, out var newNodeBorder))
                        PlayFusionAnimation(newNodeBorder);
                }
            }
            e.Handled = true;
            }
        }

    #region Unit Properties
    private void BtnUnitProperties_Click(object sender, RoutedEventArgs e)
    {
        if (unitPropsPopupBorder.Visibility == Visibility.Visible)
        {
            SaveUnitProperties();
            unitPropsPopupBorder.Visibility = Visibility.Collapsed;
            SetWebViewVisible(true);
            return;
        }
        PopulateUnitProperties();
        SetWebViewVisible(false);
        unitPropsPopupBorder.Visibility = Visibility.Visible;
    }

    private void UnitPropsPopupBackground_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == unitPropsPopupBorder)
        {
            SaveUnitProperties();
            unitPropsPopupBorder.Visibility = Visibility.Collapsed;
            SetWebViewVisible(true);
        }
    }

    private void ResetUnitProps_Click(object sender, RoutedEventArgs e)
    {
        if (_unitPropsDefaults == null) return;
        SetUnitPropsUI(_unitPropsDefaults);
    }

    private static readonly Dictionary<string, (string Title, string Desc, string? Range)> UnitPropsHelp = new()
    {
        ["Health"] = ("Health", "Unit's max HP. Depleted by attacks; when reduced to zero, the unit is defeated.", null),
        ["Structure"] = ("Structure", "Unit's posture/structure bar. When full, the unit is staggered and takes bonus damage. Resets over time.", null),
        ["MemoryLimit"] = ("Memory Limit", "Rolling observation window (seconds). Hits received within this period count toward the Hits Count threshold. After a defense triggers, the window resets. Longer windows mean old hits stay \"remembered\" longer. With Hits Count = 1, window length rarely matters.", null),
        ["HitsCount"] = ("Hits Count", "How many hits the enemy must receive within the Memory Limit window before it defends (parry, dodge, or avoid). At 1, the enemy defends on the very first hit. At 3, it \"absorbs\" 3 hits before reacting.", null),
        ["FlushLimit"] = ("Flush Limit", "Cooldown (seconds) after the enemy defends before it can defend again. Lower = more frequent defense cycles. At 0s, the enemy can defend again instantly. Combine with Hit Count = 0 for constant defense.", null),
    };

    private void UnitPropsInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string? key = btn.Tag as string;
        if (key == null || !UnitPropsHelp.TryGetValue(key, out var info)) return;

        unitPropsInfoTitle.Text = info.Title;
        unitPropsInfoDesc.Text = info.Desc;
        if (info.Range != null)
        {
            unitPropsInfoRangeValue.Text = info.Range;
            unitPropsInfoRange.Visibility = Visibility.Visible;
        }
        else
        {
            unitPropsInfoRange.Visibility = Visibility.Collapsed;
        }

        unitPropsInfoPopup.Visibility = Visibility.Visible;
    }

    private void UnitPropsInfoPopup_Click(object sender, MouseButtonEventArgs e)
    {
        unitPropsInfoPopup.Visibility = Visibility.Collapsed;
    }

    private void PopulateUnitProperties()
    {
        string arch = _activeStance?.Split('|')[0] ?? "";
        string variantTag = _activeVariant ?? $"{arch}_Base";

        if (_currentUnitProps == null || !UnitPropertiesManager.HasAnyValue(_currentUnitProps))
            _currentUnitProps = UnitPropertiesManager.Read(_contentPath, variantTag);
        _unitPropsDefaults = UnitPropertiesManager.Read(_contentPath, variantTag);

        unitPropsTitle.Text = $"Unit Properties \u2014 {variantTag}";

        SetField(txtUnitHealth, _currentUnitProps.Health?.ToString("F1"), _currentUnitProps.Health.HasValue);
        SetField(txtUnitStructure, _currentUnitProps.Structure?.ToString("F1"), _currentUnitProps.Structure.HasValue);
        SetField(txtMemoryLimit, _currentUnitProps.MemoryLimit?.ToString("F1"), _currentUnitProps.MemoryLimit.HasValue);
        SetField(txtHitsCount, _currentUnitProps.HitsCount?.ToString(), _currentUnitProps.HitsCount.HasValue);
        SetField(txtFlushLimit, _currentUnitProps.MemoryFlushLimit?.ToString("F1"), _currentUnitProps.MemoryFlushLimit.HasValue);
    }

    private void SaveUnitProperties()
    {
        if (_activeStance == "MainChar") return;

        string arch = _activeStance?.Split('|')[0] ?? "";
        string variantTag = _activeVariant ?? $"{arch}_Base";

        var props = new UnitProperties();

        if (txtUnitHealth.IsEnabled && float.TryParse(txtUnitHealth.Text, out float h)) props.Health = h;
        if (txtUnitStructure.IsEnabled && float.TryParse(txtUnitStructure.Text, out float s)) props.Structure = s;
        if (txtMemoryLimit.IsEnabled && float.TryParse(txtMemoryLimit.Text, out float ml)) props.MemoryLimit = ml;
        if (txtHitsCount.IsEnabled && int.TryParse(txtHitsCount.Text, out int hc)) props.HitsCount = hc;
        if (txtFlushLimit.IsEnabled && float.TryParse(txtFlushLimit.Text, out float fl)) props.MemoryFlushLimit = fl;

        _currentUnitProps = props;
        txtStatus.Text = $"Unit properties saved for {variantTag}";
    }

    private void SetUnitPropsUI(UnitProperties props)
    {
        SetField(txtUnitHealth, props.Health?.ToString("F1"), props.Health.HasValue);
        SetField(txtUnitStructure, props.Structure?.ToString("F1"), props.Structure.HasValue);
        SetField(txtMemoryLimit, props.MemoryLimit?.ToString("F1"), props.MemoryLimit.HasValue);
        SetField(txtHitsCount, props.HitsCount?.ToString(), props.HitsCount.HasValue);
        SetField(txtFlushLimit, props.MemoryFlushLimit?.ToString("F1"), props.MemoryFlushLimit.HasValue);
    }

    private void SetField(System.Windows.Controls.TextBox tb, string? value, bool available)
    {
        if (available && value != null)
        {
            tb.Text = value;
            tb.IsEnabled = true;
            tb.Foreground = new SolidColorBrush(Color.FromRgb(0xcd, 0xd6, 0xf4));
        }
        else
        {
            tb.Text = "N/A";
            tb.IsEnabled = false;
            tb.Foreground = new SolidColorBrush(Color.FromRgb(0x6c, 0x70, 0x86));
        }
    }
    #endregion
}

public class Settings
{
    public string ContentPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public bool ShowLines { get; set; } = true;
    public double[]? CameraPosition { get; set; }
    public double[]? CameraTarget { get; set; }
    public string UnrealPakPath { get; set; } = "";
    public string CryptoJsonPath { get; set; } = "";
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
