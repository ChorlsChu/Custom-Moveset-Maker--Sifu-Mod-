using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SifuMovesetEditor.Import;

public partial class ImportDialog : Window
{
    private static Brush MakeBrush(string hex) =>
        (Brush)new BrushConverter().ConvertFrom(hex);

    public ImportDialog()
    {
        InitializeComponent();
    }

    public void SetProgress(double percent)
    {
        var parentWidth = ((FrameworkElement)progressFill.Parent).RenderSize.Width;
        var targetWidth = (percent / 100.0) * parentWidth;
        var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(300));
        progressFill.BeginAnimation(WidthProperty, anim);
        txtProgress.Text = $"{(int)percent}%";
    }

    public void UpdateStep(int step, string state)
    {
        var stepControl = step switch
        {
            1 => step1,
            2 => step2,
            3 => step3,
            4 => step4,
            5 => step5,
            6 => step6,
            _ => step1
        };

        var (symbol, color) = state switch
        {
            "active" => ("⏳", "#89b4fa"),
            "done" => ("✓", "#a6e3a1"),
            "error" => ("✗", "#f38ba8"),
            _ => ("○", "#6c7086")
        };

        var label = stepControl.Text[(stepControl.Text.IndexOf(' ') + 1)..];
        stepControl.Text = $"{symbol} {label}";
        stepControl.Foreground = MakeBrush(color);
    }

    public void SetCurrentAction(string text)
    {
        txtCurrentAction.Text = text;
        txtCurrentAction.Foreground = MakeBrush("#6c7086");
        txtErrorDetail.Visibility = Visibility.Collapsed;
        txtErrorDetail.Text = "";
    }

    public void ShowSuccess(string summary, string? details = null)
    {
        panelLoading.Visibility = Visibility.Collapsed;
        panelComplete.Visibility = Visibility.Visible;
        txtErrorDetail.Visibility = Visibility.Collapsed;

        txtResult.Text = summary;
        txtDetails.Text = details ?? "";
        txtStatus.Text = "Import completed successfully.";
        txtStatus.Foreground = MakeBrush("#a6e3a1");
    }

    public void ShowError(string message, string? detail = null)
    {
        SetProgress(0);
        panelComplete.Visibility = Visibility.Collapsed;
        txtTitle.Text = "Import failed";
        txtTitle.Foreground = MakeBrush("#f38ba8");
        txtCurrentAction.Foreground = MakeBrush("#f38ba8");
        txtCurrentAction.Text = message;
        if (!string.IsNullOrEmpty(detail))
        {
            txtErrorDetail.Text = detail;
            txtErrorDetail.Visibility = Visibility.Visible;
        }
        else
        {
            txtErrorDetail.Text = "";
            txtErrorDetail.Visibility = Visibility.Collapsed;
        }
        loadingButtons.Visibility = Visibility.Visible;
        txtStatus.Text = "Import failed.";
        txtStatus.Foreground = MakeBrush("#f38ba8");
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
