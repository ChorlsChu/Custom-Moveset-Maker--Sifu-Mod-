using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SifuMovesetEditor;

public static class EnemyAttackScanner
{
    private static readonly string[] EnemyTypes = new[]
    {
        "BigGuy", "Bodyguard", "Fajar", "Fengjie", "FireDisciple",
        "FlashKick", "Grunt", "Kuroki", "Sean", "Sifu", "Yang"
    };

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "_Arena", "_OLD", "_Tests", "_old", "GenericData", "VitalPointsDBs",
        "Variations", "_Advanced", "_Base", "_MainGame", "_Miniboss"
    };

    public static List<MoveInfo> ScanEnemyAttacks(string contentPath)
    {
        var moves = new List<MoveInfo>();
        var archetypesDir = Path.Combine(contentPath, "DB", "AI", "Archetypes");
        if (!Directory.Exists(archetypesDir)) return moves;

        foreach (string enemyType in EnemyTypes)
        {
            string enemyDir = Path.Combine(archetypesDir, enemyType);
            if (!Directory.Exists(enemyDir)) continue;

            string attacksDir = Path.Combine(enemyDir, "Attacks");
            if (!Directory.Exists(attacksDir)) continue;

            ScanAttackDBDir(attacksDir, enemyType, "", moves);
        }

        return moves.OrderBy(m => m.Character)
                     .ThenBy(m => m.WeaponType)
                     .ThenBy(m => m.Category)
                     .ThenBy(m => m.DisplayName)
                     .ToList();
    }

    private static void ScanAttackDBDir(string dir, string enemyType, string subCategory, List<MoveInfo> moves)
    {
        foreach (var file in Directory.GetFiles(dir, "*.uasset"))
        {
            string fileName = Path.GetFileNameWithoutExtension(file);
            string relDir = Path.GetRelativePath(
                Path.Combine(Path.GetDirectoryName(dir)!, ".."),
                Path.GetDirectoryName(file)!).Replace('\\', '/');

            string weaponType = "BareHands";
            if (relDir.Contains("Staff")) weaponType = "Staff";
            else if (relDir.Contains("Blade") || relDir.Contains("Machete") || relDir.Contains("Dagger")) weaponType = "Blades";
            else if (relDir.Contains("Bat") || relDir.Contains("Blunt")) weaponType = "Bats";
            else if (relDir.Contains("Meteor") || relDir.Contains("Hammer")) weaponType = "MeteorHammer";
            else if (relDir.Contains("TriStaff")) weaponType = "TriStaff";

            string category = DetermineCategory(relDir, fileName);

            string gamePath = "Game/" + Path.GetRelativePath(
                Directory.GetParent(Path.GetDirectoryName(Path.GetDirectoryName(dir)!)!)!.FullName,
                file).Replace('\\', '/').Replace(".uasset", "");

            moves.Add(new MoveInfo
            {
                DisplayName = fileName,
                FullPath = gamePath,
                Character = enemyType,
                WeaponType = weaponType,
                Category = category,
                EnemyType = enemyType
            });
        }

        foreach (var subDir in Directory.GetDirectories(dir))
        {
            string dirName = Path.GetFileName(subDir);
            if (SkipDirs.Contains(dirName)) continue;
            ScanAttackDBDir(subDir, enemyType, subCategory + "/" + dirName, moves);
        }
    }

    private static string DetermineCategory(string relDir, string fileName)
    {
        string lower = (relDir + "/" + fileName).ToLowerInvariant();

        if (lower.Contains("lightcombo")) return "LightCombo";
        if (lower.Contains("heavycombo")) return "HeavyCombo";
        if (lower.Contains("classiccombo") || lower.Contains("combo01") || lower.Contains("combo02")) return "Combo";
        if (lower.Contains("specialmove") || lower.Contains("special")) return "Special";
        if (lower.Contains("grab") || lower.Contains("takedown") || lower.Contains("throw")) return "Grab";
        if (lower.Contains("guard") || lower.Contains("parry") || lower.Contains("disarm")) return "Guard";
        if (lower.Contains("skill")) return "Skill";
        if (lower.Contains("rushing")) return "Rush";
        if (lower.Contains("knee") || lower.Contains("elbow")) return "Strike";
        if (lower.Contains("hook") || lower.Contains("uppercut") || lower.Contains("punch") || lower.Contains("jab")) return "Strike";
        if (lower.Contains("kick") || lower.Contains("spin") || lower.Contains("falcon")) return "Kick";
        if (lower.Contains("fist") || lower.Contains("claw")) return "Strike";
        if (lower.Contains("raging") || lower.Contains("bull")) return "Special";
        if (lower.Contains("eagle") || lower.Contains("tornado") || lower.Contains("waterfall")) return "Special";
        if (lower.Contains("circle") || lower.Contains("dance")) return "Special";
        if (lower.Contains("barehand")) return "BareHands";
        if (lower.Contains("staff")) return "Staff";
        if (lower.Contains("blade") || lower.Contains("machete") || lower.Contains("dagger")) return "Blades";

        return "Other";
    }
}
