using System.Text.RegularExpressions;

namespace SifuMovesetEditor;

public sealed class CustomMovesetInfo
{
    public string DisplayName { get; set; } = "";
    public string DonorComboRel { get; set; } = "";
    public string DonorArch { get; set; } = "";
    public string DonorVariantTag { get; set; } = "";
    public string? DonorWeaponTag { get; set; }
    public List<string> TargetDbRels { get; set; } = new();
    public bool PatchWeaponPointers { get; set; } = true;

    public string NewComboRel => $"Game/DB/AI/Archetypes/Custom/{DisplayName}/{DisplayName}_Combo";

    public string NewComboDiskRel => $"DB/AI/Archetypes/Custom/{DisplayName}/{DisplayName}_Combo";

    public string CacheKey
    {
        get
        {
            var parts = new List<string> { DonorArch, DonorVariantTag };
            var weapon = MainWindow.NormalizeWeaponForCache(DonorArch, DonorWeaponTag);
            if (!string.IsNullOrEmpty(weapon)) parts.Add(weapon);
            return string.Join("|", parts);
        }
    }

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && Regex.IsMatch(name, "^[A-Za-z0-9_]+$");
}
