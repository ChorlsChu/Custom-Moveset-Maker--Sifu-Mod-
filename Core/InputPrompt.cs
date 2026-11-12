using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SifuMovesetEditor;

/// <summary>
/// Minimal modal text prompt. Built in code (not a shared resource dictionary) so any
/// window can use it - StartEntriesDialog owns its own resources, and MainWindow's
/// styled dialogs are all purpose-built. Returns null on cancel, the trimmed text
/// otherwise (an empty string is a valid answer).
/// </summary>
internal static class InputPrompt
{
    internal static string? Show(Window? owner, string title, string message, string initial)
    {
        var win = new Window
        {
            Title = title,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            Owner = owner,
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xcd, 0xd6, 0xf4))
        };
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xa6, 0xad, 0xc8)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var box = new TextBox
        {
            Text = initial,
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x1b)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xcd, 0xd6, 0xf4)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5a)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2),
            FontSize = 12,
            Height = 26
        };
        root.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        string? result = null;
        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 74,
            IsCancel = true,
            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xcd, 0xd6, 0xf4)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(4, 0, 0, 0)
        };
        var ok = new Button
        {
            Content = "OK",
            MinWidth = 74,
            IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(4, 0, 0, 0)
        };
        ok.Click += (_, _) =>
        {
            result = box.Text.Trim();
            win.DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        win.Content = root;
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? result : null;
    }
}
