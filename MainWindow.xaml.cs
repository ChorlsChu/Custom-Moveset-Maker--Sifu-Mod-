using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SifuMovesetEditor;
using SifuMovesetEditor.Setup;
using SifuMovesetEditor.Export;
using SifuMovesetEditor.Import;
using SifuMovesetEditor.Custom;
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

    /// <summary>Total writes this session - callers snapshot it to surface new
    /// errors (an export must not fail silently in the UI while only the log knows).</summary>
    public static int Writes;

    /// <summary>
    /// Writes whose payload looks like a real problem (thrown exception, or a
    /// WARNING/FAIL/Error marker). The export routine logs its progress through
    /// this same log, so Writes alone overstates failures - the completion panel
    /// snapshots this counter instead.
    /// </summary>
    public static int Warnings;

    public static void Init()
    {
        try { File.WriteAllText(LogPath, ""); } catch { }
        Writes = 0;
        Warnings = 0;
    }

    public static void Write(string tag, Exception ex)
    {
        Interlocked.Increment(ref Writes);
        if (LooksLikeWarning(ex)) Interlocked.Increment(ref Warnings);
        try
        {
            var msg = $"[{DateTime.Now:HH:mm:ss}] [{tag}] {ex}\n";
            File.AppendAllText(LogPath, msg);
        }
        catch { }
    }

    public static void Write(string tag, string message)
    {
        Interlocked.Increment(ref Writes);
        if (MessageLooksLikeWarning(message)) Interlocked.Increment(ref Warnings);
        try
        {
            var msg = $"[{DateTime.Now:HH:mm:ss}] [{tag}] {message}\n";
            File.AppendAllText(LogPath, msg);
        }
        catch { }
    }

    /// <summary>Thrown exceptions carry a stack trace; progress lines are fresh
    /// Exception objects built for the log and never thrown.</summary>
    private static bool LooksLikeWarning(Exception ex)
        => ex.StackTrace != null || MessageLooksLikeWarning(ex.Message);

    private static readonly System.Text.RegularExpressions.Regex _warningMarker =
        new(@"\bWARNING\b|\bFAIL\b|\bError\b",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Word-boundary match: "FLOW WARNING:" counts, but progress lines that
    /// merely mention a count ("... 0 flow warnings ...") do not.</summary>
    private static bool MessageLooksLikeWarning(string? message)
        => message != null && _warningMarker.IsMatch(message);
}

public partial class MainWindow : Window
{
    private AnimationParser _parser = new();
    private List<MoveInfo> _allMoves = [];
    private List<MoveInfo> _allLocomotion = [];
    // Library mode switch: attacks (default) vs cross-unit AI action/event nodes.
    // The AI catalog scans combo trees lazily on first switch and is cached here.
    private List<AiNodeInfo> _aiNodes = [];
    private bool _aiLibraryMode;
    private bool _aiScanStarted;
    // CustomAssets manifest last accepted at close (settings.json) - drives the
    // "save custom files?" prompt and which files an answer of No may delete.
    private List<string>? _customAssetsAccepted;
    // Set while an import harvest writes into CustomAssets so its own file events
    // don't pop the "New Custom File(s)" dialog (import registers cards directly).
    private bool _suppressCustomWatcher;
    private FileSystemWatcher? _customWatcher;
    private DispatcherTimer? _customWatcherDebounce;
    private readonly HashSet<string> _customWatcherPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _customWatchRows = new(StringComparer.OrdinalIgnoreCase);
    private CustomFilesDetectedDialog? _customFilesDialog;
    private string _settingsPath;
    private string _contentPath = "";
    private string _outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExportedMods");
    // Source of truth for the "Show AI Actions & Events" setting (checkbox syncs from
    // this on Settings open so Cancel discards edits; behavior reads this field).
    private bool _showAiNodes;
    // Node ids hidden from the combo canvas while _showAiNodes is false (rebuilt by
    // RenderComboGraph; null = nothing hidden). Root nodes are never included.
    private HashSet<int>? _hiddenAiNodeIds;
    private bool _initialized = false;
    private ComboGraph? _comboGraph;
    private Dictionary<int, Point> _nodePositions = new();
    private const int UNDO_MAX = 50;
    private readonly List<UndoEntry> _undoStack = new();
    private readonly List<UndoEntry> _redoStack = new();
    private const double NODE_WIDTH = 140;
    private const double NODE_HEIGHT = 32;
    private const double INPUT_HEIGHT = 22;
    private const double H_SPACING = 80;
    private const double V_SPACING = 20;
    private const int MaxColumnRows = 14;

    private bool _isPanning;
    private bool _panMoved;
    private bool _canvasHitNode;
    private Point _panStart;
    private Point _panDownPos;
    private bool _keyW, _keyA, _keyS, _keyD;
    private DispatcherTimer _panTimer;
    private DateTime _lastPanTick = DateTime.UtcNow;
    private readonly DispatcherTimer _searchDebounceTimer;

    private readonly HashSet<string> _expandedEnemies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _expandedWeapons = new(StringComparer.OrdinalIgnoreCase);

    private Border? _selectedMoveBorder;
    private int _selectedNodeId = -1;
    private readonly HashSet<int> _chainNodeIds = new();
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
    // Link authoring mode: source selected via context menu ("Connect to…" / "Add Redirect
    // via new node…"), target picked by clicking a node, then an input picker menu opens.
    private ComboNode? _linkSourceNode = null;
    private bool _linkCreatesRedirect = false;
    private bool _linkPickCommitted = false;
    // Start Entries pick-on-graph mode: the dialog suspended itself (closing like
    // Cancel but carrying the live entries copy) so the user can click the target
    // node directly on the canvas, retarget-style, with a confirmation popup.
    private bool _isNodePickMode = false;
    private Action<ComboNode>? _pendingPickApply = null;
    private Action? _pendingPickResume = null;
    private ComboGraph? _pendingPickGraph = null;
    private int _pickCandidateNodeId = -1;
    private ComboGraph? _originalVanillaComboGraph;
    private string _activeStance = "MainChar";
    private string? _activeVariant;
    private string? _activeWeapon;
    private readonly Dictionary<string, List<MoveInfo>> _mainCharWeaponMoves = new();
    private readonly List<string> _treeRefMisses = new();
    private Dictionary<string, UnitCacheEntry> _unitCaches = new();
    private UnitProperties? _currentUnitProps;
    private string? _selectedModelOverride;
    private string? _selectedModelSet;
    private bool _modelPickerRosterMode;
    private UnitProperties? _unitPropsDefaults;
    private CustomMovesetInfo? _customMoveset;

    /// <summary>
    /// Unit properties keyed by variant tag only, so they survive weapon switches (whose cache key
    /// includes the weapon and would otherwise miss) and tier switches before a save.
    /// </summary>
    private readonly Dictionary<string, UnitProperties> _propsByVariant = new(StringComparer.OrdinalIgnoreCase);

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
        InputFilter.ApplyInt(txtAttackBuildup);
        InputFilter.ApplyFloat(txtAttackRange);
        InputFilter.ApplyInt(txtDelayWinLo);
        InputFilter.ApplyInt(txtDelayWinHi);
        InputFilter.ApplyFloat(txtUnitHealth);
        InputFilter.ApplyFloat(txtUnitStructure);
        InputFilter.ApplyFloat(txtMemoryLimit);
        InputFilter.ApplyInt(txtHitsCount);
        InputFilter.ApplyFloat(txtFlushLimit);
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
        MapTextureHost(webView.CoreWebView2);

