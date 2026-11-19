using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace SifuMovesetEditor;

/// <summary>
/// Bug report form. The report can be sent (when the inbox endpoint is live), copied to
/// the clipboard for Discord, or saved as a zip for manual attachment - so the feature is
/// useful offline and before the server-side inbox exists.
/// </summary>
public partial class BugReportDialog : Window
{
    private readonly string? _contentPath;
    private readonly string? _outputPath;
    private readonly string? _context;
    private readonly string? _uncleanExit;
    private bool _busy;

    public BugReportDialog(
        string type = "bug",
        string? title = null,
        string? body = null,
        string? context = null,
        string? uncleanExit = null,
        string? contentPath = null,
        string? outputPath = null)
    {
        InitializeComponent();

        _contentPath = contentPath;
        _outputPath = outputPath;
        _context = context;
        _uncleanExit = uncleanExit;

        txtTitle.Text = title ?? "";
        txtBody.Text = body ?? "";

        if (string.Equals(type, "crash", StringComparison.OrdinalIgnoreCase)) rbCrash.IsChecked = true;
        else if (string.Equals(type, "feature", StringComparison.OrdinalIgnoreCase)) rbFeature.IsChecked = true;
        else rbBug.IsChecked = true;

        chkDiagnostics.IsChecked = true;
        UpdateLogInfo();

        if (!BugReport.SendEnabled)
        {
            btnSend.IsEnabled = false;
            btnSend.Content = "Send (not live yet)";
            btnSend.ToolTip = "The report inbox is not deployed yet - use Copy report or Save .zip.";
        }

        chkLog.Checked += (_, _) => OnTogglesChanged();
        chkLog.Unchecked += (_, _) => OnTogglesChanged();
        chkDiagnostics.Checked += (_, _) => OnTogglesChanged();
        chkDiagnostics.Unchecked += (_, _) => OnTogglesChanged();

        Loaded += (_, _) => txtTitle.Focus();
    }

    private string SelectedType =>
        rbCrash.IsChecked == true ? "crash"
        : rbFeature.IsChecked == true ? "feature"
        : "bug";

    private void UpdateLogInfo()
    {
        try { lblLogInfo.Text = "Captures " + BugReport.DescribeLogCapture(); }
        catch { lblLogInfo.Text = ""; }
    }

    private void OnTogglesChanged()
    {
        UpdateLogInfo();
        if (txtPreview.Visibility == Visibility.Visible) RefreshPreview();
    }

    private void SetResult(string text, bool error)
    {
        txtResult.Text = text ?? "";
        txtResult.Foreground = new SolidColorBrush(error
            ? Color.FromRgb(0xf3, 0x8b, 0xa8)
            : Color.FromRgb(0xa6, 0xe3, 0xa1));
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        btnSend.IsEnabled = !busy && BugReport.SendEnabled;
        btnCopy.IsEnabled = !busy;
        btnSaveZip.IsEnabled = !busy;
        btnPreview.IsEnabled = !busy;
    }

    private bool Validate()
    {
        if (txtTitle.Text.Trim().Length < 3)
        {
            SetResult("Add a short summary first.", true);
            txtTitle.Focus();
            return false;
        }
        if (txtBody.Text.Trim().Length < 10)
        {
            SetResult("Describe what happened (a sentence is enough).", true);
            txtBody.Focus();
            return false;
        }
        return true;
    }

    private static string AppVersion()
    {
        try
        {
            var asm = typeof(BugReportDialog).Assembly;
            var info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info!;
            return asm.GetName().Version?.ToString() ?? "0.0";
        }
        catch { return "0.0"; }
    }

    private static string WebView2Version()
    {
        try
        {
            var v = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return string.IsNullOrWhiteSpace(v) ? "(unavailable)" : v;
        }
        catch (Exception ex) { return $"(unavailable: {ex.GetType().Name})"; }
    }

    private BugReportPayload BuildPayload() => BugReport.Build(
        appVersion: AppVersion(),
        webview2Version: WebView2Version(),
        contentPath: _contentPath,
        outputPath: _outputPath,
        type: SelectedType,
        title: txtTitle.Text.Trim(),
        body: txtBody.Text.Trim(),
        steps: txtSteps.Text.Trim(),
        contact: txtContact.Text.Trim(),
        includeDiagnostics: chkDiagnostics.IsChecked == true,
        includeLog: chkLog.IsChecked == true,
        context: _context,
        uncleanExit: _uncleanExit);

    private static string BundleDirectory()
    {
        try
        {
            var dir = Path.Combine(ErrorLog.LogDirectory, "reports");
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch { return Path.GetTempPath(); }
    }

    private string? TrySaveBundle(BugReportPayload payload)
    {
        try
        {
            var path = Path.Combine(BundleDirectory(),
                $"SifuBugReport-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            return BugReport.WriteBundle(path, payload, out _) ? path : null;
        }
        catch { return null; }
    }

    private void RefreshPreview()
    {
        var text = BugReport.ToText(BuildPayload());
        const int cap = 30000;
        if (text.Length > cap)
            text = text.Substring(0, cap) + "\n… (preview truncated - nothing is cut when copied or sent)";
        txtPreview.Text = text;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (txtPreview.Visibility == Visibility.Visible)
        {
            txtPreview.Visibility = Visibility.Collapsed;
            btnPreview.Content = "Preview exactly what is sent";
            return;
        }
        RefreshPreview();
        txtPreview.Visibility = Visibility.Visible;
        btnPreview.Content = "Hide preview";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !Validate()) return;
        try
        {
            Clipboard.SetText(BugReport.ToText(BuildPayload()));
            SetResult("Report copied - paste it anywhere.", false);
        }
        catch (Exception ex)
        {
            SetResult("Couldn't copy: " + ex.Message, true);
        }
    }

    private void SaveZip_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !Validate()) return;
        var payload = BuildPayload();
        var dlg = new SaveFileDialog
        {
            Filter = "Zip archive (*.zip)|*.zip",
            FileName = $"SifuBugReport-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            InitialDirectory = BundleDirectory(),
            DefaultExt = ".zip",
        };
        if (dlg.ShowDialog(this) != true) return;

        if (BugReport.WriteBundle(dlg.FileName, payload, out var error))
            SetResult("Saved " + Path.GetFileName(dlg.FileName), false);
        else
            SetResult("Couldn't save: " + error, true);
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !Validate()) return;
        if (!BugReport.SendEnabled)
        {
            SetResult("Sending isn't available in this build - use Copy report or Save .zip.", true);
            return;
        }

        SetBusy(true);
        SetResult("Sending…", false);
        var payload = BuildPayload();

        var (ok, message) = await BugReport.SendAsync(payload);

        SetBusy(false);
        if (ok)
        {
            SetResult(message, false);
            btnSend.Content = "Sent";
            btnSend.IsEnabled = false;
            chkLog.IsEnabled = false;
            return;
        }

        // Nothing is lost: keep a bundle on disk and put the text on the clipboard.
        var bundle = TrySaveBundle(payload);
        try { Clipboard.SetText(BugReport.ToText(payload)); } catch { }
        SetResult($"{message} Report copied to the clipboard"
            + (bundle != null ? $" and saved to {Path.GetFileName(bundle)}." : "."), true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
