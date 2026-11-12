using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace SifuMovesetEditor;

/// <summary>
/// Name popup for cloning an attack card into the Custom Moves library: name field,
/// "Clone hit window" checkbox (with a "?" explainer) and a live destination preview.
/// Validation runs on every keystroke; the actual copy is done by the caller through
/// AttackCardCloner once Clone returns true.
/// </summary>
public partial class CloneAttackCardDialog : Window
{
    /// <summary>Validated card name (trimmed).</summary>
    public string NewName => txtName.Text.Trim();

    /// <summary>Whether the hit row gets a private copy too.</summary>
    public bool CloneHitWindow => chkCloneHit.IsChecked == true;

    private readonly string _cardRel;
    private readonly ISet<string> _existingGamePaths;
    // Extracted content root: lets ValidateName refuse names the game already uses at
    // the clone's path. Null when no content is detected - the library check still runs.
    private readonly string? _vanillaContentRoot;
    // Guards the Checked event fired by chkCloneHit's IsChecked="True" during
    // InitializeComponent (fields are not assigned yet at that point).
    private bool _ready;

    public CloneAttackCardDialog(string sourceLabel, string cardRel, IEnumerable<string> existingGamePaths,
        string? vanillaContentRoot = null)
    {
        InitializeComponent();
        _cardRel = cardRel;
        _existingGamePaths = new HashSet<string>(existingGamePaths, StringComparer.OrdinalIgnoreCase);
        _vanillaContentRoot = vanillaContentRoot;
        runSource.Text = sourceLabel;
        _ready = true;
        Loaded += (_, _) => txtName.Focus();
        Validate();
    }

    private void Name_TextChanged(object sender, TextChangedEventArgs e) => Validate();

    /// <summary>The private hit-row table is named after the card, so toggling this
    /// changes which vanilla paths the clone would collide with.</summary>
    private void CloneHit_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) Validate();
    }

    private void HitHelp_Click(object sender, RoutedEventArgs e)
        => txtHitHelp.Visibility = txtHitHelp.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void Validate()
    {
        string name = NewName;
        string destRel = name.Length == 0 ? "" : AttackCardCloner.DestRel(_cardRel, name);
        string destFile = destRel.Length == 0
            ? "CustomAssets\\" + _cardRel.Replace('/', '\\') + "\\<name>.uasset"
            : "CustomAssets\\" + destRel.Replace('/', '\\') + ".uasset";

        string? err = name.Length == 0 ? "Enter a name for the clone." 
            : AttackCardCloner.ValidateName(name, _cardRel, _existingGamePaths,
                _vanillaContentRoot, CloneHitWindow);
        txtError.Text = err ?? "";

        string preview = "Destination: " + destFile;
        if (CloneHitWindow)
        {
            string tableName = name.Length == 0 ? "<name>" : name;
            string dir = destRel.Contains('/')
                ? destRel[..destRel.LastIndexOf('/')] + "\\"
                : "";
            preview += "\nPrivate hit row: CustomAssets\\" + dir + tableName + "_HitBoxData.uasset";
        }
        else
        {
            preview += "\nHit row: shared with the vanilla attacks that use it"
                + " (you can still make it private later in the Hit Windows dialog)";
        }
        txtPreview.Text = preview;

        btnClone.IsEnabled = err == null;
    }

    private void Clone_Click(object sender, RoutedEventArgs e)
    {
        Validate();
        if (!btnClone.IsEnabled) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
