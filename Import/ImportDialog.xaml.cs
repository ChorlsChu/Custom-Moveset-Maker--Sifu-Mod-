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
            "active" => ("\u23F3", "#89b4fa"),
            "done" => ("\u2713", "#a6e3a1"),
            "error" => ("\u2717", "#f38ba8"),
            _ => ("\u25CB", "#6c7086")
        };

        var label = stepControl.Text[(stepControl.Text.IndexOf(' ') + 1)..];
        stepControl.Text = $"{symbol} {label}";
        stepControl.Foreground = MakeBrush(color);
    }

    public void SetCurrentAction(string text)
    {
        txtCurrentAction.Text = text;
    }

    public void ShowSuccess(int changedCount, string weaponName, string? extraInfo = null)
    {
        panelLoading.Visibility = Visibility.Collapsed;
        panelComplete.Visibility = Visibility.Visible;

        txtResult.Text = $"{changedCount} node(s) changed from vanilla";
        txtDetails.Text = extraInfo != null
            ? $"Weapon: {weaponName}\n{extraInfo}"
            : $"Weapon: {weaponName}";
        txtStatus.Text = "Import completed successfully.";
        txtStatus.Foreground = MakeBrush("#a6e3a1");
    }

    public void ShowError(string message)
    {
        SetProgress(0);
        txtTitle.Text = "Import failed";
        txtTitle.Foreground = MakeBrush("#f38ba8");
        txtCurrentAction.Text = message;
        txtCurrentAction.Foreground = MakeBrush("#f38ba8");
        loadingButtons.Visibility = Visibility.Visible;
        txtStatus.Text = "Import failed.";
        txtStatus.Foreground = MakeBrush("#f38ba8");
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
