using System.Windows;

namespace SifuMovesetEditor.Import;

public enum ImportChoice
{
    Merge,
    Fresh,
    Cancel
}

public partial class ImportChoiceDialog : Window
{
    public ImportChoice Choice { get; private set; } = ImportChoice.Cancel;

    public ImportChoiceDialog()
    {
        InitializeComponent();
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        Choice = ImportChoice.Merge;
        DialogResult = true;
        Close();
    }

    private void Fresh_Click(object sender, RoutedEventArgs e)
    {
        Choice = ImportChoice.Fresh;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = ImportChoice.Cancel;
        DialogResult = false;
        Close();
    }
}
