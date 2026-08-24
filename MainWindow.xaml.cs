using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using SifuMovesetEditor;

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
    private string _settingsPath;
    private string _contentPath = "";
    private bool _initialized = false;

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
        _initialized = true;
    }

    private void LoadSettings()
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
                    InitializeParser();
                }
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Error loading settings: {ex.Message}";
            }
        }
    }

    private void SaveSettings()
    {
        var settings = new Settings { ContentPath = _contentPath };
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
            BuildTree(_allMoves);
            txtMoveCount.Text = $"{_allMoves.Count} moves";
            txtStatus.Text = $"Loaded {_allMoves.Count} animations from {_contentPath}";

            PopulateCharacterDropdown();
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

        BuildTree(filtered);
        txtMoveCount.Text = $"{filtered.Count} moves";
    }

    private void BuildTree(List<MoveInfo> moves)
    {
        treeMoves.Items.Clear();

        var grouped = moves
            .GroupBy(m => m.Character)
            .OrderBy(g => g.Key);

        foreach (var charGroup in grouped)
        {
            var charNode = new TreeViewItem
            {
                Header = $"{charGroup.Key} ({charGroup.Count()})",
                Foreground = System.Windows.Media.Brushes.White
            };

            var weaponGroups = charGroup
                .GroupBy(m => m.WeaponType)
                .OrderBy(g => g.Key);

            foreach (var weaponGroup in weaponGroups)
            {
                var weaponNode = new TreeViewItem
                {
                    Header = $"{weaponGroup.Key} ({weaponGroup.Count()})",
                    Foreground = System.Windows.Media.Brushes.LightGray
                };

                var catGroups = weaponGroup
                    .GroupBy(m => string.IsNullOrEmpty(m.Category) ? "(ungrouped)" : m.Category)
                    .OrderBy(g => g.Key);

                foreach (var catGroup in catGroups)
                {
                    var catNode = new TreeViewItem
                    {
                        Header = $"{catGroup.Key} ({catGroup.Count()})",
                        Foreground = System.Windows.Media.Brushes.LightGray
                    };

                    foreach (var move in catGroup.OrderBy(m => m.DisplayName))
                    {
                        var moveNode = new TreeViewItem
                        {
                            Header = move.DisplayName,
                            Tag = move,
                            Foreground = System.Windows.Media.Brushes.LightGray
                        };
                        catNode.Items.Add(moveNode);
                    }

                    if (catNode.Items.Count > 0)
                        weaponNode.Items.Add(catNode);
                }

                if (weaponNode.Items.Count > 0)
                    charNode.Items.Add(weaponNode);
            }

            if (charNode.Items.Count > 0)
                treeMoves.Items.Add(charNode);
        }
    }

    private async void TreeMove_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (treeMoves.SelectedItem is not TreeViewItem selected) return;
        if (selected.Tag is not MoveInfo move) return;

        txtStatus.Text = $"Loading: {move.DisplayName}...";
        await LoadAnimationAsync(move.FullPath);

        if (chkShowMesh.IsChecked == true && !string.IsNullOrEmpty(move.Character))
        {
            var currentChar = "";
            if (cmbCharacter.SelectedItem is ComboBoxItem item)
                currentChar = item.Tag?.ToString() ?? "";

            if (currentChar != move.Character)
            {
                for (int i = 0; i < cmbCharacter.Items.Count; i++)
                {
                    if (cmbCharacter.Items[i] is ComboBoxItem ci && ci.Tag?.ToString() == move.Character)
                    {
                        cmbCharacter.SelectedIndex = i;
                        break;
                    }
                }
            }
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
                txtStatus.Text = "Failed to load animation (returned null)";
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
        if (cmbSpeed.SelectedItem is ComboBoxItem item && item.Tag is string speedStr)
        {
            if (float.TryParse(speedStr, out float speed))
            {
                webView.CoreWebView2?.ExecuteScriptAsync(
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
            await webView.CoreWebView2.ExecuteScriptAsync($"window.loadMesh({json})");
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
        txtStatus.Text = "Export not yet implemented";
    }
}

public class Settings
{
    public string ContentPath { get; set; } = "";
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
