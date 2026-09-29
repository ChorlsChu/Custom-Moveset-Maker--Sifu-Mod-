using System.Collections.Generic;
using System.Windows;

namespace SifuMovesetEditor.Custom;

/// <summary>
/// Non-modal "New Custom File(s) detected" popup. The list rebinds while the window
/// is open (events append new rows, deletions drop them); "Add in Library" registers
/// the files now, "Cancel" leaves them on disk for the next launch.
/// </summary>
public partial class CustomFilesDetectedDialog : Window
{
    public bool AddToLibrary { get; private set; }

    public CustomFilesDetectedDialog()
    {
        InitializeComponent();
    }

    public void SetFiles(IReadOnlyList<string> files)
    {
        listFiles.ItemsSource = files;
    }

    private void AddInLibrary_Click(object sender, RoutedEventArgs e)
    {
        AddToLibrary = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        AddToLibrary = false;
        Close();
    }
}
