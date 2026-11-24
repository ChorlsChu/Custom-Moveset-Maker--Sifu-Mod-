using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SifuMovesetEditor.Update;

/// <summary>
/// Lists every published GitHub release and installs the one the user picks -
/// forwards via the normal update path, backwards to any older tag. The actual
/// download/swap is <see cref="UpdateChecker.ApplyUpdateAsync"/>; this window
/// only adds the history, the notes and the optional pre-install backup.
/// </summary>
public partial class VersionHistoryDialog : Window
{
    private static readonly Brush Neutral = new SolidColorBrush(Color.FromRgb(0xa6, 0xad, 0xc8));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xf3, 0x8b, 0xa8));

    private readonly Version _current = UpdateChecker.CurrentVersion();
    private bool _busy;
    private bool _selectedRequiresBackup;

    public VersionHistoryDialog() : this(autoLoad: true)
    {
    }

    /// <summary><paramref name="autoLoad"/> is false in smoke tests, which fill
    /// the list with fixtures instead of calling GitHub.</summary>
    internal VersionHistoryDialog(bool autoLoad)
    {
        InitializeComponent();
        txtCurrent.Text = $"You are running v{_current}.";
        if (autoLoad) Loaded += async (_, _) => await LoadReleasesAsync();
    }

    private async System.Threading.Tasks.Task LoadReleasesAsync()
    {
        loadingPanel.Visibility = Visibility.Visible;
        errorPanel.Visibility = Visibility.Collapsed;
        contentPanel.Visibility = Visibility.Collapsed;
        btnInstall.IsEnabled = false;
        txtStatus.Visibility = Visibility.Collapsed;
        installProgress.Visibility = Visibility.Collapsed;
        try
        {
            var releases = await UpdateChecker.GetReleasesAsync();
            ShowReleases(releases);
        }
        catch (Exception ex)
        {
            ErrorLog.WriteRaw($"[UPDATE] version history failed: {ex.Message}");
            txtError.Text = $"GitHub could not be reached, so the release list is unavailable. {ex.Message}";
            loadingPanel.Visibility = Visibility.Collapsed;
            errorPanel.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Fills the list. Internal so the smoke test can render the window
    /// with fixture releases instead of hitting GitHub.</summary>
    internal void ShowReleases(List<UpdateInfo> releases)
    {
        loadingPanel.Visibility = Visibility.Collapsed;
        errorPanel.Visibility = Visibility.Collapsed;

        var rows = releases.Select(info => new ReleaseRow(info, _current)).ToList();
        if (rows.Count == 0)
        {
            txtError.Text = "No releases with the SifuMovesetEditor.zip download were found.";
            errorPanel.Visibility = Visibility.Visible;
            return;
        }

        contentPanel.Visibility = Visibility.Visible;
        listReleases.ItemsSource = rows;

        // Make the containers exist before choosing a row. Selecting while the
        // panel is still collapsed lets WPF apply the deferred selection over a
        // click that happened in between (it always landed on the newest row).
        UpdateLayout();

        // Prefer the running version so the dialog opens on familiar ground; a
        // local build that was never published simply starts at the newest tag.
        // Only when nothing is selected yet, so a reload can never overwrite the
        // user's choice.
        if (listReleases.SelectedItem == null)
        {
            var currentIndex = rows.FindIndex(r => r.IsCurrent);
            listReleases.SelectedItem = rows[currentIndex >= 0 ? currentIndex : 0];
        }
        listReleases.Focus();
    }

    /// <summary>Screenshot/test hook: selects a row by position after
    /// <see cref="ShowReleases"/> so an older release's warning state can be
    /// rendered without a live window.</summary>
    internal void SelectReleaseForTest(int index) => listReleases.SelectedIndex = index;

    internal int SelectedReleaseIndexForTest => listReleases.SelectedIndex;
    internal bool SelectedRequiresBackupForTest => _selectedRequiresBackup;
    internal bool BackupCheckedForTest => chkBackup.IsChecked == true;
    internal bool BackupEnabledForTest => chkBackup.IsEnabled;
    internal bool BackupVisibleForTest => chkBackup.Visibility == Visibility.Visible;

    private void Releases_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (listReleases.SelectedItem is not ReleaseRow row)
        {
            _selectedRequiresBackup = false;
            btnInstall.IsEnabled = false;
            return;
        }

        var info = row.Info;
        var downgrade = info.Version < _current;
        var requiresBackup = downgrade && info.Version < UpdateChecker.FirstAutoUpdateVersion;
        _selectedRequiresBackup = requiresBackup;

        txtReleaseTitle.Text = row.IsCurrent ? $"{row.Tag} (you are here)" : row.Tag;
        txtReleaseMeta.Text = row.MetaText;

        txtNotes.Inlines.Clear();
        var notes = (info.Notes ?? "").Trim();
        txtNotes.Text = notes.Length == 0 ? "No release notes for this version." : "";
        if (notes.Length > 0) txtNotes.Inlines.AddRange(MarkdownInlines.Render(notes));

        var warning =
            $"Older versions may not open projects or custom assets that v{_current} saved. " +
            "Nothing is deleted either way - your .sifu-edit files stay on disk - but save a copy " +
            $"before you edit them in v{info.VersionText}.";
        if (requiresBackup)
            warning += $" {row.Tag} was built before the in-app updater existed, so the backup below " +
                       $"is required - it is how you get back to v{_current} if this build cannot check for updates.";
        txtWarning.Text = warning;
        warningCard.Visibility = downgrade ? Visibility.Visible : Visibility.Collapsed;
        chkBackup.Visibility = downgrade ? Visibility.Visible : Visibility.Collapsed;

        chkBackup.IsChecked = requiresBackup || (downgrade && chkBackup.IsChecked == true);
        chkBackup.IsEnabled = !requiresBackup && !_busy;
        chkBackup.Content = requiresBackup
            ? "Back up the current install first (required for this version)"
            : "Back up the current install first";
        chkBackup.ToolTip = requiresBackup
            ? "This release is older than the in-app updater. The backup is how you restore the version you are running now if the old build cannot update."
            : "Copies the app files this release will replace into the Backups folder. The last three backups are kept.";

        btnInstall.Content = row.IsCurrent
            ? "You are running this version"
            : downgrade
                ? $"Revert to {row.Tag} & Restart"
                : $"Update to {row.Tag} & Restart";
        btnInstall.IsEnabled = !row.IsCurrent && !_busy;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || listReleases.SelectedItem is not ReleaseRow row) return;
        var info = row.Info;
        var downgrade = info.Version < _current;
        var backup = downgrade && (_selectedRequiresBackup || chkBackup.IsChecked == true);

        _busy = true;
        btnInstall.IsEnabled = false;
        btnCancel.IsEnabled = false;
        listReleases.IsEnabled = false;
        chkBackup.IsEnabled = false;
        installProgress.Visibility = Visibility.Visible;
        installProgress.Value = 0;
        txtStatus.Visibility = Visibility.Visible;
        txtStatus.Foreground = Neutral;
        txtStatus.Text = $"Downloading {row.Tag}...";

        try
        {
            var progress = new Progress<double>(p => installProgress.Value = p * 100.0);
            var backupDir = await UpdateChecker.ApplyUpdateAsync(info, progress, backup);
            installProgress.Value = 100;
            txtStatus.Foreground = Good;
            txtStatus.Text = backupDir == null
                ? $"{row.Tag} installed - restarting."
                : $"{row.Tag} installed - restarting. The replaced files were saved to {Path.GetFileName(backupDir)} (see RESTORE.txt there).";
            await System.Threading.Tasks.Task.Delay(900);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            installProgress.Visibility = Visibility.Collapsed;
            txtStatus.Foreground = Bad;
            txtStatus.Text = $"Install failed: {ex.Message} Nothing was changed.";
            listReleases.IsEnabled = true;
            chkBackup.IsEnabled = !_selectedRequiresBackup;
            btnCancel.IsEnabled = true;
            btnCancel.Content = "Close";
            btnInstall.IsEnabled = true;
            _busy = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Retry_Click(object sender, RoutedEventArgs e) => _ = LoadReleasesAsync();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // A half-finished download or swap must not be abandoned by closing the
        // window; the success path sets DialogResult first, so it still closes.
        if (_busy && DialogResult != true) e.Cancel = true;
        base.OnClosing(e);
    }
}

