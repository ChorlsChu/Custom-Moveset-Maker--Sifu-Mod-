using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SifuMovesetEditor;

/// <summary>
/// Name popup for renaming a custom AttackDB: the current card name is prefilled and
/// selected, validation runs on every keystroke and the preview lists the destination
/// card file plus every private hit-row table that will follow it. The actual rename is
/// performed by CustomAttackDbRenamer once Rename returns true.
/// </summary>
public partial class RenameAttackCardDialog : Window
{
    /// <summary>Validated new card name (trimmed).</summary>
    public string NewName => txtName.Text.Trim();

    private readonly CustomAttackDbInspect _info;
    private readonly ISet<string> _existingGamePaths;
    // Extracted content root: lets ValidateName refuse names the game already uses at
    // the card's path. Null when no content is detected - the library checks still run.
    private readonly string? _vanillaContentRoot;

    public RenameAttackCardDialog(string currentLabel, string cardRel, CustomAttackDbInspect info,
        IEnumerable<string> existingGamePaths, string? vanillaContentRoot = null)
    {
        InitializeComponent();
        _info = info;
        _existingGamePaths = new HashSet<string>(existingGamePaths, StringComparer.OrdinalIgnoreCase);
        _vanillaContentRoot = vanillaContentRoot;
        runSource.Text = string.IsNullOrEmpty(currentLabel) ? info.CardName : currentLabel;
        txtName.Text = info.CardName;
        Loaded += (_, _) =>
        {
            txtName.Focus();
            txtName.SelectAll();
        };
        Validate();
    }

    private void Name_TextChanged(object sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        string name = NewName;
        string? err = name.Length == 0
            ? "Enter a new name for the card."
            : CustomAttackDbRenamer.ValidateName(name, _info, _existingGamePaths, _vanillaContentRoot);
        txtError.Text = err ?? "";
        btnRename.IsEnabled = err == null;

        string destRel = name.Length == 0
            ? ""
            : AttackCardCloner.DestRel(_info.CardRel, name);
        string destFile = destRel.Length == 0
            ? "CustomAssets\\" + _info.CardRel.Replace('/', '\\') + "\\<name>.uasset"
            : "CustomAssets\\" + destRel.Replace('/', '\\') + ".uasset";

        string preview = "Destination: " + destFile;
        if (_info.PrivateTables.Count == 0)
        {
            preview += "\nHit row: shared with the vanilla attacks that use it (unchanged)";
        }
        else
        {
            preview += "\nPrivate hit row:";
            foreach (var oldRel in _info.PrivateTables)
            {
                string newRel = name.Length == 0
                    ? oldRel + "\\<name>"
                    : CustomAttackDbRenamer.RenamedTableRel(oldRel, _info.CardName, name);
                preview += "\n  " + newRel.Replace('/', '\\') + ".uasset";
            }
        }
        txtPreview.Text = preview;
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        Validate();
        if (!btnRename.IsEnabled) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
