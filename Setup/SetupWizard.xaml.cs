using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace SifuMovesetEditor.Setup;

public enum SetupMode
{
    FirstRun,
    Repair,
}

public partial class SetupWizard : Window
{
    public string? SelectedContentPath { get; private set; }
    public bool WasSkipped { get; private set; }
    public SetupMode Mode { get; set; } = SetupMode.FirstRun;
    public string? RepairReason { get; set; }
    public string? PreviousContentPath { get; set; }

    private string? _lastBrowsePath;
    private bool _launched;

    private static Brush MakeBrush(string hex) =>
        (Brush)new BrushConverter().ConvertFrom(hex)!;

    public SetupWizard()
    {
        InitializeComponent();
        Loaded += SetupWizard_Loaded;
        Closing += SetupWizard_Closing;
    }

    private void SetupWizard_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_launched) return;
        Application.Current.Shutdown();
    }

    private void SetupWizard_Loaded(object sender, RoutedEventArgs e)
    {
        if (Mode == SetupMode.Repair)
        {
            Title = "Sifu Moveset Editor — Missing Vanilla Files";
            txtHeaderMode.Text = "— Missing Vanilla Files";
            txtHeaderMode.Foreground = MakeBrush("#fab387");
            borderRepair.Visibility = Visibility.Visible;
            txtRepairReason.Text = string.IsNullOrWhiteSpace(RepairReason)
                ? "Content path is missing or incomplete."
                : RepairReason;
            txtRepairOldPath.Text = string.IsNullOrEmpty(PreviousContentPath)
                ? ""
                : $"Previous: {PreviousContentPath}";
            txtDescription.Text = "Re-extract or re-point vanilla game assets to refresh the Content Path.";
            btnCancel.Content = "Cancel";
            txtStatus.Text = "Browse a folder or pak file to restore vanilla content.";
        }
        else
        {
            Title = "Sifu Moveset Editor — Setup";
            txtHeaderMode.Text = "— Setup";
            borderRepair.Visibility = Visibility.Collapsed;
            btnCancel.Content = "Cancel";
        }

        UpdateBrowseLabel();
        UpdateExtractLabel();
        UpdatePreflightHint();
        UpdateDiskHint();
    }

    private void SourceRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateBrowseLabel();
        UpdateExtractLabel();
        UpdatePreflightHint();
        UpdateDiskHint();
        if (!string.IsNullOrEmpty(_lastBrowsePath))
            ValidatePath(_lastBrowsePath);
        else
        {
            SelectedContentPath = null;
            btnExtract.IsEnabled = false;
            txtFolderPath.Text = "";
            txtValidation.Text = rbPakFile.IsChecked == true
                ? "Select a pakchunk0*.pak file to continue."
                : "Select a folder to continue.";
            txtValidation.Foreground = MakeBrush("#6c7086");
        }
    }

    private void UpdateDiskHint()
    {
        // Disk-size note only for modes that extract from the pak; not for existing extracted content.
        txtDiskHint.Visibility = rbExtractedDir.IsChecked == true
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateBrowseLabel()
    {
        btnBrowse.Content = rbPakFile.IsChecked == true ? "Browse…" : "Browse";
    }

    private void UpdateExtractLabel()
    {
        if (rbExtractedDir.IsChecked == true)
            btnExtract.Content = "Use This Folder";
        else if (rbPakFile.IsChecked == true)
            btnExtract.Content = Mode == SetupMode.Repair ? "Extract & Refresh" : "Extract from Pak";
        else
            btnExtract.Content = Mode == SetupMode.Repair ? "Extract & Refresh" : "Extract";
    }

    private void UpdatePreflightHint()
    {
        if (rbExtractedDir.IsChecked == true)
            return;

        var unrealPak = ContentExtractor.FindUnrealPak();

        if (unrealPak == null)
        {
            if (SelectedContentPath != null)
            {
                txtValidation.Text = "✗ UnrealPak not found — required to unpack pak. Place UnrealPak.exe in tools\\ue4\\UnrealPak\\ or browse setup after copying it there.";
                txtValidation.Foreground = MakeBrush("#f38ba8");
                btnExtract.IsEnabled = false;
            }
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (rbPakFile.IsChecked == true)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Sifu pak file",
                Filter = "Pak files (*.pak)|*.pak|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog() == true)
            {
                txtFolderPath.Text = dialog.FileName;
                ValidatePath(dialog.FileName);
            }
            return;
        }

        var folderDialog = new OpenFolderDialog
        {
            Title = rbGameDir.IsChecked == true
                ? "Select Sifu Installation Folder"
                : "Select Extracted Content Folder",
        };

        if (folderDialog.ShowDialog() == true)
        {
            txtFolderPath.Text = folderDialog.FolderName;
            ValidatePath(folderDialog.FolderName);
        }
    }

    private void ValidatePath(string path)
    {
        _lastBrowsePath = path;
        SelectedContentPath = null;

        if (rbPakFile.IsChecked == true)
        {
            if (File.Exists(path) && path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(path);
                if (!name.Contains("pakchunk0", StringComparison.OrdinalIgnoreCase))
                {
                    txtValidation.Text = $"⚠ File is named '{name}' — expected pakchunk0*.pak. Continuing may still work.";
                    txtValidation.Foreground = MakeBrush("#fab387");
                }
                else
                {
                    txtValidation.Text = "✓ Pak file selected. UnrealPak will unpack it into GameContent.";
                    txtValidation.Foreground = MakeBrush("#a6e3a1");
                }

                SelectedContentPath = path;
                UpdatePreflightHint();
                if (btnExtract.IsEnabled || ContentExtractor.FindUnrealPak() != null)
                    btnExtract.IsEnabled = ContentExtractor.FindUnrealPak() != null;
            }
            else
            {
                txtValidation.Text = "✗ Select a .pak file.";
                txtValidation.Foreground = MakeBrush("#f38ba8");
                btnExtract.IsEnabled = false;
            }
            return;
        }

        if (rbGameDir.IsChecked == true)
        {
            var pak = ContentExtractor.FindPakFile(path);
            if (pak != null)
            {
                var sifuRoot = Path.Combine(path, "Sifu");
                var localAnims = Path.Combine(sifuRoot, "Content", "Animations");
                var hasLocalTree = Directory.Exists(localAnims) &&
                                   Directory.GetFiles(localAnims, "*.uasset", SearchOption.AllDirectories).Length > 0;

                SelectedContentPath = path;
                btnExtract.IsEnabled = ContentExtractor.FindUnrealPak() != null || hasLocalTree;

                if (hasLocalTree)
                {
                    txtValidation.Text = "✓ Found installation + local extracted Animations (will copy).";
                    txtValidation.Foreground = MakeBrush("#a6e3a1");
                }
                else if (ContentExtractor.FindUnrealPak() != null)
                {
                    txtValidation.Text = "✓ Found pak file — ready to extract.";
                    txtValidation.Foreground = MakeBrush("#a6e3a1");
                    btnExtract.IsEnabled = true;
                }
                else
                {
                    txtValidation.Text = "✓ Found pak file, but UnrealPak not found — cannot unpack yet.";
                    txtValidation.Foreground = MakeBrush("#f38ba8");
                    btnExtract.IsEnabled = false;
                }
            }
            else
            {
                txtValidation.Text = "✗ Could not find pakchunk0*.pak. Select the Sifu installation root.";
                txtValidation.Foreground = MakeBrush("#f38ba8");
                btnExtract.IsEnabled = false;
            }
            return;
        }

        // Extracted dir mode — accept either ...\Sifu or a parent that contains Sifu\Content (and Engine\)
        var contentRoot = ContentDetector.ResolveContentRoot(path);
        if (contentRoot != null)
        {
            var detection = ContentDetector.Detect(contentRoot);
            if (detection.IsValid)
            {
                var animCount = Directory.GetFiles(
                    Path.Combine(contentRoot, "Content", "Animations"),
                    "*.uasset", SearchOption.AllDirectories).Length;
                txtValidation.Text = $"✓ Ready — {animCount} animations; combo trees, attack DBs, stance data found.";
                txtValidation.Foreground = MakeBrush("#a6e3a1");
                btnExtract.IsEnabled = true;
                // Copy root: the folder that actually contains Content\ (Engine found via parent)
                SelectedContentPath = detection.ContentPath ?? contentRoot;
            }
            else
            {
                txtValidation.Text = $"✗ {detection.Reason}";
                txtValidation.Foreground = MakeBrush("#f38ba8");
                btnExtract.IsEnabled = false;
                SelectedContentPath = null;
            }
        }
        else
        {
            txtValidation.Text = "✗ Could not find Content/Animations/. Select an extracted content folder.";
            txtValidation.Foreground = MakeBrush("#f38ba8");
            btnExtract.IsEnabled = false;
        }
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedContentPath == null) return;

        if (!EnsureDiskSpace()) return;

        panelSelect.Visibility = Visibility.Collapsed;
        panelExtracting.Visibility = Visibility.Visible;
        txtCurrentFile.Text = "Preparing...";
        txtFileCount.Text = "";
        txtProgress.Text = "0%";

        var targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameContent");

        try
        {
            if (rbPakFile.IsChecked == true)
            {
                await ExtractPakFileAsync(SelectedContentPath, targetDir);
            }
            else if (rbGameDir.IsChecked == true)
            {
                await ExtractGameDirAsync(SelectedContentPath, targetDir);
            }
            else
            {
                await CopyExtractedAsync(SelectedContentPath, targetDir);
            }
        }
        catch (Exception ex)
        {
            FailSelect($"✗ Error: {ex.Message}");
        }
    }

    private bool EnsureDiskSpace()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var driveRoot = Path.GetPathRoot(baseDir);
            if (string.IsNullOrEmpty(driveRoot))
                return true;

            var drive = new DriveInfo(driveRoot);
            long required;

            if (rbExtractedDir.IsChecked == true)
            {
                required = 600L * 1024 * 1024;
            }
            else
            {
                // Selective filtered extract: stage (~0.7GB) + GameContent (~1.2GB) + margin.
                // Full-extract fallback re-checks free space inside ContentExtractor.
                required = 4L * 1024 * 1024 * 1024;
            }

            var free = drive.AvailableFreeSpace;
            if (free >= required)
                return true;

            static string Gb(long bytes) => $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
            txtValidation.Text =
                $"✗ Not enough disk space on {driveRoot}: need ~{Gb(required)} free, have {Gb(free)}.";
            txtValidation.Foreground = MakeBrush("#f38ba8");
            return false;
        }
        catch
        {
            return true;
        }
    }

    private async System.Threading.Tasks.Task ExtractPakFileAsync(string pakPath, string targetDir)
    {
        var stageDir = ContentExtractor.GetDefaultStageDir();

        var status = new Progress<string>(s => txtCurrentFile.Text = s);
        var prog = new Progress<double>(p =>
        {
            var targetWidth = p * ((FrameworkElement)progressFill.Parent).RenderSize.Width;
            progressFill.BeginAnimation(WidthProperty, new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(200)));
            txtProgress.Text = $"{(int)(p * 100)}%";
            txtFileCount.Text = p < 0.85 ? "UnrealPak..." : "Copying...";
        });

        var err = await ContentExtractor.ExtractPakToStageAsync(pakPath, stageDir, status, prog);
        if (err != null)
        {
            ContentExtractor.CleanupStage(stageDir);
            FailSelect($"✗ {err}");
            return;
        }

        var sourceRoot = ContentExtractor.ResolveExtractedRoot(stageDir);
        if (sourceRoot == null)
        {
            ContentExtractor.CleanupStage(stageDir);
            FailSelect("✗ Extraction finished but Animations folder was not found.");
            return;
        }

        // Wipe only after stage gate passed so a failed extract does not destroy existing content.
        var wipeErr = ContentExtractor.WipeGameContentTrees(targetDir);
        if (wipeErr != null)
        {
            ContentExtractor.CleanupStage(stageDir);
            FailSelect($"✗ {wipeErr}");
            return;
        }

        try
        {
            await CopyExtractedAsync(sourceRoot, targetDir, alreadyExtracted: true);
        }
        finally
        {
            ContentExtractor.CleanupStage(stageDir);
        }
    }

    private async System.Threading.Tasks.Task ExtractGameDirAsync(string gameDir, string targetDir)
    {
        var localSifu = Path.Combine(gameDir, "Sifu");
        var localAnims = Path.Combine(localSifu, "Content", "Animations");
        var hasLocalTree = Directory.Exists(localAnims) &&
                           Directory.GetFiles(localAnims, "*.uasset", SearchOption.AllDirectories).Length > 0;

        if (hasLocalTree)
        {
            await CopyExtractedAsync(localSifu, targetDir);
            return;
        }

        var status = new Progress<string>(s => txtCurrentFile.Text = s);
        var prog = new Progress<double>(p =>
        {
            var targetWidth = p * ((FrameworkElement)progressFill.Parent).RenderSize.Width;
            progressFill.BeginAnimation(WidthProperty, new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(200)));
            txtProgress.Text = $"{(int)(p * 100)}%";
        });

        var copyProgress = new Progress<(int copied, int total, string currentFile)>(report =>
        {
            var pct = report.total > 0 ? (double)report.copied / report.total : 0;
            var blended = 0.85 + pct * 0.15;
            var targetWidth = blended * ((FrameworkElement)progressFill.Parent).RenderSize.Width;
            progressFill.BeginAnimation(WidthProperty, new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(200)));
            txtProgress.Text = $"{(int)(blended * 100)}%";
            txtCurrentFile.Text = report.currentFile;
            txtFileCount.Text = $"{report.copied} / {report.total} files";
        });

        var err = await ContentExtractor.ExtractGameDirToGameContentAsync(
            gameDir, targetDir, copyProgress, status, prog);

        if (err != null)
        {
            FailSelect($"✗ {err}");
            return;
        }

        await FinishAsync(targetDir, -1);
    }

    private async System.Threading.Tasks.Task CopyExtractedAsync(
        string sourceRoot, string targetDir, bool alreadyExtracted = false)
    {
        int totalFiles = await System.Threading.Tasks.Task.Run(() =>
            ExtractionManifest.GetAllNeededPaths(sourceRoot).Count);

        if (totalFiles == 0)
        {
            FailSelect("✗ No source files found — extract the pak first, or point to a complete extracted Content tree.");
            return;
        }

        txtFileCount.Text = $"0 / {totalFiles} files";

        var progress = new Progress<(int copied, int total, string currentFile)>(report =>
        {
            var pct = report.total > 0 ? (double)report.copied / report.total : 0;
            var start = alreadyExtracted ? 0.85 : 0;
            var span = alreadyExtracted ? 0.15 : 1.0;
            var blended = start + pct * span;

            var targetWidth = blended * ((FrameworkElement)progressFill.Parent).RenderSize.Width;
            progressFill.BeginAnimation(WidthProperty, new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(200)));

            txtProgress.Text = $"{(int)(blended * 100)}%";
            txtCurrentFile.Text = report.currentFile;
            txtFileCount.Text = $"{report.copied} / {report.total} files";
        });

        var copied = await System.Threading.Tasks.Task.Run(() =>
            ContentExtractor.CopyFromExtracted(sourceRoot, targetDir, progress));

        var copyIssue = ContentExtractor.DescribeCopyIssues(sourceRoot, targetDir);
        if (copyIssue != null)
        {
            FailSelect($"✗ Copy finished but content is incomplete: {copyIssue}");
            return;
        }

        await FinishAsync(targetDir, copied);
    }

    private async System.Threading.Tasks.Task FinishAsync(string targetDir, int fileCount)
    {
        // Absolute completeness for extract paths (pak / game-dir), not for folder import.
        if (rbPakFile.IsChecked == true || rbGameDir.IsChecked == true)
        {
            var contentIssue = ContentExtractor.DescribeContentIssues(targetDir);
            if (contentIssue != null)
            {
                FailSelect($"✗ GameContent is incomplete: {contentIssue}");
                return;
            }
        }

        var check = ContentDetector.Detect(targetDir);
        if (check.IsValid)
        {
            SelectedContentPath = targetDir;
            ShowComplete(fileCount);
            await System.Threading.Tasks.Task.CompletedTask;
        }
        else
        {
            var prefix = rbExtractedDir.IsChecked == true ? "Import failed" : "Extraction failed";
            FailSelect($"✗ {prefix}: {check.Reason}");
        }
    }

    private void FailSelect(string message)
    {
        panelExtracting.Visibility = Visibility.Collapsed;
        panelSelect.Visibility = Visibility.Visible;
        txtValidation.Text = message;
        txtValidation.Foreground = MakeBrush("#f38ba8");
        btnExtract.IsEnabled = SelectedContentPath != null;
    }

    private void ShowComplete(int fileCount)
    {
        panelExtracting.Visibility = Visibility.Collapsed;
        panelComplete.Visibility = Visibility.Visible;

        string sizeStr;
        try
        {
            var (_, bytes) = ExtractionManifest.EstimateSize(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameContent"));
            sizeStr = bytes > 0
                ? $"{bytes / (1024.0 * 1024.0):F0} MB"
                : "content restored";
        }
        catch
        {
            sizeStr = "content restored";
        }

        txtSummary.Text = fileCount > 0
            ? $"Extracted {fileCount} files ({sizeStr})\nContent: GameContent/"
            : $"Vanilla content ready ({sizeStr})\nContent: GameContent/";

        try
        {
            var animRoot = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "GameContent", "Content", "Animations");
            var animCount = Directory.Exists(animRoot)
                ? Directory.GetFiles(animRoot, "*.uasset", SearchOption.AllDirectories).Length
                : 0;
            if (animCount < 3370)
            {
                txtSummary.Text += $"\n⚠ Animations count low ({animCount} uassets; expected ~3379). " +
                                   "Re-run extract or check setup.log.";
                txtSummary.Foreground = MakeBrush("#fab387");
            }
        }
        catch { }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        _launched = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        WasSkipped = true;
        DialogResult = false;
        Close();
    }
}
