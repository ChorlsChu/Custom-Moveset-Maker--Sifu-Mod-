using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SifuMovesetEditor;

/// <summary>One attack card offered as a Create AttackDB template.</summary>
public sealed class AttackDbTemplateOption
{
    public string GamePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string DisplayNameClean { get; set; } = "";
    public string Character { get; set; } = "";
    public string WeaponType { get; set; } = "";
    public string Category { get; set; } = "";
    public int Score { get; set; }

    /// <summary>Why this card scored the way it did ("same character and weapon", ...).</summary>
    public string MatchReason { get; set; } = "";

    /// <summary>True for the top-ranked option - the one the dialog preselects.</summary>
    public bool IsRecommended { get; set; }

    /// <summary>Content-relative folder the new card will be written into.</summary>
    public string ParentFolderRel
    {
        get
        {
            var rel = AttackDbCard.GamePathToContentRel(GamePath).Trim('/');
            int slash = rel.LastIndexOf('/');
            return slash > 0 ? rel[..slash] : "DB";
        }
    }

    public string DisplayLabel
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(DisplayNameClean) ? DisplayName : DisplayNameClean;
            var bits = new[] { Character, WeaponType, Category }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var group = string.Join(" · ", bits);
            return string.IsNullOrEmpty(group) ? name : $"{name}  —  {group}";
        }
    }
}

/// <summary>
/// Ranks the library's attack cards as Create AttackDB templates. Exact anim→DB
/// mapping wins, then same character/weapon, then the same leaf folder (e.g. both
/// under _old), then same category and shared name tokens. The list stays complete:
/// ranking only decides the default pick, never hides cards from the user.
/// </summary>
public static class AttackDbTemplateRanker
{
    private static readonly HashSet<string> CommonNameTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "mainchar", "attack", "attacks", "barehands", "barehand", "weapon", "weapons"
    };

    public static List<AttackDbTemplateOption> Rank(MoveInfo animation, string? mappedDb,
        IEnumerable<MoveInfo>? libraryCards)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<AttackDbTemplateOption>();

        void Add(MoveInfo card)
        {
            if (string.IsNullOrWhiteSpace(card.FullPath)) return;
            if (!MoveInfo.IsAttackDbGamePath(card.FullPath)) return;
            if (!seen.Add(card.FullPath)) return;
            options.Add(new AttackDbTemplateOption
            {
                GamePath = card.FullPath,
                DisplayName = card.DisplayName,
                DisplayNameClean = card.DisplayNameClean,
                Character = card.Character,
                WeaponType = card.WeaponType,
                Category = card.Category
            });
        }

        foreach (var card in libraryCards ?? Array.Empty<MoveInfo>())
        {
            if (!string.IsNullOrWhiteSpace(card.DisplayName)) Add(card);
        }

        if (!string.IsNullOrEmpty(mappedDb) && !seen.Contains(mappedDb))
        {
            Add(new MoveInfo
            {
                DisplayName = Path.GetFileNameWithoutExtension(mappedDb),
                FullPath = mappedDb,
                Character = animation.Character,
                WeaponType = animation.WeaponType,
                Category = animation.Category
            });
        }

        foreach (var option in options)
        {
            option.Score = Score(option, animation, mappedDb);
            option.MatchReason = Reason(option, animation, mappedDb);
        }

        var ranked = options
            .OrderByDescending(o => o.Score)
            .ThenBy(o => o.DisplayNameClean.Length)
            .ThenBy(o => o.GamePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ranked.Count > 0) ranked[0].IsRecommended = true;
        return ranked;
    }

    private static int Score(AttackDbTemplateOption option, MoveInfo animation, string? mappedDb)
    {
        int score = 0;
        if (IsMapped(option, mappedDb)) score += 100_000;
        if (SameCharacter(option, animation)) score += 1_000;
        if (SameWeapon(option, animation)) score += 200;
        if (SameCategory(option, animation)) score += 40;
        if (SameLeafFolder(option, animation)) score += 80;
        score += CommonNameTokenCount(option.DisplayName, animation.DisplayName) * 25;
        return score;
    }

    private static string Reason(AttackDbTemplateOption option, MoveInfo animation, string? mappedDb)
    {
        if (IsMapped(option, mappedDb)) return "the existing card for this animation";

        bool sameCharacter = SameCharacter(option, animation);
        bool sameWeapon = SameWeapon(option, animation);
        bool sameFolder = SameLeafFolder(option, animation);

        if (sameCharacter && sameWeapon && sameFolder) return "same character, weapon and folder";
        if (sameCharacter && sameWeapon) return "same character and weapon";
        if (sameCharacter && sameFolder) return "same character and folder";
        if (sameCharacter) return "same character";
        if (sameWeapon && sameFolder) return "same weapon and folder";
        if (sameWeapon) return "same weapon type";
        if (CommonNameTokenCount(option.DisplayName, animation.DisplayName) > 0) return "similar name";
        return "closest available card";
    }

    private static bool IsMapped(AttackDbTemplateOption option, string? mappedDb) =>
        !string.IsNullOrEmpty(mappedDb)
        && string.Equals(option.GamePath, mappedDb, StringComparison.OrdinalIgnoreCase);

    private static bool SameCharacter(AttackDbTemplateOption option, MoveInfo animation) =>
        !string.IsNullOrEmpty(animation.Character)
        && string.Equals(option.Character, animation.Character, StringComparison.OrdinalIgnoreCase);

    private static bool SameWeapon(AttackDbTemplateOption option, MoveInfo animation) =>
        !string.IsNullOrEmpty(animation.WeaponType)
        && string.Equals(option.WeaponType, animation.WeaponType, StringComparison.OrdinalIgnoreCase);

    private static bool SameCategory(AttackDbTemplateOption option, MoveInfo animation) =>
        !string.IsNullOrEmpty(animation.Category)
        && string.Equals(option.Category, animation.Category, StringComparison.OrdinalIgnoreCase);

    private static bool SameLeafFolder(AttackDbTemplateOption option, MoveInfo animation) =>
        string.Equals(LeafFolder(option.GamePath), LeafFolder(animation.FullPath),
            StringComparison.OrdinalIgnoreCase);

    private static string LeafFolder(string path)
    {
        var rel = AttackDbCard.GamePathToContentRel(path).Trim('/');
        var parts = rel.Split('/');
        return parts.Length >= 2 ? parts[^2] : "";
    }

    internal static int CommonNameTokenCount(string a, string b)
    {
        static HashSet<string> Tokens(string text) => new(
            (text ?? "").Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length >= 3 && !CommonNameTokens.Contains(t)),
            StringComparer.OrdinalIgnoreCase);

        return Tokens(a).Intersect(Tokens(b), StringComparer.OrdinalIgnoreCase).Count();
    }
}
