using System.Collections.Generic;
using System.Windows;

namespace SifuMovesetEditor.Custom;

/// <summary>
/// Modal "File(s) exist in CustomAssets" popup shown while loading a project whose
/// embedded custom files collide with different on-disk content. All-or-nothing:
/// ReplaceAll overwrites every conflict with the project's copies, Skip keeps the
/// on-disk versions, Cancel Load aborts the load (also the result of closing the
/// window via X or Esc).
/// </summary>
public partial class CustomAssetsConflictDialog : Window
{
    public CustomRestoreStrategy Choice { get; private set; } = CustomRestoreStrategy.Cancel;

    public CustomAssetsConflictDialog()
    {
        InitializeComponent();
    }

    public void SetConflicts(IReadOnlyList<string> files)
    {
        listFiles.ItemsSource = files;
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        Choice = CustomRestoreStrategy.ReplaceAll;
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Choice = CustomRestoreStrategy.Skip;
        Close();
    }

    private void CancelLoad_Click(object sender, RoutedEventArgs e)
    {
        Choice = CustomRestoreStrategy.Cancel;
        Close();
    }
}
