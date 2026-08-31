using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using SifuMovesetEditor;
using SifuMovesetEditor.Setup;
using SifuMovesetEditor.Export;

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

    private void SaveSettings()
    {
        var settings = new Settings { ContentPath = _contentPath, OutputPath = _outputPath };
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

            UpdateLoading("Scanning enemy attack DBs...", "Walking DB/AI/Archetypes/...");
            var enemyMoves = await Task.Run(() => EnemyAttackScanner.ScanEnemyAttacks(contentPath));
            foreach (var move in enemyMoves)
                move.IsUsed = true;
            _allMoves.AddRange(enemyMoves);

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

        var vanillaMoves = filtered.Where(m => m.IsUsed && !string.IsNullOrWhiteSpace(m.DisplayName)).ToList();
        var unusedMoves = filtered.Where(m => !m.IsUsed && !string.IsNullOrWhiteSpace(m.DisplayName)).ToList();

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

    private async void MoveCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is MoveInfo move)
        {
            txtStatus.Text = $"Loading: {move.DisplayName}...";

            if (chkShowMesh.IsChecked == true && !string.IsNullOrEmpty(move.Character))
                await LoadMeshAsync(move.Character);

            await LoadAnimationAsync(move.FullPath);
        }
    }

    private void MoveCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            sender is Border border && border.Tag is MoveInfo move)
        {
            var data = new DataObject(typeof(MoveInfo), move);
            DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
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

            txtStatus.Text = $"Loaded: {data.animation.name} ({data.animation.numFrames} frames, {data.animation.tracks.Length} tracks)";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ANIMATION ERROR] {ex}");
            ErrorLog.Write("ANIMATION", ex);
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

        var modified = _comboGraph.Nodes
            .Where(n => !n.IsRoot && !string.IsNullOrEmpty(n.AnimPath) && n.AnimPath != n.DefaultAnimPath)
            .ToList();

        if (modified.Count == 0)
        {
            txtStatus.Text = "No changes to export. Drag animations onto combo nodes first.";
            return;
        }

        var dialog = new ExportDialog(
            modified,
            _contentPath,
            _outputPath,
            _parser.AnimToDbPath)
        {
            Owner = this,
        };

        dialog.ShowDialog();
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

            txtComboInfo.Text = $"MainChar - {_comboGraph.WeaponName} ({_comboGraph.Nodes.Count} nodes, {_comboGraph.Edges.Count} edges)";
            _comboTranslate.X = 0;
            _comboTranslate.Y = 0;
            LayoutComboGraph();
            RenderComboGraph();
            btnExport.IsEnabled = true;
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

            var nodeColor = node.IsRoot
                ? new SolidColorBrush(Color.FromRgb(0xf9, 0xe2, 0xaf))
                : new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa));

            var border = new Border
            {
                Width = NODE_WIDTH,
                Height = NODE_HEIGHT,
                BorderBrush = nodeColor,
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0x89, 0xb4, 0xfa)),
                CornerRadius = new CornerRadius(4),
                Tag = node
            };
            border.MouseLeftButtonDown += ComboNode_Click;
            border.AllowDrop = true;
            border.DragEnter += ComboNode_DragEnter;
            border.DragLeave += ComboNode_DragLeave;
            border.Drop += ComboNode_Drop;
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

    private async void ComboNode_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is ComboNode node)
        {
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xff, 0xff, 0xff));
            border.Background = new SolidColorBrush(Color.FromArgb(0x80, 0xa6, 0xe3, 0xa1));
            var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            resetTimer.Tick += (_, _) =>
            {
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa));
                border.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x89, 0xb4, 0xfa));
                resetTimer.Stop();
            };
            resetTimer.Start();

            if (!string.IsNullOrEmpty(node.AnimPath))
            {
                txtStatus.Text = $"Loading: {node.DisplayName} ({node.AnimPath})...";
                try
                {
                    if (chkShowMesh.IsChecked == true)
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
            var nodeColor = node.IsRoot
                ? new SolidColorBrush(Color.FromRgb(0xf9, 0xe2, 0xaf))
                : new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa));
            border.BorderBrush = nodeColor;
            border.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x89, 0xb4, 0xfa));
            e.Handled = true;
        }
    }

    private void ComboNode_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border border && border.Tag is ComboNode node)
        {
            var nodeColor = node.IsRoot
                ? new SolidColorBrush(Color.FromRgb(0xf9, 0xe2, 0xaf))
                : new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa));
            border.BorderBrush = nodeColor;
            border.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x89, 0xb4, 0xfa));

            if (e.Data.GetDataPresent(typeof(MoveInfo)))
            {
                var move = e.Data.GetData(typeof(MoveInfo)) as MoveInfo;
                if (move != null)
                {
                    node.AnimPath = move.FullPath;
                    node.DisplayName = move.DisplayName;
                    RenderComboGraph();
                    txtStatus.Text = $"Replaced animation: {node.Name} -> {move.DisplayName}";
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