/// <summary>One row in the release list. Kept out of the dialog class so WPF's
/// binding sees a normal type.</summary>
internal sealed class ReleaseRow
{
    public ReleaseRow(UpdateInfo info, Version current)
    {
        Info = info;
        Tag = string.IsNullOrWhiteSpace(info.Tag) ? $"v{info.VersionText}" : info.Tag;
        IsCurrent = info.Version == current;
        Badge = IsCurrent ? "current" : info.Version > current ? "newer" : "";

        var parts = new List<string>();
        if (info.PublishedAt is { } published) parts.Add(published.ToLocalTime().ToString("d MMM yyyy"));
        parts.Add(IsCurrent ? "this build" : info.Version > current ? "newer than yours" : "older than yours");
        if (info.AssetSize > 0) parts.Add($"{info.AssetSize / (1024.0 * 1024.0):0.#} MB");
        MetaText = string.Join("  -  ", parts);
    }

    public UpdateInfo Info { get; }
    public string Tag { get; }
    public string MetaText { get; }
    public string Badge { get; }
    public bool IsCurrent { get; }
    public Brush BadgeBrush => IsCurrent
        ? new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1))
        : new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa));
    public string DateText => Info.PublishedAt?.ToLocalTime().ToString("d MMM yyyy") ?? "";
    public Visibility BadgeVisibility => Badge.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}
