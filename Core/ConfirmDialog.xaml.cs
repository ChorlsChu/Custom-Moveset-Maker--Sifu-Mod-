using System.Windows;

namespace SifuMovesetEditor;

/// <summary>
/// Styled OK/Cancel confirmation replacing the raw MessageBox prompts (New Project,
/// Reset All to Vanilla, Settings reset). Enter accepts, Esc/X cancels - same
/// semantics as MessageBoxButton.OKCancel.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string header, string message, string acceptLabel, string cancelLabel,
        string? detail = null)
    {
        InitializeComponent();
        txtHeader.Text = header;
        txtMessage.Text = message;
        btnAccept.Content = acceptLabel;
        btnCancel.Content = cancelLabel;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            txtDetail.Text = detail;
            detailBox.Visibility = Visibility.Visible;
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
