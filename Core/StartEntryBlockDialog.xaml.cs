using System.Windows;
using System.Windows.Media;

namespace SifuMovesetEditor;

/// <summary>Which action the user picked in <see cref="StartEntryBlockDialog"/>.</summary>
public enum StartEntryBlockChoice
{
    Cancel,
    OpenStartEntries,
    DeleteAnyway
}

/// <summary>
/// Shown when deleting a graph node that one or more start entries still target.
/// Cancel aborts; Open Start Entries aborts and opens the Start Entries dialog;
/// Delete Anyway cleans the offending entries with <see cref="NodeDeleteStartEntries"/>
/// (authored rows removed, retargeted vanilla rows reset to their original target when
/// it still exists) and lets the delete proceed.
/// </summary>
public partial class StartEntryBlockDialog : Window
{
    public StartEntryBlockChoice Choice { get; private set; } = StartEntryBlockChoice.Cancel;

    public StartEntryBlockDialog(string nodeName, IReadOnlyList<StartEntry> blockers)
    {
        InitializeComponent();
        txtHeader.Text = "Node is used by Start Entries";
        txtMessage.Text = $"'{nodeName}' is targeted by {blockers.Count} start "
            + $"entr{(blockers.Count == 1 ? "y" : "ies")}:\n\n"
            + "Deleting the node removes these targets. Rows you retargeted here snap back "
            + "to their original target when it still exists; if it is gone, that target is "
            + "dropped. Multi-key rows keep their other keys.";

        var lines = new List<string>();
        foreach (var en in blockers)
        {
            int live = en.Transitions.Count(t => !t.Removed);
            string kind = en.VanillaIndex >= 0
                ? "vanilla - retargeted rows reset to vanilla (or drop if the original is gone)"
                : "new entry - offending targets cleared";
            lines.Add($"• {en.Label} ({live} transition{(live == 1 ? "" : "s")}) - {kind}");
        }
        txtDetail.Text = string.Join("\n", lines);
        txtDetail.Foreground = new SolidColorBrush(Color.FromRgb(0xf9, 0xe2, 0xaf));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = StartEntryBlockChoice.Cancel;
        DialogResult = false;
        Close();
    }

    private void OpenEntries_Click(object sender, RoutedEventArgs e)
    {
        Choice = StartEntryBlockChoice.OpenStartEntries;
        DialogResult = true;
        Close();
    }

    private void DeleteAnyway_Click(object sender, RoutedEventArgs e)
    {
        Choice = StartEntryBlockChoice.DeleteAnyway;
        DialogResult = true;
        Close();
    }
}
