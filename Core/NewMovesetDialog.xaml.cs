using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SifuMovesetEditor;

public partial class NewMovesetDialog : Window
{
    public CustomMovesetInfo? Result { get; private set; }

    private readonly string _contentPath;
    private readonly List<MainWindow.ImportComboEntry> _donors = new();
    private List<string> _foundTargets = new();
    private int _scanSerial;

    public NewMovesetDialog(string contentPath)
    {
        InitializeComponent();
        _contentPath = contentPath;

        foreach (var entry in MainWindow.BuildImportComboCatalog().Values)
        {
            if (!entry.GamePath.StartsWith("Game/DB/AI/", StringComparison.OrdinalIgnoreCase)) continue;
            _donors.Add(entry);
        }
        _donors.Sort((a, b) => string.Compare(GetLabel(a), GetLabel(b), StringComparison.OrdinalIgnoreCase));
        foreach (var d in _donors)
            cmbDonor.Items.Add(new ComboBoxItem { Content = GetLabel(d), Tag = d });

        UpdatePreview();
    }

    private static string GetLabel(MainWindow.ImportComboEntry e)
    {
        var file = e.GamePath[(e.GamePath.LastIndexOf('/') + 1)..];
        return $"{file}  -  {e.UnitKey.Replace('|', ' ')}";
    }

    private void Name_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        var name = txtName.Text.Trim();
        txtPreview.Text = CustomMovesetInfo.IsValidName(name)
            ? $"New combo file: Game/DB/AI/Archetypes/Custom/{name}/{name}_Combo"
            : "New combo file: Game/DB/AI/Archetypes/Custom/<name>/<name>_Combo";
    }

    private async void Donor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var donor = cmbDonor.SelectedItem is ComboBoxItem item && item.Tag is MainWindow.ImportComboEntry d
            ? d : null;
        panelTargets.Children.Clear();
        _foundTargets = new List<string>();
        txtError.Text = "";
        if (donor == null)
        {
            txtScanStatus.Text = "Pick a donor to scan for referencing ArchetypeDBs.";
            return;
        }

        int serial = ++_scanSerial;
        string stem = Path.GetFileName(donor.GamePath);
        txtScanStatus.Text = "Scanning ArchetypeDB references...";
        var found = await Task.Run(() => ScanForReferences(stem));
        if (serial != _scanSerial) return;

        _foundTargets = found;
        panelTargets.Children.Clear();
        foreach (var rel in found)
        {
            panelTargets.Children.Add(new CheckBox
            {
                Content = rel,
                IsChecked = true,
                Foreground = new SolidColorBrush(Color.FromRgb(0xcd, 0xd6, 0xf4)),
                Margin = new Thickness(0, 1, 0, 1),
                FontSize = 11
            });
        }
        txtScanStatus.Text = found.Count == 0
            ? $"No ArchetypeDB under DB/AI/Archetypes references {stem}."
            : $"{found.Count} file(s) reference {stem} - checked ones are repointed at export.";
    }

    private List<string> ScanForReferences(string stem)
    {
        var result = new List<string>();
        var contentRoot = Path.Combine(_contentPath, "Content");
        var archRoot = Path.Combine(contentRoot, "DB", "AI", "Archetypes");
        if (!Directory.Exists(archRoot)) return result;

        var needle = Encoding.ASCII.GetBytes(stem);
        foreach (var f in Directory.GetFiles(archRoot, "*.uasset", SearchOption.AllDirectories))
        {
            if (f.Length > 255) continue;
            if (Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var bytes = File.ReadAllBytes(f);
                if (IndexOf(bytes, needle) < 0) continue;
            }
            catch { continue; }

            var rel = Path.GetRelativePath(contentRoot, f).Replace('\\', '/');
            if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                rel = rel[..^".uasset".Length];
            result.Add("Game/" + rel);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        txtError.Text = "";
        var name = txtName.Text.Trim();
        if (!CustomMovesetInfo.IsValidName(name))
        {
            txtError.Text = "Name must be letters, digits or underscores only.";
            return;
        }
        if (cmbDonor.SelectedItem is not ComboBoxItem { Tag: MainWindow.ImportComboEntry donor })
        {
            txtError.Text = "Pick a donor combo file.";
            return;
        }
        if (_foundTargets.Count == 0)
        {
            txtError.Text = "No ArchetypeDB references found for this donor - pick a different one.";
            return;
        }

        var targets = panelTargets.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => c.Content?.ToString() ?? "")
            .Where(s => s.Length > 0)
            .ToList();
        if (targets.Count == 0)
        {
            txtError.Text = "Check at least one repoint target.";
            return;
        }

        var unitParts = donor.UnitKey.Split('|');
        var info = new CustomMovesetInfo
        {
            DisplayName = name,
            DonorComboRel = donor.GamePath,
            DonorArch = unitParts[0],
            DonorVariantTag = unitParts.Length > 1 ? unitParts[1] : unitParts[0],
            DonorWeaponTag = donor.ActiveWeapon,
            TargetDbRels = targets,
            PatchWeaponPointers = chkWeapons.IsChecked == true
        };

        var diskUasset = Path.Combine(_contentPath, "Content", info.NewComboDiskRel + ".uasset");
        if (File.Exists(diskUasset))
        {
            txtError.Text = $"A file already exists at {info.NewComboRel}";
            return;
        }

        Result = info;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
