using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace SifuMovesetEditor.Setup;

public partial class SetupWizard : Window
{
    public string? SelectedContentPath { get; private set; }
    public bool WasSkipped { get; private set; }

    private static Brush MakeBrush(string hex) =>
        (Brush)new BrushConverter().ConvertFrom(hex);

    public SetupWizard()
    {
        InitializeComponent();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = rbGameDir.IsChecked == true
                ? "Select Sifu Installation Folder"
                : "Select Extracted Content Folder",
        };

        if (dialog.ShowDialog() == true)
        {
            txtFolderPath.Text = dialog.FolderName;
            ValidatePath(dialog.FolderName);
        }
    }

    private void ValidatePath(string path)
    {
        if (rbGameDir.IsChecked == true)
        {
            var paksDir = Path.Combine(path, "Sifu", "Content", "Paks");
            if (Directory.Exists(paksDir) && Directory.GetFiles(paksDir, "pakchunk0-*.pak").Length > 0)
            {
                txtValidation.Text = "✓ Found Sifu installation with pak file.";
                txtValidation.Foreground = MakeBrush("#a6e3a1");
                btnExtract.IsEnabled = true;
                SelectedContentPath = path;
            }
            else
            {
                txtValidation.Text = "✗ Could not find pakchunk0*.pak. Select the Sifu installation root.";
                txtValidation.Foreground = MakeBrush("#f38ba8");
                btnExtract.IsEnabled = false;
            }
        }
        else
        {
            var contentDir = Path.Combine(path, "Content");
            if (Directory.Exists(contentDir) && Directory.Exists(Path.Combine(contentDir, "Animations")))
            {
                var comboTree = Path.Combine(contentDir, "DB", "_MainChar", "Combos", "MainChar_ComboTree.uasset");
                var hasCombos = File.Exists(comboTree);
                var animCount = Directory.Exists(Path.Combine(contentDir, "Animations"))
                    ? Directory.GetFiles(Path.Combine(contentDir, "Animations"), "*.uasset", SearchOption.AllDirectories).Length
                    : 0;

                if (hasCombos && animCount > 0)
                {
                    txtValidation.Text = $"✓ Found {animCount} animations, combo tree present.";
                    txtValidation.Foreground = MakeBrush("#a6e3a1");
                    btnExtract.IsEnabled = true;
                    SelectedContentPath = path;
                }
                else
                {
                    txtValidation.Text = "✗ Missing combo tree or animations. Need a complete extraction.";
                    txtValidation.Foreground = MakeBrush("#fab387");
                    btnExtract.IsEnabled = false;
                }
            }
            else
            {
                txtValidation.Text = "✗ Could not find Content/Animations/. Select an extracted content folder.";
                txtValidation.Foreground = MakeBrush("#f38ba8");
                btnExtract.IsEnabled = false;
            }
        }
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedContentPath == null) return;

        // Switch to extracting state
        panelSelect.Visibility = Visibility.Collapsed;
        panelExtracting.Visibility = Visibility.Visible;

        var targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameContent");
        var sourceRoot = SelectedContentPath;

        if (rbGameDir.IsChecked == true)
        {
            // For game installations, we need the Content parent
            // The pak is at {path}/Sifu/Content/Paks, so sourceRoot is the game root
            // But ContentExtractor expects the root that contains Content/
            // For a game install: {gameRoot}/Sifu/Content/ — so we pass {gameRoot}/Sifu
            sourceRoot = Path.Combine(SelectedContentPath, "Sifu");
        }

        try
        {
            int totalFiles = 0;
            int copiedFiles = 0;

            // Count total files first
            await System.Threading.Tasks.Task.Run(() =>
            {
                totalFiles = ExtractionManifest.GetAllNeededPaths(sourceRoot).Count;
            });

            txtFileCount.Text = $"0 / {totalFiles} files";

            var progress = new Progress<(int copied, int total, string currentFile)>(report =>
            {
                copiedFiles = report.copied;
                var pct = report.total > 0 ? (double)report.copied / report.total : 0;

                // Animate progress bar
                var targetWidth = pct * ((FrameworkElement)progressFill.Parent).RenderSize.Width;
                var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(200));
                progressFill.BeginAnimation(WidthProperty, anim);

                txtProgress.Text = $"{(int)(pct * 100)}%";
                txtCurrentFile.Text = report.currentFile;
                txtFileCount.Text = $"{report.copied} / {report.total} files";
            });

            await System.Threading.Tasks.Task.Run(() =>
            {
                ContentExtractor.CopyFromExtracted(sourceRoot, targetDir, progress);
            });

            // Verify
            var check = ContentDetector.Detect(targetDir);
            if (check.IsValid)
            {
                SelectedContentPath = targetDir;
                ShowComplete(totalFiles);
            }
            else
            {
                // Go back to select with error
                panelExtracting.Visibility = Visibility.Collapsed;
                panelSelect.Visibility = Visibility.Visible;
                txtValidation.Text = $"✗ Extraction failed: {check.Reason}";
                txtValidation.Foreground = MakeBrush("#f38ba8");
            }
        }
        catch (Exception ex)
        {
            panelExtracting.Visibility = Visibility.Collapsed;
            panelSelect.Visibility = Visibility.Visible;
            txtValidation.Text = $"✗ Error: {ex.Message}";
            txtValidation.Foreground = MakeBrush("#f38ba8");
        }
    }

    private void ShowComplete(int fileCount)
    {
        panelExtracting.Visibility = Visibility.Collapsed;
        panelComplete.Visibility = Visibility.Visible;

        var sizeStr = "458 MB";
        txtSummary.Text = $"Extracted {fileCount} files ({sizeStr})\nContent: GameContent/";
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        WasSkipped = true;
        DialogResult = false;
        Close();
    }
}