        var viewerPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "viewer", "index.html");
        webView.CoreWebView2.Navigate(new Uri(viewerPath).AbsoluteUri);

        cmbSpeed.SelectedIndex = 2;

        _panTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _panTimer.Tick += PanTimer_Tick;
        _panTimer.Start();

        await LoadSettingsAsync();
        // Applies the AI visibility setting (library button) before first paint.
        ApplyShowAiNodes();
        if (!_initInProgress)
            loadingOverlay.Visibility = Visibility.Collapsed;
        _initialized = true;
        tabMoves.SelectedIndex = TabVanilla;
        StartCustomAssetsWatcher();
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
                    _customAssetsAccepted = settings.CustomAssetsAccepted;
                    // Applies on every branch below (including the repair-wizard path,
                    // which never syncs chkShowLines).
                    _showAiNodes = settings.ShowAiNodes;
                    chkShowAiNodes.IsChecked = _showAiNodes;
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

        return await ShowSetupWizardAsync(SetupMode.FirstRun, quitOnCancel: true);
    }

    private async Task<Settings> ShowSetupWizardAsync(
        SetupMode mode = SetupMode.FirstRun,
        string? repairReason = null,
        string? previousPath = null,
        bool quitOnCancel = false)
    {
        var wizard = new SetupWizard
        {
            Owner = this,
            Mode = mode,
            RepairReason = repairReason,
            PreviousContentPath = previousPath,
            QuitOnCancel = quitOnCancel,
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
            ShowAiNodes = _showAiNodes,
            CameraPosition = _savedCameraPos,
            CameraTarget = _savedCameraTarget,
            UnrealPakPath = Setup.ContentExtractor.CustomUnrealPakPath ?? "",
            CryptoJsonPath = Setup.ContentExtractor.CustomCryptoJsonPath ?? "",
            CustomAssetsAccepted = _customAssetsAccepted,
        };
        File.WriteAllText(_settingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
    }

    private void UpdateLoading(string text, string detail)
    {
        loadingText.Text = text;
        loadingDetail.Text = detail;
    }

    // Guards Save/Load against double-clicks during the overlay's delayed-show window.
    private bool _opInProgress;

    /// <summary>
    /// Runs a potentially slow operation behind the full-screen loading overlay. The
    /// overlay appears only if the work is still running after 250ms, so quick saves
    /// and loads don't flash it; hide in finally is idempotent for the fast path.
    /// </summary>
    private async Task RunWithLoadingAsync(string text, string detail, Func<Task> work)
        => await RunWithLoadingAsync<object?>(text, detail, async () => { await work(); return null; });

    private async Task<T> RunWithLoadingAsync<T>(string text, string detail, Func<Task<T>> work)
    {
        var workTask = work();
        var delayTask = Task.Delay(250);
        if (await Task.WhenAny(workTask, delayTask) == delayTask && !workTask.IsCompleted)
        {
            UpdateLoading(text, detail);
            loadingOverlay.Visibility = Visibility.Visible;
        }
        try
        {
            return await workTask;
        }
        finally
        {
            loadingOverlay.Visibility = Visibility.Collapsed;
        }
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

            UpdateLoading($"Building library ({_allMoves.Count} moves)...", "");
            await Task.Run(BuildMainCharLibraryMoves);
            await Task.Run(() =>
            {
                _parser.MountCustomIntoProvider(CustomAssets.Root);
                RegisterCustomMoves();
            });
            BuildTree(_allMoves);
            LogLibraryDiagnostics();

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
        // Reset the checkbox to the saved value so closing with Cancel discards edits.
        chkShowAiNodes.IsChecked = _showAiNodes;
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
        var res = new ConfirmDialog(
            "Confirm Reset",
            "Delete settings.json and vanilla game files in app directory to save space?\n\n" +
            "This will remove settings.json and folders: Vanilla, VanillaBackup, extractedPaks.\n\n" +
            "Setup will open so you can re-extract vanilla files (folder or pak).",
            "Reset",
            "Cancel")
        { Owner = this };
        if (res.ShowDialog() != true) return;
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
        bool aiVisibilityChanged = _showAiNodes != (chkShowAiNodes.IsChecked == true);
        _showAiNodes = chkShowAiNodes.IsChecked == true;
        SaveSettings();
        try { _parser?.Dispose(); var cDir = Setup.ContentDetector.ResolveContentDir(_contentPath); var fresh = new AnimationParser(); fresh.Initialize(_contentPath, cDir); fresh.MountCustomIntoProvider(CustomAssets.Root); _parser = fresh; } catch (Exception ex) { ErrorLog.Write("SETTINGS", ex); }
        bool rearranged = ApplyShowAiNodes(aiVisibilityChanged);
        settingsOverlay.Visibility = Visibility.Collapsed;
        SetWebViewVisible(true);
        txtStatus.Text = rearranged
            ? "Settings saved — provider re-initialized, graph re-arranged for AI visibility"
            : "Settings saved — provider re-initialized";
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
        if (_aiNodes.Count > 0)
            RefreshAiList();
        txtMoveCount.Text = _aiLibraryMode
            ? $"{CountAiActions()} actions / {CountAiEvents()} events"
            : $"{vanillaMoves.Count} vanilla / {unusedMoves.Count} other / {filteredLoco.Count} stances / {customMoves.Count} custom";
    }

    private int CountAiActions() => _aiNodes.Count(n => n.Kind == "Action");
    private int CountAiEvents() => _aiNodes.Count(n => n.Kind == "Event");

    private static readonly string[] MainCharWeaponOrder = ["BareHands", "Bat", "Staff", "Knife", "Special", "Special Combos"];

    private void BuildMainCharLibraryMoves()
    {
        _mainCharWeaponMoves.Clear();
        _treeRefMisses.Clear();
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
                else if (match == null)
                    _treeRefMisses.Add($"{label} node #{node.Id} \"{node.Name}\": " +
                        (string.IsNullOrEmpty(node.DefaultDBPath) ? node.DefaultAnimPath : node.DefaultDBPath));
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

        ErrorLog.Write("LIBRARY",
            $"MainChar library: {string.Join(", ", _mainCharWeaponMoves.Select(kv => $"{kv.Key}={kv.Value.Count}"))}");
        ErrorLog.Write("LIBRARY", $"tree refs with no library card: {_treeRefMisses.Count}");
        foreach (var miss in _treeRefMisses)
            ErrorLog.Write("LIBRARY", $"  missing card: {miss}");
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

    private static bool IsOldCard(MoveInfo m) =>
        (m.FullPath ?? "").Contains("/_old/", StringComparison.OrdinalIgnoreCase);

    private static string MainCharLabelForMove(MoveInfo m)
    {
        if (IsSpecialComboDb(m)) return "Special Combos";
        if (IsSpecialDb(m)) return "Special";
        var p = m.FullPath ?? "";
        if (p.Contains("/Throwable/", StringComparison.OrdinalIgnoreCase)) return "Throwable";
        var w = m.WeaponType ?? "";
        if (w.Equals("Bats", StringComparison.OrdinalIgnoreCase)) return "Bat";
        if (w.Equals("Blades", StringComparison.OrdinalIgnoreCase)) return "Knife";
        if (string.IsNullOrWhiteSpace(w)) return "BareHands";
        return w;
    }

    private List<object> BuildAccordionList(List<MoveInfo> moves, bool includeComboTreeMoves = true)
    {
        var result = new List<object>();

        if (includeComboTreeMoves)
        {
            var labelTree = new Dictionary<string, List<MoveInfo>>(StringComparer.OrdinalIgnoreCase);
            var labelRemainder = new Dictionary<string, List<MoveInfo>>(StringComparer.OrdinalIgnoreCase);
            var oldSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var oldCards = new List<MoveInfo>();

            void AddOld(MoveInfo m)
            {
                if (!string.IsNullOrEmpty(m.FullPath) && oldSeen.Add(m.FullPath))
                    oldCards.Add(m);
            }

            foreach (var kv in _mainCharWeaponMoves)
            {
                foreach (var m in kv.Value)
                {
                    if (IsOldCard(m)) { AddOld(m); continue; }
                    if (!labelTree.TryGetValue(kv.Key, out var list))
                    {
                        list = new List<MoveInfo>();
                        labelTree[kv.Key] = list;
                    }
                    if (!list.Contains(m)) list.Add(m);
                }
            }

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _mainCharWeaponMoves)
                foreach (var m in kv.Value)
                    if (!string.IsNullOrEmpty(m.FullPath))
                        claimed.Add(m.FullPath);

            foreach (var m in moves)
            {
                if (!string.Equals(m.Character, "MainChar", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsOldCard(m)) { AddOld(m); continue; }
                if (!string.IsNullOrEmpty(m.FullPath) && claimed.Contains(m.FullPath)) continue;

                var label = MainCharLabelForMove(m);
                if (!labelRemainder.TryGetValue(label, out var list))
                {
                    list = new List<MoveInfo>();
                    labelRemainder[label] = list;
                }
                if (!list.Contains(m)) list.Add(m);
            }

            var orderedLabels = MainCharWeaponOrder
                .Where(l => labelTree.ContainsKey(l) || labelRemainder.ContainsKey(l))
                .Concat(labelTree.Keys.Concat(labelRemainder.Keys)
                    .Where(k => !MainCharWeaponOrder.Contains(k, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                .ToList();

            int mainCharTotal = oldCards.Count;
            foreach (var label in orderedLabels)
            {
                if (labelTree.TryGetValue(label, out var t)) mainCharTotal += t.Count;
                if (labelRemainder.TryGetValue(label, out var r)) mainCharTotal += r.Count;
            }

            if (mainCharTotal > 0)
            {
                var mainCharExpanded = _expandedEnemies.Contains("MainChar");
                result.Add(new GroupHeader
                {
                    Name = "MainChar",
                    Level = 1,
                    Count = mainCharTotal,
                    Subtitle = "Combo tree defaults, then remaining vanilla cards",
                    IsExpanded = mainCharExpanded
                });

                if (mainCharExpanded)
                {
                    foreach (var label in orderedLabels)
                    {
                        var items = new List<MoveInfo>();
                        if (labelTree.TryGetValue(label, out var t)) items.AddRange(t);
                        if (labelRemainder.TryGetValue(label, out var r))
                            items.AddRange(r.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase));
                        if (items.Count == 0) continue;

                        var expanded = _expandedWeapons.TryGetValue("MainChar", out var wSet)
                            && wSet.Contains(label);

                        result.Add(new GroupHeader
                        {
                            Name = label,
                            Level = 2,
                            Count = items.Count,
                            IsExpanded = expanded,
                            ParentName = "MainChar"
                        });

                        if (expanded)
                            result.AddRange(items.Cast<object>());
                    }

                    if (oldCards.Count > 0)
                    {
                        var oldExpanded = _expandedWeapons.TryGetValue("MainChar", out var oldSet)
                            && oldSet.Contains("Old");

                        result.Add(new GroupHeader
                        {
                            Name = "Old",
                            Level = 2,
                            Count = oldCards.Count,
                            IsExpanded = oldExpanded,
                            ParentName = "MainChar"
                        });

                        if (oldExpanded)
                            result.AddRange(oldCards
                                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                                .Cast<object>());
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

            var groupOldCards = enemyGroup.Where(IsOldCard)
                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var activeMoves = enemyGroup.Where(m => !IsOldCard(m)).ToList();

            var weaponGroups = activeMoves
                .GroupBy(m => m.WeaponType)
                .OrderBy(g => g.Key);

            foreach (var weaponGroup in weaponGroups)
            {
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

            if (groupOldCards.Count > 0)
            {
                var oldExpanded = _expandedWeapons.TryGetValue(enemyGroup.Key, out var oldSet)
                    && oldSet.Contains("Old");

                result.Add(new GroupHeader
                {
                    Name = "Old",
                    Level = 2,
                    Count = groupOldCards.Count,
                    IsExpanded = oldExpanded,
                    ParentName = enemyGroup.Key
                });

                if (oldExpanded)
                    result.AddRange(groupOldCards.Cast<object>());
            }
        }
        return result;
    }

    private void BuildTree(List<MoveInfo> moves)
    {
        bool IsCustom(MoveInfo m) => string.Equals(m.Category, "Custom", StringComparison.OrdinalIgnoreCase);
        var customMoves = moves.Where(m => IsCustom(m) && !string.IsNullOrWhiteSpace(m.DisplayName)).ToList();
        var vanillaMoves = moves.Where(m =>
            !IsCustom(m) && m.IsUsed && m.IsValid && !string.IsNullOrWhiteSpace(m.DisplayName) &&
            IsVanillaLibraryCard(m)).ToList();
        var unusedMoves = moves.Where(m =>
            !IsCustom(m) && !string.IsNullOrWhiteSpace(m.DisplayName) &&
            !(m.IsUsed && m.IsValid && IsVanillaLibraryCard(m))).ToList();

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

    private void LogLibraryDiagnostics()
    {
        try
        {
            var nonCustom = _allMoves
                .Where(m => !string.Equals(m.Category, "Custom", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var g in nonCustom
                .GroupBy(m => m.Character, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var breakdown = g
                    .GroupBy(m => IsOldCard(m) ? "Old"
                        : string.IsNullOrEmpty(m.WeaponType) ? "?" : m.WeaponType,
                        StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase);
                ErrorLog.Write("LIBRARY",
                    $"{g.Key}: {g.Count()} cards -> {string.Join(", ", breakdown.Select(x => $"{x.Key}={x.Count()}"))}");
            }

            var vanilla = _allMoves.Where(m =>
                m.IsUsed && m.IsValid && IsVanillaLibraryCard(m)).ToList();
            var mcVanilla = vanilla
                .Where(m => string.Equals(m.Character, "MainChar", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in _mainCharWeaponMoves.Values)
                foreach (var m in list)
                    if (!string.IsNullOrEmpty(m.FullPath))
                        claimed.Add(m.FullPath);

            var outsideTree = mcVanilla
                .Where(m => !claimed.Contains(m.FullPath ?? ""))
                .ToList();

            ErrorLog.Write("LIBRARY",
                $"vanilla-library cards: {vanilla.Count} " +
                $"(MainChar {mcVanilla.Count}, outside tree groups {outsideTree.Count}, " +
                $"old {vanilla.Count(IsOldCard)})");
            foreach (var m in outsideTree.OrderBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase))
                ErrorLog.Write("LIBRARY", $"  browsable: {m.FullPath} [{m.WeaponType}/{m.Category}]");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("LIBRARY", ex);
        }
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

    private static readonly SolidColorBrush ChainHighlightBorder = new(Color.FromRgb(0xf9, 0xe2, 0xaf));
    private static readonly SolidColorBrush ChainHighlightEdgeBrush = new(Color.FromRgb(0xf9, 0xe2, 0xaf));
    private const double ChainDimOpacity = 0.3;
    private const double ChainEdgeThickness = 3.0;
    private const int ChainEdgeZIndex = 4;
    private const int NodeZIndex = 10;
    private const int NodeInputBgZIndex = 11;

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
        ChainHighlightBorder.Freeze();
        ChainHighlightEdgeBrush.Freeze();
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
        CommitAttackDbPanel();
        bool hadSelection = _selectedNodeId >= 0 || _chainNodeIds.Count > 0;
        _selectedNodeId = -1;
        _chainNodeIds.Clear();
        if (hadSelection)
            ApplySelectionStyles();
        UpdateAttackDbPanel(null);
    }


    #region Attack DB panel

    private int _attackPanelNodeId = -1;
    private bool _attackPanelCommitting;

    private ComboNode? ResolveAttackPanelNode()
    {
        if (_attackPanelNodeId < 0 || _comboGraph == null) return null;
        return _comboGraph.Nodes.FirstOrDefault(n => n.Id == _attackPanelNodeId);
    }

    private string AttackDbContentDir() => ContentDetector.ResolveContentDir(_contentPath);

    private static string FormatAttackDbVanilla(AttackDbCardValues v)
    {
        var parts = new List<string>(2);
        if (v.HasBuildup) parts.Add($"{v.Buildup} frames");
        if (v.HasRange) parts.Add($"range {AttackDbCard.FormatRange(v.Range)}");
        return string.Join(", ", parts);
    }

    private void UpdateAttackDbPanel(ComboNode? node)
    {
        if (attackDbSection == null || txtAttackBuildup == null || txtAttackRange == null) return;

        _attackPanelNodeId = node?.Id ?? -1;

        UpdateDelayWindowSection(node);

        AttackDbCardValues? vanilla = null;
        bool customCard = false;
        string cardPath = AttackDbCard.EffectiveCardPath(node);
        if (node != null && !node.IsRedirect && !string.IsNullOrEmpty(cardPath))
        {
            vanilla = AttackDbCard.ReadDbPath(AttackDbContentDir(), cardPath);
            if (vanilla == null || !vanilla.Any)
            {
                // Not a vanilla card - fall back to the persistent CustomAssets copy so
                // nodes pointing at custom attacks still get buildup/range editing here.
                var custom = AttackDbCard.ReadDbPath(CustomAssets.Root, cardPath);
                if (custom != null && custom.Any)
                {
                    vanilla = custom;
                    customCard = true;
                }
            }
        }

        bool show = vanilla != null && vanilla.Any;
        attackDbSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || node == null || vanilla == null) return;

        string cardName = Path.GetFileNameWithoutExtension(
            AttackDbCard.GamePathToContentRel(cardPath));
        txtAttackDbCard.Text = $"Card: {cardName}";
        txtAttackBuildup.IsEnabled = vanilla.HasBuildup;
        txtAttackRange.IsEnabled = vanilla.HasRange;
        txtAttackBuildup.Text = vanilla.HasBuildup
            ? (node.AttackBuildupFrames?.ToString() ?? vanilla.Buildup.ToString())
            : "";
        txtAttackRange.Text = vanilla.HasRange
            ? (node.AttackGameplayRange.HasValue
                ? AttackDbCard.FormatRange(node.AttackGameplayRange.Value)
                : AttackDbCard.FormatRange(vanilla.Range))
            : "";

        bool tuned = node.AttackBuildupFrames.HasValue || node.AttackGameplayRange.HasValue;
        string baselineLabel = customCard ? "custom card" : "vanilla";
        txtAttackDbHint.Text = $"{baselineLabel}: {FormatAttackDbVanilla(vanilla)}" + (tuned ? " - edited" : "");
        int hitEdited = 0;
        if (node.HitEdits != null)
        {
            foreach (var e in node.HitEdits) if (!e.IsEmpty()) hitEdited++;
        }
        if (hitEdited > 0)
            txtAttackDbHint.Text += $" · {hitEdited} hit window(s) edited";
    }

    private void AttackHitWindows_Click(object sender, RoutedEventArgs e)
    {
        CommitAttackDbPanel();
        var node = ResolveAttackPanelNode();
        if (node == null) return;
        string cardPath = AttackDbCard.EffectiveCardPath(node);
        if (string.IsNullOrEmpty(cardPath) || node.IsRedirect) return;

        var card = AttackHitCard.ReadDbPath(AttackDbContentDir(), cardPath);
        if (card == null || card.Windows.Count == 0)
            card = AttackHitCard.ReadDbPath(CustomAssets.Root, cardPath);
        if (card == null || card.Windows.Count == 0)
        {
            txtStatus.Text = "No readable hit windows on this card";
            return;
        }

        // Display-only armor read from the node's effective anim (CustomAssets-first);
        // any failure degrades to "unknown" rather than blocking the dialog (Phase 1).
        // Skipped while the Armor section is hidden behind ShowArmorSection.
        List<ArmorNotifyInfo>? armor = null;
        if (AttackDbEditorDialog.ShowArmorSection)
        {
            try
            {
                var animFile = ArmorNotifyReader.ResolveAnimFile(AttackDbContentDir(), node.AnimPath);
                if (animFile != null) armor = ArmorNotifyReader.Read(animFile);
            }
            catch { armor = null; }
        }

        var dialog = new AttackDbEditorDialog(card, node.HitEdits, armor) { Owner = this };
        bool? ok = dialog.ShowDialog();
        if (ok != true) return;

        var newEdits = AttackHitEdit.Prune(dialog.Result);
        bool same = (node.HitEdits == null && newEdits == null)
            || (node.HitEdits != null && newEdits != null && EditsEqual(node.HitEdits, newEdits));
        if (same) return;

        PushUndo("Hit window edits");
        node.HitEdits = newEdits;
        PropagateAttackDbTuning(node);
        SaveCurrentUnitToCache();
        txtStatus.Text = newEdits == null
            ? $"Hit window edits restored to vanilla on {node.DisplayName}"
            : $"Hit window edits set on {node.DisplayName} ({newEdits.Count} window(s))";
        UpdateAttackDbPanel(node);
    }

    /// <summary>Ordinal equality over every field of two prune-safe edit lists (same Index order).</summary>
    private static bool EditsEqual(List<AttackHitEdit> a, List<AttackHitEdit> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.Index != y.Index
                || x.FrameLo != y.FrameLo || x.FrameHi != y.FrameHi
                || x.Damage != y.Damage || x.StructureDamage != y.StructureDamage
                || x.DamageOnHit != y.DamageOnHit || x.DamageOnGuard != y.DamageOnGuard
                || x.DamageOnDeflect != y.DamageOnDeflect
                || x.StructureDamageOnHit != y.StructureDamageOnHit
                || x.StructureDamageOnGuard != y.StructureDamageOnGuard
                || x.StructureDamageOnDeflect != y.StructureDamageOnDeflect
                || x.KnockbackDistance != y.KnockbackDistance
                || x.SuperDizzyDamage != y.SuperDizzyDamage
                || x.StunFrames != y.StunFrames || x.FreezeFrames != y.FreezeFrames
                || x.Parriable != y.Parriable || x.Lethal != y.Lethal
                || x.BreakStructure != y.BreakStructure
                || x.DizzyOnParry != y.DizzyOnParry || x.DizzyOnAvoid != y.DizzyOnAvoid
                || !string.Equals(x.AttackPower, y.AttackPower, StringComparison.Ordinal)
                || x.PropertyLevel != y.PropertyLevel)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The hit-window edits another node already holds for this card. Null when nobody
    /// edited it yet (or the only holders are this node).
    /// </summary>
    private List<AttackHitEdit>? FindSharedHitEdits(string cardPath, ComboNode exclude)
    {
        if (string.IsNullOrEmpty(cardPath)) return null;
        foreach (var n in EnumerateAllGraphNodes())
        {
            if (ReferenceEquals(n, exclude) || n.IsRedirect) continue;
            if (!string.Equals(AttackDbCard.EffectiveCardPath(n), cardPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (n.HitEdits != null && n.HitEdits.Count > 0)
                return AttackHitEdit.CloneList(n.HitEdits);
        }
        return null;
    }

    /// <summary>
    /// The delay-window inputs show for any node whose ORIGINAL card carries an
    /// MC_Attacks_Alt window - visible even before a replacement so the window can be
    /// preset; export consumes it only when the card actually gets swapped. Reads
    /// DefaultDBPath (the original), never EffectiveCardPath. Fields edit seconds;
    /// storage and export stay in frames.
    /// </summary>
    private void UpdateDelayWindowSection(ComboNode? node)
    {
        if (delayWindowSection == null || txtDelayWinLo == null || txtDelayWinHi == null) return;

        bool eligible = node != null && !node.IsRedirect && !string.IsNullOrEmpty(node.DefaultDBPath);
        var window = eligible
            ? DelayWindowCard.Read(AttackDbContentDir(), node!.DefaultDBPath)
            : default;
        bool customCard = false;
        if (eligible && !window.Has)
        {
            // Custom card - its MC_Attacks_Alt window lives in the CustomAssets copy.
            window = DelayWindowCard.Read(CustomAssets.Root, node!.DefaultDBPath);
            customCard = window.Has;
        }
        if (!window.Has)
        {
            delayWindowSection.Visibility = Visibility.Collapsed;
            return;
        }

        delayWindowSection.Visibility = Visibility.Visible;
        int vanillaLo = (int)Math.Round(window.Lo);
        int vanillaHi = (int)Math.Round(window.Hi);
        int shownLo = node!.DelayWindowLo ?? vanillaLo;
        int shownHi = node.DelayWindowHi ?? vanillaHi;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        txtDelayWinLo.Text = DelayWindowCard.FramesToSeconds(shownLo).ToString("0.###", inv);
        txtDelayWinHi.Text = DelayWindowCard.FramesToSeconds(shownHi).ToString("0.###", inv);

        bool edited = node.DelayWindowLo.HasValue || node.DelayWindowHi.HasValue;
        string baselineLabel = customCard ? "custom card" : "vanilla";
        txtDelayWindowHint.Text =
            $"{baselineLabel}: {vanillaLo}-{vanillaHi} frames "
            + $"({DelayWindowCard.FramesToSeconds(vanillaLo).ToString("0.###", inv)}-"
            + $"{DelayWindowCard.FramesToSeconds(vanillaHi).ToString("0.###", inv)}s)"
            + (edited ? " - edited" : "");
    }

    private void RefreshAttackDbPanel()
    {
        if (attackDbSection == null) return;
        // Never overwrite text the user is still typing.
        if (txtAttackBuildup != null && txtAttackBuildup.IsKeyboardFocused) return;
        if (txtAttackRange != null && txtAttackRange.IsKeyboardFocused) return;
        if (txtDelayWinLo != null && txtDelayWinLo.IsKeyboardFocused) return;
        if (txtDelayWinHi != null && txtDelayWinHi.IsKeyboardFocused) return;
        UpdateAttackDbPanel(ResolveAttackPanelNode());
    }

    private void ClearAttackDbPanel()
    {
        _attackPanelNodeId = -1;
        if (attackDbSection != null) attackDbSection.Visibility = Visibility.Collapsed;
        if (delayWindowSection != null) delayWindowSection.Visibility = Visibility.Collapsed;
    }

    private void CommitAttackDbPanel()
    {
        if (_attackPanelCommitting) return;
        var node = ResolveAttackPanelNode();
        if (node == null || attackDbSection == null) return;
        bool attackVisible = attackDbSection.Visibility == Visibility.Visible;
        bool delayVisible = delayWindowSection != null && delayWindowSection.Visibility == Visibility.Visible;
        if (!attackVisible && !delayVisible) return;

        var vanilla = attackVisible
            ? AttackDbCard.ReadDbPath(AttackDbContentDir(), AttackDbCard.EffectiveCardPath(node))
            : null;
        if (attackVisible && (vanilla == null || !vanilla.Any))
        {
            // Custom card: its own on-disk values are the baseline (matching them
            // suppresses the override so the raw card ships unchanged).
            var custom = AttackDbCard.ReadDbPath(CustomAssets.Root, AttackDbCard.EffectiveCardPath(node));
            if (custom != null && custom.Any) vanilla = custom;
        }

        int? newBuildup = null;
        float? newRange = null;
        if (attackVisible)
        {
            string rawB = txtAttackBuildup?.Text?.Trim() ?? "";
            string rawR = txtAttackRange?.Text?.Trim() ?? "";

            if (rawB.Length > 0)
            {
                if (!int.TryParse(rawB, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int b) || b < 0)
                {
                    txtStatus.Text = $"Buildup frames must be a whole number >= 0 (got '{rawB}')";
                    UpdateAttackDbPanel(node);
                    return;
                }
                newBuildup = b;
            }

            if (rawR.Length > 0)
            {
                if (!float.TryParse(rawR, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float r) || r <= 0)
                {
                    txtStatus.Text = $"Gameplay range must be a number > 0 (got '{rawR}')";
                    UpdateAttackDbPanel(node);
                    return;
                }
                newRange = r;
            }

            // A value identical to vanilla is not a change - store it as "no override".
            // Display equality (3 decimals), not bit equality: the textbox round-trips
            // "0.###", so a 4-decimal vanilla value reads back with rounding error that
            // a raw tolerance check would keep as a phantom override.
            if (vanilla != null)
            {
                if (newBuildup.HasValue && vanilla.HasBuildup && newBuildup.Value == vanilla.Buildup)
                    newBuildup = null;
                if (newRange.HasValue && vanilla.HasRange && AttackDbCard.SameRange(newRange.Value, vanilla.Range))
                    newRange = null;
            }
        }

        int? newWinLo = null;
        int? newWinHi = null;
        if (delayVisible)
        {
            string rawL = txtDelayWinLo?.Text?.Trim() ?? "";
            string rawH = txtDelayWinHi?.Text?.Trim() ?? "";
            if (rawL.Length > 0 || rawH.Length > 0)
            {
                if (rawL.Length == 0 || rawH.Length == 0)
                {
                    txtStatus.Text = "Delay window: enter both start and end times, or clear both to reset";
                    UpdateAttackDbPanel(node);
                    return;
                }
                if (!double.TryParse(rawL, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double sL) || sL < 0)
                {
                    txtStatus.Text = $"Delay window start must be a number >= 0 seconds (got '{rawL}')";
                    UpdateAttackDbPanel(node);
                    return;
                }
                if (!double.TryParse(rawH, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double sH) || sH <= sL)
                {
                    txtStatus.Text = $"Delay window end must be greater than start (got '{rawH}')";
                    UpdateAttackDbPanel(node);
                    return;
                }
                int fLo = DelayWindowCard.SecondsToFrames(sL);
                int fHi = DelayWindowCard.SecondsToFrames(sH);
                if (fHi <= fLo)
                {
                    txtStatus.Text = "Delay window end must be at least 1/60s greater than start";
                    UpdateAttackDbPanel(node);
                    return;
                }

                // Seconds that convert to the vanilla frames are not an override -
                // Same "identical to vanilla is no change" rule as buildup/range -
                // for custom cards the card's own window plays the vanilla role.
                var vanillaWin = DelayWindowCard.Read(AttackDbContentDir(), node.DefaultDBPath);
                if (!vanillaWin.Has)
                    vanillaWin = DelayWindowCard.Read(CustomAssets.Root, node.DefaultDBPath);
                if (vanillaWin.Has
                    && fLo == (int)Math.Round(vanillaWin.Lo)
                    && fHi == (int)Math.Round(vanillaWin.Hi))
                {
                    newWinLo = null;
                    newWinHi = null;
                }
                else
                {
                    newWinLo = fLo;
                    newWinHi = fHi;
                }
            }
        }

        bool attackChanged = attackVisible
            && (newBuildup != node.AttackBuildupFrames || newRange != node.AttackGameplayRange);
        bool delayChanged = delayVisible
            && (newWinLo != node.DelayWindowLo || newWinHi != node.DelayWindowHi);
        if (!attackChanged && !delayChanged) return;

        _attackPanelCommitting = true;
        try
        {
            PushUndo(attackChanged && delayChanged ? "Attack DB + delay window"
                : delayChanged ? "Delay window" : "Attack DB tuning");
            if (attackChanged)
            {
                node.AttackBuildupFrames = newBuildup;
                node.AttackGameplayRange = newRange;
                // Same card file everywhere: every unit's copy of this attack moves with it.
                PropagateAttackDbTuning(node);
            }
            if (delayChanged)
            {
                // Per-node on purpose: FR/BL predecessors can map to different replacements,
                // so unlike buildup (a property of the shared card file) nothing propagates.
                node.DelayWindowLo = newWinLo;
                node.DelayWindowHi = newWinHi;
            }
            SaveCurrentUnitToCache();
        }
        finally
        {
            _attackPanelCommitting = false;
        }

        txtStatus.Text = delayChanged && !attackChanged
            ? (newWinLo.HasValue
                ? $"Delay window set on {node.DisplayName}"
                : $"Delay window reset on {node.DisplayName}")
            : attackChanged && !delayChanged
                ? (newBuildup.HasValue || newRange.HasValue
                    ? $"Attack DB tuned on {node.DisplayName}"
                    : $"Attack DB restored to vanilla on {node.DisplayName}")
                : $"Attack DB and delay window set on {node.DisplayName}";
        UpdateAttackDbPanel(node);
    }

    private void AttackDbField_Commit(object sender, RoutedEventArgs e) => CommitAttackDbPanel();

    private void AttackDbField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitAttackDbPanel();
        e.Handled = true;
    }

    #endregion

    private void SelectNode(Border border, ComboNode node)
    {
        CommitAttackDbPanel();
        _selectedNodeId = node.Id;
        ComputeSelectionChain();
        ApplySelectionStyles();
        if (node.Id < 0)
            ApplySelectedNodeStyle(border, node);
        UpdateAttackDbPanel(node);
    }

    /// <summary>Right-panel Display line for a combo node, prefixed with its tree
    /// index ([45]) or [new] so it matches the Start Entries dropdown format.
    /// Redirects keep "Redirect → name" (they are not tree nodes).</summary>
    private void UpdateNodeDisplayNameLabel(ComboNode node)
    {
        if (node.IsRedirect)
        {
            txtDisplayName.Text =
                $"Redirect → {(_comboGraph?.Nodes.FirstOrDefault(n => n.Id == node.ResolvedRedirectNodeId)?.DisplayName ?? "?")}";
            return;
        }
        string idx = node.TreeIndex >= 0 ? $"[{node.TreeIndex}]" : "[new]";
        txtDisplayName.Text = !string.IsNullOrEmpty(node.ImportedDisplayName) &&
            !string.Equals(node.ImportedDisplayName, node.DisplayName, StringComparison.OrdinalIgnoreCase)
                ? $"Display: {idx} {node.DisplayName} → Changed: {node.ImportedDisplayName}"
                : $"Display: {idx} {node.DisplayName}";
    }

    /// <summary>Start Entries dialog jump target: selects the node on the combo canvas,
    /// pans it to the viewport centre and updates the info panel. The dialog is modal,
    /// so the reveal is seen around/behind it; interaction resumes after it closes.</summary>
    internal void SelectAndRevealNode(int nodeId)
    {
        if (_comboGraph == null) return;
        var node = _comboGraph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
        {
            txtStatus.Text = "Jump target no longer exists in the graph";
            return;
        }
        if (!_nodeBorders.TryGetValue(nodeId, out var border) ||
            !_nodePositions.TryGetValue(nodeId, out var pos))
        {
            ShowToast("Node is not visible on the canvas (hidden AI or dead redirect) - jump ignored");
            return;
        }
        double cx = comboBorder.ActualWidth / 2.0;
        double cy = comboBorder.ActualHeight / 2.0;
        _comboTranslate.X = cx - (pos.X + NODE_WIDTH / 2.0);
        _comboTranslate.Y = cy - (pos.Y + NODE_HEIGHT / 2.0);
        ClearMoveSelection();
        SelectNode(border, node);
        var typePath = ComboNodeTypePath(node);
        if (node.IsActionNode)
            SetSourceTypeDisplay("Action");
        else if (node.IsEventNode)
            SetSourceTypeDisplay("Event");
        else if (!string.IsNullOrEmpty(typePath))
            SetSourceTypeDisplay(MoveInfo.SourceTypeFromPath(typePath));
        else
            SetSourceTypeDisplay("Other");
        UpdateNodeDisplayNameLabel(node);
        string idx = node.TreeIndex >= 0 ? $"[{node.TreeIndex}]" : "[new]";
        txtStatus.Text = $"Jumped to {idx} {node.DisplayName} - close Start Entries to interact";
        ShowToast($"Jumped to {idx} {node.DisplayName}");
    }

    private System.Windows.Threading.DispatcherTimer? _redirectHighlightTimer;

    /// <summary>Canvas menu: flash every redirect that points at this node, plus this
    /// node's own redirect target when it is one, and light the connecting edges for
    /// a moment - the "where does this actually enter/continue" answer in one glance.
    /// Restores the prior selection highlight when the flash window ends.</summary>
    private void HighlightConnectedRedirects_Click(object sender, RoutedEventArgs e)
    {
        if (_comboGraph == null || sender is not MenuItem mi || mi.Tag is not ComboNode node) return;

        var matches = _comboGraph.Nodes
            .Where(n => n.IsRedirect && n.ResolvedRedirectNodeId == node.Id)
            .ToList();
        var incoming = matches.Where(n => !n.Removed).ToList();
        int hidden = matches.Count - incoming.Count;

        int savedSelected = _selectedNodeId;
        var savedChain = new HashSet<int>(_chainNodeIds);

        var chain = new HashSet<int> { node.Id };
        foreach (var r in incoming) chain.Add(r.Id);

        string suffix = "";
        if (node.IsRedirect)
        {
            var fwd = _comboGraph.Nodes.FirstOrDefault(n => n.Id == node.ResolvedRedirectNodeId);
            if (fwd != null)
            {
                chain.Add(fwd.Id);
                suffix = $", forwards to {fwd.DisplayName}";
                if (_nodeBorders.TryGetValue(fwd.Id, out var fwdBorder)) PlayFusionAnimation(fwdBorder);
            }
        }

        _selectedNodeId = node.Id;
        _chainNodeIds.Clear();
        foreach (var id in chain) _chainNodeIds.Add(id);
        ApplySelectionStyles();

        foreach (var r in incoming)
            if (_nodeBorders.TryGetValue(r.Id, out var rb)) PlayFusionAnimation(rb);
        if (_nodeBorders.TryGetValue(node.Id, out var nb)) PlayFusionAnimation(nb);

        // Pan only when the node sits outside the viewport - no jarring jump for a
        // highlight the user can already see.
        if (_nodePositions.TryGetValue(node.Id, out var pos))
        {
            double viewL = -_comboTranslate.X, viewT = -_comboTranslate.Y;
            double viewR = viewL + comboBorder.ActualWidth, viewB = viewT + comboBorder.ActualHeight;
            bool offscreen = pos.X + NODE_WIDTH < viewL || pos.X > viewR
                          || pos.Y + NODE_HEIGHT < viewT || pos.Y > viewB;
            if (offscreen)
            {
                double cx = comboBorder.ActualWidth / 2.0;
                double cy = comboBorder.ActualHeight / 2.0;
                _comboTranslate.X = cx - (pos.X + NODE_WIDTH / 2.0);
                _comboTranslate.Y = cy - (pos.Y + NODE_HEIGHT / 2.0);
            }
        }

        string hiddenPart = hidden > 0 ? $", {hidden} hidden" : "";
        if (incoming.Count == 0 && !node.IsRedirect)
            ShowToast($"No redirects into {node.DisplayName}");
        else
            ShowToast($"{incoming.Count} redirects → {node.DisplayName}{hiddenPart}{suffix}");

        _redirectHighlightTimer?.Stop();
        _redirectHighlightTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _redirectHighlightTimer.Tick += (s, ev) =>
        {
            _redirectHighlightTimer.Stop();
            _selectedNodeId = savedSelected;
            _chainNodeIds.Clear();
            foreach (var id in savedChain) _chainNodeIds.Add(id);
            ApplySelectionStyles();
        };
        _redirectHighlightTimer.Start();
    }

    private void ComputeSelectionChain()
    {
        _chainNodeIds.Clear();
        if (_selectedNodeId < 0 || _comboGraph == null) return;
        if (_comboGraph.Nodes.All(n => n.Id != _selectedNodeId)) return;

        var parents = new Dictionary<int, List<int>>();
        var children = new Dictionary<int, List<int>>();
        foreach (var n in _comboGraph.Nodes)
        {
            parents[n.Id] = new List<int>();
            children[n.Id] = new List<int>();
        }
        foreach (var e in _comboGraph.Edges)
        {
            if (e.FromNodeId == -1) continue;
            // The redirect -> target forward hop is stored as an edge (IsRedirect) but
            // never drawn (RenderComboGraph skips it). The selection chain may only
            // travel drawn links, so redirects terminate the walk both ways: the
            // visible dashed incoming link still lights, the forward target does not.
            if (e.IsRedirect) continue;
            if (children.ContainsKey(e.FromNodeId) && parents.ContainsKey(e.ToNodeId))
            {
                children[e.FromNodeId].Add(e.ToNodeId);
                parents[e.ToNodeId].Add(e.FromNodeId);
            }
        }

        _chainNodeIds.Add(_selectedNodeId);

        var visitedUp = new HashSet<int> { _selectedNodeId };
        var queue = new Queue<int>();
        queue.Enqueue(_selectedNodeId);
        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            foreach (var nb in parents[id])
                if (visitedUp.Add(nb)) { _chainNodeIds.Add(nb); queue.Enqueue(nb); }
        }

        var visitedDown = new HashSet<int> { _selectedNodeId };
        queue.Enqueue(_selectedNodeId);
        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            foreach (var nb in children[id])
                if (visitedDown.Add(nb)) { _chainNodeIds.Add(nb); queue.Enqueue(nb); }
        }
    }

    private void ApplySelectionStyles()
    {
        bool active = _selectedNodeId >= 0 && _chainNodeIds.Count > 0;
        bool mainChar = active && IsCurrentGraphMainChar();

        foreach (var kvp in _nodeBorders)
        {
            if (kvp.Value.Tag is not ComboNode node) continue;
            double baseOpacity = node.Removed ? 0.4 : 1.0;
            if (active && kvp.Key == _selectedNodeId)
            {
                ApplySelectedNodeStyle(kvp.Value, node);
                kvp.Value.Opacity = baseOpacity;
            }
            else if (active && _chainNodeIds.Contains(kvp.Key))
            {
                kvp.Value.BorderBrush = GetChainBorderBrush(node, mainChar);
                kvp.Value.Background = GetNodeBackground(node);
                kvp.Value.Opacity = baseOpacity;
            }
            else
            {
                kvp.Value.BorderBrush = GetNodeColor(node);
                kvp.Value.Background = GetNodeBackground(node);
                kvp.Value.Opacity = active ? ChainDimOpacity : baseOpacity;
            }
        }

        foreach (var kvp in _nodeInputBgs)
        {
            bool lit = !active || _chainNodeIds.Contains(kvp.Key) || kvp.Key == _selectedNodeId;
            kvp.Value.Opacity = lit ? 1.0 : ChainDimOpacity;
        }

        foreach (var kvp in _edgeVisuals)
        {
            var edge = kvp.Key;
            var vis = kvp.Value;
            bool chain = active && _chainNodeIds.Contains(edge.FromNodeId) && _chainNodeIds.Contains(edge.ToNodeId);
            var brush = GetEdgeBrush(edge);
            if (chain && (!mainChar || IsIncomingToRedirect(edge)))
                brush = ChainHighlightEdgeBrush;
            if (vis.Path != null)
            {
                vis.Path.Stroke = brush;
                vis.Path.StrokeThickness = chain ? ChainEdgeThickness : GetEdgeThickness(edge);
                vis.Path.Opacity = active && !chain ? ChainDimOpacity : 1.0;
                Canvas.SetZIndex(vis.Path, chain ? ChainEdgeZIndex : 0);
            }
            if (vis.HitPath != null)
                Canvas.SetZIndex(vis.HitPath, chain ? ChainEdgeZIndex : -1);
            if (vis.Arrow != null)
            {
                vis.Arrow.Fill = brush;
                vis.Arrow.Opacity = active && !chain ? ChainDimOpacity : 1.0;
                Canvas.SetZIndex(vis.Arrow, chain ? ChainEdgeZIndex : 0);
            }
        }
    }

    private void ApplySelectedNodeStyle(Border border, ComboNode node)
    {
        var sc = GetNodeColor(node).Color;
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

    private SolidColorBrush GetChainBorderBrush(ComboNode node, bool mainChar)
    {
        if (mainChar && node.IsRedirect)
            return ChainHighlightBorder;
        if (mainChar && !string.IsNullOrEmpty(node.InputLabel))
            return GetInputColor(node.InputLabel);
        return ChainHighlightBorder;
    }

    private void RestoreNodeVisual(Border border, ComboNode node)
    {
        bool active = _selectedNodeId >= 0 && _chainNodeIds.Count > 0;
        bool isSel = active && _selectedNodeId == node.Id;
        bool inChain = active && _chainNodeIds.Contains(node.Id);
        if (isSel)
        {
            ApplySelectedNodeStyle(border, node);
        }
        else
        {
            border.BorderBrush = inChain
                ? GetChainBorderBrush(node, IsCurrentGraphMainChar())
                : GetNodeColor(node);
            border.Background = GetNodeBackground(node);
        }
        double baseOpacity = node.Removed ? 0.4 : 1.0;
        border.Opacity = active && !isSel && !inChain ? ChainDimOpacity : baseOpacity;
    }

    private bool IsIncomingToRedirect(ComboEdge edge) => _comboGraph != null
        && _comboGraph.Nodes.Any(n => n.Id == edge.ToNodeId && n.IsRedirect);

    private SolidColorBrush GetEdgeBrush(ComboEdge edge)
    {
        bool isStanceEdge = edge.FromNodeId == -1;
        var brush = isStanceEdge
            ? EdgeStanceDimBrush
            : (EdgeBrushByInput.TryGetValue(edge.InputName, out var c) ? c : EdgeDefaultBrush);
        if (IsIncomingToRedirect(edge))
            brush = new SolidColorBrush(Color.FromArgb(0x60, 0x6c, 0x70, 0x86));
        return brush;
    }

    private static double GetEdgeThickness(ComboEdge edge) => edge.FromNodeId == -1 ? 1.5 : 2.0;

    private void LibModeAttacks_Click(object sender, RoutedEventArgs e) => SetLibraryMode(false);

    private void LibModeAi_Click(object sender, RoutedEventArgs e) => SetLibraryMode(true);

    private void SetLibraryMode(bool ai)
    {
        if (_aiLibraryMode == ai) return;
        _aiLibraryMode = ai;
        tabMoves.Visibility = ai ? Visibility.Collapsed : Visibility.Visible;
        scrollAiNodes.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        btnLibAttacks.Background = new SolidColorBrush(ai ? Color.FromRgb(0x18, 0x18, 0x25) : Color.FromRgb(0xcd, 0xd6, 0xf4));
        btnLibAttacks.Foreground = new SolidColorBrush(ai ? Color.FromRgb(0x6c, 0x70, 0x86) : Color.FromRgb(0x1e, 0x1e, 0x2e));
        btnLibAi.Background = new SolidColorBrush(ai ? Color.FromRgb(0xcd, 0xd6, 0xf4) : Color.FromRgb(0x18, 0x18, 0x25));
        btnLibAi.Foreground = new SolidColorBrush(ai ? Color.FromRgb(0x1e, 0x1e, 0x2e) : Color.FromRgb(0x6c, 0x70, 0x86));
        if (ai)
            _ = EnsureAiCatalogAsync();
        else if (_allMoves.Count > 0)
            FilterMoves();
        else
            RefreshAiList();
    }

    private async System.Threading.Tasks.Task EnsureAiCatalogAsync()
    {
        if (_aiScanStarted)
        {
            RefreshAiList();
            return;
        }
        _aiScanStarted = true;
        try
        {
            txtStatus.Text = "Scanning combo trees for AI actions/events...";
            string contentDir = Setup.ContentDetector.ResolveContentDir(_contentPath);
            var scanned = await System.Threading.Tasks.Task.Run(() =>
                ActionNodeCatalog.Scan(contentDir, msg => Dispatcher.Invoke(() => txtStatus.Text = msg)));
            _aiNodes = scanned;
            if (scanned.Count > 0)
            {
                txtStatus.Text = $"AI catalog: {scanned.Count} nodes ({CountAiActions()} actions, {CountAiEvents()} events) - click a category to expand";
            }
            else
            {
                txtStatus.Text = "AI catalog: no action/event nodes found";
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("AI-CATALOG", ex);
            _aiScanStarted = false;
        }
        RefreshAiList();
    }

    private void RefreshAiList()
    {
        if (_aiNodes.Count == 0)
        {
            listAi.ItemsSource = null;
            return;
        }

        string search = txtSearch.Text.Trim();
        // Class names in files keep the generated "_C" suffix; the catalog strips it.
        // Tolerate either form so pasting "BP_AiAction_Dash_ES_Kuroki_C" still matches.
        if (search.EndsWith("_C", StringComparison.OrdinalIgnoreCase))
            search = search[..^2];
        bool Match(AiNodeInfo n) => string.IsNullOrEmpty(search)
            || n.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || n.SourceUnit.Contains(search, StringComparison.OrdinalIgnoreCase)
            || n.ClassName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || n.Kind.Contains(search, StringComparison.OrdinalIgnoreCase);
        var filtered = _aiNodes.Where(Match).ToList();

        var result = new List<object>();
        foreach (var (kind, kindKey, subtitle) in new[]
        {
            ("Action", "AI Actions", "Cross-unit AI action nodes - drag a card onto the canvas"),
            ("Event", "AI Events", "Cross-unit combo event nodes - drag a card onto the canvas")
        })
        {
            var inKind = filtered.Where(n => n.Kind == kind).ToList();
            if (inKind.Count == 0) continue;
            bool kindExpanded = _expandedEnemies.Contains(kindKey);
            result.Add(new GroupHeader
            {
                Name = kindKey,
                Level = 1,
                Count = inKind.Count,
                Subtitle = subtitle,
                IsExpanded = kindExpanded
            });
            if (!kindExpanded) continue;

            foreach (var unitGroup in inKind.GroupBy(n => n.SourceUnit, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                bool unitExpanded = _expandedWeapons.TryGetValue(kindKey, out var set) && set.Contains(unitGroup.Key);
                result.Add(new GroupHeader
                {
                    Name = unitGroup.Key,
                    Level = 2,
                    Count = unitGroup.Count(),
                    ParentName = kindKey,
                    IsExpanded = unitExpanded
                });
                if (!unitExpanded) continue;
                foreach (var n in unitGroup.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
                    result.Add(n);
            }
        }
        listAi.ItemsSource = result;
    }

    private async void MoveCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border aiBorder && aiBorder.Tag is AiNodeInfo ai)
        {
            ClearNodeSelection();
            SelectMoveCard(aiBorder);
            if (ai.PreviewAnimPath != null)
            {
                txtStatus.Text = $"Loading: {ai.DisplayName}...";
                SetSourceTypeDisplay(ai.Kind);
                await PreviewAsync(ai.Character, ai.PreviewAnimPath);
            }
            else
            {
                txtStatus.Text = $"{ai.Kind}: {ai.DisplayName} - from {ai.SourceUnit} ({ai.SourceFile})" +
                    (ai.Kind == "Event" ? " (events have no animation)" : " (no CDO animation - preview driven by the action blueprint)");
            }
            return;
        }
        if (sender is Border border && border.Tag is MoveInfo move)
        {
            ClearNodeSelection();
            SelectMoveCard(border);

            txtStatus.Text = $"Loading: {move.DisplayName}...";
            SetSourceTypeDisplay(move.SourceType);
            txtDisplayName.Text = "Display: -";

            await PreviewAsync(move.Character, move.FullPath);
        }
    }

    private void MoveCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            sender is Border aiBorder && aiBorder.Tag is AiNodeInfo aiNode)
        {
            _draggedCard = aiBorder;
            aiBorder.Opacity = 0.15;
            try
            {
                DragDrop.DoDragDrop(aiBorder, new DataObject(typeof(AiNodeInfo), aiNode), DragDropEffects.Copy);
            }
            finally
            {
                aiBorder.Opacity = 1;
                _draggedCard = null;
            }
            return;
        }
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
            "Action" => new SolidColorBrush(Color.FromRgb(0xf9, 0xe2, 0xaf)),
            "Event" => new SolidColorBrush(Color.FromRgb(0xfa, 0xb3, 0x87)),
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

    // The animation/mesh currently delivered to the preview viewer: re-clicking the node
    // whose animation is already loaded replays instead of re-parsing. _previewGen
    // invalidates in-flight loads when a newer preview request supersedes them (fast
    // A -> B click sequences must not let the stale A result overwrite B).
    private string? _previewAnimPath;
    private string? _previewMeshCharacter;
    private int _previewGen;

    /// <summary>
    /// Central preview entry used by every move/node click. When the requested animation
    /// AND character are already loaded the clip is replayed from frame 0 - no overlay,
    /// no package parse, no WebView traffic. Otherwise the mesh and the animation parse
    /// concurrently, but the animation's delivery waits for the mesh task: the viewer
    /// binds the clip to the mesh's bones (buildSkeleton prefers currentMesh), so the
    /// mesh must land first. The mesh itself is not re-sent when the character is
    /// unchanged.
    /// </summary>
    private async System.Threading.Tasks.Task PreviewAsync(string? character, string animPath)
    {
        bool sameAnim = string.Equals(animPath, _previewAnimPath, StringComparison.OrdinalIgnoreCase);
        bool sameMesh = string.IsNullOrEmpty(character)
            || string.Equals(character, _previewMeshCharacter, StringComparison.OrdinalIgnoreCase);

        if (sameAnim && sameMesh)
        {
            txtStatus.Text = $"Playing (already loaded): {animPath}";
            if (webView?.CoreWebView2 != null)
                await webView.CoreWebView2.ExecuteScriptAsync("window.seekTo(0); window.play();");
            return;
        }

        int gen = ++_previewGen;
        ShowPreviewOverlay();

        System.Threading.Tasks.Task meshTask =
            sameMesh || string.IsNullOrEmpty(character)
                ? System.Threading.Tasks.Task.CompletedTask
                : LoadMeshAsync(character, gen);

        await LoadAnimationAsync(animPath, gen, meshTask);
    }

    private async System.Threading.Tasks.Task LoadAnimationAsync(string gamePath, int gen, System.Threading.Tasks.Task? meshGate = null)
    {
        try
        {
            var data = await System.Threading.Tasks.Task.Run(() =>
                _parser.LoadAnimation(gamePath));
            if (gen != _previewGen) return; // superseded by a newer preview request

            if (data == null && Directory.Exists(CustomAssets.Root))
            {
                try { _parser.MountCustomIntoProvider(CustomAssets.Root); } catch { }
                data = await System.Threading.Tasks.Task.Run(() =>
                    _parser.LoadAnimation(gamePath));
                if (gen != _previewGen) return;
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

                _previewAnimPath = null; // failed - allow a re-click to retry
                HidePreviewOverlay();
                var escapedNull = nullMsg.Replace("'", "\\'");
                await webView.CoreWebView2.ExecuteScriptAsync($"window.showError('{escapedNull}')");
                return;
            }

            // Parse ran concurrently with the mesh; deliver only after it landed.
            if (meshGate != null) await meshGate;
            if (gen != _previewGen) return;

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

            if (gen != _previewGen) return; // superseded mid-delivery; the newer request owns the overlay
            _previewAnimPath = gamePath; // re-clicks now replay instead of re-parsing
            HidePreviewOverlay();
            txtStatus.Text = $"Loaded: {data.animation.name} ({data.animation.numFrames} frames, {data.animation.tracks.Length} tracks)";
        }
        catch (Exception ex)
        {
            if (gen != _previewGen) return;
            Console.Error.WriteLine($"[ANIMATION ERROR] {ex}");
            ErrorLog.Write("ANIMATION", ex);
            _previewAnimPath = null; // failed - allow a re-click to retry
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

    private async System.Threading.Tasks.Task LoadMeshAsync(string character, int gen)
    {
        try
        {
            var meshData = await System.Threading.Tasks.Task.Run(() => _parser.LoadMesh(character));
            if (gen != _previewGen) return; // superseded by a newer preview request
            if (meshData == null)
            {
                _previewMeshCharacter = null; // failed - allow a re-click to retry
                txtStatus.Text = $"No mesh found for {character}";
                return;
            }

            var json = _parser.ToJson(meshData);
            var escapedChar = character.Replace("'", "\\'");
            await webView.CoreWebView2.ExecuteScriptAsync($"window.loadMesh({json}, '{escapedChar}')");
            if (gen != _previewGen) return;
            _previewMeshCharacter = character; // unchanged character => no re-send on the next click
            txtStatus.Text = $"Loaded mesh for {character} ({meshData.positions.Length / 3} vertices)";
        }
        catch (Exception ex)
        {
            if (gen != _previewGen) return;
            Console.Error.WriteLine($"[MESH ERROR] {ex}");
            ErrorLog.Write("MESH", ex);
            _previewMeshCharacter = null;
            txtStatus.Text = $"Error loading mesh: {ex.Message}";
        }
    }

    private static bool IsBarehandsWeaponTag(string? weaponTag) =>
        !string.IsNullOrEmpty(weaponTag) && weaponTag.EndsWith("Barehands", StringComparison.OrdinalIgnoreCase);

    internal static string? NormalizeWeaponForCache(string arch, string? weaponTag)
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

    private string CurrentVariantTag()
    {
        var arch = _activeStance?.Split('|')[0] ?? "";
        return _activeVariant ?? $"{arch}_Base";
    }

    private void RestorePropsForCurrentVariant()
    {
        if (_currentUnitProps != null) return;
        if (_propsByVariant.TryGetValue(CurrentVariantTag(), out var stored) && stored != null)
            _currentUnitProps = stored;
    }

    private void SeedPropsByVariantFromCaches()
    {
        foreach (var kvp in _unitCaches)
        {
            var props = kvp.Value?.Props;
            if (props == null) continue;

            var parts = kvp.Key.Split('|');
            if (parts.Length < 2) continue;

            var tag = parts[1];
            if (string.IsNullOrEmpty(tag)) continue;

            if (!_propsByVariant.TryGetValue(tag, out var existing) || existing == null)
                _propsByVariant[tag] = props;
        }
    }

    /// <summary>
    /// Rebuilds the pending family flag from a loaded project, so a saved Immune-to-Focus toggle
    /// survives a restart instead of reverting to whatever is on disk. Seeds both states: an explicit
    /// saved false must win over an on-disk immune link, not silently show as checked.
    /// </summary>
    private void SeedPendingFocusFromCaches()
    {
        if (string.IsNullOrEmpty(_contentPath)) return;

        foreach (var kvp in _unitCaches)
        {
            if (kvp.Value?.Props?.ImmuneToFocus is not bool want) continue;

            var parts = kvp.Key.Split('|');
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[1])) continue;

            UnitPropertiesManager.SetPendingFocus(_contentPath, parts[1], want);
        }
    }

    private void SeedPendingModelFromCaches()
    {
        if (string.IsNullOrEmpty(_contentPath)) return;

        foreach (var kvp in _unitCaches)
        {
            var modelPath = kvp.Value?.Props?.ModelOverride;
            var modelSet = kvp.Value?.Props?.ModelSet;
            if (string.IsNullOrEmpty(modelPath) && string.IsNullOrEmpty(modelSet)) continue;

            var parts = kvp.Key.Split('|');
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[1])) continue;

            if (!string.IsNullOrEmpty(modelPath))
                UnitPropertiesManager.SetPendingModel(_contentPath, parts[1], modelPath);
            if (!string.IsNullOrEmpty(modelSet))
                UnitPropertiesManager.SetPendingModelSet(_contentPath, parts[1], modelSet);
        }
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
        // Flush a pending AttackDB edit before the graph (or unit) it belongs to is snapshotted.
        CommitAttackDbPanel();
        if (_comboGraph == null || string.IsNullOrEmpty(_activeStance)) return;

        bool keyIsMainChar = string.Equals(_activeStance.Split('|')[0], "MainChar", StringComparison.OrdinalIgnoreCase);
        bool graphHasStance = _comboGraph.Nodes.Any(n => n.Name == "MainChar_Stance");
        bool graphIsEnemy = _comboGraph.Nodes.Any(n =>
            !string.IsNullOrEmpty(n.DefaultDBPath) &&
            n.DefaultDBPath.Contains("/AI/Archetypes/", StringComparison.OrdinalIgnoreCase));
        // Graph-level identity only: a MainChar card dropped onto an enemy graph must
        // not make the enemy graph read as MainChar (it used to, skipping the cache
        // and losing the unit's edits on the next unit switch).
        bool graphIsMainCharWeapon = !keyIsMainChar && GraphIsMainChar(_comboGraph);

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
        SetComboInfo($"{key} - {_comboGraph.WeaponName} ({_comboGraph.Nodes.Count} nodes, {_comboGraph.Edges.Count} edges)");
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

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_comboGraph == null)
        {
            txtStatus.Text = "No combo graph loaded";
            return;
        }
        if (_opInProgress) return;

        SaveCurrentUnitToCache();

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Sifu Edit Files (*.sifu-edit)|*.sifu-edit|JSON Files (*.json)|*.json",
            Title = "Save Project",
            FileName = "CustomMoveset.sifu-edit"
        };

        if (dialog.ShowDialog() == true)
        {
            _opInProgress = true;
            try
            {
                var project = new EditorProject { Name = Path.GetFileNameWithoutExtension(dialog.FileName), CustomMoveset = _customMoveset };
                string saveArch = _activeStance?.Split('|')[0] ?? "MainChar";
                var stance = _activeStance;
                var variant = _activeVariant;
                var weapon = NormalizeWeaponForCache(saveArch, _activeWeapon);
                int unitCount = _unitCaches.Count;
                string filePath = dialog.FileName;
                // Serialization runs off-thread behind the overlay; it blocks input, so
                // the caches can't mutate while the background save walks them.
                await RunWithLoadingAsync("Saving project...", Path.GetFileName(filePath),
                    () => Task.Run(() => ProjectManager.SaveWithCaches(
                        project, _unitCaches, stance, variant, weapon, filePath)));
                txtStatus.Text = $"Saved project: {filePath} ({unitCount} units)";
                ShowProjectFeedbackDialog(true, filePath, project.Name);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save project:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _opInProgress = false;
            }
        }
    }

    private async void LoadProject_Click(object sender, RoutedEventArgs e)
    {
        CommitAttackDbPanel();
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Sifu Edit Files (*.sifu-edit)|*.sifu-edit|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            Title = "Open Project"
        };

        if (dialog.ShowDialog() == true)
        {
            _opInProgress = true;
            try
            {
                string filePath = dialog.FileName;
                var (project, restoredKey, ok) = await RunWithLoadingAsync(
                    "Loading project...", Path.GetFileName(filePath),
                    () => LoadProjectCoreAsync(filePath));
                if (ok && project != null)
                    ShowProjectFeedbackDialog(false, filePath, project.Name, restoredKey);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("PROJECT", ex);
                MessageBox.Show($"Failed to load project:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _opInProgress = false;
            }
        }
    }

    // Core of LoadProject_Click: runs behind the loading overlay (via
    // RunWithLoadingAsync) with staged UpdateLoading texts; the overlay is still
    // visible until this returns, so the feedback dialog must stay in the handler.
    private async Task<(EditorProject? project, string? restoredKey, bool ok)> LoadProjectCoreAsync(string filePath)
    {
        ClearUndoHistory();
        UpdateLoading("Loading project file...", Path.GetFileName(filePath));
        var (project, swaps, savedEdges, savedPositions, customNodes, redirectMods) =
            await Task.Run(() => ProjectManager.Load(filePath));

        // Embedded CustomAssets files restore before anything reads the custom root.
        // Conflicts (same rel, different content) ask the user all-or-nothing; Cancel
        // aborts the load with zero writes.
        if (project.CustomFilePayloads is { Count: > 0 })
        {
            UpdateLoading("Restoring custom files...", $"{project.CustomFilePayloads.Count} embedded");
            var restore = await Task.Run(() => CustomFileRestore.Apply(
                project.CustomFilePayloads, CustomAssets.Root, CustomRestoreStrategy.Ask,
                conflicts => Dispatcher.Invoke(() =>
                {
                    var dlg = new Custom.CustomAssetsConflictDialog { Owner = this };
                    dlg.SetConflicts(conflicts);
                    dlg.ShowDialog();
                    return dlg.Choice;
                })));
            if (restore.Cancelled)
            {
                txtStatus.Text = "Project load cancelled - file(s) exist in CustomAssets";
                return (null, null, false);
            }
            if (restore.Restored > 0 || restore.Replaced > 0)
            {
                try { _parser.MountCustomIntoProvider(CustomAssets.Root); } catch { }
                try { RegisterCustomMoves(); FilterMoves(); } catch { }
                ErrorLog.Write("PROJECT", new Exception(
                    $"Custom files: {restore.Restored} restored, {restore.Replaced} replaced, "
                    + $"{restore.SkippedIdentical} already matched, {restore.SkippedConflicts} conflict(s) kept"));
            }
            if (restore.Conflicts.Count > 0)
                txtStatus.Text = $"Custom files: {restore.Restored} restored, {restore.Replaced} replaced, "
                    + $"{restore.SkippedConflicts} kept existing";
        }

        _customMoveset = project.CustomMoveset;
        if (_customMoveset != null)
        {
            UpdateLoading("Restoring custom moveset...", _customMoveset.DisplayName);
            btnExport.IsEnabled = true;
        }

        UpdateLoading("Restoring unit caches...", project.Name);
        var loadedCaches = await Task.Run(() => ProjectManager.LoadUnitCaches(project));
        string? migrateNote = null;
        if (loadedCaches != null)
        {
            _unitCaches = loadedCaches;
            _propsByVariant.Clear();
            SeedPropsByVariantFromCaches();
            SeedPendingFocusFromCaches();
            SeedPendingModelFromCaches();
            migrateNote = await MigrateLegacyCachesAsync();
            await BackfillEdgeOwnersAsync();
        }

        string savedStance = project.ActiveStance ?? "MainChar";
        string? savedVariant = project.ActiveVariant;
        string? savedWeapon = project.ActiveWeapon;
        UpdateLoading("Loading combo graph...", savedStance);

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
            return (null, null, false);
        }

        UpdateLoading("Applying project changes...", "custom nodes, links, swaps, redirects");
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
                VanillaAnimPath = "",
                Kind = ComboNodeKind.UserCreated
            };
            _comboGraph.Nodes.Add(newNode);
        }

        if (project.RemovedNodes != null && project.RemovedNodes.Count > 0)
        {
            var removedIds = new HashSet<int>(project.RemovedNodes);
            foreach (var n in _comboGraph.Nodes)
                if (removedIds.Contains(n.Id)) n.Removed = true;
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

        // Old projects can hold two different values for one shared card - unify them
        // (live graph first, then caches) so the panel and export agree.
        int unifiedTuning = NormalizeSharedCardTuning();
        // After unify: drop overrides that display-identically match their card's vanilla
        // value (phantoms stamped by the old 0.0001 round-trip check).
        int clearedVanillaTuning = NormalizeVanillaEqualTuning();

        LayoutComboGraph();
        RenderComboGraph();
        string restoredNote = restoredKey != null ? $", restored {restoredKey}" : "";
        if (!string.IsNullOrEmpty(migrateNote)) restoredNote += ", " + migrateNote;
        if (unifiedTuning > 0)
        {
            restoredNote += $", unified {unifiedTuning} shared AttackDB value(s)";
            ErrorLog.Write("PROJECT", new Exception($"Load unified {unifiedTuning} diverged AttackDB value(s)"));
        }
        if (clearedVanillaTuning > 0)
        {
            restoredNote += $", cleared {clearedVanillaTuning} AttackDB value(s) identical to vanilla";
            ErrorLog.Write("PROJECT", new Exception($"Load cleared {clearedVanillaTuning} AttackDB value(s) identical to vanilla"));
        }
        txtStatus.Text = $"Loaded project: {project.Name} ({_unitCaches.Count} units{restoredNote}, {swaps.Count} swaps, {savedEdges.Count} edges, {customNodes.Count} custom nodes)";
        return (project, restoredKey, true);
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

    /// <summary>
    /// Post-import change summary: same list as save/load, filtered to the units
    /// this import actually touched. Imported sections are display-only - they do
    /// not affect export gates.
    /// </summary>
    private void ShowImportFeedback(IEnumerable<string> unitKeys, string fileName)
    {
        try
        {
            var keys = unitKeys
                .Where(k => !string.IsNullOrEmpty(k))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var units = ProjectChangeSummary.BuildFromUnitCaches(_unitCaches, _contentPath)
                .Where(u => keys.Contains(u.Key))
                .ToList();
            var dlg = new ProjectFeedbackDialog(
                "Import",
                "Imported mod changes",
                fileName,
                units,
                null)
            {
                Owner = this
            };
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("IMPORT_FEEDBACK", ex);
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

        CommitAttackDbPanel();
        SaveCurrentUnitToCache();

        if (_customMoveset != null)
        {
            if (!string.Equals(GetUnitCacheKey(), _customMoveset.CacheKey, StringComparison.OrdinalIgnoreCase))
            {
                txtStatus.Text = "Switch back to the custom moveset before exporting it.";
                return;
            }
            if (_comboGraph.Nodes.Count == 0)
            {
                txtStatus.Text = "The custom moveset has no nodes yet - add some before exporting.";
                return;
            }
        }

        bool hasMultiUnitChanges = _customMoveset == null && _unitCaches.Count > 1 && _unitCaches.Values.Any(c =>
            {
                var modified = c.Graph.Nodes.Any(ProjectChangeSummary.IsModifiedNode);
                var hasRetargets = c.Graph.RedirectOriginalTargets.Any(rd =>
                {
                    var node = c.Graph.Nodes.FirstOrDefault(n => n.Id == rd.Key);
                    return node != null && node.ResolvedRedirectNodeId >= 0 && node.ResolvedRedirectNodeId != rd.Value;
                });
                bool hasUnitProps = c.Props != null && UnitPropertiesManager.HasChanges(_contentPath, c.ActiveVariant ?? "", c.Props);
                bool hasAttackTuning = ProjectChangeSummary.GraphHasAttackDbTuning(c.Graph);
                bool hasNewContent = c.Graph.Edges.Any(ed => ed.IsNewLink);
                bool hasStartEntries = ProjectChangeSummary.GraphHasStartEntryChanges(c.Graph);
                bool hasChanceOrConditionEdits = c.Graph.TreeRowProbabilityOverrides.Count > 0
                    || c.Graph.TreeConditionParamOverrides.Count > 0
                    || c.Graph.TreeRowConditionOverrides.Count > 0;
                return modified || hasRetargets || hasUnitProps || hasAttackTuning || hasNewContent
                    || hasStartEntries || hasChanceOrConditionEdits;
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
            .Where(ProjectChangeSummary.IsModifiedNode)
            .ToList();

        bool hasRetargets = _comboGraph.RedirectOriginalTargets.Any(kvp =>
        {
            var node = _comboGraph.Nodes.FirstOrDefault(n => n.Id == kvp.Key);
            return node != null && node.ResolvedRedirectNodeId >= 0 && node.ResolvedRedirectNodeId != kvp.Value;
        });

        // MainChar has no variant popup, so _activeVariant stays null - its props still live
        // under the synthetic variant tag and must reach the export dialog for gating/staging.
        string? propsVariant = _activeVariant ?? (IsMainCharStance() ? CurrentVariantTag() : null);

        bool hasUnitProps = _currentUnitProps != null && propsVariant != null
            && UnitPropertiesManager.HasChanges(_contentPath, propsVariant, _currentUnitProps);

        bool hasAttackTuning = ProjectChangeSummary.GraphHasAttackDbTuning(_comboGraph);

        bool hasNewLinks = _comboGraph.Edges.Any(ed => ed.IsNewLink);

        bool hasStartEntries = ProjectChangeSummary.GraphHasStartEntryChanges(_comboGraph);

        // Chance/condition edits mutate the combo asset without touching nodes, so they
        // must open the export path on their own (chance-only was a latent gap).
        bool hasChanceOrConditionEdits = _comboGraph.TreeRowProbabilityOverrides.Count > 0
            || _comboGraph.TreeConditionParamOverrides.Count > 0
            || _comboGraph.TreeRowConditionOverrides.Count > 0;

        if (_customMoveset == null && modified.Count == 0 && !stanceChanged && !hasRetargets && !hasUnitProps && !hasAttackTuning && !hasNewLinks && !hasStartEntries && !hasChanceOrConditionEdits)
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
        if (_customMoveset != null)
            enemyComboPath = _customMoveset.DonorComboRel;
        else if (_activeVariant != null && !string.Equals(_activeStance, "MainChar", StringComparison.OrdinalIgnoreCase))
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
            propsVariant,
            mainCharComboPath,
            _customMoveset)
        {
            Owner = this,
        };

        dialog.ShowDialog();
    }

    private async void ImportMod_Click(object sender, RoutedEventArgs e)
    {
        CommitAttackDbPanel();
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
        // Short root in %TEMP%: UnrealPak cannot WRITE extraction outputs whose full path
        // is exactly 260 characters (legacy MAX_PATH dead zone) and only logs a warning -
        // the old <app>\ImportTemp\a\b\c prefix hit the zone and silently dropped files.
        // Prefix here is ~56 chars; the zone would need rel paths of 204+, real max is ~152.
        try
        {
            foreach (var stale in Directory.GetDirectories(Path.GetTempPath(), "SifuImp*"))
                try { Directory.Delete(stale, true); } catch { }
        }
        catch { }
        var importTempRoot = Path.Combine(Path.GetTempPath(),
            "SifuImp" + Guid.NewGuid().ToString("N").Substring(0, 6));
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

                // UnrealPak exits 0 even when it cannot write a file (260-char dead zone);
                // those files would be lost forever, so abort instead of importing partial content.
                var extractSkipped = new List<string>();
                foreach (Match m in Regex.Matches(stdout + "\n" + stderr,
                    "(?:Unable to create file|Missing file) \"([^\"]+)\""))
                    if (!extractSkipped.Contains(m.Groups[1].Value)) extractSkipped.Add(m.Groups[1].Value);
                if (extractSkipped.Count > 0)
                {
                    Fail($"Extraction skipped {extractSkipped.Count} file(s) - the import would be incomplete.",
                        string.Join("\n", extractSkipped));
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
                var treeCandidates = new List<(string path, string rel, ImportComboEntry entry)>();
                foreach (var f in extractedUassets)
                {
                    var baseName = Path.GetFileNameWithoutExtension(f);
                    if (comboCatalog.TryGetValue(baseName, out var entry))
                        treeCandidates.Add((f, Path.GetRelativePath(importTempRoot, f), entry));
                }

                static bool IsBackupVariant(string rel) =>
                    rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Any(seg => seg.Equals("0Backup", StringComparison.OrdinalIgnoreCase)
                                 || seg.Equals("Old", StringComparison.OrdinalIgnoreCase)
                                 || seg.Contains("_Copy", StringComparison.OrdinalIgnoreCase));

                // Real trees win over 0Backup/_Copy/Old copies; a backup copy is only used when
                // no real tree exists for that unit. One tree per unit (previously directory
                // enumeration order decided, which let the 0Backup copy overwrite the real tree).
                var chosenTrees = new Dictionary<string, (string path, ImportComboEntry entry)>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in treeCandidates)
                    if (!IsBackupVariant(c.rel)) chosenTrees.TryAdd(c.entry.UnitKey, (c.path, c.entry));
                foreach (var c in treeCandidates)
                    if (IsBackupVariant(c.rel)) chosenTrees.TryAdd(c.entry.UnitKey, (c.path, c.entry));
                var discoveredTrees = chosenTrees.Values.ToList();

                foreach (var c in treeCandidates)
                {
                    bool chosen = chosenTrees.TryGetValue(c.entry.UnitKey, out var ch)
                                  && string.Equals(ch.path, c.path, StringComparison.OrdinalIgnoreCase);
                    ErrorLog.Write("IMPORT", new Exception(
                        (chosen ? "Discovered combo tree: "
                             : IsBackupVariant(c.rel) ? "Skipped backup-variant combo tree: "
                             : "Skipped duplicate combo tree: ")
                        + $"{c.rel} → {c.entry.UnitKey} ({c.entry.GamePath})"));
                }
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

                // Focus flag. The repoint lands on the ArchetypeDB that *owns* the family's
                // VitalPointDB link, which for Grunt/Bodyguard/FlashKick/Fajar/Sifu/Servant is
                // DB/AI/_Shared/BP_Base_ArchetypeDB rather than any single unit's file - so scan by
                // family rel instead of by unit tag, which is what the loop above can only do.
                if (!string.IsNullOrEmpty(_contentPath))
                {
                    try
                    {
                        var vanillaContentDir = Setup.ContentDetector.ResolveContentDir(_contentPath);
                        foreach (var archRel in UnitPropertiesManager.AllArchetypeRelPaths())
                        {
                            var archBase = Path.GetFileNameWithoutExtension(
                                archRel.Replace('/', Path.DirectorySeparatorChar));
                            if (!extractedBasenames.TryGetValue(archBase, out var archFile)) continue;
                            if (UnitPropertiesManager.ReadFocusImmunityFromArchetypeAsset(archFile, vanillaContentDir) != true)
                                continue;

                            int matched = 0;
                            foreach (var variantTag in UnitPropertiesManager.AllVariantTags)
                            {
                                var fam = UnitPropertiesManager.GetFocusFamilyKey(_contentPath, variantTag);
                                if (fam == null || !fam.Equals(archRel, StringComparison.OrdinalIgnoreCase)) continue;

                                if (!importedProps.TryGetValue(variantTag, out var p))
                                    importedProps[variantTag] = p = new UnitProperties();
                                p.ImmuneToFocus = true;
                                matched++;
                            }

                            ErrorLog.Write("IMPORT", new Exception($"[FOCUS] {archRel} repointed in mod - immune flag restored for {matched} unit(s)"));
                        }
                    }
                    catch (Exception focusEx)
                    {
                        ErrorLog.Write("IMPORT", focusEx);
                    }
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
                var danglingRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (hasComboTree)
                {
                    // Content root up front: the readers need it for content-relative
                    // ImportComboRel provenance, and the overlay provider must be live
                    // before any vanilla resolution runs.
                    string? overlayContentRoot = FindContentRootUnder(importTempRoot);
                    if (overlayContentRoot != null)
                        _parser.SetOverlayProvider(_parser.CreateOverlayProvider(overlayContentRoot));

                    // Step 4: Parse modded combo nodes (UAssetAPI)
                    UI(() => { importDialog.UpdateStep(4, "active"); importDialog.SetCurrentAction("Parsing modded animations..."); importDialog.SetProgress(45); });
                    var moddedByUnit = new Dictionary<string, List<ModdedComboNodeInfo>>(StringComparer.OrdinalIgnoreCase);
                    var moddedStartByUnit = new Dictionary<string, List<ModdedStartEntryInfo>>(StringComparer.OrdinalIgnoreCase);
                    int parseIdx = 0;
                    foreach (var (path, entry) in discoveredTrees)
                    {
                        parseIdx++;
                        var pct = 45 + (int)(25.0 * parseIdx / discoveredTrees.Count);
                        UI(() => importDialog.SetCurrentAction($"Parsing {Path.GetFileName(path)}..."));
                        var nodes = _parser.ReadModdedComboNodes(path, overlayContentRoot);
                        if (nodes.Count == 0)
                        {
                            ErrorLog.Write("IMPORT", new Exception($"No nodes parsed from {path}"));
                            continue;
                        }
                        moddedByUnit[entry.UnitKey] = nodes;
                        var startEntries = _parser.ReadModdedStartEntries(path);
                        if (startEntries.Count > 0)
                            moddedStartByUnit[entry.UnitKey] = startEntries;
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

                    // Pre-scan: flag DB references that exist in neither the mod nor vanilla —
                    // these cannot resolve and will keep their vanilla anims.
                    if (overlayContentRoot != null)
                    {
                        try
                        {
                            string vanillaContentDir = AttackDbContentDir();
                            foreach (var (_, nodes) in moddedByUnit)
                                foreach (var n in nodes)
                                    foreach (var p in n.AttackDbPaths)
                                    {
                                        var modUasset = AttackDbCard.ResolveUassetPath(overlayContentRoot, p);
                                        var vanillaUasset = AttackDbCard.ResolveUassetPath(vanillaContentDir, p);
                                        bool inMod = modUasset != null && File.Exists(modUasset);
                                        bool inVanilla = vanillaUasset != null && File.Exists(vanillaUasset);
                                        if (!inMod && !inVanilla)
                                            danglingRefs.Add(p);
                                    }
                            foreach (var d in danglingRefs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                                ErrorLog.Write("IMPORT", new Exception($"[PRESCAN] Dangling DB ref (absent from mod AND vanilla): {d}"));
                            if (danglingRefs.Count > 0)
                                ErrorLog.Write("IMPORT", new Exception($"[PRESCAN] {danglingRefs.Count} dangling DB ref(s) — these keep vanilla anims"));
                        }
                        catch (Exception dEx)
                        {
                            ErrorLog.Write("IMPORT", dEx);
                        }
                    }

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

                        var applyResult = _parser.ApplyModdedComboToVanilla(
                            vanilla, moddedNodes, entry.WeaponName,
                            moddedStartByUnit.TryGetValue(entry.UnitKey, out var mStarts) ? mStarts : null);
                        int attackTuning = ApplyAttackDbImport(vanilla, overlayContentRoot);
                        importedUnits.Add((entry.UnitKey, vanilla, applyResult.MoveChanges, applyResult.RetargetChanges, entry));
                        int vanillaRawCount = vanilla.RawNodeCount > 0
                            ? vanilla.RawNodeCount
                            : vanilla.Nodes
                                .Where(n => n.TreeIndex >= 0)
                                .Select(n => n.TreeIndex)
                                .Distinct()
                                .Count();
                        ErrorLog.Write("IMPORT", new Exception($"Applied {entry.UnitKey}: {applyResult.MoveChanges} move(s), {applyResult.RetargetChanges} retarget(s), {applyResult.Unmatched} unmatched, {applyResult.Surfaced} new node(s) surfaced, {applyResult.AiChanges} AI payload(s), {applyResult.ChanceOverrides} chance override(s), {applyResult.CondClears + applyResult.CondInstances} cond rewire(s), {applyResult.CondParamWrites} cond param(s), {applyResult.CondSkipped} cond skip(s), {applyResult.StartTransitionsChanged} start row(s), {applyResult.StartStructuralSkipped} start add(s) skipped, {attackTuning} attackDb tune(s) (mod={moddedNodes.Count} vanillaRaw={vanillaRawCount})"));
                        if (applyResult.AiRemovalsSkipped > 0)
                            ErrorLog.Write("IMPORT", new Exception($"{entry.UnitKey}: {applyResult.AiRemovalsSkipped} AI payload removal(s) kept vanilla"));
                        if (moddedNodes.Count != vanillaRawCount)
                            treeMismatches.Add($"{entry.UnitKey}: {moddedNodes.Count} mod node(s) vs {vanillaRawCount} vanilla");
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
                        SeedPropsByVariantFromCaches();
                        SeedPendingFocusFromCaches();
                        SeedPendingModelFromCaches();
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
                            ApplySharedCardTuningToGraph(graph);
                        }
                        ErrorLog.Write("IMPORT", new Exception($"Unit props applied to {unitKey}"));
                    }

                    // Re-seed now that imported props actually live in _unitCaches: the earlier call
                    // at the cache-restore step runs before this merge, so a restored Immune-to-Focus
                    // flag would never reach _propsByVariant / FocusPending - and PopulateUnitProperties
                    // would then overwrite it with the on-disk value the first time the unit is opened.
                SeedPropsByVariantFromCaches();
                SeedPendingFocusFromCaches();
                SeedPendingModelFromCaches();

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
                if (danglingRefs.Count > 0)
                {
                    var danglingLine = $"Dangling DB ref(s): {danglingRefs.Count} (absent from mod and vanilla — kept vanilla)";
                    details = details != null ? $"{details}\n{danglingLine}" : danglingLine;
                }
                if (importedProps.Count > 0)
                {
                    var propLine = $"Unit Properties: {importedProps.Count}";
                    details = details != null ? $"{details}\n{propLine}" : propLine;
                }
                if (!string.IsNullOrEmpty(_contentPath))
                {
                    var focusFamilies = importedProps
                        .Where(kv => kv.Value.ImmuneToFocus == true)
                        .Select(kv => UnitPropertiesManager.GetFocusFamilyKey(_contentPath, kv.Key))
                        .Where(f => !string.IsNullOrEmpty(f))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (focusFamilies.Count > 0)
                    {
                        var focusLine = $"Immune to Focus: {string.Join(", ", focusFamilies)}";
                        details = details != null ? $"{details}\n{focusLine}" : focusLine;
                    }
                }
                UI(() => importDialog.ShowSuccess(summary, details));
                UI(() => ShowImportFeedback(
                    finalImported.Select(u => u.unitKey)
                        .Concat(importedProps.Keys.Select(v => $"{GetArchFromVariant(v)}|{v}")),
                    Path.GetFileName(pakPath)));
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
                    // Import registers the harvested cards itself, so the watcher must
                    // not pop the "New Custom File(s)" dialog over the import result.
                    _suppressCustomWatcher = true;
                    int harvested = CustomAssets.Harvest(importTempRoot, AttackDbContentDir());
                    _suppressCustomWatcher = false;
                    // Import harvest wrote its own files - drop any queued events so a
                    // flush after the flag clears can't pop the watcher dialog.
                    lock (_customWatcherPending) _customWatcherPending.Clear();
                    if (harvested > 0)
                    {
                        _parser.MountCustomIntoProvider(CustomAssets.Root);
                        RegisterCustomMoves();
                        Dispatcher.Invoke(FilterMoves);
                        ErrorLog.Write("IMPORT", new Exception($"Harvested {harvested} custom file(s) to CustomAssets"));
                    }
                }
            }
            catch (Exception hex) { _suppressCustomWatcher = false; ErrorLog.Write("IMPORT", hex); }
            try { if (Directory.Exists(importTempRoot)) Directory.Delete(importTempRoot, true); } catch { }
        }
    }

    /// <summary>
    /// Reads the imported mod's attack cards from the overlay root and stores
    /// m_iWantedBuildupFrames / m_fGameplayRange diffs plus per-window hit-box diffs
    /// (frames on the card, the rest from the HitBoxData row) on each node whose card
    /// differs from vanilla. Returns how many nodes picked up a tuning change.
    /// </summary>
    private int ApplyAttackDbImport(ComboGraph graph, string? overlayContentRoot)
    {
        if (graph == null || string.IsNullOrEmpty(overlayContentRoot)) return 0;

        string vanillaRoot = AttackDbContentDir();
        int tuned = 0;
        int hitTuned = 0;
        var sharedRowCards = new List<string>();
        foreach (var node in graph.Nodes)
        {
            if (node.IsRedirect || string.IsNullOrEmpty(node.DefaultDBPath)) continue;

            // The card the mod's combo tree actually points at: the swapped one when a slot
            // was redirected, otherwise the node's own default card.
            string modCard = AttackDbCard.EffectiveCardPath(node);
            if (string.IsNullOrEmpty(modCard)) continue;
            var modVals = AttackDbCard.ReadDbPath(overlayContentRoot, modCard);
            if (modVals == null || !modVals.Any) continue;

            var vanilla = AttackDbCard.ReadDbPath(vanillaRoot, modCard);

            // Pure custom card (absent from vanilla content): the mod's file itself
            // carries its values and lands in CustomAssets via the import harvest -
            // no per-field buildup/range edits against a missing baseline.
            if (vanilla == null) continue;

            int? buildup = null;
            float? range = null;
            if (modVals.HasBuildup && (!vanilla.HasBuildup || vanilla.Buildup != modVals.Buildup))
                buildup = modVals.Buildup;
            if (modVals.HasRange && (!vanilla.HasRange
                || !AttackDbCard.SameRange(vanilla.Range, modVals.Range)))
                range = modVals.Range;

            var modHit = AttackHitCard.ReadDbPath(overlayContentRoot, modCard);
            var hitEdits = AttackHitCard.DiffAgainst(AttackHitCard.ReadDbPath(vanillaRoot, modCard), modHit);

            if (buildup == null && range == null && hitEdits == null) continue;

            node.AttackBuildupFrames = buildup;
            node.AttackGameplayRange = range;
            if (buildup != null || range != null) tuned++;

            if (hitEdits != null)
            {
                node.HitEdits = hitEdits;
                hitTuned++;
                if (modHit != null && hitEdits.Any(e => AttackHitCard.HasRowFields(e)
                        && e.Index < modHit.Windows.Count && modHit.Windows[e.Index].SharedRow))
                    sharedRowCards.Add(Path.GetFileName(AttackDbCard.GamePathToContentRel(modCard)));
            }
        }

        if (tuned > 0)
            ErrorLog.Write("IMPORT", new Exception($"AttackDB tuning picked up on {tuned} node(s) in '{graph.WeaponName}'"));
        if (hitTuned > 0)
            ErrorLog.Write("IMPORT", new Exception($"AttackDB hit-window tuning picked up on {hitTuned} node(s) in '{graph.WeaponName}'"
                + (sharedRowCards.Count > 0
                    ? $" (shared HitBoxData row with other attacks: {string.Join(", ", sharedRowCards.Distinct())})"
                    : "")));
        return tuned + hitTuned;
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

    /// <summary>
    /// Registers everything in the persistent CustomAssets root as Custom-tab moves:
    /// animation files (unit from the Animations/&lt;unit&gt; folder) plus attack cards
    /// (unit from DB/AI/Archetypes/&lt;unit&gt;). Idempotent - paths already present in
    /// _allMoves are skipped, so it can run after every harvest or watcher refresh.
    /// </summary>
    private void RegisterCustomMoves()
    {
        if (_allMoves == null || !Directory.Exists(CustomAssets.Root)) return;

        var animsRoot = Path.Combine(CustomAssets.Root, "Animations");
        if (Directory.Exists(animsRoot))
        {
            foreach (var file in Directory.GetFiles(animsRoot, "*.uasset", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(CustomAssets.Root, file).Replace('\\', '/');
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

        // Custom attack cards - unit grouping comes from the archetype folder. Must run
        // after MountCustomIntoProvider so HasAttackStruct can load them from the provider.
        foreach (var card in _parser.ScanCustomAttackDbs(CustomAssets.Root))
        {
            if (_allMoves.Any(m => !string.IsNullOrEmpty(m.FullPath) &&
                    m.FullPath.Equals(card.FullPath, StringComparison.OrdinalIgnoreCase)))
                continue;
            _allMoves.Add(card);
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

            SetComboInfo($"MainChar - {_comboGraph.WeaponName} ({_comboGraph.Nodes.Count} nodes, {_comboGraph.Edges.Count} edges)");
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

    /// <summary>Node ids targeted by start entries (TargetNodeId + KeyTargetNodeIds)
    /// that are redirect nodes resolving to an existing target. These stay visible on
    /// the canvas even without edges - vanilla routes some AI tickets through a
    /// redirect nothing else links to (e.g. FD Base tree 16), and hiding those made
    /// the Start Entries jump button fail while the target combo rendered blank.
    /// Dead redirects (destination deleted) are excluded - they stay hidden.</summary>
    private HashSet<int> ComputeLiveStartEntryRedirectIds()
    {
        var ids = new HashSet<int>();
        if (_comboGraph?.StartEntries == null) return ids;
        foreach (var entry in _comboGraph.StartEntries)
            foreach (var t in entry.Transitions)
            {
                AddIfLiveRedirect(t.TargetNodeId);
                if (t.KeyTargetNodeIds == null) continue;
                foreach (var id in t.KeyTargetNodeIds) AddIfLiveRedirect(id);
            }
        return ids;

        void AddIfLiveRedirect(int id)
        {
            if (id < 0) return;
            var n = _comboGraph.Nodes.FirstOrDefault(x => x.Id == id);
            if (n?.IsRedirect == true && n.ResolvedRedirectNodeId >= 0
                && _comboGraph.Nodes.Any(x => x.Id == n.ResolvedRedirectNodeId))
                ids.Add(id);
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

        var graphIds = new HashSet<int>(_comboGraph.Nodes.Select(n => n.Id));
        var orphanIds = new HashSet<int>(_comboGraph.Nodes
            .Where(n => n.IsRedirect && (n.ResolvedRedirectNodeId < 0
                || !_comboGraph.Nodes.Any(x => x.Id == n.ResolvedRedirectNodeId)))
            .Select(n => n.Id));

        // Saved positions restore AFTER ResolveNodeOverlaps ran inside the topological
        // pass, so restored and computed nodes were never checked against each other -
        // re-home the computed ones around them instead (saved/hand-placed never move).
        // Beginner view: a hidden AI node's saved position is dropped here instead -
        // it must not anchor nor obstacle anything while invisible (the next arrange
        // with AI shown recomputes it fresh).
        var hiddenIds = new HashSet<int>(_comboGraph.Nodes.Where(IsBeginnerHiddenAi).Select(n => n.Id));
        var anchors = new HashSet<int>();
        foreach (var kvp in savedPositions)
        {
            if (graphIds.Contains(kvp.Key) && !orphanIds.Contains(kvp.Key) && !hiddenIds.Contains(kvp.Key))
            {
                _nodePositions[kvp.Key] = kvp.Value;
                anchors.Add(kvp.Key);
            }
        }
        PushComputedNodesOffAnchors(_nodePositions, anchors);
    }

    /// <summary>Final overlap pass: nodes in <paramref name="anchorIds"/> (restored
    /// saved positions) are immovable; every other node is pushed down past whatever
    /// it overlaps until its slot is clear of the whole set. Deterministic order
    /// (X then Y) and each push clears all current overlaps at once.</summary>
    public static void PushComputedNodesOffAnchors(Dictionary<int, Point> positions, ISet<int> anchorIds)
    {
        if (positions.Count < 2) return;
        double rowHeight = NODE_HEIGHT + V_SPACING;
        var movable = positions.Keys.Where(id => !anchorIds.Contains(id))
            .OrderBy(id => positions[id].X).ThenBy(id => positions[id].Y).ToList();
        foreach (var id in movable)
        {
            var pos = positions[id];
            for (int guard = 0; guard < 10000; guard++)
            {
                bool any = false;
                double lowest = double.NegativeInfinity;
                foreach (var kv in positions)
                {
                    if (kv.Key == id) continue;
                    if (Math.Abs(kv.Value.X - pos.X) < NODE_WIDTH && Math.Abs(kv.Value.Y - pos.Y) < rowHeight)
                    {
                        if (!any || kv.Value.Y > lowest) lowest = kv.Value.Y;
                        any = true;
                    }
                }
                if (!any) break;
                // Push below the lowest overlapping node: clears every current overlap
                // in one step (lowest >= each hit's Y) and strictly increases Y.
                pos = new Point(pos.X, lowest + rowHeight);
            }
            positions[id] = pos;
        }
    }

    private void LayoutComboGraph_Topological()
    {
        // Beginner view: hidden AI nodes take no column, no depth and no position -
        // every edge loop below already guards on layoutIds/parentsById, so chains
        // simply route around them and no slot is reserved for an invisible box.
        var layoutNodes = _comboGraph!.Nodes.Where(n => !n.IsRedirect && !IsBeginnerHiddenAi(n)).ToList();
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

        // ---- crossing reduction: order each column by the average position of its
        // parents (down sweep) then its children (up sweep), re-stacking rows as we
        // go. OrderBy is stable, so the TreeIndex/forwardDepth seed order survives
        // wherever the barycenter keys tie - edges fan out tree-style instead of
        // crossing whenever the asset order disagrees with the graph shape.
        double sweepRowHeight = NODE_HEIGHT + V_SPACING;
        var sweepY = new Dictionary<int, double>();
        void RestackColumn(int d)
        {
            for (int i = 0; i < columns[d].Count; i++)
                sweepY[columns[d][i].Id] = 30 + i * sweepRowHeight;
        }
        for (int d = 0; d < columns.Count; d++) RestackColumn(d);

        var parentsById = new Dictionary<int, List<int>>();
        foreach (var node in layoutNodes) parentsById[node.Id] = new List<int>();
        foreach (var edge in _comboGraph.Edges)
            if (parentsById.ContainsKey(edge.ToNodeId))
                parentsById[edge.ToNodeId].Add(edge.FromNodeId);

        double Bary(ComboNode n, Dictionary<int, List<int>> neighbors)
        {
            if (!neighbors.TryGetValue(n.Id, out var list) || list.Count == 0) return sweepY[n.Id];
            double sum = 0;
            int count = 0;
            foreach (var nb in list)
            {
                if (sweepY.TryGetValue(nb, out var y)) { sum += y; count++; }
            }
            return count > 0 ? sum / count : sweepY[n.Id];
        }

        for (int d = 1; d < columns.Count; d++)
        {
            columns[d] = columns[d].OrderBy(n => Bary(n, parentsById)).ToList();
            RestackColumn(d);
        }
        for (int d = columns.Count - 2; d >= 0; d--)
        {
            columns[d] = columns[d].OrderBy(n => Bary(n, outgoing)).ToList();
            RestackColumn(d);
        }

        // ---- column wrap: split overlong columns into balanced sub-columns so a
        // huge second column (boss graphs) doesn't run off the canvas. Empty depth
        // gaps keep their slot; each sub-column consumes one cumulative X slot.
        if (columns.Any(c => c.Count > MaxColumnRows))
        {
            var wrapped = new List<List<ComboNode>>();
            foreach (var col in columns)
            {
                if (col.Count == 0 || col.Count <= MaxColumnRows)
                {
                    wrapped.Add(col);
                    continue;
                }
                int sub = (col.Count + MaxColumnRows - 1) / MaxColumnRows;
                int per = (col.Count + sub - 1) / sub;
                for (int s = 0; s < sub; s++)
                    wrapped.Add(col.Skip(s * per).Take(per).ToList());
            }
            columns = wrapped;
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
        // AI-flagged redirects go unpositioned while hidden (they reposition on the
        // next arrange with AI shown); non-AI redirects whose only source is hidden
        // are skipped by the source lookup below - their incoming link is hidden too.
        // Start-entry redirects qualify even without edges (no incoming link to hang
        // them off - they are placed off their resolved target below instead).
        var startEntryRedirectIds = ComputeLiveStartEntryRedirectIds();
        var validRedirects = _comboGraph.Nodes
            .Where(n => n.IsRedirect && n.ResolvedRedirectNodeId >= 0
                && (connectedNodeIds.Contains(n.Id) || startEntryRedirectIds.Contains(n.Id))
                && !IsBeginnerHiddenAi(n)).ToList();

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

        // Start-entry redirects with no incoming edge: hang them off their resolved
        // target (col+1, same overlap resolution) so they get a canvas home and the
        // Start Entries jump button has a box to reveal.
        foreach (var r in validRedirects)
        {
            if (_nodePositions.ContainsKey(r.Id)) continue; // already placed by source
            if (!_nodePositions.TryGetValue(r.ResolvedRedirectNodeId, out var tgtPos)) continue;
            double x = tgtPos.X + colWidth;
            double y = tgtPos.Y;
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
            _nodePositions[r.Id] = new Point(x, y);
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
                    // Symmetric 60% pull toward the parent center - children sit on their
                    // parent's vertical line instead of only ever being dragged down
                    // (was Math.Max-clamped). The push-down pass below re-spaces anything
                    // this tightens or re-orders.
                    double newY = pos.Y + (targetY - pos.Y) * 0.6;
                    _nodePositions[nodeId] = new Point(pos.X, newY);
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

    // Single source of truth for beginner-view hiding (render set builder, layout,
    // redirect placement, anchor restore). Reads the flag directly - the render-only
    // _hiddenAiNodeIds set is stale whenever layout runs before RenderComboGraph.
    private bool IsBeginnerHiddenAi(ComboNode n)
        => !_showAiNodes && !n.IsRoot && (n.IsActionNode || n.IsEventNode);

    // True when the node is hidden from the canvas by the beginner view setting.
    private bool TouchesHiddenAi(int nodeId)
        => _hiddenAiNodeIds != null && _hiddenAiNodeIds.Contains(nodeId);

    // How many AI action/event nodes the current graph is hiding (root excluded).
    private int CountHiddenAiNodes()
        => _showAiNodes || _comboGraph == null ? 0
            : _comboGraph.Nodes.Count(IsBeginnerHiddenAi);

    // Info-bar suffix so "N nodes" matches what is actually on screen.
    private string AiHiddenSuffix()
    {
        int n = CountHiddenAiNodes();
        return n > 0 ? $" (+{n} AI hidden)" : "";
    }

    private string _comboInfoBase = "";

    // Sets the info bar to baseText + the current AI-hidden suffix and remembers the
    // base so ApplyShowAiNodes can refresh the suffix without knowing the context.
    private void SetComboInfo(string baseText)
    {
        _comboInfoBase = baseText;
        txtComboInfo.Text = baseText + AiHiddenSuffix();
    }

    private void RefreshComboInfoBar()
    {
        if (_comboInfoBase.Length > 0)
            txtComboInfo.Text = _comboInfoBase + AiHiddenSuffix();
    }

    /// <summary>
    /// Applies the "Show AI Actions & Events" setting to surfaces other than the
    /// canvas: library button visibility (with a fallback out of AI library mode),
    /// retarget/link cancellation (their nodes may now be hidden) and a re-render.
    /// When <paramref name="aiVisibilityChanged"/> is true with a graph open, runs the
    /// full Auto-arrange pattern (undoable) so the arrangement matches the visible set
    /// - hidden AI nodes take no column, shown AI nodes get re-integrated.
    /// Returns true when a re-arrange actually ran (drives the status message).
    /// </summary>
    private bool ApplyShowAiNodes(bool aiVisibilityChanged = false)
    {
        btnLibAi.Visibility = _showAiNodes ? Visibility.Visible : Visibility.Collapsed;
        if (!_showAiNodes)
        {
            if (_aiLibraryMode)
                SetLibraryMode(false);
            if (_isRetargetMode)
                ExitRetargetMode();
            if (_linkSourceNode != null)
                ExitLinkMode();
        }
        bool rearranged = aiVisibilityChanged && _comboGraph != null && !_opInProgress;
        if (rearranged)
        {
            // Same pattern as AutoArrange_Click: PushUndo captures the old arrangement
            // first, then the empty table makes LayoutComboGraph lay out from scratch.
            PushUndo("AI visibility toggle");
            _nodePositions.Clear();
            LayoutComboGraph();
        }
        RefreshComboInfoBar();
        RenderComboGraph();
        return rearranged;
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
            _hiddenAiNodeIds = null;
            UpdateComboCanvasSize();
            return;
        }

        // Beginner view: rebuild the hidden AI node set each render (null = show all).
        // Root nodes are never hidden so the graph always keeps its anchor.
        _hiddenAiNodeIds = _showAiNodes ? null
            : new HashSet<int>(_comboGraph.Nodes.Where(IsBeginnerHiddenAi).Select(n => n.Id));
        if (TouchesHiddenAi(_selectedNodeId))
            _selectedNodeId = -1;

        var existingNodeIds = new HashSet<int>(_comboGraph.Nodes.Select(n => n.Id));
        var nodeIdsWithEdges = new HashSet<int>();
        foreach (var edge in _comboGraph.Edges)
        {
            nodeIdsWithEdges.Add(edge.FromNodeId);
            nodeIdsWithEdges.Add(edge.ToNodeId);
        }
        // Start-entry redirects (live target) are exempt from the orphan hide: vanilla
        // routes some AI tickets through a redirect nothing else links to - hiding it
        // blanked the Start Entries combo and broke the jump button.
        var startEntryRedirectIds = ComputeLiveStartEntryRedirectIds();
        var orphanRedirectIds = new HashSet<int>(_comboGraph.Nodes
            .Where(n => n.IsRedirect && !startEntryRedirectIds.Contains(n.Id)
                && (n.ResolvedRedirectNodeId < 0
                || !existingNodeIds.Contains(n.ResolvedRedirectNodeId)
                || !nodeIdsWithEdges.Contains(n.Id)))
            .Select(n => n.Id));

        var edgesBySource = new Dictionary<int, List<ComboEdge>>();
        foreach (var edge in _comboGraph.Edges)
        {
            if (orphanRedirectIds.Contains(edge.FromNodeId) || orphanRedirectIds.Contains(edge.ToNodeId))
                continue;
            // Hidden AI links are excluded here so fan-out sibling indices, the
            // conflict scan below and the render loop all see the same edge set.
            if (TouchesHiddenAi(edge.FromNodeId) || TouchesHiddenAi(edge.ToNodeId))
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
                    // Literal "None" rows (imported auto-continue) are not real inputs -
                    // several of them on one node is normal branching, not a conflict.
                    if (string.Equals(e.InputName, "None", StringComparison.OrdinalIgnoreCase)) continue;
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
            if (edge.FromNodeId == -1) continue;
            if (!_nodePositions.TryGetValue(edge.FromNodeId, out var fromPos)) continue;
            if (!_nodePositions.TryGetValue(edge.ToNodeId, out var toPos)) continue;

            if (orphanRedirectIds.Contains(edge.FromNodeId) || orphanRedirectIds.Contains(edge.ToNodeId))
                continue;
            if (TouchesHiddenAi(edge.FromNodeId) || TouchesHiddenAi(edge.ToNodeId))
                continue;

            if (_isRetargetMode && edge.IsRedirect && _retargetSourceNode != null && edge.FromNodeId == _retargetSourceNode.Id)
                continue;

            if (edge.IsRedirect)
                continue;

            bool isIncomingToRedirect = _comboGraph.Nodes.Any(n => n.Id == edge.ToNodeId && n.IsRedirect);
            var color = GetEdgeBrush(edge);

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

            double thickness = GetEdgeThickness(edge);

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
            if (edge.IsNewLink)
            {
                var edgeMenu = new ContextMenu();
                var deleteLinkItem = new MenuItem { Header = "Delete Link", Tag = edge };
                deleteLinkItem.Click += DeleteEdgeMenuItem_Click;
                edgeMenu.Items.Add(deleteLinkItem);
                hitPath.ContextMenu = edgeMenu;
                path.ContextMenu = edgeMenu;
            }
            path.MouseLeftButtonDown += Edge_ResetMode_MouseLeftButtonDown;
            hitPath.MouseLeftButtonDown += Edge_ResetMode_MouseLeftButtonDown;
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
            if (TouchesHiddenAi(node.Id)) continue;
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
                Opacity = node.Removed ? 0.4 : 1.0,
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
                bool isCustomNode = node.TreeIndex < 0 && node.Kind == ComboNodeKind.UserCreated;
                if (node.Removed)
                {
                    var restoreItem = new MenuItem { Header = "Restore Node", Tag = node };
                    restoreItem.Click += RestoreNodeMenuItem_Click;
                    ctxMenu.Items.Add(restoreItem);
                }
                else
                {
                    if (!isCustomNode)
                    {
                        var resetItem = new MenuItem { Header = "Reset to Vanilla", Tag = node };
                        resetItem.Click += ResetNodeToVanilla_Click;
                        ctxMenu.Items.Add(resetItem);
                    }

                    if (!IsCurrentGraphMainChar()
                        && (HasBranchChanceData(node) || HasConditionData(node) || CanBackfillBranchRows(node)))
                    {
                        var chancesItem = new MenuItem { Header = "Conditions & Chances…", Tag = node };
                        chancesItem.Click += BranchChancesMenuItem_Click;
                        ctxMenu.Items.Add(chancesItem);
                    }

                    if (!node.IsRedirect)
                    {
                        var connectItem = new MenuItem { Header = "Connect to…", Tag = node };
                        connectItem.Click += ConnectTo_Click;
                        ctxMenu.Items.Add(connectItem);

                        var redirectViaItem = new MenuItem { Header = "Add Redirect via new node…", Tag = node };
                        redirectViaItem.Click += RedirectVia_Click;
                        ctxMenu.Items.Add(redirectViaItem);
                    }

                    if (node.IsRedirect)
                    {
                        var retargetItem = new MenuItem { Header = "Retarget", Tag = node };
                        retargetItem.Click += RetargetRedirect_Click;
                        ctxMenu.Items.Add(retargetItem);
                    }

                    var highlightRedirectsItem = new MenuItem { Header = "Highlight connected redirects", Tag = node };
                    highlightRedirectsItem.Click += HighlightConnectedRedirects_Click;
                    ctxMenu.Items.Add(highlightRedirectsItem);

                    var deleteItem = new MenuItem { Header = isCustomNode ? "Delete Node" : "Remove Node", Tag = node };
                    deleteItem.Click += DeleteNodeMenuItem_Click;
                    ctxMenu.Items.Add(deleteItem);
                }

                border.ContextMenu = ctxMenu;
            }

            Canvas.SetLeft(border, pos.X);
            Canvas.SetTop(border, pos.Y);
            comboCanvas.Children.Add(border);
            Canvas.SetZIndex(border, NodeZIndex);
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
                Canvas.SetZIndex(inputBg, NodeInputBgZIndex);
                _nodeInputBgs[node.Id] = inputBg;
            }

            var labelText = node.DisplayName.Length > 14 ? node.DisplayName[..14] + ".." : node.DisplayName;
            if (node.Removed) labelText = "✕ " + labelText;
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
                TextDecorations = node.Removed ? TextDecorations.Strikethrough : null,
                Tag = node.Id
            };
            var contentGrid = new Grid();
            string? badgeLine = null;
            if (node.ReplacedByAiNode)
            {
                badgeLine = node.ReplacedAiKind == "Attack"
                    ? $"REPLACE> attack {node.ReplacedAiClass}"
                    : $"REPLACE> {node.ReplacedAiKind}: {node.ReplacedAiClass}";
                if (badgeLine.Length > 26) badgeLine = badgeLine[..26] + "..";
            }
            else if (node.IsActionNode || node.IsEventNode)
            {
                badgeLine = node.IsActionNode
                    ? string.IsNullOrEmpty(node.ActionClass) ? "ACTION" : "ACTION: " + node.ActionClass
                    : string.IsNullOrEmpty(node.EventName) ? "EVENT" : "EVENT: " + node.EventName;
                if (badgeLine.Length > 24) badgeLine = badgeLine[..24] + "..";
            }
            if (badgeLine != null)
            {
                var kindBadge = new TextBlock
                {
                    Text = badgeLine,
                    Foreground = node.ReplacedByAiNode
                        ? new SolidColorBrush(Color.FromArgb(0xCC, 0xa6, 0xe3, 0xa1))
                        : new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                    FontSize = 7,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Center,
                    Width = NODE_WIDTH,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(label, 0);
                Grid.SetRow(kindBadge, 1);
                contentGrid.Children.Add(label);
                contentGrid.Children.Add(kindBadge);
                if (node.ReplacedByAiNode)
                {
                    string origPayload = node.IsActionNode ? $"action {node.ActionClass}"
                        : node.IsEventNode ? $"event {node.EventName}"
                        : "attack";
                    string nextPayload = node.ReplacedAiKind == "Attack"
                        ? $"attack card {node.ReplacedAiClass}"
                        : $"{node.ReplacedAiKind.ToLowerInvariant()} {node.ReplacedAiClass}";
                    border.ToolTip = $"Payload replaced on export\n{origPayload} -> {nextPayload}";
                }
                else
                {
                    border.ToolTip = node.IsActionNode
                        ? $"AI action node\n{node.ActionClass}"
                        : $"Combo event node\n{node.EventName}";
                }
            }
            else
            {
                contentGrid.Children.Add(label);
            }

            border.Child = contentGrid;
            _nodeLabels[node.Id] = label;
        }

        ComputeSelectionChain();
        ApplySelectionStyles();
        RefreshAttackDbPanel();
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
            // Same visible-only filter as RenderComboGraph's edgesBySource build,
            // otherwise fan geometry shifts when a hidden sibling exists.
            var siblings = _comboGraph.Edges.Where(e => e.FromNodeId == edge.FromNodeId
                && !TouchesHiddenAi(e.FromNodeId) && !TouchesHiddenAi(e.ToNodeId)).ToList();
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
        if (e.Key == Key.Escape && _isNodePickMode)
        {
            var resume = _pendingPickResume;
            var graphAtPick = _pendingPickGraph;
            ExitNodePickMode();
            txtStatus.Text = "Target pick cancelled";
            e.Handled = true;
            if (_comboGraph != null && graphAtPick != null && ReferenceEquals(_comboGraph, graphAtPick))
                resume?.Invoke();
            return;
        }

        if (e.Key == Key.Escape && _isRetargetMode)
        {
            ExitRetargetMode();
            txtStatus.Text = "Retarget cancelled";
            e.Handled = true;
            return;
        }


        if (e.Key == Key.Escape && _linkSourceNode != null)
        {
            ExitLinkMode();
            txtStatus.Text = "Link cancelled";
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && _comboGraph != null)
        {
            if (_selectedNodeId >= 0)
            {
                var node = _comboGraph.Nodes.FirstOrDefault(n => n.Id == _selectedNodeId);
                if (node == null || node.IsRoot || node.TreeIndex >= 0) return;

                DeleteAuthoredNode(node);
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
        _panDownPos = _panStart;
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
            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                _dragCandidate = false;
                _dragNodeId = -1;
            }
            else if ((DateTime.UtcNow - _dragStartTime).TotalMilliseconds > 150)
            {
                PushUndo("Move node");
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
        double dx = panPos.X - _panStart.X;
        double dy = panPos.Y - _panStart.Y;
        if (Math.Abs(panPos.X - _panDownPos.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(panPos.Y - _panDownPos.Y) > SystemParameters.MinimumVerticalDragDistance)
            _panMoved = true;
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
    }

    private void ComboBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateComboCanvasSize();
    }

    private void UpdateComboCanvasSize()
    {
        double maxRight = 0;
        double maxBottom = 0;
        foreach (var kvp in _nodePositions)
        {
            // Hidden AI nodes must not pad the canvas with empty scroll space.
            if (TouchesHiddenAi(kvp.Key)) continue;
            var pos = kvp.Value;
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
        else if (_linkSourceNode != null)
        {
            ExitLinkMode();
            txtStatus.Text = "Link cancelled";
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
        if (!_isPanning) return;
        bool wasClick = !_panMoved;
        StopPanning();
        if (!wasClick || e.ChangedButton != MouseButton.Left || _selectedNodeId < 0) return;
        if (_nodeBorders.TryGetValue(_selectedNodeId, out var selBorder))
        {
            var upPos = e.GetPosition(comboCanvas);
            double left = Canvas.GetLeft(selBorder);
            double top = Canvas.GetTop(selBorder);
            if (left <= upPos.X && upPos.X <= left + selBorder.ActualWidth
                && top <= upPos.Y && upPos.Y <= top + selBorder.ActualHeight)
                return;
        }
        ClearNodeSelection();
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
        CommitAttackDbPanel();
        StopCustomAssetsWatcher();
        PromptKeepCustomAssets();
        SaveSettings();
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
                    });
                });
        }
        else
        {
            SaveSettings();
        }
    }

    /// <summary>
    /// Close-time gate for the persistent CustomAssets library. Files that are new or
    /// modified against the last accepted manifest trigger the styled prompt: Yes keeps
    /// them (manifest saved into settings, so the same files are never re-asked), No -
    /// button, Esc, or window X - swaps the dialog into a "Deleting files…" progress
    /// state and removes only those unaccepted files (everything previously accepted
    /// stays). The deletion runs off the UI thread, so close never stalls.
    /// </summary>
    private void PromptKeepCustomAssets()
    {
        try
        {
            var current = CustomAssets.BuildManifest();
            if (current.Count == 0)
            {
                // Folder is empty - first run, or nothing left worth asking about.
                if (_customAssetsAccepted != null && _customAssetsAccepted.Count > 0)
                    _customAssetsAccepted = null;
                return;
            }

            if (CustomAssets.ManifestEquals(current, _customAssetsAccepted)) return;

            bool firstTime = _customAssetsAccepted == null || _customAssetsAccepted.Count == 0;
            string text = firstTime
                ? "Want to save the custom files in the Custom Section?"
                : "Want to save the new custom files added?";
            var unaccepted = CustomAssets.NewOrChanged(current, _customAssetsAccepted);

            var dlg = new KeepCustomAssetsDialog(text, unaccepted) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _customAssetsAccepted = current;
                return;
            }

            int removed = dlg.RemovedCount;
            var unacceptedSet = new HashSet<string>(unaccepted, StringComparer.OrdinalIgnoreCase);
            var survivors = current.Where(e => !unacceptedSet.Contains(e)).ToList();
            _customAssetsAccepted = survivors.Count > 0 ? survivors : null;
            if (removed > 0)
                ErrorLog.Write("CUSTOM", new Exception(
                    $"Close prompt: discarded {removed} unaccepted custom file(s)"));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("CUSTOM", ex);
        }
    }

    // ---- CustomAssets file watcher -------------------------------------------------

    private void StartCustomAssetsWatcher()
    {
        try
        {
            CustomAssets.EnsureRoot();
            _customWatcherDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _customWatcherDebounce.Tick += CustomWatcherDebounce_Tick;

            _customWatcher = new FileSystemWatcher(CustomAssets.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                    | NotifyFilters.Size | NotifyFilters.CreationTime
            };
            _customWatcher.Created += (_, e) => QueueCustomWatcherEvent(e.FullPath);
            _customWatcher.Changed += (_, e) => QueueCustomWatcherEvent(e.FullPath);
            _customWatcher.Deleted += (_, e) => QueueCustomWatcherEvent(e.FullPath);
            _customWatcher.Renamed += (_, e) =>
            {
                QueueCustomWatcherEvent(e.OldFullPath);
                QueueCustomWatcherEvent(e.FullPath);
            };
            _customWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("CUSTOM", ex);
        }
    }

    private void StopCustomAssetsWatcher()
    {
        try
        {
            _customWatcherDebounce?.Stop();
            if (_customWatcher != null)
            {
                _customWatcher.EnableRaisingEvents = false;
                _customWatcher.Dispose();
                _customWatcher = null;
            }
        }
        catch { }
    }

    private void QueueCustomWatcherEvent(string fullPath)
    {
        if (_suppressCustomWatcher) return;
        try
        {
            var rel = Path.GetRelativePath(CustomAssets.Root, fullPath).Replace('\\', '/');
            if (rel.StartsWith("..", StringComparison.Ordinal)) return;
            lock (_customWatcherPending)
            {
                _customWatcherPending.Add(rel);
            }
            Dispatcher.BeginInvoke(() =>
            {
                _customWatcherDebounce?.Stop();
                _customWatcherDebounce?.Start();
            });
        }
        catch { }
    }

    /// <summary>
    /// Flushes coalesced watcher events: rows for files that still exist are shown (or
    /// re-shown) in the popup, assets whose files vanished are unregistered from the
    /// Custom list, and an empty row set closes the popup.
    /// </summary>
    private void CustomWatcherDebounce_Tick(object? sender, EventArgs e)
    {
        _customWatcherDebounce?.Stop();
        if (_suppressCustomWatcher) return;

        List<string> touched;
        lock (_customWatcherPending)
        {
            if (_customWatcherPending.Count == 0) return;
            touched = _customWatcherPending.ToList();
            _customWatcherPending.Clear();
        }
        ErrorLog.Write("CUSTOM", new Exception(
            $"Watcher: {touched.Count} file event(s): {string.Join(", ", touched.Take(8))}"));

        bool removedSome = false;
        foreach (var rel in touched)
        {
            if (File.Exists(Path.Combine(CustomAssets.Root, rel.Replace('/', Path.DirectorySeparatorChar))))
                continue;

            // Companion-only deletion (uexp/ubulk while the uasset survives) keeps the card.
            var stem = CustomAssets.AssetStemOfRel(rel);
            if (stem.Equals(rel, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(CustomAssets.FileForContentRel(stem + ".uasset"))) continue;

            var gamePath = "Game/" + stem;
            if (_allMoves.RemoveAll(m =>
                    string.Equals(m.Category, "Custom", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(m.FullPath, gamePath, StringComparison.OrdinalIgnoreCase)) > 0)
                removedSome = true;
        }

        if (removedSome) FilterMoves();

        lock (_customWatcherPending)
        {
            foreach (var rel in touched)
            {
                var display = CustomAssets.DisplayNameForRel(rel);
                if (File.Exists(Path.Combine(CustomAssets.Root, rel.Replace('/', Path.DirectorySeparatorChar))))
                    _customWatchRows.Add(display);
                else
                    _customWatchRows.Remove(display);
            }
        }

        UpdateCustomFilesPopup();
    }

    private void UpdateCustomFilesPopup()
    {
        try
        {
            if (_customWatchRows.Count == 0)
            {
                if (_customFilesDialog != null)
                {
                    var dlg = _customFilesDialog;
                    _customFilesDialog = null;
                    dlg.Close();
                }
                return;
            }

            if (_customFilesDialog == null)
            {
                var dlg = new CustomFilesDetectedDialog { Owner = this };
                dlg.Closed += (_, _) =>
                {
                    bool addToLibrary = dlg.AddToLibrary;
                    if (ReferenceEquals(_customFilesDialog, dlg)) _customFilesDialog = null;
                    _customWatchRows.Clear();
                    if (addToLibrary)
                    {
                        try
                        {
                            _parser.MountCustomIntoProvider(CustomAssets.Root);
                            RegisterCustomMoves();
                            FilterMoves();
                            txtStatus.Text = "Custom files added to the Custom section";
                        }
                        catch (Exception ex) { ErrorLog.Write("CUSTOM", ex); }
                    }
                };
                _customFilesDialog = dlg;
                dlg.SetFiles(_customWatchRows.ToList());
                dlg.Show();
            }
            else
            {
                _customFilesDialog.SetFiles(_customWatchRows.ToList());
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("CUSTOM", ex);
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_initialized) return;

        var focused = FocusManager.GetFocusedElement(this);
        if (focused is TextBox) return;

        var mods = Keyboard.Modifiers;
        if (e.Key == Key.Z && mods == ModifierKeys.Control)
        {
            Undo();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Z && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            Redo();
            e.Handled = true;
            return;
        }

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
        if (node.IsActionNode)
            return new SolidColorBrush(Color.FromRgb(0xf2, 0xcd, 0xcd));
        if (node.IsEventNode)
            return new SolidColorBrush(Color.FromRgb(0xf5, 0xc2, 0xe7));
        if (node.Kind == ComboNodeKind.ImportedNew)
            return new SolidColorBrush(Color.FromRgb(0xcb, 0xa6, 0xf7));
        if (node.Kind == ComboNodeKind.UserCreated)
            return new SolidColorBrush(Color.FromRgb(0x94, 0xe2, 0xd5));
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
        if (node.IsActionNode)
            return new SolidColorBrush(Color.FromArgb(0x40, 0xf2, 0xcd, 0xcd));
        if (node.IsEventNode)
            return new SolidColorBrush(Color.FromArgb(0x40, 0xf5, 0xc2, 0xe7));
        if (node.Kind == ComboNodeKind.ImportedNew)
            return new SolidColorBrush(Color.FromArgb(0x40, 0xcb, 0xa6, 0xf7));
        if (node.Kind == ComboNodeKind.UserCreated)
            return new SolidColorBrush(Color.FromArgb(0x40, 0x94, 0xe2, 0xd5));
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

        // Replacement-aware order: a replaced node must land on the card/item that
        // replaced it, never the vanilla DefaultDBPath (which never changes on a
        // drop). 1) payload-replaced attack card, 2) the drop's own identity (a
        // replace sets AnimPath = move.FullPath), 3) the current card
        // (SourceDBPath when swapped, else default), 4) anim->DB map,
        // 5) vanilla card as fallback, 6) caller animPath last resort.
        // Vanilla nodes are unaffected: AnimPath == DefaultAnimPath skips step 2
        // and step 3 resolves to DefaultDBPath - same jump as before.
        AddSearchPath(sourceNode?.ReplacedAttackCardRel);
        if (sourceNode != null
            && !string.Equals(sourceNode.AnimPath, sourceNode.DefaultAnimPath,
                StringComparison.OrdinalIgnoreCase))
            AddSearchPath(sourceNode.AnimPath);
        AddSearchPath(AttackDbCard.EffectiveCardPath(sourceNode));
        if (_parser.AnimToDbPath.TryGetValue(animPath, out var mappedDb))
            AddSearchPath(mappedDb);
        AddSearchPath(sourceNode?.DefaultDBPath);
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
            if (_isNodePickMode)
            {
                HandleNodePickClick(node);
                return;
            }

            if (_isRetargetMode)
            {
                if (_retargetSourceNode == null || _comboGraph == null
                    || _comboGraph.Nodes.All(n => n.Id != _retargetSourceNode.Id))
                {
                    ExitRetargetMode();
                    txtStatus.Text = "Retarget cancelled (moveset changed)";
                    return;
                }
                if (node.Id != _retargetSourceNode.Id && !node.IsRoot && !node.IsRedirect && !node.Removed)
                    ApplyRetarget(_retargetSourceNode, node);
                ExitRetargetMode();
                RenderComboGraph();
                return;
            }

            if (_linkSourceNode != null)
            {
                HandleLinkTargetClick(node, e);
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
                        if (targetNode.IsActionNode)
                            SetSourceTypeDisplay("Action");
                        else if (targetNode.IsEventNode)
                            SetSourceTypeDisplay("Event");
                        else if (!string.IsNullOrEmpty(targetNode.AnimPath) || !string.IsNullOrEmpty(targetNode.DefaultDBPath))
                            SetSourceTypeDisplay(MoveInfo.SourceTypeFromPath(ComboNodeTypePath(targetNode)));
                                if (!string.IsNullOrEmpty(targetNode.AnimPath))
                                {
                                    txtStatus.Text = $"Loading: {targetNode.DisplayName} ({targetNode.AnimPath})...";
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
                                        await PreviewAsync(previewChar, targetNode.AnimPath);
                                    }
                            catch (Exception ex)
                            {
                                ErrorLog.Write("REDIRECT_DBLCLK", new Exception($"Node '{targetNode.DisplayName}' (Path={targetNode.AnimPath}): {ex.Message}"));
                                txtStatus.Text = $"Error loading {targetNode.DisplayName}: {ex.Message}";
                            }
                        }
                        else if (targetNode.IsEventNode)
                        {
                            txtStatus.Text = $"Event: {targetNode.DisplayName} - events fire gameplay logic and have no animation";
                        }
                        else if (targetNode.IsActionNode)
                        {
                            await HandleActionNodeClickAsync(targetNode);
                        }
                        else
                        {
                            txtStatus.Text = $"Target '{targetNode.DisplayName}' has no animation path";
                        }
                        if (targetNode.Name == "MainChar_Stance")
                            ShowToast("Combat Stance — not in library");
                        else if (!targetNode.IsActionNode && !targetNode.IsEventNode)
                            await FindCardInLibrary(targetNode.AnimPath, targetNode);
                        return;
                    }
                }
                if (node.Name == "MainChar_Stance")
                    ShowToast("Combat Stance — not in library");
                else if (node.ReplacedByAiNode && node.ReplacedAiKind == "Attack"
                    && !string.IsNullOrEmpty(node.ReplacedAttackCardRel))
                    await FindCardInLibrary(node.ReplacedAttackCardRel, node);
                else if (node.ReplacedByAiNode && node.ReplacedAiKind == "Action")
                    await HandleActionNodeClickAsync(node);
                else if (node.ReplacedByAiNode && node.ReplacedAiKind == "Event")
                    txtStatus.Text = $"Event: {(string.IsNullOrEmpty(node.ReplacedAiClass) ? node.DisplayName : node.ReplacedAiClass)}"
                        + " - events fire gameplay logic and have no animation";
                else if (node.IsActionNode)
                    await HandleActionNodeClickAsync(node);
                else if (node.IsEventNode)
                    txtStatus.Text = $"Event: {node.DisplayName} - events fire gameplay logic and have no animation";
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
            if (node.IsActionNode)
                SetSourceTypeDisplay("Action");
            else if (node.IsEventNode)
                SetSourceTypeDisplay("Event");
            else if (!string.IsNullOrEmpty(typePath))
                SetSourceTypeDisplay(MoveInfo.SourceTypeFromPath(typePath));
            else
                SetSourceTypeDisplay("Other");

            UpdateNodeDisplayNameLabel(node);

            if (!string.IsNullOrEmpty(node.AnimPath))
            {
                txtStatus.Text = $"Loading: {node.DisplayName} ({node.AnimPath})...";
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
                    await PreviewAsync(previewChar, node.AnimPath);
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("COMBO_CLICK", new Exception($"Node '{node.DisplayName}' (Name={node.Name}, Path={node.AnimPath}): {ex.Message}"));
                    txtStatus.Text = $"Error loading {node.DisplayName}: {ex.Message}";
                }
            }
            else if (node.IsEventNode)
            {
                txtStatus.Text = $"Event: {node.DisplayName} - events fire gameplay logic and have no animation";
            }
            else if (node.IsActionNode)
            {
                await HandleActionNodeClickAsync(node);
            }
            else
            {
                txtStatus.Text = $"Node '{node.DisplayName}' has no animation path";
            }
        }
    }

    /// <summary>
    /// Action nodes store no animation themselves - the animation belongs to the action
    /// blueprint (its CDO m_Animation). Resolve it through the cached AI catalog so the
    /// node previews exactly like its library card; otherwise report honestly why not.
    /// </summary>
    private async System.Threading.Tasks.Task HandleActionNodeClickAsync(ComboNode node)
    {
        if (!_aiScanStarted)
        {
            txtStatus.Text = $"Resolving action '{node.ActionClass}'...";
            await EnsureAiCatalogAsync();
        }
        // An AI-replaced node keeps its original ActionClass; the replacing class
        // lives in ReplacedAiClass - preview through the one that actually runs.
        string className = node.ReplacedByAiNode && node.ReplacedAiKind == "Action"
            && !string.IsNullOrEmpty(node.ReplacedAiClass)
                ? node.ReplacedAiClass
                : string.IsNullOrEmpty(node.ActionClass) ? node.DisplayName : node.ActionClass;
        var info = _aiNodes.FirstOrDefault(n =>
            n.Kind == "Action" &&
            (string.Equals(n.ClassName, className, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(n.DisplayName, className, StringComparison.OrdinalIgnoreCase)));

        if (info?.PreviewAnimPath != null)
        {
            txtStatus.Text = $"Action: {node.DisplayName} ({info.SourceUnit}) - anim: {info.PreviewAnimPath}";
            try
            {
                string previewChar = "MainChar";
                if (info.PreviewAnimPath.Contains("/"))
                {
                    var segs = info.PreviewAnimPath.Split('/');
                    int idx = Array.FindIndex(segs, s => s.Equals("Animations", StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0 && idx + 1 < segs.Length) previewChar = segs[idx + 1];
                }
                await PreviewAsync(previewChar, info.PreviewAnimPath);
                return;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("ACTION_CLICK", new Exception($"Node '{node.DisplayName}' (anim={info.PreviewAnimPath}): {ex.Message}"));
                txtStatus.Text = $"Action: {node.DisplayName} - preview failed ({ex.Message})";
                return;
            }
        }

        txtStatus.Text = info != null
            ? $"Action: {node.DisplayName} - animation driven by the action BP (no CDO animation to preview)"
            : $"Action: {node.DisplayName} - animation driven by the action BP ({className})";
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

        PushUndo("Retarget");

        var oldEdge = _comboGraph.Edges.FirstOrDefault(e => e.FromNodeId == redirectNode.Id && e.IsRedirect);
        if (oldEdge != null) _comboGraph.Edges.Remove(oldEdge);

        _comboGraph.Edges.Add(new ComboEdge
        {
            FromNodeId = redirectNode.Id,
            ToNodeId = newTarget.Id,
            InputName = oldEdge?.InputName ?? "",
            IsRedirect = true
        });

        int oldTargetId = redirectNode.ResolvedRedirectNodeId;
        redirectNode.ResolvedRedirectNodeId = newTarget.Id;
        MoveOwnedRedirectRows(redirectNode, oldTargetId, newTarget.Id);
        SwapRedirectRowConditions(redirectNode, newTarget);

        RenderComboGraph();
        txtStatus.Text = $"Retargeted '{redirectNode.DisplayName}' → '{newTarget.DisplayName}'";
    }

    /// <summary>
    /// A' pass-through: a redirect's re-sourced rows live on the attack node it plays,
    /// so a retarget has to carry them from the old play target to the new one or
    /// export's owner-writeback would append to the wrong struct.
    /// </summary>
    private void MoveOwnedRedirectRows(ComboNode redirectNode, int oldTargetId, int newTargetId)
    {
        if (_comboGraph == null || oldTargetId < 0 || newTargetId < 0 || oldTargetId == newTargetId)
            return;
        var owned = _comboGraph.Edges
            .Where(e => e.OwnerRedirectNodeId == redirectNode.Id && e.FromNodeId == oldTargetId)
            .ToList();
        if (owned.Count == 0) return;
        foreach (var o in owned) _comboGraph.Edges.Remove(o);
        foreach (var o in owned)
        {
            var existing = _comboGraph.Edges.FirstOrDefault(e =>
                e.FromNodeId == newTargetId && e.ToNodeId == o.ToNodeId);
            if (existing != null)
            {
                if (existing.OwnerRedirectNodeId < 0 && !existing.IsNewLink)
                    existing.OwnerRedirectNodeId = redirectNode.Id;
            }
            else
            {
                _comboGraph.Edges.Add(new ComboEdge
                {
                    FromNodeId = newTargetId,
                    ToNodeId = o.ToNodeId,
                    InputName = o.InputName,
                    OwnerRedirectNodeId = redirectNode.Id,
                    IsNewLink = o.IsNewLink
                });
            }
        }
    }

    /// <summary>
    /// Data swap on retarget (user-confirmed): outcome keys whose effective target is
    /// the redirect itself - a vanilla m_TargetNodes value, a Retarget entry, or a
    /// node-id entry - are rewritten to the new play target. RF's Conditions &amp;
    /// Chances option therefore becomes the destination, accepting that rows which
    /// used to branch on the redirect's own transition rows now branch on the
    /// destination's. Provenance records which redirect moved each key so a later
    /// re-retarget can move them again even though their effective target is no
    /// longer the redirect.
    /// </summary>
    private void SwapRedirectRowConditions(ComboNode redirectNode, ComboNode newTarget)
    {
        if (_comboGraph == null || redirectNode.TreeIndex < 0) return;
        BackfillTreeRows(); // effective-target math needs the vanilla row snapshot
        _comboGraph.RetargetProvenance ??= new Dictionary<string, int>();
        var prov = _comboGraph.RetargetProvenance;
        int redirectTree = redirectNode.TreeIndex;
        int newTree = newTarget.TreeIndex;

        foreach (var structKvp in _comboGraph.TreeRowConditionOverrides)
        {
            _comboGraph.TreeRows.TryGetValue(structKvp.Key, out var vanilla);
            foreach (var rowKvp in structKvp.Value)
            {
                var edit = rowKvp.Value;
                TreeRowInfo? vr = vanilla?.FirstOrDefault(r => r.RowIndex == rowKvp.Key);

                if (edit.ClearCondition)
                {
                    // A cleared row routes on its single surviving key (255).
                    int survivor = -1;
                    if (vr != null && vr.Keys.Count > 0)
                    {
                        int ki = vr.Keys.IndexOf(0);
                        survivor = ki >= 0 && ki < vr.TargetTreeIndices.Count
                            ? vr.TargetTreeIndices[ki]
                            : vr.TargetTreeIndices.Count > 0 ? vr.TargetTreeIndices[0] : -1;
                    }
                    if (edit.Retarget.TryGetValue(255, out var rt255) && rt255 >= 0) survivor = rt255;
                    if (survivor == redirectTree && newTree >= 0)
                    {
                        edit.Retarget[255] = newTree;
                        edit.RetargetNodeIds.Remove(255);
                        prov[ComboGraph.RowRetargetKey(structKvp.Key, rowKvp.Key, 255)] = redirectNode.Id;
                    }
                }
                else
                {
                    var keys = new HashSet<byte>(edit.Retarget.Keys);
                    if (vr != null) foreach (var k in vr.Keys) keys.Add(k);
                    foreach (byte key in keys)
                    {
                        int vanillaT = -1;
                        if (vr != null)
                        {
                            int ki = vr.Keys.IndexOf(key);
                            if (ki >= 0 && ki < vr.TargetTreeIndices.Count) vanillaT = vr.TargetTreeIndices[ki];
                        }
                        int eff = edit.Retarget.TryGetValue(key, out var rt) && rt >= 0 ? rt : vanillaT;
                        if (eff == redirectTree && newTree >= 0)
                        {
                            edit.Retarget[key] = newTree;
                            prov[ComboGraph.RowRetargetKey(structKvp.Key, rowKvp.Key, key)] = redirectNode.Id;
                        }
                    }
                }

                foreach (byte key in edit.RetargetNodeIds.Keys.ToList())
                {
                    if (edit.RetargetNodeIds[key] == redirectNode.Id)
                    {
                        edit.RetargetNodeIds[key] = newTarget.Id;
                        prov[ComboGraph.RowRetargetKey(structKvp.Key, rowKvp.Key, key)] = redirectNode.Id;
                    }
                }
                foreach (byte key in edit.AddedKeys.Keys.ToList())
                {
                    if (edit.AddedKeys[key] == redirectNode.Id)
                    {
                        edit.AddedKeys[key] = newTarget.Id;
                        prov[ComboGraph.RowRetargetKey(structKvp.Key, rowKvp.Key, key)] = redirectNode.Id;
                    }
                }
            }
        }

        foreach (var edgeKvp in _comboGraph.TreeEdgeConditionOverrides)
        {
            var edit = edgeKvp.Value;
            foreach (byte key in edit.Retarget.Keys.ToList())
            {
                if (edit.Retarget[key] == redirectTree && newTree >= 0)
                {
                    edit.Retarget[key] = newTree;
                    prov[ComboGraph.EdgeRetargetKey(edgeKvp.Key, key)] = redirectNode.Id;
                }
            }
            foreach (byte key in edit.RetargetNodeIds.Keys.ToList())
            {
                if (edit.RetargetNodeIds[key] == redirectNode.Id)
                {
                    edit.RetargetNodeIds[key] = newTarget.Id;
                    prov[ComboGraph.EdgeRetargetKey(edgeKvp.Key, key)] = redirectNode.Id;
                }
            }
            foreach (byte key in edit.AddedKeys.Keys.ToList())
            {
                if (edit.AddedKeys[key] == redirectNode.Id)
                {
                    edit.AddedKeys[key] = newTarget.Id;
                    prov[ComboGraph.EdgeRetargetKey(edgeKvp.Key, key)] = redirectNode.Id;
                }
            }
        }

        // Re-retarget: entries this redirect moved earlier point at the previous
        // destination now, so the equality pass above misses them - force them onto
        // the new target; stale claims (row edit reverted) just drop.
        foreach (var p in prov.Where(kv => kv.Value == redirectNode.Id).ToList())
        {
            string pk = p.Key;
            if (pk.StartsWith("R:", StringComparison.Ordinal))
            {
                var parts = pk.Split(':');
                if (parts.Length != 4 || !int.TryParse(parts[1], out int st)
                    || !int.TryParse(parts[2], out int ri) || !byte.TryParse(parts[3], out byte key))
                    continue;
                if (!_comboGraph.TreeRowConditionOverrides.TryGetValue(st, out var rows)
                    || !rows.TryGetValue(ri, out var edit))
                    continue;
                if (edit.RetargetNodeIds.ContainsKey(key))
                    edit.RetargetNodeIds[key] = newTarget.Id;
                else if (edit.AddedKeys.ContainsKey(key))
                    edit.AddedKeys[key] = newTarget.Id;
                else if (edit.Retarget.ContainsKey(key) && newTree >= 0)
                    edit.Retarget[key] = newTree;
                else
                    prov.Remove(pk); // row edit reverted - the claim is stale
            }
            else if (pk.StartsWith("E:", StringComparison.Ordinal))
            {
                var parts = pk.Split(':');
                if (parts.Length != 3 || !byte.TryParse(parts[2], out byte key)) continue;
                if (!_comboGraph.TreeEdgeConditionOverrides.TryGetValue(parts[1], out var edit))
                    continue;
                if (edit.RetargetNodeIds.ContainsKey(key))
                    edit.RetargetNodeIds[key] = newTarget.Id;
                else if (edit.AddedKeys.ContainsKey(key))
                    edit.AddedKeys[key] = newTarget.Id;
                else if (edit.Retarget.ContainsKey(key) && newTree >= 0)
                    edit.Retarget[key] = newTree;
                else
                    prov.Remove(pk);
            }
        }
    }

    /// <summary>
    /// Reset-to-vanilla for a redirect: rows this redirect's retargets moved (the
    /// data swap's provenance claims) drop their override entries so routing falls
    /// back to the vanilla m_TargetNodes values - which still name the redirect.
    /// </summary>
    private void RevertRedirectRowConditions(ComboNode redirectNode)
    {
        if (_comboGraph?.RetargetProvenance == null || redirectNode.TreeIndex < 0) return;
        var prov = _comboGraph.RetargetProvenance;
        foreach (var p in prov.Where(kv => kv.Value == redirectNode.Id).ToList())
        {
            string pk = p.Key;
            if (pk.StartsWith("R:", StringComparison.Ordinal))
            {
                var parts = pk.Split(':');
                if (parts.Length != 4 || !int.TryParse(parts[1], out int st)
                    || !int.TryParse(parts[2], out int ri) || !byte.TryParse(parts[3], out byte key))
                    continue;
                if (!_comboGraph.TreeRowConditionOverrides.TryGetValue(st, out var rows)
                    || !rows.TryGetValue(ri, out var edit))
                {
                    prov.Remove(pk);
                    continue;
                }
                edit.Retarget.Remove(key);
                edit.RetargetNodeIds.Remove(key);
                edit.AddedKeys.Remove(key);
            }
            else if (pk.StartsWith("E:", StringComparison.Ordinal))
            {
                var parts = pk.Split(':');
                if (parts.Length != 3 || !byte.TryParse(parts[2], out byte key)) continue;
                if (!_comboGraph.TreeEdgeConditionOverrides.TryGetValue(parts[1], out var edit))
                {
                    prov.Remove(pk);
                    continue;
                }
                edit.Retarget.Remove(key);
                edit.RetargetNodeIds.Remove(key);
                edit.AddedKeys.Remove(key);
            }
            prov.Remove(pk);
        }
    }

    private void ExitRetargetMode()
    {
        _isRetargetMode = false;
        _retargetSourceNode = null;
    }

    private void ConnectTo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is ComboNode source)
            EnterLinkMode(source, createsRedirect: false);
    }

    private void RedirectVia_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is ComboNode source)
            EnterLinkMode(source, createsRedirect: true);
    }

    private void EnterLinkMode(ComboNode source, bool createsRedirect)
    {
        ExitRetargetMode();
        _linkSourceNode = source;
        _linkCreatesRedirect = createsRedirect;
        _linkPickCommitted = false;
        txtStatus.Text = createsRedirect
            ? "Redirect mode: click the node the redirect should forward to, Escape to cancel"
            : "Connect mode: click the node to link to, Escape to cancel";
    }

    private void ExitLinkMode()
    {
        _linkSourceNode = null;
        _linkCreatesRedirect = false;
        _linkPickCommitted = false;
    }

    private void HandleLinkTargetClick(ComboNode target, MouseButtonEventArgs e)
    {
        var source = _linkSourceNode;
        if (source == null) return;
        if (_comboGraph == null || _comboGraph.Nodes.All(n => n.Id != source.Id))
        {
            ExitLinkMode();
            txtStatus.Text = "Link cancelled (moveset changed)";
            return;
        }

        if (target.Id == source.Id || target.IsRoot)
        {
            txtStatus.Text = "Pick a different node as the target (Escape cancels)";
            return;
        }
        if (target.Removed)
        {
            txtStatus.Text = $"'{target.DisplayName}' is marked removed - pick a live node (Escape cancels)";
            return;
        }
        if (_linkCreatesRedirect && target.IsRedirect)
        {
            txtStatus.Text = "Redirects cannot chain - pick an attack node (Escape cancels)";
            return;
        }

        _nodeBorders.TryGetValue(target.Id, out var targetBorder);
        ShowLinkInputPicker(target, targetBorder);
    }

    private IEnumerable<(string label, string value)> LinkInputChoices()
    {
        if (!IsCurrentGraphMainChar())
        {
            // Enemy combo transitions are AI-gated: every m_eInputTransition in both
            // vanilla and modded enemy trees is None (verified 3231/3231 across the
            // WW Kuroki tree vs vanilla), so a player input label would be a lie.
            yield return ("Default (None)", "");
            yield break;
        }
        yield return ("Default (no input)", "");
        yield return ("LMB (Light)", "LMB");
        yield return ("RMB (Heavy)", "RMB");
        yield return ("RMB Hold (HeavyHold)", "RMB Hold");
        yield return ("S (Special)", "S");
        yield return ("Shift (Dodge)", "Shift");
        yield return ("Q (Throw)", "Q");
    }

    /// <summary>True when the graph being edited is the player's moveset (full input list).</summary>
    private bool IsCurrentGraphMainChar()
    {
        if (_activeStance != null
            && string.Equals(_activeStance.Split('|')[0], "MainChar", StringComparison.OrdinalIgnoreCase))
            return true;
        if (_comboGraph != null)
        {
            var w = _comboGraph.WeaponName ?? "";
            if (string.Equals(w, "MainChar", StringComparison.OrdinalIgnoreCase)
                || string.Equals(w, "BareHands", StringComparison.OrdinalIgnoreCase)
                || w.StartsWith("MainChar_", StringComparison.OrdinalIgnoreCase))
                return true;
            if (GraphIsMainChar(_comboGraph))
                return true;
        }
        return false;
    }

    /// <summary>Graph-level MainChar identity: the combo tree's own source path (stable),
    /// never the cards sitting on the canvas - a MainChar card dropped onto an enemy
    /// graph must not flip the graph's identity (it used to, via a node DefaultDBPath
    /// sniff, which switched the link picker to player inputs and skipped the unit
    /// cache). MainChar graphs of every flavor (barehands + weapon variants) carry a
    /// DB/_MainChar SourceGamePath; enemy graphs carry DB/AI/Archetypes/....</summary>
    private static bool GraphIsMainChar(ComboGraph? graph)
        => graph?.SourceGamePath != null
           && graph.SourceGamePath.Contains("DB/_MainChar", StringComparison.OrdinalIgnoreCase);

    private void ShowLinkInputPicker(ComboNode target, Border? targetBorder)
    {
        _linkPickCommitted = false;
        _dragCandidate = false;
        _dragNodeId = -1;
        var menu = new ContextMenu
        {
            PlacementTarget = targetBorder,
            Placement = PlacementMode.MousePoint
        };
        var header = new MenuItem
        {
            Header = _linkCreatesRedirect
                ? $"Redirect via new node → {target.DisplayName}"
                : $"Connect → {target.DisplayName}",
            IsEnabled = false
        };
        menu.Items.Add(header);
        menu.Items.Add(new Separator());
        if (!IsCurrentGraphMainChar())
        {
            menu.Items.Add(new MenuItem
            {
                Header = "Enemy links are AI-gated (input = None)",
                IsEnabled = false
            });
            menu.Items.Add(new Separator());
        }
        foreach (var (label, value) in LinkInputChoices())
        {
            var item = new MenuItem { Header = label, Tag = value };
            item.Click += (s, ev) =>
            {
                _linkPickCommitted = true;
                menu.Closed -= OnPickerClosed;
                CommitLink(target, (string)((MenuItem)s!).Tag!);
            };
            menu.Items.Add(item);
        }

        void OnPickerClosed(object? s, RoutedEventArgs ev)
        {
            menu.Closed -= OnPickerClosed;
            if (!_linkPickCommitted)
            {
                ExitLinkMode();
                txtStatus.Text = "Link cancelled";
            }
        }

        menu.Closed += OnPickerClosed;
        menu.IsOpen = true;
    }

    private void CommitLink(ComboNode target, string inputName)
    {
        var source = _linkSourceNode;
        bool createRedirect = _linkCreatesRedirect;
        ExitLinkMode();
        if (source == null || _comboGraph == null) return;

        int nextId = (_comboGraph.Nodes.Count > 0 ? _comboGraph.Nodes.Max(n => n.Id) : -1) + 1;

        if (createRedirect)
        {
            PushUndo("Add redirect node");
            var redirectNode = new ComboNode
            {
                Id = nextId,
                TreeIndex = -1,
                Kind = ComboNodeKind.UserCreated,
                IsRedirect = true,
                Name = "",
                DisplayName = $"→ {target.DisplayName}",
                AnimPath = "",
                DefaultAnimPath = "",
                DefaultDBPath = "",
                SourceDBPath = "",
                VanillaAnimPath = "",
                IsRoot = false,
                RedirectTargetTreeIndex = -1,
                ResolvedRedirectNodeId = target.Id
            };
            _comboGraph.Nodes.Add(redirectNode);
            _comboGraph.RedirectOriginalTargets[nextId] = target.Id;
            _comboGraph.Edges.Add(new ComboEdge
            {
                FromNodeId = source.Id,
                ToNodeId = nextId,
                InputName = inputName,
                IsNewLink = true
            });
            if (_nodePositions.TryGetValue(source.Id, out var sp))
                _nodePositions[nextId] = new Point(sp.X + NODE_WIDTH + 90, sp.Y + 40);
            else
                _nodePositions[nextId] = new Point(0, 0);
            RenderComboGraph();
            txtStatus.Text = $"Added redirect → '{target.DisplayName}', solid link from '{source.DisplayName}'";
        }
        else
        {
            if (_comboGraph.Edges.Any(e2 => e2.FromNodeId == source.Id && e2.ToNodeId == target.Id && !e2.IsRedirect))
            {
                txtStatus.Text = "Those nodes are already linked";
                return;
            }
            PushUndo("Add link");
            _comboGraph.Edges.Add(new ComboEdge
            {
                FromNodeId = source.Id,
                ToNodeId = target.Id,
                InputName = inputName,
                IsNewLink = true
            });
            RenderComboGraph();
            txtStatus.Text = string.IsNullOrEmpty(inputName)
                ? $"Linked '{source.DisplayName}' → '{target.DisplayName}'"
                : $"Linked '{source.DisplayName}' → '{target.DisplayName}' [{inputName}]";
        }
    }

    private void DeleteNodeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is ComboNode node)
            DeleteAuthoredNode(node);
    }

    private void RestoreNodeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is ComboNode node && _comboGraph != null)
        {
            PushUndo("Restore node");
            node.Removed = false;
            RenderComboGraph();
            txtStatus.Text = $"Restored node: {node.DisplayName}";
        }
    }

    private void DeleteAuthoredNode(ComboNode node)
    {
        if (_comboGraph == null || node.IsRoot || node.Removed) return;

        // Start entries (AI combo entry points) target nodes by id - deleting the target
        // would leave the entry pointing at a ghost. Offer to clean the entries first.
        var startBlockers = (_comboGraph.StartEntries ?? new List<StartEntry>())
            .Where(en => !en.Removed
                && en.Transitions.Any(t => !t.Removed && StartTransitionTargetsNode(t, node.Id)))
            .ToList();
        if (startBlockers.Count > 0)
        {
            var blockDlg = new StartEntryBlockDialog(node.DisplayName, startBlockers) { Owner = this };
            blockDlg.ShowDialog();
            if (blockDlg.Choice != StartEntryBlockChoice.DeleteAnyway)
            {
                if (blockDlg.Choice == StartEntryBlockChoice.OpenStartEntries)
                {
                    txtStatus.Text = $"Delete cancelled: {node.DisplayName} - opened Start Entries "
                        + $"({startBlockers.Count} entr{(startBlockers.Count == 1 ? "y" : "ies")} target it)";
                    ShowStartEntriesDialog(null);
                }
                else
                {
                    txtStatus.Text = $"Delete cancelled: {node.DisplayName} (targeted by "
                        + $"{startBlockers.Count} start entr{(startBlockers.Count == 1 ? "y" : "ies")})";
                }
                return;
            }
        }

        // Event nodes fire gameplay logic (combo role changes, triggers). Removing one that
        // is still routed into silently drops those transitions at export - warn first.
        if (node.IsEventNode)
        {
            int incomingCount = _comboGraph.Edges.Count(ed => ed.ToNodeId == node.Id);
            if (incomingCount > 0)
            {
                var answer = MessageBox.Show(
                    $"Event node '{node.DisplayName}' still has {incomingCount} incoming link(s).\n\n"
                    + "Removing it disables its gameplay event and, on export, drops every "
                    + "transition row left without a target (their probabilities are renormalized).\n\n"
                    + "Delete anyway?",
                    "Delete event node",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    txtStatus.Text = $"Delete cancelled: {node.DisplayName} ({incomingCount} incoming link(s))";
                    return;
                }
            }
        }

        PushUndo("Delete node");

        // Delete Anyway: clear the offending start targets with the Start Entries dialog's
        // semantics - vanilla rows/entries marked Removed (export drops them), new ones
        // hard-removed - so the delete never leaves a ghost target behind. Multi-key rows
        // only lose the keys that target this node; a row that loses its last key (or a
        // single-key row) is removed whole.
        foreach (var en in startBlockers)
        {
            foreach (var t in en.Transitions
                .Where(t => !t.Removed && StartTransitionTargetsNode(t, node.Id)).ToList())
            {
                bool canTrim = t.KeyCount > 1
                    && t.Keys.Count == t.KeyCount
                    && t.KeyTargetNodeIds.Count == t.KeyCount
                    && t.KeyTargetTreeIndices.Count == t.KeyCount
                    && t.KeyTargetNames.Count == t.KeyCount;
                if (canTrim)
                {
                    // Drop only the keys routed to the deleted node (parallel lists
                    // stay in lockstep; export rebuilds m_TargetNodes from the model).
                    for (int ki = t.KeyTargetNodeIds.Count - 1; ki >= 0; ki--)
                    {
                        if (t.KeyTargetNodeIds[ki] != node.Id) continue;
                        t.Keys.RemoveAt(ki);
                        t.KeyTargetTreeIndices.RemoveAt(ki);
                        t.KeyTargetNodeIds.RemoveAt(ki);
                        t.KeyTargetNames.RemoveAt(ki);
                    }
                    if (t.Keys.Count == 0)
                    {
                        if (t.OriginalIndex >= 0) t.Removed = true;
                        else en.Transitions.Remove(t);
                        continue;
                    }
                    if (t.Keys.Count != t.KeyCount)
                    {
                        t.KeyCount = t.Keys.Count;
                        t.TargetSummary = string.Join(", ",
                            t.Keys.Zip(t.KeyTargetTreeIndices, (k2, v2) => $"{k2}→{v2}"));
                    }
                    // Keep the row's single-target fallback pointed at a live key.
                    if (t.TargetNodeId == node.Id)
                    {
                        t.TargetNodeId = t.KeyTargetNodeIds[0];
                        t.TargetTreeIndex = t.KeyTargetTreeIndices[0];
                        t.TargetName = t.KeyTargetNames[0];
                    }
                }
                else
                {
                    if (t.OriginalIndex >= 0) t.Removed = true;
                    else en.Transitions.Remove(t);
                }
            }
            if (en.Transitions.Count(t => !t.Removed) == 0)
            {
                if (en.VanillaIndex >= 0) en.Removed = true;
                else _comboGraph.StartEntries.Remove(en);
            }
        }

        // Authored redirects pointing at the deleted node would render as orphans (hidden),
        // so remove them with their incoming links; vanilla redirects keep existing behavior.
        var brokenRedirects = _comboGraph.Nodes
            .Where(n => n.IsRedirect && n.Kind == ComboNodeKind.UserCreated && n.ResolvedRedirectNodeId == node.Id)
            .ToList();
        foreach (var br in brokenRedirects)
        {
            foreach (var ed in _comboGraph.Edges
                .Where(ed => ed.FromNodeId == br.Id || ed.ToNodeId == br.Id).ToList())
                ClearEdgeMerge(ed);
            _comboGraph.Edges.RemoveAll(ed => ed.FromNodeId == br.Id || ed.ToNodeId == br.Id);
            _comboGraph.Nodes.Remove(br);
            _nodePositions.Remove(br.Id);
            _nodeBorders.Remove(br.Id);
            _comboGraph.RedirectOriginalTargets.Remove(br.Id);
        }

        foreach (var n in _comboGraph.Nodes)
        {
            if (n.IsRedirect && n.ResolvedRedirectNodeId == node.Id)
                n.ResolvedRedirectNodeId = -1;
        }

        if (node.TreeIndex >= 0)
        {
            node.Removed = true;
            ClearNodeSelection();
            RenderComboGraph();
            txtStatus.Text = $"Marked removed: {node.DisplayName} (export drops it from the tree; "
                + "Restore Node brings it back)"
                + (brokenRedirects.Count > 0 ? $" (+{brokenRedirects.Count} linked redirect)" : "");
            return;
        }

        foreach (var ed in _comboGraph.Edges
            .Where(ed => ed.FromNodeId == node.Id || ed.ToNodeId == node.Id).ToList())
            ClearEdgeMerge(ed);
        _comboGraph.Edges.RemoveAll(ed => ed.FromNodeId == node.Id || ed.ToNodeId == node.Id);
        _comboGraph.Nodes.Remove(node);
        _nodePositions.Remove(node.Id);
        _nodeBorders.Remove(node.Id);
        _comboGraph.RedirectOriginalTargets.Remove(node.Id);
        PurgeConditionOverridesForNode(node.Id);
        ClearNodeSelection();
        RenderComboGraph();
        txtStatus.Text = $"Deleted node: {node.DisplayName}"
            + (brokenRedirects.Count > 0 ? $" (+{brokenRedirects.Count} linked redirect)" : "")
            + (startBlockers.Count > 0 ? $" (+{startBlockers.Count} start entr{(startBlockers.Count == 1 ? "y" : "ies")} cleaned)" : "");
    }

    /// <summary>Drops a link's merge: the AddedKeys entry the link owns leaves the row
    /// edit and the edge's MergedInto* fields reset - the link survives as its own
    /// appended chance row. Call before removing a merged edge so the row edit never
    /// keeps a key pointing at a dead link.</summary>
    private void ClearEdgeMerge(ComboEdge edge)
    {
        if (_comboGraph == null || edge.MergedIntoStruct < 0) return;
        if (_comboGraph.TreeRowConditionOverrides.TryGetValue(edge.MergedIntoStruct, out var rows)
            && rows.TryGetValue(edge.MergedIntoRow, out var edit))
            edit.AddedKeys.Remove((byte)edge.MergedIntoKey);
        edge.MergedIntoStruct = -1;
        edge.MergedIntoRow = -1;
        edge.MergedIntoKey = -1;
    }

    /// <summary>True when a start transition (or one of its condition keys) targets
    /// the given graph node id.</summary>
    private static bool StartTransitionTargetsNode(StartTransition t, int nodeId)
        => t.TargetNodeId == nodeId
           || (t.KeyTargetNodeIds?.Count > 0 && t.KeyTargetNodeIds.Contains(nodeId));

    /// <summary>Drops condition-override entries that still reference a deleted node id,
    /// so a later export never resolves a ghost id. Edge overrides keyed
    /// "FromId>ToId|Input" vanish with the node's edges; row overrides keep their
    /// other outcome keys and only lose the ones routed to the deleted node.</summary>
    private void PurgeConditionOverridesForNode(int nodeId)
    {
        if (_comboGraph == null) return;

        foreach (var key in _comboGraph.TreeEdgeConditionOverrides.Keys.ToList())
        {
            var ends = key.Split('|')[0].Split('>');
            if (ends.Length == 2
                && ((int.TryParse(ends[0], out int from) && from == nodeId)
                 || (int.TryParse(ends[1], out int to) && to == nodeId)))
                _comboGraph.TreeEdgeConditionOverrides.Remove(key);
        }

        void CleanEdit(TreeRowConditionEdit edit)
        {
            foreach (var k in edit.RetargetNodeIds
                .Where(kv => kv.Value == nodeId).Select(kv => kv.Key).ToList())
                edit.RetargetNodeIds.Remove(k);
            foreach (var k in edit.AddedKeys
                .Where(kv => kv.Value == nodeId).Select(kv => kv.Key).ToList())
                edit.AddedKeys.Remove(k);
        }

        foreach (var edit in _comboGraph.TreeEdgeConditionOverrides.Values)
            CleanEdit(edit);
        foreach (var rowMap in _comboGraph.TreeRowConditionOverrides.Values)
            foreach (var edit in rowMap.Values)
                CleanEdit(edit);
    }

    private void DeleteEdgeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is ComboEdge edge && _comboGraph != null)
        {
            PushUndo("Delete link");
            ClearEdgeMerge(edge);
            _comboGraph.Edges.Remove(edge);
            RenderComboGraph();
            txtStatus.Text = "Link removed";
        }
    }

    private void Edge_ResetMode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isResetMode || _comboGraph == null) return;
        if (sender is not System.Windows.Shapes.Path path || path.Tag is not ComboEdge edge) return;
        if (edge.IsNewLink)
        {
            PushUndo("Delete link");
            ClearEdgeMerge(edge);
            _comboGraph.Edges.Remove(edge);
            RenderComboGraph();
            txtStatus.Text = "Removed new link (reset to vanilla)";
        }
        else
        {
            txtStatus.Text = "Link is already vanilla";
        }
        e.Handled = true;
    }


    private void RetargetNode_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border border || border.Tag is not ComboNode node) return;
        if (_linkSourceNode != null)
        {
            bool valid = node.Id != _linkSourceNode.Id && !node.IsRoot &&
                         (!_linkCreatesRedirect || !node.IsRedirect);
            if (!valid) return;
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1));
            border.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xa6, 0xe3, 0xa1));
            border.Opacity = 1;
            return;
        }
        if (_isRetargetMode)
        {
            if (node.Id == _retargetSourceNode?.Id || node.IsRedirect) return;
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1));
            border.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xa6, 0xe3, 0xa1));
            border.Opacity = 1;
        }
        else if (_isNodePickMode)
        {
            if (node.IsRoot || node.IsRedirect || node.Removed) return;
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1));
            border.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xa6, 0xe3, 0xa1));
            border.Opacity = 1;
        }
    }

    private void RetargetNode_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border && border.Tag is ComboNode node)
            RestoreNodeVisual(border, node);
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
        bool isMainChar = IsMainCharStance();
        btnUnitProperties.Visibility = Visibility.Visible;
        // AI start tickets only exist on enemy archetype combo trees.
        btnStartEntries.Visibility = isMainChar ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>True when the currently loaded combo graph belongs to the player character.</summary>
    private bool IsMainCharStance()
    {
        var arch = _activeStance?.Split('|')[0] ?? "";
        return string.Equals(arch, "MainChar", StringComparison.OrdinalIgnoreCase);
    }

    private async void BtnStartEntries_Click(object sender, RoutedEventArgs e)
    {
        if (_comboGraph == null)
        {
            txtStatus.Text = "No combo graph loaded";
            return;
        }
        if (IsCurrentGraphMainChar())
        {
            txtStatus.Text = "Main character trees don't use AI start entries";
            return;
        }
        if (_customMoveset == null &&
            (_comboGraph.StartEntries == null || _comboGraph.StartEntries.Count == 0))
        {
            txtStatus.Text = "Start entries not in save - recovering from vanilla tree...";
            btnStartEntries.IsEnabled = false;
            bool recovered = await BackfillStartEntriesAsync();
            btnStartEntries.IsEnabled = true;
            if (!recovered || _comboGraph.StartEntries == null || _comboGraph.StartEntries.Count == 0)
            {
                txtStatus.Text = "This combo tree has no start entries (m_ComboStartNodesContainer)";
                return;
            }
            txtStatus.Text = $"Recovered {_comboGraph.StartEntries.Count} start entries from vanilla tree";
        }

        ShowStartEntriesDialog(null);
    }

    /// <summary>Shows the Start Entries dialog (fresh, or resumed after a pick-on-graph
    /// suspend with the carried entries copy). OK commits; a pick-suspend reopens pick
    /// mode on the canvas instead; Cancel just closes.</summary>
    private void ShowStartEntriesDialog(List<StartEntry>? resume)
    {
        if (_comboGraph == null) return;
        var dlg = new StartEntriesDialog(_comboGraph, _showAiNodes, SelectAndRevealNode, resume) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            PushUndo("Edit start entries");
            _comboGraph.StartEntries = dlg.Result;
            txtStatus.Text = $"Start entries updated ({dlg.Result.Count} tickets)";
            return;
        }
        if (dlg.SuspendedEntries == null || dlg.PickTransition == null) return; // plain Cancel
        var suspendedEntries = dlg.SuspendedEntries;
        var suspendedT = dlg.PickTransition;
        int suspendedKi = dlg.PickKeyIndex;
        BeginNodePick(
            node => StartEntriesDialog.ApplyPickedTarget(suspendedT, suspendedKi, node),
            () => ShowStartEntriesDialog(suspendedEntries));
    }

    /// <summary>Enters pick mode for a suspended dialog edit (Start Entries, Conditions
    /// &amp; Chances, ...): the user clicks a node on the canvas, confirms, and apply
    /// routes the pick back into the carried working state; Escape (or the
    /// confirmation's backdrop/"Pick another") resumes the dialog with every edit
    /// intact via resume.</summary>
    private void BeginNodePick(Action<ComboNode> apply, Action resume)
    {
        if (_comboGraph == null) return;
        ExitRetargetMode();
        ExitLinkMode();
        _isNodePickMode = true;
        _pendingPickApply = apply;
        _pendingPickResume = resume;
        _pendingPickGraph = _comboGraph;
        _pickCandidateNodeId = -1;
        txtStatus.Text = "Pick target: click a node on the graph, Escape to cancel";
        ShowToast("Click a node to set the target (Esc cancels)");
    }

    /// <summary>Clears pick mode and hides the confirmation popup. Callers keep their
    /// own apply/resume closures when they intend to resume the suspended dialog.</summary>
    private void ExitNodePickMode()
    {
        _isNodePickMode = false;
        _pendingPickApply = null;
        _pendingPickResume = null;
        _pendingPickGraph = null;
        _pickCandidateNodeId = -1;
        pickConfirmBorder.Visibility = Visibility.Collapsed;
    }

    private void HandleNodePickClick(ComboNode node)
    {
        if (_pendingPickGraph == null || !ReferenceEquals(_comboGraph, _pendingPickGraph))
        {
            ExitNodePickMode();
            txtStatus.Text = "Graph changed - dialog edits discarded";
            ShowToast("Graph changed - dialog edits discarded");
            return;
        }
        if (node.IsRoot || node.IsRedirect || node.Removed)
        {
            ShowToast("That node can't be a target - click an attack, action or event node");
            return;
        }
        _pickCandidateNodeId = node.Id;
        string idx = node.TreeIndex >= 0 ? $"[{node.TreeIndex}]" : "[new]";
        string name = !string.IsNullOrEmpty(node.DisplayName) ? node.DisplayName
            : !string.IsNullOrEmpty(node.Name) ? node.Name : $"#{node.Id}";
        string type = node.IsActionNode ? "AI action" : node.IsEventNode ? "AI event" : "Attack / animation";
        pickConfirmName.Text = $"{idx} {name}";
        pickConfirmDetail.Text = $"Type: {type} - will become the target of this outcome.";
        pickConfirmBorder.Visibility = Visibility.Visible;
    }

    private void PickConfirmBackdrop_Click(object sender, MouseButtonEventArgs e)
        => pickConfirmBorder.Visibility = Visibility.Collapsed; // stay in pick mode

    private void PickConfirmCancel_Click(object sender, RoutedEventArgs e)
        => pickConfirmBorder.Visibility = Visibility.Collapsed; // stay in pick mode

    private void PickConfirmUse_Click(object sender, RoutedEventArgs e)
    {
        var apply = _pendingPickApply;
        var resume = _pendingPickResume;
        int candidateId = _pickCandidateNodeId;
        var graphAtPick = _pendingPickGraph;
        ExitNodePickMode();
        if (apply == null || resume == null) return;
        if (_comboGraph == null || graphAtPick == null || !ReferenceEquals(_comboGraph, graphAtPick))
        {
            txtStatus.Text = "Graph changed - dialog edits discarded";
            ShowToast("Graph changed - dialog edits discarded");
            return;
        }
        var node = _comboGraph.Nodes.FirstOrDefault(n => n.Id == candidateId);
        if (node == null || node.IsRoot || node.IsRedirect || node.Removed)
        {
            ShowToast("Pick failed - node no longer available");
            resume();
            return;
        }
        apply(node);
        txtStatus.Text = $"Target set by pick: {node.DisplayName}";
        resume();
    }

    /// <summary>Relevance gate for the Conditions & Chances entry points: the node's struct has
    /// vanilla rows, or the node has new links (whose chances also live in the dialog).
    /// HasConditionData covers node-level conditions (m_ConditionalAttacks /
    /// m_RequiredTags) and rows carrying a condition class.</summary>
    private bool HasBranchChanceData(ComboNode node)
    {
        if (_comboGraph == null) return false;
        if (node.TreeIndex >= 0)
        {
            int structIdx = TreeChanceMath.ResolveWriteStruct(_comboGraph, node);
            if (structIdx >= 0 && _comboGraph.TreeRows.ContainsKey(structIdx)) return true;
        }
        return _comboGraph.Edges.Any(e => e.IsNewLink && e.FromNodeId == node.Id);
    }

    private bool HasConditionData(ComboNode node)
    {
        if (_comboGraph == null) return false;
        if (node.TreeIndex >= 0 && _comboGraph.TreeNodeConditions.ContainsKey(node.TreeIndex))
            return true;
        if (node.TreeIndex >= 0)
        {
            int structIdx = TreeChanceMath.ResolveWriteStruct(_comboGraph, node);
            if (structIdx >= 0 && _comboGraph.TreeRows.TryGetValue(structIdx, out var rows)
                && rows.Any(r => !string.IsNullOrEmpty(r.ConditionClass)))
                return true;
        }
        return false;
    }

    private void BranchChancesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu ctxMenu &&
            ctxMenu.PlacementTarget is Border border && border.Tag is ComboNode node)
        {
            OpenBranchChances(node);
        }
    }

    private void OpenBranchChances(ComboNode node)
    {
        // MainChar (any variant) has no editable conditions/chances - the menu entry is
        // hidden there too; this guard covers any future caller.
        if (_comboGraph == null || IsCurrentGraphMainChar()) return;
        if (CanBackfillBranchRows(node))
            BackfillTreeRows();
        if (!HasBranchChanceData(node) && !HasConditionData(node))
        {
            txtStatus.Text = $"'{node.DisplayName}' has no transition rows yet - add a new link first";
            return;
        }
        ShowBranchChancesDialog(node);
    }

    /// <summary>Shows the Conditions &amp; Chances dialog (fresh, or resumed after a
    /// pick-on-graph suspend with the carried working state). OK commits; a pick-suspend
    /// reopens pick mode on the canvas instead; Cancel just closes.</summary>
    private void ShowBranchChancesDialog(ComboNode node,
        BranchChancesDialog.PickSuspendState? resumeState = null)
    {
        if (_comboGraph == null) return;
        var dlg = new BranchChancesDialog(_comboGraph, node, !IsCurrentGraphMainChar(),
            Setup.ContentDetector.ResolveContentDir(_contentPath), resumeState) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.ResultOverrides != null)
        {
            PushUndo("Edit branch chances");
            _comboGraph.TreeRowProbabilityOverrides = dlg.ResultOverrides;
            int edgeChanged = 0;
            if (dlg.ResultEdgeProbabilities != null)
            {
                foreach (var (edge, prob) in dlg.ResultEdgeProbabilities)
                {
                    if (edge.NewLinkProbability != prob) edgeChanged++;
                    edge.NewLinkProbability = prob;
                }
            }
            // Mixup links the clear/restore sync staged - dropped ones first so a
            // remove+re-add cycle in one session can't duplicate an edge.
            int mixupRemoved = 0;
            if (dlg.ResultRemovedEdges is { Count: > 0 })
            {
                foreach (var edge in dlg.ResultRemovedEdges)
                    if (_comboGraph.Edges.Remove(edge)) mixupRemoved++;
            }
            int mixupAdded = 0;
            if (dlg.ResultNewEdges is { Count: > 0 })
            {
                _comboGraph.Edges.AddRange(dlg.ResultNewEdges);
                mixupAdded = dlg.ResultNewEdges.Count;
            }
            int condParamEdits = 0, condRowEdits = 0;
            if (dlg.ResultConditionParams != null)
            {
                _comboGraph.TreeConditionParamOverrides = dlg.ResultConditionParams;
                condParamEdits = dlg.ResultConditionParams.Values.Sum(m => m.Count);
            }
            if (dlg.ResultRowConditions != null)
            {
                _comboGraph.TreeRowConditionOverrides = dlg.ResultRowConditions;
                condRowEdits = dlg.ResultRowConditions.Values.Sum(m => m.Count);
            }
            int edgeCondEdits = 0;
            if (dlg.ResultEdgeConditions != null)
            {
                _comboGraph.TreeEdgeConditionOverrides = dlg.ResultEdgeConditions;
                edgeCondEdits = dlg.ResultEdgeConditions.Count;
            }
            // Link merges: staged on the edges only at OK (Cancel leaves them alone).
            // The AddedKeys side already landed through ResultRowConditions above.
            int mergedLinks = 0, unmergedLinks = 0;
            if (dlg.ResultMergedEdges is { Count: > 0 })
            {
                foreach (var (edge, sIdx, rIdx, key) in dlg.ResultMergedEdges)
                {
                    edge.MergedIntoStruct = sIdx;
                    edge.MergedIntoRow = rIdx;
                    edge.MergedIntoKey = key;
                    mergedLinks++;
                }
            }
            if (dlg.ResultUnmergedEdges is { Count: > 0 })
            {
                foreach (var edge in dlg.ResultUnmergedEdges)
                {
                    edge.MergedIntoStruct = -1;
                    edge.MergedIntoRow = -1;
                    edge.MergedIntoKey = -1;
                    unmergedLinks++;
                }
            }
            if (mixupAdded > 0 || mixupRemoved > 0 || mergedLinks > 0 || unmergedLinks > 0)
                RenderComboGraph();
            int typedRows = dlg.ResultOverrides.Values.Sum(m => m.Count);
            var parts = new List<string>();
            if (typedRows > 0) parts.Add($"{typedRows} typed chance row(s)");
            if (edgeChanged > 0) parts.Add($"{edgeChanged} new-link chance(s)");
            if (mixupAdded > 0) parts.Add($"{mixupAdded} mixup link(s)");
            if (mixupRemoved > 0) parts.Add($"{mixupRemoved} mixup link(s) dropped");
            if (condParamEdits > 0) parts.Add($"{condParamEdits} condition param(s)");
            if (condRowEdits > 0) parts.Add($"{condRowEdits} condition row(s)");
            if (edgeCondEdits > 0) parts.Add($"{edgeCondEdits} new-link condition(s)");
            if (mergedLinks > 0) parts.Add($"{mergedLinks} merged link(s)");
            if (unmergedLinks > 0) parts.Add($"{unmergedLinks} merge(s) undone");
            txtStatus.Text = parts.Count > 0
                ? "Updated: " + string.Join(", ", parts)
                : "No changes";
            return;
        }
        if (dlg.SuspendedForPick && dlg.PickSuspend != null) // ⌖ suspend → pick on canvas
        {
            var state = dlg.PickSuspend;
            BeginNodePick(
                n => BranchChancesDialog.ApplyPickedTarget(state, n),
                () => ShowBranchChancesDialog(node, state));
        }
    }

    /// <summary>Old saves persist no TreeRows snapshot (empty dictionary), so both the
    /// menu gate and the dialog gate would hide vanilla rows the source combo still has.
    /// True while that one re-parse remains possible for this graph - the menu shows so
    /// the click path can backfill instead of being gated out.</summary>
    private bool CanBackfillBranchRows(ComboNode node)
    {
        if (_comboGraph == null || _comboGraph.TreeRowsBackfillAttempted) return false;
        if (_comboGraph.TreeRows.Count > 0) return false;
        if (string.IsNullOrEmpty(_comboGraph.SourceGamePath) || node.TreeIndex < 0) return false;
        return TreeChanceMath.ResolveWriteStruct(_comboGraph, node) >= 0;
    }

    /// <summary>Old saves predate the TreeRows parse snapshot, so the Conditions &amp;
    /// Chances gate and dialog can't see vanilla transition rows even though the tree
    /// has them. Re-parse the graph's source combo once and merge only missing keys -
    /// rows are a read-only snapshot and user edits live in override maps, so nothing
    /// is clobbered. A struct still absent afterwards is genuinely row-less.</summary>
    private void BackfillTreeRows()
    {
        var graph = _comboGraph;
        if (graph == null) return;
        graph.TreeRowsBackfillAttempted = true;
        if (string.IsNullOrEmpty(graph.SourceGamePath)) return;
        try
        {
            var fresh = _parser.LoadComboTreeFromPath(graph.SourceGamePath, graph.WeaponName);
            if (fresh == null) return;
            foreach (var kv in fresh.TreeRows)
                if (!graph.TreeRows.ContainsKey(kv.Key)) graph.TreeRows[kv.Key] = kv.Value;
            foreach (var kv in fresh.TreeNodeConditions)
                if (!graph.TreeNodeConditions.ContainsKey(kv.Key))
                    graph.TreeNodeConditions[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("BRANCHROWS", ex);
        }
    }

    /// <summary>
    /// Old saves predate StartEntries in the .sifu-edit format, so the restored cache
    /// graph has none even though the tree does. Re-parse the vanilla tree for the
    /// current unit (the import catalog lookup handles variants and weapon combos) and
    /// adopt its container, remapping targets by TreeIndex against the saved graph so
    /// stale parse ids can't mislink.
    /// </summary>
    private async Task<bool> BackfillStartEntriesAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_activeVariant)) return false;
            var entry = BuildImportComboCatalog().Values.FirstOrDefault(e =>
                string.Equals(e.UnitKey, GetUnitCacheKey(), StringComparison.OrdinalIgnoreCase));
            if (entry == null) return false;

            var target = _comboGraph;
            if (target == null) return false;
            var fresh = await Task.Run(() => _parser.LoadComboTreeFromPath(entry.GamePath, entry.WeaponName));
            var recovered = fresh?.StartEntries;
            if (recovered == null || recovered.Count == 0) return false;
            if (!ReferenceEquals(_comboGraph, target)) return false;

            target.RemapStartEntryTargets(recovered);
            target.StartEntries = recovered;
            return target.StartEntries.Count > 0;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("START", ex);
            return false;
        }
    }

    /// <summary>
    /// v1.2-era saves predate the Phase-2 combo graph, so their unit caches hold only
    /// the condensed moveset graph (no action/event nodes, TreeRows, StartEntries or
    /// edge owners) and restoring from cache alone renders a graph missing every
    /// event / BP_AiAction node. For each flagged cache this re-parses the source
    /// combo and overlays the saved edits onto it (see ProjectMigrator), then clears
    /// the backfill flag - the BackfillEdgeOwners pass below then skips migrated units.
    /// Returns the status-note suffix, or null when nothing was migrated.
    /// </summary>
    private async Task<string?> MigrateLegacyCachesAsync()
    {
        if (!_unitCaches.Values.Any(c => c.NeedsEdgeOwnerBackfill)) return null;
        try
        {
            IProgress<string> progress = new Progress<string>(p => UpdateLoading("Upgrading legacy unit graphs...", p));
            var report = await Task.Run(() =>
                ProjectMigrator.MigrateLegacyCaches(_unitCaches, _parser, progress.Report));
            if (report.MigratedCount == 0 && report.FailureCount == 0) return null;
            var note = report.Summary();
            ErrorLog.Write("MIGRATE",
                $"{note} | warnings={report.WarningCount} failures={report.FailureCount}");
            foreach (var u in report.Units.Where(u => u.Warnings.Count > 0))
                ErrorLog.Write("MIGRATE", $"{u.UnitKey}: {string.Join("; ", u.Warnings)}");
            return note;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("MIGRATE", ex);
            return null;
        }
    }

    /// <summary>
    /// Old saves predate edge-owner persistence (A' pass-through), so their cache graphs
    /// are missing both the re-sourced redirect rows and the owner tags export's
    /// owner-writeback needs. Re-parse each flagged unit's vanilla tree and merge only
    /// owner-tagged edges, mapping endpoints by TreeIndex so stale parse ids can't
    /// mislink and user-deleted display edges stay deleted.
    /// </summary>
    private async Task BackfillEdgeOwnersAsync()
    {
        try
        {
            var flagged = _unitCaches.Where(kv => kv.Value.NeedsEdgeOwnerBackfill).ToList();
            if (flagged.Count == 0) return;
            var catalog = BuildImportComboCatalog().Values.ToList();
            foreach (var (key, entry) in flagged)
            {
                var catEntry = catalog.FirstOrDefault(e =>
                    string.Equals(e.UnitKey, key, StringComparison.OrdinalIgnoreCase));
                if (catEntry == null) continue;
                var fresh = await Task.Run(() => _parser.LoadComboTreeFromPath(catEntry.GamePath, catEntry.WeaponName));
                if (fresh != null) MergeVanillaEdgeOwners(entry.Graph, fresh);
                entry.NeedsEdgeOwnerBackfill = false;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EDGEBF", ex);
        }
    }

    private static void MergeVanillaEdgeOwners(ComboGraph target, ComboGraph fresh)
    {
        // Fresh parse ids and saved ids can differ (conduits/extra nodes over versions);
        // map by TreeIndex, matching redirect-ness so boxes never take attack slots.
        var freshToTarget = new Dictionary<int, int>();
        foreach (var fn in fresh.Nodes)
        {
            if (fn.TreeIndex < 0) continue;
            var tn = target.Nodes.FirstOrDefault(n => n.TreeIndex == fn.TreeIndex && n.IsRedirect == fn.IsRedirect);
            if (tn != null) freshToTarget[fn.Id] = tn.Id;
        }

        foreach (var fe in fresh.Edges)
        {
            if (fe.OwnerRedirectNodeId < 0) continue;
            if (!freshToTarget.TryGetValue(fe.FromNodeId, out var fromId)) continue;
            if (!freshToTarget.TryGetValue(fe.ToNodeId, out var toId)) continue;
            if (!freshToTarget.TryGetValue(fe.OwnerRedirectNodeId, out var ownerId)) continue;

            var existing = target.Edges.FirstOrDefault(e => e.FromNodeId == fromId && e.ToNodeId == toId);
            if (existing != null)
            {
                if (existing.OwnerRedirectNodeId < 0 && !existing.IsNewLink)
                    existing.OwnerRedirectNodeId = ownerId;
            }
            else
            {
                target.Edges.Add(new ComboEdge
                {
                    FromNodeId = fromId,
                    ToNodeId = toId,
                    InputName = fe.InputName,
                    OwnerRedirectNodeId = ownerId
                });
            }

            var ownerNode = target.Nodes.FirstOrDefault(n => n.Id == ownerId);
            if (ownerNode != null) ownerNode.OwnedContinuation = true;
        }
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
        RestorePropsForCurrentVariant();

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
            ApplySharedCardTuningToGraph(graph);
            _activeWeapon = NormalizeWeaponForCache(arch, weaponTag);
            UpdateWeaponButtonVisibility();
            SetComboInfo($"{arch} {weaponTag} - {graph.Nodes.Count} nodes");
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

    internal sealed record ImportComboEntry(
        string UnitKey,
        string GamePath,
        string WeaponName,
        string? ActiveVariant,
        string? ActiveWeapon);

    internal static Dictionary<string, ImportComboEntry> BuildImportComboCatalog()
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
        ClearUndoHistory();
        var comboPath = ResolveComboFilePath(variantTag);
        if (comboPath == null) { txtStatus.Text = $"No combo file for {variantTag}"; return; }
        string arch = _activeStance.Split('|')[0];

        if (saveCurrent)
            SaveCurrentUnitToCache();
        _currentUnitProps = null;
        _activeWeapon = null;
        _activeVariant = variantTag;
        _activeStance = arch;
        RestorePropsForCurrentVariant();

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
            ApplySharedCardTuningToGraph(graph);
            if (!string.IsNullOrEmpty(_activeWeapon) && !GetWeaponVariantsForArchetype(arch).Any(w => w.tag == _activeWeapon))
                _activeWeapon = null;
            UpdateWeaponButtonVisibility();
            SetComboInfo($"{arch} {variantTag} - {graph.Nodes.Count} nodes");
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
        ClearUndoHistory();
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

            if (string.Equals(Path.GetFullPath(movementDbSrc), Path.GetFullPath(vanillaDbSrc), StringComparison.OrdinalIgnoreCase))
            {
                // MainChar: the slot is its own source. Restore the pristine backup so any
                // prior truncation (the historical self-copy bug) heals on stance switch.
                if (File.Exists(backupDbSrc))
                {
                    await CopyWithRetryAsync(backupDbSrc, vanillaDbSrc);
                    if (File.Exists(backupDbExp))
                        await CopyWithRetryAsync(backupDbExp, vanillaDbExp);
                }
            }
            else
            {
                await CopyWithRetryAsync(movementDbSrc, vanillaDbSrc);
                if (File.Exists(movementDbExp))
                    await CopyWithRetryAsync(movementDbExp, vanillaDbExp);
            }

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

                if (string.Equals(Path.GetFullPath(transSrc), Path.GetFullPath(vanillaTransSrc), StringComparison.OrdinalIgnoreCase))
                {
                    // Same self-heal as the movement DB above: MainChar's transition slot is
                    // its own source, so restore the pristine backup instead of self-copying.
                    if (File.Exists(backupTransSrc))
                    {
                        await CopyWithRetryAsync(backupTransSrc, vanillaTransSrc);
                        if (File.Exists(backupTransExp))
                            await CopyWithRetryAsync(backupTransExp, vanillaTransExp);
                    }
                }
                else if (File.Exists(transSrc))
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
            RestorePropsForCurrentVariant();

            string cacheKey = GetUnitCacheKey();
            if (!TryRestoreUnitFromCache(cacheKey))
            {
                var freshParser = new AnimationParser();
                freshParser.Initialize(_contentPath, contentDir);
                freshParser.MountCustomIntoProvider(CustomAssets.Root);
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
                    ApplySharedCardTuningToGraph(_comboGraph);

                    SetComboInfo($"{stance} - {_comboGraph.WeaponName} ({_comboGraph.Nodes.Count} nodes, {_comboGraph.Edges.Count} edges)");
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
                SetComboInfo($"{stance} - {_comboGraph.WeaponName} ({_comboGraph.Nodes.Count} nodes, {_comboGraph.Edges.Count} edges)");
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
            try { var fresh = new AnimationParser(); var cDir = Setup.ContentDetector.ResolveContentDir(_contentPath); fresh.Initialize(_contentPath, cDir); fresh.MountCustomIntoProvider(CustomAssets.Root); _parser = fresh; } catch { }
            graphLoadingBorder.Visibility = Visibility.Collapsed;
            comboCanvas.IsHitTestVisible = true;
            btnChangeUnit.IsEnabled = true;
        }
    }

    private async Task CopyWithRetryAsync(string src, string dst)
    {
        // MainChar's stance "source" is the destination slot itself (_stanceMap points
        // both at DB/Movement/...): FileMode.Create would truncate the very file we are
        // about to read, silently zeroing it - a self-copy is a no-op.
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
            return;

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
        RestorePropsForCurrentVariant();

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
        string oldCard = AttackDbCard.EffectiveCardPath(node);

        if (IsAttackDbPath(move.FullPath ?? ""))
        {
            node.SourceDBPath = move.FullPath;
        }
        else if (_parser.AnimToDbPath.TryGetValue(move.FullPath, out var srcDb))
        {
            node.SourceDBPath = srcDb;
        }

        // The node now points at a different attack card: its stored buildup/range belonged to
        // the old card, so re-baseline - inherit the values another unit already tuned on this
        // card (same file in game), otherwise fall back to the new card's vanilla values.
        string newCard = AttackDbCard.EffectiveCardPath(node);
        if (string.Equals(oldCard, newCard, StringComparison.OrdinalIgnoreCase)) return;

        var inherited = FindSharedCardTuning(newCard, node);
        node.AttackBuildupFrames = inherited?.Buildup;
        node.AttackGameplayRange = inherited?.Range;
        node.HitEdits = inherited?.HitEdits;
    }

    /// <summary>Every node the editor can tune: the live graph plus all cached unit graphs.</summary>
    private IEnumerable<ComboNode> EnumerateAllGraphNodes()
    {
        if (_comboGraph != null)
        {
            foreach (var n in _comboGraph.Nodes) yield return n;
        }
        foreach (var kvp in _unitCaches)
        {
            var g = kvp.Value?.Graph;
            if (g == null || (_comboGraph != null && ReferenceEquals(g, _comboGraph))) continue;
            foreach (var n in g.Nodes) yield return n;
        }
    }

    /// <summary>
    /// The tuning another node already holds for this card - the "changes list" lookup used when
    /// the same AttackDB file is dropped onto a second unit. Null when nobody tuned it yet.
    /// Hit window edits ride along since they are a property of the same shared card file.
    /// </summary>
    private (int? Buildup, float? Range, List<AttackHitEdit>? HitEdits)? FindSharedCardTuning(string cardPath, ComboNode exclude)
    {
        if (string.IsNullOrEmpty(cardPath)) return null;
        foreach (var n in EnumerateAllGraphNodes())
        {
            if (ReferenceEquals(n, exclude) || n.IsRedirect) continue;
            if (!string.Equals(AttackDbCard.EffectiveCardPath(n), cardPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (n.AttackBuildupFrames.HasValue || n.AttackGameplayRange.HasValue || n.HitEdits != null)
                return (n.AttackBuildupFrames, n.AttackGameplayRange, AttackHitEdit.CloneList(n.HitEdits));
        }
        return null;
    }

    /// <summary>
    /// One card file holds one set of values in game, so a commit writes them onto every node
    /// (any unit) that points at the same card.
    /// </summary>
    private void PropagateAttackDbTuning(ComboNode source)
    {
        string card = AttackDbCard.EffectiveCardPath(source);
        if (string.IsNullOrEmpty(card)) return;
        foreach (var n in EnumerateAllGraphNodes())
        {
            if (ReferenceEquals(n, source) || n.IsRedirect) continue;
            if (!string.Equals(AttackDbCard.EffectiveCardPath(n), card, StringComparison.OrdinalIgnoreCase)) continue;
            n.AttackBuildupFrames = source.AttackBuildupFrames;
            n.AttackGameplayRange = source.AttackGameplayRange;
            n.HitEdits = AttackHitEdit.CloneList(source.HitEdits);
        }
    }

    /// <summary>
    /// Fresh vanilla parses carry no AttackDB tuning, so a graph installed from disk would
    /// silently diverge from units already tuned on the same card (and a later commit from
    /// it would wipe them). Fill untuned same-card nodes from every other in-memory graph.
    /// </summary>
    private void ApplySharedCardTuningToGraph(ComboGraph? graph)
    {
        if (graph == null || _unitCaches == null) return;
        var byCard = new Dictionary<string, (int? Buildup, float? Range, List<AttackHitEdit>? HitEdits)>(
            StringComparer.OrdinalIgnoreCase);
        void Consider(ComboGraph? g)
        {
            if (g == null || ReferenceEquals(g, graph)) return;
            foreach (var n in g.Nodes)
            {
                if (n.IsRedirect) continue;
                if (!n.AttackBuildupFrames.HasValue && !n.AttackGameplayRange.HasValue && n.HitEdits == null) continue;
                string card = AttackDbCard.EffectiveCardPath(n);
                if (string.IsNullOrEmpty(card)) continue;
                if (!byCard.ContainsKey(card))
                    byCard[card] = (n.AttackBuildupFrames, n.AttackGameplayRange, AttackHitEdit.CloneList(n.HitEdits));
            }
        }
        Consider(_comboGraph);
        foreach (var kvp in _unitCaches) Consider(kvp.Value.Graph);
        if (byCard.Count == 0) return;

        foreach (var n in graph.Nodes)
        {
            if (n.IsRedirect) continue;
            if (n.AttackBuildupFrames.HasValue || n.AttackGameplayRange.HasValue || n.HitEdits != null) continue;
            string card = AttackDbCard.EffectiveCardPath(n);
            if (string.IsNullOrEmpty(card) || !byCard.TryGetValue(card, out var v)) continue;
            n.AttackBuildupFrames = v.Buildup;
            n.AttackGameplayRange = v.Range;
            n.HitEdits = AttackHitEdit.CloneList(v.HitEdits);
        }
    }

    /// <summary>
    /// Projects saved before shared-card tracking could hold two different values for one card;
    /// unify them (first unit-key wins) so export never hits its keep-first conflict path.
    /// Also fills same-card nodes carrying no tuning from the first tuned sibling, so old
    /// saves whose units were never visited after an edit self-heal on load.
    /// </summary>
    private int NormalizeSharedCardTuning()
    {
        var firstByCard = new Dictionary<string, ComboNode>(StringComparer.OrdinalIgnoreCase);
        int unified = 0;
        foreach (var n in EnumerateAllGraphNodes())
        {
            if (n.IsRedirect || (!n.AttackBuildupFrames.HasValue && !n.AttackGameplayRange.HasValue
                && n.HitEdits == null)) continue;
            string card = AttackDbCard.EffectiveCardPath(n);
            if (string.IsNullOrEmpty(card)) continue;
            if (firstByCard.TryGetValue(card, out var first))
            {
                if (first.AttackBuildupFrames != n.AttackBuildupFrames
                    || first.AttackGameplayRange != n.AttackGameplayRange
                    || !EditsEqual(first.HitEdits ?? new List<AttackHitEdit>(),
                                   n.HitEdits ?? new List<AttackHitEdit>()))
                {
                    n.AttackBuildupFrames = first.AttackBuildupFrames;
                    n.AttackGameplayRange = first.AttackGameplayRange;
                    n.HitEdits = AttackHitEdit.CloneList(first.HitEdits);
                    unified++;
                }
            }
            else
            {
                firstByCard[card] = n;
            }
        }
        // Fill pass: one card = one value set in game, so an untuned node on a tuned card
        // is a sync gap (unit never visited after the edit), not an intentional vanilla.
        foreach (var n in EnumerateAllGraphNodes())
        {
            if (n.IsRedirect) continue;
            if (n.AttackBuildupFrames.HasValue || n.AttackGameplayRange.HasValue || n.HitEdits != null) continue;
            string card = AttackDbCard.EffectiveCardPath(n);
            if (string.IsNullOrEmpty(card) || !firstByCard.TryGetValue(card, out var first)) continue;
            n.AttackBuildupFrames = first.AttackBuildupFrames;
            n.AttackGameplayRange = first.AttackGameplayRange;
            n.HitEdits = AttackHitEdit.CloneList(first.HitEdits);
            unified++;
        }
        return unified;
    }

    /// <summary>
    /// Overrides whose value renders identically to their card's vanilla baseline are not
    /// changes - null them. The Attack DB textbox shows m_fGameplayRange at "0.###" while
    /// cards carry 4+ decimals, so older saves hold round-trip phantoms (typed back the
    /// value the panel displayed, failed the raw 0.0001 equality check, got stored).
    /// Baseline: content dir first, then the persistent CustomAssets copy (mirrors
    /// CommitAttackDbPanel); an unreadable baseline keeps the override untouched.
    /// </summary>
    private int NormalizeVanillaEqualTuning()
    {
        int cleared = 0;
        string contentDir = AttackDbContentDir();
        foreach (var n in EnumerateAllGraphNodes())
        {
            if (n.IsRedirect) continue;
            bool hasBuildup = n.AttackBuildupFrames.HasValue;
            bool hasRange = n.AttackGameplayRange.HasValue;
            if (!hasBuildup && !hasRange) continue;

            string cardPath = AttackDbCard.EffectiveCardPath(n);
            if (string.IsNullOrEmpty(cardPath)) continue;
            var baseline = AttackDbCard.ReadDbPath(contentDir, cardPath);
            if (baseline == null || !baseline.Any)
                baseline = AttackDbCard.ReadDbPath(CustomAssets.Root, cardPath);
            if (baseline == null || !baseline.Any) continue;

            if (hasBuildup && baseline.HasBuildup && baseline.Buildup == n.AttackBuildupFrames!.Value)
            {
                n.AttackBuildupFrames = null;
                cleared++;
            }
            if (hasRange && baseline.HasRange
                && AttackDbCard.SameRange(baseline.Range, n.AttackGameplayRange!.Value))
            {
                n.AttackGameplayRange = null;
                cleared++;
            }
        }
        return cleared;
    }

    private void ResetNodeToVanilla(ComboNode node)
    {
        if (_comboGraph == null) return;
        if (!node.IsRoot && node.TreeIndex < 0 && node.Kind == ComboNodeKind.UserCreated)
        {
            DeleteAuthoredNode(node);
            if (!_comboGraph.Nodes.Contains(node))
                txtStatus.Text = $"Removed custom node '{node.DisplayName}' (reset = delete for new nodes)";
            return;
        }

        var vanillaPath = !string.IsNullOrEmpty(node.VanillaAnimPath)
            ? node.VanillaAnimPath
            : node.DefaultAnimPath;

        bool alreadyVanilla = string.Equals(node.AnimPath, vanillaPath, StringComparison.Ordinal)
            && string.IsNullOrEmpty(node.ImportedDisplayName)
            && !node.IsImportedFromMod
            && !node.ReplacedByAiNode
            && (string.IsNullOrEmpty(node.DefaultDBPath) || string.IsNullOrEmpty(node.SourceDBPath)
                || string.Equals(node.SourceDBPath, node.DefaultDBPath, StringComparison.OrdinalIgnoreCase));
        if (alreadyVanilla && node.IsRedirect && _comboGraph != null
            && _comboGraph.RedirectOriginalTargets.TryGetValue(node.Id, out var checkTargetId)
            && checkTargetId >= 0)
        {
            var checkTarget = _comboGraph.Nodes.FirstOrDefault(n => n.Id == checkTargetId);
            if (checkTarget != null && node.ResolvedRedirectNodeId != checkTarget.Id)
                alreadyVanilla = false;
        }
        // Branch chances + conditions: edits on this node's writeback struct also count
        // as changes (the dialog resolves the same owner struct, so reset restores them).
        int chanceStructIdx = node.TreeIndex < 0 || _comboGraph == null
            ? -1 : TreeChanceMath.ResolveWriteStruct(_comboGraph, node);
        bool chanceModified = chanceStructIdx >= 0 && _comboGraph != null
            && _comboGraph.TreeRowProbabilityOverrides.ContainsKey(chanceStructIdx);
        bool condModified = false;
        if (chanceStructIdx >= 0 && _comboGraph != null)
        {
            if (_comboGraph.TreeRowConditionOverrides.ContainsKey(chanceStructIdx))
                condModified = true;
            else if (_comboGraph.TreeRows.TryGetValue(chanceStructIdx, out var condResetRows))
                condModified = condResetRows.Any(r => r.ConditionInstanceIndex != 0
                    && _comboGraph.TreeConditionParamOverrides.ContainsKey(r.ConditionInstanceIndex));
        }
        // Conditions on rows this node appends (new links) reset with the node too.
        var nodeEdgeCondKeys = _comboGraph == null
            ? new List<string>()
            : _comboGraph.Edges
                .Where(ed => ed.FromNodeId == node.Id)
                .Select(ComboGraph.EdgeConditionKey)
                .Where(k => _comboGraph.TreeEdgeConditionOverrides.ContainsKey(k))
                .ToList();
        if (nodeEdgeCondKeys.Count > 0) condModified = true;
        if (chanceModified || condModified) alreadyVanilla = false;
        if (alreadyVanilla)
        {
            txtStatus.Text = $"{node.DisplayName} is already vanilla";
            return;
        }

        PushUndo("Reset node to vanilla");
        if (chanceModified) _comboGraph?.TreeRowProbabilityOverrides.Remove(chanceStructIdx);
        if (condModified && _comboGraph != null)
        {
            if (chanceStructIdx >= 0)
            {
                _comboGraph.TreeRowConditionOverrides.Remove(chanceStructIdx);
                if (_comboGraph.RetargetProvenance != null)
                    foreach (var pk in _comboGraph.RetargetProvenance.Keys
                        .Where(k => k.StartsWith($"R:{chanceStructIdx}:", StringComparison.Ordinal)).ToList())
                        _comboGraph.RetargetProvenance.Remove(pk);
                // Links merged into this struct's rows just lost their AddedKeys (the
                // whole override map went) - drop the merges too or export would skip
                // their appended rows while the target keys no longer exist.
                foreach (var ed in _comboGraph.Edges
                    .Where(ed => ed.MergedIntoStruct == chanceStructIdx).ToList())
                    ClearEdgeMerge(ed);
            }
            if (_comboGraph.TreeRows.TryGetValue(chanceStructIdx, out var condResetRows2))
            {
                foreach (int expIdx in condResetRows2
                    .Select(r => r.ConditionInstanceIndex).Where(i => i != 0).Distinct())
                    _comboGraph.TreeConditionParamOverrides.Remove(expIdx);
            }
            foreach (string edgeKey in nodeEdgeCondKeys)
            {
                _comboGraph.TreeEdgeConditionOverrides.Remove(edgeKey);
                if (_comboGraph.RetargetProvenance != null)
                    foreach (var pk in _comboGraph.RetargetProvenance.Keys
                        .Where(k => k.StartsWith($"E:{edgeKey}:", StringComparison.Ordinal)).ToList())
                        _comboGraph.RetargetProvenance.Remove(pk);
            }
        }
        bool hadAiReplace = node.ReplacedByAiNode;
        if (hadAiReplace) ClearAiReplace(node);

        if (!string.IsNullOrEmpty(vanillaPath))
            node.AnimPath = vanillaPath;
        node.IsImportedFromMod = false;
        node.ImportedDisplayName = "";
        // The export filter treats SourceDBPath != DefaultDBPath as a modification, so a reset
        // has to clear it too or the node would still be exported as changed.
        // AttackDB tuning belongs to the card, not the swap: keep it when the card is
        // unchanged, otherwise re-baseline onto the restored card like a drop does.
        string oldCard = AttackDbCard.EffectiveCardPath(node);
        node.SourceDBPath = node.DefaultDBPath;
        string newCard = AttackDbCard.EffectiveCardPath(node);
        if (!string.Equals(oldCard, newCard, StringComparison.OrdinalIgnoreCase))
        {
            var inherited = FindSharedCardTuning(newCard, node);
            node.AttackBuildupFrames = inherited?.Buildup;
            node.AttackGameplayRange = inherited?.Range;
            node.HitEdits = inherited?.HitEdits;
        }

        if (node.IsRedirect && _comboGraph != null
            && _comboGraph.RedirectOriginalTargets.TryGetValue(node.Id, out var origTargetId)
            && origTargetId >= 0)
        {
            var origTarget = _comboGraph.Nodes.FirstOrDefault(n => n.Id == origTargetId);
            if (origTarget != null && node.ResolvedRedirectNodeId != origTarget.Id)
            {
                int oldTargetId = node.ResolvedRedirectNodeId;
                node.ResolvedRedirectNodeId = origTarget.Id;
                MoveOwnedRedirectRows(node, oldTargetId, origTarget.Id);
                RevertRedirectRowConditions(node);

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
        txtStatus.Text = $"Reset {node.DisplayName} to vanilla"
            + (hadAiReplace ? " (AI replace reverted)" : "")
            + (chanceModified ? " (branch chances restored)" : "")
            + (condModified ? " (condition edits restored)" : "");
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
        // CustomAssets is a persistent library, not project state - custom cards in the
        // Custom tab survive imports and resets, so they never count as changes here.
        if (_unitCaches.Count > 0) return true;
        if (_isModLoaded) return true;
        return false;
    }

    private async System.Threading.Tasks.Task ResetProjectToVanillaAsync(string statusMessage)
    {
        // Always multi-second (parser re-init + MainChar reparse), so show the overlay
        // immediately - covers Import "start fresh" (before its step dialog opens) and
        // New Project. Blocks input while the reset runs.
        UpdateLoading("Resetting to vanilla...", statusMessage);
        loadingOverlay.Visibility = Visibility.Visible;
        try
        {
            _comboGraph = null;
            _unitCaches.Clear();
            _propsByVariant.Clear();
            _currentUnitProps = null;
            UnitPropertiesManager.ClearPendingFocus(_contentPath);
            UnitPropertiesManager.ClearPendingModel(_contentPath);
            _activeVariant = null;
            _activeWeapon = null;
            _customMoveset = null;
            _isModLoaded = false;
            _vanillaGraph = null;
            _moddedGraph = null;
            _nodeDiffs = new();
            ClearNodeSelection();
            ClearUndoHistory();
            UpdateUnitPropertiesButtonVisibility();

            try { _parser.SetOverlayProvider(null); } catch { }

            // CustomAssets is the persistent library - reset clears project state only,
            // so custom cards stay registered and their files stay on disk.
            try
            {
                var contentDir = Setup.ContentDetector.ResolveContentDir(_contentPath);
                var fresh = new AnimationParser();
                fresh.Initialize(_contentPath, contentDir);
                fresh.MountCustomIntoProvider(CustomAssets.Root);
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
        finally
        {
            loadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ConfirmDialog(
            "New Project",
            "Discard all changes and imported mods and reset the project to vanilla?",
            "Reset to Vanilla",
            "Cancel",
            "Discards: unit graph edits, custom nodes and links, imported mods.\n" +
            "Keeps: Custom Section files on disk (the library stays).")
        { Owner = this };
        if (dlg.ShowDialog() != true) return;

        await ResetProjectToVanillaAsync("New project — reset to vanilla");
    }

    private async void NewMoveset_Click(object sender, RoutedEventArgs e)
    {
        CommitAttackDbPanel();
        var dlg = new NewMovesetDialog(_contentPath) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        var info = dlg.Result;

        if (_customMoveset != null || ProjectHasExistingChanges())
        {
            var confirm = new ConfirmDialog(
                "New Moveset",
                "Creating a custom moveset discards all current edits. Continue?",
                "Discard & Create",
                "Cancel",
                "Discards: unit graph edits, custom nodes and links, imported mods.\n" +
                "Keeps: Custom Section files on disk (the library stays).")
            { Owner = this };
            if (confirm.ShowDialog() != true) return;
            await ResetProjectToVanillaAsync("New custom moveset — reset to vanilla");
        }

        _customMoveset = info;
        EnterCustomMovesetContext(info);
    }

    private void EnterCustomMovesetContext(CustomMovesetInfo info)
    {
        ClearNodeSelection();
        ClearUndoHistory();

        _activeStance = info.DonorArch;
        _activeVariant = info.DonorVariantTag;
        _activeWeapon = string.IsNullOrEmpty(info.DonorWeaponTag)
            ? null
            : NormalizeWeaponForCache(info.DonorArch, info.DonorWeaponTag!);
        _currentUnitProps = null;
        _vanillaGraph = null;
        _moddedGraph = null;
        _nodeDiffs = new();
        _originalVanillaComboGraph = null;
        UpdateVariantButtonVisibility();
        UpdateWeaponButtonVisibility();
        UpdateUnitPropertiesButtonVisibility();

        _comboGraph = new ComboGraph { WeaponName = info.DonorArch };
        _nodePositions.Clear();
        _comboTranslate.X = 0;
        _comboTranslate.Y = 0;
        LayoutComboGraph();
        RenderComboGraph();
        btnExport.IsEnabled = true;
        txtComboInfo.Text = $"Custom {info.DisplayName} - {info.DonorArch} (0 nodes, 0 start entries)";
        txtStatus.Text = $"Custom moveset '{info.DisplayName}' created - drop attack cards onto the canvas to build your combo";
        FilterMoves();
    }

    private void ResetComboCamera_Click(object sender, RoutedEventArgs e)
    {
        _comboTranslate.X = 0;
        _comboTranslate.Y = 0;
        txtStatus.Text = "Combo graph camera reset";
    }

    private void AutoArrange_Click(object sender, RoutedEventArgs e)
    {
        if (_comboGraph == null || _opInProgress) return;
        PushUndo("Auto-arrange");
        // Clear first: LayoutComboGraph restores any positions that remain, so an
        // empty table is what makes it lay the whole graph out from scratch
        // (PushUndo above captured the old arrangement).
        _nodePositions.Clear();
        LayoutComboGraph();
        RenderComboGraph();
        txtStatus.Text = $"Auto-arranged {_comboGraph.Nodes.Count(n => !IsBeginnerHiddenAi(n))} node(s){AiHiddenSuffix()}";
    }

    private void ResetAllToVanilla_Click(object sender, RoutedEventArgs e)
    {
        if (_comboGraph == null) return;

        int pending = ApplyResetAllToVanilla(false);
        if (pending == 0)
        {
            // Graph untouched, but nodes may have been dragged - restore the default
            // layout too (undoable, so it can be reverted like any other change).
            PushUndo("Reset all to vanilla");
            _nodePositions.Clear();
            LayoutComboGraph();
            RenderComboGraph();
            txtStatus.Text = "Already vanilla - node positions reset to default";
            return;
        }

        var confirm = new ConfirmDialog(
            "Reset All to Vanilla",
            $"Reset {pending} change(s) back to vanilla? This discards your combo edits for the current tree.",
            "Reset to Vanilla",
            "Cancel")
        { Owner = this };
        if (confirm.ShowDialog() != true) return;

        PushUndo("Reset all to vanilla");
        int resetCount = ApplyResetAllToVanilla(true);

        // LayoutComboGraph restores saved positions if any remain, so clear first to
        // snap every node back to its default spot (PushUndo above captured the old ones).
        _nodePositions.Clear();
        LayoutComboGraph();
        RenderComboGraph();
        txtStatus.Text = resetCount > 0
            ? $"Reset {resetCount} change(s) to vanilla, node positions restored to default"
            : "Already vanilla - node positions reset to default";
    }

    private int ApplyResetAllToVanilla(bool apply)
    {
        if (_comboGraph == null) return 0;

        int resetCount = 0;

        // Start Entries first: an edited entry may point at a custom node removed below.
        if (_comboGraph.StartEntries != null)
        {
            foreach (var entry in _comboGraph.StartEntries)
            {
                int modifiedRows = entry.Transitions.Count(t => t.IsModified());
                if (modifiedRows == 0) continue;
                resetCount += modifiedRows;
                if (apply)
                {
                    entry.Transitions.RemoveAll(t => t.OriginalIndex < 0);
                    foreach (var t in entry.Transitions)
                    {
                        t.Probability = t.OriginalProbability;
                        t.TargetNodeId = t.OriginalTargetNodeId;
                        t.TargetTreeIndex = t.OriginalTargetTreeIndex;
                        t.TargetName = t.OriginalTargetName;
                        t.Removed = false;
                    }
                }
            }
        }

        // Branch-chance edits (m_fProbability): row edits reset like delay windows,
        // unlike retargets. New-link edge probabilities die with the links below.
        var chanceOverrides = _comboGraph?.TreeRowProbabilityOverrides;
        if (chanceOverrides != null && chanceOverrides.Count > 0)
        {
            resetCount += chanceOverrides.Sum(kvp => kvp.Value.Count);
            if (apply) chanceOverrides.Clear();
        }

        // Condition edits: param overrides (by condition export) + row clear/re-route
        // edits reset alongside chances.
        var condParamOverrides = _comboGraph?.TreeConditionParamOverrides;
        if (condParamOverrides != null && condParamOverrides.Count > 0)
        {
            resetCount += condParamOverrides.Sum(kvp => kvp.Value.Count);
            if (apply) condParamOverrides.Clear();
        }
        var condRowOverrides = _comboGraph?.TreeRowConditionOverrides;
        if (condRowOverrides != null && condRowOverrides.Count > 0)
        {
            resetCount += condRowOverrides.Sum(kvp => kvp.Value.Count);
            if (apply) condRowOverrides.Clear();
        }
        var edgeCondOverrides = _comboGraph?.TreeEdgeConditionOverrides;
        if (edgeCondOverrides != null && edgeCondOverrides.Count > 0)
        {
            resetCount += edgeCondOverrides.Count;
            if (apply) edgeCondOverrides.Clear();
        }
        if (apply && _comboGraph?.RetargetProvenance != null && _comboGraph.RetargetProvenance.Count > 0)
            _comboGraph.RetargetProvenance.Clear();

        // Custom nodes (incl. authored redirects) and new links between surviving nodes.
        if (_comboGraph == null) return resetCount;   // flow-state re-assert (never actually null here)
        var customNodes = _comboGraph.Nodes
            .Where(n => !n.IsRoot && n.Kind == ComboNodeKind.UserCreated)
            .ToList();
        var customIds = new HashSet<int>(customNodes.Select(n => n.Id));
        var newLinks = _comboGraph.Edges
            .Where(ed => ed.IsNewLink && !customIds.Contains(ed.FromNodeId) && !customIds.Contains(ed.ToNodeId))
            .ToList();
        resetCount += customNodes.Count + newLinks.Count;
        if (apply && (customNodes.Count > 0 || newLinks.Count > 0))
        {
            foreach (var ed in newLinks) _comboGraph.Edges.Remove(ed);
            foreach (var cn in customNodes)
            {
                _comboGraph.Edges.RemoveAll(ed => ed.FromNodeId == cn.Id || ed.ToNodeId == cn.Id);
                _comboGraph.Nodes.Remove(cn);
                _nodePositions.Remove(cn.Id);
                _nodeBorders.Remove(cn.Id);
                _comboGraph.RedirectOriginalTargets.Remove(cn.Id);
            }
            foreach (var n in _comboGraph.Nodes)
            {
                if (n.IsRedirect && n.ResolvedRedirectNodeId >= 0 && customIds.Contains(n.ResolvedRedirectNodeId))
                    n.ResolvedRedirectNodeId = -1;
            }
            ClearNodeSelection();
        }

        foreach (var node in _comboGraph.Nodes)
        {
            if (node.IsRoot) continue;

            var vanillaPath = !string.IsNullOrEmpty(node.VanillaAnimPath)
                ? node.VanillaAnimPath
                : node.DefaultAnimPath;

            bool animModified = !string.IsNullOrEmpty(vanillaPath) && node.AnimPath != vanillaPath;
            bool dbModified = !string.IsNullOrEmpty(node.DefaultDBPath)
                && !string.IsNullOrEmpty(node.SourceDBPath)
                && !string.Equals(node.SourceDBPath, node.DefaultDBPath, StringComparison.OrdinalIgnoreCase);

            if (animModified || dbModified)
            {
                if (apply)
                {
                    if (animModified)
                    {
                        node.AnimPath = vanillaPath;
                        node.ImportedDisplayName = "";
                        node.IsImportedFromMod = false;
                    }
                    if (dbModified)
                    {
                        // Same rule as a single reset: tuning stays with the card. The card is
                        // changing back here, so re-baseline onto the restored card's values.
                        string oldCard = AttackDbCard.EffectiveCardPath(node);
                        node.SourceDBPath = node.DefaultDBPath;
                        string newCard = AttackDbCard.EffectiveCardPath(node);
                        if (!string.Equals(oldCard, newCard, StringComparison.OrdinalIgnoreCase))
                        {
                            var inherited = FindSharedCardTuning(newCard, node);
                            node.AttackBuildupFrames = inherited?.Buildup;
                            node.AttackGameplayRange = inherited?.Range;
                            node.HitEdits = inherited?.HitEdits;
                        }
                    }
                }
                resetCount++;
            }
            else if (node.AnimPath == vanillaPath && !string.IsNullOrEmpty(node.ImportedDisplayName))
            {
                if (apply)
                {
                    node.ImportedDisplayName = "";
                    node.IsImportedFromMod = false;
                }
                resetCount++;
            }

            if (node.ReplacedByAiNode)
            {
                if (apply) ClearAiReplace(node);
                resetCount++;
            }

            if (node.IsRedirect && _comboGraph.RedirectOriginalTargets.TryGetValue(node.Id, out var origTargetId)
                && origTargetId >= 0)
            {
                var origTarget = _comboGraph.Nodes.FirstOrDefault(n => n.Id == origTargetId);
                if (origTarget != null && node.ResolvedRedirectNodeId != origTarget.Id)
                {
                    if (apply)
                    {
                        int oldTargetId = node.ResolvedRedirectNodeId;
                        node.ResolvedRedirectNodeId = origTarget.Id;
                        MoveOwnedRedirectRows(node, oldTargetId, origTarget.Id);
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
                    }
                    resetCount++;
                }
            }
        }
        return resetCount;
    }

    private static bool IsValidMoveDropTarget(ComboNode? node)
        => node != null && !node.IsRedirect && !node.IsActionNode && !node.IsEventNode && !node.Removed;

    // Payload replacement targets: a vanilla node whose struct export can patch in place.
    // MainChar cannot host AI actions (the player pawn lacks the Order system), and
    // mod-imported / user-created nodes are not vanilla structs.
    private bool CanReplacePayload(ComboNode? node)
        => node != null && !node.IsRoot && !node.IsRedirect && !node.Removed
            && node.TreeIndex >= 0
            && node.Kind == ComboNodeKind.Vanilla
            && !node.IsImportedFromMod
            && !IsCurrentGraphMainChar();

    private bool IsValidNodeDrop(DragEventArgs e, ComboNode? node)
    {
        if (e.Data.GetDataPresent(typeof(AiNodeInfo)))
            return CanReplacePayload(node);
        if (node == null || !e.Data.GetDataPresent(typeof(MoveInfo)))
            return false;
        if (IsValidMoveDropTarget(node))
            return true;
        // Attack card onto an action/event node replaces the payload instead of the anim.
        return (node.IsActionNode || node.IsEventNode)
            && CanReplacePayload(node)
            && e.Data.GetData(typeof(MoveInfo)) is MoveInfo m
            && !string.IsNullOrEmpty(m.FullPath)
            && IsAttackDbPath(m.FullPath);
    }

    private static void ClearAiReplace(ComboNode node)
    {
        node.ReplacedByAiNode = false;
        node.ReplacedAiKind = "";
        node.ReplacedAiClass = "";
        node.ReplacedImportComboRel = "";
        node.ReplacedSourceTreeIndex = -1;
        node.ReplacedAttackCardRel = "";
    }

    private void ComboNode_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            var node = border.Tag as ComboNode;
            bool canDrop = IsValidNodeDrop(e, node);
            if (canDrop)
            {
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1));
                border.Background = new SolidColorBrush(Color.FromArgb(0x60, 0xa6, 0xe3, 0xa1));
                border.Opacity = 1;
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }
    }

    private void ComboNode_DragOver(object sender, DragEventArgs e)
    {
            var node = (sender as Border)?.Tag as ComboNode;
            e.Effects = IsValidNodeDrop(e, node)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
    }

    private void ComboNode_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border && border.Tag is ComboNode node)
        {
            RestoreNodeVisual(border, node);
            e.Handled = true;
        }
    }

    private void ComboNode_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border border && border.Tag is ComboNode node)
        {
            RestoreNodeVisual(border, node);

            if (e.Data.GetDataPresent(typeof(AiNodeInfo)))
            {
                // Drop on a node to replace that node's payload at export; drop on the
                // canvas background instead to append a new node. MainChar cannot host
                // them - the player pawn cannot run the Order system those actions rely
                // on for range/dash behavior.
                if (IsCurrentGraphMainChar())
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    txtStatus.Text = "AI actions are enemy-only - drop them on an enemy tree's canvas background "
                      + "to append a new node";
                    return;
                }
                if (e.Data.GetData(typeof(AiNodeInfo)) is not AiNodeInfo ai || !CanReplacePayload(node))
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }

                CommitAttackDbPanel();
                PushUndo("Replace payload");
                node.IsImportedFromMod = false;
                ClearAiReplace(node);
                node.ReplacedByAiNode = true;
                node.ReplacedAiKind = ai.Kind;
                node.ReplacedAiClass = ai.ClassName;
                node.ReplacedImportComboRel = ai.SourceComboRel;
                node.ReplacedSourceTreeIndex = ai.SourceTreeIndex;
                RenderComboGraph();
                txtStatus.Text = $"Payload replaced: {node.DisplayName} -> {ai.Kind} {ai.ClassName}";
                if (_nodeBorders.TryGetValue(node.Id, out var aiBorder))
                    PlayFusionAnimation(aiBorder);
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }

            if (!IsValidMoveDropTarget(node) && !IsValidNodeDrop(e, node))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(typeof(MoveInfo)))
            {
                var move = e.Data.GetData(typeof(MoveInfo)) as MoveInfo;
                if (move != null)
                {
                    string moveName = move.DisplayName;
                    if (!string.IsNullOrEmpty(move.FullPath) && IsAttackDbPath(move.FullPath))
                    {
                        var pretty = AnimationParser.PrettifyNodeName(move.FullPath);
                        if (!string.IsNullOrEmpty(pretty)) moveName = pretty;
                    }

                    // Action/event nodes cannot take an anim swap - an attack card replaces
                    // their payload instead (slots <- card, class/event prop cleared).
                    bool attackPayload = (node.IsActionNode || node.IsEventNode)
                        && CanReplacePayload(node)
                        && !string.IsNullOrEmpty(move.FullPath)
                        && IsAttackDbPath(move.FullPath);

                    // Flush any pending AttackDB edit into the pre-drop undo entry first.
                    CommitAttackDbPanel();
                    PushUndo(attackPayload ? "Replace payload" : "Replace move");
                    node.IsImportedFromMod = false;
                    if (node.Name == "MainChar_Stance" && !string.IsNullOrEmpty(move.Character)
                        && _stanceMap.ContainsKey(move.Character))
                    {
                        node.AnimPath = move.FullPath;
                        ApplyLocalChangeName(node, moveName);
                        RenderComboGraph();
                        txtStatus.Text = $"Combat Stance -> {move.Character} ({move.DisplayName})";
                    }
                    else if (attackPayload)
                    {
                        ClearAiReplace(node);
                        node.ReplacedByAiNode = true;
                        node.ReplacedAiKind = "Attack";
                        node.ReplacedAiClass = moveName;
                        node.ReplacedAttackCardRel = move.FullPath;
                        RenderComboGraph();
                        txtStatus.Text = $"Payload replaced: {node.Name} -> attack {moveName}";
                    }
                    else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        var linkedNodes = _comboGraph.Nodes
                            .Where(n => !n.IsRedirect && n.DefaultAnimPath == node.DefaultAnimPath)
                            .ToList();
                        foreach (var ln in linkedNodes)
                        {
                            ln.IsImportedFromMod = false;
                            if (ln.ReplacedByAiNode) ClearAiReplace(ln); // move drop wins over AI replace
                            ln.AnimPath = move.FullPath;
                            ApplyLocalChangeName(ln, moveName);
                            ApplyMoveSourceDbPath(ln, move);
                        }
                        RenderComboGraph();
                        txtStatus.Text = $"Replaced {linkedNodes.Count} linked nodes -> {moveName}";
                    }
                    else
                    {
                        if (node.ReplacedByAiNode) ClearAiReplace(node); // move drop wins over AI replace
                        node.AnimPath = move.FullPath;
                        ApplyLocalChangeName(node, moveName);
                        ApplyMoveSourceDbPath(node, move);
                        RenderComboGraph();
                        txtStatus.Text = $"Replaced: {node.Name} -> {moveName}";
                    }

                    if (_nodeBorders.TryGetValue(node.Id, out var newNodeBorder))
                        PlayFusionAnimation(newNodeBorder);
                }
            }
            e.Handled = true;
            }
        }

    private void ComboCanvas_DragEnter(object sender, DragEventArgs e)
    {
        ComboCanvas_DragOver(sender, e);
    }

    private void ComboCanvas_DragOver(object sender, DragEventArgs e)
    {
        // Background drops create a new attack node. Over a node border the node's own
        // handlers run first (they set e.Handled), so this only sees the canvas background.
        e.Effects = (e.Data.GetDataPresent(typeof(MoveInfo)) || e.Data.GetDataPresent(typeof(AiNodeInfo)))
            && _comboGraph != null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ComboCanvas_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_comboGraph == null) return;

        var canvasPos = e.GetPosition(comboCanvas);
        if (HitTestNode(canvasPos) != null) return; // owned by ComboNode_Drop

        if (e.Data.GetDataPresent(typeof(AiNodeInfo)))
        {
            if (IsCurrentGraphMainChar())
            {
                txtStatus.Text = "AI actions are enemy-only - switch to an enemy moveset "
                    + "to append action/event nodes";
                return;
            }
            if (e.Data.GetData(typeof(AiNodeInfo)) is AiNodeInfo aiNode)
                AddAiNode(aiNode, canvasPos);
            return;
        }
        if (!e.Data.GetDataPresent(typeof(MoveInfo))) return;
        if (e.Data.GetData(typeof(MoveInfo)) is not MoveInfo move) return;

        AddNewAttackNode(move, canvasPos);
    }

    private void AddAiNode(AiNodeInfo ai, Point canvasPos)
    {
        if (_comboGraph == null) return;

        CommitAttackDbPanel();
        PushUndo($"Add {ai.Kind.ToLowerInvariant()} node");

        int id = (_comboGraph.Nodes.Count > 0 ? _comboGraph.Nodes.Max(n => n.Id) : -1) + 1;
        var node = new ComboNode
        {
            Id = id,
            TreeIndex = -1,
            Kind = ComboNodeKind.UserCreated,
            Name = "",
            DisplayName = ai.DisplayName,
            IsActionNode = string.Equals(ai.Kind, "Action", StringComparison.Ordinal),
            ActionClass = string.Equals(ai.Kind, "Action", StringComparison.Ordinal) ? ai.ClassName : "",
            IsEventNode = string.Equals(ai.Kind, "Event", StringComparison.Ordinal),
            EventName = string.Equals(ai.Kind, "Event", StringComparison.Ordinal) ? ai.ClassName : "",
            ImportComboRel = ai.SourceComboRel,
            ImportSourceTreeIndex = ai.SourceTreeIndex,
            IsRoot = false,
            DirectionLabel = ""
        };

        _comboGraph.Nodes.Add(node);
        _nodePositions[id] = canvasPos;
        RenderComboGraph();

        if (_nodeBorders.TryGetValue(id, out var border))
            SelectNode(border, node);
        txtStatus.Text = $"Added {ai.Kind.ToLowerInvariant()} node '{ai.DisplayName}' - right-click it, Connect to… to link it";
    }

    private void AddNewAttackNode(MoveInfo move, Point canvasPos)
    {
        if (_comboGraph == null) return;
        string fullPath = move.FullPath ?? "";
        if (string.IsNullOrEmpty(fullPath))
        {
            txtStatus.Text = "Dragged card has no path";
            return;
        }

        CommitAttackDbPanel();
        PushUndo("Add attack node");

        int id = (_comboGraph.Nodes.Count > 0 ? _comboGraph.Nodes.Max(n => n.Id) : -1) + 1;
        string animPath = IsAttackDbPath(fullPath)
            ? _parser.ResolveAnimationFromDB(fullPath) ?? ""
            : fullPath;
        string display = string.IsNullOrEmpty(move.DisplayName)
            ? Path.GetFileNameWithoutExtension(fullPath)
            : move.DisplayName;
        if (IsAttackDbPath(fullPath))
        {
            var pretty = AnimationParser.PrettifyNodeName(fullPath);
            if (!string.IsNullOrEmpty(pretty)) display = pretty;
        }

        var node = new ComboNode
        {
            Id = id,
            TreeIndex = -1,
            Kind = ComboNodeKind.UserCreated,
            Name = "",
            DisplayName = display,
            AnimPath = animPath,
            DefaultAnimPath = "",
            VanillaAnimPath = "",
            DefaultDBPath = "",
            SourceDBPath = "",
            IsRoot = false,
            DirectionLabel = ""
        };
        ApplyMoveSourceDbPath(node, move);
        // Enemy-tree cards are attack DBs: record it as the node's DB path too, so the
        // exporter defers the node to the combo-tree pass instead of the MainChar DB clone.
        if (IsAttackDbPath(fullPath))
            node.DefaultDBPath = fullPath;

        _comboGraph.Nodes.Add(node);
        _nodePositions[id] = canvasPos;
        RenderComboGraph();

        if (_nodeBorders.TryGetValue(id, out var border))
            SelectNode(border, node);
        txtStatus.Text = $"Added node '{display}' - right-click it, Connect to… to link it";
    }

    #region Undo/Redo

    private sealed class UndoEntry
    {
        public string Label = "";
        public ComboGraph? Graph;
        public Dictionary<int, Point>? Positions;
        public UnitProperties? Props;
        // A card commit writes onto every unit sharing that card, so undo must carry the
        // tuning fields of ALL caches - the graph clone only covers the active unit.
        public Dictionary<string, Dictionary<int, (int? Buildup, float? Range, int? WinLo, int? WinHi)>>? CardTuning;
    }

    private UndoEntry CaptureCurrentState(string label)
    {
        return new UndoEntry
        {
            Label = label,
            Graph = _comboGraph != null ? CloneGraph(_comboGraph) : null,
            Positions = new Dictionary<int, Point>(_nodePositions),
            Props = CloneUnitProps(_currentUnitProps),
            CardTuning = CaptureCardTuning()
        };
    }

    private Dictionary<string, Dictionary<int, (int? Buildup, float? Range, int? WinLo, int? WinHi)>>? CaptureCardTuning()
    {
        var result = new Dictionary<string, Dictionary<int, (int? Buildup, float? Range, int? WinLo, int? WinHi)>>();
        foreach (var kvp in _unitCaches)
        {
            var g = kvp.Value?.Graph;
            if (g == null) continue;
            var fields = new Dictionary<int, (int? Buildup, float? Range, int? WinLo, int? WinHi)>();
            foreach (var n in g.Nodes)
                if (n.AttackBuildupFrames.HasValue || n.AttackGameplayRange.HasValue
                    || n.DelayWindowLo.HasValue || n.DelayWindowHi.HasValue)
                    fields[n.Id] = (n.AttackBuildupFrames, n.AttackGameplayRange, n.DelayWindowLo, n.DelayWindowHi);
            if (fields.Count > 0) result[kvp.Key] = fields;
        }
        return result.Count > 0 ? result : null;
    }

    private void RestoreCardTuning(Dictionary<string, Dictionary<int, (int? Buildup, float? Range, int? WinLo, int? WinHi)>>? snapshot)
    {
        foreach (var kvp in _unitCaches)
        {
            var g = kvp.Value?.Graph;
            if (g == null) continue;

            Dictionary<int, (int? Buildup, float? Range, int? WinLo, int? WinHi)>? fields = null;
            if (snapshot != null && snapshot.TryGetValue(kvp.Key, out var f)) fields = f;

            foreach (var n in g.Nodes)
            {
                if (fields != null && fields.TryGetValue(n.Id, out var v))
                {
                    n.AttackBuildupFrames = v.Buildup;
                    n.AttackGameplayRange = v.Range;
                    n.DelayWindowLo = v.WinLo;
                    n.DelayWindowHi = v.WinHi;
                }
                else
                {
                    n.AttackBuildupFrames = null;
                    n.AttackGameplayRange = null;
                    n.DelayWindowLo = null;
                    n.DelayWindowHi = null;
                }
            }
        }
    }

    private void PushUndo(string label)
    {
        if (_comboGraph == null) return;

        _undoStack.Add(CaptureCurrentState(label));
        if (_undoStack.Count > UNDO_MAX) _undoStack.RemoveAt(0);
        _redoStack.Clear();
    }

    private void ClearUndoHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            txtStatus.Text = "Nothing to undo";
            return;
        }

        var entry = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        _redoStack.Add(CaptureCurrentState(entry.Label));

        RestoreUndoEntry(entry);
        txtStatus.Text = $"Undid: {entry.Label}";
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            txtStatus.Text = "Nothing to redo";
            return;
        }

        var entry = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);

        _undoStack.Add(CaptureCurrentState(entry.Label));

        RestoreUndoEntry(entry);
        txtStatus.Text = $"Redid: {entry.Label}";
    }

    private void RestoreUndoEntry(UndoEntry entry)
    {
        if (entry.Graph == null) return;

        SetResetMode(false);
        ExitRetargetMode();
        ExitLinkMode();
        // The graph is about to be swapped out - drop any pending panel edit instead of
        // letting it land on the restored node.
        ClearAttackDbPanel();

        _comboGraph = CloneGraph(entry.Graph);
        _nodePositions = entry.Positions != null
            ? new Dictionary<int, Point>(entry.Positions)
            : new Dictionary<int, Point>();
        _currentUnitProps = CloneUnitProps(entry.Props);
        RestoreCardTuning(entry.CardTuning);

        SaveCurrentUnitToCache();
        ClearNodeSelection();
        RenderComboGraph();
    }

    private static ComboGraph CloneGraph(ComboGraph graph)
    {
        return new ComboGraph
        {
            WeaponName = graph.WeaponName,
            Nodes = graph.Nodes.Select(CloneNode).ToList(),
            Edges = graph.Edges.Select(e => new ComboEdge
            {
                FromNodeId = e.FromNodeId,
                ToNodeId = e.ToNodeId,
                InputName = e.InputName,
                IsRedirect = e.IsRedirect,
                IsNewLink = e.IsNewLink,
                OwnerRedirectNodeId = e.OwnerRedirectNodeId,
                NewLinkProbability = e.NewLinkProbability,
                FromClearMixup = e.FromClearMixup,
                MergedIntoStruct = e.MergedIntoStruct,
                MergedIntoRow = e.MergedIntoRow,
                MergedIntoKey = e.MergedIntoKey
            }).ToList(),
            RedirectOriginalTargets = new Dictionary<int, int>(graph.RedirectOriginalTargets),
            StartEntries = graph.StartEntries.Select(e => e.Clone()).ToList(),
            // TreeRows is a read-only parse snapshot (shared reference is safe);
            // overrides are mutable - deep-copy so undo clones stay isolated.
            TreeRows = graph.TreeRows,
            SourceGamePath = graph.SourceGamePath,
            TreeRowsBackfillAttempted = graph.TreeRowsBackfillAttempted,
            TreeNodeConditions = graph.TreeNodeConditions,
            TreeRowProbabilityOverrides = graph.TreeRowProbabilityOverrides
                .ToDictionary(kv => kv.Key, kv => new Dictionary<int, float>(kv.Value)),
            TreeConditionParamOverrides = graph.TreeConditionParamOverrides
                .ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value)),
            TreeRowConditionOverrides = graph.TreeRowConditionOverrides
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(
                    r => r.Key,
                    r => r.Value.Clone())),
            TreeEdgeConditionOverrides = graph.TreeEdgeConditionOverrides
                .ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
            RetargetProvenance = graph.RetargetProvenance == null
                ? null
                : new Dictionary<string, int>(graph.RetargetProvenance),
            ConduitTargets = graph.ConduitTargets
                .ToDictionary(kv => kv.Key, kv => new List<int>(kv.Value))
        };
    }

    private static ComboNode CloneNode(ComboNode n)
    {
        return new ComboNode
        {
            Id = n.Id,
            TreeIndex = n.TreeIndex,
            Name = n.Name,
            AnimPath = n.AnimPath,
            DefaultAnimPath = n.DefaultAnimPath,
            DefaultDBPath = n.DefaultDBPath,
            SourceDBPath = n.SourceDBPath,
            DisplayName = n.DisplayName,
            ImportedDisplayName = n.ImportedDisplayName,
            IsRoot = n.IsRoot,
            Depth = n.Depth,
            InputLabel = n.InputLabel,
            DirectionLabel = n.DirectionLabel,
            VanillaAnimPath = n.VanillaAnimPath,
            IsRedirect = n.IsRedirect,
            RedirectTargetTreeIndex = n.RedirectTargetTreeIndex,
            ResolvedRedirectNodeId = n.ResolvedRedirectNodeId,
            AttackBuildupFrames = n.AttackBuildupFrames,
            AttackGameplayRange = n.AttackGameplayRange,
            DelayWindowLo = n.DelayWindowLo,
            DelayWindowHi = n.DelayWindowHi,
            Kind = n.Kind,
            IsImportedFromMod = n.IsImportedFromMod,
            OwnedContinuation = n.OwnedContinuation,
            IsActionNode = n.IsActionNode,
            ActionClass = n.ActionClass,
            IsEventNode = n.IsEventNode,
            EventName = n.EventName,
            ImportComboRel = n.ImportComboRel,
            ImportSourceTreeIndex = n.ImportSourceTreeIndex,
            Removed = n.Removed,
            ReplacedByAiNode = n.ReplacedByAiNode,
            ReplacedAiKind = n.ReplacedAiKind,
            ReplacedAiClass = n.ReplacedAiClass,
            ReplacedImportComboRel = n.ReplacedImportComboRel,
            ReplacedSourceTreeIndex = n.ReplacedSourceTreeIndex,
            ReplacedAttackCardRel = n.ReplacedAttackCardRel
        };
    }

    private static UnitProperties? CloneUnitProps(UnitProperties? props)
    {
        if (props == null) return null;
        return new UnitProperties
        {
            Health = props.Health,
            Structure = props.Structure,
            MemoryLimit = props.MemoryLimit,
            HitsCount = props.HitsCount,
            MemoryFlushLimit = props.MemoryFlushLimit,
            ImmuneToFocus = props.ImmuneToFocus,
            ModelOverride = props.ModelOverride,
            ModelSet = props.ModelSet,
            PhaseSwap = props.PhaseSwap,
            PhaseSwapTarget = props.PhaseSwapTarget
        };
    }

    private static bool UnitPropsEqual(UnitProperties? a, UnitProperties? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        return a.Health == b.Health
            && a.Structure == b.Structure
            && a.MemoryLimit == b.MemoryLimit
            && a.HitsCount == b.HitsCount
            && a.MemoryFlushLimit == b.MemoryFlushLimit
            && a.ImmuneToFocus == b.ImmuneToFocus
            && string.Equals(a.ModelOverride, b.ModelOverride, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.ModelSet, b.ModelSet, StringComparison.OrdinalIgnoreCase)
            && a.PhaseSwap == b.PhaseSwap
            && string.Equals(a.PhaseSwapTarget, b.PhaseSwapTarget, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Unit Properties
    private void BtnUnitProperties_Click(object sender, RoutedEventArgs e)
    {
        if (unitPropsPopupBorder.Visibility == Visibility.Visible)
        {
            TryCloseUnitProps();
            return;
        }
        PopulateUnitProperties();
        SetWebViewVisible(false);
        unitPropsPopupBorder.Visibility = Visibility.Visible;
    }

    private void UnitPropsPopupBackground_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == unitPropsPopupBorder)
            TryCloseUnitProps();
    }

    /// <summary>
    /// Saves + closes the Unit Properties popup. Blocks the close while Last Man phase swap
    /// is checked but no moveset is picked - export token-matches only a selected target
    /// (UnitPropertiesManager.PhaseSwapActive needs both), so a checked box with no target
    /// would otherwise save a setting that silently does nothing.
    /// </summary>
    private bool TryCloseUnitProps()
    {
        if (unitPropsPhaseSection.Visibility == Visibility.Visible
            && chkPhaseSwap.IsChecked == true
            && cmbPhaseSwapTarget.SelectedItem == null)
        {
            bool noTargets = cmbPhaseSwapTarget.Items.Count == 0;
            MessageBox.Show(
                noTargets
                    ? "Last Man phase swap is checked, but no phase-swap movesets were found for this install.\n\n"
                      + "Uncheck phase swap to close - without a target the swap does nothing on export."
                    : "Last Man phase swap is checked but no moveset is picked.\n\n"
                      + "Pick a moveset from the dropdown (or uncheck phase swap) before closing - "
                      + "without a target the swap does nothing on export.",
                "Unit Properties",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        SaveUnitProperties();
        unitPropsPopupBorder.Visibility = Visibility.Collapsed;
        SetWebViewVisible(true);
        return true;
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
        ["ImmuneToFocus"] = ("Immune to Focus Attacks", "Derived from the unit's VitalPointDB: checked when that database contains no Vital Point entries, meaning the unit has no Focus target (this is how Yang behaves by default). When checked, the export repoints the family's shared ArchetypeDB at Yang's empty one. This is a single flag per family - checking it on any member checks it for every member, and un-checking cannot undo a write that was never made for you.", null),
        ["Model"] = ("Model Override", "Two ways to change this unit's looks, both picked from the Choose dialog:\n\n- Roster swap (family): replaces every spawn of this unit's family with another family's whole roster of looks (e.g. all Grunts get FireDisciple's looks). Per-spawn variety is kept: each Character BP is matched to the source's same-hideout, same-gender meshes first, rotating round-robin. Covers the whole family by design; '(default)' clears it.\n\n- Specific model (variant): replaces just this variant's spawned mesh in its own scenario files (levels, training room, void, tutorial): matching m_OverrideSkeletalMesh entries point at the chosen mesh on export. Scoped to the current unit variant - other variants keep their in-game models. '(default)' clears the pick.\n\nThe two modes exclude each other: picking one clears the other for this family. 'Import custom model...' accepts an already-converted SK_ uasset bundle on Sifu's Base_skeleton - it is validated, stored in CustomAssets and listed under the Custom group.\n\nCaveats: hats and held props are attachments of the old skeleton and may float or disappear; female spawns of a specific-model variant are replaced too; bosses' phase models are shared, so a pick there still changes the whole boss fight.", null),
        ["PhaseSwap"] = ("Phase Swap", "Makes this enemy phase-swap into the chosen moveset when taken down (Last Man transition, the mechanic Grunts already use against the player).\n\nOn export, every scenario of this unit's tier is staged - levels, hideouts, arenas, challenge rooms. Scenarios that never phase get a Last Man takedown transition appended pointing at the picked moveset DB (health, structure and super-dizzy reset; the Last Man gauge is not required). Scenarios that already phase (the vanilla Grunt roster) get their existing takedown transition retargeted at the pick instead.\n\nPairing is per variant, not by level: a per-level DB (e.g. FireDisciple / H5 (MiniBoss)) patches every scenario that spawns this variant's tier - H2 through H5 hideouts, arenas, mute arenas - by creating the DB reference when missing. Cross-family targets work too (e.g. Grunt Advanced -> FireDisciple MiniBoss). Picking a DB the scenario already phases into (self-swap) is skipped.\n\nBosses (family-wide scenarios) are excluded. Hideout/arena FDs, Grunts, Bodyguards, BigGuys and FlashKicks are the intended subjects. The export log lists every patched, retargeted or skipped scenario.", null),
    };

    /// <summary>
    /// Appends the other variants that share this unit's VitalPointDB owner, so the tooltip spells out
    /// exactly which units the flag will cover on export.
    /// </summary>
    private string FocusInheritanceSuffix(string key)
    {
        if (key != "ImmuneToFocus" || string.IsNullOrEmpty(_contentPath)) return "";

        string variantTag = CurrentVariantTag();
        var members = UnitPropertiesManager.GetFocusFamilyMembers(_contentPath, variantTag);

        var others = new List<string>();
        foreach (var m in members)
            if (!m.Equals(variantTag, StringComparison.OrdinalIgnoreCase)) others.Add(m);

        if (others.Count == 0)
            return "\n\nNo other variant shares this unit's VitalPointDB, so the flag applies here only.";

        return "\n\nShared with: " + string.Join(", ", others)
            + ".\nOne flag covers all of them; the export writes the shared ArchetypeDB once.";
    }

    private void UnitPropsInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string? key = btn.Tag as string;
        if (key == null || !UnitPropsHelp.TryGetValue(key, out var info)) return;

        unitPropsInfoTitle.Text = info.Title;
        unitPropsInfoDesc.Text = info.Desc + FocusInheritanceSuffix(key);
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
        string variantTag = CurrentVariantTag();

        var fresh = UnitPropertiesManager.Read(_contentPath, variantTag);
        _unitPropsDefaults = CloneUnitProps(fresh);
        if (_propsByVariant.TryGetValue(variantTag, out var stored) && stored != null)
            UnitPropertiesManager.MergeInto(fresh, stored);
        if (_currentUnitProps != null)
            UnitPropertiesManager.MergeInto(fresh, _currentUnitProps);
        _currentUnitProps = fresh;
        _propsByVariant[variantTag] = _currentUnitProps;

        unitPropsTitle.Text = $"Unit Properties \u2014 {variantTag}";

        // MainChar has no ArchetypeDB / ContextDefense / VitalPoint files - only the model row applies.
        bool isMainChar = IsMainCharStance();
        var sectionVisibility = isMainChar ? Visibility.Collapsed : Visibility.Visible;
        unitPropsArchSection.Visibility = sectionVisibility;
        unitPropsDefenseSection.Visibility = sectionVisibility;
        unitPropsFocusSection.Visibility = sectionVisibility;

        // Phase swap needs a scenario family with per-tier files; boss families share one
        // multi-phase scenario file, so the section stays hidden there too.
        var phaseFamily = ModelCatalog.FamilyDirForVariant(variantTag);
        bool phaseAllowed = !isMainChar && phaseFamily != null
            && !ModelCatalog.IsFamilyWideScenarioFamily(phaseFamily);
        unitPropsPhaseSection.Visibility = phaseAllowed ? Visibility.Visible : Visibility.Collapsed;
        if (phaseAllowed) PopulatePhaseSwapTargets();

        SetField(txtUnitHealth, _currentUnitProps.Health?.ToString("F1"), _currentUnitProps.Health.HasValue);
        SetField(txtUnitStructure, _currentUnitProps.Structure?.ToString("F1"), _currentUnitProps.Structure.HasValue);
        SetField(txtMemoryLimit, _currentUnitProps.MemoryLimit?.ToString("F1"), _currentUnitProps.MemoryLimit.HasValue);
        SetField(txtHitsCount, _currentUnitProps.HitsCount?.ToString(), _currentUnitProps.HitsCount.HasValue);
        SetField(txtFlushLimit, _currentUnitProps.MemoryFlushLimit?.ToString("F1"), _currentUnitProps.MemoryFlushLimit.HasValue);

        // Family-wide: show the effective value, not this unit's own (possibly stale) copy.
        chkImmuneToFocus.IsChecked = UnitPropertiesManager.GetEffectiveFocus(_contentPath, variantTag);
        _currentUnitProps.ImmuneToFocus = chkImmuneToFocus.IsChecked;

        _selectedModelOverride = null;
        _selectedModelSet = null;
        if (UnitPropertiesManager.EnableModelFeature)
        {
            _selectedModelOverride = UnitPropertiesManager.GetEffectiveModel(_contentPath, variantTag)
                ?? _currentUnitProps.ModelOverride;
            _selectedModelSet = UnitPropertiesManager.GetEffectiveModelSet(_contentPath, variantTag)
                ?? _currentUnitProps.ModelSet;
            UpdateModelOverrideLabel();
        }

        chkPhaseSwap.IsChecked = _currentUnitProps.PhaseSwap ?? false;
        SelectPhaseTarget(_currentUnitProps.PhaseSwapTarget);
        cmbPhaseSwapTarget.IsEnabled = chkPhaseSwap.IsChecked == true;
    }

    private List<ModelCatalog.PhaseSwapTargetOption>? _phaseTargetOptions;
    private string? _phaseTargetsContentDir;

    private void PopulatePhaseSwapTargets()
    {
        var contentDir = Setup.ContentDetector.ResolveContentDir(_contentPath);
        if (_phaseTargetOptions == null
            || !string.Equals(_phaseTargetsContentDir, contentDir, StringComparison.OrdinalIgnoreCase))
        {
            _phaseTargetOptions = ModelCatalog.PhaseSwapTargetOptions(_contentPath).ToList();
            _phaseTargetsContentDir = contentDir;
            cmbPhaseSwapTarget.ItemsSource = _phaseTargetOptions;
            cmbPhaseSwapTarget.DisplayMemberPath = nameof(ModelCatalog.PhaseSwapTargetOption.Label);
        }
    }

    private void SelectPhaseTarget(string? rel)
    {
        if (_phaseTargetOptions == null) return;
        cmbPhaseSwapTarget.SelectedItem = null;
        if (string.IsNullOrEmpty(rel)) return;
        cmbPhaseSwapTarget.SelectedItem = _phaseTargetOptions
            .FirstOrDefault(o => string.Equals(o.Rel, rel, StringComparison.OrdinalIgnoreCase));
    }

    private void ChkPhaseSwap_Changed(object sender, RoutedEventArgs e)
    {
        if (cmbPhaseSwapTarget != null)
            cmbPhaseSwapTarget.IsEnabled = chkPhaseSwap.IsChecked == true;
    }

    private void UpdateModelOverrideLabel()
    {
        if (txtModelOverride == null) return;
        bool hasSet = !string.IsNullOrEmpty(_selectedModelSet);
        txtModelOverride.Text = hasSet
            ? $"Roster: {_selectedModelSet} (family)"
            : ModelCatalog.DisplayNameForGamePath(_selectedModelOverride);
        txtModelOverride.Foreground = new SolidColorBrush(
            hasSet || !string.IsNullOrEmpty(_selectedModelOverride)
                ? Color.FromRgb(0xa6, 0xe3, 0xa1)
                : Color.FromRgb(0x6c, 0x70, 0x86));
    }

    private void SaveUnitProperties()
    {
        string variantTag = CurrentVariantTag();

        var props = new UnitProperties();

        if (IsMainCharStance())
        {
            // MainChar's popup only exposes the model row: no ArchetypeDB, ContextDefense
            // or VitalPoint files exist for the player, so every other field is out of scope.
            props.ModelOverride = _selectedModelOverride;
            props.ImmuneToFocus = _currentUnitProps?.ImmuneToFocus;

            bool mainModelChanged = !string.Equals(props.ModelOverride,
                UnitPropertiesManager.GetEffectiveModel(_contentPath, variantTag), StringComparison.OrdinalIgnoreCase);
            if (mainModelChanged)
                UnitPropertiesManager.SetPendingModel(_contentPath, variantTag, props.ModelOverride);

            if (UnitPropsEqual(props, _currentUnitProps)) return;

            PushUndo("Unit properties");
            _currentUnitProps = props;
            _propsByVariant[variantTag] = props;
            txtStatus.Text = $"Unit properties saved for {variantTag}";
            return;
        }

        if (txtUnitHealth.IsEnabled && float.TryParse(txtUnitHealth.Text, out float h)) props.Health = h;
        if (txtUnitStructure.IsEnabled && float.TryParse(txtUnitStructure.Text, out float s)) props.Structure = s;
        if (txtMemoryLimit.IsEnabled && float.TryParse(txtMemoryLimit.Text, out float ml)) props.MemoryLimit = ml;
        if (txtHitsCount.IsEnabled && int.TryParse(txtHitsCount.Text, out int hc)) props.HitsCount = hc;
        if (txtFlushLimit.IsEnabled && float.TryParse(txtFlushLimit.Text, out float fl)) props.MemoryFlushLimit = fl;
        props.ImmuneToFocus = chkImmuneToFocus.IsChecked ?? false;
        props.ModelOverride = _selectedModelOverride;
        props.ModelSet = _selectedModelSet;
        if (unitPropsPhaseSection.Visibility == Visibility.Visible)
        {
            props.PhaseSwap = chkPhaseSwap.IsChecked == true;
            props.PhaseSwapTarget = (cmbPhaseSwapTarget.SelectedItem as ModelCatalog.PhaseSwapTargetOption)?.Rel;
        }

        bool effective = UnitPropertiesManager.GetEffectiveFocus(_contentPath, variantTag);
        bool focusChanged = props.ImmuneToFocus != effective;
        if (focusChanged)
            UnitPropertiesManager.SetPendingFocus(_contentPath, variantTag, props.ImmuneToFocus!.Value);

        bool modelChanged = !string.Equals(props.ModelOverride,
            UnitPropertiesManager.GetEffectiveModel(_contentPath, variantTag), StringComparison.OrdinalIgnoreCase);
        if (modelChanged)
            UnitPropertiesManager.SetPendingModel(_contentPath, variantTag, props.ModelOverride);

        bool modelSetChanged = !string.Equals(props.ModelSet,
            UnitPropertiesManager.GetEffectiveModelSet(_contentPath, variantTag), StringComparison.OrdinalIgnoreCase);
        if (modelSetChanged)
            UnitPropertiesManager.SetPendingModelSet(_contentPath, variantTag, props.ModelSet);

        if (UnitPropsEqual(props, _currentUnitProps)) return;

        PushUndo("Unit properties");
        _currentUnitProps = props;
        _propsByVariant[variantTag] = props;
        if (focusChanged)
            PropagateFocusToFamily(variantTag, props.ImmuneToFocus!.Value);
        if (modelChanged && !string.IsNullOrEmpty(props.ModelOverride))
            PropagateModelSetToFamily(variantTag, null);
        if (modelSetChanged)
        {
            PropagateModelSetToFamily(variantTag, props.ModelSet);
            if (!string.IsNullOrEmpty(props.ModelSet))
                PropagateModelToFamily(variantTag, null);
        }
        txtStatus.Text = $"Unit properties saved for {variantTag}";
    }

    /// <summary>
    /// Roster swaps are family-wide, so a pick (or clear) is written to every variant of the
    /// family and into the session pending store - mirrors PropagateModelToFamily.
    /// </summary>
    private void PropagateModelSetToFamily(string variantTag, string? sourceKey)
    {
        var family = ModelCatalog.FamilyDirForVariant(variantTag);
        if (family == null) return;

        foreach (var kvp in _propsByVariant)
        {
            if (!SameModelFamily(kvp.Key, family)) continue;
            kvp.Value.ModelSet = sourceKey;
            UnitPropertiesManager.SetPendingModelSet(_contentPath, kvp.Key, sourceKey);
        }

        foreach (var kvp in _unitCaches)
        {
            var parts = kvp.Key.Split('|');
            if (parts.Length < 2 || !SameModelFamily(parts[1], family)) continue;

            var cached = kvp.Value.Props;
            if (cached == null)
                kvp.Value.Props = cached = new UnitProperties();
            cached.ModelSet = sourceKey;
            UnitPropertiesManager.SetPendingModelSet(_contentPath, parts[1], sourceKey);
        }
    }

    private void PropagateModelToFamily(string variantTag, string? gamePath)
    {
        var family = ModelCatalog.FamilyDirForVariant(variantTag);
        if (family == null) return;

        foreach (var kvp in _propsByVariant)
        {
            if (!SameModelFamily(kvp.Key, family)) continue;
            kvp.Value.ModelOverride = gamePath;
            UnitPropertiesManager.SetPendingModel(_contentPath, kvp.Key, gamePath);
        }

        foreach (var kvp in _unitCaches)
        {
            var parts = kvp.Key.Split('|');
            if (parts.Length < 2 || !SameModelFamily(parts[1], family)) continue;

            var cached = kvp.Value.Props;
            if (cached == null)
                kvp.Value.Props = cached = new UnitProperties();
            cached.ModelOverride = gamePath;
            UnitPropertiesManager.SetPendingModel(_contentPath, parts[1], gamePath);
        }
    }

    private bool SameModelFamily(string variantTag, string family)
    {
        var other = ModelCatalog.FamilyDirForVariant(variantTag);
        return other != null && string.Equals(other, family, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The focus flag lives on the family's shared ArchetypeDB, so every cached member must carry the
    /// same value - otherwise the change review and export would see stale copies.
    /// </summary>
    private void PropagateFocusToFamily(string variantTag, bool immune)
    {
        var family = UnitPropertiesManager.GetFocusFamilyKey(_contentPath, variantTag);
        if (family == null) return;

        foreach (var kvp in _propsByVariant)
        {
            if (SameFocusFamily(kvp.Key, family))
                kvp.Value.ImmuneToFocus = immune;
        }

        foreach (var kvp in _unitCaches)
        {
            var parts = kvp.Key.Split('|');
            if (parts.Length < 2 || !SameFocusFamily(parts[1], family)) continue;

            var cached = kvp.Value.Props;
            if (cached == null)
                kvp.Value.Props = cached = new UnitProperties();
            cached.ImmuneToFocus = immune;
        }
    }

    private bool SameFocusFamily(string variantTag, string family)
    {
        var other = UnitPropertiesManager.GetFocusFamilyKey(_contentPath, variantTag);
        return other != null && string.Equals(other, family, StringComparison.OrdinalIgnoreCase);
    }

    private void SetUnitPropsUI(UnitProperties props)
    {
        SetField(txtUnitHealth, props.Health?.ToString("F1"), props.Health.HasValue);
        SetField(txtUnitStructure, props.Structure?.ToString("F1"), props.Structure.HasValue);
        SetField(txtMemoryLimit, props.MemoryLimit?.ToString("F1"), props.MemoryLimit.HasValue);
        SetField(txtHitsCount, props.HitsCount?.ToString(), props.HitsCount.HasValue);
        SetField(txtFlushLimit, props.MemoryFlushLimit?.ToString("F1"), props.MemoryFlushLimit.HasValue);

        string arch = _activeStance?.Split('|')[0] ?? "";
        string variantTag = _activeVariant ?? $"{arch}_Base";
        chkImmuneToFocus.IsChecked = props.ImmuneToFocus
            ?? UnitPropertiesManager.ReadFocusImmunity(_contentPath, variantTag);
        _selectedModelOverride = props.ModelOverride;
        _selectedModelSet = props.ModelSet;
        UpdateModelOverrideLabel();
        chkPhaseSwap.IsChecked = props.PhaseSwap ?? false;
        SelectPhaseTarget(props.PhaseSwapTarget);
        cmbPhaseSwapTarget.IsEnabled = chkPhaseSwap.IsChecked == true;
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

    private List<MeshCatalogEntry> _modelCatalogEntries = new();
    private string? _modelFilter;
    private readonly Dictionary<string, List<Border>> _modelThumbCards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _thumbQueue = new();
    private readonly HashSet<string> _thumbQueued = new(StringComparer.OrdinalIgnoreCase);
    private bool _thumbPumpRunning;
    private bool _thumbReady;
    private bool _thumbUnavailable;
    private Task? _thumbInitTask;
    private TaskCompletionSource<bool>? _thumbReadyTcs;
    private int _thumbRenderSeq;
    private readonly Dictionary<int, TaskCompletionSource<string?>> _thumbRenderers = new();
    private readonly Dictionary<string, BitmapImage> _thumbBitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BitmapImage> _thumbBigBitmapCache = new(StringComparer.OrdinalIgnoreCase);

    private static string ThumbCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SifuMovesetEditor", "Thumbnails");

    private static string ThumbCachePath(string gamePath, int size = 128)
    {
        // The 128px seed stays byte-identical to the original so existing caches remain valid.
        string seed = size == 128
            ? "v3|" + gamePath.ToLowerInvariant()
            : "v3|" + size + "|" + gamePath.ToLowerInvariant();
        using var sha = System.Security.Cryptography.SHA1.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed));
        var hex = string.Concat(hash.Select(b => b.ToString("x2")));
        return Path.Combine(ThumbCacheDir, hex + ".png");
    }

    private static void MapTextureHost(CoreWebView2 core)
    {
        try
        {
            core.SetVirtualHostNameToFolderMapping(
                MeshTextureResolver.TextureHost, ThumbCacheDir,
                CoreWebView2HostResourceAccessKind.Allow);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("TEXHOST", ex);
        }
    }

    private void BtnChooseModel_Click(object sender, RoutedEventArgs e)
    {
        if (!UnitPropertiesManager.EnableModelFeature) return;
        _ = OpenModelPickerAsync();
    }

    private async void BtnImportModel_Click(object sender, RoutedEventArgs e)
    {
        if (!UnitPropertiesManager.EnableModelFeature) return;
        if (string.IsNullOrEmpty(_contentPath)) return;
        if (_parser == null || !_parser.IsLoaded)
        {
            MessageBox.Show("The model preview is still initializing - try again in a moment.",
                "Import custom model", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import custom model",
            Filter = "Sifu skeletal mesh (*.uasset)|*.uasset"
        };
        if (dlg.ShowDialog(this) != true) return;

        string srcUasset = dlg.FileName;
        var error = ModelCatalog.ValidateCustomMesh(srcUasset);
        if (error != null)
        {
            MessageBox.Show(error, "Import custom model", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string name = Path.GetFileNameWithoutExtension(srcUasset);
        string rel = $"Characters/Custom/{name}";
        string dstDir = Path.Combine(CustomAssets.Root, "Characters", "Custom");
        string dstUasset = Path.Combine(dstDir, name + ".uasset");
        var extras = new List<string>();
        foreach (var ext in new[] { ".uexp", ".ubulk" })
        {
            var sibling = Path.ChangeExtension(srcUasset, ext);
            if (File.Exists(sibling)) extras.Add(sibling);
        }

        try
        {
            _suppressCustomWatcher = true;
            Directory.CreateDirectory(dstDir);
            File.Copy(srcUasset, dstUasset, true);
            foreach (var sibling in extras)
                File.Copy(sibling, Path.Combine(dstDir, Path.GetFileName(sibling)), true);
        }
        catch (Exception ex)
        {
            _suppressCustomWatcher = false;
            MessageBox.Show($"Could not copy the mesh into CustomAssets:\n{ex.Message}",
                "Import custom model", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            _suppressCustomWatcher = false;
        }

        _parser?.MountCustomIntoProvider(CustomAssets.Root);

        bool loaded = false;
        try
        {
            var mesh = await Task.Run(() => _parser?.LoadMeshFromPath(("/Game/" + rel).TrimStart('/'), includeMaterials: false));
            loaded = mesh != null && mesh.positions.Length > 0 && mesh.uvs.Length > 0 && mesh.indices.Length > 0;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("IMPORT", ex);
        }

        if (!loaded)
        {
            try
            {
                _suppressCustomWatcher = true;
                foreach (var f in Directory.GetFiles(dstDir, name + ".*"))
                    File.Delete(f);
            }
            catch (Exception ex) { ErrorLog.Write("IMPORT", ex); }
            finally { _suppressCustomWatcher = false; }

            MessageBox.Show(
                "The file passed the skeleton check but failed to load as a skeletal mesh - " +
                "it was not imported. The asset may be corrupted or built for a different engine version.",
                "Import custom model", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ModelCatalog.InvalidateMeshCache();
        _selectedModelOverride = "/Game/" + rel;
        _selectedModelSet = null;
        UpdateModelOverrideLabel();

        if (modelPopupBorder.Visibility == Visibility.Visible)
        {
            _modelCatalogEntries = ModelCatalog.ScanMeshes(_contentPath);
            if (!_modelPickerRosterMode)
                RefreshModelPickerContent();
        }

        modelPopupBorder.Visibility = Visibility.Collapsed;
        txtStatus.Text = $"Imported custom model {name} (Custom group)";
    }

    private void ModelPopupBackground_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == modelPopupBorder)
            modelPopupBorder.Visibility = Visibility.Collapsed;
    }

    private async Task OpenModelPickerAsync()
    {
        if (!UnitPropertiesManager.EnableModelFeature) return;
        if (string.IsNullOrEmpty(_contentPath)) return;

        _modelPickerRosterMode = !IsMainCharStance() && !string.IsNullOrEmpty(_selectedModelSet);

        modelWrapPanel.Children.Clear();
        modelFilterWrap.Children.Clear();
        modelWrapPanel.Children.Add(new TextBlock
        {
            Text = "Scanning meshes...",
            Foreground = new SolidColorBrush(Color.FromRgb(0x6c, 0x70, 0x86)),
            FontSize = 12,
            Margin = new Thickness(4)
        });
        PopulateModelModeChips();
        ApplyModelPickerHint();
        modelPopupBorder.Visibility = Visibility.Visible;

        var entries = await Task.Run(() => ModelCatalog.ScanMeshes(_contentPath));
        await EnsureThumbWebViewAsync();

        _modelCatalogEntries = entries;
        RefreshModelPickerContent();
        if (_thumbQueue.Count > 0) _ = ThumbPumpAsync();
    }

    private void ApplyModelPickerHint()
    {
        string variantTag = CurrentVariantTag();
        var familyDir = ModelCatalog.FamilyDirForVariant(variantTag);

        modelPopupTitle.Text = (_modelPickerRosterMode ? "Model Set \u2014 " : "Select Model \u2014 ") + variantTag;
        if (modelPopupHint == null) return;

        if (_modelPickerRosterMode)
        {
            modelPopupHint.Text = familyDir != null
                ? $"Family-wide: every {familyDir} spawn gets the source family's roster, with per-spawn variety kept."
                : "Family-wide roster swap.";
        }
        else
        {
            modelPopupHint.Text = familyDir != null
                ? $"Applies to {variantTag} only \u2014 other {familyDir} variants keep their in-game model."
                : $"Applies to {variantTag} only.";
        }
    }

    private void PopulateModelModeChips()
    {
        modelModeWrap.Children.Clear();
        // The player has no archetype family - only the specific-model mode applies.
        if (!IsMainCharStance())
            AddModelModeChip("Roster swap (family)", true);
        AddModelModeChip("Specific model (variant)", false);
    }

    private void AddModelModeChip(string label, bool rosterMode)
    {
        bool active = _modelPickerRosterMode == rosterMode;

        var chip = new Border
        {
            Cursor = Cursors.Hand,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(active ? Color.FromRgb(0x45, 0x47, 0x5a) : Color.FromRgb(0x31, 0x32, 0x44)),
            BorderBrush = new SolidColorBrush(active ? Color.FromRgb(0xa6, 0xe3, 0xa1) : Color.FromRgb(0x58, 0x5b, 0x70)),
            BorderThickness = new Thickness(1),
            Tag = rosterMode
        };
        chip.Child = new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(active ? Color.FromRgb(0xa6, 0xe3, 0xa1) : Color.FromRgb(0xcd, 0xd6, 0xf4))
        };
        chip.MouseLeftButtonDown += (s, ev) =>
        {
            _modelPickerRosterMode = (bool)((Border)s).Tag!;
            PopulateModelModeChips();
            ApplyModelPickerHint();
            RefreshModelPickerContent();
        };
        modelModeWrap.Children.Add(chip);
    }

    private void RefreshModelPickerContent()
    {
        modelWrapPanel.Children.Clear();
        modelFilterWrap.Children.Clear();
        _modelThumbCards.Clear();
        _thumbQueued.Clear();
        if (!UnitPropertiesManager.EnableModelFeature) return;

        if (_modelPickerRosterMode)
        {
            modelFilterWrap.Visibility = Visibility.Collapsed;
            btnImportModel.Visibility = Visibility.Collapsed;

            AddRosterCard(null, "(default)", 0);

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in ModelCatalog.ModelSetKeys)
            {
                counts[key] = ModelCatalog.BuildRoster(_contentPath, key).Count;
                AddRosterCard(key, key, counts[key]);
            }
            return;
        }

        modelFilterWrap.Visibility = Visibility.Visible;
        btnImportModel.Visibility = Visibility.Visible;
        PopulateModelFilterChips();
        PopulateModelCards();
    }

    private void PopulateModelFilterChips()
    {
        modelFilterWrap.Children.Clear();
        AddModelFilterChip("All", null);
        foreach (var group in _modelCatalogEntries
                     .Select(e => e.Group)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
            AddModelFilterChip(group, group);
    }

    private void AddModelFilterChip(string label, string? group)
    {
        bool active = group == null
            ? _modelFilter == null
            : _modelFilter != null && _modelFilter.Equals(group, StringComparison.OrdinalIgnoreCase);

        var chip = new Border
        {
            Cursor = Cursors.Hand,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(active ? Color.FromRgb(0x45, 0x47, 0x5a) : Color.FromRgb(0x31, 0x32, 0x44)),
            BorderBrush = new SolidColorBrush(active ? Color.FromRgb(0x89, 0xb4, 0xfa) : Color.FromRgb(0x58, 0x5b, 0x70)),
            BorderThickness = new Thickness(1),
            Tag = group
        };
        chip.Child = new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(active ? Color.FromRgb(0x89, 0xb4, 0xfa) : Color.FromRgb(0xcd, 0xd6, 0xf4))
        };
        chip.MouseLeftButtonDown += (s, ev) =>
        {
            _modelFilter = (string?)((Border)s).Tag;
            PopulateModelFilterChips();
            PopulateModelCards();
            if (_thumbQueue.Count > 0) _ = ThumbPumpAsync();
        };
        modelFilterWrap.Children.Add(chip);
    }

    private void PopulateModelCards()
    {
        modelWrapPanel.Children.Clear();
        _modelThumbCards.Clear();
        _thumbQueued.Clear();

        AddModelCard(null, "(default)");

        foreach (var entry in _modelCatalogEntries)
        {
            if (_modelFilter != null && !entry.Group.Equals(_modelFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            AddModelCard(entry.GamePath, entry.DisplayName);
        }
    }

    private void AddModelCard(string? gamePath, string displayName)
    {
        bool selected = string.Equals(gamePath, _selectedModelOverride, StringComparison.OrdinalIgnoreCase);

        var card = new Border
        {
            Width = 90, Height = 130,
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e)),
            BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(0x89, 0xb4, 0xfa) : Color.FromRgb(0x58, 0x5b, 0x70)),
            BorderThickness = new Thickness(selected ? 2 : 1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(6),
            Cursor = Cursors.Hand,
            Tag = gamePath
        };

        var stack = new StackPanel();
        var imgBorder = new Border
        {
            Height = 90,
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Background = new SolidColorBrush(gamePath == null
                ? Color.FromRgb(0x45, 0x47, 0x5a)
                : Color.FromRgb(0x31, 0x32, 0x44)),
            ClipToBounds = true
        };
        var tile = new TextBlock
        {
            Text = gamePath == null
                ? "\u25c7"
                : displayName.Substring(0, Math.Min(2, displayName.Length)).ToUpper(),
            Foreground = Brushes.White,
            FontSize = gamePath == null ? 30 : 28,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 0)
        };

        if (gamePath == null)
        {
            imgBorder.Child = tile;
        }
        else
        {
            // Tile + eye overlay: the thumb swap replaces grid child 0, keeping the button.
            var grid = new Grid();
            grid.Children.Add(tile);

            var eye = new Button
            {
                Content = "\ue942",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Width = 16,
                Height = 16,
                Padding = new Thickness(0),
                Margin = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromArgb(0xAA, 0x00, 0x00, 0x00)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x77, 0xff, 0xff, 0xff)),
                BorderThickness = new Thickness(1)
            };
            string eyePath = gamePath;
            string eyeName = displayName;
            eye.MouseLeftButtonDown += (s, ev) => ev.Handled = true;
            eye.Click += async (s, ev) => await ShowBigModelPreviewAsync(eyePath, eyeName);
            grid.Children.Add(eye);
            imgBorder.Child = grid;
        }

        var nameBlock = new Border
        {
            Height = 40,
            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            CornerRadius = new CornerRadius(0, 0, 6, 6),
            Padding = new Thickness(4)
        };
        nameBlock.Child = new TextBlock
        {
            Text = displayName,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcd, 0xd6, 0xf4)),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        stack.Children.Add(imgBorder);
        stack.Children.Add(nameBlock);
        card.Child = stack;

        card.MouseLeftButtonDown += (s, ev) =>
        {
            _selectedModelOverride = (string?)((Border)s).Tag;
            _selectedModelSet = null;
            UpdateModelOverrideLabel();
            modelPopupBorder.Visibility = Visibility.Collapsed;
        };

        modelWrapPanel.Children.Add(card);

        if (gamePath != null)
        {
            if (!_modelThumbCards.TryGetValue(gamePath, out var borders))
                _modelThumbCards[gamePath] = borders = new List<Border>();
            borders.Add(imgBorder);

            if (_thumbBitmapCache.TryGetValue(gamePath, out var cachedBitmap))
            {
                SetThumbContent(imgBorder, new Image
                {
                    Source = cachedBitmap,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(2)
                });
            }
            else if (_thumbQueued.Add(gamePath))
                _thumbQueue.Enqueue(gamePath);
        }
    }

    /// <summary>Swaps in the thumbnail while keeping the eye overlay (cards wrap tile + button in a Grid).</summary>
    private static void SetThumbContent(Border imgBorder, Image image)
    {
        if (imgBorder.Child is Grid grid && grid.Children.Count > 0)
        {
            // WPF's UIElementCollection indexer throws "Specified index is already in use"
            // on assignment - the old child has to be disconnected before the new one goes in.
            grid.Children.RemoveAt(0);
            grid.Children.Insert(0, image);
        }
        else
            imgBorder.Child = image;
    }

    private async Task ShowBigModelPreviewAsync(string gamePath, string displayName)
    {
        bigModelTitle.Text = displayName;
        bigModelImage.Source = null;
        bigModelPlaceholder.Text = "Rendering\u2026";
        bigModelPlaceholder.Visibility = Visibility.Visible;
        bigModelPopupBorder.Visibility = Visibility.Visible;

        try
        {
            BitmapImage? bitmap = null;
            if (_thumbBigBitmapCache.TryGetValue(gamePath, out var cached))
                bitmap = cached;

            if (bitmap == null)
            {
                var bytes = await GetOrCreateThumbAsync(gamePath, 512);
                if (bytes == null)
                {
                    bigModelPlaceholder.Text = "Preview unavailable";
                    return;
                }
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(bytes);
                bitmap.EndInit();
                bitmap.Freeze();
                _thumbBigBitmapCache[gamePath] = bitmap;
            }

            bigModelImage.Source = bitmap;
            bigModelPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("THUMB", ex);
            bigModelPlaceholder.Text = "Preview unavailable";
        }
    }

    private void BigModelPopupBackground_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == bigModelPopupBorder)
            bigModelPopupBorder.Visibility = Visibility.Collapsed;
    }

    private void AddRosterCard(string? sourceKey, string displayName, int lookCount)
    {
        bool selected = string.Equals(sourceKey, _selectedModelSet, StringComparison.OrdinalIgnoreCase);

        var card = new Border
        {
            Width = 90, Height = 130,
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e)),
            BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(0xa6, 0xe3, 0xa1) : Color.FromRgb(0x58, 0x5b, 0x70)),
            BorderThickness = new Thickness(selected ? 2 : 1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(6),
            Cursor = Cursors.Hand,
            Tag = sourceKey
        };

        var stack = new StackPanel();
        var imgBorder = new Border
        {
            Height = 90,
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Background = new SolidColorBrush(sourceKey == null
                ? Color.FromRgb(0x45, 0x47, 0x5a)
                : Color.FromRgb(0x31, 0x32, 0x44)),
            ClipToBounds = true
        };
        imgBorder.Child = new TextBlock
        {
            Text = sourceKey == null ? "\u25c7" : displayName.Substring(0, Math.Min(2, displayName.Length)).ToUpper(),
            Foreground = Brushes.White,
            FontSize = sourceKey == null ? 30 : 26,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 0)
        };

        var nameBlock = new Border
        {
            Height = 40,
            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            CornerRadius = new CornerRadius(0, 0, 6, 6),
            Padding = new Thickness(4)
        };
        nameBlock.Child = new TextBlock
        {
            Text = sourceKey == null ? displayName : $"{displayName}\n{lookCount} looks",
            Foreground = new SolidColorBrush(Color.FromRgb(0xcd, 0xd6, 0xf4)),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        stack.Children.Add(imgBorder);
        stack.Children.Add(nameBlock);
        card.Child = stack;

        card.MouseLeftButtonDown += (s, ev) =>
        {
            _selectedModelSet = (string?)((Border)s).Tag;
            if (_selectedModelSet != null)
                _selectedModelOverride = null;
            UpdateModelOverrideLabel();
            modelPopupBorder.Visibility = Visibility.Collapsed;
        };

        modelWrapPanel.Children.Add(card);
    }

    private async Task ThumbPumpAsync()
    {
        if (_thumbPumpRunning) return;
        _thumbPumpRunning = true;
        try
        {
            while (_thumbQueue.Count > 0)
            {
                var gamePath = _thumbQueue.Dequeue();
                _thumbQueued.Remove(gamePath);
                try
                {
                    var bytes = await GetOrCreateThumbAsync(gamePath);
                    if (bytes != null) ApplyThumbToCards(gamePath, bytes);
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("THUMB", ex);
                }
            }
        }
        finally
        {
            _thumbPumpRunning = false;
        }
    }

    private async Task<byte[]?> GetOrCreateThumbAsync(string gamePath, int size = 128)
    {
        var cachePath = ThumbCachePath(gamePath, size);
        if (File.Exists(cachePath))
            return await Task.Run(() => File.ReadAllBytes(cachePath));

        if (!_thumbReady || _parser == null || !_parser.IsLoaded) return null;

        var mesh = await Task.Run(() => _parser.LoadMeshFromPath(gamePath.TrimStart('/'), includeMaterials: true));
        if (mesh == null) return null;

        var payload = JsonConvert.SerializeObject(new
        {
            positions = mesh.positions,
            normals = mesh.normals,
            uvs = mesh.uvs,
            indices = mesh.indices,
            materials = mesh.materials
        });

        int id = ++_thumbRenderSeq;
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thumbRenderers[id] = tcs;
        try
        {
            await thumbWebView.CoreWebView2!.ExecuteScriptAsync($"window.renderThumb({id}, {payload}, {size})");
            var dataUrl = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (dataUrl == null) return null;

            var comma = dataUrl.IndexOf(',');
            if (comma < 0) return null;
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            try
            {
                Directory.CreateDirectory(ThumbCacheDir);
                File.WriteAllBytes(cachePath, bytes);
            }
            catch { }
            return bytes;
        }
        finally
        {
            _thumbRenderers.Remove(id);
        }
    }

    private void ApplyThumbToCards(string gamePath, byte[] bytes)
    {
        if (!_modelThumbCards.TryGetValue(gamePath, out var borders) || borders.Count == 0) return;

        if (!_thumbBitmapCache.TryGetValue(gamePath, out var bitmap))
        {
            try
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(bytes);
                bitmap.EndInit();
                bitmap.Freeze();
            }
            catch { return; }
            _thumbBitmapCache[gamePath] = bitmap;
        }

        foreach (var border in borders)
        {
            SetThumbContent(border, new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(2)
            });
        }
    }

    private Task EnsureThumbWebViewAsync()
    {
        if (_thumbUnavailable || _thumbReady) return Task.CompletedTask;
        _thumbInitTask ??= InitThumbWebViewAsync();
        return _thumbInitTask;
    }

    private async Task InitThumbWebViewAsync()
    {
        try
        {
            await thumbWebView.EnsureCoreWebView2Async(null);
            thumbWebView.CoreWebView2.WebMessageReceived += OnThumbWebViewMessage;
            MapTextureHost(thumbWebView.CoreWebView2);
            _thumbReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thumbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "viewer", "thumb.html");
            thumbWebView.CoreWebView2.Navigate(new Uri(thumbPath).AbsoluteUri);
            _thumbReady = await _thumbReadyTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("THUMB", ex);
            _thumbReady = false;
            _thumbUnavailable = true;
        }
    }

    private void OnThumbWebViewMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var root = JObject.Parse(e.WebMessageAsJson);
            var type = root.Value<string>("type");
            if (type == "thumbReady")
            {
                _thumbReadyTcs?.TrySetResult(true);
            }
            else if (type == "thumb")
            {
                int id = root.Value<int>("id");
                if (_thumbRenderers.TryGetValue(id, out var tcs))
                {
                    var error = root.Value<string>("error");
                    tcs.TrySetResult(error == null ? root.Value<string>("data") : null);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("THUMB", ex);
        }
    }
    #endregion
}

public class Settings
{
    public string ContentPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public bool ShowLines { get; set; } = true;
    // Beginner view: hides AI action/event nodes, the AI Actions & Events library and
    // AI-target rows in the Start Entries popup. Default false so old settings.json
    // files (key absent) deserializing to the C# default also get the clean graph.
    public bool ShowAiNodes { get; set; } = false;
    public double[]? CameraPosition { get; set; }
    public double[]? CameraTarget { get; set; }
    public string UnrealPakPath { get; set; } = "";
    public string CryptoJsonPath { get; set; } = "";
    // CustomAssets files ("rel|utcTicks") accepted at the close prompt - re-asking
    // only happens when the folder's manifest drifts from this list.
    public List<string>? CustomAssetsAccepted { get; set; }
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
