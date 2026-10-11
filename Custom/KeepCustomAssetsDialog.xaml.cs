using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SifuMovesetEditor.Custom;

/// <summary>
/// Close-time prompt for the persistent CustomAssets library, in the app's dialog style.
/// Yes keeps the new/modified files (caller updates the accepted manifest); No - via
/// button, Esc, or window X - swaps the dialog into a "Deleting files…" state with
/// real progress, removes the unaccepted files, then closes so the app can exit.
/// The file list streams in on chunked background work so a large CustomAssets
/// folder never stalls the popup.
/// </summary>
public partial class KeepCustomAssetsDialog : Window
{
    private readonly IReadOnlyList<string> _unaccepted;
    private readonly CancellationTokenSource _listCts = new();
    private bool _deleting;
    private bool _closingDone;

    /// <summary>Files removed by the No flow (0 when nothing was deleted).</summary>
    public int RemovedCount { get; private set; }

    public KeepCustomAssetsDialog(string question, IReadOnlyList<string> unaccepted)
    {
        InitializeComponent();
        txtQuestion.Text = question;
        _unaccepted = unaccepted;
        txtDetail.Text = "Loading file list…";
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var token = _listCts.Token;
            var names = await Task.Run(() => CustomAssets.DisplayNames(_unaccepted), token);
            if (token.IsCancellationRequested) return;

            const int chunk = 100;
            int shown = 0;
            while (shown < names.Count)
            {
                if (token.IsCancellationRequested) return;
                int end = Math.Min(shown + chunk, names.Count);
                for (int i = shown; i < end; i++) listFiles.Items.Add(names[i]);
                shown = end;
                if (shown < names.Count)
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }

            txtDetail.Text = names.Count == 1
                ? "1 new or modified file - deleted if you choose No."
                : $"{names.Count} new or modified files - deleted if you choose No.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorLog.Write("CUSTOM", ex);
            txtDetail.Text = "New or modified files in CustomAssets (file list unavailable - see error log).";
        }
    }

    private async void No_Click(object sender, RoutedEventArgs e)
    {
        await RunDeletingAsync();
    }

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        _listCts.Cancel();
        _closingDone = true;
        DialogResult = true;
    }

    private async Task RunDeletingAsync()
    {
        if (_deleting) return;
        _deleting = true;
        _listCts.Cancel();

        questionPanel.Visibility = Visibility.Collapsed;
        footerPanel.Visibility = Visibility.Collapsed;
        deletingPanel.Visibility = Visibility.Visible;
        txtProgress.Text = $"0 / {_unaccepted.Count} files";

        try
        {
            RemovedCount = await Task.Run(() => CustomAssets.DeleteEntries(_unaccepted,
                (done, total) => Dispatcher.InvokeAsync(() =>
                    txtProgress.Text = $"{done} / {total} files")));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("CUSTOM", ex);
        }

        _closingDone = true;
        if (DialogResult == null) DialogResult = false;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closingDone)
        {
            base.OnClosing(e);
            return;
        }

        if (_deleting)
        {
            // Esc/IsCancel race or Alt+F4 mid-delete: stay open until files are gone.
            e.Cancel = true;
            return;
        }

        // X while still asking behaves as No (parity with the old MessageBox fallback).
        e.Cancel = true;
        _ = RunDeletingAsync();
    }
}
