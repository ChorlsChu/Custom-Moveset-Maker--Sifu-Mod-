using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace SifuMovesetEditor.Update;

public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _info;

    public UpdateDialog(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        txtVersions.Text = $"{_info.Tag} is available — you have {UpdateChecker.CurrentVersion()}.";
        var notes = (_info.Notes ?? "").Trim();
        if (notes.Length == 0)
        {
            notesCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            txtNotes.Inlines.AddRange(MarkdownInlines.Render(notes));
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        btnUpdate.IsEnabled = false;
        btnSkip.IsEnabled = false;
        downloadProgress.Visibility = Visibility.Visible;
        downloadProgress.IsIndeterminate = true;
        txtStatus.Visibility = Visibility.Visible;
        txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xa6, 0xad, 0xc8));
        txtStatus.Text = "Downloading update…";
        try
        {
            var progress = new Progress<double>(p =>
            {
                downloadProgress.IsIndeterminate = false;
                downloadProgress.Value = p * 100.0;
            });
            await UpdateChecker.ApplyUpdateAsync(_info, progress);
            downloadProgress.IsIndeterminate = true;
            txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1));
            txtStatus.Text = "Installed — restarting…";
            await Task.Delay(600);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            downloadProgress.Visibility = Visibility.Collapsed;
            txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xf3, 0x8b, 0xa8));
            txtStatus.Text = $"Update failed: {ex.Message} Nothing was changed.";
            btnUpdate.IsEnabled = true;
            btnSkip.IsEnabled = true;
            btnSkip.Content = "Close";
        }
    }
}
