using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace SifuMovesetEditor;

public partial class ProjectFeedbackDialog : Window
{
    private readonly List<UnitReview> _units;
    private readonly HashSet<string> _expandedUnits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _expandedSections = new(StringComparer.OrdinalIgnoreCase);

    public ProjectFeedbackDialog(string mode, string title, string fileName, List<UnitReview> units, string? restoredKey)
    {
        InitializeComponent();

        _units = units ?? new List<UnitReview>();
        int changeCount = _units.Sum(u => u.Total);

        txtModeLabel.Text = mode == "Save" ? "— Save" : "— Load";
        txtTitle.Text = title;
        txtChangeCount.Text = $"{changeCount} change(s) across {_units.Count} unit(s):";
        txtSummary.Text = changeCount == 0
            ? $"{fileName} — no modified moves, retargets, or unit props in cached units."
            : $"{fileName} — {changeCount} change(s) in {_units.Count} unit(s)"
              + (restoredKey != null ? $"; restored active unit {ProjectChangeSummary.FormatUnitDisplayName(restoredKey)}." : ".");
        txtStatus.Text = mode == "Save"
            ? "Review what will be written to the .sifu-edit project."
            : "Review what this project loaded into the editor.";

        lstChanges.ItemsSource = ProjectChangeSummary.BuildList(_units, _expandedUnits, _expandedSections);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();

    private void UnitHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border border || border.Tag is not GroupHeader header) return;
        var key = string.IsNullOrEmpty(header.ParentName) ? header.Name : header.ParentName;
        if (!_expandedUnits.Remove(key))
            _expandedUnits.Add(key);
        lstChanges.ItemsSource = ProjectChangeSummary.BuildList(_units, _expandedUnits, _expandedSections);
    }

    private void SectionHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border border || border.Tag is not GroupHeader header) return;
        var unitKey = header.ParentName;
        if (string.IsNullOrEmpty(unitKey)) return;

        if (!_expandedSections.TryGetValue(unitKey, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _expandedSections[unitKey] = set;
        }

        if (!set.Remove(header.Name))
            set.Add(header.Name);
        lstChanges.ItemsSource = ProjectChangeSummary.BuildList(_units, _expandedUnits, _expandedSections);
    }
}
