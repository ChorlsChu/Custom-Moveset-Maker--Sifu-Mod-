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

using System.Text;
using System.Windows.Input;
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

    public MainWindow()
    {
        InitializeComponent();
        _settingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "settings.json");
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

        LoadSettings();
        cmbSpeed.SelectedIndex = 2;
        _initialized = true;

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += MainWindow_PreviewKeyUp;

        _panTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _panTimer.Tick += PanTimer_Tick;
        _panTimer.Start();
    }

    private Settings LoadSettings()
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
                    InitializeParser();
                    return settings;
                }
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Error loading settings: {ex.Message}";
            }
        }
        return new Settings { ContentPath = _contentPath };
    }

    private void SaveSettings()
    {
        var settings = new Settings { ContentPath = _contentPath, OutputPath = _outputPath };
        File.WriteAllText(_settingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
    }

    private void InitializeParser()
    {
        try
        {
            var contentPath = Path.Combine(_contentPath, "Content");
            txtStatus.Text = $"Loading from: {_contentPath}...";
            _parser.Initialize(_contentPath, contentPath);

            _allMoves = _parser.ScanAnimations();
            txtStatus.Text = $"Scanning attack data tables...";

            var usedPaths = _parser.ScanUsedAnimations();
            foreach (var move in _allMoves)
            {
                move.IsUsed = usedPaths.Contains(move.FullPath);
            }

            var matchCount = _allMoves.Count(m => m.IsUsed);
            var sampleScanned = string.Join(", ", _allMoves.Take(5).Select(m => m.FullPath));
            var sampleUsed = string.Join(", ", usedPaths.Take(5));
            ErrorLog.Write("MATCH", new Exception($"Matched {matchCount}/{_allMoves.Count} as vanilla\nSample scanned: [{sampleScanned}]\nSample used:    [{sampleUsed}]"));

            _allLocomotion = _parser.ScanStanceAnims();
            _parser.BuildAnimToDbMapping();

            BuildTree(_allMoves);
            txtStatus.Text = $"Loaded {_allMoves.Count} animations from {_contentPath}";

            PopulateCharacterDropdown();
            LoadComboGraph();
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Error: {ex.Message}";
            MessageBox.Show($"Failed to initialize parser:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
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
            InitializeParser();
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        FilterMoves();
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
                7 => m.Character == "Servant",
                8 => m.Character is "Fajar" or "Sean" or "Kuroki" or "Yang" or "Fengjie",
                _ => true
            };

            return matchesSearch && matchesFilter;
        });

        var vanillaMoves = filtered.Where(m => m.IsUsed).ToList();
        var unusedMoves = filtered.Where(m => !m.IsUsed).ToList();

        listVanilla.ItemsSource = vanillaMoves;
        listUnused.ItemsSource = unusedMoves;

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

    private void BuildTree(List<MoveInfo> moves)
    {
        var vanillaMoves = moves.Where(m => m.IsUsed).ToList();
        var unusedMoves = moves.Where(m => !m.IsUsed).ToList();

        listVanilla.ItemsSource = vanillaMoves;
        listUnused.ItemsSource = unusedMoves;
        listLoco.ItemsSource = _allLocomotion;

        tabHeaderVanilla.Text = $"Vanilla ({vanillaMoves.Count})";
        tabHeaderUnused.Text = $"Unused ({unusedMoves.Count})";
        tabHeaderLoco.Text = $"Locomotion ({_allLocomotion.Count})";
        txtMoveCount.Text = $"{vanillaMoves.Count} vanilla / {unusedMoves.Count} unused / {_allLocomotion.Count} locomotion";
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
        if (webView.CoreWebView2 == null) return;
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
            MessageBox.Show("No changes to export. Drag animations onto combo nodes first.", "Export Pak", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var changeList = string.Join("\n\n", modified.Select(n =>
            $"  {n.DisplayName}\n    {n.DefaultAnimPath}\n    → {n.AnimPath}"));

        var result = MessageBox.Show(
            $"Export Combo Mod — {modified.Count} change(s) found:\n\n{changeList}\n\nProceed with export?",
            "Export Pak",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.OK) return;

        try
        {
            txtStatus.Text = "Exporting pak...";
            btnExport.IsEnabled = false;

            var contentPath = _contentPath;
            if (string.IsNullOrEmpty(contentPath) || !Directory.Exists(contentPath))
            {
                MessageBox.Show("Content path not set or invalid. Check Settings.", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                btnExport.IsEnabled = true;
                return;
            }

            var outputPath = _outputPath;
            if (string.IsNullOrEmpty(outputPath))
                outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExportedMods");
            Directory.CreateDirectory(outputPath);

            var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp_mod");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);

            var gameRoot = Path.Combine(contentPath, "Content");
            if (!Directory.Exists(gameRoot))
            {
                MessageBox.Show($"Game content not found at: {gameRoot}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                btnExport.IsEnabled = true;
                return;
            }

            var fileEntries = new List<(string src, string dest)>();

            var comboTreeSrc = Path.Combine(gameRoot, "DB", "_MainChar", "Combos", "MainChar_ComboTree.uasset");
            var comboTreeUexp = Path.Combine(gameRoot, "DB", "_MainChar", "Combos", "MainChar_ComboTree.uexp");
            var comboTreeTempDir = Path.Combine(tempDir, "Sifu", "Content", "DB", "_MainChar", "Combos");
            Directory.CreateDirectory(comboTreeTempDir);
            var comboTreeTemp = Path.Combine(comboTreeTempDir, "MainChar_ComboTree.uasset");
            File.Copy(comboTreeSrc, comboTreeTemp, true);
            File.Copy(comboTreeUexp, Path.Combine(comboTreeTempDir, "MainChar_ComboTree.uexp"), true);

            fileEntries.Add((comboTreeTemp, "../../../Sifu/Content/DB/_MainChar/Combos/MainChar_ComboTree.uasset"));
            fileEntries.Add((Path.Combine(comboTreeTempDir, "MainChar_ComboTree.uexp"), "../../../Sifu/Content/DB/_MainChar/Combos/MainChar_ComboTree.uexp"));

            txtStatus.Text = "Patching combo tree (raw binary patch)...";
            ErrorLog.Write("EXPORT", new Exception($"=== EXPORT START: {modified.Count} modified nodes ==="));

            var swaps = new Dictionary<string, string>();
            foreach (var node in modified)
            {
                if (string.IsNullOrEmpty(node.DefaultDBPath)) continue;
                if (!_parser.AnimToDbPath.TryGetValue(node.AnimPath, out var newDbPath)) continue;

                var oldDb = AnimationParser.NormalizeAnimPath(node.DefaultDBPath);
                var newDb = AnimationParser.NormalizeAnimPath(newDbPath);

                if (string.IsNullOrEmpty(oldDb) || string.IsNullOrEmpty(newDb)) continue;
                if (oldDb == newDb) continue;

                var oldDbWithSlash = "/" + oldDb;
                var newDbWithSlash = "/" + newDb;

                if (!swaps.ContainsKey(oldDb))
                {
                    swaps[oldDb] = newDbWithSlash;
                    ErrorLog.Write("EXPORT", new Exception($"  SWAP: {oldDbWithSlash} -> {newDbWithSlash}"));
                }
            }

            if (swaps.Count == 0)
            {
                ErrorLog.Write("EXPORT", new Exception("No valid swaps found — nothing to patch"));
                MessageBox.Show("No valid DB swaps found for modified nodes.", "Export Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                btnExport.IsEnabled = true;
                return;
            }

            var uexpPath = Path.ChangeExtension(comboTreeTemp, ".uexp");
            PatchComboTreeRawBinary(comboTreeTemp, uexpPath, swaps);

            txtStatus.Text = $"Building pak file ({fileEntries.Count} files)...";

            var filelistPath = Path.Combine(tempDir, "filelist.txt");
            var filelistContent = string.Join("\n", fileEntries.Select(f => $"\"{f.src}\" \"{f.dest}\""));
            File.WriteAllText(filelistPath, filelistContent);

            var pakExe = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\4.26\UE4\UnrealPak\UnrealPak.exe";
            var pakFileName = "MainCharComboMod.pak";
            var pakPath = Path.Combine(outputPath, pakFileName);

            if (!File.Exists(pakExe))
            {
                MessageBox.Show($"UnrealPak not found at:\n{pakExe}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                btnExport.IsEnabled = true;
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = pakExe,
                Arguments = $"\"{pakPath}\" -create=\"{filelistPath}\" -compress",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                MessageBox.Show($"UnrealPak failed (exit {proc.ExitCode}):\n\n{stderr}\n{stdout}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                btnExport.IsEnabled = true;
                return;
            }

            var sigSrc = Path.Combine(Path.GetDirectoryName(pakExe)!, "pakchunk0-WindowsNoEditor.sig");
            var sigDest = Path.ChangeExtension(pakPath, ".sig");
            if (File.Exists(sigSrc)) File.Copy(sigSrc, sigDest, true);

            Directory.Delete(tempDir, true);

            var pakSize = new FileInfo(pakPath).Length;
            var sizeStr = pakSize > 1024 * 1024
                ? $"{pakSize / (1024.0 * 1024.0):F1} MB"
                : $"{pakSize / 1024.0:F1} KB";

            MessageBox.Show(
                $"Mod exported successfully!\n\n{pakFileName} ({modified.Count} changes, {sizeStr})\nSaved to: {outputPath}\n\nCopy to your Sifu/Content/Paks/~mods/ folder to use.",
                "Export Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            txtStatus.Text = $"Exported: {pakFileName} ({sizeStr})";
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", ex);
            MessageBox.Show($"Export failed:\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            txtStatus.Text = "Export failed — see error.log";
        }
        finally
        {
            btnExport.IsEnabled = true;
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

    private static void PatchInt32InRange(byte[] bytes, int start, int end, int oldValue, int newValue)
    {
        if (oldValue == newValue) return;
        byte[] oldBytes = BitConverter.GetBytes(oldValue);
        byte[] newBytes = BitConverter.GetBytes(newValue);
        for (int i = start; i <= end - 4; i++)
        {
            if (bytes[i] == oldBytes[0] && bytes[i + 1] == oldBytes[1] && bytes[i + 2] == oldBytes[2] && bytes[i + 3] == oldBytes[3])
            {
                newBytes.CopyTo(bytes, i);
                return;
            }
        }
    }

    private static void CopyAssetToTemp(string gameRoot, string tempDir, string gamePath, List<(string src, string dest)> fileEntries)
    {
        var srcAsset = Path.Combine(gameRoot, gamePath.Replace("/", "\\") + ".uasset");
        var srcExport = Path.Combine(gameRoot, gamePath.Replace("/", "\\") + ".uexp");

        var destDir = Path.Combine(tempDir, "Sifu", "Content", gamePath.Replace("/", "\\"));
        Directory.CreateDirectory(destDir);

        if (File.Exists(srcAsset))
        {
            var destAsset = Path.Combine(destDir, Path.GetFileName(srcAsset));
            File.Copy(srcAsset, destAsset, true);
            fileEntries.Add((destAsset, "../../../Sifu/Content/" + gamePath + ".uasset"));
        }

        if (File.Exists(srcExport))
        {
            var destExport = Path.Combine(destDir, Path.GetFileName(srcExport));
            File.Copy(srcExport, destExport, true);
            fileEntries.Add((destExport, "../../../Sifu/Content/" + gamePath + ".uexp"));
        }
    }

    private void PatchComboTreeRawBinary(string uassetPath, string uexpPath, Dictionary<string, string> swaps)
    {
        var uasset = File.ReadAllBytes(uassetPath);
        var uexp = File.ReadAllBytes(uexpPath);
        var origUassetLen = uasset.Length;
        var origUexpLen = uexp.Length;
        ErrorLog.Write("RAWPATCH", new Exception($"Loaded .uasset={origUassetLen} bytes, .uexp={origUexpLen} bytes"));

        int magic = BitConverter.ToInt32(uasset, 0);
        int legacyFileVersion = BitConverter.ToInt32(uasset, 4);
        if (magic != unchecked((int)0x9E2A83C1))
            throw new Exception($"Invalid uasset magic: 0x{magic:X8}");

        int pkgNameLen = BitConverter.ToInt32(uasset, 28);
        int afterPkgName = 32 + pkgNameLen;
        int packageFlags = BitConverter.ToInt32(uasset, afterPkgName);
        int nameCount = BitConverter.ToInt32(uasset, afterPkgName + 4);
        int nameOffset = BitConverter.ToInt32(uasset, afterPkgName + 8);
        ErrorLog.Write("RAWPATCH", new Exception($"Header: NameCount={nameCount}, NameOffset={nameOffset}"));

        string[] nameTable = new string[nameCount];
        int pos = nameOffset;
        for (int i = 0; i < nameCount; i++)
        {
            int strLen = BitConverter.ToInt32(uasset, pos);
            pos += 4;
            if (strLen == 0) { nameTable[i] = ""; continue; }
            if (strLen < 0)
            {
                int byteLen = (-strLen) * 2;
                nameTable[i] = Encoding.Unicode.GetString(uasset, pos, byteLen - 2);
                pos += byteLen;
            }
            else
            {
                nameTable[i] = Encoding.ASCII.GetString(uasset, pos, strLen - 1);
                pos += strLen;
            }
            pos += 4;
        }
        int nameTableEnd = pos;
        ErrorLog.Write("RAWPATCH", new Exception($"Name table ends at {nameTableEnd} (read {nameCount} entries)"));

        var nameToIndex = new Dictionary<string, int>();
        for (int i = 0; i < nameCount; i++)
            if (!string.IsNullOrEmpty(nameTable[i]) && !nameToIndex.ContainsKey(nameTable[i]))
                nameToIndex[nameTable[i]] = i;

        var oldIndices = new Dictionary<int, int>();
        var newEntries = new List<(string name, int index)>();
        int nextIndex = nameCount;

        foreach (var (oldPath, newPathWithSlash) in swaps)
        {
            var longPath = newPathWithSlash;
            var lastSlash = longPath.LastIndexOf('/');
            var shortName = lastSlash >= 0 ? longPath.Substring(lastSlash + 1) : longPath;
            var qualifiedPath = longPath + "." + shortName;

            int longIdx, shortIdx, qualifiedIdx;

            if (nameToIndex.TryGetValue(longPath, out var existingLong))
            {
                longIdx = existingLong;
                ErrorLog.Write("RAWPATCH", new Exception($"  New long path '{longPath}' already at name[{longIdx}]"));
            }
            else
            {
                longIdx = nextIndex++;
                newEntries.Add((longPath, longIdx));
                nameToIndex[longPath] = longIdx;
                ErrorLog.Write("RAWPATCH", new Exception($"  New long path '{longPath}' -> name[{longIdx}]"));
            }

            if (nameToIndex.TryGetValue(shortName, out var existingShort))
            {
                shortIdx = existingShort;
                ErrorLog.Write("RAWPATCH", new Exception($"  New short name '{shortName}' already at name[{shortIdx}]"));
            }
            else
            {
                shortIdx = nextIndex++;
                newEntries.Add((shortName, shortIdx));
                nameToIndex[shortName] = shortIdx;
                ErrorLog.Write("RAWPATCH", new Exception($"  New short name '{shortName}' -> name[{shortIdx}]"));
            }

            if (nameToIndex.TryGetValue(qualifiedPath, out var existingQ))
            {
                qualifiedIdx = existingQ;
            }
            else
            {
                qualifiedIdx = nextIndex++;
                newEntries.Add((qualifiedPath, qualifiedIdx));
                nameToIndex[qualifiedPath] = qualifiedIdx;
                ErrorLog.Write("RAWPATCH", new Exception($"  New qualified path '{qualifiedPath}' -> name[{qualifiedIdx}]"));
            }

            if (nameToIndex.TryGetValue(oldPath, out var oldLongIdx))
                oldIndices[oldLongIdx] = longIdx;

            var oldPathWithSlash = "/" + oldPath;
            if (nameToIndex.TryGetValue(oldPathWithSlash, out var oldLongIdx2))
                oldIndices[oldLongIdx2] = longIdx;

            var oldShortParts = oldPath.Split('/');
            var oldShort = oldShortParts[^1];
            if (nameToIndex.TryGetValue(oldShort, out var oldShortIdx))
                oldIndices[oldShortIdx] = shortIdx;

            var oldQualified = oldPath + "." + oldShort;
            if (nameToIndex.TryGetValue(oldQualified, out var oldQualIdx))
                oldIndices[oldQualIdx] = qualifiedIdx;

            var oldQualifiedWithSlash = "/" + oldQualified;
            if (nameToIndex.TryGetValue(oldQualifiedWithSlash, out var oldQualIdx2))
                oldIndices[oldQualIdx2] = qualifiedIdx;
        }

        ErrorLog.Write("RAWPATCH", new Exception($"Old->New index mappings: {oldIndices.Count}"));
        foreach (var (oldIdx, newIdx) in oldIndices)
            ErrorLog.Write("RAWPATCH", new Exception($"  name[{oldIdx}]='{nameTable[oldIdx]}' -> name[{newIdx}]"));

        ErrorLog.Write("RAWPATCH", new Exception("Skipping .uasset blind scan — old FName indices preserved in expanded name table"));
        ErrorLog.Write("RAWPATCH", new Exception("Skipping .uexp blind scan — old FName indices preserved in expanded name table"));

        if (newEntries.Count == 0)
        {
            File.WriteAllBytes(uassetPath, uasset);
            File.WriteAllBytes(uexpPath, uexp);
            ErrorLog.Write("RAWPATCH", new Exception("No new name entries needed — wrote patched files directly"));
            return;
        }

        var newEntryBytes = new List<byte>();
        foreach (var (entryName, _) in newEntries)
        {
            bool isUnicode = false;
            foreach (char c in entryName)
                if (c > 127) { isUnicode = true; break; }

            if (isUnicode)
            {
                int strLen = -(entryName.Length + 1);
                newEntryBytes.AddRange(BitConverter.GetBytes(strLen));
                newEntryBytes.AddRange(Encoding.Unicode.GetBytes(entryName));
                newEntryBytes.AddRange(new byte[] { 0, 0 });
            }
            else
            {
                int strLen = entryName.Length + 1;
                newEntryBytes.AddRange(BitConverter.GetBytes(strLen));
                newEntryBytes.AddRange(Encoding.ASCII.GetBytes(entryName));
                newEntryBytes.Add(0);
            }
            newEntryBytes.AddRange(BitConverter.GetBytes(0));
        }

        var patched = new byte[uasset.Length + newEntryBytes.Count];
        Buffer.BlockCopy(uasset, 0, patched, 0, nameTableEnd);
        newEntryBytes.CopyTo(patched, nameTableEnd);
        Buffer.BlockCopy(uasset, nameTableEnd, patched, nameTableEnd + newEntryBytes.Count, uasset.Length - nameTableEnd);

        int shift = newEntryBytes.Count;
        int newTotalHeaderSize = BitConverter.ToInt32(patched, 24) + shift;
        BitConverter.GetBytes(newTotalHeaderSize).CopyTo(patched, 24);

        int newExportCount = BitConverter.ToInt32(patched, afterPkgName + 20);
        int newExportOffset = BitConverter.ToInt32(patched, afterPkgName + 24) + shift;
        BitConverter.GetBytes(newExportOffset).CopyTo(patched, afterPkgName + 24);

        int newImportCount = BitConverter.ToInt32(patched, afterPkgName + 28);
        int newImportOffset = BitConverter.ToInt32(patched, afterPkgName + 32) + shift;
        BitConverter.GetBytes(newImportOffset).CopyTo(patched, afterPkgName + 32);

        int newDependsOffset = BitConverter.ToInt32(patched, afterPkgName + 36) + shift;
        BitConverter.GetBytes(newDependsOffset).CopyTo(patched, afterPkgName + 36);

        int newNameCount = nameCount + newEntries.Count;
        BitConverter.GetBytes(newNameCount).CopyTo(patched, afterPkgName + 4);
        BitConverter.GetBytes(nameOffset).CopyTo(patched, afterPkgName + 8);

        var trailingOffsets = FindTrailingHeaderOffsets(patched, afterPkgName, nameTableEnd);
        foreach (var (trOff, trSize, trName) in trailingOffsets)
        {
            if (trSize == 4)
            {
                int val = BitConverter.ToInt32(patched, trOff) + shift;
                BitConverter.GetBytes(val).CopyTo(patched, trOff);
                ErrorLog.Write("RAWPATCH", new Exception($"  Patched {trName} at offset {trOff}: {val - shift} -> {val}"));
            }
            else if (trSize == 8)
            {
                long val = BitConverter.ToInt64(patched, trOff) + shift;
                BitConverter.GetBytes(val).CopyTo(patched, trOff);
                ErrorLog.Write("RAWPATCH", new Exception($"  Patched {trName} at offset {trOff}: {val - shift} -> {val}"));
            }
        }

        // Patch SerialOffset in each FObjectExport entry (combined stream offsets)
        // FObjectExport entry size = 104 bytes for UE4.26, SerialOffset at +36 within each entry
        int exportEntrySize = 104;
        for (int e = 0; e < newExportCount; e++)
        {
            int entryPos = newExportOffset + (e * exportEntrySize) + 36;
            long oldSerialOff = BitConverter.ToInt64(patched, entryPos);
            long newSerialOff = oldSerialOff + shift;
            BitConverter.GetBytes(newSerialOff).CopyTo(patched, entryPos);
            ErrorLog.Write("RAWPATCH", new Exception($"  Patched SerialOffset export[{e}] at offset {entryPos}: {oldSerialOff} -> {newSerialOff}"));
        }

        File.WriteAllBytes(uassetPath, patched);
        File.WriteAllBytes(uexpPath, uexp);

        ErrorLog.Write("RAWPATCH", new Exception($"WRITTEN: .uasset {origUassetLen} -> {patched.Length} bytes (+{shift} for {newEntries.Count} new name entries)"));
        ErrorLog.Write("RAWPATCH", new Exception($"WRITTEN: .uexp {origUexpLen} -> {uexp.Length} bytes (same size, no blind scan)"));
        ErrorLog.Write("RAWPATCH", new Exception($"=== RAW BINARY PATCH COMPLETE: 0 blind scan patches, {newEntries.Count} new names ==="));
    }

    private static List<(int pos, int size, string name)> FindTrailingHeaderOffsets(byte[] data, int afterPkgName, int nameTableEnd)
    {
        var result = new List<(int pos, int size, string name)>();

        int hdrPos = afterPkgName + 40; // start after DependsOffset

        // SoftPackageReferencesCount + Offset
        hdrPos += 8;

        // SearchableNamesOffset
        hdrPos += 4;

        // ThumbnailTableOffset (FileVersionUE3 >= 584 → present)
        hdrPos += 4;

        // ImportTypeHierarchies (UE5 only → NOT present for UE4.26)

        // Guid (16 bytes)
        hdrPos += 16;

        // PersistentGuid — SKIPPED because PKG_FilterEditorOnly is set

        // Generations array
        int genCount = BitConverter.ToInt32(data, hdrPos);
        hdrPos += 4;
        hdrPos += genCount * 8;
        ErrorLog.Write("RAWPATCH", new Exception($"  Header trace: GenerationsCount={genCount}, after Generations at offset {hdrPos}"));

        // SavedByEngineVersion (FEngineVersion: Major(2)+Minor(2)+Patch(2)+Changelist(4)+Branch(FString))
        hdrPos += 10;
        int branchLen = BitConverter.ToInt32(data, hdrPos);
        hdrPos += 4;
        if (branchLen > 0) hdrPos += branchLen;
        else if (branchLen < 0) hdrPos += (-branchLen) * 2;

        // CompatibleEngineVersion (same format)
        hdrPos += 10;
        branchLen = BitConverter.ToInt32(data, hdrPos);
        hdrPos += 4;
        if (branchLen > 0) hdrPos += branchLen;
        else if (branchLen < 0) hdrPos += (-branchLen) * 2;
        ErrorLog.Write("RAWPATCH", new Exception($"  Header trace: after EngineVersions at offset {hdrPos}"));

        // CompressionFlags
        hdrPos += 4;

        // CompressedChunks array
        int ccCount = BitConverter.ToInt32(data, hdrPos);
        hdrPos += 4;
        hdrPos += ccCount * 32;

        // PackageSource
        hdrPos += 4;
        ErrorLog.Write("RAWPATCH", new Exception($"  Header trace: after PackageSource at offset {hdrPos}"));

        // AdditionalPackagesToCook array
        int addPkgCount = BitConverter.ToInt32(data, hdrPos);
        hdrPos += 4;
        for (int i = 0; i < addPkgCount; i++)
        {
            int strLen = BitConverter.ToInt32(data, hdrPos);
            hdrPos += 4;
            if (strLen > 0) hdrPos += strLen;
            else if (strLen < 0) hdrPos += (-strLen) * 2;
        }

        // TextureAllocations — SKIPPED because legacyFileVersion == -7 (not > -7)

        // AssetRegistryDataOffset (int32)
        int arPos = hdrPos;
        int arVal = BitConverter.ToInt32(data, hdrPos);
        hdrPos += 4;
        if (arVal > 0 && arVal < data.Length)
        {
            result.Add((arPos, 4, "AssetRegistryDataOffset"));
            ErrorLog.Write("RAWPATCH", new Exception($"  Found AssetRegistryDataOffset at offset {arPos} = {arVal}"));
        }

        // BulkDataStartOffset (int64) — always patch, it's a combined stream offset (> .uasset size)
        int bdPos = hdrPos;
        long bdVal = BitConverter.ToInt64(data, hdrPos);
        hdrPos += 8;
        result.Add((bdPos, 8, "BulkDataStartOffset"));
        ErrorLog.Write("RAWPATCH", new Exception($"  Found BulkDataStartOffset at offset {bdPos} = {bdVal}"));

        // WorldTileInfoDataOffset (int32)
        hdrPos += 4;

        // ChunkIds array
        int chunkIdCount = BitConverter.ToInt32(data, hdrPos);
        hdrPos += 4;
        hdrPos += chunkIdCount * 4;

        // PreloadDependencyCount + PreloadDependencyOffset
        hdrPos += 4;
        int pdPos = hdrPos;
        int pdVal = BitConverter.ToInt32(data, hdrPos);
        hdrPos += 4;
        if (pdVal > 0 && pdVal < data.Length)
        {
            result.Add((pdPos, 4, "PreloadDependencyOffset"));
            ErrorLog.Write("RAWPATCH", new Exception($"  Found PreloadDependencyOffset at offset {pdPos} = {pdVal}"));
        }

        ErrorLog.Write("RAWPATCH", new Exception($"  Header trace complete: found {result.Count} offset fields to patch"));
        return result;
    }

    private void LoadComboGraph()
    {
        try
        {
            _comboGraph = _parser.LoadMainCharComboTree();
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
        _isPanning = false;
        comboCanvas.ReleaseMouseCapture();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_initialized) return;
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
        if (!_keyW && !_keyA && !_keyS && !_keyD) return;
        double speed = 8;
        if (_keyW) _comboTranslate.Y += speed;
        if (_keyS) _comboTranslate.Y -= speed;
        if (_keyA) _comboTranslate.X += speed;
        if (_keyD) _comboTranslate.X -= speed;
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
